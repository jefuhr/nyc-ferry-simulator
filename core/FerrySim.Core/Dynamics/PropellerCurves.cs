using System;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Open-water thrust and torque coefficients of the Wageningen B-screw series at Re = 2·10⁶, as the
	/// regression polynomials of Oosterveld and van Oossanen (1975), coefficients as tabulated by
	/// Bernitsas, Ray and Kinley (1981), "KT, KQ and efficiency curves for the Wageningen B-series
	/// propellers", University of Michigan. Valid for the first quadrant, 0 ≤ J ≤ J at zero thrust.
	/// </summary>
	public static class WageningenBSeries
	{
		// Rows of { C, s, t, u, v }: C · J^s · (P/D)^t · (AE/A0)^u · Z^v.
		private static readonly double[] ThrustTerms =
		{
			+0.00880496, 0, 0, 0, 0, -0.204554, 1, 0, 0, 0, +0.166351, 0, 1, 0, 0, +0.158114, 0, 2, 0, 0,
			-0.147581, 2, 0, 1, 0, -0.481497, 1, 1, 1, 0, +0.415437, 0, 2, 1, 0, +0.0144043, 0, 0, 0, 1,
			-0.0530054, 2, 0, 0, 1, +0.0143481, 0, 1, 0, 1, +0.0606826, 1, 1, 0, 1, -0.0125894, 0, 0, 1, 1,
			+0.0109689, 1, 0, 1, 1, -0.133698, 0, 3, 0, 0, +0.00638407, 0, 6, 0, 0, -0.00132718, 2, 6, 0, 0,
			+0.168496, 3, 0, 1, 0, -0.0507214, 0, 0, 2, 0, +0.0854559, 2, 0, 2, 0, -0.0504475, 3, 0, 2, 0,
			+0.010465, 1, 6, 2, 0, -0.00648272, 2, 6, 2, 0, -0.00841728, 0, 3, 0, 1, +0.0168424, 1, 3, 0, 1,
			-0.00102296, 3, 3, 0, 1, -0.0317791, 0, 3, 1, 1, +0.018604, 1, 0, 2, 1, -0.00410798, 0, 2, 2, 1,
			-0.000606848, 0, 0, 0, 2, -0.0049819, 1, 0, 0, 2, +0.0025983, 2, 0, 0, 2, -0.000560528, 3, 0, 0, 2,
			-0.00163652, 1, 2, 0, 2, -0.000328787, 1, 6, 0, 2, +0.000116502, 2, 6, 0, 2, +0.000690904, 0, 0, 1, 2,
			+0.00421749, 0, 3, 1, 2, +0.0000565229, 3, 6, 1, 2, -0.00146564, 0, 3, 2, 2,
		};

		private static readonly double[] TorqueTerms =
		{
			+0.00379368, 0, 0, 0, 0, +0.00886523, 2, 0, 0, 0, -0.032241, 1, 1, 0, 0, +0.00344778, 0, 2, 0, 0,
			-0.0408811, 0, 1, 1, 0, -0.108009, 1, 1, 1, 0, -0.0885381, 2, 1, 1, 0, +0.188561, 0, 2, 1, 0,
			-0.00370871, 1, 0, 0, 1, +0.00513696, 0, 1, 0, 1, +0.0209449, 1, 1, 0, 1, +0.00474319, 2, 1, 0, 1,
			-0.00723408, 2, 0, 1, 1, +0.00438388, 1, 1, 1, 1, -0.0269403, 0, 2, 1, 1, +0.0558082, 3, 0, 1, 0,
			+0.0161886, 0, 3, 1, 0, +0.00318086, 1, 3, 1, 0, +0.015896, 0, 0, 2, 0, +0.0471729, 1, 0, 2, 0,
			+0.0196283, 3, 0, 2, 0, -0.0502782, 0, 1, 2, 0, -0.030055, 3, 1, 2, 0, +0.0417122, 2, 2, 2, 0,
			-0.0397722, 0, 3, 2, 0, -0.00350024, 0, 6, 2, 0, -0.0106854, 3, 0, 0, 1, +0.00110903, 3, 3, 0, 1,
			-0.000313912, 0, 6, 0, 1, +0.0035985, 3, 0, 1, 1, -0.00142121, 0, 6, 1, 1, -0.00383637, 1, 0, 2, 1,
			+0.0126803, 0, 2, 2, 1, -0.00318278, 2, 3, 2, 1, +0.00334268, 0, 6, 2, 1, -0.00183491, 1, 1, 0, 2,
			+0.000112451, 3, 2, 0, 2, -0.0000297228, 3, 6, 0, 2, +0.000269551, 1, 0, 1, 2, +0.00083265, 2, 0, 1, 2,
			+0.00155334, 0, 2, 1, 2, +0.000302683, 0, 6, 1, 2, -0.0001843, 0, 0, 2, 2, -0.000425399, 0, 3, 2, 2,
			+0.0000869243, 3, 3, 2, 2, -0.0004659, 0, 6, 2, 2, +0.0000554194, 1, 6, 2, 2,
		};

		/// <summary>Thrust coefficient K_T = T / (ρn²D⁴).</summary>
		public static double ThrustCoefficient(double advanceRatio, double pitchRatio, double areaRatio, double blades) =>
			Evaluate(ThrustTerms, advanceRatio, pitchRatio, areaRatio, blades);

		/// <summary>Torque coefficient K_Q = Q / (ρn²D⁵).</summary>
		public static double TorqueCoefficient(double advanceRatio, double pitchRatio, double areaRatio, double blades) =>
			Evaluate(TorqueTerms, advanceRatio, pitchRatio, areaRatio, blades);

		/// <summary>Advance ratio at which the thrust coefficient falls to zero (end of the ahead quadrant).</summary>
		public static double ZeroThrustAdvanceRatio(double pitchRatio, double areaRatio, double blades)
		{
			var low = 0.0;
			var high = 3.0;
			for (var i = 0; i < 60; i++)
			{
				var mid = 0.5 * (low + high);
				if (ThrustCoefficient(mid, pitchRatio, areaRatio, blades) > 0.0)
				{
					low = mid;
				}
				else
				{
					high = mid;
				}
			}

			return 0.5 * (low + high);
		}

		private static double Evaluate(double[] terms, double j, double pitchRatio, double areaRatio, double blades)
		{
			var sum = 0.0;
			for (var i = 0; i < terms.Length; i += 5)
			{
				sum += terms[i] * Math.Pow(j, terms[i + 1]) * Math.Pow(pitchRatio, terms[i + 2]) *
					Math.Pow(areaRatio, terms[i + 3]) * Math.Pow(blades, terms[i + 4]);
			}

			return sum;
		}
	}

	/// <summary>
	/// Four-quadrant open-water characteristics C_T*(β) and C_Q*(β), with
	/// β = atan2(V_A, 0.7πnD), T = ½ρ(V_A² + (0.7πnD)²)(π/4)D² C_T* and Q = the same times D C_Q*.
	/// Shaft speed n is positive in the direction that drives the boat ahead, for either hand.
	/// <para>
	/// The ahead quadrant (0 ≤ β ≤ β at zero thrust) is the Wageningen B-series itself. The other
	/// quadrants come from the blade-element "L-model" of Häusler, Saccon, Hauser, Pascoal and Aguiar
	/// (2013), "Four-quadrant propeller modeling: a low-order harmonic approximation" (IFAC CAMS),
	/// fitted by linear least squares to the same B-series curve, with a flat-plate drag prior at 90°
	/// incidence and a reduced lift slope for trailing-edge-first (astern) flow. The two are blended over
	/// 30° at each edge of the ahead quadrant. The fitted model's error at the ahead bollard point is
	/// applied as a ratio at the astern bollard point too (blended out 60° either side of β = π), since both
	/// come from the same blade incidence; astern-to-ahead ratios therefore follow the blade-element
	/// model while their level follows the B-series. Values are tabulated every 2.5° for periodic cubic
	/// interpolation.
	/// </para>
	/// </summary>
	public sealed class FourQuadrantPropellerCurves
	{
		private const int TableSize = 144;
		private const double TableStep = 2.0 * Math.PI / TableSize;
		private const double BlendWidth = 30.0 * Math.PI / 180.0;
		private const double AsternWindow = 60.0 * Math.PI / 180.0;
		private const int FitSamples = 24;
		private const double TorqueRowWeight = 10.0;
		private const double DragPriorWeight = 3.0;

		private readonly double[] _thrust = new double[TableSize];
		private readonly double[] _torque = new double[TableSize];
		private readonly double[] _fit = new double[5];
		private readonly double _pitchAngle;
		private readonly double _reversedFlowLiftFactor;

		public FourQuadrantPropellerCurves(double pitchRatio, double areaRatio, double blades, double reversedFlowLiftFactor, double bladeNormalDragCoefficient)
		{
			PitchRatio = pitchRatio;
			AreaRatio = areaRatio;
			Blades = blades;
			_reversedFlowLiftFactor = reversedFlowLiftFactor;
			_pitchAngle = Math.Atan(pitchRatio / (0.7 * Math.PI));
			ZeroThrustAdvanceRatio = WageningenBSeries.ZeroThrustAdvanceRatio(pitchRatio, areaRatio, blades);
			ZeroThrustAngle = Math.Atan2(ZeroThrustAdvanceRatio, 0.7 * Math.PI);
			FitLiftDragModel(bladeNormalDragCoefficient);
			BuildTables();
		}

		public double PitchRatio { get; }

		public double AreaRatio { get; }

		public double Blades { get; }

		/// <summary>Advance ratio where the B-series thrust reaches zero.</summary>
		public double ZeroThrustAdvanceRatio { get; }

		/// <summary>Advance angle β of the zero-thrust point, rad.</summary>
		public double ZeroThrustAngle { get; }

		/// <summary>C_T* and C_Q* at advance angle β (any real angle; the curves are 2π-periodic).</summary>
		public void Evaluate(double advanceAngle, out double thrustCoefficient, out double torqueCoefficient)
		{
			var position = (advanceAngle + Math.PI) / TableStep;
			var floor = Math.Floor(position);
			var fraction = position - floor;
			var index = (int)floor % TableSize;
			if (index < 0)
			{
				index += TableSize;
			}

			thrustCoefficient = CatmullRom(_thrust, index, fraction);
			torqueCoefficient = CatmullRom(_torque, index, fraction);
		}

		/// <summary>The fitted blade-element model alone, for inspection and tests.</summary>
		public void EvaluateLiftDragModel(double advanceAngle, out double thrustCoefficient, out double torqueCoefficient)
		{
			LiftDragBasis(advanceAngle, out var t0, out var t1, out var t2, out var t3, out var t4, out var q0, out var q1, out var q2, out var q3, out var q4);
			thrustCoefficient = _fit[0] * t0 + _fit[1] * t1 + _fit[2] * t2 + _fit[3] * t3 + _fit[4] * t4;
			torqueCoefficient = _fit[0] * q0 + _fit[1] * q1 + _fit[2] * q2 + _fit[3] * q3 + _fit[4] * q4;
		}

		/// <summary>Converts a first-quadrant K_T or K_Q to C_T* or C_Q* at the same advance ratio.</summary>
		public static double OpenWaterToFourQuadrant(double coefficient, double advanceRatio) =>
			coefficient / (Math.PI / 8.0 * (advanceRatio * advanceRatio + 0.49 * Math.PI * Math.PI));

		private static double CatmullRom(double[] table, int index, double t)
		{
			var p0 = table[(index + TableSize - 1) % TableSize];
			var p1 = table[index];
			var p2 = table[(index + 1) % TableSize];
			var p3 = table[(index + 2) % TableSize];
			return 0.5 * (2.0 * p1 + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t * t + (3.0 * (p1 - p2) + p3 - p0) * t * t * t);
		}

		// Lift c_L = a1 sin2α + a2 cos2α (times the reversed-flow factor), drag c_D = d0 + d1 cos2α + d2 sin2α,
		// with α = φ − β. Thrust and torque are the lift and drag rotated by β (Häusler et al., eq. 9).
		private void LiftDragBasis(double beta, out double t0, out double t1, out double t2, out double t3, out double t4,
			out double q0, out double q1, out double q2, out double q3, out double q4)
		{
			var alpha = _pitchAngle - beta;
			var reversal = _reversedFlowLiftFactor + (1.0 - _reversedFlowLiftFactor) * 0.5 * (1.0 + Math.Cos(alpha));
			var sin2 = Math.Sin(2.0 * alpha);
			var cos2 = Math.Cos(2.0 * alpha);
			var cosBeta = Math.Cos(beta);
			var sinBeta = Math.Sin(beta);
			t0 = reversal * sin2 * cosBeta;
			t1 = reversal * cos2 * cosBeta;
			t2 = -sinBeta;
			t3 = -cos2 * sinBeta;
			t4 = -sin2 * sinBeta;
			q0 = 0.35 * reversal * sin2 * sinBeta;
			q1 = 0.35 * reversal * cos2 * sinBeta;
			q2 = 0.35 * cosBeta;
			q3 = 0.35 * cos2 * cosBeta;
			q4 = 0.35 * sin2 * cosBeta;
		}

		private void FitLiftDragModel(double bladeNormalDragCoefficient)
		{
			var normal = new double[5, 5];
			var rhs = new double[5];
			var row = new double[5];
			for (var i = 0; i <= FitSamples; i++)
			{
				var j = ZeroThrustAdvanceRatio * i / FitSamples;
				var beta = Math.Atan2(j, 0.7 * Math.PI);
				var thrust = OpenWaterToFourQuadrant(WageningenBSeries.ThrustCoefficient(j, PitchRatio, AreaRatio, Blades), j);
				var torque = OpenWaterToFourQuadrant(WageningenBSeries.TorqueCoefficient(j, PitchRatio, AreaRatio, Blades), j);
				LiftDragBasis(beta, out row[0], out row[1], out row[2], out row[3], out row[4],
					out var q0, out var q1, out var q2, out var q3, out var q4);
				Accumulate(normal, rhs, row, thrust, 1.0);
				row[0] = q0;
				row[1] = q1;
				row[2] = q2;
				row[3] = q3;
				row[4] = q4;
				Accumulate(normal, rhs, row, torque, TorqueRowWeight);
			}

			// Prior: drag at 90° incidence equals the flat-plate value (c_D(90°) = d0 − d1).
			row[0] = 0.0;
			row[1] = 0.0;
			row[2] = 1.0;
			row[3] = -1.0;
			row[4] = 0.0;
			Accumulate(normal, rhs, row, bladeNormalDragCoefficient, DragPriorWeight);
			Solve(normal, rhs, _fit);
		}

		private static void Accumulate(double[,] normal, double[] rhs, double[] row, double target, double weight)
		{
			var w2 = weight * weight;
			for (var i = 0; i < 5; i++)
			{
				rhs[i] += w2 * row[i] * target;
				for (var k = 0; k < 5; k++)
				{
					normal[i, k] += w2 * row[i] * row[k];
				}
			}
		}

		// Gaussian elimination with partial pivoting; deterministic and small.
		private static void Solve(double[,] a, double[] b, double[] x)
		{
			const int n = 5;
			for (var col = 0; col < n; col++)
			{
				var pivot = col;
				for (var r = col + 1; r < n; r++)
				{
					if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col]))
					{
						pivot = r;
					}
				}

				if (pivot != col)
				{
					for (var c = 0; c < n; c++)
					{
						var swap = a[col, c];
						a[col, c] = a[pivot, c];
						a[pivot, c] = swap;
					}

					var swapB = b[col];
					b[col] = b[pivot];
					b[pivot] = swapB;
				}

				for (var r = col + 1; r < n; r++)
				{
					var factor = a[r, col] / a[col, col];
					for (var c = col; c < n; c++)
					{
						a[r, c] -= factor * a[col, c];
					}

					b[r] -= factor * b[col];
				}
			}

			for (var r = n - 1; r >= 0; r--)
			{
				var sum = b[r];
				for (var c = r + 1; c < n; c++)
				{
					sum -= a[r, c] * x[c];
				}

				x[r] = sum / a[r, r];
			}
		}

		private void BuildTables()
		{
			EvaluateLiftDragModel(0.0, out var modelThrustAtZero, out var modelTorqueAtZero);
			EvaluateLiftDragModel(ZeroThrustAngle, out var modelThrustAtEnd, out var modelTorqueAtEnd);
			var seriesThrustAtZero = OpenWaterToFourQuadrant(WageningenBSeries.ThrustCoefficient(0.0, PitchRatio, AreaRatio, Blades), 0.0);
			var seriesTorqueAtZero = OpenWaterToFourQuadrant(WageningenBSeries.TorqueCoefficient(0.0, PitchRatio, AreaRatio, Blades), 0.0);
			var seriesTorqueAtEnd = OpenWaterToFourQuadrant(
				WageningenBSeries.TorqueCoefficient(ZeroThrustAdvanceRatio, PitchRatio, AreaRatio, Blades), ZeroThrustAdvanceRatio);
			var bollardThrustRatio = seriesThrustAtZero / modelThrustAtZero;
			var bollardTorqueRatio = seriesTorqueAtZero / modelTorqueAtZero;

			for (var k = 0; k < TableSize; k++)
			{
				var beta = -Math.PI + k * TableStep;
				EvaluateLiftDragModel(beta, out var thrust, out var torque);
				var fromAstern = Math.PI - Math.Abs(beta);
				if (fromAstern < AsternWindow)
				{
					var weight = 0.5 * (1.0 + Math.Cos(Math.PI * fromAstern / AsternWindow));
					thrust *= 1.0 + (bollardThrustRatio - 1.0) * weight;
					torque *= 1.0 + (bollardTorqueRatio - 1.0) * weight;
				}

				if (beta >= 0.0 && beta <= ZeroThrustAngle)
				{
					var j = 0.7 * Math.PI * Math.Tan(beta);
					thrust = OpenWaterToFourQuadrant(WageningenBSeries.ThrustCoefficient(j, PitchRatio, AreaRatio, Blades), j);
					torque = OpenWaterToFourQuadrant(WageningenBSeries.TorqueCoefficient(j, PitchRatio, AreaRatio, Blades), j);
				}
				else if (beta > ZeroThrustAngle && beta <= ZeroThrustAngle + BlendWidth)
				{
					var weight = 1.0 - (beta - ZeroThrustAngle) / BlendWidth;
					thrust += (0.0 - modelThrustAtEnd) * weight;
					torque += (seriesTorqueAtEnd - modelTorqueAtEnd) * weight;
				}
				else if (beta < 0.0 && beta >= -BlendWidth)
				{
					var weight = 1.0 + beta / BlendWidth;
					thrust += (seriesThrustAtZero - modelThrustAtZero) * weight;
					torque += (seriesTorqueAtZero - modelTorqueAtZero) * weight;
				}

				_thrust[k] = thrust;
				_torque[k] = torque;
			}
		}
	}
}
