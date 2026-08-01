/// Hand-built fixture graphs for `Handlers.findDeadProperties` - same
/// reasoning as `DeadVerbFinderTests.fs`: precise control over which
/// occurrences resolve and which don't, rather than depending on
/// `HandlerTests.fs`'s larger synthetic corpus.
module LanguageServer.Tests.PropertyDeadReferenceFinderTests

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

let private propMeta (name: string) : PropertyMeta = { Name = name; Owner = 2L; Perms = "rc" }

/// Unlike `DeadVerbFinderTests.fs`'s `objNode` (which hardcodes
/// `Properties = []` - fine there, since that file never needs one), this
/// module's every fixture cares about `Properties`, so it takes the list
/// directly rather than adding a second helper.
let private objNode (num: ObjRef) (verbs: VerbNode list) (properties: string list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = None
      Flags = None
      Properties = properties |> List.map propMeta
      Aliases = [] }

let private objNodeWithParents (num: ObjRef) (parents: ObjRef list) (verbs: VerbNode list) (properties: string list) : ObjectNode =
    { objNode num verbs properties with Parents = parents }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

[<Fact>]
let ``a property read via a resolvable literal-objnum receiver is not reported dead`` () =
    let reader = verbNode 1L (verbMeta 1 "reader") [ ExprStmt(Prop(ObjLit 2L, StrLit "foo", 1, 1)) ]
    let graph = graphOf [ objNode 1L [ reader ] []; objNode 2L [] [ "foo" ] ]

    let dead = findDeadProperties graph
    Assert.DoesNotContain(dead, (fun d -> d.ObjRef = 2L && d.PropertyName = "foo"))

[<Fact>]
let ``a property with no readers anywhere is reported dead, not possibly-dynamic`` () =
    let graph = graphOf [ objNode 1L [] [ "orphan" ] ]

    let dead = findDeadProperties graph

    match dead |> Array.tryFind (fun d -> d.ObjRef = 1L && d.PropertyName = "orphan") with
    | Some entry -> Assert.False(entry.PossiblyDynamic)
    | None -> Assert.Fail "expected \"orphan\" to be reported dead"

[<Fact>]
let ``a property only reachable via an unresolvable receiver is flagged possibly-dynamic, not a clean dead hit`` () =
    // `player` is explicitly documented (Resolver.fs) as genuinely
    // unresolvable - unlike `this`, which resolves to the containing object.
    let reader = verbNode 1L (verbMeta 1 "reader") [ ExprStmt(Prop(Ident("player", 1, 1), StrLit "target", 1, 1)) ]
    let graph = graphOf [ objNode 1L [ reader ] []; objNode 2L [] [ "target" ] ]

    let dead = findDeadProperties graph

    match dead |> Array.tryFind (fun d -> d.ObjRef = 2L && d.PropertyName = "target") with
    | Some entry -> Assert.True(entry.PossiblyDynamic)
    | None -> Assert.Fail "expected \"target\" to be reported dead-but-possibly-dynamic"

[<Fact>]
let ``an assignment alone does not count as a read`` () =
    // this.foo = 1; - a write, never read back anywhere in the corpus.
    let writer = verbNode 1L (verbMeta 1 "writer") [ ExprStmt(Assign(Prop(Ident("this", 1, 1), StrLit "foo", 1, 6), IntLit 1L)) ]
    let graph = graphOf [ objNode 1L [ writer ] [ "foo" ] ]

    let dead = findDeadProperties graph
    Assert.Contains(dead, (fun d -> d.ObjRef = 1L && d.PropertyName = "foo"))

[<Fact>]
let ``a read elsewhere in the same verb as an assignment still counts as read`` () =
    // this.foo = 1; return this.foo; - the assignment doesn't shadow the
    // separate read a few lines later; only the assignment's own occurrence
    // is excluded, not the whole verb.
    let target = Prop(Ident("this", 1, 1), StrLit "foo", 1, 6)
    let read = Prop(Ident("this", 2, 1), StrLit "foo", 2, 8)
    let body = [ ExprStmt(Assign(target, IntLit 1L)); Return(Some read) ]
    let verb = verbNode 1L (verbMeta 1 "roundtrip") body
    let graph = graphOf [ objNode 1L [ verb ] [ "foo" ] ]

    let dead = findDeadProperties graph
    Assert.DoesNotContain(dead, (fun d -> d.ObjRef = 1L && d.PropertyName = "foo"))

[<Fact>]
let ``a property read via inheritance (declared on a parent) is not reported dead`` () =
    let reader = verbNode 2L (verbMeta 1 "reader") [ ExprStmt(Prop(Ident("this", 1, 1), StrLit "foo", 1, 1)) ]
    let parent = objNode 1L [] [ "foo" ]
    let child = objNodeWithParents 2L [ 1L ] [ reader ] []
    let graph = graphOf [ parent; child ]

    let dead = findDeadProperties graph
    Assert.DoesNotContain(dead, (fun d -> d.ObjRef = 1L && d.PropertyName = "foo"))

[<Fact>]
let ``a computed property name access marks every property on its resolved object possibly-dynamic`` () =
    // this.(name) - the name is a variable, not a literal; can't tell which
    // property is actually read, so every property this object declares is
    // treated as possibly reached this way.
    let computed = Prop(ObjLit 1L, Ident("name", 1, 1), 1, 5)
    let reader = verbNode 1L (verbMeta 1 "reader") [ ExprStmt computed ]
    let graph = graphOf [ objNode 1L [ reader ] [ "foo" ] ]

    let dead = findDeadProperties graph

    match dead |> Array.tryFind (fun d -> d.ObjRef = 1L && d.PropertyName = "foo") with
    | Some entry -> Assert.True(entry.PossiblyDynamic)
    | None -> Assert.Fail "expected \"foo\" to be reported dead-but-possibly-dynamic"
