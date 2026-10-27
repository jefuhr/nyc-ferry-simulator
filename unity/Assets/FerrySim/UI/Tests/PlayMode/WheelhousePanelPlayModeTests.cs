using System;
using System.Collections;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Renders the real panel, driven by the fake source, at three moments of the recorded T-class crash
	/// stop (underway ahead, shifting, astern) in the day and night palettes, and checks every readout
	/// against the CSV text within the readout's display rounding. Screenshots go to
	/// artifacts/unity/ui-*.png. Needs graphics: run without -nographics.
	/// </summary>
	public class WheelhousePanelPlayModeTests
	{
		private UITestRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator Panel_ShowsTheCrashStopAtThreeMoments_InDayAndNightPalettes()
		{
			_rig = new UITestRig(1280, 720);
			var csv = new RawCsv(File.ReadAllText(UITestRig.SamplePath));
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			var firstShifting = csv.FirstRow("port_gear", "shifting");
			var firstAstern = csv.FirstRow("port_gear", "astern");
			Assert.That(firstShifting, Is.GreaterThan(20), "the fixture should start underway ahead");
			Assert.That(firstAstern, Is.GreaterThan(firstShifting));
			var moments = new[]
			{
				("ahead", firstShifting - 20, "AHEAD"),
				("shifting", (firstShifting + firstAstern) / 2, "SHIFTING"),
				("astern", firstAstern + 20, "ASTERN"),
			};

			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				foreach (var (moment, row, gear) in moments)
				{
					_rig.Source.Seek(csv.Number(row, "time_s"));
					yield return UITestRig.Frames();

					Assert.That(csv.Text(row, "port_gear").ToUpperInvariant(), Is.EqualTo(gear), $"fixture row {row} is not {moment}");
					AssertPanelMatchesRow(_rig.View.Panel, csv, row, $"{palette} {moment}");
					var image = _rig.Screenshot($"ui-panel-{palette.ToString().ToLowerInvariant()}-{moment}.png");
					AssertPanelPixels(image, palette);
					UnityEngine.Object.Destroy(image);
				}
			}
		}

		[UnityTest]
		public IEnumerator Panel_ShowsATurnRunThroughTheDynamicsModel()
		{
			// A hard-over turn run through the real core model inside the test (no CLI files needed), saved
			// with TelemetryCsvWriter and read back as text, so the turn also checks the writer.
			var recording = TurnRuns.HardOverToStarboard();
			var text = TelemetryCsvWriter.Render(recording);
			var path = Path.Combine(UIRepoPaths.Root, "artifacts", "unity", "ui-test-turn.csv");
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, text);
			var csv = new RawCsv(text);
			var row = csv.Count - 1;
			Assert.That(csv.Number(row, "helm"), Is.EqualTo(1.0), "the run ends with the helm hard over to starboard");
			Assert.That(csv.Number(row, "port_rudder_deg"), Is.EqualTo(DisplayUnits.Degrees(VesselFiles.TryLoad("t-class", out _).Controls.HardOverAngle.Value)).Within(1e-4), "the rudders have reached the vessel hard-over parameter");

			_rig = new UITestRig(1280, 720);
			Assert.That(_rig.Source.TryLoadCsv(path), Is.True, _rig.Source.LoadError);
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				_rig.Source.Seek(csv.Number(row, "time_s"));
				yield return UITestRig.Frames();

				var panel = _rig.View.Panel;
				AssertPanelMatchesRow(panel, csv, row, $"{palette} turning");
				var drift = ((csv.Number(row, "cog_deg") - csv.Number(row, "heading_deg")) % 360.0 + 540.0) % 360.0 - 180.0;
				Assert.That(Math.Abs(drift), Is.GreaterThan(1.0), "the turn has a drift angle worth seeing");
				Assert.That(panel.Compass.CourseDegrees, Is.EqualTo(csv.Number(row, "cog_deg")).Within(1e-3), "COG vector shown");
				var image = _rig.Screenshot($"ui-panel-{palette.ToString().ToLowerInvariant()}-turning.png");
				UnityEngine.Object.Destroy(image);
			}
		}

		[UnityTest]
		public IEnumerator RudderScale_CapturesBothCrewReportedStops()
		{
			var folder = Path.Combine(UIRepoPaths.Root, "artifacts/crew-fixes");
			Directory.CreateDirectory(folder);
			foreach (var helm in new[] { -1.0, 1.0 })
			{
				var path = Path.Combine(folder, helm < 0 ? "rudder-port.csv" : "rudder-starboard.csv");
				File.WriteAllText(path, TelemetryCsvWriter.Render(TurnRuns.HardOverToStarboard(helm)));
				_rig = new UITestRig(1280, 720);
				Assert.That(_rig.Source.TryLoadCsv(path), Is.True, _rig.Source.LoadError);
				_rig.Source.Seek(80);
				yield return UITestRig.Frames();
				Assert.That(_rig.View.Panel.Rudder.PortDegrees, Is.EqualTo(helm * 20).Within(.001));
				var image = _rig.Capture();
				File.WriteAllBytes(Path.Combine(folder, helm < 0 ? "panel-minus20.png" : "panel-plus20.png"), image.EncodeToPNG());
				UnityEngine.Object.Destroy(image);
				_rig.Dispose(); _rig = null;
			}
		}

		[UnityTest]
		public IEnumerator Hud_ShowsSpeedHeadingLeversAndRudder_AndToggles()
		{
			_rig = new UITestRig(1280, 720);
			var csv = new RawCsv(File.ReadAllText(UITestRig.SamplePath));
			_rig.Source.TryLoadCsv(UITestRig.SamplePath);
			var row = (csv.FirstRow("port_gear", "shifting") + csv.FirstRow("port_gear", "astern")) / 2;
			_rig.Source.Seek(csv.Number(row, "time_s"));
			var view = _rig.View;
			Assert.That(view.HudVisible, Is.False, "the HUD starts hidden");

			view.PanelVisible = false;
			view.HudVisible = true;
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();

				var hud = view.Hud;
				AssertNumber(hud.Speed.text, csv.Number(row, "sog_kn"), 0.05, "HUD SOG");
				AssertCompass(hud.Heading.text, csv.Number(row, "heading_deg"), "HUD heading");
				Assert.That(hud.PortLever.Lever, Is.EqualTo(csv.Number(row, "port_lever")).Within(1e-9));
				Assert.That(hud.StarboardLever.Lever, Is.EqualTo(csv.Number(row, "starboard_lever")).Within(1e-9));
				Assert.That(hud.PortGear.text, Is.EqualTo("SHIFT"));
				Assert.That(hud.Rudder.PortDegrees, Is.EqualTo(csv.Number(row, "port_rudder_deg")).Within(1e-6));
				Assert.That(hud.RudderText.text, Is.EqualTo(ExpectedRudder(csv.Number(row, "port_rudder_deg"))));
				Assert.That(hud.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
				Assert.That(view.Panel.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
				var image = _rig.Screenshot($"ui-hud-{palette.ToString().ToLowerInvariant()}.png");
				UnityEngine.Object.Destroy(image);
			}

			view.HudVisible = false;
			yield return UITestRig.Frames(2);
			Assert.That(view.Hud.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
		}

		[UnityTest]
		public IEnumerator Layout_ScalesFrom720pTo1440pWithoutMovingAnything()
		{
			var csv = new RawCsv(File.ReadAllText(UITestRig.SamplePath));
			var row = csv.FirstRow("port_gear", "shifting") + 5;
			var bounds720 = new Rect[0];
			foreach (var (width, height) in new[] { (1280, 720), (2560, 1440) })
			{
				_rig?.Dispose();
				_rig = new UITestRig(width, height);
				_rig.Source.TryLoadCsv(UITestRig.SamplePath);
				_rig.Source.Seek(csv.Number(row, "time_s"));
				yield return UITestRig.Frames();

				var panel = _rig.View.Panel;
				var root = panel.panel.visualTree;
				Assert.That(root.layout.width, Is.EqualTo(1280f).Within(0.5f), $"{width}x{height}: panel space is the 1280x720 design");
				Assert.That(root.layout.height, Is.EqualTo(720f).Within(0.5f));
				Assert.That(panel.layout.height, Is.EqualTo(300f).Within(0.5f));
				var parts = new VisualElement[]
				{
					panel.Port, panel.Starboard, panel.Compass, panel.Rudder, panel.Depth, panel.Contact,
					panel.Port.Tachometer, panel.Starboard.Tachometer, panel.Port.Gear, panel.Starboard.Gear,
					panel.Heading, panel.SpeedOverGround, panel.CourseOverGround, panel.SpeedThroughWater, panel.Clock,
				};
				var bounds = new Rect[parts.Length];
				for (var i = 0; i < parts.Length; i++)
				{
					bounds[i] = parts[i].worldBound;
					Assert.That(bounds[i].width, Is.GreaterThan(4f), parts[i].name + " collapsed");
					Assert.That(Contains(panel.worldBound, bounds[i]), Is.True, $"{parts[i].name} {bounds[i]} spills out of the panel {panel.worldBound}");
				}

				Assert.That(Overlaps(panel.Port.worldBound, panel.Starboard.worldBound), Is.False);
				Assert.That(panel.Port.worldBound.xMax, Is.LessThanOrEqualTo(panel.Compass.worldBound.xMin), "port column sits left of the centre");
				Assert.That(panel.Starboard.worldBound.xMin, Is.GreaterThanOrEqualTo(panel.Rudder.worldBound.xMax), "starboard column sits right of the centre");
				if (bounds720.Length == 0)
				{
					bounds720 = bounds;
				}
				else
				{
					for (var i = 0; i < bounds.Length; i++)
					{
						Assert.That(bounds[i].x, Is.EqualTo(bounds720[i].x).Within(0.5f), parts[i].name + " moved between resolutions");
						Assert.That(bounds[i].width, Is.EqualTo(bounds720[i].width).Within(0.5f), parts[i].name + " resized between resolutions");
					}
				}

				var image = _rig.Screenshot(width == 1280 ? "ui-panel-day-720p.png" : "ui-panel-day-1440p.png");
				AssertPanelPixels(image, PaletteKind.Day);
				UnityEngine.Object.Destroy(image);
			}
		}

		[UnityTest]
		public IEnumerator DepthAlarmFlashesUntilAcknowledged_AndContactShowsItsForce()
		{
			_rig = new UITestRig(1280, 720);
			var recording = SyntheticRuns.CrashStopWithShallowsAndContact(TelemetryCsv.Load(UITestRig.SamplePath), 50.0, 58.0, 52.0, 52.6);
			_rig.Source.Load(recording, VesselFiles.TryLoad(recording.VesselId, out _));
			var panel = _rig.View.Panel;

			_rig.Source.Seek(49.0);
			yield return UITestRig.Frames();
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Normal));
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.None));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"));

			_rig.Source.Seek(52.3);
			yield return UITestRig.Frames();
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active));
			AssertNumber(panel.Depth.Value.text, 1.2 / 0.3048, 0.05, "shallow depth in feet");
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Live));
			StringAssert.EndsWith(" kN", panel.Contact.Value.text);

			// Flashing follows real time, so watch it for longer than one period and require both phases.
			var sawLit = false;
			var sawDark = false;
			var deadline = Time.realtimeSinceStartup + 2.5f;
			while (Time.realtimeSinceStartup < deadline && !(sawLit && sawDark))
			{
				yield return null;
				sawLit |= panel.Depth.AlarmLit;
				sawDark |= !panel.Depth.AlarmLit;
			}

			Assert.That(sawLit && sawDark, Is.True, "an unacknowledged alarm flashes");
			_rig.View.AcknowledgeAlarms();
			yield return UITestRig.Frames();
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged));
			for (var i = 0; i < 40; i++)
			{
				yield return null;
				Assert.That(panel.Depth.AlarmLit, Is.True, "an acknowledged alarm is steady");
			}

			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();
				var image = _rig.Screenshot($"ui-panel-{palette.ToString().ToLowerInvariant()}-alarm-contact.png");
				UnityEngine.Object.Destroy(image);
			}

			_rig.Source.Seek(53.5);
			yield return UITestRig.Frames();
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Recent), "a finished contact stays visible briefly");
			StringAssert.StartsWith("PEAK ", panel.Contact.Value.text);
		}

		[UnityTest]
		public IEnumerator EveryLabel_TakesItsColorFromThePalette_InBothPalettes()
		{
			// Guards against text left in the theme's default color (dark on a dark face, or bright at
			// night), for example labels rebuilt after a palette was applied.
			_rig = new UITestRig(1280, 720);
			_rig.Source.TryLoadCsv(UITestRig.SamplePath);
			_rig.Source.Seek(46.0);
			_rig.View.HudVisible = true;
			_rig.View.Debrief.TryLoad(UIRepoPaths.SampleCrashStopCsv);
			yield return UITestRig.Frames();

			// First pass: the palette applied when the view was built, before any re-application (the
			// original bug), then each palette applied explicitly.
			foreach (var (palette, apply) in new[] { (PaletteKind.Day, false), (PaletteKind.Night, true), (PaletteKind.Day, true) })
			{
				if (apply)
				{
					_rig.SetPalette(palette);
					yield return UITestRig.Frames();
				}

				var colors = new System.Collections.Generic.List<Color>();
				foreach (var property in typeof(InstrumentPalette).GetProperties())
				{
					if (property.PropertyType == typeof(Color))
					{
						colors.Add((Color)property.GetValue(InstrumentPalette.For(palette)));
					}
				}

				var checkedLabels = 0;
				_rig.View.Query<Label>().ForEach(label =>
				{
					if (string.IsNullOrEmpty(label.text) || label.GetFirstAncestorOfType<TextField>() != null)
					{
						return;
					}

					checkedLabels++;
					var color = label.resolvedStyle.color;
					var matches = colors.Exists(c => ColourDistance(c, color) < 0.002f);
					Assert.That(matches, Is.True, $"{palette}: label '{label.text}' ({label.name}) has color {color}, which is not in the palette");
				});
				Assert.That(checkedLabels, Is.GreaterThan(100), "should have checked the whole view");
			}
		}

		[UnityTest]
		public IEnumerator WithoutTelemetry_EveryReadoutSaysSoInsteadOfShowingZeros()
		{
			_rig = new UITestRig(1280, 720, withSource: false);
			yield return UITestRig.Frames();

			var panel = _rig.View.Panel;
			Assert.That(panel.ShowingNoData, Is.True);
			foreach (var label in new[] { panel.Heading, panel.SpeedOverGround, panel.CourseOverGround, panel.SpeedThroughWater, panel.Depth.Value, panel.Port.ShaftRpm, panel.Clock })
			{
				Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), label.name);
			}

			Assert.That(panel.Port.Tachometer.ReadoutText, Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(panel.Port.Gear.Text, Is.EqualTo(InstrumentFormat.NoValue));
			var image = _rig.Screenshot("ui-panel-day-nodata.png");
			UnityEngine.Object.Destroy(image);
		}

		[UnityTest]
		public IEnumerator DevToolbarSettings_MarkTheDefaultThresholdAsAPlaceholder()
		{
			// Review finding 7: the settings readout says the 5 ft default (and its unit) is unconfirmed.
			_rig = new UITestRig(1280, 720, devToolbar: true);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			yield return UITestRig.Frames();

			var status = _rig.View.Toolbar.Status.text;
			Assert.That(status, Does.Contain("alarm below 5.0 ft"));
			Assert.That(status, Does.Contain("placeholder").IgnoreCase);
			Assert.That(status, Does.Contain("pending crew input"));
			Assert.That(Contains(_rig.View.worldBound, _rig.View.Toolbar.worldBound), Is.True, $"the toolbar {_rig.View.Toolbar.worldBound} fits on screen");
			UnityEngine.Object.Destroy(_rig.Screenshot("ui-toolbar-day.png"));

			_rig.View.StepDepthAlarmThreshold(1);
			yield return UITestRig.Frames(2);
			Assert.That(_rig.View.Toolbar.Status.text, Does.Contain("alarm below 6.0 ft"));
			Assert.That(_rig.View.Toolbar.Status.text, Does.Not.Contain("placeholder").IgnoreCase);
		}

		[UnityTest]
		public IEnumerator Rig_DestroysTheCapturesItHandedOut_EvenWhenATestNeverDoes()
		{
			// Review finding 9: a pixel assertion that fails before Object.Destroy must not leak the texture.
			var rig = new UITestRig(320, 180);
			yield return UITestRig.Frames(2);
			var image = rig.Capture();
			Assert.That(image != null, Is.True);

			rig.Dispose();
			yield return null;

			Assert.That(image == null, Is.True, "disposing the rig destroys its captures");
		}

		private static void AssertPanelMatchesRow(WheelhousePanel panel, RawCsv csv, int row, string context)
		{
			Assert.That(panel.ShowingNoData, Is.False, context);
			foreach (var (column, side, prefix) in new[] { (panel.Port, "port", "port"), (panel.Starboard, "stbd", "starboard") })
			{
				var engineRpm = csv.Number(row, side + "_engine_rpm");
				var shaftRpm = csv.Number(row, side + "_shaft_rpm");
				var lever = csv.Number(row, prefix + "_lever");
				AssertNumber(column.Tachometer.ReadoutText, engineRpm, 0.5, $"{context}: {side} engine rpm");
				Assert.That(column.Tachometer.Rpm, Is.EqualTo(engineRpm).Within(0.01), $"{context}: {side} needle value");
				Assert.That(column.Tachometer.NeedleDegrees, Is.EqualTo(Tachometer.DialDegrees(engineRpm, 2500.0)).Within(0.1f), $"{context}: {side} needle angle on the 0-2500 dial");
				Assert.That(column.Gear.Text, Is.EqualTo(csv.Text(row, side + "_gear").ToUpperInvariant()), $"{context}: {side} gear");
				AssertNumber(column.ShaftRpm.text, Math.Abs(shaftRpm), 0.5, $"{context}: {side} shaft rpm");
				var expectedShaft = Math.Abs(shaftRpm) < 0.5 ? ShaftRotation.Stopped : shaftRpm > 0.0 ? ShaftRotation.Ahead : ShaftRotation.Astern;
				Assert.That(column.ShaftArrow.Rotation, Is.EqualTo(expectedShaft), $"{context}: {side} shaft direction");
				AssertNumber(column.LeverPercent.text.TrimEnd('%'), Math.Abs(lever) * 100.0, 0.5, $"{context}: {side} lever percent");
				var expectedDirection = Math.Abs(lever) < 0.005 ? "NEUTRAL" : lever > 0.0 ? "AHEAD" : "ASTERN";
				Assert.That(column.LeverDirection.text, Is.EqualTo(expectedDirection), $"{context}: {side} lever direction");
				Assert.That(column.Lever.Lever, Is.EqualTo(lever).Within(1e-9), $"{context}: {side} lever marker");
			}

			var heading = csv.Number(row, "heading_deg");
			AssertCompass(panel.Heading.text, heading, context + ": heading");
			Assert.That(Math.Abs(Angles.WrapPi(Angles.ToRadians(panel.Compass.HeadingDegrees - heading))), Is.LessThan(1e-6), context + ": compass card");
			var sog = csv.Number(row, "sog_kn");
			AssertNumber(panel.SpeedOverGround.text, sog, 0.05, context + ": SOG");
			AssertNumber(panel.SpeedThroughWater.text, csv.Number(row, "stw_kn"), 0.05, context + ": STW");
			if (sog >= InstrumentFormat.MinimumSpeedForCourseKnots)
			{
				AssertCompass(panel.CourseOverGround.text, csv.Number(row, "cog_deg"), context + ": COG");
			}
			else
			{
				Assert.That(panel.CourseOverGround.text, Is.EqualTo(InstrumentFormat.NoValue), context + ": COG hidden when nearly stopped");
			}

			var portRudder = csv.Number(row, "port_rudder_deg");
			var starboardRudder = csv.Number(row, "stbd_rudder_deg");
			Assert.That(panel.Rudder.PortText, Is.EqualTo(ExpectedRudder(portRudder)), context + ": port rudder");
			Assert.That(panel.Rudder.StarboardText, Is.EqualTo(ExpectedRudder(starboardRudder)), context + ": starboard rudder");
			Assert.That(panel.Rudder.PortDegrees, Is.EqualTo(portRudder).Within(1e-6), context + ": port rudder pointer");

			// Depth reads in feet by default: one decimal below 100 ft, whole feet from 100 up.
			var depthFeet = csv.Number(row, "depth_under_keel_m") / 0.3048;
			AssertNumber(panel.Depth.Value.text, depthFeet, depthFeet < 99.95 ? 0.05 : 0.5, context + ": depth");
			Assert.That(panel.Depth.Unit.text, Is.EqualTo("ft"));
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"), context + ": no contact in this run");
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Normal), context + ": 158 ft under the keel is no alarm");

			// June: Eastern Daylight Time, UTC-4. The clock shows whole seconds.
			var local = UITestRig.ScenarioStart.UtcDateTime.AddHours(-4).AddSeconds(Math.Floor(csv.Number(row, "time_s") + 1e-9));
			Assert.That(panel.Clock.text, Is.EqualTo(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)), context + ": clock");
			Assert.That(panel.ClockZone.text, Is.EqualTo("EDT"));
			Assert.That(panel.VesselName.text, Is.EqualTo("T-class (River) 85 ft catamaran"));
		}

		/// <summary>The panel's bottom-left corner is console colored and the top-left corner is backdrop: drawn, in place, right way up.</summary>
		private static void AssertPanelPixels(Texture2D image, PaletteKind palette)
		{
			var console = InstrumentPalette.For(palette).Console;
			var bottomLeft = image.GetPixel(3, 3);
			var topLeft = image.GetPixel(3, image.height - 4);
			Assert.That(ColourDistance(bottomLeft, console), Is.LessThan(0.03f), $"bottom-left {bottomLeft} should be the console {console}");
			Assert.That(ColourDistance(topLeft, console), Is.GreaterThan(0.03f), "top-left should be the backdrop, not the console");
		}

		private static float ColourDistance(Color a, Color b) =>
			Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));

		internal static void AssertNumber(string text, double expected, double tolerance, string what)
		{
			Assert.That(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var shown), Is.True, $"{what}: '{text}' is not a number");
			Assert.That(shown, Is.EqualTo(expected).Within(tolerance + 1e-9), $"{what}: shows {text}, CSV has {expected}");
		}

		internal static void AssertCompass(string text, double expectedDegrees, string what)
		{
			Assert.That(text.Length, Is.EqualTo(3), $"{what}: '{text}' should be three digits");
			Assert.That(double.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shown), Is.True, $"{what}: '{text}' is not a number");
			var difference = ((shown - expectedDegrees) % 360.0 + 540.0) % 360.0 - 180.0;
			Assert.That(Math.Abs(difference), Is.LessThanOrEqualTo(0.5 + 1e-9), $"{what}: shows {text}, CSV has {expectedDegrees}");
		}

		internal static string ExpectedRudder(double degrees)
		{
			var rounded = Math.Round(degrees, MidpointRounding.AwayFromZero);
			return rounded == 0.0 ? "0°" : (rounded > 0.0 ? "S " : "P ") + Math.Abs(rounded).ToString("0", CultureInfo.InvariantCulture) + "°";
		}

		private static bool Contains(Rect outer, Rect inner) =>
			inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;

		private static bool Overlaps(Rect a, Rect b) => a.xMin < b.xMax - 0.5f && b.xMin < a.xMax - 0.5f && a.yMin < b.yMax - 0.5f && b.yMin < a.yMax - 0.5f;
	}
}
