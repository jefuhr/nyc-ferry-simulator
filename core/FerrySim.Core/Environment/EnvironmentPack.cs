using System;
using System.Collections.Generic;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// Everything the simulator knows about the water and the air for one scenario day in one region (format
	/// <c>ferrysim-env/1</c>): the day's tide, current and wind series and the region's depth set. Immutable once built,
	/// so one pack can back any number of <see cref="DataEnvironment"/>s. <c>FerrySim.Core.Serialization.EnvironmentPackJson</c>
	/// loads and verifies one from disk.
	/// </summary>
	public sealed class EnvironmentPack
	{
		/// <summary>The day manifest's format string.</summary>
		public const string Format = "ferrysim-env/1";

		/// <summary>The depth manifest's format string.</summary>
		public const string DepthFormat = "ferrysim-env-depth/1";

		private readonly CurrentStation[] _currents;
		private readonly WaterLevelStation[] _waterLevels;
		private readonly WindStation[] _winds;
		private readonly string[] _notes;
		private readonly SeriesGap[] _gaps;

		/// <param name="region">Region id, for example <c>lower-east-river</c>.</param>
		/// <param name="date">The scenario day, <c>YYYY-MM-DD</c>.</param>
		/// <param name="epochUtc">Local midnight in America/New_York on that day, as UTC; series times count from it.</param>
		/// <param name="coverageStart">Start of the time the series cover, s since the epoch.</param>
		/// <param name="coverageEnd">End of the time the series cover, s since the epoch.</param>
		/// <param name="depth">The region's bed elevations.</param>
		/// <param name="currents">Current stations, at least one.</param>
		/// <param name="waterLevels">Tide gauges, at least one.</param>
		/// <param name="winds">Wind stations; v1 uses the first. None means the day has no wind data and the air is taken as still.</param>
		/// <param name="notes">What a reader of the pack should know, such as a gap that was filled.</param>
		/// <param name="digest">The day digest (ferrysim-env/1), hex; empty for a pack built in memory.</param>
		public EnvironmentPack(string region, string date, DateTimeOffset epochUtc, double coverageStart, double coverageEnd, DepthSet depth,
			IReadOnlyList<CurrentStation> currents, IReadOnlyList<WaterLevelStation> waterLevels, IReadOnlyList<WindStation> winds,
			IReadOnlyList<string> notes, string digest)
			: this(region, date, epochUtc, coverageStart, coverageEnd, depth, currents, waterLevels, winds, notes, digest, null)
		{
		}

		/// <summary>A pack day with the region's current-field basis, or none (null) for the station blend.</summary>
		/// <param name="currentField">The region's basis; every station it names must be one of <paramref name="currents"/>.</param>
		public EnvironmentPack(string region, string date, DateTimeOffset epochUtc, double coverageStart, double coverageEnd, DepthSet depth,
			IReadOnlyList<CurrentStation> currents, IReadOnlyList<WaterLevelStation> waterLevels, IReadOnlyList<WindStation> winds,
			IReadOnlyList<string> notes, string digest, CurrentFieldBasis currentField)
		{
			if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(date))
			{
				throw new ArgumentException("An environment pack needs a region and a date.");
			}

			if (!SeriesTime.IsFinite(coverageStart) || !SeriesTime.IsFinite(coverageEnd) || !(coverageEnd > coverageStart))
			{
				throw new ArgumentException("The pack's time coverage must be a finite window with its end after its start.");
			}

			Region = region;
			Date = date;
			EpochUtc = epochUtc.ToUniversalTime();
			CoverageStart = coverageStart;
			CoverageEnd = coverageEnd;
			Depth = depth ?? throw new ArgumentNullException(nameof(depth));
			_currents = Stations(currents, "current", true);
			_waterLevels = Stations(waterLevels, "water level", true);
			_winds = Stations(winds, "wind", false);
			_notes = notes == null ? Array.Empty<string>() : new List<string>(notes).ToArray();
			Digest = digest ?? string.Empty;
			if (currentField != null)
			{
				if (currentField.Region != region)
				{
					throw new ArgumentException($"The current field is for '{currentField.Region}', the pack for '{region}'.");
				}

				foreach (var station in currentField.Stations)
				{
					if (Array.FindIndex(_currents, c => c.Id == station.Id) < 0)
					{
						throw new ArgumentException($"The current field needs current station {station.Id}, which the pack does not have.");
					}
				}
			}

			CurrentField = currentField;

			var gaps = new List<SeriesGap>();
			foreach (var station in _currents)
			{
				gaps.AddRange(SeriesTime.HeldSpans(EnvironmentSeries.Current, station.Id, station.Velocity.TimesArray, coverageStart, coverageEnd));
			}

			foreach (var station in _waterLevels)
			{
				gaps.AddRange(SeriesTime.HeldSpans(EnvironmentSeries.WaterLevel, station.Id, station.Level.TimesArray, coverageStart, coverageEnd));
			}

			foreach (var station in _winds)
			{
				gaps.AddRange(SeriesTime.HeldSpans(EnvironmentSeries.Wind, station.Id, station.Velocity.TimesArray, coverageStart, coverageEnd));
			}

			_gaps = gaps.ToArray();
		}

		public string Region { get; }

		/// <summary>The scenario day, <c>YYYY-MM-DD</c>.</summary>
		public string Date { get; }

		/// <summary><c>region/date</c>: what recordings name the pack by (<c>environment_pack</c>).</summary>
		public string Name => Region + "/" + Date;

		/// <summary>Local midnight in America/New_York on <see cref="Date"/>, as UTC. Series times are seconds since it.</summary>
		public DateTimeOffset EpochUtc { get; }

		/// <summary>Start of the time window the series cover, s since <see cref="EpochUtc"/>.</summary>
		public double CoverageStart { get; }

		/// <summary>End of the time window the series cover, s since <see cref="EpochUtc"/>.</summary>
		public double CoverageEnd { get; }

		public DepthSet Depth { get; }

		public IReadOnlyList<CurrentStation> CurrentStations => _currents;

		public IReadOnlyList<WaterLevelStation> WaterLevelStations => _waterLevels;

		/// <summary>The day's wind stations; empty when no station had data, and the air is then taken as still.</summary>
		public IReadOnlyList<WindStation> WindStations => _winds;

		/// <summary>The manifest's notes, such as gaps the pipeline filled or left.</summary>
		public IReadOnlyList<string> Notes => _notes;

		/// <summary>Where a station's series holds a sample inside the coverage window instead of interpolating.</summary>
		public IReadOnlyList<SeriesGap> HeldGaps => _gaps;

		/// <summary>The day digest, which covers the depth set's digest: what recordings carry as <c>environment_digest</c>.</summary>
		public string Digest { get; }

		/// <summary>
		/// The region's channel current-field basis when the day names one; null means the station blend
		/// (<see cref="StationBlendCurrentField"/>).
		/// </summary>
		public CurrentFieldBasis CurrentField { get; }

		private static T[] Stations<T>(IReadOnlyList<T> stations, string kind, bool required) where T : EnvironmentStation
		{
			if (stations == null || (required && stations.Count == 0))
			{
				throw new ArgumentException($"An environment pack needs at least one {kind} station.");
			}

			var copy = new T[stations.Count];
			for (var i = 0; i < copy.Length; i++)
			{
				copy[i] = stations[i] ?? throw new ArgumentException($"The {kind} station at {i} is missing.");
				for (var j = 0; j < i; j++)
				{
					if (copy[j].Id == copy[i].Id)
					{
						throw new ArgumentException($"The {kind} station {copy[i].Id} appears twice.");
					}
				}
			}

			return copy;
		}
	}
}
