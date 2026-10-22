using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	public class VesselParametersTests
	{
		[TestCase("t-class.json", "t-class")]
		[TestCase("k-class.json", "k-class")]
		public void ShippedVesselFiles_LoadAndValidate(string fileName, string expectedId)
		{
			var parameters = VesselParametersJson.Load(RepoPaths.Vessel(fileName));
			Assert.That(parameters.Id, Is.EqualTo(expectedId));
			Assert.That(parameters.Validate(), Is.Empty);
			Assert.That(parameters.Machinery.EngineCount.Value, Is.EqualTo(2.0), "both classes are twin-screw");
			Assert.That(parameters.Performance.ServiceSpeed.Value, Is.GreaterThan(0.0));
		}

		[Test]
		public void RoundTrip_PreservesValues()
		{
			var original = VesselParametersJson.Load(RepoPaths.Vessel("k-class.json"));
			var copy = VesselParametersJson.Parse(VesselParametersJson.Serialize(original));
			Assert.That(copy.Particulars.LengthOverall.Value, Is.EqualTo(original.Particulars.LengthOverall.Value));
			Assert.That(copy.Sources.Keys, Is.EquivalentTo(original.Sources.Keys));
		}

		[Test]
		public void UnknownProperty_IsRejected()
		{
			var json = Minimal("\"particulars\": { \"lenghtOverall\": " + Value("26.0", "m") + " }");
			Assert.Throws<VesselParametersException>(() => VesselParametersJson.Parse(json));
		}

		[Test]
		public void NonSiUnit_IsReported()
		{
			var errors = ParseErrors(Minimal("\"particulars\": { \"lengthOverall\": " + Value("85.3", "ft") + " }"));
			Assert.That(errors.Any(e => e.StartsWith("particulars.lengthOverall.unit")), Is.True, string.Join("\n", errors));
		}

		[Test]
		public void PublishedValue_MustCiteAKnownSource()
		{
			var errors = ParseErrors(Minimal("\"particulars\": { \"beam\": { \"value\": 8.0, \"unit\": \"m\", \"source\": \"nowhere\", \"confidence\": \"published\" } }"));
			Assert.That(errors.Any(e => e.StartsWith("particulars.beam.source")), Is.True, string.Join("\n", errors));
		}

		[Test]
		public void EstimatedValue_MayGiveFreeTextBasis()
		{
			var json = Minimal("\"particulars\": { \"displacement\": { \"value\": 60000, \"unit\": \"kg\", \"source\": \"scaled from similar 26 m catamarans\", \"confidence\": \"estimated\" } }");
			Assert.That(VesselParametersJson.Parse(json).Particulars.Displacement.Value, Is.EqualTo(60000.0));
		}

		[Test]
		public void UnknownConfidence_IsReported()
		{
			var errors = ParseErrors(Minimal("\"particulars\": { \"beam\": { \"value\": 8.0, \"unit\": \"m\", \"source\": \"ref\", \"confidence\": \"pretty sure\" } }"));
			Assert.That(errors.Any(e => e.StartsWith("particulars.beam.confidence")), Is.True, string.Join("\n", errors));
		}

		private static string Value(string number, string unit) =>
			"{ \"value\": " + number + ", \"unit\": \"" + unit + "\", \"source\": \"ref\", \"confidence\": \"published\" }";

		private static string Minimal(string body) =>
			"{ \"schemaVersion\": 1, \"id\": \"test\", \"sources\": { \"ref\": \"https://example.org\" }, " + body + " }";

		private static string[] ParseErrors(string json)
		{
			var exception = Assert.Throws<VesselParametersException>(() => VesselParametersJson.Parse(json));
			return exception.Errors.ToArray();
		}
	}
}
