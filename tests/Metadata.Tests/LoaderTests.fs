/// Exercises `Metadata.Loader.load` against synthetic, hand-built FORMAT.md
/// trees rather than the real `Survive` checkout - the old suite asserted
/// specific facts about the now-retired toastcore+`$vcs` tree (VCS object
/// #127, Wizard #2, Generic Room #3...), all gone now that `Survive` is
/// cleared and re-exported in the new sidecar-owned format. Fixtures are
/// built with `Sidecar.Exporter`'s own renderers, mirroring
/// `Sidecar.Tests/TreeParserTests.fs`'s existing convention, rather than
/// hand-writing text that could silently drift from the real render format.
module Metadata.Tests.LoaderTests

open System.IO
open Xunit
open Metadata.Schema
open Metadata.Loader
open Sidecar.Exporter

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-loader-test-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

/// A small, synthetic tree: two corponym-bearing objects (`room` #3,
/// parented on `$string_utils` and raw `#1`; `string_utils` #4, no parents)
/// plus a third corponym (`ghost` #99) with no `object.moo` at all -
/// exercising the "corponym without a captured directory yet" tolerance
/// this loader and `Sidecar.TreeParser` both share.
let private writeFixtureTree (dir: string) =
    File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")

    let corponyms = [ "room", 3L; "string_utils", 4L; "ghost", 99L ]
    File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo corponyms)

    let corponymsByObjnum = Map.ofList [ 4L, "string_utils" ]

    let lookSelf: VerbExport =
        { Names = "look_self"
          Owner = 3L
          Perms = "rxd"
          Dobj = "this"
          Prep = "none"
          Iobj = "this"
          Code = [ "\"Describe this room.\";"; "player:tell(this.description);" ] }

    let roomData: ObjectExport =
        { Parents = [ 4L; 1L ] // $string_utils, then raw #1 (uncorponymed)
          Owner = 3L
          Flags = [ "r"; "f" ]
          Properties = [ { Name = "description"; Owner = 3L; Perms = "rc"; ValueLiteral = "\"A small room.\"" } ]
          Verbs = [ lookSelf ]
          LiveName = "A Small Room"
          Aliases = [ "room"; "small room" ] }

    let roomDir = Path.Combine(dir, "objects", "room")
    let roomVerbsDir = Path.Combine(roomDir, "verbs")
    Directory.CreateDirectory(roomVerbsDir) |> ignore

    let roomVerbFileNames = assignVerbFileNames roomData.Verbs

    File.WriteAllText(
        Path.Combine(roomDir, "object.moo"),
        renderObjectMoo corponymsByObjnum "$room" roomData roomVerbFileNames
    )

    for verb, fileName in roomVerbFileNames do
        File.WriteAllText(Path.Combine(roomVerbsDir, fileName), renderVerbFile "$room" verb)

    let stringUtilsData: ObjectExport =
        { Parents = []
          Owner = 3L
          Flags = []
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let suDir = Path.Combine(dir, "objects", "string_utils")
    Directory.CreateDirectory(Path.Combine(suDir, "verbs")) |> ignore

    File.WriteAllText(
        Path.Combine(suDir, "object.moo"),
        renderObjectMoo corponymsByObjnum "$string_utils" stringUtilsData []
    )
    // "ghost" #99 deliberately has no objects/ghost directory at all.

    // FORMAT.md §1's `#0` exception: no corponym at all (not listed in
    // corponyms.moo above), yet still gets a directory - "0", the raw
    // `@object #0` self-reference.
    let systemObjectData: ObjectExport =
        { Parents = []
          Owner = 0L
          Flags = [ "wizard"; "programmer" ]
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    let systemObjectDir = Path.Combine(dir, "objects", "0")
    Directory.CreateDirectory(Path.Combine(systemObjectDir, "verbs")) |> ignore

    File.WriteAllText(
        Path.Combine(systemObjectDir, "object.moo"),
        renderObjectMoo corponymsByObjnum "#0" systemObjectData []
    )

[<Fact>]
let ``loads every corponym-bearing object that has a captured directory`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir

        Assert.Equal(3, graph.Objects.Count) // room + string_utils + #0, not ghost
        Assert.True(Map.containsKey 3L graph.Objects)
        Assert.True(Map.containsKey 4L graph.Objects)
        Assert.False(Map.containsKey 99L graph.Objects)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``#0 loads even though it has no corponym at all - FORMAT.md's one exception`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let systemObject = Map.find 0L graph.Objects

        Assert.Equal(None, systemObject.Name) // no real corponym - honest, not a fabricated label
        Assert.Equal(Some { Player = false; Programmer = true; Wizard = true; Read = false; Write = false; Fertile = false; Anonymous = false }, systemObject.Flags)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``SystemObjectProperties matches corponyms.moo exactly, including uncaptured entries`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir

        Assert.Equal<Map<string, int64>>(
            Map.ofList [ "room", 3L; "string_utils", 4L; "ghost", 99L ],
            graph.SystemObjectProperties
        )
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``resolves both $name and raw #N parent references`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let room = Map.find 3L graph.Objects

        Assert.Equal<int64 list>([ 4L; 1L ], room.Parents)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``computes Children by inverting Parents`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let stringUtils = Map.find 4L graph.Objects

        Assert.Equal<int64 list>([ 3L ], stringUtils.Children)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``LiveName/Aliases are populated from the name:/aliases: header lines, Owner and Flags are always Some`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let room = Map.find 3L graph.Objects

        Assert.Equal(Some "A Small Room", room.LiveName)
        Assert.Equal<string list>([ "room"; "small room" ], room.Aliases)
        Assert.Equal(Some 3L, room.Owner)

        match room.Flags with
        | Some flags ->
            Assert.True(flags.Read)
            Assert.True(flags.Fertile)
            Assert.False(flags.Wizard)
        | None -> Assert.Fail "expected Some flags"
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a genuinely empty live .name normalizes to LiveName = None, not Some ""`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let stringUtils = Map.find 4L graph.Objects // fixture's stringUtilsData has LiveName = ""

        Assert.Equal(None, stringUtils.LiveName)
        Assert.Equal<string list>([], stringUtils.Aliases)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a verb's captured source parses into an AST with SourcePath populated`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let room = Map.find 3L graph.Objects
        let verb = room.Verbs |> List.find (fun v -> v.Meta.Names |> List.contains "look_self")

        Assert.Equal(1, verb.Meta.Index)
        Assert.True(verb.SourcePath.IsSome)
        Assert.True(verb.Ast.IsSome)
        Assert.Equal(0, verb.DiagnosticCount)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a property's structural fields load, without its value`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir
        let room = Map.find 3L graph.Objects

        Assert.Contains(room.Properties, fun (p: PropertyMeta) -> p.Name = "description" && p.Owner = 3L && p.Perms = "rc")
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``builtins.json absence degrades to an empty map, not a crash`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir
        let graph = load dir

        Assert.Equal(0, graph.Builtins.Count)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a present, valid builtins.json loads name/arity/types and merges in the embedded static docs`` () =
    let dir = tempDir ()

    try
        writeFixtureTree dir

        let functions =
            [ { Name = "eval"; MinArgs = 1; MaxArgs = 1; Types = [ 2 ] }
              { Name = "notify"; MinArgs = 2; MaxArgs = 3; Types = [ 1; 2; -1 ] } ]

        File.WriteAllText(Path.Combine(dir, "builtins.json"), renderBuiltinsJson functions)

        let graph = load dir
        let evalFn = Map.find "eval" graph.Builtins

        Assert.Equal(1, evalFn.MinArgs)
        Assert.Equal(1, evalFn.MaxArgs)
        Assert.Equal<int list>([ 2 ], evalFn.ArgTypes)
        // Static, build-time-embedded resources (unrelated to this checkout's
        // builtins.json) - confirms the two sources actually merge by name.
        Assert.True(evalFn.Description.IsSome)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a dangling $name parent reference fails loudly rather than silently dropping`` () =
    let dir = tempDir ()

    try
        File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")
        File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo [ "room", 3L ])

        // A genuinely dangling $name reference can only occur via a
        // corrupted or hand-edited tree - renderObjectMoo only ever emits
        // $name for entries actually present in its corponymsByObjnum map,
        // so this is hand-written rather than rendered.
        let roomDir = Path.Combine(dir, "objects", "room")
        Directory.CreateDirectory(Path.Combine(roomDir, "verbs")) |> ignore

        File.WriteAllText(
            Path.Combine(roomDir, "object.moo"),
            "@object $room\nparents: $nonexistent\nowner: #3\nflags: \nverbs: \n"
        )

        Assert.Throws<System.Exception>(fun () -> load dir |> ignore) |> ignore
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a pre-name/aliases-feature object.moo (no name:/aliases: lines) loads with LiveName = None, Aliases = []`` () =
    let dir = tempDir ()

    try
        File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")
        File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo [ "room", 3L ])

        // Hand-written rather than via renderObjectMoo (which always emits
        // name:/aliases: now) - simulates a tree exported before this
        // feature existed, e.g. the real, already-committed Survive/
        // ToastCoreWorld corpora before their next re-export.
        let roomDir = Path.Combine(dir, "objects", "room")
        Directory.CreateDirectory(Path.Combine(roomDir, "verbs")) |> ignore

        File.WriteAllText(Path.Combine(roomDir, "object.moo"), "@object $room\nparents: #1\nowner: #3\nflags: \nverbs: \n")

        let graph = load dir
        let room = Map.find 3L graph.Objects

        Assert.Equal(None, room.LiveName)
        Assert.Equal<string list>([], room.Aliases)
    finally
        Directory.Delete(dir, true)
