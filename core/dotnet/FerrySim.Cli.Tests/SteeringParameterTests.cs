using System;
using System.IO;
using FerrySim.Core.Serialization;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	public class SteeringParameterTests
	{
		// A stale 35 degree divisor silently turns a requested 10 degree zigzag into 5.7 degrees.
		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void Zigzag_DefaultMaximumComesFromVessel(string vessel)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var file = scratch.File("zigzag.csv");
				var (code, _, error) = TestSupport.Run(new FakeFactory(), "maneuver", "zigzag", "--vessel", TestSupport.Vessel(vessel),
					"--out", file, "--speed-kn", "12", "--duration-s", "1");
				Assert.That(code, Is.Zero, error);
				var table = TelemetryTable.Load(file);
				var hardOver = VesselParametersJson.Load(TestSupport.Vessel(vessel)).Controls.HardOverAngle.Value * 180 / Math.PI;
				Assert.That(Math.Abs(table.Helm[table.Helm.Length - 1]), Is.EqualTo(10 / hardOver).Within(1e-4));
			}
		}

		[Test]
		public void Help_IdentifiesVesselParameterDefault()
		{
			var (_, output, _) = TestSupport.Run(new FakeFactory(), "--help");
			Assert.That(output, Does.Contain("vessel hard-over"));
		}
	}
}
