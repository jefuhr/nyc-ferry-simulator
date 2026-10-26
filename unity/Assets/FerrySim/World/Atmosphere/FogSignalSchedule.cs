using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.World
{
	/// <summary>The sound signals of Rule 35 a power-driven vessel gives in restricted visibility.</summary>
	public enum FogSignalKind
	{
		/// <summary>Rule 35(a): making way through the water, one prolonged blast.</summary>
		OneProlonged = 0,

		/// <summary>Rule 35(b): underway but stopped and making no way, two prolonged blasts about 2 seconds apart.</summary>
		TwoProlonged = 1,
	}

	/// <summary>One fog signal: the physics step and simulation time it started at, and which signal it was.</summary>
	public readonly struct FogSignalEvent : IEquatable<FogSignalEvent>
	{
		public FogSignalEvent(long step, FogSignalKind kind)
		{
			Step = step;
			Kind = kind;
		}

		public long Step { get; }

		/// <summary>Simulation seconds since the run started, from the step (<see cref="SimulationClock.TimeAtStep"/>).</summary>
		public double Time => SimulationClock.TimeAtStep(Step);

		public FogSignalKind Kind { get; }

		public bool Equals(FogSignalEvent other) => Step == other.Step && Kind == other.Kind;

		public override bool Equals(object obj) => obj is FogSignalEvent other && Equals(other);

		public override int GetHashCode() => unchecked((int)Step * 397) ^ (int)Kind;

		public override string ToString() => $"{Kind} at step {Step} ({Time:F2} s)";
	}

	/// <summary>
	/// When the learner's vessel sounds its fog signal and what it sounds, by the run's physics steps (so a replay, or the
	/// same run at another frame rate, sounds the same signals at the same times). Inland Rule 35: "In or near an area of
	/// restricted visibility, whether by day or night ... (a) A power-driven vessel making way through the water shall
	/// sound, at intervals of not more than 2 minutes, one prolonged blast. (b) A power-driven vessel underway but stopped
	/// and making no way through the water shall sound, at intervals of not more than 2 minutes, two prolonged blasts in
	/// succession, with an interval of about 2 seconds between them." Rule 32(c): "a prolonged blast means a blast of from 4
	/// to 6 seconds' duration." The first signal sounds as the run starts; the kind is chosen from the speed through the
	/// water at the signal's step.
	/// </summary>
	public static class FogSignalSchedule
	{
		/// <summary>From the start of one signal to the start of the next: Rule 35's longest interval, seconds.</summary>
		public const double IntervalSeconds = 120.0;

		/// <summary>Rule 32(c): 4 to 6 seconds; the middle.</summary>
		public const double ProlongedBlastSeconds = 5.0;

		/// <summary>Rule 35(b): about 2 seconds between the two blasts.</summary>
		public const double TwoBlastGapSeconds = 2.0;

		/// <summary>
		/// The whistle's fundamental, Hz: inside Annex III's band for a vessel under 75 m (33 CFR 86.01(b)(iii), 250 to
		/// 700 Hz). The real boats' whistles are a crew question.
		/// </summary>
		public const double WhistleHertz = 350.0;

		/// <summary>Making way through the water: 0.2 kn (an estimate; a crew question). Sternway counts.</summary>
		public const double MakingWayKnots = 0.2;

		/// <summary>Physics steps from one signal to the next.</summary>
		public static readonly long IntervalSteps = (long)Math.Round(IntervalSeconds * SimulationClock.StepsPerSecond);

		/// <summary>True when a signal starts at <paramref name="step"/> of a run with <paramref name="visibilityMetres"/>.</summary>
		public static bool IsSignalStep(long step, double visibilityMetres) =>
			MeteorologicalVisibility.IsRestricted(visibilityMetres) && step >= 0 && step % IntervalSteps == 0;

		/// <summary>
		/// Rule 35(a) when making way through the water, else 35(b). Decided on the speed through the water (m/s) as the
		/// recording writes it, knots to four decimals (<c>stw_kn</c>), so <see cref="Recompute"/> decides the same from the
		/// recording. Formats one number; called once a signal.
		/// </summary>
		public static FogSignalKind KindFor(double speedThroughWater) =>
			KindForRecordedKnots(double.Parse(Units.MetresPerSecondToKnots(speedThroughWater).ToString("F4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

		/// <summary>Rule 35(a) or (b) for a speed through the water as recorded, knots.</summary>
		public static FogSignalKind KindForRecordedKnots(double knots) =>
			Math.Abs(knots) >= MakingWayKnots ? FogSignalKind.OneProlonged : FogSignalKind.TwoProlonged;

		/// <summary>
		/// The signals a recorded run sounded, worked out again from its recording alone: its visibility (<c>visibility_m</c>)
		/// and, at each signal's step, the speed through the water its telemetry shows (<c>stw_kn</c>). The same as the run's
		/// <see cref="FogSignalRecorder"/> recorded, so a debrief needs no record of its own. Every signal step is a telemetry
		/// row at any sample rate that divides 100 Hz; the row at the run's end step is skipped, since no step started there.
		/// </summary>
		public static List<FogSignalEvent> Recompute(RecordedRun run)
		{
			if (run == null)
			{
				throw new ArgumentNullException(nameof(run));
			}

			var signals = new List<FogSignalEvent>();
			if (!MeteorologicalVisibility.IsRestricted(run.VisibilityMetres))
			{
				return signals;
			}

			var timeColumn = Array.IndexOf(TelemetryCsv.ColumnNames, "time_s");
			var speedColumn = Array.IndexOf(TelemetryCsv.ColumnNames, "stw_kn");
			foreach (var row in run.Rows)
			{
				var fields = row.Split(',');
				var step = (long)Math.Round(double.Parse(fields[timeColumn], CultureInfo.InvariantCulture) * SimulationClock.StepsPerSecond);
				if (step < run.StepCount && IsSignalStep(step, run.VisibilityMetres))
				{
					signals.Add(new FogSignalEvent(step, KindForRecordedKnots(double.Parse(fields[speedColumn], CultureInfo.InvariantCulture))));
				}
			}

			return signals;
		}

		/// <summary>Whether the whistle sounds <paramref name="secondsSinceSignal"/> after a signal of <paramref name="kind"/> started.</summary>
		public static bool IsBlasting(FogSignalKind kind, double secondsSinceSignal)
		{
			if (!(secondsSinceSignal >= 0.0))
			{
				return false;
			}

			if (secondsSinceSignal < ProlongedBlastSeconds)
			{
				return true;
			}

			var second = ProlongedBlastSeconds + TwoBlastGapSeconds;
			return kind == FogSignalKind.TwoProlonged && secondsSinceSignal >= second && secondsSinceSignal < second + ProlongedBlastSeconds;
		}

		/// <summary>How long a signal of <paramref name="kind"/> sounds from its start to the end of its last blast, seconds.</summary>
		public static double Duration(FogSignalKind kind) =>
			kind == FogSignalKind.TwoProlonged ? 2.0 * ProlongedBlastSeconds + TwoBlastGapSeconds : ProlongedBlastSeconds;
	}

	/// <summary>
	/// Watches a run's physics steps and records each fog signal as it falls due (<see cref="FogSignalSchedule"/>). Add it
	/// to the run's <see cref="FixedStepDriver"/>; it reads the speed through the water before the signal's step, so its
	/// record is the same for every replay of the run, and <see cref="FogSignalSchedule.Recompute"/> gives it again from
	/// the recording. Step 0 of a new run clears the record. Allocation-free on every step but a signal's (one formatted
	/// number) while the record stays within its first capacity (64 signals, over two hours).
	/// </summary>
	public sealed class FogSignalRecorder : IStepObserver
	{
		private readonly List<FogSignalEvent> _events = new List<FogSignalEvent>(64);

		public FogSignalRecorder(double visibilityMetres)
		{
			VisibilityMetres = visibilityMetres;
		}

		/// <summary>The run's visibility, metres (<see cref="MeteorologicalVisibility"/>); set it before the run's first step.</summary>
		public double VisibilityMetres { get; set; }

		/// <summary>Every signal of the current run so far, in order.</summary>
		public IReadOnlyList<FogSignalEvent> Events => _events;

		/// <summary>Raised with each signal as its step starts.</summary>
		public event Action<FogSignalEvent> Signalled;

		public void Clear() => _events.Clear();

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			if (step == 0)
			{
				_events.Clear();
			}

			if (!FogSignalSchedule.IsSignalStep(step, VisibilityMetres))
			{
				return;
			}

			var signal = new FogSignalEvent(step, FogSignalSchedule.KindFor(telemetry.SpeedThroughWater));
			_events.Add(signal);
			Signalled?.Invoke(signal);
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
		}
	}
}
