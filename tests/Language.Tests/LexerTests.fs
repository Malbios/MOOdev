module Language.Tests.LexerTests

open Xunit
open Language.Lexer

let private kinds (source: string) =
    let result = tokenize source
    Assert.True(result.Error.IsNone, sprintf "unexpected lex error: %A" result.Error)
    result.Tokens |> Array.map (fun t -> t.Kind) |> Array.filter ((<>) TEOF)

[<Fact>]
let ``keywords lex distinctly from identifiers`` () =
    Assert.Equal<TokenKind[]>([| TKeyword If |], kinds "if")
    Assert.Equal<TokenKind[]>([| TIdent "IF" |], kinds "IF") // case-sensitive per keywords.gperf
    Assert.Equal<TokenKind[]>([| TKeyword Any |], kinds "ANY")
    Assert.Equal<TokenKind[]>([| TIdent "any" |], kinds "any") // lowercase is not the keyword

[<Fact>]
let ``pass and true and false are ordinary identifiers, not keywords`` () =
    Assert.Equal<TokenKind[]>([| TIdent "pass" |], kinds "pass")
    Assert.Equal<TokenKind[]>([| TIdent "true" |], kinds "true")
    Assert.Equal<TokenKind[]>([| TIdent "false" |], kinds "false")

[<Fact>]
let ``the 19 error names lex as TErr, near-misses as identifiers`` () =
    Assert.Equal<TokenKind[]>([| TErr "E_PROPNF" |], kinds "E_PROPNF")
    Assert.Equal<TokenKind[]>([| TErr "E_INTRPT" |], kinds "E_INTRPT")
    Assert.Equal<TokenKind[]>([| TIdent "E_FOOBAR" |], kinds "E_FOOBAR")
    Assert.Equal<TokenKind[]>([| TIdent "E_" |], kinds "E_")

[<Fact>]
let ``block comments are stripped, non-nesting, ** slash does not close`` () =
    Assert.Equal<TokenKind[]>([| TInt 1L |], kinds "/* hi */1")
    Assert.Equal<TokenKind[]>([| TInt 1L; TInt 2L |], kinds "1/* mid */2")
    // The "**/ " quirk from moocode-reference.md: consuming the char after
    // any '*' without re-testing it means "**/" does NOT close.
    let unterminated = tokenize "/* note **/"
    Assert.True(unterminated.Error.IsSome)
    // But a real closer right after works fine.
    Assert.Equal<TokenKind[]>([| TInt 1L |], kinds "/**/1")
    Assert.Equal<TokenKind[]>([| TInt 1L |], kinds "/* note ***/1")

[<Fact>]
let ``unterminated comment is a lex error`` () =
    let result = tokenize "/* never closes"
    Assert.True(result.Error.IsSome)

[<Fact>]
let ``bare slash not followed by star is the division token`` () =
    Assert.Equal<TokenKind[]>([| TInt 1L; TSlash; TInt 2L |], kinds "1/2")

[<Fact>]
let ``int, trailing-dot float, leading-dot float, and full float all lex correctly`` () =
    Assert.Equal<TokenKind[]>([| TInt 42L |], kinds "42")
    Assert.Equal<TokenKind[]>([| TFloat 3.0 |], kinds "3.")
    Assert.Equal<TokenKind[]>([| TFloat 0.5 |], kinds ".5")
    Assert.Equal<TokenKind[]>([| TFloat 3.14 |], kinds "3.14")

[<Fact>]
let ``dotdot range is distinguished from a trailing-dot float`` () =
    Assert.Equal<TokenKind[]>([| TInt 3L; TDotDot; TInt 7L |], kinds "3..7")
    Assert.Equal<TokenKind[]>([| TFloat 3.0 |], kinds "3.")

[<Fact>]
let ``exponent floats parse including after a leading or trailing dot`` () =
    Assert.Equal<TokenKind[]>([| TFloat 1.0e10 |], kinds "1.0e10")
    Assert.Equal<TokenKind[]>([| TFloat 300000.0 |], kinds "3.e5")
    Assert.Equal<TokenKind[]>([| TFloat 50000.0 |], kinds ".5e5")

[<Fact>]
let ``lone dot not part of a number is the Dot token`` () =
    Assert.Equal<TokenKind[]>([| TIdent "obj"; TDot; TIdent "prop" |], kinds "obj.prop")

[<Fact>]
let ``object literals, including negative`` () =
    Assert.Equal<TokenKind[]>([| TObj 123L |], kinds "#123")
    Assert.Equal<TokenKind[]>([| TObj -1L |], kinds "#-1")

[<Fact>]
let ``malformed object number is a lex error`` () =
    let result = tokenize "#x"
    Assert.True(result.Error.IsSome)

[<Fact>]
let ``strings: backslash makes the next byte literal, no escape interpretation`` () =
    Assert.Equal<TokenKind[]>([| TStr "hello" |], kinds "\"hello\"")
    Assert.Equal<TokenKind[]>([| TStr "n" |], kinds "\"\\n\"") // \n is literally "n", not a newline
    Assert.Equal<TokenKind[]>([| TStr "\"" |], kinds "\"\\\"\"")
    Assert.Equal<TokenKind[]>([| TStr "\\" |], kinds "\"\\\\\"")

[<Fact>]
let ``unterminated or newline-spanning string is a lex error`` () =
    Assert.True((tokenize "\"never closes").Error.IsSome)
    Assert.True((tokenize "\"line1\nline2\"").Error.IsSome)

[<Fact>]
let ``bitwise dotted operators are distinct from their non-dotted lookalikes`` () =
    Assert.Equal<TokenKind[]>([| TBitAnd |], kinds "&.")
    Assert.Equal<TokenKind[]>([| TAnd |], kinds "&&")
    Assert.Equal<TokenKind[]>([| TBitOr |], kinds "|.")
    Assert.Equal<TokenKind[]>([| TOr |], kinds "||")
    Assert.Equal<TokenKind[]>([| TPipe |], kinds "|") // ternary else-branch / bare bitor-lookalike
    Assert.Equal<TokenKind[]>([| TBitXor |], kinds "^.")
    Assert.Equal<TokenKind[]>([| TCaret |], kinds "^") // exponent / bare-in-index

[<Fact>]
let ``shifts vs comparisons`` () =
    Assert.Equal<TokenKind[]>([| TShl |], kinds "<<")
    Assert.Equal<TokenKind[]>([| TLtEq |], kinds "<=")
    Assert.Equal<TokenKind[]>([| TLt |], kinds "<")
    Assert.Equal<TokenKind[]>([| TShr |], kinds ">>")
    Assert.Equal<TokenKind[]>([| TGtEq |], kinds ">=")
    Assert.Equal<TokenKind[]>([| TGt |], kinds ">")

[<Fact>]
let ``eq, arrow, and assign all start with equals`` () =
    Assert.Equal<TokenKind[]>([| TEq |], kinds "==")
    Assert.Equal<TokenKind[]>([| TArrow |], kinds "=>")
    Assert.Equal<TokenKind[]>([| TAssign |], kinds "=")

[<Fact>]
let ``map arrow vs minus`` () =
    Assert.Equal<TokenKind[]>([| TMapArrow |], kinds "->")
    Assert.Equal<TokenKind[]>([| TMinus |], kinds "-")

[<Fact>]
let ``not vs not-equal`` () =
    Assert.Equal<TokenKind[]>([| TNotEq |], kinds "!=")
    Assert.Equal<TokenKind[]>([| TNot |], kinds "!")

[<Fact>]
let ``caret before a genuine range is not swallowed into bitxor - list[^..5]`` () =
    // The check_two_dots() disambiguation from parser.y: `^` followed by
    // `..` must NOT lex as `^.` (bitxor) + `.` - it must stay a bare `^`
    // (first-index) followed by the `..` range token.
    Assert.Equal<TokenKind[]>(
        [| TIdent "list"; TLBracket; TCaret; TDotDot; TInt 5L; TRBracket |],
        kinds "list[^..5]"
    )

[<Fact>]
let ``backtick and single quote are their own tokens for inline catch`` () =
    Assert.Equal<TokenKind[]>(
        [| TBacktick
           TIdent "x"
           TNot
           TKeyword Any
           TArrow
           TInt 0L
           TSingleQuote |],
        kinds "`x ! ANY => 0'"
    )

[<Fact>]
let ``waif property sugar tokens: dot then colon then identifier, no special token needed`` () =
    // obj.:prop - the lexer just emits Dot, Colon, Ident; the parser (not
    // the lexer) is what turns this into sugar for obj.(":prop")
    // (parser.y:405-416).
    Assert.Equal<TokenKind[]>([| TIdent "obj"; TDot; TColon; TIdent "prop" |], kinds "obj.:prop")

[<Fact>]
let ``tilde and single-char punctuation`` () =
    Assert.Equal<TokenKind[]>([| TTilde |], kinds "~")

    Assert.Equal<TokenKind[]>(
        [| TLBrace
           TRBrace
           TLParen
           TRParen
           TLBracket
           TRBracket
           TComma
           TSemicolon
           TColon
           TQuestion
           TAt
           TDollar
           TPlus
           TStar
           TPercent |],
        kinds "{}()[],;:?@$+*%"
    )

[<Fact>]
let ``a realistic verb body round-trips with no lex error`` () =
    let source =
        """
        /* sanitize a name for use as a directory/file component */
        name = args[1];
        if (typeof(name) != STR)
          return E_TYPE;
        endif
        name = strsub(name, " ", "_");
        for c, i in ({"*", "/", "\\"})
          name = strsub(name, c, "");
        endfor
        return name[1..min(length(name), 40)];
        """

    let result = tokenize source
    Assert.True(result.Error.IsNone, sprintf "unexpected lex error: %A" result.Error)
    Assert.True(result.Tokens.Length > 10)
