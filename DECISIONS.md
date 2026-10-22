# Decisions

A light log of decisions that would be expensive to rediscover. Newest first. Each entry is a few lines:
what we decided, why, what we rejected, and what would make us revisit it. It is not a design document; link
to one when there is one. `STATUS.md` holds the current state and the Next list, and the shorter decisions
that are plain facts about the code.

To add one, copy the shape below and put it at the top. Change `Status` rather than deleting an entry that is
no longer true (`Superseded by <title>`), so the reasoning stays findable.

```
## <short title>
Date: YYYY-MM-DD. Status: Accepted | Spike | Superseded by <title>.
Decision: one or two sentences.
Why: the constraints and the evidence.
Rejected: option, and why not.
Costs: what we accepted by choosing this.
Revisit if: the signals that should reopen it.
See: files, docs or commits.
```

## The CLI names its inputs with flags; the last argument is not the output
Date: 2026-10-22. Status: Accepted.

Decision: `fergi run <mapping> -i [name=]<file> ... -l name=<file or glob> ... -o <file> [--dry-run]` and
`fergi validate <mapping>`. `-i` binds a single input (`name=file`, or the file name without its extension when
there is no `=`), `-l` appends to a list input (repeat it; the value may be a glob, expanded sorted), `-o` is
exactly one output. JSONata is registered. Output is written through a temp file and a rename, and only after
the whole run succeeded. An output that is also an input or the mapping file is refused.

Why: inputs are named in the mapping and rules read them as `name:/path`, so the CLI has to bind a file to a
name; `Files` has one `Output`. Flags put the destination in the command line explicitly and give input lists
a delimiter.

Rejected:
- **`run mapping in... out` with the last argument as the output**: forgetting one input overwrites it (a glob
  that matches the output does the same), input lists have no end marker, and positional binding by the
  mapping's `inputs` order silently rebinds files when the mapping is reordered.
- **Binding by the mapping's declaration order**: the same silent rebinding.
- **A comma-separated `-l`**: breaks paths with commas; repeating the flag does not.
- **A parsing library (System.CommandLine)**: two commands and five flags do not need a dependency.

Costs: more typing than the positional form (the derived name keeps the common case short); glob expansion
and the flag grammar are ours to maintain; the CLI is .NET only because of JSONata (it stays out of the Fable
suite like `Fergi.Jsonata`); the executable is `Fergi.Cli` because the `fergi` assembly name clashes with the
`Fergi` project name.

Revisit if: a multi-output mapping is added (`-o` would need a name), a file form for `Files` lands (item 17,
the flags may then read from it), or the CLI is packaged as a tool (then name the command `fergi`).

See: `src/Fergi.Cli/`, `tests/Fergi.Cli.Tests/`.

## Ensure comes in flavours; the closure flavour is in-process only
Date: 2026-10-16. Status: Accepted (closure and `jsonata` flavours built; `falid` is not).

Decision: `Ensure` has three flavours. `fn` (a closure, `Node -> Result<unit, Error list>`) is for people who
want their own language instead of JSONata or the built-ins; it exists only in the program that runs the
mapping and is never written to a file. The file flavours are `jsonata` (a predicate expression) and `falid`
(a reference to a Falid rules file, which serialises to data like Fergi). A closure hook that throws is
reported as `ensure.failed` instead of taking the run down.

Why: the file form has to stay plain data, so a closure can never be in it, but the programmatic use is worth
keeping as it is. Falid is a separate library that already has a data form, so it fits as its own flavour
rather than being folded into Fergi.

Rejected: registering closures under names so a file can reference them (a file could then name a hook another
program never registered, and `toNode` could no longer refuse a hook it cannot write); replacing closures with
a name registry (breaks the `ensure` builder API for no gain); one declarative check rule for everything (too
small for what Falid does).

`jsonata` (built 2026-10-22): `Ensure` is a list of `(name, EnsureCheck)` where `EnsureCheck = Fn | Jsonata`, so
`falid` is one more case. A file carries `ensure: [ { "name", "jsonata" } ]` (additive, `version` stays 1). `$` is
the final output document only (no inputs), as for closure hooks. The predicate must return a boolean: false,
a non-boolean and no value (undefined, e.g. `owner = 'x'` with no `owner`) all fail with `ensure.failed`.
Core evaluates it through the registered `jsonata` function, so it stays Fable-safe; an entry with no such
function registered is `mapping.functionUnknown` in `validate`, and `Jsonata.validate` checks its syntax.

Costs: three mechanisms to document and validate. A file never carries closure checks, so a mapping that relies
on one runs checked only where the host supplies it.

Revisit if: the `jsonata` and `falid` flavours turn out to need a shared check shape, or hosts keep wanting to
name closures from files.

See: `STATUS.md` items 12, 18, 19, `src/Fergi/Run.fs` (`runEnsure`), `src/Fergi/Mapping.fs` (`toNode`).

## Mapping file format: one-key rules, tagged arguments, the codec in core
Date: 2026-10-15. Status: Accepted.

Decision: a mapping serialises as `{version, inputs, inputLists?, rules}`. A rule is an object with one key
naming the rule; an argument is `{from}`, `{doc}` or `{value}`. `Mapping.toNode` / `ofNode` / `ofParsed` live in
`Fergi` (over `Node`, so Fable-safe). Writing fails with one `mapping.notSerialisable` error per offender
(an `Ensure` hook, a host function, a path that does not read back) instead of dropping them.

Why: the file is the contract for producers in other languages, so it has to be explicit and versioned. One-key
rules map one to one onto the union cases and keep the schema flat. Tagged arguments keep a pointer apart from
a literal string. Failing loudly means a producer learns at write time that the file would not mean the same
thing, rather than at run time.

Rejected: a `type` field inside each rule (`{type: copy, ...}`): same information, more room for a missing or
misspelled tag. Silently dropping hooks and host functions: the written file would run differently from the
mapping. Checking input and function names in `ofNode`: the names are validated at run, and a reader that
needs the registry cannot be a plain decoder; `Mapping.validate` is the place.

Costs: the field names are now a public contract (bump `version` to change them). `Files` has no file form yet.

Revisit if: a producer needs a second file version at once, or the one-key shape is awkward in a language's
type system.

See: `ARCHITECTURE.md` (Mapping file shape), `docs/mapping.schema.json`, `tests/Fergi.Tests/MappingFileTests.fs`.

## Mapping files are generated: no YAML sanitiser, no expression files
Date: 2026-10-15. Status: Accepted.

Decision: the serialised mapping is produced by a program and written through the YAML or JSON printer, so
the writer does the quoting. We do not pre-process hand-written YAML to repair expressions, and we do not
add an `expr-file` key that loads an expression from a separate file. Hand-writing is supported only by
documenting the pitfalls (`docs/jsonata.md`: use a block scalar or quote the whole expression).

Why: JSONata uses characters YAML treats specially (`: `, ` #`, quotes, `{`, a leading `&`). A plain-scalar
` #` silently cuts the expression short, so the dangerous case produces valid but wrong output, not an error.
A text pre-pass would have to guess intent (`#` is also JSONata's positional operator), would shift error
columns, and would turn the files into a YAML dialect that other tools misread. A generating program has none
of these problems.

Rejected:
- **A sanitising pre-pass over the YAML text**: guesses intent, breaks locations, makes a dialect.
- **`expr-file`** (an expression in its own `.jsonata` file): it would avoid the quoting problem and give
  syntax highlighting, but it only matters for hand-written files, which we do not expect.
- **Lint for a plain scalar whose source line contains ` #`**: possible from `Parsed.Locations`, and the
  right fix if hand-written files turn out to be common.

Costs: a hand-written mapping can silently truncate an expression until someone reads the docs.

Revisit if: people write mapping files by hand in practice. Then add the lint first, `expr-file` second.

See: `ARCHITECTURE.md` (mapping file shape), `docs/jsonata.md` (hand-writing a mapping file).

## JSONata is the expression language for computed values in mappings
Date: 2026-10-15. Status: Spike (works on .NET; the Fable JS and Python bindings are not written).

Decision: a mapping that needs to transform a value or combine fields calls a registered function by name.
Simple cases use the built-ins (`concat`, `join`, `upper`, ...). Anything richer uses the `jsonata` function,
whose first argument is a JSONata expression string: `Rule.Compute { Fn = "jsonata"; ... }`. The JSONata
library for each runtime lives in its own package outside the core, and plugs in through `Mapping.Functions`.

Why:
- Mappings must stay plain data so a CLI can run them from a file, so the rule can hold a function *name* and
  a string, never a function value.
- The core must stay Fable-safe (no dependencies, no reflection), so the expression engine cannot live in it.
- JSONata is pure (no IO, no access to the host), was made for reshaping JSON, and has implementations for
  .NET, JS and Python, which are the runtimes we target.
- It works on our `Node` values through a thin conversion, and `Int` stays an exact int64 on .NET.

Rejected:
- **F# functions inside `Rule`**: not serialisable. They stay available through code-registered functions.
- **Named built-in functions only**: kept as the base layer, but too small for real transforms.
- **Jint (JavaScript strings)**: works and can be sandboxed with a timeout, memory and statement limits, but
  it is .NET-only (so it needs its own package like JSONata does), and it puts a second general-purpose
  language in every mapping. Still the escape hatch if JSONata proves too limiting.
- **Interpreting F# (compiler services or FSI)**: heavy, seconds of cold start, runs arbitrary .NET code from
  a mapping file (no sandbox), and cannot run under Fable.
- **Other expression languages (JMESPath, CEL, Liquid)**: named, not evaluated.

Costs:
- The implementations are not identical: 17 of 90 conformance cases differ between .NET, JS and Python.
  Mappings that must behave the same everywhere have to stay in the portable subset in `docs/jsonata.md`.
- JS and Python run forever on a runaway expression unless bounds are set on every evaluation; .NET stops
  recursion itself but has no timeout option.
- The npm `evaluate` is async, so the JS target needs an async variant of the function registry.
- Three libraries to keep in step, with their versions pinned and the suite re-recorded on each bump.

Revisit if: the portable subset proves too small for the mappings people actually write; the .NET port
(`Jsonata.Net.Native`) stops being maintained; the async JS requirement makes the registry unworkable; or users
want general-purpose logic that JSONata expresses badly (then add Jint as an opt-in).

See: `docs/jsonata.md`, `tests/jsonata-conformance/`, `src/Fergi.Jsonata/`, commits `2fa0452` and `5657504`.
