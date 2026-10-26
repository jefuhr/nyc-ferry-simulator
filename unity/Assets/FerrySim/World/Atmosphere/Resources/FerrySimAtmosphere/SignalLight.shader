// A signal light seen as a point: a camera-facing sprite of a few pixels, drawn only while Allard's law says the eye
// sees it (World/Atmosphere/README.md). Everything is worked out per vertex from the light's own origin and axes:
//  - its sector: the eye's bearing in the light's frame, clockwise from its forward (+z) seen from above, against
//    _Sector (start and end bearings, degrees; z = 1 for an all-round light) and the edge profiles of 33 CFR 84.15
//    (_StartEdge, _EndEdge: full intensity this far inside, the factor at the limit, cut-off this far outside);
//  - its illuminance at the eye, E = I exp(-sigma d) / d^2, against the threshold, with sigma and the threshold from
//    the global _FerrySimLightOptics (x: extinction, 1/m; y: threshold, lux).
// Unlit, additive, not fogged (the fog is already in sigma), depth-tested so structures hide it. The sprite's centre is
// moved up to half a metre toward the eye, so its own mounting does not hide it.
Shader "FerrySim/SignalLight"
{
	Properties
	{
		_Color ("Colour (linear)", Color) = (1, 1, 1, 1)
		_Intensity ("Luminous intensity, cd", Float) = 1
		_Sector ("Sector: start, end (deg), all-round (1)", Vector) = (0, 0, 1, 0)
		_StartEdge ("Start edge: full inside, at limit, cut-off outside", Vector) = (0, 1, 0, 0)
		_EndEdge ("End edge: full inside, at limit, cut-off outside", Vector) = (0, 1, 0, 0)
	}

	SubShader
	{
		Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

		Pass
		{
			Name "SignalLight"
			Blend One One
			ZWrite Off
			ZTest LEqual
			Cull Off

			HLSLPROGRAM
			#pragma vertex Vertex
			#pragma fragment Fragment

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			CBUFFER_START(UnityPerMaterial)
				float4 _Color;
				float _Intensity;
				float4 _Sector;
				float4 _StartEdge;
				float4 _EndEdge;
			CBUFFER_END

			float4 _FerrySimLightOptics;

			// Smallest and largest sprite, pixels: a light at its threshold, and one a thousand times above it.
			static const float MinimumPixels = 2.0;
			static const float MaximumPixels = 6.0;

			struct Attributes
			{
				float4 positionOS : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 color : TEXCOORD1;
			};

			float EdgeInside(float4 edge, float degreesIn)
			{
				return degreesIn >= edge.x ? 1.0 : edge.y + (1.0 - edge.y) * degreesIn / max(edge.x, 1e-6);
			}

			float EdgeOutside(float4 edge, float degreesOut)
			{
				return degreesOut >= edge.z ? 0.0 : edge.y * (1.0 - degreesOut / max(edge.z, 1e-6));
			}

			float Wrap360(float angle)
			{
				float wrapped = fmod(angle, 360.0);
				return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
			}

			// LightSector.Factor in World/Atmosphere/NavigationLightRules.cs, line for line.
			float SectorFactor(float bearing)
			{
				if (_Sector.z > 0.5)
				{
					return 1.0;
				}

				float arc = Wrap360(_Sector.y - _Sector.x);
				float into = Wrap360(bearing - _Sector.x);
				if (into <= arc)
				{
					return min(EdgeInside(_StartEdge, into), EdgeInside(_EndEdge, arc - into));
				}

				float pastEnd = into - arc;
				float beforeStart = 360.0 - into;
				return pastEnd <= beforeStart ? EdgeOutside(_EndEdge, pastEnd) : EdgeOutside(_StartEdge, beforeStart);
			}

			Varyings Vertex(Attributes input)
			{
				Varyings output;
				float3 centre = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
				float3 toEye = _WorldSpaceCameraPos.xyz - centre;
				float eyeDistance = max(length(toEye), 0.01);

				float3 local = mul((float3x3)GetWorldToObjectMatrix(), toEye);
				float bearing = Wrap360(degrees(atan2(local.x, local.z)));
				float sector = SectorFactor(bearing);

				// log10(E / threshold), with E from Allard's law.
				float aboveThreshold = (log(max(_Intensity * sector, 1e-30)) - _FerrySimLightOptics.x * eyeDistance - 2.0 * log(eyeDistance) - log(max(_FerrySimLightOptics.y, 1e-30))) / log(10.0);
				float seen = aboveThreshold >= 0.0 ? 1.0 : 0.0;
				float brightness = seen * saturate(0.25 + 0.375 * aboveThreshold);
				float pixels = seen * lerp(MinimumPixels, MaximumPixels, saturate(aboveThreshold / 3.0));

				float4 clip = TransformWorldToHClip(centre + toEye / eyeDistance * min(0.5, 0.5 * eyeDistance));
				clip.xy += input.uv * pixels * 2.0 / _ScreenParams.xy * clip.w;
				output.positionCS = clip;
				output.uv = input.uv;
				output.color = _Color.rgb * brightness;
				return output;
			}

			half4 Fragment(Varyings input) : SV_Target
			{
				float falloff = exp(-3.0 * dot(input.uv, input.uv));
				return half4(input.color * falloff, 0.0);
			}
			ENDHLSL
		}
	}
}
