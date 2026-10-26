using System;
using System.Collections.Generic;
using System.Linq;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// What a probe run (<see cref="TilesetProbe"/>) found, written as probe.json. <see cref="passed"/> is true only when
	/// <see cref="failures"/> is empty (<see cref="Verdict"/>); the player's exit code says the same. Field names are the
	/// file's.
	/// </summary>
	[Serializable]
	public sealed class ProbeReport
	{
		/// <summary>A rendered view of the harbor has thousands of colours; a blank frame a handful.</summary>
		public const int MinimumScreenshotColours = 50;

		public bool passed;
		public string[] failures = Array.Empty<string>();

		public int probe_version = 1;
		public string product;
		public string unity_version;
		public string platform;
		public string operating_system;
		public string processor;
		public int system_memory_mb;
		public string graphics_device;
		public string graphics_api;
		public bool development_build;

		/// <summary>The commit and dirty flag from build-info.json in StreamingAssets, or "editor".</summary>
		public string build;

		public string streaming_assets;
		public string tileset_path;

		/// <summary>The URL the tileset was loaded from (<see cref="TilesetUrl"/>), and what System.Uri makes of the same path.</summary>
		public string tileset_url;
		public string tileset_url_system_uri;

		/// <summary>The packaged tiles (harbor3d/payload.json): which scope, how many tiles and files, and any that are missing or differ.</summary>
		public string payload_scope;
		public int payload_tiles;
		public int payload_files;
		public long payload_bytes;
		public int payload_missing;
		public int payload_mismatched;
		public string[] payload_problems = Array.Empty<string>();

		public long maximum_cached_bytes;

		/// <summary>Tile contents Cesium asked for: loaded, failed, and still queued when the run ended.</summary>
		public int tiles_requested;

		/// <summary>Distinct tile contents Cesium loaded and created objects for, and how many of them at each level.</summary>
		public int tiles_loaded;
		public int tiles_loaded_l0;
		public int tiles_loaded_l1;
		public int tiles_loaded_l2;

		/// <summary>Tile loads Cesium reported failing in its log, and tileset load failures it raised.</summary>
		public int tiles_failed;
		public int tileset_load_failures;

		/// <summary>From Cesium's last selection statistics: tiles resident, still loading, and rendered.</summary>
		public int tiles_resident_final;
		public int tiles_loading_final;
		public int tiles_rendered_final;
		public string selection_stats_final;

		/// <summary>A few of the URLs Cesium created tiles from, which show how it resolved the relative ones.</summary>
		public string[] loaded_examples = Array.Empty<string>();

		/// <summary>Everything Cesium logged as an error or warning, and its tileset load failures.</summary>
		public string[] cesium_errors = Array.Empty<string>();

		public float load_progress_final;
		public double first_view_s;
		public bool first_view_loaded;
		public double flight_s;
		public double flight_m;
		public double final_wait_s;
		public bool final_view_loaded;
		public double wait_timeout_s;

		/// <summary>Frame times while the camera flew (vSync off, no frame-rate cap): count and percentiles, milliseconds.</summary>
		public int frames;
		public double frame_ms_p50;
		public double frame_ms_p95;
		public double frame_ms_p99;
		public double frame_ms_max;

		/// <summary>Unity's "System Used Memory" (the process footprint, cesium-native included), and the OS's resident set if readable; 0 if not.</summary>
		public long system_used_memory_peak_bytes;
		public long system_used_memory_final_bytes;
		public long resident_memory_bytes;
		public string memory_note;

		public string[] screenshots = Array.Empty<string>();
		public int[] screenshot_colours = Array.Empty<int>();

		public int errors;
		public int warnings;
		public string[] error_messages = Array.Empty<string>();
		public double wall_s;

		/// <summary>Every reason the run failed: empty when it passed.</summary>
		public static string[] Verdict(ProbeReport report)
		{
			var failures = new List<string>();
			if (report.tileset_url == null || !report.tileset_url.StartsWith("file:///", StringComparison.Ordinal))
			{
				failures.Add($"the tileset URL is not a local file:// URL ({report.tileset_url ?? "none"})");
			}

			if (report.payload_files == 0)
			{
				failures.Add("no packaged tiles were found in StreamingAssets");
			}

			if (report.payload_missing > 0 || report.payload_mismatched > 0)
			{
				failures.Add($"{report.payload_missing} packaged files are missing and {report.payload_mismatched} differ from payload.json");
			}

			if (report.tiles_loaded == 0)
			{
				failures.Add("Cesium loaded no tile");
			}

			if (report.tileset_load_failures > 0 || report.tiles_failed > 0)
			{
				failures.Add($"Cesium reported {report.tileset_load_failures} tileset load failures and {report.tiles_failed} failed tile loads");
			}

			if (report.cesium_errors.Length > 0)
			{
				failures.Add($"Cesium logged {report.cesium_errors.Length} errors or warnings");
			}

			if (!report.first_view_loaded)
			{
				failures.Add($"the first view was not fully loaded within {report.wait_timeout_s:0} s");
			}

			if (!report.final_view_loaded)
			{
				failures.Add($"the view after the flight was not fully loaded within {report.wait_timeout_s:0} s");
			}

			if (report.screenshots.Length == 0)
			{
				failures.Add("no screenshot was taken");
			}

			foreach (var (shot, colours) in report.screenshots.Zip(report.screenshot_colours, (s, c) => (s, c)))
			{
				if (colours <= MinimumScreenshotColours)
				{
					failures.Add($"{shot} has {colours} colours: nothing was drawn");
				}
			}

			if (report.errors > 0)
			{
				failures.Add($"{report.errors} errors were logged");
			}

			return failures.ToArray();
		}

		/// <summary>The value at <paramref name="fraction"/> (0 to 1) of sorted values, by the nearest rank; 0 for none.</summary>
		public static double Percentile(IReadOnlyList<double> sorted, double fraction)
		{
			if (sorted.Count == 0)
			{
				return 0.0;
			}

			var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
			return sorted[Math.Max(0, Math.Min(sorted.Count - 1, rank))];
		}
	}
}
