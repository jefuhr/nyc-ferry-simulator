using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// CSV playback takes the scenario start from the <c># scenario_start_utc:</c> header the Unity recorder
	/// writes (review finding 5). Guards against: the clock ignoring it, so a winter run reads EDT and the
	/// wrong hour; the previous recording's start surviving the next load; a malformed start being ignored
	/// instead of rejected; and the setting-based fallback for CLI files (which carry no start) being used
	/// without saying so on the panel.
	/// </summary>
	public class ScenarioStartTests
	{
		private const double Threshold = DepthAlarm.DefaultThresholdMetres;

		private static readonly string ColumnRow = string.Join(",", TelemetryCsv.Columns);

		private const string RowAfterTime =
			",433.992,0.000,0.0000,12.8597,0.0000,0.0000,24.9973,0.000,24.9973,2025.66,2025.66,804.15,804.15,ahead,ahead,0.000,0.000,48.100,0.0,0.9590,0.9590,0.0000";

		private GameObject _host;
		private FakeTelemetrySource _source;

		[SetUp]
		public void SetUp()
		{
			_host = new GameObject("ScenarioStartTests");
			_source = _host.AddComponent<FakeTelemetrySource>();
			_source.SetScenarioStart("2026-06-15T13:00:00Z");
		}

		[TearDown]
		public void TearDown() => Object.DestroyImmediate(_host);

		[Test]
		public void UnityRecording_SetsTheScenarioStart()
		{
			_source.Load(TelemetryCsv.Parse(RecorderCsv("2026-01-15T14:00:00Z"), "winter.csv"), null);

			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero)));
		}

		[Test]
		public void ConsecutiveLoads_EachUseTheirOwnStart_AndThePanelClockFollows()
		{
			var panel = new WheelhousePanel();

			_source.Load(TelemetryCsv.Parse(RecorderCsv("2026-01-15T14:00:00Z"), "winter.csv"), null);
			panel.Refresh(_source, DistanceUnit.Feet, Threshold, 0.0);
			Assert.That(panel.Clock.text, Is.EqualTo("09:00:00"), "14:00 UTC in January is 09:00 EST");
			Assert.That(panel.ClockZone.text, Is.EqualTo("EST"));
			Assert.That(ClockNote(panel), Is.Empty, "a recorded start needs no note");

			_source.Load(TelemetryCsv.Parse(RecorderCsv("2026-07-04T16:30:00Z"), "summer.csv"), null);
			panel.Refresh(_source, DistanceUnit.Feet, Threshold, 0.1);
			Assert.That(panel.Clock.text, Is.EqualTo("12:30:00"));
			Assert.That(panel.ClockZone.text, Is.EqualTo("EDT"));

			// A CLI file carries no start: the fallback setting applies, and the panel says it is assumed.
			Assert.That(_source.TryLoadCsv(UIRepoPaths.SampleCrashStopCsv), Is.True, _source.LoadError);
			panel.Refresh(_source, DistanceUnit.Feet, Threshold, 0.2);
			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero)), "not the summer recording's start");
			Assert.That(panel.Clock.text, Is.EqualTo("09:00:35"));
			Assert.That(ClockNote(panel), Is.EqualTo("ASSUMED START"));

			_source.Load(TelemetryCsv.Parse(RecorderCsv("2026-01-15T14:00:00Z"), "winter.csv"), null);
			panel.Refresh(_source, DistanceUnit.Feet, Threshold, 0.3);
			Assert.That(panel.ClockZone.text, Is.EqualTo("EST"));
			Assert.That(ClockNote(panel), Is.Empty);
		}

		[Test]
		public void MalformedScenarioStart_IsRejectedNamingTheFile()
		{
			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(RecorderCsv("next tuesday"), "bad-start.csv"));

			StringAssert.Contains("bad-start.csv", error.Message);
			StringAssert.Contains("scenario_start_utc", error.Message);
		}

		[TestCase("2026-06-01T13:00:00Z", 13, 0, 0, 0)]
		[TestCase("2026-06-01T13:00:00.25Z", 13, 0, 0, 250)]
		[TestCase("2026-06-01T09:00:00-04:00", 13, 0, 0, 0)]
		public void ScenarioStart_AcceptsIso8601WithFractionsAndOffsets(string text, int hour, int minute, int second, int millisecond)
		{
			_source.Load(TelemetryCsv.Parse(RecorderCsv(text), "start.csv"), null);

			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 6, 1, hour, minute, second, millisecond, TimeSpan.Zero)));
		}

		[Test]
		public void SettingTheFallback_DoesNotOverrideARecordedStart()
		{
			_source.Load(TelemetryCsv.Parse(RecorderCsv("2026-01-15T14:00:00Z"), "winter.csv"), null);

			Assert.That(_source.SetScenarioStart("2026-03-01T12:00:00Z"), Is.True);

			Assert.That(_source.ScenarioStartUtc, Is.EqualTo(new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero)));
		}

		private static string ClockNote(WheelhousePanel panel) => panel.Q<Label>("clock-note")?.text;

		/// <summary>A telemetry CSV with the header keys the Unity run recorder writes, including the scenario start.</summary>
		internal static string RecorderCsv(string scenarioStartUtc) =>
			"# ferrysim-csv: 1\n# vessel: t-class\n# maneuver: unity-run\n# maneuver_start_s: 0\n# physics_hz: 100\n# sample_hz: 10\n" +
			"# source: unity\n# scenario_start_utc: " + scenarioStartUtc + "\n" +
			ColumnRow + "\n0.000" + RowAfterTime + "\n0.100" + RowAfterTime + "\n0.200" + RowAfterTime + "\n";
	}
}
