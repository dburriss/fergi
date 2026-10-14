module PrinterTests

open Xunit
open Fergi.Json

[<Fact>]
let ``compact output`` () =
    let v = JsonValue.Parse """ { "a" : [1, 2.5, true, null], "b": {} , "c": [] } """
    Assert.Equal("""{"a":[1,2.5,true,null],"b":{},"c":[]}""", v.ToString())

[<Fact>]
let ``pretty output uses two spaces`` () =
    let v = JsonValue.Parse """{"a":[1,{"x":null}],"b":{},"c":[]}"""
    let expected = "{\n  \"a\": [\n    1,\n    {\n      \"x\": null\n    }\n  ],\n  \"b\": {},\n  \"c\": []\n}"
    Assert.Equal(expected, v.ToString(true))

[<Fact>]
let ``floats keep float shape`` () =
    Assert.Equal("1.0", JsonValue.Float(1.0).ToString())
    Assert.Equal("-0.0", JsonValue.Float(-0.0).ToString())
    Assert.Equal("0.1", JsonValue.Float(0.1).ToString())
    Assert.Equal("1", JsonValue.Integer(1L).ToString())
    Assert.Equal("null", JsonValue.Float(nan).ToString())

[<Fact>]
let ``string escaping`` () =
    let s = "q\" b\\ n\n t\t c\001 f\012 \u007F \u00e9 \U0001D11E"
    let text = JsonValue.String(s).ToString()
    Assert.Equal("\"q\\\" b\\\\ n\\n t\\t c\\u0001 f\\f \u007F \u00e9 \U0001D11E\"", text)
    Assert.Equal(JsonValue.String s, JsonValue.Parse text)

[<Fact>]
let ``lone surrogate is escaped on output`` () =
    Assert.Equal("\"\\ud800\"", JsonValue.String(string (char 0xD800)).ToString())
