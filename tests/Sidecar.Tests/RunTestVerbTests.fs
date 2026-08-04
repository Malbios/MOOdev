module Sidecar.Tests.RunTestVerbTests

open Xunit
open Language.Ast
open Sidecar.IdeActions

/// Same reasoning as `CheckVerbSyntaxTests.fs`'s own `assertParsesCleanly` -
/// a concatenation-spacing regression in `buildRunTestVerbStatements` would
/// otherwise only surface live, as an indefinite hang (the whole eval runs
/// as one MOO statement sequence, so a malformed fragment never reaches the
/// tag/notify epilogue that would resolve the waiting response).
let private assertParsesCleanly (statements: string) =
    let lexResult = Language.Lexer.tokenize statements
    Assert.True(lexResult.Error.IsNone, sprintf "lex error: %A" lexResult.Error)
    let stmts = Language.Parser.parse lexResult.Tokens
    Assert.Equal(0, countErrors stmts)

[<Fact>]
let ``buildRunTestVerbStatements produces statements that lex and parse cleanly`` () =
    assertParsesCleanly (buildRunTestVerbStatements 4L "test_addition")

[<Fact>]
let ``buildRunTestVerbStatements uses computed-dispatch syntax with the verb name literal`` () =
    let statements = buildRunTestVerbStatements 4L "test_addition"
    Assert.Contains("""#4:("test_addition")()""", statements)

[<Fact>]
let ``buildRunTestVerbStatements escapes quotes and backslashes in the verb name`` () =
    assertParsesCleanly (buildRunTestVerbStatements 4L """weird"name\here""")
