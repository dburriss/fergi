module JsonFormatTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode
open Flexi.Json

let private parseOk (s: string) =
    match Json.format.Parse s with
    | Ok p -> p
    | Error e -> failwithf "unexpected error %A" e

[<Fact>]
let ``format metadata`` () =
    Assert.Equal("json", Json.format.Name)
    Assert.Equal<string list>([ ".json" ], Json.format.Extensions)

[<Fact>]
let ``json to node`` () =
    let p = parseOk """{"a":1,"b":1.5,"c":"x","d":[true,null],"e":{}}"""
    let expected =
        Obj [ "a", Int 1L; "b", Float 1.5; "c", Str "x"; "d", Arr [ Bool true; Null ]; "e", Obj [] ]
    Assert.Equal(expected, p.Node)

[<Fact>]
let ``node to json`` () =
    let n = Obj [ "a", Int 1L; "t", Timestamp "2024-01-01T00:00:00Z"; "l", Arr [ Null; Bool false ] ]
    let v = JsonConvert.ofNode n
    Assert.Equal("""{"a":1,"t":"2024-01-01T00:00:00Z","l":[null,false]}""", v.ToString())

[<Fact>]
let ``int64 extremes`` () =
    let p = parseOk "[9223372036854775807,-9223372036854775808]"
    Assert.Equal(Arr [ Int System.Int64.MaxValue; Int System.Int64.MinValue ], p.Node)
    Assert.Equal(p.Node, (parseOk (Json.format.Print p.Node)).Node)

[<Fact>]
let ``float one stays distinct from int one`` () =
    Assert.Equal(Arr [ Float 1.0; Int 1L ], (parseOk "[1.0,1]").Node)
    Assert.Contains("1.0", Json.format.Print(Float 1.0))
    Assert.Equal("1", Json.format.Print(Int 1L))

[<Fact>]
let ``key order kept`` () =
    let p = parseOk """{"z":1,"a":2,"m":3}"""
    Assert.Equal(Obj [ "z", Int 1L; "a", Int 2L; "m", Int 3L ], p.Node)
    let out = Json.format.Print p.Node
    Assert.True(out.IndexOf "\"z\"" < out.IndexOf "\"a\"" && out.IndexOf "\"a\"" < out.IndexOf "\"m\"")

[<Fact>]
let ``print is two space pretty`` () =
    Assert.Equal("{\n  \"a\": [\n    1\n  ]\n}", Json.format.Print(Obj [ "a", Arr [ Int 1L ] ]))

[<Fact>]
let ``locations for nested nodes on multiple lines`` () =
    let text = "{\n  \"a\": {\n    \"b\": [10, 20]\n  }\n}"
    let p = parseOk text
    let span (k: string) = Map.find k p.Locations
    Assert.Equal(1, (span "").Start.Line)
    Assert.Equal(1, (span "").Start.Column)
    Assert.Equal(5, (span "").End.Line)
    Assert.Equal(2, (span "").End.Column)
    let b = span "/a/b"
    Assert.Equal({ Line = 3; Column = 10 }, b.Start)
    Assert.Equal({ Line = 3; Column = 18 }, b.End)
    let second = span "/a/b/1"
    Assert.Equal({ Line = 3; Column = 15 }, second.Start)
    Assert.Equal({ Line = 3; Column = 17 }, second.End)

[<Fact>]
let ``malformed input gives parse error with position`` () =
    match Json.format.Parse "{\n  \"a\": ,\n}" with
    | Ok _ -> failwith "expected error"
    | Error [ e ] ->
        Assert.Equal("parse", e.Code)
        Assert.Equal<Path>([], e.Path)
        Assert.NotEqual<string>("", e.Message)
        let loc = e.Location.Value
        Assert.Equal(2, loc.Start.Line)
        Assert.Equal(8, loc.Start.Column)
    | Error es -> failwithf "expected one error, got %A" es

[<Fact>]
let ``empty input is an error not an exception`` () =
    match Json.format.Parse "" with
    | Error [ e ] -> Assert.Equal("parse", e.Code)
    | r -> failwithf "unexpected %A" r

let private genKey = Gen.elements [ ""; "a"; "b"; "key"; "with space"; "é"; "q\"uote"; "new\nline" ]

let private genScalar =
    Gen.oneof [ Gen.constant Null
                Gen.map Bool Arb.generate<bool>
                Gen.map Int Arb.generate<int64>
                Gen.map Float (Arb.generate<NormalFloat> |> Gen.map (fun (NormalFloat f) -> f))
                Gen.map Str (Gen.elements [ ""; "x"; "hello world"; "tab\t"; "é"; "1"; "a\\b" ]) ]

let private genNode =
    let rec go size =
        if size <= 0 then genScalar
        else
            Gen.oneof [ genScalar
                        Gen.map Arr (Gen.listOf (go (size / 3)) |> Gen.resize 4)
                        Gen.map Obj (Gen.listOf (Gen.zip genKey (go (size / 3))) |> Gen.resize 4) ]
    Gen.sized go

type NodeArb =
    static member Node() : Arbitrary<Node> = Arb.fromGen genNode

[<Property(Arbitrary = [| typeof<NodeArb> |], MaxTest = 300)>]
let ``print then parse round trips`` (n: Node) =
    match Json.format.Parse(Json.format.Print n) with
    | Ok p -> p.Node = n
    | Error _ -> false
