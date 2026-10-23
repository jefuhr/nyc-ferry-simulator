using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Hydrodynamic forces on the two demihulls at the reference point, from velocities relative to the
	/// water (body frame: surge u, sway v, yaw rate r).
	/// <list type="bullet">
	/// <item>Resistance: R = ½ρSu²[(1+k)C_F + C_A + s·C_R(Fn)], ITTC-57 friction line, astern × factor.</item>
	/// <item>Lift-like linear terms in MMG form, written with |u| and u so they vanish at zero headway and
	/// change sign correctly going astern: Y = ½ρLT(Y'_v|u|v + Y'_r u rL), N = ½ρL²T(N'_v u v + N'_r|u| rL).</item>
	/// <item>Cross-flow drag on both demihulls, −½ρC_D·2T∫(v+xr)|v+xr|dx, which carries the forces at low
	/// speed, in drift and when pivoting.</item>
	/// </list>
	/// </summary>
	public sealed class HullModel
	{
		private const int Strips = 20;

		private readonly double _rho = PhysicalConstants.WaterDensity;
		private readonly double[] _stripPositions = new double[Strips];
		private readonly double _stripLength;
		private readonly double[] _resistanceFroude;
		private readonly double[] _resistanceCoefficient;
		private readonly double _wettedSurface;
		private readonly double _formFactor;
		private readonly double _correlation;
		private readonly double _residuaryScale;
		private readonly double _asternFactor;
		private readonly double _yv;
		private readonly double _yr;
		private readonly double _nv;
		private readonly double _nr;
		private readonly double _xvv;
		private readonly double _xvr;
		private readonly double _xrr;
		private readonly double _crossFlow;

		public HullModel(VesselParameters parameters)
		{
			var particulars = Required.Section(parameters.Particulars, "particulars");
			var hull = Required.Section(parameters.Hull, "hull");
			Length = Required.Positive(particulars.LengthWaterline, "particulars.lengthWaterline");
			Draft = Required.Positive(particulars.Draft, "particulars.draft");
			DemihullSeparation = Required.Positive(hull.DemihullSeparation, "hull.demihullSeparation");
			_wettedSurface = Required.Positive(hull.WettedSurface, "hull.wettedSurface");
			_formFactor = Required.InRange(hull.FormFactor, "hull.formFactor", 1.0, 3.0);
			_correlation = Required.Value(hull.CorrelationAllowance, "hull.correlationAllowance");
			_residuaryScale = Required.NonNegative(hull.ResiduaryResistanceScale, "hull.residuaryResistanceScale");
			_asternFactor = Required.Positive(hull.AsternResistanceFactor, "hull.asternResistanceFactor");
			SurgeAddedMass = Required.NonNegative(hull.SurgeAddedMass, "hull.surgeAddedMass");
			SwayAddedMass = Required.NonNegative(hull.SwayAddedMass, "hull.swayAddedMass");
			YawAddedInertia = Required.NonNegative(hull.YawAddedInertia, "hull.yawAddedInertia");
			_yv = Required.Value(hull.DerivativeYv, "hull.derivativeYv");
			_yr = Required.Value(hull.DerivativeYr, "hull.derivativeYr");
			_nv = Required.Value(hull.DerivativeNv, "hull.derivativeNv");
			_nr = Required.Value(hull.DerivativeNr, "hull.derivativeNr");
			_xvv = Required.Value(hull.DerivativeXvv, "hull.derivativeXvv");
			_xvr = Required.Value(hull.DerivativeXvr, "hull.derivativeXvr");
			_xrr = Required.Value(hull.DerivativeXrr, "hull.derivativeXrr");
			_crossFlow = Required.NonNegative(hull.CrossFlowDragCoefficient, "hull.crossFlowDragCoefficient");
			LateralForceDepth = Required.Value(hull.LateralForceDepth, "hull.lateralForceDepth");

			if (_yv >= 0.0 || _nr >= 0.0)
			{
				throw new ArgumentException("hull.derivativeYv and hull.derivativeNr must be negative (damping).");
			}

			var points = Required.Section(hull.ResiduaryResistance, "hull.residuaryResistance");
			if (points.Count < 2)
			{
				throw new ArgumentException("hull.residuaryResistance needs at least two points.");
			}

			_resistanceFroude = new double[points.Count];
			_resistanceCoefficient = new double[points.Count];
			for (var i = 0; i < points.Count; i++)
			{
				var point = Required.Section(points[i], $"hull.residuaryResistance[{i}]");
				_resistanceFroude[i] = Required.NonNegative(point.FroudeNumber, $"hull.residuaryResistance[{i}].froudeNumber");
				_resistanceCoefficient[i] = Required.NonNegative(point.Coefficient, $"hull.residuaryResistance[{i}].coefficient");
				if (i > 0 && !(_resistanceFroude[i] > _resistanceFroude[i - 1]))
				{
					throw new ArgumentException("hull.residuaryResistance must be in increasing Froude number order.");
				}
			}

			_stripLength = Length / Strips;
			for (var i = 0; i < Strips; i++)
			{
				_stripPositions[i] = -0.5 * Length + (i + 0.5) * _stripLength;
			}
		}

		/// <summary>Waterline length L, m.</summary>
		public double Length { get; }

		/// <summary>Hull draft of one demihull T, m.</summary>
		public double Draft { get; }

		/// <summary>Demihull centreline separation, m.</summary>
		public double DemihullSeparation { get; }

		public double SurgeAddedMass { get; }

		public double SwayAddedMass { get; }

		public double YawAddedInertia { get; }

		/// <summary>Depth of the hull's lateral force below the waterline, m.</summary>
		public double LateralForceDepth { get; }

		/// <summary>
		/// Calm-water resistance magnitude at a speed through the water (either direction), N, including
		/// the shallow-water multipliers. Positive; the force opposes the motion.
		/// </summary>
		public double Resistance(double surge, in ShallowWaterFactors shallow)
		{
			var speed = Math.Abs(surge);
			if (speed <= 0.0)
			{
				return 0.0;
			}

			var reynolds = Math.Max(speed * Length / PhysicalConstants.WaterKinematicViscosity, 1e5);
			var logRe = Math.Log10(reynolds) - 2.0;
			var friction = 0.075 / (logRe * logRe);
			var froude = speed / Math.Sqrt(PhysicalConstants.Gravity * Length);
			var viscous = _formFactor * friction + _correlation;
			var residuary = _residuaryScale * ResiduaryCoefficient(froude);
			var dynamicPressure = 0.5 * _rho * _wettedSurface * speed * speed;
			var resistance = dynamicPressure * (viscous * shallow.ViscousResistance + residuary * shallow.ResiduaryResistance);
			return surge < 0.0 ? resistance * _asternFactor : resistance;
		}

		/// <summary>Residuary resistance coefficient C_R at a Froude number, before the calibration scale.</summary>
		public double ResiduaryCoefficient(double froude)
		{
			var last = _resistanceFroude.Length - 1;
			if (froude <= _resistanceFroude[0])
			{
				return _resistanceCoefficient[0];
			}

			if (froude >= _resistanceFroude[last])
			{
				return _resistanceCoefficient[last];
			}

			var i = 1;
			while (_resistanceFroude[i] < froude)
			{
				i++;
			}

			var t = (froude - _resistanceFroude[i - 1]) / (_resistanceFroude[i] - _resistanceFroude[i - 1]);
			return _resistanceCoefficient[i - 1] + t * (_resistanceCoefficient[i] - _resistanceCoefficient[i - 1]);
		}

		/// <summary>Hull surge force, sway force and yaw moment at the reference point.</summary>
		public void Forces(double u, double v, double r, in ShallowWaterFactors shallow, out double x, out double y, out double n)
		{
			var lengthDraft = 0.5 * _rho * Length * Draft;
			var resistance = Resistance(u, shallow);
			x = (u >= 0.0 ? -resistance : resistance) +
				lengthDraft * (_xvv * v * v + _xvr * v * r * Length + _xrr * r * r * Length * Length);

			var absU = Math.Abs(u);
			var linear = shallow.LinearDerivatives;
			y = lengthDraft * linear * (_yv * absU * v + _yr * u * r * Length);
			n = lengthDraft * Length * linear * (_nv * u * v + _nr * absU * r * Length);

			// Both demihulls see the same local cross-flow v + x·r; their lateral offset does not change it.
			var crossFlow = -0.5 * _rho * _crossFlow * shallow.CrossFlow * 2.0 * Draft * _stripLength;
			var sideForce = 0.0;
			var yawMoment = 0.0;
			for (var i = 0; i < Strips; i++)
			{
				var position = _stripPositions[i];
				var local = v + position * r;
				var load = local * Math.Abs(local);
				sideForce += load;
				yawMoment += position * load;
			}

			y += crossFlow * sideForce;
			n += crossFlow * yawMoment;
		}
	}
}
