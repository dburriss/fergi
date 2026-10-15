module MappingFileTests

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

let private mustOk (r: Result<'a, Error list>) : 'a =
    match r with
    | Ok x -> x
    | Error es -> failwithf "unexpected errors: %A" (es |> List.map Error.describe)

let private errorsOf (r: Result<'a, Error list>) : Error list =
    match r with
    | Ok _ -> failwith "expected errors"
    | Error es -> es

/// Every rule kind once, so a round trip covers the whole file form.
let private full : Mapping =
    Mapping.create
        [ "person"; "org" ]
        [ Rule.Layer [ "org"; "person" ]
          Rule.ListsBy(ptr "/ports", "name")
          Rule.Conflict ConflictMode.FirstWins
          Rule.NullDeletes false
          Rule.Copy(("person", sel "/name"), sel "/metadata/name")
          Rule.Rename(sel "/a", sel "/b")
          Rule.DefaultTo(sel "/kind", Str "service")
          Rule.Set(sel "/ports/[name=http]/port", Int 80L)
          Rule.Drop(sel "/internal")
          Rule.Compute
              { Fn = "join"
                Args = [ Arg.Literal(Str " "); Arg.Input("person", sel "/first"); Arg.Doc(sel "/last"); Arg.Literal Null ]
                Dest = sel "/fullName" }
          Rule.Collect
              { Input = "services"
                Into = sel "/services"
                Entry = [ Rule.Copy(("services", sel "/name"), sel "/name"); Rule.Set(sel "/kind", Obj [ "x", Arr [ Bool true; Float 1.5 ] ]) ]
                SortBy = Some(sel "/name") } ]
    |> Mapping.withInputLists [ "services" ]

[<Fact>]
let ``toNode then ofNode gives the same inputs and rules`` () =
    let back = full |> Mapping.toNode |> mustOk |> Mapping.ofNode |> mustOk
    Assert.Equal<string list>(full.Inputs, back.Inputs)
    Assert.Equal<string list>(full.InputLists, back.InputLists)
    Assert.Equal<Rule list>(full.Rules, back.Rules)

[<Fact>]
let ``the file form is versioned and a rule is an object with one key`` () =
    let node = full |> Mapping.toNode |> mustOk
    match node with
    | Obj(("version", Int 1L) :: ("inputs", _) :: ("inputLists", _) :: ("rules", Arr(Obj [ "layer", Arr [ Str "org"; Str "person" ] ] :: _)) :: []) -> ()
    | other -> failwithf "unexpected shape: %A" other

[<Fact>]
let ``inputLists is left out when empty`` () =
    match Mapping.create [ "a" ] [] |> Mapping.toNode |> mustOk with
    | Obj [ "version", Int 1L; "inputs", Arr [ Str "a" ]; "rules", Arr [] ] -> ()
    | other -> failwithf "unexpected shape: %A" other

[<Fact>]
let ``survives JSON text`` () =
    let text = Fnode.Json.format.Print(full |> Mapping.toNode |> mustOk)
    let back = text |> Fnode.Json.format.Parse |> mustOk |> Mapping.ofParsed |> mustOk
    Assert.Equal<Rule list>(full.Rules, back.Rules)

[<Fact>]
let ``survives YAML text`` () =
    let text = Fnode.Yaml.format.Print(full |> Mapping.toNode |> mustOk)
    let back = text |> Fnode.Yaml.format.Parse |> mustOk |> Mapping.ofParsed |> mustOk
    Assert.Equal<Rule list>(full.Rules, back.Rules)

[<Fact>]
let ``a mapping read from a file runs like the original`` () =
    let m =
        Mapping.create
            [ "person" ]
            [ Rule.Copy(("person", sel "/first"), sel "/first")
              Rule.Compute { Fn = "upper"; Args = [ Arg.Input("person", sel "/last") ]; Dest = sel "/LAST" } ]
    let files = { Files.Inputs = Map.ofList [ "person", "p.json" ]; InputLists = Map.empty; Output = "out.json" }
    let docs = Map.ofList [ "p.json", Obj [ "first", Str "Ada"; "last", Str "Lovelace" ] ]
    let run (mapping: Mapping) = Fergi.run mapping files (Loader.ofNodes docs) |> mustOk
    let fromFile =
        Fnode.Json.format.Print(m |> Mapping.toNode |> mustOk)
        |> Fnode.Json.format.Parse
        |> mustOk
        |> Mapping.ofParsed
        |> mustOk
    Assert.True(run m = run fromFile)

// ---- writing: what a file cannot say ----

[<Fact>]
let ``an Ensure hook is reported by name`` () =
    let m = Mapping.create [ "a" ] [] |> Mapping.ensure "has-name" (fun _ -> Ok()) |> Mapping.ensure "other" (fun _ -> Ok())
    let es = m |> Mapping.toNode |> errorsOf
    Assert.Equal(2, es.Length)
    Assert.All(es, fun e -> Assert.Equal(ErrorCodes.mappingNotSerialisable, e.Code))
    Assert.Equal<(string * string) list list>([ [ "ensure", "has-name" ]; [ "ensure", "other" ] ], es |> List.map (fun e -> e.Context))
    Assert.Contains("declarative check rule", es.Head.Message)

[<Fact>]
let ``a host function is reported with its name and the path of the rule`` () =
    let host : Function = fun _ -> Ok(Str "x")
    let m =
        Mapping.create
            [ "a" ]
            [ Rule.Set(sel "/k", Int 1L)
              Rule.Compute { Fn = "mine"; Args = []; Dest = sel "/x" }
              Rule.Collect
                  { Input = "l"
                    Into = sel "/l"
                    Entry = [ Rule.Compute { Fn = "mine"; Args = []; Dest = sel "/y" } ]
                    SortBy = None } ]
        |> Mapping.withInputLists [ "l" ]
        |> Mapping.withFunction "mine" host
    let es = m |> Mapping.toNode |> errorsOf
    Assert.Equal(2, es.Length)
    Assert.Equal<string list>(
        [ "/rules/1/compute/fn"; "/rules/2/collect/entry/0/compute/fn" ],
        es |> List.map (fun e -> Path.print e.Path)
    )
    Assert.All(es, fun e ->
        Assert.Equal(ErrorCodes.mappingNotSerialisable, e.Code)
        Assert.Equal<(string * string) list>([ "function", "mine" ], e.Context)
        Assert.Contains("mine", e.Message)
        Assert.Contains("registered", e.Message))

[<Fact>]
let ``replacing a built-in with a host function of the same name is also reported`` () =
    let m =
        Mapping.create [ "a" ] [ Rule.Compute { Fn = "upper"; Args = []; Dest = sel "/x" } ]
        |> Mapping.withFunction "upper" (fun _ -> Ok Null)
    Assert.Equal(1, (m |> Mapping.toNode |> errorsOf).Length)

[<Fact>]
let ``built-ins, jsonata and an unknown name are written by name`` () =
    let m =
        Mapping.create
            [ "a" ]
            [ Rule.Compute { Fn = "upper"; Args = []; Dest = sel "/x" }
              Rule.Compute { Fn = "jsonata"; Args = []; Dest = sel "/y" }
              Rule.Compute { Fn = "notRegistered"; Args = []; Dest = sel "/z" } ]
        |> Mapping.withFunction "jsonata" (fun _ -> Ok Null)
    Assert.True(Result.isOk (Mapping.toNode m))

[<Fact>]
let ``hooks, host functions and bad selectors are all reported together`` () =
    let m =
        Mapping.create
            [ "a" ]
            [ Rule.Compute { Fn = "mine"; Args = []; Dest = sel "/x" }
              Rule.Set([ Where("a=b", "c") ], Int 1L) ]
        |> Mapping.withFunction "mine" (fun _ -> Ok Null)
        |> Mapping.ensure "h" (fun _ -> Ok())
    Assert.Equal(3, (m |> Mapping.toNode |> errorsOf).Length)

[<Fact>]
let ``a numeric object key does not read back as the same pointer`` () =
    let m = Mapping.create [ "a" ] [ Rule.ListsBy([ Key "0" ], "name") ]
    let es = m |> Mapping.toNode |> errorsOf
    Assert.Equal(ErrorCodes.mappingNotSerialisable, es.Head.Code)
    Assert.Equal("/rules/0/listsBy/path", Path.print es.Head.Path)

// ---- reading: errors point into the file ----

let private json (s: string) = s |> Fnode.Json.format.Parse |> mustOk

let private codeAndPath (e: Error) = e.Code, Path.print e.Path

[<Fact>]
let ``a missing version is reported`` () =
    let es = Obj [ "inputs", Arr []; "rules", Arr [] ] |> Mapping.ofNode |> errorsOf
    Assert.Equal<(string * string) list>([ ErrorCodes.mappingInvalid, "" ], es |> List.map codeAndPath)
    Assert.Contains("version", es.Head.Message)

[<Fact>]
let ``another version has its own code`` () =
    let es = Obj [ "version", Int 2L; "inputs", Arr []; "rules", Arr [] ] |> Mapping.ofNode |> errorsOf
    Assert.Equal<(string * string) list>([ ErrorCodes.mappingVersion, "/version" ], es |> List.map codeAndPath)

[<Fact>]
let ``an unknown top-level field and an unknown rule are reported`` () =
    let node =
        Obj [ "version", Int 1L; "inputs", Arr []; "extra", Null; "rules", Arr [ Obj [ "frobnicate", Obj [] ] ] ]
    let es = node |> Mapping.ofNode |> errorsOf
    Assert.Equal<(string * string) list>([ ErrorCodes.mappingInvalid, "/extra" ], es |> List.map codeAndPath)
    let es2 = Obj [ "version", Int 1L; "inputs", Arr []; "rules", Arr [ Obj [ "frobnicate", Obj [] ] ] ] |> Mapping.ofNode |> errorsOf
    Assert.Equal<(string * string) list>([ ErrorCodes.mappingInvalid, "/rules/0/frobnicate" ], es2 |> List.map codeAndPath)
    Assert.Contains("copy", es2.Head.Message)

[<Fact>]
let ``a schema key is accepted and ignored`` () =
    let node = Obj [ "$schema", Str "mapping.schema.json"; "version", Int 1L; "inputs", Arr []; "rules", Arr [] ]
    Assert.True(Result.isOk (Mapping.ofNode node))

[<Fact>]
let ``problems in several rules are all reported with their paths`` () =
    let node =
        Obj [ "version", Int 1L
              "inputs", Arr [ Str "a" ]
              "rules",
              Arr [ Obj [ "set", Obj [ "to", Str "no-slash"; "value", Int 1L ] ]
                    Obj [ "copy", Obj [ "from", Str "/x"; "to", Str "/y" ] ]
                    Obj [ "compute", Obj [ "to", Str "/z"; "fn", Str "upper"; "args", Arr [ Obj [ "from", Str "a:/x"; "doc", Str "/y" ] ] ] ]
                    Obj [ "a", Null; "b", Null ]
                    Obj [ "drop", Obj [ "path", Str "/ok"; "bonus", Int 1L ] ] ] ]
    let es = node |> Mapping.ofNode |> errorsOf
    Assert.Equal<(string * string) list>(
        [ ErrorCode.pathInvalid, "/rules/0/set/to"
          ErrorCode.pathInvalid, "/rules/1/copy/from"
          ErrorCodes.mappingInvalid, "/rules/2/compute/args/0"
          ErrorCodes.mappingInvalid, "/rules/3"
          ErrorCodes.mappingInvalid, "/rules/4/drop/bonus" ],
        es |> List.map codeAndPath
    )

[<Fact>]
let ``wrong types say what they found`` () =
    let es =
        Obj [ "version", Int 1L
              "inputs", Str "a"
              "rules", Arr [ Obj [ "conflict", Str "maybe" ]; Obj [ "nullDeletes", Str "yes" ]; Str "x" ] ]
        |> Mapping.ofNode
        |> errorsOf
    Assert.Equal<(string * string) list>(
        [ ErrorCodes.mappingInvalid, "/inputs"
          ErrorCodes.mappingInvalid, "/rules/0/conflict"
          ErrorCodes.mappingInvalid, "/rules/1/nullDeletes"
          ErrorCodes.mappingInvalid, "/rules/2" ],
        es |> List.map codeAndPath
    )
    Assert.Contains("a string", es.Head.Message)

[<Fact>]
let ``errors from a parsed file carry the line and column`` () =
    let text =
        "{\n  \"version\": 1,\n  \"inputs\": [\"a\"],\n  \"rules\": [\n    { \"set\": { \"to\": \"oops\", \"value\": 1 } }\n  ]\n}"
    let es = text |> json |> Mapping.ofParsed |> errorsOf
    Assert.Equal(1, es.Length)
    Assert.Equal("/rules/0/set/to", Path.print es.Head.Path)
    Assert.Equal(Some 5, es.Head.Location |> Option.map (fun s -> s.Start.Line))

[<Fact>]
let ``an unknown key falls back to the location of its object`` () =
    let text = "{\n  \"version\": 1,\n  \"inputs\": [],\n  \"rules\": [],\n  \"bogus\": 1\n}"
    let es = text |> json |> Mapping.ofParsed |> errorsOf
    Assert.Equal("/bogus", Path.print es.Head.Path)
    Assert.True(es.Head.Location.IsSome)

[<Fact>]
let ``a file naming an unknown function reads, and run reports it`` () =
    let node =
        Obj [ "version", Int 1L
              "inputs", Arr [ Str "a" ]
              "rules", Arr [ Obj [ "compute", Obj [ "to", Str "/x"; "fn", Str "nope" ] ] ] ]
    let m = node |> Mapping.ofNode |> mustOk
    let files = { Files.Inputs = Map.ofList [ "a", "a.json" ]; InputLists = Map.empty; Output = "o.json" }
    let es = Fergi.run m files (Loader.ofNodes (Map.ofList [ "a.json", Obj [] ])) |> errorsOf
    Assert.Equal(ErrorCodes.mappingFunctionUnknown, es.Head.Code)
