using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Tests.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>Results of one run onto a shoal, SI units.</summary>
	public sealed class GroundingRun
	{
		public double ImpactSpeed;
		public double StoppingDistance;
		public double StoppingTime;
		public double PeakForce;
		public double PeakPenetration;
		public double FinalPenetration;
		public bool Stopped;
		public bool Bounced;
	}

	/// <summary>
	/// The hull meeting the bed. Failure modes guarded:
	/// <list type="bullet">
	/// <item>A boat running onto a shoal at 5 kn not stopping, stopping in an implausible distance, bouncing back, or
	/// producing NaN; the grounded flag not set.</item>
	/// <item>Deep water ever grounding, or any bed force when the keel is clear.</item>
	/// <item>The bed friction injecting energy, or chattering and creeping when held aground under power.</item>
	/// <item><see cref="VesselTelemetry.ContactForce"/> leaving the bed load out, or summing magnitudes instead of
	/// reporting the resultant when a fender touches at the same time.</item>
	/// <item>A boat that stays stuck when the tide lifts it off.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class GroundingTests
	{
		/// <summary>The touch point, m north of the origin, where the shoal's bed reaches the keel.</summary>
		public const double ShoalStart = 100.0;

		public const double ShoalEnd = 300.0;

		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public GroundingTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
		}

		[Test]
		public void AFiveKnotRunOntoAShoal_InGearAtIdle_StopsWithinABoundedDistance()
		{
			var run = RunOntoShoal(_parameters, ContactScenarios.IdleLever(_parameters), out var simulation);
			Report("idle ahead", run);
			Assert.That(Units.MetresPerSecondToKnots(run.ImpactSpeed), Is.EqualTo(5.0).Within(0.3), "met the bed at 5 kn");
			Assert.That(run.Stopped, Is.True, "stopped");
			Assert.That(run.StoppingDistance, Is.LessThan(25.0), "within a boat length of the touch point");
			Assert.That(run.StoppingDistance, Is.GreaterThan(1.0), "not an instant stop on a 1 in 29 slope");
			Assert.That(run.Bounced, Is.False, "the bed holds the boat; it does not throw it back");
			Assert.That(run.PeakPenetration, Is.LessThan(simulation.Parameters.Particulars.Draft.Value), "the hull is not driven out of the water");
			Assert.That(simulation.Grounding.Grounded, Is.True);
			Assert.That(simulation.IsAground, Is.True);
		}

		[Test]
		public void AFiveKnotRunOntoAShoal_Coasting_StopsSooner()
		{
			var coasting = RunOntoShoal(_parameters, 0.0, out var simulation);
			var driven = RunOntoShoal(_parameters, ContactScenarios.IdleLever(_parameters), out _);
			Report("coasting", coasting);
			Assert.That(Units.MetresPerSecondToKnots(coasting.ImpactSpeed), Is.EqualTo(5.0).Within(0.3), "met the bed at 5 kn");
			Assert.That(coasting.Stopped, Is.True);
			Assert.That(coasting.StoppingDistance, Is.LessThan(driven.StoppingDistance), "idle thrust pushes the boat further up the shoal");
			Assert.That(simulation.Grounding.Grounded, Is.True);
		}

		[Test]
		public void DeepWater_NeverGrounds()
		{
			var environment = new DataEnvironment(PackFixtures.DeepStillWater(), PackFixtures.Epoch);
			var simulation = new VesselDynamicsSimulation(_parameters, environment, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 8.0));
			for (var step = 0; step < 300 * SimulationClock.StepsPerSecond; step++)
			{
				var phase = step / (15 * SimulationClock.StepsPerSecond) % 3;
				simulation.Step(phase == 0 ? new ControlInputs(1.0, -1.0, 1.0) : phase == 1 ? new ControlInputs(1.0, 1.0, -1.0) : new ControlInputs(-1.0, -1.0, 0.0));
				var grounding = simulation.Grounding;
				Assert.That(grounding.Grounded, Is.False, $"step {step}");
				Assert.That(grounding.Penetration, Is.EqualTo(0.0));
				Assert.That(grounding.BedLoad, Is.EqualTo(0.0));
				Assert.That(grounding.Force, Is.EqualTo(0.0));
				Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(0.0));
				Assert.That(simulation.LastForces.GroundingX, Is.EqualTo(0.0));
			}
		}

		[Test]
		public void Aground_ContactForceIsTheBedLoadsMagnitude_AndTheBedNeverPushes()
		{
			// Straight onto the shoal at idle, then split engines and helm to twist the boat while it is held.
			var idle = ContactScenarios.IdleLever(_parameters);
			var simulation = Create(ShoalEnvironment(), VesselState.Planar(0.0, TouchPoint(_parameters) - 10.0, 0.0, 0.0, surge: Units.KnotsToMetresPerSecond(5.0)));
			var grounded = 0;
			for (var step = 0; step < 60 * SimulationClock.StepsPerSecond; step++)
			{
				var phase = grounded / (5 * SimulationClock.StepsPerSecond) % 3;
				var inputs = grounded == 0 ? new ControlInputs(idle, idle, 0.0) :
					phase == 0 ? new ControlInputs(0.6, -0.6, 1.0) : phase == 1 ? new ControlInputs(-0.6, 0.6, -1.0) : new ControlInputs(-1.0, -1.0, 0.0);
				simulation.Step(inputs);
				var grounding = simulation.Grounding;
				if (!grounding.Grounded)
				{
					Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(0.0));
					continue;
				}

				grounded++;
				Assert.That(simulation.ContactCount, Is.EqualTo(0));
				Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(grounding.Force), $"step {step}");
				var state = simulation.State;
				var power = grounding.Load.X * state.Surge + grounding.Load.Y * state.Sway + grounding.Load.N * state.YawRate;
				Assert.That(power, Is.LessThanOrEqualTo(1e-9 * (grounding.Force + 1.0)), $"step {step}: the bed's friction never does positive work");
			}

			Assert.That(grounded, Is.GreaterThan(30 * SimulationClock.StepsPerSecond), "most of the run is aground");
		}

		[Test]
		public void AFenderTouchAndAGroundingInOneStep_GiveTheResultant_NotTheSumOfMagnitudes()
		{
			// Side-to a face to starboard, sliding north along it and closing at 0.3 m/s, in water 0.2 m shallower than the keel.
			var halfBeam = ContactScenarios.HalfBeam(_parameters);
			var face = ContactScenarios.FaceToTheEast(halfBeam + 0.05, -40.0, 40.0, ContactScenarios.EstimatedBargeFender, 7);
			var probe = ManeuverRunner.Create(_parameters);
			var environment = new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, probe.KeelDraft - 0.2);
			var simulation = ContactScenarios.Create(_parameters, new SegmentListGeometry(new[] { face }),
				VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 0.6, sway: 0.3), environment);
			var both = 0;
			var fenderAtBest = 0.0;
			var bedAtBest = 0.0;
			var forceAtBest = 0.0;
			for (var step = 0; step < 5 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(ControlInputs.Neutral);
				var fender = simulation.ContactLoad;
				var bed = simulation.Grounding.Load;
				if (simulation.ContactCount == 0 || bed.Magnitude == 0.0)
				{
					continue;
				}

				both++;
				var north = fender.North + bed.North;
				var east = fender.East + bed.East;
				Assert.That(simulation.Telemetry.ContactForce, Is.EqualTo(Math.Sqrt(north * north + east * east)).Within(1e-9).Percent, $"step {step}");
				if (Math.Min(fender.Magnitude, bed.Magnitude) > Math.Min(fenderAtBest, bedAtBest))
				{
					fenderAtBest = fender.Magnitude;
					bedAtBest = bed.Magnitude;
					forceAtBest = simulation.Telemetry.ContactForce;
				}
			}

			TestContext.Out.WriteLine($"{_vesselFile}: fender {fenderAtBest / 1000.0:F2} kN and bed {bedAtBest / 1000.0:F2} kN together give {forceAtBest / 1000.0:F2} kN");
			Assert.That(both, Is.GreaterThan(10), "fender and bed act together for a while");
			Assert.That(forceAtBest, Is.LessThan(0.99 * (fenderAtBest + bedAtBest)), "the resultant, not the sum of magnitudes: the loads are not collinear");
			Assert.That(forceAtBest, Is.GreaterThan(Math.Max(fenderAtBest, bedAtBest) * 0.5));
		}

		[Test]
		public void HeldAgroundOnDryLand_UnderHarshInputs_StaysFiniteAndBarelyMoves()
		{
			var simulation = Create(new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, 0.0), VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			var start = simulation.State.Position;
			for (var step = 0; step < 120 * SimulationClock.StepsPerSecond; step++)
			{
				var phase = step / (20 * SimulationClock.StepsPerSecond) % 3;
				simulation.Step(phase == 0 ? new ControlInputs(1.0, 1.0, 1.0) : phase == 1 ? new ControlInputs(1.0, -1.0, -1.0) : new ControlInputs(-1.0, -1.0, 0.0));
				Assert.That(simulation.State.IsFinite(), Is.True, $"step {step}");
			}

			var moved = (simulation.State.Position - start).Magnitude;
			TestContext.Out.WriteLine($"{_vesselFile}: on dry land under full power for 120 s, moved {moved:F3} m, turned {Angles.ToDegrees(Angles.WrapPi(simulation.State.Heading)):F2}°");
			Assert.That(simulation.Grounding.BedLoad, Is.EqualTo(_parameters.Particulars.Displacement.Value * PhysicalConstants.Gravity).Within(1e-9).Percent, "the bed carries the whole boat");
			Assert.That(moved, Is.LessThan(5.0), "full power does not drive a boat across dry land");
			Assert.That(Math.Abs(Angles.WrapPi(simulation.State.Heading)), Is.LessThan(Angles.ToRadians(10.0)));
		}

		[Test]
		public void ARisingTide_FloatsTheBoatOff()
		{
			// Flat bed 2.4 m below NAVD88; the tide rises from −1.0 to +0.5 m over 20 minutes.
			var grid = PackFixtures.FlatGrid("g", -500.0, -500.0, 10.0, 101, 101, -2.4f);
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, grid),
				new[] { PackFixtures.SteadyCurrent("C1", 0.0, 0.0, HorizontalVector.Zero) },
				new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => Math.Max(-1.0, Math.Min(0.5, -1.0 + 1.5 * t / 1200.0))) },
				new[] { PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.Zero) });
			var simulation = Create(new DataEnvironment(pack, PackFixtures.Epoch), VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			Assert.That(simulation.Grounding.Grounded, Is.True, "aground at low water");
			var floatedAt = double.NaN;
			for (var step = 0; step < 1500 * SimulationClock.StepsPerSecond; step++)
			{
				simulation.Step(new ControlInputs(ContactScenarios.IdleLever(_parameters), ContactScenarios.IdleLever(_parameters), 0.0));
				if (double.IsNaN(floatedAt) && !simulation.Grounding.Grounded)
				{
					floatedAt = simulation.State.Time;
				}
			}

			TestContext.Out.WriteLine($"{_vesselFile}: floated off at {floatedAt:F1} s, then made {simulation.State.North:F1} m");
			Assert.That(floatedAt, Is.GreaterThan(100.0), "held while the bed carries it");
			Assert.That(simulation.Grounding.Grounded, Is.False, "afloat at high water");
			Assert.That(simulation.State.North, Is.GreaterThan(50.0), "and free to make way");
		}

		[Test]
		public void TwoRunsFromTheSamePack_AreBitIdentical()
		{
			var pack = ShoalPack();
			var first = Create(new DataEnvironment(pack, PackFixtures.Epoch.AddHours(6.0)), VesselState.Planar(0.0, ShoalStart - 40.0, 20.0, 0.2, surge: 3.0));
			var second = Create(new DataEnvironment(pack, PackFixtures.Epoch.AddHours(6.0)), VesselState.Planar(0.0, ShoalStart - 40.0, 20.0, 0.2, surge: 3.0));
			for (var step = 0; step < 90 * SimulationClock.StepsPerSecond; step++)
			{
				var t = step / (double)SimulationClock.StepsPerSecond;
				var inputs = new ControlInputs(0.5 + 0.5 * Math.Sin(0.11 * t), 0.5 * Math.Cos(0.07 * t), Math.Sin(0.05 * t));
				first.Step(inputs);
				second.Step(inputs);
				var differences = BitIdentity.Differences(first, second);
				if (differences.Length > 0)
				{
					Assert.Fail($"step {step}: {differences}");
				}
			}

			Assert.That(first.Grounding.Grounded || first.State.North > ShoalStart, Is.True, "the run reached the shoal");
		}

		/// <summary>
		/// Runs bow-first onto <see cref="ShoalEnvironment"/> at 5 kn through the water from 2 m short of the touch
		/// point, both levers at <paramref name="lever"/>, until the boat has stopped for 2 s or 120 s have passed.
		/// </summary>
		public static GroundingRun RunOntoShoal(VesselParameters parameters, double lever, out VesselDynamicsSimulation simulation)
		{
			simulation = new VesselDynamicsSimulation(parameters, ShoalEnvironment(), VesselState.Planar(0.0, TouchPoint(parameters) - 2.0, 0.0, 0.0, surge: Units.KnotsToMetresPerSecond(5.0)));
			var run = new GroundingRun();
			var groundedAt = double.NaN;
			var stoppedFor = 0;
			var maxNorth = double.MinValue;
			for (var step = 0; step < 120 * SimulationClock.StepsPerSecond; step++)
			{
				var before = simulation.State;
				simulation.Step(new ControlInputs(lever, lever, 0.0));
				var state = simulation.State;
				if (!state.IsFinite())
				{
					throw new AssertionException($"non-finite state at step {step}: {state}");
				}

				var grounding = simulation.Grounding;
				if (double.IsNaN(groundedAt) && grounding.Grounded)
				{
					groundedAt = before.North;
					run.ImpactSpeed = before.GroundVelocity.Magnitude;
					run.StoppingTime = -state.Time;
				}

				run.PeakForce = Math.Max(run.PeakForce, grounding.Force);
				run.PeakPenetration = Math.Max(run.PeakPenetration, grounding.Penetration);
				maxNorth = Math.Max(maxNorth, state.North);
				if (!double.IsNaN(groundedAt) && maxNorth - state.North > 0.05)
				{
					run.Bounced = true;
				}

				stoppedFor = !double.IsNaN(groundedAt) && state.GroundVelocity.Magnitude < 0.01 ? stoppedFor + 1 : 0;
				if (stoppedFor >= 2 * SimulationClock.StepsPerSecond)
				{
					run.Stopped = true;
					run.StoppingTime += state.Time - 2.0;
					break;
				}
			}

			run.StoppingDistance = maxNorth - groundedAt;
			run.FinalPenetration = simulation.Grounding.Penetration;
			return run;
		}

		/// <summary>North of the origin where the shoal's bed reaches the keel draft at NAVD88 zero, m.</summary>
		public static double TouchPoint(VesselParameters parameters)
		{
			var keel = parameters.Particulars.MaxDraft?.Value ?? parameters.Particulars.Draft.Value;
			return ShoalStart + (6.0 - keel) / 7.0 * (ShoalEnd - ShoalStart);
		}

		/// <summary>
		/// Still water at NAVD88 zero over a bed 6 m down south of <see cref="ShoalStart"/>, rising evenly to 1 m above the
		/// water at <see cref="ShoalEnd"/> (a slope of 1 in 29) and dry beyond.
		/// </summary>
		public static DataEnvironment ShoalEnvironment() => new DataEnvironment(ShoalPack(), PackFixtures.Epoch);

		public static EnvironmentPack ShoalPack()
		{
			var grid = PackFixtures.Grid("shoal", -200.0, -200.0, 5.0, 141, 81, (r, c) =>
			{
				var north = -200.0 + 5.0 * r;
				return (float)Math.Min(1.0, north < ShoalStart ? -6.0 : -6.0 + 7.0 * (north - ShoalStart) / (ShoalEnd - ShoalStart));
			});
			return PackFixtures.Pack(PackFixtures.Depth(20.0, grid),
				new[] { PackFixtures.SteadyCurrent("C1", 0.0, 0.0, HorizontalVector.Zero) },
				new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) },
				new[] { PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.Zero) });
		}

		private VesselDynamicsSimulation Create(IEnvironment environment, VesselState initial) => new VesselDynamicsSimulation(_parameters, environment, initial);

		private void Report(string name, GroundingRun run) =>
			TestContext.Out.WriteLine($"{_vesselFile} {name}: met the bed at {Units.MetresPerSecondToKnots(run.ImpactSpeed):F2} kn, stopped in {run.StoppingDistance:F2} m and {run.StoppingTime:F1} s, " +
				$"peak bed force {run.PeakForce / 1000.0:F1} kN, peak penetration {run.PeakPenetration:F3} m, final {run.FinalPenetration:F3} m");
	}
}
