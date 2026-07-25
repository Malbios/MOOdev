/// Thin interop over the monaco-editor npm package - "Monaco's API is a
/// direct call" per the project plan's settled decisions, so this is
/// intentionally a light wrapper (just enough typed surface for what App.fs
/// needs) rather than a full binding library.
module Client.Monaco

open Fable.Core
open Fable.Core.JsInterop
open Client.LspClient

// Vite's `?worker` import suffix bundles the target module as a Web Worker
// and gives back its constructor - the standard dependency-free way to wire
// Monaco under Vite (see Monaco's own Vite integration docs). We only ever
// use our own "moocode" Monarch language, never Monaco's built-in
// JSON/CSS/HTML/TS modes, so the plain editor worker is the only one needed.
let private editorWorkerCtor: obj = importDefault "monaco-editor/esm/vs/editor/editor.worker?worker"

emitJsStatement
    editorWorkerCtor
    "self.MonacoEnvironment = { getWorker: function (_moduleId, _label) { return new $0() } }"

let private monaco: obj = importAll "monaco-editor"

/// The Monarch tokenizer for MOOcode. Written as a raw JS object literal
/// (regex literals and all) rather than built up through generic F#
/// interop helpers - Monarch's rule shape (arrays of regex/action pairs,
/// nested tokenizer states) doesn't map cleanly onto typed bindings, and
/// this is static data with no F# values spliced in.
///
/// Deliberately not exhaustive - covers control keywords, built-in verb-call
/// variables, error constants, strings, block comments, object/property
/// references, and numbers - enough for the "stops hurting" bar M3 sets, not
/// a complete language spec.
///
/// Comments are `/* ... */` only (non-nesting - the first `*/` closes),
/// confirmed against `toaststunt/src/parser.y:889-908` and documented in
/// `C:\dev\moo\moocode-reference.md`. An earlier version of this comment
/// claimed MOOcode has no comment syntax at all - that was wrong, corrected
/// this session after reading the parser source directly. There is no `//`
/// line-comment form; a bare `/` not followed by `*` is just division.
let private moocodeLanguage: obj =
    emitJsExpr
        ()
        """({
        defaultToken: '',
        keywords: [
            'if', 'elseif', 'else', 'endif',
            'for', 'in', 'endfor',
            'while', 'endwhile',
            'fork', 'endfork',
            'try', 'except', 'endtry', 'finally', 'endfinally',
            'return', 'break', 'continue', 'pass'
        ],
        builtinVariables: [
            'this', 'caller', 'player', 'verb', 'args', 'argstr',
            'dobj', 'dobjstr', 'prep', 'prepstr', 'iobj', 'iobjstr'
        ],
        constants: ['true', 'false', 'E_NONE'],
        tokenizer: {
            root: [
                [/\bE_[A-Z]+\b/, 'constant'],
                [/[a-zA-Z_$][\w$]*/, {
                    cases: {
                        '@keywords': 'keyword',
                        '@builtinVariables': 'variable.predefined',
                        '@constants': 'constant',
                        '@default': 'identifier'
                    }
                }],
                [/#-?\d+/, 'annotation'],
                [/\$[\w]+/, 'annotation'],
                [/\d+\.\d+([eE][-+]?\d+)?/, 'number.float'],
                [/\d+/, 'number'],
                [/"([^"\\]|\\.)*"/, 'string'],
                [/\/\*/, 'comment', '@comment'],
                [/[{}()\[\]]/, '@brackets'],
                [/[<>=!]=|[-+*\/%<>!&|]=?|=>|\?|:|;|,|\.\.|\./, 'operator'],
                [/\s+/, 'white']
            ],
            comment: [
                [/[^\/*]+/, 'comment'],
                [/\*\//, 'comment', '@pop'],
                [/[\/*]/, 'comment']
            ]
        }
    })"""

/// Auto-indent rules for MOOcode's keyword-delimited blocks (`if/endif`,
/// `for/endfor`, `while/endwhile`, `fork/endfork`, `try/except/finally/
/// endtry`) - there are no braces to hang indentation off of, so this uses
/// Monaco's regex-based `indentationRules` instead of the bracket-based
/// auto-indent most languages get for free. Purely cosmetic: MOOcode's
/// grammar is not whitespace-sensitive (`parser.y` has no significant-
/// indentation rule anywhere), so this can never change what code actually
/// does - it only affects how newly-typed lines get indented.
///
/// `increaseIndentPattern` matches a block-opening line, indenting the
/// *next* line one level deeper - guarded by a negative lookahead so a
/// same-line-closed block (`if (x) return; endif;`, extremely common in
/// this corpus) does not also indent the line after it. `else`/`elseif`/
/// `except`/`finally` deliberately appear in *both* patterns: matching
/// `decreaseIndentPattern` dedents the keyword line itself back to its
/// opening statement's level, and matching `increaseIndentPattern` still
/// indents whatever follows it one level deeper again - the same "outdent-
/// then-indent" shape most editors give `} else {`.
let private moocodeLanguageConfiguration: obj =
    emitJsExpr
        ()
        """({
        indentationRules: {
            increaseIndentPattern: /^\s*(if|elseif|else|for|while|fork|try|except|finally)\b(?!.*\bend(if|for|while|fork|try)\b).*$/,
            decreaseIndentPattern: /^\s*(endif|endfor|endwhile|endfork|endtry|elseif|else|except|finally)\b.*$/
        }
    })"""

/// Registers the "moocode" language with Monaco. Call once, before creating
/// any editor that might use it.
let registerMoocodeLanguage () : unit =
    monaco?languages?register (createObj [ "id" ==> "moocode" ])
    monaco?languages?setMonarchTokensProvider ("moocode", moocodeLanguage)
    monaco?languages?setLanguageConfiguration ("moocode", moocodeLanguageConfiguration)

type ITextModel = obj

/// Just enough of Monaco's `IEditorAction` to invoke a built-in action by
/// id - see `reindentLinesActionId` below for the one this app actually
/// uses.
type IEditorAction =
    abstract run: unit -> obj

/// The id of Monaco's built-in "Reindent Lines" action (confirmed directly
/// in the installed package's own source,
/// `esm/vs/editor/contrib/indentation/browser/indentation.js`, not assumed)
/// - reindents the whole document using the same `indentationRules`
/// `moocodeLanguageConfiguration` already registers. `indentationRules`
/// alone only affects *newly typed* lines (pressing Enter, pasting) - it
/// has no effect on content that arrives via `setValue`, which is how every
/// verb actually loads here. Running this action once right after
/// `setValue` is what makes already-written, flatly-indented corpus code
/// (the overwhelming majority of it) actually show up indented.
let reindentLinesActionId = "editor.action.reindentlines"

type IStandaloneCodeEditor =
    abstract getValue: unit -> string
    abstract setValue: value: string -> unit
    abstract updateOptions: options: obj -> unit
    /// Fires on every content change, whether from typing or a programmatic
    /// `setValue` call - callers that only care about *user* edits (e.g. a
    /// "dirty" flag for autosave) must account for the latter themselves.
    abstract onDidChangeModelContent: listener: (obj -> unit) -> obj
    /// Fires when focus leaves the editor *and* all of its own widgets
    /// (find/replace, suggestions, hover) - unlike `onDidBlurEditorText`,
    /// which also fires when focus merely moves to one of those widgets
    /// while conceptually still "in" the editor.
    abstract onDidBlurEditorWidget: listener: (unit -> unit) -> obj
    abstract focus: unit -> unit
    /// Called with no arguments - Monaco measures its own container. Needed
    /// because a Monaco instance living in a container that was
    /// `display:none` doesn't always pick up its new size via
    /// ResizeObserver alone once shown again; an explicit `layout()` call
    /// right after activating the Editor tab avoids a stale/blank render.
    abstract layout: unit -> unit
    /// Fires whenever the primary cursor moves - the listener's event
    /// object carries a `position: { lineNumber, column }` (both 1-based),
    /// same shape hover/definition already read via `LspClient.fs`'s
    /// `position?lineNumber`/`position?column` convention.
    abstract onDidChangeCursorPosition: listener: (obj -> unit) -> obj
    abstract getAction: id: string -> IEditorAction
    /// Moves the cursor (no scrolling) - `lineNumber`/`column` both 1-based,
    /// same convention as everywhere else in this file. Used by go-to-
    /// definition's same-document case (a local variable's definition,
    /// where the target verb is already open, so nothing needs re-fetching
    /// - just the cursor moving).
    abstract setPosition: position: obj -> unit
    /// Scrolls so `position` ends up vertically centered, without stealing
    /// focus or changing the selection - pairs with `setPosition` so a
    /// same-document go-to-definition jump is actually visible, not just
    /// technically-moved off-screen.
    abstract revealPositionInCenter: position: obj -> unit

/// Creates a standalone editor in the given DOM element, defaulting to the
/// "moocode" language and a dark theme matching the terminal's own styling.
/// Minimap enabled (not just for the code thumbnail itself) so the built-in
/// "cursor is here" mark in the overview ruler has visual separation from
/// the scrollbar - with the minimap off, that mark renders flush against
/// the scrollbar with no gap, and Monaco's public API has no option to
/// reposition it directly (it's drawn on an internal canvas, not a
/// positionable element - `hideCursorInOverviewRuler` only offers show/hide,
/// confirmed by reading the installed package's own type definitions).
let create (container: Browser.Types.HTMLElement) : IStandaloneCodeEditor =
    let options =
        createObj
            [ "value" ==> ""
              "language" ==> "moocode"
              "theme" ==> "vs-dark"
              "automaticLayout" ==> true
              "minimap" ==> createObj [ "enabled" ==> true ] ]

    monaco?editor?create (container, options)

/// Wires the Phase 4.4 LSP server's hover/definition/completion/signature
/// help/find-references into this Monaco instance for "moocode" - see
/// `LspClient.wire` for what the callbacks are for. Call once, after
/// `create`.
let wireLsp
    (getCurrentDocument: unit -> (int64 * string) option)
    (jumpTo: int64 -> string -> int -> int -> unit)
    (showCaveat: string -> unit)
    : unit =
    wire monaco getCurrentDocument jumpTo showCaveat
