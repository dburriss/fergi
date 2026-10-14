module PropertyTests

open FsCheck
open FsCheck.Xunit
open Fergi.Json
open Fergi.Json.JsonExtensions

let private genChar : Gen<string> =
    Gen.frequency [
        6, Gen.elements [ 'a' .. 'z' ] |> Gen.map string
        2, Gen.elements [ '"'; '\\'; '/'; '\n'; '\r'; '\t'; '\b'; '\012'; '\000'; '\031'; ' '; '\127' ] |> Gen.map string
        2, Gen.elements [ "\u00e9"; "\u2028"; "\u4e2d"; "\uFFFD"; "\U0001F600"; "\U0001D11E"; "\uD7FF"; "\uE000" ]
    ]

let private genString : Gen<string> =
    Gen.listOf genChar |> Gen.map (String.concat "")

let private genFloat : Gen<float> =
    Gen.oneof [
        Arb.generate<float> |> Gen.filter (fun f -> not (System.Double.IsNaN f || System.Double.IsInfinity f))
        Gen.elements [ 1.0; -0.0; 0.1; 1e20; 1e-7; 5e-324; 1.7976931348623157e308; 123456.789 ]
    ]

let rec private genValue (size: int) : Gen<JsonValue> =
    let scalar =
        Gen.oneof [
            Gen.constant JsonValue.Null
            Gen.elements [ true; false ] |> Gen.map JsonValue.Boolean
            Arb.generate<int64> |> Gen.map JsonValue.Integer
            Gen.elements [ System.Int64.MinValue; System.Int64.MaxValue; 0L; -1L ] |> Gen.map JsonValue.Integer
            genFloat |> Gen.map JsonValue.Float
            genString |> Gen.map JsonValue.String
        ]
    if size <= 0 then
        scalar
    else
        let child = genValue (size / 3)
        Gen.frequency [
            3, scalar
            1, Gen.listOfLength 0 child |> Gen.map (List.toArray >> JsonValue.Array)
            2, Gen.resize 4 (Gen.listOf child) |> Gen.map (List.toArray >> JsonValue.Array)
            2,
            Gen.resize 4 (Gen.listOf (Gen.zip genString child))
            |> Gen.map (List.toArray >> JsonValue.Record)
        ]

type JsonArb =
    static member Value() : Arbitrary<JsonValue> = Arb.fromGen (Gen.sized genValue)

let rec private pointers (prefix: string) (v: JsonValue) : string list =
    let esc (k: string) = k.Replace("~", "~0").Replace("/", "~1")
    prefix
    :: (match v with
        | JsonValue.Array items -> items |> Array.toList |> List.mapi (fun i c -> pointers (prefix + "/" + string i) c) |> List.concat
        | JsonValue.Record props -> props |> Array.toList |> List.collect (fun (k, c) -> pointers (prefix + "/" + esc k) c)
        | _ -> [])

[<Property(Arbitrary = [| typeof<JsonArb> |], MaxTest = 500)>]
let ``parse (print v) = v, compact and pretty`` (v: JsonValue) =
    JsonValue.Parse(v.ToString()) = v && JsonValue.Parse(v.ToString(true)) = v

[<Property(Arbitrary = [| typeof<JsonArb> |], MaxTest = 500)>]
let ``print is stable after one round`` (v: JsonValue) =
    let t = v.ToString(true)
    JsonValue.Parse(t).ToString(true) = t

[<Property(Arbitrary = [| typeof<JsonArb> |], MaxTest = 300)>]
let ``every span slices back to its node`` (v: JsonValue) =
    for indent in [ false; true ] do
        let text = v.ToString(indent)
        let parsed, spans = JsonValue.ParseWithPositions text
        if parsed <> v then failwith "ParseWithPositions disagrees with Parse"
        // with duplicate keys only the first occurrence is addressable
        let ptrs = pointers "" v |> List.distinct |> List.filter (fun p -> (v.TryGetPointer p).IsSome)
        if spans.Count <> ptrs.Length then failwith "span count differs from addressable node count"
        for p in ptrs do
            match spans.TryFind p, v.TryGetPointer p with
            | Some sp, Some node ->
                let slice = text.Substring(sp.Start.Offset, sp.End.Offset - sp.Start.Offset)
                if JsonValue.Parse slice <> node then failwith ("span slice mismatch at '" + p + "'")
                // line/column agree with offset
                let before = text.Substring(0, sp.Start.Offset)
                let line = 1 + (before |> Seq.filter ((=) '\n') |> Seq.length)
                let col = sp.Start.Offset - (before.LastIndexOf '\n' + 1) + 1
                if sp.Start.Line <> line || sp.Start.Column <> col then failwith ("line/col mismatch at '" + p + "'")
            | _ -> failwith ("missing span or node for '" + p + "'")
    true
