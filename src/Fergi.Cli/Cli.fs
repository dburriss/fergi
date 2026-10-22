namespace Fergi.Cli

open System
open System.IO
open Fnode
open Fergi
open Fergi.Jsonata

/// The effects the CLI needs, so a test can run it against a temp directory and capture the output.
type Io =
    { /// The text of a file, or the reason it could not be read.
      ReadText: string -> Result<string, string>
      /// Write a file in one step: nothing is left at the path if the write fails.
      WriteText: string -> string -> Result<unit, string>
      /// Expand a glob (`*` and `?` in any segment) to existing files, sorted by ordinal path.
      Glob: string -> string list
      FullPath: string -> string
      Out: string -> unit
      Err: string -> unit }

module Io =

    let private hasWildcard (s: string) = s.IndexOfAny [| '*'; '?' |] >= 0

    let private glob (pattern: string) : string list =
        let segments = pattern.Split([| '/'; Path.DirectorySeparatorChar |], StringSplitOptions.None) |> List.ofArray
        let start, rest =
            match segments with
            | "" :: tail when Path.IsPathRooted pattern -> Path.DirectorySeparatorChar.ToString(), tail
            | _ -> "", segments
        let step (dirs: string list) (seg: string) : string list =
            dirs
            |> List.collect (fun d ->
                let dir = if d = "" then "." else d
                if seg = "" || seg = "." then [ d ]
                elif hasWildcard seg then
                    if Directory.Exists dir then
                        Directory.EnumerateFileSystemEntries(dir, seg)
                        |> Seq.map (fun p -> if d = "" then Path.GetFileName p else Path.Combine(d, Path.GetFileName p))
                        |> List.ofSeq
                    else []
                else [ (if d = "" then seg else Path.Combine(d, seg)) ])
        rest |> List.fold step [ start ] |> List.filter File.Exists |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// Write beside the target and rename, so a crash or a failed write cannot leave a half-written output.
    let private writeText (path: string) (text: string) : Result<unit, string> =
        try
            let full = Path.GetFullPath path
            let dir = Path.GetDirectoryName full
            Directory.CreateDirectory dir |> ignore
            let tmp = Path.Combine(dir, "." + Path.GetFileName full + "." + Guid.NewGuid().ToString("N") + ".tmp")
            try
                File.WriteAllText(tmp, text)
                File.Move(tmp, full, true)
                Ok()
            finally
                if File.Exists tmp then File.Delete tmp
        with ex ->
            Error ex.Message

    let real: Io =
        { ReadText = fun p -> try Ok(File.ReadAllText p) with ex -> Error ex.Message
          WriteText = writeText
          Glob = glob
          FullPath = Path.GetFullPath
          Out = fun s -> Console.Out.WriteLine s
          Err = fun s -> Console.Error.WriteLine s }

/// `-i`: the input name (None: derive it from the file name) and the file.
type InputArg = { Name: string option; File: string }

type RunArgs =
    { Mapping: string
      Inputs: InputArg list
      /// `-l name=file`, in command-line order; the value may be a glob.
      Lists: (string * string) list
      Output: string
      DryRun: bool }

type Command =
    | Help
    | Validate of mapping: string
    | Run of RunArgs

module Cli =

    let usage =
        String.concat
            "\n"
            [ "usage:"
              "  fergi validate <mapping>"
              "  fergi run <mapping> -i [name=]<file> ... [-l name=<file or glob>] ... -o <file> [--dry-run]"
              ""
              "  -i, --input    a single-file input. Without name= the name is the file name without its extension"
              "  -l, --list     a list input (the mapping's inputLists): repeat to add files in order; the value may be"
              "                 a glob (* and ?), expanded sorted by path"
              "  -o, --output   the file to write (required, once). Format follows the extension (.yaml, .yml, .json)"
              "      --dry-run  print what would change; write nothing"
              "  -h, --help"
              ""
              "exit codes: 0 ok, 1 validation or run errors, 2 usage error" ]

    // -- arguments --

    let private splitName (s: string) : (string * string) option =
        match s.IndexOf '=' with
        | -1 -> None
        | i -> Some(s.Substring(0, i), s.Substring(i + 1))

    let private parseInput (s: string) : Result<InputArg, string> =
        match splitName s with
        | None -> if s = "" then Error "-i needs a file" else Ok { Name = None; File = s }
        | Some(n, f) when n = "" || f = "" -> Error("-i '" + s + "': expected name=file or a file")
        | Some(n, f) -> Ok { Name = Some n; File = f }

    let private parseList (s: string) : Result<string * string, string> =
        match splitName s with
        | Some(n, f) when n <> "" && f <> "" -> Ok(n, f)
        | _ -> Error("-l '" + s + "': expected name=file (a list input needs its name)")

    /// The name an `-i` binds: the explicit one, else the file name without its extension.
    let inputName (a: InputArg) : string =
        match a.Name with
        | Some n -> n
        | None -> Path.GetFileNameWithoutExtension a.File

    let parse (args: string list) : Result<Command, string> =
        let rec go (mapping: string option) inputs lists output dry rest =
            let value flag (tail: string list) next =
                match tail with
                | v :: tail' -> next v tail'
                | [] -> Error(flag + " needs a value")
            match rest with
            | [] ->
                match mapping, output with
                | None, _ -> Error "missing the mapping file"
                | _, None -> Error "missing -o <output file>"
                | Some m, Some o ->
                    if List.isEmpty inputs && List.isEmpty lists then Error "at least one -i or -l input is required"
                    else Ok(Run { Mapping = m; Inputs = List.rev inputs; Lists = List.rev lists; Output = o; DryRun = dry })
            | ("-i" | "--input") as f :: tail ->
                value f tail (fun v t ->
                    match parseInput v with
                    | Ok a -> go mapping (a :: inputs) lists output dry t
                    | Error e -> Error e)
            | ("-l" | "--list") as f :: tail ->
                value f tail (fun v t ->
                    match parseList v with
                    | Ok l -> go mapping inputs (l :: lists) output dry t
                    | Error e -> Error e)
            | ("-o" | "--output") as f :: tail ->
                value f tail (fun v t ->
                    match output with
                    | Some _ -> Error "-o was given more than once (there is exactly one output)"
                    | None -> go mapping inputs lists (Some v) dry t)
            | "--dry-run" :: tail -> go mapping inputs lists output true tail
            | flag :: _ when flag.StartsWith "-" && flag <> "-" -> Error("unknown option " + flag)
            | positional :: tail ->
                match mapping with
                | Some _ -> Error("unexpected argument '" + positional + "' (inputs go behind -i or -l)")
                | None -> go (Some positional) inputs lists output dry tail
        match args with
        | [] -> Error "missing command"
        | ("-h" | "--help" | "help") :: _ -> Ok Help
        | "validate" :: [ m ] when not (m.StartsWith "-") -> Ok(Validate m)
        | "validate" :: _ -> Error "validate takes exactly one argument: the mapping file"
        | "run" :: rest -> go None [] [] None false rest
        | other :: _ -> Error("unknown command '" + other + "'")

    // -- formats --

    let private formatOf (file: string) : Format option =
        [ Fnode.Yaml.format; Fnode.Json.format ]
        |> List.tryFind (fun f -> f.Extensions |> List.exists (fun e -> file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))

    let private unknownFormat (file: string) : Error =
        Error.create "load.unknownFormat" ("no format for '" + file + "' (expected .yaml, .yml or .json)") []
        |> Error.withContext "file" file

    let private loader (io: Io) : Loader =
        fun file ->
            match formatOf file with
            | None -> Error [ unknownFormat file ]
            | Some fmt ->
                match io.ReadText file with
                | Error msg -> Error [ Error.create "load.fileNotFound" ("cannot read '" + file + "': " + msg) [] ]
                | Ok text -> fmt.Parse text

    // -- reporting --

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

    let private reportAll (io: Io) (fallback: string option) (errs: Error list) : int =
        errs |> List.iter (fun e -> io.Err(render fallback e))
        1

    let private usageError (io: Io) (msg: string) : int =
        io.Err("fergi: " + msg)
        io.Err usage
        2

    let private opText (op: Op) : string =
        match op with
        | Op.Set(p, _) -> "set " + Path.print p
        | Op.Remove p -> "remove " + Path.print p
        | Op.Append(p, _) -> "append " + Path.print p
        | Op.Move(a, b) -> "move " + Path.print a + " -> " + Path.print b
        | Op.Test(p, _) -> "test " + Path.print p

    // -- commands --

    /// Read the mapping (YAML or JSON), with the built-in functions and `jsonata` registered.
    let private loadMapping (io: Io) (file: string) : Result<Mapping, Error list> =
        match formatOf file with
        | None -> Error [ unknownFormat file ]
        | Some fmt ->
            match io.ReadText file with
            | Error msg -> Error [ Error.create "load.fileNotFound" ("cannot read '" + file + "': " + msg) [] ]
            | Ok text ->
                fmt.Parse text
                |> Result.bind Mapping.ofParsed
                |> Result.map Jsonata.register

    let private validateMapping (io: Io) (file: string) : int =
        match loadMapping io file with
        | Error errs -> reportAll io (Some file) errs
        | Ok m ->
            match Jsonata.validate m with
            | [] ->
                io.Out "ok"
                0
            | errs -> reportAll io (Some file) errs

    let private expandLists (io: Io) (lists: (string * string) list) : Result<(string * string list) list, string> =
        let expand (name: string, value: string) =
            if value.IndexOfAny [| '*'; '?' |] < 0 then Ok(name, [ value ])
            else
                match io.Glob value with
                | [] -> Error("-l " + name + "=" + value + ": no file matches")
                | files -> Ok(name, files)
        let results = lists |> List.map expand
        match results |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some e -> Error e
        | None ->
            // Keep each name's files in the order given: a name may be repeated and mix files and globs.
            let ok = results |> List.choose (function Ok x -> Some x | Error _ -> None)
            Ok(
                ok
                |> List.map fst
                |> List.distinct
                |> List.map (fun n -> n, ok |> List.filter (fun (k, _) -> k = n) |> List.collect snd)
            )

    let private runMapping (io: Io) (a: RunArgs) : int =
        let formats = [ a.Output, "the output" ] |> List.filter (fun (f, _) -> (formatOf f).IsNone)
        match formats with
        | (f, what) :: _ -> usageError io ("no format for " + what + " '" + f + "' (expected .yaml, .yml or .json)")
        | [] ->
        let names = a.Inputs |> List.map inputName
        match names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
        | Some(n, _) -> usageError io ("two -i inputs are both named '" + n + "'; give each one an explicit name=file")
        | None ->
        match expandLists io a.Lists with
        | Error msg -> usageError io msg
        | Ok lists ->
        let listFiles = lists |> List.collect snd
        let full = io.FullPath
        let clash =
            a.Mapping :: (a.Inputs |> List.map (fun i -> i.File)) @ listFiles
            |> List.tryFind (fun f -> String.Equals(full f, full a.Output, StringComparison.OrdinalIgnoreCase))
        match clash with
        | Some f -> usageError io ("the output '" + a.Output + "' is also an input or the mapping ('" + f + "'); it would be overwritten")
        | None ->
        match loadMapping io a.Mapping with
        | Error errs -> reportAll io (Some a.Mapping) errs
        | Ok m ->
        // Bind names to the right flag before the core checks them, so the message can say which flag is needed.
        let declared = String.Join(", ", m.Inputs @ m.InputLists)
        let wrongFlag =
            [ for n in names do
                  if List.contains n m.InputLists then
                      yield Error.create ErrorCodes.mappingInputUnknown ("'" + n + "' is an input list; use -l " + n + "=<file>") []
                  elif not (List.contains n m.Inputs) then
                      yield Error.create ErrorCodes.mappingInputUnknown ("-i names '" + n + "', but the mapping declares: " + declared) []
              for n, _ in lists do
                  if List.contains n m.Inputs then
                      yield Error.create ErrorCodes.mappingInputUnknown ("'" + n + "' is a single input; use -i " + n + "=<file>") []
                  elif not (List.contains n m.InputLists) then
                      yield Error.create ErrorCodes.mappingInputUnknown ("-l names '" + n + "', but the mapping declares: " + declared) [] ]
        if not (List.isEmpty wrongFlag) then
            reportAll io None wrongFlag
        else
        match Jsonata.validate m with
        | _ :: _ as errs -> reportAll io (Some a.Mapping) errs
        | [] ->
        let files: Files =
            { Inputs = a.Inputs |> List.map (fun i -> inputName i, i.File) |> Map.ofList
              InputLists = Map.ofList lists
              Output = a.Output }
        for n, f in Map.toList files.Inputs do io.Out(n + " <- " + f)
        for n, fs in Map.toList files.InputLists do io.Out(n + " <- " + String.Join(", ", fs))
        let load = loader io
        if a.DryRun then
            match Fergi.plan m files load with
            | Error errs -> reportAll io None errs
            | Ok p ->
                if List.isEmpty p.Ops then io.Out("no changes to " + a.Output)
                else
                    for op in p.Ops do io.Out(opText op)
                    io.Out(string (List.length p.Ops) + " change(s) to " + a.Output + " (dry run, nothing written)")
                0
        else
            match Fergi.run m files load with
            | Error errs -> reportAll io None errs
            | Ok outputs ->
                let fmt = (formatOf a.Output).Value
                match io.WriteText a.Output (fmt.Print(Map.find a.Output outputs)) with
                | Error msg ->
                    io.Err("fergi: cannot write '" + a.Output + "': " + msg)
                    1
                | Ok() ->
                    io.Out("wrote " + a.Output)
                    0

    /// Run the command line; the result is the exit code.
    let run (args: string list) (io: Io) : int =
        match parse args with
        | Error msg -> usageError io msg
        | Ok Help ->
            io.Out usage
            0
        | Ok(Validate m) -> validateMapping io m
        | Ok(Run a) -> runMapping io a
