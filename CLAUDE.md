# MOOdev

This is the MOO IDE: a standalone repo holding the sidecar, browser client, LSP server, DB
parser, and `moo-eval` — the tooling that lets you develop against a MOO without needing raw
telnet or the in-world editor. It submodules `ToastStunt` (this project's forked MOO server)
directly, since a live server is needed to test the IDE against; see `test-instance-start.ps1`
below.

Read `toaststunt-dev-environment-plan.md` (repo root) first — it's the source of truth for
architecture, milestones, and settled decisions. `moocode-reference.md` (repo root) is the
companion MOOcode language reference, with a section of facts verified live against this
project's ToastStunt fork during M0.

Game/world content (MOOcode verbs, a ToastCore-derived database) is not part of this repo at
all — it lives in whatever separate content project you're editing (e.g. `MOO-World`, an
independent repo). This repo's Sidecar/LanguageServer are just pointed at that project's content
tree via config (`Moo:TreeDir` / `Survive:Root` - the config *key* is still named after the
project's original content repo, purely cosmetic, not worth renaming) when you want to develop
it — see "Running the sidecar + client for local dev" below. Nothing here tracks or assumes a
specific content project, though `test.ps1`'s own `MooWorld` profile does assume one particular
sibling-checkout layout for convenience — see its own doc comment.

## Development conventions

- **Remove dead code and dead tests.** When a change makes a function, method, or test
  unreachable, delete it in that same change rather than leaving it for later cleanup.
- **Write unit tests where possible** for new logic, not just live/manual verification.
- **Prefer self-documenting code over lengthy comments.** A well-named function/variable
  should carry the "what"; comments are for the non-obvious "why" only, and should stay short.
- **Never special-case tooling behavior (tree view, inspector, etc.) by object number.** Any
  fix or feature should key off structural properties (has a parent, has a corponym, matches a
  filter) - never a hardcoded object number or object-number range. If something doesn't
  show/work as expected, verify the actual live behavior directly (start a test instance, query
  it, read the current code path) before explaining it from documentation or memory of prior
  work - docs describe intent at a point in time, not necessarily the current mechanism after
  later fixes.
- **Prefer table-driven, generalized launch tooling over bespoke per-environment scripts.** A
  new dev environment (which MOO database to boot, ports, content tree) should be a new entry
  in `test.ps1`'s `$profiles` table, not a new script or a new hardcoded if/else branch. Keep
  the automated Playwright test harness (`test-instance-start.ps1`) on the simplest/fastest DB
  (`Minimal.db`) unless there's a concrete tooling need for more - a richer manually-launched
  environment for exploration is a separate `test.ps1` profile, not something that should touch
  the scripted test harness.
- **During live-debug iteration against the test-instance stack, restart only the process whose
  source actually changed** (just Client, or just LanguageServer, etc.) rather than re-running
  the full `test-instance-start.ps1` each time - that always reboots the MOO instance and rebuilds
  every piece from scratch, which is pure overhead when only one component changed.

**MOOcode reference material should be verified against the live server or the C source in
`ToastStunt/src/` (repo root) rather than trusted from training data** — the reference doc explains
why (MOO documentation is sparse and much of what's findable describes LambdaMOO 1.8.1, not
ToastStunt) and has an explicit list of what's confirmed versus still-shaky.

## Git workflow

- **Commit and push finished, verified work without waiting to be asked.** Once a chunk of work
  is implemented and verified (build/tests pass, and live verification too if that was part of
  the task), commit it as part of finishing the task - "should I commit" isn't a separate open
  question needing its own confirmation round-trip. Push immediately after, in the same turn -
  don't leave a commit local waiting for a separate "push this."
- This repo submodules `ToastStunt`; a related content project may have its own separate
  `ToastStunt` submodule too (`MOO-World`, the current one, currently doesn't - this only applies
  if/when one is added back). If a submodule commit and a parent-repo
  submodule-pointer-bump commit both happen in the same round, push the submodule first, then
  the parent - the parent's pointer only resolves once that commit is reachable on the
  submodule's own remote.
- Still use judgment for genuinely ambiguous or destructive git actions (force-push, rewriting
  history, anything touching a shared/remote branch) - this is specifically about routine
  commits of finished local work, not a blanket override of git safety judgment.

## Tracking this project's work

Feature/task tracking for this project lives in an Obsidian vault **outside any repo** (it used
to be checked in at `vault/`, then was removed from git) - as of 2026-08-01,
`C:\Users\abrae\Documents\MEGA\SmallVaults\MOOcode\boards\MOO IDE Development.md`. **Confirm this
path fresh each session** rather than trusting this note - it has moved multiple times already.

When a vault todo is implemented, built, tested, and live-verified, move its card to **Ready for
Testing**, never straight to **Done** - the user reviews finished work manually and moves it to
Done themselves. Any feature idea proposed during brainstorming that doesn't end up on the board
by the next check-in is implicitly rejected - don't resurface it later.

**A card is two lines, not one - move both.** Each entry is `- [ ] [[Card Name]]` followed by an
indented `\t#Tag1 #Tag2` continuation line directly beneath it (see the tag vocabulary in
`.obsidian/plugins/obsidian-kanban/data.json`'s `tag-colors`, in the same vault). When relocating a
card between lanes, cut/paste the checkbox line *and* its tag line as one unit - editing just the
checkbox line and leaving the tag line behind (or dropping it) silently strips the card's
classification. This exact bug hit every card moved to Ready for Testing before 2026-08-02 (all 18
lost their tags); re-verify the tag line landed with the card after every move.

**Tags split into two axes, colored differently in `.obsidian/plugins/obsidian-kanban/data.json`'s
`tag-colors`** (same vault) - *categories* (subject/domain: UI/UX, VCS, Server, Security,
Documentation, Diagnostics, Navigation, Editor, Object-Model, Language) versus *types* (nature of
the work: Bugfix, Complex, Acceleration, Research, Safety). As of 2026-08-02:
- Every category tag shares one identical entry - `color: rgba(0, 0, 0, 1)` (black text),
  `backgroundColor: rgba(123, 164, 226, 1)` (the user's chosen blue, tuned from an earlier
  lighter shade of the same idea). **If a session introduces a new category tag, add it to
  `tag-colors` with this exact same pair of values - don't invent a new color for it.**
- Each type tag gets its own distinct dark `backgroundColor` with light-gray `color: rgba(225,
  225, 225, 1)` text - currently Bugfix red, Complex amber, Acceleration purple, Research indigo,
  Safety green. **If a session identifies a need for a new type tag, don't just pick a color for
  it - stop and ask the user**, since the dark-color assignment is a deliberate, small,
  memorable palette (not a formula like the category one) and picking a new one is a judgment
  call for the user, not the session.

## External references

- **[SindomeCorp/moo-for-llms](https://github.com/SindomeCorp/moo-for-llms)** — a public,
  MIT-licensed MOOcode reference corpus purpose-built for LLM consumption: concise guides for
  syntax/semantics, permissions, error handling, object lifecycle, core commands/utilities,
  tasks, command parsing, algorithm patterns, verb doc-comment conventions, common mistakes, and
  explicit dialect classification (LambdaMOO vs. Stunt vs. ToastStunt differences), plus runnable
  examples and eval/dataset scaffolding. Useful as a broader, cross-dialect supplement to
  `moocode-reference.md` above. It is *not* grounded against this project's own ToastStunt fork
  the way `moocode-reference.md` is, though - where the two disagree, or where its dialect
  classification doesn't clearly cover something, verify against `ToastStunt/src/` or a live
  connection rather than trusting either document outright.

**VCS ownership has moved to the sidecar.** `moo-vcs-plan.md` (repo root)'s phases 0-6 are
complete: the in-MOO `$vcs` package is fully retired, and version control (export/import/history/
diff/search/restore/promotion) is now owned entirely by the sidecar, talking to the MOO purely via
`eval()` over a wizard connection - no MOO-side file writes. The M2 status below and the
capture-path details describe the *old, retired* system, kept here for historical context only -
no current world runs it.

## Milestone status

- **M0 (substrate)** — done. ToastStunt fork builds under WSL2 Ubuntu
  (`ToastStunt\build\moo`, repo root), ToastCore loads and runs, verified against a live
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

Both `test.ps1`'s dev/play world and the automated test instance descend from
`ToastStunt\Minimal.db` (not toastcore + `$vcs` - that baseline is retired). Neither has any
in-MOO VCS content; the sidecar owns all of that from the outside via `eval()`.

- **Dev/play world** (`test.ps1`) — `ToastStunt\run\world.db` / `world.db.new`, FileIO rooted
  at whatever content project's `TreeDir` the launched profile points at (`test.ps1`'s own
  `$profiles` table - the built-in `MooWorld` profile assumes a sibling checkout at `..\MOO-World`;
  see its own doc comment if that assumption doesn't hold for your layout). `SeedFrom` for this
  profile is `MOO-World\world.db` itself (a rooted path, not a bare filename resolved under
  `ToastStunt\`) - copied once into `ToastStunt\run\world.db` on first launch, exactly like every
  other profile's `SeedFrom`, and never touched again from there on. Launched by `test.ps1
  -Database MooWorld` (also the default with no `-Database` flag) in a visible window. On clean
  shutdown (in-game `;;shutdown();`, or a graceful `SIGTERM`/Ctrl+C — the wrapping script runs once
  the `wsl` command returns, however it exited), `world.db.new` is promoted over `world.db`, so
  the next launch continues from where you left off. This is the only path that ever writes to a
  real content project's tree. **Note the double semicolon** - a bare `;shutdown();` silently does
  nothing on this world: a single leading `;` is ToastStunt's "eval" command alias
  (`parse_cmd.cc`), which needs a real `eval` verb to dispatch to (ToastCore ships one; this
  `Minimal.db`-derived world never installed one) - confirmed live via the same root cause as
  `test-instance-stop.ps1`'s own fix (see its own comment). The `;;` this world's `#0:do_command`
  bootstrap verb recognizes (see "Bootstrap verbs" below) is what actually reaches `eval()` here.
- **Automated test instance** (`test-instance-start.ps1` / `test-instance-stop.ps1`, both in this
  repo's own root) — `survive.test.db` (a fresh copy of `Minimal.db`, unrelated to `MOO-World` -
  the automated stack was deliberately left untouched by the `MOO-World` migration, see below), no
  FileIO root at all (the `-i` flag is dropped entirely here — confirmed optional at the C++ level,
  and nothing reads it since `$vcs` is retired). These two scripts manage the **full stack**
  headlessly (no visible window), not just the MOO process: Sidecar, LSP server, and Client too,
  each a single directly-tracked process (no `dotnet watch run`/`npm run dev` wrapper layers, which
  leave orphaned children behind when killed — confirmed live, this exact mistake accumulated ~25
  orphaned processes across several sessions before this script tracked them). The Sidecar (which
  owns all git-based version control) is pointed at a dedicated scratch content tree,
  `TestScratchTree` (repo root) — rebuilt from scratch on every run by exporting whatever's
  actually live on *this run's* test MOO instance (`Sidecar.exe export`), never cloned from or
  pointed at any real content project. This is the fix for a real, repeated mistake: earlier
  sessions' manual Sidecar launches for Playwright-driven verification kept defaulting to
  `Moo:TreeDir`'s real-content-project-sibling default (see `appsettings.json`), leaving real (if
  unmerged/unpushed) commits and WIP refs in that real repo. Default ports: MOO 7778, Sidecar 5900,
  LSP 5950, Client 5199, LSP-bridge listener 7782 — all distinct from `test.ps1`'s own profile
  ports, so everything can run concurrently. Nothing from this instance is ever promoted —
  `test-instance-stop.ps1` tears down Sidecar/LSP/Client immediately, then calls the MOO's own
  `shutdown()`, no save. Intentionally left on `Minimal.db`-derived content rather than something
  richer - automated tests want the simplest/fastest baseline, not realism. **Out of scope for the
  `MOO-World` migration** - it still uses its own `LspBridgeMooPort`/`listen(#5, ...)` two-port
  design unchanged (see "LSP service character + listener" below for why that design is correct,
  not a legacy leftover).

Add more named `test.ps1` profiles later by extending its `$profiles` table - no other script
logic is per-environment.

**A content project's `world.db`/exported `.moo` tree needs `.gitattributes` forcing LF line
endings, or a fresh Windows clone silently corrupts it.** Confirmed live: cloning `MOO-World`
without this produced a `world.db` with CRLF line terminators (Windows git's default `autocrlf`
checkout conversion), and the server refused to load it (`*** DBIO_READ_NUM: Bad number:
"3\r"`/`*** DB_LOAD: Cannot load database!`) - these are plain-text, line-based formats the
server's own parser reads byte-for-byte, not something git's line-ending normalization can safely
touch. `MOO-World` now carries a `.gitattributes` forcing `*.db`/`*.moo`/`corponyms.moo`/
`FORMAT_VERSION`/`builtins.json` to `eol=lf` - any other content project should carry the same one,
and this is worth checking first if a freshly-cloned world refuses to boot.

## Bootstrap verbs baked into every world (`Minimal.db` *or* real ToastCore-derived)

Two tiny verbs must exist on `#0` for the sidecar/live IDE to work against **any** world - a bare
`Minimal.db` world, or a real ToastCore-derived one - things ToastCore's own core + the old `$vcs`
used to provide implicitly for `Survive`'s world, now gone along with them. Neither appears in the
exported tree (`#0` has no corponym, per
moo-vcs-plan.md's invariant I3), so they only exist baked into the db file itself. A third,
optional verb (`do_start_script`, below) is worth adding at the same time even though nothing
currently requires it, purely because it enables a much cheaper bootstrap path for *future* worlds
once it exists.

- **`#0:user_connected`** — `notify()`s `#$#moodev-login-result ref: 0 ok: 1` followed by
  `#$#: 0` on every login. Without it, nothing tells the browser client a login succeeded
  (`$vcs:notify_login` used to own this), so the login screen would never dismiss even though the
  raw connection succeeded. **Both lines are required, not just the first** - confirmed live: a
  bare `notify(player, "#$#moodev-login-result ok: 1");` with no `ref:` field and no `#$#: <tag>`
  terminator compiles fine and looks right in isolation, but `Sidecar/McpFilter.fs`'s
  `classifyHashLine` only starts tracking a `#$#`-prefixed line as a real `moodev-*` message when it
  contains a `ref: ` field, and only ever `Emit`s the assembled message once a matching `#$#: <tag>`
  terminator line arrives - so a one-line notify with neither passes straight through to the
  terminal as plain text instead of reaching the browser's structured handler. The exact body:
  ```
  notify(player, "#$#moodev-login-result ref: 0 ok: 1");
  notify(player, "#$#: 0");
  ```
  (`0` is just a fixed, arbitrary tag - this is the only message of its kind, so there's no need for
  a fresh one per login.)
  **On a real ToastCore-derived world, `#0:user_connected` already exists** (a real, stock
  ToastCore verb doing real work - MCP negotiation via `$mcp:(verb)(@args)`, then
  `user.location:confunc(user)`/`user:confunc()` dispatch). Don't overwrite it - **append** these two
  `notify()` lines to the end of its existing code (`newcode = {@verb_code(#0, "user_connected", 0,
  1), "notify(...)", "notify(...)"}`) so both the real connection logic and the login signal work.
  Confirmed live that the two coexist fine: the real `#$#mcp version: ...` line and our
  `#$#moodev-login-result`/`#$#: 0` lines are independently recognized by `McpFilter.classifyHashLine`
  without conflict (the real MCP line doesn't match the `moodev-*` shape it filters for, so it passes
  straight through as plain terminal text, same as it would with no sidecar involved at all).
- **`#0:do_command`** — a minimal `;;`-eval shim: recognizes a raw `;;<code>` line and runs it via
  the real `eval()` builtin, letting a plain, unrecognized command fall through afterward (hence
  "I couldn't understand that." on every eval call - harmless noise, not a failure). This is the
  *entire transport* `Sidecar.MooEval` depends on (see its own doc comment) - it was quietly riding
  ToastCore's own built-in `#58:eval_cmd_string` the whole time Phases 1-6 were built and tested,
  which a bare `Minimal.db` world doesn't have. Without this verb, every sidecar eval (export,
  import, live IDE save, history, search, ...) hangs forever waiting for a response that never
  comes, rather than failing fast.
- **`#0:do_start_script`** (optional, not required for the IDE) - a generic eval entry point real
  ToastStunt already dispatches for free: `-c`/`--start-line` and `-f`/`--start-script` server
  startup flags call `#0:do_start_script(code)` directly (`ToastStunt/src/server.cc`'s
  `run_do_start_script`), with an empty MOO call stack (guard with `callers() && raise(E_PERM);` to
  keep it from being invoked any other way). Once this verb exists on a world, *future* worlds
  seeded from an export of that world can be bootstrapped non-interactively via `moo ... -f
  bootstrap.moo` instead of a manual `-e` session - see `MOO-World`'s own bootstrap commit, which
  added this specifically so `MOO-World-stable`'s own bootstrap (below) could reuse it. Body:
  ```
  callers() && raise(E_PERM);
  return eval(@args);
  ```

Both `do_command` and `user_connected` require `#0.wizard = 1` **and** `#0.programmer = 1` (two
independent flags - `eval()` itself checks `is_programmer()`, not `is_wizard()`) - not because the
*connecting player* needs those flags, but because **a verb runs with its owner's permissions by
default**, and both verbs are owned by `#0` itself.

On a bare `Minimal.db` world, `-e`/`--emergency` really is required for this *first* bootstrap -
confirmed live, and the reason is narrower than "no connectable account exists yet": `#3` (Wizard)
is already both connectable (`do_login_command` returns it unconditionally, even for a truly blank
first line - see "There is no real login/accounting yet" below) *and* already `wizard`+`programmer`
by `Minimal.db`'s own default. The actual blocker is that **no eval mechanism exists at all yet** -
`.program`/`.pr*ogram` is a genuinely native server command (`tasks.cc`'s `ICMD_PROGRAM`, confirmed
against source - it needs no verb), but it can only *reprogram* a verb that's already been
`add_verb()`'d; creating one from nothing needs `add_verb()` itself, a function call, which needs
some eval path to invoke - and none exists before `do_command` does. `-e`'s own console (`server.cc`
`emergency_mode()`) sidesteps this entirely: it's a real, independent MOO-code evaluator built into
the C++ core, using the exact same `;EXPR`/`;;CODE` syntax `Sidecar.MooEval`'s own `;;`-prefix
convention mirrors (short form evaluates one line; typing `;;` alone opens multi-line input, ended
by a lone `.`). Live-verified recipe for a fresh `Minimal.db`-derived world (one combined `;;`
block, since each separate `-e` console line runs as its own independent program with no shared
variables across lines):
```
;;
#0.wizard = 1;
#0.programmer = 1;
add_verb(#0, {#0, "rxd", "do_command"}, {"none", "none", "none"});
set_verb_code(#0, "do_command", {"if (length(argstr) >= 2 && argstr[1..2] == \";;\")", "  result = eval(argstr[3..$]);", "  if (!result[1])", "    notify(player, \"EVAL ERROR: \" + toliteral(result[2]));", "  endif", "  return 1;", "endif", "return 0;"});
add_verb(#0, {#0, "rxd", "user_connected"}, {"none", "none", "none"});
set_verb_code(#0, "user_connected", {"notify(player, \"#$#moodev-login-result ref: 0 ok: 1\");", "notify(player, \"#$#: 0\");"});
add_verb(#0, {#3, "rxd", "do_start_script"}, {"this", "none", "this"});
set_verb_code(#0, "do_start_script", {"callers() && raise(E_PERM);", "return eval(@args);"});
.
continue
```
(`continue` exits emergency mode and lets the server proceed to normal operation, still against
the same db - no restart needed.)

On a real ToastCore-derived world, there's already a live-connectable `wizard` player *and* a real
eval path (`#58:eval_cmd_string`), so this can be bootstrapped over a normal connection instead -
but with one live-confirmed gotcha: **fix `#0`'s own flags *before* `#0:do_command` exists, and do
the fixing via ToastCore's native single-`;` eval, not `;;`.** Once `do_command` exists, the server
tries it first for every command line, including `;;`-prefixed ones (confirmed in `tasks.cc`) - so
if `#0` isn't yet wizard+programmer, `do_command`'s own `eval()` call inside itself throws `E_PERM`
for literally every subsequent command, including the one meant to fix `#0`'s flags. The native
single-`;` command (real ToastCore's own `#58:eval_cmd_string`, or the server's built-in
recognition) doesn't go through the `eval()` builtin at all, so it isn't gated the same way - use
`; ; #0.wizard = 1; #0.programmer = 1;` (leading `;` for the command, a no-op `;` as the code's
first statement to defeat ToastCore's auto-`return`-prepend quirk for multi-statement bodies - same
double-semicolon idiom `Sidecar.MooEval`'s own doc comment describes, just via the native path
instead of `do_command`) to break the chicken-and-egg lock.

`executables/vcs-commit.sh` (the old `$vcs`-era shell-out script) no longer runs at all - retired
along with `$vcs` itself.

### Optional bootstrap verbs - the Errors tab

Unlike `user_connected`/`do_command` above, these two are **not** required for the IDE to work at
all - only for the Errors tab's live traceback stream. Confirmed via `git blame` that stock
ToastStunt (upstream, predating this fork's own patches) already calls
`#0:handle_uncaught_error(code, msg, value, stack, traceback)` and `#0:handle_task_timeout(tag,
stack, traceback)` automatically on every uncaught error/tick-or-seconds timeout
(`ToastStunt/src/execute.cc:557-625`, dispatch at `execute.cc:3201-3226`) - **no C patch needed**.
If the verb doesn't exist, ToastStunt silently falls back to its classic behavior (`notify()`-ing
the raw traceback straight to the connected player), so a world without these two verbs isn't
broken, it just doesn't feed the Errors tab.

- **`#0:handle_uncaught_error`** / **`#0:handle_task_timeout`** - format the traceback via the same
  `#$#moodev-*`/`#$#*`/`#$#:` multiline framing `moodev-edit-content` uses, then `return 1` (marks
  the error "handled" so the fallback plain-`notify()` doesn't *also* fire and double-print to the
  player). **The continuation-line shape is stricter than it looks** - confirmed against
  `Sidecar/McpFilter.fs`'s `classifyHashLine` (not just assumed from the doc comment describing the
  now-retired `$vcs:ide_fetch`/`ide_save`'s use of the same convention, which got this wrong the
  first time live-testing this feature): a continuation line isn't just `#$#* <content>` - it's
  `#$#* <tag> text: <content>`, where `<tag>` is the *first token* after `ref: ` in the header line
  (here, the literal `0`). Get the tag wrong (or omit the `text: ` marker) and `classifyHashLine`
  doesn't recognize the continuation at all - it passes the raw `#$#* ...` line straight through to
  the terminal as plain text instead of folding it into the structured message, which is exactly
  what happened before this was corrected:
  ```
  @verb #0:handle_uncaught_error this none this rxd
  @program #0:handle_uncaught_error
  {code, msg, value, stack, traceback} = args;
  notify(player, "#$#moodev-error ref: 0 kind: uncaught");
  notify(player, "#$#* 0 text: " + msg);
  for line in (traceback) notify(player, "#$#* 0 text: " + line); endfor
  notify(player, "#$#: 0");
  return 1;
  .

  @verb #0:handle_task_timeout this none this rxd
  @program #0:handle_task_timeout
  {tag, stack, traceback} = args;
  notify(player, "#$#moodev-error ref: 0 kind: timeout");
  notify(player, "#$#* 0 text: " + tostr(tag));
  for line in (traceback) notify(player, "#$#* 0 text: " + line); endfor
  notify(player, "#$#: 0");
  return 1;
  .
  ```
  Same `#0.wizard = 1`/`#0.programmer = 1` requirement as every other `#0`-owned bootstrap verb
  above - no separate `Sidecar`/`McpFilter.fs` change needed, since `#$#moodev-*` line recognition
  is already fully generic (`rest.StartsWith("moodev-")`, no allowlist).

## LSP service character + listener - the LanguageServer's own live connection

The LSP (`src/LanguageServer`) resolves hover, go-to-definition, and builtin docs live now, via a
direct connection to the Sidecar's own `/lsp-bridge` endpoint (`src/Sidecar/LspBridge.fs`) - not
the once-loaded static export tree these used to be read from. This needed a way for the LSP's
connection to coexist with a browser tab's own Wizard connection without kicking it: ToastStunt
kicks the currently-connected session whenever the **same player object** logs in a second time
(confirmed live, repeatedly) - it's per-character, not "only one live connection total." So each
world gets two small, additive bootstrap objects (no corponym, same as `#0` itself - never appear
in the exported tree, only exist baked into the db file):

- **A dedicated service character** (`#4` on `MOO-World`) - `wizard`+`programmer` flags, never used
  interactively, just a distinct login identity for the LSP's own connection.
- **A dedicated listener object** (`#5` on `MOO-World`) bound to its own port (`7780` for
  `MOO-World` - see `test.ps1`'s `LspBridgeMooPort`/`LspListenerObj` profile fields) via the
  `listen()` builtin, with its own copy of the two bootstrap verbs described above:
  - **`:do_login_command`** - unconditionally `return #<service character>;` (mirrors `#0`'s own
    `return #3;` exactly, just a different target object).
  - **`:do_command`** - the identical `;;`-eval shim `#0:do_command` has, needed because this verb
    dispatches on `tq->handler` (the listener object for that connection), not always `#0` -
    confirmed live: without this, `Sidecar.MooEval`'s `;;`-eval protocol never fires for a
    connection through this listener at all, since there's no `<listener>:do_command` to catch it
    (the server's own "I couldn't understand that." fallback swallows everything silently instead).

`#0:do_login_command` itself stays completely stock (`return #3;`, untouched) - the LSP's identity
comes entirely from *which port* it connects to, never from anything it types.

**This two-object/two-port design is not legacy complexity waiting to be simplified - it was tried
and live-disproven.** A same-port design (LSP sends a distinct `connect <sentinel-word>` line over
the *same* port a browser tab uses, with `#0:do_login_command` pattern-matching that word) looks
appealing - one fewer object, no second `listen()` to rebind - and was actually implemented and
bootstrapped once, before being reverted. It fails because of how ToastStunt dispatches
`do_login_command` for a brand-new connection: **the very first dispatch carries an empty `args`
list, before any input the client sends can be read** - confirmed live by instrumenting
`do_login_command` to record its own `args` into a property and connecting with *zero bytes sent*:
the connection still completed a full login. Since the stock verb returns `#3` unconditionally
regardless of `args`, that first blank dispatch logs the connection in immediately; a same-port
sentinel line sent right after `ConnectAsync` arrives too late, after login has already completed,
and is processed as an ordinary (unrecognized) command from `#3` instead. There is no way to tell
"the automatic pre-input blank dispatch" apart from "the user genuinely sent a blank line" from
inside `do_login_command` alone, so a shared port fundamentally cannot route by login text - only
the port itself (via a listener object with its own fixed-identity `do_login_command`) can
disambiguate reliably, which is exactly what this design already does. Don't re-attempt the
same-port version without solving that dispatch-ordering problem first.

`listen()` doesn't persist across a server restart, unlike the bootstrap verbs/objects themselves
(those live in the db) - `test.ps1` re-binds it every launch, right after the MOO server itself
comes up, wrapped in a MOO `try`/`except` so re-running against an already-up server (which already
has it bound) doesn't surface a scary "already listening" error.

## There is no real login/accounting yet - this is intentional for now

`#0:do_login_command()` is untouched stock `Minimal.db` (`ToastStunt/docs/README.Minimal`) -
literally `return #3;`, ignoring whatever was typed entirely. This isn't a client bug or a gap in
the bootstrap verbs above: per `do_login_task` (`ToastStunt/src/tasks.cc:894`), the server calls
`#0:do_login_command` unconditionally for *every* line an unauthenticated connection sends - there
is no separate server-native "connect"/"create" parsing at all, anywhere. Implementing that (name
lookup, password checks, account creation) has always been `do_login_command`'s own job, which real
ToastCore does in MOOcode and bare `Minimal.db` simply doesn't.

Practical effect: **typing anything (any non-empty username, any or no password) into the browser
client's login form always logs you in as Wizard.** There's no real distinction between accounts,
no password check, no way to create a second player. Deliberately left this way for now (single-
developer tool) rather than building real accounting - revisit if/when this world needs more than
one real user.

**A brand-new connection completes this login before it ever sends anything, not just when it
types something trivial.** Confirmed live: `do_login_task` (`tasks.cc`) dispatches
`do_login_command` with an empty `args` list as the very first tick after a connection is accepted,
before the client's own first line is read; since the stock verb returns `#3` unconditionally for
*any* `args` including empty, that first blank dispatch alone finishes the login. This is the root
cause behind the reverted same-port LSP design above, not just a curiosity - anything relying on
"the first thing this connection sends" to decide identity will observe an already-logged-in
connection instead.

## Running the MOO server for local testing

The `moo` binary is a Linux ELF built under WSL2 — it does not run directly from Windows.
`test.ps1` (repo root) starts everything (MOO server, Sidecar, LSP server, client dev server)
for a chosen `-Database` profile (`MooWorld` by default - see "Two MOO instances" above) in one
go; to start just the server by hand from PowerShell:

```powershell
wsl -d Ubuntu -- bash -c "cd /mnt/c/dev/moo/moo-dev/ToastStunt/run && /mnt/c/dev/moo/moo-dev/ToastStunt/build/moo world.db world.db.new 7777 -i /mnt/c/dev/moo/MOO-World"
```

For automated/headless testing, use `test-instance-start.ps1` / `test-instance-stop.ps1` instead
(see "Two MOO instances" above) rather than hand-rolling a second launch of this command — it
handles the `survive.test.db` copy, the isolated Sidecar content tree, and starting/stopping the
Sidecar/LSP/Client alongside it, all in one call.

The `-i` flag points FileIO at whichever content project's tree the profile is for (`MOO-World` by
default) - a holdover from the retired `$vcs`'s file writes there; nothing on the MOO side does its
own file I/O anymore (the sidecar owns all of that from outside via `eval()`), but `test.ps1` still
passes it per-profile for consistency.

It listens on `127.0.0.1:7777`. Connecting from localhost suppresses the MOO's own welcome banner
(a documented HAProxy source-IP-rewrite quirk) — this is expected, not a broken connection; go
straight to `connect wizard` on a fresh `Minimal.db`-derived db (see "Bootstrap verbs" above - a
truly bare `Minimal.db` with neither bootstrap verb still accepts the login, it just won't notify
the browser client or answer any sidecar eval).

## Running the sidecar + client for local dev

```powershell
cd C:\dev\moo\moo-dev
dotnet tool restore
dotnet watch run --project src\Sidecar\Sidecar.fsproj
```

```powershell
cd C:\dev\moo\moo-dev\src\Client
npm install
npm run dev
```

Then open the client dev server URL in a browser.

**This bare `dotnet watch run` defaults `Moo:TreeDir` to `../MOO-World`**
(`Sidecar/appsettings.json`) - a relative-sibling-checkout default, purely a convenience for this
project's own layout (see "no content project lives in this repo" note at the top) - every
save/add/delete action will commit real changes there if such a checkout exists alongside this
one. That's correct for interactively working against a real dev world (what `test.ps1` already
does, passing `--Moo:TreeDir` itself), but wrong for automated/Playwright-driven verification - use
`test-instance-start.ps1` for that instead (see "Two MOO instances" above), which points the
Sidecar at an isolated scratch tree automatically. This exact confusion (manually launching a
"test" Sidecar without overriding `TreeDir`) previously left real, if unmerged/unpushed, commits
and WIP refs in the real `Survive` repo across several sessions.
