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

/// `export <outputDir> [host] [port]` runs the Phase 1 exporter
/// (`Sidecar.Exporter`) against a wizard connection and exits - a one-shot
/// batch tool, distinct from the always-on WebSocket bridge below. Host/port
/// default to the same values `appsettings.json` gives the bridge
/// (127.0.0.1:7777); pass an explicit host/port to target e.g. the headless
/// test instance on 7778 instead.
let private runExport (outputDir: string) (host: string) (port: int) : int =
    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port cts.Token

            try
                do! Exporter.exportTree conn outputDir cts.Token
            finally
                MooEval.disconnect conn
        }

    work.GetAwaiter().GetResult()
    0

/// `import <treeDir> [host] [port] [--apply]` reads a tree written by
/// `export` and applies it to the target. Dry-run (prints the planned
/// operations, `Importer.describePlan`) unless `--apply` is passed - the
/// first component in this project that writes to a live MOO, so it
/// defaults to safe/preview-only per the Phase 2 plan's decision #5.
let private runImport (treeDir: string) (host: string) (port: int) (apply: bool) : int =
    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port cts.Token

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
    let commit = GitStore.commitChangedFiles repo sessionId relativePaths [] message "moo-dev" "moo-dev@localhost"
    printfn "Committed %s onto refs/moo/wip/%s" (commit.Id.Sha.Substring(0, 8)) sessionId
    0

/// `squash-wip <treeDir> <sessionId> <message>` squashes every commit on
/// that session's wip ref into one commit on `main`, per the main plan's
/// idle-timeout/explicit-checkpoint squash step.
let private runSquashWip (treeDir: string) (sessionId: string) (message: string) : int =
    use repo = new Repository(treeDir)

    match GitStore.squashWipOntoMain repo sessionId message "moo-dev" "moo-dev@localhost" with
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
let private runPromote (treeDir: string) (host: string) (port: int) (toSha: string option) (apply: bool) : int =
    use repo = new Repository(treeDir)
    let targetCommit = resolveTargetCommit repo toSha

    runPromoteDiff treeDir toSha |> ignore
    printfn ""

    let materializedDir = Path.Combine(Path.GetTempPath(), "moovcs-promote-" + Guid.NewGuid().ToString("N"))
    Promotion.materializeTree targetCommit.Tree materializedDir

    use cts = new CancellationTokenSource()

    let work =
        task {
            let! conn = MooEval.connect host port cts.Token

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

                match actionEl.GetString() with
                | "fetch-verb" ->
                    do! IdeActions.fetchVerb config session webSocket (getObj ()) (getStr "verb") ct
                    return true
                | "save-verb" ->
                    let code = root.GetProperty("code").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                    do! IdeActions.saveVerb config session webSocket (getObj ()) (getStr "verb") code ct
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
                | "get-live-children" ->
                    do! IdeActions.getLiveChildren config session webSocket (getObj ()) ct
                    return true
                | "get-live-info" ->
                    do! IdeActions.getLiveInfo config session webSocket (getObj ()) ct
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
                | "corponym-history" ->
                    do! IdeActions.corponymHistory config webSocket ct
                    return true
                | _ -> return false
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

[<EntryPoint>]
let main args =
    match args with
    | [| "export"; outputDir |] -> runExport outputDir "127.0.0.1" 7777
    | [| "export"; outputDir; host |] -> runExport outputDir host 7777
    | [| "export"; outputDir; host; port |] -> runExport outputDir host (int port)
    | [| "compare-trees"; treeA; treeB |] -> runCompareTrees treeA treeB
    | [| "squash-wip"; treeDir; sessionId; message |] -> runSquashWip treeDir sessionId message
    | [| "prune-wip"; treeDir; days |] -> runPruneWip treeDir (float days)
    | _ when args.Length >= 4 && args.[0] = "checkpoint" ->
        runCheckpoint args.[1] args.[2] args.[3] (args.[4..] |> List.ofArray)
    | _ when args.Length >= 2 && args.[0] = "import" ->
        let apply = args |> Array.contains "--apply"
        let positional = args.[1..] |> Array.filter (fun a -> a <> "--apply")

        match positional with
        | [| treeDir |] -> runImport treeDir "127.0.0.1" 7777 apply
        | [| treeDir; host |] -> runImport treeDir host 7777 apply
        | [| treeDir; host; port |] -> runImport treeDir host (int port) apply
        | _ ->
            eprintfn "Usage: import <treeDir> [host] [port] [--apply]"
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
        let positional = positional1 |> Array.filter (fun a -> a <> "--apply")

        match positional with
        | [| treeDir; host; port |] -> runPromote treeDir host (int port) toSha apply
        | _ ->
            eprintfn "Usage: promote <treeDir> <targetHost> <targetPort> [--to <sha>] [--apply]"
            1
    | _ ->
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        let endpoint =
            { Host = app.Configuration.GetValue<string>("Moo:Host", "127.0.0.1")
              Port = app.Configuration.GetValue<int>("Moo:Port", 7777) }

        // The world's dedicated LSP-service listener (see moo-dev's CLAUDE.md
        // bootstrap docs) - a separate port, bound to a separate player
        // character, so the LSP's own live connection here never collides
        // with a browser tab's Wizard session on `endpoint` above (ToastStunt
        // kicks a *repeated login of the same character*, not "more than one
        // live connection").
        let lspBridgeEndpoint: LspBridge.MooEndpoint =
            { Host = app.Configuration.GetValue<string>("Moo:Host", "127.0.0.1")
              Port = app.Configuration.GetValue<int>("Moo:LspBridgePort", 7780) }

        let treeDir = app.Configuration.GetValue<string>("Moo:TreeDir", "../../../Survive")
        let gitAuthorName = app.Configuration.GetValue<string>("Moo:GitAuthorName", "moo-dev")
        let gitAuthorEmail = app.Configuration.GetValue<string>("Moo:GitAuthorEmail", "moo-dev@localhost")
        let wipRetentionDays = app.Configuration.GetValue<float>("Moo:WipRetentionDays", 14.0)

        // Runs once per sidecar start rather than as a persistent timer - every
        // dev session already starts the sidecar fresh (test.ps1/`dotnet watch
        // run`), so stale wip refs get swept on a normal cadence with no extra
        // moving parts. `prune-wip` (the CLI subcommand above) still covers an
        // on-demand run. Best-effort: a tree that doesn't exist yet (first run
        // before any export has happened) shouldn't block the sidecar from
        // starting.
        try
            use repo = new Repository(treeDir)
            let pruned = GitStore.pruneStaleWipRefs repo (TimeSpan.FromDays(wipRetentionDays))

            if pruned.IsEmpty then
                printfn "Prune-on-startup: no stale wip refs older than %g day(s)." wipRetentionDays
            else
                printfn "Prune-on-startup: pruned %d stale wip ref(s): %s" pruned.Length (String.concat ", " pruned)
        with ex ->
            eprintfn "Prune-on-startup skipped (%s): %s" treeDir ex.Message

        app.UseWebSockets() |> ignore

        app.Map(
            "/ws",
            Func<HttpContext, Threading.Tasks.Task>(fun ctx ->
                task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        use! webSocket = ctx.WebSockets.AcceptWebSocketAsync()

                        let ideConfig: IdeActions.Config =
                            { TreeDir = treeDir
                              SessionId = Guid.NewGuid().ToString("N")
                              GitAuthorName = gitAuthorName
                              GitAuthorEmail = gitAuthorEmail }

                        let tryDispatch = buildTryDispatch ideConfig

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
                        do! LspBridge.handleConnection lspBridgeEndpoint webSocket ctx.RequestAborted
                    else
                        ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                })
        )
        |> ignore

        app.Run()

        0
