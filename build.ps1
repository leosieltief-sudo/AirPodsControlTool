<#
  Builds, tests and packages AirPods Control.
    .\build.ps1                 # test + self-contained single-file release in .\dist
    .\build.ps1 -SkipTests
    .\build.ps1 -NoKernel       # leave out the MediaTek WSL kernel (firmware licence question, see docs/RELEASING.md)
  Output:
    dist\AirPodsControl-portable\AirPodsControl.exe   (GUI, self-contained, x64)
    dist\AirPodsControl-portable\airpodsctl.exe       (CLI diagnostics / PoC)
    dist\AirPodsControl-portable\wsl-kernel\          (MediaTek WSL kernel, if assets\wsl-kernel exists)
    dist\AirPodsControl-<ver>-win-x64-portable.zip
    dist\AirPodsControl-<ver>-setup.exe               (only if Inno Setup 6 is installed)
#>
param([switch]$SkipTests, [switch]$NoKernel, [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$dist = Join-Path $PSScriptRoot "dist"
$out = Join-Path $dist "AirPodsControl-portable"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if (-not $SkipTests) {
    dotnet test tests\AirPodsControl.Tests -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
    if (Get-Command python -ErrorAction SilentlyContinue) {
        # unittest reports on stderr; with ErrorActionPreference=Stop and redirected streams PowerShell 5.1 would treat that as an error
        $ErrorActionPreference = "Continue"
        python -m unittest discover -s tests/bridge
        $pyExit = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($pyExit -ne 0) { throw "Bridge (Python) tests failed" }
    } else { Write-Host "Python not found - skipping bridge script tests" }
}

$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            # No single-file compression: compressed assemblies are inflated into private memory at startup
            # (measured ~140 MB private), uncompressed ones are memory-mapped from the exe.
            "-p:EnableCompressionInSingleFile=false", "-p:DebugType=none", "-o", $out)
dotnet publish src\AirPodsControl.App @common
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed" }
dotnet publish src\AirPodsControl.Cli @common
if ($LASTEXITCODE -ne 0) { throw "Publishing the CLI failed" }

Copy-Item README.md, LICENSE -Destination $out
Copy-Item docs -Destination (Join-Path $out "docs") -Recurse

# MediaTek WSL kernel (built by kernel/build-kernel.sh into assets/wsl-kernel, git-ignored).
# GPL-2.0: the exact source is named in BUILDINFO; build script, patches and config travel with the binary.
$kernelDir = Join-Path $PSScriptRoot "assets\wsl-kernel"
if ($NoKernel) {
    Write-Host "-NoKernel: release built WITHOUT the MediaTek WSL kernel."
} elseif (Test-Path (Join-Path $kernelDir "bzImage")) {
    $kOut = Join-Path $out "wsl-kernel"
    New-Item -ItemType Directory -Force $kOut | Out-Null
    Copy-Item (Join-Path $kernelDir "*") -Destination $kOut
    Copy-Item kernel\build-kernel.sh, kernel\gen-mediatek-ids.py -Destination $kOut
    Copy-Item kernel\patches -Destination (Join-Path $kOut "patches") -Recurse
    Copy-Item kernel\LICENCE.mediatek -Destination $kOut   # terms of the firmware embedded in bzImage (see docs/RELEASING.md)
    Write-Host "Bundled MediaTek WSL kernel: $(Get-Content (Join-Path $kernelDir 'BUILDINFO') -TotalCount 1)"
} else {
    Write-Host "assets\wsl-kernel\bzImage not found - release built WITHOUT the MediaTek WSL kernel (see kernel/build-kernel.sh)."
}

$zip = Join-Path $dist "AirPodsControl-$version-win-x64-portable.zip"
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Portable build: $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$out" "/O$dist" installer\AirPodsControl.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
} else {
    Write-Host "Inno Setup 6 not found - skipping installer (portable zip is complete)."
}
