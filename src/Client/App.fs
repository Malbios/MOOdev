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
let private settingForgetLoginBtn = document.getElementById ("setting-forget-login")
let private settingForgetLoginStatusEl = document.getElementById ("setting-forget-login-status")

let private layoutEl = document.getElementById ("layout")

let private sidebarEl = document.getElementById ("sidebar")
let private objectsFilterEl = document.getElementById ("objects-filter") :?> HTMLInputElement
let private objectsListEl = document.getElementById ("objects-list")
let private verbsFilterEl = document.getElementById ("verbs-filter") :?> HTMLInputElement
let private verbsListEl = document.getElementById ("verbs-list")
let private sidebarResizerEl = document.getElementById ("sidebar-resizer")
let private sidebarSplitResizerEl = document.getElementById ("sidebar-split-resizer")
let private objectsPaneEl = document.getElementById ("objects-pane")

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

/// Draggable dividers between panes - two of them (sidebar/main-area,
/// objects/verbs within the sidebar), each independently resizable and
/// persisted across reloads via localStorage, same "remember what the user
/// set" idea as command history's in-memory list, just surviving a refresh
/// too. (The editor/terminal split used to be a third one, but that pair is
/// now tabs sharing one space instead of a resizable split.)
///
/// Both share one pair of `window`-level mouse handlers rather than each
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

    let private loadString (key: string) (fallback: string) : string =
        match window.localStorage.getItem key with
        | null -> fallback
        | v -> v

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

        settingWordWrapEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingFontSizeEl.onchange <- fun _ -> applyAndSaveFromControls ()
        settingMinimapEl.onchange <- fun _ -> applyAndSaveFromControls ()

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
/// `openVerbTabs`, but with none of that list's caching or preview-tab
/// mechanics: an inspector tab is always permanent (one per object; opening
/// an already-open one just switches to it) and its content is never cached
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

/// Replaces `listEl`'s children with one clickable `<li>` per `(value,
/// label)` pair. When `items` is empty, shows a single unselectable
/// placeholder instead - once there are real items, the list contains ONLY
/// those (no permanent blank entry to navigate past), each highlighted via
/// `isSelected` and wired to `onClick`. `secondaryIcon`, when `Some`, adds a
/// small "ⓘ" button to each row (used by the objects list to open that
/// object's inspector without disturbing the row's own click behavior;
/// `None` for the verbs list, which has no such secondary action).
let private renderList
    (listEl: HTMLElement)
    (placeholder: string)
    (items: (string * string) seq)
    (isSelected: string -> bool)
    (onClick: string -> unit)
    (secondaryIcon: (string -> unit) option)
    : unit =
    listEl.innerHTML <- ""
    let itemsList = items |> List.ofSeq

    if List.isEmpty itemsList then
        let li = document.createElement ("li")
        li.textContent <- placeholder
        li.classList.add "placeholder"
        listEl.appendChild li |> ignore
    else
        for value, label in itemsList do
            let li = document.createElement ("li")

            match secondaryIcon with
            | None -> li.textContent <- label
            | Some onIconClick ->
                li.classList.add "picker-row"

                let labelSpan = document.createElement ("span")
                labelSpan.textContent <- label
                li.appendChild labelSpan |> ignore

                let iconBtn = document.createElement ("button")
                iconBtn.classList.add "picker-row-icon"
                iconBtn.textContent <- "ⓘ" // ⓘ
                iconBtn.title <- "Open inspector"
                iconBtn.onclick <- fun ev -> ev.stopPropagation () |> ignore; onIconClick value
                li.appendChild iconBtn |> ignore

            if isSelected value then li.classList.add "selected"
            li.onclick <- fun _ -> onClick value
            listEl.appendChild li |> ignore

/// All objects the object list currently knows about (populated once, on
/// login) - kept around so `renderObjectsList` can re-render just the
/// `.selected` highlight without re-fetching.
let mutable private allObjects: (int64 * string)[] = [||]

/// Which object's verbs the verb list is currently showing, if any.
let mutable private selectedObjRef: int64 option = None

/// The verb list's data for `selectedObjRef` - cached so `renderVerbsList`
/// can refresh the `.selected` highlight (e.g. right after a verb finishes
/// loading) without re-fetching from the server.
let mutable private currentVerbsForSelectedObj: string[] = [||]

/// Live filter text for each list, updated on every keystroke in the
/// corresponding filter box - see the `oninput` wiring below.
let mutable private objectsFilterText = ""
let mutable private verbsFilterText = ""

/// Case-insensitive substring match - an empty filter matches everything.
let private matchesFilter (filterText: string) (label: string) : bool =
    filterText = "" || label.ToLowerInvariant().Contains(filterText.ToLowerInvariant())

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
        renderVerbsList ()

/// Opens `(objRef, verbName)` - switches instantly from the client-side
/// cache if it's already an open tab, otherwise fetches it from the server
/// (the `moodev-edit-content` handler below adds it to `openVerbTabs` and
/// switches to it once the content arrives). Used by the sidebar verb
/// list's click handler and by go-to-definition (via `selectObject`) - both
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
    renderVerbsList ()

/// Closes an open inspector tab. If it was the active one, falls back the
/// same way `closeTab` does for verb tabs (the tab to its left, or the new
/// first tab, or Game if none remain) - and, per `loadInspector`'s "always
/// fresh" rule, re-loads whichever inspector tab it falls back to rather
/// than showing whatever that tab last happened to render.
and private closeInspectorTab (objRef: int64) : unit =
    let wasActive = activeTab = InspectorTab objRef
    let idx = openInspectorTabs |> List.findIndex (fun r -> r = objRef)
    openInspectorTabs <- openInspectorTabs |> List.filter (fun r -> r <> objRef)

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
        openInspectorTabs <- openInspectorTabs @ [ objRef ]

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

/// Builds the inspector pane's DOM from a `moodev/getObjectInfo` result:
/// header, a clickable owner link, permission-flag badges, clickable
/// parents/children lists, a read-only verbs table, and a properties table
/// whose value cells are editable `<input>`s (seeded blank here - filled in
/// once `ide_get_properties`'s response arrives, matched up by property
/// name via `inspectorPropertyInputs`). Kept as loosely-typed `obj` (dynamic
/// `?` field access), matching this file's existing style for
/// `listObjectsAsync`/`listVerbsAsync`'s results rather than introducing
/// heavier typed modeling for this one screen.
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
        let ownerRef: int64 = ownerVal?objRef
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

    let renderRefSection (title: string) (refs: obj[]) : unit =
        let section = document.createElement ("div")
        let titleEl = document.createElement ("div")
        titleEl.classList.add "inspector-section-title"
        titleEl.textContent <- sprintf "%s (%d)" title refs.Length
        section.appendChild titleEl |> ignore

        let list = document.createElement ("div")
        list.classList.add "inspector-refs"

        for r in refs do
            let refObjRef: int64 = r?objRef
            let link = document.createElement ("span")
            link.classList.add "inspector-link"
            link.textContent <- (r?name: string)
            link.onclick <- fun _ -> openOrSwitchToInspector refObjRef
            list.appendChild link |> ignore

        section.appendChild list |> ignore
        inspectorContentEl.appendChild section |> ignore

    renderRefSection "Parents" (unbox info?parents)
    renderRefSection "Children" (unbox info?children)

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
    // same close-× behavior) but skip the preview-tab mechanic entirely -
    // each is a permanent tab, and clicking one always re-loads it fresh
    // (`openOrSwitchToInspector`, not a bare `switchToTab`).
    for objRef in openInspectorTabs do
        let tab = document.createElement ("div")
        tab.classList.add "main-tab"
        if activeTab = InspectorTab objRef then tab.classList.add "active"

        let label = document.createElement ("span")
        label.classList.add "main-tab-label"
        label.textContent <- sprintf "ⓘ #%d" objRef
        label.onclick <- fun _ -> openOrSwitchToInspector objRef

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

/// Selects `objRef` in the object list, refreshes the verb list for it, and
/// (`autoSelectVerb` non-empty) opens a specific verb once the verb list
/// loads - used both by the object list's own click handler (no verb chosen
/// yet) and by `LspClient`'s go-to-definition jump (which knows exactly
/// which verb it wants open in the newly-selected object). Resets the verb
/// filter on every switch - an old query carried over from a different
/// object's verb set is more likely to confuse than help.
and private selectObject (objRef: int64) (autoSelectVerb: string option) : unit =
    selectedObjRef <- Some objRef
    currentVerbsForSelectedObj <- [||]
    verbsFilterText <- ""
    verbsFilterEl.value <- ""
    renderObjectsList ()
    renderVerbsList ()

    async {
        let! verbs = LspClient.listVerbsAsync objRef
        currentVerbsForSelectedObj <- verbs
        renderVerbsList ()

        match autoSelectVerb with
        | Some verb when verbs |> Array.contains verb -> openOrSwitchToVerb objRef verb
        | _ -> ()
    }
    |> Async.StartImmediate

and private renderObjectsList () : unit =
    let filtered =
        allObjects
        |> Array.map (fun (objRef, name) -> sprintf "#%d" objRef, name)
        |> Array.filter (fun (_, label) -> matchesFilter objectsFilterText label)

    let placeholder = if objectsFilterText <> "" then "no matches" else "no objects yet"

    renderList
        objectsListEl
        placeholder
        filtered
        (fun value -> selectedObjRef = Some(int64 (value.TrimStart '#')))
        (fun value -> selectObject (int64 (value.TrimStart '#')) None)
        (Some(fun value -> openOrSwitchToInspector (int64 (value.TrimStart '#'))))

and private renderVerbsList () : unit =
    match selectedObjRef with
    | None -> renderList verbsListEl "select an object" [||] (fun _ -> false) (fun _ -> ()) None
    | Some objRef ->
        let filtered =
            currentVerbsForSelectedObj
            |> Array.map (fun v -> v, v)
            |> Array.filter (fun (_, label) -> matchesFilter verbsFilterText label)

        let placeholder = if verbsFilterText <> "" then "no matches" else "no verbs"

        renderList
            verbsListEl
            placeholder
            filtered
            (fun v -> activeTab = VerbTab(objRef, v))
            (fun verbName -> openOrSwitchToVerb objRef verbName)
            None

tabGameBtn.onclick <- fun _ -> switchToTab GameTab
// `switchToTab` no-ops when its argument already equals `activeTab` (to
// avoid redundant work re-clicking the tab you're already on) - but
// `activeTab` *starts* as `GameTab`, so that guard also skipped the very
// first application of `showPaneFor`, leaving `#terminal-pane` without its
// `.active` class even though the Game tab looked selected. Call it
// directly here, once, to actually paint the initial state.
showPaneFor GameTab
renderTabs ()

objectsFilterEl.oninput <-
    fun _ ->
        objectsFilterText <- objectsFilterEl.value
        renderObjectsList ()

verbsFilterEl.oninput <-
    fun _ ->
        verbsFilterText <- verbsFilterEl.value
        renderVerbsList ()

// Both lists start out showing their empty-state placeholder - the objects
// list is populated for real once `moodev-login-result` confirms a login
// (see below); the verbs list stays on "select an object" until then.
renderObjectsList ()
renderVerbsList ()

ws.onopen <-
    fun _ ->
        appendOutput "[connected]\n"
        // v1 simplification: the sidebar/tabs are always shown once
        // connected, rather than proactively querying player.programmer
        // first. A non-programmer just sees E_PERM in the diagnostics area
        // on save - see $vcs:ide_fetch/ide_save, which both check
        // player.programmer server-side regardless of what the client
        // shows. The object *list* is stricter, though - see the
        // `moodev-login-result` handler below - it stays empty until a real
        // MOO login succeeds, since the metadata graph it's drawn from has
        // nothing to do with which (if any) account this session is using.
        sidebarEl.classList.add ("visible")
        mainTabsEl.classList.add ("visible")
        PaneResizer.init PaneResizer.LeftRight "moodev-sidebar-width-pct" layoutEl sidebarResizerEl sidebarEl
        PaneResizer.init PaneResizer.UpDown "moodev-objects-verbs-split-pct" sidebarEl sidebarSplitResizerEl objectsPaneEl
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
                        // Refresh the verb list's highlight to follow
                        // whatever just opened - cheap, reuses the
                        // already-cached verb list.
                        renderVerbsList ()
                    | false, _ -> ()
                | _ -> ()
            elif header.StartsWith("moodev-edit-result") then
                let ok = headerField "ok: " header = Some "1"

                editorDiagnosticsEl.textContent <-
                    if ok then "" else String.concat "\n" lines
            elif header.StartsWith("moodev-login-result") then
                if headerField "ok: " header = Some "1" then
                    Login.hide ()

                    async {
                        let! objects = LspClient.listObjectsAsync ()
                        allObjects <- objects
                        renderObjectsList ()
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
            // the cursor can move right away; going through `selectObject`
            // would just no-op anyway (`switchToTab` skips work when its
            // argument already equals `activeTab`).
            editor.setPosition (createObj [ "lineNumber" ==> line; "column" ==> col ])
            editor.revealPositionInCenter (createObj [ "lineNumber" ==> line; "column" ==> col ])
        else
            // A different verb (a VerbCall dispatch jump) - `line`/`col`
            // are always (1,1) here server-side (`locationOfVerb` has no
            // per-statement spans to offer), which is where a freshly-
            // loaded verb's cursor starts anyway, so nothing more to do
            // once it's open.
            selectObject objRef (Some verbName))
    (fun message -> editorDiagnosticsEl.textContent <- message)

inputEl.focus ()
