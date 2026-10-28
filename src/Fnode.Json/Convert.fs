namespace Fnode

open Flexi.Json

/// Conversion between `JsonValue` and `Node`.
module JsonConvert =

    /// JsonValue to Node. Strings stay `Str` (JSON has no timestamps).
    let rec toNode (v: JsonValue) : Node =
        match v with
        | JsonValue.Null -> Node.Null
        | JsonValue.Boolean b -> Node.Bool b
        | JsonValue.Integer i -> Node.Int i
        | JsonValue.Float f -> Node.Float f
        | JsonValue.String s -> Node.Str s
        | JsonValue.Array items -> Node.Arr(items |> Array.map toNode |> List.ofArray)
        | JsonValue.Record pairs -> Node.Obj(pairs |> Array.map (fun (k, x) -> k, toNode x) |> List.ofArray)

    /// Node to JsonValue. `Timestamp` becomes a JSON string.
    let rec ofNode (n: Node) : JsonValue =
        match n with
        | Node.Null -> JsonValue.Null
        | Node.Bool b -> JsonValue.Boolean b
        | Node.Int i -> JsonValue.Integer i
        | Node.Float f -> JsonValue.Float f
        | Node.Str s -> JsonValue.String s
        | Node.Timestamp s -> JsonValue.String s
        | Node.Arr items -> JsonValue.Array(items |> List.map ofNode |> Array.ofList)
        | Node.Obj pairs -> JsonValue.Record(pairs |> List.map (fun (k, x) -> k, ofNode x) |> Array.ofList)
