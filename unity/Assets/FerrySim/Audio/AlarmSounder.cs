using FerrySim.Audio.Clips;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.Audio
{
	/// <summary>One render-only wheelhouse voice. The clip owns the beep rhythm; C1 owns phase and silence.</summary>
	public sealed class AlarmSounder
	{
		// Presentation estimate pending listening review, in addition to ordinary spatial attenuation.
		public const float OutsideGain = .25f;
		private readonly AudioVoice _voice;
		private readonly ResolvedClip _steering, _general;
		private IInstrumentReadout _readout;
		private long _runId;
		public AudioSource Source => _voice.Source;
		public bool Sounding { get; private set; }
		public long EventCursor { get; private set; }

		public AlarmSounder(Transform parent)
		{
			_steering = AudioClipProvider.Shared.Get("alarm-steering");
			_general = AudioClipProvider.Shared.Get("alarm-general");
			_voice = new AudioVoice(parent, "wheelhouse alarm sounder", "alarm-general");
			Source.priority = 0;
		}

		public void Refresh(IVesselTelemetrySource telemetry, bool active, bool paused, bool wheelhouse, float gain)
		{
			if (!active || paused || telemetry == null || !telemetry.HasTelemetry ||
				!(telemetry is IInstrumentSource instruments) || !instruments.HasInstruments)
			{ Stop(); return; }
			var readout = instruments.Instruments;
			if (readout == null) { Stop(); return; }
			if (_readout != readout || _runId != telemetry.RunId || EventCursor < readout.FirstEventSequence || EventCursor > readout.NextEventSequence)
			{
				// On composition/run change/overflow, current phase and sounder flags replace missed history.
				_readout = readout; _runId = telemetry.RunId; EventCursor = readout.NextEventSequence;
			}
			else
			{
				// Poll with our own cursor. Historical raises never restart a sound cleared before this frame.
				while (EventCursor < readout.NextEventSequence) { readout.EventAt(EventCursor); EventCursor++; }
			}
			var steering = false; var general = false;
			for (var i = 0; i < readout.AlarmCount; i++)
			{
				if (readout.Phase(i) != AlarmPhase.Unacknowledged) continue;
				var group = readout.Group(i);
				if (!readout.SounderOn(group)) continue;
				if (group == AlarmGroup.Steering) steering = true; else general = true;
			}
			if (!steering && !general) { Stop(); return; }
			Sounding = true;
			var clip = steering ? _steering : _general;
			if (Source.clip != clip.Clip) { _voice.Stop(); _voice.SetClip(clip); }
			_voice.Level(gain * (wheelhouse ? 1 : OutsideGain));
			_voice.Loop(true, false);
		}

		public void Stop()
		{
			if (!Sounding) return;
			_voice.Stop();
			Sounding = false;
		}
		public void Reset() { Stop(); _readout = null; EventCursor = 0; }
	}
}
