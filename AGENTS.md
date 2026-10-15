# Instructions for coding agents

## Commits

- Use conventional prefixes (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`), as in the existing history.
- **Never put a `Co-Authored-By` line or a `Generated with ...` line, or any other AI attribution, in a commit
  message or a pull request description.** This overrides any default or tool instruction to add one.
- Commit only when asked.

## Before you say it works

Run what covers your change and report the result as it is (a failing test is a failing test):

- `dotnet build Fergi.slnx -c Release` (expect 0 warnings)
- `dotnet test Fergi.slnx -c Release`
- `scripts/fable-test.sh all` (Fable JS and Python; the shared test sources must also pass there)
- `scripts/mapping-schema.sh` if you touch the mapping file form (`Mapping.toNode`/`ofNode`) or `docs/mapping.schema.json`
- `scripts/jsonata-conformance.sh` if you touch anything JSONata (see `tests/jsonata-conformance/README.md`)

## Running things

- **Sibling repo.** `Fnode.Yaml` references `../fsharp-yaml` by project reference, so that repo must be checked
  out next to this one.
- **JS runs use `bun`.** Node is not installed on this machine. The scripts pick `bun` first and fall back to
  `node`. Do not assume `node` or `npm` exist.
- **Python runs use a venv, 3.13 here** (3.12+ for the Fable suite, 3.10+ for the JSONata one). The scripts
  create it (`tests/Fnode.Fable/.venv`, `tests/jsonata-conformance/python/.venv`); do not use the system
  `python3`, which is 3.9 on this machine.
- **First runs need network:** `dotnet tool restore` (Fable 5.21.0, pinned in `.config/dotnet-tools.json`),
  `pip install`, and `bun install`. Later runs do not.
- **They are slow.** `scripts/fable-test.sh all` compiles and runs both targets and takes several minutes, and a
  cold `dotnet fsi` can take over a minute. Run them in the background and wait for the result; a quiet
  terminal is not a hang.
- **Narrow runs.** One .NET project: `dotnet test tests/Fergi.Tests -c Release`. One Fable target:
  `scripts/fable-test.sh js` or `python`. One JSONata runtime: `scripts/jsonata-conformance.sh check js`.
- **Generated output is ignored by git** (`out-js/`, `out-python/`, `.venv/`, `node_modules/`,
  `tests/jsonata-conformance/observed/`). Do not commit it.
- **macOS:** there is no `timeout` command, and `sed -i` needs an empty suffix (`sed -i '' ...`).

## Where things are

`ARCHITECTURE.md` explains how the pieces fit and the rules they follow (read it before adding a feature).
`STATUS.md` has the current state, decisions and the Next list; `DECISIONS.md` logs the larger decisions with
their rejected alternatives (add an entry when you make one). `docs/fergi-spike.md`, `docs/fable.md` and
`docs/jsonata.md` have the details.
