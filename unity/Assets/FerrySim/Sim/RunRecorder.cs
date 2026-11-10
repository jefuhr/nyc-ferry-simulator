using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using Unity.Profiling;

namespace FerrySim.Sim
{
	/// <summary>
	/// Records one run for replay and debrief as two files side by side:
	/// <list type="bullet">
	/// <item><c>&lt;name&gt;.csv</c>, telemetry in the CLI's schema at <c>sampleHz</c>, readable by <c>ferrysim summarize</c> and <c>tools/plot_maneuver.py</c>;</item>
	/// <item><c>&lt;name&gt;.inputs.csv</c>, every control change, lossless, in the <c>ferrysim run --inputs</c> trace format.</item>
	/// </list>
	/// The telemetry header also carries the exact initial state and environment (the uniform values, or the pack and its
	/// digest), so <see cref="ReplayPlayer"/> can rebuild the run. Rows follow the CLI's semantics: the state at <c>time_s</c> and the controls that start
	/// applying then, with a final row at the end of the run. Both files stream to disk as the run goes. A run with traffic
	/// also writes <c>&lt;name&gt;.traffic.csv</c>, its traffic events and, when it ends, the snapshot of the episodes still open
	/// (<see cref="TrafficEventLog"/>), and its header names the traffic pack (<see cref="TrafficHeader"/>); a run without traffic
	/// writes exactly the two files above.
	/// The file factory submits rows to bounded, preallocated character rings and one storage thread. Steps never wait for
	/// disk I/O or free buffer space; overflow ends the optional recording through Failure. Finish drains and closes before
	/// replay. Only explicit failures rename closed files to .failed; crash-shortened normal filenames remain compatible.
	/// </summary>
	public sealed class RunRecorder : IStepObserver, ITrafficEventSink, IDisposable
	{
		private static readonly ProfilerMarker StepMarker = new ProfilerMarker("FerrySim.RunRecorder.BeforeStep");
		private static readonly ProfilerMarker FlushMarker = new ProfilerMarker("FerrySim.RunRecorder.Flush");
		private static readonly ProfilerMarker RowMarker = new ProfilerMarker("FerrySim.RunRecorder.WriteRow");
		private static readonly ProfilerMarker BackgroundWriteMarker = new ProfilerMarker("FerrySim.RunRecorder.BackgroundWrite");
		private static readonly ProfilerMarker BackgroundFlushMarker = new ProfilerMarker("FerrySim.RunRecorder.BackgroundFlush");
		public const string InputsSuffix = ".inputs.csv";
		public const string StepsKey = "steps";
		public const string DigestKey = "state_digest";
		public const string EndStateKey = "end_state";

		/// <summary>The recording header keys of a run with traffic.</summary>
		public const string TrafficKey = "traffic";

		public const string TrafficPackKey = "traffic_pack";

		public const string TrafficDigestKey = "traffic_digest";

		public const string TrafficEpochKey = "traffic_epoch_utc";

		public const string TrafficLeaveOutKey = "traffic_leave_out";

		public const string TrafficRemovedKey = "traffic_removed_mmsi";

		/// <summary>The header key naming the traffic event log beside the telemetry.</summary>
		public const string TrafficEventsKey = "traffic_events";

		private TextWriter _telemetry;
		private TextWriter _inputs;
		private TextWriter _traffic;
		private BufferedOutput _buffered;
		private char[] _rowBuffer;
		private Exception _failure;
		private TrafficRun _trafficRun;
		private TrafficMonitor _trafficMonitor;
		private readonly StateDigest _digest = new StateDigest();
		private readonly bool _hasJog;
		private readonly int _stepsPerSample;
		private readonly int _stepsPerFlush;
		private long _lastTraceStep = -1;
		private ControlInputs _lastControls = ControlInputs.Neutral;
		private long _lastStepSeen = -1;
		private bool _finished;
		private bool _disposed;
		private bool _outputClosed;

		public RunRecorder(TextWriter telemetry, TextWriter inputs, int sampleHz, IEnumerable<KeyValuePair<string, string>> header, IEnumerable<string> inputComments = null, IControlInputSource controlSource = null)
		{
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
			_inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
			if (sampleHz < 1 || sampleHz > SimulationClock.StepsPerSecond || SimulationClock.StepsPerSecond % sampleHz != 0)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleHz), $"The sample rate must divide the {SimulationClock.StepsPerSecond} Hz physics rate, got {sampleHz}.");
			}

			_hasJog = controlSource is INfuControlSource;
			SampleHz = sampleHz;
			_stepsPerSample = SimulationClock.StepsPerSecond / sampleHz;
			_stepsPerFlush = SimulationClock.StepsPerSecond;
			_telemetry.Write(TelemetryCsv.FormatHeader(header, _hasJog));
			if (inputComments != null)
			{
				foreach (var comment in inputComments)
				{
					_inputs.Write("# " + comment + "\n");
				}
			}

			_inputs.Write((_hasJog ? ControlTrace.NfuHeaderRow : ControlTrace.HeaderRow) + "\n");
		}

		public int SampleHz { get; }

		/// <summary>Telemetry rows written so far.</summary>
		public int RowCount { get; private set; }

		/// <summary>Control-trace rows written so far.</summary>
		public int TraceRowCount { get; private set; }

		/// <summary>Path of the telemetry CSV when created with <see cref="Create"/>.</summary>
		public string TelemetryPath { get; private set; }

		/// <summary>Path of the control trace when created with <see cref="Create"/>.</summary>
		public string InputsPath { get; private set; }

		public bool IsFinished => _finished;

		/// <summary>Non-null when recording was cut short by a write, flush or close failure.</summary>
		public Exception Failure
		{
			get => Volatile.Read(ref _failure);
			private set => Interlocked.CompareExchange(ref _failure, value, null);
		}

		/// <summary>Queued characters awaiting the storage thread; zero for a synchronous recorder.</summary>
		public long PendingWriteCharacters => _buffered?.PendingCharacters ?? 0;

		/// <summary>Preallocated character-ring storage, excluding the small reusable formatting buffer.</summary>
		public int OutputBufferBytes => _buffered?.CapacityBytes ?? 0;

		/// <summary>All sinks are closed and any failed-file cleanup has completed.</summary>
		public bool IsOutputClosed => Volatile.Read(ref _outputClosed);

		/// <summary>Digest of every state so far, written to the control trace when the run ends.</summary>
		public StateDigest Digest => _digest;

		/// <summary>Path of the traffic event log when created with <see cref="Create"/> for a run with traffic; null otherwise.</summary>
		public string TrafficPath { get; private set; }

		/// <summary>Opens <c>&lt;directory&gt;/&lt;baseName&gt;.csv</c> and its <c>.inputs.csv</c> companion.</summary>
		public static RunRecorder Create(string directory, string baseName, int sampleHz, List<KeyValuePair<string, string>> header, IControlInputSource controlSource = null) =>
			Create(directory, baseName, sampleHz, header, null, null, controlSource: controlSource);

		/// <summary>
		/// As the no-traffic <see cref="Create"/> overload; with <paramref name="traffic"/>, also
		/// <c>&lt;baseName&gt;.traffic.csv</c>, named in the header as <c>traffic_events</c>, for the events of
		/// <paramref name="monitor"/> (which this recorder receives as their sink).
		/// </summary>
		public static RunRecorder Create(string directory, string baseName, int sampleHz, List<KeyValuePair<string, string>> header, TrafficRun traffic, TrafficMonitor monitor,
			Func<string, TextWriter> writerFactory = null, int bufferCharacters = 0, IControlInputSource controlSource = null)
		{
			if (bufferCharacters < 0) throw new ArgumentOutOfRangeException(nameof(bufferCharacters));
			Directory.CreateDirectory(directory);
			var telemetryPath = Path.Combine(directory, baseName + ".csv");
			var inputsPath = Path.Combine(directory, baseName + InputsSuffix);
			var trafficPath = traffic != null ? Path.Combine(directory, baseName + TrafficEventLog.Suffix) : null;
			var fullHeader = new List<KeyValuePair<string, string>>(header) { Pair("param.inputs", Path.GetFileName(inputsPath)) };
			if (trafficPath != null)
			{
				fullHeader.Add(Pair(TrafficEventsKey, Path.GetFileName(trafficPath)));
			}

			var encoding = new UTF8Encoding(false);
			TextWriter Open(string path) => writerFactory != null ? writerFactory(path) : new StreamWriter(path, false, encoding) { NewLine = "\n" };
			var telemetry = Open(telemetryPath);
			TextWriter inputs = null;
			TextWriter events = null;
			try
			{
				inputs = Open(inputsPath);
				var comments = new[] { "ferrysim control trace recorded by Unity, for " + Path.GetFileName(telemetryPath) };
				var recorder = new RunRecorder(telemetry, inputs, sampleHz, fullHeader, comments, controlSource)
				{
					TelemetryPath = telemetryPath,
					InputsPath = inputsPath,
				};
				if (trafficPath != null)
				{
					events = Open(trafficPath);
					recorder.AttachTraffic(events, traffic, monitor, Path.GetFileName(telemetryPath));
					recorder.TrafficPath = trafficPath;
				}

				recorder.EnableBuffering(bufferCharacters);
				return recorder;
			}
			catch
			{
				try { telemetry.Dispose(); }
				finally
				{
					try { inputs?.Dispose(); }
					finally { events?.Dispose(); }
				}

				throw;
			}
		}

		private void EnableBuffering(int capacity)
		{
			var maximumName = 0;
			if (_trafficRun != null)
				foreach (var vessel in _trafficRun.Pack.Vessels) maximumName = Math.Max(maximumName, vessel.Name.Length);
			_rowBuffer = new char[checked(8192 + 2 * maximumName)];
			_buffered = new BufferedOutput(this, _telemetry, _inputs, _traffic, capacity);
			_telemetry = _buffered.Telemetry; _inputs = _buffered.Inputs; _traffic = _buffered.Traffic;
			_buffered.Start();
		}

		/// <summary>
		/// The header lines of a run with traffic, after the world geometry: <c>traffic</c> = <c>pack</c>, the pack's day, its
		/// digest and epoch, the leave-out and the MMSI it left out (<c>none</c> for none).
		/// </summary>
		public static List<KeyValuePair<string, string>> TrafficHeader(TrafficRun traffic) => new List<KeyValuePair<string, string>>
		{
			Pair(TrafficKey, TrafficRun.PackText),
			Pair(TrafficPackKey, traffic.Pack.Date),
			Pair(TrafficDigestKey, traffic.Pack.Digest),
			Pair(TrafficEpochKey, EnvironmentPacks.Iso(traffic.Pack.EpochUtc)),
			Pair(TrafficLeaveOutKey, ScenarioTraffic.LeaveOutText(traffic.LeaveOut)),
			Pair(TrafficRemovedKey, traffic.RemovedText),
		};

		/// <summary>Writes the traffic event log's header and takes the monitor's events from now on.</summary>
		private void AttachTraffic(TextWriter writer, TrafficRun traffic, TrafficMonitor monitor, string telemetryName)
		{
			_traffic = writer ?? throw new ArgumentNullException(nameof(writer));
			_trafficRun = traffic ?? throw new ArgumentNullException(nameof(traffic));
			_trafficMonitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
			var header = TrafficHeader(traffic);
			header.Insert(0, Pair("telemetry", telemetryName));
			header.AddRange(TrafficEventLog.LimitsHeader(monitor.Limits));
			_traffic.Write(TrafficEventLog.FormatHeader(header));
		}

		/// <summary>Writes one traffic event's row. A failure ends the recording, as a telemetry failure does; physics goes on.</summary>
		public void OnTrafficEvent(in TrafficEvent trafficEvent)
		{
			if (_finished || _traffic == null)
			{
				return;
			}

			try
			{
				if (Failure != null) throw Failure;
				if (_buffered != null) TelemetryCsv.WriteTrafficRow(_traffic, _rowBuffer, trafficEvent, _trafficRun.Pack.Vessels[trafficEvent.Vessel]);
				else
				{
					_traffic.Write(TrafficEventLog.FormatRow(trafficEvent, _trafficRun.Pack.Vessels[trafficEvent.Vessel]));
					_traffic.Write('\n');
				}
				TrafficRowCount++;
			}
			catch (Exception exception)
			{
				Failure = exception;
				Dispose();
			}
		}

		/// <summary>Traffic event rows written so far.</summary>
		public int TrafficRowCount { get; private set; }

		/// <summary>
		/// The header for a Unity run: the keys the CLI writes for a trace replay plus what a replay needs to
		/// rebuild the run exactly (vessel file, initial state and environment at full precision). No wall-clock
		/// time or absolute paths, so the same run gives the same bytes.
		/// </summary>
		public static List<KeyValuePair<string, string>> BuildHeader(ScenarioSettings scenario, VesselParameters vessel, ISimulation simulation, string factoryName, in VesselState initial, UniformEnvironment environment, int sampleHz)
		{
			var header = CommonHeader(scenario, vessel, simulation, factoryName, initial, sampleHz, SnapshotDigest(scenario.vesselFile, vessel));
			header.Add(Pair("env.current_north_mps", ControlTrace.FormatValue(environment.Current.North)));
			header.Add(Pair("env.current_east_mps", ControlTrace.FormatValue(environment.Current.East)));
			header.Add(Pair("env.wind_north_mps", ControlTrace.FormatValue(environment.Wind.North)));
			header.Add(Pair("env.wind_east_mps", ControlTrace.FormatValue(environment.Wind.East)));
			header.Add(Pair("env.depth_m", ControlTrace.FormatValue(environment.Depth)));
			return header;
		}

		/// <summary>
		/// The header for a run in <paramref name="environment"/>. Calm and uniform runs get exactly the uniform header above. A
		/// pack run says <c>environment</c> = <c>pack</c>, followed by the pack (region/date), its day digest, its epoch and its
		/// wind station (<see cref="RunEnvironment.RecordingHeader"/>), and has no <c>env.*</c> values: a replay loads the pack.
		/// </summary>
		public static List<KeyValuePair<string, string>> BuildHeader(ScenarioSettings scenario, VesselParameters vessel, ISimulation simulation, string factoryName, in VesselState initial, RunEnvironment environment, int sampleHz) =>
			BuildHeader(scenario, vessel, simulation, factoryName, initial, environment, sampleHz, SnapshotDigest(scenario.vesselFile, vessel));

		internal static List<KeyValuePair<string, string>> BuildHeader(ScenarioSettings scenario, VesselParameters vessel, ISimulation simulation, string factoryName, in VesselState initial, RunEnvironment environment, int sampleHz, string vesselDigest)
		{
			var header = CommonHeader(scenario, vessel, simulation, factoryName, initial, sampleHz, vesselDigest);
			if (environment.Kind == EnvironmentKind.Pack)
			{
				header.InsertRange(header.FindIndex(pair => pair.Key == "environment") + 1, environment.RecordingHeader());
			}
			else
			{
				var uniform = environment.UniformEnvironment;
				header.Add(Pair("env.current_north_mps", ControlTrace.FormatValue(uniform.Current.North)));
				header.Add(Pair("env.current_east_mps", ControlTrace.FormatValue(uniform.Current.East)));
				header.Add(Pair("env.wind_north_mps", ControlTrace.FormatValue(uniform.Wind.North)));
				header.Add(Pair("env.wind_east_mps", ControlTrace.FormatValue(uniform.Wind.East)));
				header.Add(Pair("env.depth_m", ControlTrace.FormatValue(uniform.Depth)));
			}

			return header;
		}

		/// <summary>Recording key for the SHA-256 of the exact vessel file bytes loaded for the run.</summary>
		public const string VesselDigestKey = "vessel_sha256";

		/// <summary>Hashes a resolved vessel file's raw bytes, preserving whitespace and encoding differences.</summary>
		public static string VesselFileDigest(string vesselFile) => VesselBytesDigest(File.ReadAllBytes(SimPaths.Resolve(vesselFile)));

		public static string VesselBytesDigest(byte[] bytes)
		{
			using var sha = SHA256.Create();
			return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
		}

		/// <summary>Fingerprints one byte snapshot only when it describes the supplied model; fingerprinted replay refuses a different model.</summary>
		internal static VesselParameters LoadVesselSnapshot(string vesselFile, VesselParameters supplied, out string digest, string expectedDigest = null)
		{
			var path = SimPaths.Resolve(vesselFile);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException($"Vessel file not found: {vesselFile} (looked at {path})", path);
			}
			var bytes = File.ReadAllBytes(path);
			digest = VesselBytesDigest(bytes);
			if (expectedDigest != null && !string.Equals(expectedDigest, digest, StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"{path}: vessel SHA-256 mismatch; recorded {expectedDigest}, replay file {digest}.");
			}
			using var stream = new MemoryStream(bytes, false);
			using var reader = new StreamReader(stream);
			var parsed = FerrySim.Core.Serialization.VesselParametersJson.Parse(reader.ReadToEnd());
			if (supplied != null && !Newtonsoft.Json.Linq.JToken.DeepEquals(
				Newtonsoft.Json.Linq.JToken.Parse(FerrySim.Core.Serialization.VesselParametersJson.Serialize(supplied)),
				Newtonsoft.Json.Linq.JToken.Parse(FerrySim.Core.Serialization.VesselParametersJson.Serialize(parsed))))
			{
				if (expectedDigest != null)
				{
					throw new InvalidOperationException($"{path}: supplied vessel parameters do not equal the file snapshot (SHA-256 {digest}).");
				}
				// Custom/legacy model overrides are supported, but the named file is not their provenance.
				digest = null;
				return supplied;
			}
			return parsed;
		}

		private static string SnapshotDigest(string vesselFile, VesselParameters vessel)
		{
			LoadVesselSnapshot(vesselFile, vessel, out var digest);
			return digest;
		}

		private static List<KeyValuePair<string, string>> CommonHeader(ScenarioSettings scenario, VesselParameters vessel, ISimulation simulation, string factoryName, in VesselState initial, int sampleHz, string vesselDigest)
		{
			var invariant = CultureInfo.InvariantCulture;
			var header = new List<KeyValuePair<string, string>>
			{
				Pair("vessel", vessel.Id),
				Pair("maneuver", "unity-run"),
				Pair("maneuver_start_s", "0"),
				Pair("environment", scenario.environment.SpecText()),
				Pair("model", simulation.GetType().FullName),
				Pair("factory", factoryName),
				Pair("physics_hz", SimulationClock.StepsPerSecond.ToString(invariant)),
				Pair("sample_hz", sampleHz.ToString(invariant)),
				Pair("initial_heading_deg", TelemetryCsv.Number(scenario.startHeadingDegrees)),
				Pair("lead_in_s", "0"),
				Pair("source", "unity"),
				Pair("param.scenario", scenario.name),
				Pair("vessel_file", scenario.vesselFile.Replace('\\', '/')),
				Pair("scenario_start_utc", scenario.StartUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", invariant)),
				Pair("start_latitude_deg", ControlTrace.FormatValue(scenario.startLatitude)),
				Pair("start_longitude_deg", ControlTrace.FormatValue(scenario.startLongitude)),
				Pair("initial.north_m", ControlTrace.FormatValue(initial.North)),
				Pair("initial.east_m", ControlTrace.FormatValue(initial.East)),
				Pair("initial.heading_rad", ControlTrace.FormatValue(initial.Heading)),
				Pair("initial.surge_mps", ControlTrace.FormatValue(initial.Surge)),
				Pair("initial.sway_mps", ControlTrace.FormatValue(initial.Sway)),
				Pair("initial.yaw_rate_radps", ControlTrace.FormatValue(initial.YawRate)),
			};
			if (vesselDigest != null)
			{
				header.Insert(1, Pair(VesselDigestKey, vesselDigest));
			}

			// Only a run that asked for fog says so: one that did not records the same bytes as before the setting existed.
			if (scenario.visibilityMetres > 0.0)
			{
				header.Add(Pair(ScenarioSettings.VisibilityKey, ControlTrace.FormatValue(scenario.visibilityMetres)));
			}

			return header;
		}

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			using var profile = StepMarker.Auto();
			if (_finished)
			{
				return;
			}

			try
			{
				if (Failure != null) throw Failure;
				if (!_hasJog && controls.Jog != 0.0)
					throw new InvalidOperationException("Non-zero jog from a control source without INfuControlSource cannot be recorded in the four-column format.");
				if (step % _stepsPerSample == 0)
				{
					WriteRow(state, telemetry, controls);
				}

				if (_lastTraceStep < 0 || !controls.Equals(_lastControls))
				{
					WriteTraceRow(step, controls);
				}

				_lastControls = controls;
				_lastStepSeen = step;
				if (step % _stepsPerFlush == 0)
				{
					using var flush = FlushMarker.Auto();
					_telemetry.Flush();
					_inputs.Flush();
					_traffic?.Flush();
				}
			}
			catch (Exception exception)
			{
				// Recording is optional: never let storage failure consume an owed physics step.
				Failure = exception;
				Dispose();
			}
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			if (!_finished)
			{
				_digest.Add(state);
			}
		}

		/// <summary>
		/// Ends the run after <paramref name="stepCount"/> steps: writes the final telemetry row (the last state with
		/// the controls last applied), a closing trace row so a CLI replay runs to the same end time, and trailing
		/// comments with the step count, the <see cref="StateDigest"/> and the end state at full precision (trace
		/// readers skip <c>#</c> lines), then closes both files.
		/// </summary>
		public void Finish(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			if (_finished)
			{
				return;
			}

			_finished = true;
			try
			{
				// Finalization may wait; an owed physics step never does. Make room for the final rows and trailer.
				_buffered?.Drain();
				if (stepCount != _lastStepSeen + 1)
				{
					throw new InvalidOperationException($"The recorder saw steps up to {_lastStepSeen} but the run ended after {stepCount} steps.");
				}

				if (stepCount > _lastTraceStep)
				{
					WriteTraceRow(stepCount, _lastControls);
				}

				_inputs.Write("# " + StepsKey + ": " + stepCount.ToString(CultureInfo.InvariantCulture) + "\n");
				_inputs.Write("# " + DigestKey + ": " + _digest.Text + "\n");
				_inputs.Write("# " + EndStateKey + ": " + StateDigest.Describe(state) + "\n");
				WriteRow(state, telemetry, _lastControls);
				if (_traffic != null)
				{
					// Episodes still open keep their least range and CPA so far, as a snapshot after the events, not as ends.
					var open = _trafficMonitor.ReportOpen(new SnapshotWriter(_traffic, _trafficRun));
					_traffic.Write(TrafficEventLog.FormatTrailer(_trafficMonitor.EventCount, open, stepCount, _trafficMonitor.Digest));
				}
			}
			catch (Exception exception)
			{
				Failure = exception;
			}
			finally
			{
				Dispose();
			}
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			_finished = true;
			if (_buffered != null)
			{
				if (Failure == null) _buffered.Complete();
				else _buffered.Abort();
				return;
			}
			Close(_telemetry);
			Close(_inputs);
			if (_traffic != null)
			{
				Close(_traffic);
			}
			Volatile.Write(ref _outputClosed, true);
		}

		private void Close(TextWriter writer)
		{
			try { writer.Dispose(); }
			catch (Exception exception) { Failure ??= exception; }
		}

		private void WriteRow(in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			using var profile = RowMarker.Auto();
			if (_buffered != null) TelemetryCsv.WriteRow(_telemetry, _rowBuffer, state, telemetry, controls, _hasJog);
			else
			{
				_telemetry.Write(TelemetryCsv.FormatRow(state, telemetry, controls, _hasJog));
				_telemetry.Write('\n');
			}
			RowCount++;
		}

		private void WriteTraceRow(long step, in ControlInputs controls)
		{
			if (_buffered != null) TelemetryCsv.WriteTraceRow(_inputs, _rowBuffer, step, controls, _hasJog);
			else
			{
				_inputs.Write(ControlTrace.FormatRow(step, controls, _hasJog));
				_inputs.Write('\n');
			}
			_lastTraceStep = step;
			TraceRowCount++;
		}

		private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

		private void RenameFailedFiles()
		{
			RenameFailedFile(InputsPath);
			RenameFailedFile(TrafficPath);
			RenameFailedFile(TelemetryPath);
		}

		private void RenameFailedFile(string path)
		{
			if (path == null || !File.Exists(path)) return;
			try { File.Move(path, path + ".failed"); }
			catch (Exception error)
			{
				var failure = new IOException("Could not mark failed recording file '" + path + "' as .failed.", error);
				Interlocked.Exchange(ref _failure, new AggregateException(Failure, failure));
				// The runner may already have detached the failed recorder; retain and report this later cleanup error too.
				UnityEngine.Debug.LogWarning("[FerrySim] Recording incomplete: " + failure.Message);
			}
		}

		/// <summary>One storage thread, fixed character rings, and no producer wait for free capacity.</summary>
		private sealed class BufferedOutput
		{
			private readonly RunRecorder _owner;
			private readonly RingWriter[] _writers;
			private readonly AutoResetEvent _work = new AutoResetEvent(false), _progress = new AutoResetEvent(false);
			private readonly Thread _thread;
			private bool _closing;
			public TextWriter Telemetry => _writers[0];
			public TextWriter Inputs => _writers[1];
			public TextWriter Traffic => _writers.Length > 2 ? _writers[2] : null;
			public int CapacityBytes { get; }
			public long PendingCharacters
			{
				get
				{
					var pending = 0L;
					foreach (var writer in _writers) pending += writer.Pending;
					return pending;
				}
			}

			public BufferedOutput(RunRecorder owner, TextWriter telemetry, TextWriter inputs, TextWriter traffic, int capacity)
			{
				_owner = owner;
				_writers = new RingWriter[traffic == null ? 2 : 3];
				// A full 100 Hz Astoria run wrote 16,665 ASCII telemetry bytes/s: this ring holds about 126 seconds.
				// All three UTF-16 rings occupy 6,815,744 bytes; zero capacity selects these bounded defaults.
				_writers[0] = new RingWriter(this, telemetry, capacity == 0 ? 2 * 1024 * 1024 : capacity);
				_writers[1] = new RingWriter(this, inputs, capacity == 0 ? 1024 * 1024 : capacity);
				if (traffic != null) _writers[2] = new RingWriter(this, traffic, capacity == 0 ? 256 * 1024 : capacity);
				foreach (var writer in _writers) CapacityBytes += writer.Capacity * sizeof(char);
				_thread = new Thread(Work) { IsBackground = true, Name = "FerrySim recording" };
			}

			public void Start() => _thread.Start();
			private void Signal() => _work.Set();
			private void CheckWritable()
			{
				if (_owner.Failure != null) throw _owner.Failure;
				if (Volatile.Read(ref _closing)) throw new ObjectDisposedException(nameof(RunRecorder));
			}
			public void Drain()
			{
				foreach (var writer in _writers) writer.Flush();
				while (true)
				{
					if (_owner.Failure != null) throw _owner.Failure;
					var pending = false;
					foreach (var writer in _writers) pending |= writer.Pending != 0 || writer.FlushPending;
					if (!pending) return;
					_progress.WaitOne();
				}
			}
			public void Complete()
			{
				Volatile.Write(ref _closing, true); Signal();
				_thread.Join();
				_work.Dispose(); _progress.Dispose();
			}
			public void Abort()
			{
				Volatile.Write(ref _closing, true); Signal();
			}
			private void Work()
			{
				try
				{
					while (_owner.Failure == null)
					{
						var worked = false;
						foreach (var writer in _writers) worked |= writer.Consume();
						if (worked) _progress.Set();
						if (Volatile.Read(ref _closing) && PendingCharacters == 0) break;
						if (!worked) _work.WaitOne();
					}
				}
				catch (Exception error) { _owner.Failure = error; }
				finally
				{
					foreach (var writer in _writers) _owner.Close(writer.Sink);
					if (_owner.Failure != null) _owner.RenameFailedFiles();
					Volatile.Write(ref _owner._outputClosed, true);
					_progress.Set();
				}
			}

			private sealed class RingWriter : TextWriter
			{
				private readonly BufferedOutput _output;
				private readonly char[] _characters;
				private long _written, _read, _flushRequested = -1, _flushCompleted = -1;
				public TextWriter Sink { get; }
				public int Capacity => _characters.Length;
				public long Pending => Volatile.Read(ref _written) - Volatile.Read(ref _read);
				public bool FlushPending => Volatile.Read(ref _flushRequested) > Volatile.Read(ref _flushCompleted);
				public override Encoding Encoding => Sink.Encoding;
				public RingWriter(BufferedOutput output, TextWriter sink, int capacity)
				{
					_output = output; Sink = sink; _characters = new char[capacity];
				}
				private int Reserve(int count)
				{
					_output.CheckWritable();
					if (count > Capacity - (_written - Volatile.Read(ref _read)))
						throw new IOException("The bounded recording buffer is full; recording stopped while physics continues.");
					return (int)(_written % Capacity);
				}
				private void Publish(int count)
				{
					Volatile.Write(ref _written, _written + count); _output.Signal();
				}
				public override void Write(char[] buffer, int index, int count)
				{
					if (buffer == null) throw new ArgumentNullException(nameof(buffer));
					if (index < 0 || count < 0 || index > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
					var at = Reserve(count); var first = Math.Min(count, Capacity - at);
					Array.Copy(buffer, index, _characters, at, first);
					Array.Copy(buffer, index + first, _characters, 0, count - first);
					Publish(count);
				}
				public override void Write(string value)
				{
					if (value == null) return;
					var at = Reserve(value.Length); var first = Math.Min(value.Length, Capacity - at);
					value.CopyTo(0, _characters, at, first); value.CopyTo(first, _characters, 0, value.Length - first);
					Publish(value.Length);
				}
				public override void Write(char value)
				{
					_characters[Reserve(1)] = value; Publish(1);
				}
				public override void Flush()
				{
					_output.CheckWritable(); Volatile.Write(ref _flushRequested, _written); _output.Signal();
				}
				public bool Consume()
				{
					var worked = false;
					// Acquire publication before the request: a later write also publishes every earlier flush.
					// Stop at that complete write boundary; the physical ring end can split a surrogate pair.
					var written = Volatile.Read(ref _written);
					var requested = Volatile.Read(ref _flushRequested);
					var limit = requested > _flushCompleted ? Math.Min(written, requested) : written;
					var available = limit - _read;
					if (available > 0)
					{
						var at = (int)(_read % Capacity); var count = (int)Math.Min(available, Capacity - at);
						using (BackgroundWriteMarker.Auto()) Sink.Write(_characters, at, count);
						Volatile.Write(ref _read, _read + count); worked = true;
					}
					if (requested >= 0 && _read >= requested && _flushCompleted < requested)
					{
						using (BackgroundFlushMarker.Auto()) Sink.Flush();
						Volatile.Write(ref _flushCompleted, requested); worked = true;
					}
					return worked;
				}
			}
		}

		/// <summary>Writes the traffic log's open-episode snapshot rows when the run ends.</summary>
		private sealed class SnapshotWriter : ITrafficEventSink
		{
			private readonly TextWriter _writer;
			private readonly TrafficRun _traffic;

			public SnapshotWriter(TextWriter writer, TrafficRun traffic)
			{
				_writer = writer;
				_traffic = traffic;
			}

			public void OnTrafficEvent(in TrafficEvent trafficEvent)
			{
				_writer.Write(TrafficEventLog.FormatRow(trafficEvent, _traffic.Pack.Vessels[trafficEvent.Vessel]));
				_writer.Write('\n');
			}
		}
	}
}
