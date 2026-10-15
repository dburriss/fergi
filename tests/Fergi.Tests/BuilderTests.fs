module BuilderTests

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

let private codes (es: Error list) = es |> List.map (fun e -> e.Code)

let private mustMapping (r: Result<Mapping, Error list>) =
    match r with
    | Ok m -> m
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

// ---- ops ----

[<Fact>]
let ``ops yields ops in order`` () =
    let plan =
        ops {
            yield Op.Set(ptr "/a", Int 1L)
            yield Op.Remove(ptr "/b")
        }
    Assert.Equal<Op list>([ Op.Set(ptr "/a", Int 1L); Op.Remove(ptr "/b") ], plan)

[<Fact>]
let ``ops yield! splices a list and if skips`` () =
    let more = [ Op.Set(ptr "/x", Int 2L) ]
    let build isProd =
        ops {
            yield Op.Set(ptr "/owner", Str "energy")
            yield! more
            if isProd then yield Op.Set(ptr "/replicas", Int 3L)
        }
    Assert.Equal(3, List.length (build true))
    Assert.Equal(2, List.length (build false))

[<Fact>]
let ``ops for loop yields per item`` () =
    let plan =
        ops {
            for k in [ "a"; "b"; "c" ] do
                yield Op.Remove(ptr ("/" + k))
        }
    Assert.Equal(3, List.length plan)

[<Fact>]
let ``an ops plan applies with Patch.apply`` () =
    let plan =
        ops {
            yield Op.Set(ptr "/a", Int 5L)
        }
    Assert.Equal(Ok(Obj [ "a", Int 5L ]), Patch.apply plan (Obj [ "a", Int 1L ]) |> Result.mapError codes)

[<Fact>]
let ``empty ops is the empty list`` () =
    let plan : Op list = ops { () }
    Assert.Empty plan

// ---- docs ----

let private store = Map.ofList [ "a.yaml", Obj [ "n", Int 1L ]; "b.yaml", Obj [ "n", Int 2L ] ]
let private load = Loader.ofNodes store

let private loadNode name = load name |> Result.map (fun p -> p.Node)

[<Fact>]
let ``docs and! combines successes`` () =
    let r =
        docs {
            let! a = loadNode "a.yaml"
            and! b = loadNode "b.yaml"
            return [ a; b ]
        }
    Assert.Equal<Result<Node list, Error list>>(Ok [ Obj [ "n", Int 1L ]; Obj [ "n", Int 2L ] ], r)

[<Fact>]
let ``docs and! accumulates the errors of every source`` () =
    let r =
        docs {
            let! _ = loadNode "missing1.yaml"
            and! _ = loadNode "a.yaml"
            and! _ = loadNode "missing2.yaml"
            return ()
        }
    match r with
    | Error es -> Assert.Equal<string list>([ "path.notFound"; "path.notFound" ], codes es)
    | Ok _ -> failwith "expected errors"

[<Fact>]
let ``docs let! stops at the first error`` () =
    let r =
        docs {
            let! _ = loadNode "missing1.yaml"
            let! _ = loadNode "missing2.yaml"
            return ()
        }
    match r with
    | Error es -> Assert.Equal(1, List.length es)
    | Ok _ -> failwith "expected an error"

[<Fact>]
let ``docs return! passes a result through`` () =
    let r =
        docs {
            let! a = loadNode "a.yaml"
            return! Doc.get (sel "/n") a |> Result.mapError (fun e -> [ e ])
        }
    Assert.Equal<Result<Node, Error list>>(Ok(Int 1L), r)

// ---- files ----

[<Fact>]
let ``files builds the Files value`` () =
    let f =
        files {
            input "org" "defaults/org.yaml"
            input "service" "services/billing/service.yaml"
            inputList "repos" [ "r/a.yaml"; "r/b.yaml" ]
            output "deploy/billing.yaml"
        }
    let expected : Files =
        { Inputs = Map.ofList [ "org", "defaults/org.yaml"; "service", "services/billing/service.yaml" ]
          InputLists = Map.ofList [ "repos", [ "r/a.yaml"; "r/b.yaml" ] ]
          Output = "deploy/billing.yaml" }
    Assert.Equal(expected, f)

// ---- mapping ----

[<Fact>]
let ``mapping builds the same rules as the data form`` () =
    let m =
        mapping {
            inputs [ "org"; "team"; "service" ]
            layer [ "org"; "team"; "service" ]
            listsBy "/ports" "name"
            conflict ConflictMode.Fail
            nullDeletes false
            copy "service:/name" "/metadata/name"
            rename "/team" "/owner"
            defaultTo "/replicas" (Int 2L)
            set "/env" (Str "prod")
            drop "/internal"
        }
        |> mustMapping
    let expected =
        Mapping.create
            [ "org"; "team"; "service" ]
            [ Rule.Layer [ "org"; "team"; "service" ]
              Rule.ListsBy(ptr "/ports", "name")
              Rule.Conflict ConflictMode.Fail
              Rule.NullDeletes false
              Rule.Copy(("service", sel "/name"), sel "/metadata/name")
              Rule.Rename(sel "/team", sel "/owner")
              Rule.DefaultTo(sel "/replicas", Int 2L)
              Rule.Set(sel "/env", Str "prod")
              Rule.Drop(sel "/internal") ]
    Assert.Equal<string list>(expected.Inputs, m.Inputs)
    Assert.Equal<Rule list>(expected.Rules, m.Rules)
    Assert.Empty m.Ensure

[<Fact>]
let ``mapping ensure adds a named hook`` () =
    let m =
        mapping {
            inputs [ "org" ]
            ensure "always" (fun _ -> Ok())
        }
        |> mustMapping
    Assert.Equal<string list>([ "always" ], m.Ensure |> List.map fst)

[<Fact>]
let ``mapping reports every bad pointer, not just the first`` () =
    let r =
        mapping {
            inputs [ "org" ]
            set "no-slash" (Int 1L)
            drop "also bad"
            copy "ghost:/a" "/b"
        }
    match r with
    | Error es -> Assert.Equal(3, List.length es)
    | Ok _ -> failwith "expected errors"

[<Fact>]
let ``mapping with the CE runs end to end`` () =
    let m =
        mapping {
            inputs [ "org"; "service" ]
            layer [ "org"; "service" ]
            copy "service:/name" "/metadata/name"
        }
        |> mustMapping
    let f =
        files {
            input "org" "org.yaml"
            input "service" "svc.yaml"
            output "out.yaml"
        }
    let load =
        Loader.ofNodes (Map.ofList [ "org.yaml", Obj [ "replicas", Int 2L ]; "svc.yaml", Obj [ "name", Str "billing" ] ])
    match Fergi.run m f load with
    | Ok out ->
        Assert.Equal(
            Obj [ "replicas", Int 2L; "name", Str "billing"; "metadata", Obj [ "name", Str "billing" ] ],
            Map.find "out.yaml" out
        )
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map (fun e -> e.Code, e.Message))

[<Fact>]
let ``sortBy without a preceding each is an error`` () =
    let r =
        mapping {
            inputs [ "org" ]
            sortBy "/name"
        }
    match r with
    | Error es -> Assert.Equal<string list>([ ErrorCodes.mappingInvalid ], codes es)
    | Ok _ -> failwith "expected an error"
