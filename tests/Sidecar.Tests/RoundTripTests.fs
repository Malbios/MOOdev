module Sidecar.Tests.RoundTripTests

open Xunit
open Sidecar.RoundTrip

[<Fact>]
let ``corponyms.moo lines with different objnums but same names produce no mismatch`` () =
    let a = [| "room #3"; "string_utils #4" |]
    let b = [| "room #17"; "string_utils #22" |]

    Assert.Empty(compareFileLines "corponyms.moo" a b)

[<Fact>]
let ``corponyms.moo with a different name is a real mismatch`` () =
    let a = [| "room #3" |]
    let b = [| "other_room #3" |]

    Assert.NotEmpty(compareFileLines "corponyms.moo" a b)

[<Fact>]
let ``object.moo owner line differing only by objnum produces no mismatch`` () =
    let a = [| "@object $room"; "parents: $base_thing"; "owner: #3"; "flags: "; "verbs: " |]
    let b = [| "@object $room"; "parents: $base_thing"; "owner: #99"; "flags: "; "verbs: " |]

    Assert.Empty(compareFileLines "objects/room/object.moo" a b)

[<Fact>]
let ``object.moo property owner= differing only by objnum produces no mismatch`` () =
    let a = [| "@property \"description\" owner=#3 perms=rc"; "\"a room\""; "." |]
    let b = [| "@property \"description\" owner=#99 perms=rc"; "\"a room\""; "." |]

    Assert.Empty(compareFileLines "objects/room/object.moo" a b)

[<Fact>]
let ``object.moo real property value difference is still caught`` () =
    let a = [| "@property \"description\" owner=#3 perms=rc"; "\"a room\""; "." |]
    let b = [| "@property \"description\" owner=#99 perms=rc"; "\"a different room\""; "." |]

    Assert.NotEmpty(compareFileLines "objects/room/object.moo" a b)

[<Fact>]
let ``object.moo parents line must match exactly - not normalized`` () =
    // Parents are already rendered in corponym form by the exporter when
    // resolvable, so a genuine parent difference must still be caught, not
    // swallowed by objnum normalization.
    let a = [| "@object $room"; "parents: $base_thing"; "owner: #3"; "flags: "; "verbs: " |]
    let b = [| "@object $room"; "parents: $other_thing"; "owner: #3"; "flags: "; "verbs: " |]

    Assert.NotEmpty(compareFileLines "objects/room/object.moo" a b)

[<Fact>]
let ``verb file's trailing owner objnum is normalized, everything else compared exactly`` () =
    let a = [| "@verb $room:\"look_self\" this none this rxd #3"; "@program $room:look_self"; "return;"; "." |]
    let b = [| "@verb $room:\"look_self\" this none this rxd #99"; "@program $room:look_self"; "return;"; "." |]

    Assert.Empty(compareFileLines "objects/room/verbs/look_self.moo" a b)

[<Fact>]
let ``verb file code difference is still caught`` () =
    let a = [| "@verb $room:\"look_self\" this none this rxd #3"; "@program $room:look_self"; "return 1;"; "." |]
    let b = [| "@verb $room:\"look_self\" this none this rxd #3"; "@program $room:look_self"; "return 2;"; "." |]

    Assert.NotEmpty(compareFileLines "objects/room/verbs/look_self.moo" a b)

[<Fact>]
let ``a different number of lines is always a mismatch, never normalized away`` () =
    let a = [| "@verb $room:\"look_self\" this none this rxd #3"; "@program $room:look_self"; "return;"; "." |]

    let b =
        [| "@verb $room:\"look_self\" this none this rxd #3"
           "@program $room:look_self"
           "extra_line();"
           "return;"
           "." |]

    Assert.NotEmpty(compareFileLines "objects/room/verbs/look_self.moo" a b)

[<Fact>]
let ``FORMAT_VERSION is compared verbatim, no normalization`` () =
    Assert.Empty(compareFileLines "FORMAT_VERSION" [| "1" |] [| "1" |])
    Assert.NotEmpty(compareFileLines "FORMAT_VERSION" [| "1" |] [| "2" |])

[<Fact>]
let ``compareTrees flags a file present in one tree but missing in the other`` () =
    let dirA =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-a-" + System.Guid.NewGuid().ToString("N"))

    let dirB =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-b-" + System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(dirA) |> ignore
    System.IO.Directory.CreateDirectory(dirB) |> ignore

    try
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "FORMAT_VERSION"), "1\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "corponyms.moo"), "room #3\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirB, "FORMAT_VERSION"), "1\n")
        // dirB is missing corponyms.moo entirely

        let mismatches = compareTrees dirA dirB

        Assert.Contains(mismatches, fun m -> m.RelativePath = "corponyms.moo")
    finally
        System.IO.Directory.Delete(dirA, true)
        System.IO.Directory.Delete(dirB, true)

[<Fact>]
let ``compareTrees reports zero mismatches for two trees differing only by objnum`` () =
    let dirA =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-a-" + System.Guid.NewGuid().ToString("N"))

    let dirB =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-b-" + System.Guid.NewGuid().ToString("N"))

    let objDirA = System.IO.Path.Combine(dirA, "objects", "room")
    let objDirB = System.IO.Path.Combine(dirB, "objects", "room")
    System.IO.Directory.CreateDirectory(objDirA) |> ignore
    System.IO.Directory.CreateDirectory(objDirB) |> ignore

    try
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "FORMAT_VERSION"), "1\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirB, "FORMAT_VERSION"), "1\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "corponyms.moo"), "room #3\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirB, "corponyms.moo"), "room #17\n")

        let objectMoo owner =
            sprintf "@object $room\nparents: \nowner: #%d\nflags: \nverbs: \n" owner

        System.IO.File.WriteAllText(System.IO.Path.Combine(objDirA, "object.moo"), objectMoo 3)
        System.IO.File.WriteAllText(System.IO.Path.Combine(objDirB, "object.moo"), objectMoo 17)

        let mismatches = compareTrees dirA dirB

        Assert.Empty(mismatches)
    finally
        System.IO.Directory.Delete(dirA, true)
        System.IO.Directory.Delete(dirB, true)

[<Fact>]
let ``compareTrees ignores objects/_anon/* entirely - present on only one side is not a mismatch`` () =
    // The non-corified verb capture tier has no portable identity across
    // instances (objnums aren't portable - see RoundTrip.fs's own comment on
    // isAnonPath), so it must never contribute a "present in A, missing in
    // B" mismatch, unlike every other file kind (see the sibling test above
    // for the same scenario with corponyms.moo, which *should* mismatch).
    let dirA =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-a-" + System.Guid.NewGuid().ToString("N"))

    let dirB =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "moovcs-rt-b-" + System.Guid.NewGuid().ToString("N"))

    let anonVerbsDirA = System.IO.Path.Combine(dirA, "objects", "_anon", "123", "verbs")
    System.IO.Directory.CreateDirectory(anonVerbsDirA) |> ignore
    System.IO.Directory.CreateDirectory(dirB) |> ignore

    try
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirA, "FORMAT_VERSION"), "1\n")
        System.IO.File.WriteAllText(System.IO.Path.Combine(dirB, "FORMAT_VERSION"), "1\n")

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(anonVerbsDirA, "test_verb.moo"),
            "@verb #123:\"test_verb\" this none this rxd #3\n@program #123:test_verb\nreturn 1;\n.\n"
        )
        // dirB has no _anon content at all - a genuinely different instance's
        // non-corified population, not a real drift signal.

        let mismatches = compareTrees dirA dirB

        Assert.Empty(mismatches)
    finally
        System.IO.Directory.Delete(dirA, true)
        System.IO.Directory.Delete(dirB, true)
