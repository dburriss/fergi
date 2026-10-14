module Fnode.Conformance.ConformanceTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode

/// Every adapter under test. Add new formats here.
let formats: Format list = [ Fnode.Yaml.format; Fnode.Json.format ]

let private yaml = Fnode.Yaml.format
let private json = Fnode.Json.format

/// xunit theory data: one row per format.
type FormatData() =
    interface seq<obj[]> with
        member _.GetEnumerator() : System.Collections.Generic.IEnumerator<obj[]> =
            (formats |> Seq.map (fun f -> [| box f.Name |])).GetEnumerator()
        member _.GetEnumerator() : System.Collections.IEnumerator =
            ((formats |> Seq.map (fun f -> [| box f.Name |])) :> System.Collections.IEnumerable).GetEnumerator()

let private byName (name: string) = formats |> List.find (fun f -> f.Name = name)

let private parseOk (f: Format) (text: string) : Node =
    match f.Parse text with
    | Ok p -> p.Node
    | Error es -> failwithf "[%s] expected Ok for %A, got %A" f.Name text es

let private roundTrip (f: Format) (n: Node) = f.Parse(f.Print n) |> Result.map (fun p -> p.Node)

let private assertRoundTrips (f: Format) (n: Node) =
    match roundTrip f n with
    | Ok n2 -> Assert.True((n = n2), sprintf "[%s] %A -> %s -> %A" f.Name n (f.Print n) n2)
    | Error es -> failwithf "[%s] %A printed as %s failed to parse: %A" f.Name n (f.Print n) es

/// Shared documents (rows of name, node). All avoid per-adapter limitations.
let private cases: (string * Node) list =
    [ "null", Null
      "true", Bool true
      "false", Bool false
      "int zero", Int 0L
      "int positive", Int 42L
      "int negative", Int -7L
      "int64 max", Int System.Int64.MaxValue
      "int64 min", Int System.Int64.MinValue
      "float fraction", Float 1.5
      "float negative", Float -2.25
      "float whole", Float 1.0
      "float small", Float 0.001
      "string plain", Str "hello"
      "string empty", Str ""
      "string spaces", Str "  padded  "
      "string looks like int", Str "123"
      "string looks like bool", Str "true"
      "string looks like null", Str "null"
      "string looks like float", Str "1.5"
      "string multiline", Str "line1\nline2"
      "string quotes", Str "say \"hi\" and 'bye'"
      "string backslash", Str "a\\b"
      "string tab", Str "a\tb"
      "string unicode", Str "héllo 世界"
      "string emoji", Str "party \U0001F389"
      "empty array", Arr []
      "empty object", Obj []
      "array of scalars", Arr [ Int 1L; Str "two"; Bool true; Null; Float 2.5 ]
      "nested arrays", Arr [ Arr [ Int 1L; Arr [] ]; Arr []; Arr [ Arr [ Null ] ] ]
      "nested objects", Obj [ "a", Obj [ "b", Obj [ "c", Int 1L ] ]; "d", Obj [] ]
      "mixed nesting", Obj [ "list", Arr [ Obj [ "x", Int 1L ]; Obj [ "x", Int 2L ] ]; "o", Obj [ "l", Arr [ Str "s" ] ] ]
      "key order is not alphabetical", Obj [ "z", Int 1L; "a", Int 2L; "m", Int 3L ]
      "numeric-looking keys", Obj [ "10", Int 1L; "2", Int 2L; "true", Int 3L; "null", Int 4L ]
      "unicode keys", Obj [ "kéy", Int 1L; "世界", Str "x" ]
      "empty key", Obj [ "", Int 1L ]
      "key with spaces and colon", Obj [ "a b: c", Int 1L ] ]

[<Theory; ClassData(typeof<FormatData>)>]
let ``Print then Parse equals the original for shared cases`` (name: string) =
    let f = byName name
    for (_, n) in cases do
        assertRoundTrips f n

[<Theory; ClassData(typeof<FormatData>)>]
let ``Parse Print Parse is stable`` (name: string) =
    let f = byName name
    for (label, n) in cases do
        let n1 = parseOk f (f.Print n)
        let n2 = parseOk f (f.Print n1)
        Assert.True((n1 = n2), sprintf "[%s] %s: %A vs %A" name label n1 n2)
        Assert.Equal<string>(f.Print n1, f.Print n2)

[<Theory; ClassData(typeof<FormatData>)>]
let ``Print is deterministic`` (name: string) =
    let f = byName name
    for (_, n) in cases do
        Assert.Equal<string>(f.Print n, f.Print n)

[<Theory; ClassData(typeof<FormatData>)>]
let ``key order is preserved`` (name: string) =
    let f = byName name
    match parseOk f (f.Print(Obj [ "z", Int 1L; "a", Int 2L; "m", Obj [ "y", Null; "b", Null ] ])) with
    | Obj [ ("z", _); ("a", _); ("m", Obj [ ("y", _); ("b", _) ]) ] -> ()
    | other -> failwithf "[%s] key order lost: %A" name other

[<Theory; ClassData(typeof<FormatData>)>]
let ``array order is preserved`` (name: string) =
    let f = byName name
    let n = Arr [ Int 3L; Int 1L; Int 2L ]
    Assert.Equal(Ok n, roundTrip f n)

[<Theory; ClassData(typeof<FormatData>)>]
let ``int64 extremes stay Int`` (name: string) =
    let f = byName name
    Assert.Equal(Int System.Int64.MaxValue, parseOk f "9223372036854775807")
    Assert.Equal(Int System.Int64.MinValue, parseOk f "-9223372036854775808")
    Assert.Equal(Int 0L, parseOk f "0")
    Assert.Equal(Int -1L, parseOk f "-1")

[<Theory; ClassData(typeof<FormatData>)>]
let ``1 is Int and 1.0 is Float`` (name: string) =
    let f = byName name
    Assert.Equal(Int 1L, parseOk f "1")
    Assert.Equal(Float 1.0, parseOk f "1.0")
    Assert.Equal(Float 1.5, parseOk f "1.5")
    Assert.Equal(Float -0.5, parseOk f "-0.5")

[<Theory; ClassData(typeof<FormatData>)>]
let ``Float 1.0 and Int 1 stay distinct through Print and Parse`` (name: string) =
    let f = byName name
    Assert.Equal(Ok(Float 1.0), roundTrip f (Float 1.0))
    Assert.Equal(Ok(Int 1L), roundTrip f (Int 1L))
    Assert.Equal(Ok(Arr [ Int 1L; Float 1.0 ]), roundTrip f (Arr [ Int 1L; Float 1.0 ]))

[<Theory; ClassData(typeof<FormatData>)>]
let ``scalar literals parse to the shared Node cases`` (name: string) =
    let f = byName name
    Assert.Equal(Null, parseOk f "null")
    Assert.Equal(Bool true, parseOk f "true")
    Assert.Equal(Bool false, parseOk f "false")
    Assert.Equal(Str "hi", parseOk f "\"hi\"")

[<Theory; ClassData(typeof<FormatData>)>]
let ``flow syntax shared by YAML and JSON parses to the same Node`` (name: string) =
    let f = byName name
    let expected = Obj [ "a", Arr [ Int 1L; Float 2.5; Null; Bool true ]; "b", Obj [ "c", Str "d" ]; "e", Arr []; "f", Obj [] ]
    Assert.Equal(expected, parseOk f """{"a": [1, 2.5, null, true], "b": {"c": "d"}, "e": [], "f": {}}""")

[<Theory; ClassData(typeof<FormatData>)>]
let ``unicode escapes parse the same`` (name: string) =
    let f = byName name
    Assert.Equal(Str "\u00e9\u4e16", parseOk f "\"\\u00e9\\u4e16\"")

/// JSON writes non-BMP characters as a \u surrogate pair; both parsers decode it to one character.
[<Theory; ClassData(typeof<FormatData>)>]
let ``surrogate-pair escape parses the same`` (name: string) =
    let f = byName name
    Assert.Equal(Str "\U0001F389", parseOk f "\"\\ud83c\\udf89\"")

[<Theory; ClassData(typeof<FormatData>)>]
let ``malformed input is a parse error with a message, not an exception`` (name: string) =
    let f = byName name
    for text in [ "{\"a\": "; "[1, 2"; "{\"a\" 1}"; "{\"a\": [1, }" ] do
        match f.Parse text with
        | Ok p -> failwithf "[%s] expected error for %A, got %A" name text p.Node
        | Error es ->
            Assert.NotEmpty es
            for e in es do
                Assert.Equal(ErrorCode.parse, e.Code)
                Assert.False(System.String.IsNullOrWhiteSpace e.Message)

[<Fact>]
let ``JSON parse errors carry a 1-based Location`` () =
    match Fnode.Json.format.Parse "{\n  \"a\": 1,\n  \"b\": }" with
    | Ok _ -> failwith "expected error"
    | Error es ->
        let e = List.head es
        Assert.Equal(ErrorCode.parse, e.Code)
        match e.Location with
        | None -> failwith "Location missing"
        | Some s ->
            Assert.Equal(3, s.Start.Line)
            Assert.True(s.Start.Column >= 1)

[<Fact>]
let ``JSON success carries Locations for the root`` () =
    match Fnode.Json.format.Parse "{\"a\": 1}" with
    | Ok p -> Assert.False(Map.isEmpty p.Locations)
    | Error es -> failwithf "%A" es

// --- Property over generated Nodes -----------------------------------------------------------

/// Excludes: Timestamp, NaN/inf, lone surrogates, and duplicate keys within one object.
let private genStr: Gen<string> =
    Gen.oneof
        [ Arb.generate<string> |> Gen.map (fun s -> if isNull s then "" else s)
          Gen.elements [ ""; "a"; "1"; "true"; "null"; " "; "a: b"; "#x"; "\n"; "é\U0001F389"; "- x"; "[a]"; "{a}"; "<<"; "..."; "---" ] ]
    |> Gen.map (fun s -> System.String(s.ToCharArray() |> Array.filter (fun c -> not (System.Char.IsSurrogate c))))

let private genFiniteFloat: Gen<float> =
    let bits =
        Arb.generate<int64>
        |> Gen.map System.BitConverter.Int64BitsToDouble
        |> Gen.filter System.Double.IsFinite
    let special =
        Gen.elements
            [ 0.1; 1.0 / 3.0; 2.0 / 3.0; 1e-7; 5e-324; System.Double.Epsilon; System.Double.MaxValue
              System.Double.MinValue; System.Math.PI; System.Math.E; -0.0; 0.0; 1e21; 1e-5; 123456.789e10
              4.35; 0.3; 1e300; 2.2250738585072014e-308; 9007199254740993.0 ]
    let signed = Gen.oneof [ special; special |> Gen.map (fun f -> -f) ]
    Gen.frequency [ 5, bits; 2, signed; 1, Gen.choose (-1000000, 1000000) |> Gen.map (fun i -> float i / 8.0) ]

let private sameBits (a: float) (b: float) =
    System.BitConverter.DoubleToInt64Bits a = System.BitConverter.DoubleToInt64Bits b

let private genNode: Gen<Node> =
    let rec go size =
        let leaf =
            Gen.oneof
                [ Gen.constant Null
                  Arb.generate<bool> |> Gen.map Bool
                  Arb.generate<int64> |> Gen.map Int
                  Gen.elements [ System.Int64.MaxValue; System.Int64.MinValue; 0L ] |> Gen.map Int
                  Gen.choose (-1000000, 1000000) |> Gen.map (fun i -> Float(float i / 8.0))
                  Gen.choose (-1000, 1000) |> Gen.map (fun i -> Float(float i))
                  Gen.elements [ -0.0; 1e15; -1e15; 1e16; 1e20 ] |> Gen.map Float
                  genFiniteFloat |> Gen.map Float
                  genFiniteFloat |> Gen.map Float
                  genStr |> Gen.map Str ]
        if size <= 0 then leaf
        else
            let sub = go (size / 2)
            Gen.frequency
                [ 4, leaf
                  1, Gen.listOf sub |> Gen.resize 3 |> Gen.map Arr
                  1,
                  Gen.listOf (Gen.zip genStr sub)
                  |> Gen.resize 3
                  |> Gen.map (List.distinctBy fst >> Obj) ]
    Gen.sized (fun s -> go (min s 12))

type Arbs =
    static member Node() = Arb.fromGen genNode

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 500)>]
let ``property: Print then Parse is identity for every format`` (n: Node) =
    formats |> List.forall (fun f -> roundTrip f n = Ok n)

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 300)>]
let ``property: YAML and JSON agree on the Node after a round trip`` (n: Node) =
    roundTrip yaml n = roundTrip json n

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 300)>]
let ``property: Parse Print Parse is stable for every format`` (n: Node) =
    formats
    |> List.forall (fun f ->
        match f.Parse(f.Print n) with
        | Error _ -> false
        | Ok p1 ->
            match f.Parse(f.Print p1.Node) with
            | Ok p2 -> p1.Node = p2.Node
            | Error _ -> false)

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 200)>]
let ``property: a document printed by one format parses in the other when it is JSON-shaped`` (n: Node) =
    // JSON text is valid YAML (flow style), so the JSON printout must parse to the same Node via YAML.
    match yaml.Parse(json.Print n) with
    | Ok p -> p.Node = n
    | Error _ -> false

type FloatArbs =
    static member Float() = Arb.fromGen (genFiniteFloat |> Gen.map Float)

let private floatBitsRoundTrip (f: Format) (n: Node) =
    match n with
    | Float x ->
        match f.Parse(f.Print n) with
        | Ok p -> (match p.Node with Float y -> sameBits x y | _ -> false)
        | Error _ -> false
    | _ -> true

[<Property(Arbitrary = [| typeof<FloatArbs> |], MaxTest = 2000)>]
let ``property: YAML finite Float round trips as Float with identical bits`` (n: Node) =
    floatBitsRoundTrip yaml n

[<Property(Arbitrary = [| typeof<FloatArbs> |], MaxTest = 2000)>]
let ``property: JSON finite Float round trips as Float with identical bits`` (n: Node) =
    floatBitsRoundTrip json n
