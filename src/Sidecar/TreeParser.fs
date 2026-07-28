/// The mirror of `Exporter`'s renderers: reads a tree written per
/// `C:\dev\moo-code\moo-dev\FORMAT.md` back into structured data for the
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
      Verbs: VerbExport list }

let private parseObjRefToken (token: string) : int64 = int64 (token.TrimStart('#'))

let private parseParentRef (token: string) : ParentRef =
    if token.StartsWith("$") then ByCorponym(token.Substring(1)) else ByObjnum(parseObjRefToken token)

/// `corponyms.moo`: one `<name> #<objnum>` per line.
let parseCorponyms (path: string) : (string * int64) list =
    File.ReadAllLines(path)
    |> Array.filter (fun line -> line.Trim() <> "")
    |> Array.map (fun line ->
        let parts = line.Split([| ' ' |], 2)
        parts.[0], parseObjRefToken parts.[1])
    |> List.ofArray

/// One verb file: `@verb $name:"Names" dobj prep iobj perms #owner`,
/// `@program $name:firstAlias`, then raw source lines up to a line that is
/// exactly `.`.
let parseVerbFile (path: string) : VerbExport =
    let lines = File.ReadAllLines(path)
    let verbLine = lines.[0]

    let firstQuote = verbLine.IndexOf('"')
    let secondQuote = verbLine.IndexOf('"', firstQuote + 1)
    let names = verbLine.Substring(firstQuote + 1, secondQuote - firstQuote - 1)

    let tail = verbLine.Substring(secondQuote + 1).Trim().Split(' ')
    // tail = [| dobj; prep; iobj; perms; "#owner" |]
    let dobj, prep, iobj, perms, owner = tail.[0], tail.[1], tail.[2], tail.[3], parseObjRefToken tail.[4]

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

/// `object.moo`. The four header lines (`parents:`/`owner:`/`flags:`/
/// `verbs:`) are matched by prefix rather than fixed position, tolerating
/// minor hand-editing reordering; `@object $name` must still be the first
/// line. Verb files are read from `verbsDir` in the exact order the
/// `verbs:` manifest lists them - that order is dispatch-semantic (FORMAT.md
/// §6) and is never re-derived from directory listing order.
let parseObjectMoo (objectMooPath: string) (verbsDir: string) : ParsedObject =
    let lines = File.ReadAllLines(objectMooPath)
    let selfCorponym = lines.[0].Substring("@object $".Length)

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
                let secondQuote = header.IndexOf('"', firstQuote + 1)
                let name = header.Substring(firstQuote + 1, secondQuote - firstQuote - 1)

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
      Verbs = verbs }

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
