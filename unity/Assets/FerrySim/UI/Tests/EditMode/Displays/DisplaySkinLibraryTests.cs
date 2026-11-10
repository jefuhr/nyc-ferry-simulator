using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static FerrySim.UI.Tests.EditMode.Displays.SkinJson;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// Where skins come from: a private folder first, then the shipped resources. Failure modes guarded: a private
	/// skin that silently loses to the shipped one; a broken private skin that leaves the console blank instead of
	/// falling back; a private file whose contents name another display or id; and shipped skins that do not load
	/// against the vessel's own alarm list or lack the fields the task asks for.
	/// </summary>
	public class DisplaySkinLibraryTests
	{
		private string _folder;

		[SetUp]
		public void SetUp()
		{
			_folder = Path.Combine(Path.GetTempPath(), "ferrysim-skins-" + System.Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_folder))
			{
				Directory.Delete(_folder, true);
			}
		}

		private void WritePrivate(string display, string id, string json)
		{
			Directory.CreateDirectory(Path.Combine(_folder, display));
			File.WriteAllText(Path.Combine(_folder, display, id + ".json"), json);
		}

		private static string PrivateEngine(string id = "generic-a") =>
			Skin("{'type': 'text', 'rect': [0, 0, 100, 20], 'text': 'PRIVATE'}").Replace("\"test-a\"", "\"" + id + "\"");

		[Test]
		public void APrivateSkin_IsPreferredToTheShippedOne()
		{
			WritePrivate("engine", "generic-a", PrivateEngine());
			var library = new DisplaySkinLibrary(TClassAlarms, _folder);

			var skin = library.Load("engine", "generic-a");

			Assert.That(skin.Elements.Single().Text, Is.EqualTo("PRIVATE"));
			Assert.That(library.LastSource, Does.StartWith("private:"));
			Assert.That(library.Problems, Is.Empty);
		}

		[Test]
		public void ABrokenPrivateSkin_IsReported_AndTheShippedOneUsed()
		{
			WritePrivate("engine", "generic-a", Skin("{'type': 'gauge', 'rect': [0, 0, 100, 20]}"));
			var library = new DisplaySkinLibrary(TClassAlarms, _folder);
			var reported = new List<string>();
			library.ProblemReported += reported.Add;

			LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("gauge"));
			var skin = library.Load("engine", "generic-a");

			Assert.That(library.LastSource, Does.StartWith("shipped:"));
			Assert.That(skin.Elements.Count, Is.GreaterThan(10), "the shipped generic skin");
			Assert.That(reported, Has.Count.EqualTo(1));
			Assert.That(reported[0], Does.Contain("generic-a.json").And.Contain("gauge"));
			Assert.That(library.Problems, Is.EqualTo(reported));
		}

		[Test]
		public void APrivateFileNamingAnotherId_IsReported_AndTheShippedOneUsed()
		{
			WritePrivate("engine", "generic-a", PrivateEngine("other-b"));
			var library = new DisplaySkinLibrary(TClassAlarms, _folder);

			LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("other-b"));
			library.Load("engine", "generic-a");

			Assert.That(library.LastSource, Does.StartWith("shipped:"));
			Assert.That(library.Problems.Single(), Does.Contain("other-b"));
		}

		[Test]
		public void APrivateOnlySkin_Loads_AndAnUnknownOneDoesNot()
		{
			WritePrivate("engine", "crew-b", PrivateEngine("crew-b"));
			var library = new DisplaySkinLibrary(TClassAlarms, _folder);

			Assert.That(library.TryLoad("engine", "crew-b", out var skin), Is.True);
			Assert.That(skin.Id, Is.EqualTo("crew-b"));
			Assert.That(library.TryLoad("engine", "nobody", out var missing), Is.False);
			Assert.That(missing, Is.Null);
			Assert.Throws<DisplaySkinException>(() => library.Load("engine", "nobody"));
			Assert.That(library.TryLoad("../engine", "generic-a", out _), Is.False, "names are not paths");
		}

		[Test]
		public void NoPrivateFolder_UsesTheShippedSkins()
		{
			var library = new DisplaySkinLibrary(TClassAlarms, privateFolder: null);
			Assert.That(library.Load("alarm-panel", "generic-a").Display, Is.EqualTo("alarm-panel"));
			Assert.That(library.LastSource, Is.EqualTo("shipped:FerrySimUI/Skins/alarm-panel/generic-a"));
			Assert.That(DisplaySkinLibrary.DefaultPrivateFolder, Does.EndWith("skins"));
		}

		[Test]
		public void TheShippedSkins_LoadAgainstTheTClassAlarms_FromTheVesselFile()
		{
			var parameters = InstrumentParametersJson.Load(UIRepoPaths.Resolve("vessels/instruments/t-class.json"));
			var alarms = DisplaySkinAlarms.From(parameters);
			Assert.That(alarms, Is.EquivalentTo(TClassAlarms));
			var library = new DisplaySkinLibrary(alarms, privateFolder: null);

			Assert.DoesNotThrow(() => library.Load("engine", "generic-a"));
			Assert.DoesNotThrow(() => library.Load("alarm-panel", "generic-a"));
			Assert.That(DisplaySkinAlarms.From(FakeInstrumentReadout.TClass()), Is.EquivalentTo(TClassAlarms));
		}

		[Test]
		public void TheGenericEngineSkin_ShowsTheFieldsAnEngineScreenNeeds()
		{
			var skin = new DisplaySkinLibrary(TClassAlarms, privateFolder: null).Load("engine", "generic-a");
			Assert.That(skin.Pages, Is.EqualTo(new[] { "main", "alarms" }));
			var main = skin.Elements.Where(e => e.Page == "main" && e.ChannelName != null).Select(e => e.ChannelName).ToArray();
			foreach (var channel in new[]
			{
				"engine.{side}.speed", "engine.{side}.oil-pressure", "engine.{side}.coolant-temperature", "engine.{side}.exhaust-temperature",
				"engine.{side}.load", "engine.{side}.fuel-rate", "engine.{side}.gear-oil-pressure", "engine.{side}.shaft-speed",
				"engine.{side}.shift-phase", "electrical.bus-voltage",
			})
			{
				Assert.That(main, Has.Member(channel));
			}

			Assert.That(skin.Elements.Single(e => e.ChannelName == "engine.{side}.shift-phase").Format, Is.EqualTo(SkinValueFormat.Gear));
			var list = skin.Elements.Single(e => e.Type == SkinElementType.List);
			Assert.That(list.Page, Is.EqualTo("alarms"));
			Assert.That(list.Group, Is.EqualTo(AlarmGroup.Engine), "engine alarms only; the rest stay on the alarm panel");
			Assert.That(skin.Elements.Where(e => e.AlarmId != null), Is.Empty, "no alarm ids are invented: the page fills from the readout");
			Assert.That(skin.Elements.Where(e => e.Target != null).Select(e => e.Target), Is.EquivalentTo(new[] { "main", "alarms" }));
		}

		[Test]
		public void TheGenericAlarmPanel_ListsEveryGroup_AndHasTheThreeButtons()
		{
			var skin = new DisplaySkinLibrary(TClassAlarms, privateFolder: null).Load("alarm-panel", "generic-a");
			var list = skin.Elements.Single(e => e.Type == SkinElementType.List);
			Assert.That(list.Group, Is.Null);
			Assert.That(skin.Elements.Where(e => e.Type == SkinElementType.Softkey).Select(e => e.Action),
				Is.EquivalentTo(new[] { SoftkeyAction.Acknowledge, SoftkeyAction.Silence, SoftkeyAction.LampTest }));
			Assert.That(skin.UsesSide, Is.False);
		}
	}
}
