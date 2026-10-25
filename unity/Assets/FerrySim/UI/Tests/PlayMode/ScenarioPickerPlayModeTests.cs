using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The scenario picker as rendered, driven the way a person drives it: pointer clicks picked by position, and keys
	/// to the focused field. Guards against: a click that never reaches an option or Start (picking switched off by the
	/// panel's containers, or the picker under another layer); keyboard use that needs the mouse; lever and helm keys
	/// (W, A, S, D, left and right) changing the choice or moving the focus; a refusal not shown inline; Escape not
	/// closing; the choice lost when the view is rebuilt in the same session; and the picker spilling onto the panel
	/// at 720p or 1440p. Screenshots go to artifacts/unity/ui-picker-*.png.
	/// </summary>
	public class ScenarioPickerPlayModeTests
	{
		private const int July16 = 23;

		private UITestRig _rig;
		private GameObject _environmentHost;

		[SetUp]
		public void ForgetTheSession() => ScenarioPicker.SessionChoice = null;

		[TearDown]
		public void TearDown()
		{
			ScenarioPicker.SessionChoice = null;
			_rig?.Dispose();
			_rig = null;
			if (_environmentHost != null)
			{
				Object.Destroy(_environmentHost);
			}
		}

		private FakeScenarioCatalog Open(int width = 1280, int height = 720, bool withEnvironment = false)
		{
			_rig = new UITestRig(width, height);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			var catalog = FakeScenarioCatalog.Slice("2025-01-15");
			StubEnvironmentRunner environment = null;
			if (withEnvironment)
			{
				_environmentHost = new GameObject("environment readout (test)");
				environment = _environmentHost.AddComponent<StubEnvironmentRunner>();
				environment.HasEnvironment = true;
				environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0);
			}

			_rig.UI.ConnectScenario(environment, catalog, catalog);
			return catalog;
		}

		[UnityTest]
		public IEnumerator TheMouse_ChoosesEveryField_AndStarts()
		{
			var catalog = Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			Assert.That(picker.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			Assert.That(picker.Landings.Focused, Is.True, "the first field has the keyboard focus when the picker opens");

			UIInput.Click(picker.Landings.Option(2));
			yield return null;
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(2), "clicking a landing selects it");
			UIInput.Click(picker.Days.Cell(July16));
			yield return null;
			Assert.That(picker.Days.SelectedIndex, Is.EqualTo(July16), "clicking a day selects it");
			Assert.That(picker.Days.Focused, Is.True, "and takes the focus, so the keys carry on from there");
			UIInput.Click(picker.Time.StepButtonAt(3));
			UIInput.Click(picker.Time.StepButtonAt(3));
			UIInput.Click(picker.Time.StepButtonAt(2));
			yield return null;
			Assert.That(picker.Time.Text, Is.EqualTo("11:05"));
			UIInput.Click(picker.Vessels.Option(1));
			UIInput.Click(picker.Vessels.Option(0));
			yield return null;
			Assert.That(picker.Vessels.SelectedIndex, Is.EqualTo(0));
			Assert.That(picker.Summary.text, Is.EqualTo("WED 16 JUL 2025 · 11:05 EDT · Wall St/Pier 11 · T-class"));
			Assert.That(catalog.Calls, Is.Empty, "nothing starts before Start");

			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(catalog.Calls.Count, Is.EqualTo(2), string.Join(" | ", catalog.Calls));
			Assert.That(catalog.Calls[0], Is.EqualTo("create 87 2025-07-16 665 t-class"));
			Assert.That(catalog.LastStartedSettings.scenarioStartUtc, Is.EqualTo("2025-07-16T15:05:00Z"), "11:05 EDT");
			Assert.That(_rig.View.PickerVisible, Is.False, "the picker closes once the run starts");
		}

		[UnityTest]
		public IEnumerator TheKeyboard_ChoosesEveryField_AndStarts_WithoutTheHelmKeys()
		{
			var catalog = Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;

			UIInput.Key(picker, KeyCode.DownArrow);
			UIInput.Key(picker, KeyCode.DownArrow);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(2));
			AssertHelmKeysDoNothing(picker, picker.Landings);

			UIInput.Navigate(picker, NavigationMoveEvent.Direction.Next);
			yield return null;
			Assert.That(picker.Days.Focused, Is.True, "Tab moves to the day");
			UIInput.Key(picker, KeyCode.PageDown);
			UIInput.Key(picker, KeyCode.PageDown);
			UIInput.Key(picker, KeyCode.PageDown);
			Assert.That(picker.Days.SelectedIndex, Is.EqualTo(21), "three weeks on: Monday 14 July");
			UIInput.Key(picker, KeyCode.DownArrow);
			UIInput.Key(picker, KeyCode.DownArrow);
			Assert.That(picker.Days.SelectedIndex, Is.EqualTo(July16));
			AssertHelmKeysDoNothing(picker, picker.Days);

			UIInput.Navigate(picker, NavigationMoveEvent.Direction.Next);
			yield return null;
			Assert.That(picker.Time.Focused, Is.True, "Tab moves to the time");
			UIInput.Key(picker, KeyCode.PageUp);
			UIInput.Key(picker, KeyCode.PageUp);
			UIInput.Key(picker, KeyCode.UpArrow);
			Assert.That(picker.Time.Text, Is.EqualTo("11:05"));
			AssertHelmKeysDoNothing(picker, picker.Time);

			UIInput.Navigate(picker, NavigationMoveEvent.Direction.Next);
			yield return null;
			Assert.That(picker.Vessels.Focused, Is.True, "Tab moves to the vessel");
			UIInput.Key(picker, KeyCode.End);
			Assert.That(picker.Vessels.SelectedIndex, Is.EqualTo(1));
			UIInput.Key(picker, KeyCode.Home);
			Assert.That(picker.Vessels.SelectedIndex, Is.EqualTo(0));
			AssertHelmKeysDoNothing(picker, picker.Vessels);
			Assert.That(catalog.Calls, Is.Empty);

			UIInput.Key(picker, KeyCode.Return);
			yield return UITestRig.Frames(2);
			Assert.That(catalog.Calls.Count, Is.EqualTo(2), string.Join(" | ", catalog.Calls));
			Assert.That(catalog.Calls[0], Is.EqualTo("create 87 2025-07-16 665 t-class"), "Enter starts the same run the mouse did");
			Assert.That(_rig.View.PickerVisible, Is.False);
		}

		[UnityTest]
		public IEnumerator ARefusal_ShowsInline_AndThePickerStaysOpen_InDayAndNightScreenshots()
		{
			var catalog = Open();
			catalog.StartProblem = "The run could not start because the vessel file is missing.";
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;

			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(_rig.View.PickerVisible, Is.True, "a refused run leaves the choice on screen");
			Assert.That(picker.ProblemLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			Assert.That(picker.ProblemLabel.text, Is.EqualTo(catalog.StartProblem));
			Assert.That(picker.ProblemLabel.worldBound.height, Is.GreaterThan(4f), "the reason is laid out, not clipped away");
			Assert.That(Contains(picker.worldBound, picker.ProblemLabel.worldBound), Is.True, "inside the picker");
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();
				Object.Destroy(_rig.Screenshot($"ui-picker-refusal-{palette.ToString().ToLowerInvariant()}.png"));
			}

			// Changing the choice clears a reason that was about the previous one.
			UIInput.Click(picker.Time.StepButtonAt(2));
			yield return UITestRig.Frames(2);
			Assert.That(picker.ProblemLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
		}

		[UnityTest]
		public IEnumerator Escape_Closes_AndTheFieldsGiveUpTheFocus()
		{
			Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			Assert.That(picker.Landings.Focused, Is.True);

			UIInput.Key(picker, KeyCode.Escape);
			yield return UITestRig.Frames(2);
			Assert.That(_rig.View.PickerVisible, Is.False);
			Assert.That(picker.Landings.Focused, Is.False, "a hidden field takes no keys");

			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			UIInput.Click(picker.CloseButton);
			yield return UITestRig.Frames(2);
			Assert.That(_rig.View.PickerVisible, Is.False, "the Close button closes it too");
		}

		[UnityTest]
		public IEnumerator TheChoice_IsRemembered_WhenTheViewIsRebuilt()
		{
			Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			UIInput.Click(picker.Landings.Option(3));
			UIInput.Click(picker.Days.Cell(9));
			UIInput.Click(picker.Time.StepButtonAt(0));
			UIInput.Click(picker.Vessels.Option(1));
			yield return null;
			var expected = picker.Choice;
			UIInput.Key(picker, KeyCode.Escape);

			// The host is disabled and enabled again: a new view and a new picker.
			_rig.UI.enabled = false;
			yield return null;
			_rig.UI.enabled = true;
			yield return UITestRig.Frames();
			Assert.That(_rig.View.Picker, Is.Not.SameAs(picker), "the view was rebuilt");
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var choice = _rig.View.Picker.Choice;
			Assert.That(choice.StopId, Is.EqualTo(expected.StopId));
			Assert.That(choice.Date, Is.EqualTo(expected.Date));
			Assert.That(choice.LocalMinutes, Is.EqualTo(expected.LocalMinutes));
			Assert.That(choice.VesselId, Is.EqualTo(expected.VesselId));
			Assert.That(choice.LocalMinutes, Is.EqualTo(ScenarioPicker.DefaultLocalMinutes - 60));
		}

		[UnityTest]
		public IEnumerator ThePicker_OverThePanelAndOverTheHud_InDayAndNight_At720pAnd1440p()
		{
			foreach (var (width, height, resolution) in new[] { (1280, 720, "720p"), (2560, 1440, "1440p") })
			{
				_rig?.Dispose();
				if (_environmentHost != null)
				{
					Object.Destroy(_environmentHost);
				}

				Open(width, height, withEnvironment: true);
				yield return UITestRig.Frames();
				_rig.View.PickerVisible = true;
				var picker = _rig.View.Picker;
				picker.Days.SelectedIndex = 9;
				yield return UITestRig.Frames();
				foreach (var (view, panelShown) in new[] { ("panel", true), ("hud", false) })
				{
					_rig.View.PanelVisible = panelShown;
					_rig.View.HudVisible = !panelShown;
					foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
					{
						_rig.SetPalette(palette);
						yield return UITestRig.Frames();
						var root = _rig.View.worldBound;
						Assert.That(root.width, Is.EqualTo(1280f).Within(0.5f), "panel space is the 1280x720 design");
						Assert.That(Contains(root, picker.worldBound), Is.True, $"{resolution}: the picker {picker.worldBound} fits on screen");
						var below = panelShown ? _rig.View.Panel.worldBound : _rig.View.Hud.worldBound;
						Assert.That(picker.worldBound.yMax, Is.LessThanOrEqualTo(below.yMin + 0.5f),
							$"{resolution}: the picker {picker.worldBound} reaches into the {view} {below}");
						foreach (var part in new VisualElement[] { picker.Landings, picker.Days, picker.Time, picker.Vessels, picker.StartButton, picker.CloseButton, picker.Days.Legend })
						{
							Assert.That(Contains(picker.worldBound, part.worldBound), Is.True, $"{resolution}: {part.name} {part.worldBound} spills out of the picker");
						}

						Assert.That(Contains(picker.Days.worldBound, picker.Days.Cell(27).worldBound), Is.True, $"{resolution}: the last week is visible without scrolling");
						Assert.That(Contains(picker.Landings.worldBound, picker.Landings.Option(picker.Landings.Count - 1).worldBound), Is.True,
							$"{resolution}: every slice landing is visible without scrolling");
						Object.Destroy(_rig.Screenshot($"ui-picker-{view}-{palette.ToString().ToLowerInvariant()}-{resolution}.png"));
					}
				}
			}
		}

		private static void AssertHelmKeysDoNothing(ScenarioPicker picker, PickerField field)
		{
			var choice = picker.Choice;
			foreach (var key in new[] { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Q, KeyCode.E, KeyCode.H, KeyCode.P, KeyCode.R, KeyCode.C })
			{
				UIInput.Key(picker, key);
			}

			// The Input System's default UI actions also send these moves for W, A, S, D and the arrows.
			foreach (var direction in new[] { NavigationMoveEvent.Direction.Up, NavigationMoveEvent.Direction.Down, NavigationMoveEvent.Direction.Left, NavigationMoveEvent.Direction.Right })
			{
				UIInput.Navigate(picker, direction);
			}

			var after = picker.Choice;
			Assert.That(after.StopId, Is.EqualTo(choice.StopId), field.name + ": helm keys changed the landing");
			Assert.That(after.Date, Is.EqualTo(choice.Date), field.name + ": helm keys changed the day");
			Assert.That(after.LocalMinutes, Is.EqualTo(choice.LocalMinutes), field.name + ": helm keys changed the time");
			Assert.That(after.VesselId, Is.EqualTo(choice.VesselId), field.name + ": helm keys changed the vessel");
			Assert.That(field.Focused, Is.True, field.name + ": helm keys moved the focus");
		}

		private static bool Contains(Rect outer, Rect inner) =>
			inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;
	}
}
