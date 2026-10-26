namespace FerrySim.Sim
{
	/// <summary>
	/// The light the run is in, as the wheelhouse needs it: whether the panel should show its night palette. The world's
	/// sky implements it (World/Sky/SunController, on the run's clock), and the wheelhouse UI finds it in the scene like its
	/// other sources. A scene without a sky has none, and its panel keeps whatever palette it was given.
	/// </summary>
	public interface IDaylightSource
	{
		/// <summary>
		/// True from the end of civil twilight in the evening to its start before dawn, the sun's centre more than 6°
		/// below the horizon (World/Sky/Daylight.cs): dark enough that a bright panel spoils night vision.
		/// </summary>
		bool IsNight { get; }
	}
}
