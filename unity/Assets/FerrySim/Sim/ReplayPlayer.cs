using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;

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

		/// <summary>The recorded uniform environment (calm included); null for a pack run, whose pack a replay loads.</summary>
		public UniformEnvironment Environment { get; private set; }

		/// <summary>Calm, uniform, or a pack's day (the header's <c>environment</c> is <c>pack</c>).</summary>
		public EnvironmentKind EnvironmentKind { get; private set; }

		/// <summary>The pack as region/date (<c>environment_pack</c>); null unless a pack run.</summary>
		public string EnvironmentPack { get; private set; }

		/// <summary>The pack's day digest (<c>environment_digest</c>); null unless a pack run.</summary>
		public string EnvironmentDigest { get; private set; }

		/// <summary>The pack's local-midnight epoch (<c>environment_epoch_utc</c>); default unless a pack run.</summary>
		public DateTimeOffset EnvironmentEpochUtc { get; private set; }

		public int SampleHz { get; private set; }

		/// <summary>Steps in the run: the time of the final row on the step grid.</summary>
		public long StepCount => Inputs.EndStep;

		public DateTimeOffset ScenarioStartUtc { get; private set; }

		/// <summary>The recorded <see cref="StateDigest"/> text, or null for a recording that has none (cut short by a crash).</summary>
		public string StateDigest { get; private set; }

		/// <summary>The run's visibility (<see cref="ScenarioSettings.visibilityMetres"/>), metres: 0, unlimited, when the header has no <c>visibility_m</c>.</summary>
		public double VisibilityMetres { get; private set; }

		/// <summary>True for a run with traffic: the header's <c>traffic</c> is <c>pack</c>.</summary>
		public bool HasTraffic { get; private set; }

		/// <summary>The traffic pack's day (<c>traffic_pack</c>); null without traffic.</summary>
		public string TrafficPack { get; private set; }

		/// <summary>The traffic pack's digest (<c>traffic_digest</c>); null without traffic.</summary>
		public string TrafficDigest { get; private set; }

		/// <summary>How the run chose the vessel it left out (<c>traffic_leave_out</c>).</summary>
		public TrafficLeaveOut TrafficLeaveOut { get; private set; }

		/// <summary>The MMSI the run left out (<c>traffic_removed_mmsi</c>), 0 for none: a replay leaves out the same one.</summary>
		public int TrafficRemovedMmsi { get; private set; }

		/// <summary>The run's traffic event log (<c>traffic_events</c>, beside the telemetry); null without traffic.</summary>
		public TrafficEventLogFile TrafficEvents { get; private set; }

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
			var spec = Required(header, "environment", telemetryPath);
			var kind = spec == RunEnvironment.PackText ? EnvironmentKind.Pack : spec == "calm" ? EnvironmentKind.Calm : EnvironmentKind.Uniform;
			UniformEnvironment environment = null;
			string pack = null;
			string packDigest = null;
			var epoch = default(DateTimeOffset);
			if (kind == EnvironmentKind.Pack)
			{
				pack = Required(header, RunEnvironment.PackKey, telemetryPath);
				packDigest = Required(header, RunEnvironment.DigestKey, telemetryPath);
				if (!FerrySim.Core.Serialization.EnvironmentPackDigests.IsHexDigest(packDigest))
				{
					throw new FormatException($"{telemetryPath}: header '{RunEnvironment.DigestKey}' is not 64 lower-case hex characters: '{packDigest}'.");
				}

				var epochText = Required(header, RunEnvironment.EpochKey, telemetryPath);
				if (!DateTimeOffset.TryParse(epochText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out epoch))
				{
					throw new FormatException($"{telemetryPath}: header '{RunEnvironment.EpochKey}' is not an ISO 8601 time: '{epochText}'.");
				}
			}
			else
			{
				environment = new UniformEnvironment(
					new HorizontalVector(Number(header, "env.current_north_mps", telemetryPath), Number(header, "env.current_east_mps", telemetryPath)),
					new HorizontalVector(Number(header, "env.wind_north_mps", telemetryPath), Number(header, "env.wind_east_mps", telemetryPath)),
					Number(header, "env.depth_m", telemetryPath));
			}

			var sampleHz = (int)Number(header, "sample_hz", telemetryPath);
			if (sampleHz < 1 || SimulationClock.StepsPerSecond % sampleHz != 0)
			{
				throw new FormatException($"{telemetryPath}: sample_hz {sampleHz} does not divide the physics rate.");
			}

			DateTimeOffset.TryParse(Required(header, "scenario_start_utc", telemetryPath), CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start);
			var visibility = 0.0;
			if (header.ContainsKey(ScenarioSettings.VisibilityKey))
			{
				visibility = Number(header, ScenarioSettings.VisibilityKey, telemetryPath);
				if (!(visibility >= ScenarioSettings.MinimumVisibilityMetres) || double.IsInfinity(visibility))
				{
					throw new FormatException($"{telemetryPath}: header '{ScenarioSettings.VisibilityKey}' is not a visibility of at least {ScenarioSettings.MinimumVisibilityMetres.ToString(CultureInfo.InvariantCulture)} m: '{header[ScenarioSettings.VisibilityKey]}'.");
				}
			}

			string trafficPack = null;
			string trafficDigest = null;
			var leaveOut = FerrySim.Core.Traffic.TrafficLeaveOut.Off;
			var removed = 0;
			TrafficEventLogFile trafficEvents = null;
			var hasTraffic = header.TryGetValue(RunRecorder.TrafficKey, out var trafficText);
			if (hasTraffic)
			{
				if (trafficText != TrafficRun.PackText)
				{
					throw new FormatException($"{telemetryPath}: header '{RunRecorder.TrafficKey}' is '{trafficText}'; a run with traffic records '{TrafficRun.PackText}'.");
				}

				trafficPack = Required(header, RunRecorder.TrafficPackKey, telemetryPath);
				trafficDigest = Required(header, RunRecorder.TrafficDigestKey, telemetryPath);
				if (!EnvironmentPackDigests.IsHexDigest(trafficDigest))
				{
					throw new FormatException($"{telemetryPath}: header '{RunRecorder.TrafficDigestKey}' is not 64 lower-case hex characters: '{trafficDigest}'.");
				}

				var leaveOutText = Required(header, RunRecorder.TrafficLeaveOutKey, telemetryPath);
				leaveOut = leaveOutText == ScenarioTraffic.ByMmsi ? FerrySim.Core.Traffic.TrafficLeaveOut.Mmsi : leaveOutText == ScenarioTraffic.Automatic ? FerrySim.Core.Traffic.TrafficLeaveOut.Automatic :
					leaveOutText == ScenarioTraffic.Off ? FerrySim.Core.Traffic.TrafficLeaveOut.Off : throw new FormatException($"{telemetryPath}: header '{RunRecorder.TrafficLeaveOutKey}' is '{leaveOutText}', not off, mmsi or auto.");
				var removedText = Required(header, RunRecorder.TrafficRemovedKey, telemetryPath);
				if (removedText != TrafficRun.NoneRemoved && (!int.TryParse(removedText, NumberStyles.None, CultureInfo.InvariantCulture, out removed) || removed < 1))
				{
					throw new FormatException($"{telemetryPath}: header '{RunRecorder.TrafficRemovedKey}' is '{removedText}', not an MMSI or {TrafficRun.NoneRemoved}.");
				}

				var eventsPath = Path.Combine(directory, Required(header, RunRecorder.TrafficEventsKey, telemetryPath));
				if (!File.Exists(eventsPath))
				{
					throw new FileNotFoundException($"{telemetryPath}: its traffic event log {Path.GetFileName(eventsPath)} is missing (looked at {eventsPath}).", eventsPath);
				}

				trafficEvents = TrafficEventLog.Parse(File.ReadAllLines(eventsPath), eventsPath);
			}

			return new RecordedRun
			{
				TelemetryPath = telemetryPath,
				Header = header,
				Rows = rows,
				Inputs = inputs,
				VesselFile = Required(header, "vessel_file", telemetryPath),
				InitialState = initial,
				Environment = environment,
				EnvironmentKind = kind,
				EnvironmentPack = pack,
				EnvironmentDigest = packDigest,
				EnvironmentEpochUtc = epoch,
				SampleHz = sampleHz,
				ScenarioStartUtc = start,
				StateDigest = digest,
				VisibilityMetres = visibility,
				HasTraffic = hasTraffic,
				TrafficPack = trafficPack,
				TrafficDigest = trafficDigest,
				TrafficLeaveOut = leaveOut,
				TrafficRemovedMmsi = removed,
				TrafficEvents = trafficEvents,
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

		/// <summary>A result with the traffic events' comparison (a run with traffic).</summary>
		public ReplayResult(ReplayResult rows, TrafficEventVerifier traffic)
			: this(rows.RowsCompared, rows.ExpectedRows, rows.FirstMismatchRow, rows.Expected, rows.Actual, rows.RecordedDigest, rows.ReplayedDigest)
		{
			Traffic = traffic;
		}

		/// <summary>The traffic events' comparison, or null for a run without traffic.</summary>
		public TrafficEventVerifier Traffic { get; }

		/// <summary>True without traffic, or when every traffic event and the encounter digest came out the same.</summary>
		public bool TrafficMatches => Traffic == null || Traffic.Matches;

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

		/// <summary>Every row equal and, when recorded, every state equal bit for bit, and with traffic every event too.</summary>
		public bool Matches => RowsMatch && DigestMatches && TrafficMatches;

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

			if (!TrafficMatches)
			{
				return "every row and state matches, but the traffic does not: " + Traffic;
			}

			var traffic = Traffic != null ? $", and all {Traffic.Compared} traffic events ({Traffic.ReplayedDigest})" : string.Empty;
			return RecordedDigest == null
				? $"replay matches all {RowsCompared} recorded rows (no state digest recorded){traffic}"
				: $"replay matches all {RowsCompared} recorded rows and every state bit for bit ({ReplayedDigest}){traffic}";
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
		private TrafficEventVerifier _traffic;
		private TrafficMonitor _trafficMonitor;

		public ReplayVerifier(RecordedRun run)
		{
			_run = run ?? throw new ArgumentNullException(nameof(run));
			_stepsPerSample = SimulationClock.StepsPerSecond / run.SampleHz;
		}

		/// <summary>The comparison so far; complete once <see cref="Finish"/> has run.</summary>
		public ReplayResult Result => _final ?? WithTraffic(new ReplayResult(_compared, _compared, _firstMismatch, _expected, _actual));

		/// <summary>Also compares the run's traffic events, which <paramref name="monitor"/> hands to <paramref name="traffic"/>.</summary>
		public void AttachTraffic(TrafficEventVerifier traffic, TrafficMonitor monitor)
		{
			_traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
			_trafficMonitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
		}

		private ReplayResult WithTraffic(ReplayResult rows) => _traffic == null ? rows : new ReplayResult(rows, _traffic);

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
				_traffic?.Finish(_trafficMonitor, stepCount);
				_finished = true;
			}

			_final ??= WithTraffic(new ReplayResult(_compared, _run.Rows.Count, _firstMismatch, _expected, _actual, _run.StateDigest, _digest.Text));
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
		/// type); open water when omitted. When the recording carries a <see cref="WorldGeometryDigest"/> and
		/// <paramref name="geometry"/> has another, nothing is replayed and the result says so. A pack run is replayed
		/// against <paramref name="pack"/>, or the pack its header names, found in <paramref name="packs"/> (StreamingAssets,
		/// then the repository, when null); when that pack's day digest is not the recorded one, or it cannot be loaded, nothing
		/// is replayed and the result names both digests. A calm or uniform run given a pack is refused the same way. A run with
		/// traffic is replayed against <paramref name="traffic"/>, or the traffic pack its header names from
		/// <paramref name="trafficPacks"/>, refused the same way for another digest, and its traffic events must come out the
		/// same, row for row, with the same encounter digest; a run without traffic given a traffic pack is refused.
		/// </summary>
		public static ReplayResult Verify(RecordedRun run, VesselParameters parameters, ISimulationFactory factory, IStepObserver observer = null, IWorldGeometry geometry = null,
			FerrySim.Core.Env.EnvironmentPack pack = null, EnvironmentPacks packs = null, TrafficPack traffic = null, TrafficPacks trafficPacks = null)
		{
			geometry ??= EmptyWorldGeometry.Instance;
			if (run.Header.TryGetValue(WorldGeometryDigest.HeaderKey, out var recordedGeometry))
			{
				var replayedGeometry = WorldGeometryDigest.Of(geometry) ?? "unknown (" + geometry.GetType().FullName + ")";
				if (!string.Equals(recordedGeometry, replayedGeometry, StringComparison.Ordinal))
				{
					return new ReplayResult(0, run.Rows.Count, 0, "world geometry " + recordedGeometry, "world geometry " + replayedGeometry);
				}
			}

			if (!RunEnvironment.TryForRecording(run, pack, packs, out var environment, out var recordedEnvironment, out var replayedEnvironment))
			{
				return new ReplayResult(0, run.Rows.Count, 0, recordedEnvironment, replayedEnvironment);
			}

			if (!RecordedTraffic.TryForRecording(run, traffic, trafficPacks, out var trafficRun, out var recordedTraffic, out var replayedTraffic))
			{
				return new ReplayResult(0, run.Rows.Count, 0, recordedTraffic, replayedTraffic);
			}

			var simulation = factory.Create(parameters, environment.StartRun(), geometry, run.InitialState);
			var driver = new FixedStepDriver(simulation, run.Inputs);
			var verifier = new ReplayVerifier(run);
			driver.AddObserver(verifier);
			if (trafficRun != null)
			{
				var monitor = trafficRun.StartRun(HullOutline.FromContact(parameters), RecordedTraffic.Limits(run));
				var events = new TrafficEventVerifier(run.TrafficEvents, trafficRun);
				verifier.AttachTraffic(events, monitor);
				driver.AddObserver(new TrafficStepObserver(monitor) { Verifier = events });
			}
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

	/// <summary>
	/// A recording's traffic for its replay: its pack, which must be the one it was recorded with, the vessel it left out and
	/// its alarm limits, as the run had them.
	/// </summary>
	public static class RecordedTraffic
	{
		/// <summary>
		/// The traffic to replay <paramref name="run"/> with: none for a run without traffic, else <paramref name="pack"/> or the
		/// day its header names from <paramref name="packs"/> (StreamingAssets, then the repository, when null), leaving out the
		/// recorded MMSI. False, with what was recorded and what the replay has (both digests), for another pack, a pack that
		/// cannot be loaded, or a pack given to a run without traffic.
		/// </summary>
		public static bool TryForRecording(RecordedRun run, TrafficPack pack, TrafficPacks packs, out TrafficRun traffic, out string recorded, out string replayed)
		{
			if (run == null)
			{
				throw new ArgumentNullException(nameof(run));
			}

			traffic = null;
			if (!run.HasTraffic)
			{
				recorded = "no traffic";
				replayed = pack != null ? $"traffic pack {pack.Date} (digest {pack.Digest})" : recorded;
				return pack == null;
			}

			recorded = $"traffic pack {run.TrafficPack} (digest {run.TrafficDigest})";
			string folder = null;
			if (pack == null)
			{
				try
				{
					pack = (packs ?? ScenarioTraffic.DefaultPacks).Load(run.TrafficPack, out folder);
				}
				catch (Exception error) when (error is IOException || error is ArgumentException || error is TrafficPackException)
				{
					replayed = "no usable traffic pack: " + error.Message.TrimEnd('.');
					return false;
				}
			}

			replayed = $"traffic pack {pack.Date} (digest {pack.Digest})";
			if (!string.Equals(pack.Digest, run.TrafficDigest, StringComparison.Ordinal))
			{
				return false;
			}

			try
			{
				traffic = new TrafficRun(pack, run.ScenarioStartUtc, run.TrafficLeaveOut, run.TrafficRemovedMmsi, folder);
			}
			catch (ArgumentException error)
			{
				replayed += ", " + error.Message.TrimEnd('.');
				return false;
			}

			return true;
		}

		/// <summary>The alarm limits a run with traffic recorded in its event log's header, the alongside margin among them.</summary>
		public static TrafficAlarmLimits Limits(RecordedRun run)
		{
			var log = run.TrafficEvents ?? throw new InvalidOperationException($"{Path.GetFileName(run.TelemetryPath)} has no traffic event log.");
			double Value(string key) =>
				log.Header.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
					? value
					: throw new FormatException($"{Path.GetFileName(run.TelemetryPath)}: its traffic event log has no number for '{key}'.");
			return new TrafficAlarmLimits(Value("cpa_alarm_m"), Value("tcpa_alarm_s"), Value("closing_speed_mps"), Value("alarm_clear_hold_s"), Value("collision_hold_s"), Value("alongside_margin_m"));
		}
	}

	/// <summary>
	/// Compares a replay's traffic events with a recording's, row for row, character for character, as they come, then the event
	/// count, the open-episode snapshot (when the recording has one) and the encounter digest at the end (<see cref="Finish"/>).
	/// </summary>
	public sealed class TrafficEventVerifier : ITrafficEventSink
	{
		private readonly TrafficEventLogFile _recorded;
		private readonly TrafficRun _traffic;
		private bool _finished;

		public TrafficEventVerifier(TrafficEventLogFile recorded, TrafficRun traffic)
		{
			_recorded = recorded ?? throw new ArgumentNullException(nameof(recorded));
			_traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
		}

		/// <summary>Events the replay produced so far.</summary>
		public int Compared { get; private set; }

		/// <summary>Index of the first event that differs, or −1.</summary>
		public int FirstMismatch { get; private set; } = -1;

		public string Expected { get; private set; }

		public string Actual { get; private set; }

		/// <summary>The recorded and replayed encounter digests (the replayed one once finished).</summary>
		public string RecordedDigest => _recorded.Digest;

		public string ReplayedDigest { get; private set; }

		/// <summary>Open-episode snapshot rows the replay produced at the end, compared when the recording has a snapshot.</summary>
		public int OpenCompared { get; private set; }

		/// <summary>Index of the first open-episode row that differs, or −1.</summary>
		public int FirstOpenMismatch { get; private set; } = -1;

		public string ExpectedOpen { get; private set; }

		public string ActualOpen { get; private set; }

		/// <summary>
		/// Every event equal, as many as recorded, the open-episode snapshot equal when the recording has one, and the same digest;
		/// false until <see cref="Finish"/>.
		/// </summary>
		public bool Matches => _finished && FirstMismatch < 0 && FirstOpenMismatch < 0 && Compared == _recorded.Records.Count &&
			string.Equals(RecordedDigest, ReplayedDigest, StringComparison.Ordinal);

		public void OnTrafficEvent(in TrafficEvent trafficEvent)
		{
			var index = Compared++;
			if (FirstMismatch >= 0)
			{
				return;
			}

			var actual = TrafficEventLog.FormatRow(trafficEvent, _traffic.Pack.Vessels[trafficEvent.Vessel]);
			var expected = index < _recorded.Records.Count ? _recorded.Records[index].Line : "(no event)";
			if (!string.Equals(expected, actual, StringComparison.Ordinal))
			{
				FirstMismatch = index;
				Expected = expected;
				Actual = actual;
			}
		}

		/// <summary>Ends the comparison after <paramref name="stepCount"/> steps of <paramref name="monitor"/>.</summary>
		public void Finish(TrafficMonitor monitor, long stepCount)
		{
			if (_finished)
			{
				return;
			}

			_finished = true;
			ReplayedDigest = monitor.Digest;
			if (FirstMismatch < 0 && Compared != _recorded.Records.Count)
			{
				FirstMismatch = Compared;
				Expected = $"{_recorded.Records.Count} traffic events";
				Actual = $"{Compared} after {stepCount} steps";
			}

			if (_recorded.HasOpenSnapshot)
			{
				var open = new List<string>();
				monitor.ReportOpen(new RowCollector(open, _traffic));
				OpenCompared = open.Count;
				for (var i = 0; i < Math.Max(open.Count, _recorded.Open.Count) && FirstOpenMismatch < 0; i++)
				{
					var expected = i < _recorded.Open.Count ? _recorded.Open[i].Line : "(no open episode)";
					var actual = i < open.Count ? open[i] : "(no open episode)";
					if (!string.Equals(expected, actual, StringComparison.Ordinal))
					{
						FirstOpenMismatch = i;
						ExpectedOpen = expected;
						ActualOpen = actual;
					}
				}
			}
		}

		public override string ToString()
		{
			if (FirstMismatch >= 0)
			{
				return $"traffic event {FirstMismatch} differs:\n  recorded {Expected}\n  replayed {Actual}";
			}

			if (FirstOpenMismatch >= 0)
			{
				return $"open episode {FirstOpenMismatch} at the end differs:\n  recorded {ExpectedOpen}\n  replayed {ActualOpen}";
			}

			if (!string.Equals(RecordedDigest, ReplayedDigest, StringComparison.Ordinal))
			{
				return $"every traffic event matches but the encounters differ: recorded {RecordedDigest}, replayed {ReplayedDigest}";
			}

			return _recorded.HasOpenSnapshot
				? $"all {Compared} traffic events and the {OpenCompared} episodes open at the end match ({ReplayedDigest})"
				: $"all {Compared} traffic events match ({ReplayedDigest}); the recording has no open-episode snapshot";
		}

		/// <summary>Formats the replay's open-episode snapshot rows as the recorder writes them.</summary>
		private sealed class RowCollector : ITrafficEventSink
		{
			private readonly List<string> _rows;
			private readonly TrafficRun _traffic;

			public RowCollector(List<string> rows, TrafficRun traffic)
			{
				_rows = rows;
				_traffic = traffic;
			}

			public void OnTrafficEvent(in TrafficEvent trafficEvent) => _rows.Add(TrafficEventLog.FormatRow(trafficEvent, _traffic.Pack.Vessels[trafficEvent.Vessel]));
		}
	}
}
