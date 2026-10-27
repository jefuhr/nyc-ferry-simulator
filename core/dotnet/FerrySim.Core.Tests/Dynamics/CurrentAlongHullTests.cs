using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Tests.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The current sampled along the hull. Failure modes guarded:
	/// <list type="bullet">
	/// <item>A uniform current changing any bit of state, telemetry or forces against the model that samples the
	/// reference point only (the v0.1.0 model, <see cref="VesselDynamicsSimulation.UseCurrentAlongHull"/> off).</item>
	/// <item>A current stronger toward starboard at the bow turning the bow to port, or not turning it at all; a body-frame
	/// rotation that works at one heading only.</item>
	/// <item>A cross current over part of the hull not pushing the boat sideways the way it is going.</item>
	/// <item>A current that changes only along the hull's axis yawing or pushing the boat sideways.</item>
	/// <item>A boat drifting with the water next to a station not following that station's current.</item>
	/// <item>A strong shear blowing up; allocation per step in a varying field.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class CurrentAlongHullTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public CurrentAlongHullTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
		}

		[Test]
		public void UniformCurrent_IsBitIdenticalToTheReferencePointModel()
		{
			var environment = new UniformEnvironment(HorizontalVector.FromSpeedToward(1.2, Angles.ToRadians(200.0)), HorizontalVector.FromSpeedFrom(12.0, Angles.ToRadians(300.0)), 8.0);
			var initial = VesselState.Planar(0.0, 10.0, -5.0, 1.0, surge: 4.0, sway: 0.3);
			Compare(Simulation(environment, initial, true), Simulation(environment, initial, false), 120.0);
		}

		[Test]
		public void AOneStationPack_IsBitIdenticalToTheReferencePointModel()
		{
			// One current station blends to a uniform field, so the hull's samples are all equal and nothing changes.
			var pack = PackFixtures.Pack(PackFixtures.Depth(20.0, PackFixtures.FlatGrid("g", -3000.0, -3000.0, 10.0, 601, 601, -9.0f)),
				new[] { PackFixtures.Current("C1", 0.0, 0.0, t => HorizontalVector.FromSpeedToward(1.0 + 0.5 * Math.Sin(t / 600.0), 0.8)) },
				new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.6 * Math.Sin(t / 7116.0)) },
				new[] { PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.FromSpeedFrom(7.0, 4.0)) });
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.3, surge: 3.0);
			Compare(Simulation(new DataEnvironment(pack, PackFixtures.Epoch), initial, true), Simulation(new DataEnvironment(pack, PackFixtures.Epoch), initial, false), 120.0);
		}

		[TestCase(0.0)]
		[TestCase(90.0)]
		[TestCase(225.0)]
		public void ACurrentStrongerAtTheBowTowardStarboard_TurnsTheBowToStarboard(double headingDegrees)
		{
			// The water's starboard velocity grows 0.02 m/s per metre forward: the bow is set to starboard, the stern to port.
			var heading = Angles.ToRadians(headingDegrees);
			var withHull = Drifting(Shear(heading, 0.02), heading, true, 20.0);
			var referenceOnly = Drifting(Shear(heading, 0.02), heading, false, 20.0);
			var turned = Angles.WrapPi(withHull.State.Heading - heading);
			TestContext.Out.WriteLine($"{_vesselFile} heading {headingDegrees}: turned {Angles.ToDegrees(turned):F2}° in 20 s, yaw rate {Angles.ToDegrees(withHull.State.YawRate):F3} °/s");
			Assert.That(withHull.State.YawRate, Is.GreaterThan(0.0), "turning to starboard");
			Assert.That(turned, Is.GreaterThan(Angles.ToRadians(1.0)));
			Assert.That(Math.Abs(referenceOnly.State.YawRate), Is.LessThan(1e-9), "the reference point alone feels no shear");

			var opposite = Drifting(Shear(heading, -0.02), heading, true, 20.0);
			Assert.That(opposite.State.YawRate, Is.LessThan(0.0), "stronger toward port at the bow turns the bow to port");
		}

		[Test]
		public void ACrossCurrentOverTheBow_PushesTheBoatSideways_AndTurnsItDownstream()
		{
			// Still water at the reference point; a 1 m/s current toward starboard (east) from 4 m forward of it.
			var environment = new FieldEnvironment((north, east) => north > 4.0 ? new HorizontalVector(0.0, 1.0) : HorizontalVector.Zero);
			var simulation = Simulation(environment, VesselState.Planar(0.0, 0.0, 0.0, 0.0), true);
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, 10.0);
			TestContext.Out.WriteLine($"{_vesselFile}: after 10 s east {simulation.State.East:F3} m, heading {Angles.ToDegrees(simulation.State.Heading):F2}°");
			Assert.That(simulation.State.GroundVelocity.East, Is.GreaterThan(0.0), "carried toward starboard");
			Assert.That(simulation.State.East, Is.GreaterThan(0.05));
			Assert.That(simulation.State.YawRate, Is.GreaterThan(0.0), "the bow is set downstream first");
		}

		[Test]
		public void ACurrentChangingOnlyAlongTheHull_NeitherYawsNorPushesSideways()
		{
			// The water flows along the hull, faster toward the bow: symmetric about the centreline.
			var environment = new FieldEnvironment((north, east) => new HorizontalVector(0.5 + 0.02 * north, 0.0));
			var simulation = Simulation(environment, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 0.5), true);
			ManeuverRunner.Run(simulation, new ControlInputs(0.15, 0.15, 0.0), 30.0);
			Assert.That(Math.Abs(simulation.State.YawRate), Is.LessThan(1e-9));
			Assert.That(Math.Abs(simulation.State.East), Is.LessThan(1e-9));
			Assert.That(simulation.State.North, Is.GreaterThan(10.0));
		}

		[Test]
		public void DriftingWithTheWaterNextToAStation_FollowsThatStationsCurrent()
		{
			var current = HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(45.0));
			var environment = new DataEnvironment(ThreeStationPack(current), PackFixtures.Epoch.AddHours(10.0));
			var station = environment.Pack.CurrentStations[0];
			var start = VesselState.Planar(0.0, station.North + 20.0, station.East - 15.0, 0.0,
				current.North, current.East);
			var simulation = Simulation(environment, start, true);
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, 300.0);

			var drift = simulation.State.GroundVelocity;
			var travelled = simulation.State.Position - start.Position;
			TestContext.Out.WriteLine($"{_vesselFile}: drift with the water {drift.Magnitude:F4} m/s toward {Angles.ToDegrees(drift.Direction):F2}°, " +
				$"{travelled.Magnitude:F2} m in 300 s; station {current.Magnitude:F4} m/s toward 45°");
			Assert.That((drift - current).Magnitude, Is.LessThan(0.01));
			Assert.That((travelled - current * 300.0).Magnitude, Is.LessThan(3.0));
			Assert.That(Math.Abs(simulation.Telemetry.SpeedThroughWater), Is.LessThan(0.01), "no flow past the hull");
		}

		[Test]
		public void FromRestNextToAStation_TheBoatIsCarriedTowardThatStationsCurrent()
		{
			var current = HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(45.0));
			var environment = new DataEnvironment(ThreeStationPack(current), PackFixtures.Epoch.AddHours(10.0));
			var station = environment.Pack.CurrentStations[0];
			var simulation = Simulation(environment, VesselState.Planar(0.0, station.North, station.East, 0.0), true);
			var speeds = new double[4];
			for (var i = 0; i < speeds.Length; i++)
			{
				ManeuverRunner.Run(simulation, ControlInputs.Neutral, 150.0);
				speeds[i] = simulation.State.GroundVelocity.Magnitude;
			}

			var drift = simulation.State.GroundVelocity;
			TestContext.Out.WriteLine($"{_vesselFile}: drift from rest {speeds[0]:F3}, {speeds[1]:F3}, {speeds[2]:F3}, {speeds[3]:F3} m/s at 150, 300, 450, 600 s, toward {Angles.ToDegrees(drift.Direction):F1}°");
			Assert.That(speeds[1], Is.GreaterThan(speeds[0]));
			Assert.That(speeds[3], Is.GreaterThan(0.75 * current.Magnitude), "carried at most of the station's current within ten minutes");
			// crew decision 2026-10-26: a hull drifting across a turning current can briefly outrun it; the calibrated yaw damping makes this transient larger (T peak about 2.7%)
			Assert.That(speeds[3], Is.LessThan(1.03 * current.Magnitude), "the boat stays within 3% of the station's current");
		}

		[Test]
		public void AStrongShear_UnderHarshInputs_StaysFinite()
		{
			var simulation = Simulation(Shear(0.0, 0.2), VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: 6.0), true);
			for (var step = 0; step < 300 * SimulationClock.StepsPerSecond; step++)
			{
				var phase = step / (15 * SimulationClock.StepsPerSecond) % 3;
				simulation.Step(phase == 0 ? new ControlInputs(1.0, -1.0, 1.0) : phase == 1 ? new ControlInputs(1.0, 1.0, -1.0) : new ControlInputs(-1.0, -1.0, 0.0));
				Assert.That(simulation.State.IsFinite(), Is.True, $"step {step}");
			}

			Assert.That(Math.Abs(simulation.State.YawRate), Is.LessThan(1.0), "no runaway spin");
		}

		[Test]
		public void Step_InAVaryingField_DoesNotAllocate()
		{
			var current = HorizontalVector.FromSpeedToward(1.0, Angles.ToRadians(45.0));
			var environment = new DataEnvironment(ThreeStationPack(current), PackFixtures.Epoch.AddHours(10.0));
			var simulation = Simulation(environment, VesselState.Planar(0.0, 500.0, 300.0, 1.0, surge: 3.0), true);
			var inputs = new ControlInputs(0.8, -0.4, 0.5);
			ManeuverRunner.Run(simulation, inputs, 5.0);
			// The runtime may allocate once while it recompiles a hot method; an allocation per step shows in every round.
			var allocated = -1L;
			for (var round = 0; round < 3 && allocated != 0; round++)
			{
				var before = GC.GetAllocatedBytesForCurrentThread();
				for (var step = 0; step < 10000; step++)
				{
					simulation.Step(inputs);
				}

				allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			}

			Assert.That(allocated, Is.EqualTo(0));
		}

		/// <summary>
		/// Three current stations 10 km apart, the first at the origin with a steady current; a deep grid around it.
		/// </summary>
		public static EnvironmentPack ThreeStationPack(HorizontalVector first)
		{
			var grid = PackFixtures.FlatGrid("g", -2000.0, -2000.0, 10.0, 401, 401, -15.0f);
			var currents = new[]
			{
				PackFixtures.SteadyCurrent("C1", 0.0, 0.0, first),
				PackFixtures.SteadyCurrent("C2", 10000.0, 0.0, HorizontalVector.FromSpeedToward(0.5, Angles.ToRadians(200.0))),
				PackFixtures.SteadyCurrent("C3", 0.0, 10000.0, new HorizontalVector(-0.3, 0.6)),
			};
			return PackFixtures.Pack(PackFixtures.Depth(20.0, grid), currents,
				new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) }, new[] { PackFixtures.Wind("W1", 0.0, 0.0, t => HorizontalVector.Zero) });
		}

		// A current toward starboard of the heading whose speed grows by gradient per metre ahead (pure shear across the hull).
		private static IEnvironment Shear(double heading, double gradient)
		{
			var ahead = new HorizontalVector(Math.Cos(heading), Math.Sin(heading));
			var starboard = new HorizontalVector(-Math.Sin(heading), Math.Cos(heading));
			return new FieldEnvironment((north, east) => starboard * (gradient * (north * ahead.North + east * ahead.East)));
		}

		// A boat moving with the water at its reference point, engines in neutral.
		private VesselDynamicsSimulation Drifting(IEnvironment environment, double heading, bool alongHull, double seconds)
		{
			var current = environment.SampleCurrent(0.0, 0.0, 0.0);
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var initial = VesselState.Planar(0.0, 0.0, 0.0, heading, current.North * cos + current.East * sin, -current.North * sin + current.East * cos);
			var simulation = Simulation(environment, initial, alongHull);
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, seconds);
			return simulation;
		}

		private VesselDynamicsSimulation Simulation(IEnvironment environment, VesselState initial, bool alongHull)
		{
			var simulation = new VesselDynamicsSimulation(_parameters, environment, initial) { UseCurrentAlongHull = alongHull };
			simulation.Reset(initial);
			return simulation;
		}

		private static void Compare(VesselDynamicsSimulation a, VesselDynamicsSimulation b, double seconds)
		{
			var steps = (int)Math.Round(seconds * SimulationClock.StepsPerSecond);
			for (var step = 0; step < steps; step++)
			{
				var t = step / (double)SimulationClock.StepsPerSecond;
				var inputs = new ControlInputs(Math.Sin(0.11 * t), Math.Cos(0.07 * t), Math.Sin(0.05 * t));
				a.Step(inputs);
				b.Step(inputs);
				var differences = BitIdentity.Differences(a, b);
				if (differences.Length > 0)
				{
					Assert.Fail($"step {step}: {differences}");
				}
			}
		}

		/// <summary>A steady current field given as a function of position; still air and 30 m of water.</summary>
		public sealed class FieldEnvironment : IEnvironment
		{
			private readonly Func<double, double, HorizontalVector> _current;

			public FieldEnvironment(Func<double, double, HorizontalVector> current)
			{
				_current = current;
			}

			public HorizontalVector SampleCurrent(double north, double east, double time) => _current(north, east);

			public HorizontalVector SampleWind(double north, double east, double time) => HorizontalVector.Zero;

			public double SampleDepth(double north, double east, double time) => 30.0;
		}
	}
}
