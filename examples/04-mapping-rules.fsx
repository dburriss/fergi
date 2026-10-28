// Every mapping rule, one at a time, plus functions and ensure hooks.
//
//   dotnet fsi examples/04-mapping-rules.fsx
//
// Reference: docs/reference/mapping.md

#load "common.fsx"
open Common
open Fnode
open Fergi

/// The file behind each input name used in this script.
let inputFiles = Map.ofList [ "org", data "org.yaml"; "team", data "team.yaml"; "service", data "billing.yaml" ]

/// Run a built mapping, binding just the inputs it declares, and print the result.
let runOver (built: Result<Mapping, Error list>) =
    match built with
    | Error errs -> for e in errs do printfn "mapping error %s" (describeError e)
    | Ok m ->
        let f = { Inputs = m.Inputs |> List.map (fun n -> n, inputFiles.[n]) |> Map.ofList
                  InputLists = Map.empty
                  Output = "out.yaml" }
        Fergi.run m f load |> Result.map (fun out -> out.[f.Output]) |> showResult showYaml

// ---- layer, listsBy, conflict, nullDeletes: the merge policy -----------------------------------
// The three policy rules apply to the layer wherever they sit in the block.
heading "layer + listsBy + conflict + nullDeletes"

mapping {
    inputs [ "org"; "team"; "service" ]
    layer [ "org"; "team"; "service" ]          // merged in this order, later wins
    listsBy "/ports" "name"                     // merge /ports item by item on "name"
    conflict ConflictMode.LastWins              // or FirstWins, Fail
    nullDeletes true                            // a null in a later layer deletes the key
}
|> runOver

// ---- copy: read from a NAMED input -------------------------------------------------------------
// copy reads from a named input, not from the document built so far. Missing parents are created.
heading "copy, set, defaultTo"

mapping {
    inputs [ "org"; "team"; "service" ]
    layer [ "org"; "team" ]
    copy "service:/name" "/metadata/name"       // /metadata did not exist; it is created
    set "/ports/[name=http]/port" (Int 80L)     // [name=http] picks the list item whose name is http
    set "/replicas" (Int 5L)                    // set overwrites
    defaultTo "/kind" (Str "service")           // only when /kind is absent
    defaultTo "/owner" (Str "nobody")           // /owner is there, so this does nothing
}
|> runOver

// ---- rename, drop ------------------------------------------------------------------------------
heading "rename, drop"

mapping {
    inputs [ "service" ]
    layer [ "service" ]
    rename "/old" "/previous"                   // move a value
    drop "/internal"                            // remove it
    drop "/never-existed"                       // dropping something absent is fine
}
|> runOver

// ---- compute: call a function by name ----------------------------------------------------------
// Ptr "input:/path" reads a named input. Ptr "/path" reads the document built so far.
// Ptr "input:" is the whole input document. Lit is a fixed value.
heading "compute with the built-in functions"

mapping {
    inputs [ "org"; "service" ]
    layer [ "org" ]
    compute "/fullName" "join" [ Lit(Str " "); Ptr "service:/first"; Ptr "service:/last" ]
    compute "/id" "concat" [ Lit(Str "svc-"); Ptr "service:/name" ]
    compute "/shout" "upper" [ Ptr "service:/name" ]
    compute "/quiet" "lower" [ Ptr "/owner" ]                                  // reads the document so far
    compute "/spaced" "trim" [ Lit(Str "   padded   ") ]
    compute "/replicaText" "toString" [ Ptr "service:/replicas" ]
    compute "/portNumber" "toInt" [ Lit(Str "8080") ]
    // coalesce takes the first value that is not null. (A pointer to a missing path is an error, not null.)
    compute "/contact" "coalesce" [ Lit Null; Lit(Str "unknown") ]
}
|> runOver

// ---- func: your own function -------------------------------------------------------------------
// A function is `Node list -> Result<Node, string>`. Register it with `func` and call it with compute.
// Only its name is stored in the mapping, so a mapping file can call it too when you register the same one.
heading "A custom function"

let slug : Function =
    fun args ->
        match args with
        | [ Str s ] -> Ok(Str(s.ToLowerInvariant().Replace(' ', '-')))
        | _ -> Error "slug expects one string"

mapping {
    inputs [ "service" ]
    func "slug" slug
    compute "/slug" "slug" [ Lit(Str "Billing And Invoicing") ]
}
|> runOver

// ---- ensure: check the final result ------------------------------------------------------------
// An ensure hook sees the finished document and returns Ok or errors. It is code, so it cannot be written
// to a mapping file (the JSONata `ensure` entry in 09-jsonata.fsx can).
heading "ensure: passing and failing"

let hasContact (doc: Node) =
    match Doc.get (sel "/contact") doc with
    | Ok _ -> Ok()
    | Error _ -> Error [ Error.create "ensure.contact" "every service needs a /contact" [] ]

printfn "-- with /contact:"
mapping {
    inputs [ "org"; "service" ]
    layer [ "org" ]
    compute "/contact" "join" [ Lit(Str " "); Ptr "service:/first"; Ptr "service:/last" ]
    ensure "has a contact" hasContact
}
|> runOver

printfn "-- without /contact:"
mapping {
    inputs [ "org"; "service" ]
    layer [ "org" ]
    ensure "has a contact" hasContact
}
|> runOver

// ---- Errors stop the run -----------------------------------------------------------------------
// A rule that cannot run fails the run and returns no document. The error says which rule, by index.
heading "A failing rule"

mapping {
    inputs [ "service" ]
    layer [ "service" ]
    copy "service:/does-not-exist" "/x"
}
|> runOver
