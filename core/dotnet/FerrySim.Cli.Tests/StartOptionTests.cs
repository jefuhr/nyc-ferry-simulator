using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: a start option ignored (the settle trials and the recorded run of a manoeuvre, or a trace
	// replay, not starting at the requested position and heading); a start given by halves or twice (north without east,
	// latitude without longitude, both forms, --heading-deg with --start-heading-deg); latitude and longitude not converted
	// through the world frame; a latitude and longitude on the far side of the Earth accepted because the world plane folds
	// it back into the square, or a nearby one refused or moved by the check that stops that; a settled start ignoring the
	// current where it starts; the header not saying where a run started; and runs without start options gaining header lines.
	[TestFixture]
	public sealed class StartOptionTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		[Test]
		public void StartNorthAndEast_PlaceEveryTrialAndTheRecordedRun()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "turning-circle", "--vessel", Vessel, "--out", scratch.File("t.csv"),
					"--speed-kn", "10", "--start-north-m", "1234.5", "--start-east-m", "-678.25");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(factory.Initials.Count, Is.GreaterThan(1), "the lever search's trials and the recorded run");
				foreach (var initial in factory.Initials)
				{
					Assert.That((initial.North, initial.East), Is.EqualTo((1234.5, -678.25)));
				}

				var table = TelemetryTable.Load(scratch.File("t.csv"));
				Assert.That(table.HeaderValue("start_north_m"), Is.EqualTo("1234.5"));
				Assert.That(table.HeaderValue("start_east_m"), Is.EqualTo("-678.25"));
			}
		}

		[Test]
		public void StartHeading_SetsTheInitialHeading()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "crash-stop", "--vessel", Vessel, "--out", scratch.File("s.csv"),
					"--speed-kn", "10", "--start-heading-deg", "135");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(factory.Initials.Select(s => s.Heading), Has.All.EqualTo(Angles.ToRadians(135.0)));
				Assert.That(TelemetryTable.Load(scratch.File("s.csv")).HeaderValue("initial_heading_deg"), Is.EqualTo("135"));
			}
		}

		[Test]
		public void StartLatitudeAndLongitude_AreConvertedThroughTheWorldFrame()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "pivot", "--vessel", Vessel, "--out", scratch.File("p.csv"),
					"--start-lat", "40.6437", "--start-lon", "-74.0726");

				Assert.That(code, Is.EqualTo(0), stderr);
				var expected = LocalTangentPlane.World.ToLocal(new GeodeticPosition(40.6437, -74.0726));
				Assert.That(factory.Initials[0].North, Is.EqualTo(expected.North));
				Assert.That(factory.Initials[0].East, Is.EqualTo(expected.East));
				var table = TelemetryTable.Load(scratch.File("p.csv"));
				Assert.That(table.HeaderValue("start_lat"), Is.EqualTo("40.6437"));
				Assert.That(table.HeaderValue("start_lon"), Is.EqualTo("-74.0726"));
				Assert.That(double.Parse(table.HeaderValue("start_north_m"), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(expected.North).Within(5e-4));
			}
		}

		[TestCase("--start-north-m", "10")]
		[TestCase("--start-east-m", "10")]
		[TestCase("--start-lat", "40.7")]
		[TestCase("--start-lon", "-74.0")]
		[TestCase("--start-north-m", "10", "--start-east-m", "10", "--start-lat", "40.7", "--start-lon", "-74.0")]
		[TestCase("--heading-deg", "90", "--start-heading-deg", "90")]
		[TestCase("--start-heading-deg", "361")]
		[TestCase("--start-lat", "91", "--start-lon", "-74.0")]
		[TestCase("--start-north-m", "1e9", "--start-east-m", "0")]
		[TestCase("--start-north-m", "north", "--start-east-m", "0")]
		public void AStartGivenByHalvesOrTwiceOrOutOfRange_IsAUsageError(params string[] options)
		{
			var args = new[] { "maneuver", "pivot", "--vessel", Vessel, "--out", "unused.csv" }.Concat(options).ToArray();
			var (code, stdout, stderr) = TestSupport.Run(new FakeFactory(), args);
			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.Contain("Usage:"));
			Assert.That(stdout, Is.Empty);
		}

		// The antipode of the world origin and far-side points around it. The world plane keeps only north and east, so each
		// one's world position lies inside the square: the antipode lands 42 km north of the origin, -41.0807 105.985 on the
		// origin itself, and the others towards the square's edges.
		[TestCase(-40.7, 105.985)]
		[TestCase(-41.0807, 105.985)]
		[TestCase(-40.2, 105.985)]
		[TestCase(-41.7, 104.985)]
		[TestCase(-41.0, 106.885)]
		public void AStartOnTheFarSideOfTheEarth_IsAUsageError(double latitude, double longitude)
		{
			var folded = LocalTangentPlane.World.ToLocal(new GeodeticPosition(latitude, longitude));
			Assert.That(Math.Max(Math.Abs(folded.North), Math.Abs(folded.East)), Is.LessThanOrEqualTo(StartPosition.Range), "the case folds into the square");

			AssertRefused(latitude, longitude);
		}

		[TestCase(StartPosition.Range + 10.0, 0.0)]
		[TestCase(-StartPosition.Range - 10.0, 0.0)]
		[TestCase(0.0, StartPosition.Range + 10.0)]
		[TestCase(0.0, -StartPosition.Range - 10.0)]
		public void AStartByLatitudeAndLongitudeOutsideTheSquare_IsAUsageError(double north, double east)
		{
			var outside = LocalTangentPlane.World.ToGeodetic(north, east);
			AssertRefused(outside.LatitudeDegrees, outside.LongitudeDegrees);
		}

		// Points around the harbor: the origin, and near Pier 11, St. George, Astoria and Rockaway.
		[TestCase(40.7, -74.015)]
		[TestCase(40.7033, -74.0071)]
		[TestCase(40.6437, -74.0726)]
		[TestCase(40.7713, -73.9367)]
		[TestCase(40.5845, -73.8136)]
		public void ANearbyStartByLatitudeAndLongitude_IsAcceptedUnchanged(double latitude, double longitude)
		{
			AssertAcceptedUnchanged(latitude, longitude);
		}

		// The square's corners are 141 km from the origin: the bound stays a square along the world axes, not a distance.
		[TestCase(StartPosition.Range - 10.0, StartPosition.Range - 10.0)]
		[TestCase(StartPosition.Range - 10.0, -StartPosition.Range + 10.0)]
		[TestCase(-StartPosition.Range + 10.0, StartPosition.Range - 10.0)]
		[TestCase(-StartPosition.Range + 10.0, -StartPosition.Range + 10.0)]
		public void AStartByLatitudeAndLongitudeInACornerOfTheSquare_IsAcceptedUnchanged(double north, double east)
		{
			var corner = LocalTangentPlane.World.ToGeodetic(north, east);
			AssertAcceptedUnchanged(corner.LatitudeDegrees, corner.LongitudeDegrees);
		}

		[Test]
		public void ATraceReplay_StartsWhereItIsTold()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var inputs = scratch.File("trace.csv");
				File.WriteAllText(inputs, "time_s,port_lever,starboard_lever,helm\n0,0.5,0.5,0\n5,0,0,0\n");
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "run", "--vessel", Vessel, "--inputs", inputs, "--out", scratch.File("o.csv"),
					"--start-north-m", "-500", "--start-east-m", "250", "--start-heading-deg", "270");

				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That((factory.Initials[0].North, factory.Initials[0].East, factory.Initials[0].Heading), Is.EqualTo((-500.0, 250.0, Angles.ToRadians(270.0))));
				var table = TelemetryTable.Load(scratch.File("o.csv"));
				Assert.That((table.Column("north_m")[0], table.Column("east_m")[0], table.Column("heading_deg")[0]), Is.EqualTo((-500.0, 250.0, 270.0)));
			}
		}

		[Test]
		public void AnAtRestManeuverWithTheRealModel_RecordsItsFirstRowAtTheStart()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr) = TestSupport.Run(new VesselDynamicsFactory(), "maneuver", "pivot", "--vessel", Vessel, "--out", scratch.File("p.csv"),
					"--duration-s", "5", "--start-north-m", "321.125", "--start-east-m", "-42.5", "--start-heading-deg", "200");

				Assert.That(code, Is.EqualTo(0), stderr);
				var table = TelemetryTable.Load(scratch.File("p.csv"));
				Assert.That((table.Column("north_m")[0], table.Column("east_m")[0], table.Column("heading_deg")[0]), Is.EqualTo((321.125, -42.5, 200.0)));
			}
		}

		[Test]
		public void ASettledStart_UsesTheCurrentWhereItStarts()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				// Two current stations: east at the origin, north 2 km north of it. Starting on the second, the settled start
				// is the test speed along the heading plus that station's current exactly.
				var day = WritePack(scratch);
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "turning-circle", "--vessel", Vessel, "--out", scratch.File("t.csv"),
					"--speed-kn", "10", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z", "--start-north-m", "2000", "--start-east-m", "0");

				Assert.That(code, Is.EqualTo(0), stderr);
				var initial = factory.Initials.Last();
				Assert.That(initial.Surge, Is.EqualTo(Units.KnotsToMetresPerSecond(10.0) + 0.75).Within(1e-9));
				Assert.That(initial.Sway, Is.EqualTo(0.0).Within(1e-9));
			}
		}

		[Test]
		public void RunsWithoutStartOptions_HaveNoStartLines()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", scratch.File("t.csv"), "--speed-kn", "10");
				Assert.That(code, Is.EqualTo(0), stderr);
				var header = File.ReadLines(scratch.File("t.csv")).TakeWhile(line => line.StartsWith("#")).ToList();
				Assert.That(header, Has.None.StartsWith("# start_"));
				Assert.That(header, Has.Some.EqualTo("# initial_heading_deg: 0"));
			}
		}

		[Test]
		public void Usage_DescribesTheStartOptions()
		{
			Assert.That(CliApp.UsageText(), Does.Contain("--start-north-m <n>").And.Contain("--start-east-m <n>").And.Contain("--start-heading-deg <n>")
				.And.Contain("--start-lat <deg>").And.Contain("--start-lon <deg>"));
		}

		private static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);

		// The start is refused as a usage error before any simulation is made.
		private static void AssertRefused(double latitude, double longitude)
		{
			var factory = new FakeFactory();
			var (code, stdout, stderr) = TestSupport.Run(factory, "maneuver", "pivot", "--vessel", Vessel, "--out", "unused.csv",
				"--start-lat", Text(latitude), "--start-lon", Text(longitude));

			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.Contain("is more than 100 km from the world origin.").And.Contain("Usage:"));
			Assert.That(stdout, Is.Empty);
			Assert.That(factory.Initials, Is.Empty);
		}

		// The run starts exactly where the world frame puts the point, and the header says so.
		private static void AssertAcceptedUnchanged(double latitude, double longitude)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "pivot", "--vessel", Vessel, "--out", scratch.File("p.csv"),
					"--start-lat", Text(latitude), "--start-lon", Text(longitude));

				Assert.That(code, Is.EqualTo(0), stderr);
				var expected = LocalTangentPlane.World.ToLocal(new GeodeticPosition(latitude, longitude));
				Assert.That(factory.Initials, Is.Not.Empty);
				Assert.That(factory.Initials.Select(s => (s.North, s.East)), Has.All.EqualTo((expected.North, expected.East)));
				var table = TelemetryTable.Load(scratch.File("p.csv"));
				Assert.That(table.HeaderValue("start_lat"), Is.EqualTo(Maneuvers.Number(latitude)));
				Assert.That(table.HeaderValue("start_lon"), Is.EqualTo(Maneuvers.Number(longitude)));
				Assert.That(table.HeaderValue("start_north_m"), Is.EqualTo(Maneuvers.Number(expected.North)));
				Assert.That(table.HeaderValue("start_east_m"), Is.EqualTo(Maneuvers.Number(expected.East)));
			}
		}

		// A day around the origin with two current stations 2 km apart: 0.5 m/s east at the origin, 0.75 m/s north at
		// north 2000. Made-up values for the CLI tests, not NOAA data.
		private static string WritePack(TestSupport.ScratchDirectory scratch)
		{
			var times = new List<double>();
			for (var t = -3600.0; t <= 90000.0; t += 360.0)
			{
				times.Add(t);
			}

			var count = times.Count;
			var cells = 401;
			var grid = new DepthGrid("harbor", -2000.0, -2000.0, 10.0, cells, cells, Enumerable.Repeat(-12.0f, cells * cells).ToArray());
			var depth = new DepthSet("harbor", "NAVD88", 20.0, new[] { grid }, new[] { new DepthSource("depth_merged_navd88.tif", new string('0', 64), "synthetic") }, string.Empty);
			CurrentStation Current(string id, double north, double east, double velocityNorth, double velocityEast)
			{
				var position = LocalTangentPlane.World.ToGeodetic(north, east);
				return new CurrentStation(id, "Test current " + id, "harmonic-prediction", position.LatitudeDegrees, position.LongitudeDegrees, north, east, 1.5,
					id + ".json", new string('1', 64), new VectorSeries(times, Enumerable.Repeat(velocityNorth, count).ToList(), Enumerable.Repeat(velocityEast, count).ToList()));
			}

			var battery = LocalTangentPlane.World.ToGeodetic(66.6, 67.6);
			var gauge = new WaterLevelStation("8518750", "The Battery", battery.LatitudeDegrees, battery.LongitudeDegrees, 66.6, 67.6, "8518750_wl.json", new string('2', 64),
				new ScalarSeries(times, Enumerable.Repeat(0.5, count).ToList()), Enumerable.Repeat(false, count).ToList());
			var pack = new EnvironmentPack("harbor", "2025-07-14", new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero), -3600.0, 90000.0, depth,
				new[] { Current("A", 0.0, 0.0, 0.0, 0.5), Current("B", 2000.0, 0.0, 0.75, 0.0) }, new[] { gauge }, Array.Empty<WindStation>(),
				new[] { "synthetic CLI test pack" }, string.Empty);
			return EnvironmentPackWriter.Write(pack, Path.Combine(scratch.Path, "harbor", "env"));
		}
	}
}
