<#
.SYNOPSIS
    Starts a headless, fully isolated test stack (MOO server, Sidecar, LSP server, Client) for
    automated/Playwright-driven testing - separate from the dev/play world that test.ps1 opens in
    visible Windows Terminal tabs.

.DESCRIPTION
    Copies survive.db (kept up to date by the dev world's own save-on-shutdown) to
    survive.test.db and boots the MOO server against that copy, with no visible console window.

    Isolation is structural, not just the MOO process: the Sidecar (which owns all git-based
    version control - eval() over a wizard connection, no MOO-side file writes since $vcs was
    retired) is pointed at a dedicated scratch git tree, TestScratchTree, built fresh every run by
    exporting whatever's actually live on this run's test MOO instance (via `Sidecar.exe export`) -
    never a real content repo like Survive. This is what test-instance-start.ps1 got wrong before:
    it only ever isolated the MOO server's own port, while every manual Sidecar/LSP/Client launch
    for verification kept defaulting to whatever real tree happened to be configured.

    Every service is launched as a single, directly-tracked OS process (no `dotnet watch`/`npm run
    dev` wrapper layers, which leave orphaned children behind when killed) - Sidecar.exe and
    LanguageServer.exe run from a one-shot `dotnet build`, and the Client is built once
    (`npm run build`) then served statically via `node vite.js preview`, with no file watcher or
    HMR involved at all.

    Nothing this instance does is ever promoted back to survive.db or the real Survive repo - stop
    it with test-instance-stop.ps1, which just tears everything down, no save.

.PARAMETER Port
    Port for the test MOO instance to listen on. Defaults to 7778 so it can run alongside the dev
    world (test.ps1's default MooPort 7777) at the same time.

.PARAMETER SidecarPort
    Port for the isolated Sidecar. Defaults to 5900.

.PARAMETER LspPort
    Port for the isolated LSP server. Defaults to 5950.

.PARAMETER ClientPort
    Port for the isolated Client static preview server. Defaults to 5199.

.PARAMETER LspBridgeMooPort
    Port the world's dedicated LSP-service listener binds to (see moo-dev's CLAUDE.md bootstrap
    docs) - distinct from Port, a different player character. Defaults to 7782 (test.ps1's own
    Survive profile already uses 7780).
#>

param(
    [int]$Port = 7778,
    [int]$SidecarPort = 5900,
    [int]$LspPort = 5950,
    [int]$ClientPort = 5199,
    [int]$LspBridgeMooPort = 7782
)

$ErrorActionPreference = 'Stop'

$repoRoot    = $PSScriptRoot
$mooRunDir   = Join-Path $repoRoot 'ToastStunt\run'
$mooBinary   = Join-Path $mooRunDir '..\build\moo'
$surviveDb   = Join-Path $mooRunDir 'survive.db'
$testDb      = Join-Path $mooRunDir 'survive.test.db'
$treeDir     = Join-Path $repoRoot 'TestScratchTree'
$logPath     = Join-Path $mooRunDir 'survive.test.log'
$pidPath     = Join-Path $mooRunDir 'survive.test.pid'

$moodevRoot   = $repoRoot
$sidecarProj  = Join-Path $moodevRoot 'src\Sidecar\Sidecar.fsproj'
$lspProj      = Join-Path $moodevRoot 'src\LanguageServer\LanguageServer.fsproj'
$clientDir    = Join-Path $moodevRoot 'src\Client'
$sidecarOutDir = Join-Path $moodevRoot 'src\Sidecar\bin\test-instance'
$lspOutDir     = Join-Path $moodevRoot 'src\LanguageServer\bin\test-instance'
$sidecarExe   = Join-Path $sidecarOutDir 'Sidecar.exe'
$lspExe       = Join-Path $lspOutDir 'LanguageServer.exe'

$sidecarLogPath = Join-Path $mooRunDir 'sidecar.test.log'
$sidecarPidPath = Join-Path $mooRunDir 'sidecar.test.pid'
$lspLogPath     = Join-Path $mooRunDir 'languageserver.test.log'
$lspPidPath     = Join-Path $mooRunDir 'languageserver.test.pid'
$clientLogPath  = Join-Path $mooRunDir 'client.test.log'
$clientPidPath  = Join-Path $mooRunDir 'client.test.pid'

function ConvertTo-WslPath {
    # 'C:\dev\moo\moo-dev\ToastStunt' -> '/mnt/c/dev/moo/moo-dev/ToastStunt'
    param([string]$WindowsPath)
    $drive = $WindowsPath.Substring(0, 1).ToLower()
    $rest = $WindowsPath.Substring(2) -replace '\\', '/'
    "/mnt/$drive$rest"
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

function Wait-ForPort {
    param([int]$WaitPort, [string]$Name, [int]$TimeoutSeconds = 30)
    Write-Host "Waiting for $Name on port $WaitPort..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-PortInUse -TestPort $WaitPort)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ""
            throw "$Name did not come up on port $WaitPort within $TimeoutSeconds seconds. Check its log under $mooRunDir."
        }
        Write-Host "." -NoNewline
        Start-Sleep -Milliseconds 300
    }
    Write-Host " up."
}

# --- Preflight ---------------------------------------------------------------

foreach ($p in @(
    @{ Port = $Port; Name = 'MOO test instance' },
    @{ Port = $SidecarPort; Name = 'Sidecar' },
    @{ Port = $LspPort; Name = 'LSP server' },
    @{ Port = $ClientPort; Name = 'Client' }
)) {
    if (Test-PortInUse -TestPort $p.Port) {
        throw "Port $($p.Port) ($($p.Name)) is already in use - a test instance may already be running. Stop it first with test-instance-stop.ps1."
    }
}

if (-not (Test-Path $surviveDb)) {
    throw "survive.db not found at $surviveDb - the dev world needs to exist first (run test.ps1 at least once)."
}

if (-not (Test-Path (Join-Path $moodevRoot '.config\dotnet-tools.json'))) {
    throw "dotnet tool manifest not found under $moodevRoot - run the M1 scaffold first."
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

# --- MOO server ---------------------------------------------------------------

Write-Host "Copying latest survive.db -> survive.test.db..."
Copy-Item $surviveDb $testDb -Force

Write-Host "Starting headless test MOO instance on port $Port..."
# -ArgumentList must be ONE pre-quoted string, not an array - Start-Process's
# array-to-argv reconstruction does not reliably preserve nested quotes
# (verified earlier this project: it silently dropped quotes around a nested
# `bash -c "..."` argument, corrupting the command).
$wslMooRunDir = ConvertTo-WslPath $mooRunDir
$wslMooBinary = ConvertTo-WslPath $mooBinary
$wslArgString = "-d Ubuntu -- bash -c `"cd $wslMooRunDir && $wslMooBinary survive.test.db survive.test.db.new $Port`""
$mooProc = Start-Process wsl.exe -ArgumentList $wslArgString -WindowStyle Hidden -RedirectStandardOutput $logPath -RedirectStandardError "$logPath.err" -PassThru
$mooProc.Id | Out-File $pidPath -Force

Wait-ForPort -WaitPort $Port -Name 'MOO test instance'

# --- Build Sidecar + LanguageServer once each ---------------------------------
#
# `--property:OutputPath=` (not `-o`, which does NOT relocate where
# `dotnet watch run`/`dotnet run` actually executes from - see test.ps1's own
# comment) isolates this script's build output from the interactive dev
# stack's own `bin\watch-<Database>\` - two profiles' processes must never
# lock each other's binaries. Deliberately NOT overriding
# `BaseIntermediateOutputPath` too - test.ps1's own comment documents that
# this breaks `LanguageServer -> Metadata -> Language` `ProjectReference`
# resolution (confirmed live: "namespace or module 'Language' is not
# defined").
Write-Host "Building Sidecar..."
dotnet build $sidecarProj "--property:OutputPath=$sidecarOutDir\" -v quiet
if ($LASTEXITCODE -ne 0) { throw "Sidecar build failed." }

Write-Host "Building LanguageServer..."
dotnet build $lspProj "--property:OutputPath=$lspOutDir\" -v quiet
if ($LASTEXITCODE -ne 0) { throw "LanguageServer build failed." }

# --- Scratch content tree, fresh every run -------------------------------------
#
# Rebuilt from THIS run's own live test MOO instance (via Sidecar.exe's own
# `export` CLI subcommand - Program.fs's own doc comment calls out "the
# headless test instance on 7778" as an intended target), not cloned from the
# real Survive repo: the scratch tree needs to correspond to the
# Minimal.db-derived baseline actually booted here, not unrelated real-world
# content. Mirrors survive.test.db's own "fresh copy every run" philosophy -
# never accumulates cruft, even though it's a private scratch repo never
# pushed anywhere.
if (Test-Path $treeDir) {
    Remove-Item -Recurse -Force $treeDir
}
New-Item -ItemType Directory -Path $treeDir | Out-Null

Write-Host "Exporting live test MOO state into scratch tree..."
& $sidecarExe export $treeDir '127.0.0.1' $Port
if ($LASTEXITCODE -ne 0) { throw "Sidecar export into $treeDir failed." }

# `GitStore.fs` (`resolveParent`/`squashWipOntoMain`) hardcodes
# `repo.Branches.["main"]` with no null-check - the scratch tree's `main`
# branch must exist with a commit before any Sidecar save action runs, or the
# very first save throws inside `exportAndCommitObject`'s broad `with ex ->`
# and fails silently. `--initial-branch=main` is required explicitly since
# this machine's own `init.defaultBranch` is `master`.
git -C $treeDir init --initial-branch=main -q
git -C $treeDir add -A
git -C $treeDir -c user.name="Test Instance" -c user.email="test-instance@moo.local" commit -q -m "Initial export (test instance baseline)"

# --- Sidecar + LanguageServer, as single directly-tracked processes -----------
#
# Launched as the already-built .exe directly - not `dotnet watch run`/
# `dotnet run`, which spawn a 2-3-level child-process tree that survives a
# naive top-level kill (confirmed live: orphaned Sidecar/LanguageServer
# processes accumulated across sessions this way). A single OS process per
# service, exactly like the MOO binary's own launch pattern above.
Write-Host "Starting Sidecar..."
$sidecarArgs = "--Moo:Port=$Port --Moo:TreeDir=`"$treeDir`" --Moo:LspBridgePort=$LspBridgeMooPort --urls http://127.0.0.1:$SidecarPort"
$sidecarProc = Start-Process $sidecarExe -ArgumentList $sidecarArgs -WindowStyle Hidden -RedirectStandardOutput $sidecarLogPath -RedirectStandardError "$sidecarLogPath.err" -PassThru
$sidecarProc.Id | Out-File $sidecarPidPath -Force

Write-Host "Starting LanguageServer..."
$lspArgs = "--Survive:Root=`"$treeDir`" --Sidecar:BridgeUrl=ws://127.0.0.1:$SidecarPort/lsp-bridge --urls http://127.0.0.1:$LspPort"
$lspProc = Start-Process $lspExe -ArgumentList $lspArgs -WindowStyle Hidden -RedirectStandardOutput $lspLogPath -RedirectStandardError "$lspLogPath.err" -PassThru
$lspProc.Id | Out-File $lspPidPath -Force

# `listen()` doesn't persist across a server restart - bind the world's
# dedicated LSP-service listener (see moo-dev's CLAUDE.md bootstrap docs)
# every time. Wrapped in a MOO try/except: harmless if somehow already bound.
. (Join-Path $repoRoot 'moo-client.ps1')
Send-MooCommands -Port $Port -Commands @(";;try listen(#5, $LspBridgeMooPort); except e (ANY) endtry;") -WaitMs 1000 | Out-Null

# --- Client: build once, serve statically --------------------------------------
#
# Env vars like VITE_SIDECAR_WS_URL are baked in at `vite build` time via
# `import.meta.env` (confirmed in App.fs), not read at runtime - they must be
# set before `npm run build`, not before the preview server starts.
# `VITE_DATABASE_NAME='SurviveTest'` is a distinct label from the real
# `'Survive'` dev-world profile - cheap insurance against ever confusing a
# screenshot of one for the other.
#
# Served via `node vite.js preview` directly (not `npm run dev`'s
# `dotnet fable watch --run vite`) - a single process with no file watcher or
# HMR involved at all, which also eliminates the "server connection lost,
# polling for restart" instability observed under a long, heavily-automated
# Playwright session. Trade-off: editing Client source needs a fresh
# test-instance-start.ps1 run to see changes, no live-reload - acceptable
# here since this script isn't meant for interactively iterating on Client
# source (test.ps1 already covers that).
Write-Host "Building Client..."
Push-Location $clientDir
try {
    $env:VITE_SIDECAR_WS_URL = "ws://127.0.0.1:$SidecarPort/ws"
    $env:VITE_LSP_WS_URL = "ws://127.0.0.1:$LspPort/lsp"
    $env:VITE_DATABASE_NAME = 'SurviveTest'
    $env:MOODEV_CLIENT_PORT = "$ClientPort"
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Client build failed." }
} finally {
    Pop-Location
}

Write-Host "Starting Client preview server..."
$viteJs = Join-Path $clientDir 'node_modules\vite\bin\vite.js'
$clientArgs = "`"$viteJs`" preview --port $ClientPort --strictPort"
$clientProc = Start-Process 'node.exe' -ArgumentList $clientArgs -WorkingDirectory $clientDir -WindowStyle Hidden -RedirectStandardOutput $clientLogPath -RedirectStandardError "$clientLogPath.err" -PassThru
$clientProc.Id | Out-File $clientPidPath -Force

Wait-ForPort -WaitPort $SidecarPort -Name 'Sidecar'
Wait-ForPort -WaitPort $LspPort -Name 'LSP server'
Wait-ForPort -WaitPort $ClientPort -Name 'Client'

Write-Host ""
Write-Host "Test instance ready:"
Write-Host "  MOO server:  127.0.0.1:$Port (wsl.exe PID $($mooProc.Id))"
Write-Host "  Sidecar:     http://127.0.0.1:$SidecarPort (PID $($sidecarProc.Id))"
Write-Host "  LSP server:  http://127.0.0.1:$LspPort (PID $($lspProc.Id))"
Write-Host "  Client:      http://127.0.0.1:$ClientPort (PID $($clientProc.Id))"
Write-Host "  Content tree: $treeDir (never a real content repo like Survive)"
Write-Host "  Logs: $mooRunDir\*.test.log(.err)"
Write-Host "Stop it with: .\test-instance-stop.ps1 -Port $Port"
