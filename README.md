# fergi

Experimental mapping and transformation library for YAML and JSON documents (F#, also compiled to JS and
Python through Fable). Nothing is published as stable yet; it ships as alpha packages (`Fergi`, and the CLI
as the dotnet tool `Fergi.Tool`).

Fergi reads documents into one format-neutral tree (`Node`, from the `Fnode` core), merges and edits them,
and builds one output document from several inputs. It never touches the file system: you hand it a
`Loader` and write the result yourself.

## Example

Merge an org-wide defaults file with one service file, and add a few fields. The inputs:

`defaults/org.yaml`:

```yaml
owner: platform
replicas: 2
```

`svc/billing.yaml`:

```yaml
name: billing
```

In F#:

```fsharp
open Fnode
open Fergi

// 1. Describe the transformation. Rules run top to bottom; nothing is read yet.
let built =
    mapping {
        inputs [ "org"; "service" ]                 // the names the rules refer to
        layer [ "org"; "service" ]                  // merge both documents, later wins
        copy "service:/name" "/metadata/name"       // read /name from the "service" input
        defaultTo "/kind" (Str "service")           // set /kind only if it is missing
    }

// 2. Bind the names to files and choose the output file name.
let f =
    files {
        input "org" "defaults/org.yaml"
        input "service" "svc/billing.yaml"
        output "deploy/billing.yaml"
    }

// 3. A loader turns a file name into a parsed document. Fergi never reads files itself.
let load (file: string) =
    Fnode.Yaml.format.Parse(System.IO.File.ReadAllText file)

// 4. Run it, then write the result yourself.
match built with
| Error errs -> for e in errs do eprintfn "%s: %s" e.Code e.Message   // bad pointers in the mapping
| Ok m ->
    match Fergi.run m f load with
    | Ok outputs ->
        for KeyValue(file, node) in outputs do
            System.IO.File.WriteAllText(file, Fnode.Yaml.format.Print node)
    | Error errs -> for e in errs do eprintfn "%s: %s" e.Code e.Message   // load, merge or rule errors
```

`deploy/billing.yaml` now contains:

```yaml
owner: platform
replicas: 2
name: billing
metadata:
  name: billing
kind: service
```

`Fergi.run` returns the output documents keyed by the output name and writes nothing. Errors carry a code, a
message and, for files, the line and column. For a step-by-step version see the
[tutorial](docs/tutorials/first-mapping.md); to do the same without code, use the
[CLI](docs/how-to/use-the-cli.md).

## Documentation

### Tutorials
- [Your first mapping](docs/tutorials/first-mapping.md)

### How-to guides
- [Edit one document](docs/how-to/edit-a-document.md)
- [Merge documents](docs/how-to/merge-documents.md)
- [Build one document from many files](docs/how-to/aggregate-files.md)
- [Write and read mapping files](docs/how-to/use-mapping-files.md)
- [Run a mapping from the command line](docs/how-to/use-the-cli.md)
- [Use JSONata expressions](docs/how-to/use-jsonata.md)
- [Build, test and release](docs/how-to/build-test-and-release.md)

### Reference
- [Mapping and library API](docs/reference/mapping.md)
- [CLI](docs/reference/cli.md)
- [Mapping file JSON Schema](docs/mapping.schema.json)

### Explanation
- [Two ways to use Fergi](docs/explanation/two-ways-to-use-it.md)
- [ARCHITECTURE.md](ARCHITECTURE.md): how the pieces fit and the rules they follow
- [DECISIONS.md](DECISIONS.md): larger decisions and the alternatives rejected
- [Fable support](docs/fable.md) and [JSONata across runtimes](docs/jsonata.md)
- [Spike findings](docs/fergi-spike.md)

### Project
- [STATUS.md](STATUS.md): current state and what is next
- [CHANGELOG.md](CHANGELOG.md)

## Packages

- `src/Fnode`: the tree, JSON-Pointer `Path`, `Error`, `Format`.
- `src/Fnode.Yaml`, `src/Fnode.Json`: the format adapters.
- `src/Fergi`: selectors, merge, patch, mappings and the computation expressions.
- `src/Fergi.Jsonata`: optional JSONata expressions (.NET only, a spike).
- `src/Fergi.Cli`: the `fergi` command.
