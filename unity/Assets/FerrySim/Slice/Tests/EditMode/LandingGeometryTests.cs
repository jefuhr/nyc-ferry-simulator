using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// contacts.json to contact geometry. Failure modes guarded: a reversed segment (solid on the water side)
	/// pushing the hull the wrong way, a segment whose water side the pipeline did not verify used anyway or left
	/// out without a word, ids renumbered (telemetry and debriefs name the wrong face), file order lost
	/// (determinism), a fender taken from code rather than the parameter file, a dependence on the number of
	/// segments (the data task regenerates the file), unknown fields breaking the load, and malformed or missing
	/// input producing a partial geometry instead of failing with the file and the reason.
	/// </summary>
	public class LandingGeometryTests
	{
		private const string Label = "test-contacts.json";

		/// <summary>The landing the contact tests use: South Williamsburg, an ENC outline with a pipeline-verified water side.</summary>
		public const string ContactTestStopId = "8";

		private static LandingSurfaceTable Surfaces => LandingSurfaceTable.Parse(SliceTestData.SurfacesJson(), "surfaces");

		[Test]
		public void VerifiedSegments_AreKept_InFileOrder_WithTheirIdsAndExactEndPoints()
		{
			var json = SliceTestData.Json(
				SliceTestData.Segment(7, "20", 395.03136729958675, 1623.4396393185966, 420.47475958988525, 1633.7105801271898),
				SliceTestData.Segment(3, "8", 963.0241368959863, 3771.9112337790434, 983.9753242661793, 3786.6043392838224),
				SliceTestData.Segment(1554019787, "114", 3565.19558997372, 3557.0108067955375, 3540.4006268840744, 3561.6350433948187));

			var geometry = LandingGeometry.Parse(json, Label, Surfaces);

			Assert.That(geometry.SegmentsRead, Is.EqualTo(3));
			Assert.That(geometry.Excluded, Is.Empty);
			Assert.That(geometry.Segments.Select(s => s.Id), Is.EqualTo(new[] { 7, 3, 1554019787 }));
			Assert.That(geometry.Geometry.Segments.Select(s => s.Id), Is.EqualTo(new[] { 7, 3, 1554019787 }), "the core sees the same order");
			var first = geometry.Geometry.Segments[0];
			Assert.That(first.Start.North, Is.EqualTo(395.03136729958675));
			Assert.That(first.Start.East, Is.EqualTo(1623.4396393185966));
			Assert.That(first.End.North, Is.EqualTo(420.47475958988525));
			Assert.That(first.End.East, Is.EqualTo(1633.7105801271898));
			Assert.That(geometry.Segments[1].StopId, Is.EqualTo("8"));
			Assert.That(geometry.Segments[1].StopName, Is.EqualTo("Stop 8"));
			Assert.That(geometry.Segments[1].Source, Is.EqualTo("enc-feature"));
			Assert.That(geometry.Segments[1].Confidence, Is.EqualTo("medium"));
			Assert.That(geometry.Segments[1].Verification, Is.EqualTo("needs-crew-verification"));
			Assert.That(geometry.Segments[1].FileIndex, Is.EqualTo(1));
			Assert.That(geometry.Find(3).StopId, Is.EqualTo("8"));
			Assert.That(geometry.Find(99), Is.Null);
			Assert.That(geometry.ForStop("8").Select(s => s.Id), Is.EqualTo(new[] { 3 }));
			Assert.That(geometry.ContactsFile, Is.EqualTo(Label));
			Assert.That(geometry.ContactsSha256, Has.Length.EqualTo(64));
		}

		[Test]
		public void AnUnverifiedWaterSide_IsLeftOut_AndListed()
		{
			var json = SliceTestData.Json(
				SliceTestData.Segment(1, "87", 405.596014372966, 741.9718226519191, 403.29745475542154, 743.3495803081635, waterSideVerified: false),
				SliceTestData.Segment(2, "8", 963.0, 3771.9, 984.0, 3786.6));

			var geometry = LandingGeometry.Parse(json, Label, Surfaces);

			Assert.That(geometry.Segments.Select(s => s.Id), Is.EqualTo(new[] { 2 }));
			Assert.That(geometry.Excluded, Has.Count.EqualTo(1));
			var excluded = geometry.Excluded[0];
			Assert.That(excluded.Id, Is.EqualTo(1));
			Assert.That(excluded.StopId, Is.EqualTo("87"));
			Assert.That(excluded.Reason, Does.Contain("water_side_verified_by_pipeline"));
			Assert.That(geometry.Describe(), Does.Contain("segment 1 (stop 87 Stop 87)").And.Contain("water_side_verified_by_pipeline"));
		}

		/// <summary>
		/// Coordinator schema update: crew can vouch for a water side the pipeline's chart check failed
		/// (water_side_crew_reported, optional). A segment is kept when either says so, and crew-only segments are
		/// listed apart so nobody mistakes them for pipeline-verified ones.
		/// </summary>
		[TestCase(true, null, true, "pipeline", TestName = "WaterSide_VerifiedByThePipeline_IsKept")]
		[TestCase(true, true, true, "pipeline+crew", TestName = "WaterSide_VerifiedAndCrewReported_IsKept")]
		[TestCase(false, true, true, "crew", TestName = "WaterSide_CrewReportedOnly_IsKept_AndListedApart")]
		[TestCase(null, true, true, "crew", TestName = "WaterSide_CrewReported_WithoutThePipelineField_IsKept")]
		[TestCase(false, false, false, null, TestName = "WaterSide_NeitherVerifiedNorCrewReported_IsLeftOut")]
		[TestCase(false, null, false, null, TestName = "WaterSide_UnverifiedWithoutACrewReport_IsLeftOut")]
		[TestCase(null, null, false, null, TestName = "WaterSide_WithNeitherField_IsLeftOut")]
		public void TheWaterSide_IsTrustedFromThePipelineOrTheCrew(bool? pipeline, bool? crew, bool kept, string basis)
		{
			var segment = SliceTestData.Segment(19, "19", 2311.360400129776, 4221.232459267397, 2315.871506840634, 4225.690920343873);
			segment.Remove("water_side_verified_by_pipeline");
			if (pipeline.HasValue)
			{
				segment["water_side_verified_by_pipeline"] = pipeline.Value;
			}

			if (crew.HasValue)
			{
				segment["water_side_crew_reported"] = crew.Value;
			}

			var geometry = LandingGeometry.Parse(SliceTestData.Json(segment, SliceTestData.Segment(2, "8", 963.0, 3771.9, 984.0, 3786.6)), Label, Surfaces);

			Assert.That(geometry.Find(19) != null, Is.EqualTo(kept));
			Assert.That(geometry.Find(2), Is.Not.Null, "the other landing is unaffected");
			var describe = geometry.Describe();
			if (kept)
			{
				var face = geometry.Find(19);
				Assert.That(face.WaterSideBasis, Is.EqualTo(basis));
				Assert.That(face.WaterSideVerifiedByPipeline, Is.EqualTo(pipeline == true));
				Assert.That(face.WaterSideCrewReported, Is.EqualTo(crew == true));
				Assert.That(geometry.CrewReportedOnly.Select(s => s.Id), Is.EqualTo(basis == "crew" ? new[] { 19 } : new int[0]));
				var crewSection = describe.IndexOf("Kept on the crew's report only", StringComparison.Ordinal);
				Assert.That(crewSection, Is.GreaterThanOrEqualTo(0), "the crew-only list is always shown, even when empty");
				Assert.That(describe.IndexOf("19 stop 19", crewSection, StringComparison.Ordinal) >= 0, Is.EqualTo(basis == "crew"), "crew-only faces are listed apart");
			}
			else
			{
				var excluded = geometry.Excluded.Single();
				Assert.That(excluded.Id, Is.EqualTo(19));
				Assert.That(excluded.Reason, Does.Contain("water_side_verified_by_pipeline").And.Contain("water_side_crew_reported"));
				Assert.That(geometry.CrewReportedOnly, Is.Empty);
			}
		}

		[Test]
		public void ACrewReportedSegment_IsStillCheckedAgainstItsNormal()
		{
			var segment = SliceTestData.Segment(19, "19", 2311.36, 4221.23, 2315.87, 4225.69, waterSideVerified: false);
			segment["water_side_crew_reported"] = true;
			var start = segment["start_ne_m"];
			segment["start_ne_m"] = segment["end_ne_m"];
			segment["end_ne_m"] = start;

			var geometry = LandingGeometry.Parse(SliceTestData.Json(segment), Label, Surfaces);

			Assert.That(geometry.Segments, Is.Empty);
			Assert.That(geometry.Excluded.Single().Reason, Does.Contain("water_normal_ne"));
		}

		[TestCase(180.0, false, TestName = "AReversedSegment_IsLeftOut")]
		[TestCase(90.0, false, TestName = "ANormalAlongTheFace_IsLeftOut")]
		[TestCase(5.0, false, TestName = "ANormalFiveDegreesOff_IsLeftOut")]
		[TestCase(0.5, true, TestName = "ANormalHalfADegreeOff_IsKept")]
		public void TheWaterNormal_IsCheckedAgainstStartToEnd(double degreesOff, bool kept)
		{
			var segment = SliceTestData.Segment(5, "8", 963.0, 3771.9, 984.0, 3786.6);
			var normal = (JArray)segment["water_normal_ne"];
			var angle = degreesOff * Math.PI / 180.0;
			var n = (double)normal[0];
			var e = (double)normal[1];
			segment["water_normal_ne"] = new JArray(n * Math.Cos(angle) - e * Math.Sin(angle), n * Math.Sin(angle) + e * Math.Cos(angle));

			var geometry = LandingGeometry.Parse(SliceTestData.Json(segment), Label, Surfaces);

			Assert.That(geometry.Segments.Count, Is.EqualTo(kept ? 1 : 0));
			Assert.That(geometry.Excluded.Count, Is.EqualTo(kept ? 0 : 1));
			if (!kept)
			{
				Assert.That(geometry.Excluded[0].Reason, Does.Contain("water_normal_ne").And.Contain($"{degreesOff:0.#}°"));
			}
		}

		[Test]
		public void SwappingStartAndEnd_FlipsTheSolidSide_SoTheCheckCatchesIt()
		{
			// The core's contract: solid on the right of Start to End. A segment written backwards with the
			// importer's normal would push a hull into the water side, so it must not survive.
			var segment = SliceTestData.Segment(5, "8", 963.0, 3771.9, 984.0, 3786.6);
			var start = segment["start_ne_m"];
			segment["start_ne_m"] = segment["end_ne_m"];
			segment["end_ne_m"] = start;

			var geometry = LandingGeometry.Parse(SliceTestData.Json(segment), Label, Surfaces);

			Assert.That(geometry.Segments, Is.Empty);
			Assert.That(geometry.Excluded.Single().Reason, Does.Contain("180°"));
		}

		[Test]
		public void TheCountOfSegments_DoesNotMatter_AndUnknownFieldsAreIgnored()
		{
			Assert.That(LandingGeometry.Parse("[]", Label, Surfaces).Geometry.Segments, Is.Empty, "no landings: open water, not an error");

			var many = new List<JObject>();
			for (var i = 0; i < 40; i++)
			{
				var segment = SliceTestData.Segment(1000 + i, (i % 7).ToString(), 10.0 * i, 0.0, 10.0 * i + 8.0, 1.0);
				segment["face_geometry_version"] = 3;
				segment["notes"] = new JObject { ["anything"] = new JArray(1, 2) };
				many.Add(segment);
			}

			var geometry = LandingGeometry.Parse(SliceTestData.Json(many.ToArray()), Label, Surfaces);
			Assert.That(geometry.Segments.Select(s => s.Id), Is.EqualTo(Enumerable.Range(1000, 40)));
		}

		[Test]
		public void Fenders_ComeFromTheParameterFile_PerLanding()
		{
			var json = SliceTestData.Json(
				SliceTestData.Segment(1, "8", 963.0, 3771.9, 984.0, 3786.6),
				SliceTestData.Segment(2, "20", 395.0, 1623.4, 420.5, 1633.7));
			var table = LandingSurfaceTable.Parse(SliceTestData.SurfacesJson("8", 3.0e6), "surfaces");

			var geometry = LandingGeometry.Parse(json, Label, table);

			Assert.That(geometry.Find(1).Segment.Surface.Stiffness, Is.EqualTo(3.0e6));
			Assert.That(geometry.Find(1).Surface.Key, Is.EqualTo("8"));
			Assert.That(geometry.Find(2).Segment.Surface.Stiffness, Is.EqualTo(1.0e6));
			Assert.That(geometry.Find(2).Surface.Key, Is.EqualTo(LandingSurfaceTable.DefaultKey));
			Assert.That(geometry.Find(2).Segment.Surface.MaxCompression, Is.EqualTo(table.Default.MaxCompression.Value));
			Assert.That(geometry.Surfaces, Is.SameAs(table));
		}

		[Test]
		public void TheDigest_IsTheOneRecordingsCarry()
		{
			var geometry = LandingGeometry.Parse(SliceTestData.Json(SliceTestData.Segment(1, "8", 963.0, 3771.9, 984.0, 3786.6)), Label, Surfaces);

			Assert.That(geometry.Digest, Is.EqualTo(WorldGeometryDigest.Of(geometry.Geometry)));
			Assert.That(geometry.Digest, Does.StartWith("fnv1a64:"));
			Assert.That(geometry.Describe(), Does.Contain(geometry.Digest));
		}

		private static IEnumerable<TestCaseData> MalformedFiles()
		{
			yield return Case("not JSON", "[{\"id\": 1,", "not valid JSON");
			yield return Case("an object, not a list", "{\"segments\": []}", "a JSON array");
			yield return Case("a segment that is not an object", "[42]", "segment 0");
			foreach (var field in LandingGeometry.RequiredFields)
			{
				var segment = Valid();
				segment.Remove(field);
				yield return Case("missing " + field, SliceTestData.Json(segment), "segment 0 (id " + (field == "id" ? "?" : "1") + ")", field);
			}

			yield return Case("id as text", With(s => s["id"] = "1"), "id");
			yield return Case("id zero", With(s => s["id"] = 0), "id");
			yield return Case("id fractional", With(s => s["id"] = 1.5), "id");
			yield return Case("id past int range", With(s => s["id"] = 2147483648L), "id");
			yield return Case("three coordinates", With(s => s["start_ne_m"] = new JArray(1.0, 2.0, 3.0)), "start_ne_m");
			yield return Case("coordinate as text", With(s => s["end_ne_m"] = new JArray("984.0", 3786.6)), "end_ne_m");
			yield return Case("no length", With(s => s["end_ne_m"] = s["start_ne_m"].DeepClone()), "shorter than");
			yield return Case("zero normal", With(s => s["water_normal_ne"] = new JArray(0.0, 0.0)), "water_normal_ne");
			yield return Case("normal of length 2", With(s => s["water_normal_ne"] = new JArray(2.0 * (double)s["water_normal_ne"][0], 2.0 * (double)s["water_normal_ne"][1])), "unit");
			yield return Case("water side as text", With(s => s["water_side_verified_by_pipeline"] = "true"), "water_side_verified_by_pipeline");
			yield return Case("crew report as text", With(s => s["water_side_crew_reported"] = "yes"), "water_side_crew_reported");
			yield return Case("stop id empty", With(s => s["stop_id"] = ""), "stop_id");
			yield return Case("source not text", With(s => s["source"] = 5), "source");
			yield return Case("duplicate id", SliceTestData.Json(Valid(), Valid()), "duplicate id 1");
		}

		[TestCaseSource(nameof(MalformedFiles))]
		public void AMalformedFile_FailsWithTheFileAndTheReason(string json, string reason, string detail)
		{
			var error = Assert.Throws<LandingDataException>(() => LandingGeometry.Parse(json, Label, Surfaces));
			Assert.That(error.File, Is.EqualTo(Label));
			Assert.That(error.Message, Does.StartWith(Label + ": "));
			Assert.That(error.Message, Does.Contain(reason));
			Assert.That(error.Message, Does.Contain(detail));
		}

		[Test]
		public void AMissingFile_FailsWithItsPath()
		{
			var path = Path.Combine(SliceTestData.RepoRoot, "artifacts", "no-such-contacts.json");
			var error = Assert.Throws<LandingDataException>(() => LandingGeometry.Load(path, SliceTestData.SurfacesPath));
			Assert.That(error.Message, Does.Contain(path).And.Contain("not found"));
		}

		/// <summary>
		/// The importer's real file, read independently: every segment verified neither by the pipeline nor by crew
		/// is left out and listed, every kept one agrees with its normal, and the contact tests' landing has a face. Written to
		/// artifacts/unity/slice/landing-geometry.txt for the handoff.
		/// </summary>
		[Test]
		public void TheImportersFile_LeavesOutEveryUnverifiedSegment_AndKeepsTheContactTestLanding()
		{
			var geometry = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var raw = JArray.Parse(File.ReadAllText(SliceTestData.ContactsPath));

			Assert.That(geometry.SegmentsRead, Is.EqualTo(raw.Count));
			Assert.That(geometry.Segments.Count + geometry.Excluded.Count, Is.EqualTo(raw.Count), "every segment is either kept or listed");
			foreach (JObject segment in raw)
			{
				var id = (int)segment["id"];
				var verified = segment["water_side_verified_by_pipeline"]?.Type == JTokenType.Boolean && (bool)segment["water_side_verified_by_pipeline"];
				var crew = segment["water_side_crew_reported"]?.Type == JTokenType.Boolean && (bool)segment["water_side_crew_reported"];
				if (!verified && !crew)
				{
					Assert.That(geometry.Excluded.Any(e => e.Id == id), Is.True, $"segment {id}, verified neither by the pipeline nor by crew, must be listed as excluded");
					Assert.That(geometry.Find(id), Is.Null);
				}
				else if (!verified)
				{
					Assert.That(geometry.CrewReportedOnly.Any(s => s.Id == id) || geometry.Excluded.Any(e => e.Id == id), Is.True, $"crew-only segment {id} is listed");
				}
			}

			foreach (var kept in geometry.Segments)
			{
				var rawSegment = (JObject)raw.First(s => (int)s["id"] == kept.Id);
				var normal = kept.Segment.WaterNormal;
				var cos = normal.North * (double)rawSegment["water_normal_ne"][0] + normal.East * (double)rawSegment["water_normal_ne"][1];
				Assert.That(cos, Is.GreaterThan(Math.Cos(LandingGeometry.NormalToleranceDegrees * Math.PI / 180.0)), $"segment {kept.Id}");
				Assert.That(kept.Segment.Surface.Stiffness, Is.EqualTo(geometry.Surfaces.For(kept.StopId).Surface.Stiffness));
			}

			Assert.That(geometry.ForStop(ContactTestStopId), Is.Not.Empty, "South Williamsburg needs a valid face for the contact tests");
			var report = Path.Combine(SliceTestData.RepoRoot, "artifacts", "unity", "slice");
			Directory.CreateDirectory(report);
			File.WriteAllText(Path.Combine(report, "landing-geometry.txt"), geometry.Describe());
			Debug.Log(geometry.Describe());
		}

		private static JObject Valid() => SliceTestData.Segment(1, "8", 963.0, 3771.9, 984.0, 3786.6);

		private static string With(Action<JObject> mutate)
		{
			var segment = Valid();
			mutate(segment);
			return SliceTestData.Json(segment);
		}

		private static TestCaseData Case(string name, string json, string reason, string detail = "") =>
			new TestCaseData(json, reason, detail).SetName("AMalformedFile_FailsWithTheFileAndTheReason(" + name + ")");
	}
}
