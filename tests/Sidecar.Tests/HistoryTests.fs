module Sidecar.Tests.HistoryTests

open System
open System.IO
open Xunit
open LibGit2Sharp
open Sidecar.History

/// Same convention as `GitStoreTests.fs`'s own copy - git's loose objects are
/// always read-only on Windows, so `Directory.Delete` needs the attribute
/// cleared first.
let private forceDeleteDirectory (path: string) =
    for file in Directory.GetFiles(path, "*", SearchOption.AllDirectories) do
        File.SetAttributes(file, FileAttributes.Normal)

    Directory.Delete(path, true)

let private newTestRepo () : string =
    let dir = Path.Combine(Path.GetTempPath(), "moovcs-history-" + Guid.NewGuid().ToString("N"))
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

let private commitFile (repo: Repository) (relativePath: string) (content: string) (message: string) : Commit =
    let fullPath = Path.Combine(repo.Info.WorkingDirectory, relativePath)
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)) |> ignore
    File.WriteAllText(fullPath, content)
    Commands.Stage(repo, relativePath)
    let sig' = Signature("Test", "test@example.com", DateTimeOffset.UtcNow)
    repo.Commit(message, sig', sig')

[<Fact>]
let ``getFileHistory returns every commit touching a path, most recent first`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        commitFile repo "objects/room/verbs/look_self.moo" "one\n" "first version" |> ignore
        let second = commitFile repo "objects/room/verbs/look_self.moo" "two\n" "second version"

        let history = getFileHistory repo repo.Head.Tip "objects/room/verbs/look_self.moo"

        Assert.Equal(2, history.Length)
        Assert.Equal(second.Sha, history.[0].Sha)
        Assert.Equal("second version", history.[0].Message)
        Assert.Equal("first version", history.[1].Message)
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``getFileHistory follows a rename to the same content`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let created = commitFile repo "objects/room/verbs/old_name.moo" "same content\n" "created under old name"

        // A rename at the tree level: remove the old path, add the identical
        // blob under a new path in one commit - what "git mv" produces.
        let treeDefinition = TreeDefinition.From(created.Tree)
        let oldEntry = created.Tree.["objects/room/verbs/old_name.moo"]
        treeDefinition.Remove("objects/room/verbs/old_name.moo") |> ignore
        treeDefinition.Add("objects/room/verbs/new_name.moo", oldEntry.Target :?> Blob, Mode.NonExecutableFile) |> ignore
        let renamedTree = repo.ObjectDatabase.CreateTree(treeDefinition)
        let sig' = Signature("Test", "test@example.com", DateTimeOffset.UtcNow)
        let renamed = repo.ObjectDatabase.CreateCommit(sig', sig', "renamed", renamedTree, [ created ], false)
        repo.Refs.UpdateTarget(repo.Refs.["refs/heads/main"], renamed.Id) |> ignore

        let history = getFileHistory repo repo.Head.Tip "objects/room/verbs/new_name.moo"

        Assert.Equal<string list>([ "renamed"; "created under old name" ], history |> List.map (fun e -> e.Message))
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``getBlobAtCommit returns content at that commit and None before the path existed`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let initial = repo.Branches.["main"].Tip
        let withFile = commitFile repo "a.moo" "hello\n" "add a.moo"

        Assert.Equal(None, getBlobAtCommit repo initial.Sha "a.moo")
        Assert.Equal(Some "hello\n", getBlobAtCommit repo withFile.Sha "a.moo")
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``searchContent finds the commit that added a string and the one that removed it`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let added = commitFile repo "a.moo" "return 5;\n" "added return 5"
        let changed = commitFile repo "a.moo" "return 10;\n" "changed to return 10"

        let hits = searchContent repo repo.Head.Tip "return 5" None |> List.map (fun m -> m.Sha)

        Assert.Contains(added.Sha, hits)
        Assert.Contains(changed.Sha, hits)
        Assert.Equal(2, hits.Length)

        Assert.Empty(searchContent repo repo.Head.Tip "no_such_string_anywhere" None)
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``searchContent scoped to a path ignores matches elsewhere`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        commitFile repo "a.moo" "needle\n" "a has needle" |> ignore
        let bCommit = commitFile repo "b.moo" "needle\n" "b has needle"

        let hits = searchContent repo repo.Head.Tip "needle" (Some [ "b.moo" ])

        Assert.Equal<string list>([ bCommit.Sha ], hits |> List.map (fun m -> m.Sha))
    finally
        forceDeleteDirectory dir

[<Fact>]
let ``diffCorponyms reports added, repointed, and removed entries`` () =
    let dir = newTestRepo ()

    try
        use repo = new Repository(dir)
        let addedCommit = commitFile repo "corponyms.moo" "room #4\n" "add room corponym"
        let repointedCommit = commitFile repo "corponyms.moo" "room #7\n" "repoint room"
        let removedCommit = commitFile repo "corponyms.moo" "" "remove room corponym"

        Assert.Equal<CorponymChange list>([ Added("room", 4L) ], diffCorponyms repo addedCommit.Sha)
        Assert.Equal<CorponymChange list>([ Repointed("room", 4L, 7L) ], diffCorponyms repo repointedCommit.Sha)
        Assert.Equal<CorponymChange list>([ Removed("room", 7L) ], diffCorponyms repo removedCommit.Sha)
    finally
        forceDeleteDirectory dir
