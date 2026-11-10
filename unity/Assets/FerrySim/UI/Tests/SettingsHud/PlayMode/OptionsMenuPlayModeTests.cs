using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.SettingsHud.PlayMode
{
	/// <summary>
	/// The options menu and HUD edit mode in a rendered panel, with the real binding service and virtual devices.
	/// Failure modes guarded: F10 or F8 (as bound) not reaching the menu or edit mode, or a press between frames lost; the
	/// menu not announcing itself, or announcing twice; the focus left outside the menu when it opens; a control that the
	/// keyboard alone or the gamepad alone cannot reach, or cannot change; Escape both cancelling a rebind and closing the
	/// menu; a rebind from the menu not reaching the live bindings; edit mode pausing the run.
	/// <para>
	/// The keys F10 and F8 go through the Input System as real presses. Navigation is sent as the UI events the Input
	/// System's UI module raises for those devices (NavigationMove, Submit, Cancel; KeyDown for Escape), not as device
	/// presses: whether the module turns a virtual device's presses into those events in a batch run is not covered here.
	/// </para>
	/// </summary>
	public class OptionsMenuPlayModeTests : InputTestFixture
	{
		private Keyboard _keyboard;
		private InputBindingService _service;
		private HudTestRig _rig;

		public override void Setup()
		{
			base.Setup();
			_keyboard = InputSystem.AddDevice<Keyboard>();
			InputSystem.AddDevice<Gamepad>();
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			_service = new InputBindingService(defaults);
			Object.Destroy(defaults);
			_rig = new HudTestRig(1280, 720, configure: ui => ui.ConfigureInput(_service));
		}

		public override void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
			_service?.Dispose();
			_service = null;
			base.TearDown();
		}

		[UnityTest]
		public IEnumerator TheOptionsKey_OpensAndClosesTheMenu_AndEscapeAndTheEastButtonCloseIt()
		{
			yield return HudTestRig.Frames(2);
			var view = _rig.View;
			var events = new List<bool>();
			view.OptionsVisibilityChanged += events.Add;
			Assert.That(view.OptionsVisible, Is.False);

			PressAndRelease(_keyboard.f10Key);
			yield return HudTestRig.Frames(2);
			Assert.That(view.OptionsVisible, Is.True);
			Assert.That(events, Is.EqualTo(new[] { true }));
			Assert.That(view.Options.Contains(Nav.Focused(view)), Is.True, "the focus moves into the menu");

			PressAndRelease(_keyboard.f10Key);
			yield return HudTestRig.Frames(2);
			Assert.That(view.OptionsVisible, Is.False);
			Assert.That(events, Is.EqualTo(new[] { true, false }));
			Assert.That(Nav.Focused(view) == null || !view.Options.Contains(Nav.Focused(view)), Is.True, "a closed menu keeps no focus");

			PressAndRelease(_keyboard.f10Key);
			yield return HudTestRig.Frames(2);
			Nav.Key(view.Options, KeyCode.Escape);
			yield return null;
			Assert.That(view.OptionsVisible, Is.False, "Escape closes it");

			PressAndRelease(_keyboard.f10Key);
			yield return HudTestRig.Frames(2);
			Nav.Cancel(view.Options);
			yield return null;
			Assert.That(view.OptionsVisible, Is.False, "the gamepad's east button closes it");
			Assert.That(events, Is.EqualTo(new[] { true, false, true, false, true, false }));
		}

		[UnityTest]
		public IEnumerator TheHudEditKey_TogglesEditMode_AndTheRunKeepsGoing()
		{
			yield return HudTestRig.Frames(2);
			var view = _rig.View;
			var events = new List<bool>();
			view.HudEditModeChanged += events.Add;
			_rig.Source.Seek(10.0);
			_rig.Source.IsPlaying = true;
			PressAndRelease(_keyboard.f8Key);
			yield return HudTestRig.Frames(2);
			Assert.That(view.HudEditing, Is.True);
			Assert.That(view.HudLayer.EditBar.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			var time = _rig.Source.PlaybackTime;
			yield return HudTestRig.Frames(10);
			Assert.That(_rig.Source.PlaybackTime, Is.GreaterThan(time), "edit mode does not pause the run");
			Assert.That(_rig.Source.IsPlaying, Is.True);
			Assert.That(Time.timeScale, Is.EqualTo(1f));

			PressAndRelease(_keyboard.f8Key);
			yield return HudTestRig.Frames(2);
			Assert.That(view.HudEditing, Is.False);
			Assert.That(events, Is.EqualTo(new[] { true, false }));

			// F8 does nothing while the menu is open (the menu has the keys).
			view.OptionsVisible = true;
			PressAndRelease(_keyboard.f8Key);
			yield return HudTestRig.Frames(2);
			Assert.That(view.HudEditing, Is.False);
		}

		[UnityTest]
		public IEnumerator TheKeyboardAlone_ReachesAndChangesEveryControl_OnEveryPage()
		{
			yield return WalkEveryPage(keyboard: true);
		}

		[UnityTest]
		public IEnumerator TheGamepadAlone_ReachesAndChangesEveryControl_OnEveryPage()
		{
			yield return WalkEveryPage(keyboard: false);
		}

		[UnityTest]
		public IEnumerator ARebindFromTheMenu_ReachesTheLiveBindings_AndResetPutsItBack()
		{
			yield return HudTestRig.Frames(2);
			var view = _rig.View;
			view.OptionsVisible = true;
			yield return null;
			var menu = view.Options;
			var horn = menu.BindingRows.First(row => row.Row.ActionId == ActionIds.Horn && row.Row.Scheme == "Keyboard");
			Assert.That(horn.KeyText, Is.EqualTo("H"));
			horn.Focus();
			yield return null;
			Nav.Submit(menu);
			Assert.That(_service.IsRebinding, Is.True);
			Assert.That(horn.KeyText, Is.EqualTo(OptionsMenu.PressAKey));
			Press(_keyboard.f11Key);
			yield return null;
			Assert.That(_service.IsRebinding, Is.False);
			Assert.That(horn.KeyText, Is.EqualTo("F11"));
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("F11"));
			Release(_keyboard.f11Key);
			yield return null;

			// Escape cancels a rebind and does not also close the menu.
			Nav.Submit(menu);
			Assert.That(_service.IsRebinding, Is.True);
			Press(_keyboard.escapeKey);
			yield return null;
			TestContext.WriteLine("Escape through the Input System cancelled the capture by itself: " + !_service.IsRebinding);
			Nav.Key(menu, KeyCode.Escape);
			Release(_keyboard.escapeKey);
			yield return null;
			Assert.That(_service.IsRebinding, Is.False);
			Assert.That(view.OptionsVisible, Is.True);
			Assert.That(horn.KeyText, Is.EqualTo("F11"));

			// A taken key is refused and named.
			horn.Focus();
			Nav.Submit(menu);
			Press(_keyboard.pKey);
			yield return null;
			Release(_keyboard.pKey);
			Assert.That(menu.Message, Does.Contain("Pause"));
			Assert.That(horn.KeyText, Is.EqualTo("F11"));
			yield return null;

			// Right moves to the row's Reset; Submit resets the action.
			horn.Focus();
			Nav.Move(menu, NavigationMoveEvent.Direction.Right);
			Nav.Submit(menu);
			Assert.That(horn.KeyText, Is.EqualTo("H"));
			Assert.That(_service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
		}

		private IEnumerator WalkEveryPage(bool keyboard)
		{
			yield return HudTestRig.Frames(2);
			var view = _rig.View;
			view.OptionsVisible = true;
			yield return HudTestRig.Frames(2);
			var menu = view.Options;
			var settings = view.Settings;
			Assert.That(menu.Page, Is.EqualTo(OptionsPage.Controls));
			for (var page = 0; page < 4; page++)
			{
				Assert.That(menu.Page, Is.EqualTo((OptionsPage)page));
				yield return null;
				var order = menu.FocusOrder.ToList();
				Assert.That(order.Count, Is.GreaterThan(3), "page " + menu.Page);
				var reached = new HashSet<VisualElement>();
				for (var step = 0; step < order.Count; step++)
				{
					// The keyboard has Tab as well as the arrows; the gamepad only its d-pad or stick.
					Nav.Move(menu, keyboard && step % 2 == 1 ? NavigationMoveEvent.Direction.Next : NavigationMoveEvent.Direction.Down);
					var focused = Nav.Focused(menu);
					Assert.That(menu.Contains(focused), Is.True, $"step {step} left the menu ({focused?.name})");
					reached.Add(focused);
				}

				var missed = order.Where(element => !reached.Contains(element)).Select(element => element.name).ToArray();
				Assert.That(missed, Is.Empty, $"{(keyboard ? "keyboard" : "gamepad")} cannot reach these on page {menu.Page}");
				Assert.That(reached.Contains(menu.CloseButton), Is.True);

				// Change something on this page with Left, Right and Submit only.
				switch (menu.Page)
				{
					case OptionsPage.Controls:
						FocusByMoving(menu, menu.KeybindHints);
						Nav.Move(menu, NavigationMoveEvent.Direction.Right);
						Assert.That(settings.KeybindHints, Is.EqualTo(KeybindHintMode.Always));
						FocusByMoving(menu, menu.SchemeChoice);
						Nav.Move(menu, NavigationMoveEvent.Direction.Right);
						Assert.That(menu.Scheme, Is.EqualTo("Gamepad"));
						Nav.Move(menu, NavigationMoveEvent.Direction.Left);
						break;
					case OptionsPage.Hud:
						FocusByMoving(menu, menu.Layout);
						Nav.Move(menu, NavigationMoveEvent.Direction.Right);
						Assert.That(settings.HudLayout, Is.EqualTo(HudPresets.Minimal));
						break;
					case OptionsPage.Display:
						FocusByMoving(menu, menu.DistanceUnit);
						Nav.Move(menu, NavigationMoveEvent.Direction.Right);
						Assert.That(settings.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
						FocusByMoving(menu, menu.ConsoleScreens);
						Nav.Submit(menu);
						Assert.That(settings.ConsoleScreens, Is.False);
						break;
					case OptionsPage.Audio:
						FocusByMoving(menu, menu.MasterVolume);
						Nav.Move(menu, NavigationMoveEvent.Direction.Left);
						Assert.That(settings.MasterVolume, Is.EqualTo(0.95f).Within(1e-6f));
						FocusByMoving(menu, menu.Mute);
						Nav.Submit(menu);
						Assert.That(settings.Muted, Is.True);
						break;
				}

				// Back up to the page's tab and step right to the next page.
				for (var guard = 0; guard < 400 && !IsTab(Nav.Focused(menu)); guard++)
				{
					Nav.Move(menu, NavigationMoveEvent.Direction.Up);
				}

				Assert.That(IsTab(Nav.Focused(menu)), Is.True);
				if (page < 3)
				{
					Nav.Move(menu, NavigationMoveEvent.Direction.Right);
				}
			}

			if (keyboard)
			{
				Nav.Key(menu, KeyCode.Escape);
			}
			else
			{
				Nav.Cancel(menu);
			}

			yield return null;
			Assert.That(view.OptionsVisible, Is.False);
		}

		private static bool IsTab(VisualElement element) => element != null && element.ClassListContains("fs-menu-tab");

		private static void FocusByMoving(OptionsMenu menu, VisualElement target)
		{
			for (var guard = 0; guard < 400 && Nav.Focused(menu) != target; guard++)
			{
				Nav.Move(menu, NavigationMoveEvent.Direction.Down);
			}

			Assert.That(Nav.Focused(menu), Is.EqualTo(target), "Down never reached " + target.name);
		}

		private void PressAndRelease(UnityEngine.InputSystem.Controls.KeyControl key)
		{
			Press(key);
			Release(key);
		}
	}
}
