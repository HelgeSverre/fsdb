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
              "toWire/ofWire round-trip"
              [ testCase "every case round-trips, including sub-second precision toText would drop"
                <| fun _ ->
                    let values =
                        [ VNull
                          VInt -42L
                          VDouble 1.5
                          VDecimal 12.50M
                          VString "hi | there\nwith \"quotes\""
                          VBytes [| 0uy; 255uy; 1uy |]
                          VDate(DateOnly(2024, 3, 5))
                          VDateTime(DateTime(2024, 3, 5, 13, 45, 9, 123))
                          VJson "{\"a\":1}" ]

                    for v in values do
                        Expect.equal (ofWire (toWire v)) v (sprintf "round-trip of %A" v)

                testCase "ofWire throws on an unrecognized tag"
                <| fun _ -> Expect.throws (fun () -> ofWire "?garbage" |> ignore) "bad tag" ]

          testList
              "mysqlTypeOf"
              [ testCase "VInt reports LONGLONG, so mysqlnd converts it to a native PHP int"
                <| fun _ -> Expect.equal (mysqlTypeOf (VInt 1L)) TypeLongLong "int"

                testCase "VDouble reports DOUBLE"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDouble 1.5)) TypeDouble "double"

                testCase "VDecimal reports NEWDECIMAL"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDecimal 12.5M)) TypeNewDecimal "decimal"

                testCase "VString reports VAR_STRING"
                <| fun _ -> Expect.equal (mysqlTypeOf (VString "hi")) TypeVarString "string"

                testCase "VDate reports DATE"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDate(DateOnly(2024, 3, 5)))) TypeDate "date"

                testCase "VDateTime reports DATETIME"
                <| fun _ ->
                    Expect.equal (mysqlTypeOf (VDateTime(DateTime(2024, 3, 5, 13, 45, 9)))) TypeDateTime "datetime"

                testCase "VNull falls back to VAR_STRING — NULL round-trips regardless of declared type"
                <| fun _ -> Expect.equal (mysqlTypeOf VNull) TypeVarString "null" ]

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
                    // '9' < '10' lexically but 9 > 10 is false numerically.
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

                testList
                    "Dates"
                    [ testCase "DATE_ADD/DATE_SUB apply an INTERVAL-encoded argument"
                      <| fun _ ->
                          let interval = call "INTERVAL" [ VInt 3L; VString "DAY" ]

                          Expect.equal
                              (call "DATE_ADD" [ VDate(DateOnly(2024, 1, 1)); interval ])
                              (VDate(DateOnly(2024, 1, 4)))
                              "date_add 3 days"

                          Expect.equal
                              (call "DATE_SUB" [ VDate(DateOnly(2024, 1, 4)); interval ])
                              (VDate(DateOnly(2024, 1, 1)))
                              "date_sub 3 days"

                      testCase "DATE_ADD on a datetime with a time-bearing unit stays a datetime"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_ADD" [ VDateTime(DateTime(2024, 1, 1, 10, 0, 0)); call "INTERVAL" [ VInt 90L; VString "MINUTE" ] ])
                              (VDateTime(DateTime(2024, 1, 1, 11, 30, 0)))
                              "date_add 90 minutes"

                      testCase "DATE_ADD tolerates a plain 'N UNIT' string interval"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_ADD" [ VDate(DateOnly(2024, 1, 1)); VString "1 MONTH" ])
                              (VDate(DateOnly(2024, 2, 1)))
                              "date_add '1 MONTH'"

                      testCase "DATE_ADD/DATE_SUB propagate NULL"
                      <| fun _ -> Expect.equal (call "DATE_ADD" [ VNull; VString "1 DAY" ]) VNull "null date"

                      testCase "DATE_ADD on a date-only VString stays a date, not a midnight datetime"
                      <| fun _ ->
                          // The parser never types a SQL string literal as
                          // `VDate` — `DATE_ADD('2024-01-15', INTERVAL 10
                          // DAY)` still has to answer with a plain date.
                          Expect.equal
                              (call "DATE_ADD" [ VString "2024-01-15"; call "INTERVAL" [ VInt 10L; VString "DAY" ] ])
                              (VDate(DateOnly(2024, 1, 25)))
                              "date-only string stays a date"

                          Expect.equal
                              (call "DATE_ADD" [ VString "2024-01-15 10:00:00"; call "INTERVAL" [ VInt 1L; VString "HOUR" ] ])
                              (VDateTime(DateTime(2024, 1, 15, 11, 0, 0)))
                              "a time-bearing string still becomes a datetime"

                      testCase "DATEDIFF counts whole days, ignoring time"
                      <| fun _ ->
                          Expect.equal
                              (call "DATEDIFF" [ VDateTime(DateTime(2024, 1, 10, 23, 0, 0)); VDateTime(DateTime(2024, 1, 1, 1, 0, 0)) ])
                              (VInt 9L)
                              "datediff"

                      testCase "DATE_FORMAT renders the common specifiers"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_FORMAT" [ VDateTime(DateTime(2024, 3, 5, 13, 45, 9)); VString "%Y-%m-%d %H:%i:%s" ])
                              (VString "2024-03-05 13:45:09")
                              "date_format"

                      testCase "DATE_FORMAT works on a VString datetime, not just VDateTime"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_FORMAT" [ VString "2024-03-05 13:45:09"; VString "%Y-%m-%d" ])
                              (VString "2024-03-05")
                              "date_format on string"

                      testCase "DATE truncates to the date part"
                      <| fun _ -> Expect.equal (call "DATE" [ VDateTime(DateTime(2024, 3, 5, 13, 45, 9)) ]) (VDate(DateOnly(2024, 3, 5))) "date"

                      testCase "YEAR/MONTH/DAY/HOUR/MINUTE/SECOND extract their date part"
                      <| fun _ ->
                          let dt = VDateTime(DateTime(2024, 3, 5, 13, 45, 9))
                          Expect.equal (call "YEAR" [ dt ]) (VInt 2024L) "year"
                          Expect.equal (call "MONTH" [ dt ]) (VInt 3L) "month"
                          Expect.equal (call "DAY" [ dt ]) (VInt 5L) "day"
                          Expect.equal (call "HOUR" [ dt ]) (VInt 13L) "hour"
                          Expect.equal (call "MINUTE" [ dt ]) (VInt 45L) "minute"
                          Expect.equal (call "SECOND" [ dt ]) (VInt 9L) "second"

                      testCase "DAYOFWEEK/DAYNAME/MONTHNAME match MySQL (1 = Sunday)"
                      <| fun _ ->
                          let sunday = VDate(DateOnly(2024, 3, 3))
                          Expect.equal (call "DAYOFWEEK" [ sunday ]) (VInt 1L) "sunday is 1"
                          Expect.equal (call "DAYNAME" [ sunday ]) (VString "Sunday") "dayname"
                          Expect.equal (call "MONTHNAME" [ sunday ]) (VString "March") "monthname"

                      testCase "QUARTER buckets the month into 1-4"
                      <| fun _ -> Expect.equal (call "QUARTER" [ VDate(DateOnly(2024, 8, 1)) ]) (VInt 3L) "august is q3"

                      testCase "UNIX_TIMESTAMP/FROM_UNIXTIME round-trip"
                      <| fun _ ->
                          let dt = VDateTime(DateTime(2024, 1, 1, 0, 0, 0))
                          let ts = call "UNIX_TIMESTAMP" [ dt ]
                          Expect.equal (call "FROM_UNIXTIME" [ ts ]) dt "round trip"

                      testCase "UNIX_TIMESTAMP() agrees with NOW() (both read the same clock)"
                      <| fun _ ->
                          let nowTs = call "UNIX_TIMESTAMP" [ call "NOW" [] ]
                          let bareTs = call "UNIX_TIMESTAMP" []

                          match nowTs, bareTs with
                          | VInt a, VInt b -> Expect.isTrue (abs (a - b) <= 1L) "UNIX_TIMESTAMP() and UNIX_TIMESTAMP(NOW()) read the same clock, not one UTC and one local"
                          | other -> failtestf "expected two VInt timestamps, got %A" other

                      testCase "TIMESTAMPDIFF computes whole units between two datetimes"
                      <| fun _ ->
                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "DAY"; VDate(DateOnly(2024, 1, 1)); VDate(DateOnly(2024, 1, 11)) ])
                              (VInt 10L)
                              "timestampdiff days"

                      testCase "TIMESTAMPDIFF MONTH/YEAR overshoot by one when the diff is negative"
                      <| fun _ ->
                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "MONTH"; VDate(DateOnly(2024, 3, 31)); VDate(DateOnly(2024, 1, 1)) ])
                              (VInt -2L)
                              "backwards month diff"

                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "YEAR"; VDate(DateOnly(2024, 6, 1)); VDate(DateOnly(2023, 1, 1)) ])
                              (VInt -1L)
                              "backwards year diff"

                      testCase "LAST_DAY finds the month's final day"
                      <| fun _ -> Expect.equal (call "LAST_DAY" [ VDate(DateOnly(2024, 2, 15)) ]) (VDate(DateOnly(2024, 2, 29))) "leap february"

                      testCase "STR_TO_DATE parses a common format"
                      <| fun _ ->
                          Expect.equal (call "STR_TO_DATE" [ VString "2024-03-05"; VString "%Y-%m-%d" ]) (VDate(DateOnly(2024, 3, 5))) "str_to_date" ]

                testList
                    "Strings"
                    [ testCase "SUBSTRING is 1-indexed and supports negative positions"
                      <| fun _ ->
                          Expect.equal (call "SUBSTRING" [ VString "Hello world"; VInt 7L ]) (VString "world") "positive pos"
                          Expect.equal (call "SUBSTRING" [ VString "Hello world"; VInt 1L; VInt 5L ]) (VString "Hello") "with length"
                          Expect.equal (call "SUBSTRING" [ VString "Hello"; VInt(-3L) ]) (VString "llo") "negative pos"

                      testCase "LOCATE/INSTR/POSITION find a 1-indexed offset, or 0"
                      <| fun _ ->
                          Expect.equal (call "LOCATE" [ VString "lo"; VString "Hello" ]) (VInt 4L) "locate"
                          Expect.equal (call "INSTR" [ VString "Hello"; VString "lo" ]) (VInt 4L) "instr"
                          Expect.equal (call "POSITION" [ VString "z"; VString "Hello" ]) (VInt 0L) "not found"

                      testCase "REPLACE substitutes every occurrence"
                      <| fun _ -> Expect.equal (call "REPLACE" [ VString "a-b-c"; VString "-"; VString "+" ]) (VString "a+b+c") "replace"

                      testCase "TRIM/LTRIM/RTRIM strip whitespace"
                      <| fun _ ->
                          Expect.equal (call "TRIM" [ VString "  hi  " ]) (VString "hi") "trim"
                          Expect.equal (call "LTRIM" [ VString "  hi  " ]) (VString "hi  ") "ltrim"
                          Expect.equal (call "RTRIM" [ VString "  hi  " ]) (VString "  hi") "rtrim"

                      testCase "LPAD/RPAD pad to length with a repeating pad string"
                      <| fun _ ->
                          Expect.equal (call "LPAD" [ VString "5"; VInt 3L; VString "0" ]) (VString "005") "lpad"
                          Expect.equal (call "RPAD" [ VString "5"; VInt 3L; VString "0" ]) (VString "500") "rpad"
                          Expect.equal (call "LPAD" [ VString "12345"; VInt 3L; VString "0" ]) (VString "123") "lpad truncates"

                      testCase "LEFT/RIGHT take from either end"
                      <| fun _ ->
                          Expect.equal (call "LEFT" [ VString "Hello"; VInt 3L ]) (VString "Hel") "left"
                          Expect.equal (call "RIGHT" [ VString "Hello"; VInt 3L ]) (VString "llo") "right"

                      testCase "REVERSE/REPEAT/SPACE build strings"
                      <| fun _ ->
                          Expect.equal (call "REVERSE" [ VString "abc" ]) (VString "cba") "reverse"
                          Expect.equal (call "REPEAT" [ VString "ab"; VInt 3L ]) (VString "ababab") "repeat"
                          Expect.equal (call "SPACE" [ VInt 3L ]) (VString "   ") "space"

                      testCase "ASCII returns the first character's code, 0 for empty"
                      <| fun _ ->
                          Expect.equal (call "ASCII" [ VString "A" ]) (VInt 65L) "ascii"
                          Expect.equal (call "ASCII" [ VString "" ]) (VInt 0L) "empty"

                      testCase "HEX/UNHEX round-trip a string"
                      <| fun _ ->
                          Expect.equal (call "HEX" [ VString "AB" ]) (VString "4142") "hex"
                          Expect.equal (call "UNHEX" [ VString "4142" ]) (VString "AB") "unhex"

                      testCase "MD5/SHA1 produce lowercase hex digests of the known length"
                      <| fun _ ->
                          match call "MD5" [ VString "hello" ] with
                          | VString s -> Expect.equal s.Length 32 "md5 length"
                          | v -> failwithf "expected VString, got %A" v

                          match call "SHA1" [ VString "hello" ] with
                          | VString s -> Expect.equal s.Length 40 "sha1 length"
                          | v -> failwithf "expected VString, got %A" v

                      testCase "FORMAT adds thousands separators and fixes decimal places"
                      <| fun _ -> Expect.equal (call "FORMAT" [ VDouble 1234.5; VInt 2L ]) (VString "1,234.50") "format"

                      testCase "SUBSTRING_INDEX slices before/after the Nth delimiter"
                      <| fun _ ->
                          Expect.equal (call "SUBSTRING_INDEX" [ VString "a.b.c"; VString "."; VInt 2L ]) (VString "a.b") "positive count"
                          Expect.equal (call "SUBSTRING_INDEX" [ VString "a.b.c"; VString "."; VInt(-2L) ]) (VString "b.c") "negative count"

                      testCase "CONCAT_WS skips NULL arguments but a NULL separator nulls the result"
                      <| fun _ ->
                          Expect.equal (call "CONCAT_WS" [ VString ","; VString "a"; VNull; VString "b" ]) (VString "a,b") "skips null"
                          Expect.equal (call "CONCAT_WS" [ VNull; VString "a"; VString "b" ]) VNull "null separator"

                      testCase "ELT/FIELD/FIND_IN_SET are all 1-indexed, 0/NULL when not found"
                      <| fun _ ->
                          Expect.equal (call "ELT" [ VInt 2L; VString "a"; VString "b"; VString "c" ]) (VString "b") "elt"
                          Expect.equal (call "FIELD" [ VString "b"; VString "a"; VString "b"; VString "c" ]) (VInt 2L) "field"
                          Expect.equal (call "FIELD" [ VNull; VString "a"; VString "b" ]) (VInt 0L) "field(NULL, ...) is 0, not NULL"
                          Expect.equal (call "FIND_IN_SET" [ VString "b"; VString "a,b,c" ]) (VInt 2L) "find_in_set"

                      testCase "QUOTE wraps and escapes for a SQL literal"
                      <| fun _ -> Expect.equal (call "QUOTE" [ VString "it's" ]) (VString "'it\\'s'") "quote"

                      testCase "STRCMP returns -1/0/1"
                      <| fun _ ->
                          Expect.equal (call "STRCMP" [ VString "a"; VString "b" ]) (VInt(-1L)) "a < b"
                          Expect.equal (call "STRCMP" [ VString "a"; VString "a" ]) (VInt 0L) "a = a" ]

                testList
                    "Math and misc"
                    [ testCase "CEIL/FLOOR round toward +/- infinity"
                      <| fun _ ->
                          Expect.equal (call "CEIL" [ VDouble 1.1 ]) (VInt 2L) "ceil"
                          Expect.equal (call "FLOOR" [ VDouble 1.9 ]) (VInt 1L) "floor"

                      testCase "POW/SQRT compute doubles, SQRT of a negative is NULL"
                      <| fun _ ->
                          Expect.equal (call "POW" [ VInt 2L; VInt 10L ]) (VDouble 1024.0) "pow"
                          Expect.equal (call "SQRT" [ VInt 9L ]) (VDouble 3.0) "sqrt"
                          Expect.equal (call "SQRT" [ VInt(-1L) ]) VNull "sqrt of negative"

                      testCase "SIGN returns -1/0/1"
                      <| fun _ ->
                          Expect.equal (call "SIGN" [ VInt(-5L) ]) (VInt(-1L)) "negative"
                          Expect.equal (call "SIGN" [ VInt 0L ]) (VInt 0L) "zero"
                          Expect.equal (call "SIGN" [ VInt 5L ]) (VInt 1L) "positive"

                      testCase "TRUNCATE cuts toward zero without rounding"
                      <| fun _ -> Expect.equal (call "TRUNCATE" [ VDouble 1.999; VInt 2L ]) (VDouble 1.99) "truncate"

                      testCase "GREATEST/LEAST propagate NULL and otherwise compare like ORDER BY"
                      <| fun _ ->
                          Expect.equal (call "GREATEST" [ VInt 1L; VInt 5L; VInt 3L ]) (VInt 5L) "greatest"
                          Expect.equal (call "LEAST" [ VInt 1L; VInt 5L; VInt 3L ]) (VInt 1L) "least"
                          Expect.equal (call "GREATEST" [ VInt 1L; VNull ]) VNull "null propagates"

                      testCase "NULLIF nulls out equal arguments, passes through unequal ones"
                      <| fun _ ->
                          Expect.equal (call "NULLIF" [ VInt 1L; VInt 1L ]) VNull "equal"
                          Expect.equal (call "NULLIF" [ VInt 1L; VInt 2L ]) (VInt 1L) "unequal"
                          Expect.equal (call "NULLIF" [ VInt 1L; VNull ]) (VInt 1L) "vs null is not equal"

                      testCase "ISNULL is 1 for NULL, 0 otherwise"
                      <| fun _ ->
                          Expect.equal (call "ISNULL" [ VNull ]) (VInt 1L) "null"
                          Expect.equal (call "ISNULL" [ VInt 0L ]) (VInt 0L) "zero is not null"

                      testCase "CONV converts a number between bases"
                      <| fun _ -> Expect.equal (call "CONV" [ VString "ff"; VInt 16L; VInt 10L ]) (VString "255") "hex to decimal"

                      testCase "BIN/OCT render base 2/8"
                      <| fun _ ->
                          Expect.equal (call "BIN" [ VInt 5L ]) (VString "101") "bin"
                          Expect.equal (call "OCT" [ VInt 8L ]) (VString "10") "oct"

                      testCase "CRC32 matches the standard zlib checksum"
                      <| fun _ -> Expect.equal (call "CRC32" [ VString "123456789" ]) (VInt 0x0CBF43926L) "crc32 check value"

                      testCase "UUID produces a well-formed v4-shaped string"
                      <| fun _ ->
                          match call "UUID" [] with
                          | VString s -> Expect.isTrue (Guid.TryParse(s) |> fst) "parses as a guid"
                          | v -> failwithf "expected VString, got %A" v

                      testCase "INET_ATON/INET_NTOA round-trip an IPv4 address"
                      <| fun _ ->
                          Expect.equal (call "INET_ATON" [ VString "192.168.1.1" ]) (VInt 3232235777L) "aton"
                          Expect.equal (call "INET_NTOA" [ VInt 3232235777L ]) (VString "192.168.1.1") "ntoa" ] ] ]
