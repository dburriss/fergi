namespace Fergi

open Fnode

/// What `plan` returns: the would-be output, the ops that turn the current output into it,
/// and where each merged leaf came from (layers only; later rules are not tracked).
type Plan =
    { Output: string
      Result: Node
      Ops: Op list
      Provenance: Provenance }

/// Runs a `Mapping` over loaded inputs. Pure: all IO is the injected loader (`string -> Result<Node, Error>`,
/// given a `Files.Inputs` / `Files.Output` value). Nothing is written.
///
/// Semantics:
/// - Names are validated before the loader is ever called: every `Mapping.Inputs` entry needs a
///   `Files` entry (`mapping.inputMissing`), every `Files` entry and every input named in a
///   rule must be declared (`mapping.inputUnknown`), at most one `Layer` rule (`mapping.invalid`).
/// - All inputs load; every load error is reported (message prefixed with the input name).
/// - `Layer`/`ListsBy` build the merge policy (`Policy.defaults` plus list rules) and the layered
///   inputs merge into the start document (`Obj []` with no `Layer`); conflicts become `merge.conflict` errors.
/// - `Copy`/`Set`/`DefaultTo` create missing parent objects (`/metadata/name` works when `/metadata` is absent).
/// - Other rules run top to bottom against the current document, each compiled to `Op`s and applied
///   atomically; the first failing rule stops the run (later rules would see a state that never existed).
///   Selectors resolve against the current document. `DefaultTo`/`Set` with a `Where` that matches
///   nothing is an error (there is no item to address). `Drop` of an absent path is a no-op.
/// - `Ensure` hooks run on the result; failures become `ensure.failed` errors.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fergi =

    let private err code msg = Error.create code msg []

    let private validate (m: Mapping) (f: Files) : Error list =
        let declared = m.Inputs
        let missing =
            m.Inputs
            |> List.filter (fun n -> not (Map.containsKey n f.Inputs))
            |> List.map (fun n -> err ErrorCodes.mappingInputMissing ("Input '" + n + "' has no entry in Files"))
        let unknownFiles =
            f.Inputs
            |> Map.toList
            |> List.map fst
            |> List.filter (fun n -> not (List.contains n declared))
            |> List.map (fun n -> err ErrorCodes.mappingInputUnknown ("Files entry '" + n + "' is not a declared input"))
        let used =
            m.Rules
            |> List.collect (fun r ->
                match r with
                | Rule.Layer ns -> ns
                | Rule.Copy((n, _), _) -> [ n ]
                | _ -> [])
        let unknownRules =
            used
            |> List.distinct
            |> List.filter (fun n -> not (List.contains n declared))
            |> List.map (fun n -> err ErrorCodes.mappingInputUnknown ("Rule refers to undeclared input '" + n + "'"))
        let layers = m.Rules |> List.filter (fun r -> match r with Rule.Layer _ -> true | _ -> false)
        let multi =
            if List.length layers > 1 then [ err ErrorCodes.mappingInvalid "At most one Layer rule is allowed" ] else []
        missing @ unknownFiles @ unknownRules @ multi

    let private loadAll (m: Mapping) (f: Files) (load: string -> Result<Node, Error>) : Result<Map<string, Node>, Error list> =
        let results =
            m.Inputs
            |> List.map (fun n ->
                let file = Map.find n f.Inputs
                match load file with
                | Ok node -> Ok(n, node)
                | Error e -> Error { e with Message = "input '" + n + "' (" + file + "): " + e.Message })
        let errors = results |> List.choose (fun r -> match r with Error e -> Some e | Ok _ -> None)
        if List.isEmpty errors then
            Ok(results |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None) |> Map.ofList)
        else Error errors

    let private isAbsent (e: Error) = e.Code = ErrorCode.pathNotFound || e.Code = ErrorCodes.selectorNoMatch

    /// `Set` ops that create the missing parent objects of `path` (Fnode's `set` does not), so a
    /// write to `/metadata/name` works when `/metadata` is absent. Only `Key` steps create objects;
    /// a missing array index is left to fail in the op itself. Then the write itself.
    let private setCreating (path: Path) (v: Node) (doc: Node) : Op list =
        let rec parents (pre: Path) (rest: Path) (acc: Op list) =
            match rest with
            | [] | [ _ ] -> List.rev acc
            | (Key _ as seg) :: tl ->
                let here = pre @ [ seg ]
                match Node.tryGet here doc with
                | Error e when e.Code = ErrorCode.pathNotFound -> parents here tl (Set(here, Obj []) :: acc)
                | _ -> parents here tl acc
            | seg :: tl -> parents (pre @ [ seg ]) tl acc
        // once a parent is created everything below it is new: stop probing the original doc
        let created = parents [] path []
        match created with
        | [] -> [ Set(path, v) ]
        | first :: _ ->
            let firstPath = match first with Set(p, _) -> p | _ -> []
            let below =
                [ for n in List.length firstPath + 1 .. List.length path - 1 -> Set(List.truncate n path, Obj []) ]
            first :: below @ [ Set(path, v) ]

    let private compile (inputs: Map<string, Node>) (rule: Rule) (doc: Node) : Result<Op list, Error> =
        match rule with
        | Rule.Layer _ | Rule.ListsBy _ -> Ok []
        | Rule.Copy((name, src), dest) ->
            match Doc.get src (Map.find name inputs) with
            | Error e -> Error { e with Message = "copy source '" + name + ":" + Selector.print src + "': " + e.Message }
            | Ok v -> Selector.resolve dest doc |> Result.map (fun d -> setCreating d v doc)
        | Rule.Rename(a, b) ->
            Selector.resolve a doc
            |> Result.bind (fun pa ->
                Selector.resolve b doc
                |> Result.map (fun pb ->
                    let parents = setCreating pb Null doc |> List.rev |> List.tail |> List.rev
                    parents @ [ Move(pa, pb) ]))
        | Rule.DefaultTo(p, v) ->
            Selector.resolve p doc
            |> Result.bind (fun rp ->
                match Node.tryGet rp doc with
                | Ok _ -> Ok []
                | Error e when e.Code = ErrorCode.pathNotFound -> Ok(setCreating rp v doc)
                | Error e -> Error e)
        | Rule.Set(p, v) -> Selector.resolve p doc |> Result.map (fun rp -> setCreating rp v doc)
        | Rule.Drop p ->
            match Selector.resolve p doc with
            | Error e when isAbsent e -> Ok []
            | Error e -> Error e
            | Ok rp ->
                match Node.tryGet rp doc with
                | Ok _ -> Ok [ Remove rp ]
                | Error e when e.Code = ErrorCode.pathNotFound -> Ok []
                | Error e -> Error e

    let private ruleName (rule: Rule) =
        match rule with
        | Rule.Layer _ -> "Layer"
        | Rule.ListsBy _ -> "ListsBy"
        | Rule.Copy _ -> "Copy"
        | Rule.Rename _ -> "Rename"
        | Rule.DefaultTo _ -> "DefaultTo"
        | Rule.Set _ -> "Set"
        | Rule.Drop _ -> "Drop"

    let private runRules (inputs: Map<string, Node>) (rules: Rule list) (start: Node) : Result<Node, Error list> =
        let step (acc: Result<Node, Error list>) (i: int, rule: Rule) =
            match acc with
            | Error _ -> acc
            | Ok doc ->
                let tag = "rule " + string i + " (" + ruleName rule + "): "
                match compile inputs rule doc with
                | Error e -> Error [ { e with Message = tag + e.Message } ]
                | Ok ops ->
                    Patch.apply ops doc
                    |> Result.mapError (List.map (fun e -> { e with Message = tag + e.Message }))
        rules |> List.indexed |> List.fold step (Ok start)

    let private runEnsure (m: Mapping) (result: Node) : Error list =
        m.Ensure
        |> List.collect (fun (name, check) ->
            match check result with
            | Ok() -> []
            | Error [] -> [ err ErrorCodes.ensureFailed ("Ensure '" + name + "' failed") ]
            | Error es ->
                es |> List.map (fun e -> Error.create ErrorCodes.ensureFailed ("Ensure '" + name + "': " + e.Message) e.Path))

    let private policyOf (rules: Rule list) : Policy =
        rules
        |> List.fold
            (fun p r ->
                match r with
                | Rule.ListsBy(path, key) -> Policy.withListsBy path key p
                | _ -> p)
            Policy.defaults

    let private execute (m: Mapping) (f: Files) (load: string -> Result<Node, Error>) : Result<Node * Provenance, Error list> =
        match validate m f with
        | _ :: _ as errs -> Error errs
        | [] ->
            match loadAll m f load with
            | Error errs -> Error errs
            | Ok inputs ->
                let layerNames =
                    m.Rules |> List.tryPick (fun r -> match r with Rule.Layer ns -> Some ns | _ -> None) |> Option.defaultValue []
                let layered = layerNames |> List.map (fun n -> n, Map.find n inputs)
                match Doc.layers (policyOf m.Rules) layered with
                | Error cs -> Error(List.map Conflict.toError cs)
                | Ok(start, prov) ->
                    match runRules inputs m.Rules start with
                    | Error errs -> Error errs
                    | Ok result ->
                        match runEnsure m result with
                        | [] -> Ok(result, prov)
                        | errs -> Error errs

    /// Run the mapping. Ok: `Map.ofList [ files.Output, document ]`. Any error: nothing is returned.
    let run (m: Mapping) (f: Files) (load: string -> Result<Node, Error>) : Result<Map<string, Node>, Error list> =
        execute m f load |> Result.map (fun (result, _) -> Map.ofList [ f.Output, result ])

    /// Dry run: the same result plus `Patch.diff current result`. `current` is the output loaded
    /// via the loader; if loading it fails for any reason (absent file, parse error) it is treated
    /// as absent and diffed from `Null`.
    let plan (m: Mapping) (f: Files) (load: string -> Result<Node, Error>) : Result<Plan, Error list> =
        execute m f load
        |> Result.map (fun (result, prov) ->
            let current =
                match load f.Output with
                | Ok n -> n
                | Error _ -> Null
            { Output = f.Output
              Result = result
              Ops = Patch.diff current result
              Provenance = prov })

    /// One-off helper: `(name, file)` inputs, an output path and rules, no `Ensure`.
    let runFiles
        (inputs: (string * string) list)
        (output: string)
        (rules: Rule list)
        (load: string -> Result<Node, Error>)
        : Result<Map<string, Node>, Error list> =
        run (Mapping.create (List.map fst inputs) rules) { Inputs = Map.ofList inputs; Output = output } load
