// The probe's tile material, after the world-streaming spike's: the tiles' vertex colours (World/Import's depth ramp and
// the structure grey) lit per vertex by the main light and the ambient probe. The probe passes it to Cesium as the
// tileset's opaque material. No shadow caster, so tiles cast no shadows. Harbor-world writes the real terrain shader.
Shader "FerrySim/Tiles3DProbe"
{
	Properties
	{
		_BaseColor ("Base Colour", Color) = (1, 1, 1, 1)
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

		HLSLINCLUDE
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

		CBUFFER_START(UnityPerMaterial)
		half4 _BaseColor;
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

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS : NORMAL;
				half4 color : COLOR;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				half3 color : COLOR;
				half fog : TEXCOORD0;
			};

			Varyings Vertex(Attributes input)
			{
				Varyings output;
				VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
				half3 normal = TransformObjectToWorldNormal(input.normalOS);
				Light light = GetMainLight();
				half3 lighting = light.color * saturate(dot(normal, light.direction)) + SampleSH(normal);
				output.positionCS = position.positionCS;
				output.color = input.color.rgb * _BaseColor.rgb * lighting;
				output.fog = ComputeFogFactor(position.positionCS.z);
				return output;
			}

			half4 Fragment(Varyings input) : SV_Target
			{
				return half4(MixFog(input.color, input.fog), 1.0);
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

			float4 Vertex(float4 positionOS : POSITION) : SV_POSITION
			{
				return TransformObjectToHClip(positionOS.xyz);
			}

			half Fragment() : SV_Target
			{
				return 0;
			}
			ENDHLSL
		}
	}
}
