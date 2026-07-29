/// Phase 4 of moo-vcs-plan.md: git commit machinery for the exported tree,
/// via LibGit2Sharp rather than shelling out (in-process commits, custom
/// ref manipulation, no fork per save - the retired $vcs shelled to
/// `vcs-commit.sh` per capture, which is exactly the operational cost
/// invariant I1 exists to eliminate).
///
/// Every commit here bypasses the repository's shared index/HEAD entirely -
/// commits land on `refs/moo/wip/<session>` (or are squashed onto `main`)
/// via direct tree/commit object construction, never `Commands.Stage`/
/// `Repository.Commit()` (which always mutate the checked-out HEAD). This is
/// what lets a save commit to a hidden ref without disturbing whatever's
/// actually checked out in the working tree.
module Sidecar.GitStore

open System
open System.IO
open LibGit2Sharp

/// `Removed` is never produced by a live save (there's no "delete verb/
/// property" action in the IDE today) - it exists for `Promotion.fs`'s
/// tree-to-tree diff, where a path can genuinely be present on one side and
/// missing on the other.
type ChangeKind =
    | Added
    | Modified
    | Removed

type ChangedItem = { Corponym: string; Name: string; Kind: ChangeKind }

/// `$room: look_self, describe; $player: +tell_lines` - the main plan's own
/// example shape. Grouped by corponym in first-occurrence order; `+` prefix
/// for a new verb/property, `-` for a removed one, none for a changed one.
let buildCommitMessage (items: ChangedItem list) : string =
    items
    |> List.groupBy (fun i -> i.Corponym)
    |> List.map (fun (corponym, group) ->
        let prefix kind =
            match kind with
            | Added -> "+"
            | Removed -> "-"
            | Modified -> ""

        let names =
            group
            |> List.map (fun i -> prefix i.Kind + i.Name)
            |> String.concat ", "

        sprintf "$%s: %s" corponym names)
    |> String.concat "; "

/// `None` if `branchName` doesn't exist yet - a first-ever promotion has no
/// `production` branch to compare against or move.
let tryGetBranchTip (repo: Repository) (branchName: string) : Commit option =
    match repo.Branches.[branchName] with
    | null -> None
    | branch -> Some branch.Tip

let private wipRefFullName (sessionId: string) = sprintf "refs/moo/wip/%s" sessionId

let private signature (authorName: string) (authorEmail: string) : Signature =
    Signature(authorName, authorEmail, DateTimeOffset.UtcNow)

/// Resolves the commit a new wip commit should parent onto - and, reused by
/// `IdeActions`'s history/search actions, the commit history queries should
/// treat as "current": the wip ref's own tip if it already exists, else
/// `main`'s tip (a fresh wip branch starts from wherever `main` currently
/// is). Not `private` because `History.fs`'s traversal functions default to
/// `HEAD` if not given an explicit start point, and a session's own
/// in-progress saves live on its wip ref, not on `main`/`HEAD`, until
/// squashed - `IdeActions` needs this same resolution to make sure a
/// history/search query actually sees them.
let resolveParent (repo: Repository) (sessionId: string) : Commit =
    match repo.Refs.[wipRefFullName sessionId] with
    | null -> repo.Branches.["main"].Tip
    | wipRef -> repo.Lookup<Commit>(wipRef.TargetIdentifier)

/// Commits the given files (paths relative to the repo root, read from
/// their current on-disk content - not from the repo's index) onto
/// `refs/moo/wip/<sessionId>`, creating the ref if it doesn't exist yet.
/// `removedPaths` drops paths from the tree entirely (a deleted verb's own
/// file, or an entire recycled object's directory) - `relativePaths` only
/// ever adds/updates a blob, with no way to make a path disappear.
let commitChangedFiles
    (repo: Repository)
    (sessionId: string)
    (relativePaths: string list)
    (removedPaths: string list)
    (message: string)
    (authorName: string)
    (authorEmail: string)
    : Commit =
    let parent = resolveParent repo sessionId
    let treeDefinition = TreeDefinition.From(parent.Tree)

    for relativePath in relativePaths do
        let fullPath = Path.Combine(repo.Info.WorkingDirectory, relativePath)
        use stream = File.OpenRead(fullPath)
        let blob = repo.ObjectDatabase.CreateBlob(stream)
        treeDefinition.Add(relativePath.Replace('\\', '/'), blob, Mode.NonExecutableFile) |> ignore

    for removedPath in removedPaths do
        treeDefinition.Remove(removedPath.Replace('\\', '/')) |> ignore

    let tree = repo.ObjectDatabase.CreateTree(treeDefinition)
    let sig' = signature authorName authorEmail
    let commit = repo.ObjectDatabase.CreateCommit(sig', sig', message, tree, [ parent ], false)

    let refName = wipRefFullName sessionId

    match repo.Refs.[refName] with
    | null -> repo.Refs.Add(refName, commit.Id) |> ignore
    | _ -> repo.Refs.UpdateTarget(refName, commit.Id.Sha) |> ignore

    commit

/// Squashes every commit on `refs/moo/wip/<sessionId>` into one commit on
/// `main` - since each wip commit already carries a complete tree snapshot
/// (never a diff), taking the wip ref's tip tree directly *is* the squash:
/// the intermediate wip commits simply become unreachable once the wip ref
/// is deleted. Returns `None` if the wip ref doesn't exist (nothing to
/// squash).
let squashWipOntoMain
    (repo: Repository)
    (sessionId: string)
    (message: string)
    (authorName: string)
    (authorEmail: string)
    : Commit option =
    let refName = wipRefFullName sessionId

    match repo.Refs.[refName] with
    | null -> None
    | wipRef ->
        let wipCommit = repo.Lookup<Commit>(wipRef.TargetIdentifier)
        let mainTip = repo.Branches.["main"].Tip
        let sig' = signature authorName authorEmail

        let squashed =
            repo.ObjectDatabase.CreateCommit(sig', sig', message, wipCommit.Tree, [ mainTip ], false)

        repo.Refs.UpdateTarget(repo.Refs.["refs/heads/main"], squashed.Id) |> ignore
        repo.Refs.Remove(refName)
        Some squashed

/// Deletes every `refs/moo/wip/*` ref whose tip commit is older than
/// `olderThan` - `git gc` won't collect anything reachable via a ref, so
/// this has to be explicit or the repo grows without bound (main plan §7).
/// Returns the session ids pruned.
let pruneStaleWipRefs (repo: Repository) (olderThan: TimeSpan) : string list =
    let cutoff = DateTimeOffset.UtcNow - olderThan

    repo.Refs.FromGlob("refs/moo/wip/*")
    |> Seq.choose (fun r ->
        let commit = repo.Lookup<Commit>(r.TargetIdentifier)

        if commit.Committer.When < cutoff then
            let sessionId = r.CanonicalName.Substring("refs/moo/wip/".Length)
            repo.Refs.Remove(r)
            Some sessionId
        else
            None)
    |> List.ofSeq
