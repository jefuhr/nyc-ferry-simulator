using System;
using System.IO;
using System.Text;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// A tiny pack written by an independent Python implementation of the ferrysim-env/1 layout and its digests (as
	/// revised 2026-10-25): <c>json.dumps(indent=2)</c> manifests, <c>repr</c> numbers, <c>struct.pack("&lt;f")</c> grid
	/// values, <c>hashlib.sha256</c> digests, station positions from the same ECEF maths as ferrydata.geodesy. The C#
	/// reader must load it and agree with Python's digests. Made-up values, not NOAA data. The generator is
	/// <c>python_pack.py</c> beside this file: <c>python3 python_pack.py &lt;out-dir&gt;</c> writes the pack and prints both digests.
	/// </summary>
	public static class PythonPackFixture
	{
		/// <summary>Python's depth digest of the files below.</summary>
		public const string DepthDigest = "412f47bcf8aba460c8a4d94bdd63e7527e7c8fd1d99d96c244b1db97598a4533";

		/// <summary>Python's day digest of the files below.</summary>
		public const string DayDigest = "08eed0719c207f291fd0df6dd3933e47bd4fcb8395d97d79a10cbccbbbe5b488";

		private static readonly string[] DepthManifest =
		{
			"{",
			"  \"format\": \"ferrysim-env-depth/1\",",
			"  \"region\": \"lower-east-river\",",
			"  \"datum\": \"NAVD88\",",
			"  \"value\": \"bed elevation, metres, positive up (water is negative)\",",
			"  \"grids\": [",
			"    {",
			"      \"id\": \"slice\",",
			"      \"file\": \"slice.f32\",",
			"      \"origin_north_m\": -20.0,",
			"      \"origin_east_m\": -10.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 2,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"65170a59387ec136e05316ec28c43c97c57681a600fecb5c0214e13f7e262094\"",
			"    }",
			"  ],",
			"  \"outside_depth_m\": 20.0,",
			"  \"sources\": [",
			"    {",
			"      \"name\": \"depth_merged_navd88.tif\",",
			"      \"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\",",
			"      \"note\": \"synthetic golden vector\"",
			"    }",
			"  ]",
			"}",
		};

		private static readonly string[] DayManifest =
		{
			"{",
			"  \"format\": \"ferrysim-env/1\",",
			"  \"region\": \"lower-east-river\",",
			"  \"date\": \"2025-07-14\",",
			"  \"epoch_utc\": \"2025-07-14T04:00:00Z\",",
			"  \"time_coverage_s\": [",
			"    -3600,",
			"    90000",
			"  ],",
			"  \"depth_digest\": \"412f47bcf8aba460c8a4d94bdd63e7527e7c8fd1d99d96c244b1db97598a4533\",",
			"  \"current_stations\": [",
			"    {",
			"      \"id\": \"NYH1920\",",
			"      \"name\": \"Brooklyn Bridge\",",
			"      \"kind\": \"harmonic-prediction\",",
			"      \"lat\": 40.7063,",
			"      \"lon\": -73.9971,",
			"      \"north_m\": 699.75758330211,",
			"      \"east_m\": 1512.685028260301,",
			"      \"bin_depth_m\": 1.5,",
			"      \"source\": \"NYH1920_2025-07-14.json\",",
			"      \"source_sha256\": \"1111111111111111111111111111111111111111111111111111111111111111\"",
			"    }",
			"  ],",
			"  \"water_level_stations\": [",
			"    {",
			"      \"id\": \"8518750\",",
			"      \"name\": \"The Battery\",",
			"      \"lat\": 40.7006,",
			"      \"lon\": -74.0142,",
			"      \"north_m\": 66.62917947869778,",
			"      \"east_m\": 67.61179952462828,",
			"      \"source\": \"8518750_water_level.json\",",
			"      \"source_sha256\": \"2222222222222222222222222222222222222222222222222222222222222222\"",
			"    }",
			"  ],",
			"  \"wind_stations\": [",
			"    {",
			"      \"id\": \"8518750\",",
			"      \"name\": \"The Battery\",",
			"      \"lat\": 40.7006,",
			"      \"lon\": -74.0142,",
			"      \"north_m\": 66.62917947869778,",
			"      \"east_m\": 67.61179952462828,",
			"      \"sensor_height_m\": null,",
			"      \"source\": \"8518750_wind.json\",",
			"      \"source_sha256\": \"3333333333333333333333333333333333333333333333333333333333333333\"",
			"    }",
			"  ],",
			"  \"files\": {",
			"    \"currents.csv\": \"ef0dc1a484ea9e1a2e02e4e74c79145d335eb679cd11552c141d99cce3bcc9ce\",",
			"    \"water_levels.csv\": \"5415acc05e30af3b888c9ed3605d26c3757120eda44ff45bc54f49551ba923dc\",",
			"    \"wind.csv\": \"5b1b73a2ccb0b14e66e223682fcc1414c4e8d31a2b53ca0d56347bc42ac3b009\"",
			"  },",
			"  \"notes\": [",
			"    \"water level 8518750: observations missing 00:30-01:00 local, filled with predictions\"",
			"  ]",
			"}",
		};

		private static readonly string[] Currents =
		{
			"station_id,t_s,north_mps,east_mps",
			"NYH1920,-3600,0.5,-0.25",
			"NYH1920,0,0.75,-0.125",
			"NYH1920,360,1e-05,0.3",
			"NYH1920,90000,-0.5,0.25",
		};

		private static readonly string[] WaterLevels =
		{
			"station_id,t_s,level_navd88_m,source",
			"8518750,-3600.0,0.1,observed",
			"8518750,1800.0,0.4,predicted",
			"8518750,90000.0,-0.3,observed",
		};

		private static readonly string[] Wind =
		{
			"station_id,t_s,north_mps,east_mps,gust_mps",
			"8518750,-3600,-3.0,4.0,",
			"8518750,90000,1.0,2.0,7.5",
		};

		private const string SliceF32 = "000020c1000018c10000c07f0000a03f000040c000000040";
		/// <summary>Writes the pack under <paramref name="root"/> and returns its day folder.</summary>
		public static string Write(string root)
		{
			var env = Path.Combine(root, "lower-east-river", "env");
			var depth = Path.Combine(env, "depth");
			var day = Path.Combine(env, "days", "2025-07-14");
			Directory.CreateDirectory(depth);
			Directory.CreateDirectory(day);
			File.WriteAllBytes(Path.Combine(depth, "manifest.json"), Text(DepthManifest));
			File.WriteAllBytes(Path.Combine(depth, "slice.f32"), Hex(SliceF32));
			File.WriteAllBytes(Path.Combine(depth, "DIGEST"), Encoding.ASCII.GetBytes(DepthDigest + "\n"));
			File.WriteAllBytes(Path.Combine(day, "manifest.json"), Text(DayManifest));
			File.WriteAllBytes(Path.Combine(day, "currents.csv"), Text(Currents));
			File.WriteAllBytes(Path.Combine(day, "water_levels.csv"), Text(WaterLevels));
			File.WriteAllBytes(Path.Combine(day, "wind.csv"), Text(Wind));
			File.WriteAllBytes(Path.Combine(day, "DIGEST"), Encoding.ASCII.GetBytes(DayDigest + "\n"));
			return day;
		}

		private static byte[] Text(string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

		private static byte[] Hex(string hex)
		{
			var bytes = new byte[hex.Length / 2];
			for (var i = 0; i < bytes.Length; i++)
			{
				bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
			}

			return bytes;
		}
	}
}
