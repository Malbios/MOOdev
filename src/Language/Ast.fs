/// AST for MOOcode. Node shapes follow `parser.y`'s grammar (see Lexer.fs's
/// header for the overall "port from source, not from the reference doc"
/// approach) rather than a generic scripting-language AST - notably:
/// property/verb access normalize computed and literal forms onto the same
/// node (`obj.prop`, `obj.(expr)`, and `obj.:waifprop` all become `Prop`;
/// `obj:name(...)` and `obj:(expr)()` both become `VerbCall`), and
/// `except`'s codes are an arbitrary expression list at runtime, not a
/// closed literal set (`parser.y` `codes: tANY | ne_arglist`).
module Language.Ast

// Most nodes still don't carry source spans - literals, operators, and
// control-flow shapes are never something an LSP resolves a cursor
// position against. The four *reference-bearing* expression cases
// (`Ident`, `Prop`, `VerbCall`, `Call`) do carry a `line`/`col` pointing at
// the specific name token, added for Phase 4.4 (go-to-definition/hover need
// to know exactly which identifier a cursor is over, not just which
// statement).

type BinOp =
    | Add
    | Sub
    | Mul
    | Div
    | Mod
    | Pow
    | Eq
    | NotEq
    | Lt
    | LtEq
    | Gt
    | GtEq
    | And
    | Or
    | In
    | BitAnd
    | BitOr
    | BitXor
    | Shl
    | Shr

type UnOp =
    | Neg
    | Not
    | BitNot

/// A call argument - `@expr` splices a list in, matching both list literals
/// and argument lists (`parser.y`'s `arglist`/`ne_arglist` productions).
type Arg =
    | Normal of Expr
    | Splice of Expr

/// `except`'s "codes": either the bare `ANY` keyword (compiles to integer
/// `0`, matches everything - `parser.y` `codes: tANY`), or an arbitrary
/// argument list evaluated at runtime, which may itself use splicing
/// (`codes: ne_arglist`). Not a set of error-literal tokens.
and Codes =
    | AnyCode
    | Codes of Arg list

/// A bound variable name together with the position of the identifier
/// token that introduces it - shared by every binding-site shape besides
/// plain assignment (`for`-loop variables, `fork`'s bound task-id variable,
/// `except`'s bound error-info name, and scatter-assignment targets), so
/// go-to-definition can point at the exact token that introduces the name
/// rather than just knowing its spelling. Plain `x = expr;` assignment
/// doesn't need this - its target is already an ordinary `Ident`, which
/// already carries its own `line`/`col`.
and BoundName = { Name: string; Line: int; Col: int }

and ScatterItem =
    | Required of BoundName
    | Optional of BoundName * Expr option
    | Rest of BoundName

and Expr =
    | IntLit of int64
    | FloatLit of float
    | StrLit of string
    | ObjLit of int64
    | ErrLit of string
    /// `line`/`col` point at the identifier token itself.
    | Ident of name: string * line: int * col: int
    /// `^` / `$` used bare inside index/range brackets - "first index" (1,
    /// or 0 on an empty collection) / "last index" (length). Parser-context
    /// legal only directly inside `[...]`; the parser enforces that context,
    /// not the lexer (`Lexer.fs` just emits `TCaret`/`TDollar`).
    | FirstIndex
    | LastIndex
    /// `obj.prop` (name: StrLit), `obj.(expr)` (computed), and `obj.:name`
    /// (waif property sugar for `obj.(":name")`, `parser.y:405-416`) all
    /// normalize here. `line`/`col` point at the property-name token for
    /// the literal/waif forms, or the opening `(` for the computed form.
    | Prop of obj: Expr * name: Expr * line: int * col: int
    /// `obj:name(args)` (name: StrLit) and `obj:(expr)(args)` (computed -
    /// "invisible to static analysis" per the plan doc's Known Hazards;
    /// the resolver, not the parser, is what has to flag this) both
    /// normalize here. `line`/`col` point at the verb-name token for the
    /// literal form, or the opening `(` for the computed form.
    | VerbCall of receiver: Expr * name: Expr * args: Arg list * line: int * col: int
    /// Any `IDENT(args)` call. Deliberately does NOT distinguish
    /// builtin-vs-user-defined-vs-unknown at parse time - `parser.y:496-516`
    /// confirms unknown names are still syntactically valid (they become a
    /// hard compile error only in the *real* compiler, at a later semantic
    /// stage this parser doesn't replicate). `line`/`col` point at the
    /// function-name token.
    | Call of name: string * args: Arg list * line: int * col: int
    | Index of Expr * Expr
    /// `a..b` - used both as a for-loop range (`for i in [a..b]`) and as an
    /// index/slice subscript (`list[a..b]`); `FirstIndex`/`LastIndex` are
    /// legal as either bound only in the index/slice case.
    | Range of Expr * Expr
    | Binary of BinOp * Expr * Expr
    | Unary of UnOp * Expr
    /// `cond ? then | else` - nonassoc per `parser.y`'s precedence table;
    /// the parser does not accept an unparenthesized chain.
    | Cond of Expr * Expr * Expr
    /// `` `expr ! codes => fallback' `` - asymmetric backtick/single-quote
    /// delimiters; `fallback` is `None` when omitted, which evaluates to
    /// the error code itself at runtime (element 1 of the 4-tuple), not to
    /// a list or to zero.
    | Catch of Expr * Codes * Expr option
    | Assign of Expr * Expr
    /// Both scatter-assignment grammar paths (`parser.y:466-495` list-literal
    /// rewrite, and `parser.y:742-779`'s explicit `{...} =` form with
    /// `?optional`/`@rest`) normalize to this one shape.
    | Scatter of ScatterItem list * Expr
    | ListLit of Arg list
    | MapLit of (Expr * Expr) list

type ExceptArm =
    { Name: BoundName option
      Codes: Codes
      Body: Stmt list }

and Stmt =
    | If of arms: (Expr * Stmt list) list * elsePart: Stmt list option
    /// `for x in (expr)` / `for value, key in (map-expr)` - the two-variable
    /// form binds value first, then key/index (confirmed live and from
    /// source; NOT key-then-value as other languages do it).
    | ForList of var: BoundName * indexVar: BoundName option * source: Expr * body: Stmt list
    /// `for x in [a..b]` - a distinct grammar shape from `ForList`, not the
    /// same production with a `Range` source (`parser.y` treats the two
    /// `for` forms separately).
    | ForRange of var: BoundName * lo: Expr * hi: Expr * body: Stmt list
    /// `name` is a loop label only (`break name`/`continue name` target it
    /// by string, never as a real MOO variable read via a plain `Ident`) -
    /// unlike `Fork`'s `name` below, this deliberately stays a bare
    /// `string option`, not a `BoundName`.
    | While of name: string option * cond: Expr * body: Stmt list
    /// `name` is `fork ID (delay) ... endfork`'s bound task-id identifier
    /// (`parser.y:224-233` - `find_id($2)` there registers a real variable,
    /// not a label), later readable via a plain `Ident` (e.g.
    /// `kill_task(name)`), hence `BoundName` here - unlike `While`'s
    /// loop-label `name` above. (An earlier version of this parser also
    /// recognized `IDENT = fork (delay) ... endfork` as a second way to
    /// bind this - removed: confirmed against `parser.y` that `fork` is a
    /// keyword-led statement only, never part of the `expr:` grammar, so it
    /// can never legally sit on an assignment's right-hand side. Nothing in
    /// the real `Survive` corpus used that form either.)
    | Fork of name: BoundName option * delay: Expr * body: Stmt list
    /// `except` and `finally` are disjoint - a `try` has one-or-more
    /// `except` arms XOR exactly one `finally`, never both
    /// (`parser.y:277-288`).
    | TryExcept of body: Stmt list * arms: ExceptArm list
    | TryFinally of body: Stmt list * handler: Stmt list
    | ExprStmt of Expr
    | Return of Expr option
    | Break of string option
    | Continue of string option
    /// A statement the parser couldn't make sense of - recovery skipped
    /// forward to the next `;` at the current block-nesting depth. Carries
    /// the diagnostic and the raw skipped token span so a caller (the
    /// corpus harness, eventually the LSP) can report it without the whole
    /// verb's parse failing.
    | ErrorStmt of message: string * line: int * col: int

/// Counts `ErrorStmt` nodes anywhere in the tree, including nested inside
/// block bodies - a clean parse has zero. Shared by the corpus test harness
/// and the metadata loader (Phase 4.3b), which both need to know whether a
/// parsed verb is fully clean before trusting its AST.
let rec countErrors (stmts: Stmt list) : int =
    stmts
    |> List.sumBy (fun s ->
        match s with
        | ErrorStmt _ -> 1
        | If (arms, elsePart) ->
            (arms |> List.sumBy (fun (_, body) -> countErrors body))
            + (elsePart |> Option.map countErrors |> Option.defaultValue 0)
        | ForList (_, _, _, body) -> countErrors body
        | ForRange (_, _, _, body) -> countErrors body
        | While (_, _, body) -> countErrors body
        | Fork (_, _, body) -> countErrors body
        | TryExcept (body, arms) -> countErrors body + (arms |> List.sumBy (fun a -> countErrors a.Body))
        | TryFinally (body, handler) -> countErrors body + countErrors handler
        | ExprStmt _
        | Return _
        | Break _
        | Continue _ -> 0)
