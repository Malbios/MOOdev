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
let run (socket: WebSocket) (graph: Graph) (bridge: SidecarBridge.SidecarBridge) : unit =
    let handlings =
        Server.defaultRequestHandlings ()
        |> Map.add "moodev/getObjectTree" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.GetObjectTree p))
        |> Map.add "moodev/findDeadVerbs" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.FindDeadVerbs p))
        |> Map.add "moodev/findDeadProperties" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.FindDeadProperties p))
        |> Map.add "moodev/prepareRename" (Server.serverRequestHandling (fun (s: MooLspServer) (p: PrepareRenameParams) -> s.PrepareRename p))
        |> Map.add
            "moodev/findReferencesToObject"
            (Server.serverRequestHandling (fun (s: MooLspServer) (p: FindReferencesToObjectParams) -> s.FindReferencesToObject p))
        |> Map.add "moodev/findGotchas" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.FindGotchas p))
        |> Map.add "moodev/getMoocodeDocs" (Server.serverRequestHandling (fun (s: MooLspServer) (p: obj) -> s.GetMoocodeDocs p))
        |> Map.add "moodev/reloadGraph" (Server.serverRequestHandling (fun (s: MooLspServer) (p: ReloadGraphParams) -> s.ReloadGraph p))

    let clientCreator (_notify, _request) = new MooLspClient()
    let serverCreator (client: MooLspClient) = new MooLspServer(client, graph, bridge)
    // `JsonRpc(handler)` is StreamJsonRpc's own plain constructor - no
    // extra customization needed for this phase.
    let customizeRpc (handler: IJsonRpcMessageHandler) = new JsonRpc(handler)

    Server.startWs handlings socket clientCreator serverCreator customizeRpc
    |> ignore
