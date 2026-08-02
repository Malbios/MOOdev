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

document.title <- sprintf "MOOdy: %s" databaseName

let private outputEl = document.getElementById ("output")
let private inputEl = document.getElementById ("input") :?> HTMLInputElement

let private loginPaneEl = document.getElementById ("login-pane")
let private loginUserEl = document.getElementById ("login-user") :?> HTMLInputElement
let private loginPassEl = document.getElementById ("login-pass") :?> HTMLInputElement
let private loginConnectBtn = document.getElementById ("login-connect")

let private commandPaletteOverlayEl = document.getElementById ("command-palette-overlay")
let private commandPalettePanelEl = document.getElementById ("command-palette-panel")
let private commandPaletteInputEl = document.getElementById ("command-palette-input") :?> HTMLInputElement
let private commandPaletteListEl = document.getElementById ("command-palette-list")

let private connectionStatusEl = document.getElementById ("connection-status")
let private settingsBtn = document.getElementById ("settings-btn")
let private settingsOverlayEl = document.getElementById ("settings-overlay")
let private settingsPanelEl = document.getElementById ("settings-panel")
let private settingsCloseBtn = document.getElementById ("settings-close")
let private settingWordWrapEl = document.getElementById ("setting-wordwrap") :?> HTMLInputElement
let private settingFontSizeEl = document.getElementById ("setting-fontsize") :?> HTMLInputElement
let private settingMinimapEl = document.getElementById ("setting-minimap") :?> HTMLInputElement
let private settingForgetLoginBtn = document.getElementById ("setting-forget-login")
let private settingForgetLoginStatusEl = document.getElementById ("setting-forget-login-status")
let private settingMooHostEl = document.getElementById ("setting-moo-host") :?> HTMLInputElement
let private settingMooPortEl = document.getElementById ("setting-moo-port") :?> HTMLInputElement
let private settingMooLspBridgePortEl = document.getElementById ("setting-moo-lsp-bridge-port") :?> HTMLInputElement
let private settingMooTreeDirEl = document.getElementById ("setting-moo-tree-dir") :?> HTMLInputElement
let private settingMooSwitchBtn = document.getElementById ("setting-moo-switch")
let private settingMooSwitchStatusEl = document.getElementById ("setting-moo-switch-status")

let private layoutEl = document.getElementById ("layout")

let private activityBarEl = document.getElementById ("activity-bar")
let private viewTreeBtn = document.getElementById ("view-tree")
let private viewHistoryBtn = document.getElementById ("view-history")
let private viewTasksBtn = document.getElementById ("view-tasks")
let private viewServerStatusBtn = document.getElementById ("view-server-status")
let private viewErrorsBtn = document.getElementById ("view-errors")
let private viewDeadVerbsBtn = document.getElementById ("view-dead-verbs")
let private viewDeadPropertiesBtn = document.getElementById ("view-dead-properties")
let private viewGotchasBtn = document.getElementById ("view-gotchas")
let private viewPermissionRisksBtn = document.getElementById ("view-permission-risks")
let private viewDocsBtn = document.getElementById ("view-docs")
let private viewScratchpadBtn = document.getElementById ("view-scratchpad")
let private viewPropertySearchBtn = document.getElementById ("view-property-search")

let private sidebarEl = document.getElementById ("sidebar")
let private sidebarViewTreeEl = document.getElementById ("sidebar-view-tree")
let private treeFilterEl = document.getElementById ("tree-filter") :?> HTMLInputElement
let private treeFilterClearEl = document.getElementById ("tree-filter-clear")
let private treeFilterSettingsBtn = document.getElementById ("tree-filter-settings")
let private treeFilterSettingsPopoverEl = document.getElementById ("tree-filter-settings-popover")
let private treeNewObjectBtn = document.getElementById ("tree-new-object-btn")

let private treeFilterHideEmptyLeavesEl =
    document.getElementById ("tree-filter-hide-empty-leaves") :?> HTMLInputElement

let private treeColorRulesListEl = document.getElementById ("tree-color-rules-list")

let private treeListEl = document.getElementById ("tree-list")
let private sidebarResizerEl = document.getElementById ("sidebar-resizer")

let private staleTabWarningEl = document.getElementById ("stale-tab-warning")
let private staleTabWarningTextEl = document.getElementById ("stale-tab-warning-text")
let private staleTabWarningDismissBtn = document.getElementById ("stale-tab-warning-dismiss")
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
let private editorHistoryBtn = document.getElementById ("editor-history-btn")
let private verbHistoryCloseBtn = document.getElementById ("verb-history-close-btn")
let private editorCompareParentBtn = document.getElementById ("editor-compare-parent-btn")
let private verbParentDiffPaneEl = document.getElementById ("verb-parent-diff-pane")
let private verbParentDiffHeaderEl = document.getElementById ("verb-parent-diff-header")
let private verbParentDiffEditorEl = document.getElementById ("verb-parent-diff-editor")
let private verbParentDiffCloseBtn = document.getElementById ("verb-parent-diff-close-btn")
let private editorCallGraphEl = document.getElementById ("editor-call-graph")
let private sidebarViewHistoryEl = document.getElementById ("sidebar-view-history")
let private historySearchInputEl = document.getElementById ("history-search-input") :?> HTMLInputElement
let private historySearchResultsEl = document.getElementById ("history-search-results")
let private contentSearchInputEl = document.getElementById ("content-search-input") :?> HTMLInputElement
let private contentSearchResultsEl = document.getElementById ("content-search-results")
let private corponymHistoryListEl = document.getElementById ("corponym-history-list")
let private sidebarViewTasksEl = document.getElementById ("sidebar-view-tasks")
let private tasksListEl = document.getElementById ("tasks-list")
let private sidebarViewServerStatusEl = document.getElementById ("sidebar-view-server-status")
let private serverStatusListEl = document.getElementById ("server-status-list")
let private sidebarViewErrorsEl = document.getElementById ("sidebar-view-errors")
let private errorsListEl = document.getElementById ("errors-list")
let private errorsClearBtn = document.getElementById ("errors-clear-btn")
let private sidebarViewDeadVerbsEl = document.getElementById ("sidebar-view-dead-verbs")
let private treeDeadVerbsSummaryEl = document.getElementById ("tree-dead-verbs-summary")
let private treeDeadVerbsListEl = document.getElementById ("tree-dead-verbs-list")
let private sidebarViewDeadPropertiesEl = document.getElementById ("sidebar-view-dead-properties")
let private treeDeadPropertiesSummaryEl = document.getElementById ("tree-dead-properties-summary")
let private treeDeadPropertiesListEl = document.getElementById ("tree-dead-properties-list")
let private sidebarViewGotchasEl = document.getElementById ("sidebar-view-gotchas")
let private treeGotchasSummaryEl = document.getElementById ("tree-gotchas-summary")
let private treeGotchasListEl = document.getElementById ("tree-gotchas-list")
let private sidebarViewPermissionRisksEl = document.getElementById ("sidebar-view-permission-risks")
let private treePermissionRisksSummaryEl = document.getElementById ("tree-permission-risks-summary")
let private treePermissionRisksListEl = document.getElementById ("tree-permission-risks-list")
let private sidebarViewDocsEl = document.getElementById ("sidebar-view-docs")
let private docsSearchInputEl = document.getElementById ("docs-search-input") :?> HTMLInputElement
let private docsListEl = document.getElementById ("docs-list")
let private docsDetailEl = document.getElementById ("docs-detail")
let private sidebarViewScratchpadEl = document.getElementById ("sidebar-view-scratchpad")
let private scratchpadInputEl = document.getElementById ("scratchpad-input") :?> HTMLInputElement
let private scratchpadResultEl = document.getElementById ("scratchpad-result")
let private sidebarViewPropertySearchEl = document.getElementById ("sidebar-view-property-search")
let private propertySearchNameInputEl = document.getElementById ("property-search-name-input") :?> HTMLInputElement
let private propertySearchExprInputEl = document.getElementById ("property-search-expr-input") :?> HTMLInputElement
let private propertySearchResultsEl = document.getElementById ("property-search-results")
let private viewWatchBtn = document.getElementById ("view-watch")
let private sidebarViewWatchEl = document.getElementById ("sidebar-view-watch")
let private watchAddInputEl = document.getElementById ("watch-add-input") :?> HTMLInputElement
let private watchListEl = document.getElementById ("watch-list")

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
/// No dedicated toggle button - triggered by clicking the activity bar's own
/// already-active view icon again (see `onActivityBtnClick`), matching VS
/// Code's own activity-bar behavior.
module private Sidebar =
    let private collapsedKey = "moodev-sidebar-collapsed"

    let isCollapsed () : bool = sidebarEl.classList.contains "collapsed"

    let setCollapsed (collapsed: bool) : unit =
        if collapsed then
            sidebarEl.classList.add "collapsed"
            sidebarResizerEl.classList.add "collapsed"
        else
            sidebarEl.classList.remove "collapsed"
            sidebarResizerEl.classList.remove "collapsed"

        window.localStorage.setItem (collapsedKey, (if collapsed then "1" else "0"))

    let init () : unit = setCollapsed (window.localStorage.getItem collapsedKey = "1")

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

/// The client<->Sidecar WebSocket. `onopen`/`onclose`/`onerror`/`onmessage`
/// are wired further down (`onWsOpen`/`onWsClose`/`onWsError`/
/// `onWsMessage`), at the point in module-init order where everything
/// their bodies close over (`renderTree`, `Sidebar.init`, `switchToSidebarView`,
/// ...) already exists - `connectWebSocket` re-wires those same (unchanged)
/// handler functions onto a fresh socket on every reconnect, rather than
/// duplicating or relocating their bodies.
let mutable private ws: WebSocket = Unchecked.defaultof<WebSocket>

/// One of `Connected` / `Disconnected` (a deliberate teardown - the
/// "switch MOO target" reload flow, which is about to blow away this whole
/// page anyway) / `Reconnecting` (an unexpected drop, backing off before
/// the next `connectWebSocket` retry).
type private ConnState =
    | Connected
    | Disconnected
    | Reconnecting of attempt: int

let mutable private connState: ConnState = Disconnected

/// Set `true` only immediately before the one deliberate
/// `window.location.reload()` this client ever does (the "switch MOO
/// target" flow) - lets `onWsClose` tell "the socket died because the page
/// itself is about to die" apart from "the socket died out from under a
/// page that's still very much alive", the latter being the only case that
/// should ever schedule a reconnect.
let mutable private expectingTeardown = false

let private renderConnectionStatus () : unit =
    match connState with
    | Connected ->
        connectionStatusEl.textContent <- "connected"
        connectionStatusEl.className <- "connection-status status-connected"
    | Reconnecting attempt ->
        connectionStatusEl.textContent <- sprintf "reconnecting (attempt %d)..." attempt
        connectionStatusEl.className <- "connection-status status-reconnecting"
    | Disconnected ->
        connectionStatusEl.textContent <- "disconnected"
        connectionStatusEl.className <- "connection-status status-disconnected"

let mutable private onWsOpen: Event -> unit = fun _ -> ()
let mutable private onWsClose: Event -> unit = fun _ -> ()
let mutable private onWsError: Event -> unit = fun _ -> ()
let mutable private onWsMessage: MessageEvent -> unit = fun _ -> ()

let private connectWebSocket () : unit =
    ws <- WebSocket.Create(wsUrl)
    ws.binaryType <- "arraybuffer"
    ws.onopen <- fun ev -> onWsOpen ev
    ws.onclose <- fun ev -> onWsClose ev
    ws.onerror <- fun ev -> onWsError ev
    ws.onmessage <- fun ev -> onWsMessage ev

connectWebSocket ()

Monaco.registerMoocodeLanguage ()
Monaco.registerSnippetProvider ()
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

    let private loadString (key: string) (fallback: string) : string =
        match window.localStorage.getItem key with
        | null -> fallback
        | v -> v

    /// Default ON: once the tree includes the full object universe (not
    /// just verb-owners, like the old flat list), pure dead-ends (no
    /// children - stray/leftover objects) are almost always noise for
    /// day-to-day editing. Hiding them by default keeps the common case at
    /// least as compact as the old, familiar list; the checkbox lets a
    /// rarer "audit the whole database" session turn them back on. Read
    /// directly (not cached) since it only needs checking once per tree
    /// render, not on any hot path.
    let hideEmptyLeavesEnabled () : bool = loadString hideEmptyLeavesKey "on" = "on"

    let setHideEmptyLeaves (enabled: bool) : unit =
        window.localStorage.setItem (hideEmptyLeavesKey, (if enabled then "on" else "off"))

    let private colorRulesKey = "moodev-tree-color-rules"

    /// `int64` is a real JS `BigInt` under Fable - `JSON.stringify` throws on
    /// a bare `BigInt`, so `objRef` always round-trips through `float` here,
    /// same discipline every other objRef-over-the-wire spot in this
    /// codebase already follows (see `LspClient.fs`).
    let loadColorRules () : (int64 * string * string) list =
        match window.localStorage.getItem colorRulesKey with
        | null -> []
        | json ->
            try
                (unbox (JS.JSON.parse json): obj[])
                |> Array.map (fun o -> int64 (o?objRef: float), (o?label: string), (o?color: string))
                |> Array.toList
            with _ -> []

    let saveColorRules (rules: (int64 * string * string) list) : unit =
        rules
        |> List.map (fun (objRef, label, color) -> createObj [ "objRef" ==> float objRef; "label" ==> label; "color" ==> color ])
        |> Array.ofList
        |> JS.JSON.stringify
        |> fun json -> window.localStorage.setItem (colorRulesKey, json)

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
        treeFilterHideEmptyLeavesEl.``checked`` <- hideEmptyLeavesEnabled ()

        settingWordWrapEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingFontSizeEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingMinimapEl.onchange <- fun _ -> applyAndSaveFromControls ()
        // The hide-empty-leaves checkbox's onchange redraws the tree, not
        // just Monaco (unlike the three above) - wired separately, later in
        // this file, once `renderTree` exists (this module is defined
        // before it).

        settingForgetLoginBtn.onclick <-
            fun _ ->
                Login.forget ()
                settingForgetLoginStatusEl.textContent <- "Cleared"

    let show () : unit = settingsOverlayEl.classList.add "visible"
    let hide () : unit = settingsOverlayEl.classList.remove "visible"

settingsBtn.onclick <- fun _ -> Settings.show ()
settingsCloseBtn.onclick <- fun _ -> Settings.hide ()
staleTabWarningDismissBtn.onclick <- fun _ -> staleTabWarningEl.classList.add "hidden"
// Backdrop click closes the overlay; the panel stops its own clicks from
// bubbling to the backdrop, same "stop propagation so an inner click
// doesn't also trigger the outer handler" pattern `renderTabs`'s close-×
// button uses against its tab's own switch-click.
settingsOverlayEl.onclick <- fun _ -> Settings.hide ()
settingsPanelEl.onclick <- fun ev -> ev.stopPropagation () |> ignore

// Same "inner click stops propagation, outer click closes" idiom as the
// Settings overlay just above - `document` stands in for a dedicated
// backdrop element, since this is a small inline popover, not a full-screen
// modal.
treeFilterSettingsBtn.onclick <-
    fun ev ->
        ev.stopPropagation () |> ignore
        treeFilterSettingsPopoverEl.classList.toggle "visible" |> ignore

treeFilterSettingsPopoverEl.onclick <- fun ev -> ev.stopPropagation () |> ignore

document.onclick <- fun _ -> treeFilterSettingsPopoverEl.classList.remove "visible"

Settings.init ()

/// Which "tab" is showing in the main area - the game terminal, one of the
/// open verbs, or the object inspector. Game is a permanent, non-closable,
/// always-first tab (rendered as the static `#tab-game` button); every verb
/// or inspector ever opened this session gets its own closable tab
/// alongside it in `#verb-tabs`, VS Code-style (an inspector tab is labeled
/// "ⓘ #N" - see `renderTabs`). This is the single source of truth for both
/// "which tab is highlighted" and "what's loaded in the main pane" -
/// earlier versions of this file kept a separate `currentDocument` in sync
/// by hand; folding it into this type removes that duplication.
type private OpenTab =
    | GameTab
    | VerbTab of objRef: int64 * verbName: string
    | InspectorTab of objRef: int64

let mutable private activeTab: OpenTab = GameTab

/// Which view is showing in the sidebar - independent of `activeTab`/the
/// main pane, VS-Code-Explorer-style: switching views here never touches
/// what's open in the editor, and switching editor tabs never touches this.
/// Tree is the default, always-available view; the other four only ever
/// show real content once `isLoggedIn`.
type private SidebarView =
    | TreeView
    | HistoryView
    | TasksView
    | ServerStatusView
    | ErrorsView
    | DeadVerbsView
    | DeadPropertiesView
    | GotchasView
    | PermissionRisksView
    | DocsView
    | EvalScratchpadView
    | PropertySearchView
    | WatchView

let mutable private activeSidebarView: SidebarView = TreeView

/// The property name the most recent Property search was run with -
/// captured at dispatch time so the results handler can pass it through to
/// `renderPropertySearchResults` for the "highlight this property" click
/// behavior, without threading it through the wire response itself.
let mutable private lastPropertySearchName = ""

/// The full docs catalog (`moodev/getMoocodeDocs`), fetched at most once
/// per session - unlike every other sidebar view here, it's static for the
/// whole session (nothing about MOOcode's keywords/implicit variables/live
/// builtins changes without a server restart), so `switchToSidebarView`'s
/// `DocsView` arm only fetches when this is still `None`, then just filters
/// and re-renders the cached array locally on every later switch/search.
let mutable private moocodeDocsCache: (string * string * string * string)[] option = None

/// The full, fixed set of MOO error codes (`enum error`, ToastStunt
/// `include/structures.h:70-74`) - static reference content, merged into
/// `moocodeDocsCache` once fetched (see `switchToSidebarView`'s `DocsView`
/// arm) so it rides the docs sidebar's existing search/detail UI for free,
/// rather than a new sidebar view or a server round trip for 19 fixed
/// entries. One-line descriptions are ToastStunt's own (`unparse.cc`'s
/// `unparse_error`); "common causes" prose is authored here - no such
/// prose exists anywhere in the C source or `moocode-reference.md`.
let private errorCodeGlossary: (string * string * string * string)[] =
    [| "E_NONE", "E_NONE", "No error. Never actually raised - only ever appears as a placeholder/absence value.", "error"
       "E_TYPE",
       "E_TYPE",
       "Type mismatch. An operation got a value of the wrong type - e.g. adding a string to a number, or indexing a non-list/non-map.",
       "error"
       "E_DIV", "E_DIV", "Division by zero, or `x % 0` - check the divisor before dividing, or catch this explicitly.", "error"
       "E_PERM",
       "E_PERM",
       "Permission denied. The calling verb's permissions (not necessarily the connected player's) don't allow this operation - missing the read/write/execute bit, or not the owner/a wizard.",
       "error"
       "E_PROPNF",
       "E_PROPNF",
       "Property not found. The named property doesn't exist on this object or its ancestors - check spelling, or that `add_property()` actually ran.",
       "error"
       "E_VERBNF",
       "E_VERBNF",
       "Verb not found. No verb by that name is callable on this object (via `verb_info()`/a `:name()` call) - check spelling, aliases, or that the verb has its `x` bit set.",
       "error"
       "E_VARNF",
       "E_VARNF",
       "Variable not found - only ever raised by `properties()`/`property_info()`-adjacent introspection builtins on a bad variable/scope name, not by ordinary MOOcode variable use (an unset local is simply 0/blank).",
       "error"
       "E_INVIND",
       "E_INVIND",
       "Invalid indirection - tried to index into or call a verb on a value that isn't an object/list/map (e.g. `5:foo()` or `5[1]` where `5` is a plain number).",
       "error"
       "E_RECMOVE",
       "E_RECMOVE",
       "Recursive move - `move()` would place an object inside itself, directly or via one of its own contents.",
       "error"
       "E_MAXREC",
       "E_MAXREC",
       "Too many verb calls (or too deep an expression) - usually unbounded/runaway recursion between verbs.",
       "error"
       "E_RANGE",
       "E_RANGE",
       "Range error - a list/string index (or substring range) is out of bounds. `list[0]` is a classic cause (MOO indexing is 1-based).",
       "error"
       "E_ARGS",
       "E_ARGS",
       "Incorrect number of arguments - a builtin or a verb's own `{who, ?what, @rest} = args;` scatter got a call that doesn't fit its required/optional/rest shape.",
       "error"
       "E_NACC",
       "E_NACC",
       "Move refused by destination - the destination's `:accept()` (or `:enterfunc()`) verb returned false for this `move()`.",
       "error"
       "E_INVARG",
       "E_INVARG",
       "Invalid argument - a builtin's argument had the right type but an invalid value, e.g. `add_property()` on a name that already exists, or an out-of-range object number.",
       "error"
       "E_QUOTA",
       "E_QUOTA",
       "Resource limit (quota) exceeded - usually an object-creation/ownership quota (`.ownership_quota`) running out.",
       "error"
       "E_FLOAT",
       "E_FLOAT",
       "Floating-point arithmetic error - e.g. an overflow, or a result that isn't a valid float (like `0.0 / 0.0`).",
       "error"
       "E_FILE",
       "E_FILE",
       "File error - a FileIO builtin's path doesn't exist, isn't readable/writable, or is outside the server's configured FileIO root.",
       "error"
       "E_EXEC",
       "E_EXEC",
       "Exec error - the `exec()` builtin's external command failed to start or exited abnormally.",
       "error"
       "E_INTRPT", "E_INTRPT", "Interrupted - the running task was explicitly killed (`kill_task()`) or interrupted before it finished.", "error" |]

/// Which property, if any, is the specific sub-focus within the currently
/// shown inspector - set alongside `selectedObjRef` whenever a caller asks
/// to land on a specific property row, cleared (`None`) by any plain
/// `openOrSwitchToInspector` that didn't ask for one. Read by
/// `renderInspectorStructure` to scroll to and flash that property's row.
let mutable private activeInspectorProp: (int64 * string) option = None

/// Which object the inspector currently shows (or last showed) - set
/// alongside `activeTab` by `openOrSwitchToInspectorWith`, whether reached
/// via a tree click or an owner/parent/child link inside the inspector
/// itself, so the tree's own highlight always follows wherever the
/// inspector is actually pointed. Kept independent of `activeTab` itself so
/// the highlight survives switching the main pane away to Game or a verb
/// tab, instead of disappearing the moment the inspector isn't the active
/// tab. Read by `renderTreeRows` to highlight this object's row.
let mutable private selectedObjRef: int64 option = None

/// Whether the currently-active `VerbTab`'s editor pane is showing its
/// history/diff view instead of the normal Monaco editor - orthogonal to
/// `activeTab` itself (it's a sub-mode of a `VerbTab`, not a distinct tab),
/// reset to `false` on every tab switch so opening/reactivating a verb tab
/// always starts back in the normal editor view.
let mutable private showingVerbHistory = false

/// Same sub-mode idea as `showingVerbHistory` above, for the "compare to
/// parent" diff view - mutually exclusive with it (only one of the two
/// diff panes can replace the normal editor at a time), also reset to
/// `false` on every tab switch. `parentDiffAncestorRef` is the ancestor
/// object this tab's own verb would compare against, resolved lazily
/// whenever the plain editor view for a `VerbTab` is shown (see
/// `updateCompareParentButton`) - `None` until that resolution completes,
/// or if no ancestor defines its own copy of this verb name at all.
let mutable private showingParentDiff = false
let mutable private parentDiffAncestorRef: int64 option = None

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

/// Render order for the tab strip (verb + inspector tabs, drag-reorderable) -
/// a view-order overlay on top of `openVerbTabs`/`openInspectorTabs`, which
/// remain the source of truth for membership and preview-tab bookkeeping.
/// Game is never in here - it's a static button outside the draggable
/// strip. Mirrors every append/replace/remove those two lists already go
/// through (`renderTabs`' drag handlers are the only place this is
/// reordered arbitrarily).
let mutable private tabOrder: OpenTab list = []

/// The tab currently mid-drag (`renderTabs`' drag-and-drop handlers), or
/// `None` when nothing is being dragged.
let mutable private draggedTab: OpenTab option = None

/// In-memory, session-only log of received `#0:handle_uncaught_error`/
/// `handle_task_timeout` events - newest first. Not persisted; a page
/// reload starts fresh, same as every other purely-client-side list here.
let mutable private errorLog: (System.DateTime * string * string list) list = []

/// Most-recently-active-first history of tabs actually switched away from
/// (across every kind - Game/Verb), so closing a tab can fall back to
/// whatever was genuinely active right before it, not just "the next one
/// over" in that tab's own kind-specific list. Pushed to by `switchToTab`
/// itself; `closeTab` consumes it via `isTabStillOpen` when picking a
/// fallback.
let mutable private tabHistory: OpenTab list = []

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

/// Each currently-rendered inspector's structured-editor toggle button and
/// its (hidden until toggled on) container `<div>`, by property name -
/// populated by `renderInspectorStructure`. The toggle button's own
/// visibility (only shown when the raw text looks list/map-shaped) is
/// updated both here at row-build time and by the `moodev-prop-content`
/// handler once the live value arrives; the container is read by the
/// `moodev-property-literal-parsed` handler to know where to render the
/// parsed rows for that property. Mirrors `inspectorPropertyInputs`'s own
/// population/reset points.
let mutable private inspectorPropertyStructuredToggles: Map<string, HTMLElement * HTMLElement> = Map.empty

/// Whether raw property-value text looks like it might be a list/map literal
/// worth offering structured editing for - a cheap client-side prefilter
/// (real confirmation comes from the server actually parsing it) so the
/// toggle button doesn't show for values that obviously aren't one.
let private looksListOrMapShaped (text: string) : bool =
    let trimmed = text.Trim()
    trimmed.StartsWith("{") || trimmed.StartsWith("[")

/// Whether raw property-value text looks like a waif's `toliteral()` shape
/// (`[[class = #N, owner = #N]]` - the only representation a waif ever
/// renders as, per ToastStunt `list.cc:511-513`; there's no real waif
/// literal syntax to parse, so this is a dedicated prefilter rather than an
/// extension of `parsePropertyLiteral`, which correctly falls through to
/// `NotAListOrMap` for this text today). Checked before the generic
/// `looksListOrMapShaped` (which is also true here, since `[[` starts with
/// `[`) so the toggle click can route to the waif-specific live fetch
/// instead of the plain list/map text parse.
let private looksWaifShaped (text: string) : bool = text.Trim().StartsWith("[[")

/// Each open tab's last-known content - populated when a verb is first
/// fetched, refreshed with the live editor value right before switching
/// away from it. Lets switching between already-open tabs be instant (no
/// server round-trip) and lets a closed-then-reopened-in-the-same-session
/// tab... actually no, closing drops its cache entry too (see `closeTab`) -
/// this only ever holds *currently open* tabs' content.
let mutable private tabContent: Map<int64 * string, string> = Map.empty

/// Per-line column offset between the *raw* verb source (what the LSP's
/// last-saved AST is positioned against) and the *displayed* buffer (what
/// `editor.getPosition()` reports) - `delta.[i] = displayedIndent.[i] -
/// rawIndent.[i]` for 0-based line `i`, since `Monaco.reindentLinesActionId`
/// only ever rewrites a line's leading whitespace, never adds/removes lines
/// or touches anything past the first non-whitespace character. No entry
/// (or a line index past the array's end) means "no adjustment needed" -
/// same as today's unadjusted behavior, never worse. Computed once per
/// fetch (see `moodev-edit-content`'s handler), reset to "no adjustment"
/// right after a successful save (the server's raw source becomes exactly
/// what's displayed at that instant), and cleared on tab close, same
/// lifecycle as `tabContent` above. Consumed by `LspClient.wire`'s
/// `getIndentDelta` callback.
let mutable private tabIndentDeltas: Map<int64 * string, int[]> = Map.empty

/// Leading whitespace character count of `line` - `0` for an empty line
/// (matches `indentationRules`' own treatment: nothing to offset).
let private leadingWhitespaceLength (line: string) : int =
    let trimmed = line.TrimStart(' ', '\t')
    line.Length - trimmed.Length

/// Builds `tabIndentDeltas`' entry for `(objRef, verbName)` by comparing
/// `rawLines` (the server's own text, exactly as fetched) against the
/// editor's current (just-reindented) model content line-by-line. Silently
/// skips (leaves no entry - "no adjustment") if the line counts somehow
/// differ, since that would mean reindent-lines did something beyond
/// leading-whitespace rewriting and the whole per-line-offset premise no
/// longer holds - safer to fall back to unadjusted than to compute garbage.
let private recordIndentDelta (objRef: int64) (verbName: string) (rawLines: string list) (model: Monaco.ITextModel) : unit =
    let lineCount = model.getLineCount ()

    if lineCount = List.length rawLines then
        let delta =
            rawLines
            |> List.mapi (fun i rawLine ->
                let displayedLine = model.getLineContent (i + 1)
                leadingWhitespaceLength displayedLine - leadingWhitespaceLength rawLine)
            |> Array.ofList

        tabIndentDeltas <- Map.add (objRef, verbName) delta tabIndentDeltas

/// The `int[] option` `LspClient.wire`'s `getIndentDelta` callback needs -
/// `None` for "not tracked" (never fetched this session, or explicitly
/// reset after a save), which every position-conversion call site already
/// treats the same as "no adjustment."
let private getIndentDeltaFor (objRef: int64) (verbName: string) : int[] option =
    Map.tryFind (objRef, verbName) tabIndentDeltas

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

/// Round-trips `activeTab` through a small tagged JSON shape for
/// `persistTabs`/`loadPersistedTabs` below - `GameTab` has no payload;
/// `VerbTab`/`InspectorTab` carry their objRef (through `float`, the
/// established int64-over-JSON discipline throughout this file) and, for
/// verbs, the verb name.
let private encodeActiveTab (tab: OpenTab) : obj =
    match tab with
    | GameTab -> createObj [ "kind" ==> "game" ]
    | VerbTab(o, v) -> createObj [ "kind" ==> "verb"; "obj" ==> float o; "verb" ==> v ]
    | InspectorTab o -> createObj [ "kind" ==> "inspector"; "obj" ==> float o ]

let private persistTabsKey = "moodev-open-tabs"

/// Persists which tabs are open and which is active, so a page reload can
/// restore the layout (see `restorePersistedTabs`) - same
/// `JS.JSON.stringify`/localStorage idiom `Settings.saveColorRules` already
/// uses for a list-shaped value. Called wherever `openVerbTabs`/
/// `openInspectorTabs`/`activeTab` actually change, not on a timer.
///
/// The preview tab (`previewTab`/`previewInspectorTab`, italicized, not yet
/// double-click-pinned) is deliberately excluded from what gets saved - it's
/// disposable by design (the next sidebar click/go-to-definition replaces it
/// in place), so a reload shouldn't resurrect one the user never pinned. If
/// it's also the active tab, `active` falls back to `GameTab` rather than
/// persisting a reference to a tab that's no longer in the saved list.
/// Best-effort "what does this object look like right now" - looked up from
/// `treeNodes` (the live tree), `""` if this object isn't (yet) known to it.
/// `""` is also what a stale-check comparison treats as "nothing to compare
/// against" (see `staleTabWarnings` below), so an unknown label never
/// produces a false-positive warning. A plain forward-declared function
/// value, not a direct call into `treeNodes` - `treeNodes` itself is
/// declared much further down (where the rest of the tree-building code
/// lives), after this point in the file, and ordinary top-level bindings
/// can't forward-reference like a `let rec ... and ...` group can (same
/// reasoning `onWsOpen`/`onWsClose`/etc. above use for the WebSocket
/// handlers). Assigned its real body right after `treeNodes` is declared.
let mutable private currentLiveLabel: int64 -> string = fun _ -> ""

let private persistTabs () : unit =
    let persistedVerbTabs = openVerbTabs |> List.filter (fun t -> Some t <> previewTab)
    let persistedInspectorTabs = openInspectorTabs |> List.filter (fun o -> Some o <> previewInspectorTab)

    let persistedActive =
        match activeTab with
        | VerbTab(o, v) when previewTab = Some(o, v) -> GameTab
        | InspectorTab o when previewInspectorTab = Some o -> GameTab
        | other -> other

    let payload =
        createObj
            [ "verbTabs"
              ==> (persistedVerbTabs
                   |> List.map (fun (o, v) -> createObj [ "obj" ==> float o; "verb" ==> v; "label" ==> currentLiveLabel o ])
                   |> Array.ofList)
              "inspectorTabs"
              ==> (persistedInspectorTabs |> List.map (fun o -> createObj [ "obj" ==> float o; "label" ==> currentLiveLabel o ]) |> Array.ofList)
              "active" ==> encodeActiveTab persistedActive ]

    window.localStorage.setItem (persistTabsKey, JS.JSON.stringify payload)

/// What `restorePersistedTabs` needs to reopen a saved layout - `Active`
/// mirrors `OpenTab` but stays a plain decoded value rather than the
/// original `OpenTab` itself, since restoring a verb/inspector tab is a
/// multi-step, often-async process (a live `fetch-verb`/`get-live-info`
/// round trip), not a single `switchToTab` call.
type private PersistedActiveTab =
    | PersistedGame
    | PersistedVerb of objRef: int64 * verbName: string
    | PersistedInspector of objRef: int64

type private PersistedTabs =
    { VerbTabs: (int64 * string * string) list
      InspectorTabs: (int64 * string) list
      Active: PersistedActiveTab }

let private loadPersistedTabs () : PersistedTabs option =
    match window.localStorage.getItem persistTabsKey with
    | null -> None
    | json ->
        try
            let parsed: obj = JS.JSON.parse json

            // `label` predates tabs persisted before this field existed - a
            // genuinely missing JS property reads back as `undefined`, not
            // `null` (confirmed live: an old persisted-tabs blob without
            // `label` produced the literal string "undefined" in the stale-
            // tab warning before this guard used `isNullOrUndefined`, which
            // catches both). Reads back as `""`, same "nothing to compare
            // against" degradation `currentLiveLabel` itself uses.
            let label (v: obj) =
                let l = v?label
                if isNullOrUndefined l then "" else (unbox<string> l)

            let verbTabs =
                (parsed?verbTabs: obj[])
                |> Array.map (fun v -> int64 (v?obj: float), (v?verb: string), label v)
                |> Array.toList

            let inspectorTabs =
                (parsed?inspectorTabs: obj[]) |> Array.map (fun v -> int64 (v?obj: float), label v) |> Array.toList

            let activeObj: obj = parsed?active

            let active =
                match (activeObj?kind: string) with
                | "verb" -> PersistedVerb(int64 (activeObj?obj: float), (activeObj?verb: string))
                | "inspector" -> PersistedInspector(int64 (activeObj?obj: float))
                | _ -> PersistedGame

            Some { VerbTabs = verbTabs; InspectorTabs = inspectorTabs; Active = active }
        with _ -> None

/// The `(objRef, verb) option` shape a couple of call sites still need
/// (`saveIfDirty`, `Monaco.wireLsp`'s hover/definition callback) - derived
/// from `activeTab` rather than tracked separately.
let private currentVerbDoc () : (int64 * string) option =
    match activeTab with
    | VerbTab(o, v) -> Some(o, v)
    | GameTab
    | InspectorTab _ -> None

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

/// The "Live watch dashboard" panel's own persisted state - a plain list of
/// user-typed MOO expressions (`moodev-watch-expressions` in localStorage,
/// same JSON-array-of-strings convention `Settings.loadColorRules`/
/// `saveColorRules` use for a richer shape), plus the last batch of results
/// received for them (positionally matched by index - see
/// `IdeActions.evalWatchBatch`'s own comment on why the wire reports them
/// back in request order rather than tagged pairs).
let private watchExprsKey = "moodev-watch-expressions"

let private loadWatchExprs () : string list =
    match window.localStorage.getItem watchExprsKey with
    | null -> []
    | json ->
        try
            (unbox (JS.JSON.parse json): string[]) |> Array.toList
        with _ ->
            []

let private saveWatchExprs (exprs: string list) : unit =
    window.localStorage.setItem (watchExprsKey, JS.JSON.stringify (Array.ofList exprs))

let mutable private watchExprs: string list = loadWatchExprs ()
let mutable private watchValues: string[] = [||]
let mutable private watchIntervalId: int option = None

/// Renders the current watch list - one row per expression, showing its
/// last-received value (or "..." before the first tick ever completes).
let rec private renderWatchList () : unit =
    watchListEl.innerHTML <- ""

    watchExprs
    |> List.iteri (fun i expr ->
        let li = document.createElement ("li")
        li.classList.add "picker-item"

        let label = document.createElement ("span")
        let value = if i < watchValues.Length then watchValues.[i] else "..."
        label.textContent <- sprintf "%s = %s" expr value

        let removeBtn = document.createElement ("button") :?> HTMLButtonElement
        removeBtn.classList.add "picker-fix-btn"
        removeBtn.textContent <- "×"
        removeBtn.title <- "Remove"

        removeBtn.onclick <-
            fun ev ->
                ev.stopPropagation () |> ignore
                watchExprs <- watchExprs |> List.indexed |> List.filter (fun (idx, _) -> idx <> i) |> List.map snd
                watchValues <- [||]
                saveWatchExprs watchExprs
                renderWatchList ()

        li.appendChild label |> ignore
        li.appendChild removeBtn |> ignore
        watchListEl.appendChild li |> ignore)

/// One refresh tick - a no-op with nothing watched yet, so an idle Watch
/// panel (or one nobody's opened this session) never sends anything.
let private tickWatch () : unit =
    if not (List.isEmpty watchExprs) then
        sendAction [ "action" ==> "eval-watch-batch"; "exprs" ==> (watchExprs |> Array.ofList) ]

/// Ticks only while the Watch panel is the active sidebar view - started/
/// stopped from `switchToSidebarView` below, same "only refresh what's
/// actually visible" reasoning every other on-demand panel in this file
/// already follows, just on a timer instead of a single fetch.
let private startWatchInterval () : unit =
    if watchIntervalId.IsNone then
        tickWatch ()
        watchIntervalId <- Some(JS.setInterval tickWatch 3000)

let private stopWatchInterval () : unit =
    watchIntervalId |> Option.iter JS.clearInterval
    watchIntervalId <- None

// Wired here rather than alongside the tree-filter-settings popover wiring
// above (which is plain top-level code that runs before `sendAction` itself
// is even defined) - F# requires a name to be lexically defined before use
// for ordinary top-level bindings, unlike the `let rec ... and ...` chain
// `renderTree`/`loadInspector`/etc. below belong to.
//
// No parent/name/flags prompt up front - creates a bare, parentless object
// immediately (`$nothing`/`#-1`, the same "no parent" idiom
// moocode-reference.md documents for `create()`) and lets the existing
// `moodev-object-create-result` handler open it straight into the
// inspector, where the user sets name/parent/flags via the exact same
// affordances already used for every other object (rename pencil, flags
// row, Parents "+").
treeNewObjectBtn.onclick <-
    fun _ -> sendAction [ "action" ==> "create-object"; "parentExpr" ==> "#-1" ]

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

/// Verb tabs with a save currently in flight (server hasn't responded yet).
/// Lets a second attempt to save the same tab - e.g. blur firing a save,
/// then that tab's own close button clicked right after, per `closeTab`'s
/// own comment on DOM event ordering - await the *same* request instead of
/// sending a redundant second compile.
let mutable private saveInFlight: Set<int64 * string> = Set.empty

/// Verb tabs whose most recent save response was a compile failure -
/// cleared on that tab's next successful save, and on a fresh `fetchVerb`
/// load (see the `moodev-edit-content`/`moodev-edit-result` handlers).
/// Lets `closeTab` answer "is it safe to discard this tab" for a
/// *background* tab with no network round trip - its own last save already
/// resolved back when the user blurred away from it.
let mutable private failedSaveTabs: Set<int64 * string> = Set.empty

/// Continuations waiting to learn a specific in-flight save's outcome -
/// resolved and cleared by the `moodev-edit-result` handler once that tab's
/// response arrives. A list, not a single slot, since more than one caller
/// can end up waiting on the same in-flight save (see `saveIfDirtyAsync`).
let mutable private pendingSaveResolvers: Map<int64 * string, (bool -> unit) list> = Map.empty

/// The continuation waiting on the current `"reconfigure-target"` request's
/// `moodev-reconfigure-target-result`, if one is in flight - a single slot,
/// not a map keyed by anything, since only one "Switch & Reload" click can
/// meaningfully be in flight at a time (the button's own handler awaits this
/// before doing anything else).
let mutable private pendingReconfigureResolver: (bool * string -> unit) option = None

/// Which verb needs saving and what its content was, captured at the exact
/// moment of the edit that dirtied it (every `onDidChangeModelContent`
/// firing, below) - not read later, on demand, by whatever eventually calls
/// `saveIfDirtyAsync`. `activeTab`/`editor.getValue()` are NOT safe to read
/// at blur time: confirmed live that a click transferring focus away from
/// Monaco can already have run its own handler - changing `activeTab` and,
/// for a verb-to-verb switch, overwriting the shared editor's buffer via
/// `setValue` - *before* the blur callback's body executes, so reading
/// either "live" from inside the blur handler can silently pick up the
/// *new* tab's identity and/or content instead of the one actually being
/// blurred away from. Capturing both together here, at the instant of the
/// keystroke that caused them, is immune to that race entirely. `None`
/// exactly when `isDirty` is `false` (cleared together, in `setDirty`).
let mutable private dirtySave: (int64 * string * string) option = None

/// Handle of the pending debounced live-diagnostics check (`None` when
/// nothing's scheduled) - cancelled and re-armed on every real edit, and
/// cancelled outright by `setDirty false` (a fresh load or a successful
/// save both mean there's nothing left to check for).
let mutable private syntaxCheckTimer: int option = None

/// The single place `isDirty` ever changes - also keeps the status bar's
/// dirty/saved indicator in sync, so every call site gets that for free
/// instead of needing to remember to update the status bar itself.
let private setDirty (value: bool) : unit =
    isDirty <- value
    statusDirtyEl.textContent <- if value then "Modified" else "No changes"

    if value then
        statusDirtyEl.classList.add "modified"
    else
        statusDirtyEl.classList.remove "modified"

    if not value then
        dirtySave <- None
        syntaxCheckTimer |> Option.iter JS.clearTimeout
        syntaxCheckTimer <- None

/// Debounced (~800ms idle) as-you-type compile probe - coexists with
/// (doesn't replace) the existing save-time check. Takes the exact
/// `(objRef, verbName, code)` snapshot the triggering edit already computed
/// (`dirtySave`, right above) rather than re-reading it when the timer
/// fires: if the user switches tabs before the delay elapses, `setDirty
/// false`'s own cancellation (above) already invalidates the old timer, but
/// capturing the snapshot up front also means a *new* edit in the same tab
/// re-arms against its own latest text, never a stale one.
let private scheduleSyntaxCheck (target: int64 * string * string) : unit =
    syntaxCheckTimer |> Option.iter JS.clearTimeout

    let objRef, verbName, code = target

    syntaxCheckTimer <-
        Some(
            JS.setTimeout
                (fun () ->
                    syntaxCheckTimer <- None

                    sendAction
                        [ "action" ==> "check-verb-syntax"
                          "obj" ==> int objRef
                          "verb" ==> verbName
                          "code" ==> codeLines code ])
                800
        )

editor.onDidChangeModelContent (fun _ ->
    setDirty true

    dirtySave <- currentVerbDoc () |> Option.map (fun (o, v) -> (o, v, editor.getValue ()))
    dirtySave |> Option.iter scheduleSyntaxCheck)
|> ignore

/// Autosaves the currently-open verb if it's actually been edited since it
/// was loaded or last saved (see `isDirty`'s own comment), resolving to
/// whether the save actually succeeded - `true` if there was nothing to
/// save either. Unlike the old `saveIfDirty`, this never clears `isDirty`
/// itself - only the `moodev-edit-result` handler does that, on a
/// *confirmed* `ok: 1` - and never sends a second request for a tab that
/// already has one in flight; a second caller just awaits the existing one
/// via `pendingSaveResolvers`. (A save whose response arrives after the
/// user has typed further edits during the round trip won't incorrectly
/// clear those newer edits' dirty state - `onDidChangeModelContent` will
/// already have set `isDirty` back to `true` by then.)
///
/// Wired to the editor losing focus entirely (`onDidBlurEditorWidget`, not
/// `onDidBlurEditorText` - the latter also fires when focus merely moves to
/// one of the editor's own widgets, like the find box, which isn't
/// "leaving" the editor at all), fire-and-forget - blurring to switch tabs
/// stays exactly as fast as before, no waiting. `closeTab` below is the one
/// caller that actually awaits this, since closing (unlike switching) is
/// destructive.
///
/// Reads only `dirtySave` (see its own comment for why `activeTab`/
/// `editor.getValue()` themselves are unsafe here) - and, like it, decides
/// synchronously the instant this function is *called*, not deferred into
/// whenever the returned `Async<bool>` happens to be started/awaited. Only
/// the "wait for the response" part is genuinely async.
let private saveIfDirtyAsync () : Async<bool> =
    match dirtySave with
    | None -> async { return true }
    | Some(objRef, verb, code) ->
        let key = (objRef, verb)

        if not (Set.contains key saveInFlight) then
            saveInFlight <- Set.add key saveInFlight
            sendAction [ "action" ==> "save-verb"; "obj" ==> int objRef; "verb" ==> verb; "code" ==> codeLines code ]

        Async.FromContinuations(fun (resolve, _, _) ->
            let existing = pendingSaveResolvers |> Map.tryFind key |> Option.defaultValue []
            pendingSaveResolvers <- Map.add key (resolve :: existing) pendingSaveResolvers)

editor.onDidBlurEditorWidget (fun () -> saveIfDirtyAsync () |> Async.Ignore |> Async.StartImmediate) |> ignore

// Keeps the status bar's cursor-position readout live.
editor.onDidChangeCursorPosition (fun ev ->
    let line: int = ev?position?lineNumber
    let col: int = ev?position?column
    statusPositionEl.textContent <- sprintf "Ln %d, Col %d" line col)
|> ignore

setDirty false
statusPositionEl.textContent <- "Ln 1, Col 1"

/// All four mutually-exclusive panes under `#main-pane` - `showPaneFor`
/// activates exactly one (or, for a `VerbTab` in history mode, two: the
/// verb-history pane replaces the plain editor pane, everything else stays
/// hidden the same way).
let private allPanes =
    [ terminalPaneEl
      editorPaneEl
      verbHistoryPaneEl
      verbParentDiffPaneEl
      inspectorPaneEl ]

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
    | VerbTab _ when showingParentDiff -> activateOnly verbParentDiffPaneEl
    | VerbTab _ ->
        activateOnly editorPaneEl
        // The container was `display:none` a moment ago - force Monaco to
        // re-measure rather than rely on ResizeObserver picking this up.
        editor.layout ()
        editor.focus ()
    | InspectorTab _ -> activateOnly inspectorPaneEl

/// The mutually-exclusive views under `#sidebar` - same `activateOnly`
/// pattern as `allPanes`/`showPaneFor` above, one level down.
let private allSidebarViews =
    [ sidebarViewTreeEl
      sidebarViewHistoryEl
      sidebarViewTasksEl
      sidebarViewServerStatusEl
      sidebarViewErrorsEl
      sidebarViewDeadVerbsEl
      sidebarViewDeadPropertiesEl
      sidebarViewGotchasEl
      sidebarViewPermissionRisksEl
      sidebarViewDocsEl
      sidebarViewScratchpadEl
      sidebarViewPropertySearchEl
      sidebarViewWatchEl ]

let private activateOnlySidebarView (viewEl: HTMLElement) : unit =
    for v in allSidebarViews do
        if v = viewEl then v.classList.add "active" else v.classList.remove "active"

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

let mutable private parentDiffEditor: Monaco.IDiffEditor option = None

/// Same lazy-creation reasoning as `getOrCreateHistoryDiffEditor` above, a
/// separate Monaco instance since it lives in its own pane
/// (`verb-parent-diff-pane`, distinct from the history pane's own).
let private getOrCreateParentDiffEditor () : Monaco.IDiffEditor =
    match parentDiffEditor with
    | Some e -> e
    | None ->
        let e = Monaco.createDiffEditor verbParentDiffEditorEl
        parentDiffEditor <- Some e
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

// The only place `showingVerbHistory` is ever set to `true` - everything
// else needed to show a verb's history (the pane itself, the
// `moodev-verb-history`/`verb-at-commit` response handlers, the diff
// editor, the restore button) was already fully built, but nothing ever
// flipped this on or fired the initial fetch, so the pane could never
// actually open. Only reachable while a `VerbTab` is showing the plain
// editor (see `showPaneFor` - this button lives inside `#editor-pane`,
// which is exactly that state), so `activeTab` is always a `VerbTab` here.
editorHistoryBtn.onclick <-
    fun _ ->
        match activeTab with
        | VerbTab(objRef, verbName) ->
            verbHistoryListEl.innerHTML <- "<li>Loading...</li>"
            verbHistoryRestoreBtn.setAttribute ("style", "display:none")
            currentHistoricalCode <- None
            showingVerbHistory <- true
            showPaneFor activeTab
            sendAction [ "action" ==> "verb-history"; "obj" ==> int objRef; "verb" ==> verbName ]
        | GameTab
        | InspectorTab _ -> ()

verbHistoryCloseBtn.onclick <-
    fun _ ->
        showingVerbHistory <- false
        showPaneFor activeTab

// Only reachable while a `VerbTab` is showing the plain editor and
// `updateCompareParentButton` has already resolved a real ancestor for it
// (the button stays hidden otherwise - see that function) - so both
// `activeTab` and `parentDiffAncestorRef` are always populated here.
editorCompareParentBtn.onclick <-
    fun _ ->
        match activeTab, parentDiffAncestorRef with
        | VerbTab(objRef, verbName), Some ancestorRef ->
            verbParentDiffHeaderEl.textContent <- sprintf "Comparing to parent #%d" ancestorRef
            showingVerbHistory <- false
            showingParentDiff <- true
            showPaneFor activeTab
            sendAction [ "action" ==> "verb-at-parent"; "obj" ==> int ancestorRef; "verb" ==> verbName ]
        | _ -> ()

verbParentDiffCloseBtn.onclick <-
    fun _ ->
        showingParentDiff <- false
        showPaneFor activeTab

/// Snapshots whatever's currently in the editor into `tabContent`, if the
/// active tab is a verb - called right before navigating away from it.
let private cacheCurrentEditorContent () : unit =
    match activeTab with
    | VerbTab(o, v) -> tabContent <- Map.add (o, v) (editor.getValue ()) tabContent
    | GameTab
    | InspectorTab _ -> ()

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

/// One in-memory node per object, built once from `LspClient.getObjectTreeAsync`'s
/// flat response at login - keyed by objRef (`treeNodes`) so parent/child
/// lookups don't re-scan the array. The tree itself only ever displays
/// objects (verbs/properties live in the object inspector instead - see
/// `loadInspector`), so this only tracks the structural shape - plus
/// `HasOwnContent`, which doesn't drive any row rendering, only whether
/// `flattenVisibleRows`' "hide empty leaves" filter considers this object a
/// genuine dead end (see its own comment).
type private TreeNode =
    { ObjRef: int64
      Name: string
      Parents: int64[]
      Children: int64[]
      HasOwnContent: bool
      /// Own verb names only (not inherited) - just enough for the command
      /// palette's jump-to-verb search (Part 6). The login payload already
      /// carries full `TreeVerb[]` per object; this is the only field this
      /// type previously discarded everything from except `HasOwnContent`.
      Verbs: string[] }

let mutable private treeNodes: Map<int64, TreeNode> = Map.empty

currentLiveLabel <- fun objRef -> treeNodes |> Map.tryFind objRef |> Option.map (fun n -> n.Name) |> Option.defaultValue ""

/// A user-defined "color this object and everything descending from it in
/// the tree" rule (see `colorForObject` below) - set/cleared from an
/// object's own inspector, reviewed/removed from the tree's "Tree display
/// options" popover. `TypeLabel` is captured once at rule-creation time from
/// the inspector's own current header text, so the popover's rule list reads
/// exactly like the rest of the UI rather than re-deriving a label.
type private ColorRule = { TypeObjRef: int64; TypeLabel: string; Color: string }

let mutable private colorRules: ColorRule list =
    Settings.loadColorRules () |> List.map (fun (r, l, c) -> { TypeObjRef = r; TypeLabel = l; Color = c })

let private saveColorRulesToStorage () : unit =
    Settings.saveColorRules (colorRules |> List.map (fun r -> r.TypeObjRef, r.TypeLabel, r.Color))

/// Closest-ancestor-wins distance from `current` up to `target` via
/// `treeNodes`'s `Parents` chain (recursive walk, cycle-guarded) - `None` if
/// `target` isn't reachable (not an ancestor, or its subtree was never
/// loaded into `treeNodes`).
let rec private ancestryDistance (visited: Set<int64>) (current: int64) (target: int64) (depth: int) : int option =
    if current = target then
        Some depth
    elif Set.contains current visited then
        None
    else
        match Map.tryFind current treeNodes with
        | Some node ->
            node.Parents
            |> Array.choose (fun p -> ancestryDistance (Set.add current visited) p target (depth + 1))
            |> Array.sortBy id
            |> Array.tryHead
        | None -> None

/// The color to render `objRef`'s tree row with, if any rule's type object
/// is `objRef` itself or an ancestor of it - ties broken by whichever rule's
/// type object is *closest* (fewest parent-hops), matching how a more
/// specific MOO class overrides a broader one.
let private colorForObject (objRef: int64) : string option =
    if List.isEmpty colorRules then
        None
    else
        colorRules
        |> List.choose (fun rule -> ancestryDistance Set.empty objRef rule.TypeObjRef 0 |> Option.map (fun d -> d, rule.Color))
        |> List.sortBy fst
        |> List.tryHead
        |> Option.map snd

/// True roots of the object tree - objects with zero parents (`$root_class`
/// and a handful of others, confirmed against the real corpus rather than
/// assumed: `parents(obj)` already returns `{}` for a parentless object,
/// no sentinel ref filtering needed).
let mutable private rootRefs: int64[] = [||]

let private buildTree
    (nodes: (int64 * string * int64[] * int64[] * LspClient.TreeVerb[] * LspClient.TreeProperty[])[])
    : unit =
    // Verbs/properties are still part of the wire shape (the tree's own
    // login-time fetch is shared with other consumers), but the tree itself
    // no longer displays them - see `TreeNode`'s own comment. Only whether
    // there are any at all survives, for the "hide empty leaves" check.
    treeNodes <-
        nodes
        |> Array.map (fun (objRef, name, parents, children, verbs, properties) ->
            objRef,
            { ObjRef = objRef
              Name = name
              Parents = parents
              Children = children
              HasOwnContent = not (Array.isEmpty verbs) || not (Array.isEmpty properties)
              Verbs = verbs |> Array.map (fun v -> v.Name) })
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
                      HasOwnContent = not (Array.isEmpty verbs) || not (Array.isEmpty properties)
                      Verbs = verbs |> Array.map (fun v -> v.Name) }
                    treeNodes

    match Map.tryFind parentRef treeNodes with
    | None -> ()
    | Some parentNode ->
        treeNodes <- Map.add parentRef { parentNode with Children = children |> Array.map (fun (r, _, _, _, _) -> r) } treeNodes

/// Folds a `get-live-roots` response into `treeNodes`/`rootRefs` - the
/// top-level counterpart to `mergeLiveChildren` above. `rootRefs` is
/// otherwise only ever computed once, from the static corponym export
/// (`buildTree`), so a parentless live object (confirmed live: the LSP's own
/// `#4`/`#5` bootstrap objects) would never have any discovery path at all
/// without this - unlike a live child, there's no "already-known parent" to
/// have expanded to reveal it. Adds any not-yet-known object exactly like
/// `mergeLiveChildren` does, then unions every returned ref into `rootRefs`
/// (deduplicated - repeat calls, e.g. on every login, are idempotent).
let private mergeLiveRoots (roots: (int64 * string * int64[] * LspClient.TreeVerb[] * LspClient.TreeProperty[])[]) : unit =
    for objRef, name, parents, verbs, properties in roots do
        if not (Map.containsKey objRef treeNodes) then
            treeNodes <-
                Map.add
                    objRef
                    { ObjRef = objRef
                      Name = name
                      Parents = parents
                      Children = [||]
                      HasOwnContent = not (Array.isEmpty verbs) || not (Array.isEmpty properties)
                      Verbs = verbs |> Array.map (fun v -> v.Name) }
                    treeNodes

    rootRefs <- Array.append rootRefs (roots |> Array.map (fun (r, _, _, _, _) -> r)) |> Array.distinct

/// The removal-side counterpart to `mergeLiveChildren`/`mergeLiveRoots`
/// above, for a recycled object: drops it from `treeNodes` entirely, scrubs
/// it out of every remaining node's `Children` list (it may appear under
/// more than one parent, same DAG reasoning as `ancestorsOf`), and prunes it
/// from `rootRefs` if it was a top-level entry - rather than waiting for a
/// stale entry to self-heal on that parent's next expand (roots have no
/// "next expand" to self-heal from, so this is the only cleanup they get).
let private removeLiveNode (objRef: int64) : unit =
    treeNodes <-
        treeNodes
        |> Map.remove objRef
        |> Map.map (fun _ node -> { node with Children = node.Children |> Array.filter ((<>) objRef) })

    rootRefs <- rootRefs |> Array.filter ((<>) objRef)

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

/// Objects whose live children have been asked for at least once (a
/// `get-live-children` round trip has landed, whether or not it turned up
/// anything new). `isExpandable` only shows a chevron for actually-known
/// children now, so an object whose *only* children are live-only (created
/// outside the client's own create-object flow, which already re-checks its
/// new object's parent immediately) needs some other path to ever surface
/// them - see `liveChildrenRequested` just below, which drives that
/// check automatically rather than waiting for a chevron the node no longer
/// shows. Same reset lifecycle as `expandedRefs`.
let mutable private liveChildrenChecked: Set<int64> = Set.empty

/// Objects an automatic `get-live-children` request has already been sent
/// for, whether or not the round trip has landed yet - guards
/// `requestUncheckedLeaves` (called on every `renderTree`, i.e. every
/// expand/collapse/filter keystroke) against re-sending the same request
/// over and over while the first one is still in flight. Same reset
/// lifecycle as `liveChildrenChecked`.
let mutable private liveChildrenRequested: Set<int64> = Set.empty

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

/// BFS ancestor depths for the inheritance graph (Part 7) - the full
/// upward chain, unlike `ancestorsOf`'s flat set: an ancestor reachable via
/// more than one path (diamond inheritance) keeps its *shallowest* depth,
/// matching how it'd naturally sit in a layered top-down drawing. Also
/// returns every (moreDerived, ancestor) edge actually walked, deduped -
/// each real parent link becomes exactly one line in the rendered graph.
let private ancestorLayers (rootRef: int64) : Map<int64, int> * (int64 * int64) list =
    let mutable depths = Map.empty
    let mutable edges = []
    let mutable frontier = [ rootRef ]
    let mutable depth = 0

    while not (List.isEmpty frontier) do
        let next =
            frontier
            |> List.collect (fun objRef ->
                match Map.tryFind objRef treeNodes with
                | None -> []
                | Some node ->
                    node.Parents
                    |> Array.toList
                    |> List.map (fun p ->
                        edges <- (objRef, p) :: edges
                        p))
            |> List.filter (fun p -> not (Map.containsKey p depths))
            |> List.distinct

        depth <- depth + 1

        for p in next do
            depths <- Map.add p depth depths

        frontier <- next

    depths, edges

/// Direct children only (Part 7's graph goes one level down, not the whole
/// reachable-descendant set, which has no natural bound the way the
/// ancestor chain does) - as `(child, rootRef)` pairs, matching
/// `ancestorLayers`' own `(moreDerived, ancestor)` edge direction so both
/// edge lists can be drawn by the same generic "connect the lower node's
/// top to the upper node's bottom" logic.
let private directChildEdges (rootRef: int64) : (int64 * int64) list =
    match Map.tryFind rootRef treeNodes with
    | None -> []
    | Some node -> node.Children |> Array.toList |> List.map (fun c -> c, rootRef)

/// Reveals `lastFilterSelectedObjRef` (if anything was selected while
/// filtering) by merging its own ancestor path into the persistent
/// `expandedRefs` - the same set a plain click on an already-visible object
/// would touch, just computed for the whole path at once instead of one
/// click per level. A no-op if nothing was selected (e.g. the user typed a
/// search and cleared it without ever clicking a result) - there's nothing
/// to preserve in that case, which is the point: only an explicit selection
/// survives the clear, not every match.
let private promoteFilterExpansionIfAny () : unit =
    match lastFilterSelectedObjRef with
    | None -> ()
    | Some objRef -> expandedRefs <- Set.union expandedRefs (Set.add objRef (ancestorsOf Set.empty objRef))

/// Live filter text, updated on every keystroke in the tree's filter box -
/// see the `oninput` wiring below.
let mutable private treeFilterText = ""

/// One row of the flattened, currently-visible tree: an object, its depth,
/// and whether it has anything to expand into. Children sit directly under
/// their parent, one depth deeper, once it's expanded - no separate
/// "Children" grouping node.
type private TreeRow = int64 * int * bool

/// Builds the structured (list/map) inline editor for one property's value
/// cell, replacing the plain `<input>` with one row per element/pair - a
/// text input each (plus a key input too, for a map), a `🗑` per row
/// mirroring `renderObjRefList`'s own add/remove-row idiom, and a "+ Add
/// element" row. "Done" re-serializes every row's current text back into one
/// literal-text string and sends the ordinary `set-property` action (so the
/// MOO-side format is untouched - only this parse/re-render round trip is
/// new), then reverts to the plain `<input>` view; "Cancel" reverts without
/// saving anything.
let private renderStructuredEditor
    (objRef: int64)
    (pname: string)
    (input: HTMLInputElement)
    (toggleBtn: HTMLElement)
    (container: HTMLElement)
    (isMap: bool)
    (elements: obj[])
    : unit =
    container.innerHTML <- ""

    let rows = ResizeArray<HTMLInputElement option * HTMLInputElement>()
    let rowsEl = document.createElement ("div")
    rowsEl.classList.add "inspector-structured-rows"

    let addRow (keyText: string option) (valueText: string) : unit =
        let rowEl = document.createElement ("div")
        rowEl.classList.add "inspector-structured-row"

        let keyInput =
            keyText
            |> Option.map (fun kt ->
                let ki = document.createElement ("input") :?> HTMLInputElement
                ki.classList.add "inspector-property-value"
                ki.value <- kt
                rowEl.appendChild ki |> ignore

                let arrow = document.createElement ("span")
                arrow.textContent <- " -> "
                rowEl.appendChild arrow |> ignore
                ki)

        let valueInput = document.createElement ("input") :?> HTMLInputElement
        valueInput.classList.add "inspector-property-value"
        valueInput.value <- valueText
        rowEl.appendChild valueInput |> ignore

        let removeBtn = document.createElement ("button")
        removeBtn.classList.add "inspector-row-delete-btn"
        removeBtn.textContent <- "🗑"
        removeBtn.title <- "Remove element"

        removeBtn.onclick <-
            fun _ ->
                rows.Remove((keyInput, valueInput)) |> ignore
                rowsEl.removeChild rowEl |> ignore

        rowEl.appendChild removeBtn |> ignore
        rowsEl.appendChild rowEl |> ignore
        rows.Add((keyInput, valueInput))

    for el in elements do
        if isMap then
            addRow (Some(el?key: string)) (el?value: string)
        else
            addRow None (unbox el: string)

    let addElementBtn = document.createElement ("button")
    addElementBtn.classList.add "inspector-add-property-btn"
    addElementBtn.textContent <- "+ Add element"
    addElementBtn.onclick <- fun _ -> addRow (if isMap then Some "" else None) ""

    let actionsEl = document.createElement ("div")
    actionsEl.classList.add "inspector-structured-actions"

    let revertToPlainInput () : unit =
        container.setAttribute ("style", "display:none")
        input.removeAttribute "style"
        toggleBtn.setAttribute ("style", (if looksListOrMapShaped input.value then "" else "display:none"))

    let doneBtn = document.createElement ("button")
    doneBtn.textContent <- "Done"

    doneBtn.onclick <-
        fun _ ->
            let combined =
                if isMap then
                    rows
                    |> Seq.map (fun (k, v) -> (Option.get k).value + " -> " + v.value)
                    |> String.concat ", "
                    |> sprintf "[%s]"
                else
                    rows |> Seq.map (fun (_, v) -> v.value) |> String.concat ", " |> sprintf "{%s}"

            input.value <- combined
            inspectorPropertyLastValues <- Map.add pname combined inspectorPropertyLastValues
            sendAction [ "action" ==> "set-property"; "obj" ==> int objRef; "name" ==> pname; "valueExpr" ==> combined ]
            revertToPlainInput ()

    let cancelBtn = document.createElement ("button")
    cancelBtn.textContent <- "Cancel"
    cancelBtn.onclick <- fun _ -> revertToPlainInput ()

    actionsEl.appendChild doneBtn |> ignore
    actionsEl.appendChild cancelBtn |> ignore

    container.appendChild rowsEl |> ignore
    container.appendChild addElementBtn |> ignore
    container.appendChild actionsEl |> ignore

    input.setAttribute ("style", "display:none")
    toggleBtn.setAttribute ("style", "display:none")
    container.removeAttribute "style"

/// Renders `get-waif-properties`' results into the same structured-editor
/// slot `renderStructuredEditor` uses for list/map, for a waif-shaped
/// property value. Deliberately not shaped like a batched Done/Cancel -
/// there's no single literal to reconstruct from the rows (no waif literal
/// syntax exists at all), so each row saves itself immediately via
/// `"set-waif-property"` instead. No "+ Add element" either - a waif's own
/// properties are fixed by its class, unlike a list/map's element count.
let private renderWaifEditor
    (objRef: int64)
    (pname: string)
    (input: HTMLInputElement)
    (toggleBtn: HTMLElement)
    (container: HTMLElement)
    (elements: obj[])
    : unit =
    container.innerHTML <- ""

    let rowsEl = document.createElement ("div")
    rowsEl.classList.add "inspector-structured-rows"

    let revertToPlainInput () : unit =
        container.setAttribute ("style", "display:none")
        input.removeAttribute "style"
        toggleBtn.removeAttribute "style"

    for el in elements do
        let waifPropName = el?name: string
        let waifPropValue = el?value: string

        let rowEl = document.createElement ("div")
        rowEl.classList.add "inspector-structured-row"

        let nameEl = document.createElement ("span")
        nameEl.textContent <- waifPropName + ": "
        rowEl.appendChild nameEl |> ignore

        let valueInput = document.createElement ("input") :?> HTMLInputElement
        valueInput.classList.add "inspector-property-value"
        valueInput.value <- waifPropValue
        rowEl.appendChild valueInput |> ignore

        let saveBtn = document.createElement ("button")
        saveBtn.textContent <- "Save"

        saveBtn.onclick <-
            fun _ ->
                sendAction
                    [ "action" ==> "set-waif-property"
                      "obj" ==> int objRef
                      "name" ==> pname
                      "waifProp" ==> waifPropName
                      "valueExpr" ==> valueInput.value ]

        rowEl.appendChild saveBtn |> ignore
        rowsEl.appendChild rowEl |> ignore

    let closeBtn = document.createElement ("button")
    closeBtn.textContent <- "Close"
    closeBtn.onclick <- fun _ -> revertToPlainInput ()

    container.appendChild rowsEl |> ignore
    container.appendChild closeBtn |> ignore

    input.setAttribute ("style", "display:none")
    toggleBtn.setAttribute ("style", "display:none")
    container.removeAttribute "style"

/// Switches the main area to `tab`, caching whatever was showing before the
/// switch. A no-op if `tab` is already active (e.g. clicking the tab you're
/// already on).
let rec private switchToTab (tab: OpenTab) : unit =
    if tab <> activeTab then
        cacheCurrentEditorContent ()
        tabHistory <- activeTab :: (tabHistory |> List.filter (fun t -> t <> activeTab))
        activeTab <- tab
        showingVerbHistory <- false
        showingParentDiff <- false

        match tab with
        | GameTab
        | InspectorTab _ -> ()
        | VerbTab(o, v) ->
            editor.setValue (Map.find (o, v) tabContent)
            // setValue above just re-fired onDidChangeModelContent - this
            // is a tab switch, not a user edit.
            setDirty false
            updateCompareParentButton o v
            updateCallGraph o v

        showPaneFor tab
        renderTabs ()
        renderTree ()
        persistTabs ()

and private isTabStillOpen (tab: OpenTab) : bool =
    match tab with
    | GameTab -> true
    | VerbTab(o, v) -> openVerbTabs |> List.contains (o, v)
    | InspectorTab o -> openInspectorTabs |> List.contains o

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

/// Tries each of `parents` in turn (declaration order - the same order real
/// MOO dispatch would eventually reach them in), asking
/// `moodev/resolveEffectiveMember` which ancestor's own copy of `verbName`
/// would win *starting the search from that parent* - the first `Some`
/// found is the "old" version this object's own override is shadowing.
/// Deliberately not resolved from the object itself (that call always
/// trivially returns the object's own copy, per `resolveEffectiveMember`'s
/// own doc comment) - starting from each parent in turn is what actually
/// finds the ancestor whose behavior would apply if this object's own copy
/// didn't exist.
and private tryParentsForVerb (verbName: string) (parents: int64 list) : Async<int64 option> =
    async {
        match parents with
        | [] -> return None
        | p :: rest ->
            let! winner = LspClient.resolveEffectiveMemberAsync p "verb" verbName

            match winner with
            | Some w -> return Some w
            | None -> return! tryParentsForVerb verbName rest
    }

/// Shows/hides the editor status bar's "Compare to parent" button for
/// whichever `VerbTab(objRef, verbName)` is now active, and resolves which
/// ancestor it should compare against (`parentDiffAncestorRef`) - called
/// once whenever the plain editor view for a verb tab is shown (see
/// `showPaneFor`'s `VerbTab _` arm). Hidden immediately (synchronously) so
/// switching tabs never briefly shows stale state from the previous verb
/// while the real answer is still in flight.
and private updateCompareParentButton (objRef: int64) (verbName: string) : unit =
    editorCompareParentBtn.setAttribute ("style", "display:none")
    parentDiffAncestorRef <- None

    match Map.tryFind objRef treeNodes with
    | None -> ()
    | Some node ->
        if not (Array.isEmpty node.Parents) then
            async {
                let! ancestor = tryParentsForVerb verbName (node.Parents |> Array.toList)

                // Guards against a stale response landing after the user
                // has already switched to a different verb tab - same
                // `activeTab` check `annotateShadowedMember` uses.
                if activeTab = VerbTab(objRef, verbName) then
                    match ancestor with
                    | Some ancestorRef ->
                        parentDiffAncestorRef <- Some ancestorRef
                        editorCompareParentBtn.removeAttribute "style"
                        editorCompareParentBtn.title <- sprintf "Compare to parent's copy (#%d)" ancestorRef
                    | None -> ()
            }
            |> Async.StartImmediate

/// Renders `getCallGraph`'s result for `(objRef, verbName)` into
/// `container` - one-hop callers above, the verb itself in the middle,
/// one-hop callees below, laid out in horizontal layers by the exact same
/// hand-rolled SVG approach `renderInheritanceGraph` already established
/// (see its own doc comment on why: no SVG/diagram npm dependency anywhere
/// in this codebase). Node identity here is a verb, not an object, so
/// nodes are keyed by `(int64 * string)` pairs (object ref, verb name)
/// throughout rather than a bare `int64` - kept as its own copy of the
/// layout math rather than sharing `renderInheritanceGraph`'s (which is
/// hardcoded to bare object-ref nodes) to avoid risking that already-shipped
/// view while adding this one. Skips rendering entirely when there's
/// nothing beyond the verb itself (no resolvable callers or callees) - an
/// empty graph would just be noise.
and private renderCallGraph
    (container: HTMLElement)
    (rootRef: int64)
    (rootVerbName: string)
    (callees: (int64 * string)[])
    (callers: (int64 * string)[])
    : unit =
    container.innerHTML <- ""
    let rootKey = rootRef, rootVerbName
    let calleeList = callees |> Array.toList |> List.distinct
    let callerList = callers |> Array.toList |> List.distinct

    let allNodeLayers =
        ([ for c in callerList -> c, -1 ] @ [ rootKey, 0 ] @ [ for c in calleeList -> c, 1 ])
        |> List.distinctBy fst

    if List.length allNodeLayers > 1 then
        let labelFor (key: int64 * string) : string =
            let o, v = key
            let objLabel = Map.tryFind o treeNodes |> Option.map (fun n -> n.Name) |> Option.defaultValue (sprintf "#%d" o)
            sprintf "%s:%s" objLabel v

        let nodeHeight = 26.0
        let hGap = 14.0
        let vGap = 46.0
        let topPadding = 10.0
        let sidePadding = 10.0
        let charWidth = 6.3

        let widthFor (key: int64 * string) : float = max 50.0 (float (labelFor key).Length * charWidth + 16.0)

        let layers =
            allNodeLayers
            |> List.groupBy snd
            |> List.map (fun (layer, entries) -> layer, entries |> List.map fst)
            |> List.sortBy fst

        let minLayer = layers |> List.map fst |> List.min

        let layerTotalWidth (keys: (int64 * string) list) : float =
            (keys |> List.sumBy widthFor) + hGap * float (List.length keys - 1)

        let svgWidth = (layers |> List.map (snd >> layerTotalWidth) |> List.max) + sidePadding * 2.0
        let svgHeight = topPadding * 2.0 + nodeHeight + vGap * float (List.length layers - 1)

        let positions =
            layers
            |> List.collect (fun (layer, keys) ->
                let totalWidth = layerTotalWidth keys
                let startX = (svgWidth - totalWidth) / 2.0
                let y = topPadding + float (layer - minLayer) * vGap

                keys
                |> List.fold
                    (fun (x, acc) key ->
                        let w = widthFor key
                        x + w + hGap, (key, (x, y, w)) :: acc)
                    (startX, [])
                |> snd)
            |> Map.ofList

        let svgNs = "http://www.w3.org/2000/svg"
        let svg = document.createElementNS (svgNs, "svg")
        svg.setAttribute ("width", string svgWidth)
        svg.setAttribute ("height", string svgHeight)
        svg.setAttribute ("class", "inspector-graph-svg")

        let edges = (callerList |> List.map (fun c -> c, rootKey)) @ (calleeList |> List.map (fun c -> rootKey, c))

        for fromKey, toKey in edges do
            match Map.tryFind fromKey positions, Map.tryFind toKey positions with
            | Some(fx, fy, fw), Some(tx, ty, tw) ->
                let line = document.createElementNS (svgNs, "line")
                line.setAttribute ("x1", string (fx + fw / 2.0))
                line.setAttribute ("y1", string fy)
                line.setAttribute ("x2", string (tx + tw / 2.0))
                line.setAttribute ("y2", string (ty + nodeHeight))
                line.setAttribute ("class", "inspector-graph-edge")
                svg.appendChild line |> ignore
            | _ -> ()

        for KeyValue((o, v), (x, y, w)) in positions do
            let g = document.createElementNS (svgNs, "g")

            g.setAttribute (
                "class",
                (if (o, v) = rootKey then
                     "inspector-graph-node inspector-graph-node-root"
                 else
                     "inspector-graph-node")
            )

            g?onclick <- fun (_: Event) -> openOrSwitchToVerb o v

            let rect = document.createElementNS (svgNs, "rect")
            rect.setAttribute ("x", string x)
            rect.setAttribute ("y", string y)
            rect.setAttribute ("width", string w)
            rect.setAttribute ("height", string nodeHeight)
            rect.setAttribute ("rx", "4")
            g.appendChild rect |> ignore

            let text = document.createElementNS (svgNs, "text")
            text.setAttribute ("x", string (x + w / 2.0))
            text.setAttribute ("y", string (y + nodeHeight / 2.0 + 4.0))
            text.setAttribute ("text-anchor", "middle")
            text.textContent <- labelFor (o, v)
            g.appendChild text |> ignore

            svg.appendChild g |> ignore

        let title = document.createElement ("div")
        title.classList.add "inspector-section-title"
        title.textContent <- "Call graph"
        container.appendChild title |> ignore
        container.appendChild svg |> ignore

/// Fetches and renders the call graph for whichever `VerbTab(objRef,
/// verbName)` is now active - called once whenever the plain editor view
/// for a verb tab is shown, same call sites as `updateCompareParentButton`.
/// Clears immediately (synchronously) so switching tabs never briefly shows
/// the previous verb's stale graph while the real answer is still in
/// flight.
and private updateCallGraph (objRef: int64) (verbName: string) : unit =
    editorCallGraphEl.innerHTML <- ""

    async {
        let! callees, callers = LspClient.getCallGraphAsync objRef verbName

        if activeTab = VerbTab(objRef, verbName) then
            renderCallGraph editorCallGraphEl objRef verbName callees callers
    }
    |> Async.StartImmediate

/// Actually tears down an open verb tab - no save-state check at all. Only
/// safe to call once it's already known there's nothing worth saving left
/// (a server-confirmed verb/object delete - that content is gone either
/// way) or the caller (`closeTab` below) has already decided discarding is
/// fine. If it was the active one, falls back to whatever tab was
/// genuinely active right before it (`tabHistory`, skipping anything no
/// longer open), or Game if history has nothing valid left.
and private closeTabImmediate (objRef: int64, verbName: string) : unit =
    let key = (objRef, verbName)
    let wasActive = activeTab = VerbTab(objRef, verbName)
    openVerbTabs <- openVerbTabs |> List.filter (fun t -> t <> (objRef, verbName))
    tabOrder <- tabOrder |> List.filter (fun t -> t <> VerbTab(objRef, verbName))
    if previewTab = Some(objRef, verbName) then previewTab <- None
    saveInFlight <- Set.remove key saveInFlight
    failedSaveTabs <- Set.remove key failedSaveTabs
    pendingSaveResolvers <- Map.remove key pendingSaveResolvers
    tabIndentDeltas <- Map.remove key tabIndentDeltas

    if wasActive then
        // `switchToTab` below still sees `activeTab = VerbTab(objRef,
        // verbName)` and will re-cache its editor content into
        // `tabContent` as part of the switch - harmless, since the removal
        // right after discards it again, but it must come *after* the
        // switch, not before, or `switchToTab` would resurrect the entry
        // this is trying to delete.
        let fallback = tabHistory |> List.tryFind isTabStillOpen |> Option.defaultValue GameTab
        switchToTab fallback
        tabContent <- Map.remove (objRef, verbName) tabContent
    else
        tabContent <- Map.remove (objRef, verbName) tabContent
        renderTabs ()
        renderTree ()
        persistTabs ()

/// Closes an open verb tab from user-initiated action (the tab strip's ×
/// button or middle-click, via `closeAction` below) - unlike
/// `closeTabImmediate`, this actually checks whether there's a compile-
/// failed save that would be silently discarded, and asks first:
///   - the *active* tab awaits its real, current save outcome (triggering
///     one via `saveIfDirtyAsync` if none is already in flight - e.g. from
///     the blur that just fired switching focus to this close button)
///   - a *background* tab's last save already resolved back when the user
///     blurred away from it, so `failedSaveTabs` already has the answer,
///     no round trip needed
and private closeTab (objRef: int64, verbName: string) : unit =
    async {
        let key = (objRef, verbName)

        let! ok =
            if activeTab = VerbTab(objRef, verbName) then
                saveIfDirtyAsync ()
            else
                async { return not (Set.contains key failedSaveTabs) }

        if ok || window.confirm "This verb has unsaved changes that failed to compile. Close and discard them?" then
            closeTabImmediate (objRef, verbName)
    }
    |> Async.StartImmediate

/// Closes an open inspector tab. If it was the active one, falls back the
/// same way `closeTab` does (`tabHistory`, or Game if nothing valid is
/// left) - and, per `loadInspector`'s "always fresh" rule, re-loads
/// whichever inspector tab it falls back to rather than showing whatever
/// that tab last happened to render.
and private closeInspectorTab (objRef: int64) : unit =
    let wasActive = activeTab = InspectorTab objRef
    openInspectorTabs <- openInspectorTabs |> List.filter (fun r -> r <> objRef)
    tabOrder <- tabOrder |> List.filter (fun t -> t <> InspectorTab objRef)
    if previewInspectorTab = Some objRef then previewInspectorTab <- None

    // The object's tree row stays "selected" independently of `activeTab`
    // (see `selectedObjRef`'s own comment) - but with its inspector gone,
    // there's nothing left for that selection to point at, so it shouldn't
    // outlive the tab that justified it.
    if selectedObjRef = Some objRef then
        selectedObjRef <- None
        activeInspectorProp <- None

    if wasActive then
        let fallback = tabHistory |> List.tryFind isTabStillOpen |> Option.defaultValue GameTab
        switchToTab fallback

        match fallback with
        | InspectorTab o -> openOrSwitchToInspectorWith o None
        | GameTab
        | VerbTab _ -> ()
    else
        renderTabs ()
        renderTree ()
        persistTabs ()

/// Opens `objRef`'s inspector - switches instantly if it's already an open
/// tab (adding it first if not), then *always* kicks off a fresh load
/// (structural info + live property values), even when the tab was already
/// open and already active. Used by the tab strip itself, the tree's
/// object rows, and every clickable owner/parent/child link inside the
/// inspector pane - all funnel through here (via
/// `openOrSwitchToInspector`/`openOrSwitchToInspectorWith` below) so
/// "already open" and "always fresh" are each handled in exactly one
/// place. `highlightProp`, when `Some`, is forwarded to `loadInspector` to
/// scroll to and flash that property's row once the table renders.
and private openOrSwitchToInspectorWith (objRef: int64) (highlightProp: string option) : unit =
    if not (openInspectorTabs |> List.contains objRef) then
        // Same preview-tab replacement `moodev-edit-content` does for verb
        // tabs (see `previewTab`'s own comment) - replace the current
        // preview inspector tab in place if there is one, otherwise append.
        match previewInspectorTab with
        | Some oldPreview ->
            let idx = openInspectorTabs |> List.findIndex (fun r -> r = oldPreview)
            openInspectorTabs <- openInspectorTabs |> List.mapi (fun i r -> if i = idx then objRef else r)
            tabOrder <- tabOrder |> List.map (fun t -> if t = InspectorTab oldPreview then InspectorTab objRef else t)
        | None ->
            openInspectorTabs <- openInspectorTabs @ [ objRef ]
            tabOrder <- tabOrder @ [ InspectorTab objRef ]

        previewInspectorTab <- Some objRef

    selectedObjRef <- Some objRef
    activeInspectorProp <- highlightProp |> Option.map (fun p -> (objRef, p))
    switchToTab (InspectorTab objRef)
    loadInspector objRef highlightProp

/// `openOrSwitchToInspectorWith objRef None` - every existing call site
/// (the tab strip, the tree's object rows, owner/parent/child links) goes
/// through this, unchanged.
and private openOrSwitchToInspector (objRef: int64) : unit = openOrSwitchToInspectorWith objRef None

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

/// A permissions popover widget - a toggle button showing the current
/// letters (or "(none)"), and a popover with one tooltipped checkbox per
/// `(label, letter, tooltip)` - the shared shape behind every permission
/// editor in this pane (add-property, add-verb, and the per-field editors
/// on existing rows). Returns the widget, the individual (letter,
/// checkbox) pairs (so a caller can hook into one specifically - e.g. the
/// property add-row's Chown-hides-owner wiring - without losing that), and
/// the aggregate `currentPerms` reader. `onChange` fires (after the
/// widget's own label refresh) on every checkbox change - a no-op `fun ()
/// -> ()` for callers that don't need anything extra.
and private mkPermsWidget
    (options: (string * string * string) list)
    (initialPerms: string)
    (onChange: unit -> unit)
    : HTMLElement * (string * HTMLInputElement) list * (unit -> string) =
    let widget = document.createElement ("div")
    widget.classList.add "inspector-perms-widget"

    let toggleBtn = document.createElement ("button")
    toggleBtn.classList.add "pane-action-btn"
    toggleBtn.title <- "Permissions"

    let popover = document.createElement ("div")
    popover.classList.add "tree-filter-settings-popover"
    popover.onclick <- fun ev -> ev.stopPropagation () |> ignore

    let checkboxes =
        options
        |> List.map (fun (label, letter, tooltip) ->
            let row = document.createElement ("label")
            row.classList.add "settings-row"
            row.title <- tooltip

            let cb = document.createElement ("input") :?> HTMLInputElement
            cb.setAttribute ("type", "checkbox")
            cb.``checked`` <- initialPerms.Contains(letter)

            row.appendChild cb |> ignore
            row.appendChild (document.createTextNode label) |> ignore
            popover.appendChild row |> ignore
            letter, cb)

    let currentPerms () : string =
        checkboxes |> List.filter (fun (_, cb) -> cb.``checked``) |> List.map fst |> String.concat ""

    let refreshLabel () =
        let s = currentPerms ()
        toggleBtn.textContent <- (if s = "" then "(none)" else s)

    refreshLabel ()

    toggleBtn.onclick <-
        fun ev ->
            ev.stopPropagation () |> ignore
            popover.classList.toggle "visible" |> ignore

    for _, cb in checkboxes do
        cb.onchange <-
            fun _ ->
                refreshLabel ()
                onChange ()

    widget.appendChild toggleBtn |> ignore
    widget.appendChild popover |> ignore
    widget, checkboxes, currentPerms

/// A table cell showing a static text label with a trailing pencil;
/// clicking it hides the label and reveals `widget` (already built by the
/// caller - a text input, an owner picker, a perms popover, an arg-spec
/// select, whatever fits the field) plus a confirm button that calls
/// `onConfirm`. Hides the label while editing by construction (the current
/// value is already shown pre-filled inside `widget`, so showing both
/// would just be a redundant, possibly-stale-looking duplicate).
and private mkEditableCell (labelText: string) (widget: HTMLElement) (onConfirm: unit -> unit) : HTMLElement =
    let td = document.createElement ("td")

    let labelSpan = document.createElement ("span")
    labelSpan.textContent <- labelText

    let editGroup = document.createElement ("span")
    editGroup.classList.add "inspector-inline-edit-group"
    editGroup.setAttribute ("style", "display:none")
    // A verb row's own `tr.onclick` opens the verb editor on any click
    // anywhere in the row - stopping propagation only once the widget is
    // actually revealed means the pencil/input/confirm stay safe to click
    // without also opening the editor, while a plain click on the label
    // (not yet editing) still bubbles up as before.
    editGroup.onclick <- fun ev -> ev.stopPropagation ()

    let editBtn = document.createElement ("button")
    editBtn.classList.add "inspector-owner-edit-btn"
    editBtn.textContent <- "✎"
    editBtn.title <- "Edit"

    editBtn.onclick <-
        fun ev ->
            ev.stopPropagation ()
            labelSpan.setAttribute ("style", "display:none")
            editBtn.setAttribute ("style", "display:none")
            editGroup.setAttribute ("style", "")

    let confirmBtn = document.createElement ("button")
    confirmBtn.classList.add "inspector-add-property-btn"
    confirmBtn.textContent <- "✓"
    confirmBtn.title <- "Confirm"

    // `onConfirm` itself decides whether the new value actually differs
    // from the old one - every call site skips its `sendAction` entirely
    // when it doesn't, so an unchanged value never triggers an in-game
    // mutation (and the capture hook never sees a no-op commit). Either
    // way, closing back to the label view happens here, once: a real
    // change gets overwritten a moment later anyway once the resulting
    // `loadInspector` refresh rebuilds this row from scratch, and a
    // skipped one still needs *some* way back to the label without that
    // refresh ever happening.
    confirmBtn.onclick <-
        fun _ ->
            onConfirm ()
            labelSpan.setAttribute ("style", "")
            editBtn.setAttribute ("style", "")
            editGroup.setAttribute ("style", "display:none")

    editGroup.appendChild widget |> ignore
    editGroup.appendChild confirmBtn |> ignore

    td.appendChild labelSpan |> ignore
    td.appendChild editBtn |> ignore
    td.appendChild editGroup |> ignore
    td

/// A "+"/"−" toggle that shows/hides every element in `targets` together.
/// Green "+" when collapsed, dark-red "−" (`.inspector-remove-trigger`)
/// once expanded, flipping back on a second click. `targets` should
/// already be `display:none`; this only wires the toggle, it doesn't set
/// the initial hidden state (callers do that themselves, since some also
/// need to seed default field values first, and Properties/Verbs only
/// includes their header row here when the table starts genuinely empty).
and private mkAddTrigger (label: string) (targets: HTMLElement list) : HTMLElement =
    let triggerBtn = document.createElement ("button")
    triggerBtn.classList.add "inspector-add-property-btn"
    triggerBtn.textContent <- "+"
    triggerBtn.title <- label

    let mutable expanded = false

    triggerBtn.onclick <-
        fun _ ->
            expanded <- not expanded
            let displayStyle = if expanded then "" else "display:none"
            for target in targets do
                target.setAttribute ("style", displayStyle)

            triggerBtn.textContent <- (if expanded then "−" else "+")

            if expanded then
                triggerBtn.classList.add "inspector-remove-trigger"
            else
                triggerBtn.classList.remove "inspector-remove-trigger"

            triggerBtn.title <- (if expanded then "Cancel" else label)

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
            removeBtn.onclick <-
                fun _ ->
                    if window.confirm (sprintf "Remove %s as a parent?" name) then
                        remove refObj
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

        titleRow.appendChild (mkAddTrigger (sprintf "Add %s" label) [ addItem ]) |> ignore
    | None -> ()

    section.appendChild list |> ignore
    container.appendChild section |> ignore

/// Object inheritance graph (Part 7) - a hand-rolled inline SVG rooted at
/// `rootRef`: its full ancestor chain upward (`ancestorLayers`) and direct
/// children only downward (`directChildEdges`), laid out in horizontal
/// layers by BFS depth. Deliberately no new npm dependency (confirmed:
/// zero SVG/canvas/diagram-library usage anywhere in this codebase or its
/// dependencies) - the graphs here are object-model-scale, not
/// thousands-of-nodes scale, so a hand-rolled layered layout is enough.
/// Skips rendering the section entirely when there's nothing beyond the
/// root itself (a parentless, childless object) - an empty graph would
/// just be noise.
and private renderInheritanceGraph (container: HTMLElement) (rootRef: int64) : unit =
    let ancestorDepths, ancestorEdges = ancestorLayers rootRef
    let childEdges = directChildEdges rootRef

    let allNodeLayers =
        ([ for KeyValue(objRef, depth) in ancestorDepths -> objRef, -depth ]
         @ [ rootRef, 0 ]
         @ [ for childRef, _ in childEdges -> childRef, 1 ])
        |> List.distinctBy fst

    if List.length allNodeLayers > 1 then
        let labelFor (objRef: int64) : string =
            Map.tryFind objRef treeNodes |> Option.map (fun n -> n.Name) |> Option.defaultValue (sprintf "#%d" objRef)

        let nodeHeight = 26.0
        let hGap = 14.0
        let vGap = 46.0
        let topPadding = 10.0
        let sidePadding = 10.0
        let charWidth = 6.3

        let widthFor (objRef: int64) : float = max 50.0 (float (labelFor objRef).Length * charWidth + 16.0)

        let layers =
            allNodeLayers
            |> List.groupBy snd
            |> List.map (fun (layer, entries) -> layer, entries |> List.map fst)
            |> List.sortBy fst

        let minLayer = layers |> List.map fst |> List.min

        let layerTotalWidth (objRefs: int64 list) : float =
            (objRefs |> List.sumBy widthFor) + hGap * float (List.length objRefs - 1)

        let svgWidth = (layers |> List.map (snd >> layerTotalWidth) |> List.max) + sidePadding * 2.0
        let svgHeight = topPadding * 2.0 + nodeHeight + vGap * float (List.length layers - 1)

        // Each node's box: (x, y, width) - centered within its own layer,
        // and every layer centered within the SVG's overall width, so a
        // layer with fewer/narrower nodes than its neighbors doesn't look
        // left-aligned.
        let positions =
            layers
            |> List.collect (fun (layer, objRefs) ->
                let totalWidth = layerTotalWidth objRefs
                let startX = (svgWidth - totalWidth) / 2.0
                let y = topPadding + float (layer - minLayer) * vGap

                objRefs
                |> List.fold
                    (fun (x, acc) objRef ->
                        let w = widthFor objRef
                        x + w + hGap, (objRef, (x, y, w)) :: acc)
                    (startX, [])
                |> snd)
            |> Map.ofList

        let svgNs = "http://www.w3.org/2000/svg"
        let svg = document.createElementNS (svgNs, "svg")
        svg.setAttribute ("width", string svgWidth)
        svg.setAttribute ("height", string svgHeight)
        svg.setAttribute ("class", "inspector-graph-svg")

        // Every edge is `(moreDerived, ancestor)` - `moreDerived` sits in a
        // strictly higher layer number (drawn lower), so this single rule
        // (its top edge to the ancestor's bottom edge) covers both the
        // upward ancestor chain and the root-to-child edges without a
        // separate case for either.
        for fromRef, toRef in ancestorEdges @ childEdges do
            match Map.tryFind fromRef positions, Map.tryFind toRef positions with
            | Some(fx, fy, fw), Some(tx, ty, tw) ->
                let line = document.createElementNS (svgNs, "line")
                line.setAttribute ("x1", string (fx + fw / 2.0))
                line.setAttribute ("y1", string fy)
                line.setAttribute ("x2", string (tx + tw / 2.0))
                line.setAttribute ("y2", string (ty + nodeHeight))
                line.setAttribute ("class", "inspector-graph-edge")
                svg.appendChild line |> ignore
            | _ -> ()

        for KeyValue(objRef, (x, y, w)) in positions do
            let g = document.createElementNS (svgNs, "g")

            g.setAttribute (
                "class",
                (if objRef = rootRef then
                     "inspector-graph-node inspector-graph-node-root"
                 else
                     "inspector-graph-node")
            )

            g?onclick <- fun (_: Event) -> openOrSwitchToInspector objRef

            let rect = document.createElementNS (svgNs, "rect")
            rect.setAttribute ("x", string x)
            rect.setAttribute ("y", string y)
            rect.setAttribute ("width", string w)
            rect.setAttribute ("height", string nodeHeight)
            rect.setAttribute ("rx", "4")
            g.appendChild rect |> ignore

            let text = document.createElementNS (svgNs, "text")
            text.setAttribute ("x", string (x + w / 2.0))
            text.setAttribute ("y", string (y + nodeHeight / 2.0 + 4.0))
            text.setAttribute ("text-anchor", "middle")
            text.textContent <- labelFor objRef
            g.appendChild text |> ignore

            svg.appendChild g |> ignore

        let section = document.createElement ("div")
        let title = document.createElement ("div")
        title.classList.add "inspector-section-title"
        title.textContent <- "Inheritance graph"
        section.appendChild title |> ignore
        section.appendChild svg |> ignore
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
    inspectorPropertyStructuredToggles <- Map.empty

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
            if newName <> "" && newName <> (info?rawName: string) then
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
            async {
                // Recycle-safety precheck: every reference `findReferencesToObject`
                // can confirm statically (verb-call receivers, ownership links) -
                // see its own doc comment for what this deliberately doesn't cover
                // (property values, most notably). Fetched fresh on every click
                // rather than cached, since it's a rare, deliberate action, not
                // something worth a round-trip on every inspector view.
                let! refs = LspClient.findReferencesToObjectAsync objRef

                let childCount: int = (unbox info?children: obj[]).Length
                let name: string = info?name

                let childWarning =
                    if childCount > 0 then
                        sprintf
                            " This object has %d child object(s), which will be reparented onto its own parent."
                            childCount
                    else
                        ""

                let refWarning =
                    if Array.isEmpty refs then
                        ""
                    else
                        let shown, extraCount =
                            if refs.Length > 5 then Array.truncate 5 refs, refs.Length - 5 else refs, 0

                        let items =
                            shown
                            |> Array.map (fun (kind, o, detail) ->
                                sprintf "%s on #%d%s" kind o (if detail = "" then "" else sprintf " (%s)" detail))
                            |> String.concat ", "

                        let extraSuffix = if extraCount > 0 then sprintf ", +%d more" extraCount else ""

                        sprintf
                            " Also found %d likely reference(s): %s%s. (Best-effort - based on the last exported snapshot, doesn't catch every case such as property values.)"
                            refs.Length
                            items
                            extraSuffix

                let warning = sprintf "Recycle %s?%s%s This cannot be undone." name childWarning refWarning

                if window.confirm warning then
                    sendAction [ "action" ==> "recycle-object"; "obj" ==> int objRef ]
            }
            |> Async.StartImmediate

    header.appendChild recycleBtn |> ignore

    // Tree-color rule for this object + everything descending from it (see
    // `colorForObject`/`ancestryDistance`) - a native `<input type="color">`
    // doubles as both the swatch display and the picker trigger, no separate
    // popup needed (no existing color-picker precedent elsewhere in this
    // codebase to mirror instead). `onchange`, not `oninput` - commits once
    // the picker closes rather than firing a redraw/localStorage write per
    // drag frame, matching the discrete feel of the rename/recycle buttons
    // right next to it.
    let existingColorRule = colorRules |> List.tryFind (fun r -> r.TypeObjRef = objRef)

    let colorInput = document.createElement ("input") :?> HTMLInputElement
    colorInput.setAttribute ("type", "color")
    colorInput.classList.add "inspector-tree-color-btn"
    colorInput.title <- "Set tree color for this object and its descendants"
    colorInput.value <- (existingColorRule |> Option.map (fun r -> r.Color) |> Option.defaultValue "#6699cc")
    colorInput.onchange <- fun _ -> setColorRule objRef (info?name: string) colorInput.value
    header.appendChild colorInput |> ignore

    match existingColorRule with
    | Some _ ->
        let clearColorBtn = document.createElement ("button")
        clearColorBtn.classList.add "inspector-row-delete-btn"
        clearColorBtn.textContent <- "×"
        clearColorBtn.title <- "Remove tree color rule"
        clearColorBtn.onclick <- fun _ -> removeColorRule objRef
        header.appendChild clearColorBtn |> ignore
    | None -> ()

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
        // `BigInt`, compared via `===` in `selectedObjRef`'s equality
        // checks). Left as a bare dynamic cast, this silently fails to
        // match against a ref added via the sidebar's
        // `int64 (value.TrimStart '#')` round-trip (a genuine `BigInt`),
        // breaking selection-highlight equality - confirmed live. The
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
                if expr <> "" && expr <> sprintf "#%d" ownerRef then
                    sendAction [ "action" ==> "set-owner"; "obj" ==> int objRef; "ownerExpr" ==> expr ]

                // A real change is overwritten a moment later anyway once
                // the resulting `loadInspector` refresh rebuilds this row
                // from scratch - but an unchanged value skips that refresh
                // entirely (see `mkEditableCell`'s own comment), so this is
                // the only way back to the plain link view either way.
                link.setAttribute ("style", "")
                editBtn.setAttribute ("style", "")
                editGroup.setAttribute ("style", "display:none")

        editGroup.appendChild ownerConfirmBtn |> ignore

        // The current value is already shown pre-filled in the editor
        // (`ownerEditInput`'s initial value above), so the static link
        // would just be a redundant, possibly-stale-looking duplicate
        // while editing - hide it until the change is done (a full
        // `loadInspector` refresh, like every other action here, is what
        // brings it back).
        editBtn.onclick <-
            fun _ ->
                link.setAttribute ("style", "display:none")
                editBtn.setAttribute ("style", "display:none")
                editGroup.setAttribute ("style", "")

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

    // Tooltip text verified against `ToastStunt/src/` (`include/db.h`'s
    // `db_object_flag` enum, gated through `db_object_allows()`) rather than
    // assumed - MOO documentation for these bits is sparse/LambdaMOO-era.
    let flags =
        [ "player", (info?player: bool), "Marks this as a valid player object (login-eligible, appears in players()) - not the same as currently connected."
          "programmer", (info?programmer: bool), "Lets this object's player compile and run MOO code (eval(), .program, set_verb_code())."
          "wizard", (info?wizard: bool), "Grants this object's player unrestricted permission, bypassing every other object/verb/property check."
          "r", (info?read: bool), "Lets other players' code list this object's verbs and properties (verbs(), properties(), respond_to())."
          "w", (info?write: bool), "Lets other players' code add or delete this object's verbs and properties."
          "f", (info?fertile: bool), "Lets other players use this object as a parent for new (non-anonymous) objects."
          "a", (info?anonymous: bool), "Lets other players use this object as a parent for new anonymous objects specifically." ]

    for flagName, isSet, tooltip in flags do
        // Immediate toggle-on-click, no separate confirm step - same
        // convention the property value inputs already use (autosave on
        // blur). `flagName` here is always one of these seven hardcoded
        // literals, never user-typed, so the sidecar's `setFlag` can splice
        // it directly into the generated MOO statement safely.
        let badge = document.createElement ("button")
        badge.classList.add "inspector-flag"
        if isSet then badge.classList.add "set"
        badge.textContent <- flagName
        badge.title <- tooltip

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

    renderInheritanceGraph inspectorContentEl objRef

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

    // Column labels are noise for an empty table - hidden until either a
    // real row exists or the add-trigger reveals them alongside the add
    // row (see the trigger wiring below).
    if props.Length = 0 then
        propsHeaderRow.setAttribute ("style", "display:none")

    propsTable.appendChild propsHeaderRow |> ignore

    let mutable highlightRow: HTMLElement option = None

    // Permission-inheritance visualizer: for each verb/property row (own or
    // inherited alike), asks the LSP which ancestor's copy actually wins by
    // real MOO dispatch order (`moodev/resolveEffectiveMember`, reusing the
    // already-proven, dispatch-order-faithful
    // `Metadata.Resolver.findCallableVerb`/`findDeclaringObjectForProperty`
    // against the static graph - `getLiveInfo`'s own live ancestor walk is
    // BFS-ordered, not real dispatch order, and its own doc comment admits
    // it doesn't compute this). A row whose own definer isn't the resolved
    // winner gets a small "(shadowed by #N)" suffix - e.g. an inherited copy
    // that a more-derived object's own override shadows. Same name can
    // appear on multiple rows (an own row plus one or more inherited rows
    // for the same overridden name), so results are cached per
    // (kind, name) to avoid redundant identical requests within one render.
    let effectiveMemberCache = System.Collections.Generic.Dictionary<string * string, int64 option>()

    let annotateShadowedMember (kind: string) (name: string) (rowDefinerRef: int64) (nameTd: HTMLElement) : unit =
        let applyIfShadowed (winner: int64 option) =
            // Guards against a stale response landing after the user has
            // navigated away or re-rendered this inspector - appending to a
            // detached node would be harmless either way, but this matches
            // this function's own `moodev-prop-content`-handler convention
            // of checking `activeTab` first.
            if activeTab = InspectorTab objRef then
                match winner with
                | Some w when w <> rowDefinerRef ->
                    let suffix = document.createElement ("span")
                    suffix.classList.add "inspector-shadowed-suffix"
                    suffix.textContent <- sprintf " (shadowed by #%d)" w
                    nameTd.appendChild suffix |> ignore
                | _ -> ()

        match effectiveMemberCache.TryGetValue((kind, name)) with
        | true, cached -> applyIfShadowed cached
        | false, _ ->
            async {
                let! winner = LspClient.resolveEffectiveMemberAsync objRef kind name
                effectiveMemberCache.[(kind, name)] <- winner
                applyIfShadowed winner
            }
            |> Async.StartImmediate

    for p in props do
        let pname: string = p?name
        let pPerms: string = p?perms
        let pOwnerRef: int64 = int64 (p?ownerRef: float)
        let pDefinerRef: int64 = int64 (p?definerRef: float)
        // Inherited (defined on an ancestor, not `objRef` itself) - shown
        // read-only, dimmed, with a link back to the ancestor's own
        // inspector - see this function's own module-level doc comment for
        // why (renaming/perms/deleting only make sense at the true
        // definer; the live *value* stays editable either way, below).
        let pIsOwn = pDefinerRef = objRef
        let tr = document.createElement ("tr")
        if not pIsOwn then tr.classList.add "inspector-row-inherited"

        let nameTd =
            if pIsOwn then
                // Every field below resubmits the *other* two unchanged
                // alongside whichever one this particular pencil is for -
                // `set_property_info` always wants all three together
                // (confirmed against `ToastStunt/src/property.cc`'s
                // `bf_set_prop_info`), so there's no way to change just one
                // via the builtin itself.
                let nameInput = document.createElement ("input") :?> HTMLInputElement
                nameInput.classList.add "inspector-property-value"
                nameInput.value <- pname

                mkEditableCell pname nameInput (fun () ->
                    let newName = nameInput.value.Trim()

                    if newName <> "" && newName <> pname then
                        sendAction
                            [ "action" ==> "set-property-info"
                              "obj" ==> int objRef
                              "name" ==> pname
                              "newName" ==> newName
                              "ownerExpr" ==> sprintf "#%d" pOwnerRef
                              "perms" ==> pPerms ])
            else
                let td = document.createElement ("td")
                td.appendChild (document.createTextNode pname) |> ignore

                let definerLink = document.createElement ("span")
                definerLink.classList.add "inspector-link"
                definerLink.classList.add "inspector-definer-link"
                definerLink.textContent <- sprintf " #%d" pDefinerRef
                definerLink.onclick <- fun _ -> openOrSwitchToInspector pDefinerRef
                td.appendChild definerLink |> ignore
                td

        tr.appendChild nameTd |> ignore
        annotateShadowedMember "property" pname pDefinerRef nameTd

        let ownerTd =
            if pIsOwn then
                let pOwnerGroup, pOwnerInput =
                    mkQuickFillInput "player, #5, or $room" (sprintf "#%d" pOwnerRef) ownerQuickFills false

                mkEditableCell (p?owner: string) pOwnerGroup (fun () ->
                    let expr = pOwnerInput.value.Trim()

                    if expr <> "" && expr <> sprintf "#%d" pOwnerRef then
                        sendAction
                            [ "action" ==> "set-property-info"
                              "obj" ==> int objRef
                              "name" ==> pname
                              "newName" ==> pname
                              "ownerExpr" ==> expr
                              "perms" ==> pPerms ])
            else
                let td = document.createElement ("td")
                td.textContent <- (p?owner: string)
                td

        tr.appendChild ownerTd |> ignore

        let permsTd =
            if pIsOwn then
                let pPermsWidget, _, pCurrentPerms =
                    mkPermsWidget
                        [ "Read", "r", "Other players' code can read this property's value."
                          "Write", "w", "Other players' code can set this property's value."
                          "Chown",
                          "c",
                          "This property's owner is force-locked to the object's own owner, overriding whatever owner you pick." ]
                        pPerms
                        (fun () -> ())

                mkEditableCell pPerms pPermsWidget (fun () ->
                    let newPerms = pCurrentPerms ()

                    if newPerms <> pPerms then
                        sendAction
                            [ "action" ==> "set-property-info"
                              "obj" ==> int objRef
                              "name" ==> pname
                              "newName" ==> pname
                              "ownerExpr" ==> sprintf "#%d" pOwnerRef
                              "perms" ==> newPerms ])
            else
                let td = document.createElement ("td")
                td.textContent <- pPerms
                td

        tr.appendChild permsTd |> ignore

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

        // Structured (list/map) editor toggle - only shown once the raw
        // value text actually looks list/map-shaped (see
        // `moodev-prop-content` below, which rechecks this once the live
        // value arrives; empty at this point, so hidden here too).
        let structuredContainer = document.createElement ("div")
        structuredContainer.classList.add "inspector-structured-editor"
        structuredContainer.setAttribute ("style", "display:none")

        let structuredToggleBtn = document.createElement ("button")
        structuredToggleBtn.classList.add "inspector-structured-toggle-btn"
        structuredToggleBtn.textContent <- "☰"
        structuredToggleBtn.title <- "Edit as list/map"
        structuredToggleBtn.setAttribute ("style", "display:none")

        structuredToggleBtn.onclick <-
            fun _ ->
                if looksWaifShaped input.value then
                    sendAction [ "action" ==> "get-waif-properties"; "obj" ==> int objRef; "name" ==> pname ]
                else
                    sendAction
                        [ "action" ==> "parse-property-literal"
                          "obj" ==> int objRef
                          "name" ==> pname
                          "valueText" ==> input.value ]

        valueTd.appendChild input |> ignore
        valueTd.appendChild preview |> ignore
        valueTd.appendChild structuredToggleBtn |> ignore
        valueTd.appendChild structuredContainer |> ignore
        tr.appendChild valueTd |> ignore

        // Deleting only makes sense at the true definer - an inherited row
        // gets an empty cell here instead (deleting from `objRef` would be
        // deleting an ancestor's property definition out from under every
        // other descendant too).
        let deleteTd = document.createElement ("td")

        if pIsOwn then
            let deleteBtn = document.createElement ("button")
            deleteBtn.classList.add "inspector-row-delete-btn"
            deleteBtn.textContent <- "🗑"
            deleteBtn.title <- "Delete property"

            deleteBtn.onclick <-
                fun _ ->
                    if window.confirm (sprintf "Delete property \"%s\"?" pname) then
                        sendAction [ "action" ==> "delete-property"; "obj" ==> int objRef; "name" ==> pname ]

            deleteTd.appendChild deleteBtn |> ignore

        tr.appendChild deleteTd |> ignore

        propsTable.appendChild tr |> ignore
        inspectorPropertyInputs <- Map.add pname input inspectorPropertyInputs
        inspectorPropertyPreviews <- Map.add pname preview inspectorPropertyPreviews

        inspectorPropertyStructuredToggles <-
            Map.add pname (structuredToggleBtn, structuredContainer) inspectorPropertyStructuredToggles

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
    // `validate_prop_info`; verbs' x/d don't apply here. Defined before the
    // owner widget below (even though it's appended after it) because the
    // owner widget's own visibility depends on `chownCb`'s state -
    // `onPermsChange` is a forwarding shim wired up to
    // `refreshOwnerWidgetVisibility` once that's defined further down,
    // since it doesn't exist yet at this point.
    let mutable onPermsChange: unit -> unit = fun () -> ()

    let permsWidget, propPermCheckboxes, currentPerms =
        mkPermsWidget
            [ "Read", "r", "Other players' code can read this property's value."
              "Write", "w", "Other players' code can set this property's value."
              "Chown",
              "c",
              "This property's owner is force-locked to the object's own owner, overriding whatever owner you pick." ]
            "rc"
            (fun () -> onPermsChange ())

    let chownCb = propPermCheckboxes |> List.find (fun (letter, _) -> letter = "c") |> snd

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
    onPermsChange <- refreshOwnerWidgetVisibility

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

    let propsAddTargets = if props.Length = 0 then [ propsHeaderRow; addPropRow ] else [ addPropRow ]

    let propsTitleRow = document.createElement ("div")
    propsTitleRow.classList.add "inspector-section-title-row"
    propsTitleRow.appendChild propsTitle |> ignore
    propsTitleRow.appendChild (mkAddTrigger "Add property" propsAddTargets) |> ignore

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

    if verbs.Length = 0 then
        verbsHeaderRow.setAttribute ("style", "display:none")

    verbsTable.appendChild verbsHeaderRow |> ignore

    let mkArgSpecSelect (options: string list) (defaultValue: string) : HTMLSelectElement =
        let select = document.createElement ("select") :?> HTMLSelectElement

        for opt in options do
            let optionEl = document.createElement ("option") :?> HTMLOptionElement
            optionEl.value <- opt
            optionEl.textContent <- opt
            select.appendChild optionEl |> ignore

        select.value <- defaultValue
        select

    for v in verbs do
        let tr = document.createElement ("tr")
        tr.classList.add "inspector-verb-row"
        let verbName: string = v?name
        let vFullNames: string = v?fullNames
        let vPerms: string = v?perms
        let vDobj: string = v?dobj
        let vPrep: string = v?prep
        let vIobj: string = v?iobj
        let vOwnerRef: int64 = int64 (v?ownerRef: float)
        let vDefinerRef: int64 = int64 (v?definerRef: float)
        // Inherited (defined on an ancestor, not `objRef` itself) - see
        // this function's own module-level doc comment. Clicking still
        // opens the verb for editing, just at its true definer.
        let vIsOwn = vDefinerRef = objRef
        if not vIsOwn then tr.classList.add "inspector-row-inherited"
        tr.onclick <- fun _ -> openOrSwitchToVerb vDefinerRef verbName

        let nameTd =
            if vIsOwn then
                // Every field below resubmits the verb's *other* current
                // fields unchanged alongside whichever one this pencil is
                // for - `set_verb_info`/`set_verb_args` always want their
                // whole triple together (confirmed against
                // `ToastStunt/src/verbs.cc`'s
                // `bf_set_verb_info`/`bf_set_verb_args`), so there's no way
                // to change just one via the builtins themselves.
                // `verbName` (the first alias) is only ever used to
                // *resolve* which verb this is server-side, never as the
                // value being changed.
                let nameInput = document.createElement ("input") :?> HTMLInputElement
                nameInput.classList.add "inspector-property-value"
                nameInput.value <- vFullNames

                mkEditableCell verbName nameInput (fun () ->
                    let newNames = nameInput.value.Trim()

                    if newNames <> "" && newNames <> vFullNames then
                        sendAction
                            [ "action" ==> "set-verb-info"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "newNames" ==> newNames
                              "ownerExpr" ==> sprintf "#%d" vOwnerRef
                              "perms" ==> vPerms ])
            else
                let td = document.createElement ("td")
                td.appendChild (document.createTextNode verbName) |> ignore

                let definerLink = document.createElement ("span")
                definerLink.classList.add "inspector-link"
                definerLink.classList.add "inspector-definer-link"
                definerLink.textContent <- sprintf " #%d" vDefinerRef

                definerLink.onclick <-
                    fun ev ->
                        ev.stopPropagation () |> ignore // don't also open the verb via the row's own click
                        openOrSwitchToInspector vDefinerRef

                td.appendChild definerLink |> ignore
                td

        tr.appendChild nameTd |> ignore
        annotateShadowedMember "verb" verbName vDefinerRef nameTd

        let ownerTd =
            if vIsOwn then
                let vOwnerGroup, vOwnerInput =
                    mkQuickFillInput "player, #5, or $room" (sprintf "#%d" vOwnerRef) ownerQuickFills false

                mkEditableCell (v?owner: string) vOwnerGroup (fun () ->
                    let expr = vOwnerInput.value.Trim()

                    if expr <> "" && expr <> sprintf "#%d" vOwnerRef then
                        sendAction
                            [ "action" ==> "set-verb-info"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "newNames" ==> vFullNames
                              "ownerExpr" ==> expr
                              "perms" ==> vPerms ])
            else
                let td = document.createElement ("td")
                td.textContent <- (v?owner: string)
                td

        tr.appendChild ownerTd |> ignore

        let permsTd =
            if vIsOwn then
                let vPermsWidget, _, vCurrentPerms =
                    mkPermsWidget
                        [ "Read", "r", "Other players' code can read this verb's source."
                          "Write", "w", "Other players' code can modify this verb's source."
                          "Exec", "x", "Other players' code can call this verb."
                          "Debug",
                          "d",
                          "Runtime errors actually raise/propagate (recommended). Without this, errors are silently swallowed." ]
                        vPerms
                        (fun () -> ())

                mkEditableCell vPerms vPermsWidget (fun () ->
                    let newPerms = vCurrentPerms ()

                    if newPerms <> vPerms then
                        sendAction
                            [ "action" ==> "set-verb-info"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "newNames" ==> vFullNames
                              "ownerExpr" ==> sprintf "#%d" vOwnerRef
                              "perms" ==> newPerms ])
            else
                let td = document.createElement ("td")
                td.textContent <- vPerms
                td

        tr.appendChild permsTd |> ignore

        let dobjTd =
            if vIsOwn then
                let dobjEditSelect = mkArgSpecSelect [ "none"; "any"; "this" ] vDobj

                mkEditableCell vDobj dobjEditSelect (fun () ->
                    if dobjEditSelect.value <> vDobj then
                        sendAction
                            [ "action" ==> "set-verb-args"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "dobj" ==> dobjEditSelect.value
                              "prep" ==> vPrep
                              "iobj" ==> vIobj ])
            else
                let td = document.createElement ("td")
                td.textContent <- vDobj
                td

        tr.appendChild dobjTd |> ignore

        let prepTd =
            if vIsOwn then
                let prepEditGroup, prepEditInput =
                    mkQuickFillInput "none, any, or a preposition" vPrep [ "none", "none"; "any", "any" ] false

                mkEditableCell vPrep prepEditGroup (fun () ->
                    let newPrep = prepEditInput.value.Trim()

                    if newPrep <> vPrep then
                        sendAction
                            [ "action" ==> "set-verb-args"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "dobj" ==> vDobj
                              "prep" ==> newPrep
                              "iobj" ==> vIobj ])
            else
                let td = document.createElement ("td")
                td.textContent <- vPrep
                td

        tr.appendChild prepTd |> ignore

        let iobjTd =
            if vIsOwn then
                let iobjEditSelect = mkArgSpecSelect [ "none"; "any"; "this" ] vIobj

                mkEditableCell vIobj iobjEditSelect (fun () ->
                    if iobjEditSelect.value <> vIobj then
                        sendAction
                            [ "action" ==> "set-verb-args"
                              "obj" ==> int objRef
                              "verb" ==> verbName
                              "dobj" ==> vDobj
                              "prep" ==> vPrep
                              "iobj" ==> iobjEditSelect.value ])
            else
                let td = document.createElement ("td")
                td.textContent <- vIobj
                td

        tr.appendChild iobjTd |> ignore

        // Deleting only makes sense at the true definer - see the
        // properties table's own delete-column comment above.
        let deleteTd = document.createElement ("td")

        if vIsOwn then
            // Stops propagation so this doesn't also open the verb via the
            // row's own click handler above (same idiom `renderTabs`'s
            // close-× uses against its tab's own switch-click).
            let deleteBtn = document.createElement ("button")
            deleteBtn.classList.add "inspector-row-delete-btn"
            deleteBtn.textContent <- "🗑"
            deleteBtn.title <- "Delete verb"

            deleteBtn.onclick <-
                fun ev ->
                    ev.stopPropagation () |> ignore

                    if window.confirm (sprintf "Delete verb \"%s\"?" verbName) then
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
    // Read+Exec checked by default - a normal callable command verb; Write
    // and Debug off, matching the properties widget's own "least-surprising
    // default" convention. Debug's tooltip is verified against
    // `ToastStunt/src/execute.cc`'s `RAISE_ERROR` macro - with this flag
    // unset, a runtime error is dropped entirely (not just logged
    // differently), so the verb silently continues past the failure.
    let verbPermsWidget, _, currentVerbPerms =
        mkPermsWidget
            [ "Read", "r", "Other players' code can read this verb's source."
              "Write", "w", "Other players' code can modify this verb's source."
              "Exec", "x", "Other players' code can call this verb."
              "Debug",
              "d",
              "Runtime errors actually raise/propagate (recommended). Without this, errors are silently swallowed." ]
            "rx"
            (fun () -> ())

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

    let verbsAddTargets = if verbs.Length = 0 then [ verbsHeaderRow; addVerbRow ] else [ addVerbRow ]

    let verbsTitleRow = document.createElement ("div")
    verbsTitleRow.classList.add "inspector-section-title-row"
    verbsTitleRow.appendChild verbsTitle |> ignore
    verbsTitleRow.appendChild (mkAddTrigger "Add verb" verbsAddTargets) |> ignore

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

/// Switches the sidebar to `view` - activating its container and triggering
/// that view's own "always fresh" load, exactly the convention
/// `loadCorponymHistory`/`loadTasks` already followed as main-pane tabs.
/// Entirely independent of `activeTab`/the main pane (see `SidebarView`'s
/// own comment) - unlike `switchToTab`, this always re-runs the view's load
/// even if it's already the active view, matching every one of these views'
/// existing "always fresh" behavior.
and private switchToSidebarView (view: SidebarView) : unit =
    activeSidebarView <- view

    // Only `WatchView` (below) restarts this - every other switch means the
    // panel that was ticking is no longer visible, so there's nothing left
    // for the interval to refresh.
    stopWatchInterval ()

    match view with
    | TreeView -> activateOnlySidebarView sidebarViewTreeEl
    | HistoryView ->
        activateOnlySidebarView sidebarViewHistoryEl

        if isLoggedIn then
            loadCorponymHistory ()
        else
            corponymHistoryListEl.innerHTML <- ""
            historySearchResultsEl.innerHTML <- ""
    | TasksView ->
        activateOnlySidebarView sidebarViewTasksEl
        if isLoggedIn then loadTasks () else tasksListEl.innerHTML <- ""
    | ServerStatusView ->
        activateOnlySidebarView sidebarViewServerStatusEl
        if isLoggedIn then loadServerStatus () else serverStatusListEl.innerHTML <- ""
    | ErrorsView ->
        activateOnlySidebarView sidebarViewErrorsEl
        renderErrorsList ()
    | DeadVerbsView ->
        activateOnlySidebarView sidebarViewDeadVerbsEl
        treeDeadVerbsSummaryEl.textContent <- "Scanning..."
        treeDeadVerbsListEl.innerHTML <- ""

        async {
            let! results = LspClient.findDeadVerbsAsync ()
            renderDeadVerbsResults results
        }
        |> Async.StartImmediate
    | DeadPropertiesView ->
        activateOnlySidebarView sidebarViewDeadPropertiesEl
        treeDeadPropertiesSummaryEl.textContent <- "Scanning..."
        treeDeadPropertiesListEl.innerHTML <- ""

        async {
            let! results = LspClient.findDeadPropertiesAsync ()
            renderDeadPropertiesResults results
        }
        |> Async.StartImmediate
    | GotchasView ->
        activateOnlySidebarView sidebarViewGotchasEl
        treeGotchasSummaryEl.textContent <- "Scanning..."
        treeGotchasListEl.innerHTML <- ""

        async {
            let! results = LspClient.findGotchasAsync ()
            renderGotchasResults results
        }
        |> Async.StartImmediate
    | PermissionRisksView ->
        activateOnlySidebarView sidebarViewPermissionRisksEl
        treePermissionRisksSummaryEl.textContent <- "Scanning..."
        treePermissionRisksListEl.innerHTML <- ""

        async {
            let! results = LspClient.findPermissionRisksAsync ()
            renderPermissionRisksResults results
        }
        |> Async.StartImmediate
    | DocsView ->
        activateOnlySidebarView sidebarViewDocsEl

        match moocodeDocsCache with
        | Some _ -> renderDocsList (docsSearchInputEl.value)
        | None ->
            docsListEl.innerHTML <- "<li class=\"placeholder\">Loading...</li>"

            async {
                let! results = LspClient.getMoocodeDocsAsync ()
                moocodeDocsCache <- Some(Array.append results errorCodeGlossary)
                renderDocsList (docsSearchInputEl.value)
            }
            |> Async.StartImmediate
    | EvalScratchpadView ->
        // Nothing to load - the result area only ever shows the last
        // expression's own outcome, not something fetched per-view-switch.
        activateOnlySidebarView sidebarViewScratchpadEl
    | PropertySearchView ->
        // Nothing to load - same reasoning as EvalScratchpadView, the
        // results area only ever shows the last search's own outcome.
        activateOnlySidebarView sidebarViewPropertySearchEl
    | WatchView ->
        activateOnlySidebarView sidebarViewWatchEl
        renderWatchList ()

        if isLoggedIn then
            startWatchInterval ()

    for btn, v in
        [ viewTreeBtn, TreeView
          viewHistoryBtn, HistoryView
          viewTasksBtn, TasksView
          viewServerStatusBtn, ServerStatusView
          viewErrorsBtn, ErrorsView
          viewDeadVerbsBtn, DeadVerbsView
          viewDeadPropertiesBtn, DeadPropertiesView
          viewGotchasBtn, GotchasView
          viewPermissionRisksBtn, PermissionRisksView
          viewDocsBtn, DocsView
          viewScratchpadBtn, EvalScratchpadView
          viewPropertySearchBtn, PropertySearchView
          viewWatchBtn, WatchView ] do
        if v = view then btn.classList.add "active" else btn.classList.remove "active"

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

            // A non-corified verb capture tier label is already "#objnum"
            // (see Exporter.describePath's own comment), not a real
            // corponym - shown as-is rather than double-sigilled "$#123".
            let displayLabel = if corponym.StartsWith("#") then corponym else "$" + corponym
            li.textContent <- sprintf "%s  %s / %s - %s" (date.ToString("yyyy-MM-dd HH:mm")) displayLabel label message

            match objRefOpt with
            | Some objRef ->
                li.classList.add "inspector-link"
                li.onclick <- fun _ -> openOrSwitchToVerb objRef label
            | None -> ()

            historySearchResultsEl.appendChild li |> ignore

/// Renders `search-content`'s results (the *live tree*, not history - see
/// `IdeActions.searchContent`'s own comment) - same clickable-when-resolved
/// convention `renderSearchResults` uses, minus the timestamp/commit-message
/// columns that don't exist for "what's there right now."
and private renderContentSearchResults (results: (int64 option * string * string * string) list) : unit =
    contentSearchResultsEl.innerHTML <- ""

    if results.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No matches."
        contentSearchResultsEl.appendChild li |> ignore
    else
        for objRefOpt, corponym, label, matchingLine in results do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            // Same anon-label handling as `renderSearchResults` above.
            let displayLabel = if corponym.StartsWith("#") then corponym else "$" + corponym
            li.textContent <- sprintf "%s / %s - %s" displayLabel label (matchingLine.Trim())

            match objRefOpt with
            | Some objRef ->
                li.classList.add "inspector-link"
                li.onclick <- fun _ -> openOrSwitchToVerb objRef label
            | None -> ()

            contentSearchResultsEl.appendChild li |> ignore

/// Renders `search-properties`' results into the Property search sidebar
/// view - one row per matching object, clicking through to that object's
/// inspector with the *searched* property highlighted
/// (`openOrSwitchToInspectorWith`, same "jump straight to the relevant row"
/// convention `renderDeadPropertiesResults` already uses for a
/// property-shaped finding). `searchedName` is the property name the search
/// was run with, captured at dispatch time (`lastPropertySearchName`) -
/// distinct from each result tuple's own `name`, which is the *object's*
/// display name for the result line's text, not the property.
and private renderPropertySearchResults
    (searchedName: string)
    (truncated: bool)
    (results: (int64 * string * string) list)
    : unit =
    propertySearchResultsEl.innerHTML <- ""

    if results.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No matches."
        propertySearchResultsEl.appendChild li |> ignore
    else
        for objRef, name, value in results do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            li.classList.add "inspector-link"
            li.textContent <- sprintf "#%d / %s - %s" objRef name value
            li.onclick <- fun _ -> openOrSwitchToInspectorWith objRef (Some searchedName)
            propertySearchResultsEl.appendChild li |> ignore

        if truncated then
            let li = document.createElement ("li")
            li.textContent <- sprintf "Showing the first %d matches - refine the search to see more." results.Length
            propertySearchResultsEl.appendChild li |> ignore

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

/// Refreshes the Tasks view - always, same "always fresh" convention
/// `loadCorponymHistory` uses, since the queue changes constantly.
and private loadTasks () : unit =
    tasksListEl.innerHTML <- "Loading..."
    sendAction [ "action" ==> "get-tasks" ]

/// Renders `get-tasks`'s results - `queued_tasks()` minus its two obsolete
/// tick/seconds-placeholder fields (see `IdeActions.getTasks`'s own
/// comment). `programmer`/`vloc`/`this` are each clickable through to that
/// object's inspector, same link style used throughout this file.
and private renderTasksList
    (tasks:
        {| id: int64
           start: int64
           programmerRef: int64
           programmer: string
           vlocRef: int64
           vloc: string
           verb: string
           line: int64
           thisRef: int64
           this: string
           bytes: int64 |} array)
    : unit =
    tasksListEl.innerHTML <- ""

    if tasks.Length = 0 then
        let li = document.createElement ("li")
        li.textContent <- "No queued tasks."
        tasksListEl.appendChild li |> ignore
    else
        for t in tasks do
            let li = document.createElement ("li")
            li.classList.add "picker-item"

            let startText =
                if t.start = -1L then
                    "reading"
                else
                    System.DateTimeOffset.FromUnixTimeSeconds(t.start).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")

            let label = document.createElement ("span")
            label.textContent <- sprintf "#%d  %s  " t.id startText
            li.appendChild label |> ignore

            let programmerLink = document.createElement ("span")
            programmerLink.classList.add "inspector-link"
            programmerLink.textContent <- t.programmer
            programmerLink.onclick <- fun _ -> openOrSwitchToInspector t.programmerRef
            li.appendChild programmerLink |> ignore

            let verbLabel = document.createElement ("span")
            verbLabel.textContent <- sprintf " calling "
            li.appendChild verbLabel |> ignore

            let vlocLink = document.createElement ("span")
            vlocLink.classList.add "inspector-link"
            vlocLink.textContent <- sprintf "%s:%s" t.vloc t.verb
            vlocLink.onclick <- fun _ -> openOrSwitchToVerb t.vlocRef t.verb
            li.appendChild vlocLink |> ignore

            let restLabel = document.createElement ("span")
            restLabel.textContent <- sprintf " (line %d)  this=" t.line
            li.appendChild restLabel |> ignore

            let thisLink = document.createElement ("span")
            thisLink.classList.add "inspector-link"
            thisLink.textContent <- t.this
            thisLink.onclick <- fun _ -> openOrSwitchToInspector t.thisRef
            li.appendChild thisLink |> ignore

            let bytesLabel = document.createElement ("span")
            bytesLabel.textContent <- sprintf "  %d bytes" t.bytes
            li.appendChild bytesLabel |> ignore

            let killBtn = document.createElement ("button")
            killBtn.classList.add "inspector-row-delete-btn"
            killBtn.textContent <- "🗑"
            killBtn.title <- "Kill this task"

            killBtn.onclick <-
                fun _ ->
                    if window.confirm (sprintf "Kill task #%d?" t.id) then
                        sendAction [ "action" ==> "kill-task"; "task" ==> int t.id ]

            li.appendChild killBtn |> ignore
            tasksListEl.appendChild li |> ignore

/// Refreshes the Server status view - same "always fresh" convention
/// `loadTasks` uses, since bound listeners can change between views.
and private loadServerStatus () : unit =
    serverStatusListEl.innerHTML <- "Loading..."
    sendAction [ "action" ==> "get-server-status" ]

/// Renders `get-server-status`'s results - one row per bound listener
/// (`IdeActions.getServerStatus`/ToastStunt's own `listeners()`). Room to
/// grow with more live signals later (player count, uptime) without
/// changing this render shape - not attempted here, matching the card's
/// own framing.
and private renderServerStatusResults (listeners: (int64 * int64 * string * bool) list) : unit =
    serverStatusListEl.innerHTML <- ""

    if listeners.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No bound listeners."
        serverStatusListEl.appendChild li |> ignore
    else
        for objRef, port, interfaceName, tls in listeners do
            let li = document.createElement ("li")
            li.classList.add "picker-item"

            let label = document.createElement ("span")
            label.textContent <- sprintf "port %d (%s)%s  owner: " port interfaceName (if tls then ", TLS" else "")
            li.appendChild label |> ignore

            let ownerLink = document.createElement ("span")
            ownerLink.classList.add "inspector-link"
            ownerLink.textContent <- sprintf "#%d" objRef
            ownerLink.onclick <- fun _ -> openOrSwitchToInspector objRef
            li.appendChild ownerLink |> ignore

            serverStatusListEl.appendChild li |> ignore

/// Rebuilds the Errors view's list from `errorLog`. Traceback lines are
/// plain text, not structured per-frame data yet - no click-to-jump in this
/// pass (an explicit non-goal, not forgotten). There's no server round trip
/// to refresh here (errors only ever arrive as unsolicited `moodev-error`
/// pushes, never fetched) - this just re-shows whatever's already in
/// `errorLog`.
and private renderErrorsList () : unit =
    errorsListEl.innerHTML <- ""

    if errorLog.IsEmpty then
        let li = document.createElement ("li")
        li.textContent <- "No errors yet this session."
        errorsListEl.appendChild li |> ignore
    else
        for whenReceived, kind, tracebackLines in errorLog do
            let li = document.createElement ("li")
            li.classList.add "picker-item"
            let pre = document.createElement ("pre")

            pre.textContent <-
                sprintf "%s  [%s]\n%s" (whenReceived.ToString("yyyy-MM-dd HH:mm:ss")) kind (String.concat "\n" tracebackLines)

            li.appendChild pre |> ignore
            errorsListEl.appendChild li |> ignore

/// Renders `moodev/findDeadVerbs`' results into the Dead Verbs sidebar view -
/// each entry shown in MOO-call-syntax shape (`obj:verb(dobj, prep, iobj)`)
/// and clickable straight through to that verb via the existing
/// `openOrSwitchToVerb`. Entries flagged `possiblyDynamic` (a call site with
/// a matching literal name exists but couldn't be resolved statically - see
/// `Handlers.findDeadVerbs`'s own comment) are shown distinctly rather than
/// as a clean "nothing calls this" hit.
and private renderDeadVerbsResults (results: (int64 * string * string * string * string * bool)[]) : unit =
    let dynamicCount =
        results |> Array.filter (fun (_, _, _, _, _, possiblyDynamic) -> possiblyDynamic) |> Array.length

    treeDeadVerbsSummaryEl.textContent <-
        if results.Length = 0 then
            "No dead verbs found."
        else
            sprintf "%d dead verb(s), %d possibly referenced dynamically" results.Length dynamicCount

    treeDeadVerbsListEl.innerHTML <- ""

    for objRef, verbName, dobj, prep, iobj, possiblyDynamic in results do
        let li = document.createElement ("li")
        li.classList.add "picker-item"
        li.classList.add "inspector-link"
        li.onclick <- fun _ -> openOrSwitchToVerb objRef verbName

        let call = sprintf "#%d:%s(%s, %s, %s)" objRef verbName dobj prep iobj

        li.textContent <-
            if possiblyDynamic then
                sprintf "%s (possibly referenced dynamically)" call
            else
                call

        treeDeadVerbsListEl.appendChild li |> ignore

/// Same shape as `renderDeadVerbsResults`, for `moodev/findDeadProperties`'
/// results - clicking a row opens the *owning object's inspector*
/// (`openOrSwitchToInspector`), not a verb tab, since properties don't have
/// an editor of their own the way verbs do.
and private renderDeadPropertiesResults (results: (int64 * string * bool)[]) : unit =
    let dynamicCount = results |> Array.filter (fun (_, _, possiblyDynamic) -> possiblyDynamic) |> Array.length

    treeDeadPropertiesSummaryEl.textContent <-
        if results.Length = 0 then
            "No dead properties found."
        else
            let noun = if results.Length = 1 then "property" else "properties"
            sprintf "%d dead %s, %d possibly referenced dynamically" results.Length noun dynamicCount

    treeDeadPropertiesListEl.innerHTML <- ""

    for objRef, propertyName, possiblyDynamic in results do
        let li = document.createElement ("li")
        li.classList.add "picker-item"
        li.classList.add "inspector-link"
        li.onclick <- fun _ -> openOrSwitchToInspector objRef

        let label = sprintf "#%d.%s" objRef propertyName

        li.textContent <-
            if possiblyDynamic then
                sprintf "%s (possibly referenced dynamically)" label
            else
                label

        treeDeadPropertiesListEl.appendChild li |> ignore

/// Human-readable label for one of `Handlers.GotchaEntry`'s plain-string
/// `Kind` tags - kept client-side (like `renderDeadVerbsResults`'s own
/// "possibly referenced dynamically" phrasing) rather than sent over the
/// wire, since it's presentation, not data.
and private gotchaKindLabel (kind: string) : string =
    match kind with
    | "missing-x-bit" -> "missing the x (executable) bit despite a likely caller"
    | "unbounded-loop" -> "loop with no suspend() anywhere in its body"
    | "zero-index" -> "list[0] indexing - always raises E_RANGE"
    | "arg-shape-mismatch" -> "called somewhere with an argument count its own arg-spec can't accept"
    | "inheritance-cycle" -> "member of a cycle in the parent-inheritance graph"
    | "diamond-verb-ambiguity" -> "2+ parents each define this verb - resolution depends on parent order"
    | "verb-argspec-mismatch" -> "dobj/prep/iobj differs from the nearest ancestor's own definition"
    | "verb-return-mismatch" -> "ancestor may return a value, this override never does"
    | other -> other

/// Renders `moodev/findGotchas`' results into the Gotchas sidebar view -
/// same shape as `renderDeadVerbsResults`, one entry per (object, verb,
/// kind) triple (a verb can appear more than once if it trips more than one
/// check), clickable straight through to that verb.
and private renderGotchasResults (results: (int64 * string * string)[]) : unit =
    treeGotchasSummaryEl.textContent <-
        if results.Length = 0 then
            "No gotchas found."
        else
            sprintf "%d gotcha(s) found." results.Length

    treeGotchasListEl.innerHTML <- ""

    for objRef, verbName, kind in results do
        let li = document.createElement ("li")
        li.classList.add "picker-item"
        li.classList.add "inspector-link"

        // "inheritance-cycle" is object-level, not verb-level (see
        // `GotchaEntry.VerbName`'s doc comment) - no verb to jump to, so
        // this falls through to the object's own inspector instead.
        if verbName = "" then
            li.onclick <- fun _ -> openOrSwitchToInspector objRef
            li.textContent <- sprintf "#%d - %s" objRef (gotchaKindLabel kind)
        else
            li.onclick <- fun _ -> openOrSwitchToVerb objRef verbName
            li.textContent <- sprintf "#%d:%s - %s" objRef verbName (gotchaKindLabel kind)

        treeGotchasListEl.appendChild li |> ignore

/// Human-readable label for one of `Handlers.PermissionRiskEntry`'s
/// plain-string `Kind` tags - same client-side-presentation reasoning as
/// `gotchaKindLabel` above.
and private permissionRiskKindLabel (kind: string) : string =
    match kind with
    | "wizard-writable-verb" -> "writable verb owned by a wizard - anyone can overwrite its code"
    | "world-writable-property" -> "world-writable property - anyone can overwrite its value"
    | other -> other

/// Renders `moodev/findPermissionRisks`' results into the Permission risks
/// sidebar view - same shape as `renderGotchasResults`, one entry per
/// (object, name, kind) triple. A `"wizard-writable-verb"` entry's `Name` is
/// a verb name (clicks through to that verb, like the Gotchas view); a
/// `"world-writable-property"` entry's `Name` is a property name (clicks
/// through to the object's inspector, like `renderDeadPropertiesResults`).
and private renderPermissionRisksResults (results: (int64 * string * string)[]) : unit =
    treePermissionRisksSummaryEl.textContent <-
        if results.Length = 0 then
            "No permission risks found."
        else
            sprintf "%d permission risk(s) found." results.Length

    treePermissionRisksListEl.innerHTML <- ""

    for objRef, name, kind in results do
        let li = document.createElement ("li")
        li.classList.add "picker-item"
        li.classList.add "inspector-link"

        let label = document.createElement ("span")
        label.classList.add "inspector-link"
        label.textContent <- sprintf "#%d.%s - %s" objRef name (permissionRiskKindLabel kind)
        label.onclick <-
            fun _ ->
                if kind = "wizard-writable-verb" then
                    openOrSwitchToVerb objRef name
                else
                    openOrSwitchToInspector objRef

        // Both flagged kinds are fixed the same way (strip the `w` bit) -
        // see `IdeActions.fixPermissionRisk`. Disabled immediately on click
        // so a slow round trip can't be double-submitted; the panel
        // re-scans on success (see the `moodev-permission-risk-fix-result`
        // handler), which naturally removes this row once actually fixed.
        let fixBtn = document.createElement ("button") :?> Browser.Types.HTMLButtonElement
        fixBtn.classList.add "picker-fix-btn"
        fixBtn.textContent <- "Fix"
        fixBtn.title <- "Strip the world-writable bit"

        fixBtn.onclick <-
            fun ev ->
                ev.stopPropagation () |> ignore
                fixBtn.disabled <- true
                fixBtn.textContent <- "Fixing..."
                sendAction [ "action" ==> "fix-permission-risk"; "obj" ==> int objRef; "name" ==> name; "kind" ==> kind ]

        li.appendChild label |> ignore
        li.appendChild fixBtn |> ignore
        treePermissionRisksListEl.appendChild li |> ignore

/// Shows one docs entry's full signature + description in the detail pane -
/// the only thing clicking a row in the docs list does. Unlike every other
/// clickable list in this sidebar, a docs entry is a pure reference with
/// nothing to navigate to, so this just renders text in place rather than
/// calling `openOrSwitchToVerb`/`openOrSwitchToInspector`.
and private showDocsDetail (signature: string) (description: string) (kind: string) : unit =
    docsDetailEl.innerHTML <- ""

    let heading = document.createElement ("div")
    heading.classList.add "docs-detail-heading"
    heading.textContent <- signature
    docsDetailEl.appendChild heading |> ignore

    let kindEl = document.createElement ("div")
    kindEl.classList.add "docs-detail-kind"
    kindEl.textContent <- kind
    docsDetailEl.appendChild kindEl |> ignore

    let body = document.createElement ("div")
    body.classList.add "docs-detail-description"
    body.textContent <- description
    docsDetailEl.appendChild body |> ignore

/// Renders the docs list from `moocodeDocsCache`, filtered to `filterText`
/// (case-insensitive substring match against name or description) - called
/// both right after the one-time fetch and on every search-box keystroke,
/// same "filter an already-fetched array client-side" shape the tree
/// filter uses (`treeFilterEl.oninput`), not a server round trip per
/// keystroke like history search - the whole catalog is small and static
/// for the session, so there's nothing to gain by re-asking the server.
and private renderDocsList (filterText: string) : unit =
    docsListEl.innerHTML <- ""

    match moocodeDocsCache with
    | None -> ()
    | Some allEntries ->
        let needle = filterText.Trim().ToLowerInvariant()

        let matches (name: string, _signature, description: string, _kind) =
            needle = "" || name.ToLowerInvariant().Contains(needle) || description.ToLowerInvariant().Contains(needle)

        let kindOrder (kind: string) =
            match kind with
            | "keyword" -> 0
            | "variable" -> 1
            | "error" -> 3
            | _ -> 2

        let filtered =
            allEntries
            |> Array.filter matches
            |> Array.sortBy (fun (name, _, _, kind) -> kindOrder kind, name)

        if Array.isEmpty filtered then
            let li = document.createElement ("li")
            li.textContent <- "No matches."
            li.classList.add "placeholder"
            docsListEl.appendChild li |> ignore
        else
            for name, signature, description, kind in filtered do
                let li = document.createElement ("li")
                li.classList.add "picker-item"

                let nameSpan = document.createElement ("span")
                nameSpan.textContent <- name
                li.appendChild nameSpan |> ignore

                let kindSpan = document.createElement ("span")
                kindSpan.classList.add "docs-kind-badge"
                kindSpan.textContent <- kind
                li.appendChild kindSpan |> ignore

                li.onclick <- fun _ -> showDocsDetail signature description kind
                docsListEl.appendChild li |> ignore

/// Rebuilds `#verb-tabs` (the dynamic, closable, drag-reorderable tabs) and
/// the static `#tab-game` button's `.active` state. `#tab-game` itself is
/// never recreated - only its highlight changes.
and private renderTabs () : unit =
    verbTabsEl.innerHTML <- ""

    let renderOneTab (tab: OpenTab) : HTMLElement =
        let el = document.createElement ("div")
        el.classList.add "main-tab"
        el.setAttribute ("draggable", "true")
        if activeTab = tab then el.classList.add "active"

        let label = document.createElement ("span")
        label.classList.add "main-tab-label"

        let closeAction: unit -> unit =
            match tab with
            | VerbTab(objRef, verbName) ->
                if previewTab = Some(objRef, verbName) then el.classList.add "preview"
                label.textContent <- sprintf "%s (#%d)" verbName objRef
                label.onclick <- fun _ -> switchToTab (VerbTab(objRef, verbName))

                // Double-click "pins" a preview tab - it stops being subject
                // to replacement by the next verb opened, same as VS Code.
                label.ondblclick <-
                    fun _ ->
                        if previewTab = Some(objRef, verbName) then
                            previewTab <- None
                            renderTabs ()

                fun () -> closeTab (objRef, verbName)
            | InspectorTab objRef ->
                // Inspector tabs share the same strip as verb tabs (an "ⓘ
                // #N" label, same close-× behavior, and the same
                // preview-tab mechanic) - unlike verb tabs, clicking one
                // always re-loads it fresh (`openOrSwitchToInspector`, not
                // a bare `switchToTab`).
                if previewInspectorTab = Some objRef then el.classList.add "preview"
                label.textContent <- sprintf "ⓘ #%d" objRef
                label.onclick <- fun _ -> openOrSwitchToInspector objRef

                label.ondblclick <-
                    fun _ ->
                        if previewInspectorTab = Some objRef then
                            previewInspectorTab <- None
                            renderTabs ()

                fun () -> closeInspectorTab objRef
            | GameTab ->
                // Never appears in `tabOrder` - Game is a static button
                // outside the draggable strip.
                fun () -> ()

        let closeBtn = document.createElement ("button")
        closeBtn.classList.add "main-tab-close"
        closeBtn.textContent <- "×"
        closeBtn.onclick <- fun ev -> ev.stopPropagation () |> ignore; closeAction ()

        // Middle-click anywhere on the tab closes it, matching VS Code -
        // `preventDefault` on `mousedown` (not just the `click`/`auxclick`
        // that would follow) since the middle button's default action,
        // autoscroll mode, otherwise activates before either fires.
        el.onmousedown <-
            fun ev ->
                if ev.button = 1.0 then
                    ev.preventDefault ()
                    closeAction ()

        // Drag-to-reorder: dropping onto another tab inserts the dragged
        // tab immediately before it in `tabOrder` (identity-based, not
        // index-arithmetic, so it can't drift out of sync with the list).
        el.ondragstart <-
            fun _ ->
                draggedTab <- Some tab
                el.classList.add "dragging"

        el.ondragover <- fun ev -> ev.preventDefault ()

        el.ondrop <-
            fun ev ->
                ev.preventDefault ()
                ev.stopPropagation ()

                match draggedTab with
                | Some dragged when dragged <> tab ->
                    let without = tabOrder |> List.filter (fun t -> t <> dragged)
                    let targetIdx = without |> List.findIndex (fun t -> t = tab)
                    tabOrder <- (without |> List.take targetIdx) @ [ dragged ] @ (without |> List.skip targetIdx)
                    draggedTab <- None
                    renderTabs ()
                | _ -> ()

        el.ondragend <-
            fun _ ->
                draggedTab <- None
                el.classList.remove "dragging"

        el.appendChild label |> ignore
        el.appendChild closeBtn |> ignore
        el

    for tab in tabOrder do
        verbTabsEl.appendChild (renderOneTab tab) |> ignore

    // Container-level fallback: dropping into empty space past the last tab
    // (rather than onto a specific tab) appends the dragged tab to the end.
    // The per-tab `ondrop`'s `stopPropagation()` above keeps this from also
    // firing when a drop lands on a specific tab.
    verbTabsEl.ondragover <- fun ev -> ev.preventDefault ()

    verbTabsEl.ondrop <-
        fun ev ->
            ev.preventDefault ()

            match draggedTab with
            | Some dragged ->
                tabOrder <- (tabOrder |> List.filter (fun t -> t <> dragged)) @ [ dragged ]
                draggedTab <- None
                renderTabs ()
            | None -> ()

    if activeTab = GameTab then
        tabGameBtn.classList.add "active"
    else
        tabGameBtn.classList.remove "active"

/// Whether `node` itself is a filter match - object name only, plain
/// substring match (see `matchesFilter`).
and private nodeMatches (filterText: string) (node: TreeNode) : bool = matchesFilter filterText node.Name

/// Every objRef that needs to be expanded for at least one filter match to
/// be reachable - a match's *every* parent, recursively (via `ancestorsOf`),
/// since a DAG node can have more than one parent path to a root and each
/// occurrence needs its own ancestor chain expanded for the match to be
/// visible wherever it appears.
and private ancestorExpansionSet (filterText: string) : Set<int64> =
    treeNodes
    |> Map.toSeq
    |> Seq.map snd
    |> Seq.filter (nodeMatches filterText)
    |> Seq.map (fun n -> n.ObjRef)
    |> Seq.fold (fun acc r -> Set.union acc (Set.add r (ancestorsOf Set.empty r))) Set.empty

/// One row of the flattened, currently-*visible* tree - an object, with its
/// depth and whether it has anything to expand into. Children render
/// directly under their expanded parent, one depth deeper - no separate
/// "Children" grouping node to open first.
and private flattenVisibleRows
    (hideEmptyLeaves: bool)
    (expanded: Set<int64>)
    (roots: int64[])
    : TreeRow list =
    // "Empty leaf" = no children *and* no verbs/properties of its own - a
    // genuine dead end, not just "nothing to expand in the tree" (an
    // object can easily have real verbs/properties, visible in the
    // inspector, while still being a tree leaf - that's not empty, just
    // terminal). Applied both to a parent's own children (`childrenOf`)
    // and to the top-level `roots` just below - a root used to stay
    // visible regardless of this toggle (only ever checked for a node's
    // children, never the roots themselves); fixing *that* then briefly
    // over-corrected to "any childless object", which wrongly hid `#5`
    // too (childless, but has its own verbs) alongside genuinely-empty
    // `#4`.
    let isEmptyLeaf (ref: int64) : bool =
        match Map.tryFind ref treeNodes with
        | None -> false // unknown ref - show rather than silently drop
        | Some n -> Array.isEmpty n.Children && not n.HasOwnContent

    let childrenOf (node: TreeNode) : int64[] =
        node.Children |> Array.filter (fun childRef -> not hideEmptyLeaves || not (isEmptyLeaf childRef))

    let rec go (visited: Set<int64>) (depth: int) (objRef: int64) : TreeRow list =
        match Map.tryFind objRef treeNodes with
        | None -> []
        | Some _ when Set.contains objRef visited ->
            [ objRef, depth, false ] // cycle guard: render once, never recurse again
        | Some node ->
            let visited = Set.add objRef visited
            let visibleChildren = childrenOf node

            // Reflects only actually-known children - a node not yet live-
            // checked shows no arrow rather than one shown speculatively
            // (see `liveChildrenRequested`'s own comment for how live-only
            // children still get discovered without one).
            let isExpandable = not (Array.isEmpty visibleChildren)

            let selfRow: TreeRow = objRef, depth, isExpandable

            if not (Set.contains objRef expanded) then
                [ selfRow ]
            else
                selfRow
                :: (visibleChildren
                    |> Array.sort
                    |> Array.collect (fun r -> go visited (depth + 1) r |> Array.ofList)
                    |> List.ofArray)

    roots
    |> Array.filter (fun r -> not hideEmptyLeaves || not (isEmptyLeaf r))
    |> Array.sort
    |> Array.collect (fun r -> go Set.empty 0 r |> Array.ofList)
    |> List.ofArray

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
        for objRef, depth, isExpandable in rows do
            let li = document.createElement ("li")
            li.classList.add "picker-row"
            li.classList.add "tree-row"
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

            match colorForObject objRef with
            | Some color -> kindIcon.setAttribute ("style", sprintf "color: %s" color)
            | None -> ()

            li.appendChild kindIcon |> ignore

            let labelSpan = document.createElement ("span")

            labelSpan.textContent <-
                (Map.tryFind objRef treeNodes |> Option.map (fun n -> n.Name) |> Option.defaultValue (sprintf "#%d" objRef))

            li.appendChild labelSpan |> ignore

            if selectedObjRef = Some objRef then
                li.classList.add "selected"

            li.onclick <-
                fun _ ->
                    // Remember this as "the one" while a filter's active,
                    // so clearing it (`promoteFilterExpansionIfAny`) keeps
                    // this object in view - not every other match too.
                    if treeFilterText.Trim() <> "" then
                        lastFilterSelectedObjRef <- Some objRef

                    // Selects and loads this object's inspector - always
                    // fresh, and highlights the row immediately
                    // (`openOrSwitchToInspector` sets `selectedObjRef`
                    // itself).
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

            treeListEl.appendChild li |> ignore

/// Sets (or overwrites) the tree-color rule for `objRef` - called from its
/// own inspector's color swatch. Refreshes both the popover's rule list and
/// the tree itself so the change is visible immediately in both places.
and private setColorRule (objRef: int64) (label: string) (color: string) : unit =
    colorRules <- (colorRules |> List.filter (fun r -> r.TypeObjRef <> objRef)) @ [ { TypeObjRef = objRef; TypeLabel = label; Color = color } ]
    saveColorRulesToStorage ()
    renderColorRulesList ()
    renderTree ()

/// Removes the tree-color rule for `objRef`, if any - called from either its
/// own inspector's clear button or the popover's per-rule remove button.
and private removeColorRule (objRef: int64) : unit =
    colorRules <- colorRules |> List.filter (fun r -> r.TypeObjRef <> objRef)
    saveColorRulesToStorage ()
    renderColorRulesList ()
    renderTree ()

/// Rebuilds the "Tree display options" popover's color-rules list from
/// `colorRules` - the only place a rule for an object *other than* the one
/// currently open in the inspector can be reviewed or removed.
and private renderColorRulesList () : unit =
    treeColorRulesListEl.innerHTML <- ""

    if List.isEmpty colorRules then
        let empty = document.createElement ("div")
        empty.textContent <- "No color rules yet - set one from an object's inspector."
        empty.classList.add "tree-color-rules-empty"
        treeColorRulesListEl.appendChild empty |> ignore
    else
        for rule in colorRules do
            let row = document.createElement ("div")
            row.classList.add "tree-color-rule-row"

            let swatch = document.createElement ("span")
            swatch.classList.add "tree-color-swatch"
            swatch.setAttribute ("style", sprintf "background-color: %s" rule.Color)
            row.appendChild swatch |> ignore

            let label = document.createElement ("span")

            // Same best-effort staleness comparison `staleTabWarnings` uses
            // for restored tabs - `TypeLabel` is already exactly the name
            // fingerprint that check needs, just never re-checked against
            // the live tree before now. `""` on either side (unknown at
            // rule-creation time, or this object not yet known to
            // `treeNodes`) means "nothing to compare", not a mismatch.
            let currentLabel = currentLiveLabel rule.TypeObjRef

            if currentLabel <> "" && currentLabel <> rule.TypeLabel then
                label.textContent <- sprintf "#%d - was '%s', now '%s' (recycled/reused?)" rule.TypeObjRef rule.TypeLabel currentLabel
                label.classList.add "tree-color-rule-stale"
            else
                label.textContent <- rule.TypeLabel

            row.appendChild label |> ignore

            let removeBtn = document.createElement ("button")
            removeBtn.classList.add "inspector-row-delete-btn"
            removeBtn.textContent <- "🗑"
            removeBtn.title <- sprintf "Remove color rule for %s" rule.TypeLabel
            removeBtn.onclick <- fun _ -> removeColorRule rule.TypeObjRef
            row.appendChild removeBtn |> ignore

            treeColorRulesListEl.appendChild row |> ignore

/// Fires an automatic, one-time `get-live-children` for every currently
/// rendered leaf row (`isExpandable = false`) not yet checked or already
/// requested - keeps live-only children (see `liveChildrenChecked`'s own
/// comment) discoverable without ever showing the user a chevron to click.
/// The `moodev-live-children` response handler adds the object to
/// `liveChildrenChecked` and calls `renderTree ()` itself, so a node that
/// turns out to actually have live children gets its chevron the moment the
/// round trip lands - no explicit follow-up needed here.
and private requestUncheckedLeaves (rows: TreeRow list) : unit =
    for objRef, _, isExpandable in rows do
        if
            not isExpandable
            && not (Set.contains objRef liveChildrenChecked)
            && not (Set.contains objRef liveChildrenRequested)
        then
            liveChildrenRequested <- Set.add objRef liveChildrenRequested
            sendAction [ "action" ==> "get-live-children"; "obj" ==> int objRef ]

/// Recomputes and redraws the visible tree from `treeNodes`/`expandedRefs`/
/// `treeFilterText` - the single entry point every state change (expand
/// toggle, filter keystroke, tab switch, hide-empty-leaves setting) calls
/// to stay in sync, matching this file's existing "full rebuild, no
/// incremental DOM patching" style.
and private renderTree () : unit =
    let hideEmptyLeaves = Settings.hideEmptyLeavesEnabled ()

    if treeFilterText.Trim() = "" then
        let rows = flattenVisibleRows hideEmptyLeaves expandedRefs rootRefs
        requestUncheckedLeaves rows
        renderTreeRows rows
    else
        let ancestorRefs = ancestorExpansionSet treeFilterText
        let expanded = Set.union expandedRefs ancestorRefs
        let allRows = flattenVisibleRows hideEmptyLeaves expanded rootRefs

        // Keep a row if it's itself a match, or an ancestor on the way to
        // one - expansion only ever reveals a path *down* to a match.
        let visibleRows =
            allRows
            |> List.filter (fun (objRef, _, _) ->
                Set.contains objRef ancestorRefs
                || (Map.tryFind objRef treeNodes |> Option.map (nodeMatches treeFilterText) |> Option.defaultValue false))

        requestUncheckedLeaves visibleRows
        renderTreeRows visibleRows

/// Reveals `objRef` in the tree (expanding every ancestor path to it) and
/// opens `verbName` directly - used by go-to-definition, which already
/// knows exactly which verb it wants open.
and private revealAndOpenVerb (objRef: int64) (verbName: string) : unit =
    expandedRefs <- Set.union expandedRefs (Set.add objRef (ancestorsOf Set.empty objRef))
    renderTree ()
    openOrSwitchToVerb objRef verbName

/// Command palette (Ctrl+P jump-to-anything) - one entry per object plus
/// one per own verb (`objname:verbname`), built fresh from `treeNodes` on
/// every open - object-model-scale, not corpus-of-documents scale, so
/// there's no need for a smarter index than a flat list, same tradeoff the
/// tree's own live filter already makes.
type private PaletteEntry =
    | PaletteObject of objRef: int64 * label: string
    | PaletteVerb of objRef: int64 * verbName: string * label: string

let private paletteEntryLabel (entry: PaletteEntry) : string =
    match entry with
    | PaletteObject(_, label) -> label
    | PaletteVerb(_, _, label) -> label

let private allPaletteEntries () : PaletteEntry list =
    treeNodes
    |> Map.toList
    |> List.collect (fun (objRef, node) ->
        PaletteObject(objRef, node.Name)
        :: (node.Verbs |> Array.toList |> List.map (fun v -> PaletteVerb(objRef, v, sprintf "%s:%s" node.Name v))))

let mutable private paletteAllEntries: PaletteEntry list = []
let mutable private paletteVisibleEntries: PaletteEntry list = []
let mutable private paletteSelectedIndex = 0

let private closeCommandPalette () : unit =
    commandPaletteOverlayEl.classList.remove "visible"

let private activatePaletteEntry (entry: PaletteEntry) : unit =
    closeCommandPalette ()

    match entry with
    | PaletteObject(objRef, _) -> openOrSwitchToInspector objRef
    | PaletteVerb(objRef, verbName, _) -> openOrSwitchToVerb objRef verbName

let private renderCommandPaletteResults () : unit =
    commandPaletteListEl.innerHTML <- ""

    if List.isEmpty paletteVisibleEntries then
        let li = document.createElement ("li")
        li.textContent <- "no matches"
        li.classList.add "placeholder"
        commandPaletteListEl.appendChild li |> ignore
    else
        paletteVisibleEntries
        |> List.iteri (fun i entry ->
            let li = document.createElement ("li")
            li.textContent <- paletteEntryLabel entry
            if i = paletteSelectedIndex then li.classList.add "selected"
            li.onclick <- fun _ -> activatePaletteEntry entry
            commandPaletteListEl.appendChild li |> ignore)

/// Re-filters from `paletteAllEntries` (the full, unfiltered snapshot taken
/// at open time), not from `paletteVisibleEntries` - each keystroke filters
/// fresh from the whole set, same as the tree's own filter box.
let private filterCommandPalette () : unit =
    paletteVisibleEntries <-
        paletteAllEntries |> List.filter (fun e -> matchesFilter commandPaletteInputEl.value (paletteEntryLabel e))

    paletteSelectedIndex <- 0
    renderCommandPaletteResults ()

let private openCommandPalette () : unit =
    paletteAllEntries <- allPaletteEntries () |> List.sortBy paletteEntryLabel
    paletteVisibleEntries <- paletteAllEntries
    paletteSelectedIndex <- 0
    commandPaletteInputEl.value <- ""
    renderCommandPaletteResults ()
    commandPaletteOverlayEl.classList.add "visible"
    commandPaletteInputEl.focus ()

commandPaletteInputEl.oninput <- fun _ -> filterCommandPalette ()

commandPaletteInputEl.onkeydown <-
    fun ev ->
        match ev.key with
        | "ArrowDown" ->
            ev.preventDefault ()

            if not (List.isEmpty paletteVisibleEntries) then
                paletteSelectedIndex <- min (paletteSelectedIndex + 1) (paletteVisibleEntries.Length - 1)
                renderCommandPaletteResults ()
        | "ArrowUp" ->
            ev.preventDefault ()

            if not (List.isEmpty paletteVisibleEntries) then
                paletteSelectedIndex <- max (paletteSelectedIndex - 1) 0
                renderCommandPaletteResults ()
        | "Enter" ->
            ev.preventDefault ()
            paletteVisibleEntries |> List.tryItem paletteSelectedIndex |> Option.iter activatePaletteEntry
        | "Escape" ->
            ev.preventDefault ()
            closeCommandPalette ()
        | _ -> ()

commandPaletteOverlayEl.onclick <- fun _ -> closeCommandPalette ()
commandPalettePanelEl.onclick <- fun ev -> ev.stopPropagation () |> ignore

// The first `document.onkeydown` in this app - `preventDefault()` is
// required to beat the browser's own Ctrl+P print dialog.
document.onkeydown <-
    fun ev ->
        if ev.ctrlKey && (ev.key = "p" || ev.key = "P") then
            ev.preventDefault ()
            openCommandPalette ()

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

/// Rebuilds whatever tab layout was open the last time this browser tab
/// closed or reloaded (see `persistTabs`/`loadPersistedTabs`). Hooked into
/// `moodev-login-result`'s success branch, after `buildTree`/`get-live-roots`
/// - a real logged-in session is required before `fetch-verb`/`get-live-info`
/// mean anything.
///
/// Every persisted tab is seeded directly into `openVerbTabs`/
/// `openInspectorTabs`/`tabOrder` up front, rather than through
/// `fetchVerb`/`openOrSwitchToInspectorWith` one at a time - both of those
/// paths carry the preview-tab replacement mechanic (see `previewTab`'s own
/// comment), and a fresh page load starts with no preview tab pinned yet to
/// protect the first one restored, so restoring a second tab that way would
/// silently evict the first rather than opening alongside it. Seeding the
/// full list first means every subsequent fetch/load sees its tab as
/// "already open" and skips that mechanic entirely - restored tabs come back
/// pinned, not as a preview.
///
/// Inspector tab content is never cached client-side (`loadInspector`'s
/// "always fresh" rule), so only the persisted-active inspector tab, if any,
/// needs an actual load call - the rest just need to exist in the strip
/// until clicked. Verb tab content, by contrast, needs a real fetch for
/// every restored tab; the previously-active one is fetched last so it's
/// more likely (not guaranteed) to be the last response to land, since the
/// `moodev-edit-content` handler always activates whichever tab's content
/// just arrived (see its own comment on that race) - an accepted limitation
/// shared with the rest of this restore, not something worth building
/// request-correlation infrastructure to close.
/// Best-effort "does this restored tab still point at the object it did
/// when it was saved" - a stored `label` of `""` (unknown at save time) or
/// a current `currentLiveLabel` of `""` (unknown now) means there's nothing
/// to compare, so those never produce a warning; only a genuine non-empty
/// mismatch does. There's no true recycle-generation counter to check
/// against (ToastStunt exposes none - confirmed against `db_private.h`'s
/// `Object` struct), so a same-named replacement object goes undetected -
/// an accepted limitation shared with `Settings`' own color-rule staleness
/// check below, not something this can close without server-side support
/// that doesn't exist.
let private staleTabWarnings (persisted: PersistedTabs) : string list =
    let verbWarnings =
        persisted.VerbTabs
        |> List.choose (fun (objRef, verbName, label) ->
            let current = currentLiveLabel objRef
            if label <> "" && current <> "" && label <> current then
                Some(sprintf "#%d (verb %s) - was '%s', now '%s'" objRef verbName label current)
            else
                None)

    let inspectorWarnings =
        persisted.InspectorTabs
        |> List.choose (fun (objRef, label) ->
            let current = currentLiveLabel objRef
            if label <> "" && current <> "" && label <> current then
                Some(sprintf "#%d (inspector) - was '%s', now '%s'" objRef label current)
            else
                None)

    verbWarnings @ inspectorWarnings

let private restorePersistedTabs () : unit =
    match loadPersistedTabs () with
    | None -> ()
    | Some persisted ->
        for objRef, verbName, _ in persisted.VerbTabs do
            if not (openVerbTabs |> List.contains (objRef, verbName)) then
                openVerbTabs <- openVerbTabs @ [ (objRef, verbName) ]
                tabOrder <- tabOrder @ [ VerbTab(objRef, verbName) ]

        for objRef, _ in persisted.InspectorTabs do
            if not (openInspectorTabs |> List.contains objRef) then
                openInspectorTabs <- openInspectorTabs @ [ objRef ]
                tabOrder <- tabOrder @ [ InspectorTab objRef ]

        renderTabs ()

        match staleTabWarnings persisted with
        | [] -> ()
        | warnings ->
            staleTabWarningTextEl.textContent <-
                sprintf "%d restored tab(s) may point at recycled/reused objects:\n%s" warnings.Length (String.concat "\n" warnings)

            staleTabWarningEl.classList.remove "hidden"

        let activeVerbTab =
            match persisted.Active with
            | PersistedVerb(o, v) -> Some(o, v)
            | _ -> None

        for objRef, verbName, _ in persisted.VerbTabs do
            if Some(objRef, verbName) <> activeVerbTab then
                fetchVerb objRef verbName

        match persisted.Active with
        | PersistedGame -> switchToTab GameTab
        | PersistedInspector objRef -> openOrSwitchToInspectorWith objRef None
        | PersistedVerb(objRef, verbName) -> fetchVerb objRef verbName

// Clicking the already-active view's own icon collapses the sidebar
// instead of (pointlessly) re-switching to the view already showing -
// clicking a *different* icon un-collapses first if needed, so the newly
// chosen view is actually visible. Matches VS Code's own activity-bar
// behavior - the dedicated collapse/expand button this replaced is gone
// entirely (see `Sidebar` module's own comment).
let private onActivityBtnClick (view: SidebarView) : unit =
    if activeSidebarView = view && not (Sidebar.isCollapsed ()) then
        Sidebar.setCollapsed true
    else
        if Sidebar.isCollapsed () then
            Sidebar.setCollapsed false

        switchToSidebarView view

viewTreeBtn.onclick <- fun _ -> onActivityBtnClick TreeView
viewHistoryBtn.onclick <- fun _ -> onActivityBtnClick HistoryView
viewTasksBtn.onclick <- fun _ -> onActivityBtnClick TasksView
viewServerStatusBtn.onclick <- fun _ -> onActivityBtnClick ServerStatusView
viewErrorsBtn.onclick <- fun _ -> onActivityBtnClick ErrorsView
viewDeadVerbsBtn.onclick <- fun _ -> onActivityBtnClick DeadVerbsView
viewDeadPropertiesBtn.onclick <- fun _ -> onActivityBtnClick DeadPropertiesView
viewGotchasBtn.onclick <- fun _ -> onActivityBtnClick GotchasView
viewPermissionRisksBtn.onclick <- fun _ -> onActivityBtnClick PermissionRisksView
viewDocsBtn.onclick <- fun _ -> onActivityBtnClick DocsView
viewScratchpadBtn.onclick <- fun _ -> onActivityBtnClick EvalScratchpadView
viewPropertySearchBtn.onclick <- fun _ -> onActivityBtnClick PropertySearchView
viewWatchBtn.onclick <- fun _ -> onActivityBtnClick WatchView

docsSearchInputEl.oninput <- fun _ -> renderDocsList (docsSearchInputEl.value)

/// Runs whatever's typed in the "Eval scratchpad" input as a MOO
/// expression - see `IdeActions.evalScratchpad`'s own comment for how the
/// result gets reported.
let private runScratchpadEval () : unit =
    let expr = scratchpadInputEl.value.Trim()

    if expr <> "" then
        scratchpadResultEl.textContent <- "Running..."
        sendAction [ "action" ==> "eval-scratchpad"; "expr" ==> expr ]

scratchpadInputEl.onkeydown <- fun ev -> if ev.key = "Enter" then runScratchpadEval ()

/// Adds whatever's typed in the Watch panel's own input to the watch list -
/// same "Enter to submit, then clear the box" shape as the scratchpad's
/// input above, except this appends to a persisted list instead of running
/// once. Ticks once immediately (via `startWatchInterval`'s own first-tick
/// behavior when nothing was running yet, or `tickWatch` directly when the
/// interval was already ticking) so the new expression gets a value without
/// waiting for the next scheduled refresh.
let private addWatchExpr () : unit =
    let expr = watchAddInputEl.value.Trim()

    if expr <> "" && not (List.contains expr watchExprs) then
        watchExprs <- watchExprs @ [ expr ]
        saveWatchExprs watchExprs
        watchAddInputEl.value <- ""
        renderWatchList ()

        if watchIntervalId.IsNone then startWatchInterval () else tickWatch ()

watchAddInputEl.onkeydown <- fun ev -> if ev.key = "Enter" then addWatchExpr ()

/// Runs the Property search view's two inputs (a property name, and a raw
/// MOO comparison expression referencing `val` - see
/// `IdeActions.searchPropertiesByValue`'s own comment) as a live corpus
/// scan. Both fields must be non-empty - a comparison expression alone
/// means nothing without a property name to fetch, and vice versa.
let private runPropertySearch () : unit =
    let name = propertySearchNameInputEl.value.Trim()
    let valueExpr = propertySearchExprInputEl.value.Trim()

    if name <> "" && valueExpr <> "" then
        lastPropertySearchName <- name
        propertySearchResultsEl.innerHTML <- ""
        let li = document.createElement ("li")
        li.textContent <- "Searching..."
        propertySearchResultsEl.appendChild li |> ignore
        sendAction [ "action" ==> "search-properties"; "name" ==> name; "valueExpr" ==> valueExpr ]

propertySearchNameInputEl.onkeydown <- fun ev -> if ev.key = "Enter" then runPropertySearch ()
propertySearchExprInputEl.onkeydown <- fun ev -> if ev.key = "Enter" then runPropertySearch ()

errorsClearBtn.onclick <-
    fun _ ->
        errorLog <- []
        renderErrorsList ()

historySearchInputEl.onkeydown <-
    fun ev ->
        if ev.key = "Enter" && historySearchInputEl.value.Trim() <> "" then
            historySearchResultsEl.innerHTML <- "Searching..."
            sendAction [ "action" ==> "search-history"; "query" ==> historySearchInputEl.value ]

contentSearchInputEl.onkeydown <-
    fun ev ->
        if ev.key = "Enter" && contentSearchInputEl.value.Trim() <> "" then
            contentSearchResultsEl.innerHTML <- "Searching..."
            sendAction [ "action" ==> "search-content"; "query" ==> contentSearchInputEl.value ]
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

// Persistence + the checkbox's initial `checked` state are handled inside
// `Settings.init()` already (called earlier, before `renderTree` existed) -
// this just wires the redraw, now that it's in scope.
treeFilterHideEmptyLeavesEl.onchange <-
    fun _ ->
        Settings.setHideEmptyLeaves treeFilterHideEmptyLeavesEl.``checked``
        renderTree ()

// Starts out showing its empty-state placeholder - populated for real once
// `moodev-login-result` confirms a login (see below).
renderTree ()

// Shows any color rules persisted from a previous session immediately,
// rather than waiting for the first add/remove.
renderColorRulesList ()

onWsOpen <-
    fun _ ->
        connState <- Connected
        renderConnectionStatus ()
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
        // Also correct on a *reconnect* (not just first load): the Sidecar
        // never resumes a prior session (`BridgeHandler` opens a brand-new
        // MOO TCP connection per WebSocket accept), so becoming usable
        // again always means re-running this same bootstrap and getting a
        // fresh login, exactly like today's page-reload path already does.
        sidebarEl.classList.add ("visible")
        activityBarEl.classList.add ("visible")
        mainTabsEl.classList.add ("visible")
        switchToSidebarView TreeView
        PaneResizer.init PaneResizer.LeftRight "moodev-sidebar-width-pct" layoutEl sidebarResizerEl sidebarEl
        Sidebar.init ()
        Login.init (fun cmd -> ws.send cmd)

        // Fetched once per connection (page load), not on every Settings
        // panel open - the target rarely changes except through this same
        // panel's own "Switch & Reload", which updates these fields itself
        // once it succeeds.
        sendAction [ "action" ==> "get-moo-target" ]

// Reconnect backoff: 1s, 2s, 4s, ... capped at 30s. Skipped entirely when
// `expectingTeardown` is set - the one deliberate `window.location.reload()`
// this client ever does is about to tear down this whole page anyway, so
// there's nothing useful for a reconnect attempt to do.
onWsClose <-
    fun _ ->
        appendOutput "\n[disconnected]\n"
        isLoggedIn <- false
        stopWatchInterval ()

        if expectingTeardown then
            connState <- Disconnected
            renderConnectionStatus ()
        else
            let attempt = match connState with | Reconnecting n -> n + 1 | _ -> 1
            connState <- Reconnecting attempt
            renderConnectionStatus ()
            let delayMs = min 30000 (1000 * pown 2 (attempt - 1))
            JS.setTimeout (fun () -> connectWebSocket ()) delayMs |> ignore

onWsError <- fun _ -> appendOutput "\n[connection error]\n"

// "Configurable MOO server target" feature's switch sequence: validate +
// swap the sidecar's own live connection/tree ("reconfigure-target"), then
// bring the language server's static graph in sync
// (LspClient.reloadGraphAsync), then reload the page - the simplest way to
// reset every other piece of this client's own session state (tree, tabs,
// inspector) rather than hand-resetting each mutable one by one.
settingMooSwitchBtn.onclick <-
    fun _ ->
        async {
            settingMooSwitchStatusEl.textContent <- "Switching..."

            let host = settingMooHostEl.value.Trim()
            let treeDir = settingMooTreeDirEl.value.Trim()

            let port =
                match System.Int32.TryParse settingMooPortEl.value with
                | true, n -> n
                | false, _ -> 7777

            let lspBridgePort =
                match System.Int32.TryParse settingMooLspBridgePortEl.value with
                | true, n -> n
                | false, _ -> 7780

            let! ok, message =
                Async.FromContinuations(fun (resolve, _, _) ->
                    pendingReconfigureResolver <- Some resolve

                    sendAction
                        [ "action" ==> "reconfigure-target"
                          "host" ==> host
                          "port" ==> port
                          "lspBridgePort" ==> lspBridgePort
                          "treeDir" ==> treeDir ])

            if ok then
                settingMooSwitchStatusEl.textContent <- "Reloading language server graph..."

                try
                    do! LspClient.reloadGraphAsync treeDir
                    expectingTeardown <- true
                    window.location.reload ()
                with ex ->
                    // The sidecar's own export/commit (above) already succeeded and
                    // currentTarget already switched at this point - only the language
                    // server's static graph failed to follow. Left un-reloaded (no
                    // page refresh) rather than reloading into a half-switched state
                    // with a stale graph and no visible explanation, which is exactly
                    // what silently swallowing this error used to produce.
                    settingMooSwitchStatusEl.textContent <- sprintf "Switched, but graph reload failed: %s" ex.Message
            else
                settingMooSwitchStatusEl.textContent <- sprintf "Failed: %s" message
        }
        |> Async.StartImmediate

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

onWsMessage <-
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
                        // `lines` is the raw, un-reindented text exactly as
                        // the server sent it - compare it against the model
                        // *after* the reindent above already ran, capturing
                        // this fetch's raw-vs-displayed offset before
                        // anything else touches the buffer.
                        recordIndentDelta objRef verb (List.ofArray lines) (editor.getModel ())
                        // A fresh load is a clean baseline - any earlier
                        // failed/in-flight save for this tab no longer
                        // describes anything real.
                        failedSaveTabs <- Set.remove (objRef, verb) failedSaveTabs
                        saveInFlight <- Set.remove (objRef, verb) saveInFlight

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
                                tabIndentDeltas <- Map.remove oldPreview tabIndentDeltas
                                tabOrder <- tabOrder |> List.map (fun t -> if t = VerbTab oldPreview then VerbTab(objRef, verb) else t)
                            | None ->
                                openVerbTabs <- openVerbTabs @ [ (objRef, verb) ]
                                tabOrder <- tabOrder @ [ VerbTab(objRef, verb) ]

                            previewTab <- Some(objRef, verb)

                        // Mirrors `switchToTab`'s own history push - this
                        // handler can't just call `switchToTab` itself
                        // (the content here is fresh off the wire, already
                        // set into the editor above; `switchToTab`'s
                        // `VerbTab` branch would immediately overwrite it
                        // again from the stale `tabContent` map entry that
                        // existed before the `Map.add` a few lines up).
                        tabHistory <- activeTab :: (tabHistory |> List.filter (fun t -> t <> activeTab))
                        activeTab <- VerbTab(objRef, verb)
                        showingVerbHistory <- false
                        showingParentDiff <- false
                        updateCompareParentButton objRef verb
                        updateCallGraph objRef verb
                        showPaneFor activeTab
                        renderTabs ()
                        // Refresh the tree's highlight to follow whatever
                        // just opened - cheap, reuses the already-built tree.
                        renderTree ()
                        // Doesn't go through `switchToTab` (see the comment
                        // above on why), so its own persistence must be
                        // triggered here rather than riding that function's.
                        persistTabs ()
                    | false, _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-edit-result") then
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef ->
                        let key = (objRef, verb)
                        let ok = headerField "ok: " header = Some "1"

                        saveInFlight <- Set.remove key saveInFlight

                        failedSaveTabs <-
                            if ok then Set.remove key failedSaveTabs else Set.add key failedSaveTabs

                        // A successful save makes the server's raw source
                        // exactly what's currently displayed - continuing to
                        // apply the pre-save delta would now introduce drift
                        // instead of correcting it, so drop it back to "no
                        // adjustment" (same as a tab that's never been
                        // fetched).
                        if ok then
                            tabIndentDeltas <- Map.remove key tabIndentDeltas

                        // Resolve any awaiters (e.g. `closeTab`, waiting to
                        // decide whether to confirm a discard) *before* the
                        // UI repaint below - that repaint must never be able
                        // to leave an awaited promise hanging just because
                        // Monaco's marker rendering had a problem of its
                        // own with this particular response.
                        match pendingSaveResolvers |> Map.tryFind key with
                        | Some resolvers ->
                            pendingSaveResolvers <- Map.remove key pendingSaveResolvers
                            for resolve in resolvers do
                                resolve ok
                        | None -> ()

                        // Only the tab this response is actually *for* gets
                        // repainted - a response arriving for a background
                        // tab (its own earlier blur-triggered save) must not
                        // touch whatever's currently on screen.
                        if activeTab = VerbTab(objRef, verb) then
                            if ok then setDirty false

                            editorDiagnosticsEl.textContent <- if ok then "" else String.concat "\n" lines

                            let lineErrors = lines |> Array.toList |> List.choose parseErrorLine
                            Monaco.setErrorMarkers editor (if ok then [] else lineErrors)
                    | false, _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-verb-syntax-check-result") then
                // Live diagnostics - the debounced as-you-type compile
                // probe's result (see `scheduleSyntaxCheck`). Only applied
                // to the tab it's actually for, and only while still dirty
                // - a response landing after a save or a tab switch has
                // nothing left to annotate (the save-time check, or the
                // freshly-loaded tab's own blank marker state, already
                // owns the display at that point).
                match headerField "object: #" header, headerField "verb: " header with
                | Some objNum, Some verb ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = VerbTab(objRef, verb) && isDirty ->
                        Monaco.setErrorMarkers editor (lines |> Array.toList |> List.choose parseErrorLine)
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-login-result") then
                if headerField "ok: " header = Some "1" then
                    isLoggedIn <- true
                    Login.hide ()

                    async {
                        let! nodes = LspClient.getObjectTreeAsync ()
                        buildTree nodes
                        expandedRefs <- Set.empty
                        liveChildrenChecked <- Set.empty
                        liveChildrenRequested <- Set.empty
                        selectedObjRef <- None
                        renderTree ()

                        // Re-render now that `treeNodes` (via `buildTree`
                        // above) has real live names to compare against -
                        // the initial page-load render (line ~5286, before
                        // any login) always sees an empty `treeNodes`, so
                        // every color rule's own staleness check
                        // (`currentLiveLabel`, inside `renderColorRulesList`)
                        // degrades to "nothing to compare" until this runs.
                        renderColorRulesList ()

                        // Parentless live objects (e.g. the LSP's own
                        // `#4`/`#5` bootstrap objects) have no discovery
                        // path via the static preload above or an expand
                        // click - see `mergeLiveRoots`'s own comment - so
                        // this is fetched once per login, the only trigger
                        // point with no equivalent user gesture to hang it
                        // off of. Sequenced strictly *after* `buildTree`
                        // above, not fired in parallel with it - `buildTree`
                        // overwrites `treeNodes`/`rootRefs` wholesale from
                        // the static export, so if this fired concurrently
                        // and its response (a single direct MOO eval,
                        // typically faster than the LSP's full graph fetch)
                        // landed first, `buildTree`'s later overwrite would
                        // silently erase whatever had just been merged in -
                        // confirmed live: an intermittent race, not a
                        // hypothetical one, that made `#4`/`#5` vanish from
                        // the tree unpredictably depending purely on which
                        // response happened to arrive first.
                        sendAction [ "action" ==> "get-live-roots" ]
                        restorePersistedTabs ()
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

                                    match Map.tryFind pname inspectorPropertyStructuredToggles with
                                    | Some(toggleBtn, _) ->
                                        toggleBtn.setAttribute (
                                            "style",
                                            (if looksListOrMapShaped literal then "" else "display:none")
                                        )
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
            elif header.StartsWith("moodev-property-literal-parsed") then
                match headerField "object: #" header, headerField "name: " header with
                | Some objNum, Some pname ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        match Map.tryFind pname inspectorPropertyInputs, Map.tryFind pname inspectorPropertyStructuredToggles with
                        | Some input, Some(toggleBtn, container) ->
                            let result: obj = JS.JSON.parse lines.[0]

                            match result?kind: string with
                            | "list" -> renderStructuredEditor objRef pname input toggleBtn container false (unbox result?elements)
                            | "map" -> renderStructuredEditor objRef pname input toggleBtn container true (unbox result?elements)
                            | _ ->
                                window.alert (
                                    "This property's value has content too complex to edit structurally - edit it as text instead."
                                )
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-waif-properties") then
                match headerField "object: #" header, headerField "name: " header with
                | Some objNum, Some pname ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        match Map.tryFind pname inspectorPropertyInputs, Map.tryFind pname inspectorPropertyStructuredToggles with
                        | Some input, Some(toggleBtn, container) ->
                            let elements = lines |> Array.map (fun line -> JS.JSON.parse line: obj)
                            renderWaifEditor objRef pname input toggleBtn container elements
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-waif-property-result") then
                // Re-fetches rather than patching the row's own value
                // locally - `valueExpr` is an arbitrary MOO expression, not
                // necessarily the literal that ends up stored (e.g. `1+1`),
                // so the canonical `toliteral()` form has to come back from
                // the server, same "always fresh after a write" convention
                // `moodev-prop-add-result` above already uses.
                match headerField "object: #" header, headerField "name: " header with
                | Some objNum, Some pname ->
                    match System.Int64.TryParse objNum with
                    | true, objRef when activeTab = InspectorTab objRef ->
                        if headerField "ok: " header = Some "1" then
                            sendAction [ "action" ==> "get-waif-properties"; "obj" ==> int objRef; "name" ==> pname ]
                        elif not (Array.isEmpty lines) then
                            window.alert (String.concat "\n" lines)
                    | _ -> ()
                | _ -> ()
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
                || header.StartsWith("moodev-prop-info-set-result")
                || header.StartsWith("moodev-verb-info-set-result")
                || header.StartsWith("moodev-verb-args-set-result")
            then
                // All nine share the exact same "full inspector refresh on
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
                            if openVerbTabs |> List.contains (objRef, verb) then
                                closeTabImmediate (objRef, verb)

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
                                closeTabImmediate (o, v)

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
                            // Covers creating a parentless object (e.g.
                            // parent `#-1`) - `parentRef` above would be
                            // invalid and `get-live-children` a no-op, so
                            // this is the only way such a new object ever
                            // joins `rootRefs` (see `mergeLiveRoots`'s own
                            // comment). Cheap and idempotent to just always
                            // re-fetch rather than branching on whether the
                            // parent was actually valid.
                            sendAction [ "action" ==> "get-live-roots" ]
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
            elif header.StartsWith("moodev-live-roots") then
                // Folds parentless live objects into `treeNodes`/`rootRefs` -
                // see `mergeLiveRoots`'s own comment. Same per-line JSON
                // shape as `moodev-live-children` above, just with no
                // `object: #` header field (there's no single parent this
                // response is "for").
                let roots =
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

                mergeLiveRoots roots
                renderTree ()
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
                        let currentCode = editor.getValue ()
                        currentHistoricalCode <- Some historicalCode
                        let diffEditor = getOrCreateHistoryDiffEditor ()
                        Monaco.setDiffModel diffEditor historicalCode currentCode

                        // Nothing to restore when this historical version is
                        // identical to what's already in the editor -
                        // compared per line, ignoring leading/trailing
                        // whitespace and CRLF/LF. Whitespace-insensitive,
                        // not just CRLF-insensitive, because opening a verb
                        // always reindents it to Monaco's own convention
                        // (see `moodev-edit-content`'s own comment on why -
                        // most of the live corpus has no indentation at
                        // all), which very often does not match whatever
                        // indentation style the historical exported version
                        // happens to use - a purely cosmetic difference,
                        // not a real content change, so it shouldn't make an
                        // otherwise-identical version look "different".
                        let normalizeForCompare (s: string) =
                            s.Replace("\r\n", "\n").Split('\n') |> Array.map (fun l -> l.Trim()) |> String.concat "\n"

                        if normalizeForCompare historicalCode = normalizeForCompare currentCode then
                            verbHistoryRestoreBtn.setAttribute ("style", "display:none")
                        else
                            verbHistoryRestoreBtn.setAttribute ("style", "")
                    | _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-verb-at-parent-result") then
                () // parent's copy not found live - shouldn't normally happen (the button only shows once resolved), nothing more to show
            elif header.StartsWith("moodev-verb-at-parent") then
                // The header's own `object:` field is the *ancestor's*
                // objRef (that's who we asked `verb-at-parent` to fetch
                // from), not the tab's own object - so this only checks the
                // verb name against whichever `VerbTab` is actually open,
                // unlike `moodev-verb-at-commit`'s guard above.
                match headerField "verb: " header with
                | Some verb ->
                    match activeTab with
                    | VerbTab(_, activeVerb) when showingParentDiff && activeVerb = verb ->
                        let parentCode = String.concat "\n" lines
                        let currentCode = editor.getValue ()
                        let diffEditor = getOrCreateParentDiffEditor ()
                        Monaco.setDiffModel diffEditor parentCode currentCode
                    | _ -> ()
                | None -> ()
            elif header.StartsWith("moodev-search-result") then
                if activeSidebarView = HistoryView then
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
            elif header.StartsWith("moodev-content-search-result") then
                if activeSidebarView = HistoryView then
                    let results =
                        lines
                        |> Array.choose (fun line ->
                            let parts = line.Split('\t')

                            if parts.Length = 4 then
                                let objRefOpt =
                                    match System.Int64.TryParse parts.[0] with
                                    | true, n -> Some n
                                    | false, _ -> None

                                Some(objRefOpt, parts.[1], parts.[2], parts.[3])
                            else
                                None)
                        |> List.ofArray

                    renderContentSearchResults results
            elif header.StartsWith("moodev-property-search-result") then
                if activeSidebarView = PropertySearchView then
                    let truncated = headerField "truncated: " header = Some "1"

                    let results =
                        lines
                        |> Array.map (fun line ->
                            let o: obj = JS.JSON.parse line
                            int64 (o?objRef: float), (o?name: string), (o?value: string))
                        |> List.ofArray

                    renderPropertySearchResults lastPropertySearchName truncated results
            elif header.StartsWith("moodev-corponym-history") then
                if activeSidebarView = HistoryView then
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
            elif header.StartsWith("moodev-tasks") then
                if activeSidebarView = TasksView then
                    let tasks =
                        lines
                        |> Array.map (fun line ->
                            let o: obj = JS.JSON.parse line

                            {| id = int64 (o?id: float)
                               start = int64 (o?start: float)
                               programmerRef = int64 (o?programmerRef: float)
                               programmer = (o?programmer: string)
                               vlocRef = int64 (o?vlocRef: float)
                               vloc = (o?vloc: string)
                               verb = (o?verb: string)
                               line = int64 (o?line: float)
                               thisRef = int64 (o?thisRef: float)
                               this = (o?this: string)
                               bytes = int64 (o?bytes: float) |})

                    renderTasksList tasks
            elif header.StartsWith("moodev-server-status") then
                if activeSidebarView = ServerStatusView then
                    let listeners =
                        lines
                        |> Array.map (fun line ->
                            let o: obj = JS.JSON.parse line
                            int64 (o?objRef: float), int64 (o?port: float), (o?interfaceName: string), (o?tls: bool))
                        |> List.ofArray

                    renderServerStatusResults listeners
            elif header.StartsWith("moodev-kill-task-result") then
                let ok = headerField "ok: " header = Some "1"

                if ok then
                    if activeSidebarView = TasksView then loadTasks ()
                elif not (Array.isEmpty lines) then
                    window.alert (String.concat "\n" lines)
            elif header.StartsWith("moodev-permission-risk-fix-result") then
                // Re-scanning on success (rather than surgically removing
                // just the fixed row) keeps this in sync with whatever else
                // changed the object meanwhile, same "just reload" shape
                // `moodev-kill-task-result` uses for the Tasks view above.
                let ok = headerField "ok: " header = Some "1"

                if activeSidebarView = PermissionRisksView then
                    if ok then
                        treePermissionRisksSummaryEl.textContent <- "Scanning..."

                        async {
                            let! results = LspClient.findPermissionRisksAsync ()
                            renderPermissionRisksResults results
                        }
                        |> Async.StartImmediate
                    else
                        treePermissionRisksSummaryEl.textContent <- "Fix failed: " + String.concat "\n" lines
            elif header.StartsWith("moodev-scratchpad-result") then
                let ok = headerField "ok: " header = Some "1"
                scratchpadResultEl.textContent <- (if ok then "" else "Error: ") + String.concat "\n" lines
            elif header.StartsWith("moodev-watch-result") then
                // Only trust a reply whose length matches the *current*
                // watch list - `tickWatch` carries no request id, so a
                // reply to a batch sent against a since-edited list (an
                // add/remove landed while this round trip was in flight)
                // would otherwise misalign positionally against
                // `watchExprs`. Dropped rather than applied; the next tick
                // (already scheduled) supersedes it a few seconds later.
                if lines.Length = watchExprs.Length then
                    watchValues <- lines

                    if activeSidebarView = WatchView then
                        renderWatchList ()
            elif header.StartsWith("moodev-error") then
                // Unsolicited push from `#0:handle_uncaught_error`/
                // `handle_task_timeout` (see MOOdy's CLAUDE.md bootstrap
                // verbs) - can arrive at any time, not just while the Errors
                // view is open, so this always logs it; `renderErrorsList`
                // only actually touches the DOM when that view is active.
                let kind = headerField "kind: " header |> Option.defaultValue "error"
                errorLog <- (System.DateTime.Now, kind, lines |> List.ofArray) :: errorLog

                if activeSidebarView = ErrorsView then
                    renderErrorsList ()
            elif header.StartsWith("moodev-moo-target-result") then
                match
                    headerField "host: " header,
                    headerField "port: " header,
                    headerField "lspBridgePort: " header,
                    headerField "treeDir: " header
                with
                | Some host, Some port, Some lspBridgePort, Some treeDir ->
                    settingMooHostEl.value <- host
                    settingMooPortEl.value <- port
                    settingMooLspBridgePortEl.value <- lspBridgePort
                    settingMooTreeDirEl.value <- treeDir
                | _ -> ()
            elif header.StartsWith("moodev-reconfigure-target-result") then
                match pendingReconfigureResolver with
                | Some resolve ->
                    pendingReconfigureResolver <- None
                    let ok = headerField "ok: " header = Some "1"
                    resolve (ok, (if ok then "" else String.concat "\n" lines))
                | None -> ()
            elif header.StartsWith("moodev-rename-result") then
                if headerField "ok: " header = Some "1" then
                    if not (Array.isEmpty lines) then
                        window.alert ("Renamed, with warnings:\n" + String.concat "\n" lines)

                    // A rename's blast radius isn't limited to whichever verb
                    // was open when F2 was pressed - refresh every currently
                    // open verb tab rather than trying to track exactly which
                    // ones the server touched. Inspector tabs don't need this
                    // (`loadInspector`'s "always fresh" rule already re-fetches
                    // on every activation, so a stale one self-corrects the
                    // next time it's clicked).
                    for renameObjRef, renameVerbName in openVerbTabs do
                        fetchVerb renameObjRef renameVerbName
                else
                    window.alert ("Rename failed:\n" + String.concat "\n" lines)
        else
            let text = decoder.decode (ev.data: obj)
            appendOutput text

/// F2 - resolves the verb call under the cursor via `moodev/prepareRename`,
/// confirms the new name (and the unresolved-site caveat, if any) with the
/// user, then sends the `"rename-verb"` Sidecar action with the server's
/// own confirmed-site list. The client never edits anything itself here -
/// it's purely a confirm-and-forward step; the actual rename is entirely
/// server-side (see MOOdy's "server-orchestrated batch action" design
/// note for why this doesn't go through Monaco's native
/// `registerRenameProvider`/`textDocument/rename`).
let private runRenameSymbolFlow () : unit =
    match currentVerbDoc () with
    | None -> ()
    | Some(objRef, verbName) ->
        let position = editor.getPosition ()

        if not (isNullOrUndefined position) then
            let lspLine = (position?lineNumber: int) - 1
            let lspCol = (position?column: int) - 1

            async {
                match! LspClient.prepareRenameAsync objRef verbName lspLine lspCol with
                | None -> window.alert "Nothing to rename here - place the cursor on a resolvable obj:verb(...) call."
                | Some prepared ->
                    let newName: string = window.prompt (sprintf "Rename \"%s\" to:" prepared.VerbName, prepared.VerbName)

                    if not (isNull newName) && newName.Trim() <> "" && newName.Trim() <> prepared.VerbName then
                        let proceed =
                            if prepared.UnresolvedCount > 0 then
                                window.confirm (
                                    sprintf
                                        "%d call site(s) will be updated. %d more call site(s) use this verb's name but couldn't be confirmed statically and won't be renamed. Continue?"
                                        prepared.Sites.Length
                                        prepared.UnresolvedCount
                                )
                            else
                                window.confirm (sprintf "%d call site(s) will be updated. Continue?" prepared.Sites.Length)

                        if proceed then
                            let sitesJson =
                                prepared.Sites
                                |> Array.map (fun s ->
                                    createObj
                                        [ "objRef" ==> float s.ObjRef
                                          "verbName" ==> s.VerbName
                                          "line" ==> s.Line
                                          "col" ==> s.Col
                                          "length" ==> s.Length ])

                            sendAction
                                [ "action" ==> "rename-verb"
                                  "obj" ==> int prepared.ObjRef
                                  "oldName" ==> prepared.VerbName
                                  "newName" ==> newName.Trim()
                                  "sites" ==> sitesJson ]
            }
            |> Async.StartImmediate

Monaco.registerRenameAction editor (fun () -> runRenameSymbolFlow ())
Monaco.registerShowHoverKeybinding editor

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
    getIndentDeltaFor

inputEl.focus ()
