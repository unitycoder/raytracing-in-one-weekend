using System;
using JetBrains.Annotations;
using Runtime.EntityTypes;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using static Unity.Mathematics.math;
using Rect = Runtime.EntityTypes.Rect;

namespace Runtime
{
	enum EntityType
	{
		None,
		Sphere,
		Rect,
		Box,
		Triangle
	}

	static class EntityTypeExtensions
	{
		public static bool IsConvexHull(this EntityType type) => type == EntityType.Box || type == EntityType.Sphere;
	}

	readonly unsafe struct Entity
	{
		public readonly EntityType Type;
		public readonly bool Moving;
		public readonly RigidTransform OriginTransform;
		public readonly RigidTransform InverseTransform;
		public readonly float3 DestinationOffset;
		public readonly float2 TimeRange;
		public readonly Material* Material;

		[NativeDisableUnsafePtrRestriction] public readonly void* Content;

		public Entity(EntityType type, void* contentPointer, RigidTransform originTransform, Material* material,
			bool moving = false, float3 destinationOffset = default, float2 timeRange = default) : this()
		{
			Type = type;
			Content = contentPointer;
			Moving = moving;
			TimeRange = timeRange;
			OriginTransform = originTransform;
			DestinationOffset = destinationOffset;
			TimeRange = timeRange;
			Material = material;

			if (!moving)
				InverseTransform = inverse(OriginTransform);
			else
				Assert.AreNotEqual(timeRange.x, timeRange.y, "Time range cannot be empty for moving entities.");
		}

		[Pure]
		public bool Hit(Ray ray, float tMin, float tMax, out HitRecord rec)
		{
			if (HitInternal(ray, tMin, tMax,
				out float distance, out float3 entityLocalNormal, out float2 texCoord,
				out RigidTransform transformAtTime, out _))
			{
				// TODO: normal is disregarded for isotropic materials
				rec = new HitRecord(distance, ray.GetPoint(distance), normalize(rotate(transformAtTime, entityLocalNormal)), texCoord);

				return true;
			}

			rec = default;
			return false;
		}

		bool HitInternal(Ray ray, float tMin, float tMax,
			out float distance, out float3 entitySpaceNormal, out float2 texCoord,
			out RigidTransform transformAtTime, out Ray entitySpaceRay)
		{
			RigidTransform inverseTransform;

			if (!Moving)
			{
				transformAtTime = OriginTransform;
				inverseTransform = InverseTransform;
			}
			else
			{
				transformAtTime = TransformAtTime(ray.Time);
				inverseTransform = inverse(transformAtTime);
			}

			// Triangles are always world-space
			if (Type == EntityType.Triangle)
				entitySpaceRay = ray;
			else
				entitySpaceRay = new Ray(
					transform(inverseTransform, ray.Origin),
					rotate(inverseTransform, ray.Direction));

			if (!HitContent(entitySpaceRay, tMin, tMax, out distance, out entitySpaceNormal, out texCoord))
				return false;

			return true;
		}

		bool HitContent(Ray r, float tMin, float tMax, out float distance, out float3 normal, out float2 texCoord)
		{
			// TODO: Texcoord support for primitives
			texCoord = 0;

			switch (Type)
			{
				case EntityType.Sphere: return ((Sphere*) Content)->Hit(r, tMin, tMax, out distance, out normal);
				case EntityType.Rect: return ((Rect*) Content)->Hit(r, tMin, tMax, out distance, out normal);
				case EntityType.Box: return ((Box*) Content)->Hit(r, tMin, tMax, out distance, out normal);
				case EntityType.Triangle: return ((Triangle*) Content)->Hit(r, tMin, tMax, out distance, out normal, out texCoord);

				default:
					distance = 0;
					normal = default;
					return false;
			}
		}

		public RigidTransform TransformAtTime(float t) =>
			new(OriginTransform.rot,
				OriginTransform.pos +
				DestinationOffset * clamp(unlerp(TimeRange.x, TimeRange.y, t), 0.0f, 1.0f));
	}
}