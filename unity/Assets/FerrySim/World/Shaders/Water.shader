// Simple harbor water for URP: depth-tinted colour, normal-mapped ripples, Schlick fresnel against
// the sky model in FerrySimSky.hlsl (driven by SunController), and a sun glint. Transparent, no depth write.
//
// Depth tint reads _CameraDepthTexture, so the camera must request the depth texture and the
// renderer must copy depth after opaques (FerrySim_URP_Renderer: Copy Depth Mode = After Opaques).
// Without it every pixel gets the deep colour, which is the safe failure.
//
// Ripple coordinates come from object space: the water mesh lives in the world frame, so object x/z
// are metres east/north and do not move when Cesium shifts the origin.
Shader "FerrySim/Water"
{
	Properties
	{
		_ShallowColor ("Shallow Colour (alpha = opacity)", Color) = (0.16, 0.36, 0.34, 0.45)
		_DeepColor ("Deep Colour (alpha = opacity)", Color) = (0.02, 0.10, 0.13, 0.96)
		_AbsorptionDistance ("Absorption Distance (m)", Float) = 5.0
		_RippleTileA ("Ripple Tile A (m)", Float) = 11.0
		_RippleTileB ("Ripple Tile B (m)", Float) = 37.0
		_RippleVelocityA ("Ripple Velocity A (east, north m/s)", Vector) = (0.30, 0.12, 0, 0)
		_RippleVelocityB ("Ripple Velocity B (east, north m/s)", Vector) = (-0.14, 0.22, 0, 0)
		_NormalStrength ("Ripple Strength", Range(0, 2)) = 0.55
		_RippleFadeDistance ("Ripple Fade Distance (m)", Float) = 2500
		_FresnelF0 ("Reflectance at Normal Incidence", Range(0, 0.2)) = 0.02
		_SunGlintPower ("Sun Glint Exponent", Float) = 600
		_SunGlintStrength ("Sun Glint Strength", Float) = 3
	}

	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent-100"
			"RenderPipeline" = "UniversalPipeline"
			"IgnoreProjector" = "True"
		}

		Pass
		{
			Name "WaterForward"
			Tags { "LightMode" = "UniversalForward" }

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			ZTest LEqual
			Cull Back

			HLSLPROGRAM
			#pragma target 3.5
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile_fog

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
			#include "FerrySimSky.hlsl"

			CBUFFER_START(UnityPerMaterial)
				half4 _ShallowColor;
				half4 _DeepColor;
				float _AbsorptionDistance;
				float _RippleTileA;
				float _RippleTileB;
				float4 _RippleVelocityA;
				float4 _RippleVelocityB;
				half _NormalStrength;
				float _RippleFadeDistance;
				half _FresnelF0;
				half _SunGlintPower;
				half _SunGlintStrength;
			CBUFFER_END

			// Globals: the generated ripple normal map (WaterNormalMap); the sky comes from FerrySimSky.hlsl.
			TEXTURE2D(_FerrySimWaterNormals);
			SAMPLER(sampler_FerrySimWaterNormals);

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				float3 positionOS : TEXCOORD1;
				half3 normalWS : TEXCOORD2;
				float4 screenPos : TEXCOORD3;
				half fogFactor : TEXCOORD4;
			};

			Varyings Vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = positions.positionCS;
				output.positionWS = positions.positionWS;
				output.positionOS = input.positionOS.xyz;
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.screenPos = ComputeScreenPos(positions.positionCS);
				output.fogFactor = ComputeFogFactor(positions.positionCS.z);
				return output;
			}

			// The generated map stores tangent-space x and y in red and green.
			half3 SampleRipple(float2 uv)
			{
				half4 packed = SAMPLE_TEXTURE2D(_FerrySimWaterNormals, sampler_FerrySimWaterNormals, uv);
				half3 normal;
				normal.xy = packed.rg * 2.0h - 1.0h;
				normal.z = sqrt(saturate(1.0h - dot(normal.xy, normal.xy)));
				return normal;
			}

			half4 Frag(Varyings input) : SV_Target
			{
				float3 toCamera = GetCameraPositionWS() - input.positionWS;
				float viewDistance = length(toCamera);
				half3 viewDir = (half3)(toCamera / max(viewDistance, 1e-4));

				// Two ripple layers scrolling in different directions, whiteout-blended, faded with distance.
				float2 eastNorth = input.positionOS.xz;
				float time = _Time.y;
				half3 rippleA = SampleRipple((eastNorth - _RippleVelocityA.xy * time) / _RippleTileA);
				half3 rippleB = SampleRipple((eastNorth - _RippleVelocityB.xy * time) / _RippleTileB);
				half3 ripple = half3(rippleA.xy + rippleB.xy, rippleA.z * rippleB.z);
				ripple.xy *= _NormalStrength * saturate(1.0 - viewDistance / _RippleFadeDistance);
				ripple = normalize(ripple);

				// Tangent frame from the world frame's axes: x east, z north, y up.
				half3 up = normalize(input.normalWS);
				half3 east = (half3)TransformObjectToWorldDir(float3(1.0, 0.0, 0.0));
				east = normalize(east - up * dot(east, up));
				half3 north = cross(east, up);
				half3 normal = normalize(east * ripple.x + north * ripple.y + up * ripple.z);

				// Depth tint: water thickness along the view ray from the opaque depth behind the surface.
				float2 screenUV = input.screenPos.xy / input.screenPos.w;
				float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
				float thickness = max(sceneEyeDepth - input.screenPos.w, 0.0);
				half absorbed = (half)(1.0 - exp(-thickness / max(_AbsorptionDistance, 1e-3)));
				half3 bodyColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, absorbed);
				half bodyAlpha = lerp(_ShallowColor.a, _DeepColor.a, absorbed);

				Light mainLight = GetMainLight();
				half3 lit = bodyColor * (SampleSH(up) + mainLight.color * (0.5h * saturate(dot(up, mainLight.direction))));

				// Reflected sky (the same model the skybox draws), and Schlick's fresnel approximation.
				// Reflections never point below the horizon: the ripples are small slopes.
				half3 reflected = reflect(-viewDir, normal);
				reflected.y = abs(reflected.y);
				half3 sky = FerrySimSkyColor(reflected, 48.0, 0.45, 0.12);
				half fresnel = _FresnelF0 + (1.0h - _FresnelF0) * pow(1.0h - saturate(dot(normal, viewDir)), 5.0h);

				half3 halfway = normalize(mainLight.direction + viewDir);
				half3 glint = mainLight.color * (_SunGlintStrength * pow(saturate(dot(normal, halfway)), _SunGlintPower) * step(0.0h, dot(up, mainLight.direction)));

				half3 color = lerp(lit, sky, fresnel) + glint;
				half alpha = saturate(lerp(bodyAlpha, 1.0h, fresnel) + dot(glint, half3(0.3333h, 0.3333h, 0.3333h)));
				color = MixFog(color, input.fogFactor);
				return half4(color, alpha);
			}
			ENDHLSL
		}
	}

	FallBack Off
}
