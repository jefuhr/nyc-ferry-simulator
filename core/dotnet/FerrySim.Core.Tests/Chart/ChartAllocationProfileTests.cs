using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Chart
{
	public class ChartAllocationProfileTests
	{
		private sealed class Allocations : EventListener
		{
			public readonly Dictionary<string, long> Bytes = new Dictionary<string, long>();
			protected override void OnEventSourceCreated(EventSource source)
			{
				if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
			}
			protected override void OnEventWritten(EventWrittenEventArgs e)
			{
				if (e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
				var nameIndex = e.PayloadNames.IndexOf("TypeName"); var sizeIndex = e.PayloadNames.IndexOf("AllocationAmount64");
				if (nameIndex < 0 || sizeIndex < 0) return;
				var name = (string)e.Payload[nameIndex]; var size = Convert.ToInt64(e.Payload[sizeIndex]);
				lock (Bytes) { Bytes.TryGetValue(name, out var old); Bytes[name] = old + size; }
			}
		}
		[Test, Explicit("Diagnostic sampling, excluded from timing results.")]
		public void ProfileChartAllocations()
		{
			var harbor = Path.Combine(RepoPaths.Root, "data/processed/harbor");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			using var allocations = new Allocations();
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			allocations.Dispose();
			foreach (var pair in allocations.Bytes.OrderByDescending(p => p.Value).Take(30)) TestContext.Progress.WriteLine(pair.Key + " sampled_bytes=" + pair.Value);
			GC.KeepAlive(chart); GC.KeepAlive(runtime);
		}
	}
}
