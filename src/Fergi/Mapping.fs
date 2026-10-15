namespace Fergi

open Fnode

/// How a mapping resolves two values that cannot be combined structurally (differing scalars, a type
/// mismatch, differing lists under `Replace`). The data-only subset of `OnConflict`: `OnConflict.Resolve`
/// takes a function, so it cannot live in plain data and stays code-only (set it with `Policy.withConflict`
/// on `Doc.merge` / `Doc.layers`). Qualified access, because `Fail` etc. are also `OnConflict` cases.
[<RequireQualifiedAccess>]
type ConflictMode =
    /// The later layer wins (the default).
    | LastWins
    /// The earlier layer wins.
    | FirstWins
    /// Every disagreement is reported as a `merge.conflict` error.
    | Fail

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ConflictMode =

    /// The `OnConflict` this mode stands for.
    let toOnConflict (m: ConflictMode) : OnConflict =
        match m with
        | ConflictMode.LastWins -> OnConflict.LastWins
        | ConflictMode.FirstWins -> OnConflict.FirstWins
        | ConflictMode.Fail -> OnConflict.Fail

/// One step of a mapping. Plain data (no functions), so it can be serialised or built by a
/// future computation expression. Qualified access is required so `Rule.Set`/`Rule.Drop` never
/// collide with `Op.Set`/`Op.Remove`.
[<RequireQualifiedAccess>]
type Rule =
    /// Merge these inputs (in order, later wins per the policy) into the starting document.
    /// At most one `Layer` rule per mapping.
    | Layer of inputs: string list
    /// Merge the list at this path by the string field `key` (adds to the merge `Policy`).
    | ListsBy of Path * key: string
    /// Choose how conflicting values are reconciled in the layer merge (adds to the merge `Policy`;
    /// default `ConflictMode.LastWins`). At most one `Conflict` rule per mapping.
    | Conflict of ConflictMode
    /// `true` (the default): a `null` in a later layer deletes the key. `false`: `null` is an ordinary
    /// value and is kept. Adds to the merge `Policy`. At most one `NullDeletes` rule per mapping.
    | NullDeletes of bool
    /// Copy the value at `input:/selector` (read from the NAMED input, not the merged document)
    /// to `dest` in the document being built. `path.notFound` if the source is absent.
    | Copy of from: (string * SelectorPath) * dest: SelectorPath
    /// Move the value at the first path to the second (Patch `Move`).
    | Rename of SelectorPath * SelectorPath
    /// Set the value only when the path is absent.
    | DefaultTo of SelectorPath * Node
    /// Set (create or overwrite) the value.
    | Set of SelectorPath * Node
    /// Remove the path. Dropping an absent path is a no-op.
    | Drop of SelectorPath
    /// Aggregation: build one entry per file of the `inputList` and set the resulting list at `Into`.
    | Collect of CollectSpec
    /// Call the registered function `Fn` on the evaluated `Args` and set the result at `Dest`.
    /// Only the function's name is stored, so the rule stays serialisable.
    | Compute of ComputeSpec

/// Where one argument of a `Compute` comes from.
and [<RequireQualifiedAccess>] Arg =
    /// The value at `input:/selector` in the NAMED input (like `Copy`). `path.notFound` if absent.
    | Input of string * SelectorPath
    /// The value at this selector in the document being built, as it is at this point of the run.
    | Doc of SelectorPath
    /// A fixed value.
    | Literal of Node

/// `Fn` names an entry of `Mapping.Functions` (the built-ins are in `Functions.builtins`).
and ComputeSpec =
    { Fn: string
      Args: Arg list
      Dest: SelectorPath }

/// How one `inputList` becomes a list. Every file of `Input` is run through `Entry` (starting from an
/// empty object, the file bound to the name `Input`) to make one entry; `SortBy` then orders the entries
/// by that field (a stable sort, files keep their order otherwise). `Entry` may hold `Copy` (whose source
/// name must be `Input`), `Rename`, `DefaultTo`, `Set` and `Drop`.
and CollectSpec =
    { Input: string
      Into: SelectorPath
      Entry: Rule list
      SortBy: SelectorPath option }

/// `Ensure` is a field rather than a `Rule` so `Rule` stays plain data.
/// `Inputs` name single files, `InputLists` name inputs that stand for a list of files (see `Rule.Collect`).
type Mapping =
    { Inputs: string list
      InputLists: string list
      Rules: Rule list
      Ensure: (string * (Node -> Result<unit, Error list>)) list
      /// The functions `Rule.Compute` may call by name. Kept beside the rules, not in them, so `Rule` stays
      /// plain data; a mapping read from a file gets the registry it runs with.
      Functions: Map<string, Function> }

/// Where the inputs live and where the output goes. Core never reads or writes: these are
/// opaque strings handed to the injected loader (and used as the key of the result).
/// `InputLists` binds each `Mapping.InputLists` name to its files, in order.
type Files =
    { Inputs: Map<string, string>
      InputLists: Map<string, string list>
      Output: string }

/// A pointer optionally qualified by an input name: `service:/name`. No prefix (`/name` or
/// the empty string) means the document being built.
type SourcePath =
    { Input: string option
      Path: SelectorPath }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SourcePath =

    /// Parse `input:/pointer`. `inputs` are the declared input names: an unknown name is
    /// `mapping.inputUnknown`, bad pointer syntax is `path.invalid`.
    let parse (inputs: string list) (s: string) : Result<SourcePath, Error> =
        if s = "" || s.[0] = '/' then
            Selector.parse s |> Result.map (fun p -> { Input = None; Path = p })
        else
            let i = s.IndexOf ':'
            if i <= 0 then
                Error(Error.create ErrorCode.pathInvalid ("Source path needs 'input:/pointer' or '/pointer': " + s) [])
            else
                let name = s.Substring(0, i)
                if not (List.contains name inputs) then
                    Error(Error.create ErrorCodes.mappingInputUnknown ("Unknown input '" + name + "' in source path: " + s) [])
                else
                    Selector.parse (s.Substring(i + 1)) |> Result.map (fun p -> { Input = Some name; Path = p })

    let print (sp: SourcePath) : string =
        match sp.Input with
        | Some n -> n + ":" + Selector.print sp.Path
        | None -> Selector.print sp.Path

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Mapping =

    /// A mapping over `inputs` with `rules` and no `Ensure` hooks.
    let create (inputs: string list) (rules: Rule list) : Mapping =
        { Inputs = inputs; InputLists = []; Rules = rules; Ensure = []; Functions = Functions.builtins }

    /// Declare inputs that stand for a list of files.
    let withInputLists (names: string list) (m: Mapping) : Mapping =
        { m with InputLists = m.InputLists @ names }

    /// Add a named `Ensure` hook (run on the final result).
    let ensure (name: string) (check: Node -> Result<unit, Error list>) (m: Mapping) : Mapping =
        { m with Ensure = m.Ensure @ [ name, check ] }

    /// Register (or replace) a function that `Rule.Compute` can call by name.
    let withFunction (name: string) (fn: Function) (m: Mapping) : Mapping =
        { m with Functions = Map.add name fn m.Functions }

    /// Rule helper: copy from a source-qualified string, e.g. `copy "service:/name" "/metadata/name"`.
    let copy (from: string) (dest: string) (inputs: string list) : Result<Rule, Error> =
        match SourcePath.parse inputs from, Selector.parse dest with
        | Ok { Input = Some n; Path = p }, Ok d -> Ok(Rule.Copy((n, p), d))
        | Ok _, Ok _ -> Error(Error.create ErrorCode.pathInvalid ("Copy source needs an input name: " + from) [])
        | Error e, _ -> Error e
        | _, Error e -> Error e
