# How to merge documents

`Doc.merge policy base overlay` combines two documents. `Doc.layers policy [ name, doc; ... ]` folds several,
later ones winning, and also returns provenance (which layer each leaf came from). Conflicts come back as a
list of all of them, not an exception.

```fsharp
let policy =
    Policy.defaults                                  // lists replaced, last wins, null deletes a key
    |> Policy.withListsBy (ptr "/ports") "name"      // merge /ports by each item's "name"
    |> Policy.withConflict OnConflict.LastWins
    |> Policy.withNullDeletes true

match Doc.layers policy [ "org", orgDoc; "service", serviceDoc ] with
| Ok(merged, provenance) -> ...
| Error conflicts -> conflicts |> List.map Conflict.toError
```

`OnConflict.Resolve` takes a function, so it only exists in code. A mapping file can say `lastWins`,
`firstWins` or `fail`.

Merge is not associative in general; see the limits in [STATUS.md](../../STATUS.md).

## Combine results and keep every error

`docs` is a `Result<_, Error list>` builder. `let!` stops at the first error; `and!` runs the sources side
by side and collects the errors of all of them.

```fsharp
let loaded =
    docs {
        let! org = load "org.yaml"
        and! svc = load "service.yaml"
        return org.Node, svc.Node
    }
```

Without the CE, this is `Result.bind` / `Result.map` by hand.
