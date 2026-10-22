module Fergi.Cli.ValidateCli

open Argu
open Fergi.Jsonata
open Fergi.Cli.Shared

type Cmd = { Mapping: string }

let (|ValidateCmd|_|) (r: ParseResults<FergiArgs>) =
    r.TryGetSubCommand() |> Option.bind (function
        | FergiArgs.Validate args -> Some { Mapping = args.GetResult ValidateArgs.Mapping_File }
        | _ -> None)

/// Mapping only (no files), as `Mapping.validate` is designed for; `Jsonata.validate` adds expression syntax.
let run (io: Io) (cmd: Cmd) : int =
    match loadMapping io cmd.Mapping with
    | Error errs -> reportAll io (Some cmd.Mapping) errs
    | Ok m ->
        match Jsonata.validate m with
        | [] ->
            io.Out "ok"
            0
        | errs -> reportAll io (Some cmd.Mapping) errs
