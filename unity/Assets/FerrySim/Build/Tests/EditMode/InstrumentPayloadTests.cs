using System;
using System.IO;
using System.Linq;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Build.Tests
{
	public class InstrumentPayloadTests
	{
		[TestCase("t-class"), TestCase("k-class")]
		public void PlayerRootsIncludeTheRunningVesselsInstrumentFile(string id)
		{
			var roots = JObject.Parse(File.ReadAllText(Path.Combine(PlayerPayload.RepoRoot, HarborPayload.Configuration)))["roots"].Values<string>().ToArray();
			var instrument = "vessels/instruments/" + id + ".json";
			Assert.That(roots, Does.Contain(instrument));
			var folder = Path.Combine(Path.GetTempPath(), "ferrysim-instrument-player-" + Guid.NewGuid().ToString("N"));
			var host = new GameObject("Player-path runner"); host.SetActive(false);
			try
			{
				var streaming = PlayerPayload.StreamingAssetsFolder(UnityEditor.BuildTarget.StandaloneWindows64, Path.Combine(folder, "Ferry.exe"));
				PlayerPayload.Package(PlayerPayload.RepoRoot, new[] { "vessels/" + id + ".json", instrument }, streaming, new PlayerBuildInfo());
				var runner = host.AddComponent<VesselSimulationRunner>(); runner.enabled = false;
				runner.RecordRuns = false;
				runner.InstrumentParametersFile = Path.Combine(streaming, instrument);
				runner.Initialize(new ScenarioSettings { vesselFile = Path.Combine(streaming, "vessels/" + id + ".json") });
				runner.Resume(); runner.AdvanceFrame(0.02);
				Assert.That(runner.HasInstruments, Is.True, "the runner loaded the packaged instrument file from a player-style path");
				Assert.That(runner.Instruments.AlarmCount, Is.GreaterThan(0));
				runner.EndRun();
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(host);
				if (Directory.Exists(folder)) Directory.Delete(folder, true);
			}
		}
	}
}
