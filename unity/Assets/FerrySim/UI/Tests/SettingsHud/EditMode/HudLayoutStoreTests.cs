using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// hud-layouts-v1.json (contracts C5). Failure modes guarded: a saved layout lost or changed on the way to disk
	/// and back; a half-written file; a bad or future-version file applied in part; a failed save leaving the session
	/// believing it saved; a saved layout shadowing a preset's name; duplicates.
	/// </summary>
	public class HudLayoutStoreTests
	{
		private TempFolder _folder;
		private string _path;

		[SetUp]
		public void SetUp()
		{
			_folder = new TempFolder();
			_path = _folder.File(HudLayoutStore.FileName);
		}

		[TearDown]
		public void TearDown() => _folder.Dispose();

		private static HudLayoutEntry Mine(string name = "mine") => new HudLayoutEntry
		{
			name = name,
			basedOn = HudPresets.Classic,
			widgets =
			{
				new HudWidgetEntry { id = HudWidgetIds.Compass, visible = true, anchor = "bottom-center", x = 0f, y = -12f, scale = 1.25f },
				new HudWidgetEntry { id = HudWidgetIds.ClassicPanel, visible = false, anchor = "bottom-center", x = 0f, y = 0f, scale = 1f },
			},
		};

		[Test]
		public void AMissingFile_LoadsEmpty()
		{
			var store = new HudLayoutStore(_path);
			Assert.That(store.Load(), Is.True);
			Assert.That(store.Layouts, Is.Empty);
			Assert.That(File.Exists(_path), Is.False);
		}

		[Test]
		public void ASavedLayout_RoundTrips_InTheContractShape()
		{
			var store = new HudLayoutStore(_path);
			store.Load();
			Assert.That(store.Put(Mine()), Is.True, store.LastError);
			var text = File.ReadAllText(_path);
			foreach (var key in new[] { "\"version\": 1", "\"layouts\"", "\"name\": \"mine\"", "\"basedOn\": \"classic\"", "\"widgets\"", "\"id\": \"compass\"", "\"anchor\": \"bottom-center\"", "\"y\": -12" })
			{
				Assert.That(text, Does.Contain(key));
			}

			var reloaded = new HudLayoutStore(_path);
			Assert.That(reloaded.Load(), Is.True, reloaded.LastError);
			var layout = reloaded.Find("mine");
			Assert.That(layout, Is.Not.Null);
			Assert.That(layout.basedOn, Is.EqualTo(HudPresets.Classic));
			Assert.That(layout.widgets.Count, Is.EqualTo(2));
			Assert.That(layout.widgets[0].scale, Is.EqualTo(1.25f));
			Assert.That(layout.widgets[1].visible, Is.False);
		}

		[Test]
		public void Put_ReplacesALayoutOfTheSameName_AndDeleteRemovesIt()
		{
			var store = new HudLayoutStore(_path);
			store.Load();
			store.Put(Mine());
			var changed = Mine();
			changed.basedOn = HudPresets.Minimal;
			Assert.That(store.Put(changed), Is.True);
			Assert.That(store.Layouts.Count, Is.EqualTo(1));
			Assert.That(store.Find("mine").basedOn, Is.EqualTo(HudPresets.Minimal));
			store.Put(Mine("other"));
			Assert.That(store.Delete("mine"), Is.True);
			Assert.That(store.Delete("mine"), Is.False, "already gone");

			var reloaded = new HudLayoutStore(_path);
			reloaded.Load();
			Assert.That(reloaded.Find("mine"), Is.Null);
			Assert.That(reloaded.Find("other"), Is.Not.Null);
		}

		[Test]
		public void Find_ReturnsACopy()
		{
			var store = new HudLayoutStore(_path);
			store.Load();
			store.Put(Mine());
			store.Find("mine").widgets[0].x = 500f;
			Assert.That(store.Find("mine").widgets[0].x, Is.EqualTo(0f));
		}

		[TestCase("")]
		[TestCase("classic")]
		[TestCase("Console")]
		public void ALayoutNamedLikeAPresetOrUnnamed_IsRefused(string name)
		{
			var store = new HudLayoutStore(_path);
			store.Load();
			Assert.That(store.Put(Mine(name)), Is.False);
			Assert.That(store.LastError, Is.Not.Empty);
			Assert.That(File.Exists(_path), Is.False);
		}

		[Test]
		public void AFailedSave_ChangesNothing()
		{
			var store = new HudLayoutStore(_path);
			store.Load();
			store.Put(Mine());
			var before = File.ReadAllText(_path);
			Directory.CreateDirectory(_path + ".tmp");
			Assert.That(store.Put(Mine("other")), Is.False);
			Assert.That(store.Find("other"), Is.Null, "a layout that was not saved is not kept either");
			Assert.That(store.Delete("mine"), Is.False);
			Assert.That(store.Find("mine"), Is.Not.Null);
			Assert.That(File.ReadAllText(_path), Is.EqualTo(before));
			Directory.Delete(_path + ".tmp");
		}

		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"nowhere\", \"widgets\": []}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": [{\"id\": \"compass\", \"anchor\": \"upstairs\", \"x\": 0, \"y\": 0, \"scale\": 1}]}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": [{\"id\": \"compass\", \"anchor\": \"top-left\", \"x\": 0, \"y\": 0, \"scale\": 0}]}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": [{\"id\": \"\", \"anchor\": \"top-left\", \"x\": 0, \"y\": 0, \"scale\": 1}]}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": [{\"id\": \"compass\", \"anchor\": \"top-left\", \"x\": 0, \"y\": 0, \"scale\": 1}, {\"id\": \"compass\", \"anchor\": \"top-left\", \"x\": 0, \"y\": 0, \"scale\": 1}]}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": []}, {\"name\": \"A\", \"basedOn\": \"classic\", \"widgets\": []}]}")]
		[TestCase("{\"version\": 1, \"layouts\": [{\"name\": \"minimal\", \"basedOn\": \"classic\", \"widgets\": []}]}")]
		[TestCase("{\"version\": 2, \"layouts\": []}")]
		[TestCase("{\"layouts\": []}")]
		[TestCase("[1, 2, 3")]
		public void ABadFile_IsIgnoredWhole(string bad)
		{
			// Every case also carries a good layout first; it must not load either.
			var text = bad.Replace("\"layouts\": [{", "\"layouts\": [{\"name\": \"good\", \"basedOn\": \"classic\", \"widgets\": []}, {");
			File.WriteAllText(_path, text);
			var store = new HudLayoutStore(_path);
			Assert.That(store.Load(), Is.False);
			Assert.That(store.LastError, Does.StartWith("HUD layouts ignored"));
			Assert.That(store.Layouts, Is.Empty);
			Assert.That(File.ReadAllText(_path), Is.EqualTo(text), "loading never rewrites a rejected file");
		}

		[Test]
		public void AnUnknownWidgetId_IsKeptByTheStore_ForTheRegistryToDrop()
		{
			File.WriteAllText(_path, "{\"version\": 1, \"layouts\": [{\"name\": \"a\", \"basedOn\": \"classic\", \"widgets\": [{\"id\": \"sonar\", \"visible\": true, \"anchor\": \"top-left\", \"x\": 0, \"y\": 0, \"scale\": 1}]}]}");
			var store = new HudLayoutStore(_path);
			Assert.That(store.Load(), Is.True, store.LastError);
			Assert.That(store.Find("a").widgets[0].id, Is.EqualTo("sonar"));
		}

		[Test]
		public void Changed_IsRaisedAfterLoadPutAndDelete()
		{
			var store = new HudLayoutStore(_path);
			var count = 0;
			store.Changed += () => count++;
			store.Load();
			store.Put(Mine());
			store.Delete("mine");
			Assert.That(count, Is.EqualTo(3));
		}

		[Test]
		public void TheAnchorNames_AreTheNinePositions()
		{
			var names = new[] { "top-left", "top-center", "top-right", "middle-left", "middle-center", "middle-right", "bottom-left", "bottom-center", "bottom-right" };
			for (var i = 0; i < names.Length; i++)
			{
				Assert.That(HudAnchors.TryParse(names[i], out var anchor), Is.True, names[i]);
				Assert.That(HudAnchors.Id(anchor), Is.EqualTo(names[i]));
				Assert.That(HudAnchors.Fraction(anchor), Is.EqualTo(new Vector2(i % 3 * 0.5f, i / 3 * 0.5f)));
			}

			Assert.That(HudAnchors.TryParse("centre", out _), Is.False);
		}
	}
}
