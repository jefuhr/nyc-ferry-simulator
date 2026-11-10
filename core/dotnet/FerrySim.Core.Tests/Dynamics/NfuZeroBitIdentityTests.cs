using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	public class NfuZeroBitIdentityTests
	{
		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void ThreeAndFourArgumentInputs_AgreeInEveryBit(string file)
		{
			var p = ManeuverRunner.Load(file); var a = ManeuverRunner.Create(p); var b = ManeuverRunner.Create(p);
			for (var i = 0; i < 4000; i++)
			{
				var lever = i < 1000 ? .7 : -.3; var helm = i < 2000 ? .4 : -.8;
				a.Step(new ControlInputs(lever, .3, helm)); b.Step(new ControlInputs(lever, .3, helm, 0));
				Assert.That(BitIdentity.Differences(a, b), Is.Empty, $"step {i}");
			}
		}
	}
}
