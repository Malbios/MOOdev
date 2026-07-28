/// Parses a `Survive`-shaped export tree per `C:\dev\moo-code\moo-dev\FORMAT.md` -
/// `corponyms.moo`, one `objects/<name>/object.moo`, one file per verb.
/// Deliberately independent of `Sidecar.TreeParser` (which parses the exact
/// same on-disk grammar for the importer's benefit): `Metadata` has no
/// business depending on `Sidecar.fsproj`'s ASP.NET/LibGit2Sharp/MOO-eval
/// machinery just to read text files. `FORMAT.md` is the shared, versioned,
/// authoritative source of truth both readers conform to independently - its
/// own `FORMAT_VERSION` file exists specifically so a future format change is
/// self-describing rather than inferred, which is what makes this the safe
/// place to accept a little duplication.
module Metadata.TreeFormat

open System.IO

/// A parent as written in `object.moo`'s `parents:` line - either `$name`
/// (resolved against the corponym map by the caller) or a raw `#objnum`
/// fallback for an uncorified parent (informational only - no portable
/// identity for these, per FORMAT.md §7's objnum-drift hazard).
type ParentRef =
    | ByCorponym of string
    | ByObjnum of int64

type ParsedProperty =
    { Name: string
      Owner: int64
      Perms: string
      /// `toliteral()`-rendered value text. Structural callers (`Loader.fs`)
      /// currently discard this - `Schema.PropertyMeta` stays value-less,
      /// matching the old system's contract - but it's captured here since
      /// the format itself carries it.
      ValueLiteral: string }

type ParsedVerb =
    { /// Full space-separated name-spec, declaration order preserved.
      Names: string
      Owner: int64
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string
      /// `verb_code()` output, one MOO source line per element.
      Code: string list }

type ParsedObject =
    { SelfCorponym: string
      /// Declaration order preserved exactly - ancestor search order, not
      /// cosmetic (FORMAT.md §6).
      Parents: ParentRef list
      Owner: int64
      Flags: string list
      /// Sorted by name in the file (FORMAT.md §3) - order as read.
      Properties: ParsedProperty list
      /// Declaration order as listed in `verbs:` - preserved exactly
      /// (dispatch is first-match-wins across this list). Paired with the
      /// absolute path each verb was read from, since `Loader.fs` wants that
      /// for `Schema.VerbNode.SourcePath` and there's no reason to make it
      /// re-derive a filename this module already resolved.
      Verbs: (ParsedVerb * string) list }

let private parseObjRefToken (token: string) : int64 = int64 (token.TrimStart('#'))

let private parseParentRef (token: string) : ParentRef =
    if token.StartsWith("$") then ByCorponym(token.Substring(1)) else ByObjnum(parseObjRefToken token)

/// `corponyms.moo`: one `<name> #<objnum>` per line.
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
/// exactly `.`.
let parseVerbFileLines (lines: string[]) : ParsedVerb =
    let verbLine = lines.[0]

    let firstQuote = verbLine.IndexOf('"')
    let secondQuote = verbLine.IndexOf('"', firstQuote + 1)
    let names = verbLine.Substring(firstQuote + 1, secondQuote - firstQuote - 1)

    let tail = verbLine.Substring(secondQuote + 1).Trim().Split(' ')
    // tail = [| dobj; prep; iobj; perms; "#owner" |]
    let dobj, prep, iobj, perms, owner = tail.[0], tail.[1], tail.[2], tail.[3], parseObjRefToken tail.[4]

    // Code runs from line index 2 (after @verb, @program) up to a line that
    // is exactly "." - safe because MOO code can never contain a bare "."
    // statement on its own line.
    let code = lines.[2..] |> Array.takeWhile (fun l -> l <> ".") |> List.ofArray

    { Names = names
      Owner = owner
      Perms = perms
      Dobj = dobj
      Prep = prep
      Iobj = iobj
      Code = code }

let parseVerbFile (path: string) : ParsedVerb =
    File.ReadAllLines(path) |> parseVerbFileLines

/// `object.moo`. The four header lines (`parents:`/`owner:`/`flags:`/
/// `verbs:`) are matched by prefix rather than fixed position; `@object
/// $name` (or, for FORMAT.md §1's `#0` exception, the raw `@object #0`)
/// must still be the first line - either way yields a bare identifier-like
/// string (`"0"` for `#0`, same as its directory name). Verb files are read
/// from `verbsDir` in the exact order the `verbs:` manifest lists them -
/// that order is dispatch-semantic (FORMAT.md §6) and is never re-derived
/// from directory listing order.
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

    let verbs =
        verbFileNames
        |> List.map (fun fileName ->
            let path = Path.Combine(verbsDir, fileName)
            parseVerbFile path, path)

    // Property blocks: everything after the header, split on blank lines
    // into "@property ... <value lines> ." chunks. Known, documented
    // limitation (matches Sidecar.TreeParser): a value literal containing a
    // raw embedded newline whose own text is exactly "." on its own line
    // would terminate early - a pre-existing format-level edge case, not
    // specific to this reader.
    let headerLineCount =
        1 + (lines |> Array.findIndex (fun l -> l.StartsWith("verbs: ")))

    let properties =
        let rec loop (idx: int) (acc: ParsedProperty list) =
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
/// the corponym list references, plus `objects/0/` unconditionally if
/// present - FORMAT.md §1's `#0` exception, the one directory this format
/// ever reads that isn't reachable through the corponym list at all (`#0`
/// has no corponym of its own). An entry in `corponyms.moo` with no
/// corresponding `object.moo` on disk is silently skipped here - the same
/// tolerance `Sidecar.TreeParser.parseTree` already has, since a corponym
/// can legitimately point at an object nothing has captured a directory for
/// yet.
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

    let systemObjectDir = Path.Combine(treeDir, "objects", "0")
    let systemObjectMooPath = Path.Combine(systemObjectDir, "object.moo")

    let objects =
        if File.Exists(systemObjectMooPath) then
            Map.add "0" (parseObjectMoo systemObjectMooPath (Path.Combine(systemObjectDir, "verbs"))) objects
        else
            objects

    corponyms, objects
