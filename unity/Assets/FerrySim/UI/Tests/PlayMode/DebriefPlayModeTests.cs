using System.Collections;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The debrief screen: loads the recorded crash stop from its CSV path, plots it, marks the events and
	/// scrubs. Screenshots artifacts/unity/ui-debrief-day.png and ui-debrief-night.png use the crash stop
	/// with a synthetic shallow patch and contact written over it (the subtitle says so) so every event
	/// kind is visible.
	/// </summary>
	public class DebriefPlayModeTests
	{
		private UITestRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator Debrief_LoadsTheCliCsvPlotsItMarksShiftsAndScrubs()
		{
			_rig = new UITestRig(1280, 720);
			var view = _rig.View;
			var debrief = view.Debrief;

			Assert.That(debrief.TryLoad(UIRepoPaths.SampleCrashStopCsv), Is.True, debrief.ErrorText);
			view.DebriefVisible = true;
			yield return UITestRig.Frames();

			var recording = debrief.Recording;
			Assert.That(recording.Count, Is.EqualTo(310));
			Assert.That(debrief.Parameters, Is.Not.Null, "vessel named in the CSV header is loaded for scale");
			Assert.That(debrief.Events.Count(e => e.Kind == DebriefEventKind.GearShift), Is.EqualTo(2));
			Assert.That(debrief.SpeedPlot.Series.Count, Is.EqualTo(2));
			Assert.That(debrief.SpeedPlot.LegendText, Is.EqualTo("SOG, STW"));
			Assert.That(debrief.HeadingPlot.Compass, Is.True);
			Assert.That(debrief.ControlsPlot.ValueRange.Min, Is.EqualTo(-100.0));
			Assert.That(debrief.SpeedPlot.ValueRange.Max, Is.GreaterThanOrEqualTo(25.0), "the plot range covers the 25 kn approach");
			Assert.That(debrief.Track.EastRange.Span / debrief.Track.NorthRange.Span, Is.GreaterThan(0.0));

			// Scrub to the first row showing ASTERN and check the readouts follow the cursor.
			var asternTime = recording.Time[recording.PortGear.ToList().IndexOf(GearState.Astern)];
			debrief.SetCursorTime(asternTime + 0.04);
			yield return UITestRig.Frames(2);
			Assert.That(debrief.CursorIndex, Is.EqualTo(recording.IndexAtOrBefore(asternTime)), "cursor holds the most recent sample");
			StringAssert.StartsWith("ASTERN", debrief.ReadoutText("port"));
			Assert.That(debrief.ReadoutText("levers"), Is.EqualTo("100% AST / 100% AST"));
			Assert.That(debrief.Timeline.CursorTime, Is.EqualTo(debrief.CursorTime));
			Assert.That(debrief.SpeedPlot.CursorTime, Is.EqualTo(debrief.CursorTime));
			Assert.That(debrief.Track.CursorIndex, Is.EqualTo(debrief.CursorIndex));

			debrief.Step(-1);
			Assert.That(debrief.CursorIndex, Is.EqualTo(recording.IndexAtOrBefore(asternTime) - 1));
			StringAssert.StartsWith("SHIFTING", debrief.ReadoutText("port"));

			view.DistanceUnit = DistanceUnit.Metres;
			yield return UITestRig.Frames(2);
			StringAssert.EndsWith(" m", debrief.ReadoutText("depth"));
			StringAssert.Contains("m from start", debrief.Track.TitleText);
		}

		[UnityTest]
		public IEnumerator Debrief_ShowsEveryEventKind_InDayAndNightScreenshots()
		{
			_rig = new UITestRig(1280, 720);
			var view = _rig.View;
			var recording = SyntheticRuns.CrashStopWithShallowsAndContact(TelemetryCsv.Load(UITestRig.SamplePath), 55.0, 58.0, 61.0, 61.5);
			view.Debrief.Show(recording, VesselFiles.TryLoad(recording.VesselId, out _));
			view.DebriefVisible = true;
			view.Debrief.SetCursorTime(47.0);

			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();
				var kinds = view.Debrief.Events.Select(e => e.Kind).Distinct().OrderBy(k => k).ToArray();
				Assert.That(kinds, Is.EqualTo(new[] { DebriefEventKind.GearShift, DebriefEventKind.Contact, DebriefEventKind.DepthAlarm }));
				Assert.That(view.Debrief.Q<ScrollView>("debrief-events").contentContainer.childCount, Is.EqualTo(view.Debrief.Events.Count));
				var image = _rig.Screenshot($"ui-debrief-{palette.ToString().ToLowerInvariant()}.png");
				Object.Destroy(image);
			}
		}

		[UnityTest]
		public IEnumerator Debrief_ReportsABadFileOnScreen()
		{
			_rig = new UITestRig(1280, 720);
			var debrief = _rig.View.Debrief;
			var bad = Path.Combine(UIRepoPaths.Root, "artifacts", "unity", "not-a-telemetry.csv");
			Directory.CreateDirectory(Path.GetDirectoryName(bad));
			File.WriteAllText(bad, "time,speed\n0,1\n");

			Assert.That(debrief.TryLoad(bad), Is.False);
			yield return null;
			StringAssert.Contains("ferrysim-csv", debrief.ErrorText);
			Assert.That(debrief.Recording, Is.Null);
		}
	}
}
