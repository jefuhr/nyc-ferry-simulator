using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The contact force model on its own, for a hull held at a pose. Failure modes guarded:
	/// <list type="bullet">
	/// <item>Force from a face the hull is clear of: in front of it, past its ends, or far behind it.</item>
	/// <item>Normal force not growing with compression, not matching stiffness × compression × length, or
	/// staying soft past the fender's compression limit.</item>
	/// <item>Damping that pulls the hull toward the face as it leaves (adhesion).</item>
	/// <item>Friction that helps the sliding, exceeds μ × normal force, or jumps at zero slip (chatter).</item>
	/// <item>A corner contact turning the boat the wrong way; the far faces of a barge pushing a hull
	/// through it; a face within reach not flagged for contact.</item>
	/// <item>Separate patches of contact erasing each other's load; a face cut into segments pushing
	/// differently from the whole face.</item>
	/// <item>The deepest compression understated at a corner, so a bottomed-out fender goes unreported; the total
	/// normal force jumping where a fender bottoms out while the hull moves.</item>
	/// <item>Contact let go while the hull is still deep behind the face it crossed; the model growing, or taking
	/// more segments than it was built for.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class ContactModelTests
	{
		private const int FaceId = 7;

		private readonly string _vesselFile;
		private VesselParameters _parameters;
		private ContactModel _model;
		private double _bow;
		private double _stern;
		private double _halfBeam;
		private double _bowLineLength;

		public ContactModelTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
			_model = new ContactModel(_parameters);
			_bow = ContactScenarios.BowX(_parameters);
			_stern = ContactScenarios.SternX(_parameters);
			_halfBeam = ContactScenarios.HalfBeam(_parameters);
			_bowLineLength = ContactScenarios.Length(_parameters.Contact.BowFenderLine);
		}

		[Test]
		public void BoundingRadius_CoversTheOutline()
		{
			var radius = 0.0;
			foreach (var vertex in ContactScenarios.OutlineVertices(_parameters))
			{
				radius = Math.Max(radius, Math.Sqrt(vertex.X * vertex.X + vertex.Y * vertex.Y));
			}

			Assert.That(_model.BoundingRadius, Is.EqualTo(radius).Within(1e-12));
		}

		[Test]
		public void NoForce_WhenClearOfTheSegment()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var cases = new[]
			{
				ContactScenarios.FaceToTheNorth(_bow + 0.01, -20.0, 20.0, fender, FaceId),
				ContactScenarios.FaceToTheNorth(_bow - 0.1, _halfBeam + 0.5, 30.0, fender, FaceId),
				ContactScenarios.FaceToTheNorth(_stern - 3.0, -20.0, 20.0, fender, FaceId),
			};

			foreach (var face in cases)
			{
				Evaluate(new[] { face }, 0.0, 3.0, 0.0, 0.0, out var x, out var y, out var n);
				Assert.That(x, Is.EqualTo(0.0), $"{face.Start} to {face.End}");
				Assert.That(y, Is.EqualTo(0.0));
				Assert.That(n, Is.EqualTo(0.0));
				Assert.That(_model.ContactCount, Is.EqualTo(0));
			}
		}

		[Test]
		public void NormalForce_GrowsWithCompression_AndBottomsOut()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			var previous = 0.0;
			for (var compression = 0.002; compression <= 0.6; compression += 0.002)
			{
				var force = BowOnForce(fender, compression, out var y, out var n);
				Assert.That(force, Is.GreaterThan(previous), $"compression {compression:F3} m");
				Assert.That(Math.Abs(y), Is.LessThan(1e-6 * force + 1e-6), "bow-on contact pushes straight aft");
				Assert.That(Math.Abs(n), Is.LessThan(1e-6 * force + 1e-6), "and does not turn the boat");
				previous = force;
			}

			var small = BowOnForce(fender, 0.02, out _, out _);
			Assert.That(small, Is.EqualTo(law.Force(0.02) * _bowLineLength).Within(0.03 * small), "stiffness × compression × contact length");
			Assert.That(_model.ContactCount, Is.EqualTo(1));
			Assert.That(_model.Contact(0).SegmentId, Is.EqualTo(FaceId));
			Assert.That(_model.Contact(0).Compression, Is.EqualTo(0.02).Within(1e-9));
			Assert.That(_model.Contact(0).ContactLength, Is.EqualTo(_bowLineLength).Within(0.05 * _bowLineLength));
			Assert.That(_model.Contact(0).NormalForce, Is.EqualTo(small).Within(1e-9 * small));
			Assert.That(_model.Contact(0).BottomedOut, Is.False);

			var soft = (BowOnForce(fender, 0.03, out _, out _) - BowOnForce(fender, 0.01, out _, out _)) / 0.02;
			var beyond = law.FullCompression + 0.05;
			var stiff = (BowOnForce(fender, beyond + 0.01, out _, out _) - BowOnForce(fender, beyond - 0.01, out _, out _)) / 0.02;
			Assert.That(_model.Contact(0).BottomedOut, Is.True);
			Assert.That(stiff, Is.GreaterThan(5.0 * soft), "past the compression limits the structure takes the load");
		}

		[Test]
		public void Damping_ResistsApproach_AndNeverPulls()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			var face = ContactScenarios.FaceToTheNorth(_bow - 0.05, -20.0, 20.0, fender, FaceId);
			Evaluate(new[] { face }, 0.0, 0.0, 0.0, 0.0, out var resting, out _, out _);
			Evaluate(new[] { face }, 0.0, 0.5, 0.0, 0.0, out var closing, out _, out _);
			var length = _model.Contact(0).ContactLength;
			Assert.That(-closing, Is.GreaterThan(-resting), "closing on the face adds damping force");
			Assert.That(-closing + resting, Is.EqualTo(law.Damping(0.05) * 0.5 * length - law.Hysteresis * resting).Within(0.01 * (resting - closing)),
				"damping × compression rate × contact length, plus the hysteresis share of the elastic force");

			Evaluate(new[] { face }, 0.0, -0.05, 0.0, 0.0, out var opening, out _, out _);
			Assert.That(-opening, Is.LessThan(-resting).And.GreaterThan(0.0), "opening slowly: the fender still pushes, less");
			Evaluate(new[] { face }, 0.0, -5.0, 0.0, 0.0, out var leaving, out _, out _);
			Assert.That(leaving, Is.EqualTo(0.0), "a fender never pulls the hull back");
		}

		[Test]
		public void Hysteresis_PushesHarderClosingThanOpening_AndNotAtAllAtRest()
		{
			// Same compression, slow motion either way: the closing and opening forces differ by twice the
			// hysteresis share whatever the speed, once past the smoothing; at rest it is the elastic law alone.
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			var face = ContactScenarios.FaceToTheNorth(_bow - 0.05, -20.0, 20.0, fender, FaceId);
			Evaluate(new[] { face }, 0.0, 0.0, 0.0, 0.0, out var resting, out _, out _);
			var length = _model.Contact(0).ContactLength;
			var elastic = _model.Contact(0).NormalForce;
			Assert.That(law.Hysteresis, Is.GreaterThan(0.0));
			Assert.That(-resting, Is.EqualTo(elastic).Within(1e-9 * elastic), "no hysteresis at rest");
			foreach (var speed in new[] { 0.01, 0.03 })
			{
				Evaluate(new[] { face }, 0.0, speed, 0.0, 0.0, out var closing, out _, out _);
				Evaluate(new[] { face }, 0.0, -speed, 0.0, 0.0, out var opening, out _, out _);
				var viscous = 2.0 * law.Damping(0.05) * speed * length;
				var smoothing = speed / Math.Sqrt(speed * speed + ContactModel.MinimumSlipSpeed * ContactModel.MinimumSlipSpeed);
				Assert.That(opening - closing - viscous, Is.EqualTo(2.0 * law.Hysteresis * smoothing * elastic).Within(1e-6 * elastic), $"at {speed} m/s");
			}
		}

		[Test]
		public void Friction_OpposesSliding_UpToTheCoulombLimit()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var face = ContactScenarios.FaceToTheEast(_halfBeam - 0.05, -20.0, 20.0, fender, FaceId);
			Evaluate(new[] { face }, 0.0, 0.5, 0.0, 0.0, out var aheadX, out var aheadY, out _);
			Assert.That(aheadY, Is.LessThan(0.0), "a face to starboard pushes the hull to port");
			Assert.That(aheadX, Is.LessThan(0.0), "sliding ahead along the face, friction acts aft");
			Assert.That(-aheadX, Is.EqualTo(fender.Friction * -aheadY).Within(0.01 * -aheadX), "fully sliding: μ × normal force");

			Evaluate(new[] { face }, 0.0, -0.5, 0.0, 0.0, out var asternX, out var asternY, out _);
			Assert.That(asternX, Is.EqualTo(-aheadX).Within(1e-9 * -aheadX), "sliding astern, friction acts ahead");
			Assert.That(asternY, Is.EqualTo(aheadY).Within(1e-9 * -aheadY));

			Evaluate(new[] { face }, 0.0, 0.0, 0.0, 0.0, out var stillX, out _, out _);
			Assert.That(Math.Abs(stillX), Is.LessThan(1e-9 * -aheadY), "no slip, no friction");
		}

		[Test]
		public void Friction_IsSmoothThroughZeroSlip()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var face = ContactScenarios.FaceToTheEast(_halfBeam - 0.05, -20.0, 20.0, fender, FaceId);
			var slip = 0.01;
			Evaluate(new[] { face }, 0.0, 0.0, 0.0, 0.0, out _, out var normal, out _);
			var limit = fender.Friction * -normal;
			var previous = double.PositiveInfinity;
			for (var i = -50; i <= 50; i++)
			{
				_model.Forces(0.0, 0.0, 0.0, 0.1 * i * slip, 0.0, 0.0, slip, false, out var x, out _, out _);
				Assert.That(x, Is.LessThanOrEqualTo(previous), "friction falls steadily as the slip grows ahead");
				Assert.That(Math.Abs(x), Is.LessThanOrEqualTo(limit * (1.0 + 1e-9)));
				if (!double.IsPositiveInfinity(previous))
				{
					Assert.That(previous - x, Is.LessThan(0.15 * limit), "no jump through zero slip");
				}

				previous = x;
			}
		}

		[TestCase(1)]
		[TestCase(-1)]
		public void ContactOnOneBowCorner_SwingsTheBowTowardThatSide(int side)
		{
			// Holding back the starboard half of the bow lets the port side run on: the bow swings to starboard.
			var fender = ContactScenarios.EstimatedBargeFender;
			var face = side > 0
				? ContactScenarios.FaceToTheNorth(_bow - 0.05, 0.5, 20.0, fender, FaceId)
				: ContactScenarios.FaceToTheNorth(_bow - 0.05, -20.0, -0.5, fender, FaceId);
			Evaluate(new[] { face }, 0.0, 0.0, 0.0, 0.0, out var x, out _, out var n);
			Assert.That(x, Is.LessThan(0.0));
			Assert.That(Math.Sign(n), Is.EqualTo(side), $"yaw moment {n}");
		}

		[TestCase(15.0)]
		[TestCase(-15.0)]
		public void ObliqueHit_OnTheLeadingBowCorner_TurnsTheBoatParallelToTheFace(double headingDegrees)
		{
			// Heading to starboard of the face normal, the port bow corner leads; the face pushes it back and the
			// boat swings further to starboard, toward lying alongside.
			var heading = Angles.ToRadians(headingDegrees);
			var fender = ContactScenarios.EstimatedBargeFender;
			var face = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) - 0.05, -30.0, 30.0, fender, FaceId);
			Evaluate(new[] { face }, heading, 0.0, 0.0, 0.0, out _, out _, out var n);
			Assert.That(Math.Sign(n), Is.EqualTo(Math.Sign(headingDegrees)), $"yaw moment {n}");
		}

		[Test]
		public void BargeCorner_PushesTheHullOutThroughTheNearerFace()
		{
			// The barge's south-west corner overlaps the starboard half of the bow by 5 cm. Its far faces lie
			// metres away and must not push the bow through the barge.
			var fender = ContactScenarios.EstimatedBargeFender;
			var barge = ContactScenarios.Barge(_bow - 0.05, 1.0 + 0.5 * ContactScenarios.BargeLength, fender, 1);
			ContactScenarios.Approach(_model, barge, 0.0, 0.0, 0.0);
			_model.Forces(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, ContactModel.MinimumSlipSpeed, true, out var x, out var y, out var n);
			Assert.That(x, Is.LessThan(0.0), "pushed back off the south face");
			Assert.That(Math.Abs(y), Is.LessThan(0.2 * -x), "mostly straight back");
			Assert.That(n, Is.GreaterThan(0.0), "a starboard-side contact swings the bow to starboard");
			for (var i = 0; i < _model.ContactCount; i++)
			{
				var contact = _model.Contact(i);
				Assert.That(contact.SegmentId, Is.EqualTo(1).Or.EqualTo(2), "only the faces at the corner touch");
				Assert.That(contact.Compression, Is.LessThanOrEqualTo(0.05 + 1e-9));
			}
		}

		[Test]
		public void MayTouch_FlagsFacesWithinReach_Only()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var face = ContactScenarios.FaceToTheNorth(_bow + 0.05, -20.0, 20.0, fender, FaceId);
			_model.Prepare(new[] { face }, 1);
			_model.Reset(0.0, 0.0, 0.0);
			Assert.That(_model.MayTouch(0.0, 0.0, 0.0, 0.1), Is.True);
			Assert.That(_model.MayTouch(0.0, 0.0, 0.0, 0.01), Is.False);

			var beside = ContactScenarios.FaceToTheEast(_halfBeam + 0.5, -20.0, 20.0, fender, FaceId);
			_model.Prepare(new[] { beside }, 1);
			Assert.That(_model.MayTouch(0.0, 0.0, 0.0, 0.1), Is.False);
			Assert.That(_model.MayTouch(0.0, 0.0, 0.0, 0.6), Is.True);
		}

		[Test]
		public void DisjointPatches_UnderTheBow_EachKeepTheirLoad()
		{
			// Review finding 2: two 9 cm faces under the ends of the bow fender line. Alone each pushes; together
			// they fell between the integration points of one envelope and both vanished.
			var fender = ContactScenarios.EstimatedBargeFender;
			var half = 0.5 * _bowLineLength;
			var port = ContactScenarios.FaceToTheNorth(_bow - 0.05, -half + 0.01, -half + 0.10, fender, 1);
			var starboard = ContactScenarios.FaceToTheNorth(_bow - 0.05, half - 0.10, half - 0.01, fender, 2);
			Evaluate(new[] { port }, 0.0, 0.0, 0.0, 0.0, out var portX, out _, out _);
			Evaluate(new[] { starboard }, 0.0, 0.0, 0.0, 0.0, out var starboardX, out _, out _);
			Evaluate(new[] { port, starboard }, 0.0, 0.0, 0.0, 0.0, out var bothX, out _, out var bothN);
			var alone = _model.LawFor(fender).Force(0.05) * 0.09;
			Assert.That(-portX, Is.EqualTo(alone).Within(1e-9 * alone), "9 cm of bow fender line 5 cm deep");
			Assert.That(starboardX, Is.EqualTo(portX).Within(1e-9 * -portX), "mirror images");
			Assert.That(bothX, Is.EqualTo(portX + starboardX).Within(1e-9 * -portX), "together they push as much as both");
			Assert.That(Math.Abs(bothN), Is.LessThan(1e-9 * -portX * _bow), "and balance");
			Assert.That(_model.ContactCount, Is.EqualTo(2));
			Assert.That(_model.Contact(0).ContactLength, Is.EqualTo(0.09).Within(1e-9));
			Assert.That(_model.Contact(1).ContactLength, Is.EqualTo(0.09).Within(1e-9));
		}

		[Test]
		public void AddingASeparatePatch_NeverErasesALoad()
		{
			// One patch at the port end of the bow fender line and a second, narrow or wide, anywhere clear of it:
			// the two together push exactly as the two alone.
			var fender = ContactScenarios.EstimatedBargeFender;
			var half = 0.5 * _bowLineLength;
			var first = ContactScenarios.FaceToTheNorth(_bow - 0.05, -half + 0.01, -half + 0.10, fender, 1);
			Evaluate(new[] { first }, 0.0, 0.0, 0.0, 0.0, out var firstX, out _, out var firstN);
			foreach (var width in new[] { 0.02, 0.09, 0.15, 0.6 })
			{
				for (var west = -half + 0.13; west + width < half - 0.01; west += 0.17)
				{
					var second = ContactScenarios.FaceToTheNorth(_bow - 0.05, west, west + width, fender, 2);
					Evaluate(new[] { second }, 0.0, 0.0, 0.0, 0.0, out var secondX, out _, out var secondN);
					Evaluate(new[] { first, second }, 0.0, 0.0, 0.0, 0.0, out var bothX, out _, out var bothN);
					Assert.That(secondX, Is.LessThan(0.0), $"{width} m patch at {west:F2} m");
					Assert.That(bothX, Is.EqualTo(firstX + secondX).Within(1e-9 * -firstX), $"{width} m patch at {west:F2} m");
					Assert.That(bothN, Is.EqualTo(firstN + secondN).Within(1e-9 * -firstX * _bow), $"{width} m patch at {west:F2} m");
				}
			}
		}

		[TestCase(5.0)]
		[TestCase(-12.0)]
		public void SplittingAFace_IntoSegments_DoesNotChangeTheForce(double headingDegrees)
		{
			// The same face whole and cut at uneven joints, some under the bow, pressed on at an angle. The force is
			// linear along each edge, which the midpoint rule integrates exactly however the face is cut; the moment
			// is quadratic, so it may differ by the rule's error.
			var heading = Angles.ToRadians(headingDegrees);
			var fender = ContactScenarios.EstimatedBargeFender;
			var whole = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) - 0.08, -20.0, 20.0, fender, FaceId);
			Evaluate(new[] { whole }, heading, 0.0, 0.0, 0.0, out var x, out var y, out var n);
			foreach (var cuts in new[] { new[] { 21.3 }, new[] { 17.9, 19.62, 20.4 }, new[] { 15.0, 18.31, 19.0, 19.07, 20.88, 22.5, 26.0 } })
			{
				Evaluate(ContactScenarios.Split(whole, cuts), heading, 0.0, 0.0, 0.0, out var splitX, out var splitY, out var splitN);
				Assert.That(splitX, Is.EqualTo(x).Within(1e-9 * -x), $"{cuts.Length + 1} pieces");
				Assert.That(splitY, Is.EqualTo(y).Within(1e-9 * -x));
				Assert.That(splitN, Is.EqualTo(n).Within(0.01 * Math.Abs(n)));
			}
		}

		[TestCase(20.0, 0.01)]
		[TestCase(20.0, 0.05)]
		[TestCase(-20.0, 0.05)]
		[TestCase(20.0, 0.2)]
		public void DeepestCompression_IsThatOfTheDeepestPoint_AtAnObliqueCorner(double headingDegrees, double depth)
		{
			// Review finding 5: the bow corner leads into a long face. The deepest compression is the corner's,
			// not that of the deepest integration point half a sample spacing back.
			var heading = Angles.ToRadians(headingDegrees);
			var face = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) - depth, -100.0, 100.0, ContactScenarios.EstimatedBargeFender, FaceId);
			Evaluate(new[] { face }, heading, 0.0, 0.0, 0.0, out _, out _, out _);
			Assert.That(_model.ContactCount, Is.EqualTo(1));
			Assert.That(_model.Contact(0).Compression, Is.EqualTo(depth).Within(1e-9));
			Assert.That(_model.Contact(0).BottomedOut, Is.False);
		}

		[Test]
		public void BottomedOut_IsReported_AsSoonAsTheDeepestPointPassesTheFirstLimit()
		{
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			var heading = Angles.ToRadians(20.0);
			foreach (var depth in new[] { law.BottomingCompression - 0.001, law.BottomingCompression + 0.001 })
			{
				var face = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) - depth, -100.0, 100.0, fender, FaceId);
				Evaluate(new[] { face }, heading, 0.0, 0.0, 0.0, out _, out _, out _);
				Assert.That(_model.Contact(0).Compression, Is.EqualTo(depth).Within(1e-9));
				Assert.That(_model.Contact(0).BottomedOut, Is.EqualTo(depth > law.BottomingCompression), $"{depth:F4} m");
			}
		}

		[TestCase(0.3)]
		[TestCase(1.0)]
		[TestCase(-0.3)]
		[TestCase(-1.0)]
		public void TotalNormalForce_IsContinuousWhereTheFendersBottomOut(double closingSpeed)
		{
			// Review finding 3: bow-on, closing (positive) or opening at a finite rate, just either side of each
			// point where a fender bottoms out. Elastic, hysteresis and damping forces together must not jump.
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			foreach (var corner in new[] { law.BottomingCompression, law.FullCompression })
			{
				var below = BowOnForce(fender, corner - 1e-9, closingSpeed, out _, out _);
				var above = BowOnForce(fender, corner + 1e-9, closingSpeed, out _, out _);
				var scale = law.Force(corner) * _bowLineLength;
				Assert.That(below, Is.GreaterThan(0.0));
				Assert.That(Math.Abs(above - below), Is.LessThan(1e-6 * scale), $"{closingSpeed} m/s at {corner:F4} m: {below:F1} N to {above:F1} N");
			}
		}

		[TestCase(1.5)]
		[TestCase(2.5)]
		public void AHullDeepBehindTheFaceItCrossed_IsStillPushedOut(double depth)
		{
			// Contact lasts until the hull is back on the water side, however deep it went: a point is never let go
			// while it is still behind the face.
			var fender = ContactScenarios.EstimatedBargeFender;
			var law = _model.LawFor(fender);
			var face = ContactScenarios.FaceToTheNorth(_bow - depth, -20.0, 20.0, fender, FaceId);
			ContactScenarios.Approach(_model, new[] { face }, 0.0, 0.0, 0.0, depth + 1.0);
			_model.Forces(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, ContactModel.MinimumSlipSpeed, true, out var x, out _, out _);
			Assert.That(-x, Is.GreaterThan(law.Force(depth) * _bowLineLength), "the whole bow line and more");
			Assert.That(_model.Contact(0).BottomedOut, Is.True);
		}

		[Test]
		public void PreparingMoreSegmentsThanTheModelHolds_IsAnError()
		{
			// The model is sized when it is built and never grows inside a step.
			var model = new ContactModel(_parameters, 4);
			var face = ContactScenarios.FaceToTheNorth(_bow + 5.0, -20.0, 20.0, ContactScenarios.EstimatedBargeFender, FaceId);
			var segments = new[] { face, face, face, face, face };
			Assert.DoesNotThrow(() => model.Prepare(segments, 4));
			Assert.Throws<ArgumentOutOfRangeException>(() => model.Prepare(segments, 5));
		}

		private double BowOnForce(in ContactSurface fender, double compression, out double y, out double n) =>
			BowOnForce(fender, compression, 0.0, out y, out n);

		// Surge force pushing the hull back when its bow is pressed this far into a face across the bow, closing
		// on the face at the given speed (negative: moving away).
		private double BowOnForce(in ContactSurface fender, double compression, double closingSpeed, out double y, out double n)
		{
			var face = ContactScenarios.FaceToTheNorth(_bow - compression, -20.0, 20.0, fender, FaceId);
			Evaluate(new[] { face }, 0.0, closingSpeed, 0.0, 0.0, out var x, out y, out n);
			return -x;
		}

		// The hull arrives at the origin from the water side of the first segment, then the forces at the given
		// velocity there.
		private void Evaluate(ContactSegment[] segments, double heading, double northRate, double eastRate, double yawRate,
			out double x, out double y, out double n)
		{
			ContactScenarios.Approach(_model, segments, 0.0, 0.0, heading);
			_model.Forces(0.0, 0.0, heading, northRate, eastRate, yawRate, ContactModel.MinimumSlipSpeed, true, out x, out y, out n);
		}
	}

	/// <summary>
	/// The series fender law. Failure modes guarded: the hull and landing fenders combined in parallel
	/// instead of in series, a force jump where a fender bottoms out (elastic or damping), damping lost when
	/// only one side damps or going negative between ranges, and a hysteresis outside [0, 1) that could make a
	/// recovering fender pull.
	/// </summary>
	public class FenderLawTests
	{
		private static readonly ContactSurface Landing = new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25);

		[Test]
		public void SoftRange_IsTheTwoFendersInSeries()
		{
			var law = FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.2);
			var series = 1.0 / (1.0 / 1.0e6 + 1.0 / 3.5e6);
			Assert.That(law.Stiffness(0.01), Is.EqualTo(series).Within(1e-9 * series));
			Assert.That(law.Force(0.01), Is.EqualTo(series * 0.01).Within(1e-9 * series));
			Assert.That(law.Friction, Is.EqualTo(0.3));
		}

		[Test]
		public void FirstBottomingAndFullCompression_AreAtTheFenderLimits()
		{
			// The landing fender reaches its limit at 250 kN/m, the hull fender at 350 kN/m.
			var law = FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.2);
			Assert.That(law.Force(law.BottomingCompression), Is.EqualTo(2.5e5).Within(1e-6));
			Assert.That(law.Force(law.FullCompression), Is.EqualTo(3.5e5).Within(1e-6));
			Assert.That(law.BottomingCompression, Is.EqualTo(0.25 + 2.5e5 / 3.5e6).Within(1e-12));
			var bottomed = 1.0 / (1.0 / 2.0e7 + 1.0 / 7.0e7);
			Assert.That(law.Stiffness(law.FullCompression + 0.1), Is.EqualTo(bottomed).Within(1e-9 * bottomed));
		}

		[Test]
		public void ElasticForce_IsContinuous_AndRising()
		{
			var law = FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.2);
			foreach (var corner in new[] { law.BottomingCompression, law.FullCompression })
			{
				var below = law.Force(corner - 1e-9);
				var above = law.Force(corner + 1e-9);
				Assert.That(above - below, Is.LessThan(1e-9 * law.Force(corner) + 1.0));
			}

			var previous = -1.0;
			for (var compression = 0.0; compression < 1.0; compression += 0.001)
			{
				Assert.That(law.Force(compression), Is.GreaterThan(previous));
				previous = law.Force(compression);
			}

			Assert.That(law.Force(0.0), Is.EqualTo(0.0));
		}

		[Test]
		public void Damping_KeepsTheTimeConstant_WhenBothSidesShareIt()
		{
			// Two Kelvin-Voigt elements with the same c/k in series are one element with that c/k.
			var landing = new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25);
			var law = FenderLaw.Combine(landing, 2.0e6, 1.0e5, 0.1, 20.0, 0.2);
			Assert.That(law.Damping(0.01), Is.EqualTo(0.05 * law.Stiffness(0.01)).Within(1e-9 * law.Damping(0.01)));
			var deep = law.FullCompression + 0.1;
			Assert.That(law.Damping(deep), Is.EqualTo(0.05 / Math.Sqrt(20.0) * law.Stiffness(deep)).Within(1e-9 * law.Damping(deep)),
				"bottomed out, stiffness × 20 and damping × √20 keep the damping ratio");
		}

		[Test]
		public void Hysteresis_IsKept_AndMustLeaveARecoveringFenderPushing()
		{
			Assert.That(FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.2).Hysteresis, Is.EqualTo(0.2));
			Assert.That(FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.0).Hysteresis, Is.EqualTo(0.0));
			Assert.Throws<ArgumentException>(() => FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 1.0));
			Assert.Throws<ArgumentException>(() => FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, -0.1));
		}

		[Test]
		public void Damping_IsContinuous_AndNeverNegative_ThroughBottomingOut()
		{
			// Review finding 3: the damping switched between three constants, so the total normal force jumped
			// where a fender bottoms out whenever the hull was moving. Landing side first, hull side first, both
			// at once, and one side undamped.
			var laws = new[]
			{
				FenderLaw.Combine(Landing, 3.5e6, 7.0e4, 0.1, 20.0, 0.2),
				FenderLaw.Combine(new ContactSurface(1.0e7, 2.0e4, 0.3, 0.01), 3.5e6, 7.0e4, 0.1, 20.0, 0.2),
				FenderLaw.Combine(new ContactSurface(3.5e6, 5.0e4, 0.3, 0.1), 3.5e6, 7.0e4, 0.1, 20.0, 0.2),
				FenderLaw.Combine(new ContactSurface(1.0e6, 0.0, 0.3, 0.25), 3.5e6, 7.0e4, 0.1, 20.0, 0.0),
			};

			foreach (var law in laws)
			{
				var largest = law.Damping(law.FullCompression + 1.0);
				foreach (var corner in new[] { law.BottomingCompression, law.FullCompression })
				{
					var jump = law.Damping(corner + 1e-9) - law.Damping(corner - 1e-9);
					Assert.That(Math.Abs(jump), Is.LessThan(1e-4 * largest), $"damping jump at {corner:F4} m");
				}

				var previous = law.Damping(0.0);
				for (var compression = 0.0; compression < law.FullCompression + 0.1; compression += 1e-5)
				{
					var damping = law.Damping(compression);
					Assert.That(damping, Is.GreaterThanOrEqualTo(0.0));
					Assert.That(Math.Abs(damping - previous), Is.LessThan(0.01 * largest + 1e-9), $"step to {compression:F5} m");
					previous = damping;
				}
			}
		}

		[Test]
		public void Damping_FromEitherSideAlone_StillDissipates()
		{
			var undampedHull = FenderLaw.Combine(Landing, 3.5e6, 0.0, 0.1, 20.0, 0.2);
			var undampedLanding = FenderLaw.Combine(new ContactSurface(1.0e6, 0.0, 0.3, 0.25), 3.5e6, 7.0e4, 0.1, 20.0, 0.2);
			Assert.That(undampedHull.Damping(0.01), Is.GreaterThan(0.0));
			Assert.That(undampedLanding.Damping(0.01), Is.GreaterThan(0.0));
			var undamped = FenderLaw.Combine(new ContactSurface(1.0e6, 0.0, 0.3, 0.25), 3.5e6, 0.0, 0.1, 20.0, 0.2);
			Assert.That(undamped.Damping(0.01), Is.EqualTo(0.0));
			Assert.That(undamped.Damping(1.0), Is.EqualTo(0.0));
		}
	}
}
