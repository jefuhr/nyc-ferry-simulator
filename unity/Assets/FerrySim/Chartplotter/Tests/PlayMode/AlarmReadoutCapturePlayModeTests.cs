using System.Collections;
using System.IO;
using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// Visual evidence of the alarm readout (v0.3.1) at the plotter's shipping size: the 560×400 placement on a
	/// 1920×1080 panel (1.5× the 1280×720 design), the synthetic harbor with a converging alarm target, by day and
	/// by night, armed and muted, writing artifacts/chartplotter/alarm-readout-*.png. Failure modes: the readout or the
	/// muted banner missing, clipped (the sixth row pushed below the footer) or illegible in a palette.
	/// </summary>
	public class AlarmReadoutCapturePlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator DayAndNight_ArmedAndMuted_AtTheShippingSize()
		{
			_rig = new PlotterRig(1920, 1080, new Rect(8, 8, 560, 400));
			_rig.Own.Place(0, -1500, 0, 0, 6);
			_rig.Targets.Add("TUG", 111, -600, 60, -Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("FERRY", 222, -1500, 400, Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			_rig.Targets.Add("BARGE", 333, -1400, 0, Core.Contracts.Units.KnotsToMetresPerSecond(9), 0);
			_rig.Targets.Add("LIGHTER", 444, -900, -300, 0, 1.5);
			_rig.Targets.Add("PILOT", 555, -2000, 200, 2, -1);
			_rig.Targets.Add("TENDER", 666, -1200, 500, -1, -1);
			_rig.View.SpanNauticalMiles = 2.0;
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(_rig.View.List.Shown, Is.EqualTo(6), "six rows under the readout");
			var list = _rig.View.List;
			var listBottom = list.worldBound.yMax;
			var footerTop = _rig.View.worldBound.yMax - 20f * 1.5f;
			Assert.That(list.worldBound.height, Is.GreaterThan(0));
			foreach (var palette in new[] { ChartPaletteKind.Day, ChartPaletteKind.Night })
			{
				_rig.View.Palette = palette;
				_rig.View.AlarmMuted = false;
				_rig.View.AlarmLimits = CpaAlarmLimits.Default;
				for (var i = 0; i < 3; i++) yield return null;
				Assert.That(_rig.View.AlarmShown, Is.True);
				var armed = _rig.Screenshot("alarm-readout-" + palette.ToString().ToLowerInvariant() + "-armed-1080p.png");
				Object.Destroy(armed);
				_rig.View.ToggleAlarmMute();
				_rig.View.StepCpaLimit(1);
				_rig.View.StepTcpaLimit(2);
				for (var i = 0; i < 3; i++) yield return null;
				Assert.That(_rig.View.List.MutedShown, Is.True);
				Assert.That(_rig.View.LimitsText, Is.EqualTo("LIMITS 0.15 NM · 8:00"));
				var muted = _rig.Screenshot("alarm-readout-" + palette.ToString().ToLowerInvariant() + "-muted-1080p.png");
				Object.Destroy(muted);
				// The sixth row's values sit above the footer: nothing is clipped by the readout line.
				var lastRow = list[list.childCount - 1];
				Assert.That(lastRow.worldBound.yMax, Is.LessThanOrEqualTo(footerTop + 0.5f), palette + ": the last row ends above the footer (" + lastRow.worldBound.yMax + " vs " + footerTop + ")");
				TestContext.WriteLine(palette + ": list " + list.worldBound.height.ToString("F0") + " px tall, last row bottom " + lastRow.worldBound.yMax.ToString("F1") + ", footer top " + footerTop.ToString("F1"));
			}
		}
	}
}
