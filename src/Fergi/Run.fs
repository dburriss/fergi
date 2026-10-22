namespace Fergi

open Fnode

/// What `plan` returns: the would-be output, the ops that turn the current output into it,
/// and where each merged leaf came from (layers only; later rules are not tracked).
type Plan =
    { Output: string
      Result: Node
      Ops: Op list
      Provenance: Provenance }

/// The injected loader: file name (a `Files.Inputs` / `Files.Output` value) to parsed document with source
/// locations, or every parse/load error. This is exactly what `Format.Parse` returns.
type Loader = string -> Result<Parsed, Error list>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Loader =

    /// In-memory loader over named nodes (empty `Locations`). An unknown name is a `path.notFound` error.
    let ofNodes (docs: Map<string, Node>) : Loader =
        fun name ->
            match Map.tryFind name docs with
            | Some n -> Ok { Node = n; Locations = Map.empty }
            | None -> Error [ Error.create ErrorCode.pathNotFound ("no such file " + name) [] ]

/// Runs a `Mapping` over loaded inputs. Pure: all IO is the injected `Loader`
/// (`string -> Result<Parsed, Error list>`, given a `Files.Inputs` / `Files.Output` value). Nothing is written.
///
/// Semantics:
/// - Names are validated before the loader is ever called: every `Mapping.Inputs` entry needs a
///   `Files` entry (`mapping.inputMissing`), every `Files` entry and every input named in a
///   rule must be declared (`mapping.inputUnknown`), at most one `Layer` rule (`mapping.invalid`).
/// - All inputs load; every load error (all errors of every file) is reported; each error keeps its Path and Location
///   and gets `Context` entries `input` (the name) and `file`.
/// - A merge conflict whose pointer has a recorded span in the right (else left) source's `Locations` gets
///   that span as `Location` and the message names the input, file and line:column. No span, no Location.
/// - `ListsBy`/`Conflict`/`NullDeletes` build the merge policy (`Policy.defaults` plus the list rules,
///   `ConflictMode` and null semantics) from ALL rules first, so their position relative to `Layer` does not
///   matter. The layered inputs then merge into the start document (`Obj []` with no `Layer`); conflicts
///   become `merge.conflict` errors (every conflict is reported, not just the first). `OnConflict.Resolve`
///   is not expressible in a mapping (it holds a function); use `Doc.layers` with a custom `Policy`.
/// - `Copy`/`Set`/`DefaultTo` create missing parent objects (`/metadata/name` works when `/metadata` is absent).
/// - Other rules run top to bottom against the current document, each compiled to `Op`s and applied
///   atomically (errors get context `rule` (index) and `kind`, plus `op` from `Patch.apply`; a failing
///   `Copy` source adds `copy source`; `Ensure` failures add `ensure`); the first failing rule stops the run (later rules would see a state that never existed).
///   Selectors resolve against the current document. `DefaultTo`/`Set` with a `Where` that matches
///   nothing is an error (there is no item to address). `Drop` of an absent path is a no-op.
/// - `Ensure` hooks run on the result; failures become `ensure.failed` errors.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fergi =

    let private err code msg = Error.create code msg []

    let private validate (m: Mapping) (f: Files) : Error list =
        let declared = m.Inputs
        let lists = m.InputLists
        let missing =
            (m.Inputs |> List.filter (fun n -> not (Map.containsKey n f.Inputs)))
            @ (lists |> List.filter (fun n -> not (Map.containsKey n f.InputLists)))
            |> List.map (fun n -> err ErrorCodes.mappingInputMissing ("Input '" + n + "' has no entry in Files"))
        let unknownFiles =
            (f.Inputs |> Map.toList |> List.map fst |> List.filter (fun n -> not (List.contains n declared)))
            @ (f.InputLists |> Map.toList |> List.map fst |> List.filter (fun n -> not (List.contains n lists)))
            |> List.map (fun n -> err ErrorCodes.mappingInputUnknown ("Files entry '" + n + "' is not a declared input"))
        missing @ unknownFiles @ Mapping.validate m

    /// Every single-file input, and every file of every inputList (as `(file, document)` in file order).
    type private Loaded =
        { Single: Map<string, Parsed>
          Lists: Map<string, (string * Parsed) list> }

    let private loadAll (m: Mapping) (f: Files) (load: Loader) : Result<Loaded, Error list> =
        let tag (file: string) (name: string) (es: Error list) =
            es |> List.map (fun e -> e |> Error.withContext "file" file |> Error.withContext "input" name)
        let singles =
            m.Inputs
            |> List.map (fun n ->
                let file = Map.find n f.Inputs
                match load file with
                | Ok p -> Ok(n, p)
                | Error es -> Error(tag file n es))
        let lists =
            m.InputLists
            |> List.map (fun n ->
                let results =
                    Map.find n f.InputLists
                    |> List.mapi (fun i file ->
                        match load file with
                        | Ok p -> Ok(file, p)
                        | Error es -> Error(tag file n es |> List.map (Error.withContext "index" (string i))))
                let errs = results |> List.collect (fun r -> match r with Error es -> es | Ok _ -> [])
                if List.isEmpty errs then Ok(n, results |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None))
                else Error errs)
        let errors =
            (singles |> List.collect (fun r -> match r with Error es -> es | Ok _ -> []))
            @ (lists |> List.collect (fun r -> match r with Error es -> es | Ok _ -> []))
        if List.isEmpty errors then
            Ok
                { Single = singles |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None) |> Map.ofList
                  Lists = lists |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None) |> Map.ofList }
        else Error errors

    /// Tie a conflict to a source span: right source first, then left; only when that input recorded a span
    /// for the conflict's pointer.
    let private conflictError (f: Files) (inputs: Map<string, Parsed>) (c: Conflict) : Error =
        let e = Conflict.toError c
        let key = Path.print c.Path
        let found =
            [ c.RightSource; c.LeftSource ]
            |> List.choose id
            |> List.tryPick (fun n ->
                match Map.tryFind n inputs with
                | Some p -> Map.tryFind key p.Locations |> Option.map (fun span -> n, span)
                | None -> None)
        match found with
        | Some(n, span) ->
            let file = match Map.tryFind n f.Inputs with Some x -> x | None -> n
            { e with
                Message =
                    e.Message + " - input '" + n + "' (" + file + ") at " + string span.Start.Line + ":" + string span.Start.Column
                Location = Some span }
            |> Error.withContext "file" file
            |> Error.withContext "input" n
        | None -> e

    /// `Set` ops that create the missing parent objects of `path` (`Op.Set` is strict), so a
    /// write to `/metadata/name` works when `/metadata` is absent. Only `Key` steps create objects;
    /// a missing array index is left to fail in the op itself. Then the write itself.
    let private setCreating (path: Path) (v: Node) (doc: Node) : Op list =
        let len = List.length path
        let firstMissing =
            [ 1 .. len - 1 ]
            |> List.tryFind (fun n ->
                match List.item (n - 1) path with
                | Key _ -> not (Node.exists (List.truncate n path) doc)
                | Index _ -> false)
        // once a parent is created everything below it is new: stop probing the original doc
        match firstMissing with
        | None -> [ Op.Set(path, v) ]
        | Some first -> [ for n in first .. len - 1 -> Op.Set(List.truncate n path, Obj []) ] @ [ Op.Set(path, v) ]

    /// Evaluate one `Compute` argument; a missing source is `path.notFound` naming where it was read from.
    let private evalArg (inputs: Map<string, Node>) (doc: Node) (arg: Arg) : Result<Node, Error> =
        match arg with
        | Arg.Literal v -> Ok v
        | Arg.Doc p ->
            Doc.get p doc |> Result.mapError (Error.withContext "compute argument" (Selector.print p))
        | Arg.Input(name, p) ->
            Doc.get p (Map.find name inputs)
            |> Result.mapError (Error.withContext "compute argument" (name + ":" + Selector.print p))
        | Arg.Inputs -> Ok(Obj(inputs |> Map.toList))

    let private compute (fns: Map<string, Function>) (inputs: Map<string, Node>) (c: ComputeSpec) (doc: Node) : Result<Op list, Error> =
        let args =
            c.Args
            |> List.fold
                (fun acc a ->
                    match acc with
                    | Error _ -> acc
                    | Ok vs -> evalArg inputs doc a |> Result.map (fun v -> vs @ [ v ]))
                (Ok [])
        args
        |> Result.bind (fun vs ->
            // the contract says a function does not throw; a buggy one must still not take the run down
            let called = try (Map.find c.Fn fns) vs with e -> Error("threw: " + e.Message)
            match called with
            | Ok v -> Ok v
            | Error msg ->
                Error(
                    err ErrorCodes.computeFailed (c.Fn + ": " + msg)
                    |> Error.withContext "function" c.Fn
                    |> Error.withContext "dest" (Selector.print c.Dest)
                ))
        |> Result.bind (fun v -> Selector.resolve c.Dest doc |> Result.map (fun d -> setCreating d v doc))

    let private compile (fns: Map<string, Function>) (inputs: Map<string, Node>) (rule: Rule) (doc: Node) : Result<Op list, Error> =
        match rule with
        | Rule.Layer _ | Rule.ListsBy _ | Rule.Conflict _ | Rule.NullDeletes _ | Rule.Collect _ -> Ok []
        | Rule.Copy((name, src), dest) ->
            match Doc.get src (Map.find name inputs) with
            | Error e -> Error(Error.withContext "copy source" (name + ":" + Selector.print src) e)
            | Ok v -> Selector.resolve dest doc |> Result.map (fun d -> setCreating d v doc)
        | Rule.Rename(a, b) ->
            Selector.resolve a doc
            |> Result.bind (fun pa ->
                Selector.resolve b doc
                |> Result.map (fun pb ->
                    let parents = setCreating pb Null doc |> List.rev |> List.tail |> List.rev
                    parents @ [ Op.Move(pa, pb) ]))
        | Rule.DefaultTo(p, v) ->
            Selector.resolve p doc
            |> Result.bind (fun rp ->
                match Node.tryFind rp doc with
                | Ok(Some _) -> Ok []
                | Ok None -> Ok(setCreating rp v doc)
                | Error e -> Error e)
        | Rule.Set(p, v) -> Selector.resolve p doc |> Result.map (fun rp -> setCreating rp v doc)
        | Rule.Compute c -> compute fns inputs c doc
        | Rule.Drop p ->
            match Selector.resolve p doc with
            | Error e when e.Code = ErrorCodes.selectorNoMatch || e.Code = ErrorCode.pathNotFound -> Ok []
            | Error e -> Error e
            | Ok rp ->
                match Node.tryFind rp doc with
                | Ok(Some _) -> Ok [ Op.Remove rp ]
                | Ok None -> Ok []
                | Error e -> Error e

    let private ruleName (rule: Rule) =
        match rule with
        | Rule.Layer _ -> "Layer"
        | Rule.ListsBy _ -> "ListsBy"
        | Rule.Conflict _ -> "Conflict"
        | Rule.NullDeletes _ -> "NullDeletes"
        | Rule.Copy _ -> "Copy"
        | Rule.Rename _ -> "Rename"
        | Rule.DefaultTo _ -> "DefaultTo"
        | Rule.Set _ -> "Set"
        | Rule.Drop _ -> "Drop"
        | Rule.Collect _ -> "Collect"
        | Rule.Compute _ -> "Compute"

    /// The sort key of one entry: all strings, all ints or all floats.
    type private SortKey =
        | KStr of string
        | KInt of int64
        | KFloat of float

    let private sortKeyOf (n: Node) : SortKey option =
        match n with
        | Str x -> Some(KStr x)
        | Int x -> Some(KInt x)
        | Float x -> Some(KFloat x)
        | _ -> None

    /// Order `(file, index, entry)` by the value at `by`. Every entry needs the field, and the values must
    /// be all strings, all ints or all floats; otherwise every problem is a `collect.sortKey` error.
    let private sortEntries (input: string) (by: SelectorPath) (entries: (string * int * Node) list) : Result<Node list, Error list> =
        let keyed =
            entries
            |> List.map (fun (file, i, entry) ->
                let fail why =
                    Error(
                        Error.create ErrorCodes.collectSortKey ("Cannot sort by " + Selector.print by + ": " + why) []
                        |> Error.withContext "file" file
                        |> Error.withContext "index" (string i)
                        |> Error.withContext "input" input
                    )
                match Doc.get by entry with
                | Error _ -> fail "the field is missing"
                | Ok v ->
                    match sortKeyOf v with
                    | Some k -> Ok(k, entry)
                    | None -> fail "the value is not a string or a number")
        let errs = keyed |> List.collect (fun r -> match r with Error e -> [ e ] | Ok _ -> [])
        if not (List.isEmpty errs) then Error errs
        else
            let ok = keyed |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None)
            let allStr = ok |> List.forall (fun (k, _) -> match k with KStr _ -> true | _ -> false)
            let allInt = ok |> List.forall (fun (k, _) -> match k with KInt _ -> true | _ -> false)
            let allFloat = ok |> List.forall (fun (k, _) -> match k with KFloat _ -> true | _ -> false)
            if allStr then
                Ok(ok |> List.sortBy (fun (k, _) -> match k with KStr x -> x | _ -> "") |> List.map snd)
            elif allInt then
                Ok(ok |> List.sortBy (fun (k, _) -> match k with KInt x -> x | _ -> 0L) |> List.map snd)
            elif allFloat then
                Ok(ok |> List.sortBy (fun (k, _) -> match k with KFloat x -> x | _ -> 0.0) |> List.map snd)
            else
                Error [ Error.create ErrorCodes.collectSortKey ("Cannot sort by " + Selector.print by + ": the values mix strings and numbers") []
                        |> Error.withContext "input" input ]

    let rec private runRules
        (fns: Map<string, Function>)
        (inputs: Map<string, Node>)
        (lists: Map<string, (string * Node) list>)
        (rules: Rule list)
        (start: Node)
        : Result<Node, Error list> =
        let step (acc: Result<Node, Error list>) (i: int, rule: Rule) =
            match acc with
            | Error _ -> acc
            | Ok doc ->
                let tag = Error.withContext "kind" (ruleName rule) >> Error.withContext "rule" (string i)
                let compiled =
                    match rule with
                    | Rule.Collect c -> compileCollect fns lists c doc
                    | _ -> compile fns inputs rule doc |> Result.mapError (fun e -> [ e ])
                match compiled with
                | Error es -> Error(List.map tag es)
                | Ok ops -> Patch.apply ops doc |> Result.mapError (List.map tag)
        rules |> List.indexed |> List.fold step (Ok start)

    /// One entry per file of the inputList (each built by running `Entry` over an empty object with the file
    /// bound to the input name), sorted if asked, then written at `Into`. Every file's errors are reported.
    and private compileCollect
        (fns: Map<string, Function>)
        (lists: Map<string, (string * Node) list>)
        (c: CollectSpec)
        (doc: Node)
        : Result<Op list, Error list> =
        let files = Map.tryFind c.Input lists |> Option.defaultValue []
        let built =
            files
            |> List.mapi (fun i (file, node) ->
                runRules fns (Map.ofList [ c.Input, node ]) Map.empty c.Entry (Obj [])
                |> Result.map (fun entry -> file, i, entry)
                |> Result.mapError (
                    List.map (fun e ->
                        e
                        |> Error.withContext "file" file
                        |> Error.withContext "index" (string i)
                        |> Error.withContext "input" c.Input)
                ))
        let errs = built |> List.collect (fun r -> match r with Error es -> es | Ok _ -> [])
        if not (List.isEmpty errs) then Error errs
        else
            let entries = built |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None)
            let ordered =
                match c.SortBy with
                | None -> Ok(entries |> List.map (fun (_, _, e) -> e))
                | Some by -> sortEntries c.Input by entries
            ordered
            |> Result.bind (fun nodes ->
                Selector.resolve c.Into doc
                |> Result.map (fun into -> setCreating into (Arr nodes) doc)
                |> Result.mapError (fun e -> [ e ]))

    let private runEnsure (m: Mapping) (result: Node) : Error list =
        m.Ensure
        |> List.collect (fun (name, check) ->
            let checked' =
                match check with
                | Fn f ->
                    // a closure hook is host code; a throw must not take the run down (as with `compute`)
                    try f result with e -> Error [ Error.create ErrorCodes.ensureFailed ("Ensure '" + name + "' threw: " + e.Message) [] ]
                | Jsonata expr ->
                    let failed msg = Error [ Error.create ErrorCodes.ensureFailed ("Ensure '" + name + "' " + msg) [] ]
                    match Map.tryFind "jsonata" m.Functions with
                    | None -> failed "needs function 'jsonata', which is not registered"
                    | Some f ->
                        match (try f [ Str expr; result ] with e -> Error e.Message) with
                        | Ok(Bool true) -> Ok()
                        | Ok(Bool false) -> Error []
                        | Ok _ -> failed "must return a boolean"
                        | Error msg -> failed ("could not be evaluated: " + msg)
            match checked' with
            | Ok() -> []
            | Error [] -> [ err ErrorCodes.ensureFailed ("Ensure '" + name + "' failed") |> Error.withContext "ensure" name ]
            | Error es ->
                es
                |> List.map (fun e ->
                    Error.create ErrorCodes.ensureFailed e.Message e.Path |> Error.withContext "ensure" name))

    let private policyOf (rules: Rule list) : Policy =
        rules
        |> List.fold
            (fun p r ->
                match r with
                | Rule.ListsBy(path, key) -> Policy.withListsBy path key p
                | Rule.Conflict mode -> Policy.withConflict (ConflictMode.toOnConflict mode) p
                | Rule.NullDeletes b -> Policy.withNullDeletes b p
                | _ -> p)
            Policy.defaults

    let private execute (m: Mapping) (f: Files) (load: Loader) : Result<Node * Provenance, Error list> =
        match validate m f with
        | _ :: _ as errs -> Error errs
        | [] ->
            match loadAll m f load with
            | Error errs -> Error errs
            | Ok loaded ->
                let parsed = loaded.Single
                let inputs = parsed |> Map.map (fun _ p -> p.Node)
                let lists = loaded.Lists |> Map.map (fun _ fs -> fs |> List.map (fun (file, p) -> file, p.Node))
                let layerNames =
                    m.Rules |> List.tryPick (fun r -> match r with Rule.Layer ns -> Some ns | _ -> None) |> Option.defaultValue []
                let layered = layerNames |> List.map (fun n -> n, Map.find n inputs)
                match Doc.layers (policyOf m.Rules) layered with
                | Error cs -> Error(List.map (conflictError f parsed) cs)
                | Ok(start, prov) ->
                    match runRules m.Functions inputs lists m.Rules start with
                    | Error errs -> Error errs
                    | Ok result ->
                        match runEnsure m result with
                        | [] -> Ok(result, prov)
                        | errs -> Error errs

    /// Run the mapping. Ok: `Map.ofList [ files.Output, document ]`. Any error: nothing is returned.
    let run (m: Mapping) (f: Files) (load: Loader) : Result<Map<string, Node>, Error list> =
        execute m f load |> Result.map (fun (result, _) -> Map.ofList [ f.Output, result ])

    /// Dry run: the same result plus `Patch.diff current result`. `current` is the output loaded
    /// via the loader; if loading it fails for any reason (absent file, parse error) it is treated
    /// as absent and diffed from `Null`.
    let plan (m: Mapping) (f: Files) (load: Loader) : Result<Plan, Error list> =
        execute m f load
        |> Result.map (fun (result, prov) ->
            let current =
                match load f.Output with
                | Ok p -> p.Node
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
        (load: Loader)
        : Result<Map<string, Node>, Error list> =
        run (Mapping.create (List.map fst inputs) rules) { Inputs = Map.ofList inputs; InputLists = Map.empty; Output = output } load
