using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.E2E
{
	internal static class TripData
	{
		public static readonly TripLeg Pier11Dumbo = new TripLeg("pier11-dumbo", "87", "20");
		public static readonly TripLeg DumboSouthWilliamsburg = new TripLeg("dumbo-south-williamsburg", "20", "8", "Brooklyn Bridge", "Manhattan Bridge");
		public const string ScenePath = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";
		public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
		public static string Folder => Path.Combine(Application.dataPath, "FerrySim/Tests/E2E");
		public static string Artifacts => Path.Combine(Root, "artifacts/e2e");
		public static VesselParameters Vessel() => VesselParametersJson.Parse(File.ReadAllText(Path.Combine(Root, "vessels/t-class.json")));
		public static LandingGeometry Landings() => LandingGeometry.Load(
			Path.Combine(Application.dataPath, "FerrySim/World/Generated/LowerEastRiver/contacts.json"),
			Path.Combine(Application.dataPath, "FerrySim/Slice/Data/landing-contact-surfaces.json"));
		public static ScenarioSettings Start(TripLeg leg, LandingGeometry landings, VesselParameters vessel) =>
			LandingApproach.BowOn(landings.PrimaryFace(leg.DepartureStop), vessel, 4.0, "M2 " + leg.Key, "vessels/t-class.json", "2026-06-15T16:00:00Z");
		public static HorizontalVector Middle(LandingFace face) => (face.Start + face.End) * 0.5;
		public static HorizontalVector Normal(LandingFace face) => face.Segments[0].Segment.WaterNormal;

		// Read the World's source polygon directly, independently of the rendered bridge.
		public static BridgeSpan[] BridgeSpans()
		{
			var collection = JObject.Parse(File.ReadAllText(Path.Combine(Root, "data/processed/lower-east-river/bridges.geojson")));
			return collection["features"].Where(f => (bool)f["properties"]["has_clearance"])
				.Select(f => new BridgeSpan((string)f["properties"]["name"],
					f["geometry"]["coordinates"][0].Select(p => LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)p[1], (double)p[0]))).ToArray())).ToArray();
		}

		public static bool Inside(HorizontalVector point, HorizontalVector[] ring)
		{
			var inside = false;
			for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
			{
				var a = ring[i];
				var b = ring[j];
				if ((a.North > point.North) != (b.North > point.North) &&
					point.East < (b.East - a.East) * (point.North - a.North) / (b.North - a.North) + a.East) inside = !inside;
			}
			return inside;
		}

		public static double[] Values(in VesselState s) => new[]
		{
			s.Time, s.North, s.East, s.Down, s.Roll, s.Pitch, s.Heading,
			s.Surge, s.Sway, s.Heave, s.RollRate, s.PitchRate, s.YawRate
		};
	}

	internal sealed class TripLeg
	{
		public TripLeg(string key, string departureStop, string arrivalStop, params string[] bridges)
		{
			Key = key;
			DepartureStop = departureStop;
			ArrivalStop = arrivalStop;
			RequiredBridges = bridges;
		}

		public string Key { get; }
		public string DepartureStop { get; }
		public string ArrivalStop { get; }
		public string[] RequiredBridges { get; }
		public string TracePath => Path.Combine(TripData.Folder, Key + ".inputs.csv");
		public string Artifacts => Path.Combine(TripData.Artifacts, Key);
	}

	internal sealed class BridgeSpan
	{
		public BridgeSpan(string name, HorizontalVector[] polygon) { Name = name; Polygon = polygon; }
		public string Name { get; }
		public HorizontalVector[] Polygon { get; }
	}
}
