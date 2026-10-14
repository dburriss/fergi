/// A minimal stand-in for the parts of xUnit the shared test files use, so the very same files
/// (linked from the .NET test projects) compile under Fable. xUnit itself and
/// FsCheck cannot run under Fable (reflection-based discovery, .NET-only), so the attributes are
/// plain markers read by `scripts/fable-gen-runner.fsx`, which generates the `Runner` that calls
/// every test function directly.
namespace Xunit

open System

/// Raised by a failing `Assert`.
type XunitException(message: string) =
    inherit Exception(message)

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type FactAttribute() =
    inherit Attribute()

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type TheoryAttribute() =
    inherit Attribute()

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type InlineDataAttribute([<ParamArray>] data: obj[]) =
    inherit Attribute()

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type TraitAttribute(name: string, value: string) =
    inherit Attribute()

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type ClassDataAttribute(source: Type) =
    inherit Attribute()

[<AbstractClass; Sealed>]
type Assert =
    static member private Boom<'a>(message: string) : 'a = raise (XunitException message)

    static member Fail(message: string) : unit = raise (XunitException message)

    static member Equal<'T when 'T: equality>(expected: 'T, actual: 'T) : unit =
        if not (Unchecked.equals expected actual) then
            Assert.Fail("Assert.Equal() Failure\nExpected: " + string expected + "\nActual:   " + string actual)

    static member NotEqual<'T when 'T: equality>(expected: 'T, actual: 'T) : unit =
        if Unchecked.equals expected actual then
            Assert.Fail("Assert.NotEqual() Failure\nExpected: not " + string expected)

    static member True(condition: bool) : unit =
        if not condition then Assert.Fail("Assert.True() Failure")

    static member True(condition: bool, message: string) : unit =
        if not condition then Assert.Fail("Assert.True() Failure: " + message)

    static member False(condition: bool) : unit =
        if condition then Assert.Fail("Assert.False() Failure")

    static member False(condition: bool, message: string) : unit =
        if condition then Assert.Fail("Assert.False() Failure: " + message)

    static member Contains(expectedSubstring: string, actual: string) : unit =
        if isNull actual || not (actual.Contains expectedSubstring) then
            Assert.Fail("Assert.Contains() Failure\nNot found: " + expectedSubstring + "\nIn value:  " + actual)

    static member Contains<'T when 'T: equality>(expected: 'T, collection: seq<'T>) : unit =
        if not (Seq.exists (fun x -> Unchecked.equals x expected) collection) then
            Assert.Fail("Assert.Contains() Failure\nNot found: " + string expected)

    static member DoesNotContain(expectedSubstring: string, actual: string) : unit =
        if not (isNull actual) && actual.Contains expectedSubstring then
            Assert.Fail("Assert.DoesNotContain() Failure\nFound: " + expectedSubstring + "\nIn value:  " + actual)

    static member DoesNotContain<'T when 'T: equality>(expected: 'T, collection: seq<'T>) : unit =
        if Seq.exists (fun x -> Unchecked.equals x expected) collection then
            Assert.Fail("Assert.DoesNotContain() Failure\nFound: " + string expected)

    static member StartsWith(expectedStart: string, actual: string) : unit =
        if isNull actual || not (actual.StartsWith(expectedStart, StringComparison.Ordinal)) then
            Assert.Fail("Assert.StartsWith() Failure\nExpected start: " + expectedStart + "\nActual:         " + actual)

    static member EndsWith(expectedEnd: string, actual: string) : unit =
        if isNull actual || not (actual.EndsWith(expectedEnd, StringComparison.Ordinal)) then
            Assert.Fail("Assert.EndsWith() Failure\nExpected end: " + expectedEnd + "\nActual:       " + actual)

    static member All<'T>(collection: seq<'T>, action: 'T -> unit) : unit =
        for item in collection do
            action item

    static member NotEmpty(collection: seq<'T>) : unit =
        if Seq.isEmpty collection then Assert.Fail("Assert.NotEmpty() Failure: collection was empty")

    static member DoesNotContain<'T>(collection: seq<'T>, filter: 'T -> bool) : unit =
        if Seq.exists filter collection then Assert.Fail("Assert.DoesNotContain() Failure: a matching item was found")

    static member Empty(collection: seq<'T>) : unit =
        if not (Seq.isEmpty collection) then
            Assert.Fail("Assert.Empty() Failure: collection was not empty")

    static member Single(collection: seq<'T>) : 'T =
        match List.ofSeq collection with
        | [ x ] -> x
        | xs -> Assert.Boom("Assert.Single() Failure: the collection contained " + string xs.Length + " items")

    /// Passes when `testCode` raises an exception of type `'T` (or a subclass), returning it.
    static member inline Throws<'T when 'T :> exn>(testCode: unit -> unit) : 'T =
        let raised =
            try
                testCode ()
                None
            with e ->
                Some e

        match raised with
        | Some(:? 'T as expected) -> expected
        | Some other ->
            raise (XunitException("Assert.Throws() Failure: got a different exception type: " + other.Message))
        | None -> raise (XunitException "Assert.Throws() Failure: no exception was thrown")

    /// Passes when `testCode` raises any exception (xUnit's `ThrowsAny`), returning it.
    static member inline ThrowsAny<'T when 'T :> exn>(testCode: unit -> unit) : 'T =
        Assert.Throws<'T>(testCode)
