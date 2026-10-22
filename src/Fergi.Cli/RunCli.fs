module Fergi.Cli.RunCli

open System
open System.IO
open Argu
open Fnode
open Fergi
open Fergi.Jsonata
open Fergi.Cli.Shared

/// The command line as typed: `-i` and `-l` values are split and checked in `run`, where a bad one is a usage error.
type Cmd =
    { Mapping: string
      Inputs: string list
      Lists: string list
      Output: string
      DryRun: bool }

let (|RunCmd|_|) (r: ParseResults<FergiArgs>) =
    r.TryGetSubCommand() |> Option.bind (function
        | FergiArgs.Run args ->
            Some
                { Mapping = args.GetResult RunArgs.Mapping_File
                  Inputs = args.GetResults RunArgs.Input
                  Lists = args.GetResults RunArgs.Input_List
                  Output = args.GetResult RunArgs.Output
                  DryRun = args.Contains RunArgs.Dry_Run }
        | _ -> None)

/// `-i`: the input name (None: derive it from the file name) and the file.
type InputArg = { Name: string option; File: string }

let private splitName (s: string) : (string * string) option =
    match s.IndexOf '=' with
    | -1 -> None
    | i -> Some(s.Substring(0, i), s.Substring(i + 1))

let parseInput (s: string) : Result<InputArg, string> =
    match splitName s with
    | None -> if s = "" then Error "-i needs a file" else Ok { Name = None; File = s }
    | Some(n, f) when n = "" || f = "" -> Error("-i '" + s + "': expected name=file or a file")
    | Some(n, f) -> Ok { Name = Some n; File = f }

let parseList (s: string) : Result<string * string, string> =
    match splitName s with
    | Some(n, f) when n <> "" && f <> "" -> Ok(n, f)
    | _ -> Error("-l '" + s + "': expected name=file (a list input needs its name)")

/// The name an `-i` binds: the explicit one, else the file name without its extension.
let inputName (a: InputArg) : string =
    match a.Name with
    | Some n -> n
    | None -> Path.GetFileNameWithoutExtension a.File

let private opText (op: Op) : string =
    match op with
    | Op.Set(p, _) -> "set " + Path.print p
    | Op.Remove p -> "remove " + Path.print p
    | Op.Append(p, _) -> "append " + Path.print p
    | Op.Move(a, b) -> "move " + Path.print a + " -> " + Path.print b
    | Op.Test(p, _) -> "test " + Path.print p

let private allOk (rs: Result<'a, string> list) : Result<'a list, string> =
    match rs |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some e -> Error e
    | None -> Ok(rs |> List.choose (function Ok x -> Some x | Error _ -> None))

let private expandLists (io: Io) (lists: (string * string) list) : Result<(string * string list) list, string> =
    let expand (name: string, value: string) =
        if value.IndexOfAny [| '*'; '?' |] < 0 then Ok(name, [ value ])
        else
            match io.Glob value with
            | [] -> Error("-l " + name + "=" + value + ": no file matches")
            | files -> Ok(name, files)
    lists
    |> List.map expand
    |> allOk
    // Keep each name's files in the order given: a name may be repeated and mix files and globs.
    |> Result.map (fun ok ->
        ok
        |> List.map fst
        |> List.distinct
        |> List.map (fun n -> n, ok |> List.filter (fun (k, _) -> k = n) |> List.collect snd))

/// Bind names to the right flag before the core checks them, so the message can say which flag is needed.
let private flagErrors (m: Mapping) (names: string list) (lists: (string * string list) list) : Error list =
    let declared = String.Join(", ", m.Inputs @ m.InputLists)
    let unknown (msg: string) = Error.create ErrorCodes.mappingInputUnknown msg []
    [ for n in names do
          if List.contains n m.InputLists then yield unknown ("'" + n + "' is an input list; use -l " + n + "=<file>")
          elif not (List.contains n m.Inputs) then yield unknown ("-i names '" + n + "', but the mapping declares: " + declared)
      for n, _ in lists do
          if List.contains n m.Inputs then yield unknown ("'" + n + "' is a single input; use -i " + n + "=<file>")
          elif not (List.contains n m.InputLists) then yield unknown ("-l names '" + n + "', but the mapping declares: " + declared) ]

let private execute (io: Io) (cmd: Cmd) (inputs: InputArg list) (lists: (string * string list) list) : int =
    match loadMapping io cmd.Mapping with
    | Error errs -> reportAll io (Some cmd.Mapping) errs
    | Ok m ->
        let names = inputs |> List.map inputName
        match flagErrors m names lists with
        | _ :: _ as errs -> reportAll io None errs
        | [] ->
            match Jsonata.validate m with
            | _ :: _ as errs -> reportAll io (Some cmd.Mapping) errs
            | [] ->
                let files: Files =
                    { Inputs = inputs |> List.map (fun i -> inputName i, i.File) |> Map.ofList
                      InputLists = Map.ofList lists
                      Output = cmd.Output }
                for n, f in Map.toList files.Inputs do io.Out(n + " <- " + f)
                for n, fs in Map.toList files.InputLists do io.Out(n + " <- " + String.Join(", ", fs))
                let load = loader io
                if cmd.DryRun then
                    match Fergi.plan m files load with
                    | Error errs -> reportAll io None errs
                    | Ok p ->
                        if List.isEmpty p.Ops then io.Out("no changes to " + cmd.Output)
                        else
                            for op in p.Ops do io.Out(opText op)
                            io.Out(string (List.length p.Ops) + " change(s) to " + cmd.Output + " (dry run, nothing written)")
                        0
                else
                    match Fergi.run m files load with
                    | Error errs -> reportAll io None errs
                    | Ok outputs ->
                        let fmt = (formatOf cmd.Output).Value
                        match io.WriteText cmd.Output (fmt.Print(Map.find cmd.Output outputs)) with
                        | Error msg ->
                            io.Err("fergi: cannot write '" + cmd.Output + "': " + msg)
                            1
                        | Ok() ->
                            io.Out("wrote " + cmd.Output)
                            0

/// Checks that need only the command line (exit 2), then the run itself (exit 1 on errors).
let run (io: Io) (cmd: Cmd) : int =
    let parsed = allOk (List.map parseInput cmd.Inputs), allOk (List.map parseList cmd.Lists)
    match parsed with
    | Error msg, _ | _, Error msg -> usageError io msg
    | Ok inputs, Ok rawLists ->
    if List.isEmpty inputs && List.isEmpty rawLists then usageError io "at least one -i or -l input is required"
    elif (formatOf cmd.Output).IsNone then
        usageError io ("no format for the output '" + cmd.Output + "' (expected .yaml, .yml or .json)")
    else
    let names = inputs |> List.map inputName
    match names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
    | Some(n, _) -> usageError io ("two -i inputs are both named '" + n + "'; give each one an explicit name=file")
    | None ->
    match expandLists io rawLists with
    | Error msg -> usageError io msg
    | Ok lists ->
    let full = io.FullPath
    let clash =
        cmd.Mapping :: (inputs |> List.map (fun i -> i.File)) @ (lists |> List.collect snd)
        |> List.tryFind (fun f -> String.Equals(full f, full cmd.Output, StringComparison.OrdinalIgnoreCase))
    match clash with
    | Some f -> usageError io ("the output '" + cmd.Output + "' is also an input or the mapping ('" + f + "'); it would be overwritten")
    | None -> execute io cmd inputs lists
