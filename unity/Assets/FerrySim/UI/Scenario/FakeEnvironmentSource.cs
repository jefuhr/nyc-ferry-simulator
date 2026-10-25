using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// A stand-in <see cref="IEnvironmentReadoutSource"/> for UI development: SYNTHETIC water and air that move
	/// believably with the <see cref="FakeTelemetrySource"/>'s clock, not data. Calm until a day is started, then a
	/// pack-like day: a semidiurnal tide (0.75 m about +0.15 m NAVD88, period 12.42 h), a reversing stream along the
	/// East River's axis (flood toward 035°T, ebb toward 215°T, up to 1.9 m/s) and a wind from about 240°T at 6 m/s,
	/// or none on a day marked without wind. Time holds count the 10 ms steps past the day's coverage
	/// (-1 h to +25 h around local midnight, as the packs have it); <see cref="OffDepthGrid"/> counts depth misses
	/// while it is on. It has data exactly while the fake telemetry does.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class FakeEnvironmentSource : MonoBehaviour, IEnvironmentReadoutSource
	{
		public const double CoverageStartSeconds = -3600.0;
		public const double CoverageEndSeconds = 90000.0;

		private const double TidePeriodSeconds = 12.42 * 3600.0;
		private const double TideAmplitudeMetres = 0.75;
		private const double TideMeanMetres = 0.15;
		private const double StreamMetresPerSecond = 1.9;
		private const double FloodTowardDegrees = 35.0;

		[Tooltip("The fake telemetry whose clock this follows. Empty: the first one in the scene.")]
		[SerializeField]
		private FakeTelemetrySource telemetry;

		[Tooltip("Count depth misses, to show the OFF DEPTH GRID warning.")]
		[SerializeField]
		private bool offDepthGrid;

		private EnvironmentKind _kind = EnvironmentKind.Calm;
		private string _packName = string.Empty;
		private TimeSpan _offset;
		private bool _windMissing;
		private DateTimeOffset _epochUtc;
		private long _runId = -1;
		private long _depthMisses;
		private double _lastTime = double.NaN;

		public FakeTelemetrySource Telemetry
		{
			get => telemetry;
			set => telemetry = value;
		}

		public bool OffDepthGrid
		{
			get => offDepthGrid;
			set => offDepthGrid = value;
		}

		public EnvironmentKind Kind => _kind;

		public bool HasEnvironment => telemetry != null && telemetry.HasTelemetry;

		public EnvironmentReadout Environment => HasEnvironment ? At(telemetry.State.Time) : default;

		/// <summary>Makes the following runs a pack-like day: its offset, its name and whether it has wind.</summary>
		public void StartDay(ScenarioDay day)
		{
			_kind = EnvironmentKind.Pack;
			_packName = day.PackName;
			_offset = day.LocalUtcOffset;
			_windMissing = day.WindMissing;
			var date = DateTime.ParseExact(day.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
			_epochUtc = new DateTimeOffset(date, day.LocalUtcOffset);
		}

		/// <summary>Makes the following runs calm: still water and air, no tide, a UTC clock.</summary>
		public void StartCalm()
		{
			_kind = EnvironmentKind.Calm;
			_packName = string.Empty;
			_offset = TimeSpan.Zero;
			_windMissing = false;
		}

		private void Awake()
		{
			if (telemetry == null)
			{
				telemetry = FindFirstObjectByType<FakeTelemetrySource>();
			}
		}

		private void Update()
		{
			if (!HasEnvironment)
			{
				return;
			}

			// Depth misses build up per 10 ms step while off the grid, and start over with each run.
			var time = telemetry.State.Time;
			if (telemetry.RunId != _runId)
			{
				_runId = telemetry.RunId;
				_depthMisses = 0;
				_lastTime = time;
			}

			if (offDepthGrid && time > _lastTime)
			{
				_depthMisses += Math.Max(1L, (long)Math.Round((time - _lastTime) * SimulationClock.StepsPerSecond));
			}

			_lastTime = time;
		}

		private EnvironmentReadout At(double time)
		{
			if (_kind != EnvironmentKind.Pack)
			{
				return new EnvironmentReadout(EnvironmentKind.Calm, null, null, TimeSpan.Zero, double.NaN, double.NaN,
					HorizontalVector.Zero, HorizontalVector.Zero, false, 0, _depthMisses);
			}

			var packTime = (telemetry.ScenarioStartUtc - _epochUtc).TotalSeconds + time;
			var held = Math.Max(CoverageStartSeconds, Math.Min(CoverageEndSeconds, packTime));
			var holds = packTime > CoverageEndSeconds ? (long)Math.Round((packTime - CoverageEndSeconds) * SimulationClock.StepsPerSecond) : 0L;
			var phase = 2.0 * Math.PI * held / TidePeriodSeconds;
			var level = TideMeanMetres + TideAmplitudeMetres * Math.Cos(phase);
			var rate = packTime > CoverageEndSeconds ? 0.0 : -TideAmplitudeMetres * Math.Sin(phase) * 2.0 * Math.PI / TidePeriodSeconds;
			// Strongest flood on the rising tide, slack at high and low water; a small cross-stream part keeps the set off the axis.
			var stream = -StreamMetresPerSecond * Math.Sin(phase);
			var current = HorizontalVector.FromSpeedToward(stream, Angles.ToRadians(FloodTowardDegrees)) +
				HorizontalVector.FromSpeedToward(0.08, Angles.ToRadians(FloodTowardDegrees + 90.0));
			var wind = _windMissing
				? HorizontalVector.Zero
				: HorizontalVector.FromSpeedFrom(6.0 + 1.5 * Math.Sin(held / 1800.0), Angles.ToRadians(240.0 + 15.0 * Math.Sin(held / 2700.0)));
			return new EnvironmentReadout(EnvironmentKind.Pack, _packName, null, _offset, level, rate, current, wind, _windMissing, holds, _depthMisses);
		}
	}
}
