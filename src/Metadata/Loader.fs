/// Loads a `Schema.Graph` from a `Survive`-shaped tree: `lookups.toml` +
/// `metadata.json` (both written by MOOcode - `VCS/2_write_lookups_toml.moo`
/// and `VCS/8_export_metadata.moo`) plus the captured verb source files,
/// parsed via `Language.Parser`. Live MOO export, not a raw `.db` parser -
/// see the M4 plan's "Decision: metadata graph via live-server export" for
/// why (version-proof against the checkpoint format, reuses builtins like
/// `parents()` that already do the multi-inheritance-aware work).
module Metadata.Loader

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Language.Lexer
open Language.Parser
open Metadata.Schema

let parseObjRef (s: string) : ObjRef = Int64.Parse(s.TrimStart('#'))

/// Reads a flag field that could come back from `generate_json()` as either
/// a real JSON boolean or a 0/1 number, depending on whether the MOO value
/// behind it (`is_player(obj)`, `obj.wizard`, etc.) is a real ToastStunt
/// bool or a legacy int - robust to either without needing to pin down
/// which one this fork's `generate_json()` actually produces.
let private getBoolField (el: JsonElement) (propName: string) : bool =
    let v = el.GetProperty(propName)

    match v.ValueKind with
    | JsonValueKind.True -> true
    | JsonValueKind.False -> false
    | JsonValueKind.Number -> v.GetInt32() <> 0
    | _ -> false

/// `lookups.toml` is written exclusively by `2_write_lookups_toml.moo`,
/// which only ever emits one shape per line - `"#N" = "Name"`, no nesting,
/// no escaping (names are pre-sanitized to identifier-safe characters). A
/// hand-rolled line parser matching that exact, fully-controlled format is
/// simpler and has one less dependency than pulling in a general TOML
/// library for a file we're also the sole writer of.
let private lookupLineRegex = Regex(@"^""(#-?\d+)""\s*=\s*""(.*)""$")

let private loadLookups (path: string) : Map<ObjRef, string> =
    if not (File.Exists path) then
        Map.empty
    else
        File.ReadAllLines path
        |> Array.choose (fun line ->
            let m = lookupLineRegex.Match(line.Trim())
            if m.Success then
                Some(parseObjRef m.Groups.[1].Value, m.Groups.[2].Value)
            else
                None)
        |> Map.ofArray

let private parseVerbMeta (el: JsonElement) : VerbMeta =
    { Index = el.GetProperty("index").GetInt32()
      Names =
        el.GetProperty("names").GetString().Split(' ')
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray
      Owner = parseObjRef (el.GetProperty("owner").GetString())
      Perms = el.GetProperty("perms").GetString()
      Dobj = el.GetProperty("dobj").GetString()
      Prep = el.GetProperty("prep").GetString()
      Iobj = el.GetProperty("iobj").GetString() }

/// `$vcs:capture_verb` names files `<index>_<sanitized-first-name>.moo`
/// inside the defining object's directory. The sanitized-name half isn't
/// worth reconstructing here (it'd mean porting `sanitize_name` too) since
/// `<index>` alone is already a unique key within one object's directory -
/// glob on the prefix instead.
let private findVerbSource (objDir: string) (index: int) : string option =
    if not (Directory.Exists objDir) then
        None
    else
        Directory.EnumerateFiles(objDir, sprintf "%d_*.moo" index) |> Seq.tryHead

let private loadVerb (surviveRoot: string) (objDirName: string option) (definedOn: ObjRef) (meta: VerbMeta) : VerbNode =
    let sourcePath =
        objDirName
        |> Option.bind (fun dirName -> findVerbSource (Path.Combine(surviveRoot, dirName)) meta.Index)

    let ast, diagCount, tokens =
        match sourcePath with
        | None -> None, 0, None
        | Some path ->
            let lexResult = tokenize (File.ReadAllText path)

            match lexResult.Error with
            | Some _ -> None, 0, None
            | None ->
                let stmts = parse lexResult.Tokens
                Some stmts, Language.Ast.countErrors stmts, Some lexResult.Tokens

    { Meta = meta
      DefinedOn = definedOn
      SourcePath = sourcePath
      Ast = ast
      DiagnosticCount = diagCount
      Tokens = tokens }

/// `builtin-param-names.json` is embedded in this assembly (see
/// `Metadata.fsproj`) - a static, build-time extraction from ToastStunt's
/// own C source comments, not per-`Survive`-checkout data.
let private loadBuiltinParamNames () : Map<string, string list> =
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
let private loadBuiltinDescriptions () : Map<string, string> =
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

let private parseBuiltinFunc
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

/// Reads `<surviveRoot>/lookups.toml`, `<surviveRoot>/metadata.json`, and
/// `<surviveRoot>/builtins.json`, parsing every captured verb's source
/// along the way, into one `Graph`.
let load (surviveRoot: string) : Graph =
    let lookups = loadLookups (Path.Combine(surviveRoot, "lookups.toml"))
    let builtins = loadBuiltins (Path.Combine(surviveRoot, "builtins.json"))
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(surviveRoot, "metadata.json")))

    let objRefList (el: JsonElement) =
        el.EnumerateArray() |> Seq.map (fun e -> parseObjRef (e.GetString())) |> List.ofSeq

    let objects =
        doc.RootElement.GetProperty("objects").EnumerateArray()
        |> Seq.map (fun objEl ->
            let num = parseObjRef (objEl.GetProperty("num").GetString())
            let name = Map.tryFind num lookups

            let liveName =
                match objEl.TryGetProperty("name") with
                | true, nameEl ->
                    match nameEl.GetString() with
                    | "" -> None
                    | s -> Some s
                | false, _ -> None

            let verbs =
                objEl.GetProperty("verbs").EnumerateArray()
                |> Seq.map (parseVerbMeta >> loadVerb surviveRoot name num)
                |> List.ofSeq

            let owner =
                match objEl.TryGetProperty("owner") with
                | true, ownerEl ->
                    match ownerEl.GetString() with
                    | null
                    | "" -> None
                    | s -> Some(parseObjRef s)
                | false, _ -> None

            let flags =
                match objEl.TryGetProperty("flags") with
                | true, flagsEl ->
                    Some
                        { Player = getBoolField flagsEl "player"
                          Programmer = getBoolField flagsEl "programmer"
                          Wizard = getBoolField flagsEl "wizard"
                          Read = getBoolField flagsEl "r"
                          Write = getBoolField flagsEl "w"
                          Fertile = getBoolField flagsEl "f"
                          Anonymous = getBoolField flagsEl "a" }
                | false, _ -> None

            let properties =
                match objEl.TryGetProperty("properties") with
                | true, propsEl ->
                    propsEl.EnumerateArray()
                    |> Seq.map (fun p ->
                        { Name = p.GetProperty("name").GetString()
                          Owner = parseObjRef (p.GetProperty("owner").GetString())
                          Perms = p.GetProperty("perms").GetString() })
                    |> List.ofSeq
                | false, _ -> []

            num,
            { Num = num
              Name = name
              LiveName = liveName
              Parents = objRefList (objEl.GetProperty("parents"))
              Children = objRefList (objEl.GetProperty("children"))
              Verbs = verbs
              Owner = owner
              Flags = flags
              Properties = properties })
        |> Map.ofSeq

    let sysobjProps =
        match doc.RootElement.TryGetProperty("sysobj_props") with
        | true, propsEl ->
            propsEl.EnumerateObject()
            |> Seq.map (fun p -> p.Name, parseObjRef (p.Value.GetString()))
            |> Map.ofSeq
        | false, _ -> Map.empty

    { Objects = objects
      SystemObjectProperties = sysobjProps
      Builtins = builtins }
