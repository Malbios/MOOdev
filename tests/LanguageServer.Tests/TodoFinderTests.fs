/// Hand-built fixture graphs for `Handlers.findTodos` - same reasoning as
/// `GotchaFinderTests.fs`: precise control over each verb's token stream
/// rather than depending on a larger synthetic corpus. Tokens are built by
/// hand rather than lexing real source, since only `Kind`/`Line` matter
/// here (`Col` is irrelevant to `leadingDocCommentLines`).
module LanguageServer.Tests.TodoFinderTests

open Xunit
open Language.Lexer
open Metadata.Schema
open LanguageServer.Handlers

let private verbMeta (index: int) (name: string) : VerbMeta =
    { Index = index
      Names = [ name ]
      Owner = 2L
      Perms = "rxd"
      Dobj = "this"
      Prep = "none"
      Iobj = "this" }

let private tok (line: int) (kind: TokenKind) : Token = { Kind = kind; Line = line; Col = 1 }

/// One `"text";` statement worth of tokens, on its own line.
let private strStmt (line: int) (text: string) : Token list = [ tok line (TStr text); tok line TSemicolon ]

let private verbNode (definedOn: ObjRef) (meta: VerbMeta) (tokens: Token list) : VerbNode =
    { Meta = meta
      DefinedOn = definedOn
      SourcePath = None
      Ast = None
      DiagnosticCount = 0
      Tokens = Some(Array.ofList tokens) }

let private objNode (num: ObjRef) (verbs: VerbNode list) : ObjectNode =
    { Num = num
      Name = None
      LiveName = None
      Parents = []
      Children = []
      Verbs = verbs
      Owner = None
      Flags = None
      Properties = []
      Aliases = [] }

let private graphOf (objects: ObjectNode list) : Graph =
    { Objects = objects |> List.map (fun o -> o.Num, o) |> Map.ofList
      SystemObjectProperties = Map.empty
      Builtins = Map.empty }

[<Fact>]
let ``a leading TODO: line is found with its line number and kind`` () =
    let v = verbNode 1L (verbMeta 1 "sweep") (strStmt 1 "TODO: fix this")
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Contains(todos, (fun t -> t.ObjRef = 1L && t.VerbName = "sweep" && t.Line = 1 && t.Kind = "TODO" && t.Text = "TODO: fix this"))

[<Fact>]
let ``a leading FIXME: line is found and kind is FIXME`` () =
    let v = verbNode 1L (verbMeta 1 "sweep") (strStmt 1 "FIXME: this is broken")
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Contains(todos, (fun t -> t.ObjRef = 1L && t.VerbName = "sweep" && t.Kind = "FIXME"))

[<Fact>]
let ``matching is case-insensitive on the prefix`` () =
    let v = verbNode 1L (verbMeta 1 "sweep") (strStmt 1 "todo: lowercase works too")
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Contains(todos, (fun t -> t.ObjRef = 1L && t.Kind = "TODO"))

[<Fact>]
let ``multiple leading doc-comment lines each get their own entry with the right line number`` () =
    let tokens = strStmt 1 "a plain doc line" @ strStmt 2 "TODO: second line" @ strStmt 3 "FIXME: third line"
    let v = verbNode 1L (verbMeta 1 "sweep") tokens
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Contains(todos, (fun t -> t.Line = 2 && t.Kind = "TODO"))
    Assert.Contains(todos, (fun t -> t.Line = 3 && t.Kind = "FIXME"))
    Assert.DoesNotContain(todos, (fun t -> t.Line = 1))

[<Fact>]
let ``a TODO-shaped string that isn't in the leading run is ignored`` () =
    // Real code (an assignment) breaks the leading run before the TODO line
    // is ever reached - `x = 1;` tokens, then the string statement.
    let tokens =
        [ tok 1 (TIdent "x"); tok 1 TAssign; tok 1 (TInt 1L); tok 1 TSemicolon ] @ strStmt 2 "TODO: too late, not a doc comment"

    let v = verbNode 1L (verbMeta 1 "sweep") tokens
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Empty(todos)

[<Fact>]
let ``a leading string with no TODO/FIXME prefix produces no entry`` () =
    let v = verbNode 1L (verbMeta 1 "sweep") (strStmt 1 "just a description, nothing to track")
    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Empty(todos)

[<Fact>]
let ``a verb with no tokens is skipped without error`` () =
    let v =
        { Meta = verbMeta 1 "sweep"
          DefinedOn = 1L
          SourcePath = None
          Ast = None
          DiagnosticCount = 0
          Tokens = None }

    let graph = graphOf [ objNode 1L [ v ] ]

    let todos = findTodos graph
    Assert.Empty(todos)
