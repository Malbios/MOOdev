/// The language server's one shared, reloadable static-analysis graph -
/// loaded once at process start via `init`, and reloadable in place via
/// `reload` (the `moodev/reloadGraph` custom method in `Handlers.fs` calls
/// this) without restarting the process. `Program.fs`'s per-connection
/// accept code calls `get ()` fresh for each new `/lsp` connection instead of
/// reusing one process-lifetime value, so a connection accepted *after* a
/// reload (e.g. the browser's own post-target-switch page reload) picks up
/// the fresh graph automatically - every existing `Handlers.fs` function
/// still just takes a plain immutable `Graph` parameter, unchanged, since
/// each connection's own `MooLspServer` instance only ever needs one
/// snapshot for its own (short) lifetime.
module LanguageServer.GraphStore

let mutable private current: Metadata.Schema.Graph =
    { Objects = Map.empty
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

let init (surviveRoot: string) : unit = current <- Metadata.Loader.load surviveRoot

let reload (surviveRoot: string) : unit =
    printfn "Reloading metadata graph from %s..." surviveRoot
    current <- Metadata.Loader.load surviveRoot
    printfn "Loaded %d objects." current.Objects.Count

let get () : Metadata.Schema.Graph = current
