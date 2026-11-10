using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>Panel settings for one display rendered into a texture: the skin's reference size scaled to the texture.</summary>
	public static class DisplayPanelSettings
	{
		/// <param name="target">The texture the display renders into, ideally the reference size or a whole multiple of it.</param>
		/// <param name="clear">Behind the display; it fills the texture, so this shows only if the aspect ratios differ.</param>
		public static PanelSettings Create(DisplaySkin skin, RenderTexture target, Color clear)
		{
			if (skin == null)
			{
				throw new ArgumentNullException(nameof(skin));
			}

			var settings = ScriptableObject.CreateInstance<PanelSettings>();
			settings.name = "Display panel settings " + skin.Display + "/" + skin.Id + " (runtime)";
			var theme = Resources.Load<ThemeStyleSheet>(WheelhousePanelSettings.ThemeResource);
			if (theme == null)
			{
				Debug.LogWarning("DisplayPanelSettings: theme Resources/" + WheelhousePanelSettings.ThemeResource + ".tss is missing; text will not render.");
			}

			settings.themeStyleSheet = theme;
			settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
			settings.referenceResolution = new Vector2Int(Mathf.RoundToInt(skin.Reference.x), Mathf.RoundToInt(skin.Reference.y));
			settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
			settings.match = 0.5f;
			if (target != null)
			{
				settings.targetTexture = target;
				settings.clearColor = true;
				settings.colorClearValue = clear;
			}

			return settings;
		}
	}
}
