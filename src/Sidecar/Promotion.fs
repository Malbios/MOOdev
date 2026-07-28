/// Phase 6 of moo-vcs-plan.md: "promotion is not a second system, it's the
/// importer with a different target host." `Importer.fs`/`RoundTrip.fs` both
/// already operate purely on disk directories, never git commits directly -
/// so the only new capability this module adds is materializing an arbitrary
/// commit's tree to a plain directory (never `git checkout`, which would
/// mutate the repo's real working directory/HEAD - this project avoids that
/// everywhere else, see `GitStore.fs`'s own top comment) and diffing two
/// trees for the pre-deploy review. Everything downstream of materializing a
/// tree is the unmodified Phase 2/3 machinery.
module Sidecar.Promotion

open System.IO
open LibGit2Sharp

/// Writes `tree`'s full content to `destDir`, recursively - read-only
/// against the repo, never touches its index/HEAD/working directory.
let rec materializeTree (tree: Tree) (destDir: string) : unit =
    Directory.CreateDirectory(destDir) |> ignore

    for entry in tree do
        let path = Path.Combine(destDir, entry.Name)

        match entry.TargetType with
        | TreeEntryTargetType.Tree -> materializeTree (entry.Target :?> Tree) path
        | TreeEntryTargetType.Blob -> File.WriteAllText(path, (entry.Target :?> Blob).GetContentText())
        | _ -> ()

/// The pre-deploy review (Q3: "what is different between dev and
/// production") - every added/modified/removed verb or property file
/// between two trees, labeled via `Exporter.describePath` (shared with
/// `IdeActions.searchHistory`). Paths that don't resolve to a corponym
/// (`corponyms.moo`, `FORMAT_VERSION`) are silently skipped - promotion
/// reviews code changes, not the corponym map itself (a corponym repoint
/// mid-promotion would be surprising and out of scope; `corponym-history`
/// covers that separately).
let diffSummary (repo: Repository) (fromTree: Tree) (toTree: Tree) : GitStore.ChangedItem list =
    let changes = repo.Diff.Compare<TreeChanges>(fromTree, toTree)

    let describe (kind: GitStore.ChangeKind) (path: string) : GitStore.ChangedItem option =
        match Exporter.describePath path with
        | None -> None
        | Some(corponym, label) -> Some { Corponym = corponym; Name = label; Kind = kind }

    [ for c in changes.Added do
          match describe GitStore.Added c.Path with
          | Some item -> yield item
          | None -> ()
      for c in changes.Modified do
          match describe GitStore.Modified c.Path with
          | Some item -> yield item
          | None -> ()
      for c in changes.Deleted do
          match describe GitStore.Removed c.Path with
          | Some item -> yield item
          | None -> () ]
