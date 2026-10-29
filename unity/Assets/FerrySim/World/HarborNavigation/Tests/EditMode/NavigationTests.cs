using System;
using FerrySim.World.Import;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.World.HarborNavigation.Tests
{
	public class NavigationTests
	{
		[TestCase("Fl W 2s")]
		[TestCase("Fl(2+1) R 6s")]
		[TestCase("Iso G 6s")]
		[TestCase("Oc(2) W 8s")]
		[TestCase("LFl W 5s")]
		[TestCase("Q R 1s")]
		[TestCase("F W")]
		public void FallbackIsCompiledOnceAndMatchesCharacter(string notation)
		{
			var host = new GameObject("test aid");
			try
			{
				Assert.That(LightCharacteristic.TryParse(notation, out var character), Is.True);
				var flasher = host.AddComponent<BuoyFlasher>();
				flasher.Characteristic = character;
				flasher.Lamp = host.AddComponent<Light>();
				var lights = AidLights.Find(host.transform);
				Assert.That(flasher.enabled, Is.False, "atmosphere owns the clock after registration");
				for (var i = -100; i < 1000; i++)
				{
					Assert.That(lights.CharacterLitAt(0, i * .037), Is.EqualTo(character.IsLit(i * .037)));
				}
				lights.Show(1, true);
				var allocations = MeasureAllocations(() =>
				{
					for (var i = 0; i < 1000; i++)
					{
						lights.SetNearVisible(0, i % 2 == 0);
						lights.Show(i * .037, i % 3 != 0);
					}
				});
				Assert.That(allocations, Is.Zero, "changing flash samples, day/night and near visibility");
			}
			finally { UnityEngine.Object.DestroyImmediate(host); }
		}

		internal static int MeasureAllocations(Action action)
		{
			var chars = new char[16];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true;
				var control = new string(chars);
				recorder.enabled = false;
				var calibration = recorder.sampleBlockCount;
				GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1), "GC.Alloc sees the deliberate string allocation");
				recorder.enabled = true;
				action();
				recorder.enabled = false;
				var count = recorder.sampleBlockCount;
				TestContext.WriteLine("GC.Alloc positive control=" + calibration + "; measured samples=" + count);
				return count;
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

		[Test] public void EstimatedAlternatingSequenceIsNotReportedAsChartEvidence()
		{
			var host = new GameObject("estimated character");
			try
			{
				var character = new LightCharacteristic { Rhythm = "F", Colour = "W", PeriodSeconds = 4, Groups = new[] { 1 } };
				character.SetSequence(new[] { 2.0, 2.0 }, new[] { true, false });
				var flasher = host.AddComponent<BuoyFlasher>(); flasher.Characteristic = character; flasher.Lamp = host.AddComponent<Light>();
				var lights = new AidLights(); lights.Add(flasher, sequenceEstimated: true);
				Assert.That(lights.UsesChartSequence(0), Is.False);
				Assert.That(lights.CharacterLitAt(0, 1), Is.True);
				Assert.That(lights.CharacterLitAt(0, 3), Is.False);
			}
			finally { UnityEngine.Object.DestroyImmediate(host); }
		}

		[Test] public void NearLensCullingNeverDropsFarSignal()
		{
			var host = new GameObject("near and far");
			try
			{
				LightCharacteristic.TryParse("F W", out var character);
				var flasher = host.AddComponent<BuoyFlasher>(); flasher.Characteristic = character;
				flasher.Lamp = host.AddComponent<Light>(); flasher.Lens = host.AddComponent<MeshRenderer>();
				var lights = AidLights.Find(host.transform);
				lights.SetNearVisible(0, false); lights.Show(0, true);
				Assert.That(flasher.Lens.enabled, Is.False);
				Assert.That(lights.Points[0].On, Is.True);
				lights.Show(0, false); Assert.That(lights.Points[0].On, Is.False);
			}
			finally { UnityEngine.Object.DestroyImmediate(host); }
		}

		[Test]
		public void CompletedBatchGetsDayNightAndFogStateEvenWithoutStartupDiscovery()
		{
			var host = new GameObject("atmosphere");
			try
			{
				var sun = host.AddComponent<SunController>();
				var atmosphere = host.AddComponent<Atmosphere>();
				atmosphere.Configure(sun, null);
				var point = SignalLight.Create(host.transform, "nominal bridge signal", Vector3.zero,
					Quaternion.identity, SignalColour.Green, LightSector.AllRound, 10);
				atmosphere.RegisterNavigationBatch(new AidLights(), new[] { point });
				sun.SetUtc(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc)); sun.Apply();
				atmosphere.RefreshNavigation(0);
				Assert.That(point.On, Is.False);
				sun.SetUtc(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc)); sun.Apply();
				atmosphere.RefreshNavigation(3);
				Assert.That(point.On, Is.True);
				Assert.That(atmosphere.ChartLightingOn, Is.True);
				sun.SetUtc(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc)); sun.VisibilityMetres = 200; sun.Apply();
				atmosphere.RefreshNavigation(4);
				Assert.That(point.On, Is.True);
				Assert.That(atmosphere.Optics.x, Is.EqualTo(SignalLightOptics.LightExtinction(200)).Within(1e-7));
			}
			finally { UnityEngine.Object.DestroyImmediate(host); }
		}
	}
}
