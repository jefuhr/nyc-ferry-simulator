using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace FerrySim.Harbor.Player.Journeys
{
	/// <summary>Opt-in development-player attribution. Bounded tail windows; no drive-time disk writes.</summary>
	public sealed class HarborFrameProfile : IDisposable
	{
		private const int WindowSize = 5;
		private static readonly string[] MarkerNames =
		{
			"Main Thread", "Render Thread", "PlayerLoop", "Update.ScriptRunBehaviourUpdate",
			"PreLateUpdate.ScriptRunBehaviourLateUpdate", "PostLateUpdate.FinishFrameRendering",
			"FerrySim.Cesium.OriginShift", "FerrySim.Cesium.SubsceneScan", "FerrySim.Harbor.OriginShift",
			"BehaviourUpdate", "Physics.SyncTransforms", "PreLateUpdate.UIElementsUpdatePanels",
			"PostLateUpdate.UIElementsRepaintPanels", "PostLateUpdate.UIElementsRenderBatchModeOffscreen",
			"UIElements.UpdateLayout", "UIElements.UpdateStyle", "UIElements.UpdateRenderData",
			"PostLateUpdate.UpdateAllRenderers", "Gfx.WaitForPresentOnGfxThread", "Gfx.PresentFrame",
			"Gfx.WaitForGfxCommandsFromMainThread", "Gfx.ProcessCommands", "Gfx.WaitForRenderThread",
			"GfxDeviceMetal.WaitForLastPresent", "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "WaitForTargetFPS",
			"GpuProgramMetal.GetCachedPipeline", "GpuProgramMetal.CreateCachedPipelineAsync", "CreateGraphicsPSOJobFunc",
			"CreateGraphicsPipelineAsync", "Wait for MetalDisplayLink update",
			"Semaphore.WaitForSignal", "GC.Collect", "GarbageCollector.CollectIncremental", "GC.Alloc",
			"Shader.CreateGPUProgram", "Shader.CompileGPUProgram", "Shader.DynamicLoadGPUProgram",
			"Shader.ParseMainThread", "Shader.ParseThreaded", "Shader.WarmupAllShaders", "ShaderVariantCollection.WarmupShaders",
			"Loading.ReadObject", "Loading.ReadObjectThreaded", "AsyncUploadManager.AsyncResourceUpload",
			"AsyncReadManager.ReadFile", "File.Read", "File.Write",
			"RadarController.Refresh", "FerrySim.Chartplotter.Refresh",
			"FerrySim.Chartplotter.StaticChart", "FerrySim.Chartplotter.TargetLayer",
			"FerrySim.RunRecorder.BeforeStep", "FerrySim.RunRecorder.Flush", "FerrySim.RunRecorder.WriteRow",
			"FerrySim.RunRecorder.BackgroundWrite", "FerrySim.RunRecorder.BackgroundFlush",
			"FerrySim.TelemetryCsv.FormatRow", "FerrySim.Traffic.Place", "FerrySim.Sim.RunnerUpdate", "Cesium3DTileset.Update()"
		};
		private struct Frame
		{
			public int Id, Phase;
			public double WallMs, ThreadCpuMs;
		}
		private readonly string[] _names;
		private readonly ProfilerRecorder[] _recorders;
		private readonly string[] _categories, _units;
		private readonly bool[] _available;
		private readonly int[] _boundAt;
		private readonly long[] _windowValues, _windowCounts, _values, _counts, _maxima, _totals;
		private readonly Frame[] _window = new Frame[WindowSize], _frames;
		private int _sampleCount, _frameCount, _lastSaved = -1, _tailUntil = -1;
		private double _lastCpu = -1;
		private bool _cpuAvailable;
		public int RetainedFrames => _frameCount;
		public int SampledFrames => _sampleCount;
		public bool Overflow { get; private set; }

		public HarborFrameProfile(int capacity = 20000, string[] extraMarkers = null)
		{
			if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
			var names = new List<string>(MarkerNames);
			if (extraMarkers != null) foreach (var name in extraMarkers) if (!names.Contains(name)) names.Add(name);
			var handles = new List<ProfilerRecorderHandle>();
			ProfilerRecorderHandle.GetAvailable(handles);
			// Unity registers callback markers as objects load. A later refresh binds missing named markers.
			foreach (var handle in handles)
			{
				var name = ProfilerRecorderHandle.GetDescription(handle).Name;
				if ((name.IndexOf("Cesium", StringComparison.OrdinalIgnoreCase) >= 0 ||
					name.IndexOf("TrafficView", StringComparison.Ordinal) >= 0 ||
					name.IndexOf("VesselSimulationRunner", StringComparison.Ordinal) >= 0) && !names.Contains(name))
					names.Add(name);
			}
			_names = names.ToArray();
			_recorders = new ProfilerRecorder[_names.Length];
			_categories = new string[_names.Length]; _units = new string[_names.Length]; _available = new bool[_names.Length];
			_boundAt = new int[_names.Length];
			_windowValues = new long[WindowSize * _names.Length]; _windowCounts = new long[_windowValues.Length];
			_frames = new Frame[capacity]; _values = new long[capacity * _names.Length]; _counts = new long[_values.Length];
			_maxima = new long[_names.Length]; _totals = new long[_names.Length];
			RefreshMarkers();
			try { _lastCpu = ReadThreadCpuMilliseconds(); _cpuAvailable = _lastCpu >= 0; }
			catch (DllNotFoundException) { _cpuAvailable = false; }
			catch (EntryPointNotFoundException) { _cpuAvailable = false; }
		}

		/// <summary>Call at setup boundaries only; marker discovery deliberately allocates.</summary>
		public void RefreshMarkers()
		{
			var handles = new List<ProfilerRecorderHandle>();
			ProfilerRecorderHandle.GetAvailable(handles);
			for (var i = 0; i < _names.Length; i++)
			{
				if (_available[i]) continue;
				foreach (var handle in handles)
				{
					var description = ProfilerRecorderHandle.GetDescription(handle);
					if (description.Name != _names[i]) continue;
					var options = ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately;
					// Shader compilation can run on a render/worker thread; never label those sums main-thread CPU.
					if (!AllThreads(_names[i]))
						options |= ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
					_recorders[i] = new ProfilerRecorder(handle, 1, options);
					_available[i] = _recorders[i].Valid;
					_boundAt[i] = Time.frameCount;
					_categories[i] = description.Category.Name; _units[i] = description.UnitType.ToString();
					break;
				}
			}
		}

		public void Sample(int completedFrame, int phase, double wallMs)
		{
			var slot = _sampleCount % WindowSize;
			var cpu = _cpuAvailable ? ReadThreadCpuMilliseconds() : -1;
			_window[slot] = new Frame { Id = completedFrame, Phase = phase, WallMs = wallMs,
				ThreadCpuMs = _sampleCount > 0 && cpu >= 0 && _lastCpu >= 0 ? cpu - _lastCpu : -1 };
			_lastCpu = cpu;
			for (var i = 0; i < _names.Length; i++)
			{
				var sample = _available[i] && _recorders[i].Count > 0 ? _recorders[i].GetSample(0) : default;
				_windowValues[slot * _names.Length + i] = sample.Value;
				_windowCounts[slot * _names.Length + i] = sample.Count;
				_maxima[i] = Math.Max(_maxima[i], sample.Value); _totals[i] += sample.Value;
				// Keep collection running: resetting inside PlayerLoop/Update discards those open scopes.
			}
			_sampleCount++;
			if (wallMs > 20 && phase != 4 && phase != 5)
			{
				for (var prior = Math.Min(2, _sampleCount - 1); prior >= 0; prior--)
					Retain((slot + WindowSize - prior) % WindowSize);
				_tailUntil = completedFrame + 2;
			}
			else if (completedFrame <= _tailUntil) Retain(slot);
		}

		private void Retain(int slot)
		{
			if (_window[slot].Id <= _lastSaved) return;
			if (_frameCount == _frames.Length) { Overflow = true; return; }
			_frames[_frameCount] = _window[slot];
			Array.Copy(_windowValues, slot * _names.Length, _values, _frameCount * _names.Length, _names.Length);
			Array.Copy(_windowCounts, slot * _names.Length, _counts, _frameCount * _names.Length, _names.Length);
			_lastSaved = _window[slot].Id;
			_frameCount++;
		}

		public void Write(string directory)
		{
			var handles = new List<ProfilerRecorderHandle>();
			ProfilerRecorderHandle.GetAvailable(handles);
			var inventory = new JArray();
			foreach (var handle in handles)
			{
				var description = ProfilerRecorderHandle.GetDescription(handle);
				inventory.Add(new JObject { ["name"] = description.Name, ["category"] = description.Category.Name, ["unit"] = description.UnitType.ToString() });
			}
			File.WriteAllText(Path.Combine(directory, "profile-available-markers.json"), inventory.ToString() + "\n");
			var markers = new JArray();
			for (var i = 0; i < _names.Length; i++) markers.Add(new JObject {
				["id"] = i, ["name"] = _names[i], ["available"] = _available[i], ["bound_at_frame"] = _available[i] ? (JToken)_boundAt[i] : null, ["category"] = _categories[i], ["unit"] = _units[i],
				["scope"] = AllThreads(_names[i]) ? "all threads" : "main thread",
				["max_value"] = _maxima[i], ["sum_value"] = _totals[i] });
			File.WriteAllText(Path.Combine(directory, "profile.json"), new JObject {
				["schema"] = "ferrysim-frame-profile/1", ["development_build"] = Debug.isDebugBuild,
				["sampling_method"] = "continuous per-frame sums",
				["sampled_frames"] = _sampleCount, ["retained_frames"] = _frameCount, ["capacity"] = _frames.Length,
				["overflow"] = Overflow, ["main_thread_cpu_available"] = _cpuAvailable, ["markers"] = markers,
				["note"] = "Diagnostic only. Previous completed-frame marker sums overlap; never add them. Native GPU timestamps remain independent in frames.csv. Main-thread CPU uses Darwin CLOCK_THREAD_CPUTIME_ID. Missing markers are unavailable, not zero-cost evidence. Tail includes every >20 ms frame outside replay/finalization with two neighbors each side."
			}.ToString() + "\n");
			using (var frames = new StreamWriter(Path.Combine(directory, "profile-frames.csv")))
			using (var samples = new StreamWriter(Path.Combine(directory, "profile-markers.csv")))
			{
				frames.WriteLine("frame,phase,wall_ms,main_thread_cpu_ms");
				samples.WriteLine("frame,marker,value,count");
				for (var row = 0; row < _frameCount; row++)
				{
					var frame = _frames[row];
					frames.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:R},{3:R}", frame.Id, frame.Phase, frame.WallMs, frame.ThreadCpuMs));
					for (var i = 0; i < _names.Length; i++)
					{
						var at = row * _names.Length + i;
						if (_counts[at] != 0 || _values[at] != 0)
							samples.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", frame.Id, i, _values[at], _counts[at]));
					}
				}
			}
		}

		public void Dispose()
		{
			for (var i = 0; i < _recorders.Length; i++) _recorders[i].Dispose();
		}

		private static bool AllThreads(string name) => name == "Main Thread" || name == "Render Thread" || name.StartsWith("Shader.", StringComparison.Ordinal) ||
			name.StartsWith("FerrySim.RunRecorder.Background", StringComparison.Ordinal) ||
			name.StartsWith("GpuProgramMetal.", StringComparison.Ordinal) || name.StartsWith("CreateGraphics", StringComparison.Ordinal) ||
			name == "Wait for MetalDisplayLink update" || name == "Gfx.WaitForGfxCommandsFromMainThread" || name == "Gfx.ProcessCommands";

		public static double ReadThreadCpuMilliseconds()
		{
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
			return clock_gettime(16, out var time) == 0 ? time.Seconds * 1000.0 + time.Nanoseconds * 1e-6 : -1;
#else
			return -1;
#endif
		}
		[StructLayout(LayoutKind.Sequential)]
		private struct Timespec { public long Seconds, Nanoseconds; }
		[DllImport("/usr/lib/libSystem.B.dylib")]
		private static extern int clock_gettime(int clock, out Timespec time);
	}
}
