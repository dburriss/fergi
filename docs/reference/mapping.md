# Mapping reference

Everything here uses `open Fnode` and `open Fergi`.

## Pointers and selectors

A pointer is a JSON Pointer (`/metadata/name`, `/ports/0`). A selector is a pointer that may also contain a
`[key=value]` step, which picks the array item whose string field `key` equals `value`:
`/ports/[name=http]/port`. Selector strings are parsed with `Selector.parse`, plain pointers with
`Path.parse`. Both return a `Result`.

```fsharp
let sel s = Selector.parse s |> Result.defaultWith (fun e -> failwith e.Message)
let ptr s = Path.parse s |> Result.defaultWith (fun e -> failwith e.Message)
```

## Document functions

| Function | Does |
|---|---|
| `Doc.get`, `Doc.set`, `Doc.remove` | read and write one value by selector |
| `Doc.merge policy base overlay` | combine two documents |
| `Doc.layers policy [ name, doc; ... ]` | fold several, later wins; also returns provenance |
| `Patch.apply ops doc` | apply `Op` values atomically (`Set`, `Remove`, `Append`, `Move`, `Test`) |
| `Patch.diff a b` | the ops that turn `a` into `b` (arrays compared whole) |

`Policy` starts from `Policy.defaults` (lists replaced, last wins, null deletes a key) and is adjusted with
`Policy.withListsBy`, `Policy.withConflict` and `Policy.withNullDeletes`.

## Files

```fsharp
files {
    input "org" "defaults/org.yaml"
    inputList "repos" [ "repos/a.yaml"; "repos/b.yaml" ]
    output "deploy/billing.yaml"
}
```

The plain form is the `Files` record: `Inputs: Map<string, string>`, `InputLists` and `Output`. The strings
are opaque to Fergi; they are handed to your loader and used as the key of the result.

## Mapping rules

A mapping names its inputs and lists rules that run top to bottom. The `mapping` CE returns
`Result<Mapping, Error list>`: every bad pointer in the block is reported together.

| Rule (CE) | Case | Does |
|---|---|---|
| `layer [ names ]` | `Layer` | merge these inputs, later wins (at most one layer) |
| `listsBy "/ports" "name"` | `ListsBy` | merge policy: merge a list by an item key |
| `conflict ConflictMode.LastWins` | `Conflict` | `LastWins`, `FirstWins` or `Fail` |
| `nullDeletes` | `NullDeletes` | a null overlay value deletes the key |
| `copy "service:/name" "/metadata/name"` | `Copy` | copy from a NAMED input, not the merged document |
| `rename "/old" "/new"` | `Rename` | move a value |
| `defaultTo "/kind" (Str "service")` | `DefaultTo` | set only when absent |
| `set "/ports/[name=http]/port" (Int 80L)` | `Set` | set a value |
| `drop "/internal"` | `Drop` | remove (absent is fine) |
| `compute "/fullName" "join" [ ... ]` | `Compute` | call a function by name |
| `inputList` / `each` / `sortBy` | `Collect` | aggregation; see [aggregate files](../how-to/aggregate-files.md) |
| `ensure "has name" (fun doc -> ...)` | n/a | hook checking the result; code only, not serialisable |

Selectors in rules may use `[key=value]`.

### `compute` and functions

`compute dest fn args` calls a function by name. In an argument, `Ptr "input:/path"` reads a named input,
`Ptr "/path"` reads the document built so far, `Ptr "input:"` is the whole input document, and `Lit` is a
fixed value.

Built-ins: `concat`, `join`, `upper`, `lower`, `trim`, `toInt`, `toString`, `coalesce`. Add your own:

```fsharp
Mapping.withFunction "name" (fun args -> Ok(Str "..."))
```

## Running

| Call | Returns |
|---|---|
| `Fergi.run m f load` | `Result<Map<string, Node>, Error list>`, keyed by `f.Output`; nothing is written |
| `Fergi.plan m f load` | dry run: the result, the `Op` list that turns the current output into it (absent counts as empty), and layer provenance |
| `Fergi.runFiles [ name, file ] out rules load` | one-call form without a `Mapping` or `Files` value (takes only rules: no input lists or `Ensure` hooks) |
| `Loader.ofNodes (Map.ofList [ "a.yaml", node ])` | an in-memory loader, handy in tests |
| `Mapping.validate m` | check a mapping alone, returning every problem |

A `Loader` is `string -> Result<Parsed, Error list>`. A typical one:

```fsharp
let load (file: string) : Result<Parsed, Error list> =
    let text = System.IO.File.ReadAllText file
    let format = if file.EndsWith ".json" then Fnode.Json.format else Fnode.Yaml.format
    format.Parse text
```

Names are checked before any file loads, and every load or merge error is reported with its file, and line
and column where the parser recorded one. Errors carry a `Code` such as `merge.conflict` or `path.notFound`,
and `Context` entries naming the rule index, input and file.

## Plain data form

`Rule` is plain data, and `Mapping.create` takes the input names and the rules. No CE is required.

```fsharp
let m =
    Mapping.create
        [ "org"; "service" ]
        [ Rule.Layer [ "org"; "service" ]
          Rule.ListsBy(ptr "/ports", "name")
          Rule.Copy(("service", sel "/name"), sel "/metadata/name")
          Rule.DefaultTo(sel "/kind", Str "service")
          Rule.Compute
              { Fn = "join"
                Args = [ Arg.Literal(Str " "); Arg.Input("service", sel "/first"); Arg.Doc(sel "/last") ]
                Dest = sel "/fullName" } ]
    |> Mapping.withInputLists [ "repos" ]
    |> Mapping.ensure "has name" (fun doc -> Ok())

let out = Fergi.runFiles [ "org", "org.yaml"; "service", "svc.yaml" ] "out.yaml" m.Rules load
```

`Rule` cases: `Layer`, `ListsBy`, `Conflict`, `NullDeletes`, `Copy`, `Rename`, `DefaultTo`, `Set`, `Drop`,
`Collect`, `Compute`. `Mapping.copy "service:/name" "/metadata/name" inputs` parses one `Copy` from strings.

You can also skip mappings and call the pieces yourself: parse with an adapter, `Doc.layers` to merge,
`Patch.apply` to edit, `format.Print` to write.

## Mapping file format

Versioned (`version: 1`), one key per rule. The JSON Schema is [mapping.schema.json](../mapping.schema.json).
See [how to write and read mapping files](../how-to/use-mapping-files.md).
