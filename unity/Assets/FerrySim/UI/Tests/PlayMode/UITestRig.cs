using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Hosts the real <see cref="WheelhouseUI"/> with a <see cref="FakeTelemetrySource"/> and renders it into
	/// a render texture of a chosen size, so the UI can be checked and photographed in batchmode.
	/// </summary>
	internal sealed class UITestRig : IDisposable
	{
		public static readonly Color DayBackdrop = new Color(0.36f, 0.45f, 0.54f, 1f);
		// Near-black, warm, and distinct from the night console (pure black) so the pixel checks can tell them apart.
		public static readonly Color NightBackdrop = new Color(0.02f, 0.004f, 0.002f, 1f);

		public static readonly DateTimeOffset ScenarioStart = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);

		private readonly GameObject _host;
		private readonly List<Texture2D> _captures = new List<Texture2D>();

		public UITestRig(int width, int height, bool withSource = true, bool devToolbar = false)
		{
			Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Target.Create();
			Settings = WheelhousePanelSettings.Create(Target, DayBackdrop);
			_host = new GameObject("UITestRig");
			_host.SetActive(false);
			if (withSource)
			{
				Source = _host.AddComponent<FakeTelemetrySource>();
			}

			UI = _host.AddComponent<WheelhouseUI>();
			UI.Configure(Source, Settings, devToolbar);
			_host.SetActive(true);
			if (Source != null)
			{
				Source.IsPlaying = false;
				Source.SetScenarioStart("2026-06-15T13:00:00Z");
			}
		}

		public RenderTexture Target { get; }

		public PanelSettings Settings { get; }

		public FakeTelemetrySource Source { get; }

		public WheelhouseUI UI { get; }

		public WheelhouseView View => UI.View;

		public static string RepoRoot => UIRepoPaths.Root;

		public static string SamplePath => UIRepoPaths.Resolve(UIRepoPaths.SampleCrashStopCsv);

		public void SetPalette(PaletteKind palette)
		{
			View.Palette = palette;
			Settings.colorClearValue = palette == PaletteKind.Night ? NightBackdrop : DayBackdrop;
		}

		/// <summary>Lets the UI update, lay out (twice, for labels placed from geometry) and render.</summary>
		public static IEnumerator Frames(int count = 4)
		{
			for (var i = 0; i < count; i++)
			{
				yield return null;
			}
		}

		/// <summary>Reads the render target back. The rig owns the texture and destroys it on <see cref="Dispose"/>, even if the test never does.</summary>
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

		/// <summary>Writes artifacts/unity/<paramref name="fileName"/> and checks the image is not blank.</summary>
		public Texture2D Screenshot(string fileName)
		{
			var texture = Capture();
			var path = Path.Combine(RepoRoot, "artifacts", "unity", fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllBytes(path, texture.EncodeToPNG());
			WriteLabelSnapshot(fileName);
			Assert.That(DistinctColours(texture), Is.GreaterThan(16), fileName + " looks blank");
			return texture;
		}


		/// <summary>Companion evidence for comparing original and optimized UI content/layout independently
		/// of dynamic font-atlas glyph-edge rasterization. Captures every Label, including hidden labels.</summary>
		private void WriteLabelSnapshot(string fileName)
		{
			var labels = new List<LabelSnapshot>();
			SnapshotLabels(View, "view", true, labels);
			var path = Path.Combine(RepoRoot, "artifacts", "health", "ui-labels", Path.GetFileNameWithoutExtension(fileName) + ".json");
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, JsonUtility.ToJson(new LabelSnapshotFile { labels = labels.ToArray() }, true));
		}

		private static void SnapshotLabels(VisualElement element, string path, bool parentDisplayed, List<LabelSnapshot> labels)
		{
			var resolved = element.resolvedStyle;
			var displayed = parentDisplayed && resolved.display != DisplayStyle.None;
			if (element is Label label)
			{
				labels.Add(new LabelSnapshot
				{
					path = path,
					text = label.text,
					display = resolved.display.ToString(),
					visibility = resolved.visibility.ToString(),
					visible = displayed && resolved.visibility == Visibility.Visible,
					layout = RectText(element.layout),
					worldBounds = RectText(element.worldBound),
				});
			}
			for (var i = 0; i < element.hierarchy.childCount; i++)
			{
				var child = element.hierarchy[i];
				SnapshotLabels(child, path + "/" + child.GetType().Name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]:" + child.name, displayed, labels);
			}
		}

		private static string RectText(Rect rect) => string.Format(CultureInfo.InvariantCulture,
			"{0:R},{1:R},{2:R},{3:R}", rect.x, rect.y, rect.width, rect.height);

		[Serializable]
		private sealed class LabelSnapshotFile
		{
			public LabelSnapshot[] labels;
		}

		[Serializable]
		private sealed class LabelSnapshot
		{
			public string path;
			public string text;
			public string display;
			public string visibility;
			public bool visible;
			public string layout;
			public string worldBounds;
		}

		/// <summary>Destroys everything the rig made, including captures a failed assertion left behind; written screenshots stay.</summary>
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
			Object.Destroy(_host);
			Object.Destroy(Settings);
			Target.Release();
			Object.Destroy(Target);
		}

		private static int DistinctColours(Texture2D texture)
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

	/// <summary>
	/// A deliberately independent reader for the recorded CSV text, so the PlayMode test compares what the
	/// panel shows against the file itself rather than against the parser it is also exercising.
	/// </summary>
	internal sealed class RawCsv
	{
		private readonly Dictionary<string, int> _columns = new Dictionary<string, int>();
		private readonly List<string[]> _rows = new List<string[]>();

		public RawCsv(string text)
		{
			var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0 && !line.StartsWith("#")).ToArray();
			var names = lines[0].Split(',');
			for (var i = 0; i < names.Length; i++)
			{
				_columns[names[i]] = i;
			}

			foreach (var line in lines.Skip(1))
			{
				_rows.Add(line.Split(','));
			}
		}

		public int Count => _rows.Count;

		public double Number(int row, string column) => double.Parse(_rows[row][_columns[column]], CultureInfo.InvariantCulture);

		public string Text(int row, string column) => _rows[row][_columns[column]];

		public int FirstRow(string column, string value)
		{
			for (var i = 0; i < _rows.Count; i++)
			{
				if (Text(i, column) == value)
				{
					return i;
				}
			}

			return -1;
		}
	}

	internal static class SyntheticRuns
	{
		/// <summary>
		/// The recorded crash stop with a shallow patch and a fender contact written over it, for showing the
		/// alarm and contact displays. The label says so, and it is never saved as data.
		/// </summary>
		public static TelemetryRecording CrashStopWithShallowsAndContact(TelemetryRecording source, double shallowStart, double shallowEnd, double contactStart, double contactEnd)
		{
			var builder = new TelemetryRecordingBuilder(source.Label + " + synthetic shallows and contact (UI test only)", source.Header);
			for (var i = 0; i < source.Count; i++)
			{
				var sample = source.Sample(i);
				var t = sample.Telemetry;
				var time = sample.State.Time;
				var depth = time >= shallowStart && time < shallowEnd ? 1.2 : t.DepthUnderKeel;
				var force = time >= contactStart && time < contactEnd ? 35000.0 - 20000.0 * Math.Abs(time - (contactStart + contactEnd) * 0.5) : 0.0;
				builder.Add(sample.State,
					new VesselTelemetry(t.Port, t.Starboard, t.PortRudderAngle, t.StarboardRudderAngle,
						t.SpeedThroughWater, t.SpeedOverGround, t.CourseOverGround, depth, Math.Max(0.0, force)),
					sample.Controls);
			}

			return builder.Build();
		}

		/// <summary>
		/// The recorded crash stop with a fender touch written into the single row at <paramref name="time"/>:
		/// shorter than the gap between two rendered frames once playback skips rows. Labelled, never saved.
		/// </summary>
		public static TelemetryRecording CrashStopWithOneRowContact(TelemetryRecording source, double time, double force)
		{
			var builder = new TelemetryRecordingBuilder(source.Label + " + synthetic one-row contact (UI test only)", source.Header);
			for (var i = 0; i < source.Count; i++)
			{
				var sample = source.Sample(i);
				var t = sample.Telemetry;
				builder.Add(sample.State,
					new VesselTelemetry(t.Port, t.Starboard, t.PortRudderAngle, t.StarboardRudderAngle, t.SpeedThroughWater,
						t.SpeedOverGround, t.CourseOverGround, t.DepthUnderKeel, Math.Abs(sample.State.Time - time) < 1e-6 ? force : 0.0),
					sample.Controls);
			}

			return builder.Build();
		}
	}

	internal static class TurnRuns
	{
		/// <summary>
		/// The T-class from rest: both levers at 60% for 60 s, then the helm hard over to starboard for 20 s,
		/// in calm water, recorded at 10 Hz like the CLI.
		/// </summary>
		public static TelemetryRecording HardOverToStarboard()
		{
			var parameters = VesselFiles.TryLoad("t-class", out var problem);
			Assert.That(parameters, Is.Not.Null, problem);
			var simulation = new VesselDynamicsFactory().Create(parameters, UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			var header = new[]
			{
				new KeyValuePair<string, string>("ferrysim-csv", "1"),
				new KeyValuePair<string, string>("vessel", "t-class"),
				new KeyValuePair<string, string>("maneuver", "in-test turn (levers 0.6 for 60 s, then helm 1 for 20 s)"),
				new KeyValuePair<string, string>("environment", "calm"),
			};
			var builder = new TelemetryRecordingBuilder("in-test turn", header);
			const int steps = 80 * SimulationClock.StepsPerSecond;
			for (var step = 0; step <= steps; step++)
			{
				var time = SimulationClock.TimeAtStep(step);
				var controls = new ControlInputs(0.6, 0.6, time >= 60.0 ? 1.0 : 0.0);
				if (step % 10 == 0)
				{
					builder.Add(simulation.State, simulation.Telemetry, controls);
				}

				if (step < steps)
				{
					simulation.Step(controls);
				}
			}

			return builder.Build();
		}
	}
}
