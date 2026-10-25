using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// A hand-driven <see cref="IEnvironmentReadoutSource"/>: tests put it in any state the bridge can be in (no data
	/// yet, a calm, uniform or pack run, a day without wind, coverage misses, a run that has ended).
	/// </summary>
	internal sealed class StubEnvironmentSource : IEnvironmentReadoutSource
	{
		public bool HasEnvironment { get; set; }

		public EnvironmentReadout Environment { get; set; }

		public static StubEnvironmentSource With(EnvironmentReadout readout) => new StubEnvironmentSource { HasEnvironment = true, Environment = readout };
	}

	/// <summary>Readouts for tests, in the units the contract uses (SI, north and east, toward).</summary>
	internal static class Readouts
	{
		public const string PackName = "lower-east-river/2025-07-14";
		public const string PackDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

		public static HorizontalVector CurrentToward(double knots, double degrees) =>
			HorizontalVector.FromSpeedToward(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		public static HorizontalVector WindFrom(double knots, double degrees) =>
			HorizontalVector.FromSpeedFrom(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		/// <summary>A summer pack day: 1.0 ft above NAVD88 and rising 0.3 m an hour, 1.8 kn setting 047°T, wind from 250°T at 12 kn.</summary>
		public static EnvironmentReadout Pack(double levelMetres = 0.3048, double rateMetresPerSecond = 0.3 / 3600.0, HorizontalVector? current = null,
			HorizontalVector? wind = null, bool windMissing = false, long timeHolds = 0, long depthMisses = 0, double offsetHours = -4.0) =>
			new EnvironmentReadout(EnvironmentKind.Pack, PackName, PackDigest, TimeSpan.FromHours(offsetHours), levelMetres, rateMetresPerSecond,
				current ?? CurrentToward(1.8, 47.0), windMissing ? HorizontalVector.Zero : wind ?? WindFrom(12.0, 250.0), windMissing, timeHolds, depthMisses);

		public static EnvironmentReadout Calm() =>
			new EnvironmentReadout(EnvironmentKind.Calm, null, null, TimeSpan.Zero, double.NaN, double.NaN, HorizontalVector.Zero, HorizontalVector.Zero, false, 0, 0);

		public static EnvironmentReadout Uniform(HorizontalVector current, HorizontalVector wind) =>
			new EnvironmentReadout(EnvironmentKind.Uniform, null, null, TimeSpan.Zero, double.NaN, double.NaN, current, wind, false, 0, 0);
	}
}
