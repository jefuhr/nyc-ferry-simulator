using System;
using FerrySim.UI;
using FerrySim.UI.Displays;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Console
{
	/// <summary>A retained offscreen panel. Visibility suppresses painting without collapsing its layout.</summary>
	public sealed class ScreenPanel : IDisposable
	{
		private readonly GameObject _host;
		private bool _rendering;
		public ScreenPanel(Transform parent, string id, int width, int height, DisplaySkin skin = null)
		{
			Target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
			{
				name = "Console " + id, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
				useMipMap = false, autoGenerateMips = false
			};
			Target.Create();
			var previous = RenderTexture.active; RenderTexture.active = Target; GL.Clear(false, true, Color.black); RenderTexture.active = previous;
			Settings = skin == null ? WheelhousePanelSettings.Create(Target) : DisplayPanelSettings.Create(skin, Target, Color.black);
			Settings.referenceResolution = new Vector2Int(width, height);
			Settings.clearColor = false; Settings.clearDepthStencil = false;
			_host = new GameObject("Console panel " + id); _host.transform.SetParent(parent, false);
			Document = _host.AddComponent<UIDocument>(); Document.panelSettings = Settings;
			Root = Document.rootVisualElement;
			Root.style.width = width; Root.style.height = height;
			Root.style.backgroundColor = Color.black;
			Root.style.visibility = Visibility.Hidden;
			Root.style.opacity = 0;
		}
		public RenderTexture Target { get; }
		public PanelSettings Settings { get; }
		public UIDocument Document { get; }
		public VisualElement Root { get; }
		public long ColourBytes => (long)Target.width * Target.height * 4;
		public void SetRendering(bool rendering)
		{
			if (_rendering == rendering) return;
			_rendering = rendering;
			// A child may explicitly set visibility:visible (chart labels, dial needles), overriding inherited
			// visibility. Opacity multiplies down the whole subtree and prevents those pixels accumulating.
			Root.style.opacity = rendering ? 1 : 0;
			Root.style.visibility = rendering ? Visibility.Visible : Visibility.Hidden;
		}
		public void Dispose()
		{
			Document.panelSettings = null;
			Release(_host); Release(Settings); Target.Release(); Release(Target);
		}
		internal static void Release(Object value) { if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value); }
	}
}
