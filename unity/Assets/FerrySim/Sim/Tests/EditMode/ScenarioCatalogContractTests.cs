using System;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The decision 0005 additions to the scenario catalog's types: notes and the current method are optional, never null,
	/// and the older constructors behave as before.
	/// </summary>
	public class ScenarioCatalogContractTests
	{
		[Test]
		public void OlderConstructors_LeaveTheNewFieldsEmpty()
		{
			var berth = new ScenarioBerth("enc:02261FA117EFB198", "Outer pontoon (east)");
			var landing = new ScenarioLanding("87", "Wall St/Pier 11", new[] { berth });
			var day = new ScenarioDay("2025-07-14", "lower-east-river/2025-07-14", TimeSpan.FromHours(-4), false, true);

			Assert.That(berth.Note, Is.Empty);
			Assert.That(landing.Note, Is.Empty);
			Assert.That(new ScenarioLanding("87", "Wall St/Pier 11").Note, Is.Empty);
			Assert.That(day.CurrentMethod, Is.Empty);
			Assert.That(day.HasTraffic, Is.True);
		}

		[Test]
		public void Notes_AndTheCurrentMethod_AreCarried()
		{
			var berth = new ScenarioBerth("derived-barge:113:02261FC76BF41C82", "Main berth", "Waiting for a crew answer on the landing side.");
			var landing = new ScenarioLanding("113", "East 90th St", new[] { berth }, "No start until the crew confirms the landing side.");
			var day = new ScenarioDay("2025-07-14", "harbor/2025-07-14", TimeSpan.FromHours(-4), false, currentMethod: "station blend");

			Assert.That(berth.Note, Is.EqualTo("Waiting for a crew answer on the landing side."));
			Assert.That(landing.Note, Is.EqualTo("No start until the crew confirms the landing side."));
			Assert.That(day.CurrentMethod, Is.EqualTo("station blend"));
		}

		[Test]
		public void NullNotes_ReadAsEmpty()
		{
			Assert.That(new ScenarioBerth("b", "Berth", null).Note, Is.Empty);
			Assert.That(new ScenarioLanding("1", "Landing", null, null).Note, Is.Empty);
			Assert.That(new ScenarioDay("2025-07-14", "harbor/2025-07-14", TimeSpan.FromHours(-4), false, false, null).CurrentMethod, Is.Empty);
		}

		[Test]
		public void RunHold_NoneIsTheDefault()
		{
			Assert.That(default(RunHold), Is.EqualTo(RunHold.None));
		}
	}
}
