# How to use JSONata expressions (optional, .NET only)

```fsharp
open Fergi.Jsonata
let m2 = Jsonata.register m      // adds the `jsonata` function to the registry
// compute "/fullName" "jsonata" [ Lit(Str "first & ' ' & last"); Ptr "person:" ]   // "person:" is the whole input
```

The first argument is the expression, the second is the context (`$`), and all arguments after the
expression are available as `$args`.

The CLI registers `jsonata` for you.

Runtimes disagree on some JSONata behaviour; see [jsonata.md](../jsonata.md). This is a spike: the Python and
JS bindings do not exist yet.
