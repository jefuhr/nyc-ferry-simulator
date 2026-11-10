using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// Supplies the pictures the HUD's pop-out screens show: one texture per console screen id (<c>radar</c>, <c>plotter</c>,
	/// <c>engine-port</c>, <c>engine-starboard</c>, <c>alarms</c>; see <see cref="HudWidgetIds.Screens"/>), or none. It is
	/// asked every frame while a pop-out is shown, so an implementation must answer without allocating. Nothing supplies
	/// one yet; with none, or no texture for a screen, the pop-out shows a neutral NO SIGNAL face.
	/// </summary>
	public interface IHudScreenSource
	{
		/// <summary>The screen's current picture; false (and null) when there is none.</summary>
		bool TryGetScreen(string screenId, out Texture texture);
	}
}
