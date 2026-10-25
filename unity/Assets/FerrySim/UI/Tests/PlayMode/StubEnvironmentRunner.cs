using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Stands in for the simulation bridge's environment readout in a scene. As added it has no data; tests set
	/// <see cref="HasEnvironment"/> and <see cref="Environment"/> frame by frame.
	/// </summary>
	public sealed class StubEnvironmentRunner : MonoBehaviour, IEnvironmentReadoutSource
	{
		public bool HasEnvironment { get; set; }

		public EnvironmentReadout Environment { get; set; }
	}

	/// <summary>Readouts for PlayMode tests, in the contract's units (SI, north and east, toward).</summary>
	internal static class TestReadouts
	{
		public static HorizontalVector CurrentToward(double knots, double degrees) =>
			HorizontalVector.FromSpeedToward(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		public static HorizontalVector WindFrom(double knots, double degrees) =>
			HorizontalVector.FromSpeedFrom(Units.KnotsToMetresPerSecond(knots), Angles.ToRadians(degrees));

		public static EnvironmentReadout Pack(double levelMetres, double rateMetresPerHour, double driftKnots, double setDegrees, double windKnots,
			double windFromDegrees, bool windMissing = false, long timeHolds = 0, long depthMisses = 0, double offsetHours = -4.0) =>
			new EnvironmentReadout(EnvironmentKind.Pack, "lower-east-river/2025-07-14", null, TimeSpan.FromHours(offsetHours), levelMetres,
				rateMetresPerHour / 3600.0, CurrentToward(driftKnots, setDegrees), windMissing ? HorizontalVector.Zero : WindFrom(windKnots, windFromDegrees),
				windMissing, timeHolds, depthMisses);
	}
}
