namespace Fnode

/// 1-based line and column in the source text.
type Pos = { Line: int; Column: int }

type Span = { Start: Pos; End: Pos }

/// A parsed document plus the source spans of its nodes, keyed by printed JSON Pointer.
type Parsed =
    { Node: Node
      Locations: Map<string, Span> }

/// Structural failure (parse, path, type).
type Error =
    { Code: string
      Message: string
      Path: Path
      Location: Span option }

/// Stable error codes.
module ErrorCode =
    [<Literal>]
    let parse = "parse"
    [<Literal>]
    let pathNotFound = "path.notFound"
    [<Literal>]
    let typeMismatch = "type.mismatch"
    [<Literal>]
    let pathInvalid = "path.invalid"

module Error =
    let create (code: string) (message: string) (path: Path) : Error =
        { Code = code; Message = message; Path = path; Location = None }
