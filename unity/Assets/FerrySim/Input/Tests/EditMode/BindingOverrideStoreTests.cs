using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	// Corrupt documents must never partially apply. Stale slots are the only permitted partial load.
	public class BindingOverrideStoreTests
	{
		private string _directory;
		private InputActionAsset _asset;
		private BindingOverrideStore _store;
		[SetUp] public void Setup()
		{
			_directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "binding-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_directory);
			_store = new BindingOverrideStore(System.IO.Path.Combine(_directory, BindingOverrideStore.FileName));
			_asset = InputActionAsset.FromJson(File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
		}
		[TearDown] public void Cleanup() { UnityEngine.Object.DestroyImmediate(_asset); Directory.Delete(_directory, true); }
		private static BindingOverride Override(string action = "Helm/Horn", int slot = 0) => new BindingOverride
		{ action = action, slot = slot, scheme = "Keyboard", path = "<Keyboard>/f11" };

		[Test] public void RoundTripMatchesC5AndReplacementLeavesNoTemporaryFile()
		{
			Assert.That(_store.Save(new[] { Override() }), Is.True, _store.LastError);
			Assert.That(_store.Load(_asset, out var items), Is.True, _store.LastError);
			Assert.That(items.Count, Is.EqualTo(1)); Assert.That(items[0].action, Is.EqualTo("Helm/Horn"));
			Assert.That(items[0].scheme, Is.EqualTo("Keyboard")); Assert.That(items[0].slot, Is.Zero);
			Assert.That(items[0].path, Is.EqualTo("<Keyboard>/f11"));
			Assert.That(File.ReadAllText(_store.Path), Does.Contain("\"version\": 1").And.Contain("\"overrides\""));
			Assert.That(_store.Save(new BindingOverride[0]), Is.True);
			Assert.That(_store.Load(_asset, out items), Is.True); Assert.That(items, Is.Empty);
			Assert.That(File.Exists(_store.Path + ".tmp"), Is.False);
		}
		[Test] public void FailedTemporaryWritePreservesPreviousFile()
		{
			Assert.That(_store.Save(new[] { Override() }), Is.True, _store.LastError); var before = File.ReadAllText(_store.Path);
			Directory.CreateDirectory(_store.Path + ".tmp");
			Assert.That(_store.Save(new BindingOverride[0]), Is.False);
			Assert.That(File.ReadAllText(_store.Path), Is.EqualTo(before));
			Assert.That(_store.LastError, Is.Not.Empty);
		}
		[TestCase("{broken")]
		[TestCase("{\"version\":1,\"overrides\":[{\"action\":\"Helm/Horn\",\"scheme\":\"Keyboard\",\"path\":\"<Keyboard>/f11\"}]}")]
		[TestCase("{\"version\":2,\"overrides\":[]}")]
		[TestCase("{\"version\":1}")]
		[TestCase("{\"version\":1,\"overrides\":[{\"action\":\"Helm/Horn\",\"scheme\":\"Keyboard\",\"slot\":0,\"path\":\"<Keyboard>/f11\"},{\"action\":\"Helm/Pause\",\"scheme\":\"Keyboard\",\"slot\":-1,\"path\":\"<Keyboard>/f12\"}]}")]
		public void BadFileIsIgnoredWholeAndNotOverwritten(string document)
		{
			File.WriteAllText(_store.Path, document);
			Assert.That(_store.Load(_asset, out var items), Is.False); Assert.That(items, Is.Empty);
			Assert.That(File.ReadAllText(_store.Path), Is.EqualTo(document));
		}
		[Test] public void StaleActionAndSlotAreDroppedWithWarningsOnly()
		{
			_store.Save(new[] { Override(), Override("Helm/Gone"), Override("Helm/Pause", 99) });
			Assert.That(_store.Load(_asset, out var items), Is.True);
			Assert.That(items.Count, Is.EqualTo(1)); Assert.That(_store.Warnings.Count, Is.EqualTo(2));
			Assert.That(_store.Warnings[0], Does.Contain("Helm/Gone"));
		}
		[Test] public void DuplicateOverrideRejectsTheWholeDocument()
		{
			_store.Save(new[] { Override(), Override() });
			Assert.That(_store.Load(_asset, out var items), Is.False); Assert.That(items, Is.Empty);
		}
	}
}
