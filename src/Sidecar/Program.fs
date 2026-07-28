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

[<EntryPoint>]
let main args =
    match args with
    | [| "export"; outputDir |] -> runExport outputDir "127.0.0.1" 7777
    | [| "export"; outputDir; host |] -> runExport outputDir host 7777
    | [| "export"; outputDir; host; port |] -> runExport outputDir host (int port)
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
