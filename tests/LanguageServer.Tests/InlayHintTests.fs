/// End-to-end tests for `MooLspServer.TextDocumentInlayHint`, built via the
/// same "real exported tree through `Metadata.Loader.load`" pattern
/// `HandlerTests.fs` uses - a hand-built `VerbNode` (`DeadVerbFinderTests.fs`'s
/// lighter-weight `verbNode` helper) always sets `Tokens = None`, but inlay
/// hints are anchored on real token positions (`argStartPositions`), so
/// these tests need the real lexer/parser output a fixture tree produces,
/// not a synthetic AST alone.
module LanguageServer.Tests.InlayHintTests

open System
open System.IO
open Xunit
open Ionide.LanguageServerProtocol.Types
open Metadata.Schema
open LanguageServer.Handlers
open LanguageServer.SidecarBridge
open Sidecar.Exporter

let private verb (names: string) (code: string list) : VerbExport =
    { Names = names
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this"
      Code = code }

/// Object #2 defines the callees `TextDocumentInlayHint`'s resolved calls
/// dispatch to; object #3 defines the callers whose argument lists are
/// under test. Kept in one small fixture rather than reusing
/// `HandlerTests.fs`'s (that module's fixture has no scatter-parameter
/// verb suited to this feature, and its fixture bindings are private to
/// that file anyway).
let private fixtureDir =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-inlayhinttests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")
    File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo [ "target_obj", 2L; "caller_obj", 3L ])
    File.WriteAllText(Path.Combine(dir, "builtins.json"), """{"functions": []}""")

    let corponymsByObjnum = Map.ofList [ 2L, "target_obj"; 3L, "caller_obj" ]

    let writeObject (name: string) (data: ObjectExport) =
        let objDir = Path.Combine(dir, "objects", name)
        let verbsDir = Path.Combine(objDir, "verbs")
        Directory.CreateDirectory(verbsDir) |> ignore
        let verbFileNames = assignVerbFileNames data.Verbs
        File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo corponymsByObjnum ("$" + name) data verbFileNames)
        for v, fileName in verbFileNames do
            File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile ("$" + name) v)

    writeObject
        "target_obj"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs =
            [ verb "target" [ "\"Target with named + optional params.\";"; "{who, ?what = 0} = args;"; "return who;" ]
              verb "target_rest" [ "\"Target with a rest param.\";"; "{a, @rest} = args;"; "return a;" ]
              verb "no_params" [ "\"No inferable params at all.\";"; "return 1;" ] ]
          LiveName = ""
          Aliases = [] }

    writeObject
        "caller_obj"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs =
            [ verb "caller" [ "\"Calls target with a literal + an ident.\";"; "who = player;"; "#2:target(who, \"hi\");" ]
              verb
                  "caller_rest"
                  [ "\"Calls target_rest with three args.\";"; "#2:target_rest(1, 2, 3);" ]
              verb
                  "caller_splice"
                  [ "\"Calls target with a spliced list - no positions are reliable.\";"
                    "some_list = {player, \"hi\"};"
                    "#2:target(@some_list);" ]
              verb "caller_no_params" [ "\"Calls a callee with no inferable params.\";"; "#2:no_params(1, 2);" ] ]
          LiveName = ""
          Aliases = [] }

    dir

let private graph = lazy (Metadata.Loader.load fixtureDir)

let private fakeBridge: SidecarBridge =
    { ResolveVerbDispatch = fun _ _ -> task { return None }
      GetBuiltins = fun () -> task { return Some Map.empty } }

let private server = lazy (new MooLspServer(new MooLspClient(), graph.Value, fakeBridge))

let private wholeVerbRange: Range =
    { Start = { Line = 0u; Character = 0u }
      End = { Line = 1000u; Character = 0u } }

let private inlayHintsFor (verbName: string) : InlayHint[] =
    let p: InlayHintParams =
        { TextDocument = { Uri = moodevVerbUri 3L verbName }
          Range = wholeVerbRange
          WorkDoneToken = None }

    match server.Value.TextDocumentInlayHint(p) |> Async.RunSynchronously with
    | Ok(Some hints) -> hints
    | Ok None -> [||]
    | Error e -> failwithf "unexpected error: %A" e

let private labelOf (h: InlayHint) : string =
    match h.Label with
    | U2.C1 s -> s
    | U2.C2 parts -> parts |> Array.map (fun p -> p.Value) |> String.concat ""

[<Fact>]
let ``a literal argument gets a hint, not just an ident one - the token-position path actually works`` () =
    let hints = inlayHintsFor "caller" |> Array.map labelOf
    Assert.Equal<string[]>([| "who:"; "what:" |], hints)

[<Fact>]
let ``a rest param truncates the hint list at the required prefix`` () =
    let hints = inlayHintsFor "caller_rest" |> Array.map labelOf
    Assert.Equal<string[]>([| "a:" |], hints)

[<Fact>]
let ``a spliced argument list produces no hints at all`` () =
    let hints = inlayHintsFor "caller_splice"
    Assert.Empty(hints)

[<Fact>]
let ``a callee with no inferable params produces no hints`` () =
    let hints = inlayHintsFor "caller_no_params"
    Assert.Empty(hints)
