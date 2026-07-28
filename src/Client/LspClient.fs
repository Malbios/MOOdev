/// A small hand-written JSON-RPC-over-websocket client wired straight into
/// Monaco's native `registerHoverProvider`/`registerDefinitionProvider`/
/// `registerEditorOpener` APIs - see the M4 plan's Phase 4.5 notes for why
/// this isn't `monaco-languageclient`: that library's job is making
/// *someone else's* pre-built LSP server work in Monaco without a custom
/// client, which needs full VS Code API emulation to do generically. We
/// wrote our own server this session, so both ends are already ours - no
/// generic compatibility layer is needed, just enough glue for the two
/// request types Phase 4.4a's server actually answers (hover, definition).
///
/// A second, independent websocket straight to the `LanguageServer`
/// process - not routed through the Sidecar, keeping "zero MOO privilege in
/// the Sidecar" and "the LSP needs no live MOO connection" both true.
module Client.LspClient

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types

let private lspWsUrl: string = emitJsExpr () "import.meta.env.VITE_LSP_WS_URL"

let private ws = WebSocket.Create(lspWsUrl)
let mutable private nextId = 1
let private pending = Dictionary<int, obj -> unit>()
let mutable private isReady = false
let private readyWaiters = ResizeArray<unit -> unit>()

let private send (message: obj) : unit = ws.send (JS.JSON.stringify message)

/// Resolves once `initialize`/`initialized` has completed - awaited by
/// every hover/definition request before it sends anything, same ordering
/// a real LSP client observes.
let private waitForReady () : Async<unit> =
    Async.FromContinuations(fun (resolve, _, _) -> if isReady then resolve () else readyWaiters.Add(fun () -> resolve ()))

/// Sends a request immediately, with no readiness gate - only the
/// bootstrap `initialize` call (below) should ever use this directly.
/// Everything else goes through `requestAsync`, which waits for
/// `isReady`.
let private rawRequestAsync (methodName: string) (parameters: obj) : Async<obj> =
    Async.FromContinuations(fun (resolve, _, _) ->
        let id = nextId
        nextId <- nextId + 1
        pending.[id] <- resolve

        send (
            createObj
                [ "jsonrpc" ==> "2.0"
                  "id" ==> id
                  "method" ==> methodName
                  "params" ==> parameters ]
        ))

let private requestAsync (methodName: string) (parameters: obj) : Async<obj> =
    async {
        do! waitForReady ()
        return! rawRequestAsync methodName parameters
    }

let private notify (methodName: string) (parameters: obj) : unit =
    send (createObj [ "jsonrpc" ==> "2.0"; "method" ==> methodName; "params" ==> parameters ])

ws.onopen <-
    fun _ ->
        async {
            // Must bypass `requestAsync`'s readiness gate here specifically -
            // this *is* the call that makes the connection ready, so gating
            // it on `isReady` would deadlock it against itself (confirmed:
            // this was the actual bug behind every hover/definition/etc.
            // request hanging as "Loading" forever - the initialize request
            // was never even being sent).
            do! rawRequestAsync "initialize" (createObj [ "processId" ==> None; "rootUri" ==> None; "capabilities" ==> createObj [] ]) |> Async.Ignore
            notify "initialized" (createObj [])
            isReady <- true

            for waiter in readyWaiters do
                waiter ()

            readyWaiters.Clear()
        }
        |> Async.StartImmediate

ws.onmessage <-
    fun ev ->
        let msg: obj = JS.JSON.parse (unbox ev.data)
        let id: obj = msg?id

        if not (isNullOrUndefined id) && pending.ContainsKey(unbox id) then
            let resolve = pending.[unbox id]
            pending.Remove(unbox id) |> ignore
            resolve msg?result

/// `moodev-verb://<objRef>/<verbName>` - mirrors `Handlers.moodevVerbUri` on
/// the server exactly (the browser never has a real filesystem path; object
/// # + verb name is all it ever knows, the same pair `$vcs:ide_fetch`/
/// `ide_save` already key off of).
let private documentUri (objRef: int64) (verbName: string) : string =
    sprintf "moodev-verb://%d/%s" objRef (System.Uri.EscapeDataString verbName)

let private textDocumentPositionParams (objRef: int64) (verbName: string) (lspLine: int) (lspCharacter: int) : obj =
    createObj
        [ "textDocument" ==> createObj [ "uri" ==> documentUri objRef verbName ]
          "position" ==> createObj [ "line" ==> lspLine; "character" ==> lspCharacter ] ]

/// The wire `CompletionItem.kind` is the *LSP spec's* numeric encoding
/// (`Method`=2, `Function`=3, `Variable`=6 - `Handlers.fs`'s
/// `mkCompletionItem` sets only these three). Monaco's own
/// `CompletionItemKind` enum uses a completely different, older numbering
/// of its own (`Method`=0, `Function`=1, `Variable`=4) predating any LSP
/// alignment - confirmed by reading Monaco's own `.d.ts` rather than
/// assuming the two line up. Falls back to `Text`=18 for anything else.
let private monacoCompletionKind (lspKind: int) : int =
    match lspKind with
    | 2 -> 0 // Method
    | 3 -> 1 // Function
    | 6 -> 4 // Variable
    | _ -> 18 // Text

/// Structural summary of one verb for the tree's compact perms/args
/// suffix (matches `Handlers.ObjectTreeVerb`).
type TreeVerb =
    { Name: string
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string }

/// Same idea as `TreeVerb`, for properties (matches `Handlers.ObjectTreeProperty`).
type TreeProperty = { Name: string; Perms: string }

/// Custom method (not part of the LSP spec) - one shot at login: the whole
/// object universe (not just verb-owners), with parent/child edges and
/// each object's own verb/property summaries folded in (matches
/// `Handlers.MooLspServer.GetObjectTree`), so the sidebar tree never needs
/// a per-click round trip to fetch a newly-expanded object's verbs or
/// properties.
///
/// Every ref here is read as `float` and explicitly converted via
/// `int64 (...)`, never a bare `?field: int64` cast - a JSON-RPC ref is a
/// plain JS number, not Fable's actual `int64` (a native `BigInt`), and a
/// bare dynamic cast silently produces a value that looks right but fails
/// `Map`/`Set` membership against genuine `int64`s built elsewhere (same
/// class of bug `renderInspectorStructure`'s `ownerRef`/`toRefList` already
/// hit and fixed for the inspector's parent/child refs - confirmed live
/// there as a real "duplicate tab instead of switching to the open one"
/// symptom, not a hypothetical).
let getObjectTreeAsync () : Async<(int64 * string * int64[] * int64[] * TreeVerb[] * TreeProperty[])[]> =
    async {
        let! result = requestAsync "moodev/getObjectTree" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result

            return
                items
                |> Array.map (fun o ->
                    int64 (o?objRef: float),
                    (o?name: string),
                    ((o?parents: float[]) |> Array.map int64),
                    ((o?children: float[]) |> Array.map int64),
                    ((o?verbs: obj[])
                     |> Array.map (fun v ->
                         { Name = v?name; Perms = v?perms; Dobj = v?dobj; Prep = v?prep; Iobj = v?iobj }: TreeVerb)),
                    // `properties` is missing entirely from an old, not-yet-rebuilt
                    // LSP server's response (server/client skew during dev) - degrade
                    // to an empty array rather than letting `undefined` flow into
                    // `TreeNode.Properties` and crash the first `Array.isEmpty` on it.
                    (if isNullOrUndefined o?properties then
                         [||]
                     else
                         (o?properties: obj[]) |> Array.map (fun p -> { Name = p?name; Perms = p?perms }: TreeProperty)))
    }

/// Custom method - the object inspector's structural data (owner, flags,
/// parents/children, verbs, properties) for `objRef` (matches
/// `Handlers.MooLspServer.GetObjectInfo`). Kept as a loosely-typed `obj`
/// (dynamic `?` field access at the render site in `App.fs`), matching this
/// file's existing style for `getObjectTreeAsync` rather than
/// introducing heavier typed modeling just for this one screen. `None` if
/// `objRef` isn't in the loaded graph at all.
let getObjectInfoAsync (objRef: int64) : Async<obj option> =
    async {
        let! result = requestAsync "moodev/getObjectInfo" (createObj [ "objRef" ==> objRef ])
        return if isNullOrUndefined result then None else Some result
    }

/// Wires hover, go-to-definition, completions, signature help,
/// find-references, and the custom `moodev-verb://` URI opener into the
/// given Monaco instance for the "moocode" language.
///
/// - `getCurrentDocument`: which verb the editor is currently showing, if
///   any - read fresh on every request rather than cached, since it changes
///   whenever the user clicks Open.
/// - `jumpTo`: navigates the editor to `(objRef, verbName)` and, once
///   there, positions the cursor at `(line, column)` (both 1-based, Monaco's
///   own convention - the same pair `openCodeEditor`'s `selection` already
///   uses). For a cross-verb dispatch jump this reuses the exact same
///   `$vcs:ide_fetch` flow the Open button already drives (the position is
///   always (1,1) in that case - `locationOfVerb` has no per-statement
///   spans to offer - which is where a freshly-loaded verb's cursor starts
///   anyway, so the caller doesn't need to do anything extra for it); for a
///   same-document jump (a local variable's definition) the target verb is
///   already open, so the caller can just move the cursor directly.
/// - `showCaveat`: surfaces find-references' "N call sites couldn't be
///   statically confirmed" note (see `provideReferences` below) - wired to
///   the same diagnostics area save errors already use.
let wire
    (monaco: obj)
    (getCurrentDocument: unit -> (int64 * string) option)
    (jumpTo: int64 -> string -> int -> int -> unit)
    (showCaveat: string -> unit)
    : unit =
    // Monaco can invoke a provider again before an earlier call's websocket
    // round-trip has come back - moving the mouse across a word re-fires
    // hover, typing re-fires completion/signature-help, each an independent
    // request with no ordering guarantee on the wire. Without this, an
    // earlier request that happens to resolve *after* a newer one already
    // updated the widget clobbers it with stale (or, if the newer request's
    // position no longer matches, blank-looking) content - exactly the
    // "sometimes shows nothing for the same element, not just a delay"
    // symptom reported after live testing. One counter per provider,
    // bumped on every call; a result is only handed to Monaco if its
    // request was still the latest one outstanding when the reply arrived.
    let mutable hoverGen = 0
    let mutable definitionGen = 0
    let mutable completionGen = 0
    let mutable signatureHelpGen = 0

    let provideHover (_model: obj) (position: obj) : JS.Promise<obj> =
        hoverGen <- hoverGen + 1
        let myGen = hoverGen

        async {
            match getCurrentDocument () with
            | None -> return null
            | Some(objRef, verbName) ->
                // Monaco positions are 1-based; LSP positions are 0-based.
                let lspLine = (position?lineNumber: int) - 1
                let lspCol = (position?column: int) - 1

                let! result = requestAsync "textDocument/hover" (textDocumentPositionParams objRef verbName lspLine lspCol)

                if myGen <> hoverGen then
                    return null
                elif isNullOrUndefined result then
                    return null
                else
                    let markdownValue: string = result?contents?value
                    return createObj [ "contents" ==> [| createObj [ "value" ==> markdownValue ] |] ]
        }
        |> Async.StartAsPromise

    let provideDefinition (_model: obj) (position: obj) : JS.Promise<obj> =
        definitionGen <- definitionGen + 1
        let myGen = definitionGen

        async {
            match getCurrentDocument () with
            | None -> return null
            | Some(objRef, verbName) ->
                let lspLine = (position?lineNumber: int) - 1
                let lspCol = (position?column: int) - 1

                let! result = requestAsync "textDocument/definition" (textDocumentPositionParams objRef verbName lspLine lspCol)

                if myGen <> definitionGen then
                    return null
                elif isNullOrUndefined result then
                    return null
                else
                    let uri: string = result?uri
                    let range: obj = result?range

                    // The real range, not a hardcoded (1,1) - matters for a
                    // same-document jump (a local variable's definition,
                    // which always targets a real position inside the verb
                    // already open); for a cross-verb dispatch jump this is
                    // still just (1,1) server-side (`locationOfVerb` has no
                    // per-statement spans to offer), so this doesn't change
                    // that case's behavior. LSP positions are 0-based;
                    // Monaco's are 1-based.
                    return
                        createObj
                            [ "uri" ==> monaco?Uri?parse (uri)
                              "range" ==>
                                createObj
                                    [ "startLineNumber" ==> ((range?start?line: int) + 1)
                                      "startColumn" ==> ((range?start?character: int) + 1)
                                      "endLineNumber" ==> ((range?``end``?line: int) + 1)
                                      "endColumn" ==> ((range?``end``?character: int) + 1) ] ]
        }
        |> Async.StartAsPromise

    let provideCompletionItems (model: obj) (position: obj) : JS.Promise<obj> =
        completionGen <- completionGen + 1
        let myGen = completionGen

        async {
            match getCurrentDocument () with
            | None -> return createObj [ "suggestions" ==> [||] ]
            | Some(objRef, verbName) ->
                let lspLine = (position?lineNumber: int) - 1
                let lspCol = (position?column: int) - 1

                let! result = requestAsync "textDocument/completion" (textDocumentPositionParams objRef verbName lspLine lspCol)

                if myGen <> completionGen then
                    return createObj [ "suggestions" ==> [||] ]
                elif isNullOrUndefined result then
                    return createObj [ "suggestions" ==> [||] ]
                else
                    // Monaco requires an explicit replacement `range` per
                    // item (unlike the LSP response, which carries none) -
                    // `getWordUntilPosition` is Monaco's own documented way
                    // to find "the partial word being typed right before
                    // the cursor" for exactly this purpose.
                    let wordInfo = model?getWordUntilPosition (position)

                    let range =
                        createObj
                            [ "startLineNumber" ==> position?lineNumber
                              "startColumn" ==> wordInfo?startColumn
                              "endLineNumber" ==> position?lineNumber
                              "endColumn" ==> wordInfo?endColumn ]

                    let items: obj[] = unbox result

                    let suggestions =
                        items
                        |> Array.map (fun item ->
                            let label: string = item?label

                            createObj
                                [ "label" ==> label
                                  "kind" ==> monacoCompletionKind (item?kind: int)
                                  "insertText" ==> label
                                  "range" ==> range ])

                    return createObj [ "suggestions" ==> suggestions ]
        }
        |> Async.StartAsPromise

    let provideSignatureHelp (_model: obj) (position: obj) : JS.Promise<obj> =
        signatureHelpGen <- signatureHelpGen + 1
        let myGen = signatureHelpGen

        async {
            match getCurrentDocument () with
            | None -> return null
            | Some(objRef, verbName) ->
                let lspLine = (position?lineNumber: int) - 1
                let lspCol = (position?column: int) - 1

                let! result = requestAsync "textDocument/signatureHelp" (textDocumentPositionParams objRef verbName lspLine lspCol)

                if myGen <> signatureHelpGen then
                    return null
                elif isNullOrUndefined result then
                    return null
                else
                    let signatures: obj[] = result?signatures

                    let monacoSignatures =
                        signatures
                        |> Array.map (fun s ->
                            let parameters: obj[] = s?parameters

                            createObj
                                [ "label" ==> (s?label: string)
                                  "parameters" ==> (parameters |> Array.map (fun p -> createObj [ "label" ==> (p?label: string) ])) ])

                    return
                        createObj
                            [ "value" ==>
                                createObj
                                    [ "signatures" ==> monacoSignatures
                                      "activeSignature" ==> 0
                                      "activeParameter" ==> 0 ]
                              "dispose" ==> System.Action(fun () -> ()) ]
        }
        |> Async.StartAsPromise

    /// Real LSP `Location[]` has no slot for "N more call sites couldn't be
    /// confirmed" (see `Handlers.fs`'s `TextDocumentReferences` doc comment)
    /// - the server smuggles that count through as one synthetic
    /// `moodev-caveat://` entry. Strip it out of what Monaco's own
    /// "Find All References" peek view renders (it isn't a real jump
    /// target and `registerEditorOpener` would just reject it) and surface
    /// it through `showCaveat` instead.
    let provideReferences (_model: obj) (position: obj) : JS.Promise<obj[]> =
        async {
            match getCurrentDocument () with
            | None -> return [||]
            | Some(objRef, verbName) ->
                let lspLine = (position?lineNumber: int) - 1
                let lspCol = (position?column: int) - 1

                let! result = requestAsync "textDocument/references" (textDocumentPositionParams objRef verbName lspLine lspCol)

                if isNullOrUndefined result then
                    return [||]
                else
                    let locations: obj[] = unbox result
                    let mutable caveatSuffix: string option = None

                    let realLocations =
                        locations
                        |> Array.choose (fun loc ->
                            let uri: string = loc?uri

                            if uri.StartsWith("moodev-caveat://") then
                                caveatSuffix <- Some(uri.Substring("moodev-caveat://".Length))
                                None
                            else
                                let range: obj = loc?range

                                Some(
                                    createObj
                                        [ "uri" ==> monaco?Uri?parse (uri)
                                          "range" ==>
                                            createObj
                                                [ "startLineNumber" ==> ((range?start?line: int) + 1)
                                                  "startColumn" ==> ((range?start?character: int) + 1)
                                                  "endLineNumber" ==> ((range?``end``?line: int) + 1)
                                                  "endColumn" ==> ((range?``end``?character: int) + 1) ] ]
                                ))

                    match caveatSuffix with
                    | Some suffix ->
                        let count = suffix.Split('-') |> Array.tryHead |> Option.defaultValue "?"

                        showCaveat (
                            sprintf
                                "Note: %s more call site(s) use this verb's name but use a receiver (this:/player:/computed) that can't be confirmed statically - not shown above."
                                count
                        )
                    | None -> ()

                    return realLocations
        }
        |> Async.StartAsPromise

    monaco?languages?registerHoverProvider (
        "moocode",
        createObj [ "provideHover" ==> System.Func<obj, obj, JS.Promise<obj>>(fun m p -> provideHover m p) ]
    )
    |> ignore

    monaco?languages?registerDefinitionProvider (
        "moocode",
        createObj [ "provideDefinition" ==> System.Func<obj, obj, JS.Promise<obj>>(fun m p -> provideDefinition m p) ]
    )
    |> ignore

    monaco?languages?registerCompletionItemProvider (
        "moocode",
        createObj
            [ "triggerCharacters" ==> [| ":"; "$" |]
              "provideCompletionItems" ==> System.Func<obj, obj, JS.Promise<obj>>(fun m p -> provideCompletionItems m p) ]
    )
    |> ignore

    monaco?languages?registerSignatureHelpProvider (
        "moocode",
        createObj
            [ "signatureHelpTriggerCharacters" ==> [| "(" |]
              "provideSignatureHelp" ==> System.Func<obj, obj, JS.Promise<obj>>(fun m p -> provideSignatureHelp m p) ]
    )
    |> ignore

    monaco?languages?registerReferenceProvider (
        "moocode",
        createObj [ "provideReferences" ==> System.Func<obj, obj, JS.Promise<obj[]>>(fun m p -> provideReferences m p) ]
    )
    |> ignore

    // Only fires on an actual "go to definition" commit (F12/Ctrl+click),
    // never from casual hovering - `moodev-verb://` isn't a scheme Monaco's
    // own model system knows how to open, so without this handler "go to
    // definition" across verbs would silently do nothing. `selection` is
    // Monaco's own derived range - the same one `provideDefinition` handed
    // back (converted from the LSP response's real range), so it's already
    // correct for a same-document jump; no separate lookup needed here.
    let openCodeEditor (_source: obj) (resource: obj) (selection: obj) : bool =
        let uriString: string = resource?toString ()

        match System.Uri.TryCreate(uriString, System.UriKind.Absolute) with
        | true, parsed when parsed.Scheme = "moodev-verb" ->
            let objRef = int64 parsed.Host
            let verbName = System.Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'))
            let line: int = selection?startLineNumber
            let col: int = selection?startColumn
            jumpTo objRef verbName line col
            true
        | _ -> false

    monaco?editor?registerEditorOpener (
        createObj [ "openCodeEditor" ==> System.Func<obj, obj, obj, bool>(fun s r sel -> openCodeEditor s r sel) ]
    )
    |> ignore
