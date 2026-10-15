namespace Fergi

open Fnode

/// A pure value function a mapping can call by name (`Rule.Compute`). It gets the evaluated arguments and
/// returns the new value, or a message when the arguments are unusable. No IO, no exceptions: the message
/// becomes a `compute.failed` error that carries the function name, rule index and destination.
type Function = Node list -> Result<Node, string>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Functions =

    let private kind (n: Node) =
        match n with
        | Null -> "null"
        | Bool _ -> "a bool"
        | Int _ -> "an int"
        | Float _ -> "a float"
        | Str _ -> "a string"
        | Timestamp _ -> "a timestamp"
        | Arr _ -> "an array"
        | Obj _ -> "an object"

    /// Text of a scalar that prints the same on every runtime. Floats are left out on purpose: their text
    /// differs per Fable target.
    let private text (n: Node) : Result<string, string> =
        match n with
        | Str s | Timestamp s -> Ok s
        | Int i -> Ok(string i)
        | Bool b -> Ok(if b then "true" else "false")
        | other -> Error("expected a string, int or bool but got " + kind other)

    let private texts (args: Node list) : Result<string list, string> =
        args
        |> List.fold
            (fun acc a ->
                match acc, text a with
                | Ok xs, Ok x -> Ok(xs @ [ x ])
                | Error e, _ -> Error e
                | _, Error e -> Error e)
            (Ok [])

    let private str1 (name: string) (f: string -> Node) : Function =
        fun args ->
            match args with
            | [ Str s ] -> Ok(f s)
            | [ other ] -> Error(name + " expects a string but got " + kind other)
            | _ -> Error(name + " expects exactly 1 argument but got " + string (List.length args))

    let private concat : Function = fun args -> texts args |> Result.map (fun xs -> Str(String.concat "" xs))

    let private join : Function =
        fun args ->
            match args with
            | Str sep :: rest -> texts rest |> Result.map (fun xs -> Str(String.concat sep xs))
            | _ -> Error "join expects a separator string followed by the values to join"

    let private toInt : Function =
        fun args ->
            match args with
            | [ Int i ] -> Ok(Int i)
            | [ Str s ] ->
                let t = s.Trim()
                let digits = if t.StartsWith "-" || t.StartsWith "+" then t.Substring 1 else t
                if digits <> "" && digits |> Seq.forall (fun c -> c >= '0' && c <= '9') then
                    match System.Int64.TryParse t with
                    | true, i -> Ok(Int i)
                    | _ -> Error("toInt: '" + s + "' is out of range")
                else Error("toInt: '" + s + "' is not an integer")
            | [ other ] -> Error("toInt expects a string or int but got " + kind other)
            | _ -> Error("toInt expects exactly 1 argument but got " + string (List.length args))

    let private toString : Function =
        fun args ->
            match args with
            | [ one ] -> text one |> Result.map Str
            | _ -> Error("toString expects exactly 1 argument but got " + string (List.length args))

    /// The first argument that is not `null`; `null` when all are.
    let private coalesce : Function =
        fun args -> Ok(args |> List.tryFind (fun a -> a <> Null) |> Option.defaultValue Null)

    /// The built-in functions: `concat`, `join`, `upper`, `lower`, `trim`, `toInt`, `toString`, `coalesce`.
    ///
    /// - `concat a b ...`: the arguments (strings, ints, bools) as one string.
    /// - `join sep a b ...`: the values joined by the separator string.
    /// - `upper s`, `lower s`, `trim s`: invariant-culture case change and whitespace trim of a string.
    /// - `toInt x`: a string of digits (optional sign) or an int, as an int.
    /// - `toString x`: a string, int or bool as a string.
    /// - `coalesce a b ...`: the first argument that is not null.
    let builtins : Map<string, Function> =
        Map.ofList
            [ "concat", concat
              "join", join
              "upper", str1 "upper" (fun s -> Str(s.ToUpperInvariant()))
              "lower", str1 "lower" (fun s -> Str(s.ToLowerInvariant()))
              "trim", str1 "trim" (fun s -> Str(s.Trim()))
              "toInt", toInt
              "toString", toString
              "coalesce", coalesce ]
