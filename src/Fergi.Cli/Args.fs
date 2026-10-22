namespace Fergi.Cli

open Argu

type ValidateArgs =
    | [<MainCommand; ExactlyOnce>] Mapping_File of mapping: string
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Mapping_File _ -> "The mapping file to check (.yaml, .yml or .json)."

type RunArgs =
    | [<MainCommand; ExactlyOnce>] Mapping_File of mapping: string
    | [<AltCommandLine("-i")>]     Input        of name_and_file: string
    | [<AltCommandLine("-l")>]     Input_List   of name_and_file: string
    | [<ExactlyOnce; AltCommandLine("-o")>] Output of file: string
    | [<Unique>]                   Dry_Run
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Mapping_File _ -> "The mapping file to run (.yaml, .yml or .json)."
            | Input _        -> "A single-file input: name=file, or just a file (the name is then the file name without its extension). Repeat for more inputs."
            | Input_List _   -> "A list input (the mapping's inputLists): name=file. Repeat to add files in order. The file may be a glob (* and ?), expanded sorted by path."
            | Output _       -> "The file to write. Format follows the extension (.yaml, .yml, .json). It may not be an input or the mapping."
            | Dry_Run        -> "Print what would change in the output; write nothing."

[<CliPrefix(CliPrefix.None)>]
type FergiArgs =
    | [<SubCommand>] Validate of ParseResults<ValidateArgs>
    | [<SubCommand>] Run      of ParseResults<RunArgs>
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Validate _ -> "Check a mapping file without running it."
            | Run _      -> "Run a mapping over input files and write one output file."
