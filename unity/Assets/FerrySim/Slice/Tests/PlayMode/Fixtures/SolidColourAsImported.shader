Shader "FerrySim/Tests/SolidColourAsImported"
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
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			CBUFFER_START(UnityPerMaterial)
				half4 _BaseColor;
			CBUFFER_END
			float4 vert(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
			half4 frag() : SV_Target { return _BaseColor; }
			ENDHLSL
		}
	}
}
