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
	/// geometry quietly failed, an error that does not say which file is wrong, a malformed number escaping the
	/// loader as another exception so the runner is never stopped (Astra review finding 1), and code that starts the
	/// runner after a failed load getting a stepping run.
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

		/// <summary>The review's first input: an id past Int64, as the reviewer wrote it.</summary>
		[UnityTest]
		public IEnumerator TheReviewsIdPastInt64_StopsTheRunner_NamingTheFileAndTheReason()
		{
			var (runner, bootstrap) = Build(new TextAsset("[{\"id\":9223372036854775808}]"), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Error, new Regex(@"did not load.*test/contacts\.json: segment 0 \(id \?\): stop_id missing"));
			_host.SetActive(true);
			yield return null;

			yield return AssertFailedClosed(runner, bootstrap, "stop_id missing");
		}

		/// <summary>The same id in an otherwise whole segment: the reason is the id.</summary>
		[UnityTest]
		public IEnumerator AWholeSegmentWithAnIdPastInt64_StopsTheRunner_NamingTheId()
		{
			var (runner, bootstrap) = Build(new TextAsset(Segment("9223372036854775808", "[963.0, 3771.9]", "[984.0, 3786.6]")), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Error, new Regex(@"did not load.*test/contacts\.json: segment 0 \(id \?\): id must be a whole number from 1 to 2147483647"));
			_host.SetActive(true);
			yield return null;

			yield return AssertFailedClosed(runner, bootstrap, "id must be a whole number from 1 to 2147483647");
		}

		/// <summary>The review's second input: finite end points whose difference overflows.</summary>
		[UnityTest]
		public IEnumerator TheReviewsEndPointsPlusMinus1e308_StopTheRunner_NamingTheFileAndTheReason()
		{
			var (runner, bootstrap) = Build(new TextAsset(Segment("5", "[1e308, 1e308]", "[-1e308, -1e308]")), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Error, new Regex(@"did not load.*test/contacts\.json: segment 0 \(id 5\): .*not a finite length"));
			_host.SetActive(true);
			yield return null;

			yield return AssertFailedClosed(runner, bootstrap, "not a finite length");
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
			var (runner, bootstrap) = Build(new TextAsset(Segment("5", "[963.0, 3771.9]", "[984.0, 3786.6]")), new TextAsset(Surfaces));
			LogAssert.Expect(LogType.Warning, new Regex("no terrain prefab"));
			LogAssert.Expect(LogType.Warning, new Regex("has no scenario"));
			_host.SetActive(true);
			yield return null;

			Assert.That(bootstrap.LoadError, Is.Null);
			Assert.That(runner.WorldGeometry, Is.SameAs(bootstrap.Landings.Geometry));
			Assert.That(((SegmentListGeometry)runner.WorldGeometry).Segments[0].Id, Is.EqualTo(5));
			Assert.That(WorldGeometryDigest.Of(runner.WorldGeometry), Is.EqualTo(bootstrap.Landings.Digest));
		}

		/// <summary>One South Williamsburg segment as the importer writes it, with the given id and end points (JSON text).</summary>
		private static string Segment(string id, string start, string end) =>
			"[{\"id\": " + id + ", \"stop_id\": \"8\", \"stop_name\": \"South Williamsburg\", \"start_ne_m\": " + start + ", \"end_ne_m\": " + end + ", " +
			"\"water_normal_ne\": [0.5734623443633283, -0.8192319205190405], \"source\": \"enc-feature\", \"confidence\": \"medium\", " +
			"\"verification\": \"needs-crew-verification\", \"water_side_verified_by_pipeline\": true}]";

		/// <summary>
		/// Fail closed: the runner is stopped and never initialized, and a run that code starts anyway is ended before
		/// its first step, with the reason kept.
		/// </summary>
		private static IEnumerator AssertFailedClosed(VesselSimulationRunner runner, SliceBootstrap bootstrap, string reason)
		{
			Assert.That(runner.enabled, Is.False, "the vessel does not run without its landings");
			Assert.That(runner.IsInitialized, Is.False);
			Assert.That(bootstrap.Landings, Is.Null);
			Assert.That(bootstrap.LoadError, Does.StartWith("test/contacts.json: ").And.Contain(reason));
			yield return null;
			Assert.That(runner.IsInitialized, Is.False, "nothing starts it a frame later");

			runner.RecordRuns = false;
			LogAssert.Expect(LogType.Error, new Regex(@"stopped before its first step.*test/contacts\.json"));
			runner.Initialize(new ScenarioSettings { name = "after a failed landing load", scenarioStartUtc = "2026-06-15T16:00:00Z", environment = new ScenarioEnvironment { calm = true } },
				new ConstantControls(new ControlInputs(0.15, 0.15, 0.0)));
			var advance = runner.AdvanceFrame(0.1);

			Assert.That(advance.Steps, Is.EqualTo(0), "no physics step");
			Assert.That(runner.Driver.StepCount, Is.EqualTo(0));
			Assert.That(runner.IsRunActive, Is.False);
			Assert.That(runner.HasTelemetry, Is.False, "no live telemetry");
			Assert.That(bootstrap.StartRefusal, Does.Contain("test/contacts.json"), "the reason is kept");
		}
	}
}
