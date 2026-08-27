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

          testCase "LOCK TABLES accepts MySQL lock-list syntax"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "CREATE TABLE u (id INT)"
              let session, result = handle session "LOCK TABLES t READ, u AS writer WRITE"
              Expect.equal result (Affected 0UL) "lock list accepted"
              Expect.equal (handle session "UNLOCK TABLES" |> snd) (Affected 0UL) "unlock accepted"

          testCase "single-statement stored procedures persist and execute"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "CREATE PROCEDURE answer() SELECT 42 AS value"
              Expect.equal result (Affected 0UL) "created"

              match handle session "CALL answer()" with
              | session, ResultSet([ "value" ], [ [ Some "42" ] ]) ->
                  match handle session "SHOW PROCEDURE STATUS" |> snd with
                  | ResultSet(_, [ row ]) ->
                      Expect.equal row.[0] (Some "fsdb") "routine schema"
                      Expect.equal row.[1] (Some "answer") "routine name"
                  | other -> failtestf "expected routine status, got %A" other

                  match handle session "SHOW CREATE PROCEDURE answer" |> snd with
                  | ResultSet(_, [ [ Some "answer"; _; Some ddl; _; _; _ ] ]) ->
                      Expect.stringContains ddl "PROCEDURE `answer`() SELECT 42 AS value" "stored definition"
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
              | ResultSet([ "id" ], [ [ Some "42" ] ]) -> ()
              | other -> failtestf "expected procedure result, got %A" other

              match handle session "CALL `first_post`" |> snd with
              | ResultSet([ "id" ], [ [ Some "42" ] ]) -> ()
              | other -> failtestf "expected unparenthesized procedure result, got %A" other

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
                  Expect.stringContains ddl "ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 DAY" "stored schedule"
              | other -> failtestf "expected create event, got %A" other

              let session, recurring =
                  handle session "CREATE EVENT daily ON SCHEDULE EVERY 1 DAY DO INSERT INTO event_log VALUES (2)"
              Expect.equal recurring (Affected 0UL) "recurring event created"

              match
                  handle
                      session
                      "SELECT event_type, interval_value, interval_field FROM information_schema.events WHERE event_schema = 'fsdb' AND event_name = 'daily'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "RECURRING"; Some "1"; Some "DAY" ] ]) -> ()
              | other -> failtestf "expected recurring event metadata, got %A" other

              match
                  handle
                      session
                      "SELECT event_name FROM information_schema.events WHERE event_schema = 'fsdb' AND event_name = 'tomorrow'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "tomorrow" ] ]) -> ()
              | other -> failtestf "expected information_schema event, got %A" other

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

          testCase "SERIALIZABLE is accepted and READ UNCOMMITTED remains explicit"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET @@transaction_isolation = 'SERIALIZABLE'" with
              | configured, Affected 0UL ->
                  match handle configured "BEGIN" |> fst with
                  | { Tx = Some transaction } ->
                      Expect.equal transaction.Isolation Serializable "the next transaction is serializable"
                  | _ -> failtest "BEGIN did not create a transaction"
              | _, other -> failtestf "expected SERIALIZABLE to be accepted, got %A" other

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED" |> snd with
              | Err(1235, _) -> ()
              | other -> failtestf "expected READ UNCOMMITTED to remain explicit, got %A" other

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
