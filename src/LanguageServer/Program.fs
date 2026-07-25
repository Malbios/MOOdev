module LanguageServer.Program

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)
    let app = builder.Build()

    let surviveRoot = app.Configuration.GetValue<string>("Survive:Root", "C:\\dev\\moo\\Survive")
    printfn "Loading metadata graph from %s..." surviveRoot
    let graph = Metadata.Loader.load surviveRoot
    printfn "Loaded %d objects." graph.Objects.Count

    app.UseWebSockets() |> ignore

    app.Map(
        "/lsp",
        Func<HttpContext, Task>(fun ctx ->
            task {
                if ctx.WebSockets.IsWebSocketRequest then
                    use! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                    // `Server.startWs` blocks for the connection's lifetime
                    // running its own message loop - off the async
                    // continuation thread via Task.Run so it doesn't tie up
                    // a thread-pool thread for the whole session.
                    do! Task.Run(fun () -> WsTransport.run webSocket graph)
                else
                    ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
            })
    )
    |> ignore

    app.Run()

    0
