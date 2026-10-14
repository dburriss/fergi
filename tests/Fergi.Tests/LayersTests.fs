module LayersTests

open Xunit
open Fnode
open Fergi

let private p = Policy.defaults
let private layers pol ls = Doc.layers pol ls
let private okL r =
    match r with
    | Ok(n, prov) -> n, prov
    | Error(cs: Conflict list) -> failwithf "conflicts: %A" cs
let private errL r =
    match r with
    | Error(cs: Conflict list) -> cs
    | Ok _ -> failwith "expected conflicts"

let private org =
    Obj [ "replicas", Int 2L; "labels", Obj [ "tier", Str "web" ]; "logLevel", Str "info"; "ports", Arr [ Int 80L ] ]
let private team = Obj [ "labels", Obj [ "team", Str "energy" ]; "logLevel", Null; "replicas", Int 3L ]
let private svc = Obj [ "replicas", Int 4L; "ports", Arr [ Int 1L ]; "name", Str "billing" ]

[<Fact>]
let ``no layers gives empty object and provenance`` () =
    let n, prov = okL (layers p [])
    Assert.Equal(Obj [], n)
    Assert.True(Map.isEmpty prov)

[<Fact>]
let ``single layer maps every node to it`` () =
    let n, prov = okL (layers p [ "org", org ])
    Assert.Equal(org, n)
    let expected =
        [ ""; "/replicas"; "/labels"; "/labels/tier"; "/logLevel"; "/ports"; "/ports/0" ]
        |> List.map (fun k -> k, "org")
        |> Map.ofList
    Assert.Equal<Map<string, string>>(expected, prov)

[<Fact>]
let ``layers fold in order and provenance names the last source`` () =
    let n, prov = okL (layers p [ "org", org; "team", team; "svc", svc ])
    let expected =
        Obj [ "replicas", Int 4L
              "labels", Obj [ "tier", Str "web"; "team", Str "energy" ]
              "ports", Arr [ Int 1L ]
              "name", Str "billing" ]
    Assert.Equal(expected, n)
    let get k = Map.tryFind k prov
    Assert.Equal(Some "svc", get "/replicas")
    Assert.Equal(Some "org", get "/labels/tier")
    Assert.Equal(Some "team", get "/labels/team")
    Assert.Equal(Some "team", get "/labels")
    // list replaced wholesale: the whole list and its items map to the winner
    Assert.Equal(Some "svc", get "/ports")
    Assert.Equal(Some "svc", get "/ports/0")
    Assert.Equal(None, get "/ports/1")
    Assert.Equal(Some "svc", get "/name")
    // null-deleted key has no entry; root is the last layer
    Assert.Equal(None, get "/logLevel")
    Assert.Equal(Some "svc", get "")

[<Fact>]
let ``object replaced by a scalar drops descendant entries`` () =
    let _, prov = okL (layers p [ "a", Obj [ "k", Obj [ "x", Int 1L ] ]; "b", Obj [ "k", Int 2L ] ])
    Assert.Equal<Map<string, string>>(Map.ofList [ "", "b"; "/k", "b" ], prov)

[<Fact>]
let ``wholesale replaced object maps all its nodes`` () =
    let _, prov = okL (layers p [ "a", Obj [ "k", Int 1L ]; "b", Obj [ "k", Obj [ "x", Int 1L ] ] ])
    Assert.Equal(Some "b", Map.tryFind "/k" prov)
    Assert.Equal(Some "b", Map.tryFind "/k/x" prov)

[<Fact>]
let ``provenance pointers are escaped like Path.print`` () =
    let doc = Obj [ "a/b", Obj [ "c~d", Int 1L ]; "", Int 2L ]
    let _, prov = okL (layers p [ "one", doc ])
    Assert.Equal(Some "one", Map.tryFind "/a~1b/c~0d" prov)
    Assert.Equal(Some "one", Map.tryFind "/" prov)
    for k in prov |> Map.toList |> List.map fst do
        Assert.True(Path.parse k |> Result.isOk)

[<Fact>]
let ``append lists: items keep their own source`` () =
    let pol = Policy.withLists Append p
    let _, prov = okL (layers pol [ "a", Obj [ "l", Arr [ Int 1L ] ]; "b", Obj [ "l", Arr [ Int 2L; Int 3L ] ] ])
    Assert.Equal(Some "a", Map.tryFind "/l/0" prov)
    Assert.Equal(Some "b", Map.tryFind "/l/1" prov)
    Assert.Equal(Some "b", Map.tryFind "/l/2" prov)
    Assert.Equal(Some "b", Map.tryFind "/l" prov)

[<Fact>]
let ``ByKey lists: merged item fields and new items attributed`` () =
    let pol = Policy.withListsBy [ Key "ports" ] "name" p
    let mk (items: (string * int64) list) = Obj [ "ports", Arr(items |> List.map (fun (n, v) -> Obj [ "name", Str n; "port", Int v ])) ]
    let _, prov = okL (layers pol [ "org", mk [ "http", 80L; "metrics", 9090L ]; "svc", mk [ "http", 8080L; "grpc", 9000L ] ])
    Assert.Equal(Some "svc", Map.tryFind "/ports/0/port" prov)
    Assert.Equal(Some "svc", Map.tryFind "/ports/0/name" prov) // equal value re-supplied by the later layer
    Assert.Equal(Some "org", Map.tryFind "/ports/1/port" prov)
    Assert.Equal(Some "svc", Map.tryFind "/ports/2/port" prov)

[<Fact>]
let ``FirstWins keeps the earlier source for kept values`` () =
    let pol = Policy.withConflict FirstWins p
    let _, prov = okL (layers pol [ "a", Obj [ "x", Int 1L ]; "b", Obj [ "x", Int 2L; "y", Int 3L ] ])
    Assert.Equal(Some "a", Map.tryFind "/x" prov)
    Assert.Equal(Some "b", Map.tryFind "/y" prov)

[<Fact>]
let ``conflicts carry both source names and paths and accumulate over layers`` () =
    let pol = Policy.withConflict Fail p
    let r =
        layers pol
            [ "org", Obj [ "a", Int 1L; "b", Int 1L ]
              "team", Obj [ "b", Int 1L; "c", Int 1L ]
              "svc", Obj [ "a", Int 2L; "b", Int 2L; "c", Int 2L ] ]
    let cs = errL r
    let view = cs |> List.map (fun c -> Path.print c.Path, c.LeftSource, c.RightSource)
    Assert.Equal<(string * string option * string option) list>(
        [ "/a", Some "org", Some "svc"; "/b", Some "team", Some "svc"; "/c", Some "team", Some "svc" ],
        view)
    Assert.Equal(Int 1L, cs.Head.Left)
    Assert.Equal(Int 2L, cs.Head.Right)

[<Fact>]
let ``conflicts from different layers are all reported`` () =
    let pol = Policy.withConflict Fail p
    let cs = errL (layers pol [ "a", Obj [ "x", Int 1L; "y", Int 1L ]; "b", Obj [ "x", Int 2L ]; "c", Obj [ "y", Int 2L ] ])
    Assert.Equal<(string * string option * string option) list>(
        [ "/x", Some "a", Some "b"; "/y", Some "a", Some "c" ],
        cs |> List.map (fun c -> Path.print c.Path, c.LeftSource, c.RightSource))

[<Fact>]
let ``Resolve-provided conflicts get missing sources filled`` () =
    let pol =
        Policy.withConflict
            (Resolve(fun path l r -> Error { Path = path; Left = l; Right = r; LeftSource = None; RightSource = Some "me"; Reason = "no" }))
            p
    let cs = errL (layers pol [ "a", Obj [ "x", Int 1L ]; "b", Obj [ "x", Int 2L ] ])
    Assert.Equal(Some "a", cs.Head.LeftSource)
    Assert.Equal(Some "me", cs.Head.RightSource)

[<Fact>]
let ``layers under null deletion: later layer may re-add`` () =
    let n, prov =
        okL (layers p [ "a", Obj [ "x", Int 1L ]; "b", Obj [ "x", Null ]; "c", Obj [ "x", Int 3L ] ])
    Assert.Equal(Obj [ "x", Int 3L ], n)
    Assert.Equal(Some "c", Map.tryFind "/x" prov)
