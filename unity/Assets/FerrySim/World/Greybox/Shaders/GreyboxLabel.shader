// Builder annotations use the existing sky multiplier, without changing daytime colour or fogging the text.
Shader "FerrySim/GreyboxLabel"
{
	Properties
	{
		_MainTex ("Font atlas", 2D) = "white" {}
		_Color ("Colour", Color) = (1,1,1,1)
		[Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
	}
	SubShader
	{
		Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
		Pass
		{
			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite On
			ZTest LEqual
			Cull [_Cull]
			HLSLPROGRAM
			#pragma vertex Vertex
			#pragma fragment Fragment
			#pragma multi_compile _ FERRYSIM_CHART_LIGHTING
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
			float4 _FerrySimChartLight;
			CBUFFER_START(UnityPerMaterial)
				half4 _Color;
			CBUFFER_END
			struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
			struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 color : COLOR; };
			Varyings Vertex(Attributes i)
			{
				Varyings o;
				o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
				o.uv = i.uv;
				o.color = i.color;
				return o;
			}
			half4 Fragment(Varyings i) : SV_Target
			{
				half4 color = i.color * _Color;
				color.a *= SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a;
				// Only glyph coverage writes depth; transparent atlas space must not hide another light.
				if (color.a == 0) discard;
#if defined(FERRYSIM_CHART_LIGHTING)
				color.rgb *= _FerrySimChartLight.x;
#endif
				return color;
			}
			ENDHLSL
		}
	}
}
