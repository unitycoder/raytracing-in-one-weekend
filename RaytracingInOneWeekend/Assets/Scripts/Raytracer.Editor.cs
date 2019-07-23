#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using static Unity.Mathematics.math;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
using System.IO;
#else
using Title = UnityEngine.HeaderAttribute;
#endif

namespace RaytracerInOneWeekend
{
	partial class Raytracer
	{
		[Title("Tools")]
#if ODIN_INSPECTOR
		[Button]
		[DisableInEditorMode]
		void SaveFrontBuffer()
		{
			byte[] pngBytes = frontBufferTexture.EncodeToPNG();
			File.WriteAllBytes(
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
					$"Raytracer {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png"), pngBytes);
		}

		[DisableIf(nameof(TraceActive))]
		[DisableInEditorMode]
		[ButtonGroup("Trace")]
		void TriggerTrace() => ScheduleAccumulate(true);

		[EnableIf(nameof(TraceActive))]
		[DisableInEditorMode]
		[ButtonGroup("Trace")]
		void AbortTrace() => traceAborted = true;
#endif
#if BVH
		[SerializeField] bool previewBvh = false;
#endif

		CommandBuffer opaquePreviewCommandBuffer, transparentPreviewCommandBuffer;
		bool hookedEditorUpdate;

		[SerializeField] [HideInInspector] GameObject previewObject;
		[SerializeField] [HideInInspector] List<UnityEngine.Material> previewMaterials = new List<UnityEngine.Material>();

#if BVH_ITERATIVE
		public unsafe bool HitWorld(Ray r, out HitRecord hitRec)
		{
			var nodeWA = (BvhNode*) nodeWorkingBuffer.GetUnsafeReadOnlyPtr();
			var entityWA = (Entity*) entityWorkingBuffer.GetUnsafeReadOnlyPtr();

			return World.Hit(r, 0, float.PositiveInfinity, nodeWA, entityWA, out hitRec);
		}
#else
		public bool HitWorld(Ray r, out HitRecord hitRec)
		{
			return World.Hit(r, 0, float.PositiveInfinity, out hitRec);
		}
#endif

		void WatchForWorldChanges()
		{
			// watch for world data changes (won't catch those from OnValidate)
			if (scene && scene.Dirty)
			{
				Transform cameraTransform = targetCamera.transform;
				cameraTransform.position = scene.CameraPosition;
				cameraTransform.rotation = Quaternion.LookRotation(scene.CameraTarget - scene.CameraPosition);

				if (Application.isPlaying)
					worldNeedsRebuild = true;
				else
					EditorApplication.delayCall += UpdatePreview;
			}
		}

		void OnValidate()
		{
			if (Application.isPlaying)
				worldNeedsRebuild = true;
			else if (!EditorApplication.isPlayingOrWillChangePlaymode)
				EditorApplication.delayCall += UpdatePreview;
		}

		void OnEditorUpdate()
		{
			if (!this)
			{
				// ReSharper disable once DelegateSubtraction
				EditorApplication.update -= OnEditorUpdate;
				return;
			}

			if (!EditorApplication.isPlaying)
				WatchForWorldChanges();

#if BVH
			if (bvhNodeBuffer.IsCreated && !EditorApplication.isPlaying &&
			    UnityEditorInternal.InternalEditorUtility.isApplicationActive)
			{
				EditorWindow.GetWindow<SceneView>().Repaint();
			}
#endif
		}

		void UpdatePreview()
		{
			if (!this) return;
#if BVH
			if (previewBvh)
			{
				if (!entityBuffer.IsCreated) RebuildEntityBuffer();				
				if (!bvhNodeBuffer.IsCreated) RebuildBvh();
			}
			else
			{
				if (sphereBuffer.IsCreated) sphereBuffer.Dispose();
				if (bvhNodeBuffer.IsCreated) bvhNodeBuffer.Dispose();
				if (entityBuffer.IsCreated) entityBuffer.Dispose();
#if BVH_ITERATIVE
				if (entityWorkingBuffer.IsCreated) entityWorkingBuffer.Dispose(); 
				if (nodeWorkingBuffer.IsCreated) nodeWorkingBuffer.Dispose();
#endif
				activeSpheres.Clear();
			}
#endif
			if (scene)
			{
				scene.ClearDirty();

				Transform cameraTransform = targetCamera.transform;
				cameraTransform.position = scene.CameraPosition;
				cameraTransform.rotation = Quaternion.LookRotation(scene.CameraTarget - scene.CameraPosition);
			}

			Action updateDelegate = OnEditorUpdate;
			if (EditorApplication.update.GetInvocationList().All(x => x != (Delegate) updateDelegate))
				EditorApplication.update += OnEditorUpdate;

			if (opaquePreviewCommandBuffer == null)
				opaquePreviewCommandBuffer = new CommandBuffer { name = "World Preview (Opaque)" };
			if (transparentPreviewCommandBuffer == null)
				transparentPreviewCommandBuffer = new CommandBuffer { name = "World Preview (Transparent)" };

			targetCamera.RemoveAllCommandBuffers();

			targetCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, opaquePreviewCommandBuffer);
			targetCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, transparentPreviewCommandBuffer);

			opaquePreviewCommandBuffer.Clear();
			transparentPreviewCommandBuffer.Clear();

			foreach (UnityEngine.Material material in previewMaterials)
				DestroyImmediate(material);
			previewMaterials.Clear();

			var skybox = targetCamera.GetComponent<Skybox>();
			skybox.material.SetColor("_Color1", skyBottomColor);
			skybox.material.SetColor("_Color2", skyTopColor);

			if (previewObject == null)
				previewObject = GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Sphere);
			previewObject.hideFlags = HideFlags.HideAndDontSave;
			previewObject.SetActive(false);

			var meshRenderer = previewObject.GetComponent<MeshRenderer>();
			var meshFilter = previewObject.GetComponent<MeshFilter>();

			CollectActiveSpheres();

			opaquePreviewCommandBuffer.EnableShaderKeyword("LIGHTPROBE_SH");
			transparentPreviewCommandBuffer.EnableShaderKeyword("LIGHTPROBE_SH");

			foreach (SphereData sphere in activeSpheres)
			{
				if (!sphere.Material) continue;
				
				bool transparent = sphere.Material.Type == MaterialType.Dielectric;

				Color albedoMainColor = sphere.Material.Albedo ? sphere.Material.Albedo.MainColor : Color.white;
				Color color = transparent ? albedoMainColor.GetAlphaReplaced(0.5f) : albedoMainColor;
				var material = new UnityEngine.Material(meshRenderer.sharedMaterial) { color = color };
				previewMaterials.Add(material);

				material.SetFloat("_Metallic", sphere.Material.Type == MaterialType.Metal ? 1 : 0);
				material.SetFloat("_Glossiness",
					sphere.Material.Type == MaterialType.Metal ? 1 - sphere.Material.Fuzz : transparent ? 1 : 0);

				if (transparent)
				{
					material.SetInt("_SrcBlend", (int) BlendMode.One);
					material.SetInt("_DstBlend", (int) BlendMode.OneMinusSrcAlpha);
					material.SetInt("_ZWrite", 0);
					material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
					material.renderQueue = 3000;
				}

				CommandBuffer previewCommandBuffer =
					transparent ? transparentPreviewCommandBuffer : opaquePreviewCommandBuffer;

				previewCommandBuffer.DrawMesh(meshFilter.sharedMesh,
					Matrix4x4.TRS(sphere.Center, Quaternion.identity, sphere.Radius * 2 * Vector3.one), material, 0,
					material.FindPass("FORWARD"));
			}
		}

		void ForceUpdateInspector()
		{
			EditorUtility.SetDirty(this);
		}

		void OnDrawGizmos()
		{
			var sceneCameraTransform = SceneView.GetAllSceneCameras()[0].transform;
			foreach (SphereData sphere in activeSpheres
				.Where(x => x.Material)
				.OrderBy(x => Vector3.Dot(sceneCameraTransform.position - x.Center, sceneCameraTransform.forward)))
			{
				Color albedo = sphere.Material.Albedo ? sphere.Material.Albedo.MainColor : Color.white;
				Gizmos.color = sphere.Material.Type == MaterialType.Dielectric
					? albedo.GetAlphaReplaced(0.5f)
					: albedo.GetAlphaReplaced(1);

				Gizmos.DrawSphere(sphere.Center, sphere.Radius);
			}

#if BVH
			if (previewBvh && bvhNodeBuffer.IsCreated)
			{
				float silverRatio = (sqrt(5.0f) - 1.0f) / 2.0f;
				(AxisAlignedBoundingBox, int)[] subBounds = World.GetAllSubBounds().ToArray();
				int maxDepth = subBounds.Max(x => x.Item2);
				int shownLayer = DateTime.Now.Second % (maxDepth + 1);
				int i = -1;
				foreach ((var bounds, int depth) in subBounds)
				{
					i++;
					if (depth != shownLayer) continue;

					Gizmos.color = Color.HSVToRGB(frac(i * silverRatio), 1, 1).GetAlphaReplaced(0.6f);
					Gizmos.DrawCube(bounds.Center, bounds.Size);
				}
			}
#endif
		}
	}
}
#endif