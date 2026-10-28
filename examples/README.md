# Examples

Runnable scripts for each Fergi feature, with the YAML and JSON files they read. Each script prints what it
does and what the library returns, including a few runs that fail on purpose to show the errors. None of them
writes into the repo.

## Run them

Build once, then run any script from the repo root:

```
dotnet build Fergi.slnx -c Release
dotnet fsi examples/01-edit-a-document.fsx
```

The scripts reference the built assemblies in `src/Fergi.Cli/bin/Release/net8.0` (see `common.fsx`), so they
need the Release build and the .NET 8 SDK. A cold `dotnet fsi` can take a while to start.

## Index

| Script | Shows | Guide |
|---|---|---|
| [01-edit-a-document.fsx](01-edit-a-document.fsx) | `Doc.get/set/remove`, the `ops` CE, `Patch.apply` (atomic), `Patch.diff` | [edit a document](../docs/how-to/edit-a-document.md) |
| [02-merge-documents.fsx](02-merge-documents.fsx) | `Doc.merge`, `Doc.layers`, list merging by key, conflict policies, null handling, provenance, the `docs` CE | [merge documents](../docs/how-to/merge-documents.md) |
| [03-first-mapping.fsx](03-first-mapping.fsx) | `mapping` and `files` CEs, `Fergi.run`, YAML and JSON in and out, bad pointers | [first mapping](../docs/tutorials/first-mapping.md) |
| [04-mapping-rules.fsx](04-mapping-rules.fsx) | every rule: `layer`, `listsBy`, `conflict`, `nullDeletes`, `copy`, `set`, `defaultTo`, `rename`, `drop`, `compute`, built-in and custom functions, `ensure` | [mapping reference](../docs/reference/mapping.md) |
| [05-aggregate-files.fsx](05-aggregate-files.fsx) | `inputList`, `each`, `entry`, `sortBy`, `Mapping.validate` | [aggregate files](../docs/how-to/aggregate-files.md) |
| [06-plain-data.fsx](06-plain-data.fsx) | the no-CE form: `Rule`, `Mapping.create`, `Fergi.runFiles`, `Loader.ofNodes` | [mapping reference](../docs/reference/mapping.md#plain-data-form) |
| [07-mapping-files.fsx](07-mapping-files.fsx) | `Mapping.toNode`, `Mapping.ofParsed`, errors with line and column, round trip | [mapping files](../docs/how-to/use-mapping-files.md) |
| [08-plan-dry-run.fsx](08-plan-dry-run.fsx) | `Fergi.plan`: the ops against the current output, and layer provenance | [mapping reference](../docs/reference/mapping.md#running) |
| [09-jsonata.fsx](09-jsonata.fsx) | `Jsonata.register`, `jsonata` in `compute`, JSONata `ensure`, `Jsonata.validate` | [use JSONata](../docs/how-to/use-jsonata.md) |
| [10-errors.fsx](10-errors.fsx) | error codes, source positions, rule context, every error reported at once | [mapping reference](../docs/reference/mapping.md#running) |
| [cli.sh](cli.sh) | the `fergi` command: `validate`, `run`, `-i`, `-l`, `--dry-run`, exit codes | [CLI reference](../docs/reference/cli.md) |
| [readme.fsx](readme.fsx) | the snippets from the docs, in one file, over in-memory documents | |

[common.fsx](common.fsx) is not an example. It references the assemblies and holds the helpers the scripts
share: `load` (reads a file and parses it by extension, which is the loader Fergi asks for), the `sel` and
`ptr` parsers, and printing.

## Data

Everything the scripts read is in [`data/`](data):

| File | Used as |
|---|---|
| `org.yaml` | org-wide defaults, the lowest layer |
| `team.yaml` | team overrides; it clashes with `org.yaml` on purpose (owner, tier, ports) |
| `billing.yaml` | one service (YAML) |
| `payments.json` | one service (JSON), with a `null` that deletes a key when merged |
| `services/*.yaml` | three service files for the `inputList` examples |
| `mappings/deploy.yaml` | a mapping file: merge org and service, then reshape (has a JSONata `ensure`) |
| `mappings/catalog.yaml` | a mapping file that builds a catalog from a list of files |
| `mappings/broken.yaml` | a mapping file with three mistakes, for the error reports |
| `current-output.yaml` | what the dry run compares against |
| `broken.yaml`, `broken.json` | files that do not parse |

## Where to go next

- [Documentation index](../README.md#documentation)
- [ARCHITECTURE.md](../ARCHITECTURE.md) before adding a feature
