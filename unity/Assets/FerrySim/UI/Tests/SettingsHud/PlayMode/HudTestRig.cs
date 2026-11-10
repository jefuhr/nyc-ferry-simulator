using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.SettingsHud.PlayMode
{
	/// <summary>
	/// Hosts the real <see cref="WheelhouseUI"/> with the committed crash-stop sample and renders it into a render texture,
	/// like the UI suite's rig, with the settings and layouts kept in memory (or in the stores a test passes). Screenshots go
	/// to artifacts/settings-options-hud/.
	/// </summary>
	internal sealed class HudTestRig : IDisposable
	{
		public static readonly Color DayBackdrop = new Color(0.36f, 0.45f, 0.54f, 1f);
		public static readonly Color NightBackdrop = new Color(0.02f, 0.004f, 0.002f, 1f);

		private readonly GameObject _host;
		private readonly List<Texture2D> _captures = new List<Texture2D>();

		public HudTestRig(int width, int height, SettingsStore settings = null, HudLayoutStore layouts = null, Action<WheelhouseUI> configure = null)
		{
			Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Target.Create();
			Settings = WheelhousePanelSettings.Create(Target, DayBackdrop);
			_host = new GameObject("HudTestRig");
			_host.SetActive(false);
			Source = _host.AddComponent<FakeTelemetrySource>();
			UI = _host.AddComponent<WheelhouseUI>();
			UI.Configure(Source, Settings);
			if (settings != null || layouts != null)
			{
				UI.ConfigureStores(settings, layouts);
			}

			configure?.Invoke(UI);
			_host.SetActive(true);
			Source.IsPlaying = false;
			Source.SetScenarioStart("2026-06-15T13:00:00Z");
			Assert.That(Source.TryLoadCsv(UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv)), Is.True, Source.LoadError);
			Source.Seek(46.0);
		}

		public RenderTexture Target { get; }

		public PanelSettings Settings { get; }

		public FakeTelemetrySource Source { get; }

		public WheelhouseUI UI { get; }

		public WheelhouseView View => UI.View;

		public static string ArtifactFolder => Path.Combine(UIRepoPaths.Root, "artifacts", "settings-options-hud");

		public void SetPalette(PaletteKind palette)
		{
			View.Palette = palette;
			Settings.colorClearValue = palette == PaletteKind.Night ? NightBackdrop : DayBackdrop;
		}

		public static IEnumerator Frames(int count = 4)
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

		/// <summary>Writes artifacts/settings-options-hud/<paramref name="fileName"/> and checks the image is not blank.</summary>
		public Texture2D Screenshot(string fileName, bool expectContent = true)
		{
			var texture = Capture();
			var path = Path.Combine(ArtifactFolder, "captures", fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllBytes(path, texture.EncodeToPNG());
			if (expectContent)
			{
				Assert.That(DistinctColours(texture), Is.GreaterThan(16), fileName + " looks blank");
			}
			return texture;
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
			// At once, so the UI lets go of its input actions before an input test fixture resets the Input System.
			Object.DestroyImmediate(_host);
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

	/// <summary>Navigation as the Input System's UI module sends it, to whatever has the focus in the element's panel.</summary>
	internal static class Nav
	{
		public static VisualElement Focused(VisualElement anyElement) => anyElement.panel?.focusController?.focusedElement as VisualElement;

		public static void Move(VisualElement anyElement, NavigationMoveEvent.Direction direction)
		{
			using (var evt = NavigationMoveEvent.GetPooled(direction))
			{
				(Focused(anyElement) ?? anyElement).SendEvent(evt);
			}
		}

		public static void Submit(VisualElement anyElement)
		{
			using (var evt = NavigationSubmitEvent.GetPooled())
			{
				(Focused(anyElement) ?? anyElement).SendEvent(evt);
			}
		}

		public static void Cancel(VisualElement anyElement)
		{
			using (var evt = NavigationCancelEvent.GetPooled())
			{
				(Focused(anyElement) ?? anyElement).SendEvent(evt);
			}
		}

		public static void Key(VisualElement anyElement, KeyCode key)
		{
			var target = Focused(anyElement) ?? anyElement;
			using (var evt = KeyDownEvent.GetPooled('\0', key, EventModifiers.None))
			{
				target.SendEvent(evt);
			}

			using (var evt = KeyUpEvent.GetPooled('\0', key, EventModifiers.None))
			{
				target.SendEvent(evt);
			}
		}
	}

	/// <summary>WCAG 2.x relative luminance and contrast of sRGB colors.</summary>
	internal static class Contrast
	{
		public static double Ratio(double la, double lb) => (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

		public static double Luminance(Color color) => 0.2126 * Linear(color.r) + 0.7152 * Linear(color.g) + 0.0722 * Linear(color.b);

		private static double Linear(float channel) => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

		/// <summary>
		/// Checks every visible text under <paramref name="root"/> against its ancestors' backgrounds over a black and over a
		/// white 3D view, taking the worse. Text in the palette's main text color must reach <paramref name="text"/>, any
		/// other <paramref name="other"/>. Returns how many were checked.
		/// </summary>
		public static int CheckVisibleText(VisualElement root, InstrumentPalette palette, double text, double other)
		{
			var checkedCount = 0;
			root.Query<TextElement>().ForEach(element =>
			{
				if (string.IsNullOrEmpty(element.text) || element.GetFirstAncestorOfType<TextField>() != null || !IsVisible(element))
				{
					return;
				}

				checkedCount++;
				var color = element.resolvedStyle.color;
				var foreground = Luminance(color);
				var worst = Math.Min(Ratio(foreground, Background(element, 0.0)), Ratio(foreground, Background(element, 1.0)));
				var required = Same(color, palette.Text) || Same(color, palette.EngagedText) || Same(color, palette.CautionText) ? text : other;
				Assert.That(worst, Is.GreaterThanOrEqualTo(required), $"'{element.text}' ({element.name ?? element.GetType().Name}) color {color}: {worst:F2}:1");
			});
			return checkedCount;
		}

		private static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

		private static bool IsVisible(VisualElement element)
		{
			if (element.resolvedStyle.visibility != Visibility.Visible || element.worldBound.width < 1f)
			{
				return false;
			}

			for (var e = element; e != null; e = e.parent)
			{
				if (e.resolvedStyle.display == DisplayStyle.None || e.resolvedStyle.opacity <= 0f)
				{
					return false;
				}
			}

			return true;
		}

		private static double Background(VisualElement element, double view)
		{
			var chain = new List<VisualElement>();
			for (var e = element; e != null; e = e.parent)
			{
				chain.Add(e);
			}

			var luminance = view;
			for (var i = chain.Count - 1; i >= 0; i--)
			{
				var background = chain[i].resolvedStyle.backgroundColor;
				if (background.a > 0f)
				{
					luminance = background.a * Luminance(background) + (1.0 - background.a) * luminance;
				}
			}

			return luminance;
		}
	}
}
