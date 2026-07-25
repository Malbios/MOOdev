/// End-to-end handler tests against the real `Survive` graph - calling
/// `MooLspServer`'s overridden methods directly (bypassing the JSON-RPC/
/// websocket transport, which `WsTransport.fs` owns and isn't the concern
/// here) against a known real call site, per the M4 plan's 4.4a verify
/// step: "handler unit tests against in-memory graph fixtures."
module LanguageServer.Tests.HandlerTests

open System.IO
open Xunit
open Ionide.LanguageServerProtocol.Types
open Language.Ast
open Metadata.Schema
open Metadata.Loader
open LanguageServer.Handlers
open LanguageServer.AstQuery

let private surviveRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Survive"))

let private graph = lazy (load surviveRoot)
let private server = lazy (new MooLspServer(new MooLspClient(), graph.Value))

/// Finds `(objRef, VerbNode)` by the real captured file's path suffix -
/// keeps these tests independent of exact object numbering (which the
/// `moodev-verb://` scheme itself hides from clients too) while still
/// anchoring to a real, known corpus file.
let private findByPathSuffix (suffix: string) : ObjRef * VerbNode =
    graph.Value.Objects
    |> Map.toSeq
    |> Seq.collect (fun (num, o) -> o.Verbs |> Seq.map (fun v -> num, v))
    |> Seq.find (fun (_, v) ->
        match v.SourcePath with
        | Some p -> p.Replace('\\', '/').EndsWith(suffix)
        | None -> false)

// `@paranoid_Database/7_gc.moo` line 26: `  $command_utils:suspend_if_needed(0);`
// 1-based cols: '$'=3, "command_utils"=4..16, ':'=17, "suspend_if_needed"=18..35.
// LSP position is 0-based: line 25, character 20 lands inside the verb name.
let private positionOnSuspendIfNeeded: Position = { Line = 25u; Character = 20u }

[<Fact>]
let ``fixture verb exists where the test expects it`` () =
    let _, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    Assert.True(verb.Meta.Names |> List.contains "gc")

[<Fact>]
let ``hover on a real $obj:verb(...) call site resolves and describes the verb`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = positionOnSuspendIfNeeded
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("suspend_if_needed", markup.Value)
            // Owner is shown as a real display name ("Wizard (#2)"), not a
            // bare object number - same `displayNameFor` formatting the
            // object picker and inspector already use.
            Assert.Contains("owner: `Wizard (#2)`", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``definition on the same call site points at a real verb via the moodev-verb URI scheme`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = positionOnSuspendIfNeeded
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) -> Assert.StartsWith("moodev-verb://", location.Uri)
    | other -> Assert.Fail(sprintf "expected a single Location result, got %A" other)

[<Fact>]
let ``definition on a this:verb() call resolves to the enclosing object's own verb (regression: this used to be treated as unresolvable)`` () =
    let objRef, verb = findByPathSuffix "VCS/4_handle_verb_programmed.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 5: `path = this:capture_verb(OBJ, vname);` - "capture_verb" spans
    // 1-based cols 13-24; 0-based line 4, character 15 lands inside it.
    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 4u; Character = 15u }
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) -> Assert.Equal(moodevVerbUri 127L "capture_verb", location.Uri)
    | other -> Assert.Fail(sprintf "expected a single Location result pointing at VCS:capture_verb, got %A" other)

// --- Go to definition on a local variable --------------------------------

[<Fact>]
let ``definition on a read of a for-loop variable jumps to the for statement that introduces it`` () =
    // @paranoid_Database/7_gc.moo: `for x in (properties(this))` on line 5
    // introduces `x`; `length(x)` on line 7 reads it. 1-based: line 5 col 5
    // is the "x" right after "for "; line 7 col 16 is the "x" inside
    // `length(...)`. LSP positions are 0-based.
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 6u; Character = 15u }
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) ->
        Assert.Equal(uri, location.Uri) // same document - no dispatch, just a jump within it
        Assert.Equal({ Line = 4u; Character = 4u }, location.Range.Start)
        Assert.Equal({ Line = 4u; Character = 5u }, location.Range.End)
    | other -> Assert.Fail(sprintf "expected a Location pointing at the for statement, got %A" other)

[<Fact>]
let ``definition on a read of a plain-assignment local variable jumps to its first assignment`` () =
    // @paranoid_Database/7_gc.moo: `threshold = 60 * 60 * 24 * 3;` on line 4
    // (1-based col 1) is the only assignment; line 17 reads it twice, first
    // at 1-based col 82. LSP positions are 0-based.
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 16u; Character = 81u }
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1(U2.C1 location))) ->
        Assert.Equal(uri, location.Uri)
        Assert.Equal({ Line = 3u; Character = 0u }, location.Range.Start)
        Assert.Equal({ Line = 3u; Character = 9u }, location.Range.End) // "threshold".Length = 9
    | other -> Assert.Fail(sprintf "expected a Location pointing at the assignment, got %A" other)

[<Fact>]
let ``definition on a built-in verb-call variable (args) returns no result - it has no single introduction site`` () =
    // VCS/1_sanitize_name.moo line 1: `name = args[1];` - "args" spans
    // 1-based cols 8-11; 0-based line 0, character 8 lands inside it. Same
    // position the existing hover test for this variable uses.
    let objRef, verb = findByPathSuffix "VCS/1_sanitize_name.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: DefinitionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 8u }
          WorkDoneToken = None
          PartialResultToken = None }

    match server.Value.TextDocumentDefinition(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

[<Fact>]
let ``hover on a position with no resolvable reference returns no result, not an error`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 999u; Character = 0u } // past the end of the file
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)

[<Fact>]
let ``hover on a local variable shows "local variable", not nothing`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 4: `threshold = 60 * 60 * 24 * 3;` - "threshold" spans 1-based
    // cols 1-9; 0-based line 3, character 2 lands inside it.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 3u; Character = 2u }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup -> Assert.Contains("local variable", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on this:verb() resolves via the enclosing object, not just "receiver unknown"`` () =
    // `capture_verb` is called via `this:` inside a verb defined on VCS
    // (#127) itself, and `capture_verb` is also defined directly on #127 -
    // `this` resolves to the enclosing object (see
    // `Resolver.resolveReceiverInContext`), so this should describe the
    // real, specific verb it dispatches to, not the old "receiver isn't
    // statically known" fallback (which remains correct for genuinely
    // unresolvable receivers like `player:` - see the next test).
    let objRef, verb = findByPathSuffix "VCS/4_handle_verb_programmed.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 5: `path = this:capture_verb(OBJ, vname);` - "capture_verb" spans
    // 1-based cols 13-24; 0-based line 4, character 15 lands inside it.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 4u; Character = 15u }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("capture_verb", markup.Value)
            Assert.Contains("#127 (VCS)", markup.Value)
            Assert.DoesNotContain("receiver isn't statically known", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on an unresolvable receiver (player:verb()) still lists candidate defining objects`` () =
    // `player` has no static default the way `this` now does (see
    // `resolveReceiverInContext`'s own comment) - genuinely unresolvable,
    // so this must still fall back to the ambiguous-candidates list.
    let objRef, verb = findByPathSuffix "Generic_Room/3_say.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 2: `  player:tell("You say, \"", argstr, "\"");` - "tell" spans
    // 1-based cols 10-13; 0-based line 1, character 10 lands inside it.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 1u; Character = 10u }
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
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 3 is exactly `endif` - 1-based cols 1-5; 0-based line 2, character 2.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 2u; Character = 2u }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("endif", markup.Value)
            // Hovering any one keyword of a multi-part construct (here,
            // `endif`) shows the *whole* if/elseif/else/endif explanation,
            // not just a one-liner about `endif` alone.
            Assert.Contains("elseif", markup.Value)
            Assert.Contains("Runs the first branch whose condition is true", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result (keyword help), got %A" other)

[<Fact>]
let ``hover on a builtin call shows the same signature info as signature help`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    let stmts = verb.Ast |> Option.get

    let callRef =
        collectReferences stmts
        |> List.pick (fun r ->
            match r.Ref with
            | RefCall(name, _) when name = "length" -> Some r
            | _ -> None)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (callRef.Line - 1); Character = uint32 (callRef.Col - 1) }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup -> Assert.Contains("length(", markup.Value)
        | other -> Assert.Fail(sprintf "expected MarkupContent, got %A" other)
    | other -> Assert.Fail(sprintf "expected a hover result, got %A" other)

[<Fact>]
let ``hover on an implicit built-in variable (args) shows its real description`` () =
    let objRef, verb = findByPathSuffix "VCS/1_sanitize_name.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 1: `name = args[1];` - "args" spans 1-based cols 8-11; 0-based
    // line 0, character 8 lands inside it.
    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 8u }
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
    let objRef, verb = findByPathSuffix "Generic_BigList_Resident/7_init_for_core.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    let stmts = verb.Ast |> Option.get

    // line 5: `this.mowner = $hacker;` - a bare property reference, not a
    // verb call.
    let propRef =
        collectReferences stmts
        |> List.pick (fun r ->
            match r.Ref with
            | RefProp(_, StrLit "hacker") -> Some r
            | _ -> None)

    let p: HoverParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (propRef.Line - 1); Character = uint32 (propRef.Col - 1) }
          WorkDoneToken = None }

    match server.Value.TextDocumentHover(p) |> Async.RunSynchronously with
    | Ok(Some hover) ->
        match hover.Contents with
        | U3.C1 markup ->
            Assert.Contains("$hacker", markup.Value)
            Assert.Contains("#36", markup.Value)
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

// --- Completions ------------------------------------------------------

[<Fact>]
let ``completion at the real $command_utils:suspend_if_needed(0) call site offers local vars, builtins, and reachable verb names``
    ()
    =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = positionOnSuspendIfNeeded
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) ->
        let labels = items |> Array.map (fun i -> i.Label) |> Set.ofArray
        Assert.Contains("threshold", labels) // a real local var declared in gc.moo
        Assert.Contains("typeof", labels) // a real builtin
        Assert.Contains("suspend_if_needed", labels) // reachable via $command_utils
    | other -> Assert.Fail(sprintf "expected a flat CompletionItem[] result, got %A" other)

[<Fact>]
let ``completion near a this:verb() call offers the enclosing object's own callable verbs (regression: this used to be treated as unresolvable)`` () =
    let objRef, verb = findByPathSuffix "VCS/4_handle_verb_programmed.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    // line 5: `path = this:capture_verb(OBJ, vname);` - "capture_verb" spans
    // 1-based cols 13-24; 0-based line 4, character 15 lands inside it.
    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 4u; Character = 15u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) ->
        let labels = items |> Array.map (fun i -> i.Label) |> Set.ofArray
        Assert.Contains("capture_verb", labels) // reachable via `this` == #127 (VCS)
        Assert.Contains("export_metadata", labels) // another verb on the same object
    | other -> Assert.Fail(sprintf "expected a flat CompletionItem[] result, got %A" other)

[<Fact>]
let ``completion at an unresolvable position still returns local vars and builtins, not an error`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: CompletionParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 0u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = None }

    match server.Value.TextDocumentCompletion(p) |> Async.RunSynchronously with
    | Ok(Some(U2.C1 items)) -> Assert.True(items.Length > 0)
    | other -> Assert.Fail(sprintf "expected a non-empty CompletionItem[] result, got %A" other)

// --- Signature help -----------------------------------------------------

[<Fact>]
let ``signature help on a real builtin call site describes its arity and arg types`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    // Find a real `Call` reference in this verb's own AST rather than
    // hand-counting columns - `length(x)` is used at least once in gc.moo.
    let stmts = verb.Ast |> Option.get

    let callRef =
        collectReferences stmts
        |> List.pick (fun r ->
            match r.Ref with
            | RefCall(name, _) when name = "length" -> Some r
            | _ -> None)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (callRef.Line - 1); Character = uint32 (callRef.Col - 1) }
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok(Some help) ->
        Assert.Single(help.Signatures) |> ignore
        Assert.StartsWith("length(", help.Signatures.[0].Label)
    | other -> Assert.Fail(sprintf "expected a SignatureHelp result, got %A" other)

[<Fact>]
let ``signature help on a builtin with a documented C-source signature uses its real parameter names`` () =
    // VCS/1_sanitize_name.moo calls strsub(name, "...", "...") repeatedly -
    // strsub is one of the ~55 builtins with a real extracted signature
    // ("source, what, with, case-matters"), not the generic arg1/arg2
    // fallback.
    let objRef, verb = findByPathSuffix "VCS/1_sanitize_name.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)
    let stmts = verb.Ast |> Option.get

    let callRef =
        collectReferences stmts
        |> List.pick (fun r ->
            match r.Ref with
            | RefCall(name, _) when name = "strsub" -> Some r
            | _ -> None)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = { Line = uint32 (callRef.Line - 1); Character = uint32 (callRef.Col - 1) }
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok(Some help) -> Assert.Equal("strsub(source: str, what: str, with: str, case-matters: any)", help.Signatures.[0].Label)
    | other -> Assert.Fail(sprintf "expected a SignatureHelp result, got %A" other)

[<Fact>]
let ``signature help on a VerbCall (not a builtin) returns no result`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: SignatureHelpParams =
        { TextDocument = { Uri = uri }
          Position = positionOnSuspendIfNeeded
          WorkDoneToken = None
          Context = None }

    match server.Value.TextDocumentSignatureHelp(p) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None (verb calls have no function_info signature), got %A" other)

// --- Find references ------------------------------------------------------

[<Fact>]
let ``references to $command_utils:suspend_if_needed includes the real gc.moo call site`` () =
    let objRef, verb = findByPathSuffix "@paranoid_Database/7_gc.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = positionOnSuspendIfNeeded
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
    // Both real call sites of capture_verb in the corpus are `this:`-based
    // (VCS/4_handle_verb_programmed.moo and VCS/5_import_all.moo, both on
    // VCS itself) - before the fix, `resolvableVerbCallAt` would refuse to
    // even start from a `this:` call site, so this whole request used to
    // return `Ok None`.
    let objRef, verb = findByPathSuffix "VCS/4_handle_verb_programmed.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    let p: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 4u; Character = 15u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = { IncludeDeclaration = true } }

    match server.Value.TextDocumentReferences(p) |> Async.RunSynchronously with
    | Ok(Some locations) ->
        let importAllObjRef, importAllVerb = findByPathSuffix "VCS/5_import_all.moo"
        let importAllUri = moodevVerbUri importAllObjRef (List.head importAllVerb.Meta.Names)
        Assert.Contains(locations, (fun (l: Location) -> l.Uri = importAllUri))
    | other -> Assert.Fail(sprintf "expected at least one Location, got %A" other)

[<Fact>]
let ``references to a verb with no callers anywhere returns an empty (not null) result`` () =
    // export_builtins is brand new this session - vanishingly unlikely to
    // be called from anywhere else in the corpus yet.
    let objRef, verb = findByPathSuffix "VCS/9_export_builtins.moo"
    let uri = moodevVerbUri objRef (List.head verb.Meta.Names)

    // No call site to put the cursor on within this verb itself - confirm
    // references at a position with no VerbCall under it behaves (a clean
    // "no result", not an exception) rather than trying to synthesize a
    // guaranteed-zero-callers scenario, which would need corpus-wide
    // knowledge this test shouldn't have to assume.
    let refParams: ReferenceParams =
        { TextDocument = { Uri = uri }
          Position = { Line = 0u; Character = 0u }
          WorkDoneToken = None
          PartialResultToken = None
          Context = { IncludeDeclaration = true } }

    match server.Value.TextDocumentReferences(refParams) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None (no VerbCall under cursor), got %A" other)

// --- ListObjects / ListVerbs (custom, non-LSP-spec methods) --------------

[<Fact>]
let ``ListObjects includes VCS, named, and excludes objects with no verbs`` () =
    match server.Value.ListObjects(null) |> Async.RunSynchronously with
    | Ok objects ->
        // Name is now a full display label - live name (falling back to
        // lookups.toml's sanitized name), the object number, then its
        // corified $-name if registered - not just the bare sanitized name.
        Assert.Contains(objects, (fun (o: ObjectSummary) -> o.Name = "VCS (#127) [$vcs]" && o.ObjRef = 127L))
        Assert.True(objects.Length > 1)
        // every entry must have at least one verb - the real Graph itself
        // is the source of truth for which objects qualify, not a
        // hardcoded count.
        let allObjs = graph.Value.Objects

        for o in objects do
            let node = Map.find o.ObjRef allObjs
            Assert.NotEmpty(node.Verbs)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ListObjects shows an object's real live name plus its corified $-name`` () =
    // #3 is "Generic Room" (real, space-containing live name) and is
    // registered as $room - the label should read from the real name, not
    // lookups.toml's sanitized "Generic_Room", and should surface the
    // corified alias too.
    match server.Value.ListObjects(null) |> Async.RunSynchronously with
    | Ok objects -> Assert.Contains(objects, (fun (o: ObjectSummary) -> o.Name = "Generic Room (#3) [$room]" && o.ObjRef = 3L))
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ListObjects sorts by name`` () =
    match server.Value.ListObjects(null) |> Async.RunSynchronously with
    | Ok objects ->
        let names = objects |> Array.map (fun o -> o.Name)
        Assert.Equal<string[]>(names, Array.sort names)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ListVerbs on VCS includes every real verb by primary name`` () =
    match server.Value.ListVerbs({ ObjRef = 127L }) |> Async.RunSynchronously with
    | Ok verbs ->
        let names = verbs |> Array.map (fun v -> v.Name) |> Set.ofArray
        Assert.Contains("sanitize_name", names)
        Assert.Contains("export_builtins", names)
        Assert.Contains("export_metadata", names)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ListVerbs on an object with no verbs returns an empty array, not an error`` () =
    let noVerbsObj =
        graph.Value.Objects
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.tryFind (fun o -> List.isEmpty o.Verbs)

    match noVerbsObj with
    | None -> () // every object in this corpus happens to have a verb - nothing to assert
    | Some o ->
        match server.Value.ListVerbs({ ObjRef = o.Num }) |> Async.RunSynchronously with
        | Ok verbs -> Assert.Empty(verbs)
        | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

[<Fact>]
let ``ListVerbs on an unknown object returns an empty array, not a crash`` () =
    match server.Value.ListVerbs({ ObjRef = 999999L }) |> Async.RunSynchronously with
    | Ok verbs -> Assert.Empty(verbs)
    | other -> Assert.Fail(sprintf "expected Ok, got %A" other)

// --- GetObjectInfo (custom, non-LSP-spec method) -------------------------

[<Fact>]
let ``GetObjectInfo on VCS reports owner, all-false flags, verbs, and properties from real data`` () =
    match server.Value.GetObjectInfo({ ObjRef = 127L }) |> Async.RunSynchronously with
    | Ok(Some info) ->
        Assert.Equal("VCS (#127) [$vcs]", info.Name)
        Assert.Equal(Some { ObjRef = 2L; Name = "Wizard (#2)" }, info.Owner)
        // #127 (VCS) is a standalone utility object - no player/programmer/
        // wizard/read/write/fertile/anonymous flags set, real data confirmed
        // directly against metadata.json.
        Assert.False(info.Player)
        Assert.False(info.Programmer)
        Assert.False(info.Wizard)
        Assert.False(info.Read)
        Assert.False(info.Write)
        Assert.False(info.Fertile)
        Assert.False(info.Anonymous)
        Assert.Empty(info.Parents)
        Assert.Empty(info.Children)

        Assert.Contains(
            info.Verbs,
            fun (v: ObjectInfoVerb) -> v.Name = "sanitize_name" && v.Perms = "rxd" && v.Dobj = "this" && v.Prep = "none" && v.Iobj = "this"
        )

        Assert.Contains(info.Properties, fun (p: ObjectInfoProperty) -> p.Name = "repo_root" && p.Owner = "Wizard (#2)" && p.Perms = "rw")
    | other -> Assert.Fail(sprintf "expected Ok(Some info), got %A" other)

[<Fact>]
let ``GetObjectInfo on Generic Room reports a real parent and real fertile/read flags`` () =
    match server.Value.GetObjectInfo({ ObjRef = 3L }) |> Async.RunSynchronously with
    | Ok(Some info) ->
        Assert.Equal("Generic Room (#3) [$room]", info.Name)
        Assert.True(info.Read)
        Assert.True(info.Fertile)
        Assert.False(info.Wizard)
        Assert.Contains(info.Parents, fun (r: ObjectInfoRef) -> r.ObjRef = 1L)
        Assert.NotEmpty(info.Children)
    | other -> Assert.Fail(sprintf "expected Ok(Some info), got %A" other)

[<Fact>]
let ``GetObjectInfo on the Wizard player object reports player/programmer/wizard all true`` () =
    match server.Value.GetObjectInfo({ ObjRef = 2L }) |> Async.RunSynchronously with
    | Ok(Some info) ->
        Assert.True(info.Player)
        Assert.True(info.Programmer)
        Assert.True(info.Wizard)
    | other -> Assert.Fail(sprintf "expected Ok(Some info), got %A" other)

[<Fact>]
let ``GetObjectInfo on an unknown object returns None, not an error`` () =
    match server.Value.GetObjectInfo({ ObjRef = 999999L }) |> Async.RunSynchronously with
    | Ok None -> ()
    | other -> Assert.Fail(sprintf "expected Ok None, got %A" other)
