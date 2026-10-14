namespace Fnode

/// The in-memory document tree shared by every format adapter.
/// Objects are lists of pairs so key order is preserved.
///
/// - Adapters may produce duplicate keys in an `Obj` (YAML can). Lookups act on the first match;
///   see `Node.hasDuplicateKeys`.
/// - Structural `=` is not NaN-safe (`Float nan <> Float nan`). Compare documents with `Node.equals`.
type Node =
    | Null
    | Bool of bool
    | Int of int64
    | Float of float
    | Str of string
    /// ISO 8601 text, kept as text so no information is lost to DateTimeOffset.
    | Timestamp of string
    | Arr of Node list
    | Obj of (string * Node) list

/// One step in a path: an object key or an array index.
type Segment =
    | Key of string
    | Index of int

/// A location in a document, printed and parsed as a JSON Pointer (RFC 6901).
type Path = Segment list
