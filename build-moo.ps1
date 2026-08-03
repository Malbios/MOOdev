<#
.SYNOPSIS
    Builds the ToastStunt submodule's `moo` binary under WSL2 Ubuntu.

.DESCRIPTION
    Runs `make` against the existing `ToastStunt\build` CMake build directory inside WSL -
    for rebuilding after a C source change without re-running the full configure step.
    Produces `ToastStunt\build\moo` (see start-moo-only.ps1/test.ps1, which both run that binary
    directly).

.PARAMETER Jobs
    Parallel job count passed to `make -j`. Default: 2.
#>

param(
    [int]$Jobs = 2
)

$ErrorActionPreference = 'Stop'

function ConvertTo-WslPath {
    # 'C:\dev\moo\moody' -> '/mnt/c/dev/moo/moody'
    param([string]$WindowsPath)
    $drive = $WindowsPath.Substring(0, 1).ToLower()
    $rest = $WindowsPath.Substring(2) -replace '\\', '/'
    "/mnt/$drive$rest"
}

$buildDir = Join-Path $PSScriptRoot 'ToastStunt\build'
if (-not (Test-Path $buildDir)) {
    throw "Build directory not found at $buildDir - run cmake to configure it first (see M0 in toaststunt-dev-environment-plan.md)."
}
$wslBuildDir = ConvertTo-WslPath $buildDir

Write-Host "Building ToastStunt (make -j$Jobs)..."
wsl -d Ubuntu -- bash -c "cd $wslBuildDir && make -j$Jobs"
if ($LASTEXITCODE -ne 0) { throw "ToastStunt build failed." }

Write-Host "Build succeeded: $buildDir\moo" -ForegroundColor Green
