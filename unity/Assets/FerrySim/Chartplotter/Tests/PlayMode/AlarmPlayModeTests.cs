using System.Collections;
using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: a converging target not raising the alarm (or the list and the banner not showing it), a receding or
	// parallel target raising it, the alarm surviving the target's passing, and the selection not cycling through the list.
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
