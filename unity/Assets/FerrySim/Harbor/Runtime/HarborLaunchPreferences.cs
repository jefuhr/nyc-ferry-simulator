using System;
using FerrySim.UI;

namespace FerrySim.Harbor
{
	/// <summary>Launch-only presentation choices use temporary stores before the UI creates its view.</summary>
	public static class HarborLaunchPreferences
	{
		public static SettingsStore TemporarySettings(string[] arguments)
		{
			return Array.IndexOf(arguments, "-ferrysim-route") >= 0 || Array.IndexOf(arguments, "-ferrysim-smoke") >= 0 ||
				Array.IndexOf(arguments, "-ferrysim-console-screens") >= 0 || Array.IndexOf(arguments, "-ferrysim-hud-preset") >= 0
				? SettingsStore.InMemory() : null;
		}
		public static bool Prepare(WheelhouseUI ui, string[] arguments)
		{
			if (ui == null) return false;
			var settings = TemporarySettings(arguments);
			if (settings == null) return false;
			ui.ConfigureStores(settings, HudLayoutStore.InMemory());
			return true;
		}
	}
}
