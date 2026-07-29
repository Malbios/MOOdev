module Sidecar.Tests.TreeParserTests

open System.IO
open Xunit
open Sidecar.Exporter
open Sidecar.TreeParser

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-test-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

[<Fact>]
let ``parseCorponyms round-trips renderCorponymsMoo`` () =
    let dir = tempDir ()

    try
        let original = [ "room", 3L; "string_utils", 4L; "anon", 118L ]
        File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo original)

        let parsed = parseCorponyms (Path.Combine(dir, "corponyms.moo"))

        Assert.Equal<(string * int64) list>(
            original |> List.sortBy fst,
            parsed |> List.sortBy fst
        )
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseVerbFile round-trips renderVerbFile, including multi-alias headers`` () =
    let dir = tempDir ()

    try
        let original: VerbExport =
            { Names = "l*ook get take"
              Owner = 3L
              Perms = "rxd"
              Dobj = "this"
              Prep = "none"
              Iobj = "any"
              Code = [ "\"a comment-like string;\";"; "player:tell(\"hi\");" ] }

        let path = Path.Combine(dir, "look.moo")
        File.WriteAllText(path, renderVerbFile "$room" original)

        let parsed = parseVerbFile path

        Assert.Equal(original, parsed)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseVerbFile handles a verb with empty code (never programmed)`` () =
    let dir = tempDir ()

    try
        let original: VerbExport =
            { Names = "eval"
              Owner = 3L
              Perms = "rd"
              Dobj = "any"
              Prep = "any"
              Iobj = "any"
              Code = [] }

        let path = Path.Combine(dir, "eval.moo")
        File.WriteAllText(path, renderVerbFile "$room" original)

        let parsed = parseVerbFile path

        Assert.Equal(original, parsed)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseObjectMoo round-trips renderObjectMoo - parents, flags, properties, verb order`` () =
    let dir = tempDir ()
    let objDir = Path.Combine(dir, "objects", "room")
    let verbsDir = Path.Combine(objDir, "verbs")
    Directory.CreateDirectory(verbsDir) |> ignore

    try
        let corponymsByObjnum = Map.ofList [ 4L, "string_utils" ]

        let verb1: VerbExport =
            { Names = "look_self"
              Owner = 3L
              Perms = "rxd"
              Dobj = "this"
              Prep = "none"
              Iobj = "this"
              Code = [ "player:tell(this.description);" ] }

        let verb2: VerbExport =
            { Names = "tell_lines"
              Owner = 3L
              Perms = "rxd"
              Dobj = "this"
              Prep = "none"
              Iobj = "this"
              Code = [ "return;" ] }

        let data: ObjectExport =
            { Parents = [ 4L; 1L ] // deliberately unsorted / mixed corponym+raw
              Owner = 3L
              Flags = [ "r"; "f" ]
              Properties =
                [ { Name = "zeta"; Owner = 3L; Perms = "rc"; ValueLiteral = "1" }
                  { Name = "alpha"; Owner = 3L; Perms = "rc"; ValueLiteral = "\"a string\"" } ]
              Verbs = [ verb1; verb2 ] } // declaration order: verb1 before verb2

        let verbFileNames = assignVerbFileNames data.Verbs

        File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo corponymsByObjnum "$room" data verbFileNames)

        for verb, fileName in verbFileNames do
            File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile "$room" verb)

        let parsed = parseObjectMoo (Path.Combine(objDir, "object.moo")) verbsDir

        Assert.Equal("room", parsed.SelfCorponym)
        Assert.Equal<ParentRef list>([ ByCorponym "string_utils"; ByObjnum 1L ], parsed.Parents)
        Assert.Equal(3L, parsed.Owner)
        Assert.Equal<string list>([ "r"; "f" ], parsed.Flags)

        // Properties come back sorted by name (that's the render's own
        // sort), so compare against the same sorted expectation.
        Assert.Equal<string list>([ "alpha"; "zeta" ], parsed.Properties |> List.map (fun p -> p.Name))

        // Verb declaration order preserved exactly - this is the whole
        // point of the verbs: manifest line.
        Assert.Equal<string list>([ "look_self"; "tell_lines" ], parsed.Verbs |> List.map (fun v -> v.Names))
        Assert.Equal<string list>(verb1.Code, parsed.Verbs.[0].Code)
        Assert.Equal<string list>(verb2.Code, parsed.Verbs.[1].Code)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseObjectMoo handles a property value containing embedded newlines`` () =
    let dir = tempDir ()
    let objDir = Path.Combine(dir, "objects", "room")
    let verbsDir = Path.Combine(objDir, "verbs")
    Directory.CreateDirectory(verbsDir) |> ignore

    try
        let corponymsByObjnum = Map.empty

        let data: ObjectExport =
            { Parents = []
              Owner = 3L
              Flags = []
              Properties =
                [ { Name = "multiline"
                    Owner = 3L
                    Perms = "rc"
                    ValueLiteral = "\"line one\nline two\"" } ]
              Verbs = [] }

        File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo corponymsByObjnum "$room" data [])

        let parsed = parseObjectMoo (Path.Combine(objDir, "object.moo")) verbsDir

        Assert.Equal("\"line one\nline two\"", parsed.Properties.[0].ValueLiteral)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseVerbFile round-trips a name-spec containing a literal quote character`` () =
    let dir = tempDir ()

    try
        // Real ToastCore content does this - e.g. some cores define a verb
        // alias that is itself a `"` for quoting-syntax help/commands.
        let original: VerbExport =
            { Names = "\"quote"
              Owner = 3L
              Perms = "rxd"
              Dobj = "any"
              Prep = "any"
              Iobj = "any"
              Code = [ "return;" ] }

        let path = Path.Combine(dir, "quote.moo")
        File.WriteAllText(path, renderVerbFile "$room" original)

        let parsed = parseVerbFile path

        Assert.Equal(original, parsed)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseObjectMoo round-trips a property named a literal quote character`` () =
    let dir = tempDir ()
    let objDir = Path.Combine(dir, "objects", "help")
    let verbsDir = Path.Combine(objDir, "verbs")
    Directory.CreateDirectory(verbsDir) |> ignore

    try
        // Real ToastCore's `$help` object has a property literally named `"`
        // (the help topic for quoting syntax) - confirmed live against a real
        // ToastCore export, which is what surfaced this bug.
        let data: ObjectExport =
            { Parents = []
              Owner = 36L
              Flags = []
              Properties = [ { Name = "\""; Owner = 36L; Perms = "r"; ValueLiteral = "\"how to quote things\"" } ]
              Verbs = [] }

        File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo Map.empty "$help" data [])

        let parsed = parseObjectMoo (Path.Combine(objDir, "object.moo")) verbsDir

        Assert.Equal<string list>([ "\"" ], parsed.Properties |> List.map (fun p -> p.Name))
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``parseObjectMoo reads the raw "object #0" header form (FORMAT.md's system-object exception) as SelfCorponym "0"`` () =
    let dir = tempDir ()
    let objDir = Path.Combine(dir, "objects", "0")
    let verbsDir = Path.Combine(objDir, "verbs")
    Directory.CreateDirectory(verbsDir) |> ignore

    try
        let data: ObjectExport =
            { Parents = []
              Owner = 0L
              Flags = [ "wizard"; "programmer" ]
              Properties = []
              Verbs = [] }

        File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo Map.empty "#0" data [])

        let parsed = parseObjectMoo (Path.Combine(objDir, "object.moo")) verbsDir

        Assert.Equal("0", parsed.SelfCorponym)
        Assert.Equal<string list>([ "wizard"; "programmer" ], parsed.Flags)
    finally
        Directory.Delete(dir, true)
