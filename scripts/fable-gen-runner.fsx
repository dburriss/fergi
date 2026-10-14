// Generates the Fable test runner from the shared xUnit / FsCheck test files.
//
// xUnit and FsCheck cannot run under Fable, so tests/Fnode.Fable compiles the very same test files
// (with small `Xunit` and `FsCheck` shims) and this script writes, next to it:
//
//   * Runner.generated.fs         - one entry per [<Fact>] / [<Theory>] row / [<Property>], calling the
//                                   test function directly (properties go through FsCheck.Xunit.Check.run)
//   * JsonTestSuite.generated.fs  - the vendored JSON Test Suite files embedded as strings (Fable has no
//                                   file system access), plus runner entries for them
//
// Usage: dotnet fsi scripts/fable-gen-runner.fsx
open System
open System.IO
open System.Text
open System.Text.RegularExpressions

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let testsRoot = Path.Combine(root, "tests")
let outDir = Path.Combine(testsRoot, "Fnode.Fable")

/// The .NET test projects whose sources are shared, in run order.
let projects = [ "Fnode.Tests"; "JsonValue.Tests"; "Fnode.Json.Tests"; "Fnode.Yaml.Tests"; "Fnode.Conformance" ]

/// Test files left out of the Fable run (replaced by hand-written equivalents in tests/Fnode.Fable).
/// JsonTestSuiteTests reads files and bytes from disk; JsonTestSuite.generated.fs embeds the corpus instead.
let skipFiles = set [ "JsonTestSuiteTests.fs" ]

let fsprojOrder (project: string) =
    let text = File.ReadAllText(Path.Combine(testsRoot, project, project + ".fsproj"))
    [ for m in Regex.Matches(text, "<Compile Include=\"([^\"]+\\.fs)\"") -> m.Groups.[1].Value ]

/// Splits `a, "b,c", 3` on top-level separators.
let splitTopLevel (sep: char) (text: string) : string list =
    let parts = ResizeArray<string>()
    let cur = StringBuilder()
    let mutable depth = 0
    let mutable inStr = false
    let mutable i = 0
    while i < text.Length do
        let c = text.[i]
        if inStr then
            cur.Append c |> ignore
            if c = '\\' then
                cur.Append text.[i + 1] |> ignore
                i <- i + 1
            elif c = '"' then
                inStr <- false
        elif c = '"' then
            inStr <- true
            cur.Append c |> ignore
        elif c = '(' || c = '[' || c = '<' then
            depth <- depth + 1
            cur.Append c |> ignore
        elif c = ')' || c = ']' || (c = '>' && depth > 0) then
            depth <- depth - 1
            cur.Append c |> ignore
        elif c = sep && depth = 0 then
            parts.Add(cur.ToString().Trim())
            cur.Clear() |> ignore
        else
            cur.Append c |> ignore
        i <- i + 1
    if cur.ToString().Trim() <> "" then parts.Add(cur.ToString().Trim())
    List.ofSeq parts

type Style =
    | Unit
    | Curried
    | Tupled

/// Returns the call style and the (name, type) of each parameter.
let parseParams (paramsText: string) : Style * (string * string) list =
    let t = paramsText.Trim()
    if t = "()" then
        Unit, []
    else
        let groups = [ for m in Regex.Matches(t, @"\(([^()]*)\)") -> m.Groups.[1].Value ]
        let style, items =
            match groups with
            | [ g ] when g.Contains "," -> Tupled, splitTopLevel ',' g
            | _ -> Curried, groups |> List.map (fun g -> g.Trim())
        let parsed =
            items
            |> List.map (fun item ->
                let idx = item.IndexOf ':'
                item.Substring(0, idx).Trim(), item.Substring(idx + 1).Trim())
        style, parsed

type TestFn =
    { Name: string
      Params: string
      /// Individual attributes, e.g. `Theory`, `ClassData(typeof<FormatData>)`.
      Attrs: string list }

/// Reads the module name, every [<Fact>]/[<Theory>]/[<Property>] function, and the static member
/// name of each `type X = static member Name(...)` arbitrary class.
let parseFile (path: string) : string * TestFn list * Map<string, string> =
    let lines = File.ReadAllLines path
    let moduleName =
        lines
        |> Array.pick (fun l ->
            let m = Regex.Match(l, @"^module\s+([\w.]+)")
            if m.Success then Some m.Groups.[1].Value else None)
    let arbMembers =
        let text = String.Join("\n", lines)
        Map.ofList
            [ for m in Regex.Matches(text, @"(?m)^type\s+(\w+)\s*=\s*\n\s*static member\s+(\w+)\s*\(") ->
                  m.Groups.[1].Value, m.Groups.[2].Value ]
    let tests = ResizeArray<TestFn>()
    let mutable attrs: string list = []
    let mutable skipDepth = 0
    for line in lines do
        let s = line.Trim()
        if s = "#if !FABLE_COMPILER" then
            skipDepth <- skipDepth + 1
        elif s.StartsWith "#if" && skipDepth > 0 then
            skipDepth <- skipDepth + 1
        elif s.StartsWith "#else" && skipDepth = 1 then
            skipDepth <- 0
        elif s.StartsWith "#endif" && skipDepth > 0 then
            skipDepth <- skipDepth - 1
        elif skipDepth > 0 then
            attrs <- []
        elif Regex.IsMatch(line, @"^\[<.*>\]\s*$") then
            let inner = s.Substring(2, s.Length - 4)
            attrs <- attrs @ splitTopLevel ';' inner
        else
            let m = Regex.Match(line, @"^let\s+``([^`]+)``\s*(.*?)\s*=\s*(.*)$")
            if m.Success && not attrs.IsEmpty then
                let isTest = attrs |> List.exists (fun a -> a = "Fact" || a = "Theory" || a = "Property" || a.StartsWith "Property(")
                if isTest then
                    tests.Add { Name = m.Groups.[1].Value; Params = m.Groups.[2].Value; Attrs = attrs }
                attrs <- []
            elif s <> "" && not (s.StartsWith "//") then
                attrs <- []
    moduleName, List.ofSeq tests, arbMembers

let call (moduleName: string) (name: string) (style: Style) (args: string list) =
    let fn = moduleName + ".``" + name + "``"
    match style with
    | Unit -> fn + " ()"
    | Tupled -> fn + " (" + String.Join(", ", args) + ")"
    | Curried -> fn + " " + String.Join(" ", args |> List.map (fun a -> "(" + a + ")"))

let fsString (s: string) =
    let sb = StringBuilder("\"")
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if c = '\\' then sb.Append "\\\\" |> ignore
        elif c = '"' then sb.Append "\\\"" |> ignore
        elif c = '\n' then sb.Append "\\n" |> ignore
        elif c = '\r' then sb.Append "\\r" |> ignore
        elif c = '\t' then sb.Append "\\t" |> ignore
        elif Char.IsHighSurrogate c && i + 1 < s.Length && Char.IsLowSurrogate s.[i + 1] then
            sb.Append("\\U" + (Char.ConvertToUtf32(c, s.[i + 1])).ToString("x8")) |> ignore
            i <- i + 1
        elif c < ' ' || c > '~' then sb.Append("\\u" + (int c).ToString("x4")) |> ignore
        else sb.Append c |> ignore
        i <- i + 1
    sb.Append("\"").ToString()

/// The generator expression for a property parameter without an Arbitrary class.
let defaultGen (typ: string) =
    match typ with
    | "NonNull<string> list" -> "(FsCheck.Gen.listOf (FsCheck.Gen.map FsCheck.NonNull FsCheck.Arb.generate<string>))"
    | t -> "FsCheck.Arb.generate<" + t + ">"

let attrArg (attr: string) (key: string) =
    let m = Regex.Match(attr, key + @"\s*=\s*(.+?)(?:,\s*\w+\s*=|\)$)")
    if m.Success then Some(m.Groups.[1].Value.Trim()) else None

let entries =
    [ for project in projects do
          for f in fsprojOrder project do
              if not (skipFiles.Contains f) then
                  let moduleName, tests, arbMembers = parseFile (Path.Combine(testsRoot, project, f))
                  let short = moduleName.Split('.') |> Array.last
                  for t in tests do
                      let style, ps = parseParams t.Params
                      let label = short + " / " + t.Name
                      let inlineRows =
                          [ for a in t.Attrs do
                                let m = Regex.Match(a, @"^InlineData\((.*)\)$")
                                if m.Success then splitTopLevel ',' m.Groups.[1].Value ]
                      let classData =
                          t.Attrs
                          |> List.tryPick (fun a ->
                              let m = Regex.Match(a, @"^ClassData\(typeof<([\w.]+)>\)$")
                              if m.Success then Some m.Groups.[1].Value else None)
                      let property = t.Attrs |> List.tryFind (fun a -> a = "Property" || a.StartsWith "Property(")
                      match property with
                      | Some p ->
                          let maxTest = defaultArg (attrArg p "MaxTest") "100"
                          let arb =
                              attrArg p "Arbitrary"
                              |> Option.map (fun v -> Regex.Match(v, @"typeof<([\w.]+)>").Groups.[1].Value)
                          let gens =
                              ps
                              |> List.map (fun (_, typ) ->
                                  match arb with
                                  | Some a -> "(" + moduleName + "." + a + "." + arbMembers.[a] + "()).Gen"
                                  | None -> defaultGen typ)
                          let names = ps |> List.mapi (fun i _ -> "a" + string i)
                          let genExpr =
                              match gens with
                              | [ g ] -> g
                              | [ g1; g2 ] -> "(FsCheck.Gen.zip " + g1 + " " + g2 + ")"
                              | _ -> failwithf "unsupported property arity in %s" label
                          let lambdaArg = if names.Length = 1 then "a0" else "(a0, a1)"
                          let body = call moduleName t.Name Curried names
                          yield
                              project, label,
                              "FsCheck.Xunit.Check.run " + fsString label + " " + maxTest + " " + genExpr + " (fun "
                              + lambdaArg + " -> " + body + ")"
                      | None ->
                          if not inlineRows.IsEmpty then
                              for row in inlineRows do
                                  yield project, label + " (" + String.Join(", ", row) + ")", call moduleName t.Name style row
                          else
                              match classData with
                              | Some cd ->
                                  let unboxed = ps |> List.mapi (fun i (_, typ) -> "unbox<" + typ + "> row.[" + string i + "]")
                                  yield
                                      project, label,
                                      "for row in (" + moduleName + "." + cd + "() :> seq<obj[]>) do "
                                      + call moduleName t.Name style unboxed
                              | None -> yield project, label, call moduleName t.Name style [] ]

// --- JSON Test Suite corpus -------------------------------------------------------------------
let suiteDir = Path.Combine(testsRoot, "JsonValue.Tests", "data", "test_parsing")
let strictUtf8 = UTF8Encoding(false, true)

let corpus =
    [ for p in Directory.GetFiles(suiteDir, "*.json") |> Array.sort do
          let name = Path.GetFileName p
          let text =
              try
                  Some(strictUtf8.GetString(File.ReadAllBytes p))
              with :? DecoderFallbackException ->
                  None
          name, text ]

let suite = StringBuilder()
suite.Append("// <auto-generated> by scripts/fable-gen-runner.fsx - do not edit.\nmodule JsonTestSuite\n\n")
|> ignore
suite.Append("/// (file name, text); None when the file is not valid UTF-8 (a rejection by definition).\n")
|> ignore
suite.Append("let files : (string * string option) list =\n    [\n") |> ignore
for (name, text) in corpus do
    let t =
        match text with
        | Some s -> "Some " + fsString s
        | None -> "None"
    suite.Append("      " + fsString name + ", " + t + "\n") |> ignore
suite.Append("    ]\n") |> ignore
File.WriteAllText(Path.Combine(outDir, "JsonTestSuite.generated.fs"), suite.ToString().Replace("\r\n", "\n"))

let suiteEntries =
    [ let count prefix = corpus |> List.filter (fun (n, _) -> n.StartsWith(prefix, StringComparison.Ordinal)) |> List.length
      for (name, _) in corpus do
          if name.StartsWith "y_" then
              yield "JsonValue.Tests", "JsonTestSuite / y_ files are accepted (" + name + ")", "JsonTestSuiteFable.accepted " + fsString name
          elif name.StartsWith "n_" then
              yield "JsonValue.Tests", "JsonTestSuite / n_ files are rejected (" + name + ")", "JsonTestSuiteFable.rejected " + fsString name
      yield "JsonValue.Tests", "JsonTestSuite / i_ files are reported without assertions", "JsonTestSuiteFable.reportImplementationDefined ()"
      yield "JsonValue.Tests", "JsonTestSuite / suite is present", "JsonTestSuiteFable.present ()" ]

let runner = StringBuilder()
runner.Append("// <auto-generated> by scripts/fable-gen-runner.fsx - do not edit.\nmodule Runner\n\nopen System\n\n")
|> ignore
runner.Append("/// (project, test name, test body)\nlet all : (string * string * (unit -> unit)) list =\n    [\n") |> ignore
for (project, label, body) in entries @ suiteEntries do
    runner.Append("      " + fsString project + ", " + fsString label + ", (fun () -> " + body + ")\n") |> ignore
runner.Append("    ]\n") |> ignore
File.WriteAllText(Path.Combine(outDir, "Runner.generated.fs"), runner.ToString().Replace("\r\n", "\n"))

eprintfn "generated %d test entries (%d from the JSON Test Suite)" (entries.Length + suiteEntries.Length) suiteEntries.Length
