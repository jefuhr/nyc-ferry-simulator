using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Parsing the CLI telemetry CSV (core/dotnet/FerrySim.Cli/TelemetryCsv.cs, format version 1).
	/// Guards against: files that are not ferrysim CSVs or are a newer version, missing or reordered
	/// columns, ragged rows, non-numeric and non-finite cells, unknown gear names, Windows line endings
	/// and a UTF-8 byte order mark, rows out of time order, empty recordings, unit mistakes when turning
	/// kn/deg/rpm columns back into SI, and the writer drifting from the CLI format.
	/// </summary>
	public class TelemetryCsvTests
	{
		private static string FixturePath => UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv);

		private static readonly string ColumnRow = string.Join(",", TelemetryCsv.Columns);

		private const string TypicalRow =
			"35.000,433.992,0.000,0.0000,12.8597,0.0000,0.0000,24.9973,0.000,24.9973,2025.66,2025.66,804.15,804.15,ahead,ahead,0.000,0.000,48.100,0.0,0.9590,0.9590,0.0000";

		private static string Csv(params string[] rows) =>
			"# ferrysim-csv: 1\n# vessel: t-class\n" + ColumnRow + "\n" + string.Join("\n", rows) + "\n";

		private static string RowAt(double time) =>
			time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + TypicalRow.Substring(TypicalRow.IndexOf(','));

		[Test]
		public void Fixture_ParsesEveryRowAndTheHeader()
		{
			var recording = TelemetryCsv.Load(FixturePath);

			Assert.That(recording.Count, Is.EqualTo(310));
			Assert.That(recording.Label, Is.EqualTo("t-crash-stop.csv"));
			Assert.That(recording.VesselId, Is.EqualTo("t-class"));
			Assert.That(recording.HeaderValue("maneuver"), Is.EqualTo("crash-stop"));
			Assert.That(recording.HeaderDouble("maneuver_start_s", double.NaN), Is.EqualTo(45.0));
			Assert.That(recording.StartTime, Is.EqualTo(35.0));
			Assert.That(recording.EndTime, Is.EqualTo(65.89).Within(1e-9), "the last row is the end state, off the 0.1 s grid");
		}

		[Test]
		public void Fixture_ConvertsDisplayColumnsBackToSI()
		{
			var recording = TelemetryCsv.Load(FixturePath);

			var first = recording.Sample(0);
			Assert.That(first.State.Time, Is.EqualTo(35.0));
			Assert.That(first.State.North, Is.EqualTo(433.992).Within(1e-9));
			Assert.That(first.State.Heading, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(first.State.Surge, Is.EqualTo(12.8597).Within(1e-9));
			Assert.That(first.Telemetry.SpeedOverGround, Is.EqualTo(Units.KnotsToMetresPerSecond(24.9973)).Within(1e-9));
			Assert.That(first.Telemetry.SpeedThroughWater, Is.EqualTo(Units.KnotsToMetresPerSecond(24.9973)).Within(1e-9));
			Assert.That(first.Telemetry.Port.EngineSpeed, Is.EqualTo(Units.RpmToRadiansPerSecond(2025.66)).Within(1e-9));
			Assert.That(first.Telemetry.Starboard.ShaftSpeed, Is.EqualTo(Units.RpmToRadiansPerSecond(804.15)).Within(1e-9));
			Assert.That(first.Telemetry.Port.Gear, Is.EqualTo(GearState.Ahead));
			Assert.That(double.IsNaN(first.Telemetry.Port.Throttle), Is.True, "the CSV has no throttle column, so it is unknown, not 0");
			Assert.That(first.Telemetry.DepthUnderKeel, Is.EqualTo(48.1).Within(1e-9));
			Assert.That(first.Telemetry.ContactForce, Is.EqualTo(0.0));
			Assert.That(first.Controls.PortLever, Is.EqualTo(0.959).Within(1e-12));
			Assert.That(first.Controls.Helm, Is.EqualTo(0.0));

			var last = recording.Sample(recording.Count - 1);
			Assert.That(last.Telemetry.Port.Gear, Is.EqualTo(GearState.Astern));
			Assert.That(last.Telemetry.Port.ShaftSpeed, Is.EqualTo(Units.RpmToRadiansPerSecond(-693.63)).Within(1e-9));
			Assert.That(last.Telemetry.CourseOverGround, Is.EqualTo(Math.PI).Within(1e-9));
			Assert.That(last.Telemetry.SpeedThroughWater, Is.LessThan(0.0), "speed log keeps its astern sign");
			Assert.That(last.Controls.StarboardLever, Is.EqualTo(-1.0));
		}

		[Test]
		public void Fixture_RoundTripsThroughTheWriterByteForByte()
		{
			var original = File.ReadAllText(FixturePath);

			var rendered = TelemetryCsvWriter.Render(TelemetryCsv.Parse(original, "fixture"));

			Assert.That(rendered, Is.EqualTo(original));
		}

		[Test]
		public void MissingFormatComment_IsRejected()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(ColumnRow + "\n" + TypicalRow + "\n", "x.csv"));
			StringAssert.Contains("ferrysim-csv", error.Message);
			StringAssert.Contains("x.csv", error.Message);
		}

		[Test]
		public void NewerFormatVersion_IsRejected()
		{
			var text = Csv(TypicalRow).Replace("# ferrysim-csv: 1", "# ferrysim-csv: 2");

			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(text, "x.csv"));
			StringAssert.Contains("version 2", error.Message);
		}

		[Test]
		public void MissingColumn_IsNamed()
		{
			var text = Csv(TypicalRow).Replace(",depth_under_keel_m,", ",depth_m,");

			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(text, "x.csv"));
			StringAssert.Contains("depth_under_keel_m", error.Message);
		}

		[Test]
		public void RaggedRow_NamesTheLine()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(Csv(TypicalRow, RowAt(35.1) + ",9"), "x.csv"));
			StringAssert.Contains("line 5", error.Message);
		}

		[Test]
		public void NonNumericCell_NamesLineAndColumn()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(Csv(TypicalRow.Replace(",48.100,", ",deep,")), "x.csv"));
			StringAssert.Contains("line 4", error.Message);
			StringAssert.Contains("depth_under_keel_m", error.Message);
		}

		[TestCase("NaN")]
		[TestCase("Infinity")]
		[TestCase("-Infinity")]
		public void NonFiniteCell_IsRejected(string cell)
		{
			var text = Csv(TypicalRow.Replace(",48.100,", "," + cell + ","));

			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(text, "x.csv"));
			StringAssert.Contains("depth_under_keel_m", error.Message);
		}

		[Test]
		public void UnknownGear_IsRejected()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(Csv(TypicalRow.Replace("ahead,ahead", "ahead,drive")), "x.csv"));
			StringAssert.Contains("stbd_gear", error.Message);
			StringAssert.Contains("drive", error.Message);
		}

		[Test]
		public void GearNames_AreCaseInsensitive()
		{
			var recording = TelemetryCsv.Parse(Csv(TypicalRow.Replace("ahead,ahead", "Shifting,NEUTRAL")), "x.csv");

			Assert.That(recording.Sample(0).Telemetry.Port.Gear, Is.EqualTo(GearState.Shifting));
			Assert.That(recording.Sample(0).Telemetry.Starboard.Gear, Is.EqualTo(GearState.Neutral));
		}

		[Test]
		public void WindowsLineEndingsByteOrderMarkAndBlankLines_AreAccepted()
		{
			var text = "﻿" + Csv(TypicalRow, "", RowAt(35.1), "").Replace("\n", "\r\n");

			var recording = TelemetryCsv.Parse(text, "x.csv");

			Assert.That(recording.Count, Is.EqualTo(2));
			Assert.That(recording.VesselId, Is.EqualTo("t-class"));
		}

		[Test]
		public void ExtraAndReorderedColumns_AreReadByName()
		{
			var columns = TelemetryCsv.Columns.Reverse().Concat(new[] { "future_column" }).ToArray();
			var cells = TypicalRow.Split(',').Reverse().Concat(new[] { "7" }).ToArray();
			var text = "# ferrysim-csv: 1\n" + string.Join(",", columns) + "\n" + string.Join(",", cells) + "\n";

			var sample = TelemetryCsv.Parse(text, "x.csv").Sample(0);

			Assert.That(sample.State.Time, Is.EqualTo(35.0));
			Assert.That(sample.Telemetry.DepthUnderKeel, Is.EqualTo(48.1).Within(1e-9));
			Assert.That(sample.Controls.PortLever, Is.EqualTo(0.959).Within(1e-12));
		}

		[Test]
		public void TimeMustIncrease()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(Csv(RowAt(35.1), RowAt(35.1)), "x.csv"));
			StringAssert.Contains("line 5", error.Message);
			StringAssert.Contains("time", error.Message);
		}

		[Test]
		public void EmptyRecording_IsRejected()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(Csv(), "x.csv"));
			StringAssert.Contains("no data rows", error.Message);
		}

		[Test]
		public void HeaderValues_KeepEverythingAfterTheFirstColon()
		{
			var text = Csv(TypicalRow).Replace("# vessel: t-class", "# vessel: t-class\n# note: a: b");

			Assert.That(TelemetryCsv.Parse(text, "x.csv").HeaderValue("note"), Is.EqualTo("a: b"));
		}

		[Test]
		public void MissingFile_IsAClearError()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Load(Path.Combine(UIRepoPaths.Root, "no-such-file.csv")));
			StringAssert.Contains("not found", error.Message);
		}

		[Test]
		public void IndexAtOrBefore_HoldsTheMostRecentRow()
		{
			var recording = TelemetryCsv.Parse(Csv(RowAt(35.0), RowAt(35.1), RowAt(35.2)), "x.csv");

			Assert.That(recording.IndexAtOrBefore(34.0), Is.EqualTo(0));
			Assert.That(recording.IndexAtOrBefore(35.0), Is.EqualTo(0));
			Assert.That(recording.IndexAtOrBefore(35.05), Is.EqualTo(0));
			Assert.That(recording.IndexAtOrBefore(35.1), Is.EqualTo(1));
			Assert.That(recording.IndexAtOrBefore(35.19), Is.EqualTo(1));
			Assert.That(recording.IndexAtOrBefore(99.0), Is.EqualTo(2));
		}

		[Test]
		public void Columns_MatchTheCliSchemaInTheRepository()
		{
			// Reads the CLI's own column list, so a renamed or added CLI column fails here even without CLI runs on disk.
			var source = File.ReadAllText(Path.Combine(UIRepoPaths.Root, "core", "dotnet", "FerrySim.Cli", "TelemetryCsv.cs"));
			var names = Regex.Match(source, @"Names\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
			Assert.That(names.Success, Is.True, "TelemetryColumns.Names not found in the CLI source");
			var cliColumns = Regex.Matches(names.Groups["body"].Value, "\"([a-z_]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
			var version = Regex.Match(source, "FormatVersion\\s*=\\s*\"(\\d+)\"");

			Assert.That(TelemetryCsv.Columns, Is.EqualTo(cliColumns));
			Assert.That(version.Groups[1].Value, Is.EqualTo(TelemetryCsv.FormatVersion));
		}
	}
}
