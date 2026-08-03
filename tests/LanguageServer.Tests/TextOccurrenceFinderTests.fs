/// Fixture graphs for `Handlers.findTextOccurrences` - unlike every other
/// finder in this project, this one reads real files on disk
/// (`VerbNode.SourcePath`), so fixtures need an actual temp file per verb
/// rather than a hand-built AST. Each test writes its own temp file(s) and
/// cleans them up in a `finally`, since xUnit doesn't guarantee ordering
/// between tests that might otherwise collide on a shared path.
module LanguageServer.Tests.TextOccurrenceFinderTests

open System.IO
open Xunit
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (name: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private verbNodeAt (definedOn: ObjRef) (meta: VerbMeta) (sourcePath: string option) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = sourcePath
      Ast = None
      DiagnosticCount = 0
      Tokens = None }

let private objNode (num: ObjRef) (verbs: VerbNode list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = None
      Flags = None
      Properties = []
      Aliases = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

/// Writes `lines` to a fresh temp file, wrapped in the same `@verb`/
/// `@program`/`.` envelope `Exporter.renderVerbFile` produces for a real
/// capture (`Metadata.TreeFormat.parseVerbFileLines`'s own doc comment) -
/// `findTextOccurrences` reads through `parseVerbFile`, which expects (and
/// strips) exactly this shape, so a bare code-only fixture file would
/// under-report by two header lines' worth of position. Returns the path;
/// callers delete it themselves in a `finally`.
let private writeTempVerb (codeLines: string list) : string =
    let path = Path.GetTempFileName()
    let envelope = [ "@verb #1:\"sweep\" this none this rxd #2"; "@program #1:sweep" ] @ codeLines @ [ "." ]
    File.WriteAllLines(path, envelope)
    path

[<Fact>]
let ``multiple occurrences on one line are each their own entry`` () =
    let path = writeTempVerb [ "x = foo + foo + bar;" ]

    try
        let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some path)
        let graph = graphOf [ objNode 1L [ v ] ]

        let occurrences = findTextOccurrences graph "foo"
        Assert.Equal(2, occurrences.Length)
        Assert.Contains(occurrences, (fun o -> o.Line = 1 && o.Col = 5))
        Assert.Contains(occurrences, (fun o -> o.Line = 1 && o.Col = 11))
    finally
        File.Delete path

[<Fact>]
let ``occurrences on different lines are all found`` () =
    let path = writeTempVerb [ "x = foo;"; "y = 1;"; "z = foo;" ]

    try
        let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some path)
        let graph = graphOf [ objNode 1L [ v ] ]

        let occurrences = findTextOccurrences graph "foo"
        Assert.Equal(2, occurrences.Length)
        Assert.Contains(occurrences, (fun o -> o.Line = 1))
        Assert.Contains(occurrences, (fun o -> o.Line = 3))
        Assert.DoesNotContain(occurrences, (fun o -> o.Line = 2))
    finally
        File.Delete path

[<Fact>]
let ``matching is case-insensitive`` () =
    let path = writeTempVerb [ "x = FOO;" ]

    try
        let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some path)
        let graph = graphOf [ objNode 1L [ v ] ]

        Assert.Single(findTextOccurrences graph "foo") |> ignore
    finally
        File.Delete path

[<Fact>]
let ``the matched line text is carried through unchanged`` () =
    let path = writeTempVerb [ "x = foo;" ]

    try
        let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some path)
        let graph = graphOf [ objNode 1L [ v ] ]

        let occurrence = Assert.Single(findTextOccurrences graph "foo")
        Assert.Equal("x = foo;", occurrence.LineText)
    finally
        File.Delete path

[<Fact>]
let ``a verb with no SourcePath is skipped without error`` () =
    let v = verbNodeAt 1L (verbMeta 1 "sweep") None
    let graph = graphOf [ objNode 1L [ v ] ]

    Assert.Empty(findTextOccurrences graph "foo")

[<Fact>]
let ``a verb whose SourcePath no longer exists on disk is skipped without error`` () =
    let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some (Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-" + string (System.Guid.NewGuid()) + ".moo")))
    let graph = graphOf [ objNode 1L [ v ] ]

    Assert.Empty(findTextOccurrences graph "foo")

[<Fact>]
let ``an empty query returns no occurrences`` () =
    let path = writeTempVerb [ "x = foo;" ]

    try
        let v = verbNodeAt 1L (verbMeta 1 "sweep") (Some path)
        let graph = graphOf [ objNode 1L [ v ] ]

        Assert.Empty(findTextOccurrences graph "")
    finally
        File.Delete path
