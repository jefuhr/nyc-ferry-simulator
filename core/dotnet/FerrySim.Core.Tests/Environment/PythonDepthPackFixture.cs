using System;
using System.IO;
using System.Text;
using FerrySim.Core.Env;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// A pack whose depth set has several grids, written by an independent Python implementation of the depth-set rule for
	/// several grids (ferrysim-env-depth/1 as revised 2026-10-25): four 10 m grids meeting at the origin on their shared edge
	/// rows and columns of centres, a fifth overlapping one of them by a square of four centres, and a sixth of another
	/// cell size that meets none. Every shared sample is identical; three centres are unknown (NaN), one of them shared.
	/// <c>json.dumps(indent=2)</c> manifests, <c>struct.pack("&lt;f")</c> grid values, <c>hashlib.sha256</c> digests, and
	/// golden samples of the sampling rule from that implementation: on and next to seams and corners, in the overlap,
	/// next to unknown centres and outside every grid. The C# reader must load it with Python's digests and
	/// <see cref="DepthSet"/> must reproduce the samples, the grid each point resolves to included. Made-up values, not
	/// survey data. The generator is <c>python_depth_pack.py</c> beside this file: <c>python3 python_depth_pack.py
	/// &lt;out-dir&gt;</c> writes the pack and prints the digests and the samples.
	/// </summary>
	public static class PythonDepthPackFixture
	{
		/// <summary>Largest difference from Python's golden samples, m: both sides do the same double arithmetic on the same float32 values.</summary>
		public const double Tolerance = 1e-12;

		public const string DepthDigest = "0af09e87af9ea948a1e1d66ef9ac37eb83f76f4cde782ca563b16f72db16250f";

		public const string DayDigest = "de763bada81c7f9eab52cb47debfca2dacd1a8a985a7c1a025e71cd7e7fcd9b6";

		/// <summary>A point, what the depth set says there, the grid that says it (null outside every grid) and the bed elevation.</summary>
		public sealed class Sample
		{
			public Sample(string label, double north, double east, BedSample kind, string grid, double bedElevation)
			{
				Label = label;
				North = north;
				East = east;
				Kind = kind;
				Grid = grid;
				BedElevation = bedElevation;
			}

			public string Label { get; }

			public double North { get; }

			public double East { get; }

			public BedSample Kind { get; }

			public string Grid { get; }

			public double BedElevation { get; }

			public override string ToString() => Label;
		}

		public static readonly Sample[] Samples =
		{
			new Sample("inside sw", -15.0, -5.0, BedSample.Known, "sw", -11.46875),
			new Sample("inside sw next to its unknown corner", -15.0, -25.0, BedSample.Unknown, "sw", 0.0),
			new Sample("inside se, which has three columns", -15.0, 5.0, BedSample.Known, "se", -11.65625),
			new Sample("on the sw/nw seam", 0.0, -15.0, BedSample.Known, "sw", -12.875),
			new Sample("just south of the sw/nw seam", -1.0E-09, -15.0, BedSample.Known, "sw", -12.874999999893749),
			new Sample("just north of the sw/nw seam", 1.0E-09, -15.0, BedSample.Known, "nw", -12.87499999969375),
			new Sample("1 mm south of the sw/nw seam", -0.001, -12.5, BedSample.Known, "sw", -13.4371875),
			new Sample("1 mm north of the sw/nw seam", 0.001, -12.5, BedSample.Known, "nw", -13.43719375),
			new Sample("on the sw/se seam", -15.0, 0.0, BedSample.Known, "sw", -12.59375),
			new Sample("just west of the sw/se seam", -15.0, -1.0E-09, BedSample.Known, "sw", -12.593749999775),
			new Sample("just east of the sw/se seam", -15.0, 1.0E-09, BedSample.Known, "se", -12.5937499998125),
			new Sample("on the corner of four grids", 0.0, 0.0, BedSample.Known, "sw", -8.0),
			new Sample("next to the corner, north-east", 1.0E-09, 1.0E-09, BedSample.Known, "ne", -8.00000000074375),
			new Sample("next to the corner, south-east", -1.0E-09, 1.0E-09, BedSample.Known, "se", -8.000000000531251),
			new Sample("next to the corner, north-west", 1.0E-09, -1.0E-09, BedSample.Known, "nw", -8.00000000111875),
			new Sample("on the se/ne seam, where se's cell has an unknown corner of weight zero", 0.0, 15.0, BedSample.Unknown, "se", 0.0),
			new Sample("just north of it, in ne", 1.0E-09, 15.0, BedSample.Known, "ne", -11.37500000010625),
			new Sample("in the overlap of ne and cap", 15.0, 15.0, BedSample.Known, "ne", -12.96875),
			new Sample("on cap's south edge inside ne", 10.0, 13.0, BedSample.Known, "ne", -13.6375),
			new Sample("in cap only", 25.0, 25.0, BedSample.Known, "cap", -12.15625),
			new Sample("next to a shared unknown sample, in nw", 2.0, -25.0, BedSample.Unknown, "nw", 0.0),
			new Sample("on the seam next to a shared unknown sample", 0.0, -25.0, BedSample.Unknown, "sw", 0.0),
			new Sample("on the last centre of nw, unshared", 20.0, -30.0, BedSample.Known, "nw", -11.625),
			new Sample("in far, another cell size", 110.0, 120.0, BedSample.Known, "far", -12.575),
			new Sample("just outside cap's east edge", 25.0, 30.000001, BedSample.Outside, null, 0.0),
			new Sample("outside every grid", 50.0, 50.0, BedSample.Outside, null, 0.0),
		};

		private static readonly string[] DepthManifest =
		{
			"{",
			"  \"format\": \"ferrysim-env-depth/1\",",
			"  \"region\": \"harbor\",",
			"  \"datum\": \"NAVD88\",",
			"  \"value\": \"bed elevation, metres, positive up (water is negative)\",",
			"  \"grids\": [",
			"    {",
			"      \"id\": \"sw\",",
			"      \"file\": \"sw.f32\",",
			"      \"origin_north_m\": -20.0,",
			"      \"origin_east_m\": -30.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 4,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"bd11ec5fa4955f7521853d038d9bc30ecfc0651ea12cf47f86527cc380bced39\"",
			"    },",
			"    {",
			"      \"id\": \"se\",",
			"      \"file\": \"se.f32\",",
			"      \"origin_north_m\": -20.0,",
			"      \"origin_east_m\": 0.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 3,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"a40d726a5b2d4642e179acff0738fb39f9da6c905938413b44d76ecc4e8ca430\"",
			"    },",
			"    {",
			"      \"id\": \"nw\",",
			"      \"file\": \"nw.f32\",",
			"      \"origin_north_m\": 0.0,",
			"      \"origin_east_m\": -30.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 4,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"2e55b43c4f40dd005ea5d5a1d986d7d4883b65bf2d58576fdfa0e4425e696262\"",
			"    },",
			"    {",
			"      \"id\": \"ne\",",
			"      \"file\": \"ne.f32\",",
			"      \"origin_north_m\": 0.0,",
			"      \"origin_east_m\": 0.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 3,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"8452413b669e68a2ae63c65d24b0f8f50c658e4221e404c79667bd685ef5d533\"",
			"    },",
			"    {",
			"      \"id\": \"cap\",",
			"      \"file\": \"cap.f32\",",
			"      \"origin_north_m\": 10.0,",
			"      \"origin_east_m\": 10.0,",
			"      \"cell_m\": 10.0,",
			"      \"rows\": 3,",
			"      \"cols\": 3,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"059bf487fcc6e90d39ed7776c2ff1d6eea424a42e4c110b6c36b0b66ea884c29\"",
			"    },",
			"    {",
			"      \"id\": \"far\",",
			"      \"file\": \"far.f32\",",
			"      \"origin_north_m\": 100.0,",
			"      \"origin_east_m\": 100.0,",
			"      \"cell_m\": 25.0,",
			"      \"rows\": 2,",
			"      \"cols\": 2,",
			"      \"nodata\": \"NaN\",",
			"      \"sha256\": \"f2139251c8359142ae4c631d6a1e0257b50b5e47feef136e94e03a4ebf2aee8e\"",
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

		/// <summary>Each grid file's bytes, hex, in grids order.</summary>
		private static readonly (string Name, string Hex)[] GridFiles =
		{
			("sw.f32", "0000c07f00001ac100003ec1000062c1000049c100006dc100000dc1000031c10000c07f00003cc1000060c1000000c1"),
			("se.f32", "000062c1000002c1000026c1000031c1000055c10000c07f000000c1000024c1000048c1"),
			("nw.f32", "0000c07f00003cc1000060c1000000c100006bc100000bc100002fc1000053c100003ac100005ec10000fcc0000022c1"),
			("ne.f32", "000000c1000024c1000048c1000053c1000077c1000017c1000022c1000046c100006ac1"),
			("cap.f32", "000077c1000017c100003bc1000046c100006ac100000ac1000015c1000039c100005dc1"),
			("far.f32", "000002c100004ac1000024c100006cc1"),
		};

		private static readonly string[] DayManifest =
		{
			"{",
			"  \"format\": \"ferrysim-env/1\",",
			"  \"region\": \"harbor\",",
			"  \"date\": \"2025-07-14\",",
			"  \"epoch_utc\": \"2025-07-14T04:00:00Z\",",
			"  \"time_coverage_s\": [",
			"    -3600,",
			"    90000",
			"  ],",
			"  \"depth_digest\": \"0af09e87af9ea948a1e1d66ef9ac37eb83f76f4cde782ca563b16f72db16250f\",",
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
			"  \"wind_stations\": [],",
			"  \"files\": {",
			"    \"currents.csv\": \"a8f4e8cc05fb368e7cee42e95eeaecbd276471ec4a9dd08b9db29c84840dac20\",",
			"    \"water_levels.csv\": \"02dfaf049b7e948e96a3a4b63c662a04a0791ba027d536da99ef83196afb6ddf\",",
			"    \"wind.csv\": \"1f8e028cd292a874524be9796c9cca60e6690d4aca3e5351d7f3ac63ea652106\"",
			"  },",
			"  \"notes\": [",
			"    \"synthetic golden vector with several depth grids\"",
			"  ]",
			"}",
		};

		private static readonly string[] Currents =
		{
			"station_id,t_s,north_mps,east_mps",
			"NYH1920,-3600,0.5,-0.25",
			"NYH1920,90000,-0.5,0.25",
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
			var env = Path.Combine(root, "harbor", "env");
			var depth = Path.Combine(env, "depth");
			var day = Path.Combine(env, "days", "2025-07-14");
			Directory.CreateDirectory(depth);
			Directory.CreateDirectory(day);
			File.WriteAllBytes(Path.Combine(depth, "manifest.json"), Text(DepthManifest));
			foreach (var (name, hex) in GridFiles)
			{
				File.WriteAllBytes(Path.Combine(depth, name), Hex(hex));
			}

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
