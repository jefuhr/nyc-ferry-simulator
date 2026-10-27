using System.IO;
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
			var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(root, "vessels", id + ".json")));
			var prefab = FerryModels.Load(id, lod);
			Assert.That(prefab, Is.Not.Null);
			var model = prefab.GetComponent<FerryModel>();
			Assert.That(model, Is.Not.Null);
			Assert.That(model.Length, Is.EqualTo(d.LengthOverall).Within(0.001));
			Assert.That(model.Beam, Is.EqualTo(d.Beam).Within(0.001));
			Assert.That(model.Mount("Eye").localPosition, Is.EqualTo(new Vector3(0, (float)d.BridgeEyeHeight, (float)d.BridgeEyeForward)).Using(Vector3Comparer));
			Assert.That(model.Mount("Bow").localPosition, Is.EqualTo(new Vector3(0, (float)d.Freeboard, (float)(d.LengthOverall / 2))).Using(Vector3Comparer));
			var expected = VesselLightLayout.ForGreybox(d);
			Assert.That(model.Mount("Masthead").localPosition, Is.EqualTo(expected.Masthead).Using(Vector3Comparer));
			Assert.That(model.Mount("StarboardSidelight").localPosition, Is.EqualTo(expected.StarboardSidelight).Using(Vector3Comparer));
			Assert.That(model.Mount("PortSidelight").localPosition, Is.EqualTo(expected.PortSidelight).Using(Vector3Comparer));
			Assert.That(model.Mount("Sternlight").localPosition, Is.EqualTo(expected.Sternlight).Using(Vector3Comparer));
			Assert.That(model.Mount("ForwardAnchor").localPosition, Is.EqualTo(expected.ForwardAnchor).Using(Vector3Comparer));
			Assert.That(model.Mount("AfterAnchor").localPosition, Is.EqualTo(expected.AfterAnchor).Using(Vector3Comparer));
			Assert.That(model.Mount("AfterMasthead").localPosition, Is.EqualTo(expected.AfterMasthead).Using(Vector3Comparer));
			var filter = prefab.GetComponentInChildren<MeshFilter>();
			var vertices = filter.sharedMesh.vertices;
			var bounds = new Bounds(prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(vertices[0])), Vector3.zero);
			foreach (var vertex in vertices)
			{
				bounds.Encapsulate(prefab.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
			}
			Assert.That(bounds.size.z, Is.EqualTo(d.LengthOverall).Within(.001));
			Assert.That(bounds.size.x, Is.InRange((float)d.Beam - .03f, (float)d.Beam + .001f));
			Assert.That(bounds.min.y, Is.EqualTo(-d.Draft).Within(.001));
			Assert.That(filter.sharedMesh.triangles.Length / 3, Is.LessThanOrEqualTo(lod == 0 ? 50000 : 5000));
			Assert.That(prefab.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(1));
			Assert.That(filter.sharedMesh.subMeshCount, Is.EqualTo(lod == 0 ? 7 : 1));
			Assert.That(prefab.GetComponentInChildren<Renderer>().sharedMaterials.Length, Is.EqualTo(lod == 0 ? 7 : 1));
			Assert.That(prefab.GetComponentsInChildren<Collider>(), Is.Empty);
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
			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(repo, "vessels", id + ".json")));
			var instance = Object.Instantiate(FerryModels.Load(id, 0));
			try
			{
				var geometry = instance.GetComponentInChildren<MeshFilter>();
				var collider = geometry.gameObject.AddComponent<MeshCollider>();
				collider.sharedMesh = geometry.sharedMesh;
				Physics.SyncTransforms();
				var windowHeight = d.Freeboard + (d.CabinHeight < 4 ? d.CabinHeight * .5 : 1.18);
				var ray = new Ray(new Vector3((float)d.Beam, (float)windowHeight, (float)(-.04 * d.LengthOverall + .17)), Vector3.left);
				Assert.That(collider.Raycast(ray, out var hit, (float)d.Beam), Is.True);
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
		public void OwnModelRetainsThePreviousLightCoordinatesExactly(string id)
		{
			var rootPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var d = GreyboxDimensions.From(VesselParametersJson.Load(Path.Combine(rootPath, "vessels", id + ".json")));
			var root = new GameObject("own model lighting test");
			try
			{
				FerryModels.Create(root.transform, id, 0, d.LengthOverall, d.Beam);
				var expected = VesselLightLayout.ForGreybox(d);
				var actual = VesselLightLayout.FromModel(root.transform, expected);
				Assert.That(actual.Masthead, Is.EqualTo(expected.Masthead));
				Assert.That(actual.StarboardSidelight, Is.EqualTo(expected.StarboardSidelight));
				Assert.That(actual.Sternlight, Is.EqualTo(expected.Sternlight));
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
