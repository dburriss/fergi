/// Number handling that has to hold on every target (.NET, Fable JavaScript, Fable Python): `Int of int64`
/// is exact over the whole int64 range, including beyond 2^53 where a JS number would round, and
/// finite floats round-trip bit for bit. The exact float text differs per target (`1E+21` on .NET,
/// `1e+21` on JS and Python), so these tests check values, never the spelling of exponents.
module Fnode.Conformance.NumericTests

open Xunit
open Fnode

let private formats: Format list = [ Fnode.Yaml.format; Fnode.Json.format ]

let private parseNode (f: Format) (text: string) : Node =
    match f.Parse text with
    | Ok p -> p.Node
    | Error es -> failwithf "[%s] %s: expected Ok, got %A" f.Name text es

let private sameBits (a: float) (b: float) =
    System.BitConverter.DoubleToInt64Bits a = System.BitConverter.DoubleToInt64Bits b

[<Fact>]
let ``int64 values beyond 2^53 parse and round trip exactly`` () =
    let values =
        [ 9007199254740991L; 9007199254740992L; 9007199254740993L; -9007199254740993L
          1234567890123456789L; 9223372036854775806L; System.Int64.MaxValue; System.Int64.MinValue
          -9223372036854775807L ]
    for f in formats do
        for v in values do
            Assert.Equal(Int v, parseNode f (string v))
            Assert.Equal(Int v, parseNode f (f.Print(Int v)))

[<Fact>]
let ``int64 values inside a document round trip exactly`` () =
    let n = Obj [ "min", Int System.Int64.MinValue; "max", Int System.Int64.MaxValue; "odd", Arr [ Int 9007199254740993L ] ]
    for f in formats do
        Assert.Equal(n, parseNode f (f.Print n))

[<Fact>]
let ``integers outside int64 become the nearest Float`` () =
    for f in formats do
        Assert.Equal(Float 9223372036854775808.0, parseNode f "9223372036854775808")
        Assert.Equal(Float -9223372036854775808.0, parseNode f "-9223372036854775809")
        Assert.Equal(Float 18446744073709551616.0, parseNode f "18446744073709551616")

[<Fact>]
let ``finite float extremes round trip bit for bit as Float`` () =
    let values =
        [ 1.0; -0.0; 0.0; 0.1; 1.0 / 3.0; 1e15; 1e16; 1e20; 1e21; -1e21; 1e-5; 1e-7; 5e-324; -5e-324
          2.2250738585072014e-308; 1.7976931348623157e308; -1.7976931348623157e308; 9007199254740993.0
          123456789012345678.0; 4.35 ]
    for f in formats do
        for v in values do
            match parseNode f (f.Print(Float v)) with
            | Float back -> Assert.True(sameBits v back, sprintf "[%s] %.17g came back as %.17g" f.Name v back)
            | other -> failwithf "[%s] Float %g came back as %A" f.Name v other

[<Fact>]
let ``float notation in JSON and YAML parses to Float`` () =
    for f in formats do
        Assert.Equal(Float 0.1, parseNode f "0.1")
        Assert.Equal(Float 1e21, parseNode f "1.0e21")
        Assert.Equal(Float 5e-324, parseNode f "5e-324")
        Assert.Equal(Float 1.7976931348623157e308, parseNode f "1.7976931348623157e308")
        match parseNode f "-0.0" with
        | Float z -> Assert.True(sameBits -0.0 z, "negative zero keeps its sign")
        | other -> failwithf "[%s] -0.0 parsed as %A" f.Name other

[<Fact>]
let ``JSON number out of double range is a parse error`` () =
    match Fnode.Json.format.Parse "1e400" with
    | Error es -> Assert.NotEmpty es
    | Ok p -> failwithf "expected an error, got %A" p.Node
