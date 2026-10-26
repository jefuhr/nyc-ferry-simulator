namespace FerrySim.World
{
	/// <summary>
	/// The sun-altitude rules for lights and for the panel, on the sun's geometric altitude (<see cref="SolarPosition.ElevationDegrees"/>):
	/// <list type="bullet">
	/// <item>Navigation lights show from sunset to sunrise (Inland Rule 20(b)), with sunset and sunrise where
	/// <see cref="SolarDay"/> puts them, the sun's centre 0.833° below the horizon, and also by day in restricted
	/// visibility (Rule 20(c)). Bridge lights follow the same rule (33 CFR 118.55(a): "from sunset to sunrise and at other
	/// times when the visibility is less than one mile").</item>
	/// <item>Aids to navigation show from sunset to sunrise: their daylight controls, approximated by the sun.</item>
	/// <item>The panel's night palette from the end of civil twilight, the sun 6° below the horizon, to its start before
	/// dawn: dark enough that a bright panel spoils night vision.</item>
	/// </list>
	/// </summary>
	public static class Daylight
	{
		/// <summary>Sunrise and sunset: geometric altitude of the sun's centre, degrees (NOAA, zenith 90.833°).</summary>
		public const double SunsetElevationDegrees = -0.833;

		/// <summary>Civil twilight's end: the panel's night palette below this altitude, degrees.</summary>
		public const double NightPaletteBelowDegrees = -6.0;

		/// <summary>True from sunset to sunrise.</summary>
		public static bool IsBetweenSunsetAndSunrise(in SolarPosition sun) => sun.ElevationDegrees < SunsetElevationDegrees;

		/// <summary>Rule 20(b) and (c): from sunset to sunrise, and in restricted visibility.</summary>
		public static bool ShowsNavigationLights(in SolarPosition sun, double visibilityMetres) =>
			IsBetweenSunsetAndSunrise(sun) || MeteorologicalVisibility.IsRestricted(visibilityMetres);

		/// <summary>An aid's daylight control: lit from sunset to sunrise.</summary>
		public static bool ShowsAidLights(in SolarPosition sun) => IsBetweenSunsetAndSunrise(sun);

		/// <summary>The panel's night palette, after civil twilight.</summary>
		public static bool PrefersNightPalette(in SolarPosition sun) => sun.ElevationDegrees < NightPaletteBelowDegrees;
	}
}
