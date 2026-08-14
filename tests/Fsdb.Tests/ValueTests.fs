module Fsdb.Tests.ValueTests

open System
open Expecto
open Fsdb.Value

let tests =
    testList
        "Value"
        [ testList
              "toText"
              [ testCase "VNull renders as None (the lenenc-null wire marker)"
                <| fun _ -> Expect.equal (toText VNull) None "null"

                testCase "VInt renders as a plain integer string"
                <| fun _ -> Expect.equal (toText (VInt 42L)) (Some "42") "int"

                testCase "VDouble renders without float noise"
                <| fun _ -> Expect.equal (toText (VDouble 1.5)) (Some "1.5") "double"

                testCase "VDecimal renders without trailing exponent notation"
                <| fun _ -> Expect.equal (toText (VDecimal 12.50M)) (Some "12.50") "decimal"

                testCase "VString renders unchanged"
                <| fun _ -> Expect.equal (toText (VString "hi")) (Some "hi") "string"

                testCase "VDate renders as yyyy-MM-dd"
                <| fun _ ->
                    Expect.equal (toText (VDate(DateOnly(2024, 3, 5)))) (Some "2024-03-05") "date"

                testCase "VDateTime renders as yyyy-MM-dd HH:mm:ss"
                <| fun _ ->
                    Expect.equal
                        (toText (VDateTime(DateTime(2024, 3, 5, 13, 45, 9))))
                        (Some "2024-03-05 13:45:09")
                        "datetime"

                testCase "VJson renders the raw text unchanged"
                <| fun _ -> Expect.equal (toText (VJson "{\"a\":1}")) (Some "{\"a\":1}") "json" ]

          testList
              "compare"
              [ testCase "NULL sorts before every other value"
                <| fun _ ->
                    Expect.isLessThan (compare VNull (VInt 0L)) 0 "null < int"
                    Expect.isLessThan (compare VNull (VString "")) 0 "null < string"

                testCase "NULL equals NULL under compare"
                <| fun _ -> Expect.equal (compare VNull VNull) 0 "null = null"

                testCase "two ints compare numerically"
                <| fun _ -> Expect.isLessThan (compare (VInt 2L) (VInt 10L)) 0 "2 < 10"

                testCase "a number vs. a numeric string coerces the string to a double"
                <| fun _ -> Expect.equal (compare (VInt 1L) (VString "1")) 0 "1 = '1'"

                testCase "numeric-string coercion beats lexical string ordering"
                <| fun _ ->
                    // '9' < '10' lexically but 9 > 10 is false numerically —
                    // this is the case the task explicitly calls out.
                    Expect.isLessThan (compare (VString "9") (VInt 10L)) 0 "'9' < 10 numerically"

                testCase "two strings compare lexically, not numerically"
                <| fun _ -> Expect.isGreaterThan (compare (VString "9") (VString "10")) 0 "'9' > '10' lexically"

                testCase "decimals compare exactly, without float rounding"
                <| fun _ -> Expect.equal (compare (VDecimal 1.10M) (VDecimal 1.1M)) 0 "1.10 = 1.1"

                testCase "dates compare chronologically"
                <| fun _ ->
                    Expect.isLessThan
                        (compare (VDate(DateOnly(2024, 1, 1))) (VDate(DateOnly(2024, 6, 1))))
                        0
                        "jan < jun"

                testCase "string comparison is case-insensitive, matching utf8mb4_0900_ai_ci"
                <| fun _ -> Expect.equal (compare (VString "a") (VString "A")) 0 "'a' = 'A'"

                testCase "string comparison ignores trailing spaces, matching PAD SPACE"
                <| fun _ -> Expect.equal (compare (VString "a") (VString "a ")) 0 "'a' = 'a '" ]

          testList
              "equals"
              [ testCase "1 = '1' is true (WHERE-style implicit coercion)"
                <| fun _ -> Expect.equal (equals (VInt 1L) (VString "1")) (Some true) "1 = '1'"

                testCase "NULL = NULL is unknown, not true"
                <| fun _ -> Expect.equal (equals VNull VNull) None "null = null is unknown"

                testCase "NULL = anything is unknown"
                <| fun _ -> Expect.equal (equals VNull (VInt 1L)) None "null = 1 is unknown"

                testCase "1 = '2' is false"
                <| fun _ -> Expect.equal (equals (VInt 1L) (VString "2")) (Some false) "1 <> '2'" ]

          testList
              "truthy"
              [ testCase "NULL is unknown"
                <| fun _ -> Expect.equal (truthy VNull) None "null truthiness"

                testCase "zero is false"
                <| fun _ -> Expect.equal (truthy (VInt 0L)) (Some false) "0 is false"

                testCase "nonzero is true"
                <| fun _ -> Expect.equal (truthy (VInt 1L)) (Some true) "1 is true"

                testCase "a non-numeric string coerces to zero, so it's false"
                <| fun _ -> Expect.equal (truthy (VString "abc")) (Some false) "'abc' is false"

                testCase "a leading-numeric string is true"
                <| fun _ -> Expect.equal (truthy (VString "3abc")) (Some true) "'3abc' is true" ]

          testList
              "arithmetic"
              [ testCase "int + int stays int"
                <| fun _ -> Expect.equal (add (VInt 2L) (VInt 3L)) (VInt 5L) "2 + 3"

                testCase "int - int stays int"
                <| fun _ -> Expect.equal (sub (VInt 5L) (VInt 3L)) (VInt 2L) "5 - 3"

                testCase "int * int stays int"
                <| fun _ -> Expect.equal (mul (VInt 4L) (VInt 3L)) (VInt 12L) "4 * 3"

                testCase "decimal + int promotes to decimal"
                <| fun _ -> Expect.equal (add (VDecimal 1.5M) (VInt 1L)) (VDecimal 2.5M) "1.5 + 1"

                testCase "double + int promotes to double"
                <| fun _ -> Expect.equal (add (VDouble 1.5) (VInt 1L)) (VDouble 2.5) "1.5 + 1 (double)"

                testCase "any operand NULL propagates NULL"
                <| fun _ ->
                    Expect.equal (add VNull (VInt 1L)) VNull "null + 1"
                    Expect.equal (mul (VInt 1L) VNull) VNull "1 * null"

                testCase "division always yields a double"
                <| fun _ -> Expect.equal (div (VInt 1L) (VInt 2L)) (VDouble 0.5) "1 / 2"

                testCase "division by zero yields NULL, not an exception"
                <| fun _ -> Expect.equal (div (VInt 1L) (VInt 0L)) VNull "1 / 0" ] ]
