using System;
using System.IO;
using System.Linq;
using Object = UnityEngine.Object;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.Traffic;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Vessels.Tests
{
	// Failure modes: FBX metre/axis conversion, flipped port/starboard, a shifted bridge or bow,
	// excess geometry, colliders, stale class on a new run, and guessing an unaudited AIS identity.
	public class FerryModelTests
	{
		[TestCase("t-class", 0)]
		[TestCase("t-class", 1)]
		[TestCase("k-class", 0)]
		[TestCase("k-class", 1)]
		public void ImportedPrefab_MatchesDimensionsMountsAndBudget(string id, int lod)
		{
			var d = ReadLayout(id).values;
			var prefab = FerryModels.Load(id, lod);
			Assert.That(prefab, Is.Not.Null);
			var model = prefab.GetComponent<FerryModel>();
			Assert.That(model, Is.Not.Null);
			Assert.That(model.Length, Is.EqualTo(d.length_overall.value).Within(0.001));
			Assert.That(model.Beam, Is.EqualTo(d.beam.value).Within(0.001));
			Assert.That(model.Mount("Eye").localPosition, Is.EqualTo(ExpectedEye(id)).Using(Vector3Comparer));
			Assert.That(model.Mount("Bow").localPosition, Is.EqualTo(new Vector3(0, d.bow_height.value, d.length_overall.value / 2)).Using(Vector3Comparer));
			foreach (var field in typeof(LightValues).GetFields())
			{
				Assert.That(model.Mount(field.Name).localPosition,
					Is.EqualTo(Vector(((Point)field.GetValue(d.light_mounts)).value)).Using(Vector3Comparer), field.Name);
			}
			var filter = prefab.GetComponentInChildren<MeshFilter>();
			var vertices = filter.sharedMesh.vertices;
			var bounds = new Bounds(prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[0])), Vector3.zero);
			foreach (var vertex in vertices)
			{
				bounds.Encapsulate(prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
			}
			Assert.That(bounds.size.z, Is.EqualTo(d.length_overall.value).Within(.001));
			Assert.That(bounds.size.x, Is.EqualTo(d.beam.value + 2*d.sponson_projection.value).Within(.001));
			Assert.That(bounds.min.y, Is.EqualTo(d.baseline_height.value).Within(.001));
			Assert.That(filter.sharedMesh.triangles.Length / 3, Is.LessThanOrEqualTo(lod == 0 ? 80000 : 5000));
			Assert.That(prefab.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(1));
			Assert.That(filter.sharedMesh.subMeshCount, Is.EqualTo(lod == 0 ? 7 : 1));
			Assert.That(prefab.GetComponentInChildren<Renderer>().sharedMaterials.Length, Is.EqualTo(lod == 0 ? 7 : 1));
			Assert.That(prefab.GetComponentsInChildren<Collider>(), Is.Empty);
			Assert.That(prefab.GetComponentInChildren<Renderer>().sharedMaterials.Select(m => m.name),
				Is.EquivalentTo(lod == 0 ? ReadLayout(id).materials.Select(m => m.name) : new[] { "NeutralFerry" }));
		}

		// Test-only readers check the source layout independently of the export receipt/importer.
		[Serializable] public class Number { public float value; }
		[Serializable] public class Point { public float[] value; }
		[Serializable] public class Surface { public string name; }
		[Serializable] public class LightValues
		{
			public Point Masthead, PortSidelight, StarboardSidelight, Sternlight, ForwardAnchor, AfterAnchor, AfterMasthead;
		}
		[Serializable] public class HullValues
		{
			public Number length_overall, beam, sponson_projection, baseline_height, main_deck_height, bow_height, upper_deck_height, window_bottom, window_top;
			public LightValues light_mounts;
		}
		[Serializable] public class HullLayout { public HullValues values; public Surface[] materials; }
		[Serializable] public class EyeValues { public Point eye_position, eye_seated, floor_origin; }
		[Serializable] public class EyeLayout { public EyeValues values; }
		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static HullLayout ReadLayout(string id) => JsonUtility.FromJson<HullLayout>(File.ReadAllText(Path.Combine(Repo, "art/blender/data/" + id + "-hull.json")));
		private static Vector3 Vector(float[] p) => new Vector3(p[0], p[1], p[2]);
		private static Vector3 ExpectedEye(string id)
		{
			var path = Path.Combine(Repo, "art/blender/data/" + id + "-wheelhouse.json");
			var values = File.Exists(path) ? JsonUtility.FromJson<EyeLayout>(File.ReadAllText(path)).values : null;
			if (values?.eye_position?.value != null) { return Vector(values.eye_position.value); }
			if (values?.eye_seated?.value != null)
			{
				var eye = Vector(values.eye_seated.value);
				return new Vector3(eye.x, values.floor_origin.value[1] + eye.y, eye.z);
			}
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(Repo, "vessels", id + ".json")));
			return new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward);
		}

		private static readonly System.Collections.Generic.IEqualityComparer<Vector3> Vector3Comparer = new NearVector();
		private sealed class NearVector : System.Collections.Generic.IEqualityComparer<Vector3>
		{
			public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < .001f;
			public int GetHashCode(Vector3 value) => 0;
		}

		[Test]
		public void IdentityRequiresAudit_NotNameOrRoundedDimensions()
		{
			Assert.That(FerryModels.ClassForMmsi(367798420), Is.EqualTo("t-class"));
			Assert.That(FerryModels.ClassForMmsi(368135270), Is.EqualTo("k-class"));
			Assert.That(FerryModels.ClassForMmsi(368152730), Is.Null);
			Assert.That(FerryModels.ClassForMmsi(366651000), Is.Null);
		}

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void TheCabinWindowUsesGlassAfterNativeMaterialSlotReordering(string id)
		{
			var d = ReadLayout(id).values;
			var instance = Object.Instantiate(FerryModels.Load(id, 0));
			try
			{
				var geometry = instance.GetComponentInChildren<MeshFilter>();
				var collider = geometry.gameObject.AddComponent<MeshCollider>();
				collider.sharedMesh = geometry.sharedMesh;
				Physics.SyncTransforms();
				var windowHeight = (d.window_bottom.value + d.window_top.value) / 2;
				var ray = new Ray(new Vector3(d.beam.value, windowHeight, .17f), Vector3.left);
				Assert.That(collider.Raycast(ray, out var hit, d.beam.value), Is.True);
				var triangle = hit.triangleIndex;
				var material = 0;
				while (material < geometry.sharedMesh.subMeshCount && triangle >= geometry.sharedMesh.GetIndexCount(material) / 3)
				{
					triangle -= (int)(geometry.sharedMesh.GetIndexCount(material) / 3);
					material++;
				}
				Assert.That(material, Is.LessThan(geometry.sharedMesh.subMeshCount));
				Assert.That(geometry.GetComponent<Renderer>().sharedMaterials[material].name, Is.EqualTo("WindowGlass"));
			}
			finally { Object.DestroyImmediate(instance); }
		}

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void OwnModelUsesLayoutLightsAndRetainsNavigationPlacement(string id)
		{
			var rootPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(rootPath, "vessels", id + ".json")));
			var root = new GameObject("own model lighting test");
			try
			{
				var model = FerryModels.Create(root.transform, id, 0, d.LengthOverall, d.Beam);
				Assert.That(model.transform.localScale, Is.EqualTo(Vector3.one), "own-vessel visual dimensions come from the builder layout; physics particulars remain separate");
				var actual = VesselLightLayout.FromModel(root.transform, VesselLightLayout.ForGreybox(d));
				Assert.That(actual.Masthead, Is.EqualTo(model.transform.TransformPoint(model.Mount("Masthead").localPosition)).Using(Vector3Comparer));
				var layout = ReadLayout(id).values;
				Assert.That(actual.Masthead.y - layout.main_deck_height.value, Is.GreaterThanOrEqualTo(6f));
				Assert.That(actual.Masthead.y - actual.StarboardSidelight.y, Is.GreaterThanOrEqualTo(2.5f));
				Assert.That(actual.Masthead.z, Is.GreaterThan(actual.StarboardSidelight.z));
				Assert.That(actual.PortSidelight.x, Is.LessThan(0));
				Assert.That(actual.StarboardSidelight.x, Is.GreaterThan(0));
				Assert.That(actual.Sternlight.z, Is.LessThan(0));
			}
			finally { Object.DestroyImmediate(root); }
		}

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void TheEyeCanSeeTheBowDeck_WithoutAWindowSillOrConsoleBlockingIt(string id)
		{
			var instance = Object.Instantiate(FerryModels.Load(id, 0));
			var backfaces = Physics.queriesHitBackfaces;
			try
			{
				var model = instance.GetComponent<FerryModel>();
				var geometry = instance.GetComponentInChildren<MeshFilter>();
				// Test-only collider, never saved or shipped. The rendered mesh must leave this sight line open.
				var collider = geometry.gameObject.AddComponent<MeshCollider>();
				collider.sharedMesh = geometry.sharedMesh;
				Physics.queriesHitBackfaces = false;
				Physics.SyncTransforms();
				var eye = model.Mount("Eye").position;
				var toBow = model.Mount("Bow").position - eye;
				Assert.That(collider.Raycast(new Ray(eye, toBow.normalized), out var hit, toBow.magnitude + .05f), Is.True);
				Assert.That(hit.distance, Is.GreaterThan(toBow.magnitude - .5f), "first visible surface is the bow deck/ramp, not a near wheelhouse prop");
			}
			finally
			{
				Physics.queriesHitBackfaces = backfaces;
				Object.DestroyImmediate(instance);
			}
		}

		[Test]
		public void EveryAuditRowMatchesTheRuntimeIdentityTable()
		{
			var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var rows = File.ReadAllLines(Path.Combine(root, "data-pipeline/calibration/identity-audit.csv"));
			for (var i = 1; i < rows.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(rows[i])) { continue; }
				var fields = rows[i].Split(',');
				var id = fields[5] == "T" ? "t-class" : fields[5].StartsWith("K-", System.StringComparison.Ordinal) ? "k-class" : null;
				Assert.That(FerryModels.ClassForMmsi(int.Parse(fields[0])), Is.EqualTo(id), rows[i]);
			}
		}

		[Test]
		public void ScaledTrafficUsesModelMountsWithoutWorldCoordinateQuantization()
		{
			var root = new GameObject("traffic model test");
			try
			{
				var model = FerryModels.Create(root.transform, "k-class", 1, 31, 9);
				var fallback = VesselLightLayout.Generic(31, 9, 9, 2);
				var localLayout = VesselLightLayout.FromModel(root.transform, fallback);
				root.transform.SetPositionAndRotation(new Vector3(123456, 789012, -345678), Quaternion.Euler(10, 70, 15));
				Assert.That(VesselLightLayout.FromModel(root.transform, fallback).Masthead, Is.EqualTo(localLayout.Masthead));
				root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
				Assert.That(VesselLightLayout.FromModel(root.transform, fallback).Masthead,
					Is.EqualTo(root.transform.InverseTransformPoint(model.Mount("Masthead").position)).Using(Vector3Comparer));
				model.Mount("PortSidelight").localPosition += Vector3.up * .25f;
				Assert.That(VesselLightLayout.FromModel(root.transform, fallback).PortSidelight,
					Is.EqualTo(root.transform.InverseTransformPoint(model.Mount("PortSidelight").position)).Using(Vector3Comparer));
				Assert.That(model.transform.localScale.x, Is.EqualTo(9f / 8.5f).Within(.0001));
				Assert.That(model.transform.localScale.z, Is.EqualTo(31f / 29.6f).Within(.0001));
			}
			finally { Object.DestroyImmediate(root); }
		}

		[Test]
		public void MissingModelRetainsTheGenericLightLayout()
		{
			var root = new GameObject("generic traffic test");
			try
			{
				var fallback = VesselLightLayout.Generic(31, 9, 9, 2);
				var actual = VesselLightLayout.FromModel(root.transform, fallback);
				Assert.That(actual.Masthead, Is.EqualTo(fallback.Masthead));
				Assert.That(actual.PortSidelight, Is.EqualTo(fallback.PortSidelight));
				Assert.That(actual.Sternlight, Is.EqualTo(fallback.Sternlight));
			}
			finally { Object.DestroyImmediate(root); }
		}
	}
}
