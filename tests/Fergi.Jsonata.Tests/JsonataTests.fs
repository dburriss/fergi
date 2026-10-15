module JsonataTests

open Xunit
open Fnode
open Fergi
open Fergi.Jsonata

let private person =
    Obj [ "first", Str "Ada"; "last", Str " Lovelace "; "age", Str "36"; "nick", Null; "tier", Int 1L ]

let private ev expr = Jsonata.eval expr person []

[<Fact>]
let ``combines and transforms fields`` () =
    Assert.Equal(Ok(Str "Ada Lovelace"), ev "first & ' ' & $trim(last)")
    Assert.Equal(Ok(Int 37L), ev "$number(age) + 1" |> Result.map (function Float f when f = 37.0 -> Int 37L | n -> n))

[<Fact>]
let ``conditionals and null`` () =
    Assert.Equal(Ok(Str "Ada"), ev "nick ? nick : first")
    Assert.Equal(Ok Null, ev "nick")

[<Fact>]
let ``array filter and reshape`` () =
    let doc = Obj [ "services", Arr [ Obj [ "name", Str "b"; "tier", Int 2L ]; Obj [ "name", Str "a"; "tier", Int 1L ] ] ]
    Assert.Equal(Ok(Str "a"), Jsonata.eval "services[tier = 1].name" doc [])
    Assert.Equal(Ok(Arr [ Str "a"; Str "b" ]), Jsonata.eval "$sort(services.name)" doc [])
    Assert.Equal(Ok(Int 3L), Jsonata.eval "$sum(services.tier)" doc [])

[<Fact>]
let ``int64 survives a round trip but arithmetic is double`` () =
    let big = Int 9007199254740993L // 2^53 + 1
    Assert.Equal(Ok big, Jsonata.eval "$" big [])
    Assert.Equal(Ok big, Jsonata.eval "a" (Obj [ "a", big ]) [])
    // what does arithmetic do to it?
    printfn "2^53+1 + 0  = %A" (Jsonata.eval "a + 0" (Obj [ "a", big ]) [])
    printfn "int result  = %A" (Jsonata.eval "1 + 2" Null [])
    printfn "float       = %A" (Jsonata.eval "1.5 + 1" Null [])

[<Fact>]
let ``undefined is an error not a silent nothing`` () =
    match ev "nope.deeper" with
    | Error m -> Assert.Contains("undefined", m)
    | Ok n -> failwithf "unexpected %A" n

[<Fact>]
let ``syntax and runtime errors are results`` () =
    match ev "first &" with
    | Error m -> printfn "syntax: %s" m
    | Ok n -> failwithf "unexpected %A" n
    match ev "$number('x')" with
    | Error m -> printfn "runtime: %s" m
    | Ok n -> failwithf "unexpected %A" n

[<Fact>]
let ``timestamps and nested objects pass through`` () =
    let doc = Obj [ "at", Timestamp "2026-01-02T03:04:05Z"; "o", Obj [ "k", Arr [ Int 1L; Bool true ] ] ]
    Assert.Equal(Ok(Str "2026-01-02T03:04:05Z"), Jsonata.eval "at" doc [])
    Assert.Equal(Ok(Obj [ "k", Arr [ Int 1L; Bool true ] ]), Jsonata.eval "o" doc [])

[<Fact>]
let ``object key order is kept`` () =
    let doc = Obj [ "z", Int 1L; "a", Int 2L; "m", Int 3L ]
    Assert.Equal(Ok doc, Jsonata.eval "$" doc [])

[<Fact>]
let ``singleton arrays collapse like in jsonata`` () =
    let doc = Obj [ "xs", Arr [ Int 1L ] ]
    printfn "singleton: %A" (Jsonata.eval "xs" doc [])
    printfn "keep:      %A" (Jsonata.eval "xs[]" doc [])

[<Fact>]
let ``runaway recursion does not take the process down`` () =
    let r = Jsonata.eval "( $f := function($n){ $f($n + 1) }; $f(0) )" Null []
    printfn "recursion: %A" r

// ---- inside a mapping ----

let private sel s = match Selector.parse s with Ok p -> p | Error e -> failwith e.Message

[<Fact>]
let ``compute rule with a jsonata expression`` () =
    let m =
        Mapping.create [ "person" ] [
            Rule.Compute
                { Fn = "jsonata"
                  Args = [ Arg.Literal(Str "first & ' ' & $trim(last)"); Arg.Input("person", sel "") ]
                  Dest = sel "/fullName" } ]
        |> Jsonata.register
    let f = { Inputs = Map.ofList [ "person", "p" ]; InputLists = Map.empty; Output = "o" }
    let r = Fergi.run m f (Loader.ofNodes (Map.ofList [ "p", person ]))
    Assert.Equal(Ok(Obj [ "fullName", Str "Ada Lovelace" ]), r |> Result.map (Map.find "o"))

[<Fact>]
let ``two inputs reach the expression through args`` () =
    let m =
        Mapping.create [ "a"; "b" ] [
            Rule.Compute
                { Fn = "jsonata"
                  Args = [ Arg.Literal(Str "$args[0].x + $args[1].y"); Arg.Input("a", sel ""); Arg.Input("b", sel "") ]
                  Dest = sel "/sum" } ]
        |> Jsonata.register
    let f = { Inputs = Map.ofList [ "a", "a"; "b", "b" ]; InputLists = Map.empty; Output = "o" }
    let docs = Map.ofList [ "a", Obj [ "x", Int 2L ]; "b", Obj [ "y", Int 3L ] ]
    printfn "two inputs: %A" (Fergi.run m f (Loader.ofNodes docs))
    Assert.True(Result.isOk (Fergi.run m f (Loader.ofNodes docs)))

[<Fact>]
let ``yaml in, jsonata compute, yaml out`` () =
    let yaml =
        "name: ada\nfirst: Ada\nlast: ' Lovelace '\njoined: 2026-01-02T03:04:05Z\nbig: 9007199254740993\n"
    let docs = Map.ofList [ "p.yaml", yaml ]
    let load name =
        match Map.tryFind name docs with
        | Some text -> Fnode.Yaml.format.Parse text
        | None -> Error [ Error.create ErrorCode.pathNotFound name [] ]
    let m =
        Mapping.create [ "person" ] [
            Rule.Layer [ "person" ]
            Rule.Compute
                { Fn = "jsonata"
                  Args = [ Arg.Literal(Str "first & ' ' & $trim(last)"); Arg.Input("person", sel "") ]
                  Dest = sel "/fullName" } ]
        |> Jsonata.register
    let f = { Inputs = Map.ofList [ "person", "p.yaml" ]; InputLists = Map.empty; Output = "out.yaml" }
    match Fergi.run m f load with
    | Error es -> failwithf "%A" es
    | Ok out ->
        let text = Fnode.Yaml.format.Print(Map.find "out.yaml" out)
        printfn "%s" text
        Assert.Contains("fullName: Ada Lovelace", text)
        Assert.Contains("2026-01-02T03:04:05Z", text)
        Assert.Contains("9007199254740993", text)
