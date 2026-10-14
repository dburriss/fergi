namespace Fergi

open Fnode

/// A disagreement found while merging. `Path` is where it happened (list conflicts such as a
/// bad `ByKey` item are reported at the path of the list). `LeftSource`/`RightSource` are the
/// layer names when the conflict comes from `Doc.layers`, else `None`.
type Conflict =
    { Path: Path
      Left: Node
      Right: Node
      LeftSource: string option
      RightSource: string option
      Reason: string }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Conflict =

    /// Describe a conflict as an Fnode `Error` with code `merge.conflict`.
    let toError (c: Conflict) : Error =
        let src (label: string) (s: string option) =
            match s with
            | Some n -> " (" + label + " from '" + n + "')"
            | None -> ""
        Error.create
            ErrorCodes.mergeConflict
            ("Merge conflict at '" + Path.print c.Path + "': " + c.Reason + src "left" c.LeftSource + src "right" c.RightSource)
            c.Path

/// How two arrays at the same path combine.
type ListStrategy =
    /// Treat the list as one atomic value: if the two differ, `OnConflict` decides which wins.
    | Replace
    /// Base items followed by overlay items.
    | Append
    /// Base items, then overlay items not already present (structural equality), first-seen order.
    | Union
    /// Items are objects identified by the string field named here; same-key items are merged
    /// (base order first, new items appended).
    | ByKey of string

/// What to do when two values must be reconciled and cannot be combined structurally:
/// differing scalars, a type mismatch (object vs array vs scalar), or differing lists under `Replace`.
/// Identical values never conflict.
type OnConflict =
    /// The overlay wins, no conflict.
    | LastWins
    /// The base wins, no conflict.
    | FirstWins
    /// Report a `Conflict` (the base value is kept in the partial result).
    | Fail
    /// Decide per path: given path, base and overlay return the value to use, or a `Conflict`.
    | Resolve of (Path -> Node -> Node -> Result<Node, Conflict>)

type Policy =
    { /// Strategy for lists with no matching entry in `ListRules`.
      Lists: ListStrategy
      /// Per-list overrides, matched on the exact `Path` of the list. First match wins.
      ListRules: (Path * ListStrategy) list
      Conflict: OnConflict
      /// true: an overlay `Null` under an object key removes that key. false: `Null` is an ordinary value.
      NullDeletes: bool }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Policy =

    /// Lists `Replace`, no rules, `LastWins`, `NullDeletes = true`.
    /// (Named `defaults` because `default` is an F# keyword.)
    let defaults: Policy =
        { Lists = Replace
          ListRules = []
          Conflict = LastWins
          NullDeletes = true }

    let withLists (s: ListStrategy) (p: Policy) : Policy = { p with Lists = s }

    /// Set the strategy for the list at exactly `path`, replacing an existing rule for that path.
    let withListRule (path: Path) (s: ListStrategy) (p: Policy) : Policy =
        { p with ListRules = (List.filter (fun (q, _) -> q <> path) p.ListRules) @ [ path, s ] }

    /// Merge the list at `path` by the string field `key` (`ByKey key`).
    let withListsBy (path: Path) (key: string) (p: Policy) : Policy = withListRule path (ByKey key) p

    let withConflict (c: OnConflict) (p: Policy) : Policy = { p with Conflict = c }

    let withNullDeletes (b: bool) (p: Policy) : Policy = { p with NullDeletes = b }
