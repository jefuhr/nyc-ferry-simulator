using System;
using System.IO;
using FerrySim.Core.Contracts.Geodesy;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.World.Import
{
	public static class ImportCoordinates
	{
		public static Vector3 Position(double lon, double lat, double navd88, double geoidOffset)
		{
			var ne = LocalTangentPlane.World.ToLocal(new GeodeticPosition(lat, lon));
			return new Vector3((float)ne.East, (float)(navd88 + geoidOffset), (float)ne.North);
		}
	}

	public sealed class RawDepthGrid
	{
		public readonly int Width, Height;
		public readonly double Lon, Lat, CellLon, CellLat;
		public readonly float NoData;
		public readonly float[] Values;
		public RawDepthGrid(string directory)
		{
			var h = JObject.Parse(File.ReadAllText(Path.Combine(directory, "depth.json")));
			if ((string)h["crs"] != "EPSG:4326" || (string)h["datum"] != "NAVD88" || (string)h["dtype"] != "little-endian float32")
				throw new InvalidDataException("Unsupported raster CRS, datum or encoding.");
			Width = (int)h["width"]; Height = (int)h["height"];
			Lon = (double)h["origin"][0]; Lat = (double)h["origin"][1];
			CellLon = (double)h["cell_size"][0]; CellLat = (double)h["cell_size"][1];
			NoData = h["nodata"].Type == JTokenType.Null ? float.NaN : (float)h["nodata"];
			Values = new float[checked(Width * Height)];
			var bytes = File.ReadAllBytes(Path.Combine(directory, "depth.f32"));
			if (bytes.Length != Values.Length * 4) throw new InvalidDataException("Raster byte count does not match header.");
			using (var reader = new BinaryReader(new MemoryStream(bytes)))
				for (var i = 0; i < Values.Length; i++) Values[i] = reader.ReadSingle();
		}
		public static bool IsValid(float value, float nodata) => !float.IsNaN(value) && !float.IsInfinity(value) && value != nodata;
	}
}
