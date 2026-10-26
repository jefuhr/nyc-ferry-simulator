using System;

namespace FerrySim.World
{
	/// <summary>
	/// How far a signal light is seen: Allard's law, the illuminance a point source of intensity I gives an eye d metres
	/// away through air of extinction coefficient σ, E = I·exp(−σd)/d², against a threshold of illuminance. Annex I's
	/// formula for the minimum intensity of a light (33 CFR 84.14(a)), I = 3.43×10⁶·T·D²·K^−D, is the same law solved for
	/// I at the light's range D (nautical miles) with the threshold T = 2×10⁻⁷ lux and the transmissivity K = 0.8 per
	/// nautical mile; so in that clear air a light is seen exactly at its Rule 22 range, and in fog its range shrinks.
	/// </summary>
	public static class SignalLightOptics
	{
		/// <summary>Annex I's threshold factor T (84.14(a)): the night illuminance at which a light is just seen, lux.</summary>
		public const double NightThresholdLux = 2e-7;

		/// <summary>
		/// The threshold against a daylight sky, lux: an estimate. The Rules give only the night figure; this puts a 5 nm
		/// masthead light's daylight range in clear air at about 230 m.
		/// </summary>
		public const double DayThresholdLux = 1e-3;

		/// <summary>The sun's altitude at and below which the night threshold applies, degrees (estimate: well into nautical twilight).</summary>
		public const double NightThresholdBelowDegrees = -8.0;

		/// <summary>The sun's altitude at and above which the daylight threshold applies, degrees (estimate).</summary>
		public const double DayThresholdAboveDegrees = 2.0;

		/// <summary>Annex I's transmissivity K for prescribed lights (84.14(a)), per nautical mile.</summary>
		public const double AnnexTransmissivityPerNauticalMile = 0.8;

		/// <summary>84.14(a)'s constant, 3.43×10⁶: square metres per square nautical mile, rounded.</summary>
		public const double AnnexConstant = 3.43e6;

		/// <summary>Annex I's clear air as an extinction coefficient: −ln(0.8) per nautical mile, per metre.</summary>
		public static readonly double ClearAirExtinction = -Math.Log(AnnexTransmissivityPerNauticalMile) / NavigationLightRules.MetresPerNauticalMile;

		/// <summary>Annex I's minimum intensity for a light seen at <paramref name="rangeMetres"/> in its clear air (84.14(a)), candelas.</summary>
		public static double IntensityForRange(double rangeMetres)
		{
			var miles = rangeMetres / NavigationLightRules.MetresPerNauticalMile;
			return AnnexConstant * NightThresholdLux * miles * miles * Math.Pow(AnnexTransmissivityPerNauticalMile, -miles);
		}

		/// <summary>Allard's law: the illuminance at the eye from <paramref name="intensity"/> candelas <paramref name="distanceMetres"/> away, lux.</summary>
		public static double Illuminance(double intensity, double distanceMetres, double extinction) =>
			intensity * Math.Exp(-extinction * distanceMetres) / (distanceMetres * distanceMetres);

		/// <summary>The extinction for lights: the scenario's visibility when it is limited, else Annex I's clear air.</summary>
		public static double LightExtinction(double visibilityMetres) =>
			MeteorologicalVisibility.IsLimited(visibilityMetres) ? MeteorologicalVisibility.ExtinctionCoefficient(visibilityMetres) : ClearAirExtinction;

		/// <summary>
		/// The luminous range, metres: the distance at which Allard's illuminance falls to <paramref name="thresholdLux"/>.
		/// The illuminance falls monotonically with distance, so the root is unique; it is found by bisection on ln d to full
		/// precision, deterministically. Zero for a light of no intensity.
		/// </summary>
		public static double LuminousRange(double intensity, double extinction, double thresholdLux)
		{
			if (!(intensity > 0.0) || !(thresholdLux > 0.0))
			{
				return 0.0;
			}

			var logRatio = Math.Log(intensity / thresholdLux);
			if (!(extinction > 0.0))
			{
				return Math.Exp(0.5 * logRatio);
			}

			// g(u) = ln(I/E) − σ·e^u − 2u, with d = e^u: positive near the light, zero at the range, falling throughout.
			// At the inverse-square range g = −σ·d ≤ 0; fifty natural logs nearer it is about +100.
			var high = 0.5 * logRatio;
			var low = high - 50.0;
			for (var i = 0; i < 100; i++)
			{
				var middle = 0.5 * (low + high);
				if (logRatio - extinction * Math.Exp(middle) - 2.0 * middle > 0.0)
				{
					low = middle;
				}
				else
				{
					high = middle;
				}
			}

			return Math.Exp(0.5 * (low + high));
		}

		/// <summary>
		/// The threshold for a sky with the sun at <paramref name="sunElevationDegrees"/>: Annex I's night figure below
		/// <see cref="NightThresholdBelowDegrees"/>, <see cref="DayThresholdLux"/> above <see cref="DayThresholdAboveDegrees"/>,
		/// and log-linear between (an estimate of how the background brightens through twilight).
		/// </summary>
		public static double ThresholdLux(double sunElevationDegrees)
		{
			if (!(sunElevationDegrees > NightThresholdBelowDegrees))
			{
				return NightThresholdLux;
			}

			if (sunElevationDegrees >= DayThresholdAboveDegrees)
			{
				return DayThresholdLux;
			}

			var t = (sunElevationDegrees - NightThresholdBelowDegrees) / (DayThresholdAboveDegrees - NightThresholdBelowDegrees);
			return Math.Exp(Math.Log(NightThresholdLux) + t * (Math.Log(DayThresholdLux) - Math.Log(NightThresholdLux)));
		}
	}

	/// <summary>
	/// The scenario's visibility as the meteorological optical range (MOR): the length of path in the atmosphere that
	/// leaves 5% of a collimated beam's luminous flux (WMO, Guide to Instruments and Methods of Observation, the CIMO guide).
	/// By Koschmieder's law an object's contrast against the horizon falls as exp(−σd), so the extinction coefficient is
	/// σ = −ln(0.05)/V ≈ 3/V, and that is the density of Unity's exponential fog, whose factor is exactly exp(−density·d).
	/// </summary>
	public static class MeteorologicalVisibility
	{
		/// <summary>The scenario's default: no fog. Any value that is not a positive finite distance is unlimited.</summary>
		public const double Unlimited = 0.0;

		/// <summary>The fraction of a beam left over the MOR, 5%.</summary>
		public const double ContrastThreshold = 0.05;

		/// <summary>
		/// Restricted visibility for the fog signal, for lights by day and for bridge lights: under one nautical mile. Rule 3(l)
		/// gives no figure; the one figure in these regulations is the bridge lights' "visibility is less than one mile"
		/// (33 CFR 118.55(a)). A crew question.
		/// </summary>
		public const double RestrictedBelowMetres = NavigationLightRules.MetresPerNauticalMile;

		/// <summary>True for a positive finite visibility, metres.</summary>
		public static bool IsLimited(double visibilityMetres) => visibilityMetres > 0.0 && !double.IsInfinity(visibilityMetres);

		/// <summary>True for a limited visibility under <see cref="RestrictedBelowMetres"/>.</summary>
		public static bool IsRestricted(double visibilityMetres) => IsLimited(visibilityMetres) && visibilityMetres < RestrictedBelowMetres;

		/// <summary>σ = −ln(0.05)/V, per metre, for a limited visibility V in metres.</summary>
		public static double ExtinctionCoefficient(double visibilityMetres)
		{
			if (!IsLimited(visibilityMetres))
			{
				throw new ArgumentOutOfRangeException(nameof(visibilityMetres), visibilityMetres, "An extinction coefficient needs a limited visibility (a positive finite distance).");
			}

			return -Math.Log(ContrastThreshold) / visibilityMetres;
		}

		/// <summary>The MOR, metres, for an extinction coefficient per metre.</summary>
		public static double FromExtinction(double extinction) => -Math.Log(ContrastThreshold) / extinction;
	}
}
