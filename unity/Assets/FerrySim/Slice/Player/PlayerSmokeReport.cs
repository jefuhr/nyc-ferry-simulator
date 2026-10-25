using System;

namespace FerrySim.Slice
{
	/// <summary>
	/// What a smoke run (<see cref="PlayerSmokeRun"/>) found, written as smoke.json in its folder. <see cref="passed"/> is
	/// true only when <see cref="failures"/> is empty; the player's exit code says the same. Field names are the file's.
	/// </summary>
	[Serializable]
	public sealed class PlayerSmokeReport
	{
		public bool passed;
		public string[] failures = Array.Empty<string>();

		public string product;
		public string version;

		/// <summary>The build record's description (<see cref="PlayerBuildInfo.Describe"/>), or "editor".</summary>
		public string build;

		public string unity_version;
		public string platform;
		public string operating_system;
		public string processor;
		public string graphics_device;
		public string streaming_assets;
		public string persistent_data;

		/// <summary>Where the scenario's vessel file resolved, and whether that is inside StreamingAssets.</summary>
		public string vessel_file;

		public bool vessel_file_packaged;

		/// <summary>The drive's water and air: calm, uniform or pack.</summary>
		public string environment;

		/// <summary>The environment pack the drive ran on (region/date) and its day digest; empty unless a pack run.</summary>
		public string environment_pack;

		public string environment_digest;

		/// <summary>The pack's day folder, and whether that is inside StreamingAssets.</summary>
		public string environment_folder;

		public bool environment_packaged;

		/// <summary>The pack's water level above NAVD88 at the vessel when the drive ended, m; 0 unless a pack run.</summary>
		public double water_level_navd88_m;

		/// <summary>The drawn water's level when the drive ended, m above NAVD88; 0 when the scene has no drawn water.</summary>
		public double drawn_water_level_navd88_m;

		public string player_error;
		public string landing_error;
		public int landing_segments;
		public string start_refusal;
		public double neutral_simulated_s;
		public double drive_simulated_s;
		public double drive_lever;
		public double displacement_m;
		public double max_speed_over_ground_mps;
		public bool state_finite;
		public string recording;
		public long recording_bytes;
		public string recording_error;
		public string[] screenshots = Array.Empty<string>();

		/// <summary>The camera view each screenshot shows, in the same order.</summary>
		public string[] screenshot_views = Array.Empty<string>();
		public int min_screenshot_colours;
		public int frames;
		public double wall_s;
		public double average_fps;
		public int errors;
		public int warnings;

		/// <summary>The first errors, exceptions and asserts logged while the run watched, with their type.</summary>
		public string[] error_messages = Array.Empty<string>();
	}
}
