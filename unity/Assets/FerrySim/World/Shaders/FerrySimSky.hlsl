// Sky colour shared by FerrySim/Sky (what the camera sees) and FerrySim/Water (what the water
// reflects). Inputs are globals set by SunController. Directions are Unity world space; +y is up.
#ifndef FERRYSIM_SKY_INCLUDED
#define FERRYSIM_SKY_INCLUDED

half4 _FerrySimSkyZenith;
half4 _FerrySimSkyHorizon;
half4 _FerrySimSkyGround;
float4 _FerrySimSunDirection; // xyz: unit vector toward the sun
half4 _FerrySimSunColor;
half4 _FerrySimSunParams; // x: disk visibility, y: direct-light strength, z: horizon glow strength

// Zenith-to-horizon gradient, a glow around the sun, and a warm band along the horizon toward the
// sun's azimuth around sunset. No sun disk.
half3 FerrySimSkyColor(float3 direction, float glowPower, float glowStrength, float horizonGlowHeight)
{
	float up = direction.y;
	half3 sky = up >= 0.0
		? lerp(_FerrySimSkyHorizon.rgb, _FerrySimSkyZenith.rgb, sqrt(saturate(up)))
		: lerp(_FerrySimSkyHorizon.rgb, _FerrySimSkyGround.rgb, saturate(-up * 8.0));

	float3 sun = normalize(_FerrySimSunDirection.xyz + float3(0.0, 1e-6, 0.0));
	float cosAngle = dot(direction, sun);
	sky += _FerrySimSunColor.rgb * (glowStrength * (0.3 + 0.7 * _FerrySimSunParams.y) * pow(saturate(cosAngle), glowPower));

	float2 flatDirection = normalize(direction.xz + float2(1e-5, 0.0));
	float2 flatSun = normalize(sun.xz + float2(1e-5, 0.0));
	float azimuthal = pow(saturate(0.5 + 0.5 * dot(flatDirection, flatSun)), 4.0);
	float band = exp(-abs(up) / horizonGlowHeight);
	sky += _FerrySimSunColor.rgb * (_FerrySimSunParams.z * azimuthal * band);
	return sky;
}

#endif
