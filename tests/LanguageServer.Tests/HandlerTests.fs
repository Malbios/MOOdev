/// End-to-end handler tests against a small, synthetic FORMAT.md tree -
/// calling `MooLspServer`'s overridden methods directly (bypassing the
/// JSON-RPC/websocket transport, which `WsTransport.fs` owns and isn't the
/// concern here). Built with `Sidecar.Exporter`'s own renderers rather than
/// depending on the real `Survive` checkout: the old version of this file
/// anchored to specific real content (VCS object #127's verbs,
/// `@paranoid_Database/7_gc.moo`'s exact line numbers, etc.) from the now-
/// retired toastcore+`$vcs` tree, which stopped existing once `Survive` was
/// cleared and re-exported in the new sidecar-owned format. A synthetic
/// fixture is more robust: it can't drift out from under these tests the
/// way real, evolving world content can.
module LanguageServer.Tests.HandlerTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Ionide.LanguageServerProtocol.Types
open Language.Ast
open Metadata.Schema
open Metadata.Loader
open LanguageServer.Handlers
open LanguageServer.AstQuery
open LanguageServer.SidecarBridge
open Sidecar.Exporter

// ---------------------------------------------------------------------------
// Fixture tree - built once for the whole module.
// ---------------------------------------------------------------------------

let private verb (names: string) (code: string list) : VerbExport =
    { Names = names
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this"
      Code = code }

/// A small, synthetic FORMAT.md tree - see MOOdy's `FORMAT.md` for the
/// on-disk grammar `Sidecar.Exporter`'s renderers produce. The temp
/// directory is deliberately never deleted: a per-fact `tempDir()`
/// `finally Directory.Delete` (as `Sidecar.Tests`/`Metadata.Tests` use)
/// doesn't fit a module-level shared fixture - a handful of small text
/// files left in the OS temp folder is a harmless, common tradeoff.
let private fixtureDir =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-handlertests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    let corponyms =
        [ "wizard", 2L
          "room", 3L
          "room_child", 10L
          "command_utils", 20L
          "gc_util", 30L
          "vcs_util", 40L
          "hacker_util", 50L
          "bigresident_util", 60L ]

    File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")
    File.WriteAllText(Path.Combine(dir, "corponyms.moo"), renderCorponymsMoo corponyms)

    // Real arity/type data for the few builtins these tests touch, confirmed
    // against ToastStunt's own `register_function` calls (`list.cc:1765,1794`,
    // `objects.cc:1340`) - `graph.Builtins` is otherwise always empty in the
    // new format (`builtins.json` was a retired `$vcs`-only capture, not
    // reproduced by anything else).
    File.WriteAllText(
        Path.Combine(dir, "builtins.json"),
        """{"functions": [
  {"name": "length", "minargs": 1, "maxargs": 1, "types": [-1]},
  {"name": "typeof", "minargs": 1, "maxargs": 1, "types": [-1]},
  {"name": "strsub", "minargs": 3, "maxargs": 4, "types": [2, 2, 2, -1]}
]}"""
    )

    let corponymsByObjnum = corponyms |> List.map (fun (n, o) -> o, n) |> Map.ofList

    let writeObject (name: string) (data: ObjectExport) =
        let objDir = Path.Combine(dir, "objects", name)
        let verbsDir = Path.Combine(objDir, "verbs")
        Directory.CreateDirectory(verbsDir) |> ignore
        let verbFileNames = assignVerbFileNames data.Verbs

        File.WriteAllText(
            Path.Combine(objDir, "object.moo"),
            renderObjectMoo corponymsByObjnum ("$" + name) data verbFileNames
        )

        for v, fileName in verbFileNames do
            File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile ("$" + name) v)

    writeObject
        "wizard"
        { Parents = []
          Owner = 2L
          Flags = [ "player"; "programmer"; "wizard" ]
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    writeObject
        "room"
        { Parents = [ 1L ] // raw, uncorponymed - like README.Minimal's "Root Class #1"
          Owner = 2L
          Flags = [ "r"; "f" ]
          // Not overridden anywhere - the plain "inherited, not shadowed"
          // case `ResolveEffectiveMember` tests need alongside the shadowed
          // "say" verb below.
          Properties = [ { Name = "description"; Owner = 2L; Perms = "r"; ValueLiteral = "\"A room.\"" } ]
          Verbs = [ verb "say" [ "\"Say something.\";"; "player:tell(\"You say, \\\"\", argstr, \"\\\"\");" ] ]
          LiveName = "Generic Room"
          Aliases = [ "room"; "generic room" ] }

    writeObject
        "room_child"
        { Parents = [ 3L ] // exists purely so `room` has a non-empty Children
          Owner = 2L
          Flags = []
          Properties = []
          // Shadows `room` (#3)'s own "say" - the one real override
          // scenario `ResolveEffectiveMember` tests need: an object whose
          // own copy of a name wins over an ancestor's, not just "some
          // definer in the chain."
          Verbs = [ verb "say" [ "\"Say something, child-style.\";"; "player:tell(\"You shout!\");" ] ]
          LiveName = ""
          Aliases = [] }

    writeObject
        "command_utils"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ verb "suspend_if_needed" [ "\"Suspend if needed.\";"; "return 1;" ] ]
          LiveName = ""
          Aliases = [] }

    writeObject
        "gc_util"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs =
            [ verb
                  "gc"
                  [ "\"Garbage collect old data.\";"
                    "if (1)"
                    "endif"
                    "threshold = 60 * 60 * 24 * 3;"
                    "for x in (properties(this))"
                    "  if (length(x) > 0)"
                    "    $command_utils:suspend_if_needed(0);"
                    "  endif"
                    "endfor"
                    "return threshold;" ] ]
          LiveName = ""
          Aliases = [] }

    writeObject
        "vcs_util"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = [ { Name = "repo_root"; Owner = 2L; Perms = "rw"; ValueLiteral = "\"/tmp/repo\"" } ]
          Verbs =
            [ verb "capture_verb" [ "\"Capture a verb to disk.\";"; "return 1;" ]
              verb
                  "handle_verb_programmed"
                  [ "\"Called after a verb is programmed.\";"; "path = this:capture_verb(0, \"x\");"; "return path;" ]
              verb "import_all" [ "\"Import everything.\";"; "this:capture_verb(0, \"y\");" ]
              verb
                  "sanitize_name"
                  [ "\"Sanitize a name.\";"
                    "name = args[1];"
                    "name = strsub(name, \" \", \"_\", 0);"
                    "return name;" ]
              verb "export_metadata" [ "\"Export metadata.\";"; "return 1;" ]
              verb "export_builtins" [ "\"Export builtins.\";"; "return 1;" ] ]
          LiveName = ""
          Aliases = [] }

    writeObject
        "hacker_util"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = []
          LiveName = ""
          Aliases = [] }

    writeObject
        "bigresident_util"
        { Parents = []
          Owner = 2L
          Flags = []
          Properties = []
          Verbs = [ verb "init_for_core" [ "\"Init for core.\";"; "this.target = $hacker_util;" ] ]
          LiveName = ""
          Aliases = [] }

    dir

let private graph = lazy (load fixtureDir)

/// Test-only stand-in for the live Sidecar bridge - resolves against this
/// module's own static fixture graph instead of a real live MOO connection,
/// since these tests exercise `Handlers.fs`'s hover/definition/completion
/// logic, not `SidecarBridge`'s own live-query wiring (that's a separate
/// concern, verified live rather than by a fixture). Reusing
/// `Metadata.Resolver.findCallableVerb` against the identical fixture graph
/// the old static-graph-based hover/definition code used to call directly
/// means every existing assertion below still holds - the *data* comes from
/// the same place, just routed through the new bridge interface instead of
/// called in-process. `bareNameFor` mirrors the live protocol's own
/// convention (`SidecarBridge.fs`'s `DefinerName`/`OwnerName`: a bare name,
/// or `""` if none - `hoverForResolvedVerbLive` does the "(#N)" wrapping
/// itself), not `Handlers.fs`'s private `definerName` (which instead
/// defaults to a pre-formatted `"#N"`).
let private bareNameFor (objRef: ObjRef) : string =
    graph.Value.Objects
    |> Map.tryFind objRef
    |> Option.bind (fun o -> o.LiveName |> Option.orElse o.Name)
    |> Option.defaultValue ""

let private fakeBridge: SidecarBridge =
    { ResolveVerbDispatch =
        fun startObj verbName ->
            task {
                match Metadata.Resolver.findCallableVerb graph.Value startObj verbName with
                | Some(definer, foundVerb) ->
                    return
                        Some
                            { Definer = definer
                              DefinerName = bareNameFor definer
                              Names = String.concat " " foundVerb.Meta.Names
                              Perms = foundVerb.Meta.Perms
                              Dobj = foundVerb.Meta.Dobj
                              Prep = foundVerb.Meta.Prep
                              Iobj = foundVerb.Meta.Iobj
                              Owner = foundVerb.Meta.Owner
                              OwnerName = bareNameFor foundVerb.Meta.Owner
                              // Empty on purpose - this fake resolves from
                              // the static graph, which doesn't carry raw
                              // source text; hover's new auto-inferred
                              // section degrades cleanly to "nothing to add"
                              // when `Code` is empty (see
                              // `hoverForResolvedVerbLive`), so every
                              // existing assertion below is unaffected.
                              Code = [] }
                | None -> return None
            }
      GetBuiltins = fun () -> task { return graph.Value.Builtins } }

let private server = lazy (new MooLspServer(new MooLspClient(), graph.Value, fakeBridge))

/// Finds `(objRef, VerbNode)` by object number + verb name - simpler than
/// the old file's path-suffix matching (a workaround for not knowing real
/// object numbers in advance; this fixture's numbering is fully known).
let private findVerb (objRef: ObjRef) (verbName: string) : ObjRef * VerbNode =
    let node = Map.find objRef graph.Value.Objects
    let v = node.Verbs |> List.find (fun v -> v.Meta.Names |> List.contains verbName)
    objRef, v

let private uriFor (objRef: ObjRef) (verbName: string) : string = moodevVerbUri objRef verbName

/// The (0-based) LSP `Position` of the reference matching `predicate` -
/// robust against real source content instead of hand-counted line/column
/// comments, the same pattern several of the old file's own tests already
/// used for this exact reason.
let private positionOf (predicate: Reference -> bool) (stmts: Stmt list) : Position =
    let r = collectReferences stmts |> List.pick (fun r -> if predicate r.Ref then Some r else None)
    { Line = uint32 (r.Line - 1); Character = uint32 (r.Col - 1) }

// ---------------------------------------------------------------------------
// Hover / definition
// ---------------------------------------------------------------------------

[<Fact>]
let ``fixture verb exists where the test expects it`` () =
    let _, v = findVerb 30L "gc"
    Assert.True(v.Meta.Names |> List.contains "gc")

[<Fact>]
let ``hover on a real $obj:verb(...) call site resolves and describes the verb`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(_, StrLit "suspend_if_needed", _) -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("suspend_if_needed", markup.Value)
            // Owner is shown as a real display label ("wizard (#2) [$wizard]"),
            // not a bare object number - same `displayNameFor` formatting the
            // object picker and inspector already use.
            Assert.Contains("owner: `wizard (#2)", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``definition on the same call site points at a real verb via the moodev-verb URI scheme`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(_, StrLit "suspend_if_needed", _) -> true | _ -> false)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) -> Assert.StartsWith("moodev-verb://", location.Uri)
    | other -> Assert.Fail(sprintf "expected a single Location result, got %A" other)

[<Fact>]
let ``definition on a this:verb() call resolves to the enclosing object's own verb (regression: this used to be treated as unresolvable)`` () =
    let objRef, v = findVerb 40L "handle_verb_programmed"
    let uri = uriFor objRef "handle_verb_programmed"
    let stmts = v.Ast |> Option.get

    let position =
        stmts |> positionOf (function RefVerbCall(Ident("this", _, _), StrLit "capture_verb", _) -> true | _ -> false)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) -> Assert.Equal(moodevVerbUri 40L "capture_verb", location.Uri)
    | other -> Assert.Fail(sprintf "expected a single Location result pointing at vcs_util:capture_verb, got %A" other)

[<Fact>]
let ``definition on a read of a for-loop variable jumps to the for statement that introduces it`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get

    let readPos = stmts |> positionOf (function RefIdent "x" -> true | _ -> false)
    let defLine, defCol = firstBindingSite stmts "x" |> Option.get

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = readPos
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) ->
        Assert.Equal(uri, location.Uri) // same document - no dispatch, just a jump within it
        Assert.Equal({ Line = uint32 (defLine - 1); Character = uint32 (defCol - 1) }, location.Range.Start)
    | other -> Assert.Fail(sprintf "expected a Location pointing at the for statement, got %A" other)

[<Fact>]
let ``definition on a read of a plain-assignment local variable jumps to its first assignment`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let defLine, defCol = firstBindingSite stmts "threshold" |> Option.get

    // `threshold` appears twice: the assignment itself, and the later
    // `return threshold;` read - pick the one that isn't the definition site.
    let readRef =
        collectReferences stmts
        |> List.filter (function { Ref = RefIdent "threshold" } -> true | _ -> false)
        |> List.find (fun r -> (r.Line, r.Col) <> (defLine, defCol))

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (readRef.Line - 1); Character = uint32 (readRef.Col - 1) }
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) ->
        Assert.Equal(uri, location.Uri)
        Assert.Equal({ Line = uint32 (defLine - 1); Character = uint32 (defCol - 1) }, location.Range.Start)
        Assert.Equal({ Line = uint32 (defLine - 1); Character = uint32 (defCol - 1 + "threshold".Length) }, location.Range.End)
    | other -> Assert.Fail(sprintf "expected a Location pointing at the assignment, got %A" other)

[<Fact>]
let ``definition on a built-in verb-call variable (args) returns no result - it has no single introduction site`` () =
    let objRef, v = findVerb 40L "sanitize_name"
    let uri = uriFor objRef "sanitize_name"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefIdent "args" -> true | _ -> false)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

[<Fact>]
let ``hover on a position with no resolvable reference returns no result, not an error`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 999u; Character = 0u } // past the end of the file
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

[<Fact>]
let ``hover on a local variable shows "local variable", not nothing`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let defLine, defCol = firstBindingSite stmts "threshold" |> Option.get

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (defLine - 1); Character = uint32 (defCol - 1) }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup -> Assert.Contains("local variable", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on this:verb() resolves via the enclosing object, not just "receiver unknown"`` () =
    let objRef, v = findVerb 40L "handle_verb_programmed"
    let uri = uriFor objRef "handle_verb_programmed"
    let stmts = v.Ast |> Option.get

    let position =
        stmts |> positionOf (function RefVerbCall(Ident("this", _, _), StrLit "capture_verb", _) -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("capture_verb", markup.Value)
            Assert.Contains("#40 (vcs_util)", markup.Value)
            Assert.DoesNotContain("receiver isn't statically known", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on an unresolvable receiver (player:verb()) still lists candidate defining objects`` () =
    let objRef, v = findVerb 3L "say"
    let uri = uriFor objRef "say"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(Ident("player", _, _), StrLit "tell", _) -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("tell", markup.Value)
            Assert.Contains("receiver isn't statically known", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result (candidate list), got %A" other)

[<Fact>]
let ``hover on a keyword shows its help text`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    // Line 2 (1-based) is exactly "endif" - cols 1-5; 0-based line 2, char 2.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 2u; Character = 2u }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("endif", markup.Value)
            Assert.Contains("elseif", markup.Value)
            Assert.Contains("Runs the first branch whose condition is true", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result (keyword help), got %A" other)

[<Fact>]
let ``hover on a builtin call shows the same signature info as signature help`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefCall("length", _) -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup -> Assert.Contains("length(", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on an implicit built-in variable (args) shows its real description`` () =
    let objRef, v = findVerb 40L "sanitize_name"
    let uri = uriFor objRef "sanitize_name"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefIdent "args" -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("args", markup.Value)
            Assert.Contains("built-in variable", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on a bare $name property (not a call) resolves via the #0 registry`` () =
    let objRef, v = findVerb 60L "init_for_core"
    let uri = uriFor objRef "init_for_core"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefProp(_, StrLit "hacker_util") -> true | _ -> false)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("$hacker_util", markup.Value)
            Assert.Contains("#50", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``an unparseable URI returns no result, not a crash`` () =
    let p: HoverParams =
        { TextDocument = { Uri = "file:///not/a/moodev-verb/uri.moo" }
          Position = { Line = 0u; Character = 0u }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

// ---------------------------------------------------------------------------
// Completions
// ---------------------------------------------------------------------------

[<Fact>]
let ``completion at the $command_utils:suspend_if_needed(0) call site offers local vars, builtins, and reachable verb names`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(_, StrLit "suspend_if_needed", _) -> true | _ -> false)

    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) ->
        let labels = items |> Array.map (fun i -> i.Label) |> Set.ofArray
        Assert.Contains("threshold", labels) // a real local var declared in gc
        Assert.Contains("typeof", labels) // a real builtin
        Assert.Contains("suspend_if_needed", labels) // reachable via $command_utils
    | other -> Assert.Fail(sprintf "expected a flat CompletionItem[] result, got %A" other)

[<Fact>]
let ``completion near a this:verb() call offers the enclosing object's own callable verbs (regression: this used to be treated as unresolvable)`` () =
    let objRef, v = findVerb 40L "handle_verb_programmed"
    let uri = uriFor objRef "handle_verb_programmed"
    let stmts = v.Ast |> Option.get

    let position =
        stmts |> positionOf (function RefVerbCall(Ident("this", _, _), StrLit "capture_verb", _) -> true | _ -> false)

    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) ->
        let labels = items |> Array.map (fun i -> i.Label) |> Set.ofArray
        Assert.Contains("capture_verb", labels) // reachable via `this` == #40 (vcs_util)
        Assert.Contains("export_metadata", labels) // another verb on the same object
    | other -> Assert.Fail(sprintf "expected a flat CompletionItem[] result, got %A" other)

[<Fact>]
let ``completion at an unresolvable position still returns local vars and builtins, not an error`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"

    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 0u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) -> Assert.True(items.Length > 0)
    | other -> Assert.Fail(sprintf "expected a non-empty CompletionItem[] result, got %A" other)

// ---------------------------------------------------------------------------
// Signature help
// ---------------------------------------------------------------------------

[<Fact>]
let ``signature help on a real builtin call site describes its arity and arg types`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefCall("length", _) -> true | _ -> false)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok(Some help) ->
        Assert.Single(help.Signatures) |> ignore
        Assert.StartsWith("length(", help.Signatures.[0].Label)
    | other -> Assert.Fail(sprintf "expected a SignatureHelp result, got %A" other)

[<Fact>]
let ``signature help on a builtin with a documented C-source signature uses its real parameter names`` () =
    let objRef, v = findVerb 40L "sanitize_name"
    let uri = uriFor objRef "sanitize_name"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefCall("strsub", _) -> true | _ -> false)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok(Some help) -> Assert.Equal("strsub(source: str, what: str, with: str, case-matters: any)", help.Signatures.[0].Label)
    | other -> Assert.Fail(sprintf "expected a SignatureHelp result, got %A" other)

[<Fact>]
let ``signature help on a VerbCall (not a builtin) returns no result`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(_, StrLit "suspend_if_needed", _) -> true | _ -> false)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None (verb calls have no function_info signature), got %A" other)

// ---------------------------------------------------------------------------
// Find references
// ---------------------------------------------------------------------------

[<Fact>]
let ``references to $command_utils:suspend_if_needed includes the gc.moo call site`` () =
    let objRef, v = findVerb 30L "gc"
    let uri = uriFor objRef "gc"
    let stmts = v.Ast |> Option.get
    let position = stmts |> positionOf (function RefVerbCall(_, StrLit "suspend_if_needed", _) -> true | _ -> false)

    let p: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None
          Context = { IncludeDeclaration = true } }

    match server.Value.TextDocumentReferences(p) |> Async.RunSynchronously with
    | Ok(Some locations) ->
        Assert.True(locations.Length > 0)
        Assert.Contains(locations, (fun (l: Location) -> l.Uri = uri))
    | other -> Assert.Fail(sprintf "expected at least one Location, got %A" other)

[<Fact>]
let ``references from a this:verb() call site resolves and finds the other this: caller too (regression: these used to only count as unresolved)`` () =
    let objRef, v = findVerb 40L "handle_verb_programmed"
    let uri = uriFor objRef "handle_verb_programmed"
    let stmts = v.Ast |> Option.get

    let position =
        stmts |> positionOf (function RefVerbCall(Ident("this", _, _), StrLit "capture_verb", _) -> true | _ -> false)

    let p: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = position
          WorkDoneToken = None
          PartialResultToken = None
          Context = { IncludeDeclaration = true } }

    match server.Value.TextDocumentReferences(p) |> Async.RunSynchronously with
    | Ok(Some locations) ->
        let importAllUri = uriFor 40L "import_all"
        Assert.Contains(locations, (fun (l: Location) -> l.Uri = importAllUri))
    | other -> Assert.Fail(sprintf "expected at least one Location, got %A" other)

[<Fact>]
let ``references at a position with no VerbCall under it returns Ok None, not an error`` () =
    let objRef, v = findVerb 40L "export_builtins"
    let uri = uriFor objRef "export_builtins"

    let p: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 0u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = { IncludeDeclaration = true } }

    match server.Value.TextDocumentReferences(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None (no VerbCall under cursor), got %A" other)

// ---------------------------------------------------------------------------
// PrepareRename (custom method) - the server-orchestrated rename's own
// resolution pipeline, almost identical to TextDocumentReferences above but
// returning structured RenameCallSite data instead of Location[]. Reuses
// the same `this:capture_verb(...)` fixture the "these used to only count
// as unresolved" references regression test above already exercises.
// ---------------------------------------------------------------------------

[<Fact>]
let ``PrepareRename resolves the verb under the cursor and finds every confirmed call site`` () =
    let objRef, v = findVerb 40L "handle_verb_programmed"
    let uri = uriFor objRef "handle_verb_programmed"
    let stmts = v.Ast |> Option.get

    let position =
        stmts |> positionOf (function RefVerbCall(Ident("this", _, _), StrLit "capture_verb", _) -> true | _ -> false)

    let p: PrepareRenameParams = { TextDocument = { Uri = uri }; Position = position }

    match server.Value.PrepareRename(p) |> Async.RunSynchronously with
    | Ok(Some result) ->
        Assert.Equal(40L, result.ObjRef)
        Assert.Equal("capture_verb", result.VerbName)
        Assert.Contains(result.Sites, (fun s -> s.ObjRef = 40L && s.VerbName = "handle_verb_programmed"))
        Assert.Contains(result.Sites, (fun s -> s.ObjRef = 40L && s.VerbName = "import_all"))
        Assert.Equal(0, result.UnresolvedCount)
    | other -> Assert.Fail(sprintf "expected Ok(Some _), got %A" other)

[<Fact>]
let ``PrepareRename at a position with no VerbCall under it returns Ok None, not an error`` () =
    let objRef, v = findVerb 40L "export_builtins"
    let uri = uriFor objRef "export_builtins"
    let p: PrepareRenameParams = { TextDocument = { Uri = uri }; Position = { Line = 0u; Character = 0u } }

    match server.Value.PrepareRename(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None (no VerbCall under cursor), got %A" other)

// ---------------------------------------------------------------------------
// GetObjectTree (custom, non-LSP-spec method) - replaces the retired
// ListObjects/ListVerbs (Handlers.fs no longer defines either; the unified
// object tree view reads everything from this one call instead).
// ---------------------------------------------------------------------------

[<Fact>]
let ``GetObjectTree includes every object in the graph, not just verb-owners`` () =
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes -> Assert.Equal(graph.Value.Objects.Count, nodes.Length)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree shows a corponym-registered object's real live name plus its $-name`` () =
    // The fixture's "room" object has a real `name:` line ("Generic Room") -
    // the tree should show that, not the bare corponym-derived label, with
    // the corified `[$room]` suffix still appended.
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes ->
        Assert.Contains(nodes, (fun (n: ObjectTreeNode) -> n.Name = "Generic Room (#3) [$room]" && n.ObjRef = 3L))
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree sorts by name`` () =
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes ->
        let names = nodes |> Array.map (fun n -> n.Name)
        Assert.Equal<string[]>(names, Array.sort names)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree gives an object with no parents an empty Parents array`` () =
    // vcs_util (#40) is deliberately parentless in this fixture.
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes ->
        let vcsUtil = nodes |> Array.find (fun n -> n.ObjRef = 40L)
        Assert.Empty(vcsUtil.Parents)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree's parent/child edges agree with each other`` () =
    // room_child (#10) is a direct child of room (#3) in this fixture - the
    // tree's edges should be consistent in both directions, not just
    // individually plausible.
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes ->
        let byRef = nodes |> Array.map (fun n -> n.ObjRef, n) |> Map.ofArray
        Assert.Contains(3L, byRef.[10L].Parents)
        Assert.Contains(10L, byRef.[3L].Children)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree reports vcs_util's own verbs by primary name`` () =
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes ->
        let vcsUtil = nodes |> Array.find (fun n -> n.ObjRef = 40L)
        let verbNames = vcsUtil.Verbs |> Array.map (fun v -> v.Name)
        Assert.Contains("sanitize_name", verbNames)
        Assert.Contains("export_builtins", verbNames)
        Assert.Contains("export_metadata", verbNames)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``GetObjectTree gives an object with no verbs of its own an empty Verbs array, not an error`` () =
    // wizard (#2) is deliberately verb-less in this fixture.
    match server.Value.GetObjectTree(null) |> Async.RunSynchronously with
    | Ok nodes -> Assert.Empty((nodes |> Array.find (fun n -> n.ObjRef = 2L)).Verbs)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on an object's own verb returns itself as the winning definer`` () =
    match
        server.Value.ResolveEffectiveMember({ ObjRef = 3L; Kind = "verb"; Name = "say" })
        |> Async.RunSynchronously
    with
    | Ok(Some definer) -> Assert.Equal(3L, definer)
    | other -> Assert.Fail(sprintf "expected Ok(Some 3L), got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on a shadowed verb resolves to the child's own copy, not the ancestor's`` () =
    // room_child (#10) shadows room (#3)'s own "say" (see the fixture's own
    // comment) - resolving from #10 must return #10, not #3, proving the
    // winning definer (not just "some definer in the chain") is what's
    // returned.
    match
        server.Value.ResolveEffectiveMember({ ObjRef = 10L; Kind = "verb"; Name = "say" })
        |> Async.RunSynchronously
    with
    | Ok(Some definer) -> Assert.Equal(10L, definer)
    | other -> Assert.Fail(sprintf "expected Ok(Some 10L), got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on a plain (not shadowed) inherited property resolves to the ancestor that defines it`` () =
    match
        server.Value.ResolveEffectiveMember(
            { ObjRef = 10L
              Kind = "property"
              Name = "description" }
        )
        |> Async.RunSynchronously
    with
    | Ok(Some definer) -> Assert.Equal(3L, definer)
    | other -> Assert.Fail(sprintf "expected Ok(Some 3L), got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on a property inherited from an ancestor resolves to the true owner, not the query object`` () =
    match
        server.Value.ResolveEffectiveMember(
            { ObjRef = 40L
              Kind = "property"
              Name = "repo_root" }
        )
        |> Async.RunSynchronously
    with
    | Ok(Some definer) -> Assert.Equal(40L, definer)
    | other -> Assert.Fail(sprintf "expected Ok(Some 40L), got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on a name that doesn't exist anywhere in the chain returns None, not an error`` () =
    match
        server.Value.ResolveEffectiveMember(
            { ObjRef = 10L
              Kind = "verb"
              Name = "does_not_exist" }
        )
        |> Async.RunSynchronously
    with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

[<Fact>]
let ``ResolveEffectiveMember on an unrecognized kind returns None rather than throwing`` () =
    match
        server.Value.ResolveEffectiveMember({ ObjRef = 3L; Kind = "bogus"; Name = "say" })
        |> Async.RunSynchronously
    with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)
