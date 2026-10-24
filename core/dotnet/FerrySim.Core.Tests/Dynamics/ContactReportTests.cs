using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// Typical landings for both classes, written to <c>artifacts/core/contact-report.md</c>: bow-on at 1 kn
	/// and a hard 3 kn landing on a barge face, with and without fender hysteresis, bow-on holds, and what a
	/// side-to hold with one engine does. The landing fenders are estimates (see
	/// <see cref="ContactScenarios.EstimatedBargeFender"/>), so the numbers show the model's behaviour, not
	/// measured NYC Ferry landings. Failure modes guarded: a harder landing that hits more softly, a bounce
	/// faster than the approach, hysteresis that makes a bounce livelier, and a gentle landing that bottoms
	/// the fender out.
	/// </summary>
	public class ContactReportTests
	{
		private static readonly string[] Classes = { "t-class.json", "k-class.json" };
		private static readonly double[] Speeds = { 1.0, 3.0 };
		private readonly Dictionary<string, Results> _results = new Dictionary<string, Results>();

		[OneTimeSetUp]
		public void RunLandings()
		{
			foreach (var file in Classes)
			{
				var parameters = ManeuverRunner.Load(file);
				var withoutHysteresis = ManeuverRunner.Load(file);
				withoutHysteresis.Contact.FenderHysteresis.Value = 0.0;
				var results = new Results { Parameters = parameters };
				foreach (var knots in Speeds)
				{
					var speed = Units.KnotsToMetresPerSecond(knots);
					results.Estimated.Add(ContactScenarios.BowOnLanding(parameters, ContactScenarios.EstimatedBargeFender, speed));
					results.Soft.Add(ContactScenarios.BowOnLanding(parameters, ContactScenarios.ContractTestFender, speed));
					results.EstimatedWithoutHysteresis.Add(ContactScenarios.BowOnLanding(withoutHysteresis, ContactScenarios.EstimatedBargeFender, speed));
					results.SoftWithoutHysteresis.Add(ContactScenarios.BowOnLanding(withoutHysteresis, ContactScenarios.ContractTestFender, speed));
				}

				var idle = ContactScenarios.IdleLever(parameters);
				results.OneEngineHold = BowOnHold(parameters, new ControlInputs(idle, 0.0, -0.5));
				results.OneEngineHoldWithoutHysteresis = BowOnHold(withoutHysteresis, new ControlInputs(idle, 0.0, -0.5));
				results.OneEngineHardOver = BowOnHold(parameters, new ControlInputs(idle, 0.0, -1.0));
				results.BothEngineHold = BowOnHold(parameters, new ControlInputs(idle, idle, 0.0));
				results.SideToOutboard = SideTo(parameters, new ControlInputs(idle, 0.0, 1.0));
				results.SideToInboard = SideTo(parameters, new ControlInputs(0.0, idle, -1.0));
				_results[file] = results;
			}

			WriteReport();
		}

		[TestCaseSource(nameof(Classes))]
		public void HarderLanding_HitsHarder_AndBouncesSlowerThanItArrives(string file)
		{
			foreach (var set in new[] { _results[file].Estimated, _results[file].Soft })
			{
				Assert.That(set[1].PeakForce, Is.GreaterThan(set[0].PeakForce));
				Assert.That(set[1].PeakCompression, Is.GreaterThan(set[0].PeakCompression));
				foreach (var landing in set)
				{
					Assert.That(landing.Separated, Is.True, "coasting in neutral, the boat bounces off");
					Assert.That(landing.ReboundSpeed, Is.GreaterThan(0.0).And.LessThan(landing.ApproachSpeed));
				}
			}
		}

		[TestCaseSource(nameof(Classes))]
		public void FenderHysteresis_TakesEnergyOutOfEveryBounce(string file)
		{
			var r = _results[file];
			for (var i = 0; i < Speeds.Length; i++)
			{
				Assert.That(r.Estimated[i].ReboundSpeed, Is.LessThan(r.EstimatedWithoutHysteresis[i].ReboundSpeed));
				Assert.That(r.Soft[i].ReboundSpeed, Is.LessThan(r.SoftWithoutHysteresis[i].ReboundSpeed));
			}
		}

		[TestCaseSource(nameof(Classes))]
		public void GentleLanding_StaysOnTheFender(string file)
		{
			Assert.That(_results[file].Estimated[0].BottomedOut, Is.False, "1 kn bow-on does not bottom out the estimated barge fender");
		}

		private static Hold BowOnHold(VesselParameters parameters, ControlInputs controls)
		{
			var geometry = new SegmentListGeometry(ContactScenarios.Barge(ContactScenarios.BowX(parameters) + 0.05, 0.0, ContactScenarios.EstimatedBargeFender, 1));
			var simulation = ContactScenarios.Create(parameters, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			ManeuverRunner.Run(simulation, controls, 60.0);
			return new Hold
			{
				Force = simulation.Telemetry.ContactForce,
				Compression = ContactScenarios.MaxCompression(simulation),
				Heading = Angles.WrapPi(simulation.State.Heading),
				Speed = simulation.Telemetry.SpeedOverGround,
				Slid = simulation.State.East,
				InContact = simulation.ContactCount > 0,
			};
		}

		/// <summary>
		/// Starboard side 5 cm off a long face running north, one engine at the ahead idle detent and the helm
		/// hard over. Reported, not asserted: without a line, net ahead thrust slides the boat along the face.
		/// </summary>
		private static Hold SideTo(VesselParameters parameters, ControlInputs controls)
		{
			var face = ContactScenarios.FaceToTheEast(ContactScenarios.HalfBeam(parameters) + 0.05, -100.0, 400.0, ContactScenarios.EstimatedBargeFender, 1);
			var simulation = ContactScenarios.Create(parameters, new SegmentListGeometry(new[] { face }), VesselState.Planar(0.0, 0.0, 0.0, 0.0));
			ManeuverRunner.Run(simulation, controls, 60.0);
			return new Hold
			{
				Force = simulation.Telemetry.ContactForce,
				Compression = ContactScenarios.MaxCompression(simulation),
				Heading = Angles.WrapPi(simulation.State.Heading),
				Speed = simulation.Telemetry.SpeedOverGround,
				Slid = simulation.State.North,
				InContact = simulation.ContactCount > 0,
			};
		}

		private void WriteReport()
		{
			var text = new StringBuilder();
			var c = CultureInfo.InvariantCulture;
			var estimated = ContactScenarios.EstimatedBargeFender;
			var soft = ContactScenarios.ContractTestFender;
			text.AppendLine("# Contact report");
			text.AppendLine();
			text.AppendLine("Generated by `ContactReportTests` (calm deep water). The landing fenders are **estimates**, not surveyed or");
			text.AppendLine("published properties of any NYC Ferry landing, and how the boats land is still a crew question. Read the");
			text.AppendLine("numbers as the model's behaviour.");
			text.AppendLine();
			text.AppendLine(string.Format(c, "- Estimated elastomeric barge fender: {0:0.0e0} N/m per m, {1:0.0e0} N·s/m per m, friction {2:F2}, max compression {3:F2} m.",
				estimated.Stiffness, estimated.Damping, estimated.Friction, estimated.MaxCompression));
			text.AppendLine(string.Format(c, "- Softer contract-test fender: {0:0.0e0} N/m per m, {1:0.0e0} N·s/m per m, friction {2:F2}, max compression {3:F2} m.",
				soft.Stiffness, soft.Damping, soft.Friction, soft.MaxCompression));
			text.AppendLine("- Landings: coasting in neutral, bow-on into the long face of a 90 ft × 35 ft barge. Compression is the total of the hull and landing fenders.");
			text.AppendLine("- Rebound speed is the speed away from the face when contact ends; restitution is rebound over approach speed.");
			text.AppendLine("- Fender hysteresis h is the shipped `contact.fenderHysteresis` (0.2, estimated) or switched off (0) for comparison.");
			foreach (var file in Classes)
			{
				var r = _results[file];
				var contact = r.Parameters.Contact;
				text.AppendLine();
				text.AppendLine("## " + r.Parameters.DisplayName);
				text.AppendLine();
				text.AppendLine(string.Format(c, "Hull fender (estimated): {0:0.0e0} N/m per m, {1:0.0e0} N·s/m per m, max compression {2:F2} m; bottomed stiffness × {3:F0}.",
					contact.HullFenderStiffness.Value, contact.HullFenderDamping.Value, contact.HullFenderMaxCompression.Value, contact.BottomedStiffnessFactor.Value));
				text.AppendLine();
				text.AppendLine("| landing | fender | h | approach (kn) | peak force (kN) | peak compression (mm) | bottomed out | rebound (kn) | restitution | contact (s) |");
				text.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
				for (var i = 0; i < Speeds.Length; i++)
				{
					LandingRow(text, c, Speeds[i], "estimated barge", "0.2", r.Estimated[i]);
					LandingRow(text, c, Speeds[i], "estimated barge", "0", r.EstimatedWithoutHysteresis[i]);
					LandingRow(text, c, Speeds[i], "softer (contract test)", "0.2", r.Soft[i]);
					LandingRow(text, c, Speeds[i], "softer (contract test)", "0", r.SoftWithoutHysteresis[i]);
				}

				text.AppendLine();
				text.AppendLine("Holding for 60 s, bow against the barge face (estimated fender):");
				text.AppendLine();
				text.AppendLine("| hold | contact force (kN) | compression (mm) | heading (°) | speed (mm/s) | slid along face (m) | in contact |");
				text.AppendLine("|---|---|---|---|---|---|---|");
				HoldRow(text, c, "bow-on, port engine ahead idle, helm half to port", r.OneEngineHold);
				HoldRow(text, c, "bow-on (reported), as above without fender hysteresis", r.OneEngineHoldWithoutHysteresis);
				HoldRow(text, c, "bow-on (reported), port engine ahead idle, helm hard to port", r.OneEngineHardOver);
				HoldRow(text, c, "bow-on, both engines ahead idle", r.BothEngineHold);
				HoldRow(text, c, "side-to (reported), outboard port engine ahead idle, helm hard to starboard", r.SideToOutboard);
				HoldRow(text, c, "side-to (reported), inboard starboard engine ahead idle, helm hard to port", r.SideToInboard);
			}

			var directory = Path.Combine(RepoPaths.Root, "artifacts", "core");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "contact-report.md"), text.ToString());
			TestContext.Out.Write(text.ToString());
		}

		private static void LandingRow(StringBuilder text, CultureInfo c, double knots, string fender, string hysteresis, LandingResult landing) =>
			text.AppendLine(string.Format(c, "| {0:F0} kn bow-on | {1} | {2} | {3:F2} | {4:F0} | {5:F0} | {6} | {7:F2} | {8:F2} | {9:F2} |",
				knots, fender, hysteresis, Units.MetresPerSecondToKnots(landing.ApproachSpeed), landing.PeakForce / 1000.0, landing.PeakCompression * 1000.0,
				landing.BottomedOut ? "yes" : "no", Units.MetresPerSecondToKnots(landing.ReboundSpeed), landing.ReboundSpeed / landing.ApproachSpeed,
				landing.ContactDuration));

		private static void HoldRow(StringBuilder text, CultureInfo c, string name, Hold hold) =>
			text.AppendLine(string.Format(c, "| {0} | {1:F2} | {2:F1} | {3:F1} | {4:F1} | {5:F2} | {6} |",
				name, hold.Force / 1000.0, hold.Compression * 1000.0, Angles.ToDegrees(hold.Heading), hold.Speed * 1000.0, hold.Slid,
				hold.InContact ? "yes" : "no"));

		private sealed class Hold
		{
			public double Force;
			public double Compression;
			public double Heading;
			public double Speed;
			public double Slid;
			public bool InContact;
		}

		private sealed class Results
		{
			public VesselParameters Parameters;
			public readonly List<LandingResult> Estimated = new List<LandingResult>();
			public readonly List<LandingResult> Soft = new List<LandingResult>();
			public readonly List<LandingResult> EstimatedWithoutHysteresis = new List<LandingResult>();
			public readonly List<LandingResult> SoftWithoutHysteresis = new List<LandingResult>();
			public Hold OneEngineHold;
			public Hold OneEngineHoldWithoutHysteresis;
			public Hold OneEngineHardOver;
			public Hold BothEngineHold;
			public Hold SideToOutboard;
			public Hold SideToInboard;
		}
	}
}
