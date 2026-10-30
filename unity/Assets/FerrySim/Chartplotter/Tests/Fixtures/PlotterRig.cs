using System;
using System.IO;
using FerrySim.UI;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Chartplotter.Tests
{
	/// <summary>
	/// Hosts a <see cref="ChartplotterDisplay"/> rendering into a texture of a chosen size, with the synthetic harbor,
	/// a stub own ship and synthetic targets, so the plotter can be checked and photographed in batchmode.
	/// </summary>
	public sealed class PlotterRig : IDisposable
	{
		public static readonly Color Backdrop = new Color(0.36f, 0.45f, 0.54f, 1f);

		private readonly GameObject _host;

		public PlotterRig(int width, int height, Rect placement, bool syntheticChart = true, ChartPaletteKind palette = ChartPaletteKind.Day)
		{
			Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Target.Create();
			Settings = WheelhousePanelSettings.Create(Target, Backdrop);
			_host = new GameObject("PlotterRig");
			_host.SetActive(false);
			Own = _host.AddComponent<StubOwnShip>();
			Own.LoadVessel();
			Targets = new SyntheticTargets();
			Display = _host.AddComponent<ChartplotterDisplay>();
			Display.Configure(Settings);
			Display.SetPlacement(placement);
			if (syntheticChart)
			{
				var runtime = SyntheticHarbor.Runtime();
				Display.Connect(Own, SyntheticHarbor.Chart(runtime), runtime, Targets);
			}
			else
			{
				Display.Connect(Own, null, null, Targets);
			}

			_host.SetActive(true);
			Display.View.Palette = palette;
		}

		public RenderTexture Target { get; }

		public PanelSettings Settings { get; }

		public StubOwnShip Own { get; }

		public SyntheticTargets Targets { get; }

		public ChartplotterDisplay Display { get; }

		public ChartplotterView View => Display.View;

		public static string Repo => UIRepoPaths.Root;

		public static string ArtifactFolder => Path.Combine(Repo, "artifacts", "chartplotter");

		/// <summary>Reads the render target back; the caller destroys the texture.</summary>
		public Texture2D Capture()
		{
			var previous = RenderTexture.active;
			RenderTexture.active = Target;
			var texture = new Texture2D(Target.width, Target.height, TextureFormat.RGBA32, false);
			texture.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
			texture.Apply();
			RenderTexture.active = previous;
			return texture;
		}

		/// <summary>Writes artifacts/chartplotter/<paramref name="fileName"/> and returns the image (the caller destroys it).</summary>
		public Texture2D Screenshot(string fileName)
		{
			var texture = Capture();
			Directory.CreateDirectory(ArtifactFolder);
			File.WriteAllBytes(Path.Combine(ArtifactFolder, fileName), texture.EncodeToPNG());
			return texture;
		}

		/// <summary>Mean relative luminance over a rectangle of the image (whole image by default), 0 to 1.</summary>
		public static double MeanLuminance(Texture2D texture, RectInt? area = null)
		{
			var pixels = texture.GetPixels32();
			var rect = area ?? new RectInt(0, 0, texture.width, texture.height);
			double sum = 0;
			long count = 0;
			for (var y = rect.yMin; y < rect.yMax; y++)
			{
				for (var x = rect.xMin; x < rect.xMax; x++)
				{
					var p = pixels[y * texture.width + x];
					sum += Luminance(new Color(p.r / 255f, p.g / 255f, p.b / 255f));
					count++;
				}
			}

			return count > 0 ? sum / count : 0;
		}

		public static int DistinctColours(Texture2D texture, int limit = 256)
		{
			var seen = new System.Collections.Generic.HashSet<int>();
			foreach (var pixel in texture.GetPixels32())
			{
				seen.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
				if (seen.Count > limit)
				{
					break;
				}
			}

			return seen.Count;
		}

		/// <summary>WCAG 2.x relative luminance of an sRGB colour.</summary>
		public static double Luminance(Color color) => 0.2126 * Channel(color.r) + 0.7152 * Channel(color.g) + 0.0722 * Channel(color.b);

		private static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

		public static double Contrast(Color a, Color b)
		{
			var la = Luminance(a);
			var lb = Luminance(b);
			return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
		}

		public void Dispose()
		{
			Object.Destroy(_host);
			Object.Destroy(Settings);
			Target.Release();
			Object.Destroy(Target);
		}
	}
}
