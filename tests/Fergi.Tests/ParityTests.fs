/// Behaviours that differ across runtimes if an implementation leans on the host: these run on
/// .NET and under Fable JS / Python (see docs/fable.md) and must give identical answers.
module ParityTests

open Xunit
open Fnode
open Fergi

let private ok r =
    match r with
    | Ok v -> v
    | Error e -> failwithf "unexpected %A" e

let private yaml (text: string) =
    match Fnode.Yaml.format.Parse text with
    | Ok p -> p.Node
    | Error es -> failwithf "parse failed: %A" es

[<Fact>]
let ``merged key order is base order then new keys in overlay order`` () =
    let a = Obj [ "z", Int 1L; "b", Int 2L; "a", Int 3L ]
    let b = Obj [ "n", Int 4L; "b", Int 20L; "A", Int 5L ]
    let merged = ok (Doc.merge Policy.defaults a b)
    Assert.Equal(Obj [ "z", Int 1L; "b", Int 20L; "a", Int 3L; "n", Int 4L; "A", Int 5L ], merged)
    match merged with
    | Obj kv -> Assert.Equal<string list>([ "z"; "b"; "a"; "n"; "A" ], List.map fst kv)
    | _ -> failwith "expected object"

[<Fact>]
let ``int64 values survive merge, layers and a YAML round trip exactly`` () =
    let big = 9007199254740993L // 2^53 + 1
    let a = yaml "id: 9007199254740993\nmax: 9223372036854775807\nmin: -9223372036854775808\nkeep: 1\n"
    let b = yaml "min: -9223372036854775807\nextra: 9007199254740992\n"
    let layered, _ = ok (Doc.layers Policy.defaults [ "a", a; "b", b ])
    Assert.Equal(
        Obj [ "id", Int big
              "max", Int System.Int64.MaxValue
              "min", Int(System.Int64.MinValue + 1L)
              "keep", Int 1L
              "extra", Int 9007199254740992L ],
        layered)
    Assert.Equal(layered, yaml (Fnode.Yaml.format.Print layered))
    // a one-unit difference above 2^53 is still a conflict, not "equal"
    let pol = Policy.defaults |> Policy.withConflict Fail
    Assert.True(Result.isError (Doc.merge pol (Obj [ "v", Int big ]) (Obj [ "v", Int(big - 1L) ])))
    Assert.Equal(Ok(Obj [ "v", Int big ]), Doc.merge pol (Obj [ "v", Int big ]) (Obj [ "v", Int big ]))

[<Fact>]
let ``timestamp text passes through merge and print unchanged`` () =
    let a = yaml "when: 2026-10-14\nat: 2001-12-14t21:59:43.10-05:00\n"
    let b = yaml "other: 2024-01-02 03:04:05 +05:30\n"
    let merged, _ = ok (Doc.layers Policy.defaults [ "a", a; "b", b ])
    Assert.Equal(
        Obj [ "when", Timestamp "2026-10-14"
              "at", Timestamp "2001-12-14t21:59:43.10-05:00"
              "other", Timestamp "2024-01-02 03:04:05 +05:30" ],
        merged)
    let text = Fnode.Yaml.format.Print merged
    Assert.Contains("when: 2026-10-14", text)
    Assert.Contains("at: 2001-12-14t21:59:43.10-05:00", text)
    Assert.Contains("other: 2024-01-02 03:04:05 +05:30", text)
    // Str and Timestamp with the same text are different values
    Assert.True(Result.isError (Doc.merge (Policy.defaults |> Policy.withConflict Fail) (Obj [ "t", Str "2026-10-14" ]) (Obj [ "t", Timestamp "2026-10-14" ])))

[<Fact>]
let ``provenance map iterates in ordinal key order`` () =
    let a = Obj [ "a", Obj [ "b", Int 1L ]; "B", Int 2L; "a~b", Int 3L; "é", Int 4L; "z", Int 5L ]
    let _, prov = ok (Doc.layers Policy.defaults [ "src", a ])
    // ordinal (UTF-16 code unit) order: '/' < 'B' < 'a' < '~' < 'é'; a parent sorts before its children
    let keys = prov |> Map.toList |> List.map fst
    Assert.Equal<string list>([ ""; "/B"; "/a"; "/a/b"; "/a~0b"; "/z"; "/é" ], keys)

[<Fact>]
let ``string ordering is ordinal and prefix checks do not depend on offsets`` () =
    Assert.True(compare "B" "a" < 0)
    Assert.True(compare "a" "a/b" < 0)
    Assert.True(compare "/a/b" "/a~0b" < 0)
    Assert.True(compare "z" "é" < 0)
    // provenance pruning uses a prefix test: /a must drop /a/b but not /ab
    let before = Obj [ "a", Obj [ "b", Int 1L ]; "ab", Int 2L ]
    let after = Obj [ "a", Int 9L ]
    let merged, prov = ok (Doc.layers Policy.defaults [ "x", before; "y", after ])
    Assert.Equal(Obj [ "a", Int 9L; "ab", Int 2L ], merged)
    Assert.Equal<string list>([ ""; "/a"; "/ab" ], prov |> Map.toList |> List.map fst)
    Assert.Equal(Some "y", Map.tryFind "/a" prov)
    Assert.Equal(Some "x", Map.tryFind "/ab" prov)
