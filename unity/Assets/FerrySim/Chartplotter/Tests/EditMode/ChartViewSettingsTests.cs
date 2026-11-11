using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure modes: a zoom past the limits, a ladder step that skips or sticks, orientation or centring state lost, and
	// alarm limits not applied. The view is constructed without a panel (UI Toolkit allows it); nothing is laid out here.
	public class ChartViewSettingsTests
	{
		[Test]
		public void Zoom_FollowsTheLadderWithinLimits()
		{
			var view = new ChartplotterView();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(1.0));
			view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(2.0));
			view.ZoomOut(); view.ZoomOut(); view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(8.0), "stops at 8");
			view.SpanNauticalMiles = 20;
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(8.0), "clamped");
			view.SpanNauticalMiles = 0.1;
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.25), "clamped");
			view.ZoomIn();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.25), "stays at 0.25");
			view.SpanNauticalMiles = 0.7;
			view.ZoomIn();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(0.5), "off the ladder, zooming in takes the next rung in");
			view.SpanNauticalMiles = 0.7;
			view.ZoomOut();
			Assert.That(view.SpanNauticalMiles, Is.EqualTo(1.0));
			view.Canvas.Dispose();
			view.Targets.Dispose();
		}

		// Failure modes of the alarm keys: a step that leaves the grid or passes a bound, mute changing the limits or the
		// ranking, and the readout string not following a change (or being rebuilt without one).
		[Test]
		public void AlarmLimitKeys_StepAndClampOnTheGrid_AndMuteIsSeparate()
		{
			var view = new ChartplotterView();
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa));
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa));
			Assert.That(view.AlarmMuted, Is.False, "a new view starts unmuted");
			view.StepCpaLimit(1);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(277.8).Within(1e-9), "0.10 to 0.15 nm");
			view.StepCpaLimit(-1);
			view.StepCpaLimit(-1);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "0.05 nm floor");
			view.StepCpaLimit(-1);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MinimumCpa), "held at the floor");
			for (var i = 0; i < 30; i++) view.StepCpaLimit(1);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MaximumCpa), "1 nm ceiling");
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.DefaultTcpa), "the CPA keys leave the TCPA limit alone");
			view.StepTcpaLimit(1);
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(420.0), "6 to 7 min");
			for (var i = 0; i < 10; i++) view.StepTcpaLimit(-1);
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.MinimumTcpa), "1 min floor");
			for (var i = 0; i < 40; i++) view.StepTcpaLimit(1);
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.MaximumTcpa), "30 min ceiling");
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MaximumCpa), "the TCPA keys leave the CPA limit alone");
			view.ToggleAlarmMute();
			Assert.That(view.AlarmMuted, Is.True);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.MaximumCpa), "mute changes no limit");
			Assert.That(view.AlarmLimits.Tcpa, Is.EqualTo(CpaAlarmLimits.MaximumTcpa));
			view.ToggleAlarmMute();
			Assert.That(view.AlarmMuted, Is.False);
			view.AlarmLimits = CpaAlarmLimits.Default;
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(CpaAlarmLimits.DefaultCpa));
			view.Canvas.Dispose();
			view.Targets.Dispose();
		}

		[Test]
		public void AlarmLimitReadout_IsBuiltFromTheLimits()
		{
			Assert.That(PlotterStrings.AlarmLimits(CpaAlarmLimits.Default), Is.EqualTo("LIMITS 0.10 NM · 6:00"));
			Assert.That(PlotterStrings.AlarmLimits(new CpaAlarmLimits(CpaAlarmLimits.MinimumCpa, CpaAlarmLimits.MinimumTcpa)), Is.EqualTo("LIMITS 0.05 NM · 1:00"));
			Assert.That(PlotterStrings.AlarmLimits(new CpaAlarmLimits(CpaAlarmLimits.MaximumCpa, CpaAlarmLimits.MaximumTcpa)), Is.EqualTo("LIMITS 1.00 NM · 30:00"));
			Assert.That(CpaList.MutedText, Is.EqualTo("CPA / TCPA · MUTED"));
		}

		[Test]
		public void Orientation_Centring_AndLimits()
		{
			var view = new ChartplotterView();
			view.ToggleOrientation();
			Assert.That(view.Orientation, Is.EqualTo(ChartOrientation.CourseUp));
			Assert.That(view.Centred, Is.True);
			view.Pan(10, 10);
			Assert.That(view.Centred, Is.True, "no view yet: a pan has nothing to hold");
			view.AlarmLimits = new CpaAlarmLimits(300, 120);
			Assert.That(view.AlarmLimits.Cpa, Is.EqualTo(300));
			view.VectorMinutes = 0;
			Assert.That(view.VectorMinutes, Is.EqualTo(ChartplotterView.DefaultVectorMinutes), "a non-positive vector time is ignored");
			view.SafetyContourMetres = 4.5;
			Assert.That(view.SafetyContourMetres, Is.EqualTo(4.5));
			view.SafetyContourMetres = -1;
			Assert.That(view.SafetyContourMetres, Is.EqualTo(4.5));
			view.VisibilityMetres = -5;
			Assert.That(view.VisibilityMetres, Is.EqualTo(0));
			view.CyclePalette();
			Assert.That(view.Palette, Is.EqualTo(ChartPaletteKind.Dusk));
			view.CyclePalette(); view.CyclePalette();
			Assert.That(view.Palette, Is.EqualTo(ChartPaletteKind.Day));
			Assert.That(view.List.Rows, Is.EqualTo(CpaList.DefaultRows));
			Assert.That(view.Canvas, Is.InstanceOf<VisualElement>());
			view.Canvas.Dispose();
			view.Targets.Dispose();
		}
	}
}
