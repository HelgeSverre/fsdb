module Fsdb.Tests.QueryHandlerTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let private (|ProcedureResult|_|) =
    function
    | MultipleResults [ (ResultSet(columns, rows), _); (Affected _, []) ] -> Some(columns, rows)
    | _ -> None

let tests =
    testList
        "QueryHandler"
        [ testCase "SELECT 1 returns a single row with column name '1'"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 1" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols [ "1" ] "column name"
                  Expect.equal rows [ [ Some "1" ] ] "row value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "block_encryption_mode selects AES mode per session"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, result = handle session "SET block_encryption_mode = 'AES-256-CBC'"

              match result with
              | Affected 0UL -> ()
              | other -> failtestf "expected SET to succeed, got %A" other

              match handle session "SELECT @@block_encryption_mode" |> snd with
              | ResultSet(_, [ [ Some mode ] ]) -> Expect.equal mode "aes-256-cbc" "canonical mode"
              | other -> failtestf "expected mode result, got %A" other

              match handle session "SELECT HEX(AES_ENCRYPT('hello', 'secret', '1234567890123456'))" |> snd with
              | ResultSet(_, [ [ Some ciphertext ] ]) -> Expect.equal ciphertext "2D2DA42E9EDBB5A009EB79D0594F7A92" "MySQL vector"
              | other -> failtestf "expected AES result, got %A" other

              match handle session "SET block_encryption_mode = 'aes-123-cbc'" |> snd with
              | Err(1231, message) -> Expect.stringContains message "aes-123-cbc" "invalid value"
              | other -> failtestf "expected 1231, got %A" other

          testCase "AES reports an ignored ECB initialization vector through the diagnostics area"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SELECT AES_ENCRYPT('hello', 'secret', '1234567890123456')"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "Warning"; Some "1618"; Some message ] ]) ->
                  Expect.equal message "<IV> option ignored" "warning text"
              | other -> failtestf "expected ignored-IV warning, got %A" other

          testCase "AES result metadata remains binary when LIMIT 0 returns no rows"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT AES_ENCRYPT('hello', 'secret') LIMIT 0" with
              | session, ResultSet(_, []) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeBlob ] "binary type"
              | _, other -> failtestf "expected an empty AES result, got %A" other

          testCase "TIME functions infer MySQL fractional precision metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT TIME('01:02:03.123'), TIMEDIFF(CAST('01:02:03.1' AS TIME(1)), CAST('00:00:00.1234' AS TIME(4))), SEC_TO_TIME(1.23), MAKETIME(1,2,3.1200), ADDTIME(CAST('01:02:03.1' AS TIME(1)), CAST('00:00:00.123' AS TIME(3)))"
              with
              | session, ResultSet(_, _) ->
                  match session.LastResultColumnMetadata with
                  | [ time; difference; seconds; made; added ] ->
                      for metadata in [ time; difference; seconds; made; added ] do
                          Expect.equal metadata.TypeId TypeTime "TIME type"
                          Expect.isTrue (metadata.Flags &&& BinaryFlag <> 0us) "binary flag"

                      Expect.equal (time.Decimals, time.ColumnLength) (3uy, 14u) "TIME"
                      Expect.equal (difference.Decimals, difference.ColumnLength) (4uy, 15u) "TIMEDIFF"
                      Expect.equal (seconds.Decimals, seconds.ColumnLength) (2uy, 13u) "SEC_TO_TIME"
                      Expect.equal (made.Decimals, made.ColumnLength) (4uy, 15u) "MAKETIME"
                      Expect.equal (added.Decimals, added.ColumnLength) (3uy, 14u) "ADDTIME"
                  | metadata -> failtestf "expected five metadata records, got %A" metadata
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "numeric display attributes shape text values and wire metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, created =
                  handle
                      session
                      "CREATE TABLE displayed (a INT(7) ZEROFILL DEFAULT 12, b FLOAT(8,2) ZEROFILL, c DECIMAL(7,2) ZEROFILL, d INT(3))"

              match created with
              | Affected 0UL -> ()
              | other -> failtestf "expected displayed table creation, got %A" other

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, warnings) ->
                  Expect.equal
                      (warnings |> List.map (List.item 1))
                      [ Some "1681"; Some "1681"; Some "1681"; Some "1681"; Some "1681"; Some "1681" ]
                      "deprecation warnings"
              | other -> failtestf "expected display warnings, got %A" other

              let session, _ = handle session "INSERT INTO displayed VALUES (12, 1.236, 1.2, 12)"

              match
                  handle
                      session
                      "SELECT a, b, c, d, CONCAT(a), LENGTH(a), HEX(a), a + 0, a LIKE '0%', a REGEXP '^0', CAST(a AS CHAR), DEFAULT(a) FROM displayed"
              with
              | session, ResultSet(_, [ row ]) ->
                  Expect.equal
                      row
                      [ Some "0000012"; Some "00001.24"; Some "00001.20"; Some "12"; Some "0000012"; Some "7"; Some "C"; Some "12"; Some "1"; Some "1"; Some "0000012"; Some "0000012" ]
                      "displayed values"

                  match session.LastResultColumnMetadata with
                  | a :: b :: c :: d :: _ ->
                      Expect.equal (a.ColumnLength, b.ColumnLength, c.ColumnLength, d.ColumnLength) (7u, 8u, 8u, 3u) "declared widths"
                      Expect.equal (b.Decimals, c.Decimals) (2uy, 2uy) "declared decimals"

                      for metadata in [ a; b; c ] do
                          Expect.isTrue (metadata.Flags &&& UnsignedFlag <> 0us) "ZEROFILL is unsigned"
                          Expect.isTrue (metadata.Flags &&& ZeroFillFlag <> 0us) "ZEROFILL flag"

                      Expect.isFalse (d.Flags &&& ZeroFillFlag <> 0us) "plain width has no ZEROFILL flag"
                  | metadata -> failtestf "expected twelve result columns, got %A" metadata
              | _, other -> failtestf "expected displayed row, got %A" other

              match
                  handle
                      session
                      "SELECT LEFT(a,2), RIGHT(a,2), SUBSTRING(a,2,2), UPPER(a), MD5(a), SHA2(a,256), QUOTE(a), ASCII(a), ORD(a), LOCATE('12',a), REPLACE(a,'0','x'), LPAD(a,9,'x'), HEX(UNHEX(a)), CRC32(a), HEX(AES_ENCRYPT(a,'k')) = HEX(AES_ENCRYPT('0000012','k')) FROM displayed"
                  |> snd
              with
              | ResultSet(_, [ row ]) ->
                  Expect.equal
                      row
                      [ Some "00"
                        Some "12"
                        Some "00"
                        Some "0000012"
                        Some "65b65395a835bcf1beebf4ba53f18dc2"
                        Some "6aee6249240c5098671d3de5d4520f4edb8ff67b150f7f99ae3e1f20927ae8eb"
                        Some "'0000012'"
                        Some "48"
                        Some "48"
                        Some "6"
                        Some "xxxxx12"
                        Some "xx0000012"
                        Some "00000012"
                        Some "1251905028"
                        Some "1" ]
                      "string contexts consume the displayed form"
              | other -> failtestf "expected displayed string coercions, got %A" other

              match handle session "SELECT JSON_ARRAY(b) FROM displayed" |> snd with
              | ResultSet(_, [ [ Some json ] ]) ->
                  Expect.equal json "[1.2400000095367432]" "FLOAT retains its single-precision stored value"
              | other -> failtestf "expected numeric JSON value, got %A" other

              match handle session "SHOW COLUMNS FROM displayed" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.item 1))
                      [ Some "int(7) unsigned zerofill"
                        Some "float(8,2) unsigned zerofill"
                        Some "decimal(7,2) unsigned zerofill"
                        Some "int" ]
                      "canonical column types"
              | other -> failtestf "expected displayed columns, got %A" other

              match handle session "SHOW CREATE TABLE displayed" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "`a` int(7) unsigned zerofill" "integer declaration"
                  Expect.stringContains ddl "`b` float(8,2) unsigned zerofill" "floating declaration"
              | other -> failtestf "expected displayed DDL, got %A" other

              let session, _ = handle session "CREATE VIEW displayed_view AS SELECT a, a + 0 AS plain FROM displayed"

              for query, expected in
                  [ "SELECT a FROM (SELECT a FROM displayed) AS derived", Some "0000012"
                    "SELECT a FROM displayed_view", Some "0000012"
                    "SELECT plain FROM displayed_view", Some "12"
                    "SELECT a FROM displayed GROUP BY a", Some "0000012"
                    "SELECT DISTINCT a FROM displayed", Some "0000012"
                    "SELECT a FROM displayed UNION ALL SELECT a FROM displayed", Some "12" ] do
                  match handle session query |> snd with
                  | ResultSet(_, rows) -> Expect.isTrue (rows |> List.forall (fun row -> row.Head = expected)) query
                  | other -> failtestf "expected displayed projection for %s, got %A" query other

              match handle session "CREATE TABLE too_wide (a INT(256))" |> snd with
              | Err(1439, message) -> Expect.stringContains message "max = 255" "display width limit"
              | other -> failtestf "expected display-width rejection, got %A" other

              match handle session "CREATE TABLE bad_float (a FLOAT(8,9))" |> snd with
              | Err(1427, _) -> ()
              | other -> failtestf "expected floating display rejection, got %A" other

          testCase "TIME functions retain scale through DECIMAL and CHAR casts"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT SEC_TO_TIME(CAST(1.2 AS DECIMAL(10,5))), MAKETIME(1,2,CAST(3.12 AS DECIMAL(10,4))), TIME(CAST('01:02:03.1200' AS CHAR))"
              with
              | session, ResultSet(_, [ [ Some seconds; Some made; Some time ] ]) ->
                  Expect.equal (seconds, made, time) ("00:00:01.20000", "01:02:03.1200", "01:02:03.1200") "text precision"
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.Decimals) [ 5uy; 4uy; 4uy ] "metadata precision"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "TIME constructors clamp oversized numeric inputs"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT MAKETIME(-9223372036854775808,0,0), SEC_TO_TIME(1e100), SEC_TO_TIME(-3020399.9)"
              with
              | session, ResultSet(_, [ [ Some made; Some huge; Some clipped ] ]) ->
                  Expect.equal (made, huge, clipped) ("-838:59:59", "838:59:59.000000", "-838:59:59.0") "clamped values"
                  Expect.equal (session.Diagnostics |> List.map _.Code) [ 1292; 1292; 1292 ] "truncation warnings"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "TIME rejects CURRENT_TIMESTAMP default and update clauses"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE TABLE time_default (value TIME DEFAULT CURRENT_TIMESTAMP)" |> snd with
              | Err(1067, "Invalid default value for 'value'") -> ()
              | other -> failtestf "expected invalid TIME default, got %A" other

              match handle session "CREATE TABLE time_update (value TIME ON UPDATE CURRENT_TIMESTAMP)" |> snd with
              | Err(1294, _) -> ()
              | other -> failtestf "expected invalid TIME update clause, got %A" other

          testCase "current TIME functions honour their requested precision"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT CURTIME(3), UTC_TIME(4), CURRENT_TIME(2)" with
              | session, ResultSet(_, [ [ Some local; Some utc; Some current ] ]) ->
                  Expect.isTrue (Text.RegularExpressions.Regex.IsMatch(local, "^\\d{2}:\\d{2}:\\d{2}\\.\\d{3}$")) "CURTIME(3)"
                  Expect.isTrue (Text.RegularExpressions.Regex.IsMatch(utc, "^\\d{2}:\\d{2}:\\d{2}\\.\\d{4}$")) "UTC_TIME(4)"
                  Expect.isTrue (Text.RegularExpressions.Regex.IsMatch(current, "^\\d{2}:\\d{2}:\\d{2}\\.\\d{2}$")) "CURRENT_TIME(2)"
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.Decimals) [ 3uy; 4uy; 2uy ] "fractional precision"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "WEIGHT_STRING BINARY metadata preserves its bounded binary result"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT WEIGHT_STRING('abc' AS BINARY(8)) LIMIT 0" with
              | session, ResultSet(_, []) ->
                  match session.LastResultColumnMetadata with
                  | [ metadata ] ->
                      Expect.equal metadata.TypeId TypeVarString "varbinary type"
                      Expect.equal metadata.ColumnLength 8u "BINARY width"
                      Expect.isTrue (metadata.Flags &&& BinaryFlag <> 0us) "binary flag"
                  | metadata -> failtestf "expected one metadata record, got %A" metadata
              | _, other -> failtestf "expected an empty WEIGHT_STRING result, got %A" other

          testCase "WEIGHT_STRING metadata follows source character bounds"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE weight_metadata (utf VARCHAR(10) CHARACTER SET utf8mb4, latin VARCHAR(10) CHARACTER SET latin1)"

              match
                  handle
                      session
                      "SELECT WEIGHT_STRING(utf), WEIGHT_STRING(latin), WEIGHT_STRING('abc'), WEIGHT_STRING(utf AS BINARY(2)), WEIGHT_STRING(utf AS CHAR(40)), WEIGHT_STRING(utf AS CHAR(41)), WEIGHT_STRING('abc' AS CHAR(13)) FROM weight_metadata LIMIT 0"
              with
              | session, ResultSet(_, []) ->
                  match session.LastResultColumnMetadata with
                  | [ utf; latin; literal; binary; utfChar40; utfChar41; literalChar13 ] ->
                      Expect.equal utf.ColumnLength 640u "utf8mb4 VARCHAR(10)"
                      Expect.equal latin.ColumnLength 10u "latin1 VARCHAR(10)"
                      Expect.equal literal.ColumnLength 192u "utf8mb4 literal"
                      Expect.equal binary.ColumnLength 8u "BINARY minimum"
                      Expect.equal utfChar40.ColumnLength 640u "CHAR width within UTF source bound"
                      Expect.equal utfChar41.ColumnLength 656u "CHAR width beyond UTF source bound"
                      Expect.equal literalChar13.ColumnLength 208u "CHAR width extends a literal bound"
                  | metadata -> failtestf "expected seven metadata records, got %A" metadata
              | _, other -> failtestf "expected an empty WEIGHT_STRING metadata result, got %A" other

          testCase "result metadata preserves declared and expression collations"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ =
                  handle
                      session
                      "CREATE TABLE wire_collations (latin VARCHAR(10) CHARACTER SET latin1 COLLATE latin1_swedish_ci, binary_text VARCHAR(10) COLLATE utf8mb4_bin, number INT)"

              match
                  handle
                      session
                      "SELECT latin, binary_text, number, 'x' COLLATE utf8mb4_0900_as_cs FROM wire_collations LIMIT 0"
              with
              | session, ResultSet(_, []) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.CollationId)
                      [ Some 8us; Some 46us; Some 63us; Some 278us ]
                      "wire charset numbers follow each result expression"
              | _, other -> failtestf "expected empty collation metadata, got %A" other

          testCase "text-probed result metadata uses the connection collation"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET NAMES latin1 COLLATE latin1_swedish_ci"

              match handle session "SHOW DATABASES" with
              | session, ResultSet([ "Database" ], _) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.CollationId)
                      [ Some 8us ]
                      "SHOW metadata follows the requested result charset"
              | _, other -> failtestf "expected SHOW DATABASES metadata, got %A" other

          testCase "result metadata preserves physical column origins"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE app"
              let session, _ = handle session "USE app"
              let session, _ = handle session "CREATE TABLE users (id INT, name VARCHAR(20))"

              match handle session "SELECT u.id AS renamed, u.name, u.id + 1 AS computed FROM users AS u LIMIT 0" with
              | session, ResultSet(_, []) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.Origin)
                      [ Some
                            { Schema = "app"
                              Table = "u"
                              OriginalTable = "users"
                              OriginalName = "id" }
                        Some
                            { Schema = "app"
                              Table = "u"
                              OriginalTable = "users"
                              OriginalName = "name" }
                        None ]
                      "only physical columns retain their source fields"

                  let session, _ = handle session "CREATE VIEW user_ids AS SELECT id FROM users"

                  match handle session "SELECT v.id, d.name FROM user_ids AS v JOIN (SELECT name FROM users) AS d ON 1 = 1 LIMIT 0" with
                  | session, ResultSet(_, []) ->
                      Expect.equal
                          (session.LastResultColumnMetadata |> List.map _.Origin)
                          [ None; None ]
                          "views and derived tables do not claim physical origins"
                  | _, other -> failtestf "expected empty derived metadata, got %A" other
              | _, other -> failtestf "expected empty origin metadata, got %A" other

          testCase "a version-gated /*!NNNNN ... */ comment executes its wrapped SET, matching a mysqldump preamble"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected an OK/0-rows ack, got %A" other

          testCase "a comment-only statement is a harmless no-op, not a syntax error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "/* trailing comment */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected an OK/0-rows ack, got %A" other

          testCase "a /*!NNNNN lookalike inside a string literal round-trips unchanged"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 'a /*!40101 x*/ b'" |> snd with
              | ResultSet(_, [ [ Some "a /*!40101 x*/ b" ] ]) -> ()
              | other -> failtestf "expected the literal intact, got %A" other

          testCase "a SELECT's int/string columns report their real MySQL wire types, not a blanket VAR_STRING"
          <| fun _ ->
              // Real MySQL clients (PHP's mysqlnd in particular)
              // auto-convert a LONGLONG-typed column to a native int even
              // over the text protocol — Eloquent code doing `$model->foo_id
              // === $other->id` only gets that conversion if the column
              // definition packet reports the same type real MySQL would.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'a')"

              match handle session "SELECT id, name FROM t" with
              | session, ResultSet([ "id"; "name" ], [ [ Some "1"; Some "a" ] ]) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong; TypeVarString ] "id reports INT's own width, name is a string"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "LIMIT 0 (the getColumnMeta/\"metadata, no rows\" idiom) still reports real column wire types, not a blanket VAR_STRING"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'a')"

              match handle session "SELECT id, name FROM t LIMIT 0" with
              | session, ResultSet([ "id"; "name" ], []) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong; TypeVarString ] "LIMIT 0 must not narrow types to the empty row set it returns"
              | _, other -> failtestf "expected an empty resultset, got %A" other

          testCase "JSON schema functions report result types without a row"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT JSON_SCHEMA_VALID('{}', '{}'), JSON_SCHEMA_VALIDATION_REPORT('{}', '{}') LIMIT 0"
              with
              | session, ResultSet(_, []) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeLongLong; TypeVarString ]
                      "function metadata"
              | _, other -> failtestf "expected an empty resultset, got %A" other

          testCase "planar geometry functions compose through SQL expressions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let statement =
                  "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('LINESTRING(3 0,3 4)')), "
                  + "ST_AsText(ST_Envelope(ST_GeomFromText('MULTIPOINT(2 3,1 4)'))), "
                  + "MBRContains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')), "
                  + "ST_Intersects(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('LINESTRING(0 0,1 0)')), "
                  + "ST_Disjoint(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('LINESTRING(0 0,1 0)')), "
                  + "ST_Equals(ST_GeomFromText('LINESTRING(0 0,1 1)'), ST_GeomFromText('LINESTRING(1 1,0 0)')), "
                  + "ST_Contains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')), "
                  + "ST_Within(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')), "
                  + "ST_Touches(ST_GeomFromText('POINT(0 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')), "
                  + "ST_Contains(ST_Buffer(ST_GeomFromText('POINT(0 0)'), 1), ST_GeomFromText('POINT(0 0)'))"

              match
                  handle
                      session
                      statement
                  |> snd
              with
              | ResultSet(_, [ [ Some "3"; Some "POLYGON((1 3,2 3,2 4,1 4,1 3))"; Some "1"; Some "1"; Some "1"; Some "1"; Some "1"; Some "1"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected planar geometry result, got %A" other

          testCase "planar geometry functions retain result metadata without rows"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let statement =
                  "SELECT ST_Envelope(ST_GeomFromText('POINT(1 2)')), "
                  + "ST_Distance(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(3 4)')), "
                  + "MBRIntersects(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 0)')), "
                  + "ST_Intersects(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 0)')), "
                  + "ST_Equals(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 0)')), "
                  + "ST_ConvexHull(ST_GeomFromText('MULTIPOINT((0 0),(1 0),(0 1))')), "
                  + "ST_Buffer(ST_GeomFromText('POINT(0 0)'), 1), "
                  + "ST_IsValid(ST_GeomFromText('POINT(0 0)')) LIMIT 0"

              match handle session statement with
              | session, ResultSet(_, []) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeGeometry; TypeDouble; TypeLongLong; TypeLongLong; TypeLongLong; TypeGeometry; TypeGeometry; TypeLongLong ]
                      "function metadata"
              | _, other -> failtestf "expected empty resultset, got %A" other

          // A resultset's types are read off the row `Value`s, which know
          // nothing about how the column was declared. Where a projection
          // resolves back to a real column, the declared type wins — clients
          // act on the difference (an ENUM is only an ENUM when the column
          // definition carries ENUM_FLAG; TINYINT(1) is a bool, not a number).
          testCase "a bare column reference reports its declared type, not the one its stored Value implies"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle
                      session
                      "CREATE TABLE t (st ENUM('a','b') NOT NULL, ok BOOLEAN NOT NULL, yr YEAR NOT NULL, \
                       tiny TINYINT NOT NULL, small SMALLINT NOT NULL, mid INT NOT NULL, big BIGINT NOT NULL)"

              let session, _ = handle session "INSERT INTO t VALUES ('a', 1, 2011, 1, 2, 3, 4)"

              match handle session "SELECT st, ok, yr, tiny, small, mid, big FROM t" with
              | session, ResultSet(_, [ _ ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeString; TypeTiny; TypeYear; TypeTiny; TypeShort; TypeLong; TypeLongLong ]
                      "each column reports the width and family it was declared with"
              | _, other -> failtestf "expected one row, got %A" other

          // MySQL declares YEAR()'s result as YEAR even though the value it
          // returns is an ordinary integer.
          testCase "YEAR() reports the YEAR type its integer result would otherwise hide"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES ('2011-10-16')"

              match handle session "SELECT YEAR(d) AS yr, MONTH(d) AS mo FROM t" with
              | session, ResultSet(_, [ [ Some "2011"; Some "10" ] ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeYear; TypeLongLong ]
                      "YEAR() is YEAR; the other extractors stay plain integers"
              | _, other -> failtestf "expected the extracted parts, got %A" other

          // WITH ROLLUP materializes each grouped column into a nullable
          // temporary to hold the super-aggregate row's NULL, and an enum's
          // value set doesn't survive it.
          testCase "WITH ROLLUP drops a grouped ENUM back to its data-driven type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (st ENUM('a','b') NOT NULL, n INT NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES ('a', 1), ('b', 2)"

              match handle session "SELECT st, COUNT(*) AS c FROM t GROUP BY st" |> fst with
              | grouped ->
                  Expect.equal
                      (grouped.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeString; TypeLongLong ]
                      "a plain GROUP BY keeps the enum"

              match handle session "SELECT st, COUNT(*) AS c FROM t GROUP BY st WITH ROLLUP" |> fst with
              | rolled ->
                  Expect.equal
                      (rolled.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeVarString; TypeLongLong ]
                      "the rollup temporary loses it, so claiming ENUM would overclaim"

          testCase "SUM over an integer column reports MySQL's DECIMAL result type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (n BIGINT)"
              let session, _ = handle session "INSERT INTO t VALUES (9223372036854775807), (1)"

              match handle session "SELECT SUM(n) AS total FROM t" with
              | session, ResultSet([ "total" ], [ [ Some "9223372036854775808" ] ]) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "SUM(BIGINT) is NEWDECIMAL, not LONGLONG"
              | _, other -> failtestf "expected a decimal SUM resultset, got %A" other

          testCase "computed expressions report their static result types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 1, 256, 40000, 2147483648, 1 + 2, 1 = 1, 1 / 2, CAST('x' AS CHAR(8))" with
              | session, ResultSet(_, [ _ ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeTiny; TypeShort; TypeLong; TypeLongLong; TypeLongLong; TypeLongLong; TypeNewDecimal; TypeString ]
                      "literal, arithmetic, predicate, division, and cast types"

                  let charMetadata = List.last session.LastResultColumnMetadata
                  Expect.equal charMetadata.ColumnLength 32u "CHAR(8) carries its utf8mb4 byte width"
              | _, other -> failtestf "expected one computed row, got %A" other

          testCase "declared result metadata carries widths and column flags"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE meta (id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY, code CHAR(8) NOT NULL, state ENUM('new','closed') UNIQUE)"

              match handle session "SELECT id, code, state FROM meta LIMIT 0" with
              | session, ResultSet(_, []) ->
                  let id, code, state =
                      match session.LastResultColumnMetadata with
                      | [ id; code; state ] -> id, code, state
                      | metadata -> failtestf "expected three metadata records, got %A" metadata

                  Expect.equal id.TypeId TypeLong "INT wire type"
                  Expect.isTrue (id.Flags &&& UnsignedFlag <> 0us) "UNSIGNED flag"
                  Expect.isTrue (id.Flags &&& PrimaryKeyFlag <> 0us) "PRIMARY_KEY flag"
                  Expect.isTrue (id.Flags &&& AutoIncrementFlag <> 0us) "AUTO_INCREMENT flag"
                  Expect.equal code.TypeId TypeString "CHAR wire type"
                  Expect.equal code.ColumnLength 32u "CHAR utf8mb4 byte width"
                  Expect.isTrue (code.Flags &&& NotNullFlag <> 0us) "NOT_NULL flag"
                  Expect.equal state.TypeId TypeString "ENUM wire type"
                  Expect.isTrue (state.Flags &&& EnumFlag <> 0us) "ENUM flag"
                  Expect.isTrue (state.Flags &&& UniqueKeyFlag <> 0us) "UNIQUE_KEY flag"
              | _, other -> failtestf "expected an empty typed resultset, got %A" other

          testCase "a bare system variable reports its own result metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, _ = handle session "SELECT id FROM t"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong ] "SELECT set a real type"

              let session, _ = handle session "SELECT @@version"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeVarString ] "system variables report text metadata"

              let session, result = handle session "SELECT @@max_allowed_packet, @@innodb_file_per_table, @@restrict_fk_on_non_standard_key"

              match result with
              | ResultSet(_, [ [ Some packet; Some filePerTable; Some restrictForeignKeys ] ]) ->
                  Expect.equal packet (string Fsdb.Limits.maxAllowedPacket) "live packet limit"
                  Expect.equal filePerTable "ON" "InnoDB capability"
                  Expect.equal restrictForeignKeys "ON" "MySQL 8.4 foreign-key behavior"
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeLongLong; TypeVarString; TypeVarString ]
                      "numeric variables retain numeric wire metadata"
              | other -> failtestf "unexpected system-variable result: %A" other

          testCase "RANK/DENSE_RANK/NTILE report LONGLONG and PERCENT_RANK reports DOUBLE over the wire"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, v INT)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 10), (2, 20)"

              match
                  handle
                      session
                      "SELECT RANK() OVER (ORDER BY v) AS r, DENSE_RANK() OVER (ORDER BY v) AS dr, PERCENT_RANK() OVER (ORDER BY v) AS pr, NTILE(2) OVER (ORDER BY v) AS nt FROM t"
              with
              | session, ResultSet([ "r"; "dr"; "pr"; "nt" ], _) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeLongLong; TypeLongLong; TypeDouble; TypeLongLong ]
                      "RANK/DENSE_RANK/NTILE are integers, PERCENT_RANK is a double, same as real MySQL"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version returns the server version"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version" |> snd with
              | ResultSet(cols, [ [ Some v ] ]) ->
                  Expect.equal cols [ "@@version" ] "column name"
                  Expect.equal v ServerVersion "version value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version, @@version_comment returns both columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version, @@version_comment" |> snd with
              | ResultSet(cols, [ row ]) ->
                  Expect.equal cols [ "@@version"; "@@version_comment" ] "columns"
                  Expect.equal (List.length row) 2 "row has two values"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@unknown_var returns a 1193 unknown-system-variable error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@totally_not_a_var" |> snd with
              | Err(1193, msg) -> Expect.stringContains msg "totally_not_a_var" "message names the variable"
              | other -> failtestf "expected a 1193 error, got %A" other

          testCase "the Connector/J connection probe (auto_increment_increment, transaction_isolation) resolves"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT @@session.auto_increment_increment AS auto_increment_increment, @@character_set_client AS character_set_client, @@session.transaction_isolation AS transaction_isolation"
                  |> snd
              with
              | ResultSet(_, [ [ Some "1"; Some _; Some "REPEATABLE-READ" ] ]) -> ()
              | other -> failtestf "expected all three variables resolved, got %A" other

          testCase "MySqlConnector's REPEATABLE READ transaction handshake is accepted"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ" with
              | session, Affected 0UL ->
                  Expect.equal
                      (session.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "REPEATABLE-READ")
                      "the advertised isolation matches FSDB's transaction snapshots"
              | _, other -> failtestf "expected MySqlConnector's transaction preamble to return OK, got %A" other

          testCase "SELECT @@version_comment LIMIT 1 tolerates the trailing LIMIT clause"
          <| fun _ ->
              // mysql CLI probes the connection banner with exactly this
              // query at connect time.
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "select @@version_comment limit 1" |> snd with
              | ResultSet([ "@@version_comment" ], [ [ Some _ ] ]) -> ()
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET NAMES utf8mb4 returns OK"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET NAMES utf8mb4" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "SET NAMES updates character_set_client, reflected by SELECT @@character_set_client"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET NAMES latin1"

              match handle session "SELECT @@character_set_client" |> snd with
              | ResultSet(_, [ [ Some "latin1" ] ]) -> ()
              | other -> failtestf "expected latin1, got %A" other

          testCase "SET sql_mode = '...' updates the session variable, reflected by SELECT @@sql_mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'ANSI_QUOTES'"

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "ANSI_QUOTES" ] ]) -> ()
              | other -> failtestf "expected ANSI_QUOTES, got %A" other

          testCase "ONLY_FULL_GROUP_BY is enabled by default and scoped to the session"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let strictSession = create 1 store
              let permissiveSession = create 2 store
              let strictSession, _ = handle strictSession "CREATE TABLE grouped_values (group_name VARCHAR(10), label VARCHAR(10), amount INT)"

              let strictSession, _ =
                  handle strictSession "INSERT INTO grouped_values VALUES ('a', 'first', 10), ('a', 'second', 20)"

              match handle strictSession "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some modes ] ]) ->
                  Expect.isTrue
                      (modes.Split(',') |> Array.contains "ONLY_FULL_GROUP_BY")
                      "the default mode includes ONLY_FULL_GROUP_BY"
              | other -> failtestf "expected the session sql_mode, got %A" other

              match handle strictSession "SELECT group_name, label, COUNT(*) FROM grouped_values GROUP BY group_name" |> snd with
              | Err(1055, message) -> Expect.stringContains message "only_full_group_by" "grouping error names the mode"
              | other -> failtestf "expected 1055 for a nondeterministic grouped projection, got %A" other

              match handle strictSession "SELECT label, COUNT(*) FROM grouped_values" |> snd with
              | Err(1140, message) -> Expect.stringContains message "only_full_group_by" "aggregate error names the mode"
              | other -> failtestf "expected 1140 for an aggregate without GROUP BY, got %A" other

              let permissiveSession, _ = handle permissiveSession "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              match handle permissiveSession "SELECT group_name, label, COUNT(*) FROM grouped_values GROUP BY group_name" |> snd with
              | ResultSet(_, [ [ Some "a"; Some "first"; Some "2" ] ]) -> ()
              | other -> failtestf "expected the permissive session to retain first-row behavior, got %A" other

              match handle strictSession "SELECT group_name, label, COUNT(*) FROM grouped_values GROUP BY group_name" |> snd with
              | Err(1055, _) -> ()
              | other -> failtestf "expected the sibling session to remain strict, got %A" other

          testCase "ONLY_FULL_GROUP_BY accepts functionally determined columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle
                      session
                      "CREATE TABLE grouped_values (id INT PRIMARY KEY, group_name VARCHAR(10), label VARCHAR(10), amount INT, unique_name VARCHAR(10) NOT NULL UNIQUE, nullable_name VARCHAR(10) UNIQUE)"

              let session, _ =
                  handle
                      session
                      "INSERT INTO grouped_values VALUES (1, 'a', 'first', 10, 'u1', 'n1'), (2, 'a', 'second', 20, 'u2', NULL), (3, 'b', 'third', 30, 'u3', NULL)"

              let session, _ = handle session "CREATE TABLE parents (id INT PRIMARY KEY, name VARCHAR(10))"
              let session, _ = handle session "CREATE TABLE children (id INT PRIMARY KEY, parent_id INT, amount INT)"
              let session, _ = handle session "CREATE TABLE composite_values (left_key INT NOT NULL, right_key INT NOT NULL, label VARCHAR(10), UNIQUE KEY uq_pair (left_key, right_key))"
              let session, _ = handle session "INSERT INTO parents VALUES (1, 'p1'), (2, 'p2')"
              let session, _ = handle session "INSERT INTO children VALUES (1, 1, 5), (2, 1, 7), (3, 2, 9)"
              let session, _ = handle session "INSERT INTO composite_values VALUES (1, 1, 'one'), (1, 2, 'two')"

              match handle session "SELECT id, label, COUNT(*) FROM grouped_values GROUP BY id ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "first"; Some "1" ]; [ Some "2"; Some "second"; Some "1" ]; [ Some "3"; Some "third"; Some "1" ] ]) -> ()
              | other -> failtestf "expected primary-key dependency to be accepted, got %A" other

              match handle session "SELECT unique_name, label, COUNT(*) FROM grouped_values GROUP BY unique_name ORDER BY unique_name" |> snd with
              | ResultSet(_, [ [ Some "u1"; Some "first"; Some "1" ]; [ Some "u2"; Some "second"; Some "1" ]; [ Some "u3"; Some "third"; Some "1" ] ]) -> ()
              | other -> failtestf "expected non-null unique dependency to be accepted, got %A" other

              match handle session "SELECT nullable_name, label, COUNT(*) FROM grouped_values GROUP BY nullable_name" |> snd with
              | Err(1055, _) -> ()
              | other -> failtestf "expected nullable unique columns not to determine a row, got %A" other

              match
                  handle
                      session
                      "SELECT p.id, p.name, SUM(c.amount) FROM parents p JOIN children c ON c.parent_id = p.id GROUP BY p.id ORDER BY p.id"
                  |> snd
              with
              | ResultSet(_, [ [ Some "1"; Some "p1"; Some "12" ]; [ Some "2"; Some "p2"; Some "9" ] ]) -> ()
              | other -> failtestf "expected join equality to preserve primary-key dependency, got %A" other

              match handle session "SELECT left_key, right_key, label, COUNT(*) FROM composite_values GROUP BY left_key, right_key ORDER BY right_key" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "1"; Some "one"; Some "1" ]; [ Some "1"; Some "2"; Some "two"; Some "1" ] ]) -> ()
              | other -> failtestf "expected a complete composite unique key to determine the row, got %A" other

              match handle session "SELECT left_key, label, COUNT(*) FROM composite_values GROUP BY left_key" |> snd with
              | Err(1055, _) -> ()
              | other -> failtestf "expected a partial composite key not to determine the row, got %A" other

              match handle session "SELECT group_name, label, SUM(amount) FROM grouped_values WHERE label = 'first' GROUP BY group_name" |> snd with
              | ResultSet(_, [ [ Some "a"; Some "first"; Some "10" ] ]) -> ()
              | other -> failtestf "expected an ANDed equality singleton to be accepted, got %A" other

              match handle session "SELECT label, SUM(amount) FROM grouped_values WHERE 'first' = label" |> snd with
              | ResultSet(_, [ [ Some "first"; Some "10" ] ]) -> ()
              | other -> failtestf "expected a reversed singleton equality to be accepted, got %A" other

              match handle session "SELECT label, SUM(amount) FROM grouped_values WHERE label = 'first' OR id = 2" |> snd with
              | Err(1140, _) -> ()
              | other -> failtestf "expected an OR predicate not to promise a singleton, got %A" other

              match handle session "SELECT group_name, ANY_VALUE(label), COUNT(*) FROM grouped_values GROUP BY group_name ORDER BY group_name" |> snd with
              | ResultSet(_, [ [ Some "a"; Some "first"; Some "2" ]; [ Some "b"; Some "third"; Some "1" ] ]) -> ()
              | other -> failtestf "expected ANY_VALUE to opt out of dependency checking, got %A" other

          testCase "ONLY_FULL_GROUP_BY validates ORDER BY expressions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE grouped_values (group_name VARCHAR(10), label VARCHAR(10))"
              let session, _ = handle session "INSERT INTO grouped_values VALUES ('a', 'first'), ('a', 'second')"

              match handle session "SELECT group_name, COUNT(*) FROM grouped_values GROUP BY group_name ORDER BY label" |> snd with
              | Err(1055, message) -> Expect.stringContains message "ORDER BY clause" "error identifies the clause"
              | other -> failtestf "expected 1055 for a nondeterministic ordering expression, got %A" other

              match handle session "SELECT group_name, COUNT(*), ROW_NUMBER() OVER (ORDER BY label) FROM grouped_values GROUP BY group_name" |> snd with
              | Err(1055, _) -> ()
              | other -> failtestf "expected 1055 for a nondeterministic window ordering expression, got %A" other

          testCase "ANSI_QUOTES applies to later statements in the same session"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'ANSI,TRADITIONAL'"
              let session, created = handle session "CREATE TABLE \"drupal_install_test\" (\"id\" INT NOT NULL PRIMARY KEY)"

              match created with
              | Affected 0UL -> ()
              | other -> failtestf "expected ANSI-quoted DDL to succeed, got %A" other

              match handle session "SELECT \"id\" FROM \"drupal_install_test\"" |> snd with
              | ResultSet([ "id" ], []) -> ()
              | other -> failtestf "expected ANSI-quoted SELECT to succeed, got %A" other

              match handle session "SHOW INDEX FROM \"drupal_install_test\"" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected ANSI-quoted SHOW INDEX to succeed, got %A" other

              let session, _ = handle session "CREATE TABLE strict_values (id INT, unsigned_value INT UNSIGNED NOT NULL, unsigned_float FLOAT UNSIGNED)"

              match handle session "INSERT INTO strict_values (id) VALUES (1)" |> snd with
              | Err(1364, _) -> ()
              | other -> failtestf "expected TRADITIONAL to reject a missing required value, got %A" other

              match handle session "INSERT INTO strict_values VALUES (1, -1, 0)" |> snd with
              | Err(1264, _) -> ()
              | other -> failtestf "expected TRADITIONAL to reject a negative unsigned value, got %A" other

              match handle session "INSERT INTO strict_values VALUES (1, 1, -1)" |> snd with
              | Err(1264, _) -> ()
              | other -> failtestf "expected TRADITIONAL to reject a negative unsigned float, got %A" other

              match prepareStatementForSession session "SELECT \"id\" FROM \"drupal_install_test\" WHERE \"id\" = ?" with
              | Ok(Some _, 1) -> ()
              | other -> failtestf "expected ANSI-quoted prepared statement to parse, got %A" other

          testCase "parsed statements remain isolated by ANSI_QUOTES mode"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let ansiSession, _ = handle (create 1 store) "SET sql_mode = 'ANSI_QUOTES'"

              match handle ansiSession "SELECT \"missing_column\"" |> snd with
              | Err(1054, _) -> ()
              | other -> failtestf "expected an ANSI identifier lookup, got %A" other

              match handle (create 2 store) "SELECT \"missing_column\"" |> snd with
              | ResultSet([ "missing_column" ], [ [ Some "missing_column" ] ]) -> ()
              | other -> failtestf "expected a non-ANSI string literal, got %A" other

          testCase "IGNORE_SPACE controls built-in calls, reserved names, prepares, and parse caching"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let ignoreSession, _ = handle (create 2 store) "SET sql_mode = 'IGNORE_SPACE'"

              for _ in 1..2 do
                  match handle ignoreSession "SELECT COUNT (*)" |> snd with
                  | ResultSet(_, [ [ Some "1" ] ]) -> ()
                  | other -> failtestf "expected spaced COUNT under IGNORE_SPACE, got %A" other

              match handle defaultSession "SELECT COUNT (*)" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected default mode to reject spaced COUNT, got %A" other

              match handle ignoreSession "SELECT COUNT /**/ (*)" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected an intervening comment to remain invalid, got %A" other

              match prepareStatementForSession ignoreSession "SELECT COUNT (?)" with
              | Ok(Some _, 1) -> ()
              | other -> failtestf "expected an IGNORE_SPACE prepared statement, got %A" other

              match prepareStatementForSession defaultSession "SELECT COUNT (?)" with
              | Error(1064, _) -> ()
              | other -> failtestf "expected default prepared parsing to reject spaced COUNT, got %A" other

              let ignoreSession, setResult = handle ignoreSession "SET @spaced = CAST (1 AS SIGNED)"
              Expect.equal setResult (Affected 0UL) "SET expression should honor IGNORE_SPACE"

              match handle ignoreSession "SELECT @spaced" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the spaced SET expression result, got %A" other

              match handle defaultSession "SET @spaced = CAST (1 AS SIGNED)" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected default SET expression parsing to reject spaced CAST, got %A" other

              match handle ignoreSession "CREATE TABLE count (i INT)" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected COUNT to be reserved under IGNORE_SPACE, got %A" other

              match handle ignoreSession "CREATE TABLE `count` (i INT)" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected a quoted COUNT table, got %A" other

              let ansiSession, _ = handle (create 3 store) "SET sql_mode = 'ANSI'"

              match handle ansiSession "SELECT COUNT (*)" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected ANSI to imply IGNORE_SPACE, got %A" other

          testCase "PIPES_AS_CONCAT changes pipes from logical OR to concatenation"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let concatSession, _ = handle (create 2 store) "SET sql_mode = 'PIPES_AS_CONCAT'"

              match handle defaultSession "SELECT 'a' || 'b', 1 || 0, NULL || 1" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected default pipes to be logical OR, got %A" other

              match handle concatSession "SELECT 'a' || 'b', 'a' || 1 + 2, 1 + 2 || 'x', NULL || 'x'" |> snd with
              | ResultSet(_, [ [ Some "ab"; Some "2"; Some "3"; None ] ]) -> ()
              | other -> failtestf "expected PIPES_AS_CONCAT precedence and NULL propagation, got %A" other

              match prepareStatementForSession concatSession "SELECT ? || ?" with
              | Ok(Some(Select { Projections = [ FuncCall("CONCAT", [ Placeholder 0; Placeholder 1 ]), _ ] }), 2) -> ()
              | other -> failtestf "expected prepared pipes to capture concatenation mode, got %A" other

              match prepareStatementForSession defaultSession "SELECT ? || ?" with
              | Ok(Some(Select { Projections = [ BinOp(Or, Placeholder 0, Placeholder 1), _ ] }), 2) -> ()
              | other -> failtestf "expected prepared pipes to retain default OR semantics, got %A" other

              match handle concatSession "SELECT 'a' || 'b'" |> snd, handle defaultSession "SELECT 'a' || 'b'" |> snd with
              | ResultSet(_, [ [ Some "ab" ] ]), ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected parse-cache isolation between pipe modes, got %A" other

              let ansiSession, _ = handle (create 3 store) "SET sql_mode = 'ANSI'"

              match handle ansiSession "SELECT 'a' || 'b'" |> snd with
              | ResultSet(_, [ [ Some "ab" ] ]) -> ()
              | other -> failtestf "expected ANSI to imply PIPES_AS_CONCAT, got %A" other

          testCase "HIGH_NOT_PRECEDENCE binds NOT as a high-precedence unary operator"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let highNotSession, _ = handle (create 2 store) "SET sql_mode = 'HIGH_NOT_PRECEDENCE'"

              match handle defaultSession "SELECT NOT 1 BETWEEN -1 AND 1, NOT NULL IS NULL, NOT 1 + 1" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "0"; Some "0" ] ]) -> ()
              | other -> failtestf "expected default low-precedence NOT results, got %A" other

              match handle highNotSession "SELECT NOT 1 BETWEEN -1 AND 1, NOT NULL IS NULL, NOT 1 + 1" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected HIGH_NOT_PRECEDENCE results, got %A" other

              match prepareStatementForSession highNotSession "SELECT NOT ? BETWEEN -1 AND 1" with
              | Ok(Some(Select { Projections = [ Between(Not(Placeholder 0), _, _), _ ] }), 1) -> ()
              | other -> failtestf "expected prepared HIGH_NOT_PRECEDENCE AST, got %A" other

              match prepareStatementForSession defaultSession "SELECT NOT ? BETWEEN -1 AND 1" with
              | Ok(Some(Select { Projections = [ Not(Between(Placeholder 0, _, _)), _ ] }), 1) -> ()
              | other -> failtestf "expected prepared default NOT AST, got %A" other

              match handle highNotSession "SELECT NOT 1 BETWEEN -1 AND 1" |> snd, handle defaultSession "SELECT NOT 1 BETWEEN -1 AND 1" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]), ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected parse-cache isolation between NOT modes, got %A" other

          testCase "NO_AUTO_VALUE_ON_ZERO preserves explicit zero auto-increment values"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let zeroSession, _ = handle (create 2 store) "SET sql_mode = 'NO_AUTO_VALUE_ON_ZERO'"
              let defaultSession, _ = handle defaultSession "CREATE TABLE default_ids (id BIGINT AUTO_INCREMENT PRIMARY KEY, label VARCHAR(10))"
              let zeroSession, _ = handle zeroSession "CREATE TABLE zero_ids (id BIGINT AUTO_INCREMENT PRIMARY KEY, label VARCHAR(10))"

              let defaultSession, _ = handle defaultSession "INSERT INTO default_ids VALUES (0, 'zero'), (NULL, 'null')"
              let zeroSession, _ = handle zeroSession "INSERT INTO zero_ids VALUES (0, 'zero'), (NULL, 'null')"

              match handle defaultSession "SELECT id, label FROM default_ids ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "zero" ]; [ Some "2"; Some "null" ] ]) -> ()
              | other -> failtestf "expected zero to allocate an id in default mode, got %A" other

              match handle zeroSession "SELECT id, label FROM zero_ids ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "zero" ]; [ Some "1"; Some "null" ] ]) -> ()
              | other -> failtestf "expected zero to remain explicit in NO_AUTO_VALUE_ON_ZERO mode, got %A" other

              let zeroSession, upsert = handle zeroSession "INSERT INTO zero_ids VALUES (0, 'again') ON DUPLICATE KEY UPDATE label = VALUES(label)"
              Expect.equal upsert (Affected 2UL) "zero should address the existing primary key"

              let _, replace = handle zeroSession "REPLACE INTO zero_ids VALUES (0, 'replace')"
              Expect.equal replace (Affected 2UL) "REPLACE should replace the explicit zero key"

              match handle defaultSession "INSERT INTO default_ids VALUES (0, 'next')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the sibling session to retain default zero allocation, got %A" other

              match handle defaultSession "SELECT MAX(id) FROM default_ids" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | other -> failtestf "expected the sibling session's zero to allocate id 3, got %A" other

              let defaultSession, _ = handle defaultSession "CREATE TABLE default_ignore (id INT AUTO_INCREMENT PRIMARY KEY, marker INT UNIQUE)"
              let defaultSession, _ = handle defaultSession "INSERT INTO default_ignore VALUES (0, 1)"
              let defaultSession, _ = handle defaultSession "INSERT IGNORE INTO default_ignore VALUES (0, 1)"
              let defaultSession, _ = handle defaultSession "INSERT INTO default_ignore VALUES (NULL, 2)"
              let zeroSession, _ = handle zeroSession "CREATE TABLE zero_ignore (id INT AUTO_INCREMENT PRIMARY KEY, marker INT UNIQUE)"
              let zeroSession, _ = handle zeroSession "INSERT INTO zero_ignore VALUES (0, 1)"
              let zeroSession, _ = handle zeroSession "INSERT IGNORE INTO zero_ignore VALUES (0, 1)"
              let zeroSession, _ = handle zeroSession "INSERT INTO zero_ignore VALUES (NULL, 2)"

              match handle defaultSession "SELECT id FROM default_ignore ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "3" ] ]) -> ()
              | other -> failtestf "expected an ignored generated zero to reserve id 2, got %A" other

              match handle zeroSession "SELECT id FROM zero_ignore ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "0" ]; [ Some "1" ] ]) -> ()
              | other -> failtestf "expected an ignored explicit zero not to consume an id, got %A" other

          testCase "PAD_CHAR_TO_FULL_LENGTH exposes padded CHAR values"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let paddedSession, _ = handle (create 2 store) "SET sql_mode = 'PAD_CHAR_TO_FULL_LENGTH'"
              let defaultSession, _ = handle defaultSession "CREATE TABLE chars (c CHAR(5), indexed CHAR(5), unicode CHAR(3), INDEX ix_indexed (indexed))"
              let defaultSession, _ = handle defaultSession "INSERT INTO chars VALUES ('a', 'a', 'é')"

              match handle defaultSession "SELECT CONCAT('[', c, ']'), LENGTH(c), HEX(c), c = 'a' FROM chars" |> snd with
              | ResultSet(_, [ [ Some "[a]"; Some "1"; Some "61"; Some "1" ] ]) -> ()
              | other -> failtestf "expected default CHAR retrieval to trim padding, got %A" other

              match
                  handle
                      paddedSession
                      "SELECT CONCAT('[', c, ']'), LENGTH(c), HEX(c), c = 'a', c = 'a    ', HEX(CONCAT(c, 'x')) FROM chars"
                  |> snd
              with
              | ResultSet(_, [ [ Some "[a    ]"; Some "5"; Some "6120202020"; Some "0"; Some "1"; Some "612020202078" ] ]) -> ()
              | other -> failtestf "expected padded CHAR expression semantics, got %A" other

              match handle paddedSession "SELECT indexed FROM chars WHERE indexed = 'a    '" |> snd with
              | ResultSet(_, [ [ Some "a    " ] ]) -> ()
              | other -> failtestf "expected a padded indexed predicate to retain its row, got %A" other

              match handle paddedSession "SELECT indexed FROM chars WHERE indexed = 'a'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected unpadded comparison to differ in padded mode, got %A" other

              match handle paddedSession "SELECT HEX(unicode), LENGTH(unicode), CHAR_LENGTH(unicode) FROM chars" |> snd with
              | ResultSet(_, [ [ Some "C3A92020"; Some "4"; Some "3" ] ]) -> ()
              | other -> failtestf "expected CHAR padding to count Unicode scalars, got %A" other

              match handle paddedSession "SELECT CAST('a' AS CHAR(5)), HEX(CAST('a' AS CHAR(5)))" |> snd with
              | ResultSet(_, [ [ Some "a"; Some "61" ] ]) -> ()
              | other -> failtestf "expected CHAR casts to remain unpadded, got %A" other

              let defaultSession, _ = handle defaultSession "CREATE TABLE char_left (c CHAR(2))"
              let defaultSession, _ = handle defaultSession "CREATE TABLE char_right (c CHAR(3), INDEX ix_char_right (c))"
              let defaultSession, _ = handle defaultSession "INSERT INTO char_left VALUES ('a')"
              let defaultSession, _ = handle defaultSession "INSERT INTO char_right VALUES ('a')"
              let defaultSession, _ = handle defaultSession "CREATE TABLE char_json (c CHAR(3))"
              handle defaultSession "INSERT INTO char_json VALUES ('a')" |> ignore

              match handle paddedSession "SELECT COUNT(*) FROM char_left JOIN char_right ON char_left.c = char_right.c" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected different CHAR widths not to match in padded joins, got %A" other

              match
                  handle
                      paddedSession
                      "SELECT COUNT(*) FROM char_json JOIN JSON_TABLE('[\"a\"]', '$[*]' COLUMNS(c VARCHAR(3) PATH '$')) jt USING(c)"
                  |> snd
              with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected JSON_TABLE USING to compare padded CHAR values, got %A" other

          testCase "NO_UNSIGNED_SUBTRACTION produces signed integer results"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let signedSession, _ = handle (create 2 store) "SET sql_mode = 'NO_UNSIGNED_SUBTRACTION'"

              match handle defaultSession "SELECT CAST(0 AS UNSIGNED) - 1" |> snd with
              | Err(1690, message) -> Expect.stringContains message "UNSIGNED" "default subtraction remains unsigned"
              | other -> failtestf "expected default unsigned subtraction to fail, got %A" other

              let signedSession, signedResult =
                  handle signedSession "SELECT CAST(0 AS UNSIGNED) - 1, CAST(5 AS UNSIGNED) - 2, CAST(0 AS UNSIGNED) - 1.5"

              match signedResult with
              | ResultSet(_, [ [ Some "-1"; Some "3"; Some "-1.5" ] ]) -> ()
              | other -> failtestf "expected signed subtraction results, got %A" other

              match signedSession.LastResultColumnMetadata with
              | integer :: _ ->
                  Expect.equal integer.TypeId TypeLongLong "integer subtraction reports BIGINT"
                  Expect.isFalse (integer.Flags &&& UnsignedFlag <> 0us) "mode result is signed"
              | metadata -> failtestf "expected subtraction metadata, got %A" metadata

              match prepareStatementForSession signedSession "SELECT CAST(? AS UNSIGNED) - ?" with
              | Ok(Some(Select { Projections = [ BinOp(SignedSub, _, _), _ ] }), 2) -> ()
              | other -> failtestf "expected prepared signed-subtraction AST, got %A" other

              match prepareStatementForSession defaultSession "SELECT CAST(? AS UNSIGNED) - ?" with
              | Ok(Some(Select { Projections = [ BinOp(Sub, _, _), _ ] }), 2) -> ()
              | other -> failtestf "expected prepared default-subtraction AST, got %A" other

              match handle signedSession "SELECT CAST(0 AS UNSIGNED) - 1" |> snd with
              | ResultSet(_, [ [ Some "-1" ] ]) -> ()
              | other -> failtestf "expected parse-cache isolation for signed subtraction, got %A" other

              match handle signedSession "SELECT CAST(9223372036854775808 AS UNSIGNED) - 0" |> snd with
              | Err(1690, message) -> Expect.stringContains message "BIGINT value" "signed overflow names the signed domain"
              | other -> failtestf "expected signed subtraction overflow, got %A" other

              match handle defaultSession "SELECT CAST(0 AS UNSIGNED) - 1" |> snd with
              | Err(1690, _) -> ()
              | other -> failtestf "expected the sibling session to retain unsigned subtraction, got %A" other

          testCase "REAL_AS_FLOAT changes the REAL type synonym"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store
              let floatSession, _ = handle (create 2 store) "SET sql_mode = 'REAL_AS_FLOAT'"
              let ansiSession, _ = handle (create 3 store) "SET sql_mode = 'ANSI'"

              let defaultSession, defaultCreate = handle defaultSession "CREATE TABLE default_real (value REAL)"
              let floatSession, floatCreate = handle floatSession "CREATE TABLE float_real (value REAL)"
              let ansiSession, ansiCreate = handle ansiSession "CREATE TABLE ansi_real (value REAL)"
              Expect.equal defaultCreate (Affected 0UL) "default REAL DDL"
              Expect.equal floatCreate (Affected 0UL) "REAL_AS_FLOAT DDL"
              Expect.equal ansiCreate (Affected 0UL) "ANSI DDL"

              match handle defaultSession "SHOW COLUMNS FROM default_real" |> snd with
              | ResultSet(_, [ [ Some "value"; Some "double"; _; _; _; _ ] ]) -> ()
              | other -> failtestf "expected default REAL to mean DOUBLE, got %A" other

              for session, table in [ floatSession, "float_real"; ansiSession, "ansi_real" ] do
                  match handle session ("SHOW COLUMNS FROM " + table) |> snd with
                  | ResultSet(_, [ [ Some "value"; Some "float"; _; _; _; _ ] ]) -> ()
                  | other -> failtestf "expected %s REAL to mean FLOAT, got %A" table other

          testCase "NO_BACKSLASH_ESCAPES is session-scoped and cache-safe"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let defaultSession = create 1 store

              let literalSession, _ =
                  handle (create 2 store) "SET sql_mode = 'NO_BACKSLASH_ESCAPES,PIPES_AS_CONCAT'"

              for _ in 1..2 do
                  match handle defaultSession "SELECT HEX('a\\nb')" |> snd with
                  | ResultSet(_, [ [ Some "610A62" ] ]) -> ()
                  | other -> failtestf "expected default backslash escapes, got %A" other

                  match handle literalSession "SELECT HEX('a\\nb')" |> snd with
                  | ResultSet(_, [ [ Some "615C6E62" ] ]) -> ()
                  | other -> failtestf "expected literal backslashes, got %A" other

              match handle literalSession "SELECT HEX('a\\' || 'b')" |> snd with
              | ResultSet(_, [ [ Some "615C62" ] ]) -> ()
              | other -> failtestf "expected quote scanning and pipe rewriting to share the active mode, got %A" other

              match handle literalSession "SELECT 'it\\'s'" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected an unescaped quote to terminate the literal, got %A" other

              match prepareStatementForSession literalSession "SELECT CONCAT('x\\', ?)" with
              | Ok(Some(Select { Projections = [ FuncCall("CONCAT", [ Lit(VString "x\\"); Placeholder 0 ]), _ ] }), 1) -> ()
              | other -> failtestf "expected placeholder scanning to respect literal backslashes, got %A" other

              let textStatement =
                  { Ast = None
                    Sql = "SET @escaped = ?"
                    ParamCount = 1
                    LastParamTypes = None }

              let literalSession, result = executePrepared literalSession textStatement [ VString "O'Brien\\" ]
              Expect.equal result (Affected 0UL) "text-prepared substitution should use mode-aware quoting"

              match handle literalSession "SELECT @escaped" |> snd with
              | ResultSet(_, [ [ Some "O'Brien\\" ] ]) -> ()
              | other -> failtestf "expected the prepared string to round-trip, got %A" other

              let literalSession, result = handle literalSession "SET @first = 'a,b', @second = 2"
              Expect.equal result (Affected 0UL) "commas inside literal strings should not split assignments"

              match handle literalSession "SELECT @first, @second" |> snd with
              | ResultSet(_, [ [ Some "a,b"; Some "2" ] ]) -> ()
              | other -> failtestf "expected both assignments to persist, got %A" other

          testCase "SET default_storage_engine accepts InnoDB"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "SET default_storage_engine=InnoDB"

              match result with
              | Affected 0UL -> ()
              | other -> failtestf "expected SET to succeed, got %A" other

              match handle session "SELECT @@default_storage_engine" |> snd with
              | ResultSet(_, [ [ Some "InnoDB" ] ]) -> ()
              | other -> failtestf "expected InnoDB, got %A" other

          testCase "application compatibility variables have MySQL defaults"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@GLOBAL.read_only" |> snd with
              | ResultSet(_, [ [ Some "OFF" ] ]) -> ()
              | other -> failtestf "expected read_only OFF, got %A" other

              match handle session "SELECT @@tmp_table_size, @@max_heap_table_size, @@innodb_buffer_pool_size" |> snd with
              | ResultSet(_, [ [ Some "16777216"; Some "16777216"; Some "134217728" ] ]) -> ()
              | other -> failtestf "expected table-memory limits, got %A" other

              let session, result = handle session "SET sql_generate_invisible_primary_key = OFF"

              match result with
              | Affected 0UL -> ()
              | other -> failtestf "expected SET to succeed, got %A" other

              match handle session "SELECT @@sql_generate_invisible_primary_key" |> snd with
              | ResultSet(_, [ [ Some "OFF" ] ]) -> ()
              | other -> failtestf "expected invisible primary keys OFF, got %A" other

          testCase "SET accepts backtick-quoted system variable names"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "SET group_concat_max_len = 2048, `sql_mode` = 'ANSI'"

              match result with
              | Affected 0UL -> ()
              | other -> failtestf "expected quoted variable assignments to succeed, got %A" other

              match handle session "SELECT @@group_concat_max_len, @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "2048"; Some "ANSI" ] ]) -> ()
              | other -> failtestf "expected both settings, got %A" other

          testCase "SET NAMES 'x' COLLATE 'y', SESSION sql_mode='...' applies both assignments"
          <| fun _ ->
              // Laravel's MySqlConnector::configureConnection sends exactly
              // this shape — NAMES-with-COLLATE and sql_mode as one
              // comma-joined SET, not two separate statements.
              let session = create 1 (Fsdb.Storage.create ())

              let session, result =
                  handle session "SET NAMES 'utf8mb4' COLLATE 'utf8mb4_unicode_ci', SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              match result with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @@character_set_client, @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4"; Some "NO_ENGINE_SUBSTITUTION" ] ]) -> ()
              | other -> failtestf "expected both variables updated, got %A" other

          testCase "empty SET fragments do not allocate one string per separator"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SET " + String.replicate 500_000 " ,"
              GC.Collect()
              let before = GC.GetAllocatedBytesForCurrentThread()
              handle session sql |> ignore
              let allocated = GC.GetAllocatedBytesForCurrentThread() - before
              Expect.isLessThan allocated 8_000_000L "separator count does not amplify allocation"

          testCase "SET NAMES drives collation_connection, with an explicit COLLATE winning"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              // the explicit COLLATE in the Laravel connector shape wins
              let session, _ = handle session "SET NAMES utf8mb4 COLLATE utf8mb4_bin"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_bin" ] ]) -> ()
              | other -> failtestf "expected the explicit COLLATE to set collation_connection, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected literal comparisons under bin, got %A" other

              // the charset's default collation when no COLLATE is written
              let session, _ = handle session "SET NAMES utf8mb4"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_0900_ai_ci" ] ]) -> ()
              | other -> failtestf "expected SET NAMES utf8mb4 to restore ai_ci, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected literal comparisons under ai_ci again, got %A" other

              let session, _ = handle session "SET NAMES latin1"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "latin1_swedish_ci" ] ]) -> ()
              | other -> failtestf "expected the latin1 default collation, got %A" other

              // binary's byte-wise comparisons map to utf8mb4_bin
              let session, _ = handle session "SET NAMES binary"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_bin" ] ]) -> ()
              | other -> failtestf "expected SET NAMES binary to report utf8mb4_bin, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected SET NAMES binary to compare byte-wise, got %A" other

              // an unknown COLLATE is a 1273, same as the assignment form
              match handle session "SET NAMES utf8mb4 COLLATE no_such_collation" |> snd with
              | Err(1273, _) -> ()
              | other -> failtestf "expected 1273 for an unknown COLLATE in SET NAMES, got %A" other

          testCase "lc_time_names localizes temporal names per session"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let second = create 2 store
              let first, result = handle first "SET lc_time_names = 'es_MX'"

              match result with
              | Affected _ -> ()
              | other -> failtestf "expected locale assignment to succeed, got %A" other

              match handle first "SELECT DATE_FORMAT('2020-01-01','%W %a %M %b'), DAYNAME('2020-01-01'), MONTHNAME('2020-01-01'), FROM_UNIXTIME(0,'%M'), @@lc_time_names" |> snd with
              | ResultSet(_, [ [ Some "miércoles mié enero ene"; Some "miércoles"; Some "enero"; Some "enero"; Some "es_MX" ] ]) -> ()
              | other -> failtestf "expected Spanish temporal names, got %A" other

              match handle second "SELECT DATE_FORMAT('2020-01-01','%W %a %M %b')" |> snd with
              | ResultSet(_, [ [ Some "Wednesday Wed January Jan" ] ]) -> ()
              | other -> failtestf "expected the other session to retain en_US, got %A" other

              match handle first "SET lc_time_names = 'xx_YY'" |> snd with
              | Err(1649, "Unknown locale: 'xx_YY'") -> ()
              | other -> failtestf "expected 1649 for an unknown locale, got %A" other

              match handle first "SELECT DATE_FORMAT('2020-01-01')" |> snd with
              | Err(1582, "Incorrect parameter count in the call to native function 'DATE_FORMAT'") -> ()
              | other -> failtestf "expected DATE_FORMAT to validate its arity, got %A" other

          testCase "collation_connection drives LIKE, DISTINCT, and GROUP BY over literals"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              let session, _ = handle session "SET collation_connection = utf8mb4_bin"

              match handle session "SELECT 'åge' LIKE 'ÅGE'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected bin LIKE on literals to be case-sensitive, got %A" other

              match handle session "SELECT DISTINCT v FROM (SELECT 'åge' AS v UNION ALL SELECT 'ÅGE') t" |> snd with
              | ResultSet(_, rows) -> Expect.equal (List.length rows) 2 "bin connection keeps both literals distinct"
              | other -> failtestf "expected bin DISTINCT over literals to keep both, got %A" other

              match handle session "SELECT COUNT(*) FROM (SELECT 'åge' AS v UNION ALL SELECT 'ÅGE') t GROUP BY v" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "1" ] ]) -> ()
              | other -> failtestf "expected bin GROUP BY over literals to split them, got %A" other

              let session, _ = handle session "SET collation_connection = utf8mb4_0900_ai_ci"

              match handle session "SELECT 'åge' LIKE 'ÅGE'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected ai_ci LIKE on literals to fold, got %A" other

          testCase "SET collation_connection drives literal comparisons, column collations still win"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              handle session "CREATE TABLE g (name VARCHAR(20))" |> ignore
              handle session "INSERT INTO g VALUES ('age')" |> ignore

              match handle session "SELECT 'a' = 'A'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the default ai_ci to fold, got %A" other

              match handle session "SET collation_connection = utf8mb4_bin" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected SET collation_connection to succeed, got %A" other

              match handle session "SELECT 'a' = 'A'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected bin literals after SET, got %A" other

              // the column's own ai_ci still folds
              match handle session "SELECT COUNT(*) FROM g WHERE name = 'AGE'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the column collation to win, got %A" other

              // an unknown collation is MySQL's 1273
              match handle session "SET collation_connection = no_such_collation" |> snd with
              | Err(1273, _) -> ()
              | other -> failtestf "expected 1273 for an unknown collation, got %A" other

          testCase "sql_mode inside a comma-joined SET still splits on its own internal commas correctly"
          <| fun _ ->
              // The mode list itself is comma-separated *inside its quotes*
              // — `splitSetAssignments` must not split there, only on the
              // comma between this assignment and the next.
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle session "SET SESSION sql_mode='ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES', NAMES 'latin1'"

              match handle session "SELECT @@sql_mode, @@character_set_client" |> snd with
              | ResultSet(_, [ [ Some "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES"; Some "latin1" ] ]) -> ()
              | other -> failtestf "expected both variables updated, got %A" other

          testCase "GROUP_CONCAT obeys the session group_concat_max_len byte limit"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE gc (v VARCHAR(600))"
              let session, _ = handle session ("INSERT INTO gc VALUES ('" + String.replicate 600 "x" + "'), ('" + String.replicate 600 "y" + "')")

              match handle session "SELECT LENGTH(GROUP_CONCAT(v)) FROM gc" |> snd with
              | ResultSet(_, [ [ Some "1024" ] ]) -> ()
              | other -> failtestf "expected the MySQL default 1024-byte cap, got %A" other

              let session, _ = handle session "SET SESSION group_concat_max_len = 2048"

              match handle session "SELECT LENGTH(GROUP_CONCAT(v)) FROM gc" |> snd with
              | ResultSet(_, [ [ Some "1201" ] ]) -> ()
              | other -> failtestf "expected the larger session limit, got %A" other

          testCase "sql_mode inside Laravel's real comma-joined connect-time SET turns off strict coercion"
          <| fun _ ->
              // The exact statement `strict => false` sends
              // (`MySqlConnector::configureConnection`) — reproduces the
              // real-world bug where the compound form's `sql_mode` half
              // was silently dropped and every insert stayed strict.
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle session "SET NAMES 'utf8mb4' COLLATE 'utf8mb4_unicode_ci', SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              let session, _ = handle session "CREATE TABLE t (n INT)"

              match handle session "INSERT INTO t VALUES ('not a number')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the non-strict insert to succeed, got %A" other

          testCase "one connection's non-strict sql_mode doesn't leak into a sibling connection sharing the same Store"
          <| fun _ ->
              // Two independent sessions (e.g. Laravel's default + a
              // 'strict' => false read connection) sharing one Store, the
              // way `Server` hands every accepted connection the same
              // `Store` — a session's `sql_mode` must stay scoped to that
              // session, not leak to a sibling connection that shares the
              // store.
              let store = Fsdb.Storage.create ()
              let strictSession = create 1 store
              let laxSession = create 2 store
              let laxSession, _ = handle laxSession "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              let strictSession, _ = handle strictSession "CREATE TABLE t (n INT)"

              match handle strictSession "INSERT INTO t VALUES ('not a number')" |> snd with
              | Err(1366, _) -> ()
              | other -> failtestf "expected the strict sibling to still reject a bad value, got %A" other

              ignore laxSession

          testCase "zero-date modes are independent of STRICT_TRANS_TABLES"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"

              match handle session "INSERT INTO t VALUES ('0000-00-00')" |> snd with
              | Err(1292, _) -> ()
              | other -> failtestf "expected default zero-mode rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"

              match handle session "INSERT INTO t VALUES ('2020-00-01')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected strict mode without zero modes to preserve the value, got %A" other

          testCase "non-strict zero modes coerce partial dates to all-zero"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION,NO_ZERO_DATE,NO_ZERO_IN_DATE'"
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"
              let session, result = handle session "INSERT INTO t VALUES ('2020-00-01')"
              Expect.equal result (Affected 1UL) "non-strict insert succeeds"
              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Code, condition.Message))
                  [ 1264, "Out of range value for column 'd' at row 1" ]
                  "non-strict zero-date conversion records MySQL's warning"

              match handle session "SELECT d FROM t" |> snd with
              | ResultSet(_, [ [ Some "0000-00-00" ] ]) -> ()
              | other -> failtestf "expected coercion to all-zero, got %A" other

          testCase "partial zero dates compare with strings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES ('2020-00-01')"

              match handle session "SELECT d < '2020-01-01' FROM t" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected zero month to precede January, got %A" other

          testCase "zero-date defaults validate against the executing sql_mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE TABLE t (id INT, d DATE NOT NULL DEFAULT '0000-00-00')" |> snd with
              | Err(1067, _) -> ()
              | other -> failtestf "expected default zero-mode rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let session, result = handle session "CREATE TABLE t (id INT, d DATE NOT NULL DEFAULT '0000-00-00')"
              Expect.equal result (Affected 0UL) "strict mode without zero modes accepts the default"
              let session, _ = handle session "INSERT INTO t (id) VALUES (1)"

              match handle session "SELECT d FROM t" |> snd with
              | ResultSet(_, [ [ Some "0000-00-00" ] ]) -> ()
              | other -> failtestf "expected a zero-date default, got %A" other

          testCase "impossible partial dates use MySQL's zero-date fallback"
          <| fun _ ->
              let strictSession = create 1 (Fsdb.Storage.create ())
              let strictSession, _ = handle strictSession "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let strictSession, _ = handle strictSession "CREATE TABLE t (d DATE NOT NULL)"

              match handle strictSession "INSERT INTO t VALUES ('0000-02-31')" |> snd with
              | Err(1292, _) -> ()
              | other -> failtestf "expected an impossible partial date to fail, got %A" other

              let session = create 2 (Fsdb.Storage.create ())
              let session, _ = handle session "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"
              let session, result = handle session "INSERT INTO t VALUES ('0000-02-31'), ('2020-00-32')"
              Expect.equal result (Affected 2UL) "non-strict inserts succeed"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1264; 1265 ] "MySQL warning codes"

              match handle session "SELECT d FROM t" |> snd with
              | ResultSet(_, [ [ Some "0000-00-00" ]; [ Some "0000-00-00" ] ]) -> ()
              | other -> failtestf "expected all-zero fallback values, got %A" other

          testCase "non-strict invalid temporal text coerces to zero values regardless of nullability"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "CREATE TABLE t (dn DATE NULL, dnn DATE NOT NULL, xn DATETIME NULL, xnn DATETIME NOT NULL)"
              let session, result = handle session "INSERT INTO t VALUES ('abc', 'abc', 'abc', 'abc')"
              Expect.equal result (Affected 1UL) "non-strict insert succeeds"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1265; 1265; 1265; 1265 ] "one truncation warning per temporal column"

              match handle session "SELECT dn, dnn, xn, xnn FROM t" |> snd with
              | ResultSet(_, [ [ Some "0000-00-00"; Some "0000-00-00"; Some "0000-00-00 00:00:00"; Some "0000-00-00 00:00:00" ] ]) -> ()
              | other -> failtestf "expected zero temporal fallbacks, got %A" other

          testCase "typed zero-date literals validate against the executing sql_mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT DATE '0000-00-00'" |> snd with
              | Err(1525, _) -> ()
              | other -> failtestf "expected default zero-mode literal rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"

              match handle session "SELECT DATE '0000-00-00'" |> snd with
              | ResultSet(_, [ [ Some "0000-00-00" ] ]) -> ()
              | other -> failtestf "expected a zero-date result, got %A" other

          testCase "malformed typed temporal literals return 1525"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql, expected in
                  [ "SELECT DATE '01/02/2020'", "Incorrect DATE value: '01/02/2020'"
                    "SELECT TIMESTAMP '2020-01-01'", "Incorrect DATETIME value: '2020-01-01'"
                    "SELECT TIME '839:00:00'", "Incorrect TIME value: '839:00:00'" ] do
                  Expect.equal (handle session sql |> snd) (Err(1525, expected)) sql

              match handle session "SELECT 'Incorrect DATE value: \\'x\\'' nonsense garbage" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "ordinary syntax text must remain 1064, got %A" other

          testCase "SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES') isn't split on the CONCAT's own comma"
          <| fun _ ->
              // `splitSetAssignments` must track paren depth, not just quote
              // state — a function-call argument list has its own commas
              // that aren't assignment separators.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'STRICT_TRANS_TABLES'"

              match handle session "SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES')" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "an escaped quote keeps a comma inside one SET assignment"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, result = handle session "SET @x='a\\\', @y=1'"

              match result with
              | Affected _ -> ()
              | other -> failtestf "expected the escaped quote to keep the fragment intact, got %A" other

              match handle session "SELECT @x, @y" |> snd with
              | ResultSet(_, [ [ Some "a\\', @y=1"; None ] ]) -> ()
              | other -> failtestf "expected only @x to be assigned, got %A" other

          testCase "a session refuses user variables beyond its fixed memory-growth cap"
          <| fun _ ->
              let variables = seq { for i in 1..65536 -> sprintf "v%d" i, VString "1" } |> Map.ofSeq
              let session = { create 1 (Fsdb.Storage.create ()) with UserVariables = variables }

              match handle session "SET @overflow = 1" with
              | unchanged, Err(1105, "Too many user-defined variables") ->
                  Expect.equal unchanged.UserVariables.Count 65536 "the rejected SET leaves the map unchanged"
              | _, other -> failtestf "expected the user-variable cap error, got %A" other

              match handle session "SET @v1 = 2" with
              | updated, Affected _ -> Expect.equal updated.UserVariables.["v1"] (VInt 2L) "existing variables remain writable"
              | _, other -> failtestf "expected an existing variable update to succeed, got %A" other

          testCase "SELECT INTO assigns one typed row atomically"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'one')"
              let session, result = handle session "SELECT id, name INTO @chosen_id, @chosen_name FROM t"
              Expect.equal result (Affected 0UL) "SELECT INTO has no resultset"
              Expect.equal session.UserVariables.["chosen_id"] (VInt 1L) "integer type retained"
              Expect.equal session.UserVariables.["chosen_name"] (VString "one") "string type retained"

              let session, result = handle session "SELECT id INTO @chosen_id FROM t WHERE id = 99"
              Expect.equal result (Affected 0UL) "zero rows is successful"
              Expect.equal session.UserVariables.["chosen_id"] (VInt 1L) "zero rows leaves the target unchanged"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1329 ] "zero-row warning"

              let session, _ = handle session "INSERT INTO t VALUES (2, 'two')"
              let unchanged, result = handle session "SELECT id INTO @chosen_id FROM t ORDER BY id"
              Expect.equal result (Err(1172, "Result consisted of more than one row")) "multiple rows rejected"
              Expect.equal unchanged.UserVariables.["chosen_id"] (VInt 1L) "failed assignment is atomic"

              match handle unchanged "SELECT id, name INTO @chosen_id FROM t WHERE id = 1" |> snd with
              | Err(1222, _) -> ()
              | other -> failtestf "expected target-count error, got %A" other

          testCase "SQL PREPARE binds typed user variables and deallocates by name"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "PREPARE add_values FROM 'SELECT ? + ? AS total'"
              Expect.equal result (Affected 0UL) "prepared"
              let session, _ = handle session "SET @left = 2, @right = 3"

              match handle session "EXECUTE add_values USING @left, @right" with
              | session, ResultSet([ "total" ], [ [ Some "5" ] ]) ->
                  let session, result = handle session "DEALLOCATE PREPARE add_values"
                  Expect.equal result (Affected 0UL) "deallocated"

                  match handle session "EXECUTE add_values USING @left, @right" |> snd with
                  | Err(1243, _) -> ()
                  | other -> failtestf "expected unknown statement error, got %A" other
              | _, other -> failtestf "expected prepared result, got %A" other

          testCase "placeholder binding covers DO expressions"
          <| fun _ ->
              match Fsdb.Parser.parse "DO ?, ABS(?)" with
              | Ok statement ->
                  Expect.equal
                      (bindPlaceholders statement [ VInt 2L; VInt -3L ])
                      (Do [ Lit(VInt 2L); FuncCall("ABS", [ Lit(VInt -3L) ]) ])
                      "every executable expression is rewritten"
              | Error error -> failtestf "unexpected parse error: %s" error

          testCase "HANDLER reads natural and indexed row order"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE handler_rows (id INT PRIMARY KEY, grp INT, name VARCHAR(20), INDEX ix_grp_name(grp, name))"
              let session, _ = handle session "INSERT INTO handler_rows VALUES (3,2,'c'),(1,1,'b'),(4,2,'a'),(2,1,'a')"
              let session, opened = handle session "HANDLER handler_rows OPEN AS cursor"
              Expect.equal opened (Affected 0UL) "opened"

              let session, first = handle session "HANDLER cursor READ FIRST"
              Expect.equal first (ResultSet([ "id"; "grp"; "name" ], [ [ Some "1"; Some "1"; Some "b" ] ])) "natural order"

              let session, next = handle session "HANDLER cursor READ NEXT"
              Expect.equal next (ResultSet([ "id"; "grp"; "name" ], [ [ Some "2"; Some "1"; Some "a" ] ])) "natural cursor"

              let session, primary = handle session "HANDLER cursor READ `PRIMARY` FIRST"
              Expect.equal primary (ResultSet([ "id"; "grp"; "name" ], [ [ Some "1"; Some "1"; Some "b" ] ])) "primary order"

              let session, range = handle session "HANDLER cursor READ ix_grp_name > (1, 'a') WHERE id <> 3 LIMIT 2"
              Expect.equal
                  range
                  (ResultSet(
                      [ "id"; "grp"; "name" ],
                      [ [ Some "1"; Some "1"; Some "b" ]; [ Some "4"; Some "2"; Some "a" ] ]
                  ))
                  "comparison, predicate, and limit"

              let session, _ = handle session "HANDLER cursor READ `PRIMARY` FIRST WHERE id = 99"

              match handle session "HANDLER cursor READ `PRIMARY` PREV" with
              | session, ResultSet(_, [ [ Some "4"; _; _ ] ]) ->
                  let session, _ = handle session "HANDLER handler_rows OPEN AS zero_cursor"
                  let session, empty = handle session "HANDLER zero_cursor READ `PRIMARY` FIRST LIMIT 0"
                  Expect.equal empty (ResultSet([ "id"; "grp"; "name" ], [])) "LIMIT zero"

                  match handle session "HANDLER zero_cursor READ `PRIMARY` NEXT" with
                  | nextSession, ResultSet(_, [ [ Some "1"; _; _ ] ]) ->
                      let nextSession, _ = handle nextSession "HANDLER zero_cursor CLOSE"
                      let session, closed = handle nextSession "HANDLER cursor CLOSE"
                      Expect.equal closed (Affected 0UL) "closed"

                      match handle session "HANDLER cursor READ FIRST" |> snd with
                      | Err(1109, _) -> ()
                      | other -> failtestf "expected a closed handler error, got %A" other
                  | _, other -> failtestf "expected LIMIT zero to preserve position, got %A" other
              | _, other -> failtestf "expected PREV from end of index, got %A" other

          testCase "HANDLER validates lifecycle and result metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE handler_contract (id INT PRIMARY KEY, happened TIME(3))"
              let session, _ = handle session "INSERT INTO handler_contract VALUES (1, '01:02:03.125')"
              let session, _ = handle session "CREATE VIEW handler_view AS SELECT * FROM handler_contract"

              match handle session "HANDLER handler_view OPEN" |> snd with
              | Err(1347, _) -> ()
              | other -> failtestf "expected a base-table error, got %A" other

              let session, _ = handle session "HANDLER handler_contract OPEN AS h"

              match handle session "HANDLER handler_contract OPEN AS h" |> snd with
              | Err(1066, _) -> ()
              | other -> failtestf "expected a duplicate alias error, got %A" other

              match handle session "HANDLER h READ missing FIRST" |> snd with
              | Err(1176, _) -> ()
              | other -> failtestf "expected a missing-index error, got %A" other

              match handle session "HANDLER h READ `PRIMARY` = (1, 2)" |> snd with
              | Err(1070, _) -> ()
              | other -> failtestf "expected a key-part error, got %A" other

              match handle session "HANDLER h READ `PRIMARY` FIRST" with
              | read, ResultSet(_, [ [ Some "1"; Some "01:02:03.125" ] ]) ->
                  Expect.equal (read.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong; TypeTime ] "declared metadata"
                  Expect.equal read.LastResultColumnMetadata.[1].Decimals 3uy "TIME precision"
              | _, other -> failtestf "expected typed handler row, got %A" other

              match prepareStatementForSession session "HANDLER h READ FIRST" with
              | Error(1295, _) -> ()
              | other -> failtestf "expected prepared protocol refusal, got %A" other

          testCase "HANDLER follows temporary and schema lifetimes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TEMPORARY TABLE handler_temp (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO handler_temp VALUES (2), (1)"
              let session, _ = handle session "HANDLER handler_temp OPEN AS temp_cursor"

              match handle session "HANDLER temp_cursor READ `PRIMARY` FIRST" with
              | session, ResultSet(_, [ [ Some "1" ] ]) ->
                  let session, _ = handle session "ALTER TABLE handler_temp ADD COLUMN label VARCHAR(10)"

                  match handle session "HANDLER temp_cursor READ FIRST" |> snd with
                  | Err(1109, _) -> ()
                  | other -> failtestf "expected ALTER to close the handler, got %A" other

                  let session, _ = handle session "HANDLER handler_temp OPEN AS temp_cursor"
                  let session, _ = handle session "BEGIN"

                  match handle session "HANDLER temp_cursor READ FIRST" with
                  | session, Err(1192, _) ->
                      let session, _ = handle session "ROLLBACK"

                      match handle session "HANDLER temp_cursor READ FIRST" |> snd with
                      | ResultSet(_, [ [ Some "1"; None ] ]) -> ()
                      | other -> failtestf "expected the handler after rollback, got %A" other
                  | _, other -> failtestf "expected an active-transaction refusal, got %A" other
              | _, other -> failtestf "expected a temporary indexed row, got %A" other

          testCase "HANDLER reads wait behind explicit write locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE handler_locked (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO handler_locked VALUES (1)"
              let reader, _ = handle (create 2 store) "SET SESSION innodb_lock_wait_timeout = 1"
              let reader, _ = handle reader "HANDLER handler_locked OPEN AS h"
              let holder, _ = handle (create 3 store) "LOCK TABLES handler_locked WRITE"

              match handle reader "HANDLER h READ FIRST" |> snd with
              | Err(1205, _) -> ()
              | other -> failtestf "expected a lock wait timeout, got %A" other

              let _, _ = handle holder "UNLOCK TABLES"

              match handle reader "HANDLER h READ FIRST" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the read after unlock, got %A" other

          testCase "HANDLER detects schema changes from other sessions"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let owner = create 1 store
              let owner, _ = handle owner "CREATE TABLE handler_schema (id INT PRIMARY KEY)"
              let owner, _ = handle owner "HANDLER handler_schema OPEN AS h"
              let _, altered = handle (create 2 store) "ALTER TABLE handler_schema ADD COLUMN label VARCHAR(10)"
              Expect.equal altered (Affected 0UL) "altered"

              match handle owner "HANDLER h READ FIRST" with
              | owner, Err(1109, _) ->
                  Expect.isFalse (Map.containsKey "h" owner.TableHandlers) "invalidated cursor removed"
              | _, other -> failtestf "expected the changed table to close the handler, got %A" other

          testCase "LOCK TABLES accepts MySQL lock-list syntax"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "CREATE TABLE u (id INT)"
              let session, result = handle session "LOCK TABLES t READ, u AS writer WRITE"
              Expect.equal result (Affected 0UL) "lock list accepted"

              match handle session "LOCK TABLES t SHARE" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected an invalid lock mode to fail, got %A" other

              Expect.equal (handle session "UNLOCK TABLE" |> snd) (Affected 0UL) "singular unlock accepted"

              let session, result = handle session "LOCK/**/TABLES t/**/READ"
              Expect.equal result (Affected 0UL) "comments may separate lock-list tokens"
              Expect.equal (handle session "UNLOCK TABLES" |> snd) (Affected 0UL) "plural unlock accepted"

              let session, result = handle session "LOCK-- comment\nTABLES-- comment\nt-- comment\nREAD"
              Expect.equal result (Affected 0UL) "line comments may separate lock-list tokens"
              Expect.equal (handle session "UNLOCK TABLES" |> snd) (Affected 0UL) "line-comment lock released"

          testCase "LOCK TABLES enforces aliases and access modes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE lock_access (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "CREATE TABLE unlocked_access (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO lock_access VALUES (1, 10)"
              let session, _ = handle session "INSERT INTO unlocked_access VALUES (1)"
              let session, _ = handle session "LOCK TABLES lock_access READ"

              match handle session "SELECT n FROM lock_access" |> snd with
              | ResultSet(_, [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected the read-locked table to remain readable, got %A" other

              match handle session "UPDATE lock_access SET n=11 WHERE id=1" |> snd with
              | Err(1099, _) -> ()
              | other -> failtestf "expected the read lock to reject writes, got %A" other

              match handle session "SELECT id FROM unlocked_access" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected an unlisted table to be rejected, got %A" other

              match handle session "SELECT n FROM lock_access AS other_name" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected an unlisted alias to be rejected, got %A" other

              let session, _ = handle session "UNLOCK TABLES"
              let session, _ = handle session "LOCK TABLES lock_access AS writable WRITE"

              match handle session "SELECT n FROM lock_access" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected the base name to be hidden by the lock alias, got %A" other

              match handle session "UPDATE lock_access AS writable SET n=11 WHERE id=1" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the write alias to permit updates, got %A" other

              let session, _ = handle session "UNLOCK TABLES"

              match handle session "SELECT n FROM lock_access" |> snd with
              | ResultSet(_, [ [ Some "11" ] ]) -> ()
              | other -> failtestf "expected unrestricted access after UNLOCK TABLES, got %A" other

          testCase "table READ locks share and table WRITE locks exclude"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE lock_compatibility (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO lock_compatibility VALUES (1, 10)"
              let reader, _ = handle (create 2 store) "LOCK TABLES lock_compatibility READ"

              match handle (create 3 store) "SELECT n FROM lock_compatibility" |> snd with
              | ResultSet(_, [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected concurrent reads under a READ lock, got %A" other

              let waitingWriter =
                  System.Threading.Tasks.Task.Run(fun () ->
                      handle (create 4 store) "UPDATE lock_compatibility SET n=11 WHERE id=1")

              Expect.isFalse (waitingWriter.Wait(TimeSpan.FromMilliseconds 100.0)) "the READ lock blocks writers"
              let reader, _ = handle reader "UNLOCK TABLES"
              Expect.isTrue (waitingWriter.Wait(TimeSpan.FromSeconds 2.0)) "the writer continues after the READ lock releases"

              match waitingWriter.Result |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the waiting writer to succeed, got %A" other

              let writer, _ = handle reader "LOCK TABLES lock_compatibility WRITE"

              let waitingReader =
                  System.Threading.Tasks.Task.Run(fun () ->
                      handle (create 5 store) "SELECT n FROM lock_compatibility")

              Expect.isFalse (waitingReader.Wait(TimeSpan.FromMilliseconds 100.0)) "the WRITE lock blocks readers"
              let _, _ = handle writer "UNLOCK TABLES"
              Expect.isTrue (waitingReader.Wait(TimeSpan.FromSeconds 2.0)) "the reader continues after the WRITE lock releases"

              match waitingReader.Result |> snd with
              | ResultSet(_, [ [ Some "11" ] ]) -> ()
              | other -> failtestf "expected the waiting reader to succeed, got %A" other

          testCase "ordinary statements overlap while explicit acquisition waits"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE statement_lock_scope (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO statement_lock_scope VALUES (1, 10)"
              use entered = new Threading.ManualResetEventSlim(false)
              use release = new Threading.ManualResetEventSlim(false)

              let registry =
                  Fsdb.Functions.empty
                  |> Fsdb.Functions.registerScalar
                      "HOLD_STATEMENT"
                      (fun _ ->
                          entered.Set()
                          release.Wait()
                          VInt 1L)

              let reader =
                  { create 2 store with CustomFunctions = registry }

              let reading =
                  System.Threading.Tasks.Task.Run(fun () -> handle reader "SELECT HOLD_STATEMENT() FROM statement_lock_scope")

              Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 2.0)) "the ordinary read is active"

              let updating =
                  System.Threading.Tasks.Task.Run(fun () -> handle (create 3 store) "UPDATE statement_lock_scope SET n=11 WHERE id=1")

              Expect.isTrue (updating.Wait(TimeSpan.FromSeconds 2.0)) "ordinary reads and writes use storage concurrency"

              match updating.Result |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the overlapping update to succeed, got %A" other

              let locking =
                  System.Threading.Tasks.Task.Run(fun () -> handle (create 4 store) "LOCK TABLES statement_lock_scope WRITE")

              Expect.isFalse (locking.Wait(TimeSpan.FromMilliseconds 100.0)) "an explicit WRITE lock waits for the active read"
              release.Set()
              Expect.isTrue (reading.Wait(TimeSpan.FromSeconds 2.0)) "the reader finishes after release"
              Expect.isTrue (locking.Wait(TimeSpan.FromSeconds 2.0)) "the explicit lock acquires after the reader"

              match locking.Result with
              | owner, Affected 0UL ->
                  let _, _ = handle owner "UNLOCK TABLES"
                  ()
              | _, other -> failtestf "expected the explicit lock to acquire, got %A" other

          testCase "table lock statements follow MySQL transaction boundaries"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE lock_transactions (id INT PRIMARY KEY)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO lock_transactions VALUES (1)"
              let session, _ = handle session "LOCK TABLES lock_transactions WRITE"
              let session, _ = handle session "ROLLBACK"
              let session, committed = handle session "SELECT COUNT(*) FROM lock_transactions WHERE id=1"
              Expect.equal committed (ResultSet([ "COUNT(*)" ], [ [ Some "1" ] ])) "LOCK TABLES commits the preceding transaction"
              let session, _ = handle session "SET autocommit=0"
              let session, inserted = handle session "INSERT INTO lock_transactions VALUES (2)"
              Expect.equal inserted (Affected 1UL) "the explicit WRITE lock permits the insert"
              Expect.isSome session.Tx "autocommit zero opens a transaction"
              let session, _ = handle session "UNLOCK TABLES"
              Expect.isNone session.Tx "UNLOCK TABLES commits the open transaction"
              let session, _ = handle session "ROLLBACK"
              let session, unlocked = handle session "SELECT COUNT(*) FROM lock_transactions WHERE id=2"
              Expect.equal unlocked (ResultSet([ "COUNT(*)" ], [ [ Some "1" ] ])) "UNLOCK TABLES commits while explicit locks are held"
              let session, _ = handle session "LOCK TABLES lock_transactions WRITE"
              let session, _ = handle session "START TRANSACTION"

              match handle session "SELECT COUNT(*) FROM lock_transactions" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected START TRANSACTION to release the explicit lock, got %A" other

          testCase "temporary tables remain accessible in explicit lock mode"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE permanent_lock_target (id INT PRIMARY KEY)"
              let session, _ = handle session "CREATE TEMPORARY TABLE temporary_lock_target (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO permanent_lock_target VALUES (1)"
              let session, _ = handle session "LOCK TABLES permanent_lock_target READ"

              match handle session "INSERT INTO temporary_lock_target VALUES (1)" with
              | session, Affected 1UL ->
                  match handle session "SELECT COUNT(*) FROM temporary_lock_target" |> snd with
                  | ResultSet(_, [ [ Some "1" ] ]) -> ()
                  | other -> failtestf "expected the temporary table to remain readable, got %A" other
              | _, other -> failtestf "expected the temporary table to remain writable, got %A" other

              let session, _ = handle session "UNLOCK TABLES"
              let session, _ = handle session "LOCK TABLES temporary_lock_target WRITE"

              match handle session "SELECT id FROM permanent_lock_target" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected a temporary-only lock list to restrict permanent tables, got %A" other

          testCase "information schema remains accessible in explicit lock mode"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE information_schema_lock_target (id INT PRIMARY KEY)"
              let session, _ = handle session "LOCK TABLES information_schema_lock_target READ"

              match handle session "SELECT table_name FROM information_schema.tables WHERE table_name='information_schema_lock_target'" |> snd with
              | ResultSet(_, [ [ Some "information_schema_lock_target" ] ]) -> ()
              | other -> failtestf "expected INFORMATION_SCHEMA to remain readable, got %A" other

              match handle session "SELECT user FROM mysql.user" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected ordinary system tables to remain subject to the lock list, got %A" other

          testCase "view locks include base tables without exposing their names"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE locked_view_base (id INT PRIMARY KEY, n INT)"
              let setup, _ = handle setup "INSERT INTO locked_view_base VALUES (1, 10)"
              let _, _ = handle setup "CREATE VIEW locked_view AS SELECT id,n FROM locked_view_base"
              let holder, _ = handle (create 2 store) "LOCK TABLES locked_view READ"

              match handle holder "SELECT n FROM locked_view" |> snd with
              | ResultSet(_, [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected the locked view to remain readable, got %A" other

              match handle holder "SELECT n FROM locked_view_base" |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected the implicit base lock to remain hidden, got %A" other

              let waiting =
                  System.Threading.Tasks.Task.Run(fun () ->
                      handle (create 3 store) "UPDATE locked_view_base SET n=11 WHERE id=1")

              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the view's base lock blocks writers"
              let _, _ = handle holder "UNLOCK TABLES"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the base writer continues after the view unlocks"

              match waiting.Result |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the waiting base update to succeed, got %A" other

          testCase "table locks include trigger dependencies without exposing them"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE locked_trigger_source (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE locked_trigger_log (id INT PRIMARY KEY)"

              let _, _ =
                  handle
                      setup
                      "CREATE TRIGGER lock_dependency AFTER INSERT ON locked_trigger_source FOR EACH ROW INSERT INTO locked_trigger_log VALUES(NEW.id)"

              let holder, _ = handle (create 2 store) "LOCK TABLES locked_trigger_source WRITE"

              match handle holder "INSERT INTO locked_trigger_source VALUES(1)" with
              | holder, Affected 1UL ->
                  match handle holder "SELECT id FROM locked_trigger_log" |> snd with
                  | Err(1100, _) -> ()
                  | other -> failtestf "expected the implicit trigger-table lock to remain hidden, got %A" other
              | _, other -> failtestf "expected the trigger write under its implicit dependency lock, got %A" other

              let waiting =
                  System.Threading.Tasks.Task.Run(fun () ->
                      handle (create 3 store) "SELECT id FROM locked_trigger_log")

              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the trigger dependency blocks other readers"
              let _, _ = handle holder "UNLOCK TABLES"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the trigger table becomes readable after unlock"

              match waiting.Result |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the trigger row after unlock, got %A" other

          testCase "LOCK TABLES acquisition is atomic and replaces prior locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE atomic_lock_first (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE atomic_lock_second (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO atomic_lock_first VALUES (1)"
              let _, _ = handle setup "INSERT INTO atomic_lock_second VALUES (1)"
              let holder, _ = handle (create 2 store) "LOCK TABLES atomic_lock_second WRITE"
              let contender, _ = handle (create 3 store) "LOCK TABLES atomic_lock_first READ"

              let waiting =
                  System.Threading.Tasks.Task.Run(fun () ->
                      handle contender "LOCK TABLES atomic_lock_first WRITE, atomic_lock_second WRITE")

              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the combined lock waits for every table"

              match handle (create 4 store) "UPDATE atomic_lock_first SET id=id WHERE id=1" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected the contender's prior lock to release before waiting, got %A" other

              let _, _ = handle holder "UNLOCK TABLES"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the atomic lock list acquires after its blocker releases"

              match waiting.Result with
              | contender, Affected 0UL ->
                  let _, _ = handle contender "UNLOCK TABLES"
                  ()
              | _, other -> failtestf "expected the combined lock to succeed, got %A" other

          testCase "failed table lock acquisition leaves no partial or prior locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE timeout_lock_first (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE timeout_lock_second (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO timeout_lock_first VALUES (1)"
              let _, _ = handle setup "INSERT INTO timeout_lock_second VALUES (1)"
              let holder, _ = handle (create 2 store) "LOCK TABLES timeout_lock_second WRITE"
              let contender, _ = handle (create 3 store) "LOCK TABLES timeout_lock_first READ"
              let contender, _ = handle contender "SET innodb_lock_wait_timeout=1"

              match handle contender "LOCK TABLES timeout_lock_first WRITE, timeout_lock_second WRITE" |> snd with
              | Err(1205, _) -> ()
              | other -> failtestf "expected the combined lock to time out, got %A" other

              match handle (create 4 store) "UPDATE timeout_lock_first SET id=id WHERE id=1" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected the prior and partial locks to be absent after timeout, got %A" other

              let _, _ = handle holder "UNLOCK TABLES"
              ()

          testCase "a valid replacement lock list releases prior locks before resolution"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE replaced_lock (id INT PRIMARY KEY)"
              let session, _ = handle session "CREATE TABLE newly_accessible_after_error (id INT PRIMARY KEY)"
              let session, _ = handle session "LOCK TABLES replaced_lock READ"

              match handle session "LOCK TABLES missing_replacement_lock WRITE" with
              | session, Err(1146, _) ->
                  match handle session "SELECT id FROM newly_accessible_after_error" |> snd with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected the prior lock mode to be gone after resolution failed, got %A" other
              | _, other -> failtestf "expected the missing replacement table to fail, got %A" other

          testCase "table locks cover CTE and subquery aliases"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE lock_tree_source (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO lock_tree_source VALUES (1)"
              let session, _ = handle session "LOCK TABLES lock_tree_source AS outer_source READ"

              let sql =
                  "WITH selected AS (SELECT id FROM lock_tree_source AS outer_source WHERE EXISTS (SELECT 1 FROM lock_tree_source AS inner_source WHERE inner_source.id=outer_source.id)) SELECT id FROM selected"

              match handle session sql |> snd with
              | Err(1100, _) -> ()
              | other -> failtestf "expected the unlisted subquery alias to fail, got %A" other

              let session, result =
                  handle session "LOCK TABLES lock_tree_source AS outer_source READ, lock_tree_source AS inner_source READ"

              Expect.equal result (Affected 0UL) "both source aliases locked"

              match handle session sql |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the fully locked CTE query to succeed, got %A" other

              let _, _ = handle session "UNLOCK TABLES"
              ()

          testCase "table locks distinguish read and write sides of joined DML"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE joined_lock_target (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "CREATE TABLE joined_lock_source (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO joined_lock_target VALUES (1, 0)"
              let session, _ = handle session "INSERT INTO joined_lock_source VALUES (1, 10)"

              let session, _ =
                  handle session "LOCK TABLES joined_lock_target AS target WRITE, joined_lock_source AS source READ"

              match
                  handle
                      session
                      "UPDATE joined_lock_target AS target JOIN joined_lock_source AS source ON source.id=target.id SET target.n=source.n"
                  |> snd
              with
              | Affected 1UL -> ()
              | other -> failtestf "expected the writable join target to update, got %A" other

              match
                  handle
                      session
                      "UPDATE joined_lock_target AS target JOIN joined_lock_source AS source ON source.id=target.id SET source.n=target.n"
                  |> snd
              with
              | Err(1099, _) -> ()
              | other -> failtestf "expected the read-locked join source to reject updates, got %A" other

              match
                  handle
                      session
                      "DELETE target FROM joined_lock_target AS target JOIN joined_lock_source AS source ON source.id=target.id"
                  |> snd
              with
              | Affected 1UL -> ()
              | other -> failtestf "expected the named writable delete target to succeed, got %A" other

              let _, _ = handle session "UNLOCK TABLES"
              ()

          testCase "permanent DDL releases explicit table locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE ddl_lock_source (id INT PRIMARY KEY)"
              let session, _ = handle session "LOCK TABLES ddl_lock_source READ"
              let session, result = handle session "CREATE TABLE ddl_lock_created (id INT PRIMARY KEY)"
              Expect.equal result (Affected 0UL) "DDL accepted"

              match handle session "SELECT id FROM ddl_lock_source" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected DDL to release the explicit lock mode, got %A" other

          testCase "table locks require SELECT and LOCK TABLES privileges"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE TABLE privileged_lock (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER 'lock_user'@'%'"
              let _, _ = handle root "GRANT SELECT ON fsdb.privileged_lock TO 'lock_user'@'%'"

              let limited =
                  { create 2 store with
                      User = "lock_user"
                      AccountHost = "%"
                      LoginUser = "lock_user" }

              match handle limited "LOCK TABLES privileged_lock READ" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected LOCK TABLES privilege enforcement, got %A" other

              let _, _ = handle root "GRANT LOCK TABLES ON fsdb.* TO 'lock_user'@'%'"

              match handle limited "LOCK TABLES privileged_lock WRITE" with
              | limited, Affected 0UL ->
                  let _, _ = handle limited "UNLOCK TABLES"
                  ()
              | _, other -> failtestf "expected SELECT plus LOCK TABLES to permit a WRITE lock, got %A" other

          testCase "closing a session releases its explicit table locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE abandoned_table_lock (id INT PRIMARY KEY)"
              let _, _ = handle setup "INSERT INTO abandoned_table_lock VALUES (1)"
              let holder, _ = handle (create 2 store) "LOCK TABLES abandoned_table_lock WRITE"
              closeSession holder

              match handle (create 3 store) "SELECT id FROM abandoned_table_lock" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected disconnect cleanup to release the table lock, got %A" other

          testCase "single-statement stored procedures persist and execute"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "CREATE PROCEDURE answer() SELECT 42 AS value"
              Expect.equal result (Affected 0UL) "created"

              match handle session "CALL answer()" with
              | session, ProcedureResult([ "value" ], [ [ Some "42" ] ]) ->
                  match handle session "SHOW PROCEDURE STATUS" |> snd with
                  | ResultSet(_, [ row ]) ->
                      Expect.equal row.[0] (Some "fsdb") "routine schema"
                      Expect.equal row.[1] (Some "answer") "routine name"
                  | other -> failtestf "expected routine status, got %A" other

                  match handle session "SHOW CREATE PROCEDURE answer" |> snd with
                  | ResultSet(_, [ [ Some "answer"; _; Some ddl; _; _; _ ] ]) ->
                      Expect.stringContains ddl "PROCEDURE `answer`() SQL SECURITY DEFINER SELECT 42 AS value" "stored definition"
                  | other -> failtestf "expected create procedure, got %A" other

                  match handle session "SELECT routine_name FROM information_schema.routines WHERE routine_schema = 'fsdb'" |> snd with
                  | ResultSet(_, [ [ Some "answer" ] ]) -> ()
                  | other -> failtestf "expected information_schema routine, got %A" other

                  let session, result = handle session "DROP PROCEDURE answer"
                  Expect.equal result (Affected 0UL) "dropped"

                  match handle session "CALL answer()" |> snd with
                  | Err(1305, _) -> ()
                  | other -> failtestf "expected missing procedure, got %A" other
              | _, other -> failtestf "expected procedure result, got %A" other

          testCase "routine and SET comma lists reject empty elements"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql in
                  [ "CREATE PROCEDURE invalid_parameter_list(IN value INT,, OUT doubled INT) SELECT value"
                    "CREATE PROCEDURE invalid_local_set(IN value INT) BEGIN SET value value + 1; SELECT value; END"
                    "SET @first=1,,@second=2" ] do
                  match handle session sql |> snd with
                  | Err(1064, _) -> ()
                  | other -> failtestf "expected invalid comma or assignment syntax for %s, got %A" sql other

              let session, created =
                  handle session "CREATE PROCEDURE strict_call(IN value INT, OUT doubled INT) SET doubled=value*2"

              Expect.equal created (Affected 0UL) "valid procedure created"

              match handle session "CALL strict_call(3,,@out)" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected an empty CALL argument to fail, got %A" other

          testCase "procedure declarations preserve one parameter and SQL SECURITY"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 10 LIMIT num" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected routine-only LIMIT variables, got %A" other

              let session, result =
                  handle session "CREATE PROCEDURE topics(IN num INT) SQL SECURITY INVOKER BEGIN SELECT 10 LIMIT num; END"

              Expect.equal result (Affected 0UL) "created"

              match handle session "SHOW CREATE PROCEDURE topics" |> snd with
              | ResultSet(_, [ [ Some "topics"; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "PROCEDURE `topics`(IN num INT) SQL SECURITY INVOKER" "signature retained"
              | other -> failtestf "expected parameterized procedure metadata, got %A" other

              match handle session "CALL topics(1)" |> snd with
              | ProcedureResult([ "10" ], [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected parameterized procedure result, got %A" other

          testCase "single-statement procedure blocks persist and execute"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE posts (id INT)"
              let session, _ = handle session "INSERT INTO posts VALUES (42)"

              let sql =
                  "CREATE PROCEDURE first_post() BEGIN\nSELECT id FROM posts LIMIT 1;\nEND"

              let session, result = handle session sql
              Expect.equal result (Affected 0UL) "created"

              match handle session "CALL first_post()" |> snd with
              | ProcedureResult([ "id" ], [ [ Some "42" ] ]) -> ()
              | other -> failtestf "expected procedure result, got %A" other

              match handle session "CALL `first_post`" |> snd with
              | ProcedureResult([ "id" ], [ [ Some "42" ] ]) -> ()
              | other -> failtestf "expected unparenthesized procedure result, got %A" other

          testCase "parameterized compound procedures execute typed locals and multiple resultsets"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE routine_log (n INT)"

              let definition =
                  "CREATE PROCEDURE calculate(IN n INT, IN label VARCHAR(10)) BEGIN DECLARE x INT DEFAULT n + 1; SELECT n, label, x; IF n > 1 THEN SET x = x + 10; INSERT INTO routine_log VALUES (x); ELSEIF n = 1 THEN INSERT INTO routine_log VALUES (100); ELSE INSERT INTO routine_log VALUES (0); END IF; SELECT x AS final_x; END"

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created"

              match handle session "CALL calculate(2.7, 12345)" with
              | session,
                MultipleResults
                    [ (ResultSet([ "n"; "label"; "x" ], [ [ Some "3"; Some "12345"; Some "4" ] ]), firstMetadata)
                      (ResultSet([ "final_x" ], [ [ Some "14" ] ]), finalMetadata)
                      (Affected 1UL, []) ] ->
                  Expect.equal firstMetadata.Length 3 "first result metadata"
                  Expect.equal finalMetadata.Length 1 "second result metadata"

                  match handle session "SELECT n FROM routine_log" |> snd with
                  | ResultSet(_, [ [ Some "14" ] ]) -> ()
                  | other -> failtestf "expected routine write, got %A" other
              | _, other -> failtestf "expected compound routine results, got %A" other

          testCase "stored procedures compose CASE and labeled loop control"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let definition =
                  """CREATE PROCEDURE control_flow(IN n INT, OUT answer INT)
                      main/*label*/:/*block*/BEGIN
                        DECLARE i INT DEFAULT 0;
                        SET answer = 0;
                        counting/*label*/:/*loop*/WHILE i < n DO
                          SET i = i + 1;
                          CASE
                            WHEN i = 2 THEN ITERATE counting;
                            WHEN i = 4 THEN LEAVE counting;
                            ELSE SET answer = answer + i;
                          END CASE;
                        END/*loop*/WHILE/*label*/counting;
                        REPEAT
                          SET answer = answer + 10;
                          SET i = i - 1;
                        UNTIL i = 0 END REPEAT;
                        single_pass: LOOP
                          SET answer = answer + 100;
                          LEAVE single_pass;
                        END LOOP single_pass;
                        CASE n
                          WHEN 3 THEN SET answer = answer + 1000;
                          ELSE SET answer = answer + 2000;
                        END CASE;
                      END/*label*/main"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created"

              let session, called = handle session "CALL control_flow(5, @answer)"
              Expect.equal called (Affected 0UL) "called"

              match handle session "SELECT @answer" |> snd with
              | ResultSet(_, [ [ Some "2144" ] ]) -> ()
              | other -> failtestf "expected loop result, got %A" other

              let session, called = handle session "CALL control_flow(3, @answer)"
              Expect.equal called (Affected 0UL) "called again"

              match handle session "SELECT @answer" |> snd with
              | ResultSet(_, [ [ Some "1134" ] ]) -> ()
              | other -> failtestf "expected second loop result, got %A" other

          testCase "stored-program labels and unmatched CASE use MySQL errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE PROCEDURE bad_leave() BEGIN LEAVE nowhere; END" |> snd with
              | Err(1308, "LEAVE with no matching label: nowhere") -> ()
              | other -> failtestf "expected unmatched-label error, got %A" other

              match
                  handle
                      session
                      "CREATE PROCEDURE duplicate_label() outer_label: BEGIN outer_label: LOOP LEAVE outer_label; END LOOP outer_label; END outer_label"
                  |> snd
              with
              | Err(1309, "Redefining label outer_label") -> ()
              | other -> failtestf "expected duplicate-label error, got %A" other

              let session, created =
                  handle session "CREATE PROCEDURE missing_case() BEGIN CASE 1 WHEN 2 THEN SET @selected = 1; END CASE; END"

              Expect.equal created (Affected 0UL) "created missing-case procedure"

              match handle session "CALL missing_case()" |> snd with
              | Err(1339, "Case not found for CASE statement") -> ()
              | other -> failtestf "expected CASE-not-found error, got %A" other

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE leave_block(OUT answer INT) main: BEGIN SET answer = 1; LEAVE main; SET answer = 2; END main"

              Expect.equal created (Affected 0UL) "created labeled block"
              let session, called = handle session "CALL leave_block(@answer)"
              Expect.equal called (Affected 0UL) "left block"

              match handle session "SELECT @answer" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected early block exit, got %A" other

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE local_scope(OUT answer INT) BEGIN DECLARE x INT DEFAULT 1; DECLARE y INT DEFAULT 1; BEGIN DECLARE x INT DEFAULT 2; SET x = 3; SET y = 4; END; SET answer = x * 10 + y; END"

              Expect.equal created (Affected 0UL) "created nested scope"
              let session, called = handle session "CALL local_scope(@answer)"
              Expect.equal called (Affected 0UL) "called nested scope"

              match handle session "SELECT @answer" |> snd with
              | ResultSet(_, [ [ Some "14" ] ]) -> ()
              | other -> failtestf "expected lexical local scope, got %A" other

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE nested_case(IN n INT, OUT answer INT) BEGIN CASE CASE n WHEN 1 THEN 10 ELSE 20 END WHEN 10 THEN SET answer = 100; ELSE SET answer = 200; END CASE; END"

              Expect.equal created (Affected 0UL) "created nested CASE"
              let session, called = handle session "CALL nested_case(1, @answer)"
              Expect.equal called (Affected 0UL) "called nested CASE"

              match handle session "SELECT @answer" |> snd with
              | ResultSet(_, [ [ Some "100" ] ]) -> ()
              | other -> failtestf "expected nested CASE result, got %A" other

          testCase "stored procedures signal and handle conditions by MySQL precedence"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let definition =
                  """CREATE PROCEDURE condition_flow(
                        OUT continued INT,
                        OUT exited INT,
                        OUT precedence_value INT,
                        OUT warning_value INT)
                      BEGIN
                        SET continued = 1;
                        BEGIN
                          DECLARE CONTINUE HANDLER FOR SQLSTATE '45001'
                            SET continued = continued + 10;
                          SIGNAL SQLSTATE '45001'
                            SET MYSQL_ERRNO = 60001, MESSAGE_TEXT = 'continued';
                          SET continued = continued + 1;
                        END;

                        SET exited = 1;
                        BEGIN
                          DECLARE EXIT HANDLER FOR SQLEXCEPTION
                            SET exited = exited + 10;
                          SIGNAL SQLSTATE '45002'
                            SET MYSQL_ERRNO = 60002, MESSAGE_TEXT = 'exited';
                          SET exited = 100;
                        END;
                        SET exited = exited + 1;

                        SET precedence_value = 0;
                        BEGIN
                          DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                            SET precedence_value = 1;
                          DECLARE CONTINUE HANDLER FOR SQLSTATE '45003'
                            SET precedence_value = 2;
                          DECLARE CONTINUE HANDLER FOR 60003
                            SET precedence_value = 3;
                          SIGNAL SQLSTATE '45003' SET MYSQL_ERRNO = 60003;
                        END;

                        SET warning_value = 1;
                        BEGIN
                          DECLARE CONTINUE HANDLER FOR SQLWARNING
                            SET warning_value = warning_value + 10;
                          SIGNAL SQLSTATE '01000'
                            SET MYSQL_ERRNO = 60004, MESSAGE_TEXT = 'warning';
                          SET warning_value = warning_value + 1;
                        END;
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created condition procedure"

              let session, called = handle session "CALL condition_flow(@continued, @exited, @precedence, @warning)"
              Expect.equal called (Affected 0UL) "called condition procedure"

              match handle session "SELECT @continued, @exited, @precedence, @warning" |> snd with
              | ResultSet(_, [ [ Some "12"; Some "12"; Some "3"; Some "12" ] ]) -> ()
              | other -> failtestf "expected handled condition results, got %A" other

          testCase "stored handlers expose current and stacked diagnostics"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let definition =
                  """CREATE PROCEDURE inspect_diagnostics(
                        OUT current_count INT,
                        OUT current_rows BIGINT,
                        OUT current_after_set INT,
                        OUT returned_state VARCHAR(5),
                        OUT returned_code INT,
                        OUT returned_message VARCHAR(64),
                        OUT returned_table VARCHAR(64),
                        OUT returned_column VARCHAR(64))
                      BEGIN
                        DECLARE changed INT DEFAULT 0;
                        DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                        BEGIN
                          GET CURRENT DIAGNOSTICS current_count = NUMBER, current_rows = ROW_COUNT;
                          SET changed = 1;
                          GET CURRENT DIAGNOSTICS current_after_set = NUMBER;
                          GET STACKED DIAGNOSTICS CONDITION 1
                            returned_state = RETURNED_SQLSTATE,
                            returned_code = MYSQL_ERRNO,
                            returned_message = MESSAGE_TEXT,
                            returned_table = TABLE_NAME,
                            returned_column = COLUMN_NAME,
                            @direct_code = MYSQL_ERRNO;
                        END;
                        SIGNAL SQLSTATE '45006'
                          SET MYSQL_ERRNO = 60006,
                              MESSAGE_TEXT = 'diagnostic',
                              TABLE_NAME = 'things',
                              COLUMN_NAME = 'value';
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created diagnostics procedure"

              let session, called =
                  handle
                      session
                      "CALL inspect_diagnostics(@current_count, @current_rows, @current_after_set, @returned_state, @returned_code, @returned_message, @returned_table, @returned_column)"

              Expect.equal called (Affected 0UL) "called diagnostics procedure"

              match
                  handle
                      session
                      "SELECT @current_count, @current_rows, @current_after_set, @returned_state, @returned_code, @returned_message, @returned_table, @returned_column, @direct_code"
                  |> snd
              with
              | ResultSet(_, [ [ Some "1"; Some "-1"; Some "0"; Some "45006"; Some "60006"; Some "diagnostic"; Some "things"; Some "value"; Some "60006" ] ]) -> ()
              | other -> failtestf "expected current and stacked diagnostics, got %A" other

          testCase "native and user conditions expose MySQL diagnostic origins"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE diagnostics_origin (value INT NOT NULL)"

              let definition =
                  """CREATE PROCEDURE diagnostic_origins(
                        OUT native_class VARCHAR(16),
                        OUT native_subclass VARCHAR(16),
                        OUT user_class VARCHAR(16),
                        OUT user_subclass VARCHAR(16))
                      BEGIN
                        BEGIN
                          DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                            GET STACKED DIAGNOSTICS CONDITION 1
                              native_class = CLASS_ORIGIN,
                              native_subclass = SUBCLASS_ORIGIN;
                          INSERT INTO diagnostics_origin VALUES (NULL);
                        END;
                        BEGIN
                          DECLARE CONTINUE HANDLER FOR SQLSTATE '45007'
                            GET STACKED DIAGNOSTICS CONDITION 1
                              user_class = CLASS_ORIGIN,
                              user_subclass = SUBCLASS_ORIGIN;
                          SIGNAL SQLSTATE '45007';
                        END;
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created origin procedure"

              let session, called =
                  handle
                      session
                      "CALL diagnostic_origins(@native_class, @native_subclass, @user_class, @user_subclass)"

              Expect.equal called (Affected 0UL) "called origin procedure"

              match handle session "SELECT @native_class, @native_subclass, @user_class, @user_subclass" |> snd with
              | ResultSet(_, [ [ Some "ISO 9075"; Some "ISO 9075"; Some ""; Some "" ] ]) -> ()
              | other -> failtestf "expected diagnostic origins, got %A" other

          testCase "stacked diagnostics retain every condition from the failing statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, mode = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              Expect.equal mode (Affected 0UL) "enabled non-strict conversions"

              let session, table =
                  handle session "CREATE TABLE diagnostics_values (id INT PRIMARY KEY)"

              Expect.equal table (Affected 0UL) "created diagnostics table"

              let session, seed = handle session "INSERT INTO diagnostics_values VALUES (0)"
              Expect.equal seed (Affected 1UL) "seeded duplicate key"

              let definition =
                  """CREATE PROCEDURE multiple_diagnostics(
                        OUT condition_count INT,
                        OUT first_code INT,
                        OUT second_code INT)
                      BEGIN
                        DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                        BEGIN
                          GET STACKED DIAGNOSTICS condition_count = NUMBER;
                          GET STACKED DIAGNOSTICS CONDITION 1 first_code = MYSQL_ERRNO;
                          GET STACKED DIAGNOSTICS CONDITION 2 second_code = MYSQL_ERRNO;
                        END;
                        INSERT INTO diagnostics_values VALUES ('abc'), (0);
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created multiple-condition procedure"

              let session, called =
                  handle session "CALL multiple_diagnostics(@condition_count, @first_code, @second_code)"

              Expect.equal called (Affected 0UL) "handled the failing statement"

              match handle session "SELECT @condition_count, @first_code, @second_code" |> snd with
              | ResultSet(_, [ [ Some "2"; Some "1366"; Some "1062" ] ]) -> ()
              | other -> failtestf "expected both stacked conditions, got %A" other

          testCase "stored handlers catch scalar function errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let definition =
                  """CREATE PROCEDURE scalar_diagnostic(OUT returned_code INT)
                      BEGIN
                        DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                          GET STACKED DIAGNOSTICS CONDITION 1 returned_code = MYSQL_ERRNO;
                        SELECT RANDOM_BYTES(-1);
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created scalar diagnostic procedure"

              let session, called = handle session "CALL scalar_diagnostic(@returned_code)"
              Expect.equal called (Affected 0UL) "handled scalar function error"

              match handle session "SELECT @returned_code" |> snd with
              | ResultSet(_, [ [ Some "1690" ] ]) -> ()
              | other -> failtestf "expected the scalar error diagnostic, got %A" other

          testCase "stored cursors iterate ordered rows and signal NOT FOUND"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE cursor_numbers (n INT)"
              let session, _ = handle session "INSERT INTO cursor_numbers VALUES (3), (1), (2)"

              let definition =
                  """CREATE PROCEDURE cursor_sum(
                        OUT total INT,
                        OUT seen INT,
                        OUT not_found_code INT,
                        OUT not_found_state VARCHAR(5))
                      BEGIN
                        DECLARE done INT DEFAULT 0;
                        DECLARE value INT;
                        DECLARE minimum_value INT DEFAULT 3;
                        DECLARE numbers CURSOR FOR
                          SELECT n FROM cursor_numbers WHERE n >= minimum_value ORDER BY n;
                        DECLARE CONTINUE HANDLER FOR NOT FOUND
                        BEGIN
                          SET done = 1;
                          GET STACKED DIAGNOSTICS CONDITION 1
                            not_found_code = MYSQL_ERRNO,
                            not_found_state = RETURNED_SQLSTATE;
                        END;
                        SET total = 0;
                        SET seen = 0;
                        SET minimum_value = 1;
                        OPEN numbers;
                        read_loop: LOOP
                          FETCH numbers INTO value;
                          IF done THEN LEAVE read_loop; END IF;
                          SET total = total + value;
                          SET seen = seen + 1;
                        END LOOP;
                        CLOSE numbers;
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created cursor procedure"

              let session, called =
                  handle session "CALL cursor_sum(@total, @seen, @not_found_code, @not_found_state)"

              Expect.equal called (Affected 0UL) "called cursor procedure"

              match handle session "SELECT @total, @seen, @not_found_code, @not_found_state" |> snd with
              | ResultSet(_, [ [ Some "6"; Some "3"; Some "1329"; Some "02000" ] ]) -> ()
              | other -> failtestf "expected cursor iteration results, got %A" other

              let binaryDefinition =
                  """CREATE PROCEDURE cursor_binary(OUT fetched BINARY(1))
                      BEGIN
                        DECLARE binary_cursor CURSOR FOR SELECT X'80';
                        OPEN binary_cursor;
                        FETCH binary_cursor INTO fetched;
                        CLOSE binary_cursor;
                      END"""

              let session, created = handle session binaryDefinition
              Expect.equal created (Affected 0UL) "created binary cursor procedure"
              let session, called = handle session "CALL cursor_binary(@fetched)"
              Expect.equal called (Affected 0UL) "called binary cursor procedure"

              match handle session "SELECT HEX(@fetched)" |> snd with
              | ResultSet(_, [ [ Some "80" ] ]) -> ()
              | other -> failtestf "expected binary cursor value, got %A" other

          testCase "stored cursors report lifecycle and declaration errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE cursor_source (n INT)"
              let session, _ = handle session "INSERT INTO cursor_source VALUES (1)"

              let definitions =
                  [ """CREATE PROCEDURE fetch_closed(OUT code INT)
                         BEGIN
                           DECLARE value INT;
                           DECLARE numbers CURSOR FOR SELECT n, n + 1 FROM cursor_source;
                           DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                             GET STACKED DIAGNOSTICS CONDITION 1 code = MYSQL_ERRNO;
                           FETCH numbers INTO value, value;
                         END"""
                    """CREATE PROCEDURE open_twice(OUT code INT)
                         BEGIN
                           DECLARE numbers CURSOR FOR SELECT n FROM cursor_source;
                           DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                             GET STACKED DIAGNOSTICS CONDITION 1 code = MYSQL_ERRNO;
                           OPEN numbers;
                           OPEN numbers;
                         END"""
                    """CREATE PROCEDURE wrong_fetch_arity(OUT code INT)
                         BEGIN
                           DECLARE value INT;
                           DECLARE numbers CURSOR FOR SELECT n, n + 1 FROM cursor_source;
                           DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                             GET STACKED DIAGNOSTICS CONDITION 1 code = MYSQL_ERRNO;
                           OPEN numbers;
                           FETCH numbers INTO value;
                         END"""
                    """CREATE PROCEDURE close_twice(OUT code INT)
                         BEGIN
                           DECLARE numbers CURSOR FOR SELECT n FROM cursor_source;
                           DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                             GET STACKED DIAGNOSTICS CONDITION 1 code = MYSQL_ERRNO;
                           OPEN numbers;
                           CLOSE numbers;
                           CLOSE numbers;
                         END""" ]

              let session =
                  definitions
                  |> List.fold (fun session definition ->
                      let session, created = handle session definition
                      Expect.equal created (Affected 0UL) "created cursor error procedure"
                      session) session

              let calls =
                  [ "CALL fetch_closed(@fetch_closed)"
                    "CALL open_twice(@open_twice)"
                    "CALL close_twice(@close_twice)"
                    "CALL wrong_fetch_arity(@wrong_arity)" ]

              let session =
                  calls
                  |> List.fold (fun session call ->
                      let session, called = handle session call
                      Expect.equal called (Affected 0UL) "handled cursor lifecycle error"
                      session) session

              match handle session "SELECT @fetch_closed, @open_twice, @close_twice, @wrong_arity" |> snd with
              | ResultSet(_, [ [ Some "1326"; Some "1325"; Some "1326"; Some "1328" ] ]) -> ()
              | other -> failtestf "expected cursor lifecycle codes, got %A" other

              for sql, code in
                  [ "CREATE PROCEDURE cursor_after_handler() BEGIN DECLARE CONTINUE HANDLER FOR SQLEXCEPTION SET @handled = 1; DECLARE cursor_name CURSOR FOR SELECT 1; END", 1338
                    "CREATE PROCEDURE variable_after_cursor() BEGIN DECLARE cursor_name CURSOR FOR SELECT 1; DECLARE value INT; END", 1337
                    "CREATE PROCEDURE duplicate_cursor() BEGIN DECLARE cursor_name CURSOR FOR SELECT 1; DECLARE cursor_name CURSOR FOR SELECT 2; END", 1333
                    "CREATE PROCEDURE unknown_cursor() BEGIN OPEN missing; END", 1324
                    "CREATE PROCEDURE late_cursor() BEGIN SET @started = 1; DECLARE cursor_name CURSOR FOR SELECT 1; END", 1064 ] do
                  match handle session sql |> snd with
                  | Err(actual, _) -> Expect.equal actual code "cursor declaration error code"
                  | other -> failtestf "expected cursor declaration error %d, got %A" code other

          testCase "stored procedures execute dynamic SQL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE dynamic_items (id INT PRIMARY KEY, label VARCHAR(20))"
              let session, _ = handle session "INSERT INTO dynamic_items VALUES (1, 'one'), (2, 'two')"

              let definition =
                  """CREATE PROCEDURE dynamic_label(IN wanted INT)
                      BEGIN
                        SET @statement_sql = 'SELECT label FROM dynamic_items WHERE id = ?';
                        SET @wanted = wanted;
                        PREPARE selected FROM @statement_sql;
                        EXECUTE selected USING @wanted;
                        DEALLOCATE PREPARE selected;
                      END"""

              let session, created = handle session definition
              Expect.equal created (Affected 0UL) "created dynamic SQL procedure"

              match handle session "CALL dynamic_label(2)" with
              | session, MultipleResults [ (ResultSet(_, [ [ Some "two" ] ]), _); (Affected 0UL, []) ] ->
                  Expect.isEmpty session.TextStatements "procedure deallocated its statement"
              | _, other -> failtestf "expected dynamic procedure result, got %A" other

              let persistentDefinition =
                  """CREATE PROCEDURE prepare_persistent()
                      PREPARE persistent_statement FROM 'SELECT 44'"""

              let session, created = handle session persistentDefinition
              Expect.equal created (Affected 0UL) "created persistent prepare procedure"
              let session, called = handle session "CALL prepare_persistent()"
              Expect.equal called (Affected 0UL) "prepared statement in procedure"

              match handle session "EXECUTE persistent_statement" |> snd with
              | ResultSet(_, [ [ Some "44" ] ]) -> ()
              | other -> failtestf "expected persistent prepared result, got %A" other

              match
                  handle
                      session
                      "CREATE PROCEDURE invalid_dynamic_source() BEGIN DECLARE statement_sql TEXT DEFAULT 'SELECT 1'; PREPARE invalid_statement FROM statement_sql; END"
                  |> snd
              with
              | Err(1064, _) -> ()
              | other -> failtestf "expected local PREPARE source rejection, got %A" other

          testCase "stored functions return typed scalar values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE function_source (value INT)"
              let session, _ = handle session "INSERT INTO function_source VALUES (8)"

              let definitions =
                  [ "CREATE FUNCTION doubled(value INT) RETURNS INT DETERMINISTIC RETURN value * 2"
                    "CREATE FUNCTION magnitude(value INT) RETURNS INT BEGIN IF value < 0 THEN RETURN -value; END IF; RETURN value; END"
                    "CREATE FUNCTION quadrupled(value INT) RETURNS INT RETURN doubled(doubled(value))"
                    "CREATE FUNCTION clipped(value DECIMAL(5,2)) RETURNS DECIMAL(5,2) RETURN value"
                    "CREATE FUNCTION handled(value INT) RETURNS INT BEGIN DECLARE CONTINUE HANDLER FOR SQLWARNING SET value = 9; SIGNAL SQLSTATE '01000'; RETURN value; END"
                    "CREATE FUNCTION cursor_value() RETURNS INT BEGIN DECLARE fetched INT; DECLARE values_cursor CURSOR FOR SELECT value FROM function_source; OPEN values_cursor; FETCH values_cursor INTO fetched; CLOSE values_cursor; RETURN fetched; END" ]

              let session =
                  definitions
                  |> List.fold (fun current definition ->
                      let current, created = handle current definition
                      Expect.equal created (Affected 0UL) "created stored function"
                      current) session

              match handle session "SELECT function_schema, function_name FROM mysql.functions ORDER BY function_name" |> snd with
              | ResultSet(_, rows) ->
                  Expect.sequenceEqual
                      rows
                      [ [ Some "fsdb"; Some "clipped" ]
                        [ Some "fsdb"; Some "cursor_value" ]
                        [ Some "fsdb"; Some "doubled" ]
                        [ Some "fsdb"; Some "handled" ]
                        [ Some "fsdb"; Some "magnitude" ]
                        [ Some "fsdb"; Some "quadrupled" ] ]
                      "stored function catalog rows"
              | other -> failtestf "expected stored function catalog rows, got %A" other

              match Fsdb.Storage.scanList session.Store "mysql" "functions" with
              | Ok(_, rows) ->
                  Expect.equal
                      (rows |> List.choose Fsdb.Engine.SystemCatalog.StoredFunction.tryRead |> List.map _.Name |> List.sort)
                      [ "clipped"; "cursor_value"; "doubled"; "handled"; "magnitude"; "quadrupled" ]
                      "stored function catalog decoding"
              | Error error -> failtestf "expected stored function storage rows, got %A" error

              for sql, expected in
                  [ "SELECT doubled(3)", "6"
                    "SELECT fsdb.magnitude(-4)", "4"
                    "SELECT quadrupled(5)", "20"
                    "SELECT clipped(123.456)", "123.46"
                    "SELECT handled(1)", "9"
                    "SELECT cursor_value()", "8" ] do
                  match handle session sql |> snd with
                  | ResultSet(_, [ [ Some actual ] ]) -> Expect.equal actual expected "stored function value"
                  | other -> failtestf "expected stored function value for %s, got %A" sql other

              let session, created =
                  handle session "CREATE PROCEDURE call_function(IN value INT) SELECT doubled(value)"

              Expect.equal created (Affected 0UL) "created procedure that calls a function"

              match handle session "CALL call_function(6)" |> snd with
              | ProcedureResult(_, [ [ Some "12" ] ]) -> ()
              | other -> failtestf "expected stored procedure function result, got %A" other

              match prepareStatementForSession session "SELECT doubled(?)" with
              | Ok(Some ast, 1) ->
                  let statement =
                      { Ast = Some ast
                        Sql = "SELECT doubled(?)"
                        ParamCount = 1
                        LastParamTypes = None }

                  match executePrepared session statement [ VInt 7L ] with
                  | preparedSession, ResultSet(_, [ [ Some "14" ] ]) ->
                      match preparedSession.LastResultColumnMetadata with
                      | [ metadata ] -> Expect.equal metadata.TypeId TypeLong "prepared stored function metadata"
                      | metadata -> failtestf "expected prepared stored function metadata, got %A" metadata
                  | _, other -> failtestf "expected prepared stored function value, got %A" other
              | other -> failtestf "expected stored function prepare, got %A" other

              match handle session "SELECT doubled(3), clipped(1.2) LIMIT 0" with
              | metadataSession, ResultSet(_, []) ->
                  match metadataSession.LastResultColumnMetadata with
                  | [ integer; decimal ] ->
                      Expect.equal integer.TypeId TypeLong "integer function return metadata"
                      Expect.equal decimal.TypeId TypeNewDecimal "decimal function return metadata"
                      Expect.equal decimal.Decimals 2uy "decimal function return scale"
                  | metadata -> failtestf "expected two stored function metadata records, got %A" metadata
              | _, other -> failtestf "expected empty stored function metadata result, got %A" other

              let session, created = handle session "CREATE FUNCTION abs(value INT) RETURNS VARCHAR(8) RETURN 'stored'"
              Expect.equal created (Affected 0UL) "created function with native name"

              match handle session "SELECT ABS(-3), fsdb.ABS(-3)" |> snd with
              | ResultSet(_, [ [ Some "3"; Some "stored" ] ]) -> ()
              | other -> failtestf "expected native function precedence and qualified stored function, got %A" other

              match handle session "SELECT ABS(-3), fsdb.ABS(-3) LIMIT 0" with
              | metadataSession, ResultSet(_, []) ->
                  match metadataSession.LastResultColumnMetadata with
                  | [ native; stored ] ->
                      Expect.notEqual native.TypeId TypeVarString "native function metadata"
                      Expect.equal stored.TypeId TypeVarString "qualified stored function metadata"
                  | metadata -> failtestf "expected native and stored function metadata, got %A" metadata
              | _, other -> failtestf "expected empty function precedence metadata result, got %A" other

              match handle session "SHOW CREATE FUNCTION doubled" |> snd with
              | ResultSet([ "Function"; "sql_mode"; "Create Function"; _; _; _ ], [ [ Some "doubled"; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "FUNCTION `doubled`(value INT) RETURNS int" "function DDL"
                  Expect.stringContains ddl "DETERMINISTIC" "function characteristic"
              | other -> failtestf "expected SHOW CREATE FUNCTION, got %A" other

              match handle session "SHOW FUNCTION STATUS" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal rows.Length (definitions.Length + 1) "function status rows"
                  Expect.isTrue (rows |> List.forall (fun row -> row.[2] = Some "FUNCTION")) "function status type"
              | other -> failtestf "expected SHOW FUNCTION STATUS, got %A" other

              match
                  handle
                      session
                      "SELECT ROUTINE_TYPE, DATA_TYPE, IS_DETERMINISTIC, SQL_DATA_ACCESS FROM information_schema.ROUTINES WHERE ROUTINE_NAME = 'doubled'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "FUNCTION"; Some "int"; Some "YES"; Some "CONTAINS SQL" ] ]) -> ()
              | other -> failtestf "expected function information schema row, got %A" other

              let session, _ = handle session "CREATE DATABASE routine_other"
              let session, _ =
                  handle session "CREATE FUNCTION schema_marker() RETURNS VARCHAR(8) RETURN 'fsdb'"
              let session, _ =
                  handle session "CREATE FUNCTION routine_other.schema_marker() RETURNS VARCHAR(8) RETURN 'other'"
              let session, _ =
                  handle session "CREATE VIEW function_schema_view AS SELECT schema_marker() AS marker"
              let session, _ = handle session "USE routine_other"

              match handle session "SELECT marker FROM fsdb.function_schema_view" |> snd with
              | ResultSet(_, [ [ Some "fsdb" ] ]) -> ()
              | other -> failtestf "expected a view to resolve functions in its own schema, got %A" other

          testCase "stored functions validate bodies and calls"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql, code in
                  [ "CREATE FUNCTION missing_return(value INT) RETURNS INT BEGIN SET value = value + 1; END", 1320
                    "CREATE FUNCTION bad_out(OUT value INT) RETURNS INT RETURN value", 1064
                    "CREATE FUNCTION result_function() RETURNS INT BEGIN SELECT 1; RETURN 1; END", 1415
                    "CREATE FUNCTION writing_function() RETURNS INT BEGIN INSERT INTO function_target VALUES (1); RETURN 1; END", 1235
                    "CREATE FUNCTION dynamic_function() RETURNS INT BEGIN PREPARE selected FROM 'SELECT 1'; RETURN 1; END", 1336
                    "CREATE PROCEDURE invalid_return() RETURN 1", 1313 ] do
                  match handle session sql |> snd with
                  | Err(actual, _) -> Expect.equal actual code "stored function validation error"
                  | other -> failtestf "expected error %d, got %A" code other

              let session, created = handle session "CREATE FUNCTION one_argument(value INT) RETURNS INT RETURN value"
              Expect.equal created (Affected 0UL) "created argument function"

              let session, duplicate =
                  handle session "CREATE FUNCTION IF NOT EXISTS one_argument(value INT) RETURNS INT RETURN 0"

              Expect.equal duplicate (Affected 0UL) "ignored existing function"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "Note"; Some "1304"; Some message ] ]) ->
                  Expect.stringContains message "one_argument" "duplicate function warning"
              | other -> failtestf "expected duplicate function note, got %A" other

              let session, created =
                  handle session "CREATE FUNCTION spaced_nondeterministic() RETURNS INT NOT   DETERMINISTIC RETURN 1"

              Expect.equal created (Affected 0UL) "created nondeterministic function"

              let session, created =
                  handle session "CREATE FUNCTION commented_function() RETURNS INT COMMENT 'BEGIN RETURN' RETURN 1"

              Expect.equal created (Affected 0UL) "function comment does not become its body"

              match handle session "SELECT commented_function()" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected function with a keyword-bearing comment, got %A" other

              match
                  handle
                      session
                      "SELECT IS_DETERMINISTIC FROM information_schema.ROUTINES WHERE ROUTINE_NAME = 'spaced_nondeterministic'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "NO" ] ]) -> ()
              | other -> failtestf "expected nondeterministic routine metadata, got %A" other

              match handle session "CREATE FUNCTION missing_schema.function_name() RETURNS INT RETURN 1" |> snd with
              | Err(1049, _) -> ()
              | other -> failtestf "expected unknown function schema error, got %A" other

              let session, created = handle session "CREATE FUNCTION recursive_function() RETURNS INT RETURN recursive_function()"
              Expect.equal created (Affected 0UL) "created recursive function"

              match handle session "SELECT one_argument()" |> snd with
              | Err(1318, _) -> ()
              | other -> failtestf "expected argument-count error, got %A" other

              match handle session "SELECT recursive_function()" |> snd with
              | Err(1424, _) -> ()
              | other -> failtestf "expected recursive function refusal, got %A" other

              let session, dropped = handle session "DROP FUNCTION one_argument"
              Expect.equal dropped (Affected 0UL) "dropped stored function"

              match handle session "SELECT one_argument(1)" |> snd with
              | Err(1305, _) -> ()
              | other -> failtestf "expected dropped function to be unavailable, got %A" other

              let session, _ = handle session "CREATE TABLE routine_commit (value INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO routine_commit VALUES (1)"
              let session, created =
                  handle session "CREATE FUNCTION commit_probe() RETURNS INT RETURN 1"

              Expect.equal created (Affected 0UL) "created function after transaction write"
              Expect.isNone session.Tx "function DDL commits the transaction"

              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT value FROM routine_commit" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected pre-function transaction write to stay committed, got %A" other

          testCase "stored procedures call procedures with local outputs"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session =
                  [ """CREATE PROCEDURE inner_values(IN source INT, OUT doubled INT, INOUT running INT)
                         BEGIN
                           SELECT 'inner', source;
                           SET doubled = source * 2;
                           SET running = running + source;
                         END"""
                    """CREATE PROCEDURE outer_values(IN source INT, OUT result INT)
                         BEGIN
                           DECLARE running INT DEFAULT 10;
                           CALL inner_values(source, result, running);
                           SELECT 'outer', result, running;
                         END"""
                    """CREATE PROCEDURE recursive_call()
                         BEGIN
                           CALL recursive_call();
                         END""" ]
                  |> List.fold (fun session definition ->
                      let session, created = handle session definition
                      Expect.equal created (Affected 0UL) "created nested procedure"
                      session) session

              let session, called = handle session "CALL outer_values(3, @result)"

              match called with
              | MultipleResults
                  [ (ResultSet(_, [ [ Some "inner"; Some "3" ] ]), _)
                    (ResultSet(_, [ [ Some "outer"; Some "6"; Some "13" ] ]), _)
                    (Affected 0UL, []) ] ->
                  ()
              | other -> failtestf "expected nested procedure resultsets, got %A" other

              match handle session "SELECT @result" |> snd with
              | ResultSet(_, [ [ Some "6" ] ]) -> ()
              | other -> failtestf "expected nested OUT value, got %A" other

              match handle session "CALL recursive_call()" |> snd with
              | Err(1456, message) -> Expect.stringContains message "Recursive limit 0" "recursion limit"
              | other -> failtestf "expected recursive CALL refusal, got %A" other

          testCase "nested procedure errors retain prior results and reach handlers"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE inner_failure() BEGIN SELECT 'before'; SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 60020; END"

              Expect.equal created (Affected 0UL) "created failing procedure"

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE outer_handler(OUT code INT) BEGIN DECLARE CONTINUE HANDLER FOR SQLEXCEPTION GET STACKED DIAGNOSTICS CONDITION 1 code = MYSQL_ERRNO; CALL inner_failure(); SELECT 'after', code; END"

              Expect.equal created (Affected 0UL) "created handling procedure"

              match handle session "CALL outer_handler(@code)" with
              | session,
                MultipleResults
                    [ (ResultSet(_, [ [ Some "before" ] ]), _)
                      (ResultSet(_, [ [ Some "after"; Some "60020" ] ]), _)
                      (Affected 0UL, []) ] ->
                  Expect.equal session.UserVariables.["code"] (VInt 60020L) "handled nested error"
              | _, other -> failtestf "expected handled nested procedure results, got %A" other

          testCase "SIGNAL preserves named conditions and RESIGNAL diagnostics"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, created =
                  handle
                      session
                      """CREATE PROCEDURE named_signal()
                          BEGIN
                            DECLARE custom_condition CONDITION FOR SQLSTATE '45004';
                            SIGNAL custom_condition
                              SET MYSQL_ERRNO = 60004,
                                  MESSAGE_TEXT = 'named condition',
                                  TABLE_NAME = 'things',
                                  COLUMN_NAME = 'value';
                          END"""

              Expect.equal created (Affected 0UL) "created named signal"
              let session, signaled = handle session "CALL named_signal()"

              match Fsdb.Executor.errorInfo signaled with
              | Some error ->
                  Expect.equal error.Code 60004 "signal error code"
                  Expect.equal error.State "45004" "signal SQLSTATE"
                  Expect.equal error.Message "named condition" "signal message"
                  Expect.equal error.Information.["table_name"] "things" "signal table name"
                  Expect.equal error.Information.["column_name"] "value" "signal column name"
              | None -> failtestf "expected named SIGNAL error, got %A" signaled

              let session, created =
                  handle
                      session
                      """CREATE PROCEDURE changed_signal()
                          BEGIN
                            DECLARE EXIT HANDLER FOR SQLEXCEPTION
                            BEGIN
                              RESIGNAL SQLSTATE '45009'
                                SET MYSQL_ERRNO = 60009, MESSAGE_TEXT = 'changed condition';
                            END;
                            SIGNAL SQLSTATE '45008'
                              SET MYSQL_ERRNO = 60008, MESSAGE_TEXT = 'original condition';
                          END"""

              Expect.equal created (Affected 0UL) "created resignal procedure"
              let _, signaled = handle session "CALL changed_signal()"

              match Fsdb.Executor.errorInfo signaled with
              | Some error ->
                  Expect.equal error.Code 60009 "resignal error code"
                  Expect.equal error.State "45009" "resignal SQLSTATE"
                  Expect.equal error.Message "changed condition" "resignal message"
              | None -> failtestf "expected RESIGNAL error, got %A" signaled

          testCase "condition declarations reject invalid scope and duplicate handlers"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let definitions =
                  [ "CREATE PROCEDURE bad_state() BEGIN SIGNAL SQLSTATE '00001'; END", 1407
                    "CREATE PROCEDURE missing_condition() BEGIN SIGNAL missing; END", 1319
                    "CREATE PROCEDURE duplicate_handler() BEGIN DECLARE CONTINUE HANDLER FOR SQLWARNING SET @a = 1; DECLARE EXIT HANDLER FOR SQLWARNING SET @a = 2; END",
                    1413 ]

              for definition, expectedCode in definitions do
                  match handle session definition |> snd with
                  | Err(code, _) -> Expect.equal code expectedCode "condition declaration error"
                  | other -> failtestf "expected condition declaration error %d, got %A" expectedCode other

          testCase "unhandled warnings continue and RESIGNAL requires an active handler"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE warning_signal(OUT value INT) BEGIN SIGNAL SQLSTATE '01001' SET MYSQL_ERRNO = 60010, MESSAGE_TEXT = 'routine warning'; SET value = 7; END"

              Expect.equal created (Affected 0UL) "created warning procedure"
              let session, called = handle session "CALL warning_signal(@value)"
              Expect.equal called (Affected 0UL) "unhandled warning continued"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 60010 ] "warning diagnostics"

              match handle session "CREATE PROCEDURE stray_resignal() BEGIN RESIGNAL; END" with
              | session, Affected 0UL ->
                  match handle session "CALL stray_resignal()" |> snd with
                  | Err(1645, _) -> ()
                  | other -> failtestf "expected RESIGNAL error 1645, got %A" other
              | _, other -> failtestf "expected RESIGNAL procedure creation, got %A" other

          testCase "OUT and INOUT procedure parameters write typed user variables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, created =
                  handle
                      session
                      "CREATE PROCEDURE adjust(IN a INT, OUT b INT, INOUT c INT) BEGIN SELECT a, b, c; SET b = a + 1; SET c = c + a; END"

              Expect.equal created (Affected 0UL) "created"
              let session, _ = handle session "SET @b = 99, @c = 5"

              match handle session "CALL adjust(3, @b, @c)" with
              | session,
                MultipleResults
                    [ (ResultSet([ "a"; "b"; "c" ], [ [ Some "3"; None; Some "5" ] ]), _)
                      (Affected 0UL, []) ] ->
                  Expect.equal session.UserVariables.["b"] (VInt 4L) "OUT value"
                  Expect.equal session.UserVariables.["c"] (VInt 8L) "INOUT value"
              | _, other -> failtestf "expected OUT result, got %A" other

              match handle session "CALL adjust(3, 4, @c)" |> snd with
              | Err(1414, _) -> ()
              | other -> failtestf "expected OUT target error, got %A" other

              let fullVariables = seq { for index in 1..65536 -> sprintf "v%d" index, VInt(int64 index) } |> Map.ofSeq
              let capped = { session with UserVariables = fullVariables }

              match handle capped "CALL adjust(3, @overflow, @v1)" with
              | unchanged, MultipleResults [ (ResultSet _, _); (Err(1105, "Too many user-defined variables"), []) ] ->
                  Expect.equal unchanged.UserVariables fullVariables "failed OUT assignment is atomic"
              | _, other -> failtestf "expected OUT variable cap error, got %A" other

          testCase "procedure declarations validate argument counts and local targets"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, created = handle session "CREATE PROCEDURE one(IN value INT) SELECT value"
              Expect.equal created (Affected 0UL) "created"

              match handle session "CALL one()" |> snd with
              | Err(1318, message) -> Expect.stringContains message "expected 1" "argument count"
              | other -> failtestf "expected argument-count error, got %A" other

              match handle session "CREATE PROCEDURE invalid_local() BEGIN SET missing = 1; END" |> snd with
              | Err(1193, message) -> Expect.stringContains message "missing" "local name"
              | other -> failtestf "expected unknown-local error, got %A" other

          testCase "procedure SQL SECURITY selects the execution account"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store

              let apply session sql =
                  let next, result = handle session sql
                  TestSupport.Sql.expectOk result sql
                  next

              let root = apply root "CREATE TABLE secret (n INT)"
              let root = apply root "INSERT INTO secret VALUES (9)"
              let root = apply root "CREATE USER 'owner'@'%'"
              let root = apply root "CREATE USER 'caller'@'%'"
              let root = apply root "CREATE USER 'maker'@'%'"
              let root = apply root "GRANT SELECT, EXECUTE ON fsdb.* TO 'owner'@'%'"
              let root = apply root "GRANT EXECUTE ON fsdb.* TO 'caller'@'%'"
              let root = apply root "GRANT CREATE ROUTINE ON fsdb.* TO 'maker'@'%'"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' PROCEDURE definer_probe() SQL SECURITY DEFINER SELECT CURRENT_USER(), USER(), DATABASE(), (SELECT n FROM secret)"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' PROCEDURE invoker_probe() SQL SECURITY INVOKER SELECT CURRENT_USER(), USER(), DATABASE()"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' PROCEDURE invoker_secret() SQL SECURITY INVOKER SELECT n FROM secret"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' PROCEDURE dynamic_definer() SQL SECURITY DEFINER BEGIN SET @dynamic_sql = 'SELECT n FROM secret'; PREPARE dynamic_secret FROM @dynamic_sql; EXECUTE dynamic_secret; DEALLOCATE PREPARE dynamic_secret; END"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' PROCEDURE dynamic_invoker() SQL SECURITY INVOKER BEGIN SET @dynamic_sql = 'SELECT n FROM secret'; PREPARE dynamic_secret FROM @dynamic_sql; EXECUTE dynamic_secret; DEALLOCATE PREPARE dynamic_secret; END"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' FUNCTION function_definer() RETURNS VARCHAR(100) SQL SECURITY DEFINER RETURN CONCAT(CURRENT_USER(), '|', USER(), '|', (SELECT n FROM secret))"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' FUNCTION function_invoker() RETURNS INT SQL SECURITY INVOKER RETURN (SELECT n FROM secret)"

              let root =
                  apply
                      root
                      "CREATE DEFINER='owner'@'%' FUNCTION function_identity() RETURNS VARCHAR(100) SQL SECURITY INVOKER RETURN CURRENT_USER()"

              let root =
                  apply
                      root
                      "CREATE DEFINER=`owner`@`%` SQL SECURITY DEFINER VIEW function_identity_view AS SELECT function_identity() AS identity"

              let root = apply root "GRANT SELECT ON fsdb.function_identity_view TO 'caller'@'%'"

              let caller =
                  { create 2 store with
                      User = "caller"
                      AccountHost = "%"
                      LoginUser = "caller"
                      ClientHost = "localhost" }

              match handle caller "CALL definer_probe()" |> snd with
              | ProcedureResult(_, [ [ Some "owner@%"; Some "caller@localhost"; Some "fsdb"; Some "9" ] ]) -> ()
              | other -> failtestf "expected definer execution identity and privileges, got %A" other

              match handle root "SHOW CREATE PROCEDURE definer_probe" |> snd with
              | ResultSet(_, [ [ _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "DEFINER=`owner`@`%`" "explicit definer retained"
              | other -> failtestf "expected explicit-definer DDL, got %A" other

              match handle caller "CALL invoker_probe()" |> snd with
              | ProcedureResult(_, [ [ Some "caller@%"; Some "caller@localhost"; Some "fsdb" ] ]) -> ()
              | other -> failtestf "expected invoker execution identity, got %A" other

              match handle caller "CALL invoker_secret()" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected the invoker's missing SELECT privilege, got %A" other

              match handle caller "CALL dynamic_definer()" |> snd with
              | ProcedureResult(_, [ [ Some "9" ] ]) -> ()
              | other -> failtestf "expected dynamic SQL to use definer privileges, got %A" other

              match handle caller "CALL dynamic_invoker()" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected dynamic SQL to use invoker privileges, got %A" other

              match handle caller "SELECT function_definer()" |> snd with
              | ResultSet(_, [ [ Some "owner@%|caller@localhost|9" ] ]) -> ()
              | other -> failtestf "expected stored function definer identity and privileges, got %A" other

              match handle caller "SELECT function_invoker()" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected stored function invoker privileges, got %A" other

              match handle caller "SELECT identity FROM function_identity_view" |> snd with
              | ResultSet(_, [ [ Some "owner@%" ] ]) -> ()
              | other -> failtestf "expected a view definer to become the function invoker, got %A" other

              match handle caller "SHOW CREATE FUNCTION function_definer" |> snd with
              | ResultSet(_, [ [ Some "function_definer"; _; None; _; _; _ ] ]) -> ()
              | other -> failtestf "expected an EXECUTE grantee to see redacted function DDL, got %A" other

              match
                  handle
                      caller
                      "SELECT ROUTINE_NAME, ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_NAME = 'function_definer'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "function_definer"; None ] ]) -> ()
              | other -> failtestf "expected redacted routine information for an EXECUTE grantee, got %A" other

              match handle caller "SHOW FUNCTION STATUS" |> snd with
              | ResultSet(_, rows) ->
                  Expect.isTrue
                      (rows |> List.exists (fun row -> row.[1] = Some "function_definer"))
                      "EXECUTE grantee sees function status"
              | other -> failtestf "expected function status for an EXECUTE grantee, got %A" other

              let root = apply root "CREATE DEFINER='missing'@'%' PROCEDURE missing_definer() SELECT 1"

              match handle caller "CALL missing_definer()" |> snd with
              | Err(1449, _) -> ()
              | other -> failtestf "expected missing-definer failure, got %A" other

              match handle root "CREATE DEFINER='owner'@'%' PROCEDURE missing_access() SELECT 1" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected privileged explicit definer creation, got %A" other

              let maker =
                  { create 3 store with
                      User = "maker"
                      AccountHost = "%"
                      LoginUser = "maker"
                      ClientHost = "localhost" }

              match handle maker "CREATE DEFINER='owner'@'%' PROCEDURE stolen() SELECT 1" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected an explicit-definer privilege error, got %A" other

          testCase "procedures execute with their captured SQL and charset context"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              let apply session sql =
                  let next, result = handle session sql
                  TestSupport.Sql.expectOk result sql
                  next

              let session = apply session "CREATE TABLE procedure_log (n INT)"
              let session = apply session "CREATE TABLE procedure_source (n INT)"
              let session = apply session "INSERT INTO procedure_source VALUES (7)"
              let session = apply session "SET NAMES latin1 COLLATE latin1_bin"
              let session = apply session "SET SESSION sql_mode=''"

              let session =
                  apply
                      session
                      "CREATE PROCEDURE context_probe() SELECT @@session.sql_mode, @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection, DATABASE(), 'A' = 'a'"

              let session = apply session "CREATE PROCEDURE lax_insert() INSERT INTO procedure_log VALUES ('not an integer')"

              match
                  handle
                      session
                      "SELECT SQL_MODE, SECURITY_TYPE, CHARACTER_SET_CLIENT, COLLATION_CONNECTION, DATABASE_COLLATION FROM information_schema.ROUTINES WHERE ROUTINE_NAME = 'context_probe'"
                  |> snd
              with
              | ResultSet(_, [ [ Some ""; Some "DEFINER"; Some "latin1"; Some "latin1_bin"; Some "utf8mb4_0900_ai_ci" ] ]) -> ()
              | other -> failtestf "expected captured routine metadata, got %A" other

              let session = apply session "SET SESSION sql_mode='ANSI_QUOTES'"
              let session = apply session "CREATE PROCEDURE quoted_probe() SELECT \"n\" FROM procedure_source"
              let session = apply session "SET NAMES utf8mb4 COLLATE utf8mb4_0900_ai_ci"
              let session = apply session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"

              match handle session "CALL context_probe()" with
              | next,
                ProcedureResult(
                    _,
                    [ [ Some ""
                        Some "latin1"
                        Some "latin1"
                        Some "utf8mb4"
                        Some "latin1_bin"
                        Some "fsdb"
                        Some "0" ] ]
                ) ->
                  match handle next "SELECT @@session.sql_mode, @@character_set_client, @@collation_connection" |> snd with
                  | ResultSet(_, [ [ Some "STRICT_TRANS_TABLES"; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci" ] ]) -> ()
                  | other -> failtestf "expected caller context restoration, got %A" other
              | _, other -> failtestf "expected captured routine context, got %A" other

              let session, result = handle session "CALL lax_insert()"
              Expect.equal result (Affected 1UL) "CALL lax_insert()"
              Expect.equal (TestSupport.Sql.rows store "SELECT n FROM procedure_log") [ [ Some "0" ] ] "captured lax coercion"

              match handle session "CALL quoted_probe()" |> snd with
              | ProcedureResult([ "n" ], [ [ Some "7" ] ]) -> ()
              | other -> failtestf "expected ANSI_QUOTES parsing from routine creation, got %A" other

              let session = apply session "CREATE PROCEDURE strict_insert() INSERT INTO procedure_log VALUES ('not an integer')"
              let session = apply session "SET SESSION sql_mode=''"

              match handle session "CALL strict_insert()" |> snd with
              | Err(1366, _) -> ()
              | other -> failtestf "expected captured strict coercion, got %A" other

              Expect.equal (TestSupport.Sql.rows store "SELECT n FROM procedure_log") [ [ Some "0" ] ] "strict failure is atomic"

              let session = apply session "CREATE PROCEDURE set_mode() SET SESSION sql_mode=''"
              let session = apply session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let session = apply session "CALL set_mode()"

              match handle session "SELECT @@session.sql_mode" |> snd with
              | ResultSet(_, [ [ Some "" ] ]) -> ()
              | other -> failtestf "expected an explicit routine SET to update the caller session, got %A" other

          testCase "unterminated procedure blocks are syntax errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE PROCEDURE incomplete() BEGIN" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected syntax error, got %A" other

          testCase "scheduled event declarations persist without executing"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE event_log (value INT)"
              let session, result =
                  handle
                      session
                      "CREATE EVENT tomorrow ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 DAY DO INSERT INTO event_log VALUES (1)"
              Expect.equal result (Affected 0UL) "created"

              match handle session "SHOW EVENTS" |> snd with
              | ResultSet(_, [ row ]) ->
                  Expect.equal row.[0] (Some "fsdb") "event schema"
                  Expect.equal row.[1] (Some "tomorrow") "event name"
                  Expect.equal row.[10] (Some "ENABLED") "event status"
              | other -> failtestf "expected event status, got %A" other

              match handle session "SHOW CREATE EVENT tomorrow" |> snd with
              | ResultSet(_, [ [ Some "tomorrow"; _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "ON SCHEDULE AT '" "evaluated one-time schedule"
              | other -> failtestf "expected create event, got %A" other

              let session, recurring =
                  handle session "CREATE EVENT daily ON SCHEDULE EVERY 1 DAY DO INSERT INTO event_log VALUES (2)"
              Expect.equal recurring (Affected 0UL) "recurring event created"

              match
                  handle
                      session
                      "SELECT event_type, interval_value, interval_field, starts IS NOT NULL, ends IS NULL FROM information_schema.events WHERE event_schema = 'fsdb' AND event_name = 'daily'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "RECURRING"; Some "1"; Some "DAY"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected recurring event metadata, got %A" other

              match
                  handle
                      session
                      "SELECT event_name,execute_at IS NOT NULL,starts IS NULL FROM information_schema.events WHERE event_schema = 'fsdb' AND event_name = 'tomorrow'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "tomorrow"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected information_schema event, got %A" other

              match handle session "CREATE EVENT invalid_interval ON SCHEDULE EVERY 0 SECOND DO SELECT 1" |> snd with
              | Err(1542, "INTERVAL is either not positive or too big") -> ()
              | other -> failtestf "expected invalid interval rejection, got %A" other

              match
                  handle
                      session
                      "CREATE EVENT invalid_end ON SCHEDULE EVERY 1 SECOND STARTS CURRENT_TIMESTAMP + INTERVAL 2 SECOND ENDS CURRENT_TIMESTAMP + INTERVAL 1 SECOND DO SELECT 1"
                  |> snd
              with
              | Err(1543, "ENDS is either invalid or before STARTS") -> ()
              | other -> failtestf "expected invalid end rejection, got %A" other

              let session, past =
                  handle
                      session
                      "CREATE EVENT past_event ON SCHEDULE AT CURRENT_TIMESTAMP - INTERVAL 1 SECOND DO SELECT 1"

              Expect.equal past (Affected 0UL) "past event declaration"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ _; Some "1588"; _ ] ]) -> ()
              | other -> failtestf "expected past-event note, got %A" other

              match handle session "SHOW CREATE EVENT past_event" |> snd with
              | Err(1539, _) -> ()
              | other -> failtestf "expected past event to be discarded, got %A" other

              match handle session "SELECT COUNT(*) FROM event_log" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "event declaration must not run eagerly, got %A" other

              let session, result = handle session "DROP EVENT tomorrow"
              Expect.equal result (Affected 0UL) "dropped"
              let session, result = handle session "DROP EVENT daily"
              Expect.equal result (Affected 0UL) "recurring event dropped"

              match handle session "SHOW EVENTS" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected no events after drop, got %A" other

          testCase "event scheduler executes due events as their definers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              use scheduler = Fsdb.EventScheduler.acquire store Fsdb.Functions.empty

              let apply session sql =
                  match handle session sql with
                  | next, Affected _ -> next
                  | _, result -> failtestf "expected %s to succeed, got %A" sql result

              let session = apply session "CREATE TABLE event_log (label VARCHAR(20), actor VARCHAR(100))"
              let session = apply session "CREATE TABLE event_state (id INT PRIMARY KEY, value INT)"
              let session = apply session "CREATE TABLE event_identity (login_user VARCHAR(100), current_user_name VARCHAR(100))"
              let session = apply session "INSERT INTO event_state VALUES (1,1)"
              let session = apply session "CREATE USER event_runner"
              let session = apply session "GRANT INSERT ON fsdb.event_log TO event_runner"
              let session = apply session "GRANT INSERT ON fsdb.event_identity TO event_runner"
              let session = apply session "SET GLOBAL event_scheduler=OFF"

              let session =
                  apply
                      session
                      "CREATE DEFINER=event_runner EVENT once_run ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND DO INSERT INTO event_log VALUES ('once', CURRENT_USER())"

              let session =
                  apply
                      session
                      "CREATE EVENT compound_run ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND DO BEGIN INSERT INTO event_log VALUES ('compound-a', CURRENT_USER()); INSERT INTO event_log VALUES ('compound-b', CURRENT_USER()); END"

              let session =
                  apply
                      session
                      "CREATE EVENT kept_run ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND ON COMPLETION PRESERVE DO INSERT INTO event_log VALUES ('kept', CURRENT_USER())"

              let session =
                  apply
                      session
                      "CREATE EVENT recurring_run ON SCHEDULE EVERY 1 SECOND STARTS CURRENT_TIMESTAMP + INTERVAL 2 SECOND ENDS CURRENT_TIMESTAMP + INTERVAL 3 SECOND ON COMPLETION PRESERVE DO INSERT INTO event_log VALUES ('recurring', CURRENT_USER())"

              let session =
                  apply
                      session
                      "CREATE EVENT disabled_run ON SCHEDULE EVERY 1 SECOND DISABLE DO INSERT INTO event_log VALUES ('disabled', CURRENT_USER())"

              let session =
                  apply
                      session
                      "CREATE EVENT tx_rollback ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND DO BEGIN START TRANSACTION; UPDATE event_state SET value=99 WHERE id=1; END"

              let session =
                  apply
                      session
                      "CREATE DEFINER=event_runner EVENT identity_run ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND DO INSERT INTO event_identity VALUES (USER(),CURRENT_USER())"

              System.Threading.Thread.Sleep 2200
              Expect.equal (TestSupport.Sql.rows store "SELECT label FROM event_log") [] "disabled scheduler"
              let session = apply session "SET GLOBAL event_scheduler=ON"
              let timer = System.Diagnostics.Stopwatch.StartNew()

              let waitingForEvents () =
                  TestSupport.Sql.rows store "SELECT COUNT(*) FROM event_log" <> [ [ Some "6" ] ]
                  || TestSupport.Sql.rows store "SELECT COUNT(*) FROM mysql.events WHERE event_name='tx_rollback'" <> [ [ Some "0" ] ]
                  || TestSupport.Sql.rows store "SELECT COUNT(*) FROM event_identity" <> [ [ Some "1" ] ]

              while timer.Elapsed < TimeSpan.FromSeconds 5.0 && waitingForEvents () do
                  System.Threading.Thread.Sleep 25

              Expect.equal
                  (TestSupport.Sql.rows store "SELECT label,actor FROM event_log ORDER BY label,actor")
                  [ [ Some "compound-a"; Some "root@%" ]
                    [ Some "compound-b"; Some "root@%" ]
                    [ Some "kept"; Some "root@%" ]
                    [ Some "once"; Some "event_runner@%" ]
                    [ Some "recurring"; Some "root@%" ]
                    [ Some "recurring"; Some "root@%" ] ]
                  "scheduled bodies and definer identity"

              Expect.equal
                  (TestSupport.Sql.rows store "SELECT login_user,current_user_name FROM event_identity")
                  [ [ Some "event_scheduler@localhost"; Some "event_runner@%" ] ]
                  "scheduler login and definer identities"

              Expect.equal
                  (TestSupport.Sql.rows store "SELECT value FROM event_state")
                  [ [ Some "1" ] ]
                  "unfinished event transactions roll back"

              let session = apply session "SET SESSION innodb_lock_wait_timeout=1"
              let session = apply session "UPDATE event_state SET value=2 WHERE id=1"
              Expect.equal (TestSupport.Sql.rows store "SELECT value FROM event_state") [ [ Some "2" ] ] "transaction lock released"

              match
                  handle
                      session
                      "SELECT event_name,status,last_executed IS NOT NULL,COALESCE(last_executed > execute_at,0) FROM information_schema.events WHERE event_name IN ('kept_run','recurring_run','disabled_run') ORDER BY event_name"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "disabled_run"; Some "DISABLED"; Some "0"; Some "0" ]
                    [ Some "kept_run"; Some "DISABLED"; Some "1"; Some "1" ]
                    [ Some "recurring_run"; Some "DISABLED"; Some "1"; Some "0" ] ]
                ) -> ()
              | other -> failtestf "expected completion metadata, got %A" other

              match handle session "SHOW CREATE EVENT once_run" |> snd with
              | Err(1539, _) -> ()
              | other -> failtestf "expected one-time event removal, got %A" other

          testCase "ALTER EVENT changes schedule, status, name, schema, and body"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE event_log (value INT)"
              let session, _ = handle session "CREATE DATABASE event_archive"

              let session, _ =
                  handle
                      session
                      "CREATE EVENT past_alter ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 HOUR DO SELECT 1"

              match handle session "ALTER EVENT past_alter ON SCHEDULE AT CURRENT_TIMESTAMP - INTERVAL 1 SECOND" |> snd with
              | Err(1589, _) -> ()
              | other -> failtestf "expected past non-preserved ALTER rejection, got %A" other

              let session, _ =
                  handle
                      session
                      "CREATE EVENT kept_past_alter ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 HOUR ON COMPLETION PRESERVE DO SELECT 1"

              let session, result =
                  handle session "ALTER EVENT kept_past_alter ON SCHEDULE AT CURRENT_TIMESTAMP - INTERVAL 1 SECOND"

              Expect.equal result (Affected 0UL) "past preserved ALTER"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ _; Some "1544"; _ ] ]) -> ()
              | other -> failtestf "expected past preserved ALTER note, got %A" other

              Expect.equal
                  (TestSupport.Sql.rows store "SELECT status FROM mysql.events WHERE event_name='kept_past_alter'")
                  [ [ Some "DISABLED" ] ]
                  "past preserved ALTER status"

              let session, _ = handle session "DROP EVENT past_alter"
              let session, _ = handle session "DROP EVENT kept_past_alter"
              let session, _ =
                  handle
                      session
                      "CREATE EVENT mutable_event ON SCHEDULE EVERY 1 DAY DO INSERT INTO event_log VALUES (1)"

              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO event_log VALUES (9)"

              let session, altered =
                  handle
                      session
                      "ALTER EVENT mutable_event ON SCHEDULE EVERY 2 HOUR RENAME TO event_archive.renamed_event DISABLE DO INSERT INTO fsdb.event_log VALUES (2)"

              Expect.equal altered (Affected 0UL) "altered event"
              Expect.isNone session.Tx "ALTER EVENT commits an active transaction"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT value FROM event_log" |> snd with
              | ResultSet(_, [ [ Some "9" ] ]) -> ()
              | other -> failtestf "expected the pre-ALTER write to stay committed, got %A" other

              match handle session "SHOW EVENTS FROM event_archive" |> snd with
              | ResultSet(_, [ row ]) ->
                  Expect.equal row.[1] (Some "renamed_event") "renamed event"
                  Expect.equal row.[10] (Some "DISABLED") "disabled event"
              | other -> failtestf "expected altered event metadata, got %A" other

              match handle session "SHOW CREATE EVENT event_archive.renamed_event" |> snd with
              | ResultSet(_, [ [ Some "renamed_event"; _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "ON SCHEDULE EVERY 2 HOUR" "altered schedule"
                  Expect.stringContains ddl "INSERT INTO fsdb.event_log VALUES (2)" "altered body"
              | other -> failtestf "expected altered event DDL, got %A" other

              match handle session "ALTER EVENT mutable_event ENABLE" |> snd with
              | Err(1539, _) -> ()
              | other -> failtestf "expected the old event identity to disappear, got %A" other

              let session, _ =
                  handle
                      session
                      "CREATE EVENT event_archive.taken ON SCHEDULE EVERY 1 DAY DO INSERT INTO fsdb.event_log VALUES (3)"

              match handle session "ALTER EVENT event_archive.renamed_event RENAME TO event_archive.taken" |> snd with
              | Err(1537, _) -> ()
              | other -> failtestf "expected duplicate event rename error, got %A" other

              let session, _ = handle session "CREATE USER event_editor"
              let session, _ = handle session "GRANT EVENT ON fsdb.* TO event_editor"
              let session, _ = handle session "GRANT EVENT ON event_archive.* TO event_editor"
              let editor = { create 2 store with User = "event_editor" }

              match handle editor "ALTER EVENT event_archive.renamed_event RENAME TO fsdb.renamed_event ENABLE" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected cross-schema event rename, got %A" other

              match handle session "SHOW EVENTS FROM fsdb" |> snd with
              | ResultSet(_, [ row ]) ->
                  Expect.equal row.[1] (Some "renamed_event") "event moved back"
                  Expect.equal row.[2] (Some "event_editor@%") "altering account becomes definer"
                  Expect.equal row.[10] (Some "ENABLED") "event enabled"
              | other -> failtestf "expected redefined event metadata, got %A" other

              match handle session "ALTER EVENT fsdb.renamed_event DO not valid sql" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected altered body validation, got %A" other

          testCase "event declaration options preserve security and metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              let apply session sql =
                  match handle session sql with
                  | next, Affected _ -> next
                  | _, result -> failtestf "expected %s to succeed, got %A" sql result

              let session = apply session "CREATE TABLE event_log (value INT, note VARCHAR(100))"

              let session, created =
                  handle
                      session
                      "CREATE DEFINER=CURRENT_USER EVENT IF NOT EXISTS decorated ON SCHEDULE EVERY 1 DAY ON COMPLETION PRESERVE DISABLE ON REPLICA COMMENT 'mentions DO and ''quotes''' DO INSERT INTO event_log VALUES (1, 'DO stays in the body')"

              Expect.equal created (Affected 0UL) "created decorated event"

              let session, duplicate =
                  handle
                      session
                      "CREATE EVENT IF NOT EXISTS decorated ON SCHEDULE EVERY 2 DAY DO INSERT INTO event_log VALUES (999, 'duplicate')"

              Expect.equal duplicate (Affected 0UL) "duplicate IF NOT EXISTS is a no-op"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ _; Some "1537"; Some "Event 'decorated' already exists" ] ]) -> ()
              | other -> failtestf "expected duplicate event note, got %A" other

              match
                  handle
                      session
                      "CREATE EVENT IF NOT EXISTS decorated ON SCHEDULE EVERY 1 DAY DO this is invalid"
                  |> snd
              with
              | Err(1064, _) -> ()
              | other -> failtestf "expected duplicate declarations to validate their body, got %A" other

              match
                  handle
                      session
                      "SELECT definer,status,on_completion,event_comment,created=last_altered,last_executed IS NULL FROM information_schema.events WHERE event_name='decorated'"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "root@%"; Some "REPLICA_SIDE_DISABLED"; Some "PRESERVE"; Some "mentions DO and 'quotes'"
                      Some "1"; Some "1" ] ]
                ) -> ()
              | other -> failtestf "expected event declaration metadata, got %A" other

              match handle session "SHOW CREATE EVENT decorated" |> snd with
              | ResultSet(_, [ [ _; _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "DEFINER=`root`@`%`" "stored definer"
                  Expect.stringContains ddl "ON COMPLETION PRESERVE" "completion policy"
                  Expect.stringContains ddl "DISABLE ON REPLICA" "replica status"
                  Expect.stringContains ddl "COMMENT 'mentions DO and ''quotes'''" "escaped comment"
                  Expect.stringContains ddl "DO INSERT INTO event_log VALUES (1, 'DO stays in the body')" "original body"
                  Expect.isFalse (ddl.Contains "999") "duplicate declaration did not replace the event"
              | other -> failtestf "expected decorated event DDL, got %A" other

              let session = apply session "CREATE USER event_editor"
              let session = apply session "GRANT EVENT ON fsdb.* TO event_editor"
              let editor = { create 2 store with User = "event_editor" }

              match
                  handle
                      editor
                      "CREATE DEFINER='root'@'%' EVENT denied_definer ON SCHEDULE EVERY 1 DAY DO SELECT 1"
                  |> snd
              with
              | Err(1227, _) -> ()
              | other -> failtestf "expected explicit-definer denial, got %A" other

              let session =
                  apply
                      session
                      "ALTER DEFINER=CURRENT_USER EVENT decorated ON COMPLETION NOT PRESERVE ENABLE COMMENT 'changed'"

              match handle session "SHOW CREATE EVENT decorated" |> snd with
              | ResultSet(_, [ [ _; _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "ON COMPLETION NOT PRESERVE ENABLE COMMENT 'changed'" "altered options"
              | other -> failtestf "expected altered declaration options, got %A" other

              match handle session ("ALTER EVENT decorated COMMENT '" + String.replicate 2049 "x" + "'") |> snd with
              | Err(3507, "Failed to update events dictionary object.") -> ()
              | other -> failtestf "expected oversized event comment rejection, got %A" other

          testCase "SQL PREPARE accepts user-variable source text and text-probed statements"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @sql = 'SET @answer = ?'"
              let session, result = handle session "PREPARE `SetAnswer` FROM @sql"
              Expect.equal result (Affected 0UL) "prepared from variable"
              let session, _ = handle session "SET @value = 42"
              let session, result = handle session "EXECUTE setanswer USING @value"
              Expect.equal result (Affected 0UL) "executed text probe"
              Expect.equal session.UserVariables.["answer"] (VInt 42L) "bound typed value"

              match handle session "EXECUTE setanswer" |> snd with
              | Err(1210, _) -> ()
              | other -> failtestf "expected parameter-count error, got %A" other

              for source in [ "1"; "X'53454C4543542031'" ] do
                  match handle session ("PREPARE invalid_source FROM " + source) |> snd with
                  | Err(1064, _) -> ()
                  | other -> failtestf "expected PREPARE source rejection, got %A" other

          testCase "a failed SQL PREPARE replacement deallocates the previous statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "PREPARE s FROM 'SELECT 1'"
              let session, result = handle session "PREPARE s FROM 'broken'"

              match result with
              | Err(1064, _) -> ()
              | other -> failtestf "expected invalid replacement error, got %A" other

              match handle session "EXECUTE s" |> snd with
              | Err(1243, _) -> ()
              | other -> failtestf "expected the prior statement to be gone, got %A" other

          QueryHandlerVariableTests.tests

          testCase "SELECT DATABASE() returns NULL before USE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected a single NULL row, got %A" other

          testCase "USE sets the session database, reflected by SELECT DATABASE()"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE mydb"
              let session, _ = handle session "USE mydb"

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "tracked schema and system-variable assignments are retained for the next OK packet"
          <| fun _ ->
              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      Capabilities = ClientProtocol41 ||| ClientSessionTrack }

              let session, _ = handle session "CREATE DATABASE tracked"
              let session, _ = handle session "USE tracked"
              Expect.equal session.SessionStateChanges [ SchemaChanged "tracked" ] "schema tracker"

              let session, _ = handle session "SET autocommit = 1"
              Expect.equal
                  session.SessionStateChanges
                  [ SystemVariableChanged("autocommit", "ON") ]
                  "same-value assignments are tracked"

              let session, _ = handle session "SET session_track_state_change = ON"
              Expect.isEmpty session.SessionStateChanges "the tracker does not report its own assignment"

              let session, _ = handle session "SET @tracked = 1"
              Expect.equal session.SessionStateChanges [ StateChanged ] "generic state-change tracker"

              let session, _ = handle session "SET TRANSACTION READ ONLY"
              Expect.equal session.SessionStateChanges [ StateChanged ] "next-transaction characteristics change session state"

              let session, _ = handle session "SET character_set_results = NULL"
              Expect.equal
                  session.SessionStateChanges
                  [ SystemVariableChanged("character_set_results", ""); StateChanged ]
                  "NULL is reported as an empty tracked value"

              let session, _ = handle session "SELECT 1"
              Expect.isEmpty session.SessionStateChanges "the next statement starts with an empty tracker"

          testCase "SCHEMA() is a synonym for DATABASE(), matching MySQL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE mydb"
              let session, _ = handle session "USE mydb"

              match handle session "SELECT SCHEMA()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "USE against a database that doesn't exist is a 1049, not a silent success"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE nope_does_not_exist" |> snd with
              | Err(1049, msg) -> Expect.stringContains msg "nope_does_not_exist" "message names the missing database"
              | other -> failtestf "expected a 1049 Unknown database error, got %A" other

              // The session's database is unchanged by the failed USE.
              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected DATABASE() to still be NULL, got %A" other

          testCase "USE information_schema succeeds even though it isn't a real catalog entry"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE information_schema" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected USE information_schema to succeed, got %A" other

          testCase "CREATE DATABASE with a charset/collate tail actually creates the database"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE DATABASE crescat_testing CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the CREATE DATABASE to succeed, got %A" other

              match handle session "USE crescat_testing" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the newly-created database to be usable, got %A" other

          testCase "ALTER DATABASE succeeds on an existing database and 1049s on a missing one"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "ALTER DATABASE shop CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ALTER DATABASE to succeed, got %A" other

              match handle session "ALTER DATABASE nope_does_not_exist CHARACTER SET utf8mb4" |> snd with
              | Err(1049, _) -> ()
              | other -> failtestf "expected a 1049 error, got %A" other

              let session, _ = handle session "USE shop"

              match handle session "ALTER DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ALTER DATABASE to target the current database, got %A" other

          testCase "SHOW DATABASES returns a resultset"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet([ "Database" ], _ :: _) -> ()
              | other -> failtestf "expected a non-empty resultset, got %A" other

          testCase "SHOW VARIABLES LIKE filters by pattern"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW VARIABLES LIKE 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the autocommit row, got %A" other

          testCase "SHOW TABLES / SHOW FULL TABLES list the current database's tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"

              match handle session "SHOW TABLES" |> snd with
              | ResultSet([ "Tables_in_shop" ], [ [ Some "widgets" ] ]) -> ()
              | other -> failtestf "expected the one table, got %A" other

              match handle session "SHOW FULL TABLES" |> snd with
              | ResultSet([ "Tables_in_shop"; "Table_type" ], [ [ Some "widgets"; Some "BASE TABLE" ] ]) -> ()
              | other -> failtestf "expected the FULL variant's extra column, got %A" other

          testCase "SHOW STATUS answers session/global forms and unmatched patterns with empty sets"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "show session status like 'ssl_version'" |> snd with
              | ResultSet([ "Variable_name"; "Value" ], [ [ Some "Ssl_version"; Some "" ] ]) -> ()
              | other -> failtestf "expected the empty Ssl_version row, got %A" other

              match handle session "SHOW GLOBAL STATUS LIKE 'Uptime'" |> snd with
              | ResultSet(_, [ [ Some "Uptime"; Some _ ] ]) -> ()
              | other -> failtestf "expected an Uptime row, got %A" other

              match handle session "SHOW STATUS LIKE 'no_such_counter%'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected an empty set, got %A" other

          testCase "SHOW STATUS reports core command counters"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE command_counts (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO command_counts VALUES (1, 1)"
              let session, _ = handle session "UPDATE command_counts SET n = 2 WHERE id = 1"
              let session, _ = handle session "REPLACE INTO command_counts VALUES (1, 3)"
              let session, _ = handle session "DELETE FROM command_counts WHERE id = 1"
              let _, _ = handle session "SELECT * FROM command_counts"

              for name in [ "Com_insert"; "Com_update"; "Com_replace"; "Com_delete"; "Com_select" ] do
                  match handle session (sprintf "SHOW STATUS LIKE '%s'" name) |> snd with
                  | ResultSet(_, [ [ Some actual; Some value ] ]) when actual = name && int64 value > 0L -> ()
                  | other -> failtestf "expected a positive %s counter, got %A" name other

          testCase "SHOW STATUS reports connection compression and wire bytes"
          <| fun _ ->
              let metrics: Fsdb.Session.TransportMetrics =
                  { BytesReceived = 123L
                    BytesSent = 456L }

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      Capabilities = Fsdb.Protocol.ClientCompress
                      TransportMetrics = metrics }

              for name, expected in [ "Compression", "ON"; "Bytes_received", "123"; "Bytes_sent", "456" ] do
                  match handle session (sprintf "SHOW STATUS LIKE '%s'" name) |> snd with
                  | ResultSet(_, [ [ Some actual; Some value ] ]) ->
                      Expect.equal actual name "the status name"
                      Expect.equal value expected (name + " value")
                  | other -> failtestf "expected %s status, got %A" name other

          testCase "SHOW SESSION/GLOBAL VARIABLES match like the bare form; GLOBAL reads the store scope"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET GLOBAL wait_timeout = 123"

              match handle session "SHOW SESSION VARIABLES LIKE 'wait_timeout'" |> snd with
              | ResultSet(_, [ [ Some "wait_timeout"; Some "300" ] ]) -> ()
              | other -> failtestf "expected the session value untouched, got %A" other

              match handle session "SHOW GLOBAL VARIABLES LIKE 'wait_timeout'" |> snd with
              | ResultSet(_, [ [ Some "wait_timeout"; Some "123" ] ]) -> ()
              | other -> failtestf "expected the global override, got %A" other

          testCase "SHOW ENGINES / STORAGE ENGINES / CHARACTER SET / PRIVILEGES / GRANTS answer"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW ENGINES" |> snd with
              | ResultSet(_, [ [ Some "InnoDB"; Some "DEFAULT"; _; Some "YES"; Some "YES"; Some "YES" ] ]) -> ()
              | other -> failtestf "expected the InnoDB engine row, got %A" other

              match handle session "SHOW STORAGE ENGINES" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected the same single row, got %A" other

              match handle session "SHOW CHARACTER SET LIKE 'utf8mb4'" |> snd with
              | ResultSet([ "Charset"; "Default collation"; "Description"; "Maxlen" ], [ [ Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"; _; Some "4" ] ]) -> ()
              | other -> failtestf "expected the utf8mb4 charset row, got %A" other

              match handle session "SHOW PRIVILEGES" |> snd with
              | ResultSet([ "Privilege"; "Context"; "Comment" ], rows) -> Expect.isGreaterThan rows.Length 30 "the full static list"
              | other -> failtestf "expected the privileges list, got %A" other

              match handle session "SHOW GRANTS FOR CURRENT_USER()" |> snd with
              | ResultSet(_, [ [ Some grant ] ]) -> Expect.stringContains grant "GRANT ALL PRIVILEGES ON *.*" "the one truthful grant"
              | other -> failtestf "expected one grant row, got %A" other

          testCase "SHOW TRIGGERS/EVENTS/PROCEDURE STATUS are empty with real headers; unknown db still 1049"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "SHOW TRIGGERS FROM shop" |> snd with
              | ResultSet("Trigger" :: _, []) -> ()
              | other -> failtestf "expected empty triggers, got %A" other

              match handle session "SHOW TRIGGERS FROM nope" |> snd with
              | Err(1049, _) -> ()
              | other -> failtestf "expected 1049, got %A" other

              match handle session "SHOW EVENTS FROM shop" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty events, got %A" other

              match handle session "SHOW PROCEDURE STATUS WHERE Db='shop'" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty procedure status, got %A" other

              match handle session "SHOW FUNCTION STATUS" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected empty function status, got %A" other

          testCase "SHOW FULL TABLES WHERE Table_type filters on the pseudo-column"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"

              match handle session "SHOW FULL TABLES FROM shop WHERE Table_type IN ('BASE TABLE', 'SYSTEM VERSIONED')" |> snd with
              | ResultSet(_, [ [ Some "widgets"; Some "BASE TABLE" ] ]) -> ()
              | other -> failtestf "expected the table to pass the filter, got %A" other

              match handle session "SHOW FULL TABLES FROM shop WHERE Table_type = 'VIEW'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected the VIEW filter to exclude everything, got %A" other

          testCase "SHOW TABLES FROM information_schema lists the virtual tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW TABLES FROM information_schema" |> snd with
              | ResultSet([ "Tables_in_information_schema" ], rows) ->
                  Expect.isGreaterThan rows.Length 20 "all virtual tables listed"
              | other -> failtestf "expected the virtual-table listing, got %A" other

          testCase "information_schema is readable but cannot be materialized or dropped by an unprivileged user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let _, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let limited = { create 2 store with User = "limited" }

              match handle limited "SELECT TABLE_NAME FROM information_schema.TABLES" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected information_schema SELECT to remain available, got %A" other

              match handle limited "CREATE TABLE information_schema.evil (id INT)" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected CREATE in information_schema to be denied, got %A" other

              match handle limited "DROP DATABASE information_schema" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected DROP information_schema to be denied, got %A" other

          testCase "information_schema only reveals schemas, definitions, and grants visible to the viewer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE secret"
              let root, _ = handle root "USE secret"
              let root, _ = handle root "CREATE TABLE t (id INT)"
              let root, _ = handle root "CREATE TABLE log (id INT)"
              let root, _ = handle root "CREATE VIEW secret_view AS SELECT id FROM t"
              let root, _ = handle root "CREATE TRIGGER secret_trigger AFTER INSERT ON t FOR EACH ROW INSERT INTO log VALUES (NEW.id)"
              let root, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let root, _ = handle root "CREATE USER 'grantee' IDENTIFIED BY 'pw'"
              let root, _ = handle root "GRANT SELECT ON secret.t TO 'grantee'"
              let limited = { create 2 store with User = "limited" }

              let expectEmpty sql =
                  match handle limited sql |> snd with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected no visible rows for %s, got %A" sql other

              expectEmpty "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'secret'"
              expectEmpty "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = 'secret'"
              expectEmpty "SELECT GRANTEE FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE LIKE '%grantee%'"
              expectEmpty "SELECT GRANTEE FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE LIKE '%grantee%'"

              let _, _ = handle root "GRANT SELECT ON secret.t TO 'limited'"

              match handle limited "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'secret'" |> snd with
              | ResultSet(_, [ [ Some "t" ] ]) -> ()
              | other -> failtestf "expected the granted table to become visible, got %A" other

              match handle limited "SELECT GRANTEE FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE LIKE '%limited%'" |> snd with
              | ResultSet(_, [ [ Some grantee ] ]) -> Expect.stringContains grantee "limited" "only the viewer's grant is visible"
              | other -> failtestf "expected the viewer's table grant, got %A" other

              expectEmpty "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = 'secret'"

          testCase "parsed execution restores the information schema viewer scope"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let outer = Fsdb.Auth.account "outer" "%"
              let previous = Fsdb.InformationSchema.currentViewer.Value

              Fsdb.InformationSchema.withViewer store outer (fun () ->
                  let _, result = handle session "SELECT 1"
                  Expect.equal result (ResultSet([ "1" ], [ [ Some "1" ] ])) "query result"

                  let viewer = Fsdb.InformationSchema.currentViewer.Value |> Option.map snd
                  Expect.equal viewer (Some outer) "outer viewer restored")

              let restored = Fsdb.InformationSchema.currentViewer.Value |> Option.map snd
              Expect.equal restored (previous |> Option.map snd) "prior viewer restored after outer scope"

          testCase "DROP TRIGGER requires TRIGGER privilege on its subject table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE victim"
              let root, _ = handle root "USE victim"
              let root, _ = handle root "CREATE TABLE t (id INT)"
              let root, _ = handle root "CREATE TRIGGER audit_t AFTER INSERT ON t FOR EACH ROW INSERT INTO t VALUES (NEW.id)"
              let root, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let limited = { create 2 store with User = "limited"; Database = Some "victim" }

              match handle limited "DROP TRIGGER audit_t" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected DROP TRIGGER to be denied, got %A" other

              match handle root "DROP TRIGGER audit_t" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected the denied attempt to leave the trigger intact, got %A" other

          testCase "SHOW PROCESSLIST answers (empty registry outside a server); KILL of an unknown id is 1094"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW FULL PROCESSLIST" |> snd with
              | ResultSet([ "Id"; "User"; "Host"; "db"; "Command"; "Time"; "State"; "Info" ], _) -> ()
              | other -> failtestf "expected the processlist shape, got %A" other

              match handle session "KILL QUERY 999999" |> snd with
              | Err(1094, _) -> ()
              | other -> failtestf "expected 1094 for an unknown thread id, got %A" other

          testCase "PROCESS grants visibility while SUPER grants authority to KILL another user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, created = handle root "CREATE USER 'pviewer' IDENTIFIED BY 'pw'"

              match created with
              | Err(code, msg) -> failtestf "create user: %d %s" code msg
              | _ -> ()

              // Unique-to-this-test ids/user so a concurrently running
              // server test's registered processes can't interfere.
              Fsdb.InformationSchema.registerProcess 777001L "root" "localhost" |> ignore
              Fsdb.InformationSchema.registerProcess 777002L "pviewer" "localhost" |> ignore

              try
                  let viewer = { create 777002 store with User = "pviewer" }

                  // Without the global PROCESS privilege, PROCESSLIST is
                  // scoped to the caller's own connections.
                  match handle viewer "SHOW PROCESSLIST" |> snd with
                  | ResultSet(_, rows) ->
                      Expect.equal
                          (rows |> List.map (List.item 1))
                          [ Some "pviewer" ]
                          "pviewer sees only its own connection"
                  | other -> failtestf "expected a processlist, got %A" other

                  match handle root "SHOW PROCESSLIST" |> snd with
                  | ResultSet(_, rows) ->
                      let ids = rows |> List.map (List.item 0)
                      Expect.contains ids (Some "777001") "root sees its own row"
                      Expect.contains ids (Some "777002") "root sees pviewer's row too"
                  | other -> failtestf "expected a processlist, got %A" other

                  // A connection pviewer can't see is one it can't name:
                  // MySQL reports the id as unknown, not as denied.
                  match handle viewer "KILL 777001" |> snd with
                  | Err(1094, msg) -> Expect.equal msg "Unknown thread id: 777001" "MySQL's 1094 text"
                  | other -> failtestf "expected 1094 for another user's connection, got %A" other

                  let root, granted = handle root "GRANT PROCESS ON *.* TO 'pviewer'"

                  match granted with
                  | Err(code, msg) -> failtestf "grant PROCESS: %d %s" code msg
                  | _ -> ()

                  match handle viewer "KILL 777001" |> snd with
                  | Err(1095, msg) -> Expect.equal msg "You are not owner of thread 777001" "PROCESS grants visibility, not kill authority"
                  | other -> failtestf "expected 1095 without SUPER, got %A" other

                  // Another account's grants read `mysql.user`; without SELECT
                  // there, MySQL denies with 1142 on that table.
                  match handle viewer "SHOW GRANTS FOR 'root'" |> snd with
                  | Err(1142, msg) ->
                      Expect.equal msg "SELECT command denied to user 'pviewer'@'localhost' for table 'user'" "MySQL's 1142 text"
                  | other -> failtestf "expected 1142 for another account's grants, got %A" other

                  // Its own grants stay readable.
                  match handle viewer "SHOW GRANTS" |> snd with
                  | ResultSet(_, _) -> ()
                  | other -> failtestf "expected pviewer's own grants, got %A" other
              finally
                  Fsdb.InformationSchema.unregisterProcess 777001L
                  Fsdb.InformationSchema.unregisterProcess 777002L

          testCase "process and grant metadata stay scoped to the host-qualified account"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER 'shared'@'%'"
              let root, _ = handle root "CREATE USER 'shared'@'localhost'"
              let _root, _ = handle root "GRANT SELECT ON fsdb.* TO 'shared'@'localhost'"
              let broad = { create 778001 store with User = "shared" }
              let local = { create 778002 store with User = "shared"; AccountHost = "localhost" }

              Fsdb.InformationSchema.registerProcessAs 778001L (Fsdb.Auth.account "shared" "%") "shared" "127.0.0.1" |> ignore
              Fsdb.InformationSchema.registerProcessAs 778002L (Fsdb.Auth.account "shared" "localhost") "shared" "127.0.0.1" |> ignore

              try
                  let visibleId session =
                      match handle session "SELECT ID FROM information_schema.processlist WHERE ID IN (778001, 778002)" |> snd with
                      | ResultSet(_, [ [ Some id ] ]) -> id
                      | other -> failtestf "expected one visible process, got %A" other

                  Expect.equal (visibleId broad) "778001" "percent account sees only itself"
                  Expect.equal (visibleId local) "778002" "localhost account sees only itself"

                  let ownGrantee session =
                      match handle session "SELECT GRANTEE FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE LIKE '%shared%'" |> snd with
                      | ResultSet(_, [ [ Some grantee ] ]) -> grantee
                      | other -> failtestf "expected one host-qualified grant, got %A" other

                  Expect.equal (ownGrantee local) "'shared'@'localhost'" "local grant stays local"

                  match handle broad "SELECT GRANTEE FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE LIKE '%shared%'" |> snd with
                  | ResultSet(_, rows) -> Expect.equal rows [] "percent account cannot read localhost grants"
                  | other -> failtestf "expected an empty grant list, got %A" other
              finally
                  Fsdb.InformationSchema.unregisterProcess 778001L
                  Fsdb.InformationSchema.unregisterProcess 778002L

          testCase "SHOW TABLES WHERE filters on Tables_in_<db> and 1054s an unknown column"
          <| fun _ ->
              let session = create 999901 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"
              let session, _ = handle session "CREATE TABLE gears (id INT PRIMARY KEY)"

              match handle session "SHOW TABLES FROM shop WHERE Tables_in_shop = 'widgets'" |> snd with
              | ResultSet(_, [ [ Some "widgets" ] ]) -> ()
              | other -> failtestf "expected only the named table, got %A" other

              match handle session "SHOW TABLES FROM shop WHERE bogus_col = 'x'" |> snd with
              | Err(1054, _) -> ()
              | other -> failtestf "expected 1054 for an unknown filter column, got %A" other

          testCase "SHOW TRIGGERS/EVENTS tolerate extra whitespace between keywords"
          <| fun _ ->
              let session = create 999902 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "SHOW  TRIGGERS  FROM shop" |> snd with
              | ResultSet("Trigger" :: _, []) -> ()
              | other -> failtestf "expected empty triggers despite doubled spaces, got %A" other

              match handle session "SHOW  EVENTS FROM shop" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty events despite doubled spaces, got %A" other

          testCase "SHOW STATUS/VARIABLES accept WHERE Variable_name = '...'"
          <| fun _ ->
              let session = create 999903 (Fsdb.Storage.create ())

              match handle session "SHOW SESSION STATUS WHERE Variable_name = 'Uptime'" |> snd with
              | ResultSet(_, [ [ Some "Uptime"; Some _ ] ]) -> ()
              | other -> failtestf "expected the one Uptime row, got %A" other

              match handle session "SHOW VARIABLES WHERE Variable_name = 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the one autocommit row, got %A" other

          testCase "CURRENT_USER()/USER() fall back to the root identity off the wire"
          <| fun _ ->
              // A session built directly (embedded `Db`, tests) has no
              // handshake — `Session.create` defaults its user to root.
              let session = create 999904 (Fsdb.Storage.create ())

              match handle session "SELECT CURRENT_USER(), USER()" |> snd with
              | ResultSet(_, [ [ Some "root@%"; Some "root@localhost" ] ]) -> ()
              | other -> failtestf "expected the fallback identity, got %A" other

          testCase "advisory locks are reentrant and released with their session"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let second = create 2 store

              let scalar session sql =
                  match handle session sql |> snd with
                  | ResultSet(_, [ [ value ] ]) -> value
                  | other -> failtestf "expected one scalar value, got %A" other

              Expect.equal (scalar first "SELECT GET_LOCK('migration', 0)") (Some "1") "first owner"
              Expect.equal (scalar first "SELECT GET_LOCK('migration', 0)") (Some "1") "reentrant owner"
              Expect.equal (scalar second "SELECT IS_FREE_LOCK('migration')") (Some "0") "held lock is not free"
              Expect.equal (scalar second "SELECT IS_USED_LOCK('migration')") (Some "1") "owner connection id"
              Expect.equal (scalar second "SELECT GET_LOCK('migration', 0)") (Some "0") "contended lock"
              Expect.equal (scalar first "SELECT RELEASE_LOCK('migration')") (Some "1") "one acquisition remains"
              Expect.equal (scalar second "SELECT GET_LOCK('migration', 0)") (Some "0") "recursive count retains lock"
              Expect.equal (scalar first "SELECT RELEASE_LOCK('migration')") (Some "1") "final release"
              Expect.equal (scalar first "SELECT IS_FREE_LOCK('migration')") (Some "1") "released lock is free"
              Expect.equal (scalar second "SELECT GET_LOCK('migration', 0)") (Some "1") "next owner"

              closeSession second
              Expect.equal (scalar first "SELECT GET_LOCK('migration', 0)") (Some "1") "disconnect releases locks"

          testCase "comments ahead of text-probed statements are stripped like real MySQL's lexer"
          <| fun _ ->
              let session = create 999905 (Fsdb.Storage.create ())

              // The TablePlus dump preamble shape: a -- comment banner, blank
              // lines, then a version-gated SET reaching the probe path.
              let preamble =
                  "-- ----------------
-- TablePlus 6.1.2
--
-- Database: x
-- ----------------


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */"

              match handle session preamble |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the preamble SET to succeed, got %A" other

              match handle session "/* c */ SET @x = 1" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the block-commented SET to succeed, got %A" other

              match handle session "SELECT 3 # hash comment" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | other -> failtestf "expected the hash comment to be stripped, got %A" other

              match handle session "SET @a = 1 -- trailing" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the trailing comment to be stripped, got %A" other

              match handle session "SET-- boundary\n@commented = 2" with
              | session, Affected 0UL ->
                  match handle session "SELECT @commented" |> snd with
                  | ResultSet(_, [ [ Some "2" ] ]) -> ()
                  | other -> failtestf "expected the commented SET value, got %A" other
              | _, other -> failtestf "expected the token-boundary comment to preserve whitespace, got %A" other

              // `--` without following whitespace is arithmetic, not a comment.
              match handle session "SELECT 5--3" |> snd with
              | ResultSet(_, [ [ Some "8" ] ]) -> ()
              | other -> failtestf "expected 5--3 = 8, got %A" other

              // Comment markers inside string literals are data.
              match handle session "SELECT '-- not # a /* comment */'" |> snd with
              | ResultSet(_, [ [ Some "-- not # a /* comment */" ] ]) -> ()
              | other -> failtestf "expected the literal preserved, got %A" other

          testCase "ALTER TABLE ... DISABLE/ENABLE KEYS is a no-op OK, 1146 for a missing table"
          <| fun _ ->
              let session = create 999906 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY)"

              match handle session "/*!40000 ALTER TABLE `t` DISABLE KEYS */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the versioned DISABLE KEYS no-op, got %A" other

              match handle session "ALTER TABLE t ENABLE KEYS" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the ENABLE KEYS no-op, got %A" other

              match handle session "ALTER TABLE nope DISABLE KEYS" |> snd with
              | Err(1146, _) -> ()
              | other -> failtestf "expected 1146 for a missing table, got %A" other

          testCase "SHOW COLUMNS FROM t / DESCRIBE t report field metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL)"

              match handle session "SHOW COLUMNS FROM widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "int"; Some "NO"; Some "PRI"; None; Some "auto_increment" ]
                        [ Some "name"; Some "varchar(50)"; Some "NO"; Some ""; None; Some "" ] ]
                      "both columns with their metadata"
              | other -> failtestf "expected a resultset, got %A" other

              match handle session "DESCRIBE widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) -> Expect.equal (List.length rows) 2 "DESCRIBE is SHOW COLUMNS under another name"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE reconstructs plausible DDL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) UNIQUE)"

              match handle session "SHOW CREATE TABLE widgets" |> snd with
              | ResultSet([ "Table"; "Create Table" ], [ [ Some "widgets"; Some ddl ] ]) ->
                  Expect.stringContains ddl "CREATE TABLE `widgets`" "names the table"
                  Expect.stringContains ddl "PRIMARY KEY (`id`)" "includes the primary key"
                  Expect.stringContains ddl "UNIQUE KEY `name`" "includes the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE with a backtick-quoted, db-qualified name matches the unqualified result"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE users (id INT PRIMARY KEY)"

              let unqualified = handle session "SHOW CREATE TABLE users" |> snd
              let qualified = handle session "SHOW CREATE TABLE `shop`.`users`" |> snd
              Expect.equal qualified unqualified "backtick-quoted db.table matches the unqualified form"

              match handle session "SHOW COLUMNS FROM `shop`.`users`" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected SHOW COLUMNS to resolve the backtick-quoted db.table, got %A" other

              match handle session "SHOW INDEX FROM `shop`.`users`" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected SHOW INDEX to resolve the backtick-quoted db.table, got %A" other

          testCase "SHOW INDEX FROM t lists the primary key and other indexes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY, sku VARCHAR(20) UNIQUE)"

              match handle session "SHOW INDEX FROM widgets" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols.[0] "Table" "first column is Table"
                  Expect.equal (List.length rows) 2 "primary key + the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW INDEX and SHOW CREATE retain index prefix lengths"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, created = handle session "CREATE TABLE prefixed (body TEXT, label VARCHAR(255), exact VARCHAR(128), KEY ix_body (body(191)), KEY ix_label (label(20)), KEY ix_exact (exact(128)))"
              Expect.equal created (Affected 0UL) "table created"

              match handle session "SHOW INDEX FROM prefixed" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (fun row -> row.[2], row.[4], row.[7]))
                      [ Some "ix_body", Some "body", Some "191"
                        Some "ix_label", Some "label", Some "20"
                        Some "ix_exact", Some "exact", None ]
                      "prefix metadata"
              | other -> failtestf "expected SHOW INDEX rows, got %A" other

              match handle session "SHOW CREATE TABLE prefixed" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "KEY `ix_body` (`body`(191))" "text prefix"
                  Expect.stringContains ddl "KEY `ix_label` (`label`(20))" "varchar prefix"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

              let session, altered = handle session "ALTER TABLE prefixed ADD INDEX ix_altered (label (191))"
              Expect.equal altered (Affected 0UL) "prefix index added"

              match handle session "SHOW INDEX FROM prefixed WHERE key_name = 'ix_altered' and column_name = 'label'" |> snd with
              | ResultSet(_, [ row ]) -> Expect.equal row.[7] (Some "191") "ALTER prefix metadata"
              | other -> failtestf "expected the altered prefix index, got %A" other

          testCase "index direction and visibility round-trip through metadata and ALTER"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, created = handle session "CREATE TABLE sorted (a INT, b INT, PRIMARY KEY (a DESC, b ASC), KEY ix_sorted (a DESC, b ASC) INVISIBLE)"
              Expect.equal created (Affected 0UL) "table created"

              match handle session "SHOW INDEX FROM sorted" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.filter (fun row -> row.[2] = Some "ix_sorted") |> List.map (fun row -> row.[4], row.[5], row.[13]))
                      [ Some "a", Some "D", Some "NO"; Some "b", Some "A", Some "NO" ]
                      "SHOW INDEX attributes"
              | other -> failtestf "expected index metadata, got %A" other

              match handle session "SHOW CREATE TABLE sorted" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "PRIMARY KEY (`a` DESC,`b`)" "primary-key direction"
                  Expect.stringContains ddl "KEY `ix_sorted` (`a` DESC,`b`) /*!80000 INVISIBLE */" "SHOW CREATE attributes"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

              let session, altered = handle session "ALTER TABLE sorted ALTER INDEX ix_sorted VISIBLE"
              Expect.equal altered (Affected 0UL) "visibility changed"

              match handle session "SHOW INDEX FROM sorted" |> snd with
              | ResultSet(_, rows) ->
                  rows
                  |> List.filter (fun row -> row.[2] = Some "ix_sorted")
                  |> fun indexRows -> Expect.all indexRows (fun row -> row.[13] = Some "YES") "ALTER updates metadata"
              | other -> failtestf "expected visible index metadata, got %A" other

              match handle session "ALTER TABLE sorted ALTER INDEX `PRIMARY` INVISIBLE" |> snd with
              | Err(3522, "A primary key index cannot be invisible") -> ()
              | other -> failtestf "expected primary visibility error 3522, got %A" other

              let session, _ = handle session "CREATE TABLE no_primary (id INT)"

              match handle session "ALTER TABLE no_primary ALTER INDEX `PRIMARY` INVISIBLE" |> snd with
              | Err(1176, _) -> ()
              | other -> failtestf "expected missing primary error 1176, got %A" other

          testCase "composite primary metadata follows key declaration order"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE composite_order (first INT, second INT, PRIMARY KEY (second, first))"

              match handle session "SHOW INDEX FROM composite_order" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (fun row -> row.[2], row.[3], row.[4]))
                      [ Some "PRIMARY", Some "1", Some "second"; Some "PRIMARY", Some "2", Some "first" ]
                      "SHOW INDEX preserves the declared sequence"
              | other -> failtestf "expected SHOW INDEX rows, got %A" other

              match handle session "SHOW CREATE TABLE composite_order" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "PRIMARY KEY (`second`,`first`)" "SHOW CREATE uses the declared sequence"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

          testCase "SHOW INDEXES IN filters by Key_name"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE indexed (id INT PRIMARY KEY, code INT, INDEX ix_code(code))"

              match handle session "SHOW INDEXES IN indexed WHERE `Key_name` = 'ix_code'" |> snd with
              | ResultSet(columns, [ row ]) ->
                  Expect.equal (List.item 2 columns) "Key_name" "the key-name column"
                  Expect.equal (List.item 2 row) (Some "ix_code") "only the requested index remains"
              | other -> failtestf "expected the filtered index row, got %A" other

          testCase "an unrecognized statement is a 1064 syntax error naming the query"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "GARBAGE NOT SQL" |> snd with
              | Err(1064, msg) -> Expect.stringContains msg "GARBAGE NOT SQL" "message names the query"
              | other -> failtestf "expected a 1064 error, got %A" other

          testCase "a query whose string data merely starts with SET is not hijacked by the SET-statement probe"
          <| fun _ ->
              // handle's `upper.StartsWith "SET "` check is anchored to the
              // whole trimmed query text, so this can't actually misfire.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE notes (body VARCHAR(50))"

              match handle session "INSERT INTO notes VALUES ('SET x = 1')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected a normal INSERT, got %A" other

              match handle session "SELECT body FROM notes" |> snd with
              | ResultSet(_, [ [ Some "SET x = 1" ] ]) -> ()
              | other -> failtestf "expected the literal string preserved, got %A" other

          testCase "a query containing @@ inside a string literal is not hijacked by the @@-variable probe"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE users (email VARCHAR(50))"
              let session, _ = handle session "INSERT INTO users VALUES ('a@@b.com')"

              match handle session "SELECT * FROM users WHERE email LIKE '%@@%'" |> snd with
              | ResultSet(_, [ [ Some "a@@b.com" ] ]) -> ()
              | other -> failtestf "expected the row to be found via the real parser, got %A" other

          testCase "an exception inside the engine (decimal overflow) is an Err, not an escaping exception"
          <| fun _ ->
              // Storage.coerceValue's `decimal d` throws OverflowException for
              // a DECIMAL column given a value outside decimal's range — this
              // must never escape `handle` and drop the connection.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE overflow_t (d DECIMAL(10,2))"

              match handle session "INSERT INTO overflow_t VALUES (1e300)" |> snd with
              | Err(1105, "Internal error") -> ()
              | other -> failtestf "expected a 1105 internal-error Err, got %A" other

          testCase "FOUND_ROWS() and ROW_COUNT() report the previous statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE counts (id INT, n INT)"
              let session, _ = handle session "INSERT INTO counts VALUES (1, 0), (2, 0), (3, 0)"
              let session, _ = handle session "SELECT id FROM counts ORDER BY id LIMIT 2"

              match handle session "SELECT FOUND_ROWS(), ROW_COUNT()" with
              | session, ResultSet(_, [ [ Some "2"; Some "-1" ] ]) ->
                  let session, _ = handle session "UPDATE counts SET n = 1"

                  match handle session "SELECT ROW_COUNT(), FOUND_ROWS()" |> snd with
                  | ResultSet(_, [ [ Some "3"; Some "0" ] ]) -> ()
                  | other -> failtestf "expected affected-row accounting, got %A" other
              | _, other -> failtestf "expected result-row accounting, got %A" other

          testCase "LAST_INSERT_ID() stays 0 for an explicit AUTO_INCREMENT id, unlike the OK packet's last_insert_id"
          <| fun _ ->
              // Real MySQL 8.4: PDO::lastInsertId()/the OK packet reports an
              // explicitly-supplied AUTO_INCREMENT id back to the caller, but
              // the separate LAST_INSERT_ID() SQL function only ever reflects
              // a *generated* id and stays 0 until one actually is.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO t (id, n) VALUES (5, 1)"
              Expect.equal session.LastInsertId 5L "OK packet reports the explicit id"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to stay 0, got %A" other

              // A later statement that *does* generate an id moves
              // LAST_INSERT_ID() to it, and a further all-explicit multi-row
              // insert after that leaves LAST_INSERT_ID() unchanged (matching
              // MySQL) while still reporting its own id on the OK packet.
              let session, _ = handle session "INSERT INTO t (n) VALUES (2)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "6" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to report the generated id 6, got %A" other

              let session, _ = handle session "INSERT INTO t (id, n) VALUES (20, 1), (21, 2)"
              Expect.equal session.LastInsertId 21L "OK packet reports the last row's explicit id"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "6" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to still be 6, unchanged by the all-explicit insert, got %A" other

          testCase "LAST_INSERT_ID() after an INSERT ... SELECT upsert: set by the insert path, untouched by an update-only run"
          <| fun _ ->
              // MySQL 8.4.11 write probe (disposable server, 2026-08-19):
              // after an INSERT...SELECT ODKU that inserted rows,
              // LAST_INSERT_ID() = the first generated id; a later run that
              // only updates leaves it unchanged.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE src (k INT, v INT)"
              let session, _ = handle session "INSERT INTO src VALUES (1, 10), (2, 20)"
              let session, _ = handle session "CREATE TABLE dst (id INT AUTO_INCREMENT PRIMARY KEY, k INT UNIQUE, v INT)"

              let session, _ =
                  handle session "INSERT INTO dst (k, v) SELECT k, v FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() = 1 from the insert path, got %A" other

              let session, _ =
                  handle session "INSERT INTO dst (k, v) SELECT k, v + 100 FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() unchanged by the update-only run, got %A" other

          testCase "REPLACE reports deleted plus inserted rows in both client affected-row modes"
          <| fun _ ->
              let replace capabilities =
                  let session = { create 1 (Fsdb.Storage.create ()) with Capabilities = capabilities }
                  let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, n INT)"
                  let session, _ = handle session "INSERT INTO t VALUES (1, 10)"
                  handle session "REPLACE INTO t VALUES (1, 10)" |> snd

              Expect.equal (replace 0u) (Affected 1UL) "changed-row mode"
              Expect.equal (replace ClientFoundRows) (Affected 1UL) "found-row mode"

          QueryHandlerDiagnosticsTests.tests

          testCase "SHOW CREATE DATABASE, OPEN TABLES, PLUGINS, and ENGINE INNODB STATUS are truthful"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE app"
              let session, _ = handle session "USE app"
              let session, _ = handle session "CREATE TABLE visible (id INT)"

              match handle session "SHOW CREATE DATABASE app" |> snd with
              | ResultSet([ "Database"; "Create Database" ], [ [ Some "app"; Some ddl ] ]) ->
                  Expect.stringContains ddl "CREATE DATABASE `app`" "database DDL"
              | other -> failtestf "unexpected SHOW CREATE DATABASE result: %A" other

              match handle session "SHOW OPEN TABLES FROM app LIKE 'vis%'" |> snd with
              | ResultSet([ "Database"; "Table"; "In_use"; "Name_locked" ], [ [ Some "app"; Some "visible"; Some "0"; Some "0" ] ]) -> ()
              | other -> failtestf "unexpected SHOW OPEN TABLES result: %A" other

              match handle session "SHOW PLUGINS" |> snd with
              | ResultSet([ "Name"; "Status"; "Type"; "Library"; "License" ], [ [ Some "mysql_native_password"; Some "ACTIVE"; Some "AUTHENTICATION"; None; Some "GPL" ] ]) -> ()
              | other -> failtestf "unexpected SHOW PLUGINS result: %A" other

              for sql in [ "SHOW BINARY LOGS"; "SHOW BINARY LOG STATUS" ] do
                  match handle session sql |> snd with
                  | Err(1381, "You are not using binary logging") -> ()
                  | other -> failtestf "expected binary logging disabled for %s, got %A" sql other

              for sql, operation in [ "ANALYZE TABLE visible", "analyze"; "CHECK TABLE visible", "check" ] do
                  match handle session sql |> snd with
                  | ResultSet([ "Table"; "Op"; "Msg_type"; "Msg_text" ], [ [ Some "app.visible"; Some actual; Some "status"; Some "OK" ] ]) ->
                      Expect.equal actual operation "operation"
                  | other -> failtestf "unexpected maintenance result for %s: %A" sql other

              match handle session "OPTIMIZE TABLE visible" |> snd with
              | ResultSet(
                  [ "Table"; "Op"; "Msg_type"; "Msg_text" ],
                  [ [ Some "app.visible"; Some "optimize"; Some "note"; Some "Table does not support optimize, doing recreate + analyze instead" ]
                    [ Some "app.visible"; Some "optimize"; Some "status"; Some "OK" ] ]
                ) -> ()
              | other -> failtestf "unexpected OPTIMIZE result: %A" other

              match handle session "REPAIR TABLE visible" |> snd with
              | ResultSet(
                  [ "Table"; "Op"; "Msg_type"; "Msg_text" ],
                  [ [ Some "app.visible"; Some "repair"; Some "note"; Some "The storage engine for the table doesn't support repair" ] ]
                ) -> ()
              | other -> failtestf "unexpected REPAIR result: %A" other

              for sql in [ "FLUSH TABLES"; "FLUSH STATUS"; "FLUSH LOGS" ] do
                  match handle session sql |> snd with
                  | Affected 0UL -> ()
                  | other -> failtestf "unexpected %s result: %A" sql other

              match handle session "SHOW ENGINE INNODB STATUS" |> snd with
              | ResultSet([ "Type"; "Name"; "Status" ], [ [ Some "InnoDB"; Some ""; Some status ] ]) ->
                  Expect.stringContains status "in-memory transactional row store" "engine status describes fsdb"
              | other -> failtestf "unexpected SHOW ENGINE result: %A" other

          testCase "SHOW REPLICA STATUS returns MySQL's empty 60-column shape"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql in [ "SHOW REPLICA STATUS"; "SHOW REPLICA STATUS FOR CHANNEL 'analytics'" ] do
                  match handle session sql |> snd with
                  | ResultSet(columns, []) ->
                      Expect.equal columns.Length 60 "column count"
                      Expect.equal columns.Head "Replica_IO_State" "first column"
                      Expect.equal columns.[55] "Channel_Name" "channel column"
                      Expect.equal columns.[59] "Network_Namespace" "last column"
                  | other -> failtestf "unexpected replica status for %s: %A" sql other

          testCase "SHOW CREATE reports missing routines and events with MySQL errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql, expected in
                  [ "SHOW CREATE PROCEDURE fsdb.missing", Err(1305, "PROCEDURE missing does not exist")
                    "SHOW CREATE FUNCTION `fsdb`.`missing`", Err(1305, "FUNCTION missing does not exist")
                    "SHOW CREATE EVENT missing", Err(1539, "Unknown event 'missing'") ] do
                  Expect.equal (handle session sql |> snd) expected sql

          testCase "SQL_CALC_FOUND_ROWS counts rows before LIMIT and OFFSET"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE found_rows_t (n INT)"
              let session, _ = handle session "INSERT INTO found_rows_t VALUES (1), (2), (3), (4), (5)"

              let session, result =
                  handle session "SELECT SQL_CALC_FOUND_ROWS n FROM found_rows_t WHERE n > 1 ORDER BY n LIMIT 2 OFFSET 1"

              Expect.equal result (ResultSet([ "n" ], [ [ Some "3" ]; [ Some "4" ] ])) "limited rows"

              match handle session "SELECT FOUND_ROWS()" |> snd with
              | ResultSet(_, [ [ Some "4" ] ]) -> ()
              | other -> failtestf "expected four rows before LIMIT, got %A" other

              let session, result =
                  handle session "SELECT SQL_CALC_FOUND_ROWS 1 UNION ALL SELECT 2 UNION ALL SELECT 3 LIMIT 1"

              Expect.equal result (ResultSet([ "1" ], [ [ Some "1" ] ])) "limited union rows"

              match handle session "SELECT FOUND_ROWS()" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | other -> failtestf "expected three union rows before LIMIT, got %A" other

          testCase "SET GLOBAL never changes the issuing session's own variable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, ok = handle session "SET GLOBAL max_heap_table_size = 500"
              Expect.equal ok (Affected 0UL) "SET GLOBAL acks"

              // The session keeps the value it inherited at connection
              // time; a GLOBAL write only changes the default for later
              // sessions.
              match handle session "SELECT @@SESSION.max_heap_table_size" |> snd with
              | ResultSet(_, [ [ Some "16777216" ] ]) -> ()
              | other -> failtestf "SET GLOBAL must not change this session's own value, got %A" other

          testCase "SET rejects a syntactically valid unknown system variable"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET SESSION definitely_unknown = 1" |> snd with
              | Err(1193, message) -> Expect.stringContains message "definitely_unknown" "the unknown name is reported"
              | other -> failtestf "expected 1193, got %A" other

          testCase "SET GLOBAL x = y is visible to SELECT @@GLOBAL.x on the same connection"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET GLOBAL max_heap_table_size = 500"

              match handle session "SELECT @@GLOBAL.max_heap_table_size" |> snd with
              | ResultSet(_, [ [ Some "500" ] ]) -> ()
              | other -> failtestf "expected @@GLOBAL.max_heap_table_size = 500, got %A" other

          testCase "event_scheduler is a validated global switch"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "SELECT @@GLOBAL.event_scheduler" |> snd with
              | ResultSet(_, [ [ Some "ON" ] ]) -> ()
              | other -> failtestf "expected enabled scheduler default, got %A" other

              match handle session "SET SESSION event_scheduler=OFF" |> snd with
              | Err(1229, "Variable 'event_scheduler' is a GLOBAL variable and should be set with SET GLOBAL") -> ()
              | other -> failtestf "expected global-only error, got %A" other

              let session, _ = handle session "CREATE USER event_operator"
              let operator = { create 2 store with User = "event_operator" }

              match handle operator "SET GLOBAL event_scheduler=OFF" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected scheduler privilege error, got %A" other

              match handle session "SET GLOBAL event_scheduler=0" with
              | session, Affected 0UL ->
                  match handle session "SELECT @@GLOBAL.event_scheduler" |> snd with
                  | ResultSet(_, [ [ Some "OFF" ] ]) -> ()
                  | other -> failtestf "expected disabled scheduler value, got %A" other
              | _, other -> failtestf "expected scheduler assignment, got %A" other

              match handle session "SET GLOBAL event_scheduler=DISABLED" |> snd with
              | Err(1231, "Variable 'event_scheduler' can't be set to the value of 'DISABLED'") -> ()
              | other -> failtestf "expected scheduler value validation, got %A" other

          testCase "a new session inherits a SET GLOBAL made before it connected"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setter = create 1 store
              let setter, _ = handle setter "SET GLOBAL max_heap_table_size = 500"
              ignore setter

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_heap_table_size" |> Option.flatten)
                  (Some "500")
                  "a session created after the GLOBAL write inherits it as its own session default"

          testCase "SET @@GLOBAL.x = y is equivalent to SET GLOBAL x = y"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "SET @@GLOBAL.max_heap_table_size = 777"
              ignore session

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_heap_table_size" |> Option.flatten)
                  (Some "777")
                  "the @@GLOBAL. spelling reaches the same global map as SET GLOBAL"

          testCase "SET TRANSACTION ISOLATION LEVEL applies REPEATABLE READ to the next transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ" with
              | session, Affected 0UL ->
                  Expect.equal
                      session.PendingTransactionIsolation
                      (Some RepeatableRead)
                      "the next transaction retains the requested isolation"

                  let session, started = handle session "BEGIN"
                  Expect.equal started (Affected 0UL) "BEGIN succeeds"

                  match session.Tx with
                  | Some transaction -> Expect.equal transaction.Isolation RepeatableRead "the transaction captures the pending isolation"
                  | None -> failtest "BEGIN did not create a transaction"

                  Expect.isNone session.PendingTransactionIsolation "BEGIN consumes the next-transaction setting"

                  match handle session "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ" |> snd with
                  | Err(1568, _) -> ()
                  | other -> failtestf "expected 1568 while a transaction is open, got %A" other
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SET SESSION TRANSACTION ISOLATION LEVEL updates the session setting"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET TRANSACTION ISOLATION LEVEL READ COMMITTED"

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ" with
              | session, Affected 0UL ->
                  Expect.equal
                      (session.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "REPEATABLE-READ")
                      "the session setting uses MySQL's hyphenated spelling"

                  Expect.isNone session.PendingTransactionIsolation "a session setting supersedes an earlier next-transaction setting"

                  match handle session "BEGIN" |> fst with
                  | { Tx = Some transaction } ->
                      Expect.equal transaction.Isolation RepeatableRead "the session setting selects the transaction isolation"
                  | _ -> failtest "BEGIN did not create a transaction"
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SET @@transaction_isolation applies REPEATABLE READ to the next transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET @@transaction_isolation = 'REPEATABLE-READ'" with
              | session, Affected 0UL ->
                  Expect.equal session.PendingTransactionIsolation (Some RepeatableRead) "the @@ spelling is next-transaction scoped"
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SET TRANSACTION ISOLATION LEVEL applies READ COMMITTED to the next transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET TRANSACTION ISOLATION LEVEL READ COMMITTED" with
              | session, Affected 0UL ->
                  Expect.equal session.PendingTransactionIsolation (Some ReadCommitted) "the next transaction retains READ COMMITTED"
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SET GLOBAL TRANSACTION ISOLATION LEVEL updates future session defaults"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "SET GLOBAL TRANSACTION ISOLATION LEVEL READ COMMITTED" with
              | _, Affected 0UL ->
                  let newcomer = create 2 store

                  Expect.equal
                      (newcomer.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "READ-COMMITTED")
                      "a new session inherits the global isolation default"
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SERIALIZABLE and READ UNCOMMITTED are accepted"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET @@transaction_isolation = 'SERIALIZABLE'" with
              | configured, Affected 0UL ->
                  match handle configured "BEGIN" |> fst with
                  | { Tx = Some transaction } ->
                      Expect.equal transaction.Isolation Serializable "the next transaction is serializable"
                  | _ -> failtest "BEGIN did not create a transaction"
              | _, other -> failtestf "expected SERIALIZABLE to be accepted, got %A" other

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED" with
              | configured, Affected 0UL ->
                  match handle configured "BEGIN" |> fst with
                  | { Tx = Some transaction } ->
                      Expect.equal transaction.Isolation ReadUncommitted "the transaction captures READ UNCOMMITTED"
                  | _ -> failtest "BEGIN did not create a transaction"
              | _, other -> failtestf "expected READ UNCOMMITTED to be accepted, got %A" other

          testCase "READ UNCOMMITTED refreshes dirty rows and forgets rolled-back changes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE dirty_rows (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO dirty_rows VALUES (1, 10),(3, 30)"

              let writer = create 2 store
              let writer, _ = handle writer "BEGIN"
              let writer, _ = handle writer "UPDATE dirty_rows SET value = 99 WHERE id = 1"
              let writer, _ = handle writer "INSERT INTO dirty_rows VALUES (2, 20)"
              let writer, _ = handle writer "DELETE FROM dirty_rows WHERE id = 3"

              let repeatable = create 3 store
              let repeatable, _ = handle repeatable "BEGIN"

              match handle repeatable "SELECT id,value FROM dirty_rows ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "10" ]; [ Some "3"; Some "30" ] ]) -> ()
              | other -> failtestf "expected stronger isolation to hide dirty rows, got %A" other

              let reader = create 4 store
              let reader, _ = handle reader "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
              let reader, _ = handle reader "BEGIN"
              let reader, firstRead = handle reader "SELECT id,value FROM dirty_rows ORDER BY id"

              match firstRead with
              | ResultSet(_, [ [ Some "1"; Some "99" ]; [ Some "2"; Some "20" ] ]) -> ()
              | other -> failtestf "expected the writer's uncommitted update and insert, got %A" other

              let _, _ = handle writer "ROLLBACK"

              match handle reader "SELECT id,value FROM dirty_rows ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "10" ]; [ Some "3"; Some "30" ] ]) -> ()
              | other -> failtestf "expected rolled-back dirty rows to disappear on the next statement, got %A" other

          testCase "READ UNCOMMITTED composes disjoint active transactions"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE dirty_many (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO dirty_many VALUES (1, 10),(2, 20)"

              let first, _ = handle (create 2 store) "BEGIN"
              let first, _ = handle first "UPDATE dirty_many SET value = 11 WHERE id = 1"
              let second, _ = handle (create 3 store) "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
              let second, _ = handle second "BEGIN"
              let second, _ = handle second "UPDATE dirty_many SET value = 22 WHERE id = 2"
              let reader, _ = handle (create 4 store) "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
              let reader, _ = handle reader "BEGIN"

              let read session = handle session "SELECT value FROM dirty_many ORDER BY id"

              let reader, both = read reader

              match both with
              | ResultSet(_, [ [ Some "11" ]; [ Some "22" ] ]) -> ()
              | other -> failtestf "expected both active deltas, got %A" other

              let _, _ = handle first "ROLLBACK"
              let reader, one = read reader

              match one with
              | ResultSet(_, [ [ Some "10" ]; [ Some "22" ] ]) -> ()
              | other -> failtestf "expected only the surviving dirty delta, got %A" other

              let _, _ = handle second "COMMIT"

              match read reader |> snd with
              | ResultSet(_, [ [ Some "10" ]; [ Some "22" ] ]) -> ()
              | other -> failtestf "expected the committed second delta, got %A" other

          testCase "locking reads honor NOWAIT and SKIP LOCKED"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE lock_queue (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO lock_queue VALUES (1, 10),(2, 20),(3, 30)"

              let holder, _ = handle (create 2 store) "BEGIN"
              let holder, _ = handle holder "SELECT id FROM lock_queue WHERE id = 2 FOR UPDATE"
              let contender, _ = handle (create 3 store) "BEGIN"

              match handle contender "SELECT id FROM lock_queue WHERE id = 1 FOR UPDATE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the unselected row to remain available, got %A" other

              match handle contender "SELECT id FROM lock_queue WHERE id = 2 FOR UPDATE NOWAIT" |> snd with
              | Err(3572, _) -> ()
              | other -> failtestf "expected NOWAIT to reject the held row, got %A" other

              match handle contender "SELECT id FROM lock_queue FOR UPDATE SKIP LOCKED" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "3" ] ]) -> ()
              | other -> failtestf "expected SKIP LOCKED to omit the held row, got %A" other

              let _, _ = handle holder "ROLLBACK"

              match handle contender "SELECT id FROM lock_queue WHERE id = 2 FOR UPDATE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected the transaction to remain usable after NOWAIT, got %A" other

              ()

          testCase "locking reads use the current committed row version"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE current_locks (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO current_locks VALUES (1, 10)"

              let reader, _ = handle (create 2 store) "BEGIN"
              let reader, first = handle reader "SELECT value FROM current_locks WHERE id = 1"
              Expect.equal first (ResultSet([ "value" ], [ [ Some "10" ] ])) "the consistent read establishes the snapshot"
              let _, _ = handle (create 3 store) "UPDATE current_locks SET value = 77 WHERE id = 1"
              let reader, locked = handle reader "SELECT value FROM current_locks WHERE id = 1 FOR UPDATE"
              Expect.equal locked (ResultSet([ "value" ], [ [ Some "77" ] ])) "the locking read uses the current version"

              match handle reader "SELECT value FROM current_locks WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected the consistent snapshot to remain unchanged, got %A" other

          testCase "shared locking reads coexist and block writers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE shared_locks (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO shared_locks VALUES (1, 10)"

              let first, _ = handle (create 2 store) "BEGIN"
              let first, _ = handle first "SELECT id FROM shared_locks FOR SHARE"
              let second, _ = handle (create 3 store) "BEGIN"

              match handle second "SELECT id FROM shared_locks FOR SHARE NOWAIT" with
              | second, ResultSet(_, [ [ Some "1" ] ]) ->
                  let writer, _ = handle (create 4 store) "BEGIN"
                  let waiting = System.Threading.Tasks.Task.Run(fun () -> handle writer "UPDATE shared_locks SET value = 11 WHERE id = 1")

                  Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the shared locks hold the writer"
                  let _, _ = handle first "ROLLBACK"
                  let _, _ = handle second "ROLLBACK"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the writer continues after both readers release"

                  match waiting.Result |> snd with
                  | Affected 1UL -> ()
                  | other -> failtestf "expected the waiting update to succeed, got %A" other
              | _, other -> failtestf "expected compatible shared locks, got %A" other

          testCase "locking read OF targets only the named join source"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE lock_parent (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE lock_child (id INT PRIMARY KEY, parent_id INT, KEY(parent_id))"
              let setup, _ = handle setup "INSERT INTO lock_parent VALUES (1)"
              let _, _ = handle setup "INSERT INTO lock_child VALUES (1, 1)"

              let holder, _ = handle (create 2 store) "BEGIN"
              let holder, result =
                  handle
                      holder
                      "SELECT p.id,c.id FROM lock_parent p JOIN lock_child c ON c.parent_id=p.id FOR UPDATE OF p"

              match result with
              | ResultSet(_, [ [ Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the locking join to succeed, got %A" other

              let contender, _ = handle (create 3 store) "BEGIN"

              match handle contender "SELECT id FROM lock_child FOR UPDATE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the untargeted child row to remain available, got %A" other

              match handle contender "SELECT id FROM lock_parent FOR UPDATE NOWAIT" |> snd with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the targeted parent row to be locked, got %A" other

              let _, _ = handle holder "ROLLBACK"
              ()

          testCase "multiple locking clauses preserve source-specific strengths"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE clause_parent (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE clause_child (id INT PRIMARY KEY, parent_id INT)"
              let setup, _ = handle setup "INSERT INTO clause_parent VALUES (1)"
              let _, _ = handle setup "INSERT INTO clause_child VALUES (1, 1)"

              let holder, _ = handle (create 2 store) "BEGIN"
              let holder, result =
                  handle
                      holder
                      "SELECT p.id,c.id FROM clause_parent p JOIN clause_child c ON c.parent_id=p.id FOR UPDATE OF p FOR SHARE OF c"

              match result with
              | ResultSet(_, [ [ Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the multi-clause read to succeed, got %A" other

              let contender, _ = handle (create 3 store) "BEGIN"

              match handle contender "SELECT id FROM clause_child FOR SHARE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the child shared lock to be compatible, got %A" other

              match handle contender "SELECT id FROM clause_parent FOR UPDATE NOWAIT" |> snd with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the parent update lock to conflict, got %A" other

              match handle contender "SELECT id FROM clause_child FOR UPDATE NOWAIT" |> snd with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the child shared lock to reject an update lock, got %A" other

              let _, _ = handle holder "ROLLBACK"
              ()

          testCase "failed multi-source NOWAIT releases statement locks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE lock_first (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE lock_second (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO lock_first VALUES (1)"
              let _, _ = handle setup "INSERT INTO lock_second VALUES (1)"

              let blocker, _ = handle (create 2 store) "BEGIN"
              let blocker, _ = handle blocker "SELECT id FROM lock_second FOR UPDATE"
              let contender, _ = handle (create 3 store) "BEGIN"

              match
                  handle
                      contender
                      "SELECT a.id,b.id FROM lock_first a JOIN lock_second b ON b.id=a.id FOR UPDATE OF a,b NOWAIT"
                  |> snd
              with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the second source to reject NOWAIT, got %A" other

              let observer, _ = handle (create 4 store) "BEGIN"

              match handle observer "SELECT id FROM lock_first FOR UPDATE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the first source lock to roll back with the statement, got %A" other

              let _, _ = handle blocker "ROLLBACK"
              ()

          testCase "failed NOWAIT restores earlier shared lock modes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE shared_before_failure (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE blocked_after_upgrade (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO shared_before_failure VALUES (1)"
              let _, _ = handle setup "INSERT INTO blocked_after_upgrade VALUES (1)"

              let blocker, _ = handle (create 2 store) "BEGIN"
              let blocker, _ = handle blocker "SELECT id FROM blocked_after_upgrade FOR UPDATE"
              let upgrader, _ = handle (create 3 store) "BEGIN"
              let upgrader, _ = handle upgrader "SELECT id FROM shared_before_failure FOR SHARE"

              match
                  handle
                      upgrader
                      "SELECT a.id,b.id FROM shared_before_failure a JOIN blocked_after_upgrade b ON b.id=a.id FOR UPDATE OF a,b NOWAIT"
                  |> snd
              with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the later source to reject NOWAIT, got %A" other

              let observer, _ = handle (create 4 store) "BEGIN"

              match handle observer "SELECT id FROM shared_before_failure FOR SHARE NOWAIT" with
              | observer, ResultSet(_, [ [ Some "1" ] ]) ->
                  match handle observer "SELECT id FROM shared_before_failure FOR UPDATE NOWAIT" |> snd with
                  | Err(3572, _) -> ()
                  | other -> failtestf "expected the original shared lock to remain, got %A" other
              | _, other -> failtestf "expected the failed upgrade to return to shared mode, got %A" other

              let _, _ = handle blocker "ROLLBACK"
              let _, _ = handle upgrader "ROLLBACK"
              ()

          testCase "locking clauses validate aliases and do not persist in autocommit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE lock_validation (id INT PRIMARY KEY, value INT)"
              let _, _ = handle setup "INSERT INTO lock_validation VALUES (1, 10)"
              let session, _ = handle (create 2 store) "BEGIN"

              match handle session "SELECT id FROM lock_validation v FOR UPDATE OF missing" |> snd with
              | Err(3568, _) -> ()
              | other -> failtestf "expected an unresolved locking alias error, got %A" other

              match handle session "SELECT id FROM lock_validation v FOR UPDATE OF v,v" |> snd with
              | Err(3569, _) -> ()
              | other -> failtestf "expected a duplicate locking alias error, got %A" other

              match handle (create 3 store) "SELECT id FROM lock_validation FOR UPDATE" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the autocommit locking read to succeed, got %A" other

              match handle (create 4 store) "UPDATE lock_validation SET value = 11 WHERE id = 1" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected autocommit to release the read lock, got %A" other

          testCase "read-only transactions permit shared but not update locking reads"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE readonly_locks (id INT PRIMARY KEY)"
              let _, _ = handle setup "INSERT INTO readonly_locks VALUES (1)"
              let session, _ = handle (create 2 store) "START TRANSACTION READ ONLY"

              match handle session "SELECT id FROM readonly_locks FOR SHARE" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a shared read lock in the read-only transaction, got %A" other

              match handle session "SELECT id FROM readonly_locks FOR UPDATE" |> snd with
              | Err(1792, _) -> ()
              | other -> failtestf "expected an update locking read to be rejected, got %A" other

          testCase "nested locking clauses remain query-block scoped"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE outer_locks (id INT PRIMARY KEY)"
              let setup, _ = handle setup "CREATE TABLE inner_locks (id INT PRIMARY KEY)"
              let setup, _ = handle setup "INSERT INTO outer_locks VALUES (1)"
              let _, _ = handle setup "INSERT INTO inner_locks VALUES (1)"

              let holder, _ = handle (create 2 store) "BEGIN"

              let holder, result =
                  handle
                      holder
                      "SELECT id,(SELECT id FROM inner_locks WHERE id=1) FROM outer_locks o FOR UPDATE OF o"

              match result with
              | ResultSet(_, [ [ Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the outer locking read to succeed, got %A" other

              let contender, _ = handle (create 3 store) "BEGIN"

              match handle contender "SELECT id FROM inner_locks FOR UPDATE NOWAIT" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the nested nonlocking source to remain available, got %A" other

              let _, _ = handle contender "ROLLBACK"

              let innerHolder, _ = handle (create 4 store) "BEGIN"

              let innerHolder, result =
                  handle
                      innerHolder
                      "SELECT id FROM outer_locks WHERE EXISTS(SELECT id FROM inner_locks WHERE id=1 FOR UPDATE)"

              match result with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the nested locking read to succeed, got %A" other

              let observer, _ = handle (create 5 store) "BEGIN"

              match handle observer "SELECT id FROM inner_locks FOR UPDATE NOWAIT" |> snd with
              | Err(3572, _) -> ()
              | other -> failtestf "expected the nested locking clause to hold the inner row, got %A" other

              let _, _ = handle holder "ROLLBACK"
              let _, _ = handle innerHolder "ROLLBACK"
              ()

          testCase "ordinary transaction commits do not wait on the store-wide coordination lock"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_rows (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO tx_rows VALUES (1, 0)"
              let session, _ = handle session "SET innodb_lock_wait_timeout = 1"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "UPDATE tx_rows SET n = 1 WHERE id = 1"
              use entered = new Threading.ManualResetEventSlim(false)
              use release = new Threading.ManualResetEventSlim(false)

              let holder =
                  System.Threading.Tasks.Task.Factory.StartNew(
                      (fun () ->
                          lock store.Lock (fun () ->
                              entered.Set()
                              release.Wait())),
                      System.Threading.Tasks.TaskCreationOptions.LongRunning
                  )

              Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 2.0)) "the coordination lock is held"
              let commit = System.Threading.Tasks.Task.Run(fun () -> handle session "COMMIT")

              try
                  Expect.isTrue (commit.Wait(TimeSpan.FromSeconds 3.0)) "an ordinary row-version commit bypasses unrelated global coordination"
              finally
                  release.Set()
                  holder.Wait()

              match commit.Result |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected COMMIT to succeed, got %A" other

          testCase "disjoint transactions in one database merge without lost writes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_rows (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_rows VALUES (1, 0), (2, 0)"

              let prepare id value connectionId =
                  let session = create connectionId store
                  let session, _ = handle session "BEGIN"
                  handle session (sprintf "UPDATE tx_rows SET n = %d WHERE id = %d" value id) |> fst

              let first = prepare 1 10 2
              let second = prepare 2 20 3
              use ready = new Threading.CountdownEvent(2)
              use start = new Threading.ManualResetEventSlim(false)

              let commit session =
                  System.Threading.Tasks.Task.Run(fun () ->
                      ready.Signal() |> ignore
                      start.Wait()
                      handle session "COMMIT" |> snd)

              let commits = [| commit first; commit second |]
              Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 2.0)) "both transactions are ready to publish"
              start.Set()
              commits |> Array.map (fun task -> task :> System.Threading.Tasks.Task) |> System.Threading.Tasks.Task.WaitAll
              Expect.equal (commits |> Array.map _.Result |> Array.toList) [ Affected 0UL; Affected 0UL ] "both commits succeed"

              match handle (create 4 store) "SELECT id, n FROM tx_rows ORDER BY id" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ] ] "both row versions are retained"
              | other -> failtestf "expected committed rows, got %A" other

          testCase "transactions merge updates and appends without rebuilding table indexes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE accounts (id INT PRIMARY KEY, balance INT)"
              let setup, _ = handle setup "CREATE TABLE ledger (operation_id VARCHAR(32) PRIMARY KEY, account_id INT, amount INT)"
              let _, _ = handle setup "INSERT INTO accounts VALUES (1, 0), (2, 0)"

              let prepare accountId operationId connectionId =
                  let session, _ = handle (create connectionId store) "BEGIN"
                  let session, _ = handle session (sprintf "UPDATE accounts SET balance = balance + 1 WHERE id = %d" accountId)
                  handle session (sprintf "INSERT INTO ledger VALUES ('%s', %d, 1)" operationId accountId) |> fst

              let first = prepare 1 "first" 2
              let second = prepare 2 "second" 3
              let before = Fsdb.Storage.reindexCallCount ()

              match handle first "COMMIT" |> snd, handle second "COMMIT" |> snd with
              | Affected 0UL, Affected 0UL -> ()
              | results -> failtestf "expected both commits to succeed, got %A" results

              Expect.equal (Fsdb.Storage.reindexCallCount ()) before "commit maintains derived indexes incrementally"

              match handle (create 4 store) "SELECT operation_id, account_id FROM ledger ORDER BY operation_id" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "first"; Some "1" ]; [ Some "second"; Some "2" ] ] "both appends survive row-id rebasing"
              | other -> failtestf "expected committed ledger rows, got %A" other

          testCase "same-row transactions wait and update the committed value"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_rows (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_rows VALUES (1, 0)"

              let first, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_rows SET n = n + 1 WHERE id = 1"
              Expect.equal firstUpdate (Affected 1UL) "the first transaction owns the row"

              let second, _ = handle (create 3 store) "BEGIN"
              let waiting = System.Threading.Tasks.Task.Run(fun () -> handle second "UPDATE tx_rows SET n = n + 1 WHERE id = 1")
              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 150.0)) "the second transaction waits for the row"

              match handle first "COMMIT" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the first commit to succeed, got %A" other

              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 3.0)) "the waiting update resumes after commit"
              let second, secondUpdate = waiting.Result
              Expect.equal secondUpdate (Affected 1UL) "the second transaction updates the committed row"

              match handle second "COMMIT" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the second commit to succeed, got %A" other

              match handle (create 4 store) "SELECT n FROM tx_rows WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected both increments, got %A" other

          testCase "rolling back releases transactional row ownership"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_rows (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_rows VALUES (1, 0)"

              let first, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_rows SET n = n + 10 WHERE id = 1"
              Expect.equal firstUpdate (Affected 1UL) "the first transaction owns the row"

              let second, _ = handle (create 3 store) "BEGIN"
              let waiting = System.Threading.Tasks.Task.Run(fun () -> handle second "UPDATE tx_rows SET n = n + 1 WHERE id = 1")
              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 150.0)) "the second transaction waits for the row"

              match handle first "ROLLBACK" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected rollback to succeed, got %A" other

              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 3.0)) "the waiting update resumes after rollback"
              let second, secondUpdate = waiting.Result
              Expect.equal secondUpdate (Affected 1UL) "the second transaction updates the original row"
              handle second "COMMIT" |> snd |> Expect.equal <| Affected 0UL <| "the waiting transaction commits"

              match handle (create 4 store) "SELECT n FROM tx_rows WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the rolled-back increment to be absent, got %A" other

          testCase "transaction access modes, chaining, and SET CHARACTER SET are enforced"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_mode (id INT)"
              let session, started = handle session "START TRANSACTION READ ONLY"
              Expect.equal started (Affected 0UL) "read-only transaction starts"

              match handle session "INSERT INTO tx_mode VALUES (1)" with
              | session, Err(1792, _) ->
                  let session, chained = handle session "COMMIT AND CHAIN"
                  Expect.equal chained (Affected 0UL) "commit chains"
                  Expect.isTrue (session.Tx |> Option.exists _.ReadOnly) "chained transaction retains access mode"

                  let session, _ = handle session "COMMIT AND NO CHAIN"
                  Expect.isNone session.Tx "NO CHAIN ends the transaction"

                  let session, _ = handle session "SET TRANSACTION READ ONLY"
                  let session, _ = handle session "START TRANSACTION"
                  Expect.isTrue (session.Tx |> Option.exists _.ReadOnly) "configured access mode applies"
              | _, other -> failtestf "expected read-only error 1792, got %A" other

              match handle session "SET CHARACTER SET latin1" with
              | session, Affected 0UL ->
                  Expect.equal (session.Variables.["character_set_connection"]) (Some "latin1") "connection charset"
                  Expect.equal (session.Variables.["collation_connection"]) (Some "latin1_swedish_ci") "default collation"
              | _, other -> failtestf "expected SET CHARACTER SET to succeed, got %A" other

          testCase "XA transactions detach at prepare and commit across sessions"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let second = create 2 store
              let first, _ = handle first "CREATE TABLE xa_rows (id INT PRIMARY KEY, value INT)"
              let first, started = handle first "XA START 'global', 'branch', 7"
              Expect.equal started (Affected 0UL) "XA branch starts"
              let first, inserted = handle first "INSERT INTO xa_rows VALUES (1, 10)"
              Expect.equal inserted (Affected 1UL) "branch writes privately"

              match handle second "SELECT COUNT(*) FROM xa_rows" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected the unprepared write to stay private, got %A" other

              match handle first "COMMIT" |> snd with
              | Err(1399, message) -> Expect.stringContains message "ACTIVE state" "local COMMIT is refused"
              | other -> failtestf "expected XAER_RMFAIL in ACTIVE state, got %A" other

              let first, ended = handle first "XA END 'global', 'branch', 7"
              Expect.equal ended (Affected 0UL) "XA branch becomes idle"

              match handle first "SELECT 1" |> snd with
              | Err(1399, message) -> Expect.stringContains message "IDLE state" "idle branches reject ordinary statements"
              | other -> failtestf "expected XAER_RMFAIL in IDLE state, got %A" other

              let first, prepared = handle first "XA PREPARE 'global', 'branch', 7"
              Expect.equal prepared (Affected 0UL) "XA branch prepares"
              Expect.isNone first.Tx "prepare detaches the local transaction"

              match handle second "XA RECOVER" |> snd with
              | ResultSet(columns, [ [ Some "7"; Some "6"; Some "6"; Some "globalbranch" ] ]) ->
                  Expect.equal columns [ "formatID"; "gtrid_length"; "bqual_length"; "data" ] "recover columns"
              | other -> failtestf "expected one recoverable branch, got %A" other

              let second, committed = handle second "XA COMMIT 'global', 'branch', 7"
              Expect.equal committed (Affected 0UL) "another session commits the detached branch"

              match handle second "SELECT value FROM xa_rows WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected the XA write after commit, got %A" other

              match handle first "XA COMMIT 'global', 'branch', 7" |> snd with
              | Err(1397, message) -> Expect.stringContains message "Unknown XID" "completed branch is gone"
              | other -> failtestf "expected XAER_NOTA after completion, got %A" other

          testCase "XA one-phase commit rollback and duplicate identifiers follow MySQL states"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let observer = create 2 store
              let session, _ = handle session "CREATE TABLE xa_outcomes (id INT PRIMARY KEY)"
              let session, _ = handle session "XA START X'6F6E65', b'10', 4294967295"
              let session, _ = handle session "INSERT INTO xa_outcomes VALUES (1)"
              let session, _ = handle session "XA END X'6F6E65', b'10', 4294967295"
              let session, committed = handle session "XA COMMIT X'6F6E65', b'10', 4294967295 ONE PHASE"
              Expect.equal committed (Affected 0UL) "idle branch commits in one phase"

              let session, _ = handle session "XA START 'rollback'"
              let session, _ = handle session "INSERT INTO xa_outcomes VALUES (2)"
              let session, _ = handle session "XA END 'rollback'"
              let session, _ = handle session "XA PREPARE 'rollback'"

              match handle observer "XA START 'rollback'" |> snd with
              | Err(1440, message) -> Expect.stringContains message "XID already exists" "prepared identifiers stay reserved"
              | other -> failtestf "expected XAER_DUPID, got %A" other

              let observer, rolledBack = handle observer "XA ROLLBACK 'rollback'"
              Expect.equal rolledBack (Affected 0UL) "another session rolls the branch back"

              match handle observer "SELECT GROUP_CONCAT(id ORDER BY id) FROM xa_outcomes" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected only the one-phase commit, got %A" other

              match handle session "XA END 'missing'" |> snd with
              | Err(1399, message) -> Expect.stringContains message "NON-EXISTING state" "no branch is associated"
              | other -> failtestf "expected XAER_RMFAIL without an associated branch, got %A" other

          testCase "XA statements are refused by the prepared protocol"
          <| fun _ ->
              match prepareStatement "XA START 'prepared'" with
              | Result.Error(1295, message) -> Expect.stringContains message "prepared statement protocol" "prepared refusal"
              | other -> failtestf "expected XA prepare refusal, got %A" other

          // -----------------------------------------------------------------
          // Session user identity + the built-in `mysql` system schema
          // -----------------------------------------------------------------

          QueryHandlerAccountTests.tests

          testCase "SqlError surfaces its chosen code and message and still aborts the transaction"
          <| fun _ ->
              let boom =
                  Fsdb.Functions.ScalarFunction.create "BOOM" (fun _ _ -> raise (Fsdb.Functions.SqlError(1210, "no such model")))

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension boom }

              let session, _ = handle session "CREATE TABLE t (n INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, result = handle session "SELECT BOOM()"

              match result with
              | Err(1210, msg) -> Expect.stringContains msg "no such model" "the chosen message reaches the client"
              | other -> failtestf "expected the chosen 1210, got %A" other

              Expect.isNone session.Tx "the transaction aborts, same as any other throwing function"

              match handle session "SELECT COUNT(*) FROM t" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected the aborted transaction's INSERT rolled back, got %A" other

          testCase "a DirectOnly function is rejected inside a generated column definition but fine in SELECT"
          <| fun _ ->
              let embeddish =
                  Fsdb.Functions.ScalarFunction.create "EMBEDDISH" (fun _ _ -> VString "x")
                  |> Fsdb.Functions.ScalarFunction.effectful

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension embeddish }

              match handle session "SELECT EMBEDDISH('a')" |> snd with
              | ResultSet(_, [ [ Some "x" ] ]) -> ()
              | other -> failtestf "expected the effectful function to run in a plain SELECT, got %A" other

              match handle session "CREATE TABLE d (a VARCHAR(10), b VARCHAR(10) AS (EMBEDDISH(a)))" |> snd with
              | Err(3102, msg) -> Expect.stringContains msg "generated column 'b'" "names the offending column"
              | other -> failtestf "expected 3102 at CREATE time, got %A" other

              let session, _ = handle session "CREATE TABLE d2 (a VARCHAR(10))"

              match handle session "ALTER TABLE d2 ADD COLUMN b VARCHAR(10) AS (EMBEDDISH(a))" |> snd with
              | Err(3102, _) -> ()
              | other -> failtestf "expected 3102 at ALTER time, got %A" other

              // A subquery smuggles the call past the DDL traversal — the
              // eval-time backstop must still refuse to invoke the function
              // when the engine evaluates the generated column on INSERT.
              let session, createResult =
                  handle session "CREATE TABLE d3 (a VARCHAR(10), b VARCHAR(10) AS ((SELECT EMBEDDISH(a))))"

              match createResult with
              | Affected _ -> ()
              | other -> failtestf "expected the subquery-smuggled definition to slip past DDL, got %A" other

              match handle session "INSERT INTO d3 (a) VALUES ('x')" |> snd with
              | Err(3102, msg) -> Expect.stringContains msg "EMBEDDISH" "names the offending function"
              | other -> failtestf "expected the eval-time backstop's 3102 on INSERT, got %A" other

              match handle session "SELECT EMBEDDISH('a')" |> snd with
              | ResultSet(_, [ [ Some "x" ] ]) -> ()
              | other -> failtestf "expected direct SELECT still fine after the backstop fired, got %A" other

          testCase "a rich function's QueryContext agrees with DATABASE() and CURRENT_USER()"
          <| fun _ ->
              let probe =
                  Fsdb.Functions.ScalarFunction.create "CTXPROBE" (fun ctx _ ->
                      VString(sprintf "%s|%s" (ctx.Database |> Option.defaultValue "<none>") ctx.User))

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension probe }

              let session, _ = handle session "CREATE DATABASE app"
              let session, _ = handle session "USE app"

              match handle session "SELECT CTXPROBE(), DATABASE(), CURRENT_USER()" |> snd with
              | ResultSet(_, [ [ Some ctx; Some db; Some user ] ]) ->
                  Expect.equal ctx (db + "|" + (user.Split '@').[0]) "context Database/User match the SQL-visible session"
              | other -> failtestf "expected one row, got %A" other

          testCase "temporary tables shadow permanent tables per session"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let second = create 2 store
              let first, _ = handle first "CREATE TABLE sample (n INT)"
              let first, _ = handle first "INSERT INTO sample VALUES (1)"
              let commits = ResizeArray<Fsdb.Storage.CommitEvent>()
              store.OnCommit.Add commits.Add

              let first, created = handle first "CREATE TEMPORARY TABLE sample (n VARCHAR(7))"
              Expect.equal created (Affected 0UL) "temporary table created"
              let first, inserted = handle first "INSERT INTO sample VALUES (2)"
              Expect.equal inserted (Affected 1UL) "temporary row inserted"

              match handle first "SELECT n FROM sample" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected the temporary row, got %A" other

              match handle second "SELECT n FROM sample" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the permanent row, got %A" other

              match handle first "DESCRIBE sample" |> snd with
              | ResultSet(_, [ [ Some "n"; Some "varchar(7)"; _; _; _; _ ] ]) -> ()
              | other -> failtestf "expected temporary metadata, got %A" other

              let first, dropped = handle first "DROP TEMPORARY TABLE sample"
              Expect.equal dropped (Affected 0UL) "temporary table dropped"

              match handle first "SELECT n FROM sample" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the permanent row after DROP TEMPORARY, got %A" other

              Expect.isEmpty first.TemporaryCatalog "session catalog is empty"
              Expect.equal store.Catalog.[Fsdb.Storage.defaultDatabase].Count 1 "shared catalog contains only the permanent table"
              Expect.isEmpty commits "temporary changes emit no shared commit events"

          testCase "temporary sources can feed permanent writes without publication"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE destination (n INT)"
              let session, _ = handle session "CREATE TEMPORARY TABLE staging (n INT)"
              let session, _ = handle session "INSERT INTO staging VALUES (3), (4)"
              let session, inserted = handle session "INSERT INTO destination SELECT n FROM staging"
              Expect.equal inserted (Affected 2UL) "permanent rows inserted"

              match handle session "SELECT GROUP_CONCAT(n ORDER BY n) FROM destination" |> snd with
              | ResultSet(_, [ [ Some "3,4" ] ]) -> ()
              | other -> failtestf "expected rows copied from the temporary table, got %A" other

              let published =
                  store.Catalog
                  |> Map.tryFind Fsdb.Storage.defaultDatabase
                  |> Option.exists (Map.containsKey "staging")

              Expect.isFalse published "temporary table was not published"

          testCase "temporary tables support ALTER and CREATE INDEX"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, created = handle session "CREATE TEMPORARY TABLE staging (n INT)"
              Expect.equal created (Affected 0UL) "temporary table created"

              let session, altered = handle session "ALTER TABLE staging COMMENT='temporary work'"
              Expect.equal altered (Affected 0UL) "temporary table altered"

              let session, uniqueIndex = handle session "CREATE UNIQUE INDEX ux_staging ON staging (n)"
              Expect.equal uniqueIndex (Affected 0UL) "unique index created"

              let session, index = handle session "CREATE INDEX ix_staging ON staging (n)"
              Expect.equal index (Affected 0UL) "secondary index created"

              match handle session "SHOW CREATE TABLE staging" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "COMMENT='temporary work'" "table comment"
                  Expect.stringContains ddl "UNIQUE KEY `ux_staging` (`n`)" "unique index"
                  Expect.stringContains ddl "KEY `ix_staging` (`n`)" "secondary index"
              | other -> failtestf "expected temporary DDL, got %A" other

              let published =
                  store.Catalog
                  |> Map.tryFind Fsdb.Storage.defaultDatabase
                  |> Option.exists (Map.containsKey "staging")

              Expect.isFalse published "temporary table was not published"

          testCase "temporary tables support engine-qualified create-as-select"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE source (name VARCHAR(20))"
              let session, _ = handle session "INSERT INTO source VALUES ('first'), ('second')"

              let session, created =
                  handle session "CREATE TEMPORARY TABLE staging ENGINE=MEMORY SELECT name FROM source"

              Expect.equal created (Affected 2UL) "temporary rows copied"

              match handle session "SELECT GROUP_CONCAT(name ORDER BY name) FROM staging" |> snd with
              | ResultSet(_, [ [ Some "first,second" ] ]) -> ()
              | other -> failtestf "expected rows copied into temporary table, got %A" other

              match handle session "SHOW CREATE TABLE staging" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) -> Expect.stringStarts ddl "CREATE TEMPORARY TABLE" "temporary DDL"
              | other -> failtestf "expected temporary SHOW CREATE output, got %A" other

              Expect.isFalse (Map.containsKey "staging" store.Catalog.[Fsdb.Storage.defaultDatabase]) "temporary table was not published"

          testCase "a bare ? over COM_QUERY, incl. in a DDL generated column, is a 1064 (never reaches storage)"
          <| fun _ ->
              let session = create 991001 (Fsdb.Storage.create ())

              match handle session "SELECT ?" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a bare ?, got %A" other

              // The crash vector: a placeholder in a generated-column DDL
              // expression must not survive into Storage/Persistence.
              match handle session "CREATE TABLE t (a INT, b INT AS (?))" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a DDL placeholder, got %A" other

              match handle session "SHOW TABLES FROM d WHERE Tables_in_d = ?" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a probe placeholder, got %A" other

          testCase "prepareStatement rejects a placeholder the binder can't reach (DDL generated column)"
          <| fun _ ->
              match prepareStatement "CREATE TABLE t (a INT, b INT AS (?))" with
              | Result.Error(1064, _) -> ()
              | other -> failtestf "expected a 1064 prepare error, got %A" other

          testCase "redactSql hides credential statements and string/number literals"
          <| fun _ ->
              Expect.equal
                  (Fsdb.Log.redactSql "CREATE USER 'a'@'%' IDENTIFIED BY 'hunter2'")
                  "[REDACTED CREDENTIAL STATEMENT]"
                  "credential statement collapses whole"

              Expect.equal
                  (Fsdb.Log.redactSql "SET PASSWORD FOR 'a' = 'secret'")
                  "[REDACTED CREDENTIAL STATEMENT]"
                  "SET PASSWORD collapses"

              Expect.equal
                  (Fsdb.Log.redactSql "SELECT * FROM t WHERE token = 'secret' AND n = 42")
                  "SELECT * FROM t WHERE token = ? AND n = ?"
                  "string and number literals become ?"

              Expect.equal
                  (Fsdb.Log.redactSql "SELECT `id_1` FROM `t2`")
                  "SELECT `id_1` FROM `t2`"
                  "backticked identifiers and their digits are kept"

              let bounded = Fsdb.Log.redactSql ("SELECT " + String.replicate 4096 "x" + "\nforged")
              Expect.equal bounded.Length 1024 "logged SQL is bounded"
              Expect.stringEnds bounded "..." "truncation is visible"
              Expect.equal (Fsdb.Log.redactSql "SELECT raw\nforged") "SELECT raw?forged" "control characters cannot forge log lines" ]
