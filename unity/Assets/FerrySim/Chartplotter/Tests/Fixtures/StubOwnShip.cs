using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.Chartplotter.Tests
{
	/// <summary>An own-ship source the tests drive by hand: a planar state at a time, with the T-class particulars when the vessel file loads.</summary>
	public sealed class StubOwnShip : MonoBehaviour, IVesselTelemetrySource
	{
		public VesselParameters Parameters { get; set; }

		public VesselState State { get; set; }

		public VesselTelemetry Telemetry { get; set; }

		public ControlInputs Controls { get; set; } = ControlInputs.Neutral;

		public DateTimeOffset ScenarioStartUtc { get; set; } = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);

		public bool HasTelemetry { get; set; }

		public long RunId { get; set; } = 1;

		public ContactLatch Contact { get; set; } = ContactLatch.None;

		public void LoadVessel(string id = "t-class")
		{
			Parameters = VesselFiles.TryLoad(id, out var problem);
			if (Parameters == null)
			{
				Debug.LogWarning("StubOwnShip: " + problem);
			}
		}

		/// <summary>Places the vessel under way: the heading in degrees true and the speed in knots along it.</summary>
		public void Place(double time, double north, double east, double headingDegrees, double speedKnots)
		{
			State = VesselState.Planar(time, north, east, Angles.WrapTwoPi(Angles.ToRadians(headingDegrees)), Units.KnotsToMetresPerSecond(speedKnots));
			HasTelemetry = true;
		}
	}
}
