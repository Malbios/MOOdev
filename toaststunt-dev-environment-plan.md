# ToastStunt Development Environment — Project Plan

A modern development environment for a ToastStunt MOO: browser-based client, integrated
MOOcode editor, and automatic version history. Built alongside the game itself.

---

## Settled decisions

These came out of the design discussion and shouldn't be relitigated without new information.

| Area | Decision |
|---|---|
| Server | Fork ToastStunt. Patches allowed — no reusability constraint. |
| Baseline DB | ToastCore, imported whole into git as the initial commit. |
| VCS direction | **One-way capture only.** MOO → tree → commit. No deploy-from-tree in v1. |
| Identity in tree | Names, never object numbers. `lookups.toml` maps name ↔ id, sectioned by category. |
| Properties | Values are state (checkpoint only). Definitions are schema. Both deferred past v1. |
| Client | Single app. Capabilities derived from the connected character, not a mode toggle. |
| Sidecar privilege | **Zero.** One MOO connection per browser session, authenticated as that character. |
| Diagnostics | Compile probe against the live server, not a reimplemented compiler. |
| Frontend | Fable + Monaco. Compiles to JS, so Monaco's API is a direct call. |
| Language server | F#, LSP over websocket. VS Code works as a free test harness. |

### Why one-way capture is enough for now

Nearly every hard problem from the design discussion — echo suppression, deploy locks,
reconciliation, structure-script ordering, idempotency, branch merging — exists only because
of **bidirectional** sync. Capture-only has none of them. It cannot corrupt anything, because
it never writes to the MOO.

Note the editor does not break this. When you save a verb from the IDE it calls
`set_verb_code()`, the hook fires, and it gets committed with your name. That's not echo —
that's a real edit by a real person, captured correctly. Echo was only ever a problem for
*bulk deploy from the tree*, which doesn't exist yet.

---

## Architecture

```
Browser (Fable + Monaco)
    │  websocket
    ▼
Sidecar (F#, unprivileged)          LSP server (F#)
    │  one MOO connection               │  reads git tree + metadata dump
    │  per browser session              │  answers navigation queries offline
    ▼                                   │
ToastStunt (forked)  ◄──────────────────┘  (compile probe only)
    │  handle_verb_programmed()
    ▼
$vcs package (MOOcode)
    │  FileIO write + exec() git commit
    ▼
git repo
```

Two things worth noticing:

- **Capture never touches the sidecar.** `$vcs` writes the file with FileIO and shells out to
  git via `exec()`. Fewer moving parts, and history keeps accruing even if no web service is
  running.
- **The LSP server is offline for navigation.** It parses the tree and a metadata dump. It only
  needs a live MOO for diagnostics.

---

## Milestones

Each milestone is shippable and useful on its own.

### M0 — Substrate

Build ToastStunt under WSL2, run ToastCore, connect over telnet. Fork the repo, commit
an unmodified baseline so your patches are always visible as a diff.

*Proves:* the environment works, and you know how to rebuild it.

### M1 — The spine

F# sidecar bridging websocket ↔ telnet. Minimal browser terminal: connect, log in, play.
No editor, no styling beyond legible.

*Proves:* the session model. From here you never need telnet again.

### M2 — Capture

- C patch: `handle_verb_programmed(obj, vname, programmer)` on every successful compile,
  following the existing `handle_uncaught_error` idiom.
- `$vcs` MOOcode package: derive the path, write via FileIO, `exec()` a git wrapper.
- Metadata dump script (`objects()` walk → JSON) and the F# tree writer.
- Import ToastCore as the initial commit.

*Proves:* history accrues automatically, with attribution, from now on.
*Defers:* everything about the write direction.

### M3 — Editor v1

Monaco in the client, shown when the connected character has the programmer bit.
Open a verb, edit, save via `set_verb_code()` over that character's own connection.
Monarch grammar for highlighting. Diagnostics from the compile probe: ship the buffer to a
scratch verb, collect the returned error list, discard.

*Proves:* the developer experience stops hurting. This is the milestone that changes your day.
*Defers:* navigation, completions, anything requiring a resolver.

### M4 — Language intelligence

F# lexer/parser/AST for MOOcode. Overlay the metadata graph. LSP server providing
go-to-definition, find-references, completions, hover, signature help from `function_info()`.

*Proves:* the codebase becomes navigable. Whole-program queries are exhaustive here in a way
they aren't in most languages — the corpus is finite and entirely yours.

### M5 — Context-awareness

The thing no existing MOO tooling has. Object browser tied to your character's location.
Click the door you're standing next to, see its verbs, edit one. "Edit what's in front of me"
instead of "remember the object name."

*Proves:* the single-app decision was worth it.

### Deferred indefinitely

Deploy-from-tree, branching, instance manager, structure scripts, property tracking, CI.
Add them when a second developer appears or when you have a live world you can't disturb.
None of the above forecloses them.

---

## Game work runs in parallel from week one

Write MOOcode for the world starting immediately, using whatever tooling exists at the time.
The friction you hit writing real verbs is a better priority signal than any planning document.

Two conventions worth adopting on day one:

- Don't shadow names ToastCore already uses. Git history already distinguishes your code from
  core, so no prefix scheme is needed beyond that.
- Corify anything that carries code. It's already standard MOO practice and it keeps the tree's
  naming stable for free.

---

## Known hazards

Things that will bite, in rough order of when you'll hit them.

**FileIO path restriction.** FileIO is confined to a subdirectory of the server root. The git
repo has to live where the MOO can reach it, or be symlinked. Check this at M2, not later.

**`exec()` constraints.** Binaries must live in the server's `executables/` directory, and the
builtin is wizard-only. Ship a wrapper script. Calls implicitly suspend, so a slow git operation
won't block the server.

**Verb names are patterns.** `l*ook` matches `l`, `lo`, `loo`, `look`. Symbol lookup is matching,
not dictionary hits. Verbs are also plural per verb and order-sensitive within an object —
first match wins. Build this into the resolver from the start; retrofitting is painful.

**Multiple inheritance.** ToastStunt's chain is a DAG. Take the traversal order from the C source
rather than guessing, or go-to-definition will occasionally point at the wrong ancestor.

**Invisible calls.** `obj:(name)()` and anything through `eval()` can't be resolved statically.
Find-references must say so rather than implying completeness — "no references" can't mean
"safe to delete."

**Tick limits.** Simulation depth runs into MOO's per-task tick and seconds budgets. Plan on
`suspend()` and the background/threading extensions early rather than discovering ceilings
mid-system.

**Waifs.** They carry their own property values and serialize recursively, and shared references
alias badly. Not a v1 concern since properties aren't tracked — but when properties arrive, have
the exporter refuse waif-valued ones loudly rather than silently producing something wrong.

---

## Open follow-ups

Discovered during the `l*ook` code-lookup bug fix (2026-07-27) — real, reproduced-on-source
issues, not yet fully closed out.

**`verbcasecmp` fails on a verb's own literal star-name.** `verb_code`/`set_verb_code`/
`verb_info`/`verb_args` all resolve their name argument through `db_find_defined_verb` ->
`verbcasecmp(declared_name, search_word)` (`toaststunt/src/db_verbs.cc`, `utils.cc:76-110`).
When `search_word` itself contains a literal `*` — i.e. passing a verb's own declared name
straight back in, which any tooling that iterates `verbs(obj)` will naturally do — the
star-handling logic breaks and the lookup fails with `E_VERBNF`, even though the name is
completely valid. Confirmed by hand-trace and empirically (`verbcasecmp("l*ook", "l*ook")` == 0).
Fixed in all four `$vcs` call sites that hit it (`ide_fetch`, `ide_save`, `capture_verb`,
`export_metadata` — see `Survive` commits `9195377`, `7fda606`, `48e8800`, `20e7d0e`) by
stripping `*` from the name before it's used for lookup, while keeping the original name for
anything client-facing/stored. **Not yet done:** a systematic audit of the rest of the imported
ToastCore corpus (~1964 verb programs) for the same pattern. Spot-checked three likely candidates
(`Code_Utilities/8_show_verbdef.moo`, `Code_Utilities/12_verb_documentation.moo`,
`Generic_Database/20_proxy_for_core.moo`) and found them safe (try/except-wrapped, integer-index
lookup, or user-typed names respectively) — but that was inference from three examples, not a
full grep-and-verify pass.

**`verbcasecmp`'s pointer-equality fast path masks the bug when it shouldn't.**
`verbcasecmp` (`utils.cc:85-87`) short-circuits `if (verb == word) return 1;` before running its
(buggy) star-matching logic at all. Since `verbs(obj)` returns a reference-counted alias of the
verbdef's own stored name (not a copy), code that pipes a `verbs()` result straight into
`verb_info`/`verb_args`/`verb_code` without any intervening string operation "accidentally" works
today, for the *specific* corpus currently loaded — proven fragile by forcing a fresh allocation
of the identical value (`vname + ""`), which immediately fails the same way. Any future refactor
that copies the string before the lookup (even a harmless-looking one) will silently break, with
no compile-time warning. Worth keeping in mind for M4's resolver work, which will be doing exactly
this kind of by-name verb lookup pervasively.

**`shutdown()` can race an in-flight `$vcs` git capture.** `handle_verb_programmed`'s capture path
calls `exec()` (`vcs-commit.sh`), which suspends the calling task but runs the actual git
add/commit as a detached subprocess. If the server process exits (via `;shutdown();` or otherwise)
before that subprocess finishes, the commit is left half-done: a stale `.vcs-commit.lock` and a
staged-but-uncommitted change in the working tree, with no error surfaced anywhere. Hit this for
real this session (the `export_metadata` fix's own capture got cut off this way) and recovered
manually. Needs one of: (a) block `shutdown()` until any in-flight `vcs-commit.sh` finishes, (b)
have `vcs-commit.sh` itself detect and clean up a stale lock from a previous killed run before
proceeding, or (c) both.

---

## Repository layout

Three things with different lifecycles:

- **`toaststunt/`** — fork of `github.com/lisdude/toaststunt`. Keep `master` tracking upstream and
  your patches on a branch, so `git merge upstream/master` surfaces conflicts only where you
  actually changed the server. Don't absorb this into a monorepo.
- **`world/`** — the content tree. Initial commit is stock ToastCore, imported verbatim.
- **`tools/`** — F# sidecar, LSP server, DB parser, `moo-eval`.

`world/` and `tools/` can share a repo. A `CLAUDE.md` at each root, pointing at this plan.

---

## Reference documentation

Fetch these rather than relying on training data — MOO material is sparse and much of what
exists describes LambdaMOO, not ToastStunt.

- ToastStunt server + changelog: `github.com/lisdude/toaststunt`
- ToastStunt docs: `github.com/lisdude/toaststunt-documentation`
- ToastCore: `github.com/lisdude/toastcore`
- Checkpoint file format: `lisdude.com/moo/toaststunt_anatomy/`
- Programmer's manual and collected guides: `github.com/SevenEcks/lambda-moo-programming`

When server behaviour matters, the C source is the authority. `db_file.cc` for the checkpoint
format, the verb dispatch code for inheritance traversal order.

---

## MOOcode gotchas

Things that look like other languages and aren't. Most of these produce code that compiles and
misbehaves rather than code that errors.

- **Lists are 1-indexed.** `list[1]` is the first element. `$` inside an index means "last."
- **`verb_code()` returns a list of strings, one per line.** `set_verb_code()` takes the same.
  Never a single string with newlines.
- **`set_verb_code()` returns a list of compile errors** and leaves the verb unchanged on
  failure. An empty return means success. This is the basis of the diagnostics design.
- **Verbs need the `x` bit to be called from other code.** A verb without it is command-only.
  Silently unreachable otherwise — a very common early mistake.
- **Verb arg specs are `dobj prep iobj`**, e.g. `this none this`, `any any any`. Not a parameter
  list. Actual arguments arrive in `args`.
- **`pass(@args)`** calls the parent's version of the current verb. There is no `super`.
- **Errors are first-class values** (`E_PROPNF`, `E_VERBNF`, `E_PERM`, `E_TYPE`, `E_RANGE`).
  With the verb's `d` flag set they raise; without it they're returned as values. This changes
  control flow, so know which mode a verb is in.
- **`try ... except id (E_FOO) ... endtry`**, plus the inline form `` `expr ! E_FOO => fallback' ``.
- **`$foo` is sugar for `#0.foo`.** It's a property lookup, not a namespace sigil.
- **Built-in variables** in every verb: `this`, `caller`, `player`, `verb`, `args`, `argstr`,
  `dobj`, `dobjstr`, `prepstr`, `iobj`, `iobjstr`.
- **Verb names are patterns and plural.** `l*ook get take` is one verb with wildcard matching.
  Dispatch scans the object's verb list in order and takes the first match, so order is
  semantics.
- **Tick and seconds limits** end tasks mid-execution. `suspend(0)` yields and resets the budget.
  Any loop over many objects needs one.
- **ToastStunt specifics:** maps (`["k" -> v]`), WAIFs, anonymous objects, multiple inheritance,
  and `true`/`false` as parser keywords rather than variables.
- **Verify string escapes** in the target version before assuming `\n` works — classic MOO
  strings support very little escaping.

---

## Verify before building

Answer these against the running instance in M0. Each one has a design decision resting on it.

1. Exact return shape of `set_verb_code()` on a compile error.
2. FileIO's path restriction — the one result that could force capture back into the sidecar.
3. `exec()` sandbox rules and whether the implicit suspend behaves as documented.
4. `function_info()` output shape, which becomes the LSP's builtin table.
5. Verb dispatch order under multiple inheritance, cross-checked against the C source.
6. Checkpoint format against a real file containing maps and waifs.

Use ToastCore, not Minimal — Minimal exercises almost none of this.

**Connection quirk:** connecting from localhost suppresses the welcome banner, because of
HAProxy source-IP rewriting. Expected behaviour, not a broken connection. It can be disabled
in the server options if it gets confusing.

---

## Testing

- **DB parser** — fixture checkpoints committed to the repo, including a hand-built one with
  maps, waifs, anonymous objects, and multiple inheritance. Pure function, easy to test.
- **MOO parser** — ToastCore is the corpus. "Parses every verb in ToastCore without error" is a
  real bar, and it's thousands of verbs of genuinely weird historical code.
- **Round-trip** — export ToastCore, write the tree, re-read, compare. Catches metadata you
  forgot to capture.
- **Integration** — anything touching the live server runs against a throwaway instance restored
  from a known checkpoint, never against the world you're building.

---

## Working safely before capture exists

Until M2 ships there is no history. An agent with wizard access can overwrite a verb with no
way back.

- Checkpoint before any session that writes to the MOO. `dump_database()` is one call.
- Keep `moo-eval` read-only at first — evaluation and inspection, no `set_verb_code`, no
  `recycle`, no property writes. Add write capability once capture is live.
- Do experimental work on a copy of the DB, not the world you're authoring.



M0, then M1. If M1's websocket bridge feels good, the rest of the plan is assembly rather than
invention.
