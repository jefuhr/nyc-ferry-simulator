using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Env;
using Newtonsoft.Json;

namespace FerrySim.Core.Serialization
{
	/// <summary>
	/// Writes an <see cref="EnvironmentPack"/> in the ferrysim-env/1 layout, with its manifests, series files, grid
	/// files and DIGEST files. The data pipeline makes the real packs; this exists so tests (core, CLI and later Unity)
	/// can build small synthetic ones that <see cref="EnvironmentPackJson"/> loads. Numbers are written as Python's
	/// <c>repr</c> would write them, and manifests as Python's <c>json.dump(indent=2)</c> lays them out.
	/// </summary>
	public static class EnvironmentPackWriter
	{
		/// <summary>
		/// Writes <c>depth/</c> and <c>days/&lt;date&gt;/</c> under <paramref name="environmentFolder"/> (the region's
		/// <c>env</c> folder) and returns the day folder. Existing files there are replaced.
		/// </summary>
		public static string Write(EnvironmentPack pack, string environmentFolder)
		{
			if (pack == null)
			{
				throw new ArgumentNullException(nameof(pack));
			}

			var depthDigest = WriteDepthSet(pack.Depth, Path.Combine(environmentFolder, "depth"));
			var dayFolder = Path.Combine(environmentFolder, "days", pack.Date);
			Directory.CreateDirectory(dayFolder);
			var series = new[]
			{
				EnvironmentPackCsv.WriteCurrents(pack.CurrentStations),
				EnvironmentPackCsv.WriteWaterLevels(pack.WaterLevelStations),
				EnvironmentPackCsv.WriteWind(pack.WindStations),
			};

			var manifest = Json(writer =>
			{
				writer.WriteStartObject();
				Property(writer, "format", EnvironmentPack.Format);
				Property(writer, "region", pack.Region);
				Property(writer, "date", pack.Date);
				Property(writer, "epoch_utc", pack.EpochUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
				writer.WritePropertyName("time_coverage_s");
				writer.WriteStartArray();
				Number(writer, pack.CoverageStart);
				Number(writer, pack.CoverageEnd);
				writer.WriteEndArray();
				Property(writer, "depth_digest", depthDigest);
				writer.WritePropertyName("current_stations");
				writer.WriteStartArray();
				foreach (var station in pack.CurrentStations)
				{
					writer.WriteStartObject();
					Property(writer, "id", station.Id);
					Property(writer, "name", station.Name);
					Property(writer, "kind", station.Kind);
					Position(writer, station);
					writer.WritePropertyName("bin_depth_m");
					Number(writer, station.BinDepth);
					Sources(writer, station);
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WritePropertyName("water_level_stations");
				writer.WriteStartArray();
				foreach (var station in pack.WaterLevelStations)
				{
					writer.WriteStartObject();
					Property(writer, "id", station.Id);
					Property(writer, "name", station.Name);
					Position(writer, station);
					Sources(writer, station);
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WritePropertyName("wind_stations");
				writer.WriteStartArray();
				foreach (var station in pack.WindStations)
				{
					writer.WriteStartObject();
					Property(writer, "id", station.Id);
					Property(writer, "name", station.Name);
					Position(writer, station);
					writer.WritePropertyName("sensor_height_m");
					Number(writer, station.SensorHeight);
					Sources(writer, station);
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WritePropertyName("files");
				writer.WriteStartObject();
				for (var i = 0; i < series.Length; i++)
				{
					Property(writer, EnvironmentPackDigests.SeriesFiles[i], EnvironmentPackDigests.Sha256Hex(series[i]));
				}

				writer.WriteEndObject();
				writer.WritePropertyName("notes");
				writer.WriteStartArray();
				foreach (var note in pack.Notes)
				{
					writer.WriteValue(note);
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			});

			File.WriteAllBytes(Path.Combine(dayFolder, EnvironmentPackDigests.ManifestFile), manifest);
			for (var i = 0; i < series.Length; i++)
			{
				File.WriteAllBytes(Path.Combine(dayFolder, EnvironmentPackDigests.SeriesFiles[i]), series[i]);
			}

			File.WriteAllBytes(Path.Combine(dayFolder, EnvironmentPackDigests.DigestFile),
				EnvironmentPackDigests.DigestFileBytes(EnvironmentPackDigests.DayDigest(manifest, series, depthDigest)));
			return dayFolder;
		}

		/// <summary>Writes a depth set folder and returns its digest.</summary>
		public static string WriteDepthSet(DepthSet depth, string depthFolder)
		{
			Directory.CreateDirectory(depthFolder);
			var names = new List<string>();
			var files = new List<byte[]>();
			foreach (var grid in depth.Grids)
			{
				var bytes = new byte[4 * grid.Rows * grid.Columns];
				var index = 0;
				for (var r = 0; r < grid.Rows; r++)
				{
					for (var c = 0; c < grid.Columns; c++)
					{
						var bits = BitConverter.SingleToInt32Bits(grid.BedElevation(r, c));
						bytes[index++] = (byte)bits;
						bytes[index++] = (byte)(bits >> 8);
						bytes[index++] = (byte)(bits >> 16);
						bytes[index++] = (byte)(bits >> 24);
					}
				}

				names.Add(grid.Id + ".f32");
				files.Add(bytes);
			}

			var manifest = Json(writer =>
			{
				writer.WriteStartObject();
				Property(writer, "format", EnvironmentPack.DepthFormat);
				Property(writer, "region", depth.Region);
				Property(writer, "datum", depth.Datum);
				Property(writer, "value", "bed elevation, metres, positive up (water is negative)");
				writer.WritePropertyName("grids");
				writer.WriteStartArray();
				for (var i = 0; i < depth.Grids.Count; i++)
				{
					var grid = depth.Grids[i];
					writer.WriteStartObject();
					Property(writer, "id", grid.Id);
					Property(writer, "file", names[i]);
					writer.WritePropertyName("origin_north_m");
					Number(writer, grid.OriginNorth);
					writer.WritePropertyName("origin_east_m");
					Number(writer, grid.OriginEast);
					writer.WritePropertyName("cell_m");
					Number(writer, grid.CellSize);
					writer.WritePropertyName("rows");
					writer.WriteValue(grid.Rows);
					writer.WritePropertyName("cols");
					writer.WriteValue(grid.Columns);
					Property(writer, "nodata", "NaN");
					Property(writer, "sha256", EnvironmentPackDigests.Sha256Hex(files[i]));
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WritePropertyName("outside_depth_m");
				Number(writer, depth.OutsideDepth);
				writer.WritePropertyName("sources");
				writer.WriteStartArray();
				foreach (var source in depth.Sources)
				{
					writer.WriteStartObject();
					Property(writer, "name", source.Name);
					Property(writer, "sha256", source.Sha256);
					Property(writer, "note", source.Note);
					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			});

			File.WriteAllBytes(Path.Combine(depthFolder, EnvironmentPackDigests.ManifestFile), manifest);
			for (var i = 0; i < files.Count; i++)
			{
				File.WriteAllBytes(Path.Combine(depthFolder, names[i]), files[i]);
			}

			var digest = EnvironmentPackDigests.DepthDigest(manifest, names, files);
			File.WriteAllBytes(Path.Combine(depthFolder, EnvironmentPackDigests.DigestFile), EnvironmentPackDigests.DigestFileBytes(digest));
			return digest;
		}

		private static void Position(JsonWriter writer, EnvironmentStation station)
		{
			writer.WritePropertyName("lat");
			Number(writer, station.Latitude);
			writer.WritePropertyName("lon");
			Number(writer, station.Longitude);
			writer.WritePropertyName("north_m");
			Number(writer, station.North);
			writer.WritePropertyName("east_m");
			Number(writer, station.East);
		}

		private static void Sources(JsonWriter writer, EnvironmentStation station)
		{
			Property(writer, "source", station.Source);
			Property(writer, "source_sha256", station.SourceSha256);
		}

		private static void Property(JsonWriter writer, string name, string value)
		{
			writer.WritePropertyName(name);
			writer.WriteValue(value);
		}

		private static void Number(JsonWriter writer, double? value)
		{
			if (value.HasValue)
			{
				writer.WriteRawValue(EnvironmentPackDigests.PythonFloat(value.Value));
			}
			else
			{
				writer.WriteNull();
			}
		}

		private static byte[] Json(Action<JsonTextWriter> write)
		{
			using (var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" })
			{
				using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ', CloseOutput = false })
				{
					write(writer);
				}

				text.Write('\n');
				return new UTF8Encoding(false).GetBytes(text.ToString());
			}
		}
	}
}
