using System;
using System.Diagnostics;
using OpenImageDenoise;
using Runtime.EntityTypes;
using Unity;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Util;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;
using Debug = UnityEngine.Debug;

#if ENABLE_OPTIX
using OptiX;
#endif

namespace Runtime
{
	struct Diagnostics
	{
#if FULL_DIAGNOSTICS && BVH
		public float RayCount;
		public float BoundsHitCount;
		public float CandidateCount;
#pragma warning disable 649
		public float Padding;
#pragma warning restore 649
#else
		public float RayCount;
#endif
	}

#if PATH_DEBUGGING
	struct DebugPath
	{
		public float3 From, To;
	}
#endif

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	unsafe struct AddMeshRuntimeEntitiesJob : IJob
	{
		[ReadOnly] public Mesh.MeshDataArray MeshDataArray;
		[ReadOnly] [NativeDisableUnsafePtrRestriction] public Material* Material;

		[WriteOnly] public NativeArray<Triangle> Triangles;
		[WriteOnly] public NativeArray<Entity> Entities;
		[WriteOnly] public NativeArray<Entity> ImportanceSampledEntities;

		public NativeReference<int> TriangleIndex, EntityIndex, ImportanceSampledEntityIndex;

		public bool FaceNormals, Moving;
		public RigidTransform RigidTransform;
		public float3 DestinationOffset;
		public float2 TimeRange;

		public void Execute()
		{
			for (int meshIndex = 0; meshIndex < MeshDataArray.Length; meshIndex++)
			{
				Mesh.MeshData meshData = MeshDataArray[meshIndex];

				using var vertices = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
				meshData.GetVertices(vertices);

				NativeArray<Vector3> normals = default;
				if (!FaceNormals)
				{
					normals = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
					meshData.GetNormals(normals);
				}

				NativeArray<ushort> indices = meshData.GetIndexData<ushort>();
				for (int i = 0; i < indices.Length; i += 3)
				{
					int3 triangleIndices = int3(indices[i], indices[i + 1], indices[i + 2]);

					if (FaceNormals)
						Triangles[TriangleIndex.Value] = new Triangle(
							vertices[triangleIndices[0]], vertices[triangleIndices[1]], vertices[triangleIndices[2]]);
					else
						Triangles[TriangleIndex.Value] = new Triangle(
							vertices[triangleIndices[0]], vertices[triangleIndices[1]], vertices[triangleIndices[2]],
							normals[triangleIndices[0]], normals[triangleIndices[1]], normals[triangleIndices[2]]);

					var contentPointer = (Triangle*) Triangles.GetUnsafePtr() + TriangleIndex.Value++;

					Entity entity = Moving
						? new Entity(EntityType.Triangle, contentPointer, RigidTransform, Material, true, DestinationOffset, TimeRange)
						: new Entity(EntityType.Triangle, contentPointer, RigidTransform, Material);

					Entities[EntityIndex.Value++] = entity;

					if (Material->Type == MaterialType.DiffuseLight)
						ImportanceSampledEntities[ImportanceSampledEntityIndex.Value++] = entity;
				}

				if (!FaceNormals)
					normals.Dispose();
			}
		}
	}

#if BVH
	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct BuildRuntimeBvhJob : IJob
	{
		[ReadOnly] public NativeList<BvhNodeData> BvhNodeDataBuffer;
		[WriteOnly] public NativeArray<BvhNode> BvhNodeBuffer;
		public int NodeCount;

		int nodeIndex;

		// Runtime BVH is inserted BACKWARDS while traversing postorder, which means the first node will be the root

		unsafe BvhNode* WalkBvh(BvhNodeData* nodeData)
		{
			BvhNode* leftNode = null, rightNode = null;

			if (!nodeData->IsLeaf)
			{
				leftNode = WalkBvh(nodeData->Left);
				rightNode = WalkBvh(nodeData->Right);
			}

			BvhNodeBuffer[nodeIndex] = new BvhNode(nodeData->Bounds, nodeData->EntitiesStart, nodeData->EntityCount,
				leftNode, rightNode);
			return (BvhNode*) BvhNodeBuffer.GetUnsafePtr() + nodeIndex--;
		}

		public unsafe void Execute()
		{
			nodeIndex = NodeCount - 1;
			WalkBvh((BvhNodeData*) BvhNodeDataBuffer.GetUnsafeReadOnlyPtr());
		}
	}
#endif

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	unsafe struct AccumulateJob : IJobParallelFor
	{
#if BVH_ITERATIVE
		const int FirstBlockBvhNodeCount = 16;
		const int FirstBlockHitCandidateCount = 16;
#endif
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public float2 Size;
		[ReadOnly] public int SliceOffset;
		[ReadOnly] public int SliceDivider;
		[ReadOnly] public uint Seed;
		[ReadOnly] public View View;
		[ReadOnly] public Environment Environment;
		[ReadOnly] public uint SampleCount;
		[ReadOnly] public int TraceDepth;
		[ReadOnly] public bool SubPixelJitter;
		[ReadOnly] public ImportanceSampler ImportanceSampler;
#if BVH
		[ReadOnly] [NativeDisableUnsafePtrRestriction] public BvhNode* BvhRoot;
#else
		[ReadOnly] public NativeArray<Entity> Entities;
#endif
		[ReadOnly] public PerlinNoise PerlinNoise;
		[ReadOnly] public BlueNoise BlueNoise;
		[ReadOnly] public NoiseColor NoiseColor;
		[ReadOnly] public NativeArray<float4> InputColor;
		[ReadOnly] public NativeArray<float3> InputNormal;
		[ReadOnly] public NativeArray<float3> InputAlbedo;

		[WriteOnly] public NativeArray<float4> OutputColor;
		[WriteOnly] public NativeArray<float3> OutputNormal;
		[WriteOnly] public NativeArray<float3> OutputAlbedo;

		[WriteOnly] public NativeArray<Diagnostics> OutputDiagnostics;

#if PATH_DEBUGGING
		[ReadOnly] public int2 DebugCoordinates;
		[NativeDisableUnsafePtrRestriction] public DebugPath* DebugPaths;
#endif

		public void Execute(int index)
		{
			if (CancellationToken[0])
				return;

			// ReSharper disable once PossibleLossOfFraction
			int2 coordinates = int2(
				index % (int) Size.x, // column
				index / (int) Size.x // row
			);

			// early out
			if (coordinates.y % SliceDivider != SliceOffset)
				return;

			float4 lastColor = InputColor[index];
			float3 colorAcc = lastColor.xyz;
			float3 normalAcc = InputNormal[index];
			float3 albedoAcc = InputAlbedo[index];

			int sampleCount = (int) lastColor.w;

			PerPixelBlueNoise blueNoise = BlueNoise.GetPerPixelData((uint2) coordinates);
			// big primes stolen from Unity's random class
			Random whiteNoise = new Random((Seed * 0x8C4CA03Fu) ^ (uint) (index * 0x7383ED49u));

			var rng = new RandomSource(NoiseColor, whiteNoise, blueNoise);

#if PATH_DEBUGGING
			int2 intCoordinates = (int2) coordinates;
			bool doDebugPaths = all(intCoordinates == DebugCoordinates);
			if (doDebugPaths)
				for (int i = 0; i < TraceDepth; i++) DebugPaths[i] = default;
#endif

			float3* emissionStack = stackalloc float3[TraceDepth];
			float3* attenuationStack = stackalloc float3[TraceDepth];

			float3 fallbackAlbedo = default, fallbackNormal = default;
			Diagnostics diagnostics = default;

			for (int s = 0; s < SampleCount; s++)
			{
				float2 normalizedCoordinates = (coordinates + (SubPixelJitter ? blueNoise.NextFloat2() : 0.5f)) / Size;
				Ray eyeRay = View.GetRay(normalizedCoordinates, ref rng);

				if (Sample(eyeRay, ref rng, emissionStack, attenuationStack,
#if PATH_DEBUGGING
					doDebugPaths && s == 0, s,
#endif
					out float3 sampleColor, out float3 sampleNormal, out float3 sampleAlbedo, ref diagnostics))
				{
					colorAcc += sampleColor;
					normalAcc += sampleNormal;
					albedoAcc += sampleAlbedo;

					sampleCount++;
				}

				if (s == 0)
				{
					fallbackNormal = sampleNormal;
					fallbackAlbedo = sampleAlbedo;
				}
			}

			OutputColor[index] = float4(colorAcc, sampleCount);
			OutputNormal[index] = sampleCount == 0 ? fallbackNormal : normalAcc;
			OutputAlbedo[index] = sampleCount == 0 ? fallbackAlbedo : albedoAcc;

			OutputDiagnostics[index] = diagnostics;
		}

		[SkipLocalsInit]
		bool Sample(Ray eyeRay, ref RandomSource rng, float3* emissionStack, float3* attenuationStack,
#if PATH_DEBUGGING
			bool doDebugPaths, int sampleIndex,
#endif
			out float3 sampleColor, out float3 sampleNormal, out float3 sampleAlbedo, ref Diagnostics diagnostics)
		{
			float3* emissionCursor = emissionStack;
			float3* attenuationCursor = attenuationStack;

			int depth = 0;
			Entity* explicitSamplingTarget = null;
			Material* probablisticWalkEntryMaterial = null;
			bool firstNonSpecularHit = false;
			sampleNormal = sampleAlbedo = default;

			Ray ray = eyeRay;

#if BVH_ITERATIVE
			var np = stackalloc BvhNode*[FirstBlockBvhNodeCount];
			var npb = stackalloc PointerBlock<BvhNode>[1] { new PointerBlock<BvhNode>(np, FirstBlockBvhNodeCount) };
			var nodeTraversalBuffer = new PointerBlockChain<BvhNode>(npb);

            var ep = stackalloc Entity*[FirstBlockHitCandidateCount];
			var epb = stackalloc PointerBlock<Entity>[1] { new PointerBlock<Entity>(ep, FirstBlockHitCandidateCount) };
			var hitCandidateBuffer = new PointerBlockChain<Entity>(epb);
#endif

			for (; depth < TraceDepth; depth++)
			{
#if BVH_ITERATIVE
				HitRecord rec = default;
				float3 rayInvDirection = rcp(ray.Direction);

				// Convert NaN to INFINITY, since Burst thinks that divisions by 0 = NaN
				rayInvDirection = select(rayInvDirection, INFINITY, isnan(rayInvDirection));

				nodeTraversalBuffer.Clear();
				hitCandidateBuffer.Clear();

				nodeTraversalBuffer.Push(BvhRoot);

				while (nodeTraversalBuffer.Length > 0)
				{
					BvhNode* nodePtr = nodeTraversalBuffer.Pop();

					if (!nodePtr->Bounds.Hit(ray.Origin, rayInvDirection, 0, float.PositiveInfinity))
						continue;

#if FULL_DIAGNOSTICS
					diagnostics.BoundsHitCount++;
#endif

					if (nodePtr->IsLeaf)
					{
						int entityCount = nodePtr->EntityCount;
						int toAllocate = hitCandidateBuffer.GetRequiredAllocationSize(entityCount, out var parentBlock);
						if (toAllocate > 0)
						{
							var p = stackalloc Entity*[toAllocate];
							var pb = stackalloc PointerBlock<Entity>[1] { new PointerBlock<Entity>(p, toAllocate, parentBlock) };
							parentBlock->NextBlock = pb;
						}

						Entity* entityPtr = nodePtr->EntitiesStart;
						for (int i = 0; i < entityCount; i++, ++entityPtr)
							hitCandidateBuffer.Push(entityPtr);

#if FULL_DIAGNOSTICS
						diagnostics.CandidateCount += entityCount;
#endif
					}
					else
					{
						int toAllocate = nodeTraversalBuffer.GetRequiredAllocationSize(2, out var parentBlock);
						if (toAllocate > 0)
						{
							var p = stackalloc BvhNode*[toAllocate];
							var pb = stackalloc PointerBlock<BvhNode>[1] { new PointerBlock<BvhNode>(p, toAllocate, parentBlock) };
							parentBlock->NextBlock = pb;
						}

						nodeTraversalBuffer.Push(nodePtr->Left);
						nodeTraversalBuffer.Push(nodePtr->Right);
					}
				}

				// Iterative candidate tests
				bool hit = false;
				while (hitCandidateBuffer.Length > 0)
				{
					var hitCandidate = hitCandidateBuffer.Pop();
					bool thisHit = hitCandidate->Hit(ray, 0, float.PositiveInfinity, probablisticWalkEntryMaterial, ref rng, out HitRecord thisRec);
					if (thisHit && (!hit || thisRec.Distance < rec.Distance))
					{
						hit = true;
						rec = thisRec;
						rec.EntityPtr = hitCandidate;
					}
				}
#else
#if BVH
				bool hit = BvhRoot->Hit(
#else
				bool hit = Entities.Hit(
#endif
					ray, 0, float.PositiveInfinity, ref rng,
#if FULL_DIAGNOSTICS && BVH
					ref diagnostics,
#endif
					out HitRecord rec);
#endif

				diagnostics.RayCount++;

				if (hit)
				{
#if PATH_DEBUGGING
					if (doDebugPaths)
						DebugPaths[depth] = new DebugPath { From = ray.Origin, To = rec.Point };
#endif
					// We explicitly sampled an entity and could not hit it -- early out of this sample
					if (explicitSamplingTarget != null && explicitSamplingTarget != rec.EntityPtr)
						break;

					Material* material = rec.EntityPtr->Material;
					bool didScatter;
					float3 albedo;
					Ray scatteredRay;

					if (material->Type == MaterialType.ProbabilisticVolume && probablisticWalkEntryMaterial == null)
					{
						// Hijack the scatter direction for entry hits on probabilistic volumes
						scatteredRay = new Ray(rec.Point, ray.Direction, ray.Time);
						didScatter = true;
						probablisticWalkEntryMaterial = material;
						albedo = 1;
					}
					else
					{
						if (material->Type != MaterialType.ProbabilisticVolume)
							probablisticWalkEntryMaterial = null;

						if (probablisticWalkEntryMaterial != null)
							material = probablisticWalkEntryMaterial;

						didScatter = material->Scatter(ray, rec, ref rng, PerlinNoise, out albedo, out scatteredRay);
					}

					float3 emission = material->Emit(rec.Point, rec.Normal, PerlinNoise);
					*emissionCursor++ = emission;

					if (depth == 0)
						sampleNormal = rec.Normal;

					if (!firstNonSpecularHit)
					{
						if (material->IsPerfectSpecular)
						{
							// TODO: Fresnel mix for dielectric, first diffuse bounce for metallic
						}
						else
						{
							sampleAlbedo = material->Type == MaterialType.DiffuseLight ? emission : albedo;
							sampleNormal = rec.Normal;
							firstNonSpecularHit = true;
						}
					}

					if (!didScatter)
					{
						attenuationCursor++;
						break;
					}

					if (ImportanceSampler.Mode == ImportanceSamplingMode.None || material->IsPerfectSpecular)
					{
						*attenuationCursor++ = albedo;
						ray = scatteredRay;
					}
					else
					{
						float3 outgoingLightDirection = -ray.Direction;
						float scatterPdfValue = material->Pdf(scatteredRay.Direction, outgoingLightDirection, rec.Normal);

						ImportanceSampler.Sample(scatteredRay, outgoingLightDirection, rec, material, ref rng,
							out ray, out float pdfValue, out explicitSamplingTarget);

						// scatter ray is likely parallel to the surface, and division would cause a NaN
						if (pdfValue.AlmostEquals(0))
						{
							attenuationCursor++;
							break;
						}

						*attenuationCursor++ = albedo * scatterPdfValue / pdfValue;
					}

					ray = ray.OffsetTowards(dot(scatteredRay.Direction, rec.Normal) >= 0 ? rec.Normal : -rec.Normal);
				}
				else
				{
#if PATH_DEBUGGING
					if (doDebugPaths)
						DebugPaths[depth] = new DebugPath { From = ray.Origin, To = ray.Direction * 99999 };
#endif
					// sample the sky color
					float3 hitSkyColor = default;
					switch (Environment.SkyType)
					{
						case SkyType.GradientSky:
							hitSkyColor = lerp(Environment.SkyBottomColor, Environment.SkyTopColor, 0.5f * (ray.Direction.y + 1));
							break;

						case SkyType.CubeMap:
							hitSkyColor = Environment.SkyCubemap.Sample(ray.Direction);
							break;
					}

					*emissionCursor++ = hitSkyColor;
					attenuationCursor++;

					if (!firstNonSpecularHit)
					{
						sampleAlbedo = hitSkyColor;
						sampleNormal = -ray.Direction;
					}
					break;
				}
			}

			sampleColor = 0;

			// safety : if we don't hit an emissive surface within the trace depth limit, fail this sample
			if (depth == TraceDepth)
				return false;

			// attenuate colors from the tail of the hit stack to the head
			while (emissionCursor != emissionStack)
			{
				sampleColor *= *--attenuationCursor;
				sampleColor += *--emissionCursor;
			}

			return true;
		}
	}

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct CombineJob : IJobParallelFor
	{
		static readonly float3 NoSamplesColor = new float3(1, 0, 1);
		static readonly float3 NaNColor = new float3(0, 1, 1);

		public bool DebugMode;
		public bool LdrAlbedo;

		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float4> InputColor;
		[ReadOnly] public NativeArray<float3> InputNormal;
		[ReadOnly] public NativeArray<float3> InputAlbedo;
		[ReadOnly] public int2 Size;

		[WriteOnly] public NativeArray<float3> OutputColor;
		[WriteOnly] public NativeArray<float3> OutputNormal;
		[WriteOnly] public NativeArray<float3> OutputAlbedo;

		public void Execute(int index)
		{
			if (CancellationToken[0])
				return;

			float4 inputColor = InputColor[index];
			var realSampleCount = (int) inputColor.w;

			float3 finalColor;
			if (!DebugMode)
			{
				if (realSampleCount == 0) finalColor = 0;
				else if (any(isnan(inputColor))) finalColor = 0;
				else finalColor = inputColor.xyz / realSampleCount;
			}
			else
			{
				if (realSampleCount == 0)
				{
					int tentativeIndex = index;

					// look-around (for interlaced buffer)
					while (realSampleCount == 0 && (tentativeIndex -= Size.x) >= 0)
					{
						inputColor = InputColor[tentativeIndex];
						realSampleCount = (int) inputColor.w;
					}
				}

				if (realSampleCount == 0) finalColor = NoSamplesColor;
				else if (any(isnan(inputColor))) finalColor = NaNColor;
				else finalColor = inputColor.xyz / realSampleCount;
			}

			float3 finalAlbedo = InputAlbedo[index] / max(realSampleCount, 1);

			if (LdrAlbedo)
				finalAlbedo = min(finalAlbedo, 1);

			OutputColor[index] = finalColor;
			OutputNormal[index] = normalizesafe(InputNormal[index] / max(realSampleCount, 1));
			OutputAlbedo[index] = finalAlbedo;
		}
	}

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct ClearBufferJob<T> : IJob where T : unmanaged
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[WriteOnly] public NativeArray<T> Buffer;

		public void Execute()
		{
			if (CancellationToken[0])
				return;

			Buffer.ZeroMemory();
		}
	}

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct CopyFloat3BufferJob : IJob
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float3> Input;
		[WriteOnly] public NativeArray<float3> Output;

		public void Execute()
		{
			if (CancellationToken[0])
				return;

			NativeArray<float3>.Copy(Input, Output);
		}
	}

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct CopyFloat4BufferJob : IJob
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float4> Input;
		[WriteOnly] public NativeArray<float4> Output;

		public void Execute()
		{
			if (CancellationToken[0])
				return;

			NativeArray<float4>.Copy(Input, Output);
		}
	}

	// because the OIDN API uses strings, we can't use Burst here
	struct OpenImageDenoiseJob : IJob
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float3> InputColor, InputNormal, InputAlbedo;
		[ReadOnly] public ulong Width, Height;

		public NativeArray<float3> OutputColor;

		public OidnFilter DenoiseFilter;

		public unsafe void Execute()
		{
			if (CancellationToken[0])
				return;

			OidnFilter.SetSharedImage(DenoiseFilter, "color", new IntPtr(InputColor.GetUnsafeReadOnlyPtr()),
				OidnBuffer.Format.Float3, Width, Height, 0, 0, 0);
			OidnFilter.SetSharedImage(DenoiseFilter, "normal", new IntPtr(InputNormal.GetUnsafeReadOnlyPtr()),
				OidnBuffer.Format.Float3, Width, Height, 0, 0, 0);
			OidnFilter.SetSharedImage(DenoiseFilter, "albedo", new IntPtr(InputAlbedo.GetUnsafeReadOnlyPtr()),
				OidnBuffer.Format.Float3, Width, Height, 0, 0, 0);

			OidnFilter.SetSharedImage(DenoiseFilter, "output", new IntPtr(OutputColor.GetUnsafePtr()),
				OidnBuffer.Format.Float3, Width, Height, 0, 0, 0);

			OidnFilter.Commit(DenoiseFilter);
			OidnFilter.Execute(DenoiseFilter);
		}
	}

#if ENABLE_OPTIX
	// Disabled because it current won't compile using Burst (I swear it used to work)
	// [BurstCompile(FloatPrecision.Medium, FloatMode.Fast)]
	struct OptixDenoiseJob : IJob
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float3> InputColor, InputAlbedo;
		[ReadOnly] public uint2 BufferSize;
		[ReadOnly] public OptixDenoiserSizes DenoiserSizes;

		[WriteOnly] public NativeArray<float3> OutputColor;

		public OptixDenoiser Denoiser;
		public CudaStream CudaStream;

		public CudaBuffer InputColorBuffer,
			InputAlbedoBuffer,
			OutputColorBuffer,
			ScratchMemory,
			DenoiserState;

		[BurstDiscard]
		static void Check(CudaError cudaError)
		{
			if (cudaError != CudaError.Success)
				Debug.LogError($"CUDA Error : {cudaError}");
		}

		public unsafe void Execute()
		{
			if (CancellationToken[0])
				return;

			Check(CudaBuffer.Copy(new IntPtr(InputColor.GetUnsafeReadOnlyPtr()), InputColorBuffer.Handle,
				InputColor.Length * sizeof(float3), CudaMemcpyKind.HostToDevice));
			Check(CudaBuffer.Copy(new IntPtr(InputAlbedo.GetUnsafeReadOnlyPtr()), InputAlbedoBuffer.Handle,
				InputAlbedo.Length * sizeof(float3), CudaMemcpyKind.HostToDevice));

			var colorImage = new OptixImage2D
			{
				Data = InputColorBuffer,
				Format = OptixPixelFormat.Float3,
				Width = BufferSize.x, Height = BufferSize.y,
				RowStrideInBytes = (uint) (sizeof(float3) * BufferSize.x),
				PixelStrideInBytes = (uint) sizeof(float3)
			};
			var albedoImage = new OptixImage2D
			{
				Data = InputAlbedoBuffer,
				Format = OptixPixelFormat.Float3,
				Width = BufferSize.x, Height = BufferSize.y,
				RowStrideInBytes = (uint) (sizeof(float3) * BufferSize.x),
				PixelStrideInBytes = (uint) sizeof(float3)
			};

			OptixImage2D* optixImages = stackalloc OptixImage2D[2];
			optixImages[0] = colorImage;
			optixImages[1] = albedoImage;

			OptixDenoiserParams denoiserParams = default;

			var outputImage = new OptixImage2D
			{
				Data = OutputColorBuffer,
				Format = OptixPixelFormat.Float3,
				Width = BufferSize.x, Height = BufferSize.y,
				RowStrideInBytes = (uint) (sizeof(float3) * BufferSize.x),
				PixelStrideInBytes = (uint) sizeof(float3)
			};

			OptixDenoiser.Invoke(Denoiser, CudaStream, &denoiserParams, DenoiserState, DenoiserSizes.StateSizeInBytes,
				optixImages, 2, 0, 0,
				&outputImage, ScratchMemory, DenoiserSizes.RecommendedScratchSizeInBytes);

			Check(CudaBuffer.Copy(OutputColorBuffer.Handle, new IntPtr(OutputColor.GetUnsafePtr()),
				OutputColor.Length * sizeof(float3), CudaMemcpyKind.DeviceToHost));
		}
	}
#endif

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct FinalizeTexturesJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<bool> CancellationToken;

		[ReadOnly] public NativeArray<float3> InputColor;
		[ReadOnly] public NativeArray<float3> InputNormal;
		[ReadOnly] public NativeArray<float3> InputAlbedo;

		[WriteOnly] public NativeArray<RGBA32> OutputColor;
		[WriteOnly] public NativeArray<RGBA32> OutputNormal;
		[WriteOnly] public NativeArray<RGBA32> OutputAlbedo;

		public void Execute(int index)
		{
			if (CancellationToken[0])
				return;

			// TODO: tone-mapping
			float3 outputColor = saturate(InputColor[index].LinearToGamma()) * 255;
			OutputColor[index] = new RGBA32
			{
				r = (byte) outputColor.x,
				g = (byte) outputColor.y,
				b = (byte) outputColor.z,
				a = 255
			};

			float3 outputNormal = saturate((InputNormal[index] * 0.5f + 0.5f).LinearToGamma()) * 255;
			OutputNormal[index] = new RGBA32
			{
				r = (byte) outputNormal.x,
				g = (byte) outputNormal.y,
				b = (byte) outputNormal.z,
				a = 255
			};

			float3 outputAlbedo = saturate(InputAlbedo[index].LinearToGamma()) * 255;
			OutputAlbedo[index] = new RGBA32
			{
				r = (byte) outputAlbedo.x,
				g = (byte) outputAlbedo.y,
				b = (byte) outputAlbedo.z,
				a = 255
			};
		}
	}

	[BurstCompile(FloatPrecision.Medium, FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
	struct ReduceRayCountJob : IJob
	{
		[ReadOnly] public NativeArray<Diagnostics> Diagnostics;
		[WriteOnly] public NativeReference<int> TotalRayCount;

		public void Execute()
		{
			float totalRayCount = 0;

			for (int i = 0; i < Diagnostics.Length; i++)
				totalRayCount += Diagnostics[i].RayCount;

			TotalRayCount.Value = (int) totalRayCount;
		}
	}

	struct RecordTimeJob : IJob
	{
		[ReadOnly] public int Index;
		[WriteOnly] public NativeArray<long> Buffer;

		public void Execute()
		{
			Buffer[Index] = Stopwatch.GetTimestamp();
		}
	}
}