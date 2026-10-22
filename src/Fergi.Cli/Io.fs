namespace Fergi.Cli

open System
open System.IO

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
