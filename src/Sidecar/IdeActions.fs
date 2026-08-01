/// Phase 4 of moo-vcs-plan.md: sidecar-mediated replacements for the four
/// retired `$vcs` IDE verbs (`ide_fetch`, `ide_save`, `ide_get_properties`,
/// `ide_set_property`). Each function runs its MOO
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
open Language.Ast
open Sidecar.BridgeHandler

type Config =
    { TreeDir: string
      SessionId: string
      GitAuthorName: string
      GitAuthorEmail: string }

/// Not `private` - `Program.fs`'s `"get-moo-target"`/`"reconfigure-target"`
/// actions send responses this same way but don't operate on a live MOO
/// object, so they live directly in `Program.fs` rather than here, and need
/// this helper too.
let sendWire (webSocket: WebSocket) (header: string) (lines: string list) (ct: CancellationToken) : Task =
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

                    let currentFileNames = verbFileNames |> List.map snd |> Set.ofList

                    for verb, fileName in verbFileNames do
                        let path = System.IO.Path.Combine(verbsDir, fileName)
                        System.IO.File.WriteAllText(path, Exporter.renderVerbFile selfRefText verb)
                        relativePaths.Add(System.IO.Path.Combine("objects", dirName, "verbs", fileName))

                    // Self-healing reconciliation, not just deleteVerb-specific
                    // cleanup: any file already on disk in `verbsDir` that
                    // isn't part of the *current* verb set (a verb just
                    // deleted, or any other past staleness) gets removed from
                    // disk and dropped from the git tree too - otherwise it
                    // sits there orphaned forever, no longer referenced by
                    // `object.moo`'s own `verbs:` manifest line but still
                    // physically present.
                    let removedPaths =
                        System.IO.Directory.GetFiles(verbsDir)
                        |> Array.map System.IO.Path.GetFileName
                        |> Array.filter (fun fileName -> not (currentFileNames.Contains fileName))
                        |> Array.map (fun fileName ->
                            System.IO.File.Delete(System.IO.Path.Combine(verbsDir, fileName))
                            System.IO.Path.Combine("objects", dirName, "verbs", fileName))
                        |> List.ofArray

                    use repo = new LibGit2Sharp.Repository(config.TreeDir)

                    let message =
                        GitStore.buildCommitMessage [ { Corponym = dirName; Name = changeName; Kind = changeKind } ]

                    GitStore.commitChangedFiles
                        repo
                        config.SessionId
                        (List.ofSeq relativePaths)
                        removedPaths
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

/// Removes a verb entirely - `delete_verb(obj, verb-desc)`, resolved to an
/// index the same way `saveVerb`/`fetchVerb` do (matching whichever alias
/// is currently displayed, not requiring the full name-spec). Re-exports
/// on success (`GitStore.Removed`, per moo-vcs-plan.md I3's corponym gate)
/// so the now-stale verb file is actually removed from the tree -
/// `exportAndCommitObject`'s own stale-file reconciliation handles that,
/// not this function.
let deleteVerb
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

        let statements =
            resolveVerbIndexStatements o verbLit
            + $""" ok = 0; errtext = ""; if (idx == 0) errtext = "verb not found"; else try delete_verb({o}, idx); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry endif;"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef verbName GitStore.Removed ct
                    return gitError |> Option.map (fun m -> [ "(deleted, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-verb-delete-result object: #%d verb: %s ok: %d" objRef verbName (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Creates a *new* verb - `add_verb(obj, {owner, perms, names}, {dobj, prep,
/// iobj})`. `ownerExpr` is evaluated the same "any expression resolving to a
/// valid object" way `addProperty`'s owner is - unlike a property, a verb's
/// owner has no chown-style auto-override (confirmed against
/// `ToastStunt/src/db_verbs.cc` - no analog to `db_properties.cc`'s
/// `insert_prop2` owner override exists there), so this is a plain pass-
/// through, no special-casing needed. The new verb starts with empty code;
/// the caller opens it via the normal verb-editor flow afterward.
let addVerb
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (names: string)
    (ownerExpr: string)
    (perms: string)
    (dobj: string)
    (prep: string)
    (iobj: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let namesLit = quote names
        let ownerLit = quote ownerExpr
        let permsLit = quote perms
        let dobjLit = quote dobj
        let prepLit = quote prep
        let iobjLit = quote iobj

        let statements =
            $"""ok = 0; errtext = ""; try ownerResult = eval("return " + {ownerLit} + ";"); if (ownerResult[1]) try add_verb({o}, {{ownerResult[2], {permsLit}, {namesLit}}}, {{{dobjLit}, {prepLit}, {iobjLit}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error (owner)"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef names GitStore.Added ct
                    return gitError |> Option.map (fun m -> [ "(added, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-verb-add-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Changes any/all of an *existing* verb's names, owner, and perms in one
/// call - `set_verb_info(obj, verb-desc, {owner, perms, names})` (confirmed
/// against `ToastStunt/src/verbs.cc`'s `bf_set_verb_info`). `verbName` is
/// resolved to a 1-based index the same way `deleteVerb`/`fetchVerb` do
/// (matching whichever alias is currently displayed), not passed as a raw
/// name string - same alias-matching bug class `FORMAT.md` §4 documents.
/// Callers always resubmit all three fields, only one of which actually
/// changed - mirrors `setPropertyInfo`'s own shape.
let setVerbInfo
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (newNames: string)
    (ownerExpr: string)
    (perms: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let verbLit = quote verbName
        let newNamesLit = quote newNames
        let permsLit = quote perms
        let ownerLit = quote ownerExpr

        let statements =
            resolveVerbIndexStatements o verbLit
            + $""" ok = 0; errtext = ""; if (idx == 0) errtext = "verb not found"; else try ownerResult = eval("return " + {ownerLit} + ";"); if (ownerResult[1]) try set_verb_info({o}, idx, {{ownerResult[2], {permsLit}, {newNamesLit}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error (owner)"; endif except err (ANY) errtext = tostr(err[2]); endtry endif;"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef verbName GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-verb-info-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Changes an *existing* verb's dobj/prep/iobj arg-spec -
/// `set_verb_args(obj, verb-desc, {dobj, prep, iobj})` (confirmed against
/// `ToastStunt/src/verbs.cc`'s `bf_set_verb_args`). No object-expression
/// eval needed here - all three are plain arg-spec/preposition strings, not
/// object references. Same resolve-by-alias and resubmit-all-three shape
/// as `setVerbInfo`.
let setVerbArgs
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (dobj: string)
    (prep: string)
    (iobj: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let verbLit = quote verbName
        let dobjLit = quote dobj
        let prepLit = quote prep
        let iobjLit = quote iobj

        let statements =
            resolveVerbIndexStatements o verbLit
            + $""" ok = 0; errtext = ""; if (idx == 0) errtext = "verb not found"; else try set_verb_args({o}, idx, {{{dobjLit}, {prepLit}, {iobjLit}}}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry endif;"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef verbName GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-verb-args-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// `rename-verb {objRef, oldName, newName, sites}` - the custom, server-
/// orchestrated batch rename (`moodev/prepareRename`'s own doc comment
/// explains why this isn't `textDocument/rename`): renames the verb itself
/// via `set_verb_info` (keeping its existing owner/perms, replacing only
/// its name list with the single new name - a rename picks one canonical
/// name, it doesn't try to preserve every other alias), then patches every
/// confirmed call site directly by re-fetching that verb's *current* code,
/// splicing `newName` in at the exact `(line, col, length)`
/// `moodev/prepareRename` reported, and saving - entirely server-side, no
/// client Monaco/tab involvement at all. `sites` is exactly what
/// `moodev/prepareRename` returned. Per-site failures (the call site's text
/// no longer matches, or the spliced result fails to compile) are collected
/// and reported individually rather than aborting the whole batch - this
/// project's existing per-action error-reporting convention, and
/// appropriate given a rename's real blast radius across many verbs at
/// once.
let renameVerb
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (oldName: string)
    (newName: string)
    (sites: (int64 * string * int * int * int) list)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let o = sprintf "#%d" objRef
        let oldNameLit = quote oldName
        let newNameLit = quote newName

        let renameStatements =
            resolveVerbIndexStatements o oldNameLit
            + $""" ok = 0; errtext = ""; if (idx == 0) errtext = "verb not found"; else try info = verb_info({o}, idx); set_verb_info({o}, idx, {{info[1], info[2], {newNameLit}}}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry endif;"""

        let! renameJson = evalOnSession session renameStatements """["ok" -> ok, "errtext" -> errtext]""" ct
        let renameRoot = renameJson.RootElement
        let renameOk = renameRoot.GetProperty("ok").GetInt32() = 1
        let renameErrtext = renameRoot.GetProperty("errtext").GetString()

        if not renameOk then
            do! sendWire webSocket (sprintf "moodev-rename-result object: #%d ok: 0" objRef) [ renameErrtext ] ct
        else
            let! renameGitError = exportAndCommitObject config session objRef newName GitStore.Modified ct
            let siteFailures = ResizeArray<string>()

            for siteObj, siteVerb, line, col, length in sites do
                let siteO = sprintf "#%d" siteObj
                let siteVerbLit = quote siteVerb

                let fetchStatements =
                    resolveVerbIndexStatements siteO siteVerbLit
                    + $""" code = (idx == 0) ? {{}} | verb_code({siteO}, idx, 0, 1);"""

                let! codeJson = evalOnSession session fetchStatements "code" ct
                let codeLines = codeJson.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Array.ofSeq

                if line < 1 || line > codeLines.Length then
                    siteFailures.Add(sprintf "#%d:%s - line %d out of range, skipped" siteObj siteVerb line)
                else
                    let targetLine = codeLines.[line - 1]

                    if col < 1 || col - 1 + length > targetLine.Length || targetLine.Substring(col - 1, length) <> oldName then
                        siteFailures.Add(sprintf "#%d:%s - call site text no longer matches, skipped" siteObj siteVerb)
                    else
                        let splicedLine = targetLine.Remove(col - 1, length).Insert(col - 1, newName)
                        let newCodeLines = codeLines |> Array.mapi (fun i l -> if i = line - 1 then splicedLine else l)
                        let newCodeLiteral = "{" + (newCodeLines |> Array.map quote |> String.concat ", ") + "}"

                        let saveStatements =
                            resolveVerbIndexStatements siteO siteVerbLit
                            + $""" errs = (idx == 0) ? {{"verb not found"}} | set_verb_code({siteO}, idx, {newCodeLiteral});"""

                        let! errsJson = evalOnSession session saveStatements "errs" ct
                        let errs = errsJson.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

                        if errs.IsEmpty then
                            let! siteGitError = exportAndCommitObject config session siteObj siteVerb GitStore.Modified ct
                            siteGitError |> Option.iter (fun m -> siteFailures.Add(sprintf "#%d:%s - saved, but git commit failed: %s" siteObj siteVerb m))
                        else
                            siteFailures.Add(sprintf "#%d:%s - %s" siteObj siteVerb (String.concat "; " errs))

            let diagnostics =
                (renameGitError |> Option.map (fun m -> [ "(renamed, but git commit failed: " + m + ")" ]) |> Option.defaultValue [])
                @ (siteFailures |> List.ofSeq)

            do! sendWire webSocket (sprintf "moodev-rename-result object: #%d ok: 1" objRef) diagnostics ct
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
/// syntactically valid identifier to pass through safely. `ownerExpr` is
/// evaluated the same way `literalText` is (any expression resolving to a
/// valid object - `player`, `#N`, `$name`, ...) - `add_property` itself
/// raises `E_INVARG` for an invalid owner, caught below like any other
/// failure, so there's no separate validation step needed here.
let addProperty
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (ownerExpr: string)
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
        let ownerLit = quote ownerExpr

        let statements =
            $"""ok = 0; errtext = ""; try ownerResult = eval("return " + {ownerLit} + ";"); if (ownerResult[1]) try result = eval("return " + {literalLit} + ";"); if (result[1]) try add_property({o}, {pnameLit}, result[2], {{ownerResult[2], {permsLit}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error (value)"; endif except err3 (ANY) errtext = tostr(err3[2]); endtry else errtext = "parse error (owner)"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

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

/// Changes any/all of an *existing* property's name, owner, and perms in
/// one call - `set_property_info(obj, pname, {owner, perms, new-name})`
/// (confirmed against `ToastStunt/src/property.cc`'s `bf_set_prop_info`).
/// The inspector's per-field pencils each only change one of the three,
/// but the builtin always wants all three together, so callers always pass
/// the other two unchanged - same "resubmit the full triple" shape
/// `addVerb`'s sibling `setVerbInfo` uses for verbs.
let setPropertyInfo
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (newName: string)
    (ownerExpr: string)
    (perms: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let quote (s: string) = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        let pnameLit = quote pname
        let newNameLit = quote newName
        let permsLit = quote perms
        let ownerLit = quote ownerExpr

        let statements =
            $"""ok = 0; errtext = ""; try ownerResult = eval("return " + {ownerLit} + ";"); if (ownerResult[1]) try set_property_info({o}, {pnameLit}, {{ownerResult[2], {permsLit}, {newNameLit}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error (owner)"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef pname GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-prop-info-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Removes a property entirely - `delete_property(obj, pname)`, the
/// removal counterpart to `addProperty` above. Properties live inline in
/// `object.moo` (not their own file the way verbs do), so re-exporting
/// after a successful delete is enough on its own - no separate stale-file
/// cleanup needed the way `deleteVerb` needs for `verbsDir`.
let deleteProperty
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let pnameLit = "\"" + pname.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; try delete_property({o}, {pnameLit}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef pname GitStore.Removed ct
                    return gitError |> Option.map (fun m -> [ "(deleted, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-prop-delete-result object: #%d name: %s ok: %d" objRef pname (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Destroys an object - `recycle(obj)`. If the object has a corponym (per
/// moo-vcs-plan.md I3, only corponym'd objects are versioned at all), also
/// unregisters that corponym from `#0` first (otherwise `$name` keeps
/// pointing at a garbage/reused object number after this) and removes its
/// entire `objects/<corponym>/` directory from the git tree - unlike
/// `deleteVerb`/`deleteProperty`, there's no live object left to
/// re-export afterward, so this deletes rather than re-renders.
let recycleObject
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let evalRunner = evalOnSession session
        let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct
        let corponym = Map.tryFind objRef corponymsByObjnum
        let o = sprintf "#%d" objRef

        let statements =
            match corponym with
            | Some name ->
                let nameLit = "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

                $"""ok = 0; errtext = ""; try delete_property(#0, {nameLit}); recycle({o}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""
            | None -> $"""ok = 0; errtext = ""; try recycle({o}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalRunner statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    match corponym with
                    | None -> return []
                    | Some dirName ->
                        try
                            let objDir = System.IO.Path.Combine(config.TreeDir, "objects", dirName)

                            let removedPaths =
                                if System.IO.Directory.Exists(objDir) then
                                    let paths =
                                        System.IO.Directory.GetFiles(objDir, "*", System.IO.SearchOption.AllDirectories)
                                        |> Array.map (fun fullPath ->
                                            System.IO.Path.GetRelativePath(config.TreeDir, fullPath).Replace('\\', '/'))
                                        |> List.ofArray

                                    System.IO.Directory.Delete(objDir, true)
                                    paths
                                else
                                    []

                            // Fresh, post-recycle query - #0 no longer has this
                            // corponym property (deleted above), so
                            // corponyms.moo needs the same refresh
                            // `exportAndCommitObject` always does after any
                            // change that could affect the registry.
                            let! freshCorponyms = Exporter.getCorponyms evalRunner ct
                            let corponymsList = freshCorponyms |> Map.toList |> List.map (fun (num, name) -> name, num)
                            let corponymsPath = System.IO.Path.Combine(config.TreeDir, "corponyms.moo")
                            System.IO.File.WriteAllText(corponymsPath, Exporter.renderCorponymsMoo corponymsList)

                            use repo = new LibGit2Sharp.Repository(config.TreeDir)

                            let message =
                                GitStore.buildCommitMessage [ { Corponym = dirName; Name = dirName; Kind = GitStore.Removed } ]

                            GitStore.commitChangedFiles
                                repo
                                config.SessionId
                                [ "corponyms.moo" ]
                                removedPaths
                                message
                                config.GitAuthorName
                                config.GitAuthorEmail
                            |> ignore

                            return []
                        with ex ->
                            return [ "(recycled, but git cleanup failed: " + ex.Message + ")" ]
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-recycle-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Reassigns an object's owner - `.owner = newOwner`, a direct dot-
/// assignable pseudo-property (confirmed against `ToastStunt/src/execute.cc`'s
/// `OP_PUT_PROP` handling of `BP_OWNER` - wizard-only, unconditionally, no
/// owner-of-object exception). `ownerExpr` is evaluated the same "any
/// expression resolving to a valid object" way every other owner-taking
/// action already does. `owner:` is a real field in `object.moo`
/// (`FORMAT.md` §3), so this re-exports on success like any other
/// structural change, not a live-only mutation.
let setOwner
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (ownerExpr: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let ownerLit = "\"" + ownerExpr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; try ownerResult = eval("return " + {ownerLit} + ";"); if (ownerResult[1]) try {o}.owner = ownerResult[2]; ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef "owner" GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-owner-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Renames an object - `.name = newName`, a direct dot-assignable pseudo-
/// property (confirmed against `ToastStunt/src/execute.cc`'s `OP_PUT_PROP`
/// handling of the `.name` built-in - owner-or-wizard, blocked for player
/// objects unless wizard; the sidecar's connection is always a wizard, so
/// this is never actually blocked). `name:` is a real field in
/// `object.moo` (`FORMAT.md` §3), so this re-exports on success like any
/// other structural change.
let setName
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (newName: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let nameLit = "\"" + newName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; try {o}.name = {nameLit}; ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef "name" GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-name-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Toggles one of the inspector's flag badges. `flagName` is never
/// user-typed - it only ever arrives as one of seven fixed button labels
/// the client itself defines - so splicing it directly into the generated
/// statement is safe here the same way `setProperty`'s bare `.{pname}`
/// splice already relies on trusted input shape, not a new injection
/// surface. `.player` is *not* a dot-assignable built-in property
/// (confirmed against `execute.cc`'s built-in-property table, `db.h`) -
/// it's set via the dedicated `set_player_flag(obj, value)` builtin
/// instead, hence the one special case below. `flags:` is a real field in
/// `object.moo` (`FORMAT.md` §3), so this re-exports on success.
let setFlag
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (flagName: string)
    (value: bool)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let valueInt = if value then 1 else 0

        let assign =
            match flagName with
            | "player" -> $"""set_player_flag({o}, {valueInt})"""
            | _ -> $"""{o}.{flagName} = {valueInt}"""

        let statements = $"""ok = 0; errtext = ""; try {assign}; ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef flagName GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-flag-set-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Adds one parent to an object without disturbing its existing others -
/// this fork supports true multiple inheritance (`parents()`/`chparents()`,
/// confirmed against `ToastStunt/src/objects.cc`), but `chparents` always
/// takes the *complete* desired list, so this re-fetches the object's
/// current parents live and appends to them in the same eval rather than
/// trusting a possibly-stale client-side copy. `parentExpr` is evaluated
/// the same "any expression resolving to a valid object" way every other
/// object-expression field already is; `chparents` itself raises E_RECMOVE
/// on a cycle and E_INVARG on a property/verb name collision, both caught
/// below like any other failure. `parents:` is a real field in
/// `object.moo` (`FORMAT.md` §3), so this re-exports on success.
let addParent
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (parentExpr: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let exprLit = "\"" + parentExpr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; try presult = eval("return " + {exprLit} + ";"); if (presult[1]) try curr = parents({o}); chparents({o}, {{@curr, presult[2]}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef "parents" GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-parent-add-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Removes exactly one parent, leaving the object's other parents intact -
/// same "re-fetch the live list, compute the new one, `chparents` the
/// whole thing" approach as `addParent`, just filtering `parentRef` out
/// instead of appending.
let removeParent
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (parentRef: int64)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let p = sprintf "#%d" parentRef

        let statements =
            $"""ok = 0; errtext = ""; try curr = parents({o}); newlist = {{}}; for x in (curr) if (x != {p}) newlist = {{@newlist, x}}; endif endfor chparents({o}, newlist); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let! gitError = exportAndCommitObject config session objRef "parents" GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-parent-remove-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Adds `objRef` as one more parent of some *other* object - the same
/// `chparents` operation `addParent` performs, just initiated from this
/// object's own inspector instead of the child's. `childExpr` is evaluated
/// the same "any expression resolving to a valid object" way every other
/// object-expression field already is. Re-exports the *child*, not
/// `objRef` - the child's `object.moo` is what actually changed - so the
/// resolved child ref is threaded back out of the eval result first.
let addChild
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (childExpr: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let o = sprintf "#%d" objRef
        let exprLit = "\"" + childExpr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; child = #-1; try childResult = eval("return " + {exprLit} + ";"); if (childResult[1]) child = childResult[2]; try curr = parents(child); chparents(child, {{@curr, {o}}}); ok = 1; except err2 (ANY) errtext = tostr(err2[2]); endtry else errtext = "parse error"; endif except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext, "child" -> tostr(child)]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        let! diagnostics =
            task {
                if not ok then
                    return [ errtext ]
                else
                    let childRef = int64 (root.GetProperty("child").GetString().TrimStart('#'))
                    let! gitError = exportAndCommitObject config session childRef "parents" GitStore.Modified ct
                    return gitError |> Option.map (fun m -> [ "(changed, but git commit failed: " + m + ")" ]) |> Option.defaultValue []
            }

        do!
            sendWire
                webSocket
                (sprintf "moodev-child-add-result object: #%d ok: %d" objRef (if ok then 1 else 0))
                diagnostics
                ct
    }

/// Creates a new object - `create(parent, player)`. `parentExpr` is an
/// arbitrary MOO expression (`#5`, `$room`, ...) evaluated server-side, the
/// same "type a real MOO expression" convention `setProperty`'s value
/// input already uses, so any resolvable parent reference works, not just
/// a literal object number. Stays live-only (no export/commit) per
/// invariant I3 - the caller can separately register a corponym via the
/// existing add-property-on-`#0` flow if they want it versioned.
let createObject
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (parentExpr: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let exprLit = "\"" + parentExpr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; newobj = #-1; parentRef = #-1;
try
  presult = eval("return " + {exprLit} + ";");
  if (presult[1])
    parentRef = presult[2];
    try
      newobj = create(parentRef, player);
      ok = 1;
    except err2 (ANY)
      errtext = tostr(err2[2]);
    endtry
  else
    errtext = "parse error";
  endif
except err (ANY)
  errtext = tostr(err[2]);
endtry"""

        let! json =
            evalOnSession
                session
                statements
                """["ok" -> ok, "errtext" -> errtext, "newobj" -> tostr(newobj), "parent" -> tostr(parentRef)]"""
                ct

        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()
        let newobj = root.GetProperty("newobj").GetString()
        let parent = root.GetProperty("parent").GetString()

        do!
            sendWire
                webSocket
                (sprintf "moodev-object-create-result ok: %d newobj: %s parent: %s" (if ok then 1 else 0) newobj parent)
                (if ok then [] else [ errtext ])
                ct
    }

/// Formats a display label the same way `LanguageServer.Handlers`'s
/// (private) `displayNameFor` does - `"<name> (#N) [$corponym]"`, or without
/// the suffix if uncorponym'd, `"#N"` alone for a genuinely empty live name.
/// Duplicated here rather than shared (that function is private to a
/// different project, reads from the static graph, and this is a live
/// query) - same deliberate per-module duplication convention already used
/// for `Sidecar/TreeParser.fs` vs `Metadata/TreeFormat.fs`.
let private formatLiveName (corponymsByObjnum: Map<int64, string>) (objRef: int64) (liveName: string) : string =
    let baseName = if liveName = "" then sprintf "#%d" objRef else liveName

    match Map.tryFind objRef corponymsByObjnum with
    | Some propName -> sprintf "%s (#%d) [$%s]" baseName objRef propName
    | None -> sprintf "%s (#%d)" baseName objRef

/// Live objects can have an arbitrary number of children (a monster class
/// with hundreds of spawned instances) - this directly answers the concern
/// that motivated this whole feature (don't let the IDE choke trying to
/// browse into a world with huge numbers of runtime instances).
let private maxLiveChildren = 500

/// `get-live-children` replacement for the tree's expand action on a node
/// the static (corponym-only, see moo-vcs-plan.md I3) graph doesn't fully
/// cover. Returns `children(objRef)` (capped at `maxLiveChildren`) with
/// enough per-child structural summary - live name, parents, verb/property
/// signatures, no verb code or property values - to build a tree row
/// identical in shape to a statically-preloaded one. Deliberately not built
/// on `Exporter.getObjectExport`: that fetches full decompiled verb code and
/// serialized property values for every verb/property, the right cost for
/// an export/commit but wasteful for a tree-expand click (would mean
/// decompiling every verb on every live instance of a monster class just to
/// show a name and a chevron) - this mirrors the lighter level of detail
/// `Handlers.ObjectTreeVerb`/`ObjectTreeProperty` already use for the same
/// purpose. Also, `getObjectExport` has no notion of `children()` at all -
/// the static graph's `Children` is inferred by inverting `Parents` across
/// the whole *loaded* set, which doesn't work for a partial live query.
let getLiveChildren
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let evalRunner = evalOnSession session
        let o = sprintf "#%d" objRef

        let statements =
            $"""if (!valid({o}))
  result = ["error" -> "invalid"];
else
  allkids = children({o});
  total = length(allkids);
  kids = (total > {maxLiveChildren}) ? allkids[1..{maxLiveChildren}] | allkids;
  out = {{}};
  for k in (kids)
    kname = typeof(k.name) == STR ? k.name | "";
    kparents = {{}};
    for p in (parents(k)) kparents = {{@kparents, tostr(p)}}; endfor
    kverbs = {{}};
    vlist = verbs(k);
    for i in [1..length(vlist)]
      vi = verb_info(k, i);
      va = verb_args(k, i);
      kverbs = {{@kverbs, ["names" -> vi[3], "perms" -> vi[2], "dobj" -> va[1], "prep" -> va[2], "iobj" -> va[3]]}};
    endfor
    kprops = {{}};
    for pn in (properties(k))
      pi = property_info(k, pn);
      kprops = {{@kprops, ["name" -> pn, "perms" -> pi[2]]}};
    endfor
    out = {{@out, ["objref" -> tostr(k), "name" -> kname, "parents" -> kparents, "verbs" -> kverbs, "properties" -> kprops]}};
  endfor
  result = ["kids" -> out, "truncated" -> ((total > {maxLiveChildren}) ? 1 | 0)];
endif"""

        let! json = evalRunner statements "result" ct
        let root = json.RootElement
        let hasError, _ = root.TryGetProperty("error")

        if hasError then
            do! sendWire webSocket (sprintf "moodev-live-children object: #%d truncated: 0" objRef) [] ct
        else
            let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct
            let truncated = root.GetProperty("truncated").GetInt32() = 1

            let firstAlias (nameSpec: string) =
                nameSpec.Split(' ') |> Array.tryHead |> Option.defaultValue nameSpec

            let lines =
                root.GetProperty("kids").EnumerateArray()
                |> Seq.map (fun k ->
                    let kObjRef = int64 (k.GetProperty("objref").GetString().TrimStart('#'))
                    let liveName = k.GetProperty("name").GetString()
                    let displayName = formatLiveName corponymsByObjnum kObjRef liveName

                    let parents =
                        k.GetProperty("parents").EnumerateArray()
                        |> Seq.map (fun p -> int64 (p.GetString().TrimStart('#')))
                        |> Array.ofSeq

                    let verbs =
                        k.GetProperty("verbs").EnumerateArray()
                        |> Seq.map (fun v ->
                            {| name = firstAlias (v.GetProperty("names").GetString())
                               perms = v.GetProperty("perms").GetString()
                               dobj = v.GetProperty("dobj").GetString()
                               prep = v.GetProperty("prep").GetString()
                               iobj = v.GetProperty("iobj").GetString() |})
                        |> Array.ofSeq

                    let properties =
                        k.GetProperty("properties").EnumerateArray()
                        |> Seq.map (fun p ->
                            {| name = p.GetProperty("name").GetString()
                               perms = p.GetProperty("perms").GetString() |})
                        |> Array.ofSeq

                    JsonSerializer.Serialize(
                        {| objRef = kObjRef
                           name = displayName
                           parents = parents
                           verbs = verbs
                           properties = properties |}
                    ))
                |> List.ofSeq

            do!
                sendWire
                    webSocket
                    (sprintf "moodev-live-children object: #%d truncated: %d" objRef (if truncated then 1 else 0))
                    lines
                    ct
    }

/// Same cap reasoning as `maxLiveChildren` - a sane bound on the *result*,
/// not on the scan itself (the scan below must walk every valid object
/// number up to `max_object()` to find every parentless one; there's no way
/// to shortcut that in a `parent(o)`-per-object data model like MOO's).
let private maxLiveRoots = 500

/// `get-live-roots` - the counterpart to `getLiveChildren` for the tree's
/// *top level*. `rootRefs` (the client's set of tree entry points) is
/// computed once from the static corponym export at load time, and the only
/// way a live object ever joins the tree afterward is by being discovered as
/// a child of an already-known node (`getLiveChildren`, on an expand click).
/// A parentless live object (confirmed live: the LSP's own dedicated `#4`/
/// `#5` bootstrap objects, see moo-dev/CLAUDE.md's "LSP service character +
/// listener" section) has no such node to be discovered from - not because
/// of anything special about its object number, but because nothing in the
/// tree's design ever asks "what else has no parent?" after the initial
/// load. This does exactly that: scans every valid object number for
/// `length(parents(o)) == 0`, and returns the same per-object structural
/// summary `getLiveChildren` already builds for a single object's children.
let getLiveRoots (config: Config) (session: Session) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        let evalRunner = evalOnSession session

        let statements =
            $"""total = 0;
out = {{}};
for i in [0..toint(max_object())]
  o = toobj(i);
  if (valid(o) && length(parents(o)) == 0)
    total = total + 1;
    if (total <= {maxLiveRoots})
      oname = typeof(o.name) == STR ? o.name | "";
      overbs = {{}};
      vlist = verbs(o);
      for j in [1..length(vlist)]
        vi = verb_info(o, j);
        va = verb_args(o, j);
        overbs = {{@overbs, ["names" -> vi[3], "perms" -> vi[2], "dobj" -> va[1], "prep" -> va[2], "iobj" -> va[3]]}};
      endfor
      oprops = {{}};
      for pn in (properties(o))
        pi = property_info(o, pn);
        oprops = {{@oprops, ["name" -> pn, "perms" -> pi[2]]}};
      endfor
      out = {{@out, ["objref" -> tostr(o), "name" -> oname, "verbs" -> overbs, "properties" -> oprops]}};
    endif
  endif
endfor
result = ["roots" -> out, "truncated" -> ((total > {maxLiveRoots}) ? 1 | 0)];"""

        let! json = evalRunner statements "result" ct
        let root = json.RootElement
        let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct
        let truncated = root.GetProperty("truncated").GetInt32() = 1

        let firstAlias (nameSpec: string) =
            nameSpec.Split(' ') |> Array.tryHead |> Option.defaultValue nameSpec

        let lines =
            root.GetProperty("roots").EnumerateArray()
            |> Seq.map (fun r ->
                let rObjRef = int64 (r.GetProperty("objref").GetString().TrimStart('#'))
                let liveName = r.GetProperty("name").GetString()
                let displayName = formatLiveName corponymsByObjnum rObjRef liveName

                let verbs =
                    r.GetProperty("verbs").EnumerateArray()
                    |> Seq.map (fun v ->
                        {| name = firstAlias (v.GetProperty("names").GetString())
                           perms = v.GetProperty("perms").GetString()
                           dobj = v.GetProperty("dobj").GetString()
                           prep = v.GetProperty("prep").GetString()
                           iobj = v.GetProperty("iobj").GetString() |})
                    |> Array.ofSeq

                let properties =
                    r.GetProperty("properties").EnumerateArray()
                    |> Seq.map (fun p ->
                        {| name = p.GetProperty("name").GetString()
                           perms = p.GetProperty("perms").GetString() |})
                    |> Array.ofSeq

                JsonSerializer.Serialize(
                    {| objRef = rObjRef
                       name = displayName
                       parents = Array.empty<int64>
                       verbs = verbs
                       properties = properties |}
                ))
            |> List.ofSeq

        do! sendWire webSocket (sprintf "moodev-live-roots truncated: %d" (if truncated then 1 else 0)) lines ct
    }

/// `get-tasks` - every forked/suspended/reading task (`queued_tasks()`,
/// confirmed against `ToastStunt/src/tasks.cc`). Deliberately drops that
/// list's 3rd/4th elements - both are dead placeholders from an old
/// clock-based scheduler (`/* OBSOLETE */` in the source itself), not real
/// tick/seconds usage; there is no builtin anywhere in ToastStunt that
/// reports per-task cumulative tick/second consumption, only the *current*
/// task's own remaining budget (`ticks_left()`/`seconds_left()`). Getting
/// real per-task usage would need a new C-side patch (tracked as a vault
/// follow-up card, not attempted here).
let getTasks (config: Config) (session: Session) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        let evalRunner = evalOnSession session

        let statements =
            """out = {};
for t in (queued_tasks())
  out = {@out, ["id" -> t[1], "start" -> t[2], "programmer" -> tostr(t[5]), "vloc" -> tostr(t[6]), "verb" -> t[7], "line" -> t[8], "this" -> tostr(t[9]), "bytes" -> t[10]]};
endfor
result = out;"""

        let! json = evalRunner statements "result" ct
        let root = json.RootElement
        let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct

        let refDisplay (refText: string) =
            let refNum = int64 (refText.TrimStart('#'))
            formatLiveName corponymsByObjnum refNum "", refNum

        let lines =
            root.EnumerateArray()
            |> Seq.map (fun t ->
                let programmerName, programmerRef = refDisplay (t.GetProperty("programmer").GetString())
                let vlocName, vlocRef = refDisplay (t.GetProperty("vloc").GetString())
                let thisName, thisRef = refDisplay (t.GetProperty("this").GetString())

                JsonSerializer.Serialize(
                    {| id = t.GetProperty("id").GetInt64()
                       start = t.GetProperty("start").GetInt64()
                       programmerRef = programmerRef
                       programmer = programmerName
                       vlocRef = vlocRef
                       vloc = vlocName
                       verb = t.GetProperty("verb").GetString()
                       line = t.GetProperty("line").GetInt64()
                       thisRef = thisRef
                       ``this`` = thisName
                       bytes = t.GetProperty("bytes").GetInt64() |}
                ))
            |> List.ofSeq

        do! sendWire webSocket "moodev-tasks" lines ct
    }

/// `get-server-status` - every currently-bound listener (`listeners()`,
/// confirmed against `ToastStunt/src/server.cc:3210-3240` - already returns
/// a list of maps keyed `"object"`/`"port"`/`"interface"`/`"TLS"` per
/// listener, zero new C-side work needed). Same "wrap the raw eval result
/// into JSON-safe fields, one line per entry" shape `getTasks` above uses -
/// `"object"` is `tostr()`'d before serializing for the same reason
/// `getTasks` does it for its own obj-typed fields (a raw OBJ value isn't
/// JSON-safe as-is). Room to grow with other live signals later (connected
/// player count, uptime) without changing this response shape - not
/// attempted here, matching the card's own framing.
let getServerStatus (config: Config) (session: Session) (webSocket: WebSocket) (ct: CancellationToken) : Task<unit> =
    task {
        let statements =
            """out = {};
for l in (listeners())
  out = {@out, ["object" -> tostr(l["object"]), "port" -> l["port"], "interface" -> l["interface"], "tls" -> l["TLS"]]};
endfor
result = out;"""

        let! json = evalOnSession session statements "result" ct
        let root = json.RootElement

        let lines =
            root.EnumerateArray()
            |> Seq.map (fun l ->
                JsonSerializer.Serialize(
                    {| objRef = int64 ((l.GetProperty("object").GetString()).TrimStart('#'))
                       port = l.GetProperty("port").GetInt64()
                       interfaceName = l.GetProperty("interface").GetString()
                       tls = l.GetProperty("tls").GetInt32() = 1 |}
                ))
            |> List.ofSeq

        do! sendWire webSocket "moodev-server-status" lines ct
    }

type PropertyLiteralParse =
    | ListLiteral of string list
    | MapLiteral of (string * string) list
    | NotAListOrMap

/// A property's raw value text comes from `toliteral()` (see `getProperties`
/// above) - the printed form of an already-evaluated runtime value, never
/// re-typed source code - so it can only ever contain literal scalars and
/// literal-nested lists/maps, never an identifier, operator, splice, or call.
/// This renders exactly that closed set back to literal text; anything else
/// (which can only arise if the user hand-typed a non-literal expression into
/// the raw input before ever toggling structured mode) makes the *whole*
/// value fall back to `NotAListOrMap` rather than rendering a lossy partial
/// row - there's no original source span to fall back to for a non-literal
/// element, so silently reconstructing "the parts we understood" would risk
/// discarding the parts we didn't on the next save.
let rec private literalText (e: Expr) : string option =
    match e with
    | IntLit n -> Some(string n)
    | FloatLit f ->
        // `string f` alone drops the decimal point for whole-number floats
        // (.NET's default double->string, e.g. `1.0` -> "1") - fine for the
        // read-only hover rendering `LanguageServer/Handlers.fs`'s own
        // `exprBrief` uses this same shape for, but not here: this text can
        // be resubmitted through `set-property`'s `eval()`, where a bare "1"
        // parses as an INT literal, silently changing the property's type.
        let s = string f
        Some(if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0")
    | StrLit s -> Some(sprintf "\"%s\"" (s.Replace("\\", "\\\\").Replace("\"", "\\\"")))
    | ObjLit n -> Some(sprintf "#%d" n)
    | ErrLit s -> Some s
    | Unary(Neg, inner) -> literalText inner |> Option.map (sprintf "-%s")
    | ListLit args ->
        args
        |> List.map (function
            | Normal e -> literalText e
            | Splice _ -> None)
        |> sequenceAll
        |> Option.map (String.concat ", " >> sprintf "{%s}")
    | MapLit pairs ->
        pairs
        |> List.map (fun (k, v) ->
            match literalText k, literalText v with
            | Some kt, Some vt -> Some(kt + " -> " + vt)
            | _ -> None)
        |> sequenceAll
        |> Option.map (String.concat ", " >> sprintf "[%s]")
    | _ -> None

and private sequenceAll (xs: string option list) : string list option =
    if xs |> List.forall Option.isSome then Some(xs |> List.map Option.get) else None

/// Parses a property's raw MOO-literal value text as a list or map literal,
/// for the client's structured property editor toggle. Lexes/parses
/// `"return " + valueText + ";"` - the same "eval as a return statement"
/// trick `saveVerb`/hover already lean on elsewhere in this codebase,
/// applied to *parsing* instead of *evaluating* - and matches the single
/// resulting `Return(Some(ListLit args))`/`Return(Some(MapLit pairs))`.
let parsePropertyLiteral (valueText: string) : PropertyLiteralParse =
    let lexResult = Language.Lexer.tokenize ("return " + valueText + ";")

    match lexResult.Error with
    | Some _ -> NotAListOrMap
    | None ->
        let stmts = Language.Parser.parse lexResult.Tokens

        if countErrors stmts > 0 then
            NotAListOrMap
        else
            match stmts with
            | [ Return(Some(ListLit args)) ] ->
                match
                    args
                    |> List.map (function
                        | Normal e -> literalText e
                        | Splice _ -> None)
                    |> sequenceAll
                with
                | Some texts -> ListLiteral texts
                | None -> NotAListOrMap
            | [ Return(Some(MapLit pairs)) ] ->
                let rendered =
                    pairs
                    |> List.map (fun (k, v) ->
                        match literalText k, literalText v with
                        | Some kt, Some vt -> Some(kt, vt)
                        | _ -> None)

                if rendered |> List.forall Option.isSome then
                    MapLiteral(rendered |> List.map Option.get)
                else
                    NotAListOrMap
            | _ -> NotAListOrMap

let parsePropertyLiteralAction
    (webSocket: WebSocket)
    (objRef: int64)
    (pname: string)
    (valueText: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let json =
            match parsePropertyLiteral valueText with
            | ListLiteral texts -> JsonSerializer.Serialize({| kind = "list"; elements = texts |})
            | MapLiteral pairs ->
                let elements = pairs |> List.map (fun (k, v) -> {| key = k; value = v |})
                JsonSerializer.Serialize({| kind = "map"; elements = elements |})
            | NotAListOrMap -> JsonSerializer.Serialize({| kind = "none" |})

        do!
            sendWire
                webSocket
                (sprintf "moodev-property-literal-parsed object: #%d name: %s" objRef pname)
                [ json ]
                ct
    }

/// Fixed name for the hidden scratch verb `checkVerbSyntax` compiles
/// candidate code against - never the real verb being edited, and never
/// exported/committed. A single space-free name (not multi-word) so the
/// existing `resolveVerbIndexStatements` alias-matching helper can find it
/// with a plain `in` check, same as every other verb lookup in this file.
let private syntaxCheckScratchVerbName = "moodev_syntax_check_scratch"

/// Builds `checkVerbSyntax`'s eval statements - split out from the
/// function itself purely so a unit test can assert the concatenated
/// fragments are correctly separated (this exact shape broke once already:
/// two `resolveVerbIndexStatements` calls glued directly against a
/// no-trailing-space fragment produced the single malformed token
/// `endifvlist`, which fails to compile - and since the *whole* eval
/// (including its own trailing tag/notify epilogue) is one MOO statement
/// sequence, that compile failure meant no response ever came back at
/// all, not a visible error - live-verification found it as an indefinite
/// hang, not a compile message, so this seemed worth guarding structurally
/// rather than trusting spacing-by-eye alone next time this is touched).
let buildCheckVerbSyntaxStatements (code: string list) : string =
    let verbLit = "\"" + syntaxCheckScratchVerbName + "\""
    let codeLiteral = "{" + (code |> List.map (fun l -> "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") |> String.concat ", ") + "}"

    resolveVerbIndexStatements "#0" verbLit
    + $""" if (idx == 0) try add_verb(#0, {{#0, "rxd", {verbLit}}}, {{"this", "none", "this"}}); except err (ANY) endtry endif """
    + resolveVerbIndexStatements "#0" verbLit
    + $""" errs = (idx == 0) ? {{"could not create scratch verb for syntax check"}} | set_verb_code(#0, idx, {codeLiteral});"""

/// Live-diagnostics compile probe: compiles `code` (the editor's *current,
/// unsaved* text) against a dedicated, hidden scratch verb on `#0` -
/// lazily created once (checked via the same `resolveVerbIndexStatements`
/// idx-resolution helper `saveVerb`/`deleteVerb` already use, added if
/// missing - the same `#0`-owned-bootstrap-verb convention this project's
/// own login/eval-shim verbs already rely on), reused thereafter. Returns
/// whatever real compile errors `set_verb_code()` reports - genuine
/// ToastStunt compiler feedback, not a second MOOcode compiler
/// reimplemented client-side. Never touches the real verb/tree: no export,
/// no git commit, no `moodev-edit-result`-shaped response.
let checkVerbSyntax
    (session: Session)
    (webSocket: WebSocket)
    (objRef: int64)
    (verbName: string)
    (code: string list)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let! json = evalOnSession session (buildCheckVerbSyntaxStatements code) "errs" ct
        let errors = json.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

        do! sendWire webSocket (sprintf "moodev-verb-syntax-check-result object: #%d verb: %s" objRef verbName) errors ct
    }

/// `kill-task {task}` - `kill_task(id)`, wizard-eval'd so it always has
/// permission regardless of the task's own owner.
let killTask (webSocket: WebSocket) (session: Session) (taskId: int64) (ct: CancellationToken) : Task<unit> =
    task {
        let statements =
            $"""ok = 0; errtext = ""; try kill_task({taskId}); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let errtext = root.GetProperty("errtext").GetString()

        do!
            sendWire
                webSocket
                (sprintf "moodev-kill-task-result task: %d ok: %d" taskId (if ok then 1 else 0))
                (if ok then [] else [ errtext ])
                ct
    }

/// The "Eval scratchpad" panel's one action: evaluates an arbitrary,
/// caller-typed MOO expression and reports its value, independent of
/// notify()-based terminal output (unlike the Game tab's own command
/// input, which is raw terminal pass-through with no structured response).
/// Same `eval("return " + <literal> + ";")` precedent `setProperty` already
/// uses for an arbitrary expression string, over the browser's own session
/// (`evalOnSession`, not a new `MooEval` connection - a second wizard login
/// would kick this very session, exactly the bug the "Configurable MOO
/// server target" feature's own `reconfigure-target` action hit and fixed).
/// Reports the value via `tostr()`, not `generate_json()` - some MOO value
/// types (WAIF, ANON) aren't safely JSON-renderable, while `tostr()` never
/// throws and reads as the same literal syntax MOO programmers already
/// write, a better fit for "show me the value" than forcing a JSON tree.
let evalScratchpad (session: Session) (webSocket: WebSocket) (expr: string) (ct: CancellationToken) : Task<unit> =
    task {
        let exprLit = "\"" + expr.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

        let statements =
            $"""ok = 0; errtext = ""; resulttext = "";
try
  result = eval("return " + {exprLit} + ";");
  if (result[1])
    resulttext = tostr(result[2]);
    ok = 1;
  else
    errtext = "parse error";
  endif
except err (ANY)
  errtext = tostr(err[2]);
endtry"""

        let! json = evalOnSession session statements """["ok" -> ok, "result" -> resulttext, "errtext" -> errtext]""" ct
        let root = json.RootElement
        let ok = root.GetProperty("ok").GetInt32() = 1
        let resultText = root.GetProperty("result").GetString()
        let errtext = root.GetProperty("errtext").GetString()

        do!
            sendWire
                webSocket
                (sprintf "moodev-scratchpad-result ok: %d" (if ok then 1 else 0))
                [ (if ok then resultText else errtext) ]
                ct
    }

/// The inspector's sole source of structural data (owner, flags,
/// parents/children, verbs, properties) - always live, never a static
/// export, so it reflects edits made moments ago. Owner/parent/child refs
/// each get their own live-name lookup (a live-only object's ancestors can
/// themselves be either corponym'd or not), same `formatLiveName`
/// convention used throughout this feature. No verb code, no property
/// values - same reasoning as `getLiveChildren`.
///
/// `verbs`/`properties` include every ancestor's own entries too, not just
/// `objRef`'s - walked breadth-first via `parents(objRef)` (a visited-list
/// guard, since the object graph is a DAG and a shared ancestor must only
/// be counted once), nearest ancestor first, `objRef`'s own entries last.
/// Each entry carries `definerRef`/`definerName` - the object it's actually
/// defined on, `objRef` itself for an "own" entry - kept distinct from the
/// existing `ownerRef`/`ownerName` (the unrelated verb/property permission
/// owner). Not deduplicated against MOO's real verb-dispatch precedence: a
/// verb name shadowed by a closer definition still shows every ancestor's
/// copy, each correctly tagged with its own definer, rather than only the
/// one that would actually execute - replicating exact dispatch precedence
/// is out of scope here.
let getLiveInfo (config: Config) (session: Session) (webSocket: WebSocket) (objRef: int64) (ct: CancellationToken) : Task<unit> =
    task {
        let evalRunner = evalOnSession session
        let o = sprintf "#%d" objRef

        let statements =
            $"""if (!valid({o}))
  result = ["error" -> "invalid"];
else
  live_name = typeof({o}.name) == STR ? {o}.name | "";
  alias_list = {{}};
  try
    if (typeof({o}.aliases) == LIST)
      for a in ({o}.aliases)
        if (typeof(a) == STR)
          alias_list = {{@alias_list, a}};
        endif
      endfor
    endif
  except (E_PROPNF)
  endtry
  ownername = valid({o}.owner) ? (typeof({o}.owner.name) == STR ? {o}.owner.name | "") | "";
  parents_out = {{}};
  for p in (parents({o}))
    pname = valid(p) ? (typeof(p.name) == STR ? p.name | "") | "";
    parents_out = {{@parents_out, ["objref" -> tostr(p), "name" -> pname]}};
  endfor
  children_out = {{}};
  for c in (children({o}))
    cname = valid(c) ? (typeof(c.name) == STR ? c.name | "") | "";
    children_out = {{@children_out, ["objref" -> tostr(c), "name" -> cname]}};
  endfor
  ancestor_visited = {{}};
  queue = parents({o});
  chain = {{}};
  while (length(queue) > 0)
    p = queue[1];
    queue = listdelete(queue, 1);
    if (valid(p) && !(p in ancestor_visited))
      ancestor_visited = {{@ancestor_visited, p}};
      chain = {{@chain, p}};
      for gp in (parents(p))
        queue = {{@queue, gp}};
      endfor
    endif
  endwhile
  chain = {{@chain, {o}}};
  verbs_out = {{}};
  props_out = {{}};
  for x in (chain)
    xname = typeof(x.name) == STR ? x.name | "";
    vlist = verbs(x);
    for i in [1..length(vlist)]
      vi = verb_info(x, i);
      va = verb_args(x, i);
      vowner = vi[1];
      vownername = valid(vowner) ? (typeof(vowner.name) == STR ? vowner.name | "") | "";
      verbs_out = {{@verbs_out, ["names" -> vi[3], "perms" -> vi[2], "owner" -> tostr(vowner), "ownername" -> vownername, "dobj" -> va[1], "prep" -> va[2], "iobj" -> va[3], "definer" -> tostr(x), "definername" -> xname]}};
    endfor
    for pn in (properties(x))
      pi = property_info(x, pn);
      powner = pi[1];
      pownername = valid(powner) ? (typeof(powner.name) == STR ? powner.name | "") | "";
      props_out = {{@props_out, ["name" -> pn, "owner" -> tostr(powner), "ownername" -> pownername, "perms" -> pi[2], "definer" -> tostr(x), "definername" -> xname]}};
    endfor
  endfor
  connplayername = valid(player) ? (typeof(player.name) == STR ? player.name | "") | "";
  result = ["name" -> live_name, "aliases" -> alias_list, "owner" -> tostr({o}.owner), "ownername" -> ownername,
            "player" -> is_player({o}), "programmer" -> {o}.programmer, "wizard" -> {o}.wizard,
            "read" -> {o}.r, "write" -> {o}.w, "fertile" -> {o}.f, "anonymous" -> {o}.a,
            "parents" -> parents_out, "children" -> children_out, "verbs" -> verbs_out, "properties" -> props_out,
            "connectedPlayer" -> tostr(player), "connectedPlayerName" -> connplayername];
endif"""

        let! json = evalRunner statements "result" ct
        let root = json.RootElement
        let hasError, _ = root.TryGetProperty("error")

        if hasError then
            do! sendWire webSocket (sprintf "moodev-live-info object: #%d" objRef) [] ct
        else
            let! corponymsByObjnum = Exporter.getCorponyms evalRunner ct

            let refOf (objref: string) (name: string) =
                let r = int64 (objref.TrimStart('#'))
                {| objRef = r; name = formatLiveName corponymsByObjnum r name |}

            let firstAlias (nameSpec: string) =
                nameSpec.Split(' ') |> Array.tryHead |> Option.defaultValue nameSpec

            // MOO has no real boolean type - `is_player()`/`.programmer`/
            // `.wizard`/`.r`/`.w`/`.f`/`.a` are all plain integers (0/1),
            // which the eval bridge round-trips as JSON numbers, not JSON
            // booleans - `GetBoolean()` throws on a Number-kind element
            // (confirmed live: this crashed the whole connection before a
            // response was ever sent, the same "silent hang" class of bug
            // `Exporter.getObjectExport`'s own doc comment warns about,
            // just via a different mechanism). Read as int and compare to 1
            // instead, so the wire payload still carries a genuine JSON
            // boolean for `renderInspectorStructure`'s `(info?xxx: bool)`
            // reads on the client side.
            let flag (name: string) = root.GetProperty(name).GetInt32() = 1

            let connectedPlayerRef = int64 (root.GetProperty("connectedPlayer").GetString().TrimStart('#'))

            let connectedPlayerDisplay =
                formatLiveName corponymsByObjnum connectedPlayerRef (root.GetProperty("connectedPlayerName").GetString())

            let payload =
                {| name = formatLiveName corponymsByObjnum objRef (root.GetProperty("name").GetString())
                   // The raw `.name` value (often empty for an unnamed
                   // object) - unlike `name` above, not run through
                   // `formatLiveName`, since the rename widget needs to
                   // prefill with what's actually assignable back to
                   // `.name`, not a display string like `"#6 (#6)"`.
                   rawName = root.GetProperty("name").GetString()
                   owner = refOf (root.GetProperty("owner").GetString()) (root.GetProperty("ownername").GetString())
                   connectedPlayerRef = connectedPlayerRef
                   connectedPlayerDisplay = connectedPlayerDisplay
                   aliases = root.GetProperty("aliases").EnumerateArray() |> Seq.map (fun a -> a.GetString()) |> Array.ofSeq
                   player = flag "player"
                   programmer = flag "programmer"
                   wizard = flag "wizard"
                   read = flag "read"
                   write = flag "write"
                   fertile = flag "fertile"
                   anonymous = flag "anonymous"
                   parents =
                     root.GetProperty("parents").EnumerateArray()
                     |> Seq.map (fun p -> refOf (p.GetProperty("objref").GetString()) (p.GetProperty("name").GetString()))
                     |> Array.ofSeq
                   children =
                     root.GetProperty("children").EnumerateArray()
                     |> Seq.map (fun c -> refOf (c.GetProperty("objref").GetString()) (c.GetProperty("name").GetString()))
                     |> Array.ofSeq
                   verbs =
                     root.GetProperty("verbs").EnumerateArray()
                     |> Seq.map (fun v ->
                         let vOwnerRef = int64 (v.GetProperty("owner").GetString().TrimStart('#'))
                         let vOwnerName = v.GetProperty("ownername").GetString()
                         let definerRef = int64 (v.GetProperty("definer").GetString().TrimStart('#'))
                         let definerName = v.GetProperty("definername").GetString()

                         {| name = firstAlias (v.GetProperty("names").GetString())
                            // The complete, un-truncated name-spec (e.g.
                            // "look l") - unlike `name` above (first alias
                            // only, kept as-is since resolve-by-alias call
                            // sites depend on it), the rename editor needs
                            // the whole thing to prefill, or renaming would
                            // silently drop every alias but the first.
                            fullNames = v.GetProperty("names").GetString()
                            owner = formatLiveName corponymsByObjnum vOwnerRef vOwnerName
                            ownerRef = vOwnerRef
                            perms = v.GetProperty("perms").GetString()
                            dobj = v.GetProperty("dobj").GetString()
                            prep = v.GetProperty("prep").GetString()
                            iobj = v.GetProperty("iobj").GetString()
                            // The object this verb is actually defined on -
                            // `objRef` itself for an "own" verb, an
                            // ancestor's ref otherwise (see this function's
                            // own doc comment).
                            definerRef = definerRef
                            definerName = formatLiveName corponymsByObjnum definerRef definerName |})
                     |> Array.ofSeq
                   properties =
                     root.GetProperty("properties").EnumerateArray()
                     |> Seq.map (fun p ->
                         let ownerRef = int64 (p.GetProperty("owner").GetString().TrimStart('#'))
                         let ownerName = p.GetProperty("ownername").GetString()
                         let definerRef = int64 (p.GetProperty("definer").GetString().TrimStart('#'))
                         let definerName = p.GetProperty("definername").GetString()

                         {| name = p.GetProperty("name").GetString()
                            owner = formatLiveName corponymsByObjnum ownerRef ownerName
                            definerRef = definerRef
                            definerName = formatLiveName corponymsByObjnum definerRef definerName
                            ownerRef = ownerRef
                            perms = p.GetProperty("perms").GetString() |})
                     |> Array.ofSeq |}

            do! sendWire webSocket (sprintf "moodev-live-info object: #%d" objRef) [ JsonSerializer.Serialize(payload) ] ct
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

        // #0 (System Object) is always versioned regardless of corponym -
        // FORMAT.md §1's exception, directory "0" - matching
        // `exportAndCommitObject`'s own special case. Without this, a #0
        // verb saves and commits fine (that path already handles it) but
        // history/search could never find it again: #0 never appears in
        // `corponymsByObjnum`, so the lookup below would always report
        // "not tracked" for it.
        let dirNameOpt = if objRef = 0L then Some "0" else Map.tryFind objRef corponymsByObjnum

        match dirNameOpt with
        | None -> return None
        | Some dirName ->
            let! dataOpt = Exporter.getObjectExport evalRunner objRef ct

            match dataOpt with
            | None -> return None
            | Some data ->
                let verbFileNames = Exporter.assignVerbFileNames data.Verbs

                match verbFileNames |> List.tryFind (fun (v, _) -> v.Names.Split(' ') |> Array.contains verbName) with
                | None -> return None
                | Some(_, fileName) ->
                    return Some(dirName, System.IO.Path.Combine("objects", dirName, "verbs", fileName).Replace('\\', '/'))
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

/// `search-content {query}` - "find this string in the live tree right
/// now," next to `searchHistory`'s "find it somewhere in history": reads
/// `config.TreeDir`'s working-copy files directly rather than walking git
/// history at all - there's exactly one snapshot ("now") to search, no
/// commits to enumerate, so this is simpler than `searchHistory` despite
/// searching similar content. `moodev-content-search-result` lines =
/// `objnum<TAB>corponym<TAB>label<TAB>matchingLineText` - one line per
/// matching source line, not per file (matches `searchHistory`'s own
/// one-result-per-hit granularity). `corponyms.moo` is excluded, same
/// reasoning as `searchHistory` (`corponym-history` covers that file on its
/// own terms). Empty `objnum` means the corponym no longer resolves live -
/// not clickable, same convention `searchHistory` already uses.
let searchContent
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (query: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let! corponymsByObjnum = Exporter.getCorponyms (evalOnSession session) ct
        let objnumByCorponym = corponymsByObjnum |> Map.toList |> List.map (fun (n, name) -> name, n) |> Map.ofList

        let queryLower = query.ToLowerInvariant()

        let lines =
            System.IO.Directory.GetFiles(config.TreeDir, "*.moo", System.IO.SearchOption.AllDirectories)
            |> Array.toList
            |> List.collect (fun filePath ->
                let relativePath =
                    System.IO.Path.GetRelativePath(config.TreeDir, filePath).Replace('\\', '/')

                if relativePath = "corponyms.moo" then
                    []
                else
                    match Exporter.describePath relativePath with
                    | None -> []
                    | Some(corponym, label) ->
                        let objnumText =
                            Map.tryFind corponym objnumByCorponym
                            |> Option.map (sprintf "%d")
                            |> Option.defaultValue ""

                        System.IO.File.ReadAllLines(filePath)
                        |> Array.filter (fun line -> line.ToLowerInvariant().Contains(queryLower))
                        |> Array.toList
                        |> List.map (fun line -> sprintf "%s\t%s\t%s\t%s" objnumText corponym label line))

        do! sendWire webSocket "moodev-content-search-result" lines ct
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
