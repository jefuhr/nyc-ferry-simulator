using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class BindingSlotDisplayTests
	{
		private InputActionAsset _asset;
		private InputBindingService _service;

		[SetUp]
		public void SetUp()
		{
			_asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_service = new InputBindingService(_asset);
		}

		[TearDown]
		public void TearDown()
		{
			_service.Dispose();
			UnityEngine.Object.DestroyImmediate(_asset);
		}

		private string SlotText(string actionId, int slot, string scheme) => ((IBindingService)_service).DisplayString(actionId, slot, scheme);

		[Test]
		public void EveryShippedSlotMatchesItsPartOfTheCombinedStringInEveryScheme()
		{
			foreach (var row in _service.Rows)
			{
				var parts = _service.DisplayString(row.ActionId, row.Scheme).Split(new[] { " / " }, StringSplitOptions.None);
				Assert.That(SlotText(row.ActionId, row.Slot, row.Scheme), Is.EqualTo(parts[row.Slot]), row.ActionId + " " + row.Scheme + " " + row.Slot);
			}
		}

		[TestCase(ActionIds.PortLeverStep, 1)]
		[TestCase(ActionIds.NfuJog, 2)]
		[TestCase(ActionIds.FreeFlyMove, 3)]
		[TestCase(ActionIds.Horn, 0)]
		public void OverrideChangesOnlyItsSlotAndResetRestoresIt(string actionId, int slot)
		{
			var before = _service.DisplayString(actionId, "Keyboard").Split(new[] { " / " }, StringSplitOptions.None);
			Assert.That(_service.TryApply(actionId, slot, "Keyboard", "<Keyboard>/f11", out _), Is.True);
			for (var i = 0; i < before.Length; i++)
				Assert.That(SlotText(actionId, i, "Keyboard"), Is.EqualTo(i == slot ? "F11" : before[i]));
			Assert.That(_service.DisplayString(actionId, "Keyboard"), Is.EqualTo(string.Join(" / ", before.Select((text, i) => i == slot ? "F11" : text))));
			_service.Reset(actionId);
			Assert.That(SlotText(actionId, slot, "Keyboard"), Is.EqualTo(before[slot]));
		}

		[TestCase("FutureMap/Missing", 0, "Keyboard")]
		[TestCase(ActionIds.Horn, -1, "Keyboard")]
		[TestCase(ActionIds.Horn, 99, "Keyboard")]
		[TestCase(ActionIds.Horn, 0, "MissingScheme")]
		[TestCase(ActionIds.PortLever, 0, "Gamepad")]
		public void MissingOrNonRebindableSlotsHaveNoText(string actionId, int slot, string scheme)
		{
			Assert.That(SlotText(actionId, slot, scheme), Is.Empty);
		}
	}
}
