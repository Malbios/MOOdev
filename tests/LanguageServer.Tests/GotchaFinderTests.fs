/// Hand-built fixture graphs for `Handlers.findGotchas` - same reasoning as
/// `DeadVerbFinderTests.fs`: precise control over each verb's AST and perms
/// rather than depending on a larger synthetic corpus.
module LanguageServer.Tests.GotchaFinderTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (name: string) (perms: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = perms
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) (ast: Stmt list) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = Some ast
      DiagnosticCount = 0
      Tokens = None }

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

let private suspendCall = ExprStmt(Call("suspend", [ Normal(IntLit 0L) ], 1, 1))

// --- missing-x-bit -------------------------------------------------------

[<Fact>]
let ``a verb with a confirmed caller but no x bit is flagged missing-x-bit`` () =
    let caller = verbNode 1L (verbMeta 1 "caller" "rxd") [ ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 1, 1)) ]
    let target = verbNode 2L (verbMeta 1 "target" "rd") []
    let graph = graphOf [ objNode 1L [ caller ]; objNode 2L [ target ] ]

    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 2L && g.VerbName = "target" && g.Kind = "missing-x-bit"))

[<Fact>]
let ``a verb with a confirmed caller and the x bit is not flagged`` () =
    let caller = verbNode 1L (verbMeta 1 "caller" "rxd") [ ExprStmt(VerbCall(ObjLit 2L, StrLit "target", [], 1, 1)) ]
    let target = verbNode 2L (verbMeta 1 "target" "rxd") []
    let graph = graphOf [ objNode 1L [ caller ]; objNode 2L [ target ] ]

    let gotchas = findGotchas graph
    Assert.DoesNotContain(gotchas, (fun g -> g.ObjRef = 2L && g.VerbName = "target"))

[<Fact>]
let ``a verb missing the x bit but with no confirmed caller is not flagged (it's dead, not unreachable-from-a-real-caller)`` () =
    let orphan = verbNode 1L (verbMeta 1 "orphan" "rd") []
    let graph = graphOf [ objNode 1L [ orphan ] ]

    let gotchas = findGotchas graph
    Assert.DoesNotContain(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "orphan"))

// --- unbounded-loop -------------------------------------------------------

[<Fact>]
let ``a for-loop with no suspend anywhere in its body is flagged unbounded-loop`` () =
    let v =
        verbNode
            1L
            (verbMeta 1 "sweep" "rxd")
            [ ForList(
                  { Name = "x"; Line = 1; Col = 1 },
                  None,
                  Ident("things", 1, 1),
                  [ ExprStmt(Call("notify", [], 1, 1)) ]
              ) ]

    let graph = graphOf [ objNode 1L [ v ] ]
    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "sweep" && g.Kind = "unbounded-loop"))

[<Fact>]
let ``a for-loop with a direct suspend() call in its body is not flagged`` () =
    let v =
        verbNode
            1L
            (verbMeta 1 "sweep" "rxd")
            [ ForList({ Name = "x"; Line = 1; Col = 1 }, None, Ident("things", 1, 1), [ suspendCall ]) ]

    let graph = graphOf [ objNode 1L [ v ] ]
    let gotchas = findGotchas graph
    Assert.DoesNotContain(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "sweep" && g.Kind = "unbounded-loop"))

[<Fact>]
let ``a for-loop with suspend() reachable only through a nested if is not flagged`` () =
    let v =
        verbNode
            1L
            (verbMeta 1 "sweep" "rxd")
            [ ForList(
                  { Name = "x"; Line = 1; Col = 1 },
                  None,
                  Ident("things", 1, 1),
                  [ If([ Ident("cond", 1, 1), [ suspendCall ] ], None) ]
              ) ]

    let graph = graphOf [ objNode 1L [ v ] ]
    let gotchas = findGotchas graph
    Assert.DoesNotContain(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "sweep" && g.Kind = "unbounded-loop"))

[<Fact>]
let ``a for-loop with suspend() only inside a nested fork is still flagged (a different task's budget)`` () =
    let v =
        verbNode
            1L
            (verbMeta 1 "sweep" "rxd")
            [ ForList(
                  { Name = "x"; Line = 1; Col = 1 },
                  None,
                  Ident("things", 1, 1),
                  [ Fork(None, IntLit 0L, [ suspendCall ]) ]
              ) ]

    let graph = graphOf [ objNode 1L [ v ] ]
    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "sweep" && g.Kind = "unbounded-loop"))

[<Fact>]
let ``a while-loop with no suspend is flagged, one with suspend is not`` () =
    let noSuspend = verbNode 1L (verbMeta 1 "spin" "rxd") [ While(None, Ident("cond", 1, 1), [ ExprStmt(Call("notify", [], 1, 1)) ]) ]
    let withSuspend = verbNode 2L (verbMeta 1 "spin2" "rxd") [ While(None, Ident("cond", 1, 1), [ suspendCall ]) ]
    let graph = graphOf [ objNode 1L [ noSuspend ]; objNode 2L [ withSuspend ] ]

    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "spin" && g.Kind = "unbounded-loop"))
    Assert.DoesNotContain(gotchas, (fun g -> g.ObjRef = 2L && g.VerbName = "spin2"))

// --- zero-index -----------------------------------------------------------

[<Fact>]
let ``list[0] anywhere in a verb is flagged zero-index`` () =
    let v = verbNode 1L (verbMeta 1 "peek" "rxd") [ ExprStmt(Index(Ident("things", 1, 1), IntLit 0L)) ]
    let graph = graphOf [ objNode 1L [ v ] ]

    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "peek" && g.Kind = "zero-index"))

[<Fact>]
let ``list[1] and list[$] are not flagged zero-index`` () =
    let literalOne = verbNode 1L (verbMeta 1 "first" "rxd") [ ExprStmt(Index(Ident("things", 1, 1), IntLit 1L)) ]
    let lastIndex = verbNode 2L (verbMeta 1 "last" "rxd") [ ExprStmt(Index(Ident("things", 1, 1), LastIndex)) ]
    let graph = graphOf [ objNode 1L [ literalOne ]; objNode 2L [ lastIndex ] ]

    let gotchas = findGotchas graph
    Assert.DoesNotContain(gotchas, (fun g -> g.Kind = "zero-index"))

[<Fact>]
let ``list[0] nested inside a deeper expression is still found`` () =
    let v =
        verbNode
            1L
            (verbMeta 1 "peek" "rxd")
            [ ExprStmt(Binary(Add, IntLit 1L, Index(Ident("things", 1, 1), IntLit 0L))) ]

    let graph = graphOf [ objNode 1L [ v ] ]
    let gotchas = findGotchas graph
    Assert.Contains(gotchas, (fun g -> g.ObjRef = 1L && g.VerbName = "peek" && g.Kind = "zero-index"))
