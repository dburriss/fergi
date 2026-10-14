/// Accessors for `JsonValue` in the "I know what this document looks like" style, mirroring
/// `FSharp.Data.JsonExtensions` / `YamlExtensions`. Each accessor raises a clear exception when
/// the value is not of the expected shape. Visible once this module is opened:
/// `open Fergi.Json.JsonExtensions`.
module Fergi.Json.JsonExtensions

open System

let private fail (what: string) (v: JsonValue) : 'a =
    failwith ("JsonValue." + what + ": " + v.ToString())

type JsonValue with

    /// The boolean value. Fails for anything but `Boolean`.
    member this.AsBoolean() : bool =
        match this with
        | JsonValue.Boolean b -> b
        | _ -> fail "AsBoolean: not a boolean" this

    /// The value as a 64-bit integer. `Integer`, or a `Float` that is a whole number in range.
    member this.AsInteger64() : int64 =
        match this with
        | JsonValue.Integer n -> n
        | JsonValue.Float f when Math.Floor f = f && f >= -9.2233720368547758e18 && f < 9.2233720368547758e18 ->
            int64 f
        | _ -> fail "AsInteger64: not an integer" this

    /// The value as an `int`; fails if it does not fit.
    member this.AsInteger() : int =
        let n = this.AsInteger64()
        if n < int64 Int32.MinValue || n > int64 Int32.MaxValue then
            fail "AsInteger: out of range" this
        else
            int n

    /// The value as a float (`Float`, or `Integer` widened).
    member this.AsFloat() : float =
        match this with
        | JsonValue.Float f -> f
        | JsonValue.Integer n -> float n
        | _ -> fail "AsFloat: not a number" this

    /// The value as a decimal (`Float` or `Integer`).
    member this.AsDecimal() : decimal =
        match this with
        | JsonValue.Integer n -> decimal n
        | JsonValue.Float f -> decimal f
        | _ -> fail "AsDecimal: not a number" this

    /// The string value. Fails for anything but `String`.
    member this.AsString() : string =
        match this with
        | JsonValue.String s -> s
        | _ -> fail "AsString: not a string" this

    /// The elements of an array; empty if the value is not an array.
    member this.AsArray() : JsonValue[] =
        match this with
        | JsonValue.Array items -> items
        | _ -> [||]

    /// The properties of a record, in order (duplicates kept); empty if not a record.
    member this.Properties() : (string * JsonValue)[] =
        match this with
        | JsonValue.Record props -> props
        | _ -> [||]

    /// The first property with the given name, or `None` (also `None` for non-records).
    member this.TryGetProperty(name: string) : JsonValue option =
        match this with
        | JsonValue.Record props -> props |> Array.tryPick (fun (k, v) -> if k = name then Some v else None)
        | _ -> None

    /// The first property with the given name; fails if absent or not a record.
    member this.GetProperty(name: string) : JsonValue =
        match this.TryGetProperty name with
        | Some v -> v
        | None -> failwith ("JsonValue.GetProperty: property '" + name + "' not found")

    /// The element at `index`, or `None` if out of range or not an array.
    member this.TryGetItem(index: int) : JsonValue option =
        match this with
        | JsonValue.Array items when index >= 0 && index < items.Length -> Some items.[index]
        | _ -> None

    /// Resolves an RFC 6901 JSON Pointer (`""` is the value itself). `None` if it does not resolve.
    member this.TryGetPointer(pointer: string) : JsonValue option =
        if pointer = "" then
            Some this
        elif pointer.[0] <> '/' then
            None
        else
            let segments = pointer.Substring(1).Split('/')
            let mutable cur = Some this
            for raw in segments do
                match cur with
                | None -> ()
                | Some node ->
                    let seg = raw.Replace("~1", "/").Replace("~0", "~")
                    match node with
                    | JsonValue.Record _ -> cur <- node.TryGetProperty seg
                    | JsonValue.Array items ->
                        let isIndex =
                            seg.Length > 0
                            && seg.Length < 10
                            && (seg = "0" || seg.[0] <> '0')
                            && seg |> Seq.forall (fun c -> c >= '0' && c <= '9')
                        cur <- if isIndex then node.TryGetItem(int seg) else None
                    | _ -> cur <- None
            cur

    /// Indexer for records: `v.["name"]`.
    member this.Item
        with get (name: string) : JsonValue = this.GetProperty name

    /// Indexer for arrays: `v.[0]`.
    member this.Item
        with get (index: int) : JsonValue =
            match this.TryGetItem index with
            | Some v -> v
            | None -> fail "Item: index out of range or not an array" this

    /// Concatenated text of all scalars, mirroring `JsonValue.InnerText`.
    member this.InnerText() : string =
        match this with
        | JsonValue.String s -> s
        | JsonValue.Null -> ""
        | JsonValue.Array items -> items |> Array.map (fun v -> v.InnerText()) |> String.concat ""
        | JsonValue.Record props -> props |> Array.map (fun (_, v) -> v.InnerText()) |> String.concat ""
        | _ -> this.ToString()

/// Dynamic property access: `doc?user?name`. Equivalent to `GetProperty`.
let (?) (json: JsonValue) (name: string) : JsonValue = json.GetProperty name
