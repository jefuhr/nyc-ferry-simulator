using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests
{
	/// <summary>
	/// The one keyboard map across every action map of the controls asset. Failure modes: a display key that also moves
	/// the helm, pauses, resets or opens the picker; a key the camera, the bow view or the picker reads directly being
	/// bound to an action; the Displays map losing an action the composition root subscribes to.
	/// </summary>
	public class InputBindingTests
	{
		private static string ActionsPath => Path.Combine(Application.dataPath, "FerrySim", "Input", "FerrySimControls.inputactions");

		/// <summary>Keys read outside the actions asset: the camera rig (F1–F3), the bow view (B, Shift+B) and the picker and debrief (UI Toolkit key events).</summary>
		private static readonly string[] ReservedKeys =
		{
			"<Keyboard>/f1", "<Keyboard>/f2", "<Keyboard>/f3", "<Keyboard>/b", "<Keyboard>/escape", "<Keyboard>/enter", "<Keyboard>/numpadEnter",
			"<Keyboard>/space", "<Keyboard>/upArrow", "<Keyboard>/downArrow", "<Keyboard>/pageUp", "<Keyboard>/pageDown", "<Keyboard>/home", "<Keyboard>/end",
			"<Keyboard>/leftShift", "<Keyboard>/rightShift",
		};

		private static InputActionAsset Load() => InputActionAsset.FromJson(File.ReadAllText(ActionsPath));

		[Test]
		public void NoKeyboardControlIsBoundTwiceAcrossAllActionMaps()
		{
			var asset = Load();
			try
			{
				var owners = new Dictionary<string, string>();
				var duplicates = new List<string>();
				foreach (var map in asset.actionMaps)
				{
					foreach (var binding in map.bindings)
					{
						if (binding.isComposite || !binding.path.StartsWith("<Keyboard>/"))
						{
							continue;
						}

						var owner = map.name + "/" + binding.action;
						if (owners.TryGetValue(binding.path, out var existing))
						{
							duplicates.Add(binding.path + " bound by " + existing + " and " + owner);
						}
						else
						{
							owners.Add(binding.path, owner);
						}
					}
				}

				Assert.That(duplicates, Is.Empty, "a plain key binding fires on every press, Shift or not, so one key means one action");
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
		}

		[Test]
		public void NoActionBindsAKeyReadDirectlyByTheCameraBowViewOrPicker()
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

				// Every other binding in every map, and every key read outside the asset, stays off those keys.
				var reserved = new HashSet<string>(ReservedKeys);
				foreach (var otherMap in asset.actionMaps)
				{
					foreach (var binding in otherMap.bindings)
					{
						if (binding.isComposite || expected.ContainsKey(binding.action) && otherMap.name == DisplayInputMapper.MapName)
						{
							continue;
						}

						Assert.That(expected.Values, Does.Not.Contain(binding.path), otherMap.name + "/" + binding.action + " shares " + binding.path);
					}
				}

				foreach (var path in expected.Values)
				{
					Assert.That(reserved, Does.Not.Contain(path), path + " is read directly by the camera, the bow view or the picker");
				}
			}
			finally
			{
				Object.DestroyImmediate(asset);
			}
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
