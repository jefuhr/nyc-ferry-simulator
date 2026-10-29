using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Sim.Tests
{
	/// <summary>Failure modes: stale vessel bytes accepted, replay starts before digest checks, and legacy recordings rejected.</summary>
	public class VesselDigestTests
	{
		[TestCase(false)]
		[TestCase(true)]
		public void FileBytesAreRecorded_AndMismatchRefusesBothReplayPathsBeforeStepping(bool malformed)
		{
			var folder = TestSupport.FreshArtifactFolder("vessel-digest");
			var vesselPath = Path.Combine(folder, "vessel.json");
			File.Copy(Path.Combine(TestSupport.RepoRoot, "vessels/t-class.json"), vesselPath);
			var settings = new ScenarioSettings
			{
				vesselFile = vesselPath
			};
			var obj = new GameObject("digest-test");
			try
			{
				var runner = obj.AddComponent<VesselSimulationRunner>();
				runner.RecordingDirectory = folder;
				runner.Initialize(settings, new ConstantControls(ControlInputs.Neutral));
				runner.AdvanceFrame(0.02);
				runner.EndRun();
				var run = RecordedRun.Load(runner.LastRecordingPath);
				Assert.That(run.Header["vessel_sha256"], Is.EqualTo(Hash(vesselPath)));
				var original = Hash(vesselPath);
				File.AppendAllText(vesselPath, malformed ? "\nnot JSON" : "\n");
				var changed = Hash(vesselPath);
				var result = ReplayPlayer.Verify(run, TestSupport.LoadTClass(), new VesselDynamicsFactory());
				Assert.That(result.Matches, Is.False);
				Assert.That(result.RowsCompared, Is.Zero);
				Assert.That(result.ToString(), Does.Contain(original).And.Contain(changed));
				runner.RecordRuns = false;
				runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral));
				var id = runner.RunId;
				var error = Assert.Throws<InvalidOperationException>(() => runner.InitializeReplay(run));
				Assert.That(error.Message, Does.Contain(original).And.Contain(changed));
				Assert.That(runner.RunId, Is.EqualTo(id));
				Assert.That(runner.AdvanceFrame(0.02).Steps, Is.EqualTo(2));
				// Legacy recordings keep their existing verification behavior even after a byte-only whitespace change.
				File.WriteAllLines(run.TelemetryPath, File.ReadAllLines(run.TelemetryPath).Where(l => !l.StartsWith("# vessel_sha256:", StringComparison.Ordinal)));
				Assert.That(ReplayPlayer.Verify(RecordedRun.Load(run.TelemetryPath), TestSupport.LoadTClass(), new VesselDynamicsFactory()).Matches, Is.True);
				File.Delete(vesselPath);
				var legacy = RecordedRun.Load(run.TelemetryPath);
				Assert.DoesNotThrow(() => runner.InitializeReplay(legacy, TestSupport.LoadTClass()), "legacy callers can supply parameters without a file, as before");
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(obj);
			}
		}

		private static string Hash(string path)
		{
			using var sha = SHA256.Create();
			return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
		}
	}
}
