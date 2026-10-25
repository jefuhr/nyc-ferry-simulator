using System;
using FerrySim.Sim;

namespace FerrySim.UI
{
	/// <summary>
	/// What the panel and the HUD show about the water, the air, the day and the clock, worked out once per frame
	/// from an <see cref="IVesselTelemetrySource"/> and an <see cref="IEnvironmentReadoutSource"/> (either may be
	/// null). Both read the same strings, so the clock string is built once per displayed second for both.
	/// <list type="bullet">
	/// <item>With no environment source, or <see cref="IEnvironmentReadoutSource.HasEnvironment"/> false, every water
	/// and air readout is "---", no warning shows and the clock is New York time from the telemetry, as before.</item>
	/// <item>A pack run's clock is local time from <see cref="EnvironmentReadout.LocalUtcOffset"/>, with its date; a calm
	/// or uniform run's clock is UTC, labelled UTC, with its date.</item>
	/// <item>The coverage warnings belong to the run: they start over when the telemetry source, its
	/// <see cref="IVesselTelemetrySource.RunId"/> or the environment source changes, and are hidden, not forgotten,
	/// while the environment has no data.</item>
	/// </list>
	/// Everything in range comes from shared caches; only the clock (once per displayed second) and the date (once
	/// per displayed day) build a string.
	/// </summary>
	public sealed class EnvironmentDisplay
	{
		public const string ZoneUnknown = "NEW YORK";
		public const string CalmSource = "CALM";
		public const string UniformSource = "UNIFORM";

		private static readonly TimeSpan Daylight = TimeSpan.FromHours(-4);
		private static readonly TimeSpan Standard = TimeSpan.FromHours(-5);

		private readonly ReadoutText _tideText = new ReadoutText();
		private readonly ReadoutText _driftText = new ReadoutText();
		private readonly ReadoutText _windText = new ReadoutText();
		private IVesselTelemetrySource _runSource;
		private IEnvironmentReadoutSource _runEnvironment;
		private long _runId;
		private bool _runSeen;
		private long _clockSecond = long.MinValue;
		private long _dateDay = long.MinValue;
		private TimeSpan _offset;
		private TimeSpan _otherOffset;
		private string _otherZone;

		public bool HasEnvironment { get; private set; }

		public EnvironmentKind Kind { get; private set; }

		/// <summary>"CALM" or "UNIFORM" for runs without a pack day, empty otherwise.</summary>
		public string Source { get; private set; } = string.Empty;

		public string Tide { get; private set; } = InstrumentFormat.NoValue;

		public string TideUnit { get; private set; } = DisplayUnits.Symbol(DistanceUnit.Feet);

		public TideTrend Trend { get; private set; }

		public string Set { get; private set; } = InstrumentFormat.NoValue;

		public string Drift { get; private set; } = InstrumentFormat.NoValue;

		public bool WindMissing { get; private set; }

		public string WindFrom { get; private set; } = InstrumentFormat.NoValue;

		public string WindSpeed { get; private set; } = InstrumentFormat.NoValue;

		/// <summary>The run's time holds: outside the day's data, everything held at its edge.</summary>
		public CoverageMonitor DayData { get; } = new CoverageMonitor();

		/// <summary>The run's depth misses: outside every depth grid or over unknown bed, where the pack's outside depth is used.</summary>
		public CoverageMonitor DepthGrid { get; } = new CoverageMonitor();

		public CoverageIndication DayDataIndication => HasEnvironment ? DayData.Indication : CoverageIndication.None;

		public CoverageIndication DepthGridIndication => HasEnvironment ? DepthGrid.Indication : CoverageIndication.None;

		public bool ClockKnown { get; private set; }

		/// <summary>"14:05:09", or "---".</summary>
		public string Clock { get; private set; } = InstrumentFormat.NoValue;

		/// <summary>EDT, EST, UTC, or NEW YORK while the clock is unknown.</summary>
		public string Zone { get; private set; } = ZoneUnknown;

		/// <summary>True while the date is shown: the clock is known and comes from the environment.</summary>
		public bool DateShown { get; private set; }

		/// <summary>"MON 14 JUL 2025" while <see cref="DateShown"/>.</summary>
		public string Date { get; private set; } = string.Empty;

		public void Update(IVesselTelemetrySource telemetry, IEnvironmentReadoutSource environment, DistanceUnit unit)
		{
			StartOverOnNewRun(telemetry, environment);
			var hasTelemetry = telemetry != null && telemetry.HasTelemetry;
			var time = hasTelemetry ? telemetry.State.Time : double.NaN;
			TideUnit = DisplayUnits.Symbol(unit);
			if (environment == null || !environment.HasEnvironment)
			{
				ShowNoEnvironment();
			}
			else
			{
				Show(environment.Environment, unit, time);
			}

			if (!hasTelemetry)
			{
				ShowNoClock();
			}
			else
			{
				UpdateClock(telemetry.ScenarioStartUtc, time);
			}
		}

		private void Show(in EnvironmentReadout readout, DistanceUnit unit, double time)
		{
			HasEnvironment = true;
			Kind = readout.Kind;
			Source = Kind == EnvironmentKind.Calm ? CalmSource : Kind == EnvironmentKind.Uniform ? UniformSource : string.Empty;
			Tide = _tideText.Distance(readout.WaterLevelNavd88, unit);
			Trend = EnvironmentFormat.Trend(readout.WaterLevelRate);
			var current = readout.Current;
			Set = EnvironmentFormat.Set(current);
			Drift = InstrumentFormat.IsFinite(current.North) && InstrumentFormat.IsFinite(current.East)
				? _driftText.Speed(current.Magnitude)
				: InstrumentFormat.NoValue;
			WindMissing = readout.WindMissing;
			var wind = readout.Wind;
			WindFrom = EnvironmentFormat.WindFrom(wind, WindMissing);
			WindSpeed = !WindMissing && InstrumentFormat.IsFinite(wind.North) && InstrumentFormat.IsFinite(wind.East)
				? _windText.Fixed(DisplayUnits.Knots(wind.Magnitude), 0)
				: InstrumentFormat.NoValue;
			DayData.Update(readout.TimeHolds, time);
			DepthGrid.Update(readout.DepthMisses, time);
			_offset = readout.LocalUtcOffset;
		}

		private void ShowNoEnvironment()
		{
			// The coverage monitors keep their state: a dropout inside a run is not a new run.
			HasEnvironment = false;
			Kind = EnvironmentKind.Calm;
			Source = string.Empty;
			Tide = InstrumentFormat.NoValue;
			Trend = TideTrend.Unknown;
			Set = InstrumentFormat.NoValue;
			Drift = InstrumentFormat.NoValue;
			WindMissing = false;
			WindFrom = InstrumentFormat.NoValue;
			WindSpeed = InstrumentFormat.NoValue;
		}

		private void UpdateClock(DateTimeOffset scenarioStartUtc, double time)
		{
			DateTime clock;
			string zone;
			var fromEnvironment = HasEnvironment;
			if (fromEnvironment)
			{
				if (!EnvironmentFormat.TryClock(Kind, _offset, scenarioStartUtc, time, out clock))
				{
					ShowNoClock();
					return;
				}

				zone = ZoneFor(Kind, _offset);
			}
			else
			{
				if (!NewYorkTime.TryToLocal(scenarioStartUtc, time, out clock, out var daylight))
				{
					ShowNoClock();
					return;
				}

				zone = NewYorkTime.Abbreviation(daylight);
			}

			var second = clock.Hour * 3600L + clock.Minute * 60L + clock.Second;
			if (!ClockKnown || second != _clockSecond)
			{
				_clockSecond = second;
				Clock = InstrumentFormat.Clock(clock);
			}

			ClockKnown = true;
			Zone = zone;
			DateShown = fromEnvironment;
			if (fromEnvironment)
			{
				var day = clock.Ticks / TimeSpan.TicksPerDay;
				if (day != _dateDay)
				{
					_dateDay = day;
					Date = EnvironmentFormat.Date(clock);
				}
			}
		}

		private void ShowNoClock()
		{
			ClockKnown = false;
			Clock = InstrumentFormat.NoValue;
			Zone = ZoneUnknown;
			DateShown = false;
		}

		/// <summary>The zone label: a constant for UTC, EDT and EST; built once for any other offset rather than every frame.</summary>
		private string ZoneFor(EnvironmentKind kind, TimeSpan offset)
		{
			if (kind != EnvironmentKind.Pack || offset == TimeSpan.Zero || offset == Daylight || offset == Standard)
			{
				return EnvironmentFormat.Zone(kind, offset);
			}

			if (_otherZone == null || offset != _otherOffset)
			{
				_otherOffset = offset;
				_otherZone = EnvironmentFormat.Zone(kind, offset);
			}

			return _otherZone;
		}

		/// <summary>Forgets the coverage warnings when the run, the telemetry source or the environment source changes.</summary>
		private void StartOverOnNewRun(IVesselTelemetrySource telemetry, IEnvironmentReadoutSource environment)
		{
			var runId = telemetry != null ? telemetry.RunId : 0L;
			if (_runSeen && ReferenceEquals(telemetry, _runSource) && runId == _runId && ReferenceEquals(environment, _runEnvironment))
			{
				return;
			}

			_runSeen = true;
			_runSource = telemetry;
			_runId = runId;
			_runEnvironment = environment;
			DayData.Reset();
			DepthGrid.Reset();
		}
	}
}
