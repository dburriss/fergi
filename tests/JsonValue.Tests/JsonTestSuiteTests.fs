/// Runs the vendored JSON Test Suite (https://github.com/nst/JSONTestSuite, MIT, see
/// data/JSONTestSuite.LICENSE). y_ files must be accepted, n_ files rejected, i_ files
/// (implementation-defined) are only reported.
module JsonTestSuiteTests

open System
open System.IO
open System.Text
open Xunit
open Xunit.Abstractions
open Fergi.Json

let private dir = Path.Combine(AppContext.BaseDirectory, "data", "test_parsing")

let private names (prefix: string) : seq<obj[]> =
    Directory.GetFiles(dir, prefix + "*.json")
    |> Array.map Path.GetFileName
    |> Array.sort
    |> Seq.map (fun n -> [| box n |])

let yFiles () = names "y_"
let nFiles () = names "n_"
let iFiles () = names "i_"

type Outcome =
    | Accepted
    | Rejected of string

/// Bytes are decoded as strict UTF-8; invalid UTF-8 is a rejection. A parse error is a
/// rejection. Any other exception (crash, stack overflow guard failure) propagates and fails.
let private run (name: string) : Outcome =
    let bytes = File.ReadAllBytes(Path.Combine(dir, name))
    let text =
        try
            Ok((UTF8Encoding(false, true)).GetString bytes)
        with :? DecoderFallbackException as e ->
            Error("invalid UTF-8: " + e.Message)
    match text with
    | Error m -> Rejected m
    | Ok t ->
        match JsonValue.TryParseResult t with
        | Ok v ->
            // accepted values must also print and re-parse to themselves
            Assert.Equal(v, JsonValue.Parse(v.ToString()))
            Accepted
        | Error e -> Rejected(e.Message + " @" + string e.Pos.Line + ":" + string e.Pos.Column)

[<Theory>]
[<MemberData(nameof yFiles)>]
let ``y_ files are accepted`` (name: string) =
    match run name with
    | Accepted -> ()
    | Rejected m -> failwith (name + " was rejected: " + m)

[<Theory>]
[<MemberData(nameof nFiles)>]
let ``n_ files are rejected`` (name: string) =
    match run name with
    | Rejected _ -> ()
    | Accepted -> failwith (name + " was accepted")

type ImplementationDefined(output: ITestOutputHelper) =
    [<Fact>]
    member _.``i_ files are reported without assertions``() =
        for row in iFiles () do
            let name = string row.[0]
            let r =
                match run name with
                | Accepted -> "ACCEPTED"
                | Rejected m -> "rejected (" + m + ")"
            output.WriteLine(name + ": " + r)

[<Fact>]
let ``suite is present`` () =
    Assert.True(Seq.length (yFiles ()) >= 90)
    Assert.True(Seq.length (nFiles ()) >= 180)
