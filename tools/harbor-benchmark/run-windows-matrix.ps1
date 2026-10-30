param(
	[string]$Root = 'C:/ferrysim',
	[string]$Output = 'C:/ferrysim-results'
)
$ErrorActionPreference='Stop'
if (Test-Path -LiteralPath $Output) { throw "Refusing existing output: $Output" }
$Archive=$Output.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+'.zip'
if (Test-Path -LiteralPath $Archive) { throw "Refusing existing archive: $Archive" }
New-Item -ItemType Directory -Path $Output | Out-Null
$Collector=Join-Path $PSScriptRoot 'run-windows.ps1'
$Shipping=Join-Path $Root 'windows/NYC Ferry Simulator/NYC Ferry Simulator.exe'
$Diagnostic=Join-Path $Root 'diagnostic/NYC Ferry Simulator/NYC Ferry Simulator.exe'
& $Collector -Player $Shipping -Output (Join-Path $Output 'shipping') -NoArchive
# Diagnostic builds explain CPU/render/GPU/present cost; they do not accept shipping budgets.
for ($Repeat=1;$Repeat -le 3;$Repeat++) {
	& $Collector -Player $Diagnostic -Output (Join-Path $Output "diagnostic-$Repeat") -Quick -Timing -NoArchive -Scenario @('astoria-traffic0-displays1','astoria-traffic0-displays0')
}
Compress-Archive -LiteralPath $Output -DestinationPath $Archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $Archive -Algorithm SHA256 | Format-List
Write-Host "Return this single archive: $Archive"
