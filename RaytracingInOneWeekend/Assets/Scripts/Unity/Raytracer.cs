﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOT;
using JetBrains.Annotations;
using OpenImageDenoise;
using Runtime;
using Runtime.EntityTypes;
using Runtime.Jobs;
using Sirenix.OdinInspector;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Util;
using static Unity.Mathematics.math;
using Cubemap = Runtime.Cubemap;
using Debug = UnityEngine.Debug;
using Environment = Runtime.Environment;
using float3 = Unity.Mathematics.float3;
using Material = Runtime.Material;
using Ray = Runtime.Ray;
using RigidTransform = Unity.Mathematics.RigidTransform;
using SkyType = Runtime.SkyType;
using Texture = Runtime.Texture;

#if ENABLE_OPTIX
using OptiX;
#endif

#if ODIN_INSPECTOR
using OdinReadOnly = Sirenix.OdinInspector.ReadOnlyAttribute;
#else
using OdinMock;
using OdinReadOnly = OdinMock.ReadOnlyAttribute;
#endif

namespace Unity
{
	public enum DenoiseMode
	{
		None,
		OpenImageDenoise,
#if ENABLE_OPTIX
		NvidiaOptix
#endif
	}

	struct Diagnostics
	{
#if FULL_DIAGNOSTICS
		public float RayCount;
		public float BoundsHitCount;
		public float CandidateCount;
		public float SampleCountWeight;
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

	partial class Raytracer : MonoBehaviour
	{
		static readonly ProfilerMarker RebuildEntityBuffersMarker = new("Rebuild Entity Buffers");
		static readonly ProfilerMarker RebuildBvhMarker = new("Rebuild BVH");

		[Title("References")]
		[SerializeField] BlueNoiseData blueNoise;

		[Title("Settings")]
		[SerializeField] [Range(1, 100)] int interlacing = 2;

		[SerializeField] [EnableIf(nameof(BvhEnabled))] [Range(2, 32)] [UsedImplicitly] int maxBvhDepth = 16;
		[SerializeField] [Range(0.01f, 2)] float resolutionScaling = 0.5f;
		[SerializeField] [Range(1, 10000)] uint samplesPerPixel = 1000;
		[SerializeField] int maxDurationSeconds = -1;
		[SerializeField] [MinMaxSlider(1, 1000, true)] Vector2 samplesPerBatchRange = new Vector2(1, 50);
		[SerializeField] [Range(1, 500)] int traceDepth = 35;
		[SerializeField] DenoiseMode denoiseMode = DenoiseMode.None;
		[SerializeField] NoiseColor noiseColor = NoiseColor.White;
		[SerializeField] bool subPixelJitter = true;
		[SerializeField] bool previewAfterBatch = true;
		[SerializeField] bool stopWhenCompleted = true;
		[SerializeField] bool saveWhenCompleted = true;
#if PATH_DEBUGGING
		[SerializeField] bool fadeDebugPaths = false;
		[SerializeField] [Range(0, 25)] float debugPathDuration = 1;
#endif

		[Title("Debug")]
		[SerializeField] [DisableInPlayMode] Shader viewRangeShader = null;
		[SerializeField] [DisableInPlayMode] Shader blitShader = null;

		[OdinReadOnly] public float TotalSamplesPerPixel;
		[UsedImplicitly] [OdinReadOnly] public int2 SamplesPerPixelRange;

		[UsedImplicitly] [OdinReadOnly] public float MillionRaysPerSecond,
			AvgMRaysPerSecond,
			LastBatchRayCountPerPixel;

		[UsedImplicitly] [OdinReadOnly] public string LastBatchDuration, LastTraceDuration;

		[UsedImplicitly] [OdinReadOnly] public float2 BufferValueRange;

		[UsedImplicitly] [ShowInInspector] public float AccumulateJobs => scheduledSampleJobs.Count;
		[UsedImplicitly] [ShowInInspector] public float CombineJobs => scheduledCombineJobs.Count;
		[UsedImplicitly] [ShowInInspector] public float DenoiseJobs => scheduledDenoiseJobs.Count;
		[UsedImplicitly] [ShowInInspector] public float FinalizeJobs => scheduledFinalizeJobs.Count;

		Camera TargetCamera => Camera.main;

		Pool<NativeArray<float4>> float4Buffers;
		Pool<NativeArray<float3>> float3Buffers;
		Pool<NativeArray<float>> floatBuffers;
		Pool<NativeArray<long>> longBuffers;

		Pool<NativeReference<int>> intReferences;
		Pool<NativeReference<bool>> boolReferences;
		Pool<NativeReference<int2>> int2References;
		Pool<NativeReference<float2>> float2References;

		int interlacingOffsetIndex;
		int[] interlacingOffsets;
		uint frameSeed = 1;
		float2 sampleCountWeightExtrema;

		NativeArray<float4> colorAccumulationBuffer;
		NativeArray<float3> normalAccumulationBuffer, albedoAccumulationBuffer;
		NativeArray<float> sampleCountWeightAccumulationBuffer;

		Texture2D frontBufferTexture, normalsTexture, albedoTexture, diagnosticsTexture;
		UnityEngine.Material viewRangeMaterial, blitMaterial;

		NativeArray<RGBA32> frontBuffer, normalsBuffer, albedoBuffer;
		NativeArray<Diagnostics> diagnosticsBuffer;

		// NativeArray<Sphere> sphereBuffer;
		// NativeArray<Rect> rectBuffer;
		// NativeArray<Box> boxBuffer;

		NativeList<Triangle> triangleBuffer;
		NativeList<Entity> entityBuffer;
		NativeList<Material> materialBuffer;
#if PATH_DEBUGGING
		NativeArray<DebugPath> debugPaths;
#endif

		NativeList<BvhNodeData> bvhNodeDataBuffer;
		NativeArray<BvhNode> bvhNodeBuffer;
		NativeList<Entity> bvhEntities;

		BvhNodeData? BvhRootData => bvhNodeDataBuffer.IsCreated ? bvhNodeDataBuffer[0] : null;
		unsafe BvhNode* BvhRoot => bvhNodeBuffer.IsCreated ? (BvhNode*) bvhNodeBuffer.GetUnsafePtr() : null;

		[UsedImplicitly] bool BvhEnabled => true;

		readonly PerlinNoiseData perlinNoise = new();

		OidnDevice oidnDevice;
		OidnFilter oidnFilter;

#if ENABLE_OPTIX
		OptixDeviceContext optixDeviceContext;
		OptixDenoiser optixDenoiser;
		OptixDenoiserSizes optixDenoiserSizes;
		CudaStream cudaStream;
		CudaBuffer optixScratchMemory, optixDenoiserState, optixColorBuffer, optixAlbedoBuffer, optixOutputBuffer;
#endif

		struct ScheduledJobData<T>
		{
			public JobHandle Handle;
			public Action OnComplete;
			public T OutputData;
			public NativeReference<bool> CancellationToken;

			public unsafe void Cancel()
			{
				*(bool*) CancellationToken.GetUnsafePtrWithoutChecks() = true;
			}

			public void Complete()
			{
				Handle.Complete();
				OnComplete?.Invoke();
				OnComplete = null;
			}
		}

		struct SampleBatchOutputData
		{
			public NativeArray<float4> Color;
			public NativeArray<float3> Normal, Albedo;
			public NativeArray<float> SampleCountWeight;
			public NativeReference<int> ReducedRayCount;
			public NativeReference<int> TotalSamples;
			public NativeReference<float2> SampleCountWeightExtrema;
			public NativeReference<int2> SampleCountExtrema;
			public NativeArray<long> Timing;
		}

		struct PassOutputData
		{
			public NativeArray<float3> Color, Normal, Albedo;
		}

		readonly Queue<ScheduledJobData<SampleBatchOutputData>> scheduledSampleJobs = new();
		readonly Queue<ScheduledJobData<PassOutputData>> scheduledCombineJobs = new();
		readonly Queue<ScheduledJobData<PassOutputData>> scheduledDenoiseJobs = new();
		readonly Queue<ScheduledJobData<PassOutputData>> scheduledFinalizeJobs = new();

		bool worldNeedsRebuild, initialized, traceAborted, ignoreBatchTimings;
		float focusDistance = 1;
		int lastTraceDepth;
		uint lastSamplesPerPixel;
		bool queuedSample;

		readonly Stopwatch traceTimer = new();
		readonly List<float> mraysPerSecResults = new();

		float2 bufferSize;

		int BufferLength => (int) (bufferSize.x * bufferSize.y);

		bool TraceActive => scheduledSampleJobs.Count > 0 || scheduledCombineJobs.Count > 0 || scheduledDenoiseJobs.Count > 0 || scheduledFinalizeJobs.Count > 0;

		enum BufferView
		{
			Front,
			RayCount,
#if FULL_DIAGNOSTICS && BVH
			BvhHitCount,
			CandidateCount,
			SampleCountWeight,
#endif
			Normals,
			Albedo
		}

#if !UNITY_EDITOR
		const BufferView bufferView = BufferView.Front;
#endif
		int channelPropertyId, minimumRangePropertyId;

		void Awake()
		{
			const HideFlags flags = HideFlags.HideAndDontSave;
			frontBufferTexture = new Texture2D(0, 0, TextureFormat.RGBA32, false) { hideFlags = flags };
			normalsTexture = new Texture2D(0, 0, TextureFormat.RGBA32, false) { hideFlags = flags };
			albedoTexture = new Texture2D(0, 0, TextureFormat.RGBA32, false) { hideFlags = flags };
#if FULL_DIAGNOSTICS && BVH
			diagnosticsTexture = new Texture2D(0, 0, TextureFormat.RGBAFloat, false) { hideFlags = flags };
#else
			diagnosticsTexture = new Texture2D(0, 0, TextureFormat.RFloat, false) { hideFlags = flags };
#endif
			viewRangeMaterial = new UnityEngine.Material(viewRangeShader);
			blitMaterial = new UnityEngine.Material(blitShader);

			channelPropertyId = Shader.PropertyToID("_Channel");
			minimumRangePropertyId = Shader.PropertyToID("_Minimum_Range");

			unsafe
			{
				string GetArrayName<T>(NativeArray<T> e) where T : unmanaged => $"0x{(int)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(e):x8}";
				string GetReferenceName<T>(NativeReference<T> e) where T : unmanaged => $"0x{(int)e.GetUnsafePtrWithoutChecks():x8}";

				float3Buffers = new Pool<NativeArray<float3>>(() => new NativeArray<float3>(BufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetArrayName, equalityComparer: new NativeArrayEqualityComparer<float3>()) { Capacity = 32 };

				float4Buffers = new Pool<NativeArray<float4>>(() => new NativeArray<float4>(BufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetArrayName, equalityComparer: new NativeArrayEqualityComparer<float4>()) { Capacity = 8 };

				floatBuffers = new Pool<NativeArray<float>>(() => new NativeArray<float>(BufferLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetArrayName, equalityComparer: new NativeArrayEqualityComparer<float>()) { Capacity = 8 };

				longBuffers = new Pool<NativeArray<long>>(() => new NativeArray<long>(2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetArrayName, equalityComparer: new NativeArrayEqualityComparer<long>()) { Capacity = 4 };

				intReferences = new Pool<NativeReference<int>>(() => new NativeReference<int>(AllocatorManager.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetReferenceName, equalityComparer: new NativeReferenceEqualityComparer<int>()) { Capacity = 4 };

				boolReferences = new Pool<NativeReference<bool>>(() => new NativeReference<bool>(AllocatorManager.Persistent),
					itemNameMethod: e => $"0x{(int)e.GetUnsafePtrWithoutChecks():x8}",
					cleanupMethod: reference => { *(bool*)reference.GetUnsafePtrWithoutChecks() = false; },
					equalityComparer: new NativeReferenceEqualityComparer<bool>()) { Capacity = 8 };

				int2References = new Pool<NativeReference<int2>>(() => new NativeReference<int2>(AllocatorManager.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetReferenceName, equalityComparer: new NativeReferenceEqualityComparer<int2>()) { Capacity = 4 };

				float2References = new Pool<NativeReference<float2>>(() => new NativeReference<float2>(AllocatorManager.Persistent, NativeArrayOptions.UninitializedMemory),
					itemNameMethod: GetReferenceName, equalityComparer: new NativeReferenceEqualityComparer<float2>()) { Capacity = 4 };

			}

			ignoreBatchTimings = true;

			blueNoise.Linearize();
		}

		void Start()
		{
			RebuildWorld();
			InitDenoisers();
			EnsureBuffersBuilt();
			CleanCamera();
			ScheduleSample(true);
			TargetCamera.GetComponent<HDAdditionalCameraData>().customRender += OnCustomRender;
		}

		void InitDenoisers()
		{
			// Open Image Denoise
			try
			{
				oidnDevice = OidnDevice.New(OidnDevice.Type.Default);
				OidnDevice.SetErrorFunction(oidnDevice, OnOidnError, IntPtr.Zero);
				OidnDevice.Commit(oidnDevice);

				oidnFilter = OidnFilter.New(oidnDevice, "RT");
				OidnFilter.Set(oidnFilter, "hdr", true);
			}
			catch (Exception ex)
			{
				Debug.LogError($"Could not initialize Intel OIDN.\n{ex}");
			}

#if ENABLE_OPTIX
			// nVidia OptiX
			CudaError cudaError;
			if ((cudaError = OptixApi.InitializeCuda()) != CudaError.Success)
			{
				Debug.LogError($"CUDA initialization failed : {cudaError}");
				return;
			}

			OptixResult result;
			if ((result = OptixApi.Initialize()) != OptixResult.Success)
			{
				Debug.LogError($"OptiX initialization failed : {result}");
				return;
			}

			var options = new OptixDeviceContextOptions
			{
				LogCallbackFunction = OnOptixError,
				LogCallbackLevel = OptixLogLevel.Warning
			};

			if ((result = OptixDeviceContext.Create(options, ref optixDeviceContext)) != OptixResult.Success)
			{
				Debug.LogError($"Optix device creation failed : {result}");
				return;
			}

			var denoiseOptions = new OptixDenoiserOptions
			{
				InputKind = OptixDenoiserInputKind.RgbAlbedo,
				PixelFormat = OptixPixelFormat.Float3
			};

			unsafe
			{
				OptixDenoiser.Create(optixDeviceContext, &denoiseOptions, ref optixDenoiser);
			}

			OptixDenoiser.SetModel(optixDenoiser, OptixModelKind.Hdr, IntPtr.Zero, 0);

			if ((cudaError = CudaStream.Create(ref cudaStream)) != CudaError.Success)
				Debug.LogError($"CUDA Stream creation failed : {cudaError}");
#endif
		}

#if ENABLE_OPTIX
		[MonoPInvokeCallback(typeof(OptixLogCallback))]
		static void OnOptixError(OptixLogLevel level, string tag, string message, IntPtr cbdata)
		{
			switch (level)
			{
				case OptixLogLevel.Fatal: Debug.LogError($"nVidia OptiX Fatal Error : {tag} - {message}"); break;
				case OptixLogLevel.Error: Debug.LogError($"nVidia OptiX Error : {tag} - {message}"); break;
				case OptixLogLevel.Warning: Debug.LogWarning($"nVidia OptiX Warning : {tag} - {message}"); break;
				case OptixLogLevel.Print: Debug.Log($"nVidia OptiX Trace : {tag} - {message}"); break;
			}
		}
#endif

		[MonoPInvokeCallback(typeof(OidnErrorFunction))]
		static void OnOidnError(IntPtr userPtr, OidnError code, string message)
		{
			if (string.IsNullOrWhiteSpace(message)) Debug.LogError(code);
			else Debug.LogError($"{code} : {message}");
		}

		void OnDestroy()
		{
			foreach (var jobData in scheduledSampleJobs) jobData.Cancel();
			foreach (var jobData in scheduledCombineJobs) jobData.Cancel();
			foreach (var jobData in scheduledDenoiseJobs) jobData.Cancel();
			foreach (var jobData in scheduledFinalizeJobs) jobData.Cancel();

			foreach (var jobData in scheduledSampleJobs) jobData.Handle.Complete();
			foreach (var jobData in scheduledCombineJobs) jobData.Handle.Complete();
			foreach (var jobData in scheduledDenoiseJobs) jobData.Handle.Complete();
			foreach (var jobData in scheduledFinalizeJobs) jobData.Handle.Complete();

			entityBuffer.SafeDispose();
			// sphereBuffer.SafeDispose();
			// rectBuffer.SafeDispose();
			// boxBuffer.SafeDispose();
			triangleBuffer.SafeDispose();
			materialBuffer.SafeDispose();

			float3Buffers?.Dispose();
			float4Buffers?.Dispose();
			longBuffers?.Dispose();
			floatBuffers?.Dispose();

			intReferences?.Dispose();
			boolReferences?.Dispose();
			int2References?.Dispose();
			float2References?.Dispose();

			bvhNodeDataBuffer.SafeDispose();
			bvhEntities.SafeDispose();
			bvhNodeBuffer.SafeDispose();

			perlinNoise.Dispose();
#if PATH_DEBUGGING
			debugPaths.SafeDispose();
#endif

			if (oidnFilter.Handle != IntPtr.Zero)
			{
				OidnFilter.Release(oidnFilter);
				oidnFilter = default;
			}

			if (oidnDevice.Handle != IntPtr.Zero)
			{
				OidnDevice.Release(oidnDevice);
				oidnDevice = default;
			}

#if ENABLE_OPTIX
			OptixDenoiser.Destroy(optixDenoiser);
			OptixDeviceContext.Destroy(optixDeviceContext);

			void Check(CudaError cudaError)
			{
				if (cudaError != CudaError.Success)
					Debug.LogError($"CUDA Error : {cudaError}");
			}

			Check(CudaStream.Destroy(cudaStream));

			Check(CudaBuffer.Deallocate(optixDenoiserState));
			Check(CudaBuffer.Deallocate(optixScratchMemory));
			Check(CudaBuffer.Deallocate(optixColorBuffer));
			Check(CudaBuffer.Deallocate(optixAlbedoBuffer));
			Check(CudaBuffer.Deallocate(optixOutputBuffer));
#endif
		}

		void Update()
		{
			uint2 currentSize = uint2(
				(uint) ceil(TargetCamera.pixelWidth * resolutionScaling),
				(uint) ceil(TargetCamera.pixelHeight * resolutionScaling));

			bool buffersNeedRebuild = any(currentSize != bufferSize);
			bool cameraDirty = TargetCamera.transform.hasChanged;
			bool traceDepthChanged = traceDepth != lastTraceDepth;
			bool samplesPerPixelDecreased = lastSamplesPerPixel != samplesPerPixel && TotalSamplesPerPixel > samplesPerPixel;

			bool traceNeedsReset = buffersNeedRebuild || worldNeedsRebuild || cameraDirty || traceDepthChanged || samplesPerPixelDecreased;

			if (traceNeedsReset || traceAborted)
			{
				int i = 0;
				bool ShouldCancel() => i++ > 0 || traceAborted;
				foreach (var jobData in scheduledSampleJobs)
					if (ShouldCancel())
						jobData.Cancel();

				i = 0;
				foreach (var jobData in scheduledCombineJobs)
					if (ShouldCancel())
						jobData.Cancel();

				i = 0;
				foreach (var jobData in scheduledDenoiseJobs)
					if (ShouldCancel())
						jobData.Cancel();

				i = 0;
				foreach (var jobData in scheduledFinalizeJobs)
					if (ShouldCancel())
						jobData.Cancel();

				foreach (var jobData in scheduledSampleJobs) jobData.Handle.Complete();
				foreach (var jobData in scheduledCombineJobs) jobData.Handle.Complete();
				foreach (var jobData in scheduledDenoiseJobs) jobData.Handle.Complete();
				foreach (var jobData in scheduledFinalizeJobs) jobData.Handle.Complete();
			}

			while (scheduledSampleJobs.Count > 0 && scheduledSampleJobs.Peek().Handle.IsCompleted)
			{
				ScheduledJobData<SampleBatchOutputData> completedJob = scheduledSampleJobs.Dequeue();
				completedJob.Complete();

				TimeSpan elapsedTime = DateTime.FromFileTimeUtc(completedJob.OutputData.Timing[1]) -
				                       DateTime.FromFileTimeUtc(completedJob.OutputData.Timing[0]);
				longBuffers.Return(completedJob.OutputData.Timing);

				int totalRayCount = completedJob.OutputData.ReducedRayCount.Value;
				int totalSamples = completedJob.OutputData.TotalSamples.Value;
				sampleCountWeightExtrema = completedJob.OutputData.SampleCountWeightExtrema.Value;
				int2 sampleCountExtrema = completedJob.OutputData.SampleCountExtrema.Value;
				intReferences.Return(completedJob.OutputData.ReducedRayCount);
				intReferences.Return(completedJob.OutputData.TotalSamples);
				float2References.Return(completedJob.OutputData.SampleCountWeightExtrema);
				int2References.Return(completedJob.OutputData.SampleCountExtrema);

				var totalBufferSize = (int) (bufferSize.x * bufferSize.y);
				LastBatchRayCountPerPixel = (float) totalRayCount / totalBufferSize;
				TotalSamplesPerPixel = (float) totalSamples / totalBufferSize;
				LastBatchDuration = elapsedTime.ToString("g");
				MillionRaysPerSecond = totalRayCount / (float) elapsedTime.TotalSeconds / 1000000;
				if (!ignoreBatchTimings) mraysPerSecResults.Add(MillionRaysPerSecond);
				AvgMRaysPerSecond = mraysPerSecResults.Count == 0 ? 0 : mraysPerSecResults.Average();
				SamplesPerPixelRange = sampleCountExtrema;
				ignoreBatchTimings = false;
#if UNITY_EDITOR
				ForceUpdateInspector();
#endif
				bool traceComplete = TotalSamplesPerPixel >= samplesPerPixel || maxDurationSeconds != -1 && traceTimer.Elapsed.TotalSeconds >= maxDurationSeconds;
				queuedSample = !traceAborted && !traceComplete;
			}

			while (scheduledCombineJobs.Count > 0 && scheduledCombineJobs.Peek().Handle.IsCompleted)
			{
				ScheduledJobData<PassOutputData> completedJob = scheduledCombineJobs.Dequeue();
				completedJob.Complete();
			}

			while (scheduledDenoiseJobs.Count > 0 && scheduledDenoiseJobs.Peek().Handle.IsCompleted)
			{
				ScheduledJobData<PassOutputData> completedJob = scheduledDenoiseJobs.Dequeue();
				completedJob.Complete();
			}

			LastTraceDuration = traceTimer.Elapsed.ToString("g");

			while (scheduledFinalizeJobs.Count > 0 && scheduledFinalizeJobs.Peek().Handle.IsCompleted)
			{
				ScheduledJobData<PassOutputData> completedJob = scheduledFinalizeJobs.Dequeue();
				completedJob.Complete();

				if (!traceAborted)
					SwapBuffers();
#if UNITY_EDITOR
				ForceUpdateInspector();
#endif
			}

			if (!TraceActive)
			{
				if (buffersNeedRebuild) EnsureBuffersBuilt();
				if (worldNeedsRebuild) RebuildWorld();
			}

			if (cameraDirty) CleanCamera();

			// kick if needed (with double-buffering)
			if ((queuedSample && scheduledFinalizeJobs.Count <= 1) ||
			    (traceNeedsReset && !traceAborted) ||
			    (!TraceActive && !stopWhenCompleted && !traceAborted))
			{
				ScheduleSample(
					traceNeedsReset || TotalSamplesPerPixel >= samplesPerPixel,
					scheduledSampleJobs.Count > 0 ? scheduledSampleJobs.Peek().Handle : null);

				queuedSample = false;
			}

			if (!TraceActive)
				traceTimer.Stop();
		}

		void ScheduleSample(bool firstBatch, JobHandle? dependency = null)
		{
			Transform cameraTransform = TargetCamera.transform;
			Vector3 origin = cameraTransform.position;
			Vector3 lookAt = origin + cameraTransform.forward;

			if (HitWorld(new Ray(origin, cameraTransform.forward), out HitRecord hitRec))
				focusDistance = hitRec.Distance;

			var raytracingCamera = new View(origin, lookAt, cameraTransform.up, TargetCamera.fieldOfView,
				TargetCamera.aspect, TargetCamera.GetComponent<CameraData>()?.ApertureSize ?? 0, focusDistance);

			var totalBufferSize = (int) (bufferSize.x * bufferSize.y);

			if (firstBatch)
			{
				if (!colorAccumulationBuffer.IsCreated) colorAccumulationBuffer = float4Buffers.Take();
				if (!normalAccumulationBuffer.IsCreated) normalAccumulationBuffer = float3Buffers.Take();
				if (!albedoAccumulationBuffer.IsCreated) albedoAccumulationBuffer = float3Buffers.Take();
				if (!sampleCountWeightAccumulationBuffer.IsCreated) sampleCountWeightAccumulationBuffer = floatBuffers.Take();

				colorAccumulationBuffer.ZeroMemory();
				normalAccumulationBuffer.ZeroMemory();
				albedoAccumulationBuffer.ZeroMemory();
				sampleCountWeightAccumulationBuffer.ZeroMemory();

				interlacingOffsetIndex = 0;

#if PATH_DEBUGGING
				debugPaths.EnsureCapacity(traceDepth);
#endif
				mraysPerSecResults.Clear();
				TotalSamplesPerPixel = 0;
				lastTraceDepth = traceDepth;
				lastSamplesPerPixel = samplesPerPixel;
				traceAborted = false;
#if UNITY_EDITOR
				ForceUpdateInspector();
#endif
			}

			NativeArray<float4> colorOutputBuffer = float4Buffers.Take();
			NativeArray<float3> normalOutputBuffer = float3Buffers.Take();
			NativeArray<float3> albedoOutputBuffer = float3Buffers.Take();
			NativeArray<float> sampleCountWeightOutputBuffer = floatBuffers.Take();

			NativeReference<bool> cancellationToken = boolReferences.Take();

			if (interlacingOffsets == null || interlacing != interlacingOffsets.Length)
				interlacingOffsets = Tools.SpaceFillingSeries(interlacing).ToArray();

			if (interlacingOffsetIndex >= interlacing)
				interlacingOffsetIndex = 0;

			if (interlacingOffsetIndex == 0)
			{
				blueNoise.CycleTexture();
				frameSeed = (uint) Time.frameCount + 1;
			}

			Environment environment = default;
			if (FindObjectOfType<Volume>().profile.TryGet<HDRISky>(out var hdriSky))
				environment = new Environment { SkyType = SkyType.CubeMap, SkyCubemap = new Cubemap(hdriSky.hdriSky.value as UnityEngine.Cubemap) };

			// TODO: Reimplement gradient sky
			// if (skyboxMaterial.TryGetProperty("_Color1", out Color bottomColor) && skyboxMaterial.TryGetProperty("_Color2", out Color topColor))
			// 	environment = new Environment { SkyType = SkyType.GradientSky, SkyBottomColor = bottomColor.linear.ToFloat3(), SkyTopColor = topColor.linear.ToFloat3() };

			SampleBatchJob sampleBatchJob;
			unsafe
			{
				sampleBatchJob = new SampleBatchJob
				{
					CancellationToken = cancellationToken,

					InputColor = colorAccumulationBuffer,
					InputNormal = normalAccumulationBuffer,
					InputAlbedo = albedoAccumulationBuffer,
					InputSampleCountWeight = sampleCountWeightAccumulationBuffer,

					OutputColor = colorOutputBuffer,
					OutputNormal = normalOutputBuffer,
					OutputAlbedo = albedoOutputBuffer,
					OutputSampleCountWeight = sampleCountWeightOutputBuffer,

					SliceOffset = interlacingOffsets[interlacingOffsetIndex++],
					SliceDivider = interlacing,

					Size = bufferSize,
					View = raytracingCamera,
					Environment = environment,


					Seed = frameSeed,
					SampleCountRange = min(samplesPerPixel, (uint2) (float2) samplesPerBatchRange),
					TraceDepth = traceDepth,
					SubPixelJitter = subPixelJitter,
					BvhRoot = BvhRoot,
					PerlinNoise = perlinNoise.GetRuntimeData(),
					BlueNoise = blueNoise.GetRuntimeData(frameSeed),
					NoiseColor = noiseColor,
					OutputDiagnostics = diagnosticsBuffer,
					SampleCountWeightExtrema = this.sampleCountWeightExtrema,
#if PATH_DEBUGGING
					DebugPaths = (DebugPath*) debugPaths.GetUnsafePtr(),
					DebugCoordinates = int2 (bufferSize / 2)
#endif
				};
			}

			NativeArray<long> timingBuffer = longBuffers.Take();

			JobHandle sampleBatchJobHandle;
			if (interlacing > 1)
			{
				using var handles = new NativeArray<JobHandle>(6, Allocator.Temp)
				{
					[0] = new CopyFloat4BufferJob { CancellationToken = cancellationToken, Input = colorAccumulationBuffer, Output = colorOutputBuffer }.Schedule(dependency ?? default),
					[1] = new CopyFloat3BufferJob { CancellationToken = cancellationToken, Input = normalAccumulationBuffer, Output = normalOutputBuffer }.Schedule(dependency ?? default),
					[2] = new CopyFloat3BufferJob { CancellationToken = cancellationToken, Input = albedoAccumulationBuffer, Output = albedoOutputBuffer }.Schedule(dependency ?? default),
					[3] = new CopyFloatBufferJob { CancellationToken = cancellationToken, Input = sampleCountWeightAccumulationBuffer, Output = sampleCountWeightOutputBuffer }.Schedule(dependency ?? default),
					[4] = new ClearBufferJob<Diagnostics> { CancellationToken = cancellationToken, Buffer = diagnosticsBuffer }.Schedule(dependency ?? default)
				};

				JobHandle combinedDependencies = JobHandle.CombineDependencies(handles);
				JobHandle startTimerJobHandle = new RecordTimeJob { Buffer = timingBuffer, Index = 0 }.Schedule(combinedDependencies);
				sampleBatchJobHandle = sampleBatchJob.Schedule(totalBufferSize, 1, startTimerJobHandle);
			}
			else
			{
				JobHandle startTimerJobHandle = new RecordTimeJob { Buffer = timingBuffer, Index = 0 }.Schedule(dependency ?? default);
				sampleBatchJobHandle = sampleBatchJob.Schedule(totalBufferSize, 1, startTimerJobHandle);
			}

			sampleBatchJobHandle = new RecordTimeJob { Buffer = timingBuffer, Index = 1 }.Schedule(sampleBatchJobHandle);

			NativeReference<int> reducedRayCountReference = intReferences.Take();
			NativeReference<int> totalSamplesReference = intReferences.Take();
			NativeReference<float2> sampleCountWeightExtrema = float2References.Take();
			NativeReference<int2> sampleCountExtremaReference = int2References.Take();

			JobHandle reduceMetricsJobHandle = new ReduceMetricsJob
			{
				Diagnostics = diagnosticsBuffer,
				AccumulatedColor = colorOutputBuffer,
				TotalRayCount = reducedRayCountReference,
				AccumulatedSampleCountWeight = sampleCountWeightOutputBuffer,
				SampleCountWeightExtrema = sampleCountWeightExtrema,
				SampleCountExtrema = sampleCountExtremaReference,
				TotalSamples = totalSamplesReference
			}.Schedule(sampleBatchJobHandle);

			var outputData = new SampleBatchOutputData
			{
				Color = float4Buffers.Take(),
				Normal = float3Buffers.Take(),
				Albedo = float3Buffers.Take(),
				SampleCountWeight = floatBuffers.Take(),
				ReducedRayCount = reducedRayCountReference,
				SampleCountWeightExtrema = sampleCountWeightExtrema,
				SampleCountExtrema = sampleCountExtremaReference,
				TotalSamples = totalSamplesReference,
				Timing = timingBuffer
			};

			using var jobArray = new NativeArray<JobHandle>(5, Allocator.Temp)
			{
				[0] = new CopyFloat4BufferJob { CancellationToken = cancellationToken, Input = colorOutputBuffer, Output = outputData.Color }.Schedule(reduceMetricsJobHandle),
				[1] = new CopyFloat3BufferJob { CancellationToken = cancellationToken, Input = normalOutputBuffer, Output = outputData.Normal }.Schedule(reduceMetricsJobHandle),
				[2] = new CopyFloat3BufferJob { CancellationToken = cancellationToken, Input = albedoOutputBuffer, Output = outputData.Albedo }.Schedule(reduceMetricsJobHandle),
				[3] = new CopyFloatBufferJob { CancellationToken = cancellationToken, Input = sampleCountWeightOutputBuffer, Output = outputData.SampleCountWeight }.Schedule(reduceMetricsJobHandle),
			};
			JobHandle combinedDependency = JobHandle.CombineDependencies(jobArray);

			NativeArray<float4> colorInputBuffer = colorAccumulationBuffer;
			NativeArray<float3> normalInputBuffer = normalAccumulationBuffer,
				albedoInputBuffer = albedoAccumulationBuffer;
			NativeArray<float> sampleCountWeightInputBuffer = sampleCountWeightAccumulationBuffer;

			scheduledSampleJobs.Enqueue(new ScheduledJobData<SampleBatchOutputData>
			{
				CancellationToken = cancellationToken,
				Handle = combinedDependency,
				OutputData = outputData,
				OnComplete = () =>
				{
					float4Buffers.Return(colorInputBuffer);
					float3Buffers.Return(normalInputBuffer);
					float3Buffers.Return(albedoInputBuffer);
					floatBuffers.Return(sampleCountWeightInputBuffer);
					boolReferences.Return(cancellationToken);
				}
			});

			// cycle accumulation output into the next accumulation pass's input
			colorAccumulationBuffer = colorOutputBuffer;
			normalAccumulationBuffer = normalOutputBuffer;
			albedoAccumulationBuffer = albedoOutputBuffer;
			sampleCountWeightAccumulationBuffer = sampleCountWeightOutputBuffer;

			bool traceComplete = TotalSamplesPerPixel >= samplesPerPixel || maxDurationSeconds != -1 && traceTimer.Elapsed.TotalSeconds >= maxDurationSeconds;

			if (traceComplete || previewAfterBatch)
				ScheduleCombine(combinedDependency, outputData);

			// schedule another accumulate (but no more than one)
			if (!dependency.HasValue && !traceComplete)
				ScheduleSample(false, JobHandle.CombineDependencies(combinedDependency, reduceMetricsJobHandle));

			JobHandle.ScheduleBatchedJobs();

			if (firstBatch) traceTimer.Restart();
		}

		void ScheduleCombine(JobHandle dependency, SampleBatchOutputData sampleBatchOutput)
		{
			NativeReference<bool> cancellationToken = boolReferences.Take();

			var combineJob = new CombineJob
			{
				CancellationToken = cancellationToken,

				DebugMode = debugFailedSamples,
#if ENABLE_OPTIX
				LdrAlbedo = denoiseMode == DenoiseMode.NvidiaOptix,
#else
				LdrAlbedo = false,
#endif

				InputColor = sampleBatchOutput.Color,
				InputNormal = sampleBatchOutput.Normal,
				InputAlbedo = sampleBatchOutput.Albedo,
				Size = (int2) bufferSize,

				OutputColor = float3Buffers.Take(),
				OutputNormal = float3Buffers.Take(),
				OutputAlbedo = float3Buffers.Take()
			};

			var totalBufferSize = (int) (bufferSize.x * bufferSize.y);
			JobHandle combineJobHandle = combineJob.Schedule(totalBufferSize, 128, dependency);

			var combineOutputData = new PassOutputData
			{
				Color = combineJob.OutputColor,
				Normal = combineJob.OutputNormal,
				Albedo = combineJob.OutputAlbedo
			};

			scheduledCombineJobs.Enqueue(new ScheduledJobData<PassOutputData>
			{
				CancellationToken = cancellationToken,
				Handle = combineJobHandle,
				OutputData = combineOutputData,
				OnComplete = () =>
				{
					float4Buffers.Return(sampleBatchOutput.Color);
					float3Buffers.Return(sampleBatchOutput.Normal);
					float3Buffers.Return(sampleBatchOutput.Albedo);
					floatBuffers.Return(sampleBatchOutput.SampleCountWeight);
					boolReferences.Return(cancellationToken);
				}
			});

			if (denoiseMode != DenoiseMode.None)
				ScheduleDenoise(combineJobHandle, combineOutputData);
			else
				ScheduleFinalize(combineJobHandle, combineOutputData);
		}

		void ScheduleDenoise(JobHandle dependency, PassOutputData combineOutput)
		{
			NativeArray<float3> denoiseColorOutputBuffer = float3Buffers.Take();
			NativeReference<bool> cancellationToken = boolReferences.Take();

			JobHandle denoiseJobHandle = default;

			JobHandle combinedDependency = dependency;
			foreach (ScheduledJobData<PassOutputData> priorFinalizeJob in scheduledDenoiseJobs)
				combinedDependency = JobHandle.CombineDependencies(combinedDependency, priorFinalizeJob.Handle);

			switch (denoiseMode)
			{
				case DenoiseMode.OpenImageDenoise:
				{
					var denoiseJob = new OpenImageDenoiseJob
					{
						CancellationToken = cancellationToken,
						Width = (ulong) bufferSize.x,
						Height = (ulong) bufferSize.y,
						InputColor = combineOutput.Color,
						InputNormal = combineOutput.Normal,
						InputAlbedo = combineOutput.Albedo,
						OutputColor = denoiseColorOutputBuffer,
						DenoiseFilter = oidnFilter
					};
					denoiseJobHandle = denoiseJob.Schedule(combinedDependency);
					break;
				}

#if ENABLE_OPTIX
				case DenoiseMode.NvidiaOptix:
				{
					var denoiseJob = new OptixDenoiseJob
					{
						CancellationToken = cancellationBuffer,
						Denoiser = optixDenoiser,
						CudaStream = cudaStream,
						InputColor = combineOutput.Color,
						InputAlbedo = combineOutput.Albedo,
						OutputColor = denoiseColorOutputBuffer,
						BufferSize = (uint2) bufferSize,
						DenoiserState = optixDenoiserState,
						ScratchMemory = optixScratchMemory,
						DenoiserSizes = optixDenoiserSizes,
						InputAlbedoBuffer = optixAlbedoBuffer,
						InputColorBuffer = optixColorBuffer,
						OutputColorBuffer = optixOutputBuffer
					};
					denoiseJobHandle = denoiseJob.Schedule(combinedDependency);
					break;
				}
#endif
			}

			var copyOutputData = new PassOutputData
			{
				Color = denoiseColorOutputBuffer,
				Normal = combineOutput.Normal,
				Albedo = combineOutput.Albedo
			};

			scheduledDenoiseJobs.Enqueue(new ScheduledJobData<PassOutputData>
			{
				CancellationToken = cancellationToken,
				Handle = denoiseJobHandle,
				OutputData = copyOutputData,
				OnComplete = () =>
				{
					float3Buffers.Return(combineOutput.Color);
					boolReferences.Return(cancellationToken);
				}
			});

			ScheduleFinalize(denoiseJobHandle, copyOutputData);
		}

		void ScheduleFinalize(JobHandle dependency, PassOutputData lastPassOutput)
		{
			NativeReference<bool> cancellationToken = boolReferences.Take();

			var finalizeJob = new FinalizeTexturesJob
			{
				CancellationToken = cancellationToken,

				InputColor = lastPassOutput.Color,
				InputNormal = lastPassOutput.Normal,
				InputAlbedo = lastPassOutput.Albedo,

				OutputColor = frontBuffer,
				OutputNormal = normalsBuffer,
				OutputAlbedo = albedoBuffer
			};

			var totalBufferSize = (int) (bufferSize.x * bufferSize.y);

			JobHandle combinedDependency = dependency;
			foreach (ScheduledJobData<PassOutputData> priorFinalizeJob in scheduledFinalizeJobs)
				combinedDependency = JobHandle.CombineDependencies(combinedDependency, priorFinalizeJob.Handle);

			JobHandle finalizeJobHandle = finalizeJob.Schedule(totalBufferSize, 128, combinedDependency);

			scheduledFinalizeJobs.Enqueue(new ScheduledJobData<PassOutputData>
			{
				CancellationToken = cancellationToken,
				Handle = finalizeJobHandle,
				OnComplete = () =>
				{
					float3Buffers.Return(lastPassOutput.Color);
					float3Buffers.Return(lastPassOutput.Normal);
					float3Buffers.Return(lastPassOutput.Albedo);
					boolReferences.Return(cancellationToken);
				}
			});
		}

		void CleanCamera()
		{
#if UNITY_EDITOR
			TargetCamera.transform.hasChanged = false;
#endif
		}

		unsafe void SwapBuffers()
		{
			float bufferMin = float.MaxValue, bufferMax = float.MinValue;
			var diagnosticsPtr = (Diagnostics*) NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(diagnosticsBuffer);

			switch (bufferView)
			{
				case BufferView.RayCount:
					for (int i = 0; i < diagnosticsBuffer.Length; i++, ++diagnosticsPtr)
					{
						Diagnostics value = *diagnosticsPtr;
						bufferMin = min(bufferMin, value.RayCount);
						bufferMax = max(bufferMax, value.RayCount);
					}

					break;

#if FULL_DIAGNOSTICS && BVH
				case BufferView.BvhHitCount:
					for (int i = 0; i < diagnosticsBuffer.Length; i++, ++diagnosticsPtr)
					{
						Diagnostics value = *diagnosticsPtr;
						bufferMin = min(bufferMin, value.BoundsHitCount);
						bufferMax = max(bufferMax, value.BoundsHitCount);
					}
					break;

				case BufferView.CandidateCount:
					for (int i = 0; i < diagnosticsBuffer.Length; i++, ++diagnosticsPtr)
					{
						Diagnostics value = *diagnosticsPtr;
						bufferMin = min(bufferMin, value.CandidateCount);
						bufferMax = max(bufferMax, value.CandidateCount);
					}
					break;

				case BufferView.SampleCountWeight:
					for (int i = 0; i < diagnosticsBuffer.Length; i++, ++diagnosticsPtr)
					{
						Diagnostics value = *diagnosticsPtr;
						bufferMin = min(bufferMin, value.SampleCountWeight);
						bufferMax = max(bufferMax, value.SampleCountWeight);
					}
					break;
#endif
			}

			switch (bufferView)
			{
				case BufferView.Front: frontBufferTexture.Apply(false); break;
				case BufferView.Normals: normalsTexture.Apply(false); break;
				case BufferView.Albedo: albedoTexture.Apply(false); break;

				default:
					BufferValueRange = float2(bufferMin, bufferMax);
					diagnosticsTexture.Apply(false);
					viewRangeMaterial.SetVector(minimumRangePropertyId, new Vector4(bufferMin, bufferMax - bufferMin));
					break;
			}

			bool traceComplete = TotalSamplesPerPixel >= samplesPerPixel || maxDurationSeconds != -1 && traceTimer.Elapsed.TotalSeconds >= maxDurationSeconds;
			if (traceComplete && stopWhenCompleted && saveWhenCompleted)
				SaveFrontBuffer();
		}

		private void OnCustomRender(ScriptableRenderContext context, HDCamera hdCamera)
		{
			var commandBuffer = CommandBufferPool.Get("Raytracer Blit");
			var blitTarget = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);

			switch (bufferView)
			{
				case BufferView.Front:
					blitMaterial.SetTexture("_MainTex", frontBufferTexture);
					CoreUtils.DrawFullScreen(commandBuffer, blitMaterial, blitTarget);
					break;
				case BufferView.Normals:
					blitMaterial.SetTexture("_MainTex", normalsTexture);
					CoreUtils.DrawFullScreen(commandBuffer, blitMaterial, blitTarget);
					break;
				case BufferView.Albedo:
					blitMaterial.SetTexture("_MainTex", albedoTexture);
					CoreUtils.DrawFullScreen(commandBuffer, blitMaterial, blitTarget);
					break;

				default:
					viewRangeMaterial.SetInt(channelPropertyId, (int) bufferView - 1);
					viewRangeMaterial.SetTexture("_MainTex", diagnosticsTexture);
					CoreUtils.DrawFullScreen(commandBuffer, viewRangeMaterial, blitTarget);
					break;
			}

			context.ExecuteCommandBuffer(commandBuffer);
			CommandBufferPool.Release(commandBuffer);
		}

		void EnsureBuffersBuilt()
		{
			int width = (int) ceil(TargetCamera.pixelWidth * resolutionScaling);
			int height = (int) ceil(TargetCamera.pixelHeight * resolutionScaling);

			float2 lastBufferSize = bufferSize;
			bufferSize = float2(width, height);

			if ((int) lastBufferSize.x != width || (int) lastBufferSize.y != height)
			{
				floatBuffers.Reset();
				float3Buffers.Reset();
				float4Buffers.Reset();
				colorAccumulationBuffer = default;
				albedoAccumulationBuffer = normalAccumulationBuffer = default;
				sampleCountWeightAccumulationBuffer = default;

#if ENABLE_OPTIX
				RebuildOptixBuffers((uint2) lastBufferSize);
#endif
			}

			if (frontBufferTexture.width != width || frontBufferTexture.height != height ||
			    diagnosticsTexture.width != width || diagnosticsTexture.height != height ||
			    normalsTexture.width != width || normalsTexture.height != height ||
			    albedoTexture.width != width || albedoTexture.height != height)
			{
				void PrepareTexture<T>(Texture2D texture, out NativeArray<T> buffer) where T : struct
				{
					texture.Reinitialize(width, height);
					texture.filterMode = resolutionScaling > 1 ? FilterMode.Bilinear : FilterMode.Point;
					buffer = texture.GetRawTextureData<T>();
				}

				PrepareTexture(frontBufferTexture, out frontBuffer);
				PrepareTexture(diagnosticsTexture, out diagnosticsBuffer);
				PrepareTexture(normalsTexture, out normalsBuffer);
				PrepareTexture(albedoTexture, out albedoBuffer);

				Debug.Log($"Rebuilt textures (now {width} x {height})");
			}
		}

#if ENABLE_OPTIX
		unsafe void RebuildOptixBuffers(uint2 lastBufferSize)
		{
			var newBufferSize = (uint2) bufferSize;

			SizeT lastSizeInBytes = lastBufferSize.x * lastBufferSize.y * sizeof(float3);
			SizeT newSizeInBytes = newBufferSize.x * newBufferSize.y * sizeof(float3);

			void Check(CudaError cudaError)
			{
				if (cudaError != CudaError.Success)
					Debug.LogError($"CUDA Error : {cudaError}");
			}

			Check(optixColorBuffer.EnsureCapacity(lastSizeInBytes, newSizeInBytes));
			Check(optixAlbedoBuffer.EnsureCapacity(lastSizeInBytes, newSizeInBytes));
			Check(optixOutputBuffer.EnsureCapacity(lastSizeInBytes, newSizeInBytes));

			OptixDenoiserSizes lastSizes = optixDenoiserSizes;
			OptixDenoiserSizes newSizes = default;
			OptixDenoiser.ComputeMemoryResources(optixDenoiser, newBufferSize.x, newBufferSize.y, &newSizes);

			Check(optixScratchMemory.EnsureCapacity(lastSizes.RecommendedScratchSizeInBytes, newSizes.RecommendedScratchSizeInBytes));
			Check(optixDenoiserState.EnsureCapacity(lastSizes.StateSizeInBytes, newSizes.StateSizeInBytes));

			optixDenoiserSizes = newSizes;

			OptixDenoiser.Setup(optixDenoiser, cudaStream, newBufferSize.x, newBufferSize.y, optixDenoiserState,
				newSizes.StateSizeInBytes, optixScratchMemory, newSizes.RecommendedScratchSizeInBytes);
		}
#endif
		void RebuildWorld()
		{
			CollectActiveEntities();

			using (RebuildEntityBuffersMarker.Auto())
			using (new ScopedStopwatch("^"))
				RebuildEntityBuffers();

			using (RebuildBvhMarker.Auto())
			using (new ScopedStopwatch("^"))
				RebuildBvh();

			var seedProvider = FindObjectOfType<RandomSeedData>();
			perlinNoise.Generate(seedProvider == null ? 1 : seedProvider.RandomSeed);

			worldNeedsRebuild = false;
		}

		unsafe void RebuildEntityBuffers()
		{
			// TODO: Non-mesh primitives
			// TODO: Movement support

			var materialMap = new Dictionary<UnityEngine.Material, int>();

			int entityCount = FindObjectsOfType<MeshRenderer>().Count(x => x.enabled);
			int triangleCount = FindObjectsOfType<MeshFilter>().Sum(x => x.sharedMesh.triangles.Length);

			// Preallocate lists
			materialBuffer.EnsureCapacity(entityCount);
			entityBuffer.EnsureCapacity(triangleCount);
			triangleBuffer.EnsureCapacity(triangleCount);

			// Clear lists
			materialBuffer.Clear();
			entityBuffer.Clear();
			triangleBuffer.Clear();

			// Collect mesh renderers
			// TODO: Sub-mesh support
			foreach (var meshRenderer in FindObjectsOfType<MeshRenderer>().Where(x => x.enabled))
			{
				UnityEngine.Material unityMaterial = meshRenderer.sharedMaterials.Last();
				if (!materialMap.TryGetValue(unityMaterial, out int materialIndex))
				{
					unityMaterial.TryGetProperty("_BaseColor", out Color albedo);
					Texture meshAlbedoTexture;
					if (unityMaterial.TryGetProperty("_BaseColorMap", out Texture2D albedoMap) && albedoMap.format == TextureFormat.RGB24)
						meshAlbedoTexture = new Texture(TextureType.Image, albedo.linear.ToFloat3(),
							pImage: (byte*) albedoMap.GetRawTextureData<RGB24>().GetUnsafeReadOnlyPtr(),
							imageWidth: albedoMap.width, imageHeight: albedoMap.height);
					else
						meshAlbedoTexture = new Texture(TextureType.Constant, albedo.linear.ToFloat3());

					unityMaterial.TryGetProperty("_EmissiveColor", out Color emission);
					Texture meshEmissionTexture;
					if (unityMaterial.TryGetProperty("_EmissiveColorMap", out Texture2D emissionMap) && emissionMap.format == TextureFormat.RGB24)
						meshEmissionTexture = new Texture(TextureType.Image, emission.linear.ToFloat3(),
							pImage: (byte*) emissionMap.GetRawTextureData<RGB24>().GetUnsafeReadOnlyPtr(),
							imageWidth: emissionMap.width, imageHeight: emissionMap.height);
					else
						meshEmissionTexture = new Texture(TextureType.Constant, emission.linear.ToFloat3());

					unityMaterial.TryGetProperty("_Metallic", out float metallic);
					Texture meshMetallicTexture;
					if (unityMaterial.TryGetProperty("_MetallicGlossMap", out Texture2D metallicMap))
					{
						int pixelStride;
						byte* imagePointer;
						switch (metallicMap.format)
						{
							case TextureFormat.RGB24:
								imagePointer = (byte*) metallicMap.GetRawTextureData<RGB24>().GetUnsafeReadOnlyPtr();
								pixelStride = 3;
								break;
							case TextureFormat.RGBA32:
								imagePointer = (byte*)metallicMap.GetRawTextureData<RGBA32>().GetUnsafeReadOnlyPtr();
								pixelStride = 4;
								break;
							default:
								throw new NotSupportedException($"Unsupported texture format for metallic/gloss map : {metallicMap.format}");
						}
						meshMetallicTexture = new Texture(TextureType.Image, metallic, pImage: imagePointer, imageWidth: metallicMap.width, imageHeight: metallicMap.height, pixelStride: pixelStride);
					}
					else
						meshMetallicTexture = new Texture(TextureType.Constant, metallic);

					unityMaterial.TryGetProperty("_Smoothness", out float glossiness);
					Texture meshGlossinessTexture;
					if (unityMaterial.shaderKeywords.Contains("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") && albedoMap && albedoMap.format == TextureFormat.RGBA32)
					{
						meshGlossinessTexture = new Texture(TextureType.Image, glossiness,
							pImage: (byte*) albedoMap.GetRawTextureData<RGBA32>().GetUnsafeReadOnlyPtr(),
							imageWidth: albedoMap.width, imageHeight: albedoMap.height, pixelStride: 4, scalarValueChannel: 3);
					}
					else if (metallicMap && metallicMap.format == TextureFormat.RGBA32)
						meshGlossinessTexture = new Texture(TextureType.Image, glossiness,
							pImage: (byte*) metallicMap.GetRawTextureData<RGBA32>().GetUnsafeReadOnlyPtr(),
							imageWidth: metallicMap.width, imageHeight: metallicMap.height, pixelStride: 4, scalarValueChannel: 3);
					else
						meshGlossinessTexture = new Texture(TextureType.Constant, glossiness);

					Material material;
					if (unityMaterial.TryGetProperty("_Density", out float density))
						material = new Material(MaterialType.ProbabilisticVolume, meshAlbedoTexture, density: density);
					else if (unityMaterial.TryGetProperty("_RefractionModel", out float refractionModel) && refractionModel > 0 && unityMaterial.TryGetProperty("_Ior", out float refractiveIndex))
						material = new Material(MaterialType.Dielectric, meshAlbedoTexture, glossiness: meshGlossinessTexture, indexOfRefraction: refractiveIndex);
					else
						material = new Material(MaterialType.Standard, meshAlbedoTexture, meshEmissionTexture, meshGlossinessTexture, meshMetallicTexture);

					materialBuffer.AddNoResize(material);
					materialIndex = materialBuffer.Length - 1;
					materialMap[unityMaterial] = materialIndex;
				}

				Transform meshTransform = meshRenderer.transform;
				var rigidTransform = new RigidTransform(meshTransform.rotation, meshTransform.position);

				var meshFilter = meshRenderer.gameObject.GetComponent<MeshFilter>();
				var meshData = meshRenderer.gameObject.GetComponentInParent<MeshData>();

				using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(meshFilter.sharedMesh);

				var addMeshJob = new AddMeshRuntimeEntitiesJob
				{
					Entities = entityBuffer,
					Triangles = triangleBuffer,
					FaceNormals = meshData && meshData.FaceNormals,
					Material = materialBuffer.GetUnsafeList()->Ptr + materialIndex,
					MeshDataArray = meshDataArray,
					RigidTransform = rigidTransform,
					Scale = csum(meshTransform.lossyScale) / 3
				};
				addMeshJob.Schedule().Complete();
			}

			Debug.Log($"Rebuilt entity buffer of {entityBuffer.Length} entities");
		}

		void RebuildBvh(bool editorOnly = false)
		{
			bvhEntities.EnsureCapacity(entityBuffer.Length);
			bvhEntities.Clear();

			bvhNodeDataBuffer.EnsureCapacity(max(entityBuffer.Length * 2, 1));
			bvhNodeDataBuffer.Clear();

			using (var bvhBuildingEntityBuffer = new NativeArray<BvhBuildingEntity>(entityBuffer.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
			{
				var initJob = new CreateBvhBuildingEntitiesJob
				{
					Entities = entityBuffer,
					BvhBuildingEntities = bvhBuildingEntityBuffer
				};
				JobHandle initJobHandle = initJob.Schedule(entityBuffer.Length, 128);

				var buildJob = new BuildBvhJob
				{
					MaxDepth = maxBvhDepth,
					Entities = bvhBuildingEntityBuffer,
					BvhEntities = bvhEntities,
					BvhNodes = bvhNodeDataBuffer
				};
				JobHandle buildJobHandle = buildJob.Schedule(initJobHandle);

				initJobHandle.Complete();
				buildJobHandle.Complete();
			}

			int nodeCount = BvhRootData.Value.ChildCount;
			Debug.Log($"Rebuilt BVH ({BvhRootData.Value.ChildCount} nodes for {entityBuffer.Length} entities)");

			if (editorOnly)
				return;

			bvhNodeBuffer.EnsureCapacity(nodeCount);

			var buildRuntimeBvhJob = new BuildRuntimeBvhJob
			{
				BvhNodeBuffer = bvhNodeBuffer,
				BvhNodeDataBuffer = bvhNodeDataBuffer,
				NodeCount = nodeCount
			};
			buildRuntimeBvhJob.Schedule().Complete();
		}

		unsafe bool HitWorld(Ray r, out HitRecord hitRec) => BvhRoot->Hit(r, 0, float.PositiveInfinity, out hitRec);

		void CollectActiveEntities()
		{
			// TODO: Random entity groups
			// if (scene.RandomEntityGroups != null)
			// {
			// 	var rng = new Random(scene.RandomSeed);
			// 	foreach (RandomEntityGroup group in scene.RandomEntityGroups)
			// 	{
			// 		MaterialData GetMaterial()
			// 		{
			// 			(float lambertian, float metal, float dielectric, float light) probabilities = (
			// 				group.LambertChance,
			// 				group.MetalChance,
			// 				group.DieletricChance,
			// 				group.LightChance);
			//
			// 			float sum = probabilities.lambertian + probabilities.metal + probabilities.dielectric + probabilities.light;
			// 			probabilities.metal += probabilities.lambertian;
			// 			probabilities.dielectric += probabilities.metal;
			// 			probabilities.light += probabilities.dielectric;
			// 			probabilities.lambertian /= sum;
			// 			probabilities.metal /= sum;
			// 			probabilities.dielectric /= sum;
			// 			probabilities.light /= sum;
			//
			// 			MaterialData material = null;
			// 			float randomValue = rng.NextFloat();
			// 			if (randomValue < probabilities.lambertian)
			// 			{
			// 				Color from = group.DiffuseAlbedo.colorKeys[0].color;
			// 				Color to = group.DiffuseAlbedo.colorKeys[1].color;
			// 				float3 color = rng.NextFloat3(from.ToFloat3(), to.ToFloat3());
			// 				if (group.DoubleSampleDiffuseAlbedo)
			// 					color *= rng.NextFloat3(from.ToFloat3(), to.ToFloat3());
			// 				material = MaterialData.Lambertian(TextureData.Constant(color), 1);
			// 			}
			// 			else if (randomValue < probabilities.metal)
			// 			{
			// 				Color from = group.MetalAlbedo.colorKeys[0].color;
			// 				Color to = group.MetalAlbedo.colorKeys[1].color;
			// 				float3 color = rng.NextFloat3(from.ToFloat3(), to.ToFloat3());
			// 				float fuzz = rng.NextFloat(group.Fuzz.x, group.Fuzz.y);
			// 				material = MaterialData.Metal(TextureData.Constant(color), 1, TextureData.Constant(fuzz));
			// 			}
			// 			else if (randomValue < probabilities.dielectric)
			// 			{
			// 				material = MaterialData.Dielectric(
			// 					rng.NextFloat(group.RefractiveIndex.x, group.RefractiveIndex.y),
			// 					TextureData.Constant(1), TextureData.Constant(0));
			// 			}
			// 			else if (randomValue < probabilities.light)
			// 			{
			// 				Color from = group.Emissive.colorKeys[0].color;
			// 				Color to = group.Emissive.colorKeys[1].color;
			// 				float3 color = rng.NextFloat3(from.ToFloat3(), to.ToFloat3());
			// 				material = MaterialData.DiffuseLight(TextureData.Constant(color));
			// 			}
			//
			// 			return material;
			// 		}
			//
			// 		// TODO: fix overlap test to account for all entity types
			// 		bool AnyOverlap(float3 center, float radius) => ActiveEntities
			// 			.Where(x => x.Type == EntityType.Sphere)
			// 			.Any(x => !x.SphereData.ExcludeFromOverlapTest &&
			// 			          distance(x.Position, center) < x.SphereData.Radius + radius + group.MinDistance);
			//
			// 		EntityData GetEntity(float3 center, float3 radius)
			// 		{
			// 			bool moving = rng.NextFloat() < group.MovementChance;
			// 			quaternion rotation = quaternion.Euler(group.Rotation);
			// 			var entityData = new EntityData
			// 			{
			// 				Type = group.Type,
			// 				Position = rotate(rotation, center - (float3) group.Offset) + (float3) group.Offset,
			// 				Rotation = rotation,
			// 				Material = GetMaterial()
			// 			};
			// 			switch (group.Type)
			// 			{
			// 				case EntityType.Sphere: entityData.SphereData = new SphereData(radius.x); break;
			// 				case EntityType.Box: entityData.BoxData = new BoxData(radius); break;
			// 				case EntityType.Rect: entityData.RectData = new RectData(radius.xy); break;
			// 				case EntityType.Triangle: break; // TODO
			// 			}
			//
			// 			if (moving)
			// 			{
			// 				float3 offset = rng.NextFloat3(
			// 					float3(group.MovementXOffset.x, group.MovementYOffset.x, group.MovementZOffset.x),
			// 					float3(group.MovementXOffset.y, group.MovementYOffset.y, group.MovementZOffset.y));
			//
			// 				entityData.TimeRange = new Vector2(0, 1);
			// 				entityData.Moving = true;
			// 				entityData.DestinationOffset = offset;
			// 			}
			//
			// 			return entityData;
			// 		}
			//
			// 		switch (group.Distribution)
			// 		{
			// 			case RandomDistribution.DartThrowing:
			// 				for (int i = 0; i < group.TentativeCount; i++)
			// 				{
			// 					float3 center = rng.NextFloat3(
			// 						float3(-group.SpreadX / 2, -group.SpreadY / 2, -group.SpreadZ / 2),
			// 						float3(group.SpreadX / 2, group.SpreadY / 2, group.SpreadZ / 2));
			//
			// 					center += (float3) group.Offset;
			//
			// 					float radius = rng.NextFloat(group.Radius.x, group.Radius.y);
			//
			// 					if (group.OffsetByRadius)
			// 						center += radius;
			//
			// 					if (!group.SkipOverlapTest && AnyOverlap(center, radius))
			// 						continue;
			//
			// 					ActiveEntities.Add(GetEntity(center, radius));
			// 				}
			// 				break;
			//
			// 			case RandomDistribution.JitteredGrid:
			// 				float3 ranges = float3(group.SpreadX, group.SpreadY, group.SpreadZ);
			// 				float3 cellSize = float3(group.PeriodX, group.PeriodY, group.PeriodZ) * sign(ranges);
			//
			// 				// correct the range so that it produces the same result as the book
			// 				float3 correctedRangeEnd = (float3) group.Offset + ranges / 2;
			// 				float3 period = max(float3(group.PeriodX, group.PeriodY, group.PeriodZ), 1);
			// 				correctedRangeEnd += (1 - abs(sign(ranges))) * period / 2;
			//
			// 				for (float i = group.Offset.x - ranges.x / 2; i < correctedRangeEnd.x; i += period.x)
			// 				for (float j = group.Offset.y - ranges.y / 2; j < correctedRangeEnd.y; j += period.y)
			// 				for (float k = group.Offset.z - ranges.z / 2; k < correctedRangeEnd.z; k += period.z)
			// 				{
			// 					float3 center = float3(i, j, k) + rng.NextFloat3(group.PositionVariation * cellSize);
			// 					float3 radius = rng.NextFloat(group.Radius.x, group.Radius.y) *
			// 					                float3(rng.NextFloat(group.ScaleVariationX.x, group.ScaleVariationX.y),
			// 						                rng.NextFloat(group.ScaleVariationY.x, group.ScaleVariationY.y),
			// 						                rng.NextFloat(group.ScaleVariationZ.x, group.ScaleVariationZ.y));
			//
			// 					if (!group.SkipOverlapTest && AnyOverlap(center, radius.x))
			// 						continue;
			//
			// 					ActiveEntities.Add(GetEntity(center, radius));
			// 				}
			// 				break;
			// 		}
			// 	}
			// }
		}
	}
}