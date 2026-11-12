<#
.SYNOPSIS
    Builds the OpenATLAS Windows installer: runs the tests, publishes a self-contained release
    build and compiles installer\OpenATLAS.iss with Inno Setup.

.DESCRIPTION
    Output: installer\Output\OpenATLAS-Setup-<version>.exe
    The version comes from <Version> in Workbench\OpenATLAS.Workbench.csproj.
    Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -SkipTests
#>
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Workbench\OpenATLAS.Workbench.csproj'
$publishDir = Join-Path $root 'publish\OpenATLAS'

# Version from the project file (single source of truth)
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "OpenATLAS $version" -ForegroundColor Cyan

# Inno Setup compiler: per-user or per-machine install, or on PATH
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'Tests') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed: installer not built.' }
}

Write-Host 'Publishing self-contained win-x64 build...' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true --nologo `
    -p:PublishReadyToRun=true `
    -p:SatelliteResourceLanguages=en `
    -p:DebugType=none `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host 'Compiling installer...' -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot 'OpenATLAS.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $PSScriptRoot "Output\OpenATLAS-Setup-$version.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Done: $setup ($sizeMb MB)" -ForegroundColor Green
