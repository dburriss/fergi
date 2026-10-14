module AccessorTests

open Xunit
open Fergi.Json
open Fergi.Json.JsonExtensions
open Fergi.Json.JsonBuilders

let doc = JsonValue.Parse """{"user":{"name":"ann","age":30,"tags":["a","b"],"score":1.5},"ok":true}"""

[<Fact>]
let ``dynamic operator and accessors`` () =
    Assert.Equal("ann", doc?user?name.AsString())
    Assert.Equal(30, doc?user?age.AsInteger())
    Assert.Equal(30L, doc?user?age.AsInteger64())
    Assert.Equal(1.5, doc?user?score.AsFloat())
    Assert.Equal(30.0, doc?user?age.AsFloat())
    Assert.True(doc?ok.AsBoolean())
    Assert.Equal("b", doc?user?tags.[1].AsString())
    Assert.Equal("ann", doc.["user"].["name"].AsString())
    Assert.Equal(2, doc?user?tags.AsArray().Length)

[<Fact>]
let ``try accessors`` () =
    Assert.True((doc.TryGetProperty "nope").IsNone)
    Assert.True((doc?user?tags.TryGetItem 5).IsNone)
    Assert.Equal(Some(JsonValue.String "b"), doc.TryGetPointer "/user/tags/1")
    Assert.True((doc.TryGetPointer "/user/tags/01").IsNone)
    Assert.Equal(Some doc, doc.TryGetPointer "")

[<Fact>]
let ``wrong shape raises`` () =
    Assert.ThrowsAny<exn>(fun () -> doc?user?name.AsInteger() |> ignore) |> ignore
    Assert.ThrowsAny<exn>(fun () -> doc?missing |> ignore) |> ignore

[<Fact>]
let ``immutable edits`` () =
    let d2 = doc.SetProperty("ok", JsonValue.Boolean false).SetProperty("extra", JsonValue.Null)
    Assert.True(doc?ok.AsBoolean())
    Assert.False(d2?ok.AsBoolean())
    let names = d2.Properties() |> Array.map fst
    Assert.Equal<string[]>([| "user"; "ok"; "extra" |], names)
    let d3 = d2.RemoveProperty "user"
    Assert.Equal<string[]>([| "ok"; "extra" |], d3.Properties() |> Array.map fst)
    let arr = JsonValue.Parse "[1,2,3]"
    Assert.Equal("[1,9,3]", arr.SetItem(1, JsonValue.Integer 9L).ToString())
    Assert.Equal("[1,3]", arr.RemoveItem(1).ToString())
    Assert.Equal("[1,2,3,4]", arr.Append(JsonValue.Integer 4L).ToString())
