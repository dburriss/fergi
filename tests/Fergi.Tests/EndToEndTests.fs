/// End to end: the IDP defaults-plus-overrides case with real files on disk, parsed through the
/// real Fnode.Yaml / Fnode.Json adapters. The loader below is test-side IO; core has none.
module EndToEndTests

#if !FABLE_COMPILER
open System.IO
#endif
open Xunit
open Fnode
open Fergi

#if !FABLE_COMPILER
let private dataDir = Path.Combine(System.AppContext.BaseDirectory, "data")
#endif

/// Fixture text by relative path: from disk on .NET, from the embedded `Fixtures` module (generated
/// by scripts/fable-gen-runner.fsx) under Fable, which has no file system access.
let private readFixture (file: string) : string option =
#if FABLE_COMPILER
    Fixtures.tryFind file
#else
    let full = Path.Combine(dataDir, file)
    if File.Exists full then Some(File.ReadAllText full) else None
#endif

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

/// Loader: path relative to the data dir -> Parsed. The adapter's `Result<Parsed, Error list>` passes
/// straight through, so every parse error and every Location survives.
let private loadParsed (file: string) : Result<Parsed, Error list> =
    match readFixture file with
    | None -> Error [ Error.create "load.fileNotFound" ("File not found: " + file) [] ]
    | Some text ->
        match detect file with
        | None -> Error [ Error.create "load.unknownFormat" ("No format for: " + file) [] ]
        | Some fmt -> fmt.Parse text

let private load (file: string) : Result<Parsed, Error list> = loadParsed file

let private mustLoad file =
    match load file with
    | Ok p -> p.Node
    | Error es -> failwithf "%s: %A" file (es |> List.map (fun e -> e.Message))

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
    let staleLoad f = if f = billingFiles.Output then Ok { Node = stale; Locations = Map.empty } else load f
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
let ``the ByKey conflict names the file and points into it`` () =
    let file = "services/billing/bad-port.yaml"
    match runWith (filesWith "teams/energy.yaml" file) with
    | Error es ->
        let e = es |> List.find (fun x -> x.Code = ErrorCodes.mergeConflict)
        Assert.Contains("input 'service' (" + file + ")", e.Message)
        match e.Location with
        | Some span ->
            // `ports:` is on line 3 of bad-port.yaml; its value (the `- port` item) on line 4
            Assert.True(span.Start.Line = 3 || span.Start.Line = 4)
            Assert.Contains("at " + string span.Start.Line + ":" + string span.Start.Column, e.Message)
        | None -> failwith "expected a Location"
    | Ok _ -> failwith "expected conflict"

[<Fact>]
let ``a conflict at a pointer with no recorded span leaves Location None`` () =
    // the loader drops all Locations, so the conflict cannot be tied to a position
    let noSpans f = load f |> Result.map (fun p -> { p with Locations = Map.empty })
    match Fergi.run billingMapping (filesWith "teams/energy.yaml" "services/billing/bad-port.yaml") noSpans with
    | Error es ->
        let e = es |> List.find (fun x -> x.Code = ErrorCodes.mergeConflict)
        Assert.Equal(None, e.Location)
        Assert.DoesNotContain("(services/billing/bad-port.yaml)", e.Message)
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

[<Fact>]
let ``two broken inputs accumulate errors with their own file prefixes`` () =
    match runWith (filesWith "teams/nope.yaml" "services/billing/broken.yaml") with
    | Error es ->
        Assert.True(List.length es >= 2)
        Assert.Contains("'team'", es.[0].Message)
        Assert.Equal("load.fileNotFound", es.[0].Code)
        Assert.True(es |> List.exists (fun e -> e.Code = ErrorCode.parse && e.Message.Contains "'service' (services/billing/broken.yaml)"))
    | Ok _ -> failwith "expected errors"

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
