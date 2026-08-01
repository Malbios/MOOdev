/// Hand-built fixture graphs for `Handlers.getCallGraph` - same reasoning
/// as `GotchaFinderTests.fs`/`DeadVerbFinderTests.fs`: precise control over
/// each verb's AST rather than depending on a larger synthetic corpus.
module LanguageServer.Tests.CallGraphTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (names: string list) : VerbMeta =
    { Index = index
      Names = names
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

let private callVerb (receiver: ObjRef) (name: string) : Stmt =
    ExprStmt(VerbCall(ObjLit receiver, StrLit name, [], 1, 1))

/// Shared chain for most tests below: #1:a_verb calls #2:b_verb, which
/// calls #3:c_verb (a leaf, no further calls).
let private chainGraph =
    let aVerb = verbNode 1L (verbMeta 1 [ "a_verb" ]) [ callVerb 2L "b_verb" ]
    let bVerb = verbNode 2L (verbMeta 1 [ "b_verb"; "bv" ]) [ callVerb 3L "c_verb" ]
    let cVerb = verbNode 3L (verbMeta 1 [ "c_verb" ]) []
    graphOf [ objNode 1L [ aVerb ]; objNode 2L [ bVerb ]; objNode 3L [ cVerb ] ]

[<Fact>]
let ``a middle verb's call graph shows one caller and one callee`` () =
    let result = getCallGraph chainGraph 2L "b_verb"
    Assert.Equal<(ObjRef * string)[]>([| 3L, "c_verb" |], result.Callees |> Array.map (fun n -> n.ObjRef, n.VerbName))
    Assert.Equal<(ObjRef * string)[]>([| 1L, "a_verb" |], result.Callers |> Array.map (fun n -> n.ObjRef, n.VerbName))

[<Fact>]
let ``the root of the chain has a callee but no callers`` () =
    let result = getCallGraph chainGraph 1L "a_verb"
    Assert.Equal<(ObjRef * string)[]>([| 2L, "b_verb" |], result.Callees |> Array.map (fun n -> n.ObjRef, n.VerbName))
    Assert.Empty(result.Callers)

[<Fact>]
let ``the leaf of the chain has a caller but no callees`` () =
    let result = getCallGraph chainGraph 3L "c_verb"
    Assert.Empty(result.Callees)
    Assert.Equal<(ObjRef * string)[]>([| 2L, "b_verb" |], result.Callers |> Array.map (fun n -> n.ObjRef, n.VerbName))

[<Fact>]
let ``querying by a non-primary alias resolves the same as the primary name`` () =
    let result = getCallGraph chainGraph 2L "bv"
    Assert.Equal<(ObjRef * string)[]>([| 3L, "c_verb" |], result.Callees |> Array.map (fun n -> n.ObjRef, n.VerbName))
    Assert.Equal<(ObjRef * string)[]>([| 1L, "a_verb" |], result.Callers |> Array.map (fun n -> n.ObjRef, n.VerbName))

[<Fact>]
let ``a verb name that doesn't exist on the given object returns empty, not a crash`` () =
    let result = getCallGraph chainGraph 2L "no_such_verb"
    Assert.Empty(result.Callees)
    Assert.Empty(result.Callers)

[<Fact>]
let ``calling the same callee twice from the same verb only yields one callee edge`` () =
    let v = verbNode 1L (verbMeta 1 [ "dup_caller" ]) [ callVerb 2L "target"; callVerb 2L "target" ]
    let target = verbNode 2L (verbMeta 1 [ "target" ]) []
    let graph = graphOf [ objNode 1L [ v ]; objNode 2L [ target ] ]

    let result = getCallGraph graph 1L "dup_caller"
    Assert.Equal<(ObjRef * string)[]>([| 2L, "target" |], result.Callees |> Array.map (fun n -> n.ObjRef, n.VerbName))

[<Fact>]
let ``a call to a nonexistent object's verb is not resolved as an edge`` () =
    let v = verbNode 1L (verbMeta 1 [ "orphan_caller" ]) [ callVerb 99L "ghost" ]
    let graph = graphOf [ objNode 1L [ v ] ]

    let result = getCallGraph graph 1L "orphan_caller"
    Assert.Empty(result.Callees)
