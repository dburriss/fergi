# Two ways to use Fergi

Everything the computation expressions (CEs) do can be written with plain functions and values. The CEs only
build those values with less typing, and report bad pointer strings as `Error` values instead of exceptions.

| You want to | CE | Low level |
|---|---|---|
| Edit one document | `ops { ... }` | `Op` list, `Patch.apply` |
| Combine results and keep every error | `docs { ... }` | `Result<_, Error list>` and `Result.bind` |
| Declare where files live | `files { ... }` | `Files` record |
| Describe a whole transformation | `mapping { ... }` | `Mapping.create` over a `Rule list` |
| Build the entries of an aggregation | `entry { ... }` | `Rule list` inside `Rule.Collect` |

Fergi never touches the file system. You hand it a `Loader` and write the result yourself, which keeps the
same code usable on .NET, JS and Python (see [fable.md](../fable.md)).

For the structure of the packages and the rules they follow, read [ARCHITECTURE.md](../../ARCHITECTURE.md);
for why choices were made, [DECISIONS.md](../../DECISIONS.md).
