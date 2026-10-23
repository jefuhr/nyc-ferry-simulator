using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Properties of the water and air the model assumes. They belong to the environment, but
	/// <see cref="IEnvironment"/> does not provide them yet, so they are fixed here.
	/// </summary>
	public static class PhysicalConstants
	{
		/// <summary>Standard gravity, m/s².</summary>
		public const double Gravity = 9.80665;

		/// <summary>
		/// Water density, kg/m³. New York Harbor is brackish (roughly 15 to 26 PSU in the Upper Bay
		/// and East River), so this sits between fresh water and the 1025 kg/m³ of open seawater.
		/// </summary>
		public const double WaterDensity = 1020.0;

		/// <summary>Kinematic viscosity of water at about 15 °C, m²/s (ITTC 7.5-02-01-03).</summary>
		public const double WaterKinematicViscosity = 1.19e-6;

		/// <summary>Air density at sea level, 15 °C (ISA), kg/m³.</summary>
		public const double AirDensity = 1.225;
	}

	/// <summary>Reads required parameters and rejects missing or non-physical values with the JSON path.</summary>
	internal static class Required
	{
		public static T Section<T>(T section, string path) where T : class =>
			section ?? throw new ArgumentException($"Vessel parameter section '{path}' is required by the dynamics model.");

		public static double Value(ParameterValue value, string path)
		{
			if (value == null)
			{
				throw new ArgumentException($"Vessel parameter '{path}' is required by the dynamics model.");
			}

			if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
			{
				throw new ArgumentException($"Vessel parameter '{path}' must be finite.");
			}

			return value.Value;
		}

		public static double Positive(ParameterValue value, string path)
		{
			var number = Value(value, path);
			if (!(number > 0.0))
			{
				throw new ArgumentException($"Vessel parameter '{path}' must be positive, found {number}.");
			}

			return number;
		}

		public static double NonNegative(ParameterValue value, string path)
		{
			var number = Value(value, path);
			if (number < 0.0)
			{
				throw new ArgumentException($"Vessel parameter '{path}' must not be negative, found {number}.");
			}

			return number;
		}

		public static double InRange(ParameterValue value, string path, double min, double max)
		{
			var number = Value(value, path);
			if (number < min || number > max)
			{
				throw new ArgumentException($"Vessel parameter '{path}' must be between {min} and {max}, found {number}.");
			}

			return number;
		}

		public static string Text(ParameterText text, string path, params string[] allowed)
		{
			if (text == null || string.IsNullOrWhiteSpace(text.Value))
			{
				throw new ArgumentException($"Vessel parameter '{path}' is required by the dynamics model.");
			}

			if (Array.IndexOf(allowed, text.Value) < 0)
			{
				throw new ArgumentException($"Vessel parameter '{path}' must be one of {string.Join(", ", allowed)}, found '{text.Value}'.");
			}

			return text.Value;
		}
	}
}
