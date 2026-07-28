module Sidecar.Tests.PromotionTests

open System
open System.IO
open Xunit
open LibGit2Sharp
open Sidecar.GitStore
open Sidecar.Promotion

/// Same convention as `GitStoreTests.fs`/`HistoryTests.fs`'s own copies -
/// git's loose objects are always read-only on Windows.
let private forceDeleteDirectory (path: string) =
    for file in Directory.GetFiles(path, "*", SearchOption.AllDirectories) do
        File.SetAttributes(file, FileAttributes.Normal)

    Directory.Delete(path, true)

let private newTestRepo () : string =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-promotion-" + Guid.NewGuid().ToString("N"))
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

/// Builds a tree (not committed) directly from a set of relative-path ->
/// content pairs, layered onto `baseTree` (typically the repo's current
/// `main` tip) - cheaper than committing when a test only needs to compare
/// or materialize trees, not exercise the commit machinery itself.
let private buildTree (repo: Repository) (baseTree: Tree) (files: (string * string) list) : Tree =
    let treeDefinition = TreeDefinition.From(baseTree)

    for relativePath, content in files do
        use stream = new MemoryStream(Text.Encoding.UTF8.GetBytes(content: string))
        let blob = repo.ObjectDatabase.CreateBlob(stream)
        treeDefinition.Add(relativePath, blob, Mode.NonExecutableFile) |> ignore

    repo.ObjectDatabase.CreateTree(treeDefinition)

[<Fact>]
let ``materializeTree writes every file at its exact path and content`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)

        let tree =
            buildTree
                repo
                repo.Branches.["main"].Tip.Tree
                [ "corponyms.moo", "room #4\n"
                  "objects/room/object.moo", "@object $room\n...\n"
                  "objects/room/verbs/look_self.moo", "@verb ...\n.\n" ]

        let destDir = Path.Combine(Path.GetTempPath(), "moovcs-materialize-" + Guid.NewGuid().ToString("N"))

        try
            materializeTree tree destDir

            Assert.Equal("room #4\n", File.ReadAllText(Path.Combine(destDir, "corponyms.moo")))
            Assert.Equal("@object $room\n...\n", File.ReadAllText(Path.Combine(destDir, "objects", "room", "object.moo")))

            Assert.Equal(
                "@verb ...\n.\n",
                File.ReadAllText(Path.Combine(destDir, "objects", "room", "verbs", "look_self.moo"))
            )
        finally
            if Directory.Exists destDir then
                forceDeleteDirectory destDir
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``diffSummary reports added, modified, and removed items, skipping non-corponym paths`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let baseTree = repo.Branches.["main"].Tip.Tree

        let fromTree =
            buildTree
                repo
                baseTree
                [ "corponyms.moo", "room #4\n"
                  "objects/room/verbs/look_self.moo", "old content\n"
                  "objects/room/verbs/describe.moo", "will be removed\n" ]

        let toTree =
            buildTree
                repo
                fromTree
                [ "corponyms.moo", "room #4\nplayer #6\n" // changed, but not a corponym-path item
                  "objects/room/verbs/look_self.moo", "new content\n"
                  "objects/player/verbs/tell_lines.moo", "brand new\n" ]

        // Actually remove describe.moo from toTree - `buildTree` only adds/
        // overwrites, so remove it explicitly via a fresh TreeDefinition.
        let treeDefinition = TreeDefinition.From(toTree)
        treeDefinition.Remove("objects/room/verbs/describe.moo") |> ignore
        let toTreeWithRemoval = repo.ObjectDatabase.CreateTree(treeDefinition)

        let summary = diffSummary repo fromTree toTreeWithRemoval

        Assert.Contains({ Corponym = "room"; Name = "look_self"; Kind = Modified }, summary)
        Assert.Contains({ Corponym = "room"; Name = "describe"; Kind = Removed }, summary)
        Assert.Contains({ Corponym = "player"; Name = "tell_lines"; Kind = Added }, summary)
        // Exactly 3 items - the corponyms.moo change is real (its content
        // differs between the two trees) but isn't a corponym-path item, so
        // it must not appear here at all.
        Assert.Equal(3, summary.Length)
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``tryGetBranchTip returns None for a nonexistent branch and the tip otherwise`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)

        Assert.Equal(None, tryGetBranchTip repo "refs/heads/production")
        Assert.Equal(Some repo.Branches.["main"].Tip, tryGetBranchTip repo "refs/heads/main")
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``buildCommitMessage prefixes a removed item with a minus`` () =
    let items = [ { Corponym = "room"; Name = "describe"; Kind = Removed } ]
    Assert.Equal("$room: -describe", buildCommitMessage items)
