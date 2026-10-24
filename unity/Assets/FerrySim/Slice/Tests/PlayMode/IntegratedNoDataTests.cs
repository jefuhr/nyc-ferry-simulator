using System.Collections;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (a) No data, on the real bridge: before the first physics step of a run and after the run ends, every
	/// instrument on the panel, the HUD and the alarms show no data. Failure modes guarded: the runner's default
	/// state (heading 000, 0.0 kn, NEUTRAL, 0 m depth) or the last run's values shown as if live, an alarm raised
	/// from a reading that is not current, and a contact indicator saying NONE when nothing is known.
	/// </summary>
	public class IntegratedNoDataTests
	{
		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator BeforeTheFirstStep_AndAfterTheRunEnds_EveryInstrumentHudAndAlarmShowsNoData()
		{
			_scene.Bootstrap.SwitchPanelWithCamera = false;
			var view = _scene.View;
			view.PanelVisible = true;
			view.HudVisible = true;
			// Any depth reading would raise this alarm, so a reading shown without data would be caught.
			view.DepthAlarmThresholdMetres = 100.0;
			_scene.StepsPerFrame(5);

			// A new run, paused before its first step.
			_scene.Restart(_scene.Approach(20.0, "no-data"), new ConstantControls(new ControlInputs(0.15, 0.15, 0.0)));
			_scene.Runner.Pause();
			yield return SliceScene.Frames(4);
			Assert.That(_scene.Runner.Driver.StepCount, Is.EqualTo(0), "paused before the first step");
			Assert.That(_scene.Runner.HasTelemetry, Is.False);
			AssertNoData("before the first step");

			// Data flows once the run steps: the same instruments come alive and the alarm raises.
			_scene.Runner.Resume();
			yield return SliceScene.Frames(30);
			Assert.That(_scene.Runner.HasTelemetry, Is.True);
			var panel = _scene.Panel;
			Assert.That(panel.ShowingNoData, Is.False);
			Assert.That(panel.Heading.text, Is.Not.EqualTo(InstrumentFormat.NoValue));
			Assert.That(panel.Depth.Value.text, Is.Not.EqualTo(InstrumentFormat.NoValue));
			Assert.That(panel.Alarm.IsRaised, Is.True, "48 m under the keel is below a 100 m threshold");
			Assert.That(panel.Contact.Value.text, Is.EqualTo("NONE"));
			Assert.That(_scene.Hud.Speed.text, Is.Not.EqualTo(InstrumentFormat.NoValue));
			Assert.That(panel.Port.Tachometer.NeedleVisible, Is.True);

			// The run ends: its last values are no longer current.
			_scene.Runner.EndRun();
			yield return SliceScene.Frames(4);
			Assert.That(_scene.Runner.HasTelemetry, Is.False);
			AssertNoData("after the run ends");
		}

		private void AssertNoData(string when)
		{
			var panel = _scene.Panel;
			var hud = _scene.Hud;
			Assert.That(panel.ShowingNoData, Is.True, $"{when}: NO DATA badge");
			var labels = new List<Label>
			{
				panel.Heading, panel.SpeedOverGround, panel.CourseOverGround, panel.SpeedThroughWater, panel.Depth.Value, panel.Contact.Value,
				panel.Clock, panel.Elapsed, panel.Port.ShaftRpm, panel.Port.LeverPercent, panel.Port.LeverDirection,
				panel.Starboard.ShaftRpm, panel.Starboard.LeverPercent, panel.Starboard.LeverDirection,
				hud.Speed, hud.Heading, hud.PortGear, hud.StarboardGear, hud.RudderText,
			};
			foreach (var label in labels)
			{
				Assert.That(label.text, Is.EqualTo(InstrumentFormat.NoValue), $"{when}: {label.name}");
			}

			foreach (var engine in new[] { panel.Port, panel.Starboard })
			{
				Assert.That(engine.Gear.Text, Is.EqualTo(InstrumentFormat.NoValue), $"{when}: {engine.Side} gear");
				Assert.That(engine.Tachometer.ReadoutText, Is.EqualTo(InstrumentFormat.NoValue), $"{when}: {engine.Side} rpm");
				Assert.That(engine.Tachometer.NeedleVisible, Is.False, $"{when}: {engine.Side} tachometer needle");
				Assert.That(double.IsNaN(engine.Lever.Lever), Is.True, $"{when}: {engine.Side} lever");
			}

			Assert.That(double.IsNaN(panel.Compass.HeadingDegrees), Is.True, $"{when}: compass card");
			Assert.That(double.IsNaN(panel.Compass.CourseDegrees), Is.True, $"{when}: course vector");
			Assert.That(panel.Rudder.PortText, Is.EqualTo(InstrumentFormat.NoValue), $"{when}: port rudder");
			Assert.That(panel.Rudder.StarboardText, Is.EqualTo(InstrumentFormat.NoValue), $"{when}: starboard rudder");
			Assert.That(double.IsNaN(panel.Rudder.PortDegrees) && double.IsNaN(panel.Rudder.StarboardDegrees), Is.True, $"{when}: rudder pointers");
			Assert.That(panel.Depth.AlarmLit, Is.False, $"{when}: no depth alarm without a current reading");
			Assert.That(panel.Contact.Indication, Is.EqualTo(ContactIndication.Unknown), $"{when}: contact is unknown, not NONE");
			Assert.That(double.IsNaN(hud.PortLever.Lever) && double.IsNaN(hud.StarboardLever.Lever), Is.True, $"{when}: HUD levers");
			Assert.That(double.IsNaN(hud.Rudder.PortDegrees) && double.IsNaN(hud.Rudder.StarboardDegrees), Is.True, $"{when}: HUD rudder");
		}
	}
}
