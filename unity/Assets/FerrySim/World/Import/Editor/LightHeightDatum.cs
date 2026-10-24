using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace FerrySim.World.Import
{
	/// <summary>Chart MHW heights to NAVD88 using checksum-pinned NOAA CO-OPS station datums.</summary>
	public sealed class LightHeightDatum
	{
		public double MhwStationDatumMetres { get; }
		public double Navd88StationDatumMetres { get; }
		public double OffsetMetres => MhwStationDatumMetres - Navd88StationDatumMetres;
		public string Epoch { get; }
		public JObject Provenance { get; private set; }

		public LightHeightDatum(JObject datums)
		{
			if ((string)datums["units"] != "meters" || (string)datums["OrthometricDatum"] != "NAVD88")
				throw new InvalidDataException("Light heights require metric CO-OPS datums with NAVD88.");
			MhwStationDatumMetres = Value(datums, "MHW"); Navd88StationDatumMetres = Value(datums, "NAVD88");
			Epoch = (string)datums["epoch"] ?? throw new InvalidDataException("CO-OPS datum epoch is missing.");
		}

		private static double Value(JObject datums, string name)
		{
			var entries = (datums["datums"] as JArray)?.Where(d => (string)d["name"] == name).ToArray();
			if (entries == null || entries.Length != 1 || entries[0]["value"]?.Type != JTokenType.Float && entries[0]["value"]?.Type != JTokenType.Integer)
				throw new InvalidDataException("CO-OPS datum is missing or ambiguous: " + name);
			var value = (double)entries[0]["value"];
			if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("Nonfinite CO-OPS datum: " + name);
			return value;
		}

		public double ToNavd88(double chartHeightMetres) => chartHeightMetres + OffsetMetres;

		public static LightHeightDatum Read(string repository)
		{
			const string station = "8518750"; // The Battery, the pipeline's local reference water-level station.
			var name = "datums_" + station + ".json";
			var manifest = JObject.Parse(File.ReadAllText(Path.Combine(repository, "data/manifests/coops.json")));
			var source = ((JArray)manifest["files"]).Single(f => (string)f["name"] == name);
			var cache = Environment.GetEnvironmentVariable("FERRYSIM_CACHE");
			if (string.IsNullOrEmpty(cache)) cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FerrySim-cache");
			var bytes = File.ReadAllBytes(Path.Combine(cache, "data/raw/coops", name));
			string checksum;
			using (var sha = SHA256.Create()) checksum = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
			if (checksum != (string)source["sha256"]) throw new InvalidDataException("CO-OPS datum cache does not match coops.json: " + name);
			var datum = new LightHeightDatum(JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes)));
			datum.Provenance = new JObject { ["station_id"] = station, ["station_name"] = "The Battery", ["epoch"] = datum.Epoch,
				["mhw_station_datum_m"] = datum.MhwStationDatumMetres, ["navd88_station_datum_m"] = datum.Navd88StationDatumMetres,
				["mhw_above_navd88_m"] = datum.OffsetMetres, ["source"] = source.DeepClone(),
				["mapping"] = "Published chart height above MHW + (MHW - NAVD88) at The Battery; one station relationship across the slice, not a surveyed local datum surface",
				["missing_height_note"] = "Unpublished heights remain greybox estimates at NAVD88 +3 m" };
			return datum;
		}
	}
}
