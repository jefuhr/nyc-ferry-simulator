using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Sim
{
	/// <summary>
	/// A minimal UI Toolkit text readout of the running vessel for development: clock, position, motion,
	/// controls, engines and the bridge's own health (steps, dropped time, recording). The real instruments
	/// belong to the wheelhouse UI.
	/// </summary>
	[RequireComponent(typeof(UIDocument))]
	public sealed class SimDebugReadout : MonoBehaviour
	{
		[SerializeField]
		private VesselSimulationRunner runner;

		[Tooltip("Other components that add a readout line, such as the chase camera.")]
		[SerializeField]
		private MonoBehaviour[] extraSources = Array.Empty<MonoBehaviour>();

		[SerializeField]
		private float refreshInterval = 0.1f;

		private readonly List<IDebugReadoutSource> _sources = new List<IDebugReadoutSource>();
		private Label _text;
		private float _nextRefresh;
		private float _smoothedFrameTime = 1f / 60f;
		private static TimeZoneInfo _newYork;
		private static bool _newYorkLookedUp;

		/// <summary>The text currently shown.</summary>
		public string Text => _text?.text ?? string.Empty;

		public VesselSimulationRunner Runner
		{
			get => runner;
			set => runner = value;
		}

		private void OnEnable()
		{
			var document = GetComponent<UIDocument>();
			if (document.panelSettings == null)
			{
				document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
			}

			var root = document.rootVisualElement;
			root.Clear();
			var panel = new VisualElement { name = "ferrysim-debug-readout" };
			panel.style.position = Position.Absolute;
			panel.style.left = 12;
			panel.style.top = 12;
			panel.style.paddingLeft = 10;
			panel.style.paddingRight = 10;
			panel.style.paddingTop = 8;
			panel.style.paddingBottom = 8;
			panel.style.backgroundColor = new Color(0.04f, 0.06f, 0.09f, 0.72f);
			panel.style.borderTopLeftRadius = 6;
			panel.style.borderTopRightRadius = 6;
			panel.style.borderBottomLeftRadius = 6;
			panel.style.borderBottomRightRadius = 6;
			_text = new Label("FerrySim: waiting for the simulation") { name = "ferrysim-debug-text" };
			_text.style.color = new Color(0.92f, 0.95f, 0.98f);
			_text.style.fontSize = 13;
			_text.style.whiteSpace = WhiteSpace.Normal;
			panel.Add(_text);
			root.Add(panel);
		}

		private void Start()
		{
			if (runner == null)
			{
				runner = FindAnyObjectByType<VesselSimulationRunner>();
			}

			_sources.Clear();
			if (runner != null)
			{
				_sources.AddRange(runner.GetComponents<IDebugReadoutSource>());
			}

			foreach (var extra in extraSources)
			{
				if (extra is IDebugReadoutSource source && !_sources.Contains(source))
				{
					_sources.Add(source);
				}
			}
		}

		private void Update()
		{
			_smoothedFrameTime += (Time.unscaledDeltaTime - _smoothedFrameTime) * 0.1f;
			if (_text == null || Time.unscaledTime < _nextRefresh)
			{
				return;
			}

			_nextRefresh = Time.unscaledTime + refreshInterval;
			Refresh();
		}

		/// <summary>Rebuilds the text now.</summary>
		public void Refresh()
		{
			if (_text != null)
			{
				_text.text = runner != null && runner.IsInitialized ? Compose() : "FerrySim: waiting for the simulation";
			}
		}

		private string Compose()
		{
			var state = runner.State;
			var telemetry = runner.Telemetry;
			var controls = runner.Controls;
			var driver = runner.Driver;
			var invariant = CultureInfo.InvariantCulture;
			var builder = new StringBuilder(512);

			builder.Append(runner.Parameters.DisplayName ?? runner.Parameters.Id);
			builder.Append(runner.IsReplaying ? "   REPLAY" : string.Empty);
			builder.Append(!runner.IsRunActive ? "   ENDED" : runner.IsPaused ? "   PAUSED" : "   RUNNING");
			if (runner.Recorder != null)
			{
				builder.Append("   REC ").Append(System.IO.Path.GetFileName(runner.Recorder.TelemetryPath));
			}

			builder.Append('\n');
			var clock = runner.ScenarioStartUtc.AddSeconds(state.Time);
			builder.Append("sim ").Append(TimeSpan.FromSeconds(state.Time).ToString(@"hh\:mm\:ss\.ff", invariant));
			builder.Append("   ").Append(LocalClock(clock)).Append('\n');

			var position = LocalTangentPlane.World.ToGeodetic(state.North, state.East);
			builder.AppendFormat(invariant, "pos N {0:F1} m  E {1:F1} m   {2:F5}  {3:F5}\n",
				state.North, state.East, position.LatitudeDegrees, position.LongitudeDegrees);
			builder.AppendFormat(invariant, "hdg {0:000.0}°  cog {1:000.0}°  sog {2:F1} kn  stw {3:F1} kn  rot {4:+0.0;-0.0}°/s\n",
				Angles.ToDegrees(state.Heading), Angles.ToDegrees(Angles.WrapTwoPi(telemetry.CourseOverGround)),
				Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), Units.MetresPerSecondToKnots(telemetry.SpeedThroughWater),
				Angles.ToDegrees(state.YawRate));
			builder.AppendFormat(invariant, "levers port {0:+0.00;-0.00}  stbd {1:+0.00;-0.00}   helm {2:+0.00;-0.00}   rudder {3:+0.0;-0.0}°\n",
				controls.PortLever, controls.StarboardLever, controls.Helm, Angles.ToDegrees(telemetry.PortRudderAngle));
			builder.AppendFormat(invariant, "port {0:F0} rpm {1}   stbd {2:F0} rpm {3}\n",
				Units.RadiansPerSecondToRpm(telemetry.Port.EngineSpeed), TelemetryCsv.GearName(telemetry.Port.Gear),
				Units.RadiansPerSecondToRpm(telemetry.Starboard.EngineSpeed), TelemetryCsv.GearName(telemetry.Starboard.Gear));
			builder.AppendFormat(invariant, "under keel {0:F1} m   step {1}   dropped {2:F2} s in {3} frames   {4:F0} fps",
				telemetry.DepthUnderKeel, driver.StepCount, driver.TotalDroppedTime, driver.DroppedFrameCount,
				1f / Mathf.Max(_smoothedFrameTime, 1e-4f));
			var replay = runner.ReplayResult;
			if (!string.IsNullOrEmpty(runner.RecordingError))
			{
				builder.Append("\nRECORDING INCOMPLETE: ").Append(runner.RecordingError);
			}
			if (replay != null)
			{
				builder.Append('\n');
				if (runner.ReplayCompleted)
				{
					builder.Append(replay.Matches ? replay.ToString() : "REPLAY DIVERGED: " + replay);
				}
				else
				{
					builder.Append(replay.FirstMismatchRow >= 0 ? "REPLAY DIVERGED: " + replay
						: runner.IsRunActive ? $"replay matches {replay.RowsCompared} rows so far"
						: $"replay stopped after {replay.RowsCompared} compared rows; verification incomplete");
				}
			}

			foreach (var source in _sources)
			{
				var line = source.ReadoutLine;
				if (!string.IsNullOrEmpty(line))
				{
					builder.Append('\n').Append(line);
				}
			}

			return builder.ToString();
		}

		/// <summary>America/New_York wall-clock time, which is what the UI shows; UTC if the zone is unavailable.</summary>
		private static string LocalClock(DateTimeOffset utc)
		{
			if (!_newYorkLookedUp)
			{
				_newYorkLookedUp = true;
				try
				{
					_newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
				}
				catch (Exception)
				{
					_newYork = null;
				}
			}

			if (_newYork == null)
			{
				return utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
			}

			var local = TimeZoneInfo.ConvertTime(utc, _newYork);
			var zone = _newYork.IsDaylightSavingTime(local) ? "EDT" : "EST";
			return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + zone;
		}
	}
}
