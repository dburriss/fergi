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

    // ---- file form ---------------------------------------------------------------------------
    //
    // A mapping as a `Node` (JSON or YAML text through the adapters). A rule is an object with one key
    // naming the rule; arguments are tagged (`{from}`, `{doc}`, `{value}`). See ARCHITECTURE.md.

    /// The file `version` this build writes and the only one it reads.
    let formatVersion = 1L

    let private kindOf (n: Node) =
        match n with
        | Null -> "null"
        | Bool _ -> "a bool"
        | Int _ | Float _ -> "a number"
        | Str _ -> "a string"
        | Timestamp _ -> "a timestamp"
        | Arr _ -> "an array"
        | Obj _ -> "an object"

    let private map2 (f: 'a -> 'b -> 'c) (a: Result<'a, Error list>) (b: Result<'b, Error list>) : Result<'c, Error list> =
        match a, b with
        | Ok x, Ok y -> Ok(f x y)
        | Error e1, Error e2 -> Error(e1 @ e2)
        | Error e, _ -> Error e
        | _, Error e -> Error e

    let private map3 f a b c = map2 (fun g x -> g x) (map2 f a b) c

    let private collect (results: Result<'a, Error list> list) : Result<'a list, Error list> =
        let errs = results |> List.collect (fun r -> match r with Error e -> e | Ok _ -> [])
        if List.isEmpty errs then Ok(results |> List.choose (fun r -> match r with Ok x -> Some x | Error _ -> None))
        else Error errs

    let private errorsOf (r: Result<'a, Error list>) : Error list =
        match r with
        | Error e -> e
        | Ok _ -> []

    let private fileError (path: Path) (msg: string) : Error = Error.create ErrorCodes.mappingInvalid msg path

    // -- reading --

    let private decStr (path: Path) (n: Node) : Result<string, Error list> =
        match n with
        | Str s -> Ok s
        | other -> Error [ fileError path ("Expected a string but found " + kindOf other) ]

    let private decList (dec: Path -> Node -> Result<'a, Error list>) (path: Path) (n: Node) : Result<'a list, Error list> =
        match n with
        | Arr items -> items |> List.mapi (fun i x -> dec (path @ [ Index i ]) x) |> collect
        | other -> Error [ fileError path ("Expected an array but found " + kindOf other) ]

    /// Keep the code and message of a parse error but point it at the place in the file.
    let private at (path: Path) (e: Error) : Error list = [ { e with Path = path } ]

    let private decSelector (path: Path) (n: Node) : Result<SelectorPath, Error list> =
        decStr path n |> Result.bind (fun s -> Selector.parse s |> Result.mapError (at path))

    let private decPointer (path: Path) (n: Node) : Result<Path, Error list> =
        decStr path n |> Result.bind (fun s -> Path.parse s |> Result.mapError (at path))

    /// `input:/pointer`. Whether the input is declared is left to validation, which knows the names.
    let private parseSource (s: string) : Result<string * SelectorPath, Error> =
        let i = s.IndexOf ':'
        if i <= 0 then Error(Error.create ErrorCode.pathInvalid ("Source path needs 'input:/pointer': " + s) [])
        else Selector.parse (s.Substring(i + 1)) |> Result.map (fun p -> s.Substring(0, i), p)

    let private decSource (path: Path) (n: Node) : Result<string * SelectorPath, Error list> =
        decStr path n |> Result.bind (fun s -> parseSource s |> Result.mapError (at path))

    let private decObject (path: Path) (allowed: string list) (n: Node) : Result<(string * Node) list, Error list> =
        match n with
        | Obj kvs ->
            let unknown =
                kvs
                |> List.filter (fun (k, _) -> not (List.contains k allowed))
                |> List.map (fun (k, _) ->
                    fileError (path @ [ Key k ]) ("Unknown field '" + k + "' (expected " + String.concat ", " allowed + ")"))
            if List.isEmpty unknown then Ok kvs else Error unknown
        | other -> Error [ fileError path ("Expected an object but found " + kindOf other) ]

    let private req (path: Path) (kvs: (string * Node) list) (name: string) (dec: Path -> Node -> Result<'a, Error list>) =
        match List.tryFind (fun (k, _) -> k = name) kvs with
        | Some(_, v) -> dec (path @ [ Key name ]) v
        | None -> Error [ fileError path ("Missing field '" + name + "'") ]

    let private opt (path: Path) (kvs: (string * Node) list) (name: string) (dec: Path -> Node -> Result<'a, Error list>) =
        match List.tryFind (fun (k, _) -> k = name) kvs with
        | Some(_, v) -> dec (path @ [ Key name ]) v |> Result.map Some
        | None -> Ok None

    let private decArg (path: Path) (n: Node) : Result<Arg, Error list> =
        match n with
        | Obj [ "from", v ] -> decSource (path @ [ Key "from" ]) v |> Result.map (fun (name, p) -> Arg.Input(name, p))
        | Obj [ "doc", v ] -> decSelector (path @ [ Key "doc" ]) v |> Result.map Arg.Doc
        | Obj [ "value", v ] -> Ok(Arg.Literal v)
        | _ -> Error [ fileError path "An argument is an object with exactly one of 'from', 'doc' or 'value'" ]

    let private ruleNames =
        "layer, listsBy, conflict, nullDeletes, copy, rename, defaultTo, set, drop, collect, compute"

    let rec private decRule (path: Path) (n: Node) : Result<Rule, Error list> =
        match n with
        | Obj [ name, body ] ->
            let p = path @ [ Key name ]
            let fields allowed = decObject p allowed body
            match name with
            | "layer" -> decList decStr p body |> Result.map Rule.Layer
            | "listsBy" ->
                fields [ "path"; "key" ]
                |> Result.bind (fun kvs -> map2 (fun pa k -> Rule.ListsBy(pa, k)) (req p kvs "path" decPointer) (req p kvs "key" decStr))
            | "conflict" ->
                decStr p body
                |> Result.bind (fun s ->
                    match s with
                    | "lastWins" -> Ok(Rule.Conflict ConflictMode.LastWins)
                    | "firstWins" -> Ok(Rule.Conflict ConflictMode.FirstWins)
                    | "fail" -> Ok(Rule.Conflict ConflictMode.Fail)
                    | other -> Error [ fileError p ("Unknown conflict mode '" + other + "' (expected lastWins, firstWins or fail)") ])
            | "nullDeletes" ->
                match body with
                | Bool b -> Ok(Rule.NullDeletes b)
                | other -> Error [ fileError p ("Expected a bool but found " + kindOf other) ]
            | "copy" ->
                fields [ "from"; "to" ]
                |> Result.bind (fun kvs -> map2 (fun src dest -> Rule.Copy(src, dest)) (req p kvs "from" decSource) (req p kvs "to" decSelector))
            | "rename" ->
                fields [ "from"; "to" ]
                |> Result.bind (fun kvs -> map2 (fun a b -> Rule.Rename(a, b)) (req p kvs "from" decSelector) (req p kvs "to" decSelector))
            | "defaultTo" ->
                fields [ "to"; "value" ]
                |> Result.bind (fun kvs -> map2 (fun d v -> Rule.DefaultTo(d, v)) (req p kvs "to" decSelector) (req p kvs "value" (fun _ v -> Ok v)))
            | "set" ->
                fields [ "to"; "value" ]
                |> Result.bind (fun kvs -> map2 (fun d v -> Rule.Set(d, v)) (req p kvs "to" decSelector) (req p kvs "value" (fun _ v -> Ok v)))
            | "drop" ->
                fields [ "path" ] |> Result.bind (fun kvs -> req p kvs "path" decSelector |> Result.map Rule.Drop)
            | "collect" ->
                fields [ "input"; "into"; "entry"; "sortBy" ]
                |> Result.bind (fun kvs ->
                    map3
                        (fun input into (entry, sortBy) -> Rule.Collect { Input = input; Into = into; Entry = entry; SortBy = sortBy })
                        (req p kvs "input" decStr)
                        (req p kvs "into" decSelector)
                        (map2 (fun e s -> e, s) (req p kvs "entry" (decList decRule)) (opt p kvs "sortBy" decSelector)))
            | "compute" ->
                fields [ "to"; "fn"; "args" ]
                |> Result.bind (fun kvs ->
                    map3
                        (fun dest fn args -> Rule.Compute { Fn = fn; Args = Option.defaultValue [] args; Dest = dest })
                        (req p kvs "to" decSelector)
                        (req p kvs "fn" decStr)
                        (opt p kvs "args" (decList decArg)))
            | other -> Error [ fileError p ("Unknown rule '" + other + "' (expected one of " + ruleNames + ")") ]
        | Obj _ -> Error [ fileError path ("A rule is an object with exactly one key, naming the rule (" + ruleNames + ")") ]
        | other -> Error [ fileError path ("A rule is an object, but found " + kindOf other) ]

    let private decVersion (path: Path) (n: Node) : Result<unit, Error list> =
        match n with
        | Int v when v = formatVersion -> Ok()
        | Int v ->
            Error [ Error.create ErrorCodes.mappingVersion ("Unsupported mapping version " + string v + " (this reader understands " + string formatVersion + ")") path ]
        | other -> Error [ fileError path ("'version' must be the integer " + string formatVersion + ", found " + kindOf other) ]

    /// Read a mapping from its file form. Every problem found is reported, each with the `Path` of the
    /// offending place in the file (use `ofParsed` to turn that into a line and column). The result has
    /// the built-in functions only and no `Ensure` hooks; add host functions with `withFunction`. Whether
    /// input and function names are declared is checked by `Fergi.run`, not here.
    let ofNode (node: Node) : Result<Mapping, Error list> =
        match decObject [] [ "$schema"; "version"; "inputs"; "inputLists"; "rules" ] node with
        | Error e -> Error e
        | Ok kvs ->
            let version = req [] kvs "version" decVersion
            let inputs = req [] kvs "inputs" (decList decStr)
            let lists = opt [] kvs "inputLists" (decList decStr)
            let rules = req [] kvs "rules" (decList decRule)
            match version, inputs, lists, rules with
            | Ok _, Ok i, Ok l, Ok r -> Ok(create i r |> withInputLists (Option.defaultValue [] l))
            | _ -> Error(errorsOf version @ errorsOf inputs @ errorsOf lists @ errorsOf rules)

    /// `ofNode` over a parsed file: each error also gets the `Location` of its path, or of the nearest
    /// enclosing place the parser recorded (an unknown key has no span of its own).
    let ofParsed (parsed: Parsed) : Result<Mapping, Error list> =
        let rec locate (path: Path) =
            match Map.tryFind (Path.print path) parsed.Locations with
            | Some span -> Some span
            | None -> if List.isEmpty path then None else locate (List.take (List.length path - 1) path)
        ofNode parsed.Node
        |> Result.mapError (List.map (fun e -> { e with Location = locate e.Path }))

    // -- writing --

    let private cannot (path: Path) (msg: string) : Error list =
        [ Error.create ErrorCodes.mappingNotSerialisable msg path ]

    /// A text that must read back as the same value, or the file would say something else.
    let private text (path: Path) (what: string) (s: string) (readsBack: bool) : Result<Node, Error list> =
        if readsBack then Ok(Str s)
        else Error(cannot path (what + " '" + s + "' does not read back as the same path; change it"))

    let private encSelector (path: Path) (sp: SelectorPath) : Result<Node, Error list> =
        let s = Selector.print sp
        text path "Selector" s (Selector.parse s = Ok sp)

    let private encSource (path: Path) (name: string, sp: SelectorPath) : Result<Node, Error list> =
        let s = name + ":" + Selector.print sp
        text path "Source path" s (parseSource s = Ok(name, sp))

    let private encPointer (path: Path) (p: Path) : Result<Node, Error list> =
        let s = Path.print p
        text path "Pointer" s (Path.parse s = Ok p)

    let private encArg (path: Path) (a: Arg) : Result<Node, Error list> =
        match a with
        | Arg.Input(name, sp) -> encSource (path @ [ Key "from" ]) (name, sp) |> Result.map (fun s -> Obj [ "from", s ])
        | Arg.Doc sp -> encSelector (path @ [ Key "doc" ]) sp |> Result.map (fun s -> Obj [ "doc", s ])
        | Arg.Literal v -> Ok(Obj [ "value", v ])

    /// A function that exists only in the process that registered it. The built-ins and `jsonata` (which
    /// `Fergi.Jsonata` registers, and any runner can) are written by name; an unknown name is written too,
    /// because validation, not the writer, reports it.
    let private isHostFunction (m: Mapping) (name: string) : bool =
        name <> "jsonata"
        && (match Map.tryFind name m.Functions with
            | None -> false
            | Some f ->
                match Map.tryFind name Functions.builtins with
                | Some b -> not (obj.ReferenceEquals(f, b))
                | None -> true)

    let rec private encRule (m: Mapping) (path: Path) (r: Rule) : Result<Node, Error list> =
        let p (name: string) = path @ [ Key name ]
        let into (name: string) (fields: Node list -> Node) (parts: Result<Node, Error list> list) =
            collect parts |> Result.map (fun nodes -> Obj [ name, fields nodes ])
        match r with
        | Rule.Layer names -> Ok(Obj [ "layer", Arr(List.map Str names) ])
        | Rule.ListsBy(pa, key) ->
            encPointer (p "listsBy" @ [ Key "path" ]) pa
            |> Result.map (fun s -> Obj [ "listsBy", Obj [ "path", s; "key", Str key ] ])
        | Rule.Conflict mode ->
            let s =
                match mode with
                | ConflictMode.LastWins -> "lastWins"
                | ConflictMode.FirstWins -> "firstWins"
                | ConflictMode.Fail -> "fail"
            Ok(Obj [ "conflict", Str s ])
        | Rule.NullDeletes b -> Ok(Obj [ "nullDeletes", Bool b ])
        | Rule.Copy(src, dest) ->
            into "copy" (fun ns -> Obj [ "from", ns.[0]; "to", ns.[1] ])
                [ encSource (p "copy" @ [ Key "from" ]) src; encSelector (p "copy" @ [ Key "to" ]) dest ]
        | Rule.Rename(a, b) ->
            into "rename" (fun ns -> Obj [ "from", ns.[0]; "to", ns.[1] ])
                [ encSelector (p "rename" @ [ Key "from" ]) a; encSelector (p "rename" @ [ Key "to" ]) b ]
        | Rule.DefaultTo(dest, v) ->
            encSelector (p "defaultTo" @ [ Key "to" ]) dest |> Result.map (fun s -> Obj [ "defaultTo", Obj [ "to", s; "value", v ] ])
        | Rule.Set(dest, v) ->
            encSelector (p "set" @ [ Key "to" ]) dest |> Result.map (fun s -> Obj [ "set", Obj [ "to", s; "value", v ] ])
        | Rule.Drop sp -> encSelector (p "drop" @ [ Key "path" ]) sp |> Result.map (fun s -> Obj [ "drop", Obj [ "path", s ] ])
        | Rule.Collect c ->
            let entry =
                c.Entry
                |> List.mapi (fun i er -> encRule m (p "collect" @ [ Key "entry"; Index i ]) er)
                |> collect
                |> Result.map Arr
            let sortBy =
                match c.SortBy with
                | None -> Ok None
                | Some sp -> encSelector (p "collect" @ [ Key "sortBy" ]) sp |> Result.map Some
            map3
                (fun intoNode entryNode sortNode ->
                    Obj [ "collect",
                          Obj([ "input", Str c.Input; "into", intoNode; "entry", entryNode ]
                              @ (match sortNode with Some s -> [ "sortBy", s ] | None -> [])) ])
                (encSelector (p "collect" @ [ Key "into" ]) c.Into)
                entry
                sortBy
        | Rule.Compute c ->
            let fn =
                if isHostFunction m c.Fn then
                    Error(
                        cannot (p "compute" @ [ Key "fn" ])
                            ("Function '" + c.Fn + "' was registered with Mapping.withFunction and runs only in the process that registered it. A file can name a built-in or 'jsonata'; use one of those, or run the mapping in-process")
                        |> List.map (Error.withContext "function" c.Fn)
                    )
                else Ok(Str c.Fn)
            let args =
                c.Args
                |> List.mapi (fun i a -> encArg (p "compute" @ [ Key "args"; Index i ]) a)
                |> collect
                |> Result.map Arr
            map3
                (fun destNode fnNode argsNode -> Obj [ "compute", Obj [ "to", destNode; "fn", fnNode; "args", argsNode ] ])
                (encSelector (p "compute" @ [ Key "to" ]) c.Dest)
                fn
                args

    /// Write a mapping in its file form (`ofNode (toNode m)` gives `m` back, apart from `Ensure` and the
    /// function registry, which are not part of it). Fails, listing every offender, when the mapping holds
    /// something a file cannot say: an `Ensure` hook (code; a declarative check rule is the file form and
    /// does not exist yet), a function registered with `withFunction` (it runs only where it was
    /// registered), or a selector or pointer that does not read back as itself. Print the result with an
    /// adapter (JSON by default for generated files).
    let toNode (m: Mapping) : Result<Node, Error list> =
        let hooks =
            m.Ensure
            |> List.map (fun (name, _) ->
                cannot [] ("Ensure hook '" + name + "' is code, so it cannot be written to a file. Check the result in the program that runs the mapping, or use a declarative check rule once one exists")
                |> List.map (Error.withContext "ensure" name))
            |> List.concat
        let rules = m.Rules |> List.mapi (fun i r -> encRule m [ Key "rules"; Index i ] r) |> collect
        match hooks, rules with
        | [], Ok rs ->
            Ok(
                Obj(
                    [ "version", Int formatVersion; "inputs", Arr(List.map Str m.Inputs) ]
                    @ (if List.isEmpty m.InputLists then [] else [ "inputLists", Arr(List.map Str m.InputLists) ])
                    @ [ "rules", Arr rs ]
                )
            )
        | _ -> Error(hooks @ errorsOf rules)
