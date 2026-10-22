module CliTests

open System
open System.IO
open Xunit
open Fnode
open Fergi
open Fergi.Cli

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private ptr s =
    match Path.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private dataDir = Path.Combine(AppContext.BaseDirectory, "data")

/// A scratch directory holding a copy of the billing data, removed afterwards.
type Work() =
    let dir = Path.Combine(Path.GetTempPath(), "fergi-cli-" + Guid.NewGuid().ToString("N"))
    do
        Directory.CreateDirectory dir |> ignore
        for f in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories) do
            let target = Path.Combine(dir, Path.GetRelativePath(dataDir, f))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(f, target)
    member _.Dir = dir
    member _.Path(rel: string) = Path.Combine(dir, rel)
    member this.Write (rel: string) (text: string) =
        let p = this.Path rel
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        File.WriteAllText(p, text)
        p
    member this.Exists(rel: string) = File.Exists(this.Path rel)
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(dir, true) with _ -> ()

type Result' = { Code: int; Out: string list; Err: string list }

let private cli (args: string list) : Result' =
    let out = ResizeArray<string>()
    let err = ResizeArray<string>()
    let io = { Io.real with Out = out.Add; Err = err.Add }
    let code = Program.run (Array.ofList args) io
    { Code = code; Out = List.ofSeq out; Err = List.ofSeq err }

let private errText (r: Result') = String.Join("\n", r.Err)
let private outText (r: Result') = String.Join("\n", r.Out)

let private mustOk (r: Result<'a, Error list>) =
    match r with
    | Ok v -> v
    | Error es -> failwithf "%A" (es |> List.map (fun e -> e.Message))

let private inputs = [ "org"; "team"; "service" ]

let private billing =
    Mapping.create
        inputs
        [ Rule.Layer inputs
          Rule.ListsBy(ptr "/ports", "name")
          Rule.Copy(("service", sel "/name"), sel "/metadata/name")
          Rule.Copy(("team", sel "/oncall"), sel "/contacts/oncall")
          Rule.Rename(sel "/team", sel "/owner")
          Rule.DefaultTo(sel "/replicas", Int 2L)
          Rule.Set(sel "/env", Str "prod")
          Rule.Drop(sel "/internal")
          Rule.Drop(sel "/oncall") ]
    |> fun m -> { m with Ensure = [ "replicas positive", EnsureCheck.Jsonata "replicas >= 1" ] }

let private catalog =
    Mapping.create
        []
        [ Rule.Collect
              { Input = "repos"
                Into = sel "/services"
                Entry = [ Rule.Copy(("repos", sel "/name"), sel "/name"); Rule.Copy(("repos", sel "/owner"), sel "/team") ]
                SortBy = Some(sel "/name") } ]
    |> Mapping.withInputLists [ "repos" ]

let private yaml (m: Mapping) = Fnode.Yaml.format.Print(Mapping.toNode m |> mustOk)

let private billingArgs (w: Work) (mapping: string) =
    [ "run"; mapping
      "-i"; w.Path "defaults/org.yaml"
      "-i"; "team=" + w.Path "teams/energy.yaml"
      "-i"; "service=" + w.Path "services/billing/service.yaml"
      "-o"; w.Path "out/billing.yaml" ]

let private parseFile (path: string) : Node =
    match Fnode.Yaml.format.Parse(File.ReadAllText path) with
    | Ok p -> p.Node
    | Error es -> failwithf "%A" es

// -- arguments --

[<Fact>]
let ``a bare input file takes its name from the file name`` () =
    Assert.Equal("org", RunCli.inputName { Name = None; File = "defaults/org.yaml" })
    Assert.Equal("x", RunCli.inputName { Name = Some "x"; File = "defaults/org.yaml" })

[<Fact>]
let ``the name splits on the first equals sign`` () =
    match RunCli.parseInput "a=dir/b=c.yaml" with
    | Ok a -> Assert.Equal(({ Name = Some "a"; File = "dir/b=c.yaml" }: RunCli.InputArg), a)
    | other -> failwithf "%A" other

[<Fact>]
let ``usage errors exit 2`` () =
    for args in
        [ []
          [ "bogus" ]
          [ "validate" ]
          [ "run"; "m.yaml"; "-o"; "o.yaml" ] // no input
          [ "run"; "m.yaml"; "-i"; "a.yaml" ] // no output
          [ "run"; "m.yaml"; "-i"; "a.yaml"; "-o"; "o.yaml"; "-o"; "p.yaml" ]
          [ "run"; "m.yaml"; "-i"; "=a.yaml"; "-o"; "o.yaml" ]
          [ "run"; "m.yaml"; "-l"; "a.yaml"; "-o"; "o.yaml" ] // a list needs its name
          [ "run"; "m.yaml"; "extra.yaml"; "-i"; "a.yaml"; "-o"; "o.yaml" ]
          [ "run"; "m.yaml"; "-i" ] ] do
        Assert.Equal(2, (cli args).Code)

[<Fact>]
let ``help exits 0`` () =
    let r = cli [ "--help" ]
    Assert.Equal(0, r.Code)
    Assert.Contains("run", outText r)

// -- validate --

[<Fact>]
let ``validate accepts a good mapping, YAML or JSON`` () =
    use w = new Work()
    let y = w.Write "m.yaml" (yaml billing)
    let j = w.Write "m.json" (Fnode.Json.format.Print(Mapping.toNode billing |> mustOk))
    for m in [ y; j ] do
        let r = cli [ "validate"; m ]
        Assert.Equal(0, r.Code)
        Assert.Equal<string list>([ "ok" ], r.Out)

[<Fact>]
let ``validate reports every file error with line and column`` () =
    use w = new Work()
    let m = w.Write "bad.yaml" "version: 1\ninputs: [a]\nrules:\n  - nope: 1\n  - drop: 5\n"
    let r = cli [ "validate"; m ]
    Assert.Equal(1, r.Code)
    Assert.True(r.Err.Length >= 2, errText r)
    for line in r.Err do
        Assert.Matches(@"bad\.yaml:\d+:\d+: ", line)

[<Fact>]
let ``validate catches a bad jsonata expression`` () =
    use w = new Work()
    let m = { billing with Ensure = [ "broken", EnsureCheck.Jsonata "replicas >=" ] }
    let r = cli [ "validate"; w.Write "m.yaml" (yaml m) ]
    Assert.Equal(1, r.Code)
    Assert.Contains("compute.failed", errText r)

[<Fact>]
let ``validate reports an undeclared input`` () =
    use w = new Work()
    let m = Mapping.create [ "a" ] [ Rule.Copy(("zzz", sel "/x"), sel "/y") ]
    let r = cli [ "validate"; w.Write "m.yaml" (yaml m) ]
    Assert.Equal(1, r.Code)
    Assert.Contains("mapping.inputUnknown", errText r)

[<Fact>]
let ``validate of a missing file or an unknown extension fails`` () =
    use w = new Work()
    Assert.Equal(1, (cli [ "validate"; w.Path "nope.yaml" ]).Code)
    Assert.Equal(1, (cli [ "validate"; w.Write "m.txt" "x" ]).Code)

// -- run --

[<Fact>]
let ``the billing case through the CLI equals the hand-written expected file`` () =
    use w = new Work()
    let r = cli (billingArgs w (w.Write "m.yaml" (yaml billing)))
    Assert.Equal(0, r.Code)
    Assert.Equal(parseFile (w.Path "deploy/billing.yaml"), parseFile (w.Path "out/billing.yaml"))
    Assert.Contains("org <- ", outText r) // the derived name is shown

[<Fact>]
let ``a JSON input and a JSON output work`` () =
    use w = new Work()
    let m = w.Write "m.yaml" (yaml billing)
    let args =
        [ "run"; m
          "-i"; w.Path "defaults/org.yaml"
          "-i"; w.Path "teams/energy.json"
          "-i"; "service=" + w.Path "services/billing/service.yaml"
          "-o"; w.Path "out/billing.json" ]
    // the file name `energy` is not a declared input: this must be reported, not guessed
    let r = cli args
    Assert.Equal(1, r.Code)
    Assert.Contains("'energy'", errText r)
    let fixedArgs = args |> List.map (fun a -> if a.EndsWith "energy.json" then "team=" + a else a)
    let r2 = cli fixedArgs
    Assert.Equal(0, r2.Code)
    match Fnode.Json.format.Parse(File.ReadAllText(w.Path "out/billing.json")) with
    | Ok p ->
        // JSON has no timestamp type: the verbatim text comes back as a string
        let expected = Doc.set (sel "/lastReviewed") (Str "2026-10-14") (parseFile (w.Path "deploy/billing.yaml")) |> Result.defaultWith (fun e -> failwith e.Message)
        Assert.Equal(expected, p.Node)
    | Error es -> failwithf "%A" es

[<Fact>]
let ``a missing input file fails and writes nothing`` () =
    use w = new Work()
    let args = billingArgs w (w.Write "m.yaml" (yaml billing)) |> List.map (fun a -> a.Replace("teams/energy.yaml", "teams/none.yaml"))
    let r = cli args
    Assert.Equal(1, r.Code)
    Assert.False(w.Exists "out/billing.yaml")

[<Fact>]
let ``an undeclared name, and a missing declared name, are reported`` () =
    use w = new Work()
    let m = w.Write "m.yaml" (yaml billing)
    let unknown = cli [ "run"; m; "-i"; "bogus=" + w.Path "defaults/org.yaml"; "-o"; w.Path "o.yaml" ]
    Assert.Equal(1, unknown.Code)
    Assert.Contains("org, team, service", errText unknown) // the hint lists what is declared
    let missing = cli [ "run"; m; "-i"; "org=" + w.Path "defaults/org.yaml"; "-o"; w.Path "o.yaml" ]
    Assert.Equal(1, missing.Code)
    Assert.Contains("mapping.inputMissing", errText missing)
    Assert.False(w.Exists "o.yaml")

[<Fact>]
let ``two inputs with the same derived name are a usage error`` () =
    use w = new Work()
    let m = w.Write "m.yaml" (yaml billing)
    let r = cli [ "run"; m; "-i"; w.Path "teams/energy.yaml"; "-i"; w.Path "teams/energy.json"; "-o"; w.Path "o.yaml" ]
    Assert.Equal(2, r.Code)
    Assert.Contains("name=file", errText r)

[<Fact>]
let ``an output that is also an input or the mapping is refused`` () =
    use w = new Work()
    let m = w.Write "m.yaml" (yaml billing)
    let org = w.Path "defaults/org.yaml"
    let before = File.ReadAllText org
    let r = cli [ "run"; m; "-i"; org; "-i"; "team=" + w.Path "teams/energy.yaml"; "-o"; org ]
    Assert.Equal(2, r.Code)
    Assert.Equal(before, File.ReadAllText org)
    let r2 = cli [ "run"; m; "-i"; org; "-o"; m ]
    Assert.Equal(2, r2.Code)

[<Fact>]
let ``an unknown output extension is a usage error`` () =
    use w = new Work()
    let args = billingArgs w (w.Write "m.yaml" (yaml billing)) |> List.map (fun a -> a.Replace("out/billing.yaml", "out/billing.txt"))
    Assert.Equal(2, (cli args).Code)

[<Fact>]
let ``a failing ensure leaves no output`` () =
    use w = new Work()
    let args =
        billingArgs w (w.Write "m.yaml" (yaml billing))
        |> List.map (fun a -> a.Replace("services/billing/service.yaml", "services/billing/zero-replicas.yaml"))
    let r = cli args
    Assert.Equal(1, r.Code)
    Assert.Contains("ensure.failed", errText r)
    Assert.False(w.Exists "out/billing.yaml")

[<Fact>]
let ``a parse error in an input is reported with file line and column`` () =
    use w = new Work()
    let args =
        billingArgs w (w.Write "m.yaml" (yaml billing))
        |> List.map (fun a -> a.Replace("services/billing/service.yaml", "services/billing/broken.yaml"))
    let r = cli args
    Assert.Equal(1, r.Code)
    Assert.Matches(@"broken\.yaml:\d+:\d+: ", errText r)

// -- dry run --

[<Fact>]
let ``dry run prints the ops and writes nothing`` () =
    use w = new Work()
    let r = cli (billingArgs w (w.Write "m.yaml" (yaml billing)) @ [ "--dry-run" ])
    Assert.Equal(0, r.Code)
    Assert.False(w.Exists "out/billing.yaml")
    Assert.Contains("dry run", outText r)

[<Fact>]
let ``dry run against an up to date output reports no changes`` () =
    use w = new Work()
    let args = billingArgs w (w.Write "m.yaml" (yaml billing)) |> List.map (fun a -> a.Replace("out/billing.yaml", "deploy/billing.yaml"))
    let r = cli (args @ [ "--dry-run" ])
    Assert.Equal(0, r.Code)
    Assert.Contains("no changes", outText r)

// -- lists --

let private repo (name: string) (owner: string) = sprintf "name: %s\nowner: %s\n" name owner

let private repoDir (w: Work) =
    w.Write "repos/search.yaml" (repo "search" "discovery") |> ignore
    w.Write "repos/billing.yaml" (repo "billing" "energy") |> ignore
    w.Write "repos/checkout.json" "{\"name\":\"checkout\",\"owner\":\"sales\"}" |> ignore

let private serviceNames (path: string) =
    match parseFile path with
    | Obj [ "services", Arr items ] -> items |> List.map (function Obj kv -> (match List.tryFind (fun (k, _) -> k = "name") kv with Some(_, Str s) -> s | _ -> "?") | _ -> "?")
    | other -> failwithf "%A" other

[<Fact>]
let ``repeated -l keeps the order given`` () =
    use w = new Work()
    repoDir w
    let m = w.Write "c.yaml" (yaml { catalog with Rules = [ (match catalog.Rules.Head with Rule.Collect c -> Rule.Collect { c with SortBy = None } | r -> r) ] })
    let r =
        cli [ "run"; m; "-l"; "repos=" + w.Path "repos/search.yaml"; "-l"; "repos=" + w.Path "repos/billing.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(0, r.Code)
    Assert.Equal<string list>([ "search"; "billing" ], serviceNames (w.Path "out.yaml"))

[<Fact>]
let ``a glob expands sorted and mixes with explicit files`` () =
    use w = new Work()
    repoDir w
    let m = w.Write "c.yaml" (yaml { catalog with Rules = [ (match catalog.Rules.Head with Rule.Collect c -> Rule.Collect { c with SortBy = None } | r -> r) ] })
    let r = cli [ "run"; m; "-l"; "repos=" + w.Path "repos/*.yaml"; "-l"; "repos=" + w.Path "repos/checkout.json"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(0, r.Code)
    // billing.yaml, search.yaml (sorted by path), then the explicit JSON file
    Assert.Equal<string list>([ "billing"; "search"; "checkout" ], serviceNames (w.Path "out.yaml"))

[<Fact>]
let ``a glob may use a wildcard in a directory segment`` () =
    use w = new Work()
    w.Write "r/a/service.yaml" (repo "a" "x") |> ignore
    w.Write "r/b/service.yaml" (repo "b" "y") |> ignore
    w.Write "r/b/other.yaml" (repo "zzz" "y") |> ignore
    let m = w.Write "c.yaml" (yaml catalog)
    let r = cli [ "run"; m; "-l"; "repos=" + w.Path "r/*/service.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(0, r.Code)
    Assert.Equal<string list>([ "a"; "b" ], serviceNames (w.Path "out.yaml"))

[<Fact>]
let ``a glob with no match is a usage error, never an empty list`` () =
    use w = new Work()
    let m = w.Write "c.yaml" (yaml catalog)
    let r = cli [ "run"; m; "-l"; "repos=" + w.Path "nothing/*.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(2, r.Code)
    Assert.False(w.Exists "out.yaml")

[<Fact>]
let ``a wildcard in -i is not expanded`` () =
    use w = new Work()
    repoDir w
    let m = w.Write "c.yaml" (yaml billing)
    let r = cli [ "run"; m; "-i"; "org=" + w.Path "repos/*.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(1, r.Code)
    Assert.False(w.Exists "out.yaml")

[<Fact>]
let ``the wrong flag for a name says which flag is needed`` () =
    use w = new Work()
    repoDir w
    let m = w.Write "c.yaml" (yaml catalog)
    let r = cli [ "run"; m; "-i"; "repos=" + w.Path "repos/search.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(1, r.Code)
    Assert.Contains("use -l repos=", errText r)
    let b = w.Write "b.yaml" (yaml billing)
    let r2 = cli [ "run"; b; "-l"; "org=" + w.Path "defaults/org.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(1, r2.Code)
    Assert.Contains("use -i org=", errText r2)

[<Fact>]
let ``a declared list with no -l reports it missing`` () =
    use w = new Work()
    repoDir w
    // an extra single input so the command line is valid; the list is never bound
    let m = Mapping.create [ "a" ] [ Rule.Layer [ "a" ] ] |> Mapping.withInputLists [ "repos" ]
    let r = cli [ "run"; w.Write "c.yaml" (yaml m); "-i"; "a=" + w.Path "repos/search.yaml"; "-o"; w.Path "out.yaml" ]
    Assert.Equal(1, r.Code)
    Assert.Contains("mapping.inputMissing", errText r)
