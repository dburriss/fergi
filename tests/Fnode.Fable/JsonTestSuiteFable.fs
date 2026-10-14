/// Fable counterpart of tests/JsonValue.Tests/JsonTestSuiteTests.fs. The .NET file reads the vendored
/// JSON Test Suite from disk; here the corpus is embedded by scripts/fable-gen-runner.fsx
/// (JsonTestSuite.generated.fs), with files that are not valid UTF-8 stored as `None` (the .NET test
/// rejects those at the strict UTF-8 decoding step, before the JSON parser sees them).
module JsonTestSuiteFable

open Fergi.Json

type Outcome =
    | Accepted
    | Rejected of string

let private table = dict JsonTestSuite.files

let private run (name: string) : Outcome =
    match table.[name] with
    | None -> Rejected "invalid UTF-8"
    | Some t ->
        match JsonValue.TryParseResult t with
        | Ok v ->
            // accepted values must also print and re-parse to themselves
            if v <> JsonValue.Parse(v.ToString()) then failwith (name + ": print/parse changed the value")
            Accepted
        | Error e -> Rejected(e.Message + " @" + string e.Pos.Line + ":" + string e.Pos.Column)

let accepted (name: string) : unit =
    match run name with
    | Accepted -> ()
    | Rejected m -> failwith (name + " was rejected: " + m)

let rejected (name: string) : unit =
    match run name with
    | Rejected _ -> ()
    | Accepted -> failwith (name + " was accepted")

let reportImplementationDefined () : unit =
    let results =
        [ for (name, _) in JsonTestSuite.files do
              if name.StartsWith("i_", System.StringComparison.Ordinal) then
                  match run name with
                  | Accepted -> true
                  | Rejected _ -> false ]
    printfn "JSON Test Suite i_ files (implementation-defined): %d accepted, %d rejected"
        (results |> List.filter id |> List.length) (results |> List.filter not |> List.length)

let present () : unit =
    let count (prefix: string) =
        JsonTestSuite.files |> List.filter (fun (n, _) -> n.StartsWith(prefix, System.StringComparison.Ordinal)) |> List.length
    if count "y_" < 90 then failwith "fewer than 90 y_ files"
    if count "n_" < 180 then failwith "fewer than 180 n_ files"
