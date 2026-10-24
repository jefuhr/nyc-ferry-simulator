using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The landing fender parameter file. Failure modes guarded: a surface taken from code instead of the file, a
	/// value without provenance (unit, source or confidence missing or wrong), a typo that silently falls back to
	/// the default (unknown properties), a per-landing entry leaking to other landings, values the core would
	/// reject (non-positive stiffness or travel), a number past Int64 or double range escaping as another exception
	/// than <see cref="LandingDataException"/>, and a missing or unreadable file failing quietly or without naming
	/// the file.
	/// </summary>
	public class LandingSurfaceTableTests
	{
		private const string Label = "test-surfaces.json";

		[Test]
		public void ShippedFile_IsTheCoreReportFender_EveryValueEstimated()
		{
			var table = SliceTestData.ShippedSurfaces();

			var surface = table.Default.Surface;
			Assert.That(surface.Stiffness, Is.EqualTo(1.0e6), "N/m per m");
			Assert.That(surface.Damping, Is.EqualTo(5.0e4), "N·s/m per m");
			Assert.That(surface.Friction, Is.EqualTo(0.3));
			Assert.That(surface.MaxCompression, Is.EqualTo(0.25), "m");
			var values = new[] { table.Default.Stiffness, table.Default.Damping, table.Default.Friction, table.Default.MaxCompression };
			var units = new[] { "N/m2", "N*s/m2", "1", "m" };
			for (var i = 0; i < values.Length; i++)
			{
				Assert.That(values[i].Unit, Is.EqualTo(units[i]));
				Assert.That(values[i].Confidence, Is.EqualTo(ParameterConfidence.Estimated), "no landing has been surveyed");
				Assert.That(values[i].Source, Does.Contain("Not a landing survey"), "the source says what the estimate is");
				Assert.That(ParameterUnits.IsAllowed(values[i].Unit), Is.True);
			}

			Assert.That(table.Default.Confidence, Is.EqualTo(ParameterConfidence.Estimated));
			Assert.That(table.File, Is.EqualTo(SliceTestData.SurfacesPath));
		}

		[Test]
		public void ALandingsOwnEntry_AppliesToThatLandingOnly()
		{
			var table = LandingSurfaceTable.Parse(SliceTestData.SurfacesJson("8", 2.5e6), Label);

			Assert.That(table.For("8").Key, Is.EqualTo("8"));
			Assert.That(table.For("8").Surface.Stiffness, Is.EqualTo(2.5e6));
			Assert.That(table.For("8").Note, Is.EqualTo("test entry"));
			Assert.That(table.For("20").Key, Is.EqualTo(LandingSurfaceTable.DefaultKey));
			Assert.That(table.For("20").Surface.Stiffness, Is.EqualTo(1.0e6));
			Assert.That(table.Landings.Keys, Is.EquivalentTo(new[] { "8" }));
		}

		[Test]
		public void ACitedValue_MustNameAKeyInSources()
		{
			var root = JObject.Parse(SliceTestData.SurfacesJson());
			root["sources"] = new JObject { ["survey-2027"] = "Hypothetical landing survey" };
			root["default"]["friction"]["confidence"] = "published";
			root["default"]["friction"]["source"] = "survey-2027";

			var table = LandingSurfaceTable.Parse(root.ToString(), Label);

			Assert.That(table.Default.Friction.Confidence, Is.EqualTo("published"));
			Assert.That(table.Default.Confidence, Is.EqualTo("mixed"));
			Assert.That(table.Sources["survey-2027"], Is.EqualTo("Hypothetical landing survey"));
		}

		private static IEnumerable<TestCaseData> MalformedFiles()
		{
			yield return Case("not JSON", _ => "{ \"schemaVersion\": 1, ", "not valid JSON");
			yield return Case("top level is an array", _ => "[]", "a JSON object");
			yield return Case("no schemaVersion", r => Remove(r, "schemaVersion"), "schemaVersion");
			yield return Case("other schemaVersion", r => Set(r, "schemaVersion", 2), "schemaVersion");
			// The same narrowing cast as Astra review finding 1: an integer past Int64 threw OverflowException.
			yield return Case("schemaVersion past Int64", r => Set(r, "schemaVersion", JToken.Parse("9223372036854775808")), "schemaVersion must be 1");
			yield return Case("value past double range", r => { r["default"]["damping"]["value"] = JToken.Parse(new string('9', 330)); return r.ToString(); }, "default.damping.value");
			yield return Case("no default", r => Remove(r, "default"), "default");
			yield return Case("unknown top-level property", r => Set(r, "defualt", new JObject()), "defualt");
			yield return Case("unknown entry property", r => { r["default"]["stifness"] = 1; return r.ToString(); }, "default.stifness");
			yield return Case("unknown value property", r => { r["default"]["damping"]["units"] = "N*s/m2"; return r.ToString(); }, "default.damping.units");
			yield return Case("missing stiffness", r => { ((JObject)r["default"]).Remove("stiffness"); return r.ToString(); }, "default.stiffness");
			yield return Case("wrong unit", r => { r["default"]["stiffness"]["unit"] = "N/m"; return r.ToString(); }, "default.stiffness.unit");
			yield return Case("missing source", r => { ((JObject)r["default"]["friction"]).Remove("source"); return r.ToString(); }, "default.friction.source");
			yield return Case("empty source", r => { r["default"]["friction"]["source"] = " "; return r.ToString(); }, "default.friction.source");
			yield return Case("unknown confidence", r => { r["default"]["friction"]["confidence"] = "guessed"; return r.ToString(); }, "default.friction.confidence");
			yield return Case("citation not in sources", r => { r["default"]["friction"]["confidence"] = "crew-reported"; return r.ToString(); }, "not a key in sources");
			yield return Case("value is text", r => { r["default"]["damping"]["value"] = "5e4"; return r.ToString(); }, "default.damping.value");
			yield return Case("negative stiffness", r => { r["default"]["stiffness"]["value"] = -1.0; return r.ToString(); }, "default");
			yield return Case("zero travel", r => { r["default"]["maxCompression"]["value"] = 0.0; return r.ToString(); }, "default");
			yield return Case("landings is not an object", r => Set(r, "landings", new JArray()), "landings");
			yield return Case("duplicate property", _ => SliceTestData.SurfacesJson().Replace("\"landings\": {}", "\"landings\": {}, \"landings\": {}"), "landings");
		}

		[TestCaseSource(nameof(MalformedFiles))]
		public void AMalformedFile_FailsWithTheFileAndTheReason(string json, string reason)
		{
			var error = Assert.Throws<LandingDataException>(() => LandingSurfaceTable.Parse(json, Label));
			Assert.That(error.File, Is.EqualTo(Label));
			Assert.That(error.Message, Does.StartWith(Label + ": "));
			Assert.That(error.Message, Does.Contain(reason));
		}

		[Test]
		public void AMissingFile_FailsWithItsPath()
		{
			var path = Path.Combine(SliceTestData.RepoRoot, "artifacts", "no-such-surfaces.json");
			var error = Assert.Throws<LandingDataException>(() => LandingSurfaceTable.Load(path));
			Assert.That(error.Message, Does.Contain(path).And.Contain("not found"));
		}

		private static TestCaseData Case(string name, System.Func<JObject, string> mutate, string reason) =>
			new TestCaseData(mutate(JObject.Parse(SliceTestData.SurfacesJson())), reason).SetName("AMalformedFile_FailsWithTheFileAndTheReason(" + name + ")");

		private static string Remove(JObject root, string property)
		{
			root.Remove(property);
			return root.ToString();
		}

		private static string Set(JObject root, string property, JToken value)
		{
			root[property] = value;
			return root.ToString();
		}
	}
}
