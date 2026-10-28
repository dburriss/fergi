module Fergi.Cli.VersionCli

open System.Reflection
open Argu

let (|VersionCmd|_|) (r: ParseResults<FergiArgs>) =
    if r.Contains FergiArgs.Version then Some () else None

/// Splits an informational version such as "0.1.0-alpha.1+abc123" into (version, commit).
let parseVersion (informational: string) : string * string option =
    match informational.IndexOf '+' with
    | -1 -> informational, None
    | i ->
        let commit = informational.Substring(i + 1)
        informational.Substring(0, i), (if commit = "" then None else Some commit)

/// The package version, from the assembly's informational version (set from `FergiVersion`).
let current () : string * string option =
    Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    |> Option.ofObj
    |> Option.map (fun a -> parseVersion a.InformationalVersion)
    |> Option.defaultValue ("unknown", None)

let run (io: Io) : int =
    let version, commit = current ()
    io.Out version
    match commit with
    | Some c -> io.Out("commit: " + c)
    | None -> ()
    0
