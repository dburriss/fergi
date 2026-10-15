module AggregationTests

open Xunit
open Fnode
open Fergi

let private sel s =
    match Selector.parse s with
    | Ok p -> p
    | Error e -> failwith e.Message

let private svc (name: string) (owner: string) (tier: int64) =
    Obj [ "name", Str name; "owner", Str owner; "tier", Int tier ]

let private store =
    Map.ofList
        [ "repos/search.yaml", svc "search" "discovery" 2L
          "repos/billing.yaml", svc "billing" "energy" 1L
          "repos/checkout.yaml", svc "checkout" "sales" 1L ]

let private repoFiles = [ "repos/search.yaml"; "repos/billing.yaml"; "repos/checkout.yaml" ]

let private catalogFiles (inputFiles: string list) =
    files {
        inputList "repos" inputFiles
        output "catalog/services.yaml"
    }

let private mustMapping (r: Result<Mapping, Error list>) =
    match r with
    | Ok m -> m
    | Error es -> failwithf "bad mapping: %A" (es |> List.map (fun e -> e.Message))

let private catalog =
    mapping {
        inputList "repos"
        each "repos" "/services" (fun r ->
            entry {
                copy (r "/name") "/name"
                copy (r "/owner") "/team"
                copy (r "/tier") "/tier"
            })
        sortBy "/name"
    }
    |> mustMapping

let private runWith (m: Mapping) (f: Files) (docs: Map<string, Node>) =
    Fergi.run m f (Loader.ofNodes docs) |> Result.map (Map.find f.Output)

let private runOk m f docs =
    match runWith m f docs with
    | Ok n -> n
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

let private runErr m f docs =
    match runWith m f docs with
    | Error es -> es
    | Ok _ -> failwith "expected errors"

let private names (n: Node) =
    match n with
    | Obj [ "services", Arr items ] ->
        items |> List.map (fun i -> match Doc.get (sel "/name") i with Ok(Str s) -> s | _ -> "?")
    | _ -> failwithf "unexpected shape: %A" n

[<Fact>]
let ``one entry per file, sorted by the key`` () =
    let out = runOk catalog (catalogFiles repoFiles) store
    Assert.Equal<string list>([ "billing"; "checkout"; "search" ], names out)

[<Fact>]
let ``entries hold only the copied fields`` () =
    let out = runOk catalog (catalogFiles [ "repos/billing.yaml" ]) store
    Assert.Equal(Obj [ "services", Arr [ Obj [ "name", Str "billing"; "team", Str "energy"; "tier", Int 1L ] ] ], out)

[<Fact>]
let ``without sortBy the file order is kept`` () =
    let m =
        mapping {
            inputList "repos"
            each "repos" "/services" (fun r -> entry { copy (r "/name") "/name" })
        }
        |> mustMapping
    let out = runOk m (catalogFiles repoFiles) store
    Assert.Equal<string list>([ "search"; "billing"; "checkout" ], names out)

[<Fact>]
let ``sorting is stable for equal keys`` () =
    let m =
        mapping {
            inputList "repos"
            each "repos" "/services" (fun r -> entry { copy (r "/name") "/name"; copy (r "/tier") "/tier" })
            sortBy "/tier"
        }
        |> mustMapping
    let out = runOk m (catalogFiles repoFiles) store
    // tier 1: billing then checkout (file order), then tier 2: search
    Assert.Equal<string list>([ "billing"; "checkout"; "search" ], names out)

[<Fact>]
let ``no files gives an empty list`` () =
    let out = runOk catalog (catalogFiles []) store
    Assert.Equal(Obj [ "services", Arr [] ], out)

[<Fact>]
let ``two repos with the same name give two entries`` () =
    let docs = Map.ofList [ "a.yaml", svc "dup" "x" 1L; "b.yaml", svc "dup" "y" 1L ]
    let out = runOk catalog (catalogFiles [ "a.yaml"; "b.yaml" ]) docs
    Assert.Equal<string list>([ "dup"; "dup" ], names out)

[<Fact>]
let ``a missing field names the file and index`` () =
    let docs = Map.ofList [ "a.yaml", svc "a" "x" 1L; "b.yaml", Obj [ "name", Str "b" ] ]
    let es = runErr catalog (catalogFiles [ "a.yaml"; "b.yaml" ]) docs
    Assert.Equal<string list>([ ErrorCode.pathNotFound ], es |> List.map (fun e -> e.Code))
    let e = List.head es
    Assert.Contains(("file", "b.yaml"), e.Context)
    Assert.Contains(("index", "1"), e.Context)
    Assert.Contains(("input", "repos"), e.Context)
    Assert.Contains(("kind", "Collect"), e.Context)

[<Fact>]
let ``a file that fails to load reports every failing file`` () =
    let es = runErr catalog (catalogFiles [ "nope1.yaml"; "repos/billing.yaml"; "nope2.yaml" ]) store
    Assert.Equal(2, List.length es)
    let fileOf (e: Error) = e.Context |> List.find (fun (k, _) -> k = "file") |> snd
    Assert.Equal<string list>([ "nope1.yaml"; "nope2.yaml" ], es |> List.map fileOf)
    Assert.Contains(("index", "2"), List.last es |> fun e -> e.Context)

[<Fact>]
let ``a sort key that is missing is a sortKey error`` () =
    let m =
        mapping {
            inputList "repos"
            each "repos" "/services" (fun r -> entry { copy (r "/name") "/name" })
            sortBy "/tier"
        }
        |> mustMapping
    let es = runErr m (catalogFiles [ "repos/billing.yaml" ]) store
    Assert.Equal<string list>([ ErrorCodes.collectSortKey ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``mixed sort key types are a sortKey error`` () =
    let docs = Map.ofList [ "a.yaml", Obj [ "name", Str "a" ]; "b.yaml", Obj [ "name", Int 1L ] ]
    let m =
        mapping {
            inputList "repos"
            each "repos" "/services" (fun r -> entry { copy (r "/name") "/name" })
            sortBy "/name"
        }
        |> mustMapping
    let es = runErr m (catalogFiles [ "a.yaml"; "b.yaml" ]) docs
    Assert.Equal<string list>([ ErrorCodes.collectSortKey ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``an inputList without a Files entry is rejected before loading`` () =
    let f = { catalogFiles repoFiles with InputLists = Map.empty }
    let es = runErr catalog f store
    Assert.Equal<string list>([ ErrorCodes.mappingInputMissing ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``a Files inputList the mapping does not declare is rejected`` () =
    let f = { catalogFiles repoFiles with InputLists = Map.ofList [ "repos", repoFiles; "extra", [] ] }
    let es = runErr catalog f store
    Assert.Equal<string list>([ ErrorCodes.mappingInputUnknown ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``a Layer cannot use an inputList`` () =
    let m = Mapping.create [] [ Rule.Layer [ "repos" ] ] |> Mapping.withInputLists [ "repos" ]
    let es = runErr m (catalogFiles repoFiles) store
    Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``an entry can only copy from its own input`` () =
    let spec =
        { Input = "repos"
          Into = sel "/services"
          Entry = [ Rule.Copy(("other", sel "/name"), sel "/name") ]
          SortBy = None }
    let m =
        Mapping.create [ "other" ] [ Rule.Collect spec ] |> Mapping.withInputLists [ "repos" ]
    let f =
        { Inputs = Map.ofList [ "other", "repos/billing.yaml" ]
          InputLists = Map.ofList [ "repos", repoFiles ]
          Output = "out.yaml" }
    let es = runErr m f store
    Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], es |> List.map (fun e -> e.Code))

[<Fact>]
let ``aggregation composes with layering and later rules`` () =
    let m =
        mapping {
            inputs [ "org" ]
            inputList "repos"
            layer [ "org" ]
            each "repos" "/services" (fun r -> entry { copy (r "/name") "/name" })
            sortBy "/name"
            set "/kind" (Str "catalog")
        }
        |> mustMapping
    let f =
        files {
            input "org" "org.yaml"
            inputList "repos" repoFiles
            output "catalog.yaml"
        }
    let docs = store |> Map.add "org.yaml" (Obj [ "owner", Str "platform" ])
    let out = runOk m f docs
    Assert.Equal(
        Obj [ "owner", Str "platform"
              "services", Arr [ Obj [ "name", Str "billing" ]; Obj [ "name", Str "checkout" ]; Obj [ "name", Str "search" ] ]
              "kind", Str "catalog" ],
        out
    )

[<Fact>]
let ``plan diffs the aggregated catalog against the current file`` () =
    let f = catalogFiles [ "repos/billing.yaml" ]
    let current = Obj [ "services", Arr [] ]
    let docs = store |> Map.add "catalog/services.yaml" current
    match Fergi.plan catalog f (Loader.ofNodes docs) with
    | Ok p ->
        Assert.Equal(Obj [ "services", Arr [ Obj [ "name", Str "billing"; "team", Str "energy"; "tier", Int 1L ] ] ], p.Result)
        Assert.NotEmpty p.Ops
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))
