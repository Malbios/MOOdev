/// Hand-built fixture graphs for `Resolver.fs`'s two ported algorithms -
/// deliberately not the real `Survive` corpus (that's `LoaderTests.fs`'s
/// job), since these need precise control over parent order, duplicate
/// paths, and permission bits to exercise the traversal's actual decision
/// points.
module Metadata.Tests.ResolverTests

open Xunit
open Language.Ast
open Metadata.Schema
open Metadata.Resolver

let private verbMeta (index: int) (names: string) (perms: string) : VerbMeta =
    { Index = index
      Names = names.Split(' ') |> List.ofArray
      Owner = 2L
      Perms = perms
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private verbNode (meta: VerbMeta) : VerbNode =
    { Meta = meta
      DefinedOn = 0L
      SourcePath = None
      Ast = None
      DiagnosticCount = 0
      Tokens = None }

let private objNode (num: ObjRef) (parents: ObjRef list) (verbs: VerbNode list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = parents
      Children = []
      Verbs = verbs |> List.map (fun v -> { v with DefinedOn = num })
      Owner = None
      Flags = None
      Properties = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

let private graphWithSysobjProps (objects: ObjectNode list) (props: (string * ObjRef) list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.ofList props
      Builtins = Map.empty }

// --- verbNameMatchesAny --------------------------------------------------

[<Theory>]
[<InlineData("look", "look", true)>]
[<InlineData("look", "loo", false)>]
[<InlineData("l*ook", "l", true)>]
[<InlineData("l*ook", "lo", true)>]
[<InlineData("l*ook", "loo", true)>]
[<InlineData("l*ook", "look", true)>]
[<InlineData("l*ook", "looks", false)>]
[<InlineData("get*", "get", true)>]
[<InlineData("get*", "getaway", true)>]
[<InlineData("*", "anything", true)>]
[<InlineData("*", "", true)>]
[<InlineData("look", "LOOK", true)>]
[<InlineData("LOOK", "look", true)>]
[<InlineData("look", "", false)>]
let ``verbNameMatchesAny matches a single pattern per verbcasecmp rules`` (pattern: string) (candidate: string) (expected: bool) =
    Assert.Equal(expected, verbNameMatchesAny [ pattern ] candidate)

[<Fact>]
let ``verbNameMatchesAny checks every space-separated name`` () =
    let names = [ "s"; "ies"; "es" ]
    Assert.True(verbNameMatchesAny names "es")
    Assert.False(verbNameMatchesAny names "ing")

// --- findCallableVerb -----------------------------------------------------

[<Fact>]
let ``finds a verb defined directly on the starting object`` () =
    let foo = verbNode (verbMeta 1 "foo" "rxd")
    let graph = graphOf [ objNode 1L [] [ foo ] ]

    match findCallableVerb graph 1L "foo" with
    | Some(definer, v) ->
        Assert.Equal(1L, definer)
        Assert.Equal("foo", (List.head v.Meta.Names))
    | None -> Assert.Fail "expected a match"

[<Fact>]
let ``walks a single-parent chain to find an inherited verb`` () =
    let root = objNode 1L [] [ verbNode (verbMeta 1 "foo" "rxd") ]
    let child = objNode 2L [ 1L ] []
    let grandchild = objNode 3L [ 2L ] []
    let graph = graphOf [ root; child; grandchild ]

    match findCallableVerb graph 3L "foo" with
    | Some(definer, _) -> Assert.Equal(1L, definer)
    | None -> Assert.Fail "expected a match"

[<Fact>]
let ``a verb without the exec bit is not callable`` () =
    let root = objNode 1L [] [ verbNode (verbMeta 1 "foo" "rd") ] // no 'x'
    let graph = graphOf [ root ]

    Assert.True((findCallableVerb graph 1L "foo").IsNone)

[<Fact>]
let ``diamond inheritance: left-to-right depth-first picks the first parent's ancestor over the second's`` () =
    // D's parents = [B, C]; both B and C descend from A, but B and C each
    // define their own competing "foo" - depth-first through B (the first
    // parent) must win before C is ever considered.
    let bFoo = verbNode (verbMeta 1 "foo" "rxd")
    let cFoo = verbNode (verbMeta 1 "foo" "rxd")
    let a = objNode 1L [] []
    let b = objNode 2L [ 1L ] [ bFoo ]
    let c = objNode 3L [ 1L ] [ cFoo ]
    let d = objNode 4L [ 2L; 3L ] []
    let graph = graphOf [ a; b; c; d ]

    match findCallableVerb graph 4L "foo" with
    | Some(definer, _) -> Assert.Equal(2L, definer) // B, not C
    | None -> Assert.Fail "expected a match"

[<Fact>]
let ``diamond inheritance: a verb only the shared ancestor defines is still found`` () =
    let a = objNode 1L [] [ verbNode (verbMeta 1 "shared" "rxd") ]
    let b = objNode 2L [ 1L ] []
    let c = objNode 3L [ 1L ] []
    let d = objNode 4L [ 2L; 3L ] []
    let graph = graphOf [ a; b; c; d ]

    match findCallableVerb graph 4L "shared" with
    | Some(definer, _) -> Assert.Equal(1L, definer)
    | None -> Assert.Fail "expected a match"

[<Fact>]
let ``a dangling parent reference (invalid object) is skipped, not an error`` () =
    // 99L is listed as a parent but has no ObjectNode - matches
    // `dbpriv_find_object` returning null for an invalid object.
    let child = objNode 1L [ 99L ] []
    let graph = graphOf [ child ]

    Assert.True((findCallableVerb graph 1L "foo").IsNone)

[<Fact>]
let ``no match anywhere in the ancestor chain returns None`` () =
    let root = objNode 1L [] [ verbNode (verbMeta 1 "bar" "rxd") ]
    let child = objNode 2L [ 1L ] []
    let graph = graphOf [ root; child ]

    Assert.True((findCallableVerb graph 2L "foo").IsNone)

[<Fact>]
let ``verb order on the same object: first match wins`` () =
    let first = verbNode (verbMeta 1 "get" "rxd")
    let second = verbNode (verbMeta 2 "get take" "rxd")
    let graph = graphOf [ objNode 1L [] [ first; second ] ]

    match findCallableVerb graph 1L "get" with
    | Some(_, v) -> Assert.Equal(1, v.Meta.Index)
    | None -> Assert.Fail "expected a match"

// --- resolveReceiver --------------------------------------------------

[<Fact>]
let ``resolveReceiver: a literal object reference resolves directly`` () =
    let graph = graphOf []
    Assert.Equal(Some 123L, resolveReceiver graph (ObjLit 123L))

[<Fact>]
let ``resolveReceiver: $name resolves via the real #0 property registry`` () =
    let graph = graphWithSysobjProps [] [ "vcs", 127L ]
    let dollarVcs = Prop(ObjLit 0L, StrLit "vcs", 1, 1)
    Assert.Equal(Some 127L, resolveReceiver graph dollarVcs)

[<Fact>]
let ``resolveReceiver: $name lookup is ASCII case-insensitive, matching MOO property semantics`` () =
    let graph = graphWithSysobjProps [] [ "VCS", 127L ]
    let dollarVcs = Prop(ObjLit 0L, StrLit "vcs", 1, 1)
    Assert.Equal(Some 127L, resolveReceiver graph dollarVcs)

[<Fact>]
let ``resolveReceiver: an unregistered $name returns None, not a crash`` () =
    let graph = graphWithSysobjProps [] [ "vcs", 127L ]
    let dollarUnknown = Prop(ObjLit 0L, StrLit "nope", 1, 1)
    Assert.True((resolveReceiver graph dollarUnknown).IsNone)

[<Fact>]
let ``resolveReceiver: a non-literal receiver (this, player, computed) is not statically resolvable`` () =
    let graph = graphOf []
    Assert.True((resolveReceiver graph (Ident("this", 1, 1))).IsNone)
    Assert.True((resolveReceiver graph (Prop(Ident("this", 1, 1), StrLit "vcs", 1, 1))).IsNone)

// --- resolveReceiverInContext -------------------------------------------

[<Fact>]
let ``resolveReceiverInContext: a bare this resolves to the enclosing object`` () =
    let graph = graphOf []
    Assert.Equal(Some 24L, resolveReceiverInContext graph 24L (Ident("this", 1, 1)))

[<Fact>]
let ``resolveReceiverInContext: a literal object reference still resolves directly, ignoring the enclosing object`` () =
    let graph = graphOf []
    Assert.Equal(Some 123L, resolveReceiverInContext graph 24L (ObjLit 123L))

[<Fact>]
let ``resolveReceiverInContext: player and computed receivers remain unresolvable`` () =
    let graph = graphOf []
    Assert.True((resolveReceiverInContext graph 24L (Ident("player", 1, 1))).IsNone)
    Assert.True((resolveReceiverInContext graph 24L (Prop(Ident("this", 1, 1), StrLit "vcs", 1, 1))).IsNone)

[<Fact>]
let ``resolveReceiverInContext: this on an object without the verb still finds a parent's definition via findCallableVerb`` () =
    let parent = objNode 1L [] [ verbNode (verbMeta 1 "expire_mail_lists" "rxd") ]
    let child = objNode 24L [ 1L ] [ verbNode (verbMeta 1 "expire_mail" "rxd") ]
    let graph = graphOf [ parent; child ]

    match resolveReceiverInContext graph 24L (Ident("this", 1, 1)) with
    | Some startObj ->
        match findCallableVerb graph startObj "expire_mail_lists" with
        | Some(definer, _) -> Assert.Equal(1L, definer)
        | None -> Assert.Fail "expected findCallableVerb to walk up to the parent"
    | None -> Assert.Fail "expected this to resolve to the enclosing object"

// --- findAllDefiningObjects -------------------------------------------------

[<Fact>]
let ``findAllDefiningObjects finds every object defining a matching verb, ambiguity and all`` () =
    let a = objNode 1L [] [ verbNode (verbMeta 1 "tell" "rxd") ]
    let b = objNode 2L [] [ verbNode (verbMeta 1 "tell" "rxd") ]
    let c = objNode 3L [] [ verbNode (verbMeta 1 "other" "rxd") ]
    let graph = graphOf [ a; b; c ]

    let found = findAllDefiningObjects graph "tell" |> List.map fst |> List.sort
    Assert.Equal<ObjRef list>([ 1L; 2L ], found)

[<Fact>]
let ``findAllDefiningObjects excludes non-executable verbs and unrelated names`` () =
    let a = objNode 1L [] [ verbNode (verbMeta 1 "tell" "rd") ] // no 'x'
    let graph = graphOf [ a ]
    Assert.Empty(findAllDefiningObjects graph "tell")
    Assert.Empty(findAllDefiningObjects graph "nonexistent")

// --- allCallableVerbNames --------------------------------------------------

[<Fact>]
let ``allCallableVerbNames collects the starting object's own verbs`` () =
    let graph = graphOf [ objNode 1L [] [ verbNode (verbMeta 1 "foo" "rxd") ] ]
    Assert.Equal<string list>([ "foo" ], allCallableVerbNames graph 1L)

[<Fact>]
let ``allCallableVerbNames collects verbs from the whole ancestor chain`` () =
    let root = objNode 1L [] [ verbNode (verbMeta 1 "foo" "rxd") ]
    let child = objNode 2L [ 1L ] [ verbNode (verbMeta 1 "bar" "rxd") ]
    let grandchild = objNode 3L [ 2L ] []
    let graph = graphOf [ root; child; grandchild ]
    Assert.Equal<string list>([ "bar"; "foo" ], allCallableVerbNames graph 3L)

[<Fact>]
let ``allCallableVerbNames excludes non-executable verbs`` () =
    let graph = graphOf [ objNode 1L [] [ verbNode (verbMeta 1 "foo" "rd") ] ] // no 'x'
    Assert.Empty(allCallableVerbNames graph 1L)

[<Fact>]
let ``allCallableVerbNames de-duplicates a diamond-shared ancestor's verbs`` () =
    let a = objNode 1L [] [ verbNode (verbMeta 1 "shared" "rxd") ]
    let b = objNode 2L [ 1L ] []
    let c = objNode 3L [ 1L ] []
    let d = objNode 4L [ 2L; 3L ] []
    let graph = graphOf [ a; b; c; d ]
    Assert.Equal<string list>([ "shared" ], allCallableVerbNames graph 4L)

[<Fact>]
let ``allCallableVerbNames on an object with no verbs and no parents is empty, not an error`` () =
    let graph = graphOf [ objNode 1L [] [] ]
    Assert.Empty(allCallableVerbNames graph 1L)
