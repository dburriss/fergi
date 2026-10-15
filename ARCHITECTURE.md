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
  not an F# API. The format does not exist yet (`STATUS.md` Next, item 7).
- **No language depends on another's builders.** The F# builders compile under Fable to JS and Python, but the
  generated names carry hash suffixes and are not a usable API (a builder call becomes nested
  `MappingBuilder__Compute_3B2A1428(...)` calls). A Python or TypeScript author writes the data form, or a
  mapping file, not the transpiled builders.
- **Anything that cannot be data stays beside the rules and is referenced by name.** Custom functions are
  registered under a name and called by `Rule.Compute`; `Ensure` hooks are a separate field. A file can name a
  function, and whoever runs it must supply that function, or validation fails before any input is loaded.

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
never see a state that did not exist.

## Mapping file shape (sketch, not built)

Status: a proposal for the serialised form of `Mapping`. Nothing reads or writes it yet (`STATUS.md` Next,
items 7 and 8), and the field names below are not agreed. It is recorded so the next session starts from it.

The file is itself a document, so it is a `Node` and can be YAML or JSON. `Mapping.toNode` / `Mapping.ofNode`
would convert, and the existing printers and parsers do the text. Generated files should default to JSON: no
quoting questions, easy to diff.

```yaml
version: 1
inputs: [person, org]
rules:
  - copy: { from: "person:/name", to: /metadata/name }
  - set: { to: /kind, value: service }
  - compute:
      to: /fullName
      fn: join
      args:
        - { value: " " }
        - { from: "person:/first" }
        - { from: "person:/last" }
  - jsonata:
      to: /owner
      expr: org.teams[id = $doc.teamId].name
```

- **A rule is an object with one key** naming the rule, whose value holds its fields: the F# union case as data.
- **Arguments are tagged**, because a bare string cannot say whether it is a pointer or a literal. `from` is a
  pointer into a named input (`input:/path`, as `Arg.Input`), `doc` is a pointer into the document built so
  far (`Arg.Doc`), and `value` is a fixed value (`Arg.Literal`), which YAML types natively: ints, bools and
  nulls survive.
- **`jsonata` is sugar** for `fn: jsonata` with the expression as a literal and the context set to all inputs
  as one object keyed by input name, with `$doc` for the output so far. That needs an `Arg.Inputs` case that
  does not exist yet. See `DECISIONS.md` and `docs/jsonata.md`.
- **`version`** is required: files outlive the code that reads them.
- **Only `copy`, `set`, `compute`, `jsonata` and `layer: [names]` were sketched.** The other rules (`ListsBy`,
  `Conflict`, `NullDeletes`, `Rename`, `DefaultTo`, `Drop`, `Collect`) follow the same pattern; their field
  names are open. `Files` (where the inputs live, and the output) is data too, and its shape was not sketched.
- **Not in the file:** `Ensure` hooks, and the code of any custom function. A file names a function, and
  whoever runs it supplies the function (`mapping.functionUnknown` otherwise).

What the format must guarantee: `ofNode (toNode m) = m` for every serialisable mapping, validation errors that
point at the file location, and the same behaviour under Fable JS and Python (the codec works on `Node`, so it
can live in the core). A generated file gets its quoting from the printer; hand-writing rules for YAML are in
`docs/jsonata.md`.
