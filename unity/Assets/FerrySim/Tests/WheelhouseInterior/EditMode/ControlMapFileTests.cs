using System;
using System.IO;
using System.Reflection;
using FerrySim.Vessels;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class ControlMapFileTests
	{
		// Reflection keeps the first red run compilable while the new loader does not exist yet.
		private static object Parse(string json, WheelhouseControlMap mounts)
		{
			var type = typeof(WheelhouseControlsView).Assembly.GetType("FerrySim.World.ControlMapFile");
			Assert.That(type, Is.Not.Null, "validated control-map loader is required");
			return type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { json, mounts });
		}

		[TestCase("k-class")]
		[TestCase("t-class")]
		public void ShippedMapLoadsForBothClasses(string id)
		{
			using var f = new InstrumentFixture(id);
			var file = Resources.Load<TextAsset>("FerrySimWheelhouse/controls-v1");
			Assert.That(file, Is.Not.Null);
			Assert.That(Parse(file.text, f.Root.GetComponent<WheelhouseControlMap>()), Is.Not.Null);
		}

		[TestCase("\"channel\":\"no.channel\",\"range\":[-1,1]", "PortLever", "no.channel")]
		[TestCase("\"channel\":\"lever.port\",\"range\":[-1,1]", "NoMount", "NoMount")]
		[TestCase("\"actions\":[\"Helm/NoAction\"]", "PortLever", "Helm/NoAction")]
		[TestCase("\"channel\":\"lever.port\",\"range\":[1,1]", "PortLever", "PortLever")]
		public void BadEntryRejectsEntireFile(string fields, string mount, string diagnostic)
		{
			using var f = new InstrumentFixture();
			var json = "{\"version\":1,\"controls\":[{\"mount\":\"HelmFFU\"},{\"mount\":\"" + mount + "\"," + fields + "}]}";
			var error = Assert.Throws<TargetInvocationException>(() => Parse(json, f.Root.GetComponent<WheelhouseControlMap>()));
			Assert.That(error.InnerException, Is.TypeOf<FormatException>());
			Assert.That(error.InnerException.Message, Does.Contain(diagnostic));
		}

		[Test]
		public void PopupEntriesExposeBoundTransformsLabelsAndActions()
		{
			using var f = new InstrumentFixture();
			var entries = f.View.ControlMap.Entries;
			Assert.That(entries.Count, Is.EqualTo(73));
			foreach (var entry in entries)
			{
				Assert.That(entry.Transform, Is.SameAs(f.Root.transform.Find(entry.MountId)));
				if (entry.Actions.Count > 0) Assert.That(entry.Label, Is.Not.Empty);
			}
			var horn = System.Linq.Enumerable.Single(entries, e => e.MountId == "HornButton");
			Assert.That(horn.Actions, Is.EqualTo(new[] { "Helm/Horn" }));
		}

		[Serializable] private class LayoutData { public LampData[] controls; }
		[Serializable] private class PaletteData { public LampData[] lamps; }
		[Serializable] private class LampData { public string id, mount, kind, colour; public int lamp; }
		[TestCase("k-class")]
		[TestCase("t-class")]
		public void ShippedLampPaletteIsExactLayoutColourSnapshot(string id)
		{
			var layout = JsonUtility.FromJson<LayoutData>(File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../art/blender/data/" + id + "-wheelhouse.json"))));
			var palette = JsonUtility.FromJson<PaletteData>(Resources.Load<TextAsset>("FerrySimWheelhouse/lamp-colours-v1").text);
			var count = 0;
			foreach (var control in layout.controls)
			{
				if (control.kind != "lamp") continue;
				var colour = Array.Find(palette.lamps, c => c.mount == control.id);
				Assert.That(colour, Is.Not.Null, control.id); Assert.That(colour.lamp, Is.EqualTo(control.lamp));
				Assert.That(colour.colour, Is.EqualTo(control.colour)); count++;
			}
			Assert.That(palette.lamps.Length, Is.EqualTo(count));
		}

		[Serializable] private class FileData { public Entry[] controls; }
		[Serializable] private class Entry { public string[] actions; }
		[Test]
		public void EveryShippedActionExistsInActionsAsset()
		{
			var file = Resources.Load<TextAsset>("FerrySimWheelhouse/controls-v1"); Assert.That(file, Is.Not.Null);
			var actions = UnityEngine.InputSystem.InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			try
			{
				foreach (var entry in JsonUtility.FromJson<FileData>(file.text).controls)
					foreach (var action in entry.actions ?? Array.Empty<string>()) Assert.That(actions.FindAction(action), Is.Not.Null, action);
			}
			finally { UnityEngine.Object.DestroyImmediate(actions); }
		}
	}
}
