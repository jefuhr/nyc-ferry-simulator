using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The bootstrap's failure path: missing or malformed landing data stops the runner with an error naming the file
	/// and the reason. Failure modes guarded: the vessel running in open water (through every landing) because the
	/// geometry quietly failed, and an error that does not say which file is wrong.
	/// </summary>
	public class SliceBootstrapTests
	{
		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.Destroy(_host);
			}
		}

		private static string Surfaces => File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", LandingSurfaceTable.DefaultAssetPath));

		private (VesselSimulationRunner Runner, SliceBootstrap Bootstrap) Build(TextAsset contacts, TextAsset surfaces)
		{
			_host = new GameObject("slice bootstrap (test)");
			_host.SetActive(false);
			var runner = _host.AddComponent<VesselSimulationRunner>();
			var bootstrap = _host.AddComponent<SliceBootstrap>();
			bootstrap.Configure(runner, contacts, "test/contacts.json", surfaces, "test/surfaces.json");
			return (runner, bootstrap);
		}

		[UnityTest]
		public IEnumerator AMalformedContactsFile_StopsTheRunner_NamingTheFileAndTheReason()
		{
			var (runner, bootstrap) = Build(new TextAsset("[{\"id\": 1, \"stop_id\": \"8\"}]"), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Error, new Regex(@"did not load.*test/contacts\.json: segment 0 \(id 1\): stop_name missing"));
			_host.SetActive(true);
			yield return null;

			Assert.That(runner.enabled, Is.False, "the vessel does not run without its landings");
			Assert.That(runner.IsInitialized, Is.False);
			Assert.That(bootstrap.Landings, Is.Null);
			Assert.That(bootstrap.LoadError, Does.StartWith("test/contacts.json: ").And.Contain("stop_name missing"));
		}

		[UnityTest]
		public IEnumerator AMissingSurfacesFile_StopsTheRunner_NamingTheFile()
		{
			var (runner, bootstrap) = Build(new TextAsset("[]"), null);
			LogAssert.Expect(LogType.Error, new Regex(@"did not load.*test/surfaces\.json: missing"));
			_host.SetActive(true);
			yield return null;

			Assert.That(runner.enabled, Is.False);
			Assert.That(bootstrap.LoadError, Does.StartWith("test/surfaces.json: missing"));
		}

		[UnityTest]
		public IEnumerator GoodFiles_GiveTheRunnerTheGeometry()
		{
			var contacts = "[" + "{\"id\": 5, \"stop_id\": \"8\", \"stop_name\": \"South Williamsburg\", \"start_ne_m\": [963.0, 3771.9], \"end_ne_m\": [984.0, 3786.6], " +
				"\"water_normal_ne\": [0.5734623443633283, -0.8192319205190405], \"source\": \"enc-feature\", \"confidence\": \"medium\", " +
				"\"verification\": \"needs-crew-verification\", \"water_side_verified_by_pipeline\": true}]";
			var (runner, bootstrap) = Build(new TextAsset(contacts), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Warning, new Regex("no terrain prefab"));
			LogAssert.Expect(LogType.Warning, new Regex("has no scenario"));
			_host.SetActive(true);
			yield return null;

			Assert.That(bootstrap.LoadError, Is.Null);
			Assert.That(runner.WorldGeometry, Is.SameAs(bootstrap.Landings.Geometry));
			Assert.That(((SegmentListGeometry)runner.WorldGeometry).Segments[0].Id, Is.EqualTo(5));
			Assert.That(WorldGeometryDigest.Of(runner.WorldGeometry), Is.EqualTo(bootstrap.Landings.Digest));
		}
	}
}
