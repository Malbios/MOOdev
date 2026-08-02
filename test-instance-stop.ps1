<#
.SYNOPSIS
    Cleanly stops the full isolated test stack (Client, LSP server, Sidecar, MOO instance)
    started by test-instance-start.ps1.

.DESCRIPTION
    Kills Client/LSP server/Sidecar immediately via their recorded pid files - no graceful
    protocol needed, matching this whole script's "never promote test state" philosophy - then
    shuts the MOO server down in-band over its own port (the MOO shutdown() builtin) rather than
    killing its OS process by pattern, safe even with the dev world's own moo process running
    concurrently on a different port.

    Never promotes survive.test.db.new over anything; test state is discarded. The scratch content
    tree (TestScratchTree) is also left as-is - the next test-instance-start.ps1 run wipes and
    rebuilds it fresh, same as survive.test.db itself.
#>

param(
    [int]$Port = 7778
)

$ErrorActionPreference = 'Stop'

$mooRunDir = Join-Path $PSScriptRoot 'ToastStunt\run'

# Recursive kill, not just the recorded pid - defense in depth in case a
# future change reintroduces a wrapper process (e.g. `dotnet watch run`,
# which spawns a 2-3-level child tree that survives a naive single-pid kill,
# confirmed live: orphaned Sidecar/LanguageServer/Client processes
# accumulated across sessions this way before this script tracked them).
# Tolerant of an already-dead pid or a missing pid file (never started).
function Stop-ProcessTree {
    param([string]$PidFilePath, [string]$Name)

    if (-not (Test-Path $PidFilePath)) {
        Write-Host "$Name - no pid file, assuming not running."
        return
    }

    $rootPid = Get-Content $PidFilePath -Raw
    $rootPid = [int]($rootPid.Trim())

    function Get-DescendantIds {
        param([int]$OfPid)
        $kids = Get-CimInstance Win32_Process -Filter "ParentProcessId=$OfPid" -ErrorAction SilentlyContinue
        $ids = @()
        foreach ($k in $kids) {
            $ids += $k.ProcessId
            $ids += Get-DescendantIds -OfPid $k.ProcessId
        }
        return $ids
    }

    $allIds = @($rootPid) + (Get-DescendantIds -OfPid $rootPid)
    $stopped = 0
    foreach ($id in $allIds) {
        try {
            Stop-Process -Id $id -Force -ErrorAction Stop
            $stopped++
        } catch {
            # already gone
        }
    }
    Write-Host "$Name - stopped $stopped process(es)."
    Remove-Item $PidFilePath -Force -ErrorAction SilentlyContinue
}

Stop-ProcessTree -PidFilePath (Join-Path $mooRunDir 'client.test.pid') -Name 'Client'
Stop-ProcessTree -PidFilePath (Join-Path $mooRunDir 'languageserver.test.pid') -Name 'LSP server'
Stop-ProcessTree -PidFilePath (Join-Path $mooRunDir 'sidecar.test.pid') -Name 'Sidecar'

. (Join-Path $PSScriptRoot 'moo-client.ps1')

try {
    # A single leading `;` isn't real server-level eval - `parse_cmd.cc`
    # rewrites it to the plain command "eval <rest>", which needs an actual
    # "eval" verb to dispatch to (ToastCore ships one; this project's
    # Minimal.db-derived worlds - both the dev world and this test instance -
    # never installed one). The `;;` this project's own `do_command`
    # bootstrap verb recognizes (see MOOdy's CLAUDE.md) is what actually
    # reaches the real `eval()` builtin here - confirmed live: a bare
    # `;shutdown(...)` silently produces "I couldn't understand that." and
    # never sets `shutdown_triggered` at all, which is why this always
    # timed out rather than the server ever being genuinely slow to exit.
    Send-MooCommands -Port $Port -Commands @(';;shutdown("test run complete");') -WaitMs 1000 | Out-Null
} catch {
    Write-Host "Could not reach a server on port $Port - it may already be stopped." -ForegroundColor Yellow
    return
}

$deadline = (Get-Date).AddSeconds(10)
Write-Host "Waiting for port $Port to go down..." -NoNewline
while ($true) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $result = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        $ok = $result.AsyncWaitHandle.WaitOne(200)
        $stillUp = $ok -and $client.Connected
        $client.Close()
    } catch {
        $stillUp = $false
    }
    if (-not $stillUp) { break }
    if ((Get-Date) -gt $deadline) {
        Write-Host ""
        Write-Host "Server did not shut down within 10 seconds - check survive.test.log.err." -ForegroundColor Yellow
        return
    }
    Write-Host "." -NoNewline
    Start-Sleep -Milliseconds 300
}
Write-Host " down."
Write-Host "Test instance stopped. survive.test.db / survive.test.db.new left as-is (never promoted, safe to ignore or overwrite next run)."
