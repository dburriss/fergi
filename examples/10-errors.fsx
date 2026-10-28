// Errors are values: codes, source positions, context, and everything reported at once.
//
//   dotnet fsi examples/10-errors.fsx
//
// Nothing here throws. Every call returns a Result, and a failed run returns no document at all.

#load "common.fsx"
open Common
open Fnode
open Fergi

// An Error has a Code (stable, safe to match on), a Message, a Path into the document, an optional source
// Location (line and column) and Context entries naming the rule, input and file involved.
let printFull (e: Error) =
    printfn "code     %s" e.Code
    printfn "message  %s" e.Message
    printfn "path     %s" (Path.print e.Path)
    printfn "location %A" (e.Location |> Option.map (fun s -> s.Start.Line, s.Start.Column))
    printfn "context  %A" e.Context

// ---- Parse errors keep the position ------------------------------------------------------------
heading "A file that does not parse"

match load (data "broken.yaml") with
| Error errs -> errs |> List.iter printFull
| Ok _ -> ()

// ---- Merge conflicts point at both files -------------------------------------------------------
// With ConflictMode.Fail every disagreement between layers is reported. The positions come from the
// parsed files, so the message can say where each side was written.
heading "Merge conflicts, with the file and line"

let strict =
    mapping {
        inputs [ "org"; "team" ]
        layer [ "org"; "team" ]
        conflict ConflictMode.Fail
    }

let f = files { input "org" (data "org.yaml"); input "team" (data "team.yaml"); output "out.yaml" }

match strict with
| Ok m -> Fergi.run m f load |> showResult ignore
| Error errs -> for e in errs do printfn "mapping error %s" (describeError e)

// ---- Context says which rule failed ------------------------------------------------------------
heading "Rule context"

let failing =
    mapping {
        inputs [ "service" ]
        layer [ "service" ]
        set "/ok" (Int 1L)
        copy "service:/missing" "/x"
    }

match failing with
| Ok m ->
    match Fergi.run m (files { input "service" (data "billing.yaml"); output "o" }) load with
    | Error errs -> errs |> List.iter printFull
    | Ok _ -> ()
| Error _ -> ()

// ---- Names are checked before any file is read -------------------------------------------------
// A name used in a rule but never declared, a declared input with no file, or a second layer: all reported
// up front, so a loader is never called for a run that cannot succeed.
heading "Names are checked before loading"

let incomplete = mapping { inputs [ "org"; "service" ]; layer [ "org"; "service" ] }

match incomplete with
| Ok m -> Fergi.run m (files { input "org" (data "org.yaml"); output "o" }) load |> showResult ignore
| Error _ -> ()

// ---- Error codes you can match on --------------------------------------------------------------
heading "Matching on a code"

let describeFailure (errs: Error list) =
    match errs |> List.map (fun e -> e.Code) with
    | codes when List.contains "merge.conflict" codes -> "two inputs disagree"
    | codes when List.contains "path.notFound" codes -> "something the mapping reads is missing"
    | codes when List.contains "parse" codes -> "a file is malformed"
    | _ -> "something else"

match strict with
| Ok m ->
    match Fergi.run m f load with
    | Error errs -> printfn "%s" (describeFailure errs)
    | Ok _ -> printfn "ok"
| Error _ -> ()
