using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The greybox follows the vessel file. Failure modes guarded: a K-class drawn at the T-class's size (or at the
	/// fallback sizes for a missing particular), and a run of another vessel keeping the previous vessel's greybox.
	/// </summary>
	public class VesselGreyboxTests
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

		private static VesselParameters Load(string id) => VesselParametersJson.Load(Path.Combine(TestSupport.RepoRoot, "vessels", id + ".json"));

		[TestCase("t-class")]
		[TestCase("k-class")]
		public void TheGreybox_IsTheVesselFilesSize(string id)
		{
			var vessel = Load(id);

			var size = GreyboxDimensions.From(vessel);

			Assert.That(size.LengthOverall, Is.EqualTo(vessel.Particulars.LengthOverall.Value));
			Assert.That(size.Beam, Is.EqualTo(vessel.Particulars.Beam.Value));
			Assert.That(size.Depth, Is.EqualTo(vessel.Particulars.Depth.Value));
			Assert.That(size.Draft, Is.EqualTo(vessel.Particulars.Draft.Value));
			Assert.That(size.DemihullBeam, Is.EqualTo(vessel.Hull.DemihullBeam.Value));
			Assert.That(size.DemihullSeparation, Is.EqualTo(vessel.Hull.DemihullSeparation.Value));
		}

		[Test]
		public void ARunOfAnotherVessel_RebuildsTheGreyboxAtItsSize()
		{
			_host = new GameObject("greybox runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			var greybox = _host.AddComponent<VesselGreybox>();
			var serialized = new UnityEditor.SerializedObject(greybox);
			serialized.FindProperty("runner").objectReferenceValue = runner;
			serialized.ApplyModifiedPropertiesWithoutUndo();
			runner.Initialize(new ScenarioSettings { vesselFile = "vessels/t-class.json" }, new ConstantControls(ControlInputs.Neutral));
			// Start, which subscribes the greybox in play mode, does not run in edit mode.
			greybox.Rebuild();
			runner.RunStarted += greybox.Rebuild;
			var tLength = _host.transform.Find(VesselGreybox.RootName + "/PortHull").localScale.z;

			runner.Initialize(new ScenarioSettings { vesselFile = "vessels/k-class.json" }, new ConstantControls(ControlInputs.Neutral));

			var hull = _host.transform.Find(VesselGreybox.RootName + "/PortHull");
			Assert.That(runner.Parameters.Id, Is.EqualTo("k-class"));
			Assert.That(hull.localScale.z, Is.EqualTo((float)Load("k-class").Particulars.LengthOverall.Value));
			Assert.That(hull.localScale.z, Is.GreaterThan(tLength));
			Assert.That(greybox.Dimensions.Beam, Is.EqualTo(Load("k-class").Particulars.Beam.Value));
		}
	}
}
