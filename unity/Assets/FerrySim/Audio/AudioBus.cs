using System;
using System.Globalization;

namespace FerrySim.Audio
{
	public enum AudioGroup { Engines, HornAndSignals, Ambience }

	/// <summary>Session settings only. Zero gains never disable the rig or bypass its work.</summary>
	public sealed class AudioBus
	{
		public float Master { get; set; } = 1;
		public float Engines { get; set; } = 1;
		public float HornAndSignals { get; set; } = 1;
		public float Ambience { get; set; } = 1;
		public bool Muted { get; set; }
		public float Gain(AudioGroup group) => Muted ? 0 : (float)(EngineSoundMapping.Clamp(Master) *
			EngineSoundMapping.Clamp(group == AudioGroup.Engines ? Engines : group == AudioGroup.HornAndSignals ? HornAndSignals : Ambience));
	}

	public static class AudioLaunchOptions
	{
		public const string VolumeArgument = "-ferrysim-audio-volume";
		public static float MasterVolume(string[] arguments)
		{
			var volume = 1f;
			// Existing repository test/release entry points cannot pass new arguments without coordinator-owned script edits.
			// Their opt-in automation modes stay silent by default; an explicit volume argument still takes precedence.
			for (var i = 0; i < arguments.Length; i++)
				if (arguments[i] == "-runTests" || arguments[i] == "-ferrysim-smoke") volume = 0;
			for (var i = 0; i < arguments.Length; i++)
			{
				if (arguments[i] != VolumeArgument) continue;
				if (++i >= arguments.Length || !float.TryParse(arguments[i], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
					CultureInfo.InvariantCulture, out volume) || !(volume >= 0 && volume <= 1))
					throw new ArgumentException(VolumeArgument + " needs a finite level from 0 to 1, using a decimal point.");
			}
			return volume;
		}
	}

	public static class AudioVoiceBudget
	{
		public const int Limit = 32;
		public const int Engines = 12;
		public const int OwnHornAndFog = 2;
		public const int Ambience = 5;
		public const int Traffic = 8;
		public const int Total = Engines + OwnHornAndFog + Ambience + Traffic;
	}
}
