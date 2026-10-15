// Writes the mapping files that Mapping.toNode produces, then reads every case file and every generated
// file with Mapping.ofParsed and records whether it was accepted. check.py compares that with the JSON Schema.
//   dotnet run -- <cases dir> <out dir>
module Program

open System.IO
open Fnode
open Fergi

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private ptr s =
    match Path.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private generated : (string * Mapping) list =
    [ "minimal",
      Mapping.create [] []
      "every-rule",
      Mapping.create
          [ "person"; "org" ]
          [ Rule.Layer [ "org"; "person" ]
            Rule.ListsBy(ptr "/ports", "name")
            Rule.Conflict ConflictMode.FirstWins
            Rule.NullDeletes false
            Rule.Copy(("person", sel "/name"), sel "/metadata/name")
            Rule.Rename(sel "/a", sel "/b")
            Rule.DefaultTo(sel "/kind", Str "service")
            Rule.Set(sel "/ports/[name=http]/port", Int 80L)
            Rule.Drop(sel "/internal")
            Rule.Compute
                { Fn = "join"
                  Args = [ Arg.Literal(Str " "); Arg.Input("person", sel "/first"); Arg.Doc(sel "/last"); Arg.Literal Null ]
                  Dest = sel "/fullName" }
            Rule.Collect
                { Input = "services"
                  Into = sel "/services"
                  Entry = [ Rule.Copy(("services", sel "/name"), sel "/name"); Rule.Set(sel "/kind", Obj [ "x", Arr [ Bool true; Float 1.5 ] ]) ]
                  SortBy = Some(sel "/name") } ]
      |> Mapping.withInputLists [ "services" ]
      "escapes",
      Mapping.create
          [ "a" ]
          [ Rule.Copy(("a", sel "/a~1b/c~0d"), sel "/x~1y")
            Rule.ListsBy(ptr "/m~1n", "k") ] ]

let private relative (root: string) (file: string) = Path.GetRelativePath(root, file).Replace('\\', '/')

[<EntryPoint>]
let main argv =
    let casesDir, outDir = argv.[0], argv.[1]
    let genDir = Path.Combine(outDir, "generated")
    Directory.CreateDirectory genDir |> ignore
    for name, m in generated do
        match Mapping.toNode m with
        | Ok node -> File.WriteAllText(Path.Combine(genDir, name + ".json"), Json.format.Print node + "\n")
        | Error es -> failwithf "could not write %s: %A" name (es |> List.map Error.describe)
    let files =
        [ for root in [ casesDir; outDir ] do
              for f in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories) do
                  if not (f.EndsWith "dotnet.json") then yield root, f ]
        |> List.sortBy snd
    let verdicts =
        files
        |> List.map (fun (root, f) ->
            let result = File.ReadAllText f |> Json.format.Parse |> Result.bind Mapping.ofParsed
            let v =
                match result with
                | Ok _ -> Obj [ "accepted", Bool true; "errors", Arr [] ]
                | Error es -> Obj [ "accepted", Bool false; "errors", Arr(es |> List.map (fun e -> Str(e.Code + " " + Path.print e.Path))) ]
            relative root f, v)
    File.WriteAllText(Path.Combine(outDir, "dotnet.json"), Json.format.Print(Obj verdicts) + "\n")
    printfn "ofParsed read %d files" verdicts.Length
    0
