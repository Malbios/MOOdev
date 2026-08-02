<#
.SYNOPSIS
    Starts the MOO server, Sidecar, LSP server, and Vite client dev server for manually
    testing the dev environment - each in its own tab of one new Windows Terminal window
    so you can watch its output.

.DESCRIPTION
    Spans all four pieces (ToastStunt fork - a submodule of this repo, MOOdy's sidecar, MOOdy's
    LSP server, MOOdy's client). Run it, wait for the "ready" line, then open the printed URL
    yourself.

    Re-running while a previous run is still up is safe - already-listening ports
    are detected and left alone rather than double-started.

    Multiple named database profiles are supported (-Database), each with its own db
    file, seed source, content tree, and port block, so more than one MOOdy instance
    can run side by side. Adding a new environment later is a new $profiles entry, not
    new script logic.

.PARAMETER Database
    Which named profile to launch (see $profiles below). Default: MooWorld.

.PARAMETER MooPort
    Override the profile's MOO server port. Must match Sidecar's Moo:Port config.

.PARAMETER SidecarPort
    Override the profile's Sidecar ASP.NET Core port.

.PARAMETER LspPort
    Override the profile's LSP server port. Must match the Client's VITE_LSP_WS_URL.

.PARAMETER ClientPort
    Override the profile's Vite dev server port.

.PARAMETER LspBridgeMooPort
    Override the profile's dedicated MOO-side LSP-service listener port (see
    MOOdy's CLAUDE.md bootstrap docs) - what the LSP process connects to
    live via the Sidecar's own `/lsp-bridge` endpoint, logging in as a
    dedicated service character rather than Wizard.
#>

param(
    [string]$Database = 'MooWorld',
    [int]$MooPort = 0,
    [int]$SidecarPort = 0,
    [int]$LspPort = 0,
    [int]$ClientPort = 0,
    [int]$LspBridgeMooPort = 0
)

$ErrorActionPreference = 'Stop'

$repoRoot       = $PSScriptRoot
$toaststuntRoot = Join-Path $repoRoot 'ToastStunt'
$mooRunDir      = Join-Path $toaststuntRoot 'run'
$mooBinary      = Join-Path $toaststuntRoot 'build\moo'

$moodevRoot     = $repoRoot
$sidecarProj    = Join-Path $moodevRoot 'src\Sidecar\Sidecar.fsproj'
$languageServerProj = Join-Path $moodevRoot 'src\LanguageServer\LanguageServer.fsproj'
$clientDir      = Join-Path $moodevRoot 'src\Client'

# For the post-startup `listen()` call below - `Send-MooCommands`.
. (Join-Path $repoRoot 'moo-client.ps1')

# --- Database profiles -------------------------------------------------------
#
# Each profile is a full, independent MOOdy instance: its own db file (seeded
# once from SeedFrom if missing - relative to $toaststuntRoot unless SeedFrom
# is itself rooted, e.g. MooWorld's own seed below), its own content tree
# (git-init'd once if missing), and its own port block so profiles can run
# simultaneously. Add a new environment by adding a table entry here - nothing
# else in this script is per-environment.

$profiles = @{
    MooWorld = @{
        DbFile      = 'world.db'
        # MOO-World is its own independent repo, checked out as a sibling of
        # this one (see MOOdy's own CLAUDE.md for the assumed layout). Its
        # own world.db is this profile's seed - a rooted path, copied once
        # into ToastStunt\run\world.db and never touched again from there on,
        # same as every other profile's SeedFrom.
        SeedFrom    = Join-Path $repoRoot '..\MOO-World\world.db'
        TreeDir     = Join-Path $repoRoot '..\MOO-World'
        MooPort     = 7777
        SidecarPort = 5000
        LspPort     = 5050
        ClientPort  = 5173
        # The world's dedicated LSP-service listener port (bound to object #5
        # there, always logging in as the service character #4 - see MOOdy's
        # CLAUDE.md bootstrap docs). Distinct from MooPort - a different
        # player character, so it never collides with a browser tab's own
        # Wizard connection on MooPort.
        LspBridgeMooPort = 7780
        LspListenerObj   = 5
    }
}

if (-not $profiles.ContainsKey($Database)) {
    throw "Unknown -Database '$Database'. Known profiles: $($profiles.Keys -join ', ')"
}
$dbProfile = $profiles[$Database]

if ($MooPort -eq 0)      { $MooPort = $dbProfile.MooPort }
if ($SidecarPort -eq 0)  { $SidecarPort = $dbProfile.SidecarPort }
if ($LspPort -eq 0)      { $LspPort = $dbProfile.LspPort }
if ($ClientPort -eq 0)   { $ClientPort = $dbProfile.ClientPort }
if ($LspBridgeMooPort -eq 0) { $LspBridgeMooPort = $dbProfile.LspBridgeMooPort }
$lspListenerObj = $dbProfile.LspListenerObj

$dbFile   = $dbProfile.DbFile
$seedFrom = $dbProfile.SeedFrom
$treeDir  = $dbProfile.TreeDir
$dbPath   = Join-Path $mooRunDir $dbFile

function Test-PortInUse {
    param([int]$Port)
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $result = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        $ok = $result.AsyncWaitHandle.WaitOne(200)
        if ($ok -and $client.Connected) { $client.Close(); return $true }
        $client.Close()
        return $false
    } catch {
        return $false
    }
}

function New-TabSegment {
    # Builds one `new-tab ...` segment of a combined `wt.exe` command line -
    # a pwsh tab under the "PowerShell" profile so it gets the same icon as a
    # normal pwsh session (a bare `Start-Process pwsh.exe` opens through the
    # plain console host instead, with a generic icon). --suppressApplicationTitle
    # is required or pwsh resets --title's tab name right after starting
    # (verified: title silently reverted without it).
    param([string]$ScriptText, [string]$WorkingDirectory, [string]$Title)
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($ScriptText))
    "new-tab --title `"$Title`" --suppressApplicationTitle -p `"PowerShell`" -d `"$WorkingDirectory`" -- pwsh.exe -NoExit -EncodedCommand $encoded"
}

function Start-Windows {
    # Opens every pending service as its own tab within ONE new Windows
    # Terminal window - a single `wt.exe` call joining each `new-tab ...`
    # segment with `;` (WT's own multi-command separator), rather than one
    # `wt.exe` call per service (which either lands every tab in whatever
    # window already happens to be open, or - if forced new per-call via
    # `-w -1` - opens four separate windows; neither is what's wanted here).
    # `-w -1` on the whole invocation is WT's documented sentinel meaning
    # "always start a brand-new window" for this one combined command (`0`
    # would mean "the most recently used window" instead).
    #
    # -ArgumentList is passed as ONE pre-quoted string, not an array -
    # Start-Process's array-to-argv reconstruction does not reliably
    # preserve nested quotes (verified: it silently dropped the quotes
    # around a nested `bash -c "..."` argument, corrupting the command).
    # The encoded command itself sidesteps quoting for the inner script text.
    param([string[]]$Segments)
    if ($Segments.Count -eq 0) { return }
    $argString = "-w -1 " + ($Segments -join ' ; ')
    Start-Process wt.exe -ArgumentList $argString
}

function Wait-ForPort {
    param([int]$Port, [string]$Name, [int]$TimeoutSeconds = 60)
    Write-Host "Waiting for $Name on port $Port..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-PortInUse -Port $Port)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ""
            throw "$Name did not come up on port $Port within $TimeoutSeconds seconds. Check its window for errors."
        }
        Write-Host "." -NoNewline
        Start-Sleep -Milliseconds 500
    }
    Write-Host " up."
}

function ConvertTo-WslPath {
    # 'C:\dev\moo\Survive' -> '/mnt/c/dev/moo/Survive'
    param([string]$WindowsPath)
    $drive = $WindowsPath.Substring(0, 1).ToLower()
    $rest = $WindowsPath.Substring(2) -replace '\\', '/'
    "/mnt/$drive$rest"
}

# --- Preflight checks -------------------------------------------------------

Write-Host "Launching profile '$Database'..."
Write-Host "Checking prerequisites..."

# wsl.exe writes UTF-16LE to stdout when redirected, which PowerShell misreads
# as space-separated characters unless OutputEncoding is switched first.
$prevOutputEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::Unicode
$wslDistros = wsl -l -q 2>&1
[Console]::OutputEncoding = $prevOutputEncoding
# NB: "-notmatch" on an array filters to non-matching elements (an array,
# which is truthy if non-empty) rather than testing "none of them match" -
# wrap "-match" (which returns matching elements) in "-not" instead.
if (-not ($wslDistros -match 'Ubuntu')) {
    throw "WSL Ubuntu distro not found. Expected it to be installed (see M0 setup)."
}

if (-not (Test-Path $mooBinary)) {
    throw "moo binary not found at $mooBinary - build it first (see M0 in toaststunt-dev-environment-plan.md)."
}

if (-not (Test-Path $mooRunDir)) {
    New-Item -ItemType Directory -Path $mooRunDir | Out-Null
}

if (-not (Test-Path $dbPath)) {
    # SeedFrom may be a bare filename (resolved under $toaststuntRoot, e.g.
    # Minimal.db) or already a rooted path (e.g. MooWorld's own world.db,
    # which lives outside ToastStunt entirely) - Join-Path doesn't detect an
    # already-rooted second argument on its own.
    $seedPath = if ([System.IO.Path]::IsPathRooted($seedFrom)) { $seedFrom } else { Join-Path $toaststuntRoot $seedFrom }
    if (-not (Test-Path $seedPath)) {
        throw "Seed db not found at $seedPath for profile '$Database'."
    }
    Write-Host "$dbFile not found - seeding from $seedFrom..."
    Copy-Item $seedPath $dbPath
}

if (-not (Test-Path $treeDir)) {
    Write-Host "Content tree not found at $treeDir - creating and git-init'ing it..."
    New-Item -ItemType Directory -Path $treeDir | Out-Null
    git -C $treeDir init | Out-Null
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

# --- Launch every pending service as a tab in one new window ----------------

$tabSegments = @()

if (Test-PortInUse -Port $MooPort) {
    Write-Host "Port $MooPort already in use - assuming the MOO server is already running, leaving it alone."
} else {
    Write-Host "Starting MOO server..."
    # This is the '$Database' profile's world: FileIO rooted at $treeDir. Once
    # the wsl command below returns - i.e. the server shut down, whether via
    # in-game `;shutdown();` or Ctrl+C - its <db>.new dump is promoted back
    # over the profile's db file, so next launch continues from where you
    # left off. This is a separate world from whatever automated tests run
    # against (see test-instance-start.ps1), which never promotes and never
    # touches these files.
    $wslTreeDir = ConvertTo-WslPath $treeDir
    $wslMooRunDir = ConvertTo-WslPath $mooRunDir
    $wslMooBinary = ConvertTo-WslPath $mooBinary
    $mooScript = @"
wsl -d Ubuntu -- bash -c `"cd $wslMooRunDir && $wslMooBinary $dbFile $dbFile.new $MooPort -i $wslTreeDir`"
Write-Host ''
if ((Test-Path '$dbPath.new') -and ((Get-Item '$dbPath.new').Length -gt 0)) {
    Copy-Item '$dbPath.new' '$dbPath' -Force
    Write-Host 'Saved: $dbFile.new promoted to $dbFile.' -ForegroundColor Green
} else {
    Write-Host 'No $dbFile.new dump found - nothing to save.' -ForegroundColor Yellow
}
"@
    $tabSegments += New-TabSegment -ScriptText $mooScript -WorkingDirectory $mooRunDir -Title "MOO Server ($Database)"
}

if (Test-PortInUse -Port $SidecarPort) {
    Write-Host "Port $SidecarPort already in use - assuming the Sidecar is already running, leaving it alone."
} else {
    Write-Host "Starting Sidecar..."
    # `watch` (not plain `run`) so this tab auto-rebuilds and restarts on its
    # own the moment a source file changes - the tab stays open and gets
    # reused indefinitely instead of needing to be closed/reopened per edit.
    # `--no-hot-reload` - F# doesn't support hot reload at all (confirmed
    # live: `dotnet watch` prints "F# does not support Hot Reload" and
    # interactively prompts "Restart your app? Yes/No/Always/Never" on every
    # single edit otherwise), so leaving it enabled is pure friction with no
    # possible benefit for this project - this makes every edit restart
    # automatically with no prompt.
    #
    # Each profile needs a genuinely separate build, not just a separate
    # *copy* of one shared build - two profiles' Sidecar processes must never
    # contend over the same on-disk files while both are running. `-o` looks
    # like it does this (it's the flag `dotnet build`/`dotnet publish` use for
    # exactly that purpose) but for `dotnet watch run` specifically it does
    # NOT relocate where the app actually executes from - confirmed live
    # (`Get-CimInstance Win32_Process`'s own `CommandLine` still showed
    # `bin\Debug\net10.0\Sidecar.exe`, the shared default, even with `-o`
    # pointed elsewhere) - it turns out to only affect an incidental copy
    # step, and reproducing the real failure (running Survive and ToastCore
    # side by side, then editing Exporter.fs to force a rebuild while both
    # are up) still crashed with the shared `Sidecar.exe` locked by the other
    # profile's still-running process, `-o` notwithstanding.
    #
    # The fix that actually works, confirmed live the same way: set the
    # `OutputPath` MSBuild property directly (needs a trailing `\` per
    # MSBuild's own convention) rather than relying on `-o`'s shorthand.
    # This redirects the `bin\` output, and this time `CommandLine` showed
    # the app genuinely running from its own `bin\watch-$Database\Sidecar.exe`
    # - re-tested the same concurrent-edit scenario with both profiles up and
    # it rebuilt cleanly, no lock, no crash. (`-p:` - MSBuild's usual short
    # alias for `--property:` - does NOT work here: `dotnet watch` itself
    # reserves `-p` as shorthand for its own `--project`, and errors out with
    # "Cannot specify both '--project' and '-p' options" if both appear.)
    #
    # Deliberately NOT also overriding `BaseIntermediateOutputPath` (the
    # `obj\` intermediate tree) - confirmed live this actively breaks any
    # project with its own `ProjectReference`s (e.g. LanguageServer ->
    # Metadata -> Language below): MSBuild global properties on the command
    # line propagate down through `ProjectReference` edges by default, so
    # overriding `BaseIntermediateOutputPath` redirects the *referenced*
    # projects' restore assets (`project.assets.json` etc.) to a path that
    # was never actually restored into, corrupting their reference
    # resolution (manifested as "The namespace or module 'Language' is not
    # defined" and every AST type "not defined," even with untouched, correct
    # source). `obj\` intermediate files are never held open by a *running*
    # process the way the final `bin\...\Sidecar.exe` is, so isolating
    # `OutputPath` alone already fixes the real bug (the file-lock collision
    # between two profiles' running processes) without this side effect -
    # confirmed live that the exe still lands in and runs from the isolated
    # `OutputPath` with no `BaseIntermediateOutputPath` override at all.
    $sidecarOutDir = Join-Path $moodevRoot "src\Sidecar\bin\watch-$Database"
    $sidecarScript = "dotnet watch run --no-hot-reload --project `"$sidecarProj`" --property:OutputPath=`"$sidecarOutDir\`" --Moo:Port=$MooPort --Moo:TreeDir=`"$treeDir`" --Moo:LspBridgePort=$LspBridgeMooPort --urls http://127.0.0.1:$SidecarPort"
    $tabSegments += New-TabSegment -ScriptText $sidecarScript -WorkingDirectory $moodevRoot -Title "Sidecar ($Database)"
}

if (Test-PortInUse -Port $LspPort) {
    Write-Host "Port $LspPort already in use - assuming the LSP server is already running, leaving it alone."
} else {
    Write-Host "Starting LSP server..."
    # Same shared-build issue, and the same `--no-hot-reload` plus
    # `--property:OutputPath=` fix, as the Sidecar above - `BaseIntermediateOutputPath`
    # deliberately left untouched here too, and especially matters for this
    # project specifically since it's the one with `ProjectReference`s
    # (LanguageServer -> Metadata -> Language) that a `BaseIntermediateOutputPath`
    # override was confirmed live to break.
    $lspOutDir = Join-Path $moodevRoot "src\LanguageServer\bin\watch-$Database"
    $lspScript = "dotnet watch run --no-hot-reload --project `"$languageServerProj`" --property:OutputPath=`"$lspOutDir\`" --Survive:Root=`"$treeDir`" --Sidecar:BridgeUrl=ws://127.0.0.1:$SidecarPort/lsp-bridge --urls http://127.0.0.1:$LspPort"
    $tabSegments += New-TabSegment -ScriptText $lspScript -WorkingDirectory $moodevRoot -Title "LSP Server ($Database)"
}

if (Test-PortInUse -Port $ClientPort) {
    Write-Host "Port $ClientPort already in use - assuming the client dev server is already running, leaving it alone."
} else {
    Write-Host "Starting client dev server..."
    # Real process env vars take precedence over .env file values (Vite's
    # dotenv loading never overwrites an already-set process env var), so
    # this overrides the checked-in .env defaults per-profile without
    # touching the file itself.
    $clientScript = @"
`$env:VITE_SIDECAR_WS_URL = 'ws://localhost:$SidecarPort/ws'
`$env:VITE_LSP_WS_URL = 'ws://localhost:$LspPort/lsp'
`$env:VITE_DATABASE_NAME = '$Database'
`$env:MOODEV_CLIENT_PORT = '$ClientPort'
npm run dev
"@
    $tabSegments += New-TabSegment -ScriptText $clientScript -WorkingDirectory $clientDir -Title "Client ($Database)"
}

Start-Windows -Segments $tabSegments

# --- Wait for readiness -------------------------------------------------------

Wait-ForPort -Port $MooPort -Name 'MOO server'

# `listen()` doesn't persist across a server restart - re-bind the world's
# dedicated LSP-service listener (see MOOdy's CLAUDE.md bootstrap docs)
# every time, not just on first launch. Wrapped in a MOO try/except: if this
# server process was already up (left alone above) and already has it bound
# from an earlier launch, re-binding the same object+port throws rather than
# silently no-op'ing - harmless either way, so it's swallowed rather than
# surfaced as a scary error on the completely normal "already running" path.
Send-MooCommands -Port $MooPort -Commands @(";;try listen(#$lspListenerObj, $LspBridgeMooPort); except e (ANY) endtry;") -WaitMs 1000 | Out-Null

Wait-ForPort -Port $SidecarPort -Name 'Sidecar'
Wait-ForPort -Port $LspPort -Name 'LSP server'
Wait-ForPort -Port $ClientPort -Name 'Client dev server'

Write-Host ""
Write-Host "Everything's up. Open this yourself when ready:"
Write-Host "  http://localhost:$ClientPort" -ForegroundColor Cyan
Write-Host ""
Write-Host "To stop: close the window this script opened (or its individual tabs)."
