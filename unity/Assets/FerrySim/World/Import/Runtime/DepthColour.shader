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
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			struct Input { float4 positionOS : POSITION; half4 color : COLOR; };
			struct Varying { float4 positionCS : SV_POSITION; half4 color : COLOR; };
			Varying vert(Input i) { Varying o; o.positionCS = TransformObjectToHClip(i.positionOS.xyz); o.color = i.color; return o; }
			half4 frag(Varying i) : SV_Target { return i.color; }
			ENDHLSL
		}
	}
}
