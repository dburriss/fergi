module DocTests

open Xunit
open Fnode
open Fergi

let doc =
    Obj [ "name", Str "svc"
          "ports", Arr [ Obj [ "name", Str "http"; "port", Int 80L ]; Obj [ "name", Str "grpc"; "port", Int 9000L ] ] ]

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private code r =
    match r with
    | Error(e: Error) -> e.Code
    | Ok _ -> "ok"

[<Fact>]
let ``get through a selector`` () =
    Assert.Equal(Ok(Int 9000L), Doc.get (sel "/ports/[name=grpc]/port") doc)
    Assert.Equal(Ok doc, Doc.get [] doc)

[<Fact>]
let ``get reports why it failed`` () =
    Assert.Equal("path.notFound", code (Doc.get (sel "/missing") doc))
    Assert.Equal("selector.noMatch", code (Doc.get (sel "/ports/[name=x]") doc))
    Assert.Equal("type.mismatch", code (Doc.get (sel "/name/[a=b]") doc))

[<Fact>]
let ``set through a selector`` () =
    let r = Doc.set (sel "/ports/[name=http]/port") (Int 8080L) doc
    Assert.Equal(Ok(Int 8080L), r |> Result.bind (Doc.get (sel "/ports/[name=http]/port")))
    Assert.Equal(Ok(Int 9000L), r |> Result.bind (Doc.get (sel "/ports/1/port")))

[<Fact>]
let ``set can add a new key`` () =
    let r = Doc.set (sel "/ports/[name=http]/proto") (Str "tcp") doc
    Assert.Equal(Ok(Str "tcp"), r |> Result.bind (Doc.get (sel "/ports/0/proto")))

[<Fact>]
let ``set on a missing where leaves error`` () =
    Assert.Equal("selector.noMatch", code (Doc.set (sel "/ports/[name=x]/port") Null doc))

[<Fact>]
let ``remove through a selector shifts the array`` () =
    let r = Doc.remove (sel "/ports/[name=http]") doc
    Assert.Equal(Ok(Str "grpc"), r |> Result.bind (Doc.get (sel "/ports/0/name")))
    Assert.Equal("path.notFound", code (r |> Result.bind (Doc.get (sel "/ports/1"))))

[<Fact>]
let ``remove a key and the root`` () =
    Assert.Equal(Ok(Obj [ "name", Str "svc" ]), Doc.remove (sel "/ports") doc)
    Assert.Equal("path.invalid", code (Doc.remove [] doc))
