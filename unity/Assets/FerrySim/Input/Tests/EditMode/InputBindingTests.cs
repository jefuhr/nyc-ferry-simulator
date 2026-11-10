using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	/// <summary>
	/// The keyboard defaults across the controls asset. Guards collisions in coexisting contexts, picker navigation
	/// reservations and missing actions that consumers subscribe to. Free-fly and helm keys may overlap.
	/// </summary>
	public class InputBindingTests
	{
		private static string ActionsPath => Path.Combine(Application.dataPath, "FerrySim", "Input", "FerrySimControls.inputactions");

		// Picker and debrief still consume UI Toolkit navigation events outside the asset.
		private static readonly IReadOnlyList<string> ReservedKeys = BindingConflicts.ReservedKeys;

		private static InputActionAsset Load() => InputActionAsset.FromJson(File.ReadAllText(ActionsPath));

		[Test]
		public void NoKeyboardControlIsBoundTwiceInCoexistingContexts()
		{
			var asset = Load();
			try
			{
				var owners = new List<(string action, string path)>();
				var duplicates = new List<string>();
				foreach (var map in asset.actionMaps)
				{
					foreach (var binding in map.bindings)
					{
						if (binding.isComposite || !binding.path.StartsWith("<Keyboard>/")) continue;
						var owner = map.name + "/" + binding.action;
						foreach (var existing in owners)
							if (BindingConflicts.Conflicts(existing.action, existing.path, owner, binding.path))
								duplicates.Add(binding.path + " bound by " + existing.action + " and " + owner);
						owners.Add((owner, binding.path));
					}
				}

				Assert.That(duplicates, Is.Empty, "one key has one owner in contexts that can be live together");
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}

		[Test]
		public void NoActionBindsPickerNavigationKeys()
		{
			var asset = Load();
			try
			{
				var reserved = new HashSet<string>(ReservedKeys);
				var clashes = new List<string>();
				foreach (var map in asset.actionMaps)
				{
					foreach (var binding in map.bindings)
					{
						if (!binding.isComposite && reserved.Contains(binding.path))
						{
							clashes.Add(map.name + "/" + binding.action + " on " + binding.path);
						}
					}
				}

				Assert.That(clashes, Is.Empty);
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}

		[Test]
		public void DisplaysMapHasEveryActionTheMapperSubscribesTo()
		{
			var asset = Load();
			try
			{
				var map = asset.FindActionMap(DisplayInputMapper.MapName, throwIfNotFound: true);
				Assert.That(map.actions.Count, Is.EqualTo(30));
				foreach (var action in map.actions)
				{
					Assert.That(action.type, Is.EqualTo(InputActionType.Button), action.name);
					Assert.That(action.bindings.Count, Is.EqualTo(1), action.name + " has one keyboard binding");
					Assert.That(action.bindings[0].path, Does.StartWith("<Keyboard>/"), action.name);
				}

				using (var mapper = new DisplayInputMapper(asset))
				{
					Assert.That(mapper.Map, Is.SameAs(map));
					Assert.That(mapper.Enabled, Is.False, "the composition root enables the map only in the wheelhouse view");
				}
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}

		/// <summary>The CPA alarm keys (v0.3.1): the documented keys, bound once, and none of them a key the picker's search field, the helm, the bow view or the cameras read.</summary>
		[Test]
		public void TheChartAlarmKeysAreTheDocumentedOnes_AndClashWithNothing()
		{
			var asset = Load();
			try
			{
				var expected = new Dictionary<string, string>
				{
					{ "ChartCpaLimitDown", "<Keyboard>/7" }, { "ChartCpaLimitUp", "<Keyboard>/8" },
					{ "ChartTcpaLimitDown", "<Keyboard>/9" }, { "ChartTcpaLimitUp", "<Keyboard>/0" },
					{ "ChartAlarmMute", "<Keyboard>/g" },
				};
				var map = asset.FindActionMap(DisplayInputMapper.MapName, throwIfNotFound: true);
				foreach (var pair in expected)
				{
					var action = map.FindAction(pair.Key, throwIfNotFound: true);
					Assert.That(action.bindings.Count, Is.EqualTo(1), pair.Key);
					Assert.That(action.bindings[0].path, Is.EqualTo(pair.Value), pair.Key);
				}

				// The same context rule governs defaults and live rebinding.
				var reserved = new HashSet<string>(ReservedKeys);
				foreach (var otherMap in asset.actionMaps)
				{
					foreach (var binding in otherMap.bindings)
					{
						if (binding.isComposite || expected.ContainsKey(binding.action) && otherMap.name == DisplayInputMapper.MapName)
						{
							continue;
						}

						foreach (var pair in expected)
							Assert.That(BindingConflicts.Conflicts(otherMap.name + "/" + binding.action, binding.path,
								DisplayInputMapper.MapName + "/" + pair.Key, pair.Value), Is.False,
								otherMap.name + "/" + binding.action + " shares " + binding.path);
					}
				}

				foreach (var path in expected.Values)
				{
					Assert.That(reserved, Does.Not.Contain(path), path + " is reserved for picker navigation");
				}
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}

		[Test]
		public void EveryContractActionExists_AndNewDefaultsMatchC4()
		{
			var asset = Load();
			try
			{
				foreach (var field in typeof(ActionIds).GetFields())
					Assert.That(asset.FindAction((string)field.GetValue(null)), Is.Not.Null, field.Name);
				var expected = new Dictionary<string, string>
				{
					{ ActionIds.ViewFreeFly, "f1" }, { ActionIds.ViewChase, "f2" }, { ActionIds.ViewWheelhouse, "f3" },
					{ ActionIds.BowView, "b" }, { ActionIds.BowAutoToggle, "f4" }, { ActionIds.FocusCycle, "f5" },
					{ ActionIds.AlarmAcknowledge, "slash" }, { ActionIds.AlarmSilence, "backslash" }, { ActionIds.AlarmLampTest, "f9" },
					{ ActionIds.Options, "f10" }, { ActionIds.HudEdit, "f8" }, { ActionIds.KeybindHints, "backquote" },
				};
				foreach (var pair in expected) Assert.That(asset.FindAction(pair.Key).bindings[0].path, Is.EqualTo("<Keyboard>/" + pair.Value));
				Assert.That(asset.FindAction(ActionIds.LookHold).bindings[0].path, Is.EqualTo("<Mouse>/rightButton"));
				Assert.That(asset.FindAction(ActionIds.Recentre).bindings[0].path, Is.EqualTo("<Mouse>/middleButton"));
			}
			finally { Object.DestroyImmediate(asset); }
		}

		[Test]
		public void HelmMapIsUnchangedByTheDisplaysMap()
		{
			var asset = Load();
			try
			{
				var helm = asset.FindActionMap(HelmInputMapper.MapName, throwIfNotFound: true);
				var expected = new Dictionary<string, string>
				{
					{ "<Keyboard>/f6", "NfuJog" }, { "<Keyboard>/f7", "NfuJog" },
					{ "<Keyboard>/numpad4", "NfuJog" }, { "<Keyboard>/numpad6", "NfuJog" },
					{ "<Keyboard>/q", "PortLeverStep" }, { "<Keyboard>/a", "PortLeverStep" }, { "<Keyboard>/e", "StarboardLeverStep" }, { "<Keyboard>/d", "StarboardLeverStep" },
					{ "<Keyboard>/w", "BothLeversStep" }, { "<Keyboard>/s", "BothLeversStep" }, { "<Keyboard>/leftArrow", "HelmStep" }, { "<Keyboard>/rightArrow", "HelmStep" },
					{ "<Keyboard>/m", "Midships" }, { "<Keyboard>/h", "Horn" }, { "<Keyboard>/p", "Pause" }, { "<Keyboard>/r", "Reset" }, { "<Keyboard>/c", "CameraCycle" }, { "<Keyboard>/n", "NewRun" },
				};
				var actual = new Dictionary<string, string>();
				foreach (var binding in helm.bindings)
				{
					if (!binding.isComposite && binding.path.StartsWith("<Keyboard>/"))
					{
						actual[binding.path] = binding.action;
					}
				}

				Assert.That(actual, Is.EquivalentTo(expected));
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}
	}
}
