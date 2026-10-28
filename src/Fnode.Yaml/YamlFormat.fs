/// The YAML `Format` adapter.
module Fnode.Yaml

open Flexi.Yaml
open Fnode

let private firstLine (s: string) =
    match s.IndexOf '\n' with
    | -1 -> s
    | i -> s.Substring(0, i)

let private pos (p: Flexi.Yaml.Pos) : Fnode.Pos = { Line = p.Line; Column = p.Column }

let private span (s: Flexi.Yaml.Span) : Fnode.Span = { Start = pos s.Start; End = pos s.End }

let private ofException (ex: exn) : Error =
    match ex with
    | :? YamlParseException as p ->
        let pos = { Line = p.Line; Column = p.Column }
        { Error.create ErrorCode.parse (firstLine p.Message) [] with
            Location = Some { Start = pos; End = pos } }
    | _ -> Error.create ErrorCode.parse (firstLine ex.Message) []

let private ofParseError (e: YamlParseError) : Error =
    let p = pos e.Pos
    { Error.create ErrorCode.parse (firstLine e.Message) [] with
        Location = Some { Start = p; End = p } }

let private toParsed (y: YamlParsed) : Result<Parsed, Error list> =
    match YamlConvert.toNodeWith y.TimestampText y.Root with
    | Ok n -> Ok { Node = n; Locations = y.Spans |> Map.map (fun _ s -> span s) }
    | Error e -> Error [ e ]

/// Parses exactly one document. Never throws: parse failures, multi-document input, and
/// non-string keys come back as an `Error list`. An empty document is `Null`. `Locations` is
/// keyed by JSON Pointer over the expanded tree (aliases and merge keys are positioned at their
/// site); Timestamp nodes keep their original source text.
let parse (text: string) : Result<Parsed, Error list> =
    try
        match YamlValue.TryParseWithMetadata text with
        | Ok y -> toParsed y
        | Error e -> Error [ ofParseError e ]
    with ex ->
        Error [ ofException ex ]

/// Parses a multi-document stream (`---` separated). Never throws. Each document's locations
/// are relative to the whole stream.
let parseAll (text: string) : Result<Parsed list, Error list> =
    try
        let docs = YamlValue.ParseMultipleWithMetadata text |> List.ofSeq
        let results = docs |> List.map toParsed
        let errors = results |> List.collect (function Error es -> es | Ok _ -> [])
        if List.isEmpty errors then
            Ok(results |> List.choose (function Ok p -> Some p | Error _ -> None))
        else
            Error errors
    with ex ->
        Error [ ofException ex ]

/// Prints a node as block-style YAML, 2-space indent, trailing newline. A Timestamp whose text
/// is itself a plain YAML timestamp (`2026-10-14`, `2026-10-14 01:02:03 +01:00`, zoneless, ...)
/// is written verbatim and unquoted, so it re-parses as a Timestamp with the same text. Other
/// Timestamp text is normalised through DateTimeOffset, or printed as a string if unparseable.
let print (node: Node) : string =
    let rec strings (n: Node) : string seq =
        match n with
        | Str s -> Seq.singleton s
        | Arr items -> Seq.collect strings items
        | Obj props -> props |> Seq.collect (fun (k, v) -> Seq.append (Seq.singleton k) (strings v))
        | _ -> Seq.empty
    let all = strings node |> Seq.toList
    // The emitter cannot write a Timestamp's source text, so those nodes are emitted as a unique
    // plain-scalar marker that is swapped for the text afterwards.
    let mutable prefix = "FnodeTimestampMarker"
    while all |> List.exists (fun s -> s.Contains prefix) do
        prefix <- prefix + "X"
    let texts = System.Collections.Generic.List<string>()
    let rec swap (n: Node) : Node =
        match n with
        | Timestamp t when YamlConvert.isYamlTimestampText t ->
            texts.Add t
            Str(prefix + "N" + string (texts.Count - 1) + "N")
        | Arr items -> Arr(List.map swap items)
        | Obj props -> Obj(props |> List.map (fun (k, v) -> k, swap v))
        | other -> other
    let swapped = swap node
    let out = (YamlConvert.ofNode swapped).ToString(YamlSaveOptions.None, 2)
    if texts.Count = 0 then out
    else
        System.Text.RegularExpressions.Regex.Replace(
            out,
            prefix + "N(\\d+)N",
            fun m -> texts.[int m.Groups.[1].Value])

let format: Format =
    { Name = "yaml"
      Extensions = [ ".yaml"; ".yml" ]
      Parse = parse
      Print = print }
