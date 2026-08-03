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

.PARAMETER Emergency
    Passes `-e` (emergency wizard mode) - drops into the server's own interactive console
    (stdin/stdout, not the network port) before normal startup, for the one-time bootstrap recipe
    in MOOdy's own CLAUDE.md ("Bootstrap verbs baked into every world"): fixing #0's wizard/
    programmer flags and adding do_command/user_connected on a brand-new Minimal.db-derived world
    that has no eval path yet. Type `continue` in that console to exit emergency mode and proceed
    to normal startup against the same db - no restart needed.

.PARAMETER Bootstrap
    Requires -Emergency. Instead of leaving the emergency console's stdin attached to your
    terminal for interactive typing/pasting, writes the standard bootstrap recipe (the exact `;;`
    block from CLAUDE.md's "Bootstrap verbs baked into every world" section - #0's wizard/
    programmer flags, do_command, user_connected, do_start_script, then `continue`) to a temp file
    and redirects it onto moo's stdin from inside the `bash -c` command line (`< file`) - not
    piped in via PowerShell's own `|` operator, which was tried first and confirmed live to break
    Ctrl+C for the rest of the session (see the launch section's own comment for why). Exists
    because pasting that block by hand into a PowerShell -> wsl.exe -> raw-mode linenoise console
    has been confirmed live to silently drop/merge lines under paste speed, leaving several
    statements missing with no error - feeding it as file input instead delivers the exact bytes
    with no human paste step in between.

    Every step checks for its own prior work first (mirrors bootstrap-moo-world.ps1's own
    existence-check pattern) and notify()s which branch it took, visible live in the console
    output - safe to re-run against a world that's already partially or fully bootstrapped from
    an earlier attempt (e.g. one that got interrupted, or a restart against the same db).

.PARAMETER LspBridgePort
    Requires -Bootstrap. Also creates the LSP bridge's dedicated service character + listener
    object (see CLAUDE.md's "LSP service character + listener" section) bound to this port, the
    same objects bootstrap-moo-world.ps1's own -LspBridgePort would create - except that script
    can never reach a world bootstrapped this way at all (it only speaks the native single-`;`
    eval convention a real ToastCore world has built in; this recipe only installs the `;;` shim),
    so this is the only way to set that up for a bare Minimal.db-derived world. Must differ from
    -Port. Skipped (and left alone) if something's already listening on this port, so it's safe to
    re-run too.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$DbPath,
    [int]$Port = 7779,
    [string]$TreeDir = '',
    [switch]$Emergency,
    [switch]$Bootstrap,
    [int]$LspBridgePort = 0
)

if ($Bootstrap -and -not $Emergency) {
    throw "-Bootstrap requires -Emergency - the bootstrap recipe only makes sense inside the emergency console."
}

if ($LspBridgePort -and -not $Bootstrap) {
    throw "-LspBridgePort requires -Bootstrap - it's only wired into the automated bootstrap recipe."
}

if ($LspBridgePort -and $LspBridgePort -eq $Port) {
    throw "-LspBridgePort must differ from -Port - they're separate listeners on the same server."
}

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
$emergencyArg = if ($Emergency) { '-e' } else { '' }

$mooCommand = "cd $wslDbDir && $wslMooBinary $emergencyArg $dbFile $dbFile.new $Port $treeArg"

if ($Bootstrap) {
    # Built as an array joined with an explicit "`n" (not a here-string's own
    # embedded newlines) so the delivered bytes are guaranteed bare LF, never
    # CRLF - a stray trailing "`r" on the "." line would make the emergency
    # console's `!strcmp(line, ".")` check fail to match, leaving it stuck
    # waiting for a terminator that never arrives. Single-quoted strings
    # throughout: this MOO code's own `\"` escapes and `$` (in `#$#moodev-*`)
    # must reach the console byte-for-byte, not be touched by PowerShell's
    # own interpolation/escaping.
    # Every step checks verb CODE content, not just verb existence, before
    # deciding whether to (re)populate it - confirmed live to matter: an
    # earlier version of this recipe guarded do_command/do_start_script with
    # a plain verb_info()/E_VERBNF existence check, which left #0 with
    # duplicate, empty (codeless) verb shells after being re-run across a
    # few separate attempts while this Ctrl+C/-LspBridgePort work was in
    # progress - the exact mechanism was never pinned down, but checking
    # code length instead of mere existence is self-healing against it
    # either way: a verb that exists but has empty code (however that
    # happened) gets its code (re)applied via set_verb_code without a
    # second add_verb, rather than being mistaken for "already done" or
    # blindly duplicated. user_connected already worked this way (check its
    # own hook content, not just its existence) - do_command/do_start_script
    # now match that same shape. Each branch notify()s which path it took -
    # visible live in the console output, since emergency mode's own
    # notify() override prints straight to stdout rather than going
    # anywhere network-bound.
    $bootstrapLines = @(
        ';;'
        '#0.wizard = 1;'
        '#0.programmer = 1;'
        'has_code = 0;'
        'try has_code = length(verb_code(#0, "do_command", 0, 1)) > 0; except (E_VERBNF) has_code = 0; endtry'
        'if (has_code)'
        '  notify(player, "do_command: already existed, left untouched");'
        'else'
        '  try verb_info(#0, "do_command"); except (E_VERBNF) add_verb(#0, {#0, "rxd", "do_command"}, {"none", "none", "none"}); endtry'
        '  set_verb_code(#0, "do_command", {"if (length(argstr) >= 2 && argstr[1..2] == \";;\")", "  result = eval(argstr[3..$]);", "  if (!result[1])", "    notify(player, \"EVAL ERROR: \" + toliteral(result[2]));", "  endif", "  return 1;", "endif", "return 0;"});'
        '  notify(player, "do_command: added");'
        'endif'
        'try'
        '  existing = verb_code(#0, "user_connected", 0, 1);'
        '  has_hook = 0;'
        '  for line in (existing) if (index(line, "#$#moodev-login-result") != 0) has_hook = 1; endif endfor'
        '  if (has_hook)'
        '    notify(player, "user_connected: already hooked, left untouched");'
        '  else'
        '    newcode = {@existing, "notify(player, \"#$#moodev-login-result ref: 0 ok: 1\");", "notify(player, \"#$#: 0\");"};'
        '    set_verb_code(#0, "user_connected", newcode);'
        '    notify(player, "user_connected: existing verb kept, moodev hook appended");'
        '  endif'
        'except (E_VERBNF)'
        '  add_verb(#0, {#0, "rxd", "user_connected"}, {"none", "none", "none"});'
        '  set_verb_code(#0, "user_connected", {"notify(player, \"#$#moodev-login-result ref: 0 ok: 1\");", "notify(player, \"#$#: 0\");"});'
        '  notify(player, "user_connected: added fresh");'
        'endtry'
        'has_code = 0;'
        'try has_code = length(verb_code(#0, "do_start_script", 0, 1)) > 0; except (E_VERBNF) has_code = 0; endtry'
        'if (has_code)'
        '  notify(player, "do_start_script: already existed, left untouched");'
        'else'
        '  try verb_info(#0, "do_start_script"); except (E_VERBNF) add_verb(#0, {#3, "rxd", "do_start_script"}, {"this", "none", "this"}); endtry'
        '  set_verb_code(#0, "do_start_script", {"callers() && raise(E_PERM);", "return eval(@args);"});'
        '  notify(player, "do_start_script: added");'
        'endif'
    )

    if ($LspBridgePort) {
        # Same idempotency approach as the block above - checked via
        # listeners() first, exactly like bootstrap-moo-world.ps1's own
        # -LspBridgePort check - and the created listener's own do_command
        # gets the identical `;;`-shim, needed because the server looks
        # verbs up on the listener object (tq->handler), not always #0.
        $bootstrapLines += @(
            'found = 0;'
            ('for l in (listeners()) if (l["port"] == ' + $LspBridgePort + ') found = l["object"]; endif endfor')
            'if (found != 0)'
            '  notify(player, "LSP bridge: already bound to " + tostr(found) + ", left untouched");'
            'else'
            '  svc = create(#-1);'
            '  svc.wizard = 1;'
            '  svc.programmer = 1;'
            '  set_player_flag(svc, 1);'
            '  lst = create(#-1);'
            '  lst.wizard = 1;'
            '  lst.programmer = 1;'
            '  add_verb(lst, {lst, "rxd", "do_login_command"}, {"none", "none", "none"});'
            '  set_verb_code(lst, "do_login_command", {"return " + tostr(svc) + ";"});'
            '  add_verb(lst, {lst, "rxd", "do_command"}, {"none", "none", "none"});'
            '  set_verb_code(lst, "do_command", {"if (length(argstr) >= 2 && argstr[1..2] == \";;\")", "  result = eval(argstr[3..$]);", "  if (!result[1])", "    notify(player, \"EVAL ERROR: \" + toliteral(result[2]));", "  endif", "  return 1;", "endif", "return 0;"});'
            ('  listen(lst, ' + $LspBridgePort + ');')
            '  notify(player, "LSP bridge: created service=" + tostr(svc) + " listener=" + tostr(lst));'
            'endif'
        )
    }

    $bootstrapLines += @('.', 'continue')
    $bootstrapContent = ($bootstrapLines -join "`n") + "`n"

    # Written to a file and redirected onto moo's stdin from *inside* the
    # bash command line (`< file`), not piped into `wsl.exe` from
    # PowerShell's own `|` operator. That distinction matters: PowerShell
    # piping redirects wsl.exe's own stdin for its entire process lifetime,
    # which - confirmed live - breaks Ctrl+C's SIGINT forwarding through the
    # whole wsl.exe -> WSL2 -> bash -> moo chain for as long as that process
    # runs (not just while the bootstrap text is being delivered), since it
    # changes how wsl.exe itself is attached to the console at the Windows
    # level. Redirecting from a file inside the bash command is a purely
    # Linux-side shell redirection instead - wsl.exe is launched exactly as
    # it is in the non-bootstrap branches below, so Ctrl+C keeps working for
    # the process's whole lifetime, including long after the bootstrap text
    # itself has been consumed.
    $bootstrapFile = Join-Path ([System.IO.Path]::GetTempPath()) "moodev-bootstrap-$([Guid]::NewGuid().ToString('N')).txt"
    [System.IO.File]::WriteAllText($bootstrapFile, $bootstrapContent, [System.Text.UTF8Encoding]::new($false))
    $wslBootstrapFile = ConvertTo-WslPath $bootstrapFile

    Write-Host "Starting MOO server on port $Port against $dbPathFull in EMERGENCY MODE," -ForegroundColor Yellow
    Write-Host "feeding it the standard MOOdy bootstrap recipe (see CLAUDE.md's 'Bootstrap" -ForegroundColor Yellow
    Write-Host "verbs baked into every world' section) instead of leaving it for interactive" -ForegroundColor Yellow
    Write-Host "typing/pasting..." -ForegroundColor Yellow
    try {
        wsl -d Ubuntu -- bash -c "$mooCommand < $wslBootstrapFile"
    } finally {
        Remove-Item $bootstrapFile -Force -ErrorAction SilentlyContinue
    }
} elseif ($Emergency) {
    Write-Host "Starting MOO server on port $Port against $dbPathFull in EMERGENCY MODE..."
    Write-Host "Type MOO code at the ';' prompt, or ';;' followed by a '.'-terminated block; 'continue' exits to normal startup." -ForegroundColor Yellow
    wsl -d Ubuntu -- bash -c $mooCommand
} else {
    Write-Host "Starting MOO server on port $Port against $dbPathFull..."
    wsl -d Ubuntu -- bash -c $mooCommand
}

Write-Host ''
$newPath = "$dbPathFull.new"
if ((Test-Path $newPath) -and ((Get-Item $newPath).Length -gt 0)) {
    Copy-Item $newPath $dbPathFull -Force
    Write-Host "Saved: $dbFile.new promoted to $dbFile." -ForegroundColor Green
} else {
    Write-Host "No $dbFile.new dump found - nothing to save." -ForegroundColor Yellow
}
