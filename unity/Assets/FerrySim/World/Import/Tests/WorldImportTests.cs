using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts.Geodesy;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.World.Import.Tests
{
	public sealed class WorldImportTests
	{
		[Test]
		public void EmptySerializedSequenceUsesNominalTiming()
		{
			LightCharacteristic.TryParse("Fl W 2s", out var light);
			light.Durations = Array.Empty<double>(); light.States = Array.Empty<bool>();
			Assert.That(light.IsLit(.1), Is.True);
			Assert.That(light.IsLit(1), Is.False);
		}

		[TestCase(false, 1.0, 1.0)]
		[TestCase(true, -1.0, 3.0)]
		[TestCase(true, double.NaN, 2.0)]
		[TestCase(true, double.PositiveInfinity, 2.0)]
		[TestCase(true, .1, .1)]
		public void InvalidOrUnmarkedChartSequenceCannotOverrideFallback(bool marked, double first, double second)
		{
			LightCharacteristic.TryParse("Fl W 2s", out var light);
			light.Durations = new[] { first, second }; light.States = new[] { false, false }; light.UsesChartSequence = marked;
			Assert.That(light.IsLit(.1), Is.True);
			Assert.That(light.IsLit(1), Is.False);
		}

		[Test]
		public void InfeasibleLongFlashFallbackWarnsButChartSequenceStillWins()
		{
			Assert.That(LightCharacteristic.TryParse("LFl(2) W 3s", out var light), Is.True);
			Assert.That(light.TryValidateFallback(out var warning), Is.False);
			Assert.That(warning, Does.Contain("cannot fit"));
			Assert.That(light.IsLit(.1), Is.False);
			light.SetSequence(new[] { 2.5, .5 }, new[] { true, false });
			Assert.That(light.IsLit(2.4), Is.True);
			Assert.That(light.IsLit(2.6), Is.False);
		}

		[Test]
		public void SyntheticStationDatumConvertsMhwRelativeToNavd88()
		{
			var datums = JObject.Parse(@"{""units"":""meters"",""OrthometricDatum"":""NAVD88"",""epoch"":""test epoch"",""datums"":[{""name"":""MHW"",""value"":4.2},{""name"":""NAVD88"",""value"":1.5}]}");
			var datum = new LightHeightDatum(datums);
			Assert.That(datum.OffsetMetres, Is.EqualTo(2.7).Within(1e-12));
			Assert.That(datum.ToNavd88(10), Is.EqualTo(12.7).Within(1e-12));
			datums["units"] = "feet";
			Assert.Throws<InvalidDataException>(() => new LightHeightDatum(datums));
			datums["units"] = "meters";
			((JArray)datums["datums"]).RemoveAt(1);
			Assert.Throws<InvalidDataException>(() => new LightHeightDatum(datums));
		}

		[Test]
		public void SavedNominalLightFlashesAfterInstantiation()
		{
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SliceImporter.Output + "/aid-02260220D42B0032.prefab");
			var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			try
			{
				var flasher = instance.GetComponentInChildren<BuoyFlasher>();
				Assert.That(flasher.Characteristic.UsesChartSequence, Is.False);
				flasher.SetSimulationTime(.1);
				Assert.That(flasher.Lamp.enabled, Is.True); Assert.That(flasher.Lens.enabled, Is.True);
				flasher.SetSimulationTime(1);
				Assert.That(flasher.Lamp.enabled, Is.False); Assert.That(flasher.Lens.enabled, Is.False);
			}
			finally { UnityEngine.Object.DestroyImmediate(instance); }
		}

		[TestCase("Oc(2) R 6s", false, 2)]
		[TestCase("Fl(3) W 2s", true, 3)]
		[TestCase("Fl(2+1) W 2s", true, 3)]
		public void FallbackPreservesEveryFlashOrEclipse(string notation, bool countedState, int expected)
		{
			Assert.That(LightCharacteristic.TryParse(notation, out var light), Is.True);
			var count = 0; var previous = light.IsLit(-light.PeriodSeconds / 10000);
			var litSamples = 0;
			for (var i = 0; i < 10000; i++)
			{
				var lit = light.IsLit(i * light.PeriodSeconds / 10000);
				if (lit == countedState && lit != previous) count++;
				if (lit) litSamples++;
				previous = lit;
			}
			Assert.That(count, Is.EqualTo(expected));
			Assert.That(litSamples, countedState ? Is.LessThan(5000) : Is.GreaterThan(5000));
		}

		[Test]
		public void PublishedHeightsIncludeNonzeroMhwToNavd88Correction()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			var cache = Environment.GetEnvironmentVariable("FERRYSIM_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FerrySim-cache");
			var datums = JObject.Parse(File.ReadAllText(Path.Combine(cache, "data/raw/coops/datums_8518750.json")));
			var values = ((JArray)datums["datums"]).ToDictionary(d => (string)d["name"], d => (double)d["value"]);
			var offset = values["MHW"] - values["NAVD88"];
			Assert.That(Math.Abs(offset), Is.GreaterThan(.1), "Fixture must exercise a nonzero datum correction.");
			SliceImporter.Import();
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SliceImporter.Output + "/lower-east-river.prefab");
			var count = 0;
			foreach (var feature in SliceImporter.ReadFeatures("aids.geojson"))
			foreach (var data in (JArray)feature["properties"]["lights"])
			{
				if (data["height_m"].Type == JTokenType.Null) continue;
				var lamp = prefab.transform.Find("Aids/" + SliceImporter.FeatureName(feature)).GetComponentInChildren<Light>();
				Assert.That(lamp.transform.position.y, Is.EqualTo(SliceImporter.ReadGeoid() + (double)data["height_m"] + offset).Within(.00001), (string)feature["properties"]["name"]);
				count++;
			}
			Assert.That(count, Is.EqualTo(4));
		}

		[TestCase("Fl G 4s", 0.1, true)]
		[TestCase("Fl G 4s", 1.0, false)]
		[TestCase("Fl G 4s", 4.1, true)]
		[TestCase("Q R 1s", 0.1, true)]
		[TestCase("Q R 1s", 0.7, false)]
		[TestCase("Iso W 4s", 1.9, true)]
		[TestCase("Iso W 4s", 2.1, false)]
		[TestCase("Oc R 4s", 2.5, true)]
		[TestCase("Oc R 4s", 3.5, false)]
		[TestCase("LFl W 6s", 1.9, true)]
		[TestCase("LFl W 6s", 2.1, false)]
		[TestCase("Fl(2) G 6s", 1.1, true)]
		[TestCase("Fl(2) G 6s", 2.0, false)]
		[TestCase("Fl(2+1) R 10s", 3.1, true)]
		public void CharacteristicTiming(string notation, double time, bool expected)
		{
			Assert.That(LightCharacteristic.TryParse(notation, out var light), Is.True);
			Assert.That(light.IsLit(time), Is.EqualTo(expected));
			Assert.That(light.IsLit(time + light.PeriodSeconds * 10), Is.EqualTo(expected));
		}

		[TestCase("Fl(99999999999999999999) G 4s")]
		[TestCase("Fl W")]
		[TestCase("Fl G 0s")]
		[TestCase("Fl(0) G 4s")]
		[TestCase("Mo W 4s")]
		[TestCase("Fl Z 4s")]
		[TestCase("Fl G NaNs")]
		public void UnknownOrInvalidCharacteristicsFailClosed(string notation)
		{
			Assert.That(LightCharacteristic.TryParse(notation, out _), Is.False);
		}

		[Test]
		public void ExplicitChartSequenceOverridesEstimatedPulseWidths()
		{
			LightCharacteristic.TryParse("Fl G 4s", out var light);
			light.SetSequence(new[] { 0.8, 3.2 }, new[] { true, false });
			Assert.That(light.IsLit(.7), Is.True);
			Assert.That(light.IsLit(.9), Is.False);
			Assert.Throws<ArgumentException>(() => light.SetSequence(new[] { .8, 2.2 }, new[] { true, false }));
		}

		[Test]
		public void PublishedCompositeGroupSequenceAndRuntimeFlasherAgree()
		{
			LightCharacteristic.TryParse("Fl(2+1) G 6s", out var characteristic);
			characteristic.SetSequence(new[] { .3, .4, .3, 1.2, .3, 3.5 }, new[] { true, false, true, false, true, false });
			var go = new GameObject("TestAid");
			try
			{
				var lamp = go.AddComponent<Light>(); var flasher = go.AddComponent<BuoyFlasher>();
				flasher.Characteristic = characteristic; flasher.Lamp = lamp;
				flasher.SetSimulationTime(2.3); Assert.That(lamp.enabled, Is.True);
				flasher.SetSimulationTime(2.6); Assert.That(lamp.enabled, Is.False);
				flasher.SetSimulationTime(8.3); Assert.That(lamp.enabled, Is.True);
			}
			finally { UnityEngine.Object.DestroyImmediate(go); }
		}

		[Test]
		public void KnownAidUsesCoreGeodesyAndNavd88Offset()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			var feature = SliceImporter.ReadFeatures("aids.geojson").First();
			var coordinate = (JArray)feature["geometry"]["coordinates"];
			var lon = (double)coordinate[0]; var lat = (double)coordinate[1];
			var local = LocalTangentPlane.World.ToLocal(new GeodeticPosition(lat, lon));
			var placed = ImportCoordinates.Position(lon, lat, 2, SliceImporter.ReadGeoid());
			Assert.That(placed.x, Is.EqualTo(local.East).Within(.5));
			Assert.That(placed.z, Is.EqualTo(local.North).Within(.5));
			Assert.That(placed.y, Is.EqualTo(2 + SliceImporter.ReadGeoid()).Within(.001));
			SliceImporter.Import();
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SliceImporter.Output + "/lower-east-river.prefab");
			var aid = prefab.transform.Find("Aids/" + SliceImporter.FeatureName(feature));
			Assert.That(aid, Is.Not.Null);
			Assert.That(aid.position.x, Is.EqualTo(local.East).Within(.5));
			Assert.That(aid.position.z, Is.EqualTo(local.North).Within(.5));
		}

		// Failure modes: end faces flipped to the main face normal, disconnected joints,
		// unstable primary segment IDs, and fresh checkouts failing before data is built.
		[Test]
		public void EveryLandingSegmentHasWaterOnLeft()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			foreach (var feature in SliceImporter.ReadFeatures("landing_faces.geojson"))
			{
				var segments = SliceImporter.LandingSegments(feature).ToArray();
				Assert.That(segments.Length, Is.GreaterThan(0));
				var ring = (JArray)feature["geometry"]["coordinates"][0];
				var centreNorth = ring.Take(ring.Count - 1).Average(p => LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)p[1], (double)p[0])).North);
				var centreEast = ring.Take(ring.Count - 1).Average(p => LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)p[1], (double)p[0])).East);
				for (var i = 0; i < segments.Length; i++)
				{
					var segment = segments[i];
					var north = (segment.Start.North + segment.End.North) * .5 - centreNorth;
					var east = (segment.Start.East + segment.End.East) * .5 - centreEast;
					Assert.That(segment.WaterNormal.North * north + segment.WaterNormal.East * east, Is.GreaterThan(0));
					if (i > 0) Assert.That(Math.Sqrt(Math.Pow(segments[i - 1].End.North - segment.Start.North, 2) + Math.Pow(segments[i - 1].End.East - segment.Start.East, 2)), Is.LessThan(.001));
				}
			}
		}

		[Test]
		public void ConnectedContactOutlineKeepsItsEndNormalsAndPrimaryId()
		{
			var feature = JObject.Parse(@"{'properties':{'stop_id':'fixture','face_geometry':{'type':'LineString','coordinates':[[-74.0151,40.7001],[-74.0149,40.7001]]},'outward_normal_ne':[1,0]}}");
			var primary = SliceImporter.LandingSegments(feature).Single();
			feature["properties"]["contact_geometry"] = JObject.Parse(@"{'type':'LineString','coordinates':[[-74.0151,40.7],[-74.0151,40.7001],[-74.0149,40.7001],[-74.0149,40.7]]}");
			var segments = SliceImporter.LandingSegments(feature).ToArray();
			Assert.That(segments.Length, Is.EqualTo(3));
			Assert.That(segments[1].Id, Is.EqualTo(primary.Id));
			Assert.That(segments[0].WaterNormal.East, Is.LessThan(-.99));
			Assert.That(segments[1].WaterNormal.North, Is.GreaterThan(.99));
			Assert.That(segments[2].WaterNormal.East, Is.GreaterThan(.99));
			Assert.That(segments.Select(x => x.Id).Distinct().Count(), Is.EqualTo(3));
			for (var i = 1; i < segments.Length; i++)
			{
				Assert.That(segments[i - 1].End.North, Is.EqualTo(segments[i].Start.North));
				Assert.That(segments[i - 1].End.East, Is.EqualTo(segments[i].Start.East));
			}
		}

		[Test]
		public void MissingPipelineDirectorySkipsWithBuildCommandButExistingDirectoryDoesNotSkip()
		{
			var directory = Path.Combine(Path.GetTempPath(), "ferrysim-import-" + Guid.NewGuid().ToString("N"));
			var skip = Assert.Throws<NUnit.Framework.IgnoreException>(() => RequirePipelineInputs(directory));
			Assert.That(skip.Message, Does.Contain("uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river"));
			Directory.CreateDirectory(directory);
			try { Assert.DoesNotThrow(() => RequirePipelineInputs(directory)); }
			finally { Directory.Delete(directory); }
		}

		private static void RequirePipelineInputs(string directory)
		{
			if (!Directory.Exists(directory)) Assert.Ignore("Pipeline outputs are not built in this checkout; run uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river then uv run --project data-pipeline python data-pipeline/ferrydata export-unity --slice lower-east-river.");
		}

		[Test]
		public void InvalidRasterValuesDoNotBecomeGeometry()
		{
			Assert.That(RawDepthGrid.IsValid(float.NaN, -9999), Is.False);
			Assert.That(RawDepthGrid.IsValid(float.PositiveInfinity, -9999), Is.False);
			Assert.That(RawDepthGrid.IsValid(-9999, -9999), Is.False);
			Assert.That(RawDepthGrid.IsValid(-25, -9999), Is.True);
		}

		[Test]
		public void CrewApprovalCannotTurnAFailedChartCheckIntoPipelineVerification()
		{
			var feature = JObject.Parse(@"{'properties':{'water_side':true,'water_side_verified_by_pipeline':false,'water_side_crew_reported':true}}");
			Assert.That(SliceImporter.PipelineWaterSideVerified(feature), Is.False);
			((JObject)feature["properties"]).Remove("water_side_verified_by_pipeline");
			Assert.That(SliceImporter.PipelineWaterSideVerified(feature), Is.True, "legacy files retain their old field semantics");
		}

		[Test]
		public void CrewReportedWaterSideIsExportedAlongsideIndependentChartSupport()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			var north = contacts.Where(c => (string)c["polyline_id"] == "landing-19").ToArray();
			Assert.That(north.Length, Is.GreaterThan(0));
			foreach (var contact in north)
			{
				if ((int)contact["index"] >= 1 && (int)contact["index"] <= 3)
					Assert.That((bool?)contact["water_side_crew_reported"], Is.True);
				else Assert.That(contact["water_side_crew_reported"], Is.Null, "ENC end-side normals are not a separate crew observation");
				Assert.That((bool)contact["water_side_verified_by_pipeline"], Is.EqualTo((int)contact["index"] != 4));
			}
			foreach (var contact in contacts.Where(c => (string)c["stop_id"] != "19" && (string)c["stop_id"] != "8"))
				Assert.That(contact["water_side_crew_reported"], Is.Null);
		}

		[Test]
		public void ReviewPhysicalEdgeIdsSurvivePrimaryRoleChangesAndOutlineWinding()
		{
			var feature = JObject.Parse(@"{'geometry':{'type':'Polygon','coordinates':[[[-74.0151,40.7],[-74.0151,40.7001],[-74.0149,40.7001],[-74.0149,40.7],[-74.0151,40.7]]]},'properties':{'stop_id':'fixture','enc_id':'ENC-fixture','source':'enc-feature','outward_normal_ne':[1,0],'face_geometry':{'type':'LineString','coordinates':[[-74.0151,40.7001],[-74.0149,40.7001]]},'contact_geometry':{'type':'LineString','coordinates':[[-74.0151,40.7],[-74.0151,40.7001],[-74.0149,40.7001],[-74.0149,40.7]]}}}");
			var first = SliceImporter.LandingSegments(feature).Select(s => s.Id).ToArray();
			feature["properties"]["face_geometry"] = JObject.Parse(@"{'type':'LineString','coordinates':[[-74.0151,40.7],[-74.0151,40.7001]]}");
			var second = SliceImporter.LandingSegments(feature).Select(s => s.Id).ToArray();
			Assert.That(second, Is.EqualTo(first), "physical IDs cannot depend on primary/end role");
			Assert.That(first.Distinct().Count(), Is.EqualTo(3));
			var ring = (JArray)feature["geometry"]["coordinates"][0];
			feature["geometry"]["coordinates"][0] = new JArray(ring.Reverse());
			Assert.That(SliceImporter.LandingSegments(feature).Select(s => s.Id).ToArray(), Is.EqualTo(first));
		}

		[Test]
		public void ReviewUnsupportedNorthEnd787614137CannotInheritPrimaryProof()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			var end = contacts.Single(c => (string)c["polyline_id"] == "landing-19" && (int)c["index"] == 4); // reviewed old ID 787614137, migrated to physical-edge identity
			Assert.That((int)end["index"], Is.EqualTo(4));
			Assert.That((bool)end["water_side_verified_by_pipeline"], Is.False);
			Assert.That(end["water_side_crew_reported"], Is.Null);
			Assert.That(end["water_side_evidence"], Is.Not.Null);
			foreach (var primary in contacts.Where(c => (string)c["polyline_id"] == "landing-19" && (int)c["index"] >= 1 && (int)c["index"] <= 3))
				Assert.That((bool)primary["water_side_verified_by_pipeline"], Is.True);
		}

		[Test]
		public void ReviewCrewEvidenceDoesNotReplaceOrdinalGeometryConfidence()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			foreach (var contact in contacts)
				Assert.That(new[] { "high", "medium", "low" }, Does.Contain((string)contact["confidence"]));
			var north = contacts.Single(c => (string)c["polyline_id"] == "landing-19" && (int)c["index"] == 2);
			Assert.That((string)north["confidence"], Is.EqualTo("medium"));
			Assert.That((string)north["crew_evidence"]["kind"], Is.EqualTo("face-orientation"));
			var pier = contacts.Single(c => (string)c["polyline_id"] == "landing-87" && (int)c["index"] == 2);
			Assert.That((string)pier["confidence"], Is.EqualTo("low"));
			Assert.That((string)pier["crew_evidence"]["kind"], Is.EqualTo("berth-use-policy"));
			Assert.That(pier["water_side_crew_reported"], Is.Null);
		}

		[Test]
		public void ReviewNorthHullUsesMediumOrdinalConfidenceMaterial()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			var greybox = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FerrySim/World/Greybox/LowerEastRiverGreybox.prefab");
			var hull = greybox.GetComponentsInChildren<MeshRenderer>(true).Single(r => r.name == "Hull" && r.transform.parent.name.StartsWith("19 ", StringComparison.Ordinal));
			Assert.That(hull.sharedMaterial.name, Is.EqualTo("Barge_MediumConfidence"));
		}

		[Test]
		public void PrimaryFaceFlagsExactlyMatchSelectedSubsegmentsForEveryStop()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			var faces = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(SliceImporter.Inputs, "landing_faces.geojson")))["features"];
			Assert.That(contacts.Select(c => (string)c["stop_id"]).Distinct().Count(), Is.EqualTo(faces.Count));
			foreach (JObject face in faces)
			{
				var stop = (string)face["properties"]["stop_id"];
				var primaryOnly = (JObject)face.DeepClone();
				((JObject)primaryOnly["properties"]).Remove("contact_geometry");
				var expected = SliceImporter.LandingSegments(primaryOnly).ToArray();
				var segments = contacts.Where(c => (string)c["polyline_id"] == "landing-" + stop).OrderBy(c => (int)c["index"]).ToArray();
				var flagged = segments.Where(c => (bool?)c["primary_face"] == true).ToArray();
				Assert.That(expected.Length, Is.GreaterThan(0), stop);
				Assert.That(flagged.Select(c => (int)c["id"]).ToArray(), Is.EqualTo(expected.Select(s => s.Id).ToArray()), stop + ": flag every selected primary subdivision and no other side");
				var indices = flagged.Select(c => (int)c["index"]).ToArray();
				Assert.That(indices, Is.EqualTo(Enumerable.Range(indices[0], indices.Length).ToArray()), stop + ": primary indices must be contiguous");
				for (var i = 0; i < flagged.Length; i++)
				{
					Assert.That(flagged[i]["primary_face"].Type, Is.EqualTo(JTokenType.Boolean), stop);
					Assert.That((double)flagged[i]["start_ne_m"][0], Is.EqualTo(expected[i].Start.North), stop);
					Assert.That((double)flagged[i]["start_ne_m"][1], Is.EqualTo(expected[i].Start.East), stop);
					Assert.That((double)flagged[i]["end_ne_m"][0], Is.EqualTo(expected[i].End.North), stop);
					Assert.That((double)flagged[i]["end_ne_m"][1], Is.EqualTo(expected[i].End.East), stop);
				}
				foreach (var other in contacts.Where(c => (string)c["stop_id"] == stop && !expected.Any(s => s.Id == (int)c["id"])))
					Assert.That(other["primary_face"], Is.Null, stop + ": non-primary ends and structures must omit the field");
			}
		}

		[Test]
		public void SouthWilliamsburgCrewPrimaryPreservesChartFailureAndPhysicalIds()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			var south = contacts.Where(c => (string)c["polyline_id"] == "landing-8").OrderBy(c => (int)c["index"]).ToArray();
			Assert.That(south.Select(c => (int)c["id"]).ToArray(), Is.EqualTo(new[] { 216221522, 149111046 }), "IDs from the previous contacts.json must survive the role change");
			var primary = south.Where(c => (bool?)c["primary_face"] == true).ToArray();
			Assert.That(primary.Select(c => (int)c["index"]).ToArray(), Is.EqualTo(new[] { 1 }));
			foreach (var contact in primary)
			{
				Assert.That((bool)contact["water_side_verified_by_pipeline"], Is.False);
				Assert.That((bool?)contact["water_side_crew_reported"], Is.True);
				Assert.That((string)contact["crew_evidence"]["note_id"], Is.EqualTo("south-williamsburg-face-orientation-2026-10-24"));
				Assert.That((string)contact["crew_evidence"]["kind"], Is.EqualTo("face-orientation"));
				Assert.That((string)contact["confidence"], Is.EqualTo("medium"));
				Assert.That((string)contact["verification"], Is.EqualTo("crew-reported"), "the crew-reported NW face is no longer labelled as needing crew verification");
			}
			Assert.That((string)south[0]["verification"], Is.EqualTo("needs-crew-verification"));
			Assert.That(south[0]["primary_face"], Is.Null);
			Assert.That(south[0]["water_side_crew_reported"], Is.Null);
			Assert.That(south[0]["crew_evidence"], Is.Null);
			Assert.That((bool)south[0]["water_side_verified_by_pipeline"], Is.True);
			Assert.That((string)south[0]["water_side_evidence"]["method"], Is.EqualTo("direct-12m"));
		}

		// contacts.json version 2. Failure modes: a version 1 field lost or retyped on a new segment, positions
		// taken from the pipeline's geodesy instead of the core's, the exposed runs re-identified, an unknown kind
		// imported silently, a barge outline that does not close, and segments dropped between export and import.
		[Test]
		public void Version2ImportKeepsVersion1FieldsCoreCoordinatesAndEverySegment()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			var exported = JArray.Parse(File.ReadAllText(Path.Combine(SliceImporter.Inputs, "unity/contacts.json")));
			Assert.That(contacts.Select(c => (int)c["id"]), Is.EqualTo(exported.Select(c => (int)c["id"])), "every exported segment, in file order");
			Assert.That(contacts.Select(c => (int)c["id"]).Distinct().Count(), Is.EqualTo(contacts.Count));
			foreach (JObject contact in contacts)
			{
				foreach (var field in new[] { "id", "stop_id", "stop_name", "start_ne_m", "end_ne_m", "water_normal_ne", "source", "confidence", "verification", "water_side_verified_by_pipeline", "kind", "solid_side_evidence" })
					Assert.That(contact[field], Is.Not.Null, contact["id"] + " " + field);
				Assert.That(SliceImporter.ContactKinds, Does.Contain((string)contact["kind"]));
				var start = LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)contact["start_lonlat"][1], (double)contact["start_lonlat"][0]));
				var end = LocalTangentPlane.World.ToLocal(new GeodeticPosition((double)contact["end_lonlat"][1], (double)contact["end_lonlat"][0]));
				Assert.That((double)contact["start_ne_m"][0], Is.EqualTo(start.North)); Assert.That((double)contact["start_ne_m"][1], Is.EqualTo(start.East));
				Assert.That((double)contact["end_ne_m"][0], Is.EqualTo(end.North)); Assert.That((double)contact["end_ne_m"][1], Is.EqualTo(end.East));
				var length = Math.Sqrt(Math.Pow(end.North - start.North, 2) + Math.Pow(end.East - start.East, 2));
				Assert.That((double)contact["water_normal_ne"][0], Is.EqualTo((end.East - start.East) / length).Within(1e-12), "water on the left");
				Assert.That((double)contact["water_normal_ne"][1], Is.EqualTo(-(end.North - start.North) / length).Within(1e-12));
			}
			foreach (var stop in contacts.Select(c => (string)c["stop_id"]).Distinct())
			{
				var run = contacts.Where(c => (string)c["polyline_id"] == "landing-" + stop).ToArray();
				var back = contacts.Where(c => (string)c["polyline_id"] == "landing-" + stop + "-back").ToArray();
				Assert.That(back, Is.Not.Empty, stop + ": the whole barge is contact geometry");
				Assert.That(JToken.DeepEquals(run.Last()["end_ne_m"], back.First()["start_ne_m"]) && JToken.DeepEquals(back.Last()["end_ne_m"], run.First()["start_ne_m"]), Is.True, stop + ": the barge outline closes");
			}
			var manifest = JObject.Parse(File.ReadAllText(SliceImporter.Output + "/import-manifest.json"));
			Assert.That((int)manifest["contacts_version"], Is.EqualTo(2));
			Assert.That((int)manifest["contact_segments"], Is.EqualTo(contacts.Count));
			Assert.That(manifest["source_sha256"]["unity/contacts.json"], Is.Not.Null);
		}

		[Test]
		public void ExposedRunsKeepTheLegacyPhysicalEdgeIds()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			foreach (var feature in SliceImporter.ReadFeatures("landing_faces.geojson"))
			{
				var stop = (string)feature["properties"]["stop_id"];
				var legacy = SliceImporter.LandingSegments(feature).ToArray();
				var run = contacts.Where(c => (string)c["polyline_id"] == "landing-" + stop).ToArray();
				Assert.That(run.Select(c => (int)c["id"]), Is.EqualTo(legacy.Select(s => s.Id)), stop);
				for (var i = 0; i < run.Length; i++)
				{
					Assert.That((double)run[i]["start_ne_m"][0], Is.EqualTo(legacy[i].Start.North), stop);
					Assert.That((double)run[i]["start_ne_m"][1], Is.EqualTo(legacy[i].Start.East), stop);
				}
			}
		}

		[Test]
		public void AContactRecordWithAnUnknownKindOrMovedCoordinatesIsRefused()
		{
			var start = LocalTangentPlane.World.ToLocal(new GeodeticPosition(40.7001, -74.0151));
			var end = LocalTangentPlane.World.ToLocal(new GeodeticPosition(40.7001, -74.0149));
			var record = new JObject { ["id"] = 7, ["stop_id"] = "fixture", ["polyline_id"] = "fixed-fixture-0", ["index"] = 0, ["kind"] = "pier",
				["start_lonlat"] = new JArray(-74.0151, 40.7001), ["end_lonlat"] = new JArray(-74.0149, 40.7001),
				["start_ne_m"] = new JArray(start.North, start.East), ["end_ne_m"] = new JArray(end.North, end.East), ["water_normal_ne"] = new JArray(0.0, 0.0) };
			var imported = SliceImporter.ContactRecord(record);
			Assert.That((double)imported["water_normal_ne"][0], Is.EqualTo(1.0).Within(1e-9), "recomputed: water to the north of a segment running east");
			Assert.That((double)record["water_normal_ne"][0], Is.EqualTo(0.0), "the pipeline record is not modified");
			var unknown = (JObject)record.DeepClone(); unknown["kind"] = "wharf";
			Assert.That(Assert.Throws<InvalidDataException>(() => SliceImporter.ContactRecord(unknown)).Message, Does.Contain("unknown kind wharf"));
			var moved = (JObject)record.DeepClone(); moved["end_ne_m"][0] = end.North + .01;
			Assert.That(Assert.Throws<InvalidDataException>(() => SliceImporter.ContactRecord(moved)).Message, Does.Contain("end_ne_m"));
			var zero = (JObject)record.DeepClone(); zero["id"] = 0;
			Assert.That(Assert.Throws<InvalidDataException>(() => SliceImporter.ContactRecord(zero)).Message, Does.Contain("id must be"));
		}

		[Test]
		public void ImportTwicePreservesEveryGeneratedByteAndGuid()
		{
			RequirePipelineInputs(SliceImporter.Inputs);
			SliceImporter.Import();
			var contacts = JArray.Parse(File.ReadAllText(SliceImporter.Output + "/contacts.json"));
			foreach (var group in contacts.GroupBy(c => (string)c["polyline_id"]))
			{
				var segments = group.ToArray();
				Assert.That(segments.Select(c => (string)c["stop_id"]).Distinct().Count(), Is.EqualTo(1), group.Key);
				Assert.That(segments.Select(c => (int)c["index"]), Is.EqualTo(Enumerable.Range(0, segments.Length)), group.Key + ": file order is polyline order");
				for (var i = 1; i < segments.Length; i++) Assert.That(JToken.DeepEquals(segments[i - 1]["end_ne_m"], segments[i]["start_ne_m"]), Is.True, group.Key);
			}
			var first = SliceImporter.GeneratedHashes();
			var firstPrefab = File.ReadAllBytes(SliceImporter.Output + "/lower-east-river.prefab");
			Assert.That(AssetDatabase.LoadAssetAtPath<Material>(SliceImporter.Output + "/buoy-grey.mat").shader.name, Is.EqualTo("FerrySim/SolidColour"));
			SliceImporter.Import();
			var second = SliceImporter.GeneratedHashes();
			var evidence = Path.Combine(SliceImporter.Repository, "artifacts/unity"); Directory.CreateDirectory(evidence);
			File.WriteAllLines(Path.Combine(evidence, "determinism-first.sha256"), first);
			File.WriteAllLines(Path.Combine(evidence, "determinism-second.sha256"), second);
			if (!first.SequenceEqual(second))
			{
				File.WriteAllBytes(Path.Combine(evidence, "determinism-first.prefab"), firstPrefab);
				File.Copy(SliceImporter.Output + "/lower-east-river.prefab", Path.Combine(evidence, "determinism-second.prefab"), true);
			}
			Assert.That(second, Is.EqualTo(first));
			Assert.That(first.Length, Is.GreaterThan(10));
		}
	}
}
