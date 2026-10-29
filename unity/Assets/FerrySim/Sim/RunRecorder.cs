using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;

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
	/// </summary>
	public sealed class RunRecorder : IStepObserver, ITrafficEventSink, IDisposable
	{
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

		private readonly TextWriter _telemetry;
		private readonly TextWriter _inputs;
		private TextWriter _traffic;
		private TrafficRun _trafficRun;
		private TrafficMonitor _trafficMonitor;
		private readonly StateDigest _digest = new StateDigest();
		private readonly int _stepsPerSample;
		private readonly int _stepsPerFlush;
		private long _lastTraceStep = -1;
		private ControlInputs _lastControls = ControlInputs.Neutral;
		private long _lastStepSeen = -1;
		private bool _finished;
		private bool _disposed;

		public RunRecorder(TextWriter telemetry, TextWriter inputs, int sampleHz, IEnumerable<KeyValuePair<string, string>> header, IEnumerable<string> inputComments = null)
		{
			_telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
			_inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
			if (sampleHz < 1 || sampleHz > SimulationClock.StepsPerSecond || SimulationClock.StepsPerSecond % sampleHz != 0)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleHz), $"The sample rate must divide the {SimulationClock.StepsPerSecond} Hz physics rate, got {sampleHz}.");
			}

			SampleHz = sampleHz;
			_stepsPerSample = SimulationClock.StepsPerSecond / sampleHz;
			_stepsPerFlush = SimulationClock.StepsPerSecond;
			_telemetry.Write(TelemetryCsv.FormatHeader(header));
			if (inputComments != null)
			{
				foreach (var comment in inputComments)
				{
					_inputs.Write("# " + comment + "\n");
				}
			}

			_inputs.Write(ControlTrace.HeaderRow + "\n");
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
		public Exception Failure { get; private set; }

		/// <summary>Digest of every state so far, written to the control trace when the run ends.</summary>
		public StateDigest Digest => _digest;

		/// <summary>Path of the traffic event log when created with <see cref="Create"/> for a run with traffic; null otherwise.</summary>
		public string TrafficPath { get; private set; }

		/// <summary>Opens <c>&lt;directory&gt;/&lt;baseName&gt;.csv</c> and its <c>.inputs.csv</c> companion.</summary>
		public static RunRecorder Create(string directory, string baseName, int sampleHz, List<KeyValuePair<string, string>> header) =>
			Create(directory, baseName, sampleHz, header, null, null);

		/// <summary>
		/// As <see cref="Create(string, string, int, List{KeyValuePair{string, string}})"/>; with <paramref name="traffic"/>, also
		/// <c>&lt;baseName&gt;.traffic.csv</c>, named in the header as <c>traffic_events</c>, for the events of
		/// <paramref name="monitor"/> (which this recorder receives as their sink).
		/// </summary>
		public static RunRecorder Create(string directory, string baseName, int sampleHz, List<KeyValuePair<string, string>> header, TrafficRun traffic, TrafficMonitor monitor)
		{
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
			var telemetry = new StreamWriter(telemetryPath, false, encoding) { NewLine = "\n" };
			StreamWriter inputs = null;
			StreamWriter events = null;
			try
			{
				inputs = new StreamWriter(inputsPath, false, encoding) { NewLine = "\n" };
				var comments = new[] { "ferrysim control trace recorded by Unity, for " + Path.GetFileName(telemetryPath) };
				var recorder = new RunRecorder(telemetry, inputs, sampleHz, fullHeader, comments)
				{
					TelemetryPath = telemetryPath,
					InputsPath = inputsPath,
				};
				if (trafficPath != null)
				{
					events = new StreamWriter(trafficPath, false, encoding) { NewLine = "\n" };
					recorder.AttachTraffic(events, traffic, monitor, Path.GetFileName(telemetryPath));
					recorder.TrafficPath = trafficPath;
				}

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
				_traffic.Write(TrafficEventLog.FormatRow(trafficEvent, _trafficRun.Pack.Vessels[trafficEvent.Vessel]));
				_traffic.Write('\n');
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
			if (_finished)
			{
				return;
			}

			try
			{
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
			Close(_telemetry);
			Close(_inputs);
			if (_traffic != null)
			{
				Close(_traffic);
			}
		}

		private void Close(TextWriter writer)
		{
			try { writer.Dispose(); }
			catch (Exception exception) { Failure ??= exception; }
		}

		private void WriteRow(in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			_telemetry.Write(TelemetryCsv.FormatRow(state, telemetry, controls));
			_telemetry.Write('\n');
			RowCount++;
		}

		private void WriteTraceRow(long step, in ControlInputs controls)
		{
			_inputs.Write(ControlTrace.FormatRow(step, controls));
			_inputs.Write('\n');
			_lastTraceStep = step;
			TraceRowCount++;
		}

		private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

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
