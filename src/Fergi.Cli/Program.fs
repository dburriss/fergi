module Fergi.Cli.Program

open Argu
open Fergi.Cli.ValidateCli
open Fergi.Cli.RunCli

/// Parse and dispatch. A command is an active pattern over the parse result (see `ValidateCli`, `RunCli`).
/// Split from `main` so the tests can run it against a temp directory and capture the output.
let run (argv: string[]) (io: Io) : int =
    let parser = ArgumentParser.Create<FergiArgs>(programName = "fergi")
    try
        // With CliPrefix.None Argu's own help flag is the bare word `help`; accept the usual spellings too.
        let argv = argv |> Array.map (function "--help" | "-h" -> "help" | a -> a)
        let parsed = parser.ParseCommandLine argv
        match parsed with
        | ValidateCmd cmd -> ValidateCli.run io cmd
        | RunCmd cmd -> RunCli.run io cmd
        | _ ->
            io.Err(parser.PrintUsage())
            2
    with
    | :? ArguParseException as ex when ex.ErrorCode = ErrorCode.HelpText ->
        io.Out ex.Message
        0
    | :? ArguParseException as ex ->
        io.Err ex.Message
        2
    | ex ->
        io.Err("fergi: unexpected error: " + ex.Message)
        1

[<EntryPoint>]
let main argv = run argv Io.real
