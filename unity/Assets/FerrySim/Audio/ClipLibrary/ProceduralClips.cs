using System;
using UnityEngine;

namespace FerrySim.Audio.Clips
{
	/// <summary>Small mono deterministic placeholders, generated once. No captured or third-party audio.</summary>
	public static class ProceduralClips
	{
		public const int SampleRate = 22050;
		public static AudioClip Create(string id, bool loop)
		{
			var seconds = id == "horn" ? 5 : loop ? 2 : id == "barge-creak" ? 1.5 : .6;
			var count = (int)(seconds * SampleRate);
			var samples = new float[count];
			for (var i = 0; i < count; i++)
			{
				var t = i / (double)SampleRate;
				var value = Sample(id, t);
				if (!loop) value *= Math.Min(1, Math.Min(t / .02, (seconds - t) / .08));
				samples[i] = (float)(.45 * value);
			}
			var clip = AudioClip.Create("procedural " + id, samples.Length, 1, SampleRate, false);
			clip.hideFlags = HideFlags.HideAndDontSave;
			clip.SetData(samples, 0); return clip;
		}

		private static double Tone(double hertz, double time) => Math.Sin(2 * Math.PI * hertz * time);
		private static double Sample(string id, double t)
		{
			switch (id)
			{
				case "horn": return (Tone(350, t) + .5 * Tone(700, t) + Tone(1050, t) / 3 + Tone(1400, t) / 6) / 2;
				case "engine-idle": return (.7 * Tone(40, t) + .2 * Tone(80, t) + .1 * Tone(160, t)) * (.85 + .15 * Tone(8, t));
				case "engine-mid": return (.6 * Tone(80, t) + .25 * Tone(160, t) + .15 * Tone(240, t)) * (.85 + .15 * Tone(16, t));
				case "engine-high": return .6 * Tone(120, t) + .25 * Tone(240, t) + .15 * Tone(480, t);
				case "engine-low": return .7 * Tone(30, t) + .3 * Tone(60, t);
				case "propeller": return (.6 * Tone(50, t) + .25 * Tone(100, t)) * (.7 + .3 * Tone(10, t));
				case "water-wash": return Noise(t) * (.65 + .35 * Tone(.5, t));
				case "wind": return Noise(t) * (.75 + .25 * Tone(1, t));
				case "gull": return Tone(950 + 180 * Tone(3, t), t) * Math.Pow(Math.Sin(Math.PI * t / .6), 2);
				case "fender": return .7 * Tone(230 + 45 * Tone(6, t), t);
				case "barge-creak": return .65 * Tone(75 + 12 * Tone(2, t), t);
				case "clutch-engage": return (Tone(100, t) + .4 * Tone(270, t)) * Math.Exp(-12 * t);
				case "clutch-disengage": return (Tone(70, t) + .4 * Tone(190, t)) * Math.Exp(-10 * t);
				default: throw new ArgumentException("Unknown procedural audio slot " + id + ".");
			}
		}
		// Periodic band-limited noise substitute: whole half-Hz periods give seamless two-second loops.
		private static double Noise(double t) => (.25 * Tone(117.5, t) + .22 * Tone(281, t) + .2 * Tone(533.5, t) +
			.18 * Tone(1021, t) + .15 * Tone(1747.5, t));
	}
}
