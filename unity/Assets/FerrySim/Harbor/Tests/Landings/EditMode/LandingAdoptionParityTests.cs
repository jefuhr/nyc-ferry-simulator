using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests.Landings
{
	/// <summary>Adoption must preserve legacy face references and every replay state at unmoved landings.</summary>
	public class LandingAdoptionParityTests
	{
		private static string Root => LandingVideoScenarioTests.Root;
		private static string BeforeJson()
		{
			var file = Path.Combine(Root, "unity/Assets/FerrySim/Harbor/Tests/Landings/EditMode/landing-adoption-before.json.gz");
			using var source = File.OpenRead(file);
			using var gzip = new GZipStream(source, CompressionMode.Decompress);
			using var bytes = new MemoryStream();
			gzip.CopyTo(bytes);
			using var hash = SHA256.Create();
			Assert.That(BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant(),
				Is.EqualTo("0ff8e977a49b72bf77837148067ab25f60a0f154b0855b120caf0e2d050e285e"), "original shipped contacts, not an updated golden");
			return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
		}

		private static LandingGeometry Geometry(string json) => LandingGeometry.Parse(json, "adoption contacts",
			LandingSurfaceTable.Load(Path.Combine(Root, "unity/Assets/FerrySim/Harbor/Data/landing-contact-surfaces.json")));
		private static string AfterJson() => File.ReadAllText(Path.Combine(Root, "data/processed/harbor/unity/contacts.json"));

		[Test]
		public void EveryShippedBerthIdStillResolves_AndStop17KeepsItsOriginalFace()
		{
			var before = Geometry(BeforeJson());
			var after = Geometry(AfterJson());
			foreach (var stop in JArray.Parse(BeforeJson()).Select(s => (string)s["stop_id"]).Distinct())
			foreach (var berth in before.Berths(stop))
			{
				Assert.That(after.Berths(stop).Any(b => b.Id == berth.Id), Is.True, stop + " " + berth.Id);
				if (stop == "17") Assert.That(after.Berths(stop).Single(b => b.Id == berth.Id).SegmentIds, Is.EqualTo(berth.SegmentIds));
			}
		}

		[TestCase("87", "t-class")]
		[TestCase("87", "k-class")]
		[TestCase("111", "t-class")]
		[TestCase("111", "k-class")]
		public void UnmovedLanding_ReplaysTheSameControlsWithIdenticalStateDigest(string stop, string hull)
		{
			var before = Geometry(BeforeJson()); var after = Geometry(AfterJson());
			var vesselFile = Path.Combine(Root, "vessels", hull + ".json");
			var vessel = VesselParametersJson.Load(vesselFile);
			var oldStart = LandingApproach.BowOn(before.BerthFace(stop), vessel, 4, "Adoption parity", vesselFile, "2025-07-16T16:18:00Z");
			var newStart = LandingApproach.BowOn(after.BerthFace(stop), vessel, 4, "Adoption parity", vesselFile, "2025-07-16T16:18:00Z");
			Assert.That(newStart.StartPosition.North, Is.EqualTo(oldStart.StartPosition.North));
			Assert.That(newStart.StartPosition.East, Is.EqualTo(oldStart.StartPosition.East));
			Assert.That(newStart.StartHeading, Is.EqualTo(oldStart.StartHeading));
			var folder = Path.Combine(Root, "artifacts/landing-adopt/unmoved-replays"); Directory.CreateDirectory(folder);
			var trace = new ControlTrace(); trace.Append(0, new ControlInputs(.15, .15, 0)); trace.Append(12000, ControlInputs.Neutral);
			var traceFile = Path.Combine(folder, stop + "-" + hull + "-controls.csv");
			using (var writer = new StreamWriter(traceFile)) trace.Write(writer);
			var replay = ControlTrace.Load(traceFile);
			var initial = VesselState.Planar(0, oldStart.StartPosition.North, oldStart.StartPosition.East, oldStart.StartHeading);
			string Run(LandingGeometry geometry)
			{
				// Controlled calm-water landing isolates contact adoption from an unrelated weather change.
				var sim = new VesselDynamicsSimulation(vessel, UniformEnvironment.Calm, geometry.Geometry, initial);
				var digest = new StateDigest(); var touched = false;
				for (var step = 0; step < replay.EndStep; step++)
				{
					sim.Step(replay.At(step)); digest.Add(sim.State);
					touched |= sim.ContactCount > 0;
					Assert.That(sim.State.IsFinite(), Is.True);
				}
				Assert.That(touched, Is.True, "the parity replay must land, not just traverse open water");
				return digest.Text;
			}
			var oldDigest = Run(before); var newDigest = Run(after);
			File.WriteAllText(Path.Combine(folder, stop + "-" + hull + "-digests.txt"), "before " + oldDigest + "\nafter " + newDigest + "\nsteps 12000\ncontact true\nenvironment controlled calm\n");
			Assert.That(newDigest, Is.EqualTo(oldDigest));
		}

		[Test]
		public void ExistingUnmovedFaces_KeepTheirExactStartPosesForBothHulls()
		{
			var changed = new[] { "4", "18", "25", "89", "90", "112", "113", "115", "118", "138", "141", "24", "114" };
			var before = Geometry(BeforeJson()); var after = Geometry(AfterJson());
			foreach (var stop in JArray.Parse(BeforeJson()).Select(s => (string)s["stop_id"]).Distinct().Where(s => !changed.Contains(s)))
			foreach (var berth in before.Berths(stop))
			{
				if (!berth.SegmentIds.All(id => before.Find(id)?.StartFaceEligible == true)) continue;
				foreach (var hull in new[] { "t-class", "k-class" })
				{
					var file = Path.Combine(Root, "vessels", hull + ".json");
					var vessel = VesselParametersJson.Load(file);
					var oldPose = LandingApproach.BowOn(before.BerthFace(stop, berth.Id), vessel, 4, "Unmoved face", file, "2025-07-16T16:18:00Z");
					var newPose = LandingApproach.BowOn(after.BerthFace(stop, berth.Id), vessel, 4, "Unmoved face", file, "2025-07-16T16:18:00Z");
					Assert.That(new[] { newPose.StartPosition.North, newPose.StartPosition.East, newPose.StartHeading },
						Is.EqualTo(new[] { oldPose.StartPosition.North, oldPose.StartPosition.East, oldPose.StartHeading }), stop + " " + berth.Id + " " + hull);
				}
			}
		}

		[Test, Explicit("Requires the original-contact scenario root under artifacts/landing-adopt; reports failures without tuning.")]
		public void CaptionScenarios_CompareOriginalAndAdoptedWithoutTuning()
		{
			var folder = Path.Combine(Root, "artifacts/landing-adopt");
			var originalRoot = Path.Combine(folder, "scenario-before-root");
			Assert.That(File.ReadAllText(Path.Combine(originalRoot, "data/processed/harbor/unity/contacts.json")), Is.EqualTo(BeforeJson()));
			var results = new JArray();
			foreach (var scenario in LandingVideoScenario.LoadDirectory(Path.Combine(Root, "unity/Assets/FerrySim/Harbor/Scenarios/landings")))
			{
				var before = scenario.Run(originalRoot, verifyReplay: true);
				var after = scenario.Run(Root, verifyReplay: true);
				results.Add(new JObject { ["id"] = scenario.Id, ["before"] = before, ["after"] = after });
				File.WriteAllText(Path.Combine(folder, "scenario-adoption-results.json"), results.ToString() + "\n");
				if ((string)after["verdict"] != "blocked") Assert.That((bool)after["replay_matches"], Is.True, scenario.Id);
				if (new[] { "23", "20" }.Contains((string)scenario.Data["mapping"]["stop_id"]))
				{
					Assert.That((string)after["state_digest"], Is.EqualTo((string)before["state_digest"]), scenario.Id + " unchanged scenario digest");
					Assert.That((string)after["verdict"], Is.EqualTo((string)before["verdict"]), scenario.Id + " unchanged scenario verdict");
				}
			}
		}

		[TestCase("astoria")]
		[TestCase("soundview")]
		[TestCase("astoria-return")]
		[TestCase("astoria-busy")]
		public void JourneyArrivalFaceReferencesMatchTheAdoptedFace(string name)
		{
			var routes = JObject.Parse(File.ReadAllText(Path.Combine(Root, "tools/harbor-benchmark/routes.json")));
			var route = routes["routes"].Single(r => (string)r["name"] == name);
			var face = Geometry(AfterJson()).BerthFace((string)route["arrival_stop"]);
			var middle = (face.Start + face.End) * .5;
			var along = face.End - face.Start;
			var source = route["arrival_face"];
			Assert.That(source["segment_ids"].Values<int>(), Is.EqualTo(face.Segments.Select(s => s.Id)), name);
			Assert.That((double)source["middle"][0], Is.EqualTo(middle.North).Within(1e-6), name + " north");
			Assert.That((double)source["middle"][1], Is.EqualTo(middle.East).Within(1e-6), name + " east");
			Assert.That((double)source["normal"][0], Is.EqualTo(along.East / along.Magnitude).Within(1e-12), name + " normal north");
			Assert.That((double)source["normal"][1], Is.EqualTo(-along.North / along.Magnitude).Within(1e-12), name + " normal east");
		}
	}
}
