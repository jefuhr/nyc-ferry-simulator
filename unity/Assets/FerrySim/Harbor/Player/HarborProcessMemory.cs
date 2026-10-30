using System;
using System.Runtime.InteropServices;

namespace FerrySim.Harbor.Player
{
	/// <summary>Unity's Windows Mono returns zero for Process.PeakWorkingSet64; PSAPI reads the kernel counters.</summary>
	public static class HarborProcessMemory
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessMemoryCounters
		{
			public uint Size, PageFaultCount;
			public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
			public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
		}

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		[DllImport("psapi.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

		public static int WindowsCounterSize => Marshal.SizeOf<ProcessMemoryCounters>();
		public static bool Plausible(long working, long peak) => working >= 1024 * 1024 && peak >= working;

		public static void Read(out long working, out long peak, out long pagefile, out string source)
		{
			pagefile = 0;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
			var counters = new ProcessMemoryCounters { Size = (uint)WindowsCounterSize };
			if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size))
				throw new InvalidOperationException("GetProcessMemoryInfo failed, Win32 error " + Marshal.GetLastWin32Error());
			working = checked((long)counters.WorkingSetSize.ToUInt64());
			peak = checked((long)counters.PeakWorkingSetSize.ToUInt64());
			pagefile = checked((long)counters.PagefileUsage.ToUInt64());
			source = "GetProcessMemoryInfo/PROCESS_MEMORY_COUNTERS_EX";
#else
			using (var process = System.Diagnostics.Process.GetCurrentProcess())
			{
				process.Refresh();
				working = process.WorkingSet64;
				peak = process.PeakWorkingSet64;
			}
			source = "Process.WorkingSet64/PeakWorkingSet64";
#endif
			if (!Plausible(working, peak))
				throw new InvalidOperationException("Invalid process memory counters: working=" + working + ", peak=" + peak + ", source=" + source);
		}
	}
}
