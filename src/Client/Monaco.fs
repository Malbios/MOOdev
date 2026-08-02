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
/// confirmed against `ToastStunt/src/parser.y:889-908` and documented in
/// `moocode-reference.md` (repo root). An earlier version of this comment
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

/// Three fixed local boilerplate snippets - a verb's arg-spec scatter
/// skeleton, a `try`/`except (ANY)` guard, and this project's own leading
/// block-comment doc-comment convention (confirmed the real one via
/// `Handlers.fs`'s own doc comment on `inferredVerbSummary`, not guessed -
/// MOOcode has exactly one comment form, non-nesting). `$1`/`${1:text}` tab
/// stops need `insertTextRules = 4` (`InsertAsSnippet` - confirmed directly
/// in the installed package's own `editor.api.d.ts:6980-6991`, same
/// "read the real enum value" discipline `KeyCode.F2 = 60`/
/// `MarkerSeverity.Error = 8` already establish in this file) or Monaco
/// inserts the tab-stop syntax as literal text instead of parsing it.
///
/// Registered as a **second, purely client-side** completion provider for
/// "moocode", alongside (not replacing) `LspClient.fs`'s own LSP-backed
/// `registerCompletionItemProvider` call - these three are static local
/// text, nothing here needs a server round trip, so keeping this separate
/// avoids touching that provider's LSP-only contract at all.
let registerSnippetProvider () : unit =
    let snippets =
        [ "argspec", "Verb arg-spec scatter skeleton", "{${1:who}, ?${2:what} = ${3:0}, @${4:rest}} = args;"
          "try/except (ANY)", "try/except (ANY) guard", "try\n\t$1\nexcept ${2:e} (ANY)\n\t$3\nendtry"
          "/* doc comment */", "Leading doc-comment block", "/* ${1:Describe this verb.} */\n$0" ]

    let provideCompletionItems (model: obj) (position: obj) : obj =
        let wordInfo = model?getWordUntilPosition (position)

        let range =
            createObj
                [ "startLineNumber" ==> position?lineNumber
                  "startColumn" ==> wordInfo?startColumn
                  "endLineNumber" ==> position?lineNumber
                  "endColumn" ==> wordInfo?endColumn ]

        let suggestions =
            snippets
            |> List.map (fun (label, detail, insertText) ->
                createObj
                    [ "label" ==> label
                      "kind" ==> 27 // CompletionItemKind.Snippet
                      "detail" ==> detail
                      "insertText" ==> insertText
                      "insertTextRules" ==> 4 // CompletionItemInsertTextRule.InsertAsSnippet
                      "range" ==> range ])
            |> Array.ofList

        createObj [ "suggestions" ==> suggestions ]

    monaco?languages?registerCompletionItemProvider (
        "moocode",
        createObj [ "provideCompletionItems" ==> System.Func<obj, obj, obj>(fun m p -> provideCompletionItems m p) ]
    )
    |> ignore

type ITextModel =
    /// Confirmed in the installed package's own `editor.api.d.ts:2093`.
    abstract getLineMaxColumn: lineNumber: int -> int
    /// Confirmed in the installed package's own `editor.api.d.ts:2064`.
    abstract getLineCount: unit -> int

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
    /// Binds `handler` to `keybinding` - unlike `addAction` (which merely
    /// *contributes* an action that only gets a live keybinding if nothing
    /// else already claims it), this *overrides* whatever was bound to that
    /// exact key combo before, editor-instance-scoped (confirmed directly
    /// against the installed package's own `editor.api.d.ts`, same as
    /// `MarkerSeverity.Error = 8` above). Needed for F2 specifically: Monaco
    /// ships a built-in "Rename Symbol" action already bound to F2 that
    /// silently no-ops for a language with no registered rename provider
    /// (`moocode` has none - this project's rename is a custom action, not
    /// `textDocument/rename`) - `addAction` alone left that built-in
    /// shadowing every keypress with no visible effect and no error,
    /// confirmed live before switching to `addCommand` here.
    abstract addCommand: keybinding: int * handler: System.Action -> unit
    /// The cursor's current position (`{lineNumber, column}`, both 1-based)
    /// - `null` if the editor has no model yet. Same shape
    /// `onDidChangeCursorPosition`'s event object already carries.
    abstract getPosition: unit -> obj
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
    abstract getModel: unit -> ITextModel

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

/// F2 - the universal "rename symbol" convention, not a context-menu item
/// (`KeyCode.F2 = 60`, confirmed directly in the installed package's own
/// `editor.api.d.ts`, same as `MarkerSeverity.Error = 8` above). `onRename`
/// gets no arguments - callers already have `editor` itself in scope
/// (this is called right after `create`) and read the cursor position via
/// `editor.getPosition()` themselves, rather than this function threading
/// it through.
let registerRenameAction (editor: IStandaloneCodeEditor) (onRename: unit -> unit) : unit =
    editor.addCommand (60, System.Action(onRename))

/// The id of Monaco's built-in "Show or Focus Hover" action - the same
/// documentation/type-info tooltip that already appears on mouse hover,
/// triggerable from the keyboard instead (confirmed directly in the
/// installed package's own source,
/// `esm/vs/editor/contrib/hover/browser/hoverActionIds.js`).
let showHoverActionId = "editor.action.showHover"

/// Ctrl+Shift+Space - runs `showHoverActionId` for whatever's at the current
/// cursor position. `KeyMod.CtrlCmd = 2048`, `KeyMod.Shift = 1024`,
/// `KeyCode.Space = 10` (all confirmed directly in the installed package's
/// own source, `esm/vs/editor/common/services/editorBaseApi.js` and
/// `esm/vs/editor/editor.api.d.ts` respectively - same "read the real enum
/// value" discipline `KeyCode.F2 = 60` above already establishes). Doesn't
/// override anything: Monaco's own default keybinding for this action is
/// the chord Ctrl+K Ctrl+I (confirmed in
/// `esm/vs/editor/contrib/hover/browser/hoverActions.js`), not a single
/// combo, so Ctrl+Shift+Space was unclaimed.
let registerShowHoverKeybinding (editor: IStandaloneCodeEditor) : unit =
    editor.addCommand (2048 ||| 1024 ||| 10, System.Action(fun () -> editor.getAction(showHoverActionId).run () |> ignore))

/// Replaces every compile-error marker on `editor`'s current model with
/// `lineErrors` (line number, message) - an empty list clears them. Owner
/// string is just a namespace so this diagnostic source can't collide with
/// another one setting markers on the same model; there's only one source
/// today. `MarkerSeverity.Error = 8`, confirmed in editor.api.d.ts:90-95.
/// Clamps the reported line to the model's actual last line - confirmed
/// live (via the debounced live-diagnostics check, which reports on
/// mid-edit, not-yet-saved text far more often than the old save-time-only
/// check ever did) that MOO's own compile errors can name a line number
/// past the end of a short/incomplete buffer (e.g. an unterminated
/// statement on the buffer's only line reported as "Line 2"), and
/// `getLineMaxColumn` throws outright for an out-of-range line - this
/// would otherwise silently abort the whole marker update, not just that
/// one marker.
let setErrorMarkers (editor: IStandaloneCodeEditor) (lineErrors: (int * string) list) : unit =
    let model = editor.getModel ()
    let lineCount = model.getLineCount ()

    let markers =
        lineErrors
        |> List.map (fun (line, message) ->
            let clampedLine = max 1 (min line lineCount)

            createObj
                [ "severity" ==> 8
                  "message" ==> message
                  "startLineNumber" ==> clampedLine
                  "startColumn" ==> 1
                  "endLineNumber" ==> clampedLine
                  "endColumn" ==> model.getLineMaxColumn clampedLine ])
        |> List.toArray

    monaco?editor?setModelMarkers (model, "moocode-compile", markers)

/// Just enough of Monaco's diff editor for Phase 5's per-verb history view:
/// a historical version (read-only "original" side) against the live editor's
/// current content ("modified" side). One instance is created lazily and
/// reused across every commit the user picks (`setDiffModel` swaps the
/// models in place) rather than recreated per selection - a diff editor owns
/// real DOM/canvas state, so churning instances would leak them.
type IDiffEditor =
    abstract setModel: models: obj -> unit
    abstract layout: unit -> unit
    abstract dispose: unit -> unit

/// Creates a read-only, side-by-side diff editor in `container` - "read-only"
/// because this view is for comparison, not editing; restoring an old version
/// is a separate explicit action (`App.fs` calls the *regular* editor's
/// `setValue` for that, not anything here).
let createDiffEditor (container: Browser.Types.HTMLElement) : IDiffEditor =
    let options =
        createObj
            [ "theme" ==> "vs-dark"
              "automaticLayout" ==> true
              "readOnly" ==> true
              "renderSideBySide" ==> true ]

    monaco?editor?createDiffEditor (container, options)

let private createModel (content: string) : ITextModel =
    monaco?editor?createModel (content, "moocode")

/// Swaps a diff editor's two sides to `originalText` (the historical
/// version) vs `modifiedText` (today's, usually the live editor's current
/// content) - each call creates fresh models, matching Monaco's own
/// "a model is disposable, an editor's models can be replaced wholesale"
/// convention rather than trying to mutate an existing model's text.
let setDiffModel (diffEditor: IDiffEditor) (originalText: string) (modifiedText: string) : unit =
    let original = createModel originalText
    let modified = createModel modifiedText
    diffEditor.setModel (createObj [ "original" ==> original; "modified" ==> modified ])

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
