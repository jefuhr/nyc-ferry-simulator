using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// The surface current over a region: what <see cref="DataEnvironment.SampleCurrent"/> returns. Pluggable, so the
	/// method that turns station data into a field can change without a format change. Implementations must be
	/// deterministic, finite everywhere and allocation-free per sample.
	/// </summary>
	public interface ICurrentField
	{
		/// <summary>
		/// Velocity of the water (the direction it flows toward) at a world position, m/s, at a pack time: seconds since
		/// the pack's epoch, already held inside its coverage window by the caller.
		/// </summary>
		HorizontalVector Sample(double north, double east, double packTime);
	}
}
