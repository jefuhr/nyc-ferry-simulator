using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The rendered panel and HUD against the telemetry contract's lifecycle (review findings 1 and 2):
	/// a fender touch that lasts one recorded row and falls between two rendered frames, a run that has
	/// ended, and a runner in the scene whose initialization failed. Screenshots go to artifacts/unity/:
	/// ui-panel-{day,night}-contact-latch.png and ui-nodata-{day,night}.png.
	/// </summary>
	public class LiveTelemetryPlayModeTests
	{
		private UITestRig _rig;
		private GameObject _runnerHost;
		private GameObject _uiHost;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
			if (_uiHost != null)
			{
				Object.Destroy(_uiHost);
			}

			if (_runnerHost != null)
			{
				Object.Destroy(_runnerHost);
			}
		}

		[UnityTest]
		public IEnumerator AContactBetweenTwoFrames_IsLatchedOnThePanel_InDayAndNightScreenshots()
		{
			_rig = new UITestRig(1280, 720);
			var recording = SyntheticRuns.CrashStopWithOneRowContact(TelemetryCsv.Load(UITestRig.SamplePath), 50.0, 28000.0);
			_rig.Source.Load(recording, VesselFiles.TryLoad(recording.VesselId, out _));
			var panel = _rig.View.Panel;
			_rig.Source.Seek(49.8);
			yield return UITestRig.Frames();
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"));

			// Between two rendered frames playback passes three rows; only the middle one (50.0 s) touches.
			_rig.Source.Advance(0.35);
			Assert.That(_rig.Source.State.Time, Is.EqualTo(50.1).Within(1e-9));
			Assert.That(_rig.Source.Telemetry.ContactForce, Is.EqualTo(0.0), "the row on screen has no contact");
			yield return UITestRig.Frames(2);

			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent), "the touch is shown, as finished");
			Assert.That(panel.Contact.Value.text, Is.EqualTo("PEAK 28 kN"));
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();
				Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent), "held while paused");
				var image = _rig.Screenshot($"ui-panel-{palette.ToString().ToLowerInvariant()}-contact-latch.png");
				Object.Destroy(image);
			}

			_rig.Source.Seek(53.5);
			yield return UITestRig.Frames();
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"), "the hold ends 3 s after the touch");
		}

		[UnityTest]
		public IEnumerator WhenTheRunEnds_PanelAndHudShowNoData_InDayAndNightScreenshots()
		{
			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			var view = _rig.View;
			view.HudVisible = true;
			_rig.Source.Seek(50.0);
			yield return UITestRig.Frames();
			Assert.That(view.Panel.ShowingNoData, Is.False);
			Assert.That(view.Hud.Speed.text, Is.Not.EqualTo(InstrumentFormat.NoValue));

			// Play off the end without looping: the run is over, and its last row is no longer current.
			_rig.Source.Loop = false;
			_rig.Source.Advance(100.0);
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();

				var panel = view.Panel;
				Assert.That(panel.ShowingNoData, Is.True, $"{palette}: NO DATA badge");
				foreach (var label in new[] { panel.Heading, panel.SpeedOverGround, panel.Depth.Value, panel.Port.ShaftRpm, panel.Clock, panel.Contact.Value })
				{
					Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{palette}: {label.name}");
				}

				Assert.That(panel.Starboard.Tachometer.NeedleVisible, Is.False, $"{palette}: no needle without data");
				foreach (var label in new[] { view.Hud.Speed, view.Hud.Heading, view.Hud.PortGear, view.Hud.RudderText })
				{
					Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{palette}: HUD {label.name}");
				}

				var image = _rig.Screenshot($"ui-nodata-{palette.ToString().ToLowerInvariant()}.png");
				Object.Destroy(image);
			}
		}

		[UnityTest]
		public IEnumerator AFailedRunnerFoundInTheScene_ShowsNoData_WithoutErrors()
		{
			_runnerHost = new GameObject("failed runner (test)");
			var runner = _runnerHost.AddComponent<StubRunner>();
			_uiHost = new GameObject("wheelhouse UI (test)");
			var ui = _uiHost.AddComponent<WheelhouseUI>();
			yield return UITestRig.Frames();
			Assert.That(ui.View, Is.Not.Null, "the view is built once the document has a panel");
			ui.View.HudVisible = true;
			yield return UITestRig.Frames();

			Assert.That(ui.Source, Is.SameAs(runner), "the UI found the runner by itself");
			var panel = ui.View.Panel;
			Assert.That(panel.ShowingNoData, Is.True);
			Assert.That(panel.SpeedOverGround.text, Is.EqualTo(InstrumentFormat.NoValue), "not the runner's default 0.0");
			Assert.That(panel.Port.Gear.Text, Is.EqualTo(InstrumentFormat.NoValue), "not its default NEUTRAL");
			Assert.That(panel.Clock.text, Is.EqualTo(InstrumentFormat.NoValue), "an unset scenario start is never formatted");
			Assert.That(panel.Depth.AlarmLit, Is.False, "no alarm from its default 0 m depth");
			Assert.That(ui.View.Hud.Speed.text, Is.EqualTo(InstrumentFormat.NoValue));
			LogAssert.NoUnexpectedReceived();
		}
	}
}
