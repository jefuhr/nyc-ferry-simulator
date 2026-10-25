using System;
using System.Collections.Generic;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// A station in an environment pack: where it is, where its data came from, and its series. The core uses the world
	/// position (<see cref="North"/>, <see cref="East"/>); latitude and longitude are kept for display.
	/// </summary>
	public abstract class EnvironmentStation
	{
		protected EnvironmentStation(string id, string name, double latitude, double longitude, double north, double east, string source, string sourceSha256)
		{
			if (string.IsNullOrEmpty(id))
			{
				throw new ArgumentException("A station needs an id.");
			}

			if (!SeriesTime.IsFinite(latitude) || !SeriesTime.IsFinite(longitude) || !SeriesTime.IsFinite(north) || !SeriesTime.IsFinite(east))
			{
				throw new ArgumentException($"Station {id}: position must be finite.");
			}

			Id = id;
			Name = name ?? string.Empty;
			Latitude = latitude;
			Longitude = longitude;
			North = north;
			East = east;
			Source = source ?? string.Empty;
			SourceSha256 = sourceSha256 ?? string.Empty;
		}

		/// <summary>The station's id at its source (NOAA CO-OPS id, current prediction id).</summary>
		public string Id { get; }

		public string Name { get; }

		/// <summary>WGS84 latitude, degrees.</summary>
		public double Latitude { get; }

		/// <summary>WGS84 longitude, degrees.</summary>
		public double Longitude { get; }

		/// <summary>World position north of the origin, m.</summary>
		public double North { get; }

		/// <summary>World position east of the origin, m.</summary>
		public double East { get; }

		/// <summary>The raw file the series came from, as the pack's manifest names it.</summary>
		public string Source { get; }

		/// <summary>SHA-256 of that raw file, hex.</summary>
		public string SourceSha256 { get; }
	}

	/// <summary>A current station: the water's velocity at one place, in world north and east components, m/s.</summary>
	public sealed class CurrentStation : EnvironmentStation
	{
		public CurrentStation(string id, string name, string kind, double latitude, double longitude, double north, double east, double? binDepth,
			string source, string sourceSha256, VectorSeries velocity)
			: base(id, name, latitude, longitude, north, east, source, sourceSha256)
		{
			Kind = kind ?? string.Empty;
			BinDepth = binDepth;
			Velocity = velocity ?? throw new ArgumentNullException(nameof(velocity));
		}

		/// <summary>What the series is, for example <c>harmonic-prediction</c>.</summary>
		public string Kind { get; }

		/// <summary>Depth of the measurement or prediction bin below the surface, m, when the source gives one.</summary>
		public double? BinDepth { get; }

		/// <summary>Velocity of the water (the direction it flows toward), m/s.</summary>
		public VectorSeries Velocity { get; }
	}

	/// <summary>A tide gauge: the water surface height above NAVD88, m.</summary>
	public sealed class WaterLevelStation : EnvironmentStation
	{
		private readonly bool[] _predicted;

		public WaterLevelStation(string id, string name, double latitude, double longitude, double north, double east, string source, string sourceSha256,
			ScalarSeries level, IReadOnlyList<bool> predicted)
			: base(id, name, latitude, longitude, north, east, source, sourceSha256)
		{
			Level = level ?? throw new ArgumentNullException(nameof(level));
			if (predicted == null || predicted.Count != level.Count)
			{
				throw new ArgumentException($"Station {id}: one observed or predicted flag per sample is required.");
			}

			_predicted = new bool[predicted.Count];
			for (var i = 0; i < _predicted.Length; i++)
			{
				_predicted[i] = predicted[i];
			}
		}

		/// <summary>Water surface height above NAVD88, m.</summary>
		public ScalarSeries Level { get; }

		/// <summary>True when sample <paramref name="index"/> is a prediction filling a gap in the observations.</summary>
		public bool IsPredicted(int index) => _predicted[index];
	}

	/// <summary>A wind station: the air's velocity (the direction it blows toward) observed at the station, m/s.</summary>
	public sealed class WindStation : EnvironmentStation
	{
		private readonly double[] _gusts;

		public WindStation(string id, string name, double latitude, double longitude, double north, double east, double? sensorHeight,
			string source, string sourceSha256, VectorSeries velocity, IReadOnlyList<double> gusts)
			: base(id, name, latitude, longitude, north, east, source, sourceSha256)
		{
			SensorHeight = sensorHeight;
			Velocity = velocity ?? throw new ArgumentNullException(nameof(velocity));
			_gusts = SeriesTime.Values(gusts, velocity.Count, $"Station {id} gusts", allowNaN: true);
			for (var i = 0; i < _gusts.Length; i++)
			{
				if (_gusts[i] < 0.0)
				{
					throw new ArgumentException($"Station {id}: gust {i} is negative.");
				}
			}
		}

		/// <summary>Anemometer height above the water, m, when the source gives one.</summary>
		public double? SensorHeight { get; }

		/// <summary>Mean wind velocity (the direction the air moves toward), m/s.</summary>
		public VectorSeries Velocity { get; }

		/// <summary>Gust speed at sample <paramref name="index"/>, m/s; NaN when the source has none. Not applied in v1.</summary>
		public double Gust(int index) => _gusts[index];
	}
}
