using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// settings-v1.json (contracts C5). Failure modes guarded: a value lost on the way to disk and back; a crash
	/// mid-write leaving a half file (writes go through a temporary file and replace the old one whole); a bad,
	/// partly bad or future-version file applied in part; a missing file or section not falling back to the
	/// defaults; one change raising events for sections it did not touch.
	/// </summary>
	public class SettingsStoreTests
	{
		private TempFolder _folder;
		private string _path;

		[SetUp]
		public void SetUp()
		{
			_folder = new TempFolder();
			_path = _folder.File(SettingsStore.FileName);
		}

		[TearDown]
		public void TearDown() => _folder.Dispose();

		[Test]
		public void Defaults_AreTheContractDefaults_AndFirstLaunchIsClassic()
		{
			var store = new SettingsStore(_path);
			Assert.That(store.Load(), Is.True, "a missing file is not an error");
			Assert.That(store.Palette, Is.EqualTo(PaletteSetting.Auto));
			Assert.That(store.DistanceUnit, Is.EqualTo(DistanceUnit.Feet));
			Assert.That(store.DepthAlarmMetres, Is.EqualTo(DepthAlarm.DefaultThresholdMetres));
			Assert.That(store.VectorMinutes, Is.EqualTo(2.0));
			Assert.That(store.Predictor, Is.EqualTo(PredictorKind.Straight));
			Assert.That(store.HudLayout, Is.EqualTo(HudPresets.Classic));
			Assert.That(HudPresets.FirstLaunch, Is.EqualTo(HudPresets.Classic));
			Assert.That(store.KeybindHints, Is.EqualTo(KeybindHintMode.Hold));
			Assert.That(store.ConsoleScreens, Is.True);
			Assert.That(store.MasterVolume, Is.EqualTo(1f));
			Assert.That(store.Muted, Is.False);
			Assert.That(store.AlarmVolume, Is.EqualTo(1f));
			Assert.That(store.LookSensitivity, Is.EqualTo(0.15f));
			Assert.That(File.Exists(_path), Is.False, "loading never writes");
		}

		[Test]
		public void EverySetting_RoundTripsThroughTheFile()
		{
			var store = new SettingsStore(_path);
			store.Load();
			store.Palette = PaletteSetting.Night;
			store.DistanceUnit = DistanceUnit.Metres;
			store.DepthAlarmMetres = 2.5;
			store.VectorMinutes = 4.5;
			store.Predictor = PredictorKind.Curved;
			store.HudLayout = "mine";
			store.KeybindHints = KeybindHintMode.Always;
			store.ConsoleScreens = false;
			store.MasterVolume = 0.4f;
			store.Muted = true;
			store.AlarmVolume = 0.65f;
			store.LookSensitivity = 0.3f;

			var reloaded = new SettingsStore(_path);
			Assert.That(reloaded.Load(), Is.True, reloaded.LastError);
			Assert.That(reloaded.Palette, Is.EqualTo(PaletteSetting.Night));
			Assert.That(reloaded.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
			Assert.That(reloaded.DepthAlarmMetres, Is.EqualTo(2.5));
			Assert.That(reloaded.VectorMinutes, Is.EqualTo(4.5));
			Assert.That(reloaded.Predictor, Is.EqualTo(PredictorKind.Curved));
			Assert.That(reloaded.HudLayout, Is.EqualTo("mine"));
			Assert.That(reloaded.KeybindHints, Is.EqualTo(KeybindHintMode.Always));
			Assert.That(reloaded.ConsoleScreens, Is.False);
			Assert.That(reloaded.MasterVolume, Is.EqualTo(0.4f));
			Assert.That(reloaded.Muted, Is.True);
			Assert.That(reloaded.AlarmVolume, Is.EqualTo(0.65f));
			Assert.That(reloaded.LookSensitivity, Is.EqualTo(0.3f));
		}

		[Test]
		public void TheFile_HasTheContractShape()
		{
			var store = new SettingsStore(_path);
			store.Load();
			store.Predictor = PredictorKind.Curved;
			var text = File.ReadAllText(_path);
			foreach (var key in new[]
			{
				"\"version\": 1", "\"display\"", "\"palette\": \"auto\"", "\"distanceUnit\": \"feet\"", "\"depthAlarmMetres\"", "\"vectorMinutes\"",
				"\"predictor\": \"curved\"", "\"hud\"", "\"layout\": \"classic\"", "\"keybindHints\": \"hold\"", "\"console\"", "\"screens\": true",
				"\"audio\"", "\"master\"", "\"muted\": false", "\"alarms\"", "\"controls\"", "\"lookSensitivity\"",
			})
			{
				Assert.That(text, Does.Contain(key));
			}
		}

		[Test]
		public void AWrite_GoesThroughATemporaryFile_AndAFailedWriteLeavesTheOldFileWhole()
		{
			var store = new SettingsStore(_path);
			store.Load();
			store.DistanceUnit = DistanceUnit.Metres;
			var before = File.ReadAllText(_path);
			Assert.That(File.Exists(_path + ".tmp"), Is.False, "the temporary file is gone after a write");

			// A directory where the temporary file should go makes the write fail before the real file is touched.
			Directory.CreateDirectory(_path + ".tmp");
			store.VectorMinutes = 6.0;
			Assert.That(store.LastError, Does.Contain("Could not save"));
			Assert.That(File.ReadAllText(_path), Is.EqualTo(before), "the old file is untouched");
			Assert.That(store.VectorMinutes, Is.EqualTo(6.0), "the session keeps the change");
			Directory.Delete(_path + ".tmp");

			var reloaded = new SettingsStore(_path);
			Assert.That(reloaded.Load(), Is.True);
			Assert.That(reloaded.VectorMinutes, Is.EqualTo(2.0));
			Assert.That(reloaded.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
		}

		[TestCase("not json at all")]
		[TestCase("{\"version\": 1, \"display\": {\"palette\": \"purple\"}}")]
		[TestCase("{\"version\": 1, \"display\": {\"distanceUnit\": \"furlongs\"}}")]
		[TestCase("{\"version\": 1, \"display\": {\"predictor\": \"wiggly\"}}")]
		[TestCase("{\"version\": 1, \"display\": {\"vectorMinutes\": -3}}")]
		[TestCase("{\"version\": 1, \"display\": {\"depthAlarmMetres\": -1}}")]
		[TestCase("{\"version\": 1, \"hud\": {\"layout\": \"\"}}")]
		[TestCase("{\"version\": 1, \"hud\": {\"keybindHints\": \"sometimes\"}}")]
		[TestCase("{\"version\": 1, \"audio\": {\"master\": 7}}")]
		[TestCase("{\"version\": 1, \"audio\": {\"alarms\": -0.5}}")]
		[TestCase("{\"version\": 1, \"controls\": {\"lookSensitivity\": 0}}")]
		[TestCase("{\"display\": {\"distanceUnit\": \"metres\"}}")]
		public void ABadFile_IsIgnoredWhole_AndTheDefaultsAreUsed(string text)
		{
			// The good value in the same file (metres) must not be applied either: all or nothing.
			File.WriteAllText(_path, text.Replace("{\"version\": 1, ", "{\"version\": 1, \"console\": {\"screens\": false}, "));
			var store = new SettingsStore(_path);
			Assert.That(store.Load(), Is.False);
			Assert.That(store.LastError, Does.StartWith("Settings ignored"));
			Assert.That(store.DistanceUnit, Is.EqualTo(DistanceUnit.Feet));
			Assert.That(store.ConsoleScreens, Is.True);
			Assert.That(store.Palette, Is.EqualTo(PaletteSetting.Auto));
		}

		[Test]
		public void AnUnknownVersion_IsIgnored_AndNotOverwrittenByLoading()
		{
			const string future = "{\"version\": 2, \"display\": {\"distanceUnit\": \"metres\"}}";
			File.WriteAllText(_path, future);
			var store = new SettingsStore(_path);
			Assert.That(store.Load(), Is.False);
			Assert.That(store.LastError, Does.Contain("version"));
			Assert.That(store.DistanceUnit, Is.EqualTo(DistanceUnit.Feet));
			Assert.That(File.ReadAllText(_path), Is.EqualTo(future));
		}

		[Test]
		public void AFileMissingASection_TakesTheDefaultsForIt()
		{
			File.WriteAllText(_path, "{\"version\": 1, \"display\": {\"distanceUnit\": \"metres\"}}");
			var store = new SettingsStore(_path);
			Assert.That(store.Load(), Is.True, store.LastError);
			Assert.That(store.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
			Assert.That(store.Palette, Is.EqualTo(PaletteSetting.Auto));
			Assert.That(store.VectorMinutes, Is.EqualTo(2.0));
			Assert.That(store.HudLayout, Is.EqualTo(HudPresets.FirstLaunch));
			Assert.That(store.MasterVolume, Is.EqualTo(1f));
		}

		[Test]
		public void AChange_RaisesTheEventOfItsSectionOnly_AndLoadRaisesEvery()
		{
			var store = new SettingsStore(_path);
			var raised = new List<SettingsSection>();
			store.Changed += raised.Add;
			var audio = 0;
			store.AudioChanged += () => audio++;
			store.Load();
			Assert.That(raised, Is.EquivalentTo(new[] { SettingsSection.Display, SettingsSection.Hud, SettingsSection.Console, SettingsSection.Audio, SettingsSection.Controls }));
			Assert.That(audio, Is.EqualTo(1));

			raised.Clear();
			store.MasterVolume = 0.5f;
			store.MasterVolume = 0.5f;
			store.DistanceUnit = DistanceUnit.Metres;
			store.HudLayout = HudPresets.Minimal;
			store.ConsoleScreens = false;
			store.LookSensitivity = 0.2f;
			Assert.That(raised, Is.EqualTo(new[] { SettingsSection.Audio, SettingsSection.Display, SettingsSection.Hud, SettingsSection.Console, SettingsSection.Controls }),
				"an unchanged value raises nothing");
			Assert.That(audio, Is.EqualTo(2));
		}

		[Test]
		public void OutOfRangeValues_AreClampedWhenSet()
		{
			var store = SettingsStore.InMemory();
			store.MasterVolume = 3f;
			store.AlarmVolume = -1f;
			store.VectorMinutes = 0.0;
			store.DepthAlarmMetres = double.NaN;
			Assert.That(store.MasterVolume, Is.EqualTo(1f));
			Assert.That(store.AlarmVolume, Is.EqualTo(0f));
			Assert.That(store.VectorMinutes, Is.EqualTo(SettingsStore.MinVectorMinutes));
			Assert.That(store.DepthAlarmMetres, Is.EqualTo(DepthAlarm.DefaultThresholdMetres), "NaN is ignored");
		}

		[Test]
		public void AnInMemoryStore_NeverWrites()
		{
			var store = SettingsStore.InMemory();
			Assert.That(store.Persistent, Is.False);
			store.DistanceUnit = DistanceUnit.Metres;
			Assert.That(store.Save(), Is.True);
			Assert.That(store.LastError, Is.Empty);
			Assert.That(Directory.GetFiles(_folder.Path), Is.Empty);
		}
	}
}
