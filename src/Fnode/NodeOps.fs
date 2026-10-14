namespace Fnode

/// Path-based navigation and edits. Never throw; failures are `Error` values.
/// Error `Path` is the prefix of the requested path up to and including the failing segment.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Node =

    let private fail code (msg: string) (trail: Segment list) : Error =
        Error.create code msg (List.rev trail)

    let private describe (n: Node) =
        match n with
        | Obj _ -> "object"
        | Arr _ -> "array"
        | _ -> "scalar"

    let private mismatch (seg: Segment) (node: Node) trail =
        let want = match seg with Key _ -> "object" | Index _ -> "array"
        fail ErrorCode.typeMismatch
            ("Expected " + want + " for segment '" + Path.printSegment seg + "' but found " + describe node)
            (seg :: trail)

    let private notFound (seg: Segment) trail =
        fail ErrorCode.pathNotFound ("Path not found at segment '" + Path.printSegment seg + "'") (seg :: trail)

    /// First value stored under `key`. Duplicate keys are allowed in `Obj` (adapters may produce
    /// them); every lookup in this module acts on the first match.
    let tryFindKey (key: string) (kvs: (string * Node) list) : Node option =
        List.tryFind (fun (k, _) -> k = key) kvs |> Option.map snd

    /// Replace the first `key` in place, or append it. Order is kept.
    let upsertKey (key: string) (value: Node) (kvs: (string * Node) list) : (string * Node) list =
        if List.exists (fun (k, _) -> k = key) kvs then
            let rec go pairs replaced =
                match pairs with
                | [] -> []
                | (k, _) :: tl when k = key && not replaced -> (k, value) :: go tl true
                | p :: tl -> p :: go tl replaced
            go kvs false
        else kvs @ [ (key, value) ]

    /// Remove every `key`. Order of the rest is kept.
    let removeKey (key: string) (kvs: (string * Node) list) : (string * Node) list =
        List.filter (fun (k, _) -> k <> key) kvs

    let hasDuplicateKeys (kvs: (string * Node) list) : bool =
        let rec go (seen: Set<string>) pairs =
            match pairs with
            | [] -> false
            | (k, _) :: tl -> Set.contains k seen || go (Set.add k seen) tl
        go Set.empty kvs

    /// Structural equality that is safe for NaN: two `Float` NaN leaves are equal. Otherwise the
    /// same as `=` (no Int/Float coercion, object key order significant, `-0.0 = 0.0`).
    /// Plain `=` on `Node` is not NaN-safe; use this for every document comparison.
    let rec equals (a: Node) (b: Node) : bool =
        match a, b with
        | Float x, Float y -> x = y || (System.Double.IsNaN x && System.Double.IsNaN y)
        | Arr xs, Arr ys -> List.length xs = List.length ys && List.forall2 equals xs ys
        | Obj xs, Obj ys ->
            List.length xs = List.length ys
            && List.forall2 (fun (k1, v1) (k2, v2) -> k1 = k2 && equals v1 v2) xs ys
        | _ -> a = b

    /// Fetch the node at `path`.
    let tryGet (path: Path) (node: Node) : Result<Node, Error> =
        let rec go (trail: Segment list) path node =
            match path with
            | [] -> Ok node
            | seg :: rest ->
                match seg, node with
                | Key k, Obj kvs ->
                    match List.tryFind (fun (k2, _) -> k2 = k) kvs with
                    | Some(_, child) -> go (seg :: trail) rest child
                    | None -> Error(notFound seg trail)
                | Index i, Arr items ->
                    if i >= 0 && i < List.length items then go (seg :: trail) rest (List.item i items)
                    else Error(notFound seg trail)
                | _ -> Error(mismatch seg node trail)
        go [] path node

    /// Like `tryGet`, but absence is a value: `Ok None` when the path does not exist,
    /// `Error` only for a type mismatch.
    let tryFind (path: Path) (node: Node) : Result<Node option, Error> =
        match tryGet path node with
        | Ok n -> Ok(Some n)
        | Error e when e.Code = ErrorCode.pathNotFound -> Ok None
        | Error e -> Error e

    /// True when `path` resolves to a node. A type mismatch counts as not existing.
    let exists (path: Path) (node: Node) : bool =
        match tryGet path node with
        | Ok _ -> true
        | Error _ -> false

    /// Set the node at `path`. Existing key/index: replaced in place. New key: appended.
    /// Index equal to the array length: appended. Missing intermediate nodes are `path.notFound`.
    /// The empty path replaces the whole document.
    let private setWith (create: bool) (path: Path) (value: Node) (node: Node) : Result<Node, Error> =
        let rec go (trail: Segment list) path node =
            match path with
            | [] -> Ok value
            | seg :: rest ->
                let last = List.isEmpty rest
                match seg, node with
                | Key k, Obj kvs ->
                    if List.exists (fun (k2, _) -> k2 = k) kvs then
                        // replace the first match only
                        let rec rep pairs done' =
                            match pairs with
                            | [] -> Ok []
                            | (k2, child) :: tl when k2 = k && not done' ->
                                match go (seg :: trail) rest child with
                                | Error e -> Error e
                                | Ok c ->
                                    match rep tl true with
                                    | Error e -> Error e
                                    | Ok tl' -> Ok((k2, c) :: tl')
                            | p :: tl ->
                                match rep tl done' with
                                | Error e -> Error e
                                | Ok tl' -> Ok(p :: tl')
                        rep kvs false |> Result.map Obj
                    elif last then Ok(Obj(kvs @ [ (k, value) ]))
                    elif create && (match rest with Key _ :: _ -> true | _ -> false) then
                        // missing intermediate key: build the whole chain below it
                        match go (seg :: trail) rest (Obj []) with
                        | Error e -> Error e
                        | Ok c -> Ok(Obj(kvs @ [ (k, c) ]))
                    else Error(notFound seg trail)
                | Index i, Arr items ->
                    let len = List.length items
                    if i >= 0 && i < len then
                        match go (seg :: trail) rest (List.item i items) with
                        | Error e -> Error e
                        | Ok c -> Ok(Arr(List.mapi (fun j x -> if j = i then c else x) items))
                    elif i = len && last then Ok(Arr(items @ [ value ]))
                    else Error(notFound seg trail)
                | _ -> Error(mismatch seg node trail)
        go [] path node

    let set (path: Path) (value: Node) (node: Node) : Result<Node, Error> = setWith false path value node

    /// Like `set`, but a missing `Key` intermediate becomes `Obj []` (mkdir -p). A missing array
    /// index parent is still `path.notFound`, and type mismatches still fail.
    let setCreating (path: Path) (value: Node) (node: Node) : Result<Node, Error> = setWith true path value node

    /// Remove the node at `path`. Removing from an array shifts later items down.
    /// Removing the root is `path.invalid`.
    let remove (path: Path) (node: Node) : Result<Node, Error> =
        let rec go (trail: Segment list) path node =
            match path with
            | [] -> Error(fail ErrorCode.pathInvalid "Cannot remove the root" trail)
            | seg :: rest ->
                let last = List.isEmpty rest
                match seg, node with
                | Key k, Obj kvs ->
                    match List.tryFindIndex (fun (k2, _) -> k2 = k) kvs with
                    | None -> Error(notFound seg trail)
                    | Some idx ->
                        if last then Ok(Obj(List.indexed kvs |> List.filter (fun (j, _) -> j <> idx) |> List.map snd))
                        else
                            match go (seg :: trail) rest (snd (List.item idx kvs)) with
                            | Error e -> Error e
                            | Ok c -> Ok(Obj(List.mapi (fun j (k2, x) -> if j = idx then (k2, c) else (k2, x)) kvs))
                | Index i, Arr items ->
                    if i < 0 || i >= List.length items then Error(notFound seg trail)
                    elif last then Ok(Arr(List.indexed items |> List.filter (fun (j, _) -> j <> i) |> List.map snd))
                    else
                        match go (seg :: trail) rest (List.item i items) with
                        | Error e -> Error e
                        | Ok c -> Ok(Arr(List.mapi (fun j x -> if j = i then c else x) items))
                | _ -> Error(mismatch seg node trail)
        go [] path node
