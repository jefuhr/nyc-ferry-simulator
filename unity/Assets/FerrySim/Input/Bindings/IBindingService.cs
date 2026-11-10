using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	/// <summary>One rebindable binding of one action: what the options menu lists as a row.</summary>
	public readonly struct BindingRow
	{
		public BindingRow(string actionId, int slot, string scheme, string label)
		{
			ActionId = actionId;
			Slot = slot;
			Scheme = scheme;
			Label = label;
		}

		/// <summary>"Map/Action", as in <see cref="ActionIds"/>.</summary>
		public string ActionId { get; }

		/// <summary>
		/// Which binding of the action within <see cref="Scheme"/>, counting from zero. Each part of a
		/// composite (for example the port and starboard sides of a step pair) is its own slot.
		/// </summary>
		public int Slot { get; }

		/// <summary>Control scheme: "Keyboard", "Gamepad" or "Joystick".</summary>
		public string Scheme { get; }

		/// <summary>Text for the menu, for example "Jog lever to port".</summary>
		public string Label { get; }
	}

	/// <summary>How an interactive rebind ended.</summary>
	public enum RebindResult
	{
		Applied,
		Cancelled,

		/// <summary>The chosen control is already used in a context that is live at the same time; nothing changed.</summary>
		Conflict,
	}

	/// <summary>The result of <see cref="IBindingService.BeginRebind"/>.</summary>
	public readonly struct RebindOutcome
	{
		public RebindOutcome(RebindResult result, string conflictActionId)
		{
			Result = result;
			ConflictActionId = conflictActionId;
		}

		public RebindResult Result { get; }

		/// <summary>The action already holding the control when <see cref="Result"/> is <see cref="RebindResult.Conflict"/>; otherwise null.</summary>
		public string ConflictActionId { get; }
	}

	/// <summary>
	/// The one place key and button bindings are changed, saved and looked up. The simulator
	/// instantiates the actions asset more than once (helm, displays, audio), so every instance is
	/// registered here and an override reaches all of them. Analog axis assignment and calibration stay
	/// with the device profiles and are not part of this service.
	/// </summary>
	public interface IBindingService
	{
		/// <summary>Raised after any binding changes, including a reset and the initial load. Popups and menus refresh their text on it.</summary>
		event Action BindingsChanged;

		/// <summary>Adds an instance of the actions asset. Saved overrides are applied to it at once and kept in step afterwards.</summary>
		void Register(InputActionAsset instance);

		/// <summary>Every rebindable binding, grouped by map in asset order. The list is fixed for the life of the service.</summary>
		IReadOnlyList<BindingRow> Rows { get; }

		/// <summary>
		/// What to show for an action in a scheme, for example "F6 / F7" or "Q / A". Empty when the action
		/// has no binding in that scheme. Reflects overrides.
		/// </summary>
		string DisplayString(string actionId, string scheme);

		/// <summary>True between <see cref="BeginRebind"/> and its callback.</summary>
		bool IsRebinding { get; }

		/// <summary>
		/// Listens for the next key or button and binds it to the given slot. The callback runs exactly
		/// once. A control that conflicts is refused and reported, never applied.
		/// </summary>
		void BeginRebind(string actionId, int slot, string scheme, Action<RebindOutcome> completed);

		/// <summary>Stops listening; the pending callback runs with <see cref="RebindResult.Cancelled"/>.</summary>
		void CancelRebind();

		/// <summary>
		/// Binds a control path (for example "&lt;Keyboard&gt;/f6") to a slot without listening. Returns false
		/// and names the holder when the path conflicts; nothing changes in that case.
		/// </summary>
		bool TryApply(string actionId, int slot, string scheme, string path, out string conflictActionId);

		/// <summary>Restores one action's bindings to the defaults in the asset.</summary>
		void Reset(string actionId);

		/// <summary>Restores every binding to the defaults in the asset.</summary>
		void ResetAll();

		/// <summary>Restores one action's defaults unless another binding now holds a default control. On conflict nothing changes.</summary>
		bool TryReset(string actionId, out string conflictActionId);
	}
}
