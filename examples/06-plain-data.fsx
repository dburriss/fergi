// The same ideas as plain data, with no computation expressions: Rule, Mapping.create, Fergi.runFiles.
//
//   dotnet fsi examples/06-plain-data.fsx
//
// Reference: docs/reference/mapping.md (Plain data form)

#load "common.fsx"
open Common
open Fnode
open Fergi

// `Rule` is plain data and Mapping.create takes the input names plus the rules. Nothing here is a function
// except the ensure hook, so you can build, store, compare and print a mapping like any other value.
let m =
    Mapping.create
        [ "org"; "service" ]
        [ Rule.Layer [ "org"; "service" ]
          Rule.ListsBy(ptr "/ports", "name")
          Rule.Conflict ConflictMode.LastWins
          Rule.Copy(("service", sel "/name"), sel "/metadata/name")
          Rule.DefaultTo(sel "/kind", Str "service")
          Rule.Set(sel "/ports/[name=http]/port", Int 80L)
          Rule.Rename(sel "/old", sel "/previous")
          Rule.Drop(sel "/internal")
          Rule.Compute
              { Fn = "join"
                Args = [ Arg.Literal(Str " "); Arg.Input("service", sel "/first"); Arg.Doc(sel "/last") ]
                Dest = sel "/fullName" } ]
    |> Mapping.ensure "has a name" (fun doc ->
        match Doc.get (sel "/metadata/name") doc with
        | Ok _ -> Ok()
        | Error e -> Error [ e ])

// Rules print as ordinary F# data.
heading "The rules, as data"
for r in m.Rules do printfn "%A" r

// ---- Run it with Fergi.run ---------------------------------------------------------------------
heading "Fergi.run"

let f = { Inputs = Map.ofList [ "org", data "org.yaml"; "service", data "billing.yaml" ]
          InputLists = Map.empty
          Output = "out.yaml" }

Fergi.run m f load |> Result.map (fun out -> out.[f.Output]) |> showResult showYaml

// ---- Fergi.runFiles: the one-call form ---------------------------------------------------------
// (name, file) pairs, an output name and rules: no Files value and no Mapping value. It takes only the
// rules, so input lists and ensure hooks of `m` are not used.
heading "Fergi.runFiles"

Fergi.runFiles [ "org", data "org.yaml"; "service", data "billing.yaml" ] "out.yaml" m.Rules load
|> Result.map (fun out -> out.["out.yaml"])
|> showResult showYaml

// ---- Mapping.copy: one Copy rule from strings --------------------------------------------------
heading "Mapping.copy parses a Copy rule from strings"

printfn "%A" (Mapping.copy "service:/name" "/metadata/name" [ "org"; "service" ])
printfn "%A" (Mapping.copy "nobody:/name" "/metadata/name" [ "org"; "service" ] |> Result.mapError (fun e -> e.Code))

// ---- In-memory documents -----------------------------------------------------------------------
// Loader.ofNodes serves documents from a map: no files at all. Handy in tests.
heading "Loader.ofNodes"

let docs =
    Map.ofList
        [ "org", Obj [ "owner", Str "platform"; "ports", Arr [ Obj [ "name", Str "http"; "port", Int 8080L ] ] ]
          "svc", Obj [ "name", Str "inline"; "first", Str "In"; "last", Str "Memory"; "old", Str "v0" ] ]

Fergi.runFiles [ "org", "org"; "service", "svc" ] "out" m.Rules (Loader.ofNodes docs)
|> Result.map (fun out -> out.["out"])
|> showResult showYaml

// ---- The whole input as one value --------------------------------------------------------------
// "service:" (no path) is the entire input document, not a key named "". "/" would mean the key "".
heading "Arg.Input with an empty selector is the whole document"

let whole =
    Mapping.create [ "service" ] [ Rule.Compute { Fn = "concat"; Args = [ Arg.Input("service", sel "") ]; Dest = sel "/x" } ]

// concat takes strings, ints and bools and the whole input is an object, so this fails. It shows that the
// pointer resolved to the document, and that the failure comes back as a value.
Fergi.runFiles [ ("service", data "billing.yaml") ] "out" whole.Rules load |> showResult ignore
