using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Fender hysteresis. Failure modes guarded:
	/// <list type="bullet">
	/// <item>The hysteresis term injecting energy at any instant or over a closed compression cycle, for
	/// random amplitudes (into the bottomed range) and rates (through the smoothing near zero rate).</item>
	/// <item>The smoothing through zero rate chattering or blowing up at 100 Hz: when held on the fender,
	/// and when thrust and helm jump about in contact.</item>
	/// <item>A hysteresis of 0 changing anything at all against the model without hysteresis.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class FenderHysteresisTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public FenderHysteresisTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
		}

		[TestCase(1)]
		[TestCase(2)]
		[TestCase(3)]
		[TestCase(4)]
		[TestCase(5)]
		public void Hysteresis_NeverInjectsEnergy_OverRandomCompressionCycles(int seed)
		{
			// The hull moves straight into and out of a face along a random closed compression history. With
			// viscous damping off, the force difference between hysteresis on and off is the hysteresis term:
			// its power must never be positive, and its work over the cycle must be negative.
			var withHysteresis = Model(0.2);
			var withoutHysteresis = Model(0.0);
			var bow = ContactScenarios.BowX(_parameters);
			var face = ContactScenarios.FaceToTheNorth(bow, -20.0, 20.0, Undamped, 1);
			ContactScenarios.Approach(withHysteresis, new[] { face }, 0.02, 0.0, 0.0);
			ContactScenarios.Approach(withoutHysteresis, new[] { face }, 0.02, 0.0, 0.0);

			var random = new Random(seed);
			const int steps = 2000;
			var compression = 0.02;
			var hysteresisWork = 0.0;
			var elasticWork = 0.0;
			var peakEnergy = 0.0;
			var samples = 0;
			for (var leg = 0; leg < 40; leg++)
			{
				// Random target into the bottomed range at a random speed from far below the smoothing speed up
				// to 1 m/s; the last leg returns to the start to close the cycle.
				var target = leg == 39 ? 0.02 : 0.45 * random.NextDouble();
				var speed = Math.Pow(10.0, -4.0 + 4.0 * random.NextDouble());
				var rate = target > compression ? speed : -speed;
				var dt = Math.Abs(target - compression) / speed / steps;
				for (var i = 0; i < steps; i++)
				{
					var midpoint = compression + 0.5 * rate * dt;
					Force(withHysteresis, midpoint, rate, out var on);
					Force(withoutHysteresis, midpoint, rate, out var off);
					var hysteresisPower = (on - off) * rate;
					Assert.That(hysteresisPower, Is.LessThanOrEqualTo(1e-9 * Math.Abs(off * rate) + 1e-12), $"leg {leg} step {i}");
					hysteresisWork += hysteresisPower * dt;
					elasticWork += off * rate * dt;
					peakEnergy = Math.Max(peakEnergy, -elasticWork);
					compression += rate * dt;
					samples++;
				}

				compression = target;
			}

			TestContext.Out.WriteLine($"{_vesselFile} seed {seed}: {samples} samples, hysteresis work {hysteresisWork:F1} J, elastic work round the cycle {elasticWork:F3} J (peak {peakEnergy:F0} J)");
			Assert.That(compression, Is.EqualTo(0.02).Within(1e-9), "the cycle closes");
			Assert.That(Math.Abs(elasticWork), Is.LessThan(1e-3 * peakEnergy + 1e-6), "the elastic law alone gives back what it took");
			Assert.That(hysteresisWork, Is.LessThan(0.0), "hysteresis takes energy out");
		}

		[Test]
		public void HeldOnTheFender_TheForceSettlesWithoutChatter()
		{
			var idle = ContactScenarios.IdleLever(_parameters);
			var simulation = ContactScenarios.Create(_parameters,
				new SegmentListGeometry(ContactScenarios.Barge(ContactScenarios.BowX(_parameters) + 0.05, 0.0, ContactScenarios.EstimatedBargeFender, 1)),
				VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			// Preserve d769106's physical -17.5° order after the 35° to 20° stop change.
			var controls = new ControlInputs(idle, 0.0, -0.875);
			ManeuverRunner.Run(simulation, controls, 50.0);
			var previous = simulation.Telemetry.ContactForce;
			var largestJump = 0.0;
			for (var step = 0; step < 10 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(controls);
				var force = simulation.Telemetry.ContactForce;
				largestJump = Math.Max(largestJump, Math.Abs(force - previous));
				previous = force;
			}

			Assert.That(previous, Is.GreaterThan(1000.0));
			Assert.That(largestJump, Is.LessThan(1e-4 * previous), "step to step the holding force barely moves");
		}

		[Test]
		public void RandomThrustAndHelm_InContact_StayFiniteAndOnTheFender()
		{
			var random = new Random(11);
			var simulation = ContactScenarios.Create(_parameters,
				new SegmentListGeometry(ContactScenarios.Barge(ContactScenarios.BowX(_parameters) + 0.05, 0.0, ContactScenarios.EstimatedBargeFender, 1)),
				VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			var law = simulation.Contact.LawFor(ContactScenarios.EstimatedBargeFender);
			var controls = ControlInputs.Neutral;
			var inContact = 0;
			for (var step = 0; step < 180 * SimulationClock.StepsPerSecond; step++)
			{
				if (step % 200 == 0)
				{
					// Mostly ahead, so the boat keeps pushing on the barge between bursts.
					controls = new ControlInputs(0.7 * random.NextDouble() - 0.1, 0.7 * random.NextDouble() - 0.1, 2.0 * random.NextDouble() - 1.0);
				}

				simulation.Step(controls);
				Assert.That(simulation.State.IsFinite(), Is.True, $"step {step}");
				// crew decision 2026-10-26: a hard oblique re-hit may bottom a fender; bounded at 10 cm
				Assert.That(ContactScenarios.MaxCompression(simulation), Is.LessThanOrEqualTo(law.FullCompression + 0.10), $"step {step}");
				inContact += simulation.ContactCount > 0 ? 1 : 0;
			}

			Assert.That(inContact, Is.GreaterThan(60 * SimulationClock.StepsPerSecond), "most of the run is spent against the barge");
		}

		[Test]
		public void ZeroHysteresis_IsBitIdenticalToTheModelWithoutHysteresis()
		{
			// The acceptance rule, within one build: the same contact runs with fenderHysteresis 0, and with the
			// shipped value but hysteresis switched off, agree in every bit of state, telemetry, contact load and
			// per-segment contact at every step. A 3 kn landing, a 6 kn hit 30° off the normal (fenders bottomed,
			// sliding), a bow held for 60 s on one engine, and scripted thrust and helm along a barge.
			Assert.That(_parameters.Contact.FenderHysteresis.Value, Is.GreaterThan(0.0), "the switched-off run would otherwise prove nothing");
			var bow = ContactScenarios.BowX(_parameters);
			var fender = ContactScenarios.EstimatedBargeFender;
			var oblique = Angles.ToRadians(30.0);
			var idle = ContactScenarios.IdleLever(_parameters);
			BothWays("3 kn landing", ContactScenarios.Barge(bow + 0.2, 0.0, fender, 1),
				VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 3.0 * Units.MetresPerSecondPerKnot), 20.0, t => ControlInputs.Neutral);
			BothWays("6 kn at 30°", new[] { ContactScenarios.FaceToTheNorth(ContactScenarios.MaxNorth(_parameters, oblique) + 0.3, -300.0, 300.0, fender, 1) },
				VesselState.Planar(0.0, 0.0, 0.0, oblique, surge: 6.0 * Units.MetresPerSecondPerKnot), 15.0, t => ControlInputs.Neutral);
			BothWays("held bow", ContactScenarios.Barge(bow + 0.05, 0.0, fender, 1), VesselState.Planar(0.0, 0.0, 0.0, 0.0), 60.0,
				t => new ControlInputs(idle, 0.0, -0.5));
			BothWays("scripted", ContactScenarios.Barge(bow + 0.5, 3.0, fender, 1), VesselState.Planar(0.0, 0.0, 0.0, Angles.ToRadians(5.0), surge: 1.5), 30.0,
				t => new ControlInputs(0.3 + 0.2 * Math.Sin(0.11 * t), 0.3 + 0.2 * Math.Cos(0.07 * t), Math.Sin(0.05 * t)));
		}

		private void BothWays(string name, ContactSegment[] segments, in VesselState initial, double seconds, Func<double, ControlInputs> controls)
		{
			var zero = ManeuverRunner.Load(_vesselFile);
			zero.Contact.FenderHysteresis.Value = 0.0;
			var geometry = new SegmentListGeometry(segments);
			var withZero = ContactScenarios.Create(zero, geometry, initial);
			var switchedOff = ContactScenarios.Create(_parameters, geometry, initial);
			switchedOff.Contact.UseHysteresis = false;
			switchedOff.Reset(initial);
			var touched = 0;
			var steps = (int)Math.Round(seconds * SimulationClock.StepsPerSecond);
			for (var step = 0; step < steps; step++)
			{
				var inputs = controls(step / (double)SimulationClock.StepsPerSecond);
				withZero.Step(inputs);
				switchedOff.Step(inputs);
				var differences = Differences(withZero, switchedOff);
				if (differences.Length > 0)
				{
					Assert.Fail($"{_vesselFile} {name}, step {step}: {differences}");
				}

				touched += withZero.ContactCount > 0 ? 1 : 0;
			}

			TestContext.Out.WriteLine($"{_vesselFile} {name}: {steps} steps, {touched} in contact, bit-identical");
			Assert.That(touched, Is.GreaterThan(0), $"{name} touches");
		}

		// The quantities of a and b whose bits differ, as text; empty when every bit agrees.
		private static string Differences(VesselDynamicsSimulation a, VesselDynamicsSimulation b)
		{
			var text = new System.Text.StringBuilder();
			var x = a.State;
			var y = b.State;
			Compare(text, "time", x.Time, y.Time);
			Compare(text, "north", x.North, y.North);
			Compare(text, "east", x.East, y.East);
			Compare(text, "down", x.Down, y.Down);
			Compare(text, "roll", x.Roll, y.Roll);
			Compare(text, "pitch", x.Pitch, y.Pitch);
			Compare(text, "heading", x.Heading, y.Heading);
			Compare(text, "surge", x.Surge, y.Surge);
			Compare(text, "sway", x.Sway, y.Sway);
			Compare(text, "heave", x.Heave, y.Heave);
			Compare(text, "roll rate", x.RollRate, y.RollRate);
			Compare(text, "pitch rate", x.PitchRate, y.PitchRate);
			Compare(text, "yaw rate", x.YawRate, y.YawRate);
			var s = a.Telemetry;
			var t = b.Telemetry;
			Compare(text, "contact force", s.ContactForce, t.ContactForce);
			Compare(text, "speed through water", s.SpeedThroughWater, t.SpeedThroughWater);
			Compare(text, "SOG", s.SpeedOverGround, t.SpeedOverGround);
			Compare(text, "COG", s.CourseOverGround, t.CourseOverGround);
			Compare(text, "depth under keel", s.DepthUnderKeel, t.DepthUnderKeel);
			Compare(text, "port rudder", s.PortRudderAngle, t.PortRudderAngle);
			Compare(text, "starboard rudder", s.StarboardRudderAngle, t.StarboardRudderAngle);
			Compare(text, "port engine", s.Port.EngineSpeed, t.Port.EngineSpeed);
			Compare(text, "port shaft", s.Port.ShaftSpeed, t.Port.ShaftSpeed);
			Compare(text, "port throttle", s.Port.Throttle, t.Port.Throttle);
			Compare(text, "starboard engine", s.Starboard.EngineSpeed, t.Starboard.EngineSpeed);
			Compare(text, "starboard shaft", s.Starboard.ShaftSpeed, t.Starboard.ShaftSpeed);
			Compare(text, "starboard throttle", s.Starboard.Throttle, t.Starboard.Throttle);
			var p = a.ContactLoad;
			var q = b.ContactLoad;
			Compare(text, "load north", p.North, q.North);
			Compare(text, "load east", p.East, q.East);
			Compare(text, "load X", p.X, q.X);
			Compare(text, "load Y", p.Y, q.Y);
			Compare(text, "load N", p.N, q.N);
			var f = a.LastForces;
			var g = b.LastForces;
			Compare(text, "contact X at step start", f.ContactX, g.ContactX);
			Compare(text, "contact Y at step start", f.ContactY, g.ContactY);
			Compare(text, "contact N at step start", f.ContactN, g.ContactN);
			if (a.ContactCount != b.ContactCount)
			{
				text.Append($"contacts {a.ContactCount} and {b.ContactCount}; ");
				return text.ToString();
			}

			for (var i = 0; i < a.ContactCount; i++)
			{
				var m = a.GetContact(i);
				var n = b.GetContact(i);
				if (m.SegmentId != n.SegmentId || m.BottomedOut != n.BottomedOut)
				{
					text.Append($"contact {i} segment or bottoming; ");
				}

				Compare(text, $"contact {i} normal", m.NormalForce, n.NormalForce);
				Compare(text, $"contact {i} friction", m.FrictionForce, n.FrictionForce);
				Compare(text, $"contact {i} compression", m.Compression, n.Compression);
				Compare(text, $"contact {i} length", m.ContactLength, n.ContactLength);
			}

			return text.ToString();
		}

		private static void Compare(System.Text.StringBuilder text, string name, double a, double b)
		{
			if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b))
			{
				text.Append($"{name} {a:R} and {b:R}; ");
			}
		}

		private static readonly ContactSurface Undamped = new ContactSurface(ContactScenarios.EstimatedBargeFender.Stiffness, 0.0, 0.3, 0.25);

		private ContactModel Model(double hysteresis)
		{
			var parameters = ManeuverRunner.Load(_vesselFile);
			parameters.Contact.HullFenderDamping.Value = 0.0;
			parameters.Contact.FenderHysteresis.Value = hysteresis;
			return new ContactModel(parameters);
		}

		// Surge force on a hull at the origin heading north whose bow sits this far into the face, closing at
		// the given rate.
		private static void Force(ContactModel model, double compression, double rate, out double x) =>
			model.Forces(compression, 0.0, 0.0, rate, 0.0, 0.0, ContactModel.MinimumSlipSpeed, false, out x, out _, out _);
	}
}
