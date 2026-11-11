using System;
using System.Collections.Generic;
using FerrySim.Controls;
using UnityEngine.InputSystem;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// An <see cref="IBindingService"/> without an actions asset: fixed rows, keys the test sets, a rebind the test
	/// finishes by hand, and resets that refuse when the test says so. It records every call the menu makes.
	/// </summary>
	internal sealed class FakeBindingService : IBindingService
	{
		private readonly List<BindingRow> _rows = new List<BindingRow>();
		private readonly Dictionary<string, string> _keys = new Dictionary<string, string>();
		private readonly Dictionary<string, string> _slotKeys = new Dictionary<string, string>();
		private Action<RebindOutcome> _pending;

		public FakeBindingService()
		{
			AddRow(ActionIds.HelmStep, 0, "Keyboard", "Helm to port", "Left Arrow / Right Arrow");
			AddRow(ActionIds.HelmStep, 1, "Keyboard", "Helm to starboard", null);
			AddRow(ActionIds.Horn, 0, "Keyboard", "Horn", "H");
			AddRow(ActionIds.Horn, 0, "Gamepad", "Horn", "Button South");
			AddRow(ActionIds.NfuJog, 0, "Keyboard", "Jog lever to port", "F6 / F7");
			AddRow(ActionIds.NfuJog, 1, "Keyboard", "Jog lever to starboard", null);
			AddRow(ActionIds.BowView, 0, "Keyboard", "Bow View", "B");
			AddRow(ActionIds.FocusCycle, 0, "Keyboard", "Focus Cycle", "F5");
			AddRow(ActionIds.AlarmSilence, 0, "Keyboard", "Silence", "\\");
			AddRow(ActionIds.Options, 0, "Keyboard", "Options", "F10");
			AddRow(ActionIds.Options, 0, "Joystick", "Options", "");
			AddRow(ActionIds.Mute, 0, "Keyboard", "Mute", "V");
		}

		public event Action BindingsChanged;

		public IReadOnlyList<BindingRow> Rows => _rows;

		public bool IsRebinding => _pending != null;

		public string LastBegun { get; private set; }

		public int Cancels { get; private set; }

		public int ResetAlls { get; private set; }

		public List<string> ResetCalls { get; } = new List<string>();

		/// <summary>Action id → the action TryReset names as holding its default control.</summary>
		public Dictionary<string, string> RefuseReset { get; } = new Dictionary<string, string>();

		public void SetKey(string actionId, string scheme, string text)
		{
			_keys[actionId + "|" + scheme] = text;
			BindingsChanged?.Invoke();
		}

		public void Register(InputActionAsset instance)
		{
		}

		public string DisplayString(string actionId, string scheme) => _keys.TryGetValue(actionId + "|" + scheme, out var text) ? text : string.Empty;

		public void BeginRebind(string actionId, int slot, string scheme, Action<RebindOutcome> completed)
		{
			if (completed == null)
			{
				throw new ArgumentNullException(nameof(completed));
			}

			CancelRebind();
			LastBegun = actionId + "|" + slot + "|" + scheme;
			_pending = completed;
		}

		public void CancelRebind()
		{
			if (_pending == null)
			{
				return;
			}

			Cancels++;
			Finish(new RebindOutcome(RebindResult.Cancelled, null));
		}

		/// <summary>Completes the pending rebind as if a key had been pressed.</summary>
		public void Finish(RebindOutcome outcome)
		{
			var pending = _pending;
			_pending = null;
			if (outcome.Result == RebindResult.Applied)
			{
				BindingsChanged?.Invoke();
			}

			pending?.Invoke(outcome);
		}

		public bool TryApply(string actionId, int slot, string scheme, string path, out string conflictActionId)
		{
			conflictActionId = null;
			return true;
		}

		public void Reset(string actionId)
		{
			if (!TryReset(actionId, out var conflict))
			{
				throw new InvalidOperationException("Reset would conflict with " + conflict);
			}
		}

		public void ResetAll()
		{
			ResetAlls++;
			RefuseReset.Clear();
			BindingsChanged?.Invoke();
		}

		public bool TryReset(string actionId, out string conflictActionId)
		{
			ResetCalls.Add(actionId);
			if (RefuseReset.TryGetValue(actionId, out conflictActionId))
			{
				return false;
			}

			conflictActionId = null;
			// Resetting the holder frees the control for whoever was refused because of it.
			var freed = new List<string>();
			foreach (var pair in RefuseReset)
			{
				if (pair.Value == actionId)
				{
					freed.Add(pair.Key);
				}
			}

			foreach (var key in freed)
			{
				RefuseReset.Remove(key);
			}

			BindingsChanged?.Invoke();
			return true;
		}

		/// <summary>Adds a row after the fixed ones (a map the fixed rows do not have, say).</summary>
		public void Add(string actionId, int slot, string scheme, string label, string keys)
		{
			AddRow(actionId, slot, scheme, label, keys);
			BindingsChanged?.Invoke();
		}

		private void AddRow(string actionId, int slot, string scheme, string label, string keys)
		{
			_rows.Add(new BindingRow(actionId, slot, scheme, label));
			if (keys != null)
			{
				_keys[actionId + "|" + scheme] = keys;
			}
		}

		/// <summary>Sets one slot's key text on its own, so a test can make it differ from the combined string.</summary>
		public void SetSlotKey(string actionId, int slot, string scheme, string text)
		{
			_slotKeys[actionId + "|" + slot + "|" + scheme] = text;
			BindingsChanged?.Invoke();
		}

		public string DisplayString(string actionId, int slot, string scheme)
		{
			if (_slotKeys.TryGetValue(actionId + "|" + slot + "|" + scheme, out var own))
			{
				return own;
			}

			var parts = DisplayString(actionId, scheme).Split(new[] { " / " }, StringSplitOptions.None);
			return slot >= 0 && slot < parts.Length ? parts[slot] : string.Empty;
		}
	}
}
