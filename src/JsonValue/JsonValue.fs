/// Untyped JSON document API: a discriminated union you can pattern match, a strict RFC 8259
/// parser (with line/column errors and optional source positions), and a compact / pretty printer.
/// Everything here is Fable-safe: no reflection, no printf, no regex, no dependencies.
namespace Fergi.Json

open System
open System.Globalization
open System.Text

/// A position in the source text. `Line` and `Column` are 1-based (column counts UTF-16 code
/// units from the start of the line); `Offset` is the 0-based UTF-16 index into the source string.
type Pos = { Line: int; Column: int; Offset: int }

/// A source range. `Start` is the first character of the node, `End` is the position just after
/// its last character (exclusive).
type Span = { Start: Pos; End: Pos }

/// A parse failure: a message and where it happened.
type JsonParseError = { Message: string; Pos: Pos }

/// Raised by `Parse` / `ParseWithPositions` on malformed input.
type JsonParseException(error: JsonParseError) =
    inherit Exception(
        error.Message
        + " (line " + string error.Pos.Line
        + ", column " + string error.Pos.Column + ")"
    )
    /// The structured error.
    member _.Error: JsonParseError = error
    member _.Line: int = error.Pos.Line
    member _.Column: int = error.Pos.Column
    member _.Offset: int = error.Pos.Offset

/// An immutable JSON value. Integers that fit in `int64` stay `Integer`; numbers with a fraction
/// or exponent (or too large for `int64`) are `Float`. `Record` keeps key order and duplicates.
[<RequireQualifiedAccess>]
type JsonValue =
    | Null
    | Boolean of bool
    | Integer of int64
    | Float of float
    | String of string
    | Array of JsonValue[]
    | Record of (string * JsonValue)[]

    /// Default maximum container nesting depth accepted by the parser.
    static member DefaultMaxDepth = 512

    /// Parses strict RFC 8259 JSON. Raises `JsonParseException` (with line and column) on error.
    static member Parse(text: string, ?maxDepth: int) : JsonValue =
        let p = JsonParser(text, defaultArg maxDepth JsonValue.DefaultMaxDepth, false)
        p.ParseDocument()

    /// Like `Parse` but returns `Error` instead of raising.
    static member TryParseResult(text: string, ?maxDepth: int) : Result<JsonValue, JsonParseError> =
        try
            Ok(JsonValue.Parse(text, defaultArg maxDepth JsonValue.DefaultMaxDepth))
        with :? JsonParseException as e ->
            Error e.Error

    /// Like `Parse` but returns `None` on malformed input.
    static member TryParse(text: string, ?maxDepth: int) : JsonValue option =
        match JsonValue.TryParseResult(text, defaultArg maxDepth JsonValue.DefaultMaxDepth) with
        | Ok v -> Some v
        | Error _ -> None

    /// Parses and also returns the source `Span` of every node, keyed by its JSON Pointer
    /// (RFC 6901): `""` for the root, `/items/0/name` for nested nodes, with `~0`/`~1` escapes.
    /// For duplicate object keys the first occurrence owns the pointer.
    static member ParseWithPositions(text: string, ?maxDepth: int) : JsonValue * Map<string, Span> =
        let p = JsonParser(text, defaultArg maxDepth JsonValue.DefaultMaxDepth, true)
        let v = p.ParseDocument()
        v, p.Spans()

    /// Like `ParseWithPositions` but returns `Error` instead of raising.
    static member TryParseWithPositions(text: string, ?maxDepth: int)
        : Result<JsonValue * Map<string, Span>, JsonParseError> =
        try
            Ok(JsonValue.ParseWithPositions(text, defaultArg maxDepth JsonValue.DefaultMaxDepth))
        with :? JsonParseException as e ->
            Error e.Error

    /// Compact JSON text.
    override this.ToString() : string = JsonPrinter.Print(this, false)

    /// `ToString(true)` is 2-space pretty printed; `ToString(false)` is compact.
    member this.ToString(indent: bool) : string = JsonPrinter.Print(this, indent)

and [<Sealed>] internal JsonParser(s: string, maxDepth: int, track: bool) =
    let n = s.Length
    let mutable i = 0
    let mutable line = 1
    let mutable lineStart = 0
    let spans = ResizeArray<string * Span>()
    let sb = StringBuilder()

    let posNow () : Pos = { Line = line; Column = i - lineStart + 1; Offset = i }

    let describe (c: char) : string =
        if c < ' ' || c > '~' then "U+" + (int c).ToString("X4") else "'" + string c + "'"

    let err (msg: string) : exn = JsonParseException({ Message = msg; Pos = posNow () }) :> exn

    let skipWs () =
        let mutable go = true
        while go && i < n do
            match s.[i] with
            | ' ' | '\t' | '\r' -> i <- i + 1
            | '\n' ->
                i <- i + 1
                line <- line + 1
                lineStart <- i
            | _ -> go <- false

    let isDigit (c: char) = c >= '0' && c <= '9'

    let hexVal (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    let readHex4 () : int =
        if i + 4 > n then raise (err "incomplete \\u escape")
        let mutable v = 0
        for k in 0 .. 3 do
            let h = hexVal s.[i + k]
            if h < 0 then
                i <- i + k
                raise (err "invalid hex digit in \\u escape")
            v <- v * 16 + h
        i <- i + 4
        v

    let escapePointer (k: string) : string =
        if k.IndexOf('~') >= 0 || k.IndexOf('/') >= 0 then k.Replace("~", "~0").Replace("/", "~1") else k

    // Called with s.[i] = '"'. Returns the decoded string and leaves i after the closing quote.
    let parseString () : string =
        i <- i + 1
        sb.Clear() |> ignore
        let mutable fin = false
        while not fin do
            if i >= n then raise (err "unterminated string")
            let c = s.[i]
            if c = '"' then
                i <- i + 1
                fin <- true
            elif c = '\\' then
                i <- i + 1
                if i >= n then raise (err "unterminated string")
                let e = s.[i]
                match e with
                | '"' -> sb.Append('"') |> ignore; i <- i + 1
                | '\\' -> sb.Append('\\') |> ignore; i <- i + 1
                | '/' -> sb.Append('/') |> ignore; i <- i + 1
                | 'b' -> sb.Append('\b') |> ignore; i <- i + 1
                | 'f' -> sb.Append('\012') |> ignore; i <- i + 1
                | 'n' -> sb.Append('\n') |> ignore; i <- i + 1
                | 'r' -> sb.Append('\r') |> ignore; i <- i + 1
                | 't' -> sb.Append('\t') |> ignore; i <- i + 1
                | 'u' ->
                    i <- i + 1
                    let escStart = i - 2
                    let cu = readHex4 ()
                    if cu >= 0xD800 && cu <= 0xDBFF then
                        if i + 1 < n && s.[i] = '\\' && s.[i + 1] = 'u' then
                            i <- i + 2
                            let lo = readHex4 ()
                            if lo >= 0xDC00 && lo <= 0xDFFF then
                                sb.Append(char cu).Append(char lo) |> ignore
                            else
                                i <- escStart
                                raise (err "high surrogate escape not followed by a low surrogate")
                        else
                            i <- escStart
                            raise (err "lone high surrogate escape")
                    elif cu >= 0xDC00 && cu <= 0xDFFF then
                        i <- escStart
                        raise (err "lone low surrogate escape")
                    else
                        sb.Append(char cu) |> ignore
                | _ -> raise (err ("invalid escape sequence \\" + describe e))
            elif c < ' ' then
                raise (err ("unescaped control character " + describe c + " in string"))
            elif Char.IsHighSurrogate c then
                if i + 1 < n && Char.IsLowSurrogate s.[i + 1] then
                    sb.Append(c).Append(s.[i + 1]) |> ignore
                    i <- i + 2
                else
                    raise (err "unpaired surrogate in string")
            elif Char.IsLowSurrogate c then
                raise (err "unpaired surrogate in string")
            else
                sb.Append(c) |> ignore
                i <- i + 1
        sb.ToString()

    let parseNumber () : JsonValue =
        let start = i
        let neg = s.[i] = '-'
        if neg then i <- i + 1
        if i >= n || not (isDigit s.[i]) then raise (err "invalid number: expected digit")
        let intStart = i
        if s.[i] = '0' then
            i <- i + 1
            if i < n && isDigit s.[i] then raise (err "invalid number: leading zero")
        else
            while i < n && isDigit s.[i] do i <- i + 1
        let intEnd = i
        let mutable isFloat = false
        if i < n && s.[i] = '.' then
            isFloat <- true
            i <- i + 1
            if i >= n || not (isDigit s.[i]) then raise (err "invalid number: expected digit after '.'")
            while i < n && isDigit s.[i] do i <- i + 1
        if i < n && (s.[i] = 'e' || s.[i] = 'E') then
            isFloat <- true
            i <- i + 1
            if i < n && (s.[i] = '+' || s.[i] = '-') then i <- i + 1
            if i >= n || not (isDigit s.[i]) then raise (err "invalid number: expected digit in exponent")
            while i < n && isDigit s.[i] do i <- i + 1
        let text = s.Substring(start, i - start)
        let asFloat () =
            let ok, f = Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            if not ok || Double.IsInfinity f || Double.IsNaN f then
                i <- start
                raise (err "number out of range")
            JsonValue.Float f
        if isFloat then
            asFloat ()
        else
            let digits = intEnd - intStart
            if digits <= 18 then
                let mutable acc = 0L
                for k in intStart .. intEnd - 1 do
                    acc <- acc * 10L + int64 (int s.[k] - int '0')
                JsonValue.Integer(if neg then -acc else acc)
            else
                let ok, v = Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                if ok then JsonValue.Integer v else asFloat ()

    let literal (word: string) (v: JsonValue) : JsonValue =
        if String.CompareOrdinal(s, i, word, 0, word.Length) = 0 then
            i <- i + word.Length
            v
        else
            raise (err ("invalid literal, expected " + word))

    let rec parseValue (ptr: string) (depth: int) : JsonValue =
        if i >= n then raise (err "unexpected end of input")
        let start = posNow ()
        let v =
            match s.[i] with
            | '{' -> parseObject ptr depth
            | '[' -> parseArray ptr depth
            | '"' -> JsonValue.String(parseString ())
            | 't' -> literal "true" (JsonValue.Boolean true)
            | 'f' -> literal "false" (JsonValue.Boolean false)
            | 'n' -> literal "null" JsonValue.Null
            | c when c = '-' || isDigit c -> parseNumber ()
            | c -> raise (err ("unexpected character " + describe c))
        if track then spans.Add((ptr, { Start = start; End = posNow () }))
        v

    and parseArray (ptr: string) (depth: int) : JsonValue =
        if depth >= maxDepth then raise (err "maximum nesting depth exceeded")
        i <- i + 1
        skipWs ()
        let items = ResizeArray<JsonValue>()
        if i < n && s.[i] = ']' then
            i <- i + 1
        else
            let mutable fin = false
            while not fin do
                skipWs ()
                let childPtr = if track then ptr + "/" + string items.Count else ""
                items.Add(parseValue childPtr (depth + 1))
                skipWs ()
                if i >= n then raise (err "unterminated array")
                match s.[i] with
                | ',' -> i <- i + 1
                | ']' ->
                    i <- i + 1
                    fin <- true
                | c -> raise (err ("expected ',' or ']' but found " + describe c))
        JsonValue.Array(items.ToArray())

    and parseObject (ptr: string) (depth: int) : JsonValue =
        if depth >= maxDepth then raise (err "maximum nesting depth exceeded")
        i <- i + 1
        skipWs ()
        let props = ResizeArray<string * JsonValue>()
        let seen = System.Collections.Generic.HashSet<string>()
        if i < n && s.[i] = '}' then
            i <- i + 1
        else
            let mutable fin = false
            while not fin do
                skipWs ()
                if i >= n then raise (err "unterminated object")
                if s.[i] <> '"' then raise (err ("expected string key but found " + describe s.[i]))
                let key = parseString ()
                skipWs ()
                if i >= n then raise (err "unterminated object")
                if s.[i] <> ':' then raise (err ("expected ':' but found " + describe s.[i]))
                i <- i + 1
                skipWs ()
                let childPtr = if track then ptr + "/" + escapePointer key else ""
                let mark = spans.Count
                let isDuplicate = not (seen.Add key)
                props.Add((key, parseValue childPtr (depth + 1)))
                // later duplicates are not addressable by pointer: drop their spans
                if track && isDuplicate then spans.RemoveRange(mark, spans.Count - mark)
                skipWs ()
                if i >= n then raise (err "unterminated object")
                match s.[i] with
                | ',' -> i <- i + 1
                | '}' ->
                    i <- i + 1
                    fin <- true
                | c -> raise (err ("expected ',' or '}' but found " + describe c))
        JsonValue.Record(props.ToArray())

    member _.ParseDocument() : JsonValue =
        if i < n && s.[i] = '﻿' then
            i <- i + 1
            lineStart <- i // a BOM does not count as a column
        skipWs ()
        if i >= n then raise (err "unexpected end of input: empty document")
        let v = parseValue "" 0
        skipWs ()
        if i < n then raise (err ("unexpected character " + describe s.[i] + " after end of document"))
        v

    member _.Spans() : Map<string, Span> =
        let mutable m = Map.empty
        for (k, v) in spans do
            if not (Map.containsKey k m) then m <- Map.add k v m
        m

and [<AbstractClass; Sealed>] internal JsonPrinter =

    static member Escape(sb: StringBuilder, s: string) : unit =
        sb.Append('"') |> ignore
        for idx in 0 .. s.Length - 1 do
            let c = s.[idx]
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            | '\b' -> sb.Append("\\b") |> ignore
            | '\012' -> sb.Append("\\f") |> ignore
            | _ ->
                let isLoneSurrogate =
                    (Char.IsHighSurrogate c && not (idx + 1 < s.Length && Char.IsLowSurrogate s.[idx + 1]))
                    || (Char.IsLowSurrogate c && not (idx > 0 && Char.IsHighSurrogate s.[idx - 1]))
                if c < ' ' || isLoneSurrogate then
                    sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
                else
                    sb.Append(c) |> ignore
        sb.Append('"') |> ignore

    /// Round-tripping float text that is always recognisable as a float (`1.0`, not `1`).
    /// NaN and infinities have no JSON form and print as `null`.
    static member FloatText(f: float) : string =
        if Double.IsNaN f || Double.IsInfinity f then
            "null"
        else
            let t = f.ToString("R", CultureInfo.InvariantCulture)
            if t.IndexOf('.') >= 0 || t.IndexOf('E') >= 0 || t.IndexOf('e') >= 0 then t else t + ".0"

    static member Write(sb: StringBuilder, v: JsonValue, indent: bool, level: int) : unit =
        let newline (lvl: int) =
            if indent then
                sb.Append('\n') |> ignore
                for _ in 1 .. lvl do sb.Append("  ") |> ignore
        match v with
        | JsonValue.Null -> sb.Append("null") |> ignore
        | JsonValue.Boolean b -> sb.Append(if b then "true" else "false") |> ignore
        | JsonValue.Integer x -> sb.Append(x.ToString(CultureInfo.InvariantCulture)) |> ignore
        | JsonValue.Float f -> sb.Append(JsonPrinter.FloatText f) |> ignore
        | JsonValue.String s -> JsonPrinter.Escape(sb, s)
        | JsonValue.Array items ->
            if items.Length = 0 then
                sb.Append("[]") |> ignore
            else
                sb.Append('[') |> ignore
                for k in 0 .. items.Length - 1 do
                    if k > 0 then sb.Append(',') |> ignore
                    newline (level + 1)
                    JsonPrinter.Write(sb, items.[k], indent, level + 1)
                newline level
                sb.Append(']') |> ignore
        | JsonValue.Record props ->
            if props.Length = 0 then
                sb.Append("{}") |> ignore
            else
                sb.Append('{') |> ignore
                for k in 0 .. props.Length - 1 do
                    if k > 0 then sb.Append(',') |> ignore
                    newline (level + 1)
                    let (name, value) = props.[k]
                    JsonPrinter.Escape(sb, name)
                    sb.Append(if indent then ": " else ":") |> ignore
                    JsonPrinter.Write(sb, value, indent, level + 1)
                newline level
                sb.Append('}') |> ignore

    static member Print(v: JsonValue, indent: bool) : string =
        let sb = StringBuilder()
        JsonPrinter.Write(sb, v, indent, 0)
        sb.ToString()
