// Runs every case in cases.json with Jsonata.Net.Native and writes what it observed.
//   dotnet run --project Runner.fsproj -- <cases.json> <observed.json>
open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Jsonata.Net.Native
open Jsonata.Net.Native.Json

let codeOf (e: exn) =
    let m = Regex.Match(e.Message, @"^[A-Z]\d{4}")
    if m.Success then m.Value else e.GetType().Name

[<EntryPoint>]
let main argv =
    let casesFile, outFile = argv.[0], argv.[1]
    let spec = JsonNode.Parse(File.ReadAllText casesFile)
    let results = JsonObject()
    for c in spec.["cases"].AsArray() do
        let id = c.["id"].GetValue<string>()
        let expr = c.["expr"].GetValue<string>()
        // the library has its own DOM: hand it the context as JSON text
        let ctx =
            match c.["ctx"] with
            | null -> JValue.CreateNull()
            | node -> JToken.Parse(node.ToJsonString())
        let result = JsonObject()
        try
            let r = JsonataQuery(expr).Eval(ctx, EvaluationEnvironment())
            if r.Type = JTokenType.Undefined then
                result.["kind"] <- JsonValue.Create "undefined"
            else
                result.["kind"] <- JsonValue.Create "value"
                result.["value"] <- JsonNode.Parse(r.ToFlatString())
        with e ->
            result.["kind"] <- JsonValue.Create "error"
            result.["code"] <- JsonValue.Create(codeOf e)
        results.[id] <- result
    let asm = typeof<JsonataQuery>.Assembly.GetName()
    let doc = JsonObject()
    doc.["runtime"] <- JsonValue.Create "dotnet"
    doc.["library"] <- JsonValue.Create("Jsonata.Net.Native " + asm.Version.ToString(3))
    doc.["results"] <- results
    let opts = JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
    File.WriteAllText(outFile, doc.ToJsonString opts)
    0
