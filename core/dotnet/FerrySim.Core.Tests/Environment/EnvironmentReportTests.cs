using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Typical results of the environment model for both classes, written to <c>artifacts/core/environment-report.md</c>:
	/// drift next to a current station, the 5 kn shoal runs, the turn in a shear, and the cost of a step in each kind of
	/// environment. The physics is asserted in CurrentAlongHullTests and GroundingTests; this only reports. Timings are
	/// wall-clock on whatever machine runs the tests and are never asserted.
	/// </summary>
	[TestFixture]
	public class EnvironmentReportTests
	{
		private static readonly string[] Classes = { "t-class.json", "k-class.json" };

		[Test]
		public void WriteTheEnvironmentReport()
		{
			var report = new StringBuilder();
			report.Append("# Environment report\n\n");
			report.Append("Written by `EnvironmentReportTests` from synthetic packs (made-up values, not NOAA data). See core/FerrySim.Core/Environment/README.md.\n\n");

			report.Append("## Drift next to a current station\n\n");
			report.Append("Three stations 10 km apart, the first steady at 1.000 m/s toward 045°; engines in neutral.\n\n");
			report.Append("| Class | Moving with the water, after 300 s | From rest: 150 / 300 / 450 / 600 s |\n|---|---|---|\n");
			foreach (var file in Classes)
			{
				var parameters = ManeuverRunner.Load(file);
				var current = HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(45.0));
				var withWater = Drift(parameters, current, true, out _);
				Drift(parameters, current, false, out var fromRest);
				report.Append($"| {Name(file)} | {withWater.Magnitude:F4} m/s toward {Angles.ToDegrees(withWater.Direction):F2}° | " +
					$"{fromRest[0]:F3} / {fromRest[1]:F3} / {fromRest[2]:F3} / {fromRest[3]:F3} m/s |\n");
			}

			report.Append("\nWith the water, the drift is the field's own blend 300 m from the station (0.998 m/s there). From rest, the hull's ");
			report.Append("drag brings the boat up to the current; the surge part is slow because resistance at walking pace is small.\n\n");

			report.Append("## A 5 kn run onto a shoal\n\n");
			report.Append($"The bed rises from 6 m below the water to 1 m above it over 200 m (1 in 29), bed friction μ = {PhysicalConstants.BedFrictionCoefficient}. ");
			report.Append("Stopping distance and time run from the step the bed first touches the keel.\n\n");
			report.Append("| Class | Levers | Met the bed | Stopped in | Time | Peak bed force | Peak penetration |\n|---|---|---|---|---|---|---|\n");
			foreach (var file in Classes)
			{
				var parameters = ManeuverRunner.Load(file);
				foreach (var (name, lever) in new[] { ("ahead idle", ContactScenarios.IdleLever(parameters)), ("neutral", 0.0) })
				{
					var run = GroundingTests.RunOntoShoal(parameters, lever, out _);
					report.Append($"| {Name(file)} | {name} | {Units.MetresPerSecondToKnots(run.ImpactSpeed):F2} kn | {run.StoppingDistance:F2} m | {run.StoppingTime:F1} s | " +
						$"{run.PeakForce / 1000.0:F1} kN | {run.PeakPenetration:F3} m |\n");
				}
			}

			report.Append("\n## A current stronger at the bow\n\n");
			report.Append("The water's starboard velocity grows 0.02 m/s per metre forward; the boat starts with the water at its reference point, in neutral.\n\n");
			report.Append("| Class | Turned in 20 s | Yaw rate at 20 s |\n|---|---|---|\n");
			foreach (var file in Classes)
			{
				var parameters = ManeuverRunner.Load(file);
				var shear = new CurrentAlongHullTests.FieldEnvironment((north, east) => new HorizontalVector(0.0, 0.02 * north));
				var simulation = new VesselDynamicsSimulation(parameters, shear, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
				ManeuverRunner.Run(simulation, ControlInputs.Neutral, 20.0);
				report.Append($"| {Name(file)} | {Angles.ToDegrees(Angles.WrapPi(simulation.State.Heading)):F2}° to starboard | {Angles.ToDegrees(simulation.State.YawRate):F3} °/s |\n");
			}

			report.Append("\n## Cost of a step\n\n");
			report.Append("Fastest of seven interleaved rounds of 20 000 steps (after 3 000 steps of warm-up each), scripted inputs, T-class, ");
			report.Append("the test build on whatever machine ran it: compare rows, not machines.\n\n");
			report.Append("| Environment | Along the hull | µs per step |\n|---|---|---|\n");
			var t = ManeuverRunner.Load("t-class.json");
			var pack = CurrentAlongHullTests.ThreeStationPack(HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(45.0)));
			var cases = new (string Name, string Along, Func<IEnvironment> Environment, VesselState Initial, bool AlongHull)[]
			{
				("Uniform (calm)", "on", () => UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 6.0), true),
				("Uniform (calm)", "off (v0.1.0 model)", () => UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 6.0), false),
				("Pack, three-station blend", "on", () => new DataEnvironment(pack, PackFixtures.Epoch), VesselState.Planar(0.0, 300.0, 200.0, 0.0, surge: 6.0), true),
				("Pack, three-station blend", "off (reference point only)", () => new DataEnvironment(pack, PackFixtures.Epoch), VesselState.Planar(0.0, 300.0, 200.0, 0.0, surge: 6.0), false),
				("Uniform 1.5 m, aground", "on", () => new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, 1.5), VesselState.Planar(0.0, 0.0, 0.0, 0.0), true),
			};
			var fastest = new double[cases.Length];
			for (var i = 0; i < fastest.Length; i++)
			{
				fastest[i] = double.MaxValue;
			}

			for (var round = 0; round < 7; round++)
			{
				for (var i = 0; i < cases.Length; i++)
				{
					fastest[i] = Math.Min(fastest[i], StepCost(t, cases[i].Environment(), cases[i].Initial, cases[i].AlongHull));
				}
			}

			for (var i = 0; i < cases.Length; i++)
			{
				report.Append($"| {cases[i].Name} | {cases[i].Along} | {fastest[i]:F2} |\n");
			}

			report.Append($"\nAlong the hull, the varying field costs {fastest[2] - fastest[3]:F2} µs a step more than sampling the reference point only, ");
			report.Append($"and a step in it {fastest[2] - fastest[0]:F2} µs more than in calm water. A uniform environment takes no samples along the hull. ");
			report.Append("A step that starts aground takes ten substeps with the bed friction, like a step in fender contact.\n");

			var directory = Path.Combine(RepoPaths.Root, "artifacts", "core");
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, "environment-report.md");
			File.WriteAllText(path, report.ToString());
			TestContext.Out.WriteLine(report.ToString());
			Assert.That(File.Exists(path), Is.True);
		}

		private static HorizontalVector Drift(VesselParameters parameters, HorizontalVector current, bool withTheWater, out double[] speeds)
		{
			var environment = new DataEnvironment(CurrentAlongHullTests.ThreeStationPack(current), PackFixtures.Epoch.AddHours(10.0));
			var initial = withTheWater
				? VesselState.Planar(0.0, 20.0, -15.0, 0.0, current.North, current.East)
				: VesselState.Planar(0.0, 0.0, 0.0, 0.0);
			var simulation = new VesselDynamicsSimulation(parameters, environment, initial);
			speeds = new double[4];
			for (var i = 0; i < speeds.Length; i++)
			{
				ManeuverRunner.Run(simulation, ControlInputs.Neutral, withTheWater ? 75.0 : 150.0);
				speeds[i] = simulation.State.GroundVelocity.Magnitude;
			}

			return simulation.State.GroundVelocity;
		}

		// Microseconds per step of one timed run after a warm-up.
		private static double StepCost(VesselParameters parameters, IEnvironment environment, VesselState initial, bool alongHull)
		{
			var simulation = new VesselDynamicsSimulation(parameters, environment, initial) { UseCurrentAlongHull = alongHull };
			simulation.Reset(initial);
			for (var step = 0; step < 3000; step++)
			{
				simulation.Step(Inputs(step));
			}

			var watch = Stopwatch.StartNew();
			for (var step = 0; step < 20000; step++)
			{
				simulation.Step(Inputs(step));
			}

			watch.Stop();
			return watch.Elapsed.TotalMilliseconds * 1000.0 / 20000.0;
		}

		private static ControlInputs Inputs(int step)
		{
			var t = step / (double)SimulationClock.StepsPerSecond;
			return new ControlInputs(0.4 + 0.3 * Math.Sin(0.11 * t), 0.4 + 0.3 * Math.Cos(0.07 * t), Math.Sin(0.05 * t));
		}

		private static string Name(string file) => file.StartsWith("t", StringComparison.Ordinal) ? "T-class" : "K-class";
	}
}
