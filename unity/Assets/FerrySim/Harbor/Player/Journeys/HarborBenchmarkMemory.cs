using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.Harbor.Player.Journeys
{
	public sealed partial class HarborBenchmark
	{
		/// <summary>Boundary snapshots allocate outside the measured update loop. Missing counters stay explicit.</summary>
		public JObject MemorySnapshot()
		{
			var snapshot=new JObject {
				["wall_s"]=Time.realtimeSinceStartupAsDouble,
				["frame"]=Time.frameCount,
				["system_used_bytes"]=Counter(_system),["system_counter_valid"]=_system.Valid,
				["mono_heap_bytes"]=Profiler.GetMonoHeapSizeLong(),["mono_used_bytes"]=Profiler.GetMonoUsedSizeLong(),
				["total_reserved_bytes"]=Profiler.GetTotalReservedMemoryLong(),["total_allocated_bytes"]=Profiler.GetTotalAllocatedMemoryLong(),
				["gfx_bytes"]=Counter(_gfx),["gfx_counter_valid"]=_gfx.Valid,
				["texture_bytes"]=Counter(_texture),["texture_counter_valid"]=_texture.Valid };
			try
			{
				HarborProcessMemory.Read(out var working,out var peak,out var pagefile,out var source);
				snapshot["process_working_bytes"]=working;snapshot["process_peak_bytes"]=peak;
				snapshot["process_pagefile_usage_bytes"]=pagefile;snapshot["process_memory_source"]=source;
			}
			catch (Exception error) {snapshot["process_memory_error"]=error.Message;}
			return snapshot;
		}
	}
}
