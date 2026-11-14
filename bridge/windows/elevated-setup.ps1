<#
  AirPods Control — one-time elevated part of the driverless (WSL2) setup. Started by the app with a single UAC prompt.
  Does NOT change Test Mode, Secure Boot, Code Integrity or any driver.

  1. (optional) installs usbipd-win from winget (dorssel.usbipd-win, GPL-3.0)
  2. usbipd bind --hardware-id <VID:PID>   (shares the Bluetooth adapter; Windows keeps using it until attach)
  3. copies the BR/EDR link key of the given AirPods only: Windows keeps pairing keys in
     HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys, readable only by SYSTEM, so a one-shot
     scheduled task running as SYSTEM exports that key branch to a SYSTEM-only temp file; this script keeps only the
     requested device values, writes them to -OutFile (user profile) and deletes the temp export and the task.
  Writes a JSON status file (-StatusFile) for the app.
#>
param(
    [switch]$InstallUsbipd,
    [Parameter(Mandatory)][string]$VidPid,
    [string]$DeviceMacs = "",          # comma-separated 12-hex-digit addresses, e.g. A1B2C3D4E5F6
    [Parameter(Mandatory)][string]$OutFile,
    [Parameter(Mandatory)][string]$StatusFile,
    [switch]$Unbind                    # removal: usbipd unbind --hardware-id
)
$ErrorActionPreference = "Stop"
$status = [ordered]@{ usbipd = "skipped"; bind = "skipped"; keys = "skipped"; error = $null }
function Save-Status { $status | ConvertTo-Json | Set-Content -Path $StatusFile -Encoding UTF8 }

function Find-Usbipd {
    $c = Get-Command usbipd.exe -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $p = Join-Path $env:ProgramFiles "usbipd-win\usbipd.exe"
    if (Test-Path $p) { return $p }
    return $null
}

try {
    if ($Unbind) {
        $u = Find-Usbipd
        $ErrorActionPreference = "Continue"
        if ($u) { & $u unbind --hardware-id $VidPid 2>&1 | Out-Null; $status.bind = "unbound (exit $LASTEXITCODE)" }
        Save-Status; exit 0
    }

    if ($InstallUsbipd -and -not (Find-Usbipd)) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $winget) { throw "winget is not available; install usbipd-win manually from https://github.com/dorssel/usbipd-win/releases" }
        $ErrorActionPreference = "Continue"
        & $winget.Source install --exact --id dorssel.usbipd-win --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-Null
        $ErrorActionPreference = "Stop"
        if (-not (Find-Usbipd)) { throw "usbipd-win installation failed (winget exit $LASTEXITCODE)" }
        $status.usbipd = "installed"
    } elseif (Find-Usbipd) { $status.usbipd = "present" }

    $usbipd = Find-Usbipd
    if (-not $usbipd) { throw "usbipd-win is not installed" }
    # usbipd prints "info:" lines on stderr; under Windows PowerShell 5.1 with ErrorActionPreference=Stop, "2>&1" would
    # turn them into a terminating error although bind succeeded. Judge by the exit code only.
    $ErrorActionPreference = "Continue"
    $out = & $usbipd bind --hardware-id $VidPid 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($code -ne 0 -and ($out -join " ") -notmatch "already") { throw "usbipd bind failed (exit $code): $($out -join ' ')" }
    $status.bind = "ok"

    if ($DeviceMacs) {
        $wanted = $DeviceMacs.Split(",") | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ -match '^[0-9a-f]{12}$' }
        $tmp = Join-Path $env:SystemRoot ("Temp\apc-keys-" + [guid]::NewGuid().ToString("N") + ".reg")
        $task = "AirPodsControl-KeyExport-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\reg.exe" `
            -Argument "export `"HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys`" `"$tmp`" /y"
        Register-ScheduledTask -TaskName $task -Action $action -User "NT AUTHORITY\SYSTEM" -RunLevel Highest -Force | Out-Null
        try {
            Start-ScheduledTask -TaskName $task
            for ($i = 0; $i -lt 50 -and -not (Test-Path $tmp); $i++) { Start-Sleep -Milliseconds 200 }
            Start-Sleep -Milliseconds 300
            if (-not (Test-Path $tmp)) { throw "key export task produced no file" }
            # keep only the adapter headers and the requested device values
            $keep = New-Object System.Collections.Generic.List[string]
            $keep.Add("Windows Registry Editor Version 5.00")
            $header = $null; $cont = $false
            foreach ($line in (Get-Content -Path $tmp -Encoding Unicode)) {
                if ($line.StartsWith("[")) { $header = $line; $cont = $false; continue }
                if ($cont) { $keep.Add($line); $cont = $line.TrimEnd().EndsWith("\"); continue }
                if ($header -and $line -match '^"([0-9a-fA-F]{12})"=hex:' -and $wanted -contains $Matches[1].ToLowerInvariant()) {
                    if (-not $keep.Contains($header)) { $keep.Add($header) }
                    $keep.Add($line); $cont = $line.TrimEnd().EndsWith("\")
                }
            }
            New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
            Set-Content -Path $OutFile -Value $keep -Encoding UTF8
            $status.keys = "exported $(($keep | Where-Object { $_ -match '=hex:' }).Count)"
        } finally {
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
            if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        }
    }
} catch {
    $status.error = $_.Exception.Message
}
Save-Status
