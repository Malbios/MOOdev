/// Hand-built fixture graphs for `MooLspServer.TextDocumentFoldingRange` -
/// same reasoning as `DocumentHighlightTests.fs`: precise control over each
/// verb's AST rather than depending on a larger synthetic corpus.
/// `computeFoldingRanges` itself is private, so (matching this project's
/// existing convention for `maxNestingDepth`/`VerbMetricsTests.fs`) it's
/// only exercised indirectly, through the public LSP override.
/// `TextDocumentFoldingRange` never touches the bridge, so the fake bridge
/// here just throws if ever called - a canary for that invariant breaking.
module LanguageServer.Tests.FoldingRangeTests

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
      GetBuiltins = fun () -> Task.FromException<Map<string, BuiltinFunc> option>(exn "should not be called") }

let private serverFor (graph: Graph) : MooLspServer =
    new MooLspServer(new MooLspClient(), graph, neverCalledBridge)

let private foldingRangesFor (server: MooLspServer) (objRef: ObjRef) (verbName: string) : FoldingRange[] =
    let p: FoldingRangeParams =
        { TextDocument = { Uri = moodevVerbUri objRef verbName }
          WorkDoneToken = None
          PartialResultToken = None }

    match Async.RunSynchronously(server.TextDocumentFoldingRange p) with
    | Ok(Some ranges) -> ranges
    | Ok None -> [||]
    | Error e -> failwithf "unexpected error: %A" e

[<Fact>]
let ``a single if with a multi-line body folds from the condition's line to the body's last line`` () =
    // if (cond)         -- line 1
    //   notify(this);   -- line 2
    // endif
    let v =
        verbNode
            1L
            (verbMeta 1 "single")
            [ If([ Ident("cond", 1, 1), [ ExprStmt(Call("notify", [ Normal(Ident("this", 2, 8)) ], 2, 1)) ] ], None) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let ranges = foldingRangesFor server 1L "single"

    Assert.Equal(1, ranges.Length)
    Assert.Equal(0u, ranges.[0].StartLine) // 1-based line 1 -> 0-based 0
    Assert.Equal(1u, ranges.[0].EndLine) // 1-based line 2 -> 0-based 1

[<Fact>]
let ``a nested if inside a for gets its own independent fold in addition to the outer for's`` () =
    // for x in (things)     -- line 1
    //   if (cond)            -- line 2
    //     notify(this);      -- line 3
    //   endif
    // endfor
    let v =
        verbNode
            1L
            (verbMeta 1 "nested")
            [ ForList(
                  { Name = "x"; Line = 1; Col = 1 },
                  None,
                  Ident("things", 1, 10),
                  [ If([ Ident("cond", 2, 1), [ ExprStmt(Call("notify", [ Normal(Ident("this", 3, 8)) ], 3, 1)) ] ], None) ]
              ) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let ranges = foldingRangesFor server 1L "nested"

    Assert.Equal(2, ranges.Length)
    Assert.Contains(ranges, (fun r -> r.StartLine = 0u && r.EndLine = 2u)) // outer for: line 1 -> line 3
    Assert.Contains(ranges, (fun r -> r.StartLine = 1u && r.EndLine = 2u)) // inner if: line 2 -> line 3

[<Fact>]
let ``a for-range loop with literal bounds still folds, via its own loop variable's position`` () =
    // for i in [1..3]     -- line 1 (both `1` and `3` are IntLit - no
    //   notify(i);        -- line 2   position at all, unlike an Ident/Call
    // endfor                bound)
    let v =
        verbNode
            1L
            (verbMeta 1 "literalbounds")
            [ ForRange({ Name = "i"; Line = 1; Col = 5 }, IntLit 1L, IntLit 3L, [ ExprStmt(Call("notify", [ Normal(Ident("i", 2, 8)) ], 2, 1)) ]) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let ranges = foldingRangesFor server 1L "literalbounds"

    Assert.Equal(1, ranges.Length)
    Assert.Equal(0u, ranges.[0].StartLine) // 1-based line 1 -> 0-based 0
    Assert.Equal(1u, ranges.[0].EndLine) // 1-based line 2 -> 0-based 1

[<Fact>]
let ``a fork with a literal delay and no bound name still folds, via the body's own earliest line`` () =
    // fork (0)            -- line 1 (delay is IntLit, unbound - no
    //   notify(this);     -- line 2   position anywhere in the header)
    // endfork
    let v =
        verbNode 1L (verbMeta 1 "literalfork") [ Fork(None, IntLit 0L, [ ExprStmt(Call("notify", [ Normal(Ident("this", 2, 8)) ], 2, 1)) ]) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let ranges = foldingRangesFor server 1L "literalfork"

    // No header position at all (no bound name, literal delay) - falls
    // back to the body's own earliest line for both endpoints, which
    // collapses to a single line and correctly yields no fold (a
    // documented approximation limit, not a crash).
    Assert.Empty(ranges)

[<Fact>]
let ``a block whose condition and body share one line is not folded (needs at least 2 lines)`` () =
    // while (cond) notify(this); endwhile   -- all on line 1
    let v =
        verbNode
            1L
            (verbMeta 1 "oneline")
            [ While(None, Ident("cond", 1, 1), [ ExprStmt(Call("notify", [ Normal(Ident("this", 1, 20)) ], 1, 10)) ]) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    Assert.Empty(foldingRangesFor server 1L "oneline")

[<Fact>]
let ``each except arm folds independently, not merged with the try body or each other`` () =
    // try                        -- line 1
    //   risky();                 -- line 2
    // except e1 (ANY)            -- line 3
    //   a(); b();                -- lines 4-5
    // except e2 (ANY)            -- line 6
    //   c(); d();                -- lines 7-8
    // endtry
    let arm1: ExceptArm =
        { Name = None
          Codes = AnyCode
          Body = [ ExprStmt(Call("a", [], 4, 1)); ExprStmt(Call("b", [], 5, 1)) ] }

    let arm2: ExceptArm =
        { Name = None
          Codes = AnyCode
          Body = [ ExprStmt(Call("c", [], 7, 1)); ExprStmt(Call("d", [], 8, 1)) ] }

    let v = verbNode 1L (verbMeta 1 "trycatch") [ TryExcept([ ExprStmt(Call("risky", [], 2, 1)) ], [ arm1; arm2 ]) ]

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    let ranges = foldingRangesFor server 1L "trycatch"

    // The one-statement try body (line 2 only) isn't foldable on its own -
    // only the two multi-line arms are.
    Assert.Equal(2, ranges.Length)
    Assert.Contains(ranges, (fun r -> r.StartLine = 3u && r.EndLine = 4u)) // arm1: line 4 -> line 5
    Assert.Contains(ranges, (fun r -> r.StartLine = 6u && r.EndLine = 7u)) // arm2: line 7 -> line 8

[<Fact>]
let ``an unparsed verb returns no folding ranges`` () =
    let v =
        { Meta = verbMeta 1 "broken"
          DefinedOn = 1L
          SourcePath = None
          Ast = None
          DiagnosticCount = 0
          Tokens = None }

    let server = serverFor (graphOf [ objNode 1L [ v ] ])
    Assert.Empty(foldingRangesFor server 1L "broken")
