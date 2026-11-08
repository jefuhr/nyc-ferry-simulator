Shader "FerrySim/LandingSurface"
{
	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
		Pass
		{
			Name "LandingForward"
			Tags { "LightMode"="UniversalForward" }
			Cull Off
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma multi_compile_fog
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; half4 color:COLOR; };
			struct Varyings { float4 positionCS:SV_POSITION; half3 normalWS:TEXCOORD0; half4 color:COLOR; half fog:TEXCOORD1; };
			Varyings Vert(Attributes i)
			{
				Varyings o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz);
				o.normalWS=TransformObjectToWorldNormal(i.normalOS); o.color=i.color;
				o.fog=ComputeFogFactor(o.positionCS.z); return o;
			}
			half4 Frag(Varyings i, FRONT_FACE_TYPE frontFace:FRONT_FACE_SEMANTIC):SV_Target
			{
				half3 n=normalize(i.normalWS)*IS_FRONT_VFACE(frontFace,1,-1);
				Light sun=GetMainLight();
				half3 illumination=max(SampleSH(n),half3(.025,.03,.04))+sun.color*saturate(dot(n,sun.direction));
				// Alpha is an authored lamp strength, never transparency. No per-landing lights or shadows.
				half3 color=i.color.rgb*illumination+i.color.rgb*i.color.a;
				return half4(MixFog(color,i.fog),1);
			}
			ENDHLSL
		}
	}
}
