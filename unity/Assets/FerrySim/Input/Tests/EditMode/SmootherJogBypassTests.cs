using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Controls.Tests
{
	// A lagged jog value would keep the steering override alive after the operator releases it.
	public class SmootherJogBypassTests
	{
		[Test]
		public void JogChangesAndReleasesImmediately_WhileOtherChannelsStillLag()
		{
			var smoother = new ControlInputSmoother(1);
			smoother.Step(ControlInputs.Neutral);
			var pressed = smoother.Step(new ControlInputs(1, 1, 1, -1));
			Assert.That(pressed.Jog, Is.EqualTo(-1));
			Assert.That(pressed.PortLever, Is.GreaterThan(0).And.LessThan(1));
			Assert.That(smoother.Output.Jog, Is.EqualTo(-1));
			Assert.That(smoother.Step(new ControlInputs(1, 1, 1, 0)).Jog, Is.Zero);
			Assert.That(smoother.Output.Jog, Is.Zero);
		}
	}
}
