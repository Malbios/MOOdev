/// Wizard-connection eval client used by the Phase 1 exporter (and later
/// phases). Talks plain telnet to a MOO instance, logs in as the wizard, and
/// runs one MOO command at a time, reading back a `generate_json()`-rendered
/// value tagged with a sentinel prefix so it can be picked out of an
/// otherwise unstructured text stream. Deliberately does not depend on any
/// custom in-MOO verb (unlike the retired `$vcs`'s `ide_fetch`/`ide_save`) -
/// per moo-vcs-plan.md's invariant I1, this rides standard builtins only.
module Sidecar.MooEval

open System
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// One open, logged-in connection used for sequential eval/response round
/// trips. Not thread-safe and not meant for concurrent use - a batch
/// exporter issues one command, awaits its tagged response, then issues the
/// next.
type Connection =
    { Client: TcpClient
      Stream: NetworkStream
      mutable TelnetState: TelnetFilter.State
      mutable NextTagValue: int }

let private sentinel = "###MOOVCS"

/// Opens a TCP connection to the MOO and logs in as `username` - a bare
/// `connect <username>`, no password. Works unmodified against a bare
/// `Minimal.db` world (no real accounting - any text logs in as Wizard, see
/// MOOdy's CLAUDE.md) with the default `"wizard"`, and equally against a
/// real, separately-accounted world *if* the target account's password is
/// genuinely blank - confirmed live against a real HellMOO-derived world
/// where the wizard-equivalent character's own name (not literally
/// "wizard") had to be passed here instead; a world requiring a real,
/// non-blank password needs a different mechanism entirely (not just an
/// extra CLI argument), out of scope for this project's own single-developer
/// dev tooling.
let connect (host: string) (port: int) (username: string) (ct: CancellationToken) : Task<Connection> =
    task {
        let client = new TcpClient()
        do! client.ConnectAsync(host, port, ct)
        let stream = client.GetStream()
        let login = Encoding.UTF8.GetBytes("connect " + username + "\r\n")
        do! stream.WriteAsync(login, 0, login.Length, ct)

        return
            { Client = client
              Stream = stream
              TelnetState = TelnetFilter.State.Data
              NextTagValue = 0 }
    }

let disconnect (conn: Connection) =
    conn.Stream.Dispose()
    conn.Client.Dispose()

/// Allocates the next sentinel tag for this connection.
let nextTag (conn: Connection) : int =
    let tag = conn.NextTagValue
    conn.NextTagValue <- tag + 1
    tag

/// The exact literal prefix a MOO command must `notify()` back (immediately
/// followed by `generate_json()`'d output, no space) for `sendAndAwaitJson`
/// to recognize it as this tag's response.
let sentinelPrefix (tag: int) : string = sprintf "%s:%d:" sentinel tag

/// Reads bytes off the connection - stripping telnet IAC via `TelnetFilter`,
/// same as `BridgeHandler` already does for the browser-facing bridge -
/// until a complete line starting with `prefix` has been seen, returning the
/// text after that prefix. Any other complete line encountered first
/// (login banner, unrelated notifications) is silently discarded; the tag
/// is what makes this safe even though nothing else should be writing to
/// this connection.
let private readTaggedLine (conn: Connection) (prefix: string) (ct: CancellationToken) : Task<string> =
    task {
        let buffer = Array.zeroCreate<byte> 8192
        let lineBuf = StringBuilder()
        let mutable found: string option = None

        while found.IsNone do
            let! bytesRead = conn.Stream.ReadAsync(buffer, 0, buffer.Length, ct)

            if bytesRead = 0 then
                failwith "MOO connection closed while waiting for an eval response"

            let chunk = buffer.[0 .. bytesRead - 1]
            let filtered, nextState = TelnetFilter.filterChunk conn.TelnetState chunk
            conn.TelnetState <- nextState

            for b in filtered do
                if b = byte '\n' then
                    let line = lineBuf.ToString().TrimEnd('\r')
                    lineBuf.Clear() |> ignore

                    if found.IsNone && line.StartsWith(prefix: string) then
                        found <- Some(line.Substring(prefix.Length))
                else
                    lineBuf.Append(char b) |> ignore

        return found.Value
    }

/// Sends a full MOO command (the body of a `;`-eval, without the leading
/// `;`) and waits for the line the command itself is expected to
/// `notify()` back starting with `sentinelPrefix tag`, returning it
/// JSON-decoded. The caller is responsible for making its MOO code actually
/// notify() that exact prefix immediately followed by `generate_json(...)`
/// output - see `evalExpr` for the common single-expression case, or build a
/// multi-statement body directly for anything that needs local variables.
///
/// `mooCommandBody` may be written across multiple lines for readability,
/// but any embedded newlines are collapsed to a single space before
/// sending: this connection is a single-line-per-command telnet protocol
/// (the same reason `moo-client.ps1`'s `ConvertTo-MooCodeListLiteral` builds
/// one-line list literals rather than sending raw multi-line source) - a
/// literal `\n` in the middle of a command would be read by the server as a
/// second, separate (and incomplete) command.
///
/// Sent as `";; " + mooCommandBody`, not `"; " + mooCommandBody` - the extra
/// leading `;` is the documented "double-semicolon" idiom that defeats
/// ToastCore's `;` eval command (`#58:eval_cmd_string`) auto-prepending
/// `return ` to any program that doesn't already start with `;`/`if`/`fork`/
/// `return`/`while`/`try`. Without it, a multi-statement body like
/// `corps = []; for ... notify(...)` would silently execute only its first
/// statement - the `return` fires after `corps = []`, and the `for` loop and
/// `notify()` call this function is waiting on never run, hanging forever.
/// Confirmed against `moocode-reference.md`'s documented quirk and by
/// hitting exactly this hang live against a real instance.
let sendAndAwaitJson
    (conn: Connection)
    (tag: int)
    (mooCommandBody: string)
    (ct: CancellationToken)
    : Task<JsonDocument> =
    task {
        let oneLine = mooCommandBody.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ")
        let bytes = Encoding.UTF8.GetBytes(";; " + oneLine + "\r\n")
        do! conn.Stream.WriteAsync(bytes, 0, bytes.Length, ct)
        let! json = readTaggedLine conn (sentinelPrefix tag) ct
        return JsonDocument.Parse(json)
    }

/// Convenience for the common case: `mooExpr` is a single MOO expression
/// whose value should come back as JSON. Wrapped in a try/except so a
/// MOO-level error (E_PROPNF, etc.) still produces valid, correctly-tagged
/// JSON - `{"error": "E_..."}` - instead of an unmatched plain-text error
/// line that would leave the caller waiting forever.
let evalExpr (conn: Connection) (mooExpr: string) (ct: CancellationToken) : Task<JsonDocument> =
    let tag = nextTag conn
    let prefix = sentinelPrefix tag

    let body =
        $"""notify(player, "{prefix}" + (`generate_json({mooExpr}) ! ANY => generate_json(["error" -> tostr(error_code)])'));"""

    sendAndAwaitJson conn tag body ct

/// General-purpose version of `evalExpr` for a full MOO statement sequence
/// that does not itself notify() anything: `statements` runs as-is, then
/// `resultExpr` is evaluated and notify()'d back, all under ONE allocated
/// tag. Exists specifically so callers never hand-roll tag/prefix
/// bookkeeping themselves - allocating a tag once for embedding the
/// sentinel text and again for the awaited response would silently produce
/// two different tags and hang forever waiting for a response that never
/// arrives under the tag actually awaited.
let runAndAwaitJson
    (conn: Connection)
    (statements: string)
    (resultExpr: string)
    (ct: CancellationToken)
    : Task<JsonDocument> =
    let tag = nextTag conn
    let prefix = sentinelPrefix tag

    let body =
        $"""{statements} notify(player, "{prefix}" + (`generate_json({resultExpr}) ! ANY => generate_json(["error" -> tostr(error_code)])'));"""

    sendAndAwaitJson conn tag body ct
