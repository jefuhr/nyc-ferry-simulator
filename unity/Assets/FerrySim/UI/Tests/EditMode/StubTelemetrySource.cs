using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// A hand-driven <see cref="IVesselTelemetrySource"/> whose members are all settable, so tests can put it
	/// in any lifecycle state the real bridge can be in: not started, failed, running, ended or reset.
	/// </summary>
	internal sealed class StubTelemetrySource : IVesselTelemetrySource
	{
		public static readonly DateTimeOffset June15 = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);

		public VesselParameters Parameters { get; set; }

		public VesselState State { get; set; }

		public VesselTelemetry Telemetry { get; set; }

		public ControlInputs Controls { get; set; }

		public DateTimeOffset ScenarioStartUtc { get; set; }

		public bool HasTelemetry { get; set; }

		public long RunId { get; set; }

		public ContactLatch Contact { get; set; } = ContactLatch.None;

		/// <summary>
		/// A bridge whose run never started or failed to initialize: no telemetry, and every value at its
		/// default (zero speeds, neutral gears, zero depth, an unset scenario start).
		/// </summary>
		public static StubTelemetrySource NeverStarted() => new StubTelemetrySource { RunId = 1 };

		/// <summary>8 kn ahead on 045°T, both engines at 1200 rpm, at <paramref name="time"/> s into a run on 2026-06-15.</summary>
		public static StubTelemetrySource Underway(double time = 10.0, double depthUnderKeel = 12.0, double contactForce = 0.0, long runId = 1)
		{
			var source = new StubTelemetrySource { RunId = runId, ScenarioStartUtc = June15, HasTelemetry = true };
			source.Set(time, depthUnderKeel, contactForce);
			return source;
		}

		/// <summary>Moves the sample on to <paramref name="time"/> with the given depth and instantaneous contact force.</summary>
		public void Set(double time, double depthUnderKeel, double contactForce = 0.0)
		{
			var speed = Units.KnotsToMetresPerSecond(8.0);
			var heading = Angles.ToRadians(45.0);
			State = VesselState.Planar(time, 100.0, 200.0, heading, speed);
			var engine = new EngineTelemetry(Units.RpmToRadiansPerSecond(1200.0), Units.RpmToRadiansPerSecond(480.0), GearState.Ahead, double.NaN);
			Telemetry = new VesselTelemetry(engine, engine, 0.0, 0.0, speed, speed, heading, depthUnderKeel, contactForce);
			Controls = new ControlInputs(0.5, 0.5, 0.0);
		}
	}
}
