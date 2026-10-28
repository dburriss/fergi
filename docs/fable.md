# Fable support

`Fnode`, `Fnode.Json`, `Fnode.Yaml` and `Fergi` compile with [Fable](https://fable.io) 5.21.0 to
JavaScript and Python from the same source (no separate files). The few differences are
`#if FABLE_COMPILER` / `#if FABLE_COMPILER_PYTHON` branches; on .NET every branch is the BCL call
the code always made, so .NET behaviour is unchanged. `Fnode.Json` and `Fnode.Yaml` build on the
`Flexi.Json` and `Flexi.Yaml` NuGet packages (0.1.0), which ship their F# sources under `fable/` and have their own Fable support.
Their own test suites (including the JSON Test Suite) live in their repositories, not here.

## Running the test suite under Fable

```bash
scripts/fable-test.sh          # JS and Python
scripts/fable-test.sh js
scripts/fable-test.sh python
```

The script exits non-zero when any test fails or the run crashes (CI friendly).

Prerequisites:

- .NET SDK 8+ (the Fable tool is pinned to 5.21.0 in `.config/dotnet-tools.json`; the script runs `dotnet tool restore`)
- JS: `bun` or `node` (anything that runs ES modules). Tested with bun 1.4
- Python: 3.12+ (3.13 tested). The script creates `tests/Fnode.Fable/.venv` and installs `fable-library==5.21.0` (network needed the first time)

### How it works

xUnit and FsCheck cannot run under Fable, so `tests/Fnode.Fable` compiles the **same test files** as the
.NET projects (`Fnode.Tests`, `Fnode.Json.Tests`, `Fnode.Yaml.Tests`,
`Fnode.Conformance`, `Fergi.Tests`) through two small shims, `XunitShim.fs` and `FsCheckShim.fs`.
`scripts/fable-gen-runner.fsx` (run by the script; `dotnet fsi`) reads those files and generates
`Runner.generated.fs` (one entry per `[<Fact>]`, `[<Theory>]` data set or `[<Property>]`, calling the test
function directly) and `Fixtures.generated.fs` (the `tests/Fergi.Tests/data` YAML/JSON files as relative path -> text).
All generated files are git-ignored. `tests/Fnode.Fable` is not in `Fergi.slnx`; the harness is also
runnable on .NET (`dotnet run --project tests/Fnode.Fable`) as a check on the shims.

When you add a test file to a .NET test project, add the same `<Compile Include=...>` line to
`tests/Fnode.Fable/Fnode.Fable.fsproj`.

### Results (Fable 5.21.0)

| Project | .NET (xUnit) | Fable JS | Fable Python |
|---|---|---|---|
| Fnode.Tests | 22 | 22 | 22 |
| Fnode.Json.Tests | 11 | 11 | 11 |
| Fnode.Yaml.Tests | 57 | 57 | 57 |
| Fnode.Conformance | 47 | 30 | 30 |
| Fergi.Tests | 213 | 213 | 213 |

Conformance differs only in counting: a `[<Theory; ClassData(FormatData)>]` is one xUnit case per
format (17 x 2) but one runner entry that loops over both formats. Every case runs.

### Fergi under Fable

`src/Fergi` compiled and passed unchanged (no `src/Fergi` edits were needed). Differences were in the tests
and shims:

- **FsCheck shim** gained `gen { }` (`GenBuilder`), `Gen.bind`, `Gen.map2` and `Gen.subListOf`; the runner
  generator supports properties with three parameters.
- **Fixtures**: `EndToEndTests.fs` reads files through `readFixture`, which uses the generated `Fixtures`
  module under `FABLE_COMPILER` and the disk on .NET. Every fixture (YAML, JSON, the malformed file, the
  error-path variants) goes through the same adapters, so the e2e results are the same on all targets.
- **Union case clash**: Fable's compiler does not use the expected type to choose between `Op.Append` and
  `ListStrategy.Append` (the .NET compiler does), so `Policy.withLists Append` failed to compile. Fixed at
  the source: `Op`, `ListStrategy` and `OnConflict` are `[<RequireQualifiedAccess>]`, so the cases are always
  written qualified (`Op.Append`, `ListStrategy.Append`).
- `ParityTests.fs` pins the behaviours most likely to diverge: merged key order, int64 values above 2^53
  and at the int64 limits through merge/layers and a YAML round trip, Timestamp text through merge and
  print, the ordinal iteration order of the provenance `Map` (`/B` < `/a` < `/a/b` < `/a~0b` < `/z` < `/é`)
  and the prefix pruning of provenance entries (`/a` must not drop `/ab`). All identical on .NET, JS, Python.
- Nothing is excluded: all Fergi tests run on both targets. (The `Fnode.Conformance` count differs only as
  described above.)

### Computation expressions under Fable

`src/Fergi/Builders.fs` (`ops`, `docs`, `files`, `entry`, `mapping`) compiles unchanged for JS and Python.
Confirmed by `BuilderTests.fs` and `AggregationTests.fs` on both targets: `[<CustomOperation>]` members
(including one taking a lambda, `each`), `yield`/`yield!`/`for`/`if` in `ops`, and `and!` through
`MergeSources` + `BindReturn` (errors from every source accumulate). The compiler desugars a CE into plain
method calls before Fable sees it, so no Fable-specific code was needed.

### What is not the same as on .NET

- **FsCheck is replaced by a shim**: a seeded xorshift generator (same sequence on every target and run,
  no shrinking), with `Gen.*`, `Arb.generate` for `int`, `int64`, `bool`, `string`, `float`,
  `NormalFloat`, `NonNull<string>`, and `[<Property(Arbitrary, MaxTest)>]`. Every property runs its full
  `MaxTest` (about 10,000 generated cases per run). The shim's `int64` generator mixes in 2^53 and int64
  boundary values. Generated data differs from real FsCheck's, so the .NET and Fable runs explore
  different cases. `Check.maxCases` caps cases per property if a run needs to be faster.
- **JSON Test Suite**: `JsonTestSuiteTests.fs` reads files from disk, so `JsonTestSuiteFable.fs` runs the
  same y_/n_/i_ checks against the embedded corpus. Files that are not valid UTF-8 are stored as "no
  text" and count as rejected, which is what the .NET test does at its strict UTF-8 decoding step. All
  285 y_/n_ files run (same as .NET).
- **Two test bodies branch on `FABLE_COMPILER`** (both in `YamlFormatTests.fs`): an integer beyond decimal
  range is a parse error on .NET but parses to a `Float` under Fable (Fable's decimal has no range limit),
  and a non-ISO timestamp text (`10/14/2026 10:00:00 +00:00`) is normalised on .NET but kept as a string
  under Fable (see Timestamps).
- Two shared test lines changed to Fable-supported APIs: `Double.IsFinite` became an explicit
  `IsNaN || IsInfinity`, and a `///` comment lost a `\u` (a backslash-u in a doc comment becomes an
  invalid Python docstring).

## Numbers under Fable (the `Int of int64` question)

`Node.Int of int64` is exact on all three targets. Fable represents `int64` as a 64-bit value (JS: a
`Long` object, Python: a plain `int`), not a JS number, so nothing is lost past 2^53.
Evidence (tests in `tests/Fnode.Conformance/NumericTests.fs`, run on every target, plus the
property tests which draw int64 values from the whole range and from the 2^53 / int64 boundaries):

| Case | JSON | YAML | JS | Python |
|---|---|---|---|---|
| `9007199254740993` (2^53 + 1) parses to | `Int` exactly | `Int` exactly | same | same |
| `Int64.MaxValue` / `MinValue` print + parse | exact `Int` | exact `Int` | same | same |
| `9223372036854775808`, `-9223372036854775809` | `Float` (nearest double) | `Float` | same | same |
| `-0` | `Int 0` | `Int 0` | same | same |
| `-0.0` | `Float -0.0` (sign kept) | same | same | same |
| `1e21`, `5e-324`, `1.7976931348623157e308` | round trip bit for bit | same | same | same |
| `1.0` prints / reparses | `1.0` / `Float` | `!!float 1.0` / `Float` | same | same |

**Recommendation: keep `Int of int64`.** It is exact and identical on .NET, Fable JS and Fable Python,
including the full int64 range through both JSON and YAML, and `Int`/`Float` stay distinct. Switching to
`int` + `Float` fallback would lose exact values between 2^31 and 2^63 (ids, timestamps in ms) for no
Fable benefit. The only cost is that JS consumers see `Long` objects rather than numbers (convert at the
boundary).

Float text is the only visible difference, and it is spelling, not value:

- **.NET**: `1E+21`, `5E-324`. **JS and Python**: `1e+21`, `5e-324`. JSON output is valid and
  round-trips on every target. Do not compare float text across targets.
- **JS** prints -0 as `0`, so `JsonPrinter.FloatText` adds the sign explicitly (`-0.0`); Python already
  prints `-0.0`.
- **Python** `string` spells whole doubles out in full (1e300 = 301 digits), so from 1e21 up
  (and below 1e-6) the JSON printer uses `G17` (the shortest repr, `1e+300`). Below 1e21 Python prints
  digits like JS and .NET (`10000000000000000.0`).
- YAML float text comes from Flexi.Yaml's `G17`: `0.1` is `0.10000000000000001` on .NET and `0.1` on
  Python (shortest repr); both re-parse to the same double.
- NaN and infinities: JSON prints `null`, YAML `.nan`/`.inf`, on every target.

### Number findings that are not Fable specific

- Flexi.Yaml resolves an exponent form with an integral value in int64 range to a number with no
  fraction, so YAML `1e16`, `2.5e3`, `1e2` become `Int`, while JSON gives `Float`. YAML 1.2 calls
  these floats. Consequence: JSON text such as `1.5E+16` (what .NET prints for 1.5e16) read through the
  YAML parser is an `Int`. This affects .NET too and is in Flexi.Yaml (read-only here); `Fnode.Yaml.print`
  avoids it by tagging floats (`!!float`).
- YAML `1e400` parses to `Float Infinity` (JSON rejects it as out of range). Same on all targets.

## Python specifics

- **Recursion depth**: CPython allows about 1000 frames and the JSON parser uses about 2 per level, so
  `JsonValue.DefaultMaxDepth` is 200 under Fable Python (512 elsewhere). Deeper input is a normal
  `JsonParseError`, not a `RecursionError`. Pass `maxDepth` explicitly to change it.
- **Strings are code points, not UTF-16 units.** An astral character has `Length` 1, so `Pos.Column` /
  `Offset` count it as one unit (JS and .NET count two). A `𝄞` escape decodes to the single
  character. Lone surrogates cannot be represented.

## Timestamps under Fable

- Parsing is unchanged: `Timestamp` keeps the original source text (`2024-01-02`, `2024-01-02 03:04:05
  +05:30`, `2001-12-14t21:59:43.10-05:00`), found through `YamlParsed.TimestampText`, so it does not
  touch `DateTimeOffset` at all. Identical output on .NET, JS and Python.
- `Fnode.Yaml.print`'s marker workaround (timestamps swapped for a unique plain scalar, then replaced
  with the text via `Regex.Replace` and a `MatchEvaluator`) works unchanged under both targets,
  including nested timestamps and text that already contains the marker prefix.
- `Timestamp` text that is not YAML-shaped (`10/14/2026 10:00:00 +00:00`) is normalised through
  `DateTimeOffset.TryParse` on .NET. Fable's `TryParse` ignores culture and styles (and the compiler
  rejects it), so under Fable `YamlConvert.ofNode` accepts only the shapes Flexi.Yaml itself resolves as
  timestamps; anything else is kept as a string and re-parses as `Str`.
- `YamlConvert.toNode` (no source text) renders a Timestamp from the instant: `.0000000+00:00` on .NET,
  `.000+00:00` under Fable (millisecond precision).

## Other Fable-driven code changes

- `Double.TryParse` / `Int64.TryParse` with `NumberStyles` and culture are replaced under Fable by the
  one-argument overload (the text is already validated JSON). A 19-digit integer that overflows int64
  still falls back to `Float`.
- `String.CompareOrdinal(s, i, word, 0, len)` is replaced by `Substring` equality everywhere (Fable's
  overload does not honour the offsets, so `true`/`false`/`null` literals were never matched).
- `Double.ToString("R")` is not available; see the float notes above.
