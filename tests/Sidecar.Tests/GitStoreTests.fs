module Sidecar.Tests.GitStoreTests

open System
open System.IO
open Xunit
open LibGit2Sharp
open Sidecar.GitStore

/// `Directory.Delete(path, true)` throws `UnauthorizedAccessException` on
/// Windows if any file underneath is read-only - which git's own loose
/// object files always are, by design (content-addressed immutability), so
/// every throwaway test repo hits this. Clear the attribute first.
let private forceDeleteDirectory (path: string) =
    for file in Directory.GetFiles(path, "*", SearchOption.AllDirectories) do
        File.SetAttributes(file, FileAttributes.Normal)

    Directory.Delete(path, true)

/// Inits a throwaway non-bare repo with one initial commit, ensuring the
/// branch is named "main" regardless of this machine's `init.defaultBranch`
/// config (GitStore hardcodes "main").
let private newTestRepo () : string =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-gitstore-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    Repository.Init(dir) |> ignore

    use repo = new Repository(dir)
    File.WriteAllText(Path.Combine(dir, "FORMAT_VERSION"), "1\n")
    Commands.Stage(repo, "FORMAT_VERSION")
    let sig' = Signature("Test", "test@example.com", DateTimeOffset.UtcNow)
    repo.Commit("initial", sig', sig') |> ignore

    if repo.Head.FriendlyName <> "main" then
        repo.Refs.Rename(repo.Refs.["refs/heads/" + repo.Head.FriendlyName], "refs/heads/main") |> ignore
        repo.Refs.UpdateTarget("HEAD", "refs/heads/main") |> ignore

    dir

let private writeFile (repoDir: string) (relativePath: string) (content: string) =
    let fullPath = Path.Combine(repoDir, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)) |> ignore
    File.WriteAllText(fullPath, content)

[<Fact>]
let ``buildCommitMessage groups by corponym, marks new items with a plus`` () =
    let items =
        [ { Corponym = "room"; Name = "look_self"; Kind = Modified }
          { Corponym = "room"; Name = "describe"; Kind = Modified }
          { Corponym = "player"; Name = "tell_lines"; Kind = Added } ]

    Assert.Equal("$room: look_self, describe; $player: +tell_lines", buildCommitMessage items)

[<Fact>]
let ``commitChangedFiles creates the wip ref if it doesn't exist, parented on main`` () =
    let dir = newTestRepo ()

    try
        writeFile dir "objects/room/verbs/look_self.moo" "@verb ...\n.\n"
        use repo = new Repository(dir)

        let commit =
            commitChangedFiles repo "session-1" [ "objects/room/verbs/look_self.moo" ] [] "test commit" "Tester" "t@example.com"

        Assert.Equal<string>("refs/moo/wip/session-1", (repo.Refs.["refs/moo/wip/session-1"]).CanonicalName)
        Assert.Equal(commit.Id, repo.Lookup<Commit>("refs/moo/wip/session-1").Id)
        Assert.Equal<Commit>(repo.Branches.["main"].Tip, commit.Parents |> Seq.exactlyOne)
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``commitChangedFiles's removedPaths drops a path from the tree entirely`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)

        writeFile dir "objects/room/verbs/look_self.moo" "one\n"
        writeFile dir "objects/room/verbs/describe.moo" "two\n"

        let first =
            commitChangedFiles
                repo
                "session-1"
                [ "objects/room/verbs/look_self.moo"; "objects/room/verbs/describe.moo" ]
                []
                "first"
                "Tester"
                "t@example.com"

        Assert.NotNull(first.Tree.["objects/room/verbs/look_self.moo"])
        Assert.NotNull(first.Tree.["objects/room/verbs/describe.moo"])

        let second =
            commitChangedFiles repo "session-1" [] [ "objects/room/verbs/describe.moo" ] "removed a verb" "Tester" "t@example.com"

        Assert.NotNull(second.Tree.["objects/room/verbs/look_self.moo"])
        Assert.Null(second.Tree.["objects/room/verbs/describe.moo"])
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``commitChangedFiles onto an existing wip ref parents on the wip ref's own tip, not main`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)

        writeFile dir "a.moo" "one\n"
        let first = commitChangedFiles repo "session-1" [ "a.moo" ] [] "first" "Tester" "t@example.com"

        writeFile dir "b.moo" "two\n"
        let second = commitChangedFiles repo "session-1" [ "b.moo" ] [] "second" "Tester" "t@example.com"

        Assert.Equal<Commit>(first, second.Parents |> Seq.exactlyOne)
        // main hasn't moved - only the wip ref has.
        Assert.NotEqual(second.Id, repo.Branches.["main"].Tip.Id)
        // The wip tree accumulates both files (built from the parent's tree
        // each time), not just the latest change.
        Assert.NotNull(second.Tree.["a.moo"])
        Assert.NotNull(second.Tree.["b.moo"])
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``squashWipOntoMain returns None when there is no wip ref`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        Assert.Equal(None, squashWipOntoMain repo "no-such-session" "msg" "Tester" "t@example.com")
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``squashWipOntoMain produces exactly one new commit on main and removes the wip ref`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let mainTipBefore = repo.Branches.["main"].Tip

        writeFile dir "a.moo" "one\n"
        commitChangedFiles repo "session-1" [ "a.moo" ] [] "first" "Tester" "t@example.com" |> ignore
        writeFile dir "b.moo" "two\n"
        commitChangedFiles repo "session-1" [ "b.moo" ] [] "second" "Tester" "t@example.com" |> ignore

        let squashed = squashWipOntoMain repo "session-1" "squash message" "Tester" "t@example.com"

        match squashed with
        | None -> Assert.Fail("expected a squashed commit")
        | Some commit ->
            Assert.Equal("squash message", commit.MessageShort)
            Assert.Equal<Commit>(mainTipBefore, commit.Parents |> Seq.exactlyOne)
            Assert.Equal(commit.Id, repo.Branches.["main"].Tip.Id)
            Assert.NotNull(commit.Tree.["a.moo"])
            Assert.NotNull(commit.Tree.["b.moo"])
            Assert.Null(repo.Refs.["refs/moo/wip/session-1"])
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``pruneStaleWipRefs removes only wip refs older than the threshold`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)

        // A "fresh" wip ref via the normal path.
        writeFile dir "fresh.moo" "x\n"
        commitChangedFiles repo "fresh-session" [ "fresh.moo" ] [] "fresh" "Tester" "t@example.com" |> ignore

        // A manually-constructed "stale" wip ref, backdated via Signature's
        // own DateTimeOffset parameter (no need to touch the system clock).
        let parent = repo.Branches.["main"].Tip
        let treeDefinition = TreeDefinition.From(parent.Tree)
        writeFile dir "stale.moo" "y\n"
        let blob = use s = File.OpenRead(Path.Combine(dir, "stale.moo")) in repo.ObjectDatabase.CreateBlob(s)
        treeDefinition.Add("stale.moo", blob, Mode.NonExecutableFile) |> ignore
        let tree = repo.ObjectDatabase.CreateTree(treeDefinition)
        let oldSig = Signature("Tester", "t@example.com", DateTimeOffset.UtcNow.AddDays(-30.0))
        let staleCommit = repo.ObjectDatabase.CreateCommit(oldSig, oldSig, "stale", tree, [ parent ], false)
        repo.Refs.Add("refs/moo/wip/stale-session", staleCommit.Id) |> ignore

        let pruned = pruneStaleWipRefs repo (TimeSpan.FromDays(7.0))

        Assert.Equal<string list>([ "stale-session" ], pruned)
        Assert.Null(repo.Refs.["refs/moo/wip/stale-session"])
        Assert.NotNull(repo.Refs.["refs/moo/wip/fresh-session"])
    finally
        forceDeleteDirectory dir
