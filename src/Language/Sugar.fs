/// Sugared MOOcode <-> real MOOcode, full round trip, for the editor's own
/// display-only dialect: no trailing `;` on statements, indentation implies
/// the block closer instead of an explicit `endif`/`endfor`/`endwhile`/
/// `endfork`/`endtry`. The MOO server, git-tracked exports, and every other
/// tool only ever see real MOOcode - sugar is purely this editor's own
/// presentation, built and torn down here.
///
/// `toSugar` is a subtractive, lexer-driven line transform, not an AST
/// pretty-printer: `Language.Ast`'s `Expr` normalizes away exactly the
/// surface detail a faithful unparser would need (e.g. `obj.prop`/
/// `obj.(expr)`/`obj.:name` all collapse to one `Prop` node; original
/// parenthesization and literal spelling aren't preserved; most statement
/// nodes carry no source span at all) - reconstructing text from the AST
/// would be a second, harder unparsing project that still risks corrupting
/// the majority of a verb it didn't need to touch. Every character
/// `toSugar` doesn't explicitly drop or strip survives verbatim.
///
/// `toReal` can't be another lexer-only pass the same way - MOOcode has no
/// signal for "this statement continues on the next line" other than an
/// unclosed paren/bracket/brace, so it needs a small indentation-tracking
/// preprocessor (a block-stack), not a second parser/grammar duplicating
/// `Parser.fs`'s expression precedence just to reach the same Ast for no
/// benefit here.
module Language.Sugar

open Language.Lexer

/// Per-line correspondence between real and sugar text, produced by both
/// `toSugar` and `toReal` alike - the same two arrays make sense either
/// direction, since a line only ever gets dropped/inserted on the *real*
/// side (a closer-only line has no sugar counterpart), while every sugar
/// line always corresponds to exactly one real line (itself, unmodified or
/// with a semicolon added/removed).
type LineMap =
    { /// 0-based real line -> 0-based sugar line, `None` for a real line
      /// with no sugar counterpart (a dropped/inserted closer-only line).
      RealToSugar: int option []
      /// 0-based sugar line -> the 0-based real line whose content produced
      /// it. Always present - every sugar line comes from exactly one real
      /// line.
      SugarToReal: int[] }

type SugarResult = { Text: string; Map: LineMap }

/// Given a 0-based *real* line index that might have no direct sugar
/// counterpart (a dropped closer line - `map.RealToSugar.[i] = None`), finds
/// the sugar line to attribute it to: itself if it maps directly, otherwise
/// the nearest real line at or before it that does map. That's the last real
/// statement whose dedent caused the closer to be inserted, matching where a
/// human reading the sugar text would expect e.g. a "missing endif" class of
/// result to point. Used wherever a real-line-numbered result (a compile
/// error, an LSP position) needs a best-effort sugar-displayed line.
let nearestMappedSugarLine (map: LineMap) (realIdx: int) : int =
    let rec find (idx: int) : int =
        if idx < 0 || map.RealToSugar.Length = 0 then
            0
        else
            let clamped = min idx (map.RealToSugar.Length - 1)

            match map.RealToSugar.[clamped] with
            | Some sugarIdx -> sugarIdx
            | None -> find (clamped - 1)

    find realIdx

let private closerKeywords = set [ Keyword.EndIf; Keyword.EndFor; Keyword.EndWhile; Keyword.EndFork; Keyword.EndTry ]

let private closerText =
    dict
        [ Keyword.EndIf, "endif"
          Keyword.EndFor, "endfor"
          Keyword.EndWhile, "endwhile"
          Keyword.EndFork, "endfork"
          Keyword.EndTry, "endtry" ]

/// Block openers this feature understands, mapped to their one matching
/// closer - confirmed against `Parser.fs`'s own block-opener/closer set:
/// exactly these five pairs, no others exist, and every opener always has
/// exactly one closer (no bodyless single-line `if` form exists in the
/// grammar).
let private openerCloser =
    dict
        [ Keyword.If, Keyword.EndIf
          Keyword.For, Keyword.EndFor
          Keyword.While, Keyword.EndWhile
          Keyword.Fork, Keyword.EndFork
          Keyword.Try, Keyword.EndTry ]

/// Reverse of `openerCloser` - used to recognize an explicit closer keyword
/// that survived into sugar text (`toSugar` leaves a closer-only line
/// untouched if it carries a trailing comment, since dropping it would lose
/// that comment - see `toSugar`'s own doc comment) so `toReal` can pop the
/// matching frame using *that* text as the closer, instead of also
/// auto-inserting a second one via the ordinary dedent path.
let private closerOpener = dict [ Keyword.EndIf, Keyword.If; Keyword.EndFor, Keyword.For; Keyword.EndWhile, Keyword.While; Keyword.EndFork, Keyword.Fork; Keyword.EndTry, Keyword.Try ]

/// Continuation keywords legal at an open frame's own indentation (siblings
/// of the opener in `Ast.fs`'s model, not nested under it) - narrows as arms
/// are seen, mirroring `Parser.parseTry`'s try/except-xor-finally
/// exclusivity and `parseIf`'s arm order.
let private openerContinuations = dict [ Keyword.If, [ Keyword.ElseIf; Keyword.Else ]; Keyword.Try, [ Keyword.Except; Keyword.Finally ] ]

let private continuationKeywordText =
    dict [ Keyword.ElseIf, "elseif"; Keyword.Else, "else"; Keyword.Except, "except"; Keyword.Finally, "finally" ]

let private narrowContinuations (opener: Keyword) (seen: Keyword) : Keyword list =
    match opener, seen with
    | Keyword.If, Keyword.ElseIf -> [ Keyword.ElseIf; Keyword.Else ]
    | Keyword.If, Keyword.Else -> []
    | Keyword.Try, Keyword.Except -> [ Keyword.Except; Keyword.Finally ]
    | Keyword.Try, Keyword.Finally -> []
    | _ -> []

let private splitLines (source: string) : string[] = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

let private leadingWhitespaceLength (line: string) : int =
    let mutable i = 0
    while i < line.Length && (line.[i] = ' ' || line.[i] = '\t') do
        i <- i + 1
    i

let private bracketDelta (k: TokenKind) : int =
    match k with
    | TLBrace
    | TLParen
    | TLBracket -> 1
    | TRBrace
    | TRParen
    | TRBracket -> -1
    | _ -> 0

let private tokensByLine (tokens: Token[]) : Map<int, Token[]> =
    tokens |> Array.filter (fun t -> t.Kind <> TEOF) |> Array.groupBy (fun t -> t.Line) |> Map.ofArray

/// Real MOOcode (as `verb_code()` returns it) -> sugared display text.
/// Drops a line whose only token is a closer keyword (checked by exact
/// character span, not just "one token" - a trailing block comment right
/// after the keyword is real content and must survive, so this only drops
/// the line when nothing but whitespace remains once the keyword's own
/// span is removed); strips a line's trailing semicolon token the same
/// character-precise way (so a same-line trailing comment after the `;`
/// survives too); everything else passes through completely unchanged.
let toSugar (source: string) : Result<SugarResult, string> =
    match tokenize source with
    | { Error = Some e } -> Error(sprintf "Line %d: %s" e.Line e.Message)
    | { Tokens = tokens } ->
        let realLines = splitLines source
        let byLine = tokensByLine tokens

        let sugarLines = ResizeArray<string>()
        let sugarSourceLines = ResizeArray<int>() // parallel to sugarLines: which real line it came from

        for i in 0 .. realLines.Length - 1 do
            let lineNo = i + 1
            let lineText = realLines.[i]

            match Map.tryFind lineNo byLine with
            | None ->
                sugarLines.Add(lineText)
                sugarSourceLines.Add(i)
            | Some lineTokens ->
                let soleCloser =
                    if lineTokens.Length = 1 then
                        match lineTokens.[0].Kind with
                        | TKeyword kw when closerKeywords.Contains kw -> Some(lineTokens.[0], kw)
                        | _ -> None
                    else
                        None

                match soleCloser with
                | Some(tok, kw) ->
                    let kwText = closerText.[kw]
                    let startIdx = tok.Col - 1
                    let before = lineText.Substring(0, min startIdx lineText.Length)
                    let afterIdx = min (startIdx + kwText.Length) lineText.Length
                    let after = lineText.Substring(afterIdx)

                    if before.Trim() = "" && after.Trim() = "" then
                        () // drop the line entirely - nothing else on it
                    else
                        sugarLines.Add(lineText)
                        sugarSourceLines.Add(i)
                | None ->
                    let lastTok = lineTokens.[lineTokens.Length - 1]

                    match lastTok.Kind with
                    | TSemicolon ->
                        let idx = lastTok.Col - 1

                        let stripped =
                            if idx >= 0 && idx < lineText.Length && lineText.[idx] = ';' then
                                lineText.Remove(idx, 1)
                            else
                                lineText // shouldn't happen - defensive, leave untouched rather than corrupt

                        sugarLines.Add(stripped)
                        sugarSourceLines.Add(i)
                    | _ ->
                        sugarLines.Add(lineText)
                        sugarSourceLines.Add(i)

        let sugarToReal = sugarSourceLines.ToArray()
        let realToSugar = Array.create realLines.Length None
        sugarToReal |> Array.iteri (fun sugarIdx realIdx -> realToSugar.[realIdx] <- Some sugarIdx)

        Ok
            { Text = String.concat "\n" sugarLines
              Map = { RealToSugar = realToSugar; SugarToReal = sugarToReal } }

/// One open block frame while desugaring - `OpenerIndent` is the opener
/// line's own leading-whitespace column; `Continuations` narrows as
/// `elseif`/`else`/`except`/`finally` arms are seen (see
/// `narrowContinuations`).
type private Frame =
    { OpenerIndent: int
      Opener: Keyword
      mutable Continuations: Keyword list }

/// Sugared display text -> real MOOcode. See the module doc comment for why
/// this is an indentation-tracking preprocessor, not a second parser.
/// Single forward pass over physical lines, maintaining a block-stack and a
/// running paren/bracket/brace depth (the only multi-line-statement signal
/// MOOcode has - there is no other way to know a leaf statement continues
/// onto the next line). A continuation keyword (`elseif`/`else`/`except`/
/// `finally`) that doesn't match any open frame at its own indentation is a
/// fast `Error`, surfaced before any server round trip.
let toReal (sugarSource: string) : Result<SugarResult, string> =
    match tokenize sugarSource with
    | { Error = Some e } -> Error(sprintf "Line %d: %s" e.Line e.Message)
    | { Tokens = tokens } ->
        let sugarLines = splitLines sugarSource
        let byLine = tokensByLine tokens

        let realLines = ResizeArray<string>()
        let realSourceLines = ResizeArray<int option>() // None for an inserted closer line
        let stack = ResizeArray<Frame>()
        let mutable bracketDepth = 0
        // Some appendSemicolonWhenDone - set while a statement/line is mid-
        // continuation (bracketDepth > 0) across physical lines; cleared
        // once depth returns to 0. `appendSemicolonWhenDone` is true only
        // for a leaf statement - an opener/continuation/self-contained
        // line spanning multiple physical lines (e.g. a wrapped `if (...)`
        // condition) never gets one.
        let mutable pending: bool option = None
        let mutable errorMsg: string option = None

        /// Appends `lineText` as-is, folds its tokens into the running
        /// bracket depth, and - once depth returns to 0 - either finishes
        /// the statement (appending `;` if `appendSemicolonWhenDone`) or
        /// leaves `pending` set for the next physical line to continue.
        let addLine (lineText: string) (i: int) (lineTokens: Token[]) (appendSemicolonWhenDone: bool) =
            realLines.Add(lineText)
            realSourceLines.Add(Some i)

            for t in lineTokens do
                bracketDepth <- bracketDepth + bracketDelta t.Kind

            if bracketDepth <= 0 then
                if appendSemicolonWhenDone then
                    let lastIdx = realLines.Count - 1
                    realLines.[lastIdx] <- realLines.[lastIdx].TrimEnd() + ";"

                pending <- None
            else
                pending <- Some appendSemicolonWhenDone

        let mutable i = 0

        while i < sugarLines.Length && errorMsg.IsNone do
            let lineNo = i + 1
            let lineText = sugarLines.[i]
            let lineTokensOpt = Map.tryFind lineNo byLine

            match pending with
            | Some appendSemicolonWhenDone ->
                // Mid-continuation of an already-started line - not a new
                // logical line, no indentation/keyword analysis at all.
                realLines.Add(lineText)
                realSourceLines.Add(Some i)

                match lineTokensOpt with
                | Some lts -> for t in lts do bracketDepth <- bracketDepth + bracketDelta t.Kind
                | None -> ()

                if bracketDepth <= 0 then
                    if appendSemicolonWhenDone then
                        let lastIdx = realLines.Count - 1
                        realLines.[lastIdx] <- realLines.[lastIdx].TrimEnd() + ";"

                    pending <- None

                i <- i + 1
            | None ->
                match lineTokensOpt with
                | None ->
                    // Blank or fully comment-covered line - passes through,
                    // no stack effect (no tokens means no depth change
                    // either).
                    realLines.Add(lineText)
                    realSourceLines.Add(Some i)
                    i <- i + 1
                | Some lts ->
                    let indent = leadingWhitespaceLength lineText

                    let firstKw =
                        match lts.[0].Kind with
                        | TKeyword kw -> Some kw
                        | _ -> None

                    let isLegalContinuationOf (frame: Frame) =
                        indent = frame.OpenerIndent
                        && (match firstKw with
                            | Some kw -> List.contains kw frame.Continuations
                            | None -> false)

                    let isExplicitCloserOf (frame: Frame) =
                        match firstKw with
                        | Some kw -> closerOpener.ContainsKey kw && closerOpener.[kw] = frame.Opener
                        | None -> false

                    // Dedent-close: pop frames while this line's indent is
                    // at or below the top frame's own opener indent, unless
                    // it's exactly that indent and a legal continuation
                    // (which narrows the frame instead of closing it). A
                    // line whose own text is already the exact matching
                    // closer for the current top frame (kept as real syntax
                    // by `toSugar`, e.g. because of a trailing comment) pops
                    // that one frame using *this* line as its closer -
                    // stopping there, without inserting a second one -
                    // rather than falling through to the generic
                    // leaf/opener handling below.
                    let mutable keepPopping = true
                    let mutable usedAsExplicitCloser = false

                    while keepPopping && stack.Count > 0 do
                        let top = stack.[stack.Count - 1]

                        if isExplicitCloserOf top then
                            stack.RemoveAt(stack.Count - 1)
                            usedAsExplicitCloser <- true
                            keepPopping <- false
                        elif indent <= top.OpenerIndent && not (isLegalContinuationOf top) then
                            realLines.Add(String.replicate top.OpenerIndent " " + closerText.[openerCloser.[top.Opener]])
                            realSourceLines.Add(None)
                            stack.RemoveAt(stack.Count - 1)
                        else
                            keepPopping <- false

                    if usedAsExplicitCloser then
                        realLines.Add(lineText)
                        realSourceLines.Add(Some i)

                        for t in lts do
                            bracketDepth <- bracketDepth + bracketDelta t.Kind

                        i <- i + 1
                    elif stack.Count > 0 && isLegalContinuationOf stack.[stack.Count - 1] then
                        let top = stack.[stack.Count - 1]
                        let kw = firstKw.Value
                        top.Continuations <- narrowContinuations top.Opener kw
                        addLine lineText i lts false
                        i <- i + 1
                    else
                        match firstKw with
                        | Some kw when continuationKeywordText.ContainsKey kw ->
                            // Didn't match any open frame above at this
                            // indentation - a real, fast error rather than
                            // silently misplacing a closer.
                            errorMsg <- Some(sprintf "Line %d: '%s' does not match an open block at this indentation" lineNo continuationKeywordText.[kw])
                        | Some kw when openerCloser.ContainsKey kw ->
                            // Self-contained one-liner check: does this same
                            // physical line already carry its own matching
                            // closer(s) (real syntax someone typed as-is,
                            // e.g. `if (x) foo(); endif`)? Net-count openers
                            // of this same keyword against its closer across
                            // the line's own tokens; net <= 0 means fully
                            // closed already - leave untouched. A net > 1
                            // (nested same-type one-liners on one physical
                            // line) is rare enough that pushing `net` frames
                            // at this line's indent is a reasonable, if
                            // approximate, fallback rather than a hard error.
                            let closer = openerCloser.[kw]

                            let net =
                                lts
                                |> Array.sumBy (fun t ->
                                    match t.Kind with
                                    | TKeyword k when k = kw -> 1
                                    | TKeyword k when k = closer -> -1
                                    | _ -> 0)

                            if net <= 0 then
                                addLine lineText i lts false
                            else
                                for _ in 1..net do
                                    stack.Add(
                                        { OpenerIndent = indent
                                          Opener = kw
                                          Continuations = (if openerContinuations.ContainsKey kw then openerContinuations.[kw] else []) }
                                    )

                                addLine lineText i lts false

                            i <- i + 1
                        | _ ->
                            // Leaf statement (or a line not starting with a
                            // recognized keyword at all).
                            addLine lineText i lts true
                            i <- i + 1

        if errorMsg.IsNone then
            while stack.Count > 0 do
                let top = stack.[stack.Count - 1]
                realLines.Add(String.replicate top.OpenerIndent " " + closerText.[openerCloser.[top.Opener]])
                realSourceLines.Add(None)
                stack.RemoveAt(stack.Count - 1)

        match errorMsg with
        | Some msg -> Error msg
        | None ->
            let realToSugar = realSourceLines.ToArray()
            let sugarToReal = Array.create sugarLines.Length 0

            realToSugar
            |> Array.iteri (fun r sOpt ->
                match sOpt with
                | Some s -> sugarToReal.[s] <- r
                | None -> ())

            Ok
                { Text = String.concat "\n" realLines
                  Map = { RealToSugar = realToSugar; SugarToReal = sugarToReal } }

/// `toReal(toSugar(real).Text).Text = Ok real` - the safety net a caller
/// checks before ever showing sugar text for a given verb (see the plan's
/// Phase 2). Cheap: both directions are pure functions, no I/O.
let roundTripsCleanly (realSource: string) : bool =
    match toSugar realSource with
    | Error _ -> false
    | Ok sugar ->
        match toReal sugar.Text with
        | Error _ -> false
        | Ok real2 -> real2.Text = realSource
