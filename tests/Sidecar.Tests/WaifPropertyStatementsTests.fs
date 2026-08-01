module Sidecar.Tests.WaifPropertyStatementsTests

open Xunit
open Language.Ast
open Sidecar.IdeActions

/// Same regression guard as `CheckVerbSyntaxTests` - parsing the generated
/// statements directly catches a concatenation-spacing regression without
/// needing a live MOO connection.
let private assertParsesCleanly (statements: string) =
    let lexResult = Language.Lexer.tokenize statements
    Assert.True(lexResult.Error.IsNone, sprintf "lex error: %A" lexResult.Error)
    let stmts = Language.Parser.parse lexResult.Tokens
    Assert.Equal(0, countErrors stmts)

[<Fact>]
let ``buildGetWaifPropertiesStatements produces statements that lex and parse cleanly`` () =
    assertParsesCleanly (buildGetWaifPropertiesStatements 1L "waif_prop")

[<Fact>]
let ``buildGetWaifPropertiesStatements escapes quotes and backslashes in the property name`` () =
    assertParsesCleanly (buildGetWaifPropertiesStatements 1L """say "hi" \ prop""")

[<Fact>]
let ``buildSetWaifPropertyStatements produces statements that lex and parse cleanly`` () =
    assertParsesCleanly (buildSetWaifPropertyStatements 1L "waif_prop" "count" "42")

[<Fact>]
let ``buildSetWaifPropertyStatements escapes quotes and backslashes in the property and waif-property names`` () =
    assertParsesCleanly (
        buildSetWaifPropertyStatements 1L """say "hi" \ prop""" """another "quoted" \ name""" "\"literal value\""
    )

[<Fact>]
let ``buildGetWaifPropertiesStatements strips the leading colon before indexing into the waif`` () =
    let statements = buildGetWaifPropertiesStatements 1L "moodev_waif_test"
    Assert.Contains("shortname = n[2..length(n)]", statements)
    Assert.Contains("w.(shortname)", statements)
    Assert.DoesNotContain("w.(\":\" + shortname)", statements)

[<Fact>]
let ``buildSetWaifPropertyStatements reassigns the outer property after mutating the waif`` () =
    let statements = buildSetWaifPropertyStatements 1L "moodev_waif_test" "count" "42"
    Assert.Contains("#1.(\"moodev_waif_test\") = w;", statements)
