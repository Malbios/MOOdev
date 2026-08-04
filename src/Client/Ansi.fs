/// A small, hand-rolled ANSI/SGR (Select Graphic Rendition) parser for the
/// terminal scrollback pane - not a full VT100 emulator (no cursor
/// movement, no screen clearing, no alternate buffer), since this is an
/// append-only `<pre>` log, not an interactive terminal. The MOO server
/// already sends real ANSI escape codes by default (confirmed live via
/// `@ansi-test` - `ANSI_Utilities:get_code` on the server side only ever
/// emits standard `ESC[<n>m` / `ESC[38;5;<n>m` / `ESC[38;2;<r>;<g>;<b>m`
/// forms), so this only needs to understand standard SGR, not any
/// MOO-specific tag syntax - that conversion already happened server-side.
/// See `App.fs`'s `appendOutput`, the one call site.
module Client.Ansi

open Browser
open Browser.Types

type Style =
    { Bold: bool
      Dim: bool
      Italic: bool
      Underline: bool
      Blink: bool
      Inverse: bool
      Strikethrough: bool
      Fg: string option
      Bg: string option }

let private defaultStyle =
    { Bold = false
      Dim = false
      Italic = false
      Underline = false
      Blink = false
      Inverse = false
      Strikethrough = false
      Fg = None
      Bg = None }

/// Carried between `feed` calls: the currently-active style, and any
/// trailing bytes of an escape sequence `feed` hasn't seen the end of yet.
/// WebSocket frames can split a sequence across two calls (e.g. `ESC[3` in
/// one frame, `1m` in the next) - this is what makes that safe, rather than
/// each call assuming it starts and ends on a clean boundary.
type State = { Current: Style; Pending: string }

let initialState = { Current = defaultStyle; Pending = "" }

type Segment = { Text: string; Style: Style }

/// Standard xterm 16-color palette (0-7 normal, 8-15 bright) - the same
/// widely-recognized values most terminal emulators default to, so MOO's
/// "red"/"green"/etc. tags read as the colors players already expect.
let private basicPalette =
    [| "#000000"
       "#cd0000"
       "#00cd00"
       "#cdcd00"
       "#0000ee"
       "#cd00cd"
       "#00cdcd"
       "#e5e5e5"
       "#7f7f7f"
       "#ff0000"
       "#00ff00"
       "#ffff00"
       "#5c5cff"
       "#ff00ff"
       "#00ffff"
       "#ffffff" |]

/// xterm's standard 256-color palette, computed rather than a giant literal
/// table: 0-15 the basic palette above, 16-231 a 6x6x6 color cube, 232-255
/// a 24-step grayscale ramp.
let private xterm256 (n: int) : string =
    if n < 16 then
        basicPalette.[n]
    elif n < 232 then
        let n = n - 16
        let r = n / 36
        let g = (n / 6) % 6
        let b = n % 6
        let level (x: int) = if x = 0 then 0 else 55 + x * 40
        sprintf "rgb(%d,%d,%d)" (level r) (level g) (level b)
    else
        let level = 8 + (n - 232) * 10
        sprintf "rgb(%d,%d,%d)" level level level

/// Applies one plain (single-number) SGR code to `style`. The two
/// multi-parameter extended-color forms (38/48;5;N and 38/48;2;R;G;B) are
/// handled separately by `applySgr`, which consumes the extra parameters
/// those need before this ever sees a bare code.
let private applyCode (style: Style) (code: int) : Style =
    match code with
    | 0 -> defaultStyle
    | 1 -> { style with Bold = true }
    | 2 -> { style with Dim = true }
    | 3 -> { style with Italic = true }
    | 4 -> { style with Underline = true }
    | 5 -> { style with Blink = true }
    | 7 -> { style with Inverse = true }
    | 9 -> { style with Strikethrough = true }
    | 22 -> { style with Bold = false; Dim = false }
    | 23 -> { style with Italic = false }
    | 24 -> { style with Underline = false }
    | 25 -> { style with Blink = false }
    | 27 -> { style with Inverse = false }
    | 29 -> { style with Strikethrough = false }
    | 39 -> { style with Fg = None }
    | 49 -> { style with Bg = None }
    | n when n >= 30 && n <= 37 -> { style with Fg = Some basicPalette.[n - 30] }
    | n when n >= 90 && n <= 97 -> { style with Fg = Some basicPalette.[n - 90 + 8] }
    | n when n >= 40 && n <= 47 -> { style with Bg = Some basicPalette.[n - 40] }
    | n when n >= 100 && n <= 107 -> { style with Bg = Some basicPalette.[n - 100 + 8] }
    | _ -> style

/// Applies a full SGR parameter list (already split on `;`, an empty
/// parameter - e.g. the trailing one in "1;" - meaning 0 per the SGR spec).
let private applySgr (style: Style) (parameters: int list) : Style =
    let rec go (style: Style) (ps: int list) : Style =
        match ps with
        | [] -> style
        | 38 :: 5 :: n :: rest -> go { style with Fg = Some(xterm256 n) } rest
        | 48 :: 5 :: n :: rest -> go { style with Bg = Some(xterm256 n) } rest
        | 38 :: 2 :: r :: g :: b :: rest -> go { style with Fg = Some(sprintf "rgb(%d,%d,%d)" r g b) } rest
        | 48 :: 2 :: r :: g :: b :: rest -> go { style with Bg = Some(sprintf "rgb(%d,%d,%d)" r g b) } rest
        | code :: rest -> go (applyCode style code) rest

    go style parameters

/// Final bytes (0x40-0x7E) that terminate a CSI sequence (`ESC [ ... final`),
/// per the ECMA-48 CSI grammar. Only `m` (SGR) actually changes rendering -
/// every other final byte (cursor movement, clear-line, etc.) is still
/// consumed so it doesn't leak into the visible text, just discarded; this
/// is a scrollback log, not an interactive terminal, so there's nothing
/// meaningful to do with them.
let private isFinalByte (c: char) : bool = c >= '@' && c <= '~'

let private tryParseInt (s: string) : int =
    match System.Int32.TryParse s with
    | true, n -> n
    | false, _ -> 0

/// This module's original target MOO content always emits a real ESC byte
/// (0x1B) for color codes - confirmed via `@ansi-test`. A different,
/// real-world MOO codebase (HellMOO) was confirmed live to instead emit the
/// LITERAL TEXT "~1b"/"~1B" (tilde followed by the byte's own 2-hex-digit
/// value, not a real control byte at all - three ordinary printable
/// characters) as its escape introducer, likely a "safe/textual fallback"
/// some MUD ANSI implementations use for clients not detected as
/// ANSI-capable. Confirmed by reading raw bytes off a plain TCP connection
/// straight to the MOO, bypassing this whole pipeline entirely - the server
/// itself sends these three characters, this isn't corruption introduced
/// anywhere in MOOdy's own path. Treated as fully interchangeable with a
/// real ESC byte below (deliberately not generalized to arbitrary "~XX"
/// values - only this one spelling, representing ESC specifically, has
/// actually been observed).
///
/// Returns the introducer's length at `text.[i]` (`1` for a real ESC byte,
/// `3` for "~1b"/"~1B"), or `None` if this position is definitely neither.
let private escIntroducerLenAt (text: string) (i: int) : int option =
    if text.[i] = '\x1b' then
        Some 1
    elif text.[i] = '~' && i + 2 < text.Length && text.[i + 1] = '1' && (text.[i + 2] = 'b' || text.[i + 2] = 'B') then
        Some 3
    else
        None

/// True only when `text.[i..]` is a strict, incomplete prefix of "~1b"/
/// "~1B" (not a real introducer yet, but could still become one once more
/// text arrives) - lets `feed` defer a trailing partial match to the next
/// call instead of misreading it as plain text, the same protection a lone
/// trailing real ESC byte already gets.
let private isPartialTextualEscAt (text: string) (i: int) : bool =
    let remaining = text.Length - i
    text.[i] = '~' && remaining < 3 && (remaining = 1 || text.[i + 1] = '1')

/// Scans `text` (with `state.Pending` prepended) for CSI/SGR sequences
/// (introduced by either escape form above) and the bare BEL byte
/// (`@ansi-test`'s "Beep" line uses this raw, not a CSI sequence),
/// returning the resulting styled segments plus the state to carry into the
/// next call. Never assumes `text` starts or ends on a clean
/// escape-sequence boundary.
let feed (state: State) (text: string) : Segment list * State =
    let text = state.Pending + text
    let len = text.Length
    let segments = ResizeArray<Segment>()
    let plain = System.Text.StringBuilder()
    let mutable style = state.Current
    let mutable i = 0
    let mutable tailStart = -1

    let flushPlain () =
        if plain.Length > 0 then
            segments.Add { Text = plain.ToString(); Style = style }
            plain.Clear () |> ignore

    while i < len && tailStart < 0 do
        match escIntroducerLenAt text i with
        | Some introLen when i + introLen >= len ->
            // Lone trailing introducer - could be the start of a CSI
            // sequence in the next chunk.
            tailStart <- i
        | Some introLen when text.[i + introLen] = '[' ->
            let mutable j = i + introLen + 1

            while j < len && not (isFinalByte text.[j]) do
                j <- j + 1

            if j >= len then
                tailStart <- i // no final byte yet in what we have
            else
                let final = text.[j]

                if final = 'm' then
                    flushPlain ()
                    let paramsText = text.Substring(i + introLen + 1, j - (i + introLen + 1))

                    let parameters =
                        if paramsText = "" then
                            [ 0 ]
                        else
                            paramsText.Split(';') |> Array.map tryParseInt |> Array.toList

                    style <- applySgr style parameters

                i <- j + 1
        | Some introLen ->
            // Introducer not followed by '[' - not a CSI sequence MOO's own
            // ANSI systems ever emit (confirmed via `get_code`, and via the
            // "~1b"-form world's own live output); drop just the introducer
            // rather than risk mis-parsing something unexpected.
            i <- i + introLen
        | None when isPartialTextualEscAt text i -> tailStart <- i
        | None when text.[i] = '\x07' -> i <- i + 1
        | None ->
            plain.Append(text.[i]) |> ignore
            i <- i + 1

    flushPlain ()

    let pending = if tailStart >= 0 then text.Substring(tailStart) else ""
    segments |> List.ofSeq, { Current = style; Pending = pending }

/// Like `feed`, but for a complete, already-loaded string (an Inspector
/// property's raw `toliteral()` text) where the point is to inspect the raw
/// codes, not just watch their effect - so each SGR sequence becomes its own
/// segment showing "\e[<params>m" literally, styled with the look it just
/// set, immediately followed by the text it affects in that same style. No
/// `State` needed - unlike `feed`, this is never called across multiple
/// chunks, so there's nothing to carry between calls. Only SGR (`m`) and the
/// bare BEL byte get real handling, since those are the only two forms
/// `ANSI_Utilities:get_code` (`6_get_code.moo`) ever emits - a lone ESC or
/// any other CSI final still becomes a visible, unstyled `\e`/`\a` label
/// rather than silently vanishing, but isn't given special styling since it
/// never actually occurs in this codebase's content.
let renderLiteralPreview (text: string) : Segment list =
    let len = text.Length
    let segments = ResizeArray<Segment>()
    let plain = System.Text.StringBuilder()
    let mutable style = defaultStyle
    let mutable i = 0

    let flushPlain () =
        if plain.Length > 0 then
            segments.Add { Text = plain.ToString(); Style = style }
            plain.Clear () |> ignore

    while i < len do
        let c = text.[i]

        if c = '\x1b' && i + 1 < len && text.[i + 1] = '[' then
            let mutable j = i + 2

            while j < len && not (isFinalByte text.[j]) do
                j <- j + 1

            if j >= len then
                // Unterminated - show the rest verbatim rather than dropping it.
                plain.Append(text.Substring(i)) |> ignore
                i <- len
            else
                let final = text.[j]
                let paramsText = text.Substring(i + 2, j - (i + 2))

                if final = 'm' then
                    flushPlain ()

                    let parameters =
                        if paramsText = "" then
                            [ 0 ]
                        else
                            paramsText.Split(';') |> Array.map tryParseInt |> Array.toList

                    style <- applySgr style parameters
                    segments.Add { Text = sprintf "\\e[%sm" paramsText; Style = style }

                i <- j + 1
        elif c = '\x1b' then
            flushPlain ()
            segments.Add { Text = "\\e"; Style = style }
            i <- i + 1
        elif c = '\x07' then
            flushPlain ()
            segments.Add { Text = "\\a"; Style = style }
            i <- i + 1
        else
            plain.Append(c) |> ignore
            i <- i + 1

    flushPlain ()
    segments |> List.ofSeq

let private styleAttr (s: Style) : string =
    let parts = ResizeArray<string>()
    // Inverse swaps fg/bg rather than being a separate rendering mode -
    // matches how real terminals (and `@ansi-test`'s "Inverse" line) treat
    // it, falling back to the pane's own default colors when the other
    // side was never set.
    let fg, bg =
        if s.Inverse then
            (s.Bg |> Option.defaultValue "#000000"), (s.Fg |> Option.defaultValue "#e5e5e5")
        else
            (s.Fg |> Option.defaultValue ""), (s.Bg |> Option.defaultValue "")

    if fg <> "" then
        parts.Add(sprintf "color:%s" fg)

    if bg <> "" then
        parts.Add(sprintf "background-color:%s" bg)

    if s.Bold then
        parts.Add "font-weight:bold"

    if s.Dim then
        parts.Add "opacity:0.7"

    if s.Italic then
        parts.Add "font-style:italic"

    if s.Underline && s.Strikethrough then
        parts.Add "text-decoration:underline line-through"
    elif s.Underline then
        parts.Add "text-decoration:underline"
    elif s.Strikethrough then
        parts.Add "text-decoration:line-through"

    if s.Blink then
        parts.Add "animation:ansi-blink 1s step-start infinite"

    String.concat ";" parts

/// Appends `segments` to `outputEl` - a plain `Text` node when no style is
/// active (the common case, matches today's plain-text behavior exactly),
/// or a `<span>` with an inline `style` attribute when one is. Always via
/// `createTextNode`, never `innerHTML` - MOO output is untrusted, live,
/// multi-user content, so it must never be interpreted as markup.
let renderInto (outputEl: HTMLElement) (segments: Segment list) : unit =
    for seg in segments do
        if seg.Style = defaultStyle then
            outputEl.appendChild (document.createTextNode seg.Text) |> ignore
        else
            let span = document.createElement "span"
            span.appendChild (document.createTextNode seg.Text) |> ignore
            span.setAttribute ("style", styleAttr seg.Style)
            outputEl.appendChild span |> ignore
