using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: a pack run's header not saying which current field it used (the channel field by method and
	// digest, or the station blend), the count of current samples outside the field's grid missing, counting the lever
	// search's samples as well as the recorded run's, or not counting at all; and a calm run gaining either line.
	[TestFixture]
	public sealed class CurrentFieldHeaderTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		[Test]
		public void APackWithoutAField_SaysStationBlend()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch, withField: false);
				var csv = scratch.File("p.csv");
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "pivot", "--vessel", Vessel, "--out", csv, "--duration-s", "2",
					"--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");

				Assert.That(code, Is.EqualTo(0), stderr);
				var table = TelemetryTable.Load(csv);
				Assert.That(table.HeaderValue("environment_current_field"), Is.EqualTo("station-blend"));
				Assert.That(table.HeaderValue("environment_current_outside_samples", "absent"), Is.EqualTo("absent"));
			}
		}

		[TestCase(0.0, 0.0, false, TestName = "APackWithAField_NamesIt_AndCountsTheRecordedRunsSamplesOutsideItsGrid(inside the grid)")]
		[TestCase(1600.0, 1600.0, true, TestName = "APackWithAField_NamesIt_AndCountsTheRecordedRunsSamplesOutsideItsGrid(outside the grid)")]
		public void APackWithAField_NamesIt_AndCountsTheRecordedRunsSamplesOutsideItsGrid(double north, double east, bool outside)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch, withField: true);
				var csv = scratch.File("t.csv");
				var (code, _, stderr) = TestSupport.Run(new VesselDynamicsFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", csv,
					"--duration-s", "10", "--speed-kn", "8", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z",
					"--start-north-m", north.ToString(System.Globalization.CultureInfo.InvariantCulture), "--start-east-m", east.ToString(System.Globalization.CultureInfo.InvariantCulture));

				Assert.That(code, Is.EqualTo(0), stderr);
				var table = TelemetryTable.Load(csv);
				var digest = File.ReadAllText(Path.Combine(EnvironmentPackJson.FieldFolderOf(day), "DIGEST")).Trim();
				Assert.That(table.HeaderValue("environment_current_field"), Is.EqualTo("channel-harmonic/1 " + digest));
				var count = long.Parse(table.HeaderValue("environment_current_outside_samples"), System.Globalization.CultureInfo.InvariantCulture);
				if (outside)
				{
					// The recorded run alone: the lead-in and 10 s of turning at 100 Hz, every sample outside the grid. The settle and
					// the lever search before it sample far more.
					var steps = (long)Math.Round((table.Column("time_s").Last() - table.Column("time_s").First()) * 100.0);
					Assert.That(count, Is.GreaterThanOrEqualTo(steps));
					Assert.That(count, Is.LessThan(100 * steps));
				}
				else
				{
					Assert.That(count, Is.EqualTo(0));
				}
			}
		}

		[Test]
		public void ACalmRun_HasNoCurrentFieldLines()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var csv = scratch.File("c.csv");
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "pivot", "--vessel", Vessel, "--out", csv, "--duration-s", "2");
				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(File.ReadLines(csv).TakeWhile(line => line.StartsWith("#")), Has.None.Contain("current_field").And.None.Contain("outside_samples"));
			}
		}

		// A day around the origin, 12 m deep, with one current station at the origin flowing east at 0.5 m/s; with a field,
		// a 3 by 3 grid of 1 km cells around the origin whose cells all follow the station. Made-up values.
		private static string WritePack(TestSupport.ScratchDirectory scratch, bool withField)
		{
			var times = new List<double>();
			for (var t = -3600.0; t <= 90000.0; t += 360.0)
			{
				times.Add(t);
			}

			var count = times.Count;
			var cells = 501;
			var grid = new DepthGrid("slice", -2500.0, -2500.0, 10.0, cells, cells, Enumerable.Repeat(-12.0f, cells * cells).ToArray());
			var depth = new DepthSet("lower-east-river", "NAVD88", 20.0, new[] { grid }, new[] { new DepthSource("depth_merged_navd88.tif", new string('0', 64), "synthetic") }, string.Empty);
			var origin = LocalTangentPlane.World.ToGeodetic(0.0, 0.0);
			var current = new CurrentStation("NYH1920", "Test current", "harmonic-prediction", origin.LatitudeDegrees, origin.LongitudeDegrees, 0.0, 0.0, 1.5,
				"NYH1920.json", new string('1', 64), new VectorSeries(times, Enumerable.Repeat(0.0, count).ToList(), Enumerable.Repeat(0.5, count).ToList()));
			var battery = LocalTangentPlane.World.ToGeodetic(66.6, 67.6);
			var gauge = new WaterLevelStation("8518750", "The Battery", battery.LatitudeDegrees, battery.LongitudeDegrees, 66.6, 67.6, "8518750_wl.json", new string('2', 64),
				new ScalarSeries(times, Enumerable.Repeat(0.5, count).ToList()), Enumerable.Repeat(false, count).ToList());
			CurrentFieldBasis field = null;
			if (withField)
			{
				// Each cell: axis east, along and cross factors 1, the one station with weight 1 in its single slot.
				var values = new float[9 * 5];
				var slots = new byte[9];
				for (var cell = 0; cell < 9; cell++)
				{
					values[5 * cell + 1] = 1.0f;
					values[5 * cell + 2] = 1.0f;
					values[5 * cell + 3] = 1.0f;
					values[5 * cell + 4] = 1.0f;
				}

				var fieldGrid = new CurrentFieldGrid("slice", -1000.0, -1000.0, 1000.0, 3, 3, 1, values, slots, 1);
				var parameters = new CurrentFieldParameters(1000.0, -1000.0, 1000.0, -1000.0, 1000.0, "south", "north", 0.0, 1.0, 800.0, 1.0, 1.0 / 6.0, 200.0, 60.0, 40.0, 120.0, 500.0, 1e-10);
				field = new CurrentFieldBasis("lower-east-river", new[] { new CurrentFieldStation("NYH1920", 0.0, 0.0, 0.0, 1.0) }, new[] { fieldGrid }, parameters,
					new[] { new DepthSource("depth/DIGEST", new string('0', 64), "synthetic") }, new[] { "synthetic CLI test field" }, string.Empty);
			}

			var pack = new EnvironmentPack("lower-east-river", "2025-07-14", new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero), -3600.0, 90000.0, depth,
				new[] { current }, new[] { gauge }, Array.Empty<WindStation>(), new[] { "synthetic CLI test pack" }, string.Empty, field);
			return EnvironmentPackWriter.Write(pack, Path.Combine(scratch.Path, "lower-east-river", "env"));
		}
	}
}
