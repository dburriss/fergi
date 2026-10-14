namespace Fergi

open Fnode

/// One edit over a resolved `Path`.
[<RequireQualifiedAccess>]
type Op =
    /// Replace the node at the path, or add a new key / append at index = length.
    | Set of Path * Node
    /// Remove the node at the path.
    | Remove of Path
    /// Add an item at the end of the array at the path.
    | Append of Path * Node
    /// Move the node at `from` to `dest` (dest is evaluated after `from` was removed).
    | Move of from: Path * dest: Path
    /// Assert the node at the path equals the value (structural equality).
    | Test of Path * Node

module Patch =

    let private applyOne (op: Op) (doc: Node) : Result<Node, Error> =
        match op with
        | Op.Set(p, v) -> Node.set p v doc
        | Op.Remove p -> Node.remove p doc
        | Op.Append(p, v) ->
            match Node.tryGet p doc with
            | Error e -> Error e
            | Ok(Arr items) -> Node.set p (Arr(items @ [ v ])) doc
            | Ok _ ->
                Error(Error.create ErrorCode.typeMismatch ("Append needs an array at '" + Path.print p + "'") p)
        | Op.Move(from, dest) ->
            if from = dest then Node.tryGet from doc |> Result.map (fun _ -> doc)
            else
                Node.tryGet from doc
                |> Result.bind (fun v -> Node.remove from doc |> Result.bind (Node.set dest v))
        | Op.Test(p, expected) ->
            match Node.tryGet p doc with
            | Error e -> Error e
            | Ok actual when Node.equals actual expected -> Ok doc
            | Ok _ ->
                Error(Error.create ErrorCodes.patchTestFailed ("Value at '" + Path.print p + "' differs from the expected value") p)

    /// Apply ops in order. Atomic: either every op succeeds and the new document is returned,
    /// or the input is untouched and every failing op is reported (a failing op is skipped so
    /// later ops are still checked against the state before it). Each error carries the 0-based
    /// op index as context `op`; `Code` and `Path` are the underlying Fnode / Fergi ones.
    let apply (ops: Op list) (doc: Node) : Result<Node, Error list> =
        let cur, errors =
            ops
            |> List.indexed
            |> List.fold
                (fun (cur, errs) (i, op) ->
                    match applyOne op cur with
                    | Ok next -> next, errs
                    | Error e -> cur, Error.withContext "op" (string i) e :: errs)
                (doc, [])
        if List.isEmpty errors then Ok cur else Error(List.rev errors)

    /// Ops that turn `a` into `b`: `Apply (diff a b) a = Ok b`.
    ///
    /// Objects are diffed by key (`Remove` for dropped keys, `Set` for changed or new keys,
    /// recursing into nested objects). Arrays are atomic: a changed array is one `Set` of the
    /// whole array. Key order is part of equality, so when the surviving keys of `a` are not a
    /// prefix of `b`'s key order (reordered, or a new key placed before an old one), or either
    /// object has duplicate keys, the whole object is replaced with one `Set`.
    let diff (a: Node) (b: Node) : Op list =
        let rec go (prefix: Segment list) (a: Node) (b: Node) : Op list =
            let here = List.rev prefix
            if Node.equals a b then []
            else
                match a, b with
                | Obj akv, Obj bkv when not (Node.hasDuplicateKeys akv) && not (Node.hasDuplicateKeys bkv) ->
                    let bKeys = List.map fst bkv
                    let common = akv |> List.map fst |> List.filter (fun k -> List.contains k bKeys)
                    if List.truncate (List.length common) bKeys <> common then [ Op.Set(here, b) ]
                    else
                        let removes =
                            akv
                            |> List.filter (fun (k, _) -> not (List.contains k bKeys))
                            |> List.map (fun (k, _) -> Op.Remove(here @ [ Key k ]))
                        let sets =
                            bkv
                            |> List.collect (fun (k, bv) ->
                                match Node.tryFindKey k akv with
                                | Some av -> go (Key k :: prefix) av bv
                                | None -> [ Op.Set(here @ [ Key k ], bv) ])
                        removes @ sets
                | _ -> [ Op.Set(here, b) ]
        go [] a b
