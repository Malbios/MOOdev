/// Recognizes our own editor protocol riding on top of the plain MOO text
/// stream: lines shaped like real MCP framing (`#$#msgname ... `, multiline
/// continuation via `#$#*`/`#$#:`) but for message names we define ourselves
/// (`moodev-edit-content`, `moodev-edit-result`, `moodev-login-result`, and
/// any future `moodev-*` message) rather than anything registered through
/// ToastCore's own MCP negotiation/registry - $vcs emits these directly via
/// notify(), so no negotiation handshake is needed on either side. Every
/// other line - including the server's own unrelated `#$#mcp version: ...`
/// greeting - passes through untouched.
///
/// Line-buffered only for lines that start with the literal bytes `#$#`;
/// everything else still streams through byte-for-byte with no added
/// latency, same as before this filter existed.
module Sidecar.McpFilter

open System
open System.Text

/// One complete moodev-edit-* message, ready to hand to the browser as a
/// structured (JSON, text-frame) message instead of raw terminal bytes.
type McpMessage =
    { Header: string // full text after "#$#", e.g. "moodev-edit-content 1 ref: 123-456 object: #127 verb: sanitize_name"
      Lines: string list } // unwrapped content of each "#$#* <tag> text: ..." line, in order

// Not `private`: these appear as fields of the public State type below, and
// F# requires a public type's field types to be at least as accessible as
// the type itself. Still opaque in practice - nothing outside this module
// has a reason to construct or pattern-match them directly.
type ActiveMessage =
    { Header: string
      Tag: string
      Lines: ResizeArray<string> }

/// LineStart: at the start of a line, having matched 0..2 bytes of the "#$#"
/// prefix so far (heldPrefix). BufferingHashLine: confirmed "#$#" prefix,
/// buffering the rest of this one line until '\n'. PassThroughLine: a line
/// that's confirmed NOT to start with "#$#" - stream the remaining bytes of
/// it straight through with zero buffering, same as the old behavior.
type LineState =
    | LineStart of heldPrefix: byte[]
    | BufferingHashLine of buffer: ResizeArray<byte>
    | PassThroughLine

type State =
    { Line: LineState
      Active: ActiveMessage option }

let initial: State = { Line = LineStart [||]; Active = None }

let private hashDollarHash = [| byte '#'; byte '$'; byte '#' |]

/// What to do with one classified line: forward its raw bytes to the
/// terminal untouched, or - once a full moodev-edit-* message has been
/// assembled across possibly several lines - emit it as a structured event.
type Output =
    | PassThrough of byte[]
    | Emit of McpMessage

let private decode (bytes: byte[]) = Encoding.UTF8.GetString(bytes)

/// Splits "key: value" style text on the first occurrence of `marker`,
/// returning the trimmed text after it. Used for the small, fixed set of
/// fields our own MOOcode emits - not a general MCP argument parser.
let private after (marker: string) (s: string) =
    let idx = s.IndexOf(marker: string)
    if idx < 0 then None else Some(s.Substring(idx + marker.Length).Trim())

let private firstToken (s: string) =
    let s = s.Trim()
    let spaceIdx = s.IndexOf(' ')
    if spaceIdx < 0 then s else s.Substring(0, spaceIdx)

/// Classifies one complete "#$#..." line (bytes, no trailing \r\n) against
/// whatever message is currently being accumulated, producing zero or one
/// outputs plus the next Active-message state.
let private classifyHashLine (active: ActiveMessage option) (lineBytes: byte[]) : Output list * ActiveMessage option =
    let text = decode lineBytes
    let rest = text.Substring(3) // drop leading "#$#"

    if rest.StartsWith("* ") then
        // Continuation line: "#$#* <tag> text: <content>"
        let body = rest.Substring(2)
        let tag = firstToken body
        match active with
        | Some a when a.Tag = tag ->
            match after "text: " body with
            | Some content -> a.Lines.Add(content)
            | None -> ()
            [], Some a
        | _ ->
            // Continuation for a message we're not tracking (e.g. arrived
            // out of order, or a tag we never opened) - pass the raw line
            // through rather than silently eating it.
            [ PassThrough(Array.append lineBytes [| byte '\n' |]) ], active
    elif rest.StartsWith(": ") || rest = ":" then
        // End-of-message line: "#$#: <tag>"
        let tag = (if rest.StartsWith(": ") then rest.Substring(2) else "").Trim()
        match active with
        | Some a when a.Tag = tag -> [ Emit { Header = a.Header; Lines = List.ofSeq a.Lines } ], None
        | _ -> [ PassThrough(Array.append lineBytes [| byte '\n' |]) ], active
    else
        // A fresh "#$#<msgname> ..." header line.
        match after "ref: " rest with
        | Some afterRef when rest.StartsWith("moodev-") ->
            let tag = firstToken afterRef
            [], Some { Header = rest; Tag = tag; Lines = ResizeArray() }
        | _ ->
            // Some other "#$#" line we don't own (e.g. the server's own MCP
            // greeting/negotiation) - leave it for the terminal, unchanged.
            [ PassThrough(Array.append lineBytes [| byte '\n' |]) ], active

/// Feeds one chunk of already telnet-filtered bytes through the line
/// scanner. Returns outputs in order (pass-through chunks interleaved with
/// any completed moodev-edit-* messages) plus the state to carry forward.
let filterChunk (state: State) (bytes: byte[]) : Output list * State =
    let outputs = ResizeArray<Output>()
    let mutable line = state.Line
    let mutable active = state.Active

    for b in bytes do
        match line with
        | PassThroughLine ->
            outputs.Add(PassThrough [| b |])
            if b = byte '\n' then line <- LineStart [||]
        | LineStart held ->
            if b = byte '\n' then
                // blank line
                outputs.Add(PassThrough(Array.append held [| b |]))
                line <- LineStart [||]
            else
                let next = Array.append held [| b |]
                if next.Length <= hashDollarHash.Length && next = hashDollarHash.[.. next.Length - 1] then
                    if next.Length = hashDollarHash.Length then
                        line <- BufferingHashLine(ResizeArray(next))
                    else
                        line <- LineStart next
                else
                    outputs.Add(PassThrough next)
                    line <- PassThroughLine
        | BufferingHashLine buf ->
            if b = byte '\n' then
                let raw = buf.ToArray()
                // MOO's telnet output uses \r\n; strip a trailing \r before
                // classifying so it doesn't end up embedded in extracted
                // content (e.g. every line of a fetched verb).
                let lineBytes =
                    if raw.Length > 0 && raw.[raw.Length - 1] = byte '\r' then
                        raw.[.. raw.Length - 2]
                    else
                        raw
                let evs, nextActive = classifyHashLine active lineBytes
                outputs.AddRange(evs)
                active <- nextActive
                line <- LineStart [||]
            else
                buf.Add(b)

    List.ofSeq outputs, { Line = line; Active = active }
