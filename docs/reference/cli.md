# CLI reference

`src/Fergi.Cli` (Argu), installed as the `fergi` command. `--help` works on every command. It reads YAML or
JSON (by extension), registers the built-ins and `jsonata`, and writes one output file.

To install and try it, see [how to run a mapping from the command line](../how-to/use-the-cli.md).

## Commands

```
fergi validate mapping.yaml
fergi run mapping.yaml [-i [name=]file]... [-l name=file]... -o file [--dry-run]
fergi --version
```

## Options

- `-i [name=]file` is a single input. Without `name=` the name is the file name without its extension
  (`defaults/org.yaml` binds `org`); two inputs with the same derived name need explicit names.
- `-l name=file` (`--input-list`) adds a file to a list input (the mapping's `inputLists`). Repeat it; the
  order is kept. A value with `*` or `?` is expanded by the CLI and sorted by path; no match is an error.
- `-o file` is the single output, written only when the whole run succeeds. The output may not be an input or
  the mapping.
- `--dry-run` prints the changes against the current output and writes nothing.
- `--version` prints the package version and the commit it was built from.

## Errors and exit codes

Errors go to stderr as `file:line:col: code: message`.

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | validation or run errors |
| 2 | usage |
