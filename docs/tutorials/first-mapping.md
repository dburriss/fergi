# Tutorial: your first mapping

You will build one output document from two inputs, run it, and look at the result. It takes about ten
minutes and needs a built checkout (`dotnet build Fergi.slnx -c Release`). You will work in an F# script, so
nothing is written to disk.

## 1. Start a script

Create `first.fsx` in the repo root:

```fsharp
#r "src/Fnode/bin/Release/net8.0/Fnode.dll"
#r "src/Fergi/bin/Release/net8.0/Fergi.dll"

open Fnode
open Fergi
```

## 2. Make two input documents

An organisation default and one service. They are in-memory nodes here; in real use you parse files.

```fsharp
let store =
    Map.ofList
        [ "org.yaml", Obj [ "owner", Str "platform"; "replicas", Int 2L ]
          "svc.yaml", Obj [ "name", Str "billing"; "first", Str "Ada"; "last", Str "Lovelace" ] ]
```

## 3. Describe the transformation

A mapping names its inputs and lists rules that run top to bottom.

```fsharp
let built =
    mapping {
        inputs [ "org"; "svc" ]
        layer [ "org"; "svc" ]                        // merge the two, later wins
        copy "svc:/name" "/metadata/name"             // read from the named input
        defaultTo "/kind" (Str "service")             // only when /kind is absent
        compute "/author" "join" [ Lit(Str " "); Ptr "svc:/first"; Ptr "svc:/last" ]
    }
```

## 4. Say where the files are

The strings are opaque to Fergi. They go to your loader, and the output name keys the result.

```fsharp
let f =
    files {
        input "org" "org.yaml"
        input "svc" "svc.yaml"
        output "out.yaml"
    }
```

## 5. Run it

```fsharp
match built with
| Error errs -> for e in errs do printfn "%s: %s" e.Code e.Message
| Ok m ->
    match Fergi.run m f (Loader.ofNodes store) with
    | Ok outputs -> printfn "%A" outputs.["out.yaml"]
    | Error errs -> for e in errs do printfn "%s: %s" e.Code e.Message
```

Run the script:

```
dotnet fsi first.fsx
```

You should see the merged document as a node tree: the org values, the service values, `metadata.name`, `kind: service` and
`author: Ada Lovelace`.

## What you did

You wrote a mapping, bound its input names to files, and ran it with a loader. Nothing touched the file
system: `Fergi.run` returns the output documents and leaves writing to you.

## Next

- Do a specific job: see the [how-to guides](../../README.md#how-to-guides).
- Look up a rule or function: [mapping reference](../reference/mapping.md).
- Understand why it is shaped this way: [ARCHITECTURE.md](../../ARCHITECTURE.md).
