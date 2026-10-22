# Architecture

How the pieces fit and the rules they follow. `STATUS.md` lists every project and the current state;
`DECISIONS.md` has the reasoning behind the larger choices; this page is the shape of the thing.

## Layers

```
 files (YAML / JSON)            what people and programs read and write
        |
 Fnode.Yaml, Fnode.Json         adapters: text <-> Node, with source locations
        |
 Fnode                          Node, Path, Error, Parsed: the shared core, no dependencies
        |
 Fergi                          selectors, patch, merge, mapping: Rule / Mapping / Files, run / plan
        |
 Fergi.Jsonata (and similar)    optional packages that plug functions into a mapping
```

Each layer depends only on the ones below it.

## Principles

### The data form is primary; the builders are sugar

A mapping is a value: `Mapping` (inputs, `Rule list`, and beside them the `Ensure` hooks and the `Functions`
registry) plus `Files` (where the inputs live and where the output goes). `Rule` is plain data: it holds names,
selectors and `Node` values, never a function. Everything the engine can do is reachable by building these
values directly.

The computation expressions (`mapping`, `files`, `entry`, `ops`, `docs`) only produce those same values. They
are a convenience for F# code, never a second way to express something the data form cannot. The design
follows from that:

- **The serialised form is the contract.** Mapping files are expected to be produced by a program (a build
  step, a generator, a script in any language), then run by a CLI, so what must be stable is the file format,
  not an F# API (see "Mapping file shape" below).
- **No language depends on another's builders.** The F# builders compile under Fable to JS and Python, but the
  generated names carry hash suffixes and are not a usable API (a builder call becomes nested
  `MappingBuilder__Compute_3B2A1428(...)` calls). A Python or TypeScript author writes the data form, or a
  mapping file, not the transpiled builders.
- **Anything that cannot be data stays beside the rules and is referenced by name.** Custom functions are
  registered under a name and called by `Rule.Compute`; `Ensure` closure hooks are a separate field and exist
  only in the program that runs the mapping (the in-process flavour of `Ensure`; the file flavours, `jsonata`
  and `falid`, are not built yet). A file can name a function, and whoever runs it must supply that function,
  or validation fails before any input is loaded.

### `Node` is the working form; the format lives at the edges

YAML and JSON both parse to the same `Node` tree, and nothing in Fergi knows which it came from. Adapters turn
text into `Parsed` (the tree plus source locations) and back. This is why a mapping works the same over YAML
in and JSON out, and why converting to JSON text first would add nothing and lose locations, timestamp text and
YAML-only values. Cost: `Node` carries no comments, so they do not survive a mapping.

### The core is pure and Fable-safe

`Fnode` and `Fergi` have no IO, no reflection and no dependencies the Fable targets cannot compile, so the same
source runs on .NET, Fable JS and Fable Python, and the shared tests run on all three. Reading and writing is
the injected `Loader` (`string -> Result<Parsed, Error list>`); `run` returns the result and writes nothing;
`plan` returns the ops that would change the output. A library that cannot be Fable-safe (`Jsonata.Net.Native`
is .NET-only) lives in its own package and plugs in through `Mapping.Functions`; it never goes into the core.

### Failures are values

Operations return `Result<_, Error list>`. An `Error` has a stable code, a path, an optional source location
and context entries (the rule index and kind, the input, the file, the function). Validation reports every
problem it can find before any file is loaded, and a run stops at the first failing rule, so later rules
never see a state that did not exist. A registered function is expected not to throw, but the engine does not
trust that: an exception inside one becomes a `compute.failed` error like any other failure.

## Mapping file shape

`Mapping.toNode` writes a mapping as a `Node` and `Mapping.ofNode` reads it back (`Mapping.ofParsed` also fills
each error's line and column from the parser's spans). They live in `src/Fergi/Mapping.fs`, are pure and
Fable-safe, and the existing adapters do the text. Generated files should default to JSON: no quoting
questions, easy to diff. `docs/mapping.schema.json` is the JSON Schema for editors and other producers.

```yaml
version: 1
inputs: [person, org]
inputLists: [services]            # only when there are any
rules:
  - layer: [org, person]
  - listsBy: { path: /ports, key: name }
  - copy: { from: "person:/name", to: /metadata/name }
  - set: { to: /kind, value: service }
  - compute:
      to: /fullName
      fn: join
      args:
        - { value: " " }
        - { from: "person:/first" }
        - { doc: /last }
  - collect:
      input: services
      into: /services
      entry: [ { copy: { from: "services:/name", to: /name } } ]
      sortBy: /name
```

- **A rule is an object with one key** naming the rule, whose value holds its fields: the F# union case as data.
  The rules are `layer`, `listsBy`, `conflict` (`lastWins`, `firstWins`, `fail`), `nullDeletes`, `copy`,
  `rename`, `defaultTo`, `set`, `drop`, `collect` and `compute`.
- **Arguments are tagged**, because a bare string cannot say whether it is a pointer or a literal. `from` is a
  pointer into a named input (`input:/path`, as `Arg.Input`), `doc` is a pointer into the document built so
  far (`Arg.Doc`), `value` is a fixed value (`Arg.Literal`), which YAML types natively: ints, bools and
  nulls survive, and `inputs: true` is every input as one object keyed by name (`Arg.Inputs`).
- **`version`** is required and must be `1`; another number is `mapping.version`. Files outlive the code that
  reads them. A top-level `$schema` key is accepted and ignored.
- **Reading reports every problem it finds**, each with the `Path` of the place in the file (and, through
  `ofParsed`, the line and column). It checks shape, types and selector syntax. It does not check that input
  and function names are declared: `Fergi.run` does, and the standalone `Mapping.validate` does (`Jsonata.validate`
  adds expression syntax on .NET).
- **Writing fails, listing every offender, when the mapping holds something a file cannot say**
  (`mapping.notSerialisable`): an `Ensure` closure hook (code, in-process only), a function registered with `Mapping.withFunction` (it
  runs only in the process that registered it; the built-ins and the name `jsonata` are written by name, and
  an unregistered name is written too, since validation reports it), and a selector or pointer that does not
  read back as itself (a `Where` key containing `=`, a numeric object key that would read back as an index).
- **Not in the file:** `Ensure` closure hooks, the code of any custom function, and `Files` (where the inputs live; its
  shape is not decided). A `Timestamp` literal is a string once it has been through JSON text.
- **`jsonata` sugar:** `open Fergi.Jsonata` adds a `jsonata "/dest" "expr"` operation to the `mapping` builder. It
  writes a `compute` of `jsonata` over `[ value expr; inputs; doc "" ]` and registers the function (the builder's
  `func` operation registers any other host function). In the expression `$` is the inputs object and `$doc` the
  output so far.

`ofNode (toNode m)` gives back the inputs and rules of `m`, and a mapping read from a file runs like the one it
was written from (both are tested, through JSON and YAML text, on .NET, Fable JS and Fable Python).
