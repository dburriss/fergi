namespace Fergi

open Fnode

/// Computation expressions over the data forms. Everything here is sugar: each builder only produces
/// a plain value (`Op list`, `Result`, `Files`, `Mapping`) that can equally be written by hand.
[<AutoOpen>]
module Builders =

    /// `ops { yield Op.Set(...); yield! more; if cond then yield ... }` builds an `Op list` for `Patch.apply`.
    type OpsBuilder() =
        member _.Yield(op: Op) : Op list = [ op ]
        member _.YieldFrom(ops: Op list) : Op list = ops
        member _.Zero() : Op list = []
        member _.Combine(a: Op list, b: Op list) : Op list = a @ b
        member _.Delay(f: unit -> Op list) : Op list = f ()
        member _.For(xs: 'a list, f: 'a -> Op list) : Op list = List.collect f xs

    /// `docs { let! a = load "x" and! b = load "y" in return ... }`: `Result<_, Error list>` where `and!`
    /// accumulates the errors of every source instead of stopping at the first.
    type DocsBuilder() =
        member _.Return(x: 'a) : Result<'a, Error list> = Ok x
        member _.ReturnFrom(r: Result<'a, Error list>) : Result<'a, Error list> = r
        member _.Zero() : Result<unit, Error list> = Ok()
        member _.Bind(r: Result<'a, Error list>, f: 'a -> Result<'b, Error list>) : Result<'b, Error list> =
            Result.bind f r
        member _.BindReturn(r: Result<'a, Error list>, f: 'a -> 'b) : Result<'b, Error list> = Result.map f r
        member _.MergeSources(a: Result<'a, Error list>, b: Result<'b, Error list>) : Result<'a * 'b, Error list> =
            match a, b with
            | Ok x, Ok y -> Ok(x, y)
            | Error e1, Error e2 -> Error(e1 @ e2)
            | Error e, _ -> Error e
            | _, Error e -> Error e

    /// `files { input "org" "defaults/org.yaml"; inputList "repos" [ ... ]; output "catalog.yaml" }` builds `Files`.
    type FilesBuilder() =
        let empty : Files = { Inputs = Map.empty; InputLists = Map.empty; Output = "" }
        member _.Yield(_: unit) : Files = empty
        member _.Zero() : Files = empty

        /// A single-file input: its name and the file.
        [<CustomOperation("input")>]
        member _.Input(f: Files, name: string, file: string) : Files = { f with Inputs = Map.add name file f.Inputs }

        /// An input that stands for a list of files (see `Rule.Collect`).
        [<CustomOperation("inputList")>]
        member _.InputList(f: Files, name: string, files: string list) : Files =
            { f with InputLists = Map.add name files f.InputLists }

        [<CustomOperation("output")>]
        member _.Output(f: Files, file: string) : Files = { f with Output = file }

    // ---- rule-building helpers shared by `entry` and `mapping` ----

    /// A rule that did not parse is an error in the builder's state, not an exception.
    let private parseSel (s: string) (k: SelectorPath -> Rule) : Result<Rule, Error> = Selector.parse s |> Result.map k

    let private ruleCopy (declared: string list option) (src: string) (dest: string) : Result<Rule, Error> =
        let names =
            match declared with
            | Some ns -> ns
            | None ->
                // inside an entry the input name is the text before the first ':'
                let i = src.IndexOf ':'
                if i > 0 then [ src.Substring(0, i) ] else []
        match SourcePath.parse names src, Selector.parse dest with
        | Ok { Input = Some n; Path = p }, Ok d -> Ok(Rule.Copy((n, p), d))
        | Ok _, Ok _ -> Error(Error.create ErrorCode.pathInvalid ("Copy source needs an input name: " + src) [])
        | Error e, _ -> Error e
        | _, Error e -> Error e

    let private pair (a: Result<'a, Error>) (b: Result<'b, Error>) : Result<'a * 'b, Error> =
        match a, b with
        | Ok x, Ok y -> Ok(x, y)
        | Error e, _ -> Error e
        | _, Error e -> Error e

    /// An argument of `compute`: a pointer (`"input:/path"` reads an input, `"/path"` the document being
    /// built) or a fixed value.
    type ArgSpec =
        | Ptr of string
        | Lit of Node
        /// All inputs as one object keyed by input name (`Arg.Inputs`).
        | AllInputs

    let private ruleCompute (declared: string list option) (fn: string) (args: ArgSpec list) (dest: string) : Result<Rule, Error> =
        let names (src: string) =
            match declared with
            | Some ns -> ns
            | None ->
                let i = src.IndexOf ':'
                if i > 0 then [ src.Substring(0, i) ] else []
        let arg (a: ArgSpec) : Result<Arg, Error> =
            match a with
            | Lit v -> Ok(Arg.Literal v)
            | AllInputs -> Ok Arg.Inputs
            | Ptr src ->
                SourcePath.parse (names src) src
                |> Result.map (fun sp ->
                    match sp.Input with
                    | Some n -> Arg.Input(n, sp.Path)
                    | None -> Arg.Doc sp.Path)
        let parsed =
            args
            |> List.fold
                (fun acc a ->
                    match acc, arg a with
                    | Ok xs, Ok x -> Ok(xs @ [ x ])
                    | Error e, _ -> Error e
                    | _, Error e -> Error e)
                (Ok [])
        pair parsed (Selector.parse dest)
        |> Result.map (fun (xs, d) -> Rule.Compute { Fn = fn; Args = xs; Dest = d })

    type EntryState =
        { EntryRules: Rule list
          EntryErrors: Error list }

    /// `entry { copy (r "/name") "/name"; set "/kind" (Str "service") }`: the rules that build one entry
    /// of a `Collect`. `r` is the function `each` hands you; it qualifies a pointer with the input name.
    type EntryBuilder() =
        let add (s: EntryState) (r: Result<Rule, Error>) : EntryState =
            match r with
            | Ok rule -> { s with EntryRules = s.EntryRules @ [ rule ] }
            | Error e -> { s with EntryErrors = s.EntryErrors @ [ e ] }
        let empty : EntryState = { EntryRules = []; EntryErrors = [] }
        member _.Yield(_: unit) : EntryState = empty
        member _.Zero() : EntryState = empty
        member _.Run(s: EntryState) : Result<Rule list, Error list> =
            if List.isEmpty s.EntryErrors then Ok s.EntryRules else Error s.EntryErrors

        [<CustomOperation("copy")>]
        member _.Copy(s: EntryState, src: string, dest: string) = add s (ruleCopy None src dest)
        [<CustomOperation("rename")>]
        member _.Rename(s: EntryState, a: string, b: string) =
            add s (pair (Selector.parse a) (Selector.parse b) |> Result.map Rule.Rename)
        [<CustomOperation("defaultTo")>]
        member _.DefaultTo(s: EntryState, p: string, v: Node) = add s (parseSel p (fun sp -> Rule.DefaultTo(sp, v)))
        [<CustomOperation("set")>]
        member _.Set(s: EntryState, p: string, v: Node) = add s (parseSel p (fun sp -> Rule.Set(sp, v)))
        [<CustomOperation("drop")>]
        member _.Drop(s: EntryState, p: string) = add s (parseSel p Rule.Drop)
        /// `compute "/fullName" "concat" [ Ptr(r "/first"); Lit(Str " "); Ptr(r "/last") ]`
        [<CustomOperation("compute")>]
        member _.Compute(s: EntryState, dest: string, fn: string, args: ArgSpec list) =
            add s (ruleCompute None fn args dest)

    type MappingState =
        { Inputs: string list
          InputLists: string list
          Rules: Rule list
          Ensure: (string * (Node -> Result<unit, Error list>)) list
          Functions: Map<string, Function>
          Errors: Error list }

    /// `mapping { inputs [...]; layer [...]; copy ...; each ... }` builds a `Mapping`. Pointer strings are
    /// parsed as the rules are added; every bad pointer is collected, and the result is
    /// `Error` with all of them (no exception). The same mapping can be written as `Mapping.create` over
    /// a `Rule list`.
    type MappingBuilder() =
        let empty : MappingState = { Inputs = []; InputLists = []; Rules = []; Ensure = []; Functions = Functions.builtins; Errors = [] }
        let add (s: MappingState) (r: Result<Rule, Error>) : MappingState =
            match r with
            | Ok rule -> { s with Rules = s.Rules @ [ rule ] }
            | Error e -> { s with Errors = s.Errors @ [ e ] }
        member _.Yield(_: unit) : MappingState = empty
        member _.Zero() : MappingState = empty
        member _.Run(s: MappingState) : Result<Mapping, Error list> =
            if List.isEmpty s.Errors then
                Ok { Mapping.Inputs = s.Inputs; InputLists = s.InputLists; Rules = s.Rules; Ensure = s.Ensure; Functions = s.Functions }
            else Error s.Errors

        /// Names of the single-file inputs.
        [<CustomOperation("inputs")>]
        member _.Inputs(s: MappingState, names: string list) = { s with Inputs = s.Inputs @ names }
        /// Names of inputs that each stand for a list of files (aggregation).
        [<CustomOperation("inputList")>]
        member _.InputList(s: MappingState, name: string) = { s with InputLists = s.InputLists @ [ name ] }
        [<CustomOperation("layer")>]
        member _.Layer(s: MappingState, names: string list) = add s (Ok(Rule.Layer names))
        [<CustomOperation("listsBy")>]
        member _.ListsBy(s: MappingState, path: string, key: string) =
            add s (Path.parse path |> Result.map (fun p -> Rule.ListsBy(p, key)))
        [<CustomOperation("conflict")>]
        member _.Conflict(s: MappingState, mode: ConflictMode) = add s (Ok(Rule.Conflict mode))
        [<CustomOperation("nullDeletes")>]
        member _.NullDeletes(s: MappingState, b: bool) = add s (Ok(Rule.NullDeletes b))
        [<CustomOperation("copy")>]
        member _.Copy(s: MappingState, src: string, dest: string) =
            add s (ruleCopy (Some(s.Inputs)) src dest)
        [<CustomOperation("rename")>]
        member _.Rename(s: MappingState, a: string, b: string) =
            add s (pair (Selector.parse a) (Selector.parse b) |> Result.map Rule.Rename)
        [<CustomOperation("defaultTo")>]
        member _.DefaultTo(s: MappingState, p: string, v: Node) = add s (parseSel p (fun sp -> Rule.DefaultTo(sp, v)))
        [<CustomOperation("set")>]
        member _.Set(s: MappingState, p: string, v: Node) = add s (parseSel p (fun sp -> Rule.Set(sp, v)))
        [<CustomOperation("drop")>]
        member _.Drop(s: MappingState, p: string) = add s (parseSel p Rule.Drop)
        /// Call a registered function and set its result: `compute "/fullName" "concat" [ Ptr "person:/first"; Lit(Str " "); Ptr "person:/last" ]`.
        [<CustomOperation("compute")>]
        member _.Compute(s: MappingState, dest: string, fn: string, args: ArgSpec list) =
            add s (ruleCompute (Some s.Inputs) fn args dest)
        /// Register a function by name for `compute` (the builder's mapping starts with the built-ins).
        [<CustomOperation("func")>]
        member _.Func(s: MappingState, name: string, fn: Function) = { s with Functions = Map.add name fn s.Functions }
        [<CustomOperation("ensure")>]
        member _.Ensure(s: MappingState, name: string, check: Node -> Result<unit, Error list>) =
            { s with Ensure = s.Ensure @ [ name, check ] }

        /// Aggregate: one entry per file of the inputList `input`, set as a list at `into`.
        /// `build` gets the function that qualifies a pointer with the input name.
        [<CustomOperation("each")>]
        member _.Each(s: MappingState, input: string, into: string, build: (string -> string) -> Result<Rule list, Error list>) =
            match Selector.parse into, build (fun p -> input + ":" + p) with
            | Ok sp, Ok entry ->
                add s (Ok(Rule.Collect { Input = input; Into = sp; Entry = entry; SortBy = None }))
            | a, b ->
                let es = (match a with Error e -> [ e ] | Ok _ -> []) @ (match b with Error es -> es | Ok _ -> [])
                { s with Errors = s.Errors @ es }
        /// Order the entries of the most recent `each` by this field.
        [<CustomOperation("sortBy")>]
        member _.SortBy(s: MappingState, path: string) =
            match Selector.parse path, List.rev s.Rules with
            | Ok sp, Rule.Collect c :: before ->
                { s with Rules = List.rev (Rule.Collect { c with SortBy = Some sp } :: before) }
            | Ok _, _ ->
                { s with Errors = s.Errors @ [ Error.create ErrorCodes.mappingInvalid "sortBy must directly follow an each" [] ] }
            | Error e, _ -> { s with Errors = s.Errors @ [ e ] }

    let ops = OpsBuilder()
    let docs = DocsBuilder()
    let files = FilesBuilder()
    let entry = EntryBuilder()
    let mapping = MappingBuilder()
