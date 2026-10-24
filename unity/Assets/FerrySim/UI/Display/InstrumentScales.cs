using System;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	/// <summary>
	/// Dial ranges and markings for one vessel, taken from its parameter file. Anything the file does
	/// not state (or states in an unexpected unit) falls back to a generic scale with no marking, so the
	/// panel never shows a redline or hard-over mark that nobody published.
	/// </summary>
	public sealed class InstrumentScales
	{
		public const double GenericTachMaxRpm = 3000.0;
		public const double MinimumRudderScaleDegrees = 45.0;
		public const string UnknownVesselName = "Unknown vessel";

		private InstrumentScales(string vesselName, double ratedRpm, double hardOverDegrees, double clutchInLever)
		{
			VesselName = vesselName;
			RatedRpm = ratedRpm;
			TachMaxRpm = TachMaxFor(ratedRpm);
			HardOverDegrees = hardOverDegrees;
			RudderScaleDegrees = RudderScaleFor(hardOverDegrees);
			ClutchInLever = clutchInLever;
		}

		public static InstrumentScales Generic { get; } =
			new InstrumentScales(UnknownVesselName, double.NaN, double.NaN, double.NaN);

		public string VesselName { get; }

		/// <summary>Full scale of the tachometers, rpm.</summary>
		public double TachMaxRpm { get; }

		/// <summary>Rated engine speed, rpm, where the redline starts; NaN when unknown.</summary>
		public double RatedRpm { get; }

		public bool HasRedline => InstrumentFormat.IsFinite(RatedRpm);

		/// <summary>The rudder indicator spans this many degrees each side of midships.</summary>
		public double RudderScaleDegrees { get; }

		/// <summary>Hard-over rudder angle, degrees; NaN when unknown (no hard-over marks).</summary>
		public double HardOverDegrees { get; }

		/// <summary>Lever position where the clutch engages, in [0, 1]; NaN when unknown (no detent marks).</summary>
		public double ClutchInLever { get; }

		public static InstrumentScales From(VesselParameters parameters)
		{
			if (parameters == null)
			{
				return Generic;
			}

			var name = !string.IsNullOrWhiteSpace(parameters.DisplayName) ? parameters.DisplayName :
				!string.IsNullOrWhiteSpace(parameters.Id) ? parameters.Id : UnknownVesselName;
			var ratedRpm = Read(parameters.Machinery?.RatedEngineSpeed, "rad/s", DisplayUnits.Rpm);
			var hardOver = Read(parameters.Controls?.HardOverAngle, "rad", DisplayUnits.Degrees);
			var clutchIn = Read(parameters.Controls?.ClutchInLever, "1", value => value);
			if (clutchIn <= 0.0 || clutchIn >= 1.0)
			{
				clutchIn = double.NaN;
			}

			return new InstrumentScales(name, ratedRpm, hardOver, clutchIn);
		}

		/// <summary>The next multiple of 500 rpm at or above 110% of the rated speed; the generic scale when unknown.</summary>
		public static double TachMaxFor(double ratedRpm) =>
			InstrumentFormat.IsFinite(ratedRpm) && ratedRpm > 0.0
				? Math.Ceiling(ratedRpm * 1.1 / 500.0) * 500.0
				: GenericTachMaxRpm;

		/// <summary>At least 45 degrees each side, and at least 5 degrees beyond hard over, in steps of 5.</summary>
		public static double RudderScaleFor(double hardOverDegrees) =>
			InstrumentFormat.IsFinite(hardOverDegrees) && hardOverDegrees > 0.0
				? Math.Max(MinimumRudderScaleDegrees, Math.Ceiling((hardOverDegrees + 5.0) / 5.0) * 5.0)
				: MinimumRudderScaleDegrees;

		private static double Read(ParameterValue value, string unit, Func<double, double> toDisplay)
		{
			if (value == null || value.Unit != unit || !InstrumentFormat.IsFinite(value.Value) || value.Value <= 0.0)
			{
				return double.NaN;
			}

			return toDisplay(value.Value);
		}
	}
}
