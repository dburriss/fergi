# Status

Snapshot of the Fnode / Fergi work. Design sources live in the `second-brain` vault: `Shared Document Core Design.md` (Fnode, the shared core) and `Fergi.md` (the merge/mapping library). Findings from the spike are in `docs/fergi-spike.md`; Fable notes are in `docs/fable.md`.

Last verified: 2026-10-14. `dotnet build Fergi.slnx -c Release` has 0 warnings. `dotnet test Fergi.slnx -c Release` passes 693 tests. `scripts/fable-test.sh all` passes 676 entries on Fable JS and on Fable Python. Nothing is published.

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
| `tests/*` | Unit and property tests per project, `Fnode.Conformance` (shared YAML+JSON cases), `Fnode.Fable` (Fable harness), `Fergi.Tests` (includes the end-to-end billing case under `tests/Fergi.Tests/data/`) |
| `scripts/fable-test.sh` | Runs the shared test sources under Fable JS and Python (`js`, `python` or `all`) |

Test counts on .NET: Fnode 30, JsonValue 323, Fnode.Json 11, Fnode.Yaml 57, Conformance 47, Fergi 225. Fable runs 676 entries per target (Conformance counts differently there, every case still runs).

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

## Next

1. **Priority-1 Fnode API fixes before publishing** (from the findings doc): a public `Path` segment escape and parse, a creating set plus `exists`, a way to tell "absent" from "mismatch", and NaN-safe equality.
2. Priority-2 fixes: object helpers (`tryFindKey`, upsert and remove by key, `hasDuplicateKeys`), structured context on `Error`, the numeric key vs `Index` pointer ambiguity.
3. **Step 6:** check package names on NuGet, npm and PyPI (`Fnode` is a placeholder), decide the final name of `JsonValue`, swap `Fnode.Yaml` back to a released YamlValue package, then publish `0.x` alphas.
4. Step 7: Falid consumes the core.
5. Fergi, not yet started: three-way merge (use case 5), a default list strategy in the mapping form, provenance for rule-produced nodes and aggregated entries, `uniqueBy` and a keyed-object output for aggregation. Done: the computation expressions and custom operations (confirmed under Fable JS and Python) and aggregation across repos (use case 6), see `docs/fergi-spike.md` section 7.
6. Update the `second-brain` notes with the amendments listed in `docs/fergi-spike.md` (`Policy.ListRules`, `Doc.get` returning `Result`, `Policy.defaults`, the `Where` selector, the Rule policy cases, the merge-law caveat). Nothing in the vault was edited by this work.
