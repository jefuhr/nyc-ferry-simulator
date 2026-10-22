using System;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// The water and air around the vessel. <c>time</c> is simulation time in seconds since scenario
	/// start (the same clock as <see cref="VesselState.Time"/>); implementations map it to UTC with
	/// their own scenario epoch. Implementations must return finite values everywhere, using a
	/// documented conservative value outside their data coverage.
	/// </summary>
	public interface IEnvironment
	{
		/// <summary>Surface current velocity (direction the water moves toward), m/s.</summary>
		HorizontalVector SampleCurrent(double north, double east, double time);

		/// <summary>True wind velocity (direction the air moves toward) at 10 m height, m/s.</summary>
		HorizontalVector SampleWind(double north, double east, double time);

		/// <summary>
		/// Water depth from the instantaneous surface (including tide) to the seabed, metres, positive.
		/// Zero or negative means dry land.
		/// </summary>
		double SampleDepth(double north, double east, double time);
	}

	/// <summary>The same current, wind and depth everywhere and at all times. For tests and calm-water trials.</summary>
	public sealed class UniformEnvironment : IEnvironment
	{
		public UniformEnvironment(HorizontalVector current, HorizontalVector wind, double depth)
		{
			if (!IsFinite(current.North) || !IsFinite(current.East) || !IsFinite(wind.North) || !IsFinite(wind.East) || !IsFinite(depth))
			{
				throw new ArgumentException("Uniform environment values must be finite.");
			}

			Current = current;
			Wind = wind;
			Depth = depth;
		}

		/// <summary>Still water and still air, 50 m deep: effectively unrestricted water for a 1.6 m draft.</summary>
		public static UniformEnvironment Calm => new UniformEnvironment(HorizontalVector.Zero, HorizontalVector.Zero, 50.0);

		public HorizontalVector Current { get; }

		public HorizontalVector Wind { get; }

		public double Depth { get; }

		public HorizontalVector SampleCurrent(double north, double east, double time) => Current;

		public HorizontalVector SampleWind(double north, double east, double time) => Wind;

		public double SampleDepth(double north, double east, double time) => Depth;

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
