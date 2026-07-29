/// Phase 2 of moo-vcs-plan.md: reads a tree (`TreeParser`) and applies it to
/// a target instance. Diff-driven and idempotent - only issues MOO builtin
/// calls for what actually differs from the target's current state (read
/// via `Exporter.getObjectExport`, the same reader Phase 1 already built).
/// Two-pass compile-checked before any real mutation; dry-run by default
/// (see `Program.fs`'s `import` subcommand).
module Sidecar.Importer

open System.Threading
open System.Threading.Tasks
open Sidecar.Exporter
open Sidecar.TreeParser

// ---------------------------------------------------------------------------
// Plan types. `PropertyOp`/`VerbOp` are per-object and fully resolvable at
// plan time (they never depend on an object this same run hasn't created
// yet). Parentage can - a parent may be a sibling corponym also being
// created in this run, whose objnum doesn't exist until after the create
// step runs - so parents are deliberately NOT diffed/resolved here; that
// happens in `applyPlan` once the target's corponym map has been refreshed
// post-create.
// ---------------------------------------------------------------------------

type PropertyOp =
    | AddProperty of PropertyExport
    | DeleteProperty of name: string
    | UpdatePropertyInfo of name: string * owner: int64 * perms: string
    | UpdatePropertyValue of name: string * valueLiteral: string

type VerbOp =
    | AddVerb of VerbExport
    | DeleteVerb of names: string
    | UpdateVerbInfo of names: string * owner: int64 * perms: string
    | UpdateVerbArgs of names: string * dobj: string * prep: string * iobj: string
    | UpdateVerbCode of names: string * code: string list

/// Best-effort *preview* of the parents change, for dry-run display only -
/// `applyPlan` always recomputes this for real once every create in the run
/// has happened and the corponym map is refreshed, since a parent can be a
/// sibling corponym this same run is about to create (unresolvable here).
type ParentsPreview =
    | ParentsUnchanged
    | ParentsWillChange of desired: int64 list
    | ParentsUnresolvableAtPlanTime

type ObjectPlan =
    { Corponym: string
      /// `true` if this corponym has no backing object on the target yet.
      NeedsCreate: bool
      DesiredParents: ParentRef list
      ParentsPreview: ParentsPreview
      PropertyOps: PropertyOp list
      /// `Some desiredOrder` when the verb set or declaration order changed
      /// at all - applied as delete-everything-current, then add-everything
      /// in `desiredOrder` (with code). `None` means the verb set and order
      /// are unchanged; `VerbOps` then carries only info/args/code updates
      /// for the matched-by-name-spec verbs.
      VerbReorder: VerbExport list option
      VerbOps: VerbOp list }

type Plan = { Objects: ObjectPlan list }

let private emptyObject: ObjectExport =
    { Parents = []
      Owner = 0L
      Flags = []
      Properties = []
      Verbs = []
      LiveName = ""
      Aliases = [] }

/// Pure diff for one object - no MOO connection needed, fully unit-testable.
/// `current = None` means the corponym has no object on the target yet.
/// `resolveParentForPreview` looks up a corponym's *current* (pre-this-run)
/// objnum on the target for display purposes only; `None` means it doesn't
/// exist there yet (this run's about to create it), which makes the parents
/// diff unresolvable until apply time.
let planObject
    (corponym: string)
    (desired: ParsedObject)
    (current: ObjectExport option)
    (resolveParentForPreview: ParentRef -> int64 option)
    : ObjectPlan =
    let currentOrEmpty = defaultArg current emptyObject

    let parentsPreview =
        let resolved = desired.Parents |> List.map resolveParentForPreview

        if resolved |> List.exists Option.isNone then
            ParentsUnresolvableAtPlanTime
        else
            let resolvedList = resolved |> List.map Option.get

            if resolvedList = currentOrEmpty.Parents then
                ParentsUnchanged
            else
                ParentsWillChange resolvedList

    let currentPropsByName = currentOrEmpty.Properties |> List.map (fun p -> p.Name, p) |> Map.ofList
    let desiredPropsByName = desired.Properties |> List.map (fun p -> p.Name, p) |> Map.ofList

    let propertyOps =
        [ for KeyValue(name, dp) in desiredPropsByName do
              match Map.tryFind name currentPropsByName with
              | None -> yield AddProperty dp
              | Some cp ->
                  if cp.Owner <> dp.Owner || cp.Perms <> dp.Perms then
                      yield UpdatePropertyInfo(name, dp.Owner, dp.Perms)

                  if cp.ValueLiteral <> dp.ValueLiteral then
                      yield UpdatePropertyValue(name, dp.ValueLiteral)
          for KeyValue(name, _) in currentPropsByName do
              if not (Map.containsKey name desiredPropsByName) then
                  yield DeleteProperty name ]

    let currentVerbsByNames = currentOrEmpty.Verbs |> List.map (fun v -> v.Names, v) |> Map.ofList
    let desiredVerbsByNames = desired.Verbs |> List.map (fun v -> v.Names, v) |> Map.ofList

    let currentNameSet = currentVerbsByNames |> Map.toList |> List.map fst |> Set.ofList
    let desiredNameSet = desiredVerbsByNames |> Map.toList |> List.map fst |> Set.ofList
    let setChanged = currentNameSet <> desiredNameSet

    // Order of the verbs that survive (present in both current and
    // desired), taken from current's own declaration order, compared
    // against desired's declaration order restricted to the same set. If
    // these differ, the surviving verbs would dispatch in a different
    // sequence than the source DB - not just a cosmetic reshuffle.
    let survivingCurrentOrder =
        currentOrEmpty.Verbs |> List.map (fun v -> v.Names) |> List.filter desiredNameSet.Contains

    let desiredOrderRestrictedToSurviving =
        desired.Verbs |> List.map (fun v -> v.Names) |> List.filter currentNameSet.Contains

    let orderChanged = survivingCurrentOrder <> desiredOrderRestrictedToSurviving

    let verbReorder, verbOps =
        if setChanged || orderChanged then
            Some desired.Verbs, []
        else
            None,
            [ for KeyValue(names, dv) in desiredVerbsByNames do
                  let cv = currentVerbsByNames.[names] // set unchanged, so this always exists

                  if cv.Owner <> dv.Owner || cv.Perms <> dv.Perms then
                      yield UpdateVerbInfo(names, dv.Owner, dv.Perms)

                  if cv.Dobj <> dv.Dobj || cv.Prep <> dv.Prep || cv.Iobj <> dv.Iobj then
                      yield UpdateVerbArgs(names, dv.Dobj, dv.Prep, dv.Iobj)

                  if cv.Code <> dv.Code then
                      yield UpdateVerbCode(names, dv.Code) ]

    { Corponym = corponym
      NeedsCreate = current.IsNone
      DesiredParents = desired.Parents
      ParentsPreview = parentsPreview
      PropertyOps = propertyOps
      VerbReorder = verbReorder
      VerbOps = verbOps }

/// Builds the full plan: reads the target's current corponyms and, for each
/// one the tree also has, its current object state, then diffs every
/// corponym in the tree against that. Corponyms present only on the target
/// (not in the tree) are left untouched - removing a corponym/recycling an
/// object is explicitly out of scope this phase (matches the main plan's
/// deferred-scope list).
let planImport
    (conn: MooEval.Connection)
    (treeCorponyms: (string * int64) list)
    (treeObjects: Map<string, ParsedObject>)
    (ct: CancellationToken)
    : Task<Plan> =
    task {
        let! currentCorponymsByObjnum = getCorponyms (MooEval.runAndAwaitJson conn) ct

        let currentCorponymByName =
            currentCorponymsByObjnum |> Map.toList |> List.map (fun (n, name) -> name, n) |> Map.ofList

        let resolveParentForPreview (r: ParentRef) : int64 option =
            match r with
            | ByObjnum n -> Some n
            | ByCorponym name -> Map.tryFind name currentCorponymByName

        let objectPlans = ResizeArray<ObjectPlan>()

        for name, _ in treeCorponyms do
            match Map.tryFind name treeObjects with
            | None -> () // corponym listed but no object.moo on disk - nothing to plan
            | Some desired ->
                let! current =
                    match Map.tryFind name currentCorponymByName with
                    | Some objRef -> getObjectExport (MooEval.runAndAwaitJson conn) objRef ct
                    | None -> task { return None }

                objectPlans.Add(planObject name desired current resolveParentForPreview)

        return { Objects = List.ofSeq objectPlans }
    }

// ---------------------------------------------------------------------------
// Two-pass compile check. Runs before any real mutation - see the Phase 2
// plan's decision #4.
// ---------------------------------------------------------------------------

/// Every verb whose code this plan will write (adds, code updates, and
/// every verb in a reorder, since reorder deletes-and-recreates all of
/// them) - collected up front so the whole set can be compile-checked
/// before touching anything real.
let private codeToCheck (plan: Plan) : (string * string list) list =
    [ for op in plan.Objects do
          match op.VerbReorder with
          | Some verbs -> for v in verbs -> op.Corponym, v.Code
          | None ->
              for verbOp in op.VerbOps do
                  match verbOp with
                  | AddVerb v -> yield op.Corponym, v.Code
                  | UpdateVerbCode(_, code) -> yield op.Corponym, code
                  | _ -> () ]

let private mooLiteralList (lines: string list) : string =
    "{" + (lines |> List.map (fun l -> "\"" + l.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") |> String.concat ", ") + "}"

let private mooLiteralString (s: string) : string = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Runs a statement sequence and discards its (`1`) result, properly
/// awaited - not `Task.map`/`ignore` on the unawaited task itself, which
/// would fire the command without ever confirming it actually ran (a real
/// bug an earlier draft of this file had, including inside a `finally`
/// cleanup, where an un-awaited "recycle the scratch object" call could
/// silently race the rest of the function returning).
let private runIgnore (conn: MooEval.Connection) (statements: string) (ct: CancellationToken) : Task<unit> =
    task {
        let! _ = MooEval.runAndAwaitJson conn statements "1" ct
        return ()
    }

/// Adds each candidate verb's code to one shared scratch object, checks
/// `set_verb_code()`'s error list, deletes the verb immediately - so a
/// syntax error is caught with a full report before a single live verb is
/// touched. Returns every failure found (not just the first), or `Ok ()`.
let compileCheck
    (conn: MooEval.Connection)
    (plan: Plan)
    (ct: CancellationToken)
    : Task<Result<unit, (string * string) list>> =
    task {
        let candidates = codeToCheck plan

        if candidates.IsEmpty then
            return Ok()
        else
            let! scratchJson = MooEval.evalExpr conn "create(#-1, player)" ct
            let scratchObj = int64 (scratchJson.RootElement.GetString().TrimStart('#'))
            let o = sprintf "#%d" scratchObj
            let errors = ResizeArray<string * string>()

            try
                for corponym, code in candidates do
                    let statements =
                        $"""add_verb({o}, {{player, "rxd", "scratch_check"}}, {{"any", "any", "any"}}); errs = set_verb_code({o}, "scratch_check", {mooLiteralList code}); delete_verb({o}, "scratch_check");"""

                    let! result = MooEval.runAndAwaitJson conn statements "errs" ct
                    let errList = result.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq

                    if not errList.IsEmpty then
                        errors.Add(corponym, String.concat "; " errList)
            finally
                (task { do! runIgnore conn $"recycle({o});" ct }).GetAwaiter().GetResult()

            if errors.Count = 0 then return Ok() else return Error(List.ofSeq errors)
    }

// ---------------------------------------------------------------------------
// Apply. Only called when the caller has decided to actually write
// (`--apply`, not a dry run) - and only after `compileCheck` has returned
// `Ok`.
// ---------------------------------------------------------------------------

/// Applies one already-compile-checked plan. Creates first (so every
/// corponym this run introduces has an objnum before anything references
/// it), then re-reads the corponym map to resolve parents (a parent may be
/// a sibling corponym this same run just created), then properties, then
/// verbs.
let applyPlan (conn: MooEval.Connection) (plan: Plan) (ct: CancellationToken) : Task<unit> =
    task {
        for op in plan.Objects do
            if op.NeedsCreate then
                let! created = MooEval.evalExpr conn "create(#-1, player)" ct
                let newObj = created.RootElement.GetString().TrimStart('#')

                // add_property fails E_INVARG if the name is already
                // defined on #0 (confirmed in property.cc:bf_add_prop) -
                // that's the "corponym already exists but pointed at a
                // stale/invalid object" case, so just repoint it instead.
                let statements =
                    $"""`add_property(#0, "{op.Corponym}", #{newObj}, {{player, "r"}}) ! E_INVARG => (#0.{op.Corponym} = #{newObj})';"""

                do! runIgnore conn statements ct

        let! refreshedCorponyms = getCorponyms (MooEval.runAndAwaitJson conn) ct

        let corponymByName =
            refreshedCorponyms |> Map.toList |> List.map (fun (n, name) -> name, n) |> Map.ofList

        let resolveParent (r: ParentRef) : int64 =
            match r with
            | ByObjnum n -> n
            | ByCorponym name ->
                match Map.tryFind name corponymByName with
                | Some n -> n
                | None -> failwithf "Cannot resolve parent corponym '$%s' on the target - it has no corponym there" name

        for op in plan.Objects do
            let objRef = corponymByName.[op.Corponym]
            let o = sprintf "#%d" objRef

            let desiredParents = op.DesiredParents |> List.map resolveParent
            let parentsLiteral = "{" + (desiredParents |> List.map (sprintf "#%d") |> String.concat ", ") + "}"

            let parentStatements =
                $"""cur = parents({o}); des = {parentsLiteral}; if (cur != des) chparents({o}, des); endif"""

            do! runIgnore conn parentStatements ct

            for propOp in op.PropertyOps do
                let statements =
                    match propOp with
                    | AddProperty p ->
                        // eval()'s second return element IS the value
                        // directly on success, not a singleton list -
                        // confirmed live (`{ok, val} = eval("return 10+5;")`
                        // gives val=15, not val={15}). Indexing val[1] here
                        // was a real bug: for any non-indexable value
                        // (an int, a string...) it raises E_TYPE, which
                        // fails the *entire* submitted command silently
                        // (ToastCore's `;` eval-command convention reports
                        // the failure via notify() of error lines, never
                        // reaching this command's own sentinel notify()),
                        // hanging the client forever waiting for a response
                        // that will never arrive.
                        $"""add_property({o}, "{p.Name}", 0, {{player, "{p.Perms}"}}); {{ok, val}} = eval("return " + {mooLiteralString p.ValueLiteral} + ";"); if (ok) {o}.{p.Name} = val; endif"""
                    | DeleteProperty name -> $"""delete_property({o}, "{name}");"""
                    | UpdatePropertyInfo(name, owner, perms) -> $"""set_property_info({o}, "{name}", {{#{owner}, "{perms}"}});"""
                    | UpdatePropertyValue(name, literal) ->
                        $"""{{ok, val}} = eval("return " + {mooLiteralString literal} + ";"); if (ok) {o}.{name} = val; endif"""

                do! runIgnore conn statements ct

            match op.VerbReorder with
            | Some desiredVerbs ->
                do! runIgnore conn $"""for vn in (verbs({o})) delete_verb({o}, vn); endfor""" ct

                for v in desiredVerbs do
                    let firstAlias = v.Names.Split(' ').[0].Replace("*", "")

                    let statements =
                        $"""add_verb({o}, {{#{v.Owner}, "{v.Perms}", "{v.Names}"}}, {{"{v.Dobj}", "{v.Prep}", "{v.Iobj}"}}); set_verb_code({o}, "{firstAlias}", {mooLiteralList v.Code});"""

                    do! runIgnore conn statements ct
            | None ->
                for verbOp in op.VerbOps do
                    let statements =
                        match verbOp with
                        | AddVerb v ->
                            let firstAlias = v.Names.Split(' ').[0].Replace("*", "")

                            $"""add_verb({o}, {{#{v.Owner}, "{v.Perms}", "{v.Names}"}}, {{"{v.Dobj}", "{v.Prep}", "{v.Iobj}"}}); set_verb_code({o}, "{firstAlias}", {mooLiteralList v.Code});"""
                        | DeleteVerb names -> $"""delete_verb({o}, "{names.Split(' ').[0]}");"""
                        | UpdateVerbInfo(names, owner, perms) ->
                            $"""set_verb_info({o}, "{names.Split(' ').[0]}", {{#{owner}, "{perms}", "{names}"}});"""
                        | UpdateVerbArgs(names, dobj, prep, iobj) ->
                            $"""set_verb_args({o}, "{names.Split(' ').[0]}", {{"{dobj}", "{prep}", "{iobj}"}});"""
                        | UpdateVerbCode(names, code) -> $"""set_verb_code({o}, "{names.Split(' ').[0]}", {mooLiteralList code});"""

                    do! runIgnore conn statements ct
    }

// ---------------------------------------------------------------------------
// Human-readable plan summary - the dry-run output, per the Phase 2 plan's
// decision #5 (dry-run by default, `--apply` opts into writing).
// ---------------------------------------------------------------------------

let describePlan (plan: Plan) : string =
    let describeParent (r: ParentRef) =
        match r with
        | ByCorponym name -> "$" + name
        | ByObjnum n -> sprintf "#%d" n

    let describeProp op =
        match op with
        | AddProperty p -> sprintf "  + property %s" p.Name
        | DeleteProperty name -> sprintf "  - property %s" name
        | UpdatePropertyInfo(name, owner, perms) -> sprintf "  ~ property %s info (owner=#%d perms=%s)" name owner perms
        | UpdatePropertyValue(name, _) -> sprintf "  ~ property %s value" name

    let describeVerb op =
        match op with
        | AddVerb v -> sprintf "  + verb %s" v.Names
        | DeleteVerb names -> sprintf "  - verb %s" names
        | UpdateVerbInfo(names, owner, perms) -> sprintf "  ~ verb %s info (owner=#%d perms=%s)" names owner perms
        | UpdateVerbArgs(names, dobj, prep, iobj) -> sprintf "  ~ verb %s args (%s %s %s)" names dobj prep iobj
        | UpdateVerbCode(names, _) -> sprintf "  ~ verb %s code" names

    let lines =
        [ for op in plan.Objects do
              let hasChanges =
                  op.NeedsCreate
                  || op.ParentsPreview <> ParentsUnchanged
                  || not op.PropertyOps.IsEmpty
                  || op.VerbReorder.IsSome
                  || not op.VerbOps.IsEmpty

              if hasChanges then
                  yield sprintf "$%s%s" op.Corponym (if op.NeedsCreate then " (new object)" else "")

                  match op.ParentsPreview with
                  | ParentsUnchanged -> ()
                  | ParentsWillChange desired ->
                      yield sprintf "  ~ parents -> %s" (desired |> List.map (sprintf "#%d") |> String.concat " ")
                  | ParentsUnresolvableAtPlanTime ->
                      yield
                          sprintf
                              "  ~ parents (references a corponym this run is also creating - exact change resolved at apply time): %s"
                              (op.DesiredParents |> List.map describeParent |> String.concat " ")

                  for p in op.PropertyOps do
                      yield describeProp p

                  match op.VerbReorder with
                  | Some verbs -> yield sprintf "  ~ verbs reordered/changed (%d verbs, full replace)" verbs.Length
                  | None -> for v in op.VerbOps -> describeVerb v ]

    if lines.IsEmpty then "No changes." else String.concat "\n" lines
