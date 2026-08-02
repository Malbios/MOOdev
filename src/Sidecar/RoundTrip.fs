/// Phase 3 of moo-vcs-plan.md: the round-trip gate. Compares two exported
/// trees for equivalence, tolerating the specific fields the format itself
/// declares non-portable across instances (invariant I2: objnum drift is
/// guaranteed; FORMAT.md §7: ownership is never carried over) rather than
/// requiring a literal byte-for-byte file diff, which would fail on real
/// content even for a perfectly faithful round trip.
///
/// Deliberately a targeted, line-context-aware normalizer rather than a full
/// semantic tree comparator: as long as a tree's parents never reference an
/// uncorified object (an accepted, pre-existing limitation - FORMAT.md §7
/// already documents that uncorified objnums have no portable identity), the
/// exporter's own preference for corponym form (`Exporter.refText`) means
/// `parents:` lines are already written as `$name` in both trees and need no
/// special resolution here. Only four narrow patterns ever legitimately
/// differ between a source and a fresh target instance: `corponyms.moo`'s
/// trailing `#objnum`, `object.moo`'s `owner: #N`, `@property ... owner=#N`,
/// and `@verb`'s trailing `#N`.
module Sidecar.RoundTrip

open System.IO
open System.Text.RegularExpressions

type Mismatch = { RelativePath: string; Detail: string }

let private objnumPattern = @"#-?\d+"

let private normalizeCorponymsLine (line: string) : string =
    Regex.Replace(line, objnumPattern + "$", "#N")

let private normalizeObjectMooLine (line: string) : string =
    if line.StartsWith("owner: ") then
        Regex.Replace(line, objnumPattern, "#N")
    elif line.StartsWith("@property ") then
        Regex.Replace(line, "owner=" + objnumPattern, "owner=#N")
    else
        line

let private normalizeVerbFileLine (line: string) : string =
    if line.StartsWith("@verb ") then
        Regex.Replace(line, objnumPattern + "$", "#N")
    else
        line

let private isVerbFile (relativePath: string) = relativePath.Contains("/verbs/")

/// Normalizes one file's lines according to which of the format's four
/// file kinds `relativePath` is - `FORMAT_VERSION` needs no normalization.
let normalizeFile (relativePath: string) (lines: string[]) : string[] =
    if relativePath = "corponyms.moo" then
        lines |> Array.map normalizeCorponymsLine
    elif relativePath.EndsWith("object.moo") then
        lines |> Array.map normalizeObjectMooLine
    elif isVerbFile relativePath then
        lines |> Array.map normalizeVerbFileLine
    else
        lines

/// Compares one file's already-read lines after normalization.
let compareFileLines (relativePath: string) (linesA: string[]) (linesB: string[]) : Mismatch list =
    let normA = normalizeFile relativePath linesA
    let normB = normalizeFile relativePath linesB

    if normA.Length <> normB.Length then
        [ { RelativePath = relativePath
            Detail = sprintf "line count differs: %d vs %d" normA.Length normB.Length } ]
    else
        [ for i in 0 .. normA.Length - 1 do
              if normA.[i] <> normB.[i] then
                  yield
                      { RelativePath = relativePath
                        Detail = sprintf "line %d differs:\n    A: %s\n    B: %s" (i + 1) normA.[i] normB.[i] } ]

let private relativeFiles (dir: string) : Set<string> =
    Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
    |> Array.map (fun f -> Path.GetRelativePath(dir, f).Replace('\\', '/'))
    |> Set.ofArray

/// `objects/_anon/*` (the non-corified verb capture tier) is excluded from
/// round-trip comparison entirely - unlike everything else here, it has no
/// portable identity across instances (keyed by objnum, not a corponym; two
/// different servers can assign the same objnum to two unrelated objects -
/// see the card's own accepted-resolution note). Comparing it wouldn't
/// detect real drift, just coincidental same-numbered/unrelated content, so
/// it's skipped rather than normalized like the four genuinely-portable
/// patterns above.
let private isAnonPath (relativePath: string) = relativePath.StartsWith("objects/_anon/")

/// Compares two exported trees. Every mismatch found is returned (not just
/// the first) - a missing/extra file is always a real failure, never
/// normalized away.
let compareTrees (treeA: string) (treeB: string) : Mismatch list =
    let filesA = relativeFiles treeA |> Set.filter (isAnonPath >> not)
    let filesB = relativeFiles treeB |> Set.filter (isAnonPath >> not)

    let missingMismatches =
        [ for f in Set.difference filesA filesB -> { RelativePath = f; Detail = "present in A, missing in B" }
          for f in Set.difference filesB filesA -> { RelativePath = f; Detail = "present in B, missing in A" } ]

    let commonMismatches =
        [ for f in Set.intersect filesA filesB do
              let linesA = File.ReadAllLines(Path.Combine(treeA, f))
              let linesB = File.ReadAllLines(Path.Combine(treeB, f))
              yield! compareFileLines f linesA linesB ]

    missingMismatches @ commonMismatches

let formatMismatches (mismatches: Mismatch list) : string =
    mismatches |> List.map (fun m -> sprintf "%s: %s" m.RelativePath m.Detail) |> String.concat "\n\n"
