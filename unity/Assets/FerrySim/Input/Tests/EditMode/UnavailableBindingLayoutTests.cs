using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class UnavailableBindingLayoutTests
	{
		private const string Layout = "BindingOfflineJoystick";
		private const string ButtonPath = "<BindingOfflineJoystick>/extraButton";
		private InputActionAsset _asset;
		private BindingOverrideStore _store;
		private string _directory;

		[SetUp]
		public void SetUp()
		{
			RegisterLayout();
			_asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_directory = Path.Combine(Path.GetTempPath(), "binding-offline-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_directory);
			_store = new BindingOverrideStore(Path.Combine(_directory, BindingOverrideStore.FileName));
		}

		[TearDown]
		public void TearDown()
		{
			UnityEngine.Object.DestroyImmediate(_asset);
			InputSystem.RemoveLayout(Layout);
			Directory.Delete(_directory, true);
		}

		private static void RegisterLayout(string parent = "Joystick", string button = "extraButton", string controlLayout = "Button")
		{
			InputSystem.RegisterLayout("{\"name\":\"" + Layout + "\",\"extend\":\"" + parent + "\",\"controls\":[{\"name\":\"" +
				button + "\",\"layout\":\"" + controlLayout + "\"}]}");
		}

		private void SaveThenForgetLayout(string actionId = ActionIds.Horn)
		{
			using (var service = new InputBindingService(_asset, _store))
				Assert.That(service.TryApply(actionId, 0, "Joystick", ButtonPath, out _), Is.True);
			InputSystem.RemoveLayout(Layout);
			Assert.That(InputSystem.LoadLayout(Layout), Is.Null);
		}

		[Test]
		public void UnknownLayoutSurvivesLoadAndSaveByteForByteWithAnExplicitWarning()
		{
			SaveThenForgetLayout();
			var before = File.ReadAllBytes(_store.Path);
			Assert.That(_store.Load(_asset, out var loaded), Is.True);
			Assert.That(loaded.Count, Is.EqualTo(1));
			Assert.That(loaded[0].action, Is.EqualTo(ActionIds.Horn));
			Assert.That(loaded[0].scheme, Is.EqualTo("Joystick"));
			Assert.That(loaded[0].slot, Is.Zero);
			Assert.That(loaded[0].path, Is.EqualTo(ButtonPath));
			Assert.That(_store.Warnings.Single(), Does.Contain(ActionIds.Horn).And.Contain(ButtonPath)
				.And.Contain("cannot be checked until its device is connected"));
			Assert.That(_store.Save(loaded), Is.True);
			Assert.That(File.ReadAllBytes(_store.Path), Is.EqualTo(before), "the entire one-entry document must round-trip unchanged");
		}

		[Test]
		public void UnrelatedSaveRetainsAnUnknownLayoutOverrideOnRegisteredInstances()
		{
			SaveThenForgetLayout();
			using (var service = new InputBindingService(_asset, _store))
			{
				service.Register(_asset);
				Assert.That(_asset.FindAction(ActionIds.Horn).bindings[2].overridePath, Is.EqualTo(ButtonPath));
				Assert.That(service.TryApply(ActionIds.Pause, 0, "Keyboard", "<Keyboard>/f12", out _), Is.True);
				Assert.That(_store.Load(_asset, out var loaded), Is.True);
				Assert.That(loaded.Count, Is.EqualTo(2));
				Assert.That(loaded.Single(item => item.action == ActionIds.Horn).path, Is.EqualTo(ButtonPath));
			}
		}

		[Test]
		public void LayoutKnownOnLaterLoadValidatesAndAppliesTheKeptOverride()
		{
			SaveThenForgetLayout();
			Assert.That(_store.Load(_asset, out var kept), Is.True);
			Assert.That(kept.Count, Is.EqualTo(1));
			RegisterLayout();
			using (var service = new InputBindingService(_asset, _store))
			{
				service.Register(_asset);
				Assert.That(service.Load(), Is.True);
				Assert.That(_store.Warnings, Is.Empty);
				Assert.That(_asset.FindAction(ActionIds.Horn).bindings[2].overridePath, Is.EqualTo(ButtonPath));
			}
		}

		[TestCase("Gamepad", "extraButton", "Button")]
		[TestCase("Joystick", "extraButton", "Axis")]
		[TestCase("Joystick", "differentButton", "Button")]
		public void LayoutKnownOnLaterLoadDropsAReplacementThatIsNowInvalid(string parent, string button, string controlLayout)
		{
			SaveThenForgetLayout();
			Assert.That(_store.Load(_asset, out var kept), Is.True);
			Assert.That(kept.Count, Is.EqualTo(1));
			RegisterLayout(parent, button, controlLayout);
			Assert.That(_store.Load(_asset, out var loaded), Is.True);
			Assert.That(loaded, Is.Empty);
			Assert.That(_store.Warnings.Single(), Does.Contain("Dropped invalid").And.Contain(ButtonPath));
		}

		[Test]
		public void LayoutKnownOnLaterLoadRejectsAConflictWithTheDefaultHorn()
		{
			// Add a joystick slot to this test's private asset so another action can use the extra button.
			_asset.FindAction(ActionIds.Pause).AddBinding("<Joystick>/stick/up", groups: "Joystick");
			SaveThenForgetLayout(ActionIds.Pause);
			using (var service = new InputBindingService(_asset, _store))
			{
				Assert.That(service.Load(), Is.True);
				Assert.That(_store.Warnings.Single(), Does.Contain("cannot be checked"));
				// On this later layout the saved name aliases the trigger, now colliding with the horn.
				InputSystem.RegisterLayout("{\"name\":\"" + Layout + "\",\"extend\":\"Joystick\",\"controls\":[{\"name\":\"trigger\",\"aliases\":[\"extraButton\"]}]}");
				Assert.That(service.Load(), Is.False);
				Assert.That(service.LastError, Does.Contain("conflicting"));
				service.Register(_asset);
				Assert.That(_asset.FindAction(ActionIds.Pause).bindings.Last().overridePath, Is.Null);
				Assert.That(_asset.FindAction(ActionIds.Horn).bindings[2].effectivePath, Is.EqualTo("<Joystick>/trigger"));
			}
		}

		[Test]
		public void LiveApplyStillRefusesAnUnknownLayout()
		{
			InputSystem.RemoveLayout(Layout);
			using (var service = new InputBindingService(_asset))
				Assert.Throws<ArgumentException>(() => service.TryApply(ActionIds.Horn, 0, "Joystick", ButtonPath, out _));
		}

		[Test]
		public void WildcardOnAnUnknownLayoutStillDropsAndDoesNotDisplaceAGoodOverride()
		{
			InputSystem.RemoveLayout(Layout);
			_store.Save(new[]
			{
				new BindingOverride { action = ActionIds.Horn, scheme = "Joystick", slot = 0, path = "<" + Layout + ">/extra*" },
				new BindingOverride { action = ActionIds.Pause, scheme = "Keyboard", slot = 0, path = "<Keyboard>/f12" },
			});
			Assert.That(_store.Load(_asset, out var loaded), Is.True);
			Assert.That(loaded.Single().action, Is.EqualTo(ActionIds.Pause));
			Assert.That(_store.Warnings.Single(), Does.Contain("Dropped invalid"));
		}
	}
}
