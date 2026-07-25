/// Runs one LSP session over an already-accepted browser WebSocket.
/// `Server.startWs` takes a raw `WebSocket` directly - Ionide's library
/// handles the JSON-RPC framing over it internally, so there's no
/// stream-wrapping step to write here (the M4 plan's original assumption,
/// made before checking the library's real API, was wrong about needing
/// one).
module LanguageServer.WsTransport

open System.Net.WebSockets
open Ionide.LanguageServerProtocol
open StreamJsonRpc
open Metadata.Schema
open LanguageServer.Handlers

/// Blocks until the client disconnects or the socket closes.
let run (socket: WebSocket) (graph: Graph) : unit =
    let handlings =
        Server.defaultRequestHandlings ()
        |> Map.add "moodev/listObjects" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.ListObjects p))
        |> Map.add "moodev/listVerbs" (Server.serverRequestHandling (fun (s: MooLspServer) (p: ListVerbsParams) -> s.ListVerbs p))
        |> Map.add "moodev/getObjectInfo" (Server.serverRequestHandling (fun (s: MooLspServer) (p: GetObjectInfoParams) -> s.GetObjectInfo p))

    let clientCreator (_notify, _request) = new MooLspClient()
    let serverCreator (client: MooLspClient) = new MooLspServer(client, graph)
    // `JsonRpc(handler)` is StreamJsonRpc's own plain constructor - no
    // extra customization needed for this phase.
    let customizeRpc (handler: IJsonRpcMessageHandler) = new JsonRpc(handler)

    Server.startWs handlings socket clientCreator serverCreator customizeRpc
    |> ignore
