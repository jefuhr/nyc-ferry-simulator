using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Traffic;
using Newtonsoft.Json.Linq;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The harbor traffic renderer's coverage: the active tiles' <c>bounds_local_m</c> rectangles from
	/// <c>data/processed/harbor/index.json</c>, as Traffic/README.md asks (no margin, holes stay holes). The water
	/// service covers exactly these tiles.
	/// </summary>
	public static class HarborTrafficCoverage
	{
		public static TrafficTileArea Load(string indexFile)
		{
			if (!File.Exists(indexFile))
			{
				throw new FileNotFoundException("Missing harbor tile index: " + indexFile, indexFile);
			}

			return Parse(File.ReadAllText(indexFile), indexFile);
		}

		public static TrafficTileArea Parse(string json, string file)
		{
			JObject root;
			try
			{
				root = JObject.Parse(json ?? throw new ArgumentNullException(nameof(json)));
			}
			catch (Newtonsoft.Json.JsonException error)
			{
				throw new FormatException(file + ": " + error.Message);
			}

			if (!(root["tiles"] is JArray tiles))
			{
				throw new FormatException(file + ": no 'tiles' array.");
			}

			var rectangles = new List<TrafficBounds>();
			foreach (var tile in tiles)
			{
				if (tile["active"] == null || tile["active"].Type != JTokenType.Boolean)
				{
					throw new FormatException(file + ": every tile needs a boolean 'active'.");
				}

				if (!(bool)tile["active"])
				{
					continue;
				}

				if (!(tile["bounds_local_m"] is JArray bounds) || bounds.Count != 4)
				{
					throw new FormatException(file + ": tile " + tile["id"] + " needs bounds_local_m [northMin, eastMin, northMax, eastMax].");
				}

				// Source order is [northMin, eastMin, northMax, eastMax]; TrafficBounds takes (minNorth, maxNorth, minEast, maxEast).
				rectangles.Add(new TrafficBounds((double)bounds[0], (double)bounds[2], (double)bounds[1], (double)bounds[3]));
			}

			if (rectangles.Count == 0)
			{
				throw new FormatException(file + ": no active tiles.");
			}

			return new TrafficTileArea(rectangles.ToArray());
		}
	}
}
