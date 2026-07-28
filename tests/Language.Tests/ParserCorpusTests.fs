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
    let objectsRoot = Path.Combine(surviveRoot, "objects")

    if Directory.Exists(objectsRoot) then
        Directory.EnumerateFiles(objectsRoot, "*.moo", SearchOption.AllDirectories)
        // Only actual verb bodies - `object.moo`/`corponyms.moo` are
        // FORMAT.md's own manifest grammar (parents/owner/flags/properties),
        // not MOOcode statements, and have their own round-trip coverage in
        // Sidecar.Tests/TreeParserTests.fs.
        |> Seq.filter (fun p -> Path.GetFileName(Path.GetDirectoryName(p)) = "verbs")
        |> Seq.map (fun p -> [| box p |])
    else
        Seq.empty

/// A verb file is FORMAT.md's own `@verb .../@program ...` wrapper around
/// the real code, terminated by a bare `.` line (`Sidecar.TreeParser.
/// parseVerbFileLines`'s exact same convention) - only the code between
/// those, not the wrapper itself, is actual MOOcode. Duplicated rather than
/// referencing `Sidecar.TreeParser` directly, since this test project has
/// no other reason to depend on the Sidecar project.
let private codeBody (path: string) : string =
    File.ReadAllLines(path).[2..] |> Array.takeWhile (fun l -> l <> ".") |> String.concat "\n"

[<Theory>]
[<MemberData(nameof allMooFiles)>]
let ``every captured verb parses to a complete, error-free AST`` (path: string) =
    let source = codeBody path
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
