# Reusable MOO test client - avoids the WSL/nc/file round-trip for iterating
# on MOOcode. Talks raw TCP directly from PowerShell.

function Send-MooCommands {
    param(
        [string[]]$Commands,
        [string]$HostName = '127.0.0.1',
        [int]$Port = 7777,
        [int]$WaitMs = 2000
    )
    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($HostName, $Port)
    $stream = $client.GetStream()

    $allCommands = @('connect wizard') + $Commands
    foreach ($cmd in $allCommands) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($cmd + "`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        Start-Sleep -Milliseconds 150
    }

    Start-Sleep -Milliseconds $WaitMs

    $buffer = New-Object byte[] 65536
    $ms = New-Object System.IO.MemoryStream
    while ($stream.DataAvailable) {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { break }
        $ms.Write($buffer, 0, $read)
    }
    $client.Close()

    # Latin1: decodes any byte 1:1 without UTF-8 validation errors - good
    # enough for reading back diagnostic text, not meant to be "correct"
    # Unicode handling.
    return [System.Text.Encoding]::GetEncoding("ISO-8859-1").GetString($ms.ToArray())
}

function Connect-MooMcp {
    # Prepends the standard MCP 2.1 negotiation handshake (client greeting +
    # negotiate-can/-end for the packages we care about) ahead of $Commands,
    # then delegates to Send-MooCommands for the actual socket I/O - see
    # moo.mud.org/mcp/mcp2.html for the handshake shape. Needed before the
    # server will use MCP framing (e.g. the simpleedit verb editor) instead
    # of its plain-text fallback.
    param(
        [string[]]$Commands,
        [string]$HostName = '127.0.0.1',
        [int]$Port = 7777,
        [int]$WaitMs = 2000,
        [string]$AuthKey = 'moodev',
        [string[]]$Packages = @('dns-org-mud-moo-simpleedit')
    )
    $negotiate = @("#`$#mcp authentication-key: $AuthKey version: 2.1 to: 2.1")
    foreach ($pkg in $Packages) {
        $negotiate += "#`$#mcp-negotiate-can $AuthKey package: $pkg min-version: 1.0 max-version: 1.0"
    }
    $negotiate += "#`$#mcp-negotiate-end $AuthKey"

    return Send-MooCommands -Commands ($negotiate + $Commands) -HostName $HostName -Port $Port -WaitMs $WaitMs
}

function ConvertTo-MooCodeListLiteral {
    # Turns an array of MOOcode source lines into a MOO list-of-strings
    # literal suitable for set_verb_code()'s third argument, e.g.
    # {"line one;", "line two;"} - escaping backslashes and double quotes.
    param([string[]]$Lines)
    $escaped = $Lines | ForEach-Object {
        $l = $_ -replace '\\', '\\'
        $l = $l -replace '"', '\"'
        '"' + $l + '"'
    }
    return '{' + ($escaped -join ', ') + '}'
}
