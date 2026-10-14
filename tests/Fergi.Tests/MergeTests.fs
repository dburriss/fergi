module MergeTests

open Xunit
open Fnode
open Fergi

let private p = Policy.defaults
let private merge pol a b = Doc.merge pol a b
let private ok (r: Result<Node, Conflict list>) =
    match r with
    | Ok n -> n
    | Error cs -> failwithf "unexpected conflicts: %A" (List.map (fun (c: Conflict) -> Path.print c.Path) cs)
let private conflicts (r: Result<Node, Conflict list>) =
    match r with
    | Error cs -> cs
    | Ok n -> failwithf "expected conflicts, got %A" n
let private ints xs = Arr(List.map (int64 >> Int) xs)
let private item name v = Obj [ "name", Str name; "v", Int v ]

// objects and order

[<Fact>]
let ``objects merge key by key keeping base order and appending new keys`` () =
    let a = Obj [ "z", Int 1L; "a", Obj [ "x", Int 1L; "y", Int 2L ]; "m", Str "keep" ]
    let b = Obj [ "n", Int 9L; "a", Obj [ "y", Int 3L; "w", Int 4L ]; "z", Int 2L ]
    let expected = Obj [ "z", Int 2L; "a", Obj [ "x", Int 1L; "y", Int 3L; "w", Int 4L ]; "m", Str "keep"; "n", Int 9L ]
    Assert.Equal(expected, ok (merge p a b))

[<Fact>]
let ``empty object is a two-sided identity`` () =
    let a = Obj [ "b", Int 1L; "a", Arr [ Null ] ]
    Assert.Equal(a, ok (merge p a (Obj [])))
    Assert.Equal(a, ok (merge p (Obj []) a))

// scalars and types

[<Fact>]
let ``identical values never conflict even under Fail`` () =
    let fail = Policy.withConflict OnConflict.Fail p
    let d = Obj [ "a", Int 1L; "t", Timestamp "2024-01-01T00:00:00Z"; "l", ints [ 1; 2 ]; "o", Obj [ "k", Str "v" ] ]
    Assert.Equal(d, ok (merge fail d d))

[<Fact>]
let ``no Int Float coercion`` () =
    let fail = Policy.withConflict OnConflict.Fail p
    let cs = conflicts (merge fail (Obj [ "n", Int 1L ]) (Obj [ "n", Float 1.0 ]))
    Assert.Equal(1, List.length cs)
    Assert.Equal<Path>([ Key "n" ], cs.Head.Path)
    Assert.Equal(Obj [ "n", Float 1.0 ], ok (merge p (Obj [ "n", Int 1L ]) (Obj [ "n", Float 1.0 ])))

[<Fact>]
let ``timestamps compare by text and differ from strings`` () =
    let fail = Policy.withConflict OnConflict.Fail p
    let t = Timestamp "2024-01-01T00:00:00Z"
    Assert.Equal(t, ok (merge fail t t))
    Assert.Equal(1, List.length (conflicts (merge fail t (Timestamp "2024-01-01T00:00:00+00:00"))))
    Assert.Equal(1, List.length (conflicts (merge fail t (Str "2024-01-01T00:00:00Z"))))

// OnConflict

let private a1 = Obj [ "x", Int 1L; "keep", Bool true ]
let private b2 = Obj [ "x", Int 2L; "new", Bool false ]

[<Fact>]
let ``LastWins takes overlay`` () =
    Assert.Equal(Obj [ "x", Int 2L; "keep", Bool true; "new", Bool false ], ok (merge p a1 b2))

[<Fact>]
let ``FirstWins keeps base but still adds new keys`` () =
    let r = ok (merge (Policy.withConflict OnConflict.FirstWins p) a1 b2)
    Assert.Equal(Obj [ "x", Int 1L; "keep", Bool true; "new", Bool false ], r)

[<Fact>]
let ``Fail reports the conflict with path, values and reason`` () =
    let cs = conflicts (merge (Policy.withConflict OnConflict.Fail p) a1 b2)
    match cs with
    | [ c ] ->
        Assert.Equal<Path>([ Key "x" ], c.Path)
        Assert.Equal(Int 1L, c.Left)
        Assert.Equal(Int 2L, c.Right)
        Assert.Equal(None, c.LeftSource)
        Assert.Equal("values differ", c.Reason)
    | _ -> failwithf "%A" cs

[<Fact>]
let ``type mismatch: LastWins overlay, FirstWins base, Fail conflict`` () =
    let a = Obj [ "k", Obj [ "x", Int 1L ] ]
    let b = Obj [ "k", Arr [ Int 1L ] ]
    Assert.Equal(b, ok (merge p a b))
    Assert.Equal(a, ok (merge (Policy.withConflict OnConflict.FirstWins p) a b))
    let cs = conflicts (merge (Policy.withConflict OnConflict.Fail p) a b)
    Assert.Equal("type mismatch: object vs array", cs.Head.Reason)
    // scalar vs container, and root level
    Assert.Equal(1, List.length (conflicts (merge (Policy.withConflict OnConflict.Fail p) (Int 1L) (Obj []))))
    Assert.Equal(Obj [], ok (merge p (Int 1L) (Obj [])))

[<Fact>]
let ``Resolve decides per path`` () =
    let pick: Path -> Node -> Node -> Result<Node, Conflict> =
        fun path l r ->
            match path, l, r with
            | [ Key "x" ], Int a, Int b -> Ok(Int(a + b))
            | _ ->
                Error { Path = path; Left = l; Right = r; LeftSource = None; RightSource = None; Reason = "custom" }
    let pol = Policy.withConflict (OnConflict.Resolve pick) p
    Assert.Equal(Obj [ "x", Int 3L ], ok (merge pol (Obj [ "x", Int 1L ]) (Obj [ "x", Int 2L ])))
    let cs = conflicts (merge pol (Obj [ "x", Int 1L; "y", Int 1L ]) (Obj [ "x", Int 2L; "y", Int 2L ]))
    Assert.Equal<string list>([ "custom" ], List.map (fun (c: Conflict) -> c.Reason) cs)
    Assert.Equal<Path>([ Key "y" ], cs.Head.Path)

[<Fact>]
let ``Resolve is not called for identical values`` () =
    let pol = Policy.withConflict (OnConflict.Resolve(fun _ _ _ -> failwith "called")) p
    Assert.Equal(Obj [ "x", Int 1L ], ok (merge pol (Obj [ "x", Int 1L ]) (Obj [ "x", Int 1L ])))

[<Fact>]
let ``conflicts accumulate across the whole walk in document order`` () =
    let a = Obj [ "a", Int 1L; "n", Obj [ "b", Str "x"; "c", Int 1L ]; "l", ints [ 1 ] ]
    let b = Obj [ "a", Int 2L; "n", Obj [ "b", Str "y"; "c", Int 1L ]; "l", ints [ 2 ] ]
    let cs = conflicts (merge (Policy.withConflict OnConflict.Fail p) a b)
    Assert.Equal<string list>([ "/a"; "/n/b"; "/l" ], List.map (fun (c: Conflict) -> Path.print c.Path) cs)

[<Fact>]
let ``Conflict.toError uses merge.conflict code, path and sources`` () =
    let c =
        { Path = [ Key "a"; Index 0 ]; Left = Int 1L; Right = Int 2L; LeftSource = Some "org"; RightSource = Some "svc"; Reason = "values differ" }
    let e = Conflict.toError c
    Assert.Equal("merge.conflict", e.Code)
    Assert.Equal<Path>(c.Path, e.Path)
    Assert.Contains("/a/0", e.Message)
    Assert.Contains("org", e.Message)
    Assert.Contains("svc", e.Message)

// NullDeletes

[<Fact>]
let ``NullDeletes true removes the key`` () =
    let a = Obj [ "a", Int 1L; "b", Obj [ "x", Int 1L ]; "c", Int 3L ]
    let b = Obj [ "a", Null; "b", Null; "gone", Null ]
    Assert.Equal(Obj [ "c", Int 3L ], ok (merge p a b))

[<Fact>]
let ``NullDeletes true ignores Fail and does not add absent null keys`` () =
    let r = ok (merge (Policy.withConflict OnConflict.Fail p) (Obj [ "a", Int 1L ]) (Obj [ "a", Null; "n", Null ]))
    Assert.Equal(Obj [], r)

[<Fact>]
let ``NullDeletes false keeps null as an ordinary value`` () =
    let pol = Policy.withNullDeletes false p
    let a = Obj [ "a", Int 1L; "b", Null ]
    Assert.Equal(Obj [ "a", Null; "b", Int 2L; "n", Null ], ok (merge pol a (Obj [ "a", Null; "b", Int 2L; "n", Null ])))
    Assert.Equal(1, List.length (conflicts (merge (Policy.withConflict OnConflict.Fail pol) (Obj [ "a", Int 1L ]) (Obj [ "a", Null ]))))

[<Fact>]
let ``Null outside object keys is ordinary even with NullDeletes`` () =
    Assert.Equal(Null, ok (merge p (Int 1L) Null))
    Assert.Equal(Arr [ Int 1L; Null ], ok (merge (Policy.withLists ListStrategy.Append p) (Arr [ Int 1L ]) (Arr [ Null ])))

// lists

let private listDoc l = Obj [ "l", l ]

[<Fact>]
let ``Replace is the default and LastWins replaces`` () =
    Assert.Equal(listDoc (ints [ 3 ]), ok (merge p (listDoc (ints [ 1; 2 ])) (listDoc (ints [ 3 ]))))

[<Fact>]
let ``Replace honours FirstWins and Fail`` () =
    let a, b = listDoc (ints [ 1 ]), listDoc (ints [ 2 ])
    Assert.Equal(a, ok (merge (Policy.withConflict OnConflict.FirstWins p) a b))
    let cs = conflicts (merge (Policy.withConflict OnConflict.Fail p) a b)
    Assert.Equal<Path>([ Key "l" ], cs.Head.Path)
    Assert.Equal(a, ok (merge (Policy.withConflict OnConflict.Fail p) a a))

[<Fact>]
let ``Append concatenates including duplicates`` () =
    let pol = Policy.withLists ListStrategy.Append p
    Assert.Equal(listDoc (ints [ 1; 2; 2; 3 ]), ok (merge pol (listDoc (ints [ 1; 2 ])) (listDoc (ints [ 2; 3 ]))))
    Assert.Equal(listDoc (ints [ 1; 1 ]), ok (merge pol (listDoc (ints [ 1 ])) (listDoc (ints [ 1 ]))))

[<Fact>]
let ``Union keeps first-seen order with structural equality`` () =
    let pol = Policy.withLists ListStrategy.Union p
    let a = listDoc (Arr [ Int 1L; Obj [ "k", Int 1L ]; Int 2L ])
    let b = listDoc (Arr [ Int 2L; Obj [ "k", Int 1L ]; Int 3L; Float 1.0; Int 3L ])
    Assert.Equal(listDoc (Arr [ Int 1L; Obj [ "k", Int 1L ]; Int 2L; Int 3L; Float 1.0 ]), ok (merge pol a b))

[<Fact>]
let ``ByKey merges same-key items, base first, new appended`` () =
    let pol = Policy.withLists (ListStrategy.ByKey "name") p
    let a = listDoc (Arr [ item "http" 1L; item "grpc" 2L ])
    let b = listDoc (Arr [ Obj [ "name", Str "new"; "v", Int 0L ]; Obj [ "name", Str "grpc"; "v", Int 20L; "x", Int 1L ] ])
    let expected =
        listDoc (Arr [ item "http" 1L; Obj [ "name", Str "grpc"; "v", Int 20L; "x", Int 1L ]; item "new" 0L ])
    Assert.Equal(expected, ok (merge pol a b))

[<Fact>]
let ``ByKey item merge uses the policy (Fail on differing field)`` () =
    let pol = Policy.withLists (ListStrategy.ByKey "name") (Policy.withConflict OnConflict.Fail p)
    let cs = conflicts (merge pol (listDoc (Arr [ item "a" 1L ])) (listDoc (Arr [ item "a" 2L ])))
    Assert.Equal<Path>([ Key "l"; Index 0; Key "v" ], cs.Head.Path)

[<Fact>]
let ``ByKey item missing the key or not an object is a conflict on either side`` () =
    let pol = Policy.withLists (ListStrategy.ByKey "name") p
    let good = listDoc (Arr [ item "a" 1L ])
    let noKey = listDoc (Arr [ Obj [ "v", Int 1L ] ])
    let scalar = listDoc (Arr [ Int 1L ])
    let nonString = listDoc (Arr [ Obj [ "name", Int 1L ] ])
    for bad in [ noKey; scalar; nonString ] do
        let cs1 = conflicts (merge pol good bad)
        Assert.Equal(1, List.length cs1)
        Assert.Equal<Path>([ Key "l" ], cs1.Head.Path)
        Assert.Contains("overlay item 0", cs1.Head.Reason)
        let cs2 = conflicts (merge pol bad good)
        Assert.Contains("base item 0", cs2.Head.Reason)
    // all bad items are reported, and a bad key is a conflict even under LastWins
    let two = listDoc (Arr [ Int 1L; Obj [] ])
    Assert.Equal(2, List.length (conflicts (merge pol good two)))

[<Fact>]
let ``ByKey with empty lists`` () =
    let pol = Policy.withLists (ListStrategy.ByKey "name") p
    let a = listDoc (Arr [ item "a" 1L ])
    Assert.Equal(a, ok (merge pol a (listDoc (Arr []))))
    Assert.Equal(a, ok (merge pol (listDoc (Arr [])) a))

[<Fact>]
let ``ByKey duplicate overlay keys merge into one item`` () =
    let pol = Policy.withLists (ListStrategy.ByKey "name") p
    let r = ok (merge pol (listDoc (Arr [])) (listDoc (Arr [ item "a" 1L; Obj [ "name", Str "a"; "w", Int 2L ] ])))
    Assert.Equal(listDoc (Arr [ Obj [ "name", Str "a"; "v", Int 1L; "w", Int 2L ] ]), r)

[<Fact>]
let ``ListRules match the exact list path and override Lists`` () =
    let pol = p |> Policy.withLists ListStrategy.Append |> Policy.withListsBy [ Key "ports" ] "name"
    let a = Obj [ "ports", Arr [ item "http" 1L ]; "tags", ints [ 1 ]; "svc", Obj [ "ports", ints [ 1 ] ] ]
    let b = Obj [ "ports", Arr [ item "http" 2L ]; "tags", ints [ 2 ]; "svc", Obj [ "ports", ints [ 2 ] ] ]
    let expected =
        Obj [ "ports", Arr [ item "http" 2L ]; "tags", ints [ 1; 2 ]; "svc", Obj [ "ports", ints [ 1; 2 ] ] ]
    Assert.Equal(expected, ok (merge pol a b))

[<Fact>]
let ``ListRules on a nested path and withListRule replaces an earlier rule`` () =
    let path = [ Key "svc"; Key "ports" ]
    let pol = p |> Policy.withListRule path ListStrategy.Append |> Policy.withListRule path ListStrategy.Union
    Assert.Equal<(Path * ListStrategy) list>([ path, ListStrategy.Union ], pol.ListRules)
    let a = Obj [ "svc", Obj [ "ports", ints [ 1 ] ] ]
    let b = Obj [ "svc", Obj [ "ports", ints [ 1; 2 ] ] ]
    Assert.Equal(Obj [ "svc", Obj [ "ports", ints [ 1; 2 ] ] ], ok (merge pol a b))

[<Fact>]
let ``ByKey rule applies to lists inside ByKey items`` () =
    let pol = p |> Policy.withLists ListStrategy.Append |> Policy.withListsBy [ Key "l" ] "name" |> Policy.withListsBy [ Key "l"; Index 0; Key "sub" ] "id"
    let mk v = listDoc (Arr [ Obj [ "name", Str "a"; "sub", Arr [ Obj [ "id", Str "i"; "v", Int v ] ] ] ])
    Assert.Equal(mk 2L, ok (merge pol (mk 1L) (mk 2L)))

[<Fact>]
let ``Policy defaults`` () =
    Assert.Equal(ListStrategy.Replace, Policy.defaults.Lists)
    Assert.Empty(Policy.defaults.ListRules)
    Assert.True(Policy.defaults.NullDeletes)
    Assert.True(match Policy.defaults.Conflict with OnConflict.LastWins -> true | _ -> false)

[<Fact>]
let ``identical NaN leaves do not conflict`` () =
    let a = Obj [ "x", Float nan; "l", Arr [ Float nan ] ]
    let policy = Policy.withConflict OnConflict.Fail p
    Assert.True(Node.equals a (ok (merge policy a a)))
    // Union treats NaN items as already present
    let u = Policy.withLists ListStrategy.Union policy
    Assert.True(Node.equals (Arr [ Float nan ]) (ok (merge u (Arr [ Float nan ]) (Arr [ Float nan ]))))
