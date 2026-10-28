// Editing one document: Doc.get/set/remove, ops + Patch.apply, and Patch.diff.
//
//   dotnet fsi examples/01-edit-a-document.fsx
//
// Guide: docs/how-to/edit-a-document.md

#load "common.fsx"
open Common
open Fnode
open Fergi

let billing = loadNode (data "billing.yaml")

// ---- Read and write one value ------------------------------------------------------------------
// Doc.get, Doc.set and Doc.remove take a selector. [name=http] picks the list item whose name is http.
heading "Doc.get / Doc.set / Doc.remove"

printfn "name        = %A" (Doc.get (sel "/name") billing)
printfn "http port   = %A" (Doc.get (sel "/ports/[name=http]/port") billing)
printfn "missing key = %A" (Doc.get (sel "/nope") billing |> Result.mapError (fun e -> e.Code))

let edited =
    billing
    |> Doc.set (sel "/ports/[name=http]/port") (Int 80L)
    |> Result.bind (Doc.set (sel "/owner") (Str "finance"))
    |> Result.bind (Doc.remove (sel "/internal"))

edited |> Result.mapError List.singleton |> showResult showYaml

// ---- Build a plan with the `ops` CE ------------------------------------------------------------
// `ops` only builds an `Op list`: yield, if and for all work. Nothing runs until Patch.apply.
heading "ops + Patch.apply"

let isProd = true

let plan =
    ops {
        yield Op.Set(ptr "/owner", Str "energy")
        yield Op.Remove(ptr "/internal")
        yield Op.Move(ptr "/old", ptr "/previous")
        yield Op.Append(ptr "/ports", Obj [ "name", Str "metrics"; "port", Int 9090L ])
        if isProd then yield Op.Set(ptr "/replicas", Int 3L)
        // A Test op aborts the whole patch when the value is not what you expect.
        yield Op.Test(ptr "/name", Str "billing")
    }

printfn "%d ops" plan.Length
Patch.apply plan billing |> showResult showYaml

// ---- Patch.apply is atomic ---------------------------------------------------------------------
// Every failing op is reported and no document comes back, not a half-edited one.
heading "A failing patch reports every error and returns nothing"

let broken =
    ops {
        yield Op.Remove(ptr "/does/not/exist")
        yield Op.Test(ptr "/name", Str "somebody-else")
        yield Op.Set(ptr "/owner", Str "fine")        // a good op does not save the patch
    }

Patch.apply broken billing |> showResult showYaml

// ---- Diff --------------------------------------------------------------------------------------
// Patch.diff a b returns the ops that turn a into b. Arrays are compared whole.
heading "Patch.diff"

let payments = loadNode (data "payments.json")
for op in Patch.diff billing payments do
    printfn "%A" op

// Applying the diff gives the other document back.
let roundTrip = Patch.apply (Patch.diff billing payments) billing
printfn "diff then apply gives the target back: %b" (roundTrip = Ok payments)
