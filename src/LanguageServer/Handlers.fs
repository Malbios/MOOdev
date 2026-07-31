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
      Dobj: string
      Prep: string
      Iobj: string
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


/// Every declared field defaulted to `None`/absent except `Label` and,
/// where the caller has it (currently only builtins - see `builtinHoverText`),
/// `Documentation` - the same Markdown hover already shows, so a completion
/// popup describes a builtin identically instead of just naming it.
let private mkCompletionItem (label: string) (kind: CompletionItemKind) (documentation: string option) : CompletionItem =
    { Label = label
      LabelDetails = None
      Kind = Some kind
      Tags = None
      Detail = None
      Documentation = documentation |> Option.map (fun text -> U2.C2 { Kind = MarkupKind.Markdown; Value = text })
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

/// One documentable "thing" in MOOcode - a control keyword, an implicit
/// variable, or a builtin function - unified into one flat, searchable
/// catalog for the client's docs sidebar (`moodev/getMoocodeDocs`).
/// `Signature` is just the bare name for keywords/variables (nothing more
/// to show) and the full `name(args)` shape for builtins.
type MoocodeDocEntry =
    { Name: string
      Signature: string
      Description: string
      Kind: string }

/// All 19 cases of `Language.Lexer.Keyword` - listed explicitly since
/// `keywordHelp`'s own match is exhaustive but this project doesn't reach
/// for reflection-based DU enumeration elsewhere, so neither does this.
let private allKeywords: Language.Lexer.Keyword list =
    [ Language.Lexer.Keyword.If
      Language.Lexer.Keyword.Else
      Language.Lexer.Keyword.ElseIf
      Language.Lexer.Keyword.EndIf
      Language.Lexer.Keyword.For
      Language.Lexer.Keyword.In
      Language.Lexer.Keyword.EndFor
      Language.Lexer.Keyword.Fork
      Language.Lexer.Keyword.EndFork
      Language.Lexer.Keyword.Return
      Language.Lexer.Keyword.While
      Language.Lexer.Keyword.EndWhile
      Language.Lexer.Keyword.Try
      Language.Lexer.Keyword.Except
      Language.Lexer.Keyword.Finally
      Language.Lexer.Keyword.EndTry
      Language.Lexer.Keyword.Any
      Language.Lexer.Keyword.Break
      Language.Lexer.Keyword.Continue ]

/// The same 12 names `implicitVariableHelp`'s own match hardcodes above -
/// kept as a literal list here too so `moocodeDocs` can enumerate them. The
/// prose itself still only ever comes from calling `implicitVariableHelp`,
/// so there's exactly one place the actual descriptions live.
let private implicitVariableNames =
    [ "this"; "caller"; "player"; "verb"; "args"; "argstr"; "dobj"; "dobjstr"; "prep"; "prepstr"; "iobj"; "iobjstr" ]

/// Full catalog for the client's docs sidebar: every control keyword,
/// implicit variable, and live builtin - reusing the exact same prose hover
/// already shows (`keywordHelp`/`implicitVariableHelp`/`fn.Description`), so
/// the two surfaces can never disagree; this only ever enumerates *which*
/// existing function to call for each name, never duplicates the text
/// itself.
let moocodeDocs (liveBuiltins: Map<string, BuiltinFunc>) : MoocodeDocEntry[] =
    let keywordEntries =
        allKeywords
        |> List.map (fun k ->
            { Name = keywordText k
              Signature = keywordText k
              Description = keywordHelp k
              Kind = "keyword" })

    let variableEntries =
        implicitVariableNames
        |> List.choose (fun name ->
            implicitVariableHelp name
            |> Option.map (fun desc ->
                { Name = name
                  Signature = name
                  Description = desc
                  Kind = "variable" }))

    let builtinEntries =
        liveBuiltins
        |> Map.toList
        |> List.map (fun (name, fn) ->
            { Name = name
              Signature = builtinSignatureLabel fn
              Description = fn.Description |> Option.defaultValue "Built-in function."
              Kind = "builtin" })

    keywordEntries @ variableEntries @ builtinEntries |> List.toArray

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

/// The `(definer, verb-index)` set of every verb `allVerbCallReferences`
/// resolves to a real callable target, corpus-wide, alongside every call
/// name that couldn't be resolved statically - shared by `findDeadVerbs` (a
/// verb NOT in the confirmed set is dead; a call name in the unresolved set
/// matching its own names makes it "possibly dynamic" rather than clean)
/// and `findGotchas`'s missing-x-bit check (a verb IN the confirmed set
/// needs its `x` bit, or the resolvable caller that put it there can never
/// actually reach it).
let private computeReferenceResolution (graph: Graph) : System.Collections.Generic.HashSet<ObjRef * int> * System.Collections.Generic.HashSet<string> =
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

    confirmedTargets, unresolvedCallNames

/// Corpus-wide counterpart to `TextDocumentReferences` - instead of
/// resolving every call site against one target verb, resolves every call
/// site's own target *once* and checks every verb in the graph against that
/// single confirmed-targets set. Deliberately not `private` (unlike
/// `allVerbCallReferences` above) so `LanguageServer.Tests` can call it
/// directly without spinning up a full `MooLspServer` - same reasoning
/// `Metadata.Resolver`'s functions are public for `ResolverTests.fs`.
let findDeadVerbs (graph: Graph) : DeadVerbEntry[] =
    let confirmedTargets, unresolvedCallNames = computeReferenceResolution graph

    graph.Objects
    |> Map.toSeq
    |> Seq.collect (fun (num, o) ->
        o.Verbs
        |> Seq.choose (fun v ->
            match v.Meta.Names with
            | primary :: _ when not (confirmedTargets.Contains(num, v.Meta.Index)) ->
                let possiblyDynamic = unresolvedCallNames |> Seq.exists (Metadata.Resolver.verbNameMatchesAny v.Meta.Names)

                Some
                    { ObjRef = num
                      VerbName = primary
                      Dobj = v.Meta.Dobj
                      Prep = v.Meta.Prep
                      Iobj = v.Meta.Iobj
                      PossiblyDynamic = possiblyDynamic }
            | _ -> None))
    |> Array.ofSeq

/// True if `pred` matches `e` or any expression nested anywhere inside it -
/// exhaustive over every `Expr` case (no wildcard arm) so adding a new AST
/// node shape to `Ast.fs` is a compile error here until this is updated too,
/// rather than a silently-incomplete gotcha scan.
let rec private existsInExpr (pred: Expr -> bool) (e: Expr) : bool =
    let go = existsInExpr pred
    let goArg (a: Arg) = match a with | Normal e | Splice e -> go e

    pred e
    || match e with
       | IntLit _
       | FloatLit _
       | StrLit _
       | ObjLit _
       | ErrLit _
       | Ident _
       | FirstIndex
       | LastIndex -> false
       | Prop(o, n, _, _) -> go o || go n
       | VerbCall(r, n, args, _, _) -> go r || go n || (args |> List.exists goArg)
       | Call(_, args, _, _) -> args |> List.exists goArg
       | Index(a, b) -> go a || go b
       | Range(a, b) -> go a || go b
       | Binary(_, a, b) -> go a || go b
       | Unary(_, a) -> go a
       | Cond(a, b, c) -> go a || go b || go c
       | Catch(a, _, fb) -> go a || (fb |> Option.map go |> Option.defaultValue false)
       | Assign(a, b) -> go a || go b
       | Scatter(_, b) -> go b
       | ListLit args -> args |> List.exists goArg
       | MapLit kvs -> kvs |> List.exists (fun (k, v) -> go k || go v)

/// Every "root" expression directly inside `s` (a loop's condition/source,
/// an `if`'s condition, an expression statement, a `return` value, ...),
/// recursively including every nested statement body - `descendIntoFork`
/// controls whether a nested `fork ... endfork` body's own expressions are
/// included. `existsInExpr` still has to be applied on top of each yielded
/// expression to reach *its* nested sub-expressions; this only walks the
/// statement tree. Exhaustive over every `Stmt` case, same reasoning as
/// `existsInExpr` above.
let rec private stmtExprs (descendIntoFork: bool) (s: Stmt) : Expr seq =
    let body = Seq.collect (stmtExprs descendIntoFork)

    match s with
    | If(arms, elsePart) ->
        seq {
            for cond, b in arms do
                yield cond
                yield! body b

            match elsePart with
            | Some b -> yield! body b
            | None -> ()
        }
    | ForList(_, _, source, b) -> seq { yield source; yield! body b }
    | ForRange(_, lo, hi, b) -> seq { yield lo; yield hi; yield! body b }
    | While(_, cond, b) -> seq { yield cond; yield! body b }
    | Fork(_, delay, b) -> if descendIntoFork then seq { yield delay; yield! body b } else Seq.singleton delay
    | TryExcept(b, arms) -> Seq.append (body b) (arms |> Seq.collect (fun a -> body a.Body))
    | TryFinally(b, h) -> Seq.append (body b) (body h)
    | ExprStmt e -> Seq.singleton e
    | Return(Some e) -> Seq.singleton e
    | Return None
    | Break _
    | Continue _
    | ErrorStmt _ -> Seq.empty

/// Every `ForList`/`ForRange`/`While` loop statement anywhere in `stmts`,
/// at any nesting depth (including inside a `fork` body - that's still a
/// real loop with its own tick/seconds budget once its task starts
/// running, so it still needs checking on its own terms).
let rec private allLoops (stmts: Stmt list) : Stmt seq =
    seq {
        for s in stmts do
            match s with
            | ForList(_, _, _, b)
            | ForRange(_, _, _, b)
            | While(_, _, b) ->
                yield s
                yield! allLoops b
            | If(arms, elsePart) ->
                for _, b in arms do
                    yield! allLoops b

                match elsePart with
                | Some b -> yield! allLoops b
                | None -> ()
            | Fork(_, _, b) -> yield! allLoops b
            | TryExcept(b, arms) ->
                yield! allLoops b
                for a in arms do
                    yield! allLoops a.Body
            | TryFinally(b, h) ->
                yield! allLoops b
                yield! allLoops h
            | ExprStmt _
            | Return _
            | Break _
            | Continue _
            | ErrorStmt _ -> ()
    }

let private isSuspendCall (e: Expr) : bool =
    match e with
    | Call(name, _, _, _) -> System.String.Equals(name, "suspend", System.StringComparison.OrdinalIgnoreCase)
    | _ -> false

/// `list[0]` - always `E_RANGE` (MOO lists are 1-indexed, so there is no
/// legitimate reason to index with a literal `0`); `list[$]`/`list[a..b]`
/// aren't this shape and are left alone.
let private isZeroIndex (e: Expr) : bool =
    match e with
    | Index(_, IntLit 0L) -> true
    | _ -> false

/// A loop's own body has no `suspend()` reachable anywhere inside it (a
/// nested loop's suspend still counts - it runs during the outer loop's own
/// iterations; a nested `fork`'s doesn't - that's a different task's
/// budget), so a source that grows past the tick/seconds limit will end
/// the task mid-iteration with no chance to yield first.
let private loopBodyNeedsSuspend (body: Stmt list) : bool =
    body |> List.collect (stmtExprs false >> List.ofSeq) |> List.exists (existsInExpr isSuspendCall) |> not

/// Every statement anywhere in `stmts`, at any nesting depth (including
/// inside `fork`/`try` bodies) - same traversal shape as `allLoops` above,
/// just unfiltered (every `Stmt` case is yielded, not only loops). Backs
/// both the "can suspend" (any `Fork`) and "may return a value" (any
/// `Return(Some _)`) facts `inferredVerbSummary` reports below.
let rec private allStmtsDeep (stmts: Stmt list) : Stmt seq =
    seq {
        for s in stmts do
            yield s

            match s with
            | If(arms, elsePart) ->
                for _, b in arms do
                    yield! allStmtsDeep b

                match elsePart with
                | Some b -> yield! allStmtsDeep b
                | None -> ()
            | ForList(_, _, _, b)
            | ForRange(_, _, _, b)
            | While(_, _, b)
            | Fork(_, _, b) -> yield! allStmtsDeep b
            | TryExcept(b, arms) ->
                yield! allStmtsDeep b

                for a in arms do
                    yield! allStmtsDeep a.Body
            | TryFinally(b, h) ->
                yield! allStmtsDeep b
                yield! allStmtsDeep h
            | ExprStmt _
            | Return _
            | Break _
            | Continue _
            | ErrorStmt _ -> ()
    }

/// One inferred verb parameter, in whatever shape it was recovered as -
/// `Ast.fs`'s `ScatterItem` for the `{who, ?what = 0, @rest} = args;` idiom
/// (which already carries required/optional-with-default/rest for free), or
/// a bare ordinal `args[N]` index for the `who = args[1];` fallback idiom.
type private InferredParam =
    | ReqParam of string
    | OptParam of string * string option
    | RestParam of string
    | IndexParam of int * string

/// Renders simple literal defaults (`?what = 0`, `?msg = ""`, ...) verbatim;
/// anything more complex (an expression referencing another variable, a
/// property, a call) is left as "no default text" rather than attempting a
/// full expression pretty-printer, which doesn't exist in this project and
/// is out of scope here (`verb_code`'s own indent=1 rendering is the only
/// full-source pretty-printer this tooling has, and it operates MOO-side).
let rec private exprBrief (e: Expr) : string option =
    match e with
    | IntLit n -> Some(string n)
    | FloatLit f -> Some(string f)
    | StrLit s -> Some(sprintf "\"%s\"" s)
    | ObjLit n -> Some(sprintf "#%d" n)
    | ErrLit s -> Some s
    | Unary(Neg, inner) -> exprBrief inner |> Option.map (sprintf "-%s")
    | _ -> None

let private renderParam (p: InferredParam) : string =
    match p with
    | ReqParam name -> sprintf "`%s`" name
    | OptParam(name, None) -> sprintf "`%s` (optional)" name
    | OptParam(name, Some def) -> sprintf "`%s` (optional, default `%s`)" name def
    | RestParam name -> sprintf "`@%s` (rest)" name
    | IndexParam(n, name) -> sprintf "`%s` (args[%d])" name n

/// Recovers a verb's parameter list from whichever of the two common
/// "unpack `args`" idioms appears among the verb's **top-level** statements
/// (not nested inside `if`/`for`/etc. - conditional/looped arg-unpacking
/// isn't a pattern worth guessing at). The scatter-assignment idiom, when
/// present, is authoritative for the whole list and wins outright; the
/// `args[N]`-indexing idiom is only consulted as a fallback.
let private inferredParams (stmts: Stmt list) : InferredParam list option =
    let scatterFromArgs =
        stmts
        |> List.tryPick (function
            | ExprStmt(Scatter(items, Ident("args", _, _))) ->
                Some(
                    items
                    |> List.map (function
                        | Required bn -> ReqParam bn.Name
                        | Optional(bn, def) -> OptParam(bn.Name, def |> Option.bind exprBrief)
                        | Rest bn -> RestParam bn.Name)
                )
            | _ -> None)

    match scatterFromArgs with
    | Some ps -> Some ps
    | None ->
        let indexed =
            stmts
            |> List.choose (function
                | ExprStmt(Assign(Ident(name, _, _), Index(Ident("args", _, _), IntLit n))) -> Some(int n, name)
                | _ -> None)
            |> List.sortBy fst
            |> List.map IndexParam

        if List.isEmpty indexed then None else Some indexed

/// Which of the three interesting `AstQuery.Reference` kinds a call/prop/
/// verb-call reference is - unused `RefIdent`s (local variables, the 12
/// implicit built-ins) are deliberately dropped, since a verb touches
/// `this`/`player`/`args` near-universally and listing them would be noise,
/// not signal.
type private DepKind =
    | DepProp
    | DepVerb
    | DepBuiltin

/// Everything a verb's body references worth calling out as a dependency -
/// reuses `AstQuery.collectReferences` wholesale rather than a fresh walk. A
/// bare `Call` is always a builtin in MOOcode (there are no receiver-less
/// user-defined functions), so no live-builtins lookup is needed to classify
/// it.
let private inferredDependencies (stmts: Stmt list) : (DepKind * string) list =
    AstQuery.collectReferences stmts
    |> List.choose (fun fr ->
        match fr.Ref with
        | AstQuery.RefProp(_, StrLit name) -> Some(DepProp, name)
        | AstQuery.RefVerbCall(_, StrLit name, _) -> Some(DepVerb, name)
        | AstQuery.RefCall(name, _) -> Some(DepBuiltin, name)
        | _ -> None)
    |> List.distinct

/// Renders one dependency kind's line, e.g. "Properties: `foo`, `bar`" -
/// capped at 8 names with an explicit "+N more" suffix rather than a silent
/// truncation, `None` if this verb has none of this kind.
let private renderDeps (kind: DepKind) (label: string) (deps: (DepKind * string) list) : string option =
    let names =
        deps |> List.choose (fun (k, n) -> if k = kind then Some n else None) |> List.distinct |> List.sort

    match names with
    | [] -> None
    | _ ->
        let shown, extra =
            if List.length names > 8 then names |> List.truncate 8, List.length names - 8 else names, 0

        let suffix = if extra > 0 then sprintf ", +%d more" extra else ""
        Some(sprintf "%s: %s%s" label (shown |> List.map (sprintf "`%s`") |> String.concat ", ") suffix)

/// Whether this verb ever suspends the current task - either directly
/// (`suspend()`, reusing the exact `stmtExprs true` + `existsInExpr
/// isSuspendCall` idiom `findGotchas`'s own unbounded-loop check already
/// uses) or by forking a separate task (`Fork` is a `Stmt`, not an `Expr`,
/// so `existsInExpr` alone can never see it - `allStmtsDeep` covers that
/// case).
let private canSuspend (stmts: Stmt list) : bool =
    (stmts |> List.collect (stmtExprs true >> List.ofSeq) |> List.exists (existsInExpr isSuspendCall))
    || (allStmtsDeep stmts |> Seq.exists (function Fork _ -> true | _ -> false))

/// Existence check only, not a full control-flow proof that *every* path
/// returns a value - a bare `return;`/falling off the end both implicitly
/// yield 0 either way, so this only looks for at least one `return <expr>;`
/// anywhere in the body worth mentioning.
let private mayReturnValue (stmts: Stmt list) : bool =
    allStmtsDeep stmts |> Seq.exists (function Return(Some _) -> true | _ -> false)

/// Auto-inferred documentation summary for a user-authored verb, derived
/// entirely from its own AST (no authoring convention required) - the
/// primary half of the "self-documenting code" hover feature; a verb's own
/// leading `/* ... */` comment (captured by `Language.Lexer.tokenize`, see
/// `LexResult.LeadingComment`) is the supplementary half, appended
/// separately by the caller. `None` when none of the four facts below found
/// anything worth reporting. Deliberately not `private`, same reasoning as
/// `findGotchas` below - unit tests call this directly against hand-built
/// ASTs rather than only exercising it through the live hover path.
let inferredVerbSummary (stmts: Stmt list) : string option =
    let paramsLine =
        inferredParams stmts
        |> Option.map (fun ps -> sprintf "Parameters: %s" (ps |> List.map renderParam |> String.concat ", "))

    let deps = inferredDependencies stmts

    let depLines =
        [ renderDeps DepProp "Properties" deps
          renderDeps DepVerb "Verb calls" deps
          renderDeps DepBuiltin "Builtins" deps ]
        |> List.choose id

    let suspendLine =
        if canSuspend stmts then Some "Can suspend (calls `suspend()` or forks a task)" else None

    let returnsLine = if mayReturnValue stmts then Some "May return a value" else None

    let lines =
        [ yield! Option.toList paramsLine
          yield! depLines
          yield! Option.toList suspendLine
          yield! Option.toList returnsLine ]

    if List.isEmpty lines then
        None
    else
        Some(sprintf "**Inferred:**\n%s" (lines |> List.map (sprintf "- %s") |> String.concat "\n"))

/// A bare string-literal statement as the very first thing in a verb body
/// (`"Does a thing.";`) - a MOOcode "docstring" idiom, the supplementary
/// half of the self-documenting-code hover feature. Deliberately NOT a
/// `/* ... */` block comment: confirmed live that `verb_code()` reconstructs
/// source from the *compiled* verb program, and comments are discarded by
/// the lexer before the parser (and therefore the compiled program) ever
/// sees them - a real block comment can never survive a save/re-fetch round
/// trip. A bare string-literal expression statement, by contrast, is a
/// genuine (if inert) AST node, so it round-trips through `set_verb_code()`/
/// `verb_code()` exactly like any other statement. Deliberately not
/// `private`, same reasoning as `inferredVerbSummary` above - unit tests
/// call this directly.
let leadingDocString (stmts: Stmt list) : string option =
    match stmts with
    | ExprStmt(StrLit s) :: _ when s <> "" -> Some s
    | _ -> None

/// Hover body for a `VerbCall` resolved via `SidecarBridge.ResolveVerbDispatch`
/// - the live equivalent of the old `hoverForResolvedVerb`, which took a
/// static-graph `VerbNode` this project no longer builds for the resolved
/// verb (see `Handlers.MooLspServer`'s `TextDocumentHover`/`TextDocumentDefinition`).
/// Also lexes/parses `result.Code` (fetched live alongside the rest of this
/// record, see `SidecarBridge.VerbDispatchResult.Code`) to append an
/// auto-inferred summary plus any leading docstring statement - the
/// "self-documenting code" hover feature, extending the same treatment
/// `builtinHoverText` already gives builtins to user-authored verbs.
/// Degrades cleanly to just the metadata above (no extra section at all)
/// when `Code` is empty or fails to lex - never an error, matching every
/// other graceful miss in this dispatcher.
let private hoverForResolvedVerbLive (verbName: string) (result: SidecarBridge.VerbDispatchResult) : Hover =
    let baseText =
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

    let extraSections =
        if List.isEmpty result.Code then
            []
        else
            let lexResult = Language.Lexer.tokenize (result.Code |> String.concat "\n")

            match lexResult.Error with
            | Some _ -> []
            | None ->
                let stmts = Language.Parser.parse lexResult.Tokens

                [ inferredVerbSummary stmts
                  leadingDocString stmts |> Option.map (sprintf "**Comment:**\n%s") ]
                |> List.choose id

    (baseText :: extraSections) |> String.concat "\n\n" |> mkHover

/// One of the three catalogued "MOOcode gotchas" (the project plan's own
/// MOOcode gotchas section) `findGotchas` checks for, exhaustively across
/// every parsed verb in the graph. `Kind` is a plain string tag rather than
/// a DU - simpler to send over the wire, matching `Dobj`/`Prep`/`Iobj`'s own
/// plain-string convention on `DeadVerbEntry` above - one of
/// `"missing-x-bit"` / `"unbounded-loop"` / `"zero-index"`.
type GotchaEntry = { ObjRef: ObjRef; VerbName: string; Kind: string }

/// True if `target` is `start` itself or reachable by walking `start`'s
/// parents transitively - i.e. whether a dispatch search starting at
/// `start` and walking up through its ancestors would ever reach an object
/// defining a verb (the same walk `findCallableVerb` does, just without
/// that function's own x-bit filter - see `findGotchas`'s missing-x-bit
/// check for why this needs its own, laxer walk). `visited` guards against
/// a malformed/cyclic parent chain; a well-formed DAG never needs it.
let rec private isReachableAncestor (graph: Graph) (visited: System.Collections.Generic.HashSet<ObjRef>) (start: ObjRef) (target: ObjRef) : bool =
    if start = target then
        true
    elif not (visited.Add start) then
        false
    else
        match Map.tryFind start graph.Objects with
        | None -> false
        | Some node -> node.Parents |> List.exists (fun p -> isReachableAncestor graph visited p target)

/// Every resolvable `VerbCall`'s `(receiver start, call name)` pair,
/// corpus-wide - deliberately *not* filtered through `findCallableVerb`
/// (unlike `computeReferenceResolution`'s `confirmedTargets`): that
/// function's own `findOwnVerb` already excludes non-executable verbs
/// before matching by name, so a verb missing the `x` bit can never appear
/// in `confirmedTargets` no matter how many callers name it - the exact
/// case the missing-x-bit check exists to catch. This collects the raw
/// receiver+name instead, so `findGotchas` can check name-match and
/// ancestor-reachability itself, independent of executability.
let private allResolvableCallSites (graph: Graph) : (ObjRef * string) seq =
    allVerbCallReferences graph
    |> Seq.choose (fun (containingObj, _, r) ->
        match r.Ref with
        | AstQuery.RefVerbCall(receiver, StrLit callName, _) ->
            Metadata.Resolver.resolveReceiverInContext graph containingObj receiver
            |> Option.map (fun receiverStart -> receiverStart, callName)
        | _ -> None)

/// Static, whole-corpus checks for the gotchas already catalogued in the
/// project plan doc but never turned into tooling - cheap to check
/// exhaustively precisely because a MOO codebase is finite (the same
/// property `findDeadVerbs`/the M4 resolver both lean on). Reports at verb
/// granularity, not a specific line/column - `Ast.fs`'s statement nodes
/// (unlike its four reference-bearing expression nodes) don't carry source
/// positions, so there's nothing more precise to report without extending
/// the parser itself, out of scope here. Deliberately not `private`, same
/// reasoning as `findDeadVerbs`.
///
/// Missing-x-bit note: this doesn't replicate `findCallableVerb`'s exact
/// first-match-wins dispatch order (which would need checking whether some
/// *other*, executable, same-named verb sits closer in the ancestor chain
/// and would shadow this one anyway) - it flags any non-executable verb
/// whose own name matches some resolvable call site whose receiver can
/// reach this verb's defining object. A verb genuinely shadowed by a
/// working same-named verb closer up the chain would still be flagged here
/// as a false positive - same disclosed-approximation trade-off
/// `DeadVerbEntry.PossiblyDynamic` already makes elsewhere in this file,
/// not a precision this check claims.
let findGotchas (graph: Graph) : GotchaEntry[] =
    let resolvableCallSites = allResolvableCallSites graph |> Array.ofSeq

    let missingXBit =
        graph.Objects
        |> Map.toSeq
        |> Seq.collect (fun (num, o) ->
            o.Verbs
            |> Seq.choose (fun v ->
                match v.Meta.Names with
                | primary :: _ when
                    not (v.Meta.Perms.Contains 'x')
                    && resolvableCallSites
                       |> Array.exists (fun (receiverStart, callName) ->
                           Metadata.Resolver.verbNameMatchesAny v.Meta.Names callName
                           && isReachableAncestor graph (System.Collections.Generic.HashSet()) receiverStart num)
                    ->
                    Some { ObjRef = num; VerbName = primary; Kind = "missing-x-bit" }
                | _ -> None))

    let structural =
        graph.Objects
        |> Map.toSeq
        |> Seq.collect (fun (num, o) ->
            o.Verbs
            |> Seq.collect (fun v ->
                match v.Ast, v.Meta.Names with
                | Some stmts, primary :: _ ->
                    seq {
                        if allLoops stmts |> Seq.exists (function ForList(_, _, _, b) | ForRange(_, _, _, b) | While(_, _, b) -> loopBodyNeedsSuspend b | _ -> false) then
                            yield { ObjRef = num; VerbName = primary; Kind = "unbounded-loop" }

                        if stmts |> List.collect (stmtExprs true >> List.ofSeq) |> List.exists (existsInExpr isZeroIndex) then
                            yield { ObjRef = num; VerbName = primary; Kind = "zero-index" }
                    }
                | _ -> Seq.empty))

    Seq.append missingXBit structural |> Array.ofSeq

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
                        |> List.map (fun name -> mkCompletionItem name CompletionItemKind.Variable None)

                    let! liveBuiltins = bridge.GetBuiltins() |> Async.AwaitTask

                    let builtinItems =
                        liveBuiltins
                        |> Map.toList
                        |> List.map (fun (name, fn) -> mkCompletionItem name CompletionItemKind.Function (Some(builtinHoverText fn)))

                    let verbItems =
                        AstQuery.nearestReferenceAtOrBefore (lspLine + 1) (lspCol + 1) stmts
                        |> Option.bind (fun r ->
                            match r.Ref with
                            | AstQuery.RefVerbCall(receiver, _, _) -> Metadata.Resolver.resolveReceiverInContext graph enclosingObj receiver
                            | _ -> None)
                        |> Option.map (fun startObj ->
                            Metadata.Resolver.allCallableVerbNames graph startObj
                            |> List.map (fun name -> mkCompletionItem name CompletionItemKind.Method None))
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
                              Documentation =
                                fn.Description
                                |> Option.map (fun text -> U2.C2 { Kind = MarkupKind.Markdown; Value = text })
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

    /// Custom method (`moodev/findGotchas`, no params) - the "MOOcode
    /// gotchas" static-check report: every verb `findGotchas` flags for a
    /// missing `x` bit despite a confirmed caller, an unbounded loop with no
    /// `suspend()`, or a `list[0]`-shaped index, corpus-wide.
    member _.FindGotchas(_p: obj) : Async<Result<GotchaEntry[], JsonRpc.Error>> =
        async { return Ok(findGotchas graph) }

    /// Custom method (`moodev/getMoocodeDocs`, no params) - the full catalog
    /// for the client's docs sidebar: every control keyword, implicit
    /// variable, and live builtin, one flat searchable list (`moocodeDocs`).
    member _.GetMoocodeDocs(_p: obj) : Async<Result<MoocodeDocEntry[], JsonRpc.Error>> =
        async {
            let! liveBuiltins = bridge.GetBuiltins() |> Async.AwaitTask
            return Ok(moocodeDocs liveBuiltins)
        }
