module PositionTests

open Xunit
open Fergi.Json

[<Fact>]
let ``root and nested spans on multi-line input`` () =
    let text = "{\n  \"a\": [10, {\"b\": \"x\"}],\n  \"c\": true\n}"
    let v, spans = JsonValue.ParseWithPositions text
    Assert.Equal(JsonValue.Parse text, v)
    let at (p: string) = spans.[p]
    // root
    Assert.Equal({ Line = 1; Column = 1; Offset = 0 }, (at "").Start)
    Assert.Equal({ Line = 4; Column = 2; Offset = text.Length }, (at "").End)
    // "a" array starts on line 2 col 8
    Assert.Equal({ Line = 2; Column = 8; Offset = 9 }, (at "/a").Start)
    Assert.Equal(2, (at "/a/0").Start.Line)
    Assert.Equal(9, (at "/a/0").Start.Column)
    Assert.Equal(11, (at "/a/0").End.Column)
    Assert.Equal("{\"b\": \"x\"}", text.Substring((at "/a/1").Start.Offset, (at "/a/1").End.Offset - (at "/a/1").Start.Offset))
    Assert.Equal("\"x\"", text.Substring((at "/a/1/b").Start.Offset, (at "/a/1/b").End.Offset - (at "/a/1/b").Start.Offset))
    Assert.Equal(3, (at "/c").Start.Line)
    Assert.Equal(8, (at "/c").Start.Column)
    Assert.Equal(12, (at "/c").End.Column)
    Assert.Equal(6, spans.Count)

[<Fact>]
let ``pointer keys escape tilde and slash`` () =
    let _, spans = JsonValue.ParseWithPositions """{"a/b":1,"m~n":2,"":3}"""
    Assert.True(spans.ContainsKey "/a~1b")
    Assert.True(spans.ContainsKey "/m~0n")
    Assert.True(spans.ContainsKey "/")

[<Fact>]
let ``duplicate keys first occurrence wins`` () =
    let text = """{"a":1,"a":22}"""
    let _, spans = JsonValue.ParseWithPositions text
    Assert.Equal(5, spans.["/a"].Start.Offset)
    let _, spans2 = JsonValue.ParseWithPositions "{\"a\":{\"x\":1},\"a\":{\"y\":2}}"
    Assert.True(spans2.ContainsKey "/a/x")
    Assert.False(spans2.ContainsKey "/a/y")

[<Fact>]
let ``crlf counts as one line break and bom is not a column`` () =
    let _, spans = JsonValue.ParseWithPositions "\uFEFF[1,\r\n 2]"
    Assert.Equal(1, spans.["/0"].Start.Line)
    Assert.Equal(2, spans.["/0"].Start.Column)
    Assert.Equal(2, spans.["/1"].Start.Line)
    Assert.Equal(2, spans.["/1"].Start.Column)

[<Fact>]
let ``positions parse errors surface through the result api`` () =
    match JsonValue.TryParseWithPositions "[1,\n  oops]" with
    | Error e ->
        Assert.Equal(2, e.Pos.Line)
        Assert.Equal(3, e.Pos.Column)
    | Ok _ -> failwith "expected error"
