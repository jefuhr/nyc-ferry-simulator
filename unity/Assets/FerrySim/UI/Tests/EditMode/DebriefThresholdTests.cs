using System;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Debrief depth alarms come from the threshold the learner actually had when the CSV records it, and
	/// are labelled as a retrospective reconstruction when it does not (review finding 8). Guards against:
	/// lowering today's threshold hiding an alarm the learner saw, raising it inventing alarms that never
	/// went off, a reconstruction presented as a record, a malformed threshold column read as data, and the
	/// writer dropping the recorded thresholds.
	/// </summary>
	public class DebriefThresholdTests
	{
		private const string ThresholdColumn = "depth_alarm_threshold_m";

		[Test]
		public void RecordedThresholdHistory_DecidesTheAlarms_NotTodaysThreshold()
		{
			// 1.2 m under the keel from 5 to 8 s with the 1.524 m default: the learner saw an alarm. They lowered
			// the threshold to 0.5 m at 10 s, so 1.0 m from 14 to 16 s raised nothing.
			var recording = TelemetryCsv.Parse(CsvWithThresholds(), "history.csv");

			foreach (var today in new[] { 0.5, 3.0 })
			{
				var alarms = DebriefEvents.Detect(recording, today).Where(e => e.Kind == DebriefEventKind.DepthAlarm).ToList();

				Assert.That(alarms.Count, Is.EqualTo(1), $"today's threshold {today} m must neither hide nor invent alarms");
				Assert.That(alarms[0].Time, Is.EqualTo(5.0).Within(1e-9));
				Assert.That(alarms[0].EndTime, Is.EqualTo(8.0).Within(1e-9));
				Assert.That(alarms[0].Value, Is.EqualTo(1.2).Within(1e-9));
			}
		}

		[Test]
		public void Debrief_SaysWhichThresholdItsDepthAlarmsUse()
		{
			var debrief = new DebriefScreen();

			debrief.Show(TelemetryCsv.Load(UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv)), null);
			Assert.That(Note(debrief), Is.Not.Null, "the debrief notes where its depth alarms come from");
			Assert.That(Note(debrief), Does.Contain("retrospective, using the current threshold"), "the CLI sample records no threshold");

			debrief.Show(TelemetryCsv.Parse(CsvWithThresholds(), "history.csv"), null);
			Assert.That(Note(debrief), Does.Not.Contain("retrospective"));
			Assert.That(Note(debrief), Does.Contain("recorded"));
		}

		[TestCase("nan")]
		[TestCase("-0.5")]
		[TestCase("")]
		public void ThresholdColumn_RejectsCellsThatAreNotAThreshold(string cell)
		{
			var text = CsvWithThresholds().Replace(",1.524\n", "," + cell + "\n");

			var error = Assert.Throws<TelemetryCsvException>(() => TelemetryCsv.Parse(text, "bad-threshold.csv"));

			StringAssert.Contains(ThresholdColumn, error.Message);
		}

		[Test]
		public void Writer_KeepsTheRecordedThresholds()
		{
			var recording = TelemetryCsv.Parse(CsvWithThresholds(), "history.csv");

			var text = TelemetryCsvWriter.Render(recording);

			StringAssert.Contains("," + ThresholdColumn + "\n", text);
			var again = TelemetryCsv.Parse(text, "again.csv");
			Assert.That(DebriefEvents.Detect(again, 3.0).Count(e => e.Kind == DebriefEventKind.DepthAlarm), Is.EqualTo(1));
			Assert.That(TelemetryCsvWriter.Render(again), Is.EqualTo(text), "round trip");
		}

		private static string Note(DebriefScreen debrief) => debrief.Q<Label>("debrief-events-note")?.text;

		/// <summary>Rows every 0.5 s for 20 s with a <c>depth_alarm_threshold_m</c> column after the CLI columns.</summary>
		private static string CsvWithThresholds()
		{
			var builder = new StringBuilder("# ferrysim-csv: 1\n# vessel: t-class\n# source: unity\n");
			builder.Append(string.Join(",", TelemetryCsv.Columns)).Append(',').Append(ThresholdColumn).Append('\n');
			for (var i = 0; i <= 40; i++)
			{
				var time = i * 0.5;
				var depth = time >= 5.0 && time < 8.0 ? 1.2 : time >= 14.0 && time < 16.0 ? 1.0 : 10.0;
				var threshold = time < 10.0 ? 1.524 : 0.5;
				builder.Append(time.ToString("F3", CultureInfo.InvariantCulture))
					.Append(",100.000,200.000,45.0000,4.1156,0.0000,0.0000,8.0000,45.000,8.0000,1200.00,1200.00,480.00,480.00,ahead,ahead,0.000,0.000,")
					.Append(depth.ToString("F3", CultureInfo.InvariantCulture))
					.Append(",0.0,0.5000,0.5000,0.0000,")
					.Append(threshold.ToString("0.0##", CultureInfo.InvariantCulture))
					.Append('\n');
			}

			return builder.ToString();
		}
	}
}
