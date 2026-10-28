// Mapping files: write a mapping out, read one in, and see errors with their line and column.
//
//   dotnet fsi examples/07-mapping-files.fsx
//
// Guide: docs/how-to/use-mapping-files.md. The format is versioned (`version: 1`) and has a JSON Schema in
// docs/mapping.schema.json, so another program can generate a mapping for the CLI.

#load "common.fsx"
open Common
open Fnode
open Fergi
open Fergi.Jsonata

// ---- Write a mapping to a file form ------------------------------------------------------------
// Mapping.toNode gives a Node, which either adapter prints. Nothing is written to disk here.
heading "Mapping.toNode, printed as YAML"

let built =
    mapping {
        inputs [ "org"; "service" ]
        layer [ "org"; "service" ]
        listsBy "/ports" "name"
        copy "service:/name" "/metadata/name"
        defaultTo "/kind" (Str "service")
        compute "/contact" "join" [ Lit(Str " "); Ptr "service:/first"; Ptr "service:/last" ]
    }

match built |> Result.bind Mapping.toNode with
| Ok node ->
    showYaml node
    heading "The same node, printed as JSON"
    showJson node
| Error errs -> for e in errs do printfn "error %s" (describeError e)

// A hook or a host function is code, so it cannot be written to a file.
heading "A mapping with an ensure hook cannot be serialised"

mapping { inputs [ "org" ]; ensure "always" (fun _ -> Ok()) }
|> Result.bind Mapping.toNode
|> showResult ignore

// ---- Read a mapping from a file ----------------------------------------------------------------
// ofParsed reads the whole parse result, so errors carry the line and column of the file.
// Mapping.ofNode does the same from a Node and has no positions to report.
heading "Read data/mappings/deploy.yaml and run it"

let readMapping (file: string) : Result<Mapping, Error list> =
    load file |> Result.bind Mapping.ofParsed

match readMapping (data "mappings/deploy.yaml") with
| Error errs -> for e in errs do printfn "error %s" (describeError e)
| Ok m ->
    // A file knows its rules and `jsonata` ensure entries. Host functions and ensure hooks are code, so you
    // register them here. The file's ensure entry is a JSONata predicate, so register `jsonata` too.
    let m = Jsonata.register m
    let f =
        files {
            input "org" (data "org.yaml")
            input "service" (data "billing.yaml")
            output "deploy/billing.yaml"
        }
    Fergi.run m f load |> Result.map (fun out -> out.[f.Output]) |> showResult showYaml

// ---- Validate before running -------------------------------------------------------------------
heading "Mapping.validate on the file's mapping"

match readMapping (data "mappings/deploy.yaml") with
| Ok m -> printfn "%d problems" (Mapping.validate (Jsonata.register m)).Length
| Error errs -> for e in errs do printfn "error %s" (describeError e)

// ---- A broken file -----------------------------------------------------------------------------
// Every problem is reported, each with the place in the file it points at.
heading "Read data/mappings/broken.yaml"

readMapping (data "mappings/broken.yaml") |> showResult ignore

// ---- Round trip --------------------------------------------------------------------------------
heading "Round trip: ofNode (toNode m) gives the same mapping back"

match built with
| Ok m ->
    let again = Mapping.toNode m |> Result.bind Mapping.ofNode
    printfn "same rules: %b" (again |> Result.map (fun m2 -> m2.Rules = m.Rules) = Ok true)
| Error _ -> ()
