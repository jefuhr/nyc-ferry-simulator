using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The panel's and the HUD's readouts of the water and the air, through the whole view. Guards against: values
	/// or a stale "NO WIND DATA" or coverage warning shown with no source or once <c>HasEnvironment</c> is false; the
	/// HUD and the panel disagreeing; the distance unit not reaching the tide; a pack run's clock ignoring the pack's
	/// offset, and a calm or uniform run's clock not saying it is UTC; coverage warnings that ignore the counts or
	/// carry into a new run; and any change to the clock without an environment source.
	/// </summary>
	public class EnvironmentPanelTests
	{
		private static WheelhouseView View(IEnvironmentReadoutSource environment)
		{
			var view = new WheelhouseView { HudVisible = true };
			view.EnvironmentSource = environment;
			return view;
		}

		private static bool Shown(VisualElement element) => element.style.display != DisplayStyle.None;

		[Test]
		public void WithoutAnEnvironmentSource_EveryReadoutShowsNoData_AndTheClockIsNewYorkTimeAsBefore()
		{
			var view = View(null);
			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			AssertNoEnvironment(view, "no source");
			Assert.That(view.Panel.Clock.text, Is.EqualTo("09:00:10"), "13:00:10 UTC on 15 June is 09:00:10 EDT");
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("EDT"));
			Assert.That(Shown(view.Panel.Date), Is.False, "no date without an environment");
			Assert.That(view.Hud.Clock.text, Is.EqualTo("09:00:10"));
			Assert.That(view.Hud.ClockZone.text, Is.EqualTo("EDT"));
		}

		[Test]
		public void HasEnvironmentFalse_HidesTheLastValuesAndWarnings()
		{
			var environment = StubEnvironmentSource.With(Readouts.Pack(windMissing: true, timeHolds: 4, depthMisses: 9));
			var view = View(environment);
			var telemetry = StubTelemetrySource.Underway();
			view.Refresh(telemetry, 0.0);
			Assert.That(view.Panel.EnvironmentReadouts.WindFrom.text, Is.EqualTo(EnvironmentFormat.NoWindData), "the fixture shows data while it has it");
			Assert.That(view.Panel.DayData.Indication, Is.EqualTo(CoverageIndication.Live));

			// The run ends: the source still holds its last readout, but it is no longer current.
			environment.HasEnvironment = false;
			view.Refresh(telemetry, 0.1);

			AssertNoEnvironment(view, "HasEnvironment false");
			Assert.That(view.Panel.Clock.text, Is.EqualTo("09:00:10"), "the clock falls back to New York time from the telemetry");
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("EDT"));
		}

		[Test]
		public void APackRun_ShowsTideSetDriftWind_AndTheLocalDateAndClock()
		{
			var view = View(StubEnvironmentSource.With(Readouts.Pack()));
			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			foreach (var (readouts, where) in new[] { (view.Panel.EnvironmentReadouts, "panel"), (view.Hud.EnvironmentReadouts, "HUD") })
			{
				Assert.That(readouts.Tide.text, Is.EqualTo("1.0"), where);
				Assert.That(readouts.TideUnit.text, Is.EqualTo("ft"), where);
				Assert.That(readouts.TideTrend.text, Is.EqualTo("RISING"), where);
				Assert.That(readouts.TideArrow.Trend, Is.EqualTo(TideTrend.Rising), where);
				Assert.That(readouts.TideDatum.text, Is.EqualTo("NAVD88"), where);
				Assert.That(readouts.Set.text, Is.EqualTo("047"), where);
				Assert.That(readouts.Drift.text, Is.EqualTo("1.8"), where);
				Assert.That(readouts.WindCaption.text, Is.EqualTo("WIND FROM"), where);
				Assert.That(readouts.WindFrom.text, Is.EqualTo("250"), where);
				Assert.That(readouts.WindSpeed.text, Is.EqualTo("12"), where);
				Assert.That(Shown(readouts.WindSpeed), Is.True, where);
				Assert.That(Shown(readouts.Source), Is.False, where + ": a pack run is named by its date");
			}

			Assert.That(view.Panel.DayData.Indication, Is.EqualTo(CoverageIndication.None));
			Assert.That(view.Hud.DayData.Indication, Is.EqualTo(CoverageIndication.None));

			Assert.That(view.Panel.Date.text, Is.EqualTo("MON 15 JUN 2026"));
			Assert.That(Shown(view.Panel.Date), Is.True);
			Assert.That(view.Panel.Clock.text, Is.EqualTo("09:00:10"));
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("EDT"));
			Assert.That(view.Hud.Clock.text, Is.EqualTo("09:00:10"));
			Assert.That(view.Hud.ClockZone.text, Is.EqualTo("EDT"));
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.None));

			view.DistanceUnit = DistanceUnit.Metres;
			view.Refresh(StubTelemetrySource.Underway(), 0.1);
			Assert.That(view.Panel.EnvironmentReadouts.Tide.text, Is.EqualTo("0.3"));
			Assert.That(view.Panel.EnvironmentReadouts.TideUnit.text, Is.EqualTo("m"));
			Assert.That(view.Hud.EnvironmentReadouts.TideUnit.text, Is.EqualTo("m"));
		}

		[Test]
		public void APackRunInWinter_UsesItsOwnOffset()
		{
			var view = View(StubEnvironmentSource.With(Readouts.Pack(offsetHours: -5.0)));
			view.Refresh(StubTelemetrySource.Underway(), 0.0);
			Assert.That(view.Panel.Clock.text, Is.EqualTo("08:00:10"), "the pack's offset, not the DST rule for the telemetry's date");
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("EST"));
		}

		[Test]
		public void ACalmRun_ReadsInUtc_WithNoTideAndStillWaterAndAir()
		{
			var view = View(StubEnvironmentSource.With(Readouts.Calm()));
			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			var readouts = view.Panel.EnvironmentReadouts;
			Assert.That(readouts.Source.text, Is.EqualTo("CALM"));
			Assert.That(Shown(readouts.Source), Is.True);
			Assert.That(readouts.Tide.text, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(readouts.TideTrend.text, Is.EqualTo(string.Empty));
			Assert.That(readouts.TideArrow.Trend, Is.EqualTo(TideTrend.Unknown));
			Assert.That(readouts.Set.text, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(readouts.Drift.text, Is.EqualTo("0.0"));
			Assert.That(readouts.WindFrom.text, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(readouts.WindSpeed.text, Is.EqualTo("0"));
			Assert.That(view.Panel.Clock.text, Is.EqualTo("13:00:10"));
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("UTC"));
			Assert.That(view.Panel.Date.text, Is.EqualTo("MON 15 JUN 2026"));
			Assert.That(view.Hud.ClockZone.text, Is.EqualTo("UTC"));
		}

		[Test]
		public void AUniformRun_ShowsItsCurrentAndWind_InUtc()
		{
			var view = View(StubEnvironmentSource.With(Readouts.Uniform(Readouts.CurrentToward(1.5, 90.0), Readouts.WindFrom(15.0, 200.0))));
			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			var readouts = view.Panel.EnvironmentReadouts;
			Assert.That(readouts.Source.text, Is.EqualTo("UNIFORM"));
			Assert.That(readouts.Set.text, Is.EqualTo("090"));
			Assert.That(readouts.Drift.text, Is.EqualTo("1.5"));
			Assert.That(readouts.WindFrom.text, Is.EqualTo("200"));
			Assert.That(readouts.WindSpeed.text, Is.EqualTo("15"));
			Assert.That(readouts.Tide.text, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("UTC"));
		}

		[Test]
		public void ADayWithoutWindData_SaysSo_OnThePanelAndTheHud()
		{
			var view = View(StubEnvironmentSource.With(Readouts.Pack(windMissing: true)));
			view.Refresh(StubTelemetrySource.Underway(), 0.0);

			foreach (var readouts in new[] { view.Panel.EnvironmentReadouts, view.Hud.EnvironmentReadouts })
			{
				Assert.That(readouts.WindCaption.text, Is.EqualTo("WIND"));
				Assert.That(readouts.WindFrom.text, Is.EqualTo("NO WIND DATA"));
				Assert.That(Shown(readouts.WindSpeed), Is.False, "no speed beside it");
				Assert.That(Shown(readouts.WindFromUnit), Is.False);
			}
		}

		[Test]
		public void TheTideTrend_HasAnArrowAndAWord()
		{
			var environment = StubEnvironmentSource.With(Readouts.Pack(rateMetresPerSecond: -0.2 / 3600.0));
			var view = View(environment);
			view.Refresh(StubTelemetrySource.Underway(), 0.0);
			Assert.That(view.Panel.EnvironmentReadouts.TideTrend.text, Is.EqualTo("FALLING"));
			Assert.That(view.Panel.EnvironmentReadouts.TideArrow.Trend, Is.EqualTo(TideTrend.Falling));

			environment.Environment = Readouts.Pack(rateMetresPerSecond: 0.01 / 3600.0);
			view.Refresh(StubTelemetrySource.Underway(), 0.1);
			Assert.That(view.Panel.EnvironmentReadouts.TideTrend.text, Is.EqualTo("STEADY"));
			Assert.That(view.Panel.EnvironmentReadouts.TideArrow.Trend, Is.EqualTo(TideTrend.Steady));
		}

		[Test]
		public void CoverageWarnings_FollowTheCounts_LiveThenEarlier()
		{
			var environment = StubEnvironmentSource.With(Readouts.Pack());
			var view = View(environment);
			var telemetry = StubTelemetrySource.Underway(time: 10.0);
			view.Refresh(telemetry, 0.0);
			Assert.That(view.Panel.DayData.Indication, Is.EqualTo(CoverageIndication.None));
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.None));
			Assert.That(Shown(view.Panel.DayData), Is.False);
			Assert.That(Shown(view.Panel.Depth.Grid), Is.False);

			environment.Environment = Readouts.Pack(timeHolds: 3, depthMisses: 2);
			telemetry.Set(10.1, 12.0);
			view.Refresh(telemetry, 0.1);
			Assert.That(view.Panel.DayData.Indication, Is.EqualTo(CoverageIndication.Live));
			Assert.That(view.Panel.DayData.text, Is.EqualTo("OUTSIDE DAY'S DATA"));
			Assert.That(Shown(view.Panel.Date), Is.False, "the warning stands where the date was: the clock has run past the day");
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Live));
			Assert.That(view.Panel.Depth.Grid.text, Is.EqualTo("OFF DEPTH GRID"));
			Assert.That(view.Hud.DayData.Indication, Is.EqualTo(CoverageIndication.Live));
			Assert.That(view.Hud.DepthGrid.Indication, Is.EqualTo(CoverageIndication.Live));
			Assert.That(Shown(view.Panel.Depth.Grid), Is.True);

			// Back on the grid: the time holds keep counting, the depth misses stop.
			environment.Environment = Readouts.Pack(timeHolds: 300, depthMisses: 2);
			telemetry.Set(13.0, 12.0);
			view.Refresh(telemetry, 0.2);
			Assert.That(view.Panel.DayData.Indication, Is.EqualTo(CoverageIndication.Live));
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Earlier), "outlined once the misses stop");
			Assert.That(view.Hud.DepthGrid.Indication, Is.EqualTo(CoverageIndication.Earlier));
			Assert.That(Shown(view.Panel.Depth.Grid), Is.True, "still shown: this run used made-up depths");
		}

		[Test]
		public void ANewRun_StartsWithoutTheLastRunsWarnings()
		{
			var environment = StubEnvironmentSource.With(Readouts.Pack(depthMisses: 4));
			var view = View(environment);
			var telemetry = StubTelemetrySource.Underway(time: 10.0, runId: 7);
			view.Refresh(telemetry, 0.0);
			telemetry.Set(20.0, 12.0);
			view.Refresh(telemetry, 0.1);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Earlier), "the fixture ends run 7 with misses in its past");

			// Run 8 starts clean; its first miss is live, not "earlier".
			telemetry.RunId = 8;
			environment.Environment = Readouts.Pack(depthMisses: 0);
			telemetry.Set(0.01, 12.0);
			view.Refresh(telemetry, 0.2);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.None));
			environment.Environment = Readouts.Pack(depthMisses: 1);
			telemetry.Set(0.02, 12.0);
			view.Refresh(telemetry, 0.3);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Live));
		}

		[Test]
		public void ANewRunId_ResetsTheWarnings_EvenIfTheCountLooksTheSame()
		{
			var environment = StubEnvironmentSource.With(Readouts.Pack(depthMisses: 4));
			var view = View(environment);
			var telemetry = StubTelemetrySource.Underway(time: 10.0, runId: 1);
			view.Refresh(telemetry, 0.0);
			telemetry.Set(20.0, 12.0);
			view.Refresh(telemetry, 0.1);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Earlier));

			telemetry.RunId = 2;
			telemetry.Set(20.01, 12.0);
			view.Refresh(telemetry, 0.2);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Live), "a new run's misses are news, not history");
		}

		[Test]
		public void ReplacingTheEnvironmentSource_StartsOver()
		{
			var first = StubEnvironmentSource.With(Readouts.Pack(depthMisses: 4));
			var view = View(first);
			var telemetry = StubTelemetrySource.Underway(time: 10.0);
			view.Refresh(telemetry, 0.0);
			telemetry.Set(20.0, 12.0);
			view.Refresh(telemetry, 0.1);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Earlier));

			view.EnvironmentSource = StubEnvironmentSource.With(Readouts.Pack(depthMisses: 4));
			view.Refresh(telemetry, 0.2);
			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Live));
		}

		[Test]
		public void WithoutTelemetry_TheEnvironmentStillFollowsItsOwnSource()
		{
			// The bridge sets both flags together; if a source ever reports one without the other, each part shows
			// what its own source says rather than mixing them.
			var view = View(StubEnvironmentSource.With(Readouts.Pack()));
			view.Refresh(StubTelemetrySource.NeverStarted(), 0.0);
			Assert.That(view.Panel.ShowingNoData, Is.True);
			Assert.That(view.Panel.EnvironmentReadouts.Set.text, Is.EqualTo("047"));
			Assert.That(view.Panel.Clock.text, Is.EqualTo(InstrumentFormat.NoValue), "no telemetry, no clock");
			Assert.That(Shown(view.Panel.Date), Is.False);
		}

		private static void AssertNoEnvironment(WheelhouseView view, string context)
		{
			foreach (var (readouts, where) in new[] { (view.Panel.EnvironmentReadouts, "panel"), (view.Hud.EnvironmentReadouts, "HUD") })
			{
				foreach (var label in new[] { readouts.Tide, readouts.Set, readouts.Drift, readouts.WindFrom, readouts.WindSpeed })
				{
					Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{context}: {where} {label.name}");
				}

				Assert.That(readouts.WindCaption.text, Is.EqualTo("WIND FROM"), $"{context}: {where} wind caption");
				Assert.That(readouts.TideTrend.text, Is.EqualTo(string.Empty), $"{context}: {where} trend");
				Assert.That(readouts.TideArrow.Trend, Is.EqualTo(TideTrend.Unknown), $"{context}: {where} trend arrow");
				Assert.That(Shown(readouts.Source), Is.False, $"{context}: {where} source");
			}

			foreach (var (tag, where) in new[] { (view.Panel.DayData, "panel"), (view.Hud.DayData, "HUD") })
			{
				Assert.That(tag.Indication, Is.EqualTo(CoverageIndication.None), $"{context}: {where} day data warning");
				Assert.That(Shown(tag), Is.False, $"{context}: {where} day data warning");
			}

			Assert.That(view.Panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.None), context + ": depth grid warning");
			Assert.That(Shown(view.Panel.Depth.Grid), Is.False, context + ": depth grid warning");
			Assert.That(view.Hud.DepthGrid.Indication, Is.EqualTo(CoverageIndication.None), context + ": HUD depth grid warning");
		}
	}
}
