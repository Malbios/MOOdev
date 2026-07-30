module Client.App

open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop

// Minimal binding for the Web Encoding API's TextDecoder - not covered by
// Fable.Browser.Dom, and this is the only place we need it. `decode` is
// non-fatal by design: invalid byte sequences become U+FFFD rather than
// throwing, which matters since MOO output isn't guaranteed valid UTF-8.
type private TextDecoder =
    abstract decode: data: obj -> string

[<Emit("new TextDecoder()")>]
let private createTextDecoder () : TextDecoder = jsNative

let private decoder = createTextDecoder ()

// `Element.scrollIntoView(options)` - not covered by Fable.Browser.Dom's
// typed bindings (only the argument-less overload is), and this is the
// only place that needs the centering/smooth-scroll options.
[<Emit("$0.scrollIntoView({ behavior: 'smooth', block: 'center' })")>]
let private scrollIntoViewCentered (el: HTMLElement) : unit = jsNative

// Vite exposes build-time env vars via import.meta.env.VITE_*; there's no
// typed Fable binding for import.meta itself, so this is a direct JS emit.
let private wsUrl: string =
    emitJsExpr () "import.meta.env.VITE_SIDECAR_WS_URL"

// Per-profile server core name (test.ps1's -Database, e.g. "Survive" or
// "ToastCore") - lets the browser tab tell multiple simultaneously-running
// profiles apart.
let private databaseName: string =
    emitJsExpr () "import.meta.env.VITE_DATABASE_NAME"

document.title <- sprintf "MOOcode Development: %s" databaseName

let private outputEl = document.getElementById ("output")
let private inputEl = document.getElementById ("input") :?> HTMLInputElement

let private loginPaneEl = document.getElementById ("login-pane")
let private loginUserEl = document.getElementById ("login-user") :?> HTMLInputElement
let private loginPassEl = document.getElementById ("login-pass") :?> HTMLInputElement
let private loginConnectBtn = document.getElementById ("login-connect")

let private settingsBtn = document.getElementById ("settings-btn")
let private settingsOverlayEl = document.getElementById ("settings-overlay")
let private settingsPanelEl = document.getElementById ("settings-panel")
let private settingsCloseBtn = document.getElementById ("settings-close")
let private settingWordWrapEl = document.getElementById ("setting-wordwrap") :?> HTMLInputElement
let private settingFontSizeEl = document.getElementById ("setting-fontsize") :?> HTMLInputElement
let private settingMinimapEl = document.getElementById ("setting-minimap") :?> HTMLInputElement
let private settingForgetLoginBtn = document.getElementById ("setting-forget-login")
let private settingForgetLoginStatusEl = document.getElementById ("setting-forget-login-status")

let private layoutEl = document.getElementById ("layout")

let private sidebarEl = document.getElementById ("sidebar")
let private treeFilterEl = document.getElementById ("tree-filter") :?> HTMLInputElement
let private treeFilterClearEl = document.getElementById ("tree-filter-clear")
let private treeFilterSettingsBtn = document.getElementById ("tree-filter-settings")
let private treeFilterSettingsPopoverEl = document.getElementById ("tree-filter-settings-popover")
let private treeNewObjectBtn = document.getElementById ("tree-new-object-btn")
let private treeNewObjectPopoverEl = document.getElementById ("tree-new-object-popover")
let private treeNewObjectParentEl = document.getElementById ("tree-new-object-parent") :?> HTMLInputElement
let private treeNewObjectCreateBtn = document.getElementById ("tree-new-object-create-btn")

let private treeFilterShowPropertiesEl =
    document.getElementById ("tree-filter-show-properties") :?> HTMLInputElement

let private treeFilterShowVerbsEl = document.getElementById ("tree-filter-show-verbs") :?> HTMLInputElement

let private treeFilterHideEmptyLeavesEl =
    document.getElementById ("tree-filter-hide-empty-leaves") :?> HTMLInputElement

let private treeListEl = document.getElementById ("tree-list")
let private sidebarResizerEl = document.getElementById ("sidebar-resizer")
let private sidebarToggleBtn = document.getElementById ("sidebar-toggle")

let private mainTabsEl = document.getElementById ("main-tabs")
let private tabGameBtn = document.getElementById ("tab-game")
let private verbTabsEl = document.getElementById ("verb-tabs")
let private editorPaneEl = document.getElementById ("editor-pane")
let private editorMonacoEl = document.getElementById ("editor-monaco")
let private editorDiagnosticsEl = document.getElementById ("editor-diagnostics")
let private statusDirtyEl = document.getElementById ("status-dirty")
let private statusPositionEl = document.getElementById ("status-position")
let private terminalPaneEl = document.getElementById ("terminal-pane")
let private inspectorPaneEl = document.getElementById ("inspector-pane")
let private inspectorContentEl = document.getElementById ("inspector-content")
let private inspectorDiagnosticsEl = document.getElementById ("inspector-diagnostics")
let private verbHistoryPaneEl = document.getElementById ("verb-history-pane")
let private verbHistoryListEl = document.getElementById ("verb-history-list")
let private verbHistoryDiffEditorEl = document.getElementById ("verb-history-diff-editor")
let private verbHistoryRestoreBtn = document.getElementById ("verb-history-restore-btn")
let private tabHistoryBtn = document.getElementById ("tab-history")
let private historyPaneEl = document.getElementById ("history-pane")
let private historySearchInputEl = document.getElementById ("history-search-input") :?> HTMLInputElement
let private historySearchResultsEl = document.getElementById ("history-search-results")
let private corponymHistoryListEl = document.getElementById ("corponym-history-list")

/// Carries the active ANSI style and any not-yet-complete escape sequence
/// bytes across calls - a single WebSocket frame can split a sequence in
/// half, see `Ansi.feed`'s own doc comment.
let mutable private ansiState = Ansi.initialState

let private appendOutput (text: string) : unit =
    let segments, newState = Ansi.feed ansiState text
    ansiState <- newState
    Ansi.renderInto outputEl segments
    outputEl.scrollTop <- outputEl.scrollHeight

/// Draggable divider between the sidebar and the main area, resizable and
/// persisted across reloads via localStorage, same "remember what the user
/// set" idea as command history's in-memory list, just surviving a refresh
/// too. (Used to also split the sidebar's objects/verbs panes, and the
/// editor/terminal split before that - both are gone now, folded into the
/// unified tree and tabs respectively - but the module stays generic over
/// both drag axes since a future resizable split is one `PaneResizer.init`
/// call away either way.)
///
/// Uses one pair of `window`-level mouse handlers rather than each resizer
/// owning its own - assigning `window.onmousemove` replaces whatever
/// handler was there before, so independent per-resizer handlers would just
/// keep clobbering each other. Instead, one shared mutable "which resizer
/// (if any) is currently being dragged" state is consulted
/// by a single pair of handlers registered once.
module private PaneResizer =
    type DragAxis =
        | LeftRight // dragging resizes width (container is a row)
        | UpDown // dragging resizes height (container is a column)

    type private Drag =
        { Axis: DragAxis
          StorageKey: string
          ContainerEl: HTMLElement
          PaneEl: HTMLElement
          ResizerEl: HTMLElement
          mutable LastPct: float }

    let private clamp (pct: float) : float = max 15.0 (min 85.0 pct)

    let private apply (d: Drag) (pct: float) : unit =
        d.PaneEl.setAttribute ("style", sprintf "flex: 0 0 %.2f%%" (clamp pct))

    let mutable private active: Drag option = None

    window.onmousemove <-
        fun ev ->
            match active with
            | None -> ()
            | Some d ->
                let mouseEv: Browser.Types.MouseEvent = unbox ev
                let rect = d.ContainerEl.getBoundingClientRect ()

                let pct =
                    match d.Axis with
                    | LeftRight -> (mouseEv.clientX - rect.left) / rect.width * 100.0
                    | UpDown -> (mouseEv.clientY - rect.top) / rect.height * 100.0

                d.LastPct <- pct
                apply d pct

    window.onmouseup <-
        fun _ ->
            match active with
            | Some d ->
                d.ResizerEl.classList.remove "dragging"
                window.localStorage.setItem (d.StorageKey, string d.LastPct)
                active <- None
            | None -> ()

    let init
        (axis: DragAxis)
        (storageKey: string)
        (containerEl: HTMLElement)
        (resizerEl: HTMLElement)
        (paneEl: HTMLElement)
        : unit =
        (match window.localStorage.getItem storageKey with
         | null -> ()
         | saved ->
             match System.Double.TryParse saved with
             | true, pct ->
                 apply
                     { Axis = axis
                       StorageKey = storageKey
                       ContainerEl = containerEl
                       PaneEl = paneEl
                       ResizerEl = resizerEl
                       LastPct = pct }
                     pct
             | false, _ -> ())

        resizerEl.classList.add "visible"

        resizerEl.onmousedown <-
            fun _ ->
                active <-
                    Some
                        { Axis = axis
                          StorageKey = storageKey
                          ContainerEl = containerEl
                          PaneEl = paneEl
                          ResizerEl = resizerEl
                          LastPct = 50.0 }

                resizerEl.classList.add "dragging"

/// Collapsible sidebar, same idea as VS Code's explorer-panel toggle - hides
/// the Objects/Verbs picker entirely and gives the editor/terminal the full
/// width back. Deliberately separate from `PaneResizer`: collapsing never
/// touches the persisted width, it just toggles a `.collapsed` class whose
/// `!important` overrides `PaneResizer`'s inline `flex` style while active,
/// so removing the class snaps straight back to whatever width was set
/// before. Persisted across reloads the same way as everything else here.
module private Sidebar =
    let private collapsedKey = "moodev-sidebar-collapsed"

    let private apply (collapsed: bool) : unit =
        if collapsed then
            sidebarEl.classList.add "collapsed"
            sidebarResizerEl.classList.add "collapsed"
        else
            sidebarEl.classList.remove "collapsed"
            sidebarResizerEl.classList.remove "collapsed"

        sidebarToggleBtn.textContent <- (if collapsed then "▶" else "◀")
        sidebarToggleBtn.setAttribute ("title", (if collapsed then "Show sidebar" else "Hide sidebar"))

    let init () : unit =
        apply (window.localStorage.getItem collapsedKey = "1")

        sidebarToggleBtn.onmousedown <- fun ev -> ev.stopPropagation () |> ignore

        sidebarToggleBtn.onclick <-
            fun _ ->
                let collapsed = not (sidebarEl.classList.contains "collapsed")
                window.localStorage.setItem (collapsedKey, (if collapsed then "1" else "0"))
                apply collapsed

/// Remembers the last-used player name/password in localStorage so a
/// returning visit can log straight back in, instead of retyping "connect
/// wizard ..." every time. Plaintext localStorage is an acceptable tradeoff
/// here - this is a personal single-user dev tool talking to a local MOO
/// instance, not a multi-tenant service - and it's strictly better than the
/// alternative of typing the password into the free-text terminal input,
/// which would otherwise land in `commandHistory` (kept in memory,
/// arrow-key-navigable for the rest of the session).
module private Login =
    let private userKey = "moodev-login-user"
    let private passKey = "moodev-login-pass"

    let private saved () : (string * string) option =
        match window.localStorage.getItem userKey with
        | null -> None
        | "" -> None
        | user ->
            let pass =
                match window.localStorage.getItem passKey with
                | null -> ""
                | p -> p

            Some(user, pass)

    let private save (user: string) (pass: string) : unit =
        window.localStorage.setItem (userKey, user)
        window.localStorage.setItem (passKey, pass)

    /// Empty password is a real, supported case - a fresh ToastCore db's
    /// wizard has none (see test.ps1's own printed hint: "just type: connect
    /// wizard") - sending a trailing space for it would be needless noise.
    let private connect (send: string -> unit) (user: string) (pass: string) : unit =
        save user pass
        send (if pass = "" then "connect " + user else "connect " + user + " " + pass)

    /// Called once the socket is open - auto-logs in immediately if
    /// credentials were saved from a previous visit, otherwise leaves the
    /// login form visible (default CSS state) for the user to fill in.
    let init (send: string -> unit) : unit =
        match saved () with
        | Some(user, pass) ->
            loginUserEl.value <- user
            loginPassEl.value <- pass
            connect send user pass
        | None -> loginUserEl.focus ()

        let submit () =
            let user = loginUserEl.value.Trim()

            if user <> "" then
                connect send user loginPassEl.value

        loginConnectBtn.onclick <- fun _ -> submit ()
        loginUserEl.onkeydown <- fun ev -> if ev.key = "Enter" then submit ()
        loginPassEl.onkeydown <- fun ev -> if ev.key = "Enter" then submit ()

    /// Called once the server confirms a real login (`moodev-login-result`)
    /// - hides the form so it doesn't linger over an already-logged-in
    /// session.
    let hide () : unit = loginPaneEl.classList.add "hidden"

    /// Clears remembered credentials - does not affect an already-open
    /// connection, only whether the *next* page load auto-logs-in.
    let forget () : unit =
        window.localStorage.removeItem userKey
        window.localStorage.removeItem passKey

let private ws = WebSocket.Create(wsUrl)
ws.binaryType <- "arraybuffer"

Monaco.registerMoocodeLanguage ()
let private editor = Monaco.create editorMonacoEl

/// Word wrap / font size / minimap: real, live-applied Monaco preferences
/// persisted to localStorage - shown/edited via the gear-icon overlay, and
/// applied immediately on change (no explicit "Save" button, same "live"
/// feel as the sidebar's filter boxes).
module private Settings =
    let private wordWrapKey = "moodev-wordwrap" // "on" | "off", matches Monaco's own value domain
    let private fontSizeKey = "moodev-fontsize" // stringified int
    let private minimapKey = "moodev-minimap" // "on" | "off"
    let private hideEmptyLeavesKey = "moodev-hide-empty-leaves" // "on" | "off"
    let private showPropertiesKey = "moodev-show-properties" // "on" | "off"
    let private showVerbsKey = "moodev-show-verbs" // "on" | "off"

    let private loadString (key: string) (fallback: string) : string =
        match window.localStorage.getItem key with
        | null -> fallback
        | v -> v

    /// Default ON: once the tree includes the full object universe (not
    /// just verb-owners, like the old flat list), pure dead-ends (no
    /// children, no verbs of their own - stray/leftover objects) are almost
    /// always noise for day-to-day editing. Hiding them by default keeps
    /// the common case at least as compact as the old, familiar list; the
    /// checkbox lets a rarer "audit the whole database" session turn them
    /// back on. Read directly (not cached) since it only needs checking
    /// once per tree render, not on any hot path.
    let hideEmptyLeavesEnabled () : bool = loadString hideEmptyLeavesKey "on" = "on"

    let setHideEmptyLeaves (enabled: bool) : unit =
        window.localStorage.setItem (hideEmptyLeavesKey, (if enabled then "on" else "off"))

    /// Properties default OFF - noisy for day-to-day editing (most objects
    /// have far more properties than verbs), so they're opt-in via the tree
    /// filter's settings popover. Verbs stay default ON, matching today's
    /// always-shown behavior.
    let showPropertiesEnabled () : bool = loadString showPropertiesKey "off" = "on"
    let showVerbsEnabled () : bool = loadString showVerbsKey "on" = "on"

    let setShowProperties (enabled: bool) : unit =
        window.localStorage.setItem (showPropertiesKey, (if enabled then "on" else "off"))

    let setShowVerbs (enabled: bool) : unit =
        window.localStorage.setItem (showVerbsKey, (if enabled then "on" else "off"))

    let private apply (wordWrap: string) (fontSize: int) (minimap: bool) : unit =
        editor.updateOptions (
            createObj
                [ "wordWrap" ==> wordWrap
                  "fontSize" ==> fontSize
                  "minimap" ==> createObj [ "enabled" ==> minimap ] ]
        )

    /// Reapplies all three from the panel's current control values and
    /// persists them - always the full set rather than trying to figure out
    /// which single control changed.
    let private applyAndSaveFromControls () : unit =
        let wordWrap = if settingWordWrapEl.``checked`` then "on" else "off"

        let fontSize =
            match System.Int32.TryParse settingFontSizeEl.value with
            | true, n -> n
            | false, _ -> 14

        let minimap = settingMinimapEl.``checked``

        window.localStorage.setItem (wordWrapKey, wordWrap)
        window.localStorage.setItem (fontSizeKey, string fontSize)
        window.localStorage.setItem (minimapKey, (if minimap then "on" else "off"))
        apply wordWrap fontSize minimap

    /// Loads persisted settings (or defaults matching Monaco's/this app's
    /// existing hardcoded values, so nothing visibly changes for anyone
    /// until they actually open the panel), applies them to the editor, and
    /// initializes the panel's controls to match.
    let init () : unit =
        let wordWrap = loadString wordWrapKey "off"

        let fontSize =
            match System.Int32.TryParse(loadString fontSizeKey "14") with
            | true, n -> n
            | false, _ -> 14

        let minimap = loadString minimapKey "on" = "on"

        apply wordWrap fontSize minimap
        settingWordWrapEl.``checked`` <- (wordWrap = "on")
        settingFontSizeEl.value <- string fontSize
        settingMinimapEl.``checked`` <- minimap
        treeFilterShowPropertiesEl.``checked`` <- showPropertiesEnabled ()
        treeFilterShowVerbsEl.``checked`` <- showVerbsEnabled ()
        treeFilterHideEmptyLeavesEl.``checked`` <- hideEmptyLeavesEnabled ()

        settingWordWrapEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingFontSizeEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingMinimapEl.onchange <- fun _ -> applyAndSaveFromControls ()
        // The hide-empty-leaves/show-properties/show-verbs checkboxes' onchange
        // redraws the tree, not just Monaco (unlike the three above) - wired
        // separately, later in this file, once `renderTree` exists (this
        // module is defined before it).

        settingForgetLoginBtn.onclick <-
            fun _ ->
                Login.forget ()
                settingForgetLoginStatusEl.textContent <- "Cleared"

    let show () : unit = settingsOverlayEl.classList.add "visible"
    let hide () : unit = settingsOverlayEl.classList.remove "visible"

settingsBtn.onclick <- fun _ -> Settings.show ()
settingsCloseBtn.onclick <- fun _ -> Settings.hide ()
// Backdrop click closes the overlay; the panel stops its own clicks from
// bubbling to the backdrop, same "stop propagation so an inner click
// doesn't also trigger the outer handler" pattern `renderTabs`'s close-×
// button uses against its tab's own switch-click.
settingsOverlayEl.onclick <- fun _ -> Settings.hide ()
settingsPanelEl.onclick <- fun ev -> ev.stopPropagation () |> ignore

// Same "inner click stops propagation, outer click closes" idiom as the
// Settings overlay just above - `document` stands in for a dedicated
// backdrop element, since this is a small inline popover, not a full-screen
// modal. Opening one of these two sidebar popovers closes the other, same
// as VS Code's own toolbar popovers.
treeFilterSettingsBtn.onclick <-
    fun ev ->
        ev.stopPropagation () |> ignore
        treeNewObjectPopoverEl.classList.remove "visible"
        treeFilterSettingsPopoverEl.classList.toggle "visible" |> ignore

treeNewObjectBtn.onclick <-
    fun ev ->
        ev.stopPropagation () |> ignore
        treeFilterSettingsPopoverEl.classList.remove "visible"
        treeNewObjectPopoverEl.classList.toggle "visible" |> ignore

treeFilterSettingsPopoverEl.onclick <- fun ev -> ev.stopPropagation () |> ignore
treeNewObjectPopoverEl.onclick <- fun ev -> ev.stopPropagation () |> ignore

document.onclick <-
    fun _ ->
        treeFilterSettingsPopoverEl.classList.remove "visible"
        treeNewObjectPopoverEl.classList.remove "visible"

Settings.init ()

/// Which "tab" is showing in the main area - the game terminal, or one of
/// the open verbs. Game is a permanent, non-closable, always-first tab
/// (rendered as the static `#tab-game` button); every verb ever opened this
/// session gets its own closable tab alongside it in `#verb-tabs`, VS
/// Code-style. This is the single source of truth for both "which tab is
/// highlighted" and "what's loaded in the editor" - earlier versions of
/// this file kept a separate `currentDocument` in sync by hand; folding it
/// into this type removes that duplication.
type private OpenTab =
    | GameTab
    | VerbTab of objRef: int64 * verbName: string
    | InspectorTab of objRef: int64
    | HistoryTab

let mutable private activeTab: OpenTab = GameTab

/// Which property, if any, is the specific sub-focus within the currently
/// active `InspectorTab` - set alongside `activeTab` whenever
/// `revealPropertyInInspector` (the tree's `PropRow` click) requests one,
/// cleared (`None`) by any plain `openOrSwitchToInspector` that didn't ask
/// for a specific property. Read by `renderTreeRows` (always paired with an
/// `activeTab = InspectorTab objRef` check, same as the object/verb row
/// highlighting already does) to highlight that property's own row in the
/// sidebar - `activeTab` alone can't do it, since it only ever names an
/// object or verb, never a property within one.
let mutable private activeInspectorProp: (int64 * string) option = None

/// The object row most recently clicked in the tree (to expand/collapse it,
/// or via its "ⓘ" button) - kept independent of `activeTab` so an object
/// highlights the moment it's clicked, the same way a verb row already
/// highlights the moment it's opened, without requiring its own Inspector
/// tab to be the active one. Read by `renderTreeRows` alongside the
/// existing `activeTab = InspectorTab objRef` check - either one alone is
/// enough to highlight an object row, so an object can stay visibly
/// "selected" even after the main pane has moved on to a different tab.
let mutable private selectedObjRef: int64 option = None

/// Whether the currently-active `VerbTab`'s editor pane is showing its
/// history/diff view instead of the normal Monaco editor - orthogonal to
/// `activeTab` itself (it's a sub-mode of a `VerbTab`, not a distinct tab),
/// reset to `false` on every tab switch so opening/reactivating a verb tab
/// always starts back in the normal editor view.
let mutable private showingVerbHistory = false

/// Whether a real MOO login has succeeded this session - set by the
/// `moodev-login-result` handler. Nothing client-side previously needed this
/// as a standing boolean; the History tab uses it to skip firing
/// `corponym-history` before there's a logged-in player to ask about.
let mutable private isLoggedIn = false

/// Open verb tabs, in the order they were opened. Game isn't stored here -
/// it's permanent and rendered separately.
let mutable private openVerbTabs: (int64 * string) list = []

/// Open inspector tabs, in the order they were opened - parallel to
/// `openVerbTabs`, including the same preview-tab mechanic (see
/// `previewInspectorTab`). Unlike verb tabs, content is never cached
/// client-side - see `loadInspector`'s own comment for why.
let mutable private openInspectorTabs: int64 list = []

/// Each currently-rendered inspector's property `<input>` elements, by
/// property name - populated by `renderInspectorStructure`, then read both
/// by the `moodev-prop-content` handler (to fill in the live values once
/// they arrive) and by each input's own `onblur` handler (autosave-on-
/// change). Rebuilt fresh on every `loadInspector` call.
let mutable private inspectorPropertyInputs: Map<string, HTMLInputElement> = Map.empty

/// The value each property input was last loaded/saved with - compared
/// against on blur so autosave only fires on an actual change. Simpler than
/// the Monaco editor's `isDirty`-flag mechanism (see `setDirty`'s comment)
/// since a plain `<input>` has no "changed programmatically vs by the user"
/// ambiguity to account for - direct comparison is enough.
let mutable private inspectorPropertyLastValues: Map<string, string> = Map.empty

/// Each currently-rendered inspector's read-only ANSI-code preview `<div>`,
/// by property name - mirrors `inspectorPropertyInputs` exactly (same
/// population/reset points), but only ever written to by the
/// `moodev-prop-content` handler, never read from (there's nothing to save
/// back - see `renderLiteralPreview`'s call site).
let mutable private inspectorPropertyPreviews: Map<string, HTMLElement> = Map.empty

/// Each open tab's last-known content - populated when a verb is first
/// fetched, refreshed with the live editor value right before switching
/// away from it. Lets switching between already-open tabs be instant (no
/// server round-trip) and lets a closed-then-reopened-in-the-same-session
/// tab... actually no, closing drops its cache entry too (see `closeTab`) -
/// this only ever holds *currently open* tabs' content.
let mutable private tabContent: Map<int64 * string, string> = Map.empty

/// VS Code's "preview tab" mechanic: at most one open verb tab is ever a
/// preview at a time, shown in italics. Opening a brand-new verb while a
/// preview tab exists *replaces* it (same slot in `openVerbTabs`) rather
/// than adding another tab, so quickly browsing through verbs (sidebar
/// clicks, go-to-definition) doesn't pile up tabs. Double-clicking a
/// preview tab "pins" it (clears this, drops the italics) - after that, new
/// verbs open in their own tab instead of replacing it. Switching to an
/// already-open tab (preview or pinned) never changes this - only opening
/// something *not yet open* does.
let mutable private previewTab: (int64 * string) option = None

/// Same "preview tab" mechanic as `previewTab`, for inspector tabs: at most
/// one open inspector tab is a preview at a time (shown in italics via the
/// same `.preview` CSS class). Opening a brand-new inspector while a preview
/// exists replaces it in place rather than piling up tabs - useful since
/// clicking through owner/parent/child/verb-object links tends to hop
/// between objects quickly. Double-clicking pins it. Switching to an
/// already-open tab (preview or pinned) never touches this.
let mutable private previewInspectorTab: int64 option = None

/// The `(objRef, verb) option` shape a couple of call sites still need
/// (`saveIfDirty`, `Monaco.wireLsp`'s hover/definition callback) - derived
/// from `activeTab` rather than tracked separately.
let private currentVerbDoc () : (int64 * string) option =
    match activeTab with
    | VerbTab(o, v) -> Some(o, v)
    | GameTab
    | InspectorTab _
    | HistoryTab -> None

/// Sends a Phase 4 structured IDE-action envelope (`{"action": ..., ...}`)
/// over the main WebSocket - the sidecar's `Program.buildTryDispatch` parses
/// this JSON and dispatches to `Sidecar.IdeActions` instead of forwarding it
/// as raw MOO text (ordinary terminal input isn't valid JSON, so the two
/// never collide on the wire). Replaces the retired `$vcs:ide_*` verb calls
/// this client used to send as literal MOO source - the receiving side
/// (`ws.onmessage`'s `moodev-edit-*`/`moodev-prop-*` handlers below) is
/// unchanged, since the sidecar responds in the exact
/// same wire shape either way.
let private sendAction (fields: (string * obj) list) : unit =
    ws.send (JS.JSON.stringify (createObj fields))

// Wired here rather than alongside the popover's show/hide toggling above
// (which is plain top-level code that runs before `sendAction` itself is
// even defined) - F# requires a name to be lexically defined before use for
// ordinary top-level bindings, unlike the `let rec ... and ...` chain
// `renderTree`/`loadInspector`/etc. below belong to.
treeNewObjectCreateBtn.onclick <-
    fun _ ->
        let parentExpr = treeNewObjectParentEl.value.Trim()

        if parentExpr <> "" then
            sendAction [ "action" ==> "create-object"; "parentExpr" ==> parentExpr ]
            treeNewObjectParentEl.value <- ""
            treeNewObjectPopoverEl.classList.remove "visible"

/// Turns the editor's current content into the line array `IdeActions.saveVerb`
/// expects for its JSON `code` field.
let private codeLines (source: string) : string[] =
    source.Replace("\r\n", "\n").Split('\n')

/// Asks the server to load a verb - unconditionally, no "is this already
/// open" check (that's `openOrSwitchToVerb`'s job). The `moodev-edit-content`
/// handler is what actually adds the resulting tab and shows it once the
/// content arrives.
let private fetchVerb (objRef: int64) (verb: string) : unit =
    sendAction [ "action" ==> "fetch-verb"; "obj" ==> int objRef; "verb" ==> verb ]

/// Whether the editor holds unsaved changes - set on every real content
/// change, cleared right after a save is sent and right after a fresh verb
/// loads (`editor.setValue` calls also fire `onDidChangeModelContent`, so
/// each must reset this *after* calling `setValue`/sending the save, not
/// before). Guards autosave-on-blur against firing on an unchanged document
/// - without it, merely switching tabs and back would re-save identical
/// content, adding a no-op commit to `Survive`'s git history each time
/// (`$vcs`'s capture hook commits on every successful `set_verb_code()`,
/// not just real diffs).
let mutable private isDirty = false

/// The single place `isDirty` ever changes - also keeps the status bar's
/// dirty/saved indicator in sync, so every call site gets that for free
/// instead of needing to remember to update the status bar itself.
let private setDirty (value: bool) : unit =
    isDirty <- value
    statusDirtyEl.textContent <- if value then "Modified" else "Saved"

    if value then
        statusDirtyEl.classList.add "modified"
    else
        statusDirtyEl.classList.remove "modified"

editor.onDidChangeModelContent (fun _ -> setDirty true) |> ignore

/// Autosaves the currently-open verb, but only if it's actually been
/// edited since it was loaded or last saved - see `isDirty`'s own comment
/// for why that check matters. Wired to the editor losing focus entirely
/// (`onDidBlurEditorWidget`, not `onDidBlurEditorText` - the latter also
/// fires when focus merely moves to one of the editor's own widgets, like
/// the find box, which isn't "leaving" the editor at all).
///
/// This also happens to be what makes closing/switching tabs safe without
/// any confirmation prompt: clicking a different tab or a tab's close
/// button is a click outside Monaco, which blurs it - and therefore runs
/// this - *before* the click handler that switches/closes anything, per
/// standard DOM event ordering. By the time a tab becomes a background tab
/// at all, its edits (if any) are already flushed to the server.
let private saveIfDirty () : unit =
    if isDirty then
        match currentVerbDoc () with
        | Some(objRef, verb) ->
            let code = codeLines (editor.getValue ())
            sendAction [ "action" ==> "save-verb"; "obj" ==> int objRef; "verb" ==> verb; "code" ==> code ]
            setDirty false
        | None -> ()

editor.onDidBlurEditorWidget (fun () -> saveIfDirty ()) |> ignore

// Keeps the status bar's cursor-position readout live.
editor.onDidChangeCursorPosition (fun ev ->
    let line: int = ev?position?lineNumber
    let col: int = ev?position?column
    statusPositionEl.textContent <- sprintf "Ln %d, Col %d" line col)
|> ignore

setDirty false
statusPositionEl.textContent <- "Ln 1, Col 1"

/// All six mutually-exclusive panes under `#main-pane` - `showPaneFor`
/// activates exactly one (or, for a `VerbTab` in history mode, two: the
/// verb-history pane replaces the plain editor pane, everything else stays
/// hidden the same way).
let private allPanes =
    [ terminalPaneEl; editorPaneEl; verbHistoryPaneEl; inspectorPaneEl; historyPaneEl ]

let private activateOnly (paneEl: HTMLElement) : unit =
    for p in allPanes do
        if p = paneEl then p.classList.add "active" else p.classList.remove "active"

/// Shows whichever pane `tab` needs and hides the rest; focuses that pane's
/// primary input.
let private showPaneFor (tab: OpenTab) : unit =
    match tab with
    | GameTab ->
        activateOnly terminalPaneEl
        inputEl.focus ()
    | VerbTab _ when showingVerbHistory -> activateOnly verbHistoryPaneEl
    | VerbTab _ ->
        activateOnly editorPaneEl
        // The container was `display:none` a moment ago - force Monaco to
        // re-measure rather than rely on ResizeObserver picking this up.
        editor.layout ()
        editor.focus ()
    | InspectorTab _ -> activateOnly inspectorPaneEl
    | HistoryTab -> activateOnly historyPaneEl

/// The historical code currently shown in the verb-history diff view's
/// "original" side - what "Restore this version" writes into the live
/// editor once clicked. `None` until a commit has actually been picked.
let mutable private currentHistoricalCode: string option = None

let mutable private historyDiffEditor: Monaco.IDiffEditor option = None

/// Created lazily on first use rather than up front - most verb tabs never
/// open their history view, so there's no reason to pay for a second Monaco
/// instance until one actually does.
let private getOrCreateHistoryDiffEditor () : Monaco.IDiffEditor =
    match historyDiffEditor with
    | Some e -> e
    | None ->
        let e = Monaco.createDiffEditor verbHistoryDiffEditorEl
        historyDiffEditor <- Some e
        e

/// Renders the verb-history pane's commit list - each entry, clicked,
/// fetches that commit's code (`verb-at-commit`) and diffs it against
/// whatever the live editor currently holds, not necessarily the last-saved
/// version - comparing against in-progress unsaved edits is useful too.
let private renderVerbHistoryList (objRef: int64) (verbName: string) (entries: (string * int64 * string) list) : unit =
    verbHistoryListEl.innerHTML <- ""

    if entries.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No history yet."
        verbHistoryListEl.appendChild li |> ignore
    else
        for sha, whenEpochSeconds, message in entries do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            let date = System.DateTimeOffset.FromUnixTimeSeconds(whenEpochSeconds).LocalDateTime
            li.textContent <- sprintf "%s  %s" (date.ToString("yyyy-MM-dd HH:mm")) message

            li.onclick <-
                fun _ ->
                    verbHistoryRestoreBtn.setAttribute ("style", "display:none")
                    currentHistoricalCode <- None
                    sendAction [ "action" ==> "verb-at-commit"; "obj" ==> int objRef; "verb" ==> verbName; "sha" ==> sha ]

            verbHistoryListEl.appendChild li |> ignore

// Loads the picked historical version straight into the live editor - not
// a new server action, just `editor.setValue()` - the existing
// `onDidChangeModelContent`/`setDirty true` and blur-triggered
// `saveIfDirty` autosave machinery takes it from there exactly like a
// manual edit, so "restore" is really "load old content, then save
// normally".
verbHistoryRestoreBtn.onclick <-
    fun _ ->
        match currentHistoricalCode with
        | Some code ->
            editor.setValue code
            showingVerbHistory <- false
            showPaneFor activeTab
        | None -> ()

/// Snapshots whatever's currently in the editor into `tabContent`, if the
/// active tab is a verb - called right before navigating away from it.
let private cacheCurrentEditorContent () : unit =
    match activeTab with
    | VerbTab(o, v) -> tabContent <- Map.add (o, v) (editor.getValue ()) tabContent
    | GameTab
    | InspectorTab _
    | HistoryTab -> ()

/// Pulls the value following `marker` out of an mcp header line, up to the
/// next space - used for short fixed-shape fields like "ref:" and "ok:".
/// The `text:` field on continuation lines is handled separately by the
/// Sidecar itself (McpFilter), not here.
let private headerField (marker: string) (header: string) : string option =
    let idx = header.IndexOf(marker: string)
    if idx < 0 then
        None
    else
        let rest = header.Substring(idx + marker.Length)
        let spaceIdx = rest.IndexOf(' ')
        Some(if spaceIdx < 0 then rest else rest.Substring(0, spaceIdx))

let private isMcpMessage (data: obj) : bool = emitJsExpr data "typeof $0 === 'string'"

/// Parses a "Line N:  message" compile-error string (set_verb_code()'s own
/// format) into (line, message). Errors that don't match this shape (should
/// not happen in practice, but not asserted) are just skipped for markers -
/// they still show in the plain-text diagnostics area either way.
let private parseErrorLine (line: string) : (int * string) option =
    if line.StartsWith("Line ") then
        let colonIdx = line.IndexOf(':')

        if colonIdx > 5 then
            match System.Int32.TryParse(line.Substring(5, colonIdx - 5)) with
            | true, lineNum -> Some(lineNum, line.Substring(colonIdx + 1).TrimStart())
            | false, _ -> None
        else
            None
    else
        None

/// Case-insensitive substring match - an empty filter matches everything.
let private matchesFilter (filterText: string) (label: string) : bool =
    filterText = "" || label.ToLowerInvariant().Contains(filterText.ToLowerInvariant())

/// `dobj`/`iobj` are always exactly one of these three (confirmed against
/// `toaststunt/src/verbs.cc`'s `unparse_arg_spec` - `ASPEC_NONE`/`ASPEC_ANY`/
/// `ASPEC_THIS`, nothing else), so the `'?'` fallback should never actually
/// show up in practice.
let private argSpecChar (spec: string) : char =
    match spec with
    | "this" -> 't'
    | "any" -> 'a'
    | "none" -> 'n'
    | _ -> '?'

/// `prep` also has `none`/`any`, but otherwise resolves to one of the
/// server's 15 fixed preposition groups (`toaststunt/src/db_verbs.cc`'s
/// `db_unparse_prep`) - collapsed to a generic `*` here rather than picking
/// an arbitrary single letter for 15 different values. The Inspector's own
/// Prep column still shows the real preposition.
let private prepChar (prep: string) : char =
    match prep with
    | "none" -> 'n'
    | "any" -> 'a'
    | _ -> '*'

/// The tree's compact 3-char dobj/prep/iobj summary, e.g. `tnt` for
/// `this none this`, `n*n` for `none <some preposition> none`.
let private verbArgsCode (v: LspClient.TreeVerb) : string =
    sprintf "%c%c%c" (argSpecChar v.Dobj) (prepChar v.Prep) (argSpecChar v.Iobj)

/// The tree filter box's search scope - unrestricted by default, or
/// narrowed to just object names/numbers, just verb names, or just property
/// names via an "obj:"/"verb:"/"prop:" prefix (see `parseFilter`). Lets a
/// search like "look" find only the verb, not also every object whose
/// description happens to contain "look". Unlike verbs, properties never
/// participate in the unprefixed `AnyKind` search - only "prop:" ever
/// searches them - so turning this on can't change what an existing plain
/// search already finds.
type private FilterKind =
    | AnyKind
    | ObjectOnly
    | VerbOnly
    | PropertyOnly

type private ParsedFilter = { Kind: FilterKind; Text: string }

/// Splits the raw filter box text into a scope + the actual search text -
/// "obj:foo"/"verb:foo"/"prop:foo" (prefix case-insensitive, whitespace
/// around the colon tolerated) restrict the scope; anything else searches
/// objects and verbs both.
let private parseFilter (rawText: string) : ParsedFilter =
    let trimmed = rawText.Trim()
    let lower = trimmed.ToLowerInvariant()

    if lower.StartsWith("obj:") then
        { Kind = ObjectOnly; Text = trimmed.Substring(4).Trim() }
    elif lower.StartsWith("verb:") then
        { Kind = VerbOnly; Text = trimmed.Substring(5).Trim() }
    elif lower.StartsWith("prop:") then
        { Kind = PropertyOnly; Text = trimmed.Substring(5).Trim() }
    else
        { Kind = AnyKind; Text = trimmed }

/// One in-memory node per object, built once from `LspClient.getObjectTreeAsync`'s
/// flat response at login - keyed by objRef (`treeNodes`) so parent/child
/// lookups don't re-scan the array. `Verbs`/`Properties` are this object's
/// own verbs/properties only (already filtered server-side), in the
/// server's declaration order - never re-fetched per click, unlike the old
/// per-selection `listVerbsAsync` round-trip.
type private TreeNode =
    { ObjRef: int64
      Name: string
      Parents: int64[]
      Children: int64[]
      Verbs: LspClient.TreeVerb[]
      Properties: LspClient.TreeProperty[] }

let mutable private treeNodes: Map<int64, TreeNode> = Map.empty

/// True roots of the object tree - objects with zero parents (`$root_class`
/// and a handful of others, confirmed against the real corpus rather than
/// assumed: `parents(obj)` already returns `{}` for a parentless object,
/// no sentinel ref filtering needed).
let mutable private rootRefs: int64[] = [||]

let private buildTree
    (nodes: (int64 * string * int64[] * int64[] * LspClient.TreeVerb[] * LspClient.TreeProperty[])[])
    : unit =
    treeNodes <-
        nodes
        |> Array.map (fun (objRef, name, parents, children, verbs, properties) ->
            objRef,
            { ObjRef = objRef
              Name = name
              Parents = parents
              Children = children
              Verbs = verbs
              Properties = properties })
        |> Map.ofArray

    rootRefs <-
        nodes
        |> Array.filter (fun (_, _, parents, _, _, _) -> Array.isEmpty parents)
        |> Array.map (fun (objRef, _, _, _, _, _) -> objRef)

/// Folds a `get-live-children` response into `treeNodes` - the mechanism
/// that lets a live (uncorponym'd, per moo-vcs-plan.md I3) object appear in
/// the tree exactly like a statically-preloaded one, with zero rendering
/// changes anywhere else: every field here is typed identically to a
/// preloaded `TreeNode`, so `flattenVisibleRows`/`renderTreeRows` can't tell
/// how an entry got into the map. A child already present in `treeNodes`
/// (a corponym'd child the static preload already covered) is left
/// untouched - its own `Children` may carry real static data that must not
/// be clobbered by this partial, one-level-deep query. `parentRef`'s own
/// `Children` is *replaced*, not unioned, with the live-authoritative list
/// just returned - simpler than tracking removals separately, and it
/// self-heals a recycled/destroyed child for free (it just stops appearing
/// next time the parent re-expands).
let private mergeLiveChildren
    (parentRef: int64)
    (children: (int64 * string * int64[] * LspClient.TreeVerb[] * LspClient.TreeProperty[])[])
    : unit =
    for objRef, name, parents, verbs, properties in children do
        if not (Map.containsKey objRef treeNodes) then
            treeNodes <-
                Map.add
                    objRef
                    { ObjRef = objRef
                      Name = name
                      Parents = parents
                      Children = [||]
                      Verbs = verbs
                      Properties = properties }
                    treeNodes

    match Map.tryFind parentRef treeNodes with
    | None -> ()
    | Some parentNode ->
        treeNodes <- Map.add parentRef { parentNode with Children = children |> Array.map (fun (r, _, _, _, _) -> r) } treeNodes

/// The removal-side counterpart to `mergeLiveChildren` above, for a recycled
/// object: drops it from `treeNodes` entirely and scrubs it out of every
/// remaining node's `Children` list (it may appear under more than one
/// parent, same DAG reasoning as `ancestorsOf`), rather than waiting for a
/// stale entry to self-heal on that parent's next expand.
let private removeLiveNode (objRef: int64) : unit =
    treeNodes <-
        treeNodes
        |> Map.remove objRef
        |> Map.map (fun _ node -> { node with Children = node.Children |> Array.filter ((<>) objRef) })

/// The tree's own `Verbs`/`Properties` arrays (static preload, or the
/// live-children fetch) are a separate, independently-cached copy from what
/// the inspector shows - a successful `delete-verb`/`delete-property`
/// refreshes the inspector fine via `loadInspector`'s own fresh round-trip,
/// but leaves the tree's cached copy stale (confirmed live: the deleted
/// verb/property kept showing under the object's tree row until a full
/// reload). `verbName`/`propName` here is the same identifier string
/// `openOrSwitchToVerb`/the inspector's own row both already use, so it
/// matches `TreeVerb.Name`/`TreeProperty.Name` directly with no extra
/// resolution.
let private removeTreeVerb (objRef: int64) (verbName: string) : unit =
    match Map.tryFind objRef treeNodes with
    | None -> ()
    | Some node -> treeNodes <- Map.add objRef { node with Verbs = node.Verbs |> Array.filter (fun v -> v.Name <> verbName) } treeNodes

let private removeTreeProperty (objRef: int64) (propName: string) : unit =
    match Map.tryFind objRef treeNodes with
    | None -> ()
    | Some node -> treeNodes <- Map.add objRef { node with Properties = node.Properties |> Array.filter (fun p -> p.Name <> propName) } treeNodes

/// Which object nodes are expanded, by objRef - a `Set`, not per-occurrence:
/// expanding #7 once should reveal its children under *every* parent it
/// appears under (the object graph is a DAG - see the project plan's
/// "Known hazards"), not just the occurrence that was clicked, since expand
/// state belongs to the object, not to one place it happens to be reachable
/// from. Reset on every fresh login/tree rebuild, never persisted across
/// reloads - unlike the font-size/word-wrap settings (stable preferences),
/// which nodes are expanded is transient exploration state, and the
/// filter's auto-expand (below) already covers "reveal what I'm looking
/// for" on demand.
let mutable private expandedRefs: Set<int64> = Set.empty

/// Which objects have their "Verbs" virtual group node expanded, by objRef.
/// Separate from `expandedRefs` - expanding an object reveals its verb group
/// and child objects, but the verbs themselves stay collapsed behind that
/// group node until it's opened too, so a deep chain of objects can be
/// walked without wading through every intermediate object's verb list.
let mutable private expandedVerbGroups: Set<int64> = Set.empty

/// Same idea as `expandedVerbGroups`, for the "Properties" virtual group.
let mutable private expandedPropGroups: Set<int64> = Set.empty

/// Same idea as `expandedVerbGroups`, for the "Children" virtual group -
/// child *objects* now live behind this gate too, not directly under their
/// parent's own row, so revealing a deeply-nested object needs every
/// ancestor's children group opened, not just its own row.
let mutable private expandedChildGroups: Set<int64> = Set.empty

/// Objects whose live children have been asked for at least once (a
/// `get-live-children` round trip has landed, whether or not it turned up
/// anything new) - lets `isExpandable` show a chevron before the first ask
/// (an object whose *only* children are live-only would otherwise show none
/// at all, hiding the exact case this feature exists to reveal) while still
/// self-correcting to "no chevron" afterward if nothing real ever surfaces.
/// Same reset lifecycle as `expandedRefs`.
let mutable private liveChildrenChecked: Set<int64> = Set.empty

/// The object row the user actually clicked (or opened the inspector for)
/// while a filter was active - the one thing `promoteFilterExpansionIfAny`
/// keeps in view once the filter clears. Deliberately *not* "every object
/// the filter matched" - that was tried first and was wrong: a search like
/// "verb:notify" matches dozens of objects, and clearing used to leave every
/// one of their ancestor chains expanded instead of just the one actually
/// being looked at.
let mutable private lastFilterSelectedObjRef: int64 option = None

/// Every ancestor of `objRef`, walking `Parents` upward, recursively - a
/// DAG node can have more than one parent path to a root, so this returns
/// every one of them, not just one. `visited` is a defensive cycle guard
/// (the graph shouldn't have cycles, but a hand-edited `metadata.json`
/// could introduce one - without this, that would hang the tab). Shared by
/// both the filter's auto-expand and go-to-definition's reveal.
let rec private ancestorsOf (visited: Set<int64>) (objRef: int64) : Set<int64> =
    if Set.contains objRef visited then
        Set.empty
    else
        let visited = Set.add objRef visited

        match Map.tryFind objRef treeNodes with
        | None -> Set.empty
        | Some node -> node.Parents |> Array.fold (fun acc p -> Set.add p acc |> Set.union (ancestorsOf visited p)) Set.empty

/// Reveals `lastFilterSelectedObjRef` (if anything was selected while
/// filtering) by merging its own ancestor path into the persistent
/// `expandedRefs`/`expandedChildGroups` - the same two sets a plain click on
/// an already-visible object would touch, just computed for the whole path
/// at once instead of one click per level. A no-op if nothing was selected
/// (e.g. the user typed a search and cleared it without ever clicking a
/// result) - there's nothing to preserve in that case, which is the point:
/// only an explicit selection survives the clear, not every match.
let private promoteFilterExpansionIfAny () : unit =
    match lastFilterSelectedObjRef with
    | None -> ()
    | Some objRef ->
        let path = Set.add objRef (ancestorsOf Set.empty objRef)
        expandedRefs <- Set.union expandedRefs path
        expandedChildGroups <- Set.union expandedChildGroups path

/// Live filter text, updated on every keystroke in the tree's filter box -
/// see the `oninput` wiring below.
let mutable private treeFilterText = ""

/// One row of the flattened, currently-visible tree. `VerbGroupRow`/
/// `PropGroupRow`/`ChildGroupRow` are virtual nodes - not real MOO objects,
/// verbs, or properties - representing an object's collapsible
/// "Verbs"/"Properties"/"Children" buckets, so each can be hidden
/// independently of the others. `ChildGroupRow`'s contents are just more
/// `ObjectRow`s (one depth deeper), unlike the flat `PropRow`/`VerbRow`
/// leaves - a child is a full recursively-expandable node, not a leaf.
type private TreeRow =
    | ObjectRow of objRef: int64 * depth: int * isExpandable: bool
    | PropGroupRow of objRef: int64 * depth: int * count: int
    | PropRow of objRef: int64 * prop: LspClient.TreeProperty * depth: int
    | VerbGroupRow of objRef: int64 * depth: int * count: int
    | VerbRow of objRef: int64 * verb: LspClient.TreeVerb * depth: int
    | ChildGroupRow of objRef: int64 * depth: int * count: int

/// Switches the main area to `tab`, caching whatever was showing before the
/// switch. A no-op if `tab` is already active (e.g. clicking the tab you're
/// already on).
let rec private switchToTab (tab: OpenTab) : unit =
    if tab <> activeTab then
        cacheCurrentEditorContent ()
        activeTab <- tab
        showingVerbHistory <- false

        match tab with
        | GameTab
        | InspectorTab _
        | HistoryTab -> ()
        | VerbTab(o, v) ->
            editor.setValue (Map.find (o, v) tabContent)
            // setValue above just re-fired onDidChangeModelContent - this
            // is a tab switch, not a user edit.
            setDirty false

        showPaneFor tab
        renderTabs ()
        renderTree ()

/// Opens `(objRef, verbName)` - switches instantly from the client-side
/// cache if it's already an open tab, otherwise fetches it from the server
/// (the `moodev-edit-content` handler below adds it to `openVerbTabs` and
/// switches to it once the content arrives). Used by the tree's verb-row
/// click handler and by go-to-definition (via `revealAndOpenVerb`) - both
/// funnel every verb-open through here so "already open" is checked in
/// exactly one place.
and private openOrSwitchToVerb (objRef: int64) (verbName: string) : unit =
    if Map.containsKey (objRef, verbName) tabContent then
        switchToTab (VerbTab(objRef, verbName))
    else
        fetchVerb objRef verbName

/// Closes an open verb tab. If it was the active one, falls back to the
/// tab that was to its left (or the new first tab, or Game if none remain).
/// See `saveIfDirty`'s comment for why this never risks losing unsaved
/// edits without a confirmation prompt.
and private closeTab (objRef: int64, verbName: string) : unit =
    let wasActive = activeTab = VerbTab(objRef, verbName)
    let idx = openVerbTabs |> List.findIndex (fun t -> t = (objRef, verbName))
    openVerbTabs <- openVerbTabs |> List.filter (fun t -> t <> (objRef, verbName))
    tabContent <- Map.remove (objRef, verbName) tabContent
    if previewTab = Some(objRef, verbName) then previewTab <- None

    if wasActive then
        activeTab <-
            match openVerbTabs with
            | [] -> GameTab
            | tabs -> VerbTab tabs.[max 0 (min (idx - 1) (tabs.Length - 1))]

        showingVerbHistory <- false

        match activeTab with
        | VerbTab(o, v) ->
            editor.setValue (Map.find (o, v) tabContent)
            setDirty false
        | GameTab
        | InspectorTab _
        | HistoryTab -> ()

        showPaneFor activeTab

    renderTabs ()
    renderTree ()

/// Closes an open inspector tab. If it was the active one, falls back the
/// same way `closeTab` does for verb tabs (the tab to its left, or the new
/// first tab, or Game if none remain) - and, per `loadInspector`'s "always
/// fresh" rule, re-loads whichever inspector tab it falls back to rather
/// than showing whatever that tab last happened to render.
and private closeInspectorTab (objRef: int64) : unit =
    let wasActive = activeTab = InspectorTab objRef
    let idx = openInspectorTabs |> List.findIndex (fun r -> r = objRef)
    openInspectorTabs <- openInspectorTabs |> List.filter (fun r -> r <> objRef)
    if previewInspectorTab = Some objRef then previewInspectorTab <- None

    // The object's tree row stays "selected" independently of `activeTab`
    // (see `selectedObjRef`'s own comment) - but with its inspector gone,
    // there's nothing left for that selection to point at, so it shouldn't
    // outlive the tab that justified it.
    if selectedObjRef = Some objRef then
        selectedObjRef <- None

    if wasActive then
        activeTab <-
            match openInspectorTabs with
            | [] -> GameTab
            | refs -> InspectorTab refs.[max 0 (min (idx - 1) (refs.Length - 1))]

        showPaneFor activeTab

        match activeTab with
        | InspectorTab o -> loadInspector o None
        | GameTab
        | VerbTab _
        | HistoryTab -> ()

    renderTabs ()
    renderTree ()

/// Opens `objRef`'s inspector - switches instantly if it's already an open
/// tab (adding it first if not), then *always* kicks off a fresh load
/// (structural info + live property values), even when the tab was already
/// open and already active. Used by the tab strip itself, the sidebar
/// objects list's "ⓘ" icon, and every clickable owner/parent/child link
/// inside the inspector pane - all funnel through here (via
/// `openOrSwitchToInspector`/`revealPropertyInInspector` below) so
/// "already open" and "always fresh" are each handled in exactly one place.
/// `highlightProp`, when `Some`, is forwarded to `loadInspector` to scroll
/// to and flash that property's row once the table renders - used by the
/// tree's `PropRow` click, so landing on the inspector also lands on the
/// specific property clicked, not just the object.
and private openOrSwitchToInspectorWith (objRef: int64) (highlightProp: string option) : unit =
    if not (openInspectorTabs |> List.contains objRef) then
        // Same preview-tab replacement `moodev-edit-content` does for verb
        // tabs (see `previewTab`'s own comment) - replace the current
        // preview inspector tab in place if there is one, otherwise append.
        match previewInspectorTab with
        | Some oldPreview ->
            let idx = openInspectorTabs |> List.findIndex (fun r -> r = oldPreview)
            openInspectorTabs <- openInspectorTabs |> List.mapi (fun i r -> if i = idx then objRef else r)
        | None -> openInspectorTabs <- openInspectorTabs @ [ objRef ]

        previewInspectorTab <- Some objRef

    activeInspectorProp <- highlightProp |> Option.map (fun p -> (objRef, p))
    switchToTab (InspectorTab objRef)
    // `switchToTab` only redraws the sidebar tree when the tab itself
    // changes - clicking a different property of an *already*-open
    // inspector doesn't, so without this the tree's highlight would stay on
    // whichever property was selected last instead of following the new
    // click.
    renderTree ()
    loadInspector objRef highlightProp

/// `openOrSwitchToInspectorWith objRef None` - every existing call site
/// (the "ⓘ" button, owner/parent/child links, the tab strip) goes through
/// this, unchanged.
and private openOrSwitchToInspector (objRef: int64) : unit = openOrSwitchToInspectorWith objRef None

/// `openOrSwitchToInspectorWith objRef (Some propName)` - used by the
/// tree's `PropRow` click to land directly on that property's row, not
/// just the object's inspector in general.
and private revealPropertyInInspector (objRef: int64) (propName: string) : unit =
    openOrSwitchToInspectorWith objRef (Some propName)

/// Fetches and renders `objRef`'s inspector content: structural data
/// (`moodev/getObjectInfo`, over the LSP websocket - cheap, the graph is
/// already in memory server-side) and live property values
/// (`$vcs:ide_get_properties`, over the main MOO websocket - a real
/// round-trip). Deliberately not cached client-side, unlike verb tabs:
/// property values are live, mutable game state, not something this editor
/// owns a stable copy of the way verb source is (nothing else can change a
/// verb's source out from under the editor; plenty can change a property's
/// value out from under the inspector) - so every activation re-fetches
/// both, fresh. `highlightProp`, when `Some`, is forwarded to
/// `renderInspectorStructure` to scroll to and flash that property's row.
and private loadInspector (objRef: int64) (highlightProp: string option) : unit =
    inspectorDiagnosticsEl.textContent <- ""
    inspectorContentEl.textContent <- "Loading..."

    // Always live - matches the "live governs, no export needed" rule
    // already applied to hover/go-to-definition/builtins.
    sendAction [ "action" ==> "get-live-info"; "obj" ==> int objRef ]

    sendAction [ "action" ==> "get-properties"; "obj" ==> int objRef ]

/// A "type anything, or click a quick-fill button" widget - the shared shape
/// behind every owner picker (You/This object -> player/#N) and the verb
/// Prep picker (none/any -> literal keywords). `compact` narrows it to fit
/// its content (for a standalone context like the header) instead of
/// stretching to 100% width (the right behavior inside a table cell, where
/// the column width already constrains it).
and private mkQuickFillInput
    (placeholder: string)
    (initialValue: string)
    (quickFills: (string * string) list)
    (compact: bool)
    : HTMLElement * HTMLInputElement =
    let group = document.createElement ("span")
    group.classList.add "inspector-owner-edit-group"
    if compact then group.classList.add "inspector-owner-edit-group-compact"

    let input = document.createElement ("input") :?> HTMLInputElement
    input.classList.add "inspector-property-value"
    input.placeholder <- placeholder
    input.value <- initialValue
    group.appendChild input |> ignore

    for label, value in quickFills do
        let btn = document.createElement ("button")
        btn.classList.add "inspector-owner-quick-btn"
        btn.textContent <- label
        btn.onclick <- fun _ -> input.value <- value
        group.appendChild btn |> ignore

    group, input

/// A small "+" trigger that reveals an already-built (currently hidden)
/// element on click, then hides itself - the same reveal-on-click
/// convention the header's owner/rename pencils use. `target` should
/// already be `display:none`; this only wires the toggle, it doesn't set
/// the initial hidden state (callers do that themselves, since some also
/// need to seed default field values first).
and private mkAddTrigger (label: string) (target: HTMLElement) : HTMLElement =
    let triggerBtn = document.createElement ("button")
    triggerBtn.classList.add "inspector-add-property-btn"
    triggerBtn.textContent <- "+"
    triggerBtn.title <- label

    triggerBtn.onclick <-
        fun _ ->
            triggerBtn.setAttribute ("style", "display:none")
            target.setAttribute ("style", "")

    triggerBtn

/// Renders a titled list of clickable object links into `container` - shared
/// by the inspector pane's Parents/Children sections. Each entry opens that
/// object's own inspector on click. `onAdd`, when `Some (singular label,
/// callback)`, puts a "+" trigger inline with the section title (e.g.
/// "Parents (2) [+]") - clicking it reveals an add-field appended as the
/// list's own last item (a real new line in the same list, not a separate
/// control floating below it), matching "new line after the last existing
/// item, or first if none" for the empty case too, since it's simply the
/// last child of a container that otherwise only holds existing items.
and private renderObjRefList
    (container: HTMLElement)
    (title: string)
    (refs: (int64 * string) list)
    (onRemove: (int64 -> unit) option)
    (onAdd: (string * (string -> unit)) option)
    : unit =
    let titleRow = document.createElement ("div")
    titleRow.classList.add "inspector-section-title-row"

    let titleEl = document.createElement ("div")
    titleEl.classList.add "inspector-section-title"
    titleEl.textContent <- sprintf "%s (%d)" title refs.Length
    titleRow.appendChild titleEl |> ignore

    let section = document.createElement ("div")
    section.appendChild titleRow |> ignore

    let list = document.createElement ("div")
    list.classList.add "inspector-refs"

    for refObj, name in refs do
        let item = document.createElement ("span")
        item.classList.add "inspector-ref-item"

        let link = document.createElement ("span")
        link.classList.add "inspector-link"
        link.textContent <- name
        link.onclick <- fun _ -> openOrSwitchToInspector refObj
        item.appendChild link |> ignore

        match onRemove with
        | Some remove ->
            let removeBtn = document.createElement ("button")
            removeBtn.classList.add "inspector-row-delete-btn"
            removeBtn.textContent <- "🗑"
            removeBtn.title <- sprintf "Remove %s as a parent" name
            removeBtn.onclick <- fun _ -> remove refObj
            item.appendChild removeBtn |> ignore
        | None -> ()

        list.appendChild item |> ignore

    match onAdd with
    | Some(label, addFn) ->
        let addItem = document.createElement ("span")
        addItem.classList.add "inspector-add-parent"
        addItem.setAttribute ("style", "display:none")

        let addInput = document.createElement ("input") :?> HTMLInputElement
        addInput.classList.add "inspector-property-value"
        addInput.placeholder <- "#5, $room, ... (MOO expr)"

        let addBtn = document.createElement ("button")
        addBtn.classList.add "inspector-add-property-btn"
        addBtn.textContent <- "+"
        addBtn.title <- sprintf "Add %s" label

        addBtn.onclick <-
            fun _ ->
                let expr = addInput.value.Trim()
                if expr <> "" then addFn expr

        addItem.appendChild addInput |> ignore
        addItem.appendChild addBtn |> ignore
        list.appendChild addItem |> ignore

        titleRow.appendChild (mkAddTrigger (sprintf "Add %s" label) addItem) |> ignore
    | None -> ()

    section.appendChild list |> ignore
    container.appendChild section |> ignore

/// Builds the inspector pane's DOM from a `moodev/getObjectInfo` result:
/// header, a clickable owner link, permission-flag badges, clickable
/// parents/children lists, a read-only verbs table, and a properties table
/// whose value cells are editable `<input>`s (seeded blank here - filled in
/// once `ide_get_properties`'s response arrives, matched up by property
/// name via `inspectorPropertyInputs`). Kept as loosely-typed `obj` (dynamic
/// `?` field access), matching this file's existing style for
/// `getObjectTreeAsync`'s results rather than introducing heavier typed
/// modeling for this one screen. `highlightProp`, when `Some`, scrolls to
/// and briefly flashes that property's row once the table is actually in
/// the document - no cleanup needed for the flash, since this function
/// throws the whole pane away and rebuilds it fresh on every call anyway.
and private renderInspectorStructure (objRef: int64) (info: obj) (highlightProp: string option) : unit =
    inspectorContentEl.innerHTML <- ""
    inspectorPropertyInputs <- Map.empty
    inspectorPropertyLastValues <- Map.empty
    inspectorPropertyPreviews <- Map.empty

    // Whoever is connected on *this* session - shown in the "You" button's
    // own label (e.g. "You (Wizard (#3))") and used as its actual quick-fill
    // value (a real resolved objref, not the bare "player" expression - it
    // used to send that literal keyword, but a resolved ref is what was
    // asked for).
    let connectedPlayerDisplay: obj = info?connectedPlayerDisplay

    let connectedPlayerRef: int64 option =
        let raw: obj = info?connectedPlayerRef
        if isNullOrUndefined raw then None else Some(int64 (unbox<float> raw))

    let youLabel =
        if isNullOrUndefined connectedPlayerDisplay then
            "You"
        else
            sprintf "You (%s)" (unbox<string> connectedPlayerDisplay)

    // Shared by every owner picker in this pane (property-add, verb-add,
    // header owner-edit) - "This object" is only offered when it wouldn't
    // just duplicate "You" (i.e. the connected player isn't already the
    // object being edited).
    let ownerQuickFills: (string * string) list =
        let youValue = connectedPlayerRef |> Option.map (sprintf "#%d") |> Option.defaultValue "player"

        if connectedPlayerRef = Some objRef then
            [ youLabel, youValue ]
        else
            [ youLabel, youValue; "This object", sprintf "#%d" objRef ]

    let header = document.createElement ("div")
    header.classList.add "inspector-header"

    let headerName = document.createElement ("span")
    headerName.textContent <- (info?name: string)
    header.appendChild headerName |> ignore

    // Renaming follows the exact same pencil-reveal pattern as the owner
    // edit below - `.name = ` is dot-assignable the same way `.owner` is
    // (confirmed against `ToastStunt/src/execute.cc`'s `OP_PUT_PROP`), and
    // the sidecar's connection is always a wizard, so this is never
    // actually permission-blocked.
    let renameBtn = document.createElement ("button")
    renameBtn.classList.add "inspector-owner-edit-btn"
    renameBtn.textContent <- "✎"
    renameBtn.title <- "Rename object"

    let renameGroup, renameInput = mkQuickFillInput "new name" (info?rawName: string) [] true
    renameGroup.setAttribute ("style", "display:none")

    let renameConfirmBtn = document.createElement ("button")
    renameConfirmBtn.classList.add "inspector-add-property-btn"
    renameConfirmBtn.textContent <- "✓"
    renameConfirmBtn.title <- "Confirm"

    renameConfirmBtn.onclick <-
        fun _ ->
            let newName = renameInput.value.Trim()
            if newName <> "" then
                sendAction [ "action" ==> "rename-object"; "obj" ==> int objRef; "name" ==> newName ]

    renameGroup.appendChild renameConfirmBtn |> ignore
    renameBtn.onclick <- fun _ -> renameGroup.setAttribute ("style", "")

    header.appendChild renameBtn |> ignore
    header.appendChild renameGroup |> ignore

    // Recycling is irreversible (the object's data is gone, and its number
    // gets reused later) - unlike every other mutation in this pane, this
    // one gets a confirmation dialog first, naming the object and warning
    // about any children. `recycle()` moves *contents* (`.location`)
    // elsewhere via an optional `obj:recycle()` hook, and - confirmed
    // against `ToastStunt/src/objects.cc`'s `bf_recycle` and live-verified
    // against this fork - also walks the inheritance hierarchy, reparenting
    // every child onto the recycled object's own parent(s) rather than
    // leaving them with an invalid `parent()`. Still worth flagging: a
    // child silently jumping up a level in the hierarchy can be just as
    // surprising as losing it outright.
    let recycleBtn = document.createElement ("button")
    recycleBtn.classList.add "inspector-recycle-btn"
    recycleBtn.textContent <- "🗑"
    recycleBtn.title <- "Recycle object"

    recycleBtn.onclick <-
        fun _ ->
            let childCount: int = (unbox info?children: obj[]).Length
            let name: string = info?name

            let warning =
                if childCount > 0 then
                    sprintf
                        "Recycle %s? This object has %d child object(s), which will be reparented onto its own parent. This cannot be undone."
                        name
                        childCount
                else
                    sprintf "Recycle %s? This cannot be undone." name

            if window.confirm warning then
                sendAction [ "action" ==> "recycle-object"; "obj" ==> int objRef ]

    header.appendChild recycleBtn |> ignore
    inspectorContentEl.appendChild header |> ignore

    let ownerRow = document.createElement ("div")
    ownerRow.classList.add "inspector-owner"
    ownerRow.appendChild (document.createTextNode "Owner: ") |> ignore

    let ownerVal: obj = info?owner

    if isNullOrUndefined ownerVal then
        ownerRow.appendChild (document.createTextNode "?") |> ignore
    else
        // `?objRef` here is a value freshly parsed from the LSP's JSON
        // response - a plain JS number, not Fable's actual `int64` (a native
        // `BigInt`, compared via `===` in `openInspectorTabs`' `List.contains`).
        // Left as a bare dynamic cast, this silently fails to match against
        // entries added via the sidebar's `int64 (value.TrimStart '#')`
        // round-trip (a genuine `BigInt`), producing a duplicate tab instead
        // of switching to the one already open - confirmed live. The
        // explicit `int64 (... : float)` conversion below forces a real
        // `BigInt`, matching the sidebar's path.
        let ownerRef: int64 = int64 (ownerVal?objRef: float)
        let link = document.createElement ("span")
        link.classList.add "inspector-link"
        link.textContent <- (ownerVal?name: string)
        link.onclick <- fun _ -> openOrSwitchToInspector ownerRef
        ownerRow.appendChild link |> ignore

        // Editing is opt-in via this pencil - the link above stays a plain
        // navigation click, same as it always has. `.owner = ` is
        // wizard-only unconditionally (confirmed against
        // `ToastStunt/src/execute.cc`'s `OP_PUT_PROP` handling of
        // `BP_OWNER`), and the sidecar's connection always is one, so this
        // is never actually blocked - failures here are user-input errors
        // (a bad expression), not permission errors.
        let editBtn = document.createElement ("button")
        editBtn.classList.add "inspector-owner-edit-btn"
        editBtn.textContent <- "✎"
        editBtn.title <- "Change owner"

        let editGroup, ownerEditInput =
            mkQuickFillInput "player, #5, or $room" (sprintf "#%d" ownerRef) ownerQuickFills true

        editGroup.setAttribute ("style", "display:none")

        let ownerConfirmBtn = document.createElement ("button")
        ownerConfirmBtn.classList.add "inspector-add-property-btn"
        ownerConfirmBtn.textContent <- "✓"
        ownerConfirmBtn.title <- "Confirm"

        ownerConfirmBtn.onclick <-
            fun _ ->
                let expr = ownerEditInput.value.Trim()
                if expr <> "" then
                    sendAction [ "action" ==> "set-owner"; "obj" ==> int objRef; "ownerExpr" ==> expr ]

        editGroup.appendChild ownerConfirmBtn |> ignore

        editBtn.onclick <- fun _ -> editGroup.setAttribute ("style", "")

        ownerRow.appendChild editBtn |> ignore
        ownerRow.appendChild editGroup |> ignore

    inspectorContentEl.appendChild ownerRow |> ignore

    // Only shown when the object actually has aliases - absent entirely for
    // one with none (also the case for a tree exported before this field
    // existed, per FORMAT.md's backwards-compat note on `aliases:`).
    let aliases: string[] = unbox info?aliases

    if aliases.Length > 0 then
        let aliasesRow = document.createElement ("div")
        aliasesRow.classList.add "inspector-owner"
        aliasesRow.appendChild (document.createTextNode "Aliases: ") |> ignore
        aliasesRow.appendChild (document.createTextNode (String.concat ", " aliases)) |> ignore
        inspectorContentEl.appendChild aliasesRow |> ignore

    let flagsRow = document.createElement ("div")
    flagsRow.classList.add "inspector-flags"

    let flags =
        [ "player", (info?player: bool)
          "programmer", (info?programmer: bool)
          "wizard", (info?wizard: bool)
          "r", (info?read: bool)
          "w", (info?write: bool)
          "f", (info?fertile: bool)
          "a", (info?anonymous: bool) ]

    for flagName, isSet in flags do
        // Immediate toggle-on-click, no separate confirm step - same
        // convention the property value inputs already use (autosave on
        // blur). `flagName` here is always one of these seven hardcoded
        // literals, never user-typed, so the sidecar's `setFlag` can splice
        // it directly into the generated MOO statement safely.
        let badge = document.createElement ("button")
        badge.classList.add "inspector-flag"
        if isSet then badge.classList.add "set"
        badge.textContent <- flagName
        badge.title <- sprintf "Click to %s %s" (if isSet then "clear" else "set") flagName

        badge.onclick <-
            fun _ ->
                sendAction
                    [ "action" ==> "set-flag"
                      "obj" ==> int objRef
                      "flag" ==> flagName
                      "value" ==> (if isSet then 0 else 1) ]

        flagsRow.appendChild badge |> ignore

    inspectorContentEl.appendChild flagsRow |> ignore

    // `?objRef` here is a value freshly parsed from the LSP's JSON response -
    // see the matching comment on `ownerRef` above, same fix, same reason.
    let toRefList (refs: obj[]) : (int64 * string) list =
        refs |> Array.map (fun r -> int64 (r?objRef: float), (r?name: string)) |> Array.toList

    renderObjRefList
        inspectorContentEl
        "Parents"
        (toRefList (unbox info?parents))
        (Some(fun parentRef -> sendAction [ "action" ==> "remove-parent"; "obj" ==> int objRef; "parent" ==> int parentRef ]))
        (Some("parent", fun expr -> sendAction [ "action" ==> "add-parent"; "obj" ==> int objRef; "parentExpr" ==> expr ]))

    // No per-item removal here - removing a child is already possible from
    // *that* child's own Parents section (removing this object from its
    // parent list), so it isn't duplicated on this side.
    renderObjRefList
        inspectorContentEl
        "Children"
        (toRefList (unbox info?children))
        None
        (Some("child", fun expr -> sendAction [ "action" ==> "add-child"; "obj" ==> int objRef; "childExpr" ==> expr ]))

    let propsSection = document.createElement ("div")
    let propsTitle = document.createElement ("div")
    propsTitle.classList.add "inspector-section-title"
    let props: obj[] = unbox info?properties
    propsTitle.textContent <- sprintf "Properties (%d)" props.Length

    let propsTable = document.createElement ("table")
    propsTable.classList.add "inspector-table"
    let propsHeaderRow = document.createElement ("tr")

    for h in [ "Name"; "Owner"; "Perms"; "Value"; "" ] do
        let th = document.createElement ("th")
        th.textContent <- h
        propsHeaderRow.appendChild th |> ignore

    propsTable.appendChild propsHeaderRow |> ignore

    let mutable highlightRow: HTMLElement option = None

    for p in props do
        let pname: string = p?name
        let tr = document.createElement ("tr")

        for cellText in [ p?name; p?owner; p?perms ] do
            let td = document.createElement ("td")
            td.textContent <- (cellText: string)
            tr.appendChild td |> ignore

        let valueTd = document.createElement ("td")
        let input = document.createElement ("input") :?> HTMLInputElement
        input.classList.add "inspector-property-value"
        input.value <- "" // filled in once ide_get_properties responds

        // Autosave-on-blur, mirroring the editor's own save-on-blur
        // (`saveIfDirty`) - only sends an update if the value actually
        // changed since it was last loaded/saved (see
        // `inspectorPropertyLastValues`'s own comment for why a direct
        // comparison is enough here, unlike Monaco's `isDirty` flag).
        // `input.value` is sent raw (not MOO-quoted client-side) - the
        // sidecar's `IdeActions.setProperty` does the quoting and `eval()`s
        // it server-side, so what the user types (`5`, `"hello"`, `{1, 2}`,
        // ...) is evaluated as a real MOO expression, the same UX
        // `$vcs:ide_set_property` used to provide.
        input.onblur <-
            fun _ ->
                let lastValue = inspectorPropertyLastValues |> Map.tryFind pname |> Option.defaultValue ""

                if input.value <> lastValue then
                    inspectorPropertyLastValues <- Map.add pname input.value inspectorPropertyLastValues

                    sendAction
                        [ "action" ==> "set-property"
                          "obj" ==> int objRef
                          "name" ==> pname
                          "valueExpr" ==> input.value ]

        // Read-only ANSI-code preview, filled in (only when the value
        // actually contains escape bytes) by the `moodev-prop-content`
        // handler below - stays empty, and so invisible via style.css's
        // `:empty` rule, for the overwhelming majority of properties.
        let preview = document.createElement ("div")
        preview.classList.add "inspector-property-ansi-preview"

        valueTd.appendChild input |> ignore
        valueTd.appendChild preview |> ignore
        tr.appendChild valueTd |> ignore

        // No confirmation - unlike recycling an object, a deleted property
        // is trivial to recreate by hand if this was a mistake.
        let deleteTd = document.createElement ("td")
        let deleteBtn = document.createElement ("button")
        deleteBtn.classList.add "inspector-row-delete-btn"
        deleteBtn.textContent <- "🗑"
        deleteBtn.title <- "Delete property"
        deleteBtn.onclick <- fun _ -> sendAction [ "action" ==> "delete-property"; "obj" ==> int objRef; "name" ==> pname ]
        deleteTd.appendChild deleteBtn |> ignore
        tr.appendChild deleteTd |> ignore

        propsTable.appendChild tr |> ignore
        inspectorPropertyInputs <- Map.add pname input inspectorPropertyInputs
        inspectorPropertyPreviews <- Map.add pname preview inspectorPropertyPreviews

        if highlightProp = Some pname then
            highlightRow <- Some tr

    // Nothing before this could create a *new* property at all - `set-property`
    // (the autosave-on-blur inputs above) only ever assigns to one that
    // already exists (`E_PROPNF` otherwise). This is a separate action
    // (`add-property`), reported back on its own wire header
    // (`moodev-prop-add-result`, handled below) so a successful add can
    // trigger a full inspector refresh (a new row now needs to exist),
    // unlike a plain value change. A real `<tr>` in the same table (not a
    // separate flex row below it) so its cells line up with the Name/
    // Owner/Perms/Value columns above - confirmed live this was
    // misaligned as a standalone row, since an unrelated flex container
    // has no way to match a `<table>`'s own column widths.
    let addPropRow = document.createElement ("tr")
    addPropRow.classList.add "inspector-add-property"

    let addNameInput = document.createElement ("input") :?> HTMLInputElement
    addNameInput.classList.add "inspector-property-value"
    addNameInput.placeholder <- "name"

    // Properties only ever have three permission bits - r/w/c (Read/Write/
    // Chown) - confirmed against `ToastStunt/src/property.cc`'s
    // `validate_prop_info`; verbs' x/d don't apply here. A dropdown of
    // checkboxes behind a toggle button, same popover pattern the sidebar's
    // "Tree display options" button already uses. Defined before the owner
    // widget below (even though it's appended after it) because the owner
    // widget's own visibility depends on `chownCb`'s state.
    let permsWidget = document.createElement ("div")
    permsWidget.classList.add "inspector-perms-widget"

    let permsToggleBtn = document.createElement ("button")
    permsToggleBtn.classList.add "pane-action-btn"
    permsToggleBtn.title <- "Permissions"

    let permsPopover = document.createElement ("div")
    permsPopover.classList.add "tree-filter-settings-popover"
    permsPopover.onclick <- fun ev -> ev.stopPropagation () |> ignore

    let mkPermCheckbox (label: string) (tooltip: string) (isChecked: bool) : HTMLInputElement =
        let row = document.createElement ("label")
        row.classList.add "settings-row"
        row.title <- tooltip

        let cb = document.createElement ("input") :?> HTMLInputElement
        cb.setAttribute ("type", "checkbox")
        cb.``checked`` <- isChecked

        row.appendChild cb |> ignore
        row.appendChild (document.createTextNode label) |> ignore
        permsPopover.appendChild row |> ignore
        cb

    let readCb = mkPermCheckbox "Read" "Other players' code can read this property's value." true
    let writeCb = mkPermCheckbox "Write" "Other players' code can set this property's value." false

    let chownCb =
        mkPermCheckbox
            "Chown"
            "This property's owner is force-locked to the object's own owner, overriding whatever owner you pick."
            true

    let currentPerms () : string =
        [ readCb, "r"; writeCb, "w"; chownCb, "c" ]
        |> List.filter (fun (cb, _) -> cb.``checked``)
        |> List.map snd
        |> String.concat ""

    let refreshPermsLabel () =
        let s = currentPerms ()
        permsToggleBtn.textContent <- (if s = "" then "(none)" else s)

    refreshPermsLabel ()

    permsToggleBtn.onclick <-
        fun ev ->
            ev.stopPropagation () |> ignore
            permsPopover.classList.toggle "visible" |> ignore

    permsWidget.appendChild permsToggleBtn |> ignore
    permsWidget.appendChild permsPopover |> ignore

    // Owner is any MOO expression resolving to a valid object - same
    // convention as the value input below, and as the "New Object"
    // popover's parent field - `player`/`#N` here just happen to be the two
    // most common cases, pre-offered as quick-fill buttons rather than a
    // separate input mode. BUT the Chown ('c') perm bit - confirmed live
    // and against `ToastStunt/src/db_properties.cc`'s `insert_prop2` -
    // unconditionally forces a property's owner to match the *object's*
    // own owner the instant it's created, discarding whatever owner was
    // requested. So while Chown is checked, offering a picker that
    // silently does nothing would be worse than not offering one at all -
    // show what the owner will actually end up being instead.
    let ownerWidget = document.createElement ("div")
    ownerWidget.classList.add "inspector-owner-widget"

    let ownerEditGroup, addOwnerInput =
        mkQuickFillInput "player, #5, or $room" "player" ownerQuickFills false

    // The object's own current owner - reuses `ownerVal`, already fetched
    // above for the header's "Owner:" row - as both the auto-label's text
    // and the actual `ownerExpr` sent when Chown is checked.
    let objectOwnerRef: int64 option =
        if isNullOrUndefined ownerVal then None else Some(int64 (ownerVal?objRef: float))

    let ownerAutoLabel = document.createElement ("span")
    ownerAutoLabel.classList.add "inspector-owner-auto-label"
    ownerAutoLabel.title <- "Locked to the object's own owner while Chown is checked"
    ownerAutoLabel.textContent <- (if isNullOrUndefined ownerVal then "?" else (ownerVal?name: string))

    ownerWidget.appendChild ownerEditGroup |> ignore
    ownerWidget.appendChild ownerAutoLabel |> ignore

    let refreshOwnerWidgetVisibility () =
        if chownCb.``checked`` then
            ownerEditGroup.setAttribute ("style", "display:none")
            ownerAutoLabel.setAttribute ("style", "")
        else
            ownerEditGroup.setAttribute ("style", "")
            ownerAutoLabel.setAttribute ("style", "display:none")

    refreshOwnerWidgetVisibility ()

    for cb in [ readCb; writeCb; chownCb ] do
        cb.onchange <-
            fun _ ->
                refreshPermsLabel ()
                refreshOwnerWidgetVisibility ()

    let addValueInput = document.createElement ("input") :?> HTMLInputElement
    addValueInput.classList.add "inspector-property-value"
    addValueInput.placeholder <- "value (MOO expr)"

    let addBtn = document.createElement ("button")
    addBtn.classList.add "inspector-add-property-btn"
    addBtn.textContent <- "+"
    addBtn.title <- "Add property"

    addBtn.onclick <-
        fun _ ->
            let name = addNameInput.value.Trim()

            let ownerExpr =
                if chownCb.``checked`` then
                    objectOwnerRef |> Option.map (sprintf "#%d") |> Option.defaultValue "player"
                else
                    addOwnerInput.value.Trim()

            if name <> "" && ownerExpr <> "" then
                sendAction
                    [ "action" ==> "add-property"
                      "obj" ==> int objRef
                      "name" ==> name
                      "ownerExpr" ==> ownerExpr
                      "valueExpr" ==> addValueInput.value
                      "perms" ==> currentPerms () ]

    let mkCell (child: HTMLElement) : HTMLElement =
        let td = document.createElement ("td")
        td.appendChild child |> ignore
        td

    addPropRow.appendChild (mkCell addNameInput) |> ignore
    addPropRow.appendChild (mkCell ownerWidget) |> ignore
    addPropRow.appendChild (mkCell permsWidget) |> ignore
    addPropRow.appendChild (mkCell addValueInput) |> ignore
    addPropRow.appendChild (mkCell addBtn) |> ignore
    addPropRow.setAttribute ("style", "display:none")
    propsTable.appendChild addPropRow |> ignore

    let propsTitleRow = document.createElement ("div")
    propsTitleRow.classList.add "inspector-section-title-row"
    propsTitleRow.appendChild propsTitle |> ignore
    propsTitleRow.appendChild (mkAddTrigger "Add property" addPropRow) |> ignore

    propsSection.appendChild propsTitleRow |> ignore
    propsSection.appendChild propsTable |> ignore

    inspectorContentEl.appendChild propsSection |> ignore

    let verbsSection = document.createElement ("div")
    let verbsTitle = document.createElement ("div")
    verbsTitle.classList.add "inspector-section-title"
    let verbs: obj[] = unbox info?verbs
    verbsTitle.textContent <- sprintf "Verbs (%d)" verbs.Length

    let verbsTable = document.createElement ("table")
    verbsTable.classList.add "inspector-table"
    let verbsHeaderRow = document.createElement ("tr")

    for h in [ "Name"; "Owner"; "Perms"; "Dobj"; "Prep"; "Iobj"; "" ] do
        let th = document.createElement ("th")
        th.textContent <- h
        verbsHeaderRow.appendChild th |> ignore

    verbsTable.appendChild verbsHeaderRow |> ignore

    for v in verbs do
        let tr = document.createElement ("tr")
        tr.classList.add "inspector-verb-row"
        let verbName: string = v?name
        tr.onclick <- fun _ -> openOrSwitchToVerb objRef verbName

        for cellText in [ v?name; v?owner; v?perms; v?dobj; v?prep; v?iobj ] do
            let td = document.createElement ("td")
            td.textContent <- (cellText: string)
            tr.appendChild td |> ignore

        // No confirmation - unlike recycling an object, a deleted verb is
        // trivial to recreate by hand if this was a mistake. Stops
        // propagation so this doesn't also open the verb via the row's own
        // click handler above (same idiom `renderTabs`'s close-× uses
        // against its tab's own switch-click).
        let deleteTd = document.createElement ("td")
        let deleteBtn = document.createElement ("button")
        deleteBtn.classList.add "inspector-row-delete-btn"
        deleteBtn.textContent <- "🗑"
        deleteBtn.title <- "Delete verb"

        deleteBtn.onclick <-
            fun ev ->
                ev.stopPropagation () |> ignore
                sendAction [ "action" ==> "delete-verb"; "obj" ==> int objRef; "verb" ==> verbName ]

        deleteTd.appendChild deleteBtn |> ignore
        tr.appendChild deleteTd |> ignore

        verbsTable.appendChild tr |> ignore

    // Same real-`<tr>`-in-the-same-`<table>` shape as the properties table's
    // own add row, so the Name/Owner/Perms/Dobj/Prep/Iobj columns above
    // line up with the fields below.
    let addVerbRow = document.createElement ("tr")
    addVerbRow.classList.add "inspector-add-property"

    let addVerbNameInput = document.createElement ("input") :?> HTMLInputElement
    addVerbNameInput.classList.add "inspector-property-value"
    addVerbNameInput.placeholder <- "name alias2 ..."

    // Unlike a property's owner, a verb's owner has no chown-style
    // auto-override (confirmed against `ToastStunt/src/db_verbs.cc` - no
    // analog to `db_properties.cc`'s `insert_prop2` exists there), so this
    // is always a plain editable field - no conditional hide/show needed.
    // Same shared widget the property-owner picker uses above - literally
    // the same component, per the review note asking for consistency.
    let addVerbOwnerGroup, addVerbOwnerInput =
        mkQuickFillInput "player, #5, or $room" "player" ownerQuickFills false

    // Verbs only ever have four permission bits - r/w/x/d (Read/Write/Exec/
    // Debug) - confirmed against `ToastStunt/src/verbs.cc`'s
    // `validate_verb_info`; properties' `c` (Chown) doesn't apply here.
    // Same popover pattern the properties table's own perms widget uses.
    let verbPermsWidget = document.createElement ("div")
    verbPermsWidget.classList.add "inspector-perms-widget"

    let verbPermsToggleBtn = document.createElement ("button")
    verbPermsToggleBtn.classList.add "pane-action-btn"
    verbPermsToggleBtn.title <- "Permissions"

    let verbPermsPopover = document.createElement ("div")
    verbPermsPopover.classList.add "tree-filter-settings-popover"
    verbPermsPopover.onclick <- fun ev -> ev.stopPropagation () |> ignore

    let mkVerbPermCheckbox (label: string) (tooltip: string) (isChecked: bool) : HTMLInputElement =
        let row = document.createElement ("label")
        row.classList.add "settings-row"
        row.title <- tooltip

        let cb = document.createElement ("input") :?> HTMLInputElement
        cb.setAttribute ("type", "checkbox")
        cb.``checked`` <- isChecked

        row.appendChild cb |> ignore
        row.appendChild (document.createTextNode label) |> ignore
        verbPermsPopover.appendChild row |> ignore
        cb

    // Read+Exec checked by default - a normal callable command verb; Write
    // and Debug off, matching the properties widget's own "least-surprising
    // default" convention.
    let verbReadCb = mkVerbPermCheckbox "Read" "Other players' code can read this verb's source." true
    let verbWriteCb = mkVerbPermCheckbox "Write" "Other players' code can modify this verb's source." false
    let verbExecCb = mkVerbPermCheckbox "Exec" "Other players' code can call this verb." true

    // Verified against `ToastStunt/src/execute.cc`'s `RAISE_ERROR` macro -
    // with this flag unset, a runtime error is dropped entirely (not just
    // logged differently), so the verb silently continues past the failure.
    let verbDebugCb =
        mkVerbPermCheckbox
            "Debug"
            "Runtime errors actually raise/propagate (recommended). Without this, errors are silently swallowed."
            false

    let currentVerbPerms () : string =
        [ verbReadCb, "r"; verbWriteCb, "w"; verbExecCb, "x"; verbDebugCb, "d" ]
        |> List.filter (fun (cb, _) -> cb.``checked``)
        |> List.map snd
        |> String.concat ""

    let refreshVerbPermsLabel () =
        let s = currentVerbPerms ()
        verbPermsToggleBtn.textContent <- (if s = "" then "(none)" else s)

    refreshVerbPermsLabel ()

    verbPermsToggleBtn.onclick <-
        fun ev ->
            ev.stopPropagation () |> ignore
            verbPermsPopover.classList.toggle "visible" |> ignore

    for cb in [ verbReadCb; verbWriteCb; verbExecCb; verbDebugCb ] do
        cb.onchange <- fun _ -> refreshVerbPermsLabel ()

    verbPermsWidget.appendChild verbPermsToggleBtn |> ignore
    verbPermsWidget.appendChild verbPermsPopover |> ignore

    let mkArgSpecSelect (options: string list) (defaultValue: string) : HTMLSelectElement =
        let select = document.createElement ("select") :?> HTMLSelectElement

        for opt in options do
            let optionEl = document.createElement ("option") :?> HTMLOptionElement
            optionEl.value <- opt
            optionEl.textContent <- opt
            select.appendChild optionEl |> ignore

        select.value <- defaultValue
        select

    // "this none this" - a normal command verb takes its own object as
    // dobj/iobj by default (per the review note); prep defaults to "none".
    let dobjSelect = mkArgSpecSelect [ "none"; "any"; "this" ] "this"
    let iobjSelect = mkArgSpecSelect [ "none"; "any"; "this" ] "this"

    // Free-typed rather than a dropdown of the full preposition table -
    // `add_verb`'s own `match_prep_spec` (confirmed against
    // `ToastStunt/src/db_verbs.cc`) validates it server-side (E_INVARG on
    // garbage), surfaced through the same `errtext` path every other field
    // already uses. "none"/"any" are the two common cases, quick-filled the
    // same way an owner's "You"/"This object" are.
    let prepGroup, prepInput = mkQuickFillInput "none, any, or a preposition (e.g. \"on top of\")" "none" [ "none", "none"; "any", "any" ] false

    let addVerbBtn = document.createElement ("button")
    addVerbBtn.classList.add "inspector-add-property-btn"
    addVerbBtn.textContent <- "+"
    addVerbBtn.title <- "Add verb"

    addVerbBtn.onclick <-
        fun _ ->
            let names = addVerbNameInput.value.Trim()
            let ownerExpr = addVerbOwnerInput.value.Trim()

            if names <> "" && ownerExpr <> "" then
                sendAction
                    [ "action" ==> "add-verb"
                      "obj" ==> int objRef
                      "name" ==> names
                      "ownerExpr" ==> ownerExpr
                      "perms" ==> currentVerbPerms ()
                      "dobj" ==> dobjSelect.value
                      "prep" ==> prepInput.value.Trim()
                      "iobj" ==> iobjSelect.value ]

    let mkVerbCell (child: HTMLElement) : HTMLElement =
        let td = document.createElement ("td")
        td.appendChild child |> ignore
        td

    addVerbRow.appendChild (mkVerbCell addVerbNameInput) |> ignore
    addVerbRow.appendChild (mkVerbCell addVerbOwnerGroup) |> ignore
    addVerbRow.appendChild (mkVerbCell verbPermsWidget) |> ignore
    addVerbRow.appendChild (mkVerbCell dobjSelect) |> ignore
    addVerbRow.appendChild (mkVerbCell prepGroup) |> ignore
    addVerbRow.appendChild (mkVerbCell iobjSelect) |> ignore
    addVerbRow.appendChild (mkVerbCell addVerbBtn) |> ignore
    addVerbRow.setAttribute ("style", "display:none")
    verbsTable.appendChild addVerbRow |> ignore

    let verbsTitleRow = document.createElement ("div")
    verbsTitleRow.classList.add "inspector-section-title-row"
    verbsTitleRow.appendChild verbsTitle |> ignore
    verbsTitleRow.appendChild (mkAddTrigger "Add verb" addVerbRow) |> ignore

    verbsSection.appendChild verbsTitleRow |> ignore
    verbsSection.appendChild verbsTable |> ignore
    inspectorContentEl.appendChild verbsSection |> ignore

    // Only safe to scroll/flash once the row is actually attached to the
    // live document - `propsSection` (and the `tr` inside it) just got
    // appended above.
    match highlightRow with
    | Some tr ->
        scrollIntoViewCentered tr
        tr.classList.add "inspector-prop-highlight"
    | None -> ()

/// Refreshes the History tab's corponym-history section - always, same
/// "always fresh" convention `loadInspector` uses, since this is server-side
/// git history that could have changed since last shown.
and private loadCorponymHistory () : unit =
    corponymHistoryListEl.innerHTML <- "Loading..."
    sendAction [ "action" ==> "corponym-history" ]

/// Switches to the History tab and refreshes its corponym-history section
/// (the search section stays whatever it last showed - search is explicit,
/// user-triggered via Enter, not something to unconditionally re-run).
and private openOrSwitchToHistory () : unit =
    switchToTab HistoryTab

    if isLoggedIn then
        loadCorponymHistory ()
    else
        corponymHistoryListEl.innerHTML <- ""
        historySearchResultsEl.innerHTML <- ""

/// Renders `search-history`'s results - each clickable (when it resolved to
/// a live objnum; see `IdeActions.searchHistory`'s own comment on why an
/// unresolvable corponym is shown but not clickable) straight through to
/// that verb via the existing `openOrSwitchToVerb`, same as every other
/// verb-opening entry point in this file.
and private renderSearchResults (results: (string * int64 * int64 option * string * string * string) list) : unit =
    historySearchResultsEl.innerHTML <- ""

    if results.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No matches."
        historySearchResultsEl.appendChild li |> ignore
    else
        for _sha, whenEpochSeconds, objRefOpt, corponym, label, message in results do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            let date = System.DateTimeOffset.FromUnixTimeSeconds(whenEpochSeconds).LocalDateTime

            li.textContent <- sprintf "%s  $%s / %s - %s" (date.ToString("yyyy-MM-dd HH:mm")) corponym label message

            match objRefOpt with
            | Some objRef ->
                li.classList.add "inspector-link"
                li.onclick <- fun _ -> openOrSwitchToVerb objRef label
            | None -> ()

            historySearchResultsEl.appendChild li |> ignore

/// Renders `corponym-history`'s entries - each `repointed` entry's old/new
/// objnum is clickable through to that object's inspector via the existing
/// `openOrSwitchToInspector`, same link style the inspector pane's own
/// parent/child lists use.
and private renderCorponymHistoryList (entries: (string * int64 * string * string * string) list) : unit =
    corponymHistoryListEl.innerHTML <- ""

    if entries.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No corponym changes yet."
        corponymHistoryListEl.appendChild li |> ignore
    else
        for _sha, whenEpochSeconds, kind, name, detail in entries do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            let date = System.DateTimeOffset.FromUnixTimeSeconds(whenEpochSeconds).LocalDateTime
            li.textContent <- sprintf "%s  %s $%s: %s" (date.ToString("yyyy-MM-dd HH:mm")) kind name detail
            corponymHistoryListEl.appendChild li |> ignore

/// Rebuilds `#verb-tabs` (the dynamic, closable tabs) and the static
/// `#tab-game` button's `.active` state. `#tab-game` itself is never
/// recreated - only its highlight changes.
and private renderTabs () : unit =
    verbTabsEl.innerHTML <- ""

    for objRef, verbName in openVerbTabs do
        let tab = document.createElement ("div")
        tab.classList.add "main-tab"
        if activeTab = VerbTab(objRef, verbName) then tab.classList.add "active"
        if previewTab = Some(objRef, verbName) then tab.classList.add "preview"

        let label = document.createElement ("span")
        label.classList.add "main-tab-label"
        label.textContent <- sprintf "%s (#%d)" verbName objRef
        label.onclick <- fun _ -> switchToTab (VerbTab(objRef, verbName))

        // Double-click "pins" a preview tab - it stops being subject to
        // replacement by the next verb opened, same as VS Code.
        label.ondblclick <-
            fun _ ->
                if previewTab = Some(objRef, verbName) then
                    previewTab <- None
                    renderTabs ()

        let closeBtn = document.createElement ("button")
        closeBtn.classList.add "main-tab-close"
        closeBtn.textContent <- "×"
        closeBtn.onclick <- fun ev -> ev.stopPropagation () |> ignore; closeTab (objRef, verbName)

        // Middle-click anywhere on the tab closes it, matching VS Code -
        // `preventDefault` on `mousedown` (not just the `click`/`auxclick`
        // that would follow) since the middle button's default action,
        // autoscroll mode, otherwise activates before either fires.
        tab.onmousedown <-
            fun ev ->
                if ev.button = 1.0 then
                    ev.preventDefault ()
                    closeTab (objRef, verbName)

        tab.appendChild label |> ignore
        tab.appendChild closeBtn |> ignore
        verbTabsEl.appendChild tab |> ignore

    // Inspector tabs share the same strip as verb tabs (an "ⓘ #N" label,
    // same close-× behavior, and the same preview-tab mechanic) - unlike
    // verb tabs, clicking one always re-loads it fresh
    // (`openOrSwitchToInspector`, not a bare `switchToTab`).
    for objRef in openInspectorTabs do
        let tab = document.createElement ("div")
        tab.classList.add "main-tab"
        if activeTab = InspectorTab objRef then tab.classList.add "active"
        if previewInspectorTab = Some objRef then tab.classList.add "preview"

        let label = document.createElement ("span")
        label.classList.add "main-tab-label"
        label.textContent <- sprintf "ⓘ #%d" objRef
        label.onclick <- fun _ -> openOrSwitchToInspector objRef

        // Double-click "pins" a preview inspector tab - same mechanic as
        // verb tabs.
        label.ondblclick <-
            fun _ ->
                if previewInspectorTab = Some objRef then
                    previewInspectorTab <- None
                    renderTabs ()

        let closeBtn = document.createElement ("button")
        closeBtn.classList.add "main-tab-close"
        closeBtn.textContent <- "×"
        closeBtn.onclick <- fun ev -> ev.stopPropagation () |> ignore; closeInspectorTab objRef

        // Middle-click anywhere on the tab closes it - same as verb tabs above.
        tab.onmousedown <-
            fun ev ->
                if ev.button = 1.0 then
                    ev.preventDefault ()
                    closeInspectorTab objRef

        tab.appendChild label |> ignore
        tab.appendChild closeBtn |> ignore
        verbTabsEl.appendChild tab |> ignore

    if activeTab = GameTab then
        tabGameBtn.classList.add "active"
    else
        tabGameBtn.classList.remove "active"

    if activeTab = HistoryTab then
        tabHistoryBtn.classList.add "active"
    else
        tabHistoryBtn.classList.remove "active"

/// True if `node` itself is a filter match, respecting the filter's scope.
/// "prop:" is exclusive - a property match never falls through to also
/// checking the name/verbs, unlike `AnyKind`'s own name-or-verb check.
and private nodeMatches (filter: ParsedFilter) (node: TreeNode) : bool =
    match filter.Kind with
    | ObjectOnly -> matchesFilter filter.Text node.Name
    | VerbOnly -> node.Verbs |> Array.exists (fun v -> matchesFilter filter.Text v.Name)
    | PropertyOnly -> node.Properties |> Array.exists (fun p -> matchesFilter filter.Text p.Name)
    | AnyKind -> matchesFilter filter.Text node.Name || node.Verbs |> Array.exists (fun v -> matchesFilter filter.Text v.Name)

/// Every objRef that needs to be expanded for at least one filter match to
/// be reachable - a match's *every* parent, recursively (via `ancestorsOf`),
/// since a DAG node can have more than one parent path to a root and each
/// occurrence needs its own ancestor chain expanded for the match to be
/// visible wherever it appears.
and private ancestorExpansionSet (filter: ParsedFilter) : Set<int64> =
    treeNodes
    |> Map.toSeq
    |> Seq.map snd
    |> Seq.filter (nodeMatches filter)
    |> Seq.map (fun n -> n.ObjRef)
    |> Seq.fold (fun acc r -> Set.union acc (Set.add r (ancestorsOf Set.empty r))) Set.empty

/// One row of the flattened, currently-*visible* tree - either an object
/// (with its depth and whether it has anything to expand into), the
/// virtual "Properties"/"Verbs"/"Children" group node for an expanded
/// object that has any (properties, then verbs, then children), or (once
/// that group is itself expanded) one of its actual properties/verbs/child
/// objects.
and private flattenVisibleRows
    (hideEmptyLeaves: bool)
    (showProperties: bool)
    (showVerbs: bool)
    (expanded: Set<int64>)
    (expandedPropGroups: Set<int64>)
    (expandedVerbGroups: Set<int64>)
    (expandedChildGroups: Set<int64>)
    (liveChildrenChecked: Set<int64>)
    (roots: int64[])
    : TreeRow list =
    let childrenOf (node: TreeNode) : int64[] =
        node.Children
        |> Array.filter (fun childRef ->
            not hideEmptyLeaves
            || match Map.tryFind childRef treeNodes with
               | None -> true // unknown ref - show rather than silently drop
               | Some c -> not (Array.isEmpty c.Children) || not (Array.isEmpty c.Verbs) || not (Array.isEmpty c.Properties))

    let rec go (visited: Set<int64>) (depth: int) (objRef: int64) : TreeRow list =
        match Map.tryFind objRef treeNodes with
        | None -> []
        | Some _ when Set.contains objRef visited ->
            [ ObjectRow(objRef, depth, false) ] // cycle guard: render once, never recurse again
        | Some node ->
            let visited = Set.add objRef visited
            let visibleChildren = childrenOf node

            let isExpandable =
                not (Array.isEmpty visibleChildren)
                || (showVerbs && not (Array.isEmpty node.Verbs))
                || (showProperties && not (Array.isEmpty node.Properties))
                || not (Set.contains objRef liveChildrenChecked) // unknown - assume yes until asked once

            let selfRow = ObjectRow(objRef, depth, isExpandable)

            if not (Set.contains objRef expanded) then
                [ selfRow ]
            else
                let propGroupRows =
                    if not showProperties || Array.isEmpty node.Properties then
                        []
                    else
                        let groupRow = PropGroupRow(objRef, depth + 1, node.Properties.Length)

                        if Set.contains objRef expandedPropGroups then
                            groupRow
                            :: (node.Properties
                                |> Array.sortBy (fun p -> p.Name)
                                |> Array.map (fun p -> PropRow(objRef, p, depth + 2))
                                |> List.ofArray)
                        else
                            [ groupRow ]

                let verbGroupRows =
                    if not showVerbs || Array.isEmpty node.Verbs then
                        []
                    else
                        let groupRow = VerbGroupRow(objRef, depth + 1, node.Verbs.Length)

                        if Set.contains objRef expandedVerbGroups then
                            groupRow
                            :: (node.Verbs
                                |> Array.sortBy (fun v -> v.Name)
                                |> Array.map (fun v -> VerbRow(objRef, v, depth + 2))
                                |> List.ofArray)
                        else
                            [ groupRow ]

                let childGroupRows =
                    if Array.isEmpty visibleChildren then
                        []
                    else
                        let groupRow = ChildGroupRow(objRef, depth + 1, visibleChildren.Length)

                        if Set.contains objRef expandedChildGroups then
                            groupRow
                            :: (visibleChildren
                                |> Array.sort
                                |> Array.collect (fun r -> go visited (depth + 2) r |> Array.ofList)
                                |> List.ofArray)
                        else
                            [ groupRow ]

                selfRow :: (propGroupRows @ verbGroupRows @ childGroupRows)

    roots |> Array.sort |> Array.collect (fun r -> go Set.empty 0 r |> Array.ofList) |> List.ofArray

/// Renders the currently-visible tree into `#tree-list` - reuses
/// `renderList`'s old DOM idiom (`.picker-row`/`.selected`/`.placeholder`),
/// plus depth indentation and an expand chevron on object rows.
and private renderTreeRows (rows: TreeRow list) : unit =
    treeListEl.innerHTML <- ""

    if List.isEmpty rows then
        let li = document.createElement ("li")
        li.textContent <- (if treeFilterText.Trim() <> "" then "no matches" else "no objects yet")
        li.classList.add "placeholder"
        treeListEl.appendChild li |> ignore
    else
        for row in rows do
            let li = document.createElement ("li")
            li.classList.add "picker-row"
            li.classList.add "tree-row"

            match row with
            | ObjectRow(objRef, depth, isExpandable) ->
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))

                let chevron = document.createElement ("span")
                chevron.classList.add "tree-chevron"

                if isExpandable then
                    chevron.textContent <- (if Set.contains objRef expandedRefs then "▾" else "▸")

                li.appendChild chevron |> ignore

                let kindIcon = document.createElement ("span")
                kindIcon.classList.add "tree-icon"
                kindIcon.classList.add "tree-icon-object"
                kindIcon.textContent <- "◇"
                li.appendChild kindIcon |> ignore

                let labelSpan = document.createElement ("span")

                labelSpan.textContent <-
                    (Map.tryFind objRef treeNodes |> Option.map (fun n -> n.Name) |> Option.defaultValue (sprintf "#%d" objRef))

                li.appendChild labelSpan |> ignore

                if activeTab = InspectorTab objRef || selectedObjRef = Some objRef then
                    li.classList.add "selected"

                li.onclick <-
                    fun _ ->
                        // Remember this as "the one" while a filter's active,
                        // so clearing it (`promoteFilterExpansionIfAny`) keeps
                        // this object in view - not every other match too.
                        if treeFilterText.Trim() <> "" then
                            lastFilterSelectedObjRef <- Some objRef

                        // Highlights this row immediately, the same way
                        // clicking a verb row already highlights it - doesn't
                        // require the inspector to be open.
                        selectedObjRef <- Some objRef

                        // Opens/switches to this object's inspector directly -
                        // there's no separate "ⓘ" button for this anymore.
                        openOrSwitchToInspector objRef

                        if isExpandable then
                            let wasExpanded = Set.contains objRef expandedRefs

                            expandedRefs <- if wasExpanded then Set.remove objRef expandedRefs else Set.add objRef expandedRefs

                            // Every expand asks live, unconditionally - there's no
                            // reliable client-side signal for "this corponym'd
                            // object might have live-only children" without
                            // asking, and the response is a cheap no-op merge
                            // when nothing new turns up.
                            if not wasExpanded then
                                sendAction [ "action" ==> "get-live-children"; "obj" ==> int objRef ]

                        renderTree ()
            | PropGroupRow(objRef, depth, count) ->
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))
                li.classList.add "tree-prop-group"

                let chevron = document.createElement ("span")
                chevron.classList.add "tree-chevron"
                chevron.textContent <- (if Set.contains objRef expandedPropGroups then "▾" else "▸")
                li.appendChild chevron |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- sprintf "Properties (%d)" count
                li.appendChild labelSpan |> ignore

                li.onclick <-
                    fun _ ->
                        expandedPropGroups <-
                            if Set.contains objRef expandedPropGroups then Set.remove objRef expandedPropGroups
                            else Set.add objRef expandedPropGroups

                        renderTree ()
            | PropRow(objRef, prop, depth) ->
                li.classList.add "tree-row-wrap"
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))

                let kindIcon = document.createElement ("span")
                kindIcon.classList.add "tree-icon"
                kindIcon.classList.add "tree-icon-prop"
                kindIcon.textContent <- "•"
                li.appendChild kindIcon |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- prop.Name
                li.appendChild labelSpan |> ignore

                let metaSpan = document.createElement ("span")
                metaSpan.classList.add "tree-row-meta"
                metaSpan.textContent <- sprintf "[%s]" prop.Perms
                li.appendChild metaSpan |> ignore

                if activeTab = InspectorTab objRef && activeInspectorProp = Some(objRef, prop.Name) then
                    li.classList.add "selected"

                li.onclick <- fun _ -> revealPropertyInInspector objRef prop.Name
            | VerbGroupRow(objRef, depth, count) ->
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))
                li.classList.add "tree-verb-group"

                let chevron = document.createElement ("span")
                chevron.classList.add "tree-chevron"
                chevron.textContent <- (if Set.contains objRef expandedVerbGroups then "▾" else "▸")
                li.appendChild chevron |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- sprintf "Verbs (%d)" count
                li.appendChild labelSpan |> ignore

                li.onclick <-
                    fun _ ->
                        expandedVerbGroups <-
                            if Set.contains objRef expandedVerbGroups then Set.remove objRef expandedVerbGroups
                            else Set.add objRef expandedVerbGroups

                        renderTree ()
            | VerbRow(objRef, verb, depth) ->
                li.classList.add "tree-row-wrap"
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))

                let kindIcon = document.createElement ("span")
                kindIcon.classList.add "tree-icon"
                kindIcon.classList.add "tree-icon-verb"
                kindIcon.textContent <- "ƒ"
                li.appendChild kindIcon |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- verb.Name
                li.appendChild labelSpan |> ignore

                let metaSpan = document.createElement ("span")
                metaSpan.classList.add "tree-row-meta"
                metaSpan.textContent <- sprintf "[%s] [%s]" verb.Perms (verbArgsCode verb)
                li.appendChild metaSpan |> ignore

                if activeTab = VerbTab(objRef, verb.Name) then
                    li.classList.add "selected"

                li.onclick <- fun _ -> openOrSwitchToVerb objRef verb.Name
            | ChildGroupRow(objRef, depth, count) ->
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))
                li.classList.add "tree-child-group"

                let chevron = document.createElement ("span")
                chevron.classList.add "tree-chevron"
                chevron.textContent <- (if Set.contains objRef expandedChildGroups then "▾" else "▸")
                li.appendChild chevron |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- sprintf "Children (%d)" count
                li.appendChild labelSpan |> ignore

                li.onclick <-
                    fun _ ->
                        expandedChildGroups <-
                            if Set.contains objRef expandedChildGroups then Set.remove objRef expandedChildGroups
                            else Set.add objRef expandedChildGroups

                        renderTree ()

            treeListEl.appendChild li |> ignore

/// Recomputes and redraws the visible tree from `treeNodes`/`expandedRefs`/
/// `treeFilterText` - the single entry point every state change (expand
/// toggle, filter keystroke, tab switch, hide-empty-leaves setting) calls
/// to stay in sync, matching this file's existing "full rebuild, no
/// incremental DOM patching" style.
and private renderTree () : unit =
    let hideEmptyLeaves = Settings.hideEmptyLeavesEnabled ()
    let showProperties = Settings.showPropertiesEnabled ()
    let showVerbs = Settings.showVerbsEnabled ()

    if treeFilterText.Trim() = "" then
        renderTreeRows (
            flattenVisibleRows
                hideEmptyLeaves
                showProperties
                showVerbs
                expandedRefs
                expandedPropGroups
                expandedVerbGroups
                expandedChildGroups
                liveChildrenChecked
                rootRefs
        )
    else
        let filter = parseFilter treeFilterText
        let ancestorRefs = ancestorExpansionSet filter
        let expanded = Set.union expandedRefs ancestorRefs

        // Objects whose own verbs (not just their name) are what matched -
        // their "Verbs" group needs auto-expanding too, or a matching verb
        // would stay hidden behind it. Never any, under "obj:" - verbs are
        // out of scope for that search entirely.
        let verbMatchOwners =
            if filter.Kind = ObjectOnly then
                Set.empty
            else
                treeNodes
                |> Map.toSeq
                |> Seq.map snd
                |> Seq.filter (fun n -> n.Verbs |> Array.exists (fun v -> matchesFilter filter.Text v.Name))
                |> Seq.map (fun n -> n.ObjRef)
                |> Set.ofSeq

        let expandedVerbGroups = Set.union expandedVerbGroups verbMatchOwners

        // Objects whose own properties are what matched - only ever
        // non-empty under "prop:" itself, unlike `verbMatchOwners` (which
        // also fires for the unprefixed `AnyKind` search) - see
        // `FilterKind`'s own comment for why properties don't participate
        // in the default search the way verbs do.
        let propMatchOwners =
            if filter.Kind = PropertyOnly then
                treeNodes
                |> Map.toSeq
                |> Seq.map snd
                |> Seq.filter (fun n -> n.Properties |> Array.exists (fun p -> matchesFilter filter.Text p.Name))
                |> Seq.map (fun n -> n.ObjRef)
                |> Set.ofSeq
            else
                Set.empty

        let expandedPropGroups = Set.union expandedPropGroups propMatchOwners

        // Unlike verbs/props (flat, exact-match gated), a child subtree can
        // hide a match arbitrarily deep - every ancestor-of-a-match needs
        // its children group force-open too, or the match is unreachable
        // regardless of `expanded`, same reasoning as `expanded` itself one
        // level up.
        let expandedChildGroups = Set.union expandedChildGroups ancestorRefs

        let allRows =
            flattenVisibleRows
                hideEmptyLeaves
                showProperties
                showVerbs
                expanded
                expandedPropGroups
                expandedVerbGroups
                expandedChildGroups
                liveChildrenChecked
                rootRefs

        // Keep a row if it's itself a match, or an ancestor object-row on
        // the way to one - the prop/verb group and prop/verb rows never need
        // to survive purely as ancestors, only object rows do (expansion
        // only ever reveals a path *down* to a match). The children group
        // survives on the same `ancestorRefs` test as `ObjectRow` - a
        // forced-open group whose children don't lead anywhere gets built
        // into `allRows` but its non-matching child `ObjectRow`s are
        // dropped right below by that same arm, same as today.
        allRows
        |> List.filter (fun row ->
            match row with
            | ObjectRow(objRef, _, _) ->
                Set.contains objRef ancestorRefs
                || (Map.tryFind objRef treeNodes |> Option.map (nodeMatches filter) |> Option.defaultValue false)
            | PropGroupRow(objRef, _, _) -> Set.contains objRef propMatchOwners
            | PropRow(_, prop, _) -> filter.Kind = PropertyOnly && matchesFilter filter.Text prop.Name
            | VerbGroupRow(objRef, _, _) -> Set.contains objRef verbMatchOwners
            | VerbRow(_, verb, _) -> filter.Kind <> ObjectOnly && matchesFilter filter.Text verb.Name
            | ChildGroupRow(objRef, _, _) -> Set.contains objRef ancestorRefs)
        |> renderTreeRows

/// Reveals `objRef` in the tree (expanding every ancestor path to it, and
/// its own verb group) and opens `verbName` directly - used by
/// go-to-definition, which already knows exactly which verb it wants open.
/// The bulk tree has every object's own verbs in memory already, so
/// there's nothing to wait on the way the old
/// `selectObject`/`listVerbsAsync` round-trip did. Every ancestor's
/// children group needs opening too, not just its row-level expand flag -
/// child objects live behind that group gate now, so without this the path
/// down to `objRef` would stay hidden even though each ancestor's chevron
/// looks expanded.
and private revealAndOpenVerb (objRef: int64) (verbName: string) : unit =
    let ancestorPath = Set.add objRef (ancestorsOf Set.empty objRef)
    expandedRefs <- Set.union expandedRefs ancestorPath
    expandedChildGroups <- Set.union expandedChildGroups ancestorPath
    expandedVerbGroups <- Set.add objRef expandedVerbGroups
    renderTree ()
    openOrSwitchToVerb objRef verbName

// Clicking anywhere in the scrollback should feel like clicking into the
// terminal itself, not require a precise click on the (visually tiny) input
// row below it. Bound on `#output` specifically, not the whole
// `#terminal-pane` - that also contains `#login-pane`'s own inputs/button,
// and since click-driven focus fires after the browser's own mousedown
// focus, refocusing `#input` unconditionally on any pane click would yank
// focus back away from whichever login field was just clicked. `#output`
// has no interactive children of its own and is a sibling of `#input`, not
// an ancestor, so this can't double-handle a click on the input either.
//
// A drag-to-select-text mouseup still fires this same `click` event - if we
// focused unconditionally, moving focus to `#input` would collapse the
// selection the user just made (an input's own selection model steals the
// page's `Selection`), so a click that leaves behind a real selection skips
// the focus instead of destroying it.
outputEl.onclick <-
    fun _ ->
        let selection: obj = window?getSelection ()

        if selection?isCollapsed then
            inputEl.focus ()

tabGameBtn.onclick <- fun _ -> switchToTab GameTab
tabHistoryBtn.onclick <- fun _ -> openOrSwitchToHistory ()

historySearchInputEl.onkeydown <-
    fun ev ->
        if ev.key = "Enter" && historySearchInputEl.value.Trim() <> "" then
            historySearchResultsEl.innerHTML <- "Searching..."
            sendAction [ "action" ==> "search-history"; "query" ==> historySearchInputEl.value ]
// `switchToTab` no-ops when its argument already equals `activeTab` (to
// avoid redundant work re-clicking the tab you're already on) - but
// `activeTab` *starts* as `GameTab`, so that guard also skipped the very
// first application of `showPaneFor`, leaving `#terminal-pane` without its
// `.active` class even though the Game tab looked selected. Call it
// directly here, once, to actually paint the initial state.
showPaneFor GameTab
renderTabs ()

treeFilterEl.oninput <-
    fun _ ->
        treeFilterText <- treeFilterEl.value
        // Covers clearing by backspacing the box empty too, not just the ×
        // button below - either way, keep whatever was just found in view.
        if treeFilterText.Trim() = "" then
            promoteFilterExpansionIfAny ()
        renderTree ()

treeFilterClearEl.onclick <-
    fun _ ->
        treeFilterEl.value <- ""
        treeFilterText <- ""
        promoteFilterExpansionIfAny ()
        renderTree ()
        treeFilterEl.focus ()

// Persistence + each checkbox's initial `checked` state are handled inside
// `Settings.init()` already (called earlier, before `renderTree` existed) -
// this just wires the redraw, now that it's in scope.
treeFilterShowPropertiesEl.onchange <-
    fun _ ->
        Settings.setShowProperties treeFilterShowPropertiesEl.``checked``
        renderTree ()

treeFilterShowVerbsEl.onchange <-
    fun _ ->
        Settings.setShowVerbs treeFilterShowVerbsEl.``checked``
        renderTree ()

treeFilterHideEmptyLeavesEl.onchange <-
    fun _ ->
        Settings.setHideEmptyLeaves treeFilterHideEmptyLeavesEl.``checked``
        renderTree ()

// Starts out showing its empty-state placeholder - populated for real once
// `moodev-login-result` confirms a login (see below).
renderTree ()

ws.onopen <-
    fun _ ->
        appendOutput "[connected]\n"
        // v1 simplification: the sidebar/tabs are always shown once
        // connected, rather than proactively querying player.programmer
        // first. A non-programmer just sees E_PERM in the diagnostics area
        // on save - see $vcs:ide_fetch/ide_save, which both check
        // player.programmer server-side regardless of what the client
        // shows. The tree is stricter, though - see the
        // `moodev-login-result` handler below - it stays empty until a real
        // MOO login succeeds, since the metadata graph it's drawn from has
        // nothing to do with which (if any) account this session is using.
        sidebarEl.classList.add ("visible")
        mainTabsEl.classList.add ("visible")
        PaneResizer.init PaneResizer.LeftRight "moodev-sidebar-width-pct" layoutEl sidebarResizerEl sidebarEl
        Sidebar.init ()
        Login.init (fun cmd -> ws.send cmd)

ws.onclose <- fun _ -> appendOutput "\n[disconnected]\n"
ws.onerror <- fun _ -> appendOutput "\n[connection error]\n"

/// Arrow-up/down command history for the terminal input, same convention
/// as a normal shell: -1 means "not currently browsing history" (the live
/// edit in progress); `historyDraft` holds that live edit so ArrowDown can
/// restore it after browsing back up.
let private commandHistory = ResizeArray<string>()
let mutable private historyIndex = -1
let mutable private historyDraft = ""

inputEl.onkeydown <-
    fun ev ->
        match ev.key with
        | "Enter" ->
            let cmd = inputEl.value

            if cmd <> "" then
                commandHistory.Add cmd

            if ws.readyState <> WebSocketState.OPEN then
                appendOutput "\n[not connected - message not sent]\n"
            else
                ws.send cmd

            inputEl.value <- ""
            historyIndex <- -1
            historyDraft <- ""
        | "ArrowUp" ->
            ev.preventDefault ()

            if commandHistory.Count > 0 then
                if historyIndex = -1 then
                    historyDraft <- inputEl.value
                    historyIndex <- commandHistory.Count - 1
                elif historyIndex > 0 then
                    historyIndex <- historyIndex - 1

                inputEl.value <- commandHistory.[historyIndex]
        | "ArrowDown" ->
            ev.preventDefault ()

            if historyIndex <> -1 then
                if historyIndex < commandHistory.Count - 1 then
                    historyIndex <- historyIndex + 1
                    inputEl.value <- commandHistory.[historyIndex]
                else
                    historyIndex <- -1
                    inputEl.value <- historyDraft
        | _ -> ()

ws.onmessage <-
    fun ev ->
        if isMcpMessage ev.data then
            let text: string = unbox ev.data
            let parsed: obj = JS.JSON.parse text
            let header: string = parsed?header
            let lines: string[] = parsed?lines

            if header.StartsWith("moodev-edit-content") then
                let content = String.concat "\n" lines
                editor.setValue content
                // `indentationRules` alone only governs newly-typed lines -
                // it has no retroactive effect on content that arrives via
                // `setValue`, which is how every verb loads. Most of the
                // real corpus has no indentation at all, so without this,
                // "indentation" would only ever be visible on lines typed
                // fresh in the editor, never on anything just opened.
                (editor.getAction Monaco.reindentLinesActionId).run () |> ignore
                // Both `setValue` and the reindent above just fired
                // `onDidChangeModelContent` - freshly-loaded (and now
                // reindented) content is a clean baseline, not something
                // the user has edited yet, so undo that.
                setDirty false
                editorDiagnosticsEl.textContent <- ""
                // Monaco reuses one editor instance (and its one underlying
                // model) across every verb tab - `setValue` just replaces
                // that model's text, it never creates a new model per verb -
                // so without this, switching to a different verb would
                // carry over stale squigglies from whatever verb was open
                // before.
                Monaco.setErrorMarkers editor []

                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef ->
                        tabContent <- Map.add (objRef, verb) content tabContent

                        if not (openVerbTabs |> List.contains (objRef, verb)) then
                            // Brand-new tab - VS Code's preview-tab mechanic
                            // (see `previewTab`'s own comment): replace the
                            // current preview tab in place if there is one,
                            // otherwise just append.
                            match previewTab with
                            | Some oldPreview ->
                                let idx = openVerbTabs |> List.findIndex (fun t -> t = oldPreview)
                                openVerbTabs <- openVerbTabs |> List.mapi (fun i t -> if i = idx then (objRef, verb) else t)
                                tabContent <- Map.remove oldPreview tabContent
                            | None -> openVerbTabs <- openVerbTabs @ [ (objRef, verb) ]

                            previewTab <- Some(objRef, verb)

                        activeTab <- VerbTab(objRef, verb)
                        showPaneFor activeTab
                        renderTabs ()
                        // Refresh the tree's highlight to follow whatever
                        // just opened - cheap, reuses the already-built tree.
                        renderTree ()
                    | false, _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-edit-result") then
                let ok = headerField "ok: " header = Some "1"

                editorDiagnosticsEl.textContent <-
                    if ok then "" else String.concat "\n" lines

                let lineErrors = lines |> Array.toList |> List.choose parseErrorLine
                Monaco.setErrorMarkers editor (if ok then [] else lineErrors)
            elif header.StartsWith("moodev-login-result") then
                if headerField "ok: " header = Some "1" then
                    Login.hide ()

                    async {
                        let! nodes = LspClient.getObjectTreeAsync ()
                        buildTree nodes
                        expandedRefs <- Set.empty
                        liveChildrenChecked <- Set.empty
                        selectedObjRef <- None
                        renderTree ()
                    }
                    |> Async.StartImmediate
            elif header.StartsWith("moodev-prop-content") then
                // Each line is "propname<TAB>literal" (see
                // `$vcs:ide_get_properties` - a real tab character, not
                // escaped text, since MOOcode string literals have no `\t`
                // escape). Only applied if this is still the inspector tab
                // showing - the user may have switched away before this
                // round-trip returned.
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        for line in lines do
                            let tabIdx = line.IndexOf('\t')

                            if tabIdx >= 0 then
                                let pname = line.Substring(0, tabIdx)
                                let literal = line.Substring(tabIdx + 1)

                                match Map.tryFind pname inspectorPropertyInputs with
                                | Some input ->
                                    input.value <- literal
                                    inspectorPropertyLastValues <- Map.add pname literal inspectorPropertyLastValues

                                    match Map.tryFind pname inspectorPropertyPreviews with
                                    | Some preview ->
                                        preview.textContent <- ""
                                        // Only bother rendering when there's an actual escape
                                        // byte to show - leaves the `<div>` empty (and so
                                        // hidden via style.css's `:empty` rule) for the
                                        // overwhelming majority of properties.
                                        if literal.IndexOf('\x1b') >= 0 || literal.IndexOf('\x07') >= 0 then
                                            Ansi.renderLiteralPreview literal |> Ansi.renderInto preview
                                    | None -> ()
                                | None -> ()
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-prop-result") then
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        let ok = headerField "ok: " header = Some "1"
                        inspectorDiagnosticsEl.textContent <- (if ok then "" else String.concat "\n" lines)
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-prop-add-result") then
                // A successful add needs a full inspector refresh (a new
                // row now exists) rather than just clearing diagnostics -
                // `loadInspector`'s own "always fresh" round-trip already
                // covers that, same as every other inspector action.
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        if headerField "ok: " header = Some "1" then
                            loadInspector objRef None
                        else
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-verb-add-result") then
                // Same shape as `moodev-prop-add-result` above - a
                // successful add needs a full inspector refresh (the new
                // verb row now exists).
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        if headerField "ok: " header = Some "1" then
                            loadInspector objRef None
                        else
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | None -> ()
            elif
                header.StartsWith("moodev-owner-set-result")
                || header.StartsWith("moodev-flag-set-result")
                || header.StartsWith("moodev-parent-add-result")
                || header.StartsWith("moodev-parent-remove-result")
                || header.StartsWith("moodev-name-set-result")
                || header.StartsWith("moodev-child-add-result")
            then
                // All six share the exact same "full inspector refresh on
                // success, diagnostics on failure" shape as every other
                // mutating inspector action.
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        if headerField "ok: " header = Some "1" then
                            loadInspector objRef None
                        else
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-verb-delete-result") then
                // No confirmation on the way in (see the inspector's own
                // per-row delete button) - trivial to recreate by hand if
                // this was a mistake, unlike recycling a whole object.
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef ->
                        if headerField "ok: " header = Some "1" then
                            // The tree's own `Verbs` list is a separate cached
                            // copy from what the inspector shows - always
                            // scrub it, not just when this object's inspector
                            // happens to be the active tab (see
                            // `removeTreeVerb`'s own comment).
                            removeTreeVerb objRef verb
                            renderTree ()

                            if openVerbTabs |> List.contains (objRef, verb) then
                                closeTab (objRef, verb)

                            if activeTab = InspectorTab objRef then
                                loadInspector objRef None
                        elif activeTab = InspectorTab objRef then
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-prop-delete-result") then
                match headerField "object: #" header, headerField "name: " header with
                | Some objNum, Some pname ->
                    match System.Int64.TryParse objNum with
                    | true, objRef ->
                        if headerField "ok: " header = Some "1" then
                            removeTreeProperty objRef pname
                            renderTree ()

                            if activeTab = InspectorTab objRef then
                                loadInspector objRef None
                        elif activeTab = InspectorTab objRef then
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-recycle-result") then
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef ->
                        if headerField "ok: " header = Some "1" then
                            // The object is gone - drop every open tab that
                            // referenced it (a verb tab, or its own
                            // inspector tab) and scrub it out of the tree,
                            // rather than leaving a dangling reference an
                            // unrelated click could still hit.
                            for o, v in openVerbTabs |> List.filter (fun (o, _) -> o = objRef) do
                                closeTab (o, v)

                            if openInspectorTabs |> List.contains objRef then
                                closeInspectorTab objRef

                            removeLiveNode objRef
                            renderTree ()
                        elif activeTab = InspectorTab objRef then
                            inspectorDiagnosticsEl.textContent <- String.concat "\n" lines
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-object-create-result") then
                if headerField "ok: " header = Some "1" then
                    match headerField "newobj: #" header, headerField "parent: #" header with
                    | Some newObjNum, Some parentNum ->
                        match System.Int64.TryParse newObjNum, System.Int64.TryParse parentNum with
                        | (true, newObj), (true, parentRef) ->
                            // Same round trip an ordinary tree-expand click
                            // triggers (see `renderTreeRows`'s own use of
                            // "get-live-children") - the `moodev-live-children`
                            // handler above folds the result into `treeNodes`,
                            // which the new object's inspector needs before
                            // `openOrSwitchToInspector` can show anything
                            // useful for it.
                            expandedRefs <- Set.add parentRef expandedRefs
                            sendAction [ "action" ==> "get-live-children"; "obj" ==> int parentRef ]
                            openOrSwitchToInspector newObj
                        | _ -> ()
                    | _ -> ()
                else
                    // No dedicated diagnostics area for the standalone "New
                    // Object" popover (unlike every other action here, which
                    // always has an open inspector tab to report into) - a
                    // modal is the simplest surface available.
                    window.alert (String.concat "\n" lines)
            elif header.StartsWith("moodev-live-children") then
                // Folds live (uncorponym'd, per moo-vcs-plan.md I3) children
                // into `treeNodes` exactly like a statically-preloaded
                // object - see `mergeLiveChildren`'s own comment. One JSON
                // object per line (nested verb/property arrays don't fit the
                // tab-separated convention `moodev-prop-content` uses for
                // flat rows), same envelope parsing as the outer `{header,
                // lines}` message itself.
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, parentRef ->
                        let children =
                            lines
                            |> Array.map (fun line ->
                                let o: obj = JS.JSON.parse line

                                int64 (o?objRef: float),
                                (o?name: string),
                                ((o?parents: float[]) |> Array.map int64),
                                ((o?verbs: obj[])
                                 |> Array.map (fun v ->
                                     { Name = v?name; Perms = v?perms; Dobj = v?dobj; Prep = v?prep; Iobj = v?iobj }
                                     : LspClient.TreeVerb)),
                                ((o?properties: obj[])
                                 |> Array.map (fun p -> { Name = p?name; Perms = p?perms }: LspClient.TreeProperty)))

                        mergeLiveChildren parentRef children
                        liveChildrenChecked <- Set.add parentRef liveChildrenChecked
                        renderTree ()
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-live-info") then
                // Inspector fallback for an object the static graph never
                // heard of (see `loadInspector`'s `None` arm) - same
                // `renderInspectorStructure` the LSP-sourced path uses,
                // unchanged, since this payload is shaped identically.
                match headerField "object: #" header with
                | Some objNum ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        match Array.tryHead lines with
                        | Some line ->
                            let info: obj = JS.JSON.parse line

                            if isNullOrUndefined info then
                                inspectorContentEl.textContent <- sprintf "#%d - not found." objRef
                            else
                                let highlightProp =
                                    activeInspectorProp |> Option.bind (fun (r, p) -> if r = objRef then Some p else None)

                                renderInspectorStructure objRef info highlightProp
                        | None -> inspectorContentEl.textContent <- sprintf "#%d - not found." objRef
                    | _ -> ()
                | None -> ()
            // "-result" (the ok:0 / error variants) checked before their
            // plain "-content"-shaped counterparts, since e.g.
            // "moodev-verb-history" is itself a string-prefix of
            // "moodev-verb-history-result" - checking the shorter one first
            // would swallow every error response too.
            elif header.StartsWith("moodev-verb-history-result") then
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = VerbTab(objRef, verb) && showingVerbHistory -> renderVerbHistoryList objRef verb []
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-verb-history") then
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = VerbTab(objRef, verb) && showingVerbHistory ->
                        let entries =
                            lines
                            |> Array.choose (fun line ->
                                let parts = line.Split('\t')

                                if parts.Length = 3 then
                                    match System.Int64.TryParse parts.[1] with
                                    | true, whenEpoch -> Some(parts.[0], whenEpoch, parts.[2])
                                    | false, _ -> None
                                else
                                    None)
                            |> List.ofArray

                        renderVerbHistoryList objRef verb entries
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-verb-at-commit-result") then
                () // verb not found at that commit - restore stays hidden, nothing more to show
            elif header.StartsWith("moodev-verb-at-commit") then
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = VerbTab(objRef, verb) && showingVerbHistory ->
                        let historicalCode = String.concat "\n" lines
                        currentHistoricalCode <- Some historicalCode
                        let diffEditor = getOrCreateHistoryDiffEditor ()
                        Monaco.setDiffModel diffEditor historicalCode (editor.getValue ())
                        verbHistoryRestoreBtn.setAttribute ("style", "")
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-search-result") then
                if activeTab = HistoryTab then
                    let results =
                        lines
                        |> Array.choose (fun line ->
                            let parts = line.Split('\t')

                            if parts.Length = 6 then
                                match System.Int64.TryParse parts.[1] with
                                | true, whenEpoch ->
                                    let objRefOpt =
                                        match System.Int64.TryParse parts.[2] with
                                        | true, n -> Some n
                                        | false, _ -> None

                                    Some(parts.[0], whenEpoch, objRefOpt, parts.[3], parts.[4], parts.[5])
                                | false, _ -> None
                            else
                                None)
                        |> List.ofArray

                    renderSearchResults results
            elif header.StartsWith("moodev-corponym-history") then
                if activeTab = HistoryTab then
                    let entries =
                        lines
                        |> Array.choose (fun line ->
                            let parts = line.Split('\t')

                            if parts.Length = 5 then
                                match System.Int64.TryParse parts.[1] with
                                | true, whenEpoch -> Some(parts.[0], whenEpoch, parts.[2], parts.[3], parts.[4])
                                | false, _ -> None
                            else
                                None)
                        |> List.ofArray

                    renderCorponymHistoryList entries
        else
            let text = decoder.decode (ev.data: obj)
            appendOutput text

Monaco.wireLsp
    (fun () -> currentVerbDoc ())
    (fun objRef verbName line col ->
        if activeTab = VerbTab(objRef, verbName) then
            // Same document (e.g. a local variable's definition, which
            // always targets the verb already open) - already loaded, so
            // the cursor can move right away; going through
            // `revealAndOpenVerb` would just no-op anyway (`switchToTab`
            // skips work when its argument already equals `activeTab`).
            editor.setPosition (createObj [ "lineNumber" ==> line; "column" ==> col ])
            editor.revealPositionInCenter (createObj [ "lineNumber" ==> line; "column" ==> col ])
        else
            // A different verb (a VerbCall dispatch jump) - `line`/`col`
            // are always (1,1) here server-side (`locationOfVerb` has no
            // per-statement spans to offer), which is where a freshly-
            // loaded verb's cursor starts anyway, so nothing more to do
            // once it's open.
            revealAndOpenVerb objRef verbName)
    (fun message -> editorDiagnosticsEl.textContent <- message)

inputEl.focus ()
