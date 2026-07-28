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
    let objectsRoot = Path.Combine(surviveRoot, "objects")

    if Directory.Exists(objectsRoot) then
        Directory.EnumerateFiles(objectsRoot, "*.moo", SearchOption.AllDirectories)
        // Only actual verb bodies - `object.moo`/`corponyms.moo` are
        // FORMAT.md's own manifest grammar, not MOOcode source, and aren't
        // meant to run through the lexer at all.
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
let ``every captured verb lexes without error`` (path: string) =
    let source = codeBody path
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
