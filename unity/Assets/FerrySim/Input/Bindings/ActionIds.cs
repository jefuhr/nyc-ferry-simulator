namespace FerrySim.Controls
{
	/// <summary>
	/// Identifiers of the input actions other assemblies refer to, written "Map/Action" exactly as in
	/// FerrySimControls.inputactions. The options menu, the keybind popups and the wheelhouse control
	/// map use these instead of literals. The Displays map is not listed: its actions are reached
	/// through <see cref="IBindingService.Rows"/>.
	/// </summary>
	public static class ActionIds
	{
		// Helm (existing).
		public const string PortLever = "Helm/PortLever";
		public const string StarboardLever = "Helm/StarboardLever";
		public const string BothLevers = "Helm/BothLevers";
		public const string Helm = "Helm/Helm";
		public const string PortLeverStep = "Helm/PortLeverStep";
		public const string StarboardLeverStep = "Helm/StarboardLeverStep";
		public const string BothLeversStep = "Helm/BothLeversStep";
		public const string HelmStep = "Helm/HelmStep";
		public const string Midships = "Helm/Midships";
		public const string Horn = "Helm/Horn";
		public const string Pause = "Helm/Pause";
		public const string Reset = "Helm/Reset";
		public const string CameraCycle = "Helm/CameraCycle";
		public const string NewRun = "Helm/NewRun";

		// Helm (new in v0.5): the jog lever, a button pair producing -1, 0 or +1.
		public const string NfuJog = "Helm/NfuJog";

		// Camera (new map; these were read straight from devices before v0.5).
		public const string ViewFreeFly = "Camera/ViewFreeFly";
		public const string ViewChase = "Camera/ViewChase";
		public const string ViewWheelhouse = "Camera/ViewWheelhouse";
		public const string BowView = "Camera/BowView";
		public const string BowAutoToggle = "Camera/BowAutoToggle";
		public const string LookHold = "Camera/LookHold";
		public const string LookDelta = "Camera/LookDelta";
		public const string Recentre = "Camera/Recentre";
		public const string FocusCycle = "Camera/FocusCycle";

		// FreeFly (new map; enabled only in the free-fly view, while the Helm map is disabled).
		public const string FreeFlyMove = "FreeFly/Move";
		public const string FreeFlyVertical = "FreeFly/Vertical";
		public const string FreeFlyFast = "FreeFly/Fast";
		public const string FreeFlySlow = "FreeFly/Slow";

		// Alarms (new map).
		public const string AlarmAcknowledge = "Alarms/Acknowledge";
		public const string AlarmSilence = "Alarms/Silence";
		public const string AlarmLampTest = "Alarms/LampTest";

		// Interface (new map).
		public const string Options = "Interface/Options";
		public const string KeybindHints = "Interface/KeybindHints";
		public const string HudEdit = "Interface/HudEdit";

		// Audio (existing).
		public const string Mute = "Audio/Mute";
	}
}
