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
			: this(id, name, kind, latitude, longitude, north, east, binDepth, source, sourceSha256, velocity, null)
		{
		}

		/// <param name="derivation">How the series was derived from a reference station, or null for a station's own predictions.</param>
		public CurrentStation(string id, string name, string kind, double latitude, double longitude, double north, double east, double? binDepth,
			string source, string sourceSha256, VectorSeries velocity, CurrentStationDerivation derivation)
			: base(id, name, latitude, longitude, north, east, source, sourceSha256)
		{
			Kind = kind ?? string.Empty;
			BinDepth = binDepth;
			Velocity = velocity ?? throw new ArgumentNullException(nameof(velocity));
			Derivation = derivation;
		}

		/// <summary>What the series is, for example <c>harmonic-prediction</c> or <c>subordinate-prediction</c>.</summary>
		public string Kind { get; }

		/// <summary>For a subordinate station, how its series came from its reference station's; null otherwise.</summary>
		public CurrentStationDerivation Derivation { get; }

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

	/// <summary>A raw file a series was made from, as the pack's manifest names it.</summary>
	public sealed class SourceFile
	{
		public SourceFile(string name, string sha256)
		{
			Name = name ?? string.Empty;
			Sha256 = sha256 ?? string.Empty;
		}

		public string Name { get; }

		/// <summary>SHA-256 of the file, hex.</summary>
		public string Sha256 { get; }
	}

	/// <summary>
	/// How a subordinate station's series was made by NOAA's reference-station method: the reference station and bin, the
	/// time offsets of its four phases, the speed ratios of its maxima, its flood and ebb directions, and the raw files.
	/// Values are as CO-OPS publishes them (minutes, degrees true toward), for provenance; the series itself is in the pack.
	/// </summary>
	public sealed class CurrentStationDerivation
	{
		/// <summary>The method name for NOAA's subordinate-station corrections.</summary>
		public const string NoaaSubordinate = "noaa-subordinate";

		public CurrentStationDerivation(string method, string referenceStation, int referenceBin, double slackBeforeFloodMinutes, double maximumFloodMinutes,
			double slackBeforeEbbMinutes, double maximumEbbMinutes, double floodRatio, double ebbRatio, double floodDirectionDegrees, double ebbDirectionDegrees,
			IReadOnlyList<SourceFile> sources)
		{
			if (method != NoaaSubordinate || string.IsNullOrEmpty(referenceStation) || referenceBin < 1)
			{
				throw new ArgumentException($"A derivation needs method {NoaaSubordinate}, a reference station and its bin.");
			}

			foreach (var value in new[] { slackBeforeFloodMinutes, maximumFloodMinutes, slackBeforeEbbMinutes, maximumEbbMinutes, floodDirectionDegrees, ebbDirectionDegrees })
			{
				if (!SeriesTime.IsFinite(value))
				{
					throw new ArgumentException("A derivation's offsets and directions must be finite.");
				}
			}

			if (!(floodRatio > 0.0) || !(ebbRatio > 0.0) || double.IsInfinity(floodRatio) || double.IsInfinity(ebbRatio))
			{
				throw new ArgumentException("A derivation's speed ratios must be positive.");
			}

			if (floodDirectionDegrees < 0.0 || floodDirectionDegrees >= 360.0 || ebbDirectionDegrees < 0.0 || ebbDirectionDegrees >= 360.0)
			{
				throw new ArgumentException("A derivation's directions must be degrees in [0, 360).");
			}

			if (sources == null || sources.Count == 0)
			{
				throw new ArgumentException("A derivation needs the raw files it came from.");
			}

			Method = method;
			ReferenceStation = referenceStation;
			ReferenceBin = referenceBin;
			SlackBeforeFloodMinutes = slackBeforeFloodMinutes;
			MaximumFloodMinutes = maximumFloodMinutes;
			SlackBeforeEbbMinutes = slackBeforeEbbMinutes;
			MaximumEbbMinutes = maximumEbbMinutes;
			FloodRatio = floodRatio;
			EbbRatio = ebbRatio;
			FloodDirectionDegrees = floodDirectionDegrees;
			EbbDirectionDegrees = ebbDirectionDegrees;
			Sources = new List<SourceFile>(sources).ToArray();
		}

		public string Method { get; }

		public string ReferenceStation { get; }

		public int ReferenceBin { get; }

		public double SlackBeforeFloodMinutes { get; }

		public double MaximumFloodMinutes { get; }

		public double SlackBeforeEbbMinutes { get; }

		public double MaximumEbbMinutes { get; }

		public double FloodRatio { get; }

		public double EbbRatio { get; }

		/// <summary>Mean flood direction, degrees true, toward.</summary>
		public double FloodDirectionDegrees { get; }

		/// <summary>Mean ebb direction, degrees true, toward.</summary>
		public double EbbDirectionDegrees { get; }

		/// <summary>The offsets record, the reference's 6-minute series and its events.</summary>
		public IReadOnlyList<SourceFile> Sources { get; }
	}
}
