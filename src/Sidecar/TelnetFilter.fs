/// Strips telnet protocol negotiation (IAC sequences) out of a raw byte stream
/// coming from the MOO server, so the browser terminal only ever sees the
/// actual game text. We never answer negotiation - the live server has been
/// confirmed to work fine with a client that doesn't (a raw `nc` connection
/// behaves identically), so this is deliberately strip-only, not a full
/// telnet option negotiator.
module Sidecar.TelnetFilter

// Telnet protocol constants (RFC 854 / RFC 855).
[<Literal>]
let IAC = 255uy

[<Literal>]
let SB = 250uy

[<Literal>]
let SE = 240uy

let private isWillWontDoDont (b: byte) = b >= 251uy && b <= 254uy // WILL WONT DO DONT
let private isTwoByteCommand (b: byte) = b >= 241uy && b <= 249uy // NOP DM BRK IP AO AYT EC EL GA

/// Parser state carried across chunk boundaries. IAC SB ... IAC SE
/// subnegotiations (e.g. MSSP) can legitimately span multiple TCP reads,
/// so this can't be a per-chunk regex - it has to be a real state machine.
type State =
    | Data
    | SawIac
    | SawIacCommand // saw IAC WILL/WONT/DO/DONT, next byte is the option, always discarded
    | InSubneg // inside IAC SB ... , discarding until IAC SE
    | SubnegSawIac // inside a subnegotiation, just saw IAC, check whether it's SE

/// Feeds one byte through the filter. Returns the (at most one) output byte
/// and the next state. IAC IAC (the telnet escape for a literal 0xFF data
/// byte) is the one case that emits a byte while still being IAC-related.
let step (state: State) (b: byte) : State * byte option =
    match state with
    | Data ->
        if b = IAC then SawIac, None
        else Data, Some b
    | SawIac ->
        if b = IAC then Data, Some b // IAC IAC -> literal 0xFF
        elif b = SB then InSubneg, None
        elif isWillWontDoDont b then SawIacCommand, None
        elif isTwoByteCommand b then Data, None // 2-byte command, already complete
        else Data, None // unrecognized byte after IAC; drop it defensively
    | SawIacCommand ->
        // this byte is the option for WILL/WONT/DO/DONT - always discarded
        Data, None
    | InSubneg ->
        if b = IAC then SubnegSawIac, None
        else InSubneg, None
    | SubnegSawIac ->
        if b = SE then Data, None // end of subnegotiation
        elif b = IAC then InSubneg, None // escaped IAC inside subneg data, stay in subneg
        else InSubneg, None // any other byte: back into subneg body

/// Runs `step` over a whole chunk, returning the filtered bytes plus the
/// state to carry into the next chunk.
let filterChunk (state: State) (bytes: byte[]) : byte[] * State =
    let output = System.Collections.Generic.List<byte>(bytes.Length)
    let mutable s = state

    for b in bytes do
        let next, out = step s b
        s <- next

        match out with
        | Some byteOut -> output.Add(byteOut)
        | None -> ()

    output.ToArray(), s
