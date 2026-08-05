/// Hand-built fixture graphs for `Handlers.findTestVerbs` - same fixture
/// shape as `TodoFinderTests.fs`, minus the token stream (`findTestVerbs`
/// only ever looks at `VerbMeta.Names`, never a verb's source).
module LanguageServer.Tests.TestVerbFinderTests

open Xunit
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

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = None
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
let ``a verb whose primary name starts with test_ is found`` () =
    let v = verbNode 1L (verbMeta 1 [ "test_addition" ])
    let graph = graphOf [ objNode 1L [ v ] ]

    let tests = findTestVerbs graph
    Assert.Contains(tests, (fun t -> t.ObjRef = 1L && t.VerbName = "test_addition"))

[<Fact>]
let ``a verb matches via a non-primary alias too`` () =
    let v = verbNode 1L (verbMeta 1 [ "sweep"; "test_sweep" ])
    let graph = graphOf [ objNode 1L [ v ] ]

    let tests = findTestVerbs graph
    Assert.Contains(tests, (fun t -> t.ObjRef = 1L && t.VerbName = "test_sweep"))

[<Fact>]
let ``a verb with no test_-prefixed name/alias produces no entry`` () =
    let v = verbNode 1L (verbMeta 1 [ "sweep"; "clean" ])
    let graph = graphOf [ objNode 1L [ v ] ]

    Assert.Empty(findTestVerbs graph)

[<Fact>]
let ``test_ must prefix the name, not just appear anywhere in it`` () =
    let v = verbNode 1L (verbMeta 1 [ "run_test_later" ])
    let graph = graphOf [ objNode 1L [ v ] ]

    Assert.Empty(findTestVerbs graph)

[<Fact>]
let ``matches are collected across every object in the graph`` () =
    let v1 = verbNode 1L (verbMeta 1 [ "test_one" ])
    let v2 = verbNode 2L (verbMeta 1 [ "test_two" ])
    let graph = graphOf [ objNode 1L [ v1 ]; objNode 2L [ v2 ] ]

    let tests = findTestVerbs graph
    Assert.Contains(tests, (fun t -> t.ObjRef = 1L && t.VerbName = "test_one"))
    Assert.Contains(tests, (fun t -> t.ObjRef = 2L && t.VerbName = "test_two"))
