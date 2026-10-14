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
