using System;
using System.Collections.Generic;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The <c>contact</c> section of the shipped parameter files. Failure modes guarded:
	/// <list type="bullet">
	/// <item>The section or one of its fields missing from one file; C# and JSON drifting apart.</item>
	/// <item>A contact value claiming more confidence than an estimate, or giving no basis.</item>
	/// <item>An outline longer or wider than the published length overall and beam, swapped bow and stern,
	/// gaps between the demihull sheer lines and the fender lines, or port and starboard not mirror images.</item>
	/// <item>Hull fender values in the wrong units or out of physical range.</item>
	/// </list>
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class ContactParameterFileTests
	{
		private readonly string _vesselFile;
		private VesselParameters _parameters;

		public ContactParameterFileTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[OneTimeSetUp]
		public void Load()
		{
			_parameters = VesselParametersJson.Load(RepoPaths.Vessel(_vesselFile));
		}

		[Test]
		public void ContactSection_IsComplete()
		{
			var contact = _parameters.Contact;
			Assert.That(contact, Is.Not.Null);
			foreach (var property in contact.GetType().GetProperties())
			{
				Assert.That(property.GetValue(contact), Is.Not.Null, $"contact.{property.Name}");
			}

			foreach (var part in ContactScenarios.OutlineParts(_parameters))
			{
				Assert.That(part.Count, Is.GreaterThanOrEqualTo(2));
				Assert.That(part.All(point => point.X != null && point.Y != null), Is.True);
			}
		}

		[Test]
		public void ContactValues_AreAllMarkedEstimated_WithABasis()
		{
			foreach (var value in Values(_parameters.Contact))
			{
				Assert.That(value.Confidence, Is.EqualTo(ParameterConfidence.Estimated), value.Source);
				Assert.That(value.Source.Length, Is.GreaterThan(20), "an estimate states its basis");
			}
		}

		[Test]
		public void ContactSection_SurvivesARoundTrip()
		{
			var copy = VesselParametersJson.Parse(VesselParametersJson.Serialize(_parameters));
			Assert.That(copy.Contact.StarboardDemihull.Count, Is.EqualTo(_parameters.Contact.StarboardDemihull.Count));
			Assert.That(copy.Contact.BowFenderLine[1].Y.Value, Is.EqualTo(_parameters.Contact.BowFenderLine[1].Y.Value));
			Assert.That(copy.Contact.HullFenderStiffness.Value, Is.EqualTo(_parameters.Contact.HullFenderStiffness.Value));
			Assert.That(new ContactModel(copy).BoundingRadius, Is.EqualTo(new ContactModel(_parameters).BoundingRadius));
		}

		[Test]
		public void Outline_SpansThePublishedLengthAndBeam()
		{
			var length = ContactScenarios.BowX(_parameters) - ContactScenarios.SternX(_parameters);
			Assert.That(length, Is.EqualTo(_parameters.Particulars.LengthOverall.Value).Within(0.02), "bow to stern is the length overall");
			Assert.That(2.0 * ContactScenarios.HalfBeam(_parameters), Is.EqualTo(_parameters.Particulars.Beam.Value).Within(0.02), "side to side is the beam");

			var halfWaterline = 0.5 * _parameters.Particulars.LengthWaterline.Value;
			Assert.That(ContactScenarios.BowX(_parameters), Is.GreaterThan(halfWaterline), "the bow overhangs the waterline");
			Assert.That(ContactScenarios.SternX(_parameters), Is.LessThan(-halfWaterline), "the stern is at or behind the waterline's aft end");
		}

		[Test]
		public void Outline_IsMirrorSymmetric()
		{
			var contact = _parameters.Contact;
			Assert.That(contact.PortDemihull.Count, Is.EqualTo(contact.StarboardDemihull.Count));
			for (var i = 0; i < contact.StarboardDemihull.Count; i++)
			{
				Assert.That(contact.PortDemihull[i].X.Value, Is.EqualTo(contact.StarboardDemihull[i].X.Value).Within(1e-9), $"point {i}");
				Assert.That(contact.PortDemihull[i].Y.Value, Is.EqualTo(-contact.StarboardDemihull[i].Y.Value).Within(1e-9), $"point {i}");
			}

			foreach (var line in new[] { contact.BowFenderLine, contact.SternFenderLine })
			{
				var first = line[0];
				var last = line[line.Count - 1];
				Assert.That(last.X.Value, Is.EqualTo(first.X.Value).Within(1e-9));
				Assert.That(last.Y.Value, Is.EqualTo(-first.Y.Value).Within(1e-9));
			}
		}

		[Test]
		public void DemihullSheerLines_RunAftToForward_AndJoinTheFenderLines()
		{
			var contact = _parameters.Contact;
			foreach (var demihull in new[] { contact.StarboardDemihull, contact.PortDemihull })
			{
				for (var i = 1; i < demihull.Count; i++)
				{
					Assert.That(demihull[i].X.Value, Is.GreaterThan(demihull[i - 1].X.Value), "sheer points run from aft to forward");
				}

				var aft = demihull[0];
				var forward = demihull[demihull.Count - 1];
				Assert.That(IsEndOf(contact.SternFenderLine, aft), Is.True, "the sheer line starts where the stern fender line ends");
				Assert.That(IsEndOf(contact.BowFenderLine, forward), Is.True, "and ends where the bow fender line ends");
			}

			Assert.That(contact.BowFenderLine.All(point => point.X.Value > 0.0), Is.True, "bow fender forward of midships");
			Assert.That(contact.SternFenderLine.All(point => point.X.Value < 0.0), Is.True, "stern fender aft of midships");
		}

		[Test]
		public void HullFender_HasPerMetreUnits_AndPhysicalValues()
		{
			var contact = _parameters.Contact;
			Assert.That(contact.HullFenderStiffness.Unit, Is.EqualTo("N/m2"), "N/m per metre of contact length");
			Assert.That(contact.HullFenderDamping.Unit, Is.EqualTo("N*s/m2"), "N·s/m per metre of contact length");
			Assert.That(contact.HullFenderMaxCompression.Unit, Is.EqualTo("m"));
			Assert.That(contact.BottomedStiffnessFactor.Unit, Is.EqualTo("1"));
			Assert.That(contact.FenderHysteresis.Unit, Is.EqualTo("1"));
			Assert.That(contact.FenderHysteresis.Value, Is.InRange(0.0, 0.5));
			Assert.That(contact.HullFenderStiffness.Value, Is.InRange(1.0e5, 1.0e8));
			Assert.That(contact.HullFenderDamping.Value, Is.InRange(0.0, 1.0e6));
			Assert.That(contact.HullFenderMaxCompression.Value, Is.InRange(0.01, 0.5));
			Assert.That(contact.BottomedStiffnessFactor.Value, Is.InRange(5.0, 200.0));
			Assert.That(contact.StarboardDemihull.All(point => point.X.Unit == "m" && point.Y.Unit == "m"), Is.True);
		}

		private static bool IsEndOf(List<OutlinePoint> line, OutlinePoint point)
		{
			foreach (var end in new[] { line[0], line[line.Count - 1] })
			{
				if (Math.Abs(end.X.Value - point.X.Value) < 1e-9 && Math.Abs(end.Y.Value - point.Y.Value) < 1e-9)
				{
					return true;
				}
			}

			return false;
		}

		private static IEnumerable<ParameterValue> Values(ContactParameters contact)
		{
			yield return contact.HullFenderStiffness;
			yield return contact.HullFenderDamping;
			yield return contact.HullFenderMaxCompression;
			yield return contact.BottomedStiffnessFactor;
			yield return contact.FenderHysteresis;
			foreach (var part in new[] { contact.StarboardDemihull, contact.PortDemihull, contact.BowFenderLine, contact.SternFenderLine })
			{
				foreach (var point in part)
				{
					yield return point.X;
					yield return point.Y;
				}
			}
		}
	}

	/// <summary>
	/// The per-metre fender units added for the contact section. Failure modes guarded: the validator
	/// rejecting N/m2 and N*s/m2 (so a contact section could never load), or accepting any unit at all.
	/// </summary>
	public class ContactUnitTests
	{
		[Test]
		public void PerMetreFenderUnits_LoadAndValidate()
		{
			var parameters = VesselParametersJson.Parse(Minimal(Fender("N/m2", "N*s/m2")));
			Assert.That(parameters.Validate(), Is.Empty);
			Assert.That(parameters.Contact.HullFenderStiffness.Value, Is.EqualTo(3.5e6));
			Assert.That(parameters.Contact.HullFenderDamping.Value, Is.EqualTo(7.0e4));
		}

		[TestCase("N/m", "N*s/m2", "contact.hullFenderStiffness.unit")]
		[TestCase("N/m2", "N*s/m", "contact.hullFenderDamping.unit")]
		[TestCase("kN/m2", "N*s/m2", "contact.hullFenderStiffness.unit")]
		public void UnknownUnit_InTheContactSection_IsRejected(string stiffnessUnit, string dampingUnit, string expectedPath)
		{
			var exception = Assert.Throws<VesselParametersException>(() => VesselParametersJson.Parse(Minimal(Fender(stiffnessUnit, dampingUnit))));
			Assert.That(exception.Errors.Any(error => error.StartsWith(expectedPath, StringComparison.Ordinal)), Is.True, string.Join("\n", exception.Errors));
		}

		private static string Estimate(string number, string unit) =>
			"{ \"value\": " + number + ", \"unit\": \"" + unit + "\", \"source\": \"estimated for the unit test\", \"confidence\": \"estimated\" }";

		private static string Fender(string stiffnessUnit, string dampingUnit) =>
			"\"contact\": { " +
			"\"hullFenderStiffness\": " + Estimate("3.5e6", stiffnessUnit) + ", " +
			"\"hullFenderDamping\": " + Estimate("7.0e4", dampingUnit) + ", " +
			"\"hullFenderMaxCompression\": " + Estimate("0.1", "m") + ", " +
			"\"bowFenderLine\": [ { \"x\": " + Estimate("13.0", "m") + ", \"y\": " + Estimate("-2.0", "m") + " }, " +
			"{ \"x\": " + Estimate("13.0", "m") + ", \"y\": " + Estimate("2.0", "m") + " } ] }";

		private static string Minimal(string body) =>
			"{ \"schemaVersion\": 1, \"id\": \"test\", \"sources\": { \"ref\": \"https://example.org\" }, " + body + " }";
	}
}
