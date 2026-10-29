using System;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Core.World;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// The fixed-step bridge. Owns one <see cref="ISimulation"/> from <see cref="VesselDynamicsFactory"/> and
	/// advances it in exact <see cref="SimulationClock.FixedTimeStep"/> steps from a time accumulator in
	/// <c>Update</c> (not Unity's 0.02 s <c>FixedUpdate</c>), then draws the vessel interpolated between the
	/// last two physics states. Every run is recorded for replay and debrief. The water and air come from the scenario
	/// (<see cref="RunEnvironment"/>: calm, uniform or an environment pack's day). UI and cameras read it through
	/// <see cref="IVesselTelemetrySource"/> and <see cref="IEnvironmentReadoutSource"/>. A scenario with a traffic day replays
	/// that day's harbor traffic (<see cref="Traffic"/>): its CPA alarms and collisions are watched at every step, recorded and
	/// replayed (Sim/README.md, Traffic), and read through <see cref="ITrafficReadoutSource"/>.
	/// </summary>
	[DefaultExecutionOrder(-100)]
	[DisallowMultipleComponent]
	public sealed class VesselSimulationRunner : MonoBehaviour, IVesselTelemetrySource, IEnvironmentReadoutSource, ITrafficReadoutSource, IRunHoldSource
	{
		private const double DropReportIntervalSeconds = 5.0;
		private static long _nextRunId;

		/// <summary>The readout before any run: no pack, no tide, still water and air.</summary>
		private static readonly EnvironmentReadout NoEnvironment =
			new EnvironmentReadout(EnvironmentKind.Calm, string.Empty, string.Empty, TimeSpan.Zero, double.NaN, double.NaN, HorizontalVector.Zero, HorizontalVector.Zero, false, 0, 0);

		[Tooltip("Scenario to start from. Ignored when a scenario JSON file is set.")]
		[SerializeField]
		private ScenarioAsset scenario;

		[Tooltip("Optional scenario JSON, relative to the repository root (or StreamingAssets in a player).")]
		[SerializeField]
		private string scenarioJsonFile = string.Empty;

		[Tooltip("Component supplying the controls (for example HelmInputController). Empty: the first IControlInputSource on this GameObject.")]
		[SerializeField]
		private MonoBehaviour controlSource;

		[Tooltip("Most physics steps one frame may run; a slower frame drops the excess time and logs it.")]
		[SerializeField]
		[Min(1)]
		private int maxStepsPerFrame = FixedStepDriver.DefaultMaxStepsPerFrame;

		[SerializeField]
		private bool recordRuns = true;

		[Tooltip("Where runs are recorded (never inside Assets). Relative to the Unity project in the editor, persistent data in a player. Empty: artifacts/unity/runs in the editor.")]
		[SerializeField]
		private string recordingDirectory = string.Empty;

		[Tooltip("Telemetry CSV rows per simulated second; must divide 100.")]
		[SerializeField]
		private int telemetrySampleHz = 10;

		[Tooltip("Optional: a recorded run's telemetry CSV to play back (and verify) instead of live controls.")]
		[SerializeField]
		private string replayRecording = string.Empty;

		[Tooltip("Flat placement only: put the render origin at the start position so the vessel starts at Unity (0, 0, 0).")]
		[SerializeField]
		private bool recenterRenderOriginOnStart = true;

		[Tooltip("Flat placement only: Unity height of the calm water surface in the parent frame.")]
		[SerializeField]
		private float waterSurfaceHeight;

		[Tooltip("Transform that is moved to the vessel pose. Empty: this GameObject's transform.")]
		[SerializeField]
		private Transform renderTarget;

		private FixedStepDriver _driver;
		private ISimulationFactory _factory;
		private IWorldGeometry _worldGeometry = EmptyWorldGeometry.Instance;
		private IVesselPlacement _placement;
		private TangentPlanePlacement _flatPlacement;
		private RunEnvironment _environment;
		private EnvironmentPacks _packs;
		private EnvironmentReadout _readout = NoEnvironment;
		private long _readoutRun = -1;
		private long _readoutStep = -1;
		private VesselState _initialState;
		private string _vesselDigest;
		private HarborHoldObserver _harborHoldObserver;
		private RunRecorder _recorder;
		private RecordedRun _replay;
		private ReplayVerifier _replayVerifier;
		private ReplayResult _lastReplayResult;
		private readonly ContactObserver _contactObserver = new ContactObserver();
		private TrafficPacks _trafficPacks;
		private TrafficRun _traffic;
		private HullOutline _trafficOutline;
		private TrafficAlarmLimits _trafficLimits = TrafficAlarmLimits.Default;
		private TrafficAlarmLimits _runTrafficLimits;
		private TrafficStepObserver _trafficObserver;
		private int _runNumber;
		private bool _runActive;
		private double _lastDropReportTime = double.NegativeInfinity;
		private double _unreportedDrop;
		private int _unreportedDropFrames;

		/// <summary>Raised after a run starts: on initialization and on every reset.</summary>
		public event Action RunStarted;

		/// <summary>Raised when a run ends (reset, replay end or shutdown), after its recording is closed.</summary>
		public event Action RunEnded;

		/// <summary>Raised for every frame that hit the per-frame step guard.</summary>
		public event Action<FrameAdvance> FrameDropped;

		/// <summary>
		/// Raised just before the vessel is placed at its render pose: every frame after its physics steps (also while paused)
		/// and at every run start. Whatever the placement reads that follows the run, such as the drawn tide, updates here.
		/// </summary>
		public event Action BeforePlacement;

		public VesselParameters Parameters { get; private set; }

		public VesselState State => _driver != null ? _driver.CurrentState : default;

		public VesselTelemetry Telemetry => _driver != null ? _driver.Simulation.Telemetry : default;

		public ControlInputs Controls => _driver != null ? _driver.LastControls : ControlInputs.Neutral;

		public DateTimeOffset ScenarioStartUtc { get; private set; }

		public bool HasTelemetry => _runActive && _driver != null && _driver.StepCount > 0 && !_driver.Faulted;

		public long RunId { get; private set; }

		public ContactLatch Contact => _contactObserver.Contact;

		public bool HasEnvironment => HasTelemetry && _environment != null;

		/// <summary>
		/// The water and air at the vessel's reference point at the most recent physics step (at the start before the first),
		/// worked out once per step from <see cref="RunEnvironment"/>. Read it only while <see cref="HasEnvironment"/>.
		/// </summary>
		public EnvironmentReadout Environment
		{
			get
			{
				if (_environment == null || _driver == null)
				{
					return NoEnvironment;
				}

				if (_readoutRun != RunId || _readoutStep != _driver.StepCount)
				{
					var state = _driver.CurrentState;
					_readout = _environment.Readout(state.North, state.East, _driver.SimulationTime);
					_readoutRun = RunId;
					_readoutStep = _driver.StepCount;
				}

				return _readout;
			}
		}

		/// <summary>The current run's water and air: what its simulation samples, and the readout and tide built on it. Null before the first run.</summary>
		public RunEnvironment RunEnvironment => _environment;

		/// <summary>The active harbor runtime; null in slice/uniform mode.</summary>
		public HarborRuntimePack HarborRuntime { get; private set; }

		/// <summary>The latched harbor coverage hold; only a new run clears it.</summary>
		public RunHold Hold { get; private set; }

		/// <summary>Preparation cost of the latest successful harbor start.</summary>
		public HarborStartTimings HarborLoadTimings { get; private set; }

		/// <summary>Last actual TrafficMonitor.Update cost, including event dispatch, in milliseconds.</summary>
		public double TrafficStepMilliseconds => _trafficObserver?.LastMilliseconds ?? 0;

		/// <summary>Rolling 95th percentile of the latest 256 traffic steps, in milliseconds.</summary>
		public double TrafficStepP95Milliseconds => _trafficObserver?.P95Milliseconds ?? 0;

		/// <summary>Where scenarios and replays find environment packs: StreamingAssets, then the repository, unless replaced.</summary>
		public EnvironmentPacks EnvironmentPacks
		{
			get => _packs ?? EnvironmentPacks.Default;
			set => _packs = value;
		}

		/// <summary>Where scenarios and replays find traffic packs: StreamingAssets, then the repository, unless replaced.</summary>
		public TrafficPacks TrafficPacks
		{
			get => _trafficPacks ?? ScenarioTraffic.DefaultPacks;
			set => _trafficPacks = value;
		}

		/// <summary>The current scenario's traffic, or null for a run without traffic.</summary>
		public TrafficRun Traffic => _traffic;

		/// <summary>The current run's traffic monitor (alarms, collisions, the readout), or null without traffic.</summary>
		public TrafficMonitor TrafficMonitor => _trafficObserver?.Monitor;

		/// <summary>The CPA alarm's limits for new live runs (estimates, Core Traffic/README.md); a replay uses the ones it recorded.</summary>
		public TrafficAlarmLimits TrafficLimits
		{
			get => _trafficLimits;
			set => _trafficLimits = value;
		}

		public bool TrafficEnabled => _traffic != null;

		public bool HasTraffic => HasTelemetry && _trafficObserver != null && _trafficObserver.Monitor.LastStep == _driver.StepCount;

		/// <summary>The traffic at the most recent physics step: the alarm state and its target. Read it only while <see cref="HasTraffic"/>.</summary>
		public TrafficAlarmReadout TrafficAlarm => _trafficObserver != null ? _trafficObserver.Monitor.Readout : default;

		public bool IsInitialized => _driver != null;

		public bool IsPaused { get; private set; }

		/// <summary>Only a new run/reset can reactivate an ended run, including after disable/re-enable.</summary>
		public bool IsRunActive => _runActive;

		/// <summary>True while playing back a recorded run instead of live controls.</summary>
		public bool IsReplaying => _replay != null;

		/// <summary>The recording being replayed, or null.</summary>
		public RecordedRun Replay => _replay;

		public FixedStepDriver Driver => _driver;

		public ScenarioSettings Scenario { get; private set; }

		/// <summary>Pose drawn this frame: interpolated between the last two physics states.</summary>
		public VesselPose RenderPose { get; private set; }

		/// <summary>Time interpolated between the states used by RenderPose, for harbor moving visuals and water.</summary>
		public double RenderTime => _driver == null ? 0 : _driver.PreviousState.Time +
			(_driver.CurrentState.Time - _driver.PreviousState.Time) * _driver.InterpolationFactor;

		public IVesselPlacement Placement => _placement;

		/// <summary>The flat placement in use, or null when another <see cref="IVesselPlacement"/> places the vessel.</summary>
		public TangentPlanePlacement FlatPlacement => _flatPlacement;

		public IControlInputSource ControlSource => _driver?.Controls;

		/// <summary>The recorder of the current run, or null when not recording.</summary>
		public RunRecorder Recorder => _recorder;

		/// <summary>Telemetry CSV of the most recently finished recording.</summary>
		public string LastRecordingPath { get; private set; }

		/// <summary>Replay comparison so far, when replaying.</summary>
		public ReplayResult ReplayResult => _replayVerifier?.Result ?? _lastReplayResult;

		public bool ReplayCompleted { get; private set; }

		/// <summary>Why the current recording is missing/incomplete, retained for the debrief readout.</summary>
		public string RecordingError { get; private set; }

		/// <summary>The factory used for new runs; <see cref="VesselDynamicsFactory"/> unless replaced before initialization.</summary>
		public ISimulationFactory Factory
		{
			get => _factory ??= new VesselDynamicsFactory();
			set => _factory = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// Fixed structures the hull can touch (landing faces, piers), built by the world from the data pipeline.
		/// Open water unless set; takes effect at the next run start. A replay of a run needs the same geometry:
		/// recordings carry its <see cref="WorldGeometryDigest"/>, and <see cref="InitializeReplay"/> refuses another.
		/// </summary>
		public IWorldGeometry WorldGeometry
		{
			get => _worldGeometry;
			set => _worldGeometry = value ?? EmptyWorldGeometry.Instance;
		}

		public bool RecordRuns
		{
			get => recordRuns;
			set => recordRuns = value;
		}

		public string RecordingDirectory
		{
			get => SimPaths.ResolveRecordingDirectory(string.IsNullOrEmpty(recordingDirectory) ? SimPaths.DefaultRecordingDirectory : recordingDirectory);
			set => recordingDirectory = value;
		}

		public int TelemetrySampleHz
		{
			get => telemetrySampleHz;
			set => telemetrySampleHz = value;
		}

		public int MaxStepsPerFrame
		{
			get => maxStepsPerFrame;
			set
			{
				maxStepsPerFrame = value;
				if (_driver != null)
				{
					_driver.MaxStepsPerFrame = value;
				}
			}
		}

		/// <summary>
		/// Starts a run from <paramref name="settings"/>. Controls come from <paramref name="controls"/>, else the
		/// inspector's control source, else the first <see cref="IControlInputSource"/> on this GameObject. The scenario is
		/// checked and its vessel and environment loaded before the current run ends, so a scenario that cannot start (an
		/// unknown pack, a start outside its day, a missing vessel file) throws and leaves the current run going.
		/// </summary>
		public void Initialize(ScenarioSettings settings, IControlInputSource controls = null, VesselParameters parameters = null)
		{
			if (settings == null)
			{
				throw new ArgumentNullException(nameof(settings));
			}

			settings.Validate(EnvironmentPacks, TrafficPacks);
			var vessel = RunRecorder.LoadVesselSnapshot(settings.vesselFile, parameters, out var vesselDigest);
			var environment = settings.CreateEnvironment(EnvironmentPacks);
			var traffic = Verified(() => settings.CreateTraffic(TrafficPacks));
			var outline = traffic != null ? TrafficOutline(vessel, settings.name) : null;
			EndRun();
			HarborRuntime = null;
			_replay = null;
			_replayVerifier = null;
			Scenario = settings;
			Parameters = vessel;
			_vesselDigest = vesselDigest;
			ScenarioStartUtc = settings.StartUtc;
			_environment = environment;
			_traffic = traffic;
			_trafficOutline = outline;
			_runTrafficLimits = _trafficLimits;
			_initialState = settings.InitialState(_environment.Observer);
			StartRun(controls ?? FindControlSource());
		}

		/// <summary>
		/// Prepares the complete harbor bundle, vessel bytes and core before ending the current run. The contact parser
		/// receives verified text and its exact source path. An optional start gate runs against the candidate only.
		/// </summary>
		public void InitializeHarbor(ScenarioSettings settings, string runtimeFolder, Func<string, string, IWorldGeometry> parseContacts,
			IControlInputSource controls = null, VesselParameters parameters = null,
			Action<PreparedHarborStart, VesselParameters, VesselState> validateStart = null)
		{
			var packs = _packs ??= EnvironmentPacks.Default;
			var candidate = packs.PrepareHarbor(settings, runtimeFolder, parseContacts, TrafficPacks);
			var vessel = RunRecorder.LoadVesselSnapshot(settings.vesselFile, parameters, out var vesselDigest);
			var initial = settings.InitialState(candidate.Environment.Observer);
			if (!candidate.Runtime.Geoid.TrySample(initial.North, initial.East, out _))
			{
				throw new InvalidOperationException(Path.Combine(runtimeFolder, "geoid.json") + ": the vessel start is outside the active datum lattice.");
			}

			validateStart?.Invoke(candidate, vessel, initial);
			var outline = candidate.Traffic != null ? TrafficOutline(vessel, settings.name) : null;
			var simulation = Factory.Create(vessel, candidate.Environment.StartRun(), candidate.Contacts, initial);
			var trafficObserver = candidate.Traffic != null ? new TrafficStepObserver(candidate.Traffic.StartRun(outline, _trafficLimits)) : null;
			var input = controls ?? FindControlSource();

			EndRun();
			_replay = null;
			_replayVerifier = null;
			Scenario = settings;
			Parameters = vessel;
			_vesselDigest = vesselDigest;
			ScenarioStartUtc = settings.StartUtc;
			_environment = candidate.Environment;
			_worldGeometry = candidate.Contacts;
			HarborRuntime = candidate.Runtime;
			HarborLoadTimings = candidate.Timings;
			_traffic = candidate.Traffic;
			_trafficOutline = outline;
			_runTrafficLimits = _trafficLimits;
			_initialState = initial;
			StartRun(input, simulation, trafficObserver);
		}

		/// <summary>
		/// Plays back <paramref name="run"/> from its recorded start, checking every row as it goes. Not re-recorded. A pack
		/// run plays against <paramref name="pack"/>, or the pack its header names from <see cref="EnvironmentPacks"/>; a pack
		/// with another day digest is refused, naming both, before the current run ends, as are other world geometry and a
		/// calm or uniform run given a pack. A run with traffic plays against <paramref name="traffic"/>, or the traffic pack its
		/// header names from <see cref="TrafficPacks"/>, refused the same way for another digest, leaving out the recorded MMSI,
		/// with its recorded alarm limits; its events are checked as they come.
		/// </summary>
		public void InitializeReplay(RecordedRun run, VesselParameters parameters = null, EnvironmentPack pack = null, TrafficPack traffic = null)
		{
			if (run == null)
			{
				throw new ArgumentNullException(nameof(run));
			}

			if (run.Header.TryGetValue(WorldGeometryDigest.HeaderKey, out var recordedGeometry) &&
				!string.Equals(recordedGeometry, WorldGeometryDigest.Of(_worldGeometry), StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					$"{Path.GetFileName(run.TelemetryPath)} was recorded against world geometry {recordedGeometry}, but this runner has " +
					$"{WorldGeometryDigest.Of(_worldGeometry) ?? _worldGeometry.GetType().FullName}; set the same WorldGeometry before replaying.");
			}

			var environment = RunEnvironment.ForRecording(run, pack, EnvironmentPacks);
			if (!RecordedTraffic.TryForRecording(run, traffic, TrafficPacks, out var trafficRun, out var recordedTraffic, out var replayedTraffic))
			{
				throw new InvalidOperationException($"{Path.GetFileName(run.TelemetryPath)} was recorded against {recordedTraffic}, but the replay has {replayedTraffic}; replay it against the same traffic.");
			}

			run.Header.TryGetValue(RunRecorder.VesselDigestKey, out var recordedVessel);
			string vesselDigest = null;
			var vessel = recordedVessel == null && parameters != null ? parameters :
				RunRecorder.LoadVesselSnapshot(run.VesselFile, parameters, out vesselDigest, recordedVessel);
			var outline = trafficRun != null ? TrafficOutline(vessel, Path.GetFileName(run.TelemetryPath)) : null;
			var limits = trafficRun != null ? RecordedTraffic.Limits(run) : _trafficLimits;
			EndRun();
			HarborRuntime = null;
			Scenario = null;
			Parameters = vessel;
			_vesselDigest = vesselDigest;
			ScenarioStartUtc = run.ScenarioStartUtc;
			_environment = environment;
			_traffic = trafficRun;
			_trafficOutline = outline;
			_runTrafficLimits = limits;
			_initialState = run.InitialState;
			_replay = run;
			StartRun(run.Inputs);
		}

		/// <summary>Ends the current run (closing its recording) and starts a new one from the scenario start.</summary>
		public void ResetRun()
		{
			if (!IsInitialized)
			{
				return;
			}

			var controls = _driver.Controls;
			EndRun();
			StartRun(controls);
		}

		public void Pause() => IsPaused = true;

		public void Resume() => IsPaused = false;

		public void TogglePause() => IsPaused = !IsPaused;

		/// <summary>
		/// Advances one frame of <paramref name="deltaTime"/> seconds unless paused. <c>Update</c> calls this with
		/// the frame time; tests may call it directly with simulated frame times.
		/// </summary>
		public FrameAdvance AdvanceFrame(double deltaTime)
		{
			if (!IsInitialized || !_runActive || IsPaused || Hold != RunHold.None)
			{
				return new FrameAdvance(0, 0.0);
			}

			var advance = _driver.Advance(deltaTime);
			if (_recorder?.Failure != null)
			{
				// Detach after the observer loop so other observers still see every step in this frame.
				_driver.RemoveObserver(_recorder);
				if (_trafficObserver != null)
				{
					_trafficObserver.Recorder = null;
				}

				ReportRecordingFailure(_recorder.Failure);
				_recorder = null;
			}
			if (advance.Dropped)
			{
				ReportDrop(advance);
			}

			if (_driver.Faulted)
			{
				Debug.LogError($"[FerrySim] The model produced a non-finite state at t={_driver.SimulationTime:F2} s; the run is stopped.", this);
				Pause();
				EndRun();
			}
			else if (_replay != null && _driver.StepCount >= _replay.StepCount)
			{
				FinishReplay();
			}

			return advance;
		}

		/// <summary>Ends the current run: closes its recording. The vessel stays where it is until the next run starts.</summary>
		public void EndRun()
		{
			if (_driver == null)
			{
				return;
			}

			var wasActive = _runActive;
			_runActive = false;
			Hold = RunHold.None;
			if (_trafficObserver != null)
			{
				// The monitor stays readable (its last readout and counts) until the next run starts; it sees no more steps.
				_driver.RemoveObserver(_trafficObserver);
			}

			if (_replayVerifier != null)
			{
				_driver.RemoveObserver(_replayVerifier);
				_lastReplayResult = _replayVerifier.Result;
				_replayVerifier = null;
			}

			if (_recorder != null)
			{
				_driver.RemoveObserver(_recorder);
				try
				{
					_recorder.Finish(_driver.StepCount, _driver.CurrentState, _driver.Simulation.Telemetry);
					if (_recorder.Failure == null) LastRecordingPath = _recorder.TelemetryPath;
					else ReportRecordingFailure(_recorder.Failure);
				}
				catch (Exception exception)
				{
					ReportRecordingFailure(exception);
				}
				finally { _recorder.Dispose(); }

				_recorder = null;
			}

			if (wasActive)
			{
				RunEnded?.Invoke();
			}
		}

		private void Start()
		{
			if (IsInitialized)
			{
				return;
			}

			try
			{
				if (!string.IsNullOrEmpty(replayRecording))
				{
					InitializeReplay(RecordedRun.Load(SimPaths.Resolve(replayRecording)));
				}
				else if (!string.IsNullOrEmpty(scenarioJsonFile))
				{
					Initialize(ScenarioAsset.LoadJson(scenarioJsonFile));
				}
				else if (scenario != null)
				{
					Initialize(scenario.Settings);
				}
				else
				{
					Debug.LogWarning("[FerrySim] VesselSimulationRunner has no scenario; call Initialize or assign one.", this);
				}
			}
			catch (Exception exception)
			{
				Debug.LogException(exception, this);
				enabled = false;
			}
		}

		private void Update()
		{
			if (!IsInitialized)
			{
				return;
			}

			AdvanceFrame(FrameTime());
			ApplyRenderPose();
		}

		/// <summary>
		/// Simulated time owed for this frame. Unity clamps <c>Time.deltaTime</c> to <c>Time.maximumDeltaTime</c>
		/// (a third of a second), which would hide most of a long hitch; the unscaled frame time keeps the
		/// accounting honest and the step guard still caps the work. A capture frame rate (tests, recording)
		/// fixes <c>Time.deltaTime</c> instead, so it is used as is.
		/// </summary>
		private static double FrameTime() =>
			Time.captureDeltaTime > 0f ? Time.deltaTime : (double)Time.unscaledDeltaTime * Time.timeScale;

		private void OnDisable() => EndRun();

		private void OnApplicationQuit() => EndRun();

		private void OnValidate()
		{
			maxStepsPerFrame = Math.Max(1, maxStepsPerFrame);
			if (telemetrySampleHz < 1 || telemetrySampleHz > SimulationClock.StepsPerSecond || SimulationClock.StepsPerSecond % telemetrySampleHz != 0)
			{
				telemetrySampleHz = 10;
			}
		}

		/// <summary>Moves the render target to the interpolated pose. Runs every frame after stepping, also while paused.</summary>
		public void ApplyRenderPose()
		{
			if (!IsInitialized || !_runActive)
			{
				return;
			}

			RenderPose = VesselPose.Interpolate(_driver.PreviousState, _driver.CurrentState, _driver.InterpolationFactor);
			if (Hold == RunHold.OutsideCoveredWater)
			{
				// The held pose is past the water the run has data for, where the harbor datum refuses a sample; the
				// vessel keeps the position it was last drawn at until a new start.
				return;
			}

			BeforePlacement?.Invoke();
			_placement.Place(renderTarget != null ? renderTarget : transform, RenderPose, UsesHarborWater ? RenderTime : _driver.SimulationTime);
		}

		// A harbor replay has its recorded environment, while the scene supplies the verified datum.
		private bool UsesHarborWater => HarborRuntime != null || (_replay != null && _environment.Pack?.Region == "harbor");

		private void StartRun(IControlInputSource controls, ISimulation preparedSimulation = null, TrafficStepObserver preparedTraffic = null)
		{
			_lastReplayResult = null;
			ReplayCompleted = false;
			RecordingError = null;
			var simulation = preparedSimulation ?? Factory.Create(Parameters, _environment.StartRun(), _worldGeometry, _initialState);
			RunId = System.Threading.Interlocked.Increment(ref _nextRunId);
			_contactObserver.Reset();
			if (_driver == null)
			{
				_driver = new FixedStepDriver(simulation, controls, maxStepsPerFrame);
				_driver.AddObserver(_contactObserver);
			}
			else
			{
				_driver.Controls = controls;
				_driver.Restart(simulation);
			}

			Hold = RunHold.None;
			if (_harborHoldObserver != null)
			{
				_driver.RemoveObserver(_harborHoldObserver);
			}
			_harborHoldObserver = HarborRuntime != null ? new HarborHoldObserver(this) : null;
			if (_harborHoldObserver != null)
			{
				_driver.AddObserver(_harborHoldObserver);
			}

			_driver.StepLimit = _replay != null ? _replay.StepCount : (long?)null;
			_placement = FindPlacement();
			IsPaused = false;
			_runNumber++;
			_unreportedDrop = 0.0;
			_unreportedDropFrames = 0;
			if (_trafficObserver != null)
			{
				_driver.RemoveObserver(_trafficObserver);
			}

			_trafficObserver = preparedTraffic ?? (_traffic != null ? new TrafficStepObserver(_traffic.StartRun(_trafficOutline, _runTrafficLimits)) : null);
			if (_replay != null)
			{
				_replayVerifier = new ReplayVerifier(_replay);
				_driver.AddObserver(_replayVerifier);
				if (_trafficObserver != null)
				{
					var events = new TrafficEventVerifier(_replay.TrafficEvents, _traffic);
					_replayVerifier.AttachTraffic(events, _trafficObserver.Monitor);
					_trafficObserver.Verifier = events;
				}
			}
			else if (recordRuns)
			{
				StartRecording(simulation);
			}

			if (_trafficObserver != null)
			{
				_trafficObserver.Recorder = _recorder;
				_driver.AddObserver(_trafficObserver);
			}

			_runActive = true;
			if (UsesHarborWater)
			{
				// Bind the new spatial water and views before placing the new start against them.
				RunStarted?.Invoke();
				ApplyRenderPose();
			}
			else
			{
				ApplyRenderPose();
				RunStarted?.Invoke();
			}
		}

		private void StartRecording(ISimulation simulation)
		{
			try
			{
				var header = RunRecorder.BuildHeader(Scenario, Parameters, simulation, Factory.GetType().FullName, _initialState, _environment, telemetrySampleHz, _vesselDigest);
				header.Add(new System.Collections.Generic.KeyValuePair<string, string>(
					"world_geometry", _worldGeometry is EmptyWorldGeometry ? "none" : _worldGeometry.GetType().FullName));
				var geometryDigest = WorldGeometryDigest.Of(_worldGeometry);
				if (geometryDigest != null)
				{
					header.Add(new System.Collections.Generic.KeyValuePair<string, string>(WorldGeometryDigest.HeaderKey, geometryDigest));
				}
				if (_traffic != null)
				{
					header.AddRange(RunRecorder.TrafficHeader(_traffic));
				}
				_recorder = _traffic != null
					? RunRecorder.Create(RecordingDirectory, UniqueRunName(), telemetrySampleHz, header, _traffic, _trafficObserver.Monitor)
					: RunRecorder.Create(RecordingDirectory, UniqueRunName(), telemetrySampleHz, header);
				_driver.AddObserver(_recorder);
			}
			catch (Exception exception)
			{
				_recorder = null;
				ReportRecordingFailure(exception);
			}
		}

		private void ReportRecordingFailure(Exception exception)
		{
			RecordingError = exception.Message;
			Debug.LogWarning($"[FerrySim] Recording incomplete: {RecordingError}. Physics continues; this recording cannot be used as a complete replay.", this);
		}

		private string UniqueRunName()
		{
			var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			var baseName = $"{stamp}-{Parameters.Id}-run{_runNumber:D2}";
			var name = baseName;
			for (var suffix = 2; File.Exists(Path.Combine(RecordingDirectory, name + ".csv")); suffix++)
			{
				name = $"{baseName}-{suffix}";
			}

			return name;
		}

		private void FinishReplay()
		{
			var result = _replayVerifier.Finish(_driver.StepCount, _driver.CurrentState, _driver.Simulation.Telemetry);
			ReplayCompleted = true;
			if (result.Matches)
			{
				Debug.Log($"[FerrySim] Replay of {Path.GetFileName(_replay.TelemetryPath)}: {result}", this);
			}
			else
			{
				Debug.LogError($"[FerrySim] Replay of {Path.GetFileName(_replay.TelemetryPath)}: {result}", this);
			}

			Pause();
			EndRun();
		}

		private void ReportDrop(FrameAdvance advance)
		{
			FrameDropped?.Invoke(advance);
			_unreportedDrop += advance.DroppedTime;
			_unreportedDropFrames++;
			var now = Time.realtimeSinceStartupAsDouble;
			if (now - _lastDropReportTime < DropReportIntervalSeconds)
			{
				return;
			}

			Debug.LogWarning(
				$"[FerrySim] Frame fell behind: dropped {_unreportedDrop:F2} s of simulated time over {_unreportedDropFrames} frame(s) " +
				$"(max {_driver.MaxStepsPerFrame} steps per frame; last frame {Time.unscaledDeltaTime * 1000f:F0} ms). The simulation runs slower than real time while this lasts.",
				this);
			_lastDropReportTime = now;
			_unreportedDrop = 0.0;
			_unreportedDropFrames = 0;
		}

		private IControlInputSource FindControlSource()
		{
			if (controlSource is IControlInputSource assigned)
			{
				return assigned;
			}

			if (TryGetComponent<IControlInputSource>(out var local))
			{
				return local;
			}

			Debug.LogWarning("[FerrySim] No control source found; the vessel runs with neutral controls.", this);
			return new ConstantControls(ControlInputs.Neutral);
		}

		private IVesselPlacement FindPlacement()
		{
			if (TryGetComponent<IVesselPlacement>(out var custom))
			{
				_flatPlacement = null;
				return custom;
			}

			_flatPlacement ??= new TangentPlanePlacement();
			_flatPlacement.WaterSurfaceHeight = waterSurfaceHeight;
			if (recenterRenderOriginOnStart)
			{
				_flatPlacement.RenderOriginNorth = _initialState.North;
				_flatPlacement.RenderOriginEast = _initialState.East;
			}

			return _flatPlacement;
		}

		private sealed class HarborHoldObserver : IStepObserver
		{
			private readonly VesselSimulationRunner _runner;
			private readonly long _depthMisses;
			private readonly long _timeHolds;

			public HarborHoldObserver(VesselSimulationRunner runner)
			{
				_runner = runner;
				var coverage = runner._environment.PackRun.Coverage;
				_depthMisses = coverage.DepthMisses;
				_timeHolds = coverage.TimeHolds;
			}

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				var readout = _runner.Environment;
				if (readout.DepthMisses > _depthMisses)
				{
					_runner.Hold = RunHold.OutsideCoveredWater;
				}
				else if (readout.TimeHolds > _timeHolds)
				{
					_runner.Hold = RunHold.BeyondDayData;
				}

				if (_runner.Hold != RunHold.None)
				{
					_runner._driver.StepLimit = stepCount;
				}
			}
		}

		/// <summary>One observer for the driver's lifetime, updated after every completed finite physics step.</summary>
		private sealed class ContactObserver : IStepObserver
		{
			public ContactLatch Contact { get; private set; } = ContactLatch.None;

			public void Reset() => Contact = ContactLatch.None;

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				var force = telemetry.ContactForce;
				if (!(force > 0.0)) return;
				var step = stepCount - 1;
				var peak = force > Contact.PeakContactForce;
				Contact = new ContactLatch(Contact.StepsWithContact + 1, step, force,
					peak ? force : Contact.PeakContactForce, peak ? step : Contact.PeakContactStep);
			}
		}

		/// <summary>The vessel's contact outline for collisions with traffic; a vessel file without one cannot run with traffic.</summary>
		private static HullOutline TrafficOutline(VesselParameters vessel, string label)
		{
			try
			{
				return HullOutline.FromContact(vessel);
			}
			catch (ArgumentException error)
			{
				throw new ArgumentException($"Scenario '{label}': traffic needs the vessel's contact outline for collisions ({error.Message}).", error);
			}
		}

		/// <summary>A traffic pack that does not verify, as the refusal a scenario start reports (its message names the file).</summary>
		private static TrafficRun Verified(Func<TrafficRun> load)
		{
			try
			{
				return load();
			}
			catch (TrafficPackException error)
			{
				throw new InvalidOperationException(error.Message, error);
			}
		}

	}

	/// <summary>
	/// Steps a run's <see cref="TrafficMonitor"/> after every physics step (the state the step ended at) and hands its events to
	/// the run's recorder and, in a replay, its verifier. Allocation-free per step.
	/// </summary>
	public sealed class TrafficStepObserver : IStepObserver, ITrafficEventSink
	{
		private readonly TrafficStepTimingWindow _timing = new TrafficStepTimingWindow();

		/// <summary>Last monitor update cost, including event dispatch, in milliseconds.</summary>
		public double LastMilliseconds => _timing.LastMilliseconds;

		/// <summary>Rolling nearest-rank p95 of the latest 256 monitor updates.</summary>
		public double P95Milliseconds => _timing.P95Milliseconds;

		public TrafficStepObserver(TrafficMonitor monitor)
		{
			Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
		}

		public TrafficMonitor Monitor { get; }

		/// <summary>The run's recorder, or null when the run is not recorded.</summary>
		public ITrafficEventSink Recorder { get; set; }

		/// <summary>A replay's verifier, or null for a live run.</summary>
		public ITrafficEventSink Verifier { get; set; }

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			var start = System.Diagnostics.Stopwatch.GetTimestamp();
			Monitor.Update(stepCount, state, this);
			_timing.Record((System.Diagnostics.Stopwatch.GetTimestamp() - start) * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
		}

		public void OnTrafficEvent(in TrafficEvent trafficEvent)
		{
			Recorder?.OnTrafficEvent(trafficEvent);
			Verifier?.OnTrafficEvent(trafficEvent);
		}
	}
	/// <summary>A fixed 256-step timing window with an allocation-free sorted sample set.</summary>
	public sealed class TrafficStepTimingWindow
	{
		private const int Capacity = 256;
		private readonly double[] _history = new double[Capacity];
		private readonly double[] _sorted = new double[Capacity];
		private int _next;
		private int _count;

		/// <summary>Most recent timing sample in milliseconds.</summary>
		public double LastMilliseconds { get; private set; }

		/// <summary>Nearest-rank p95 over at most the latest 256 samples.</summary>
		public double P95Milliseconds => _count == 0 ? 0 : _sorted[(95 * _count + 99) / 100 - 1];

		/// <summary>Records one finite nonnegative cost in milliseconds.</summary>
		public void Record(double milliseconds)
		{
			if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(milliseconds));
			}

			if (_count == Capacity)
			{
				var old = _history[_next];
				var index = 0;
				while (_sorted[index] != old)
				{
					index++;
				}
				for (var i = index; i < _count - 1; i++)
				{
					_sorted[i] = _sorted[i + 1];
				}
				_count--;
			}

			var at = _count;
			while (at > 0 && _sorted[at - 1] > milliseconds)
			{
				_sorted[at] = _sorted[at - 1];
				at--;
			}
			_sorted[at] = milliseconds;
			_count++;
			_history[_next] = milliseconds;
			_next = (_next + 1) % Capacity;
			LastMilliseconds = milliseconds;
		}
	}

}
