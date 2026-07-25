/// Hand-written scanner for MOOcode, a direct port of the hand-written lexer
/// inside `toaststunt/src/parser.y` (the `yylex` function feeding the bison
/// grammar) - not a generated lexer, since the source algorithm is small,
/// fully known, and easier to keep provably correct as a direct port than
/// to re-derive through a lexer-generator's own abstractions.
///
/// Every token spelling and disambiguation rule here is cited against
/// `parser.y` line ranges read directly this session, not against
/// `moocode-reference.md` (which itself now cites the same lines - both
/// should agree, but this file's comments point at the primary source).
module Language.Lexer

/// Control keywords from `keywords.gperf` (case-sensitive - gperf's
/// `in_word_set` does an exact match, no case folding). Note what's
/// deliberately absent: no `endfinally` (the block still closes with
/// `endtry`), no `pass` (it's a registered builtin, not syntax - confirmed
/// `execute.cc:3817`), no `true`/`false` (pre-bound local variables, not
/// keywords - confirmed `sym_table.cc:118-122`).
type Keyword =
    | If
    | Else
    | ElseIf
    | EndIf
    | For
    | In
    | EndFor
    | Fork
    | EndFork
    | Return
    | While
    | EndWhile
    | Try
    | Except
    | Finally
    | EndTry
    | Any
    | Break
    | Continue

let keywords =
    dict
        [ "if", If
          "else", Else
          "elseif", ElseIf
          "endif", EndIf
          "for", For
          "in", In
          "endfor", EndFor
          "fork", Fork
          "endfork", EndFork
          "return", Return
          "while", While
          "endwhile", EndWhile
          "try", Try
          "except", Except
          "finally", Finally
          "endtry", EndTry
          "ANY", Any
          "break", Break
          "continue", Continue ]

/// One 19-name closed set (`keywords.gperf`) - not an open `E_[A-Z]+`
/// pattern. Anything spelled `E_something` outside this list lexes as a
/// plain identifier.
let errorNames =
    set
        [ "E_NONE"
          "E_TYPE"
          "E_DIV"
          "E_PERM"
          "E_PROPNF"
          "E_VERBNF"
          "E_VARNF"
          "E_INVIND"
          "E_RECMOVE"
          "E_MAXREC"
          "E_RANGE"
          "E_ARGS"
          "E_NACC"
          "E_INVARG"
          "E_QUOTA"
          "E_FLOAT"
          "E_FILE"
          "E_EXEC"
          "E_INTRPT" ]

type TokenKind =
    | TKeyword of Keyword
    | TIdent of string
    | TInt of int64
    | TFloat of float
    | TObj of int64
    | TErr of string
    | TStr of string
    // Brackets / punctuation
    | TLBrace
    | TRBrace
    | TLParen
    | TRParen
    | TLBracket
    | TRBracket
    | TComma
    | TSemicolon
    | TColon
    | TQuestion
    | TAt
    | TDollar
    | TCaret
    | TTilde
    | TBacktick
    | TSingleQuote
    // Operators, exact spellings per parser.y's yylex switch (line ~1049-1067)
    | TPlus
    | TMinus
    | TStar
    | TSlash
    | TPercent
    | TAssign // =
    | TEq // ==
    | TNotEq // !=
    | TLt // <
    | TLtEq // <=
    | TGt // >
    | TGtEq // >=
    | TShl // <<
    | TShr // >>
    | TAnd // &&
    | TOr // ||
    | TNot // !
    | TBitAnd // &.
    | TBitOr // |.
    | TBitXor // ^.
    | TPipe // | (bare - ternary else-branch)
    | TArrow // => (tARROW - inline-catch fallback separator)
    | TMapArrow // -> (tMAP - map-literal separator)
    | TDot // .
    | TDotDot // .. (tTO - range)
    | TEOF

type Token =
    { Kind: TokenKind
      Line: int
      Col: int }

type LexError = { Message: string; Line: int; Col: int }

type LexResult =
    { Tokens: Token[]
      Error: LexError option }

let private isDigit (c: char) = c >= '0' && c <= '9'

let private isIdentStart (c: char) =
    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'

let private isIdentCont (c: char) = isIdentStart c || isDigit c

/// Scans `source` into a token stream. Mirrors `yylex`'s structure closely:
/// each branch below corresponds to one of its `if (...)` blocks or its
/// final `switch(c)`, in the same order, so a `parser.y` line reference
/// stays easy to cross-check against the matching branch here.
///
/// Uses plain index-based peek/advance throughout rather than trying to
/// mirror `lex_getc`/`lex_ungetc`'s single-character-pushback stream
/// directly - `source` already gives random access, a real pushback buffer
/// would just be reimplementing that. The one place this needs care is the
/// int/float/range disambiguation, where the C code's "read a char, decide,
/// maybe leave it read" shape is replaced here with "peek before deciding
/// whether to consume," applied consistently.
let tokenize (source: string) : LexResult =
    let n = source.Length
    let tokens = ResizeArray<Token>()
    let mutable error: LexError option = None

    let mutable index = 0
    let mutable line = 1
    let mutable col = 1

    let peekAt (i: int) : char = if i < n then source.[i] else '\000'
    let peek () = peekAt index
    let peek2 () = peekAt (index + 1)

    let advance () : char =
        let c = peek ()
        index <- index + 1

        if c = '\n' then
            line <- line + 1
            col <- 1
        else
            col <- col + 1

        c

    let atEnd () = index >= n

    let emit (startLine: int) (startCol: int) (kind: TokenKind) =
        tokens.Add { Kind = kind; Line = startLine; Col = startCol }

    let fail (startLine: int) (startCol: int) (msg: string) =
        if error.IsNone then
            error <- Some { Message = msg; Line = startLine; Col = startCol }

    /// `follow(expected, whenMatch, whenNot)` - parser.y's own helper,
    /// ported directly: if the next char is `expected`, consume it and
    /// yield `whenMatch`; otherwise leave the stream untouched and yield
    /// `whenNot`.
    let follow (expected: char) (whenMatch: TokenKind) (whenNot: TokenKind) =
        if peek () = expected then
            advance () |> ignore
            whenMatch
        else
            whenNot

    let mutable stop = false

    while not stop && error.IsNone do
        // Skip whitespace (parser.y's start_over loop: `while (isspace(c))`).
        while not (atEnd ()) && System.Char.IsWhiteSpace(peek ()) do
            advance () |> ignore

        if atEnd () then
            stop <- true
        else
            let startLine = line
            let startCol = col
            let c = advance ()

            // --- Block comments: parser.y:889-908 ---
            // Non-nesting; the scanner consumes the char after any `*`
            // without re-examining it, so `**/` does not close a comment
            // (the second `*` "uses up" the slot checking for `/`, finds
            // another `*` instead, and the loop restarts from scratch -
            // the stray `/` is never seen as a closer). Ported faithfully,
            // not "fixed", since real corpus code can rely on this quirk
            // either way. Confirmed in moocode-reference.md's Comments
            // section too.
            if c = '/' && peek () = '*' then
                advance () |> ignore // consume '*'
                let mutable closed = false
                let mutable sawStar = false

                while not closed && error.IsNone do
                    if atEnd () then
                        fail startLine startCol "End of program while in a comment"
                        closed <- true
                    else
                        let cc = advance ()

                        if sawStar then
                            if cc = '/' then
                                closed <- true

                            sawStar <- false
                        elif cc = '*' then
                            sawStar <- true
            elif c = '/' then
                emit startLine startCol TSlash
            // --- Object literals: parser.y:910-932 ---
            elif c = '#' then
                let mutable negative = false

                if peek () = '-' then
                    advance () |> ignore
                    negative <- true

                if not (isDigit (peek ())) then
                    fail startLine startCol "Malformed object number"
                else
                    let mutable oid = 0L

                    while isDigit (peek ()) do
                        oid <- oid * 10L + int64 (int (advance ()) - int '0')

                    emit startLine startCol (TObj(if negative then -oid else oid))
            // --- Numbers: parser.y:934-984 (leading-digit or leading-dot) ---
            elif isDigit c || c = '.' then
                let buf = System.Text.StringBuilder()
                let mutable isFloat = false
                let mutable bail = false // true => not a number at all (bare '.')

                if isDigit c then
                    buf.Append(c) |> ignore

                    while isDigit (peek ()) do
                        buf.Append(advance ()) |> ignore

                    if peek () = '.' then
                        let afterDot = peek2 ()

                        if isDigit afterDot then
                            buf.Append(advance ()) |> ignore // consume '.'
                            isFloat <- true

                            while isDigit (peek ()) do
                                buf.Append(advance ()) |> ignore
                        elif afterDot <> '.' then
                            // digits then a trailing dot, e.g. "3." or "3.x"
                            buf.Append(advance ()) |> ignore
                            isFloat <- true
                        // else "N.." - leave the dot alone; falls through
                        // to be re-lexed as the range token below.
                else
                    // c = '.' (leading-dot case, buf still empty)
                    if isDigit (peek ()) then
                        buf.Append(c) |> ignore
                        isFloat <- true

                        while isDigit (peek ()) do
                            buf.Append(advance ()) |> ignore
                    else
                        bail <- true

                if bail then
                    // `c` (the leading '.') was already consumed but never
                    // added to `buf` - reinterpret as punctuation, matching
                    // parser.y's `normal_dot` label / `follow('.', tTO, '.')`.
                    if peek () = '.' then
                        advance () |> ignore
                        emit startLine startCol TDotDot
                    else
                        emit startLine startCol TDot
                else
                    if peek () = 'e' || peek () = 'E' then
                        isFloat <- true
                        buf.Append(advance ()) |> ignore

                        if peek () = '+' || peek () = '-' then
                            buf.Append(advance ()) |> ignore

                        if not (isDigit (peek ())) then
                            fail startLine startCol "Malformed floating-point literal"
                        else
                            while isDigit (peek ()) do
                                buf.Append(advance ()) |> ignore

                    if error.IsNone then
                        let text = buf.ToString()

                        if isFloat then
                            emit
                                startLine
                                startCol
                                (TFloat(System.Double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)))
                        else
                            emit
                                startLine
                                startCol
                                (TInt(System.Int64.Parse(text, System.Globalization.CultureInfo.InvariantCulture)))
            // --- Identifiers / keywords / error literals: parser.y:1003-1030 ---
            elif isIdentStart c then
                let buf = System.Text.StringBuilder()
                buf.Append(c) |> ignore

                while isIdentCont (peek ()) do
                    buf.Append(advance ()) |> ignore

                let word = buf.ToString()

                if errorNames.Contains word then
                    emit startLine startCol (TErr word)
                else
                    match keywords.TryGetValue word with
                    | true, kw -> emit startLine startCol (TKeyword kw)
                    | false, _ -> emit startLine startCol (TIdent word)
            // --- Strings: parser.y:1032-1047 ---
            elif c = '"' then
                let buf = System.Text.StringBuilder()
                let mutable closed = false

                while not closed && error.IsNone do
                    if atEnd () then
                        fail startLine startCol "Missing quote"
                        closed <- true
                    else
                        let cc = advance ()

                        if cc = '"' then
                            closed <- true
                        elif cc = '\n' then
                            fail startLine startCol "Missing quote"
                            closed <- true
                        elif cc = '\\' then
                            if atEnd () || peek () = '\n' then
                                fail startLine startCol "Missing quote"
                                closed <- true
                            else
                                buf.Append(advance ()) |> ignore // next raw byte, unconditionally
                        else
                            buf.Append(cc) |> ignore

                if error.IsNone then
                    emit startLine startCol (TStr(buf.ToString()))
            // --- Operators / punctuation: parser.y:1049-1067 ---
            else
                match c with
                | '^' ->
                    // `check_two_dots()`: don't consume '.' as the start of
                    // `^.` (bitxor) if it's actually `..` (range) - needed
                    // for `list[^..N]` (first-index-to-N).
                    if peek () = '.' && peek2 () = '.' then
                        emit startLine startCol TCaret
                    else
                        emit startLine startCol (follow '.' TBitXor TCaret)
                | '>' -> emit startLine startCol (follow '>' TShr (follow '=' TGtEq TGt))
                | '<' -> emit startLine startCol (follow '<' TShl (follow '=' TLtEq TLt))
                | '=' -> emit startLine startCol (follow '=' TEq (follow '>' TArrow TAssign))
                | '|' -> emit startLine startCol (follow '.' TBitOr (follow '|' TOr TPipe))
                | '&' -> emit startLine startCol (follow '.' TBitAnd (follow '&' TAnd TAnd))
                | '-' -> emit startLine startCol (follow '>' TMapArrow TMinus)
                | '!' -> emit startLine startCol (follow '=' TNotEq TNot)
                | '.' -> emit startLine startCol (follow '.' TDotDot TDot)
                | '{' -> emit startLine startCol TLBrace
                | '}' -> emit startLine startCol TRBrace
                | '(' -> emit startLine startCol TLParen
                | ')' -> emit startLine startCol TRParen
                | '[' -> emit startLine startCol TLBracket
                | ']' -> emit startLine startCol TRBracket
                | ',' -> emit startLine startCol TComma
                | ';' -> emit startLine startCol TSemicolon
                | ':' -> emit startLine startCol TColon
                | '?' -> emit startLine startCol TQuestion
                | '@' -> emit startLine startCol TAt
                | '$' -> emit startLine startCol TDollar
                | '~' -> emit startLine startCol TTilde
                | '`' -> emit startLine startCol TBacktick
                | '\'' -> emit startLine startCol TSingleQuote
                | '+' -> emit startLine startCol TPlus
                | '*' -> emit startLine startCol TStar
                | '%' -> emit startLine startCol TPercent
                | other -> fail startLine startCol (sprintf "Unexpected character %A" other)

    match error with
    | Some _ -> { Tokens = tokens.ToArray(); Error = error }
    | None ->
        tokens.Add { Kind = TEOF; Line = line; Col = col }
        { Tokens = tokens.ToArray(); Error = None }
