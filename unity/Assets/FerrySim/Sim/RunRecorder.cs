using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// Records one run for replay and debrief as two files side by side:
	/// <list type="bullet">
	/// <item><c>&lt;name&gt;.csv</c>, telemetry in the CLI's schema at <c>sampleHz</c>, readable by <c>ferrysim summarize</c> and <c>tools/plot_maneuver.py</c>;</item>
	/// <item><c>&lt;name&gt;.inputs.csv</c>, every control change, lossless, in the <c>ferrysim run --inputs</c> trace format.</item>
	/// </list>
	/// The telemetry header also carries the exact initial state and environment, so <see cref="ReplayPlayer"/>
	/// can rebuild the run. Rows follow the CLI's semantics: the state at <c>time_s</c> and the controls that start
	/// applying then, with a final row at the end of the run. Both files stream to disk as the run goes.
	/// </summary>
	public sealed class RunRecorder : IStepObserver, IDisposable
	{
		public const string InputsSuffix = ".inputs.csv";
		public const string StepsKey = "steps";
		public const string DigestKey = "state_digest";
		public const string EndStateKey = "end_state";

		private readonly TextWriter _telemetry;
		private readonly TextWriter _inputs;
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

		/// <summary>Opens <c>&lt;directory&gt;/&lt;baseName&gt;.csv</c> and its <c>.inputs.csv</c> companion.</summary>
		public static RunRecorder Create(string directory, string baseName, int sampleHz, List<KeyValuePair<string, string>> header)
		{
			Directory.CreateDirectory(directory);
			var telemetryPath = Path.Combine(directory, baseName + ".csv");
			var inputsPath = Path.Combine(directory, baseName + InputsSuffix);
			var fullHeader = new List<KeyValuePair<string, string>>(header) { Pair("param.inputs", Path.GetFileName(inputsPath)) };
			var encoding = new UTF8Encoding(false);
			var telemetry = new StreamWriter(telemetryPath, false, encoding) { NewLine = "\n" };
			StreamWriter inputs = null;
			try
			{
				inputs = new StreamWriter(inputsPath, false, encoding) { NewLine = "\n" };
				var comments = new[] { "ferrysim control trace recorded by Unity, for " + Path.GetFileName(telemetryPath) };
				return new RunRecorder(telemetry, inputs, sampleHz, fullHeader, comments)
				{
					TelemetryPath = telemetryPath,
					InputsPath = inputsPath,
				};
			}
			catch
			{
				try { telemetry.Dispose(); }
				finally { inputs?.Dispose(); }
				throw;
			}
		}

		/// <summary>
		/// The header for a Unity run: the keys the CLI writes for a trace replay plus what a replay needs to
		/// rebuild the run exactly (vessel file, initial state and environment at full precision). No wall-clock
		/// time or absolute paths, so the same run gives the same bytes.
		/// </summary>
		public static List<KeyValuePair<string, string>> BuildHeader(ScenarioSettings scenario, VesselParameters vessel, ISimulation simulation, string factoryName, in VesselState initial, UniformEnvironment environment, int sampleHz)
		{
			var invariant = CultureInfo.InvariantCulture;
			return new List<KeyValuePair<string, string>>
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
				Pair("env.current_north_mps", ControlTrace.FormatValue(environment.Current.North)),
				Pair("env.current_east_mps", ControlTrace.FormatValue(environment.Current.East)),
				Pair("env.wind_north_mps", ControlTrace.FormatValue(environment.Wind.North)),
				Pair("env.wind_east_mps", ControlTrace.FormatValue(environment.Wind.East)),
				Pair("env.depth_m", ControlTrace.FormatValue(environment.Depth)),
			};
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
	}
}
