/// `/lsp-bridge`: a dedicated WebSocket endpoint for the LanguageServer
/// process (not the browser) - see moo-vcs-plan.md and this session's
/// design notes for why. The LSP connects here as a WS *client* (the
/// reverse direction from `/ws`, which accepts the browser) once at its own
/// startup, and asks live queries (`get-builtins`, `resolve-verb-dispatch`)
/// instead of the LSP's own static, once-loaded export-tree graph.
///
/// This is a genuinely separate live MOO connection from any browser tab's
/// - opened here logged in as a dedicated, non-interactive service
/// character (MOOdy's CLAUDE.md documents the bootstrap: a world-specific
/// player object plus a second `listen()` port whose `do_login_command`
/// always returns that character, never `#3`/Wizard). Two *different*
/// player characters can be connected simultaneously with no collision -
/// ToastStunt only kicks a *repeated* login of the *same* character, which
/// this sidesteps entirely rather than needing any change to how the
/// browser's own `/ws` connections are opened.
module Sidecar.LspBridge

open System
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Sidecar.BridgeHandler

type MooEndpoint = { Host: string; Port: int }

let private bufferSize = 8192

/// Same tag-extraction convention `BridgeHandler`'s own (private) copy
/// implements, for the identical `#$#bridge-eval ref: <tag>` header shape
/// `evalOnSession` produces - duplicated rather than exposed cross-module,
/// matching this codebase's existing preference for small, bespoke
/// duplication over cross-cutting abstraction (see e.g. `getLiveChildren`/
/// `getLiveInfo`, which don't share a combinator either).
let private tagFromHeader (header: string) : int option =
    let marker = "ref: "
    let idx = header.IndexOf(marker)

    if idx < 0 then
        None
    else
        match Int32.TryParse(header.Substring(idx + marker.Length).Trim()) with
        | true, tag -> Some tag
        | false, _ -> None

/// Reads from the MOO connection, resolving `evalOnSession`'s tagged
/// `#$#bridge-eval` responses exactly as `BridgeHandler.pumpTcpToWebSocket`
/// does - but with nowhere to forward ordinary output *to* (there's no
/// browser on this connection's other end, and the service character never
/// produces any output beyond our own eval responses), so anything that
/// isn't a recognized `bridge-eval` reply is simply dropped.
let private pumpMooReads (session: Session) (ct: CancellationToken) : Task =
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
                    | McpFilter.Emit msg when msg.Header.StartsWith("bridge-eval") ->
                        match tagFromHeader msg.Header with
                        | Some tag ->
                            match session.Waiters.TryGetValue(tag) with
                            | true, tcs ->
                                let payload = msg.Lines |> List.tryHead |> Option.defaultValue "null"
                                tcs.TrySetResult(payload) |> ignore
                            | false, _ -> ()
                        | None -> ()
                    | _ -> ()
    }

/// One incoming LSP request, dispatched by its `action` field and answered
/// by echoing back the request's own `id` - the only correlation this layer
/// needs, since the request *may* run concurrently with others (unlike
/// `evalOnSession`'s own tag correlation one level down, which every
/// request here still goes through for its actual MOO round trip).
/// Malformed input (bad JSON, missing `id`/`action`) is silently dropped -
/// there's no `id` to answer with in that case, and this is a
/// programmatic-only connection with a single trusted caller (the LSP
/// process), not a boundary that needs defensive error reporting. Once
/// `id`/`action` are known, though, a failure during dispatch itself (e.g.
/// `handleGetBuiltins`'s live eval throwing) still sends an error response
/// rather than dropping silently - `id` exists to answer with here, and
/// silently dropping used to leave the LSP's own `SidecarBridge.sendRequest`
/// waiting the full request timeout for a reply that would never come,
/// turning one failed MOO eval into a several-second hang for whatever
/// hover/completion/etc. call triggered it.
let private handleRequest
    (session: Session)
    (sendResponse: string -> Task)
    (text: string)
    (ct: CancellationToken)
    : Task =
    task {
        try
            use doc = JsonDocument.Parse(text)
            let root = doc.RootElement
            let id = root.GetProperty("id").GetInt32()
            let action = root.GetProperty("action").GetString()

            let! responseJson =
                task {
                    try
                        match action with
                        | "get-builtins" -> return! LspBridgeActions.handleGetBuiltins id session ct
                        | "resolve-verb-dispatch" ->
                            let objRef = root.GetProperty("obj").GetInt64()
                            let verbName = root.GetProperty("verb").GetString()
                            return! LspBridgeActions.handleResolveVerbDispatch id session objRef verbName ct
                        | _ -> return JsonSerializer.Serialize({| id = id; error = "unknown action" |})
                    with ex ->
                        return JsonSerializer.Serialize({| id = id; error = ex.Message |})
                }

            do! sendResponse responseJson
        with _ ->
            ()
    }

/// Owns the WebSocket (the LSP process's own connection to us) and the
/// dedicated MOO TcpClient for this session's lifetime - mirrors
/// `BridgeHandler.handleConnection`'s shape, but simpler: no raw terminal
/// pass-through, no MCP-filtered browser forwarding, every message either
/// direction is a JSON request/response. Requests are handled concurrently
/// (fired off as they arrive, not awaited one at a time), each answered
/// whenever its own MOO round trip completes - `sendLock` only serializes
/// the WebSocket *sends* themselves (concurrent unsynchronized
/// `SendAsync` calls on one WebSocket are not safe), not the requests.
let handleConnection (endpoint: MooEndpoint) (webSocket: WebSocket) (ct: CancellationToken) : Task =
    task {
        use tcpClient = new TcpClient()

        // Same "MOO server down/unreachable is expected, not a bug" handling
        // as `BridgeHandler.handleConnection`.
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
            use sendLock = new SemaphoreSlim(1, 1)

            use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)

            let sendResponse (json: string) : Task =
                task {
                    do! sendLock.WaitAsync(cts.Token)

                    try
                        if webSocket.State = WebSocketState.Open then
                            let bytes = Encoding.UTF8.GetBytes(json)
                            do! webSocket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, cts.Token)
                    finally
                        sendLock.Release() |> ignore
                }

            let mooReadLoop =
                task {
                    try
                        try
                            do! pumpMooReads session cts.Token
                        with :? OperationCanceledException -> ()
                    finally
                        cts.Cancel()
                }

            let wsReadLoop =
                task {
                    try
                        try
                            let buffer = Array.zeroCreate<byte> bufferSize
                            // A WebSocket message can span more than one
                            // `ReceiveAsync` call (`EndOfMessage = false`
                            // until the last chunk) - `get-builtins`'s full
                            // response is well over one `bufferSize` chunk,
                            // so a request built directly off the first
                            // chunk alone would be truncated, invalid JSON.
                            let messageBuffer = ResizeArray<byte>()
                            let mutable finished = false

                            while not finished do
                                let! result = webSocket.ReceiveAsync(ArraySegment(buffer), cts.Token)

                                match result.MessageType with
                                | WebSocketMessageType.Close -> finished <- true
                                | WebSocketMessageType.Text ->
                                    messageBuffer.AddRange(buffer.[0 .. result.Count - 1])

                                    if result.EndOfMessage then
                                        let text = Encoding.UTF8.GetString(messageBuffer.ToArray())
                                        messageBuffer.Clear()
                                        handleRequest session sendResponse text cts.Token |> ignore
                                | _ -> ()
                        with :? OperationCanceledException -> ()
                    finally
                        cts.Cancel()
                }

            do! Task.WhenAny(mooReadLoop, wsReadLoop) :> Task

            if webSocket.State = WebSocketState.Open then
                do!
                    webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "LSP bridge closed",
                        CancellationToken.None
                    )
    }
