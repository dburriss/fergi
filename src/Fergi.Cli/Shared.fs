/// What the commands have in common: formats by extension, the loader, loading a mapping, and reporting.
module Fergi.Cli.Shared

open System
open Fnode
open Fergi
open Fergi.Jsonata

let formatOf (file: string) : Format option =
    [ Fnode.Yaml.format; Fnode.Json.format ]
    |> List.tryFind (fun f -> f.Extensions |> List.exists (fun e -> file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))

let unknownFormat (file: string) : Error =
    Error.create "load.unknownFormat" ("no format for '" + file + "' (expected .yaml, .yml or .json)") []
    |> Error.withContext "file" file

let private cannotRead (file: string) (msg: string) : Error =
    Error.create "load.fileNotFound" ("cannot read '" + file + "': " + msg) []

/// The loader the core runs over: read, then parse with the format the extension names.
let loader (io: Io) : Loader =
    fun file ->
        match formatOf file with
        | None -> Error [ unknownFormat file ]
        | Some fmt ->
            match io.ReadText file with
            | Error msg -> Error [ cannotRead file msg ]
            | Ok text -> fmt.Parse text

/// Read a mapping (YAML or JSON), with the built-in functions and `jsonata` registered.
let loadMapping (io: Io) (file: string) : Result<Mapping, Error list> =
    match formatOf file with
    | None -> Error [ unknownFormat file ]
    | Some fmt ->
        match io.ReadText file with
        | Error msg -> Error [ cannotRead file msg ]
        | Ok text ->
            fmt.Parse text
            |> Result.bind Mapping.ofParsed
            |> Result.map Jsonata.register

/// `file:line:col: code: message`. `fallback` names the file for an error with a location but no `file`
/// context (a mapping-file error); the `file` context is dropped from the message, the prefix has it.
let render (fallback: string option) (e: Error) : string =
    let file = e.Context |> List.tryFind (fun (k, _) -> k = "file") |> Option.map snd |> Option.orElse fallback
    let where =
        match file, e.Location with
        | Some f, Some s -> f + ":" + string s.Start.Line + ":" + string s.Start.Column + ": "
        | Some f, None -> f + ": "
        | None, _ -> ""
    let message = { e with Context = e.Context |> List.filter (fun (k, _) -> k <> "file") } |> Error.describe
    let at = if List.isEmpty e.Path || e.Location.IsSome then "" else " (at " + Path.print e.Path + ")"
    where + e.Code + ": " + message + at

/// Print every error to stderr; the exit code for "the mapping or the run failed".
let reportAll (io: Io) (fallback: string option) (errs: Error list) : int =
    errs |> List.iter (fun e -> io.Err(render fallback e))
    1

/// A command line that cannot be run as given; the exit code is 2.
let usageError (io: Io) (msg: string) : int =
    io.Err("fergi: " + msg)
    io.Err "run 'fergi --help' for usage"
    2
