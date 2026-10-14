/// End to end: the IDP defaults-plus-overrides case with real files on disk, parsed through the
/// real Fnode.Yaml / Fnode.Json adapters. The loader below is test-side IO; core has none.
module EndToEndTests

open System.IO
open Xunit
open Fnode
open Fergi

let private dataDir = Path.Combine(System.AppContext.BaseDirectory, "data")

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private ptr s =
    match Path.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

/// Format.detect-style helper: pick the adapter by file extension.
let private detect (file: string) : Format option =
    [ Fnode.Yaml.format; Fnode.Json.format ]
    |> List.tryFind (fun f -> f.Extensions |> List.exists (fun e -> file.EndsWith e))

/// Loader: path relative to the data dir -> Node. Parse errors pass through unchanged (first one;
/// the loader shape returns a single Error), so Location survives.
let private loadParsed (file: string) : Result<Parsed, Error> =
    let full = Path.Combine(dataDir, file)
    if not (File.Exists full) then
        Error(Error.create "load.fileNotFound" ("File not found: " + file) [])
    else
        match detect file with
        | None -> Error(Error.create "load.unknownFormat" ("No format for: " + file) [])
        | Some fmt ->
            match fmt.Parse(File.ReadAllText full) with
            | Ok p -> Ok p
            | Error(e :: _) -> Error e
            | Error [] -> Error(Error.create ErrorCode.parse "parse failed" [])

let private load (file: string) : Result<Node, Error> = loadParsed file |> Result.map (fun p -> p.Node)

let private mustLoad file =
    match load file with
    | Ok n -> n
    | Error e -> failwithf "%s: %s" file e.Message

let private inputs = [ "org"; "team"; "service" ]

let private replicasPositive (n: Node) =
    match Doc.get (sel "/replicas") n with
    | Ok(Int i) when i >= 1L -> Ok()
    | _ -> Error [ Error.create ErrorCode.typeMismatch "replicas must be an integer >= 1" (ptr "/replicas") ]

let private namePresent (n: Node) =
    match Doc.get (sel "/metadata/name") n with
    | Ok(Str s) when s <> "" -> Ok()
    | _ -> Error [ Error.create ErrorCode.pathNotFound "metadata.name is required" (ptr "/metadata/name") ]

let billingMapping =
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
    |> Mapping.ensure "replicas >= 1" replicasPositive
    |> Mapping.ensure "metadata.name present" namePresent

let private filesWith team service =
    { Inputs = Map.ofList [ "org", "defaults/org.yaml"; "team", team; "service", service ]
      Output = "deploy/billing.yaml" }

let billingFiles = filesWith "teams/energy.yaml" "services/billing/service.yaml"

let private runWith files =
    Fergi.run billingMapping files load |> Result.map (Map.find files.Output)

let private runOk files =
    match runWith files with
    | Ok n -> n
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

let private codes (es: Error list) = es |> List.map (fun e -> e.Code)

// (1)
[<Fact>]
let ``billing run equals the hand-written expected file`` () =
    Assert.Equal(mustLoad "deploy/billing.yaml", runOk billingFiles)

// (2)
[<Fact>]
let ``printed output re-parses to the same Node and keeps the timestamp verbatim`` () =
    let result = runOk billingFiles
    let text = Fnode.Yaml.format.Print result
    Assert.Contains("lastReviewed: 2026-10-14", text)
    match Fnode.Yaml.format.Parse text with
    | Ok p -> Assert.Equal(result, p.Node)
    | Error es -> failwithf "re-parse failed: %A" es

[<Fact>]
let ``timestamps stay Timestamp nodes and anchors are expanded`` () =
    let result = runOk billingFiles
    Assert.Equal(Ok(Timestamp "2026-10-14"), Doc.get (sel "/lastReviewed") result)
    Assert.Equal(Ok(Str "256Mi"), Doc.get (sel "/resources/requests/memory") result)
    Assert.Equal(Ok(Str "250m"), Doc.get (sel "/resources/requests/cpu") result)

// (3)
[<Fact>]
let ``plan against the current output yields no ops and with a stale output yields ops that apply`` () =
    let expected = mustLoad "deploy/billing.yaml"
    match Fergi.plan billingMapping billingFiles load with
    | Ok p ->
        Assert.Equal<Op list>([], p.Ops)
        Assert.Equal(expected, p.Result)
    | Error es -> failwithf "%A" es
    // a stale current output: loader serves a modified copy for the output path only
    let stale = Doc.set (sel "/replicas") (Int 1L) expected |> Result.defaultValue expected
    let staleLoad f = if f = billingFiles.Output then Ok stale else load f
    match Fergi.plan billingMapping billingFiles staleLoad with
    | Ok p ->
        Assert.Contains(Set(ptr "/replicas", Int 4L), p.Ops)
        Assert.Equal(Ok p.Result, Patch.apply p.Ops stale)
    | Error es -> failwithf "%A" es

[<Fact>]
let ``plan provenance names the right source per layered key`` () =
    match Fergi.plan billingMapping billingFiles load with
    | Ok p ->
        let src k = Map.tryFind k p.Provenance
        Assert.Equal(Some "service", src "/replicas")
        Assert.Equal(Some "service", src "/labels/tier")
        Assert.Equal(Some "service", src "/name")
        Assert.Equal(Some "team", src "/labels/team")
        Assert.Equal(Some "team", src "/team")
        Assert.Equal(Some "team", src "/oncall")
        Assert.Equal(Some "org", src "/resources/limits/cpu")
        Assert.Equal(Some "org", src "/internal/costCentre")
        Assert.Equal(None, src "/logLevel")
    | Error es -> failwithf "%A" es

// (4)
[<Fact>]
let ``a port item without name is a ByKey merge.conflict naming the sources`` () =
    match runWith (filesWith "teams/energy.yaml" "services/billing/bad-port.yaml") with
    | Error es ->
        Assert.Contains(ErrorCodes.mergeConflict, codes es)
        let msg = es |> List.map (fun e -> e.Message) |> String.concat "\n"
        Assert.Contains("service", msg)
        Assert.Contains("/ports", msg)
    | Ok _ -> failwith "expected conflict"

[<Fact>]
let ``Doc.layers with Fail reports a scalar conflict between team and service`` () =
    let team = mustLoad "teams/energy.yaml"
    let service = Obj [ "team", Str "platform" ]
    let pol = Policy.defaults |> Policy.withConflict Fail
    match Doc.layers pol [ "team", team; "service", service ] with
    | Error [ c ] ->
        Assert.True((ptr "/team" = c.Path))
        Assert.Equal(Some "team", c.LeftSource)
        Assert.Equal(Some "service", c.RightSource)
        let e = Conflict.toError c
        Assert.Contains("team", e.Message)
        Assert.Contains("service", e.Message)
    | other -> failwithf "unexpected %A" other

// (5)
[<Fact>]
let ``a missing input file is a load error and the others still load`` () =
    match runWith (filesWith "teams/nope.yaml" "services/billing/nope.json") with
    | Error es ->
        Assert.Equal<string list>([ "load.fileNotFound"; "load.fileNotFound" ], codes es)
        Assert.Contains("'team'", es.[0].Message)
        Assert.Contains("'service'", es.[1].Message)
    | Ok _ -> failwith "expected errors"

[<Fact>]
let ``a missing input name is mapping.inputMissing before any load`` () =
    let calls = ref 0
    let counting f =
        calls.Value <- calls.Value + 1
        load f
    let files = { billingFiles with Inputs = Map.remove "team" billingFiles.Inputs }
    match Fergi.run billingMapping files counting with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mappingInputMissing ], codes es)
        Assert.Equal(0, calls.Value)
    | Ok _ -> failwith "expected error"

// (6)
[<Fact>]
let ``malformed YAML reports file and line through the whole pipeline`` () =
    match runWith (filesWith "teams/energy.yaml" "services/billing/broken.yaml") with
    | Error [ e ] ->
        Assert.Equal(ErrorCode.parse, e.Code)
        Assert.Contains("'service'", e.Message)
        Assert.Contains("services/billing/broken.yaml", e.Message)
        match e.Location with
        | Some span -> Assert.True(span.Start.Line >= 1 && span.Start.Column >= 1)
        | None -> failwith "expected a Location"
    | other -> failwithf "unexpected %A" other

// (7)
[<Fact>]
let ``a JSON team input gives the same result as the YAML one`` () =
    let mixed = filesWith "teams/energy.json" "services/billing/service.yaml"
    Assert.Equal(runOk billingFiles, runOk mixed)

// (8)
[<Fact>]
let ``Ensure failure returns ensure.failed and no document`` () =
    match runWith (filesWith "teams/energy.yaml" "services/billing/zero-replicas.yaml") with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.ensureFailed ], codes es)
        Assert.Contains("replicas", es.[0].Message)
    | Ok _ -> failwith "expected ensure failure"
