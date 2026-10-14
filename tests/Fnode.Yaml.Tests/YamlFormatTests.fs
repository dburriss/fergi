module Fnode.Yaml.Tests.YamlFormatTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode

let private parseOk (text: string) : Node =
    match Fnode.Yaml.format.Parse text with
    | Ok p -> p.Node
    | Error es -> failwithf "expected Ok, got %A" es

let private parseErr (text: string) : Error list =
    match Fnode.Yaml.format.Parse text with
    | Ok p -> failwithf "expected Error, got %A" p.Node
    | Error es -> es

let private print = Fnode.Yaml.format.Print

// --- Format record ---------------------------------------------------------------------------

[<Fact>]
let ``format metadata`` () =
    Assert.Equal("yaml", Fnode.Yaml.format.Name)
    Assert.Equal<string list>([ ".yaml"; ".yml" ], Fnode.Yaml.format.Extensions)

[<Fact>]
let ``Locations are filled for successful parses`` () =
    match Fnode.Yaml.format.Parse "a: 1" with
    | Ok p ->
        Assert.False(Map.isEmpty p.Locations)
        Assert.True(Map.containsKey "" p.Locations)
        Assert.True(Map.containsKey "/a" p.Locations)
    | Error es -> failwithf "%A" es

// --- Scalars and structure -------------------------------------------------------------------

[<Fact>]
let ``scalars map to Node cases`` () =
    let n = parseOk "a: 1\nb: -7\nc: 1.5\nd: true\ne: null\nf: ~\ng: hello\nh: \"1\"\ni:\n"
    Assert.Equal(
        Obj [ "a", Int 1L; "b", Int -7L; "c", Float 1.5; "d", Bool true; "e", Null; "f", Null
              "g", Str "hello"; "h", Str "1"; "i", Null ],
        n)

[<Fact>]
let ``1.0 is Float and 1 is Int`` () =
    Assert.Equal(Arr [ Float 1.0; Int 1L; Float 2.5 ], parseOk "[1.0, 1, 2.50]")

[<Fact>]
let ``empty and comment-only documents are Null`` () =
    Assert.Equal(Null, parseOk "")
    Assert.Equal(Null, parseOk "# nothing")

[<Fact>]
let ``yes no on off stay strings`` () =
    Assert.Equal(
        Obj [ "a", Str "yes"; "b", Str "no"; "c", Str "on"; "d", Str "off"; "e", Str "Yes" ],
        parseOk "a: yes\nb: no\nc: on\nd: off\ne: Yes")

[<Fact>]
let ``key order is kept`` () =
    let n = parseOk "z: 1\na: 2\nm: 3\nb:\n  y: 1\n  x: 2"
    Assert.Equal(Obj [ "z", Int 1L; "a", Int 2L; "m", Int 3L; "b", Obj [ "y", Int 1L; "x", Int 2L ] ], n)
    Assert.Equal("z: 1\na: 2\nm: 3\nb:\n  y: 1\n  x: 2\n", print n)

[<Fact>]
let ``duplicate keys are kept as written`` () =
    Assert.Equal(Obj [ "a", Int 1L; "a", Int 2L ], parseOk "a: 1\na: 2")

// --- Integers --------------------------------------------------------------------------------

[<Fact>]
let ``int64 range is Int`` () =
    Assert.Equal(
        Arr [ Int System.Int64.MaxValue; Int System.Int64.MinValue ],
        parseOk "[9223372036854775807, -9223372036854775808]")

[<Fact>]
let ``integers beyond int64 but within decimal become Float (known lossy)`` () =
    match parseOk "9223372036854775808" with
    | Float f -> Assert.Equal(9223372036854775808.0, f)
    | n -> failwithf "%A" n

[<Fact>]
let ``integers beyond decimal range give a parse error, not an exception`` () =
#if FABLE_COMPILER
    // Fable's decimal has no range limit (big.js / Python decimal): the integer parses, then Int's
    // int64 range sends it to Float.
    Assert.Equal(Float 123456789012345678901234567890.0, parseOk "123456789012345678901234567890")
#else
    let es = parseErr "123456789012345678901234567890"
    Assert.Equal(ErrorCode.parse, (List.head es).Code)
#endif

[<Fact>]
let ``int64 extremes round trip`` () =
    let n = Arr [ Int System.Int64.MaxValue; Int System.Int64.MinValue; Int 0L ]
    Assert.Equal(n, parseOk (print n))

[<Fact>]
let ``integral floats stay floats through print and parse`` () =
    let n = Arr [ Float 1.0; Float -3.0; Float 0.0; Float 1e14; Float 2.5; Float 0.1 ]
    let text = print n
    Assert.Contains("1.0", text)
    Assert.Equal(n, parseOk text)

[<Fact>]
let ``non-finite floats round trip`` () =
    let n = Arr [ Float infinity; Float -infinity ]
    Assert.Equal(n, parseOk (print n))
    match parseOk (print (Float nan)) with
    | Float f -> Assert.True(System.Double.IsNaN f)
    | other -> failwithf "%A" other

// --- Anchors, aliases, merge keys ------------------------------------------------------------

[<Fact>]
let ``anchors and aliases are expanded`` () =
    let n = parseOk "base: &b\n  x: 1\n  y: [1, 2]\ncopy: *b"
    let b = Obj [ "x", Int 1L; "y", Arr [ Int 1L; Int 2L ] ]
    Assert.Equal(Obj [ "base", b; "copy", b ], n)

[<Fact>]
let ``merge keys are applied, own keys first then merged ones`` () =
    let n = parseOk "base: &b {x: 1, y: 2}\nd:\n  <<: *b\n  y: 9\n  z: 3"
    match n with
    | Obj [ _; "d", Obj d ] ->
        Assert.Equal(Int 9L, List.find (fst >> (=) "y") d |> snd)
        Assert.Equal(Int 1L, List.find (fst >> (=) "x") d |> snd)
        Assert.Equal(Int 3L, List.find (fst >> (=) "z") d |> snd)
        Assert.DoesNotContain(d, fun (k, _) -> k = "<<")
    | other -> failwithf "%A" other

[<Fact>]
let ``recursive alias is a parse error`` () =
    let es = parseErr "a: &a [*a]"
    Assert.Equal(ErrorCode.parse, (List.head es).Code)

[<Fact>]
let ``unknown alias is a parse error`` () =
    Assert.Equal(ErrorCode.parse, (List.head (parseErr "a: *nope")).Code)

// --- Timestamps ------------------------------------------------------------------------------

[<Fact>]
let ``timestamps keep their original source text`` () =
    match parseOk "a: 2026-10-14T10:00:00+02:00\nb: 2026-10-14T10:00:00.123Z" with
    | Obj [ "a", Timestamp a; "b", Timestamp b ] ->
        Assert.Equal("2026-10-14T10:00:00+02:00", a)
        Assert.Equal("2026-10-14T10:00:00.123Z", b)
    | other -> failwithf "%A" other

[<Fact>]
let ``date-only timestamp keeps its text`` () =
    Assert.Equal(Timestamp "2026-10-14", parseOk "2026-10-14")

[<Fact>]
let ``zoneless date-time stays zoneless`` () =
    Assert.Equal(Timestamp "2026-10-14T10:00:00", parseOk "2026-10-14T10:00:00")

[<Fact>]
let ``space-separated timestamps keep their text`` () =
    Assert.Equal(Timestamp "2026-10-14 10:00:00", parseOk "2026-10-14 10:00:00")
    Assert.Equal(Timestamp "2026-10-14 10:00:00 +01:00", parseOk "2026-10-14 10:00:00 +01:00")

[<Fact>]
let ``fractional seconds keep their text`` () =
    Assert.Equal(Timestamp "2026-10-14T10:00:00.5Z", parseOk "2026-10-14T10:00:00.5Z")

[<Fact>]
let ``nested timestamps use their own pointer`` () =
    match parseOk "a:\n  - 2026-10-14\n  - x: 2026-10-14 10:00:00\nb/c~d: 2026-01-02" with
    | Obj [ "a", Arr [ Timestamp d; Obj [ "x", Timestamp t ] ]; "b/c~d", Timestamp k ] ->
        Assert.Equal("2026-10-14", d)
        Assert.Equal("2026-10-14 10:00:00", t)
        Assert.Equal("2026-01-02", k)
    | other -> failwithf "%A" other

[<Fact>]
let ``aliased timestamp reports the anchor text at every site`` () =
    match parseOk "a: &t 2026-10-14\nb: *t" with
    | Obj [ "a", Timestamp a; "b", Timestamp b ] ->
        Assert.Equal("2026-10-14", a)
        Assert.Equal("2026-10-14", b)
    | other -> failwithf "%A" other

[<Fact>]
let ``timestamp text round trips`` () =
    let n = Obj [ "t", Timestamp "2026-10-14T10:00:00.0000000+02:00" ]
    Assert.Equal(n, parseOk (print n))

[<Fact>]
let ``timestamp text prints verbatim and unquoted and round trips`` () =
    for text in
        [ "2026-10-14"; "2026-10-14T10:00:00Z"; "2026-10-14T10:00:00+02:00"; "2026-10-14T10:00:00"
          "2026-10-14 10:00:00"; "2026-10-14 10:00:00 +01:00"; "2026-10-14T10:00:00.5Z" ] do
        let n = Obj [ "t", Timestamp text; "l", Arr [ Timestamp text ] ]
        let printed = print n
        Assert.Equal("t: " + text + "\nl:\n  - " + text + "\n", printed)
        Assert.Equal(n, parseOk printed)
    Assert.Equal("2026-10-14\n", print (Timestamp "2026-10-14"))
    Assert.Equal(Timestamp "2026-10-14", parseOk (print (Timestamp "2026-10-14")))

[<Fact>]
let ``timestamp marker does not collide with user strings`` () =
    let n = Arr [ Str "FnodeTimestampMarkerN0N"; Timestamp "2026-10-14" ]
    Assert.Equal(n, parseOk (print n))

[<Fact>]
let ``non-YAML-shaped timestamp text is normalised through DateTimeOffset`` () =
#if FABLE_COMPILER
    // Fable's DateTimeOffset.TryParse ignores culture and styles, so only YAML-shaped timestamps
    // are normalised; anything else is kept as a string.
    Assert.Equal(Str "10/14/2026 10:00:00 +00:00", parseOk (print (Timestamp "10/14/2026 10:00:00 +00:00")))
#else
    Assert.Equal(Timestamp "2026-10-14T10:00:00.0000000+00:00", parseOk (print (Timestamp "10/14/2026 10:00:00 +00:00")))
#endif

[<Fact>]
let ``unparseable Timestamp text prints as a string and does not throw`` () =
    Assert.Equal(Str "not a date", parseOk (print (Timestamp "not a date")))

[<Fact>]
let ``quoted date stays a string`` () =
    Assert.Equal(Str "2026-10-14", parseOk "\"2026-10-14\"")

// --- Errors ----------------------------------------------------------------------------------

[<Fact>]
let ``malformed input gives parse Error with location, no exception`` () =
    match parseErr "a: [1, 2" with
    | [ e ] ->
        Assert.Equal(ErrorCode.parse, e.Code)
        Assert.Equal<Path>([], e.Path)
        Assert.True(e.Location.IsSome)
        Assert.Equal(1, e.Location.Value.Start.Line)
        Assert.DoesNotContain("\n", e.Message)
    | other -> failwithf "%A" other

[<Fact>]
let ``multi-document input is a parse Error from Parse`` () =
    match parseErr "a: 1\n---\nb: 2" with
    | [ e ] ->
        Assert.Equal(ErrorCode.parse, e.Code)
        Assert.Contains("Multiple documents", e.Message)
    | other -> failwithf "%A" other

[<Fact>]
let ``non-string keys give a type.mismatch Error`` () =
    for text in [ "1: a"; "true: a"; "null: a"; "? [x]\n: b"; "outer:\n  2: x" ] do
        match parseErr text with
        | [ e ] -> Assert.Equal(ErrorCode.typeMismatch, e.Code)
        | other -> failwithf "%s -> %A" text other

[<Fact>]
let ``non-string key error carries the path of the mapping`` () =
    match parseErr "outer:\n  list:\n    - 1: x" with
    | [ e ] -> Assert.Equal<Path>([ Key "outer"; Key "list"; Index 0 ], e.Path)
    | other -> failwithf "%A" other

[<Fact>]
let ``Parse never throws on garbage`` () =
    for text in [ "\t"; "a: b: c"; "{"; "[,]"; "- a\nb: c"; "\"unterminated"; "&"; "*"; "%YAML 9\n---\na"; "a: !!bad x"; "\u0000" ] do
        match Fnode.Yaml.format.Parse text with
        | Ok _ -> ()
        | Error es -> Assert.All(es, fun e -> Assert.Equal(ErrorCode.parse, e.Code) |> ignore)

// --- parseAll --------------------------------------------------------------------------------

[<Fact>]
let ``parseAll returns each document`` () =
    match Fnode.Yaml.parseAll "a: 1\n---\n- 2\n---\nhello" with
    | Ok [ a; b; c ] ->
        Assert.Equal(Obj [ "a", Int 1L ], a.Node)
        Assert.Equal(Arr [ Int 2L ], b.Node)
        Assert.Equal(Str "hello", c.Node)
    | other -> failwithf "%A" other

[<Fact>]
let ``parseAll of a single document and of empty input`` () =
    Assert.Equal(1, (Fnode.Yaml.parseAll "a: 1" |> Result.defaultValue []).Length)
    Assert.Equal(0, (match Fnode.Yaml.parseAll "" with Ok l -> l.Length | Error _ -> -1))

[<Fact>]
let ``parseAll maps errors instead of throwing`` () =
    match Fnode.Yaml.parseAll "a: 1\n---\nb: [" with
    | Error [ e ] -> Assert.Equal(ErrorCode.parse, e.Code)
    | other -> failwithf "%A" other
    match Fnode.Yaml.parseAll "1: x\n---\n2: y" with
    | Error es -> Assert.Equal(2, es.Length)
    | other -> failwithf "%A" other

// --- Round trip for quirky strings -----------------------------------------------------------

let quirky =
    [ "yes"; "no"; "on"; "off"; "y"; "n"; "true"; "False"; "null"; "~"; ""; "1"; "1.0"; "1e3"; "0x1F"; "0o7"
      "+1"; ".5"; ".inf"; ".nan"; "2026-10-14"; "2026-10-14T10:00:00Z"; "12:30"; "a: b"; "- x"; "#c"; "a #c"
      " lead"; "trail "; "  "; "x\ny"; "x\n"; "\n"; "\t"; "a\tb"; "it's"; "a\"b"; "\\"; "*a"; "&a"; "!t"; "@a"
      "%a"; "|"; ">"; "-"; "?"; ":"; "<<"; "[a]"; "{a}"; "a, b"; "---"; "..."; "é😀"; "\u0000"; "\r\n"; " " ]

let private roundTrips (n: Node) =
    match Fnode.Yaml.format.Parse(print n) with
    | Ok p -> p.Node = n
    | Error _ -> false

[<Fact>]
let ``quirky strings round trip as values`` () =
    let bad = quirky |> List.filter (fun s -> not (roundTrips (Str s)))
    Assert.True(List.isEmpty bad, sprintf "failed: %A" bad)

[<Fact>]
let ``quirky strings round trip as keys and in containers`` () =
    let bad =
        quirky
        |> List.filter (fun s ->
            not (roundTrips (Obj [ s, Arr [ Str s; Int 1L ]; "k", Obj [ s, Str s ] ])))
    Assert.True(List.isEmpty bad, sprintf "failed: %A" bad)

/// The string `<<` is quoted on emit, so a literal `<<` key/value is not re-read as a merge key.
[<Fact>]
let ``literal << key and value round trip`` () =
    Assert.True(roundTrips (Obj [ "<<", Int 1L ]))
    Assert.True(roundTrips (Obj [ "k", Str "<<" ]))
    Assert.True(roundTrips (Str "<<"))

/// Root-level `...` and `---` strings are quoted so they are not read as document markers.
[<Fact>]
let ``root-level document marker strings round trip`` () =
    Assert.True(roundTrips (Str "..."))
    Assert.True(roundTrips (Str "---"))
    Assert.True(roundTrips (Arr [ Str "..." ]))

[<Fact>]
let ``printed quirky strings are quoted so they keep their type`` () =
    Assert.Contains("'1.0'", print (Obj [ "v", Str "1.0" ]))
    Assert.Contains("'2026-10-14'", print (Obj [ "v", Str "2026-10-14" ]))
    Assert.Contains("'null'", print (Obj [ "v", Str "null" ]))

[<Fact>]
let ``empty containers round trip`` () =
    let n = Obj [ "a", Arr []; "b", Obj []; "c", Arr [ Obj []; Arr [] ] ]
    Assert.Equal(n, parseOk (print n))
    Assert.Equal(Arr [], parseOk (print (Arr [])))
    Assert.Equal(Obj [], parseOk (print (Obj [])))

[<Fact>]
let ``print ends with a newline`` () =
    Assert.EndsWith("\n", print (Obj [ "a", Int 1L ]))

// --- Property: Print >> Parse -----------------------------------------------------------------

let private genStr =
    Gen.oneof
        [ Arb.generate<string> |> Gen.map (fun s -> if isNull s then "" else s)
          Gen.elements quirky ]
    // lone surrogates are not valid text for any YAML encoding
    |> Gen.map (fun s -> System.String(s.ToCharArray() |> Array.filter (fun c -> not (System.Char.IsSurrogate c))))

let private genFiniteFloat: Gen<float> =
    let bits =
        Arb.generate<int64>
        |> Gen.map System.BitConverter.Int64BitsToDouble
        |> Gen.filter (fun f -> not (System.Double.IsNaN f || System.Double.IsInfinity f))
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
    let scalar =
        Gen.oneof
            [ Gen.constant Null
              Arb.generate<bool> |> Gen.map Bool
              Arb.generate<int64> |> Gen.map Int
              Gen.elements [ System.Int64.MaxValue; System.Int64.MinValue ] |> Gen.map Int
              Gen.choose (-1000000, 1000000) |> Gen.map (fun i -> Float(float i / 8.0))
              Gen.choose (-1000, 1000) |> Gen.map (fun i -> Float(float i))
              Gen.elements [ -0.0; 1e15; -1e15; 1e16; 1e20; 123456789012345678.0 ] |> Gen.map Float
              genFiniteFloat |> Gen.map Float
              genFiniteFloat |> Gen.map Float
              genStr |> Gen.map Str ]
    let rec go size =
        if size <= 0 then scalar
        else
            let sub = go (size / 3)
            Gen.frequency
                [ 3, scalar
                  1, Gen.listOfLength 3 sub |> Gen.resize 3 |> Gen.map Arr
                  1, Gen.listOf (Gen.zip genStr sub) |> Gen.resize 3 |> Gen.map Obj ]
    Gen.sized (fun s -> go (min s 12))

type Arbs =
    static member Node() = Arb.fromGen genNode

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 500)>]
let ``Print then Parse is identity`` (n: Node) =
    match Fnode.Yaml.format.Parse(print n) with
    | Ok p -> p.Node = n
    | Error _ -> false

[<Property(Arbitrary = [| typeof<Arbs> |], MaxTest = 200)>]
let ``Parse then Print then Parse is stable`` (n: Node) =
    let t1 = print n
    match Fnode.Yaml.format.Parse t1 with
    | Ok p -> print p.Node = t1
    | Error _ -> false

type FloatArbs =
    static member Float() = Arb.fromGen (genFiniteFloat |> Gen.map Float)

[<Property(Arbitrary = [| typeof<FloatArbs> |], MaxTest = 2000)>]
let ``YAML: finite Float round trips as Float with identical bits`` (n: Node) =
    match n with
    | Float f ->
        match Fnode.Yaml.format.Parse(print n) with
        | Ok p -> (match p.Node with Float g -> sameBits f g | _ -> false)
        | Error _ -> false
    | _ -> true

// --- Locations -------------------------------------------------------------------------------

let private locs (text: string) : Map<string, Span> =
    match Fnode.Yaml.format.Parse text with
    | Ok p -> p.Locations
    | Error es -> failwithf "expected Ok, got %A" es

[<Fact>]
let ``locations for nested nodes on multiple lines`` () =
    let l = locs "a:\n  b:\n    - 10\n    - 20\n"
    Assert.Equal({ Line = 1; Column = 1 }, l.[""].Start)
    Assert.Equal(2, l.["/a"].Start.Line)
    Assert.Equal(3, l.["/a/b"].Start.Line)
    Assert.Equal(3, l.["/a/b/0"].Start.Line)
    Assert.Equal({ Line = 3; Column = 7 }, l.["/a/b/0"].Start)
    Assert.Equal({ Line = 3; Column = 9 }, l.["/a/b/0"].End)
    Assert.Equal({ Line = 4; Column = 7 }, l.["/a/b/1"].Start)

[<Fact>]
let ``locations escape pointer keys`` () =
    let l = locs "a/b: 1\nc~d: 2"
    Assert.True(l.ContainsKey "/a~1b")
    Assert.True(l.ContainsKey "/c~0d")

[<Fact>]
let ``alias sites are positioned at the alias token`` () =
    let l = locs "a: &x\n  k: 1\nb: *x"
    Assert.Equal({ Line = 3; Column = 4 }, l.["/b"].Start)
    Assert.Equal({ Line = 3; Column = 4 }, l.["/b/k"].Start)

[<Fact>]
let ``merge-key entries are positioned at the merge value`` () =
    let l = locs "base: &b\n  k: 1\nd:\n  <<: *b\n  z: 2"
    Assert.Equal({ Line = 4; Column = 7 }, l.["/d/k"].Start)
    Assert.Equal(5, l.["/d/z"].Start.Line)

[<Fact>]
let ``parse error carries line and column`` () =
    match parseErr "a: 1\nb: [1, 2\n" with
    | [ e ] ->
        Assert.Equal(ErrorCode.parse, e.Code)
        let loc = e.Location.Value
        Assert.True(loc.Start.Line >= 2)
        Assert.True(loc.Start.Column >= 1)
        Assert.Equal(loc.Start, loc.End)
        Assert.DoesNotContain("\n", e.Message)
    | other -> failwithf "%A" other

[<Fact>]
let ``parseAll gives per-document locations relative to the stream`` () =
    match Fnode.Yaml.parseAll "a: 1\n---\nb: 2\nc: 2026-10-14\n" with
    | Ok [ d1; d2 ] ->
        Assert.Equal(1, d1.Locations.["/a"].Start.Line)
        Assert.Equal(3, d2.Locations.["/b"].Start.Line)
        Assert.Equal(4, d2.Locations.["/c"].Start.Line)
        Assert.Equal(Obj [ "b", Int 2L; "c", Timestamp "2026-10-14" ], d2.Node)
    | other -> failwithf "%A" other

[<Fact>]
let ``parseAll error has a location`` () =
    match Fnode.Yaml.parseAll "a: 1\n---\nb: [1" with
    | Error [ e ] -> Assert.True(e.Location.IsSome)
    | other -> failwithf "%A" other
