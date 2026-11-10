using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>Viewer observations and alarm actions must never feed back into physics or its digest.</summary>
	public class InstrumentRenderOnlyTests
	{
		private static string Digest(bool attach, bool actions)
		{
			var simulation = new VesselDynamicsFactory().Create(TestSupport.LoadTClass(), UniformEnvironment.Calm, default);
			var driver = new FixedStepDriver(simulation, new NfuReplayTests.JogControls());
			var model = InstrumentObserverTests.Model();
			if (attach) driver.AddObserver(InstrumentObserverTests.Observer(model, simulation));
			var digest = new StateDigest();
			for (var i = 0; i < 1000; i++)
			{
				driver.StepOnce();
				if (actions) { model.Acknowledge(); model.Silence(); model.SetLampTest(i % 2 == 0); }
				digest.Add(simulation.State);
			}
			return digest.Text;
		}

		[Test]
		public void DigestIsIdenticalWithObserverDetachedAttachedOrAcknowledged()
		{
			var baseline = Digest(false, false);
			Assert.That(Digest(true, false), Is.EqualTo(baseline));
			Assert.That(Digest(true, true), Is.EqualTo(baseline));
		}
	}
}
