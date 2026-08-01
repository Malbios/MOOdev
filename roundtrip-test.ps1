<#
.SYNOPSIS
    Phase 3 of moo-vcs-plan.md - the round-trip gate. Export -> import into a
    fresh instance -> export again -> assert equivalent (RoundTrip.fs
    normalizes the fields the format declares non-portable across instances -
    objnum, ownership - rather than requiring a literal file diff).

.DESCRIPTION
    Spins up two throwaway, headless ToastStunt instances from Minimal.db
    (never Survive/survive.db):
      - Source: the `;;`-eval bootstrap shim plus a three-object fixture
        ($base_thing, $room with a list-valued property and two verbs in a
        specific order, $special_room with two parents - exercising multiple
        inheritance and parent-order preservation).
      - Fresh target: the eval shim only. Everything in it must come from
        the import.

    Then: export source -> treeA, import treeA into fresh (--apply), export
    fresh -> treeB, compare-trees treeA treeB. Prints PASS/FAIL and exits
    with the comparison's own exit code. Scratch dbs/trees are removed on
    success, left in place for inspection on failure.

.PARAMETER SourcePort
    Port for the source instance. Defaults to 7810 (distinct from the dev
    world's 7777 and the automated test instance's 7778).

.PARAMETER FreshPort
    Port for the fresh target instance. Defaults to 7811.
#>

param(
    [int]$SourcePort = 7810,
    [int]$FreshPort = 7811
)

$ErrorActionPreference = 'Stop'

$toaststuntRoot = Join-Path $PSScriptRoot 'ToastStunt'
$mooBinary      = Join-Path $toaststuntRoot 'build\moo'
$minimalDb      = Join-Path $toaststuntRoot 'Minimal.db'
$sourceRunDir   = Join-Path $toaststuntRoot 'scratch-run-source'
$freshRunDir    = Join-Path $toaststuntRoot 'scratch-run-fresh'
$moodevRoot     = $PSScriptRoot
$sidecarProj    = Join-Path $moodevRoot 'src\Sidecar\Sidecar.fsproj'
$treeA          = Join-Path $moodevRoot 'scratch-roundtrip-a'
$treeB          = Join-Path $moodevRoot 'scratch-roundtrip-b'

. (Join-Path $PSScriptRoot 'moo-client.ps1')

if (-not (Test-Path $mooBinary)) {
    throw "moo binary not found at $mooBinary - build ToastStunt first."
}

# The `;;`-eval bootstrap shim, common to both instances: faithfully
# replicates ToastCore's real `;` eval-command convention (per
# moocode-reference.md) so MooEval's wire protocol is genuinely exercised,
# not a simplified stand-in.
$evalShim = @'
add_verb(#0, {#3, "rxd", "do_command"}, {"this", "none", "this"});
set_verb_code(#0, "do_command", {
  "if (index(argstr, \";\") == 1)",
  "  program = argstr[2..$];",
  "  if (!match(program, \"^ *%(;%|%(if%|fork?%|return%|while%|try%)[^a-z0-9A-Z_]%)\"))",
  "    program = \"return \" + program;",
  "  endif",
  "  {code, result} = eval(program);",
  "  if (!code)",
  "    for line in (result)",
  "      notify(player, line);",
  "    endfor",
  "  endif",
  "  return 1;",
  "endif",
  "return 0;"
});
'@

# Source-only fixture: three corponym objects exercising inheritance
# (single and multiple), a list-valued property, and verb declaration order.
$fixture = @'
base = create(#-1, #3);
add_property(#0, "base_thing", base, {#3, "r"});
add_property(base, "label", "a base thing", {#3, "rc"});
add_verb(base, {#3, "rxd", "describe"}, {"this", "none", "this"});
set_verb_code(base, "describe", {"player:tell(this.label);"});

room = create(base, #3);
add_property(#0, "room", room, {#3, "r"});
add_property(room, "description", "A basic room.", {#3, "rc"});
add_property(room, "exits", {"north", "south"}, {#3, "rc"});
add_verb(room, {#3, "rxd", "look_self l*ook"}, {"this", "none", "this"});
set_verb_code(room, "look_self", {"player:tell(this.description);"});
add_verb(room, {#3, "rxd", "tell_lines"}, {"this", "none", "any"});
set_verb_code(room, "tell_lines", {"for l in (args) player:tell(l); endfor"});

special = create({room, base}, #3);
add_property(#0, "special_room", special, {#3, "r"});
add_property(special, "magic", 1, {#3, "rc"});
add_verb(special, {#3, "rxd", "cast"}, {"this", "none", "this"});
set_verb_code(special, "cast", {"player:tell(\"Poof!\");"});
'@

function New-ScratchInstance {
    param([string]$RunDir, [string]$BootstrapContent, [int]$Port)

    if (Test-Path $RunDir) { Remove-Item $RunDir -Recurse -Force }
    New-Item -ItemType Directory -Path $RunDir | Out-Null

    # Minimal.db is committed with CRLF line endings (git checkout on
    # Windows); the checkpoint format needs bare LF - confirmed live during
    # Phase 1 (DBIO_READ_NUM parse failure otherwise).
    $dbPath = Join-Path $RunDir 'minimal.db'
    (Get-Content $minimalDb -Raw) -replace "`r`n", "`n" | Set-Content -NoNewline -Path $dbPath

    Set-Content -Path (Join-Path $RunDir 'bootstrap.moo') -Value $BootstrapContent -NoNewline

    $unixRunDir = ($RunDir -replace '^C:', '/mnt/c') -replace '\\', '/'
    $unixMooBinary = ($mooBinary -replace '^C:', '/mnt/c') -replace '\\', '/'
    $wslArgString = "-d Ubuntu -- bash -c `"cd $unixRunDir && $unixMooBinary -f bootstrap.moo minimal.db minimal.db.new $Port`""
    $proc = Start-Process wsl.exe -ArgumentList $wslArgString -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $RunDir 'moo.log') -RedirectStandardError (Join-Path $RunDir 'moo.log.err')

    $deadline = (Get-Date).AddSeconds(30)
    while ($true) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $result = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
            $ok = $result.AsyncWaitHandle.WaitOne(200)
            $up = $ok -and $client.Connected
            $client.Close()
        } catch { $up = $false }
        if ($up) { break }
        if ((Get-Date) -gt $deadline) { throw "Instance on port $Port did not come up within 30 seconds. Check $RunDir\moo.log.err." }
        Start-Sleep -Milliseconds 300
    }

    return $proc
}

function Stop-ScratchInstance {
    param([int]$Port)
    try {
        Send-MooCommands -Port $Port -Commands @(';;shutdown("round-trip test complete");') -WaitMs 1000 | Out-Null
    } catch {
        Write-Host "Could not reach port $Port to shut it down gracefully - it may already be stopped." -ForegroundColor Yellow
    }
}

$passed = $false

try {
    Write-Host "Starting source instance (fixture + eval shim) on port $SourcePort..."
    New-ScratchInstance -RunDir $sourceRunDir -BootstrapContent ($evalShim + $fixture) -Port $SourcePort | Out-Null

    Write-Host "Starting fresh target instance (eval shim only) on port $FreshPort..."
    New-ScratchInstance -RunDir $freshRunDir -BootstrapContent $evalShim -Port $FreshPort | Out-Null

    if (Test-Path $treeA) { Remove-Item $treeA -Recurse -Force }
    if (Test-Path $treeB) { Remove-Item $treeB -Recurse -Force }

    Write-Host "Exporting from source..."
    & dotnet run --project $sidecarProj -- export $treeA 127.0.0.1 $SourcePort
    if ($LASTEXITCODE -ne 0) { throw "export from source failed" }

    Write-Host "Importing into fresh target..."
    & dotnet run --project $sidecarProj -- import $treeA 127.0.0.1 $FreshPort --apply
    if ($LASTEXITCODE -ne 0) { throw "import into fresh target failed" }

    Write-Host "Exporting from fresh target..."
    & dotnet run --project $sidecarProj -- export $treeB 127.0.0.1 $FreshPort
    if ($LASTEXITCODE -ne 0) { throw "export from fresh target failed" }

    Write-Host ""
    Write-Host "Comparing trees..."
    & dotnet run --project $sidecarProj -- compare-trees $treeA $treeB
    $passed = ($LASTEXITCODE -eq 0)
} finally {
    Write-Host ""
    Write-Host "Stopping both instances..."
    Stop-ScratchInstance -Port $SourcePort
    Stop-ScratchInstance -Port $FreshPort
}

if ($passed) {
    Write-Host ""
    Write-Host "ROUND-TRIP GATE: PASS" -ForegroundColor Green
    Remove-Item $sourceRunDir, $freshRunDir, $treeA, $treeB -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
} else {
    Write-Host ""
    Write-Host "ROUND-TRIP GATE: FAIL - scratch state left in place for inspection:" -ForegroundColor Red
    Write-Host "  $sourceRunDir"
    Write-Host "  $freshRunDir"
    Write-Host "  $treeA"
    Write-Host "  $treeB"
    exit 1
}
