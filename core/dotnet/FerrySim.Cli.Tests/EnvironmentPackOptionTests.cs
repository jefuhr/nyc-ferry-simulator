using System;
using System.Collections.Generic;
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
	// Failure modes guarded: --env and --env-pack both accepted, a pack run without a start time (or a start time
	// without a pack) accepted, a malformed start time or a start outside the pack's day accepted, a corrupted pack
	// run anyway or crashing the CLI, the CSV header not naming the pack, its digest, epoch and start, the pack's water
	// not reaching the model, and calm runs changing their header.
	[TestFixture]
	public sealed class EnvironmentPackOptionTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		/// <summary>Local midnight in New York on 2025-07-14 (EDT).</summary>
		private static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);

		[TestCase("--env", "calm", "--env-pack", "pack", "--start-utc", "2025-07-14T16:00:00Z")]
		[TestCase("--env-pack", "pack")]
		[TestCase("--start-utc", "2025-07-14T16:00:00Z")]
		[TestCase("--env-pack", "pack", "--start-utc", "tomorrow")]
		[TestCase("--env-pack", "pack", "--start-utc", "2025-07-14 16:00")]
		[TestCase("--env-pack", " ", "--start-utc", "2025-07-14T16:00:00Z")]
		public void BadPackOptions_AreUsageErrors_BeforeAnyFileIsRead(params string[] options)
		{
			// The pack folder does not exist: the usage error must win.
			var args = new[] { "maneuver", "turning-circle", "--vessel", Vessel, "--out", "o.csv" }.Concat(options).ToArray();
			var (code, stdout, stderr) = TestSupport.Run(new FakeFactory(), args);
			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.Contain("Usage:"));
			Assert.That(stdout, Is.Empty);
		}

		[Test]
		public void APackRun_RecordsThePackDigestEpochAndStart_InTheHeader()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var csv = scratch.File("turn.csv");
				var (code, stdout, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", csv,
					"--speed-kn", "10", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");

				Assert.That(code, Is.EqualTo(0), stderr);
				var table = TelemetryTable.Load(csv);
				Assert.That(table.HeaderValue("environment"), Is.EqualTo("pack"));
				Assert.That(table.HeaderValue("environment_pack"), Is.EqualTo("lower-east-river/2025-07-14"));
				Assert.That(table.HeaderValue("environment_digest"), Is.EqualTo(File.ReadAllText(Path.Combine(day, "DIGEST")).Trim()));
				Assert.That(table.HeaderValue("environment_epoch_utc"), Is.EqualTo("2025-07-14T04:00:00Z"));
				Assert.That(table.HeaderValue("scenario_start_utc"), Is.EqualTo("2025-07-14T16:30:00Z"));
				Assert.That(table.HeaderValue("environment_wind_station"), Is.EqualTo("8518750"));
				Assert.That(table.HeaderValue("environment_depth_misses"), Is.EqualTo("0"));
				Assert.That(table.HeaderValue("environment_time_holds"), Is.EqualTo("0"));
				Assert.That(stdout, Does.Not.Contain("environment:"), "nothing was made up");

				var keys = File.ReadLines(csv).TakeWhile(line => line.StartsWith("#")).Select(line => line.Substring(2, line.IndexOf(':') - 2)).ToList();
				Assert.That(keys.IndexOf("environment_pack"), Is.EqualTo(keys.IndexOf("environment") + 1), "the pack follows the environment line");
			}
		}

		[Test]
		public void APackRun_StartsTheBoatWithThePacksCurrent()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var factory = new FakeFactory();
				var (code, _, stderr) = TestSupport.Run(factory, "maneuver", "turning-circle", "--vessel", Vessel, "--out", scratch.File("t.csv"),
					"--speed-kn", "10", "--heading-deg", "0", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");
				Assert.That(code, Is.EqualTo(0), stderr);

				// The current flows east at 0.5 m/s: the settled start is the test speed north plus the current.
				var initial = factory.Initials.Last();
				Assert.That(initial.Surge, Is.EqualTo(Units.KnotsToMetresPerSecond(10.0)).Within(1e-9));
				Assert.That(initial.Sway, Is.EqualTo(0.5).Within(1e-9));
			}
		}

		[Test]
		public void APackRun_WithTheRealModel_FeelsThePacksDepthAndCurrent()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var csv = scratch.File("stop.csv");
				var (code, _, stderr) = TestSupport.Run(new VesselDynamicsFactory(), "maneuver", "crash-stop", "--vessel", Vessel, "--out", csv,
					"--speed-kn", "12", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");
				Assert.That(code, Is.EqualTo(0), stderr);

				var table = TelemetryTable.Load(csv);
				var keel = Core.Serialization.VesselParametersJson.Load(Vessel).Particulars.MaxDraft.Value;
				Assert.That(table.Column("depth_under_keel_m")[0], Is.EqualTo(0.5 + 12.0 - keel).Within(0.001), "the tide's 0.5 m over a bed 12 m down");
				var sog = table.Column("sog_kn");
				var stw = table.StwKn;
				Assert.That(sog[0], Is.GreaterThan(stw[0] + 0.01), "the current adds to the speed over the ground");
			}
		}

		[Test]
		public void APackRun_ThatLeavesTheDepthGrids_SaysSo()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch, gridHalfWidth: 20.0);
				var csv = scratch.File("turn.csv");
				var (code, stdout, stderr) = TestSupport.Run(new VesselDynamicsFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", csv,
					"--speed-kn", "10", "--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");
				Assert.That(code, Is.EqualTo(0), stderr);
				Assert.That(stdout, Does.Contain("environment:").And.Contain("off the depth grids"));
				Assert.That(int.Parse(TelemetryTable.Load(csv).HeaderValue("environment_depth_misses")), Is.GreaterThan(0));
			}
		}

		[Test]
		public void ATraceReplay_TakesAPackToo()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var inputs = scratch.File("trace.csv");
				File.WriteAllText(inputs, "time_s,port_lever,starboard_lever,helm\n0,0.5,0.5,0\n10,0,0,0\n");
				var csv = scratch.File("out.csv");
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "run", "--vessel", Vessel, "--inputs", inputs, "--out", csv,
					"--env-pack", day, "--start-utc", "2025-07-14T23:59:00-04:00");
				Assert.That(code, Is.EqualTo(0), stderr);
				var table = TelemetryTable.Load(csv);
				Assert.That(table.HeaderValue("environment_pack"), Is.EqualTo("lower-east-river/2025-07-14"));
				Assert.That(table.HeaderValue("scenario_start_utc"), Is.EqualTo("2025-07-15T03:59:00Z"), "an offset is converted to UTC");
			}
		}

		[Test]
		public void ACorruptedPack_FailsWithExitCode1_NamingTheFile_AndWritesNothing()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var currents = Path.Combine(day, "currents.csv");
				var bytes = File.ReadAllBytes(currents);
				bytes[bytes.Length / 2] ^= 0x01;
				File.WriteAllBytes(currents, bytes);
				var csv = scratch.File("turn.csv");
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", csv,
					"--env-pack", day, "--start-utc", "2025-07-14T16:30:00Z");
				Assert.That(code, Is.EqualTo(1), stderr);
				Assert.That(stderr, Does.StartWith("ferrysim: error: ").And.Contain("currents.csv"));
				Assert.That(File.Exists(csv), Is.False);
			}
		}

		[TestCase("2025-07-13T02:00:00Z")]
		[TestCase("2025-07-15T06:00:00Z")]
		public void AStartOutsideThePacksDay_FailsWithExitCode1(string start)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var day = WritePack(scratch);
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", scratch.File("t.csv"),
					"--env-pack", day, "--start-utc", start);
				Assert.That(code, Is.EqualTo(1), stderr);
				Assert.That(stderr, Does.Contain("is outside lower-east-river/2025-07-14").And.Contain("2025-07-14T03:00:00Z to 2025-07-15T05:00:00Z"));
			}
		}

		[Test]
		public void ACalmRun_HasNoPackLines()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var csv = scratch.File("turn.csv");
				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", Vessel, "--out", csv, "--speed-kn", "10");
				Assert.That(code, Is.EqualTo(0), stderr);
				var header = File.ReadLines(csv).TakeWhile(line => line.StartsWith("#")).ToList();
				Assert.That(header, Has.None.Contain("environment_"));
				Assert.That(header, Has.None.Contain("scenario_start_utc"));
				Assert.That(header, Has.Some.EqualTo("# environment: calm"));
			}
		}

		[Test]
		public void Usage_DescribesThePackOptions()
		{
			Assert.That(CliApp.UsageText(), Does.Contain("--env-pack <folder>").And.Contain("--start-utc <time>"));
		}

		// A day pack around the origin: a bed 12 m down, the tide steady at +0.5 m, a 0.5 m/s current to the east from one
		// station and a light westerly. Made-up values for the CLI tests, not NOAA data.
		private static string WritePack(TestSupport.ScratchDirectory scratch, double gridHalfWidth = 2000.0)
		{
			var times = new List<double>();
			for (var t = -3600.0; t <= 90000.0; t += 360.0)
			{
				times.Add(t);
			}

			var count = times.Count;
			var cells = (int)(2.0 * gridHalfWidth / 10.0) + 1;
			var bed = Enumerable.Repeat(-12.0f, cells * cells).ToArray();
			var grid = new DepthGrid("slice", -gridHalfWidth, -gridHalfWidth, 10.0, cells, cells, bed);
			var depth = new DepthSet("lower-east-river", "NAVD88", 20.0, new[] { grid }, new[] { new DepthSource("depth_merged_navd88.tif", new string('0', 64), "synthetic") }, string.Empty);
			var battery = LocalTangentPlane.World.ToGeodetic(66.6, 67.6);
			var current = new CurrentStation("NYH1920", "Test current", "harmonic-prediction", WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees, 0.0, 0.0, 1.5,
				"NYH1920.json", new string('1', 64), new VectorSeries(times, Enumerable.Repeat(0.0, count).ToList(), Enumerable.Repeat(0.5, count).ToList()));
			var gauge = new WaterLevelStation("8518750", "The Battery", battery.LatitudeDegrees, battery.LongitudeDegrees, 66.6, 67.6, "8518750_wl.json", new string('2', 64),
				new ScalarSeries(times, Enumerable.Repeat(0.5, count).ToList()), Enumerable.Repeat(false, count).ToList());
			var wind = new WindStation("8518750", "The Battery", battery.LatitudeDegrees, battery.LongitudeDegrees, 66.6, 67.6, null, "8518750_wind.json", new string('3', 64),
				new VectorSeries(times, Enumerable.Repeat(0.0, count).ToList(), Enumerable.Repeat(3.0, count).ToList()), Enumerable.Repeat(double.NaN, count).ToList());
			var pack = new EnvironmentPack("lower-east-river", "2025-07-14", Epoch, -3600.0, 90000.0, depth, new[] { current }, new[] { gauge }, new[] { wind },
				new[] { "synthetic CLI test pack" }, string.Empty);
			return EnvironmentPackWriter.Write(pack, Path.Combine(scratch.Path, "lower-east-river", "env"));
		}
	}
}
