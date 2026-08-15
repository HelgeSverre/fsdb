module Fsdb.Tests.ValueTests

open System
open Expecto
open Fsdb.Value
open Fsdb.Functions

/// Looks up a builtin by name and applies it — the same path
/// `Executor.evalExpr`'s `FuncCall` case takes, minus the `Result` plumbing
/// around an unknown-function name that isn't this module's concern.
let private call (name: string) (args: Value list) : Value =
    match lookup name builtins with
    | Some fn -> fn args
    | None -> failwithf "no such builtin: %s" name

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
                <| fun _ -> Expect.equal (div (VInt 1L) (VInt 0L)) VNull "1 / 0" ]

          // `Functions.fs` has no dedicated `FunctionsTests.fs` — its home
          // in the `.fsproj`'s `<Compile>` list isn't this module's file to
          // add (see `Fsdb.Tests.fsproj`), so the registry's builtins are
          // exercised here instead, through `call` above.
          testList
              "Functions"
              [ testList
                    "JSON"
                    [ testCase "JSON_EXTRACT walks $.a.b, $[0], and $.a[2].b"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": {"b": 1}}"""; VString "$.a.b" ])
                              (VJson "1")
                              "$.a.b"

                          Expect.equal (call "JSON_EXTRACT" [ VJson "[10, 20, 30]"; VString "$[0]" ]) (VJson "10") "$[0]"

                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": [0, 1, {"b": 5}]}"""; VString "$.a[2].b" ])
                              (VJson "5")
                              "$.a[2].b"

                      testCase "JSON_EXTRACT quotes string results (json out, not unquoted text)"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": "hi"}"""; VString "$.a" ])
                              (VJson "\"hi\"")
                              "quoted"

                      testCase "JSON_EXTRACT $.* returns every top-level value as an array"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": 1, "b": 2}"""; VString "$.*" ])
                              (VJson "[1, 2]")
                              "$.*"

                      testCase "JSON_EXTRACT on a missing path is NULL, not an error"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VJson "{}"; VString "$.missing" ]) VNull "missing path"

                      testCase "JSON_EXTRACT on invalid JSON text is NULL"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VString "{not json"; VString "$.a" ]) VNull "invalid json"

                      testCase "JSON_EXTRACT on NULL doc is NULL"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VNull; VString "$.a" ]) VNull "null doc"

                      testCase "JSON_UNQUOTE strips the quotes from a JSON string"
                      <| fun _ -> Expect.equal (call "JSON_UNQUOTE" [ VJson "\"hi\"" ]) (VString "hi") "unquote"

                      testCase "JSON_UNQUOTE on a non-string JSON value renders its text unchanged"
                      <| fun _ -> Expect.equal (call "JSON_UNQUOTE" [ VJson "1" ]) (VString "1") "non-string"

                      testCase "JSON_CONTAINS finds an object subset"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_CONTAINS" [ VJson """{"a": 1, "b": 2}"""; VJson """{"a": 1}""" ])
                              (VInt 1L)
                              "subset"

                          Expect.equal
                              (call "JSON_CONTAINS" [ VJson """{"a": 1, "b": 2}"""; VJson """{"a": 2}""" ])
                              (VInt 0L)
                              "not a subset"

                      testCase "JSON_CONTAINS finds an array element"
                      <| fun _ -> Expect.equal (call "JSON_CONTAINS" [ VJson "[1, 2, 3]"; VJson "2" ]) (VInt 1L) "array membership"

                      testCase "JSON_SET overwrites an existing key and adds a new one"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SET" [ VJson """{"a": 1}"""; VString "$.a"; VInt 2L; VString "$.b"; VInt 3L ])
                              (VJson """{"a": 2, "b": 3}""")
                              "set"

                      testCase "JSON_INSERT never overwrites an existing key"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_INSERT" [ VJson """{"a": 1}"""; VString "$.a"; VInt 99L ])
                              (VJson """{"a": 1}""")
                              "insert no-op on existing key"

                      testCase "JSON_REPLACE never creates a missing key"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_REPLACE" [ VJson """{"a": 1}"""; VString "$.b"; VInt 99L ])
                              (VJson """{"a": 1}""")
                              "replace no-op on missing key"

                      testCase "JSON_REMOVE deletes a key"
                      <| fun _ ->
                          Expect.equal (call "JSON_REMOVE" [ VJson """{"a": 1, "b": 2}"""; VString "$.a" ]) (VJson """{"b": 2}""") "remove"

                      testCase "JSON_ARRAY builds a JSON array from mixed values, NULL included"
                      <| fun _ ->
                          Expect.equal (call "JSON_ARRAY" [ VInt 1L; VString "x"; VNull ]) (VJson """[1, "x", null]""") "array"

                      testCase "JSON_OBJECT builds a JSON object from key/value pairs"
                      <| fun _ -> Expect.equal (call "JSON_OBJECT" [ VString "a"; VInt 1L ]) (VJson """{"a": 1}""") "object"

                      testCase "JSON_LENGTH counts object members, array elements, and scalars"
                      <| fun _ ->
                          Expect.equal (call "JSON_LENGTH" [ VJson """{"a": 1, "b": 2}""" ]) (VInt 2L) "object"
                          Expect.equal (call "JSON_LENGTH" [ VJson "[1, 2, 3]" ]) (VInt 3L) "array"
                          Expect.equal (call "JSON_LENGTH" [ VJson "1" ]) (VInt 1L) "scalar"

                      testCase "JSON_VALID distinguishes valid from invalid JSON text"
                      <| fun _ ->
                          Expect.equal (call "JSON_VALID" [ VString """{"a": 1}""" ]) (VInt 1L) "valid"
                          Expect.equal (call "JSON_VALID" [ VString "{not json" ]) (VInt 0L) "invalid"

                      testCase "JSON_KEYS lists an object's top-level keys"
                      <| fun _ -> Expect.equal (call "JSON_KEYS" [ VJson """{"a": 1, "b": 2}""" ]) (VJson """["a", "b"]""") "keys" ]

              ]
        ]
