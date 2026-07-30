1. Verb outline / breadcrumb view

No symbol-list sidebar or in-verb outline exists anywhere in Client/App.fs (confirmed by search). For a longer verb, there's no way to jump straight to a for/if/try block or see the verb's structure at a glance — you scroll. Since the language server already builds a full AST per verb (Language/Ast.fs, LanguageServer/AstQuery.fs) for hover/go-to-def, an outline is mostly a rendering layer over data that already exists.

2. Global content search (live tree, not history)

The IDE has search-history, which is git log -S over commit history (find when a string was introduced/removed). There's no equivalent for "grep every verb currently in the tree for this string right now" — a different and arguably more common need (find every place a function/property is referenced today, not when it changed). This would be a straightforward addition next to the existing history-search panel, reusing the same tree the exporter already walks.

3. Snippet/scaffold insertion

No matches for "snippet" or "template" anywhere in the client. Boilerplate patterns that get retyped by hand every time include: a new verb's arg-spec skeleton, a try ... except id (ANY) ... endtry guard, and the doc-comment-as-leading-string convention the doc-comment card also depends on. A small snippet menu (or Monaco's built-in snippet completion provider, which is already available since Monaco is already integrated) would cut down repetitive typing.

4. Waif and anonymous-object visibility

No mentions anywhere in the client — confirmed via search. Waifs carry their own property values and serialize recursively (flagged explicitly as a hazard in toaststunt-dev-environment-plan.md, in the "Known hazards" section, in the context of the property exporter). But independent of the exporter question, there's currently no way to inspect a waif's own properties from the Inspector at all today, since a waif isn't a real object and doesn't get its own tree node. Right now if a property holds a waif, you likely just see an opaque value in the raw-expression input rather than something you can drill into.

5. Commit-message annotation at wip-squash time

Phase 4 of moo-vcs-plan.md describes auto-squashing the wip ref into main with a generated message ($room: look_self, describe; $player: +tell_lines), and explicitly says this should be "overridable when a change deserves a real message." Today that override path doesn't exist in the UI — the squash is purely mechanical, with no prompt or hook to substitute a real message before or during the squash.

6. Corponym rename/move tracking, surfaced distinctly

corponym-history already shows history of the $name -> #objnum map (per invariant I2 in moo-vcs-plan.md, and answers "Q5: why is $room pointing at #14?"). But the diff view doesn't call out that specific kind of change distinctly from an ordinary value edit — a corponym repoint is a structurally different, higher-stakes event (it can silently change what code every $foo reference in the whole codebase resolves to) and arguably deserves its own visual treatment (e.g., "was → #12, now → #14") rather than reading as a generic property diff.

7. Tick/seconds live-usage readout (soft overlap — flagging honestly)

The existing "live task queue inspector" card already covers "tick/seconds usage per task" as part of its scope, so this isn't a clean gap. The one piece not explicit there: a static hint at edit time (before you even run anything) flagging a loop with no suspend() call as a likely tick-limit risk — that's a lint-style check rather than a live runtime readout, so it's a smaller, complementary addition rather than a separate feature.