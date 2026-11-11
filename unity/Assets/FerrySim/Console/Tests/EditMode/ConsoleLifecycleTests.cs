using System;
using FerrySim.Core.Radar;
using FerrySim.Chartplotter.Tests;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Console.Tests
{
	// Failure modes: wrong resolution, HUD copies diverge, leaked targets, missing surface, and disabled NO SIGNAL.
	public class ConsoleLifecycleTests
	{
		[Test] public void TargetsHaveReferenceSizes_AndDisableReleasesThem()
		{
			var interior = new GameObject("interior");
			var source = interior.AddComponent<StubOwnShip>();
			foreach (var mount in ConsoleScreens.MountNames)
			{
				var surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
				surface.name = mount + "Surface"; surface.transform.SetParent(interior.transform);
			}
			var host = interior.AddComponent<ConsoleScreens>();
			try
			{
				host.Connect(interior.transform, source, null, null, new RadarScene(Array.Empty<RadarFeature>()));
				var targets = new RenderTexture[5];
				long runtimeBytes = 0;
				for (var i = 0; i < 5; i++)
				{
					Assert.That(host.TryGetScreen(ScreenScheduler.Id(i), out var texture), Is.True);
					targets[i] = (RenderTexture)texture;
					Assert.That(texture.width, Is.EqualTo(i < 2 ? 1024 : i < 4 ? 800 : 640));
					Assert.That(texture.height, Is.EqualTo(i < 2 ? 576 : 480));
					Assert.That(targets[i].depth, Is.Zero);
					runtimeBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(targets[i]);
					Assert.That(host.TryGetScreen(ScreenScheduler.Id(i), out var again), Is.True);
					Assert.That(again, Is.SameAs(texture));
				}
				TestContext.WriteLine("five target colour bytes=" + host.TargetColourBytes + "; profiler runtime target bytes=" + runtimeBytes);
				Assert.That(host.TryGetScreen("unknown", out _), Is.False);
				host.Enabled = false;
				for (var i = 0; i < 5; i++)
				{
					Assert.That(targets[i] == null || !targets[i].IsCreated(), Is.True);
					Assert.That(host.TryGetScreen(ScreenScheduler.Id(i), out var off), Is.False);
					Assert.That(off, Is.Null);
				}
				host.Enabled = true;
				Assert.That(host.TryGetScreen("radar", out _), Is.True);
			}
			finally { UnityEngine.Object.DestroyImmediate(interior); }
		}

		// Failure mode: the radar and chartplotter palette keys (day, dusk, night) are undone on the next
		// scheduled refresh because the console forces day or night from the daylight source.
		[Test] public void PaletteKeysChoiceWinsOverDaylight_AndClearingItFollowsDaylightAgain()
		{
			Assert.That(ConsoleScreens.RadarPaletteFor(night: false, chosen: null), Is.EqualTo(FerrySim.Radar.RadarPalette.Day));
			Assert.That(ConsoleScreens.RadarPaletteFor(night: true, chosen: null), Is.EqualTo(FerrySim.Radar.RadarPalette.Night));
			Assert.That(ConsoleScreens.RadarPaletteFor(night: false, chosen: FerrySim.Radar.RadarPalette.Dusk), Is.EqualTo(FerrySim.Radar.RadarPalette.Dusk));
			Assert.That(ConsoleScreens.RadarPaletteFor(night: true, chosen: FerrySim.Radar.RadarPalette.Day), Is.EqualTo(FerrySim.Radar.RadarPalette.Day));
			Assert.That(ConsoleScreens.PlotterPaletteFor(night: false, chosen: null), Is.EqualTo(FerrySim.Chartplotter.ChartPaletteKind.Day));
			Assert.That(ConsoleScreens.PlotterPaletteFor(night: true, chosen: null), Is.EqualTo(FerrySim.Chartplotter.ChartPaletteKind.Night));
			Assert.That(ConsoleScreens.PlotterPaletteFor(night: true, chosen: FerrySim.Chartplotter.ChartPaletteKind.Dusk), Is.EqualTo(FerrySim.Chartplotter.ChartPaletteKind.Dusk));
			var host = new GameObject("palette host").AddComponent<ConsoleScreens>();
			try
			{
				Assert.That(host.RadarPaletteOverride, Is.Null);
				Assert.That(host.PlotterPaletteOverride, Is.Null);
				host.RadarPaletteOverride = FerrySim.Radar.RadarPalette.Dusk;
				host.PlotterPaletteOverride = FerrySim.Chartplotter.ChartPaletteKind.Dusk;
				// The choice belongs to the host, not to the targets, so it survives the screens being switched off and on.
				host.Enabled = false; host.Enabled = true;
				Assert.That(host.RadarPaletteOverride, Is.EqualTo(FerrySim.Radar.RadarPalette.Dusk));
				Assert.That(host.PlotterPaletteOverride, Is.EqualTo(FerrySim.Chartplotter.ChartPaletteKind.Dusk));
				host.RadarPaletteOverride = null;
				Assert.That(host.RadarPaletteOverride, Is.Null);
			}
			finally { UnityEngine.Object.DestroyImmediate(host.gameObject); }
		}
	}
}
