/// Loads a `Schema.Graph` from a `Survive`-shaped export tree per
/// `FORMAT.md` (repo root) (`corponyms.moo` + one
/// `objects/<name>/object.moo` + one file per verb, parsed via
/// `Metadata.TreeFormat`), plus `builtins.json` (a separate, static,
/// server-version-specific capture unrelated to the tree format itself),
/// and the captured verb source, parsed through `Language.Parser`. Live MOO
/// export, not a raw `.db` parser - see the M4 plan's "Decision: metadata
/// graph via live-server export" for why (version-proof against the
/// checkpoint format, reuses builtins like `parents()` that already do the
/// multi-inheritance-aware work).
module Metadata.Loader

open System
open System.IO
open System.Text.Json
open Language.Lexer
open Language.Parser
open Metadata.Schema
open Metadata.TreeFormat

/// `builtin-param-names.json` is embedded in this assembly (see
/// `Metadata.fsproj`) - a static, build-time extraction from ToastStunt's
/// own C source comments, not per-`Survive`-checkout data. Not `private`:
/// `LanguageServer.SidecarBridge` reuses this (and `loadBuiltinDescriptions`/
/// `parseBuiltinFunc` below) to merge these same static docs onto live
/// `get-builtins` responses, so hover text is identical whether the arity/
/// type data came from `builtins.json` or a live query.
let loadBuiltinParamNames () : Map<string, string list> =
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let resourceName = "Metadata.builtin-param-names.json"

    use stream = asm.GetManifestResourceStream(resourceName)

    if isNull stream then
        Map.empty
    else
        use reader = new StreamReader(stream)
        use doc = JsonDocument.Parse(reader.ReadToEnd())

        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, (p.Value.EnumerateArray() |> Seq.map (fun s -> s.GetString()) |> List.ofSeq))
        |> Map.ofSeq

/// `builtin-descriptions.json` is embedded the same way `builtin-param-names.json`
/// is - one-line "what does this do" hover text, hand-written per builtin
/// (not extracted from source comments the way param names are, since
/// there's no equivalent doc-comment convention for behavior).
let loadBuiltinDescriptions () : Map<string, string> =
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let resourceName = "Metadata.builtin-descriptions.json"

    use stream = asm.GetManifestResourceStream(resourceName)

    if isNull stream then
        Map.empty
    else
        use reader = new StreamReader(stream)
        use doc = JsonDocument.Parse(reader.ReadToEnd())

        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.GetString())
        |> Map.ofSeq

let parseBuiltinFunc
    (paramNames: Map<string, string list>)
    (descriptions: Map<string, string>)
    (el: JsonElement)
    : BuiltinFunc =
    let name = el.GetProperty("name").GetString()

    { Name = name
      MinArgs = el.GetProperty("minargs").GetInt32()
      MaxArgs = el.GetProperty("maxargs").GetInt32()
      ArgTypes = el.GetProperty("types").EnumerateArray() |> Seq.map (fun t -> t.GetInt32()) |> List.ofSeq
      ParamNames = Map.tryFind name paramNames
      Description = Map.tryFind name descriptions }

/// `builtins.json` is a static, server-version-specific capture (which
/// builtins this ToastStunt fork registers, and their arity/arg types) -
/// unrelated to `FORMAT.md`'s tree format and not written by any part of
/// the current sidecar-owned export pipeline (it was a `$vcs`-only capture,
/// `export_builtins.moo`, now retired along with the rest of `$vcs`). Its
/// absence degrades gracefully to an empty map rather than failing the
/// whole load - unlike the tree files below, which are mandatory per
/// `FORMAT.md` and fail loudly if missing/malformed.
let private loadBuiltins (path: string) : Map<string, BuiltinFunc> =
    if not (File.Exists path) then
        Map.empty
    else
        let paramNames = loadBuiltinParamNames ()
        let descriptions = loadBuiltinDescriptions ()
        use doc = JsonDocument.Parse(File.ReadAllText path)

        doc.RootElement.GetProperty("functions").EnumerateArray()
        |> Seq.map (fun el ->
            let b = parseBuiltinFunc paramNames descriptions el
            b.Name, b)
        |> Map.ofSeq

/// Parses one verb's already-read source lines into an AST/token stream.
/// Unlike the old format (where `metadata.json` could know about a verb
/// never captured to disk, so `SourcePath` was sometimes `None`), a verb
/// only exists in the tree at all if it has a file - this always produces a
/// populated `SourcePath`/`Ast`/`Tokens`, barring a genuine lex error in the
/// captured source itself.
let private loadVerb (definedOn: ObjRef) (index: int) (pv: ParsedVerb, sourcePath: string) : VerbNode =
    let names = pv.Names.Split(' ') |> Array.filter (fun s -> s <> "") |> List.ofArray
    let sourceText = String.concat "\n" pv.Code
    let lexResult = tokenize sourceText

    let ast, diagCount, tokens =
        match lexResult.Error with
        | Some _ -> None, 0, None
        | None ->
            let stmts = parse lexResult.Tokens
            Some stmts, Language.Ast.countErrors stmts, Some lexResult.Tokens

    { Meta =
        { Index = index
          Names = names
          Owner = pv.Owner
          Perms = pv.Perms
          Dobj = pv.Dobj
          Prep = pv.Prep
          Iobj = pv.Iobj }
      DefinedOn = definedOn
      SourcePath = Some sourcePath
      Ast = ast
      DiagnosticCount = diagCount
      Tokens = tokens }

let private loadObjectFlags (flags: string list) : ObjectFlags =
    { Player = List.contains "player" flags
      Programmer = List.contains "programmer" flags
      Wizard = List.contains "wizard" flags
      Read = List.contains "r" flags
      Write = List.contains "w" flags
      Fertile = List.contains "f" flags
      Anonymous = List.contains "a" flags }

/// Resolves a `parents:` token against the corponym map. `$name` must
/// resolve to a real entry - per `FORMAT.md` §7's "fail loudly on a missing
/// corponym; do not guess", a dangling reference is a real bug in the tree,
/// not a normal condition to route around silently.
let private resolveParentRef (corponymToObjnum: Map<string, int64>) (p: ParentRef) : int64 =
    match p with
    | ByObjnum n -> n
    | ByCorponym name ->
        match Map.tryFind name corponymToObjnum with
        | Some n -> n
        | None -> failwithf "Dangling parent reference: $%s has no entry in corponyms.moo" name

/// Shared between every corponym-bearing object and FORMAT.md §1's `#0`
/// exception (which has no corponym, hence the separate `nameOpt` rather
/// than looking it up here).
let private buildObjectNode
    (corponymToObjnum: Map<string, int64>)
    (num: ObjRef)
    (nameOpt: string option)
    (po: ParsedObject)
    : ObjectNode =
    let parents = po.Parents |> List.map (resolveParentRef corponymToObjnum)
    let verbs = po.Verbs |> List.mapi (fun i verbAndPath -> loadVerb num (i + 1) verbAndPath)

    let properties =
        po.Properties
        |> List.map (fun p -> { Name = p.Name; Owner = p.Owner; Perms = p.Perms })

    { Num = num
      Name = nameOpt
      // Empty-string normalizes to `None` here (not in `ParsedObject`/
      // `ObjectExport`, which stay plain mirrors of on-disk/live state) -
      // preserves this field's pre-existing documented contract that `None`
      // means "genuinely empty `.name`", same as the old $vcs-based system.
      LiveName = po.Name |> Option.filter (fun s -> s <> "")
      Parents = parents
      Children = [] // filled by the post-pass below
      Verbs = verbs
      Owner = Some po.Owner
      Flags = Some(loadObjectFlags po.Flags)
      Properties = properties
      Aliases = po.Aliases }

/// Reads `<surviveRoot>`'s export tree (`corponyms.moo` + `objects/*/`, per
/// `FORMAT.md`) and `<surviveRoot>/builtins.json`, parsing every verb's
/// captured source along the way, into one `Graph`.
let load (surviveRoot: string) : Graph =
    let builtins = loadBuiltins (Path.Combine(surviveRoot, "builtins.json"))
    let corponyms, parsedObjects = parseTree surviveRoot
    let corponymToObjnum = corponyms |> Map.ofList

    // `buildObjectNode` (and everything it calls - `loadVerb`, `tokenize`,
    // `parse`, `resolveParentRef`) is side-effect-free and reads only
    // immutable inputs (`corponymToObjnum`/`parsedObjects` are plain F#
    // `Map`s, safe for concurrent reads), so every corponym's object node
    // - dominated by tokenizing/parsing every verb's captured source - can
    // build independently in parallel. Confirmed live: this was the actual
    // bottleneck behind a 90-120s LSP startup against a real, large
    // (425-object, 17MB) world - not a quadratic algorithm (this file
    // already used `Map` throughout), just hundreds of sequential
    // tokenize+parse calls on one thread. `Array.Parallel.map` (not
    // `.choose`, which isn't guaranteed present in every FSharp.Core
    // version) + a plain `Array.choose id` keeps the same "skip corponyms
    // with no matching parsed object" behavior the old `List.choose` had.
    //
    // `Array.Parallel.map` wraps any worker exception (e.g.
    // `resolveParentRef`'s dangling-`$name` `failwithf`) in an
    // `AggregateException` - unwrapped back to the original exception here
    // so `load`'s own external contract (throws the real error, not a TPL
    // implementation detail) stays unchanged regardless of execution
    // strategy.
    let fromCorponyms =
        try
            corponyms
            |> List.toArray
            |> Array.Parallel.map (fun (name, num) ->
                match Map.tryFind name parsedObjects with
                | None -> None
                | Some po -> Some(num, buildObjectNode corponymToObjnum num (Some name) po))
            |> Array.choose id
            |> Map.ofArray
        with :? AggregateException as ex -> raise (if isNull ex.InnerException then ex :> exn else ex.InnerException)

    // FORMAT.md §1's `#0` exception: folded in whenever present, regardless
    // of having a corponym (it never does - corponyms are properties ON #0
    // pointing elsewhere, not at itself).
    let objectsWithoutChildren =
        match Map.tryFind "0" parsedObjects with
        | None -> fromCorponyms
        | Some po -> Map.add 0L (buildObjectNode corponymToObjnum 0L None po) fromCorponyms

    // `Children` isn't recorded anywhere in the new format (`FORMAT.md` §3
    // only has `parents:`) - compute it by inverting every loaded object's
    // Parents. Only meaningful among objects that are themselves loaded
    // (corponym-bearing), matching the old system's own scope - nothing
    // dispatch-critical reads this (`Resolver.fs` only ever reads Parents),
    // only `Handlers.fs`'s object inspector display.
    let childrenByParent =
        objectsWithoutChildren
        |> Map.toList
        |> List.collect (fun (num, node) -> node.Parents |> List.map (fun p -> p, num))
        |> List.groupBy fst
        |> List.map (fun (parent, children) -> parent, children |> List.map snd)
        |> Map.ofList

    let objects =
        objectsWithoutChildren
        |> Map.map (fun num node ->
            { node with Children = Map.tryFind num childrenByParent |> Option.defaultValue [] })

    { Objects = objects
      SystemObjectProperties = corponymToObjnum
      Builtins = builtins }
