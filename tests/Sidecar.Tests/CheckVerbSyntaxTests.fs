module Sidecar.Tests.CheckVerbSyntaxTests

open Xunit
open Language.Ast
open Sidecar.IdeActions

/// Regression guard for the exact bug this shape broke on once already:
/// two `resolveVerbIndexStatements` calls concatenated directly against a
/// fragment with no trailing space produced the single malformed token
/// `endifvlist`, which fails to lex/parse cleanly - and since the *whole*
/// eval (this plus its own tag/notify epilogue) runs as one MOO statement
/// sequence, that failure meant no response ever came back at all, not a
/// visible compile error - live-verification found it as an indefinite
/// hang. Parsing the generated statements directly (no live MOO
/// connection needed) catches a concatenation-spacing regression here
/// without needing the live-only scratch-verb round trip itself.
let private assertParsesCleanly (statements: string) =
    let lexResult = Language.Lexer.tokenize statements
    Assert.True(lexResult.Error.IsNone, sprintf "lex error: %A" lexResult.Error)
    let stmts = Language.Parser.parse lexResult.Tokens
    Assert.Equal(0, countErrors stmts)

[<Fact>]
let ``buildCheckVerbSyntaxStatements produces statements that lex and parse cleanly`` () =
    assertParsesCleanly (buildCheckVerbSyntaxStatements [ "return 1;" ])

[<Fact>]
let ``buildCheckVerbSyntaxStatements handles multi-line candidate code`` () =
    assertParsesCleanly (buildCheckVerbSyntaxStatements [ "x = 1;"; "return x;" ])

[<Fact>]
let ``buildCheckVerbSyntaxStatements handles empty candidate code`` () =
    assertParsesCleanly (buildCheckVerbSyntaxStatements [])

[<Fact>]
let ``buildCheckVerbSyntaxStatements escapes quotes and backslashes in candidate code`` () =
    assertParsesCleanly (buildCheckVerbSyntaxStatements [ """notify(player, "say \"hi\"");""" ])
