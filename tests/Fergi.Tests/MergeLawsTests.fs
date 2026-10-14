module MergeLawsTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Fnode
open Fergi

/// The only policy under which merge is a monoid: overlay always wins, lists are atomic,
/// null is an ordinary value.
let private lawful = Policy.defaults |> Policy.withNullDeletes false

let private genScalar =
    Gen.oneof [ Gen.constant Null
                Gen.map Bool Arb.generate<bool>
                Gen.map (int64 >> Int) Arb.generate<int>
                Gen.elements [ "a"; "b"; "" ] |> Gen.map Str ]

let private pool = [ "a"; "b"; "c"; "d" ]

let rec private genObj size : Gen<Node> =
    let sub = if size <= 0 then genScalar else genAny (size / 2)
    gen {
        let! picks = Gen.listOfLength (List.length pool) Arb.generate<bool>
        let keys = List.zip pool picks |> List.filter snd |> List.map fst
        let! vs = Gen.listOfLength (List.length keys) sub
        return Obj(List.zip keys vs)
    }

and private genAny size : Gen<Node> =
    if size <= 0 then genScalar
    else Gen.frequency [ 2, genScalar; 1, Gen.map Arr (Gen.listOfLength 2 (genAny (size / 2))); 3, genObj size ]

/// Documents whose keys have a stable shape ("a", "b" scalars; "o" a nested document; "l" a list).
/// Associativity needs this: see the type-flip counter-example below.
let rec private genTyped size : Gen<Node> =
    gen {
        let! picks = Gen.listOfLength 4 Arb.generate<bool>
        let! a = genScalar
        let! b = genScalar
        let! o = if size <= 0 then Gen.constant (Obj []) else genTyped (size - 1)
        let! l = Gen.listOfLength 2 genScalar |> Gen.map Arr
        let fields = [ "a", a; "b", b; "o", o; "l", l ]
        return Obj(List.zip fields picks |> List.filter snd |> List.map fst)
    }

type TypedArb =
    static member Node() : Arbitrary<Node> = Arb.fromGen (Gen.sized (fun n -> genTyped (min n 3)))

type ObjArb =
    static member Node() : Arbitrary<Node> = Arb.fromGen (Gen.sized (fun n -> genObj (min n 6)))

let private m pol a b = Doc.merge pol a b

[<Property(Arbitrary = [| typeof<TypedArb> |])>]
let ``merge is associative under LastWins, Replace, NullDeletes=false for shape-stable documents`` (a: Node) (b: Node) (c: Node) =
    let left = m lawful a b |> Result.bind (fun ab -> m lawful ab c)
    let right = m lawful b c |> Result.bind (fun bc -> m lawful a bc)
    left = right

[<Property(Arbitrary = [| typeof<TypedArb> |])>]
let ``Obj [] is a left and right identity for shape-stable documents`` (a: Node) =
    m lawful (Obj []) a = Ok a && m lawful a (Obj []) = Ok a

[<Property(Arbitrary = [| typeof<ObjArb> |])>]
let ``Obj [] is a left and right identity under the lawful policy`` (a: Node) =
    m lawful (Obj []) a = Ok a && m lawful a (Obj []) = Ok a

[<Property(Arbitrary = [| typeof<ObjArb> |])>]
let ``merge is idempotent under the lawful policy`` (a: Node) = m lawful a a = Ok a

[<Property(Arbitrary = [| typeof<ObjArb> |])>]
let ``layers equals a left fold of merge under the lawful policy`` (a: Node) (b: Node) (c: Node) =
    let folded = m lawful (Obj []) a |> Result.bind (fun x -> m lawful x b) |> Result.bind (fun x -> m lawful x c)
    match Doc.layers lawful [ "a", a; "b", b; "c", c ], folded with
    | Ok(n, _), Ok f -> n = f
    | _ -> false

[<Property(Arbitrary = [| typeof<ObjArb> |])>]
let ``every node of a layered result has provenance (lawful policy)`` (a: Node) (b: Node) =
    match Doc.layers lawful [ "a", a; "b", b ] with
    | Ok(n, prov) ->
        let rec pointers (p: Path) (n: Node) =
            Path.print p
            :: (match n with
                | Obj kv -> kv |> List.collect (fun (k, v) -> pointers (p @ [ Key k ]) v)
                | Arr xs -> xs |> List.indexed |> List.collect (fun (i, v) -> pointers (p @ [ Index i ]) v)
                | _ -> [])
        pointers [] n |> List.forall (fun k -> Map.containsKey k prov)
    | Error _ -> false

// Documented counter-examples: where associativity breaks.

[<Fact>]
let ``even the lawful policy is not associative when a key flips type (object, scalar, object)`` () =
    // A type mismatch replaces the base wholesale, discarding it. Grouping (a b) c loses a's
    // fields; grouping a (b c) keeps them because b c has already replaced b's scalar.
    let a = Obj [ "x", Obj [ "p", Int 1L ] ]
    let b = Obj [ "x", Int 5L ]
    let c = Obj [ "x", Obj [ "q", Int 2L ] ]
    let left = m lawful a b |> Result.bind (fun ab -> m lawful ab c)
    let right = m lawful b c |> Result.bind (fun bc -> m lawful a bc)
    Assert.Equal(Ok(Obj [ "x", Obj [ "q", Int 2L ] ]), left)
    Assert.Equal(Ok(Obj [ "x", Obj [ "p", Int 1L; "q", Int 2L ] ]), right)

[<Fact>]
let ``NullDeletes=true is not associative: a null delete between two layers hides the middle's effect`` () =
    // Left-grouping deletes x after merging {z} into it; right-grouping lets b's null erase c's
    // nothing-to-delete first, so a's {z} survives.
    let pol = Policy.defaults // NullDeletes = true
    let a = Obj [ "x", Obj [ "z", Int 2L ] ]
    let b = Obj [ "x", Obj [ "y", Int 1L ] ]
    let c = Obj [ "x", Null ]
    let left = m pol a b |> Result.bind (fun ab -> m pol ab c)
    let right = m pol b c |> Result.bind (fun bc -> m pol a bc)
    Assert.Equal(Ok(Obj []), left)
    Assert.Equal(Ok(Obj [ "x", Obj [ "z", Int 2L ] ]), right)
    Assert.NotEqual<Result<Node, Conflict list>>(left, right)

[<Fact>]
let ``NullDeletes=true with Fail is not associative: success on one grouping, conflict on the other`` () =
    let pol = Policy.defaults |> Policy.withConflict Fail
    let a = Obj [ "x", Int 1L ]
    let b = Obj [ "x", Null ]
    let c = Obj [ "x", Int 2L ]
    // (a b) deletes x, then c adds it back: fine. (b c): the null is a base value that differs from 2.
    let left = m pol a b |> Result.bind (fun ab -> m pol ab c)
    let right = m pol b c |> Result.bind (fun bc -> m pol a bc)
    Assert.Equal(Ok(Obj [ "x", Int 2L ]), left)
    Assert.True(Result.isError right)

[<Fact>]
let ``Fail is associative on success but the two groupings report different conflicts`` () =
    let pol = Policy.defaults |> Policy.withNullDeletes false |> Policy.withConflict Fail
    let a = Obj [ "x", Int 1L ]
    let b = Obj [ "x", Int 2L ]
    let c = Obj [ "x", Int 3L ]
    let left = m pol a b |> Result.bind (fun ab -> m pol ab c)
    let right = m pol b c |> Result.bind (fun bc -> m pol a bc)
    let rights (r: Result<Node, Conflict list>) =
        match r with
        | Error cs -> List.map (fun (c: Conflict) -> c.Left, c.Right) cs
        | Ok _ -> []
    Assert.True(Result.isError left && Result.isError right)
    Assert.Equal<(Node * Node) list>([ Int 1L, Int 2L ], rights left)
    Assert.Equal<(Node * Node) list>([ Int 2L, Int 3L ], rights right)

[<Fact>]
let ``Append is associative but not idempotent, Union is idempotent`` () =
    let a, b, c = Obj [ "l", Arr [ Int 1L ] ], Obj [ "l", Arr [ Int 2L ] ], Obj [ "l", Arr [ Int 3L ] ]
    let ap = lawful |> Policy.withLists Append
    let left = m ap a b |> Result.bind (fun ab -> m ap ab c)
    let right = m ap b c |> Result.bind (fun bc -> m ap a bc)
    Assert.Equal(left, right)
    Assert.NotEqual<Result<Node, Conflict list>>(Ok a, m ap a a)
    let un = lawful |> Policy.withLists Union
    Assert.Equal(Ok a, m un a a)
