// Production terrain and skyline surface. The sun, ambient colours and fog are the same inputs as the harbor sky/water.
Shader "FerrySim/HarborSurface"
{
	Properties
	{
		_BaseColor ("Base Colour", Color) = (1, 1, 1, 1)
		_AmbientStrength ("Estimated Ambient Strength", Range(0, 2)) = 0.65
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
		HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
		#include "../Shaders/FerrySimSky.hlsl"
		CBUFFER_START(UnityPerMaterial)
		half4 _BaseColor;
		half _AmbientStrength;
		CBUFFER_END
		ENDHLSL
		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }
			Cull Back
			HLSLPROGRAM
			#pragma vertex Vertex
			#pragma fragment Fragment
			#pragma multi_compile_fog
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile_fragment _ _SHADOWS_SOFT
			struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; };
			struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; half3 color : COLOR; half fog : TEXCOORD2; };
			Varyings Vertex(Attributes input)
			{
				Varyings output;
				VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
				output.positionCS = position.positionCS;
				output.positionWS = position.positionWS;
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.color = input.color.rgb * _BaseColor.rgb;
				output.fog = ComputeFogFactor(position.positionCS.z);
				return output;
			}
			half4 Fragment(Varyings input) : SV_Target
			{
				half3 normal = normalize(input.normalWS);
				Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
				half3 ambient = normal.y >= 0
					? lerp(_FerrySimSkyHorizon.rgb, _FerrySimSkyZenith.rgb, normal.y)
					: lerp(_FerrySimSkyHorizon.rgb, _FerrySimSkyGround.rgb, -normal.y);
				half3 lighting = ambient * _AmbientStrength + light.color * saturate(dot(normal, light.direction)) * light.shadowAttenuation;
				return half4(MixFog(input.color * lighting, input.fog), 1);
			}
			ENDHLSL
		}
		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode" = "DepthOnly" }
			ColorMask R
			Cull Back
			HLSLPROGRAM
			#pragma vertex Vertex
			#pragma fragment Fragment
			float4 Vertex(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
			half Fragment() : SV_Target { return 0; }
			ENDHLSL
		}
	}
}
