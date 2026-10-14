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
