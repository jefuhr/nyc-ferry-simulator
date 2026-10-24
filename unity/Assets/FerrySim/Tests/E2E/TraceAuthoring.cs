using System;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using FerrySim.Slice;
using UnityEngine;

namespace FerrySim.E2E
{
	/// <summary>
	/// Test/editor-only offline controller. It never runs during acceptance: acceptance reads the committed CSV.
	/// Positions are route-authoring estimates in shared NED metres; the initial/arrival poses follow real faces.
	/// Every command is sampled on the exact 100 Hz clock and rounded to hundredths for a reviewable fixture.
	/// </summary>
	public static class TraceAuthoring
	{
		public static void Generate()
		{
			GeneratePier11Dumbo();
			GenerateDumboSouthWilliamsburg();
		}

		public static void GeneratePier11Dumbo() => Generate(TripData.Pier11Dumbo);
		public static void GenerateDumboSouthWilliamsburg() => Generate(TripData.DumboSouthWilliamsburg);

		private static void Generate(TripLeg leg)
		{
			var vessel = TripData.Vessel();
			var landings = TripData.Landings();
			var scenario = TripData.Start(leg, landings, vessel);
			var environment = scenario.environment.Create();
			var initial = scenario.InitialState(environment);
			var simulation = (VesselDynamicsSimulation)new VesselDynamicsFactory().Create(vessel, environment, landings.Geometry, initial);
			var arrival = landings.PrimaryFace(leg.ArrivalStop);
			var normal = TripData.Normal(arrival);
			var middle = TripData.Middle(arrival);
			// Estimated calm-water authoring speeds: 7 m/s (~13.6 kn) for transit, 1.5 m/s backing.
			// Leg 1 crosses directly. Leg 2 follows the river through the two World's span centres.
			var points = leg.RequiredBridges.Length == 0 ? new[] { middle + normal * 70 } : new[]
			{
				new HorizontalVector(650, 1560),
				new HorizontalVector(820, 2050),
				new HorizontalVector(1120, 3300),
				middle + normal * 90
			};
			var trace = new ControlTrace();
			var phase = -1;
			long contactStep = -1;
			var previous = ControlInputs.Neutral;
			ControlInputs controls = ControlInputs.Neutral;
			Directory.CreateDirectory(leg.Artifacts);
			using (var log = new StreamWriter(Path.Combine(leg.Artifacts, "trace-authoring.csv")))
			{
				log.WriteLine("time_s,phase,north_m,east_m,heading_deg,surge_mps,sway_mps,yaw_rate,contact_n,clearance_m,cross_m,port,starboard,helm");
				for (long step = 0; step < 240000; step++)
				{
					var state = simulation.State;
					var position = new HorizontalVector(state.North, state.East);
					var fromFace = position - middle;
					var distance = Dot(fromFace, normal) - 0.5 * vessel.Particulars.LengthOverall.Value;
					var cross = fromFace.North * -normal.East + fromFace.East * normal.North;
					if (simulation.ContactCount > 0 && contactStep < 0)
					{
						contactStep = step;
						Debug.Log($"authoring contact at {state.Time:F2}s, {simulation.Telemetry.SpeedOverGround:F4} m/s, face {simulation.GetContact(0).SegmentId}");
					}
					if (step % 10 == 0)
					{
						if (contactStep >= 0) controls = new ControlInputs(0.15, 0.15, 0);
						else if (phase < 0)
						{
							var backed = -(state.North - initial.North) * Math.Cos(initial.Heading) - (state.East - initial.East) * Math.Sin(initial.Heading);
							controls = Navigate(state, initial.Heading, -1.5);
							if (backed >= 25) { phase = 0; Debug.Log($"{leg.Key}: departed at {state.Time:F2}s"); }
						}
						else if (phase < points.Length)
						{
							var delta = points[phase] - position;
							var heading = Math.Atan2(delta.East, delta.North);
							var last = phase == points.Length - 1;
							var speed = last ? Math.Min(7.0, Math.Sqrt(0.22 * delta.Magnitude)) : 7.0;
							if (Math.Abs(Angles.WrapPi(heading - state.Heading)) > 0.45) speed = 0;
							controls = Navigate(state, heading, speed);
							if (delta.Magnitude < (last ? 12 : 30) && (!last || Math.Abs(state.Surge) < 1.1))
							{
								Debug.Log($"{leg.Key}: waypoint {phase} at {state.Time:F2}s N{state.North:F2} E{state.East:F2}");
								phase++;
							}
						}
						else
						{
							var heading = Math.Atan2(-normal.East, -normal.North) + Math.Atan2(cross, Math.Max(25, distance));
							var speed = distance > 20 ? 1.5 : 0.48;
							if (Math.Abs(Angles.WrapPi(heading - state.Heading)) > 0.25) speed = 0;
							controls = Navigate(state, heading, speed);
						}
						controls = FixedStepDriver.Canonical(controls);
					}
					if (step == 0 || !controls.Equals(previous)) trace.Append(step, controls);
					previous = controls;
					simulation.Step(controls);
					if (step % 100 == 0)
						log.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F5},{8:F1},{9:F4},{10:F4},{11},{12},{13}",
							state.Time, phase, state.North, state.East, Angles.ToDegrees(state.Heading), state.Surge, state.Sway, state.YawRate,
							simulation.Telemetry.ContactForce, distance, cross, controls.PortLever, controls.StarboardLever, controls.Helm));
					if (!simulation.State.IsFinite()) throw new InvalidOperationException("Non-finite authoring state");
					if (contactStep >= 0 && step >= contactStep + 6000)
					{
						trace.Append(step + 1, controls);
						using (var writer = new StreamWriter(leg.TracePath))
							trace.Write(writer, new[] { "Offline TraceAuthoring.Generate, calm 50 m depth, T-class, 100 Hz.",
								$"{leg.Key}: bow-on 4 m clear of stop {leg.DepartureStop} primary face; astern departure, {(leg.RequiredBridges.Length == 0 ? "direct crossing without bridges" : "Brooklyn Bridge then Manhattan Bridge main spans")}, bow-in approach to stop {leg.ArrivalStop} primary face and 60 s engine hold." });
						Debug.Log($"{leg.Key}: authored {trace.Count} control changes, {trace.EndTime:F2}s, final contact {simulation.Telemetry.ContactForce:F1}N, speed {simulation.Telemetry.SpeedOverGround:F6}m/s");
						return;
					}
				}
			}
			throw new InvalidOperationException(leg.Key + ": trace authoring did not land in 2400 s; inspect its trace-authoring.csv");
		}

		private static ControlInputs Navigate(in VesselState state, double heading, double targetSpeed)
		{
			var error = Angles.WrapPi(heading - state.Heading);
			var common = Clamp(0.4 * (targetSpeed - state.Surge) + 0.02 * targetSpeed, -0.45, 0.6);
			var twist = Clamp(1.3 * error - 7.0 * state.YawRate, -0.35, 0.35);
			var helm = Clamp(2.0 * error - 5.0 * state.YawRate, -1, 1);
			return new ControlInputs(Round(common + twist), Round(common - twist), Round(helm));
		}

		private static double Dot(HorizontalVector a, HorizontalVector b) => a.North * b.North + a.East * b.East;
		private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));
		private static double Round(double value) => Math.Round(Clamp(value, -1, 1), 2, MidpointRounding.AwayFromZero);
	}
}
