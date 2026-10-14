module PatchTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode
open Fergi

let doc =
    Obj [ "name", Str "svc"
          "tags", Arr [ Str "a"; Str "b" ]
          "meta", Obj [ "owner", Str "me"; "n", Int 1L ] ]

let private codes r =
    match r with
    | Error(es: Error list) -> List.map (fun (e: Error) -> e.Code) es
    | Ok _ -> []

[<Fact>]
let ``apply runs ops in order`` () =
    let r =
        Patch.apply
            [ Set([ Key "name" ], Str "other")
              Set([ Key "extra" ], Bool true)
              Remove [ Key "tags"; Index 0 ] ]
            doc
    let expected =
        Obj [ "name", Str "other"
              "tags", Arr [ Str "b" ]
              "meta", Obj [ "owner", Str "me"; "n", Int 1L ]
              "extra", Bool true ]
    Assert.Equal(Ok expected, r)

[<Fact>]
let ``empty patch is identity`` () = Assert.Equal(Ok doc, Patch.apply [] doc)

[<Fact>]
let ``failed op returns every error and the input is untouched`` () =
    let before = doc
    let r =
        Patch.apply
            [ Set([ Key "name" ], Str "x")
              Remove [ Key "nope" ]
              Set([ Key "meta"; Key "n" ], Int 2L)
              Append([ Key "name" ], Null) ]
            doc
    Assert.Equal<string list>([ "path.notFound"; "type.mismatch" ], codes r)
    Assert.Equal(before, doc)
    match r with
    | Error [ e1; e2 ] ->
        Assert.StartsWith("op 1:", e1.Message)
        Assert.StartsWith("op 3:", e2.Message)
    | _ -> failwith "expected two errors"

[<Fact>]
let ``later ops see earlier successes`` () =
    let r = Patch.apply [ Set([ Key "a" ], Int 1L); Test([ Key "a" ], Int 1L) ] (Obj [])
    Assert.Equal(Ok(Obj [ "a", Int 1L ]), r)

[<Fact>]
let ``Test passes and fails`` () =
    Assert.Equal(Ok doc, Patch.apply [ Test([ Key "meta"; Key "owner" ], Str "me") ] doc)
    Assert.Equal(Ok doc, Patch.apply [ Test([], doc) ] doc)
    Assert.Equal<string list>([ "patch.testFailed" ], codes (Patch.apply [ Test([ Key "name" ], Str "nope") ] doc))
    Assert.Equal<string list>([ "path.notFound" ], codes (Patch.apply [ Test([ Key "zzz" ], Null) ] doc))

[<Fact>]
let ``failed Test aborts the whole patch`` () =
    let r = Patch.apply [ Set([ Key "name" ], Str "x"); Test([ Key "name" ], Str "y") ] doc
    Assert.Equal<string list>([ "patch.testFailed" ], codes r)

[<Fact>]
let ``Append adds to the end of an array`` () =
    match Patch.apply [ Append([ Key "tags" ], Str "c") ] doc with
    | Ok d -> Assert.Equal(Ok(Arr [ Str "a"; Str "b"; Str "c" ]), Node.tryGet [ Key "tags" ] d)
    | Error _ -> failwith "expected Ok"
    Assert.Equal<string list>([ "type.mismatch" ], codes (Patch.apply [ Append([ Key "meta" ], Null) ] doc))
    Assert.Equal<string list>([ "path.notFound" ], codes (Patch.apply [ Append([ Key "zzz" ], Null) ] doc))

[<Fact>]
let ``Move relocates a node`` () =
    let r = Patch.apply [ Move([ Key "meta"; Key "owner" ], [ Key "owner" ]) ] doc
    let expected =
        Obj [ "name", Str "svc"
              "tags", Arr [ Str "a"; Str "b" ]
              "meta", Obj [ "n", Int 1L ]
              "owner", Str "me" ]
    Assert.Equal(Ok expected, r)

[<Fact>]
let ``Move within an array evaluates dest after removal`` () =
    // remove index 0, then set index 1 (= length after removal) appends
    match Patch.apply [ Move([ Key "tags"; Index 0 ], [ Key "tags"; Index 1 ]) ] doc with
    | Ok d -> Assert.Equal(Ok(Arr [ Str "b"; Str "a" ]), Node.tryGet [ Key "tags" ] d)
    | Error _ -> failwith "expected Ok"

[<Fact>]
let ``Move errors`` () =
    Assert.Equal<string list>([ "path.notFound" ], codes (Patch.apply [ Move([ Key "zzz" ], [ Key "y" ]) ] doc))
    // missing intermediate at dest
    Assert.Equal<string list>([ "path.notFound" ], codes (Patch.apply [ Move([ Key "name" ], [ Key "q"; Key "r" ]) ] doc))
    // moving to itself is a no-op when it exists
    Assert.Equal(Ok doc, Patch.apply [ Move([ Key "name" ], [ Key "name" ]) ] doc)

[<Fact>]
let ``diff of equal nodes is empty`` () = Assert.Empty(Patch.diff doc doc)

[<Fact>]
let ``diff by key`` () =
    let b =
        Obj [ "name", Str "svc"
              "tags", Arr [ Str "a"; Str "c" ]
              "meta", Obj [ "n", Int 2L ]
              "new", Null ]
    let ops = Patch.diff doc b
    Assert.Equal<Op list>(
        [ Set([ Key "tags" ], Arr [ Str "a"; Str "c" ])
          Remove [ Key "meta"; Key "owner" ]
          Set([ Key "meta"; Key "n" ], Int 2L)
          Set([ Key "new" ], Null) ],
        ops
    )
    Assert.Equal(Ok b, Patch.apply ops doc)

[<Fact>]
let ``diff replaces the object when key order changes`` () =
    let a = Obj [ "x", Int 1L; "y", Int 2L ]
    let b = Obj [ "y", Int 2L; "x", Int 1L ]
    Assert.Equal<Op list>([ Set([], b) ], Patch.diff a b)
    Assert.Equal(Ok b, Patch.apply (Patch.diff a b) a)

[<Fact>]
let ``diff treats arrays atomically`` () =
    let a = Obj [ "l", Arr [ Int 1L; Int 2L ] ]
    let b = Obj [ "l", Arr [ Int 1L; Int 2L; Int 3L ] ]
    Assert.Equal<Op list>([ Set([ Key "l" ], Arr [ Int 1L; Int 2L; Int 3L ]) ], Patch.diff a b)

[<Fact>]
let ``diff of different scalars or types sets the root`` () =
    Assert.Equal<Op list>([ Set([], Int 2L) ], Patch.diff (Int 1L) (Int 2L))
    Assert.Equal<Op list>([ Set([], Null) ], Patch.diff doc Null)

let private genScalar =
    Gen.oneof [ Gen.constant Null
                Gen.map Bool Arb.generate<bool>
                Gen.map (int64 >> Int) Arb.generate<int>
                Gen.elements [ "a"; "b"; "" ] |> Gen.map Str ]

let private pool = [ "a"; "b"; "c"; "d" ]

let rec private genTree size : Gen<Node> =
    if size <= 0 then genScalar
    else
        let sub = genTree (size / 2)
        Gen.frequency
            [ 2, genScalar
              1, Gen.map Arr (Gen.listOfLength 2 sub)
              3,
              gen {
                  let! keys = Gen.subListOf pool
                  let! flip = Gen.elements [ false; false; true ]
                  let keys = if flip then List.rev keys else keys
                  let! vs = Gen.listOfLength (List.length keys) sub
                  return Obj(List.zip keys vs)
              } ]

type NodeArb =
    static member Node() : Arbitrary<Node> = Arb.fromGen (Gen.sized (fun n -> genTree (min n 8)))

[<Property(Arbitrary = [| typeof<NodeArb> |])>]
let ``apply (diff a b) a = Ok b`` (a: Node) (b: Node) =
    Patch.apply (Patch.diff a b) a = Ok b

[<Property(Arbitrary = [| typeof<NodeArb> |])>]
let ``diff a a is empty`` (a: Node) = List.isEmpty (Patch.diff a a)
