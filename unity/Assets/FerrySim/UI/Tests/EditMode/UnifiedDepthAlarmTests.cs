using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using static FerrySim.UI.Tests.EditMode.Displays.SkinJson;

namespace FerrySim.UI.Tests.EditMode
{
	// Failure modes: split acknowledgement, real-time oscillation while paused, different flash duty,
	// ignored saved thresholds, absent lamp test/ring-back, and changes to telemetry-only fallback.
	public class UnifiedDepthAlarmTests
	{
		private sealed class Source : IVesselTelemetrySource, IInstrumentSource
		{
			private readonly StubTelemetrySource _telemetry = StubTelemetrySource.Underway();
			public readonly InstrumentModel Model = new InstrumentModel(InstrumentParametersJson.Load(UIRepoPaths.Resolve("vessels/instruments/t-class.json")));
			public bool HasInstruments { get; set; } = true;
			public IInstrumentReadout Instruments => Model;
			public VesselParameters Parameters => _telemetry.Parameters;
			public VesselState State => _telemetry.State;
			public VesselTelemetry Telemetry => _telemetry.Telemetry;
			public ControlInputs Controls => _telemetry.Controls;
			public DateTimeOffset ScenarioStartUtc => _telemetry.ScenarioStartUtc;
			public bool HasTelemetry => _telemetry.HasTelemetry;
			public long RunId => _telemetry.RunId;
			public ContactLatch Contact => _telemetry.Contact;
			public void AcknowledgeAlarms() => Model.Acknowledge();
			public void SilenceAlarms() => Model.Silence();
			public void SetLampTest(bool on) => Model.SetLampTest(on);
			public void SetShallowWaterThreshold(double? metres) => Model.SetShallowWaterThreshold(metres);
			public void Observe(double time, double depth)
			{
				_telemetry.Set(time, depth);
				Model.Step(State, Telemetry, Controls, new Taps());
			}
		}

		private sealed class Taps : IInstrumentTaps
		{
			public double RudderOrder => 0;
			public bool NfuActive => false;
			public double HardOverAngle => .35;
			public double SteeringRate => .12;
			public ShiftPhase PortShiftPhase => ShiftPhase.Engaged;
			public ShiftPhase StarboardShiftPhase => ShiftPhase.Engaged;
			public double PortEngineLoad => 0;
			public double StarboardEngineLoad => 0;
		}

		private Source _source;
		private WheelhousePanel _classic;
		private DepthWidget _hud;
		private AlarmPanelView _console;

		[SetUp]
		public void SetUp()
		{
			_source = new Source();
			_classic = new WheelhousePanel();
			_hud = new DepthWidget(HudWidgetRegistry.Default.Find(HudWidgetIds.Depth));
			_console = new AlarmPanelView(DisplaySkin.Parse(Skin("{'type': 'list', 'rect': [0, 0, 640, 480], 'text': 'ALARMS'}", display: "alarm-panel", reference: "[640, 480]"), TClassAlarms));
			_console.Connect(_source);
		}

		private void Refresh(double realTime, double threshold = 1.524)
		{
			_classic.Refresh(_source, DistanceUnit.Metres, threshold, realTime);
			_hud.Refresh(new HudFrame(_source, _classic, DistanceUnit.Metres, threshold, realTime, null));
			_console.Refresh(_source.Instruments, _source.State.Time);
		}

		private void Agree(bool expected)
		{
			Assert.That(_console.List.Rows.Single(row => row.Text == "SHALLOW WATER").Lit, Is.EqualTo(expected), "console");
			Assert.That(_classic.Depth.AlarmLit, Is.EqualTo(expected), "classic");
			Assert.That(_hud.Depth.AlarmLit, Is.EqualTo(expected), "HUD");
			var phase = _source.Instruments.Phase(0);
			var state = phase == AlarmPhase.Normal ? AlarmState.Normal : phase == AlarmPhase.Acknowledged ? AlarmState.Acknowledged : AlarmState.Active;
			Assert.That(_classic.DepthAlarmShown.State, Is.EqualTo(state));
			Assert.That(_classic.DepthAlarmShown.IsRaised, Is.EqualTo(state != AlarmState.Normal));
			Assert.That(_classic.DepthAlarmShown.IsLit, Is.EqualTo(expected));
		}

		[Test]
		public void EnteringShallowWater_ReachesAllThreeInTheSameRefresh()
		{
			_source.Observe(0, 10); Refresh(10); Agree(false);
			_source.Observe(.1, 1); Refresh(10.1); Agree(true);
		}

		[TestCase(true)]
		[TestCase(false)]
		public void HudClickAndKeyPath_AcknowledgeTheSharedAlarm(bool click)
		{
			_source.Observe(0, 1); Refresh(10);
			if (click)
			{
				var host = new GameObject("Depth alarm click test");
				var settings = ScriptableObject.CreateInstance<PanelSettings>();
				try
				{
					var document = host.AddComponent<UIDocument>(); document.panelSettings = settings;
					document.rootVisualElement.Add(_hud.Element);
					Assert.That(_hud.Depth.panel, Is.Not.Null, "click events need an attached UI panel");
					using (var evt = ClickEvent.GetPooled()) { evt.target = _hud.Depth; _hud.Depth.SendEvent(evt); }
				}
				finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings); }
			}
			else _source.AcknowledgeAlarms();
			Assert.That(_source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Acknowledged));
			Assert.That(_source.Instruments.SounderOn(AlarmGroup.Navigation), Is.False);
			for (var sample = 0; sample < 20; sample++)
			{
				_source.Observe(.6 + sample * .05, 1); Refresh(10.8 + sample * .1); Agree(true);
			}
		}

		[UnityTest]
		public IEnumerator StoppedSimulationTime_FreezesAllThreeAcrossTwoRealSeconds()
		{
			_source.Observe(.6, 1); Refresh(10); Agree(false);
			var began = Time.realtimeSinceStartupAsDouble;
			while (Time.realtimeSinceStartupAsDouble - began < 2)
			{
				Refresh(10 + Time.realtimeSinceStartupAsDouble - began); Agree(false);
				yield return null;
			}
		}

		[TestCase(false)]
		[TestCase(true)]
		public void TwentyFlashSamplesAgree_IncludingRingBack(bool cleared)
		{
			_source.Observe(0, 1); Refresh(10);
			for (var sample = 0; sample < 20; sample++)
			{
				var time = .1 + sample * .1;
				_source.Observe(time, cleared ? 10 : 1); Refresh(10.17 + sample * .13);
				Agree(AlarmAnnunciator.LampLit(cleared ? AlarmPhase.RingBack : AlarmPhase.Unacknowledged, false, time));
			}
			if (cleared) { _classic.AcknowledgeAlarm(); Refresh(20); Agree(false); }
		}

		[Test]
		public void SavedThreeMetreThreshold_RaisesC1AtTwoMetres()
		{
			_source.Observe(0, 2); Refresh(10); Agree(false);
			Refresh(10, 3); // the viewer changes its saved display threshold
			_source.Observe(.1, 2); Refresh(10.1, 3); Agree(true);
			Assert.That(_source.Instruments.SounderOn(AlarmGroup.Navigation), Is.True);
		}

		[Test]
		public void LampTest_LightsTheDepthBlocks()
		{
			_source.Observe(.6, 10); _source.SetLampTest(true); Refresh(10); Agree(true);
		}

		[Test]
		public void TelemetryOnlyAndUnavailableInstruments_KeepLegacyAlarmAndClock()
		{
			_source.HasInstruments = false;
			_source.Observe(.6, 1); Refresh(10); // legacy starts lit even though C1 clock is dark
			Assert.That(_classic.Depth.AlarmLit, Is.True); Assert.That(_hud.Depth.AlarmLit, Is.True);
			Refresh(10.8);
			Assert.That(_classic.Depth.AlarmLit, Is.False); Assert.That(_hud.Depth.AlarmLit, Is.False);
			_classic.AcknowledgeAlarm(); Refresh(11);
			Assert.That(_classic.Depth.AlarmLit, Is.True); Assert.That(_hud.Depth.AlarmLit, Is.True);
			var only = StubTelemetrySource.Underway(.6, 1);
			_classic.Refresh(only, DistanceUnit.Metres, 1.524, 10);
			_hud.Refresh(new HudFrame(only, _classic, DistanceUnit.Metres, 1.524, 10, null));
			Assert.That(_classic.Depth.AlarmLit, Is.True); Assert.That(_hud.Depth.AlarmLit, Is.True);
		}
		[Test]
		public void StandaloneWidget_ForwardsItsThresholdWithoutAClassicPanel()
		{
			_source.Observe(0, 2);
			_hud.Refresh(new HudFrame(_source, null, DistanceUnit.Metres, 3, 10, null));
			_source.Observe(.1, 2);
			_hud.Refresh(new HudFrame(_source, null, DistanceUnit.Metres, 3, 10.1, null));
			Assert.That(_source.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			Assert.That(_hud.Depth.AlarmLit, Is.True);
		}

		[Test]
		public void InstrumentsBecomingUnavailable_ReevaluateTheLegacyAlarmAtTheSameSample()
		{
			_source.Observe(0, 1); Refresh(10);
			_source.HasInstruments = false; Refresh(10);
			Assert.That(_classic.Alarm.State, Is.EqualTo(AlarmState.Active));
			Assert.That(_classic.Depth.AlarmLit, Is.True);
			Assert.That(_hud.Depth.AlarmLit, Is.True);
		}

		private sealed class ProfileEnvironment : IEnvironment
		{
			private readonly double _draft;
			public ProfileEnvironment(double draft) { _draft = draft; }
			public HorizontalVector SampleCurrent(double n, double e, double t) => HorizontalVector.Zero;
			public HorizontalVector SampleWind(double n, double e, double t) => HorizontalVector.Zero;
			public double SampleDepth(double n, double e, double t) => _draft +
				(t >= .2 && t < .4 || t >= .6 && t < .8 || t >= 1.01 && t < 1.05 ? 2 : t >= .4 && t < .6 ? 3.1 : 4);
		}

		[Test]
		public void RecordedShallowWaterRun_MeasuresDebriefDifferenceWithoutChangingTheRecorder()
		{
			var folder = UIRepoPaths.Resolve("artifacts/alarm-unify"); Directory.CreateDirectory(folder);
			var vessel = VesselParametersJson.Load(UIRepoPaths.Resolve("vessels/t-class.json"));
			var environment = new ProfileEnvironment((vessel.Particulars.MaxDraft ?? vessel.Particulars.Draft).Value);
			var sim = new VesselDynamicsFactory().Create(vessel, environment, default);
			var model = new InstrumentModel(InstrumentParametersJson.Load(UIRepoPaths.Resolve("vessels/instruments/t-class.json")));
			model.SetShallowWaterThreshold(3);
			var header = new[] { new KeyValuePair<string, string>("vessel", "t-class"),
				new KeyValuePair<string, string>("source", "unity"),
				new KeyValuePair<string, string>("measurement", "Scripted depth fixture; no replay environment; threshold 3 m") };
			var full = new TelemetryRecordingBuilder("100 Hz observations", header);
			var history = new TelemetryRecordingBuilder("10 Hz with threshold history", header);
			var telemetry = new StringWriter(CultureInfo.InvariantCulture);
			var inputs = new StringWriter(CultureInfo.InvariantCulture);
			using (var recorder = new RunRecorder(telemetry, inputs, 10, header))
			{
				for (var step = 0; step < 200; step++)
				{
					recorder.BeforeStep(step, sim.State, sim.Telemetry, ControlInputs.Neutral);
					sim.Step(ControlInputs.Neutral);
					model.Step(sim.State, sim.Telemetry, ControlInputs.Neutral, (IInstrumentTaps)sim);
					recorder.AfterStep(step + 1, sim.State, sim.Telemetry);
					full.Add(sim.State, sim.Telemetry, ControlInputs.Neutral, 3);
					if ((step + 1) % 10 == 0) history.Add(sim.State, sim.Telemetry, ControlInputs.Neutral, 3);
				}
				recorder.Finish(200, sim.State, sim.Telemetry);
				Assert.That(recorder.Failure, Is.Null);
			}
			File.WriteAllText(Path.Combine(folder, "shallow-run.csv"), telemetry.ToString());
			File.WriteAllText(Path.Combine(folder, "shallow-run.inputs.csv"), inputs.ToString());
			File.WriteAllText(Path.Combine(folder, "shallow-run-100hz-history.csv"), TelemetryCsvWriter.Render(full.Build()));
			File.WriteAllText(Path.Combine(folder, "shallow-run-10hz-history.csv"), TelemetryCsvWriter.Render(history.Build()));
			var raw = TelemetryCsv.Parse(telemetry.ToString(), "shallow-run.csv");
			var summary = new StringBuilder("Clock: simulation seconds. Scripted depth fixture with real dynamics, unchanged RunRecorder, 3 m threshold.\n");
			var raised = 0;
			for (var cursor = model.FirstEventSequence; cursor < model.NextEventSequence; cursor++)
			{
				var evt = model.EventAt(cursor);
				if (evt.Alarm != 0) continue;
				if (evt.Kind == AlarmEventKind.Raised) raised++;
				summary.AppendFormat(CultureInfo.InvariantCulture, "C1 {0} at {1:F2} s\n", evt.Kind, evt.Step / 100.0);
			}
			foreach (var recording in new[] { full.Build(), history.Build(), raw })
			{
				var events = DebriefEvents.Detect(recording, 3).Where(e => e.Kind == DebriefEventKind.DepthAlarm).ToArray();
				summary.AppendFormat("Legacy {0}: {1} events\n", recording.Label, events.Length);
				foreach (var evt in events) summary.AppendFormat(CultureInfo.InvariantCulture, "  {0:F2} to {1:F2} s\n", evt.Time, evt.EndTime);
			}
			summary.AppendFormat("C1: {0} raises; ring-back remains visible after each clear until acknowledged.\n", raised);
			File.WriteAllText(Path.Combine(folder, "debrief-comparison.txt"), summary.ToString());
			TestContext.WriteLine(summary.ToString());
			Assert.That(raised, Is.EqualTo(3));
			Assert.That(DebriefEvents.Detect(full.Build(), 3).Count(e => e.Kind == DebriefEventKind.DepthAlarm), Is.EqualTo(2));
			Assert.That(DebriefEvents.Detect(raw, 3).Count(e => e.Kind == DebriefEventKind.DepthAlarm), Is.EqualTo(1));
		}

	}
}
