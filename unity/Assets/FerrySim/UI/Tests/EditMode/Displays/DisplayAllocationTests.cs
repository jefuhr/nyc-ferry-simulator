using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// The displays refresh every rendered frame, on the console and in the head-up display, so their steady state
	/// must make no garbage (the UI README's zero-allocation policy, measured as in RefreshAllocationTests). Guards
	/// against: number formatting that builds a string per frame, lamps that box an enum or build a colour object,
	/// and alarm lists that rebuild their rows every frame. The values move over a repeating cycle; warm-up shows the
	/// cycle once, so every string the display can need has been made before measuring.
	/// </summary>
	public class DisplayAllocationTests
	{
		private const int Cycle = 240;

		[Test]
		public void BothEngineMonitors_WithMovingValues_AllocateNothing()
		{
			var library = new DisplaySkinLibrary(SkinJson.TClassAlarms, privateFolder: null);
			var port = new EngineMonitorView(DisplaySide.Port, library.Load("engine", "generic-a"));
			var starboard = new EngineMonitorView(DisplaySide.Starboard, library.Load("engine", "generic-a"));
			var readout = FakeInstrumentReadout.TClass();
			for (var frame = 0; frame < Cycle; frame++)
			{
				Move(readout, frame);
				port.Refresh(readout, frame * 0.1);
				starboard.Refresh(readout, frame * 0.1);
			}

			Assert.That(() =>
			{
				for (var frame = Cycle; frame < 2 * Cycle; frame++)
				{
					Move(readout, frame);
					port.Refresh(readout, frame * 0.1);
					starboard.Refresh(readout, frame * 0.1);
				}
			}, Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void TheAlarmPages_WithFlashingLamps_AllocateNothing()
		{
			var library = new DisplaySkinLibrary(SkinJson.TClassAlarms, privateFolder: null);
			var panel = new AlarmPanelView(library.Load("alarm-panel", "generic-a"));
			var engine = new EngineMonitorView(DisplaySide.Port, library.Load("engine", "generic-a"));
			engine.NextPage();
			var readout = new FakeInstrumentReadout(
				("steering.failure", AlarmGroup.Steering), ("engine.port.overspeed", AlarmGroup.Engine),
				("navigation.shallow-water", AlarmGroup.Navigation), ("hull.contact", AlarmGroup.Hull));
			for (var frame = 0; frame < Cycle; frame++)
			{
				MoveAlarms(readout, frame);
				panel.Refresh(readout, frame * 0.05);
				engine.Refresh(readout, frame * 0.05);
			}

			Assert.That(() =>
			{
				for (var frame = Cycle; frame < 2 * Cycle; frame++)
				{
					MoveAlarms(readout, frame);
					panel.Refresh(readout, frame * 0.05);
					engine.Refresh(readout, frame * 0.05);
				}
			}, Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void WithoutAReadout_TheDisplaysAllocateNothing()
		{
			var library = new DisplaySkinLibrary(SkinJson.TClassAlarms, privateFolder: null);
			var panel = new AlarmPanelView(library.Load("alarm-panel", "generic-a"));
			var engine = new EngineMonitorView(DisplaySide.Port, library.Load("engine", "generic-a"));
			for (var frame = 0; frame < 3; frame++)
			{
				panel.Refresh(null, frame);
				engine.Refresh(null, frame);
			}

			Assert.That(() =>
			{
				panel.Refresh(null, 5.0);
				engine.Refresh(null, 5.0);
			}, Is.Not.AllocatingGCMemory());
		}

		/// <summary>Every engine channel sweeps its normal range and beyond, with a lost reading now and then.</summary>
		private static void Move(FakeInstrumentReadout readout, int frame)
		{
			var phase = frame % Cycle / (double)Cycle;
			var lost = frame % Cycle >= 230;
			// A for loop, not foreach over a new array: the measured block must not allocate either.
			for (var side = 0; side < 2; side++)
			{
				var speed = side == 0 ? Channel.EnginePortSpeed : Channel.EngineStarboardSpeed;
				var offset = (int)speed - (int)Channel.EnginePortSpeed;
				readout.Set(speed, lost ? double.NaN : Units.RpmToRadiansPerSecond(500 + 2200 * phase));
				readout.Set(Channel.EnginePortShaftSpeed + offset, Units.RpmToRadiansPerSecond(-400 + 900 * phase));
				readout.Set(Channel.EnginePortShiftPhase + offset, frame % 7);
				readout.Set(Channel.EnginePortLoad + offset, -0.1 + 1.3 * phase);
				readout.Set(Channel.EnginePortOilPressure + offset, 150000 + 300000 * phase);
				readout.Set(Channel.EnginePortCoolantTemperature + offset, 340 + 30 * phase);
				readout.Set(Channel.EnginePortExhaustTemperature + offset, 420 + 420 * phase);
				readout.Set(Channel.EnginePortBoostPressure + offset, 200000 * phase);
				readout.Set(Channel.EnginePortFuelRate + offset, 0.001 + 0.04 * phase);
				readout.Set(Channel.EnginePortGearOilPressure + offset, 1400000 + 300000 * phase);
			}

			readout.Set(Channel.LeverPort, -1 + 2 * phase);
			readout.Set(Channel.LeverStarboard, 1 - 2 * phase);
			readout.Set(Channel.BusVoltage, 26 + 3 * phase);
		}

		private static void MoveAlarms(FakeInstrumentReadout readout, int frame)
		{
			for (var alarm = 0; alarm < readout.AlarmCount; alarm++)
			{
				readout.SetPhase(alarm, (AlarmPhase)((frame / 20 + alarm) % 4));
			}

			readout.LampTest = frame % Cycle >= 200;
		}
	}
}
