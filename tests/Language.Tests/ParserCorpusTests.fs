/// The parser's real proof point per the M4 plan: "produces a complete AST
/// with an empty diagnostics list" over every verb `$vcs` has captured into
/// `Survive`.
module Language.Tests.ParserCorpusTests

open System.IO
open Xunit
open Language.Lexer
open Language.Ast
open Language.Parser

// Duplicated from CorpusTests.fs rather than shared via cross-module
// MemberData (which needs a real type for xUnit's MemberType, and an F#
// module isn't directly nameable as one) - both are ~5 lines, not worth
// the reflection complexity.
let private surviveRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Survive"))

let allMooFiles: obj[] seq =
    if Directory.Exists(surviveRoot) then
        Directory.EnumerateFiles(surviveRoot, "*.moo", SearchOption.AllDirectories)
        |> Seq.map (fun p -> [| box p |])
    else
        Seq.empty

[<Theory>]
[<MemberData(nameof allMooFiles)>]
let ``every captured verb parses to a complete, error-free AST`` (path: string) =
    let source = File.ReadAllText(path)
    let lexResult = tokenize source

    match lexResult.Error with
    | Some err ->
        Assert.Fail(
            sprintf "%s: lex error at line %d col %d: %s" (Path.GetFileName path) err.Line err.Col err.Message
        )
    | None ->
        let stmts = parse lexResult.Tokens
        let errorCount = countErrors stmts

        Assert.True(
            (errorCount = 0),
            sprintf "%s: %d statement(s) failed to parse" (Path.GetFileName path) errorCount
        )
