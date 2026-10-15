# Decisions

A light log of decisions that would be expensive to rediscover. Newest first. Each entry is a few lines:
what we decided, why, what we rejected, and what would make us revisit it. It is not a design document; link
to one when there is one. `STATUS.md` holds the current state and the Next list, and the shorter decisions
that are plain facts about the code.

To add one, copy the shape below and put it at the top. Change `Status` rather than deleting an entry that is
no longer true (`Superseded by <title>`), so the reasoning stays findable.

```
## <short title>
Date: YYYY-MM-DD. Status: Accepted | Spike | Superseded by <title>.
Decision: one or two sentences.
Why: the constraints and the evidence.
Rejected: option, and why not.
Costs: what we accepted by choosing this.
Revisit if: the signals that should reopen it.
See: files, docs or commits.
```

## JSONata is the expression language for computed values in mappings
Date: 2026-10-15. Status: Spike (works on .NET; the Fable JS and Python bindings are not written).

Decision: a mapping that needs to transform a value or combine fields calls a registered function by name.
Simple cases use the built-ins (`concat`, `join`, `upper`, ...). Anything richer uses the `jsonata` function,
whose first argument is a JSONata expression string: `Rule.Compute { Fn = "jsonata"; ... }`. The JSONata
library for each runtime lives in its own package outside the core, and plugs in through `Mapping.Functions`.

Why:
- Mappings must stay plain data so a CLI can run them from a file, so the rule can hold a function *name* and
  a string, never a function value.
- The core must stay Fable-safe (no dependencies, no reflection), so the expression engine cannot live in it.
- JSONata is pure (no IO, no access to the host), was made for reshaping JSON, and has implementations for
  .NET, JS and Python, which are the runtimes we target.
- It works on our `Node` values through a thin conversion, and `Int` stays an exact int64 on .NET.

Rejected:
- **F# functions inside `Rule`**: not serialisable. They stay available through code-registered functions.
- **Named built-in functions only**: kept as the base layer, but too small for real transforms.
- **Jint (JavaScript strings)**: works and can be sandboxed with a timeout, memory and statement limits, but
  it is .NET-only (so it needs its own package like JSONata does), and it puts a second general-purpose
  language in every mapping. Still the escape hatch if JSONata proves too limiting.
- **Interpreting F# (compiler services or FSI)**: heavy, seconds of cold start, runs arbitrary .NET code from
  a mapping file (no sandbox), and cannot run under Fable.
- **Other expression languages (JMESPath, CEL, Liquid)**: named, not evaluated.

Costs:
- The implementations are not identical: 17 of 90 conformance cases differ between .NET, JS and Python.
  Mappings that must behave the same everywhere have to stay in the portable subset in `docs/jsonata.md`.
- JS and Python run forever on a runaway expression unless bounds are set on every evaluation; .NET stops
  recursion itself but has no timeout option.
- The npm `evaluate` is async, so the JS target needs an async variant of the function registry.
- Three libraries to keep in step, with their versions pinned and the suite re-recorded on each bump.

Revisit if: the portable subset proves too small for the mappings people actually write; the .NET port
(`Jsonata.Net.Native`) stops being maintained; the async JS requirement makes the registry unworkable; or users
want general-purpose logic that JSONata expresses badly (then add Jint as an opt-in).

See: `docs/jsonata.md`, `tests/jsonata-conformance/`, `src/Fergi.Jsonata/`, commits `2fa0452` and `5657504`.
