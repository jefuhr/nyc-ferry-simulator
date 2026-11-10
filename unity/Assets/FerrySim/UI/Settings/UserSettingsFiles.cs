using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// The session's settings and saved HUD layouts, shared by every <see cref="WheelhouseUI"/> in the process. In a player
	/// or the editor they are <c>settings-v1.json</c> and <c>hud-layouts-v1.json</c> in the persistent data folder, loaded
	/// once. In batch mode (test runs, benchmarks, smoke runs) each request gets fresh stores kept in memory, so an automated
	/// run never reads the player's own files nor writes over them, and one test's choices never reach the next. Tests and
	/// hosts may set their own with <see cref="SetSession"/>.
	/// </summary>
	public static class UserSettingsFiles
	{
		private static SettingsStore _settings;
		private static HudLayoutStore _layouts;

		public static SettingsStore Settings
		{
			get
			{
				if (_settings == null && Application.isBatchMode)
				{
					return SettingsStore.InMemory();
				}

				EnsureLoaded();
				return _settings;
			}
		}

		public static HudLayoutStore Layouts
		{
			get
			{
				if (_layouts == null && Application.isBatchMode)
				{
					return HudLayoutStore.InMemory();
				}

				EnsureLoaded();
				return _layouts;
			}
		}

		public static void SetSession(SettingsStore settings, HudLayoutStore layouts)
		{
			_settings = settings ?? SettingsStore.InMemory();
			_layouts = layouts ?? HudLayoutStore.InMemory();
		}

		public static void ResetSession()
		{
			_settings = null;
			_layouts = null;
		}

		private static void EnsureLoaded()
		{
			if (_settings != null && _layouts != null)
			{
				return;
			}

			_settings ??= new SettingsStore(SettingsStore.DefaultPath);
			_layouts ??= new HudLayoutStore(HudLayoutStore.DefaultPath);
			if (!_settings.Load())
			{
				Debug.LogWarning("[FerrySim] " + _settings.LastError);
			}

			if (!_layouts.Load())
			{
				Debug.LogWarning("[FerrySim] " + _layouts.LastError);
			}
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void OnSubsystemRegistration() => ResetSession();
	}
}
