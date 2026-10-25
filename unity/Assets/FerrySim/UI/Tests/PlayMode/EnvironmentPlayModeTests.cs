using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The rendered panel and HUD against an environment readout source in the scene, connected through
	/// <see cref="WheelhouseUI.ConnectScenario"/>. Guards against: readouts that do not follow the source frame by frame;
	/// a new run (a new <c>RunId</c>) keeping the last run's coverage warnings; a destroyed source, or none, showing
	/// anything but "---"; and the new strip or HUD row spilling out of the panel or moving between 720p and 1440p.
	/// Screenshots go to artifacts/unity/ui-env-*.png.
	/// </summary>
	public class EnvironmentPlayModeTests
	{
		private UITestRig _rig;
		private GameObject _environmentHost;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
			if (_environmentHost != null)
			{
				Object.Destroy(_environmentHost);
			}
		}

		private StubEnvironmentRunner Environment()
		{
			_environmentHost = new GameObject("environment readout (test)");
			return _environmentHost.AddComponent<StubEnvironmentRunner>();
		}

		private static bool Shown(VisualElement element) => element.resolvedStyle.display != DisplayStyle.None;

		[UnityTest]
		public IEnumerator Readouts_FollowTheSource_FrameByFrame_OnThePanelAndTheHud()
		{
			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			var environment = Environment();
			environment.HasEnvironment = true;
			_rig.UI.ConnectScenario(environment, null, null);
			_rig.View.HudVisible = true;
			var frames = new[]
			{
				(TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0), "3.0", "RISING", "047", "1.8", "250", "12"),
				(TestReadouts.Pack(-0.4, -0.2, 2.6, 215.0, 18.0, 20.0), "-1.3", "FALLING", "215", "2.6", "020", "18"),
				(TestReadouts.Pack(0.05, 0.01, 0.0, 0.0, 0.2, 0.0), "0.2", "STEADY", InstrumentFormat.NoValue, "0.0", InstrumentFormat.NoValue, "0"),
			};
			foreach (var (readout, tide, trend, set, drift, windFrom, windSpeed) in frames)
			{
				environment.Environment = readout;
				yield return null;
				yield return null;
				foreach (var (readouts, where) in new[] { (_rig.View.Panel.EnvironmentReadouts, "panel"), (_rig.View.Hud.EnvironmentReadouts, "HUD") })
				{
					Assert.That(readouts.Tide.text, Is.EqualTo(tide), where);
					Assert.That(readouts.TideTrend.text, Is.EqualTo(trend), where);
					Assert.That(readouts.Set.text, Is.EqualTo(set), where);
					Assert.That(readouts.Drift.text, Is.EqualTo(drift), where);
					Assert.That(readouts.WindFrom.text, Is.EqualTo(windFrom), where);
					Assert.That(readouts.WindSpeed.text, Is.EqualTo(windSpeed), where);
				}
			}

			// The sample's clock reads in the pack's local time, with the date; the CLI sample has no recorded start, so it is still marked.
			Assert.That(_rig.View.Panel.ClockZone.text, Is.EqualTo("EDT"));
			Assert.That(_rig.View.Panel.Date.text, Is.EqualTo("MON 15 JUN 2026"));
			Assert.That(Shown(_rig.View.Panel.Date), Is.True);
			Assert.That(_rig.View.Hud.Clock.text, Is.EqualTo(_rig.View.Panel.Clock.text));
			Assert.That(_rig.View.Hud.ClockZone.text, Is.EqualTo("EDT"));
		}

		[UnityTest]
		public IEnumerator PackRun_InDayAndNightPalettes_At720pAnd1440p_WithWarnings()
		{
			foreach (var (width, height, resolution) in new[] { (1280, 720, "720p"), (2560, 1440, "1440p") })
			{
				_rig?.Dispose();
				_rig = new UITestRig(width, height);
				Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
				// A chosen start, as a picker or the bridge gives it: the clock is not marked ASSUMED START.
				_rig.Source.StartScenario(new System.DateTimeOffset(2025, 7, 16, 13, 0, 0, System.TimeSpan.Zero));
				_rig.Source.IsPlaying = false;
				_rig.Source.Seek(46.0);
				if (_environmentHost != null)
				{
					Object.Destroy(_environmentHost);
				}

				var environment = Environment();
				environment.HasEnvironment = true;
				environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0);
				_rig.UI.ConnectScenario(environment, null, null);
				_rig.View.HudVisible = true;
				yield return UITestRig.Frames();
				foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
				{
					_rig.SetPalette(palette);
					yield return UITestRig.Frames();
					AssertInsideThePanel(_rig.View, resolution);
					UnityEngine.Object.Destroy(_rig.Screenshot($"ui-env-{palette.ToString().ToLowerInvariant()}-{resolution}.png"));
				}

				// The day's data has run out, the depth is off the grid now, and the day has no wind.
				environment.Environment = TestReadouts.Pack(0.9, 0.0, 1.8, 47.0, 0.0, 0.0, windMissing: true, timeHolds: 500, depthMisses: 40);
				yield return UITestRig.Frames();
				var panel = _rig.View.Panel;
				Assert.That(panel.DayData.Indication, Is.EqualTo(CoverageIndication.Live));
				Assert.That(panel.Depth.Grid.Indication, Is.EqualTo(CoverageIndication.Live));
				Assert.That(_rig.View.Hud.DepthGrid.Indication, Is.EqualTo(CoverageIndication.Live));
				Assert.That(panel.EnvironmentReadouts.WindFrom.text, Is.EqualTo(EnvironmentFormat.NoWindData));
				foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
				{
					_rig.SetPalette(palette);
					yield return UITestRig.Frames();
					AssertInsideThePanel(_rig.View, resolution + " with warnings");
					UnityEngine.Object.Destroy(_rig.Screenshot($"ui-env-warnings-{palette.ToString().ToLowerInvariant()}-{resolution}.png"));
				}
			}
		}

		[UnityTest]
		public IEnumerator Layout_TheStripAndTheHudRow_DoNotMoveBetween720pAnd1440p()
		{
			// The groups of the water and the air have fixed widths, so those widths must match to half a pixel. Positions,
			// and the widths of labels sized by their text, are allowed TextTolerance: the font measures each label up to
			// half a pixel differently at twice the size, and that adds up along the strip (the existing clock moves a
			// pixel here too). A real layout move, such as a wrap or an overflow, is far larger.
			const float TextTolerance = 2f;
			Rect[] first = null;
			foreach (var (width, height) in new[] { (1280, 720), (2560, 1440) })
			{
				_rig?.Dispose();
				_rig = new UITestRig(width, height);
				_rig.Source.TryLoadCsv(UITestRig.SamplePath);
				_rig.Source.StartScenario(new System.DateTimeOffset(2025, 7, 16, 13, 0, 0, System.TimeSpan.Zero));
				_rig.Source.IsPlaying = false;
				_rig.Source.Seek(46.0);
				if (_environmentHost != null)
				{
					Object.Destroy(_environmentHost);
				}

				var environment = Environment();
				environment.HasEnvironment = true;
				environment.Environment = TestReadouts.Pack(-0.4, -0.2, 2.6, 215.0, 18.0, 20.0, depthMisses: 3);
				_rig.UI.ConnectScenario(environment, null, null);
				_rig.View.HudVisible = true;
				yield return UITestRig.Frames();

				var panel = _rig.View.Panel;
				var hud = _rig.View.Hud;
				var parts = new (VisualElement element, bool fixedBox)[]
				{
					(panel.EnvironmentReadouts, true), (hud.EnvironmentReadouts, true),
					(panel.EnvironmentReadouts.Tide, true), (panel.EnvironmentReadouts.Set, true), (panel.EnvironmentReadouts.WindSpeed, true),
					(hud.EnvironmentReadouts.Tide, true), (hud.EnvironmentReadouts.WindFrom, true),
					(panel.Date, false), (panel.Clock, false), (panel.Depth.Grid, false), (hud.Clock, false), (hud.DepthGrid, false),
				};
				var bounds = new Rect[parts.Length];
				for (var i = 0; i < parts.Length; i++)
				{
					bounds[i] = parts[i].element.worldBound;
					Assert.That(bounds[i].width, Is.GreaterThan(4f), parts[i].element.name + " collapsed");
				}

				Assert.That(panel.EnvironmentReadouts.worldBound.width, Is.EqualTo(628f).Within(0.5f), "three fixed groups: 230 + 16 + 186 + 16 + 180");
				Assert.That(hud.EnvironmentReadouts.worldBound.width, Is.EqualTo(432f).Within(0.5f), "the HUD's two rows are 432 wide");
				Assert.That(hud.EnvironmentReadouts.WindFrom.worldBound.yMin, Is.GreaterThan(hud.EnvironmentReadouts.Tide.worldBound.yMax - 0.5f),
					"the HUD puts the wind on its second row");
				if (first == null)
				{
					first = bounds;
				}
				else
				{
					for (var i = 0; i < bounds.Length; i++)
					{
						var tolerance = parts[i].fixedBox ? 0.5f : TextTolerance;
						var name = parts[i].element.name;
						Assert.That(bounds[i].x, Is.EqualTo(first[i].x).Within(TextTolerance), name + " moved between resolutions");
						Assert.That(bounds[i].y, Is.EqualTo(first[i].y).Within(TextTolerance), name + " moved between resolutions");
						Assert.That(bounds[i].width, Is.EqualTo(first[i].width).Within(tolerance), name + " resized between resolutions");
					}
				}
			}
		}

		[UnityTest]
		public IEnumerator ARunChange_ResetsTheCoverageWarnings()
		{
			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			var environment = Environment();
			environment.HasEnvironment = true;
			environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0, depthMisses: 12);
			_rig.UI.ConnectScenario(environment, null, null);
			_rig.Source.Seek(40.0);
			yield return UITestRig.Frames();
			var grid = _rig.View.Panel.Depth.Grid;
			Assert.That(grid.Indication, Is.EqualTo(CoverageIndication.Live));
			_rig.Source.Seek(45.0);
			yield return UITestRig.Frames();
			Assert.That(grid.Indication, Is.EqualTo(CoverageIndication.Earlier), "five seconds without a new miss");

			// Seeking back replays from there: a new run, whose source starts its counts again.
			var runId = _rig.Source.RunId;
			_rig.Source.Seek(10.0);
			Assert.That(_rig.Source.RunId, Is.Not.EqualTo(runId));
			environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0, depthMisses: 0);
			yield return UITestRig.Frames();
			Assert.That(grid.Indication, Is.EqualTo(CoverageIndication.None), "the new run has no misses");
			Assert.That(Shown(grid), Is.False);
		}

		[UnityTest]
		public IEnumerator WithNoSource_OrADestroyedOne_EveryReadoutSaysNoData()
		{
			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			_rig.View.HudVisible = true;
			yield return UITestRig.Frames();
			AssertNoData(_rig.View, "no source");
			UnityEngine.Object.Destroy(_rig.Screenshot("ui-env-nodata-day.png"));

			var environment = Environment();
			environment.HasEnvironment = true;
			environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0, windMissing: true, timeHolds: 3, depthMisses: 3);
			_rig.UI.ConnectScenario(environment, null, null);
			yield return UITestRig.Frames();
			Assert.That(_rig.View.Panel.EnvironmentReadouts.WindFrom.text, Is.EqualTo(EnvironmentFormat.NoWindData));

			Object.Destroy(_environmentHost);
			yield return UITestRig.Frames();
			AssertNoData(_rig.View, "destroyed source");
			LogAssert.NoUnexpectedReceived();
		}

		private static void AssertNoData(WheelhouseView view, string context)
		{
			foreach (var readouts in new[] { view.Panel.EnvironmentReadouts, view.Hud.EnvironmentReadouts })
			{
				foreach (var label in new[] { readouts.Tide, readouts.Set, readouts.Drift, readouts.WindFrom, readouts.WindSpeed })
				{
					Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{context}: {label.name}");
				}

			}

			Assert.That(Shown(view.Panel.DayData), Is.False, context + ": day data warning");
			Assert.That(Shown(view.Hud.DayData), Is.False, context + ": HUD day data warning");

			Assert.That(Shown(view.Panel.Depth.Grid), Is.False, context + ": depth grid warning");
			Assert.That(Shown(view.Panel.Date), Is.False, context + ": no date without an environment");
			Assert.That(view.Panel.ClockZone.text, Is.EqualTo("EDT"), context + ": New York time from the telemetry, as before");
		}

		/// <summary>The strip's readouts sit inside the panel's top strip, clear of the vessel name and the clock; the HUD row inside the HUD.</summary>
		private static void AssertInsideThePanel(WheelhouseView view, string context)
		{
			var panel = view.Panel;
			var strip = panel.EnvironmentReadouts.worldBound;
			Assert.That(Contains(panel.worldBound, strip), Is.True, $"{context}: the strip {strip} spills out of the panel {panel.worldBound}");
			Assert.That(strip.xMin, Is.GreaterThanOrEqualTo(panel.VesselName.worldBound.xMax), context + ": overlaps the vessel name");
			// The date, or the warning standing in its place, sits between the strip and the clock.
			var beside = Shown(panel.DayData) ? panel.DayData.worldBound : panel.Date.worldBound;
			Assert.That(strip.xMax, Is.LessThanOrEqualTo(beside.xMin + 0.5f), $"{context}: the strip {strip} overlaps {beside}");
			Assert.That(beside.xMax, Is.LessThanOrEqualTo(panel.Clock.worldBound.xMin + 0.5f), context + ": the date or its warning overlaps the clock");
			Assert.That(strip.yMax, Is.LessThanOrEqualTo(panel.Port.worldBound.yMin + 0.5f), context + ": the strip reaches into the console");
			var grid = panel.Depth.Grid.worldBound;
			Assert.That(Contains(panel.Depth.worldBound, grid), Is.True, $"{context}: the grid tag {grid} spills out of the depth block {panel.Depth.worldBound}");
			var hud = view.Hud.worldBound;
			Assert.That(Contains(hud, view.Hud.EnvironmentReadouts.worldBound), Is.True, context + ": the HUD rows spill out of the HUD");
			foreach (var tag in new[] { view.Hud.DayData, view.Hud.DepthGrid })
			{
				if (Shown(tag))
				{
					Assert.That(Contains(hud, tag.worldBound), Is.True, $"{context}: {tag.name} spills out of the HUD");
				}
			}

			Assert.That(Contains(hud, view.Hud.Clock.worldBound), Is.True, context + ": the HUD clock spills out of the HUD");
			Assert.That(hud.yMax, Is.LessThanOrEqualTo(panel.worldBound.yMin + 0.5f), context + ": the HUD reaches into the panel");
			Assert.That(hud.xMin, Is.GreaterThanOrEqualTo(-0.5f), context + ": the HUD is wider than the screen");
			Assert.That(hud.xMax, Is.LessThanOrEqualTo(view.worldBound.xMax + 0.5f), context + ": the HUD is wider than the screen");
		}

		private static bool Contains(Rect outer, Rect inner) =>
			inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;
	}
}
