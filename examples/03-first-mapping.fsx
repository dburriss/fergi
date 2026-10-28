// A first mapping: describe a transformation, bind names to files, run it, write the result yourself.
//
//   dotnet fsi examples/03-first-mapping.fsx
//
// Tutorial: docs/tutorials/first-mapping.md

#load "common.fsx"
open Common
open Fnode
open Fergi

// 1. Describe the transformation. Rules run top to bottom; nothing is read yet.
//    `mapping` returns Result<Mapping, Error list>: every bad pointer is reported together.
let built =
    mapping {
        inputs [ "org"; "service" ]                 // the names the rules refer to
        layer [ "org"; "service" ]                  // merge both documents, later wins
        copy "service:/name" "/metadata/name"       // read /name from the "service" input
        defaultTo "/kind" (Str "service")           // set /kind only if it is missing
    }

// 2. Bind the names to files and choose the output name. The strings are opaque to Fergi: they go to the
//    loader, and the output name is the key of the result.
let f =
    files {
        input "org" (data "org.yaml")
        input "service" (data "billing.yaml")
        output "deploy/billing.yaml"
    }

// 3. Run it. `load` (from common.fsx) reads a file and parses it by extension.
//    Fergi.run returns the output documents keyed by output name and writes nothing.
heading "Fergi.run"

match built with
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)
| Ok m ->
    match Fergi.run m f load with
    | Ok outputs ->
        for KeyValue(file, node) in outputs do
            printfn "# would write %s" file
            // Writing is yours: System.IO.File.WriteAllText(file, Fnode.Yaml.format.Print node)
            showYaml node
    | Error errs -> for e in errs do printfn "run error %s" (describeError e)

// ---- The same mapping over a JSON service file, printed as JSON --------------------------------
// The format is chosen by the loader, not by the mapping, so YAML and JSON inputs mix freely.
heading "A JSON input, JSON output"

let f2 =
    files {
        input "org" (data "org.yaml")
        input "service" (data "payments.json")
        output "deploy/payments.json"
    }

match built with
| Ok m -> Fergi.run m f2 load |> Result.map (fun out -> out.[f2.Output]) |> showResult showJson
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- Mistakes are values -----------------------------------------------------------------------
// A bad pointer in the mapping is reported when it is built. Both typos are listed, not just the first.
heading "Bad pointers are reported together"

let typo =
    mapping {
        inputs [ "org" ]
        copy "nobody:/name" "/a"        // there is no input called nobody
        copy "org:/name" "no-slash"     // a pointer must start with /
        set "/bad~escape" (Int 1L)      // ~ must be ~0 or ~1
    }

showResult ignore (typo |> Result.map ignore)

// A missing file is an error from the loader (common.fsx turns it into a path.notFound). Nothing is returned, not a half-built document.
heading "A missing input file"

let missing = files { input "org" (data "nope.yaml"); input "service" (data "billing.yaml"); output "out" }

match built with
| Ok m ->
    Fergi.run m missing load |> showResult ignore
| Error _ -> ()
