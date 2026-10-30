param(
	[Parameter(Mandatory=$true)][string]$Player,
	[Parameter(Mandatory=$true)][string]$Output,
	[string[]]$Scenario = @(),
	[switch]$Quick,
	[switch]$Timing,
	[switch]$NoArchive,
	[int]$TimeoutSeconds = 20000
)
$ErrorActionPreference = 'Stop'
# Invoke from any empty working folder. Inputs resolve beside this script, never under cwd.
$Player = [IO.Path]::GetFullPath($Player)
$Output = [IO.Path]::GetFullPath($Output)
if (!(Test-Path -LiteralPath $Player -PathType Leaf)) { throw "Player missing: $Player" }
if (Test-Path -LiteralPath $Output) { throw "Refusing an existing result folder: $Output" }
$RouteFile = Join-Path $PSScriptRoot 'routes.json'
$MatrixFile = Join-Path $PSScriptRoot 'matrix.json'
if (!(Test-Path -LiteralPath $RouteFile)) { throw "Missing $RouteFile" }
$Matrix = Get-Content -LiteralPath $MatrixFile -Raw | ConvertFrom-Json
$Selected = @($Matrix.scenarios | Where-Object { !$Scenario.Count -or $Scenario -contains $_.id })
if (!$Selected.Count) { throw 'No matching matrix ids' }
New-Item -ItemType Directory -Path $Output | Out-Null
Copy-Item -LiteralPath $RouteFile,$MatrixFile -Destination $Output
if (-not ('HarborMemory' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HarborMemory {
	[StructLayout(LayoutKind.Sequential)] public struct Counters {
		public uint cb, faults;
		public UIntPtr peak, working, peakPaged, paged, peakNonPaged, nonPaged, pagefile, peakPagefile, privateBytes;
	}
	[DllImport("psapi.dll", SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint cb);
	public static Counters Read(IntPtr process) {
		var c = new Counters(); c.cb=(uint)Marshal.SizeOf(typeof(Counters));
		if (!GetProcessMemoryInfo(process,ref c,c.cb)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
		return c;
	}
}
'@
}
$GpuDrivers = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,DriverDate)
$Identity = [ordered]@{ platform='Windows'; gpu_drivers=$GpuDrivers; os=[Environment]::OSVersion.VersionString; cpu=$env:PROCESSOR_IDENTIFIER; powershell=$PSVersionTable.PSVersion.ToString(); utc=[DateTime]::UtcNow.ToString('o'); player=$Player; player_sha256=(Get-FileHash -LiteralPath $Player -Algorithm SHA256).Hash.ToLowerInvariant(); quick=[bool]$Quick; process_cache='cold process, OS cache uncontrolled' }
$Identity | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Output 'host.json') -Encoding UTF8
# Native external samples independently check the player's Mono P/Invoke path and include preparation/first view.
foreach ($Case in $Selected) {
	$Folder = Join-Path $Output $Case.id
	New-Item -ItemType Directory -Path $Folder | Out-Null
	$Empty = Join-Path ([IO.Path]::GetTempPath()) ('ferrysim-harbor-empty-' + [Guid]::NewGuid().ToString('N'))
	New-Item -ItemType Directory -Path $Empty | Out-Null
	$Duration = if ($Quick) { 60 } else { $Case.duration }
	$Capture = if ($Quick) { '0' } elseif ($Case.capture) { '1' } else { '0' }
	$Traffic = if ($Case.traffic) { '1' } else { '0' }
	$Displays = if ($Case.displays) { '1' } else { '0' }
	$Rate = if ($Case.rate) { [string]$Case.rate } else { '1' }
	$TimingFlag = if ($Timing) { '1' } else { '0' }
	$SwitchDiagnostics = if ($Case.switch_diagnostics) { '1' } else { '0' }
	$SwitchCycles = if ($Case.switch_cycles) { [string]$Case.switch_cycles } else { '1' }
	$SwitchCollect = if ($Case.switch_collect) { '1' } else { '0' }
	$Arguments = @('-ferrysim-switch-diagnostics',$SwitchDiagnostics,'-ferrysim-switch-cycles',$SwitchCycles,'-ferrysim-switch-collect',$SwitchCollect,'-ferrysim-rate',$Rate,'-ferrysim-timing',$TimingFlag,'-ferrysim-route',$Case.route,'-ferrysim-route-file',$RouteFile,'-ferrysim-benchmark-output',$Folder,
		'-ferrysim-traffic',$Traffic,'-ferrysim-displays',$Displays,'-ferrysim-render',$Case.render,
		'-ferrysim-duration',"$Duration",'-ferrysim-minute',"$($Case.minute)",'-ferrysim-visibility',"$($Case.visibility)",
		'-ferrysim-capture',$Capture,'-screen-width','1920','-screen-height','1080','-screen-fullscreen','0','-force-d3d12','-logFile',(Join-Path $Folder 'Player.log'))
	@($Player) + $Arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Folder 'command.json') -Encoding UTF8
	$Quoted = @($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' })
	$Started = [DateTime]::UtcNow
	$Timer = [Diagnostics.Stopwatch]::StartNew()
	$Peak = [UInt64]0
	$Samples = New-Object IO.StreamWriter((Join-Path $Folder 'os-memory.csv'),$false)
	$Samples.WriteLine('wall_s,rss_bytes,peak_rss_bytes,pagefile_usage_bytes,private_bytes')
	$Process = $null
	$TimedOut = $false
	try {
		$Process = Start-Process -FilePath $Player -ArgumentList $Quoted -WorkingDirectory $Empty -PassThru
		while (!$Process.HasExited) {
			try {
				$Memory = [HarborMemory]::Read($Process.Handle)
				$Peak = [Math]::Max($Peak,$Memory.peak.ToUInt64())
				$Samples.WriteLine(('{0},{1},{2},{3},{4}' -f $Timer.Elapsed.TotalSeconds.ToString('R',[Globalization.CultureInfo]::InvariantCulture),$Memory.working.ToUInt64(),$Memory.peak.ToUInt64(),$Memory.pagefile.ToUInt64(),$Memory.privateBytes.ToUInt64()))
			} catch { $_.ToString() | Add-Content -LiteralPath (Join-Path $Folder 'memory-errors.log') }
			if ($Timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $TimedOut=$true; $Process.Kill(); break }
			Start-Sleep -Milliseconds 100
			$Process.Refresh()
		}
		$Process.WaitForExit()
		$FinalPeakRead=$false
		try {
			$FinalMemory=[HarborMemory]::Read($Process.Handle)
			$Peak=[Math]::Max($Peak,$FinalMemory.peak.ToUInt64())
			$FinalPeakRead=($FinalMemory.peak.ToUInt64() -gt 0 -and $FinalMemory.peak.ToUInt64() -ge $Peak)
		} catch { $_.ToString() | Add-Content -LiteralPath (Join-Path $Folder 'memory-errors.log') }
		$ExitCode = $Process.ExitCode
	} finally {
		$Samples.Dispose()
		# This directory was created here and must still be empty; never recurse over an arbitrary supplied path.
		if ((Get-ChildItem -LiteralPath $Empty -Force | Measure-Object).Count -eq 0) { [IO.Directory]::Delete($Empty) }
	}
	$Receipt = [ordered]@{exit_code=$ExitCode; launch_utc=$Started.ToString('o'); complete_wall_s=$Timer.Elapsed.TotalSeconds; peak_rss_bytes=$Peak; peak_source='GetProcessMemoryInfo PeakWorkingSetSize over active process lifetime'; timed_out=$TimedOut; final_peak_read=$FinalPeakRead; process_cache='cold process, OS cache uncontrolled'; quick=[bool]$Quick}
	$Receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Folder 'process.json') -Encoding UTF8
	$NativeStatus = 'unverified'
	$IdentityFile = Join-Path $Folder 'identity.json'
	if (Test-Path -LiteralPath $IdentityFile) {
		$Inside = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
		if ($Inside.native_process_memory.source -eq 'GetProcessMemoryInfo/PROCESS_MEMORY_COUNTERS_EX' -and $Inside.native_process_memory.working_bytes -gt 1048576 -and $Inside.native_process_memory.pagefile_usage_bytes -gt 1048576 -and $Inside.native_process_memory.peak_bytes -ge $Inside.native_process_memory.working_bytes -and $Peak -ge $Inside.native_process_memory.peak_bytes) { $NativeStatus='pass' }
	}
	[ordered]@{verdict=$NativeStatus; source='player native PSAPI counter compared with independent external PSAPI peak'; peak_rss_bytes=$Peak} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Folder 'windows-memory-proof.json') -Encoding UTF8
	Write-Host "$($Case.id): exit=$ExitCode peak=$Peak native_memory=$NativeStatus"
}
if ($NoArchive) { Write-Host "Collection complete: $Output"; return }
$Archive = $Output.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar) + '.zip'
if (Test-Path -LiteralPath $Archive) { throw "Refusing existing archive $Archive" }
Compress-Archive -LiteralPath $Output -DestinationPath $Archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $Archive -Algorithm SHA256 | Format-List
Write-Host "Return this one archive: $Archive"
