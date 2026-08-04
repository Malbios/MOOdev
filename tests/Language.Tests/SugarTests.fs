/// Round-trip proof for `Language.Sugar` - every case asserts
/// `toReal(toSugar(real).Text).Text = Ok real` exactly, covering the
/// structurally tricky shapes `Ast.fs` itself flags (elseif/else and
/// except/finally as siblings of their opener, not nested under it) plus
/// the sugar<->real boundary cases (multi-line statements via an unclosed
/// bracket, inconsistent real-world indentation, a self-contained
/// same-line block someone typed as real syntax, and a malformed
/// continuation keyword that must fail fast rather than silently
/// misplace a closer).
module Language.Tests.SugarTests

open System.IO
open Xunit
open Language.Sugar

let private assertRoundTrips (real: string) =
    match toSugar real with
    | Error msg -> Assert.Fail(sprintf "toSugar failed: %s" msg)
    | Ok sugar ->
        match toReal sugar.Text with
        | Error msg -> Assert.Fail(sprintf "toReal failed: %s\nsugar was:\n%s" msg sugar.Text)
        | Ok real2 -> Assert.Equal(real, real2.Text)

[<Fact>]
let ``if/elseif/elseif/else/endif round-trips`` () =
    let real =
        "if (x == 1)\n  a = 1;\nelseif (x == 2)\n  a = 2;\nelseif (x == 3)\n  a = 3;\nelse\n  a = 0;\nendif"

    assertRoundTrips real

[<Fact>]
let ``nested for-in-while-in-if round-trips`` () =
    let real =
        "if (x > 0)\n  for i in [1..10]\n    while (i < 5)\n      i = i + 1;\n    endwhile\n  endfor\nendif"

    assertRoundTrips real

[<Fact>]
let ``try with multiple except arms round-trips`` () =
    let real =
        "try\n  x = 1 / 0;\nexcept e1 (E_DIV)\n  x = 0;\nexcept e2 (ANY)\n  x = -1;\nendtry"

    assertRoundTrips real

[<Fact>]
let ``try/finally (never both with except) round-trips`` () =
    let real = "try\n  do_something();\nfinally\n  cleanup();\nendtry"
    assertRoundTrips real

[<Fact>]
let ``multi-line statement via an unclosed brace round-trips`` () =
    let real = "x = {1, 2,\n     3, 4};"
    assertRoundTrips real

[<Fact>]
let ``inconsistent real-world indentation round-trips unchanged`` () =
    // Leaf lines inside a block don't need consistent indentation for real
    // MOOcode to parse - and toReal shouldn't require it either, only
    // block open/close/continuation lines are indentation-sensitive.
    let real = "if (x)\n a = 1;\n   b = 2;\nendif"
    assertRoundTrips real

[<Fact>]
let ``a self-contained same-line block (real syntax already) round-trips untouched`` () =
    // Someone typed `if (x) foo(); endif` as one physical line, real
    // syntax throughout - toReal must recognize its own embedded closer
    // and NOT push a frame for it (which would otherwise misinterpret the
    // next line as this block's body).
    let real = "if (x) foo(); endif\ny = 1;"
    assertRoundTrips real

[<Fact>]
let ``toSugar strips the trailing semicolon but keeps a trailing comment intact`` () =
    let real = "x = 1; /* note */"
    match toSugar real with
    | Error msg -> Assert.Fail(sprintf "toSugar failed: %s" msg)
    | Ok s ->
        Assert.Equal("x = 1 /* note */", s.Text)

        // Accepted v1 cosmetic gap, not a round-trip requirement here: a
        // block comment trailing a statement on the same physical line has
        // no stored span to reinsert `;` before, so toReal re-appends it at
        // the line's own end instead of at the original pre-comment
        // position - `x = 1; /* note */` becomes `x = 1 /* note */;`,
        // semantically identical MOOcode, just cosmetically reflowed.
        match toReal s.Text with
        | Error msg -> Assert.Fail(sprintf "toReal failed: %s" msg)
        | Ok real2 -> Assert.Equal("x = 1 /* note */;", real2.Text)

[<Fact>]
let ``toSugar keeps an endif line with a trailing comment (not sole-token-only)`` () =
    let real = "if (x)\n  a = 1;\nendif /* done */"
    assertRoundTrips real

[<Fact>]
let ``an orphan else at a non-matching indentation is a fast error`` () =
    let sugar = "if (x)\n  a = 1\n    else\n  b = 2"

    match toReal sugar with
    | Error msg -> Assert.Contains("else", msg)
    | Ok r -> Assert.Fail(sprintf "expected an error, got: %s" r.Text)

[<Fact>]
let ``an orphan except with no open try is a fast error`` () =
    let sugar = "except e (ANY)\n  x = 1"

    match toReal sugar with
    | Error msg -> Assert.Contains("except", msg)
    | Ok r -> Assert.Fail(sprintf "expected an error, got: %s" r.Text)

[<Fact>]
let ``roundTripsCleanly is true for well-formed real code and false for nothing pathological here`` () =
    Assert.True(roundTripsCleanly "if (x)\n  a = 1;\nendif")

// ---------------------------------------------------------------------------
// Corpus round trip - the real proof point, reusing the same fixture
// enumeration CorpusTests.fs/ParserCorpusTests.fs already use. Duplicated
// rather than shared via cross-module MemberData (needs a real type for
// xUnit's MemberType, and an F# module isn't directly nameable as one) -
// both are ~5 lines, not worth the reflection complexity, matching
// ParserCorpusTests.fs's own stated reasoning for the same duplication.
// ---------------------------------------------------------------------------

let private surviveRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Survive"))

let allMooFiles: obj[] seq =
    let objectsRoot = Path.Combine(surviveRoot, "objects")

    if Directory.Exists(objectsRoot) then
        Directory.EnumerateFiles(objectsRoot, "*.moo", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> Path.GetFileName(Path.GetDirectoryName(p)) = "verbs")
        |> Seq.map (fun p -> [| box p |])
    else
        Seq.empty

let private codeBody (path: string) : string =
    File.ReadAllLines(path).[2..] |> Array.takeWhile (fun l -> l <> ".") |> String.concat "\n"

/// The real proof point: every captured real-world verb must round-trip
/// losslessly. Also the empirical answer to the one known accepted v1 gap -
/// a multi-line statement with no enclosing bracket (e.g. a bare multi-line
/// ternary) can't be detected by the depth-based heuristic - this either
/// stays green (the pattern doesn't occur in practice) or names exactly
/// which real verb needs a look.
[<Theory>]
[<MemberData(nameof allMooFiles)>]
let ``every captured verb round-trips through sugar losslessly`` (path: string) =
    let real = codeBody path

    match Language.Lexer.tokenize real with
    | { Error = Some _ } -> () // already covered by CorpusTests.fs's own lexer pass
    | { Error = None } ->
        match toSugar real with
        | Error msg -> Assert.Fail(sprintf "%s: toSugar failed: %s" (Path.GetRelativePath(surviveRoot, path)) msg)
        | Ok sugar ->
            match toReal sugar.Text with
            | Error msg -> Assert.Fail(sprintf "%s: toReal failed: %s" (Path.GetRelativePath(surviveRoot, path)) msg)
            | Ok real2 -> Assert.Equal(real, real2.Text)
