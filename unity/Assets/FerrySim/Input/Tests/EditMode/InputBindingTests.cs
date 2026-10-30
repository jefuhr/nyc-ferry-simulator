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
				Assert.That(map.actions.Count, Is.EqualTo(25));
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
