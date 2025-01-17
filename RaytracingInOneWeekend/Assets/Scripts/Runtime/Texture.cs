using System;
using JetBrains.Annotations;
using Unity.Burst.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Util;
using static Unity.Mathematics.math;

namespace Runtime
{
	enum TextureType
	{
		None,
		Constant,
		CheckerPattern,
		PerlinNoise,
		Image,
		ConstantScalar
	}

	readonly unsafe struct Texture
	{
		public readonly TextureType Type;
		public readonly float3 MainColor;
		public readonly float3 SecondaryColor;
		public readonly float Parameter;
		public readonly int2 ImageSize;
		public readonly byte* ImagePointer;
		public readonly int PixelStride;
		public readonly int ScalarValueChannel;

		float NoiseFrequency => Parameter;
		float ConstantValue => Parameter;

		public Texture(TextureType type, float3 mainColor,
			float3 secondaryColor = default, float parameter = default, byte* pImage = default, int imageWidth = default, int imageHeight = default, int pixelStride = 3, int scalarValueChannel = 0)
		{
			Type = type;
			MainColor = mainColor;
			SecondaryColor = secondaryColor;
			Parameter = parameter;
			ImagePointer = pImage;
			ImageSize = int2(imageWidth, imageHeight);
			PixelStride = pixelStride;
			ScalarValueChannel = scalarValueChannel;
		}

		[Pure]
		public float3 SampleColor(float2 textureCoordinates, PerlinNoise perlinNoise)
		{
			switch (Type)
			{
				case TextureType.Constant:
					return MainColor;

				case TextureType.ConstantScalar:
					return ConstantValue;

				// case TextureType.CheckerPattern:
				// {
				// 	// from iq : https://www.shadertoy.com/view/ltl3D8
				// 	float3 n = abs(normal);
				// 	float3 v = n.x > n.y && n.x > n.z ? normal.xyz :
				// 		n.y > n.x && n.y > n.z ? normal.yzx :
				// 		normal.zxy;
				// 	float2 q = v.yz / v.x;
				// 	float2 uv = 0.5f + 0.5f * q;
				//
				// 	float2 sines = sin(PI * scale * uv);
				// 	return sines.x * sines.y < 0 ? MainColor : SecondaryColor;
				// }
				//
				// case TextureType.PerlinNoise:
				// 	return 0.5f * (1 + sin(NoiseFrequency * position.z +
				// 	                       10 * perlinNoise.Turbulence(position))) *
				// 	       MainColor;

				case TextureType.Image:
				{
					if (ImagePointer == null)
						return 0;

					int2 coords = (int2) (textureCoordinates * ImageSize);

					byte* pPixelData = ImagePointer + (coords.y * ImageSize.x + coords.x) * PixelStride;
					return float3(pPixelData[0], pPixelData[1], pPixelData[2]) / 255 * MainColor;
				}
			}

			return default;
		}

		[Pure]
		public float SampleScalar(float2 textureCoordinates, PerlinNoise perlinNoise)
		{
			switch (Type)
			{
				case TextureType.Constant:
					return MainColor[ScalarValueChannel];

				case TextureType.ConstantScalar:
					return ConstantValue;

				// case TextureType.CheckerPattern:
				// {
				// 	// from iq : https://www.shadertoy.com/view/ltl3D8
				// 	float3 n = abs(normal);
				// 	float3 v = n.x > n.y && n.x > n.z ? normal.xyz :
				// 		n.y > n.x && n.y > n.z ? normal.yzx :
				// 		normal.zxy;
				// 	float2 q = v.yz / v.x;
				// 	float2 uv = 0.5f + 0.5f * q;
				//
				// 	float2 sines = sin(PI * scale * uv);
				// 	return sines.x * sines.y < 0 ? MainColor : SecondaryColor;
				// }
				//
				// case TextureType.PerlinNoise:
				// 	return 0.5f * (1 + sin(NoiseFrequency * position.z +
				// 	                       10 * perlinNoise.Turbulence(position))) *
				// 	       MainColor;

				case TextureType.Image:
				{
					if (ImagePointer == null)
						return 0;

					int2 coords = (int2) (textureCoordinates * ImageSize);

					byte* pPixelData = ImagePointer + (coords.y * ImageSize.x + coords.x) * PixelStride;
					return pPixelData[ScalarValueChannel] / 255.0f * MainColor[ScalarValueChannel];
				}
			}

			return default;
		}
	}

	readonly unsafe struct Cubemap
	{
		readonly int2 halfFaceSize, faceSizeMinusOne, pixelStrideVector;
		readonly ChannelType channelType;
		readonly int faceStride;

		[NativeDisableUnsafePtrRestriction] readonly byte* dataPointer;

		public Cubemap(UnityEngine.Cubemap cubemap)
		{
			int2 faceSize = int2(cubemap.width, cubemap.height);
			halfFaceSize = faceSize / 2;
			faceSizeMinusOne = faceSize - 1;

			int pixelStride;
			switch (cubemap.graphicsFormat)
			{
				case GraphicsFormat.R16G16B16A16_SFloat:
					channelType = ChannelType.SignedHalf;
					pixelStride = 8; // 4 * 16 / 8
					break;

				default: throw new NotSupportedException();
			}

			pixelStrideVector = int2(pixelStride, pixelStride * faceSize.x);
			faceStride = pixelStride * faceSize.x * faceSize.y;
			dataPointer = (byte*) cubemap.GetPixelData<byte>(0, CubemapFace.PositiveX).GetUnsafeReadOnlyPtr();
		}

		[Pure]
		public float3 Sample(float3 vector)
		{
			if (Hint.Unlikely(dataPointer == (byte*) 0))
				return default;

			// indexing math adapted from : https://scalibq.wordpress.com/2013/06/23/cubemaps/
			float4 absVector = float4(abs(vector), 0);
			float maxDistance = cmax(absVector);
			int laneMask = bitmask(maxDistance == absVector);
			int firstLane = tzcnt(laneMask);
			bool positive = vector[firstLane] >= 0;

			float2 uv;
			switch (firstLane)
			{
				case 0: uv = float2(positive ? -vector.z : vector.z, -vector.y); break; // x
				case 1: uv = float2(vector.x, positive ? vector.z : -vector.z); break;  // y
				case 2: uv = float2(positive ? vector.x : -vector.x, -vector.y); break; // z
				default: throw new InvalidOperationException();
			}
			uv /= absVector[firstLane];

			int2 coords = min((int2) ((uv + 1) * halfFaceSize), faceSizeMinusOne);

			byte* pFaceData = dataPointer + (firstLane * 2 + (positive ? 0 : 1)) * faceStride;
			pFaceData += dot(coords, pixelStrideVector);

			switch (channelType)
			{
				case ChannelType.UnsignedByte:
					return float3(pFaceData[0], pFaceData[1], pFaceData[2]) / 255;

				case ChannelType.SignedHalf:
					var pTypedData = (half*) pFaceData;
					return float3(pTypedData[0], pTypedData[1], pTypedData[2]);

				default: throw new NotSupportedException();
			}
		}
	}
}