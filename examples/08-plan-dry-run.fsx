// A dry run: Fergi.plan returns the result, the ops that turn the current output into it, and provenance.
//
//   dotnet fsi examples/08-plan-dry-run.fsx
//
// The CLI's `--dry-run` prints the same thing. Nothing is written by either.

#load "common.fsx"
open Common
open Fnode
open Fergi

let built =
    mapping {
        inputs [ "org"; "team"; "service" ]
        layer [ "org"; "team"; "service" ]
        listsBy "/ports" "name"
        copy "service:/name" "/metadata/name"
        defaultTo "/kind" (Str "service")
        drop "/internal"
    }

// The output name is also a file name here: plan loads it through the same loader to learn what is
// there today. An output that cannot be loaded (absent, or unparsable) counts as empty.
let f =
    files {
        input "org" (data "org.yaml")
        input "team" (data "team.yaml")
        input "service" (data "billing.yaml")
        output (data "current-output.yaml")
    }

// Patch.diff compares objects key by key. If the keys both documents share are not in the same order (or one
// has duplicate keys) it falls back to replacing the whole object with one `set`, so the sample output file
// keeps its keys in the order the mapping produces them.
let opText (op: Op) =
    match op with
    | Op.Set(p, v) -> sprintf "set    %s = %s" (Path.print p) ((Fnode.Json.format.Print v).Replace("\n", "").Replace("  ", ""))
    | Op.Remove p -> sprintf "remove %s" (Path.print p)
    | Op.Append(p, v) -> sprintf "append %s %A" (Path.print p) v
    | Op.Move(a, b) -> sprintf "move   %s -> %s" (Path.print a) (Path.print b)
    | Op.Test(p, _) -> sprintf "test   %s" (Path.print p)

heading "Changes against data/current-output.yaml"

match built with
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)
| Ok m ->
    match Fergi.plan m f load with
    | Error errs -> for e in errs do printfn "error %s" (describeError e)
    | Ok plan ->
        printfn "output: %s" (System.IO.Path.GetFileName plan.Output)
        for op in plan.Ops do printfn "  %s" (opText op)

        // Provenance: which layer each merged leaf came from. Later rules (copy, set, ...) are not tracked.
        heading "Where the merged values came from"
        for KeyValue(path, layer) in plan.Provenance |> Map.filter (fun p _ -> p <> "" && p.LastIndexOf '/' <= 0) do
            printfn "  %-12s %s" path layer

        heading "The result the run would write"
        showYaml plan.Result

// ---- No current output -------------------------------------------------------------------------
// With no file to compare against, every key is an addition.
heading "Against an output that does not exist yet"

let fresh = { f with Output = data "not-written-yet.yaml" }

match built |> Result.bind (fun m -> Fergi.plan m fresh load) with
| Ok plan ->
    match plan.Ops with
    | [ Op.Set([], _) ] -> printfn "one op: set the whole document"
    | ops -> printfn "%d ops" ops.Length
| Error errs -> for e in errs do printfn "error %s" (describeError e)
