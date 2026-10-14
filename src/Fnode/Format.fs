namespace Fnode

/// The record every format adapter exposes. Text in, text out; no file IO.
type Format =
    { Name: string
      Extensions: string list
      Parse: string -> Result<Parsed, Error list>
      Print: Node -> string }
