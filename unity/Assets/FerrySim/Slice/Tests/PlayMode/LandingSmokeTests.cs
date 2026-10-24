using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (d) Landing smoke at South Williamsburg (stop 8): scripted controls bring the T-class bow-on into the real
	/// face at about 1 kn, then hold it there ahead idle. Failure modes guarded: the hull passing through the
	/// landing (geometry not handed to the core, or a face pointing the wrong way), a fender that bottoms out at a
	/// gentle landing (wrong parameters), the boat bouncing off or sliding away instead of coming to rest on the face,
	/// and force from a face it never crossed. Writes artifacts/unity/slice/landing-smoke.md.
	/// </summary>
	public class LandingSmokeTests
	{
		private const double TargetKnots = 1.0;

		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator ScriptedControls_LandOnSouthWilliamsburg_AtAboutOneKnot_AndHoldThere()
		{
			var face = _scene.ContactTestFace();
			var landing = _scene.Landings.ForStop(SliceScene.ContactTestStopId).Select(s => s.Id).ToArray();
			var scenario = _scene.Approach(15.0, "landing smoke");
			var fender = _scene.Landings.Surfaces.For(SliceScene.ContactTestStopId);
			var controls = new LandingApproachControls(_scene.Runner, TargetKnots);
			var log = new ContactLog(() => (VesselDynamicsSimulation)_scene.Runner.Driver.Simulation);
			_scene.StepsPerFrame(50);
			_scene.Restart(scenario, controls);
			_scene.Runner.Driver.AddObserver(log);
			Assert.That(_scene.Bootstrap.StartClearance.IsClear(), Is.True, "the approach starts clear of every face");
			try
			{
				yield return _scene.FramesUntil(() => controls.HoldFromStep >= 0, 200, "the bow to touch the face");
				var holdFrom = _scene.Runner.Driver.StepCount;
				yield return _scene.FramesUntil(() => _scene.Runner.Driver.StepCount >= holdFrom + 45 * SimulationClock.StepsPerSecond, 200, "45 s on the face");

				var state = _scene.Runner.State;
				var telemetry = _scene.Runner.Telemetry;
				var impactKnots = Units.MetresPerSecondToKnots(controls.SpeedAtContact);
				var clearance = FaceClearance.Check(_scene.Vessel, state.North, state.East, state.Heading, new[] { face.Segment });
				var others = FaceClearance.Check(_scene.Vessel, state.North, state.East, state.Heading,
					_scene.Landings.Geometry.Segments.Where(s => !landing.Contains(s.Id)).ToArray());
				var headingChange = Angles.ToDegrees(Math.Abs(Angles.WrapPi(state.Heading - Angles.ToRadians(scenario.startHeadingDegrees))));
				var report = string.Format(CultureInfo.InvariantCulture,
					"# Landing smoke: {0} (stop {1}, face {2})\n\n" +
					"T-class bow-on from rest, bow 15 m off the face; ahead idle (lever {3}) on both engines below {4} kn, neutral above, ahead idle again from the first touch. Calm water.\n\n" +
					"| Quantity | Value |\n|---|---|\n" +
					"| Speed over ground at first touch | {5:F2} kn |\n| First touch | step {6} ({7:F2} s) |\n| Peak contact force | {8:F1} kN |\n" +
					"| Deepest compression, hull and landing fenders | {9:F1} mm (landing fender travel {10} m) |\n| A fender bottomed out | {11} |\n" +
					"| Faces touched | {12} |\n| After 45 s on the face: speed over ground | {13:F4} kn |\n| After 45 s: contact force | {14:F1} kN |\n" +
					"| After 45 s: heading change from the approach | {15:F2} deg |\n| Nearest other face | {16:F0} m |\n\n" +
					"Fender: '{17}' entry of {18} ({19}).\n",
					face.StopName, face.StopId, face.Id, LandingApproachControls.IdleAhead, TargetKnots, impactKnots, controls.HoldFromStep, controls.HoldFromStep / 100.0,
					log.PeakForce / 1000.0, log.DeepestCompression * 1000.0, fender.MaxCompression.Value, log.BottomedOut, string.Join(", ", log.SegmentsTouched),
					Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), telemetry.ContactForce / 1000.0, headingChange, others.Distance,
					fender.Key, _scene.Landings.Surfaces.File, fender.Confidence);
				Directory.CreateDirectory(Path.Combine(SliceScene.ArtifactDirectory, "slice"));
				File.WriteAllText(Path.Combine(SliceScene.ArtifactDirectory, "slice", "landing-smoke.md"), report);
				Debug.Log(report);

				Assert.That(impactKnots, Is.InRange(0.8, 1.2), "about 1 kn at the face");
				Assert.That(log.PeakForce, Is.GreaterThan(0.0), "the face pushed back");
				Assert.That(log.DeepestCompression, Is.GreaterThan(0.0).And.LessThanOrEqualTo(fender.MaxCompression.Value), "within the landing fender's travel");
				Assert.That(log.BottomedOut, Is.False, "no fender bottoms out at 1 kn");
				Assert.That(log.SegmentsTouched, Is.SubsetOf(landing), "no force from a face it never crossed");
				Assert.That(log.SegmentsTouched, Does.Contain(face.Id));
				Assert.That(Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), Is.LessThan(0.02), "stopped");
				Assert.That(telemetry.ContactForce, Is.GreaterThan(0.0), "held on the face");
				Assert.That(clearance.Overlaps, Is.True, "the bow is on the face it approached, its fender compressed");
				Assert.That(others.IsClear(), Is.True, "nowhere near another landing");
				Assert.That(headingChange, Is.LessThan(5.0), "still bow-on");
			}
			finally
			{
				_scene.Runner.Driver.RemoveObserver(log);
			}
		}

		/// <summary>Negative control: the same approach without the landing geometry runs straight through the face.</summary>
		[UnityTest]
		public IEnumerator WithoutTheGeometry_TheSameApproachPassesThroughTheFace()
		{
			var face = _scene.ContactTestFace();
			var scenario = _scene.Approach(15.0, "landing smoke, open water");
			var environment = scenario.environment.Create();
			var simulation = new VesselDynamicsFactory().Create(_scene.Vessel, environment, EmptyWorldGeometry.Instance, scenario.InitialState(environment));
			var controls = new FixedSpeedApproach(simulation, TargetKnots);
			for (long step = 0; step < 90 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(controls.ControlsForStep(step));
			}

			var state = simulation.State;
			var depthPastFace = -((state.North - face.Segment.Start.North) * face.Segment.WaterNormal.North + (state.East - face.Segment.Start.East) * face.Segment.WaterNormal.East);
			Assert.That(depthPastFace, Is.GreaterThan(5.0), "in open water the reference point ends up past the face line: only the geometry stops the boat");
			yield return null;
		}

		/// <summary>The same speed hold as <see cref="LandingApproachControls"/>, without the runner, never holding.</summary>
		private sealed class FixedSpeedApproach : IControlInputSource
		{
			private readonly ISimulation _simulation;
			private readonly double _target;
			private bool _coasting;

			public FixedSpeedApproach(ISimulation simulation, double knots)
			{
				_simulation = simulation;
				_target = Units.KnotsToMetresPerSecond(knots);
			}

			public ControlInputs ControlsForStep(long step)
			{
				var speed = _simulation.Telemetry.SpeedOverGround;
				_coasting = speed >= _target + Units.KnotsToMetresPerSecond(0.1) || (_coasting && speed >= _target - Units.KnotsToMetresPerSecond(0.05));
				return _coasting ? ControlInputs.Neutral : new ControlInputs(LandingApproachControls.IdleAhead, LandingApproachControls.IdleAhead, 0.0);
			}
		}
	}
}
