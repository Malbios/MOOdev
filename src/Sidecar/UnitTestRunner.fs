/// Runs discovered `test_`-prefixed verbs (see `LanguageServer.Handlers.findTestVerbs`) on a
/// dedicated, throwaway MOO instance - never the live connection the browser session is already
/// using for editing. Isolation is the entire point of this feature (see the reverted first
/// attempt, `1db7f29`/`a3298dc`, which ran tests over the live connection and was rejected for
/// exactly that reason): a test's side effects (recycled objects, mutated properties, thrown
/// errors) must never be able to touch real dev/content data.
///
/// One throwaway MOO per run (a "Run" or "Run all" click), not one per individual test - boots
/// fresh from a copy of `ToastStunt/run/survive.db` (already "Minimal.db plus the tiny
/// `#0:do_command`/`#0:user_connected` bootstrap verbs baked in" - see this repo's own CLAUDE.md
/// "Bootstrap verbs" section; a truly bare, un-bootstrapped `Minimal.db` has no `eval()` pathway at
/// all), runs whichever tests were requested, then tears down via the MOO's own in-band
/// `shutdown()` - the same reasoning `test-instance-stop.ps1` already documents for avoiding a
/// WSL process-tree kill from the Windows side.
module Sidecar.UnitTestRunner

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Sidecar.BridgeHandler
open Sidecar.IdeActions

/// One test to run, as requested by the client - `ObjRef`/`TestVerb` is the
/// `test_`-prefixed verb itself (already discovered by `Handlers.findTestVerbs`
/// on the LSP side); the "verb under test" is derived from it (below), not
/// sent separately, so the wire payload stays minimal.
type TestRequest = { ObjRef: int64; TestVerb: string }

type TestOutcome =
    { ObjRef: int64
      TestVerb: string
      Ok: bool
      ErrText: string }

/// `test_foo` tests `foo` on the same object - `None` if `TestVerb` somehow
/// doesn't carry the prefix (shouldn't happen, since discovery only ever
/// finds `test_`-prefixed names, but this function doesn't assume that).
let private underTestVerbName (testVerb: string) : string option =
    let prefix = "test_"
    if testVerb.StartsWith(prefix) then Some(testVerb.Substring(prefix.Length)) else None

let private escapeMooString (s: string) : string = s.Replace("\\", "\\\\").Replace("\"", "\\\"")

let private mooStringLiteral (s: string) : string = "\"" + escapeMooString s + "\""

let private mooCodeListLiteral (lines: string list) : string =
    "{" + (lines |> List.map mooStringLiteral |> String.concat ", ") + "}"

/// Builds the statements that fetch, from the *live* session, the test
/// verb's own code and (if it exists) the verb-under-test's code in one
/// round trip - read-only (`verb_code()`), no live-world side effect.
/// Reuses `IdeActions.resolveVerbIndexStatements`, the same idx-resolution
/// helper `fetchVerb`/`saveVerb`/`checkVerbSyntax` already rely on.
let buildFetchLiveCodeStatements (objRef: int64) (testVerb: string) : string * string =
    let o = sprintf "#%d" objRef
    let testLit = mooStringLiteral testVerb

    let underLit =
        match underTestVerbName testVerb with
        | Some name -> mooStringLiteral name
        | None -> "\"\""

    let statements =
        resolveVerbIndexStatements o testLit
        + " test_idx = idx; "
        + resolveVerbIndexStatements o underLit
        + " under_idx = idx;"

    let resultExpr =
        $"""["testCode" -> (test_idx == 0) ? {{}} | verb_code({o}, test_idx, 0, 1), "underCode" -> (under_idx == 0) ? {{}} | verb_code({o}, under_idx, 0, 1)]"""

    statements, resultExpr

/// Builds the statements that run entirely on the *throwaway* instance:
/// creates one fresh scratch object (`create(#-1)` - the raw "no parent"
/// form, not `$nothing`, since a bare `Minimal.db`-derived world has no
/// sysobj corponym properties to rely on), installs the test verb (and the
/// verb-under-test, if one was found) onto it, then invokes the test verb
/// wrapped in the exact `ok`/`errtext` try/except idiom `killTask`/
/// `evalScratchpad`/`runTestVerb` already use elsewhere in this file -
/// passing is returning normally, failing is any raised MOO error. A test
/// is responsible for creating whatever further fixtures/scaffolding it
/// needs itself, as its own first statements - this harness's only job is
/// getting the two verbs onto a fresh object and invoking one of them.
let buildInstallAndRunStatements (testVerb: string) (testCode: string list) (underTest: (string * string list) option) : string =
    let testLit = mooStringLiteral testVerb
    let testCodeLit = mooCodeListLiteral testCode

    let underPart =
        match underTest with
        | Some(name, code) ->
            let nameLit = mooStringLiteral name
            let codeLit = mooCodeListLiteral code
            $"""add_verb(obj, {{obj, "rxd", {nameLit}}}, {{"this", "none", "this"}}); set_verb_code(obj, {nameLit}, {codeLit});"""
        | None -> ""

    $"""obj = create(#-1); add_verb(obj, {{obj, "rxd", {testLit}}}, {{"this", "none", "this"}}); set_verb_code(obj, {testLit}, {testCodeLit}); {underPart} ok = 0; errtext = ""; try obj:({testLit})(); ok = 1; except err (ANY) errtext = tostr(err[2]); endtry"""

/// Binds to port 0 and reads back whatever the OS assigned, then releases
/// it immediately - the standard "let the OS pick a free port" trick.
/// Racy in principle (another process could grab it before the real MOO
/// binds moments later) but adequate for this infrequent, user-triggered
/// action, not a tight allocation loop.
let private findFreePort () : int =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

/// `C:\dev\moo\moody\ToastStunt` -> `/mnt/c/dev/moo/moody/ToastStunt` - same
/// conversion `test-instance-start.ps1`'s own `ConvertTo-WslPath` does.
let private toWslPath (windowsPath: string) : string =
    let full = Path.GetFullPath windowsPath
    let drive = full.Substring(0, 1).ToLowerInvariant()
    let rest = full.Substring(2).Replace('\\', '/')
    sprintf "/mnt/%s%s" drive rest

let private isPortOpen (port: int) : bool =
    try
        use client = new TcpClient()
        let result = client.BeginConnect("127.0.0.1", port, null, null)
        result.AsyncWaitHandle.WaitOne(200) && client.Connected
    with _ ->
        false

let private waitForPort (port: int) (timeoutSeconds: float) (ct: CancellationToken) : Task =
    task {
        let deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds)

        while not (isPortOpen port) do
            if DateTime.UtcNow > deadline then
                failwithf "Isolated test MOO did not come up on port %d within %g seconds" port timeoutSeconds

            do! Task.Delay(300, ct)
    }

let private resultHeader (outcome: TestOutcome) : string =
    sprintf "moodev-test-run-result object: #%d verb: %s ok: %d" outcome.ObjRef outcome.TestVerb (if outcome.Ok then 1 else 0)

/// Fetches one test's live code, runs it on the already-connected throwaway
/// instance, and returns its outcome - the two round trips
/// (`evalOnSession` against the live world, `MooEval.runAndAwaitJson`
/// against the throwaway instance) are entirely independent connections;
/// nothing here ever writes to the live world.
let private runOneTest
    (session: Session)
    (testConn: MooEval.Connection)
    (req: TestRequest)
    (ct: CancellationToken)
    : Task<TestOutcome> =
    task {
        let fetchStatements, fetchResultExpr = buildFetchLiveCodeStatements req.ObjRef req.TestVerb
        let! fetched = evalOnSession session fetchStatements fetchResultExpr ct
        let fetchedRoot = fetched.RootElement

        let readLines (prop: string) =
            fetchedRoot.GetProperty(prop).EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

        let testCode = readLines "testCode"
        let underCode = readLines "underCode"

        // An empty fetch means the verb no longer exists on the live
        // world (deleted/renamed since discovery ran) - installing zero
        // lines of code would compile as a legal, silently-passing no-op
        // verb, which would misreport as PASS rather than surfacing the
        // real problem.
        if List.isEmpty testCode then
            return
                { ObjRef = req.ObjRef
                  TestVerb = req.TestVerb
                  Ok = false
                  ErrText = sprintf "test verb #%d:%s no longer exists on the live world" req.ObjRef req.TestVerb }
        else
            let underTest =
                match underTestVerbName req.TestVerb, underCode with
                | Some name, [] -> None
                | Some name, code -> Some(name, code)
                | None, _ -> None

            let runStatements = buildInstallAndRunStatements req.TestVerb testCode underTest
            let! result = MooEval.runAndAwaitJson testConn runStatements """["ok" -> ok, "errtext" -> errtext]""" ct
            let root = result.RootElement
            let ok = root.GetProperty("ok").GetInt32() = 1
            let errtext = root.GetProperty("errtext").GetString()

            return
                { ObjRef = req.ObjRef
                  TestVerb = req.TestVerb
                  Ok = ok
                  ErrText = errtext }
    }

/// Launches the throwaway MOO via `wsl.exe`, same command shape
/// `test-instance-start.ps1` already uses for its own MOO launch (a single
/// `bash -c "cd <rundir> && <moobinary> <db> <db>.new <port>"`), just built
/// from .NET instead of PowerShell.
let private launchMoo (runDir: string) (mooBinary: string) (dbName: string) (port: int) : Process =
    let wslRunDir = toWslPath runDir
    let wslBinary = toWslPath mooBinary
    let command = sprintf "cd %s && %s %s %s.new %d" wslRunDir wslBinary dbName dbName port

    let psi = ProcessStartInfo("wsl.exe")
    psi.ArgumentList.Add("-d")
    psi.ArgumentList.Add("Ubuntu")
    psi.ArgumentList.Add("--")
    psi.ArgumentList.Add("bash")
    psi.ArgumentList.Add("-c")
    psi.ArgumentList.Add(command)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true

    Process.Start(psi)

/// Sends `shutdown()` without awaiting a tagged response - the server
/// exits as soon as it runs, so there's no `#$#...`-framed reply ever
/// coming back to await (unlike every other `MooEval`/`evalOnSession`
/// call). In-band, not an OS process kill, matching `test-instance-stop.ps1`'s
/// own documented reasoning for avoiding WSL process-tree kill issues.
let private sendShutdown (conn: MooEval.Connection) (ct: CancellationToken) : Task =
    task {
        let bytes = Encoding.UTF8.GetBytes(";; shutdown();\r\n")
        do! conn.Stream.WriteAsync(ReadOnlyMemory(bytes), ct).AsTask()
    }

/// Runs every requested test on one fresh, throwaway MOO instance,
/// sending each `moodev-test-run-result` back as soon as that individual
/// test completes (so "Run all" can update rows incrementally, not just at
/// the very end). Cleanup (in-band `shutdown()`, a backstop process kill if
/// it didn't actually exit, deleting the scratch db files) always runs
/// before this function returns *or* re-raises - explicit catch-cleanup-
/// rethrow rather than `try/finally`, since a task CE's `finally` clause
/// cannot itself run further `do!`-bound async work (the in-band shutdown
/// needs a real async write). Nothing here should ever be able to leak a
/// running MOO process or a stray db file on disk, whether every test
/// passes, one throws unexpectedly, or the MOO never even comes up.
let runIsolatedTests
    (config: Config)
    (session: Session)
    (webSocket: WebSocket)
    (requests: TestRequest list)
    (ct: CancellationToken)
    : Task =
    task {
        let runDir = Path.Combine(config.ToastStuntRoot, "run")
        let mooBinary = Path.Combine(config.ToastStuntRoot, "build", "moo")
        let baseDb = Path.Combine(runDir, "survive.db")
        let runId = Guid.NewGuid().ToString("N")
        let dbName = sprintf "unittest-%s.db" runId
        let scratchDb = Path.Combine(runDir, dbName)
        let scratchDbNew = scratchDb + ".new"

        File.Copy(baseDb, scratchDb, true)
        let port = findFreePort ()
        let proc = launchMoo runDir mooBinary dbName port

        let mutable testConn: MooEval.Connection option = None
        let mutable failure: exn option = None

        try
            do! waitForPort port 30.0 ct
            let! conn = MooEval.connect "127.0.0.1" port "wizard" ct
            testConn <- Some conn

            for req in requests do
                let! outcome = runOneTest session conn req ct
                do! sendWire webSocket (resultHeader outcome) (if outcome.Ok then [] else [ outcome.ErrText ]) ct
        with ex ->
            failure <- Some ex

        match testConn with
        | Some conn ->
            try
                do! sendShutdown conn ct
            with _ ->
                ()

            MooEval.disconnect conn
        | None -> ()

        try
            if not proc.HasExited then
                proc.WaitForExit(5000) |> ignore

            if not proc.HasExited then
                proc.Kill(true)
        with _ ->
            ()

        proc.Dispose()

        for f in [ scratchDb; scratchDbNew ] do
            if File.Exists f then
                File.Delete f

        match failure with
        | Some ex -> raise ex
        | None -> ()
    }
