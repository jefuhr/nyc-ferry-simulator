using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The Unity copy of the CLI's telemetry CSV schema. Failure modes guarded: the column list drifting from the
	/// CLI's (then <c>ferrysim summarize</c> and <c>tools/plot_maneuver.py</c> reject Unity runs), a different number
	/// format or unit, culture-dependent decimal commas, and non-finite values written as text.
	/// </summary>
	public class TelemetryCsvTests
	{
		[Test]
		public void ColumnNames_MatchTheCliSource()
		{
			var source = File.ReadAllText(Path.Combine(TestSupport.RepoRoot, "core", "dotnet", "FerrySim.Cli", "TelemetryCsv.cs"));
			var block = Regex.Match(source, @"Names\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
			Assert.That(block.Success, Is.True, "could not find TelemetryColumns.Names in the CLI source");
			var names = new List<string>();
			foreach (Match match in Regex.Matches(block.Groups["body"].Value, "\"(?<name>[^\"]+)\""))
			{
				names.Add(match.Groups["name"].Value);
			}

			Assert.That(TelemetryCsv.ColumnNames, Is.EqualTo(names));
			var version = Regex.Match(source, "FormatVersion\\s*=\\s*\"(?<v>[^\"]+)\"");
			Assert.That(TelemetryCsv.FormatVersion, Is.EqualTo(version.Groups["v"].Value));
		}

		[Test]
		public void FormatRow_UsesTheCliUnitsAndFormats()
		{
			var row = TelemetryCsv.FormatRow(SampleState(), SampleTelemetry(), new ControlInputs(0.6, -0.4, 0.25));

			Assert.That(row, Is.EqualTo(
				"12.340,100.250,-3.500,90.0000,5.0000,0.0000,0.5730,9.7192,90.000,9.7192," +
				"1500.00,1200.00,750.00,-600.00,ahead,shifting,5.730,5.730,10.000,0.0," +
				"0.6000,-0.4000,0.2500"));
		}

		[Test]
		public void FormatRow_KeepsTheSignOfNegativeValuesThatRoundToZero_LikeTheCli()
		{
			var state = new VesselState(1.0, -0.0001, -0.0, 0.0, 0.0, 0.0, 0.0, 2.0, -0.00001, 0.0, 0.0, 0.0, 0.0);

			var cells = TelemetryCsv.FormatRow(state, SampleTelemetry(), ControlInputs.Neutral).Split(',');

			Assert.That(cells[1], Is.EqualTo("-0.000"), "north -0.0001 m, as CoreCLR prints it");
			Assert.That(cells[2], Is.EqualTo("-0.000"), "east -0.0");
			Assert.That(cells[5], Is.EqualTo("-0.0000"), "sway");
			Assert.That(cells[4], Is.EqualTo("2.0000"));
			Assert.That(cells[20], Is.EqualTo("0.0000"), "positive zero stays unsigned");
		}

		[Test]
		public void FormatRow_IgnoresTheCurrentCulture()
		{
			var saved = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
				var row = TelemetryCsv.FormatRow(SampleState(), SampleTelemetry(), new ControlInputs(0.6, -0.4, 0.25));

				Assert.That(row.Split(',').Length, Is.EqualTo(TelemetryCsv.ColumnNames.Length));
				Assert.That(row, Does.StartWith("12.340,100.250,"));
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = saved;
			}
		}

		[Test]
		public void FormatRow_RefusesNonFiniteValues()
		{
			var state = VesselState.Planar(1.0, double.NaN, 0.0, 0.0);

			Assert.That(() => TelemetryCsv.FormatRow(state, SampleTelemetry(), ControlInputs.Neutral), Throws.InvalidOperationException);
		}

		[Test]
		public void Header_AndSplit_RoundTrip()
		{
			var header = TelemetryCsv.FormatHeader(new[]
			{
				new KeyValuePair<string, string>("vessel", "t-class"),
				new KeyValuePair<string, string>("environment", "current=1.5kn@200,wind=15kn@from300,depth=8m"),
			});
			var lines = (header + "1,2\n\n").Split('\n');

			Assert.That(lines[0], Is.EqualTo("# ferrysim-csv: 1"));
			TelemetryCsv.Split(lines, "test", out var parsed, out var rows);
			Assert.That(parsed["vessel"], Is.EqualTo("t-class"));
			Assert.That(parsed["environment"], Is.EqualTo("current=1.5kn@200,wind=15kn@from300,depth=8m"));
			Assert.That(rows, Is.EqualTo(new[] { "1,2" }));
			Assert.That(() => TelemetryCsv.Split(new[] { "# ferrysim-csv: 2", TelemetryCsv.ColumnRow }, "test", out _, out _), Throws.TypeOf<FormatException>());
			Assert.That(() => TelemetryCsv.Split(new[] { TelemetryCsv.ColumnRow }, "test", out _, out _), Throws.TypeOf<FormatException>());
		}

		private static VesselState SampleState() =>
			new VesselState(12.34, 100.25, -3.5, 0.0, 0.0, 0.0, Math.PI / 2.0, 5.0, 0.0, 0.0, 0.0, 0.0, 0.01);

		private static VesselTelemetry SampleTelemetry() =>
			new VesselTelemetry(
				new EngineTelemetry(Units.RpmToRadiansPerSecond(1500.0), Units.RpmToRadiansPerSecond(750.0), GearState.Ahead, 0.4),
				new EngineTelemetry(Units.RpmToRadiansPerSecond(1200.0), Units.RpmToRadiansPerSecond(-600.0), GearState.Shifting, 0.0),
				0.1,
				0.1,
				5.0,
				5.0,
				Math.PI / 2.0,
				10.0,
				0.0);
	}
}
