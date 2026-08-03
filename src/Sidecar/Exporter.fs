/// Phase 1 of moo-vcs-plan.md: a read-only exporter that walks `#0`'s
/// corponyms, resolves objects, and emits an on-disk tree per
/// `FORMAT.md` (repo root) - `corponyms.moo`, one `object.moo`
/// per corponym-bearing object, and one file per verb. Talks to the MOO
/// only through `MooEval` (standard builtins over a wizard connection, no
/// custom in-MOO verb), per invariant I1.
module Sidecar.Exporter

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// ---------------------------------------------------------------------------
// In-memory shape. Deliberately not `Metadata/Schema.fs` - that module is
// tied to the metadata.json/lookups.toml pipeline being retired and carries
// LSP-only concerns (Ast, Tokens, SourcePath) that don't belong here.
// ---------------------------------------------------------------------------

type PropertyExport =
    { Name: string
      Owner: int64
      Perms: string
      /// `toliteral()`-rendered value text, written to disk verbatim and
      /// read back with `eval("return " + line + ";")` on import - the same
      /// round-trip the retired `$vcs` IDE property verbs already used.
      ValueLiteral: string }

type VerbExport =
    { /// Full space-separated name-spec, declaration order preserved -
      /// canonical identity, lives in the `@verb` header, never the filename.
      Names: string
      Owner: int64
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string
      /// `verb_code(obj, index, 0, 1)` output, one MOO line per element.
      Code: string list }

type ObjectExport =
    { /// Declaration order preserved - ancestor search order, not cosmetic.
      Parents: int64 list
      Owner: int64
      Flags: string list
      /// Declaration order as returned by `properties(obj)` - re-sorted by
      /// name before rendering (see `FORMAT.md` §6).
      Properties: PropertyExport list
      /// Declaration order as returned by `verbs(obj)` - preserved exactly,
      /// both for the `object.moo` `verbs:` manifest and each verb file's
      /// on-disk order-of-writing (dispatch is first-match-wins).
      Verbs: VerbExport list
      /// The object's live `.name` value, fetched directly rather than via
      /// `properties(obj)` (see `getObjectExport`'s own comment for why) -
      /// display-only, for the tree/inspector's benefit. `""` for a
      /// genuinely empty `.name`; normalized to `None` only at
      /// `Metadata/Loader.fs`'s layer, not here (this type stays a plain
      /// mirror of live state). Fetched but not diffed by
      /// `Importer.fs`'s `planObject` - same deliberate scope as the
      /// existing `Owner`/`Flags` fields, which also have no corresponding
      /// `*Op` promotion type.
      LiveName: string
      /// The object's live `.aliases` value, same direct-fetch reasoning
      /// and same fetched-but-undiffed-by-import scope as `LiveName`. `[]`
      /// for no aliases.
      Aliases: string list }

// ---------------------------------------------------------------------------
// MOO-side queries. One eval per corponym-bearing object (not per
// verb/property) - see the Phase 1 plan's decision #2.
// ---------------------------------------------------------------------------

/// Runs `statements` then reports `resultExpr` back as JSON - the shared
/// two-part shape both `MooEval.runAndAwaitJson` (a dedicated wizard
/// connection, used by the batch CLI tools) and `BridgeHandler.evalOnSession`
/// (the browser's own live connection, used by `IdeActions`) already
/// implement. Exporter's queries take this instead of a concrete
/// `MooEval.Connection` so `IdeActions.saveVerb` can reuse the browser
/// session's own connection for its read-back-and-render step rather than
/// opening a second wizard connection - opening a second `connect wizard`
/// while the browser session is *also* logged in as the wizard (the normal
/// case for this single-developer tool) makes ToastStunt treat the second
/// login as a reconnect of the same player and drop the first connection,
/// silently killing the browser's own session out from under it (found live
/// during Phase 4 verification).
type EvalRunner = string -> string -> CancellationToken -> Task<JsonDocument>

/// `for pname in (properties(#0)) if (typeof(#0.(pname)) == OBJ) ...` -
/// exactly what the retired `export_metadata.moo` already computed as
/// `sysobj_props`. Returns objnum -> corponym name (note: inverse of the
/// on-disk map's own key order) so the exporter can look up "does this
/// referenced object have a corponym" while building `object.moo`/`@verb`
/// lines.
let getCorponyms (evalRunner: EvalRunner) (ct: CancellationToken) : Task<Map<int64, string>> =
    task {
        let statements =
            """corps = [];
for pname in (properties(#0))
  if (typeof(#0.(pname)) == OBJ)
    corps[pname] = tostr(#0.(pname));
  endif
endfor"""

        let! json = evalRunner statements "corps" ct
        let root = json.RootElement

        return
            root.EnumerateObject()
            |> Seq.map (fun prop ->
                // Values are "#123" strings (tostr() of an OBJ) - strip the
                // leading '#' and parse the number.
                let objnumText = prop.Value.GetString().TrimStart('#')
                int64 objnumText, prop.Name)
            |> Map.ofSeq
    }

/// One entry from `function_info()` (no args) - every builtin ToastStunt
/// registers, confirmed against `ToastStunt/src/functions.cc`'s
/// `bf_function_info`/`function_description`. Replaces the retired
/// `$vcs:export_builtins()` MOOcode verb as the source of `builtins.json` -
/// `Metadata/Loader.fs`'s `loadBuiltins`/`parseBuiltinFunc` and
/// `LanguageServer/Handlers.fs`'s hover text were both already written
/// against this exact shape and never needed to change, only the producer
/// did. `Types` is already the "masked, user-visible" encoding
/// (`proto < 0 ? proto : (proto & TYPE_DB_MASK)`, per `functions.cc`) that
/// `Handlers.fs`'s `typeName` expects - no transformation needed.
type BuiltinFuncExport =
    { Name: string
      MinArgs: int
      MaxArgs: int
      Types: int list }

/// One eval, connection-wide (not per-object) - every registered builtin's
/// name/arity/arg-type-protos, straight from the live server.
let getBuiltinFunctions (evalRunner: EvalRunner) (ct: CancellationToken) : Task<BuiltinFuncExport list> =
    task {
        let statements =
            """funcs = {};
for entry in (function_info())
  funcs = {@funcs, ["name" -> entry[1], "minargs" -> entry[2], "maxargs" -> entry[3], "types" -> entry[4]]};
endfor"""

        let! json = evalRunner statements "funcs" ct

        return
            json.RootElement.EnumerateArray()
            |> Seq.map (fun el ->
                { Name = el.GetProperty("name").GetString()
                  MinArgs = el.GetProperty("minargs").GetInt32()
                  MaxArgs = el.GetProperty("maxargs").GetInt32()
                  Types = el.GetProperty("types").EnumerateArray() |> Seq.map (fun t -> t.GetInt32()) |> List.ofSeq })
            |> List.ofSeq
    }

/// `builtins.json`'s on-disk shape - a `"functions"` array, each element
/// `name`/`minargs`/`maxargs`/`types`, exactly matching `Loader.fs`'s
/// `parseBuiltinFunc` field-for-field (no renaming step exists anywhere in
/// this pipeline).
let renderBuiltinsJson (functions: BuiltinFuncExport list) : string =
    let payload =
        {| functions =
             functions
             |> List.map (fun f -> {| name = f.Name; minargs = f.MinArgs; maxargs = f.MaxArgs; types = f.Types |})
             |> List.ofSeq |}

    JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))

let private getString (el: JsonElement) (name: string) = el.GetProperty(name).GetString()
let private getInt64 (el: JsonElement) (name: string) = int64 (el.GetProperty(name).GetString().TrimStart('#'))

let private parseVerb (el: JsonElement) : VerbExport =
    { Names = getString el "names"
      Owner = getInt64 el "owner"
      Perms = getString el "perms"
      Dobj = getString el "dobj"
      Prep = getString el "prep"
      Iobj = getString el "iobj"
      Code = el.GetProperty("code").EnumerateArray() |> Seq.map (fun l -> l.GetString()) |> List.ofSeq }

let private parseProperty (el: JsonElement) : PropertyExport =
    { Name = getString el "name"
      Owner = getInt64 el "owner"
      Perms = getString el "perms"
      ValueLiteral = getString el "value" }

/// Fetches everything needed to write one object's `object.moo` and all its
/// verb files, in a single eval. Returns `None` if the corponym turned out
/// to point at a no-longer-valid object (recycled since the corponym map was
/// read) - the caller skips it with a warning rather than crashing, since
/// invariant I2 explicitly anticipates objnum/identity drift.
///
/// The `.aliases` fetch below is wrapped in `try`/`except (E_PROPNF)`, unlike
/// `.name` - `.name` is a genuine C-level built-in property (`db.h`'s
/// `BUILTIN_PROPERTIES`), always present on every object, but `.aliases` is
/// only a MOOcode convention inherited from wherever it's declared (usually
/// `$root_class`). An object with no parents at all - confirmed live: real
/// ToastCore's own `$anon` prototype (`#118`) - genuinely has no `.aliases`
/// anywhere in its (empty) ancestry, so this must be caught, not assumed
/// present. (Note for future edits to the MOO source below: MOOcode has no
/// comment syntax at all - a stray `//` here would silently corrupt the eval
/// statement, not get stripped, and manifests as this entire eval hanging
/// forever rather than a visible parse error - confirmed live, the hard way.)
let getObjectExport
    (evalRunner: EvalRunner)
    (objRef: int64)
    (ct: CancellationToken)
    : Task<ObjectExport option> =
    task {
        let o = sprintf "#%d" objRef

        let statements =
            $"""if (!valid({o}))
  result = ["error" -> "invalid"];
else
  parents_list = {{}};
  for p in (parents({o})) parents_list = {{@parents_list, tostr(p)}}; endfor
  flags = {{}};
  if (is_player({o})) flags = {{@flags, "player"}}; endif
  if ({o}.programmer) flags = {{@flags, "programmer"}}; endif
  if ({o}.wizard) flags = {{@flags, "wizard"}}; endif
  if ({o}.r) flags = {{@flags, "r"}}; endif
  if ({o}.w) flags = {{@flags, "w"}}; endif
  if ({o}.f) flags = {{@flags, "f"}}; endif
  if ({o}.a) flags = {{@flags, "a"}}; endif
  props = {{}};
  for pn in (properties({o}))
    pi = property_info({o}, pn);
    props = {{@props, ["name" -> pn, "owner" -> tostr(pi[1]), "perms" -> pi[2], "value" -> toliteral({o}.(pn))]}};
  endfor
  vout = {{}};
  vlist = verbs({o});
  for i in [1..length(vlist)]
    vi = verb_info({o}, i);
    va = verb_args({o}, i);
    code = verb_code({o}, i, 0, 1);
    vout = {{@vout, ["names" -> vi[3], "owner" -> tostr(vi[1]), "perms" -> vi[2], "dobj" -> va[1], "prep" -> va[2], "iobj" -> va[3], "code" -> code]}};
  endfor
  live_name = typeof({o}.name) == STR ? {o}.name | "";
  alias_list = {{}};
  try
    if (typeof({o}.aliases) == LIST)
      for a in ({o}.aliases)
        if (typeof(a) == STR)
          alias_list = {{@alias_list, a}};
        endif
      endfor
    endif
  except (E_PROPNF)
  endtry
  result = ["parents" -> parents_list, "owner" -> tostr({o}.owner), "flags" -> flags, "properties" -> props, "verbs" -> vout, "name" -> live_name, "aliases" -> alias_list];
endif"""

        let! json = evalRunner statements "result" ct
        let root = json.RootElement
        let hasError, _ = root.TryGetProperty("error")

        if hasError then
            return None
        else
            let parents = root.GetProperty("parents").EnumerateArray() |> Seq.map (fun p -> int64 (p.GetString().TrimStart('#'))) |> List.ofSeq
            let flags = root.GetProperty("flags").EnumerateArray() |> Seq.map (fun f -> f.GetString()) |> List.ofSeq
            let properties = root.GetProperty("properties").EnumerateArray() |> Seq.map parseProperty |> List.ofSeq
            let verbs = root.GetProperty("verbs").EnumerateArray() |> Seq.map parseVerb |> List.ofSeq
            let aliases = root.GetProperty("aliases").EnumerateArray() |> Seq.map (fun a -> a.GetString()) |> List.ofSeq

            return
                Some
                    { Parents = parents
                      Owner = int64 (root.GetProperty("owner").GetString().TrimStart('#'))
                      Flags = flags
                      Properties = properties
                      Verbs = verbs
                      LiveName = getString root "name"
                      Aliases = aliases }
    }

/// One eval, connection-wide - `tostr(max_object())` (see `parseVerb`'s
/// sibling helpers: an OBJ value never survives `generate_json()` bare, it
/// must be `tostr()`'d first, same as every other objnum this file emits).
/// Only used to size/report progress on `getAnonVerbs`'s chunked scan below.
let getMaxObject (evalRunner: EvalRunner) (ct: CancellationToken) : Task<int64> =
    task {
        let! json = evalRunner "" "[\"max\" -> tostr(max_object())]" ct
        return int64 ((getString json.RootElement "max").TrimStart('#'))
    }

/// Non-corified verb capture tier, one chunk of the objnum range at a time
/// (FORMAT.md §5's enumeration fallback, documented but never previously
/// used to *keep* anything - only to filter non-corified objects out).
/// Returns directly-defined verb data for every valid object in
/// `[loObj..hiObj]` that has no corponym (per `corponymObjnums`) and at
/// least one directly-defined verb - `#0` is never included, since it's
/// always handled separately (normally corponym'd, or via its own
/// export-time exception). Most objects in this range have zero
/// directly-defined verbs, so the response payload stays bounded by the
/// actual anon-verb-bearing population rather than chunk size.
///
/// Chunked (by `exportTree`'s caller loop) rather than one single
/// `[#1..max_object()]` eval covering the whole world: a world with
/// hundreds of thousands of objects turns that into one MOO-side loop long
/// enough to look indistinguishable from a hang (no output at all until it
/// either returns or the connection's read loop times out), and risks
/// running into ToastStunt's own foreground task time/tick limits on a
/// truly large world. Chunking trades that for periodic round trips
/// `exportTree` can report progress on.
let getAnonVerbs
    (evalRunner: EvalRunner)
    (corponymObjnums: Set<int64>)
    (loObj: int64)
    (hiObj: int64)
    (ct: CancellationToken)
    : Task<(int64 * VerbExport list) list> =
    task {
        let corponymNumsLiteral =
            corponymObjnums |> Set.toList |> List.map (sprintf "#%d") |> String.concat ", " |> sprintf "{%s}"

        let statements =
            $"""corponym_nums = {corponymNumsLiteral};
anon = {{}};
for i in [#{loObj}..#{hiObj}]
  if (valid(i) && !(i in corponym_nums))
    vlist = verbs(i);
    if (length(vlist) > 0)
      vout = {{}};
      for j in [1..length(vlist)]
        vi = verb_info(i, j);
        va = verb_args(i, j);
        code = verb_code(i, j, 0, 1);
        vout = {{@vout, ["names" -> vi[3], "owner" -> tostr(vi[1]), "perms" -> vi[2], "dobj" -> va[1], "prep" -> va[2], "iobj" -> va[3], "code" -> code]}};
      endfor
      anon = {{@anon, ["objnum" -> tostr(i), "verbs" -> vout]}};
    endif
  endif
endfor"""

        let! json = evalRunner statements "anon" ct
        let root = json.RootElement

        return
            root.EnumerateArray()
            |> Seq.map (fun el ->
                let objnum = int64 ((getString el "objnum").TrimStart('#'))
                let verbs = el.GetProperty("verbs").EnumerateArray() |> Seq.map parseVerb |> List.ofSeq
                objnum, verbs)
            |> List.ofSeq
    }

/// Object-count width of one `getAnonVerbs` round trip - see that function's
/// own comment for why the scan is chunked at all. 10,000 keeps each
/// round-trip's MOO-side loop well short of typical foreground task limits
/// while still giving frequent progress output on a large world.
let private anonVerbChunkSize = 10_000L

/// Resolves a FORMAT.md tree-relative path to `(corponym, label)` - shared by
/// `IdeActions.searchHistory` (labeling a pickaxe-search hit) and
/// `Promotion.diffSummary` (labeling a production/main tree diff), so the
/// path-shape knowledge lives in exactly one place. `"(properties)"` is a
/// stand-in label for `object.moo` itself, not the exact property name
/// (which needs parsing the blob at a specific commit - not worth it for
/// either caller). `None` for paths outside `objects/<corponym>/...`
/// entirely (`corponyms.moo`, `FORMAT_VERSION`).
///
/// `objects/_anon/<objnum>/verbs/...` (the non-corified verb capture tier)
/// resolves to `"#" + objnum` in the corponym slot rather than a real
/// corponym name - deliberately, since MOO property names can never start
/// with `#`, so this can never collide with a real corponym string. Every
/// downstream consumer (`GitStore.buildCommitMessage`, the client's search
/// result renderers) distinguishes "anon label" from "real corponym" by
/// checking this same `#`-prefix convention, the same raw-objnum-reference
/// shape `refText` already uses elsewhere in this file.
let describePath (path: string) : (string * string) option =
    match path.Split('/') with
    | [| "objects"; corponym; "object.moo" |] -> Some(corponym, "(properties)")
    | [| "objects"; corponym; "verbs"; fileName |] -> Some(corponym, Path.GetFileNameWithoutExtension(fileName: string))
    | [| "objects"; "_anon"; objnumStr; "verbs"; fileName |] -> Some("#" + objnumStr, Path.GetFileNameWithoutExtension(fileName: string))
    | _ -> None

// ---------------------------------------------------------------------------
// Filename derivation - ports Survive/VCS/1_sanitize_name.moo verbatim.
// ---------------------------------------------------------------------------

let sanitizeName (raw: string) : string =
    let replacements = [ " ", "_"; "*", ""; "/", ""; "\\", ""; ":", ""; "\"", ""; "<", ""; ">", ""; "|", ""; "?", "" ]
    replacements |> List.fold (fun (s: string) (find, repl) -> s.Replace(find, repl)) raw

/// First alias of a verb's name-spec, `*` stripped and sanitized, falling
/// back to "verb" if that leaves nothing - mirrors the retired `$vcs`
/// capture logic's fallback, but always derives from the canonical first
/// alias (declaration order), never "whichever alias triggered this edit" -
/// that's the exact bug `FORMAT.md` §4 documents and fixes.
let private baseVerbFileName (names: string) : string =
    let firstAlias = names.Split(' ').[0]
    let sanitized = sanitizeName firstAlias
    if sanitized = "" then "verb" else sanitized

/// Assigns disk filenames to a list of verbs in declaration order, adding a
/// numeric suffix on collision (matching main-plan §5's filename rule).
let assignVerbFileNames (verbs: VerbExport list) : (VerbExport * string) list =
    let used = Collections.Generic.HashSet<string>()

    verbs
    |> List.map (fun v ->
        let baseName = baseVerbFileName v.Names
        let mutable candidate = baseName
        let mutable suffix = 1

        while used.Contains(candidate) do
            suffix <- suffix + 1
            candidate <- sprintf "%s_%d" baseName suffix

        used.Add(candidate) |> ignore
        v, candidate + ".moo")

// ---------------------------------------------------------------------------
// Rendering - text per FORMAT.md's grammar.
// ---------------------------------------------------------------------------

/// `$name` if `objRef` has a corponym, else raw `#objnum` - used for
/// `parents:` only (see FORMAT.md §3: owners are always raw, never
/// corponym-resolved).
let private refText (corponymsByObjnum: Map<int64, string>) (objRef: int64) : string =
    match Map.tryFind objRef corponymsByObjnum with
    | Some name -> "$" + name
    | None -> sprintf "#%d" objRef

/// Escapes `\` and `"` so a name (a property name, or a verb's space-separated
/// name-spec) can be embedded in a quoted FORMAT.md field even if it contains
/// one of those characters itself - real ToastCore content does this (e.g.
/// `$help` has a property literally named `"`, the help topic for quoting
/// syntax). Paired with `TreeParser`/`TreeFormat`'s own quote-aware field
/// readers, which undo this same escaping.
let private escapeQuotedField (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"")

/// Renders `aliases:`'s value - one independently-escaped quoted token per
/// alias, space-separated between tokens, NOT a single space-joined field
/// like a verb's name-spec. Real `.aliases` entries are routinely multi-word
/// phrases (confirmed against ToastStunt's own `parse_cmd.cc`/`match.cc`:
/// the command parser joins multiple typed words into the dobj/iobj match
/// target before comparing it against each alias, e.g. "brass lantern" is
/// ordinary alias content) - joining/splitting on space the way verb
/// name-specs do would silently corrupt any multi-word alias.
let private renderQuotedFieldList (items: string list) : string =
    items |> List.map (fun s -> sprintf "\"%s\"" (escapeQuotedField s)) |> String.concat " "

let renderCorponymsMoo (corponyms: (string * int64) list) : string =
    corponyms
    |> List.sortWith (fun (a, _) (b, _) -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase))
    |> List.map (fun (name, num) -> sprintf "%s #%d" name num)
    |> String.concat "\n"
    |> fun s -> s + "\n"

/// `selfRefText` is the already-formatted self-reference for the `@object`
/// line - `"$" + corponym` for a normal object, or the raw `"#0"` for the
/// one FORMAT.md §1 exception (`#0` has no corponym of its own to render).
let renderObjectMoo
    (corponymsByObjnum: Map<int64, string>)
    (selfRefText: string)
    (data: ObjectExport)
    (verbFileNames: (VerbExport * string) list)
    : string =
    // Explicit "\n" joining throughout, not StringBuilder.AppendLine (which
    // uses Environment.NewLine) - invariant I4 calls line-ending stability
    // out by name as the thing most likely to quietly wreck this project,
    // so output must not depend on which OS the exporter happens to run on.
    let lines = ResizeArray<string>()
    lines.Add(sprintf "@object %s" selfRefText)

    let parentsText =
        data.Parents |> List.map (refText corponymsByObjnum) |> String.concat " "

    lines.Add(sprintf "parents: %s" parentsText)
    lines.Add(sprintf "owner: #%d" data.Owner)
    lines.Add(sprintf "flags: %s" (String.concat " " data.Flags))
    lines.Add(sprintf "name: \"%s\"" (escapeQuotedField data.LiveName))
    lines.Add(sprintf "aliases: %s" (renderQuotedFieldList data.Aliases))

    let verbFilesText = verbFileNames |> List.map snd |> String.concat " "
    lines.Add(sprintf "verbs: %s" verbFilesText)

    let sortedProps =
        data.Properties
        |> List.sortWith (fun a b -> String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))

    for p in sortedProps do
        lines.Add("")
        lines.Add(sprintf "@property \"%s\" owner=#%d perms=%s" (escapeQuotedField p.Name) p.Owner p.Perms)
        lines.Add(p.ValueLiteral)
        lines.Add(".")

    String.concat "\n" lines + "\n"

/// `selfRefText` - see `renderObjectMoo`'s own comment; same convention.
let renderVerbFile (selfRefText: string) (v: VerbExport) : string =
    let firstAliasNoStar = v.Names.Split(' ').[0].Replace("*", "")
    let lines = ResizeArray<string>()

    lines.Add(
        sprintf "@verb %s:\"%s\" %s %s %s %s #%d" selfRefText (escapeQuotedField v.Names) v.Dobj v.Prep v.Iobj v.Perms v.Owner
    )
    lines.Add(sprintf "@program %s:%s" selfRefText firstAliasNoStar)

    for line in v.Code do
        lines.Add(line)

    lines.Add(".")
    String.concat "\n" lines + "\n"

// ---------------------------------------------------------------------------
// Orchestration.
// ---------------------------------------------------------------------------

/// Wall-clock cap on a single eval round trip, applied uniformly to every
/// `evalRunner` call `exportTree` makes below (the corponym/builtins
/// queries, each per-object fetch, each anon-verb scan chunk). Without this,
/// a MOO-side task that never notify()s its tagged response back - hits
/// ToastStunt's own foreground task time/tick limit on an unusually large
/// object, or (confirmed live against a large real-world db running under
/// WSL2) sits idle long enough for the WSL2 NAT to silently drop the TCP
/// mapping with no FIN/RST ever reaching the Windows side - leaves
/// `MooEval.readTaggedLine`'s blocking socket read waiting forever: no
/// exception, no output, indistinguishable from the exporter still working.
/// This turns that into a clear, bounded failure instead.
let private evalTimeout = TimeSpan.FromSeconds(90.0)

/// Wraps `inner` so every call races against `evalTimeout`, throwing
/// `TimeoutException` on expiry instead of hanging. The timeout token is
/// linked to (not a replacement for) the caller's own `ct`, so external
/// cancellation still works exactly as before - the `when` guard tells a
/// genuine caller-driven cancellation apart from our own `CancelAfter` and
/// re-raises that as-is rather than mislabeling it a timeout.
///
/// Safe to keep using the same connection for the next call after a
/// timeout: cancelling `NetworkStream.ReadAsync` genuinely aborts that
/// pending read at the socket layer (a real .NET guarantee, not just
/// "stop waiting and leave it running in the background"), and every
/// response is tagged with a unique, never-reused sentinel
/// (`MooEval.nextTag`) - so if the timed-out MOO task somehow still
/// notify()s its stale response later, the next call's own read just
/// discards it as an unrecognized line, per `MooEval.readTaggedLine`'s
/// existing behavior.
let private withEvalTimeout (inner: EvalRunner) : EvalRunner =
    fun statements resultExpr ct ->
        task {
            use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            cts.CancelAfter(evalTimeout)

            try
                return! inner statements resultExpr cts.Token
            with :? OperationCanceledException when not ct.IsCancellationRequested ->
                return raise (TimeoutException(sprintf "timed out after %gs" evalTimeout.TotalSeconds))
        }

/// Walks every corponym, fetches its object's export data, and writes the
/// full tree to `outputDir`. Overwrites whatever's already there - callers
/// that want a clean tree should clear `outputDir` first (the round-trip
/// test, Phase 3, will do exactly that against a fresh directory each run).
let exportTree (conn: MooEval.Connection) (outputDir: string) (ct: CancellationToken) : Task<unit> =
    task {
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "FORMAT_VERSION"), "1\n")

        let evalRunner = MooEval.runAndAwaitJson conn |> withEvalTimeout
        let! corponymsByObjnum = getCorponyms evalRunner ct
        let corponymList = corponymsByObjnum |> Map.toList |> List.map (fun (n, name) -> name, n)

        File.WriteAllText(Path.Combine(outputDir, "corponyms.moo"), renderCorponymsMoo corponymList)

        // Connection-wide, one-shot - server-version-specific, not
        // per-object, so it belongs here rather than in the per-corponym
        // loop below or IdeActions.fs's per-object live-save re-export.
        let! functions = getBuiltinFunctions evalRunner ct
        File.WriteAllText(Path.Combine(outputDir, "builtins.json"), renderBuiltinsJson functions)

        let sortedByName =
            corponymList |> List.sortWith (fun (a, _) (b, _) -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase))

        let totalCorponyms = sortedByName.Length
        printfn "Exporting %d corponym-bearing object(s)..." totalCorponyms

        for i, (name, objRef) in List.indexed sortedByName do
            // Announced *before* the round trip, not after - so if this
            // particular object's fetch is slow (large properties/verb code,
            // a laggy connection), the last line on screen still names what's
            // currently in flight instead of going silent until it returns.
            printfn "[%d/%d] %s (#%d)..." (i + 1) totalCorponyms name objRef

            try
                let! dataOpt = getObjectExport evalRunner objRef ct

                match dataOpt with
                | None ->
                    eprintfn "Skipping %s (#%d): corponym points at an invalid object" name objRef
                | Some data ->
                    let objDir = Path.Combine(outputDir, "objects", name)
                    let verbsDir = Path.Combine(objDir, "verbs")
                    Directory.CreateDirectory(verbsDir) |> ignore

                    let verbFileNames = assignVerbFileNames data.Verbs

                    File.WriteAllText(
                        Path.Combine(objDir, "object.moo"),
                        renderObjectMoo corponymsByObjnum ("$" + name) data verbFileNames
                    )

                    for verb, fileName in verbFileNames do
                        File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile ("$" + name) verb)
            with :? TimeoutException as ex ->
                eprintfn "Skipping %s (#%d): %s - continuing with the rest of the export" name objRef ex.Message

        // FORMAT.md §1's one exception: #0 (System Object) always gets a
        // directory even without a corponym of its own to be discovered by
        // (corponyms are properties ON #0 pointing elsewhere, not at itself)
        // - it's where the sidecar's own bootstrap verbs live
        // (user_connected/do_command). Only when #0 has no corponym, though:
        // real ToastCore-derived worlds already corify #0 as $sysobj (unlike
        // Minimal.db/Survive, where #0 has none), and the corponym loop above
        // already exported it correctly under that name - exporting it again
        // here would just duplicate every verb/property under a second,
        // redundant `objects/0/` directory.
        if not (Map.containsKey 0L corponymsByObjnum) then
            try
                let! systemObjectData = getObjectExport evalRunner 0L ct

                match systemObjectData with
                | None -> eprintfn "Skipping #0: object.moo export query failed"
                | Some data ->
                    let objDir = Path.Combine(outputDir, "objects", "0")
                    let verbsDir = Path.Combine(objDir, "verbs")
                    Directory.CreateDirectory(verbsDir) |> ignore

                    let verbFileNames = assignVerbFileNames data.Verbs

                    File.WriteAllText(Path.Combine(objDir, "object.moo"), renderObjectMoo corponymsByObjnum "#0" data verbFileNames)

                    for verb, fileName in verbFileNames do
                        File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile "#0" verb)
            with :? TimeoutException as ex ->
                eprintfn "Skipping #0: %s" ex.Message

        // Non-corified verb capture tier: objects with no corponym can still
        // carry real, directly-defined verb code (a one-off override nobody
        // bothered to name) - captured on a best-effort basis, keyed by
        // objnum, into a bucket separate from the corponym-keyed tree above.
        // No object.moo, no stable identity, no import/restore path - a
        // plain safety net against silently losing code nobody named, not
        // parity with the corponym-keyed tree (see the card's own
        // accepted-resolution note for the reasoning).
        let corponymObjnums = corponymsByObjnum |> Map.toList |> List.map fst |> Set.ofList
        let! maxObj = getMaxObject evalRunner ct

        let anonVerbsByObjnum = ResizeArray<int64 * VerbExport list>()
        let mutable chunkLo = 1L

        while chunkLo <= maxObj do
            let chunkHi = min maxObj (chunkLo + anonVerbChunkSize - 1L)
            // Announced before the round trip - see the corponym loop above
            // for why.
            printfn "Scanning #%d..#%d/#%d for non-corified verbs..." chunkLo chunkHi maxObj

            try
                let! chunkResults = getAnonVerbs evalRunner corponymObjnums chunkLo chunkHi ct
                anonVerbsByObjnum.AddRange(chunkResults)
            with :? TimeoutException as ex ->
                eprintfn "Skipping #%d..#%d: %s - continuing with the rest of the scan" chunkLo chunkHi ex.Message

            chunkLo <- chunkHi + 1L

        for objnum, verbs in anonVerbsByObjnum do
            let objDir = Path.Combine(outputDir, "objects", "_anon", string objnum)
            let verbsDir = Path.Combine(objDir, "verbs")
            Directory.CreateDirectory(verbsDir) |> ignore

            let verbFileNames = assignVerbFileNames verbs
            let selfRefText = sprintf "#%d" objnum

            for verb, fileName in verbFileNames do
                File.WriteAllText(Path.Combine(verbsDir, fileName), renderVerbFile selfRefText verb)
    }
