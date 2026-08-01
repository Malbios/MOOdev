# MOOcode Reference

A working reference for writing MOO code against ToastStunt. Written for agents and humans who
know other languages and need the parts where MOO differs.

## How to use this document

**This is a starting point, not an authority.** MOO documentation is sparse and much of what
exists describes LambdaMOO 1.8.1 rather than ToastStunt. Items marked **[verify]** are ones
where confidence is genuinely lower — check them against the running server or the C source
before depending on them.

The authoritative sources, in order:

1. The running server — `function_info()`, `verb_info()`, and direct experimentation via `;eval`
2. The C source in `toaststunt/src/`
3. `github.com/lisdude/toaststunt-documentation`
4. This document

When something here contradicts the server, the server is right and this document should be
corrected.

The two sections at the bottom — **Verified against the C source** and **Verified against the live
server** — carry `file:line` citations and outrank the body text above them. Treat anything there
as settled; treat the body as a readable summary of it.

---

## Mental model

MOO is not a language with a filesystem. **The database is the program.** Objects live in a
persistent graph held entirely in RAM, and code lives on those objects as verbs. There is no
source tree, no import statement, no module system, no build step. Editing a verb changes the
running system immediately.

Consequences that trip people up:

- The unit of code is a *verb on an object*, not a file or a class.
- There is no namespace. `$foo` is a property lookup on object `#0`, used by convention as a
  global registry.
- Objects have identity as numbers (`#1234`) that are allocated by the server and can be
  recycled and reused.
- Everything is persistent by default. A property you set stays set across restarts.
- Execution is cooperative and budgeted — tasks are killed if they run too long.

---

## Object model

Every object has:

- **A number** — `#1234`. Allocated by `create()`. Recycled numbers get reused.
- **A parent** (ToastStunt: possibly several — multiple inheritance). Inheritance is by
  delegation: children inherit verbs and property definitions.
- **A name and aliases** — used by the command parser for matching.
- **An owner** — an object, normally a player. Determines permissions.
- **Flags** — `player`, `programmer`, `wizard`, plus `r` (readable), `w` (writable),
  `f` (fertile, i.e. others may create children of it).
- **A location and contents** — the containment graph, separate from inheritance.

Well-known objects by convention:

| Object | Meaning |
|---|---|
| `#0` | The system object. Its properties form the `$name` global namespace. |
| `#1` | Root class — ancestor of everything. |
| `#2` | Conventionally the first wizard/archwizard. |
| `#-1` | `$nothing` — the standard "no object" value. |
| `#-2` | `$ambiguous_match` — parser result. |
| `#-3` | `$failed_match` — parser result. |

`$foo` is exactly `#0.foo`. Adding a property to `#0` is called "corifying."

---

## Types and literals

### Core types

| Type | Literal | Notes |
|---|---|---|
| INT | `42`, `-7` | 64-bit in ToastStunt (configurable at build) |
| FLOAT | `3.14`, `1.0e10` | IEEE double |
| STR | `"hello"` | Double quotes only |
| OBJ | `#123`, `$foo` | Object reference; may be invalid |
| ERR | `E_PROPNF` | Errors are first-class values |
| LIST | `{1, "a", #5}` | Heterogeneous, 1-indexed |

### ToastStunt additions

| Type | Literal | Notes |
|---|---|---|
| MAP | `["a" -> 1, "b" -> 2]` | Sorted-by-key associative array; `[]` is empty. `->` is a real token |
| BOOL | `true`, `false` | **Built-in variables, not keywords** — see below |
| WAIF | — | Lightweight object-like value, created from a class |
| ANON | — | Anonymous object, no object number |

`true` and `false` are *not* grammar-level literals. They are pre-bound entries in the local
variable table, exactly like `this`, `player`, and the type constants `INT`/`STR`/`LIST`. Three
consequences: variable names are matched case-insensitively, so `TRUE` and `True` are the same
binding; they can be shadowed or reassigned inside a verb (`true = 5;` compiles and does what it
says for the rest of that verb); and comparison against integers is special-cased, so `true == 1`
and `false == 0` are true while every other cross-type `==` is false.

### Error values

The set is **closed** — 19 literals, hardcoded in the keyword table. There is no open
`E_[A-Z]+` pattern; anything else is just an identifier.

`E_NONE`, `E_TYPE`, `E_DIV`, `E_PERM`, `E_PROPNF`, `E_VERBNF`, `E_VARNF`, `E_INVIND`,
`E_RECMOVE`, `E_MAXREC`, `E_RANGE`, `E_ARGS`, `E_NACC`, `E_INVARG`, `E_QUOTA`, `E_FLOAT`,
`E_FILE`, `E_EXEC`, `E_INTRPT`.

Errors are values you can store, compare, and return. Whether an error *raises* or is *returned*
depends on the verb's `d` flag — see Permissions below.

Errors are also **always false** in a boolean context, along with objects — see Operators.

---

## Syntax

### Comments

ToastStunt's lexer supports **C-style block comments only**:

```moo
/* This is a comment. */
x = 1;  /* Trailing comments work too. */
```

- There is **no `//` line-comment form.** `//` lexes as two division operators and will produce a
  syntax error, not a comment. This is the single easiest way to write MOO that looks fine and
  doesn't compile.
- Block comments **do not nest.** The first `*/` closes the comment; a second `*/` is a syntax
  error.
- An unterminated comment is a compile error: `End of program while in a comment`.
- A `/` not followed by `*` is just the division operator, so `a/b` is unaffected.
- **Quirk worth knowing:** the scanner consumes the character after any `*` without re-examining
  it. The practical effect is that a comment ending in two asterisks and a slash does *not*
  close — `/* note **/` fails with "End of program while in a comment", because the two asterisks
  are eaten as a pair and the `/` is left stranded. `/**/` closes fine, and so does `/* note ***/`.
  Avoid decorative asterisk runs at the end of a comment.

### Statements

```moo
if (condition)
  ...
elseif (other)
  ...
else
  ...
endif

for x in (some_list)
  ...
endfor

for i in [1..10]
  ...
endfor

while (condition)
  ...
endwhile

return value;
return;
```

Every statement ends with `;`. Block keywords do not.

**What `for ... in (...)` accepts.** LIST, MAP, **and STR** — iterating a string yields
one-character strings. Anything else raises `E_TYPE`.

**The two-variable form is `value` first, then `key`/`index`.**

```moo
for value, key in (some_map)    /* NOT key, value */
  ...
endfor

for element, i in (some_list)   /* i is the 1-based index */
  ...
endfor
```

This trips up everyone who arrives from Python or JavaScript, where the key comes first.

Map iteration order is **sorted by key**, not insertion order — a map is a balanced binary tree
keyed on the ordinary `compare()` used by `<`, so string keys sort case-insensitively.

**Range loops** (`for i in [a..b]`) accept `INT..INT` or `OBJ..OBJ` — the object form is legal and
occasionally handy: `for o in [#1..max_object()]`. Both bounds must be the same type. The range is
inclusive, and an empty range (`from > to`) simply runs zero times.

### Named loops

```moo
while chunk (condition)
  ...
  break chunk;
  continue chunk;
endwhile
```

`break` and `continue` work unlabelled too.

### Error handling

```moo
try
  x = obj.prop;
except e (E_PROPNF, E_PERM)
  ...
endtry

try
  ...
finally
  ...
endtry
```

**The caught value `e` is a 4-element list**, not a bare error code:

```moo
{code, message, value, call-stack}
```

- `[1]` — the error value itself (`E_PROPNF`, …). This is what you usually want.
- `[2]` — a human-readable message string.
- `[3]` — an arbitrary extra value attached to the raise (the third argument to `raise()`).
- `[4]` — the call stack at the point of the raise, in `callers()` format.

So `except e (ANY) ... return e[1];` is the idiom for "give me the error code."

**`except` and `finally` cannot be mixed in the same `try`.** They are two separate forms; a
`try` has either one-or-more `except` arms or exactly one `finally`, never both. Nest two `try`
blocks if you need both.

Other constraints on `except` arms: an `ANY` arm must be last (otherwise the compiler rejects it
with "Unreachable EXCEPT clause"), and there is a hard limit of 255 arms.

**The codes are an ordinary expression list**, evaluated at runtime — not a literal-only syntax.
All of these are legal:

```moo
except e (E_PROPNF, E_PERM)     /* the usual form           */
except e (my_error_codes)       /* a variable holding a list */
except e (@codes, E_RANGE)      /* splicing works too        */
```

At runtime the arm matches if the evaluated value is a list containing the raised code, or if it
is not a list at all (which is how `ANY` — compiled as the integer `0` — matches everything).

Inline form, useful and idiomatic:

```moo
x = `obj.prop ! E_PROPNF => 0';
x = `risky() ! ANY => "fallback"';
```

Note the asymmetric quoting: backtick to open, single quote to close. The `! codes` part is
mandatory; the `=> fallback` part is optional. **Omitting the fallback yields the error code**
(element 1 of the tuple), so `` `obj.prop ! ANY' `` evaluates to `E_PROPNF` rather than to a list
or to zero.

### Forking

```moo
fork (5)
  ...
endfork

task = fork tid (5)
  ...
endfork
```

Runs the block as a separate task after a delay in seconds. Delay `0` means "as soon as
possible," not "now."

### Operators

- Arithmetic: `+ - * / %`. Integer `/` truncates toward zero; division by zero is `E_DIV`.
- **Exponent is `^`, and it is right-associative** — `2^3^2` is `2^(3^2)` = 512. Integer base with
  a negative exponent gives `0` (except `1^-n` = 1, `(-1)^-n` = ±1, and `0^-n` = `E_DIV`).
- **`%` is floored, not truncated.** The result takes the sign of the *divisor*, Python-style:
  `-7 % 3` is `2`, not `-1`. This diverges from LambdaMOO and from C. `x % 0` is `E_DIV`.
- **There is no implicit numeric coercion anywhere.** Mixing INT and FLOAT raises `E_TYPE` in
  `+ - * / % ^` *and* in `< <= > >=`. `1 / 2.0` is an error, not `0.5`. Use `tofloat()`/`toint()`
  explicitly.
- Comparison: `== != < <= > >=`. **String comparison is case-insensitive** for all six — `"A" ==
  "a"` is true, and `<`/`>` compare case-insensitively too. Use `equal()` for a case-**sensitive**
  equality test, or `strcmp()` for a case-sensitive ordering. `<` `<=` `>` `>=` raise `E_TYPE` on
  lists, maps, or mismatched types.
- `==` across types is always false, with one exception: BOOL compares numerically against INT, so
  `true == 1` and `false == 0` hold. Note `1 == 1.0` is **false** — different types.
- Logical: `&& || !`, short-circuiting. **`&&` and `||` sit at the same precedence level and are
  left-associative** — `a || b && c` parses as `(a || b) && c`, *not* as C would parse it.
  Parenthesise mixed chains.
- Bitwise: `~` (complement, unary), and the **dotted two-character forms** `&.` (and), `|.` (or),
  `^.` (xor). The dots are deliberate — bare `&`/`|`/`^` are already taken by `&&`, the ternary's
  `|`, and the exponent operator. All operands must be INT or you get `E_TYPE`.
- Shifts: `<<` and `>>`. **`>>` is a *logical* shift** — it does not sign-extend, so `-1 >> 1` is a
  huge positive number, not `-1`. A shift count below `0` or above `64` raises `E_INVARG`.
- **Ternary is `? |`**, not `? :` — `cond ? a | b`.
- `in` — `x in {1,2,3}` returns the 1-based index, or `0` if absent. Not a boolean. Two extras
  worth knowing: `"lo" in "hello"` is a case-insensitive **substring search** returning `4`; and
  `x in some_map` searches the map's **values**, returning an ordinal position (not a key).
- `+` concatenates strings and lists. Note the asymmetry: `list + list` concatenates, but
  `list + anything-else` **appends** — `{1, 2} + 3` is `{1, 2, 3}`, not an error. String
  concatenation is capped by `$server_options.max_concat_string` and raises `E_QUOTA` past it.
- `@` splices a list, both in list literals and argument passing: `{@a, @b}`, `f(@args)`.
- `->` is a token in its own right, used as the map-literal separator: `["a" -> 1]`.

**Truthiness.** Only INT, FLOAT, STR, LIST, MAP and BOOL have truth values (non-zero, non-empty,
`true`). **OBJ and ERR are always false**, including `#1` and valid objects — `if (some_object)`
never fires. Use `valid(x)` or `x != $nothing`. WAIFs and anonymous objects are false too.

#### Precedence, lowest to highest

| Level | Operators | Associativity |
|---|---|---|
| 1 | `=` (assignment) | right |
| 2 | `? \|` (ternary) | non-assoc |
| 3 | `\|\|` `&&` | left — **same level** |
| 4 | `==` `!=` `<` `<=` `>` `>=` `in` | left |
| 5 | `\|.` `&.` `^.` | left |
| 6 | `<<` `>>` | left |
| 7 | `+` `-` | left |
| 8 | `*` `/` `%` | left |
| 9 | `^` (exponent) | **right** |
| 10 | `!` `~` unary `-` | — |
| 11 | `.` `:` `[` `$` | non-assoc |

Note that unary operators bind **tighter** than `^`, so `-x^2` is `(-x)^2`, not `-(x^2)`.

### Indexing

```moo
list[1]           /* first element — 1-INDEXED       */
list[$]           /* last element                    */
list[^]           /* first element                   */
list[2..4]        /* sublist                         */
list[2..$]        /* to the end                      */
list[^..2]        /* from the start                  */
str[3]            /* single-character string         */
map["key"]        /* map lookup                      */
list[1] = "x";    /* indexed assignment works        */
```

**`$` and `^` are a matched pair, and both are parser-context-sensitive.** They are legal *only*
directly inside index or range brackets; anywhere else the compiler rejects them with "Illegal
context for `$'/`^' expression." They are not general-purpose tokens, and `$` here is unrelated to
`$foo` object references.

What they actually evaluate to depends on the type being indexed:

| | STR / LIST | MAP |
|---|---|---|
| `^` | `1`, or **`0` if empty** | the first key |
| `$` | the length | the last key |

Two consequences worth internalising: `^` is *not* a constant `1` — on an empty list it is `0`, so
`{}[^]` raises `E_RANGE` rather than silently misbehaving. And for maps, `map[^]` and `map[$]`
return the first and last **keys**, which makes `map[map[^]]` the first value.

### Scattering assignment

A list can be destructured in one statement. This is idiomatic in ToastStunt cores and easy to
miss because it looks like a list literal on the left of `=`:

```moo
{a, b} = {1, 2};                    /* a = 1, b = 2                     */
{who, ?how = "quietly"} = args;     /* optional target with a default   */
{first, @rest} = args;              /* @ collects the remainder         */
{a, ?b, @rest} = args;              /* they combine                     */
```

- `?name` marks an optional target; `?name = expr` supplies a default when the list is too short.
- `@name` collects everything left over into a list. At most one `@` target is allowed.
- Targets must be plain variable names — you cannot scatter into `obj.prop` or `list[i]`.
- The right-hand side must be a LIST or you get `E_TYPE`; too few or too many elements to satisfy
  the required targets raises `E_ARGS`.
- Maximum 255 targets.

This is the usual way to unpack `args` at the top of a verb.

### Property and verb access

```moo
obj.prop            /* property                                          */
obj.(expr)          /* computed property name                            */
obj.:prop           /* waif property — sugar for obj.(":prop")           */
obj:verb(a, b)      /* verb call                                         */
obj:(expr)(a, b)    /* computed verb name — invisible to static analysis */
pass(@args)         /* call the parent's version of this verb            */
```

`obj.:name` is real syntax, not a typo: waif properties are defined on the class object with a
leading `:` in their name, and `.:` is the shorthand for reaching them.

`pass()` is an ordinary builtin function, not a keyword.

---

## Verbs

### Structure

A verb has no signature line. The code *is* the body. Arguments arrive in `args`.

```moo
/* on $hatch, verb "cycle" */
if (length(args) < 1)
  return E_ARGS;
endif
who = args[1];
this.is_open = !this.is_open;
return this.is_open;
```

### Built-in variables

Available in every verb without declaration:

| Variable | Meaning |
|---|---|
| `this` | The object the verb was found on |
| `caller` | The object whose verb called this one |
| `player` | The player who initiated the task |
| `verb` | The name the verb was invoked as (string) |
| `args` | List of arguments |
| `argstr` | Raw argument string (command invocation) |
| `dobj`, `iobj` | Direct/indirect object, resolved by the parser |
| `dobjstr`, `iobjstr` | Raw strings the parser matched from |
| `prepstr` | The preposition used |

Reading an undefined variable raises `E_VARNF`. There is no declaration syntax; assignment
creates.

### Verb names are patterns

A single verb can have several names, and names can contain wildcards:

```
l*ook get take put
```

`l*ook` matches `l`, `lo`, `loo`, `look`. `*` alone matches anything.

**Dispatch scans the object's verb list in order and takes the first match.** Verb *order is
semantics*. Adding a verb named `get` before an existing `get take` changes behaviour.

Inheritance walks the ancestor chain depth-first, left-to-right through the parents list, with no
C3-style linearization — see "Multiple-inheritance verb dispatch order" in the verified section
at the bottom for the exact traversal.

#### The exact matching algorithm

Anything reimplementing dispatch (an LSP, a linter, a command router) needs this precisely rather
than by feel. The whole name string is scanned name-by-name, left to right; the first name that
matches wins:

1. Split the verb's name string on spaces. Each space-delimited token is one pattern.
2. Compare the candidate word against the pattern character by character, **case-insensitively**.
   Case folding is ASCII-only via a fixed 256-entry table — bytes ≥ 128 are compared unchanged, so
   non-ASCII characters are effectively case-sensitive.
3. A `*` in the pattern sets a flag and is skipped. If the `*` is at the end of the pattern (or
   immediately before a space) the flag is **end**; otherwise it is **inner**.
4. Matching stops at the first character mismatch, or when either side is exhausted.
5. The pattern matches if:
   - the candidate is exhausted **and** (a `*` was seen anywhere, or the pattern is also
     exhausted) — this is what makes `l*ook` accept `l`, `lo`, `loo`, `look`; or
   - the candidate is *not* exhausted **and** the flag is **end** — this is what makes `get*` and
     bare `*` accept arbitrary trailing text.
6. Otherwise, skip to the next space-delimited name and retry from step 2.

An empty candidate string never matches a pattern that doesn't begin with `*`.

### Argument specification

Verbs have a `dobj prep iobj` spec used by the command parser — **not** a parameter list:

```
this none this
any any any
none none none
```

- `this` — must be the object the verb is on
- `any` — anything
- `none` — nothing in that slot

Prepositions come from a fixed server list of 15 entries, in this order (the index is stored raw
in the DB, so the order never changes and entries are never removed): `with/using`, `at/to`,
`in front of`, `in/inside/into`, `on top of/on/onto/upon`, `out of/from inside/from`, `over`,
`through`, `under/underneath/beneath`, `behind`, `beside`, `for/about`, `is`, `as`, `off/off of`.

### Verb permissions

Permission string, some subset of `rwxd`:

| Bit | Meaning |
|---|---|
| `r` | Readable — non-owners can see the code |
| `w` | Writable — non-owners can change the code |
| `x` | **Executable — callable from other code** |
| `d` | Debug — errors raise instead of returning as values |

**The `x` bit is the single most common early mistake.** A verb without `x` can be invoked as a
command but not via `obj:verb()`. It fails with `E_VERBNF`, which reads like the verb doesn't
exist.

The `d` bit changes control flow: with it set, `obj.missing_prop` raises and unwinds; without it,
the expression evaluates to `E_PROPNF` and execution continues. Know which mode you're in.

**Defaults.** The C server has none — `add_verb()` requires an explicit permission string. What
people think of as "the default" comes from the core's `@verb` command, which picks:

- `"rxd"`, with `x` force-added if you removed it, when the argspec is exactly `this none this`
- `"rd"` — **no `x` bit** — for every other argspec

That second rule is the mechanical cause of the `x`-bit mistake above: a verb made with
`@verb obj:foo any any any` is deliberately created command-callable-only. Programmers can change
their own default via `player:prog_option("verb_perms")`.

Verbs run with the **owner's** permissions, not the caller's. `set_task_perms()` and
`caller_perms()` manage privilege deliberately.

---

## Properties

### Definition versus inheritance

A property is **defined** on one object via `add_property()`. Every descendant **inherits** it.

Inherited properties start **clear**: reading one reads through to the nearest non-clear
ancestor. Writing a value makes it concrete on that object, breaking the link.

```moo
add_property($room, "description", "A room.", {$room.owner, "rc"});
is_clear_property(#500, "description")  /* => 1, reads through to $room */
#500.description = "A cold room.";      /* now concrete on #500        */
clear_property(#500, "description");    /* back to inheriting          */
```

**This matters for any tooling that reads properties.** Reading `obj.prop` on a clear property
silently returns the inherited value. Code that reads and writes back will freeze inherited
values into concrete copies across every object it touches, silently severing inheritance.
Always check `is_clear_property()` first.

### Property permissions

| Bit | Meaning |
|---|---|
| `r` | Readable by non-owners |
| `w` | Writable by non-owners |
| `c` | Change ownership — descendants' copies are owned by the descendant's owner |

Built-in properties (`name`, `owner`, `location`, `contents`, `parent`, the flags) are not in the
property list and cannot be deleted.

---

## Tasks, ticks, and suspension

Every task has a **tick budget** and a **seconds budget**. Exceeding either kills the task with a
traceback. Budgets differ for foreground tasks (started by player input or the server, never yet
suspended) and background tasks (forked, or anything that has suspended):

| Limit | Foreground | Background |
|---|---|---|
| Ticks | 60,000 | 30,000 |
| Seconds | 5 | 3 |

Plus a maximum verb-call stack depth of **50** and a lag-report threshold of **5.0 seconds**.

Those are the compiled-in fallbacks. Each is looked up at task start as
`$server_options.<name>` — `fg_ticks`, `bg_ticks`, `fg_seconds`, `bg_seconds`,
`max_stack_depth` — and only falls back to the constant above when the property is absent. So
read `$server_options` to learn the *effective* values on a given server, but the numbers above
are what you get on a database that doesn't override them.

```moo
for o in (objects)
  ...
  if (ticks_left() < 5000 || seconds_left() < 2)
    suspend(0);
  endif
endfor
```

`suspend(0)` yields to the scheduler and resets the budget. **Any loop over an unbounded
collection needs this**, or it will die partway on a large database.

Suspension makes a task asynchronous. After `suspend()`, the world may have changed — objects
recycled, properties altered. Re-validate assumptions across a suspend.

Relevant builtins: `task_id()`, `suspend()`, `resume()`, `queued_tasks()`, `kill_task()`,
`callers()`, `ticks_left()`, `seconds_left()`.

ToastStunt adds threaded builtins that implicitly suspend, so some operations that look
synchronous are not.

---

## Command parsing

When a player types a line, the server:

1. Splits it into verb, direct object string, preposition, indirect object string.
2. Matches object strings against the player, the player's contents, and the location's contents.
3. Searches for a matching verb on: the player, the room, the direct object, the indirect object.
4. Invokes the first match whose argument spec fits.

Unmatched strings resolve to `$failed_match` (`#-3`); ambiguous ones to `$ambiguous_match`
(`#-2`). Always check both.

`#0:do_command` is called first and can intercept everything, which is where custom parsing
belongs.

---

## Server-called verbs

The server calls these on `#0` when it wants the database to handle something. This is the
extension mechanism for anything the C server needs to hand off.

| Verb | When |
|---|---|
| `do_login_command` | Input from an unauthenticated connection |
| `do_command` | Every command, before normal parsing |
| `do_blank_command` | A blank line from a connected player |
| `do_out_of_band_command` | Lines matching the out-of-band prefix |
| `user_connected` / `user_created` / `user_reconnected` | Session lifecycle |
| `user_disconnected` / `user_client_disconnected` | Session end |
| `server_started` | Startup |
| `checkpoint_started` / `checkpoint_finished` | Database dump |
| `handle_uncaught_error` | Unhandled error in a task |
| `handle_task_timeout` | Task exceeded its budget |
| `handle_lagging_task` | ToastStunt — task exceeded the lag threshold |
| `handle_signal` | ToastStunt — process signal received; return true to suppress |
| `handle_verb_programmed` | **This fork only** — see below |

Objects also receive `:recycle()` before destruction, and `:initialize()` on creation. Note that
`:initialize()` is called by the **C server** from `create()`, not merely by core convention —
it receives the init-args list if one was passed to `create()`.

**`handle_verb_programmed` is a local patch in this project's tree, not upstream ToastStunt.** It
is an uncommitted modification to `execute.cc`, `execute.h`, `tasks.cc` and `verbs.cc` in
`toaststunt/`, so it will not exist on a stock ToastStunt build and will vanish if that tree is
ever reset. It fires `#0:handle_verb_programmed(object, verb-names, programmer)` after any
successful `set_verb_code()` or `.program`, dispatched *after* the triggering task finishes rather
than on its stack. Anything relying on it must degrade gracefully when the verb is never called.

Adding a new hook of this kind is a small, idiomatic C patch — the existing handlers are the
template, and the one above is this project's worked example.

---

## Builtin functions

Illustrative, not exhaustive. **`function_info()` on the running server is authoritative** and
should be used to generate any real list.

**Objects** — `create`, `recycle`, `valid`, `parent`, `children`, `chparent`, `max_object`,
`players`, `is_player`, `set_player_flag`, `move`, `renumber`, `object_bytes`

**Properties** — `properties`, `property_info`, `set_property_info`, `add_property`,
`delete_property`, `is_clear_property`, `clear_property`

**Verbs** — `verbs`, `verb_info`, `set_verb_info`, `verb_args`, `set_verb_args`, `add_verb`,
`delete_verb`, `verb_code`, `set_verb_code`, `disassemble`

**Values** — `typeof`, `tostr`, `toliteral`, `toint`, `toobj`, `tofloat`, `equal`, `value_bytes`

**Lists** — `length`, `is_member`, `listinsert`, `listappend`, `listdelete`, `listset`, `setadd`,
`setremove`

**Strings** — `strsub`, `index`, `rindex`, `strcmp`, `match`, `rmatch`, `substitute`, `crypt`,
`string_hash`, `encode_binary`, `decode_binary`

**Numbers and time** — `abs`, `min`, `max`, `random`, `sqrt`, `floatstr`, `time`, `ctime`

**Tasks** — `task_id`, `suspend`, `resume`, `queued_tasks`, `kill_task`, `callers`, `task_stack`,
`ticks_left`, `seconds_left`, `set_task_perms`, `caller_perms`

**Server** — `server_version`, `server_log`, `dump_database`, `shutdown`, `memory_usage`,
`function_info`, `load_server_options`

**Connections** — `notify`, `read`, `force_input`, `boot_player`, `connected_players`,
`connected_seconds`, `idle_seconds`, `connection_name`, `set_connection_option`,
`open_network_connection`, `listen`, `unlisten`, `listeners`

### ToastStunt additions

Names below are from the feature list and should be confirmed via `function_info()` **[verify]**.

- **FileIO** — `file_open`, `file_close`, `file_readline`, `file_write`, and related. Confined to
  a subdirectory of the server root; limit configurable via `$server_options.file_io_max_files`.
- **`exec()`** — run an external program. Binary must live in the server's `executables/`
  directory. Wizard-only. Implicitly suspends.
- **SQLite** — `sqlite_open`, `sqlite_query`, and related.
- **JSON** — `parse_json`, `generate_json`.
- **PCRE** — regex matching beyond MOO's native `match()`.
- **Crypto** — `argon2`, `argon2_verify`, `salt`.
- **Threading** — background execution for expensive builtins; these implicitly suspend.
- **WAIFs** — `new_waif`, `waif_stats`, indexing hooks `:_index` / `:_set_index`.
- **Network** — TLS, IPv6, curl-backed outbound requests, HAProxy source-IP rewriting.

---

## Common mistakes

Ordered by how often they bite.

1. **0-indexing.** `list[0]` is `E_RANGE`. Lists start at 1.
2. **Missing `x` bit.** Verb is unreachable from code, fails as `E_VERBNF`. The core's `@verb`
   gives `rd` (no `x`) to anything that isn't `this none this`, so this is the default outcome,
   not an accident.
3. **Writing `//` comments.** MOO has only `/* ... */`. `//` is two division operators and will
   not compile.
4. **Passing a string to `set_verb_code()`.** It takes a list of strings, one per line.
   `verb_code()` returns the same.
5. **Ignoring the return of `set_verb_code()`.** It returns a list of compile errors and leaves
   the verb unchanged on failure. Empty return means success.
6. **Unbounded loops without `suspend()`.** Works on a small test DB, dies on the real one.
7. **Reading properties without `is_clear_property()`.** Silently converts inherited values to
   concrete copies.
8. **Assuming `?:` for ternary.** MOO uses `? |`.
9. **Testing an object for truth.** `if (obj)` is **always false** — objects and errors have no
   truth value. Use `valid(obj)`.
10. **Assuming `&&` binds tighter than `||`.** They share one precedence level and associate left,
    so `a || b && c` is `(a || b) && c`. Parenthesise.
11. **Treating `in` as boolean.** It returns an index; `0` is falsy but any position is truthy,
    which is usually what you want but not always what you mean.
12. **Expecting numeric coercion, or C's `%`.** Mixing INT and FLOAT is `E_TYPE` in every
    arithmetic and relational operator, including `+` and `<`. And MOO's modulo is floored, so
    `-7 % 3` is `2`.
13. **Forgetting `$failed_match` / `$ambiguous_match`.** Parser results need checking before use.
14. **Assuming state survives `suspend()`.** It's a yield point; the world moves.
15. **Assuming `d` flag semantics.** Whether an error raises or returns changes control flow
    entirely.
16. **String escapes.** There are none. A backslash makes the next character literal and is
    discarded, so `"\n"` is the one-character string `n`, not a newline. See the verified section
    for how to actually get a newline.
17. **`tostr()` on a list or map.** You get the literal text `"{list}"` or `"[map]"`, not the
    contents. Use `toliteral()`.
18. **`for key, value in (map)`.** Backwards — MOO binds the **value** first.

---

## Things this document is least sure about

The five long-standing uncertainties in this section — map iteration order, the `except e` value
shape, tick/seconds/permission defaults, `==` case sensitivity, and string escapes — have all been
resolved from the C source and are documented inline above, with citations in the source-verified
section below.

What remains genuinely open:

- **The ToastStunt builtin list** (the "ToastStunt additions" section) is still from the feature
  list rather than enumerated from the source. This is a deliberate deferral, not an oversight:
  `function_info()` on the running server is both easier and more authoritative than reading
  several hundred `register_function()` calls, and it stays correct as the fork changes. The
  `[verify]` tag there stands.
- **The on-disk checkpoint format** for maps and waifs — see the note at the very bottom.

Correct this file as those get answered. A reference with known-good and known-shaky sections
clearly separated is more useful than one that sounds uniformly confident.

---

## Verified against the C source (`toaststunt/src`, M4 audit)

Read directly out of the server this project builds and runs — `build/moo` is newer than every
file cited here, so these describe the running binary. Same standing as the live-server section
below: these supersede any `[verify]` tag above on the same topic.

Where "the language version" matters: `parse_list_as_program()` always compiles at
`current_db_version` (`parser.y:1336`), which is the newest version the build knows
(`DBV_Bool`, `include/version.h`). Every keyword in the table — including `E_FILE`, `E_EXEC`,
`E_INTRPT` and the `true`/`false` bindings — is therefore unconditionally available to anything
compiled via `set_verb_code()`, `eval()` or `.program`, regardless of what version the database
file on disk claims. Version gating only affects loading an old DB.

### Comments — `/* */` only, and `**/` does not close one

`parser.y:889-908`. On seeing `/` the lexer peeks: if the next character isn't `*`, it ungets and
returns the division token, so there is no `//` form. Inside a comment it loops looking for `*`
followed by `/`; `EOF` first produces `yyerror("End of program while in a comment")`.

The loop consumes the character after each `*` **without re-testing it**, which is why `**/`
fails: the two asterisks are eaten as a pair, then the lone `/` isn't a terminator. `/**/` and
`/* x ***/` both close correctly.

### `true` / `false` are built-in variables, not keywords

`sym_table.cc:118-122` adds `SLOT_BOOL`, `SLOT_TRUE`, `SLOT_FALSE` to the pre-populated name
table when `version >= DBV_Bool`, in exactly the same way as `this`, `player`, `caller` and the
type constants. They are absent from `keywords.gperf`, so the lexer never emits a token for them —
they resolve through `find_id()` like any other identifier.

`eval_env.cc:110-119` fills those slots with real `TYPE_BOOL` values at frame setup. Practical
consequences, all confirmed in source rather than assumed:

- Name lookup is `strcasecmp` (`sym_table.cc:133, 144`), so `TRUE`, `True` and `true` are one
  binding.
- They are ordinary local slots, so `true = 5;` compiles and rebinds for the rest of that verb.
  A real keyword could not be assigned to.
- `equality()` special-cases BOOL against INT (`utils.cc:484-491`), so `true == 1` is true. Every
  other cross-type `==` is false.

### The error-literal set is closed

`keywords.gperf:48-66` — 19 entries, each mapping a fixed name to `tERROR`. There is no pattern
rule, so `E_WHATEVER` is simply an identifier and will raise `E_VARNF` at runtime. `E_FLOAT`,
`E_FILE`, `E_EXEC` and `E_INTRPT` carry version gates (`DBV_Float`, `DBV_FileIO`, `DBV_Exec`,
`DBV_Interrupt`), all of which are below the current version, so all 19 are live here.

### The operator set

Lexer at `parser.y:1049-1067`, precedence at `parser.y:103-113`, evaluation in `execute.cc`.

- `^` is both exponent (`EXPR_EXP`, `parser.y:537-540`) and, bare inside index brackets, "first".
  The lexer disambiguates `^..` from `^.` via `check_two_dots()` so that `list[^..2]` lexes
  correctly rather than as a bitwise-xor token.
- `&.`, `|.`, `^.` are the bitwise and/or/xor tokens — spelled with a dot precisely because bare
  `&`, `|` and `^` are taken by `&&`, the ternary's `|`, and exponent.
- `<<` / `>>` are `tBITSHL` / `tBITSHR`. `execute.cc:2827-2872`: both operands must be INT
  (`E_TYPE` otherwise); a count `< 0` or `> 64` is `E_INVARG`; a count of exactly 64 yields 0; and
  `>>` casts to unsigned before shifting, so it is a **logical** shift with no sign extension.
- `~` is unary complement (`EXPR_COMPLEMENT`, `execute.cc:2874-2888`), INT only.
- `->` is `tMAP`, produced by `-` followed by `>` (`parser.y:1062`), and is only meaningful inside
  a map literal. `=>` is a separate token (`tARROW`, `parser.y:1056-1057`) used solely for the
  inline-catch fallback.
- `&&` and `||` share precedence level (`parser.y:105`, corroborated by the unparser's table at
  `unparse.cc:232-233` where both are level 3). Unary `!`, `~` and negation sit *above* `^`
  (`parser.y:111-112`), so `-x^2` is `(-x)^2`.

`%` is floored: `numbers.cc:294-309` computes `(n % d + d) % d` for INT and the `fmod` equivalent
for FLOAT, giving the divisor's sign.

The no-coercion rule is uniform and comes from a single shape repeated throughout `numbers.cc`:
`do_divide`, `do_modulus`, `do_power` and the `SIMPLE_BINARY` macro that generates `do_add`,
`do_subtract` and `do_multiply` (`numbers.cc:251-355`) all open with
`if (a.type != b.type) return E_TYPE`. `compare_numbers()` (`numbers.cc:222-248`) does the same,
so `1 < 1.0` is an error too. The opcode handlers only check that both operands are *some* numeric
type before delegating, so the actual type equality check is always the one in `numbers.cc`.

`OP_ADD` (`execute.cc:1488-1523`) additionally handles STR (concatenation, capped by
`SVO_MAX_STRING_CONCAT` with `E_QUOTA` on overflow) and LIST — where `list + list` calls
`listconcat` but `list + non-list` calls `listappend`, so the operator quietly changes meaning
based on the right operand's type.

### `^` and `$` in index brackets

`parser.y:441-465`. The grammar rule `expr '[' dollars_up expr ']'` increments a `dollars_ok`
counter before parsing the subscript; the bare `'^'` and `'$'` productions check that counter and
call `yyerror("Illegal context for ...")` if it is zero. That is the whole of the
context-sensitivity — they are not tokens that mean anything outside brackets.

`execute.cc:2414-2460` gives the values. For STR and LIST, `EOP_FIRST` pushes `1` **if the
collection is non-empty and `0` if it is empty**, while `EOP_LAST` pushes the length. For MAP they
push the first and last **key** respectively. Any other type is a type mismatch.

Both read the collection off a compile-time-recorded stack slot (`code_gen.cc:779-800`), so they
always refer to the value being indexed by the innermost enclosing bracket.

### Map / list iteration binds value first

`parser.y:160-174` — the rule `tFOR tID ',' tID tIN '(' expr ')'` stores the **first** identifier
as `s.list.id` and the **second** as `s.list.index`.

`execute.cc:2725-2780` (`EOP_FOR_LIST_2`) then assigns `rt_env[id] = pair.b` and
`rt_env[index] = pair.a` for maps — `.b` is the value, `.a` is the key. For STR and LIST it
assigns the element to `id` and the 1-based counter to `index`. So the form is unambiguously
`for value, key in (map)` / `for element, index in (list)`.

`EOP_FOR_LIST_1` and `_2` both accept STR, LIST and MAP and raise `E_TYPE` on anything else.
`OP_FOR_RANGE` (`execute.cc:1040-1053`) accepts `INT..INT` or `OBJ..OBJ`, requiring both bounds to
share a type.

Map ordering is not insertion order. `map.cc:65-90` implements a red-black tree whose
`node_compare()` is the same `compare()` that backs `<` — so keys are kept sorted, and string keys
sort case-insensitively (`case_matters` is 0 on the insert/erase paths, `map.cc:344-408`).
Iteration is an in-order traversal, which is why `map[^]` and `map[$]` are the smallest and
largest keys rather than "first inserted" and "last inserted".

### `except e` binds a 4-element list

`execute.cc:565-585` builds the raise value as `new_list(4)`:
`{code, message, value, call-stack}`, where `call-stack` comes from `make_stack_list()` in
`callers()` format. An *uncaught* error gets a fifth element (the backtrace) and is routed to
`handle_uncaught_error` instead, so you never see a 5-element list from an `except` arm.

`unwind_stack()` (`execute.cc:299-321`) pushes that entire list onto the stack when a handler
matches, and `code_gen.cc:1112-1114` emits `OP_PUT <id>` to store it into the `except` variable.
Hence `e[1]` for the code.

The codes themselves compile as an ordinary argument list (`code_gen.cc:598-606`), so any
expression — a variable, a splice — is legal; `ANY` compiles to the integer `0` instead. The
match test at `execute.cc:304-306` is `vv->type != TYPE_LIST || ismember(code, *vv, 0)`, i.e.
"not a list" means "match anything", which is how `ANY` works and why a non-list expression in the
codes position silently catches everything.

`try/except` and `try/finally` are disjoint grammar productions (`parser.y:277-288`) — they cannot
be combined. `parser.y:329-332` rejects an arm following an `ANY` arm and caps arms at 255.

Inline catch without `=> fallback` compiles to "index 1 of the tuple" (`code_gen.cc:945-949`), so
`` `expr ! ANY' `` evaluates to the error code.

### Unknown builtin functions are a **compile error**, not a warning

This one is easy to get backwards from a quick read of the grammar. `parser.y:496-516` does
rewrite an unrecognised `name(args)` into `call_function("name", @args)` and calls `warning()`
rather than `error()`. But `warning()` at `parser.y:824-831` is:

```c
static void warning(const char *s, const char *t) {
    if (client.warning) (*(client.warning))(client_data, fmt_error(s, t));
    else error(s, t);          /* nerrors++ — the compile fails */
}
```

Only one of the three parser clients in the tree supplies a warning handler:

| Client | Warning handler | Used by |
|---|---|---|
| `db_io.cc:271` | yes — logs and continues | loading the database from disk |
| `parser.y:1324` | **none** | `set_verb_code()`, `eval()`, `program.cc`, `server.cc` |
| `tasks.cc:693` | **none** | the `.program` command |

So every programmer-facing compile path turns it into a hard error reading
`Line N:  Unknown built-in function: foo`, and the verb is left unchanged. The `call_function`
fallback only ever takes effect while loading an old database that referenced a builtin this
build no longer has.

Practical upshot for tooling: unknown-function detection is available *at compile time* via
`set_verb_code()`'s error list. Arity and argument-type checking remain runtime-only.

### String literals have no escape sequences

`parser.y:1032-1047`. The scanner reads until an unescaped `"`. A backslash causes the next
character to be read and **added raw** — there is no translation table, so `"\n"` is the
one-character string `n` and `"\t"` is `t`. The only characters worth escaping are `"` and `\`
themselves. A newline or EOF encountered after the backslash produces `Missing quote`, so a string
literal cannot span source lines.

Output is symmetric: `unparse_value()` (`list.cc:468-484`) escapes only `"` and `\` when producing
a literal, so `toliteral()` round-trips.

To get a real newline into a string, build it — `chr(10)`, or whichever core utility your database
provides — rather than reaching for `"\n"`.

### String comparison is case-insensitive

`execute.cc:1317-1331` — `==` and `!=` call `equality(rhs, lhs, 0)`, and the `0` is
`case_matters`. `utils.cc:458-468` then uses `strcasecmp` for STR. The relational operators do the
same (`execute.cc:1370`), and they raise `E_TYPE` for LIST, MAP, or mismatched types
(`execute.cc:1355-1358`).

`equal()` is the case-**sensitive** counterpart — `list.cc:792` passes `case_matters = 1`.

Folding uses a fixed 256-byte table (`utils.cc:58-74`) that only maps ASCII `A-Z`; bytes ≥ 128 are
left alone, so non-ASCII text compares case-sensitively. The same table backs `verbcasecmp` and
`str_hash`, so verb-name matching and property-name hashing inherit the same ASCII-only rule.

`in` on two strings is a case-insensitive substring search returning a 1-based index
(`execute.cc:1409-1414`, `strindex(..., 0)`). `x in map` searches **values** and returns an
ordinal position in iteration order (`collection.cc:45-69`), not a key.

### Truthiness excludes objects and errors

`utils.cc:381-400`. INT/FLOAT are non-zero, STR is non-empty, LIST is non-empty, MAP is non-empty,
BOOL is `true`. Everything else — OBJ, ERR, WAIF, ANON — falls to `default: return 0`. So a valid
object is falsy, and `if (obj)` is a silent no-op.

### Verb-name matching — `verbcasecmp`

`utils.cc:76-110`. The prose algorithm in "Verb names are patterns" above is a direct transcription
of this function; the loop structure is small enough that reimplementing from the description
should be exact. Two details easy to lose: the `star` flag distinguishes a trailing `*` (matches
arbitrary remaining input) from an interior `*` (only permits *truncation* of the candidate), and
the outer loop restarts at each space-delimited name in the verb's name string.

Verb *lookup* then scans an object's verbdef list head-to-tail and takes the first
`verbcasecmp` hit, optionally requiring `VF_EXEC` (`db_verbs.cc:227-237`). New verbs are appended
to the tail (`db_verbs.cc:217-223`), which is why adding a verb never shadows an existing one on
the same object — only inserting earlier in the list does.

### Task limits and permission defaults

`include/options.h:127-133` defines `DEFAULT_FG_TICKS 60000`, `DEFAULT_BG_TICKS 30000`,
`DEFAULT_FG_SECONDS 5`, `DEFAULT_BG_SECONDS 3`, `DEFAULT_MAX_STACK_DEPTH 50` and
`DEFAULT_LAG_THRESHOLD 5.0`.

These are consulted per task through `server_int_option()`, which reads
`$server_options.<name>` and falls back to the constant: `execute.cc:3098-3104` for the four
tick/second limits, `execute.cc:3253` for stack depth.

There is **no compiled-in default verb permission string** — `add_verb()` requires one explicitly.
The familiar defaults are core behaviour: `Survive/Generic_Programmer/6_@verb.moo:34-41` chooses
`"rxd"` (force-adding `x` if absent) when the argspec is exactly `this none this`, and `"rd"`
otherwise, with `player:prog_option("verb_perms")` as a per-programmer override.

### Prepositions — 15, not 14

`db_verbs.cc:48-65`. The doc previously omitted **`beside`**, which sits between `behind` and
`for/about`. The comment above the table notes indices are stored raw in the DB file, so the order
is frozen and entries are never removed — the list can only ever grow at the end.

### Scattering assignment

`parser.y:466-495` (a list literal on the left of `=` is rewritten into `EXPR_SCATTER`) and
`parser.y:742-779` (the explicit `{...} = expr` form with `?optional` and `@rest` items).
`vet_scatter()` at `parser.y:1105-1122` enforces at most one `@` target and at most 255 targets;
`scatter_from_arglist()` at `parser.y:1082-1103` rejects anything that isn't a bare identifier.

At runtime `EOP_SCATTER` (`execute.cc:2480-2547`) raises `E_TYPE` for a non-list RHS and `E_ARGS`
when the length can't satisfy the required targets.

### Miscellaneous confirmations

- `tostr()` renders a LIST as the literal text `{list}` and a MAP as `[map]`
  (`list.cc:394-400`). `toliteral()` is the one that renders contents.
- `obj.:name` is parsed at `parser.y:405-416` into `obj.(":name")`; the `:` prefix constant is
  `WAIF_PROP_PREFIX` (`include/waif.h:27-28`).
- `pass` and `raise` are registered builtins, not syntax (`execute.cc:3817, 3825`);
  `raise(code [, message [, value]])` is what populates elements 2 and 3 of the `except` tuple.
- `:initialize` is invoked by the C server from `create()` at `objects.cc:435` (with init-args) and
  `objects.cc:510`, so it is a genuine server-called verb rather than a core convention.
- `handle_verb_programmed` is a **local uncommitted patch** to this tree
  (`execute.cc:82-90, 3176-3189`; `tasks.cc:738-743`; `verbs.cc:546`). `git log -S` finds no commit
  introducing it and `git status` shows those four files modified. It is not upstream ToastStunt.

---

## Verified against the live server (M0, ToastStunt 2.7.3_5 + ToastCore)

Confirmed against the running instance and/or the C source in this fork. Superseding any
`[verify]` tags above on the same topic.

### The `;` eval command silently discards code after your first statement — unless you know the trick

**This bit hard during recon and will bite anyone testing interactively.** `#58:eval_cmd_string`
(the code behind the `;` command) contains:

```moo
if (!match(program, "^ *%(;%|%(if%|fork?%|return%|while%|try%)[^a-z0-9A-Z_]%)"))
  program = "return " + program;
endif
```

If your input does **not** already start with `;`, `if`, `fork`/`forked`, `return`, `while`, or
`try`, the whole thing gets prefixed with `return `. So `; x = 1; return x + 100;` compiles as
`return x = 1; return x + 100;` — the first `return` fires immediately (with value `1`, the value
of the assignment expression), and the second statement is dead code that never runs. No error,
no warning — it just quietly gives you the wrong answer. This is inherited LambdaCore behavior,
not ToastStunt-specific.

Consequences:
- A bare command like `; a; b; c;` returns the value of `a` and silently drops `b` and `c`.
- Wrapping in a keyword the check recognizes avoids it: `; if (1) a; b; c; endif` runs all three.
- The classic power-user idiom is a **leading double semicolon**: `;; a; b; c;` — the outer `;`
  is the command prefix, the inner `;` makes the check match "already starts with `;`" (an empty
  statement), so no auto-`return` is prepended and your statements run in order.
- Semicolons *inside* a quoted string are safe (`; return "a;b;c";` works correctly) — the
  auto-prefix check only cares about what the text starts with, not about brace/bracket nesting
  elsewhere in the line.
- This is purely a quirk of the interactive eval command. Code shipped via `set_verb_code()` (a
  list of source lines, not raw command text) is never touched by this logic — which is exactly
  why the plan's compile-probe design (M3) uses a scratch verb instead of the raw eval channel.

### `set_verb_code()` / `verb_code()` error shape — confirmed

- On failure: a list of one-or-more human-readable error strings, e.g. `{"Line 1:  syntax error"}`.
  The verb's code is left unchanged.
- On success: empty list `{}`.
- `verb_code()` returns the current code as a list of strings, one per line, matching what
  `set_verb_code()` expects as input.

### FileIO path restriction — confirmed from `src/fileio.cc`

- Every path goes through `file_resolve_path()`, which prepends `file_subdir` (the `-i` option;
  **default `"files/"` relative to the server's current working directory** — it is not
  auto-created, `mkdir` it yourself before first use).
- `file_verify_path()` rejects (returns `E_INVARG`, not a silent no-op) any path that starts with
  `..`, or that contains the substring `/.` **anywhere** — this also blocks hidden files/dotfiles
  and any `../` traversal buried mid-path, not just at the start.
- A leading `/` is stripped and the path is then treated as relative to `file_subdir` — so
  `file_open("/etc/passwd", ...)` does not reach the real `/etc/passwd`; it resolves to
  `files/etc/passwd` under the server's FileIO root (and fails with `ENOENT` if that doesn't
  exist there, not a permissions error).
- Mode string for `file_open()` must be **exactly 4 characters**: `[r|w|a][+|-][t|b][f|n]` — e.g.
  `"r-tn"` (read, text, no flush) or `"w-tf"` (write, text, flush-on-write). Anything else raises
  `E_INVARG "Invalid mode string"`. Note the 4th character must be `f` or `n`, **not** `-`.

### `exec()` sandbox — confirmed from `src/exec.cc`

- Wizard-only: raises `E_PERM` otherwise (checked via `is_wizard(progr)`).
- The command path is prepended with `exec_subdir` (the `-x` option; default `"executables/"`).
- Rejects (raises `E_INVARG "Invalid path"`) any command starting with `/` or `..`, or containing
  `/.` or `./` anywhere.
- Confirmed to implicitly suspend the task (`make_suspend_pack`) as documented — a slow external
  process does not block the server.

### `function_info()` output shape — confirmed live

`function_info(name)` returns `{name, min_args, max_args, {arg_type_1, arg_type_2, ...}}`, e.g.
`function_info("create")` → `{"create", 1, 4, {-1, -1, -1, -1}}` (`-1` = `TYPE_ANY`).
`function_info()` with no arguments returns the full list (238 entries on this build).

### `create()` full signature — confirmed from `src/objects.cc`

```
create(OBJ|LIST parent(s) [, OBJ owner] [, INT anonymous] [, LIST init-args])
```

The 2nd/3rd/4th positional args are disambiguated **by type**, not position: an `OBJ` is taken as
owner, an `INT` as the anonymous flag, a `LIST` as init-args — they can appear in any order after
the parent. **Gotcha:** `create($nothing, 1)` does not mean "owner #1" — the `1` is an `INT`, so
it's read as the anonymous flag, silently producing an anonymous object instead of a normal one
owned by you. Anonymous objects print as `*anonymous*` and behave differently enough (e.g. in
list/return-value printing) that this is an easy source of confusing test results. Use
`create(parent)` alone for a plain object owned by the calling programmer.

### Multiple-inheritance verb dispatch order — confirmed from `src/db_verbs.cc` and live

`db_find_callable_verb()` does a **depth-first search, left-to-right through the parents list,
with no C3-style linearization**. Concretely: given `child` with `parents = {p1, p2}`, the search
checks `child` itself, then `p1`, then **all of `p1`'s ancestors** (via `p1`'s own parents,
recursively) — and only once that entire branch is exhausted does it ever look at `p2`. Confirmed
live: a grandparent-only verb reachable through `p1` was returned in preference to `p2`'s own verb
of the same name, even though `p2` is "closer" in the parents list. Shared ancestors can be
visited more than once since there's no de-duplication/linearization — build the LSP resolver
around this exact order, not a C3 assumption.

### Waifs — `new_waif()` needs a real calling verb, confirmed from `src/waif.cc`

`new_waif()` takes **zero arguments** and uses `caller()` (the object of the verb that invoked
it) as the waif's class — not `this`, and not an argument you pass. Calling it from the bare `;`
eval command fails with `E_INVIND`, because `caller()` there is `#-1` (invalid). It must be called
from inside a real verb on a real object; that object becomes `waif.class`. `typeof()` a waif is
`13` (`TYPE_WAIF`).

### Checkpoint / `dump_database()` — spot-checked, not exhaustively verified

Maps stored via `add_property()` round-trip correctly as live in-memory property values.
`dump_database()` ran without error and produced an updated `.db.new` file reflecting session
changes. The on-disk **binary/text format** of the checkpoint (map and waif encoding specifically)
was not byte-level inspected here — that's `db_file.cc`-reading work for whenever the DB parser
tool actually gets built, not something worth re-deriving by hand in a recon pass.
