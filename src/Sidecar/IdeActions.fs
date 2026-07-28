/// Phase 4 of moo-vcs-plan.md: sidecar-mediated replacements for all five
/// retired `$vcs` IDE verbs (`ide_fetch`, `ide_save`, `ide_get_properties`,
/// `ide_set_property`, `ide_get_location`). Each function runs its MOO
/// query over the browser session's own live connection
/// (`BridgeHandler.evalOnSession` - so `player` is whichever character is
/// actually logged into that tab) and sends the response to the browser in
/// the exact same `moodev-*` wire shape the client already parses
/// (`App.fs`'s `ws.onmessage` handler needs zero changes), so only the
/// *sending* side of the client changes, not the receiving side.
module Sidecar.IdeActions

open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Sidecar.BridgeHandler

type Config =
    { TreeDir: string
      SessionId: string
      GitAuthorName: string
      GitAuthorEmail: string }

let private sendWire (webSocket: WebSocket) (header: string) (lines: string list) (ct: CancellationToken) : Task =
    task {
        if webSocket.State = WebSocketState.Open then
            let json = JsonSerializer.Serialize<McpWireMessage>({ header = header; lines = lines })
            let bytes = Encoding.UTF8.GetBytes(json)
            do! webSocket.SendAsync(System.ArraySegment(bytes), WebSocketMessageType.Text, true, ct)
    }

/// MOO statements resolving `verbName` to its 1-based index in `verbs(obj)`
/// - matching the alias the name is *found in*, not requiring it to equal
/// the object's full name-spec exactly. Sets a local `idx` (0 if not
/// found), same fix `Survive/VCS/3_capture_verb.moo` needed historically
/// (see `FORMAT.md` §4) and `Exporter.fs` already applies.
let private resolveVerbIndexStatements (o: string) (verbNameLiteral: string) : string =
    $"""vlist = verbs({o}); idx = 0; for i in [1..length(vlist)] if ({verbNameLiteral} in explode(vlist[i], " ")) idx = i; endif endfor"""

/// `ide_fetch(objRef, verbName)` replacement. `verb_code()` flags are
/// pinned (`0, 1`) per `FORMAT.md` §4, not left to ToastStunt's implicit
/// defaults.
let fetchVerb
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let verbLit = "\"" + verbName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let statements = resolveVerbIndexStatements o verbLit
        let resultExpr = """(idx == 0) ? ["error" -> "verb not found"] | ["code" -> verb_code(""" + o + ", idx, 0, 1)]"

        let! json = evalOnSession session statements resultExpr ct
        let root = json.RootElement
        let hasError, _ = root.TryGetProperty("error")

        if hasError then
            do! sendWire webSocket (sprintf "moodev-edit-result object: #%d verb: %s ok: 0" objRef verbName) [ "verb not found" ] ct
        else
            let code = root.GetProperty("code").EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> List.ofSeq
            do! sendWire webSocket (sprintf "moodev-edit-content object: #%d verb: %s" objRef verbName) code ct
    }

/// `ide_save(objRef, verbName, code)` replacement. On a successful save,
/// re-renders and commits *only this object's* tree files (not a full-tree
/// re-export) if it has a corponym - I3, no corponym means no versioning,
/// so a verb on an uncorified object still saves live but isn't tracked.
/// The read-back-and-render-to-disk step reuses this *session's own*
/// connection (`Exporter.EvalRunner` over `BridgeHandler.evalOnSession`),
/// not a second wizard `MooEval.connect` - an earlier version opened a
/// separate wizard connection here, but since the browser session is
/// typically *also* logged in as the wizard on this single-developer tool,
/// the second `connect wizard` made ToastStunt treat it as a reconnect of
/// the same player and silently drop the first connection, killing the
/// browser's own session out from under it (found live during Phase 4
/// verification - see `Exporter.EvalRunner`'s own comment for the full
/// story).
/// Re-exports `objRef`'s whole object (object.moo + every verb file) and
/// commits the result to the session's WIP ref, exactly the "capture
/// whatever's live now" step every mutation that changes an object's
/// exported shape needs (`saveVerb` for a verb body, `addProperty` for a
/// newly-registered property) - shared here so both stay in sync rather
/// than duplicating the export/write/commit sequence. `None` (silently, per
/// I3) if `objRef` isn't versioned at all; `Some errorMessage` if the MOO
/// query or export/commit itself threw - best-effort, since a failure here
/// shouldn't undo a change that's already live on the MOO.
let private exportAndCommitObject
    (config: Config)
    (session: Session)
    (objRef: int64)
    (changeName: string)
    (changeKind: GitStore.ChangeKind)
    (ct: CancellationToken)
    : Task<string option> =
    task {
        try
            let evalRunner = evalOnSession session
            let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct

            // #0 (System Object) is always versioned regardless of
            // corponym - FORMAT.md §1's exception, directory "0", raw "#0"
            // self-reference - so editing/adding to it through this same
            // save path actually commits, like every other object.
            let versionedAs =
                if objRef = 0L then
                    Some("0", "#0")
                else
                    Map.tryFind objRef corponymsByObjnum |> Option.map (fun name -> name, "$" + name)

            match versionedAs with
            | None -> return None // uncorified - not versioned, per I3
            | Some(dirName, selfRefText) ->
                let! dataOpt = Exporter.getObjectExport evalRunner objRef ct

                match dataOpt with
                | None -> return None
                | Some data ->
                    let objDir = System.IO.Path.Combine(config.TreeDir, "objects", dirName)
                    let verbsDir = System.IO.Path.Combine(objDir, "verbs")
                    System.IO.Directory.CreateDirectory(verbsDir) |> ignore

                    let verbFileNames = Exporter.assignVerbFileNames data.Verbs
                    let objectMooPath = System.IO.Path.Combine(objDir, "object.moo")
                    System.IO.File.WriteAllText(objectMooPath, Exporter.renderObjectMoo corponymsByObjnum selfRefText data verbFileNames)

                    // `corponymsByObjnum` above is always a fresh, live query
                    // (`getCorponyms` scans every object-valued property on
                    // #0 right now) - `corponyms.moo` on disk is only ever a
                    // cached snapshot of that, so it needs the same refresh
                    // whenever a change here could have added a new one
                    // (confirmed live: registering a corponym through
                    // `addProperty` then re-exporting rendered a `$name`
                    // parent reference the *next* load couldn't resolve,
                    // since `corponyms.moo` itself was never told about it).
                    let corponymsList = corponymsByObjnum |> Map.toList |> List.map (fun (num, name) -> name, num)
                    let corponymsPath = System.IO.Path.Combine(config.TreeDir, "corponyms.moo")
                    System.IO.File.WriteAllText(corponymsPath, Exporter.renderCorponymsMoo corponymsList)

                    let relativePaths =
                        ResizeArray<string>(
                            [ System.IO.Path.Combine("objects", dirName, "object.moo")
                              "corponyms.moo" ]
                        )

                    for verb, fileName in verbFileNames do
                        let path = System.IO.Path.Combine(verbsDir, fileName)
                        System.IO.File.WriteAllText(path, Exporter.renderVerbFile selfRefText verb)
                        relativePaths.Add(System.IO.Path.Combine("objects", dirName, "verbs", fileName))

                    use repo = new LibGit2Sharp.Repository(config.TreeDir)

                    let message =
                        GitStore.buildCommitMessage [ { Corponym = dirName; Name = changeName; Kind = changeKind } ]

                    GitStore.commitChangedFiles
                        repo
                        config.SessionId
                        (List.ofSeq relativePaths)
                        message
                        config.GitAuthorName
                        config.GitAuthorEmail
                    |> ignore

                    return None
        with ex ->
            return Some ex.Message
    }

let saveVerb
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (code: string list)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let verbLit = "\"" + verbName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let codeLiteral = "{" + (code |> List.map (fun l -> "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") |> String.concat ", ") + "}"

        let statements =
            resolveVerbIndexStatements o verbLit
            + $""" errs = (idx == 0) ? {{"verb not found"}} | set_verb_code({o}, idx, {codeLiteral});"""

        let! json = evalOnSession session statements "errs" ct
        let errors = json.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

        if not errors.IsEmpty then
            do!
                sendWire
                    webSocket
                    (sprintf "moodev-edit-result object: #%d verb: %s ok: 0" objRef verbName)
                    errors
                    ct
        else
            // Best-effort: a failure here shouldn't undo a save that's
            // already live on the MOO - just report it to diagnostics
            // rather than claiming the save itself failed.
            let! gitError = exportAndCommitObject config session objRef verbName GitStore.Modified ct

            let diagnostics = gitError |> Option.map (fun m -> [ "(saved, but git commit failed: " + m + ")" ]) |> Option.defaultValue []

            do! sendWire webSocket (sprintf "moodev-edit-result object: #%d verb: %s ok: 1" objRef verbName) diagnostics ct
    }

/// `ide_get_properties(objRef)` replacement. `properties(obj)` already
/// only lists properties *defined* on `obj` (confirmed against
/// `property.cc:bf_properties`, see `Importer.fs`'s own note on this) -
/// matching the retired verb's exact behavior, not a redesign of it.
let getProperties (config: Config) (session: Session) (webSocket: WebSocket) (objRef: int64) (ct: CancellationToken) : Task<unit> =
    task {
        let o = sprintf "#%d" objRef

        // A real tab byte via chr(9), not "\t" - MOO string literals have no
        // \t escape (only \" and \\ are escaped), confirmed against
        // moocode-reference.md and the retired $vcs:ide_get_properties'
        // own use of chr(9) for exactly this reason.
        let statements =
            $"""props = {{}}; for pn in (properties({o})) props = {{@props, pn + chr(9) + toliteral({o}.(pn))}}; endfor"""

        let! json = evalOnSession session statements "props" ct
        let lines = json.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
        do! sendWire webSocket (sprintf "moodev-prop-content object: #%d" objRef) lines ct
    }

/// `ide_set_property(objRef, pname, literalText)` replacement. Property
/// values stay "an expression the user typed, evaluated server-side" -
/// exactly the retired verb's own semantics (`Survive/VCS/12_ide_set_property.moo`:
/// `result = eval("return " + literal + ";"); ... OBJ.(pname) = result[2];`
/// - note `result[2]`, not `result[2][1]`: `eval()`'s second element is the
/// value directly on success, the same fact `Importer.fs`'s own bug fix
/// confirmed live against the server), not a redesign of that UX.
let setProperty
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (literalText: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let literalLit = "\"" + literalText.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; try result = eval("return " + {literalLit} + ";"); if (result[1]) try {o}.{pname} = result[2]; ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        do!
            sendWire
                webSocket
                (sprintf "moodev-prop-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                (if ok then [] else [ errtext ])
                ct
    }

/// Creates a *new* property - `setProperty` above only ever assigns to one
/// that already exists (`E_PROPNF` otherwise, reported as a normal error).
/// Nothing before this (client- or server-side) could actually create a
/// property at all, which is what registering a new `$name` corponym on
/// `#0` needs. Same value-parsing convention as `setProperty` (`eval("return
/// " + literal + ";")`), but calls `add_property(obj, name, value, {owner,
/// perms})` instead of a plain assignment - unlike `setProperty`'s bare
/// `.{pname}` identifier splice, the property name here is a real quoted
/// MOO string argument to `add_property`, so it doesn't need to be a
/// syntactically valid identifier to pass through safely.
let addProperty
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (literalText: string)
    (perms: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let literalLit = quote literalText
        let pnameLit = quote pname
        let permsLit = quote perms

        let statements =
            $"""ok = 0; errtext = ""; try result = eval("return " + {literalLit} + ";"); if (result[1]) try add_property({o}, {pnameLit}, result[2], {{player, {permsLit}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        // A brand new property has no row in the LSP's static graph at all
        // (unlike a verb body edit, which only ever changes content the
        // inspector already has a row for) - without this same
        // export+commit step `saveVerb` uses, it would stay live on the MOO
        // but invisible to the inspector/tree until some unrelated save
        // happened to re-export the object.
        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef pname GitStore.Added ct
                    return gitError |> Option.map (fun m -> [ "(added, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-prop-add-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// `ide_get_location()` replacement - "player" here is whichever character
/// is logged into this session, matching the retired verb's own
/// `player.location` (this only works correctly because the query runs
/// over the session's own connection, not a shared wizard one).
let getLocation (config: Config) (session: Session) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        // Real tab bytes via chr(9), not "\t" - see getProperties' comment.
        let statements =
            """room = player.location; lines = {}; if (valid(room)) exits = `room.exits ! E_PROPNF => {}'; lines = {"room" + chr(9) + tostr(room) + chr(9) + room.name}; for e in (exits) lines = {@lines, "exit" + chr(9) + tostr(e) + chr(9) + e.name}; endfor for c in (room.contents) if (c != player) lines = {@lines, "content" + chr(9) + tostr(c) + chr(9) + c.name}; endif endfor endif"""

        let! json = evalOnSession session statements "lines" ct
        let lines = json.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
        do! sendWire webSocket "moodev-location-content" lines ct
    }

/// Resolves `obj`+`verbName` to its corponym and *current* on-disk path
/// (`objects/<corponym>/verbs/<file>.moo`) - the same lookup `saveVerb`
/// already does, shared here for the three history/search actions below
/// that need it too. `None` covers every reason the verb isn't tracked:
/// no corponym (I3), the object vanished, or no verb by that name.
let private resolveVerbPath
    (evalRunner: Exporter.EvalRunner)
    (objRef: int64)
    (verbName: string)
    (ct: CancellationToken)
    : Task<(string * string) option> =
    task {
        let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct

        match Map.tryFind objRef corponymsByObjnum with
        | None -> return None
        | Some name ->
            let! dataOpt = Exporter.getObjectExport evalRunner objRef ct

            match dataOpt with
            | None -> return None
            | Some data ->
                let verbFileNames = Exporter.assignVerbFileNames data.Verbs

                match verbFileNames |> List.tryFind (fun (v, _) -> v.Names.Split(' ') |> Array.contains verbName) with
                | None -> return None
                | Some(_, fileName) ->
                    return Some(name, System.IO.Path.Combine("objects", name, "verbs", fileName).Replace('\\', '/'))
    }

/// `verb-history {obj, verb}` - Q1/Q2's "what did this look like before" /
/// "when did this break", per-verb: every commit that touched this verb's
/// file, most recent first. `moodev-verb-history` on success (lines =
/// `sha<TAB>unixSeconds<TAB>message`, matching `getProperties`'ish
/// tab-separated convention); `moodev-verb-history-result ok: 0` if the verb
/// isn't tracked at all (mirrors `fetchVerb`'s content/result header split).
let verbHistory
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let! resolved = resolveVerbPath (evalOnSession session) objRef verbName ct

        match resolved with
        | None ->
            do!
                sendWire
                    webSocket
                    (sprintf "moodev-verb-history-result object: #%d verb: %s ok: 0" objRef verbName)
                    [ "verb not tracked (no corponym, or verb not found)" ]
                    ct
        | Some(_, relativePath) ->
            use repo = new LibGit2Sharp.Repository(config.TreeDir)
            let startCommit = GitStore.resolveParent repo config.SessionId
            let history = History.getFileHistory repo startCommit relativePath

            let lines =
                history
                |> List.map (fun e -> sprintf "%s\t%d\t%s" e.Sha (e.When.ToUnixTimeSeconds()) e.Message)

            do! sendWire webSocket (sprintf "moodev-verb-history object: #%d verb: %s" objRef verbName) lines ct
    }

/// `verb-at-commit {obj, verb, sha}` - the historical code for one entry
/// from `verb-history`'s own list, for the diff view (and, via the client
/// just calling `editor.setValue()` with it, "restore"). Looks up the path
/// *at that specific commit* from `verb-history`'s own result rather than
/// assuming today's filename applied back then - a verb whose canonical
/// first alias changed would otherwise resolve to the wrong (or missing)
/// blob for its older commits.
let verbAtCommit
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (sha: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let sendError () =
            sendWire
                webSocket
                (sprintf "moodev-verb-at-commit-result object: #%d verb: %s sha: %s ok: 0" objRef verbName sha)
                [ "verb not found at that commit" ]
                ct

        let! resolved = resolveVerbPath (evalOnSession session) objRef verbName ct

        match resolved with
        | None -> do! sendError ()
        | Some(_, currentPath) ->
            use repo = new LibGit2Sharp.Repository(config.TreeDir)
            let startCommit = GitStore.resolveParent repo config.SessionId
            let history = History.getFileHistory repo startCommit currentPath

            match history |> List.tryFind (fun e -> e.Sha = sha) with
            | None -> do! sendError ()
            | Some entry ->
                match History.getBlobAtCommit repo sha entry.Path with
                | None -> do! sendError ()
                | Some text ->
                    let code = (TreeParser.parseVerbFileLines (text.Split('\n'))).Code
                    do! sendWire webSocket (sprintf "moodev-verb-at-commit object: #%d verb: %s sha: %s" objRef verbName sha) code ct
    }

/// `search-history {query}` - Q4's "what did I change yesterday", across
/// every tracked verb/property file (`corponyms.moo` itself is excluded -
/// `corponym-history` covers that distinctly). `moodev-search-result` lines
/// = `sha<TAB>unixSeconds<TAB>objnum<TAB>corponym<TAB>label<TAB>message`;
/// `objnum` is resolved against the *current* live corponym map (not the
/// historical one at that commit) since it's used for click-through into
/// the live editor, and I2 means a corponym's objnum is stable within one
/// instance's own history once assigned - only a repoint changes it, which
/// `corponym-history` surfaces on its own. Empty `objnum` means the
/// corponym no longer resolves live (renamed/removed since) - not
/// clickable.
let searchHistory
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (query: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let! corponymsByObjnum = Exporter.getCorponyms (evalOnSession session) ct
        let objnumByCorponym = corponymsByObjnum |> Map.toList |> List.map (fun (n, name) -> name, n) |> Map.ofList

        use repo = new LibGit2Sharp.Repository(config.TreeDir)
        let startCommit = GitStore.resolveParent repo config.SessionId

        let matches =
            History.searchContent repo startCommit query None
            |> List.filter (fun m -> m.Path <> "corponyms.moo")

        let lines =
            matches
            |> List.choose (fun m ->
                match Exporter.describePath m.Path with
                | None -> None
                | Some(corponym, label) ->
                    let objnumText =
                        Map.tryFind corponym objnumByCorponym
                        |> Option.map (sprintf "%d")
                        |> Option.defaultValue ""

                    Some(sprintf "%s\t%d\t%s\t%s\t%s\t%s" m.Sha (m.When.ToUnixTimeSeconds()) objnumText corponym label m.Message))

        do! sendWire webSocket "moodev-search-result" lines ct
    }

/// `corponym-history {}` - Q5's "why is $room pointing at #14": every
/// change ever made to `corponyms.moo`, each expanded into its individual
/// added/removed/repointed entries via `History.diffCorponyms`.
/// `moodev-corponym-history` lines =
/// `sha<TAB>unixSeconds<TAB>kind<TAB>name<TAB>detail` (`kind` one of
/// `added`/`removed`/`repointed`; `detail` is `#objnum` or `#old -> #new`).
/// Pure git history - doesn't need the session's live MOO connection at
/// all, same as `checkpoint`.
let corponymHistory (config: Config) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        use repo = new LibGit2Sharp.Repository(config.TreeDir)
        let startCommit = GitStore.resolveParent repo config.SessionId
        let history = History.getFileHistory repo startCommit "corponyms.moo"

        let lines =
            history
            |> List.collect (fun entry ->
                History.diffCorponyms repo entry.Sha
                |> List.map (fun change ->
                    let kind, name, detail =
                        match change with
                        | History.Added(name, objnum) -> "added", name, sprintf "#%d" objnum
                        | History.Removed(name, objnum) -> "removed", name, sprintf "#%d" objnum
                        | History.Repointed(name, fromObjnum, toObjnum) ->
                            "repointed", name, sprintf "#%d -> #%d" fromObjnum toObjnum

                    sprintf "%s\t%d\t%s\t%s\t%s" entry.Sha (entry.When.ToUnixTimeSeconds()) kind name detail))

        do! sendWire webSocket "moodev-corponym-history" lines ct
    }

/// Explicit checkpoint action (`{"action":"checkpoint"}`) - squashes this
/// session's wip ref onto `main` on demand, the same operation the idle
/// timer triggers automatically.
let checkpoint (config: Config) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        use repo = new LibGit2Sharp.Repository(config.TreeDir)

        let message = sprintf "Checkpoint (%s)" config.SessionId

        match GitStore.squashWipOntoMain repo config.SessionId message config.GitAuthorName config.GitAuthorEmail with
        | Some _ -> do! sendWire webSocket "moodev-edit-result ok: 1" [ "Checkpoint committed." ] ct
        | None -> do! sendWire webSocket "moodev-edit-result ok: 1" [ "Nothing to checkpoint." ] ct
    }
