using System;
using CesiumForUnity;
using FerrySim.Controls;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.UI;
using FerrySim.World;
using FerrySim.World.HarborTiles;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Harbor
{
	/// <summary>Composition only. The supplied launcher owns atomic starts; the existing runner owns physics and recording.</summary>
	[DefaultExecutionOrder(-200)]
	[DisallowMultipleComponent]
	public sealed class HarborBootstrap : MonoBehaviour, IScenarioLauncher
	{
		[SerializeField] private TextAsset configuration;
		[SerializeField] private HarborSceneServiceFactory serviceFactory;
		[SerializeField] private VesselSimulationRunner runner;
		[SerializeField] private CesiumVesselPlacement placement;
		[SerializeField] private CesiumGeoreference georeference;
		[SerializeField] private CameraRig cameraRig;
		[SerializeField] private WheelhouseUI wheelhouseUI;
		[SerializeField] private HelmInputController helm;
		[SerializeField] private SunController sun;
		[SerializeField] private HarborTileset[] renderLayers;
		[SerializeField] private Transform waterRoot;
		[SerializeField] private Transform navigationRoot;
		[SerializeField] private Transform trafficRoot;
		private IHarborSceneServices _services;
		private HarborInitialView _initialView;
		private IHarborViewLayer[] _viewLayers;
		private HelmInputMapper _mapper;
		private WheelhouseView _view;
		private Label _statusLabel;
		private VisualElement _statusRoot;
		private string _drawnStatus;
		private bool _heldHelm;
		private bool _pickerPausedRun;
		private bool _awaitingInitialView;
		private bool _openPicker;
		private bool _subscribed;
		private string _fatalProblem;
		private CameraRig.Mode _shownMode = (CameraRig.Mode)(-1);
		private RunHold _shownHold;

		public VesselSimulationRunner Runner => runner;
		public CameraRig CameraRig => cameraRig;
		public WheelhouseUI WheelhouseUI => wheelhouseUI;
		public HarborSceneContext Context { get; private set; }
		public IHarborSceneServices Services => _services;
		public HarborViewState ViewState => _fatalProblem != null ? HarborViewState.Failed : _initialView?.State ?? HarborViewState.Waiting;
		public string Status { get; private set; } = "Preparing the harbor";
		public string Problem => _fatalProblem ?? (_initialView?.State == HarborViewState.Failed ? _initialView.Problem : null);
		public bool IsHeld => _services == null || _fatalProblem != null || _awaitingInitialView;
		/// <summary>The runner's own hold the status line explains (decision 0005, Coverage and day end).</summary>
		public RunHold ShownHold => _shownHold;

		public void Configure(TextAsset config, VesselSimulationRunner simulationRunner, CesiumVesselPlacement vesselPlacement,
			CesiumGeoreference reference, CameraRig cameras, WheelhouseUI ui, HelmInputController controls, SunController lighting,
			HarborTileset[] layers, Transform water, Transform navigation, Transform traffic)
		{
			configuration = config;
			runner = simulationRunner;
			placement = vesselPlacement;
			georeference = reference;
			cameraRig = cameras;
			wheelhouseUI = ui;
			helm = controls;
			sun = lighting;
			renderLayers = layers;
			waterRoot = water;
			navigationRoot = navigation;
			trafficRoot = traffic;
		}

		/// <summary>Test/integration substitution; call before a run. Real scenes use the serialized render adapters.</summary>
		public void SetViewLayers(IHarborViewLayer[] layers)
		{
			if (runner != null && runner.IsRunActive) throw new InvalidOperationException("Cannot replace view layers during a run.");
			_viewLayers = layers;
			_initialView = null;
		}

		private void Awake()
		{
			if (runner != null)
			{
				runner.Pause();
				if (!runner.IsInitialized) runner.enabled = false;
				SubscribeRunner();
			}
		}

		private void Start()
		{
			try
			{
				EnsureContext();
				if (_services != null) return;
				if (serviceFactory == null)
				{
					Fail("Harbor services are not connected. Required runtime pack: " + Context.Configuration.runtimePackFolder +
						"; contacts: " + Context.Configuration.contactsFile + "; environment: " + Context.Configuration.environmentFolder + ".");
					return;
				}
				Connect(serviceFactory.Create(Context), out _);
			}
			catch (Exception error) { Fail(error.Message); }
		}

		/// <summary>Installs one verified service bundle before a run; builders execute once and never during Update.</summary>
		public bool Connect(IHarborSceneServices services, out string problem)
		{
			try
			{
				EnsureContext();
				if (_services != null) throw new InvalidOperationException("Harbor services are already connected.");
				if (services == null) throw new InvalidOperationException("Missing harbor scene services: " + Context.Configuration.runtimePackFolder);
				if (services.RuntimePack == null) throw new InvalidOperationException("Missing verified runtime pack: " + Context.Configuration.runtimePackFolder);
				if (services.WaterSurface == null) throw new InvalidOperationException("Missing harbor IWaterSurface.");
				if (services.Landings == null) throw new InvalidOperationException("Missing harbor landing geometry: " + Context.Configuration.contactsFile);
				if (services.Catalog == null || services.Launcher == null) throw new InvalidOperationException("Missing harbor IScenarioCatalog or IScenarioLauncher.");
				if (services.BuildNavigation == null || services.BuildTraffic == null) throw new InvalidOperationException("Missing harbor navigation or traffic builder.");
				services.BuildNavigation(Context);
				services.BuildTraffic(Context);
				_services = services;
				placement.WaterSurface = services.WaterSurface;
				Shader.SetGlobalTexture("_FerrySimWaterNormals", WaterNormalMap.Get());
				runner.WorldGeometry = services.Landings.Geometry;
				sun.Runner = runner;
				wheelhouseUI.Source = runner;
				wheelhouseUI.DaylightSource = sun;
				wheelhouseUI.ConnectScenario(runner, services.Catalog, this);
				_fatalProblem = null;
				Status = "Choose a harbor scenario to begin";
				_openPicker = true;
				problem = null;
				return true;
			}
			catch (Exception error)
			{
				problem = error.Message;
				Fail(problem);
				return false;
			}
		}

		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			if (_services == null)
			{
				problem = Problem ?? "Harbor runtime services are not connected.";
				return false;
			}
			if (!_services.Launcher.TryStart(settings, out problem)) return false;
			problem = Problem;
			return problem == null;
		}

		private void EnsureContext()
		{
			if (Context != null) return;
			if (runner == null || placement == null || georeference == null || cameraRig == null || wheelhouseUI == null || sun == null ||
				waterRoot == null || navigationRoot == null || trafficRoot == null || configuration == null)
				throw new InvalidOperationException("The harbor scene is missing its runner, view, roots, or configuration. Rebuild Harbor.unity.");
			var settings = HarborSceneConfiguration.Parse(configuration.text);
			SubscribeRunner();
			if (_viewLayers == null)
			{
				if (renderLayers == null || renderLayers.Length == 0) throw new InvalidOperationException("Missing harbor terrain and skyline layers.");
				_viewLayers = new IHarborViewLayer[renderLayers.Length];
				for (var i = 0; i < renderLayers.Length; i++)
				{
					if (renderLayers[i] == null) throw new InvalidOperationException("Missing harbor render layer.");
					renderLayers[i].Open();
					_viewLayers[i] = renderLayers[i];
				}
			}
			Context = new HarborSceneContext(runner, georeference, cameraRig, sun, waterRoot, navigationRoot, trafficRoot, settings);
		}

		private void SubscribeRunner()
		{
			if (_subscribed || runner == null) return;
			runner.RunStarted += OnRunStarted;
			_subscribed = true;
		}

		private void OnRunStarted()
		{
			if (_services == null)
			{
				runner.EndRun();
				Fail("Harbor services must be connected before a run can start.");
				return;
			}
			if (!runner.IsReplaying && runner.Parameters?.Contact != null)
			{
				var pose = runner.State;
				var clearance = FaceClearance.Check(runner.Parameters, pose.North, pose.East, pose.Heading, _services.Landings.Geometry.Segments);
				if (!clearance.IsClear())
				{
					runner.EndRun();
					Fail("The harbor start is too close to landing face " + clearance.NearestSegmentId + ".");
					return;
				}
			}
			_fatalProblem = null;
			runner.Pause();
			// OnDisable ends a run. Keep it enabled while held, and pause before its -100 Update instead.
			runner.enabled = true;
			_awaitingInitialView = true;
			_pickerPausedRun = false;
			_openPicker = false;
			var mount = cameraRig.WheelhouseMount;
			if (mount != null)
			{
				var eye = GreyboxDimensions.From(runner.Parameters);
				mount.transform.localPosition = new Vector3(0, (float)eye.BridgeEyeHeight, (float)eye.BridgeEyeForward);
				mount.ApproachFaces = BowApproachFaces.From(_services.Landings);
			}
			cameraRig.ChaseTarget = runner.transform;
			sun.Follow(runner);
			var settings = Context.Configuration;
			_initialView = new HarborInitialView(_viewLayers, settings.initialViewStableFrames, settings.initialViewTimeoutSeconds);
			_initialView.Begin(Time.realtimeSinceStartupAsDouble);
			Status = "Loading the initial harbor view — vessel held";
			HoldHelm();
		}

		private void Update() => RefreshControls();

		public void RefreshControls()
		{
			SubscribeMapper(helm != null && helm.isActiveAndEnabled ? helm.Mapper : null);
			FollowView(wheelhouseUI != null ? wheelhouseUI.View : null);
			if (_openPicker && _view != null) { _openPicker = false; _view.PickerVisible = true; }
			if (IsHeld)
			{
				runner?.Pause();
				HoldHelm();
			}
		}

		private void LateUpdate() => RefreshView(Time.realtimeSinceStartupAsDouble);

		public void RefreshView(double now)
		{
			if (_awaitingInitialView && _initialView != null && _fatalProblem == null)
			{
				_initialView.Poll(now);
				if (_initialView.State == HarborViewState.Failed) Fail(_initialView.Problem);
				else if (_initialView.State == HarborViewState.Ready)
				{
					_awaitingInitialView = false;
					Status = null;
					if (_view == null || !_view.PickerVisible) { runner.Resume(); ReleaseHelm(); }
					else _pickerPausedRun = true;
				}
			}
			RefreshHold();
			if (_view != null && cameraRig != null && _shownMode != cameraRig.CurrentMode)
			{
				_shownMode = cameraRig.CurrentMode;
				_view.PanelVisible = _shownMode == CameraRig.Mode.Wheelhouse;
				_view.HudVisible = _shownMode != CameraRig.Mode.Wheelhouse;
			}
			RefreshStatus();
		}

		/// <summary>The runner stops stepping on its own hold; the status line says why, and a new start resumes.</summary>
		private void RefreshHold()
		{
			var hold = runner != null ? runner.Hold : RunHold.None;
			if (hold == _shownHold) return;
			if (_shownHold != RunHold.None && Status == HoldStatus(_shownHold)) Status = null;
			_shownHold = hold;
			if (hold != RunHold.None && _fatalProblem == null && !_awaitingInitialView) Status = HoldStatus(hold);
		}

		/// <summary>What the status line says for a hold; null for none.</summary>
		public static string HoldStatus(RunHold hold)
		{
			switch (hold)
			{
				case RunHold.OutsideCoveredWater: return "Run held — the vessel left the water this day has depth for.\nStart a new run to continue.";
				case RunHold.BeyondDayData: return "Run held — the day's tide, current and wind data has run out.\nStart a new run to continue.";
				default: return null;
			}
		}

		private void Fail(string reason)
		{
			_fatalProblem = reason;
			Status = "Harbor unavailable — vessel held\n" + reason;
			runner?.Pause();
			HoldHelm();
		}

		private void RefreshStatus()
		{
			var root = wheelhouseUI != null && wheelhouseUI.Document != null ? wheelhouseUI.Document.rootVisualElement : null;
			if (root == null) return;
			if (!ReferenceEquals(root, _statusRoot))
			{
				_statusLabel?.RemoveFromHierarchy();
				_statusRoot = root;
				_statusLabel = new Label { name = "harbor-status", pickingMode = PickingMode.Ignore };
				_statusLabel.style.position = Position.Absolute;
				_statusLabel.style.left = 18;
				_statusLabel.style.right = 18;
				_statusLabel.style.top = 18;
				_statusLabel.style.paddingTop = 12;
				_statusLabel.style.paddingBottom = 12;
				_statusLabel.style.paddingLeft = 16;
				_statusLabel.style.paddingRight = 16;
				_statusLabel.style.whiteSpace = WhiteSpace.Normal;
				_statusLabel.style.fontSize = 18;
				_statusLabel.style.color = Color.white;
				_statusLabel.style.backgroundColor = new Color(0.025f, 0.055f, 0.08f, 0.94f);
				root.Add(_statusLabel);
				_drawnStatus = null;
			}
			if (_drawnStatus == Status && _statusLabel.text == (Status ?? string.Empty)) return;
			_drawnStatus = Status;
			_statusLabel.text = Status ?? string.Empty;
			_statusLabel.style.display = string.IsNullOrEmpty(Status) ? DisplayStyle.None : DisplayStyle.Flex;
		}

		private void SubscribeMapper(HelmInputMapper mapper)
		{
			if (ReferenceEquals(mapper, _mapper)) return;
			if (_mapper != null) { _mapper.CameraCycleRequested -= CycleCamera; _mapper.NewRunRequested -= TogglePicker; }
			_mapper = mapper;
			_heldHelm = false;
			if (_mapper == null) return;
			_mapper.CameraCycleRequested += CycleCamera;
			_mapper.NewRunRequested += TogglePicker;
			if (IsHeld || (_view != null && _view.PickerVisible)) HoldHelm();
		}

		private void FollowView(WheelhouseView view)
		{
			if (ReferenceEquals(view, _view)) return;
			if (_view != null) _view.PickerVisibilityChanged -= PickerChanged;
			_view = view;
			_shownMode = (CameraRig.Mode)(-1);
			if (_view == null) return;
			_view.PickerVisibilityChanged += PickerChanged;
			if (_view.PickerVisible) PickerChanged(true);
		}

		private void PickerChanged(bool visible)
		{
			if (visible)
			{
				_pickerPausedRun = runner != null && !runner.IsPaused;
				runner?.Pause();
				HoldHelm();
			}
			else
			{
				if (!IsHeld)
				{
					if (_pickerPausedRun) runner?.Resume();
					ReleaseHelm();
				}
				_pickerPausedRun = false;
			}
		}

		private void TogglePicker() => _view?.TogglePicker();
		private void CycleCamera() => cameraRig.CurrentMode = cameraRig.CurrentMode == CameraRig.Mode.Wheelhouse ? CameraRig.Mode.Chase : CameraRig.Mode.Wheelhouse;
		private void HoldHelm() { if (_mapper != null && !_heldHelm) { _mapper.Disable(); _heldHelm = true; } }
		private void ReleaseHelm() { if (_mapper != null && _heldHelm) { _mapper.Enable(); _heldHelm = false; } }

		private void OnDestroy()
		{
			if (_subscribed && runner != null) runner.RunStarted -= OnRunStarted;
			SubscribeMapper(null);
			if (_view != null) _view.PickerVisibilityChanged -= PickerChanged;
			_statusLabel?.RemoveFromHierarchy();
		}
	}
}
