# How to build one document from many files

An `inputList` stands for many files. `each` builds one entry per file and sets the list at a path; the
builder function `r` qualifies a pointer with the input name. `entry` builds the rules for one entry.

```fsharp
let catalog =
    mapping {
        inputList "services"
        each "services" "/services" (fun r ->
            entry {
                copy (r "/name") "/name"
                set "/kind" (Str "service")
                compute "/title" "upper" [ Ptr(r "/name") ]
            })
        sortBy "/name"                                  // orders the entries of the preceding each
    }

let f =
    files {
        inputList "services" [ "a/service.yaml"; "b/service.yaml" ]
        output "catalog.yaml"
    }
```

From the command line, add files to the list with `-l`; see [the CLI reference](../reference/cli.md).

## Validate a mapping without running it

`Mapping.validate m` checks a mapping alone, with no files or loader: undeclared inputs, an input list used
outside `each`, a second `layer`, unknown function names. It returns every problem. Use it before writing a
mapping file you generated. With JSONata registered, `Jsonata.validate m` (in `Fergi.Jsonata`) also checks
the syntax of literal expressions.
