# How to edit one document

Use `Op` values and `Patch.apply` to change a single document.

```fsharp
open Fnode
open Fergi

let ptr s = Path.parse s |> Result.defaultWith (fun e -> failwith e.Message)

let isProd = true

let plan =
    ops {
        yield Op.Set(ptr "/owner", Str "energy")
        yield Op.Remove(ptr "/draft")
        if isProd then yield Op.Set(ptr "/replicas", Int 3L)
        for k in [ "a"; "b" ] do
            yield Op.Remove(ptr ("/tmp/" + k))
    }

let result = Patch.apply plan (Obj [ "owner", Str "x"; "draft", Bool true ])
// Ok (Obj [...]) or Error [ ... ]
```

`Patch.apply` is atomic: if any op fails you get every error and no document.

`ops` is only sugar for building a list; `[ Op.Set(...); Op.Remove(...) ]` is the same value.

## See what changed between two documents

`Patch.diff a b` returns the ops that turn `a` into `b`. Arrays are compared whole.

## Read or write a single value

Use `Doc.get`, `Doc.set` and `Doc.remove` with a selector. See [pointers and selectors](../reference/mapping.md#pointers-and-selectors).
