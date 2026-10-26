# Status

Snapshot of the Fnode / Fergi work. Design sources live in the `second-brain` vault: `Shared Document Core Design.md` (Fnode, the shared core) and `Fergi.md` (the merge/mapping library). Findings from the spike are in `docs/fergi-spike.md`; Fable notes are in `docs/fable.md`; how the pieces fit is in `ARCHITECTURE.md`; larger decisions with their rejected alternatives are in `DECISIONS.md`; rules for coding agents are in `AGENTS.md`.

Last verified: 2026-10-24 on the YamlValue 0.2.1 package reference. `dotnet build Fergi.slnx -c Release` has 0 warnings. `dotnet test Fergi.slnx -c Release` passes 775 tests. `scripts/fable-test.sh all` passes 710 entries on Fable JS and on Fable Python. Nothing is published.

## Delivery plan (from the Fnode design doc)

| Step | Work | State |
|---|---|---|
| 1 | Core spike: `Node`, `Path` (JSON Pointer), `Node.tryGet/set/remove`, `Error`, `Parsed`, `Format` | [x] Done |
| 2 | `Fnode.Yaml` on .NET | [x] Done, now also with positions and original timestamp text |
| 3 | JSON library (`JsonValue`) plus `Fnode.Json`, shared conformance suite | [x] Done. The JSON Test Suite passes with zero deviations |
| 4 | Back-port to YamlValue: positions, timestamp text, Fable | [x] Done and released as `FSharp.Data.YamlValue` 0.2.1. Our projects also compile and test under Fable JS and Python |
| 5 | Fergi spike consumes the core, IDP defaults-plus-overrides case end to end | [x] Done, with a mapping data form, real YAML/JSON fixtures and provenance |
| 6 | Publish `0.x` alpha prereleases of core and adapters | [ ] **Not started**. The P1/P2 API additions are in, so the core API shape is ready |
| 7 | Falid consumes the core (`file:line:col` in the CLI) | [ ] Not started |

## What is in this repo

| Project | Purpose |
|---|---|
| `src/Fnode` | Core: `Node`, `Path`, `Error`, `Parsed`, `Format`, `Node.tryGet/tryFind/exists/set/setCreating/remove`, key helpers, `Node.equals`. No dependencies |
| `src/Fnode.Yaml` | YAML adapter over `FSharp.Data.YamlValue`. Fills `Parsed.Locations`, keeps timestamp text, parse errors carry a location |
| `src/JsonValue` | Standalone JSON library (placeholder namespace `Fergi.Json`), positions built in, no dependencies |
| `src/Fnode.Json` | JSON adapter over `JsonValue` |
| `src/Fergi` | Selectors with `Where`, `Doc`, `Patch` (atomic apply, diff), `Policy`, `Doc.merge`/`layers` with provenance, `Mapping`/`Files` data form, `run`/`plan`/`runFiles`, aggregation (`inputList`, `Rule.Collect`), the file form (`Mapping.toNode`/`ofNode`/`ofParsed`, schema in `docs/mapping.schema.json`), computed values (`Rule.Compute`, named functions from `Mapping.Functions`, built-ins in `Functions.builtins`), and the CEs `ops`, `docs`, `files`, `entry`, `mapping` |
| `src/Fergi.Cli` | The command line (`Fergi.Cli`, .NET only, Argu, structured like eru's CLI): `validate <mapping>` and `run <mapping> -i [name=]file -l name=file/glob -o file [--dry-run]`, JSONata registered. See `DECISIONS.md` |
| `src/Fergi.Jsonata` | **Spike, .NET only.** Registers a `jsonata` function (`Jsonata.Net.Native`) for `Rule.Compute`. There are no Fable (JS or Python) bindings yet. See `docs/jsonata.md` |
| `tests/*` | Unit and property tests per project, `Fnode.Conformance` (shared YAML+JSON cases), `Fnode.Fable` (Fable harness), `Fergi.Tests` (includes the end-to-end billing case under `tests/Fergi.Tests/data/`) |
| `tests/jsonata-conformance` | 90 JSONata expressions run through the .NET, JS and Python libraries with recorded expectations (73 portable, 17 divergent). `scripts/jsonata-conformance.sh` runs it |
| `scripts/fable-test.sh` | Runs the shared test sources under Fable JS and Python (`js`, `python` or `all`) |

Test counts on .NET: Fnode 30, JsonValue 323, Fnode.Json 11, Fnode.Yaml 57, Conformance 47, Fergi 259, Fergi.Jsonata 21, Fergi.Cli 27. Fable runs 710 entries per target (Conformance counts differently there, every case still runs).

## Decisions made

- `Int of int64` stays. Under Fable int64 is exact on JS and Python (values past 2^53 and int64 min/max round-trip through JSON and YAML).
- `Timestamp of string` holds the original ISO text. Core is pure and Fable-safe, with no reflection, IO or dependencies.
- `Where` is not in core `Path`. It is a Fergi selector with `[key=value]` segment syntax.
- `Fnode.Yaml` uses the `FSharp.Data.YamlValue` 0.2.1 NuGet package, which ships its F# sources under `fable/` so Fable JS and Python work from the package. `Directory.Build.props` pins `FSharpCoreImplicitPackageVersion` to 10.1.402 because YamlValue needs it and this SDK implies 10.1.401.
- Computed values are `Rule.Compute { Fn; Args; Dest }`: only the function **name** is in the rule, so mappings stay serialisable for a CLI. Functions are pure `Node list -> Result<Node, string>` held in `Mapping.Functions` (default `Functions.builtins`; add with `Mapping.withFunction`). Unknown names fail validation before any file loads.
- The mapping form separates mapping and files (the `files` variant in `Fergi.md`). `Rule` is plain data. `Ensure` hooks are a separate field so `Rule` stays serialisable. `Resolve` conflict handlers stay code-only.
- `Op`, `ListStrategy`, `OnConflict`, `Rule` and `ConflictMode` all use `[<RequireQualifiedAccess>]`.
- Loader signature is `string -> Result<Parsed, Error list>`. Merge conflicts carry the source file and a line and column.
- `Ensure` comes in flavours: `fn` (a closure, in-process only, never in a file), `jsonata` and `falid` (the file flavours, not built). See `DECISIONS.md`.
- Commits: conventional prefixes, no AI attribution lines.

## Related state in `../fsharp-yaml`

Everything Fnode needs is released in `FSharp.Data.YamlValue` 0.2.1: surrogate-pair escapes, quoting of `<<` and `...`, `!!float` tagging so every finite float round-trips, `ParseWithPositions`, `ParseWithMetadata` (original timestamp text) and Fable JS and Python support (sources under `fable/` in the package).

## Known limits and findings

- **JSONata is not identical across runtimes.** 17 of 90 conformance cases differ between the .NET, JS and Python libraries (large ints, `$string` of non-integers, non-BMP strings, `$sort` ties, `$number("NaN")`, date pictures, runaway-recursion handling). The portable subset and the divergence table are in `docs/jsonata.md`; the Python and JS bindings for Fable are not written.

Full detail and proposed fixes are in `docs/fergi-spike.md`.

- **Merge is not associative in general**, even under LastWins/Replace/NullDeletes=false (a key flipping between object and scalar). It holds for shape-stable documents. `NullDeletes=true` and `Fail` break it further. "Monoid, so fold works" only holds under a restricted policy.
- Provenance covers only the layer stage. Nodes created by later rules have no entry.
- `Patch.diff` treats arrays atomically and emits a whole-object `Set` when key order changes.
- `Where` matches string values only, and a key that literally looks like `[a=b]` is not addressable.
- A numeric object key prints the same as an `Index` in a JSON Pointer, so such keys are ambiguous in provenance and `ListRules` paths. `Path.resolve` normalises a path against a document; the printed form stays ambiguous.
- A mapping cannot yet set a default list strategy or non-`ByKey` list rules (only `ListsBy`).
- `Fnode.Yaml.print` keeps original timestamp text through a marker-substitution workaround, because YamlValue's emitter only prints a `DateTimeOffset`. `YamlConvert.ofNode` on its own still loses it.
- Fable differences: float text differs by target (`1E+21` on .NET, `1e+21` on JS and Python), Python counts an astral character as one column unit, non-ISO timestamp text stays a `Str` under Fable, and the FsCheck runs use a seeded shim with no shrinking.
- Open YamlValue finding: YAML integral exponent scalars (`1e16`, `2.5e3`) parse as `Int`, while JSON gives `Float`.
- Environment: Node is not installed here, so JS runs use bun. Python runs use a 3.13 venv and need network on first run.

## Open checks (never run)

- **YAML comments are lost on output.** `Node` carries no comments, so a mapped YAML file loses them. Not yet confirmed as acceptable for the files this will be used on.
- **Bare `no` / `on` in input YAML.** Not tested whether `Fnode.Yaml` reads them as bools (YAML 1.1) or strings (1.2).

## Next

1. [x] **Priority-1 Fnode API fixes before publishing** (from the findings doc): `Path.escapeSegment`/`unescapeSegment`/`parseSegment`, `Node.setCreating` and `Node.exists`, `Node.tryFind` (`Ok None` is absent, `Error` is a mismatch), and the NaN-safe `Node.equals` (plain `=` is unchanged). Checked against the code on 2026-10-24.
2. [x] Priority-2 fixes: `Node.tryFindKey`/`upsertKey`/`removeKey`/`hasDuplicateKeys` (duplicate keys stay allowed in `Obj`), `Error.Context` with `withContext`/`mapMessage`/`describe`, and `Path.resolve` for the numeric key vs `Index` ambiguity (the ambiguity itself is documented in `Path.fs`, not removed). Items 5 and 9 of the findings doc stay P3, documentation only.
3. [ ] **Step 6:** check package names on NuGet, npm and PyPI (`Fnode` is a placeholder), decide the final name of `JsonValue`, then publish `0.x` alphas.
4. [ ] Step 7: Falid consumes the core.
5. [ ] Fergi features (see `docs/fergi-spike.md` section 7):
    - [x] The computation expressions and custom operations (confirmed under Fable JS and Python).
    - [x] Aggregation across repos (use case 6).
    - [ ] Three-way merge (use case 5).
    - [ ] A default list strategy in the mapping form.
    - [ ] Provenance for rule-produced nodes and aggregated entries.
    - [ ] `uniqueBy` and a keyed-object output for aggregation.
6. [ ] Update the `second-brain` notes with the amendments listed in `docs/fergi-spike.md` (`Policy.ListRules`, `Doc.get` returning `Result`, `Policy.defaults`, the `Where` selector, the Rule policy cases, the merge-law caveat), plus `Rule.Compute`, `Arg`, the `Functions` registry on `Mapping` and the JSONata decision (see `DECISIONS.md`), which change the "`Rule` is plain data" story in `Fergi.md`. `docs/fergi-spike.md` has no section on computed values yet either. Nothing in the vault was edited by this work.
7. [x] **Serialised mapping format**: `Mapping.toNode` / `ofNode` / `ofParsed` in `src/Fergi/Mapping.fs`, JSON or YAML text through the adapters, `version` 1, one-key rules, tagged arguments (`{from}`, `{doc}`, `{value}`), a JSON Schema in `docs/mapping.schema.json`. Round trips and runs from a file are tested through JSON and YAML text on .NET, Fable JS and Python. Reading reports every problem with a path (a line and column through `ofParsed`). Writing returns one `mapping.notSerialisable` error per offender (`Ensure` hook, host function, a path that does not read back). Shape in `ARCHITECTURE.md`, reasoning in `DECISIONS.md`. The schema is checked against the reader by `scripts/mapping-schema.sh` (`tests/mapping-schema`: 11 valid and 38 invalid case files plus the files `toNode` writes; the schema and `ofParsed` must agree on every one). It found two gaps (bad `~` escapes) that are fixed.
8. [x] **`Mapping.validate`** (`src/Fergi/Mapping.fs`): checks a mapping without `Files` or a loader, for a program that generates mapping files. It reports every problem: undeclared input names, an `inputList` used outside `Collect`, a `Collect` entry reading another input, a second `Layer`, `Conflict` or `NullDeletes` rule, and unknown function names against `m.Functions`. `Fergi.run` calls it too. `Jsonata.validate` (`Fergi.Jsonata`, .NET only) adds the syntax of every `jsonata` rule whose expression is a literal string (`compute.failed`, with the library's message and the context `rule`, `function`, `expression`). Not done: it does not yet tell portable functions from ones that exist only where registered (item 14), and the JSONata check has no Fable binding (item 11).
9. [x] JSONata in the builders: `Arg.Inputs` (all inputs as one object keyed by input name; `{ inputs: true }` in the file form and the schema), `ArgSpec.AllInputs`, a `func` operation on the `mapping` builder (registers a host function; `MappingState` now carries `Functions`), and a `jsonata "/dest" "expr"` operation added by `open Fergi.Jsonata` (registers `jsonata`; `$` is the inputs, `$doc` the output so far, the third argument of the `jsonata` function). Not added to `entry`. A whole-output `Rule.Transform` was discussed and deliberately left out.
10. [ ] JSONata errors:
    - [x] Exceptions from the library and non-value results (a function) are `compute.failed`. A thrown exception from any registered function is also caught by `Rule.Compute`.
    - [ ] Carry the position inside the expression in the error.
11. [ ] Fable bindings for JSONata: Python (`[<Import>]` of `jsonata-python`, synchronous) and JS (the npm `evaluate` is async, so the registry needs an async variant). Neither is written, and neither is confirmed to work under Fable.
12. [ ] Decisions open on JSONata (see `docs/jsonata.md`): lint mappings down to the portable subset or allow all of JSONata, and an external time limit for .NET (the library has no timeout option). How `Ensure` hooks are expressed in a file moved to items 18 and 19.
13. [ ] Conformance suite: run it in CI (the repo has no CI config; the first run needs network), and re-record it with `scripts/jsonata-conformance.sh record` whenever a JSONata library version changes.
14. [ ] **Producers in other languages** (the driver is a Python producer of mapping files; JS, TS and more languages expected later). The CLI (`src/Fergi.Cli`, built) is the only executor, so a producer only has to write the file; the mapping format plus its schema is the contract. Two ways to build the producer API, undecided:
    - **Hand-written per language** (about 100 lines of dicts and JSON): idiomatic, no runtime dependency, but each language is a new builder and can drift from the reader.
    - **F# facade compiled by Fable**: one source, the format defined once by `Mapping.toNode`, a few `#if` lines per language. Findings from a scratch experiment (not in this repo): the generated `Builders.fs` output is unusable as an API (computation expressions are compiler sugar, so JS and Python get hashed names like `OpsBuilder__Yield_42ADAC0B` and `FSharpList`/`FSharpResult` types). `[<AttachMembers>]` on a class gives real classes with working chaining. `[<CompiledName>]` inside `#if FABLE_COMPILER_PYTHON` / `#if FABLE_COMPILER_JAVASCRIPT` gives different names per target (`to_json` vs `toJson`) from one source. The facade must take and return only strings, numbers, arrays and dicts. Not checked: records, unions, static members, the quality of the type hints, the TypeScript target, and any language Fable does not support (it would need a hand-written builder). Users would also need `fable-library` and a release per ecosystem.
    - **Later option, a consumer facade:** the same compiled core could also run mappings in-process (`run`, `plan`, `Doc.merge`) from Python, JS or TS without the CLI. Not planned until someone has a concrete use. It is a bigger API than the producer one (documents, errors with positions, provenance, plans, policies), and the JSONata runtimes disagree on 17 of 90 cases, so in-process runs would have to stick to the portable subset (or treat the .NET result as the reference). The Fable bindings for JSONata (item 11) are not written. Two executors need the corpus below run against every runtime, not only the CLI.
    - **Host functions instead of JSONata, in-process:** the consumer facade would let a host-language lambda be registered by name in `Mapping.Functions` (the existing `Node list -> Result<Node, string>` registry), so a Python or JS user computes values in their own language and avoids the JSONata divergence. A file can only reference such a function by name, so mappings split into portable ones (built-ins, JSONata) and ones that only run where the function is registered; `Mapping.validate` (item 8) should report which, so a producer knows before writing the file. The registry is synchronous, so an `async` JS function needs an async variant (same issue as item 11). A host exception or throw should become `compute.failed` with the original message. `each` and `Ensure` lambdas have the same in-process-only limit. Not built.
    - Either way, keep a shared corpus of mapping files and expected outputs, so every producer is checked against the same files. Decide after item 7, with a facade experiment against the real `Mapping.toNode`.
15. [ ] **JSONata on .NET can return an invalid string.** `$substring` on a non-BMP character returns half of a surrogate pair without an error (found by the conformance suite; JS and Python return the whole character). The mapping then holds a corrupt string. Decide whether the JSONata boundary should reject results containing an unpaired surrogate (a `compute.failed`), and check what the YAML and JSON printers do with one.
16. [ ] Cache the parsed `JsonataQuery` per expression: it is documented as immutable and thread-safe, and `Jsonata.eval` re-parses it on every call. No cost has been measured.
17. [ ] **A file form for `Files`** (where the inputs live and the output goes). The mapping file does not cover it; the CLI takes it from flags (`-i`, `-l`, `-o`) for now, so this is no longer blocking. A file form would let a run be repeated without a long command line. Decide whether it sits in the same file or a second one, then add `Files.toNode` / `ofNode` with the same error rules as the mapping codec.
18. [x] **`Ensure` flavour: `jsonata`.** Done: `EnsureCheck = Fn | Jsonata`, file field `ensure: [ {name, jsonata} ]`, `ensureJsonata` builder operation in `Fergi.Jsonata`, `Jsonata.validate` checks the syntax; the predicate must return a boolean (undefined fails). See `DECISIONS.md`. Original brief: (The `fn` closure flavour is done: `Mapping.ensure`, in-process only, never in a file; a throwing hook is reported as `ensure.failed`.) An ensure entry in the mapping file holding a literal JSONata expression used as a predicate over the output document. Only as portable as the JSONata subset (item 12), and `Jsonata.validate` should check its syntax. It reports through the same `ensure.failed` error as the closure flavour. The closure signature does not carry over, so the check shape for a file entry is decided here.
19. [ ] **`Ensure` flavour: `falid`.** An ensure entry that points at a Falid rules file (or holds the rules inline; undecided). Falid also serialises to a data file, so the mapping stays plain data, and errors can carry `file:line:col`. Needs a check interface in Fergi and the adapter in its own project (for example `Fergi.Falid`), so Fergi core does not reference Falid. Depends on step 7.
20. [x] **The CLI** (`src/Fergi.Cli`, tests in `tests/Fergi.Cli.Tests`): `validate` and `run` with named inputs, input lists with globs, one output, `--dry-run`, JSONata registered. Not done: a `fergi` command name and a release (item 21), multiple outputs (the core has one `Output`), a `--format` override, reading `Files` from a file (item 17), and `validate` checking a set of `-i`/`-l` files against a mapping without running it. Decision in `DECISIONS.md`.
21. [ ] **CLI release** (separate from step 6, which is the core packages `Fnode`, `Fnode.Yaml`, `Fnode.Json`, `JsonValue`). Today the executable is `Fergi.Cli` and is run with `dotnet run` or a self-contained publish; nothing is packaged, versioned or published. To decide and do:
    - **Form.** Self-contained single file (`-r <rid> --self-contained -p:PublishSingleFile=true`): tested on osx-arm64, runs `validate` and `run` with a JSONata ensure, about 78 MB, no .NET install needed. Or a dotnet tool (`PackAsTool`, `ToolCommandName` `fergi`, which also fixes the `fergi` assembly name clash with the `Fergi` project, but needs the .NET runtime). Native AOT is ruled out for now: Argu's union reflection fails at runtime even with trimmer roots and an `rd.xml` (tested, recorded in the CLI entry in `DECISIONS.md`). Revisit only with the hand-rolled parser and an AOT check of JSONata and the YAML adapter.
    - **Platforms.** Only osx-arm64 has been built and run. Decide the set (for example osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64) and smoke-test each.
    - **Distribution.** GitHub releases with checksums, or NuGet for the tool form. Wrappers for npm and PyPI only if producers in those ecosystems want a one-line install (item 14); they would ship the binary, not reimplement it.
    - **Version and `--version`.** There is no `--version` command and no version policy yet. Add one (eru's `VersionCli` is the pattern) and decide whether the CLI is versioned with the core packages or on its own.
    - **CI.** The repo has no CI (item 13). A release build needs one.
    - **Dependency on step 6.** A self-contained binary built from source does not need the core packages published. A NuGet tool package bundles its dependencies, so it does not either, but publishing the core first keeps one story for versions.
