namespace Fnode

/// JSON Pointer (RFC 6901) parsing and printing.
///
/// Ambiguity: a segment made only of digits (no leading zero, at most 9 digits) parses as
/// `Index`; everything else (including "01") parses as `Key`. A numeric object key therefore
/// cannot be expressed through `Path.parse` alone, and `print (Key "5")` re-parses as `Index 5`.
/// Resolving that is the consumer's concern.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Path =

    /// Escape `~` and `/` for use inside a pointer segment.
    let escapeSegment (s: string) : string =
        s.Replace("~", "~0").Replace("/", "~1")

    /// Reverse `escapeSegment`. Returns None when `s` contains a `~` not followed by 0 or 1.
    let unescapeSegment (s: string) : string option =
        if s.IndexOf '~' < 0 then Some s
        else
            let sb = System.Text.StringBuilder()
            let mutable ok = true
            let mutable i = 0
            while ok && i < s.Length do
                let c = s.[i]
                if c = '~' then
                    if i + 1 < s.Length && s.[i + 1] = '0' then
                        sb.Append('~') |> ignore
                        i <- i + 2
                    elif i + 1 < s.Length && s.[i + 1] = '1' then
                        sb.Append('/') |> ignore
                        i <- i + 2
                    else ok <- false
                else
                    sb.Append(c) |> ignore
                    i <- i + 1
            if ok then Some(sb.ToString()) else None

    let private isIndexText (s: string) =
        s.Length > 0
        && s.Length <= 9
        && (s = "0" || s.[0] <> '0')
        && (let mutable all = true
            for c in s do
                if c < '0' || c > '9' then all <- false
            all)

    let private toIndex (s: string) =
        let mutable n = 0
        for c in s do
            n <- n * 10 + (int c - int '0')
        n

    let private invalid (msg: string) : Error = Error.create ErrorCode.pathInvalid msg []

    /// Parse one (still escaped) pointer segment: all-digit text becomes `Index`, anything else `Key`.
    let parseSegment (text: string) : Result<Segment, Error> =
        match unescapeSegment text with
        | None -> Error(invalid ("Invalid escape in JSON Pointer segment: " + text))
        | Some k -> Ok(if isIndexText text then Index(toIndex text) else Key k)

    /// Parse a JSON Pointer. The empty string is the root.
    let parse (pointer: string) : Result<Path, Error> =
        if pointer = "" then Ok []
        elif pointer.[0] <> '/' then
            Error(invalid ("JSON Pointer must be empty or start with '/': " + pointer))
        else
            let parts = pointer.Substring(1).Split('/') |> List.ofArray
            let rec go acc parts =
                match parts with
                | [] -> Ok(List.rev acc)
                | (p: string) :: rest ->
                    match parseSegment p with
                    | Error e -> Error e
                    | Ok seg -> go (seg :: acc) rest
            go [] parts

    let printSegment (seg: Segment) : string =
        match seg with
        | Key k -> escapeSegment k
        | Index i -> string i

    /// Normalise `path` against `node`: a `Key` made of index text becomes `Index` where the node
    /// is an array, and an `Index` becomes `Key` where the node is an object. Resolution stops
    /// (rest left as is) at a missing step or a scalar. Settles the numeric-key ambiguity.
    let resolve (node: Node) (path: Path) : Path =
        let rec go (node: Node option) path =
            match path with
            | [] -> []
            | seg :: rest ->
                let seg', child =
                    match seg, node with
                    | Key k, Some(Arr items) when isIndexText k ->
                        let i = toIndex k
                        Index i, List.tryItem i items
                    | Index i, Some(Obj kvs) ->
                        let k = string i
                        Key k, List.tryFind (fun (k2, _) -> k2 = k) kvs |> Option.map snd
                    | Key k, Some(Obj kvs) -> seg, List.tryFind (fun (k2, _) -> k2 = k) kvs |> Option.map snd
                    | Index i, Some(Arr items) -> seg, List.tryItem i items
                    | _ -> seg, None
                seg' :: go child rest
        go (Some node) path

    /// Print as a JSON Pointer. Root is "".
    let print (path: Path) : string =
        let sb = System.Text.StringBuilder()
        for seg in path do
            sb.Append('/').Append(printSegment seg) |> ignore
        sb.ToString()
