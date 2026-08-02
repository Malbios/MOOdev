<#
.SYNOPSIS
    Starts just the MOO server (no Sidecar, no LSP server, no Client) against a given db file -
    for quickly poking at an arbitrary database without booting the full test.ps1 stack.

.DESCRIPTION
    Runs the ToastStunt submodule's built `moo` binary under WSL, in the foreground, against
    whatever -DbPath you point it at. On clean shutdown (in-game `;;shutdown();` or Ctrl+C), the
    resulting <db>.new is promoted over the original db file, same as test.ps1's own MOO server
    tab - so the next launch against the same -DbPath continues from where you left off.

    Defaults to port 7779 - deliberately distinct from test.ps1's default MooPort (7777) and
    test-instance-start.ps1's default Port (7778), so this can run alongside either without a
    collision.

.PARAMETER DbPath
    Path (relative or absolute) to the .db file to boot. Must already exist - this script doesn't
    seed one for you.

.PARAMETER Port
    Port for the MOO server to listen on. Default: 7779.

.PARAMETER TreeDir
    Optional path to a content tree to root FileIO at (the `-i` flag). Omitted by default - most
    ad hoc uses of this script don't need FileIO wired up at all.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$DbPath,
    [int]$Port = 7779,
    [string]$TreeDir = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot  = $PSScriptRoot
$mooBinary = Join-Path $repoRoot 'ToastStunt\build\moo'

function Test-PortInUse {
    param([int]$TestPort)
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $result = $client.BeginConnect('127.0.0.1', $TestPort, $null, $null)
        $ok = $result.AsyncWaitHandle.WaitOne(200)
        if ($ok -and $client.Connected) { $client.Close(); return $true }
        $client.Close()
        return $false
    } catch {
        return $false
    }
}

function ConvertTo-WslPath {
    # 'C:\dev\moo\moody' -> '/mnt/c/dev/moo/moody'
    param([string]$WindowsPath)
    $drive = $WindowsPath.Substring(0, 1).ToLower()
    $rest = $WindowsPath.Substring(2) -replace '\\', '/'
    "/mnt/$drive$rest"
}

# --- Preflight checks --------------------------------------------------------

if (-not (Test-Path $mooBinary)) {
    throw "moo binary not found at $mooBinary - build it first (see M0 in toaststunt-dev-environment-plan.md)."
}

if (-not (Test-Path $DbPath)) {
    throw "Db file not found at $DbPath."
}
$dbPathFull = (Resolve-Path $DbPath).Path
$dbDir  = Split-Path $dbPathFull -Parent
$dbFile = Split-Path $dbPathFull -Leaf

if ($TreeDir -and -not (Test-Path $TreeDir)) {
    throw "TreeDir not found at $TreeDir."
}

if (Test-PortInUse -TestPort $Port) {
    throw "Port $Port is already in use - pass a different -Port (test.ps1 defaults to 7777, test-instance-start.ps1 to 7778)."
}

# --- Launch --------------------------------------------------------------

$wslDbDir = ConvertTo-WslPath $dbDir
$wslMooBinary = ConvertTo-WslPath $mooBinary
$treeArg = if ($TreeDir) { "-i $(ConvertTo-WslPath (Resolve-Path $TreeDir).Path)" } else { '' }

Write-Host "Starting MOO server on port $Port against $dbPathFull..."
wsl -d Ubuntu -- bash -c "cd $wslDbDir && $wslMooBinary $dbFile $dbFile.new $Port $treeArg"

Write-Host ''
$newPath = "$dbPathFull.new"
if ((Test-Path $newPath) -and ((Get-Item $newPath).Length -gt 0)) {
    Copy-Item $newPath $dbPathFull -Force
    Write-Host "Saved: $dbFile.new promoted to $dbFile." -ForegroundColor Green
} else {
    Write-Host "No $dbFile.new dump found - nothing to save." -ForegroundColor Yellow
}
