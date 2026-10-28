// JSONata expressions inside a mapping (optional, .NET only): Jsonata.register, compute, ensure, validate.
//
//   dotnet fsi examples/09-jsonata.fsx
//
// Guide: docs/how-to/use-jsonata.md. Runtimes disagree on some JSONata behaviour; see docs/jsonata.md.

#load "common.fsx"
open Common
open Fnode
open Fergi
open Fergi.Jsonata

/// Run a built mapping over data/billing.yaml (as "service") and print it.
let runService (built: Result<Mapping, Error list>) =
    let f = files { input "service" (data "billing.yaml"); output "out.yaml" }
    match built |> Result.map Jsonata.register with     // register adds the `jsonata` function
    | Error errs -> for e in errs do printfn "mapping error %s" (describeError e)
    | Ok m -> Fergi.run m f load |> Result.map (fun out -> out.[f.Output]) |> showResult showYaml

// ---- compute with a jsonata expression ---------------------------------------------------------
// The first argument is the expression (a literal string). The second is the context, `$`. Every argument
// after the expression is also available as `$args`, and the third is bound to `$doc`.
heading "A computed string, a number and a list"

mapping {
    inputs [ "service" ]
    compute "/fullName" "jsonata" [ Lit(Str "first & ' ' & last"); Ptr "service:" ]
    compute "/replicasTimesTwo" "jsonata" [ Lit(Str "replicas * 2"); Ptr "service:" ]
    compute "/portNumbers" "jsonata" [ Lit(Str "ports.port[]"); Ptr "service:" ]   // [] keeps a one-item list a list
    // The third argument is bound to $doc: here the document built so far.
    compute "/summary" "jsonata" [ Lit(Str "$doc.fullName & ' runs ' & $string(name)"); Ptr "service:"; Ptr "" ]
}
|> runService

// ---- An expression that matches nothing is an error --------------------------------------------
// JSONata returns "undefined" for a path that is not there. Fergi treats that like a missing source in
// `copy`, so a typo does not silently produce nothing.
heading "A typo in an expression"

mapping {
    inputs [ "service" ]
    compute "/x" "jsonata" [ Lit(Str "nmae"); Ptr "service:" ]
}
|> runService

// ---- Validate the syntax before running --------------------------------------------------------
// Jsonata.validate is Mapping.validate plus a syntax check of every literal expression.
heading "Jsonata.validate"

let badSyntax =
    mapping {
        inputs [ "service" ]
        compute "/x" "jsonata" [ Lit(Str "first &"); Ptr "service:" ]
    }

match badSyntax with
| Ok m -> for e in Jsonata.validate (Jsonata.register m) do printfn "problem %s" (describeError e)
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- A jsonata ensure, which a mapping file can hold -------------------------------------------
// A closure ensure cannot be written to a file, but a JSONata predicate over the output can: it is just
// text. data/mappings/deploy.yaml has one (`metadata.name != null`), and the same entry in code is
// `Jsonata "..."`. The predicate must return a boolean.
heading "A JSONata ensure that passes and one that fails"

let withEnsure (predicate: string) =
    mapping { inputs [ "service" ]; layer [ "service" ] }
    |> Result.map (fun m -> { m with Ensure = [ ("check", Jsonata predicate) ] })

withEnsure "name = 'billing'" |> runService
withEnsure "owner != null" |> runService

heading "The ensure entries of the mapping file"

load (data "mappings/deploy.yaml")
|> Result.bind Mapping.ofParsed
|> Result.map (fun m -> m.Ensure |> List.map (fun (name, check) -> name, check))
|> printfn "%A"

// ---- Calling the evaluator directly ------------------------------------------------------------
heading "Jsonata.eval"

let doc = loadNode (data "billing.yaml")
printfn "%A" (Jsonata.eval "$sum(ports.port)" doc [])
printfn "%A" (Jsonata.eval "$uppercase(name)" doc [])
printfn "%A" (Jsonata.eval "ports[name = 'http'].port" doc [])
