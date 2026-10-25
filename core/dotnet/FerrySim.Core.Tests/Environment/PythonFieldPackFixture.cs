using System;
using System.IO;
using System.Text;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// A small pack with a current field, written by an independent Python implementation of the ferrysim-env/1 current-field
	/// extension (approved 2026-10-25): <c>json.dumps(indent=2)</c> manifests, <c>struct.pack("&lt;f")</c> field values,
	/// raw slot bytes, <c>hashlib.sha256</c> digests (the day digest with the field digest appended), and golden samples of the
	/// sampling rule from that implementation. The C# reader must load it with Python's digests and ChannelCurrentField must
	/// reproduce the samples. Made-up values, not NOAA data. The generator is <c>python_field_pack.py</c> beside this file:
	/// <c>python3 python_field_pack.py &lt;out-dir&gt;</c> writes the pack and prints the digests and the samples.
	/// </summary>
	public static class PythonFieldPackFixture
	{
		/// <summary>Largest difference from Python's golden samples, m/s: both sides do the same double arithmetic on the same float32 values.</summary>
		public const double Tolerance = 1e-12;

		public const string DepthDigest = "8ab628e98fb5e24e42115b721608f82b239f40eb5324415a65b9fe44d40bec97";

		public const string FieldDigest = "ba4e1c9ea4c5316566f13c07f03cecea046a3a3fecb04845b349c85c54e6988f";

		public const string DayDigest = "fc53618b5721d688e84c1f887bc4207f954b3eefb48b9e7eb4429caf48b1aeb5";

		/// <summary>A point, a pack time and the velocity Python's sampling rule gives there (north, east, m/s).</summary>
		public sealed class Sample
		{
			public Sample(string label, double north, double east, double packTime, double velocityNorth, double velocityEast)
			{
				Label = label;
				North = north;
				East = east;
				PackTime = packTime;
				VelocityNorth = velocityNorth;
				VelocityEast = velocityEast;
			}

			public string Label { get; }

			public double North { get; }

			public double East { get; }

			public double PackTime { get; }

			public double VelocityNorth { get; }

			public double VelocityEast { get; }

			public override string ToString() => Label;
		}

		/// <summary>Mid-channel, at and next to stations, near a shore, on land, across a cell boundary and outside the grid.</summary>
		public static readonly Sample[] Samples =
		{
			new Sample("mid-channel", 5.0, 7.0, 180.0, 0.9199733133036236, 0.5990419746255757),
			new Sample("at station A", -11.104811271306076, 1.1200973482061727E-10, 0.0, 1.0, 0.39999999999999997),
			new Sample("next to station A", -8.104811271306076, -1.9999999998879903, 180.0, 1.1244132696577223, 0.44868058908615227),
			new Sample("at station B (cross factor 0 on two corners)", 27.762059079348532, 21.128797941312868, 360.0, 0.22717655252684202, 1.0816278703295517),
			new Sample("near the shore, wet share 0.65", 27.0, 47.0, 360.0, 0.5379339824863804, 0.7900490415295685),
			new Sample("near the shore, wet share 0.4", 27.0, 52.0, 360.0, 0.43034718598910426, 0.6320392332236546),
			new Sample("on a land centre", 40.0, 60.0, 360.0, 0.0, 0.0),
			new Sample("just west of a cell boundary", 13.0, 19.999, 540.0, 0.0510475911286148, 0.14970167392772815),
			new Sample("on a cell boundary", 13.0, 20.0, 540.0, 0.051041957170809164, 0.1497022955097831),
			new Sample("just east of a cell boundary", 13.0, 20.001, 540.0, 0.051042565139477455, 0.14969878699372496),
			new Sample("outside the grid, north", 40.001, 10.0, 540.0, -0.01943493193603338, 0.14617637027188446),
			new Sample("outside the grid, far", 100.0, 150.0, 360.0, 0.6905718216818271, 0.17582998713700515),
			new Sample("at station C, outside the grid", 133.25967404346818, 169.02798211318012, 720.0, 0.1, 0.4),
			new Sample("on a cell whose first two slots are empty", 20.0, 40.0, 180.0, 0.557368084988209, 0.767151373846864),
			new Sample("on a cell with an empty slot between two stations", 20.0, -20.0, 540.0, 0.13972930941909634, 0.13266999954370068),
		};

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
			"      \"origin_north_m\": -50.0,",
			"      \"origin_east_m\": -50.0,",
			"      \"cell_m\": 50.0,",
			"      \"rows\": 2,",
			"      \"cols\": 2,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"d4d3c190d35408bacfc62503cef122195831b0e98e9ec30c056cfb77edc469fd\"",
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

		private const string DepthGrid = "000040c1000030c1000020c1000010c1";

		private static readonly string[] FieldManifest =
		{
			"{",
			"  \"format\": \"ferrysim-env-field/1\",",
			"  \"region\": \"lower-east-river\",",
			"  \"method\": \"channel-harmonic/1\",",
			"  \"stations\": [",
			"    {",
			"      \"id\": \"A\",",
			"      \"north_m\": -11.104811271306076,",
			"      \"east_m\": 1.1200973482061727e-10,",
			"      \"frame_north\": 0.8632549450656356,",
			"      \"frame_east\": 0.502903018597249",
			"    },",
			"    {",
			"      \"id\": \"B\",",
			"      \"north_m\": 27.762059079348532,",
			"      \"east_m\": 21.128797941312868,",
			"      \"frame_north\": 0.6631063181620679,",
			"      \"frame_east\": 0.7466244078891531",
			"    },",
			"    {",
			"      \"id\": \"C\",",
			"      \"north_m\": 133.25967404346818,",
			"      \"east_m\": 169.02798211318012,",
			"      \"frame_north\": 0.6,",
			"      \"frame_east\": 0.8",
			"    }",
			"  ],",
			"  \"grids\": [",
			"    {",
			"      \"id\": \"slice\",",
			"      \"values_file\": \"slice.f32\",",
			"      \"stations_file\": \"slice.u8\",",
			"      \"origin_north_m\": -20.0,",
			"      \"origin_east_m\": -20.0,",
			"      \"cell_m\": 20.0,",
			"      \"rows\": 4,",
			"      \"cols\": 5,",
			"      \"slots\": 3,",
			"      \"values_sha256\": \"4dd15fd85f29c51ddd5a7db058a8e1fd1e08092de0ad34b9e68011b542f0a730\",",
			"      \"stations_sha256\": \"7d9f7162693df7b5930129e2fb1356617ddd65976344a9386d856ffe13cb2249\"",
			"    }",
			"  ],",
			"  \"parameters\": {",
			"    \"cell_m\": 20.0,",
			"    \"computation_north_m\": [",
			"      -20.0,",
			"      40.0",
			"    ],",
			"    \"computation_east_m\": [",
			"      -20.0,",
			"      60.0",
			"    ],",
			"    \"inflow\": \"south\",",
			"    \"outflow\": \"north\",",
			"    \"conveyance_exponent\": 0.0,",
			"    \"min_depth_m\": 1.0,",
			"    \"smoothing_m\": 400.0,",
			"    \"shelter_threshold\": 0.85,",
			"    \"depth_exponent\": 0.16666666666666666,",
			"    \"depth_smoothing_m\": 200.0,",
			"    \"cross_taper_m\": 60.0,",
			"    \"station_disk_m\": 40.0,",
			"    \"station_reach_m\": 120.0,",
			"    \"across_reach_m\": 500.0,",
			"    \"tolerance\": 1e-10",
			"  },",
			"  \"sources\": [",
			"    {",
			"      \"name\": \"depth/DIGEST\",",
			"      \"sha256\": \"8ab628e98fb5e24e42115b721608f82b239f40eb5324415a65b9fe44d40bec97\",",
			"      \"note\": \"synthetic golden vector\"",
			"    }",
			"  ],",
			"  \"notes\": [",
			"    \"synthetic golden vector\"",
			"  ]",
			"}",
		};

		private const string FieldValues =
			"b28f703f441daf3e0000803f0000803f0000803f0000000000000000da08623f945ef03e0000803f0000803f000000000000803f00000000bd1b4f3f1879163f0000803f0000803f0000803f0000000000000000a726383f22d5313f0000003f0000003f9a99193f00000000cdcccc3efe9b1d3f13bb493f0000803f0000803f000000000000003f0000003fca03683f6c61d83e0000803f0000803f0000803f00000000000000001db3563f776d0b3f0000803f0000803f0000803f0000000000000000a634413f7cf3273f0000803f0000803f000000000000803f000000007cf3273fa634413f0000403f000000009a99193f00000000cdcccc3e00000000000000000000000000000000000000000000000000000000d7b35d3f0000003f0000803f0000803f3333333f000000009a99993e13bb493ffe9b1d3f0000803f0000803f3333333f000000009a99993e22d5313fa726383f0000803f0000803f0000803f00000000000000001879163fbd1b4f3f0000803f0000000000000000000000000000803f00000000000000000000000000000000000000000000000000000000f3b3513fe8d5123f0000803f0000803f3333333f000000009a99993eff393b3f72972e3f0000803f0000803f3333333f000000009a99993e241b213f0af3463f0000803f0000803f0000803f000000000000000089d9033f516f5b3f0000803f000000000000803f000000000000000000000000000000000000000000000000000000000000000000000000";

		private const string FieldSlots = "00ffffff00ff00ffff00ff01ff000200ffff00ffffff00ff00ff01ffffff01ff0201ff0201ffffffff01ffffff01ff0201ff0201ffff01ffffffffff";

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
			"  \"depth_digest\": \"8ab628e98fb5e24e42115b721608f82b239f40eb5324415a65b9fe44d40bec97\",",
			"  \"field_digest\": \"ba4e1c9ea4c5316566f13c07f03cecea046a3a3fecb04845b349c85c54e6988f\",",
			"  \"current_stations\": [",
			"    {",
			"      \"id\": \"A\",",
			"      \"name\": \"Station A\",",
			"      \"kind\": \"harmonic-prediction\",",
			"      \"lat\": 40.6999,",
			"      \"lon\": -74.015,",
			"      \"north_m\": -11.104811271306076,",
			"      \"east_m\": 1.1200973482061727e-10,",
			"      \"bin_depth_m\": null,",
			"      \"source\": \"A_currents.csv\",",
			"      \"source_sha256\": \"1111111111111111111111111111111111111111111111111111111111111111\"",
			"    },",
			"    {",
			"      \"id\": \"B\",",
			"      \"name\": \"Station B\",",
			"      \"kind\": \"subordinate-prediction\",",
			"      \"lat\": 40.70025,",
			"      \"lon\": -74.01475,",
			"      \"north_m\": 27.762059079348532,",
			"      \"east_m\": 21.128797941312868,",
			"      \"bin_depth_m\": null,",
			"      \"source\": \"B_currents.csv\",",
			"      \"source_sha256\": \"1111111111111111111111111111111111111111111111111111111111111111\",",
			"      \"derivation\": {",
			"        \"method\": \"noaa-subordinate\",",
			"        \"reference_station\": \"NYH1924\",",
			"        \"reference_bin\": 6,",
			"        \"slack_before_flood_min\": 1.0,",
			"        \"max_flood_min\": -6.0,",
			"        \"slack_before_ebb_min\": -2.0,",
			"        \"max_ebb_min\": 8.0,",
			"        \"flood_ratio\": 0.6,",
			"        \"ebb_ratio\": 1.12,",
			"        \"flood_dir_deg\": 37.0,",
			"        \"ebb_dir_deg\": 215.0,",
			"        \"sources\": [",
			"          {",
			"            \"name\": \"offsets_B.json\",",
			"            \"sha256\": \"4444444444444444444444444444444444444444444444444444444444444444\"",
			"          }",
			"        ]",
			"      }",
			"    },",
			"    {",
			"      \"id\": \"C\",",
			"      \"name\": \"Station C\",",
			"      \"kind\": \"harmonic-prediction\",",
			"      \"lat\": 40.7012,",
			"      \"lon\": -74.013,",
			"      \"north_m\": 133.25967404346818,",
			"      \"east_m\": 169.02798211318012,",
			"      \"bin_depth_m\": null,",
			"      \"source\": \"C_currents.csv\",",
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
			"  \"wind_stations\": [],",
			"  \"files\": {",
			"    \"currents.csv\": \"4f02df0ce238edc3a03df943c43adcf280e38a21127e153ae7d1dc4d1b3be8b6\",",
			"    \"water_levels.csv\": \"02dfaf049b7e948e96a3a4b63c662a04a0791ba027d536da99ef83196afb6ddf\",",
			"    \"wind.csv\": \"1f8e028cd292a874524be9796c9cca60e6690d4aca3e5351d7f3ac63ea652106\"",
			"  },",
			"  \"notes\": [",
			"    \"synthetic golden vector with a current field\"",
			"  ]",
			"}",
		};

		private static readonly string[] Currents =
		{
			"station_id,t_s,north_mps,east_mps",
			"A,-3600,0.5,0.2",
			"A,0,1.0,0.4",
			"A,360,1.25,0.5",
			"A,720,-0.75,-0.3",
			"A,90000,0.1,0.0",
			"B,-3600,0.3,0.6",
			"B,0,0.4,0.9",
			"B,360,0.2,1.1",
			"B,720,-0.3,-0.8",
			"B,90000,0.0,0.1",
			"C,-3600,-0.2,0.1",
			"C,0,0.6,-0.2",
			"C,360,0.7,0.1",
			"C,720,0.1,0.4",
			"C,90000,0.0,0.0",
		};

		private static readonly string[] WaterLevels =
		{
			"station_id,t_s,level_navd88_m,source",
			"8518750,-3600,0.1,observed",
			"8518750,90000,-0.3,observed",
		};

		private static readonly string[] Wind =
		{
			"station_id,t_s,north_mps,east_mps,gust_mps",
		};

		/// <summary>Writes the pack under <paramref name="root"/> and returns its day folder.</summary>
		public static string Write(string root)
		{
			var env = Path.Combine(root, "lower-east-river", "env");
			var depth = Path.Combine(env, "depth");
			var field = Path.Combine(env, "field");
			var day = Path.Combine(env, "days", "2025-07-14");
			Directory.CreateDirectory(depth);
			Directory.CreateDirectory(field);
			Directory.CreateDirectory(day);
			File.WriteAllBytes(Path.Combine(depth, "manifest.json"), Text(DepthManifest));
			File.WriteAllBytes(Path.Combine(depth, "slice.f32"), Hex(DepthGrid));
			File.WriteAllBytes(Path.Combine(depth, "DIGEST"), Encoding.ASCII.GetBytes(DepthDigest + "\n"));
			File.WriteAllBytes(Path.Combine(field, "manifest.json"), Text(FieldManifest));
			File.WriteAllBytes(Path.Combine(field, "slice.f32"), Hex(FieldValues));
			File.WriteAllBytes(Path.Combine(field, "slice.u8"), Hex(FieldSlots));
			File.WriteAllBytes(Path.Combine(field, "DIGEST"), Encoding.ASCII.GetBytes(FieldDigest + "\n"));
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
