using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class BindingPathValidationTests
	{
		private const string Pad = "BindingReviewGamepad";
		private const string Stick = "BindingReviewJoystick";
		private InputActionAsset _asset;
		private string _directory;
		private BindingOverrideStore _store;

		[SetUp]
		public void SetUp()
		{
			InputSystem.RegisterLayout(@"{
				""name"":""BindingReviewGamepad"", ""extend"":""Gamepad"", ""controls"":[
					{""name"":""buttonSouth"", ""aliases"":[""a"",""cross"",""accept""], ""usages"":[""PrimaryAction"",""Submit""]},
					{""name"":""dpad/left"", ""aliases"":[""portButton""], ""usages"":[""PortDirection""]},
					{""name"":""spare"", ""layout"":""Button"", ""aliases"":[""auxiliary""], ""usages"":[""SpareAction""]}
				]}");
			InputSystem.RegisterLayout(@"{
				""name"":""BindingReviewJoystick"", ""extend"":""Joystick"", ""controls"":[
					{""name"":""buttonSouth"", ""layout"":""Button""}
				]}");
			_asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_directory = Path.Combine(Path.GetTempPath(), "binding-path-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_directory);
			_store = new BindingOverrideStore(Path.Combine(_directory, BindingOverrideStore.FileName));
		}

		[TearDown]
		public void TearDown()
		{
			UnityEngine.Object.DestroyImmediate(_asset);
			InputSystem.RemoveLayout(Pad);
			InputSystem.RemoveLayout(Stick);
			Directory.Delete(_directory, true);
		}

		[Test]
		public void DerivedGamepadCannotTakeTheHornButtonWithoutAConnectedDevice()
		{
			Assert.That(InputSystem.devices.Any(device => device.layout == Pad), Is.False);
			var devices = InputSystem.devices.ToArray();
			using (var service = new InputBindingService(_asset))
			{
				Assert.That(service.TryApply(ActionIds.Pause, 0, "Gamepad", "<" + Pad + ">/buttonSouth", out var holder), Is.False);
				Assert.That(holder, Is.EqualTo(ActionIds.Horn));
				Assert.That(service.DisplayString(ActionIds.Pause, "Gamepad"), Is.EqualTo(_asset.FindAction(ActionIds.Pause).GetBindingDisplayString(1)));
			}
			Assert.That(InputSystem.devices.ToArray(), Is.EqualTo(devices), "validation must not instantiate a device");
		}

		[TestCase("<Gamepad>/buttonSouth", "<BindingReviewGamepad>/buttonSouth")]
		[TestCase("<Gamepad>/a", "<BindingReviewGamepad>/buttonSouth")]
		[TestCase("<Gamepad>/buttonSouth", "<BindingReviewGamepad>/accept")]
		[TestCase("<Gamepad>/{PrimaryAction}", "<BindingReviewGamepad>/buttonSouth")]
		[TestCase("<Gamepad>/buttonSouth", "<BindingReviewGamepad>/{Submit}")]
		[TestCase("<Gamepad>/dpad/left", "<BindingReviewGamepad>/dpad/portButton")]
		[TestCase("<Gamepad>/dpad/left", "<BindingReviewGamepad>/dpad/{PortDirection}")]
		[TestCase("<Gamepad>/dpad/left", "<BindingReviewGamepad>/{PortDirection}")]
		public void RelatedLayoutsAliasesAndUsagesOverlapInBothDirections(string first, string second)
		{
			Assert.That(BindingConflicts.PathsOverlap(first, second), Is.True);
			Assert.That(BindingConflicts.PathsOverlap(second, first), Is.True);
		}

		[TestCase("<Joystick>/trigger", "<Gamepad>/buttonSouth")]
		[TestCase("<BindingReviewJoystick>/buttonSouth", "<BindingReviewGamepad>/buttonSouth")]
		[TestCase("<Gamepad>/buttonSouth", "<BindingReviewGamepad>/buttonEast")]
		public void UnrelatedLayoutsOrDifferentControlsDoNotOverlap(string first, string second)
		{
			Assert.That(BindingConflicts.PathsOverlap(first, second), Is.False);
			Assert.That(BindingConflicts.PathsOverlap(second, first), Is.False);
		}

		[Test]
		public void InheritedControlIdentityStillRespectsMutuallyExclusiveContexts()
		{
			Assert.That(BindingConflicts.Conflicts(ActionIds.Horn, "<Gamepad>/buttonSouth", ActionIds.FreeFlyFast, "<" + Pad + ">/accept"), Is.False);
		}

		[Test]
		public void SavedDerivedLayoutConflictIsRefused()
		{
			using (var service = new InputBindingService(_asset, _store))
			{
				_store.Save(new[] { new BindingOverride { action = ActionIds.Pause, scheme = "Gamepad", slot = 0, path = "<" + Pad + ">/buttonSouth" } });
				Assert.That(service.Load(), Is.False);
				Assert.That(service.LastError, Does.Contain("conflicting"));
			}
		}

		[TestCase("Keyboard", "<Gamepad>/leftStickPress")]
		[TestCase("Gamepad", "<Joystick>/trigger")]
		[TestCase("Keyboard", "<Keyboard>/notARealKey")]
		[TestCase("Keyboard", "<Mouse>/delta/x")]
		[TestCase("Gamepad", "<Gamepad>/leftStick")]
		[TestCase("Gamepad", "<Gamepad>/leftStick/x")]
		[TestCase("Keyboard", "<Keyboard>/f11/missingChild")]
		[TestCase("Keyboard", "<Button>/pressed")]
		[TestCase("Keyboard", "<Keyboard>/")]
		[TestCase("Keyboard", "<Keyboard>/f*")]
		public void InvalidSavedControlIsDroppedWithWarningWhileGoodOverrideLoads(string scheme, string badPath)
		{
			var bad = new BindingOverride { action = ActionIds.Horn, scheme = scheme, slot = 0, path = badPath };
			var good = new BindingOverride { action = ActionIds.Pause, scheme = "Keyboard", slot = 0, path = "<Keyboard>/f12" };
			Assert.That(_store.Save(new[] { bad, good }), Is.True);
			Assert.That(_store.Load(_asset, out var loaded), Is.True);
			Assert.That(loaded.Select(item => item.action), Is.EqualTo(new[] { ActionIds.Pause }));
			Assert.That(_store.Warnings.Count, Is.EqualTo(1));
			Assert.That(_store.Warnings[0], Does.Contain(ActionIds.Horn).And.Contain(scheme).And.Contain(badPath));
			using (var service = new InputBindingService(_asset, _store))
			{
				Assert.That(service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
				Assert.That(service.DisplayString(ActionIds.Pause, "Keyboard"), Is.EqualTo("F12"));
			}
		}

		[TestCase("Keyboard", "<Gamepad>/leftStickPress")]
		[TestCase("Gamepad", "<Joystick>/trigger")]
		[TestCase("Keyboard", "<Keyboard>/notARealKey")]
		[TestCase("Keyboard", "<Mouse>/delta/x")]
		[TestCase("Gamepad", "<Gamepad>/leftStick")]
		[TestCase("Gamepad", "<Gamepad>/leftStick/x")]
		public void LiveApplicationRejectsTheSameInvalidControls(string scheme, string badPath)
		{
			using (var service = new InputBindingService(_asset))
				Assert.Throws<ArgumentException>(() => service.TryApply(ActionIds.Horn, 0, scheme, badPath, out _));
		}

		[TestCase("<BindingReviewGamepad>/spare")]
		[TestCase("<BindingReviewGamepad>/auxiliary")]
		[TestCase("<BindingReviewGamepad>/{SpareAction}")]
		[TestCase("<Gamepad>/leftStickPress")]
		public void ValidUnconnectedButtonNamesAliasesAndUsagesWorkForLiveAndSavedOverrides(string controlPath)
		{
			Assert.That(InputSystem.devices.Any(device => device.layout == Pad), Is.False);
			using (var service = new InputBindingService(_asset, _store))
				Assert.That(service.TryApply(ActionIds.Pause, 0, "Gamepad", controlPath, out _), Is.True);
			Assert.That(_store.Load(_asset, out var loaded), Is.True);
			Assert.That(loaded.Single().path, Is.EqualTo(controlPath));
			Assert.That(_store.Warnings, Is.Empty);
		}
	}
}
