namespace FerrySim.Sim
{
	/// <summary>Why a run has stopped stepping on its own, apart from the user's pause.</summary>
	public enum RunHold
	{
		/// <summary>The run steps normally.</summary>
		None = 0,

		/// <summary>The vessel left the water the run has depth for; the environment readout's depth misses rose.</summary>
		OutsideCoveredWater = 1,

		/// <summary>The run went past the day's tide, current or wind data; the environment readout's time holds rose.</summary>
		BeyondDayData = 2,
	}

	/// <summary>
	/// The runner's own holds (decision 0005, Coverage and day end). A runner that implements it stops stepping rather
	/// than continue on fallback data, and keeps the hold until a new start; the HUD explains it. UI tests use a fake.
	/// </summary>
	public interface IRunHoldSource
	{
		/// <summary>The current hold; <see cref="RunHold.None"/> while the run steps normally or when there is no run.</summary>
		RunHold Hold { get; }
	}
}
