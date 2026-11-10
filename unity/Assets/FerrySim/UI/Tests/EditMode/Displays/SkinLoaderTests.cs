using System;
using FerrySim.Core.Instruments;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using static FerrySim.UI.Tests.EditMode.Displays.SkinJson;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// Loading a display skin (contract C6). Failure modes guarded: a typo in a channel, alarm, element type, unit or
	/// page that loads anyway and shows a blank or wrong instrument; a half-loaded skin (some elements kept, some
	/// dropped); an error that does not say which element is wrong; {side} resolved to the wrong engine; and a hex
	/// colour that lights the wheelhouse white or green at night.
	/// </summary>
	public class SkinLoaderTests
	{
		[Test]
		public void AValidSkin_Loads_WithEveryElementInOrder()
		{
			var skin = DisplaySkin.Parse(EveryElementType(), TClassAlarms);

			Assert.That(skin.Display, Is.EqualTo("engine"));
			Assert.That(skin.Id, Is.EqualTo("test-a"));
			Assert.That(skin.Reference, Is.EqualTo(new Vector2(800, 480)));
			Assert.That(skin.RefreshHz, Is.EqualTo(10.0));
			Assert.That(skin.Pages, Is.EqualTo(new[] { "main", "alarms" }));
			Assert.That(skin.Elements, Has.Count.EqualTo(11));
			Assert.That(skin.Elements[1].Type, Is.EqualTo(SkinElementType.Dial));
			Assert.That(skin.Elements[1].Rect, Is.EqualTo(new Rect(16, 52, 300, 300)));
			Assert.That(skin.Elements[1].Low, Is.EqualTo(0.0));
			Assert.That(skin.Elements[1].High, Is.EqualTo(2400.0));
			Assert.That(skin.Elements[1].SweepStart, Is.EqualTo(-135f));
			Assert.That(skin.Elements[1].SweepEnd, Is.EqualTo(135f));
			Assert.That(skin.Elements[1].Unit, Is.EqualTo(DisplayUnit.Rpm));
			Assert.That(skin.Elements[1].Bands, Has.Count.EqualTo(1));
			Assert.That(skin.Elements[1].Bands[0].Low, Is.EqualTo(2100.0));
			Assert.That(skin.Elements[1].Bands[0].Colour.Role, Is.EqualTo(PaletteRole.Redline));
			Assert.That(skin.Elements[3].Decimals, Is.EqualTo(1));
			Assert.That(skin.Elements[4].Format, Is.EqualTo(SkinValueFormat.Gear));
			Assert.That(skin.Elements[5].AlarmId, Is.EqualTo("steering.failure"));
			Assert.That(skin.Elements[7].Type, Is.EqualTo(SkinElementType.List));
			Assert.That(skin.Elements[7].Group, Is.EqualTo(AlarmGroup.Engine));
			Assert.That(skin.Elements[8].Target, Is.EqualTo("main"));
			Assert.That(skin.Elements[10].Action, Is.EqualTo(SoftkeyAction.Acknowledge));
			Assert.That(skin.UsesSide, Is.True);
		}

		[Test]
		public void PagesAbsent_MeansOneUnnamedPage()
		{
			var skin = DisplaySkin.Parse(Skin("{'type': 'text', 'rect': [0, 0, 100, 20], 'text': 'HELLO'}"), TClassAlarms);
			Assert.That(skin.Pages, Is.Empty);
			Assert.That(skin.Elements[0].Page, Is.Null);
			Assert.That(skin.UsesSide, Is.False);
		}

		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.oil-presure', 'unit': 'psi'}", "engine.{side}.oil-presure", TestName = "UnknownChannel_IsRejectedWhole")]
		[TestCase("{'type': 'lamp', 'rect': [0, 0, 100, 40], 'alarm': 'engine.{side}.overspeed'}", "engine.{side}.overspeed", TestName = "UnknownAlarm_IsRejectedWhole")]
		[TestCase("{'type': 'gauge', 'rect': [0, 0, 100, 40]}", "gauge", TestName = "UnknownElementType_IsRejectedWhole")]
		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'nav.sog', 'unit': 'mph'}", "mph", TestName = "UnknownUnit_IsRejectedWhole")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 100, 40], 'page': 'trends', 'text': 'X'}", "trends", TestName = "UnknownPage_IsRejectedWhole")]
		[TestCase("{'type': 'softkey', 'rect': [0, 0, 100, 40], 'text': 'X', 'target': 'trends'}", "trends", TestName = "UnknownSoftkeyTarget_IsRejectedWhole")]
		public void ABadSecondElement_RejectsTheWholeSkin_NamingTheElement(string bad, string named)
		{
			var json = Skin("{'type': 'text', 'rect': [0, 0, 100, 20], 'text': 'FINE'}," + Q(bad), "['main', 'alarms']");

			var ok = DisplaySkin.TryParse(json, TClassAlarms, out var skin, out var error);

			Assert.That(ok, Is.False);
			Assert.That(skin, Is.Null, "nothing of a rejected skin is kept");
			Assert.That(error, Does.Contain("element 2"), error);
			Assert.That(error, Does.Contain(named), error);
			var thrown = Assert.Throws<DisplaySkinException>(() => DisplaySkin.Parse(json, TClassAlarms));
			Assert.That(thrown.ElementNumber, Is.EqualTo(2));
		}

		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.speed', 'unit': 'psi'}", "psi", TestName = "AUnitThatDoesNotFitTheChannel_IsRejected")]
		[TestCase("{'type': 'dial', 'rect': [0, 0, 100, 100], 'channel': 'engine.{side}.speed', 'unit': 'rpm'}", "range", TestName = "ADialWithoutARange_IsRejected")]
		[TestCase("{'type': 'dial', 'rect': [0, 0, 100, 100], 'channel': 'engine.{side}.speed', 'unit': 'rpm', 'range': [2400, 0]}", "range", TestName = "AnInvertedRange_IsRejected")]
		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40]}", "channel", TestName = "AValueWithoutAChannel_IsRejected")]
		[TestCase("{'type': 'lamp', 'rect': [0, 0, 100, 40]}", "alarm", TestName = "ALampWithoutAnAlarm_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 100, 40], 'text': 'X', 'range': [0, 1]}", "range", TestName = "APropertyThatDoesNotApplyToTheType_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 100, 40], 'text': 'X', 'colur': 'text'}", "colur", TestName = "AnUnknownProperty_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [700, 0, 200, 40], 'text': 'X'}", "rect", TestName = "ARectOutsideTheReference_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 0, 40], 'text': 'X'}", "rect", TestName = "AnEmptyRect_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 100, 40], 'text': 'X', 'colour': 'chartreuse'}", "chartreuse", TestName = "AnUnknownPaletteRole_IsRejected")]
		[TestCase("{'type': 'text', 'rect': [0, 0, 100, 40], 'text': 'X', 'colour': '#FFF'}", "#FFF", TestName = "AShortHexColour_IsRejected")]
		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.oil-pressure', 'unit': 'psi', 'decimals': 1.5}", "decimals", TestName = "FractionalDecimals_AreRejected")]
		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.oil-pressure', 'format': 'gear'}", "gear", TestName = "GearFormat_OnlyOnAShiftPhaseChannel")]
		[TestCase("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.shift-phase', 'format': 'words'}", "words", TestName = "AnUnknownFormat_IsRejected")]
		[TestCase("{'type': 'list', 'rect': [0, 0, 100, 40], 'group': 'Galley'}", "Galley", TestName = "AnUnknownAlarmGroup_IsRejected")]
		[TestCase("{'type': 'softkey', 'rect': [0, 0, 100, 40], 'text': 'X', 'action': 'reboot'}", "reboot", TestName = "AnUnknownSoftkeyAction_IsRejected")]
		[TestCase("{'type': 'softkey', 'rect': [0, 0, 100, 40], 'text': 'X', 'action': 'silence', 'target': 'main'}", "both", TestName = "ASoftkeyWithTargetAndAction_IsRejected")]
		[TestCase("{'type': 'bar', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.load', 'unit': 'pct', 'range': [0, 100], 'bands': [{'range': [90, 110], 'colour': 'alarm'}]}", "band", TestName = "ABandOutsideTheRange_IsRejected")]
		[TestCase("{'type': 'bar', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.load', 'unit': 'pct', 'range': [0, 100], 'bands': [{'range': [90, 90], 'colour': 'alarm'}]}", "band", TestName = "AnEmptyBand_IsRejected")]
		[TestCase("{'type': 'bar', 'rect': [0, 0, 100, 40], 'channel': 'engine.{side}.load', 'unit': 'pct', 'range': [0, 100], 'bands': [{'range': [80, 90]}]}", "colour", TestName = "ABandWithoutAColour_IsRejected")]
		public void AMalformedElement_IsRejectedWhole(string bad, string named)
		{
			var json = Skin(Q(bad), "['main']");

			Assert.That(DisplaySkin.TryParse(json, TClassAlarms, out var skin, out var error), Is.False, "should not load");
			Assert.That(skin, Is.Null);
			Assert.That(error, Does.Contain("element 1"), error);
			Assert.That(error, Does.Contain(named), error);
		}

		[TestCase("{'version': 2, 'display': 'engine', 'id': 'a', 'reference': [10, 10], 'refreshHz': 5, 'elements': []}", "version")]
		[TestCase("{'version': 1, 'display': 'toaster', 'id': 'a', 'reference': [10, 10], 'refreshHz': 5, 'elements': []}", "toaster")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': '', 'reference': [10, 10], 'refreshHz': 5, 'elements': []}", "id")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'reference': [10], 'refreshHz': 5, 'elements': []}", "reference")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'reference': [10, 10], 'refreshHz': 0, 'elements': []}", "refreshHz")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'reference': [10, 10], 'refreshHz': 5}", "elements")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'reference': [10, 10], 'refreshHz': 5, 'elements': [], 'theme': 'x'}", "theme")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'reference': [10, 10], 'refreshHz': 5, 'pages': ['a', 'a'], 'elements': []}", "pages")]
		[TestCase("{'version': 1, 'display': 'engine', 'id': 'a', 'id': 'b', 'reference': [10, 10], 'refreshHz': 5, 'elements': []}", "id")]
		[TestCase("not json", "JSON")]
		public void AMalformedSkin_IsRejected(string json, string named)
		{
			Assert.That(DisplaySkin.TryParse(Q(json), TClassAlarms, out var skin, out var error), Is.False);
			Assert.That(skin, Is.Null);
			Assert.That(error, Does.Contain(named), error);
		}

		[Test]
		public void Side_IsSubstitutedWhenTheViewIsCreated()
		{
			var skin = DisplaySkin.Parse(EveryElementType(), TClassAlarms);
			var dial = skin.Elements[1];

			Assert.That(dial.ChannelName, Is.EqualTo("engine.{side}.speed"), "the skin itself keeps the token");
			Assert.That(dial.ResolveChannel(DisplaySide.Port), Is.EqualTo(Channel.EnginePortSpeed));
			Assert.That(dial.ResolveChannel(DisplaySide.Starboard), Is.EqualTo(Channel.EngineStarboardSpeed));
			Assert.That(skin.Elements[0].ResolveText(DisplaySide.Port), Is.EqualTo("PORT ENGINE"));
			Assert.That(skin.Elements[0].ResolveText(DisplaySide.Starboard), Is.EqualTo("STARBOARD ENGINE"));
			Assert.Throws<InvalidOperationException>(() => dial.ResolveChannel(DisplaySide.None), "a {side} skin needs a side");
		}

		[Test]
		public void SideInAnAlarm_MustNameAnAlarmForBothSides()
		{
			var lamp = "{'type': 'lamp', 'rect': [0, 0, 100, 40], 'alarm': 'engine.{side}.overspeed'}";
			var both = new System.Collections.Generic.HashSet<string> { "engine.port.overspeed", "engine.starboard.overspeed" };
			var portOnly = new System.Collections.Generic.HashSet<string> { "engine.port.overspeed" };

			var skin = DisplaySkin.Parse(Skin(lamp), both);
			Assert.That(skin.Elements[0].ResolveAlarm(DisplaySide.Starboard), Is.EqualTo("engine.starboard.overspeed"));
			Assert.That(DisplaySkin.TryParse(Skin(lamp), portOnly, out _, out var error), Is.False);
			Assert.That(error, Does.Contain("engine.starboard.overspeed"), error);
		}

		[Test]
		public void AChannelWithoutSide_ResolvesTheSameForEitherSide()
		{
			var skin = DisplaySkin.Parse(Skin("{'type': 'value', 'rect': [0, 0, 100, 40], 'channel': 'electrical.bus-voltage', 'unit': 'V', 'decimals': 1}"), TClassAlarms);
			Assert.That(skin.Elements[0].ResolveChannel(DisplaySide.None), Is.EqualTo(Channel.BusVoltage));
			Assert.That(skin.Elements[0].ResolveChannel(DisplaySide.Port), Is.EqualTo(Channel.BusVoltage));
		}

		[Test]
		public void APaletteRole_ResolvesFromEachPalette()
		{
			Assert.That(SkinColour.TryParse("text-dim", out var colour), Is.True);
			Assert.That(colour.IsRole, Is.True);
			Assert.That(colour.Resolve(InstrumentPalette.Day), Is.EqualTo(InstrumentPalette.Day.TextDim));
			Assert.That(colour.Resolve(InstrumentPalette.Night), Is.EqualTo(InstrumentPalette.Night.TextDim));
		}

		[Test]
		public void AHexColour_IsTheDayColour_WithTheNightColourDerived()
		{
			Assert.That(SkinColour.TryParse("#3CD483", out var green), Is.True);
			Assert.That(green.IsRole, Is.False);
			Assert.That(green.Resolve(InstrumentPalette.Day), Is.EqualTo(new Color(0x3C / 255f, 0xD4 / 255f, 0x83 / 255f, 1f)).Using(ColourComparer));
			var night = green.Resolve(InstrumentPalette.Night);
			Assert.That(night, Is.EqualTo(SkinColour.NightFromDay(green.Day)));
			AssertNightSafe(night, "#3CD483 at night");
			Assert.That(night.g, Is.LessThan(night.r), "green day colour must not stay green at night");
		}

		[Test]
		public void NightDerivation_KeepsEveryColourRedToAmber_DimAndInBrightnessOrder()
		{
			// A sweep over the RGB cube, including white, pure green and pure blue: the colours most harmful at night.
			var previous = -1.0;
			for (var grey = 0; grey <= 255; grey += 15)
			{
				var night = SkinColour.NightFromDay(new Color32((byte)grey, (byte)grey, (byte)grey, 255));
				AssertNightSafe(night, "grey " + grey);
				var luminance = Luminance(night);
				Assert.That(luminance, Is.GreaterThanOrEqualTo(previous - 1e-9), "a brighter day grey must not be dimmer at night");
				previous = luminance;
			}

			for (var r = 0; r <= 255; r += 51)
			for (var g = 0; g <= 255; g += 51)
			for (var b = 0; b <= 255; b += 51)
			{
				AssertNightSafe(SkinColour.NightFromDay(new Color32((byte)r, (byte)g, (byte)b, 255)), $"({r},{g},{b})");
			}

			// White text at night is as legible as the palette's own night numerals on the night face.
			var white = SkinColour.NightFromDay(Color.white);
			Assert.That(Ratio(white, InstrumentPalette.Night.Face), Is.GreaterThanOrEqualTo(3.0));
		}

		private static readonly System.Collections.Generic.IEqualityComparer<Color> ColourComparer = new ColourEquality();

		/// <summary>The night rules of PaletteTests: red ≥ green ≥ blue, green at most 0.65 of red, luminance at most 0.25.</summary>
		private static void AssertNightSafe(Color c, string what)
		{
			Assert.That(c.r + 1e-4f, Is.GreaterThanOrEqualTo(c.g), what + ": green must not exceed red");
			Assert.That(c.g + 1e-4f, Is.GreaterThanOrEqualTo(c.b), what + ": blue must not exceed green");
			if (c.r > 0.05f)
			{
				Assert.That(c.g / c.r, Is.LessThanOrEqualTo(0.65f + 1e-4f), what + ": stays red to amber");
			}

			Assert.That(Luminance(c), Is.LessThanOrEqualTo(0.25), what + ": too bright for night vision");
			Assert.That(c.a, Is.EqualTo(1f));
		}

		private static double Ratio(Color a, Color b)
		{
			var la = Luminance(a);
			var lb = Luminance(b);
			return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
		}

		private static double Luminance(Color c) => 0.2126 * Linear(c.r) + 0.7152 * Linear(c.g) + 0.0722 * Linear(c.b);

		private static double Linear(float v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

		private sealed class ColourEquality : System.Collections.Generic.IEqualityComparer<Color>
		{
			public bool Equals(Color a, Color b) => Mathf.Abs(a.r - b.r) < 1e-3f && Mathf.Abs(a.g - b.g) < 1e-3f && Mathf.Abs(a.b - b.b) < 1e-3f && Mathf.Abs(a.a - b.a) < 1e-3f;

			public int GetHashCode(Color c) => 0;
		}
	}
}
