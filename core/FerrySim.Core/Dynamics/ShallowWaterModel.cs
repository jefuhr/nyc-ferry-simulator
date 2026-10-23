using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>Multipliers that finite depth applies to the deep-water hull model.</summary>
	public readonly struct ShallowWaterFactors
	{
		public ShallowWaterFactors(double viscousResistance, double residuaryResistance, double linearDerivatives, double crossFlow, double addedMass)
		{
			ViscousResistance = viscousResistance;
			ResiduaryResistance = residuaryResistance;
			LinearDerivatives = linearDerivatives;
			CrossFlow = crossFlow;
			AddedMass = addedMass;
		}

		/// <summary>No correction: deep water.</summary>
		public static ShallowWaterFactors Deep => new ShallowWaterFactors(1.0, 1.0, 1.0, 1.0, 1.0);

		/// <summary>On frictional resistance (return flow past the hull).</summary>
		public double ViscousResistance { get; }

		/// <summary>On residuary resistance (return flow and the depth-critical wave hump).</summary>
		public double ResiduaryResistance { get; }

		/// <summary>On the linear sway and yaw derivatives.</summary>
		public double LinearDerivatives { get; }

		/// <summary>On the cross-flow drag coefficient.</summary>
		public double CrossFlow { get; }

		/// <summary>On sway added mass and yaw added inertia.</summary>
		public double AddedMass { get; }
	}

	/// <summary>
	/// Finite-depth corrections. Depth is clamped to <c>minimumDepthRatio</c> times the hull draft so the
	/// factors stay finite as the water shoals to the draft and below (grounding is reported, not modelled).
	/// <list type="bullet">
	/// <item>Return flow: speed increase ΔV/V = c (A_X/h² − 0.05) on both resistance parts (Lackenby 1963).</item>
	/// <item>Depth-critical hump: residuary resistance × 1 + a·exp(−((Fn_h − 0.95)/w)²), weighted by L/h.</item>
	/// <item>Manoeuvring: derivatives, cross-flow drag and added mass × 1 + c (T/h)²/(1 − T/h).</item>
	/// </list>
	/// </summary>
	public sealed class ShallowWaterModel
	{
		private const double CriticalDepthFroude = 0.95;

		private readonly double _draft;
		private readonly double _length;
		private readonly double _midshipArea;
		private readonly double _blockage;
		private readonly double _criticalAmplitude;
		private readonly double _criticalWidth;
		private readonly double _linear;
		private readonly double _crossFlow;
		private readonly double _addedMass;
		private readonly double _minimumDepth;

		public ShallowWaterModel(VesselParameters parameters)
		{
			var particulars = Required.Section(parameters.Particulars, "particulars");
			var hull = Required.Section(parameters.Hull, "hull");
			var section = Required.Section(parameters.ShallowWater, "shallowWater");
			_draft = Required.Positive(particulars.Draft, "particulars.draft");
			_length = Required.Positive(particulars.LengthWaterline, "particulars.lengthWaterline");
			_midshipArea = Required.Positive(hull.MidshipSectionArea, "hull.midshipSectionArea");
			_blockage = Required.NonNegative(section.BlockageCoefficient, "shallowWater.blockageCoefficient");
			_criticalAmplitude = Required.NonNegative(section.CriticalSpeedAmplitude, "shallowWater.criticalSpeedAmplitude");
			_criticalWidth = Required.Positive(section.CriticalSpeedWidth, "shallowWater.criticalSpeedWidth");
			_linear = Required.NonNegative(section.LinearDerivativeFactor, "shallowWater.linearDerivativeFactor");
			_crossFlow = Required.NonNegative(section.CrossFlowFactor, "shallowWater.crossFlowFactor");
			_addedMass = Required.NonNegative(section.AddedMassFactor, "shallowWater.addedMassFactor");
			_minimumDepth = _draft * Required.InRange(section.MinimumDepthRatio, "shallowWater.minimumDepthRatio", 1.01, 2.0);
		}

		/// <summary>The depth the corrections actually use for a sampled depth.</summary>
		public double EffectiveDepth(double depth)
		{
			if (double.IsNaN(depth) || double.IsPositiveInfinity(depth))
			{
				// IEnvironment promises finite depths; treat anything else as open water.
				return double.PositiveInfinity;
			}

			return depth > _minimumDepth ? depth : _minimumDepth;
		}

		/// <summary>Correction factors at a sampled depth and a speed through the water.</summary>
		public ShallowWaterFactors Evaluate(double depth, double speed)
		{
			var h = EffectiveDepth(depth);
			if (double.IsPositiveInfinity(h))
			{
				return ShallowWaterFactors.Deep;
			}

			var blockage = 1.0 + _blockage * Math.Max(0.0, _midshipArea / (h * h) - 0.05);
			var returnFlow = blockage * blockage;

			var depthFroude = Math.Abs(speed) / Math.Sqrt(PhysicalConstants.Gravity * h);
			var relativeDepth = Math.Clamp((_length / h - 0.5) / 2.5, 0.0, 1.0);
			var detuning = (depthFroude - CriticalDepthFroude) / _criticalWidth;
			var hump = 1.0 + _criticalAmplitude * relativeDepth * Math.Exp(-detuning * detuning);

			var ratio = _draft / h;
			var growth = ratio * ratio / (1.0 - ratio);
			return new ShallowWaterFactors(
				returnFlow,
				returnFlow * hump,
				1.0 + _linear * growth,
				1.0 + _crossFlow * growth,
				1.0 + _addedMass * growth);
		}
	}
}
