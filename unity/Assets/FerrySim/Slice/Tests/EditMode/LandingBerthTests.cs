using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	// Failure modes: ring/file order mistaken for face order, omitted subdivisions accepted,
	// inconsistent berth metadata, or an unknown berth silently selecting the primary face.
	public class LandingBerthTests
	{
		private static JObject Segment(int id, double start, double end)
		{
			var item = SliceTestData.Segment(id, "87", 0, start, 0, end);
			item["primary_face"] = true;
			item["berth_id"] = "enc:EAST";
			item["berth_face"] = true;
			item["berth_name"] = "Outer pontoon (east)";
			item["berth_order"] = 0;
			item["berth_face_segment_ids"] = new JArray(11, 12);
			return item;
		}

		private static LandingGeometry Parse(params JObject[] records) => LandingGeometry.Parse(SliceTestData.Json(records), "berths.json",
			LandingSurfaceTable.Parse(SliceTestData.SurfacesJson(), "surfaces"));

		[Test]
		public void Face_UsesExplicitSubdivisionOrder_AndNullMeansDefault()
		{
			var geometry = Parse(Segment(12, 15, 30), Segment(11, 0, 15));
			Assert.That(geometry.Berths("87").Single().Name, Is.EqualTo("Outer pontoon (east)"));
			Assert.That(geometry.BerthFace("87", null).Segments.Select(s => s.Id), Is.EqualTo(new[] { 11, 12 }));
			Assert.That(() => geometry.BerthFace("87", "missing"), Throws.TypeOf<LandingDataException>().With.Message.Contains("stop 87 berth missing"));
		}

		[Test]
		public void APartiallyExcludedOrDisconnectedFace_IsRefusedWithStopAndBerth()
		{
			var second = Segment(12, 15, 30);
			second["water_side_verified_by_pipeline"] = false;
			var geometry = Parse(Segment(11, 0, 15), second);
			Assert.That(() => geometry.BerthFace("87", "enc:EAST"), Throws.TypeOf<LandingDataException>().With.Message.Contains("stop 87 berth enc:EAST"));
			geometry = Parse(Segment(11, 0, 15), Segment(12, 16, 30));
			Assert.That(() => geometry.BerthFace("87", null), Throws.TypeOf<LandingDataException>().With.Message.Contains("join"));
		}

		private static JObject CrewBerth()
		{
			var item = Segment(11, 0, 30);
			item["berth_face_segment_ids"] = new JArray(11);
			item["water_side_verified_by_pipeline"] = false;
			item["berth_use_crew_note"] = "current";
			item["berth_use_evidence"] = JObject.Parse("{ 'crew_notes_sha256':'" + new string('a', 64) + "', 'note': {'id':'current','kind':'berth-use-policy','berths':[{'id':'enc:EAST'}]} }");
			item["berth_side_evidence"] = JObject.Parse("{'verified':true,'outward_reports':3,'inward_reports':0}");
			item["solid_side_evidence"] = JObject.Parse("{'verified':true,'winding_check':true,'probe_offset_m':0.5,'probe_in_charted_water':true,'probe_water_layer':'UNSARE','probe_in_lndare':false,'probe_in_other_structure':false}");
			return item;
		}

		[Test]
		public void PrimaryCrewUseBerth_HasItsOwnHonestBasis_WithoutClaimingWaterSideProof()
		{
			var geometry = Parse(CrewBerth());
			var face = geometry.BerthFace("87");
			Assert.That(face.Segments[0].WaterSideBasis, Is.EqualTo("solid-side + crew berth use (water side unsurveyed: UNSARE)"));
			Assert.That(face.Segments[0].WaterSideVerifiedByPipeline || face.Segments[0].WaterSideCrewReported, Is.False);
		}

		[TestCase("structure")]
		[TestCase("land")]
		[TestCase("no-outward")]
		[TestCase("more-inward")]
		[TestCase("old-note")]
		[TestCase("unlisted")]
		[TestCase("depare")]
		public void PrimaryBerthException_RequiresEveryPieceOfEvidence(string change)
		{
			var item = CrewBerth();
			if (change == "structure") item["solid_side_evidence"]["probe_in_other_structure"] = true;
			if (change == "land") item["solid_side_evidence"]["probe_in_lndare"] = true;
			if (change == "no-outward") item["berth_side_evidence"]["outward_reports"] = 0;
			if (change == "more-inward") item["berth_side_evidence"]["inward_reports"] = 4;
			if (change == "old-note") item["berth_use_crew_note"] = "old";
			if (change == "unlisted") item["berth_use_evidence"]["note"]["berths"][0]["id"] = "enc:OTHER";
			if (change == "depare") item["solid_side_evidence"]["probe_water_layer"] = "DEPARE";
			Assert.That(() => Parse(item).BerthFace("87"), Throws.TypeOf<LandingDataException>());
		}

		[Test]
		public void SelectableDefault_CannotOmitAnAdditionalPrimarySubdivision()
		{
			var extra = SliceTestData.Segment(13, "87", 0, 30, 0, 40);
			extra["primary_face"] = true;
			Assert.That(() => Parse(Segment(11, 0, 15), Segment(12, 15, 30), extra),
				Throws.TypeOf<LandingDataException>().With.Message.Contains("unmarked primary"));
		}

		[Test]
		public void ConflictingMetadata_IsNotSilentlyTakenFromTheFirstSegment()
		{
			var second = Segment(12, 15, 30);
			second["berth_name"] = "different";
			Assert.That(() => Parse(Segment(11, 0, 15), second), Throws.TypeOf<LandingDataException>().With.Message.Contains("inconsistent"));
		}
	}
}
