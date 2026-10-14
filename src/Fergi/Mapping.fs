namespace Fergi

open Fnode

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

/// `Ensure` is a field rather than a `Rule` so `Rule` stays plain data.
type Mapping =
    { Inputs: string list
      Rules: Rule list
      Ensure: (string * (Node -> Result<unit, Error list>)) list }

/// Where the inputs live and where the output goes. Core never reads or writes: these are
/// opaque strings handed to the injected loader (and used as the key of the result).
type Files =
    { Inputs: Map<string, string>
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
        { Inputs = inputs; Rules = rules; Ensure = [] }

    /// Add a named `Ensure` hook (run on the final result).
    let ensure (name: string) (check: Node -> Result<unit, Error list>) (m: Mapping) : Mapping =
        { m with Ensure = m.Ensure @ [ name, check ] }

    /// Rule helper: copy from a source-qualified string, e.g. `copy "service:/name" "/metadata/name"`.
    let copy (from: string) (dest: string) (inputs: string list) : Result<Rule, Error> =
        match SourcePath.parse inputs from, Selector.parse dest with
        | Ok { Input = Some n; Path = p }, Ok d -> Ok(Rule.Copy((n, p), d))
        | Ok _, Ok _ -> Error(Error.create ErrorCode.pathInvalid ("Copy source needs an input name: " + from) [])
        | Error e, _ -> Error e
        | _, Error e -> Error e
