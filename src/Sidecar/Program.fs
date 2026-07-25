module Sidecar.Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Sidecar.BridgeHandler

[<EntryPoint>]
let main args =
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
