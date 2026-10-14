namespace Fergi

open Fnode

/// Pointer (RFC 6901, as `Path.print`) -> name of the last source that supplied or merged into
/// that node. Every node of a `Doc.layers` result has an entry.
type Provenance = Map<string, string>

/// Merge semantics (see `Doc.merge` / `Doc.layers` in Doc.fs).
module Merge =

    type private St =
        { Prov: Provenance
          Conflicts: Conflict list } // reversed

    let private hasPrefix (prefix: string) (k: string) = k.Length >= prefix.Length && k.Substring(0, prefix.Length) = prefix

    /// Drop the entries of `p` and everything below it.
    let private dropUnder (p: string) (m: Provenance) : Provenance =
        Map.filter (fun k _ -> not (k = p || (hasPrefix p k && k.Length > p.Length && k.[p.Length] = '/'))) m

    /// The overlay node at `path` was taken wholesale: every node of it maps to `src`.
    let private assignWhole (path: Path) (node: Node) (src: string) (st: St) : St =
        let rec walk (p: Path) (n: Node) (m: Provenance) =
            let m = Map.add (Path.print p) src m
            match n with
            | Obj kv -> List.fold (fun m (k, v) -> walk (p @ [ Key k ]) v m) m kv
            | Arr items -> items |> List.indexed |> List.fold (fun m (i, v) -> walk (p @ [ Index i ]) v m) m
            | _ -> m
        { st with Prov = walk path node (dropUnder (Path.print path) st.Prov) }

    let private touch (path: Path) (src: string) (st: St) : St =
        { st with Prov = Map.add (Path.print path) src st.Prov }

    let private conflict path l r reason : Conflict =
        { Path = path; Left = l; Right = r; LeftSource = None; RightSource = None; Reason = reason }

    let private addConflict (c: Conflict) (st: St) = { st with Conflicts = c :: st.Conflicts }

    let private kind (n: Node) =
        match n with
        | Obj _ -> "object"
        | Arr _ -> "array"
        | Null -> "null"
        | Bool _ -> "bool"
        | Int _ -> "int"
        | Float _ -> "float"
        | Str _ -> "string"
        | Timestamp _ -> "timestamp"

    let private strategyFor (pol: Policy) (path: Path) =
        match List.tryFind (fun (p, _) -> p = path) pol.ListRules with
        | Some(_, s) -> s
        | None -> pol.Lists

    /// Two values that cannot be combined structurally: `OnConflict` decides.
    let private decide (pol: Policy) (src: string) (path: Path) (b: Node) (o: Node) (reason: string) (st: St) : Node * St =
        match pol.Conflict with
        | OnConflict.LastWins -> o, assignWhole path o src st
        | OnConflict.FirstWins -> b, st
        | OnConflict.Fail -> b, addConflict (conflict path b o reason) st
        | OnConflict.Resolve f ->
            match f path b o with
            | Ok n -> n, assignWhole path n src st
            | Error c -> b, addConflict c st

    let private keyOf (k: string) (item: Node) : string option =
        match item with
        | Obj kv ->
            match List.tryFind (fun (n, _) -> n = k) kv with
            | Some(_, Str s) -> Some s
            | _ -> None
        | _ -> None

    let rec private go (pol: Policy) (src: string) (path: Path) (b: Node) (o: Node) (st: St) : Node * St =
        match b, o with
        | Obj bkv, Obj okv ->
            let step (acc: (string * Node) list, st: St) (k: string, ov: Node) =
                let sub = path @ [ Key k ]
                if ov = Null && pol.NullDeletes then
                    List.filter (fun (n, _) -> n <> k) acc, { st with Prov = dropUnder (Path.print sub) st.Prov }
                else
                    match List.tryFindIndex (fun (n, _) -> n = k) acc with
                    | Some i ->
                        let merged, st = go pol src sub (snd (List.item i acc)) ov st
                        List.mapi (fun j kv -> if j = i then (k, merged) else kv) acc, st
                    | None -> acc @ [ k, ov ], assignWhole sub ov src st
            let kv, st = List.fold step (bkv, st) okv
            Obj kv, touch path src st
        | Arr bi, Arr oi ->
            match strategyFor pol path with
            | ListStrategy.Replace ->
                if Node.equals b o then b, assignWhole path o src st
                else decide pol src path b o "lists differ (Replace)" st
            | ListStrategy.Append ->
                let st =
                    oi
                    |> List.indexed
                    |> List.fold (fun st (j, item) -> assignWhole (path @ [ Index(List.length bi + j) ]) item src st) st
                Arr(bi @ oi), touch path src st
            | ListStrategy.Union ->
                let items, st =
                    List.fold
                        (fun (acc: Node list, st) item ->
                            if List.exists (Node.equals item) acc then acc, st
                            else acc @ [ item ], assignWhole (path @ [ Index(List.length acc) ]) item src st)
                        (bi, st)
                        oi
                Arr items, touch path src st
            | ListStrategy.ByKey k ->
                let bad (side: string) (items: Node list) =
                    items
                    |> List.indexed
                    |> List.choose (fun (i, item) ->
                        match keyOf k item with
                        | Some _ -> None
                        | None ->
                            let why =
                                match item with
                                | Obj _ -> "has no string field '" + k + "'"
                                | _ -> "is not an object"
                            Some(conflict path b o (side + " item " + string i + " " + why + " (ByKey '" + k + "')")))
                match bad "base" bi @ bad "overlay" oi with
                | [] ->
                    let items, st =
                        List.fold
                            (fun (acc: Node list, st) item ->
                                let key = keyOf k item
                                match List.tryFindIndex (fun a -> keyOf k a = key) acc with
                                | Some i ->
                                    let merged, st = go pol src (path @ [ Index i ]) (List.item i acc) item st
                                    List.mapi (fun j a -> if j = i then merged else a) acc, st
                                | None -> acc @ [ item ], assignWhole (path @ [ Index(List.length acc) ]) item src st)
                            (bi, st)
                            oi
                    Arr items, touch path src st
                | cs -> b, List.fold (fun st c -> addConflict c st) st cs
        | _ ->
            let scalar (n: Node) = match n with Obj _ | Arr _ -> false | _ -> true
            if scalar b && scalar o && Node.equals b o then b, assignWhole path o src st
            else
                let reason =
                    if scalar b && scalar o then "values differ"
                    else "type mismatch: " + kind b + " vs " + kind o
                decide pol src path b o reason st

    /// See `Doc.merge`.
    let merge (pol: Policy) (b: Node) (o: Node) : Result<Node, Conflict list> =
        let n, st = go pol "" [] b o { Prov = Map.empty; Conflicts = [] }
        if List.isEmpty st.Conflicts then Ok n else Error(List.rev st.Conflicts)

    /// See `Doc.layers`.
    let layers (pol: Policy) (ls: (string * Node) list) : Result<Node * Provenance, Conflict list> =
        let n, prov, errs =
            List.fold
                (fun (acc, prov, errs) (name, doc) ->
                    let n, st = go pol name [] acc doc { Prov = prov; Conflicts = [] }
                    let fill (c: Conflict) =
                        { c with
                            LeftSource = (match c.LeftSource with Some _ -> c.LeftSource | None -> Map.tryFind (Path.print c.Path) prov)
                            RightSource = (match c.RightSource with Some _ -> c.RightSource | None -> Some name) }
                    n, st.Prov, errs @ List.map fill (List.rev st.Conflicts))
                (Obj [], Map.empty, [])
                ls
        if List.isEmpty errs then Ok(n, prov) else Error errs
