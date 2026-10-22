module ComputeTests

open Xunit
open Fnode
open Fergi

let private mustMapping (r: Result<Mapping, Error list>) =
    match r with
    | Ok m -> m
    | Error es -> failwithf "bad mapping: %A" (es |> List.map (fun e -> e.Message))

let private person = Obj [ "first", Str "Ada"; "last", Str " Lovelace "; "age", Str "36"; "nick", Null ]

let private singleFile =
    files {
        input "person" "person.yaml"
        output "out.yaml"
    }

let private run (m: Mapping) (docs: Map<string, Node>) =
    Fergi.run m singleFile (Loader.ofNodes docs) |> Result.map (Map.find "out.yaml")

let private personDocs = Map.ofList [ "person.yaml", person ]

let private codes (r: Result<'a, Error list>) =
    match r with
    | Ok _ -> []
    | Error es -> es |> List.map (fun e -> e.Code)


[<Fact>]
let ``compute combines several fields`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/fullName" "join" [ Lit(Str " "); Ptr "person:/first"; Ptr "person:/last" ]
        }
        |> mustMapping
    // `last` has surrounding spaces, so trim it first to prove functions chain through the document
    let m2 =
        mapping {
            inputs [ "person" ]
            compute "/last" "trim" [ Ptr "person:/last" ]
            compute "/fullName" "join" [ Lit(Str " "); Ptr "person:/first"; Ptr "/last" ]
        }
        |> mustMapping
    Assert.Equal(Ok(Obj [ "fullName", Str "Ada  Lovelace " ]), run m personDocs)
    Assert.Equal(Ok(Obj [ "last", Str "Lovelace"; "fullName", Str "Ada Lovelace" ]), run m2 personDocs)

[<Fact>]
let ``compute transforms one value`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/name" "upper" [ Ptr "person:/first" ]
            compute "/age" "toInt" [ Ptr "person:/age" ]
        }
        |> mustMapping
    Assert.Equal(Ok(Obj [ "name", Str "ADA"; "age", Int 36L ]), run m personDocs)

[<Fact>]
let ``compute coalesce skips null`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/who" "coalesce" [ Ptr "person:/nick"; Ptr "person:/first" ]
        }
        |> mustMapping
    Assert.Equal(Ok(Obj [ "who", Str "Ada" ]), run m personDocs)

[<Fact>]
let ``compute creates missing parents`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/meta/title" "concat" [ Lit(Str "Dr "); Ptr "person:/first" ]
        }
        |> mustMapping
    Assert.Equal(Ok(Obj [ "meta", Obj [ "title", Str "Dr Ada" ] ]), run m personDocs)

[<Fact>]
let ``unknown function is reported before any file is loaded`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/x" "nope" [ Ptr "person:/first" ]
        }
        |> mustMapping
    let mutable loads = 0
    let load name =
        loads <- loads + 1
        Loader.ofNodes personDocs name
    let r = Fergi.run m singleFile load
    Assert.Equal<string list>([ ErrorCodes.mappingFunctionUnknown ], codes r)
    Assert.Equal(0, loads)

[<Fact>]
let ``function failure is compute.failed with context`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/age" "toInt" [ Ptr "person:/first" ]
        }
        |> mustMapping
    match run m personDocs with
    | Error [ e ] ->
        Assert.Equal(ErrorCodes.computeFailed, e.Code)
        Assert.Contains("toInt", e.Message)
        Assert.Contains(("function", "toInt"), e.Context)
        Assert.Contains(("kind", "Compute"), e.Context)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``missing source is path.notFound`` () =
    let m =
        mapping {
            inputs [ "person" ]
            compute "/x" "upper" [ Ptr "person:/absent" ]
        }
        |> mustMapping
    Assert.Equal<string list>([ ErrorCode.pathNotFound ], codes (run m personDocs))

[<Fact>]
let ``bad argument pointer is a mapping error`` () =
    let r =
        mapping {
            inputs [ "person" ]
            compute "/x" "upper" [ Ptr "ghost:/first" ]
        }
    Assert.Equal<string list>([ ErrorCodes.mappingInputUnknown ], codes r)

[<Fact>]
let ``registered function is called by name`` () =
    let shout : Function =
        fun args ->
            match args with
            | [ Str s ] -> Ok(Str(s + "!"))
            | _ -> Error "shout expects one string"
    let m =
        mapping {
            inputs [ "person" ]
            compute "/x" "shout" [ Ptr "person:/first" ]
        }
        |> mustMapping
        |> Mapping.withFunction "shout" shout
    Assert.Equal(Ok(Obj [ "x", Str "Ada!" ]), run m personDocs)

[<Fact>]
let ``a compute rule is plain data`` () =
    // built by hand, no builder and no function value: this is what a CLI would read from a file
    let sel s = match Selector.parse s with Ok p -> p | Error e -> failwith e.Message
    let rule =
        Rule.Compute
            { Fn = "concat"
              Args = [ Arg.Input("person", sel "/first"); Arg.Literal(Str "-"); Arg.Doc(sel "/id") ]
              Dest = sel "/key" }
    let m = Mapping.create [ "person" ] [ Rule.Set(sel "/id", Int 7L); rule ]
    Assert.Equal(Ok(Obj [ "id", Int 7L; "key", Str "Ada-7" ]), run m personDocs)

[<Fact>]
let ``compute works inside an each entry`` () =
    let m =
        mapping {
            inputList "people"
            each "people" "/people" (fun r ->
                entry {
                    compute "/label" "join" [ Lit(Str ", "); Ptr(r "/last"); Ptr(r "/first") ]
                })
            sortBy "/label"
        }
        |> mustMapping
    let f =
        files {
            inputList "people" [ "a.yaml"; "b.yaml" ]
            output "out.yaml"
        }
    let docs =
        Map.ofList
            [ "a.yaml", Obj [ "first", Str "Grace"; "last", Str "Hopper" ]
              "b.yaml", Obj [ "first", Str "Ada"; "last", Str "Byron" ] ]
    let r = Fergi.run m f (Loader.ofNodes docs) |> Result.map (Map.find "out.yaml")
    Assert.Equal(
        Ok(Obj [ "people", Arr [ Obj [ "label", Str "Byron, Ada" ]; Obj [ "label", Str "Hopper, Grace" ] ] ]),
        r
    )

[<Fact>]
let ``builtins reject bad arguments`` () =
    let call name args = Functions.builtins |> Map.find name |> fun f -> f args
    Assert.True(Result.isError (call "upper" [ Int 1L ]))
    Assert.True(Result.isError (call "upper" []))
    Assert.True(Result.isError (call "concat" [ Arr [] ]))
    Assert.True(Result.isError (call "toInt" [ Str "12x" ]))
    Assert.True(Result.isError (call "toInt" [ Str "99999999999999999999" ]))
    Assert.Equal(Ok(Int -5L), call "toInt" [ Str "-5" ])
    Assert.Equal(Ok(Str "a-1-true"), call "join" [ Str "-"; Str "a"; Int 1L; Bool true ])

[<Fact>]
let ``a function that throws is compute.failed, not an exception`` () =
    let boom : Function = fun _ -> failwith "boom"
    let m =
        mapping {
            inputs [ "person" ]
            compute "/x" "boom" [ Ptr "person:/first" ]
        }
        |> mustMapping
        |> Mapping.withFunction "boom" boom
    match run m personDocs with
    | Error [ e ] ->
        Assert.Equal(ErrorCodes.computeFailed, e.Code)
        Assert.Contains("boom", e.Message)
        Assert.Contains(("function", "boom"), e.Context)
        Assert.Contains(("dest", "/x"), e.Context)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``Arg.Inputs hands the function every input keyed by name`` () =
    let m =
        mapping {
            inputs [ "person"; "other" ]
            layer [ "person" ]
            func "keys" (fun args ->
                match args with
                | [ Obj kvs ] -> Ok(Str(kvs |> List.map fst |> String.concat ","))
                | _ -> Error "expected one object")
            compute "/keys" "keys" [ AllInputs ]
        }
        |> mustMapping
    let f = { Inputs = Map.ofList [ "person", "person.yaml"; "other", "other.yaml" ]; InputLists = Map.empty; Output = "out.yaml" }
    let docs = Map.ofList [ "person.yaml", person; "other.yaml", Obj [] ]
    match Fergi.run m f (Loader.ofNodes docs) |> Result.map (Map.find "out.yaml") with
    | Ok out -> Assert.Equal(Some(Str "other,person"), Node.tryGet [ Key "keys" ] out |> Result.toOption)
    | Error es -> failwithf "%A" es
