using FerrySim.Audio.Clips;
using UnityEngine;

namespace FerrySim.Audio
{
	/// <summary>Exactly one AudioSource/voice. One-shots replace this source's clip; PlayOneShot cannot evade the voice budget.</summary>
	internal sealed class AudioVoice
	{
		private ResolvedClip _clip;
		private bool _paused;
		public AudioSource Source { get; }
		public AudioLowPassFilter Filter { get; }
		public AudioVoice(Transform parent, string name, string slot, bool lowPass = false, float range = 300)
		{
			var host = new GameObject(name); host.transform.SetParent(parent, false);
			Source = host.AddComponent<AudioSource>(); Source.playOnAwake = false;
			Source.spatialBlend = 1; Source.dopplerLevel = 0;
			Source.minDistance = 8; Source.maxDistance = range; Source.rolloffMode = AudioRolloffMode.Linear;
			Source.priority = 100;
			if (lowPass) Filter = host.AddComponent<AudioLowPassFilter>();
			SetClip(AudioClipProvider.Shared.Get(slot));
		}
		public void SetClip(ResolvedClip clip) { _clip = clip; Source.clip = clip.Clip; Source.loop = clip.Slot.loop; }
		public void Level(double gain, double pitch = 1)
		{ Source.volume = Mathf.Clamp01((float)gain * _clip.Slot.baseGain); Source.pitch = (float)pitch; }
		public void Loop(bool active, bool paused)
		{
			if (!active) { Stop(); return; }
			Pause(paused);
			if (!paused && !Source.isPlaying) Source.Play();
		}
		public void Pause(bool paused)
		{
			if (paused == _paused) return;
			if (paused) Source.Pause(); else Source.UnPause();
			_paused = paused;
		}
		public void Play(ResolvedClip clip, double gain)
		{ SetClip(clip); _paused = false; Level(gain); Source.Play(); }
		public void Stop() { if (Source != null) Source.Stop(); _paused = false; }
	}

	internal sealed class EngineVoices
	{
		private readonly AudioVoice _idle, _mid, _high, _low, _prop, _clutch;
		private readonly ResolvedClip _engage = AudioClipProvider.Shared.Get("clutch-engage");
		private readonly ResolvedClip _disengage = AudioClipProvider.Shared.Get("clutch-disengage");
		public ClutchEvents Changes { get; } = new ClutchEvents();
		public Transform Mount { get; }
		public AudioSource Idle => _idle.Source;
		public EngineVoices(Transform parent, string side)
		{
			var host = new GameObject(side + " engine room"); host.transform.SetParent(parent, false); Mount = host.transform;
			_idle = new AudioVoice(Mount, "idle", "engine-idle", true);
			_mid = new AudioVoice(Mount, "mid", "engine-mid", true);
			_high = new AudioVoice(Mount, "high", "engine-high", true);
			_low = new AudioVoice(Mount, "load low end", "engine-low", true);
			_prop = new AudioVoice(Mount, "propeller", "propeller", true);
			_clutch = new AudioVoice(Mount, "clutch", "clutch-engage", true);
		}
		public void Refresh(in EngineMix mix, in ListeningPosition ear, float bus, bool active, bool paused)
		{
			var gain = bus * ear.EngineGain;
			_idle.Level(gain * mix.Gain * mix.IdleGain, mix.IdlePitch);
			_mid.Level(gain * mix.Gain * mix.MidGain, mix.MidPitch);
			_high.Level(gain * mix.Gain * mix.HighGain, mix.HighPitch);
			_low.Level(gain * mix.LowGain); _prop.Level(gain * mix.PropGain, mix.PropPitch);
			_idle.Filter.cutoffFrequency = _mid.Filter.cutoffFrequency = _high.Filter.cutoffFrequency =
				_low.Filter.cutoffFrequency = _prop.Filter.cutoffFrequency = _clutch.Filter.cutoffFrequency = (float)ear.EngineCutoffHertz;
			_idle.Loop(active, paused); _mid.Loop(active, paused); _high.Loop(active, paused); _low.Loop(active, paused); _prop.Loop(active, paused);
			_clutch.Level(gain); _clutch.Pause(paused);
			if (!active) _clutch.Stop();
			else if (!paused && !_clutch.Source.isPlaying && Changes.TryTake(out var change)) _clutch.Play(change == ClutchSound.Engage ? _engage : _disengage, gain);
		}
		public void Stop() { _idle.Stop(); _mid.Stop(); _high.Stop(); _low.Stop(); _prop.Stop(); _clutch.Stop(); }
	}
}
