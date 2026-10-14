module NodeOpsTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode

let doc =
    Obj [ "name", Str "svc"
          "items", Arr [ Obj [ "name", Str "a"; "n", Int 1L ]; Obj [ "name", Str "b"; "n", Int 2L ] ]
          "z", Null
          "a", Bool true ]

let private code r =
    match r with
    | Error(e: Error) -> e.Code
    | Ok _ -> "ok"

[<Fact>]
let ``tryGet nested`` () =
    Assert.Equal(Ok(Str "b"), Node.tryGet [ Key "items"; Index 1; Key "name" ] doc)
    Assert.Equal(Ok doc, Node.tryGet [] doc)

[<Fact>]
let ``tryGet not found`` () =
    let r = Node.tryGet [ Key "items"; Index 2 ] doc
    Assert.Equal("path.notFound", code r)
    match r with
    | Error e -> Assert.Equal<Path>([ Key "items"; Index 2 ], e.Path)
    | _ -> ()
    Assert.Equal("path.notFound", code (Node.tryGet [ Key "nope" ] doc))
    Assert.Equal("path.notFound", code (Node.tryGet [ Key "items"; Index -1 ] doc))

[<Fact>]
let ``type mismatch`` () =
    Assert.Equal("type.mismatch", code (Node.tryGet [ Index 0 ] doc))
    Assert.Equal("type.mismatch", code (Node.tryGet [ Key "items"; Key "x" ] doc))
    Assert.Equal("type.mismatch", code (Node.tryGet [ Key "name"; Key "x" ] doc))
    Assert.Equal("type.mismatch", code (Node.set [ Index 0 ] Null doc))
    Assert.Equal("type.mismatch", code (Node.remove [ Key "items"; Key "x" ] doc))

[<Fact>]
let ``set replaces existing key in place keeping order`` () =
    let r = Node.set [ Key "z" ] (Int 9L) doc
    match r with
    | Ok(Obj kvs) -> Assert.Equal<string list>([ "name"; "items"; "z"; "a" ], List.map fst kvs)
    | _ -> failwith "expected Obj"
    Assert.Equal(Ok(Int 9L), r |> Result.bind (Node.tryGet [ Key "z" ]))

[<Fact>]
let ``set appends new key at end`` () =
    match Node.set [ Key "new" ] (Str "v") doc with
    | Ok(Obj kvs) -> Assert.Equal("new", fst (List.last kvs))
    | _ -> failwith "expected Obj"

[<Fact>]
let ``set nested keeps siblings`` () =
    let r = Node.set [ Key "items"; Index 0; Key "n" ] (Int 5L) doc
    Assert.Equal(Ok(Int 5L), r |> Result.bind (Node.tryGet [ Key "items"; Index 0; Key "n" ]))
    Assert.Equal(Ok(Str "b"), r |> Result.bind (Node.tryGet [ Key "items"; Index 1; Key "name" ]))

[<Fact>]
let ``set array append at length, out of range is notFound`` () =
    let r = Node.set [ Key "items"; Index 2 ] (Str "c") doc
    Assert.Equal(Ok(Str "c"), r |> Result.bind (Node.tryGet [ Key "items"; Index 2 ]))
    Assert.Equal("path.notFound", code (Node.set [ Key "items"; Index 3 ] (Str "c") doc))
    Assert.Equal("path.notFound", code (Node.set [ Key "items"; Index -1 ] (Str "c") doc))

[<Fact>]
let ``set with missing intermediate is notFound`` () =
    Assert.Equal("path.notFound", code (Node.set [ Key "nope"; Key "x" ] Null doc))

[<Fact>]
let ``set empty path replaces root`` () =
    Assert.Equal(Ok(Int 1L), Node.set [] (Int 1L) doc)

[<Fact>]
let ``remove key preserves order of rest`` () =
    match Node.remove [ Key "items" ] doc with
    | Ok(Obj kvs) -> Assert.Equal<string list>([ "name"; "z"; "a" ], List.map fst kvs)
    | _ -> failwith "expected Obj"

[<Fact>]
let ``remove array element shifts`` () =
    let r = Node.remove [ Key "items"; Index 0 ] doc
    Assert.Equal(Ok(Str "b"), r |> Result.bind (Node.tryGet [ Key "items"; Index 0; Key "name" ]))
    Assert.Equal("path.notFound", code (r |> Result.bind (Node.tryGet [ Key "items"; Index 1 ])))

[<Fact>]
let ``remove not found and root`` () =
    Assert.Equal("path.notFound", code (Node.remove [ Key "nope" ] doc))
    Assert.Equal("path.notFound", code (Node.remove [ Key "items"; Index 5 ] doc))
    Assert.Equal("path.invalid", code (Node.remove [] doc))

[<Fact>]
let ``remove nested`` () =
    let r = Node.remove [ Key "items"; Index 1; Key "n" ] doc
    Assert.Equal(Ok(Obj [ "name", Str "b" ]), r |> Result.bind (Node.tryGet [ Key "items"; Index 1 ]))

// property: set p v n |> tryGet p = Ok v, over paths that exist or are appendable
let rec private paths (n: Node) : Path list =
    [ yield []
      match n with
      | Obj kvs ->
          for (k, c) in kvs do
              for p in paths c -> Key k :: p
      | Arr items ->
          for i, c in List.indexed items do
              for p in paths c -> Index i :: p
      | _ -> () ]

let private genScalar =
    Gen.oneof [ Gen.constant Null
                Gen.map Bool Arb.generate<bool>
                Gen.map (int64 >> Int) Arb.generate<int>
                Gen.map (fun (NonNull s) -> Str s) Arb.generate ]

let private genNode =
    let rec go size =
        if size <= 0 then genScalar
        else
            let sub = go (size / 3)
            Gen.oneof [ genScalar
                        Gen.map Arr (Gen.listOfLength 3 sub |> Gen.resize 3)
                        Gen.map (fun vs -> Obj(vs |> List.mapi (fun i v -> "k" + string i, v)))
                            (Gen.listOfLength 3 sub) ]
    Gen.sized go

type NodeArb =
    static member Node() : Arbitrary<Node> = Arb.fromGen genNode

[<Property(Arbitrary = [| typeof<NodeArb> |])>]
let ``set p v n |> tryGet p = Ok v`` (n: Node) (v: Node) =
    paths n
    |> List.forall (fun p -> Node.set p v n |> Result.bind (Node.tryGet p) = Ok v)

[<Property(Arbitrary = [| typeof<NodeArb> |])>]
let ``remove then tryGet is notFound`` (n: Node) =
    paths n
    |> List.filter (fun p -> not (List.isEmpty p))
    |> List.forall (fun p ->
        match Node.remove p n with
        | Ok n' ->
            // for objects the key is gone; for arrays the index may be reoccupied by a later item
            match List.last p with
            | Key _ -> (match Node.tryGet p n' with Error e -> e.Code = "path.notFound" | Ok _ -> false)
            | Index _ -> true
        | Error _ -> false)
