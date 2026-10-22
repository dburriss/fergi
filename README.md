# fergi

Experimental mapping and transformation library for YAML and JSON documents (F#, also compiled to JS and
Python through Fable). Nothing is published yet; use it by project reference.

Fergi reads documents into one format-neutral tree (`Node`, from the `Fnode` core), merges and edits them,
and builds one output document from several inputs. It never touches the file system: you hand it a
`Loader` (`string -> Result<Parsed, Error list>`) and write the result yourself.

- `src/Fnode`: the tree, JSON-Pointer `Path`, `Error`, `Format`.
- `src/Fnode.Yaml`, `src/Fnode.Json`: the format adapters (`Fnode.Yaml.format`, `Fnode.Json.format`).
- `src/Fergi`: selectors, merge, patch, mappings and the computation expressions (this README).
- `src/Fergi.Jsonata`: optional JSONata expressions for computed values (.NET only, a spike).

How the pieces fit is in `ARCHITECTURE.md`, current state in `STATUS.md`.

```fsharp
open Fnode
open Fergi
```

## Two ways to use it

Everything the computation expressions (CEs) do can be written with plain functions and values. The CEs only
build those values with less typing, and report bad pointer strings as `Error` values instead of exceptions.

| You want to | CE | Low level |
|---|---|---|
| Edit one document | `ops { ... }` | `Op` list, `Patch.apply` |
| Combine results and keep every error | `docs { ... }` | `Result<_, Error list>` and `Result.bind` |
| Declare where files live | `files { ... }` | `Files` record |
| Describe a whole transformation | `mapping { ... }` | `Mapping.create` over a `Rule list` |
| Build the entries of an aggregation | `entry { ... }` | `Rule list` inside `Rule.Collect` |

## Pointers and selectors

A pointer is a JSON Pointer (`/metadata/name`, `/ports/0`). A selector is a pointer that may also contain a
`[key=value]` step, which picks the array item whose string field `key` equals `value`:
`/ports/[name=http]/port`. Selector strings are parsed with `Selector.parse`, plain pointers with
`Path.parse`. Both return a `Result`.

```fsharp
let sel s = Selector.parse s |> Result.defaultWith (fun e -> failwith e.Message)
let ptr s = Path.parse s |> Result.defaultWith (fun e -> failwith e.Message)
```

## Editing one document: `ops` and `Patch`

`Op` has `Set`, `Remove`, `Append`, `Move` and `Test`. `Patch.apply` applies a list atomically: if any op
fails, you get every error and no document.

```fsharp
let isProd = true

let plan =
    ops {
        yield Op.Set(ptr "/owner", Str "energy")
        yield Op.Remove(ptr "/draft")
        if isProd then yield Op.Set(ptr "/replicas", Int 3L)
        for k in [ "a"; "b" ] do
            yield Op.Remove(ptr ("/tmp/" + k))
    }

let result = Patch.apply plan (Obj [ "owner", Str "x"; "draft", Bool true ])
// Ok (Obj [...]) or Error [ ... ]
```

`ops` is only sugar for building a list; `[ Op.Set(...); Op.Remove(...) ]` is the same value. `Patch.diff a b`
goes the other way: the ops that turn `a` into `b` (arrays are compared whole).

Single reads and writes on a document, by selector: `Doc.get`, `Doc.set`, `Doc.remove`.

## Merging: `Doc.merge`, `Doc.layers` and `Policy`

`Doc.merge policy base overlay` combines two documents; `Doc.layers policy [ name, doc; ... ]` folds several,
later ones winning, and also returns provenance (which layer each leaf came from). Conflicts come back as a
list (all of them), not an exception.

```fsharp
let policy =
    Policy.defaults                                  // lists replaced, last wins, null deletes a key
    |> Policy.withListsBy (ptr "/ports") "name"      // merge /ports by each item's "name"
    |> Policy.withConflict OnConflict.LastWins
    |> Policy.withNullDeletes true

match Doc.layers policy [ "org", orgDoc; "service", serviceDoc ] with
| Ok(merged, provenance) -> ...
| Error conflicts -> conflicts |> List.map Conflict.toError
```

`OnConflict.Resolve` takes a function, so it only exists in code (a mapping file can say `lastWins`,
`firstWins` or `fail`). Merge is not associative in general; see the limits in `STATUS.md`.

## Combining results: `docs`

`docs` is a `Result<_, Error list>` builder. `let!` stops at the first error; `and!` runs the sources side by
side and collects the errors of all of them.

```fsharp
let loaded =
    docs {
        let! org = load "org.yaml"
        and! svc = load "service.yaml"
        return org.Node, svc.Node
    }
```

Without the CE, this is `Result.bind` / `Result.map` by hand.

## Where the files live: `files` and `Files`

```fsharp
let f =
    files {
        input "org" "defaults/org.yaml"
        input "service" "services/billing/service.yaml"
        inputList "repos" [ "repos/a.yaml"; "repos/b.yaml" ]   // see aggregation below
        output "deploy/billing.yaml"
    }

// the same value by hand
let f2 : Files =
    { Inputs = Map.ofList [ "org", "defaults/org.yaml"; "service", "services/billing/service.yaml" ]
      InputLists = Map.empty
      Output = "deploy/billing.yaml" }
```

The strings are opaque to Fergi; they are handed to your loader and used as the key of the result.

## A whole transformation: `mapping` and `Mapping`

A mapping names its inputs and lists rules that run top to bottom. `mapping` returns
`Result<Mapping, Error list>`: every bad pointer in the block is reported together.

```fsharp
let built =
    mapping {
        inputs [ "org"; "service" ]
        layer [ "org"; "service" ]                      // merge these, later wins (at most one layer)
        listsBy "/ports" "name"                         // merge policy
        conflict ConflictMode.LastWins                  // or FirstWins, Fail
        copy "service:/name" "/metadata/name"           // from a NAMED input, not the merged document
        defaultTo "/kind" (Str "service")               // only when absent
        set "/ports/[name=http]/port" (Int 80L)
        rename "/old" "/new"
        drop "/internal"
        compute "/fullName" "join" [ Lit(Str " "); Ptr "service:/first"; Ptr "/last" ]
        ensure "has name" (fun doc -> ...)              // checked on the result; code only
    }
```

Rules: `layer`, `listsBy`, `conflict`, `nullDeletes`, `copy`, `rename`, `defaultTo`, `set`, `drop`, `compute`,
`each` (with `sortBy`) and `inputList`; `ensure` is a hook rather than a rule. Selectors may use `[key=value]`.

`compute` calls a function by name. In `Ptr`, `"input:/path"` reads a named input and `"/path"` reads the
document built so far; `Lit` is a fixed value. Built-ins: `concat`, `join`, `upper`, `lower`, `trim`, `toInt`,
`toString`, `coalesce`. Add your own with `Mapping.withFunction "name" (fun args -> Ok(Str "..."))`.

### Run it

```fsharp
// your loader: file name -> parsed document with positions
let load (file: string) : Result<Parsed, Error list> =
    let text = System.IO.File.ReadAllText file
    let format = if file.EndsWith ".json" then Fnode.Json.format else Fnode.Yaml.format
    format.Parse text

match built with
| Error errs -> ...                                    // bad pointers in the mapping
| Ok m ->
    match Fergi.run m f load with                      // Result<Map<string, Node>, Error list>
    | Ok outputs -> System.IO.File.WriteAllText("deploy/billing.yaml", Fnode.Yaml.format.Print outputs.["deploy/billing.yaml"])
    | Error errs -> for e in errs do printfn "%s: %s" e.Code e.Message
```

- `Fergi.run m f load` returns the output documents, keyed by `f.Output`. Nothing is written.
- `Fergi.plan m f load` is a dry run: the result, the `Op` list that turns the current output file into it
  (an absent output counts as empty), and the layer provenance.
- `Loader.ofNodes (Map.ofList [ "a.yaml", node ])` is an in-memory loader, handy in tests.
- Names are checked before any file loads, and every load or merge error is reported with its file, and
  line and column where the parser recorded one. Errors carry a `Code` such as `merge.conflict` or
  `path.notFound`, and `Context` entries naming the rule index, input and file.

### Aggregation: `inputList`, `each`, `entry`

An `inputList` stands for many files. `each` builds one entry per file and sets the list at a path; the
builder function `r` qualifies a pointer with the input name.

```fsharp
let catalog =
    mapping {
        inputList "services"
        each "services" "/services" (fun r ->
            entry {
                copy (r "/name") "/name"
                set "/kind" (Str "service")
                compute "/title" "upper" [ Ptr(r "/name") ]
            })
        sortBy "/name"                                  // orders the entries of the preceding each
    }
// files { inputList "services" [ "a/service.yaml"; "b/service.yaml" ]; output "catalog.yaml" }
```

### Validate without running

`Mapping.validate m` checks a mapping alone, with no files or loader: undeclared inputs, an input list used
outside `each`, a second `layer`, unknown function names. It returns every problem. Use it before writing a
mapping file you generated. With JSONata registered, `Jsonata.validate m` (in `Fergi.Jsonata`) also checks
the syntax of literal expressions.

## Low level: the same mapping as plain data

No CE is required. `Rule` is plain data, and `Mapping.create` takes the input names and the rules.

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

// the whole thing in one call, without a Mapping or Files value
let out = Fergi.runFiles [ "org", "org.yaml"; "service", "svc.yaml" ] "out.yaml" m.Rules load
```

`Rule` cases: `Layer`, `ListsBy`, `Conflict`, `NullDeletes`, `Copy`, `Rename`, `DefaultTo`, `Set`, `Drop`,
`Collect`, `Compute`. `Mapping.copy "service:/name" "/metadata/name" inputs` parses one `Copy` from strings.

You can also skip mappings and call the pieces yourself: parse with an adapter, `Doc.layers` to merge,
`Patch.apply` to edit, `format.Print` to write.

## Mapping files

A mapping can be written to and read from a file, so another program can generate it (for the CLI below, or a
producer in another language).

```fsharp
match Mapping.toNode m with                     // Result<Node, Error list>
| Ok node -> System.IO.File.WriteAllText("mapping.yaml", Fnode.Yaml.format.Print node)
| Error errs -> ...   // mapping.notSerialisable: an Ensure hook, a host function

let fromFile =
    Fnode.Yaml.format.Parse(System.IO.File.ReadAllText "mapping.yaml")
    |> Result.bind (fun parsed -> Mapping.ofParsed parsed)   // errors carry line and column
```

Use `Mapping.ofNode` when you have a `Node` and no positions. The format is versioned (`version: 1`), one key
per rule, with a JSON Schema in `docs/mapping.schema.json`. `Files` has no file form yet (the CLI takes it from flags). Details and
reasoning are in `ARCHITECTURE.md` and `DECISIONS.md`.

## Command line

`src/Fergi.Cli` (Argu) runs a mapping file; `--help` works on every command. It reads YAML or JSON (by extension), registers the built-ins and
`jsonata`, and writes one output file.

```
fergi validate mapping.yaml
fergi run mapping.yaml -i defaults/org.yaml -i team=teams/energy.yaml -i service=svc/billing.yaml -o deploy/billing.yaml
fergi run catalog.yaml -l repos="repos/*/service.yaml" -o catalog/services.yaml
fergi run mapping.yaml -i org=defaults/org.yaml -o out.yaml --dry-run
```

- `-i [name=]file` is a single input. Without `name=` the name is the file name without its extension
  (`defaults/org.yaml` binds `org`); two inputs with the same derived name need explicit names.
- `-l name=file` (`--input-list`) adds a file to a list input (the mapping's `inputLists`). Repeat it; the order is kept. A value
  with `*` or `?` is expanded by the CLI and sorted by path; no match is an error.
- `-o file` is the single output, written only when the whole run succeeds. The output may not be an input or
  the mapping. `--dry-run` prints the changes against the current output and writes nothing.
- Errors go to stderr as `file:line:col: code: message`. Exit codes: 0 ok, 1 validation or run errors, 2 usage.

Build and run it with `dotnet run --project src/Fergi.Cli -c Release -- run ...` (the executable is `Fergi.Cli`).
The `fergi` command name needs the tool packaging that comes with publishing.

## JSONata (optional, .NET only)

```fsharp
open Fergi.Jsonata
let m2 = Jsonata.register m      // adds the `jsonata` function to the registry
// compute "/fullName" "jsonata" [ Lit(Str "first & ' ' & last"); Ptr "person:" ]   // "person:" is the whole input
```

The first argument is the expression, the second is the context (`$`), and all arguments after the
expression are available as `$args`. Runtimes disagree on some JSONata behaviour (see `docs/jsonata.md`).

## Examples

`examples/readme.fsx` holds the snippets above as a runnable script (`dotnet fsi examples/readme.fsx` after a
build). It uses toy data, so a few runs print an error on purpose.

## Build and test

```
dotnet build Fergi.slnx -c Release
dotnet test Fergi.slnx -c Release
scripts/fable-test.sh all        # JS (bun) and Python; slow, needs network the first time
```

`Fnode.Yaml` references `../fsharp-yaml` by project reference, so check that repo out next to this one.
