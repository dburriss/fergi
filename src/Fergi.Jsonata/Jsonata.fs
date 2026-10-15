namespace Fergi.Jsonata

open System.Collections.Generic
open Fnode
open Fergi
open Jsonata.Net.Native
open Jsonata.Net.Native.Json

/// A result `toNode` cannot represent (for example a function); `eval` turns it into an `Error`.
exception UnsupportedResult of string

/// JSONata expressions as Fergi functions. .NET only (wraps Jsonata.Net.Native), so it lives outside the
/// Fable-safe core and plugs in through `Mapping.Functions`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Jsonata =

    /// `Node` to the library's JSON DOM. Ints stay `long`, so int64 survives; timestamps become strings.
    let rec ofNode (n: Node) : JToken =
        match n with
        | Null -> JValue.CreateNull()
        | Bool b -> JValue(b) :> JToken
        | Int i -> JValue(i) :> JToken
        | Float f -> JValue(f) :> JToken
        | Str s | Timestamp s -> JValue(s) :> JToken
        | Arr xs ->
            let a = JArray()
            for x in xs do
                a.Add(ofNode x)
            a :> JToken
        | Obj kvs ->
            let o = JObject()
            for k, v in kvs do
                o.Set(k, ofNode v)
            o :> JToken

    /// The library's JSON DOM to `Node`; `None` when the result is JSONata's "undefined" (no match).
    let rec toNode (t: JToken) : Node option =
        match t.Type with
        | JTokenType.Undefined -> None
        | JTokenType.Null -> Some Null
        | JTokenType.Boolean -> Some(Bool(JToken.op_Explicit t : bool))
        | JTokenType.Integer -> Some(Int(JToken.op_Explicit t : int64))
        | JTokenType.Float -> Some(Float(JToken.op_Explicit t : float))
        | JTokenType.String -> Some(Str(JToken.op_Explicit t : string))
        | JTokenType.Array -> Some(Arr((t :?> JArray).ChildrenTokens |> Seq.choose toNode |> List.ofSeq))
        | JTokenType.Object ->
            let o = t :?> JObject
            Some(Obj(o.Properties |> Seq.choose (fun kv -> toNode kv.Value |> Option.map (fun n -> kv.Key, n)) |> List.ofSeq))
        | JTokenType.Function -> raise (UnsupportedResult "the expression returned a function, not a value")
        | other -> raise (UnsupportedResult(sprintf "the expression returned an unsupported result (%A)" other))

    /// Evaluate `expr` with `context` as `$`. An expression that matches nothing is an error, like a
    /// missing source in `Copy`, so a typo in a path does not silently produce nothing.
    let eval (expr: string) (context: Node) (vars: (string * Node) list) : Result<Node, string> =
        try
            let q = JsonataQuery(expr)
            let env = EvaluationEnvironment()
            for k, v in vars do
                env.BindValue(k, ofNode v)
            match toNode (q.Eval(ofNode context, env)) with
            | Some n -> Ok n
            | None -> Error "the expression produced no value (undefined)"
        with
        | :? JsonataException as e -> Error e.Message
        | UnsupportedResult msg -> Error msg
        | e -> Error(e.GetType().Name + ": " + e.Message)

    /// The registered function: `jsonata <expression> <context> [more...]`. The expression is a literal string,
    /// the second argument is the context (`$`), and every argument after the expression is also available as
    /// the array `$args`.
    let jsonata : Function =
        fun args ->
            match args with
            | Str expr :: rest ->
                let context = match rest with c :: _ -> c | [] -> Null
                eval expr context [ "args", Arr rest ]
            | _ -> Error "jsonata expects the expression (a string) as its first argument"

    /// Make `jsonata` callable from the mapping's `Compute` rules.
    let register (m: Mapping) : Mapping = Mapping.withFunction "jsonata" jsonata m

    /// `Mapping.validate` plus the syntax of every `jsonata` rule whose expression is a literal string (also
    /// inside `Collect` entries). A bad expression is a `compute.failed` error carrying the library's message
    /// (which names the position) and the context `function`, `rule` (index) and `expression`. An expression
    /// that is not a literal is not checked.
    let validate (m: Mapping) : Error list =
        let syntax (expr: string) : string option =
            try
                JsonataQuery(expr) |> ignore
                None
            with e -> Some e.Message
        let rec check (prefix: string) (rules: Rule list) : Error list =
            rules
            |> List.mapi (fun i r ->
                let at = prefix + string i
                match r with
                | Rule.Compute { Fn = "jsonata"; Args = Arg.Literal(Str expr) :: _ } ->
                    match syntax expr with
                    | Some msg ->
                        [ Error.create ErrorCodes.computeFailed ("Invalid JSONata expression: " + msg) []
                          |> Error.withContext "function" "jsonata"
                          |> Error.withContext "rule" at
                          |> Error.withContext "expression" expr ]
                    | None -> []
                | Rule.Collect c -> check (at + ".") c.Entry
                | _ -> [])
            |> List.concat
        Mapping.validate m @ check "" m.Rules
