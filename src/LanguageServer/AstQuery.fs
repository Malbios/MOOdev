/// Finds the reference-bearing AST node (if any) under a given cursor
/// position - the piece hover/go-to-definition both need first: "which
/// identifier is the cursor actually on," not just "which statement."
/// Walks the whole `Stmt list` since nodes don't carry parent pointers.
module LanguageServer.AstQuery

open Language.Ast

/// The four expression shapes an LSP ever needs to resolve identity for.
/// Kept as a closed set rather than exposing raw `Expr` so callers (the
/// hover/definition handlers) pattern-match on intent, not on AST shape.
type Reference =
    | RefIdent of name: string
    | RefCall of name: string * args: Arg list
    | RefProp of receiver: Expr * name: Expr
    | RefVerbCall of receiver: Expr * name: Expr * args: Arg list

type FoundReference =
    { Line: int
      Col: int
      /// Best-effort span length: the literal name's character count where
      /// there is one, 1 otherwise (a computed `obj.(expr)`/`obj:(expr)()`
      /// reference has no name token to measure - `Line`/`Col` there point
      /// at the opening `(` and the "span" is just that one character).
      Length: int
      Ref: Reference }

let private refLength (r: Reference) : int =
    match r with
    | RefIdent name -> name.Length
    | RefCall(name, _) -> name.Length
    | RefProp(_, StrLit name) -> name.Length
    | RefProp _ -> 1
    | RefVerbCall(_, StrLit name, _) -> name.Length
    | RefVerbCall _ -> 1

let rec private collectExpr (acc: ResizeArray<FoundReference>) (e: Expr) : unit =
    let add line col r =
        acc.Add { Line = line; Col = col; Length = refLength r; Ref = r }

    match e with
    | Ident(name, line, col) -> add line col (RefIdent name)
    | Call(name, args, line, col) ->
        add line col (RefCall(name, args))
        args |> List.iter (collectArg acc)
    | Prop(objE, nameE, line, col) ->
        add line col (RefProp(objE, nameE))
        collectExpr acc objE
        collectExpr acc nameE
    | VerbCall(recvE, nameE, args, line, col) ->
        add line col (RefVerbCall(recvE, nameE, args))
        collectExpr acc recvE
        collectExpr acc nameE
        args |> List.iter (collectArg acc)
    | IntLit _
    | FloatLit _
    | StrLit _
    | ObjLit _
    | ErrLit _
    | FirstIndex
    | LastIndex -> ()
    | Index(a, b) ->
        collectExpr acc a
        collectExpr acc b
    | Range(a, b) ->
        collectExpr acc a
        collectExpr acc b
    | Binary(_, a, b) ->
        collectExpr acc a
        collectExpr acc b
    | Unary(_, a) -> collectExpr acc a
    | Cond(a, b, c) ->
        collectExpr acc a
        collectExpr acc b
        collectExpr acc c
    | Catch(a, codes, fallback) ->
        collectExpr acc a
        collectCodes acc codes
        fallback |> Option.iter (collectExpr acc)
    | Assign(a, b) ->
        collectExpr acc a
        collectExpr acc b
    | Scatter(_, e) -> collectExpr acc e
    | ListLit args -> args |> List.iter (collectArg acc)
    | MapLit pairs ->
        pairs
        |> List.iter (fun (k, v) ->
            collectExpr acc k
            collectExpr acc v)

and private collectArg (acc: ResizeArray<FoundReference>) (a: Arg) : unit =
    match a with
    | Normal e -> collectExpr acc e
    | Splice e -> collectExpr acc e

and private collectCodes (acc: ResizeArray<FoundReference>) (c: Codes) : unit =
    match c with
    | AnyCode -> ()
    | Codes args -> args |> List.iter (collectArg acc)

let rec private collectStmt (acc: ResizeArray<FoundReference>) (s: Stmt) : unit =
    match s with
    | If(arms, elsePart) ->
        arms
        |> List.iter (fun (cond, body) ->
            collectExpr acc cond
            body |> List.iter (collectStmt acc))

        elsePart |> Option.iter (List.iter (collectStmt acc))
    | ForList(_, _, source, body) ->
        collectExpr acc source
        body |> List.iter (collectStmt acc)
    | ForRange(_, lo, hi, body) ->
        collectExpr acc lo
        collectExpr acc hi
        body |> List.iter (collectStmt acc)
    | While(_, cond, body) ->
        collectExpr acc cond
        body |> List.iter (collectStmt acc)
    | Fork(_, delay, body) ->
        collectExpr acc delay
        body |> List.iter (collectStmt acc)
    | TryExcept(body, arms) ->
        body |> List.iter (collectStmt acc)

        arms
        |> List.iter (fun a ->
            collectCodes acc a.Codes
            a.Body |> List.iter (collectStmt acc))
    | TryFinally(body, handler) ->
        body |> List.iter (collectStmt acc)
        handler |> List.iter (collectStmt acc)
    | ExprStmt e -> collectExpr acc e
    | Return eOpt -> eOpt |> Option.iter (collectExpr acc)
    | Break _
    | Continue _
    | ErrorStmt _ -> ()

/// Every reference-bearing node in a verb's AST, in no particular order.
let collectReferences (stmts: Stmt list) : FoundReference list =
    let acc = ResizeArray()
    stmts |> List.iter (collectStmt acc)
    List.ofSeq acc

/// The reference whose span contains `(line, col)`, if any. Both are
/// 1-based, matching `Lexer.fs`'s token positions directly - callers
/// converting from LSP's 0-based `Position` must add 1 to each first.
let referenceAt (line: int) (col: int) (stmts: Stmt list) : FoundReference option =
    collectReferences stmts
    |> List.tryFind (fun r -> r.Line = line && col >= r.Col && col < r.Col + max r.Length 1)

/// The reference (of any kind) on the same line, at or before `(line,
/// col)`, closest to it - "what's the nearest reference to the left of the
/// cursor." Used for completion/signature-help context, which cares about
/// a reference the cursor is *inside or just past* (e.g. right after
/// `$vcs:` with nothing typed yet for the verb name, or mid-argument-list),
/// not necessarily still exactly touching it the way `referenceAt` requires.
let nearestReferenceAtOrBefore (line: int) (col: int) (stmts: Stmt list) : FoundReference option =
    collectReferences stmts
    |> List.filter (fun r -> r.Line = line && r.Col <= col)
    |> List.sortByDescending (fun r -> r.Col)
    |> List.tryHead

/// Every place a variable name is bound anywhere in a verb's AST - via
/// assignment, scatter targets, `for` loop variables, `fork`'s bound
/// task-id variable, and `except`'s bound error name - each with the
/// position of the identifier token that introduces it. Not
/// position/scope-aware in the sense of "declared before this point": MOO
/// has no block-scoping rule that would make that meaningfully more
/// correct, and the editor doesn't sync unsaved keystrokes to the server
/// anyway (`Handlers.fs`'s completion/go-to-definition handlers both
/// operate on the last-saved AST). Order is traversal order, not
/// necessarily position order - callers that care about "the *first*
/// binding" (`firstBindingSite`) sort explicitly rather than relying on it.
let allBoundNames (stmts: Stmt list) : BoundName list =
    let acc = ResizeArray<BoundName>()

    let rec goExpr (e: Expr) : unit =
        match e with
        | Assign(Ident(name, line, col), rhs) ->
            acc.Add { Name = name; Line = line; Col = col }
            goExpr rhs
        | Assign(lhs, rhs) ->
            goExpr lhs
            goExpr rhs
        | Scatter(items, rhs) ->
            items
            |> List.iter (fun item ->
                match item with
                | Required b
                | Rest b -> acc.Add b
                | Optional(b, defOpt) ->
                    acc.Add b
                    defOpt |> Option.iter goExpr)

            goExpr rhs
        | Ident _
        | IntLit _
        | FloatLit _
        | StrLit _
        | ObjLit _
        | ErrLit _
        | FirstIndex
        | LastIndex -> ()
        | Call(_, args, _, _) -> args |> List.iter goArg
        | Prop(a, b, _, _) ->
            goExpr a
            goExpr b
        | VerbCall(a, b, args, _, _) ->
            goExpr a
            goExpr b
            args |> List.iter goArg
        | Index(a, b)
        | Range(a, b)
        | Binary(_, a, b) ->
            goExpr a
            goExpr b
        | Unary(_, a) -> goExpr a
        | Cond(a, b, c) ->
            goExpr a
            goExpr b
            goExpr c
        | Catch(a, codes, fallback) ->
            goExpr a
            goCodes codes
            fallback |> Option.iter goExpr
        | ListLit args -> args |> List.iter goArg
        | MapLit pairs ->
            pairs
            |> List.iter (fun (k, v) ->
                goExpr k
                goExpr v)

    and goArg (a: Arg) : unit =
        match a with
        | Normal e
        | Splice e -> goExpr e

    and goCodes (c: Codes) : unit =
        match c with
        | AnyCode -> ()
        | Codes args -> args |> List.iter goArg

    let rec goStmt (s: Stmt) : unit =
        match s with
        | If(arms, elsePart) ->
            arms
            |> List.iter (fun (cond, body) ->
                goExpr cond
                body |> List.iter goStmt)

            elsePart |> Option.iter (List.iter goStmt)
        | ForList(var, indexVar, source, body) ->
            acc.Add var
            indexVar |> Option.iter acc.Add
            goExpr source
            body |> List.iter goStmt
        | ForRange(var, lo, hi, body) ->
            acc.Add var
            goExpr lo
            goExpr hi
            body |> List.iter goStmt
        | While(_, cond, body) ->
            goExpr cond
            body |> List.iter goStmt
        | Fork(name, delay, body) ->
            name |> Option.iter acc.Add
            goExpr delay
            body |> List.iter goStmt
        | TryExcept(body, arms) ->
            body |> List.iter goStmt

            arms
            |> List.iter (fun a ->
                a.Name |> Option.iter acc.Add
                goCodes a.Codes
                a.Body |> List.iter goStmt)
        | TryFinally(body, handler) ->
            body |> List.iter goStmt
            handler |> List.iter goStmt
        | ExprStmt e -> goExpr e
        | Return eOpt -> eOpt |> Option.iter goExpr
        | Break _
        | Continue _
        | ErrorStmt _ -> ()

    stmts |> List.iter goStmt
    List.ofSeq acc

/// Every `(line, col)` position of a `Prop` node that is the assignment
/// *target* of `obj.prop = expr;` - `collectReferences`'s `RefProp` entries
/// don't distinguish a read from a write (`collectExpr`'s `Assign` case
/// recurses into both sides uniformly, so `x = obj.prop;` and
/// `obj.prop = x;` produce the identical `RefProp` entry for the same
/// `Prop` node), so a caller that needs to tell them apart (the
/// property-level dead-reference finder) checks its occurrences' positions
/// against this set instead. Mirrors `allBoundNames`'s own full-tree walk
/// shape.
let assignedPropertyPositions (stmts: Stmt list) : Set<int * int> =
    let acc = ResizeArray<int * int>()

    let rec goExpr (e: Expr) : unit =
        match e with
        | Assign(Prop(objE, nameE, line, col), rhs) ->
            acc.Add(line, col)
            goExpr objE
            goExpr nameE
            goExpr rhs
        | Assign(lhs, rhs) ->
            goExpr lhs
            goExpr rhs
        | Ident _
        | IntLit _
        | FloatLit _
        | StrLit _
        | ObjLit _
        | ErrLit _
        | FirstIndex
        | LastIndex -> ()
        | Call(_, args, _, _) -> args |> List.iter goArg
        | Prop(a, b, _, _) ->
            goExpr a
            goExpr b
        | VerbCall(a, b, args, _, _) ->
            goExpr a
            goExpr b
            args |> List.iter goArg
        | Index(a, b)
        | Range(a, b)
        | Binary(_, a, b) ->
            goExpr a
            goExpr b
        | Unary(_, a) -> goExpr a
        | Cond(a, b, c) ->
            goExpr a
            goExpr b
            goExpr c
        | Catch(a, codes, fallback) ->
            goExpr a
            goCodes codes
            fallback |> Option.iter goExpr
        | Scatter(items, rhs) ->
            items
            |> List.iter (function
                | Optional(_, Some def) -> goExpr def
                | Required _
                | Optional(_, None)
                | Rest _ -> ())

            goExpr rhs
        | ListLit args -> args |> List.iter goArg
        | MapLit pairs ->
            pairs
            |> List.iter (fun (k, v) ->
                goExpr k
                goExpr v)

    and goArg (a: Arg) : unit =
        match a with
        | Normal e
        | Splice e -> goExpr e

    and goCodes (c: Codes) : unit =
        match c with
        | AnyCode -> ()
        | Codes args -> args |> List.iter goArg

    let rec goStmt (s: Stmt) : unit =
        match s with
        | If(arms, elsePart) ->
            arms
            |> List.iter (fun (cond, body) ->
                goExpr cond
                body |> List.iter goStmt)

            elsePart |> Option.iter (List.iter goStmt)
        | ForList(_, _, source, body) ->
            goExpr source
            body |> List.iter goStmt
        | ForRange(_, lo, hi, body) ->
            goExpr lo
            goExpr hi
            body |> List.iter goStmt
        | While(_, cond, body) ->
            goExpr cond
            body |> List.iter goStmt
        | Fork(_, delay, body) ->
            goExpr delay
            body |> List.iter goStmt
        | TryExcept(body, arms) ->
            body |> List.iter goStmt

            arms
            |> List.iter (fun a ->
                goCodes a.Codes
                a.Body |> List.iter goStmt)
        | TryFinally(body, handler) ->
            body |> List.iter goStmt
            handler |> List.iter goStmt
        | ExprStmt e -> goExpr e
        | Return eOpt -> eOpt |> Option.iter goExpr
        | Break _
        | Continue _
        | ErrorStmt _ -> ()

    stmts |> List.iter goStmt
    Set.ofSeq acc

/// Every distinct variable name bound anywhere in a verb's AST - see
/// `allBoundNames` for exactly which binding shapes count. Used by
/// completion, which only wants the set of names, not where they come from.
let boundVariableNames (stmts: Stmt list) : string list =
    allBoundNames stmts |> List.map (fun b -> b.Name) |> List.distinct |> List.sort

/// Where `name` is *first* bound in `stmts`, if it's bound at all - the
/// earliest `(line, col)` among every binding site `allBoundNames` finds
/// for it, regardless of traversal order (MOO's lack of block scoping means
/// "first" is well-defined this way, same reasoning `allBoundNames` itself
/// documents). Used by go-to-definition on a plain local-variable
/// identifier: unlike a verb call, there's no dispatch to resolve - jumping
/// to wherever the name was introduced (a plain assignment, a scatter
/// target, a `for`/`fork` binding, or an `except` arm's error name) is the
/// whole answer.
let firstBindingSite (stmts: Stmt list) (name: string) : (int * int) option =
    allBoundNames stmts
    |> List.filter (fun b -> b.Name = name)
    |> List.sortBy (fun b -> b.Line, b.Col)
    |> List.tryHead
    |> Option.map (fun b -> b.Line, b.Col)
