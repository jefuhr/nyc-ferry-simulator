using System;
using FerrySim.Core.Contracts;
using FerrySim.Sim;

namespace FerrySim.Audio
{
	public readonly struct EngineMix
	{
		public EngineMix(double idleGain, double midGain, double highGain, double idlePitch, double midPitch, double highPitch,
			double gain, double lowGain, double propGain, double propPitch)
		{
			IdleGain = idleGain; MidGain = midGain; HighGain = highGain;
			IdlePitch = idlePitch; MidPitch = midPitch; HighPitch = highPitch;
			Gain = gain; LowGain = lowGain; PropGain = propGain; PropPitch = propPitch;
		}
		public double IdleGain { get; }
		public double MidGain { get; }
		public double HighGain { get; }
		public double IdlePitch { get; }
		public double MidPitch { get; }
		public double HighPitch { get; }
		public double Gain { get; }
		public double LowGain { get; }
		public double PropGain { get; }
		public double PropPitch { get; }
	}

	/// <summary>Render-only estimates. Inputs are SI: crankshaft and propeller rad/s, dimensionless ratio/throttle.</summary>
	public static class EngineSoundMapping
	{
		public static EngineMix Evaluate(double engineSpeed, double idleSpeed, double ratedSpeed, double shaftSpeed,
			double gearRatio, double throttle, GearState gear)
		{
			if (!Finite(engineSpeed) || !Finite(shaftSpeed) || !Finite(throttle) || !(idleSpeed > 0) ||
				!(ratedSpeed > idleSpeed) || !Finite(ratedSpeed) || !(gearRatio > 0) || !Finite(gearRatio)) return default;
			var speed = Math.Abs(engineSpeed);
			var normalized = Clamp((speed - idleSpeed) / (ratedSpeed - idleSpeed));
			// Complementary smoothstep weights: amplitude sum = 1, with zero slopes at crossovers.
			var blend = normalized < .5 ? Smooth(normalized * 2) : Smooth((normalized - .5) * 2);
			var idle = normalized < .5 ? 1 - blend : 0;
			var high = normalized < .5 ? 0 : blend;
			var mid = 1 - idle - high;
			var shaft = Clamp(Math.Abs(shaftSpeed) * gearRatio / ratedSpeed);
			var engaged = gear == GearState.Ahead || gear == GearState.Astern;
			var load = engaged ? .65 * Clamp(throttle) + .35 * shaft : 0;
			return new EngineMix(idle, mid, high, Pitch(speed / idleSpeed), Pitch(speed / ((idleSpeed + ratedSpeed) * .5)),
				Pitch(speed / ratedSpeed), .3 + .45 * normalized + .25 * load, .18 * load,
				.45 * shaft + .15 * load, .7 + .6 * shaft);
		}
		private static double Smooth(double value) => value * value * (3 - 2 * value);
		private static double Pitch(double value) => Math.Max(.75, Math.Min(1.3, value));
		internal static double Clamp(double value) => Finite(value) ? Math.Max(0, Math.Min(1, value)) : 0;
		internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	public enum ClutchSound { Disengage, Engage }

	/// <summary>Captures every fixed-step gear change without making any simulation input or allocating.</summary>
	public sealed class ClutchEvents
	{
		private readonly ClutchSound[] _queue = new ClutchSound[64];
		private int _first, _count;
		private GearState _gear;
		public int Dropped { get; private set; }
		public void Reset(GearState gear) { _gear = gear; _first = _count = Dropped = 0; }
		public void Observe(GearState gear)
		{
			if (gear == _gear) return;
			_gear = gear;
			if (_count == _queue.Length) { Dropped++; return; }
			_queue[(_first + _count++) % _queue.Length] = gear == GearState.Ahead || gear == GearState.Astern ? ClutchSound.Engage : ClutchSound.Disengage;
		}
		public bool TryTake(out ClutchSound sound)
		{
			if (_count == 0) { sound = default; return false; }
			sound = _queue[_first]; _first = (_first + 1) % _queue.Length; _count--; return true;
		}
	}

	public enum HornState { Off, Attack, Sustain, Release }

	public sealed class HornEnvelope
	{
		public const double AttackSeconds = .08, ReleaseSeconds = .15;
		public HornState State { get; private set; }
		public double Gain { get; private set; }
		public void Hold(bool held)
		{
			if (held && (State == HornState.Off || State == HornState.Release)) State = HornState.Attack;
			else if (!held && State != HornState.Off) State = HornState.Release;
		}
		public void Advance(double seconds)
		{
			if (!(seconds >= 0) || !EngineSoundMapping.Finite(seconds)) return;
			if (State == HornState.Attack) { Gain = Math.Min(1, Gain + seconds / AttackSeconds); if (Gain >= 1) State = HornState.Sustain; }
			else if (State == HornState.Release) { Gain = Math.Max(0, Gain - seconds / ReleaseSeconds); if (Gain <= 0) State = HornState.Off; }
		}
		public void Reset() { Gain = 0; State = HornState.Off; }
	}

	public readonly struct ListeningPosition
	{
		private ListeningPosition(double engines, double wind, double water, double cutoff)
		{ EngineGain = engines; WindGain = wind; WaterGain = water; EngineCutoffHertz = cutoff; }
		public double EngineGain { get; }
		public double WindGain { get; }
		public double WaterGain { get; }
		public double EngineCutoffHertz { get; }
		public static ListeningPosition ForWheelhouse(bool inside) => inside ? new ListeningPosition(.32, .18, .55, 850) : new ListeningPosition(1, 1, 1, 22000);
	}

	[Flags] public enum ContactSound { None = 0, Fender = 1, Creak = 2, Both = Fender | Creak }

	public sealed class ContactSounds
	{
		private long _seen;
		private double _fenderAt = double.NegativeInfinity, _creakAt = double.NegativeInfinity;
		public void Reset() { _seen = 0; _fenderAt = _creakAt = double.NegativeInfinity; }
		public ContactSound Update(ContactLatch latch, double time)
		{
			if (latch.StepsWithContact <= _seen) return ContactSound.None;
			_seen = latch.StepsWithContact;
			if (!(latch.LastContactForce > 0) || !EngineSoundMapping.Finite(latch.LastContactForce)) return ContactSound.None;
			var sound = ContactSound.None;
			if (time - _fenderAt >= .75) { sound |= ContactSound.Fender; _fenderAt = time; }
			if (time - _creakAt >= 3) { sound |= ContactSound.Creak; _creakAt = time; }
			return sound;
		}
	}

	/// <summary>Private render RNG, deliberately unrelated to UnityEngine.Random and the simulation.</summary>
	public sealed class GullTiming
	{
		private uint _random;
		private double _next;
		public GullTiming(uint seed) { _random = seed == 0 ? 1 : seed; _next = Interval(); }
		private double Interval() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return 8 + _random / (double)uint.MaxValue * 20; }
		public bool Due(double time, bool nearLanding)
		{
			if (time < _next) return false;
			_next = time + Interval(); return nearLanding;
		}
	}
}
