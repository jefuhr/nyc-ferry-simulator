using FerrySim.Core.Instruments;
using FerrySim.Vessels;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	// Failure modes: raw telemetry wins over instruments; wrong axis/sign/range; stale unavailable values; both jogs move.
	public class MappedControlsTests
	{
		[TestCase("k-class")]
		[TestCase("t-class")]
		public void InstrumentsDriveEachMovingMount_ClampAndNaNRest(string id)
		{
			using var f = new InstrumentFixture(id);
			var names = new[] { "PortLever", "StarboardLever", "HelmFFU", "PortNFU", "RudderNeedle", "StarboardRudderNeedle" };
			var channels = new[] { Channel.LeverPort, Channel.LeverStarboard, Channel.TillerOrder, Channel.SteeringJog, Channel.RudderPort, Channel.RudderStarboard };
			var map = f.Root.GetComponent<WheelhouseControlMap>();
			for (var i = 0; i < names.Length; i++)
			{
				var m = map.Controls.Find(c => c.Id == names[i]);
				var limit = i >= 4 ? f.Parameters.Controls.HardOverAngle.Value : 1;
				foreach (var demand in new[] { -2.0, -1, 0, 1, 2, double.NaN })
				{
					f.Values[(int)channels[i]] = demand * limit;
					f.View.Refresh();
					var angle = double.IsNaN(demand) ? 0 : Mathf.Lerp(m.Travel.x, m.Travel.y, ((float)demand + 1) * .5f);
					Assert.That(Quaternion.Angle(m.Mount.localRotation, Quaternion.AngleAxis(angle, m.Axis)), Is.LessThan(.05), names[i] + "/" + demand);
				}
			}
		}

		[Test]
		public void OnlyNearerJogMoves_AndLossOfInstrumentsRestsExtraControls()
		{
			using var f = new InstrumentFixture();
			var eye = f.Root.transform.Find("Eye");
			var port = f.Root.transform.Find("PortNFU"); var other = f.Root.transform.Find("StarboardNFU");
			Assert.That(Vector3.Distance(eye.position, port.position), Is.LessThan(Vector3.Distance(eye.position, other.position)));
			f.Values[(int)Channel.TillerOrder] = 1; f.View.Refresh();
			Assert.That(port.localRotation, Is.EqualTo(Quaternion.identity));
			f.Values[(int)Channel.SteeringJog] = 1; f.View.Refresh();
			Assert.That(Quaternion.Angle(port.localRotation, Quaternion.identity), Is.EqualTo(20).Within(.05));
			Assert.That(other.localRotation, Is.EqualTo(Quaternion.identity));
			f.Available = false; f.View.Refresh();
			Assert.That(port.localRotation, Is.EqualTo(Quaternion.identity));
			Assert.That(Quaternion.Angle(f.Root.transform.Find("PortLever").localRotation, Quaternion.Euler(-.7f * f.View.EngineTravelDegrees, 0, 0)), Is.LessThan(.05));
		}
	}
}
