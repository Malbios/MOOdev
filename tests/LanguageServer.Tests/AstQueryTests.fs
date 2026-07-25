module LanguageServer.Tests.AstQueryTests

open Xunit
open Language.Ast
open LanguageServer.AstQuery

[<Fact>]
let ``referenceAt finds a VerbCall by its verb-name token position`` () =
    // $foo:bar(1) parses with the VerbCall's line/col pointing at "bar".
    // Line 1, "bar" starts at col 6 ('$'=1,'f'=2,'o'=3,'o'=4,':'=5,'b'=6).
    let call = VerbCall(Prop(ObjLit 0L, StrLit "foo", 1, 2), StrLit "bar", [ Normal(IntLit 1L) ], 1, 6)
    let stmts = [ ExprStmt call ]

    match referenceAt 1 6 stmts with
    | Some { Ref = RefVerbCall(_, StrLit "bar", _) } -> ()
    | other -> Assert.Fail(sprintf "expected RefVerbCall \"bar\", got %A" other)

    match referenceAt 1 8 stmts with
    | Some { Ref = RefVerbCall(_, StrLit "bar", _) } -> () // still inside "bar" (b-a-r = cols 6,7,8)
    | other -> Assert.Fail(sprintf "expected RefVerbCall \"bar\" at col 8, got %A" other)

[<Fact>]
let ``referenceAt returns None just past the end of a token's span`` () =
    let call = VerbCall(ObjLit 0L, StrLit "bar", [], 1, 6) // "bar" spans cols 6-8
    let stmts = [ ExprStmt call ]

    Assert.True((referenceAt 1 9 stmts).IsNone)
    Assert.True((referenceAt 1 5 stmts).IsNone)

[<Fact>]
let ``referenceAt finds the innermost/most specific reference among nested ones`` () =
    // $foo:bar(1) - both the Prop ($foo, at col 2) and the VerbCall (bar,
    // at col 6) are reference nodes on the same line; querying at col 6
    // must hit the VerbCall, not the Prop, since their spans don't overlap.
    let receiver = Prop(ObjLit 0L, StrLit "foo", 1, 2)
    let call = VerbCall(receiver, StrLit "bar", [], 1, 6)
    let stmts = [ ExprStmt call ]

    match referenceAt 1 2 stmts with
    | Some { Ref = RefProp(_, StrLit "foo") } -> ()
    | other -> Assert.Fail(sprintf "expected RefProp \"foo\" at col 2, got %A" other)

    match referenceAt 1 6 stmts with
    | Some { Ref = RefVerbCall(_, StrLit "bar", _) } -> ()
    | other -> Assert.Fail(sprintf "expected RefVerbCall \"bar\" at col 6, got %A" other)

[<Fact>]
let ``referenceAt walks into nested statement bodies`` () =
    let call = ExprStmt(Call("foo", [], 3, 5))
    let stmts = [ If([ (IntLit 1L, [ call ]) ], None) ]

    match referenceAt 3 5 stmts with
    | Some { Ref = RefCall("foo", _) } -> ()
    | other -> Assert.Fail(sprintf "expected RefCall \"foo\", got %A" other)

[<Fact>]
let ``collectReferences finds an Ident reference`` () =
    let stmts = [ ExprStmt(Ident("x", 2, 4)) ]
    let refs = collectReferences stmts
    Assert.Single(refs) |> ignore
    Assert.Equal(RefIdent "x", (List.head refs).Ref)

// --- firstBindingSite / allBoundNames / boundVariableNames ---------------

[<Fact>]
let ``firstBindingSite finds a plain assignment's Ident position`` () =
    let stmts = [ ExprStmt(Assign(Ident("x", 3, 5), IntLit 1L)) ]
    Assert.Equal(Some(3, 5), firstBindingSite stmts "x")

[<Fact>]
let ``firstBindingSite finds a for-loop variable's position`` () =
    let stmts = [ ForList({ Name = "x"; Line = 5; Col = 5 }, None, Ident("things", 5, 12), []) ]
    Assert.Equal(Some(5, 5), firstBindingSite stmts "x")

[<Fact>]
let ``firstBindingSite finds a for-loop's second (index) variable's position too`` () =
    let stmts =
        [ ForList({ Name = "v"; Line = 1; Col = 5 }, Some { Name = "k"; Line = 1; Col = 8 }, Ident("m", 1, 13), []) ]

    Assert.Equal(Some(1, 8), firstBindingSite stmts "k")

[<Fact>]
let ``firstBindingSite finds a for-range variable's position`` () =
    let stmts = [ ForRange({ Name = "i"; Line = 2; Col = 5 }, IntLit 1L, IntLit 10L, []) ]
    Assert.Equal(Some(2, 5), firstBindingSite stmts "i")

[<Fact>]
let ``firstBindingSite finds a scatter target's position (Required, Optional, and Rest)`` () =
    let items =
        [ Required { Name = "a"; Line = 1; Col = 2 }
          Optional({ Name = "b"; Line = 1; Col = 5 }, None)
          Rest { Name = "c"; Line = 1; Col = 9 } ]

    let stmts = [ ExprStmt(Scatter(items, Ident("args", 1, 15))) ]
    Assert.Equal(Some(1, 2), firstBindingSite stmts "a")
    Assert.Equal(Some(1, 5), firstBindingSite stmts "b")
    Assert.Equal(Some(1, 9), firstBindingSite stmts "c")

[<Fact>]
let ``firstBindingSite finds a fork's bound task-id variable's position`` () =
    // `fork task (delay) ... endfork` - the only real MOO form
    // (`parser.y:224-233`); an earlier version of this parser also invented
    // a second `task = fork (delay) ... endfork` form, which `Ast.Fork` no
    // longer has a slot for at all (confirmed against `parser.y` that
    // `fork` can never appear on an assignment's right-hand side).
    let stmts = [ Fork(Some { Name = "task"; Line = 4; Col = 6 }, IntLit 0L, []) ]
    Assert.Equal(Some(4, 6), firstBindingSite stmts "task")

[<Fact>]
let ``firstBindingSite finds an except arm's bound error-name position`` () =
    let arm =
        { Name = Some { Name = "err"; Line = 3; Col = 9 }
          Codes = AnyCode
          Body = [] }

    let stmts = [ TryExcept([], [ arm ]) ]
    Assert.Equal(Some(3, 9), firstBindingSite stmts "err")

[<Fact>]
let ``firstBindingSite returns the earliest position when a name is bound more than once, not traversal order`` () =
    // Reassigned twice - the second assignment (line 1) is visited first in
    // a naive top-to-bottom walk of this hand-built list, but line 1 is
    // NOT earlier than... reversed here on purpose: put the later position
    // first in the list to prove sorting, not list order, decides "first."
    let stmts =
        [ ExprStmt(Assign(Ident("x", 10, 1), IntLit 2L))
          ExprStmt(Assign(Ident("x", 2, 1), IntLit 1L)) ]

    Assert.Equal(Some(2, 1), firstBindingSite stmts "x")

[<Fact>]
let ``firstBindingSite returns None for a name that's never bound`` () =
    let stmts = [ ExprStmt(Ident("x", 1, 1)) ] // a read, not a binding
    Assert.True((firstBindingSite stmts "x").IsNone)

[<Fact>]
let ``boundVariableNames still returns a plain distinct sorted name list`` () =
    let stmts =
        [ ExprStmt(Assign(Ident("b", 1, 1), IntLit 1L))
          ExprStmt(Assign(Ident("a", 2, 1), IntLit 2L))
          ExprStmt(Assign(Ident("b", 3, 1), IntLit 3L)) ] // reassigned - must not duplicate

    Assert.Equal<string list>([ "a"; "b" ], boundVariableNames stmts)
