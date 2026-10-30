using System;

namespace FerrySim.Core.Radar
{
	/// <summary>Estimated training-display parameters, not manufacturer specifications. All model units are SI.</summary>
	public sealed class RadarConfiguration
	{
		public RadarConfiguration(int spokes = 512, int maxSpokesPerFrame = 4, bool deduplicateEdges = true)
		{
			if (spokes < 64 || spokes > 4096) throw new ArgumentOutOfRangeException(nameof(spokes));
			if (maxSpokesPerFrame < 1 || maxSpokesPerFrame > spokes) throw new ArgumentOutOfRangeException(nameof(maxSpokesPerFrame));
			Spokes = spokes; MaxSpokesPerFrame = maxSpokesPerFrame; DeduplicateEdges = deduplicateEdges;
		}
		public int Spokes { get; }
		/// <summary>Disable only to compare against the original repeated-edge traversal in evidence tests.</summary>
		public bool DeduplicateEdges { get; }
		/// <summary>Estimated frame-work cap. Remaining passed spokes carry forward, with at most one revolution retained.</summary>
		public int MaxSpokesPerFrame { get; }
		/// <summary>Estimated 24 rpm, expressed as revolutions per second.</summary>
		public double RevolutionsPerSecond => .4;
		/// <summary>Estimated 1.2 degree horizontal beamwidth, radians.</summary>
		public double Beamwidth => Math.PI / 150;
		/// <summary>Estimated unknown-building roof, NAVD88 metres, used for shadow tests only.</summary>
		public double UnknownRoof => 12;
		/// <summary>Estimated tall bridge proxy when no upper vertical bound exists, NAVD88 metres.</summary>
		public double UnknownBridgeRoof => 80;
		/// <summary>No radar mount exists in the audited vessel manifest. Estimates above design waterline.</summary>
		public static double AntennaHeight(string vesselClass) => vesselClass == "k-class" ? 11 : 9;
	}

	/// <summary>Estimated range/pulse/bin presets. Nautical miles are converted at this configuration boundary.</summary>
	public readonly struct RadarScale
	{
		private static readonly double[] Ranges = { .125, .25, .5, .75, 1.5, 3, 6 };
		private static readonly double[] Pulses = { 60e-9, 60e-9, 90e-9, 90e-9, 150e-9, 300e-9, 600e-9 };
		private RadarScale(int index) { RangeMetres = Ranges[index] * 1852; PulseSeconds = Pulses[index]; Bins = index < 4 ? 512 : 1024; }
		public static int Count => Ranges.Length;
		public static RadarScale At(int index) => index >= 0 && index < Count ? new RadarScale(index) : throw new ArgumentOutOfRangeException(nameof(index));
		public double RangeMetres { get; }
		public double PulseSeconds { get; }
		public int Bins { get; }
		public double PulseMetres => 299792458 * PulseSeconds / 2;
		public double Centre(int bin) => (bin + .5) * RangeMetres / Bins;
		public int Bin(double metres) => !(metres >= 0 && metres <= RangeMetres) ? -1 : Math.Min(Bins - 1, (int)(metres * Bins / RangeMetres));
	}

	public static class RadarMath
	{
		public const double Tau = Math.PI * 2;
		public static double Wrap(double angle) => (angle % Tau + Tau) % Tau;
		public static double Bearing(double north, double east) => Wrap(Math.Atan2(east, north));
		public static double Range(double north, double east) => Math.Sqrt(north * north + east * east);
		public static double RelativeBearing(double bearing, double heading) => Wrap(bearing - heading);
		public static double TrueBearing(double relative, double heading) => Wrap(relative + heading);
		/// <summary>Task-specified 4/3-earth approximation: 2.21(sqrt(ha)+sqrt(ht)) nautical miles.</summary>
		public static double HorizonMetres(double antennaAboveWater, double targetAboveWater) =>
			2.21 * 1852 * (Math.Sqrt(Math.Max(0, antennaAboveWater)) + Math.Sqrt(Math.Max(0, targetAboveWater)));
		internal static double Noise(long revolution, int spoke, int bin)
		{
			unchecked
			{
				var h = (uint)revolution * 747796405u ^ (uint)(revolution >> 32) ^ (uint)spoke * 2891336453u ^ (uint)bin * 277803737u;
				h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
				return (h & 65535) / 65535.0;
			}
		}
	}

	/// <summary>One immutable render sample; tide is NAVD88, antenna height is above the current local water.</summary>
	public readonly struct RadarFrame
	{
		public RadarFrame(double north, double east, double heading, double tide, double antennaAboveWater, double windSpeed)
		{ North = north; East = east; Heading = heading; Tide = tide; AntennaAboveWater = antennaAboveWater; WindSpeed = windSpeed; }
		public double North { get; }
		public double East { get; }
		public double Heading { get; }
		public double Tide { get; }
		public double AntennaAboveWater { get; }
		public double WindSpeed { get; }
	}
	public readonly struct RadarTarget
	{
		public RadarTarget(double north, double east, double length, double height, double amplitude = .85)
		{ North = north; East = east; Length = length; Height = height; Amplitude = amplitude; }
		public double North { get; }
		public double East { get; }
		public double Length { get; }
		public double Height { get; }
		public double Amplitude { get; }
	}
}
