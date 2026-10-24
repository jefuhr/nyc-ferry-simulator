using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// Recordings and replays use the same landing faces. Failure modes guarded: a recording that does not say
	/// which faces it ran against, a replay against other faces that silently diverges at the first contact
	/// (through <see cref="ReplayPlayer.Verify"/> or the runner's in-scene replay), and older recordings without
	/// the header no longer replaying.
	/// </summary>
	public class GeometryIdentityTests
	{
		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		private static ScenarioSettings OffSouthWilliamsburg() => new ScenarioSettings
		{
			name = "geometry-identity",
			vesselFile = "vessels/t-class.json",
			startLatitude = 40.7095,
			startLongitude = -73.9725,
			startHeadingDegrees = 125.0,
			startSpeedKnots = 2.0,
			scenarioStartUtc = "2026-06-15T16:00:00Z",
			environment = new ScenarioEnvironment { calm = true },
		};

		private string Record(IWorldGeometry geometry, VesselParameters vessel, string folder)
		{
			_host = new GameObject("geometry identity runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordingDirectory = folder;
			runner.WorldGeometry = geometry;
			runner.Initialize(OffSouthWilliamsburg(), new ConstantControls(new ControlInputs(0.15, 0.15, 0.0)), vessel);
			for (var frame = 0; frame < 150; frame++)
			{
				runner.AdvanceFrame(0.02);
			}

			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			return runner.LastRecordingPath;
		}

		[Test]
		public void ARecording_CarriesItsGeometryDigest_AndReplaysAgainstTheSameFaces()
		{
			var vessel = SliceTestData.LoadTClass();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var path = Record(landings.Geometry, vessel, SliceTestData.ArtifactFolder("identity-same"));

			var run = RecordedRun.Load(path);
			Assert.That(run.Header[WorldGeometryDigest.HeaderKey], Is.EqualTo(landings.Digest));
			Assert.That(run.Header["world_geometry"], Is.EqualTo(typeof(SegmentListGeometry).FullName));
			var result = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), null, landings.Geometry);
			Assert.That(result.Matches, Is.True, result.ToString());
		}

		[Test]
		public void AnOpenWaterRecording_SaysNone()
		{
			var path = Record(EmptyWorldGeometry.Instance, SliceTestData.LoadTClass(), SliceTestData.ArtifactFolder("identity-open-water"));

			Assert.That(RecordedRun.Load(path).Header[WorldGeometryDigest.HeaderKey], Is.EqualTo("none"));
		}

		[Test]
		public void AReplayAgainstOtherFaces_IsRefused_NamingBothDigests()
		{
			var vessel = SliceTestData.LoadTClass();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var run = RecordedRun.Load(Record(landings.Geometry, vessel, SliceTestData.ArtifactFolder("identity-other")));
			var fewer = new SegmentListGeometry(landings.Geometry.Segments.Skip(1));
			var fewerDigest = WorldGeometryDigest.Of(fewer);

			var refused = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), null, fewer);
			Assert.That(refused.Matches, Is.False);
			Assert.That(refused.RowsCompared, Is.EqualTo(0), "nothing is replayed against the wrong faces");
			Assert.That(refused.ToString(), Does.Contain(landings.Digest).And.Contain(fewerDigest));

			var openWater = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory());
			Assert.That(openWater.Matches, Is.False, "omitting the geometry means open water, which is not what was recorded");
			Assert.That(openWater.ToString(), Does.Contain(landings.Digest).And.Contain("none"));
		}

		[Test]
		public void TheRunnersReplay_AgainstOtherFaces_Throws_NamingBothDigests()
		{
			var vessel = SliceTestData.LoadTClass();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var run = RecordedRun.Load(Record(landings.Geometry, vessel, SliceTestData.ArtifactFolder("identity-runner")));
			var runner = _host.GetComponent<VesselSimulationRunner>();
			var fewer = new SegmentListGeometry(landings.Geometry.Segments.Skip(1));
			runner.WorldGeometry = fewer;

			var error = Assert.Throws<InvalidOperationException>(() => runner.InitializeReplay(run, vessel));
			Assert.That(error.Message, Does.Contain(landings.Digest).And.Contain(WorldGeometryDigest.Of(fewer)));

			runner.WorldGeometry = landings.Geometry;
			runner.InitializeReplay(run, vessel);
			Assert.That(runner.IsReplaying, Is.True, "the same faces replay");
		}

		[Test]
		public void ARecordingWithoutTheHeader_StillReplays()
		{
			var vessel = SliceTestData.LoadTClass();
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var path = Record(landings.Geometry, vessel, SliceTestData.ArtifactFolder("identity-legacy"));
			var lines = File.ReadAllLines(path).Where(line => !line.StartsWith("# " + WorldGeometryDigest.HeaderKey + ":", StringComparison.Ordinal)).ToArray();
			File.WriteAllLines(path, lines);

			var run = RecordedRun.Load(path);
			Assert.That(run.Header.ContainsKey(WorldGeometryDigest.HeaderKey), Is.False);
			var result = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), null, landings.Geometry);
			Assert.That(result.Matches, Is.True, result.ToString());
		}
	}
}
