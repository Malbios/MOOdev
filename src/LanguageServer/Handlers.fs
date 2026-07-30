/// The LSP server itself: go-to-definition and hover, both built directly
/// on the Phase 4.3c resolver (`Metadata.Resolver.findCallableVerb`) - the
/// milestone's stated goal for this phase. Scoped deliberately to `$name`/
/// `#N`-receiver verb calls (`VerbCall` AST nodes with a statically
/// resolvable receiver and a literal name) - anything else
/// (`this:foo()`/`player:foo()`, a computed name, a bare `Ident`/`Call`/
/// `Prop` reference) isn't something the verb-dispatch resolver can answer,
/// and returns no result rather than a guess.
module LanguageServer.Handlers

open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Language.Ast
open Metadata.Schema

/// Custom, non-LSP-spec request/result shapes for populating the editor's
/// object tree - `WsTransport.fs` registers these alongside the standard
/// handlers via `Server.serverRequestHandling`, the same escape hatch the
/// plan's `moodev-caveat://` URI extension already uses for "this host
/// needs something the base spec doesn't have a slot for."
///
/// One node of the full object tree, sent in one shot at login rather than
/// per-click - the whole graph is small (a personal MOO's database), and
/// the client's filter needs to search arbitrarily deep with no latency,
/// so there's nothing to gain from a lazier per-object fetch. Flat + edges
/// rather than a nested JSON shape - the object graph is a DAG (see
/// "Known hazards" in the project plan), so a node can need to appear
/// under more than one parent; the client rebuilds parent-to-children
/// adjacency itself from these edges rather than this server picking one
/// parent arbitrarily.
/// Structural summary of one verb for the tree's compact perms/args suffix.
type ObjectTreeVerb =
    { Name: string
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string }

/// Same idea as `ObjectTreeVerb`, for properties.
type ObjectTreeProperty = { Name: string; Perms: string }

/// One verb `FindDeadVerbs` found no confirmed reference to.
/// `PossiblyDynamic` carries forward the exact same "unresolvable call site
/// with a matching literal name" caveat `TextDocumentReferences`'s
/// `moodev-caveat://` entry already surfaces for a single search - applied
/// per-verb here instead of per-search, since a verb only reachable via
/// `this:(name)()`/computed dispatch isn't safely "dead", just unconfirmed.
type DeadVerbEntry =
    { ObjRef: ObjRef
      VerbName: string
      PossiblyDynamic: bool }

type ObjectTreeNode =
    { ObjRef: ObjRef
      Name: string
      Parents: ObjRef[]
      Children: ObjRef[]
      Verbs: ObjectTreeVerb[]
      Properties: ObjectTreeProperty[] }

/// The browser client never has a real filesystem path - it only ever
/// knows "object # + verb name" (the same pair `$vcs:ide_fetch`/`ide_save`
/// already key off of), so document identity here is a custom
/// `moodev-verb://<objRef>/<verbName>` URI rather than a `file://` one.
/// Symmetric in both directions: the client builds these to say "here's
/// what's open", and go-to-definition results use the same shape so the
/// client can jump to a different verb via the same `ide_fetch` flow it
/// already has, without the server ever exposing a disk path to the
/// browser.
let moodevVerbUri (objRef: ObjRef) (verbName: string) : string =
    sprintf "moodev-verb://%d/%s" objRef (System.Uri.EscapeDataString verbName)

let private tryParseMoodevVerbUri (uri: string) : (ObjRef * string) option =
    try
        let parsed = System.Uri(uri)

        if parsed.Scheme = "moodev-verb" then
            let objRef = int64 parsed.Host
            let verbName = System.Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'))
            Some(objRef, verbName)
        else
            None
    with _ ->
        None

/// The verb identified by a `moodev-verb://` URI - an *exact* name match
/// against the object's own verb list (the client always asks for the
/// specific verb it has open, never a wildcard pattern), unlike
/// `findCallableVerb`'s dispatch-time `verbNameMatchesAny`.
let private verbAtUri (graph: Graph) (uri: string) : (ObjRef * VerbNode) option =
    tryParseMoodevVerbUri uri
    |> Option.bind (fun (objRef, verbName) ->
        graph.Objects
        |> Map.tryFind objRef
        |> Option.bind (fun o -> o.Verbs |> List.tryFind (fun v -> v.Meta.Names |> List.contains verbName))
        |> Option.map (fun v -> objRef, v))

/// The `VerbCall` under the cursor, if the position lands on one and its
/// receiver resolves to a real object - the one reference shape this
/// phase's resolver can actually answer. `enclosingObj` (the object the
/// verb being viewed is defined on) lets a bare `this` receiver resolve too,
/// via `resolveReceiverInContext` - see that function's own comment for why
/// that's sound.
let private resolvableVerbCallAt
    (graph: Graph)
    (enclosingObj: ObjRef)
    (stmts: Stmt list)
    (lspLine: int)
    (lspCol: int)
    : (ObjRef * string * Arg list) option =
    // LSP positions are 0-based; AST positions (from Lexer.fs) are 1-based.
    match AstQuery.referenceAt (lspLine + 1) (lspCol + 1) stmts with
    | Some { Ref = AstQuery.RefVerbCall(receiver, StrLit verbName, args) } ->
        Metadata.Resolver.resolveReceiverInContext graph enclosingObj receiver
        |> Option.map (fun startObj -> startObj, verbName, args)
    | _ -> None


/// Every declared field defaulted to `None`/absent - `CompletionItem` has
/// 19 fields and only `Label` is ever meaningfully populated here.
let private mkCompletionItem (label: string) (kind: CompletionItemKind) : CompletionItem =
    { Label = label
      LabelDetails = None
      Kind = Some kind
      Tags = None
      Detail = None
      Documentation = None
      Deprecated = None
      Preselect = None
      SortText = None
      FilterText = None
      InsertText = None
      InsertTextFormat = None
      InsertTextMode = None
      TextEdit = None
      TextEditText = None
      AdditionalTextEdits = None
      CommitCharacters = None
      Command = None
      Data = None }

/// `function_info()`'s per-argument type codes, masked back to their
/// "user-visible" base values (`structures.h:99-122`, `functions.cc:478-483`)
/// - best-effort names for signature help, not exhaustive (the handful that
/// never appear in a builtin's declared prototype - `TYPE_CLEAR`/`TYPE_NONE`/
/// `TYPE_CATCH`/`TYPE_FINALLY`/`TYPE_ITER` - aren't mapped).
let private typeName (code: int) : string =
    match code with
    | 0 -> "int"
    | 1 -> "obj"
    | 2 -> "str"
    | 3 -> "err"
    | 4 -> "list"
    | 9 -> "float"
    | 10 -> "map"
    | 12 -> "anon"
    | 13 -> "waif"
    | 14 -> "bool"
    | -1 -> "any"
    | -2 -> "number"
    | other -> sprintf "?%d" other

/// Real name when the C source documented one (`fn.ParamNames`), else a
/// generic `argN` fallback - shared by hover and signature help so both
/// describe a builtin identically.
let private builtinParamLabel (fn: BuiltinFunc) (i: int) (t: int) : string =
    match fn.ParamNames |> Option.bind (List.tryItem i) with
    | Some name -> sprintf "%s: %s" name (typeName t)
    | None -> sprintf "arg%d: %s" (i + 1) (typeName t)

let private builtinSignatureLabel (fn: BuiltinFunc) : string =
    let paramLabels = fn.ArgTypes |> List.mapi (builtinParamLabel fn)
    sprintf "%s(%s)" fn.Name (String.concat ", " paramLabels)

/// Full hover body for a builtin call: signature, then either its real
/// one-line description (`fn.Description`, hand-written - see
/// `Metadata.fsproj`'s `builtin-descriptions.json`) or a bare "Built-in
/// function." fallback for the rare case a name isn't in that file.
let private builtinHoverText (fn: BuiltinFunc) : string =
    match fn.Description with
    | Some desc -> sprintf "**%s**\n\n%s" (builtinSignatureLabel fn) desc
    | None -> sprintf "**%s**\n\nBuilt-in function." (builtinSignatureLabel fn)

let private mkHover (text: string) : Hover =
    { Contents = U3.C1 { Kind = MarkupKind.Markdown; Value = text }
      Range = None }

let private definerName (graph: Graph) (definer: ObjRef) : string =
    graph.Objects
    |> Map.tryFind definer
    |> Option.bind (fun o -> o.Name)
    |> Option.defaultValue (sprintf "#%d" definer)

/// A live display name, falling back to bare `#N` when the live query
/// didn't have one (e.g. a genuinely nameless object) - simpler than
/// `displayNameFor` above (no `[$corponym]` suffix, since that needs the
/// corponym registry too - a known, deliberate gap for this first live
/// version, not a bug).
let private liveDisplayName (name: string) (objRef: ObjRef) : string =
    if name = "" then sprintf "#%d" objRef else sprintf "%s (#%d)" name objRef

/// Hover body for a `VerbCall` resolved via `SidecarBridge.ResolveVerbDispatch`
/// - the live equivalent of the old `hoverForResolvedVerb`, which took a
/// static-graph `VerbNode` this project no longer builds for the resolved
/// verb (see `Handlers.MooLspServer`'s `TextDocumentHover`/`TextDocumentDefinition`).
let private hoverForResolvedVerbLive (verbName: string) (result: SidecarBridge.VerbDispatchResult) : Hover =
    sprintf
        "**%s** on `#%d (%s)`\n\nnames: `%s`  \nargs: `%s %s %s`  \nperms: `%s`  \nowner: `%s`"
        verbName
        result.Definer
        (if result.DefinerName = "" then sprintf "#%d" result.Definer else result.DefinerName)
        result.Names
        result.Dobj
        result.Prep
        result.Iobj
        result.Perms
        (liveDisplayName result.OwnerName result.Owner)
    |> mkHover

/// `result.Names`'s primary (first-declared) name is what the client
/// re-requests through the normal `ide_fetch` flow to actually jump there -
/// not necessarily the alias the caller happened to use.
let private locationOfVerbLive (result: SidecarBridge.VerbDispatchResult) : Location =
    { Uri = moodevVerbUri result.Definer (result.Names.Split(' ') |> Array.head)
      // No per-statement spans exist yet for the verb body itself (only
      // expression-level reference nodes carry positions) - land at the
      // top of the file rather than guess a line.
      Range =
        { Start = { Line = 0u; Character = 0u }
          End = { Line = 0u; Character = 0u } } }

/// Reverse of `Graph.SystemObjectProperties` (`$name` -> obj) - if a real
/// object happens to be registered under more than one `$name`, this
/// arbitrarily keeps one; rare enough in practice not to matter for a
/// display label.
let private corifiedNamesOf (graph: Graph) : Map<ObjRef, string> =
    graph.SystemObjectProperties
    |> Map.toSeq
    |> Seq.map (fun (name, objRef) -> objRef, name)
    |> Map.ofSeq

/// Full display label for an object number - the real (unsanitized) live
/// name (falling back to `lookups.toml`'s sanitized name, then bare `#N`),
/// the object number, and its corified `$name` suffix if `#0` registers
/// one, e.g. "Generic Room (#3) [$room]". Falls back to bare `#N` for a ref
/// with no `ObjectNode` at all (e.g. an owner outside the loaded graph).
let private displayNameFor (graph: Graph) (objRef: ObjRef) : string =
    match Map.tryFind objRef graph.Objects with
    | None -> sprintf "#%d" objRef
    | Some o ->
        let baseName =
            o.LiveName |> Option.orElse o.Name |> Option.defaultValue (sprintf "#%d" o.Num)

        match Map.tryFind o.Num (corifiedNamesOf graph) with
        | Some propName -> sprintf "%s (#%d) [$%s]" baseName o.Num propName
        | None -> sprintf "%s (#%d)" baseName o.Num

/// Descriptions for MOOcode's 19 control keywords. Four of these are really
/// one multi-part construct spelled across several keywords (`if`/`elseif`/
/// `else`/`endif`, `for`/`in`/`endfor`, `while`/`endwhile`, `fork`/
/// `endfork`, `try`/`except`/`finally`/`endtry`) - hovering *any* keyword in
/// one of these families shows the identical full explanation of how the
/// whole construct works, not just a one-line description of that single
/// piece, since understanding e.g. `except` in isolation requires already
/// knowing how `try`/`finally`/`endtry` fit together anyway.
let private ifFamilyHelp =
    "`if (cond) ... [elseif (cond) ...] [else ...] endif`\n\n\
     Runs the first branch whose condition is true, checked in order: `if`'s \
     condition, then each `elseif`'s condition in turn, falling through to \
     `else` (if present) when none matched. Any number of `elseif` arms are \
     allowed; `else` is optional and must come last."

let private forFamilyHelp =
    "`for x in (list-or-map) ... endfor`, `for x, i in (...) ... endfor`, or \
     `for x in [a..b] ... endfor`\n\n\
     Loops over a list, map, or integer range. With one loop variable, `x` \
     is each element in turn (or each value, for a map); with two (`x, i`), \
     `i` is also bound to the element's index (or key, for a map). The \
     `[a..b]` range form counts from `a` to `b` inclusive without building \
     a list."

let private whileFamilyHelp =
    "`while (cond) ... endwhile` or `while name (cond) ... endwhile`\n\n\
     Repeats the body for as long as `cond` is true, checked before each \
     iteration. Naming the loop (`while name (...)`) lets `break name;`/\
     `continue name;` target it specifically from inside a nested loop."

let private forkFamilyHelp =
    "`fork (delay) ... endfork` or `fork name (delay) ... endfork`\n\n\
     Schedules the body to run as a separate, independent task after \
     `delay` seconds, then continues immediately past the `endfork` in the \
     current task. Naming the fork (`fork name (...)`) binds `name` to the \
     new task's id in the current task, so it can be cancelled later with \
     `kill_task(name)`."

let private tryFamilyHelp =
    "`try ... except name (codes) ... endtry` or `try ... finally ... endtry`\n\n\
     Runs the body; a `try` has one-or-more `except` arms **or** exactly \
     one `finally`, never both. Each `except` arm catches errors whose code \
     matches `codes` (a list of `ERR_*` values, or `any` for all of them), \
     binding `name` to the 4-element `{code, message, value, call-stack}` \
     error info and running only that one arm. `finally`'s body always runs \
     afterward instead - whether the `try` body succeeded, raised an error, \
     or hit `return` - and cannot itself suppress an error or return value \
     passing through it."

let private keywordHelp (k: Language.Lexer.Keyword) : string =
    match k with
    | Language.Lexer.Keyword.If
    | Language.Lexer.Keyword.ElseIf
    | Language.Lexer.Keyword.Else
    | Language.Lexer.Keyword.EndIf -> ifFamilyHelp
    | Language.Lexer.Keyword.For
    | Language.Lexer.Keyword.In
    | Language.Lexer.Keyword.EndFor -> forFamilyHelp
    | Language.Lexer.Keyword.While
    | Language.Lexer.Keyword.EndWhile -> whileFamilyHelp
    | Language.Lexer.Keyword.Fork
    | Language.Lexer.Keyword.EndFork -> forkFamilyHelp
    | Language.Lexer.Keyword.Try
    | Language.Lexer.Keyword.Except
    | Language.Lexer.Keyword.Finally
    | Language.Lexer.Keyword.EndTry -> tryFamilyHelp
    | Language.Lexer.Keyword.Return -> "`return [expr];` - ends the current verb call, optionally with a value."
    | Language.Lexer.Keyword.Any ->
        "`any` - a wildcard matching every error code, used in an `except` clause's `codes` (`except id (any)`) or an inline catch expression (`` `expr ! any => fallback' ``) to catch any error rather than only specific `ERR_*` codes."
    | Language.Lexer.Keyword.Break -> "`break [name];` - exits the (optionally named) enclosing loop."
    | Language.Lexer.Keyword.Continue -> "`continue [name];` - skips to the next iteration of the (optionally named) enclosing loop."

/// The literal spelling of each keyword - needed to know a token's span
/// (`Token` carries only a start `Line`/`Col`, not a length), matching
/// `Lexer.fs`'s own `keywords` dict spellings exactly.
let private keywordText (k: Language.Lexer.Keyword) : string =
    match k with
    | Language.Lexer.Keyword.If -> "if"
    | Language.Lexer.Keyword.Else -> "else"
    | Language.Lexer.Keyword.ElseIf -> "elseif"
    | Language.Lexer.Keyword.EndIf -> "endif"
    | Language.Lexer.Keyword.For -> "for"
    | Language.Lexer.Keyword.In -> "in"
    | Language.Lexer.Keyword.EndFor -> "endfor"
    | Language.Lexer.Keyword.Fork -> "fork"
    | Language.Lexer.Keyword.EndFork -> "endfork"
    | Language.Lexer.Keyword.Return -> "return"
    | Language.Lexer.Keyword.While -> "while"
    | Language.Lexer.Keyword.EndWhile -> "endwhile"
    | Language.Lexer.Keyword.Try -> "try"
    | Language.Lexer.Keyword.Except -> "except"
    | Language.Lexer.Keyword.Finally -> "finally"
    | Language.Lexer.Keyword.EndTry -> "endtry"
    | Language.Lexer.Keyword.Any -> "any"
    | Language.Lexer.Keyword.Break -> "break"
    | Language.Lexer.Keyword.Continue -> "continue"

/// The keyword token (if any) whose span contains `(astLine, astCol)`
/// (both 1-based, matching `Lexer.fs`).
let private keywordAt (astLine: int) (astCol: int) (tokens: Language.Lexer.Token[]) : Language.Lexer.Keyword option =
    tokens
    |> Array.tryPick (fun t ->
        match t.Kind with
        | Language.Lexer.TKeyword k when t.Line = astLine ->
            let len = (keywordText k).Length
            if astCol >= t.Col && astCol < t.Col + len then Some k else None
        | _ -> None)

/// Descriptions for the 12 built-in variables bound in every verb call
/// without declaration (`moocode-reference.md`'s "Built-in variables"
/// section; `prep`'s own description extends `prepstr`'s by the same
/// resolved-vs-raw-string pattern `dobj`/`dobjstr` and `iobj`/`iobjstr`
/// already establish). `None` for any other identifier - an ordinary
/// user-declared local variable, which this parser has no type/flow
/// information about beyond "it's assigned somewhere in this verb."
let private implicitVariableHelp (name: string) : string option =
    match name with
    | "this" -> Some "The object the currently-running verb is defined on."
    | "caller" -> Some "The object whose verb called this one (or `player`, if called from the command parser)."
    | "player" -> Some "The player who initiated the current task."
    | "verb" -> Some "The name the current verb was invoked as (string)."
    | "args" -> Some "The list of arguments passed to the current verb call."
    | "argstr" -> Some "The raw, unparsed argument string (command-line invocation only)."
    | "dobj" -> Some "The direct object resolved by the command parser (`#-1`/`$nothing` if none)."
    | "dobjstr" -> Some "The raw string the command parser matched as the direct object."
    | "prep" -> Some "The preposition resolved by the command parser (`#-1`/`$nothing` if none)."
    | "prepstr" -> Some "The raw string the command parser matched as the preposition."
    | "iobj" -> Some "The indirect object resolved by the command parser (`#-1`/`$nothing` if none)."
    | "iobjstr" -> Some "The raw string the command parser matched as the indirect object."
    | _ -> None

/// Hover text for a verb call whose receiver *couldn't* be resolved
/// statically (`this:foo()`, `who:tell()`) - best-effort via
/// `findAllDefiningObjects`: genuinely ambiguous when there's more than
/// one candidate, so this says so rather than silently picking one.
let private hoverForUnresolvedVerbCall (graph: Graph) (verbName: string) : Hover =
    match Metadata.Resolver.findAllDefiningObjects graph verbName with
    | [] -> mkHover (sprintf "**%s** - verb call; receiver isn't statically known, and no object in the graph defines a matching verb." verbName)
    | [ (definer, foundVerb) ] ->
        mkHover (
            sprintf
                "**%s** - receiver isn't statically known, but only one object defines a matching verb: `#%d (%s)`."
                verbName
                definer
                (definerName graph definer)
        )
    | candidates ->
        let shown = candidates |> List.truncate 5
        let lines = shown |> List.map (fun (d, _) -> sprintf "- `#%d (%s)`" d (definerName graph d))
        let moreNote = if candidates.Length > shown.Length then sprintf "\n- ...and %d more" (candidates.Length - shown.Length) else ""

        mkHover (
            sprintf
                "**%s** - receiver isn't statically known; %d objects define a matching verb, so this could be any of:\n\n%s%s"
                verbName
                candidates.Length
                (String.concat "\n" lines)
                moreNote
        )

/// Every `VerbCall` reference across every parsed verb in the graph,
/// tagged with the (object, primary verb name) it's found inside - the raw
/// material `TextDocumentReferences` scans. Rebuilt per request rather
/// than cached - the graph is loaded once at startup and doesn't change,
/// but this keeps the handler simple, and a full-corpus AST walk is cheap
/// (already proven fast enough for `dotnet test`'s corpus theories).
let private allVerbCallReferences (graph: Graph) : (ObjRef * string * AstQuery.FoundReference) seq =
    graph.Objects
    |> Map.toSeq
    |> Seq.collect (fun (num, o) ->
        o.Verbs
        |> Seq.collect (fun v ->
            match v.Ast, v.Meta.Names with
            | Some stmts, primary :: _ ->
                AstQuery.collectReferences stmts
                |> Seq.filter (fun r ->
                    match r.Ref with
                    | AstQuery.RefVerbCall _ -> true
                    | _ -> false)
                |> Seq.map (fun r -> num, primary, r)
            | _ -> Seq.empty))

/// Corpus-wide counterpart to `TextDocumentReferences` - instead of
/// resolving every call site against one target verb, resolves every call
/// site's own target *once* and checks every verb in the graph against that
/// single confirmed-targets set. Deliberately not `private` (unlike
/// `allVerbCallReferences` above) so `LanguageServer.Tests` can call it
/// directly without spinning up a full `MooLspServer` - same reasoning
/// `Metadata.Resolver`'s functions are public for `ResolverTests.fs`.
let findDeadVerbs (graph: Graph) : DeadVerbEntry[] =
    let confirmedTargets = System.Collections.Generic.HashSet<ObjRef * int>()
    let unresolvedCallNames = System.Collections.Generic.HashSet<string>()

    for containingObj, _, r in allVerbCallReferences graph do
        match r.Ref with
        | AstQuery.RefVerbCall(receiver, StrLit callName, _) ->
            match Metadata.Resolver.resolveReceiverInContext graph containingObj receiver with
            | Some receiverStart ->
                match Metadata.Resolver.findCallableVerb graph receiverStart callName with
                | Some(actualDefiner, actualVerb) -> confirmedTargets.Add(actualDefiner, actualVerb.Meta.Index) |> ignore
                | None -> ()
            | None -> unresolvedCallNames.Add callName |> ignore
        | _ -> ()

    graph.Objects
    |> Map.toSeq
    |> Seq.collect (fun (num, o) ->
        o.Verbs
        |> Seq.choose (fun v ->
            match v.Meta.Names with
            | primary :: _ when not (confirmedTargets.Contains(num, v.Meta.Index)) ->
                let possiblyDynamic = unresolvedCallNames |> Seq.exists (Metadata.Resolver.verbNameMatchesAny v.Meta.Names)
                Some { ObjRef = num; VerbName = primary; PossiblyDynamic = possiblyDynamic }
            | _ -> None))
    |> Array.ofSeq

/// Minimal client stub - this phase never needs to push notifications or
/// send server-initiated requests back to the editor (no diagnostics, no
/// progress reporting yet), so the base class's defaults are enough.
type MooLspClient() =
    inherit LspClient()

type MooLspServer(_client: MooLspClient, graph: Graph, bridge: SidecarBridge.SidecarBridge) =
    inherit LspServer()

    override _.Dispose() = ()

    override _.Initialize(_p: InitializeParams) =
        async {
            let capabilities: ServerCapabilities =
                { PositionEncoding = None
                  TextDocumentSync = None
                  NotebookDocumentSync = None
                  CompletionProvider =
                    Some
                        { WorkDoneProgress = None
                          TriggerCharacters = Some [| ":"; "$" |]
                          AllCommitCharacters = None
                          ResolveProvider = None
                          CompletionItem = None }
                  HoverProvider = Some(U2.C1 true)
                  SignatureHelpProvider =
                    Some
                        { WorkDoneProgress = None
                          TriggerCharacters = Some [| "(" |]
                          RetriggerCharacters = None }
                  DeclarationProvider = None
                  DefinitionProvider = Some(U2.C1 true)
                  TypeDefinitionProvider = None
                  ImplementationProvider = None
                  ReferencesProvider = Some(U2.C1 true)
                  DocumentHighlightProvider = None
                  DocumentSymbolProvider = None
                  CodeActionProvider = None
                  CodeLensProvider = None
                  DocumentLinkProvider = None
                  ColorProvider = None
                  WorkspaceSymbolProvider = None
                  DocumentFormattingProvider = None
                  DocumentRangeFormattingProvider = None
                  DocumentOnTypeFormattingProvider = None
                  RenameProvider = None
                  FoldingRangeProvider = None
                  SelectionRangeProvider = None
                  ExecuteCommandProvider = None
                  CallHierarchyProvider = None
                  LinkedEditingRangeProvider = None
                  SemanticTokensProvider = None
                  MonikerProvider = None
                  TypeHierarchyProvider = None
                  InlineValueProvider = None
                  InlayHintProvider = None
                  DiagnosticProvider = None
                  Workspace = None
                  Experimental = None }

            let result: InitializeResult =
                { Capabilities = capabilities
                  ServerInfo = Some { InitializeResultServerInfo.Name = "moodev-lsp"; Version = None } }

            return Ok result
        }

    /// A full dispatcher, not just "verb calls" - every reference kind
    /// `AstQuery` knows about gets *some* hover text, plus a token-level
    /// fallback for keywords (which never become AST nodes at all). Scope,
    /// precisely:
    ///   - `VerbCall` with a resolvable receiver -> the real dispatch
    ///     target (definer, metadata) via the 4.3c resolver.
    ///   - `VerbCall` with an unresolvable receiver (`this:foo()`,
    ///     `who:tell()`) -> best-effort "which objects even define a
    ///     matching verb" via `findAllDefiningObjects`, explicit about the
    ///     ambiguity rather than guessing one.
    ///   - `VerbCall` with a computed name -> says so; genuinely can't
    ///     resolve statically.
    ///   - `Call` (builtin) -> the same signature info `TextDocumentSignatureHelp`
    ///     shows, so hover and Ctrl+Shift+Space agree.
    ///   - `Prop(ObjLit 0, StrLit name)` (`$name` as a bare property, not a
    ///     call) -> resolved via the same `#0` registry `VerbCall`
    ///     receivers use.
    ///   - Any other `Prop` -> just names the property; no per-object
    ///     property metadata exists to say more (only verbs are tracked).
    ///   - `Ident` -> the 12 built-in verb-call variables get real
    ///     descriptions; anything else is labeled "local variable" (no
    ///     type/flow information beyond that exists).
    ///   - No reference at the position at all -> falls back to the raw
    ///     token stream for a keyword (`if`/`for`/`return`/...), which the
    ///     parser fully consumes into statement structure and so never
    ///     shows up as an `AstQuery` reference.
    override _.TextDocumentHover(p: HoverParams) =
        async {
            match verbAtUri graph p.TextDocument.Uri with
            | None -> return Ok None
            | Some(enclosingObj, verb) ->
                let astLine = int p.Position.Line + 1
                let astCol = int p.Position.Character + 1

                // Verb-call dispatch and builtin lookups go live (via
                // `bridge`, the Sidecar's `/lsp-bridge` connection) instead
                // of the static `graph` - see `SidecarBridge.fs`'s own doc
                // comment for why. Everything else here is pure AST/local
                // lookup, unchanged.
                let computeHover (r: AstQuery.FoundReference) : Async<Hover> =
                    async {
                        match r.Ref with
                        | AstQuery.RefVerbCall(receiver, StrLit verbName, _) ->
                            match Metadata.Resolver.resolveReceiverInContext graph enclosingObj receiver with
                            | Some startObj ->
                                let! resolved = bridge.ResolveVerbDispatch startObj verbName |> Async.AwaitTask

                                match resolved with
                                | Some result -> return hoverForResolvedVerbLive verbName result
                                | None ->
                                    return
                                        mkHover (sprintf "**%s** - no callable verb found via dispatch from `#%d`." verbName startObj)
                            | None -> return hoverForUnresolvedVerbCall graph verbName
                        | AstQuery.RefVerbCall(_, _, _) -> return mkHover "Verb call with a computed name - cannot resolve statically."
                        | AstQuery.RefCall(name, _) ->
                            let! builtins = bridge.GetBuiltins() |> Async.AwaitTask

                            match Map.tryFind name builtins with
                            | Some fn -> return mkHover (builtinHoverText fn)
                            | None -> return mkHover (sprintf "**%s(...)** - not a registered builtin function." name)
                        | AstQuery.RefProp(ObjLit 0L, StrLit name) ->
                            match Metadata.Resolver.resolveReceiver graph (Prop(ObjLit 0L, StrLit name, 0, 0)) with
                            | Some objRef -> return mkHover (sprintf "**$%s** -> `#%d (%s)`" name objRef (definerName graph objRef))
                            | None -> return mkHover (sprintf "**$%s** - `#0.%s` isn't a registered object property." name name)
                        | AstQuery.RefProp(_, StrLit name) -> return mkHover (sprintf "Property **%s**." name)
                        | AstQuery.RefProp(_, _) -> return mkHover "Computed property access - cannot resolve the property name statically."
                        | AstQuery.RefIdent name ->
                            match implicitVariableHelp name with
                            | Some help -> return mkHover (sprintf "**%s** (built-in variable)\n\n%s" name help)
                            | None -> return mkHover (sprintf "**%s** - local variable." name)
                    }

                match verb.Ast |> Option.bind (fun stmts -> AstQuery.referenceAt astLine astCol stmts) with
                | Some r ->
                    let! hover = computeHover r
                    return Ok(Some hover)
                | None ->
                    let fromKeyword =
                        verb.Tokens
                        |> Option.bind (keywordAt astLine astCol)
                        |> Option.map (fun k -> mkHover (sprintf "**%s**\n\n%s" (keywordText k) (keywordHelp k)))

                    return Ok fromKeyword
        }

    /// Two independent things this can resolve, tried in order:
    ///   - A `VerbCall` (`resolvableVerbCallAt`) - dispatch to a real verb
    ///     elsewhere, same as ever.
    ///   - Failing that, a plain local-variable `Ident` (not one of the 12
    ///     always-bound built-ins, which have no single "introduced here"
    ///     site) - jumps to wherever it's first bound in *this same verb*
    ///     (`AstQuery.firstBindingSite`): a plain assignment, a scatter
    ///     target, a `for`/`fork` binding, or an `except` arm's error name.
    ///     Reported as a `Location` back into the same document (not
    ///     `locationOfVerb`, which is for jumping to a *different* verb) -
    ///     no dispatch involved, just "where did this name come from."
    override _.TextDocumentDefinition(p: DefinitionParams) =
        async {
            match verbAtUri graph p.TextDocument.Uri with
            | None -> return Ok None
            | Some(enclosingObj, verb) ->
                match verb.Ast with
                | None -> return Ok None
                | Some stmts ->
                    let lspLine = int p.Position.Line
                    let lspCol = int p.Position.Character

                    match resolvableVerbCallAt graph enclosingObj stmts lspLine lspCol with
                    | Some(startObj, verbName, _args) ->
                        let! resolved = bridge.ResolveVerbDispatch startObj verbName |> Async.AwaitTask

                        match resolved with
                        | Some result -> return Ok(Some(U2.C1(U2.C1(locationOfVerbLive result))))
                        | None -> return Ok None
                    | None ->
                        match AstQuery.referenceAt (lspLine + 1) (lspCol + 1) stmts with
                        | Some { Ref = AstQuery.RefIdent name } when (implicitVariableHelp name).IsNone ->
                            match AstQuery.firstBindingSite stmts name with
                            | Some(defLine, defCol) ->
                                let loc: Location =
                                    { Uri = p.TextDocument.Uri
                                      Range =
                                        { Start = { Line = uint32 (defLine - 1); Character = uint32 (defCol - 1) }
                                          End = { Line = uint32 (defLine - 1); Character = uint32 (defCol - 1 + name.Length) } } }

                                return Ok(Some(U2.C1(U2.C1 loc)))
                            | None -> return Ok None
                        | _ -> return Ok None
        }

    /// Local variables (from the currently-open verb's last-saved AST) +
    /// every builtin + verb names reachable from whatever receiver the
    /// nearest preceding `VerbCall` resolves to. Not context-filtered by
    /// "is the cursor actually in a position where a verb name syntactically
    /// belongs" - Monaco's own completion widget narrows the combined list
    /// as the user keeps typing, so returning a superset here is normal
    /// LSP practice, not a shortcut.
    ///
    /// Deliberately operates on the last-*saved* AST, same as hover and
    /// go-to-definition - there's no `textDocument/didChange` sync in this
    /// editor (M3's Open/Save model isn't a live buffer), so a receiver the
    /// user is actively typing but hasn't saved yet won't be seen. Property
    /// completion remains out of scope per the plan doc's own v1 decision.
    override _.TextDocumentCompletion(p: CompletionParams) =
        async {
            match verbAtUri graph p.TextDocument.Uri with
            | None -> return Ok None
            | Some(enclosingObj, verb) ->
                match verb.Ast with
                | None -> return Ok None
                | Some stmts ->
                    let lspLine = int p.Position.Line
                    let lspCol = int p.Position.Character

                    let localVarItems =
                        AstQuery.boundVariableNames stmts
                        |> List.map (fun name -> mkCompletionItem name CompletionItemKind.Variable)

                    let! liveBuiltins = bridge.GetBuiltins() |> Async.AwaitTask

                    let builtinItems =
                        liveBuiltins
                        |> Map.toList
                        |> List.map (fun (name, _) -> mkCompletionItem name CompletionItemKind.Function)

                    let verbItems =
                        AstQuery.nearestReferenceAtOrBefore (lspLine + 1) (lspCol + 1) stmts
                        |> Option.bind (fun r ->
                            match r.Ref with
                            | AstQuery.RefVerbCall(receiver, _, _) -> Metadata.Resolver.resolveReceiverInContext graph enclosingObj receiver
                            | _ -> None)
                        |> Option.map (fun startObj ->
                            Metadata.Resolver.allCallableVerbNames graph startObj
                            |> List.map (fun name -> mkCompletionItem name CompletionItemKind.Method))
                        |> Option.defaultValue []

                    let items = List.toArray (localVarItems @ builtinItems @ verbItems)
                    return Ok(Some(U2.C1 items))
        }

    /// Builtins only - a MOO verb call's "signature" is just `(list args)`,
    /// nothing typed to show, so signature help is meaningful for the
    /// builtin-function case `function_info()` actually describes, not for
    /// `VerbCall` nodes.
    override _.TextDocumentSignatureHelp(p: SignatureHelpParams) =
        async {
            match verbAtUri graph p.TextDocument.Uri with
            | None -> return Ok None
            | Some(_, verb) ->
                match verb.Ast with
                | None -> return Ok None
                | Some stmts ->
                    let lspLine = int p.Position.Line
                    let lspCol = int p.Position.Character

                    let builtinName =
                        AstQuery.nearestReferenceAtOrBefore (lspLine + 1) (lspCol + 1) stmts
                        |> Option.bind (fun r ->
                            match r.Ref with
                            | AstQuery.RefCall(name, _) -> Some name
                            | _ -> None)

                    let! liveBuiltins = bridge.GetBuiltins() |> Async.AwaitTask

                    match builtinName |> Option.bind (fun name -> Map.tryFind name liveBuiltins) with
                    | None -> return Ok None
                    | Some fn ->
                        let parameters =
                            fn.ArgTypes
                            |> List.mapi (fun i t -> { Label = U2.C1(builtinParamLabel fn i t); Documentation = None })
                            |> List.toArray

                        let sigInfo: SignatureInformation =
                            { Label = builtinSignatureLabel fn
                              Documentation = None
                              Parameters = Some parameters
                              ActiveParameter = None }

                        return Ok(Some { Signatures = [| sigInfo |]; ActiveSignature = None; ActiveParameter = None })
        }

    /// Scans every parsed verb in the whole graph for `VerbCall` sites that
    /// resolve (receiver + name) to the exact same verb identified at the
    /// cursor. Also counts literal-name-matching call sites whose receiver
    /// *couldn't* be resolved (`this:foo()`, computed names) - these might
    /// also be references, but can't be confirmed statically. Per the plan
    /// doc's Known Hazards list, that count must be surfaced, not silently
    /// dropped - standard LSP `Location[]` has no field for it, so when the
    /// count is nonzero this appends one synthetic `Location` whose URI
    /// uses a distinct `moodev-caveat://` scheme carrying the count in
    /// plain text. `LspClient.fs` recognizes that scheme and renders it as
    /// a caveat rather than a jump target - a deliberate, visible extension
    /// beyond the base LSP contract, not a hidden hack.
    override _.TextDocumentReferences(p: ReferenceParams) =
        async {
            match verbAtUri graph p.TextDocument.Uri with
            | None -> return Ok None
            | Some(enclosingObj, verb) ->
                match verb.Ast with
                | None -> return Ok None
                | Some stmts ->
                    match resolvableVerbCallAt graph enclosingObj stmts (int p.Position.Line) (int p.Position.Character) with
                    | None -> return Ok None
                    | Some(startObj, verbName, _args) ->
                        match Metadata.Resolver.findCallableVerb graph startObj verbName with
                        | None -> return Ok None
                        | Some(targetDefiner, targetVerb) ->
                            let confirmed = ResizeArray<Location>()
                            let mutable unresolvedCount = 0

                            for containingObj, containingVerbName, r in allVerbCallReferences graph do
                                match r.Ref with
                                | AstQuery.RefVerbCall(receiver, StrLit callName, _) ->
                                    match Metadata.Resolver.resolveReceiverInContext graph containingObj receiver with
                                    | Some receiverStart ->
                                        match Metadata.Resolver.findCallableVerb graph receiverStart callName with
                                        | Some(actualDefiner, actualVerb) when
                                            actualDefiner = targetDefiner && actualVerb.Meta.Index = targetVerb.Meta.Index
                                            ->
                                            confirmed.Add(
                                                { Uri = moodevVerbUri containingObj containingVerbName
                                                  Range =
                                                    { Start = { Line = uint32 (r.Line - 1); Character = uint32 (r.Col - 1) }
                                                      End =
                                                        { Line = uint32 (r.Line - 1)
                                                          Character = uint32 (r.Col - 1 + r.Length) } } }
                                            )
                                        | _ -> ()
                                    | None ->
                                        if Metadata.Resolver.verbNameMatchesAny targetVerb.Meta.Names callName then
                                            unresolvedCount <- unresolvedCount + 1
                                | _ -> ()

                            if unresolvedCount > 0 then
                                confirmed.Add(
                                    { Uri = sprintf "moodev-caveat://%d-unresolvable-call-sites" unresolvedCount
                                      Range =
                                        { Start = { Line = 0u; Character = 0u }
                                          End = { Line = 0u; Character = 0u } } }
                                )

                            return Ok(Some(confirmed.ToArray()))
        }

    /// Custom method (`moodev/getObjectTree`, no params) - every object in
    /// the graph (not just ones with verbs of their own - the client's tree
    /// needs the full structural chain to reach them), with parent/child
    /// edges and own-verb/own-property names (declaration order, not
    /// inherited - matches `$vcs:ide_fetch`/`ide_save`'s own scope, which
    /// only ever operates on a verb literally defined on the object you
    /// name), for the editor's object tree. `Name` is a fully-formatted
    /// display label - the
    /// object's real (unsanitized) live name, its object number, and its
    /// corified `$name` if it's registered as one of `#0`'s properties, e.g.
    /// "Generic Room (#3) [$room]" - not `lookups.toml`'s sanitized
    /// "Generic_Room" (that's a git-directory-safe name, not meant for a
    /// human-facing picker). Falls back to `#N` alone if the object has
    /// neither a live name nor a `lookups.toml` entry.
    member _.GetObjectTree(_p: obj) : Async<Result<ObjectTreeNode[], JsonRpc.Error>> =
        async {
            let nodes =
                graph.Objects
                |> Map.toSeq
                |> Seq.map (fun (_, o) ->
                    { ObjRef = o.Num
                      Name = displayNameFor graph o.Num
                      Parents = o.Parents |> Array.ofList
                      Children = o.Children |> Array.ofList
                      Verbs =
                        o.Verbs
                        |> List.choose (fun v ->
                            v.Meta.Names
                            |> List.tryHead
                            |> Option.map (fun name ->
                                { Name = name
                                  Perms = v.Meta.Perms
                                  Dobj = v.Meta.Dobj
                                  Prep = v.Meta.Prep
                                  Iobj = v.Meta.Iobj }: ObjectTreeVerb))
                        |> Array.ofList
                      Properties =
                        o.Properties
                        |> List.map (fun pr -> { Name = pr.Name; Perms = pr.Perms }: ObjectTreeProperty)
                        |> Array.ofList }
                    : ObjectTreeNode)
                |> Seq.sortBy (fun n -> n.Name)
                |> Array.ofSeq

            return Ok nodes
        }

    /// Custom method (`moodev/findDeadVerbs`, no params) - the "what's safe
    /// to delete" report: every verb `findDeadVerbs` found no confirmed
    /// reference to, corpus-wide, in one pass rather than searching one verb
    /// at a time via `TextDocumentReferences`.
    member _.FindDeadVerbs(_p: obj) : Async<Result<DeadVerbEntry[], JsonRpc.Error>> =
        async { return Ok(findDeadVerbs graph) }
