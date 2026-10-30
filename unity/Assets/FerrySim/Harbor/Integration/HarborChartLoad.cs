using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FerrySim.Chartplotter;
using FerrySim.Core.Radar;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;

namespace FerrySim.Harbor
{
	public enum HarborChartLoadState
	{
		Loading,
		Ready,
		Failed,
	}

	/// <summary>
	/// The resident chart pack, loaded once per process on a thread-pool thread with the two derived products the
	/// displays need (the radar's semantic index and the chartplotter's tessellation), so a harbor scene composes and
	/// starts its first run without waiting the seconds the verified load takes. The same folder and runtime digest
	/// always return the same load, so a second scene (a replay, a test reloading Harbor.unity) shares the one copy in
	/// memory. The composition root polls <see cref="State"/> on the main thread; nothing here touches the scene.
	/// </summary>
	public sealed class HarborChartLoad
	{
		/// <summary>What the background work produces; a test may supply its own.</summary>
		public sealed class Result
		{
			public HarborChartPack Pack;
			public RadarScene RadarScene;
			public ChartTessellation Tessellation;
			public double LoadMilliseconds;
			public double IndexMilliseconds;
			public double TessellationMilliseconds;
		}

		private static readonly object Gate = new object();
		private static readonly Dictionary<string, HarborChartLoad> Loads = new Dictionary<string, HarborChartLoad>(StringComparer.Ordinal);

		private readonly Stopwatch _watch = Stopwatch.StartNew();
		private int _state = (int)HarborChartLoadState.Loading;
		private Result _result;
		private string _error;
		private double _totalMilliseconds;
		private long _managedBytesBefore;
		private long _managedBytesAfter;

		private HarborChartLoad(string key)
		{
			Key = key;
			StartedUtc = DateTime.UtcNow;
		}

		/// <summary>The folder and runtime digest this load is for.</summary>
		public string Key { get; }

		public DateTime StartedUtc { get; }

		/// <summary>How many loads this process has started, over every key; a test resets it.</summary>
		public static int StartedCount
		{
			get
			{
				lock (Gate)
				{
					return Loads.Count;
				}
			}
		}

		public HarborChartLoadState State => (HarborChartLoadState)Volatile.Read(ref _state);

		public bool IsReady => State == HarborChartLoadState.Ready;

		public bool IsFailed => State == HarborChartLoadState.Failed;

		/// <summary>The failure, naming the file when the reader refused the pack; null unless <see cref="IsFailed"/>.</summary>
		public string Error => IsFailed ? _error : null;

		public HarborChartPack Pack => IsReady ? _result.Pack : null;

		public RadarScene RadarScene => IsReady ? _result.RadarScene : null;

		public ChartTessellation Tessellation => IsReady ? _result.Tessellation : null;

		public double LoadMilliseconds => IsReady ? _result.LoadMilliseconds : double.NaN;

		public double IndexMilliseconds => IsReady ? _result.IndexMilliseconds : double.NaN;

		public double TessellationMilliseconds => IsReady ? _result.TessellationMilliseconds : double.NaN;

		/// <summary>Wall time from the start of the load to its end, on the worker thread; NaN while loading.</summary>
		public double TotalMilliseconds => State == HarborChartLoadState.Loading ? double.NaN : _totalMilliseconds;

		/// <summary>Managed heap before and after the work on the worker thread (GC.GetTotalMemory(false)): a rough resident delta, not a peak.</summary>
		public long ManagedBytesBefore => _managedBytesBefore;

		public long ManagedBytesAfter => State == HarborChartLoadState.Loading ? 0 : _managedBytesAfter;

		/// <summary>
		/// The shared load of <paramref name="chartFolder"/> against <paramref name="runtime"/>, started now on the thread
		/// pool if this process has not started it yet. Returns immediately.
		/// </summary>
		public static HarborChartLoad Start(string chartFolder, HarborRuntimePack runtime)
		{
			if (string.IsNullOrEmpty(chartFolder)) throw new ArgumentException("A chart folder is required.", nameof(chartFolder));
			if (runtime == null) throw new ArgumentNullException(nameof(runtime));
			return Begin(chartFolder + "\n" + runtime.Digest, () => Work(chartFolder, runtime));
		}

		/// <summary>Starts <paramref name="work"/> once for <paramref name="key"/>; a later call returns the same load, loading or not.</summary>
		public static HarborChartLoad Begin(string key, Func<Result> work)
		{
			if (string.IsNullOrEmpty(key)) throw new ArgumentException("A key is required.", nameof(key));
			if (work == null) throw new ArgumentNullException(nameof(work));
			HarborChartLoad load;
			lock (Gate)
			{
				if (Loads.TryGetValue(key, out load))
				{
					return load;
				}

				load = new HarborChartLoad(key);
				Loads.Add(key, load);
			}

			load._managedBytesBefore = GC.GetTotalMemory(false);
			Task.Run(() => load.Run(work));
			return load;
		}

		/// <summary>The shared load for <paramref name="key"/>, or null when none was started.</summary>
		public static HarborChartLoad Find(string key)
		{
			lock (Gate)
			{
				return Loads.TryGetValue(key, out var load) ? load : null;
			}
		}

		/// <summary>Forgets every load so a test can start again; the loads themselves finish on their own.</summary>
		public static void ResetForTests()
		{
			lock (Gate)
			{
				Loads.Clear();
			}
		}

		private void Run(Func<Result> work)
		{
			try
			{
				var result = work() ?? throw new InvalidOperationException("The chart load produced nothing.");
				if (result.Pack == null) throw new InvalidOperationException("The chart load produced no pack.");
				_result = result;
				_managedBytesAfter = GC.GetTotalMemory(false);
				_totalMilliseconds = _watch.Elapsed.TotalMilliseconds;
				Volatile.Write(ref _state, (int)HarborChartLoadState.Ready);
			}
			catch (Exception error)
			{
				_error = error.Message;
				_managedBytesAfter = GC.GetTotalMemory(false);
				_totalMilliseconds = _watch.Elapsed.TotalMilliseconds;
				Volatile.Write(ref _state, (int)HarborChartLoadState.Failed);
			}
		}

		/// <summary>The real work: the verified pack, then the radar index, then the tessellation. All engine-free.</summary>
		public static Result Work(string chartFolder, HarborRuntimePack runtime)
		{
			var result = new Result();
			var watch = Stopwatch.StartNew();
			result.Pack = HarborChartPackJson.Load(chartFolder, runtime);
			result.LoadMilliseconds = watch.Elapsed.TotalMilliseconds;
			watch.Restart();
			result.RadarScene = RadarScene.FromPacks(result.Pack, runtime);
			result.IndexMilliseconds = watch.Elapsed.TotalMilliseconds;
			watch.Restart();
			result.Tessellation = ChartTessellation.Build(result.Pack, runtime);
			result.TessellationMilliseconds = watch.Elapsed.TotalMilliseconds;
			return result;
		}
	}
}
