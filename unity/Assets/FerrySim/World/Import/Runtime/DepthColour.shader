Shader "FerrySim/DepthColour"
{
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
		Pass
		{
			Cull Off
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// World/Atmosphere/README.md: while a run is dark or foggy, FERRYSIM_CHART_LIGHTING dims the chart colours by the
			// sky's light and draws them through the run's fog. Without it this is the import's shader, unchanged.
			#pragma multi_compile _ FERRYSIM_CHART_LIGHTING
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			struct Input { float4 positionOS : POSITION; half4 color : COLOR; };
#if defined(FERRYSIM_CHART_LIGHTING)
			float4 _FerrySimChartLight; float4 _FerrySimChartFog; half4 _FerrySimChartFogColour;
			struct Varying { float4 positionCS : SV_POSITION; half4 color : COLOR; float3 positionWS : TEXCOORD0; };
			Varying vert(Input i) { Varying o; o.positionWS = TransformObjectToWorld(i.positionOS.xyz); o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
			half4 frag(Varying i) : SV_Target
			{
				half3 lit = i.color.rgb * _FerrySimChartLight.x;
				return half4(lerp(_FerrySimChartFogColour.rgb, lit, exp(-_FerrySimChartFog.x * distance(i.positionWS, _WorldSpaceCameraPos.xyz))), i.color.a);
			}
#else
			struct Varying { float4 positionCS : SV_POSITION; half4 color : COLOR; };
			Varying vert(Input i) { Varying o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
			half4 frag(Varying i) : SV_Target { return i.color; }
#endif
			ENDHLSL
		}
	}
}
