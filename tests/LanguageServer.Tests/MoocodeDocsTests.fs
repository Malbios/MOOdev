/// Hand-built fixture graphs for the corified-verb-docs entries `moocodeDocs`
/// adds to the MOOcode docs catalog - same reasoning as `VerbMetricsTests.fs`:
/// precise control over each verb's AST rather than depending on a larger
/// synthetic corpus. Tests go through the public `moocodeDocs` entry point
/// (not the private `leadingDocComment`/`corifiedVerbDocEntries` helpers,
/// which are invisible from this separate test assembly).
module LanguageServer.Tests.MoocodeDocsTests

open Xunit
open Language.Ast
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (name: string) (dobj: string) (prep: string) (iobj: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = "rxd"
      Dobj = dobj
      Prep = prep
      Iobj = iobj }

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) (ast: Stmt list) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = Some ast
      DiagnosticCount = 0
      Tokens = Some [||] }

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

let private graphOf (systemObjectProperties: (string * ObjRef) list) (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.ofList systemObjectProperties
      Builtins = Map.empty }

let private corifiedEntries (graph: Graph) : MoocodeDocEntry[] =
    moocodeDocs graph Map.empty |> Array.filter (fun e -> e.Kind = "corified-verb")

[<Fact>]
let ``a corified verb with a single leading string-literal statement gets a doc entry`` () =
    let v =
        verbNode 2L (verbMeta 1 "pad" "this" "none" "this") [ ExprStmt(StrLit "Pads a string to a given length."); Return None ]

    let graph = graphOf [ "string_utils", 2L ] [ objNode 2L [ v ] ]

    let entries = corifiedEntries graph
    Assert.Single(entries) |> ignore

    Assert.Contains(
        entries,
        (fun e ->
            e.Name = "$string_utils:pad"
            && e.Signature = "$string_utils:pad(this, none, this)"
            && e.Description = "Pads a string to a given length.")
    )

[<Fact>]
let ``a corified verb with three consecutive leading string-literal statements joins them with newlines`` () =
    let v =
        verbNode 2L (verbMeta 1 "helper" "this" "none" "this") [ ExprStmt(StrLit "Line one."); ExprStmt(StrLit "Line two."); ExprStmt(StrLit "Line three."); Return None ]

    let graph = graphOf [ "test_util", 2L ] [ objNode 2L [ v ] ]

    let entries = corifiedEntries graph
    Assert.Contains(entries, (fun e -> e.Name = "$test_util:helper" && e.Description = "Line one.\nLine two.\nLine three."))

[<Fact>]
let ``a corified verb with no leading string-literal gets no doc entry`` () =
    let v = verbNode 2L (verbMeta 1 "nodoc" "this" "none" "this") [ Return(Some(IntLit 1L)) ]
    let graph = graphOf [ "test_util", 2L ] [ objNode 2L [ v ] ]

    Assert.Empty(corifiedEntries graph)

[<Fact>]
let ``a stray string-literal statement after real code doesn't count as a doc`` () =
    let v =
        verbNode 2L (verbMeta 1 "laterstring" "this" "none" "this") [ Return(Some(IntLit 1L)); ExprStmt(StrLit "Not a doc comment.") ]

    let graph = graphOf [ "test_util", 2L ] [ objNode 2L [ v ] ]

    Assert.Empty(corifiedEntries graph)

[<Fact>]
let ``a verb on an object with no corponym isn't included even if documented`` () =
    let v = verbNode 3L (verbMeta 1 "documented" "this" "none" "this") [ ExprStmt(StrLit "Has a doc, but not corified.") ]
    let graph = graphOf [] [ objNode 3L [ v ] ]

    Assert.Empty(corifiedEntries graph)
