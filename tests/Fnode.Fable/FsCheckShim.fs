/// A small stand-in for the parts of FsCheck the shared test files use, so the very same property
/// tests compile and run under Fable (FsCheck itself is .NET-only). Generation is deterministic:
/// every property starts from a fixed seed (derived from its name by the runner), so a failure
/// reproduces on every run and on every target. There is no shrinking.
namespace FsCheck

open System

/// xorshift32 on uint32 (only shifts and xors, so it behaves identically on .NET, JS and Python).
[<AllowNullLiteral>]
type Rng(seed: uint32) =
    let mutable state = if seed = 0u then 2463534242u else seed

    member _.NextUInt() : uint32 =
        let mutable x = state
        x <- x ^^^ (x <<< 13)
        x <- x ^^^ (x >>> 17)
        x <- x ^^^ (x <<< 5)
        state <- x
        x

    /// Uniform-ish integer in [lo, hi] (inclusive).
    member this.Between(lo: int, hi: int) : int =
        if hi <= lo then lo
        else lo + int (this.NextUInt() % uint32 (hi - lo + 1))

/// A generator: given a size and a random source, produces a value.
type Gen<'a> = { Run: int -> Rng -> 'a }

type Arbitrary<'a> = { Gen: Gen<'a> }

/// FsCheck's `NonNull<'a>` wrapper (strings are never null here anyway).
type NonNull<'a> = NonNull of 'a

/// FsCheck's `NormalFloat`: a finite float (no NaN or infinity).
type NormalFloat = NormalFloat of float

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Gen =
    let create (f: int -> Rng -> 'a) : Gen<'a> = { Run = f }

    let run (size: int) (rng: Rng) (g: Gen<'a>) : 'a = g.Run size rng

    let constant (x: 'a) : Gen<'a> = create (fun _ _ -> x)

    let map (f: 'a -> 'b) (g: Gen<'a>) : Gen<'b> = create (fun size rng -> f (run size rng g))

    let bind (f: 'a -> Gen<'b>) (g: Gen<'a>) : Gen<'b> =
        create (fun size rng ->
            let x = run size rng g
            run size rng (f x))

    let map2 (f: 'a -> 'b -> 'c) (a: Gen<'a>) (b: Gen<'b>) : Gen<'c> =
        create (fun size rng ->
            let x = run size rng a
            let y = run size rng b
            f x y)

    let zip (a: Gen<'a>) (b: Gen<'b>) : Gen<'a * 'b> =
        create (fun size rng ->
            let x = run size rng a
            let y = run size rng b
            x, y)

    let sized (f: int -> Gen<'a>) : Gen<'a> = create (fun size rng -> run size rng (f size))

    let resize (n: int) (g: Gen<'a>) : Gen<'a> = create (fun _ rng -> run n rng g)

    let choose (lo: int, hi: int) : Gen<int> = create (fun _ rng -> rng.Between(lo, hi))

    let elements (xs: seq<'a>) : Gen<'a> =
        let arr = Array.ofSeq xs
        create (fun _ rng -> arr.[rng.Between(0, arr.Length - 1)])

    let oneof (gens: seq<Gen<'a>>) : Gen<'a> =
        let arr = Array.ofSeq gens
        create (fun size rng -> run size rng arr.[rng.Between(0, arr.Length - 1)])

    let frequency (weighted: seq<int * Gen<'a>>) : Gen<'a> =
        let arr = Array.ofSeq weighted
        let total = arr |> Array.sumBy fst
        create (fun size rng ->
            let mutable pick = rng.Between(1, total)
            let mutable chosen = snd arr.[0]
            let mutable found = false
            for (w, g) in arr do
                if not found then
                    if pick <= w then
                        chosen <- g
                        found <- true
                    else
                        pick <- pick - w
            run size rng chosen)

    /// Retries until the predicate holds (the filters in the tests accept almost every value).
    let filter (pred: 'a -> bool) (g: Gen<'a>) : Gen<'a> =
        create (fun size rng ->
            let mutable v = run size rng g
            let mutable tries = 0
            while not (pred v) && tries < 1000 do
                v <- run size rng g
                tries <- tries + 1
            if not (pred v) then failwith "Gen.filter: gave up after 1000 attempts"
            v)

    let listOfLength (n: int) (g: Gen<'a>) : Gen<'a list> =
        create (fun size rng ->
            let items = ResizeArray<'a>()
            for _ in 1..n do
                items.Add(run size rng g)
            List.ofSeq items)

    /// A random sub-sequence of `xs` (each element kept with probability 1/2, order preserved).
    let subListOf (xs: seq<'a>) : Gen<'a list> =
        let arr = Array.ofSeq xs
        create (fun _ rng -> [ for x in arr do if rng.Between(0, 1) = 1 then yield x ])

    let listOf (g: Gen<'a>) : Gen<'a list> =
        create (fun size rng -> run size rng (listOfLength (rng.Between(0, size)) g))

/// FsCheck's `gen { let! ... return ... }` builder.
type GenBuilder() =
    member _.Return(x: 'a) : Gen<'a> = Gen.constant x
    member _.ReturnFrom(g: Gen<'a>) : Gen<'a> = g
    member _.Bind(g: Gen<'a>, f: 'a -> Gen<'b>) : Gen<'b> = Gen.bind f g
    member _.Zero() : Gen<unit> = Gen.constant ()

[<AutoOpen>]
module GenBuilderValue =
    let gen = GenBuilder()

/// Built-in generators for the primitive types the tests ask for with `Arb.generate<'T>`.
module Primitives =
    let private edgeInts: int64[] =
        [| 0L; 1L; -1L; 2L; -2L; 9007199254740991L; 9007199254740992L; 9007199254740993L
           -9007199254740991L; -9007199254740992L; -9007199254740993L
           2147483647L; 2147483648L; -2147483648L; -2147483649L
           Int64.MaxValue; Int64.MinValue; Int64.MaxValue - 1L; Int64.MinValue + 1L |]

    let genInt64: Gen<int64> =
        Gen.create (fun size rng ->
            match rng.Between(0, 3) with
            | 0 -> int64 (rng.Between(-size, size))
            | 1 -> edgeInts.[rng.Between(0, edgeInts.Length - 1)]
            | _ ->
                let hi = int64 (rng.NextUInt())
                let lo = int64 (rng.NextUInt())
                (hi <<< 32) ||| lo)

    let genInt: Gen<int> =
        Gen.create (fun size rng ->
            match rng.Between(0, 3) with
            | 0 -> rng.Between(-size, size)
            | 1 -> Int32.MaxValue - rng.Between(0, 2)
            | 2 -> Int32.MinValue + rng.Between(0, 2)
            | _ -> int (rng.NextUInt()))

    let genBool: Gen<bool> = Gen.create (fun _ rng -> rng.Between(0, 1) = 1)

    /// Mostly printable ASCII, some Latin-1 and other BMP characters (a lone surrogate now and then,
    /// as .NET strings may contain them; the tests that cannot take them filter them out).
    let private genChar: Gen<char> =
        Gen.create (fun _ rng ->
            match rng.Between(0, 9) with
            | 0 -> char (rng.Between(0, 255))
            | 1 -> char (rng.Between(0, 0xFFFF))
            | _ -> char (rng.Between(32, 126)))

    let genString: Gen<string> =
        Gen.create (fun size rng ->
            let n = rng.Between(0, size)
            let sb = System.Text.StringBuilder()
            for _ in 1..n do
                sb.Append(Gen.run size rng genChar) |> ignore
            sb.ToString())

    let genFloat: Gen<float> =
        Gen.create (fun _ rng ->
            // random bit patterns reach every exponent; NaN and infinities do occur (as in FsCheck)
            let bits = (int64 (rng.NextUInt()) <<< 32) ||| int64 (rng.NextUInt())
            BitConverter.Int64BitsToDouble bits)

    let genNormalFloat: Gen<NormalFloat> =
        Gen.create (fun size rng ->
            if rng.Between(0, 3) = 0 then
                NormalFloat(float (rng.Between(-size, size)) / 8.0)
            else
                let mutable f = Double.NaN
                while Double.IsNaN f || Double.IsInfinity f do
                    f <- Gen.run size rng genFloat
                NormalFloat f)

module Arb =
    /// `Arb.generate<'T>` for the primitive types the tests use.
    let inline generate<'T> : Gen<'T> =
        if typeof<'T> = typeof<int64> then unbox<Gen<'T>> (box Primitives.genInt64)
        elif typeof<'T> = typeof<int> then unbox<Gen<'T>> (box Primitives.genInt)
        elif typeof<'T> = typeof<NonNull<string>> then
            unbox<Gen<'T>> (box (Gen.map NonNull Primitives.genString))
        elif typeof<'T> = typeof<bool> then unbox<Gen<'T>> (box Primitives.genBool)
        elif typeof<'T> = typeof<string> then unbox<Gen<'T>> (box Primitives.genString)
        elif typeof<'T> = typeof<NormalFloat> then unbox<Gen<'T>> (box Primitives.genNormalFloat)
        elif typeof<'T> = typeof<float> then unbox<Gen<'T>> (box Primitives.genFloat)
        else failwith ("FsCheckShim: no generator for " + typeof<'T>.FullName)

    let fromGen (g: Gen<'a>) : Arbitrary<'a> = { Gen = g }

namespace FsCheck.Xunit

open System

[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type PropertyAttribute() =
    inherit Attribute()
    member val Arbitrary: Type[] = [||] with get, set
    member val MaxTest: int = 100 with get, set

/// Used by the generated runner.
module Check =
    /// Cap on cases per property in the Fable run. Above the largest MaxTest in the suite (2000), so every
    /// property runs its full attribute count; lower it to speed up a run.
    let mutable maxCases = 3000

    /// Total generated cases run so far (reported by Program).
    let mutable casesRun = 0

    let private asBool (r: 'r) : bool =
        match box r with
        | :? bool as b -> b
        | _ -> true // unit-returning properties pass unless they throw

    /// Runs `prop` on `cases` values from `gen`, seeded from the property's name. Throws on the first failure.
    let run (name: string) (maxTest: int) (gen: FsCheck.Gen<'a>) (prop: 'a -> 'r) : unit =
        let cases = min maxTest maxCases
        let mutable h = 7L
        for c in name do
            h <- (h * 31L + int64 (int c)) % 2147483647L
        let seed = uint32 h
        let rng = FsCheck.Rng seed
        for i in 0 .. cases - 1 do
            let size = 1 + (if cases > 1 then i * 99 / (cases - 1) else 0)
            let value = FsCheck.Gen.run size rng gen
            casesRun <- casesRun + 1
            let ok =
                try
                    asBool (prop value)
                with e ->
                    failwithf "Falsifiable after %d tests (seed %d): threw %s\n%A" (i + 1) seed e.Message value
            if not ok then failwithf "Falsifiable after %d tests (seed %d):\n%A" (i + 1) seed value
