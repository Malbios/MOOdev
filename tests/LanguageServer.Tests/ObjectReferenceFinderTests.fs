/// Hand-built fixture graphs for `Handlers.findReferencesToObject` - same
/// reasoning as `DeadVerbFinderTests.fs`: precise control over which call
/// sites/ownership links resolve to the target, rather than depending on
/// `HandlerTests.fs`'s larger synthetic corpus.
module LanguageServer.Tests.ObjectReferenceFinderTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (name: string) (owner: ObjRef) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = owner
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

let private objNode (num: ObjRef) (owner: ObjRef option) (verbs: VerbNode list) (properties: PropertyMeta list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = owner
      Flags = None
      Properties = properties
      Aliases = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

[<Fact>]
let ``a verb-call whose receiver resolves to the target is found`` () =
    let caller = verbNode 1L (verbMeta 1 "caller" 2L) [ ExprStmt(VerbCall(ObjLit 5L, StrLit "somev", [], 1, 1)) ]
    let graph = graphOf [ objNode 1L None [ caller ] []; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.Contains(refs, (fun r -> r.Kind = "verb-call" && r.ObjRef = 1L && r.Detail = "somev"))

[<Fact>]
let ``a verb-call to a different object is not found`` () =
    let caller = verbNode 1L (verbMeta 1 "caller" 2L) [ ExprStmt(VerbCall(ObjLit 9L, StrLit "somev", [], 1, 1)) ]
    let graph = graphOf [ objNode 1L None [ caller ] []; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.DoesNotContain(refs, (fun r -> r.ObjRef = 1L))

[<Fact>]
let ``an object owned by the target is found`` () =
    let graph = graphOf [ objNode 1L (Some 5L) [] []; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.Contains(refs, (fun r -> r.Kind = "object-owner" && r.ObjRef = 1L))

[<Fact>]
let ``a verb owned by the target is found`` () =
    let ownedVerb = verbNode 1L (verbMeta 1 "someverb" 5L) []
    let graph = graphOf [ objNode 1L None [ ownedVerb ] []; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.Contains(refs, (fun r -> r.Kind = "verb-owner" && r.ObjRef = 1L && r.Detail = "someverb"))

[<Fact>]
let ``a property owned by the target is found`` () =
    let prop: PropertyMeta = { Name = "someprop"; Owner = 5L; Perms = "r" }
    let graph = graphOf [ objNode 1L None [] [ prop ]; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.Contains(refs, (fun r -> r.Kind = "property-owner" && r.ObjRef = 1L && r.Detail = "someprop"))

[<Fact>]
let ``a target with no references at all returns an empty result`` () =
    let unrelatedVerb = verbNode 1L (verbMeta 1 "unrelated" 2L) []
    let graph = graphOf [ objNode 1L None [ unrelatedVerb ] []; objNode 5L None [] [] ]

    let refs = findReferencesToObject graph 5L
    Assert.Empty(refs)
