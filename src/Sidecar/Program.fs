module Sidecar.Program

open System
open System.IO
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open LibGit2Sharp
open Sidecar.BridgeHandler

/// Everything that identifies "which MOO world" the sidecar's live
/// WebSocket bridge currently points at - see `currentTarget` (the always-on
/// bridge's mutable copy of this, reconfigurable via `"reconfigure-target"`).
type private MooTarget =
    { Host: string
      Port: int
      LspBridgePort: int
      TreeDir: string }

/// The sidecar's one shared, live-reconfigurable connection target - read
/// fresh by `buildTryDispatch`'s `"reconfigure-target"`/`"get-moo-target"`
/// arms and by every new `/ws`/`/lsp-bridge` connection accepted in `main`
/// below, instead of a value fixed for the whole process lifetime. Starts
/// out holding hardcoded placeholder defaults - `main` overwrites it with
/// the real `Moo:*` config values the moment `app.Configuration` exists,
/// before any connection can be accepted.
let mutable private currentTarget: MooTarget =
    { Host = "127.0.0.1"
      Port = 7777
      LspBridgePort = 7780
      TreeDir = "../../../Survive" }

/// `export <outputDir> [host] [port] [--user <name>]` runs the Phase 1
/// exporter (`Sidecar.Exporter`) against a wizard connection and exits - a
/// one-shot batch tool, distinct from the always-on WebSocket bridge below.
/// Host/port default to the same values `appsettings.json` gives the bridge
/// (127.0.0.1:7777); pass an explicit host/port to target e.g. the headless
/// test instance on 7778 instead. `--user` defaults to `"wizard"` (a bare
/// `Minimal.db` world's own unaccounted default, per `MooEval.connect`'s own
/// comment) - a real, separately-accounted world needs its actual
/// (passwordless) wizard-equivalent character name here instead, confirmed
/// live against a real HellMOO-derived world where the bare "wizard" login
/// silently never authenticated, leaving the connection to sit until
/// ToastStunt's own 300-second not-logged-in timeout killed it.
let private runExport (outputDir: string) (host: string) (port: int) (username: string) : int =
    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port username cts.Token

            try
                do! Exporter.exportTree conn outputDir cts.Token
            finally
                MooEval.disconnect conn
        }

    work.GetAwaiter().GetResult()
    0

/// `import <treeDir> [host] [port] [--user <name>] [--apply]` reads a tree
/// written by `export` and applies it to the target. Dry-run (prints the
/// planned operations, `Importer.describePlan`) unless `--apply` is passed -
/// the first component in this project that writes to a live MOO, so it
/// defaults to safe/preview-only per the Phase 2 plan's decision #5. `--user`
/// - see `runExport`'s own comment.
let private runImport (treeDir: string) (host: string) (port: int) (username: string) (apply: bool) : int =
    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port username cts.Token

            try
                let corponyms, objects = TreeParser.parseTree treeDir
                let! plan = Importer.planImport conn corponyms objects cts.Token

                printfn "%s" (Importer.describePlan plan)

                if apply then
                    let! compileResult = Importer.compileCheck conn plan cts.Token

                    match compileResult with
                    | Error errors ->
                        printfn ""
                        printfn "Compile check failed - aborting, nothing applied:"

                        for corponym, msg in errors do
                            printfn "  $%s: %s" corponym msg
                    | Ok() ->
                        do! Importer.applyPlan conn plan cts.Token
                        printfn ""
                        printfn "Applied."
                else
                    printfn ""
                    printfn "Dry run - pass --apply to write these changes."
            finally
                MooEval.disconnect conn
        }

    work.GetAwaiter().GetResult()
    0

/// `compare-trees <treeA> <treeB>` is the Phase 3 round-trip gate's
/// assertion step: compares two exported trees via `Sidecar.RoundTrip`,
/// which tolerates only the specific fields the format declares
/// non-portable across instances (objnum, ownership) rather than requiring
/// a literal file diff. Prints every mismatch found and exits non-zero on
/// any - the "done when it passes in CI" bar from the main plan.
let private runCompareTrees (treeA: string) (treeB: string) : int =
    let mismatches = RoundTrip.compareTrees treeA treeB

    if mismatches.IsEmpty then
        printfn "Trees match."
        0
    else
        printfn "%d mismatch(es):" mismatches.Length
        printfn ""
        printfn "%s" (RoundTrip.formatMismatches mismatches)
        1

/// `checkpoint <treeDir> <sessionId> <message> <relativePath...>` commits
/// the given already-on-disk files onto `refs/moo/wip/<sessionId>` - a CLI
/// entry point purely so `Sidecar.GitStore` can be exercised live before it
/// gets wired into the real save flow, same as `export`/`import`/
/// `compare-trees` were built and verified standalone first.
let private runCheckpoint (treeDir: string) (sessionId: string) (message: string) (relativePaths: string list) : int =
    use repo = new Repository(treeDir)
    let commit = GitStore.commitChangedFiles repo sessionId relativePaths [] message "MOOdy" "moody@localhost"
    printfn "Committed %s onto refs/moo/wip/%s" (commit.Id.Sha.Substring(0, 8)) sessionId
    0

/// `squash-wip <treeDir> <sessionId> <message>` squashes every commit on
/// that session's wip ref into one commit on `main`, per the main plan's
/// idle-timeout/explicit-checkpoint squash step.
let private runSquashWip (treeDir: string) (sessionId: string) (message: string) : int =
    use repo = new Repository(treeDir)

    match GitStore.squashWipOntoMain repo sessionId message "MOOdy" "moody@localhost" with
    | Some commit ->
        printfn "Squashed onto main as %s" (commit.Id.Sha.Substring(0, 8))
        0
    | None ->
        printfn "No wip ref refs/moo/wip/%s - nothing to squash." sessionId
        1

/// `prune-wip <treeDir> <days>` deletes wip refs whose tip commit is older
/// than `days` - run this periodically (an operator/scheduled task, not an
/// automatic timer in the long-running sidecar), per the main plan's own
/// "git gc won't collect anything reachable" note.
let private runPruneWip (treeDir: string) (days: float) : int =
    use repo = new Repository(treeDir)
    let pruned = GitStore.pruneStaleWipRefs repo (TimeSpan.FromDays(days))

    if pruned.IsEmpty then
        printfn "Nothing to prune."
    else
        printfn "Pruned %d stale wip ref(s):" pruned.Length
        for sessionId in pruned do
            printfn "  %s" sessionId

    0

/// Resolves `--to <sha>`'s target commit, or `main`'s tip by default -
/// shared by `promote-diff` and `promote` so "what am I comparing/promoting"
/// is resolved identically in both.
let private resolveTargetCommit (repo: Repository) (toSha: string option) : Commit =
    match toSha with
    | Some sha -> repo.Lookup<Commit>(sha)
    | None -> repo.Branches.["main"].Tip

/// `promote-diff <treeDir> [--to <sha>]` - Phase 6's pre-deploy review (Q3:
/// "what is different between dev and production"). Read-only - never
/// touches the repo. `production` not existing yet (a first-ever promotion)
/// is reported against an empty tree, so everything shows up as new.
let private runPromoteDiff (treeDir: string) (toSha: string option) : int =
    use repo = new Repository(treeDir)
    let targetCommit = resolveTargetCommit repo toSha

    let fromTree =
        match GitStore.tryGetBranchTip repo "refs/heads/production" with
        | Some prodTip ->
            printfn "production (%s) -> %s:" (prodTip.Sha.Substring(0, 8)) (targetCommit.Sha.Substring(0, 8))
            prodTip.Tree
        | None ->
            printfn "Nothing deployed yet - everything in %s would be new:" (targetCommit.Sha.Substring(0, 8))
            repo.ObjectDatabase.CreateTree(TreeDefinition())

    let summary = Promotion.diffSummary repo fromTree targetCommit.Tree
    printfn ""

    if summary.IsEmpty then
        printfn "No differences."
    else
        printfn "%s" (GitStore.buildCommitMessage summary)

    0

/// `promote <treeDir> <targetHost> <targetPort> [--to <sha>] [--apply]` -
/// "promotion is not a second system, it's the importer with a different
/// target host" (main plan's own words): materializes the target commit's
/// tree to a temp directory and runs the *unmodified* Phase 2 importer
/// against it, dry-run by default like `import` itself. With `--apply`, a
/// successful compile-check + apply is followed by a re-export-and-compare
/// against the materialized (intended) tree - simultaneously deploy
/// verification and drift detection, per the main plan - and only a match
/// actually moves `production`. Rollback is this same command with `--to`
/// pointing at an earlier commit; no separate code path.
let private runPromote (treeDir: string) (host: string) (port: int) (username: string) (toSha: string option) (apply: bool) : int =
    use repo = new Repository(treeDir)
    let targetCommit = resolveTargetCommit repo toSha

    runPromoteDiff treeDir toSha |> ignore
    printfn ""

    let materializedDir = Path.Combine(Path.GetTempPath(), "moovcs-promote-" + Guid.NewGuid().ToString("N"))
    Promotion.materializeTree targetCommit.Tree materializedDir

    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port username cts.Token

            try
                let corponyms, objects = TreeParser.parseTree materializedDir
                let! plan = Importer.planImport conn corponyms objects cts.Token

                printfn "%s" (Importer.describePlan plan)

                if not apply then
                    printfn ""
                    printfn "Dry run - pass --apply to actually promote."
                    return 0
                else
                    let! compileResult = Importer.compileCheck conn plan cts.Token

                    match compileResult with
                    | Error errors ->
                        printfn ""
                        printfn "Compile check failed - aborting, production untouched:"

                        for corponym, msg in errors do
                            printfn "  $%s: %s" corponym msg

                        printfn ""
                        printfn "Materialized tree left at %s for inspection." materializedDir
                        return 1
                    | Ok() ->
                        do! Importer.applyPlan conn plan cts.Token

                        let reExportDir =
                            Path.Combine(Path.GetTempPath(), "moovcs-promote-verify-" + Guid.NewGuid().ToString("N"))

                        do! Exporter.exportTree conn reExportDir cts.Token

                        let mismatches = RoundTrip.compareTrees materializedDir reExportDir

                        if mismatches.IsEmpty then
                            match GitStore.tryGetBranchTip repo "refs/heads/production" with
                            | None -> repo.Refs.Add("refs/heads/production", targetCommit.Id) |> ignore
                            | Some _ ->
                                repo.Refs.UpdateTarget(repo.Refs.["refs/heads/production"], targetCommit.Id.Sha)
                                |> ignore

                            printfn "Promoted. production now at %s." (targetCommit.Sha.Substring(0, 8))
                            Directory.Delete(materializedDir, true)
                            Directory.Delete(reExportDir, true)
                            return 0
                        else
                            printfn "%d mismatch(es) after applying - production NOT moved:" mismatches.Length
                            printfn ""
                            printfn "%s" (RoundTrip.formatMismatches mismatches)
                            printfn ""
                            printfn "Materialized tree: %s" materializedDir
                            printfn "Re-exported tree:  %s" reExportDir
                            return 1
            finally
                MooEval.disconnect conn
        }

    work.GetAwaiter().GetResult()

/// The `"reconfigure-target"` action's actual work - validates *before*
/// touching `currentTarget`, so a failure (bad host/port, an unwritable
/// `newTreeDir`, ...) never leaves the sidecar half-configured:
/// 1. Connects through the new target's *LSP-bridge* port, not its ordinary
///    player port - confirmed live this must NOT reuse `newPort`: this
///    action runs *while the calling browser tab's own `/ws` connection is
///    still live* (unlike `runExport`'s one-shot CLI usage, always run
///    before any browser connects), and `MooEval.connect` logs in as the
///    same `wizard` character that connection almost certainly already is
///    (there's no real accounting on a bare `Minimal.db` world - see
///    MOOdy's CLAUDE.md). ToastStunt kicks a *repeated* login of the same
///    character, so connecting via `newPort` would disconnect the very
///    session that triggered the switch. The LSP-bridge port's listener
///    always logs in as its own dedicated service character regardless of
///    the text sent (see `LspBridge.fs`'s own doc comment) - reusing it here
///    sidesteps the collision entirely with no new bootstrap objects, and
///    doubles as a stricter reachability check (confirms the target has
///    this project's own bootstrap objects set up, not just that some MOO
///    process is listening on `newPort`).
/// 2. `git init`s `newTreeDir` if it has no `.git` yet (LibGit2Sharp has no
///    `--initial-branch` equivalent, so a fresh init's default branch -
///    whatever this machine's `init.defaultBranch` is - gets renamed to
///    `main` after its first commit, since `GitStore.fs` hardcodes
///    `refs/heads/main` with no fallback). An *existing* `.git` is left
///    exactly as-is - no re-init, no history loss.
/// 3. Re-exports (`Exporter.exportTree`, the same function `runExport`/
///    `test-instance-start.ps1`'s `Sidecar.exe export` already call) and
///    commits whatever changed directly onto the checked-out branch (unlike
///    `GitStore.fs`'s own commits, which deliberately avoid touching HEAD -
///    this one *should*, since "switch to this target" is meant to leave a
///    real, checked-out `main` reflecting exactly what's live there now).
/// Only on full success does `currentTarget` actually change.
let private reconfigureTarget
    (webSocket: WebSocket)
    (newHost: string)
    (newPort: int)
    (newLspBridgePort: int)
    (newTreeDir: string)
    (gitAuthorName: string)
    (gitAuthorEmail: string)
    (ct: CancellationToken)
    : Task<unit> =
    task {
        let mutable errorMessage = None

        try
            // The literal text sent here is irrelevant - the LSP-bridge
            // port's own listener object logs in as its fixed service
            // character regardless (see this function's own doc comment,
            // point 1), so there's no real account name to thread through.
            let! conn = MooEval.connect newHost newLspBridgePort "wizard" ct

            try
                let isFreshTree = not (Directory.Exists(Path.Combine(newTreeDir, ".git")))

                if not (Directory.Exists newTreeDir) then
                    Directory.CreateDirectory newTreeDir |> ignore

                if isFreshTree then
                    Repository.Init(newTreeDir, false) |> ignore

                do! Exporter.exportTree conn newTreeDir ct

                use repo = new Repository(newTreeDir)
                Commands.Stage(repo, "*")

                if repo.RetrieveStatus().IsDirty then
                    let author = Signature(gitAuthorName, gitAuthorEmail, DateTimeOffset.Now)
                    repo.Commit(sprintf "Sync on switch to %s:%d" newHost newPort, author, author, CommitOptions()) |> ignore

                    if isFreshTree && repo.Head.FriendlyName <> "main" then
                        repo.Branches.Rename(repo.Head.FriendlyName, "main") |> ignore
            finally
                MooEval.disconnect conn
        with ex ->
            errorMessage <- Some ex.Message

        match errorMessage with
        | None ->
            currentTarget <-
                { Host = newHost
                  Port = newPort
                  LspBridgePort = newLspBridgePort
                  TreeDir = newTreeDir }

            do! IdeActions.sendWire webSocket "moodev-reconfigure-target-result ok: 1" [] ct
        | Some msg -> do! IdeActions.sendWire webSocket "moodev-reconfigure-target-result ok: 0" [ msg ] ct
    }

/// Recognizes Phase 4's structured IDE-action envelope on a WebSocket Text
/// frame and dispatches to `IdeActions` - returns `false` (meaning "forward
/// this raw, as before") on a JSON parse failure or an unrecognized
/// `action` field, which is every ordinary typed MOO command, since none of
/// those parse as JSON. This is the one place `BridgeHandler` and
/// `IdeActions` meet - `BridgeHandler.handleConnection` takes this as an
/// injected callback specifically so it doesn't need to reference
/// `IdeActions` directly (avoiding a circular module dependency, since
/// `IdeActions` itself needs `BridgeHandler.Session`/`evalOnSession`).
let private buildTryDispatch
    (config: IdeActions.Config)
    (session: Session)
    (webSocket: WebSocket)
    (text: string)
    (ct: CancellationToken)
    : Task<bool> =
    task {
        let mutable doc = Unchecked.defaultof<JsonDocument>

        let parsed =
            try
                doc <- JsonDocument.Parse(text)
                true
            with _ ->
                false

        if not parsed then
            return false
        else
            use _ = doc
            let root = doc.RootElement
            let hasAction, actionEl = root.TryGetProperty("action")

            if not hasAction || actionEl.ValueKind <> JsonValueKind.String then
                return false
            else
                let getObj () = root.GetProperty("obj").GetInt64()
                let getStr (name: string) = root.GetProperty(name).GetString()

                try
                    match actionEl.GetString() with
                    | "fetch-verb" ->
                        do! IdeActions.fetchVerb config session webSocket (getObj ()) (getStr "verb") ct
                        return true
                    | "verb-at-parent" ->
                        do! IdeActions.verbAtParent session webSocket (getObj ()) (getStr "verb") ct
                        return true
                    | "save-verb" ->
                        let code = root.GetProperty("code").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                        do! IdeActions.saveVerb config session webSocket (getObj ()) (getStr "verb") code ct
                        return true
                    | "check-verb-syntax" ->
                        let code = root.GetProperty("code").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                        do! IdeActions.checkVerbSyntax session webSocket (getObj ()) (getStr "verb") code ct
                        return true
                    | "run-test-verb" ->
                        do! IdeActions.runTestVerb webSocket session (getObj ()) (getStr "verb") ct
                        return true
                    | "get-properties" ->
                        do! IdeActions.getProperties config session webSocket (getObj ()) ct
                        return true
                    | "set-property" ->
                        do! IdeActions.setProperty config session webSocket (getObj ()) (getStr "name") (getStr "valueExpr") ct
                        return true
                    | "add-property" ->
                        do!
                            IdeActions.addProperty
                                config
                                session
                                webSocket
                                (getObj ())
                                (getStr "name")
                                (getStr "ownerExpr")
                                (getStr "valueExpr")
                                (getStr "perms")
                                ct

                        return true
                    | "delete-verb" ->
                        do! IdeActions.deleteVerb config session webSocket (getObj ()) (getStr "verb") ct
                        return true
                    | "delete-property" ->
                        do! IdeActions.deleteProperty config session webSocket (getObj ()) (getStr "name") ct
                        return true
                    | "recycle-object" ->
                        do! IdeActions.recycleObject config session webSocket (getObj ()) ct
                        return true
                    | "create-object" ->
                        do! IdeActions.createObject config session webSocket (getStr "parentExpr") ct
                        return true
                    | "add-verb" ->
                        do!
                            IdeActions.addVerb
                                config
                                session
                                webSocket
                                (getObj ())
                                (getStr "name")
                                (getStr "ownerExpr")
                                (getStr "perms")
                                (getStr "dobj")
                                (getStr "prep")
                                (getStr "iobj")
                                ct

                        return true
                    | "set-owner" ->
                        do! IdeActions.setOwner config session webSocket (getObj ()) (getStr "ownerExpr") ct
                        return true
                    | "rename-object" ->
                        do! IdeActions.setName config session webSocket (getObj ()) (getStr "name") ct
                        return true
                    | "set-flag" ->
                        do! IdeActions.setFlag config session webSocket (getObj ()) (getStr "flag") (root.GetProperty("value").GetInt32() = 1) ct
                        return true
                    | "add-parent" ->
                        do! IdeActions.addParent config session webSocket (getObj ()) (getStr "parentExpr") ct
                        return true
                    | "remove-parent" ->
                        do! IdeActions.removeParent config session webSocket (getObj ()) (root.GetProperty("parent").GetInt64()) ct
                        return true
                    | "add-child" ->
                        do! IdeActions.addChild config session webSocket (getObj ()) (getStr "childExpr") ct
                        return true
                    | "set-property-info" ->
                        do!
                            IdeActions.setPropertyInfo
                                config
                                session
                                webSocket
                                (getObj ())
                                (getStr "name")
                                (getStr "newName")
                                (getStr "ownerExpr")
                                (getStr "perms")
                                ct

                        return true
                    | "set-verb-info" ->
                        do!
                            IdeActions.setVerbInfo
                                config
                                session
                                webSocket
                                (getObj ())
                                (getStr "verb")
                                (getStr "newNames")
                                (getStr "ownerExpr")
                                (getStr "perms")
                                ct

                        return true
                    | "set-verb-args" ->
                        do!
                            IdeActions.setVerbArgs
                                config
                                session
                                webSocket
                                (getObj ())
                                (getStr "verb")
                                (getStr "dobj")
                                (getStr "prep")
                                (getStr "iobj")
                                ct

                        return true
                    | "fix-permission-risk" ->
                        do! IdeActions.fixPermissionRisk config session webSocket (getObj ()) (getStr "name") (getStr "kind") ct

                        return true
                    | "eval-watch-batch" ->
                        let exprs = root.GetProperty("exprs").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                        do! IdeActions.evalWatchBatch session webSocket exprs ct
                        return true
                    | "get-live-children" ->
                        do! IdeActions.getLiveChildren config session webSocket (getObj ()) ct
                        return true
                    | "get-live-roots" ->
                        do! IdeActions.getLiveRoots config session webSocket ct
                        return true
                    | "get-live-info" ->
                        do! IdeActions.getLiveInfo config session webSocket (getObj ()) ct
                        return true
                    | "get-tasks" ->
                        do! IdeActions.getTasks config session webSocket ct
                        return true
                    | "get-server-status" ->
                        do! IdeActions.getServerStatus config session webSocket ct
                        return true
                    | "env-doctor-check" ->
                        do! IdeActions.envDoctorCheck config session webSocket ct
                        return true
                    | "parse-property-literal" ->
                        do! IdeActions.parsePropertyLiteralAction webSocket (getObj ()) (getStr "name") (getStr "valueText") ct
                        return true
                    | "kill-task" ->
                        do! IdeActions.killTask webSocket session (root.GetProperty("task").GetInt64()) ct
                        return true
                    | "eval-scratchpad" ->
                        do! IdeActions.evalScratchpad session webSocket (getStr "expr") ct
                        return true
                    | "checkpoint" ->
                        do! IdeActions.checkpoint config webSocket ct
                        return true
                    | "verb-history" ->
                        do! IdeActions.verbHistory config session webSocket (getObj ()) (getStr "verb") ct
                        return true
                    | "verb-at-commit" ->
                        do! IdeActions.verbAtCommit config session webSocket (getObj ()) (getStr "verb") (getStr "sha") ct
                        return true
                    | "search-history" ->
                        do! IdeActions.searchHistory config session webSocket (getStr "query") ct
                        return true
                    | "search-content" ->
                        do! IdeActions.searchContent config session webSocket (getStr "query") ct
                        return true
                    | "search-properties" ->
                        do! IdeActions.searchPropertiesByValue session webSocket (getStr "name") (getStr "valueExpr") ct
                        return true
                    | "get-waif-properties" ->
                        do! IdeActions.getWaifProperties session webSocket (getObj ()) (getStr "name") ct
                        return true
                    | "set-waif-property" ->
                        do!
                            IdeActions.setWaifProperty
                                session
                                webSocket
                                (getObj ())
                                (getStr "name")
                                (getStr "waifProp")
                                (getStr "valueExpr")
                                ct

                        return true
                    | "rename-verb" ->
                        let sites =
                            root.GetProperty("sites").EnumerateArray()
                            |> Seq.map (fun s ->
                                s.GetProperty("objRef").GetInt64(),
                                s.GetProperty("verbName").GetString(),
                                s.GetProperty("line").GetInt32(),
                                s.GetProperty("col").GetInt32(),
                                s.GetProperty("length").GetInt32())
                            |> List.ofSeq

                        do! IdeActions.renameVerb config session webSocket (getObj ()) (getStr "oldName") (getStr "newName") sites ct
                        return true
                    | "bulk-replace" ->
                        let sites =
                            root.GetProperty("sites").EnumerateArray()
                            |> Seq.map (fun s ->
                                s.GetProperty("objRef").GetInt64(),
                                s.GetProperty("verbName").GetString(),
                                s.GetProperty("line").GetInt32(),
                                s.GetProperty("col").GetInt32())
                            |> List.ofSeq

                        do! IdeActions.bulkReplace config session webSocket (getStr "query") (getStr "replacement") sites ct
                        return true
                    | "corponym-history" ->
                        do! IdeActions.corponymHistory config webSocket ct
                        return true
                    | "get-moo-target" ->
                        do!
                            IdeActions.sendWire
                                webSocket
                                (sprintf
                                    "moodev-moo-target-result host: %s port: %d lspBridgePort: %d treeDir: %s"
                                    currentTarget.Host
                                    currentTarget.Port
                                    currentTarget.LspBridgePort
                                    currentTarget.TreeDir)
                                []
                                ct

                        return true
                    | "reconfigure-target" ->
                        do!
                            reconfigureTarget
                                webSocket
                                (getStr "host")
                                (root.GetProperty("port").GetInt32())
                                (root.GetProperty("lspBridgePort").GetInt32())
                                (getStr "treeDir")
                                config.GitAuthorName
                                config.GitAuthorEmail
                                ct

                        return true
                    | _ -> return false
                with :? TimeoutException as ex ->
                    // Safety net for every action handler above that calls
                    // `IdeActions.evalOnSession` without its own specific
                    // recovery (unlike `getLiveInfo`, which has one) - an
                    // uncaught `TimeoutException` here would otherwise
                    // propagate past this whole dispatch and crash the
                    // *entire* browser connection (confirmed: the caller's
                    // own `with :? OperationCanceledException -> ()` doesn't
                    // match this exception type at all). Reuses the existing
                    // Errors-tab wire message (`#0:handle_uncaught_error`'s
                    // own channel, `App.fs`'s `moodev-error` handler) rather
                    // than inventing a new one - same "something went wrong,
                    // log it" shape, already wired up and visible.
                    do!
                        IdeActions.sendWire
                            webSocket
                            "moodev-error kind: action-timeout"
                            [ sprintf "Action '%s' timed out: %s" (actionEl.GetString()) ex.Message ]
                            ct

                    return true
    }

/// Pulls `--to <sha>` (a flag *with* a value, unlike the bare `--apply`
/// flag `import`/`promote` already handle via `Array.contains`) out of an
/// args array, returning the value (if present) and the remaining
/// positional args with both the flag and its value removed.
let private extractToFlag (args: string[]) : string option * string[] =
    match args |> Array.tryFindIndex (fun a -> a = "--to") with
    | Some idx when idx + 1 < args.Length ->
        let value = args.[idx + 1]
        let remaining = Array.append args.[.. idx - 1] args.[idx + 2 ..]
        Some value, remaining
    | _ -> None, args

/// Pulls `--user <name>` (same value-carrying-flag shape as `extractToFlag`
/// above) out of an args array for `export`/`import`/`promote` - see
/// `runExport`'s own comment on why this exists. Defaults to `"wizard"`
/// when absent, matching `MooEval.connect`'s own default expectation for a
/// bare `Minimal.db` world.
let private extractUserFlag (args: string[]) : string * string[] =
    match args |> Array.tryFindIndex (fun a -> a = "--user") with
    | Some idx when idx + 1 < args.Length ->
        let value = args.[idx + 1]
        let remaining = Array.append args.[.. idx - 1] args.[idx + 2 ..]
        value, remaining
    | _ -> "wizard", args

[<EntryPoint>]
let main args =
    match args with
    | _ when args.Length >= 2 && args.[0] = "export" ->
        let username, positional = extractUserFlag args.[1..]

        match positional with
        | [| outputDir |] -> runExport outputDir "127.0.0.1" 7777 username
        | [| outputDir; host |] -> runExport outputDir host 7777 username
        | [| outputDir; host; port |] -> runExport outputDir host (int port) username
        | _ ->
            eprintfn "Usage: export <outputDir> [host] [port] [--user <name>]"
            1
    | [| "compare-trees"; treeA; treeB |] -> runCompareTrees treeA treeB
    | [| "squash-wip"; treeDir; sessionId; message |] -> runSquashWip treeDir sessionId message
    | [| "prune-wip"; treeDir; days |] -> runPruneWip treeDir (float days)
    | _ when args.Length >= 4 && args.[0] = "checkpoint" ->
        runCheckpoint args.[1] args.[2] args.[3] (args.[4..] |> List.ofArray)
    | _ when args.Length >= 2 && args.[0] = "import" ->
        let apply = args |> Array.contains "--apply"
        let username, positional1 = extractUserFlag (args.[1..] |> Array.filter (fun a -> a <> "--apply"))
        let positional = positional1

        match positional with
        | [| treeDir |] -> runImport treeDir "127.0.0.1" 7777 username apply
        | [| treeDir; host |] -> runImport treeDir host 7777 username apply
        | [| treeDir; host; port |] -> runImport treeDir host (int port) username apply
        | _ ->
            eprintfn "Usage: import <treeDir> [host] [port] [--user <name>] [--apply]"
            1
    | _ when args.Length >= 2 && args.[0] = "promote-diff" ->
        let toSha, positional = extractToFlag args.[1..]

        match positional with
        | [| treeDir |] -> runPromoteDiff treeDir toSha
        | _ ->
            eprintfn "Usage: promote-diff <treeDir> [--to <sha>]"
            1
    | _ when args.Length >= 4 && args.[0] = "promote" ->
        let apply = args |> Array.contains "--apply"
        let toSha, positional1 = extractToFlag args.[1..]
        let username, positional2 = extractUserFlag positional1
        let positional = positional2 |> Array.filter (fun a -> a <> "--apply")

        match positional with
        | [| treeDir; host; port |] -> runPromote treeDir host (int port) username toSha apply
        | _ ->
            eprintfn "Usage: promote <treeDir> <targetHost> <targetPort> [--user <name>] [--to <sha>] [--apply]"
            1
    | _ ->
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        // `currentTarget` (module-level, above) starts out holding hardcoded
        // placeholder defaults - this is its one real initialization, now
        // that `app.Configuration` actually exists. `gitAuthorName`/
        // `gitAuthorEmail`/`wipRetentionDays` are process identity/policy,
        // not part of the target, so they stay separate, ordinary immutable
        // bindings (never reconfigured live).
        currentTarget <-
            { Host = app.Configuration.GetValue<string>("Moo:Host", "127.0.0.1")
              Port = app.Configuration.GetValue<int>("Moo:Port", 7777)
              LspBridgePort = app.Configuration.GetValue<int>("Moo:LspBridgePort", 7780)
              TreeDir = app.Configuration.GetValue<string>("Moo:TreeDir", "../../../Survive") }

        let gitAuthorName = app.Configuration.GetValue<string>("Moo:GitAuthorName", "MOOdy")
        let gitAuthorEmail = app.Configuration.GetValue<string>("Moo:GitAuthorEmail", "moody@localhost")
        let wipRetentionDays = app.Configuration.GetValue<float>("Moo:WipRetentionDays", 14.0)

        // Runs once per sidecar start rather than as a persistent timer - every
        // dev session already starts the sidecar fresh (test.ps1/`dotnet watch
        // run`), so stale wip refs get swept on a normal cadence with no extra
        // moving parts. `prune-wip` (the CLI subcommand above) still covers an
        // on-demand run. Best-effort: a tree that doesn't exist yet (first run
        // before any export has happened) shouldn't block the sidecar from
        // starting.
        try
            use repo = new Repository(currentTarget.TreeDir)
            let pruned = GitStore.pruneStaleWipRefs repo (TimeSpan.FromDays(wipRetentionDays))

            if pruned.IsEmpty then
                printfn "Prune-on-startup: no stale wip refs older than %g day(s)." wipRetentionDays
            else
                printfn "Prune-on-startup: pruned %d stale wip ref(s): %s" pruned.Length (String.concat ", " pruned)
        with ex ->
            eprintfn "Prune-on-startup skipped (%s): %s" currentTarget.TreeDir ex.Message

        app.UseWebSockets() |> ignore

        app.Map(
            "/ws",
            Func<HttpContext, Threading.Tasks.Task>(fun ctx ->
                task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        use! webSocket = ctx.WebSockets.AcceptWebSocketAsync()

                        // Read fresh here, not captured once at process
                        // start - so a connection accepted *after* a
                        // successful `"reconfigure-target"` (i.e. the
                        // Client's own post-switch page reload) picks up
                        // whatever `currentTarget` holds right now.
                        let ideConfig: IdeActions.Config =
                            { TreeDir = currentTarget.TreeDir
                              SessionId = Guid.NewGuid().ToString("N")
                              GitAuthorName = gitAuthorName
                              GitAuthorEmail = gitAuthorEmail
                              LspBridgePort = currentTarget.LspBridgePort }

                        let tryDispatch = buildTryDispatch ideConfig
                        let endpoint: MooEndpoint = { Host = currentTarget.Host; Port = currentTarget.Port }

                        do! handleConnection endpoint webSocket tryDispatch ctx.RequestAborted
                    else
                        ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                })
        )
        |> ignore

        app.Map(
            "/lsp-bridge",
            Func<HttpContext, Threading.Tasks.Task>(fun ctx ->
                task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        use! webSocket = ctx.WebSockets.AcceptWebSocketAsync()

                        let lspBridgeEndpoint: LspBridge.MooEndpoint =
                            { Host = currentTarget.Host; Port = currentTarget.LspBridgePort }

                        do! LspBridge.handleConnection lspBridgeEndpoint webSocket ctx.RequestAborted
                    else
                        ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                })
        )
        |> ignore

        app.Run()

        0
