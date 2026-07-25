/// Round-trips `Metadata.Loader.load` against the real `Survive` tree (same
/// corpus `Language.Tests` reads) - the proof point per the M4 plan's 4.3b
/// verify step: "load the real exporter's output... confirm every
/// object/verb appears in the graph correctly."
module Metadata.Tests.LoaderTests

open System.IO
open System.Text.Json
open Xunit
open Metadata.Schema
open Metadata.Loader

let private surviveRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Survive"))

// `load` reparses every captured verb in the corpus - shared across facts
// via `lazy` so a full `dotnet test` run pays that cost once, not once per
// fact.
let private graph = lazy (load surviveRoot)

[<Fact>]
let ``Survive metadata.json is present`` () =
    Assert.True(File.Exists(Path.Combine(surviveRoot, "metadata.json")))

[<Fact>]
let ``loads every object metadata.json contains`` () =
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(surviveRoot, "metadata.json")))
    let expectedCount = doc.RootElement.GetProperty("objects").GetArrayLength()

    Assert.Equal(expectedCount, graph.Value.Objects.Count)

[<Fact>]
let ``drops no verbs - every object's verb count matches metadata.json`` () =
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(surviveRoot, "metadata.json")))

    for objEl in doc.RootElement.GetProperty("objects").EnumerateArray() do
        let num = parseObjRef (objEl.GetProperty("num").GetString())
        let expectedVerbCount = objEl.GetProperty("verbs").GetArrayLength()
        let node = Map.find num graph.Value.Objects
        Assert.Equal(expectedVerbCount, node.Verbs.Length)

[<Fact>]
let ``known object names resolve from lookups.toml`` () =
    let systemObject = Map.find 0L graph.Value.Objects
    Assert.Equal(Some "The_System_Object", systemObject.Name)

[<Fact>]
let ``an object's real (unsanitized) live name loads from metadata.json`` () =
    // #3 (Generic Room) is a stable, long-captured ToastCore object -
    // `Name` (lookups.toml) is the sanitized "Generic_Room" used for the
    // git directory; `LiveName` should be the real, space-containing name
    // as `metadata.json`'s `"name"` field (from `i.name`) actually has it.
    let genericRoom = Map.find 3L graph.Value.Objects
    Assert.Equal(Some "Generic Room", genericRoom.LiveName)

[<Fact>]
let ``a known captured verb parses cleanly with an attached AST`` () =
    let vcsObj =
        graph.Value.Objects |> Map.toSeq |> Seq.map snd |> Seq.find (fun o -> o.Name = Some "VCS")

    let exportVerb =
        vcsObj.Verbs |> List.find (fun v -> v.Meta.Names |> List.contains "export_metadata")

    Assert.True(exportVerb.SourcePath.IsSome)
    Assert.True(exportVerb.Ast.IsSome)
    Assert.Equal(0, exportVerb.DiagnosticCount)

[<Fact>]
let ``Survive builtins.json is present`` () =
    Assert.True(File.Exists(Path.Combine(surviveRoot, "builtins.json")))

[<Fact>]
let ``loads every builtin builtins.json contains, keyed by name`` () =
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(surviveRoot, "builtins.json")))
    let expectedCount = doc.RootElement.GetProperty("functions").GetArrayLength()
    Assert.Equal(expectedCount, graph.Value.Builtins.Count)

[<Fact>]
let ``a known builtin's arity and arg types load correctly`` () =
    let f = Map.find "function_info" graph.Value.Builtins
    Assert.Equal(0, f.MinArgs)
    Assert.Equal(1, f.MaxArgs)
    Assert.Equal<int list>([ 2 ], f.ArgTypes) // TYPE_STR

[<Fact>]
let ``a builtin with a documented C-source signature loads its real parameter names`` () =
    let f = Map.find "strsub" graph.Value.Builtins
    Assert.Equal(Some [ "source"; "what"; "with"; "case-matters" ], f.ParamNames)

[<Fact>]
let ``a builtin with no documented signature has ParamNames = None, not a crash`` () =
    let f = Map.find "length" graph.Value.Builtins
    Assert.True(f.ParamNames.IsNone)

// --- Owner/Flags/Properties (added for the object inspector) ------------

[<Fact>]
let ``VCS's owner, flags, and properties load correctly from real exported data`` () =
    let vcs = Map.find 127L graph.Value.Objects
    Assert.Equal(Some 2L, vcs.Owner)

    match vcs.Flags with
    | Some flags ->
        Assert.False(flags.Player)
        Assert.False(flags.Programmer)
        Assert.False(flags.Wizard)
        Assert.False(flags.Fertile)
    | None -> Assert.Fail "expected Some flags"

    Assert.Contains(vcs.Properties, fun (p: PropertyMeta) -> p.Name = "repo_root" && p.Owner = 2L && p.Perms = "rw")

[<Fact>]
let ``the Wizard player object's flags reflect real player/programmer/wizard bits`` () =
    let wizard = Map.find 2L graph.Value.Objects

    match wizard.Flags with
    | Some flags ->
        Assert.True(flags.Player)
        Assert.True(flags.Programmer)
        Assert.True(flags.Wizard)
    | None -> Assert.Fail "expected Some flags"

[<Fact>]
let ``Generic Room's real fertile/read flags load correctly`` () =
    let genericRoom = Map.find 3L graph.Value.Objects

    match genericRoom.Flags with
    | Some flags ->
        Assert.True(flags.Read)
        Assert.True(flags.Fertile)
        Assert.False(flags.Wizard)
    | None -> Assert.Fail "expected Some flags"
