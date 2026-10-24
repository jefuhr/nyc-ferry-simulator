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

		[Test]
		public void EveryLandingSegmentHasWaterOnLeft()
		{
			foreach (var feature in SliceImporter.ReadFeatures("landing_faces.geojson"))
			{
				var normal = (JArray)feature["properties"]["outward_normal_ne"];
				var segments = SliceImporter.LandingSegments(feature).ToArray();
				Assert.That(segments.Length, Is.GreaterThan(0));
				foreach (var segment in segments)
					Assert.That(segment.WaterNormal.North * (double)normal[0] + segment.WaterNormal.East * (double)normal[1], Is.GreaterThan(.99));
			}
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
		public void ImportTwicePreservesEveryGeneratedByteAndGuid()
		{
			SliceImporter.Import();
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
