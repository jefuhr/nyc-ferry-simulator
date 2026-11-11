using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	public class BindingLabelTests
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

		[Test]
		public void EveryShippedRowHasAnExplicitSentenceCaseLabel()
		{
			Assert.That(_service.Rows, Is.Not.Empty);
			foreach (var row in _service.Rows)
			{
				Assert.That(BindingLabels.TryGetRowLabel(row.ActionId, row.Slot, out var label), Is.True, row.ActionId + " slot " + row.Slot);
				Assert.That(row.Label, Is.EqualTo(label), row.ActionId);
				Assert.That(row.Label, Does.Not.Match("[a-z][A-Z]|(?:Port|Starboard|Both)Lever|^[^ ]+/"), row.ActionId);
				Assert.That(char.IsUpper(row.Label[0]), Is.True, row.ActionId);
				Assert.That(row.Label.Substring(1), Is.EqualTo(row.Label.Substring(1).ToLowerInvariant()), row.ActionId);
				Assert.That(row.Label, Does.Not.Contain("negative").And.Not.Contain("positive"), row.ActionId);
			}
		}

		[TestCase(ActionIds.PortLeverStep, "Port engine astern", "Port engine ahead")]
		[TestCase(ActionIds.StarboardLeverStep, "Starboard engine astern", "Starboard engine ahead")]
		[TestCase(ActionIds.BothLeversStep, "Both engines astern", "Both engines ahead")]
		[TestCase(ActionIds.HelmStep, "Tiller to port", "Tiller to starboard")]
		[TestCase(ActionIds.NfuJog, "Jog lever to port", "Jog lever to starboard")]
		[TestCase(ActionIds.FreeFlyVertical, "Fly down", "Fly up")]
		public void CompositeSidesNameTheirDistinctDirections(string actionId, string first, string second)
		{
			foreach (var scheme in _service.Rows.Where(row => row.ActionId == actionId).GroupBy(row => row.Scheme))
			{
				var rows = scheme.OrderBy(row => row.Slot).ToArray();
				Assert.That(rows.Length % 2, Is.Zero, actionId);
				for (var pair = 0; pair < rows.Length / 2; pair++)
				{
					var suffix = pair == 0 ? "" : " (alternate)";
					Assert.That(rows[pair * 2].Label, Is.EqualTo(first + suffix), scheme.Key);
					Assert.That(rows[pair * 2 + 1].Label, Is.EqualTo(second + suffix), scheme.Key);
					Assert.That(rows[pair * 2].Label, Is.Not.EqualTo(rows[pair * 2 + 1].Label));
				}
			}
		}

		[Test]
		public void FreeFlyMovementNamesAllFourDirections()
		{
			Assert.That(_service.Rows.Where(row => row.ActionId == ActionIds.FreeFlyMove).Select(row => row.Label),
				Is.EqualTo(new[] { "Fly forward", "Fly backward", "Fly left", "Fly right" }));
		}

		[Test]
		public void LabelsAreUniqueWithinEachScheme()
		{
			foreach (var scheme in _service.Rows.GroupBy(row => row.Scheme))
				Assert.That(scheme.Select(row => row.Label), Is.Unique, scheme.Key);
		}

		[TestCase(ActionIds.Horn, "Horn")]
		[TestCase(ActionIds.AlarmAcknowledge, "Acknowledge alarms")]
		[TestCase(ActionIds.AlarmSilence, "Silence alarms")]
		[TestCase(ActionIds.AlarmLampTest, "Lamp test")]
		[TestCase(ActionIds.ViewFreeFly, "Free-fly view")]
		[TestCase("Displays/ChartZoomIn", "Chart zoom in")]
		public void SharedControlNounsAndPlayerFacingExamplesAgree(string actionId, string label)
		{
			Assert.That(_service.Rows.Where(row => row.ActionId == actionId).Select(row => row.Label), Is.Not.Empty.And.All.EqualTo(label));
		}

		[TestCase("Helm", "Helm")]
		[TestCase("Displays", "Displays")]
		[TestCase("Audio", "Audio")]
		[TestCase("Camera", "Camera")]
		[TestCase("FreeFly", "Free-fly camera")]
		[TestCase("Alarms", "Alarms")]
		[TestCase("Interface", "Interface")]
		[TestCase("FutureMap", "FutureMap")]
		public void GroupNamesHaveDisplayLabelsAndUnknownGroupsFallBack(string map, string expected)
		{
			Assert.That(BindingLabels.GroupName(map), Is.EqualTo(expected));
		}

		[Test]
		public void UnknownRowsKeepTheGeneratedFallback()
		{
			var map = new InputActionMap("FutureMap");
			map.AddAction("FutureButton", InputActionType.Button, "<Keyboard>/f12", groups: "Keyboard", expectedControlLayout: "Button");
			_asset.AddActionMap(map);
			using (var service = new InputBindingService(_asset))
				Assert.That(service.Rows.Single(row => row.ActionId == "FutureMap/FutureButton").Label, Is.EqualTo("Future Button"));
		}
	}
}
