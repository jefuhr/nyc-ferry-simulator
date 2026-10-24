// Gradient sky for URP, driven by the same colours SunController publishes for the water's
// reflection, so the sky the camera sees and the sky the water reflects match. Zenith-to-horizon
// gradient, a glow around the sun, a warm band along the horizon toward the sun around sunset,
// and the sun's disk. Unity's +y is local up at the camera because Cesium keeps the georeference
// origin there.
Shader "FerrySim/Sky"
{
	Properties
	{
		_SunDiskRadius ("Sun Disk Angular Radius (deg)", Float) = 0.27
		_SunDiskBrightness ("Sun Disk Brightness", Float) = 12
		_SunGlowPower ("Glow Around Sun, Tightness", Float) = 48
		_SunGlowStrength ("Glow Around Sun, Strength", Float) = 0.45
		_HorizonGlowHeight ("Horizon Glow Height (sine of elevation)", Float) = 0.12
	}

	SubShader
	{
		Tags
		{
			"Queue" = "Background"
			"RenderType" = "Background"
			"PreviewType" = "Skybox"
			"RenderPipeline" = "UniversalPipeline"
		}

		Cull Off
		ZWrite Off

		Pass
		{
			Name "Sky"

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "FerrySimSky.hlsl"

			CBUFFER_START(UnityPerMaterial)
				float _SunDiskRadius;
				float _SunDiskBrightness;
				float _SunGlowPower;
				float _SunGlowStrength;
				float _HorizonGlowHeight;
			CBUFFER_END

			struct Attributes
			{
				float4 positionOS : POSITION;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 direction : TEXCOORD0;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.direction = input.positionOS.xyz;
				return output;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				float3 direction = normalize(input.direction);
				half3 sky = FerrySimSkyColor(direction, _SunGlowPower, _SunGlowStrength, _HorizonGlowHeight);

				// The sun's disk, softened over a few hundredths of a degree.
				float3 sun = normalize(_FerrySimSunDirection.xyz + float3(0.0, 1e-6, 0.0));
				float diskCos = cos(radians(_SunDiskRadius));
				float disk = smoothstep(diskCos - 0.00003, diskCos + 0.00003, dot(direction, sun));
				sky += _FerrySimSunColor.rgb * (_SunDiskBrightness * _FerrySimSunParams.x * disk);

				return half4(sky, 1.0);
			}
			ENDHLSL
		}
	}

	FallBack Off
}
