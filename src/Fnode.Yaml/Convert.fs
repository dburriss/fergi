/// Conversion between YamlValue and Fnode.Node, using only YamlValue's public API.
module Fnode.YamlConvert

open System
open System.Globalization
open Flexi.Yaml
open Fnode

/// Decimal scale (number of digits after the point; 0 for integers).
let private scale (d: decimal) : int = (Decimal.GetBits d).[3] >>> 16 &&& 0xFF

let private int64Min = decimal Int64.MinValue
let private int64Max = decimal Int64.MaxValue

/// Plain integers in int64 range become Int. Anything with a fractional part (including `1.0`,
/// which YamlValue keeps as a decimal with scale 1) or outside int64 becomes Float.
let private number (d: decimal) : Node =
    if scale d = 0 && d >= int64Min && d <= int64Max then Int(int64 d) else Float(float d)

let private pathOf (rev: Segment list) : Path = List.rev rev

let private timestampFallback (t: DateTimeOffset) : string = t.ToString("o", CultureInfo.InvariantCulture)

/// YamlValue -> Node. `stamps` maps a node's RFC 6901 pointer (as printed by `Path.print`) to the
/// original source text of a timestamp (`YamlParsed.TimestampText`); a Timestamp with no entry
/// falls back to "o"-format text. Fails with `type.mismatch` on a non-string mapping key, since
/// Fnode objects have string keys only.
let toNodeWith (stamps: Map<string, string>) (value: YamlValue) : Result<Node, Error> =
    let rec go (rev: Segment list) (v: YamlValue) : Result<Node, Error> =
        match v with
        | YamlValue.Null -> Ok Null
        | YamlValue.Boolean b -> Ok(Bool b)
        | YamlValue.String s -> Ok(Str s)
        | YamlValue.Number d -> Ok(number d)
        | YamlValue.Float f -> Ok(Float f)
        | YamlValue.Timestamp t ->
            match Map.tryFind (Path.print (pathOf rev)) stamps with
            | Some text -> Ok(Timestamp text)
            | None -> Ok(Timestamp(timestampFallback t))
        | YamlValue.Sequence items ->
            let mutable acc = []
            let mutable err = None
            let mutable i = 0
            while err.IsNone && i < items.Length do
                match go (Index i :: rev) items.[i] with
                | Ok n -> acc <- n :: acc
                | Error e -> err <- Some e
                i <- i + 1
            match err with
            | Some e -> Error e
            | None -> Ok(Arr(List.rev acc))
        | YamlValue.Mapping props ->
            let mutable acc = []
            let mutable err = None
            let mutable i = 0
            while err.IsNone && i < props.Length do
                match props.[i] with
                | YamlValue.String k, child ->
                    match go (Key k :: rev) child with
                    | Ok n -> acc <- (k, n) :: acc
                    | Error e -> err <- Some e
                | key, _ ->
                    err <-
                        Some(
                            Error.create
                                ErrorCode.typeMismatch
                                ("Mapping key must be a string, found " + key.ToString(YamlSaveOptions.DisableFormatting).Trim())
                                (pathOf rev)
                        )
                i <- i + 1
            match err with
            | Some e -> Error e
            | None -> Ok(Obj(List.rev acc))
    go [] value

/// `toNodeWith` without timestamp source text: Timestamps render in "o" format.
let toNode (value: YamlValue) : Result<Node, Error> = toNodeWith Map.empty value

/// True when `text` is, verbatim, a plain YAML timestamp scalar (so it can be written unquoted
/// and re-parses as a Timestamp).
let isYamlTimestampText (text: string) : bool =
    text.Length > 0
    && text = text.Trim()
    && text.IndexOfAny [| '\n'; '\r' |] < 0
    && (try
            match YamlValue.Parse text with
            | YamlValue.Timestamp _ -> true
            | _ -> false
        with _ -> false)

/// Node -> YamlValue. Total. A YamlValue Timestamp cannot carry source text, so a Timestamp node
/// goes through DateTimeOffset here (a date-only text becomes midnight UTC in "o"-style output);
/// `Fnode.Yaml.print` preserves the text itself. Integral floats print as `1.0` and re-parse as a Float, not an Int.
let ofNode (node: Node) : YamlValue =
    let rec go (n: Node) : YamlValue =
        match n with
        | Null -> YamlValue.Null
        | Bool b -> YamlValue.Boolean b
        | Int i -> YamlValue.Number(decimal i)
        | Float f -> YamlValue.Float f
        | Str s -> YamlValue.String s
        | Timestamp s ->
#if FABLE_COMPILER
            // Fable's DateTimeOffset.TryParse ignores culture and styles; accept the shapes YamlValue itself
            // resolves as timestamps instead.
            match (if isYamlTimestampText s then YamlValue.Parse s else YamlValue.String s) with
            | YamlValue.Timestamp t -> YamlValue.Timestamp t
            | _ ->
                // Not a parseable timestamp: keep the text (the emitter quotes it where needed).
                YamlValue.String s
#else
            match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, t -> YamlValue.Timestamp t
            // Not a parseable timestamp: keep the text (the emitter quotes it where needed).
            | false, _ -> YamlValue.String s
#endif
        | Arr items -> YamlValue.Sequence(items |> List.map go |> Array.ofList)
        | Obj props ->
            YamlValue.Mapping(props |> List.map (fun (k, v) -> YamlValue.String k, go v) |> Array.ofList)
    go node
