<#
.SYNOPSIS
    Starts Sidecar + LanguageServer + Client against an ALREADY-RUNNING MOO server (e.g. one
    started with start-moo-only.ps1) - isolated enough that multiple copies of this script can
    run side by side, against different worlds, with no port or build conflicts.

.DESCRIPTION
    Does NOT start or manage a MOO server at all - that's start-moo-only.ps1's job, run
    separately, once per world you want up. This script only wires Sidecar/LanguageServer/Client
    up to whichever already-running MOO instance you point it at.

    Runs in the foreground; Ctrl+C stops all three child processes cleanly. One terminal window
    per concurrent instance, same shape as start-moo-only.ps1's own foreground/Ctrl+C pattern -
    no separate stop script to remember.

    Isolation strategy - what's shared vs. what's unique per invocation:
    - Sidecar.exe / LanguageServer.exe / the Fable-compiled Client JS (`output/`) are all
      content-deterministic - same source in, same binary/JS out, regardless of which MOO
      instance you later point them at via CLI args/env vars. These are BUILT ONCE into a shared
      `bin\ide-stack\` / `output\` location and safely reused/rebuilt-in-place across every
      concurrent or sequential invocation - a named Mutex serializes just the build steps
      themselves, so two invocations starting at the same moment don't race on the same
      obj/bin/output files (confirmed against test-instance-start.ps1's own doc comment: the
      `LanguageServer -> Metadata -> Language` ProjectReference resolution breaks if
      BaseIntermediateOutputPath is also isolated, so `obj/` deliberately stays shared - that's
      exactly the directory a concurrent, unsynchronized `dotnet build` could corrupt).
    - The Vite BUILD output is different - `VITE_SIDECAR_WS_URL`/`VITE_LSP_WS_URL`/
      `VITE_DATABASE_NAME` are baked into the bundle at `vite build` time via
      `import.meta.env` (confirmed in App.fs), so two instances pointed at different MOO
      targets genuinely need different bundles. Each invocation gets its own `--outDir`,
      keyed by its own auto-picked Client port - never shared, never mutex-protected, just
      structurally unique.
    - Sidecar/LSP/Client's own HTTP ports are auto-picked free ports (bind to port 0, read back
      what the OS assigned, release it) - never fixed defaults, so N concurrent invocations never
      collide with each other or with test.ps1/test-instance-start.ps1's own fixed port sets.
      Small accepted race: another process could grab the port in the moment between releasing
      the probe listener and the real service binding it - same trade-off every "find a free
      port" helper makes.
    - Logs and PIDs are named after the auto-picked Sidecar port, so concurrent runs' logs never
      overwrite each other either.

    Also re-binds the world's dedicated LSP-service listener (`listen(#LspListenerObj,
    LspBridgeMooPort)`) every run, wrapped in a MOO try/except - it doesn't persist across a MOO
    server restart (confirmed live, same as MOOdy's CLAUDE.md documents for test.ps1's own
    profiles), and start-moo-only.ps1 doesn't do this itself. The listener object's number isn't
    assumed to be any fixed value (unlike MOO-World's own #5) - a world bootstrapped via
    bootstrap-moo-world.ps1 gets a dynamically-created object number instead, so -LspListenerObj
    must be passed explicitly.

.PARAMETER MooHost
    Host the MOO server is listening on. Default: 127.0.0.1.

.PARAMETER MooPort
    Port the already-running MOO server (e.g. from start-moo-only.ps1) is listening on. No
    default - always pass it explicitly.

.PARAMETER LspBridgeMooPort
    Port the world's dedicated LSP-service listener is bound to (see MOOdy's CLAUDE.md "LSP
    service character + listener" section). Must already have been bootstrapped once on this
    world (bootstrap-moo-world.ps1's -LspBridgePort, or test.ps1's own profile) - this script
    only re-binds listen() on restart, it doesn't create the objects/verbs.

.PARAMETER LspListenerObj
    Object number of that listener (e.g. 5 on MOO-World; whatever bootstrap-moo-world.ps1
    reported as `lst` for another world).

.PARAMETER TreeDir
    Content tree for this MOO world (Moo:TreeDir) - where the Sidecar exports/reads/commits
    MOOcode. Must already exist.

.PARAMETER MooUsername
    Account name for the content-tree export's own connection (`Sidecar.exe export ... --user`,
    see `MooEval.connect`'s own comment). Default: `wizard` - correct for a bare `Minimal.db`
    world with no real accounting. A real, separately-accounted world (confirmed live against a
    HellMOO-derived one) needs its actual wizard-equivalent character's name instead - a bare
    `connect wizard` there silently never authenticates, and the export just hangs until
    ToastStunt's own 300-second not-logged-in connection timeout kills it. Only ever a bare
    `connect <name>`, no password - see the same comment for why that's the limit of what this
    supports.

.PARAMETER RefreshExport
    Forces a full `Sidecar.exe export` even if TreeDir already has a content tree. Omit for the
    normal case: once a tree exists, this script skips the export entirely and starts straight
    from what's already on disk - confirmed live that a full export against a large real-world
    database (~633k objects) can take 30+ minutes (see the "Large real-world database
    performance" kanban card), so re-running it unconditionally on every single launch is not
    something day-to-day IDE starts should pay for. Most live edits made *through* the IDE
    already keep the tree in sync incrementally on their own (`IdeActions.fs`'s per-object
    live-save re-export) without needing this. Reach for -RefreshExport when the world was
    changed some other way since the tree was last exported - a manual `.program`/telnet edit
    bypassing the Sidecar entirely, or a genuinely fresh TreeDir being pointed at content that
    already has history elsewhere.

.EXAMPLE
    # Terminal 1:
    .\start-moo-only.ps1 -DbPath C:\dev\moo\ToastCore-World\world.db -Port 7779
    # Terminal 2:
    .\start-ide-stack.ps1 -MooPort 7779 -LspBridgeMooPort 7781 -LspListenerObj 11 -TreeDir C:\dev\moo\ToastCore-World

    # At the same time, a second independent world, in two more terminals:
    .\start-moo-only.ps1 -DbPath C:\dev\moo\MOO-World\world.db -Port 7780
    .\start-ide-stack.ps1 -MooPort 7780 -LspBridgeMooPort 7782 -LspListenerObj 5 -TreeDir C:\dev\moo\MOO-World
#>

param(
    [string]$MooHost = '127.0.0.1',
    [Parameter(Mandatory = $true)] [int]$MooPort,
    [Parameter(Mandatory = $true)] [int]$LspBridgeMooPort,
    [Parameter(Mandatory = $true)] [int]$LspListenerObj,
    [Parameter(Mandatory = $true)] [string]$TreeDir,
    [string]$MooUsername = 'wizard',
    [switch]$RefreshExport
)

$ErrorActionPreference = 'Stop'

$repoRoot     = $PSScriptRoot
$moodevRoot   = $repoRoot
$sidecarProj  = Join-Path $moodevRoot 'src\Sidecar\Sidecar.fsproj'
$lspProj      = Join-Path $moodevRoot 'src\LanguageServer\LanguageServer.fsproj'
$clientDir    = Join-Path $moodevRoot 'src\Client'
$sidecarOutDir = Join-Path $moodevRoot 'src\Sidecar\bin\ide-stack'
$lspOutDir     = Join-Path $moodevRoot 'src\LanguageServer\bin\ide-stack'
$sidecarExe    = Join-Path $sidecarOutDir 'Sidecar.exe'
$lspExe        = Join-Path $lspOutDir 'LanguageServer.exe'
$runLogDir     = Join-Path $moodevRoot 'ToastStunt\run'
if (-not (Test-Path $runLogDir)) {
    New-Item -ItemType Directory -Path $runLogDir -Force | Out-Null
}

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

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Wait-ForPort {
    param([int]$WaitPort, [string]$Name, [int]$TimeoutSeconds = 60)
    Write-Host "Waiting for $Name on port $WaitPort..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-PortInUse -TestPort $WaitPort)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ""
            throw "$Name did not come up on port $WaitPort within $TimeoutSeconds seconds. Check its log under $runLogDir."
        }
        Write-Host "." -NoNewline
        Start-Sleep -Milliseconds 300
    }
    Write-Host " up."
}

# --- Preflight -----------------------------------------------------------------

if (-not (Test-PortInUse -TestPort $MooPort)) {
    throw "Nothing is listening on $MooHost`:$MooPort - start the MOO server first (start-moo-only.ps1)."
}

if (-not (Test-Path $TreeDir)) {
    throw "TreeDir not found at $TreeDir."
}
$TreeDir = (Resolve-Path $TreeDir).Path

if (-not (Test-Path (Join-Path $moodevRoot '.config\dotnet-tools.json'))) {
    throw "dotnet tool manifest not found under $moodevRoot."
}
Push-Location $moodevRoot
try {
    dotnet tool restore | Out-Null
} finally {
    Pop-Location
}

if (-not (Test-Path (Join-Path $clientDir 'node_modules'))) {
    Write-Host "node_modules missing in Client, running npm install (one-time)..."
    Push-Location $clientDir
    try {
        npm install
    } finally {
        Pop-Location
    }
}

# Auto-pick free ports up front - never fixed defaults, so concurrent invocations
# never collide with each other.
$sidecarPort = Get-FreePort
$lspPort = Get-FreePort
$clientPort = Get-FreePort

$sidecarLogPath = Join-Path $runLogDir "ide-stack-$sidecarPort.sidecar.log"
$lspLogPath     = Join-Path $runLogDir "ide-stack-$sidecarPort.lsp.log"
$clientLogPath  = Join-Path $runLogDir "ide-stack-$sidecarPort.client.log"
$clientDistDir  = Join-Path $moodevRoot "src\Client\dist-ide-stack-$sidecarPort"

Write-Host "Target MOO: $MooHost`:$MooPort (LSP bridge port $LspBridgeMooPort, listener #$LspListenerObj)"
Write-Host "Tree:       $TreeDir"
Write-Host "Ports:      Sidecar $sidecarPort / LSP $lspPort / Client $clientPort"

# --- Re-bind the world's LSP-service listener -----------------------------------
#
# Doesn't persist across a MOO restart - wrapped in a MOO try/except, harmless if
# somehow already bound (e.g. another already-running start-ide-stack.ps1 instance
# against this same world already did it this session).
. (Join-Path $repoRoot 'moo-client.ps1')
Send-MooCommands -HostName $MooHost -Port $MooPort -Commands @(";;try listen(#$LspListenerObj, $LspBridgeMooPort); except e (ANY) endtry;") -WaitMs 1000 | Out-Null

# --- Build (mutex-serialized - shared obj/bin/output across every invocation) ---

$buildMutex = New-Object System.Threading.Mutex($false, 'Global\MoodyIdeStackBuild')
$buildMutex.WaitOne() | Out-Null
try {
    Write-Host "Building Sidecar..."
    dotnet build $sidecarProj "--property:OutputPath=$sidecarOutDir\" -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Sidecar build failed." }

    Write-Host "Building LanguageServer..."
    dotnet build $lspProj "--property:OutputPath=$lspOutDir\" -v quiet
    if ($LASTEXITCODE -ne 0) { throw "LanguageServer build failed." }

    Write-Host "Compiling Client (Fable)..."
    Push-Location $clientDir
    try {
        dotnet fable . -o output
        if ($LASTEXITCODE -ne 0) { throw "Fable compile failed." }
    } finally {
        Pop-Location
    }
} finally {
    $buildMutex.ReleaseMutex()
}

# --- Export/refresh the content tree ---------------------------------------------
#
# GraphStore.init reads corponyms.moo/objects/* unconditionally at LanguageServer
# startup and throws hard (FORMAT.md's own "fail loudly, don't guess" policy) if
# they don't exist yet - confirmed live: a brand-new, never-exported TreeDir made
# the LanguageServer crash on launch with a bare FileNotFoundException, hanging
# this script at Wait-ForPort until its own timeout. So a tree that doesn't exist
# yet always gets a full export, no way around it - but once one exists, this
# script no longer re-exports it on every subsequent launch by default (see
# -RefreshExport's own doc comment for why: confirmed live that a full export
# against a large real-world database can take 30+ minutes, making "always
# re-export" a bad default for day-to-day IDE starts). Mirrors reconfigureTarget's
# own C# logic in Program.fs (init-if-fresh / export / stage / commit-only-if-
# dirty) rather than test-instance-start.ps1's version, which wipes the tree
# first - that's wrong here, TreeDir is a real content project, not disposable
# scratch.
if (-not (Test-Path (Join-Path $TreeDir '.git'))) {
    git -C $TreeDir init --initial-branch=main -q
}

$treeAlreadyExists = Test-Path (Join-Path $TreeDir 'corponyms.moo')

if ($RefreshExport -or -not $treeAlreadyExists) {
    if ($treeAlreadyExists) {
        Write-Host "Refreshing content tree (-RefreshExport was passed - this can take a long time for a large database)..."
    } else {
        Write-Host "Content tree not found yet - exporting for the first time (this can take a long time for a large database)..."
    }
    & $sidecarExe export $TreeDir $MooHost $MooPort --user $MooUsername
    if ($LASTEXITCODE -ne 0) { throw "Export into $TreeDir failed." }
    git -C $TreeDir add -A
    if (git -C $TreeDir status --porcelain) {
        git -C $TreeDir -c user.name="MOOdy IDE Stack" -c user.email="ide-stack@moo.local" commit -q -m "Sync on start-ide-stack.ps1 launch ($MooHost`:$MooPort)"
    }
} else {
    Write-Host "Content tree already exists - skipping export (pass -RefreshExport to force a full refresh)." -ForegroundColor Yellow
}

# --- Client bundle - unique outDir, bakes in this instance's own ws URLs --------

Write-Host "Bundling Client for this instance (vite build)..."
Push-Location $clientDir
try {
    $env:VITE_SIDECAR_WS_URL = "ws://127.0.0.1:$sidecarPort/ws"
    $env:VITE_LSP_WS_URL = "ws://127.0.0.1:$lspPort/lsp"
    $env:VITE_DATABASE_NAME = "IdeStack-$sidecarPort"
    $env:MOODEV_CLIENT_PORT = "$clientPort"
    npx vite build --outDir $clientDistDir --emptyOutDir
    if ($LASTEXITCODE -ne 0) { throw "Client build failed." }
} finally {
    Pop-Location
}

# --- Launch: single, directly-tracked processes ---------------------------------

Write-Host "Starting Sidecar..."
$sidecarArgs = "--Moo:Host=$MooHost --Moo:Port=$MooPort --Moo:TreeDir=`"$TreeDir`" --Moo:LspBridgePort=$LspBridgeMooPort --urls http://127.0.0.1:$sidecarPort"
$sidecarProc = Start-Process $sidecarExe -ArgumentList $sidecarArgs -WindowStyle Hidden -RedirectStandardOutput $sidecarLogPath -RedirectStandardError "$sidecarLogPath.err" -PassThru

Write-Host "Starting LanguageServer..."
$lspArgs = "--Survive:Root=`"$TreeDir`" --Sidecar:BridgeUrl=ws://127.0.0.1:$sidecarPort/lsp-bridge --urls http://127.0.0.1:$lspPort"
$lspProc = Start-Process $lspExe -ArgumentList $lspArgs -WindowStyle Hidden -RedirectStandardOutput $lspLogPath -RedirectStandardError "$lspLogPath.err" -PassThru

Write-Host "Starting Client preview server..."
$viteJs = Join-Path $clientDir 'node_modules\vite\bin\vite.js'
$clientArgs = "`"$viteJs`" preview --outDir $clientDistDir --port $clientPort --strictPort"
$clientProc = Start-Process 'node.exe' -ArgumentList $clientArgs -WorkingDirectory $clientDir -WindowStyle Hidden -RedirectStandardOutput $clientLogPath -RedirectStandardError "$clientLogPath.err" -PassThru

try {
    Wait-ForPort -WaitPort $sidecarPort -Name 'Sidecar'
    # 240s, not the 60s default: LanguageServer.exe loads Metadata.Loader's
    # full metadata graph synchronously before it starts listening -
    # confirmed live against a real ~425-object/17MB exported tree
    # (HellMOO-World) that this alone takes 90-120s, comfortably blowing the
    # 60s default and getting torn down by this script's own timeout right
    # before it would have succeeded. Sidecar/Client don't have this scaling
    # problem (Sidecar talks to the MOO live via eval(), no upfront tree
    # parse; Client is just a static preview server), so only this wait
    # needs the larger budget.
    Wait-ForPort -WaitPort $lspPort -Name 'LSP server' -TimeoutSeconds 240
    Wait-ForPort -WaitPort $clientPort -Name 'Client'

    Write-Host ""
    Write-Host "IDE stack ready:"
    Write-Host "  Sidecar:    http://127.0.0.1:$sidecarPort (PID $($sidecarProc.Id))"
    Write-Host "  LSP server: http://127.0.0.1:$lspPort (PID $($lspProc.Id))"
    Write-Host "  Client:     http://127.0.0.1:$clientPort (PID $($clientProc.Id))"
    Write-Host "  Logs:       $runLogDir\ide-stack-$sidecarPort.*.log(.err)"
    Write-Host ""
    Write-Host "Open http://127.0.0.1:$clientPort - Ctrl+C here stops all three processes." -ForegroundColor Cyan

    while ($true) { Start-Sleep -Seconds 1 }
} finally {
    Write-Host ""
    Write-Host "Stopping..."
    foreach ($p in @($sidecarProc, $lspProc, $clientProc)) {
        if ($p -and -not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path $clientDistDir) {
        Remove-Item -Recurse -Force $clientDistDir -ErrorAction SilentlyContinue
    }
    Write-Host "Stopped."
}
