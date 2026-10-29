using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.Sim.Tests
{
	/// <summary>Failure modes: partial bundle binds, changed contacts/day/runtime accepted, leaked observer counters and late coverage holds.</summary>
	public class HarborStartTests
	{
		private string _root;
		private string _runtime;
		private string _day;
		private GameObject _object;
		private VesselSimulationRunner _runner;
		private ScenarioSettings _settings;
		private static readonly ConstantControls Controls = new ConstantControls(ControlInputs.Neutral);
		[SetUp]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "harbor-start-" + Guid.NewGuid().ToString("N"));
			_runtime = Path.Combine(_root, "harbor/runtime");
			Directory.CreateDirectory(_runtime);
			var source = Path.Combine(TestSupport.RepoRoot, "core/dotnet/FerrySim.Core.Tests/World/Fixtures");
			foreach (var file in Directory.GetFiles(source))
			{
				File.Copy(file, Path.Combine(_runtime, Path.GetFileName(file)));
			}

			var pack = SyntheticPacks.Pack(SyntheticPacks.SummerDate);
			var depth = new DepthSet("harbor", "NAVD88", pack.Depth.OutsideDepth, pack.Depth.Grids, pack.Depth.Sources, "");
			var harbor = new EnvironmentPack("harbor", pack.Date, pack.EpochUtc, pack.CoverageStart, pack.CoverageEnd, depth, pack.CurrentStations, pack.WaterLevelStations, pack.WindStations, pack.Notes, "");
			_day = EnvironmentPackWriter.Write(harbor, Path.Combine(_root, "harbor/env"));
			var digest = File.ReadAllText(Path.Combine(_root, "harbor/env/depth/DIGEST")).Trim();
			var manifestPath = Path.Combine(_runtime, "manifest.json");
			File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(new string ('0', 64), digest));
			RehashRuntime();
			var position = LocalTangentPlane.World.ToGeodetic(100, 100);
			_settings = new ScenarioSettings
			{
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				scenarioStartUtc = EnvironmentPacks.Iso(pack.EpochUtc),
				environment = new ScenarioEnvironment
				{
					pack = "harbor/" + pack.Date
				},
			};
			_object = new GameObject("harbor-atomic-start");
			_runner = _object.AddComponent<VesselSimulationRunner>();
			_runner.EnvironmentPacks = new EnvironmentPacks(_root);
			_runner.RecordRuns = false;
			_runner.Initialize(new ScenarioSettings(), Controls);
			_runner.AdvanceFrame(0.02);
		}

		[TearDown]
		public void Cleanup()
		{
			UnityEngine.Object.DestroyImmediate(_object);
			Directory.Delete(_root, true);
		}

		[TestCase("contacts")]
		[TestCase("day")]
		[TestCase("runtime")]
		[TestCase("factory")]
		[TestCase("traffic")]
		[TestCase("gate")]
		[TestCase("depth-link")]
		public void FailedCandidate_LeavesTheCurrentRunAndBindingsIntact(string failure)
		{
			var id = _runner.RunId;
			var driver = _runner.Driver;
			var env = _runner.RunEnvironment;
			var geometry = _runner.WorldGeometry;
			var path = failure == "day" ? Path.Combine(_day, "currents.csv") : Path.Combine(_runtime, failure == "contacts" ? "contacts.json" : "aids.json");
			if (failure == "day" || failure == "runtime")
			{
				File.AppendAllText(path, " ");
			}

			if (failure == "factory")
			{
				_runner.Factory = new FailingFactory();
			}

			if (failure == "traffic")
			{
				path = Path.Combine(WriteTraffic(), TrafficPackJson.ReportsFile);
				File.AppendAllText(path, " ");
			}

			if (failure == "depth-link")
			{
				path = Path.Combine(_runtime, "manifest.json");
				var depthDigest = File.ReadAllText(Path.Combine(_root, "harbor/env/depth/DIGEST")).Trim();
				File.WriteAllText(path, File.ReadAllText(path).Replace(depthDigest, new string ('a', 64)));
				RehashRuntime();
			}

			var error = Assert.Catch(() => _runner.InitializeHarbor(_settings, _runtime, (json, file) => failure == "contacts" ? throw new FormatException(file + ": fixture refusal") : EmptyWorldGeometry.Instance, Controls, validateStart: (candidate, vessel, state) =>
			{
				if (failure == "gate")
				{
					throw new InvalidOperationException("fixture start-gate refusal");
				}
			}));
			if (failure != "factory" && failure != "gate")
			{
				Assert.That(error.Message, Does.Contain(failure == "runtime" ? Path.Combine(_runtime, "DIGEST") : path));
			}

			Assert.That(_runner.RunId, Is.EqualTo(id));
			Assert.That(_runner.Driver, Is.SameAs(driver));
			Assert.That(_runner.RunEnvironment, Is.SameAs(env));
			Assert.That(_runner.WorldGeometry, Is.SameAs(geometry));
			Assert.That(_runner.AdvanceFrame(0.02).Steps, Is.EqualTo(2));
		}

		[Test]
		public void SuccessfulStart_BindsTheBundleAndKeepsObserverCountersSeparate()
		{
			var id = _runner.RunId;
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			Assert.That(_runner.RunId, Is.Not.EqualTo(id));
			Assert.That(_runner.HarborRuntime.Stops.Count, Is.EqualTo(1));
			Assert.That(_runner.RunEnvironment.Observer, Is.Not.SameAs(_runner.RunEnvironment.Simulation));
			var before = _runner.RunEnvironment.PackRun.Coverage;
			_runner.RunEnvironment.WaterLevel(1e9, 1e9, 1e9);
			Assert.That(_runner.RunEnvironment.PackRun.Coverage.TimeHolds, Is.EqualTo(before.TimeHolds));
			Assert.That(_runner.RunEnvironment.PackRun.Coverage.DepthMisses, Is.EqualTo(before.DepthMisses));
		}

		[Test]
		public void SuccessfulStart_NotifiesBindingsBeforeItsFirstPlacement()
		{
			RunEnvironment bound = null;
			_runner.RunStarted += () => bound = _runner.RunEnvironment;
			_runner.BeforePlacement += () => Assert.That(bound, Is.SameAs(_runner.RunEnvironment));
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			_runner.AdvanceFrame(.025);
			Assert.That(_runner.RenderTime, Is.EqualTo(.015).Within(1e-12));
		}

		[Test]
		public void HarborStepping_WithTrafficTimingAndCoverageObserver_AllocatesNothing()
		{
			WriteTraffic();
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			for (var i = 0; i < 100; i++)
			{
				_runner.AdvanceFrame(.01);
			}

			var chars = new char[8];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			recorder.enabled = true;
			var control = new string (chars);
			recorder.enabled = false;
			var calibration = recorder.sampleBlockCount;
			GC.KeepAlive(control);
			Assert.That(calibration, Is.EqualTo(1));
			recorder.enabled = true;
			for (var i = 0; i < 1000; i++)
			{
				_runner.AdvanceFrame(.01);
			}

			recorder.enabled = false;
			var allocations = recorder.sampleBlockCount;
			recorder.CollectFromAllThreads();
			Assert.That(_runner.Hold, Is.EqualTo(RunHold.None));
			Assert.That(_runner.Driver.StepCount, Is.EqualTo(1100));
			Assert.That(_runner.TrafficStepMilliseconds, Is.GreaterThan(0));
			Assert.That(_runner.TrafficStepP95Milliseconds, Is.GreaterThan(0));
			Assert.That(allocations, Is.Zero, "calibrated GC.Alloc includes physics, coverage readout, actual traffic monitor and its rolling timing window");
		}

		[TestCase(false, 0)]
		[TestCase(true, 1)]
		public void HeldOutsideCoveredWater_KeepsTheLastDrawnPosition(bool time, int placements)
		{
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			_runner.Driver.AddObserver(new MissObserver(_runner.RunEnvironment.PackRun, time));
			_runner.AdvanceFrame(0.1);
			Assert.That(_runner.Hold, Is.EqualTo(time ? RunHold.BeyondDayData : RunHold.OutsideCoveredWater));
			var placed = 0;
			_runner.BeforePlacement += () => placed++;
			_runner.ApplyRenderPose();
			// Past covered water the harbor datum refuses the held pose, so the vessel is not placed again; a day-end hold still is.
			Assert.That(placed, Is.EqualTo(placements));
			Assert.That(_runner.RenderPose.North, Is.EqualTo(_runner.State.North), "the render pose still follows the held state");
		}

		[TestCase(false, RunHold.OutsideCoveredWater)]
		[TestCase(true, RunHold.BeyondDayData)]
		public void FirstIncreasedCounter_HoldsWithinFrame_UntilANewStart(bool time, RunHold expected)
		{
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			_runner.Driver.AddObserver(new MissObserver(_runner.RunEnvironment.PackRun, time));
			_runner.AdvanceFrame(0.1);
			// The fault is injected after the runner's observer, so it is seen at the next completed step.
			Assert.That(_runner.Driver.StepCount, Is.InRange(1L, 2L));
			Assert.That(_runner.Hold, Is.EqualTo(expected));
			var held = _runner.Driver.StepCount;
			_runner.Resume();
			_runner.AdvanceFrame(0.1);
			Assert.That(_runner.Driver.StepCount, Is.EqualTo(held));
			_runner.InitializeHarbor(_settings, _runtime, (json, file) => EmptyWorldGeometry.Instance, Controls);
			Assert.That(_runner.Hold, Is.EqualTo(RunHold.None));
			Assert.That(_runner.AdvanceFrame(0.02).Steps, Is.EqualTo(2));
			_runner.EndRun();
			Assert.That(_runner.Hold, Is.EqualTo(RunHold.None));
		}

		private string WriteTraffic()
		{
			var date = SyntheticPacks.SummerDate;
			var pack = new TrafficPack(date, _settings.StartUtc, -3600, 90000, new[] { new TrafficVessel(0, 123456789, "SYNTHETIC", 52, TrafficClass.TugTow, 30, 10, 3, 0, 120, 2, 1, false, false) }, new TrafficReports(new[] { 0, 0 }, new[] { 0, 0 }, new double[] { 0, 120 }, new double[] { 500, 500 }, new double[] { 500, 500 }, new float[] { 0, 0 }, new float[] { 0, 0 }, new float[] { 0, 0 }), string.Empty);
			_runner.TrafficPacks = new TrafficPacks(_root);
			_settings.traffic = new ScenarioTraffic
			{
				day = date,
				leaveOut = ScenarioTraffic.Off
			};
			return TrafficPackWriter.Write(pack, Path.Combine(_root, "harbor/traffic", date));
		}

		private sealed class MissObserver : IStepObserver
		{
			private readonly DataEnvironment _env;
			private readonly bool _time;
			public MissObserver(DataEnvironment env, bool time)
			{
				_env = env;
				_time = time;
			}

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
			{
			}

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				if (_time)
				{
					_env.SampleCurrent(100, 100, 1e9);
				}
				else
				{
					_env.SampleDepth(1e9, 1e9, state.Time);
				}
			}
		}

		private sealed class FailingFactory : ISimulationFactory
		{
			public ISimulation Create(VesselParameters vessel, IEnvironment environment, in VesselState initial) => throw new InvalidOperationException("fixture factory failure");
			public ISimulation Create(VesselParameters vessel, IEnvironment environment, IWorldGeometry geometry, in VesselState initial) => throw new InvalidOperationException("fixture factory failure");
		}

		private void RehashRuntime()
		{
			using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			void Add(string name)
			{
				sha.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
				sha.AppendData(File.ReadAllBytes(Path.Combine(_runtime, name)));
				sha.AppendData(new byte[] { 0 });
			}

			Add("manifest.json");
			foreach (var name in HarborRuntimePackJson.Files)
			{
				Add(name);
			}

			File.WriteAllText(Path.Combine(_runtime, "DIGEST"), BitConverter.ToString(sha.GetHashAndReset()).Replace("-", "").ToLowerInvariant() + "\n");
		}
	}
}
