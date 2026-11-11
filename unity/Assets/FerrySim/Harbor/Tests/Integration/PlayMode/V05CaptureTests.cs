using System.Collections;
using System.IO;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Harbor.Tests.Integration.PlayMode
{
	/// <summary>
	/// Real harbor, authored focus mounts and shared live pictures, at 1920 by 1080. Editor evidence only.
	/// The first capture waits for Settle after scene/palette/layout changes so cold text atlases and shaders
	/// have rendered before reading the targets; preserve that wait when extending these captures.
	/// </summary>
	public class V05CaptureTests
	{
		private RenderTexture _uiTarget;
		private PanelSettings _panel;
		private string _folder;
#if UNITY_EDITOR
		private bool _asynchronous, _shaderConfigured;
#endif
		[UnityTearDown] public IEnumerator Unload()
		{
			yield return HarborSceneFixture.Unload();
#if UNITY_EDITOR
			if (_shaderConfigured) ShaderUtil.allowAsyncCompilation = _asynchronous;
#endif
			if (_panel != null) Object.Destroy(_panel);
			if (_uiTarget != null) { _uiTarget.Release(); Object.Destroy(_uiTarget); }
		}

		[UnityTest, Explicit("1920x1080 real-harbor visual evidence; select this test explicitly")]
		public IEnumerator DayAndNight_ClassicEye_AllFocusMounts_ConsolePopouts_OptionsAndHints()
		{
			HarborSceneFixture.RequireData();
#if UNITY_EDITOR
			_asynchronous = ShaderUtil.allowAsyncCompilation; _shaderConfigured = true; ShaderUtil.allowAsyncCompilation = false;
#endif
			_folder = Path.Combine(HarborSceneFixture.Repo, "artifacts/v05-integration/captures");
			Directory.CreateDirectory(_folder);
			yield return HarborSceneFixture.Load();
			var bootstrap = HarborSceneFixture.Bootstrap();
			var services = (HarborIntegrationServices)bootstrap.Services;
			var runner = bootstrap.Runner; runner.RecordRuns = false;
			var ui = bootstrap.WheelhouseUI;
			_uiTarget = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_uiTarget.Create(); _panel = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
			ui.enabled = false; ui.Configure(runner, _panel); ui.enabled = true;
			ui.DaylightSource = bootstrap.Context.Sun;
			yield return null;
			var camera = bootstrap.CameraRig.GetComponent<Camera>(); camera.aspect = 1920f / 1080;
			foreach (var night in new[] { false, true })
			{
				var prefix = night ? "night" : "day";
				var choice = new ScenarioChoice(HarborSceneFixture.Pier11StopId, HarborSceneFixture.TrafficDay,
					night ? 180 : HarborSceneFixture.BusiestLocalMinute, services.HarborCatalog.Vessels[0].Id, traffic: true);
				Assert.That(services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
				Assert.That(bootstrap.TryStart(settings, out problem), Is.True, problem);
				ui.View.PickerVisible = false;
				yield return HarborSceneFixture.WaitReady(bootstrap, prefix);
				var began = Time.realtimeSinceStartupAsDouble;
				while (services.Displays.Screens?.Plotter == null || runner.State.Time < 4)
				{
					Assert.That(Time.realtimeSinceStartupAsDouble - began, Is.LessThan(180));
					yield return null;
				}
				runner.Pause();
				var view = ui.View;
				view.Settings.Palette = PaletteSetting.Auto;
				view.Settings.KeybindHints = KeybindHintMode.Off;
				view.HudLayout.Choose(HudPresets.Classic);
				var wheelhouse = bootstrap.CameraRig.Wheelhouse;
				wheelhouse.Recentre(); wheelhouse.Snap();
				yield return Settle(); Capture(camera, prefix + "-eye-classic");
				for (var screen = 0; screen < 4; screen++)
				{
					wheelhouse.CycleFocus(); wheelhouse.AdvancePresentation(3); wheelhouse.Snap();
					view.HudLayout.Choose(HudPresets.Classic);
					yield return Settle(); Capture(camera, prefix + "-focus-" + wheelhouse.FocusedScreen + "-classic");
					view.HudLayout.Choose(HudPresets.Console);
					yield return Settle(); Capture(camera, prefix + "-focus-" + wheelhouse.FocusedScreen + "-console");
				}
				wheelhouse.Recentre(); wheelhouse.Snap();
				view.HudLayout.Choose(HudPresets.Console);
				view.HudLayout.SetSessionShown("screen:radar", true);
				view.HudLayout.SetSessionShown("screen:plotter", true);
				yield return Settle(); Capture(camera, prefix + "-console-two-popouts");
				view.HudLayout.Choose(HudPresets.Classic); runner.Resume(); view.OptionsVisible = true;
				Assert.That(runner.IsPaused, Is.True);
				yield return Settle(); Capture(camera, prefix + "-options-paused");
				view.OptionsVisible = false; runner.Pause();
				view.Settings.KeybindHints = KeybindHintMode.Always;
				wheelhouse.HeadLook = new Vector2(-60, 35); wheelhouse.Snap();
				yield return Settle();
				Assert.That(services.Displays.Presentation.Hints.Layer.VisibleCount, Is.GreaterThan(0));
				Capture(camera, prefix + "-key-hints");
			}
			File.WriteAllText(Path.Combine(_folder, "scope.txt"), "24 editor captures at 1920x1080, Pier 11 with live traffic, day and night. Lean-in poses come from each prefab FocusEye. Classic remains the first-launch preset. Pictures and UI are composited from their actual render targets. This is visual integration evidence, not player performance or physical-display acceptance.\n");
		}

		private static IEnumerator Settle() { for (var frame = 0; frame < 120; frame++) yield return null; }
		private void Capture(Camera camera, string name)
		{
						var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var target = RenderTexture.GetTemporary(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Texture2D world = null, overlay = null;
			try
			{
				camera.targetTexture = target; camera.Render(); world = Read(target); overlay = Read(_uiTarget);
				var pixels = world.GetPixels32(); var top = overlay.GetPixels32();
				for (var i = 0; i < pixels.Length; i++)
				{
					var a = top[i].a / 255f;
					pixels[i] = new Color32((byte)Mathf.Min(255, top[i].r + pixels[i].r * (1 - a)),
						(byte)Mathf.Min(255, top[i].g + pixels[i].g * (1 - a)), (byte)Mathf.Min(255, top[i].b + pixels[i].b * (1 - a)), 255);
				}
				world.SetPixels32(pixels); world.Apply(); File.WriteAllBytes(Path.Combine(_folder, name + ".png"), world.EncodeToPNG());
			}
			finally
			{
								camera.targetTexture = previous; RenderTexture.active = active;
				if (world != null) Object.Destroy(world); if (overlay != null) Object.Destroy(overlay);
				RenderTexture.ReleaseTemporary(target);
			}
		}
		private static Texture2D Read(RenderTexture target)
		{
			RenderTexture.active = target;
			var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); return image;
		}
	}
}
