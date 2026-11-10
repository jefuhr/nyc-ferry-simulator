using System;
using System.IO;
using FerrySim.Audio.Clips;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Object = UnityEngine.Object;

namespace FerrySim.Audio.Tests
{
	public class AlarmAudioTests
	{
		private GameObject _host;
		private AlarmSounder _sounder;
		private FakeSource _source;
		private float _listenerVolume;
		[SetUp] public void SetUp()
		{
			_listenerVolume = AudioListener.volume; AudioListener.volume = 0;
			_host = new GameObject("silent alarm fixture");
			_sounder = new AlarmSounder(_host.transform);
			_source = new FakeSource();
		}
		[TearDown] public void TearDown() { Object.DestroyImmediate(_host); AudioListener.volume = _listenerVolume; }
		private void Refresh(bool active = true, bool paused = false, bool wheelhouse = true, float gain = 1) =>
			_sounder.Refresh(_source, active, paused, wheelhouse, gain);

		[Test] public void RaisedAlarmsSelectSteeringFirstAndStopOnAcknowledgeSilenceClearPauseAndRunEnd()
		{
			_source.Readout.General = true; Refresh();
			Assert.That(_sounder.Sounding, Is.True);
			Assert.That(_sounder.Source.clip, Is.SameAs(AudioClipProvider.Shared.Get("alarm-general").Clip));
			_source.Readout.Steering = true; Refresh();
			Assert.That(_sounder.Source.clip, Is.SameAs(AudioClipProvider.Shared.Get("alarm-steering").Clip));
			_source.Readout.General = false;
			_source.Readout.SteeringPhase = AlarmPhase.Acknowledged; Refresh();
			Assert.That(_sounder.Sounding, Is.False, "acknowledged");
			_source.Readout.SteeringPhase = AlarmPhase.Unacknowledged;
			_source.Readout.Steering = false; Refresh(); Assert.That(_sounder.Sounding, Is.False, "silenced");
			_source.Readout.Steering = true; Refresh(); Assert.That(_sounder.Sounding, Is.True);
			_source.Readout.SteeringPhase = AlarmPhase.Normal; Refresh(); Assert.That(_sounder.Sounding, Is.False, "condition cleared");
			_source.Readout.SteeringPhase = AlarmPhase.Unacknowledged;
			Refresh(paused: true); Assert.That(_sounder.Sounding, Is.False, "paused");
			Refresh(); Assert.That(_sounder.Sounding, Is.True, "resume reads current sounder");
			Refresh(active: false); Assert.That(_sounder.Sounding, Is.False, "run ended");
		}

		[Test] public void RingBackAndUnavailableInstrumentsAreSilent()
		{
			_source.Readout.Steering = true; _source.Readout.SteeringPhase = AlarmPhase.RingBack; Refresh();
			Assert.That(_sounder.Sounding, Is.False);
			_source.Readout.SteeringPhase = AlarmPhase.Unacknowledged;
			_source.HasInstruments = false; Refresh(); Assert.That(_sounder.Sounding, Is.False);
			_sounder.Refresh(new TelemetryOnly(), true, false, true, 1); Assert.That(_sounder.Sounding, Is.False);
		}

		[Test] public void EventCursorPollsIndependentlyAndResynchronisesWithoutReplayingExpiredEvents()
		{
			_source.Readout.Next = 3; _source.Readout.Steering = true; Refresh();
			Assert.That(_sounder.EventCursor, Is.EqualTo(3)); Assert.That(_source.Readout.EventReads, Is.Zero);
			_source.Readout.Next = 5; Refresh();
			Assert.That(_source.Readout.EventReads, Is.EqualTo(2)); Assert.That(_sounder.EventCursor, Is.EqualTo(5));
			_source.Readout.First = 10; _source.Readout.Next = 12;
			_source.Readout.SteeringPhase = AlarmPhase.RingBack; Refresh();
			Assert.That(_source.Readout.EventReads, Is.EqualTo(2), "expired events are never replayed");
			Assert.That(_sounder.EventCursor, Is.EqualTo(12)); Assert.That(_sounder.Sounding, Is.False);
			_source.RunId++; _source.Readout.First = _source.Readout.Next = 0;
			_source.Readout.SteeringPhase = AlarmPhase.Unacknowledged; Refresh();
			Assert.That(_sounder.EventCursor, Is.Zero); Assert.That(_sounder.Sounding, Is.True);
		}

		[Test] public void AlarmGainIsIndependentUnderMasterMuteAndWheelhouseHasNoLowPass()
		{
			var bus = new AudioBus { Master = .5f, Alarms = .4f };
			Assert.That(bus.Gain(AudioGroup.Alarms), Is.EqualTo(.2f).Within(1e-6));
			Assert.That(bus.Gain(AudioGroup.Engines), Is.EqualTo(.5f));
			Assert.That(bus.Gain(AudioGroup.Ambience), Is.EqualTo(.5f));
			Assert.That(bus.Gain(AudioGroup.HornAndSignals), Is.EqualTo(.5f));
			_source.Readout.Steering = true; Refresh(gain: bus.Gain(AudioGroup.Alarms));
			var inside = _sounder.Source.volume;
			Assert.That(inside, Is.EqualTo(.2f * AudioClipProvider.Shared.Get("alarm-steering").Slot.baseGain).Within(1e-6));
			Refresh(wheelhouse: false, gain: bus.Gain(AudioGroup.Alarms));
			Assert.That(_sounder.Source.volume, Is.EqualTo(inside * AlarmSounder.OutsideGain).Within(1e-6));
			Assert.That(_sounder.Source.GetComponent<AudioLowPassFilter>(), Is.Null);
			bus.Muted = true; Refresh(gain: bus.Gain(AudioGroup.Alarms)); Assert.That(_sounder.Source.volume, Is.Zero);
			bus.Muted = false; bus.Master = 0; Assert.That(bus.Gain(AudioGroup.Alarms), Is.Zero);
			Assert.That(AudioVoiceBudget.Total, Is.EqualTo(28)); Assert.That(AudioVoiceBudget.Total, Is.LessThanOrEqualTo(32));
		}

		[Test] public void RigPrivateActionsFollowRebindingAndKeyMutePublishesOnlyKeyChanges()
		{
			// The batch editor normally routes injected input to the editor state, not player actions.
			var savedSettings = InputSystem.settings;
			var savedFlags = savedSettings.hideFlags;
			if (savedFlags == HideFlags.HideAndDontSave) savedSettings.hideFlags = HideFlags.DontSave;
			var settings = Object.Instantiate(savedSettings);
			settings.hideFlags = HideFlags.HideAndDontSave;
			settings.SetInternalFeatureFlag("RUN_PLAYER_UPDATES_IN_EDIT_MODE", true);
			settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
			settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
			InputSystem.settings = settings;
			var defaults = InputActionAsset.FromJson(File.ReadAllText("Assets/FerrySim/Input/FerrySimControls.inputactions"));
			var service = new InputBindingService(defaults); InputBindingService.SetSession(service);
			var keyboard = InputSystem.AddDevice<Keyboard>();
			var vessel = new GameObject("rebind fixture");
			try
			{
				var runner = vessel.AddComponent<VesselSimulationRunner>();
				var helm = vessel.AddComponent<HelmInputController>(); helm.Actions = defaults;
				// Call the production entry point without adding a Traffic assembly reference to the EditMode tests.
				var attach = typeof(FerryAudioRig).GetMethod("Attach");
				var rig = (FerryAudioRig)attach.Invoke(null, new object[] { runner, helm, null, null, null, null });
				var changes = 0; var last = false; rig.MuteChangedFromKey += value => { changes++; last = value; };
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.V)); InputSystem.Update();
				Assert.That(keyboard.vKey.isPressed, Is.True, "injected V was processed");
				Assert.That(rig.Bus.Muted, Is.True, "default key reaches the rig before rebinding");
				InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
				rig.Bus.Muted = false; Assert.That(changes, Is.EqualTo(1));
				Assert.That(service.TryApply(ActionIds.Mute, 0, "Keyboard", "<Keyboard>/f12", out var conflict), Is.True, conflict);
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.V)); InputSystem.Update();
				Assert.That(rig.Bus.Muted, Is.False, "old key no longer reaches the private copy");
				InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update();
				InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F12)); InputSystem.Update();
				Assert.That(keyboard.f12Key.isPressed, Is.True, "injected F12 was processed");
				Assert.That(rig.Bus.Muted, Is.True); Assert.That(changes, Is.EqualTo(2)); Assert.That(last, Is.True);
				rig.Bus.Muted = false; Assert.That(changes, Is.EqualTo(2), "restoring saved settings does not emit a key event");
				Object.DestroyImmediate(rig.gameObject);
				Assert.DoesNotThrow(() => service.ResetAll(), "destroyed copy is unregistered");
			}
			finally
			{
				Object.DestroyImmediate(vessel); InputSystem.RemoveDevice(keyboard);
				InputBindingService.ResetSession(); Object.DestroyImmediate(defaults);
				InputSystem.settings = savedSettings; savedSettings.hideFlags = savedFlags;
				if (settings != null) Object.DestroyImmediate(settings);
			}
		}

		private class TelemetryOnly : IVesselTelemetrySource
		{
			public VesselParameters Parameters => null;
			public VesselState State => default;
			public VesselTelemetry Telemetry => default;
			public ControlInputs Controls => default;
			public DateTimeOffset ScenarioStartUtc => default;
			public bool HasTelemetry => true;
			public long RunId { get; set; } = 1;
			public ContactLatch Contact => default;
		}
		private sealed class FakeSource : TelemetryOnly, IInstrumentSource
		{
			public readonly FakeReadout Readout = new FakeReadout();
			public bool HasInstruments { get; set; } = true;
			public IInstrumentReadout Instruments => Readout;
			public void AcknowledgeAlarms() { }
			public void SilenceAlarms() { }
			public void SetLampTest(bool on) { }
		}
		private sealed class FakeReadout : IInstrumentReadout
		{
			public bool Steering, General;
			public AlarmPhase SteeringPhase = AlarmPhase.Unacknowledged;
			public long First, Next;
			public int EventReads;
			public double Value(Channel channel) => double.NaN;
			public int AlarmCount => 2;
			public string AlarmId(int alarm) => alarm == 0 ? "steering.failure" : "engine.port.failure";
			public AlarmGroup Group(int alarm) => alarm == 0 ? AlarmGroup.Steering : AlarmGroup.Engine;
			public AlarmPhase Phase(int alarm) => alarm == 0 ? SteeringPhase : General ? AlarmPhase.Unacknowledged : AlarmPhase.Normal;
			public bool SounderOn(AlarmGroup group) => group == AlarmGroup.Steering ? Steering : General;
			public bool LampTest => false;
			public long FirstEventSequence => First;
			public long NextEventSequence => Next;
			public AlarmEvent EventAt(long sequence) { Assert.That(sequence, Is.InRange(First, Next - 1)); EventReads++; return default; }
		}
	}
}
