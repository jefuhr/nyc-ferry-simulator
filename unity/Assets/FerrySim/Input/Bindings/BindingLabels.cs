using System.Collections.Generic;

namespace FerrySim.Controls
{
	/// <summary>Player-facing row and group names. Action identifiers and slot order remain the input contract.</summary>
	public static class BindingLabels
	{
		// Slots count rebindable bindings within a scheme, in asset order. A repeated pair gets an alternate label.
		private static readonly Dictionary<string, string[]> Rows = new Dictionary<string, string[]>
		{
			{ ActionIds.PortLeverStep, new[] { "Port engine astern", "Port engine ahead" } },
			{ ActionIds.StarboardLeverStep, new[] { "Starboard engine astern", "Starboard engine ahead" } },
			{ ActionIds.BothLeversStep, new[] { "Both engines astern", "Both engines ahead" } },
			{ ActionIds.Horn, new[] { "Horn" } },
			{ ActionIds.Pause, new[] { "Pause simulation" } },
			{ ActionIds.Reset, new[] { "Restart run" } },
			{ ActionIds.CameraCycle, new[] { "Cycle camera view" } },
			{ ActionIds.NewRun, new[] { "New run" } },
			{ ActionIds.HelmStep, new[] { "Tiller to port", "Tiller to starboard" } },
			{ ActionIds.Midships, new[] { "Tiller to midships" } },
			{ ActionIds.NfuJog, new[] { "Jog lever to port", "Jog lever to starboard", "Jog lever to port (alternate)", "Jog lever to starboard (alternate)" } },

			{ "Displays/ChartplotterToggle", new[] { "Show or hide chart" } },
			{ "Displays/RadarToggle", new[] { "Show or hide radar" } },
			{ "Displays/ChartZoomIn", new[] { "Chart zoom in" } },
			{ "Displays/ChartZoomOut", new[] { "Chart zoom out" } },
			{ "Displays/ChartOrientation", new[] { "Change chart orientation" } },
			{ "Displays/ChartCentre", new[] { "Centre chart on vessel" } },
			{ "Displays/ChartPanUp", new[] { "Chart pan up" } },
			{ "Displays/ChartPanLeft", new[] { "Chart pan left" } },
			{ "Displays/ChartPanDown", new[] { "Chart pan down" } },
			{ "Displays/ChartPanRight", new[] { "Chart pan right" } },
			{ "Displays/ChartPalette", new[] { "Change chart colours" } },
			{ "Displays/ChartNextTarget", new[] { "Select next chart target" } },
			{ "Displays/ChartClearSelection", new[] { "Clear chart selection" } },
			{ "Displays/RadarRangeUp", new[] { "Increase radar range" } },
			{ "Displays/RadarRangeDown", new[] { "Decrease radar range" } },
			{ "Displays/RadarOrientation", new[] { "Change radar orientation" } },
			{ "Displays/RadarPalette", new[] { "Change radar colours" } },
			{ "Displays/RadarEblLeft", new[] { "Radar bearing line left" } },
			{ "Displays/RadarEblRight", new[] { "Radar bearing line right" } },
			{ "Displays/RadarVrmIn", new[] { "Radar range marker in" } },
			{ "Displays/RadarVrmOut", new[] { "Radar range marker out" } },
			{ "Displays/RadarGainDown", new[] { "Decrease radar gain" } },
			{ "Displays/RadarGainUp", new[] { "Increase radar gain" } },
			{ "Displays/RadarSeaDown", new[] { "Decrease radar sea clutter filter" } },
			{ "Displays/RadarSeaUp", new[] { "Increase radar sea clutter filter" } },
			{ "Displays/ChartCpaLimitDown", new[] { "Decrease chart closest approach distance" } },
			{ "Displays/ChartCpaLimitUp", new[] { "Increase chart closest approach distance" } },
			{ "Displays/ChartTcpaLimitDown", new[] { "Decrease chart closest approach time" } },
			{ "Displays/ChartTcpaLimitUp", new[] { "Increase chart closest approach time" } },
			{ "Displays/ChartAlarmMute", new[] { "Mute or unmute chart collision warning" } },

			{ ActionIds.Mute, new[] { "Mute or unmute audio" } },
			{ ActionIds.ViewFreeFly, new[] { "Free-fly view" } },
			{ ActionIds.ViewChase, new[] { "Chase view" } },
			{ ActionIds.ViewWheelhouse, new[] { "Wheelhouse view" } },
			{ ActionIds.BowView, new[] { "Bow view" } },
			{ ActionIds.BowAutoToggle, new[] { "Automatic bow view" } },
			{ ActionIds.FocusCycle, new[] { "Focus next screen" } },
			{ ActionIds.LookHold, new[] { "Look around (hold)" } },
			{ ActionIds.Recentre, new[] { "Centre camera view" } },
			{ ActionIds.FreeFlyMove, new[] { "Fly forward", "Fly backward", "Fly left", "Fly right" } },
			{ ActionIds.FreeFlyVertical, new[] { "Fly down", "Fly up" } },
			{ ActionIds.FreeFlyFast, new[] { "Fly faster" } },
			{ ActionIds.FreeFlySlow, new[] { "Fly slower" } },
			{ ActionIds.AlarmAcknowledge, new[] { "Acknowledge alarms" } },
			{ ActionIds.AlarmSilence, new[] { "Silence alarms" } },
			{ ActionIds.AlarmLampTest, new[] { "Lamp test" } },
			{ ActionIds.Options, new[] { "Options menu" } },
			{ ActionIds.KeybindHints, new[] { "Show key hints" } },
			{ ActionIds.HudEdit, new[] { "Edit head-up display" } },
		};

		private static readonly Dictionary<string, string> Groups = new Dictionary<string, string>
		{
			{ "Helm", "Helm" },
			{ "Displays", "Displays" },
			{ "Audio", "Audio" },
			{ "Camera", "Camera" },
			{ "FreeFly", "Free-fly camera" },
			{ "Alarms", "Alarms" },
			{ "Interface", "Interface" },
		};

		/// <summary>False for an unknown action or slot, so the caller can keep its generated fallback.</summary>
		public static bool TryGetRowLabel(string actionId, int slot, out string label)
		{
			label = null;
			if (slot < 0 || !Rows.TryGetValue(actionId, out var labels) || slot >= labels.Length) return false;
			label = labels[slot];
			return true;
		}

		/// <summary>The heading for a raw map name. Unknown maps keep their name.</summary>
		public static string GroupName(string mapName) => Groups.TryGetValue(mapName, out var label) ? label : mapName;
	}
}
