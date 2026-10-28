// Building one document from many files: inputList, each, entry and sortBy.
//
//   dotnet fsi examples/05-aggregate-files.fsx
//
// Guide: docs/how-to/aggregate-files.md

#load "common.fsx"
open System.IO
open Common
open Fnode
open Fergi

// An inputList stands for many files. `each` builds one entry per file and sets the list at a path.
// `r` qualifies a pointer with the input name, so each entry reads from the file being processed.
// `entry` builds the rules for one entry; its rules are copy, rename, defaultTo, set, drop and compute.
let catalog =
    mapping {
        inputList "services"
        set "/generated-by" (Str "fergi")
        each "services" "/services" (fun r ->
            entry {
                copy (r "/name") "/name"
                copy (r "/owner") "/owner"
                set "/kind" (Str "service")
                compute "/title" "upper" [ Ptr(r "/name") ]
            })
        sortBy "/name"                                  // orders the entries of the `each` just above
    }

// The files come from the services folder. A directory listing is the caller's job; Fergi gets the names.
let serviceFiles =
    Directory.GetFiles(data "services", "*.yaml")
    |> Array.sort                                       // file order is kept, so make it deterministic
    |> List.ofArray

let f =
    files {
        inputList "services" serviceFiles
        output "catalog.yaml"
    }

heading "One entry per file, sorted by name"

match catalog with
| Ok m -> Fergi.run m f load |> Result.map (fun out -> out.[f.Output]) |> showResult showYaml
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- Without sortBy the entries keep the order of the files ------------------------------------
heading "Reverse the files, no sortBy: the entries follow the files"

let unsorted =
    mapping {
        inputList "services"
        each "services" "/services" (fun r -> entry { copy (r "/name") "/name" })
    }

let reversed = files { inputList "services" (List.rev serviceFiles); output "catalog.yaml" }

match unsorted with
| Ok m -> Fergi.run m reversed load |> Result.map (fun out -> out.[reversed.Output]) |> showResult showYaml
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- Validate without running ------------------------------------------------------------------
// Mapping.validate checks a mapping alone: no files, no loader. It finds an undeclared input, an input
// list used outside `each`, a second `layer`, an unknown function. Every problem is returned.
heading "Mapping.validate"

let suspicious =
    mapping {
        inputList "services"
        inputs [ "org" ]
        layer [ "org"; "services" ]                     // services is a list, so it cannot be layered
        compute "/x" "no-such-function" [ Lit(Str "a") ]
    }

match suspicious with
| Ok m -> for e in Mapping.validate m do printfn "problem %s" (describeError e)
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- A sort key must be there ------------------------------------------------------------------
// sortBy needs the field in every entry, with one type throughout. Every problem is reported, one per file.
heading "A sort key that no entry has"

let badSort =
    mapping {
        inputList "services"
        each "services" "/services" (fun r -> entry { copy (r "/name") "/name"; copy (r "/owner") "/owner" })
        sortBy "/rank"
    }

match badSort with
| Ok m -> Fergi.run m f load |> showResult ignore
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)
