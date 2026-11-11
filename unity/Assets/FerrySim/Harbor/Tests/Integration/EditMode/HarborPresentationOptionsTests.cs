using System;
using System.IO;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Harbor.Tests.Integration
{
	public class HarborPresentationOptionsTests
	{
		[Test] public void OrdinaryLaunchLeavesSavedPreferencesAlone()
		{
			var options = HarborPresentationOptions.Parse(Array.Empty<string>());
			Assert.That(options.ConsoleScreens, Is.Null); Assert.That(options.HudPreset, Is.Null);
		}
		[Test] public void ExistingBenchmarkKeepsTheFlatClassicPresentation()
		{
			var options = HarborPresentationOptions.Parse(new[] { "-ferrysim-route", "astoria" });
			Assert.That(options.ConsoleScreens, Is.False); Assert.That(options.HudPreset, Is.EqualTo("classic"));
		}
		[TestCase("classic"), TestCase("console"), TestCase("minimal"), TestCase("everything")]
		public void ExplicitBenchmarkPresentationWins(string preset)
		{
			var options = HarborPresentationOptions.Parse(new[] { "-ferrysim-route", "astoria", "-ferrysim-console-screens", "1", "-ferrysim-hud-preset", preset });
			Assert.That(options.ConsoleScreens, Is.True); Assert.That(options.HudPreset, Is.EqualTo(preset));
		}
		[TestCase("-ferrysim-console-screens"), TestCase("-ferrysim-hud-preset")]
		public void MissingOrInvalidValuesAreRefused(string key)
		{
			Assert.Throws<ArgumentException>(() => HarborPresentationOptions.Parse(new[] { key }));
			Assert.Throws<ArgumentException>(() => HarborPresentationOptions.Parse(new[] { key, "invalid" }));
		}
		[TestCase("-runTests"), TestCase("-ferrysim-smoke"), TestCase("-ferrysim-audio-volume")]
		public void AutomationAndExplicitVolumeHavePriority(string argument) => Assert.That(HarborPresentation.HasAudioOverride(new[] { argument }), Is.True);
		[Test] public void InteractiveSavedVolumeIsUsed() => Assert.That(HarborPresentation.HasAudioOverride(Array.Empty<string>()), Is.False);
		[TestCase(false), TestCase(true)]
		public void PresentationOverridesCannotWriteThePlayersPreferences(bool automation)
		{
			var folder = Path.Combine(FerrySim.Sim.SimPaths.RepoRoot, "artifacts/v05-integration/launch-preferences", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(folder);
			var file = Path.Combine(folder, "settings-v1.json");
			var saved = new SettingsStore(file); saved.MasterVolume = 0.42f; saved.ConsoleScreens = false;
			var original = File.ReadAllBytes(file);
			var args = automation ? new[] { "-ferrysim-route", "astoria", "-ferrysim-console-screens", "1", "-ferrysim-hud-preset", "console" } : Array.Empty<string>();
			var view = new WheelhouseView(HarborLaunchPreferences.TemporarySettings(args) ?? saved, HudLayoutStore.InMemory());
			try
			{
				HarborPresentationOptions.Parse(args).Apply(view);
				Assert.That(File.ReadAllBytes(file), Is.EqualTo(original), "launch overrides never enter the player's file");
				Assert.That(view.Settings.Persistent, Is.EqualTo(!automation));
				if (!automation) Assert.That(view.Settings.MasterVolume, Is.EqualTo(0.42f));
			}
			finally { view.Dispose(); }

		}
	}
}
