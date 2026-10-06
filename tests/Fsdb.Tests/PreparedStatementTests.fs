module Fsdb.Tests.PreparedStatementTests

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
        "PreparedStatements"
        [ testCase "placeholderPositions counts only ? outside strings, comments, and backtick identifiers"
          <| fun _ ->
              let sql =
                  "SELECT * FROM t WHERE a = ? AND b = '?' AND c = \"?\" AND d = `?` -- ?\nAND e = ? /* ? */ AND f = ?"

              Expect.equal (placeholderPositions sql |> List.length) 3 "three real placeholders (a, e, f)"

          testCase "placeholderPositions treats a doubled quote as an escaped quote, not the string's end"
          <| fun _ ->
              let sql = "SELECT * FROM t WHERE a = 'it''s a ? mystery' AND b = ?"
              Expect.equal (placeholderPositions sql |> List.length) 1 "one real placeholder"

          testCase "placeholderPositions treats a backslash-escaped quote as not ending the string"
          <| fun _ ->
              let sql = @"SELECT * FROM t WHERE a = 'a \' ? b' AND b = ?"
              Expect.equal (placeholderPositions sql |> List.length) 1 "one real placeholder"

          testCase "substitutePlaceholders replaces placeholders in order and leaves the rest of the SQL untouched"
          <| fun _ ->
              let sql = "INSERT INTO t (a, b) VALUES (?, ?)"
              let result = substitutePlaceholders sql [ "1"; "'x'" ]
              Expect.equal result "INSERT INTO t (a, b) VALUES (1, 'x')" "substitution"

          testCase "valueToSqlLiteral escapes single quotes and backslashes in strings"
          <| fun _ ->
              Expect.equal (valueToSqlLiteral (VString "O'Brien\\")) "'O\\'Brien\\\\'" "escaped literal"

          testCase "valueToSqlLiteral escapes CR/LF so a bound param round-trips through re-parsing"
          <| fun _ ->
              // A raw CR spliced into the SQL text gets silently normalized
              // to LF by FParsec's CharStream on re-parse (it
              // treats bare \r/\r\n as line endings) unless the literal
              // escapes it, corrupting any CRLF value a prepared statement
              // substitutes in — e.g. an HTML textarea's body.
              let original = "a\r\nb\rc"
              let literal = valueToSqlLiteral (VString original)
              Expect.stringContains literal "\\r" "CR is escaped in the literal"

              match Fsdb.Parser.parse (sprintf "SELECT %s AS x" literal) with
              | Result.Ok(Select { Projections = [ Lit(VString roundtripped), _ ] }) ->
                  Expect.equal roundtripped original "CR/LF survive the literal round-trip"
              | other -> failtestf "expected a parsed SELECT literal, got %A" other

          testCase "valueToSqlLiteral renders NULL for VNull and a plain digit string for VInt"
          <| fun _ ->
              Expect.equal (valueToSqlLiteral VNull) "NULL" "null literal"
              Expect.equal (valueToSqlLiteral (VInt 42L)) "42" "int literal"

          testCase "valueToSqlLiteral renders raw bytes as a hexadecimal literal"
          <| fun _ ->
              let literal = valueToSqlLiteral (VBytes [| 0x00uy; 0xffuy; 0x80uy |])
              Expect.equal literal "X'00FF80'" "lossless binary literal"

              match Fsdb.Parser.parse ("SELECT " + literal) with
              | Result.Ok(Select { Projections = [ Lit(VBytes bytes), _ ] }) ->
                  Expect.equal bytes [| 0x00uy; 0xffuy; 0x80uy |] "prepared substitution round-trip"
              | other -> failtestf "expected a parsed binary literal, got %A" other

          testCase "prepareStatement reports the placeholder count for a valid statement"
          <| fun _ ->
              match prepareStatement "INSERT INTO t (a, b) VALUES (?, ?)" with
              | Result.Ok(Some _, 2) -> ()
              | other -> failtestf "expected Ok 2, got %A" other

          testCase "prepareStatement reports a 1064 syntax error for invalid SQL"
          <| fun _ ->
              match prepareStatement "GARBAGE NOT SQL" with
              | Result.Error(1064, _) -> ()
              | other -> failtestf "expected a 1064 error, got %A" other

          testCase "prepareStatement accepts SET/SHOW/transaction-control forms the grammar itself doesn't parse"
          <| fun _ ->
              // Laravel's Schema::disableForeignKeyConstraints() runs
              // Connection::statement(), which always calls PDO::prepare()
              // regardless of emulation — real MySQL PDO's default is
              // ATTR_EMULATE_PREPARES = false, so even a bare `SET
              // FOREIGN_KEY_CHECKS=0` goes through COM_STMT_PREPARE.
              for sql in
                  [ "SET FOREIGN_KEY_CHECKS=0"
                    "SET NAMES utf8mb4"
                    "START TRANSACTION"
                    "COMMIT"
                    "COMMIT RELEASE"
                    "ROLLBACK AND CHAIN NO RELEASE"
                    "SHOW TABLES" ] do
                  match prepareStatement sql with
                  | Result.Ok(None, 0) -> ()
                  | other -> failtestf "expected %s to prepare with 0 placeholders, got %A" sql other

          testCase "a prepared SHOW COLUMNS filters by its bound Field value"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE metadata_fields (id BINARY(16), name VARCHAR(255) NOT NULL)"
              let sql = "SHOW COLUMNS FROM metadata_fields WHERE Field = ?"

              match prepareStatement sql with
              | Result.Ok(None, 1) ->
                  let statement =
                      { Ast = None
                        Sql = sql
                        ParamCount = 1
                        LastParamTypes = None
                        ParameterTypes = None }

                  match executePrepared session statement [ VString "name" ] |> snd with
                  | ResultSet(_, [ [ Some "name"; Some "varchar(255)"; Some "NO"; Some ""; None; Some "" ] ]) -> ()
                  | other -> failtestf "expected only the bound metadata field, got %A" other
              | other -> failtestf "expected a text-probed prepared SHOW COLUMNS, got %A" other

          testCase "prepared projection names survive parameter binding and repeated execution"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql, names in
                  [ "SELECT ?, ABS(?), ? + 1", [ "?"; "ABS(?)"; "? + 1" ]
                    "SELECT d.`?` FROM (SELECT ?) d", [ "?" ]
                    "WITH c AS (SELECT ?) SELECT * FROM c", [ "?" ]
                    "SELECT ? UNION ALL SELECT ?", [ "?" ]
                    "SELECT ? AS chosen", [ "chosen" ] ] do
                  let ast, count =
                      match prepareStatement sql with
                      | Ok prepared -> prepared
                      | Error error -> failtestf "prepare failed: %A" error
                  let statement = { Ast = ast; Sql = sql; ParamCount = count; LastParamTypes = None; ParameterTypes = None }
                  for value in [ VInt -2L; VInt 7L; VNull ] do
                      match executePrepared session statement (List.replicate count value) |> snd with
                      | ResultSet(columns, _) -> Expect.equal columns names sql
                      | other -> failtestf "%s returned %A" sql other

          testCase "SQL EXECUTE retains projection names from PREPARE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, prepared = handle session "PREPARE labels FROM 'SELECT ?, ABS(?)'"
              Expect.equal prepared (Affected 0UL) "prepared"
              for value in [ "-2"; "7"; "NULL" ] do
                  let session, _ = handle session ("SET @label_value=" + value)
                  match handle session "EXECUTE labels USING @label_value,@label_value" |> snd with
                  | ResultSet(columns, _) -> Expect.equal columns [ "?"; "ABS(?)" ] "stable SQL prepared names"
                  | other -> failtestf "EXECUTE returned %A" other

          testCase "SQL prepared parameter types retain widening across executions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "PREPARE typed FROM 'SELECT ?, ABS(?), ? + 1'"
              let steps =
                  [ "-2", TypeLongLong, [ Some "-2"; Some "2"; Some "-1" ]
                    "'-3'", TypeNewDecimal, [ Some "-3"; Some "3"; Some "-2" ]
                    "NULL", TypeNewDecimal, [ None; None; None ]
                    "1.25", TypeNewDecimal, [ Some "1.25"; Some "1.25"; Some "2.25" ]
                    "-4", TypeNewDecimal, [ Some "-4"; Some "4"; Some "-3" ]
                    "1.5e0", TypeDouble, [ Some "1.5"; Some "1.5"; Some "2.5" ]
                    "'oops'", TypeDouble, [ Some "0"; Some "0"; Some "1" ]
                    "-5", TypeDouble, [ Some "-5"; Some "5"; Some "-4" ] ]

              (session, steps)
              ||> List.fold (fun session (value, expectedType, expectedRow) ->
                  let session, _ = handle session ("SET @v=" + value)
                  let session, result = handle session "EXECUTE typed USING @v,@v,@v"
                  match result with
                  | ResultSet(_, [ row ]) ->
                      let numbers = List.map (Option.map (fun (value: string) -> Decimal.Parse(value, Globalization.CultureInfo.InvariantCulture)))
                      Expect.equal (numbers row) (numbers expectedRow) value
                  | other -> failtestf "execution returned %A" other
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      (List.replicate 3 expectedType)
                      ("retained types for " + value)
                  session)
              |> ignore

          testCase "statement-wide repreparation rederives NULL markers from their context"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "PREPARE typed FROM 'SELECT ?, ABS(?)'"
              let steps =
                  [ "-2", "-2", [ TypeLongLong; TypeLongLong ]
                    "NULL", "1.25", [ TypeVarString; TypeNewDecimal ]
                    "-4", "-4", [ TypeLongLong; TypeLongLong ] ]
              (session, steps)
              ||> List.fold (fun session (first, second, expected) ->
                  let session, _ = handle session ("SET @a=" + first + ",@b=" + second)
                  let session, result = handle session "EXECUTE typed USING @a,@b"
                  match result with
                  | ResultSet(_, [ _ ]) -> ()
                  | other -> failtestf "execution returned %A" other
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) expected "rederived types"
                  session)
              |> ignore

          testCase "binary prepared handles retain independent parameter types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ?, ABS(?)"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              let session = { session with Statements = Map.ofList [ 1, statement; 2, statement ] }
              let execute session id value expected =
                  let session, result = executePreparedHandle session id [ value; value ]
                  match result with
                  | ResultSet(_, [ _ ]) -> ()
                  | other -> failtestf "execution returned %A" other
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ expected; expected ]
                      "handle-local types"
                  session
              let session = execute session 1 (VInt -2L) TypeLongLong
              let session = execute session 2 (VDouble 1.5) TypeDouble
              let session = execute session 1 (VString "-3") TypeNewDecimal
              let session = execute session 2 (VInt -4L) TypeDouble
              let session = execute session 1 VNull TypeNewDecimal
              execute session 2 VNull TypeDouble |> ignore

          testCase "numeric prepared expressions use rederived families and integer argument rounding"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql, parameters, types, expected in
                  [ "SELECT MOD(?, ?)", [ VDecimal 5.5M; VDecimal 2M ], [ TypeDouble ], [ "1.5" ]
                    "SELECT ?, MOD(?, ?)", [ VInt 1L; VDecimal 5.5M; VDecimal 2M ],
                        [ TypeLongLong; TypeNewDecimal ], [ "1"; "1.5" ]
                    "SELECT ?, CAST(? AS SIGNED), SUBSTRING('abcdef', ?)", [ VInt -2L; VDouble 2.5; VDouble 2.5 ],
                        [ TypeLongLong; TypeLongLong; TypeVarString ], [ "-2"; "2"; "bcdef" ] ] do
                  let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
                  let statement = createPreparedStatement session sql ast count
                  let session, result = executePrepared session statement parameters
                  match result with
                  | ResultSet(_, [ row ]) ->
                      let normalize (value: string) =
                          match Decimal.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                          | true, number -> number.ToString("G29", Globalization.CultureInfo.InvariantCulture)
                          | _ -> value
                      Expect.equal (row |> List.map (Option.map normalize)) (expected |> List.map Some) sql
                  | other -> failtestf "%s returned %A" sql other
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) types sql

          testCase "binary prepared temporal parameters retain DATETIME after widening from DATE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let ast, count = prepareStatement "SELECT ?" |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session "SELECT ?" ast count
              let session = { session with Statements = Map.ofList [ 1, statement ] }
              let steps =
                  [ VDate(DateOnly(2024, 1, 2)), TypeDate, Some "2024-01-02"
                    VDateTime(DateTime(2024, 1, 3, 12, 30, 0)), TypeDateTime, Some "2024-01-03 12:30:00"
                    VDate(DateOnly(2024, 1, 4)), TypeDateTime, Some "2024-01-04 00:00:00"
                    VInt 20240105L, TypeDateTime, Some "2024-01-05 00:00:00"
                    VNull, TypeDateTime, None ]
              (session, steps)
              ||> List.fold (fun session (value, expectedType, expectedValue) ->
                  let session, result = executePreparedHandle session 1 [ value ]
                  Expect.equal result (ResultSet([ "?" ], [ [ expectedValue ] ])) "temporal value"
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ expectedType ] "retained temporal type"
                  session)
              |> ignore

          testCase "prepared parameter inference honors signatures that override polymorphic builtins"
          <| fun _ ->
              for name in [ "MOD"; "GREATEST"; "LEAST" ] do
                  let extension =
                      Fsdb.Functions.ScalarFunction.create name (fun _ _ -> VNull)
                      |> Fsdb.Functions.ScalarFunction.withSignature [ TJson; TJson ] TJson
                  let session =
                      { create 1 (Fsdb.Storage.create ()) with
                          CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension extension }
                  let sql = "SELECT " + name + "(?, ?)"
                  let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
                  let parameters, _ = preparedMetadata session ast count
                  Expect.equal (parameters |> List.map _.TypeId) [ TypeJson; TypeJson ] name

          testCase "prepared parameter types survive an execution error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ?, ABS(?)"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              let session = { session with Statements = Map.ofList [ 1, statement ] }
              let session, result = executePreparedHandle session 1 [ VInt Int64.MinValue; VInt Int64.MinValue ]
              match result with
              | Err(1690, _) -> ()
              | other -> failtestf "expected signed overflow, got %A" other
              let session, result = executePreparedHandle session 1 [ VNull; VNull ]
              Expect.equal result (ResultSet([ "?"; "ABS(?)" ], [ [ None; None ] ])) "NULL execution after error"
              Expect.equal
                  (session.LastResultColumnMetadata |> List.map _.TypeId)
                  [ TypeLongLong; TypeLongLong ]
                  "derived types survive the failed execution"

          testCase "a prepared INSERT/SELECT binds values into the parsed AST and executes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE ps_t (id INT, name VARCHAR(50))"

              match prepareStatement "INSERT INTO ps_t (id, name) VALUES (?, ?)" with
              | Result.Ok(Some ast, 2) ->
                  let stmt =
                      { Ast = Some ast
                        Sql = "INSERT INTO ps_t (id, name) VALUES (?, ?)"
                        ParamCount = 2
                        LastParamTypes = None
                        ParameterTypes = None }

                  // The name carries a quote and a backslash — bound as a
                  // `Value` into the AST, never re-spliced SQL text, so there
                  // is nothing to escape and the string arrives intact.
                  let session, insertResult = executePrepared session stmt [ VInt 1L; VString "O'Brien\\" ]

                  match insertResult with
                  | Affected 1UL -> ()
                  | other -> failtestf "expected 1 affected row, got %A" other

                  match handle session "SELECT name FROM ps_t WHERE id = 1" |> snd with
                  | ResultSet(_, [ [ Some "O'Brien\\" ] ]) -> ()
                  | other -> failtestf "expected the bound name back, got %A" other
              | other -> failtestf "expected a parsed statement with 2 params, got %A" other

          testCase "prepared parameters are coerced to their inferred builtin argument types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql =
                  "SELECT SUBSTRING('abcdef', ?), "
                  + "ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?)), "
                  + "CAST(? AS SIGNED), WEEK('2024-01-01', ?), SHA2('x', ?)"

              match prepareStatement sql with
              | Result.Ok(Some ast, 5) ->
                  let statement =
                      { Ast = Some ast
                        Sql = sql
                        ParamCount = 5
                        LastParamTypes = None
                        ParameterTypes = None }

                  let parameters =
                      [ VString "1.9"; VDouble 1.5; VDouble 1.5; VDouble 1.5; VString "255.5" ]

                  match executePrepared session statement parameters |> snd with
                  | ResultSet(_, [ [ Some "bcdef"; Some "POINT(20 0)"; Some "2"; Some "53"; Some digest ] ]) ->
                      Expect.equal
                          digest
                          "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881"
                          "SHA2 bit length rounds in its inferred integer context"
                  | other -> failtestf "expected integral marker coercion before builtin evaluation, got %A" other
              | other -> failtestf "expected a parsed statement with 5 params, got %A" other

          testCase "spatial prepared metadata distinguishes geometry, index, and radius parameters"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ST_PointN(?, ?), ST_Distance_Sphere(?, ?, ?), ST_Length(?, ?)"

              match prepareStatement sql with
              | Result.Ok(statement, 7) ->
                  let parameters, _ = preparedMetadata session statement 7

                  Expect.equal
                      (parameters |> List.map _.TypeId)
                      [ TypeGeometry; TypeLongLong; TypeGeometry; TypeGeometry; TypeDouble; TypeGeometry; TypeVarString ]
                      "spatial parameter families"
              | other -> failtestf "expected a parsed statement with 7 params, got %A" other

          testCase "prepared comparison parameters retain their dynamic type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE prepared_types (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO prepared_types VALUES (1), (2)"
              let sql = "SELECT id FROM prepared_types WHERE id = ?"

              match prepareStatement sql with
              | Result.Ok(Some ast, 1) ->
                  let statement =
                      { Ast = Some ast
                        Sql = sql
                        ParamCount = 1
                        LastParamTypes = None
                        ParameterTypes = None }

                  match executePrepared session statement [ VString "1.5" ] |> snd with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected comparison coercion rather than marker pre-conversion, got %A" other
              | other -> failtestf "expected a parsed statement with one param, got %A" other

          testCase "SQL EXECUTE applies the same contextual parameter coercion"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @position = 1.5"
              let session, _ = handle session "PREPARE pick_suffix FROM 'SELECT SUBSTRING(\"abcdef\", ?)'"

              match handle session "EXECUTE pick_suffix USING @position" |> snd with
              | ResultSet(_, [ [ Some "bcdef" ] ]) -> ()
              | other -> failtestf "expected SQL EXECUTE to use the inferred integer context, got %A" other

          testCase "prepared protocol LIMIT and OFFSET enforce MySQL value families"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE prepared_limits (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO prepared_limits VALUES (1), (2), (3)"

              let execute sql value =
                  match prepareStatement sql with
                  | Result.Ok(Some ast, 1) ->
                      executePrepared
                          session
                          { Ast = Some ast
                            Sql = sql
                            ParamCount = 1
                            LastParamTypes = None
                            ParameterTypes = None }
                          [ value ]
                      |> snd
                  | other -> failtestf "expected one LIMIT parameter in %s, got %A" sql other

              let limitedStatements =
                  [ "SELECT id FROM prepared_limits ORDER BY id LIMIT ?"
                    "SELECT id FROM prepared_limits ORDER BY id LIMIT 1 OFFSET ?"
                    "UPDATE prepared_limits SET id = id LIMIT ?"
                    "DELETE FROM prepared_limits LIMIT ?" ]

              for sql in limitedStatements do
                  for invalid in [ VDouble 1.0; VDecimal 1M ] do
                      match execute sql invalid with
                      | Err(1210, _) -> ()
                      | other -> failtestf "expected error 1210 for %A in %s, got %A" invalid sql other

                  match execute sql (VInt -1L) with
                  | Err(1690, _) -> ()
                  | other -> failtestf "expected error 1690 for a negative limit in %s, got %A" sql other

              let selectSql = "SELECT id FROM prepared_limits ORDER BY id LIMIT ?"

              match execute selectSql (VInt 2L) with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
              | other -> failtestf "expected a non-negative integer LIMIT to execute, got %A" other

              match execute selectSql (VString "1.9") with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected protocol text to retain numeric-string conversion, got %A" other

              for emptyLimit in [ VString "abc"; VNull ] do
                  match execute selectSql emptyLimit with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected %A to produce an empty limited result, got %A" emptyLimit other

          testCase "SQL EXECUTE enforces prepared LIMIT parameter types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @row_count = '2'"
              let session, _ = handle session "PREPARE limited FROM 'SELECT 1 LIMIT ?'"

              match handle session "EXECUTE limited USING @row_count" |> snd with
              | Err(1210, _) -> ()
              | other -> failtestf "expected error 1210 for a string LIMIT, got %A" other

              let session, _ = handle session "SET @row_count = NULL"

              match handle session "EXECUTE limited USING @row_count" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected a NULL user-variable LIMIT to produce no rows, got %A" other

              let session, _ = handle session "SET @row_count = -1"

              match handle session "EXECUTE limited USING @row_count" |> snd with
              | Err(1690, _) -> ()
              | other -> failtestf "expected error 1690 for a negative user-variable LIMIT, got %A" other

          testCase "a prepared CTE update binds source and assignment parameters"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE cte_update (id INT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO cte_update VALUES (1, 10), (2, 20)"
              let sql = "WITH chosen AS (SELECT id FROM cte_update WHERE id = ?) UPDATE cte_update SET n = ? WHERE id IN (SELECT id FROM chosen)"

              match prepareStatement sql with
              | Result.Ok(Some ast, 2) ->
                  let statement =
                      { Ast = Some ast
                        Sql = sql
                        ParamCount = 2
                        LastParamTypes = None
                        ParameterTypes = None }

                  let session, result = executePrepared session statement [ VInt 2L; VInt 99L ]
                  Expect.equal result (Affected 1UL) "the selected row updates"

                  match handle session "SELECT id, n FROM cte_update ORDER BY id" |> snd with
                  | ResultSet(_, rows) ->
                      Expect.equal rows [ [ Some "1"; Some "10" ]; [ Some "2"; Some "99" ] ] "both placeholders bind"
                  | other -> failtestf "expected updated rows, got %A" other
              | other -> failtestf "expected a parsed CTE update with two params, got %A" other

          testCase "a prepared ROW predicate binds each constructor field"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ROW(?, ?) IN (ROW(1, 2), ROW(3, 4)) AS matches"

              match prepareStatement sql with
              | Result.Ok(Some ast, 2) ->
                  let statement =
                      { Ast = Some ast
                        Sql = sql
                        ParamCount = 2
                        LastParamTypes = None
                        ParameterTypes = None }

                  match executePrepared session statement [ VInt 3L; VInt 4L ] |> snd with
                  | ResultSet(_, [ [ Some "1" ] ]) -> ()
                  | other -> failtestf "expected bound ROW predicate to match, got %A" other
              | other -> failtestf "expected a parsed ROW predicate with 2 params, got %A" other

          testCase "a backtracked atom does not double-count its placeholder, and renumbering binds it correctly"
          <| fun _ ->
              // CONVERT(?, x) makes `convertUsingAtom` consume the `?` then
              // fail over to `genericFuncCall`; FParsec rewinds the input but
              // not the parse-time counter, so the raw count was 2 for one `?`
              // and the surviving node was `Placeholder 1` (a gap).
              match prepareStatement "SELECT CONVERT(?, CHAR)" with
              | Result.Ok(Some _, 1) -> ()
              | Result.Ok(Some _, n) -> failtestf "expected ParamCount 1, got %d" n
              | other -> failtestf "expected a parsed statement, got %A" other

              // A second `?` after the backtracked one: the raw counter
              // reported 3 for two placeholders; renumbering restores 2.
              match prepareStatement "SELECT CONVERT(?, CHAR), ? AS second" with
              | Result.Ok(Some _, 2) -> ()
              | Result.Ok(Some _, n) -> failtestf "expected ParamCount 2, got %d" n
              | other -> failtestf "expected a parsed statement, got %A" other

          testCase "prepared native functions preserve their MySQL error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "START TRANSACTION"

              match prepareStatement "SELECT RANDOM_BYTES(?)" with
              | Result.Ok(Some ast, 1) ->
                  let statement =
                      { Ast = Some ast
                        Sql = "SELECT RANDOM_BYTES(?)"
                        ParamCount = 1
                        LastParamTypes = None
                        ParameterTypes = None }

                  let session, result = executePrepared session statement [ VInt 0L ]

                  Expect.equal result (Err(1690, "The length of RANDOM_BYTES must be between 1 and 1024")) "error"
                  Expect.isNone session.Tx "transaction"
              | other -> failtestf "expected a parsed statement, got %A" other ]
