using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// What the wheelhouse panel and the HUD read about the water and the air at the vessel. The simulation
	/// bridge implements it next to <see cref="IVesselTelemetrySource"/>; UI development scenes and tests use a
	/// fake. Values are SI and follow docs/conventions.md: the current is the velocity of the water (the
	/// direction it flows toward) and the wind the velocity of the air (the direction it blows toward), both as
	/// north and east components. Convert to knots and degrees only for display.
	/// </summary>
	public interface IEnvironmentReadoutSource
	{
		/// <summary>
		/// True only while <see cref="Environment"/> describes the current run at its most recent physics step.
		/// False before the first step, after the run ends, and whenever the source has no current data.
		/// </summary>
		bool HasEnvironment { get; }

		/// <summary>The water and the air at the vessel's reference point at the most recent physics step.</summary>
		EnvironmentReadout Environment { get; }
	}

	/// <summary>Where a run's water and air come from.</summary>
	public enum EnvironmentKind
	{
		/// <summary>Still water and air, 50 m deep.</summary>
		Calm = 0,

		/// <summary>One current, wind and depth everywhere and at all times, from the scenario.</summary>
		Uniform = 1,

		/// <summary>A real day from an environment pack (ferrysim-env/1).</summary>
		Pack = 2,
	}

	/// <summary>A snapshot of the water and the air at the vessel. Immutable; copying it allocates nothing.</summary>
	public readonly struct EnvironmentReadout
	{
		public EnvironmentReadout(EnvironmentKind kind, string packName, string packDigest, TimeSpan localUtcOffset,
			double waterLevelNavd88, double waterLevelRate, HorizontalVector current, HorizontalVector wind, bool windMissing,
			long timeHolds, long depthMisses)
		{
			Kind = kind;
			PackName = packName ?? string.Empty;
			PackDigest = packDigest ?? string.Empty;
			LocalUtcOffset = localUtcOffset;
			WaterLevelNavd88 = waterLevelNavd88;
			WaterLevelRate = waterLevelRate;
			Current = current;
			Wind = wind;
			WindMissing = windMissing;
			TimeHolds = timeHolds;
			DepthMisses = depthMisses;
		}

		public EnvironmentKind Kind { get; }

		/// <summary>The pack as region/date, for example <c>lower-east-river/2025-07-14</c>. Empty unless <see cref="Kind"/> is Pack.</summary>
		public string PackName { get; }

		/// <summary>The pack's day digest, 64 lower-case hex characters. Empty unless <see cref="Kind"/> is Pack.</summary>
		public string PackDigest { get; }

		/// <summary>
		/// America/New_York's offset from UTC on the scenario's day (-4 h or -5 h), taken from the pack's local-midnight
		/// epoch, so displays can show local time. Zero when <see cref="Kind"/> is not Pack: show UTC then.
		/// </summary>
		public TimeSpan LocalUtcOffset { get; }

		/// <summary>Water surface height above NAVD88 at the vessel, metres. NaN when the run has no tide (calm and uniform runs).</summary>
		public double WaterLevelNavd88 { get; }

		/// <summary>Rate of change of <see cref="WaterLevelNavd88"/>, metres per second, positive while the water rises. NaN with no tide.</summary>
		public double WaterLevelRate { get; }

		/// <summary>Velocity of the water at the vessel's reference point, m/s, north and east (the direction it flows toward).</summary>
		public HorizontalVector Current { get; }

		/// <summary>Velocity of the air, m/s, north and east (the direction it blows toward). Mean wind; gusts are not applied.</summary>
		public HorizontalVector Wind { get; }

		/// <summary>True when the pack's day has no wind data, so the wind reads as still air.</summary>
		public bool WindMissing { get; }

		/// <summary>Samples this run that fell outside the pack's time coverage and were held at its edge.</summary>
		public long TimeHolds { get; }

		/// <summary>Depth samples this run outside every grid or over unknown bed, where the pack's outside depth was used.</summary>
		public long DepthMisses { get; }
	}
}
