using System;
using System.Reflection;
using System.Runtime.Serialization;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Radar;
using FerrySim.Sim;
using FerrySim.Traffic;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.FrameStalls.Tests
{
	public class FrameAttributionTests
	{
		[Test]
		public void AnOrdinaryBenchmarkBindsTheRadarCounterAfterDisplaysConnect()
		{
			var host = new GameObject("late radar binding");
			host.SetActive(false);
			var benchmark = host.AddComponent<HarborBenchmark>();
			var radar = host.AddComponent<RadarController>();
			const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
			var radarField = typeof(HarborBenchmark).GetField("_radarTick", fields);
			var servicesField = typeof(HarborBenchmark).GetField("_services", fields);
			// Only the connection boundary is needed; no real harbor pack or composition is loaded.
			var services = FormatterServices.GetUninitializedObject(servicesField.FieldType);
			var displaysProperty = servicesField.FieldType.GetProperty("Displays");
			var displays = host.AddComponent(displaysProperty.PropertyType);
			var connectedField = displays.GetType().GetField("_connected", fields);
			displaysProperty.SetValue(services, displays);
			servicesField.SetValue(benchmark, services);
			try
			{
				benchmark.InitializeSamplerForTests();
				((ProfilerRecorder)radarField.GetValue(benchmark)).Dispose();
				radarField.SetValue(benchmark, default(ProfilerRecorder));
				Assert.That(typeof(HarborBenchmark).GetField("_profile", fields).GetValue(benchmark), Is.Null);
				benchmark.SampleFrameForTests();
				Assert.That(((ProfilerRecorder)radarField.GetValue(benchmark)).Valid, Is.False);
				radar.Refresh();
				connectedField.SetValue(displays, true);
				// Since v0.5 the displays count as connected once the console screens or the flat overlays are up; this
				// fixture has no interior, so it stands for the flat overlays having connected.
				displays.GetType().GetField("_flatConnected", fields).SetValue(displays, true);
				benchmark.SampleFrameForTests();
				var bound = (ProfilerRecorder)radarField.GetValue(benchmark);
				Assert.That(bound.Valid, Is.True, "late binding must not require the opt-in diagnostic collector");
				benchmark.SampleFrameForTests();
				Assert.That((ProfilerRecorder)radarField.GetValue(benchmark), Is.EqualTo(bound), "bind once, not on each frame");
			}
			finally
			{
				// Inactive fixture objects do not receive OnDestroy; close its profiler and event subscriptions explicitly.
				typeof(HarborBenchmark).GetMethod("OnDestroy", fields).Invoke(benchmark, null);
				UnityEngine.Object.DestroyImmediate(host);
			}
		}

		[TestCase(false, "FerrySim.Sim.RunnerUpdate")]
		[TestCase(true, "FerrySim.Traffic.Place")]
		public void AttributionScopesCloseOnEarlyReturnAndAllocateNothing(bool traffic, string name)
		{
			var host = new GameObject("frame attribution");
			host.SetActive(false);
			Action invoke;
			if (traffic) invoke = host.AddComponent<TrafficView>().Place;
			else
			{
				var runner = host.AddComponent<VesselSimulationRunner>();
				invoke = (Action)Delegate.CreateDelegate(typeof(Action), runner,
					typeof(VesselSimulationRunner).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic));
			}
			invoke();
			var marker = Recorder.Get(name); marker.enabled = false; marker.FilterToCurrentThread();
			var allocations = Recorder.Get("GC.Alloc"); allocations.enabled = false; allocations.FilterToCurrentThread();
			var chars = new char[16];
			try
			{
				marker.enabled = true; invoke(); marker.enabled = false;
				Assert.That(marker.sampleBlockCount, Is.EqualTo(1));
				allocations.enabled = true; var control = new string(chars); allocations.enabled = false;
				var calibration = allocations.sampleBlockCount; GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1));
				allocations.enabled = true;
				for (var i = 0; i < 1000; i++) invoke();
				allocations.enabled = false;
				var count = allocations.sampleBlockCount;
				TestContext.WriteLine(name + ": positive control=" + calibration + "; idle scope GC.Alloc=" + count);
				Assert.That(count, Is.Zero);
			}
			finally
			{
				marker.enabled = false; marker.CollectFromAllThreads();
				allocations.enabled = false; allocations.CollectFromAllThreads();
				UnityEngine.Object.DestroyImmediate(host);
			}
		}

		[Test]
		public void RadarRefreshEmitsTheMarkerTheBenchmarkRecords()
		{
			var host = new GameObject("radar attribution");
			var radar = host.AddComponent<RadarController>();
			radar.Refresh();
			var marker = Recorder.Get("RadarController.Refresh");
			marker.enabled = false;
			marker.FilterToCurrentThread();
			try
			{
				Assert.That(marker.isValid, Is.True, "A zero from a missing marker cannot attribute radar cost.");
				marker.enabled = true;
				radar.Refresh();
				marker.enabled = false;
				Assert.That(marker.sampleBlockCount, Is.EqualTo(1), "The early return must close the scope too.");
			}
			finally
			{
				marker.enabled = false;
				marker.CollectFromAllThreads();
				UnityEngine.Object.DestroyImmediate(host);
			}
		}
	}
}
