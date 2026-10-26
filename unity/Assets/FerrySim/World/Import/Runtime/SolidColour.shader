Shader "FerrySim/SolidColour"
{
	Properties { _BaseColor ("Colour", Color) = (1,1,1,1) }
	SubShader
	{
		Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
		Pass
		{
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// World/Atmosphere/README.md: while a run is dark or foggy, FERRYSIM_CHART_LIGHTING dims the colour by the sky's
			// light (not a lens's: _FerrySimEmissive 1, set per renderer in a property block) and draws it through the run's
			// fog. Without it this is the import's shader, unchanged.
			#pragma multi_compile _ FERRYSIM_CHART_LIGHTING
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			CBUFFER_START(UnityPerMaterial)
				half4 _BaseColor;
			CBUFFER_END
#if defined(FERRYSIM_CHART_LIGHTING)
			float4 _FerrySimChartLight; float4 _FerrySimChartFog; half4 _FerrySimChartFogColour; float _FerrySimEmissive;
			struct Varying { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };
			Varying vert(float4 positionOS : POSITION) { Varying o; o.positionWS = TransformObjectToWorld(positionOS.xyz); o.positionCS = TransformObjectToHClip(positionOS.xyz); return o; }
			half4 frag(Varying i) : SV_Target
			{
				half3 lit = _BaseColor.rgb * lerp(_FerrySimChartLight.x, 1.0, _FerrySimEmissive);
				return half4(lerp(_FerrySimChartFogColour.rgb, lit, exp(-_FerrySimChartFog.x * distance(i.positionWS, _WorldSpaceCameraPos.xyz))), _BaseColor.a);
			}
#else
			float4 vert(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
			half4 frag() : SV_Target { return _BaseColor; }
#endif
			ENDHLSL
		}
	}
}
