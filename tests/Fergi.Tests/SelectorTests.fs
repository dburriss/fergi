module SelectorTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode
open Fergi

let doc =
    Obj [ "name", Str "svc"
          "ports",
          Arr [ Obj [ "name", Str "http"; "port", Int 80L ]
                Obj [ "name", Str "grpc"; "port", Int 9000L ]
                Obj [ "port", Int 1L ]
                Str "scalar"
                Obj [ "name", Int 5L ] ]
          "dup", Arr [ Obj [ "name", Str "x" ]; Obj [ "name", Str "x" ] ] ]

let private parseOk s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwithf "parse failed: %s" e.Message

let private code r =
    match r with
    | Error(e: Error) -> e.Code
    | Ok _ -> "ok"

[<Fact>]
let ``parse plain pointer as Seg`` () =
    Assert.Equal<SelectorPath>([ Seg(Key "a"); Seg(Index 0); Seg(Key "b~/") ], parseOk "/a/0/b~0~1")
    Assert.Equal<SelectorPath>([], parseOk "")

[<Fact>]
let ``parse where segment`` () =
    Assert.Equal<SelectorPath>([ Seg(Key "ports"); Where("name", "http"); Seg(Key "port") ], parseOk "/ports/[name=http]/port")

[<Fact>]
let ``where value may contain equals, escapes and be empty`` () =
    Assert.Equal<SelectorPath>([ Where("a", "b=c") ], parseOk "/[a=b=c]")
    Assert.Equal<SelectorPath>([ Where("a", "x/y~") ], parseOk "/[a=x~1y~0]")
    Assert.Equal<SelectorPath>([ Where("a", "") ], parseOk "/[a=]")

[<Fact>]
let ``bracketed text without a key is a plain key`` () =
    Assert.Equal<SelectorPath>([ Seg(Key "[x]") ], parseOk "/[x]")

[<Fact>]
let ``parse errors`` () =
    Assert.Equal("path.invalid", code (Selector.parse "a/b"))
    Assert.Equal("path.invalid", code (Selector.parse "/a~2"))
    Assert.Equal("path.invalid", code (Selector.parse "/[a~2=b]"))
    // empty key
    Assert.Equal("path.invalid", code (Selector.parse "/[=x]"))

[<Fact>]
let ``print where`` () =
    Assert.Equal("/ports/[name=http]/0", Selector.print [ Seg(Key "ports"); Where("name", "http"); Seg(Index 0) ])
    Assert.Equal("/[a=x~1y~0]", Selector.print [ Where("a", "x/y~") ])
    Assert.Equal("", Selector.print [])

[<Theory>]
[<InlineData("")>]
[<InlineData("/a/b/0")>]
[<InlineData("/ports/[name=http]/port")>]
[<InlineData("/[k=v=w]/[a=~1~0]")>]
[<InlineData("//[a=]")>]
let ``parse then print round trips`` (s: string) =
    Assert.Equal(s, Selector.print (parseOk s))

let private genText =
    Gen.elements [ ""; "a"; "http"; "x/y"; "~"; "b=c"; "[z]"; "0"; " " ]

type SelArb =
    static member Sel() : Arbitrary<SelectorPath> =
        let seg =
            Gen.oneof [ Gen.map (fun k -> Seg(Key k)) (Gen.elements [ "a"; "b/c"; "~"; "[x]"; "k" ])
                        Gen.map (fun i -> Seg(Index(abs i % 100))) Arb.generate<int>
                        Gen.map2 (fun k v -> Where(k, v)) (Gen.elements [ "name"; "id"; "a/b" ]) genText ]
        Arb.fromGen (Gen.listOf seg)

[<Property(Arbitrary = [| typeof<SelArb> |])>]
let ``print then parse round trips`` (p: SelectorPath) =
    Selector.parse (Selector.print p) = Ok p

[<Fact>]
let ``resolve where to index`` () =
    Assert.Equal(Ok [ Key "ports"; Index 1; Key "port" ], Selector.resolve (parseOk "/ports/[name=grpc]/port") doc)

[<Fact>]
let ``resolve plain segments is not validated`` () =
    Assert.Equal(Ok [ Key "nope"; Key "x" ], Selector.resolve (parseOk "/nope/x") doc)

[<Fact>]
let ``resolve no match`` () =
    let r = Selector.resolve (parseOk "/ports/[name=nope]") doc
    Assert.Equal("selector.noMatch", code r)
    match r with
    | Error e -> Assert.Equal<Path>([ Key "ports" ], e.Path)
    | _ -> ()

[<Fact>]
let ``resolve where on missing parent is no match`` () =
    Assert.Equal("selector.noMatch", code (Selector.resolve (parseOk "/nope/[name=a]") doc))

[<Fact>]
let ``resolve ambiguous`` () =
    Assert.Equal("selector.ambiguous", code (Selector.resolve (parseOk "/dup/[name=x]") doc))

[<Fact>]
let ``resolve where on non-array is type mismatch`` () =
    Assert.Equal("type.mismatch", code (Selector.resolve (parseOk "/name/[a=b]") doc))
    Assert.Equal("type.mismatch", code (Selector.resolve (parseOk "/[a=b]") (Obj [])))

[<Fact>]
let ``items missing the key, non-objects and non-string values never match`` () =
    // the third item has no name, the fourth is a scalar, the fifth has name = Int 5
    Assert.Equal("selector.noMatch", code (Selector.resolve (parseOk "/ports/[name=5]") doc))
    Assert.Equal(Ok [ Key "ports"; Index 0 ], Selector.resolve (parseOk "/ports/[name=http]") doc)

[<Fact>]
let ``nested where`` () =
    let d = Obj [ "a", Arr [ Obj [ "id", Str "1"; "b", Arr [ Obj [ "id", Str "2"; "v", Null ] ] ] ] ]
    Assert.Equal(Ok [ Key "a"; Index 0; Key "b"; Index 0; Key "v" ], Selector.resolve (parseOk "/a/[id=1]/b/[id=2]/v") d)
