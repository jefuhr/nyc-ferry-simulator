using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class RebindReachesEveryInstanceTests
	{
		[Test]
		public void OverrideAndResetReachEveryRegisteredInstance_IncludingLateRegistration()
		{
			var defaults = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			var copies = new[] { Object.Instantiate(defaults), Object.Instantiate(defaults), Object.Instantiate(defaults) };
			using (var service = new InputBindingService(defaults))
			try
			{
				service.Register(copies[0]); service.Register(copies[1]);
				Assert.That(service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out _), Is.True);
				service.Register(copies[2]);
				foreach (var asset in copies) Assert.That(asset.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/f11"));
				service.ResetAll();
				foreach (var asset in copies) Assert.That(asset.FindAction(ActionIds.Horn).bindings[0].effectivePath, Is.EqualTo("<Keyboard>/h"));
			}
			finally { foreach (var asset in copies) Object.DestroyImmediate(asset); Object.DestroyImmediate(defaults); }
		}
		[Test]
		public void InitialLoadRaisesChangedAndResourceMatchesTheEditableAsset()
		{
			var json = File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions"));
			var resource = Resources.Load<TextAsset>("FerrySimInput/BindingDefaults");
			Assert.That(resource, Is.Not.Null);
			Assert.That(resource.text, Is.EqualTo(json), "regenerate the runtime defaults snapshot after editing the inputactions asset");
			var defaults = InputActionAsset.FromJson(json);
			using (var service = new InputBindingService(defaults))
			{
				var changed = 0; service.BindingsChanged += () => changed++;
				Assert.That(service.Load(), Is.True); Assert.That(changed, Is.EqualTo(1));
			}
			Object.DestroyImmediate(defaults);
		}
	}
}
