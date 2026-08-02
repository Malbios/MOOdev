# MOO VCS export format — FORMAT.md

Phase 0 deliverable of `moo-vcs-plan.md` (repo root). This document is the gate: if you can
hand-write a valid export tree from this spec alone — a `corponyms.moo`, one `object.moo`, one
verb file — with no ambiguity about sort order, flags, or filename derivation, Phase 0 is done.
Everything downstream (exporter, importer, round-trip test) either conforms to this document or
is a bug, per invariant I4/I7 in the main plan.

Grounded against the current (being-retired) `Survive/VCS/*.moo` package, its real output tree,
and the ToastStunt C source (`verbs.cc`, `property.cc`, `db_verbs.cc`, `db_properties.cc`,
`objects.cc`, `parser.y`, `unparse.cc`) — not assumed from the plan text alone.

---

## 0. Format version

Every export tree has a top-level `FORMAT_VERSION` file, containing a single line: the integer
format version (starts at `1`). No prior version of this format existed with a version marker —
the retired `metadata.json` grew four incompatible shapes across real commits with nothing to
detect that — so this file exists from day one specifically so a future format change is
self-describing rather than inferred from field presence.

---

## 1. Directory layout

```
FORMAT_VERSION            # "1"
corponyms.moo             # sorted $name -> #objnum map
objects/
  room/
    object.moo            # parents, flags, owner, property definitions + defining-object values
    verbs/
      look_self.moo
      tell_lines.moo
  string_utils/
    object.moo
    verbs/
      ...
```

Directory name under `objects/` is the corponym with the `$` stripped. One verb per file. Only
objects with a `$0` corponym get a directory — no-corponym-no-versioning (I3) — which is a
deliberate change from the current system (see §7).

**Exception: `#0` (System Object) always gets a directory, at `objects/0/`, when it has no
corponym.** By construction it can't easily have one pointing at itself (corponyms are properties
*on* `#0` pointing elsewhere), yet it's exactly where sidecar/live-IDE bootstrap verbs live
(`#0:user_connected`, `#0:do_command` — see MOOdy's `CLAUDE.md`'s "Bootstrap verbs" section) and
needs to be visible/editable through the same tooling as everything else. `object.moo` renders
`@object #0` (a raw objnum, per §3's own grammar) instead of `@object $name`, and its verb files
render `@verb #0:"..." ...`/`@program #0:...` the same way — a fabricated `$0` would look like a
real corponym that doesn't exist. `"0"` is safe as a directory/map key precisely because it can
never collide with a real one: MOO property names can't start with a digit. **If `#0` already has a
real corponym** (real ToastCore-derived worlds already corify it as `$sysobj`, unlike
`Minimal.db`/`Survive`, where `#0` has none), the normal corponym-driven export above already
handles it under that name — this exception is skipped in that case, so `#0` never gets exported
twice under two different names. This exception is **export/read-model only** — the
importer/promotion pipeline stays corponym-driven and does not pick `#0` up (it has its own
separate bootstrap path, the `-e`/emergency console, documented alongside the verbs above).

---

## 2. `corponyms.moo` grammar

Plain text, one entry per line:

```
<name> <#objnum>
```

Example:

```
ambiguous_match #-2
anon #118
ansi_pc #100
room #3
string_utils #4
```

- **Source query**: `for pname in (properties(#0)) if (typeof(#0.(pname)) == OBJ) ...` — this is
  exactly what the retired `export_metadata.moo`'s `sysobj_props` already computes; the new
  exporter reuses the same query.
- **Sort order**: by `name`, ordinal case-insensitive comparison (matching MOO's own string
  comparison semantics, confirmed in `map.cc`). **This sort is imposed by the exporter.** The MOO
  server has no `objects()`-style call that returns this pre-sorted; if the exporter instead reads
  this off `#0` via a MOO **map** (map keys auto-sort as a red-black tree, confirmed in `map.cc`),
  the sort comes for free — but the spec's requirement is the sorted *output*, regardless of which
  path produces it.
- **Objnum is informational only** (I2) — never used as an identity key on re-import; recorded so
  a human reading the file can cross-reference a live instance.

---

## 3. `object.moo` grammar

```
@object <corponym-or-#objnum>
parents: <corponym-or-#objnum> <corponym-or-#objnum> ...
owner: #<objnum>
flags: <subset of "r w f a" in that order>
name: "<live .name value>"
aliases: "<alias1>" "<alias2>" ...
verbs: <verb-file-1.moo> <verb-file-2.moo> ...

@property "<name>" owner=#<objnum> perms=<subset of "r w c" in that order>
<toliteral()-rendered value>
.

@property "<name2>" owner=... perms=...
...
.
```

- **`parents:`** — a space-separated list, **in the exact order `parents(obj)` returns them, never
  re-sorted.** ToastStunt supports multiple inheritance (`parents()` returns a list; `parent()` is
  deprecated and lossy — confirmed in `objects.cc`). Parent order is the ancestor search order for
  verb/property resolution, so it is semantically load-bearing, not cosmetic — see §6 for why this
  overrides the "sort everything" instinct from invariant I4. Rendered in corponym form when the
  parent itself has one (parents are versioned classes by construction, per I2) — falls back to raw
  `#objnum` only for an uncorified parent.
- **`owner:`** (object-level) and every `owner=` on a `@property`/`@verb` line — **always raw
  `#objnum`, never corponym-resolved**, unlike `parents:`. Owners are players, not code objects;
  I2's corponym system has no opinion about them, and per §7's hazards, ownership is not translated
  across instances on import anyway.
- **`flags:`** — `is_player`/`programmer`/`wizard`/`r`/`w`/`f`/`a`, only the ones set, in a fixed
  literal order (pick one, e.g. the order listed above) so the line doesn't wobble between exports.
- **`verbs:`** — filenames under `verbs/`, listed **in the exact order `verbs(obj)` returns them,
  never re-sorted.** Each verb still gets its own file (for per-verb history/blame/`--follow`, main
  plan §5), so this line is purely a manifest: the on-disk *filenames* don't need to encode order
  (see §6), but the *importer* needs this order recorded somewhere to replay `add_verb` faithfully,
  since dispatch is first-match-wins across the declared list. `object.moo` is that "somewhere."
- **Property order**: sorted by name, ordinal case-insensitive. Unlike verbs, property lookup is
  purely by name — sibling order has no runtime effect — so this sort is safe and exists purely for
  stable, readable diffs.
- **`name:`/`aliases:`** — the object's live `.name` and `.aliases` values, for the tree
  view/inspector's benefit. **Deliberate exception to invariant I5** ("only defining-object values
  are recorded"): `.name`/`.aliases` are conventionally *declared* once on a root ancestor
  (`$root_class`/`#1`) and every object overrides them with its own value via plain assignment, so
  `properties(obj)` — normally how this format decides what to capture — essentially never lists
  them for any individual object even though each has a distinct value (confirmed against a real
  ToastCore export: only the object where they're actually declared captured them at all). Fetched
  directly (`{o}.name`/`{o}.aliases`), same as `parents()`/`verbs()`/flags already are, bypassing
  `properties()` entirely for just these two. Both lines are **optional** — a tree exported before
  this field existed simply lacks them (`Array.tryFind`, not `Array.find`, on read); no forced
  re-export, no parse failure. Both must render **before** `verbs:` (`headerLineCount`, where
  property-block parsing starts, is computed from `verbs:`'s own line position). `name:`'s value uses
  the same `\`/`"` quoting as `<name>` above; an empty `.name` renders as `name: ""` and loads as
  `None` (not `Some ""`), matching this field's pre-existing display contract. `aliases:` uses **one
  independently-escaped quoted token per alias** — deliberately *not* the same space-joined
  convention as §4's verb name-spec: a MOO alias is routinely a multi-word phrase (e.g. "brass
  lantern"; confirmed by reading ToastStunt's own `parse_cmd.cc`/`match.cc` — the command parser
  joins multiple typed words into the match target before comparing it against each alias), and
  joining/splitting aliases on space the way verb aliases do would silently corrupt any multi-word
  one. Fetched-but-not-diffed by `Sidecar/Importer.fs`'s promotion planner — same deliberate scope as
  the pre-existing `owner:`/`flags:` fields, neither of which has a corresponding promotion op either.
- **Quoting**: `<name>` is escaped for `\` and `"` (`\\` and `\"` respectively) before being written
  between the quotes, and unescaped the same way on read — needed because a real property name can
  itself contain a literal `"` (confirmed live: real ToastCore's `$help` object has a property
  literally named `"`, the help topic for quoting syntax). The same convention applies to §4's verb
  name-spec field, for the same reason.
- **Property values**: rendered with `toliteral()`, read back with `eval("return " + line + ";")`.
  This isn't a new idea — it's the exact round-trip already used by the current `$vcs` IDE property
  verbs (`Survive/VCS/12_ide_set_property.moo`), reused here rather than inventing a new value
  grammar. Wrap both the parse and the assignment in separate `try`/`except` (as the existing code
  does) so a bad literal and a bad assignment are distinguishable failures.
- **Only defining-object property values are recorded** (I5). A property inherited and merely
  present on a descendant is state, not schema, and is skipped — except entries on the small `#0`
  opt-out list (counters, timestamps, caches) that invariant I5 calls out even on the defining
  object.
- **Waif-valued properties**: refuse loudly (exporter error, not silent corruption) rather than
  attempt to serialize — waifs carry their own values and alias badly, and this format has no
  representation for them yet (per the main plan's hazards list).

---

## 4. Verb file grammar

```
@verb <corponym-or-#objnum>:"<full name-spec, all aliases>" <dobj> <prep> <iobj> <perms> <owner>
@program <corponym-or-#objnum>:<first-alias-with-* stripped>
<verb_code() output, one line per list element, verbatim>
.
```

Example (matches the main plan's own sketch):

```
@verb $room:"look_self" this none this rxd #2
@program $room:look_self
"Describe this room to the caller.";
player:tell(this:title());
.
```

- **Quoting**: `<full name-spec, all aliases>` follows §3's same `\`/`"` escaping convention (an
  alias could in principle contain a literal `"`, same reasoning as a property name).
- **`verb_code()` flags are pinned, not defaulted.** Call it as `verb_code(obj, idx, 0, 1)` — fully
  parenthesized `0` (off), indent `1` (on) — passed as **explicit literal arguments**. The current
  `$vcs` code passes only 2 of 4 args and silently rides ToastStunt's C-level defaults
  (`fully_paren=0, indent=1`, confirmed in `verbs.cc`); that's exactly the kind of implicit
  dependency invariant I4 exists to prevent, so the new exporter states the flags explicitly even
  though today's defaults happen to match.
- **Verb lookup is by 1-based numeric index into `verbs(obj)`, never by name string.** This
  sidesteps two real, already-encountered bugs: (1) the alias passed to a capture hook is often a
  bare single alias while `verbs(obj)` entries are full space-joined alias strings (required two
  separate bug-fix commits in the current `$vcs` code to get right); (2) `verbcasecmp` breaks on a
  verb's own literal `*`-containing name when that name is fed back into itself (documented,
  reproduced bug in `toaststunt-dev-environment-plan.md`'s open follow-ups). Numeric index avoids
  name-matching entirely for `verb_code()`/`verb_info()`/`verb_args()`.
- **`perms` string order is fixed by the server itself** (`r,w,x,d`, confirmed in `verbs.cc`) — only
  the letters actually set appear, always in that relative order. No exporter-side sort needed here.
- **Filename**: derived from the first alias (canonical order in the name-spec, not whichever alias
  triggered a particular edit), `*` stripped, sanitized; numeric suffix on collision. **The
  canonical full name-spec always lives in the `@verb` line, and reconciliation on import matches
  the header, never the filename.** This directly fixes a real, still-present bug in the current
  system: today's filename is derived from whichever alias happened to trigger that specific
  capture event, which can silently produce an orphaned duplicate file if the same verb is later
  re-programmed under a different one of its own aliases.
- **Verb order across a directory's `verbs/*.moo` files is not encoded by filename and does not need
  to be** — each verb is a separate file specifically so per-verb history/blame/`--follow` works
  (main plan §5). But see §6: the *declaration order* `verbs(obj)` returns is still semantically
  load-bearing for dispatch and must be preserved by the importer when it replays `add_verb` calls,
  even though the on-disk file layout doesn't need to encode it via naming.

---

## 5. Object enumeration

There is no `objects()` builtin in ToastStunt (confirmed — no such registration exists in the
source). Enumerate as:

```
for i in [#0..max_object()]
  if (valid(i))
    ...
  endif
endfor
```

This is exactly what the retired `export_metadata.moo` already does; the new exporter does the
same, then filters to objects with a `$0` corponym (I3) before deciding what gets a directory.

---

## 6. Sort orders, summarized

| Data | Sorted? | By what | Why |
|---|---|---|---|
| `corponyms.moo` entries | **Yes** — imposed | name, ordinal case-insensitive | Cosmetic only; corponym-to-object mapping has no server-side "order" to preserve. |
| Properties within `object.moo` | **Yes** — imposed | name, ordinal case-insensitive | Property lookup is by name; sibling order has zero runtime effect. Sort exists purely for stable diffs. |
| **Parents** in `object.moo` | **No — preserve exactly** | `parents(obj)`'s own return order | Ancestor search order for multiple-inheritance verb/property resolution. Re-sorting and replaying in sorted order would silently change resolution behavior versus the source DB. |
| **Verb declaration order** (as replayed by the importer's `add_verb` calls) | **No — preserve exactly** | `verbs(obj)`'s own return order, recorded in `object.moo`'s `verbs:` manifest line | Verb dispatch is first-match-wins across the object's ordered verb list (confirmed both in `toaststunt-dev-environment-plan.md`'s gotchas and the C source's linked-list walk). Sorting verbs alphabetically and replaying them in that order can change which verb wins an ambiguous/overlapping match. |
| Verb *files on disk* (`verbs/*.moo`) | N/A (one file per verb) | — | Filenames don't need to encode order at all; the `verbs:` manifest line in `object.moo` is the recorded order, not the filesystem. |

This table is a direct refinement of the main plan's invariant I4 ("sorted key order everywhere").
That phrasing holds for unordered-by-name data (properties, the corponym map) but does **not**
hold for parents or verb declaration order, both of which are semantically meaningful sequences in
ToastStunt, not arbitrary keys. Worth reflecting this distinction back into `moo-vcs-plan.md`
itself if/when that document is revised.

---

## 7. Known hazards (spec-level)

Carried forward from the main plan's §8, plus one addition found while grounding this spec:

- **Decompilation normalizes formatting** (main plan). Re-export and commit immediately after any
  hand-edit-then-import.
- **Import is not transactional** (main plan). The two-pass compile check stands in for a rollback.
- **Objnum drift is guaranteed** (main plan). I2 is the only protection; any objnum in an identity
  position is a latent bug.
- **Ownership on import** (main plan). Owner is recorded as objnum, informational only (§2/§3/§4) —
  it is not translated across instances, since players have no cross-instance identity in this
  design. Importing as a wizard can silently reassign ownership if not explicit; this format does
  not solve that, it only specifies the on-disk representation.
- **`$` resolution failures on import** (main plan). Fail loudly on a missing corponym; do not guess.
- **Float-literal decompilation uses locale-sensitive `%g` formatting** (`list.cc`, confirmed). Not
  a practical risk under a fixed server locale, but "byte-identical" (I7) is conditional on that,
  not an absolute guarantee independent of environment.
- **Object identity in the system being retired is `.name`, not a corponym** — confirms I2/I3 are a
  real, motivated fix (the current system versions *any* named object that ever had a verb
  programmed on it, corponym or not) rather than a speculative tightening.

---

## 8. Implementation notes (non-normative)

Pointers for whoever writes the Phase 1 exporter next — not part of the spec, just avoids
re-discovering what's already true of the codebase:

- `Metadata/Schema.fs` (`ObjectNode`, `VerbMeta`, `PropertyMeta`, `ObjectFlags`,
  `Graph.SystemObjectProperties`) already models a shape very close to what this format needs
  in-memory — worth reusing or lightly adapting rather than inventing new F# types.
- `generate_json()` (a ToastStunt builtin) is a ready-made way to get structured data back from an
  eval over the wizard connection without writing a MOO-literal parser in F# — the retired
  `export_metadata.moo` already used exactly this for `metadata.json`. `verb_code()`'s list-of-lines
  result and similar can ride the same mechanism.
- No eval/RPC request-response client exists yet in `Sidecar` (today it's a pure byte-pumping
  WebSocket↔TCP bridge, `BridgeHandler.fs`) and no git library dependency is present in any
  `.fsproj` — both are net-new Phase 1/4 work, not a gap in this spec.
