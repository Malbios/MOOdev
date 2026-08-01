# MOO Version Control — Development Plan

A sidecar-owned, git-backed version control system for a ToastStunt MOO, designed
to make history easy to *use* rather than merely to exist.

---

## 1. Current state

Already built:

- **ToastStunt server** — the runtime. Includes a custom `handle_verb_programmed()`
  builtin hook and an in-database `$vcs` package.
- **Sidecar** — long-lived process bridging the web app and ToastStunt.
- **LSP server** — MOOcode language services.
- **Web app** — Vite + F# + Fable, Monaco-based editor with object/verb/property tree.

Being retired or demoted:

- **`$vcs` (in-MOO)** — retired. Version control moves entirely to the sidecar. The
  MOO stops owning history, files, and diffs.
- **`handle_verb_programmed()`** — demoted from *mechanism* to *notification*. Nothing
  in the new design depends on it. Keep it only if maintaining the fork patch is
  already cheap; it saves a poll when you program over telnet instead of the IDE.
  Delete it without hesitation if rebasing on upstream ToastStunt becomes annoying.
  Note that it will **not** fire for edits made through the IDE, since those go via
  `set_verb_code()` rather than `.program`.

---

## 2. What the system is for

Five questions, each of which should be one click in the IDE. Every design decision
below exists to serve one of them.

1. What did this verb look like before?
2. When did this break?
3. What is different between dev and production?
4. What did I change yesterday?
5. Why is `$room` pointing at `#14`?

If a proposed feature does not serve one of these, it is out of scope.

---

## 3. Invariants

These are the load-bearing decisions. Changing one of them invalidates work downstream,
so treat them as fixed unless something concrete forces a revision.

**I1 — The sidecar owns version control.** The MOO owns runtime state only. The MOO
never writes files, never shells out, never stores history. It exposes exactly two
things: the ability to read its own structure, and the ability to have code written
into it. Both are already available through standard server builtins.

**I2 — Corponyms are identity.** Paths are keyed on the `#0` corponym (`$room/`), never
on objnum. Objnum is recorded inside files as informational metadata only. This is what
makes `git log --follow` survive recycling *and* what makes dev→prod promotion possible
at all, since the two instances allocate objnums independently.

**I3 — No corponym, no versioning.** This is the core/content boundary, and it is the
one MOO already uses. Assigning a corponym is the deliberate act of declaring "this is
code, not world."

**I4 — Serialization is deterministic.** Fixed `verb_code()` flags, sorted key order
everywhere, stable line endings. MOO decompiles verbs from bytecode rather than storing
source, so any wobble in the flags produces hundreds of phantom diffs and the history
becomes unreadable. This is the single most likely thing to quietly ruin the project.

**I5 — Defining-object rule for property values.** A property's value on the object that
*defines* it is schema and gets versioned. The same property on a descendant is state and
does not. Small opt-out list on `#0` for defining-object properties that are really state
(counters, timestamps, caches).

**I6 — Production is a branch.** `main` is dev. A `production` ref tracks what is
deployed. Promotion, rollback, and dev/prod diffing are then all the same machinery.

**I7 — Round-trip fidelity is the acceptance test.** Export → import into a fresh
instance → export again → byte-identical. Nothing else is trustworthy until this passes.

---

## 4. Scope

| Item | Versioned |
|---|---|
| Verb code | Yes |
| Verb metadata (names/aliases, args, perms, owner) | Yes |
| Property definitions (name, owner, perms) | Yes |
| Property value on the **defining** object | Yes |
| Property value on descendants | No |
| Parent, object flags, object owner | Yes |
| Corponym map (`#0` properties → objects) | Yes |
| Location, contents, connections, player state | No |
| Objects without a corponym | No |

---

## 5. Repository format

### Layout

```
corponyms.moo            # sorted $name -> #objnum map
objects/
  room/
    object.moo           # parent, flags, owner, property definitions + defaults
    verbs/
      look_self.moo
      tell_lines.moo
  string_utils/
    object.moo
    verbs/
      ...
```

Directory name is the corponym with the `$` stripped. One verb per file — this is what
makes per-verb history, blame, and `--follow` work.

### File format

Aim for something close to `@dump` output: `@verb` / `@program` / `.` terminator. Two
reasons. It is trivially machine-parseable, and the worst-case recovery path is pasting
a file straight into a wizard connection with no tooling at all. That is what
future-proofing means here — the files stay useful even if the sidecar dies.

Sketch of a verb file:

```
@verb $room:"look_self" this none this rxd #2
@program $room:look_self
"Describe this room to the caller.";
player:tell(this:title());
.
```

Verb metadata lives in the verb's own file, **not** in a shared manifest. A shared
manifest means adding one verb churns a file that every other verb also touches, which
destroys the signal in per-verb history.

### Verb filenames

Verb name-specs contain characters that are awkward in paths (`l*ook`) and may collide
(the same name defined twice on one object). Rule:

- Filename is derived from the first alias, with `*` stripped, sanitized.
- On collision, append a numeric suffix.
- The **canonical full name-spec always lives in the file header**, and reconciliation
  matches on the header, never on the filename.

That way a filename-derivation change shows up as a git rename rather than as a
delete-plus-add.

---

## 6. Component responsibilities

- **ToastStunt** — runtime. No VCS role.
- **Sidecar** — owns the git repo, the exporter, the importer, the serialization format,
  and the promotion logic. Talks to one or more MOO instances over a wizard connection.
- **LSP** — unchanged, but shares the compile-check trick (below) with the importer.
- **Web app** — history UI only; all logic lives in the sidecar.

Keep every MOO-facing call behind **one narrow interface** in the sidecar. That is the
whole portability story for now — if another ToastStunt project ever wants this, the
work becomes a refactor rather than a rewrite. Do not build plugin tiering today.

---

## 7. Build phases

### Phase 0 — Write the format spec

Deliverable: a `FORMAT.md` in the sidecar repo defining the layout, the file grammar,
the sort orders, the `verb_code()` flags, and the filename derivation rules.

*Done when:* you can hand-write a valid export tree from the spec alone.

This is boring and it is the most valuable hour in the project. Everything downstream
either conforms to this document or is a bug.

### Phase 1 — Exporter

Read-only, therefore safe to iterate on aggressively against the live dev instance.

Walk `#0`'s corponyms → resolve objects → emit `corponyms.moo`, `object.moo`, and one
file per verb, per the spec.

*Done when:* running it twice in a row against an unchanged MOO produces zero diff.
That is invariant I4 under test, and it is worth catching now rather than after you
have a thousand commits.

### Phase 2 — Importer

Reads a tree (or a subset of files) and applies it to a target instance. Must be
**diff-driven and idempotent** — only touch what actually differs. Non-negotiable for
promotion, where the target is a live server.

Application order matters:

1. Resolve corponyms to objnums on the target; create missing objects and assign their
   corponyms immediately.
2. Set parentage — parents before children, topologically sorted.
3. Property definitions (`add_property` / `delete_property` / `set_property_info`).
4. Property values on defining objects.
5. Verb definitions (`add_verb` / `delete_verb` / `set_verb_info`).
6. Verb code (`set_verb_code`) last.

`$foo` inside verb code compiles to a runtime property lookup, so corponym resolution is
not needed at compile time — but object creation is, so step 1 stays first.

**Two-pass safety.** Before applying anything, compile-check every verb by adding it to a
scratch object and deleting it. `set_verb_code()` returns the compiler's error list and
leaves the verb untouched on failure, so you get a full error report before touching a
single live verb. This is the same trick the LSP uses for diagnostics — share the code.

*Done when:* you can revert one verb to a week-old version from the CLI.

### Phase 3 — Round-trip test (gate)

Export → import into a fresh ToastStunt instance from a minimal core → export again →
assert byte-identical.

*Done when:* it passes in CI, or at least in a script you run before every change to the
format.

**This is the gate.** It proves the format is complete, the serialization is
deterministic, and the importer is faithful — the three things every feature below
assumes. If it fails, it fails loudly and names the field you forgot. Do not build UI
before this passes.

### Phase 4 — Git integration

- Every save from the IDE commits to a hidden ref (`refs/moo/wip/<session>`), so nothing
  is ever lost and `git log` / `git branch` / any GitHub UI stay clean.
- After N minutes idle, or on explicit checkpoint, squash the batch into one commit on
  `main` with an auto-generated message: `$room: look_self, describe; $player: +tell_lines`.
  Overridable when a change deserves a real message.
- A prune job expires wip refs older than N days. `git gc` will not collect anything
  reachable, so this must be explicit or the repo grows without bound.
- Use a library binding rather than shelling out (LibGit2Sharp if the sidecar is .NET) —
  in-process commits, custom ref manipulation, no fork per save.

*Done when:* a day of editing yields a readable `main` log and a recoverable wip stream.

### Phase 5 — IDE surfaces

Now the five questions get answered:

- **Per-verb timeline** — `git log --follow` scoped to one verb file, rendered as a
  history list or scrubber in the sidebar. Answers Q1 and Q2. Nobody browses a global
  log; scoping to a verb is what makes noisy history navigable.
- **Diff view** — Monaco's built-in diff editor. Old version vs current, any two points.
- **Content search** — `git log -S'<string>'` finds the commit that introduced or removed
  a string. This is how you realistically find "where did that check go," and it works
  fine over a noisy log.
- **Restore** — one button, Phase 2 pointed at the current instance.
- **Corponym history** — diff of `corponyms.moo` over time. Answers Q5.

### Phase 6 — Promotion

- `git diff production main` — the pre-deploy review. Answers Q3.
- Apply the changed files to the prod instance via the Phase 2 importer.
- Fast-forward the `production` ref on success.
- Re-export prod and assert it matches the ref. This is simultaneously deploy
  verification and drift detection.
- Rollback is the same operation against an earlier commit.

Promotion is not a second system. It is the importer with a different target host.

### Phase 7 — Optional, only if the absence is felt

- Reconciliation scan for out-of-band edits (telnet `.program`, generated verbs). Only
  needed if you regularly edit outside the IDE.
- Wiring `handle_verb_programmed()` as a change notification to skip polling.
- Recycle/attic handling and an identity map on `#0`. Only relevant if you start
  recycling versioned objects, which invariant I3 makes unlikely.

---

## 8. Known hazards

- **Decompilation normalizes formatting.** MOO does not store your source. If you
  hand-edit a file in git and import it, the next export will reformat it. Mitigation:
  always re-export and commit immediately after an import.
- **Import is not transactional.** MOO has no rollback across a partial apply. The
  two-pass compile check in Phase 2 is what stands in for one — do not skip it.
- **Objnum drift between instances is guaranteed** the moment a player builds something
  on prod. Invariant I2 is the only thing protecting you here; any objnum that leaks into
  an identity position is a latent bug.
- **Ownership and permissions** on import: verbs and properties carry an owner. Importing
  as a wizard can silently reassign them if you are not explicit.
- **`$` resolution failures** on import mean the target lacks a corponym the source had.
  Fail loudly with the missing name; do not guess.

---

## 9. Explicitly deferred

Not building these, and not designing around them:

- Branch-per-feature workflows. You cannot have two branches live in one MOO, and
  spinning a second instance per branch is not a problem you have.
- Multi-user concurrency, optimistic locking, conflict UI. Solo project.
- General-purpose plugin tiering for other MOO servers. Invariant: one narrow interface,
  refactor later if it ever matters.
- Versioning property values on descendants, or any world state.
- Merge, rebase, or three-way conflict resolution inside the IDE.

---

## 10. Immediate next step

Phase 0, then Phase 1, then the Phase 3 gate. Do not write UI until the round-trip test
is green.
