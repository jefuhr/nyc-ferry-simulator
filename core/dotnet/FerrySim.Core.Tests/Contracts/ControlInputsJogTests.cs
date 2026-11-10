using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	/// <summary>The jog (non-follow-up) input added for v0.5. Its shape is a frozen contract; see docs/contracts/README.md.</summary>
	public class ControlInputsJogTests
	{
		[Test]
		public void ThreeArgumentInputs_HaveNoJogDemand()
		{
			var inputs = new ControlInputs(0.4, -0.2, 0.7);
			Assert.That(inputs.Jog, Is.EqualTo(0.0));
			Assert.That(inputs, Is.EqualTo(new ControlInputs(0.4, -0.2, 0.7, 0.0)));
		}

		[Test]
		public void Neutral_HasNoJogDemand()
		{
			Assert.That(ControlInputs.Neutral.Jog, Is.EqualTo(0.0));
		}

		[Test]
		public void Jog_ClampsToUnitRange_AndTreatsNaNAsReleased()
		{
			Assert.That(new ControlInputs(0, 0, 0, 5.0).Jog, Is.EqualTo(1.0));
			Assert.That(new ControlInputs(0, 0, 0, -5.0).Jog, Is.EqualTo(-1.0));
			Assert.That(new ControlInputs(0, 0, 0, double.NaN).Jog, Is.EqualTo(0.0));
		}

		[Test]
		public void Equality_IncludesJog()
		{
			var held = new ControlInputs(0.4, -0.2, 0.7, 1.0);
			var released = new ControlInputs(0.4, -0.2, 0.7, 0.0);
			Assert.That(held, Is.Not.EqualTo(released));
			Assert.That(held.Equals((object)new ControlInputs(0.4, -0.2, 0.7, 1.0)), Is.True);
			Assert.That(held.GetHashCode(), Is.EqualTo(new ControlInputs(0.4, -0.2, 0.7, 1.0).GetHashCode()));
		}

		[Test]
		public void Text_IsUnchangedWithoutJog_AndNamesItOtherwise()
		{
			Assert.That(new ControlInputs(0.5, -0.5, 0.25).ToString(), Is.EqualTo("port=0.50 stbd=-0.50 helm=0.25"));
			Assert.That(new ControlInputs(0.5, -0.5, 0.25, -1.0).ToString(), Is.EqualTo("port=0.50 stbd=-0.50 helm=0.25 jog=-1.00"));
		}
	}
}
