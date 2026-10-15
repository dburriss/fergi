module MappingTests

open Xunit
open Fnode
open Fergi

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private ptr s =
    match Path.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let org =
    Obj [ "replicas", Int 2L
          "logLevel", Str "info"
          "ports", Arr [ Obj [ "name", Str "http"; "port", Int 80L ]; Obj [ "name", Str "metrics"; "port", Int 9090L ] ]
          "internal", Obj [ "x", Int 1L ] ]

let svc =
    Obj [ "name", Str "billing"
          "team", Str "energy"
          "logLevel", Null
          "ports", Arr [ Obj [ "name", Str "http"; "port", Int 8080L ]; Obj [ "name", Str "grpc"; "port", Int 50051L ] ] ]

let store =
    Map.ofList [ "org.yaml", org; "svc.yaml", svc ]

/// In-memory loader that counts calls.
let private loader (docs: Map<string, Node>) =
    let calls = ref 0
    let inner = Loader.ofNodes docs
    let load (p: string) =
        calls.Value <- calls.Value + 1
        inner p
    load, calls

let files = { InputLists = Map.empty; Inputs = Map.ofList [ "org", "org.yaml"; "service", "svc.yaml" ]; Output = "out.yaml" }

let private mk rules = Mapping.create [ "org"; "service" ] rules

let private runOk rules =
    let load, _ = loader store
    match Fergi.run (mk rules) files load with
    | Ok m -> Map.find "out.yaml" m
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

let private runErr (m: Mapping) =
    let load, _ = loader store
    match Fergi.run m files load with
    | Error es -> es
    | Ok _ -> failwith "expected errors"

let private codes (es: Error list) = es |> List.map (fun e -> e.Code)

let private layerAll = Rule.Layer [ "org"; "service" ]

[<Fact>]
let ``no rules gives an empty object`` () = Assert.Equal(Obj [], runOk [])

[<Fact>]
let ``run result is keyed by output path`` () =
    let load, _ = loader store
    let r = Fergi.run (mk []) files load
    Assert.Equal(Ok(Map.ofList [ "out.yaml", Obj [] ]), r)

[<Fact>]
let ``Layer merges inputs with null deleting`` () =
    let r = runOk [ layerAll ]
    Assert.Equal(Ok(Int 2L), Doc.get (sel "/replicas") r)
    Assert.True(Result.isError (Doc.get (sel "/logLevel") r))
    Assert.Equal(Ok(Str "billing"), Doc.get (sel "/name") r)

[<Fact>]
let ``ListsBy merges the list by key`` () =
    let r = runOk [ layerAll; Rule.ListsBy(ptr "/ports", "name") ]
    let expected =
        Arr [ Obj [ "name", Str "http"; "port", Int 8080L ]
              Obj [ "name", Str "metrics"; "port", Int 9090L ]
              Obj [ "name", Str "grpc"; "port", Int 50051L ] ]
    Assert.Equal(Ok expected, Doc.get (sel "/ports") r)

[<Fact>]
let ``without ListsBy lists are replaced`` () =
    let r = runOk [ layerAll ]
    Assert.Equal(Ok(Int 2L), Result.map (fun n -> match n with Arr xs -> Int(int64 (List.length xs)) | _ -> Null) (Doc.get (sel "/ports") r))

[<Fact>]
let ``ListsBy before or after Layer is equivalent`` () =
    Assert.Equal(
        runOk [ layerAll; Rule.ListsBy(ptr "/ports", "name") ],
        runOk [ Rule.ListsBy(ptr "/ports", "name"); layerAll ]
    )

[<Fact>]
let ``Copy reads the named input not the merged document`` () =
    // merged /replicas is 2 (org); copy from service /name, and from org /replicas explicitly
    let r =
        runOk
            [ layerAll
              Rule.Copy(("service", sel "/name"), sel "/metadata/name")
              Rule.Copy(("org", sel "/logLevel"), sel "/orgLevel") ]
    Assert.Equal(Ok(Str "billing"), Doc.get (sel "/metadata/name") r)
    // logLevel was null-deleted from the merged doc but still exists in org
    Assert.Equal(Ok(Str "info"), Doc.get (sel "/orgLevel") r)

[<Fact>]
let ``Copy from a missing source path is path.notFound`` () =
    let es = runErr (mk [ Rule.Copy(("service", sel "/nope"), sel "/x") ])
    Assert.Equal<string list>([ ErrorCode.pathNotFound ], codes es)

[<Fact>]
let ``Copy supports Where selectors on source and dest`` () =
    let r =
        runOk
            [ layerAll
              Rule.ListsBy(ptr "/ports", "name")
              Rule.Copy(("org", sel "/ports/[name=metrics]/port"), sel "/ports/[name=http]/port") ]
    Assert.Equal(Ok(Int 9090L), Doc.get (sel "/ports/[name=http]/port") r)

[<Fact>]
let ``Rename moves a value`` () =
    let r = runOk [ layerAll; Rule.Rename(sel "/team", sel "/owner") ]
    Assert.Equal(Ok(Str "energy"), Doc.get (sel "/owner") r)
    Assert.True(Result.isError (Doc.get (sel "/team") r))

[<Fact>]
let ``Rename of a missing path is an error`` () =
    let es = runErr (mk [ layerAll; Rule.Rename(sel "/nope", sel "/x") ])
    Assert.Equal<string list>([ ErrorCode.pathNotFound ], codes es)

[<Fact>]
let ``DefaultTo sets when absent`` () =
    let r = runOk [ layerAll; Rule.DefaultTo(sel "/region", Str "eu") ]
    Assert.Equal(Ok(Str "eu"), Doc.get (sel "/region") r)

[<Fact>]
let ``DefaultTo keeps a present value`` () =
    let r = runOk [ layerAll; Rule.DefaultTo(sel "/replicas", Int 9L) ]
    Assert.Equal(Ok(Int 2L), Doc.get (sel "/replicas") r)

[<Fact>]
let ``DefaultTo keeps a present null`` () =
    let r = runOk [ Rule.Set(sel "/a", Null); Rule.DefaultTo(sel "/a", Int 1L) ]
    Assert.Equal(Ok Null, Doc.get (sel "/a") r)

[<Fact>]
let ``Set creates and overwrites`` () =
    let r = runOk [ layerAll; Rule.Set(sel "/env", Obj [ "A", Str "1" ]); Rule.Set(sel "/replicas", Int 7L) ]
    Assert.Equal(Ok(Obj [ "A", Str "1" ]), Doc.get (sel "/env") r)
    Assert.Equal(Ok(Int 7L), Doc.get (sel "/replicas") r)

[<Fact>]
let ``Set through a Where selector edits the item`` () =
    let r =
        runOk [ layerAll; Rule.ListsBy(ptr "/ports", "name"); Rule.Set(sel "/ports/[name=grpc]/port", Int 1L) ]
    Assert.Equal(Ok(Int 1L), Doc.get (sel "/ports/[name=grpc]/port") r)

[<Fact>]
let ``Set through a Where that matches nothing is an error`` () =
    let es = runErr (mk [ layerAll; Rule.Set(sel "/ports/[name=zzz]/port", Int 1L) ])
    Assert.Equal<string list>([ ErrorCodes.selectorNoMatch ], codes es)

[<Fact>]
let ``Set Copy DefaultTo and Rename create missing parent objects`` () =
    let r =
        runOk
            [ Rule.Set(sel "/a/b/c", Int 1L)
              Rule.DefaultTo(sel "/d/e", Int 2L)
              Rule.Rename(sel "/a/b/c", sel "/f/g/h")
              Rule.Set(sel "/a/b/z", Int 3L) ]
    Assert.Equal(Ok(Int 1L), Doc.get (sel "/f/g/h") r)
    Assert.Equal(Ok(Int 2L), Doc.get (sel "/d/e") r)
    Assert.Equal(Ok(Int 3L), Doc.get (sel "/a/b/z") r)

[<Fact>]
let ``Set under a scalar parent is a type mismatch`` () =
    let es = runErr (mk [ Rule.Set(sel "/a", Int 1L); Rule.Set(sel "/a/b", Int 2L) ])
    Assert.Equal<string list>([ ErrorCode.typeMismatch ], codes es)

[<Fact>]
let ``Drop removes`` () =
    let r = runOk [ layerAll; Rule.Drop(sel "/internal") ]
    Assert.True(Result.isError (Doc.get (sel "/internal") r))

[<Fact>]
let ``Drop of an absent path is a no-op`` () =
    Assert.Equal(runOk [ layerAll ], runOk [ layerAll; Rule.Drop(sel "/nope"); Rule.Drop(sel "/ports/[name=zzz]") ])

[<Fact>]
let ``Drop through a Where removes the item`` () =
    let r = runOk [ layerAll; Rule.ListsBy(ptr "/ports", "name"); Rule.Drop(sel "/ports/[name=http]") ]
    Assert.True(Result.isError (Doc.get (sel "/ports/[name=http]") r))
    Assert.Equal(Ok(Int 9090L), Doc.get (sel "/ports/[name=metrics]/port") r)

[<Fact>]
let ``rules run top to bottom: Set then Rename then Drop`` () =
    let r =
        runOk
            [ Rule.Set(sel "/a", Int 1L)
              Rule.Rename(sel "/a", sel "/b")
              Rule.Set(sel "/c", Int 2L)
              Rule.Drop(sel "/c") ]
    Assert.Equal(Obj [ "b", Int 1L ], r)

[<Fact>]
let ``order matters: Drop before Set leaves the value`` () =
    Assert.Equal(Obj [ "a", Int 1L ], runOk [ Rule.Drop(sel "/a"); Rule.Set(sel "/a", Int 1L) ])
    Assert.Equal(Obj [], runOk [ Rule.Set(sel "/a", Int 1L); Rule.Drop(sel "/a") ])

[<Fact>]
let ``a later rule sees the result of an earlier one`` () =
    let r = runOk [ Rule.Set(sel "/x", Int 1L); Rule.DefaultTo(sel "/x", Int 2L); Rule.DefaultTo(sel "/y", Int 3L) ]
    Assert.Equal(Obj [ "x", Int 1L; "y", Int 3L ], r)

[<Fact>]
let ``a failing rule stops the run and reports its index`` () =
    let es = runErr (mk [ Rule.Set(sel "/a", Int 1L); Rule.Rename(sel "/zz", sel "/b"); Rule.Set(sel "/c", Int 1L) ])
    Assert.Equal(1, List.length es)
    let e = List.head es
    Assert.Contains(("rule", "1"), e.Context)
    Assert.Contains(("kind", "Rename"), e.Context)
    Assert.StartsWith("rule 1: kind Rename:", Error.describe e)

// ---- validation and loading ----

[<Fact>]
let ``unknown input names are rejected before any load`` () =
    let load, calls = loader store
    let m = Mapping.create [ "org"; "service" ] [ Rule.Layer [ "org"; "ghost" ]; Rule.Copy(("phantom", sel "/a"), sel "/b") ]
    let r = Fergi.run m files load
    Assert.Equal(0, calls.Value)
    match r with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mappingInputUnknown; ErrorCodes.mappingInputUnknown ], codes es)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``missing Files entry and undeclared Files entry are rejected before any load`` () =
    let load, calls = loader store
    let f = { files with Inputs = Map.ofList [ "org", "org.yaml"; "extra", "x.yaml" ] }
    let r = Fergi.run (mk []) f load
    Assert.Equal(0, calls.Value)
    match r with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mappingInputMissing; ErrorCodes.mappingInputUnknown ], codes es)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``more than one Layer rule is mapping.invalid`` () =
    let load, calls = loader store
    let r = Fergi.run (mk [ Rule.Layer [ "org" ]; Rule.Layer [ "service" ] ]) files load
    Assert.Equal(0, calls.Value)
    match r with
    | Error es -> Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], codes es)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``every load error is reported`` () =
    let load, _ = loader Map.empty
    match Fergi.run (mk [ layerAll ]) files load with
    | Error es ->
        Assert.Equal(2, List.length es)
        Assert.Contains(("input", "org"), es.[0].Context)
        Assert.Contains(("input", "service"), es.[1].Context)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``every error of one file survives with context, Path and Location`` () =
    let span l c = { Start = { Line = l; Column = c }; End = { Line = l; Column = c + 1 } }
    let e1 = { Error.create ErrorCode.parse "bad one" (ptr "/a") with Location = Some(span 1 2) }
    let e2 = { Error.create ErrorCode.parse "bad two" (ptr "/b") with Location = Some(span 3 4) }
    let ok = Loader.ofNodes store
    let load f = if f = "svc.yaml" then Error [ e1; e2 ] else ok f
    match Fergi.run (mk [ layerAll ]) files load with
    | Error [ a; b ] ->
        Assert.Equal("bad one", a.Message)
        Assert.Equal("bad two", b.Message)
        Assert.Equal<(string * string) list>([ "file", "svc.yaml"; "input", "service" ], a.Context)
        Assert.Equal("input service: file svc.yaml: bad one", Error.describe a)
        Assert.Equal("input service: file svc.yaml: bad two", Error.describe b)
        Assert.True((ptr "/a" = a.Path))
        Assert.True((ptr "/b" = b.Path))
        Assert.Equal(e1.Location, a.Location)
        Assert.Equal(e2.Location, b.Location)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``errors of two failing inputs accumulate in input order`` () =
    let e m = Error.create ErrorCode.parse m []
    let load f = if f = "org.yaml" then Error [ e "o1"; e "o2" ] else Error [ e "s1" ]
    match Fergi.run (mk [ layerAll ]) files load with
    | Error es ->
        Assert.Equal<string list>(
            [ "input org: file org.yaml: o1"; "input org: file org.yaml: o2"; "input service: file svc.yaml: s1" ],
            es |> List.map Error.describe)
    | Ok _ -> failwith "expected errors"

[<Fact>]
let ``plan treats any load error of the output as absent`` () =
    let ok = Loader.ofNodes store
    let load f = if f = "out.yaml" then Error [ Error.create ErrorCode.parse "x" []; Error.create ErrorCode.parse "y" [] ] else ok f
    match Fergi.plan (mk [ layerAll ]) files load with
    | Ok p -> Assert.Equal<Op list>([ Op.Set([], p.Result) ], p.Ops)
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``a conflict with a recorded span carries it and names the file`` () =
    let span = { Start = { Line = 12; Column = 3 }; End = { Line = 12; Column = 9 } }
    let docs = Map.ofList [ "org.yaml", Obj [ "ports", Arr [ Obj [ "port", Int 1L ] ] ]; "svc.yaml", Obj [ "ports", Arr [ Obj [ "name", Str "a" ] ] ] ]
    let inner = Loader.ofNodes docs
    let load f =
        inner f |> Result.map (fun p -> if f = "svc.yaml" then { p with Locations = Map.ofList [ "/ports", span ] } else p)
    match Fergi.run (mk [ layerAll; Rule.ListsBy(ptr "/ports", "name") ]) files load with
    | Error es ->
        let e = es |> List.find (fun x -> x.Code = ErrorCodes.mergeConflict)
        Assert.Equal(Some span, e.Location)
        Assert.Contains("input 'service' (svc.yaml) at 12:3", e.Message)
    | Ok _ -> failwith "expected conflict"

[<Fact>]
let ``a conflict without any span has no Location`` () =
    let docs = Map.ofList [ "org.yaml", Obj [ "ports", Arr [ Obj [ "port", Int 1L ] ] ]; "svc.yaml", Obj [ "ports", Arr [ Obj [ "name", Str "a" ] ] ] ]
    let load, _ = loader docs
    match Fergi.run (mk [ layerAll; Rule.ListsBy(ptr "/ports", "name") ]) files load with
    | Error es -> Assert.True(es |> List.forall (fun e -> e.Location = None))
    | Ok _ -> failwith "expected conflict"

[<Fact>]
let ``all inputs load once each, including ones not layered`` () =
    let load, calls = loader store
    Fergi.run (mk [ Rule.Layer [ "org" ] ]) files load |> ignore
    Assert.Equal(2, calls.Value)

[<Fact>]
let ``merge conflicts surface as errors`` () =
    // Policy.defaults is LastWins, so provoke a conflict via a non-object layer under Fail is impossible
    // in data form; a layer that is not an object conflicts with the initial Obj [] only under Fail.
    // The reachable conflict is ByKey on items without the key.
    let docs = Map.ofList [ "org.yaml", Obj [ "ports", Arr [ Obj [ "port", Int 1L ] ] ]; "svc.yaml", Obj [ "ports", Arr [ Obj [ "name", Str "a" ] ] ] ]
    let load, _ = loader docs
    match Fergi.run (mk [ layerAll; Rule.ListsBy(ptr "/ports", "name") ]) files load with
    | Error es -> Assert.Equal<string list>([ ErrorCodes.mergeConflict ], codes (List.truncate 1 es))
    | Ok _ -> failwith "expected conflict"

// ---- Ensure ----

let private nameRequired (n: Node) =
    match Doc.get (sel "/name") n with
    | Ok _ -> Ok()
    | Error _ -> Error [ Error.create ErrorCode.pathNotFound "name is required" (ptr "/name") ]

[<Fact>]
let ``Ensure passing returns the result`` () =
    let load, _ = loader store
    let m = mk [ layerAll ] |> Mapping.ensure "name" nameRequired
    Assert.True(Result.isOk (Fergi.run m files load))

[<Fact>]
let ``Ensure failing returns ensure.failed and nothing`` () =
    let load, _ = loader store
    let m = mk [ Rule.Layer [ "org" ] ] |> Mapping.ensure "name" nameRequired
    match Fergi.run m files load with
    | Error [ e ] ->
        Assert.Equal(ErrorCodes.ensureFailed, e.Code)
        Assert.True((ptr "/name" = e.Path))
        Assert.Contains("name", e.Message)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``Ensure failure with no detail still reports one error`` () =
    let load, _ = loader store
    let m = mk [] |> Mapping.ensure "boom" (fun _ -> Error [])
    match Fergi.run m files load with
    | Error [ e ] -> Assert.Equal(ErrorCodes.ensureFailed, e.Code)
    | other -> failwithf "unexpected %A" other

// ---- plan ----

[<Fact>]
let ``plan against an absent output diffs from Null`` () =
    let load, _ = loader store
    match Fergi.plan (mk [ layerAll ]) files load with
    | Ok p ->
        Assert.Equal("out.yaml", p.Output)
        Assert.Equal<Op list>([ Op.Set([], p.Result) ], p.Ops)
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``plan against an existing output returns minimal ops that apply`` () =
    let current = Obj [ "replicas", Int 1L; "old", Bool true ]
    let load, _ = loader (Map.add "out.yaml" current store)
    match Fergi.plan (mk [ Rule.Layer [ "org" ]; Rule.Drop(sel "/internal"); Rule.Drop(sel "/ports") ]) files load with
    | Ok p ->
        Assert.Equal(Ok p.Result, Patch.apply p.Ops current)
        Assert.Contains(Op.Remove(ptr "/old"), p.Ops)
        Assert.Contains(Op.Set(ptr "/replicas", Int 2L), p.Ops)
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``plan of an up to date output has no ops`` () =
    let first = (let load, _ = loader store in Fergi.run (mk [ layerAll ]) files load)
    let out = first |> Result.map (Map.find "out.yaml")
    let load, _ = loader (Map.add "out.yaml" (Result.defaultValue Null out) store)
    match Fergi.plan (mk [ layerAll ]) files load with
    | Ok p -> Assert.Empty(p.Ops)
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``plan provenance names the last layer per leaf`` () =
    let load, _ = loader store
    match Fergi.plan (mk [ layerAll ]) files load with
    | Ok p ->
        Assert.Equal(Some "org", Map.tryFind "/replicas" p.Provenance)
        Assert.Equal(Some "service", Map.tryFind "/name" p.Provenance)
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``plan surfaces run errors`` () =
    let load, _ = loader Map.empty
    Assert.True(Result.isError (Fergi.plan (mk [ layerAll ]) files load))

// ---- runFiles and SourcePath ----

[<Fact>]
let ``runFiles runs a one-off`` () =
    let load, _ = loader store
    let r =
        Fergi.runFiles [ "org", "org.yaml"; "service", "svc.yaml" ] "o.yaml" [ layerAll; Rule.Copy(("service", sel "/name"), sel "/id") ] load
    match r with
    | Ok m -> Assert.Equal(Ok(Str "billing"), Doc.get (sel "/id") (Map.find "o.yaml" m))
    | Error _ -> failwith "expected ok"

[<Fact>]
let ``SourcePath parses qualified and unqualified`` () =
    Assert.Equal(Ok { Input = Some "service"; Path = sel "/name" }, SourcePath.parse [ "service" ] "service:/name")
    Assert.Equal(Ok { Input = None; Path = sel "/name" }, SourcePath.parse [ "service" ] "/name")
    Assert.Equal(Ok { Input = None; Path = [] }, SourcePath.parse [] "")
    Assert.Equal(Ok { Input = Some "org"; Path = sel "/ports/[name=a:b]" }, SourcePath.parse [ "org" ] "org:/ports/[name=a:b]")
    Assert.Equal("service:/name", SourcePath.print { Input = Some "service"; Path = sel "/name" })

[<Fact>]
let ``SourcePath errors cleanly`` () =
    let code r = match r with Error(e: Error) -> e.Code | Ok _ -> "ok"
    Assert.Equal(ErrorCodes.mappingInputUnknown, code (SourcePath.parse [ "a" ] "b:/x"))
    Assert.Equal(ErrorCode.pathInvalid, code (SourcePath.parse [ "a" ] "a:x"))
    Assert.Equal(ErrorCode.pathInvalid, code (SourcePath.parse [ "a" ] "nocolon"))
    Assert.Equal(ErrorCode.pathInvalid, code (SourcePath.parse [ "a" ] ":/x"))
    Assert.Equal(ErrorCode.pathInvalid, code (SourcePath.parse [] "/a/~2"))

[<Fact>]
let ``Mapping.copy builds a Copy rule`` () =
    Assert.Equal(Ok(Rule.Copy(("service", sel "/name"), sel "/m/name")), Mapping.copy "service:/name" "/m/name" [ "service" ])
    Assert.True(Result.isError (Mapping.copy "/name" "/m" [ "service" ]))

// ---- policy rules: Conflict and NullDeletes ----

let private strictStore =
    Map.ofList
        [ "org.yaml", Obj [ "a", Int 1L; "b", Int 1L; "c", Int 1L; "gone", Str "x" ]
          "svc.yaml", Obj [ "a", Int 2L; "b", Int 2L; "c", Int 1L; "gone", Null ] ]

let private runStrict rules =
    let load, _ = loader strictStore
    Fergi.run (mk rules) files load

let private strictOk rules =
    match runStrict rules with
    | Ok m -> Map.find "out.yaml" m
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

[<Fact>]
let ``ConflictMode maps to the matching OnConflict`` () =
    Assert.True(match ConflictMode.toOnConflict ConflictMode.LastWins with OnConflict.LastWins -> true | _ -> false)
    Assert.True(match ConflictMode.toOnConflict ConflictMode.FirstWins with OnConflict.FirstWins -> true | _ -> false)
    Assert.True(match ConflictMode.toOnConflict ConflictMode.Fail with OnConflict.Fail -> true | _ -> false)

[<Fact>]
let ``Conflict Fail through a mapping accumulates all conflicts with source names`` () =
    match runStrict [ layerAll; Rule.Conflict ConflictMode.Fail ] with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mergeConflict; ErrorCodes.mergeConflict ], codes es)
        Assert.Equal<Path list>([ ptr "/a"; ptr "/b" ], es |> List.map (fun e -> e.Path))
        for e in es do
            Assert.Contains("left from 'org'", e.Message)
            Assert.Contains("right from 'service'", e.Message)
    | Ok _ -> failwith "expected conflicts"

[<Fact>]
let ``the same inputs succeed by default and Conflict LastWins is the default`` () =
    let expected = Obj [ "a", Int 2L; "b", Int 2L; "c", Int 1L ]
    Assert.Equal(expected, strictOk [ layerAll ])
    Assert.Equal(expected, strictOk [ layerAll; Rule.Conflict ConflictMode.LastWins ])

[<Fact>]
let ``Conflict FirstWins keeps the earlier layer and provenance names it`` () =
    let load, _ = loader strictStore
    match Fergi.plan (mk [ layerAll; Rule.Conflict ConflictMode.FirstWins ]) files load with
    | Ok p ->
        Assert.Equal(Obj [ "a", Int 1L; "b", Int 1L; "c", Int 1L ], p.Result)
        Assert.Equal(Some "org", Map.tryFind "/a" p.Provenance)
        Assert.Equal(Some "org", Map.tryFind "/b" p.Provenance)
    | Error es -> failwithf "%A" es

[<Fact>]
let ``NullDeletes true deletes the key by default`` () =
    let out = strictOk [ layerAll ]
    Assert.True(Result.isError (Doc.get (sel "/gone") out))

[<Fact>]
let ``NullDeletes false keeps null as an explicit value attributed to its layer`` () =
    let load, _ = loader strictStore
    match Fergi.plan (mk [ layerAll; Rule.NullDeletes false ]) files load with
    | Ok p ->
        Assert.Equal(Ok Null, Doc.get (sel "/gone") p.Result)
        Assert.Equal(Some "service", Map.tryFind "/gone" p.Provenance)
    | Error es -> failwithf "%A" es

[<Fact>]
let ``NullDeletes true is the same as leaving the rule out`` () =
    Assert.Equal(strictOk [ layerAll ], strictOk [ layerAll; Rule.NullDeletes true ])

[<Fact>]
let ``duplicate Conflict rules are mapping.invalid before any load`` () =
    let load, calls = loader strictStore
    let r = Fergi.run (mk [ layerAll; Rule.Conflict ConflictMode.Fail; Rule.Conflict ConflictMode.Fail ]) files load
    Assert.Equal(0, calls.Value)
    match r with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], codes es)
        Assert.Contains("Conflict", es.[0].Message)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``duplicate NullDeletes rules are mapping.invalid`` () =
    match runStrict [ layerAll; Rule.NullDeletes true; Rule.NullDeletes false ] with
    | Error es ->
        Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], codes es)
        Assert.Contains("NullDeletes", es.[0].Message)
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``duplicate Layer, Conflict and NullDeletes rules are all reported`` () =
    match runStrict [ Rule.Layer [ "org" ]; Rule.Layer [ "service" ]; Rule.Conflict ConflictMode.Fail; Rule.Conflict ConflictMode.LastWins; Rule.NullDeletes true; Rule.NullDeletes true ] with
    | Error es -> Assert.Equal<string list>([ ErrorCodes.mappingInvalid; ErrorCodes.mappingInvalid; ErrorCodes.mappingInvalid ], codes es)
    | Ok _ -> failwith "expected errors"

[<Fact>]
let ``policy rule order relative to Layer does not matter`` () =
    let policyRules = [ Rule.Conflict ConflictMode.Fail; Rule.NullDeletes false; Rule.ListsBy(ptr "/ports", "name") ]
    let before = runStrict (policyRules @ [ layerAll ])
    let after = runStrict (layerAll :: policyRules)
    let mixed = runStrict [ Rule.Conflict ConflictMode.Fail; layerAll; Rule.NullDeletes false ]
    let msgs r = match r with Error es -> es |> List.map (fun e -> e.Code, e.Path, e.Message) | Ok _ -> failwith "expected conflicts"
    Assert.Equal<(string * Path * string) list>(msgs before, msgs after)
    Assert.Equal<(string * Path * string) list>(msgs before, msgs mixed)
    // and for a succeeding policy the documents are identical too
    let ok1 = strictOk [ Rule.NullDeletes false; Rule.Conflict ConflictMode.FirstWins; layerAll ]
    let ok2 = strictOk [ layerAll; Rule.Conflict ConflictMode.FirstWins; Rule.NullDeletes false ]
    Assert.Equal(ok1, ok2)
    // NullDeletes=false makes the null an ordinary value, so FirstWins keeps the base "x" (not deleted)
    Assert.Equal(Ok(Str "x"), Doc.get (sel "/gone") ok1)

[<Fact>]
let ``Mapping.validate reports names without Files or a loader`` () =
    let sel s = (Selector.parse s |> Result.defaultWith (fun _ -> failwith "selector"))
    let m =
        Mapping.create [ "a" ] [ Rule.Layer [ "a"; "zzz" ]; Rule.Compute { Fn = "nope"; Args = []; Dest = sel "/x" } ]
    let codes = Mapping.validate m |> List.map (fun e -> e.Code) |> List.sort
    Assert.Equal<string list>([ ErrorCodes.mappingFunctionUnknown; ErrorCodes.mappingInputUnknown ], codes)
    Assert.Empty(Mapping.validate (Mapping.create [ "a" ] [ Rule.Layer [ "a" ] ]))
