/// Sidecar-side implementation of the two live queries the LSP asks over
/// `/lsp-bridge` (see `LspBridge.fs` for the WS transport/dispatch loop
/// these plug into): `get-builtins` (wraps `Exporter.getBuiltinFunctions` -
/// the same live query `builtins.json`'s export step already uses) and
/// `resolve-verb-dispatch` (a live equivalent of
/// `Metadata.Resolver.findCallableVerb` - see that module's own doc comment
/// for the exact ported algorithm this mirrors). Both run over the
/// `/lsp-bridge` connection's own `Session` - always the dedicated LSP
/// service character (see MOOdy's CLAUDE.md bootstrap docs), never a
/// browser tab's connection.
module Sidecar.LspBridgeActions

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Sidecar.BridgeHandler

/// Escapes a verb name for embedding in a MOO string literal - same
/// convention `IdeActions.fetchVerb`'s `verbLit` already uses.
let private mooStringLiteral (s: string) : string =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

let handleGetBuiltins (id: int) (session: Session) (ct: CancellationToken) : Task<string> =
    task {
        let evalRunner = evalOnSession session
        let! functions = Exporter.getBuiltinFunctions evalRunner ct

        let payload =
            {| id = id
               functions =
                 functions
                 |> List.map (fun f -> {| name = f.Name; minargs = f.MinArgs; maxargs = f.MaxArgs; types = f.Types |})
                 |> List.ofSeq |}

        return JsonSerializer.Serialize(payload)
    }

/// Live equivalent of `Metadata.Resolver.findCallableVerb` - own verbs
/// first, then a stack-based DFS over `parents()`, front-pushed (not
/// appended), no de-duplication, first match wins - see that module's doc
/// comment for why this exact order/no-dedup shape matters. Seeding the
/// walk's stack with `{startObj}` itself (rather than special-casing "check
/// start, then walk parentsOf(start)" as two phases like the F# version
/// does) reproduces the identical order in one uniform loop: the first pop
/// is always `startObj`, checked before any parent ever gets pushed.
///
/// Verb-name matching (wildcards like `l*ook`) is NOT re-implemented here:
/// ToastStunt's own `verb_info(obj, "name")`/`verb_args(obj, "name")`
/// already resolve a string verb descriptor via `db_find_defined_verb`
/// (`ToastStunt/src/db_verbs.cc`), which calls the exact same `verbcasecmp`
/// real dispatch itself uses - delegating to the real builtin is both less
/// code and strictly more faithful than porting the wildcard matcher a
/// second time (`Resolver.fs`'s `matchesOnePattern` already IS that port,
/// for the static-graph path). `verb_info`/`verb_args` don't themselves
/// filter by the executable (`x`) bit the way real dispatch does, so that
/// check still happens here explicitly, matching `Resolver.fs`'s
/// `isExecutable`.
let handleResolveVerbDispatch
    (id: int)
    (session: Session)
    (startObj: int64)
    (verbName: string)
    (ct: CancellationToken)
    : Task<string> =
    task {
        let evalRunner = evalOnSession session
        let verbLit = mooStringLiteral verbName

        let statements =
            $"""found = 0; definer = #-1; def_names = ""; def_perms = ""; def_dobj = ""; def_prep = ""; def_iobj = ""; def_owner = #-1; def_ownername = ""; def_definername = ""; def_code = {{}};
stack = {{#{startObj}}};
while (!found && length(stack) > 0)
  candidate = stack[1];
  stack = listdelete(stack, 1);
  if (valid(candidate))
    hit = 0;
    try
      vi = verb_info(candidate, {verbLit});
      if (index(vi[2], "x"))
        hit = 1;
      endif
    except (E_VERBNF)
    endtry
    if (hit)
      found = 1;
      definer = candidate;
      def_names = vi[3];
      def_perms = vi[2];
      def_owner = vi[1];
      def_ownername = valid(vi[1]) ? (typeof(vi[1].name) == STR ? vi[1].name | "") | "";
      def_definername = typeof(candidate.name) == STR ? candidate.name | "";
      va = verb_args(candidate, {verbLit});
      def_dobj = va[1];
      def_prep = va[2];
      def_iobj = va[3];
      def_code = verb_code(candidate, {verbLit}, 0, 1);
    else
      stack = {{@parents(candidate), @stack}};
    endif
  endif
endwhile"""

        let resultExpr =
            """found ? ["found" -> 1, "definer" -> tostr(definer), "definername" -> def_definername, "names" -> def_names, "perms" -> def_perms, "dobj" -> def_dobj, "prep" -> def_prep, "iobj" -> def_iobj, "owner" -> tostr(def_owner), "ownername" -> def_ownername, "code" -> def_code] | ["found" -> 0]"""

        let! json = evalRunner statements resultExpr ct
        let root = json.RootElement

        if root.GetProperty("found").GetInt32() = 1 then
            let code =
                root.GetProperty("code").EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> List.ofSeq

            let payload =
                {| id = id
                   found = true
                   definer = int64 (root.GetProperty("definer").GetString().TrimStart('#'))
                   definerName = root.GetProperty("definername").GetString()
                   names = root.GetProperty("names").GetString()
                   perms = root.GetProperty("perms").GetString()
                   dobj = root.GetProperty("dobj").GetString()
                   prep = root.GetProperty("prep").GetString()
                   iobj = root.GetProperty("iobj").GetString()
                   owner = int64 (root.GetProperty("owner").GetString().TrimStart('#'))
                   ownerName = root.GetProperty("ownername").GetString()
                   code = code |}

            return JsonSerializer.Serialize(payload)
        else
            return JsonSerializer.Serialize({| id = id; found = false |})
    }
