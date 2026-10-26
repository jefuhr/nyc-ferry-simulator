using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// contacts.json version 2: whole landing structures. Failure modes guarded: a version 1 file no longer loading,
	/// the new kinds or berth ids dropped, a segment kept on its charted solid side counted as crew-reported, a
	/// primary face kept on the solid-side path alone (its eligibility must not change), a solid-side proof that
	/// failed used anyway, malformed new fields producing a partial geometry instead of failing with the file and
	/// the reason, a landing's own fender leaking onto the piers and bulkheads beside it, and a kind without a
	/// fender entry failing instead of using the default.
	/// </summary>
	public class LandingGeometryV2Tests
	{
		private const string Label = "test-contacts-v2.json";

		private static LandingSurfaceTable Surfaces => LandingSurfaceTable.Parse(SliceTestData.SurfacesJson(), "surfaces");

		private static JObject Kind(JObject segment, string kind, string berthId = null, bool? solidSide = null)
		{
			segment["kind"] = kind;
			if (berthId != null)
			{
				segment["berth_id"] = berthId;
			}

			if (solidSide.HasValue)
			{
				segment["solid_side_evidence"] = new JObject { ["verified"] = solidSide.Value, ["method"] = "enc-closed-outline", ["layer"] = "LNDARE", ["lnam"] = "LAND1" };
			}

			return segment;
		}

		[Test]
		public void AVersion2File_ExposesKindsAndBerths_AndAVersion1FileStillLoads()
		{
			var primary = Kind(SliceTestData.Segment(11, "87", 0.0, 0.0, 0.0, 30.0), "berth-face", "enc:SOUTH", true);
			primary["primary_face"] = true;
			var side = Kind(SliceTestData.Segment(12, "87", 0.0, 30.0, -10.0, 30.0), "barge-end", "enc:SOUTH", true);
			var pier = Kind(SliceTestData.Segment(13, "87", 50.0, 0.0, 50.0, 60.0), "pier", solidSide: true);
			var geometry = LandingGeometry.Parse(SliceTestData.Json(primary, side, pier), Label, Surfaces);

			Assert.That(geometry.Segments.Select(s => s.Kind), Is.EqualTo(new[] { "berth-face", "barge-end", "pier" }));
			Assert.That(geometry.Segments.Select(s => s.BerthId), Is.EqualTo(new[] { "enc:SOUTH", "enc:SOUTH", null }));
			Assert.That(geometry.PrimaryFace("87").Segments.Select(s => s.Id), Is.EqualTo(new[] { 11 }));

			var version1 = LandingGeometry.Parse(SliceTestData.Json(SliceTestData.Segment(7, "20", 0.0, 0.0, 0.0, 30.0)), Label, Surfaces);
			Assert.That(version1.Segments.Single().Kind, Is.Null);
			Assert.That(version1.Segments.Single().BerthId, Is.Null);
			Assert.That(version1.Segments.Single().Surface.Key, Is.EqualTo(LandingSurfaceTable.DefaultKey));
		}

		[TestCase(false, false, true, true, "solid-side", TestName = "SolidSide_KeepsAStructureSegment_TheChartTestAndCrewDidNot")]
		[TestCase(false, false, true, false, null, TestName = "SolidSide_FailedProof_IsLeftOut")]
		[TestCase(true, false, true, true, null, TestName = "SolidSide_NeverKeepsAPrimaryFaceSegment")]
		[TestCase(false, true, false, true, "pipeline", TestName = "SolidSide_DoesNotChangeTheChartTestBasis")]
		[TestCase(false, false, false, false, null, TestName = "SolidSide_AbsentOnAVersion1Segment_IsLeftOut")]
		public void TheSolidSidePath_KeepsOnlyNonPrimarySegmentsWithAVerifiedProof(bool primary, bool pipeline, bool hasProof, bool verified, string basis)
		{
			var segment = SliceTestData.Segment(21, "19", 0.0, 0.0, 0.0, 30.0, waterSideVerified: pipeline);
			segment["kind"] = primary ? "berth-face" : "pier";
			if (primary)
			{
				segment["primary_face"] = true;
			}

			if (hasProof)
			{
				segment["solid_side_evidence"] = new JObject { ["verified"] = verified, ["method"] = "enc-closed-outline", ["layer"] = "LNDARE", ["lnam"] = "LAND1" };
			}

			var geometry = LandingGeometry.Parse(SliceTestData.Json(segment, SliceTestData.Segment(2, "8", 963.0, 3771.9, 984.0, 3786.6)), Label, Surfaces);

			Assert.That(geometry.Find(21) != null, Is.EqualTo(basis != null));
			if (basis != null)
			{
				Assert.That(geometry.Find(21).WaterSideBasis, Is.EqualTo(basis));
				Assert.That(geometry.Find(21).SolidSideVerified, Is.EqualTo(hasProof && verified));
				Assert.That(geometry.CrewReportedOnly, Is.Empty, "the solid side is not a crew report");
				Assert.That(geometry.SolidSideOnly.Select(s => s.Id), Is.EqualTo(basis == "solid-side" ? new[] { 21 } : new int[0]));
				Assert.That(geometry.Describe(), Does.Contain("Kept without water-side proof on charted solid geometry"));
			}
			else
			{
				var excluded = geometry.Excluded.Single();
				Assert.That(excluded.Id, Is.EqualTo(21));
				Assert.That(excluded.Reason, Does.Contain(primary ? "primary face" : "solid_side_evidence"));
			}
		}

		[TestCase("kind", 5, "kind must be non-empty text")]
		[TestCase("kind", "", "kind must be non-empty text")]
		[TestCase("berth_id", 7, "berth_id must be text")]
		[TestCase("solid_side_evidence", "yes", "solid_side_evidence must be an object")]
		public void AMalformedVersion2Field_FailsWithTheFileAndTheReason(string field, object value, string reason)
		{
			var segment = SliceTestData.Segment(1, "8", 963.0, 3771.9, 984.0, 3786.6);
			segment[field] = JToken.FromObject(value);

			var error = Assert.Throws<LandingDataException>(() => LandingGeometry.Parse(SliceTestData.Json(segment), Label, Surfaces));
			Assert.That(error.File, Is.EqualTo(Label));
			Assert.That(error.Message, Does.Contain("segment 0 (id 1)").And.Contain(reason));
		}

		[Test]
		public void ASolidSideProofThatIsNotTrueOrFalse_Fails()
		{
			var segment = Kind(SliceTestData.Segment(1, "8", 963.0, 3771.9, 984.0, 3786.6), "pier");
			segment["solid_side_evidence"] = new JObject { ["verified"] = "true" };

			var error = Assert.Throws<LandingDataException>(() => LandingGeometry.Parse(SliceTestData.Json(segment), Label, Surfaces));
			Assert.That(error.Message, Does.Contain("solid_side_evidence.verified must be true or false"));
		}

		[Test]
		public void Fenders_FollowTheKind_AndALandingsOwnEntryStaysOnItsBarge()
		{
			var root = JObject.Parse(SliceTestData.SurfacesJson("87", 2.5e6));
			var bulkhead = (JObject)root["default"].DeepClone();
			bulkhead["stiffness"]["value"] = 5.0e6;
			root["kinds"] = new JObject { ["bulkhead"] = bulkhead };
			var surfaces = LandingSurfaceTable.Parse(root.ToString(), "surfaces");
			var json = SliceTestData.Json(
				Kind(SliceTestData.Segment(1, "87", 0.0, 0.0, 0.0, 30.0), "barge-side"),
				Kind(SliceTestData.Segment(2, "87", 20.0, 0.0, 20.0, 30.0), "bulkhead"),
				Kind(SliceTestData.Segment(3, "87", 40.0, 0.0, 40.0, 30.0), "pier"),
				Kind(SliceTestData.Segment(4, "20", 60.0, 0.0, 60.0, 30.0), "bulkhead"),
				Kind(SliceTestData.Segment(5, "20", 80.0, 0.0, 80.0, 30.0), "barge-end"));

			var geometry = LandingGeometry.Parse(json, Label, surfaces);

			Assert.That(geometry.Find(1).Surface.Key, Is.EqualTo("87"), "the landing's own fender is on its barge");
			Assert.That(geometry.Find(2).Surface.Key, Is.EqualTo("kind:bulkhead"), "not on the bulkhead beside it");
			Assert.That(geometry.Find(2).Segment.Surface.Stiffness, Is.EqualTo(5.0e6));
			Assert.That(geometry.Find(3).Surface.Key, Is.EqualTo(LandingSurfaceTable.DefaultKey), "a kind without an entry uses the default");
			Assert.That(geometry.Find(4).Surface.Key, Is.EqualTo("kind:bulkhead"));
			Assert.That(geometry.Find(5).Surface.Key, Is.EqualTo(LandingSurfaceTable.DefaultKey));
		}

		[Test]
		public void TheShippedFenderFile_HasAnEstimatedEntryForEveryKind()
		{
			var table = SliceTestData.ShippedSurfaces();

			Assert.That(table.Kinds.Keys, Is.EquivalentTo(LandingSurfaceTable.ContactKinds));
			foreach (var entry in table.Kinds.Values)
			{
				foreach (var value in new[] { entry.Stiffness, entry.Damping, entry.Friction, entry.MaxCompression })
				{
					Assert.That(value.Confidence, Is.EqualTo("estimated"), entry.Key);
					Assert.That(value.Source, Does.Contain("Not a landing survey").Or.Contain("Not a survey"), entry.Key);
				}
			}
		}

		[Test]
		public void AnUnknownKindInTheFenderFile_Fails()
		{
			var root = JObject.Parse(SliceTestData.SurfacesJson());
			root["kinds"] = new JObject { ["wharf"] = root["default"].DeepClone() };

			var error = Assert.Throws<LandingDataException>(() => LandingSurfaceTable.Parse(root.ToString(), "surfaces"));
			Assert.That(error.Message, Does.Contain("kinds.wharf"));
		}

		/// <summary>
		/// The importer's real file, read independently: every segment is kept or listed with a reason, every kind is
		/// one the fender file knows, structure segments kept on their solid side alone are listed apart, and the
		/// contact tests' landing keeps its primary face. Written to artifacts/unity/slice/landing-geometry-v2.txt.
		/// </summary>
		[Test]
		public void TheImportersVersion2File_KeepsOrListsEverySegment_ByKind()
		{
			var raw = JArray.Parse(File.ReadAllText(SliceTestData.ContactsPath));
			if (raw.Cast<JObject>().All(s => s["kind"] == null))
			{
				Assert.Ignore("contacts.json is still version 1; run the import after export-unity.");
			}

			var geometry = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);

			Assert.That(geometry.Segments.Count + geometry.Excluded.Count, Is.EqualTo(raw.Count));
			Assert.That(raw.Select(s => (string)s["kind"]).Distinct(), Is.SubsetOf(LandingSurfaceTable.ContactKinds));
			foreach (var kept in geometry.Segments)
			{
				var record = (JObject)raw.First(s => (int)s["id"] == kept.Id);
				Assert.That(kept.Kind, Is.EqualTo((string)record["kind"]));
				if (kept.WaterSideBasis == "solid-side")
				{
					Assert.That(kept.IsPrimary, Is.False, $"segment {kept.Id}");
					Assert.That((bool)record["solid_side_evidence"]["verified"], Is.True, $"segment {kept.Id}");
				}
			}

			Assert.That(geometry.PrimaryFace(LandingGeometryTests.ContactTestStopId).Segments.Select(s => s.Id), Is.EqualTo(new[] { 149111046 }));
			Assert.That(geometry.Find(149111046).Verification, Is.EqualTo("crew-reported landing side"));
			var report = Path.Combine(SliceTestData.RepoRoot, "artifacts", "unity", "slice");
			Directory.CreateDirectory(report);
			File.WriteAllText(Path.Combine(report, "landing-geometry-v2.txt"), geometry.Describe());
		}
	}
}
