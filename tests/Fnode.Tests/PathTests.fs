module PathTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode

let private isDigits (s: string) = s <> "" && s |> Seq.forall (fun c -> c >= '0' && c <= '9')

let private genKey =
    Gen.elements [ ""; "a"; "/"; "~"; "~0"; "~1"; "a/b"; "m~n"; " "; "é"; "x y"; "01"; "-"; "a~/b" ]

let private genSeg =
    Gen.oneof [ Gen.map Key genKey
                Gen.map Index (Gen.choose (0, 1000)) ]

let private genPath = Gen.listOf genSeg

type PathArb =
    static member Path() : Arbitrary<Path> = Arb.fromGen genPath

[<Property(Arbitrary = [| typeof<PathArb> |])>]
let ``parse (print p) = p`` (p: Path) = Path.parse (Path.print p) = Ok p

[<Property>]
let ``parse (print p) = p for arbitrary non-numeric string keys`` (keys: NonNull<string> list) =
    let p = keys |> List.map (fun (NonNull k) -> k) |> List.filter (isDigits >> not) |> List.map Key
    Path.parse (Path.print p) = Ok p

// RFC 6901 section 5 examples: pointer, segments
[<Fact>]
let ``RFC 6901 examples`` () =
    let cases =
        [ "", []
          "/foo", [ Key "foo" ]
          "/foo/0", [ Key "foo"; Index 0 ]
          "/", [ Key "" ]
          "/a~1b", [ Key "a/b" ]
          "/c%d", [ Key "c%d" ]
          "/e^f", [ Key "e^f" ]
          "/g|h", [ Key "g|h" ]
          "/i\\j", [ Key "i\\j" ]
          "/k\"l", [ Key "k\"l" ]
          "/ ", [ Key " " ]
          "/m~0n", [ Key "m~n" ] ]
    for (ptr, expected) in cases do
        Assert.Equal(Ok expected, Path.parse ptr)
        Assert.Equal(ptr, Path.print expected)

[<Fact>]
let ``root is empty string`` () =
    Assert.Equal(Ok [], Path.parse "")
    Assert.Equal("", Path.print [])

[<Fact>]
let ``digit-only segment is Index, leading zero is Key`` () =
    Assert.Equal(Ok [ Index 0; Index 12; Key "01"; Key "-1" ], Path.parse "/0/12/01/-1")

[<Fact>]
let ``numeric object key is ambiguous: print (Key "5") parses as Index`` () =
    Assert.Equal(Ok [ Index 5 ], Path.parse (Path.print [ Key "5" ]))

[<Fact>]
let ``missing leading slash and bad escapes are path.invalid`` () =
    for bad in [ "foo"; "/a~"; "/a~2"; "/~x" ] do
        match Path.parse bad with
        | Error e -> Assert.Equal(ErrorCode.pathInvalid, e.Code)
        | Ok _ -> failwith ("expected error for " + bad)

[<Fact>]
let ``segment escape round trip`` () =
    Assert.Equal("a~1b~0c", Path.escapeSegment "a/b~c")
    Assert.Equal(Some "a/b~c", Path.unescapeSegment "a~1b~0c")
    Assert.Equal(None, Path.unescapeSegment "a~2")
    Assert.Equal(None, Path.unescapeSegment "a~")

[<Fact>]
let ``parseSegment key or index`` () =
    Assert.Equal(Ok(Key "a/b"), Path.parseSegment "a~1b")
    Assert.Equal(Ok(Index 3), Path.parseSegment "3")
    Assert.Equal(Ok(Key "03"), Path.parseSegment "03")
    match Path.parseSegment "~x" with
    | Error e -> Assert.Equal("path.invalid", e.Code)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``resolve turns numeric keys into indexes and back`` () =
    let doc = Obj [ "a", Arr [ Str "x"; Str "y" ]; "o", Obj [ "0", Str "z" ] ]
    Assert.True(([ Key "a"; Index 1 ] = Path.resolve doc [ Key "a"; Key "1" ]))
    Assert.True(([ Key "o"; Key "0" ] = Path.resolve doc [ Key "o"; Index 0 ]))
    // missing steps are left alone
    Assert.True(([ Key "nope"; Key "1" ] = Path.resolve doc [ Key "nope"; Key "1" ]))
