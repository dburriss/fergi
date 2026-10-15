# Status

Snapshot of the Fnode / Fergi work. Design sources live in the `second-brain` vault: `Shared Document Core Design.md` (Fnode, the shared core) and `Fergi.md` (the merge/mapping library). Findings from the spike are in `docs/fergi-spike.md`; Fable notes are in `docs/fable.md`; how the pieces fit is in `ARCHITECTURE.md`; larger decisions with their rejected alternatives are in `DECISIONS.md`; rules for coding agents are in `AGENTS.md`.

Last verified: 2026-10-15. `dotnet build Fergi.slnx -c Release` has 0 warnings. `dotnet test Fergi.slnx -c Release` passes 709 tests. `scripts/fable-test.sh all` passes 677 entries on Fable JS and on Fable Python. Nothing is published.

## Delivery plan (from the Fnode design doc)

| Step | Work | State |
|---|---|---|
| 1 | Core spike: `Node`, `Path` (JSON Pointer), `Node.tryGet/set/remove`, `Error`, `Parsed`, `Format` | Done |
| 2 | `Fnode.Yaml` on .NET | Done, now also with positions and original timestamp text |
| 3 | JSON library (`JsonValue`) plus `Fnode.Json`, shared conformance suite | Done. The JSON Test Suite passes with zero deviations |
| 4 | Back-port to YamlValue: positions, timestamp text, Fable | Done in `../fsharp-yaml` (unreleased). Our projects also compile and test under Fable JS and Python |
| 5 | Fergi spike consumes the core, IDP defaults-plus-overrides case end to end | Done, with a mapping data form, real YAML/JSON fixtures and provenance |
| 6 | Publish `0.x` alpha prereleases of core and adapters | **Not started**. The P1/P2 API additions are in, so the core API shape is ready |
| 7 | Falid consumes the core (`file:line:col` in the CLI) | Not started |

## What is in this repo

| Project | Purpose |
|---|---|
| `src/Fnode` | Core: `Node`, `Path`, `Error`, `Parsed`, `Format`, `Node.tryGet/tryFind/exists/set/setCreating/remove`, key helpers, `Node.equals`. No dependencies |
| `src/Fnode.Yaml` | YAML adapter over `FSharp.Data.YamlValue`. Fills `Parsed.Locations`, keeps timestamp text, parse errors carry a location |
| `src/JsonValue` | Standalone JSON library (placeholder namespace `Fergi.Json`), positions built in, no dependencies |
| `src/Fnode.Json` | JSON adapter over `JsonValue` |
| `src/Fergi` | Selectors with `Where`, `Doc`, `Patch` (atomic apply, diff), `Policy`, `Doc.merge`/`layers` with provenance, `Mapping`/`Files` data form, `run`/`plan`/`runFiles`, aggregation (`inputList`, `Rule.Collect`), computed values (`Rule.Compute`, named functions from `Mapping.Functions`, built-ins in `Functions.builtins`), and the CEs `ops`, `docs`, `files`, `entry`, `mapping` |
| `src/Fergi.Jsonata` | **Spike, .NET only.** Registers a `jsonata` function (`Jsonata.Net.Native`) for `Rule.Compute`. There are no Fable (JS or Python) bindings yet. See `docs/jsonata.md` |
| `tests/*` | Unit and property tests per project, `Fnode.Conformance` (shared YAML+JSON cases), `Fnode.Fable` (Fable harness), `Fergi.Tests` (includes the end-to-end billing case under `tests/Fergi.Tests/data/`) |
| `tests/jsonata-conformance` | 90 JSONata expressions run through the .NET, JS and Python libraries with recorded expectations (73 portable, 17 divergent). `scripts/jsonata-conformance.sh` runs it |
| `scripts/fable-test.sh` | Runs the shared test sources under Fable JS and Python (`js`, `python` or `all`) |

Test counts on .NET: Fnode 30, JsonValue 323, Fnode.Json 11, Fnode.Yaml 57, Conformance 47, Fergi 226, Fergi.Jsonata 15. Fable runs 677 entries per target (Conformance counts differently there, every case still runs).

## Decisions made

- `Int of int64` stays. Under Fable int64 is exact on JS and Python (values past 2^53 and int64 min/max round-trip through JSON and YAML).
- `Timestamp of string` holds the original ISO text. Core is pure and Fable-safe, with no reflection, IO or dependencies.
- `Where` is not in core `Path`. It is a Fergi selector with `[key=value]` segment syntax.
- `Fnode.Yaml` references `../fsharp-yaml` by **project reference** while YamlValue changes. Swap back to the NuGet package once it stabilises.
- Computed values are `Rule.Compute { Fn; Args; Dest }`: only the function **name** is in the rule, so mappings stay serialisable for a CLI. Functions are pure `Node list -> Result<Node, string>` held in `Mapping.Functions` (default `Functions.builtins`; add with `Mapping.withFunction`). Unknown names fail validation before any file loads.
- The mapping form separates mapping and files (the `files` variant in `Fergi.md`). `Rule` is plain data. `Ensure` hooks are a separate field so `Rule` stays serialisable. `Resolve` conflict handlers stay code-only.
- `Op`, `ListStrategy`, `OnConflict`, `Rule` and `ConflictMode` all use `[<RequireQualifiedAccess>]`.
- Loader signature is `string -> Result<Parsed, Error list>`. Merge conflicts carry the source file and a line and column.
- Commits: conventional prefixes, no AI attribution lines.

## Related state in `../fsharp-yaml`

Unreleased commits, not yet versioned or published: surrogate-pair escapes, quoting of `<<` and `...`, `!!float` tagging so every finite float round-trips, `ParseWithPositions`, `ParseWithMetadata` (original timestamp text), Fable JS and Python support with its own harness, plus docs. Fnode depends on all of it through the project reference.

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

1. **Priority-1 Fnode API fixes before publishing** (from the findings doc): a public `Path` segment escape and parse, a creating set plus `exists`, a way to tell "absent" from "mismatch", and NaN-safe equality.
2. Priority-2 fixes: object helpers (`tryFindKey`, upsert and remove by key, `hasDuplicateKeys`), structured context on `Error`, the numeric key vs `Index` pointer ambiguity.
3. **Step 6:** check package names on NuGet, npm and PyPI (`Fnode` is a placeholder), decide the final name of `JsonValue`, swap `Fnode.Yaml` back to a released YamlValue package, then publish `0.x` alphas.
4. Step 7: Falid consumes the core.
5. Fergi, not yet started: three-way merge (use case 5), a default list strategy in the mapping form, provenance for rule-produced nodes and aggregated entries, `uniqueBy` and a keyed-object output for aggregation. Done: the computation expressions and custom operations (confirmed under Fable JS and Python) and aggregation across repos (use case 6), see `docs/fergi-spike.md` section 7.
6. Update the `second-brain` notes with the amendments listed in `docs/fergi-spike.md` (`Policy.ListRules`, `Doc.get` returning `Result`, `Policy.defaults`, the `Where` selector, the Rule policy cases, the merge-law caveat), plus `Rule.Compute`, `Arg`, the `Functions` registry on `Mapping` and the JSONata decision (see `DECISIONS.md`), which change the "`Rule` is plain data" story in `Fergi.md`. `docs/fergi-spike.md` has no section on computed values yet either. Nothing in the vault was edited by this work.
7. **Serialised mapping format** (the prerequisite for a CLI that runs mappings; mapping files are expected to be produced by a program, not hand-written): `Mapping.toNode` / `Mapping.ofNode` in the core (Fable-safe, over `Node`), JSON first, with a `version` field and tagged arguments (`{from}`, `{doc}`, `{value}`). Needs round-trip tests, validation errors that point at the file location, Fable coverage and a JSON Schema for editors. Nothing like it exists yet.
8. A standalone `Mapping.validate` that a generating program can call before writing a file: unknown function names, input names, and JSONata syntax (`JsonataQuery` already reports a position). Today validation only runs inside `run`.
9. JSONata in the builders: an `Arg.Inputs` case (all inputs as one object, keyed by input name, plus `$doc` for the output so far) and a `jsonata` custom operation that also registers the function. The builder's `Run` fills `Functions.builtins` today, so it needs a way to add to the registry. A whole-output `Rule.Transform` (JSONata builds the document) was discussed and deliberately left out.
10. JSONata errors: exceptions from the library and non-value results (a function) are now `compute.failed` (done). Still open: carry the position inside the expression in the error. A thrown exception from any registered function is also caught by `Rule.Compute`.
11. Fable bindings for JSONata: Python (`[<Import>]` of `jsonata-python`, synchronous) and JS (the npm `evaluate` is async, so the registry needs an async variant). Neither is written, and neither is confirmed to work under Fable.
12. Decisions open on JSONata (see `docs/jsonata.md`): lint mappings down to the portable subset or allow all of JSONata, an external time limit for .NET (the library has no timeout option), and how to express `Ensure` hooks in a file (a declarative check rule or a JSONata predicate).
13. Conformance suite: run it in CI (the repo has no CI config; the first run needs network), and re-record it with `scripts/jsonata-conformance.sh record` whenever a JSONata library version changes.
14. Python users: either generate mapping files from Python (the mapping format is the contract) or write a hand-made facade over the Fable Python output, whose generated names carry hash suffixes and are not stable.
15. **JSONata on .NET can return an invalid string.** `$substring` on a non-BMP character returns half of a surrogate pair without an error (found by the conformance suite; JS and Python return the whole character). The mapping then holds a corrupt string. Decide whether the JSONata boundary should reject results containing an unpaired surrogate (a `compute.failed`), and check what the YAML and JSON printers do with one.
16. Cache the parsed `JsonataQuery` per expression: it is documented as immutable and thread-safe, and `Jsonata.eval` re-parses it on every call. No cost has been measured.
