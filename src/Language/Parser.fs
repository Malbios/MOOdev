/// Recursive-descent, precedence-climbing parser for MOOcode, built directly
/// on `Lexer.fs`'s token stream. Precedence levels and associativity are
/// taken from `parser.y`'s own table (also transcribed in
/// `moocode-reference.md`'s "Precedence, lowest to highest" section) - not
/// re-derived by guessing.
///
/// Deliberately permissive where the real compiler does semantic (not
/// syntactic) validation: unknown function names, scatter-target shape,
/// and assignment-lvalue shape are all accepted here and left for a later
/// layer, matching this parser's job (IDE navigation over possibly-invalid
/// code) rather than the real compiler's job (reject invalid code).
///
/// Error recovery is statement-level: a failure inside one statement skips
/// forward to the next `;` at the current block-nesting depth and continues
/// with an `ErrorStmt` in its place, so one bad verb never aborts parsing
/// the rest of a file, let alone a corpus.
///
/// `Lexer.Keyword` and `Ast.Stmt`/`Ast.BinOp` share several case names
/// (`If`, `While`, `Fork`, `In`, `Return`, `Break`, `Continue`) - every
/// reference to a lexer keyword below is written `Keyword.X` even where it
/// wouldn't strictly need to be, so this file doesn't silently depend on
/// `open` order.
module Language.Parser

open Language.Lexer
open Language.Ast

exception private ParseFailure of string * int * int

type private BraceItem =
    | BiNormal of Expr
    | BiSplice of Expr
    /// `line`/`col` point at the target-name token, kept only for the
    /// lossy `Ident` fallback `toArg` builds when a `?name` scatter item
    /// shows up outside a real scatter-assignment context.
    | BiOptional of name: string * def: Expr option * line: int * col: int

/// Parses `tokens` (from `Lexer.tokenize`) into a statement list. Failures
/// never propagate out - they surface as `ErrorStmt` nodes in the returned
/// list (or nested inside block bodies), positioned where recovery found
/// its next foothold.
let parse (tokens: Token[]) : Stmt list =
    let mutable idx = 0
    let lastIdx = tokens.Length - 1

    let peekKindAt (offset: int) : TokenKind =
        let i = idx + offset
        if i <= lastIdx then tokens.[i].Kind else TEOF

    let peekKind () = peekKindAt 0
    let curTok () = tokens.[min idx lastIdx]

    let advance () : Token =
        let t = curTok ()

        if idx < tokens.Length then
            idx <- idx + 1

        t

    let check (k: TokenKind) = peekKind () = k

    let fail (msg: string) : 'a =
        let t = curTok ()
        raise (ParseFailure(msg, t.Line, t.Col))

    let expect (k: TokenKind) =
        if check k then
            advance () |> ignore
        else
            fail (sprintf "expected %A, found %A" k (peekKind ()))

    let expectIdent () =
        match peekKind () with
        | TIdent name ->
            advance () |> ignore
            name
        | other -> fail (sprintf "expected identifier, found %A" other)

    /// Like `expectIdent`, but also captures the identifier token's own
    /// position as a `BoundName` - for the binding-site shapes
    /// (`for`/`fork`/`except`/scatter targets) that need to record where a
    /// variable name was introduced, not just its spelling.
    let expectBoundName () : BoundName =
        let t = curTok ()
        let name = expectIdent ()
        { Name = name; Line = t.Line; Col = t.Col }

    let optionalName () =
        match peekKind () with
        | TIdent name ->
            advance () |> ignore
            Some name
        | _ -> None

    /// Like `optionalName`, but also captures the identifier token's own
    /// position as a `BoundName` - see `expectBoundName`.
    let optionalBoundName () : BoundName option =
        match peekKind () with
        | TIdent name ->
            let t = advance ()
            Some { Name = name; Line = t.Line; Col = t.Col }
        | _ -> None

    // ---------------------------------------------------------------
    // Expressions
    // ---------------------------------------------------------------

    let rec parseExpr () = parseAssign ()

    /// True when the current token is `{` and, scanning forward by brace
    /// depth (pure lookahead, no consumption - tokens are already a random
    /// -access array so this is cheap), the matching `}` is immediately
    /// followed by `=`. Distinguishes scatter assignment (`{a, b} = x;`)
    /// from an ordinary list literal that merely starts an expression
    /// (`{"a","b"}[i]`, `{what} == y`, etc.) - both start identically, and
    /// only this lookahead tells them apart. Scatter's own grammar
    /// (`parser.y:466-495`/`742-779`) never nests another top-level `{` at
    /// the same depth in a way this depth-counter would mis-track.
    and looksLikeScatterAssignment () : bool =
        if not (check TLBrace) then
            false
        else
            let mutable depth = 0
            let mutable i = idx
            let mutable scanning = true
            let mutable result = false

            while scanning && i <= lastIdx do
                match tokens.[i].Kind with
                | TLBrace ->
                    depth <- depth + 1
                    i <- i + 1
                | TRBrace ->
                    depth <- depth - 1
                    i <- i + 1

                    if depth = 0 then
                        scanning <- false
                        result <- i <= lastIdx && tokens.[i].Kind = TAssign
                | TEOF -> scanning <- false
                | _ -> i <- i + 1

            result

    and parseAssign () : Expr =
        if looksLikeScatterAssignment () then
            let items = parseBraceItemList ()
            expect TAssign
            Scatter(items |> List.map toScatterItem, parseAssign ())
        else
            let lhs = parseTernary ()

            if check TAssign then
                advance () |> ignore
                Assign(lhs, parseAssign ())
            else
                lhs

    and parseBraceItemList () : BraceItem list =
        expect TLBrace
        let items = ResizeArray()

        if not (check TRBrace) then
            items.Add(parseBraceItem ())

            while check TComma do
                advance () |> ignore
                items.Add(parseBraceItem ())

        expect TRBrace
        List.ofSeq items

    and parseBraceItem () : BraceItem =
        match peekKind () with
        | TAt ->
            advance () |> ignore
            BiSplice(parseAssign ())
        | TQuestion ->
            advance () |> ignore
            let tok = curTok ()
            let name = expectIdent ()

            if check TAssign then
                advance () |> ignore
                BiOptional(name, Some(parseAssign ()), tok.Line, tok.Col)
            else
                BiOptional(name, None, tok.Line, tok.Col)
        | _ -> BiNormal(parseAssign ())

    and toScatterItem (item: BraceItem) : ScatterItem =
        match item with
        | BiNormal (Ident (name, line, col)) -> Required { Name = name; Line = line; Col = col }
        | BiNormal _ -> Required { Name = ""; Line = 0; Col = 0 } // malformed scatter target; permissive fallback, not a parse failure
        | BiSplice (Ident (name, line, col)) -> Rest { Name = name; Line = line; Col = col }
        | BiSplice _ -> Rest { Name = ""; Line = 0; Col = 0 }
        | BiOptional (name, def, line, col) -> Optional({ Name = name; Line = line; Col = col }, def)

    and toArg (item: BraceItem) : Arg =
        match item with
        | BiNormal e -> Normal e
        | BiSplice e -> Splice e
        | BiOptional (name, defOpt, line, col) -> Normal(defaultArg defOpt (Ident(name, line, col))) // lossy fallback for `?` outside scatter

    /// Level 2: `cond ? then | else`. `parser.y`'s actual production is
    /// `expr: expr '?' expr '|' expr` - all three positions are the same
    /// unrestricted `expr` nonterminal, so both `then` and `else` may
    /// themselves be full (possibly nested) ternaries with no parens
    /// needed; `%nonassoc '?' '|'` only blocks a ternary's *overall result*
    /// from being immediately followed by another `?` (left-chaining,
    /// `(a?b|c)?d|e`, a genuine grammar conflict bison refuses to resolve).
    /// This parser doesn't replicate that specific rejection - being more
    /// permissive than the real compiler here is consistent with its
    /// overall design (see module doc) and observed corpus code relies on
    /// the right-nesting this enables (e.g. `cond ? a?b|c | d`).
    and parseTernary () : Expr =
        let cond = parseOrAnd ()

        if check TQuestion then
            advance () |> ignore
            // Branches parse at parseAssign, not parseTernary: `expr '?'
            // expr '|' expr` in parser.y uses the unrestricted `expr`
            // nonterminal for both branches, so real corpus code embeds
            // assignments here (`cond ? this.(k) = v | default`) alongside
            // the already-needed nested-ternary case.
            let thenE = parseAssign ()
            expect TPipe
            let elseE = parseAssign ()
            Cond(cond, thenE, elseE)
        else
            cond

    /// Level 3: `||` and `&&` share one precedence level, left-assoc.
    and parseOrAnd () : Expr =
        let mutable left = parseCompare ()
        let mutable go = true

        while go do
            match peekKind () with
            | TOr ->
                advance () |> ignore
                left <- Binary(BinOp.Or, left, parseCompare ())
            | TAnd ->
                advance () |> ignore
                left <- Binary(BinOp.And, left, parseCompare ())
            | _ -> go <- false

        left

    /// Level 4: comparisons and `in`.
    and parseCompare () : Expr =
        let mutable left = parseBitwise ()
        let mutable go = true

        while go do
            match peekKind () with
            | TEq ->
                advance () |> ignore
                left <- Binary(BinOp.Eq, left, parseBitwise ())
            | TNotEq ->
                advance () |> ignore
                left <- Binary(BinOp.NotEq, left, parseBitwise ())
            | TLt ->
                advance () |> ignore
                left <- Binary(BinOp.Lt, left, parseBitwise ())
            | TLtEq ->
                advance () |> ignore
                left <- Binary(BinOp.LtEq, left, parseBitwise ())
            | TGt ->
                advance () |> ignore
                left <- Binary(BinOp.Gt, left, parseBitwise ())
            | TGtEq ->
                advance () |> ignore
                left <- Binary(BinOp.GtEq, left, parseBitwise ())
            | TKeyword Keyword.In ->
                advance () |> ignore
                left <- Binary(BinOp.In, left, parseBitwise ())
            | _ -> go <- false

        left

    /// Level 5: `|.` `&.` `^.`
    and parseBitwise () : Expr =
        let mutable left = parseShift ()
        let mutable go = true

        while go do
            match peekKind () with
            | TBitOr ->
                advance () |> ignore
                left <- Binary(BinOp.BitOr, left, parseShift ())
            | TBitAnd ->
                advance () |> ignore
                left <- Binary(BinOp.BitAnd, left, parseShift ())
            | TBitXor ->
                advance () |> ignore
                left <- Binary(BinOp.BitXor, left, parseShift ())
            | _ -> go <- false

        left

    /// Level 6: `<<` `>>`
    and parseShift () : Expr =
        let mutable left = parseAdditive ()
        let mutable go = true

        while go do
            match peekKind () with
            | TShl ->
                advance () |> ignore
                left <- Binary(BinOp.Shl, left, parseAdditive ())
            | TShr ->
                advance () |> ignore
                left <- Binary(BinOp.Shr, left, parseAdditive ())
            | _ -> go <- false

        left

    /// Level 7: `+` `-`
    and parseAdditive () : Expr =
        let mutable left = parseMultiplicative ()
        let mutable go = true

        while go do
            match peekKind () with
            | TPlus ->
                advance () |> ignore
                left <- Binary(BinOp.Add, left, parseMultiplicative ())
            | TMinus ->
                advance () |> ignore
                left <- Binary(BinOp.Sub, left, parseMultiplicative ())
            | _ -> go <- false

        left

    /// Level 8: `*` `/` `%`
    and parseMultiplicative () : Expr =
        let mutable left = parseExponent ()
        let mutable go = true

        while go do
            match peekKind () with
            | TStar ->
                advance () |> ignore
                left <- Binary(BinOp.Mul, left, parseExponent ())
            | TSlash ->
                advance () |> ignore
                left <- Binary(BinOp.Div, left, parseExponent ())
            | TPercent ->
                advance () |> ignore
                left <- Binary(BinOp.Mod, left, parseExponent ())
            | _ -> go <- false

        left

    /// Level 9: `^`, right-associative, tighter than the binary levels
    /// above but looser than unary prefix (level 10) - so `-x^2` is
    /// `(-x)^2`: `parseUnary` (called for the left operand here) consumes
    /// the `-` before this rule ever sees the `^`.
    and parseExponent () : Expr =
        let left = parseUnary ()

        if check TCaret then
            advance () |> ignore
            Binary(BinOp.Pow, left, parseExponent ())
        else
            left

    /// Level 10: unary `!` `~` `-`
    and parseUnary () : Expr =
        match peekKind () with
        | TNot ->
            advance () |> ignore
            Unary(UnOp.Not, parseUnary ())
        | TTilde ->
            advance () |> ignore
            Unary(BitNot, parseUnary ())
        | TMinus ->
            advance () |> ignore
            Unary(Neg, parseUnary ())
        | _ -> parsePostfix ()

    /// Level 11: `.` `:` `[` (property/verb access, indexing) applied
    /// left-to-right after a primary expression.
    and parsePostfix () : Expr = parsePostfixFrom (parsePrimary ())

    /// Same postfix loop, starting from an already-parsed expression
    /// rather than calling `parsePrimary` itself - used when a primary
    /// was parsed speculatively elsewhere (brace-list/scatter
    /// disambiguation in `parseAssign`) and still needs `[...]`/`.`/`:`
    /// applied.
    and parsePostfixFrom (start: Expr) : Expr =
        let mutable e = start
        let mutable go = true

        while go do
            match peekKind () with
            | TDot ->
                advance () |> ignore

                match peekKind () with
                | TColon ->
                    // obj.:name - waif property sugar for obj.(":name")
                    // (parser.y:405-416).
                    advance () |> ignore
                    let tok = curTok ()
                    let name = expectIdent ()
                    e <- Prop(e, StrLit(":" + name), tok.Line, tok.Col)
                | TLParen ->
                    let tok = curTok ()
                    advance () |> ignore
                    let inner = parseExpr ()
                    expect TRParen
                    e <- Prop(e, inner, tok.Line, tok.Col)
                | _ ->
                    let tok = curTok ()
                    let name = expectIdent ()
                    e <- Prop(e, StrLit name, tok.Line, tok.Col)
            | TColon ->
                advance () |> ignore
                let tok = curTok ()

                let nameExpr =
                    match peekKind () with
                    | TLParen ->
                        advance () |> ignore
                        let inner = parseExpr ()
                        expect TRParen
                        inner
                    | _ -> StrLit(expectIdent ())

                let args = parseArgList ()
                e <- VerbCall(e, nameExpr, args, tok.Line, tok.Col)
            | TLBracket ->
                advance () |> ignore
                let sub = parseRangeOrExpr ()
                expect TRBracket
                e <- Index(e, sub)
            | _ -> go <- false

        e

    /// Shared by index/slice subscripts (`list[a..b]`) and `for`-range
    /// bounds - a bare expr, or `a..b`.
    and parseRangeOrExpr () : Expr =
        let first = parseAssign ()

        if check TDotDot then
            advance () |> ignore
            Range(first, parseAssign ())
        else
            first

    and parseArgList () : Arg list =
        expect TLParen
        let args = ResizeArray()

        if not (check TRParen) then
            args.Add(parseArg ())

            while check TComma do
                advance () |> ignore
                args.Add(parseArg ())

        expect TRParen
        List.ofSeq args

    and parseArg () : Arg =
        if check TAt then
            advance () |> ignore
            Splice(parseAssign ())
        else
            Normal(parseAssign ())

    and parseCodes () : Codes =
        if check (TKeyword Keyword.Any) then
            advance () |> ignore
            AnyCode
        else
            let args = ResizeArray()
            args.Add(parseArg ())

            while check TComma do
                advance () |> ignore
                args.Add(parseArg ())

            Codes(List.ofSeq args)

    and parsePrimary () : Expr =
        match peekKind () with
        | TInt v ->
            advance () |> ignore
            IntLit v
        | TFloat v ->
            advance () |> ignore
            FloatLit v
        | TStr s ->
            advance () |> ignore
            StrLit s
        | TObj v ->
            advance () |> ignore
            ObjLit v
        | TErr e ->
            advance () |> ignore
            ErrLit e
        | TDollar ->
            advance () |> ignore

            match peekKind () with
            | TIdent name ->
                let tok = curTok ()
                advance () |> ignore

                if check TLParen then
                    // $foo(args) === #0:foo(args) (parser.y:428-436: "treat
                    // $bar(args) like #0:('bar')(args)") - a distinct
                    // production from plain $foo (property sugar, below).
                    VerbCall(ObjLit 0L, StrLit name, parseArgList (), tok.Line, tok.Col)
                else
                    // $foo === #0.foo
                    Prop(ObjLit 0L, StrLit name, tok.Line, tok.Col)
            | _ -> LastIndex
        | TCaret ->
            advance () |> ignore
            FirstIndex
        | TIdent name ->
            let tok = curTok ()
            advance () |> ignore

            if check TLParen then
                Call(name, parseArgList (), tok.Line, tok.Col)
            else
                Ident(name, tok.Line, tok.Col)
        | TLParen ->
            advance () |> ignore
            let inner = parseExpr ()
            expect TRParen
            inner
        | TLBrace -> ListLit(parseBraceItemList () |> List.map toArg)
        | TLBracket ->
            advance () |> ignore

            if check TRBracket then
                advance () |> ignore
                MapLit []
            else
                let pairs = ResizeArray()

                let parsePair () =
                    let k = parseAssign ()
                    expect TMapArrow
                    let v = parseAssign ()
                    pairs.Add(k, v)

                parsePair ()

                while check TComma do
                    advance () |> ignore
                    parsePair ()

                expect TRBracket
                MapLit(List.ofSeq pairs)
        | TBacktick ->
            advance () |> ignore
            let tryE = parseExpr ()
            expect TNot
            let codes = parseCodes ()

            let fallback =
                if check TArrow then
                    advance () |> ignore
                    Some(parseExpr ())
                else
                    None

            expect TSingleQuote
            Catch(tryE, codes, fallback)
        | other -> fail (sprintf "unexpected token %A" other)

    // ---------------------------------------------------------------
    // Statements
    // ---------------------------------------------------------------

    and parseIf () : Stmt =
        advance () |> ignore // 'if'
        expect TLParen
        let cond = parseExpr ()
        expect TRParen
        let body = parseBlockUntil [ Keyword.ElseIf; Keyword.Else; Keyword.EndIf ]
        let arms = ResizeArray [ (cond, body) ]

        while check (TKeyword Keyword.ElseIf) do
            advance () |> ignore
            expect TLParen
            let c2 = parseExpr ()
            expect TRParen
            let b2 = parseBlockUntil [ Keyword.ElseIf; Keyword.Else; Keyword.EndIf ]
            arms.Add(c2, b2)

        let elsePart =
            if check (TKeyword Keyword.Else) then
                advance () |> ignore
                Some(parseBlockUntil [ Keyword.EndIf ])
            else
                None

        expect (TKeyword Keyword.EndIf)
        Ast.If(List.ofSeq arms, elsePart)

    and parseFor () : Stmt =
        advance () |> ignore // 'for'
        let var1 = expectBoundName ()

        if check TComma then
            advance () |> ignore
            let var2 = expectBoundName ()
            expect (TKeyword Keyword.In)
            expect TLParen
            let src = parseExpr ()
            expect TRParen
            let body = parseBlockUntil [ Keyword.EndFor ]
            expect (TKeyword Keyword.EndFor)
            ForList(var1, Some var2, src, body)
        else
            expect (TKeyword Keyword.In)

            match peekKind () with
            | TLParen ->
                advance () |> ignore
                let src = parseExpr ()
                expect TRParen
                let body = parseBlockUntil [ Keyword.EndFor ]
                expect (TKeyword Keyword.EndFor)
                ForList(var1, None, src, body)
            | TLBracket ->
                advance () |> ignore
                let lo = parseAssign ()
                expect TDotDot
                let hi = parseAssign ()
                expect TRBracket
                let body = parseBlockUntil [ Keyword.EndFor ]
                expect (TKeyword Keyword.EndFor)
                ForRange(var1, lo, hi, body)
            | other -> fail (sprintf "expected '(' or '[' after 'for %s in', found %A" var1.Name other)

    and parseWhile () : Stmt =
        advance () |> ignore // 'while'
        let name = optionalName ()
        expect TLParen
        let cond = parseExpr ()
        expect TRParen
        let body = parseBlockUntil [ Keyword.EndWhile ]
        expect (TKeyword Keyword.EndWhile)
        Ast.While(name, cond, body)

    and parseFork () : Stmt =
        advance () |> ignore // 'fork'
        let name = optionalBoundName ()
        expect TLParen
        let delay = parseExpr ()
        expect TRParen
        let body = parseBlockUntil [ Keyword.EndFork ]
        expect (TKeyword Keyword.EndFork)
        Ast.Fork(name, delay, body)

    and parseTry () : Stmt =
        advance () |> ignore // 'try'
        let body = parseBlockUntil [ Keyword.Except; Keyword.Finally; Keyword.EndTry ]

        if check (TKeyword Keyword.Finally) then
            advance () |> ignore
            let handler = parseBlockUntil [ Keyword.EndTry ]
            expect (TKeyword Keyword.EndTry)
            TryFinally(body, handler)
        else
            let arms = ResizeArray()

            while check (TKeyword Keyword.Except) do
                advance () |> ignore
                let name = optionalBoundName ()
                expect TLParen
                let codes = parseCodes ()
                expect TRParen
                let armBody = parseBlockUntil [ Keyword.Except; Keyword.EndTry ]
                arms.Add { Name = name; Codes = codes; Body = armBody }

            expect (TKeyword Keyword.EndTry)
            TryExcept(body, List.ofSeq arms)

    and parseStatement () : Stmt =
        match peekKind () with
        | TKeyword Keyword.If -> parseIf ()
        | TKeyword Keyword.For -> parseFor ()
        | TKeyword Keyword.While -> parseWhile ()
        | TKeyword Keyword.Fork -> parseFork ()
        | TKeyword Keyword.Try -> parseTry ()
        | TKeyword Keyword.Return ->
            advance () |> ignore

            if check TSemicolon then
                advance () |> ignore
                Ast.Return None
            else
                let e = parseExpr ()
                expect TSemicolon
                Ast.Return(Some e)
        | TKeyword Keyword.Break ->
            advance () |> ignore
            let name = optionalName ()
            expect TSemicolon
            Ast.Break name
        | TKeyword Keyword.Continue ->
            advance () |> ignore
            let name = optionalName ()
            expect TSemicolon
            Ast.Continue name
        | _ ->
            let e = parseExpr ()
            expect TSemicolon
            ExprStmt e

    /// Skips forward past a failed statement to the next `;` at the
    /// current block-nesting depth (tracking `if/for/while/fork/try`
    /// opens against their `end*` closes), or to the next token in
    /// `stoppers` seen at depth 0, or EOF - whichever comes first. Mirrors
    /// a standard "sync token" recovery strategy, sized for MOOcode's
    /// clearly-delimited statement/block shape.
    and skipToRecoveryPoint (stoppers: Keyword list) =
        let mutable depth = 0
        let mutable stop = false

        while not stop do
            match peekKind () with
            | TEOF -> stop <- true
            | TKeyword k when depth = 0 && List.contains k stoppers -> stop <- true
            | TKeyword (Keyword.If | Keyword.For | Keyword.Fork | Keyword.While | Keyword.Try) ->
                depth <- depth + 1
                advance () |> ignore
            | TKeyword (Keyword.EndIf | Keyword.EndFor | Keyword.EndFork | Keyword.EndWhile | Keyword.EndTry) ->
                if depth = 0 then
                    stop <- true
                else
                    depth <- depth - 1
                    advance () |> ignore
            | TSemicolon when depth = 0 ->
                advance () |> ignore
                stop <- true
            | _ -> advance () |> ignore

    and parseStatementWithRecovery (stoppers: Keyword list) : Stmt =
        let startIdx = idx

        try
            parseStatement ()
        with ParseFailure (msg, line, col) ->
            // Guarantee forward progress even if recovery itself finds
            // nothing to skip (e.g. failure right at EOF).
            if idx = startIdx && not (check TEOF) then
                advance () |> ignore

            skipToRecoveryPoint stoppers
            ErrorStmt(msg, line, col)

    and parseBlockUntil (stoppers: Keyword list) : Stmt list =
        let stmts = ResizeArray()

        let isStopper () =
            match peekKind () with
            | TKeyword k -> List.contains k stoppers
            | TEOF -> true
            | _ -> false

        while not (isStopper ()) do
            stmts.Add(parseStatementWithRecovery stoppers)

        List.ofSeq stmts

    parseBlockUntil []
