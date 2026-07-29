module Sidecar.Tests.ImporterTests

open Xunit
open Sidecar.Exporter
open Sidecar.TreeParser
open Sidecar.Importer

let private prop name value : PropertyExport =
    { Name = name; Owner = 2L; Perms = "rc"; ValueLiteral = value }

let private verb names code : VerbExport =
    { Names = names
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this"
      Code = code }

let private parsed parents props verbs : ParsedObject =
    { SelfCorponym = "room"
      Parents = parents
      Owner = 2L
      Flags = []
      Properties = props
      Verbs = verbs
      Name = None
      Aliases = [] }

let private resolveAll: ParentRef -> int64 option =
    function
    | ByObjnum n -> Some n
    | ByCorponym _ -> Some 999L // not exercised by these tests directly

[<Fact>]
let ``a corponym with no current object needs create and gets every property/verb added`` () =
    let desired = parsed [ ByObjnum 1L ] [ prop "description" "\"a room\"" ] [ verb "look_self" [ "return;" ] ]

    let plan = planObject "room" desired None resolveAll

    Assert.True(plan.NeedsCreate)
    Assert.Equal<PropertyOp list>([ AddProperty(prop "description" "\"a room\"") ], plan.PropertyOps)
    Assert.Equal(Some [ verb "look_self" [ "return;" ] ], plan.VerbReorder)
    Assert.Empty(plan.VerbOps)

[<Fact>]
let ``identical desired and current produce no operations at all`` () =
    let props = [ prop "description" "\"a room\"" ]
    let verbs = [ verb "look_self" [ "return;" ] ]
    let desired = parsed [ ByObjnum 1L ] props verbs

    let current: ObjectExport =
        { Parents = [ 1L ]
          Owner = 2L
          Flags = []
          Properties = props
          Verbs = verbs
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.False(plan.NeedsCreate)
    Assert.Equal(ParentsUnchanged, plan.ParentsPreview)
    Assert.Empty(plan.PropertyOps)
    Assert.Equal(None, plan.VerbReorder)
    Assert.Empty(plan.VerbOps)

[<Fact>]
let ``property value change is detected without touching property info`` () =
    let desired = parsed [] [ prop "description" "\"new text\"" ] []

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = [ prop "description" "\"old text\"" ]
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal<PropertyOp list>([ UpdatePropertyValue("description", "\"new text\"") ], plan.PropertyOps)

[<Fact>]
let ``property owner/perms change is detected separately from value`` () =
    let desired = parsed [] [ { prop "description" "\"same\"" with Perms = "r" } ] []

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = [ prop "description" "\"same\"" ]
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal<PropertyOp list>([ UpdatePropertyInfo("description", 2L, "r") ], plan.PropertyOps)

[<Fact>]
let ``a property removed from the tree is deleted on the target`` () =
    let desired = parsed [] [] []

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = [ prop "obsolete" "1" ]
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal<PropertyOp list>([ DeleteProperty "obsolete" ], plan.PropertyOps)

[<Fact>]
let ``adding a verb triggers a reorder even though nothing else changed`` () =
    let v1 = verb "look_self" [ "return;" ]
    let v2 = verb "tell_lines" [ "return;" ]
    let desired = parsed [] [] [ v1; v2 ]

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ v1 ]
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal(Some [ v1; v2 ], plan.VerbReorder)
    Assert.Empty(plan.VerbOps)

[<Fact>]
let ``reordering the same verb set (no other change) is a reorder, not a no-op`` () =
    let v1 = verb "look_self" [ "return;" ]
    let v2 = verb "tell_lines" [ "return;" ]
    let desired = parsed [] [] [ v2; v1 ] // swapped

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ v1; v2 ]
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal(Some [ v2; v1 ], plan.VerbReorder)

[<Fact>]
let ``verb code change with unchanged set and order yields a targeted UpdateVerbCode, not a reorder`` () =
    let currentVerb = verb "look_self" [ "old code;" ]
    let desiredVerb = verb "look_self" [ "new code;" ]
    let desired = parsed [] [] [ desiredVerb ]

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ currentVerb ]
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal(None, plan.VerbReorder)
    Assert.Equal<VerbOp list>([ UpdateVerbCode("look_self", [ "new code;" ]) ], plan.VerbOps)

[<Fact>]
let ``verb args change (dobj/prep/iobj) is detected as UpdateVerbArgs`` () =
    let currentVerb = verb "look_self" [ "return;" ]
    let desiredVerb = { currentVerb with Dobj = "any" }
    let desired = parsed [] [] [ desiredVerb ]

    let current: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ currentVerb ]
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) resolveAll

    Assert.Equal<VerbOp list>([ UpdateVerbArgs("look_self", "any", "none", "this") ], plan.VerbOps)

[<Fact>]
let ``parents preview is unresolvable when a parent corponym doesn't exist on target yet`` () =
    let desired = parsed [ ByCorponym "not_yet_created" ] [] []

    let plan = planObject "room" desired None (fun _ -> None)

    Assert.Equal(ParentsUnresolvableAtPlanTime, plan.ParentsPreview)

[<Fact>]
let ``parents preview reports the resolved change when everything is resolvable`` () =
    let desired = parsed [ ByCorponym "generic_room" ] [] []

    let current: ObjectExport =
        { Parents = [ 1L ]
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let plan = planObject "room" desired (Some current) (fun _ -> Some 4L)

    Assert.Equal(ParentsWillChange [ 4L ], plan.ParentsPreview)

[<Fact>]
let ``describePlan reports no changes for an all-unchanged plan`` () =
    let plan =
        { Objects =
            [ { Corponym = "room"
                NeedsCreate = false
                DesiredParents = []
                ParentsPreview = ParentsUnchanged
                PropertyOps = []
                VerbReorder = None
                VerbOps = [] } ] }

    Assert.Equal("No changes.", describePlan plan)
