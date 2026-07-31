/// Hand-built AST fixtures for `Handlers.inferredVerbSummary` - same reasoning
/// as `GotchaFinderTests.fs`: precise control over each verb's AST rather
/// than depending on a larger synthetic corpus.
module LanguageServer.Tests.VerbSummaryTests

open Xunit
open Language.Ast
open LanguageServer.Handlers

let private bound (name: string) : BoundName = { Name = name; Line = 1; Col = 1 }

let private argsIdent = Ident("args", 1, 1)

// --- parameters -----------------------------------------------------------

[<Fact>]
let ``scatter-assignment from args infers required, optional-with-default, and rest params`` () =
    let stmts =
        [ ExprStmt(
              Scatter(
                  [ Required(bound "who"); Optional(bound "what", Some(StrLit "")); Rest(bound "rest") ],
                  argsIdent
              )
          ) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text ->
        Assert.Contains("`who`", text)
        Assert.Contains("`what` (optional, default `\"\"`)", text)
        Assert.Contains("`@rest` (rest)", text)

[<Fact>]
let ``args indexing assignment is the fallback param idiom, sorted by index`` () =
    let stmts =
        [ ExprStmt(Assign(Ident("second", 1, 1), Index(argsIdent, IntLit 2L)))
          ExprStmt(Assign(Ident("first", 1, 1), Index(argsIdent, IntLit 1L))) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text -> Assert.Contains("Parameters: `first` (args[1]), `second` (args[2])", text)

[<Fact>]
let ``a verb with no arg-unpacking idiom has no parameters line`` () =
    let stmts = [ ExprStmt(Call("suspend", [ Normal(IntLit 0L) ], 1, 1)) ]

    match inferredVerbSummary stmts with
    | None -> ()
    | Some text -> Assert.DoesNotContain("Parameters:", text)

// --- dependencies -----------------------------------------------------------

[<Fact>]
let ``property, verb-call, and builtin references are all listed`` () =
    let stmts =
        [ ExprStmt(Prop(Ident("this", 1, 1), StrLit "description", 1, 1))
          ExprStmt(VerbCall(Ident("this", 1, 1), StrLit "announce", [], 1, 1))
          ExprStmt(Call("tostr", [ Normal(IntLit 1L) ], 1, 1)) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text ->
        Assert.Contains("Properties: `description`", text)
        Assert.Contains("Verb calls: `announce`", text)
        Assert.Contains("Builtins: `tostr`", text)

// --- suspend ----------------------------------------------------------------

[<Fact>]
let ``a direct suspend() call is reported as can-suspend`` () =
    let stmts = [ ExprStmt(Call("suspend", [ Normal(IntLit 0L) ], 1, 1)) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text -> Assert.Contains("Can suspend", text)

[<Fact>]
let ``a fork with no suspend() inside is still reported as can-suspend`` () =
    let stmts = [ Fork(None, IntLit 0L, [ ExprStmt(Call("notify", [], 1, 1)) ]) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text -> Assert.Contains("Can suspend", text)

[<Fact>]
let ``a verb with neither suspend() nor fork is not reported as can-suspend`` () =
    let stmts = [ ExprStmt(Call("tostr", [ Normal(IntLit 1L) ], 1, 1)) ]

    match inferredVerbSummary stmts with
    | None -> ()
    | Some text -> Assert.DoesNotContain("Can suspend", text)

// --- returns ------------------------------------------------------------

[<Fact>]
let ``an explicit return with a value is reported as may-return-a-value`` () =
    let stmts = [ Return(Some(IntLit 1L)) ]

    match inferredVerbSummary stmts with
    | None -> Assert.True(false, "expected a summary")
    | Some text -> Assert.Contains("May return a value", text)

[<Fact>]
let ``a bare return with no value is not reported as may-return-a-value`` () =
    let stmts = [ Return None ]

    match inferredVerbSummary stmts with
    | None -> ()
    | Some text -> Assert.DoesNotContain("May return a value", text)

[<Fact>]
let ``a verb with none of the four facts has no summary at all`` () =
    let stmts = [ ExprStmt(Assign(Ident("x", 1, 1), IntLit 1L)) ]
    Assert.Equal(None, inferredVerbSummary stmts)

// --- leading docstring ----------------------------------------------------

[<Fact>]
let ``a bare string-literal statement first in the body is the leading docstring`` () =
    let stmts = [ ExprStmt(StrLit "Does a thing."); Return(Some(IntLit 1L)) ]
    Assert.Equal(Some "Does a thing.", leadingDocString stmts)

[<Fact>]
let ``a bare string literal after real code is not the leading docstring`` () =
    let stmts = [ ExprStmt(Assign(Ident("x", 1, 1), IntLit 1L)); ExprStmt(StrLit "too late") ]
    Assert.Equal(None, leadingDocString stmts)

[<Fact>]
let ``an empty leading string literal is not treated as a docstring`` () =
    let stmts = [ ExprStmt(StrLit ""); Return(Some(IntLit 1L)) ]
    Assert.Equal(None, leadingDocString stmts)

[<Fact>]
let ``a verb with no leading string literal has no docstring`` () =
    let stmts = [ Return(Some(IntLit 1L)) ]
    Assert.Equal(None, leadingDocString stmts)
