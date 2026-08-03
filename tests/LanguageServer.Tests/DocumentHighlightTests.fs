/// Hand-built fixture graphs for `MooLspServer.TextDocumentDocumentHighlight`
/// - same reasoning as `VerbMetricsTests.fs`/`MoocodeDocsTests.fs`: precise
/// control over each verb's AST rather than depending on a larger synthetic
/// corpus. `TextDocumentDocumentHighlight` never touches the bridge (all
/// matching is by-name within one verb's own AST), so the fake bridge here
/// just throws if ever called - a canary for that invariant breaking.
module LanguageServer.Tests.DocumentHighlightTests

open System.Threading.Tasks
open Xunit
open Ionide.LanguageServerProtocol.Types
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers
open LanguageServer.SidecarBridge

let private verbMeta (index: int) (name: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) (ast: Stmt list) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = Some ast
      DiagnosticCount = 0
      Tokens = Some [||] }

let private objNode (num: ObjRef) (verbs: VerbNode list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = None
      Flags = None
      Properties = []
      Aliases = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

let private neverCalledBridge: SidecarBridge =
    { ResolveVerbDispatch = fun _ _ -> Task.FromException<VerbDispatchResult option>(exn "should not be called")
      GetBuiltins = fun () -> Task.FromException<Map<string, BuiltinFunc>>(exn "should not be called") }

let private serverFor (graph: Graph) : MooLspServer =
    new MooLspServer(new MooLspClient(), graph, neverCalledBridge)

let private highlightsAt (server: MooLspServer) (objRef: ObjRef) (verbName: string) (astLine: int) (astCol: int) : Range[] =
    let p: DocumentHighlightParams =
        { TextDocument = { Uri = moodevVerbUri objRef verbName }
          Position = { Line = uint32 (astLine - 1); Character = uint32 (astCol - 1) }
          WorkDoneToken = None
          PartialResultToken = None }

    match Async.RunSynchronously(server.TextDocumentDocumentHighlight p) with
    | Ok(Some highlights) -> highlights |> Array.map (fun h -> h.Range)
    | Ok None -> [||]
    | Error e -> failwithf "unexpected error: %A" e

[<Fact>]
let ``a repeated local variable highlights every occurrence of that name, nothing else`` () =
    // x = 1; y = 2; return x + y;
    let v =
        verbNode
            1L
            (verbMeta 1 "repeated")
            [ ExprStmt(Assign(Ident("x", 1, 1), IntLit 1L))
              ExprStmt(Assign(Ident("y", 2, 1), IntLit 2L))
              Return(Some(Binary(Add, Ident("x", 3, 8), Ident("y", 3, 12)))) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let highlights = highlightsAt server 1L "repeated" 1 1

    Assert.Equal(2, highlights.Length)
    Assert.All(highlights, (fun r -> Assert.True(r.Start.Character = 0u || r.Start.Character = 7u)))

[<Fact>]
let ``a repeated verb call name highlights every call site, not a different name`` () =
    // #2:target(); #2:target(); #2:other();
    let v =
        verbNode
            1L
            (verbMeta 1 "caller")
            [ ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 1, 1))
              ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 2, 1))
              ExprStmt(VerbCall(ObjLit 2L, StrLit "other", [], 3, 1)) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let highlights = highlightsAt server 1L "caller" 1 1

    Assert.Equal(2, highlights.Length)

[<Fact>]
let ``a repeated property name highlights every access, receiver-independent`` () =
    // this.foo; other.foo; this.bar;
    let v =
        verbNode
            1L
            (verbMeta 1 "propverb")
            [ ExprStmt(Prop(Ident("this", 1, 1), StrLit "foo", 1, 6))
              ExprStmt(Prop(Ident("other", 2, 1), StrLit "foo", 2, 7))
              ExprStmt(Prop(Ident("this", 3, 1), StrLit "bar", 3, 6)) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let highlights = highlightsAt server 1L "propverb" 1 6

    Assert.Equal(2, highlights.Length)

[<Fact>]
let ``cursor not on any reference returns no highlights`` () =
    let v = verbNode 1L (verbMeta 1 "empty") [ Return(Some(IntLit 1L)) ]
    let server = serverFor (graphOf [ objNode 1L [ v ] ])

    Assert.Empty(highlightsAt server 1L "empty" 1 1)

[<Fact>]
let ``a builtin call name doesn't match a same-named verb-call name`` () =
    // notify(this); #2:notify();
    let v =
        verbNode
            1L
            (verbMeta 1 "mixed")
            [ ExprStmt(Call("notify", [ Normal(Ident("this", 1, 8)) ], 1, 1))
              ExprStmt(VerbCall(ObjLit 2L, StrLit "notify", [], 2, 1)) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let highlights = highlightsAt server 1L "mixed" 1 1

    Assert.Equal(1, highlights.Length)
