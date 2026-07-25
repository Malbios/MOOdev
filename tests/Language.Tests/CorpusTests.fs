/// The corpus test named in the M4 plan: "parses every verb in ToastCore
/// without error" (applied to the lexer here, the parser gets its own pass
/// in a later phase). Runs the lexer over every .moo file captured into
/// `Survive` by $vcs and reports a pass rate rather than a single
/// pass/fail - any failure here is unambiguously a lexer bug, not a
/// grammar-ambiguity judgment call, so each one is worth seeing by name.
module Language.Tests.CorpusTests

open System.IO
open Xunit
open Language.Lexer

let private surviveRoot =
    // Repo-group root is two levels above MOOdev (…/MOOdev/tests/Language.Tests).
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Survive"))

let allMooFiles: obj[] seq =
    if Directory.Exists(surviveRoot) then
        Directory.EnumerateFiles(surviveRoot, "*.moo", SearchOption.AllDirectories)
        |> Seq.map (fun p -> [| box p |])
    else
        Seq.empty

[<Theory>]
[<MemberData(nameof allMooFiles)>]
let ``every captured verb lexes without error`` (path: string) =
    let source = File.ReadAllText(path)
    let result = tokenize source

    match result.Error with
    | None -> ()
    | Some err ->
        Assert.Fail(
            sprintf
                "%s: line %d col %d: %s"
                (Path.GetRelativePath(surviveRoot, path))
                err.Line
                err.Col
                err.Message
        )

[<Fact>]
let ``corpus is actually present and non-trivial`` () =
    let count = allMooFiles |> Seq.length
    Assert.True(count > 1000, sprintf "expected 1000+ captured verbs under Survive, found %d - is the path right?" count)
