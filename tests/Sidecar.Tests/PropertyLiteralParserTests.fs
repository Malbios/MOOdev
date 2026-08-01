module Sidecar.Tests.PropertyLiteralParserTests

open Xunit
open Sidecar.IdeActions

[<Fact>]
let ``parses a simple list literal into brief text elements`` () =
    Assert.Equal(ListLiteral [ "1"; "2"; "3" ], parsePropertyLiteral "{1, 2, 3}")

[<Fact>]
let ``renders strings, objects, errors, and negative numbers in a list`` () =
    Assert.Equal(
        ListLiteral [ "\"hello\""; "#3"; "E_PERM"; "-1" ],
        parsePropertyLiteral """{"hello", #3, E_PERM, -1}"""
    )

[<Fact>]
let ``renders a whole-number float with a decimal point so it re-parses as a float`` () =
    Assert.Equal(ListLiteral [ "1.0"; "2.5" ], parsePropertyLiteral "{1.0, 2.5}")

[<Fact>]
let ``escapes quotes and backslashes when rendering a string back to literal text`` () =
    Assert.Equal(ListLiteral [ "\"a\\\"b\\\\c\"" ], parsePropertyLiteral """{"a\"b\\c"}""")

[<Fact>]
let ``renders nested list and map literals recursively`` () =
    Assert.Equal(ListLiteral [ "{2, 3}"; "[\"x\" -> 1]" ], parsePropertyLiteral """{{2, 3}, ["x" -> 1]}""")

[<Fact>]
let ``a splice element makes the whole value not structurally editable`` () =
    Assert.Equal(NotAListOrMap, parsePropertyLiteral "{1, @x, 3}")

[<Fact>]
let ``a non-literal element makes the whole value not structurally editable`` () =
    Assert.Equal(NotAListOrMap, parsePropertyLiteral "{1, x + 1, 3}")

[<Fact>]
let ``parses a simple map literal into brief key/value pairs`` () =
    Assert.Equal(MapLiteral [ "\"a\"", "1"; "\"b\"", "2" ], parsePropertyLiteral """["a" -> 1, "b" -> 2]""")

[<Fact>]
let ``a non-literal map value makes the whole value not structurally editable`` () =
    Assert.Equal(NotAListOrMap, parsePropertyLiteral """["a" -> x + 1]""")

[<Fact>]
let ``a scalar value is not a list or map`` () =
    Assert.Equal(NotAListOrMap, parsePropertyLiteral "42")

[<Fact>]
let ``garbage text is not a list or map`` () =
    Assert.Equal(NotAListOrMap, parsePropertyLiteral "{1, 2,")

[<Fact>]
let ``an empty list literal parses to zero elements`` () =
    Assert.Equal(ListLiteral [], parsePropertyLiteral "{}")
