// Shared by the numbered examples: references the built libraries and defines a few helpers.
//
//   build first:  dotnet build Fergi.slnx -c Release
//   run one:      dotnet fsi examples/01-edit-a-document.fsx
//
// The CLI output folder holds every assembly the examples need (Fnode, the YAML and JSON adapters and their
// Flexi dependencies, Fergi and Fergi.Jsonata), so one directory is referenced.

#r "../src/Fergi.Cli/bin/Release/net8.0/Fnode.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Flexi.Yaml.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Flexi.Json.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Fnode.Yaml.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Fnode.Json.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Fergi.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Jsonata.Net.Native.dll"
#r "../src/Fergi.Cli/bin/Release/net8.0/Fergi.Jsonata.dll"

open System.IO
open Fnode
open Fergi

/// The folder with the example inputs.
let dataDir = Path.Combine(__SOURCE_DIRECTORY__, "data")

/// A file under `data/`.
let data (name: string) = Path.Combine(dataDir, name)

// Pointer strings parse to a Result. A script can just throw on a bad one.
// `sel` accepts selectors (a pointer that may contain [key=value]); `ptr` accepts plain JSON Pointers.
let sel s = Selector.parse s |> Result.defaultWith (fun e -> failwith e.Message)
let ptr s = Path.parse s |> Result.defaultWith (fun e -> failwith e.Message)

/// The adapter for a file, picked by extension.
let formatOf (file: string) : Format =
    if file.EndsWith ".json" then Fnode.Json.format else Fnode.Yaml.format

/// A loader turns a file name into a parsed document. Fergi never reads files itself, so this is where the
/// file system comes in. The parsed document carries source positions, which end up in error messages.
let load (file: string) : Result<Parsed, Error list> =
    if File.Exists file then (formatOf file).Parse(File.ReadAllText file)
    else Error [ Error.create ErrorCode.pathNotFound ("no such file " + file) [] ]

/// Read a file as a plain node, or stop the script.
let loadNode (file: string) : Node =
    match load file with
    | Ok p -> p.Node
    | Error errs -> failwithf "%s: %s" file (errs |> List.map (fun e -> e.Message) |> String.concat "; ")

/// One line per error: `line:col code: message`, with the position when the parser recorded one.
let describeError (e: Error) : string =
    let pos =
        match e.Location with
        | Some s -> sprintf "%d:%d " s.Start.Line s.Start.Column
        | None -> ""
    sprintf "%s%s: %s" pos e.Code (Error.describe e)

/// Print a section heading.
let heading (text: string) = printfn "\n== %s ==" text

/// Print a node as YAML (the printer is the format's own; nothing is written to disk).
let showYaml (node: Node) = printf "%s" (Fnode.Yaml.format.Print node)

/// Print a node as JSON.
let showJson (node: Node) = printf "%s" (Fnode.Json.format.Print node)

/// Print every error of a failed result, or the value of a successful one.
let showResult (show: 'a -> unit) (r: Result<'a, Error list>) =
    match r with
    | Ok v -> show v
    | Error errs -> for e in errs do printfn "error %s" (describeError e)
