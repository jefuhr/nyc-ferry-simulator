using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode.Displays
{
	/// <summary>
	/// Hosts one skinned display in its own panel, rendering into a texture of the skin's reference size times
	/// <c>scale</c>: the way a display reaches a console screen. Writes captures to artifacts/display-skins/.
	/// </summary>
	internal sealed class DisplayRig : IDisposable
	{
		private readonly GameObject _host;
		private readonly List<Texture2D> _captures = new List<Texture2D>();

		public DisplayRig(SkinnedDisplayView view, int scale)
		{
			View = view;
			Scale = scale;
			var reference = view.Skin.Reference;
			Target = new RenderTexture(Mathf.RoundToInt(reference.x * scale), Mathf.RoundToInt(reference.y * scale), 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Target.Create();
			Settings = DisplayPanelSettings.Create(view.Skin, Target, Color.magenta);
			_host = new GameObject("DisplayRig " + view.Skin.Display);
			_host.SetActive(false);
			var document = _host.AddComponent<UIDocument>();
			document.panelSettings = Settings;
			_host.SetActive(true);
			document.rootVisualElement.Add(view);
		}

		public SkinnedDisplayView View { get; }

		public int Scale { get; }

		public RenderTexture Target { get; }

		public PanelSettings Settings { get; }

		public static string Folder => Path.Combine(UIRepoPaths.Root, "artifacts", "display-skins");

		public static System.Collections.IEnumerator Frames(int count = 4)
		{
			for (var i = 0; i < count; i++)
			{
				yield return null;
			}
		}

		public Texture2D Capture()
		{
			var previous = RenderTexture.active;
			RenderTexture.active = Target;
			var texture = new Texture2D(Target.width, Target.height, TextureFormat.RGBA32, false);
			_captures.Add(texture);
			texture.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
			texture.Apply();
			RenderTexture.active = previous;
			return texture;
		}

		/// <summary>Writes artifacts/display-skins/<paramref name="fileName"/> and checks the image is not blank.</summary>
		public Texture2D Screenshot(string fileName)
		{
			var texture = Capture();
			Directory.CreateDirectory(Folder);
			File.WriteAllBytes(Path.Combine(Folder, fileName), texture.EncodeToPNG());
			Assert.That(DistinctColours(texture), Is.GreaterThan(16), fileName + " looks blank");
			return texture;
		}

		/// <summary>The pixel at a point given in the skin's reference pixels (origin top left).</summary>
		public Color PixelAt(Texture2D image, Vector2 reference)
		{
			var x = Mathf.Clamp(Mathf.FloorToInt(reference.x * Scale), 0, image.width - 1);
			var y = Mathf.Clamp(image.height - 1 - Mathf.FloorToInt(reference.y * Scale), 0, image.height - 1);
			return image.GetPixel(x, y);
		}

		public void Dispose()
		{
			foreach (var capture in _captures)
			{
				if (capture != null)
				{
					Object.Destroy(capture);
				}
			}

			_captures.Clear();
			View.RemoveFromHierarchy();
			Object.Destroy(_host);
			Object.Destroy(Settings);
			Target.Release();
			Object.Destroy(Target);
		}

		public static int DistinctColours(Texture2D texture)
		{
			var seen = new HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
				if (seen.Count > 64)
				{
					break;
				}
			}

			return seen.Count;
		}
	}
}
