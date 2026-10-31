using System.Collections;
using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: a converging target not raising the alarm (or the list and the banner not showing it), a receding or
	// parallel target raising it, the alarm surviving the target's passing, and the selection not cycling through the list.
	// For the alarm keys (v0.3.1): mute hiding more than the treatment (a row, a value, the ranking) or less (a filled row
	// or target while muted); the limits readout or the muted banner missing in a palette; a wider limit not raising the
	// alarm on a target the default leaves alone.
	public class AlarmPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator AConvergingTarget_RaisesTheAlarm_AndItClearsOncePast()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			// Own ship northbound at 6 kn from (-1500, 0); a tug 900 m ahead and 60 m to starboard, southbound at 6 kn:
			// closing at 12 kn, CPA 60 m in about 146 s. A parallel ferry abeam never closes; a receding one is already past.
			_rig.Own.Place(0, -1500, 0, 0, 6);
			_rig.Targets.Add("TUG", 111, -600, 60, -Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("FERRY", 222, -1500, 400, Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("BARGE", 333, -1400, 0, Core.Contracts.Units.KnotsToMetresPerSecond(9), 0);
			_rig.View.SpanNauticalMiles = 2.0;
			for (var i = 0; i < 3; i++) yield return null;
			var view = _rig.View;
			Assert.That(view.Alarm, Is.True);
			Assert.That(view.Ranking.AlarmCount, Is.EqualTo(1));
			Assert.That(view.List.AlarmShown, Is.True);
			Assert.That(view.List.RowName(0), Is.EqualTo("TUG"), "the alarm row comes first");
			Assert.That(view.List.RowCpa(0), Is.EqualTo("0.03"), "60 m is 0.03 nm");
			Assert.That(view.List.RowTcpa(0), Is.EqualTo("2:26"));
			Assert.That(view.List.RowBearing(0), Is.EqualTo("004"));
			Assert.That(view.List.RowName(1), Is.EqualTo("FERRY"), "the parallel ferry (TCPA 0, active) before the past barge");
			Assert.That(view.Ranking.Encounter(1).Tcpa, Is.EqualTo(0.0), "parallel course: no relative motion");
			Assert.That(view.List.RowName(2), Is.EqualTo("BARGE"), "past its closest point: last, however small its CPA");
			Assert.That(view.List.RowTcpa(2), Is.EqualTo("PAST"));
			Assert.That(view.List.RowDimmed(2), Is.True, "PAST rows read dimmed");
			Assert.That(view.List.RowDimmed(1), Is.False);

			// A stricter TCPA limit silences it; the default raises it again.
			view.AlarmLimits = new CpaAlarmLimits(185.2, 60);
			yield return null;
			Assert.That(view.Alarm, Is.False);
			Assert.That(view.List.AlarmShown, Is.False);
			view.AlarmLimits = CpaAlarmLimits.Default;
			yield return null;
			Assert.That(view.Alarm, Is.True);

			// Selection cycles the list rows, then none.
			view.SelectNextTarget();
			Assert.That(view.SelectedTarget, Is.EqualTo(0));
			view.SelectNextTarget();
			Assert.That(view.SelectedTarget, Is.EqualTo(1), "the second row is the ferry, roster index 1");
			view.SelectNextTarget();
			view.SelectNextTarget();
			Assert.That(view.SelectedTarget, Is.EqualTo(-1));

			// 200 s on: the tug has passed; the alarm is down.
			_rig.Own.Place(200, -1500 + Core.Contracts.Units.KnotsToMetresPerSecond(6) * 200, 0, 0, 6);
			yield return null;
			yield return null;
			Assert.That(view.Alarm, Is.False, "past the closest point");
			Assert.That(view.List.RowTcpa(0), Is.EqualTo("PAST").Or.EqualTo("0:00"));
		}

		[UnityTest]
		public IEnumerator Mute_HidesOnlyTheAlarmTreatment_AndTheReadoutShowsInEveryPalette()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			// The same geometry as the converging case: the tug is an alarm (CPA 60 m in 146 s), the ferry parallel, the barge past.
			_rig.Own.Place(0, -1500, 0, 0, 6);
			_rig.Targets.Add("TUG", 111, -600, 60, -Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("FERRY", 222, -1500, 400, Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("BARGE", 333, -1400, 0, Core.Contracts.Units.KnotsToMetresPerSecond(9), 0);
			_rig.View.SpanNauticalMiles = 2.0;
			for (var i = 0; i < 3; i++) yield return null;
			var view = _rig.View;
			var list = view.List;
			Assert.That(view.AlarmShown, Is.True);
			Assert.That(list.AlarmShown, Is.True);
			Assert.That(list.BannerShown, Is.EqualTo(CpaList.AlarmText));
			Assert.That(list.RowFilled(0), Is.True, "the alarm row is filled");
			Assert.That(view.Targets.TargetAlarmShown(0), Is.True, "the tug is drawn as an alarm");
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.10 NM · 6:00"), "the default limits read out");

			view.ToggleAlarmMute();
			yield return null;
			Assert.That(view.AlarmMuted, Is.True);
			Assert.That(view.Alarm, Is.True, "the ranking still holds the alarm");
			Assert.That(view.Ranking.AlarmCount, Is.EqualTo(1));
			Assert.That(view.AlarmShown, Is.False, "but no treatment is drawn");
			Assert.That(list.AlarmShown, Is.False);
			Assert.That(list.MutedShown, Is.True);
			Assert.That(list.BannerShown, Is.EqualTo(CpaList.MutedText));
			Assert.That(list.RowFilled(0), Is.False, "the row is not filled while muted");
			Assert.That(view.Targets.TargetAlarmShown(0), Is.False, "nor the target");
			Assert.That(list.Shown, Is.EqualTo(3), "every row stays");
			Assert.That(list.RowName(0), Is.EqualTo("TUG"), "the alarm still leads: mute does not change the ranking");
			Assert.That(list.RowCpa(0), Is.EqualTo("0.03"), "and its values stay");
			Assert.That(list.RowTcpa(0), Is.EqualTo("2:26"));
			Assert.That(list.RowName(1), Is.EqualTo("FERRY"));
			Assert.That(list.RowName(2), Is.EqualTo("BARGE"));
			Assert.That(list.RowDimmed(2), Is.True, "PAST rows still read dimmed");
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.10 NM · 6:00"), "mute changes no limit");

			// The readout and the muted banner in every palette: the text holds and takes each palette's colours.
			foreach (var palette in new[] { ChartPaletteKind.Night, ChartPaletteKind.Dusk, ChartPaletteKind.Day })
			{
				view.Palette = palette;
				yield return null;
				var colours = ChartPalette.For(palette);
				Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.10 NM · 6:00"), palette.ToString());
				Assert.That(list.BannerShown, Is.EqualTo(CpaList.MutedText), palette.ToString());
				Assert.That(list.MutedShown, Is.True, palette.ToString());
				Assert.That(list.RowFilled(0), Is.False, palette.ToString());
				Assert.That(list.BannerColour, Is.EqualTo((Color)colours.Instruments.Caution).Using(ColourComparer), palette + ": the muted banner in the caution colour");
				Assert.That(list.LimitsColour, Is.EqualTo((Color)colours.TextDim).Using(ColourComparer), palette + ": the readout in the dim text colour");
			}

			// Unmuted again, the treatment returns at once; a narrower CPA limit (0.05 nm) silences the tug (CPA 0.03 nm is still under it: it stays);
			// a shorter TCPA limit (1 min) silences it; and the readout follows each key.
			view.ToggleAlarmMute();
			yield return null;
			Assert.That(view.AlarmMuted, Is.False);
			Assert.That(list.AlarmShown, Is.True);
			Assert.That(list.RowFilled(0), Is.True);
			Assert.That(view.Targets.TargetAlarmShown(0), Is.True);
			view.StepCpaLimit(-1);
			yield return null;
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.05 NM · 6:00"));
			Assert.That(view.Alarm, Is.True, "60 m is still under 0.05 nm");
			for (var i = 0; i < 5; i++) view.StepTcpaLimit(-1);
			yield return null;
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.05 NM · 1:00"));
			Assert.That(view.Alarm, Is.False, "2:26 is beyond a 1 min limit");
			Assert.That(list.AlarmShown, Is.False);
			Assert.That(list.BannerShown, Is.EqualTo(CpaList.BannerText));
			// The wave 3c order holds under the new limit: the parallel ferry (TCPA 0, active) now precedes the tug, whose closest point is beyond the limit.
			Assert.That(list.RowName(0), Is.EqualTo("FERRY"), "active targets first");
			Assert.That(list.RowName(1), Is.EqualTo("BARGE"), "then the rest by CPA: the receding barge's extrapolated line passes through own ship (CPA 0, PAST)");
			Assert.That(list.RowName(2), Is.EqualTo("TUG"), "the tug's 60 m is beyond that");
			for (var i = 0; i < 5; i++) view.StepTcpaLimit(1);
			view.StepCpaLimit(1);
			yield return null;
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 0.10 NM · 6:00"));
			Assert.That(view.Alarm, Is.True);
			// A wide CPA limit makes the ferry (parallel, TCPA 0) no alarm still, so widening never promotes a target that never closes.
			for (var i = 0; i < 20; i++) view.StepCpaLimit(1);
			yield return null;
			Assert.That(list.LimitsText, Is.EqualTo("LIMITS 1.00 NM · 6:00"));
			Assert.That(view.Ranking.AlarmCount, Is.EqualTo(1), "the parallel ferry and the past barge are never alarms");
			Assert.That(list.RowName(0), Is.EqualTo("TUG"));
		}

		private static readonly System.Collections.Generic.IEqualityComparer<Color> ColourComparer = new ColourEquality();

		private sealed class ColourEquality : System.Collections.Generic.IEqualityComparer<Color>
		{
			public bool Equals(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

			public int GetHashCode(Color c) => 0;
		}

		[UnityTest]
		public IEnumerator ANewRun_ClearsTheTrackAndTheSelection()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Targets.Add("TUG", 111, -600, 60, -3, 0);
			for (var i = 0; i < 40; i++)
			{
				_rig.Own.Place(i * 5.0, -1500 + i * 15.0, 0, 0, 6);
				yield return null;
			}

			Assert.That(_rig.View.Track.Count, Is.GreaterThan(10), "fixes every 20 m");
			_rig.View.SelectNextTarget();
			Assert.That(_rig.View.SelectedTarget, Is.EqualTo(0));
			_rig.Own.RunId = 2;
			_rig.Own.Place(0, -1500, 0, 0, 6);
			yield return null;
			Assert.That(_rig.View.Track.Count, Is.EqualTo(1), "a new run starts a new track");
			Assert.That(_rig.View.SelectedTarget, Is.EqualTo(-1));
		}
	}
}
