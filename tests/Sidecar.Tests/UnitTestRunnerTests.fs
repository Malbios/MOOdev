module Sidecar.Tests.UnitTestRunnerTests

open Xunit
open Language.Ast
open Sidecar.UnitTestRunner

/// Same reasoning as `CheckVerbSyntaxTests.fs`'s own `assertParsesCleanly` -
/// a concatenation-spacing regression in either statement builder would
/// otherwise only surface live, as an indefinite hang or a silent
/// installed-verb compile failure (the whole eval runs as one MOO
/// statement sequence, so a malformed fragment never reaches the
/// tag/notify epilogue that would resolve the waiting response).
let private assertParsesCleanly (statements: string) =
    let lexResult = Language.Lexer.tokenize statements
    Assert.True(lexResult.Error.IsNone, sprintf "lex error: %A" lexResult.Error)
    let stmts = Language.Parser.parse lexResult.Tokens
    Assert.Equal(0, countErrors stmts)

[<Fact>]
let ``buildFetchLiveCodeStatements produces statements that lex and parse cleanly`` () =
    let statements, resultExpr = buildFetchLiveCodeStatements 4L "test_addition"
    assertParsesCleanly (statements + " return " + resultExpr + ";")

[<Fact>]
let ``buildFetchLiveCodeStatements resolves the under-test verb name by stripping the prefix`` () =
    let statements, _ = buildFetchLiveCodeStatements 4L "test_addition"
    Assert.Contains("\"addition\"", statements)

[<Fact>]
let ``buildInstallAndRunStatements produces statements that lex and parse cleanly, with no under-test verb`` () =
    assertParsesCleanly (buildInstallAndRunStatements "test_ok" [ "return 1;" ] None)

[<Fact>]
let ``buildInstallAndRunStatements produces statements that lex and parse cleanly, with an under-test verb`` () =
    assertParsesCleanly (buildInstallAndRunStatements "test_addition" [ "return 1;" ] (Some("addition", [ "return x + 1;" ])))

[<Fact>]
let ``buildInstallAndRunStatements escapes quotes and backslashes in verb code`` () =
    assertParsesCleanly (buildInstallAndRunStatements "test_ok" [ """notify(player, "say \"hi\"");""" ] None)

[<Fact>]
let ``buildInstallAndRunStatements uses computed-dispatch syntax to invoke the test verb`` () =
    let statements = buildInstallAndRunStatements "test_ok" [ "return 1;" ] None
    Assert.Contains("""obj:("test_ok")()""", statements)
