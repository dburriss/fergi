namespace Fergi

open Fnode

/// One step of a selector: a plain `Segment`, or a `Where` that picks the array item whose
/// string field `key` equals `value`.
type Selector =
    | Seg of Segment
    | Where of key: string * value: string

/// A path that may contain `Where` steps. Resolve it against a document to get a `Path`.
type SelectorPath = Selector list

/// Selector parsing, printing and resolution.
///
/// String form: JSON Pointer (RFC 6901) where a segment written `[name=value]` is a `Where`,
/// e.g. `/ports/[name=http]/port`. The key is the text before the first `=`, the value is
/// everything after it (so values may contain `=`). Key and value use the same `~0`/`~1`
/// escapes as a pointer segment.
///
/// Known limits: an object key that itself looks like `[a=b]` is not addressable (it parses
/// as `Where`), and a `Where` key containing `=` does not round-trip through print/parse.
/// `Where` matches string values only.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Selector =

    let private invalid (msg: string) : Error = Error.create ErrorCode.pathInvalid msg []

    let private isWhere (part: string) =
        part.Length >= 3 && part.[0] = '[' && part.[part.Length - 1] = ']' && part.IndexOf '=' > 0

    let private parsePart (part: string) : Result<Selector, Error> =
        if isWhere part then
            let inner = part.Substring(1, part.Length - 2)
            let eq = inner.IndexOf '='
            match Path.unescapeSegment (inner.Substring(0, eq)), Path.unescapeSegment (inner.Substring(eq + 1)) with
            | Some k, Some v when k <> "" -> Ok(Where(k, v))
            | _ -> Error(invalid ("Invalid selector segment: " + part))
        else
            match Path.parse ("/" + part) with
            | Ok [ seg ] -> Ok(Seg seg)
            | Ok _ -> Error(invalid ("Invalid selector segment: " + part))
            | Error e -> Error e

    /// Parse a selector string. The empty string is the root.
    let parse (s: string) : Result<SelectorPath, Error> =
        if s = "" then Ok []
        elif s.[0] <> '/' then
            Error(invalid ("Selector must be empty or start with '/': " + s))
        else
            let rec go acc parts =
                match parts with
                | [] -> Ok(List.rev acc)
                | p :: rest ->
                    match parsePart p with
                    | Error e -> Error e
                    | Ok sel -> go (sel :: acc) rest
            go [] (s.Substring(1).Split('/') |> List.ofArray)

    let printSelector (sel: Selector) : string =
        match sel with
        | Seg seg -> Path.printSegment seg
        | Where(k, v) -> "[" + Path.escapeSegment k + "=" + Path.escapeSegment v + "]"

    /// Print as a selector string. Root is "".
    let print (path: SelectorPath) : string =
        path |> List.map (fun s -> "/" + printSelector s) |> String.concat ""

    /// Lift a plain path into a selector path.
    let ofPath (path: Path) : SelectorPath = List.map Seg path

    let private describe (n: Node) =
        match n with
        | Obj _ -> "object"
        | Arr _ -> "array"
        | _ -> "scalar"

    /// Turn a `SelectorPath` into a `Path` against `doc`.
    ///
    /// `Seg` steps are not validated: a missing key or a type mismatch is left to the
    /// operation that consumes the path (so `Doc.set` can still create a new final key). A
    /// `Where` needs a real array to look at: a non-array is `type.mismatch`, no matching item
    /// (or a missing array) is `selector.noMatch`, several matching items is
    /// `selector.ambiguous`. Items that are not objects, lack the key, or hold a non-string
    /// value under it simply do not match. Error paths are the resolved prefix.
    let resolve (path: SelectorPath) (doc: Node) : Result<Path, Error> =
        // `cur` is the node at `trail` when known; None once a Seg stepped off the document.
        let rec go (trail: Segment list) (cur: Node option) (rest: SelectorPath) =
            match rest with
            | [] -> Ok(List.rev trail)
            | Seg seg :: tl ->
                let next =
                    match cur with
                    | Some n -> Node.tryGet [ seg ] n |> Result.toOption
                    | None -> None
                go (seg :: trail) next tl
            | Where(k, v) :: tl ->
                let text = "[" + Path.escapeSegment k + "=" + Path.escapeSegment v + "]"
                match cur with
                | None ->
                    Error(Error.create ErrorCodes.selectorNoMatch ("Cannot resolve " + text + ": parent does not exist") (List.rev trail))
                | Some(Arr items) ->
                    let hits =
                        items
                        |> List.indexed
                        |> List.filter (fun (_, item) ->
                            match item with
                            | Obj kvs -> kvs |> List.exists (fun (k2, x) -> k2 = k && x = Str v)
                            | _ -> false)
                    match hits with
                    | [ (i, item) ] -> go (Index i :: trail) (Some item) tl
                    | [] ->
                        Error(Error.create ErrorCodes.selectorNoMatch ("No item matches " + text) (List.rev trail))
                    | _ ->
                        Error(Error.create ErrorCodes.selectorAmbiguous ("Several items match " + text) (List.rev trail))
                | Some other ->
                    Error(
                        Error.create ErrorCode.typeMismatch
                            ("Expected array for " + text + " but found " + describe other)
                            (List.rev trail)
                    )
        go [] (Some doc) path
