/// Phase 5 of moo-vcs-plan.md: read-only git history queries backing the
/// IDE's "what did this look like before" / "what changed" / "why is $room
/// pointing at #14" surfaces. LibGit2Sharp only, no shelling out to `git` -
/// same precedent `GitStore.fs` already set for Phase 4's commit machinery.
module Sidecar.History

open System
open LibGit2Sharp
open Sidecar.TreeParser

/// `Path` is the path *at that specific commit* - not necessarily today's
/// path, since a file can be renamed partway through its history. Callers
/// that need to read the blob at a given entry's commit must use its own
/// `Path`, not whatever path they queried with (see `getBlobAtCommit`'s own
/// callers in `IdeActions.fs`).
type HistoryEntry =
    { Sha: string
      When: DateTimeOffset
      Message: string
      Path: string }

let private toEntry (logEntry: LogEntry) : HistoryEntry =
    { Sha = logEntry.Commit.Sha
      When = logEntry.Commit.Author.When
      Message = logEntry.Commit.MessageShort
      Path = logEntry.Path }

/// `git log --follow <path>` equivalent - rename-following is built into
/// `Commits.QueryBy(path, ...)` itself (confirmed against the installed
/// LibGit2Sharp 0.30.0 package's own shipped XML docs: its `FileHistory`
/// enumerator explicitly documents following renames), so no manual
/// rename-detection is needed here. Most recent first.
///
/// `startCommit` is explicit rather than defaulting to `HEAD` (`CommitFilter`'s
/// own default) - a session's in-progress saves live on its own
/// `refs/moo/wip/<session>` ref until squashed (see `GitStore.fs`), not on
/// `main`/`HEAD`, so a caller must pass `GitStore.resolveParent`'s result
/// (that session's wip tip, or `main`'s tip if none exists yet) or every
/// unsquashed save would be silently invisible to history/search - exactly
/// the bug this was written to fix, found live during Phase 5 verification.
let getFileHistory (repo: Repository) (startCommit: Commit) (relativePath: string) : HistoryEntry list =
    let path = relativePath.Replace('\\', '/')

    let filter =
        CommitFilter(
            IncludeReachableFrom = startCommit,
            SortBy = (CommitSortStrategies.Topological ||| CommitSortStrategies.Time)
        )

    repo.Commits.QueryBy(path, filter) |> Seq.map toEntry |> List.ofSeq

/// A file's content at a specific historical commit - `None` if the path
/// didn't exist there yet (or was already deleted).
let getBlobAtCommit (repo: Repository) (sha: string) (relativePath: string) : string option =
    let commit = repo.Lookup<Commit>(sha)
    let entry = commit.[relativePath.Replace('\\', '/')]

    if isNull (box entry) || entry.TargetType <> TreeEntryTargetType.Blob then
        None
    else
        Some((entry.Target :?> Blob).GetContentText())

type SearchMatch = { Sha: string; When: DateTimeOffset; Message: string; Path: string }

/// `git log -S<query>` equivalent - LibGit2Sharp has no built-in pickaxe, so
/// this walks every commit's diff against its (single, since this project's
/// history is always linear - wip commits and squashes each have exactly one
/// parent) parent and scans added/removed lines for `query`. Root commits
/// (no parent) are skipped: diffed against an empty tree, every line would
/// read as "added", which is noise rather than signal for "what changed".
/// `pathScope`, when given, limits the diff itself (not a post-filter) so a
/// search scoped to one corponym's files is proportionally cheaper too.
/// `startCommit` - see `getFileHistory`'s own comment on why this can't
/// default to `HEAD`.
let searchContent (repo: Repository) (startCommit: Commit) (query: string) (pathScope: string list option) : SearchMatch list =
    let queryLower = query.ToLowerInvariant()

    let isContentLine (line: string) =
        (line.StartsWith("+") && not (line.StartsWith("+++")))
        || (line.StartsWith("-") && not (line.StartsWith("---")))

    let matches (entry: PatchEntryChanges) =
        entry.Patch.Split('\n')
        |> Array.exists (fun line -> isContentLine line && line.ToLowerInvariant().Contains(queryLower))

    let filter = CommitFilter(IncludeReachableFrom = startCommit)

    [ for commit in repo.Commits.QueryBy(filter) do
          match commit.Parents |> Seq.tryHead with
          | None -> ()
          | Some parent ->
              let patch =
                  match pathScope with
                  | Some paths -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree, paths)
                  | None -> repo.Diff.Compare<Patch>(parent.Tree, commit.Tree)

              for entry in patch do
                  if matches entry then
                      yield
                          { Sha = commit.Sha
                            When = commit.Author.When
                            Message = commit.MessageShort
                            Path = entry.Path } ]

type CorponymChange =
    | Added of name: string * objnum: int64
    | Removed of name: string * objnum: int64
    | Repointed of name: string * fromObjnum: int64 * toObjnum: int64

let private corponymMapAt (commit: Commit) : Map<string, int64> =
    match commit.["corponyms.moo"] with
    | entry when isNull (box entry) || entry.TargetType <> TreeEntryTargetType.Blob -> Map.empty
    | entry ->
        (entry.Target :?> Blob).GetContentText().Split('\n')
        |> parseCorponymLines
        |> Map.ofList

/// Diffs `corponyms.moo` between `sha` and its parent - answers "why is
/// $room pointing at #14" by pinning down exactly when a corponym was
/// (re)assigned. A root commit's "before" map is empty, so every corponym
/// already present shows up as `Added`.
let diffCorponyms (repo: Repository) (sha: string) : CorponymChange list =
    let commit = repo.Lookup<Commit>(sha)
    let after = corponymMapAt commit
    let before = commit.Parents |> Seq.tryHead |> Option.map corponymMapAt |> Option.defaultValue Map.empty

    [ for KeyValue(name, objnum) in after do
          match Map.tryFind name before with
          | None -> Added(name, objnum)
          | Some oldObjnum when oldObjnum <> objnum -> Repointed(name, oldObjnum, objnum)
          | Some _ -> ()
      for KeyValue(name, objnum) in before do
          if not (Map.containsKey name after) then
              Removed(name, objnum) ]
