using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (b) A contact against a real landing face that lasts one 10 ms physics step, with every rendered frame
	/// skipping several steps, is latched by the bridge and shown by the panel's contact indicator. Failure modes
	/// guarded: the indicator sampling the contact force at frame time (it would miss the touch), the latch counting
	/// a touch the core never had or losing one it had, and a touch against the wrong face.
	/// <para>
	/// A one-step touch needs the touching part of the hull to leave the face within a step: the hull drifts with a
	/// uniform current along South Williamsburg's face toward its open end and onto it, so the aft end of the
	/// starboard sheer clips the last millimetres of the face and slides off its end. The overlap is searched with
	/// the core alone, from the same scenario settings the runner uses, so the runner must reproduce the same step.
	/// </para>
	/// </summary>
	public class IntegratedContactLatchTests
	{
		private const double AlongKnots = 2.0;
		private const double TowardKnots = 0.8;
		private const double StartGap = 2.0;

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
		public IEnumerator AOneStepTouchBetweenFrames_IsLatched_AndShownByTheContactIndicator()
		{
			var face = _scene.ContactTestFace();
			var graze = FindOneStepGraze(face);
			Debug.Log($"One-step graze at {face.StopName} face {face.Id}: overlap {graze.Overlap * 1000.0:F1} mm, contact at step {graze.Step}, {graze.Force:F1} N.");

			// Frames of n steps with the touch strictly inside one, never on the frame's last step.
			var stepsPerFrame = Enumerable.Range(5, 6).First(n => graze.Step % n != n - 1);
			_scene.StepsPerFrame(stepsPerFrame);
			var log = new ContactLog(() => (VesselDynamicsSimulation)_scene.Runner.Driver.Simulation);
			_scene.Restart(graze.Scenario, new ConstantControls(ControlInputs.Neutral));
			_scene.Runner.Driver.AddObserver(log);
			Assert.That(_scene.Bootstrap.StartClearance.IsClear(), Is.True, "the drift starts clear of every face");
			try
			{
				var frameEnds = new List<long>();
				var forceAtFrameEnd = new List<double>();
				while (_scene.Runner.Driver.StepCount <= graze.Step + 3 * stepsPerFrame)
				{
					yield return null;
					frameEnds.Add(_scene.Runner.Driver.StepCount);
					forceAtFrameEnd.Add(_scene.Runner.Telemetry.ContactForce);
					if (frameEnds.Count > 2000)
					{
						Assert.Fail("the run did not reach the touch");
					}
				}

				var latch = _scene.Runner.Contact;
				Assert.That(log.ContactSteps, Is.EqualTo(new[] { graze.Step }), "the runner reproduces the core's one-step touch");
				Assert.That(log.SegmentsTouched, Is.EquivalentTo(new[] { face.Id }), "against South Williamsburg's face only");
				Assert.That(latch.StepsWithContact, Is.EqualTo(1));
				Assert.That(latch.LastContactStep, Is.EqualTo(graze.Step));
				Assert.That(latch.PeakContactForce, Is.EqualTo(graze.Force));

				var frame = frameEnds.FindIndex(end => end > graze.Step);
				var previousEnd = frame > 0 ? frameEnds[frame - 1] : 0;
				Assert.That(frameEnds[frame] - previousEnd, Is.GreaterThan(1), "the frame ran several steps");
				Assert.That(frameEnds[frame] - 1, Is.GreaterThan(graze.Step), "the touch ended inside the frame");
				Assert.That(forceAtFrameEnd.All(force => force == 0.0), Is.True, "no rendered frame ever saw contact force");

				var panel = _scene.Panel;
				Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent), "shown as a touch that has ended");
				Assert.That(panel.Contact.Value.text, Is.EqualTo("PEAK " + InstrumentFormat.Force(graze.Force) + " kN"));
				Assert.That(panel.ContactMonitor.PeakNewtons, Is.EqualTo(graze.Force));
			}
			finally
			{
				_scene.Runner.Driver.RemoveObserver(log);
			}
		}

		private sealed class GrazeResult
		{
			public ScenarioSettings Scenario;
			public double Overlap;
			public long Step;
			public double Force;
		}

		/// <summary>The one-step touch with the most force among overlaps of 0.5 to 20 mm.</summary>
		private GrazeResult FindOneStepGraze(LandingSegment face)
		{
			GrazeResult best = null;
			for (var overlapMillimetres = 0.5; overlapMillimetres <= 20.0; overlapMillimetres += 0.5)
			{
				var scenario = Drift(face, overlapMillimetres / 1000.0);
				var environment = scenario.environment.Create();
				var simulation = new VesselDynamicsFactory().Create(_scene.Vessel, environment, _scene.Landings.Geometry, scenario.InitialState(environment));
				var steps = new List<long>();
				var peak = 0.0;
				var end = (long)((StartGap / Units.KnotsToMetresPerSecond(TowardKnots) + 3.0) * SimulationClock.StepsPerSecond);
				for (long step = 0; step < end; step++)
				{
					simulation.Step(ControlInputs.Neutral);
					if (simulation.Telemetry.ContactForce > 0.0)
					{
						steps.Add(step);
						peak = Math.Max(peak, simulation.Telemetry.ContactForce);
					}
				}

				if (steps.Count == 1 && (best == null || peak > best.Force))
				{
					best = new GrazeResult { Scenario = scenario, Overlap = overlapMillimetres / 1000.0, Step = steps[0], Force = peak };
				}
			}

			Assert.That(best, Is.Not.Null, "no overlap from 0.5 to 20 mm gives a one-step touch at this face");
			return best;
		}

		/// <summary>
		/// At rest in the water, heading along the face (start to end, starboard side toward the structure), drifting
		/// with a current toward the face's end and onto it, so that when the starboard sheer reaches the face line its
		/// aft end overlaps the face by <paramref name="overlap"/> metres.
		/// </summary>
		private ScenarioSettings Drift(LandingSegment face, double overlap)
		{
			var outline = FaceClearance.BodyOutline(_scene.Vessel);
			var sheer = outline.Max(p => p.East);
			var sheerAft = outline.Where(p => p.East > sheer - 1e-9).Min(p => p.North);
			var segment = face.Segment;
			var along = segment.Direction;
			var normal = segment.WaterNormal;
			var alongSpeed = Units.KnotsToMetresPerSecond(AlongKnots);
			var towardSpeed = Units.KnotsToMetresPerSecond(TowardKnots);
			var crossing = StartGap / towardSpeed;
			var startAlong = segment.Length - overlap - sheerAft - alongSpeed * crossing;
			var startOut = sheer + StartGap;
			var start = LocalTangentPlane.World.ToGeodetic(
				segment.Start.North + along.North * startAlong + normal.North * startOut,
				segment.Start.East + along.East * startAlong + normal.East * startOut);
			var current = along * alongSpeed - normal * towardSpeed;
			return new ScenarioSettings
			{
				name = "one-step graze",
				vesselFile = _scene.Runner.Scenario?.vesselFile ?? "vessels/t-class.json",
				startLatitude = start.LatitudeDegrees,
				startLongitude = start.LongitudeDegrees,
				startHeadingDegrees = Angles.ToDegrees(Angles.WrapTwoPi(Math.Atan2(along.East, along.North))),
				startSpeedKnots = 0.0,
				scenarioStartUtc = "2026-06-15T16:00:00Z",
				environment = new ScenarioEnvironment
				{
					calm = false,
					currentSpeedKnots = Units.MetresPerSecondToKnots(current.Magnitude),
					currentTowardDegrees = Angles.ToDegrees(Angles.WrapTwoPi(Math.Atan2(current.East, current.North))),
					windSpeedKnots = 0.0,
					windFromDegrees = 0.0,
					depthMetres = 50.0,
				},
			};
		}
	}
}
