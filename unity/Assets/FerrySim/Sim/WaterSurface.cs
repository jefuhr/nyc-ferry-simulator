using System;
using System.IO;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Where the calm water surface is in WGS84 terms: what Cesium needs to put the vessel on the water.
	/// The world owns the real implementation (tides from CO-OPS, the slice's geoid grid); the bridge only
	/// depends on this interface.
	/// </summary>
	public interface IWaterSurface
	{
		/// <summary>
		/// Ellipsoidal height of the calm water surface above WGS84, metres, at world position (north, east) and
		/// simulation time <paramref name="time"/>: the geoid offset (ellipsoidal height of NAVD88 zero) plus the
		/// water level above NAVD88.
		/// </summary>
		double EllipsoidalHeight(double north, double east, double time);
	}

	/// <summary>
	/// The same water surface everywhere and always: one geoid offset plus one NAVD88 water level. The offset
	/// comes from the data pipeline's <c>geoid_offset.json</c> or is supplied by the caller; nothing here
	/// assumes a value.
	/// </summary>
	public sealed class ConstantWaterSurface : IWaterSurface
	{
		/// <summary>The lower East River slice output, relative to the repository root.</summary>
		public const string DefaultGeoidOffsetFile = "data/processed/lower-east-river/geoid_offset.json";

		public ConstantWaterSurface(double navd88ZeroEllipsoidalHeight, double waterLevelNavd88 = 0.0)
		{
			if (double.IsNaN(navd88ZeroEllipsoidalHeight) || double.IsInfinity(navd88ZeroEllipsoidalHeight) ||
				double.IsNaN(waterLevelNavd88) || double.IsInfinity(waterLevelNavd88))
			{
				throw new ArgumentException("Water surface heights must be finite.");
			}

			Navd88ZeroEllipsoidalHeight = navd88ZeroEllipsoidalHeight;
			WaterLevelNavd88 = waterLevelNavd88;
		}

		/// <summary>Ellipsoidal height of the NAVD88 zero surface, metres (negative around New York).</summary>
		public double Navd88ZeroEllipsoidalHeight { get; }

		/// <summary>Water level above NAVD88, metres.</summary>
		public double WaterLevelNavd88 { get; }

		public double EllipsoidalHeight(double north, double east, double time) => Navd88ZeroEllipsoidalHeight + WaterLevelNavd88;

		/// <summary>
		/// Reads the slice-centre offset (<c>points.centre.navd88_zero_ellipsoidal_height_m</c>) from a
		/// <c>geoid_offset.json</c> written by <c>ferrydata build</c>. Returns false with a reason when the file
		/// is missing or malformed.
		/// </summary>
		public static bool TryReadGeoidOffset(string path, out double offset, out string problem)
		{
			offset = double.NaN;
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
			{
				problem = $"geoid offset file not found: {path}";
				return false;
			}

			GeoidOffsetFile parsed;
			try
			{
				parsed = JsonUtility.FromJson<GeoidOffsetFile>(File.ReadAllText(path));
			}
			catch (Exception exception)
			{
				problem = $"{path}: {exception.Message}";
				return false;
			}

			var centre = parsed?.points?.centre;
			if (centre == null || double.IsNaN(centre.navd88_zero_ellipsoidal_height_m) || centre.navd88_zero_ellipsoidal_height_m == 0.0)
			{
				problem = $"{path}: no points.centre.navd88_zero_ellipsoidal_height_m";
				return false;
			}

			offset = centre.navd88_zero_ellipsoidal_height_m;
			problem = null;
			return true;
		}

		// Field names mirror data-pipeline/ferrydata/geoid.py.
		[Serializable]
		private sealed class GeoidOffsetFile
		{
			public GeoidPoints points;
		}

		[Serializable]
		private sealed class GeoidPoints
		{
			public GeoidPoint centre;
		}

		[Serializable]
		private sealed class GeoidPoint
		{
			public double navd88_zero_ellipsoidal_height_m = double.NaN;
		}
	}
}
