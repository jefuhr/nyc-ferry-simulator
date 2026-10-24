using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Contact starts only where the hull crosses a face from its water side, and stays with that face while
	/// the hull is behind it. Failure modes guarded:
	/// <list type="bullet">
	/// <item>The far face of a thin or closed structure taking hold of a hull that is clear of the near face
	/// and pushing it into the structure, even from rest.</item>
	/// <item>A hull point deep in a thin structure handed to the far face and pushed out through it.</item>
	/// <item>At 0 to 6 kn, head-on and 30° off the normal, into a thin wall or a barge: the hull passing the near
	/// face, the far face ever acting, or a contact force pulling the hull into the structure.</item>
	/// <item>A hull placed overlapping a structure being pushed by it, or contact surviving a reset.</item>
	/// <item>Contact lost or doubled where a face is made of several segments, or kept past the end of a face.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class ContactCaptureTests
	{
		private const double WallThickness = 0.4;
		private const double WallHalfLength = 100.0;

		private readonly string _vesselFile;
		private VesselParameters _parameters;
		private double _bow;
		private FenderLaw _law;

		public ContactCaptureTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		private static ContactSurface Fender => ContactScenarios.EstimatedBargeFender;

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
			_bow = ContactScenarios.BowX(_parameters);
			_law = new ContactModel(_parameters).LawFor(Fender);
		}

		[Test]
		public void AtRestClearOfAThinWall_TheHullFeelsNothing_AndMatchesOpenWater()
		{
			// Review finding 1: 0.2 m short of a 0.4 m wall the far face used to push the boat north with 37 MN.
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.0);
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(ThinWall(_bow + 0.2)), initial);
			var openWater = ManeuverRunner.Create(_parameters, null, initial);
			Assert.That(simulation.ContactCount, Is.EqualTo(0), "clear of the wall at reset");
			Assert.That(simulation.ContactLoad.Magnitude, Is.EqualTo(0.0));
			for (var step = 0; step < 20 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				openWater.Step(ControlInputs.Neutral);
				Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(0.0), $"step {step}");
			}

			Assert.That(simulation.State, Is.EqualTo(openWater.State), "the wall changes nothing");
		}

		[Test]
		public void Approaching_StopsAtTheNearFace_AndTheFarFaceNeverActs(
			[Values(0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0)] double knots,
			[Values(0.0, 30.0)] double angleDegrees,
			[Values(WallThickness, ContactScenarios.BargeWidth)] double thickness)
		{
			// Coasting in neutral from 0.2 m off a thin wall or a barge-sized block, head-on or 30° off the normal.
			var heading = Angles.ToRadians(angleDegrees);
			var structure = ContactScenarios.Block(ContactScenarios.MaxNorth(_parameters, heading) + 0.2, thickness, -WallHalfLength, WallHalfLength, Fender, 1);
			var near = structure[0];
			var far = structure[2];
			var initial = VesselState.Planar(0.0, 0.0, 0.0, heading, surge: knots * Units.MetresPerSecondPerKnot);
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(structure), initial);
			var openWater = ManeuverRunner.Create(_parameters, null, initial);
			var deepest = 0.0;
			var peakForce = 0.0;
			for (var step = 0; step < 15 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				openWater.Step(ControlInputs.Neutral);
				Assert.That(simulation.State.IsFinite(), Is.True);
				var load = simulation.ContactLoad;
				var outward = load.North * near.WaterNormal.North + load.East * near.WaterNormal.East;
				Assert.That(outward, Is.GreaterThanOrEqualTo(-1e-9 * load.Magnitude), $"step {step}: the contact never pulls the hull into the structure");
				for (var i = 0; i < simulation.ContactCount; i++)
				{
					Assert.That(simulation.GetContact(i).SegmentId, Is.Not.EqualTo(far.Id), $"step {step}: the far face never acts");
				}

				deepest = Math.Max(deepest, ContactScenarios.MaxCompression(simulation));
				deepest = Math.Max(deepest, ContactScenarios.DeepestVertex(_parameters, simulation.State, near));
				peakForce = Math.Max(peakForce, simulation.Telemetry.ContactForce);
			}

			TestContext.Out.WriteLine($"{_vesselFile} {knots} kn at {angleDegrees} deg into {thickness:F2} m: deepest {deepest:F3} m, peak force {peakForce / 1000.0:F0} kN");
			if (knots == 0.0)
			{
				Assert.That(peakForce, Is.EqualTo(0.0), "at rest clear of the structure nothing touches");
				Assert.That(simulation.State, Is.EqualTo(openWater.State));
				return;
			}

			Assert.That(peakForce, Is.GreaterThan(0.0), "the hull hits the near face");
			Assert.That(deepest, Is.LessThan(_law.FullCompression + 0.3), "stopped by the fenders and the structure behind them");
			Assert.That(ContactScenarios.DeepestVertex(_parameters, simulation.State, near), Is.LessThan(_law.BottomingCompression),
				"and back on the water side afterwards");
		}

		[TestCase(0.1)]
		[TestCase(0.3)]
		[TestCase(0.6)]
		public void DeepInAThinWall_TheHullIsPushedBackOutThroughTheFaceItCrossed(double depth)
		{
			// Past half the wall's thickness the far face is the nearer one; past 0.4 m the bow is beyond it.
			var model = new ContactModel(_parameters);
			var wall = ThinWall(_bow - depth);
			ContactScenarios.Approach(model, wall, 0.0, 0.0, 0.0);
			model.Forces(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, ContactModel.MinimumSlipSpeed, true, out var x, out var y, out _);
			Assert.That(x, Is.LessThan(0.0), "pushed back aft, out of the face it went in through");
			Assert.That(Math.Abs(y), Is.LessThan(1e-6 * -x));
			Assert.That(model.ContactCount, Is.EqualTo(1));
			Assert.That(model.Contact(0).SegmentId, Is.EqualTo(wall[0].Id));
			Assert.That(model.Contact(0).Compression, Is.EqualTo(depth).Within(1e-9));
		}

		[Test]
		public void AHullPlacedOverlappingAFace_IsNotPushedByIt_AndResetForgetsContact()
		{
			// Points behind a face that never crossed it from the water side are not in contact with it.
			var face = ContactScenarios.FaceToTheNorth(_bow - 0.05, -20.0, 20.0, Fender, 1);
			var geometry = new SegmentListGeometry(new[] { face });
			var placed = ContactScenarios.Create(_parameters, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Assert.That(placed.ContactCount, Is.EqualTo(0));
			Assert.That(placed.ContactLoad.Magnitude, Is.EqualTo(0.0));
			placed.Step(ControlInputs.Neutral);
			Assert.That(placed.Telemetry.ContactForce, Is.EqualTo(0.0));

			// Arriving there from the water side is contact; resetting to the same pose forgets it.
			var arriving = ContactScenarios.Create(_parameters, geometry, VesselState.Planar(0.0, -0.3, 0.0, 0.0, surge: 1.0));
			var touched = false;
			for (var step = 0; step < 2 * SimulationClock.StepsPerSecond && !touched; step++)
			{
				arriving.Step(ControlInputs.Neutral);
				touched = arriving.ContactCount > 0;
			}

			Assert.That(touched, Is.True);
			arriving.Reset(VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Assert.That(arriving.ContactCount, Is.EqualTo(0));
			Assert.That(arriving.ContactLoad.Magnitude, Is.EqualTo(0.0));
			arriving.Step(ControlInputs.Neutral);
			Assert.That(arriving.Telemetry.ContactForce, Is.EqualTo(0.0));
		}

		[Test]
		public void SlidingAlongAFaceMadeOfSeveralSegments_KeepsTheSameForce()
		{
			// The bow 5 cm into a face, then the hull slides 1.5 m east in 1 cm steps. The face cut into pieces at
			// uneven joints (one piece shorter than the bow) pushes as the whole face does: the same force, and the
			// same moment but for the midpoint rule's error on the shoulders, whose depth varies.
			var whole = ContactScenarios.FaceToTheNorth(_bow - 0.05, -20.0, 20.0, Fender, 1);
			var pieces = ContactScenarios.Split(whole, 19.3, 20.15, 20.9, 22.7);
			var single = new ContactModel(_parameters);
			var split = new ContactModel(_parameters);
			ContactScenarios.Approach(single, new[] { whole }, 0.0, 0.0, 0.0);
			ContactScenarios.Approach(split, pieces, 0.0, 0.0, 0.0);
			for (var i = 0; i <= 150; i++)
			{
				var east = 0.01 * i;
				single.Commit(0.0, east, 0.0);
				split.Commit(0.0, east, 0.0);
				single.Forces(0.0, east, 0.0, 0.0, 0.1, 0.0, ContactModel.MinimumSlipSpeed, true, out var x1, out var y1, out var n1);
				split.Forces(0.0, east, 0.0, 0.0, 0.1, 0.0, ContactModel.MinimumSlipSpeed, true, out var x2, out var y2, out var n2);
				Assert.That(x1, Is.LessThan(0.0));
				Assert.That(x2, Is.EqualTo(x1).Within(1e-9 * -x1), $"surge at {east:F2} m east");
				Assert.That(y2, Is.EqualTo(y1).Within(1e-9 * -x1), $"sway at {east:F2} m east");
				Assert.That(n2, Is.EqualTo(n1).Within(1e-4 * Math.Abs(n1)), $"yaw at {east:F2} m east");
			}
		}

		[Test]
		public void SlidingPastTheCornerOfABarge_LetsGo_AndTheEndFacePushesBack()
		{
			// The barge's south-west corner under the starboard half of the bow, 5 cm deep. Sliding west carries
			// the bow past the corner into open water; sliding back, the barge's end face meets the bow side.
			var barge = ContactScenarios.Barge(_bow - 0.05, 1.0 + 0.5 * ContactScenarios.BargeLength, Fender, 1);
			var model = new ContactModel(_parameters);
			ContactScenarios.Approach(model, barge, 0.0, 0.0, 0.0);
			Assert.That(model.MayTouch(0.0, 0.0, 0.0, 0.02), Is.True);
			var east = 0.0;
			for (var i = 1; i <= 150; i++)
			{
				east = -0.01 * i;
				model.Commit(0.0, east, 0.0);
			}

			model.Forces(0.0, east, 0.0, 0.0, 0.0, 0.0, ContactModel.MinimumSlipSpeed, true, out var x, out var y, out _);
			Assert.That(model.ContactCount, Is.EqualTo(0), "the bow is past the corner");
			Assert.That(x, Is.EqualTo(0.0));
			Assert.That(y, Is.EqualTo(0.0));

			for (var i = 1; i <= 60; i++)
			{
				east += 0.01;
				model.Commit(0.0, east, 0.0);
			}

			model.Forces(0.0, east, 0.0, 0.0, 0.0, 0.0, ContactModel.MinimumSlipSpeed, true, out x, out y, out _);
			Assert.That(model.ContactCount, Is.EqualTo(1));
			Assert.That(model.Contact(0).SegmentId, Is.EqualTo(barge[1].Id), "the west end face");
			Assert.That(y, Is.LessThan(0.0), "pushes the bow back to port");
			Assert.That(Math.Abs(x), Is.LessThan(1e-9 * -y), "and not aft: the south face is out of reach");
		}

		[Test]
		public void Landing_OnAFaceMadeOfSeveralSegments_MatchesTheWholeFace()
		{
			var whole = ContactScenarios.Barge(_bow + 0.2, 0.0, Fender, 1);
			var cut = ContactScenarios.Split(whole[0], 0.5 * ContactScenarios.BargeLength - 1.3, 0.5 * ContactScenarios.BargeLength + 0.9);
			var split = new[] { cut[0], cut[1], cut[2], whole[1], whole[2], whole[3] };
			foreach (var knots in new[] { 1.0, 3.0 })
			{
				var speed = knots * Units.MetresPerSecondPerKnot;
				var reference = ContactScenarios.BowOnLanding(_parameters, whole, speed);
				var landing = ContactScenarios.BowOnLanding(_parameters, split, speed);
				Assert.That(landing.PeakForce, Is.EqualTo(reference.PeakForce).Within(1e-6 * reference.PeakForce), $"{knots} kn");
				Assert.That(landing.PeakCompression, Is.EqualTo(reference.PeakCompression).Within(1e-6 * reference.PeakCompression));
				Assert.That(landing.ReboundSpeed, Is.EqualTo(reference.ReboundSpeed).Within(1e-6 * reference.ReboundSpeed));
			}
		}

		private static ContactSegment[] ThinWall(double southFace) =>
			ContactScenarios.Block(southFace, WallThickness, -WallHalfLength, WallHalfLength, Fender, 1);
	}
}
