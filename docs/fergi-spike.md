# Fergi spike: findings (delivery step 5)

Step 5 of the shared document core design: a Fergi spike that consumes `Fnode` by project reference
before anything is published, so mistakes in the core API cost nothing. Exit criterion: the IDP
defaults-plus-overrides case works end to end. It does (see 1). This document records what was built,
what hurt in the Fnode API, what to amend in `second-brain/Fergi.md`, what the merge laws really are,
and how it behaves under Fable. The second-brain notes were not edited; suggested edits are listed here.

## 1. What was built

`src/Fergi` (netstandard2.0 and net8.0, references only `Fnode`, no packages, no IO, no reflection, Fable-safe).
Compile order and content:

| Level | File | Content |
|---|---|---|
| 0/1 | `ErrorCodes.fs` | `selector.noMatch`, `selector.ambiguous`, `patch.testFailed`, `merge.conflict`, `mapping.inputMissing`, `mapping.inputUnknown`, `mapping.invalid`, `ensure.failed` |
| 0/1 | `Selector.fs` | `Selector = Seg | Where`, `SelectorPath`, `parse`/`print`/`ofPath`/`resolve` (`/ports/[name=http]/port`) |
| 2 | `Policy.fs` | `Conflict`, `ListStrategy`, `OnConflict`, `Policy` (+ `ListRules`), `Policy.defaults`/`withLists`/`withListRule`/`withListsBy`/`withConflict`/`withNullDeletes` |
| 2 | `Merge.fs` | the merge walk, provenance, `Merge.merge`/`Merge.layers` |
| 1/2 | `Doc.fs` | `Doc.get/set/remove` (selector-addressed Fnode ops), `Doc.merge`, `Doc.layers` |
| 1 | `Patch.fs` | `Op = Set | Remove | Append | Move | Test`, atomic `Patch.apply`, `Patch.diff` |
| 4 | `Mapping.fs` | `Rule`, `Mapping`, `Files`, `SourcePath.parse` (`service:/name`), `Mapping.create/ensure/copy` |
| 4 | `Run.fs` | `Fergi.run`, `Fergi.plan` (`{ Output; Result; Ops; Provenance }`), `Fergi.runFiles` |

Levels: 0 selectors, 1 `Doc` + `Patch`, 2 `Policy` + merge + layers + provenance, 4 (data form) `Mapping` + `run`/`plan`.
Computation expressions (level 3, and the CE form of level 4) were cut (see 6).

Tests: `tests/Fergi.Tests`, 159 tests (xUnit 2.9.2 + FsCheck.Xunit 2.16.6):

| File | Tests |
|---|---|
| `SelectorTests` | 20 (15 + a 5-row theory) |
| `DocTests` | 7 |
| `PatchTests` | 17 |
| `MergeTests` | 30 |
| `LayersTests` | 13 |
| `MergeLawsTests` | 11 |
| `MappingTests` | 44 |
| `EndToEndTests` | 12 |
| `ParityTests` | 5 (cross-runtime behaviours, added for step 5) |

The e2e case (billing service) is `tests/Fergi.Tests/data/` (see its README): org defaults
(`defaults/org.yaml`, with an anchor and merge key), a team layer (`teams/energy.yaml`, also as `.json`),
and a service layer (`services/billing/service.yaml`) merge with rules `Layer org team service`,
`ListsBy /ports name`, `Copy service:/name -> /metadata/name`, `Copy team:/oncall -> /contacts/oncall`,
`Rename /team -> /owner`, `DefaultTo /replicas`, `Set /env`, `Drop /internal`, `Drop /oncall`, and two `Ensure`
hooks. The output equals the hand-written `deploy/billing.yaml`, read from real files through
`Fnode.Yaml` / `Fnode.Json`. Also covered: printed output re-parses to the same Node with the timestamp
verbatim, `plan` ops and provenance, a `Fail` conflict, a ByKey conflict, missing/unknown inputs,
accumulated load errors, malformed YAML reporting file and line, a JSON team input, and an `Ensure` failure.

Run it:

```bash
dotnet build Fergi.slnx -c Release          # 0 warnings
dotnet test Fergi.slnx -c Release           # whole solution
dotnet test tests/Fergi.Tests -c Release    # just Fergi (159)
scripts/fable-test.sh all                   # Fnode + Fergi under Fable JS and Python
```

## 2. Fnode API friction (deduplicated, with fixes)

Priority: **P1** fix before publishing 0.x (public API shape, hard to change later), **P2** worth doing
before 0.x if cheap, **P3** can follow in a minor version.

1. **No public path-segment escaping.** `Path.escape`/`unescape` are private, `Path.parse` only parses a
   whole pointer. Fergi re-implemented `unescape` (and `escape`) to parse `[key=value]` selector parts.
   Fix: expose `Path.escapeSegment`, `Path.unescapeSegment : string -> string option` and
   `Path.parseSegment : string -> Result<Segment, Error>` (`Path.printSegment` is already public). **P1**.
2. **`Node.set` does not create missing parents, and there is no existence test.** The mapping rule
   `Copy ... -> /metadata/name` failed with `path.notFound` when `/metadata` was absent; Fergi added its own
   `setCreating` (emits `Set(parent, Obj [])` ops). Fix: add `Node.setCreating : Path -> Node -> Node -> Result<Node, Error>`
   (creates `Obj []` for missing `Key` parents, still errors for a missing array index) and
   `Node.exists : Path -> Node -> bool`. **P1** (every consumer needs it).
3. **`tryGet`/`set`/`remove` cannot say "absent" vs "mismatch" without reading `Error.Code`.** Fergi has
   `isAbsent e = e.Code = "path.notFound" || ...` in several places. Fix: either a total
   `Node.tryFind : Path -> Node -> Result<Node option, Error>` (Ok None = absent, Error = type mismatch), or
   a DU on the error (`ErrorKind = NotFound | Mismatch | ...`) next to the string code. **P1**.
4. **No object helpers; `Obj` can hold duplicate keys.** Fergi hand-rolls "find key", "replace in place",
   "append key", "remove key keeping order" and `hasDuplicates` (needed because `Patch.diff` cannot be
   correct on duplicate keys, so it falls back to a whole-object `Set`). Fix: `Node.tryFindKey`,
   `Node.upsertKey`, `Node.removeKey` (order preserving), `Node.hasDuplicateKeys`; decide and document whether
   adapters may produce duplicates (YAML parse currently can). **P2**.
5. **`Error.Path` is a `Path`, so it cannot carry a `Where`.** A selector error such as "no item matches
   `[name=x]`" can only report the resolved prefix; the failing step is in the message text. Fix: keep
   `Path` and document the convention (path = resolved prefix), or make `Error` generic over the path
   type. Low value to change; **P3**, mainly a documentation item.
6. **`Error` has no structured context.** Fergi puts context in message prefixes: `op 3:`, `rule 2 (Copy):`,
   `input 'team' (teams/x.yaml):`, and conflicts need two values and two source names (Fergi has its own
   `Conflict` type with `Left/Right/LeftSource/RightSource`, converted by `Conflict.toError`).
   Callers cannot get these back as data. Fix: add `Context: (string * string) list` (or a
   `Source: string option`) to `Error`, plus `Error.withContext`/`Error.mapMessage` helpers. **P2**.
7. **`Float nan` breaks structural equality.** `Test`, `diff`, `merge` and `Union` all use `=`, and
   `Float nan <> Float nan`, so `diff a a` is not empty and merge reports `values differ` for identical NaN
   leaves (documented in `Doc.merge`). Fix: define `Node` equality/`Node.equals` with bitwise float
   comparison (or custom equality on `Float`), or state in the core that NaN is not a legal `Node`. **P1**
   (equality is part of the contract; changing it after publishing is breaking).
8. **A numeric object key prints the same as an `Index`.** `Path.print [Key "0"]` and `[Index 0]` are both
   `/0`, so provenance keys (`Path.print`) and `ListRules` pointers are ambiguous between "object key 0" and
   "array item 0"; `Path.parse "/0"` always yields `Key "0"` (resolution against the document decides).
   Fix: document it as a property of JSON Pointer, and offer `Path.resolve : Node -> Path -> Path` (turn
   numeric `Key` into `Index` when the node is an array) so consumers can normalise. **P2**.
9. **Path equality in xunit assertions needs `Assert.True(a = b)`.** `Path` is a plain list alias, so
   xunit's `Assert.Equal` can bind to a sequence overload; the tests use
   `Assert.True((ptr "/team" = c.Path))` or `Assert.Equal<Path>`. Fix: make `Path` a single-case record/struct
   (`Path of Segment list`) with its own equality and `ToString`, or document `Assert.Equal<Path>`. **P3**.
10. **The loader shape `string -> Result<Node, Error>` loses information.** It keeps only the first adapter
    error (`Format.Parse` returns `Error list`) and drops `Parsed.Locations`. Consequences: a file with several
    parse errors reports one, and merge conflicts carry source *names* but never `file:line`. Fix: loader type
    `string -> Result<Parsed, Error list>` (or `Result<Node, Error list>` as the minimum) so Fergi can attach
    locations to conflicts and keep every parse error. **P1** for Fergi's API (see follow-up [b]), but the Fnode
    types already support it, so no Fnode change is needed beyond documenting the intended loader shape.
11. **Fable: expected-type union-case resolution differs from the .NET compiler.** Not an Fnode issue; see 5.

## 3. Fergi design findings and proposed amendments to `second-brain/Fergi.md`

Facts established by the spike, each with a suggested note edit:

- **`Policy.ListRules` was added** (`(Path * ListStrategy) list`, exact-path match, first match wins) because
  `listsBy "/ports" "name"` needs a per-path strategy. Amend the Policy section; `Policy.withListsBy path key`
  is the user-facing form.
- **`Doc.get` returns `Result<Node, Error>`, not `option`**, to keep Fnode's shape and so a caller can tell a
  missing path from a type mismatch or an ambiguous selector. Amend every `get` example and the "missing
  versus present" discussion.
- **`Policy.default` is `Policy.defaults`** (`default` is an F# keyword). Defaults: lists `Replace`,
  `LastWins`, `NullDeletes = true`.
- **`Where` is a Fergi selector, not part of `Path`** (the core design already removed it from `Segment`).
  String form `[key=value]` inside a JSON Pointer, e.g. `/ports/[name=http]/port`; key is the text before the
  first `=`, value everything after, both with `~0`/`~1` escapes. Limits: matches string values only (a
  port number or bool never matches); an object key that itself looks like `[a=b]` is not addressable; a
  `Where` key containing `=` does not round-trip; `Where` is resolved against a concrete document, so it
  cannot be used as an insert target (no item to address) and a `Where` on a missing parent is
  `selector.noMatch`.
- **The `Rule` form cannot set `Policy.Conflict` or `NullDeletes`.** `run` always starts from `Policy.defaults`
  plus `ListsBy` rules, so a mapping can never choose `Fail`, `FirstWins`, `Union`/`Append` or
  `NullDeletes=false`. Propose `Rule.Policy of Policy`-style rules (follow-up [c]): either `Mapping.Policy: Policy`
  or a `Rule.Policy of PolicySetting` (`Conflict`, `NullDeletes`, `Lists`).
- **Provenance covers only the layer stage.** Rules after the merge (`Copy`, `Set`, `Rename`, ...) are not
  tracked, so `/metadata/name`, `/owner`, `/env` have no entry and the `Rename`d key keeps its old pointer.
  Provenance is also keyed by `Path.print`, so see friction item 8. State this in the note, or track
  rule-produced nodes with the rule's name as the source.
- **`Patch.diff` treats arrays atomically** (one `Set` of the whole array) and **emits a whole-object `Set`
  when key order changes** (key order is part of `Node` equality), or when either object has duplicate keys.
  So `plan` output for a reordered object is coarse. `apply (diff a b) a = Ok b` holds (property test).
- **`Drop` of an absent path is a no-op** (also an unmatched `Where`); `Copy` of an absent source is
  `path.notFound`; `DefaultTo`/`Set` with an unmatched `Where` is an error.
- **Multiple `Layer` rules are a `mapping.invalid` error.** A mapping has one layering stage; to layer in
  two steps you need two mappings.
- **`Copy` reads the named input, not the merged document**, so `Copy` after a `Layer` cannot see
  the overlaid value, and `Copy` ignores earlier rules. Specify this explicitly in the note (it is the
  consequence of `service:/name` meaning "that file").
- **Mapping-level conflicts can only come from `ByKey`.** Because the policy is fixed to `LastWins`, the only
  merge conflict a `run` can produce is a `ByKey` item that is not an object or lacks the key (these report
  regardless of `OnConflict`; `Resolve` is not consulted for them).
- **The first failing rule stops the run** (later rules would see a state that never existed); only load
  errors, name validation and `Ensure` failures accumulate. Rule errors are prefixed `rule i (Kind):`.
- **`Ensure` is a field of `Mapping`**, not a `Rule`, to keep `Rule` plain data (serialisable).
- Stale in `Fergi.md`: `Int of int`, `Where` in `Segment`, and "Fergi.Json" as a namespace (the placeholder
  `JsonValue` namespace; do not `open Fergi.Json` and `open Fergi` together blindly).
- **Union case clash:** `Op.Append`/`ListStrategy.Append` and `Op.Set`/`Rule.Set` share names. `Rule` is
  `[<RequireQualifiedAccess>]` already; `Op` and `ListStrategy` are not (follow-up [a]).

## 4. Merge law findings

`Doc.merge` is **not associative in general**, even under the friendliest policy (`LastWins`, `Replace`,
`NullDeletes=false`). Fergi.md's "monoid shape means a fold works" holds only for a restricted policy and
restricted documents. State this plainly in the note. What is true (tests in `MergeLawsTests.fs`):

| Law | Result |
|---|---|
| Associativity, lawful policy, shape-stable documents (each key keeps its type: scalar, nested document, list) | holds (property test) |
| Associativity, lawful policy, arbitrary documents | **fails**: a key that flips type object -> scalar -> object. `a = {x:{p:1}}`, `b = {x:5}`, `c = {x:{q:2}}`: `(a b) c = {x:{q:2}}` but `a (b c) = {x:{p:1,q:2}}`, because a type mismatch replaces the base wholesale, so grouping decides whether `a`'s fields survive |
| Associativity with `NullDeletes=true` | **fails**: `a = {x:{z:2}}`, `b = {x:{y:1}}`, `c = {x:null}`: `(a b) c = {}` but `a (b c) = {x:{z:2}}` (`b c` already deleted `x`, so `a`'s `x` survives) |
| `Fail` | **fails** in two ways: with `NullDeletes=true`, `a={x:1}`, `b={x:null}`, `c={x:2}`: `(a b) c = {x:2}` but `b c` is a conflict (a null value vs 2). With `NullDeletes=false`, three differing values succeed on neither grouping but the two groupings report different conflicts (`1 vs 2` against `2 vs 3`) |
| Identity | `Obj []` is a left and right identity (lawful policy; verified for shape-stable and general object documents). Note a non-object layer conflicts with the initial `Obj []` under `Fail` |
| Idempotence | holds for the lawful policy (`merge a a = a`); `Union` is idempotent; `Append` is associative but **not** idempotent (`merge a a` doubles the list) |

Consequences for the design: `Doc.layers` is defined as a left fold from `Obj []`, which is the only
well-defined meaning; do not describe it as "any grouping gives the same answer", and do not
parallelise or re-associate merges (for example merging team and service first and caching it) unless the
policy and the document shape are known to be stable. Under the default policy (`NullDeletes=true`) the
order and grouping of layers is observable, which is exactly what a null-delete is for, but it means the
result is not a monoid homomorphism.

## 5. Fable results

`scripts/fable-test.sh all` (Fable 5.21.0). The Fergi tests are compiled from the same sources through the
existing xunit/FsCheck shims.

| Project | .NET | Fable JS | Fable Python |
|---|---|---|---|
| Fnode.Tests | 22 | 22 | 22 |
| JsonValue.Tests | 323 | 323 | 323 |
| Fnode.Json.Tests | 11 | 11 | 11 |
| Fnode.Yaml.Tests | 57 | 57 | 57 |
| Fnode.Conformance | 47 | 30 | 30 (counting differs only, every case runs) |
| Fergi.Tests | 159 | 159 | 159 |
| Total | 619 | 602 | 602 |

- **No change to `src/Fergi` was needed**: it compiled and passed unchanged under both targets.
- **Test-side changes**: `ListStrategy.Append` is written qualified in 7 places (the Fable compiler does not use the
  expected type to pick between `Op.Append` and `ListStrategy.Append`; the .NET compiler does); `EndToEndTests`
  reads fixtures through `readFixture`, which uses the embedded `Fixtures` module under `FABLE_COMPILER` and
  the disk on .NET; `ParityTests.fs` added.
- **Harness changes**: the shim gained `gen { }`, `Gen.bind`, `Gen.map2`, `Gen.subListOf` (cheap, so the
  generator in `PatchTests` was left alone); the runner generator supports 3-argument properties and also
  writes `Fixtures.generated.fs` (git-ignored with the other `*.generated.fs`).
- **Checked identical on .NET, JS and Python** (`ParityTests` plus the e2e tests): merged key order (base order,
  new keys appended); int64 values 2^53+1, `Int64.MaxValue` and `MinValue` through YAML parse, merge, layers and
  print/re-parse, and a one-unit difference above 2^53 still conflicts; Timestamp text (`2026-10-14`,
  `2001-12-14t21:59:43.10-05:00`, `2024-01-02 03:04:05 +05:30`) through merge and `Yaml.print`, with
  `Str` and `Timestamp` of the same text still different values; ordinal ordering of the provenance `Map`
  (`/B` < `/a` < `/a/b` < `/a~0b` < `/z` < `/é`) and prefix pruning of provenance (`/a` drops `/a/b`, not
  `/ab`), which is where the earlier `String.CompareOrdinal`-with-offsets bug lived (Fergi uses
  `Substring` equality, not offset overloads).
- **Not covered / differences**: the FsCheck shim explores different cases from real FsCheck (no shrinking), so
  the property tests are cross-runtime checks rather than the same sample; astral characters in object keys
  would order differently between UTF-16 targets and Python (code points) in a `Map` (Fnode's own Python
  note), not exercised. **Nothing is excluded.**

## 6. Deliberately cut, and follow-ups

Cut from the spike (the plan's out-of-scope list):

- Computation expressions: `ops { }`, `docs { }`, `mapping { }`, custom operations, `and!`.
- Three-way merge (use case 5, base/ours/theirs).
- Aggregation `each` / `inputs` globs (use case 6); `Fergi.runFiles` takes an explicit input list only.
- Comment preservation (the YAML adapter drops comments; anchors are expanded).
- Writing files: core never writes; `run` returns `Map<output, Node>` and the caller prints and writes.

Also not done, found while building:

- `Resolve` is not consulted for `ByKey` failures; `ListRules` match only exact paths (no wildcard such as
  `/services/*/ports`).
- `plan` treats any failure to load the current output as "absent" and diffs from `Null`.

Queued follow-ups (in suggested order):

- **[a] `[<RequireQualifiedAccess>]` on `Op` and `ListStrategy`.** Removes the union-case clashes
  `Op.Append` / `ListStrategy.Append` and `Op.Set` / `Rule.Set`. Cost: about 38 test usages plus `Merge.fs`
  and `Policy.fs`; also removes the Fable expected-type workaround.
- **[b] Loader signature change** to `string -> Result<Parsed, Error list>` (or a record) so conflicts can carry
  `file:line` and all parse errors survive (friction item 10).
- **[c] `Rule.Policy` / `Mapping.Policy`** so a mapping can pick `Conflict`, `NullDeletes`, default list
  strategy and `ListRules` (design finding in 3).
- Fnode additions from section 2 marked P1 (`Path.*Segment`, `Node.setCreating`/`exists`/`tryFind`, NaN
  equality) before publishing 0.x, then Fergi drops its private copies (`Selector.unescape`, `setCreating`,
  `isAbsent`, `hasDuplicates`).
