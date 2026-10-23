using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: column drift between writer, reader and plot script, locale-dependent
	// number formatting, wrong sample spacing, headers that lose the run settings, and non-deterministic
	// output (timestamps, absolute paths).
	[TestFixture]
	public sealed class CsvFormatTests
	{
		private static readonly string[] ExpectedColumns =
		{
			"time_s", "north_m", "east_m", "heading_deg", "surge_mps", "sway_mps", "yaw_rate_degps",
			"sog_kn", "cog_deg", "stw_kn",
			"port_engine_rpm", "stbd_engine_rpm", "port_shaft_rpm", "stbd_shaft_rpm", "port_gear", "stbd_gear",
			"port_rudder_deg", "stbd_rudder_deg", "depth_under_keel_m", "contact_force_n",
			"port_lever", "starboard_lever", "helm",
		};

		private static string[] RunPivot(TestSupport.ScratchDirectory scratch, FakeFactory factory, params string[] extra)
		{
			var path = scratch.File("pivot.csv");
			var args = new[] { "maneuver", "pivot", "--vessel", TestSupport.Vessel("t-class.json"), "--out", path, "--duration-s", "5" }.Concat(extra).ToArray();
			var (code, _, stderr) = TestSupport.Run(factory, args);
			Assert.That(code, Is.EqualTo(0), stderr);
			return File.ReadAllLines(path);
		}

		[Test]
		public void ColumnRow_MatchesTheDocumentedSchemaExactly()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var lines = RunPivot(scratch, new FakeFactory());

				var columns = lines.First(l => !l.StartsWith("#", StringComparison.Ordinal)).Split(',');
				Assert.That(columns, Is.EqualTo(ExpectedColumns));
				Assert.That(TelemetryColumns.Names, Is.EqualTo(ExpectedColumns));
			}
		}

		[Test]
		public void HeaderComment_RecordsVesselManeuverEnvironmentModelAndRunSettings()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var lines = RunPivot(scratch, new FakeFactory(), "--env", "current=1.5kn@200,depth=8m", "--sample-hz", "20", "--heading-deg", "45");

				var header = lines.TakeWhile(l => l.StartsWith("#", StringComparison.Ordinal)).ToArray();
				Assert.That(header[0], Is.EqualTo("# ferrysim-csv: 1"));
				Assert.That(header, Does.Contain("# vessel: t-class"));
				Assert.That(header, Does.Contain("# maneuver: pivot"));
				Assert.That(header, Does.Contain("# environment: current=1.5kn@200,depth=8m"));
				Assert.That(header, Does.Contain("# model: " + typeof(FakeSimulation).FullName));
				Assert.That(header, Does.Contain("# factory: " + typeof(FakeFactory).FullName));
				Assert.That(header, Does.Contain("# physics_hz: 100"));
				Assert.That(header, Does.Contain("# sample_hz: 20"));
				Assert.That(header, Does.Contain("# initial_heading_deg: 45"));
				Assert.That(header, Does.Contain("# maneuver_start_s: 0"));
				Assert.That(header, Does.Contain("# param.direction: starboard"));
				Assert.That(header, Does.Contain("# param.lever: 0.5"));
				Assert.That(header, Does.Contain("# duration_s: 5"));
			}
		}

		[Test]
		public void Header_ContainsNoPathsOrTimestamps()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var lines = RunPivot(scratch, new FakeFactory());

				var header = string.Join("\n", lines.TakeWhile(l => l.StartsWith("#", StringComparison.Ordinal)));
				Assert.That(header, Does.Not.Contain(scratch.Path));
				Assert.That(header, Does.Not.Contain(TestSupport.RepoRoot));
			}
		}

		[TestCase("10", 51, 0.1)]
		[TestCase("2", 11, 0.5)]
		[TestCase("100", 501, 0.01)]
		public void SampleRate_SetsRowSpacing(string sampleHz, int expectedRows, double spacing)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var lines = RunPivot(scratch, new FakeFactory(), "--sample-hz", sampleHz);

				var table = TelemetryTable.Parse(lines);
				Assert.That(table.RowCount, Is.EqualTo(expectedRows));
				Assert.That(table.Time[0], Is.EqualTo(0.0));
				Assert.That(table.Time[table.RowCount - 1], Is.EqualTo(5.0).Within(1e-9));
				for (var i = 1; i < table.RowCount; i++)
				{
					Assert.That(table.Time[i] - table.Time[i - 1], Is.EqualTo(spacing).Within(1e-9));
				}
			}
		}

		[Test]
		public void EveryRowHasOneCellPerColumn_AndNumbersUseInvariantCulture()
		{
			var previous = System.Globalization.CultureInfo.CurrentCulture;
			System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
			try
			{
				using (var scratch = new TestSupport.ScratchDirectory())
				{
					var lines = RunPivot(scratch, new FakeFactory());

					foreach (var row in lines.Where(l => !l.StartsWith("#", StringComparison.Ordinal)))
					{
						Assert.That(row.Split(',').Length, Is.EqualTo(ExpectedColumns.Length), row);
					}

					Assert.That(lines.Last(), Does.Match(@"^5\.000,"));
				}
			}
			finally
			{
				System.Globalization.CultureInfo.CurrentCulture = previous;
			}
		}

		[Test]
		public void ConvenienceColumns_AgreeWithSiColumns()
		{
			var csv = new SyntheticCsv("pivot")
				.Row(0.0, 0.0, 0.0, 90.0, 5.0, yawRateDegps: 2.0);

			var table = TelemetryTable.Parse(csv.Render().Split('\n'));

			Assert.That(table.Column("heading_deg")[0], Is.EqualTo(90.0).Within(1e-3));
			Assert.That(table.Column("surge_mps")[0], Is.EqualTo(5.0).Within(1e-3));
			Assert.That(table.Column("yaw_rate_degps")[0], Is.EqualTo(2.0).Within(1e-3));
			Assert.That(table.Column("stw_kn")[0], Is.EqualTo(Units.MetresPerSecondToKnots(5.0)).Within(1e-3));
			Assert.That(table.Column("sog_kn")[0], Is.EqualTo(Units.MetresPerSecondToKnots(5.0)).Within(1e-3));
			Assert.That(table.Column("cog_deg")[0], Is.EqualTo(90.0).Within(1e-2));
			Assert.That(table.Column("port_engine_rpm")[0], Is.EqualTo(Units.RadiansPerSecondToRpm(100.0)).Within(1e-2));
		}

		[Test]
		public void GearColumnsAreNamesAndHelmAndLeversAreInTheRow()
		{
			var csv = new SyntheticCsv("pivot").Row(0.0, 0.0, 0.0, 0.0, 0.0, helm: -0.25, lever: 0.75);

			var data = csv.Render().Split('\n').First(l => l.StartsWith("0.000,", StringComparison.Ordinal)).Split(',');

			Assert.That(data[Array.IndexOf(ExpectedColumns, "port_gear")], Is.EqualTo("ahead"));
			Assert.That(data[Array.IndexOf(ExpectedColumns, "helm")], Is.EqualTo("-0.2500"));
			Assert.That(data[Array.IndexOf(ExpectedColumns, "port_lever")], Is.EqualTo("0.7500"));
		}

		[Test]
		public void Reader_RejectsFilesThatAreNotFerrysimCsvOrAreMissingColumns()
		{
			Assert.Throws<CliException>(() => TelemetryTable.Parse(new[] { "time_s,north_m", "0,0" }));
			Assert.Throws<CliException>(() => TelemetryTable.Parse(new[] { "# ferrysim-csv: 1", "time_s,north_m", "0,0" }));
			Assert.Throws<CliException>(() => TelemetryTable.Parse(new[] { "# ferrysim-csv: 99", string.Join(",", ExpectedColumns) }));
		}

		[Test]
		public void SameRunTwice_ProducesIdenticalBytes()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var first = RunPivot(scratch, new FakeFactory());
				var second = RunPivot(scratch, new FakeFactory());

				Assert.That(second, Is.EqualTo(first));
			}
		}
	}
}
