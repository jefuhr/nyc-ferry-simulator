using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Stands in for the simulation bridge in a scene, so <see cref="WheelhouseUI"/> finds it by itself. As
	/// added it is a runner whose initialization failed: no telemetry, every value at its default and a
	/// scenario start that was never set.
	/// </summary>
	public sealed class StubRunner : MonoBehaviour, IVesselTelemetrySource
	{
		public VesselParameters Parameters { get; set; }

		public VesselState State { get; set; }

		public VesselTelemetry Telemetry { get; set; }

		public ControlInputs Controls { get; set; }

		public DateTimeOffset ScenarioStartUtc { get; set; }

		public bool HasTelemetry { get; set; }

		public long RunId { get; set; }

		public ContactLatch Contact { get; set; } = ContactLatch.None;
	}
}
