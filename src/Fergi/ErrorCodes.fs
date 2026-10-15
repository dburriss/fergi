namespace Fergi

/// Stable error codes added by Fergi on top of `Fnode.ErrorCode`.
module ErrorCodes =
    /// A `Where` selector matched no array item (or its array does not exist).
    [<Literal>]
    let selectorNoMatch = "selector.noMatch"
    /// A `Where` selector matched more than one array item.
    [<Literal>]
    let selectorAmbiguous = "selector.ambiguous"
    /// A `Test` op found a value different from the expected one.
    [<Literal>]
    let patchTestFailed = "patch.testFailed"

    /// Two documents disagree under the active merge `Policy` (see `Conflict.toError`).
    [<Literal>]
    let mergeConflict = "merge.conflict"

    /// A `Mapping` input has no entry in `Files`.
    [<Literal>]
    let mappingInputMissing = "mapping.inputMissing"
    /// A name is used (in `Files`, a rule, or a source path) that the `Mapping` does not declare.
    [<Literal>]
    let mappingInputUnknown = "mapping.inputUnknown"
    /// The mapping itself is malformed (for example more than one `Layer` rule).
    [<Literal>]
    let mappingInvalid = "mapping.invalid"
    /// An `Ensure` hook rejected the result.
    [<Literal>]
    let ensureFailed = "ensure.failed"

    /// A `Collect` rule could not order its entries (a file lacks the `SortBy` field, or the values
    /// are not all strings or all numbers).
    [<Literal>]
    let collectSortKey = "collect.sortKey"

    /// A `Compute` rule names a function that is not in the mapping's registry.
    [<Literal>]
    let mappingFunctionUnknown = "mapping.functionUnknown"
    /// A function called by a `Compute` rule rejected its arguments.
    [<Literal>]
    let computeFailed = "compute.failed"

    /// A mapping file names an unsupported `version`.
    [<Literal>]
    let mappingVersion = "mapping.version"
    /// A `Mapping` holds something a file cannot express (an `Ensure` hook, a host function, a path that
    /// does not read back the same).
    [<Literal>]
    let mappingNotSerialisable = "mapping.notSerialisable"
