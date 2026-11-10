Shader "FerrySim/WheelhouseLamps"
{
	SubShader
	{
		Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
		Pass
		{
			Name "Lenses"
			Tags { "LightMode" = "SRPDefaultUnlit" }
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 3.5
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			float _LampLevels[32];
			float4 _LampColours[32];
			struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 lamp : TEXCOORD1; };
			struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; nointerpolation uint lamp : TEXCOORD1; };
			Varyings Vert(Attributes input)
			{
				Varyings output;
				output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
				output.normalWS = TransformObjectToWorldNormal(input.normalOS);
				output.lamp = (uint)clamp(round(input.lamp.x), 0, 31);
				return output;
			}
			half4 Frag(Varyings input) : SV_Target
			{
				half3 lens = _LampColours[input.lamp].rgb;
				half level = saturate(_LampLevels[input.lamp]);
				// A dim, coloured lens stays legible at night. Emission works without bloom or scene lights.
				half facing = .7h + .3h * abs(normalize(input.normalWS).y);
				return half4((lens * .055h + .008h) * facing + lens * level * 2.5h, 1);
			}
			ENDHLSL
		}
	}
}
