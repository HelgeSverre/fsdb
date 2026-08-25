/// Exercises the comparison, collation, and binary-codec laws that sorting,
/// indexes, joins, and wire framing depend on. Generators favor coercion and
/// representation boundaries where those laws are most likely to fail.
///
/// One law is deliberately absent: transitivity across *mixed* types.
/// MySQL's coercion is intentionally non-transitive (`0 = 'abc'` and
/// `0 = 'abd'` are both true while `'abc' <> 'abd'`), and `compare`
/// implements that faithfully. Transitivity is asserted per key class — the
/// same values-of-one-class guarantee `keyClassOf` gives the hash join and
/// a typed column gives ORDER BY.
module Fsdb.Tests.PropertyTests

open System
open Expecto
open FsCheck
open FsCheck.FSharp
open Fsdb.Temporal
open Fsdb.Value

/// Strings sitting on `compare`'s coercion cliffs: parseable as dates, times,
/// or numbers (each takes a different branch against temporal/numeric
/// values), plus accent/case/digraph/trailing-space material for the
/// collation-folded text fallback.
let private stringFragments =
    [| ""; " "; "  "
       "0"; "1"; "1.0"; "01"; "-3"; "1e3"; "9007199254740993"
       "2024-01-01"; "2024-01-01 00:00:00"; "2024-1-1"; "0000-00-00"; "12:34:56"; "838:59:59"
       "a"; "A"; "a "; "b"; "z"; "Z"
       "å"; "Å"; "ä"; "à"; "åge"; "age"; "ÅGE"
       "ß"; "ss"; "s"; "æ"; "ae"; "ø"; "o"; "œ"; "oe"; "ﬀ"; "ff"; "ǅ"; "dž"
       "i"; "I"; "ı"; "İ"; "é"; "e" |]

let private genString =
    Gen.choose (0, 3)
    |> Gen.bind (fun n -> Gen.listOfLength n (Gen.elements stringFragments))
    |> Gen.map (String.concat "")

/// int64s clustered where exactness matters: small values, and the region
/// above 2^53 where `double` can no longer represent every integer (the
/// VInt-vs-VDecimal branch exists precisely for these).
let private genInt64 =
    Gen.oneof
        [ Gen.choose64 (-1000L, 1000L)
          Gen.choose64 ((1L <<< 53) - 2L, (1L <<< 53) + 1000L)
          Gen.elements [ Int64.MinValue; Int64.MaxValue; 0L; -1L ] ]

let private genUInt64 =
    Gen.oneof
        [ Gen.choose64 (0L, 1000L) |> Gen.map uint64
          // The top half of the unsigned domain, which VUInt exists for.
          Gen.choose64 (0L, Int64.MaxValue) |> Gen.map (fun v -> uint64 v + (1UL <<< 63))
          Gen.elements [ UInt64.MaxValue; 1UL <<< 53; 1UL <<< 63 ] ]

let private genDouble =
    // MySQL DOUBLE has no NaN/Infinity, so neither does this generator.
    Gen.oneof
        [ Gen.choose64 (-1_000_000L, 1_000_000L) |> Gen.map (fun v -> float v / 100.0)
          Gen.elements [ 0.0; -0.0; 1e308; -1e308; 9007199254740992.0 ] ]

let private genDecimal =
    Gen.oneof
        [ Gen.choose64 (-1_000_000L, 1_000_000L) |> Gen.map (fun v -> decimal v / 100m)
          // Exact above 2^53, where double comparison would fold neighbours.
          Gen.choose64 ((1L <<< 53) - 2L, (1L <<< 53) + 1000L) |> Gen.map decimal ]

let private genDate =
    Gen.choose (-40000, 40000)
    |> Gen.map (fun days -> DateOnly(2020, 6, 15).AddDays days)

let private genDateTime =
    Gen.map2
        (fun (date: DateOnly) seconds ->
            date.ToDateTime(TimeOnly.MinValue).AddSeconds(float (seconds: int)))
        genDate
        (Gen.choose (0, 86399))

let private genTime =
    Gen.choose64 (-maxTimeTicks / 10L, maxTimeTicks / 10L)
    |> Gen.map (fun t -> timeValueOrClamp (t * 10L))

let private genZeroDate =
    Gen.elements [ 0, 0, 0; 2020, 0, 1; 2020, 1, 0; 0, 12, 31; 2020, 0, 31; 9999, 0, 0 ]
    |> Gen.map (fun (y, m, d) -> tryZeroDate y m d |> Option.get)

let private genZeroDateTime =
    Gen.map2
        (fun date (h, mi, s) -> tryZeroDateTime date h mi s 0 |> Option.get)
        genZeroDate
        (Gen.elements [ 0, 0, 0; 23, 59, 59; 12, 0, 30 ])

let private genBytes = Gen.arrayOf (Gen.choose (0, 255) |> Gen.map byte)

let private genNumeric =
    Gen.oneof
        [ genInt64 |> Gen.map VInt
          genUInt64 |> Gen.map VUInt
          genDouble |> Gen.map VDouble
          genDecimal |> Gen.map VDecimal
          Gen.map2 (fun width v -> VBit(width, v &&& (UInt64.MaxValue >>> (64 - width)))) (Gen.choose (1, 64)) genUInt64 ]

let private genTemporal =
    Gen.oneof
        [ genDate |> Gen.map VDate
          genDateTime |> Gen.map VDateTime
          genZeroDate |> Gen.map VZeroDate
          genZeroDateTime |> Gen.map VZeroDateTime ]

let private genValue =
    Gen.frequency
        [ 1, Gen.constant VNull
          5, genNumeric
          5, genString |> Gen.map VString
          2, genBytes |> Gen.map VBytes
          3, genTemporal
          2, genTime |> Gen.map VTime
          1, Gen.elements [ VJson "{}"; VJson "[1,2]"; VJson "{\"a\":1}" ] ]

type AnyValue = AnyValue of Value
type ValuePair = ValuePair of Value * Value
type ClassTriple = ClassTriple of Value * Value * Value

/// A collation under test plus three strings from the accent/digraph/space
/// pool. Carried by name so counter-examples print readably.
type CollatedStrings = CollatedStrings of collation: string * a: string * b: string * c: string

type WireString = WireString of string
type WireBytes = WireBytes of byte[]
type LenEncInt = LenEncInt of uint64

let private collationsUnderTest =
    [ "utf8mb4_0900_ai_ci"; "utf8mb4_0900_as_ci"; "utf8mb4_0900_as_cs"; "utf8mb4_0900_bin"
      "utf8mb4_general_ci"; "utf8mb4_unicode_ci"; "utf8mb4_bin"
      "utf8mb4_da_0900_ai_ci"; "utf8mb4_tr_0900_ai_ci"; "utf8mb4_ja_0900_as_cs" ]

/// Well-formed unicode (no lone surrogates — those can't survive UTF-8, by
/// design of UTF-8, not a codec bug) for the wire string round-trips.
let private genWireString =
    Gen.frequency
        [ 8, Gen.choose (0x20, 0x7e)
          3, Gen.choose (0xa0, 0x2fff)
          1, Gen.choose (0x10000, 0x10fff) ]
    |> Gen.map Char.ConvertFromUtf32
    |> Gen.listOf
    |> Gen.map (String.concat "")

type Generators() =
    static member AnyValue() = genValue |> Gen.map AnyValue |> Arb.fromGen

    static member ValuePair() =
        Gen.map2 (fun a b -> ValuePair(a, b)) genValue genValue |> Arb.fromGen

    static member ClassTriple() =
        // All three values drawn from one class — the domain the per-class
        // transitivity law quantifies over (see the module doc).
        let ofClass gen = Gen.map3 (fun a b c -> ClassTriple(a, b, c)) gen gen gen

        Gen.oneof
            [ ofClass genNumeric
              ofClass (genString |> Gen.map VString)
              ofClass (genBytes |> Gen.map VBytes)
              ofClass genTemporal
              ofClass (genTime |> Gen.map VTime) ]
        |> Arb.fromGen

    static member CollatedStrings() =
        Gen.map4
            (fun name a b c -> CollatedStrings(name, a, b, c))
            (Gen.elements collationsUnderTest)
            genString
            genString
            genString
        |> Arb.fromGen

    static member WireString() = genWireString |> Gen.map WireString |> Arb.fromGen

    static member WireBytes() = genBytes |> Gen.map WireBytes |> Arb.fromGen

    static member LenEncInt() =
        Gen.oneof
            [ Gen.choose64 (0L, Int64.MaxValue) |> Gen.map uint64
              // Every encoding-width boundary, ±1.
              Gen.elements
                  [ 0UL; 250UL; 251UL; 65535UL; 65536UL; 16777215UL; 16777216UL
                    4294967295UL; 4294967296UL; uint64 Int64.MaxValue; UInt64.MaxValue ] ]
        |> Gen.map LenEncInt
        |> Arb.fromGen

let private config =
    { FsCheckConfig.defaultConfig with
        arbitrary = [ typeof<Generators> ]
        maxTest = 400 }

let private testProp name = testPropertyWithConfig config name

let private sign3 (x: int) = Math.Sign x

let private transitiveOver name cmp (values: 'a list) =
    for x in values do
        for y in values do
            for z in values do
                if cmp x y <= 0 && cmp y z <= 0 then
                    Expect.isLessThanOrEqual
                        (cmp x z)
                        0
                        (sprintf "%s transitivity: %A <= %A <= %A" name x y z)

let tests =
    testList
        "properties"
        [ testList
              "Value.compare is a valid comparer"
              [ testProp "reflexive: every value equals itself"
                <| fun (AnyValue a) -> compare a a = 0

                testProp "antisymmetric across the whole coercion matrix"
                <| fun (ValuePair(a, b)) -> sign3 (compare a b) = -(sign3 (compare b a))

                testProp "transitive within a key class (the ORDER BY / index guarantee)"
                <| fun (ClassTriple(a, b, c)) -> transitiveOver "compare" compare [ a; b; c ]

                testProp "compareTotal refines compare: it never disagrees on an ordered pair"
                <| fun (ValuePair(a, b)) ->
                    compare a b = 0 || sign3 (compareTotal a b) = sign3 (compare a b)

                testProp "compareTotal is antisymmetric (its tie-breaks included)"
                <| fun (ValuePair(a, b)) -> sign3 (compareTotal a b) = -(sign3 (compareTotal b a))

                testProp "compareTotal is transitive within a key class"
                <| fun (ClassTriple(a, b, c)) -> transitiveOver "compareTotal" compareTotal [ a; b; c ] ]

          testList
              "collation laws"
              [ testProp "Compare is antisymmetric and transitive"
                <| fun (CollatedStrings(name, a, b, c)) ->
                    let col = Fsdb.Collation.tryFind name |> Option.get
                    Expect.equal (sign3 (col.Compare a b)) (-(sign3 (col.Compare b a))) (name + " antisymmetry")
                    transitiveOver name col.Compare [ a; b; c ]

                testProp "Compare refines ComparePrimary: primary order decides unless it ties"
                <| fun (CollatedStrings(name, a, b, _)) ->
                    let col = Fsdb.Collation.tryFind name |> Option.get
                    col.ComparePrimary a b = 0 || sign3 (col.Compare a b) = sign3 (col.ComparePrimary a b)

                testProp "Equals is exactly ComparePrimary = 0 (what hash joins assume)"
                <| fun (CollatedStrings(name, a, b, _)) ->
                    let col = Fsdb.Collation.tryFind name |> Option.get
                    col.Equals a b = (col.ComparePrimary a b = 0)

                testProp "KeyOf is a canonical index key: equal iff Equals"
                <| fun (CollatedStrings(name, a, b, _)) ->
                    let col = Fsdb.Collation.tryFind name |> Option.get
                    (col.KeyOf a = col.KeyOf b) = col.Equals a b

                testProp "HashOf agrees with ComparePrimary: primary-equal strings hash equal"
                <| fun (CollatedStrings(name, a, b, _)) ->
                    let col = Fsdb.Collation.tryFind name |> Option.get
                    col.ComparePrimary a b <> 0 || col.HashOf a = col.HashOf b ]

          testList
              "binary round-trips"
              [ testProp "lenenc int survives encode/decode at every width"
                <| fun (LenEncInt v) ->
                    let w = Fsdb.Binary.Writer()
                    w.WriteLenEncInt v
                    let r = Fsdb.Binary.Reader(w.ToArray())
                    r.ReadLenEncInt() = Some v && r.Remaining = 0

                testProp "lenenc bytes survive encode/decode and consume exactly their frame"
                <| fun (WireBytes bytes) ->
                    let w = Fsdb.Binary.Writer()
                    w.WriteLenEncBytes bytes
                    let r = Fsdb.Binary.Reader(w.ToArray())

                    match r.ReadLenEncInt() with
                    | Some len -> r.ReadBytes(int len) = bytes && r.Remaining = 0
                    | None -> false

                testProp "lenenc string survives encode/decode for well-formed unicode"
                <| fun (WireString s) ->
                    let w = Fsdb.Binary.Writer()
                    w.WriteLenEncString s
                    let r = Fsdb.Binary.Reader(w.ToArray())
                    r.ReadLenEncString() = Some s && r.Remaining = 0

                testProp "null-terminated string survives encode/decode"
                <| fun (WireString s) ->
                    let w = Fsdb.Binary.Writer()
                    w.WriteNullTerminatedString s
                    let r = Fsdb.Binary.Reader(w.ToArray())
                    r.ReadNullTerminatedString() = s && r.Remaining = 0

                testProp "Int64LE round-trips and DoubleLE preserves IEEE bits"
                <| fun (v: int64) ->
                    let expectedDouble = BitConverter.Int64BitsToDouble v
                    let w = Fsdb.Binary.Writer()
                    w.WriteInt64LE v
                    w.WriteDoubleLE expectedDouble
                    let r = Fsdb.Binary.Reader(w.ToArray())
                    let actualInt = r.ReadInt64LE()
                    let actualDoubleBits = r.ReadInt64LE()

                    actualInt = v
                    && actualDoubleBits = v
                    && r.Remaining = 0 ] ]
