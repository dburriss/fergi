/// Immutable edits for `JsonValue` (each member returns a new value), mirroring
/// `YamlValueBuilders`. Visible once opened: `open Fergi.Json.JsonBuilders`.
module Fergi.Json.JsonBuilders

type JsonValue with

    /// Replaces the first property called `name` (keeping its position) or appends a new one.
    /// Fails if the value is not a record.
    member this.SetProperty(name: string, value: JsonValue) : JsonValue =
        match this with
        | JsonValue.Record props ->
            if props |> Array.exists (fun (k, _) -> k = name) then
                let mutable replaced = false
                props
                |> Array.map (fun (k, v) ->
                    if not replaced && k = name then
                        replaced <- true
                        (k, value)
                    else
                        (k, v))
                |> JsonValue.Record
            else
                JsonValue.Record(Array.append props [| (name, value) |])
        | _ -> failwith ("JsonValue.SetProperty: not a record: " + this.ToString())

    /// Removes every property called `name`. Fails if the value is not a record.
    member this.RemoveProperty(name: string) : JsonValue =
        match this with
        | JsonValue.Record props -> JsonValue.Record(props |> Array.filter (fun (k, _) -> k <> name))
        | _ -> failwith ("JsonValue.RemoveProperty: not a record: " + this.ToString())

    /// Replaces the element at `index`. Fails if not an array or out of range.
    member this.SetItem(index: int, value: JsonValue) : JsonValue =
        match this with
        | JsonValue.Array items when index >= 0 && index < items.Length ->
            JsonValue.Array(items |> Array.mapi (fun i v -> if i = index then value else v))
        | _ -> failwith ("JsonValue.SetItem: not an array or index out of range: " + this.ToString())

    /// Removes the element at `index`. Fails if not an array or out of range.
    member this.RemoveItem(index: int) : JsonValue =
        match this with
        | JsonValue.Array items when index >= 0 && index < items.Length ->
            JsonValue.Array(items |> Array.indexed |> Array.filter (fun (i, _) -> i <> index) |> Array.map snd)
        | _ -> failwith ("JsonValue.RemoveItem: not an array or index out of range: " + this.ToString())

    /// Appends an element. Fails if not an array.
    member this.Append(value: JsonValue) : JsonValue =
        match this with
        | JsonValue.Array items -> JsonValue.Array(Array.append items [| value |])
        | _ -> failwith ("JsonValue.Append: not an array: " + this.ToString())
