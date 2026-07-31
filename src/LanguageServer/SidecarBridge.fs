/// The LSP's live connection to the Sidecar's `/lsp-bridge` endpoint (see
/// that endpoint's own doc comment in `Sidecar/LspBridge.fs` for the wire
/// protocol and why this is a genuinely separate connection from the
/// browser's own, never colliding with it). Replaces this project's
/// previous static-only sourcing of builtin docs (`builtins.json`) and verb-
/// dispatch resolution (`Metadata.Resolver.findCallableVerb` against the
/// once-loaded export-tree `Graph`) with live queries, so both are never
/// stale and both now also work for live-only (non-corponym'd) objects the
/// static graph never knew about.
///
/// The LSP is a WebSocket *client* here (the reverse of its own `/lsp`
/// endpoint, where it's the server) - it connects out to the Sidecar once
/// lazily on first use and stays connected, reconnecting on demand if the
/// Sidecar isn't up yet or the connection drops. A `None` result from either
/// public function means "no live answer available right now" (Sidecar
/// unreachable, or the MOO itself doesn't have this), which callers
/// (`Handlers.fs`) treat the same way a `graph.Objects`/`graph.Builtins`
/// miss used to be treated - no hover/definition, not a crash.
module LanguageServer.SidecarBridge

open System
open System.Collections.Concurrent
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Metadata.Schema

type VerbDispatchResult =
    { Definer: ObjRef
      /// Live `.name` of `Definer`, or `""` - a simpler display than the
      /// static path's `displayNameFor` (no `[$corponym]` suffix, since that
      /// needs the corponym registry too - a known, deliberate gap for this
      /// first live version, not a bug).
      DefinerName: string
      Names: string
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string
      Owner: ObjRef
      OwnerName: string
      /// `verb_code(definer, verbName, 0, 1)` output, one MOO source line
      /// per element - fetched alongside the rest of this record so hover
      /// can lex/parse it for the leading doc comment and an auto-inferred
      /// summary (`Handlers.hoverForResolvedVerbLive`) without a second
      /// round trip.
      Code: string list }

/// One connection's worth of mutable state - a fresh one is created each
/// time `ensureConnected` (re)connects, so a dropped/failed connection never
/// leaves stale waiters behind for the next attempt.
type private ConnectionState =
    { Socket: ClientWebSocket
      Waiters: ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>>
      Cts: CancellationTokenSource }

/// Not a class only because this module already needs top-level mutable
/// connection state (the `ClientWebSocket` itself, reconnected lazily) -
/// `SidecarBridge.create` below is the actual public constructor, this type
/// exists purely so `Handlers.fs` has something concrete to hold onto and
/// pass around instead of reaching into this module's private mutable
/// state directly.
type SidecarBridge =
    { ResolveVerbDispatch: ObjRef -> string -> Task<VerbDispatchResult option>
      GetBuiltins: unit -> Task<Map<string, BuiltinFunc>> }

let private bufferSize = 8192

/// Reads response frames off `state.Socket`, resolving whichever
/// `ResolveVerbDispatch`/`GetBuiltins` call is waiting on that response's
/// `id`. Runs for the connection's lifetime; when it exits (socket closed,
/// error, cancellation), every still-pending waiter is failed so no caller
/// hangs forever waiting on a connection that's gone.
let private readLoop (state: ConnectionState) : Task =
    task {
        let buffer = Array.zeroCreate<byte> bufferSize
        // A WebSocket message can span more than one `ReceiveAsync` call
        // (`EndOfMessage = false` until the last chunk) - `get-builtins`'s
        // full response (238 functions) is well over one `bufferSize`
        // chunk, so parsing off the first chunk alone silently threw on
        // truncated JSON (swallowed by the `with _ -> ()` below), leaving
        // the waiting caller hung forever with nothing to resolve or fail
        // it - confirmed live as an actual hover-request timeout before
        // this fix.
        let messageBuffer = ResizeArray<byte>()

        try
            let mutable finished = false

            while not finished do
                let! result = state.Socket.ReceiveAsync(ArraySegment(buffer), state.Cts.Token)

                match result.MessageType with
                | WebSocketMessageType.Close -> finished <- true
                | WebSocketMessageType.Text ->
                    messageBuffer.AddRange(buffer.[0 .. result.Count - 1])

                    if result.EndOfMessage then
                        let text = Encoding.UTF8.GetString(messageBuffer.ToArray())
                        messageBuffer.Clear()

                        try
                            let doc = JsonDocument.Parse(text)
                            let id = doc.RootElement.GetProperty("id").GetInt32()

                            match state.Waiters.TryRemove(id) with
                            | true, tcs -> tcs.TrySetResult(doc) |> ignore
                            | false, _ -> doc.Dispose() // no one waiting - drop it
                        with _ ->
                            ()
                | _ -> ()
        with _ ->
            ()

        for kvp in state.Waiters do
            kvp.Value.TrySetCanceled() |> ignore
    }

/// Creates a bridge that lazily connects to `wsUrl` on first use and
/// reconnects on demand whenever the current connection is missing/closed -
/// there is no persistent retry loop or background reconnect timer, since a
/// hover/go-to-definition request already only happens on user demand, so
/// "try to (re)connect right before this one request" is enough responsiveness.
let create (wsUrl: string) : SidecarBridge =
    let mutable current: ConnectionState option = None
    let connectLock = new SemaphoreSlim(1, 1)
    let mutable nextId = 0

    let ensureConnected () : Task<ConnectionState option> =
        task {
            match current with
            | Some state when state.Socket.State = WebSocketState.Open -> return Some state
            | _ ->
                do! connectLock.WaitAsync()

                try
                    // Re-check after acquiring the lock - another caller may
                    // have already reconnected while this one was waiting.
                    match current with
                    | Some state when state.Socket.State = WebSocketState.Open -> return Some state
                    | _ ->
                        let socket = new ClientWebSocket()

                        try
                            do! socket.ConnectAsync(Uri(wsUrl), CancellationToken.None)

                            let state =
                                { Socket = socket
                                  Waiters = ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>>()
                                  Cts = new CancellationTokenSource() }

                            current <- Some state
                            readLoop state |> ignore
                            return Some state
                        with _ ->
                            socket.Dispose()
                            current <- None
                            return None
                finally
                    connectLock.Release() |> ignore
        }

    let sendRequest (fields: (string * obj) list) : Task<JsonDocument option> =
        task {
            match! ensureConnected () with
            | None -> return None
            | Some state ->
                let id = Interlocked.Increment(&nextId)
                let tcs = TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously)
                state.Waiters.[id] <- tcs

                let payload = dict ((("id", box id)) :: fields)
                let json = JsonSerializer.Serialize(payload)
                let bytes = Encoding.UTF8.GetBytes(json)

                try
                    do! state.Socket.SendAsync(ArraySegment(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                    let! doc = tcs.Task
                    return Some doc
                with _ ->
                    state.Waiters.TryRemove(id) |> ignore
                    return None
        }

    let resolveVerbDispatch (startObj: ObjRef) (verbName: string) : Task<VerbDispatchResult option> =
        task {
            let! response =
                sendRequest [ "action", box "resolve-verb-dispatch"; "obj", box startObj; "verb", box verbName ]

            match response with
            | None -> return None
            | Some doc ->
                use doc = doc
                let root = doc.RootElement

                if root.GetProperty("found").GetBoolean() then
                    return
                        Some
                            { Definer = root.GetProperty("definer").GetInt64()
                              DefinerName = root.GetProperty("definerName").GetString()
                              Names = root.GetProperty("names").GetString()
                              Perms = root.GetProperty("perms").GetString()
                              Dobj = root.GetProperty("dobj").GetString()
                              Prep = root.GetProperty("prep").GetString()
                              Iobj = root.GetProperty("iobj").GetString()
                              Owner = root.GetProperty("owner").GetInt64()
                              OwnerName = root.GetProperty("ownerName").GetString()
                              Code =
                                root.GetProperty("code").EnumerateArray()
                                |> Seq.map (fun e -> e.GetString())
                                |> List.ofSeq }
                else
                    return None
        }

    // Builtins never change during a session (fixed by the server binary,
    // per `Exporter.getBuiltinFunctions`'s own doc comment) - fetched once
    // lazily on first request and cached forever after, not re-fetched per
    // hover/signature-help call.
    let mutable cachedBuiltins: Map<string, BuiltinFunc> option = None
    let builtinsLock = new SemaphoreSlim(1, 1)

    let getBuiltins () : Task<Map<string, BuiltinFunc>> =
        task {
            match cachedBuiltins with
            | Some m -> return m
            | None ->
                do! builtinsLock.WaitAsync()

                try
                    match cachedBuiltins with
                    | Some m -> return m
                    | None ->
                        let! response = sendRequest [ "action", box "get-builtins" ]

                        match response with
                        | None -> return Map.empty
                        | Some doc ->
                            use doc = doc
                            let paramNames = Metadata.Loader.loadBuiltinParamNames ()
                            let descriptions = Metadata.Loader.loadBuiltinDescriptions ()

                            let builtins =
                                doc.RootElement.GetProperty("functions").EnumerateArray()
                                |> Seq.map (fun el ->
                                    let b = Metadata.Loader.parseBuiltinFunc paramNames descriptions el
                                    b.Name, b)
                                |> Map.ofSeq

                            cachedBuiltins <- Some builtins
                            return builtins
                finally
                    builtinsLock.Release() |> ignore
        }

    { ResolveVerbDispatch = resolveVerbDispatch
      GetBuiltins = getBuiltins }
