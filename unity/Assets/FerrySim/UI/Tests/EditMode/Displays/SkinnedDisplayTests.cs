using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine.UIElements;
using static FerrySim.UI.Tests.EditMode.Displays.SkinJson;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// A skinned display against a fake readout. Failure modes guarded: a needle that runs past the end of its dial or
	/// points somewhere off the scale; a missing reading that reads as zero (a stopped engine and a lost sensor must
	/// never look alike); rounding that differs from the panel's; a page switch that leaves the old page's elements
	/// showing; the other engine's channel shown; and a lamp that does not follow the alarm sequence of contract C1.
	/// </summary>
	public class SkinnedDisplayTests
	{
		private FakeInstrumentReadout _readout;
		private double _time;

		[SetUp]
		public void SetUp()
		{
			_readout = FakeInstrumentReadout.TClass();
			_time = 0.0;
		}

		private SkinnedDisplayView EngineView(DisplaySide side = DisplaySide.Port) =>
			new SkinnedDisplayView(DisplaySkin.Parse(EveryElementType(), TClassAlarms), side);

		/// <summary>Refreshes well past the skin's refresh interval, so each call shows the readout as it is now.</summary>
		private void Refresh(SkinnedDisplayView view)
		{
			_time += 1.0;
			view.Refresh(_readout, _time);
		}

		[TestCase(0.0, -135f)]
		[TestCase(1200.0, 0f)]
		[TestCase(2400.0, 135f)]
		[TestCase(600.0, -67.5f)]
		[TestCase(3000.0, 135f)]
		[TestCase(-200.0, -135f)]
		public void Dial_PointsAlongItsSweep_AndPegsAtBothEnds(double rpm, float degrees)
		{
			var view = EngineView();
			var dial = (DialView)view.Elements[1];
			_readout.Set(Channel.EnginePortSpeed, Units.RpmToRadiansPerSecond(rpm));
			Refresh(view);

			Assert.That(dial.NeedleDegrees, Is.EqualTo(degrees).Within(1e-3f));
			Assert.That(dial.NeedleVisible, Is.True);
			Assert.That(dial.Needle.style.rotate.value.angle.value, Is.EqualTo(degrees).Within(1e-3f), "the needle is a rotated element");
		}

		[Test]
		public void DialAngle_HandlesAReversedSweep()
		{
			Assert.That(DialView.Angle(0.0, 0.0, 100.0, 90f, -90f), Is.EqualTo(90f));
			Assert.That(DialView.Angle(25.0, 0.0, 100.0, 90f, -90f), Is.EqualTo(45f).Within(1e-4f));
			Assert.That(DialView.Angle(500.0, 0.0, 100.0, 90f, -90f), Is.EqualTo(-90f));
		}

		[Test]
		public void Dial_ShowsTheOtherEngine_ForTheOtherSide()
		{
			var view = EngineView(DisplaySide.Starboard);
			_readout.Set(Channel.EnginePortSpeed, Units.RpmToRadiansPerSecond(2400));
			_readout.Set(Channel.EngineStarboardSpeed, Units.RpmToRadiansPerSecond(1200));
			Refresh(view);

			Assert.That(((DialView)view.Elements[1]).NeedleDegrees, Is.EqualTo(0f).Within(1e-3f));
			Assert.That(((DialView)view.Elements[1]).ReadoutText, Is.EqualTo("1200"));
			Assert.That(((FrameView)view.Elements[0]).CaptionText, Is.EqualTo("STARBOARD ENGINE"));
		}

		[Test]
		public void Dial_WithoutAReading_HidesTheNeedle_AndShowsDashes()
		{
			var view = EngineView();
			var dial = (DialView)view.Elements[1];
			_readout.Set(Channel.EnginePortSpeed, Units.RpmToRadiansPerSecond(900));
			Refresh(view);
			_readout.Set(Channel.EnginePortSpeed, double.NaN);
			Refresh(view);

			Assert.That(dial.NeedleVisible, Is.False);
			Assert.That(dial.ReadoutText, Is.EqualTo(SkinValueText.NoValue));
			Assert.That(SkinValueText.NoValue, Is.EqualTo("---"));
		}

		[TestCase(0.25, 0.25f)]
		[TestCase(0.0, 0f)]
		[TestCase(1.0, 1f)]
		[TestCase(1.5, 1f)]
		[TestCase(-0.2, 0f)]
		public void Bar_FillsToTheValue_Clamped(double load, float fraction)
		{
			var view = EngineView();
			var bar = (BarView)view.Elements[2];
			_readout.Set(Channel.EnginePortLoad, load);
			Refresh(view);

			Assert.That(bar.Fraction, Is.EqualTo(fraction).Within(1e-5f));
			Assert.That(bar.FillVisible, Is.True);
			Assert.That(bar.Fill.style.scale.value.value.x, Is.EqualTo(fraction).Within(1e-5f), "the fill is scaled, not relaid out");
		}

		[Test]
		public void Bar_WithoutAReading_ShowsNoFill_AndDashes()
		{
			var view = EngineView();
			var bar = (BarView)view.Elements[2];
			_readout.Set(Channel.EnginePortLoad, double.NaN);
			Refresh(view);

			Assert.That(bar.FillVisible, Is.False);
			Assert.That(bar.ReadoutText, Is.EqualTo("---"));
		}

		[TestCase(344737.86465841805, "50.0")]
		[TestCase(0.0, "0.0")]
		[TestCase(-0.01, "0.0")]
		[TestCase(6894.757293168361 * 12.26, "12.3")]
		[TestCase(6894.757293168361 * 150.04, "150.0")]
		[TestCase(6894.757293168361 * 1234.56, "1234.6")]
		[TestCase(double.NaN, "---")]
		[TestCase(double.PositiveInfinity, "---")]
		public void Value_ConvertsAndRoundsToItsDecimals(double pascals, string text)
		{
			var view = EngineView();
			var value = (ValueView)view.Elements[3];
			_readout.Set(Channel.EnginePortOilPressure, pascals);
			Refresh(view);

			Assert.That(value.ValueText, Is.EqualTo(text));
			Assert.That(value.CaptionText, Is.EqualTo("OIL PRESS"));
			Assert.That(value.UnitText, Is.EqualTo("psi"));
		}

		[Test]
		public void Value_WithZeroDecimals_RoundsHalfAwayFromZero_LikeThePanel()
		{
			var skin = DisplaySkin.Parse(Skin("{'type': 'value', 'rect': [0, 0, 200, 60], 'channel': 'engine.{side}.exhaust-temperature', 'unit': 'degC', 'decimals': 0}"), TClassAlarms);
			var view = new SkinnedDisplayView(skin, DisplaySide.Port);
			var value = (ValueView)view.Elements[0];
			foreach (var (kelvin, text) in new[] { (273.15 + 412.51, "413"), (273.15 + 412.49, "412"), (273.15 - 0.4, "0"), (273.15 - 2.6, "-3") })
			{
				_readout.Set(Channel.EnginePortExhaustTemperature, kelvin);
				Refresh(view);
				Assert.That(value.ValueText, Is.EqualTo(text), kelvin.ToString());
			}
		}

		[TestCase(0.0, 0.0, "NEUTRAL")]
		[TestCase(4.0, 0.6, "AHEAD")]
		[TestCase(4.0, -0.6, "ASTERN")]
		[TestCase(1.0, 0.6, "SHIFTING")]
		[TestCase(2.0, 0.6, "SHIFTING")]
		[TestCase(3.0, -0.6, "SHIFTING")]
		[TestCase(5.0, 0.0, "SHIFTING")]
		[TestCase(6.0, -0.6, "SHIFTING")]
		[TestCase(double.NaN, 0.6, "---")]
		[TestCase(17.0, 0.6, "---")]
		public void GearFormat_ShowsTheGearAsAWord_AsTheControlModelDefinesIt(double phase, double lever, string word)
		{
			var view = EngineView();
			_readout.Set(Channel.EnginePortShiftPhase, phase);
			_readout.Set(Channel.LeverPort, lever);
			Refresh(view);

			Assert.That(((ValueView)view.Elements[4]).ValueText, Is.EqualTo(word));
		}

		[Test]
		public void GearFormat_WithTheLeverUnknown_TakesDirectionFromTheShaft()
		{
			var view = EngineView();
			_readout.Set(Channel.EnginePortShiftPhase, 4.0);
			_readout.Set(Channel.LeverPort, double.NaN);
			_readout.Set(Channel.EnginePortShaftSpeed, -20.0);
			Refresh(view);
			Assert.That(((ValueView)view.Elements[4]).ValueText, Is.EqualTo("ASTERN"));
		}

		[Test]
		public void Pages_StartOnTheFirst_AndSwitchByNameOrInTurn()
		{
			var view = EngineView();
			var changes = new List<string>();
			view.PageChanged += changes.Add;
			var dial = view.Elements[1];
			var lamp = view.Elements[5];
			var frame = view.Elements[0];

			Assert.That(view.CurrentPage, Is.EqualTo("main"));
			Assert.That(dial.OnPage, Is.True);
			Assert.That(lamp.OnPage, Is.False);
			Assert.That(lamp.style.display.value, Is.EqualTo(DisplayStyle.None));
			Assert.That(frame.OnPage, Is.True, "an element without a page is on every page");

			Assert.That(view.ShowPage("alarms"), Is.True);
			Assert.That(dial.OnPage, Is.False);
			Assert.That(dial.style.display.value, Is.EqualTo(DisplayStyle.None));
			Assert.That(lamp.OnPage, Is.True);
			Assert.That(frame.OnPage, Is.True);

			Assert.That(view.ShowPage("trends"), Is.False, "an unknown page changes nothing");
			Assert.That(view.CurrentPage, Is.EqualTo("alarms"));

			view.NextPage();
			Assert.That(view.CurrentPage, Is.EqualTo("main"), "next page wraps to the first");
			view.NextPage();
			Assert.That(view.CurrentPage, Is.EqualTo("alarms"));
			Assert.That(changes, Is.EqualTo(new[] { "alarms", "main", "alarms" }));
		}

		[Test]
		public void ASoftkeyWithATarget_OpensItsPage_AndShowsItIsSelected()
		{
			var view = EngineView();
			var mainKey = (SoftkeyView)view.Elements[8];
			var alarmsKey = (SoftkeyView)view.Elements[9];
			Assert.That(mainKey.Selected, Is.True);
			Assert.That(alarmsKey.Selected, Is.False);

			view.PressSoftkey(9);
			view.ReleaseSoftkey(9);

			Assert.That(view.CurrentPage, Is.EqualTo("alarms"));
			Assert.That(mainKey.Selected, Is.False);
			Assert.That(alarmsKey.Selected, Is.True);
		}

		[Test]
		public void ASoftkeyWithAnAction_RaisesIt_AndChangesNoPage()
		{
			var view = EngineView();
			var acknowledged = 0;
			view.AcknowledgeRequested += () => acknowledged++;
			view.PressSoftkey(10);
			view.ReleaseSoftkey(10);

			Assert.That(acknowledged, Is.EqualTo(1));
			Assert.That(view.CurrentPage, Is.EqualTo("main"));
			Assert.Throws<System.ArgumentException>(() => view.PressSoftkey(1), "element 2 is a dial, not a softkey");
		}

		[Test]
		public void AValueChange_WaitsForTheSkinsRefreshInterval()
		{
			var view = EngineView();
			var value = (ValueView)view.Elements[3];
			_readout.Set(Channel.EnginePortOilPressure, SkinUnits.ToSi(DisplayUnit.Psi, 40.0));
			view.Refresh(_readout, 10.0);
			_readout.Set(Channel.EnginePortOilPressure, SkinUnits.ToSi(DisplayUnit.Psi, 41.0));

			view.Refresh(_readout, 10.05);
			Assert.That(value.ValueText, Is.EqualTo("40.0"), "10 Hz: not yet");
			view.Refresh(_readout, 10.1);
			Assert.That(value.ValueText, Is.EqualTo("41.0"));
			_readout.Set(Channel.EnginePortOilPressure, SkinUnits.ToSi(DisplayUnit.Psi, 42.0));
			view.Refresh(_readout, 3.0);
			Assert.That(value.ValueText, Is.EqualTo("42.0"), "time going backwards (a new run) refreshes at once");
		}

		[Test]
		public void WithoutAReadout_EveryReadingShowsDashes()
		{
			var view = EngineView();
			_readout.Set(Channel.EnginePortOilPressure, 300000.0);
			Refresh(view);
			view.Refresh(null, _time + 1.0);

			Assert.That(((ValueView)view.Elements[3]).ValueText, Is.EqualTo("---"));
			Assert.That(((ValueView)view.Elements[4]).ValueText, Is.EqualTo("---"));
			Assert.That(((DialView)view.Elements[1]).NeedleVisible, Is.False);
			Assert.That(((LampView)view.Elements[5]).Lit, Is.False);
		}

		[Test]
		public void LampStates_FollowEachAlarmPhase()
		{
			Assert.That(AlarmLamp.State(AlarmPhase.Normal, false), Is.EqualTo(LampState.Off));
			Assert.That(AlarmLamp.State(AlarmPhase.Unacknowledged, false), Is.EqualTo(LampState.Flashing));
			Assert.That(AlarmLamp.State(AlarmPhase.Acknowledged, false), Is.EqualTo(LampState.Steady));
			Assert.That(AlarmLamp.State(AlarmPhase.RingBack, false), Is.EqualTo(LampState.SlowFlash));
			foreach (var phase in new[] { AlarmPhase.Normal, AlarmPhase.Unacknowledged, AlarmPhase.Acknowledged, AlarmPhase.RingBack })
			{
				Assert.That(AlarmLamp.State(phase, true), Is.EqualTo(LampState.Test), phase + " under lamp test");
			}
		}

		// Unacknowledged flashes at 1 Hz and ring-back at 0.5 Hz, both at 50 percent duty, lit at the start of each
		// period: the rhythm of AlarmAnnunciator.LampOn in core.
		[TestCase(AlarmPhase.Normal, 0.1, false)]
		[TestCase(AlarmPhase.Normal, 0.6, false)]
		[TestCase(AlarmPhase.Unacknowledged, 0.1, true)]
		[TestCase(AlarmPhase.Unacknowledged, 0.6, false)]
		[TestCase(AlarmPhase.Unacknowledged, 1.1, true)]
		[TestCase(AlarmPhase.Unacknowledged, 1.9, false)]
		[TestCase(AlarmPhase.Acknowledged, 0.1, true)]
		[TestCase(AlarmPhase.Acknowledged, 0.6, true)]
		[TestCase(AlarmPhase.RingBack, 0.1, true)]
		[TestCase(AlarmPhase.RingBack, 0.9, true)]
		[TestCase(AlarmPhase.RingBack, 1.5, false)]
		[TestCase(AlarmPhase.RingBack, 2.2, true)]
		public void ALamp_LightsInTheRhythmOfItsAlarmsPhase(AlarmPhase phase, double time, bool lit)
		{
			var view = EngineView();
			view.ShowPage("alarms");
			var lamp = (LampView)view.Elements[5];
			_readout.SetPhase("steering.failure", phase);
			view.Refresh(_readout, time);

			Assert.That(lamp.Lit, Is.EqualTo(lit));
			Assert.That(lamp.State, Is.EqualTo(AlarmLamp.State(phase, false)));
		}

		[Test]
		public void LampTest_LightsALampWhoseAlarmIsNormal()
		{
			var view = EngineView();
			view.ShowPage("alarms");
			var lamp = (LampView)view.Elements[5];
			_readout.LampTest = true;
			foreach (var time in new[] { 0.1, 0.6, 1.5 })
			{
				view.Refresh(_readout, time);
				Assert.That(lamp.Lit, Is.True, "at " + time);
				Assert.That(lamp.State, Is.EqualTo(LampState.Test));
			}

			_readout.LampTest = false;
			view.Refresh(_readout, 2.0);
			Assert.That(lamp.Lit, Is.False);
		}

		[Test]
		public void ALampForAnAlarmTheReadoutDoesNotDefine_StaysOff()
		{
			var view = EngineView();
			view.ShowPage("alarms");
			var readout = new FakeInstrumentReadout(("hull.contact", AlarmGroup.Hull));
			view.Refresh(readout, 0.1);
			Assert.That(((LampView)view.Elements[5]).Lit, Is.False);
			Assert.That(((LampView)view.Elements[5]).State, Is.EqualTo(LampState.Off));
		}

		[Test]
		public void TheView_IsTheSkinsReferenceSize_WithElementsAtTheirRects()
		{
			var view = EngineView();
			Assert.That(view.style.width.value.value, Is.EqualTo(800f));
			Assert.That(view.style.height.value.value, Is.EqualTo(480f));
			var dial = view.Elements[1];
			Assert.That(dial.style.left.value.value, Is.EqualTo(16f));
			Assert.That(dial.style.top.value.value, Is.EqualTo(52f));
			Assert.That(dial.style.width.value.value, Is.EqualTo(300f));
			Assert.That(dial.style.height.value.value, Is.EqualTo(300f));
		}

		[Test]
		public void APerEngineSkin_NeedsASide()
		{
			var skin = DisplaySkin.Parse(EveryElementType(), TClassAlarms);
			Assert.Throws<System.ArgumentException>(() => new SkinnedDisplayView(skin, DisplaySide.None));
		}

		[Test]
		public void Palette_RecoloursEveryElement()
		{
			var view = EngineView();
			view.Palette = PaletteKind.Night;
			Assert.That(view.style.backgroundColor.value, Is.EqualTo(InstrumentPalette.Night.Console));
			var text = (TextView)view.Elements[6];
			Assert.That(text.Label.style.color.value, Is.EqualTo(SkinColour.NightFromDay(text.Spec.Colour.Value.Day)), "hex text takes its derived night colour");
			view.Palette = PaletteKind.Day;
			Assert.That(view.style.backgroundColor.value, Is.EqualTo(InstrumentPalette.Day.Console));
		}
	}
}
