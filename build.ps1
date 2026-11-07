<#
  Builds, tests and packages AirPods Control.
    .\build.ps1                 # test + self-contained single-file release in .\dist
    .\build.ps1 -SkipTests
  Output:
    dist\AirPodsControl-portable\AirPodsControl.exe   (GUI, self-contained, x64)
    dist\AirPodsControl-portable\airpodsctl.exe       (CLI diagnostics / PoC)
    dist\AirPodsControl-<ver>-win-x64-portable.zip
    dist\AirPodsControl-<ver>-setup.exe               (only if Inno Setup 6 is installed)
#>
param([switch]$SkipTests, [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$dist = Join-Path $PSScriptRoot "dist"
$out = Join-Path $dist "AirPodsControl-portable"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

if (-not $SkipTests) {
    dotnet test tests\AirPodsControl.Tests -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
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
