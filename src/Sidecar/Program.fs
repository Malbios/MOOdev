module Sidecar.Program

open System
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
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

[<EntryPoint>]
let main args =
    match args with
    | [| "export"; outputDir |] -> runExport outputDir "127.0.0.1" 7777
    | [| "export"; outputDir; host |] -> runExport outputDir host 7777
    | [| "export"; outputDir; host; port |] -> runExport outputDir host (int port)
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
    | _ ->
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        let endpoint =
            { Host = app.Configuration.GetValue<string>("Moo:Host", "127.0.0.1")
              Port = app.Configuration.GetValue<int>("Moo:Port", 7777) }

        app.UseWebSockets() |> ignore

        app.Map(
            "/ws",
            Func<HttpContext, Threading.Tasks.Task>(fun ctx ->
                task {
                    if ctx.WebSockets.IsWebSocketRequest then
                        use! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                        do! handleConnection endpoint webSocket ctx.RequestAborted
                    else
                        ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                })
        )
        |> ignore

        app.Run()

        0
