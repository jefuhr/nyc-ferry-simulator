using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>A run written by <see cref="RunRecorder"/>, read back with everything needed to drive it again.</summary>
	public sealed class RecordedRun
	{
		private RecordedRun()
		{
		}

		public string TelemetryPath { get; private set; }

		public IReadOnlyDictionary<string, string> Header { get; private set; }

		/// <summary>Telemetry data lines exactly as recorded.</summary>
		public IReadOnlyList<string> Rows { get; private set; }

		public ControlTrace Inputs { get; private set; }

		public string VesselFile { get; private set; }

		public VesselState InitialState { get; private set; }

		public UniformEnvironment Environment { get; private set; }

		public int SampleHz { get; private set; }

		/// <summary>Steps in the run: the time of the final row on the step grid.</summary>
		public long StepCount => Inputs.EndStep;

		public DateTimeOffset ScenarioStartUtc { get; private set; }

		/// <summary>The recorded <see cref="StateDigest"/> text, or null for a recording that has none (cut short by a crash).</summary>
		public string StateDigest { get; private set; }

		public static RecordedRun Load(string telemetryPath)
		{
			if (!File.Exists(telemetryPath))
			{
				throw new FileNotFoundException($"Recorded run not found: {telemetryPath}", telemetryPath);
			}

			TelemetryCsv.Split(File.ReadAllLines(telemetryPath), telemetryPath, out var header, out var rows);
			var inputsName = Required(header, "param.inputs", telemetryPath);
			var directory = Path.GetDirectoryName(Path.GetFullPath(telemetryPath)) ?? string.Empty;
			var inputsPath = Path.Combine(directory, inputsName);
			var inputs = ControlTrace.Load(inputsPath);
			string digest = null;
			foreach (var line in File.ReadAllLines(inputsPath))
			{
				var marker = "# " + RunRecorder.DigestKey + ":";
				if (line.StartsWith(marker, StringComparison.Ordinal))
				{
					digest = line.Substring(marker.Length).Trim();
				}
			}
			var initial = VesselState.Planar(
				0.0,
				Number(header, "initial.north_m", telemetryPath),
				Number(header, "initial.east_m", telemetryPath),
				Number(header, "initial.heading_rad", telemetryPath),
				Number(header, "initial.surge_mps", telemetryPath),
				Number(header, "initial.sway_mps", telemetryPath),
				Number(header, "initial.yaw_rate_radps", telemetryPath));
			var environment = new UniformEnvironment(
				new HorizontalVector(Number(header, "env.current_north_mps", telemetryPath), Number(header, "env.current_east_mps", telemetryPath)),
				new HorizontalVector(Number(header, "env.wind_north_mps", telemetryPath), Number(header, "env.wind_east_mps", telemetryPath)),
				Number(header, "env.depth_m", telemetryPath));
			var sampleHz = (int)Number(header, "sample_hz", telemetryPath);
			if (sampleHz < 1 || SimulationClock.StepsPerSecond % sampleHz != 0)
			{
				throw new FormatException($"{telemetryPath}: sample_hz {sampleHz} does not divide the physics rate.");
			}

			DateTimeOffset.TryParse(Required(header, "scenario_start_utc", telemetryPath), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start);
			return new RecordedRun
			{
				TelemetryPath = telemetryPath,
				Header = header,
				Rows = rows,
				Inputs = inputs,
				VesselFile = Required(header, "vessel_file", telemetryPath),
				InitialState = initial,
				Environment = environment,
				SampleHz = sampleHz,
				ScenarioStartUtc = start,
				StateDigest = digest,
			};
		}

		private static string Required(Dictionary<string, string> header, string key, string label) =>
			header.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
				? value
				: throw new FormatException($"{label}: header has no '{key}'; was it recorded by Unity's RunRecorder?");

		private static double Number(Dictionary<string, string> header, string key, string label)
		{
			var text = Required(header, key, label);
			if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
			{
				throw new FormatException($"{label}: header '{key}' is not a number: '{text}'.");
			}

			return value;
		}
	}

	/// <summary>The outcome of re-driving a recorded run.</summary>
	public sealed class ReplayResult
	{
		public ReplayResult(int rowsCompared, int expectedRows, int firstMismatchRow, string expected, string actual, string recordedDigest = null, string replayedDigest = null)
		{
			RowsCompared = rowsCompared;
			ExpectedRows = expectedRows;
			FirstMismatchRow = firstMismatchRow;
			Expected = expected;
			Actual = actual;
			RecordedDigest = recordedDigest;
			ReplayedDigest = replayedDigest;
		}

		public int RowsCompared { get; }

		public int ExpectedRows { get; }

		/// <summary>Index of the first data row that differs, or −1 when every row matched.</summary>
		public int FirstMismatchRow { get; }

		public string Expected { get; }

		public string Actual { get; }

		/// <summary>Digest of every recorded state, when the recording has one.</summary>
		public string RecordedDigest { get; }

		/// <summary>Digest of every replayed state, once the replay has finished.</summary>
		public string ReplayedDigest { get; }

		public bool RowsMatch => FirstMismatchRow < 0 && RowsCompared == ExpectedRows;

		/// <summary>True when the digests agree, or when there is no recorded digest to check.</summary>
		public bool DigestMatches => RecordedDigest == null || string.Equals(RecordedDigest, ReplayedDigest, StringComparison.Ordinal);

		/// <summary>Every row equal and, when recorded, every state equal bit for bit.</summary>
		public bool Matches => RowsMatch && DigestMatches;

		public override string ToString()
		{
			if (!RowsMatch)
			{
				return FirstMismatchRow < 0
					? $"replay produced {RowsCompared} rows, recording has {ExpectedRows}"
					: $"replay differs at data row {FirstMismatchRow}:\n  recorded {Expected}\n  replayed {Actual}";
			}

			if (!DigestMatches)
			{
				return $"every row matches at CSV precision but the states differ: recorded {RecordedDigest}, replayed {ReplayedDigest}";
			}

			return RecordedDigest == null
				? $"replay matches all {RowsCompared} recorded rows (no state digest recorded)"
				: $"replay matches all {RowsCompared} recorded rows and every state bit for bit ({ReplayedDigest})";
		}
	}

	/// <summary>
	/// Compares a replay with a recording as it runs: every telemetry row the replay produces must equal the
	/// recorded row character for character. Attach it to a <see cref="FixedStepDriver"/> driven by the run's
	/// <see cref="RecordedRun.Inputs"/>, then call <see cref="Finish"/> after <see cref="RecordedRun.StepCount"/> steps.
	/// </summary>
	public sealed class ReplayVerifier : IStepObserver
	{
		private readonly RecordedRun _run;
		private readonly int _stepsPerSample;
		private readonly StateDigest _digest = new StateDigest();
		private int _compared;
		private int _firstMismatch = -1;
		private string _expected;
		private string _actual;
		private bool _finished;
		private ReplayResult _final;

		public ReplayVerifier(RecordedRun run)
		{
			_run = run ?? throw new ArgumentNullException(nameof(run));
			_stepsPerSample = SimulationClock.StepsPerSecond / run.SampleHz;
		}

		/// <summary>The comparison so far; complete once <see cref="Finish"/> has run.</summary>
		public ReplayResult Result => _final ?? new ReplayResult(_compared, _compared, _firstMismatch, _expected, _actual);

		public bool IsFinished => _finished;

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			if (step % _stepsPerSample == 0 && step < _run.StepCount)
			{
				Compare(TelemetryCsv.FormatRow(state, telemetry, controls));
			}
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			if (!_finished)
			{
				_digest.Add(state);
			}
		}

		/// <summary>Compares the final row (the recorder writes it when the run ends) and returns the full result.</summary>
		public ReplayResult Finish(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			if (!_finished)
			{
				if (stepCount != _run.StepCount && _firstMismatch < 0)
				{
					_firstMismatch = _compared;
					_expected = $"a run of {_run.StepCount} steps";
					_actual = $"{stepCount} steps";
				}

				// The trace's closing row repeats the last controls, which is what the final row records.
				Compare(TelemetryCsv.FormatRow(state, telemetry, _run.Inputs.At(stepCount)));
				_finished = true;
			}

			_final ??= new ReplayResult(_compared, _run.Rows.Count, _firstMismatch, _expected, _actual, _run.StateDigest, _digest.Text);
			return _final;
		}

		private void Compare(string actual)
		{
			var index = _compared++;
			if (_firstMismatch >= 0)
			{
				return;
			}

			var expected = index < _run.Rows.Count ? _run.Rows[index] : "(no row)";
			if (!string.Equals(expected, actual, StringComparison.Ordinal))
			{
				_firstMismatch = index;
				_expected = expected;
				_actual = actual;
			}
		}
	}

	/// <summary>
	/// Re-drives the simulation from a recorded control trace and checks the same states come out. The model is
	/// deterministic and the trace is lossless, so on the same runtime any difference is a real divergence.
	/// </summary>
	public static class ReplayPlayer
	{
		/// <summary>
		/// Replays <paramref name="run"/> on a fresh simulation from <paramref name="factory"/> and compares every row.
		/// Pass the <paramref name="geometry"/> the run was recorded with (its <c>world_geometry</c> header names the
		/// type); open water when omitted.
		/// </summary>
		public static ReplayResult Verify(RecordedRun run, VesselParameters parameters, ISimulationFactory factory, IStepObserver observer = null, IWorldGeometry geometry = null)
		{
			var simulation = factory.Create(parameters, run.Environment, geometry ?? EmptyWorldGeometry.Instance, run.InitialState);
			var driver = new FixedStepDriver(simulation, run.Inputs);
			var verifier = new ReplayVerifier(run);
			driver.AddObserver(verifier);
			if (observer != null)
			{
				driver.AddObserver(observer);
			}

			while (driver.StepCount < run.StepCount)
			{
				if (!driver.StepOnce())
				{
					return new ReplayResult(verifier.Result.RowsCompared, run.Rows.Count, verifier.Result.RowsCompared,
						"(finite state)", "the model produced a non-finite state at step " + driver.StepCount);
				}
			}

			return verifier.Finish(driver.StepCount, simulation.State, simulation.Telemetry);
		}
	}
}
