# Starts the app hidden in the tray, samples memory/CPU, then closes it. Usage: .\tools\measure.ps1 [-Exe path] [-Seconds 30]
param([string]$Exe = "$PSScriptRoot\..\dist\AirPodsControl-portable\AirPodsControl.exe", [int]$Seconds = 30)
$ErrorActionPreference = "Stop"
$full = (Resolve-Path $Exe).Path
# Windows may report the process under its 8.3 short name (AIRPOD~1), so match by path, not by name.
Get-Process | Where-Object { $_.Path -and ($_.Path -like '*\AIRPOD~*.EXE' -or $_.Path -like '*\AirPodsControl.exe') } | Stop-Process
Write-Host "Measuring $full"
$p = Start-Process -FilePath $full -ArgumentList "--background" -PassThru
$elapsed = 0
foreach ($t in @(5, 15, $Seconds)) {
    Start-Sleep -Seconds ($t - $elapsed); $elapsed = $t
    $p.Refresh()
    if ($p.HasExited) { Write-Host "process exited with code $($p.ExitCode)"; return }
    "{0,3}s  WS {1,6:N1} MB  Private {2,6:N1} MB  CPU total {3,5:N2}s" -f $t, ($p.WorkingSet64/1MB), ($p.PrivateMemorySize64/1MB), $p.TotalProcessorTime.TotalSeconds
}
Stop-Process -Id $p.Id
