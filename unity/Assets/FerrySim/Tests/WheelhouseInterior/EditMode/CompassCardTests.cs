using FerrySim.Core.Instruments;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class CompassCardTests
	{
		[TestCase("k-class")]
		[TestCase("t-class")]
		public void NorthStaysNorthThroughFullTurnAndWrap(string id)
		{
			using var f = new InstrumentFixture(id);
			var card = f.Root.transform.Find("CompassCard");
			foreach (var degrees in new[] { 0, 1, 45, 90, 135, 180, 225, 270, 315, 359, 0, 1 })
			{
				f.Root.transform.rotation = Quaternion.Euler(0, degrees, 0);
				f.Values[(int)Channel.Heading] = degrees * System.Math.PI / 180;
				f.View.Refresh();
				Assert.That(Vector3.Angle(card.forward, Vector3.forward), Is.LessThan(.05), "heading " + degrees);
			}
			f.Values[(int)Channel.Heading] = double.NaN; f.View.Refresh();
			Assert.That(card.localRotation, Is.EqualTo(Quaternion.identity));
		}
	}
}
