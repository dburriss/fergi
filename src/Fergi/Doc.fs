namespace Fergi

open Fnode

/// Level 1 document access: Fnode's `Node.tryGet/set/remove` addressed by selectors.
///
/// `get` returns `Result` (Fnode's shape) rather than `option`, so a caller can tell a missing
/// path from a type mismatch or an ambiguous selector. Nothing throws.
module Doc =

    /// Fetch the node at `path`.
    let get (path: SelectorPath) (doc: Node) : Result<Node, Error> =
        Selector.resolve path doc |> Result.bind (fun p -> Node.tryGet p doc)

    /// Set the node at `path` (Fnode semantics: replace in place, or append a new key).
    let set (path: SelectorPath) (value: Node) (doc: Node) : Result<Node, Error> =
        Selector.resolve path doc |> Result.bind (fun p -> Node.set p value doc)

    /// Remove the node at `path`.
    let remove (path: SelectorPath) (doc: Node) : Result<Node, Error> =
        Selector.resolve path doc |> Result.bind (fun p -> Node.remove p doc)

    /// Merge `overlay` onto `base`. Semantics (every point is tested):
    ///
    /// - Objects merge key by key: base key order is kept, new keys are appended in overlay order.
    /// - Identical scalars never conflict. Scalars compare structurally with no Int/Float
    ///   coercion; `Timestamp` compares by text; `Str "x"` and `Timestamp "x"` differ; NaN equals NaN (`Node.equals`).
    /// - Differing scalars, or a type mismatch (object vs array vs scalar), are decided by
    ///   `OnConflict`: `LastWins` takes the overlay, `FirstWins` keeps the base (neither reports a
    ///   conflict), `Fail` reports a `Conflict` and keeps the base in the partial result,
    ///   `Resolve f` calls `f path base overlay`. A type mismatch is not treated specially.
    /// - `NullDeletes = true`: an overlay `Null` under an object key removes the key (no-op if
    ///   absent; this ignores `OnConflict`). A `Null` that is not under an object key (root, list
    ///   item) and any `Null` inside a newly added subtree is an ordinary value. `false`: `Null`
    ///   is always an ordinary value.
    /// - Lists use the strategy of the exact-`Path` match in `ListRules`, else `Lists`.
    ///   `Replace`: one atomic value (equal: no conflict; differing: `OnConflict` decides).
    ///   `Append`: base then overlay. `Union`: overlay items not already present, first-seen
    ///   order, base untouched. `ByKey k`: same-`k` items merge recursively, new items appended;
    ///   an item that is not an object or lacks a string field `k` (either side) is a
    ///   `Conflict` at the list's path (regardless of `OnConflict`; `Resolve` is not consulted).
    /// - Conflicts accumulate over the whole walk. `Ok` only when there are none.
    let merge (policy: Policy) (baseDoc: Node) (overlay: Node) : Result<Node, Conflict list> =
        Merge.merge policy baseDoc overlay

    /// Fold `merge` over named layers, starting from `Obj []`. Also returns, for every node of
    /// the result, the last layer that supplied it (wholesale or leaf) or merged into it
    /// (objects and lists combined by key/Append/Union/ByKey), keyed by `Path.print` pointers.
    /// Under `FirstWins` a kept base value keeps its earlier source. Conflicts carry the source
    /// names (`LeftSource` = last source of the base value at that path). After a conflict the
    /// fold continues with the base value kept, so later layers' conflicts are reported too.
    /// A non-object layer conflicts with the initial `Obj []` under `Fail`.
    let layers (policy: Policy) (named: (string * Node) list) : Result<Node * Provenance, Conflict list> =
        Merge.layers policy named
