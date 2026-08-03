module Sidecar.Tests.ExporterTests

open System.Threading
open System.Text.Json
open Xunit
open Sidecar.Exporter

// ---------------------------------------------------------------------------
// Filename derivation - Survive/VCS/1_sanitize_name.moo ported verbatim.
// ---------------------------------------------------------------------------

[<Fact>]
let ``sanitizeName strips filesystem-unsafe characters and replaces spaces`` () =
    Assert.Equal("look_self", sanitizeName "look self")
    Assert.Equal("weird_name", sanitizeName "weird*/\\:\"<>|?_name")

[<Fact>]
let ``assignVerbFileNames derives from the first alias, star stripped`` () =
    let verb names : VerbExport =
        { Names = names
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [] }

    let result = assignVerbFileNames [ verb "look_self"; verb "l*ook get take" ]

    Assert.Equal<string list>([ "look_self.moo"; "look.moo" ], result |> List.map snd)

[<Fact>]
let ``assignVerbFileNames appends a numeric suffix on collision, in declaration order`` () =
    let verb names : VerbExport =
        { Names = names
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [] }

    // Two different verbs whose first alias sanitizes to the same name.
    let result = assignVerbFileNames [ verb "tell"; verb "tell announce" ]

    Assert.Equal<string list>([ "tell.moo"; "tell_2.moo" ], result |> List.map snd)

[<Fact>]
let ``assignVerbFileNames strips control characters sanitizeName's fixed punctuation set never covered`` () =
    let verb names : VerbExport =
        { Names = names
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [] }

    // A real-world alias can contain arbitrary bytes, including a plain
    // ASCII control code (built via `char`/`int`, not a literal control
    // byte in this source file) spliced into otherwise-ordinary text -
    // this crashed File.WriteAllText live with a bare "filename ... syntax
    // is incorrect" IOException against a large, messy real-world db
    // before this hardening pass existed.
    let controlChar = char 7
    let alias = "ga" + string controlChar + "y"
    let result = assignVerbFileNames [ verb alias ]

    Assert.Equal<string list>([ "gay.moo" ], result |> List.map snd)

[<Fact>]
let ``assignVerbFileNames trims a trailing dot sanitizeName's fixed punctuation set never covered`` () =
    let verb names : VerbExport =
        { Names = names
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [] }

    // A trailing "." is a perfectly fine alias character but an invalid
    // final character for an NTFS path component.
    let result = assignVerbFileNames [ verb "greet." ]

    Assert.Equal<string list>([ "greet.moo" ], result |> List.map snd)

[<Fact>]
let ``assignVerbFileNames disambiguates a name that collides with a reserved Windows device name`` () =
    let verb names : VerbExport =
        { Names = names
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [] }

    let result = assignVerbFileNames [ verb "aux" ]

    Assert.Equal<string list>([ "aux_.moo" ], result |> List.map snd)

// ---------------------------------------------------------------------------
// getObjectExport - the hidden syntax-check scratch verb (IdeActions.fs's
// checkVerbSyntax) must never leak into the exported/git-tracked tree.
// ---------------------------------------------------------------------------

[<Fact>]
let ``getObjectExport filters out the syntax-check scratch verb but keeps real verbs`` () =
    let json =
        $$"""{
            "parents": [], "owner": "#3", "flags": [], "properties": [],
            "verbs": [
                {"names": "do_command", "owner": "#0", "perms": "rxd", "dobj": "none", "prep": "none", "iobj": "none", "code": ["return 0;"]},
                {"names": "{{syntaxCheckScratchVerbName}}", "owner": "#0", "perms": "rxd", "dobj": "this", "prep": "none", "iobj": "this", "code": ["return 1;"]}
            ],
            "name": "System Object", "aliases": []
        }"""

    let evalRunner: EvalRunner =
        fun _ _ _ -> task { return JsonDocument.Parse(json) }

    let result = (getObjectExport evalRunner 0L CancellationToken.None).Result

    match result with
    | None -> Assert.Fail("expected Some ObjectExport")
    | Some data -> Assert.Equal<string list>([ "do_command" ], data.Verbs |> List.map (fun v -> v.Names))

// ---------------------------------------------------------------------------
// Rendering - exact text, per FORMAT.md's grammar. Explicit "\n" only, never
// "\r\n" - these assertions are what catch a future accidental regression to
// Environment.NewLine (invariant I4: line-ending stability).
// ---------------------------------------------------------------------------

[<Fact>]
let ``renderCorponymsMoo sorts by name, case-insensitive, one "name #objnum" per line`` () =
    let result = renderCorponymsMoo [ "room", 3L; "Ansi_Utilities", 100L; "anon", 118L ]

    Assert.Equal("anon #118\nAnsi_Utilities #100\nroom #3\n", result)

[<Fact>]
let ``renderVerbFile emits the verb-header/program/terminator grammar with pinned owner`` () =
    let verb: VerbExport =
        { Names = "look_self"
          Owner = 2L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [ "\"Describe this room to the caller.\";"; "player:tell(this:title());" ] }

    let result = renderVerbFile "$room" verb

    let expected =
        "@verb $room:\"look_self\" this none this rxd #2\n"
        + "@program $room:look_self\n"
        + "\"Describe this room to the caller.\";\n"
        + "player:tell(this:title());\n"
        + ".\n"

    Assert.Equal(expected, result)

[<Fact>]
let ``renderVerbFile strips * from the program line but keeps it in the verb header`` () =
    let verb: VerbExport =
        { Names = "l*ook get take"
          Owner = 2L
          Perms = "rd"
          Dobj = "this"
          Prep = "none"
          Iobj = "none"
          Code = [ "return;" ] }

    let result = renderVerbFile "$room" verb

    Assert.Contains("@verb $room:\"l*ook get take\"", result)
    Assert.Contains("@program $room:look", result)

[<Fact>]
let ``renderVerbFile renders the raw #0 self-reference, not a fabricated $0 corponym`` () =
    let verb: VerbExport =
        { Names = "do_command"
          Owner = 2L
          Perms = "rxd"
          Dobj = "none"
          Prep = "none"
          Iobj = "none"
          Code = [ "return 0;" ] }

    let result = renderVerbFile "#0" verb

    Assert.Contains("@verb #0:\"do_command\"", result)
    Assert.Contains("@program #0:do_command", result)

[<Fact>]
let ``renderObjectMoo preserves parent order, sorts properties by name, resolves corponym parents`` () =
    let data: ObjectExport =
        { Parents = [ 4L; 1L ] // deliberately not in objnum/alpha order - must survive verbatim
          Owner = 2L
          Flags = [ "r"; "f" ]
          Properties =
            [ { Name = "zeta"; Owner = 2L; Perms = "rc"; ValueLiteral = "1" }
              { Name = "alpha"; Owner = 2L; Perms = "rc"; ValueLiteral = "2" } ]
          Verbs = []
          LiveName = "Generic Room"
          Aliases = [ "room"; "generic room" ] }

    let corponymsByObjnum = Map.ofList [ 4L, "string_utils" ] // 1L deliberately uncorified

    let result = renderObjectMoo corponymsByObjnum "$room" data []

    let expected =
        "@object $room\n"
        + "parents: $string_utils #1\n"
        + "owner: #2\n"
        + "flags: r f\n"
        + "name: \"Generic Room\"\n"
        + "aliases: \"room\" \"generic room\"\n"
        + "verbs: \n"
        + "\n"
        + "@property \"alpha\" owner=#2 perms=rc\n"
        + "2\n"
        + ".\n"
        + "\n"
        + "@property \"zeta\" owner=#2 perms=rc\n"
        + "1\n"
        + ".\n"

    Assert.Equal(expected, result)

[<Fact>]
let ``renderObjectMoo renders the raw #0 self-reference for FORMAT.md's system-object exception`` () =
    let data: ObjectExport =
        { Parents = []
          Owner = 2L
          Flags = [ "wizard"; "programmer" ]
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let result = renderObjectMoo Map.empty "#0" data []

    Assert.StartsWith("@object #0\n", result)

// ---------------------------------------------------------------------------
// builtins.json - restored producer for the retired `$vcs:export_builtins()`
// verb's job (see Metadata/Loader.fs's `parseBuiltinFunc`, which this must
// match field-for-field: "name"/"minargs"/"maxargs"/"types").
// ---------------------------------------------------------------------------

[<Fact>]
let ``renderBuiltinsJson matches Loader.fs's expected "functions" array shape`` () =
    let functions =
        [ { Name = "eval"; MinArgs = 1; MaxArgs = 1; Types = [ 2 ] }
          { Name = "notify"; MinArgs = 2; MaxArgs = 3; Types = [ 1; 2; -1 ] } ]

    let result = renderBuiltinsJson functions
    use doc = JsonDocument.Parse(result)
    let funcsEl = doc.RootElement.GetProperty("functions")

    Assert.Equal(2, funcsEl.GetArrayLength())

    let eval = funcsEl.[0]
    Assert.Equal("eval", eval.GetProperty("name").GetString())
    Assert.Equal(1, eval.GetProperty("minargs").GetInt32())
    Assert.Equal(1, eval.GetProperty("maxargs").GetInt32())
    Assert.Equal<int list>([ 2 ], eval.GetProperty("types").EnumerateArray() |> Seq.map (fun t -> t.GetInt32()) |> List.ofSeq)

    let notify = funcsEl.[1]
    // maxargs > minargs and a -1 ("any") type proto both need to survive -
    // not special-cased anywhere in the rendering path.
    Assert.Equal(3, notify.GetProperty("maxargs").GetInt32())
    Assert.Equal(-1, notify.GetProperty("types").EnumerateArray() |> Seq.last |> fun t -> t.GetInt32())

// ---------------------------------------------------------------------------
// describePath - the non-corified verb capture tier's "#objnum" label
// convention (see the function's own comment for why "#" can never collide
// with a real corponym).
// ---------------------------------------------------------------------------

[<Fact>]
let ``describePath resolves a corponym'd object.moo and verb file as before`` () =
    Assert.Equal(Some("room", "(properties)"), describePath "objects/room/object.moo")
    Assert.Equal(Some("room", "look_self"), describePath "objects/room/verbs/look_self.moo")

[<Fact>]
let ``describePath resolves an anon verb file to a "#objnum" label`` () =
    Assert.Equal(Some("#123", "test_verb"), describePath "objects/_anon/123/verbs/test_verb.moo")

[<Fact>]
let ``describePath returns None for paths outside objects/ entirely`` () =
    Assert.Equal(None, describePath "corponyms.moo")
    Assert.Equal(None, describePath "FORMAT_VERSION")
