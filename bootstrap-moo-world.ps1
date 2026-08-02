<#
.SYNOPSIS
  One-time moodev bootstrap for an ALREADY-RUNNING MOO world reachable over the network.

.DESCRIPTION
  Installs the #0:do_command / #0:user_connected / #0:do_start_script verbs the MOOdy IDE
  needs (see CLAUDE.md's "Bootstrap verbs baked into every world" section) via a live telnet
  connection, using a single-';'-prefixed eval command.

  IMPORTANT caveat, confirmed by reading ToastStunt/src/server.cc directly: the single-';'/
  ';;' syntax at server.cc:1091 is the '-e' EMERGENCY CONSOLE's own stdin parser - it is NOT
  network-reachable at all. What CLAUDE.md calls "native eval" on a real ToastCore-derived
  world is a different thing: a convention baked into ToastCore's own database content (a
  MOOcode verb that ToastCore's login/command dispatch calls when a line starts with ';'),
  not a universal C-server feature. This script's wire format for that ('; ; CODE', a leading
  '; ' trigger plus a no-op ';' first statement to defeat the auto-return-prepend quirk) is
  taken directly from CLAUDE.md's own previously live-verified recipe for exactly this
  scenario - not guessed - but this script has only been tested end-to-end against a
  Minimal.db-derived scratch world (which correctly fails the probe below and stops, since it
  has no such verb) - there was no real ToastCore instance available to confirm the trigger
  format against. If the probe below fails against a world you believe genuinely is
  ToastCore-derived, that's worth a closer look before assuming this script is broken.

  This ONLY works if the world already has that eval convention available (a real
  ToastCore-derived world, or one that's already been through this bootstrap once). It does
  NOT work against a genuinely bare Minimal.db-derived world that has never had any eval
  mechanism installed at all - that case has no live-network solution: it can only be
  bootstrapped via the '-e' emergency console at server LAUNCH time, which needs shell/process
  access, not just a socket. The script probes for eval support first and stops cleanly,
  without touching anything, if the probe fails.

  do_command and do_start_script are only ever ADDED if they don't already exist - never
  overwritten (neither has any legitimate pre-existing content worth preserving; both are
  purely this project's own invention, so "already exists" just means a prior bootstrap run
  already added it). user_connected is different: a real ToastCore-derived world almost always
  already has one doing real MCP/confunc work (confirmed live: this is the actual, common case,
  not a hypothetical), so this verb is handled as add-if-missing / APPEND-the-moodev-hook-if-
  missing-from-an-existing-verb / skip-if-already-hooked, using the exact splice technique
  CLAUDE.md's "Bootstrap verbs" section documents (`newcode = {@verb_code(...), "notify(...)",
  "notify(...)"}` - append, never overwrite) - live-verified end to end against a disposable
  scratch instance (installed a stock-shaped user_connected, ran the append, confirmed the
  resulting verb_code has both the original logic and the new lines, confirmed a second run
  correctly detects the hook and skips instead of double-appending).

  Also creates the LSP bridge's dedicated service character + listener object (if
  -LspBridgePort is given): a plain object marked as a real player via set_player_flag()
  (required - do_login_command's caller-side check is `is_user(result)`, not just object
  validity, confirmed against ToastStunt/src/tasks.cc), plus a second plain object holding
  its own do_login_command (unconditionally returns the service character) and do_command
  (the same ;;-eval shim as #0's, needed because the server looks verbs up on the listener
  object - `tq->handler` - not always #0, confirmed against the same source) bound to
  -LspBridgePort via listen(). This exact MOO code (object creation, set_player_flag, the two
  verbs, the listen() call) was live-verified end to end against a disposable scratch MOO
  instance - reached there via the project's own ;;-eval shim, since that instance had no
  native-eval convention to trigger it through this script's own native-eval path - confirming
  the objects get created correctly, the port binds, and a fresh connection through it really
  does log in as the new service character with its own eval shim working. What's NOT been
  exercised end-to-end is this exact code running via the native ';' trigger against a real
  ToastCore world (see the caveat above) - the MOO code itself is proven, the outer wire
  format is documented-not-guessed but unconfirmed in this exact combination. See MOOdy's
  CLAUDE.md "LSP service character + listener" section for how these two objects are used
  afterward. Skipped entirely (and left alone) if something is already listening on
  -LspBridgePort.

  The connecting account MUST already be wizard+programmer (eval()'s own permission check
  requires it) - this script fixes #0's OWN flags for you, but can't grant itself wizard in
  the first place.

.PARAMETER HostName
  The MOO server's host/IP. No default - always pass it explicitly, so this never silently
  targets the wrong instance.

.PARAMETER Port
  The MOO server's player-connection port. No default, same reasoning.

.PARAMETER Username
  Login name to send as `connect <Username> <Password>`. Omit (along with -Password) to send
  a bare `connect wizard` instead, which is what a Minimal.db-derived world's unconditional
  do_login_command accepts (see CLAUDE.md's "There is no real login/accounting yet" section) -
  a real ToastCore world will need real credentials here.

.PARAMETER Password
  Password for -Username. Ignored if -Username is omitted.

.PARAMETER LspBridgePort
  If given, also create and bind the LSP bridge's service character + listener object on
  this port (must differ from -Port). Omit to skip that part entirely (hover/go-to-definition
  won't work afterward, but everything else - tree/tasks/status - will).

.PARAMETER Force
  Skip the interactive confirmation prompt before mutating the server.

.EXAMPLE
  ./bootstrap-moo-world.ps1 -HostName 127.0.0.1 -Port 8888 -LspBridgePort 8890
  # Minimal.db-derived world already past its own -e bootstrap, default wizard login.

.EXAMPLE
  ./bootstrap-moo-world.ps1 -HostName moo.example.com -Port 7777 -Username Wizard -Password hunter2 -LspBridgePort 7780
  # Real ToastCore-derived world with actual accounts.
#>
param(
    [Parameter(Mandatory)] [string]$HostName,
    [Parameter(Mandatory)] [int]$Port,
    [string]$Username,
    [string]$Password,
    [int]$LspBridgePort,
    [switch]$Force
)

if ($LspBridgePort -and $LspBridgePort -eq $Port) {
    Write-Host "-LspBridgePort must differ from -Port (they're separate listeners on the same server)." -ForegroundColor Red
    return
}

. (Join-Path $PSScriptRoot 'moo-client.ps1')

function Read-MooResponse {
    param($Stream, [int]$WaitMs = 2000)
    Start-Sleep -Milliseconds $WaitMs
    $buffer = New-Object byte[] 65536
    $ms = New-Object System.IO.MemoryStream
    while ($Stream.DataAvailable) {
        $read = $Stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $ms.Write($buffer, 0, $read)
    }
    return [System.Text.Encoding]::GetEncoding("ISO-8859-1").GetString($ms.ToArray())
}

function Send-MooLine {
    param($Stream, [string]$Line)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Line + "`r`n")
    $Stream.Write($bytes, 0, $bytes.Length)
}

function Invoke-NativeEval {
    # Wire format for ToastStunt's native single-';' eval command: a leading '; ' triggers
    # it, then a no-op ';' as the code's first statement defeats the auto-return-prepend
    # quirk for multi-statement bodies (see CLAUDE.md's "There is no real login/accounting
    # yet" -> bootstrap section for the exact same idiom, confirmed live in that context).
    #
    # This is a line-based telnet protocol: ANY embedded newline in $Code becomes a
    # separate command to the server, not a continuation - so a multi-line here-string
    # (used here purely for readability in the script source) must be flattened to one
    # physical line before sending, or only its first line is actually evaluated and
    # every line after it gets sent as its own unrecognized top-level command instead.
    param($Stream, [string]$Code, [int]$WaitMs = 2000)
    $flatCode = ($Code -replace '\r?\n\s*', ' ').Trim()
    Send-MooLine $Stream "; ; $flatCode"
    return Read-MooResponse -Stream $Stream -WaitMs $WaitMs
}

Write-Host "Target: $HostName`:$Port" -ForegroundColor Cyan
if (-not $Force) {
    $confirm = Read-Host "About to connect and (conditionally) mutate #0 on this world. Continue? [y/N]"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') {
        Write-Host "Aborted, nothing sent." -ForegroundColor Yellow
        return
    }
}

$client = New-Object System.Net.Sockets.TcpClient
try {
    $client.Connect($HostName, $Port)
} catch {
    Write-Host "Could not connect to $HostName`:$Port - $($_.Exception.Message)" -ForegroundColor Red
    return
}
$stream = $client.GetStream()

try {
    # Drain the initial banner/welcome text (also absorbs the localhost HAProxy
    # banner-suppression quirk CLAUDE.md notes - harmless either way).
    Read-MooResponse -Stream $stream -WaitMs 1000 | Out-Null

    if ($Username) {
        Send-MooLine $stream "connect $Username $Password"
    } else {
        Send-MooLine $stream "connect wizard"
    }
    $loginResponse = Read-MooResponse -Stream $stream -WaitMs 1500
    Write-Host "--- login response ---" -ForegroundColor DarkGray
    Write-Host $loginResponse

    Write-Host "Probing for native eval support..." -ForegroundColor Cyan
    $probeMarker = "MOODEV_BOOTSTRAP_PROBE_OK"
    $probeResponse = Invoke-NativeEval -Stream $stream -Code "notify(player, `"$probeMarker`");"

    if ($probeResponse -notmatch [regex]::Escape($probeMarker)) {
        Write-Host ""
        Write-Host "Native eval does not appear to work on this world (no '$probeMarker' echoed back)." -ForegroundColor Red
        Write-Host "Raw probe response was:" -ForegroundColor Red
        Write-Host $probeResponse
        Write-Host ""
        Write-Host "This is expected for a bare Minimal.db-derived world that's never been" -ForegroundColor Yellow
        Write-Host "bootstrapped at all - there's no eval mechanism to reach over the network yet." -ForegroundColor Yellow
        Write-Host "That case needs the '-e' emergency console at server LAUNCH time instead" -ForegroundColor Yellow
        Write-Host "(shell/process access to the MOO host, not just this connection) - see" -ForegroundColor Yellow
        Write-Host "CLAUDE.md's 'Bootstrap verbs baked into every world' section for that recipe." -ForegroundColor Yellow
        Write-Host "Also double check -Username/-Password/login actually succeeded above, in case" -ForegroundColor Yellow
        Write-Host "eval does exist but the connecting account isn't wizard+programmer." -ForegroundColor Yellow
        return
    }
    Write-Host "Native eval confirmed working." -ForegroundColor Green

    Write-Host "Fixing #0's own wizard/programmer flags..." -ForegroundColor Cyan
    Invoke-NativeEval -Stream $stream -Code "#0.wizard = 1; #0.programmer = 1;" | Out-Null

    $doCommandCode = ConvertTo-MooCodeListLiteral -Lines @(
        'if (length(argstr) >= 2 && argstr[1..2] == ";;")',
        '  result = eval(argstr[3..$]);',
        '  if (!result[1])',
        '    notify(player, "EVAL ERROR: " + toliteral(result[2]));',
        '  endif',
        '  return 1;',
        'endif',
        'return 0;'
    )
    Write-Host "Installing #0:do_command (skips if it already exists)..." -ForegroundColor Cyan
    $r = Invoke-NativeEval -Stream $stream -Code @"
try
  verb_info(#0, "do_command");
  notify(player, "MOODEV_DO_COMMAND_EXISTS_SKIPPED");
except (E_VERBNF)
  add_verb(#0, {#0, "rxd", "do_command"}, {"none", "none", "none"});
  set_verb_code(#0, "do_command", $doCommandCode);
  notify(player, "MOODEV_DO_COMMAND_ADDED");
endtry
"@
    if ($r -match 'MOODEV_DO_COMMAND_ADDED') { Write-Host "  added" -ForegroundColor Green }
    elseif ($r -match 'MOODEV_DO_COMMAND_EXISTS_SKIPPED') { Write-Host "  already existed, left untouched" -ForegroundColor Yellow }
    else { Write-Host "  unexpected response:`n$r" -ForegroundColor Red }

    $userConnectedCode = ConvertTo-MooCodeListLiteral -Lines @(
        'notify(player, "#$#moodev-login-result ref: 0 ok: 1");',
        'notify(player, "#$#: 0");'
    )
    Write-Host "Installing #0:user_connected (adds if missing; appends the moodev hook if the" -ForegroundColor Cyan
    Write-Host "verb already exists without it - e.g. a real ToastCore world's own MCP/confunc" -ForegroundColor Cyan
    Write-Host "logic - never overwrites; skips if the hook's already there)..." -ForegroundColor Cyan
    $r = Invoke-NativeEval -Stream $stream -Code @"
try
  existing = verb_code(#0, "user_connected", 0, 1);
  has_hook = 0;
  for line in (existing)
    if (index(line, "#$#moodev-login-result") != 0)
      has_hook = 1;
    endif
  endfor
  if (has_hook)
    notify(player, "MOODEV_USER_CONNECTED_ALREADY_HOOKED");
  else
    newcode = {@existing, "notify(player, \"#$#moodev-login-result ref: 0 ok: 1\");", "notify(player, \"#$#: 0\");"};
    set_verb_code(#0, "user_connected", newcode);
    notify(player, "MOODEV_USER_CONNECTED_APPENDED");
  endif
except (E_VERBNF)
  add_verb(#0, {#0, "rxd", "user_connected"}, {"none", "none", "none"});
  set_verb_code(#0, "user_connected", $userConnectedCode);
  notify(player, "MOODEV_USER_CONNECTED_ADDED");
endtry
"@
    if ($r -match 'MOODEV_USER_CONNECTED_ADDED') { Write-Host "  added fresh" -ForegroundColor Green }
    elseif ($r -match 'MOODEV_USER_CONNECTED_APPENDED') { Write-Host "  existing verb kept, moodev hook appended" -ForegroundColor Green }
    elseif ($r -match 'MOODEV_USER_CONNECTED_ALREADY_HOOKED') { Write-Host "  already had the moodev hook, left untouched" -ForegroundColor Yellow }
    else { Write-Host "  unexpected response:`n$r" -ForegroundColor Red }

    $doStartScriptCode = ConvertTo-MooCodeListLiteral -Lines @(
        'callers() && raise(E_PERM);',
        'return eval(@args);'
    )
    Write-Host "Installing #0:do_start_script (owned by the connecting player; skips if it already exists)..." -ForegroundColor Cyan
    $r = Invoke-NativeEval -Stream $stream -Code @"
try
  verb_info(#0, "do_start_script");
  notify(player, "MOODEV_DO_START_SCRIPT_EXISTS_SKIPPED");
except (E_VERBNF)
  add_verb(#0, {player, "rxd", "do_start_script"}, {"this", "none", "this"});
  set_verb_code(#0, "do_start_script", $doStartScriptCode);
  notify(player, "MOODEV_DO_START_SCRIPT_ADDED");
endtry
"@
    if ($r -match 'MOODEV_DO_START_SCRIPT_ADDED') { Write-Host "  added" -ForegroundColor Green }
    elseif ($r -match 'MOODEV_DO_START_SCRIPT_EXISTS_SKIPPED') { Write-Host "  already existed, left untouched" -ForegroundColor Yellow }
    else { Write-Host "  unexpected response:`n$r" -ForegroundColor Red }

    $lspBridgeDone = $false
    if ($LspBridgePort) {
        Write-Host "Checking if something's already listening on -LspBridgePort $LspBridgePort..." -ForegroundColor Cyan
        $checkCode = "found = 0; for l in (listeners()) if (l[`"port`"] == $LspBridgePort) found = l[`"object`"]; endif endfor notify(player, `"MOODEV_LSP_PORT_CHECK `" + toliteral(found));"
        $r = Invoke-NativeEval -Stream $stream -Code $checkCode

        if ($r -match 'MOODEV_LSP_PORT_CHECK (#-?\d+|0)') {
            $existing = $Matches[1]
        } else {
            $existing = $null
        }

        if ($existing -and $existing -ne '0') {
            Write-Host "  already bound to $existing, left untouched" -ForegroundColor Yellow
            $lspBridgeDone = $true
        } elseif ($existing -eq $null) {
            Write-Host "  unexpected response, skipping LSP bridge setup:`n$r" -ForegroundColor Red
        } else {
            Write-Host "Creating LSP bridge service character + listener on port $LspBridgePort..." -ForegroundColor Cyan
            $lspDoCommandCode = ConvertTo-MooCodeListLiteral -Lines @(
                'if (length(argstr) >= 2 && argstr[1..2] == ";;")',
                '  result = eval(argstr[3..$]);',
                '  if (!result[1])',
                '    notify(player, "EVAL ERROR: " + toliteral(result[2]));',
                '  endif',
                '  return 1;',
                'endif',
                'return 0;'
            )
            $r = Invoke-NativeEval -Stream $stream -Code @"
svc = create(#-1);
svc.wizard = 1;
svc.programmer = 1;
set_player_flag(svc, 1);
lst = create(#-1);
lst.wizard = 1;
lst.programmer = 1;
add_verb(lst, {lst, "rxd", "do_login_command"}, {"none", "none", "none"});
e1 = set_verb_code(lst, "do_login_command", {"return " + tostr(svc) + ";"});
add_verb(lst, {lst, "rxd", "do_command"}, {"none", "none", "none"});
e2 = set_verb_code(lst, "do_command", $lspDoCommandCode);
r = listen(lst, $LspBridgePort);
notify(player, "MOODEV_LSP_BRIDGE_RESULT svc=" + tostr(svc) + " lst=" + tostr(lst) + " e1=" + toliteral(e1) + " e2=" + toliteral(e2));
"@
            if ($r -match 'MOODEV_LSP_BRIDGE_RESULT svc=(#\d+) lst=(#\d+) e1=\{\} e2=\{\}') {
                Write-Host "  created: service character $($Matches[1]), listener $($Matches[2]) on port $LspBridgePort" -ForegroundColor Green
                $lspBridgeDone = $true
            } else {
                Write-Host "  unexpected response (compile error or listen() failure), left as-is:`n$r" -ForegroundColor Red
            }
        }
    }

    Write-Host ""
    Write-Host "Done. This world can now be reached via the ';;eval' shim the Sidecar depends on." -ForegroundColor Green
    Write-Host "Next: use the web app's Settings panel (switch-target) to point MOOdy at" -ForegroundColor Green
    Write-Host "$HostName`:$Port - it will re-export the tree and pick up Tasks/Server status" -ForegroundColor Green
    Write-Host "automatically." -ForegroundColor Green
    if ($LspBridgePort -and $lspBridgeDone) {
        Write-Host "Set the Settings panel's LSP bridge port to $LspBridgePort as well - hover/" -ForegroundColor Green
        Write-Host "go-to-definition should then work too." -ForegroundColor Green
    } elseif ($LspBridgePort) {
        Write-Host "LSP bridge setup did not complete - hover/go-to-definition won't work until" -ForegroundColor Yellow
        Write-Host "that's sorted out (see the message above)." -ForegroundColor Yellow
    } else {
        Write-Host "-LspBridgePort wasn't given, so hover/go-to-definition won't work yet - rerun" -ForegroundColor Yellow
        Write-Host "with -LspBridgePort <port> to also set that up." -ForegroundColor Yellow
    }
} finally {
    $client.Close()
}
