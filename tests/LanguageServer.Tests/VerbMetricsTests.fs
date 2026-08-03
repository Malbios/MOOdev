/// Hand-built fixture graphs for `Handlers.computeVerbMetrics` - same
/// reasoning as `GotchaFinderTests.fs`/`CallGraphTests.fs`: precise control
/// over each verb's AST/tokens rather than depending on a larger synthetic
/// corpus.
module LanguageServer.Tests.VerbMetricsTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

// Deliberately not `open Language.Lexer` - its `Keyword.While`/etc. case
// names collide with `Ast.Stmt`'s own `While`/`Fork`/... constructors, so
// `Token`/`TokenKind` stay fully qualified below instead.

let private verbMeta (index: int) (name: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private tokensSpanningLines (firstLine: int) (lastLine: int) : Language.Lexer.Token[] =
    [| { Kind = Language.Lexer.TEOF; Line = firstLine; Col = 1 }
       { Kind = Language.Lexer.TEOF; Line = lastLine; Col = 1 } |]

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) (ast: Stmt list) (tokens: Language.Lexer.Token[]) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = Some ast
      DiagnosticCount = 0
      Tokens = Some tokens }

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

// --- line count --------------------------------------------------------

[<Fact>]
let ``line count spans from the first to the last token's line, inclusive`` () =
    let v = verbNode 1L (verbMeta 1 "spans") [] (tokensSpanningLines 5 12)
    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "spans" && m.LineCount = 8))

[<Fact>]
let ``a verb with no tokens gets a zero line count, not an exception`` () =
    let v = verbNode 1L (verbMeta 1 "empty") [] [||]
    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "empty" && m.LineCount = 0))

// --- call count ----------------------------------------------------------

[<Fact>]
let ``a verb called from two different call sites has call count 2`` () =
    let target = verbNode 2L (verbMeta 1 "target") [] (tokensSpanningLines 1 1)

    let callerA =
        verbNode 1L (verbMeta 1 "callerA") [ ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 1, 1)) ] (tokensSpanningLines 1 1)

    let callerB =
        verbNode 1L (verbMeta 2 "callerB") [ ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 1, 1)) ] (tokensSpanningLines 1 1)

    let graph = graphOf [ objNode 1L [ callerA; callerB ]; objNode 2L [ target ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 2L && m.VerbName = "target" && m.CallCount = 2))

[<Fact>]
let ``an uncalled verb has call count 0`` () =
    let v = verbNode 1L (verbMeta 1 "lonely") [] (tokensSpanningLines 1 1)
    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "lonely" && m.CallCount = 0))

// --- max nesting depth -----------------------------------------------------

[<Fact>]
let ``a flat verb body with no nesting has max depth 0`` () =
    let v =
        verbNode 1L (verbMeta 1 "flat") [ ExprStmt(Call("notify", [], 1, 1)); Return(Some(IntLit 1L)) ] (tokensSpanningLines 1 1)

    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "flat" && m.MaxDepth = 0))

[<Fact>]
let ``a triply-nested if inside a for inside a while has max depth 3`` () =
    let innerIf = If([ Ident("cond", 1, 1), [ ExprStmt(Call("notify", [], 1, 1)) ] ], None)

    let forLoop =
        ForList({ Name = "x"; Line = 1; Col = 1 }, None, Ident("things", 1, 1), [ innerIf ])

    let whileLoop = While(None, Ident("cond2", 1, 1), [ forLoop ])

    let v = verbNode 1L (verbMeta 1 "nested") [ whileLoop ] (tokensSpanningLines 1 1)
    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "nested" && m.MaxDepth = 3))

[<Fact>]
let ``depth is the deepest branch, not the sum of all branches`` () =
    let shallowArm = Ident("a", 1, 1), [ ExprStmt(Call("notify", [], 1, 1)) ]

    let deepArm =
        Ident("b", 1, 1), [ While(None, Ident("cond", 1, 1), [ ExprStmt(Call("notify", [], 1, 1)) ]) ]

    let v = verbNode 1L (verbMeta 1 "branchy") [ If([ shallowArm; deepArm ], None) ] (tokensSpanningLines 1 1)
    let graph = graphOf [ objNode 1L [ v ] ]

    let metrics = computeVerbMetrics graph
    Assert.Contains(metrics, (fun m -> m.ObjRef = 1L && m.VerbName = "branchy" && m.MaxDepth = 2))

[<Fact>]
let ``a verb with no parsed AST is skipped entirely, not reported with zeroed metrics`` () =
    let unparsed =
        { Meta = verbMeta 1 "unparsed"
          DefinedOn = 1L
          SourcePath = None
          Ast = None
          DiagnosticCount = 0
          Tokens = None }

    let graph = graphOf [ objNode 1L [ unparsed ] ]

    let metrics = computeVerbMetrics graph
    Assert.DoesNotContain(metrics, (fun m -> m.VerbName = "unparsed"))
