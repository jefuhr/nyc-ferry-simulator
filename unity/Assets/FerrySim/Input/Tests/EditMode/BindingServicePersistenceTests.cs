using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class BindingServicePersistenceTests
	{
		private string _directory;
		private InputActionAsset _defaults;
		private InputActionAsset _live;
		private BindingOverrideStore _store;
		[SetUp] public void Setup()
		{
			_directory = Path.Combine(Path.GetTempPath(), "binding-service-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory);
			_defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_live = UnityEngine.Object.Instantiate(_defaults);
			_store = new BindingOverrideStore(Path.Combine(_directory, BindingOverrideStore.FileName));
		}
		[TearDown] public void Cleanup() { UnityEngine.Object.DestroyImmediate(_live); UnityEngine.Object.DestroyImmediate(_defaults); Directory.Delete(_directory, true); }
		[Test] public void SavedOverrideLoadsIntoANewServiceAndLateInstance()
		{
			using (var first = new InputBindingService(_defaults, _store))
				Assert.That(first.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
			using (var second = new InputBindingService(_defaults, _store))
			{
				second.Register(_live);
				Assert.That(second.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("F11"));
				Assert.That(_live.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/f11"));
				var changed = 0; second.BindingsChanged += () => changed++;
				Assert.That(second.Load(), Is.True); Assert.That(changed, Is.EqualTo(1));
			}
		}
		[Test] public void ConflictingDocumentIsIgnoredWholeWithoutRewritingIt()
		{
			using (var service = new InputBindingService(_defaults, _store))
			{
				service.Register(_live); service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _);
				_store.Save(new[] { new BindingOverride { action = ActionIds.Horn, scheme = "Keyboard", slot = 0, path = "<Keyboard>/p" } });
				var before = File.ReadAllText(_store.Path);
				Assert.That(service.Load(), Is.False); Assert.That(service.LastError, Does.Contain("conflicting"));
				Assert.That(service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("H"));
				Assert.That(_live.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/h"));
				Assert.That(File.ReadAllText(_store.Path), Is.EqualTo(before));
			}
		}
		[Test] public void FailedSaveDoesNotPublishOrChangeAnyLiveInstance()
		{
			using (var service = new InputBindingService(_defaults, _store))
			{
				service.Register(_live); service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _);
				var before = File.ReadAllText(_store.Path); var changed = 0; service.BindingsChanged += () => changed++;
				Directory.CreateDirectory(_store.Path + ".tmp");
				Assert.Throws<IOException>(() => service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f12", out _));
				Assert.That(changed, Is.Zero); Assert.That(service.LastError, Is.Not.Empty);
				Assert.That(service.DisplayString(ActionIds.Horn, "Keyboard"), Is.EqualTo("F11"));
				Assert.That(_live.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/f11"));
				Assert.That(File.ReadAllText(_store.Path), Is.EqualTo(before));
			}
		}
	}
}
