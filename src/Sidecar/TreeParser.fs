/// The mirror of `Exporter`'s renderers: reads a tree written per
/// `FORMAT.md` (repo root) back into structured data for the
/// Phase 2 importer. Line-oriented parsing only - verb *bodies* are read as
/// raw source lines for `set_verb_code()`, never parsed into an AST (that's
/// `Language/Parser.fs`'s job for the LSP, a different concern).
module Sidecar.TreeParser

open System.IO
open Sidecar.Exporter

/// A parent as written in `object.moo`'s `parents:` line - either `$name`
/// (resolved against whichever corponym map is relevant at apply time, per
/// I2 - deliberately NOT resolved here at parse time) or a raw `#objnum`
/// fallback for an uncorified parent (informational only; see FORMAT.md §7's
/// objnum-drift hazard - there is no portable identity for these).
type ParentRef =
    | ByCorponym of string
    | ByObjnum of int64

/// `object.moo` parsed, plus its verb files. Properties and verb
/// owner/perms/etc. reuse `Exporter`'s types unchanged - those fields are
/// always raw `#objnum` in the file with no `$name` ambiguity (FORMAT.md
/// §3), unlike `Parents`.
type ParsedObject =
    { SelfCorponym: string
      Parents: ParentRef list
      Owner: int64
      Flags: string list
      Properties: PropertyExport list
      Verbs: VerbExport list
      /// The `name:` header line, if present - absent (`None`) for a tree
      /// exported before this field existed, per FORMAT.md's backwards-compat
      /// note. Not otherwise normalized here (an empty `Some ""` is left
      /// as-is) - parsed for parity with `Metadata.TreeFormat.ParsedObject`,
      /// but not currently read anywhere in the import/promotion path (see
      /// `Exporter.ObjectExport.LiveName`'s own scope note).
      Name: string option
      /// The `aliases:` header line's tokens, `[]` if the line is present
      /// but empty, also `[]` if the line is absent entirely (pre-feature
      /// tree). Same not-currently-read scope note as `Name`.
      Aliases: string list }

let private parseObjRefToken (token: string) : int64 = int64 (token.TrimStart('#'))

let private parseParentRef (token: string) : ParentRef =
    if token.StartsWith("$") then ByCorponym(token.Substring(1)) else ByObjnum(parseObjRefToken token)

/// Reads a quoted field (a property name, or a verb's name-spec) starting at
/// `openQuoteIdx`, unescaping `\\`/`\"` along the way, and returns the
/// unescaped text plus the index of the closing (unescaped) quote. Needed
/// because real ToastCore content can have a name that's itself a literal `"`
/// (e.g. `$help`'s quoting-syntax topic) - see `Exporter.escapeQuotedField`,
/// which this undoes.
let private parseQuotedField (s: string) (openQuoteIdx: int) : string * int =
    let sb = System.Text.StringBuilder()
    let mutable i = openQuoteIdx + 1
    let mutable closeIdx = -1

    while closeIdx < 0 do
        if s.[i] = '\\' then
            sb.Append(s.[i + 1]) |> ignore
            i <- i + 2
        elif s.[i] = '"' then
            closeIdx <- i
        else
            sb.Append(s.[i]) |> ignore
            i <- i + 1

    sb.ToString(), closeIdx

/// Reads zero or more independently-escaped quoted tokens starting at
/// `startIdx` to the end of the line - `aliases:`'s encoding (see
/// `Exporter.renderQuotedFieldList`'s own comment for why aliases use one
/// quoted token per alias rather than a single space-joined field like a
/// verb's name-spec: a MOO alias can itself contain spaces).
let private parseQuotedFieldList (s: string) (startIdx: int) : string list =
    let rec loop (i: int) (acc: string list) =
        if i >= s.Length then
            List.rev acc
        else
            match s.IndexOf('"', i) with
            | -1 -> List.rev acc
            | quoteIdx ->
                let token, closeIdx = parseQuotedField s quoteIdx
                loop (closeIdx + 1) (token :: acc)

    loop startIdx []

/// `corponyms.moo`: one `<name> #<objnum>` per line. Split from `parseCorponyms`
/// below so `History.fs` can parse a blob's content (fetched from git, never
/// written to disk) with the exact same logic instead of a copy of it.
let parseCorponymLines (lines: string[]) : (string * int64) list =
    lines
    |> Array.filter (fun line -> line.Trim() <> "")
    |> Array.map (fun line ->
        let parts = line.Split([| ' ' |], 2)
        parts.[0], parseObjRefToken parts.[1])
    |> List.ofArray

let parseCorponyms (path: string) : (string * int64) list =
    File.ReadAllLines(path) |> parseCorponymLines

/// One verb file: `@verb $name:"Names" dobj prep iobj perms #owner`,
/// `@program $name:firstAlias`, then raw source lines up to a line that is
/// exactly `.`. Split from `parseVerbFile` below for the same reason as
/// `parseCorponymLines`.
let parseVerbFileLines (lines: string[]) : VerbExport =
    let verbLine = lines.[0]

    let firstQuote = verbLine.IndexOf('"')
    let names, secondQuote = parseQuotedField verbLine firstQuote

    let tail = verbLine.Substring(secondQuote + 1).Trim().Split(' ')
    // tail = [| dobj; <prep - one or more words>; iobj; perms; "#owner" |].
    // dobj/iobj/perms/owner are always single tokens; prep is the only field
    // that can itself contain spaces - real MOO prepositions include
    // multi-word forms like "on top of/on/onto/upon" or "out of/from inside/from"
    // (ToastStunt's db_verbs.cc prep_list) - so prep is whatever's left
    // between dobj and the fixed-width iobj/perms/owner suffix.
    let dobj = tail.[0]
    let owner = parseObjRefToken tail.[tail.Length - 1]
    let perms = tail.[tail.Length - 2]
    let iobj = tail.[tail.Length - 3]
    let prep = String.concat " " tail.[1 .. tail.Length - 4]

    // Code runs from line index 2 (after @verb, @program) up to a line that
    // is exactly "." - safe because MOO code can never contain a bare "."
    // statement on its own line (not valid grammar), unlike a property
    // value's toliteral() text (see parsePropertyBlock's caveat below).
    let code = lines.[2..] |> Array.takeWhile (fun l -> l <> ".") |> List.ofArray

    { Names = names
      Owner = owner
      Perms = perms
      Dobj = dobj
      Prep = prep
      Iobj = iobj
      Code = code }

let parseVerbFile (path: string) : VerbExport =
    File.ReadAllLines(path) |> parseVerbFileLines

/// `object.moo`. The four header lines (`parents:`/`owner:`/`flags:`/
/// `verbs:`) are matched by prefix rather than fixed position, tolerating
/// minor hand-editing reordering; `@object $name` (or, for FORMAT.md §1's
/// `#0` exception, the raw `@object #0`) must still be the first line -
/// either way yields a bare identifier-like string (`"0"` for `#0`, same as
/// its directory name), so `ParsedObject.SelfCorponym`'s shape never needs
/// to change to accommodate it. Verb files are read from `verbsDir` in the
/// exact order the `verbs:` manifest lists them - that order is
/// dispatch-semantic (FORMAT.md §6) and is never re-derived from directory
/// listing order.
let parseObjectMoo (objectMooPath: string) (verbsDir: string) : ParsedObject =
    let lines = File.ReadAllLines(objectMooPath)
    let objectRefText = lines.[0].Substring("@object ".Length)

    let selfCorponym =
        if objectRefText.StartsWith("$") then objectRefText.Substring(1) else objectRefText.TrimStart('#')

    let headerValue (prefix: string) =
        lines
        |> Array.find (fun l -> l.StartsWith(prefix: string))
        |> fun l -> l.Substring(prefix.Length)

    let splitNonEmpty (s: string) =
        s.Split(' ') |> Array.filter (fun t -> t <> "") |> List.ofArray

    let parents = headerValue "parents: " |> splitNonEmpty |> List.map parseParentRef
    let owner = headerValue "owner: " |> parseObjRefToken
    let flags = headerValue "flags: " |> splitNonEmpty
    let verbFileNames = headerValue "verbs: " |> splitNonEmpty

    let verbs = verbFileNames |> List.map (fun fileName -> parseVerbFile (Path.Combine(verbsDir, fileName)))

    // `name:`/`aliases:` are optional (`Array.tryFind`, not `Array.find`) -
    // absent entirely in a tree exported before this field existed, per
    // FORMAT.md's backwards-compat note. Both must render before `verbs:`
    // so `headerLineCount` below (computed from `verbs:`'s own position)
    // stays correct regardless of whether these lines are present.
    let name =
        lines
        |> Array.tryFind (fun l -> l.StartsWith("name: "))
        |> Option.map (fun l -> fst (parseQuotedField l (l.IndexOf('"'))))

    let aliases =
        lines
        |> Array.tryFind (fun l -> l.StartsWith("aliases: "))
        |> Option.map (fun l -> parseQuotedFieldList l "aliases: ".Length)
        |> Option.defaultValue []

    // Property blocks: everything after the header, split on blank lines
    // into "@property ... <value lines> ." chunks. A value literal can
    // itself span multiple physical lines if the underlying string contains
    // a raw embedded newline (toliteral() does not escape those) - known,
    // documented limitation: if one of those embedded lines is itself
    // exactly ".", this parser terminates the value early. Not solved here;
    // property values with a raw newline immediately followed by a
    // stand-alone "." line are a pre-existing format-level edge case, not a
    // Phase 2 concern - flagged, not silently mishandled.
    let headerLineCount =
        1 + (lines |> Array.findIndex (fun l -> l.StartsWith("verbs: ")))

    let properties =
        let rec loop (idx: int) (acc: PropertyExport list) =
            if idx >= lines.Length then
                List.rev acc
            elif lines.[idx].Trim() = "" then
                loop (idx + 1) acc
            else
                let header = lines.[idx] // @property "name" owner=#N perms=XX
                let firstQuote = header.IndexOf('"')
                let name, secondQuote = parseQuotedField header firstQuote

                let tail = header.Substring(secondQuote + 1).Trim()
                // tail = "owner=#N perms=XX"
                let tailParts = tail.Split(' ')
                let owner = parseObjRefToken (tailParts.[0].Substring("owner=".Length))
                let perms = tailParts.[1].Substring("perms=".Length)

                let valueLines =
                    lines.[idx + 1 ..] |> Array.takeWhile (fun l -> l <> ".") |> List.ofArray

                let nextIdx = idx + 1 + valueLines.Length + 1 // +1 for the "." line

                let prop =
                    { Name = name
                      Owner = owner
                      Perms = perms
                      ValueLiteral = String.concat "\n" valueLines }

                loop nextIdx (prop :: acc)

        loop headerLineCount []

    { SelfCorponym = selfCorponym
      Parents = parents
      Owner = owner
      Flags = flags
      Properties = properties
      Verbs = verbs
      Name = name
      Aliases = aliases }

/// Reads a full tree: `corponyms.moo` plus every `objects/<name>/object.moo`
/// the corponym list references.
let parseTree (treeDir: string) : (string * int64) list * Map<string, ParsedObject> =
    let corponyms = parseCorponyms (Path.Combine(treeDir, "corponyms.moo"))

    let objects =
        corponyms
        |> List.choose (fun (name, _) ->
            let objDir = Path.Combine(treeDir, "objects", name)
            let objectMooPath = Path.Combine(objDir, "object.moo")

            if File.Exists(objectMooPath) then
                Some(name, parseObjectMoo objectMooPath (Path.Combine(objDir, "verbs")))
            else
                None)
        |> Map.ofList

    corponyms, objects
