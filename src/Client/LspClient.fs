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
let private pending = Dictionary<int, (obj -> unit) * (exn -> unit)>()
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
    Async.FromContinuations(fun (resolve, reject, _) ->
        let id = nextId
        nextId <- nextId + 1
        pending.[id] <- (resolve, reject)

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
            let resolve, reject = pending.[unbox id]
            pending.Remove(unbox id) |> ignore
            let error: obj = msg?error

            if isNullOrUndefined error then
                resolve msg?result
            else
                // Previously ignored entirely (only `resolve` was ever wired up,
                // so an error response silently resolved with `undefined` as if
                // it had succeeded) - confirmed live as the root cause of a
                // switch-target failure looking like a no-op success: the graph
                // reload threw server-side (a dangling parent reference), but
                // the client sailed on to `window.location.reload()` as if it
                // had worked, leaving the stale pre-switch graph in place with
                // no visible error anywhere.
                let message: string = error?message |> Option.ofObj |> Option.defaultValue "LSP request failed"
                reject (exn message)

/// `moodev-verb://<objRef>/<verbName>` - mirrors `Handlers.moodevVerbUri` on
/// the server exactly (the browser never has a real filesystem path; object
/// # + verb name is all it ever knows, the same pair `$vcs:ide_fetch`/
/// `ide_save` already key off of).
let private documentUri (objRef: int64) (verbName: string) : string =
    sprintf "moodev-verb://%d/%s" objRef (System.Uri.EscapeDataString verbName)

/// The inverse of `documentUri` - `None` for anything not in that exact
/// shape (the `moodev-caveat://` sentinel `provideReferences` also sees on
/// this same wire, for one). Used to look up a reference *result*'s own
/// verb (not necessarily the currently-open one) for its own reindent
/// delta - see `provideReferences`' own comment.
let private tryParseDocumentUri (uri: string) : (int64 * string) option =
    let prefix = "moodev-verb://"

    if not (uri.StartsWith(prefix)) then
        None
    else
        match uri.Substring(prefix.Length).Split('/') with
        | [| objNum; verbName |] ->
            match System.Int64.TryParse objNum with
            | true, objRef -> Some(objRef, System.Uri.UnescapeDataString verbName)
            | false, _ -> None
        | _ -> None

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

/// Custom method (`moodev/findDeadVerbs`, no params) - manually triggered
/// corpus-wide "what's safe to delete" scan (matches
/// `Handlers.MooLspServer.FindDeadVerbs`/`Handlers.findDeadVerbs`). Same
/// `float`-then-`int64` conversion discipline as `getObjectTreeAsync` above.
/// `dobj`/`prep`/`iobj` let the dead-verbs view show each entry in
/// MOO-call-syntax shape (`obj:verb(this, none, this)`), not just a bare
/// name.
let findDeadVerbsAsync () : Async<(int64 * string * string * string * string * bool)[]> =
    async {
        let! result = requestAsync "moodev/findDeadVerbs" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result

            return
                items
                |> Array.map (fun o ->
                    int64 (o?objRef: float),
                    (o?verbName: string),
                    (o?dobj: string),
                    (o?prep: string),
                    (o?iobj: string),
                    (o?possiblyDynamic: bool))
    }

/// Custom method (`moodev/getVerbMetrics`, no params) - the "Verb
/// complexity size metrics dashboard": every verb's line count, corpus-wide
/// call count, and max nesting depth (matches `Handlers.VerbMetricsEntry`/
/// `Handlers.computeVerbMetrics`).
let getVerbMetricsAsync () : Async<(int64 * string * int * int * int)[]> =
    async {
        let! result = requestAsync "moodev/getVerbMetrics" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result

            return
                items
                |> Array.map (fun o ->
                    int64 (o?objRef: float),
                    (o?verbName: string),
                    int (o?lineCount: float),
                    int (o?callCount: float),
                    int (o?maxDepth: float))
    }

/// Custom method (`moodev/findDeadProperties`, no params) - the same
/// "what's safe to delete" scan as `findDeadVerbsAsync`, for properties
/// (matches `Handlers.MooLspServer.FindDeadProperties`/
/// `Handlers.findDeadProperties`).
let findDeadPropertiesAsync () : Async<(int64 * string * bool)[]> =
    async {
        let! result = requestAsync "moodev/findDeadProperties" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result

            return
                items
                |> Array.map (fun o -> int64 (o?objRef: float), (o?propertyName: string), (o?possiblyDynamic: bool))
    }

/// Custom method (`moodev/findReferencesToObject`, `{objRef}`) - the
/// recycle-safety precheck: every reference `Handlers.findReferencesToObject`
/// can confirm statically for one candidate object (matches
/// `Handlers.MooLspServer.FindReferencesToObject`). `kind` is one of
/// `"verb-call"`/`"object-owner"`/`"verb-owner"`/`"property-owner"`; `detail`
/// is the call/verb/property name, `""` for `"object-owner"`.
let findReferencesToObjectAsync (objRef: int64) : Async<(string * int64 * string)[]> =
    async {
        let! result = requestAsync "moodev/findReferencesToObject" (createObj [ "objRef" ==> float objRef ])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result
            return items |> Array.map (fun o -> (o?kind: string), int64 (o?objRef: float), (o?detail: string))
    }

/// Custom method (`moodev/resolveEffectiveMember`, `{objRef, kind, name}`) -
/// the permission-inheritance visualizer's own lookup: which ancestor's copy
/// of this verb/property actually wins by real MOO dispatch order (matches
/// `Handlers.MooLspServer.ResolveEffectiveMember`). `kind` is `"verb"` or
/// `"property"`. `None` when the name doesn't resolve at all against the
/// static graph.
let resolveEffectiveMemberAsync (objRef: int64) (kind: string) (name: string) : Async<int64 option> =
    async {
        let! result =
            requestAsync
                "moodev/resolveEffectiveMember"
                (createObj [ "objRef" ==> float objRef; "kind" ==> kind; "name" ==> name ])

        return if isNullOrUndefined result then None else Some(int64 (unbox result: float))
    }

/// Custom method (`moodev/getCallGraph`, `{objRef, verbName}`) - the call
/// graph view's own lookup (matches `Handlers.MooLspServer.GetCallGraph`):
/// one-hop callees and callers of this verb. Each returned as a plain
/// `(objRef, verbName)` tuple pair - no need for a richer type client-side,
/// same "just enough to label and navigate" convention this file's other
/// custom-method results already follow.
let getCallGraphAsync (objRef: int64) (verbName: string) : Async<(int64 * string)[] * (int64 * string)[]> =
    async {
        let! result = requestAsync "moodev/getCallGraph" (createObj [ "objRef" ==> float objRef; "verbName" ==> verbName ])

        if isNullOrUndefined result then
            return [||], [||]
        else
            let toNodes (items: obj[]) = items |> Array.map (fun o -> int64 (o?objRef: float), (o?verbName: string))
            return toNodes (result?callees: obj[]), toNodes (result?callers: obj[])
    }

/// Custom method (`moodev/reloadGraph`, `{surviveRoot}`) - reloads the
/// language server's static analysis graph in place from `surviveRoot`,
/// without restarting its process (matches
/// `Handlers.MooLspServer.ReloadGraph`). Part of the "Configurable MOO
/// server target" feature's switch sequence: after the sidecar's own
/// `"reconfigure-target"` action succeeds, this brings the *next* `/lsp`
/// connection's graph in sync too, before the page reload that opens it.
let reloadGraphAsync (surviveRoot: string) : Async<unit> =
    async { do! requestAsync "moodev/reloadGraph" (createObj [ "surviveRoot" ==> surviveRoot ]) |> Async.Ignore }

/// One confirmed call site from `moodev/prepareRename` - everything the
/// `"rename-verb"` Sidecar action needs to splice the new name in, without
/// the client re-resolving anything itself.
type RenameCallSite =
    { ObjRef: int64
      VerbName: string
      Line: int
      Col: int
      Length: int }

/// `moodev/prepareRename`'s result (matches
/// `Handlers.MooLspServer.PrepareRename`/`Handlers.PrepareRenameResult`) -
/// the resolved verb's own current name plus every confirmed call site,
/// everything the F2 rename flow needs to build a confirm dialog and the
/// exact `"rename-verb"` patch list in one round trip.
type PrepareRenameResult =
    { ObjRef: int64
      VerbName: string
      Sites: RenameCallSite[]
      UnresolvedCount: int }

/// Custom method (`moodev/prepareRename`, `{textDocument, position}`) -
/// resolves the verb call under the cursor and every confirmed call site to
/// it, corpus-wide. `None` when the cursor isn't on a resolvable verb call
/// at all (mirrors every other position-based query's "nothing here" case).
let prepareRenameAsync (objRef: int64) (verbName: string) (lspLine: int) (lspCharacter: int) : Async<PrepareRenameResult option> =
    async {
        let! result = requestAsync "moodev/prepareRename" (textDocumentPositionParams objRef verbName lspLine lspCharacter)

        if isNullOrUndefined result then
            return None
        else
            let sitesArr: obj[] = result?sites

            let sites =
                sitesArr
                |> Array.map (fun s ->
                    { ObjRef = int64 (s?objRef: float)
                      VerbName = s?verbName
                      Line = int (s?line: float)
                      Col = int (s?col: float)
                      Length = int (s?length: float) })

            return
                Some
                    { ObjRef = int64 (result?objRef: float)
                      VerbName = result?verbName
                      Sites = sites
                      UnresolvedCount = int (result?unresolvedCount: float) }
    }

/// Custom method (`moodev/findGotchas`, no params) - manually triggered
/// corpus-wide "MOOcode gotchas" static-check scan (matches
/// `Handlers.MooLspServer.FindGotchas`/`Handlers.findGotchas`). `kind` is
/// one of the plain-string tags `GotchaEntry.Kind` uses server-side
/// (`"missing-x-bit"` / `"unbounded-loop"` / `"zero-index"`) - the client's
/// `gotchaKindLabel` turns it into display text.
let findGotchasAsync () : Async<(int64 * string * string)[]> =
    async {
        let! result = requestAsync "moodev/findGotchas" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result
            return items |> Array.map (fun o -> int64 (o?objRef: float), (o?verbName: string), (o?kind: string))
    }

/// Custom method (`moodev/findPermissionRisks`, no params) - the permission
/// flag audit report (matches `Handlers.MooLspServer.FindPermissionRisks`/
/// `Handlers.findPermissionRisks`). `Kind` is one of the plain-string tags
/// `PermissionRiskEntry.Kind` uses server-side (`"wizard-writable-verb"` /
/// `"world-writable-property"`) - the client's `permissionRiskKindLabel`
/// turns it into display text.
let findPermissionRisksAsync () : Async<(int64 * string * string)[]> =
    async {
        let! result = requestAsync "moodev/findPermissionRisks" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result
            return items |> Array.map (fun o -> int64 (o?objRef: float), (o?name: string), (o?kind: string))
    }

/// Custom method (`moodev/getMoocodeDocs`, no params) - the full docs
/// catalog (matches `Handlers.MooLspServer.GetMoocodeDocs`/
/// `Handlers.moocodeDocs`): every control keyword, implicit variable, and
/// live builtin, one flat list. `kind` is one of `"keyword"`/`"variable"`/
/// `"builtin"`. Static for the whole session (nothing here changes without
/// a server restart), so the caller fetches this once and caches it rather
/// than re-requesting on every sidebar-view switch.
let getMoocodeDocsAsync () : Async<(string * string * string * string)[]> =
    async {
        let! result = requestAsync "moodev/getMoocodeDocs" (createObj [])

        if isNullOrUndefined result then
            return [||]
        else
            let items: obj[] = unbox result

            return
                items
                |> Array.map (fun o -> (o?name: string), (o?signature: string), (o?description: string), (o?kind: string))
    }

/// Converts a Monaco (1-based) `(lineNumber, column)` to an LSP (0-based)
/// `(line, character)`, undoing the client-side reindent-on-load offset for
/// that line first if `delta` has one - see `App.fs`'s `tabIndentDeltas` for
/// why this exists: the server's AST is positioned against the *raw* verb
/// source, but `editor.getPosition()` reports columns in the *displayed*
/// (locally reindented-for-readability) buffer, which diverge in leading
/// whitespace as soon as they differ at all. `delta.[i]` is
/// `displayedIndent - rawIndent` for 0-based line `i`, so subtracting it
/// from the displayed column recovers the equivalent raw column. Missing/
/// out-of-range entries mean "no adjustment" (the delta wasn't tracked, or
/// this line wasn't in it), same as the unadjusted behavior before this
/// existed.
let private toRawPosition (delta: int[] option) (lineNumber: int) (column: int) : int * int =
    let offset =
        delta
        |> Option.bind (fun d -> if lineNumber - 1 < d.Length then Some d.[lineNumber - 1] else None)
        |> Option.defaultValue 0

    (lineNumber - 1, column - 1 - offset)

/// The inverse of `toRawPosition` - an LSP (0-based) `(line, character)` to
/// Monaco (1-based) `(lineNumber, column)`, re-applying the same line's
/// offset. Used for a same-document go-to-definition jump (a local
/// variable's declaration, always inside the currently-open, already-
/// reindented verb) - see `provideDefinition` below.
let private toDisplayedPosition (delta: int[] option) (lspLine: int) (lspChar: int) : int * int =
    let offset = delta |> Option.bind (fun d -> if lspLine < d.Length then Some d.[lspLine] else None) |> Option.defaultValue 0

    (lspLine + 1, lspChar + 1 + offset)

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
/// - `getIndentDelta`: `App.fs`'s `tabIndentDeltas` lookup, `None` for a
///   verb never fetched this session (or reset right after a save) - every
///   position conversion here treats that the same as "no adjustment."
let wire
    (monaco: obj)
    (getCurrentDocument: unit -> (int64 * string) option)
    (jumpTo: int64 -> string -> int -> int -> unit)
    (showCaveat: string -> unit)
    (getIndentDelta: int64 -> string -> int[] option)
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
                let lspLine, lspCol =
                    toRawPosition (getIndentDelta objRef verbName) (position?lineNumber: int) (position?column: int)

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
                let currentDelta = getIndentDelta objRef verbName
                let lspLine, lspCol = toRawPosition currentDelta (position?lineNumber: int) (position?column: int)

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
                    // that case's behavior. Only re-applies the reindent
                    // delta for the *same-document* case (`uri` matches the
                    // verb we just queried against) - a cross-verb jump's
                    // (1,1) placeholder isn't a real position to begin with,
                    // so adjusting it by any delta (this document's or the
                    // target's, once it's opened and gets its own) wouldn't
                    // be principled.
                    let responseDelta = if uri = documentUri objRef verbName then currentDelta else None
                    let startLine, startCol = toDisplayedPosition responseDelta (range?start?line: int) (range?start?character: int)
                    let endLine, endCol = toDisplayedPosition responseDelta (range?``end``?line: int) (range?``end``?character: int)

                    return
                        createObj
                            [ "uri" ==> monaco?Uri?parse (uri)
                              "range" ==>
                                createObj
                                    [ "startLineNumber" ==> startLine
                                      "startColumn" ==> startCol
                                      "endLineNumber" ==> endLine
                                      "endColumn" ==> endCol ] ]
        }
        |> Async.StartAsPromise

    let provideCompletionItems (model: obj) (position: obj) : JS.Promise<obj> =
        completionGen <- completionGen + 1
        let myGen = completionGen

        async {
            match getCurrentDocument () with
            | None -> return createObj [ "suggestions" ==> [||] ]
            | Some(objRef, verbName) ->
                let lspLine, lspCol =
                    toRawPosition (getIndentDelta objRef verbName) (position?lineNumber: int) (position?column: int)

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

                            let documentation: obj =
                                if isNullOrUndefined item?documentation then
                                    null
                                else
                                    createObj [ "value" ==> (item?documentation?value: string) ]

                            createObj
                                [ "label" ==> label
                                  "kind" ==> monacoCompletionKind (item?kind: int)
                                  "insertText" ==> label
                                  "range" ==> range
                                  "documentation" ==> documentation ])

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
                let lspLine, lspCol =
                    toRawPosition (getIndentDelta objRef verbName) (position?lineNumber: int) (position?column: int)

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

                            let documentation: obj =
                                if isNullOrUndefined s?documentation then
                                    null
                                else
                                    createObj [ "value" ==> (s?documentation?value: string) ]

                            createObj
                                [ "label" ==> (s?label: string)
                                  "documentation" ==> documentation
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
                let lspLine, lspCol =
                    toRawPosition (getIndentDelta objRef verbName) (position?lineNumber: int) (position?column: int)

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

                                // Best-effort: this location's own verb
                                // might not be the currently-open one, and
                                // might never have been fetched this session
                                // at all - `getIndentDelta` returns `None`
                                // for that case, same as "no adjustment"
                                // (today's behavior, not a regression).
                                // Covered whenever the target happens to
                                // already be cached (e.g. it's the verb
                                // currently open, or was opened earlier this
                                // session).
                                let locationDelta =
                                    tryParseDocumentUri uri |> Option.bind (fun (o, v) -> getIndentDelta o v)

                                let startLine, startCol = toDisplayedPosition locationDelta (range?start?line: int) (range?start?character: int)
                                let endLine, endCol = toDisplayedPosition locationDelta (range?``end``?line: int) (range?``end``?character: int)

                                Some(
                                    createObj
                                        [ "uri" ==> monaco?Uri?parse (uri)
                                          "range" ==>
                                            createObj
                                                [ "startLineNumber" ==> startLine
                                                  "startColumn" ==> startCol
                                                  "endLineNumber" ==> endLine
                                                  "endColumn" ==> endCol ] ]
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

    /// Monaco calls this on every visible-range change with the range
    /// currently in view, unlike hover/references/etc which only fire on a
    /// single cursor gesture - matches `textDocument/inlayHint`'s own LSP
    /// shape, which takes a range rather than a position for the same
    /// reason. Monaco's JS `InlayHintKind`/label/position shapes already
    /// number-align with the LSP wire values `Handlers.fs`'s
    /// `TextDocumentInlayHint` sends (`Parameter` = 2 on both sides), so
    /// they pass straight through with no remapping - only the position's
    /// 0-based-to-1-based line/column conversion every other provider here
    /// already does.
    let provideInlayHints (_model: obj) (range: obj) : JS.Promise<obj> =
        async {
            let noHints = createObj [ "hints" ==> [||]; "dispose" ==> System.Action(fun () -> ()) ]

            match getCurrentDocument () with
            | None -> return noHints
            | Some(objRef, verbName) ->
                let delta = getIndentDelta objRef verbName
                let startLspLine, startLspChar = toRawPosition delta (range?startLineNumber: int) (range?startColumn: int)
                let endLspLine, endLspChar = toRawPosition delta (range?endLineNumber: int) (range?endColumn: int)

                let lspRange =
                    createObj
                        [ "start" ==> createObj [ "line" ==> startLspLine; "character" ==> startLspChar ]
                          "end" ==> createObj [ "line" ==> endLspLine; "character" ==> endLspChar ] ]

                let p =
                    createObj
                        [ "textDocument" ==> createObj [ "uri" ==> documentUri objRef verbName ]
                          "range" ==> lspRange ]

                let! result = requestAsync "textDocument/inlayHint" p

                if isNullOrUndefined result then
                    return noHints
                else
                    let lspHints: obj[] = unbox result

                    let monacoHints =
                        lspHints
                        |> Array.map (fun h ->
                            let pos: obj = h?position
                            let lineNumber, column = toDisplayedPosition delta (pos?line: int) (pos?character: int)

                            createObj
                                [ "label" ==> (h?label: string)
                                  "position" ==> createObj [ "lineNumber" ==> lineNumber; "column" ==> column ]
                                  "kind" ==> h?kind
                                  "paddingLeft" ==> h?paddingLeft
                                  "paddingRight" ==> h?paddingRight ])

                    return createObj [ "hints" ==> monacoHints; "dispose" ==> System.Action(fun () -> ()) ]
        }
        |> Async.StartAsPromise

    monaco?languages?registerInlayHintsProvider (
        "moocode",
        createObj [ "provideInlayHints" ==> System.Func<obj, obj, obj, JS.Promise<obj>>(fun m r _tok -> provideInlayHints m r) ]
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
