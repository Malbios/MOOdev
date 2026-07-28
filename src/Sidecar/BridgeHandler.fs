/// Bridges one browser WebSocket connection to one MOO telnet TCP
/// connection. Zero MOO privilege here by design: this pumps bytes for
/// whichever character logs in over that one connection - it never touches
/// the MOO out of band, except for the Phase 4 eval channel below, which
/// still rides this SAME connection (so `player` is whichever character is
/// actually logged into this browser tab), never a separate wizard
/// connection like the batch `Exporter`/`Importer` tools use.
module Sidecar.BridgeHandler

open System
open System.Collections.Concurrent
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type MooEndpoint = { Host: string; Port: int }

let private bufferSize = 8192

/// Wire shape sent to the browser for a completed moodev-edit-* message -
/// see McpFilter for how it's assembled out of the raw #$# lines. Not
/// `private`: System.Text.Json's reflection-based serializer silently
/// produces an empty `{}` for non-public types instead of throwing
/// (verified live - this cost real debugging time), so it has to be public.
type McpWireMessage = { header: string; lines: string list }

/// Per-connection state shared between the read pump (which owns the only
/// `ReadAsync` loop on this session's `NetworkStream`) and `evalOnSession`
/// (Phase 4's sidecar-issued eval channel, reused by `IdeActions`). A
/// second independent reader on the same stream (e.g. `MooEval`'s own
/// read loop, as-is) would race unpredictably for incoming bytes - so an
/// eval's response is instead recognized by the read pump itself (a
/// `#$#bridge-eval ref: <tag>` message, McpFilter-recognized the same way
/// as `moodev-*`) and routed to whichever caller is waiting on that tag,
/// rather than forwarded to the browser. `WriteLock` serializes writes to
/// the stream between forwarded browser/terminal bytes and sidecar-issued
/// eval commands - concurrent unsynchronized writes could interleave their
/// bytes mid-line, corrupting both.
type Session =
    { Stream: NetworkStream
      WriteLock: SemaphoreSlim
      Waiters: ConcurrentDictionary<int, TaskCompletionSource<string>>
      mutable NextTag: int }

let newSession (stream: NetworkStream) : Session =
    { Stream = stream
      WriteLock = new SemaphoreSlim(1, 1)
      Waiters = ConcurrentDictionary<int, TaskCompletionSource<string>>()
      NextTag = 0 }

let private nextTag (session: Session) : int =
    let tag = session.NextTag
    session.NextTag <- tag + 1
    tag

/// Writes raw bytes to the MOO connection, serialized against any other
/// writer via `WriteLock`.
let private writeBytesLocked (session: Session) (bytes: byte[]) (ct: CancellationToken) : Task =
    task {
        do! session.WriteLock.WaitAsync(ct)

        try
            do! session.Stream.WriteAsync(bytes, 0, bytes.Length, ct)
        finally
            session.WriteLock.Release() |> ignore
    }

let private writeLine (session: Session) (text: string) (ct: CancellationToken) : Task =
    writeBytesLocked session (Encoding.UTF8.GetBytes(text + "\r\n")) ct

/// Parses the tag out of a `bridge-eval`-recognized `McpMessage`'s
/// `Header` (shaped "bridge-eval ref: <tag>" by `evalOnSession` below) -
/// `McpMessage` itself only carries `Header`/`Lines` once emitted, not the
/// tag `McpFilter` used internally to correlate continuation lines.
let private tagFromHeader (header: string) : int option =
    let marker = "ref: "
    let idx = header.IndexOf(marker)

    if idx < 0 then
        None
    else
        match Int32.TryParse(header.Substring(idx + marker.Length).Trim()) with
        | true, tag -> Some tag
        | false, _ -> None

/// Sends a MOO command over this session's own live connection - so
/// `player` is whichever character is actually logged into this browser
/// tab, not a separate wizard connection - and awaits its `#$#bridge-eval`
/// response, JSON-decoded. `statements` may set up local variables (e.g. a
/// `for` loop resolving a verb name to its `verbs()` index - a single
/// expression can't express that); `resultExpr` is the one expression whose
/// value is reported back. Mirrors `MooEval.runAndAwaitJson`'s exact
/// two-part contract and its double-semicolon-safe, try/except-wrapped
/// convention (`;;` defeats ToastCore's `;` eval command auto-prepending
/// `return` to anything not already starting with a recognized keyword -
/// see `MooEval.sendAndAwaitJson`'s own comment for the full story and the
/// real hang it caused before that fix - the exact same hang risk applies
/// here if this wrapping is ever skipped).
let evalOnSession
    (session: Session)
    (statements: string)
    (resultExpr: string)
    (ct: CancellationToken)
    : Task<JsonDocument> =
    task {
        let tag = nextTag session
        let tcs = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        session.Waiters.[tag] <- tcs

        use _reg = ct.Register(fun () -> tcs.TrySetCanceled() |> ignore)

        let fullStatements =
            $"""{statements} jsonval = (`generate_json({resultExpr}) ! ANY => generate_json(["error" -> tostr(error_code)])'); notify(player, "#$#bridge-eval ref: {tag}"); notify(player, "#$#* {tag} text: " + jsonval); notify(player, "#$#: {tag}");"""

        let oneLine = fullStatements.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ")

        try
            do! writeLine session (";; " + oneLine) ct
            let! json = tcs.Task
            return JsonDocument.Parse(json)
        finally
            session.Waiters.TryRemove(tag) |> ignore
    }

/// Pumps bytes from the MOO TCP connection to the browser WebSocket,
/// stripping telnet IAC negotiation and pulling out our own editor protocol
/// (McpFilter) along the way. Ordinary game text still goes out as Binary
/// frames exactly as before - a completed moodev-edit-* message goes out as
/// a Text frame (JSON) instead. A completed `bridge-eval` message (Phase
/// 4's own sidecar-issued eval responses) is neither - it's routed to
/// whichever `evalOnSession` call is waiting on that tag and never reaches
/// the browser at all.
let private pumpTcpToWebSocket (session: Session) (webSocket: WebSocket) (ct: CancellationToken) : Task =
    task {
        let buffer = Array.zeroCreate<byte> bufferSize
        let mutable telnetState = TelnetFilter.State.Data
        let mutable mcpState = McpFilter.initial
        let mutable finished = false

        while not finished do
            let! bytesRead = session.Stream.ReadAsync(buffer, 0, buffer.Length, ct)

            if bytesRead = 0 then
                finished <- true
            else
                let chunk = buffer.[0 .. bytesRead - 1]
                let filtered, nextTelnetState = TelnetFilter.filterChunk telnetState chunk
                telnetState <- nextTelnetState

                let outputs, nextMcpState = McpFilter.filterChunk mcpState filtered
                mcpState <- nextMcpState

                for output in outputs do
                    match output with
                    | McpFilter.PassThrough bytes when bytes.Length > 0 ->
                        if webSocket.State = WebSocketState.Open then
                            do! webSocket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Binary, true, ct)
                    | McpFilter.PassThrough _ -> ()
                    | McpFilter.Emit msg when msg.Header.StartsWith("bridge-eval") ->
                        match tagFromHeader msg.Header with
                        | Some tag ->
                            match session.Waiters.TryGetValue(tag) with
                            | true, tcs ->
                                let payload = msg.Lines |> List.tryHead |> Option.defaultValue "null"
                                tcs.TrySetResult(payload) |> ignore
                            | false, _ -> () // no one waiting (e.g. already timed out/cancelled) - drop it
                        | None -> ()
                    | McpFilter.Emit msg ->
                        if webSocket.State = WebSocketState.Open then
                            let json =
                                JsonSerializer.Serialize<McpWireMessage>({ header = msg.Header; lines = msg.Lines })

                            let jsonBytes = Encoding.UTF8.GetBytes(json)
                            do! webSocket.SendAsync(ArraySegment(jsonBytes), WebSocketMessageType.Text, true, ct)
    }

/// Pumps bytes from the browser WebSocket to the MOO TCP connection.
/// Whatever the browser sends (text or binary frame) is forwarded as-is,
/// with a conformant telnet line ending appended - writes go through the
/// same `WriteLock` `evalOnSession` uses, so a forwarded browser command
/// can't interleave with a sidecar-issued eval command on the wire.
/// `tryDispatch` gets first look at every WebSocket **Text** frame's raw
/// string: Phase 4's structured IDE actions (`{"action": "fetch-verb", ...}`
/// etc.) are recognized and handled here, returning `true`; anything else -
/// parse failure, or no recognized `action` field, which covers every
/// ordinary typed command since MOO command text isn't valid JSON - falls
/// through to the raw-forward behavior this bridge has always had. Binary
/// frames never go through `tryDispatch` at all (nothing sends structured
/// actions as binary). Injected by the caller (`Program.fs`, which can see
/// both `BridgeHandler` and `IdeActions`) rather than referenced directly
/// here, to avoid a circular module dependency (`IdeActions` needs
/// `Session`/`evalOnSession` from this module).
let private pumpWebSocketToTcp
    (session: Session)
    (webSocket: WebSocket)
    (tryDispatch: string -> CancellationToken -> Task<bool>)
    (ct: CancellationToken)
    : Task =
    task {
        let buffer = Array.zeroCreate<byte> bufferSize
        let mutable finished = false

        while not finished do
            let! result = webSocket.ReceiveAsync(ArraySegment(buffer), ct)

            match result.MessageType with
            | WebSocketMessageType.Close -> finished <- true
            | WebSocketMessageType.Text ->
                let text = Encoding.UTF8.GetString(buffer, 0, result.Count)
                let! handled = tryDispatch text ct

                if not handled then
                    let bytes = Encoding.UTF8.GetBytes(text + "\r\n")
                    do! writeBytesLocked session bytes ct
            | _ ->
                let crlf = Encoding.ASCII.GetBytes("\r\n")

                let combined =
                    if result.Count > 0 then
                        Array.append buffer.[0 .. result.Count - 1] crlf
                    else
                        crlf

                do! writeBytesLocked session combined ct
    }

/// Owns the WebSocket and TcpClient for the lifetime of one bridged session.
/// Opens the MOO connection, pumps both directions concurrently, and tears
/// everything down as soon as either side closes. `tryDispatch` - see
/// `pumpWebSocketToTcp`'s own comment - is how Phase 4's IDE actions get
/// wired in without this module needing to know `IdeActions` exists.
let handleConnection
    (endpoint: MooEndpoint)
    (webSocket: WebSocket)
    (tryDispatch: Session -> WebSocket -> string -> CancellationToken -> Task<bool>)
    (ct: CancellationToken)
    : Task =
    task {
        use tcpClient = new TcpClient()

        // The MOO server being down/unreachable when a browser connects is a
        // real, expected outcome (not a bug here) - e.g. mid-restart, like
        // the connection-refused case this was written for. Caught
        // specifically (not a catch-all) so the websocket closes with a
        // clear reason instead of the exception surfacing as an unhandled
        // failure in Kestrel's own request-pipeline logs.
        let! connected =
            task {
                try
                    do! tcpClient.ConnectAsync(endpoint.Host, endpoint.Port, ct)
                    return true
                with :? SocketException ->
                    return false
            }

        if not connected then
            if webSocket.State = WebSocketState.Open then
                do!
                    webSocket.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Could not connect to the MOO server",
                        CancellationToken.None
                    )
        else
            use stream = tcpClient.GetStream()
            let session = newSession stream

            use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)

            // When either pump finishes (cleanly or otherwise), cts.Cancel() stops
            // the other one. That cancellation surfaces as OperationCanceledException
            // on the ReadAsync/ReceiveAsync call inside it - expected shutdown, not a
            // real failure, so it's swallowed here rather than left as an unobserved
            // faulted task.
            let tcpToWs =
                task {
                    try
                        try
                            do! pumpTcpToWebSocket session webSocket cts.Token
                        with :? OperationCanceledException -> ()
                    finally
                        cts.Cancel()
                }

            let wsToTcp =
                task {
                    try
                        try
                            do! pumpWebSocketToTcp session webSocket (tryDispatch session webSocket) cts.Token
                        with :? OperationCanceledException -> ()
                    finally
                        cts.Cancel()
                }

            do! Task.WhenAny(tcpToWs, wsToTcp) :> Task

            if webSocket.State = WebSocketState.Open then
                do!
                    webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "MOO connection closed",
                        CancellationToken.None
                    )
    }
