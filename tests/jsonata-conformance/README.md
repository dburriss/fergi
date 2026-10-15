# JSONata conformance suite

One list of expressions, run through every JSONata implementation Fergi uses, with the expected result
recorded for each. The point is to learn when the same mapping would give different output on different
runtimes, and to notice when a library upgrade changes behaviour.

| Runtime | Library | Runner |
|---|---|---|
| .NET | `Jsonata.Net.Native` 3.0.0 | `dotnet/Program.fs` |
| JS / TS | `jsonata` (npm) 2.2.2 | `js/run.mjs` |
| Python | `jsonata-python` 0.7.1 | `python/run.py` |

The versions are pinned in `dotnet/Runner.fsproj`, `js/package.json` and `python/requirements.txt`.

## Running it

```
scripts/jsonata-conformance.sh                  # check all three against cases.json
scripts/jsonata-conformance.sh check js         # one runtime
scripts/jsonata-conformance.sh record           # rewrite the expectations after a library change
```

Needs the .NET SDK 8+, `bun` or `node`/`npm`, and Python 3.10+ (3.13 tested). The first run installs
`jsonata` and creates `python/.venv`. The runners only execute the expressions and write what they saw to
`observed/<runtime>.json` (not committed); `check.py` does all the comparing. Exit code is non-zero when any
result differs from its expectation.

## cases.json

```json
{ "id": "concat", "expr": "first & ' ' & last", "ctx": { "first": "Ada", "last": "L" },
  "expect": { "kind": "value", "value": "Ada L" } }
```

- `kind` is `value`, `undefined` (the expression matched nothing) or `error` (with the JSONata `code`).
- **Portable** cases have one `expect`: every implementation must produce it. Numbers compare by value
  (`2` equals `2.0`), object key order counts.
- **Divergent** cases have `"divergent": true`, a `note`, and one `expect` per runtime (`js`, `python`,
  `dotnet`). Each runtime must keep producing its own recorded result, so a divergence that disappears or
  changes after an upgrade fails the check too and has to be looked at.
- Python reports a null result and an undefined result alike (`None`). It is recorded as `kind: "none"` and
  matches either expectation.
- Non-finite numbers are written as `{"$nonFinite": "NaN"}` because JSON cannot hold them.
- All runners apply the same guardrails (`guardrails` in the file: a 2 s timeout and a depth of 500), so the
  runaway-recursion cases finish. Without them JS and Python never return.

## After changing a library version

1. Bump the version in the runner's manifest.
2. `scripts/jsonata-conformance.sh record`, then read the `git diff` of `cases.json`. Each changed line is a
   behaviour change in a library.
3. Update `docs/jsonata.md` if the portable subset or the divergence table moved.

## Adding a case

Add `{ "id", "expr", "ctx" }` to `cases.json` and run `record`. A new case that comes out portable needs
nothing more; a divergent one needs a real `note` (record writes a `TODO` placeholder).
