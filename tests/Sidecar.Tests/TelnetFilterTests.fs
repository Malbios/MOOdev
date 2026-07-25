module Sidecar.Tests.TelnetFilterTests

open Xunit
open Sidecar.TelnetFilter

let private bytes (s: string) = System.Text.Encoding.ASCII.GetBytes(s)
let private text (b: byte[]) = System.Text.Encoding.ASCII.GetString(b)

[<Fact>]
let ``plain data passes through unchanged`` () =
    let input = bytes "hello world"
    let output, endState = filterChunk State.Data input

    Assert.Equal("hello world", text output)
    Assert.Equal(State.Data, endState)

[<Fact>]
let ``IAC WILL option is stripped, surrounding data preserved`` () =
    // "abc" + IAC WILL <opt> + "def"
    let input =
        Array.concat
            [ bytes "abc"
              [| IAC; 251uy; 1uy |] // WILL, option byte (e.g. ECHO)
              bytes "def" ]

    let output, endState = filterChunk State.Data input

    Assert.Equal("abcdef", text output)
    Assert.Equal(State.Data, endState)

[<Fact>]
let ``IAC WONT DO DONT are all stripped`` () =
    for cmd in [ 251uy; 252uy; 253uy; 254uy ] do // WILL WONT DO DONT
        let input = Array.concat [ bytes "x"; [| IAC; cmd; 42uy |]; bytes "y" ]
        let output, endState = filterChunk State.Data input

        Assert.Equal("xy", text output)
        Assert.Equal(State.Data, endState)

[<Fact>]
let ``two-byte command (no option byte) is stripped`` () =
    // IAC GA (Go Ahead, 249) is a bare 2-byte command, no option byte follows
    let input = Array.concat [ bytes "x"; [| IAC; 249uy |]; bytes "y" ]
    let output, endState = filterChunk State.Data input

    Assert.Equal("xy", text output)
    Assert.Equal(State.Data, endState)

[<Fact>]
let ``subnegotiation is fully stripped in one chunk`` () =
    // "before" + IAC SB <mssp option + data> IAC SE + "after"
    let input =
        Array.concat
            [ bytes "before"
              [| IAC; SB; 39uy; 1uy; 2uy; 3uy; IAC; SE |]
              bytes "after" ]

    let output, endState = filterChunk State.Data input

    Assert.Equal("beforeafter", text output)
    Assert.Equal(State.Data, endState)

[<Fact>]
let ``subnegotiation split across two chunks carries state correctly`` () =
    // This is the one real bug risk: MSSP-style subnegotiations can be split
    // across TCP reads, so the state machine must resume mid-subnegotiation.
    let chunk1 = Array.concat [ bytes "before"; [| IAC; SB; 39uy; 1uy; 2uy |] ]
    let chunk2 = Array.concat [ [| 3uy; IAC; SE |]; bytes "after" ]

    let output1, state1 = filterChunk State.Data chunk1
    Assert.Equal("before", text output1)
    Assert.Equal(State.InSubneg, state1)

    let output2, state2 = filterChunk state1 chunk2
    Assert.Equal("after", text output2)
    Assert.Equal(State.Data, state2)

[<Fact>]
let ``IAC IAC decodes to a single literal 0xFF data byte`` () =
    let input = Array.concat [ bytes "a"; [| IAC; IAC |]; bytes "b" ]
    let output, endState = filterChunk State.Data input

    Assert.Equal<byte[]>([| byte 'a'; 0xFFuy; byte 'b' |], output)
    Assert.Equal(State.Data, endState)

[<Fact>]
let ``IAC split across chunk boundary is still recognized`` () =
    let chunk1 = Array.concat [ bytes "x"; [| IAC |] ]
    let chunk2 = Array.concat [ [| 251uy; 1uy |]; bytes "y" ]

    let output1, state1 = filterChunk State.Data chunk1
    Assert.Equal("x", text output1)
    Assert.Equal(State.SawIac, state1)

    let output2, state2 = filterChunk state1 chunk2
    Assert.Equal("y", text output2)
    Assert.Equal(State.Data, state2)
