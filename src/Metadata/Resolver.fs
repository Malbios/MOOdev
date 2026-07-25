/// Exact ports of the two C algorithms that decide "which verb actually
/// runs" - `verbcasecmp` (`utils.cc:76-110`, wildcard verb-name matching)
/// and `db_find_callable_verb`/`find_callable_verbdef` (`db_verbs.cc:483-524`,
/// the depth-first ancestor walk). Reimplementing dispatch by feel instead
/// of by source is exactly the trap the M4 plan calls out - both are
/// ported from the C control flow directly, not from a prose description.
module Metadata.Resolver

open Language.Ast
open Metadata.Schema

/// Case-insensitive ASCII-only fold matching `cmap[]` (`utils.cc:58-74`) -
/// only A-Z/a-z fold; bytes >= 128 compare unchanged, so non-ASCII verb
/// names are effectively case-sensitive.
let private foldChar (c: char) : char =
    if c >= 'A' && c <= 'Z' then char (int c + 32) else c

/// One space-delimited name pattern (e.g. `"l*ook"`) against one candidate
/// word - the inner loop body of `verbcasecmp`, with the outer
/// split-on-space handled separately via `VerbMeta.Names` (already split at
/// load time), rather than re-joining and re-splitting a string here.
let private matchesOnePattern (pattern: string) (candidate: string) : bool =
    let plen = pattern.Length
    let wlen = candidate.Length
    let mutable pi = 0
    let mutable wi = 0
    // 0 = no `*` seen yet, 1 = an interior `*` (permits only truncation of
    // the candidate at this point), 2 = a trailing `*` (permits arbitrary
    // remaining candidate text).
    let mutable star = 0
    let mutable brk = false

    while not brk do
        while pi < plen && pattern.[pi] = '*' do
            pi <- pi + 1
            star <- if pi >= plen then 2 else 1

        if pi >= plen || wi >= wlen || foldChar candidate.[wi] <> foldChar pattern.[pi] then
            brk <- true
        else
            wi <- wi + 1
            pi <- pi + 1

    if wi >= wlen then
        star <> 0 || pi >= plen
    else
        star = 2

/// True if `candidate` matches any of a verb's declared name patterns, in
/// the same left-to-right order `verbcasecmp` itself scans them (order
/// doesn't affect the boolean result here, only in the doc's "first match
/// wins" framing, which is about *verb* order, not pattern order within one
/// verb's name string - either way this checks all of them).
let verbNameMatchesAny (names: string list) (candidate: string) : bool =
    names |> List.exists (fun pattern -> matchesOnePattern pattern candidate)

/// A verb is callable via normal dispatch only when it carries the `x`
/// (executable) permission bit - `find_verbdef_by_name`'s `check_x_bit` is
/// always passed as `1` from `db_find_callable_verb` (`db_verbs.cc:489,509`).
let private isExecutable (meta: VerbMeta) : bool = meta.Perms.Contains 'x'

/// Scans one object's own verbs, in declared order, for the first
/// executable verb whose name list matches - `find_verbdef_by_name`
/// (`db_verbs.cc:227-238`), which stops at the first hit rather than
/// collecting every match.
let private findOwnVerb (graph: Graph) (obj: ObjRef) (verbName: string) : VerbNode option =
    graph.Objects
    |> Map.tryFind obj
    |> Option.bind (fun node ->
        node.Verbs
        |> List.tryFind (fun v -> isExecutable v.Meta && verbNameMatchesAny v.Meta.Names verbName))

/// Exact port of `find_callable_verbdef` (`db_verbs.cc:483-524`): check
/// `start` itself first, then walk ancestors depth-first, left-to-right
/// through each object's `parents` list, pushing each visited object's own
/// parents onto the *front* of the work stack (not the back) so that
/// deeper ancestors are explored before shallower siblings already queued.
/// No linearization (C3 or otherwise) and no de-duplication - an object
/// reachable through two different parent paths is visited, and can match,
/// twice. An object number missing from `graph.Objects` (an invalid object,
/// matching `dbpriv_find_object` returning null) is silently skipped, same
/// as the C `if (!o) continue;`.
let findCallableVerb (graph: Graph) (start: ObjRef) (verbName: string) : (ObjRef * VerbNode) option =
    match findOwnVerb graph start verbName with
    | Some v -> Some(start, v)
    | None ->
        let parentsOf obj =
            graph.Objects
            |> Map.tryFind obj
            |> Option.map (fun n -> n.Parents)
            |> Option.defaultValue []

        let mutable stack = parentsOf start
        let mutable result = None

        while result.IsNone && not stack.IsEmpty do
            let candidate = List.head stack
            stack <- List.tail stack

            match findOwnVerb graph candidate verbName with
            | Some v -> result <- Some(candidate, v)
            | None -> stack <- parentsOf candidate @ stack

        result

/// Every executable verb's primary name reachable from `start` via the
/// full ancestor closure (self + all transitive parents) - for completion
/// lists, not dispatch: unlike `findCallableVerb`, this collects the whole
/// reachable set instead of stopping at the first match, and de-duplicates
/// visited objects (a diamond-inherited ancestor is only visited once here
/// - a completion list only wants the set of possible names, not a
/// dispatch-order-faithful walk, so the "no de-duplication" rule that
/// matters for `findCallableVerb`'s correctness doesn't apply).
let allCallableVerbNames (graph: Graph) (start: ObjRef) : string list =
    let visited = System.Collections.Generic.HashSet<ObjRef>()
    let names = System.Collections.Generic.HashSet<string>()
    let mutable stack = [ start ]

    while not stack.IsEmpty do
        let obj = List.head stack
        stack <- List.tail stack

        if visited.Add obj then
            match Map.tryFind obj graph.Objects with
            | None -> ()
            | Some node ->
                for v in node.Verbs do
                    if isExecutable v.Meta then
                        match v.Meta.Names with
                        | primary :: _ -> names.Add primary |> ignore
                        | [] -> ()

                stack <- node.Parents @ stack

    names |> List.ofSeq |> List.sort

/// Statically resolves a `VerbCall`'s receiver expression to a starting
/// object, for the shapes that don't need a live `this`/`player`/`caller`
/// binding to mean anything:
///   - a literal object (`#123`)
///   - `$name` (parses as `Prop(ObjLit 0L, StrLit name, _, _)`) - looked up
///     against `SystemObjectProperties`, the real `#0` registry, ASCII
///     case-folded on both sides to match MOO property-name lookup
///     semantics (`moocode-reference.md`'s case-insensitivity note).
/// Anything else (`this:foo()`, `player:foo()`, a computed `obj:(expr)()`
/// receiver, a local variable) returns `None` - genuinely unresolvable
/// without running the program, matching the plan doc's Known Hazards
/// framing for this exact situation.
let resolveReceiver (graph: Graph) (receiver: Expr) : ObjRef option =
    match receiver with
    | ObjLit n -> Some n
    | Prop(ObjLit 0L, StrLit name, _, _) ->
        let folded = name |> String.map foldChar

        graph.SystemObjectProperties
        |> Map.tryPick (fun k v -> if (k |> String.map foldChar) = folded then Some v else None)
    | _ -> None

/// Like `resolveReceiver`, but additionally resolves a bare `this` receiver
/// to `currentObj` - the object the verb containing the call is defined on.
/// Sound because of how MOO dispatch actually binds `this`: `execute.cc`'s
/// `call_verb2` passes the same object both to `db_find_callable_verb` (to
/// find the verb) and as `_this` (bound to the `this` variable), so a verb
/// found via inheritance still runs with `this` equal to the actual call
/// receiver, not the object whose verb list holds the source. Assuming that
/// receiver equals the verb's own defining object (rather than some
/// descendant that inherited it unmodified) is the same static-analysis
/// assumption any IDE makes for a `self.method()`/`this.method()` call:
/// correct for the overwhelmingly common case, and this still finds the
/// real definition via `findCallableVerb`'s own ancestor walk even when
/// `currentObj` itself doesn't define the verb but a parent does. `player`/
/// computed receivers remain genuinely unresolvable - there's no equivalent
/// static default for those, so they still fall through to `resolveReceiver`
/// (and its `None`).
let resolveReceiverInContext (graph: Graph) (currentObj: ObjRef) (receiver: Expr) : ObjRef option =
    match receiver with
    | Ident("this", _, _) -> Some currentObj
    | _ -> resolveReceiver graph receiver

/// Every object in the whole graph that directly defines an executable
/// verb matching `verbName` - the best-effort fallback for hovering a
/// verb call whose receiver isn't statically resolvable (`this:foo()`,
/// `who:tell()`). Not dispatch (no notion of "starting object" to walk an
/// ancestor chain from at all here), just "which objects even have a verb
/// this call *could* land on" - genuinely ambiguous when there's more than
/// one, and callers should present it that way rather than picking one.
let findAllDefiningObjects (graph: Graph) (verbName: string) : (ObjRef * VerbNode) list =
    graph.Objects
    |> Map.toSeq
    |> Seq.collect (fun (num, o) -> o.Verbs |> Seq.map (fun v -> num, v))
    |> Seq.filter (fun (_, v) -> isExecutable v.Meta && verbNameMatchesAny v.Meta.Names verbName)
    |> List.ofSeq
