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

// Vite exposes build-time env vars via import.meta.env.VITE_*; there's no
// typed Fable binding for import.meta itself, so this is a direct JS emit.
let private wsUrl: string =
    emitJsExpr () "import.meta.env.VITE_SIDECAR_WS_URL"

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
let private settingHideEmptyLeavesEl = document.getElementById ("setting-hide-empty-leaves") :?> HTMLInputElement
let private settingForgetLoginBtn = document.getElementById ("setting-forget-login")
let private settingForgetLoginStatusEl = document.getElementById ("setting-forget-login-status")

let private layoutEl = document.getElementById ("layout")

let private sidebarEl = document.getElementById ("sidebar")
let private treeFilterEl = document.getElementById ("tree-filter") :?> HTMLInputElement
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

let private appendOutput (text: string) : unit =
    outputEl.textContent <- outputEl.textContent + text
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
        settingHideEmptyLeavesEl.``checked`` <- hideEmptyLeavesEnabled ()

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
// Backdrop click closes the overlay; the panel stops its own clicks from
// bubbling to the backdrop, same "stop propagation so an inner click
// doesn't also trigger the outer handler" pattern `renderTabs`'s close-×
// button uses against its tab's own switch-click.
settingsOverlayEl.onclick <- fun _ -> Settings.hide ()
settingsPanelEl.onclick <- fun ev -> ev.stopPropagation () |> ignore
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

let mutable private activeTab: OpenTab = GameTab

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
    | InspectorTab _ -> None

/// Quotes and escapes a raw string for splicing into MOO source as a string
/// literal - backslash and double-quote are the only two characters classic
/// MOO string literals escape (see moocode-reference.md).
let private mooStringLiteral (s: string) : string =
    let escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
    "\"" + escaped + "\""

/// Turns the editor's current content into a MOO list-of-strings literal
/// suitable for set_verb_code()'s (via $vcs:ide_save's) third argument.
let private mooCodeListLiteral (source: string) : string =
    let lines = source.Replace("\r\n", "\n").Split('\n')
    "{" + (lines |> Array.map mooStringLiteral |> String.concat ", ") + "}"

/// Asks the server to load a verb - unconditionally, no "is this already
/// open" check (that's `openOrSwitchToVerb`'s job). The `moodev-edit-content`
/// handler is what actually adds the resulting tab and shows it once the
/// content arrives.
let private fetchVerb (objExpr: string) (verb: string) : unit =
    ws.send (sprintf "; $vcs:ide_fetch(%s, %s)" objExpr (mooStringLiteral verb))

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
            let codeLiteral = mooCodeListLiteral (editor.getValue ())
            ws.send (sprintf "; $vcs:ide_save(#%d, %s, %s)" objRef (mooStringLiteral verb) codeLiteral)
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

/// Shows whichever pane `tab` needs and hides the other; focuses that
/// pane's primary input.
let private showPaneFor (tab: OpenTab) : unit =
    match tab with
    | GameTab ->
        terminalPaneEl.classList.add "active"
        editorPaneEl.classList.remove "active"
        inspectorPaneEl.classList.remove "active"
        inputEl.focus ()
    | VerbTab _ ->
        terminalPaneEl.classList.remove "active"
        editorPaneEl.classList.add "active"
        inspectorPaneEl.classList.remove "active"
        // The container was `display:none` a moment ago - force Monaco to
        // re-measure rather than rely on ResizeObserver picking this up.
        editor.layout ()
        editor.focus ()
    | InspectorTab _ ->
        terminalPaneEl.classList.remove "active"
        editorPaneEl.classList.remove "active"
        inspectorPaneEl.classList.add "active"

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
/// lookups don't re-scan the array. `Verbs` is this object's own verbs
/// only (already filtered server-side), in the server's declaration order -
/// never re-fetched per click, unlike the old per-selection `listVerbsAsync`
/// round-trip.
type private TreeNode =
    { ObjRef: int64
      Name: string
      Parents: int64[]
      Children: int64[]
      Verbs: string[] }

let mutable private treeNodes: Map<int64, TreeNode> = Map.empty

/// True roots of the object tree - objects with zero parents (`$root_class`
/// and a handful of others, confirmed against the real corpus rather than
/// assumed: `parents(obj)` already returns `{}` for a parentless object,
/// no sentinel ref filtering needed).
let mutable private rootRefs: int64[] = [||]

let private buildTree (nodes: (int64 * string * int64[] * int64[] * string[])[]) : unit =
    treeNodes <-
        nodes
        |> Array.map (fun (objRef, name, parents, children, verbs) ->
            objRef, { ObjRef = objRef; Name = name; Parents = parents; Children = children; Verbs = verbs })
        |> Map.ofArray

    rootRefs <-
        nodes
        |> Array.filter (fun (_, _, parents, _, _) -> Array.isEmpty parents)
        |> Array.map (fun (objRef, _, _, _, _) -> objRef)

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

/// Live filter text, updated on every keystroke in the tree's filter box -
/// see the `oninput` wiring below.
let mutable private treeFilterText = ""

/// One row of the flattened, currently-visible tree.
type private TreeRow =
    | ObjectRow of objRef: int64 * depth: int * isExpandable: bool
    | VerbRow of objRef: int64 * verbName: string * depth: int

/// Switches the main area to `tab`, caching whatever was showing before the
/// switch. A no-op if `tab` is already active (e.g. clicking the tab you're
/// already on).
let rec private switchToTab (tab: OpenTab) : unit =
    if tab <> activeTab then
        cacheCurrentEditorContent ()
        activeTab <- tab

        match tab with
        | GameTab
        | InspectorTab _ -> ()
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
        fetchVerb (sprintf "#%d" objRef) verbName

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

        match activeTab with
        | VerbTab(o, v) ->
            editor.setValue (Map.find (o, v) tabContent)
            setDirty false
        | GameTab
        | InspectorTab _ -> ()

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

    if wasActive then
        activeTab <-
            match openInspectorTabs with
            | [] -> GameTab
            | refs -> InspectorTab refs.[max 0 (min (idx - 1) (refs.Length - 1))]

        showPaneFor activeTab

        match activeTab with
        | InspectorTab o -> loadInspector o
        | GameTab
        | VerbTab _ -> ()

    renderTabs ()

/// Opens `objRef`'s inspector - switches instantly if it's already an open
/// tab (adding it first if not), then *always* kicks off a fresh load
/// (structural info + live property values), even when the tab was already
/// open and already active. Used by the tab strip itself, the sidebar
/// objects list's "ⓘ" icon, and every clickable owner/parent/child link
/// inside the inspector pane - all funnel through here so "already open"
/// and "always fresh" are each handled in exactly one place.
and private openOrSwitchToInspector (objRef: int64) : unit =
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

    switchToTab (InspectorTab objRef)
    loadInspector objRef

/// Fetches and renders `objRef`'s inspector content: structural data
/// (`moodev/getObjectInfo`, over the LSP websocket - cheap, the graph is
/// already in memory server-side) and live property values
/// (`$vcs:ide_get_properties`, over the main MOO websocket - a real
/// round-trip). Deliberately not cached client-side, unlike verb tabs:
/// property values are live, mutable game state, not something this editor
/// owns a stable copy of the way verb source is (nothing else can change a
/// verb's source out from under the editor; plenty can change a property's
/// value out from under the inspector) - so every activation re-fetches
/// both, fresh.
and private loadInspector (objRef: int64) : unit =
    inspectorDiagnosticsEl.textContent <- ""
    inspectorContentEl.textContent <- "Loading..."

    async {
        let! infoOpt = LspClient.getObjectInfoAsync objRef

        // The user may have switched away from (or closed) this inspector
        // tab before this round-trip returned - only apply a stale result
        // if it's still what should be showing.
        if activeTab = InspectorTab objRef then
            match infoOpt with
            | Some info -> renderInspectorStructure objRef info
            | None -> inspectorContentEl.textContent <- sprintf "#%d - not found." objRef
    }
    |> Async.StartImmediate

    ws.send (sprintf "; $vcs:ide_get_properties(#%d)" objRef)

/// Renders a titled list of clickable object links into `container` - shared
/// by the inspector pane's Parents/Children sections. Each entry opens that
/// object's own inspector on click.
and private renderObjRefList (container: HTMLElement) (title: string) (refs: (int64 * string) list) : unit =
    let section = document.createElement ("div")
    let titleEl = document.createElement ("div")
    titleEl.classList.add "inspector-section-title"
    titleEl.textContent <- sprintf "%s (%d)" title refs.Length
    section.appendChild titleEl |> ignore

    let list = document.createElement ("div")
    list.classList.add "inspector-refs"

    for objRef, name in refs do
        let link = document.createElement ("span")
        link.classList.add "inspector-link"
        link.textContent <- name
        link.onclick <- fun _ -> openOrSwitchToInspector objRef
        list.appendChild link |> ignore

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
/// modeling for this one screen.
and private renderInspectorStructure (objRef: int64) (info: obj) : unit =
    inspectorContentEl.innerHTML <- ""
    inspectorPropertyInputs <- Map.empty
    inspectorPropertyLastValues <- Map.empty

    let header = document.createElement ("div")
    header.classList.add "inspector-header"
    header.textContent <- (info?name: string)
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

    inspectorContentEl.appendChild ownerRow |> ignore

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
        let badge = document.createElement ("span")
        badge.classList.add "inspector-flag"
        if isSet then badge.classList.add "set"
        badge.textContent <- flagName
        flagsRow.appendChild badge |> ignore

    inspectorContentEl.appendChild flagsRow |> ignore

    // `?objRef` here is a value freshly parsed from the LSP's JSON response -
    // see the matching comment on `ownerRef` above, same fix, same reason.
    let toRefList (refs: obj[]) : (int64 * string) list =
        refs |> Array.map (fun r -> int64 (r?objRef: float), (r?name: string)) |> Array.toList

    renderObjRefList inspectorContentEl "Parents" (toRefList (unbox info?parents))
    renderObjRefList inspectorContentEl "Children" (toRefList (unbox info?children))

    let verbsSection = document.createElement ("div")
    let verbsTitle = document.createElement ("div")
    verbsTitle.classList.add "inspector-section-title"
    let verbs: obj[] = unbox info?verbs
    verbsTitle.textContent <- sprintf "Verbs (%d)" verbs.Length
    verbsSection.appendChild verbsTitle |> ignore

    let verbsTable = document.createElement ("table")
    verbsTable.classList.add "inspector-table"
    let verbsHeaderRow = document.createElement ("tr")

    for h in [ "Name"; "Perms"; "Dobj"; "Prep"; "Iobj" ] do
        let th = document.createElement ("th")
        th.textContent <- h
        verbsHeaderRow.appendChild th |> ignore

    verbsTable.appendChild verbsHeaderRow |> ignore

    for v in verbs do
        let tr = document.createElement ("tr")
        tr.classList.add "inspector-verb-row"
        let verbName: string = v?name
        tr.onclick <- fun _ -> openOrSwitchToVerb objRef verbName

        for cellText in [ v?name; v?perms; v?dobj; v?prep; v?iobj ] do
            let td = document.createElement ("td")
            td.textContent <- (cellText: string)
            tr.appendChild td |> ignore

        verbsTable.appendChild tr |> ignore

    verbsSection.appendChild verbsTable |> ignore
    inspectorContentEl.appendChild verbsSection |> ignore

    let propsSection = document.createElement ("div")
    let propsTitle = document.createElement ("div")
    propsTitle.classList.add "inspector-section-title"
    let props: obj[] = unbox info?properties
    propsTitle.textContent <- sprintf "Properties (%d)" props.Length
    propsSection.appendChild propsTitle |> ignore

    let propsTable = document.createElement ("table")
    propsTable.classList.add "inspector-table"
    let propsHeaderRow = document.createElement ("tr")

    for h in [ "Name"; "Owner"; "Perms"; "Value" ] do
        let th = document.createElement ("th")
        th.textContent <- h
        propsHeaderRow.appendChild th |> ignore

    propsTable.appendChild propsHeaderRow |> ignore

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
        // `mooStringLiteral` here is doing the same "quote this raw text for
        // safe embedding as MOO source" job it already does for `ide_save`'s
        // verb name/code arguments - the resulting quoted string is what
        // `$vcs:ide_set_property` receives as `literal_text` and itself
        // `eval()`s, so what the user types (`5`, `"hello"`, `{1, 2}`, ...)
        // is evaluated as a real MOO expression, not taken as a raw string.
        input.onblur <-
            fun _ ->
                let lastValue = inspectorPropertyLastValues |> Map.tryFind pname |> Option.defaultValue ""

                if input.value <> lastValue then
                    inspectorPropertyLastValues <- Map.add pname input.value inspectorPropertyLastValues

                    ws.send (
                        sprintf
                            "; $vcs:ide_set_property(#%d, %s, %s)"
                            objRef
                            (mooStringLiteral pname)
                            (mooStringLiteral input.value)
                    )

        valueTd.appendChild input |> ignore
        tr.appendChild valueTd |> ignore
        propsTable.appendChild tr |> ignore
        inspectorPropertyInputs <- Map.add pname input inspectorPropertyInputs

    propsSection.appendChild propsTable |> ignore
    inspectorContentEl.appendChild propsSection |> ignore

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

        tab.appendChild label |> ignore
        tab.appendChild closeBtn |> ignore
        verbTabsEl.appendChild tab |> ignore

    if activeTab = GameTab then
        tabGameBtn.classList.add "active"
    else
        tabGameBtn.classList.remove "active"

/// True if `node` itself is a filter match - its display name, or any of
/// its own verb names.
and private nodeMatches (filterText: string) (node: TreeNode) : bool =
    matchesFilter filterText node.Name || node.Verbs |> Array.exists (matchesFilter filterText)

/// Every objRef that needs to be expanded for at least one filter match to
/// be reachable - a match's *every* parent, recursively (via `ancestorsOf`),
/// since a DAG node can have more than one parent path to a root and each
/// occurrence needs its own ancestor chain expanded for the match to be
/// visible wherever it appears.
and private ancestorExpansionSet (filterText: string) : Set<int64> =
    if filterText = "" then
        Set.empty
    else
        treeNodes
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.filter (nodeMatches filterText)
        |> Seq.map (fun n -> n.ObjRef)
        |> Seq.fold (fun acc r -> Set.union acc (Set.add r (ancestorsOf Set.empty r))) Set.empty

/// One row of the flattened, currently-*visible* tree - either an object
/// (with its depth and whether it has anything to expand into) or one of
/// an expanded object's own verbs.
and private flattenVisibleRows (hideEmptyLeaves: bool) (expanded: Set<int64>) (roots: int64[]) : TreeRow list =
    let childrenOf (node: TreeNode) : int64[] =
        node.Children
        |> Array.filter (fun childRef ->
            not hideEmptyLeaves
            || match Map.tryFind childRef treeNodes with
               | None -> true // unknown ref - show rather than silently drop
               | Some c -> not (Array.isEmpty c.Children) || not (Array.isEmpty c.Verbs))

    let rec go (visited: Set<int64>) (depth: int) (objRef: int64) : TreeRow list =
        match Map.tryFind objRef treeNodes with
        | None -> []
        | Some _ when Set.contains objRef visited ->
            [ ObjectRow(objRef, depth, false) ] // cycle guard: render once, never recurse again
        | Some node ->
            let visited = Set.add objRef visited
            let visibleChildren = childrenOf node
            let isExpandable = not (Array.isEmpty visibleChildren) || not (Array.isEmpty node.Verbs)
            let selfRow = ObjectRow(objRef, depth, isExpandable)

            if not (Set.contains objRef expanded) then
                [ selfRow ]
            else
                let verbRows =
                    node.Verbs
                    |> Array.sort
                    |> Array.map (fun v -> VerbRow(objRef, v, depth + 1))
                    |> List.ofArray

                let childRows =
                    visibleChildren
                    |> Array.sort
                    |> Array.collect (fun r -> go visited (depth + 1) r |> Array.ofList)
                    |> List.ofArray

                selfRow :: (verbRows @ childRows)

    roots |> Array.sort |> Array.collect (fun r -> go Set.empty 0 r |> Array.ofList) |> List.ofArray

/// Renders the currently-visible tree into `#tree-list` - reuses
/// `renderList`'s old DOM idiom (`.picker-row`/`.picker-row-icon`/
/// `.selected`/`.placeholder`), plus depth indentation and an expand
/// chevron on object rows.
and private renderTreeRows (rows: TreeRow list) : unit =
    treeListEl.innerHTML <- ""

    if List.isEmpty rows then
        let li = document.createElement ("li")
        li.textContent <- (if treeFilterText <> "" then "no matches" else "no objects yet")
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

                let iconBtn = document.createElement ("button")
                iconBtn.classList.add "picker-row-icon"
                iconBtn.textContent <- "ⓘ"
                iconBtn.title <- "Open inspector"
                iconBtn.onclick <- fun ev -> ev.stopPropagation () |> ignore; openOrSwitchToInspector objRef
                li.appendChild iconBtn |> ignore

                if activeTab = InspectorTab objRef then
                    li.classList.add "selected"

                li.onclick <-
                    fun _ ->
                        if isExpandable then
                            expandedRefs <-
                                if Set.contains objRef expandedRefs then Set.remove objRef expandedRefs
                                else Set.add objRef expandedRefs

                            renderTree ()
            | VerbRow(objRef, verbName, depth) ->
                li.setAttribute ("style", sprintf "padding-left: %dem" (depth + 1))

                let kindIcon = document.createElement ("span")
                kindIcon.classList.add "tree-icon"
                kindIcon.classList.add "tree-icon-verb"
                kindIcon.textContent <- "ƒ"
                li.appendChild kindIcon |> ignore

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- verbName
                li.appendChild labelSpan |> ignore

                if activeTab = VerbTab(objRef, verbName) then
                    li.classList.add "selected"

                li.onclick <- fun _ -> openOrSwitchToVerb objRef verbName

            treeListEl.appendChild li |> ignore

/// Recomputes and redraws the visible tree from `treeNodes`/`expandedRefs`/
/// `treeFilterText` - the single entry point every state change (expand
/// toggle, filter keystroke, tab switch, hide-empty-leaves setting) calls
/// to stay in sync, matching this file's existing "full rebuild, no
/// incremental DOM patching" style.
and private renderTree () : unit =
    let hideEmptyLeaves = Settings.hideEmptyLeavesEnabled ()

    if treeFilterText = "" then
        renderTreeRows (flattenVisibleRows hideEmptyLeaves expandedRefs rootRefs)
    else
        let ancestorRefs = ancestorExpansionSet treeFilterText
        let expanded = Set.union expandedRefs ancestorRefs
        let allRows = flattenVisibleRows hideEmptyLeaves expanded rootRefs

        // Keep a row if it's itself a match, or an ancestor object-row on
        // the way to one - verb rows never need to survive purely as
        // ancestors, only object rows do (expansion only ever reveals a
        // path *down* to a match).
        allRows
        |> List.filter (fun row ->
            match row with
            | ObjectRow(objRef, _, _) ->
                Set.contains objRef ancestorRefs
                || (Map.tryFind objRef treeNodes |> Option.map (nodeMatches treeFilterText) |> Option.defaultValue false)
            | VerbRow(_, verbName, _) -> matchesFilter treeFilterText verbName)
        |> renderTreeRows

/// Reveals `objRef` in the tree (expanding every ancestor path to it) and
/// opens `verbName` directly - used by go-to-definition, which already
/// knows exactly which verb it wants open. The bulk tree has every
/// object's own verbs in memory already, so there's nothing to wait on the
/// way the old `selectObject`/`listVerbsAsync` round-trip did.
and private revealAndOpenVerb (objRef: int64) (verbName: string) : unit =
    expandedRefs <- Set.union expandedRefs (Set.add objRef (ancestorsOf Set.empty objRef))
    renderTree ()
    openOrSwitchToVerb objRef verbName

tabGameBtn.onclick <- fun _ -> switchToTab GameTab
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
        renderTree ()

// Persistence + the checkbox's initial `checked` state are handled inside
// `Settings.init()` already (called earlier, before `renderTree` existed) -
// this just wires the redraw, now that it's in scope.
settingHideEmptyLeavesEl.onchange <-
    fun _ ->
        Settings.setHideEmptyLeaves settingHideEmptyLeavesEl.``checked``
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
