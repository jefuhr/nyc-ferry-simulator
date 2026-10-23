using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Light heave, roll and pitch layer for feel and visuals. Each is a damped oscillator at its natural
	/// period driven toward a quasi-static equilibrium:
	/// <list type="bullet">
	/// <item>Roll: heeling moment of the lateral forces about the centre of gravity over m·g·GM_T, so the
	/// boat heels outward in a turn and to leeward in a beam wind.</item>
	/// <item>Pitch: running trim from the calm-water curve plus the bow-up moment of acceleration acting
	/// above the thrust line, over m·g·GM_L.</item>
	/// <item>Heave: running sinkage from the calm-water curve.</item>
	/// </list>
	/// These degrees of freedom do not feed back into the horizontal dynamics. Seakeeping is out of scope.
	/// </summary>
	public sealed class MotionResponse
	{
		private readonly double _gmTransverse;
		private readonly double _gmLongitudinal;
		private readonly double _rollFrequency;
		private readonly double _pitchFrequency;
		private readonly double _heaveFrequency;
		private readonly double _rollDamping;
		private readonly double _pitchDamping;
		private readonly double _heaveDamping;
		private readonly double[] _froude;
		private readonly double[] _trim;
		private readonly double[] _sinkage;

		public MotionResponse(VesselParameters parameters)
		{
			var section = Required.Section(parameters.MotionResponse, "motionResponse");
			_gmTransverse = Required.Positive(section.TransverseMetacentricHeight, "motionResponse.transverseMetacentricHeight");
			_gmLongitudinal = Required.Positive(section.LongitudinalMetacentricHeight, "motionResponse.longitudinalMetacentricHeight");
			_rollFrequency = 2.0 * Math.PI / Required.Positive(section.RollPeriod, "motionResponse.rollPeriod");
			_pitchFrequency = 2.0 * Math.PI / Required.Positive(section.PitchPeriod, "motionResponse.pitchPeriod");
			_heaveFrequency = 2.0 * Math.PI / Required.Positive(section.HeavePeriod, "motionResponse.heavePeriod");
			_rollDamping = Required.Positive(section.RollDampingRatio, "motionResponse.rollDampingRatio");
			_pitchDamping = Required.Positive(section.PitchDampingRatio, "motionResponse.pitchDampingRatio");
			_heaveDamping = Required.Positive(section.HeaveDampingRatio, "motionResponse.heaveDampingRatio");

			var points = Required.Section(section.RunningAttitude, "motionResponse.runningAttitude");
			if (points.Count < 2)
			{
				throw new ArgumentException("motionResponse.runningAttitude needs at least two points.");
			}

			_froude = new double[points.Count];
			_trim = new double[points.Count];
			_sinkage = new double[points.Count];
			for (var i = 0; i < points.Count; i++)
			{
				var point = Required.Section(points[i], $"motionResponse.runningAttitude[{i}]");
				_froude[i] = Required.NonNegative(point.FroudeNumber, $"motionResponse.runningAttitude[{i}].froudeNumber");
				_trim[i] = Required.Value(point.Trim, $"motionResponse.runningAttitude[{i}].trim");
				_sinkage[i] = Required.Value(point.Sinkage, $"motionResponse.runningAttitude[{i}].sinkage");
				if (i > 0 && !(_froude[i] > _froude[i - 1]))
				{
					throw new ArgumentException("motionResponse.runningAttitude must be in increasing Froude number order.");
				}
			}
		}

		public double Heave { get; private set; }

		public double HeaveRate { get; private set; }

		public double Roll { get; private set; }

		public double RollRate { get; private set; }

		public double Pitch { get; private set; }

		public double PitchRate { get; private set; }

		public void Reset(double heave, double heaveRate, double roll, double rollRate, double pitch, double pitchRate)
		{
			Heave = heave;
			HeaveRate = heaveRate;
			Roll = roll;
			RollRate = rollRate;
			Pitch = pitch;
			PitchRate = pitchRate;
		}

		/// <summary>Running trim (rad) and sinkage (m) in calm deep water at a Froude number.</summary>
		public void RunningAttitude(double froude, out double trim, out double sinkage)
		{
			var last = _froude.Length - 1;
			if (froude <= _froude[0])
			{
				trim = _trim[0];
				sinkage = _sinkage[0];
				return;
			}

			if (froude >= _froude[last])
			{
				trim = _trim[last];
				sinkage = _sinkage[last];
				return;
			}

			var i = 1;
			while (_froude[i] < froude)
			{
				i++;
			}

			var t = (froude - _froude[i - 1]) / (_froude[i] - _froude[i - 1]);
			trim = _trim[i - 1] + t * (_trim[i] - _trim[i - 1]);
			sinkage = _sinkage[i - 1] + t * (_sinkage[i] - _sinkage[i - 1]);
		}

		/// <summary>
		/// Advances one step. <paramref name="heelingMoment"/> is the roll moment about the centre of gravity
		/// (N·m, positive starboard side down), <paramref name="surgeAcceleration"/> the forward acceleration
		/// (m/s²) and <paramref name="accelerationArm"/> the height of the centre of gravity above the thrust
		/// line (m).
		/// </summary>
		public void Step(double dt, double mass, double froude, double heelingMoment, double surgeAcceleration, double accelerationArm)
		{
			var weight = mass * PhysicalConstants.Gravity;
			RunningAttitude(Math.Abs(froude), out var runningTrim, out var runningSinkage);
			var rollTarget = heelingMoment / (weight * _gmTransverse);
			var pitchTarget = runningTrim + surgeAcceleration * accelerationArm / (PhysicalConstants.Gravity * _gmLongitudinal);

			// Semi-implicit Euler: rates first, then angles with the new rates.
			RollRate += (-2.0 * _rollDamping * _rollFrequency * RollRate - _rollFrequency * _rollFrequency * (Roll - rollTarget)) * dt;
			Roll += RollRate * dt;
			PitchRate += (-2.0 * _pitchDamping * _pitchFrequency * PitchRate - _pitchFrequency * _pitchFrequency * (Pitch - pitchTarget)) * dt;
			Pitch += PitchRate * dt;
			HeaveRate += (-2.0 * _heaveDamping * _heaveFrequency * HeaveRate - _heaveFrequency * _heaveFrequency * (Heave - runningSinkage)) * dt;
			Heave += HeaveRate * dt;
		}
	}
}
