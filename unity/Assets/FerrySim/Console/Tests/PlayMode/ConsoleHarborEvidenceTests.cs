#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Harbor;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.Core.Contracts;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

namespace FerrySim.Console.Tests
{
	// Failure modes: display composition changes recorded states, real mounts go blank, focus poses clip text,
	// editor timing substitutes frame delta for CPU time, or compares unlike percentiles.
	public class ConsoleHarborEvidenceTests
	{
		private const string ScenePath = "Assets/FerrySim/Harbor/Scenes/Harbor.unity";
		private static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/console-screens/cost-follow-up"));
		private HarborBootstrap _bootstrap;
		private HarborIntegrationServices _services;
		private ConsoleScreens _screens;
		private Camera _camera;
		private Transform _interior;
		private VesselSimulationRunner Runner => _bootstrap.Runner;
		[UnityTearDown] public IEnumerator Unload()
		{
			Time.timeScale = 1;
			if (_bootstrap != null) Runner.EndRun();
			var scene = SceneManager.GetSceneByPath(ScenePath);
			if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
		}
		private IEnumerator Load()
		{
			Directory.CreateDirectory(Folder);
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
			yield return null;
			_bootstrap = Object.FindFirstObjectByType<HarborBootstrap>();
			Assert.That(_bootstrap, Is.Not.Null); Assert.That(_bootstrap.Problem, Is.Null);
			_services = (HarborIntegrationServices)_bootstrap.Services;
			_camera = _bootstrap.CameraRig.GetComponent<Camera>();
			Runner.RecordRuns = false;
		}
		private IEnumerator Start(string vessel = "t-class", int minute = 738)
		{
			if (_screens != null) { Object.Destroy(_screens); _screens = null; yield return null; }
			var choice = new ScenarioChoice("87", "2025-07-16", minute, vessel, traffic: true);
			Assert.That(_services.HarborCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			Assert.That(_bootstrap.TryStart(settings, out problem), Is.True, problem);
			_bootstrap.WheelhouseUI.View.PickerVisible = false;
			_bootstrap.WheelhouseUI.View.PanelVisible = false;
			_bootstrap.WheelhouseUI.View.HudVisible = false;
			_bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
			_services.Displays.RadarShown = false; _services.Displays.ChartplotterShown = false;
			var began = Time.realtimeSinceStartupAsDouble;
			while (_bootstrap.ViewState != HarborViewState.Ready || !_services.Displays.Connected)
			{
				Assert.That(_bootstrap.Problem, Is.Null);
				Assert.That(_services.Displays.ChartState, Is.Not.EqualTo(HarborChartLoadState.Failed), _services.Displays.Load.Error);
				Assert.That(Time.realtimeSinceStartupAsDouble - began, Is.LessThan(180), "harbor readiness"); yield return null;
			}
			yield return null;
			var owner = _bootstrap.CameraRig.WheelhouseMount.transform.parent;
			_interior = owner.Find(WheelhouseControlsView.RootName);
			Assert.That(_interior, Is.Not.Null, "real interior instance");
			_screens = _interior.gameObject.AddComponent<ConsoleScreens>();
			_screens.Connect(_interior, Runner, Runner, _services.Displays.Load.Tessellation, _services.Displays.Load.RadarScene);
			_screens.DaylightSource = _bootstrap.Context.Sun;
			_screens.SetWheelhouseVisible(true);
			_camera.fieldOfView = 40; _camera.nearClipPlane = .05f;
		}
		[UnityTest, Explicit("Real harbor, both classes, screenshots and exact-step recording comparison"), Timeout(1200000)]
		public IEnumerator CapturesAndRecordingDigest()
		{
			yield return Load(); Time.timeScale = 0;
			foreach (var vessel in new[] { "t-class", "k-class" })
			{
				foreach (var minute in new[] { 738, 180 })
				{
					yield return Start(vessel, minute);
					Runner.Resume();
					for (var i = 0; i < 180; i++) { Runner.AdvanceFrame(1.0 / 60); yield return null; }
					Runner.Pause();
					var rig = _bootstrap.CameraRig; rig.enabled = false; rig.Wheelhouse.enabled = false;
					for (var screen = -1; screen < 4; screen++)
					{
						var pose = _interior.Find(screen < 0 ? "Eye" : ConsoleScreens.MountNames[screen] + "FocusEye");
						Assert.That(pose, Is.Not.Null);
						_camera.transform.position = pose.position;
						if (screen < 0) _camera.transform.rotation = _interior.rotation * Quaternion.Euler(14, 0, 0);
						else
						{
							var surface = ConsolePresentationTests.Find(_interior, ConsoleScreens.MountNames[screen] + "Surface");
							_camera.transform.rotation = Quaternion.LookRotation(surface.position - pose.position, _interior.up);
						}
						_screens.SetFocus(screen < 0 ? null : ScreenScheduler.Id(screen));
						for (var frame = 0; frame < 20; frame++) yield return null;
						Capture(vessel + "-" + (minute == 180 ? "night" : "day") + "-" + (screen < 0 ? "Eye" : ConsoleScreens.MountNames[screen] + "FocusEye"));
					}
					// Keep the forward seated view above, and also look down at the console from the same Eye.
					var centre = Vector3.zero;
					for (var i = 0; i < 4; i++) centre += ConsolePresentationTests.Find(_interior, ConsoleScreens.MountNames[i] + "Surface").position;
					_camera.transform.position = _interior.Find("Eye").position;
					_camera.transform.rotation = Quaternion.LookRotation(centre / 4 - _camera.transform.position, _interior.up);
					_screens.SetFocus(null);
					for (var frame = 0; frame < 20; frame++) yield return null;
					Capture(vessel + "-" + (minute == 180 ? "night" : "day") + "-Eye-console");
					for (var i = 0; i < 5; i++)
					{
						_screens.TryGetScreen(ScreenScheduler.Id(i), out var texture);
						if (i == 4) { _screens.SetPopOutShown("alarms", true); for (var f = 0; f < 20; f++) yield return null; }
						var read = ConsolePresentationTests.Read((RenderTexture)texture);
						File.WriteAllBytes(Path.Combine(Folder, vessel + "-" + (minute == 180 ? "night" : "day") + "-texture-" + ScreenScheduler.Id(i) + ".png"), read.EncodeToPNG()); Object.Destroy(read);
					}
					File.AppendAllText(Path.Combine(Folder, "texture-memory.txt"), vessel + " targets RGBA32 bytes " + _screens.TargetColourBytes + "; radar texture runtime bytes " + UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(_screens.Radar.View.Raster.Texture) + "\n");
					rig.enabled = true; rig.Wheelhouse.enabled = true;
				}
			}
			Runner.RecordRuns = true; Runner.RecordingDirectory = Path.Combine(Folder, "recordings", Guid.NewGuid().ToString("N"));
			string first = null;
			foreach (var on in new[] { false, true })
			{
				yield return Start(); _screens.Enabled = on;
				Runner.Driver.Controls = new ConstantControls(new ControlInputs(.4, .4, .1)); Runner.Resume();
				for (var frame = 0; frame < 600; frame++) { Runner.AdvanceFrame(1.0 / 60); yield return null; }
				Runner.EndRun(); Assert.That(Runner.LastRecordingPath, Is.Not.Null, Runner.RecordingError);
				var run = RecordedRun.Load(Runner.LastRecordingPath);
				Assert.That(run.StateDigest, Is.Not.Null.And.Not.Empty);
				File.AppendAllText(Path.Combine(Folder, "state-digest.txt"), "console=" + on + " state_digest=" + run.StateDigest + " steps=" + run.StepCount + " path=" + Runner.LastRecordingPath + "\n");
				if (first == null) first = run.StateDigest; else Assert.That(run.StateDigest, Is.EqualTo(first));
			}
		}
		[UnityTest, Explicit("One 60 s console attribution window; records data calls and native UI markers"), Timeout(300000)]
		public IEnumerator AttributeConsoleCost()
		{
			yield return Load(); yield return Start(); Runner.Resume();
			_screens.SetPopOutShown("alarms", true); // Include the HUD-only target in per-screen attribution.
			// Canvas paints are observed separately from legitimate static chart generations.
			var paints = 0;
			_screens.Plotter.Canvas.generateVisualContent += _ => paints++;
			var warm = Time.realtimeSinceStartupAsDouble;
			while (Time.realtimeSinceStartupAsDouble - warm < 10) yield return null;
			var handles = new List<ProfilerRecorderHandle>();
			ProfilerRecorderHandle.GetAvailable(handles);
			var names = new List<string>(); var recorders = new List<ProfilerRecorder>();
			foreach (var handle in handles)
			{
				var description = ProfilerRecorderHandle.GetDescription(handle);
				var name = description.Name;
				if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;
				if (name != "Main Thread" && !name.StartsWith("FerrySim.Console.") && !name.StartsWith("FerrySim.Chartplotter.") &&
					!name.StartsWith("UI") && !name.Contains("RenderChain") && !name.Contains("Layout") && !name.Contains("Painter") && !name.Contains("Tessell")) continue;
				names.Add(name);
				recorders.Add(new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.StartImmediately |
					ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame));
			}
			Assert.That(names, Does.Contain("Main Thread"));
			Assert.That(names, Does.Contain("FerrySim.Console.Refresh.plotter"));
			var times = new double[100000]; var frames = new int[100000]; var slots = new int[100000];
			var paintCounts = new int[100000]; var generations = new int[100000];
			var values = new long[names.Count][];
			for (var i = 0; i < values.Length; i++) values[i] = new long[100000];
			var count = 0;
			try
			{
				for (var i = 0; i < 60; i++) yield return null;
				var beforePaints = paints; var beforeRenders = _screens.Plotter.Canvas.RenderCount;
				var began = Time.realtimeSinceStartupAsDouble;
				while (Time.realtimeSinceStartupAsDouble - began < 60)
				{
					yield return null;
					if (count == times.Length) throw new InvalidOperationException("Attribution buffer full");
					times[count] = Time.realtimeSinceStartupAsDouble - began;
					frames[count] = _screens.LastRefreshFrame; slots[count] = _screens.LastRefreshScreen;
					paintCounts[count] = paints - beforePaints; beforePaints = paints;
					generations[count] = _screens.Plotter.Canvas.RenderCount - beforeRenders; beforeRenders = _screens.Plotter.Canvas.RenderCount;
					for (var i = 0; i < recorders.Count; i++) values[i][count] = recorders[i].LastValue;
					count++;
				}
			}
			finally { for (var i = 0; i < recorders.Count; i++) recorders[i].Dispose(); }
			var csv = new StringBuilder("elapsed_s,frame,screen,chart_paints,chart_generations");
			for (var i = 0; i < names.Count; i++) csv.Append(',').Append('"').Append(names[i].Replace("\"", "\"\"")).Append('"');
			csv.Append('\n');
			for (var row = 0; row < count; row++)
			{
				csv.Append(times[row].ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(frames[row]).Append(',')
					.Append(slots[row] < 0 ? "none" : ScreenScheduler.Id(slots[row])).Append(',').Append(paintCounts[row]).Append(',').Append(generations[row]);
				for (var i = 0; i < names.Count; i++) csv.Append(',').Append(values[i][row]);
				csv.Append('\n');
			}
			var file = Path.Combine(Folder, "attribution-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
			File.WriteAllText(file, csv.ToString()); TestContext.WriteLine(file);
		}
		[UnityTest, Explicit("Six paired 60 s editor windows; coordinate a quiet machine first"), Timeout(1800000)]
		public IEnumerator PairedEditorCost()
		{
			yield return Load();
			using var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
			Assert.That(main.Valid, Is.True, "no wall-time substitute for CPU time");
			var samples = new long[100000]; var timestamps = new double[100000];
			foreach (var treatment in new[] { "off", "flat", "console" })
			{
				foreach (var control in new[] { true, false })
				{
					yield return Start();
					_screens.Enabled = !control && treatment == "console";
					_services.Displays.RadarShown = !control && treatment == "flat";
					_services.Displays.ChartplotterShown = !control && treatment == "flat";
					_services.Displays.Chartplotter.View.VectorMinutes = 6; // v0.4 flat reference horizon
					Runner.Resume();
					var warm = Time.realtimeSinceStartupAsDouble;
					while (Time.realtimeSinceStartupAsDouble - warm < 10) yield return null;
					var count = 0; var start = Time.realtimeSinceStartupAsDouble;
					while (Time.realtimeSinceStartupAsDouble - start < 60)
					{
						yield return null;
						if (main.LastValue <= 0 || count >= samples.Length) throw new InvalidOperationException("Invalid profiler sample or full sample buffer");
						samples[count] = main.LastValue; timestamps[count] = Time.realtimeSinceStartupAsDouble - start; count++;
					}
					var label = (control ? "control-" : "") + treatment;
					var csv = new StringBuilder("elapsed_s,main_thread_ns\n");
					for (var i = 0; i < count; i++) csv.Append(timestamps[i].ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(samples[i]).Append('\n');
					File.WriteAllText(Path.Combine(Folder, "frames-" + label + ".csv"), csv.ToString());
					var counts = new StringBuilder();
					for (var i = 0; i < 5; i++) counts.Append(ScreenScheduler.Id(i)).Append('=').Append(_screens.RefreshCount(ScreenScheduler.Id(i))).Append('\n');
					File.WriteAllText(Path.Combine(Folder, "refresh-" + label + ".txt"), counts.ToString());
				}
			}
		}
		private void Capture(string name)
		{
			var target = RenderTexture.GetTemporary(1920, 1080, 24);
			var previous = _camera.targetTexture;
			try
			{
				_camera.targetTexture = target; _camera.Render();
				var texture = ConsolePresentationTests.Read(target);
				File.WriteAllBytes(Path.Combine(Folder, name + ".png"), texture.EncodeToPNG()); Object.Destroy(texture);
			}
			finally { _camera.targetTexture = previous; RenderTexture.ReleaseTemporary(target); }
		}
	}
}

#endif
