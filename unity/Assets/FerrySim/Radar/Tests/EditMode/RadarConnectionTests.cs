using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Radar.Tests
{
	public class RadarConnectionTests
	{
		// Failure modes: scene discovery, touching the source state, stale echoes after end/new run, nonmoving refresh allocations.
		[Test] public void ExplicitConnectionReadsOnlyAndClearsWhenRunEnds()
		{
			var layers = new ChartFeatureLayer[8];
			for (var i = 0; i < layers.Length; i++) layers[i] = new ChartFeatureLayer(Array.Empty<ChartFeature>(), new Dictionary<long, int[]>());
			var chart = new HarborChartPack("chart", "runtime", layers, HarborValue.Null);
			var runtime = new HarborRuntimePack("fixture", "runtime", "depth", Array.Empty<HarborAid>(), Array.Empty<HarborBridge>(),
				Array.Empty<HarborStop>(), Array.Empty<HarborBarge>(), null, null, Array.Empty<HarborFootprint>(), HarborValue.Null, "{}");
			var host = new GameObject("explicit radar"); var source = new Source();
			try
			{
				var controller = host.AddComponent<RadarController>(); controller.enabled = false;
				controller.Connect(source, chart, runtime, null, null, new VisualElement());
				controller.Refresh(); source.Time = .5; controller.Refresh();
				Assert.That(controller.Engine.LastSpokeCount, Is.GreaterThan(0)); Assert.That(source.Time, Is.EqualTo(.5));
				Action tick = () => { source.Time += 1.0 / 60; controller.Refresh(); };
				for (var i = 0; i < 20; i++) tick();
				Assert.That(RadarDisplayTests.MeasureAllocations(() => { for (var i = 0; i < 100; i++) tick(); }), Is.Zero);
				source.Available = false; controller.Refresh(); Assert.That(controller.Engine.Echo(0, 0), Is.Zero);
			}
			finally { UnityEngine.Object.DestroyImmediate(host); }
		}
		private sealed class Source : IVesselTelemetrySource
		{
			public double Time; public bool Available = true;
			public VesselParameters Parameters => null;
			public VesselState State => new VesselState(Time, 0, 0, 0, 0, 0, Time * .1, 0, 0, 0, 0, 0, 0);
			public VesselTelemetry Telemetry => default;
			public ControlInputs Controls => default;
			public DateTimeOffset ScenarioStartUtc => DateTimeOffset.UnixEpoch;
			public bool HasTelemetry => Available;
			public long RunId => 1;
			public ContactLatch Contact => default;
		}
	}
}
