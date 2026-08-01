/// Hand-built fixture graphs for `Handlers.findPermissionRisks` - same
/// reasoning as `GotchaFinderTests.fs`/`DeadVerbFinderTests.fs`: precise
/// control over each verb/property's owner and perms rather than depending
/// on a larger synthetic corpus.
module LanguageServer.Tests.PermissionRiskFinderTests

open Xunit
open Metadata.Schema
open LanguageServer.Handlers

let private wizardFlags: ObjectFlags =
    { Player = false
      Programmer = true
      Wizard = true
      Read = true
      Write = true
      Fertile = true
      Anonymous = false }

let private nonWizardFlags: ObjectFlags =
    { Player = false
      Programmer = true
      Wizard = false
      Read = true
      Write = true
      Fertile = true
      Anonymous = false }

let private verbMeta (name: string) (owner: ObjRef) (perms: string) : VerbMeta =
    { Index = 1
      Names = [ name ]
      Owner = owner
      Perms = perms
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

let private objNode (num: ObjRef) (flags: ObjectFlags option) (verbs: VerbNode list) (properties: PropertyMeta list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = None
      Flags = flags
      Properties = properties
      Aliases = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

// The wizard owner object itself (#2) and a non-wizard owner object (#3) -
// shared by every test below, same convention as `HandlerTests.fs`'s own
// fixture numbering (wizard = #2).
let private wizardOwner = objNode 2L (Some wizardFlags) [] []
let private nonWizardOwner = objNode 3L (Some nonWizardFlags) [] []

// --- wizard-writable-verb --------------------------------------------------

[<Fact>]
let ``a writable verb owned by a wizard-flagged object is flagged wizard-writable-verb`` () =
    let v = verbNode 10L (verbMeta "risky" 2L "rwxd")
    let graph = graphOf [ wizardOwner; objNode 10L None [ v ] [] ]

    let risks = findPermissionRisks graph
    Assert.Contains(risks, (fun r -> r.ObjRef = 10L && r.Name = "risky" && r.Kind = "wizard-writable-verb"))

[<Fact>]
let ``a non-writable verb owned by a wizard is not flagged`` () =
    let v = verbNode 10L (verbMeta "safe" 2L "rxd")
    let graph = graphOf [ wizardOwner; objNode 10L None [ v ] [] ]

    let risks = findPermissionRisks graph
    Assert.DoesNotContain(risks, (fun r -> r.ObjRef = 10L && r.Name = "safe"))

[<Fact>]
let ``a writable verb owned by a non-wizard is not flagged wizard-writable-verb`` () =
    let v = verbNode 10L (verbMeta "shared" 3L "rwxd")
    let graph = graphOf [ nonWizardOwner; objNode 10L None [ v ] [] ]

    let risks = findPermissionRisks graph
    Assert.DoesNotContain(risks, (fun r -> r.ObjRef = 10L && r.Name = "shared"))

// --- world-writable-property ------------------------------------------------

[<Fact>]
let ``a writable property is flagged world-writable-property regardless of owner`` () =
    let graph =
        graphOf [ nonWizardOwner; objNode 10L None [] [ { Name = "data"; Owner = 3L; Perms = "rw" } ] ]

    let risks = findPermissionRisks graph
    Assert.Contains(risks, (fun r -> r.ObjRef = 10L && r.Name = "data" && r.Kind = "world-writable-property"))

[<Fact>]
let ``a read-only property is not flagged`` () =
    let graph =
        graphOf [ wizardOwner; objNode 10L None [] [ { Name = "data"; Owner = 2L; Perms = "r" } ] ]

    let risks = findPermissionRisks graph
    Assert.DoesNotContain(risks, (fun r -> r.ObjRef = 10L && r.Name = "data"))
