﻿using JetBrains.Annotations;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;
using Random = Unity.Mathematics.Random;

namespace RaytracerInOneWeekend
{
    public class Raytracer : MonoBehaviour
    {
        [SerializeField] UnityEngine.Camera targetCamera = null;

        [SerializeField] [Range(0.01f, 1)] float ResolutionScaling = 1;
        [SerializeField] [Range(1, 1000)] int SamplesPerPixel = 100;
        [SerializeField] [Range(1, 100)] int TraceDepth = 50;

        CommandBuffer commandBuffer;
        Texture2D backBufferTexture;
        NativeArray<half4> backBuffer;
        NativeArray<Sphere> spheres;

        JobHandle raytraceJobHandle;

        int Width => Mathf.RoundToInt(targetCamera.pixelWidth * ResolutionScaling);
        int Height => Mathf.RoundToInt(targetCamera.pixelHeight * ResolutionScaling);

        void OnValidate()
        {
            if (Application.isPlaying)
                RebuildBuffers();
        }

        void RebuildBuffers()
        {
            if (backBufferTexture != null)
                Destroy(backBufferTexture);

            backBufferTexture = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true)
            {
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };

            if (commandBuffer != null)
            {
                targetCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, commandBuffer);
                commandBuffer.Release();
            }

            commandBuffer = new CommandBuffer { name = "Raytracer" };
            commandBuffer.Blit(backBufferTexture, new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
            targetCamera.AddCommandBuffer(CameraEvent.AfterEverything, commandBuffer);

            if (backBuffer.IsCreated)
                backBuffer.Dispose();

            backBuffer = new NativeArray<half4>(Width * Height,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        void Awake()
        {
            RebuildBuffers();

            spheres = new NativeArray<Sphere>(4, Allocator.Persistent)
            {
                [0] = new Sphere(float3(0, 0, -1), 0.5f, Material.Lambertian(float3(0.8f, 0.3f, 0.3f))),
                [1] = new Sphere(float3(0, -100.5f, -1), 100, Material.Lambertian(float3(0.8f, 0.8f, 0.0f))),
                [2] = new Sphere(float3(1, 0, -1), 0.5f, Material.Metal(float3(0.8f, 0.6f, 0.2f), 0.3f)),
                [3] = new Sphere(float3(-1, 0, -1), 0.5f, Material.Metal(float3(0.8f, 0.8f, 0.8f), 1)),
            };
        }

        void OnDestroy()
        {
            if (backBuffer.IsCreated) backBuffer.Dispose();
            if (spheres.IsCreated) spheres.Dispose();
        }

        void Update()
        {
            float aspect = (float) Width / Height;

            var raytracingCamera = new Camera(0,
                float3(-aspect, -1, -1),
                float3(aspect * 2, 0, 0),
                float3(0, 2, 0));

            var raytraceJob = new RaytraceJob
            {
                Size = int2(Width, Height),
                Camera = raytracingCamera,
                Target = backBuffer,
                Spheres = spheres,
                Rng = new Random((uint) Time.frameCount),
                SampleCount = SamplesPerPixel,
                TraceDepth = TraceDepth
            };
            raytraceJobHandle = raytraceJob.Schedule(Width * Height, Width);
        }

        void LateUpdate()
        {
            raytraceJobHandle.Complete();

            backBufferTexture.LoadRawTextureData(backBuffer);
            backBufferTexture.Apply(false);
        }
    }

    [BurstCompile(FloatPrecision.Medium, FloatMode.Fast)]
    struct RaytraceJob : IJobParallelFor
    {
        [ReadOnly] public int2 Size;
        [ReadOnly] public Camera Camera;
        [ReadOnly] public NativeArray<Sphere> Spheres;
        [ReadOnly] public int SampleCount;
        [ReadOnly] public int TraceDepth;
        [ReadOnly] public Random Rng;

        [WriteOnly] public NativeArray<half4> Target;

        float3 Color(Ray r, int depth)
        {
            if (Spheres.Hit(r, 0.001f, float.PositiveInfinity, out HitRecord rec))
            {
                if (depth < TraceDepth && rec.Material.Scatter(r, rec, Rng, out float3 attenuation, out Ray scattered))
                    return attenuation * Color(scattered, depth + 1);
                return 0;
            }

            float3 unitDirection = normalize(r.Direction);
            float t = 0.5f * (unitDirection.y + 1);
            return lerp(1, float3(0.5f, 0.7f, 1), t);
        }

        public void Execute(int index)
        {
            int2 coordinates = int2(
                index % Size.x, // column 
                index / Size.x // row
            );

            float3 color = 0;
            for (int s = 0; s < SampleCount; s++)
            {
                float2 normalizedCoordinates = (coordinates + Rng.NextFloat2()) / Size; // (u, v)
                Ray r = Camera.GetRay(normalizedCoordinates);
                color += Color(r, 0);
            }

            color /= SampleCount;

            color = color.LinearToGamma();

            Target[index] = half4(half3(color), half(1));
        }
    }

    struct Ray
    {
        public readonly float3 Origin;
        public readonly float3 Direction;

        public Ray(float3 origin, float3 direction)
        {
            Origin = origin;
            Direction = direction;
        }

        public float3 GetPoint(float t) => Origin + t * Direction;
    }

    struct HitRecord
    {
        public readonly float Distance;
        public readonly float3 Point;
        public readonly float3 Normal;
        public readonly Material Material;

        public HitRecord(float distance, float3 point, float3 normal, Material material)
        {
            Distance = distance;
            Point = point;
            Normal = normal;
            Material = material;
        }
    }

    struct Sphere
    {
        public readonly float3 Center;
        public readonly float Radius;
        public readonly Material Material;

        public Sphere(float3 center, float radius, Material material)
        {
            Center = center;
            Radius = radius;
            Material = material;
        }

        public bool Hit(Ray r, float tMin, float tMax, out HitRecord rec)
        {
            float3 oc = r.Origin - Center;
            float a = dot(r.Direction, r.Direction);
            float b = dot(oc, r.Direction);
            float c = dot(oc, oc) - Radius * Radius;
            float discriminant = b * b - a * c;

            if (discriminant > 0)
            {
                float sqrtDiscriminant = sqrt(discriminant);
                float t = (-b - sqrtDiscriminant) / a;
                if (t < tMax && t > tMin)
                {
                    float3 point = r.GetPoint(t);
                    rec = new HitRecord(t, point, (point - Center) / Radius, Material);
                    return true;
                }

                t = (-b + sqrtDiscriminant) / a;
                if (t < tMax && t > tMin)
                {
                    float3 point = r.GetPoint(t);
                    rec = new HitRecord(t, point, (point - Center) / Radius, Material);
                    return true;
                }
            }

            rec = default;
            return false;
        }
    }

    struct Camera
    {
        public readonly float3 Origin;
        public readonly float3 LowerLeftCorner;
        public readonly float3 Horizontal;
        public readonly float3 Vertical;

        public Camera(float3 origin, float3 lowerLeftCorner, float3 horizontal, float3 vertical)
        {
            Origin = origin;
            LowerLeftCorner = lowerLeftCorner;
            Horizontal = horizontal;
            Vertical = vertical;
        }

        public Ray GetRay(float2 normalizedCoordinates) => new Ray(Origin,
            LowerLeftCorner + normalizedCoordinates.x * Horizontal + normalizedCoordinates.y * Vertical - Origin);
    }

    struct Material
    {
        public readonly MaterialType Type;
        public readonly float3 Albedo;
        public readonly float Fuzz;

        private Material(MaterialType type, float3 albedo, float fuzz = 0)
        {
            Type = type;
            Albedo = albedo;
            Fuzz = saturate(fuzz);
        }

        public static Material Lambertian(float3 albedo) => new Material(MaterialType.Lambertian, albedo);
        public static Material Metal(float3 albedo, float fuzz) => new Material(MaterialType.Metal, albedo, fuzz);

        [Pure]
        public bool Scatter(Ray r, HitRecord rec, Random rng, out float3 attenuation, out Ray scattered)
        {
            switch (Type)
            {
                case MaterialType.Lambertian:
                    float3 target = rec.Point + rec.Normal + rng.InUnitSphere();
                    scattered = new Ray(rec.Point, target - rec.Point);
                    attenuation = Albedo;
                    return true;

                case MaterialType.Metal:
                    float3 reflected = reflect(normalize(r.Direction), rec.Normal);
                    scattered = new Ray(rec.Point, reflected + Fuzz * rng.InUnitSphere());
                    attenuation = Albedo;
                    return dot(scattered.Direction, rec.Normal) > 0;
            }

            attenuation = default;
            scattered = default;
            return false;
        }
    }

    public enum MaterialType
    {
        Lambertian,
        Metal
    }

    static class Extensions
    {
        public static bool Hit(this NativeArray<Sphere> spheres, Ray r, float tMin, float tMax, out HitRecord rec)
        {
            bool hitAnything = false;
            rec = new HitRecord(tMax, 0, 0, default);

            for (var i = 0; i < spheres.Length; i++)
            {
                Sphere sphere = spheres[i];
                if (sphere.Hit(r, tMin, tMax, out HitRecord thisRec) && thisRec.Distance < rec.Distance)
                {
                    hitAnything = true;
                    rec = thisRec;
                }
            }

            return hitAnything;
        }

        public static float3 InUnitSphere(this Random rng)
        {
            // TODO: is this really as fast it gets?
            float3 p;
            do
            {
                p = 2 * rng.NextFloat3() - 1;
            } while (lengthsq(p) >= 1);

            return p;
        }

        public static float3 LinearToGamma(this float3 value)
        {
            value = max(value, 0);
            return max(1.055f * pow(value, 0.416666667f) - 0.055f, 0);
        }
    }
}