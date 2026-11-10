using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Prompts.Tests.PlayMode
{
	/// <summary>
	/// The popups over the real slice with the real interior prefabs of both classes, a binding service with no
	/// saved file, and the UI rendered to its own transparent target and laid over the camera's image.
	/// Failure modes guarded: a label that is not over its control (wrong projection, panel scale, or a stale
	/// interior after a class switch), plates unreadable over a bright window or in the night palette, an update
	/// over its 0.15 ms budget or making garbage, and a rebind or the lean-in view not reaching the popups.
	/// Writes artifacts/keybind-popups/ (captures, label positions, frame cost).
	/// </summary>
	public class KeybindPopupsPlayModeTests
	{
		private const string ScenePath = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";
		private const string SceneName = "LowerEastRiverSlice";
		private const string DayStart = "2025-07-20T17:00:00Z";
		private const string NightStart = "2025-07-21T02:30:00Z";
		private const double DayAA = 4.5;
		private const double NightMinimum = 3.0;

		private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/keybind-popups"));

		private readonly List<Object> _temporary = new List<Object>();
		private Scene _previous;
		private SliceBootstrap _slice;
		private InputBindingService _service;
		private bool _automaticBow;
		private Camera _camera;

		[OneTimeSetUp]
		public void StartSeatViewNotes()
		{
			Directory.CreateDirectory(Output);
			File.Delete(Path.Combine(Output, "seat-view.txt"));
		}

		[UnitySetUp]
		public IEnumerator LoadSlice()
		{
			_previous = SceneManager.GetActiveScene();
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
#else
			yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
#endif
			SceneManager.SetActiveScene(SceneManager.GetSceneByName(SceneName));
			yield return null;
			yield return null;
			_slice = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(_slice, Is.Not.Null);
			Assert.That(_slice.LoadError, Is.Null);
			_slice.Runner.RecordRuns = false;
			_camera = _slice.CameraRig.GetComponent<Camera>();
			_automaticBow = WheelhouseCameraController.AutomaticBowEnabled;
			WheelhouseCameraController.AutomaticBowEnabled = false;
			var defaults = InputActionAsset.FromJson(Resources.Load<TextAsset>("FerrySimInput/BindingDefaults").text);
			_service = new InputBindingService(defaults);
			Object.Destroy(defaults);
			Directory.CreateDirectory(Path.Combine(Output, "captures"));
		}

		[UnityTearDown]
		public IEnumerator Unload()
		{
			foreach (var item in _temporary)
			{
				if (item is RenderTexture texture)
				{
					texture.Release();
				}

				if (item != null)
				{
					Object.Destroy(item);
				}
			}

			_temporary.Clear();
			_service?.Dispose();
			_service = null;
			WheelhouseCameraController.AutomaticBowEnabled = _automaticBow;
			if (_camera != null)
			{
				_camera.ResetAspect();
			}

			if (_previous.IsValid() && _previous.isLoaded)
			{
				SceneManager.SetActiveScene(_previous);
			}

			var scene = SceneManager.GetSceneByName(SceneName);
			if (scene.IsValid() && scene.isLoaded)
			{
				yield return SceneManager.UnloadSceneAsync(scene);
			}
		}

		[UnityTest]
		public IEnumerator EveryMappedControl_HasItsLabelJustAboveIt_FromTheSeatedEye_BothClasses()
		{
			var report = new StringBuilder();
			foreach (var id in new[] { "t-class", "k-class" })
			{
				var view = StartClass(id, night: false);
				yield return null;
				var host = CreateHost(1280, 720);
				host.Popups.Bind(_camera, view, _service);
				host.Popups.Mode = KeybindHintMode.Always;
				var controller = _slice.CameraRig.Wheelhouse;
				var mapped = view.ControlMap.Entries.Where(e => e.Actions.Count > 0).ToList();
				Assert.That(mapped.Select(e => e.MountId), Is.EquivalentTo(new[] { "PortLever", "StarboardLever", "HelmFFU", "PortNFU", "HornButton", "AlarmAcknowledge", "AlarmSilence", "LampTest" }),
					"controls-v1.json gives actions to these mounts");
				foreach (var entry in mapped)
				{
					controller.Recentre();
					controller.HeadLook = HeadLookAt(controller, entry.Transform.position);
					controller.Snap();
					yield return null;
					yield return null;
					Assert.That(host.Popups.Layer.TryGetState(entry.MountId, out var state), Is.True, entry.MountId);
					Assert.That(state.KeyText, Is.Not.Empty, $"{id} {entry.MountId}: keyboard binding");
					Assert.That(state.Visible, Is.True, $"{id} {entry.MountId}: shown when looked at from the seat");
					var anchor = Project(host.Popups.Layer, entry.Transform.position);
					Assert.That(Vector2.Distance(state.Anchor, anchor), Is.LessThan(1f), $"{id} {entry.MountId}: anchor is the mount's projection");
					var plate = host.Popups.Layer.PlateElement(entry.MountId).worldBound;
					Assert.That(Vector2.Distance(plate.min, state.Plate.min), Is.LessThan(1.5f), $"{id} {entry.MountId}: drawn where placed {plate} vs {state.Plate}");
					var above = anchor.y - plate.yMax;
					var leader = Vector2.Distance(anchor, new Vector2(plate.center.x, plate.yMax));
					Assert.That(above, Is.GreaterThanOrEqualTo(0f), $"{id} {entry.MountId}: above the mount {plate} anchor {anchor}");
					var home = Mathf.Abs(plate.center.x - anchor.x) < 1f && Mathf.Abs(above - KeybindPopupLayer.LeaderLength) < 0.5f;
					if (!home)
					{
						// Moved because its home would cover a plate or pivot nearer the centre: at most one plate away.
						Assert.That(leader, Is.LessThanOrEqualTo(Mathf.Max(plate.width, plate.height) + KeybindPopupLayer.LeaderLength + 10f), $"{id} {entry.MountId}: a short leader {plate} anchor {anchor}");
					}

					report.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1,-17} key '{2}' head look {3:F1},{4:F1} anchor {5:F1},{6:F1} plate {7:F1},{8:F1} {9:F1}x{10:F1} gap above {11:F1} px leader {12:F1} px {13}",
						id, entry.MountId, state.KeyText, controller.HeadLook.x, controller.HeadLook.y, anchor.x, anchor.y, plate.x, plate.y, plate.width, plate.height, above, leader,
						home ? "home" : "nudged"));
				}

				DestroyHost(host);
			}

			File.WriteAllText(Path.Combine(Output, "label-positions.txt"), report.ToString());
		}

		[UnityTest]
		public IEnumerator Update_StaysWithinItsBudget_AndAllocatesNothing_WithEveryMappedControlInView()
		{
			var view = StartClass("t-class", night: false);
			yield return null;
			var host = CreateHost(1280, 720);
			host.Popups.Bind(_camera, view, _service);
			host.Popups.Mode = KeybindHintMode.Always;
			var controller = _slice.CameraRig.Wheelhouse;
			PoseWithEveryControlInView(view);
			var basePosition = _camera.transform.position;
			var baseRotation = _camera.transform.rotation;
			var mapped = view.ControlMap.Entries.Count(e => e.Actions.Count > 0);
			yield return null;
			yield return null;
			Assert.That(host.Popups.Layer.ControlCount, Is.EqualTo(mapped));

			var samples = new List<long>();
			using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, KeybindPopups.MarkerName, 1))
			{
				for (var frame = 0; frame < 330; frame++)
				{
					_camera.transform.SetPositionAndRotation(basePosition, baseRotation * Quaternion.Euler(Mathf.Sin(frame * 0.05f) * 1f, Mathf.Sin(frame * 0.07f) * 1.5f, 0f));
					yield return null;
					AssertAllInView(view);
					if (frame >= 30)
					{
						samples.Add(recorder.LastValue);
					}
				}

				Assert.That(recorder.Valid, Is.True, "the marker recorder");
			}

			samples.Sort();
			var p95 = samples[(int)(samples.Count * 0.95)] / 1e6;
			var median = samples[samples.Count / 2] / 1e6;
			var max = samples[samples.Count - 1] / 1e6;
			var nonZero = samples.Count(s => s > 0);
			var lines = string.Format(CultureInfo.InvariantCulture,
				"marker {0}\nframes {1} (camera turning, all {2} mapped controls in view, {3} shown)\nmedian {4:F4} ms\np95 {5:F4} ms\nmax {6:F4} ms\nnon-zero samples {7}\nbudget p95 <= 0.15 ms\n",
				KeybindPopups.MarkerName, samples.Count, mapped, host.Popups.Layer.VisibleCount, median, p95, max, nonZero);
			File.WriteAllText(Path.Combine(Output, "frame-cost.txt"), lines);
			Assert.That(nonZero, Is.GreaterThan(samples.Count * 9 / 10), "the marker records every frame\n" + lines);
			Assert.That(p95, Is.LessThanOrEqualTo(0.15), lines);

			// Steady state, the camera still turning: the driver's whole per-frame update makes no garbage. The camera
			// is turned directly so only the popups' own work is measured.
			var step = 0;
			void Turn()
			{
				for (var i = 0; i < 120; i++, step++)
				{
					_camera.transform.rotation = baseRotation * Quaternion.Euler(Mathf.Sin(step * 0.05f) * 1f, Mathf.Sin(step * 0.07f) * 1.5f, 0f);
					host.Popups.Tick();
				}
			}

			Turn();
			Assert.That(host.Popups.Layer.VisibleCount, Is.GreaterThan(0));
			Assert.That(Turn, Is.Not.AllocatingGCMemory());
			controller.enabled = true;
			DestroyHost(host);
		}

		[UnityTest]
		public IEnumerator Captures_DayAndNight_BothClasses_BothResolutions_ARebindAndTheLeanInView_AreReadable()
		{
			var report = new StringBuilder();
			foreach (var id in new[] { "t-class", "k-class" })
			{
				foreach (var night in new[] { false, true })
				{
					var view = StartClass(id, night);
					// Let the scene's light and sky settle after the class switch (the first capture of a run was washed out).
					yield return Frames(30);
					var controller = _slice.CameraRig.Wheelhouse;
					// From the seat the mapped controls span about 140 degrees of yaw, so no one view holds them all: the
					// console (the seat pose with the most in view), the engine levers to starboard and the horn and alarm
					// buttons to port. Between them every mapped control is photographed.
					var poses = new[]
					{
						("console", AllInViewPose(controller, view)),
						("levers", HeadLookAt(controller, Centroid(view, "PortLever", "StarboardLever"))),
						("buttons", HeadLookAt(controller, Centroid(view, "HornButton", "AlarmAcknowledge", "AlarmSilence", "LampTest"))),
					};
					foreach (var (where, pose) in poses)
					{
						foreach (var (width, height) in new[] { (1280, 720), (2560, 1440) })
						{
							var host = CreateHost(width, height);
							host.Popups.Bind(_camera, view, _service);
							host.Popups.Mode = KeybindHintMode.Always;
							host.Popups.Palette = night ? PaletteKind.Night : PaletteKind.Day;
							controller.HeadLook = pose;
							controller.Snap();
							yield return Frames(4);
							var name = string.Format(CultureInfo.InvariantCulture, "{0}-{1}-eye-{2}-always-{3}p", id, night ? "night" : "day", where, height);
							Assert.That(host.Popups.Layer.VisibleCount, Is.GreaterThanOrEqualTo(2), name + ": controls in view are labelled");
							CaptureAndCheck(host, name, night, report);
							DestroyHost(host);
						}
					}

					if (id == "t-class" && !night)
					{
						var host = CreateHost(1280, 720);
						host.Popups.Bind(_camera, view, _service);
						host.Popups.Mode = KeybindHintMode.Always;
						controller.HeadLook = poses[2].Item2;
						controller.Snap();
						yield return Frames(4);
						Assert.That(host.Popups.Layer.TryGetState("HornButton", out var before), Is.True);
						Assert.That(before.KeyText, Is.EqualTo("H"));
						Assert.That(_service.TryApply(ActionIds.Horn, 0, "Keyboard", "<Keyboard>/f11", out var holder), Is.True, holder);
						Assert.That(host.Popups.Layer.TryGetState("HornButton", out var after), Is.True);
						Assert.That(after.KeyText, Is.EqualTo("F11"), "the rebind reaches the popup at once");
						yield return Frames(3);
						CaptureAndCheck(host, "t-class-day-rebind-horn-f11-720p", false, report);
						_service.ResetAll();

						controller.Recentre();
						controller.CycleFocus();
						Assert.That(controller.FocusedScreen, Is.EqualTo("RadarScreen"));
						for (var frame = 0; frame < 150; frame++)
						{
							controller.AdvancePresentation(1f / 60f);
						}

						yield return Frames(4);
						Assert.That(host.Popups.Layer.IsShowing, Is.True, "the lean-in view is part of the wheelhouse view");
						CaptureAndCheck(host, "t-class-day-lean-in-radar-720p", false, report);
						controller.Recentre();
						DestroyHost(host);
					}
				}
			}

			File.WriteAllText(Path.Combine(Output, "captures", "labels.txt"), report.ToString());
		}

		[UnityTest]
		public IEnumerator Plates_KeepTheirTextContrast_OverWhiteAndBlack()
		{
			// The plates over a pure white and a pure black backdrop (the UI target cleared to it), so the measure does
			// not depend on what the window shows. Day text must reach WCAG AA (4.5:1); night text the night rule (3:1).
			var view = StartClass("t-class", night: false);
			yield return null;
			var controller = _slice.CameraRig.Wheelhouse;
			var pose = AllInViewPose(controller, view);
			var measured = 0;
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				foreach (var backdrop in new[] { Color.white, Color.black })
				{
					var host = CreateHost(1280, 720, backdrop);
					host.Popups.Bind(_camera, view, _service);
					host.Popups.Mode = KeybindHintMode.Always;
					host.Popups.Palette = palette;
					controller.HeadLook = pose;
					controller.Snap();
					yield return Frames(4);
					var image = Read(host.Target);
					var colors = InstrumentPalette.For(palette);
					var minimum = palette == PaletteKind.Night ? NightMinimum : DayAA;
					for (var i = 0; i < host.Popups.Layer.ControlCount; i++)
					{
						var state = host.Popups.Layer.GetState(i);
						if (!state.Visible)
						{
							continue;
						}

						var plate = SamplePlate(image, state, 1f);
						foreach (var (what, color) in new[] { ("key", colors.Text), ("caption", KeybindPopupLayer.CaptionColor(colors)) })
						{
							var ratio = Contrast.Ratio(color, plate);
							Assert.That(ratio, Is.GreaterThanOrEqualTo(minimum), $"{palette} {what} on {state.MountId}'s plate over {backdrop}: {ratio:F2}:1");
							measured++;
						}
					}

					// Every visible text element, from its resolved colour and the backgrounds behind it (as ContrastPlayModeTests).
					host.Popups.Layer.Query<Label>().ForEach(label =>
					{
						if (string.IsNullOrEmpty(label.text) || !IsVisible(label))
						{
							return;
						}

						var ratio = Contrast.Ratio(label.resolvedStyle.color, BehindLabel(label, backdrop));
						Assert.That(ratio, Is.GreaterThanOrEqualTo(minimum), $"{palette} '{label.text}' {label.resolvedStyle.color}: {ratio:F2}:1");
					});
					DestroyHost(host);
				}
			}

			Assert.That(measured, Is.GreaterThanOrEqualTo(16));
		}

		private WheelhouseControlsView StartClass(string id, bool night)
		{
			_slice.Runner.Initialize(new ScenarioSettings
			{
				vesselFile = "vessels/" + id + ".json", startLatitude = 40.7027, startLongitude = -74.0022, startHeadingDegrees = 25,
				scenarioStartUtc = night ? NightStart : DayStart, environment = new ScenarioEnvironment { calm = true },
			}, new ConstantControls(ControlInputs.Neutral));
			_slice.Runner.Pause();
			var view = _slice.Runner.GetComponentInChildren<WheelhouseControlsView>();
			Assert.That(view, Is.Not.Null);
			Assert.That(view.ClassId, Is.EqualTo(id));
			Assert.That(view.ControlMap, Is.Not.Null, id + " control map");
			_slice.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			_camera.aspect = 16f / 9f;
			_slice.CameraRig.Wheelhouse.Recentre();
			_slice.CameraRig.Wheelhouse.Snap();
			return view;
		}

		private sealed class Host
		{
			public GameObject GameObject;
			public UIDocument Document;
			public KeybindPopups Popups;
			public RenderTexture Target;
			public PanelSettings Settings;
		}

		private Host CreateHost(int width, int height, Color? backdrop = null)
		{
			var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			target.Create();
			var settings = WheelhousePanelSettings.Create(target, backdrop ?? new Color(0f, 0f, 0f, 0f));
			var gameObject = new GameObject("keybind popups (" + width + "x" + height + ")");
			gameObject.SetActive(false);
			var document = gameObject.AddComponent<UIDocument>();
			document.panelSettings = settings;
			var popups = gameObject.AddComponent<KeybindPopups>();
			gameObject.SetActive(true);
			popups.AttachTo(document);
			var host = new Host { GameObject = gameObject, Document = document, Popups = popups, Target = target, Settings = settings };
			_temporary.Add(gameObject);
			_temporary.Add(settings);
			_temporary.Add(target);
			return host;
		}

		private static void DestroyHost(Host host)
		{
			host.Popups.Unbind();
			host.GameObject.SetActive(false);
		}

		private static IEnumerator Frames(int count)
		{
			for (var i = 0; i < count; i++)
			{
				yield return null;
			}
		}

		private static Vector2 HeadLookAt(WheelhouseCameraController controller, Vector3 target)
		{
			var mount = controller.Mount.transform;
			var direction = Quaternion.Inverse(mount.rotation) * (target - mount.position);
			var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
			var pitch = -Mathf.Atan2(direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg;
			return new Vector2(yaw, pitch);
		}

		/// <summary>
		/// The head look, within the seat's limits, that comes closest to keeping every mapped control in view (the
		/// widest worst-case margin). Records the margin and any control left outside to seat-view.txt: from the
		/// seated eye the whole set does not quite fit (measured during this task), so callers do not require it.
		/// </summary>
		private Vector2 AllInViewPose(WheelhouseCameraController controller, WheelhouseControlsView view)
		{
			var mapped = view.ControlMap.Entries.Where(e => e.Actions.Count > 0).Select(e => e.Transform).ToList();
			var best = new Vector2(float.NaN, float.NaN);
			var bestMargin = float.MinValue;
			for (var pitch = 0f; pitch <= 60f; pitch += 1f)
			{
				for (var yaw = -60f; yaw <= 60f; yaw += 1f)
				{
					controller.HeadLook = new Vector2(yaw, pitch);
					controller.Snap();
					var margin = float.MaxValue;
					foreach (var mount in mapped)
					{
						var viewport = _camera.WorldToViewportPoint(mount.position);
						margin = Mathf.Min(margin, viewport.z <= 0f ? -1f : Mathf.Min(Mathf.Min(viewport.x, 1f - viewport.x), Mathf.Min(viewport.y, 1f - viewport.y)));
					}

					if (margin > bestMargin)
					{
						bestMargin = margin;
						best = controller.HeadLook;
					}
				}
			}

			controller.HeadLook = best;
			controller.Snap();
			var outside = mapped.Where(m =>
			{
				var v = _camera.WorldToViewportPoint(m.position);
				return v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f;
			}).Select(m => m.name + " " + _camera.WorldToViewportPoint(m.position).ToString("F3")).ToList();
			File.AppendAllText(Path.Combine(Output, "seat-view.txt"), string.Format(CultureInfo.InvariantCulture,
				"{0}: best seat head look {1:F0},{2:F0} (yaw, pitch down) fov {3:F1}; worst viewport margin {4:F4}; outside: {5}\n",
				view.ClassId, best.x, best.y, _camera.fieldOfView, bestMargin, outside.Count == 0 ? "none" : string.Join("; ", outside)));
			Assert.That(outside.Count, Is.LessThan(mapped.Count), "some mapped controls are in view from the seat");
			return best;
		}

		private static Vector3 Centroid(WheelhouseControlsView view, params string[] mounts)
		{
			var sum = Vector3.zero;
			foreach (var mount in mounts)
			{
				var entry = view.ControlMap.Entries.Single(e => e.MountId == mount);
				sum += entry.Transform.position;
			}

			return sum / mounts.Length;
		}

		/// <summary>
		/// Takes the camera from the controller, aims it from the seated eye at the middle of the mapped controls and
		/// backs it away along that line until every mapped control is inside the view with a 3% margin. For the
		/// budget only: from the seat itself no view holds them all (they span about 140 degrees of yaw).
		/// </summary>
		private void PoseWithEveryControlInView(WheelhouseControlsView view)
		{
			var controller = _slice.CameraRig.Wheelhouse;
			controller.Recentre();
			controller.Snap();
			controller.enabled = false;
			Assert.That(_slice.CameraRig.CurrentMode, Is.EqualTo(CameraRig.Mode.Wheelhouse), "the rig still says wheelhouse");
			var mapped = view.ControlMap.Entries.Where(e => e.Actions.Count > 0).Select(e => e.Transform).ToList();
			var centroid = Vector3.zero;
			foreach (var mount in mapped)
			{
				centroid += mount.position;
			}

			centroid /= mapped.Count;
			var mountTransform = controller.Mount.transform;
			var eye = mountTransform.position;
			var up = mountTransform.parent != null ? mountTransform.parent.up : Vector3.up;
			_camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(centroid - eye, up));
			for (var back = 0; back <= 150; back++)
			{
				var fits = mapped.All(m =>
				{
					var v = _camera.WorldToViewportPoint(m.position);
					return v.z > 0f && v.x > 0.03f && v.x < 0.97f && v.y > 0.03f && v.y < 0.97f;
				});
				if (fits)
				{
					File.AppendAllText(Path.Combine(Output, "seat-view.txt"), string.Format(CultureInfo.InvariantCulture,
						"{0}: budget pose aimed from the eye at the controls' centroid and backed {1:F2} m along the view line\n", view.ClassId, back * 0.02f));
					return;
				}

				_camera.transform.position -= _camera.transform.forward * 0.02f;
			}

			Assert.Fail("no pose within 3 m of the seat, aimed at the controls, shows every mapped control");
		}

		private void AssertAllInView(WheelhouseControlsView view)
		{
			foreach (var entry in view.ControlMap.Entries)
			{
				if (entry.Actions.Count == 0)
				{
					continue;
				}

				var viewport = _camera.WorldToViewportPoint(entry.Transform.position);
				Assert.That(viewport.z > 0f && viewport.x > 0f && viewport.x < 1f && viewport.y > 0f && viewport.y < 1f, Is.True, entry.MountId + " in view");
			}
		}

		private Vector2 Project(VisualElement layer, Vector3 world)
		{
			var viewport = _camera.WorldToViewportPoint(world);
			var size = layer.layout.size;
			return new Vector2(viewport.x * size.x, (1f - viewport.y) * size.y);
		}

		private void CaptureAndCheck(Host host, string name, bool night, StringBuilder report)
		{
			var image = Composite(host.Target);
			File.WriteAllBytes(Path.Combine(Output, "captures", name + ".png"), image.EncodeToPNG());
			var scale = host.Target.height / 720f;
			var colors = InstrumentPalette.For(night ? PaletteKind.Night : PaletteKind.Day);
			var minimum = night ? NightMinimum : DayAA;
			report.AppendLine(name);
			var shown = 0;
			for (var i = 0; i < host.Popups.Layer.ControlCount; i++)
			{
				var state = host.Popups.Layer.GetState(i);
				var line = string.Format(CultureInfo.InvariantCulture, "  {0,-17} '{1}' / '{2}' {3} plate {4:F1},{5:F1} {6:F1}x{7:F1} anchor {8:F1},{9:F1} scale {10:F2}",
					state.MountId, state.KeyText, state.Caption, state.Visible ? "shown " : "hidden", state.Plate.x, state.Plate.y, state.Plate.width, state.Plate.height, state.Anchor.x, state.Anchor.y, state.Scale);
				if (state.Visible)
				{
					shown++;
					var plate = SamplePlate(image, state, scale);
					var key = Contrast.Ratio(colors.Text, plate);
					var caption = Contrast.Ratio(KeybindPopupLayer.CaptionColor(colors), plate);
					line += string.Format(CultureInfo.InvariantCulture, " contrast key {0:F2} caption {1:F2}", key, caption);
					Assert.That(Math.Min(key, caption), Is.GreaterThanOrEqualTo(minimum), name + " " + line);
				}

				report.AppendLine(line);
			}

			Assert.That(shown, Is.GreaterThan(0), name);
			Object.Destroy(image);
		}

		/// <summary>The plate's own colour as drawn: inside the border, in the padding above the key text.</summary>
		private static Color SamplePlate(Texture2D image, KeybindPopupState state, float scale)
		{
			var x = Mathf.RoundToInt((state.Plate.xMin + 5f * state.Scale) * scale);
			var y = Mathf.RoundToInt((state.Plate.yMin + 2.5f * state.Scale) * scale);
			return image.GetPixel(x, image.height - 1 - y);
		}

		/// <summary>The camera's view with the UI drawn over it (the UI renders premultiplied to its own transparent target).</summary>
		private Texture2D Composite(RenderTexture ui)
		{
			var scene = new RenderTexture(ui.width, ui.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			var previous = _camera.targetTexture;
			try
			{
				_camera.targetTexture = scene;
				_camera.Render();
			}
			finally
			{
				_camera.targetTexture = previous;
			}

			var image = Read(scene);
			var overlay = Read(ui);
			scene.Release();
			Object.Destroy(scene);
			var under = image.GetPixels32();
			var top = overlay.GetPixels32();
			for (var i = 0; i < under.Length; i++)
			{
				var a = top[i].a / 255f;
				under[i] = new Color32(
					(byte)Mathf.Clamp(top[i].r + under[i].r * (1f - a), 0f, 255f),
					(byte)Mathf.Clamp(top[i].g + under[i].g * (1f - a), 0f, 255f),
					(byte)Mathf.Clamp(top[i].b + under[i].b * (1f - a), 0f, 255f),
					255);
			}

			image.SetPixels32(under);
			image.Apply();
			Object.Destroy(overlay);
			return image;
		}

		private static Texture2D Read(RenderTexture source)
		{
			var previous = RenderTexture.active;
			var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
			RenderTexture.active = source;
			texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
			texture.Apply();
			RenderTexture.active = previous;
			return texture;
		}

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

		/// <summary>The colour behind a label: its own and its ancestors' backgrounds, outermost first, over the backdrop.</summary>
		private static Color BehindLabel(VisualElement element, Color backdrop)
		{
			var chain = new List<VisualElement>();
			for (var e = element; e != null; e = e.parent)
			{
				chain.Add(e);
			}

			var color = backdrop;
			for (var i = chain.Count - 1; i >= 0; i--)
			{
				var background = chain[i].resolvedStyle.backgroundColor;
				var opacity = chain[i].resolvedStyle.opacity;
				var a = background.a * opacity;
				if (a > 0f)
				{
					color = Color.Lerp(color, new Color(background.r, background.g, background.b, 1f), a);
				}
			}

			return color;
		}
	}

	/// <summary>WCAG 2.x relative luminance and contrast of sRGB colours.</summary>
	internal static class Contrast
	{
		public static double Ratio(Color a, Color b)
		{
			var la = Luminance(a);
			var lb = Luminance(b);
			return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
		}

		public static double Luminance(Color color) => 0.2126 * Linear(color.r) + 0.7152 * Linear(color.g) + 0.0722 * Linear(color.b);

		private static double Linear(float channel) => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
	}
}
