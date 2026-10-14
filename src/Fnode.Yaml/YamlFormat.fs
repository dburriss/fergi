/// The YAML `Format` adapter.
module Fnode.Yaml

open FSharp.Data
open Fnode

let private firstLine (s: string) =
    match s.IndexOf '\n' with
    | -1 -> s
    | i -> s.Substring(0, i)

let private ofException (ex: exn) : Error =
    match ex with
    | :? YamlParseException as p ->
        let pos = { Line = p.Line; Column = p.Column }
        { Error.create ErrorCode.parse (firstLine p.Message) [] with
            Location = Some { Start = pos; End = pos } }
    | _ -> Error.create ErrorCode.parse (firstLine ex.Message) []

let private toParsed (v: YamlValue) : Result<Parsed, Error list> =
    match YamlConvert.toNode v with
    | Ok n -> Ok { Node = n; Locations = Map.empty }
    | Error e -> Error [ e ]

/// Parses exactly one document. Never throws: parse failures, multi-document input, and
/// non-string keys come back as an `Error list`. An empty document is `Null`.
let parse (text: string) : Result<Parsed, Error list> =
    try
        match YamlValue.Parse text with
        | v -> toParsed v
    with ex ->
        Error [ ofException ex ]

/// Parses a multi-document stream (`---` separated). Never throws.
let parseAll (text: string) : Result<Parsed list, Error list> =
    try
        let docs = YamlValue.ParseMultiple text |> List.ofSeq
        let results = docs |> List.map toParsed
        let errors = results |> List.collect (function Error es -> es | Ok _ -> [])
        if List.isEmpty errors then
            Ok(results |> List.choose (function Ok p -> Some p | Error _ -> None))
        else
            Error errors
    with ex ->
        Error [ ofException ex ]

/// Prints a node as block-style YAML, 2-space indent, trailing newline.
let print (node: Node) : string =
    (YamlConvert.ofNode node).ToString(YamlSaveOptions.None, 2)

let format: Format =
    { Name = "yaml"
      Extensions = [ ".yaml"; ".yml" ]
      Parse = parse
      Print = print }
