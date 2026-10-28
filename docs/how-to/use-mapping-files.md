# How to write and read mapping files

A mapping can be written to and read from a file, so another program can generate it (for the CLI, or a
producer in another language).

## Write one

```fsharp
match Mapping.toNode m with                     // Result<Node, Error list>
| Ok node -> System.IO.File.WriteAllText("mapping.yaml", Fnode.Yaml.format.Print node)
| Error errs -> ...   // mapping.notSerialisable: an Ensure hook, a host function
```

## Read one

```fsharp
let fromFile =
    Fnode.Yaml.format.Parse(System.IO.File.ReadAllText "mapping.yaml")
    |> Result.bind (fun parsed -> Mapping.ofParsed parsed)   // errors carry line and column
```

Use `Mapping.ofNode` when you have a `Node` and no positions.

## Check it

The format is versioned (`version: 1`), one key per rule, with a JSON Schema in
[`mapping.schema.json`](../mapping.schema.json). `Files` has no file form yet; the CLI takes it from flags.

Details and reasoning are in [ARCHITECTURE.md](../../ARCHITECTURE.md) and [DECISIONS.md](../../DECISIONS.md).
