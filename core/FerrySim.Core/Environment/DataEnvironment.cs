using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// The water and air of one real day, from an <see cref="EnvironmentPack"/>. Simulation time t (seconds since the
	/// scenario start) reads the pack at t + (scenario start − pack epoch).
	/// <list type="bullet">
	/// <item>Depth: the water level minus the bed elevation, bilinear on the depth grids. The water level is each gauge's
	/// series at that time, blended between gauges by inverse distance squared. Outside every grid, or where the bed is
	/// unknown, it is the depth set's outside depth, and the miss is counted.</item>
	/// <item>Current: from the <see cref="ICurrentField"/>, by default the provisional <see cref="StationBlendCurrentField"/>.</item>
	/// <item>Wind: uniform, the first wind station's mean wind at that time; gusts are not applied.</item>
	/// <item>Before or after the pack's coverage window, every value is held at the window's nearer edge, and the hold is counted.</item>
	/// </list>
	/// Every sample is deterministic and allocation-free. <see cref="Coverage"/> reports the misses and holds for the UI.
	/// Not thread-safe: the counters and the per-time caches belong to one simulation thread.
	/// </summary>
	public sealed class DataEnvironment : IEnvironment
	{
		private readonly DepthSet _depth;
		private readonly ICurrentField _currentField;
		private readonly VectorSeries _wind;
		private readonly ScalarSeries[] _gauges;
		private readonly double[] _gaugeNorth;
		private readonly double[] _gaugeEast;
		private readonly double[] _levels;
		private readonly double _offset;
		private readonly double _coverageStart;
		private readonly double _coverageEnd;
		private double _levelTime = double.NaN;
		private long _samples;
		private long _timeHolds;
		private long _depthSamples;
		private long _depthMisses;

		/// <summary>
		/// The pack's day with its own current field: the channel field when the pack has a current-field basis, the
		/// station blend otherwise.
		/// </summary>
		public DataEnvironment(EnvironmentPack pack, DateTimeOffset scenarioStartUtc)
			: this(pack, scenarioStartUtc, DefaultField(pack ?? throw new ArgumentNullException(nameof(pack))))
		{
		}

		/// <summary>The pack's day with a given current field.</summary>
		public DataEnvironment(EnvironmentPack pack, DateTimeOffset scenarioStartUtc, ICurrentField currentField)
		{
			Pack = pack ?? throw new ArgumentNullException(nameof(pack));
			_currentField = currentField ?? throw new ArgumentNullException(nameof(currentField));
			_depth = pack.Depth;
			_wind = pack.WindStations.Count > 0 ? pack.WindStations[0].Velocity : null;
			var gauges = pack.WaterLevelStations;
			_gauges = new ScalarSeries[gauges.Count];
			_gaugeNorth = new double[gauges.Count];
			_gaugeEast = new double[gauges.Count];
			_levels = new double[gauges.Count];
			for (var i = 0; i < gauges.Count; i++)
			{
				_gauges[i] = gauges[i].Level;
				_gaugeNorth[i] = gauges[i].North;
				_gaugeEast[i] = gauges[i].East;
			}

			ScenarioStartUtc = scenarioStartUtc.ToUniversalTime();
			_offset = (ScenarioStartUtc.UtcTicks - pack.EpochUtc.UtcTicks) / (double)TimeSpan.TicksPerSecond;
			_coverageStart = pack.CoverageStart;
			_coverageEnd = pack.CoverageEnd;
		}

		public EnvironmentPack Pack { get; }

		/// <summary>The scenario's absolute start, UTC: simulation time zero.</summary>
		public DateTimeOffset ScenarioStartUtc { get; }

		public ICurrentField CurrentField => _currentField;

		/// <summary>Seconds from the pack's epoch to the scenario start: pack time = simulation time + this.</summary>
		public double EpochOffset => _offset;

		/// <summary>Pack time (seconds since the pack's epoch) of a simulation time, before any hold at the coverage edges.</summary>
		public double PackTime(double time) => time + _offset;

		/// <summary>What has been made up so far: depth misses, holds at the coverage edges, and the series' gaps.</summary>
		public EnvironmentCoverage Coverage => new EnvironmentCoverage(
			At(_coverageStart), At(_coverageEnd), _coverageStart - _offset, _coverageEnd - _offset, _depth.OutsideDepth,
			_depthSamples, _depthMisses, _samples, _timeHolds, _wind == null, Pack.Notes, Pack.HeldGaps);

		/// <summary>
		/// Zeroes the sample, miss and hold counts, and the channel field's count of samples outside its grids, for example
		/// before the run a report is about.
		/// </summary>
		public void ResetCounters()
		{
			_samples = 0;
			_timeHolds = 0;
			_depthSamples = 0;
			_depthMisses = 0;
			if (_currentField is ChannelCurrentField channel)
			{
				channel.ResetCounters();
			}
		}

		public HorizontalVector SampleCurrent(double north, double east, double time) => _currentField.Sample(north, east, HeldPackTime(time));

		public HorizontalVector SampleWind(double north, double east, double time)
		{
			var packTime = HeldPackTime(time);
			return _wind == null ? HorizontalVector.Zero : _wind.Sample(packTime);
		}

		public double SampleDepth(double north, double east, double time)
		{
			var packTime = HeldPackTime(time);
			_depthSamples++;
			if (_depth.Sample(north, east, out var bed) != BedSample.Known)
			{
				_depthMisses++;
				return _depth.OutsideDepth;
			}

			return WaterLevelAt(north, east, packTime) - bed;
		}

		/// <summary>Water surface height above NAVD88 at a world position and simulation time, m.</summary>
		public double SampleWaterLevel(double north, double east, double time) => WaterLevelAt(north, east, HeldPackTime(time));

		/// <summary>Bed elevation (NAVD88 m, positive up) at a world position, without counting a sample.</summary>
		public BedSample SampleBedElevation(double north, double east, out double bedElevation) => _depth.Sample(north, east, out bedElevation);

		private double WaterLevelAt(double north, double east, double packTime)
		{
			if (!(packTime == _levelTime))
			{
				for (var i = 0; i < _gauges.Length; i++)
				{
					_levels[i] = _gauges[i].Sample(packTime);
				}

				_levelTime = packTime;
			}

			return InverseDistanceSquared.Blend(north, east, _gaugeNorth, _gaugeEast, _levels);
		}

		private double HeldPackTime(double time)
		{
			_samples++;
			var packTime = time + _offset;
			if (packTime >= _coverageStart && packTime <= _coverageEnd)
			{
				return packTime;
			}

			_timeHolds++;
			return packTime > _coverageEnd ? _coverageEnd : _coverageStart;
		}

		private DateTimeOffset At(double packTime) => Pack.EpochUtc.AddTicks((long)Math.Round(packTime * TimeSpan.TicksPerSecond));

		private static ICurrentField DefaultField(EnvironmentPack pack) => pack.CurrentField != null
			? (ICurrentField)new ChannelCurrentField(pack.CurrentField, pack.CurrentStations)
			: new StationBlendCurrentField(pack.CurrentStations);
	}
}
