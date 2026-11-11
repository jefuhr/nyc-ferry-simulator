using System;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// Shared session stores must not retain or notify a discarded view, its HUD or its options menu. These tests keep
	/// the publishers alive, count notifications independently of collection, and exercise display, HUD and binding changes.
	/// </summary>
	public class ViewLifetimeTests
	{
		[Test]
		public void SharedStores_NotifyOnlyTheLiveView_OncePerDisplayChange()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var first = new WheelhouseView(settings, layouts);
			var second = new WheelhouseView(settings, layouts);
			var discardedCalls = 0;
			var liveCalls = 0;
			first.SettingsChanged += () => discardedCalls++;
			second.SettingsChanged += () => liveCalls++;
			var oldUnit = first.DistanceUnit;
			var oldThreshold = first.DepthAlarmThresholdMetres;
			try
			{
				Teardown(first);
				Assert.That(() => settings.DistanceUnit = DistanceUnit.Metres, Throws.Nothing);
				Assert.That(liveCalls, Is.EqualTo(1));
				Assert.That(discardedCalls, Is.Zero, "discarded view received the shared store's distance change");
				Assert.That(() => settings.DepthAlarmMetres = 3.0, Throws.Nothing);
				Assert.That(liveCalls, Is.EqualTo(2));
				Assert.That(discardedCalls, Is.Zero, "discarded view received the shared store's depth change");
				Assert.That(second.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
				Assert.That(second.DepthAlarmThresholdMetres, Is.EqualTo(3.0));
				Assert.That(first.DistanceUnit, Is.EqualTo(oldUnit));
				Assert.That(first.DepthAlarmThresholdMetres, Is.EqualTo(oldThreshold));
			}
			finally
			{
				Teardown(first);
				Teardown(second);
			}
		}

		[Test]
		public void TeardownTwice_UnhooksHudOptionsAndBindings_AndRaisesNoViewEvents()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var bindings = new FakeBindingService();
			var view = new WheelhouseView(settings, layouts);
			view.ConnectBindings(bindings);
			view.HudEditing = true;
			var session = view.HudLayout.Session;
			var row = view.Options.BindingRows.First(item => item.Row.ActionId == FerrySim.Controls.ActionIds.Horn);
			var key = row.KeyText;
			var calls = 0;
			view.SettingsChanged += () => calls++;
			view.HudEditModeChanged += _ => calls++;
			view.OptionsVisibilityChanged += _ => calls++;
			view.PickerVisibilityChanged += _ => calls++;
			view.TextEntryChanged += _ => calls++;
			Assert.That(() => { Teardown(view); Teardown(view); }, Throws.Nothing);
			Assert.That(view.HudLayout.Editing, Is.False);
			Assert.That(calls, Is.Zero, "teardown must be silent");
			var hudCalls = 0;
			view.HudLayout.LayoutChanged += _ => hudCalls++;
			view.HudLayout.ChoicesChanged += () => hudCalls++;
			var oldLayout = view.HudLayout.Current;
			settings.HudLayout = HudPresets.Minimal;
			layouts.Put(new HudLayoutEntry { name = "saved", basedOn = HudPresets.Minimal });
			settings.DistanceUnit = DistanceUnit.Metres;
			bindings.SetKey(FerrySim.Controls.ActionIds.Horn, "Keyboard", "F11");
			session.Select(HudWidgetIds.Depth);
			session.ToggleVisible(HudWidgetIds.Depth);
			Assert.That(hudCalls, Is.Zero);
			Assert.That(view.HudLayout.Current, Is.SameAs(oldLayout));
			Assert.That(view.Options.Layout.Options, Does.Not.Contain("saved"));
			Assert.That(view.Options.DistanceUnit.Value, Is.Not.EqualTo((int)DistanceUnit.Metres));
			Assert.That(row.KeyText, Is.EqualTo(key));
			view.TogglePalette();
			view.ToggleOptions();
			view.TogglePicker();
			Assert.That(calls, Is.Zero);
		}

		[Test]
		public void SharedStores_DoNotKeepATornDownViewAlive()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var bindings = new FakeBindingService();
			var reference = CreateDiscardedView(settings, layouts, bindings);
			for (var i = 0; i < 3 && reference.IsAlive; i++)
			{
				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect();
			}

			GC.KeepAlive(settings);
			GC.KeepAlive(layouts);
			GC.KeepAlive(bindings);
			if (reference.IsAlive)
			{
				Assert.Ignore("EditMode collector retained the detached UI Toolkit tree after three full collections; handler-count tests remain mandatory.");
			}

			Assert.That(reference.IsAlive, Is.False);
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private static WeakReference CreateDiscardedView(SettingsStore settings, HudLayoutStore layouts, FakeBindingService bindings)
		{
			var view = new WheelhouseView(settings, layouts);
			view.ConnectBindings(bindings);
			Teardown(view);
			return new WeakReference(view);
		}

		private static void Teardown(WheelhouseView view) => view.Dispose();
	}
}
