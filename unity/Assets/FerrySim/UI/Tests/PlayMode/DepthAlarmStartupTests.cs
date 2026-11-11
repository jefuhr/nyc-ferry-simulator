using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>Exercise real MonoBehaviour enable/source binding before the host's first Update.</summary>
	public class DepthAlarmStartupTests
	{
		[TestCase(true)]
		[TestCase(false)]
		public void ComponentBindsSourceBeforeItsFirstUpdate(bool sourceBeforeEnable)
		{
			var vessel = new GameObject("Depth alarm startup vessel");
			var host = new GameObject("Depth alarm startup UI");
			host.SetActive(false);
			var settings = WheelhousePanelSettings.Create();
			try
			{
				var runner = vessel.AddComponent<VesselSimulationRunner>();
				runner.RecordRuns = false;
				runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral));
				var ui = host.AddComponent<WheelhouseUI>();
				ui.ReadInterfaceKeys = false;
				ui.Configure(sourceBeforeEnable ? runner : null, settings);
				host.SetActive(true);
				Assert.That(ui.View, Is.Not.Null);
				if (!sourceBeforeEnable) ui.Source = runner;
				ui.View.DepthAlarmThresholdMetres = 100;
				runner.Driver.StepOnce(); // no host Update or manual View.Refresh has run
				Assert.That(runner.Instruments.Phase(0), Is.EqualTo(AlarmPhase.Unacknowledged));
			}
			finally { Object.DestroyImmediate(host); Object.DestroyImmediate(vessel); Object.DestroyImmediate(settings); }
		}
	}
}
