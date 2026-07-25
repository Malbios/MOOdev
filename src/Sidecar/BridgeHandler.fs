/// Bridges one browser WebSocket connection to one MOO telnet TCP connection.
/// Zero MOO privilege here by design: this just pumps bytes for whichever
/// character logs in over that one connection, it never touches the MOO
/// itself out of band.
module Sidecar.BridgeHandler

open System
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

/// Pumps bytes from the MOO TCP connection to the browser WebSocket,
/// stripping telnet IAC negotiation and pulling out our own editor protocol
/// (McpFilter) along the way. Ordinary game text still goes out as Binary
/// frames exactly as before (MOO output isn't guaranteed valid UTF-8, and
/// binary frames avoid the stricter validation browsers/.NET apply to Text
/// frames) - a completed moodev-edit-* message goes out as a Text frame
/// (JSON) instead, so the browser can tell the two apart by frame type
/// without needing an envelope around the common case.
let private pumpTcpToWebSocket
    (stream: NetworkStream)
    (webSocket: WebSocket)
    (ct: CancellationToken)
    : Task =
    task {
        let buffer = Array.zeroCreate<byte> bufferSize
        let mutable telnetState = TelnetFilter.State.Data
        let mutable mcpState = McpFilter.initial
        let mutable finished = false

        while not finished do
            let! bytesRead = stream.ReadAsync(buffer, 0, buffer.Length, ct)

            if bytesRead = 0 then
                finished <- true
            else
                let chunk = buffer.[0 .. bytesRead - 1]
                let filtered, nextTelnetState = TelnetFilter.filterChunk telnetState chunk
                telnetState <- nextTelnetState

                let outputs, nextMcpState = McpFilter.filterChunk mcpState filtered
                mcpState <- nextMcpState

                for output in outputs do
                    if webSocket.State = WebSocketState.Open then
                        match output with
                        | McpFilter.PassThrough bytes when bytes.Length > 0 ->
                            do! webSocket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Binary, true, ct)
                        | McpFilter.PassThrough _ -> ()
                        | McpFilter.Emit msg ->
                            let json =
                                JsonSerializer.Serialize<McpWireMessage>({ header = msg.Header; lines = msg.Lines })

                            let jsonBytes = Encoding.UTF8.GetBytes(json)
                            do! webSocket.SendAsync(ArraySegment(jsonBytes), WebSocketMessageType.Text, true, ct)
    }

/// Pumps bytes from the browser WebSocket to the MOO TCP connection.
/// Whatever the browser sends (text or binary frame) is forwarded as-is,
/// with a conformant telnet line ending appended.
let private pumpWebSocketToTcp
    (webSocket: WebSocket)
    (stream: NetworkStream)
    (ct: CancellationToken)
    : Task =
    task {
        let buffer = Array.zeroCreate<byte> bufferSize
        let mutable finished = false

        while not finished do
            let! result = webSocket.ReceiveAsync(ArraySegment(buffer), ct)

            match result.MessageType with
            | WebSocketMessageType.Close -> finished <- true
            | _ ->
                if result.Count > 0 then
                    do! stream.WriteAsync(buffer, 0, result.Count, ct)

                let crlf = Encoding.ASCII.GetBytes("\r\n")
                do! stream.WriteAsync(crlf, 0, crlf.Length, ct)
    }

/// Owns the WebSocket and TcpClient for the lifetime of one bridged session.
/// Opens the MOO connection, pumps both directions concurrently, and tears
/// everything down as soon as either side closes.
let handleConnection
    (endpoint: MooEndpoint)
    (webSocket: WebSocket)
    (ct: CancellationToken)
    : Task =
    task {
        use tcpClient = new TcpClient()
        do! tcpClient.ConnectAsync(endpoint.Host, endpoint.Port, ct)
        use stream = tcpClient.GetStream()

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
                        do! pumpTcpToWebSocket stream webSocket cts.Token
                    with :? OperationCanceledException -> ()
                finally
                    cts.Cancel()
            }

        let wsToTcp =
            task {
                try
                    try
                        do! pumpWebSocketToTcp webSocket stream cts.Token
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
