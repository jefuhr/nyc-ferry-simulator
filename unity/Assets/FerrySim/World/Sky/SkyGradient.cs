using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Hand-tuned sky and light colours keyed by the sun's apparent elevation, interpolated linearly
	/// between keys. The values are an artistic approximation for greybox, not a sky model: night below
	/// -18 deg (astronomical twilight), blue hour around -4 deg (civil twilight), a glow along the
	/// horizon toward the sun around sunset, and a hazy summer sky above 35 deg. The horizon colour is
	/// also the fog colour, so the fogged far water meets the sky without a seam. Colours are sRGB, as
	/// the inspector shows them.
	/// </summary>
	public static class SkyGradient
	{
		private static readonly Key[] Keys =
		{
			new Key(-18f, new Color(0.020f, 0.025f, 0.050f), new Color(0.050f, 0.050f, 0.070f), new Color(0.020f, 0.021f, 0.026f), new Color(1.00f, 0.45f, 0.25f), 0f, 0f),
			new Key(-10f, new Color(0.035f, 0.050f, 0.110f), new Color(0.090f, 0.090f, 0.130f), new Color(0.028f, 0.028f, 0.038f), new Color(1.00f, 0.45f, 0.25f), 0f, 0.15f),
			new Key(-4f, new Color(0.050f, 0.080f, 0.190f), new Color(0.180f, 0.160f, 0.230f), new Color(0.040f, 0.040f, 0.050f), new Color(1.00f, 0.45f, 0.22f), 0f, 0.5f),
			new Key(-1f, new Color(0.100f, 0.150f, 0.320f), new Color(0.360f, 0.310f, 0.360f), new Color(0.075f, 0.068f, 0.072f), new Color(1.00f, 0.50f, 0.25f), 0f, 0.8f),
			new Key(3f, new Color(0.220f, 0.350f, 0.600f), new Color(0.620f, 0.600f, 0.620f), new Color(0.150f, 0.135f, 0.125f), new Color(1.00f, 0.66f, 0.42f), 0.35f, 0.7f),
			new Key(12f, new Color(0.280f, 0.480f, 0.780f), new Color(0.700f, 0.770f, 0.840f), new Color(0.230f, 0.220f, 0.200f), new Color(1.00f, 0.88f, 0.75f), 0.8f, 0.2f),
			new Key(35f, new Color(0.250f, 0.480f, 0.840f), new Color(0.700f, 0.800f, 0.900f), new Color(0.270f, 0.260f, 0.240f), new Color(1.00f, 0.96f, 0.92f), 1f, 0f),
		};

		/// <summary>Colours and strengths for a sun at <paramref name="apparentElevationDegrees"/>.</summary>
		public static Sample Evaluate(double apparentElevationDegrees)
		{
			var elevation = (float)apparentElevationDegrees;

			// The disk fades as the upper limb sets (the sun's angular radius is about 0.27 deg).
			var disk = Mathf.Clamp01((elevation + 0.27f) / 0.54f);
			if (elevation <= Keys[0].Elevation)
			{
				return Keys[0].ToSample(disk);
			}

			for (var i = 1; i < Keys.Length; i++)
			{
				if (elevation <= Keys[i].Elevation)
				{
					var a = Keys[i - 1];
					var b = Keys[i];
					var t = (elevation - a.Elevation) / (b.Elevation - a.Elevation);
					return new Sample(
						Color.Lerp(a.Zenith, b.Zenith, t),
						Color.Lerp(a.Horizon, b.Horizon, t),
						Color.Lerp(a.Ground, b.Ground, t),
						Color.Lerp(a.SunColor, b.SunColor, t),
						Mathf.Lerp(a.SunStrength, b.SunStrength, t),
						Mathf.Lerp(a.HorizonGlow, b.HorizonGlow, t),
						disk);
				}
			}

			return Keys[Keys.Length - 1].ToSample(disk);
		}

		/// <summary>One interpolated set of sky colours.</summary>
		public readonly struct Sample
		{
			public Sample(Color zenith, Color horizon, Color ground, Color sunColor, float sunStrength, float horizonGlow, float sunDisk)
			{
				Zenith = zenith;
				Horizon = horizon;
				Ground = ground;
				SunColor = sunColor;
				SunStrength = sunStrength;
				HorizonGlow = horizonGlow;
				SunDisk = sunDisk;
			}

			public Color Zenith { get; }

			/// <summary>Sky at the horizon away from the sun; also the fog colour.</summary>
			public Color Horizon { get; }

			public Color Ground { get; }

			public Color SunColor { get; }

			/// <summary>Direct sunlight: 0 with the sun below the horizon, 1 by mid-morning.</summary>
			public float SunStrength { get; }

			/// <summary>Strength of the warm band along the horizon toward the sun, strongest around sunset.</summary>
			public float HorizonGlow { get; }

			/// <summary>Visibility of the sun's disk: 1 above the horizon, 0 once it has set.</summary>
			public float SunDisk { get; }
		}

		private readonly struct Key
		{
			public Key(float elevation, Color zenith, Color horizon, Color ground, Color sunColor, float sunStrength, float horizonGlow)
			{
				Elevation = elevation;
				Zenith = zenith;
				Horizon = horizon;
				Ground = ground;
				SunColor = sunColor;
				SunStrength = sunStrength;
				HorizonGlow = horizonGlow;
			}

			public float Elevation { get; }

			public Color Zenith { get; }

			public Color Horizon { get; }

			public Color Ground { get; }

			public Color SunColor { get; }

			public float SunStrength { get; }

			public float HorizonGlow { get; }

			public Sample ToSample(float disk) => new Sample(Zenith, Horizon, Ground, SunColor, SunStrength, HorizonGlow, disk);
		}
	}
}
