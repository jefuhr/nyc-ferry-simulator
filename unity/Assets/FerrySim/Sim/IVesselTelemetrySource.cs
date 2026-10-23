using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// What instruments, cameras and debrief tools read from a running vessel. The simulation bridge
	/// implements it; UI development scenes can use a fake. All values are SI and follow
	/// docs/conventions.md; convert to knots, rpm and degrees only for display.
	/// </summary>
	public interface IVesselTelemetrySource
	{
		/// <summary>Parameters of the vessel being simulated (display name, particulars).</summary>
		VesselParameters Parameters { get; }

		/// <summary>Physics state at the end of the most recent fixed step.</summary>
		VesselState State { get; }

		/// <summary>Telemetry from the most recent fixed step.</summary>
		VesselTelemetry Telemetry { get; }

		/// <summary>Operator inputs applied in the most recent fixed step, after device mapping.</summary>
		ControlInputs Controls { get; }

		/// <summary>Scenario start in UTC. Clock time is this plus <see cref="VesselState.Time"/> seconds.</summary>
		DateTimeOffset ScenarioStartUtc { get; }
	}
}
