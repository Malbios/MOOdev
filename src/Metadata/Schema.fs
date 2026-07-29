/// In-memory shape of the metadata graph `Metadata.Loader.load` builds from
/// a `Survive`-shaped export tree per `C:\dev\moo-code\moo-dev\FORMAT.md`
/// (`corponyms.moo` + one `objects/<name>/object.moo` + one file per verb,
/// parsed via `Metadata.TreeFormat`) plus the captured verb source (parsed
/// through `Language.Parser`). Previously loaded from `lookups.toml` +
/// `metadata.json`, both written by the now-retired `$vcs`'s own MOOcode -
/// several fields below still carry comments describing that old source for
/// context on why they're shaped the way they are (e.g. `LiveName`, always
/// `None` now - see `Loader.fs`).
module Metadata.Schema

open Language.Ast
open Language.Lexer

/// A MOO object number (`#123`). Bare `int64`, matching `Ast.ObjLit` -
/// `#-1`/`#-2` etc. (`E_NONE`-adjacent sentinels like `$nothing`) are valid
/// values here, not just non-negative ids.
type ObjRef = int64

/// Dispatch-relevant facts about one verb, straight from `verb_info`/
/// `verb_args`/its position in `verbs(obj)` - everything
/// `db_find_callable_verb`/`verbcasecmp` (Phase 4.3c) need, independent of
/// whether the verb's source parsed.
type VerbMeta =
    { /// 1-based position in `verbs(obj)` at export time - also the file-name
      /// prefix `$vcs:capture_verb` uses, so it doubles as the join key to
      /// the verb's source file on disk.
      Index: int
      /// The raw space-separated name list (`"s ies es"`, `"look l*ook"`),
      /// in declared order - wildcard matching needs the ordered list, not
      /// just membership.
      Names: string list
      Owner: ObjRef
      Perms: string
      Dobj: string
      Prep: string
      Iobj: string }

type VerbNode =
    { Meta: VerbMeta
      /// The object this verb is defined ON (`verbs(obj)` returned it) -
      /// same as the owning `ObjectNode.Num`, kept alongside the verb so a
      /// flattened verb list (e.g. search results) doesn't need its parent
      /// looked back up.
      DefinedOn: ObjRef
      /// Absolute path to the captured `.moo` source, if `$vcs` has ever
      /// captured this verb. `None` for verbs metadata knows about (from
      /// `export_metadata`) that have no capture on disk yet - degrade
      /// gracefully rather than dropping the verb from the graph.
      SourcePath: string option
      /// `Some stmts` whenever a source file was found and lexed without a
      /// hard lex error, regardless of how many `ErrorStmt` nodes it
      /// contains - callers check `DiagnosticCount` for parse quality, not
      /// `Ast.IsSome`.
      Ast: Stmt list option
      /// Count of `ErrorStmt` nodes in `Ast` (0 if `Ast` is `None`, or if
      /// the parse was fully clean).
      DiagnosticCount: int
      /// The raw token stream (`Some` under the same condition as `Ast` -
      /// lexed without a hard error). Keywords (`if`/`for`/`return`/...)
      /// never become AST nodes (the parser fully consumes them into
      /// statement structure), so hovering one needs the token stream
      /// directly rather than anything `AstQuery` can find in the AST.
      Tokens: Token[] option }

/// One entry from `properties(obj)`/`property_info(obj, name)` - structural
/// only (name/owner/perm bits), not the property's current value. Mirrors
/// `VerbMeta` exactly (same `verbs()`/`verb_info()` -> `properties()`/
/// `property_info()` shape on the MOO side). Values are live, mutable game
/// state - fetched on demand (`$vcs:ide_get_properties`), never baked into
/// `metadata.json` the way this structural data is.
type PropertyMeta = { Name: string; Owner: ObjRef; Perms: string }

/// The object's built-in permission/type flags. These are NOT the
/// pseudo-properties one might guess (`.player`, `.fertile`, `.readable`,
/// `.writable` don't exist) - confirmed against `include/db.h`'s
/// `BUILTIN_PROPERTIES` macro and `db_properties.cc`: the real flags are
/// single-letter (`r`/`w`/`f`/`a`) plus `programmer`/`wizard`, and "is this
/// a player" is the separate `is_player(obj)` builtin, not a flag at all.
type ObjectFlags =
    { Player: bool
      Programmer: bool
      Wizard: bool
      Read: bool
      Write: bool
      Fertile: bool
      Anonymous: bool }

type ObjectNode =
    { Num: ObjRef
      /// From `lookups.toml` - a sanitized, filesystem-safe copy of the
      /// object's live `.name` (spaces/punctuation stripped for use as a git
      /// directory name - see `$vcs:sanitize_name`). `None` for objects
      /// `$vcs` has never named (no verb of theirs has been captured/edited
      /// yet). Not what a human wants to read in a picker - see `LiveName`.
      Name: string option
      /// The object's actual, unsanitized `.name` property value - from the
      /// export tree's `object.moo` `name:` header line (`Exporter.fs`
      /// fetches it directly, bypassing `properties(obj)` - see FORMAT.md
      /// §3's note on why), specifically for display purposes - e.g.
      /// "Generic Room" rather than `Name`'s "Generic_Room". `None` for a
      /// genuinely empty `.name`, or for a tree exported before this field
      /// existed (`TreeFormat.ParsedObject.Name`'s own backwards-compat
      /// tolerance).
      LiveName: string option
      Parents: ObjRef list
      Children: ObjRef list
      Verbs: VerbNode list
      /// `None`/`None`/`[]` only if `metadata.json` predates these fields
      /// (same graceful-degradation convention `LiveName` already uses) -
      /// in practice always present once `export_metadata()` is re-run.
      Owner: ObjRef option
      Flags: ObjectFlags option
      Properties: PropertyMeta list
      /// The object's live `.aliases` values, same direct-fetch reasoning as
      /// `LiveName`. `[]` for no aliases, or for a tree exported before this
      /// field existed.
      Aliases: string list }

/// One entry from `function_info()` (`functions.cc:463-510`) - name,
/// arity bounds, and per-argument type codes (`TYPE_INT`=0, `TYPE_OBJ`=1,
/// `TYPE_STR`=2, `TYPE_ERR`=3, `TYPE_LIST`=4, `TYPE_FLOAT`=9, `TYPE_MAP`=10,
/// `TYPE_ANON`=12, `TYPE_WAIF`=13, `TYPE_BOOL`=14, `TYPE_ANY`=-1,
/// `TYPE_NUMERIC`=-2 - `structures.h:99-122`, masked back to their
/// "user-visible" base values by `function_description`). Captured once via
/// `$vcs:export_builtins()` into `builtins.json` - builtins don't change
/// between server restarts, so this doesn't need the live-export-on-demand
/// treatment `metadata.json` gets.
type BuiltinFunc =
    { Name: string
      MinArgs: int
      MaxArgs: int
      ArgTypes: int list
      /// Real parameter names, when extracted from ToastStunt's own C
      /// source comments (~55 of 238 builtins - most single/simple-arg
      /// functions were never documented that way in the source, so this
      /// is `None` for the rest, not a bug). `None`, not an empty list, so
      /// callers can tell "not documented" apart from "documented as
      /// taking no listed params."
      ParamNames: string list option
      /// One-line "what does this do" hover text, hand-written (not from
      /// ToastStunt source comments - unlike `ParamNames`, there's no
      /// convention of doc comments above `bf_*` bodies describing
      /// behavior) against the C source and `docs/Features/new_builtins.md`
      /// for the fork-specific functions. `None` only if a real gap is
      /// later found, not expected in practice - all 238 are covered.
      Description: string option }

type Graph =
    { Objects: Map<ObjRef, ObjectNode>
      /// `#0`'s own object-typed properties (property name -> value), from
      /// `metadata.json`'s `sysobj_props` - the actual registry `$name`
      /// syntax reads at runtime (`$foo` === `#0.foo`). Deliberately NOT
      /// derived from `lookups.toml`: that file maps obj# -> a sanitized
      /// copy of `OBJ.name` (a display-name property, `$vcs`'s own capture
      /// bookkeeping), a completely different string from whatever property
      /// name(s) `#0` happens to register that object under - the two only
      /// coincide by ToastCore convention, not by any guarantee, so
      /// resolving `$name` receivers needs this real registry data instead.
      SystemObjectProperties: Map<string, ObjRef>
      /// From `builtins.json` (`$vcs:export_builtins()`), keyed by name.
      Builtins: Map<string, BuiltinFunc> }
