using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.EditMode
{
	// Failure modes: a live alarm's public view reads the dormant legacy model, no-data retains a
	// stale phase, settings arrive a frame late, acknowledgements leak across runs, or disposed viewers
	// keep applying preferences through a runner subscription.
	public class DepthAlarmPresentationTests
	{
		private GameObject _host;
		private VesselSimulationRunner _runner;
		private WheelhouseView _view;

		[SetUp]
		public void SetUp()
		{
			_host = new GameObject("Depth alarm presentation test");
			_runner = _host.AddComponent<VesselSimulationRunner>();
			_runner.RecordRuns = false;
			_runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral));
			_view = new WheelhouseView();
		}

		[TearDown]
		public void TearDown()
		{
			_view.Dispose();
			Object.DestroyImmediate(_host);
		}

		private static void Shown(WheelhousePanel panel, AlarmState state, bool raised, bool lit)
		{
			var shown = panel.DepthAlarmShown;
			Assert.That(shown.State, Is.EqualTo(state));
			Assert.That(shown.IsRaised, Is.EqualTo(raised));
			Assert.That(shown.IsLit, Is.EqualTo(lit));
			Assert.That(panel.Depth.AlarmLit, Is.EqualTo(lit), "the public view and actual fill agree");
		}

		private static void Phase(VesselSimulationRunner runner, AlarmPhase expected)
		{
			var readout = runner.Instruments;
			for (var i = 0; i < readout.AlarmCount; i++)
			{
				if (readout.AlarmId(i) != "navigation.shallow-water") continue;
				Assert.That(readout.Phase(i), Is.EqualTo(expected));
				return;
			}
			Assert.Fail("the real runner must define the shallow-water alarm");
		}

		[Test]
		public void PublicViewFollowsLegacyStateFlashAcknowledgementAndNoData()
		{
			var panel = _view.Panel;
			var source = StubTelemetrySource.Underway(1, 1);
			panel.Refresh(source, DistanceUnit.Metres, 1.524, 10);
			Shown(panel, AlarmState.Active, true, true);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Active));
			panel.Refresh(source, DistanceUnit.Metres, 1.524, 10.8);
			Shown(panel, AlarmState.Active, true, false);
			panel.AcknowledgeAlarm();
			panel.Refresh(source, DistanceUnit.Metres, 1.524, 11);
			Shown(panel, AlarmState.Acknowledged, true, true);
			source.HasTelemetry = false;
			panel.Refresh(source, DistanceUnit.Metres, 1.524, 12);
			Shown(panel, AlarmState.Normal, false, false);
			Assert.That(panel.Alarm.State, Is.EqualTo(AlarmState.Acknowledged), "legacy dropout state is retained");
		}

		[Test]
		public void SettingReachesNextStep_AndNewRunIsFreshWhilePauseHoldsAcknowledgement()
		{
			_view.Refresh(_runner, 0);
			_view.DepthAlarmThresholdMetres = 100;
			_runner.Driver.StepOnce(); // no intervening UI refresh may be required
			Phase(_runner, AlarmPhase.Unacknowledged);
			_view.Refresh(_runner, 10);
			Shown(_view.Panel, AlarmState.Active, true, true);
			Assert.That(_view.Panel.Alarm.State, Is.EqualTo(AlarmState.Normal), "the live path leaves the legacy model dormant");
			_view.AcknowledgeAlarms(); _runner.Pause();
			var time = _runner.State.Time;
			foreach (var realTime in new[] { 10.2, 11.1, 12.2 })
			{
				_view.Refresh(_runner, realTime);
				Phase(_runner, AlarmPhase.Acknowledged);
				Shown(_view.Panel, AlarmState.Acknowledged, true, true);
				Assert.That(_runner.State.Time, Is.EqualTo(time));
			}
			_runner.Resume(); _view.DepthAlarmThresholdMetres = 0; _runner.Driver.StepOnce();
			Phase(_runner, AlarmPhase.Normal);
			_view.DepthAlarmThresholdMetres = 100; _runner.Driver.StepOnce();
			Phase(_runner, AlarmPhase.Unacknowledged);
			_view.AcknowledgeAlarms();
			_runner.ResetRun(); _runner.Driver.StepOnce(); // reapply before the new run's first step
			Phase(_runner, AlarmPhase.Unacknowledged);
			_view.Refresh(_runner, 13);
			Shown(_view.Panel, AlarmState.Active, true, true);
			_runner.EndRun(); _view.Refresh(_runner, 14);
			Shown(_view.Panel, AlarmState.Normal, false, false);
		}

		[Test]
		public void DisposedViewStopsApplyingItsThresholdOnFutureRuns()
		{
			_view.Refresh(_runner, 0); _view.DepthAlarmThresholdMetres = 100;
			_view.Dispose();
			_runner.ResetRun(); _runner.Driver.StepOnce();
			Phase(_runner, AlarmPhase.Normal);
		}

		[Test]
		public void ShownAlarmMemberHasNoPublicOrPrivateSetter()
		{
			var property = typeof(WheelhousePanel).GetProperty("DepthAlarmShown");
			Assert.That(property, Is.Not.Null);
			Assert.That(property.GetSetMethod(true), Is.Null);
		}
	}
}
