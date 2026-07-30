/// Hand-built fixture graphs for `Handlers.findDeadVerbs` - same reasoning
/// as `Metadata.Tests.ResolverTests`: precise control over which call sites
/// resolve and which don't, rather than depending on `HandlerTests.fs`'s
/// larger synthetic corpus (built for LSP-position-based lookups, not this).
module LanguageServer.Tests.DeadVerbFinderTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

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

[<Fact>]
let ``a verb called via a resolvable literal-objnum receiver is not reported dead`` () =
    let caller = verbNode 1L (verbMeta 1 "caller") [ ExprStmt(VerbCall(ObjLit 2L, StrLit "referenced", [], 1, 1)) ]
    let referenced = verbNode 2L (verbMeta 1 "referenced") []
    let graph = graphOf [ objNode 1L [ caller ]; objNode 2L [ referenced ] ]

    let dead = findDeadVerbs graph
    Assert.DoesNotContain(dead, (fun d -> d.ObjRef = 2L && d.VerbName = "referenced"))

[<Fact>]
let ``a verb with no callers anywhere is reported dead, not possibly-dynamic`` () =
    let orphan = verbNode 1L (verbMeta 1 "orphan") []
    let graph = graphOf [ objNode 1L [ orphan ] ]

    let dead = findDeadVerbs graph

    match dead |> Array.tryFind (fun d -> d.ObjRef = 1L && d.VerbName = "orphan") with
    | Some entry -> Assert.False(entry.PossiblyDynamic)
    | None -> Assert.Fail "expected \"orphan\" to be reported dead"

[<Fact>]
let ``a verb only reachable via an unresolvable receiver is flagged possibly-dynamic, not a clean dead hit`` () =
    // `player` is explicitly documented (Resolver.fs) as genuinely
    // unresolvable - unlike `this`, which resolves to the containing object.
    let caller = verbNode 1L (verbMeta 1 "caller") [ ExprStmt(VerbCall(Ident("player", 1, 1), StrLit "target", [], 1, 1)) ]
    let target = verbNode 2L (verbMeta 1 "target") []
    let graph = graphOf [ objNode 1L [ caller ]; objNode 2L [ target ] ]

    let dead = findDeadVerbs graph

    match dead |> Array.tryFind (fun d -> d.ObjRef = 2L && d.VerbName = "target") with
    | Some entry -> Assert.True(entry.PossiblyDynamic)
    | None -> Assert.Fail "expected \"target\" to be reported dead-but-possibly-dynamic"
