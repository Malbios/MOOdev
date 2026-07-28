# MOOdev

This repo is the `tools/` half of the project plan documented at
`C:\dev\moo-code\toaststunt-dev-environment-plan.md` — read that file first, it is the source of truth
for architecture, milestones, and settled decisions. `C:\dev\moo-code\moocode-reference.md` is the
companion MOOcode language reference, with a section of facts verified live against this project's
ToastStunt fork during M0.

Game/world content (MOOcode verbs, the ToastCore-derived database) lives in the sibling `Survive`
repo, not here. This repo holds the sidecar, browser client, and (later) the LSP server, DB
parser, and `moo-eval` — the tooling that lets you develop against the MOO without needing raw
telnet or the in-world editor.

**MOOcode reference material should be verified against the live server or the C source in
`C:\dev\moo-code\ToastStunt\src\` rather than trusted from training data** — the reference doc explains
why (MOO documentation is sparse and much of what's findable describes LambdaMOO 1.8.1, not
ToastStunt) and has an explicit list of what's confirmed versus still-shaky.

## Milestone status

- **M0 (substrate)** — done. ToastStunt fork builds under WSL2 Ubuntu
  (`C:\dev\moo-code\ToastStunt\build\moo`), ToastCore loads and runs, verified against a live
  connection.
- **M1 (the spine)** — done. F# sidecar bridging browser WebSocket ↔ MOO telnet TCP, plus a
  minimal Fable browser terminal. See `src/Sidecar` and `src/Client`.
- **M2 (capture)** — done. C patch adds `handle_verb_programmed(obj, vname, programmer)`, fired
  after every successful verb compile (both the `set_verb_code()` builtin and the native
  `.program` command). `$vcs` (MOOcode, in-world) writes the verb to disk and shells out to git via
  `exec()` (`executables/vcs-commit.sh`, `flock`-serialized since concurrent `exec()` calls race on
  git's own index lock). `$vcs:import_all()` did the initial ToastCore import — `Survive` now holds
  the full verb tree + `lookups.toml`, and `$vcs` itself (the object, its 5 verbs, the
  `#0:handle_verb_programmed` dispatcher) is baked into `survive.db`, the permanent baseline (see
  below) — no more reinstalling it after a restart.
- **M3 (editor v1)** — done. Monaco in the browser client (`src/Client/Monaco.fs`), with a Monarch
  grammar for MOOcode (`Client.Monaco.registerMoocodeLanguage`; MOOcode has no comment syntax at
  all, so none is defined). Open/save ride the *same* MOO connection the terminal already uses —
  `$vcs:ide_fetch`/`$vcs:ide_save` (new verbs, gated on `player.programmer` via
  `set_task_perms(player)`) `notify()` real MCP-shaped framing (`#$#moodev-edit-content` /
  `#$#moodev-edit-result`, multiline via `#$#*`/`#$#:`) rather than going through ToastCore's own
  `$verb_editor`/`dns-org-mud-moo-simpleedit` package (verified live: that package needs its
  human-oriented "look/help" prompt flow even after full MCP negotiation, not a clean
  request/response shape) or ToastCore's negotiate/registry machinery at all (not needed — both
  ends of this channel are ours). The Sidecar's `McpFilter.fs` recognizes `#$#`-prefixed lines with
  zero added latency for everything else (line-buffered only once a line is confirmed to start with
  the literal bytes `#$#`), and forwards a completed message to the browser as a JSON **Text**
  frame, keeping ordinary terminal output on **Binary** frames — the client tells them apart by
  frame type (`typeof ev.data === 'string'`), no envelope needed for the common case. Saving through
  this path calls `set_verb_code()` for real, so M2's capture hook fires automatically — no separate
  wiring needed for browser edits to land in git.
  **Known gap**: the editor pane is always shown once connected rather than proactively checking
  `player.programmer` first (v1 simplification) — a non-programmer just gets `E_PERM` in the
  diagnostics area on Open, which is server-enforced either way.

## Two MOO instances: dev world vs. automated tests

There are two separate database files, both descended from the same `survive.db` baseline
(toastcore + `$vcs`), so they never collide:

- **Dev/play world** — `toastcore/run/survive.db` / `survive.db.new`, FileIO rooted at the real
  `C:\dev\moo-code\Survive` repo. Launched by `test.ps1` in a visible window. On clean shutdown (in-game
  `;shutdown();` or Ctrl+C in the window — the wrapping script runs once the `wsl` command returns,
  however it exited), `survive.db.new` is promoted over `survive.db`, so the next launch continues
  from where you left off. This is the only path that ever writes to the real `Survive` repo.
- **Automated test instance** — `survive.test.db` (a fresh copy of `survive.db` taken at start),
  FileIO rooted at a throwaway scratch repo (`C:\dev\moo-code\SurviveTestScratch`, `git init`'d once and
  reused — its history is never inspected). Started/stopped headlessly (no visible window) via
  `test-instance-start.ps1` / `test-instance-stop.ps1` in this repo group's root, on port 7778 by
  default so it can run alongside the dev world's 7777. `$vcs.repo_root` is set to the scratch path
  right after boot (an eval over the raw TCP port, via `moo-client.ps1`'s `Send-MooCommands`) so
  captures land there, never in `Survive`. Nothing from this instance is ever promoted —
  `test-instance-stop.ps1` just calls `shutdown()`, no save. Because the scratch repo is separate,
  the two instances can run at the same time with zero risk of racing on `Survive`'s git state.

`executables/vcs-commit.sh` takes the repo root as its first argument (`this.repo_root` from
whichever instance is calling it) rather than a hardcoded path, which is what makes this split
possible without duplicating the script.

## Running the MOO server for local testing

The `moo` binary is a Linux ELF built under WSL2 — it does not run directly from Windows.
`C:\dev\moo-code\test.ps1` starts everything (MOO server, Sidecar, client dev server) in one go; to
start just the server by hand from PowerShell:

```powershell
wsl -d Ubuntu -- bash -c "cd /mnt/c/dev/moo-code/ToastStunt/run && /mnt/c/dev/moo-code/ToastStunt/build/moo survive.db survive.db.new 7777 -i /mnt/c/dev/moo-code/Survive"
```

For automated/headless testing, use `test-instance-start.ps1` / `test-instance-stop.ps1` instead
(see "Two MOO instances" above) rather than hand-rolling a second launch of this command — it
handles the `survive.test.db` copy and scratch FileIO root for you.

The `-i` flag points FileIO at the `Survive` repo (required since M2 — `$vcs` writes verb files
there). `exec()`'s working directory (`executables/`) is `C:\dev\moo-code\ToastStunt\run\executables\`
— both resolve relative to the server's CWD at launch (`run/`), not the repo root.

It listens on `127.0.0.1:7777`. Connecting from localhost suppresses the MOO's own welcome banner
(a documented HAProxy source-IP-rewrite quirk) — this is expected, not a broken connection; go
straight to `connect wizard` on a fresh ToastCore db.

## Running the sidecar + client for local dev

```powershell
cd C:\dev\moo-code\moo-dev
dotnet tool restore
dotnet run --project src\Sidecar\Sidecar.fsproj
```

```powershell
cd C:\dev\moo-code\moo-dev\src\Client
npm install
npm run dev
```

Then open the client dev server URL in a browser.
