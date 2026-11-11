using System;
using System.Collections;
using System.IO;
using FerrySim.Chartplotter;
using FerrySim.Chartplotter.Tests;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Core.Radar;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.UI.Displays;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Console.Tests
{
	// Failure modes: blank UV-mounted display, collapsed chart layout, frozen pixels cleared, refresh allocates.
	public class ConsolePresentationTests
	{
		private GameObject _interior;
		private ConsoleScreens _screens;
		private StubOwnShip _own;
		private readonly InstrumentStub _instruments = new InstrumentStub();
		private static string Folder => Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/console-screens"));
		private sealed class InstrumentStub : IInstrumentSource
		{
			public FakeInstrumentReadout Readout = FakeInstrumentReadout.Underway();
			public bool HasInstruments => true;
			public IInstrumentReadout Instruments => Readout;
			public void AcknowledgeAlarms() { }
			public void SilenceAlarms() { }
			public void SetLampTest(bool on) => Readout.LampTest = on;

			public void SetShallowWaterThreshold(double? metres) { }
		}
		[TearDown] public void Dispose() { if (_interior != null) Object.DestroyImmediate(_interior); }
		[UnityTest] public IEnumerator StationaryScheduledChart_KeepsItsRetainedGeometry()
		{
			Create("t-class"); _screens.AutomaticRefresh = false;
			var paints = 0;
			_screens.Plotter.Canvas.generateVisualContent += _ => paints++;
			for (var i = 0; i < 180; i++) { _screens.Tick(i / 60.0, i); yield return null; }
			var before = paints; var renders = _screens.Plotter.Canvas.RenderCount;
			var refreshes = _screens.RefreshCount("plotter");
			for (var i = 180; i < 360; i++) { _screens.Tick(i / 60.0, i); yield return null; }
			Assert.That(_screens.RefreshCount("plotter"), Is.GreaterThan(refreshes));
			Assert.That(_screens.Plotter.Canvas.RenderCount, Is.EqualTo(renders), "no legitimate static regeneration");
			Assert.That(paints, Is.EqualTo(before), "a scheduled data refresh must not re-upload unchanged chart geometry");
		}
		[UnityTest] public IEnumerator BothInteriors_RetainPicturesAndLayout_AndReadoutsChange()
		{
			foreach (var id in new[] { "t-class", "k-class" })
			{
				Create(id);
				for (var i = 0; i < 90; i++) { _own.Place(i / 60.0, 0, 0, 20, 8); yield return null; }
				Assert.That(_screens.Plotter.HasView, Is.True);
				Assert.That(_screens.Plotter.StaticRenders, Is.GreaterThan(0));
				for (var i = 0; i < 4; i++)
				{
					Assert.That(_screens.TryGetScreen(ScreenScheduler.Id(i), out var image), Is.True);
					var mount = Find(_interior.transform, ConsoleScreens.MountNames[i] + "Surface");
					Assert.That(mount.GetComponent<Renderer>().sharedMaterial.mainTexture, Is.SameAs(image));
					var capture = Read((RenderTexture)image);
					Assert.That(PlotterRig.DistinctColours(capture), Is.GreaterThan(20), ScreenScheduler.Id(i));
					Object.Destroy(capture);
				}
				_screens.SetWheelhouseVisible(false);
				yield return null; yield return null;
				_screens.TryGetScreen("plotter", out var plotter);
				var before = Read((RenderTexture)plotter);
				var pixels = before.GetPixels32();
				var paints = _screens.Plotter.Targets.PaintCount;
				for (var i = 0; i < 120; i++) yield return null;
				var after = Read((RenderTexture)plotter);
				CollectionAssert.AreEqual(pixels, after.GetPixels32());
				Assert.That(_screens.Plotter.Targets.PaintCount, Is.EqualTo(paints));
				Assert.That(_screens.Plotter.Viewport.contentRect.width, Is.GreaterThan(2));
				Object.Destroy(before); Object.Destroy(after);
				var earlier = new Texture2D[4];
				for (var i = 0; i < 4; i++) { _screens.TryGetScreen(ScreenScheduler.Id(i), out var image); earlier[i] = Read((RenderTexture)image); }
				_instruments.Readout.Set(Channel.EnginePortSpeed, Units.RpmToRadiansPerSecond(2200));
				_instruments.Readout.Set(Channel.EngineStarboardSpeed, Units.RpmToRadiansPerSecond(2100));
				_own.Place(5, 0, 0, 90, 10);
				_screens.SetWheelhouseVisible(true);
				for (var i = 0; i < 30; i++) yield return null;
				for (var i = 0; i < 4; i++)
				{
					_screens.TryGetScreen(ScreenScheduler.Id(i), out var image); after = Read((RenderTexture)image);
					CollectionAssert.AreNotEqual(earlier[i].GetPixels32(), after.GetPixels32(), ScreenScheduler.Id(i) + " must update");
					Object.Destroy(earlier[i]); Object.Destroy(after);
				}
				Assert.That(_screens.Plotter.HeadingText, Does.Contain("090"));
				Object.Destroy(_interior); _interior = null; yield return null;
			}
		}
		[UnityTest] public IEnumerator ScheduledCallsAllocateNothingAfterWarmup()
		{
			Create("t-class");
			_screens.AutomaticRefresh = false;
			_screens.SetPopOutShown("alarms", true);
			for (var i = 0; i < 600; i++) { Move(i); _screens.Tick(i / 60.0, i); yield return null; }
			var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			var chars = new char[16]; recorder.enabled = true; var calibration = new string(chars); recorder.enabled = false;
			GC.KeepAlive(calibration); Assert.That(recorder.sampleBlockCount, Is.EqualTo(1));
			var samples = 0;
			try
			{
				for (var i = 600; i < 1200; i++)
				{
					Move(i);
					recorder.enabled = true; _screens.Tick(i / 60.0, i); recorder.enabled = false;
					samples += recorder.sampleBlockCount; yield return null;
				}
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
			TestContext.WriteLine("isolated scheduled calls: " + samples + " GC.Alloc samples / 600 frames; positive control 1");
			Assert.That(samples, Is.Zero);
		}
		private void Move(int frame)
		{
			_own.Place(frame / 60.0, frame * .02, 0, 20, 8);
			var phase = frame / 60 % 4;
			_instruments.Readout.Set(Channel.EnginePortSpeed, Units.RpmToRadiansPerSecond(1400 + phase * 100));
			_instruments.Readout.Set(Channel.EngineStarboardSpeed, Units.RpmToRadiansPerSecond(1300 + phase * 100));
			_instruments.Readout.SetPhase(0, (AlarmPhase)phase);
		}
		private void Create(string id)
		{
			_instruments.Readout = FakeInstrumentReadout.Underway();
			var prefab = Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior");
			Assert.That(prefab, Is.Not.Null);
			_interior = Object.Instantiate(prefab);
			_own = _interior.AddComponent<StubOwnShip>(); _own.LoadVessel(id); _own.Place(0, 0, 0, 20, 8);
			_screens = _interior.AddComponent<ConsoleScreens>();
			var runtime = SyntheticHarbor.Runtime();
			_screens.Connect(_interior.transform, _own, _instruments, ChartTessellation.Build(SyntheticHarbor.Chart(runtime), runtime), new RadarScene(Array.Empty<RadarFeature>()));
			_screens.SetWheelhouseVisible(true);
		}
		public static Transform Find(Transform root, string name)
		{
			if (root.name == name) return root;
			for (var i = 0; i < root.childCount; i++) { var found = Find(root.GetChild(i), name); if (found != null) return found; }
			return null;
		}
		public static Texture2D Read(RenderTexture target)
		{
			var prior = RenderTexture.active; RenderTexture.active = target;
			var result = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			result.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); result.Apply(); RenderTexture.active = prior; return result;
		}
	}
}
