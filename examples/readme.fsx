// The library snippets from the docs, run against the built libraries.
//
//   build first:  dotnet build Fergi.slnx -c Release
//   run:          dotnet fsi examples/readme.fsx
//
// The data is a toy, so some runs print an error on purpose. Each section says which.
// Every section shows the computation expression (CE) form; docs/reference/mapping.md shows the plain-data equivalent.

#r "../src/Fnode/bin/Release/net8.0/Fnode.dll"
#r "../src/Fergi/bin/Release/net8.0/Fergi.dll"

open Fnode
open Fergi

// ---- Helpers ----------------------------------------------------------------------------------
// Pointer strings parse to a Result. A script can just throw on a bad one.
// `sel` accepts selectors (a pointer that may contain [key=value]); `ptr` accepts plain JSON Pointers.
let sel s = Selector.parse s |> Result.defaultWith (fun e -> failwith e.Message)
let ptr s = Path.parse s |> Result.defaultWith (fun e -> failwith e.Message)

// ---- 1. Editing one document: ops + Patch.apply ----------------------------------------------
// `ops` only builds an `Op list`: yield, if and for all work. Nothing runs until Patch.apply.
let isProd = true

let plan =
    ops {
        yield Op.Set(ptr "/owner", Str "energy")
        yield Op.Remove(ptr "/draft")
        if isProd then yield Op.Set(ptr "/replicas", Int 3L)
        for k in [ "a"; "b" ] do
            yield Op.Remove(ptr ("/tmp/" + k))
    }

// Expected: Error [path.notFound; path.notFound]. The document has no /tmp/a or /tmp/b, and Patch.apply
// is atomic: it reports every failing op and returns no document at all.
printfn "ops: %A" (Patch.apply plan (Obj [ "owner", Str "x"; "draft", Bool true ]) |> Result.mapError (List.map (fun e -> e.Code)))

// ---- 2. Merging: Policy + Doc.layers ----------------------------------------------------------
// A Policy says how two documents combine. Start from the defaults and adjust it.
let policy =
    Policy.defaults                                  // lists replaced, last wins, null deletes a key
    |> Policy.withListsBy (ptr "/ports") "name"      // merge the /ports list item by item, on "name"
    |> Policy.withConflict OnConflict.LastWins
    |> Policy.withNullDeletes true

// Layers are folded in order (later wins) and return provenance (where each leaf came from).
// Conflicts come back as a list, never as an exception. Two empty documents cannot conflict.
match Doc.layers policy [ "org", Obj []; "service", Obj [] ] with
| Ok(_merged, _provenance) -> printfn "layers: ok"
| Error conflicts -> conflicts |> List.map Conflict.toError |> ignore

// ---- 3. A whole transformation: the `mapping` CE ----------------------------------------------
// Rules run top to bottom. `mapping` returns Result<Mapping, Error list>: every bad pointer in the
// block is reported together, so a typo does not hide the next one.
let built =
    mapping {
        inputs [ "org"; "service" ]                 // names, not files; `files` binds them below
        layer [ "org"; "service" ]                  // merge these two, later wins (at most one layer)
        listsBy "/ports" "name"                     // part of the merge policy
        conflict ConflictMode.LastWins              // or FirstWins, Fail
        copy "service:/name" "/metadata/name"       // reads the NAMED input, creates /metadata if absent
        defaultTo "/kind" (Str "service")           // only when /kind is absent
        set "/ports/[name=http]/port" (Int 80L)     // [name=http] picks the list item whose name is http
        rename "/old" "/new"
        drop "/internal"                            // dropping something absent is fine
        // Call a registered function by name. Ptr "input:/path" reads a named input, Ptr "/path"
        // reads the document built so far, Lit is a fixed value.
        compute "/fullName" "join" [ Lit(Str " "); Ptr "service:/first"; Ptr "/last" ]
        // A hook that checks the final result. Code only: it cannot be written to a mapping file.
        ensure "has name" (fun _doc -> Ok())
    }

printfn "mapping: %A" (built |> Result.map (fun _ -> "ok"))

// ---- 4. Where the files live: the `files` CE and running the mapping --------------------------
// The strings are opaque to Fergi. They go to the loader and the output name keys the result.
let f =
    files {
        input "org" "o.yaml"
        input "service" "s.yaml"
        output "out.yaml"
    }

// A loader maps a file name to a parsed document. `Loader.ofNodes` serves in-memory nodes, which is
// enough here. In real use, read the file and call `Fnode.Yaml.format.Parse` (or the JSON one).
let docsStore =
    Map.ofList
        [ "o.yaml", Obj [ "a", Int 1L ]
          "s.yaml", Obj [ "name", Str "x"; "first", Str "A"; "last", Str "B"; "old", Int 1L ] ]

// Expected: an error. The mapping sets /ports/[name=http]/port, but no /ports list exists to select
// from. Nothing is returned on error, not a half-built document. Remove that `set` rule and it succeeds.
match built with
| Ok m -> printfn "run: %A" (Fergi.run m f (Loader.ofNodes docsStore) |> Result.mapError (List.map (fun e -> e.Message)))
| Error _ -> ()

// ---- 5. Aggregation: inputList, each and entry ------------------------------------------------
// An inputList stands for many files. `each` builds one entry per file and sets the list at a path.
// `r` qualifies a pointer with the input name, so entries read from the file being processed.
let catalog =
    mapping {
        inputList "services"
        each "services" "/services" (fun r ->
            entry {
                copy (r "/name") "/name"
                set "/kind" (Str "service")
                compute "/title" "upper" [ Ptr(r "/name") ]
            })
        sortBy "/name"                              // orders the entries of the `each` just above
    }

printfn "aggregation: %A" (catalog |> Result.map (fun _ -> "ok"))

// ---- 6. Low level: the same ideas as plain data -----------------------------------------------
// No CE needed. `Rule` is plain data and Mapping.create takes the input names plus the rules.
let m =
    Mapping.create
        [ "org"; "service" ]
        [ Rule.Layer [ "org"; "service" ]
          Rule.Copy(("service", sel "/name"), sel "/metadata/name") ]
    |> Mapping.withInputLists [ "repos" ]
    |> Mapping.ensure "has name" (fun _doc -> Ok())

// runFiles is the one-call form: (name, file) pairs, an output name and rules, with no Files value.
// It takes only the rules, so the input lists and Ensure hooks of `m` are not used. Expected: Ok.
printfn "runFiles: %A" (Fergi.runFiles [ "org", "o.yaml"; "service", "s.yaml" ] "out.yaml" m.Rules (Loader.ofNodes docsStore) |> Result.mapError List.length)

// ---- 7. Whole-input pointer -------------------------------------------------------------------
// "person:" (no path) is the entire input document, not a key named "". "/" would mean the key "".
let p = mapping { inputs [ "person" ]; compute "/x" "concat" [ Ptr "person:" ] }

// Expected: an error. `concat` takes strings, ints and bools, and the whole input is an object.
// It shows that the pointer resolved to the document and that the failure is reported as a value.
match p with
| Ok m ->
    let one = files { input "person" "o.yaml"; output "z" }
    printfn "whole input: %A" (Fergi.run m one (Loader.ofNodes docsStore) |> Result.mapError (List.map (fun e -> e.Message)))
| Error _ -> failwith "the mapping itself should build"
