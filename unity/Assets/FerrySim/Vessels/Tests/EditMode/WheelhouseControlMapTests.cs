using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Vessels.Tests
{
	// Failure modes: missing controls, FBX frame drift, inverted travel, lost UV1 indices,
	// per-lamp renderers, screen back faces and physics components on render-only parts.
	public class WheelhouseControlMapTests
	{
		[Serializable] private class Number { public float value; }
		[Serializable] private class Point { public float[] value; }
		[Serializable] private class Values { public Number upper_deck_height, wheelhouse_fore_z; }
		[Serializable] private class Control { public string id, kind; public Point pivot, travel; public float[] axis, normal; public int lamp; }
		[Serializable] private class Screen { public string id; public Point centre, size, focus_eye; public Number tilt, yaw; }
		[Serializable] private class Layout { public int schema_version; public Values values; public Control[] controls; public Screen[] screens; }
		[Serializable] private class Entry { public string Id, Kind; public Vector3 Axis, Normal; public Vector2 Travel; public int LampIndex; }
		[Serializable] private class Map { public Entry[] Controls; }
		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static Layout Read(string id, string kind) => JsonUtility.FromJson<Layout>(File.ReadAllText(Path.Combine(Repo, "art/blender/data/" + id + "-" + kind + ".json")));
		private static Vector3 V(float[] p) => new Vector3(p[0], p[1], p[2]);
		private static Vector3 Position(float[] p, Values d) => V(p) + new Vector3(0, d.upper_deck_height.value, d.wheelhouse_fore_z.value);

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void EveryControlHasIdentityPivotAndSerializedMetadata(string id)
		{
			var layout = Read(id, "wheelhouse");
			Assert.That(layout.schema_version, Is.EqualTo(2));
			var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
			var component = prefab.GetComponents<MonoBehaviour>().SingleOrDefault(c => c.GetType().Name == "WheelhouseControlMap");
			Assert.That(component, Is.Not.Null);
			var map = JsonUtility.FromJson<Map>(JsonUtility.ToJson(component));
			Assert.That(map.Controls.Select(c => c.Id), Is.EquivalentTo(layout.controls.Select(c => c.id)));
			var datums = Read(id, "hull").values;
			foreach (var control in layout.controls)
			{
				var mount = prefab.transform.Find(control.id);
				Assert.That(mount, Is.Not.Null, control.id);
				Assert.That(Vector3.Distance(mount.localPosition, Position(control.pivot.value, datums)), Is.LessThanOrEqualTo(.001f), control.id);
				Assert.That(Quaternion.Angle(mount.localRotation, Quaternion.identity), Is.LessThan(.001f), control.id);
				var entry = map.Controls.Single(c => c.Id == control.id);
				Assert.That(prefab.GetComponent<WheelhouseControlMap>().Controls.Single(c => c.Id == control.id).Mount, Is.EqualTo(mount));
				Assert.That(entry.Kind, Is.EqualTo(control.kind));
				Assert.That(Vector3.Distance(entry.Normal, V(control.normal)), Is.LessThan(.0001f));
				Assert.That(Vector3.Distance(entry.Axis, V(control.axis)), Is.LessThan(.0001f));
				Assert.That(entry.Travel, Is.EqualTo(new Vector2(control.travel.value[0], control.travel.value[1])));
				Assert.That(entry.LampIndex, Is.EqualTo(control.kind == "lamp" ? control.lamp : -1));
				if (control.id == "PortLever" || control.id == "StarboardLever")
					Assert.That((Quaternion.AngleAxis(entry.Travel.y, entry.Axis) * Vector3.up).z, Is.GreaterThan(0));
			}
		}

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void LampsUseOneRendererAndUV1BankIndices_WithoutColliders(string id)
		{
			var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
			Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);
			Assert.That(prefab.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
			var bank = prefab.GetComponentsInChildren<MeshRenderer>().Where(r => r.sharedMaterials.Any(m => m.name == "LampsSurface")).ToArray();
			Assert.That(bank.Length, Is.EqualTo(1));
			Assert.That(bank[0].sharedMaterials.Length, Is.EqualTo(1));
			var mesh = bank[0].GetComponent<MeshFilter>().sharedMesh;
			Assert.That(mesh.uv2.Length, Is.EqualTo(mesh.vertexCount));
			Assert.That(mesh.uv2.Select(v => v.x).Distinct(), Is.EquivalentTo(Read(id, "wheelhouse").controls.Where(c => c.kind == "lamp").Select(c => (float)c.lamp)));
			var triangles = mesh.triangles;
			var uv = mesh.uv2;
			for (var i = 0; i < triangles.Length; i += 3)
			{
				Assert.That(uv[triangles[i]].x, Is.EqualTo(uv[triangles[i + 1]].x));
				Assert.That(uv[triangles[i]].x, Is.EqualTo(uv[triangles[i + 2]].x));
			}
			var count = prefab.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.triangles.Length / 3);
			Assert.That(count, Is.InRange(1000, 150000));
			TestContext.WriteLine(id + ": " + count + " triangles, " + prefab.GetComponentsInChildren<Renderer>().Length + " renderers");
		}

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void ScreensFaceEyeAndFocusIsOnNormal(string id)
		{
			var layout = Read(id, "wheelhouse");
			Assert.That(layout.screens, Has.Length.EqualTo(4));
			var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
			var datums = Read(id, "hull").values;
			var eye = prefab.transform.Find("Eye");
			foreach (var screen in layout.screens)
			{
				var mount = prefab.transform.Find(screen.id);
				Assert.That(Vector3.Distance(mount.localPosition, Position(screen.centre.value, datums)), Is.LessThan(.001f));
				var expected = Quaternion.AngleAxis(screen.yaw?.value ?? 0, Vector3.up) * Quaternion.AngleAxis(screen.tilt.value, Vector3.right);
				Assert.That(Quaternion.Angle(mount.localRotation, expected), Is.LessThan(.01f));
				Assert.That(Vector3.Angle(-mount.forward, eye.position - mount.position), Is.LessThanOrEqualTo(45));
				var focus = prefab.transform.Find(screen.id + "FocusEye");
				Assert.That(Vector3.Distance(focus.localPosition, Position(screen.focus_eye.value, datums)), Is.LessThan(.001f));
				Assert.That(Vector3.Distance(focus.position, mount.position), Is.GreaterThanOrEqualTo(.45f));
				Assert.That(Vector3.Angle(focus.position - mount.position, -mount.forward), Is.LessThan(.01f));
				var surface = mount.GetComponentsInChildren<Renderer>().Single(r => r.sharedMaterial.name == screen.id);
				var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
				// Read UV corners in mount space so a mirrored or flattened face cannot pass on orientation alone.
				for (var i = 0; i < mesh.vertexCount; i++)
				{
					var p = mount.InverseTransformPoint(surface.transform.TransformPoint(mesh.vertices[i]));
					Assert.That(mesh.uv[i].x, Is.EqualTo(p.x / screen.size.value[0] + .5f).Within(.001f));
					Assert.That(mesh.uv[i].y, Is.EqualTo(p.y / screen.size.value[1] + .5f).Within(.001f));
					Assert.That(Vector3.Dot(surface.transform.TransformDirection(mesh.normals[i]), -mount.forward), Is.GreaterThan(.999f));
				}
				foreach (var x in new[] { -.48f, -.32f, -.16f, 0, .16f, .32f, .48f })
				foreach (var y in new[] { -.48f, -.32f, -.16f, 0, .16f, .32f, .48f })
				{
					var target = mount.TransformPoint(new Vector3(x * screen.size.value[0], y * screen.size.value[1], -.003f));
					foreach (var geometry in prefab.GetComponentsInChildren<MeshFilter>())
						Assert.That(Blocks(geometry, focus.position, target), Is.False, screen.id + " focus blocked by " + geometry.name);
				}
			}
		}

		private static bool Blocks(MeshFilter geometry, Vector3 eye, Vector3 target)
		{
			var vertices = geometry.sharedMesh.vertices;
			var triangles = geometry.sharedMesh.triangles;
			var direction = target - eye;
			for (var i = 0; i < triangles.Length; i += 3)
			{
				var a = geometry.transform.TransformPoint(vertices[triangles[i]]);
				var e1 = geometry.transform.TransformPoint(vertices[triangles[i + 1]]) - a;
				var e2 = geometry.transform.TransformPoint(vertices[triangles[i + 2]]) - a;
				var h = Vector3.Cross(direction, e2); var det = Vector3.Dot(e1, h);
				if (Mathf.Abs(det) < 1e-10f) continue;
				var s = eye - a; var u = Vector3.Dot(s, h) / det;
				if (u < 0 || u > 1) continue;
				var q = Vector3.Cross(s, e1); var v = Vector3.Dot(direction, q) / det;
				var t = Vector3.Dot(e2, q) / det;
				if (v >= 0 && u + v <= 1 && t > 1e-5f && t < 1 - 1e-5f) return true;
			}
			return false;
		}
	}
}
