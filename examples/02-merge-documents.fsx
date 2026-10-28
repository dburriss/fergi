// Merging documents: Doc.merge, Doc.layers, merge policies and provenance.
//
//   dotnet fsi examples/02-merge-documents.fsx
//
// Guide: docs/how-to/merge-documents.md

#load "common.fsx"
open Common
open Fnode
open Fergi

let org = loadNode (data "org.yaml")
let team = loadNode (data "team.yaml")
let billing = loadNode (data "billing.yaml")
let payments = loadNode (data "payments.json")

let showConflicts (r: Result<_, Conflict list>) =
    match r with
    | Ok(node: Node) -> showYaml node
    | Error conflicts ->
        for c in conflicts do printfn "conflict %s" (describeError (Conflict.toError c))

// ---- Defaults ----------------------------------------------------------------------------------
// Policy.defaults: objects merge key by key, lists are replaced, the later document wins a clash, and a
// null in the later document deletes the key.
heading "Doc.merge with the defaults (lists replaced, last wins)"

Doc.merge Policy.defaults org team |> showConflicts

// ---- Merge a list item by item -----------------------------------------------------------------
// withListsBy merges the list at a path by a key field instead of replacing it: http is merged,
// metrics is kept from the org file, and grpc is added from the team file.
heading "Merge /ports by name"

let byName = Policy.defaults |> Policy.withListsBy (ptr "/ports") "name"
Doc.merge byName org team |> showConflicts

// ---- Conflict policies -------------------------------------------------------------------------
heading "FirstWins: the earlier document wins a clash"

Doc.merge (Policy.defaults |> Policy.withConflict OnConflict.FirstWins) org team |> showConflicts

// Fail reports every disagreement, not just the first. Nothing is thrown and no document comes back.
heading "Fail: every disagreement is reported"

Doc.merge (Policy.defaults |> Policy.withConflict OnConflict.Fail) org team |> showConflicts

// Resolve takes a function, so it only exists in code (a mapping file cannot say it). Here org and billing
// disagree on /replicas (2 and 4) and the function keeps the bigger number.
heading "Resolve: decide each clash yourself"

let biggerWins =
    OnConflict.Resolve(fun _path baseValue overlay ->
        match baseValue, overlay with
        | Int a, Int b -> Ok(Int(max a b))
        | _, later -> Ok later)

Doc.merge (Policy.defaults |> Policy.withConflict biggerWins) org billing |> showConflicts

// ---- Null handling -----------------------------------------------------------------------------
// payments.json has "labels": { "team": null }. By default that deletes the key from the merged result.
heading "null deletes a key (default)"

Doc.merge Policy.defaults org payments |> showConflicts

heading "null kept as a value when NullDeletes is off"

Doc.merge (Policy.defaults |> Policy.withNullDeletes false) org payments |> showConflicts

// ---- Layers and provenance ---------------------------------------------------------------------
// Doc.layers folds a named list in order (later wins) and also says which layer each leaf came from.
heading "Doc.layers with provenance"

match Doc.layers byName [ "org", org; "team", team; "service", billing ] with
| Ok(merged, provenance) ->
    showYaml merged
    printfn "\nwhere each value came from:"
    for KeyValue(path, layer) in provenance do
        printfn "  %-22s %s" path layer
| Error conflicts ->
    for c in conflicts do printfn "conflict %s" (describeError (Conflict.toError c))

// ---- Collect every error with `docs` -----------------------------------------------------------
// `let!` stops at the first error; `and!` runs the sources side by side and keeps the errors of all of them.
heading "docs CE: load two files and keep every error"

let both =
    docs {
        let! a = load (data "broken.yaml")
        and! b = load (data "broken.json")
        return a.Node, b.Node
    }

showResult ignore both
