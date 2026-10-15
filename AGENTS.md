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
- `scripts/jsonata-conformance.sh` if you touch anything JSONata (see `tests/jsonata-conformance/README.md`)

## Where things are

`STATUS.md` has the current state, decisions and the Next list. `docs/fergi-spike.md`, `docs/fable.md` and
`docs/jsonata.md` have the details.
