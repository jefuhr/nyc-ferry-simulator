using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Zero thrust next to a station of a channel field: the hull drifts at that station's prediction. Failure modes
	/// guarded: a channel field that is not the station's own velocity next to it (so a drifting hull is set off course),
	/// the hull not keeping up with a tidal station as its prediction changes, a hull at rest not carried toward the
	/// water's velocity where it is.
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class ChannelDriftTests
	{
		private const double Period = 44712.0;
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public ChannelDriftTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = ManeuverRunner.Load(_vesselFile);
		}

		/// <summary>
		/// A channel 4 km by 4 km along 045°: station A at the origin floods at 1.5 m/s toward 045°, station B 2 km up the
		/// channel at 0.6 m/s toward 050°, a quarter period later (or, when <paramref name="sameTide"/>, with A's own
		/// velocity). A cell's weight of B grows along the channel from 60 m past A to 60 m short of B, so A's four cells are
		/// all A's.
		/// </summary>
		internal static EnvironmentPack Channel(bool sameTide = false)
		{
			var axis = Angles.ToRadians(45.0);
			var along = HorizontalVector.FromSpeedToward(1.0, axis);
			var grid = FieldFixtures.Grid("channel", -2000.0, -2000.0, 20.0, 201, 201, 2, (r, c) =>
			{
				var s = (-2000.0 + 20.0 * r) * along.North + (-2000.0 + 20.0 * c) * along.East;
				var b = Math.Min(1.0, Math.Max(0.0, (s - 60.0) / 1880.0));
				return FieldFixtures.Cell.Of(45.0, 1.0, 1.0, (0, 1.0 - b), (1, b));
			});
			var a = FieldFixtures.StationOn(grid, "A", 0.0, 0.0);
			var bStation = FieldFixtures.StationOn(grid, "B", 2000.0 * along.North, 2000.0 * along.East);
			var basis = new CurrentFieldBasis(PackFixtures.Region, new[] { a, bStation }, new[] { grid }, null, string.Empty);
			var currents = new[]
			{
				PackFixtures.Current("A", 0.0, 0.0, t => HorizontalVector.FromSpeedToward(1.5 * Math.Sin(2.0 * Math.PI * t / Period), axis)),
				PackFixtures.Current("B", bStation.North, bStation.East, t => sameTide
					? HorizontalVector.FromSpeedToward(1.5 * Math.Sin(2.0 * Math.PI * t / Period), axis)
					: HorizontalVector.FromSpeedToward(0.6 * Math.Sin(2.0 * Math.PI * t / Period - Math.PI / 2.0), Angles.ToRadians(50.0))),
			};
			var depth = PackFixtures.Depth(20.0, PackFixtures.FlatGrid("slice", -2000.0, -2000.0, 10.0, 401, 401, -15.0f));
			return new EnvironmentPack(PackFixtures.Region, PackFixtures.Date, PackFixtures.Epoch, PackFixtures.CoverageStart, PackFixtures.CoverageEnd,
				depth, currents, new[] { PackFixtures.Gauge("G1", 0.0, 0.0, t => 0.0) }, Array.Empty<WindStation>(), null, string.Empty, basis);
		}

		[Test]
		public void MovingWithTheWaterAtAStation_TheHullDriftsAtThatStationsPrediction()
		{
			// Start a minute before A's flood peaks, moving with the water there.
			var pack = Channel();
			var environment = new DataEnvironment(pack, PackFixtures.Epoch.AddSeconds(Period / 4.0 - 60.0));
			Assert.That(environment.CurrentField, Is.TypeOf<ChannelCurrentField>());
			var station = pack.CurrentStations[0];
			var start = station.Velocity.Sample(environment.PackTime(0.0));
			var simulation = new VesselDynamicsSimulation(_parameters, environment, VesselState.Planar(0.0, 0.0, 0.0, 0.0, start.North, start.East));
			var worst = 0.0;
			for (var second = 1; second <= 20; second++)
			{
				ManeuverRunner.Run(simulation, ControlInputs.Neutral, 1.0);
				var predicted = station.Velocity.Sample(environment.PackTime(simulation.State.Time));
				worst = Math.Max(worst, (simulation.State.GroundVelocity - predicted).Magnitude);
			}

			var travelled = simulation.State.Position.Magnitude;
			TestContext.Out.WriteLine($"{_vesselFile}: over 20 s next to A the drift differs from A's prediction by at most {worst:F5} m/s; {travelled:F1} m travelled");
			Assert.That(travelled, Is.LessThan(40.0), "still on A's own cells");
			Assert.That(worst, Is.LessThan(0.005));
			Assert.That(Math.Abs(simulation.Telemetry.SpeedThroughWater), Is.LessThan(0.005), "no flow past the hull");

			// Further along, it keeps drifting with the field where it is.
			ManeuverRunner.Run(simulation, ControlInputs.Neutral, 100.0);
			var here = environment.SampleCurrent(simulation.State.Position.North, simulation.State.Position.East, simulation.State.Time);
			Assert.That((simulation.State.GroundVelocity - here).Magnitude, Is.LessThan(0.02));
		}

		[Test]
		public void FromRestAtAStation_TheHullIsCarriedToTheWatersVelocity()
		{
			// Both stations on A's tide, so wherever the boat is carried the water is A's: the station's prediction.
			var pack = Channel(sameTide: true);
			var environment = new DataEnvironment(pack, PackFixtures.Epoch.AddSeconds(Period / 4.0 - 300.0));
			var simulation = new VesselDynamicsSimulation(_parameters, environment, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			var lag = new double[5];
			var speed = new double[5];
			for (var i = 0; i < lag.Length; i++)
			{
				ManeuverRunner.Run(simulation, ControlInputs.Neutral, i == 0 ? 30.0 : 150.0);
				var here = environment.SampleCurrent(simulation.State.Position.North, simulation.State.Position.East, simulation.State.Time);
				lag[i] = (simulation.State.GroundVelocity - here).Magnitude / here.Magnitude;
				speed[i] = simulation.State.GroundVelocity.Magnitude;
			}

			TestContext.Out.WriteLine($"{_vesselFile}: from rest, ground speed {speed[0]:F3}, {speed[1]:F3}, {speed[2]:F3}, {speed[3]:F3}, {speed[4]:F3} m/s and " +
				$"difference from the water there {lag[0]:P1}, {lag[1]:P1}, {lag[2]:P1}, {lag[3]:P1}, {lag[4]:P1} at 30, 180, 330, 480, 630 s");
			// How fast the last few per cent go is the hull's surge resistance at walking pace (CurrentAlongHullTests), not the field.
			Assert.That(speed[0], Is.GreaterThan(0.3), "set moving by the water within half a minute");
			for (var i = 1; i < lag.Length; i++)
			{
				Assert.That(lag[i], Is.LessThan(lag[i - 1]), $"closer to the water at each check ({i})");
			}

			Assert.That(lag[4], Is.LessThan(0.05), "within 5 % of the station's prediction after ten minutes");
		}
	}
}
