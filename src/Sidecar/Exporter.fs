/// Phase 1 of moo-vcs-plan.md: a read-only exporter that walks `#0`'s
/// corponyms, resolves objects, and emits an on-disk tree per
/// `C:\dev\moo-code\moo-dev\FORMAT.md` - `corponyms.moo`, one `object.moo`
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
      Verbs: VerbExport list }

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
  result = ["parents" -> parents_list, "owner" -> tostr({o}.owner), "flags" -> flags, "properties" -> props, "verbs" -> vout];
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

            return
                Some
                    { Parents = parents
                      Owner = int64 (root.GetProperty("owner").GetString().TrimStart('#'))
                      Flags = flags
                      Properties = properties
                      Verbs = verbs }
    }

/// Resolves a FORMAT.md tree-relative path to `(corponym, label)` - shared by
/// `IdeActions.searchHistory` (labeling a pickaxe-search hit) and
/// `Promotion.diffSummary` (labeling a production/main tree diff), so the
/// path-shape knowledge lives in exactly one place. `"(properties)"` is a
/// stand-in label for `object.moo` itself, not the exact property name
/// (which needs parsing the blob at a specific commit - not worth it for
/// either caller). `None` for paths outside `objects/<corponym>/...`
/// entirely (`corponyms.moo`, `FORMAT_VERSION`).
let describePath (path: string) : (string * string) option =
    match path.Split('/') with
    | [| "objects"; corponym; "object.moo" |] -> Some(corponym, "(properties)")
    | [| "objects"; corponym; "verbs"; fileName |] -> Some(corponym, Path.GetFileNameWithoutExtension(fileName: string))
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

    let verbFilesText = verbFileNames |> List.map snd |> String.concat " "
    lines.Add(sprintf "verbs: %s" verbFilesText)

    let sortedProps =
        data.Properties
        |> List.sortWith (fun a b -> String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))

    for p in sortedProps do
        lines.Add("")
        lines.Add(sprintf "@property \"%s\" owner=#%d perms=%s" p.Name p.Owner p.Perms)
        lines.Add(p.ValueLiteral)
        lines.Add(".")

    String.concat "\n" lines + "\n"

/// `selfRefText` - see `renderObjectMoo`'s own comment; same convention.
let renderVerbFile (selfRefText: string) (v: VerbExport) : string =
    let firstAliasNoStar = v.Names.Split(' ').[0].Replace("*", "")
    let lines = ResizeArray<string>()

    lines.Add(sprintf "@verb %s:\"%s\" %s %s %s %s #%d" selfRefText v.Names v.Dobj v.Prep v.Iobj v.Perms v.Owner)
    lines.Add(sprintf "@program %s:%s" selfRefText firstAliasNoStar)

    for line in v.Code do
        lines.Add(line)

    lines.Add(".")
    String.concat "\n" lines + "\n"

// ---------------------------------------------------------------------------
// Orchestration.
// ---------------------------------------------------------------------------

/// Walks every corponym, fetches its object's export data, and writes the
/// full tree to `outputDir`. Overwrites whatever's already there - callers
/// that want a clean tree should clear `outputDir` first (the round-trip
/// test, Phase 3, will do exactly that against a fresh directory each run).
let exportTree (conn: MooEval.Connection) (outputDir: string) (ct: CancellationToken) : Task<unit> =
    task {
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "FORMAT_VERSION"), "1\n")

        let evalRunner = MooEval.runAndAwaitJson conn
        let! corponymsByObjnum = getCorponyms evalRunner ct
        let corponymList = corponymsByObjnum |> Map.toList |> List.map (fun (n, name) -> name, n)

        File.WriteAllText(Path.Combine(outputDir, "corponyms.moo"), renderCorponymsMoo corponymList)

        let sortedByName =
            corponymList |> List.sortWith (fun (a, _) (b, _) -> String.Compare(a, b, StringComparison.OrdinalIgnoreCase))

        for name, objRef in sortedByName do
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

        // FORMAT.md §1's one exception: #0 (System Object) always gets a
        // directory, corponym or not - it's where the sidecar's own
        // bootstrap verbs live (user_connected/do_command) and has no
        // corponym of its own to be discovered by, since corponyms are
        // properties ON #0 pointing elsewhere, not at itself.
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
    }
