using System.Collections.Generic;
using System.IO;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Hosts <see cref="WheelhouseView"/> in a UIDocument and refreshes it every frame from an
	/// <see cref="IVesselTelemetrySource"/>: the serialized source if set, else <see cref="Source"/> assigned
	/// from code, else the first source found in the scene (the simulation bridge in preference to a fake).
	/// Other systems drive it through the public members (palette, units, HUD toggle, acknowledge); the
	/// development toolbar is only for the dev scene.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class WheelhouseUI : MonoBehaviour
	{
		[Tooltip("A component implementing IVesselTelemetrySource. Empty: found in the scene at startup.")]
		[SerializeField]
		private MonoBehaviour telemetrySource;

		[Tooltip("Optional. Empty: runtime panel settings scaled from a 1280x720 design.")]
		[SerializeField]
		private PanelSettings panelSettings;

		[SerializeField]
		private PaletteKind palette = PaletteKind.Day;

		[SerializeField]
		private DistanceUnit distanceUnit = DistanceUnit.Feet;

		[SerializeField]
		[Min(0f)]
		private float depthAlarmThresholdMetres = (float)DepthAlarm.DefaultThresholdMetres;

		[SerializeField]
		private bool showPanel = true;

		[SerializeField]
		private bool showHud;

		[SerializeField]
		private bool showDevToolbar;

		private PanelSettings _ownedSettings;
		private bool _sourceConfigured;
		private readonly List<string> _recordings = new List<string>();
		private int _recordingIndex;
		private string _statusRun;
		private double _statusThreshold = double.NaN;
		private DistanceUnit _statusUnit;
		private bool _statusAssumedStart;

		public IVesselTelemetrySource Source { get; set; }

		public WheelhouseView View { get; private set; }

		public UIDocument Document { get; private set; }

		/// <summary>
		/// Sets the source (null for none: the panel then shows NO DATA) and panel settings before the
		/// component is enabled, for tests and bootstrap code. A configured source is never replaced by one
		/// found in the scene.
		/// </summary>
		public void Configure(IVesselTelemetrySource source, PanelSettings settings, bool devToolbar = false)
		{
			Source = source;
			_sourceConfigured = true;
			panelSettings = settings;
			showDevToolbar = devToolbar;
		}

		private void OnEnable()
		{
			if (Source == null && !_sourceConfigured)
			{
				Source = telemetrySource as IVesselTelemetrySource ?? FindSource();
			}

			if (panelSettings == null && _ownedSettings == null)
			{
				_ownedSettings = WheelhousePanelSettings.Create();
			}

			Document = GetComponent<UIDocument>();
			if (Document == null)
			{
				Document = gameObject.AddComponent<UIDocument>();
			}

			Document.panelSettings = panelSettings != null ? panelSettings : _ownedSettings;
			BuildView();
		}

		private void OnDisable()
		{
			View?.RemoveFromHierarchy();
			View = null;
		}

		private void OnDestroy()
		{
			if (_ownedSettings != null)
			{
				Destroy(_ownedSettings);
				_ownedSettings = null;
			}
		}

		private void Update()
		{
			if (View == null)
			{
				BuildView();
			}

			// A scene source that has been destroyed would keep answering with its last values: treat it as none.
			var source = Source is Object component && component == null ? null : Source;
			View?.Refresh(source, Time.unscaledTimeAsDouble);
			if (View != null && View.ToolbarVisible)
			{
				UpdateToolbarStatus();
			}
		}

		private void BuildView()
		{
			var root = Document != null ? Document.rootVisualElement : null;
			if (root == null || View != null)
			{
				return;
			}

			View = new WheelhouseView
			{
				Palette = palette,
				DistanceUnit = distanceUnit,
				DepthAlarmThresholdMetres = depthAlarmThresholdMetres,
				PanelVisible = showPanel,
				HudVisible = showHud,
			};
			View.SettingsChanged += MirrorSettings;
			root.Add(View);
			View.StretchToParentSize();
			if (showDevToolbar)
			{
				BuildDevToolbar();
			}
		}

		private void MirrorSettings()
		{
			palette = View.Palette;
			distanceUnit = View.DistanceUnit;
			depthAlarmThresholdMetres = (float)View.DepthAlarmThresholdMetres;
		}

		private static IVesselTelemetrySource FindSource()
		{
			IVesselTelemetrySource fallback = null;
			foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.InstanceID))
			{
				if (behaviour is IVesselTelemetrySource source)
				{
					if (!(behaviour is FakeTelemetrySource))
					{
						return source;
					}

					fallback ??= source;
				}
			}

			return fallback;
		}

		private void BuildDevToolbar()
		{
			var toolbar = View.Toolbar;
			View.ToolbarVisible = true;
			toolbar.AddButton("Day / Night", View.TogglePalette, "toggle-palette");
			toolbar.AddButton("ft / m", View.ToggleDistanceUnit, "toggle-units");
			toolbar.AddButton("Alarm -", () => View.StepDepthAlarmThreshold(-1), "alarm-down");
			toolbar.AddButton("Alarm +", () => View.StepDepthAlarmThreshold(1), "alarm-up");
			toolbar.AddButton("Ack", View.AcknowledgeAlarms, "acknowledge");
			toolbar.AddButton("Panel", () => View.PanelVisible = !View.PanelVisible, "toggle-panel");
			toolbar.AddButton("HUD", () => View.HudVisible = !View.HudVisible, "toggle-hud");
			if (Source is FakeTelemetrySource fake)
			{
				toolbar.AddButton("Play / Pause", () => fake.IsPlaying = !fake.IsPlaying, "toggle-play");
				toolbar.AddButton("Next run", () => LoadNextRecording(fake), "next-run");
				toolbar.AddButton("Debrief", () => OpenDebrief(fake), "open-debrief");
				FindRecordings();
			}
		}

		private void FindRecordings()
		{
			_recordings.Clear();
			_recordings.Add(UIRepoPaths.SampleCrashStopCsv);
			var artifacts = UIRepoPaths.Resolve(UIRepoPaths.ManeuverArtifacts);
			if (Directory.Exists(artifacts))
			{
				var files = Directory.GetFiles(artifacts, "*.csv");
				System.Array.Sort(files, System.StringComparer.Ordinal);
				_recordings.AddRange(files);
			}
		}

		private void LoadNextRecording(FakeTelemetrySource fake)
		{
			if (_recordings.Count == 0)
			{
				return;
			}

			_recordingIndex = (_recordingIndex + 1) % _recordings.Count;
			fake.TryLoadCsv(_recordings[_recordingIndex]);
			fake.IsPlaying = true;
		}

		private void OpenDebrief(FakeTelemetrySource fake)
		{
			if (fake.Recording == null)
			{
				return;
			}

			fake.IsPlaying = false;
			View.Debrief.Show(fake.Recording, fake.Parameters);
			View.DebriefVisible = true;
		}

		/// <summary>The run, the alarm setting (marked while it is the unconfirmed default) and an assumed clock; rebuilt only when one changes.</summary>
		private void UpdateToolbarStatus()
		{
			var fake = Source as FakeTelemetrySource;
			var run = fake != null && fake.Recording != null ? fake.Recording.Label : "live";
			var assumedStart = fake != null && fake.Recording != null && fake.ScenarioStartIsFallback;
			var thresholdMetres = View.DepthAlarmThresholdMetres;
			var unit = View.DistanceUnit;
			if (ReferenceEquals(run, _statusRun) && thresholdMetres.Equals(_statusThreshold) && unit == _statusUnit && assumedStart == _statusAssumedStart)
			{
				return;
			}

			_statusRun = run;
			_statusThreshold = thresholdMetres;
			_statusUnit = unit;
			_statusAssumedStart = assumedStart;
			var threshold = InstrumentFormat.Distance(thresholdMetres, unit) + " " + DisplayUnits.Symbol(unit);
			var text = $"{run}  |  alarm below {threshold}";
			if (DepthAlarm.IsDefaultThreshold(thresholdMetres))
			{
				text += $" ({DepthAlarm.PlaceholderNote})";
			}

			if (assumedStart)
			{
				text += "  |  clock: assumed start (no scenario_start_utc in the CSV)";
			}

			PaletteRoles.SetText(View.Toolbar.Status, text);
		}
	}
}
