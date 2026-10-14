module ParserTests

open Xunit
open Fergi.Json

let parseError (text: string) : JsonParseError =
    match JsonValue.TryParseResult text with
    | Error e -> e
    | Ok v -> failwith ("expected failure, got " + v.ToString())

[<Fact>]
let ``scalars`` () =
    Assert.Equal(JsonValue.Null, JsonValue.Parse "null")
    Assert.Equal(JsonValue.Boolean true, JsonValue.Parse " true ")
    Assert.Equal(JsonValue.Integer 42L, JsonValue.Parse "42")
    Assert.Equal(JsonValue.Integer -7L, JsonValue.Parse "-7")
    Assert.Equal(JsonValue.Float 1.0, JsonValue.Parse "1.0")
    Assert.Equal(JsonValue.Float 1500.0, JsonValue.Parse "1.5e3")
    Assert.Equal(JsonValue.String "a\nb", JsonValue.Parse "\"a\\nb\"")

[<Fact>]
let ``int64 limits stay integers and overflow becomes float`` () =
    Assert.Equal(JsonValue.Integer System.Int64.MaxValue, JsonValue.Parse "9223372036854775807")
    Assert.Equal(JsonValue.Integer System.Int64.MinValue, JsonValue.Parse "-9223372036854775808")
    Assert.Equal(JsonValue.Float 9223372036854775808.0, JsonValue.Parse "9223372036854775808")

[<Fact>]
let ``surrogate pair escape decodes`` () =
    Assert.Equal(JsonValue.String "\U0001D11E", JsonValue.Parse "\"\\uD834\\uDD1E\"")

[<Fact>]
let ``bom is tolerated`` () =
    Assert.Equal(JsonValue.Integer 1L, JsonValue.Parse "\uFEFF1")

[<Fact>]
let ``records keep order and duplicates`` () =
    match JsonValue.Parse """{"b":1,"a":2,"b":3}""" with
    | JsonValue.Record [| ("b", JsonValue.Integer 1L); ("a", _); ("b", JsonValue.Integer 3L) |] -> ()
    | v -> failwith (v.ToString())

[<Theory>]
[<InlineData("[1,]")>]
[<InlineData("{\"a\":1,}")>]
[<InlineData("// c\n1")>]
[<InlineData("01")>]
[<InlineData("+1")>]
[<InlineData(".5")>]
[<InlineData("1.")>]
[<InlineData("\"\\uD800\"")>]
[<InlineData("\"\\uDC00\"")>]
[<InlineData("'a'")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("[1] x")>]
let ``strict rejections`` (text: string) =
    Assert.True((JsonValue.TryParse text).IsNone)

[<Fact>]
let ``control character in string is rejected`` () =
    Assert.True((JsonValue.TryParse "\"a\tb\"").IsNone)

[<Fact>]
let ``error carries line and column`` () =
    let e = parseError "{\n  \"a\": 1,\n  \"b\": ?\n}"
    Assert.Equal(3, e.Pos.Line)
    Assert.Equal(8, e.Pos.Column)
    let ex = Assert.Throws<JsonParseException>(fun () -> JsonValue.Parse "[1,\n 2,\n x]" |> ignore)
    Assert.Equal(3, ex.Line)
    Assert.Equal(2, ex.Column)
    Assert.Contains("line 3, column 2", ex.Message)

[<Fact>]
let ``depth limit rejects deep nesting without overflowing`` () =
    let deep = System.String('[', 100000)
    Assert.True((JsonValue.TryParse deep).IsNone)
    let ok = String.replicate 100 "[" + String.replicate 100 "]"
    Assert.True((JsonValue.TryParse ok).IsSome)
    Assert.True((JsonValue.TryParse(ok, 50)).IsNone)
    Assert.True((JsonValue.TryParse(ok, 100)).IsSome)
