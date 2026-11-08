using System.IO;
using System.Linq;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class InteriorAssetTests
	{
		[TestCase("k-class")]
		[TestCase("t-class")]
		public void Interior_HasOwnEyeScreensBudgetAndClearBow(string id)
		{
			var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
			Assert.That(prefab, Is.Not.Null, "interior prefab is separate from traffic LODs");
			var root = Object.Instantiate(prefab);
			try
			{
				Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty, "render only");
				Assert.That(root.GetComponentsInChildren<Rigidbody>(), Is.Empty);
				var meshes = root.GetComponentsInChildren<MeshFilter>();
				Assert.That(meshes.Sum(m => m.sharedMesh.triangles.Length / 3), Is.InRange(1000, 60000));
				foreach (var screen in new[] { "RadarScreen", "PlotterScreen" })
				{
					var mount = root.transform.Find(screen);
					Assert.That(mount, Is.Not.Null, screen);
					Assert.That(mount.GetComponentInChildren<Renderer>().sharedMaterial.name, Is.EqualTo(screen));
				}
				var v = VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../vessels/" + id + ".json")));
				var d = GreyboxDimensions.From(v);
				var bow = new Vector3(0, (float)d.Freeboard, 0);
				foreach (var name in new[] { "Eye", "EyeStanding" })
				{
					var eye = root.transform.Find(name);
					Assert.That(eye, Is.Not.Null);
					foreach (var fraction in new[] { .45f, .5f })
					{
						bow.z = (float)d.LengthOverall * fraction;
						var direction = bow - eye.position;
						foreach (var mesh in meshes)
						{
							// A collider exists only inside this test, independently checking the imported triangles.
							var collider = mesh.gameObject.AddComponent<MeshCollider>();
							collider.sharedMesh = mesh.sharedMesh;
							Physics.SyncTransforms();
							Assert.That(collider.Raycast(new Ray(eye.position, direction.normalized), out _, direction.magnitude), Is.False, name + " blocked by " + mesh.name);
							Object.DestroyImmediate(collider);
						}
					}
				}
			}
			finally { Object.DestroyImmediate(root); }
		}

		[Test]
		public void TrafficPrefabs_DoNotContainInterior()
		{
			foreach (var id in new[] { "t-class", "k-class" })
			{
				var traffic = Resources.Load<GameObject>("FerrySimVessels/" + id + "/LOD1");
				Assert.That(traffic.GetComponentsInChildren<Transform>().Any(t => t.name == "HelmFFU"), Is.False);
			}
		}
	}
}
