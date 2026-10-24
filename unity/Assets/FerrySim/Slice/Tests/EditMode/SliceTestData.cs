using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>Paths and small builders shared by the slice's EditMode tests.</summary>
	internal static class SliceTestData
	{
		// Application.dataPath is <repo>/unity/Assets.
		public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ContactsPath => Path.Combine(ProjectRoot, LandingGeometry.DefaultContactsAssetPath);

		public static string SurfacesPath => Path.Combine(ProjectRoot, LandingSurfaceTable.DefaultAssetPath);

		public static string ArtifactFolder(string name)
		{
			var path = Path.Combine(RepoRoot, "artifacts", "unity", "slice", name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		public static VesselParameters LoadTClass() => VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json"));

		public static LandingSurfaceTable ShippedSurfaces() => LandingSurfaceTable.Load(SurfacesPath);

		/// <summary>One contacts.json segment as the importer writes it, with its left-hand water normal.</summary>
		public static JObject Segment(int id, string stopId, double startNorth, double startEast, double endNorth, double endEast, bool waterSideVerified = true)
		{
			var dn = endNorth - startNorth;
			var de = endEast - startEast;
			var length = Math.Sqrt(dn * dn + de * de);
			return new JObject
			{
				["id"] = id,
				["stop_id"] = stopId,
				["stop_name"] = "Stop " + stopId,
				["start_ne_m"] = new JArray(startNorth, startEast),
				["end_ne_m"] = new JArray(endNorth, endEast),
				["water_normal_ne"] = new JArray(de / length, -dn / length),
				["source"] = "enc-feature",
				["confidence"] = "medium",
				["verification"] = "needs-crew-verification",
				["water_side_verified_by_pipeline"] = waterSideVerified,
			};
		}

		public static string Json(params JObject[] segments) => new JArray(segments).ToString();

		/// <summary>A surfaces file with the shipped default and an optional own entry for one stop.</summary>
		public static string SurfacesJson(string ownStopId = null, double ownStiffness = 2.0e6)
		{
			var root = JObject.Parse(File.ReadAllText(SurfacesPath));
			if (ownStopId != null)
			{
				var own = (JObject)root["default"].DeepClone();
				own["stiffness"]["value"] = ownStiffness;
				own["note"] = "test entry";
				((JObject)root["landings"])[ownStopId] = own;
			}

			return root.ToString();
		}

		public static ContactSegment Face(int id, double startNorth, double startEast, double endNorth, double endEast) =>
			new ContactSegment(new HorizontalVector(startNorth, startEast), new HorizontalVector(endNorth, endEast), new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25), id);
	}
}
