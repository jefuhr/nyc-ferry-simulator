using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Panel settings for the wheelhouse UI, created at runtime. The layout is designed at 1280×720 and
	/// scales with the screen (2× at 2560×1440), matching width and height equally so other aspect
	/// ratios still fit.
	/// </summary>
	public static class WheelhousePanelSettings
	{
		public const string ThemeResource = "FerrySimUI/WheelhouseTheme";

		public static readonly Vector2Int ReferenceResolution = new Vector2Int(1280, 720);

		/// <param name="target">Render into this texture instead of the screen (tests, screenshots); null for the screen.</param>
		/// <param name="clear">Background for a texture target; ignored on screen, where the camera image shows through.</param>
		public static PanelSettings Create(RenderTexture target = null, Color clear = default)
		{
			var settings = ScriptableObject.CreateInstance<PanelSettings>();
			settings.name = "Wheelhouse panel settings (runtime)";
			var theme = Resources.Load<ThemeStyleSheet>(ThemeResource);
			if (theme == null)
			{
				Debug.LogWarning($"WheelhousePanelSettings: theme Resources/{ThemeResource}.tss is missing; text will not render.");
			}

			settings.themeStyleSheet = theme;
			settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
			settings.referenceResolution = ReferenceResolution;
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
