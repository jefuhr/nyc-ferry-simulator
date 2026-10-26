using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Contact through the whole model (<see cref="VesselDynamicsSimulation"/> with world geometry). Failure
	/// modes guarded:
	/// <list type="bullet">
	/// <item>A bounce that gains energy, or loses none with damping (or loses it without damping).</item>
	/// <item>Tunnelling: a hull at 6 kn passing through a face or a barge, head-on or obliquely, because the
	/// step is too coarse, the query too small, or a barge's far face captures the hull.</item>
	/// <item>A corner hit turning the boat the wrong way.</item>
	/// <item>Holding against a landing that bounces away, sinks into the fender, creeps or chatters.</item>
	/// <item>Geometry that changes open-water results when it is empty, distant or never touched.</item>
	/// <item>Nondeterminism or heap allocation in contact; telemetry that disagrees with the contacts.</item>
	/// <item>More structures nearby than the segment buffer holds: some silently left out, or the buffer grown
	/// (allocating) inside a step.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class ContactDynamicsTests
	{
		private const double SixKnots = 6.0 * Units.MetresPerSecondPerKnot;
		private const double HalfHelm = 0.5;

		private readonly string _vesselFile;
		private VesselParameters _parameters;
		private double _bow;

		public ContactDynamicsTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
			_bow = ContactScenarios.BowX(_parameters);
		}

		[Test]
		public void FreeBounce_OffAFender_LosesKineticEnergy_OnlyThroughDamping()
		{
			var damped = ContactScenarios.BowOnLanding(_parameters, ContactScenarios.EstimatedBargeFender, Units.MetresPerSecondPerKnot);
			var undampedVessel = ManeuverRunner.Load(_vesselFile);
			undampedVessel.Contact.HullFenderDamping.Value = 0.0;
			undampedVessel.Contact.FenderHysteresis.Value = 0.0;
			var undampedFender = new ContactSurface(ContactScenarios.EstimatedBargeFender.Stiffness, 0.0, 0.3, 0.25);
			var undamped = ContactScenarios.BowOnLanding(undampedVessel, undampedFender, Units.MetresPerSecondPerKnot);

			Assert.That(damped.Separated && undamped.Separated, Is.True, "both boats bounce off");
			var dampedRestitution = damped.ReboundSpeed / damped.ApproachSpeed;
			var undampedRestitution = undamped.ReboundSpeed / undamped.ApproachSpeed;
			TestContext.Out.WriteLine($"restitution: damped {dampedRestitution:F3}, undamped {undampedRestitution:F3}");
			Assert.That(undampedRestitution, Is.InRange(0.95, 1.0), "springs alone give the energy back, less a little hull drag");
			Assert.That(dampedRestitution, Is.InRange(0.2, 0.85), "fender damping takes a good part of it");
			Assert.That(damped.PeakCompression, Is.LessThan(undamped.PeakCompression));
		}

		[TestCase(0.0)]
		[TestCase(30.0)]
		[TestCase(60.0)]
		public void SixKnots_IntoAFace_DoesNotTunnel(double angleDegrees)
		{
			var heading = Angles.ToRadians(angleDegrees);
			var face = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) + 0.3, -300.0, 300.0,
				ContactScenarios.EstimatedBargeFender, 1);
			AssertNoTunnelling(new[] { face }, face, heading);
		}

		[TestCase(0.0)]
		[TestCase(30.0)]
		public void SixKnots_IntoABarge_DoesNotTunnel(double angleDegrees)
		{
			var heading = Angles.ToRadians(angleDegrees);
			var barge = ContactScenarios.Barge(ContactScenarios.MaxNorth(_parameters, heading) + 0.3, 0.0, ContactScenarios.EstimatedBargeFender, 1);
			AssertNoTunnelling(barge, barge[0], heading);
		}

		[TestCase(1)]
		[TestCase(-1)]
		public void BowCornerHit_SwingsTheBoatTowardLyingAlongside(int side)
		{
			var heading = Angles.ToRadians(20.0 * side);
			var face = ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, heading) + 0.2, -60.0, 60.0,
				ContactScenarios.EstimatedBargeFender, 1);
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(new[] { face }),
				VesselState.Planar(0.0, 0.0, 0.0, heading, surge: 1.0));
			var touched = false;
			for (var step = 0; step < 10 * SimulationClock.StepsPerSecond && !(touched && simulation.ContactCount == 0); step++)
			{
				simulation.Step(ControlInputs.Neutral);
				touched |= simulation.ContactCount > 0;
			}

			Assert.That(touched, Is.True);
			Assert.That(Math.Sign(simulation.State.YawRate), Is.EqualTo(side), $"yaw rate {simulation.State.YawRate}");
		}

		[TestCase(1)]
		[TestCase(-1)]
		public void OneEngineAhead_WithHelmOver_HoldsTheBowAgainstTheBarge(int side)
		{
			// A bow-on push-up: one engine at the ahead idle detent, helm half over against its twist. Hard over
			// over-corrects the idle twist with the estimated rudders (see the contact report).
			var idle = ContactScenarios.IdleLever(_parameters);
			var controls = side > 0 ? new ControlInputs(idle, 0.0, -HalfHelm) : new ControlInputs(0.0, idle, HalfHelm);
			var hold = Hold(controls, 60.0);

			TestContext.Out.WriteLine($"{_vesselFile} side {side}: holding force {hold.MeanForce / 1000.0:F2} kN, " +
				$"range {hold.ForceRange:F1} N, compression {hold.Compression * 1000.0:F1} mm, heading {Angles.ToDegrees(Angles.WrapPi(hold.Simulation.State.Heading)):F2} deg, " +
				$"SOG {hold.Simulation.Telemetry.SpeedOverGround * 1000.0:F2} mm/s");
			AssertHeld(hold);
		}

		[Test]
		public void BothEnginesAheadAtIdle_HoldTheBowAgainstTheBarge()
		{
			var idle = ContactScenarios.IdleLever(_parameters);
			var hold = Hold(new ControlInputs(idle, idle, 0.0), 60.0);
			TestContext.Out.WriteLine($"{_vesselFile} both engines: holding force {hold.MeanForce / 1000.0:F2} kN, compression {hold.Compression * 1000.0:F1} mm");
			AssertHeld(hold);
			Assert.That(Math.Abs(Angles.WrapPi(hold.Simulation.State.Heading)), Is.LessThan(Angles.ToRadians(0.5)), "a symmetric push stays square to the face");
		}

		[Test]
		public void EmptyOrDistantGeometry_IsBitIdenticalToOpenWater()
		{
			var initial = VesselState.Planar(0.0, 10.0, -5.0, 1.0, surge: 2.0);
			var openWater = ManeuverRunner.Create(_parameters, null, initial);
			var empty = ContactScenarios.Create(_parameters, EmptyWorldGeometry.Instance, initial);
			var distant = ContactScenarios.Create(_parameters, new SegmentListGeometry(ContactScenarios.Barge(2000.0, 0.0, ContactScenarios.EstimatedBargeFender, 1)), initial);
			for (var step = 0; step < 60 * SimulationClock.StepsPerSecond; step++)
			{
				var inputs = ScriptedInputs(step);
				openWater.Step(inputs);
				empty.Step(inputs);
				distant.Step(inputs);
			}

			Assert.That(empty.State, Is.EqualTo(openWater.State));
			Assert.That(distant.State, Is.EqualTo(openWater.State));
			Assert.That(distant.Telemetry.ContactForce, Is.EqualTo(0.0));
		}

		[Test]
		public void PassingClearOfAFace_IsBitIdenticalToOpenWater()
		{
			// A face 0.5 m off the starboard side for the whole run is never touched, so nothing changes.
			var face = ContactScenarios.FaceToTheEast(ContactScenarios.HalfBeam(_parameters) + 0.5, -100.0, 300.0, ContactScenarios.EstimatedBargeFender, 1);
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 3.0);
			var openWater = ManeuverRunner.Create(_parameters, null, initial);
			var passing = ContactScenarios.Create(_parameters, new SegmentListGeometry(new[] { face }), initial);
			var idle = ContactScenarios.IdleLever(_parameters);
			for (var step = 0; step < 30 * SimulationClock.StepsPerSecond; step++)
			{
				openWater.Step(new ControlInputs(idle, idle, 0.0));
				passing.Step(new ControlInputs(idle, idle, 0.0));
				Assert.That(passing.Telemetry.ContactForce, Is.EqualTo(0.0));
			}

			Assert.That(passing.State.North, Is.GreaterThan(60.0), "the run passes along the face");
			Assert.That(passing.State, Is.EqualTo(openWater.State));
		}

		[Test]
		public void SameGeometryAndInputs_GiveBitIdenticalStates_InContact()
		{
			var geometry = new SegmentListGeometry(ContactScenarios.Barge(_bow + 0.5, 3.0, ContactScenarios.EstimatedBargeFender, 1));
			var initial = VesselState.Planar(0.0, 0.0, 0.0, Angles.ToRadians(5.0), surge: 1.5);
			var first = ContactScenarios.Create(_parameters, geometry, initial);
			var second = ContactScenarios.Create(_parameters, geometry, initial);
			var touched = false;
			for (var step = 0; step < 30 * SimulationClock.StepsPerSecond; step++)
			{
				var inputs = ScriptedInputs(step);
				first.Step(inputs);
				second.Step(inputs);
				touched |= first.ContactCount > 0;
			}

			Assert.That(touched, Is.True);
			Assert.That(second.State, Is.EqualTo(first.State));
			Assert.That(second.Telemetry.ContactForce, Is.EqualTo(first.Telemetry.ContactForce));
		}

		[Test]
		public void Step_InContact_DoesNotAllocate()
		{
			var idle = ContactScenarios.IdleLever(_parameters);
			var hold = Hold(new ControlInputs(idle, 0.0, -HalfHelm), 5.0);
			var simulation = hold.Simulation;
			Assert.That(simulation.ContactCount, Is.GreaterThan(0));
			// An allocation per step shows in every window. The runtime's tiered JIT can allocate once while it recompiles
			// hot methods early in a test run (seen as about 6 kB under load, never with tiering off), so up to four
			// windows are measured and one must be clean.
			var allocated = 0L;
			for (var window = 0; window < 4; window++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var step = 0; step < 2000; step++)
				{
					simulation.Step(new ControlInputs(idle, 0.0, -HalfHelm));
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
				if (allocated == 0)
				{
					break;
				}
			}

			Assert.That(simulation.ContactCount, Is.GreaterThan(0), "still in contact");
			Assert.That(allocated, Is.EqualTo(0), "steps in contact allocate nothing once the runtime has settled");
		}

		[Test]
		public void Telemetry_IsTheResultantOfTheSegmentContacts()
		{
			var idle = ContactScenarios.IdleLever(_parameters);
			var hold = Hold(new ControlInputs(idle, 0.0, -HalfHelm), 20.0);
			var simulation = hold.Simulation;
			var load = simulation.ContactLoad;
			Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(load.Magnitude));
			Assert.That(load.Magnitude, Is.EqualTo(Math.Sqrt(load.North * load.North + load.East * load.East)).Within(1e-9 * load.Magnitude));

			var north = 0.0;
			var east = 0.0;
			for (var i = 0; i < simulation.ContactCount; i++)
			{
				var contact = simulation.GetContact(i);
				var segment = FindSegment(hold.Geometry, contact.SegmentId);
				north += contact.NormalForce * segment.WaterNormal.North + contact.FrictionForce * segment.Direction.North;
				east += contact.NormalForce * segment.WaterNormal.East + contact.FrictionForce * segment.Direction.East;
				Assert.That(contact.NormalForce, Is.GreaterThan(0.0));
				Assert.That(contact.ContactLength, Is.GreaterThan(0.0));
			}

			Assert.That(north, Is.EqualTo(load.North).Within(1e-9 * load.Magnitude));
			Assert.That(east, Is.EqualTo(load.East).Within(1e-9 * load.Magnitude));
			var heading = simulation.State.Heading;
			Assert.That(load.X, Is.EqualTo(load.North * Math.Cos(heading) + load.East * Math.Sin(heading)).Within(1e-9 * load.Magnitude));
			Assert.That(load.Y, Is.EqualTo(-load.North * Math.Sin(heading) + load.East * Math.Cos(heading)).Within(1e-9 * load.Magnitude));
		}

		[Test]
		public void Factory_PassesTheGeometryToTheSimulation()
		{
			var geometry = new SegmentListGeometry(ContactScenarios.Barge(_bow + 0.2, 0.0, ContactScenarios.EstimatedBargeFender, 1));
			var simulation = new VesselDynamicsFactory().Create(_parameters, UniformEnvironment.Calm, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 1.0));
			var peak = 0.0;
			for (var step = 0; step < 5 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				peak = Math.Max(peak, simulation.Telemetry.ContactForce);
			}

			Assert.That(peak, Is.GreaterThan(1.0e4), "the bow meets the barge");
			Assert.That(simulation.State.North, Is.LessThan(0.2 + 0.5), "and does not pass through it");
		}

		[Test]
		public void SaturatedSegmentQuery_IsFlagged()
		{
			// Review finding 4: more segments within the query radius than the buffer holds. Those past the limit in
			// the geometry's order are left out, here the face ahead of the bow, so the simulation must say so.
			var segments = DenseFieldAstern(VesselDynamicsSimulation.MaximumSegments);
			segments.Add(ContactScenarios.FaceToTheNorth(_bow + 0.05, -10.0, 10.0, ContactScenarios.EstimatedBargeFender, 99999));
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(segments), VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 0.5));
			Assert.That(simulation.SegmentQuerySaturated, Is.True, "at reset");
			Assert.That(simulation.SaturatedSegmentQueries, Is.EqualTo(1));
			for (var step = 0; step < 20; step++)
			{
				simulation.Step(ControlInputs.Neutral);
			}

			Assert.That(simulation.SegmentQuerySaturated, Is.True);
			Assert.That(simulation.SaturatedSegmentQueries, Is.EqualTo(21), "every query since the reset");

			simulation.Reset(VesselState.Planar(0.0, -100.0, 0.0, 0.0));
			Assert.That(simulation.SegmentQuerySaturated, Is.False, "nothing nearby");
			Assert.That(simulation.SaturatedSegmentQueries, Is.EqualTo(0));
		}

		[Test]
		public void SegmentQuery_AtTheLimit_UsesEverySegment_UpToTheLast()
		{
			// Exactly as many segments nearby as the simulation takes: the last one, ahead of the bow, still counts.
			var segments = DenseFieldAstern(VesselDynamicsSimulation.MaximumSegments - 1);
			segments.Add(ContactScenarios.FaceToTheNorth(_bow + 0.05, -10.0, 10.0, ContactScenarios.EstimatedBargeFender, 99999));
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(segments), VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 0.5));
			var touched = false;
			for (var step = 0; step < 50 && !touched; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				Assert.That(simulation.SegmentQuerySaturated, Is.False);
				touched = simulation.ContactCount > 0;
			}

			Assert.That(touched, Is.True);
			Assert.That(simulation.GetContact(0).SegmentId, Is.EqualTo(99999));
			Assert.That(simulation.SaturatedSegmentQueries, Is.EqualTo(0));
		}

		[Test]
		public void PassingDenserStructures_DoesNotAllocate()
		{
			// The segment buffer and the contact model are sized when the simulation is built, so a step that meets
			// more structures than any step before it allocates nothing. Hundreds of short faces line the track 2 m
			// off the starboard side. The run is repeated with a new simulation each time: the runtime's tiered
			// compilation can allocate a little now and then, but growing a buffer would allocate in every run.
			var faces = new List<ContactSegment>();
			var off = ContactScenarios.HalfBeam(_parameters) + 2.0;
			for (var i = 0; i < 300; i++)
			{
				faces.Add(ContactScenarios.FaceToTheEast(off, 0.1 * i, 0.1 * i + 0.5, ContactScenarios.EstimatedBargeFender, i));
			}

			var geometry = new SegmentListGeometry(faces);
			var idle = ContactScenarios.IdleLever(_parameters);
			var least = long.MaxValue;
			for (var run = 0; run < 3; run++)
			{
				var simulation = ContactScenarios.Create(_parameters, geometry, VesselState.Planar(0.0, -40.0, 0.0, 0.0, surge: 2.0));
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var step = 0; step < 40 * SimulationClock.StepsPerSecond; step++)
				{
					simulation.Step(new ControlInputs(idle, idle, 0.0));
				}

				var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
				TestContext.Out.WriteLine($"{_vesselFile} run {run}: {allocated} bytes allocated");
				least = Math.Min(least, allocated);
				Assert.That(simulation.State.North, Is.GreaterThan(60.0), "the run passes the whole line of faces");
				Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(0.0));
			}

			Assert.That(least, Is.EqualTo(0));
		}

		[Test]
		public void MissingContactSection_IsAClearError_OnlyWhenThereIsGeometry()
		{
			var parameters = ManeuverRunner.Load(_vesselFile);
			parameters.Contact = null;
			var geometry = new SegmentListGeometry(ContactScenarios.Barge(_bow + 5.0, 0.0, ContactScenarios.EstimatedBargeFender, 1));
			var error = Assert.Throws<ArgumentException>(() => ContactScenarios.Create(parameters, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0)));
			Assert.That(error.Message, Does.Contain("contact"));
			Assert.That(ContactScenarios.Create(parameters, EmptyWorldGeometry.Instance, VesselState.Planar(0.0, 0.0, 0.0, 0.0)), Is.Not.Null,
				"open water does not need the hull outline");
		}

		private void AssertNoTunnelling(ContactSegment[] segments, in ContactSegment face, double heading)
		{
			var law = new ContactModel(_parameters).LawFor(ContactScenarios.EstimatedBargeFender);
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(segments), VesselState.Planar(0.0, 0.0, 0.0, heading, surge: SixKnots));
			var deepest = 0.0;
			var peakForce = 0.0;
			for (var step = 0; step < 15 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				deepest = Math.Max(deepest, ContactScenarios.MaxCompression(simulation));
				deepest = Math.Max(deepest, ContactScenarios.DeepestVertex(_parameters, simulation.State, face));
				peakForce = Math.Max(peakForce, simulation.Telemetry.ContactForce);
				Assert.That(simulation.State.IsFinite(), Is.True);
			}

			TestContext.Out.WriteLine($"{_vesselFile} at {Angles.ToDegrees(heading):F0} deg: deepest {deepest:F3} m, peak force {peakForce / 1000.0:F0} kN");
			Assert.That(peakForce, Is.GreaterThan(0.0), "the hull hits the face");
			Assert.That(deepest, Is.LessThan(law.FullCompression + 0.3), "stopped by the fenders and the structure behind them");
			Assert.That(ContactScenarios.DeepestVertex(_parameters, simulation.State, face), Is.LessThan(law.BottomingCompression),
				"and back on the water side afterwards");
		}

		private HoldResult Hold(ControlInputs controls, double seconds)
		{
			var segments = ContactScenarios.Barge(_bow + 0.05, 0.0, ContactScenarios.EstimatedBargeFender, 1);
			var geometry = new SegmentListGeometry(segments);
			var simulation = ContactScenarios.Create(_parameters, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			var result = new HoldResult { Simulation = simulation, Geometry = geometry };
			var steps = (int)Math.Round(seconds * SimulationClock.StepsPerSecond);
			var lastTen = steps - 10 * SimulationClock.StepsPerSecond;
			var lastThirty = steps - 30 * SimulationClock.StepsPerSecond;
			var low = double.MaxValue;
			var high = double.MinValue;
			var sum = 0.0;
			var count = 0;
			for (var step = 0; step < steps; step++)
			{
				simulation.Step(controls);
				if (step >= lastThirty && simulation.ContactCount == 0)
				{
					result.LostContact = true;
				}

				if (step == lastTen)
				{
					result.StartOfWindow = simulation.State;
				}

				if (step >= lastTen)
				{
					var force = simulation.Telemetry.ContactForce;
					low = Math.Min(low, force);
					high = Math.Max(high, force);
					sum += force;
					count++;
				}
			}

			result.MeanForce = count > 0 ? sum / count : 0.0;
			result.ForceRange = count > 0 ? high - low : 0.0;
			result.Compression = ContactScenarios.MaxCompression(simulation);
			result.Law = new ContactModel(_parameters).LawFor(ContactScenarios.EstimatedBargeFender);
			return result;
		}

		// At rest after a minute: under 2 mm/s and 0.01°/s, under 2 cm of drift over the last 10 s, and the
		// holding force steady within 1%. A boat still rocking on its fender (about 5 mm/s) fails this.
		private static void AssertHeld(HoldResult hold)
		{
			var simulation = hold.Simulation;
			Assert.That(hold.LostContact, Is.False, "the bow stays on the barge: no bouncing away");
			Assert.That(simulation.Telemetry.SpeedOverGround, Is.LessThan(0.002), "at rest within 2 mm/s after a minute");
			Assert.That(Math.Abs(simulation.State.YawRate), Is.LessThan(Angles.ToRadians(0.01)), "not swinging");
			Assert.That((simulation.State.Position - hold.StartOfWindow.Position).Magnitude, Is.LessThan(0.02), "no creep over the last 10 s");
			Assert.That(hold.MeanForce, Is.GreaterThan(1000.0), "pressed against the face");
			Assert.That(hold.ForceRange, Is.LessThan(0.01 * hold.MeanForce), "a steady holding force, no chatter");
			Assert.That(hold.Compression, Is.GreaterThan(0.0).And.LessThan(hold.Law.BottomingCompression), "on the fender, not through it");

			// At rest the contact holds exactly what the engines push with.
			var forces = simulation.LastForces;
			var load = simulation.ContactLoad;
			var surge = forces.HullX + forces.PropellerX + forces.RudderX + forces.WindX + load.X;
			var sway = forces.HullY + forces.PropellerY + forces.RudderY + forces.WindY + load.Y;
			Assert.That(Math.Abs(surge), Is.LessThan(0.03 * load.Magnitude), $"surge balance {surge} N");
			Assert.That(Math.Abs(sway), Is.LessThan(0.03 * load.Magnitude), $"sway balance {sway} N");
		}

		// Faces 2 m astern of the transom, 10 µm apart, facing the hull: inside the query radius, never touched.
		private List<ContactSegment> DenseFieldAstern(int count)
		{
			var stern = ContactScenarios.SternX(_parameters);
			var segments = new List<ContactSegment>(count + 1);
			for (var i = 0; i < count; i++)
			{
				var north = stern - 2.0 - 1e-5 * i;
				segments.Add(new ContactSegment(new HorizontalVector(north, -10.0), new HorizontalVector(north, 10.0), ContactScenarios.EstimatedBargeFender, i));
			}

			return segments;
		}

		private static ContactSegment FindSegment(SegmentListGeometry geometry, int id)
		{
			foreach (var segment in geometry.Segments)
			{
				if (segment.Id == id)
				{
					return segment;
				}
			}

			throw new InvalidOperationException($"no segment {id}");
		}

		private static ControlInputs ScriptedInputs(int step)
		{
			var t = step / (double)SimulationClock.StepsPerSecond;
			return new ControlInputs(0.3 + 0.2 * Math.Sin(0.11 * t), 0.3 + 0.2 * Math.Cos(0.07 * t), Math.Sin(0.05 * t));
		}

		private sealed class HoldResult
		{
			public VesselDynamicsSimulation Simulation;
			public SegmentListGeometry Geometry;
			public VesselState StartOfWindow;
			public FenderLaw Law;
			public double MeanForce;
			public double ForceRange;
			public double Compression;
			public bool LostContact;
		}
	}
}
