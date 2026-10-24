using System;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	/// <summary>Which way a propeller shaft is turning, as far as the shaft rpm readout can tell.</summary>
	public enum ShaftRotation
	{
		/// <summary>The shaft rpm reads 0.</summary>
		Stopped = 0,
		Ahead = 1,
		Astern = 2,
		Unknown = 3,
	}

	/// <summary>How a gear badge is drawn. SHIFTING is its own tone so it can never look engaged.</summary>
	public enum GearTone
	{
		/// <summary>Neutral: outlined, no drive.</summary>
		Idle = 0,

		/// <summary>Ahead or astern with the clutch fully in.</summary>
		Engaged = 1,

		/// <summary>Clutch filling, engaging or releasing: no reliable drive to the shaft.</summary>
		Transition = 2,

		Unknown = 3,
	}

	/// <summary>What one engine column says about the gear and shaft, derived only from <see cref="EngineTelemetry"/>.</summary>
	public readonly struct GearReadout
	{
		private GearReadout(string label, string shortLabel, GearTone tone, ShaftRotation shaft, string shaftRpm)
		{
			Label = label;
			ShortLabel = shortLabel;
			Tone = tone;
			Shaft = shaft;
			ShaftRpm = shaftRpm;
		}

		/// <summary>"NEUTRAL", "AHEAD", "ASTERN" or "SHIFTING", as the control head would indicate it.</summary>
		public string Label { get; }

		/// <summary>Compact label for the HUD: "N", "AHD", "AST" or "SHIFT".</summary>
		public string ShortLabel { get; }

		public GearTone Tone { get; }

		public ShaftRotation Shaft { get; }

		/// <summary>Shaft speed magnitude in whole rpm.</summary>
		public string ShaftRpm { get; }

		/// <summary>"AHEAD", "ASTERN", "STOPPED" or <see cref="InstrumentFormat.NoValue"/> for the shaft line.</summary>
		public string ShaftLabel =>
			Shaft == ShaftRotation.Ahead ? "AHEAD" :
			Shaft == ShaftRotation.Astern ? "ASTERN" :
			Shaft == ShaftRotation.Stopped ? "STOPPED" :
			InstrumentFormat.NoValue;

		public static GearReadout From(EngineTelemetry engine)
		{
			var shaftRpm = InstrumentFormat.Rpm(engine.ShaftSpeed);
			var shaft = ShaftDirection(engine.ShaftSpeed);
			switch (engine.Gear)
			{
				case GearState.Neutral:
					return new GearReadout("NEUTRAL", "N", GearTone.Idle, shaft, shaftRpm);
				case GearState.Ahead:
					return new GearReadout("AHEAD", "AHD", GearTone.Engaged, shaft, shaftRpm);
				case GearState.Astern:
					return new GearReadout("ASTERN", "AST", GearTone.Engaged, shaft, shaftRpm);
				case GearState.Shifting:
					return new GearReadout("SHIFTING", "SHIFT", GearTone.Transition, shaft, shaftRpm);
				default:
					return new GearReadout(InstrumentFormat.NoValue, InstrumentFormat.NoValue, GearTone.Unknown, shaft, shaftRpm);
			}
		}

		/// <summary>Direction from the sign of the shaft speed, but only once the readout would show a non-zero rpm.</summary>
		public static ShaftRotation ShaftDirection(double shaftSpeed)
		{
			if (!InstrumentFormat.IsFinite(shaftSpeed))
			{
				return ShaftRotation.Unknown;
			}

			var rpm = Math.Round(DisplayUnits.Rpm(shaftSpeed), MidpointRounding.AwayFromZero);
			return rpm > 0.0 ? ShaftRotation.Ahead : rpm < 0.0 ? ShaftRotation.Astern : ShaftRotation.Stopped;
		}
	}
}
