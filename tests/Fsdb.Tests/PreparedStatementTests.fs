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
                  | Result.Ok(_, 0) -> ()
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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                  let statement = { Ast = ast; Sql = sql; ParamCount = count; LastParamTypes = None; ParameterTypes = None; SchemaDependencies = Map.empty }
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

          testCase "prepared decimal division retains declared scale and operand precision"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=1.25"
              let session, _ = handle session "PREPARE division_types FROM 'SELECT @v/3 AS integral_divisor,@v/3.00 AS decimal_divisor'"
              let session, result = handle session "EXECUTE division_types"
              Expect.equal result
                  (ResultSet([ "integral_divisor"; "decimal_divisor" ],
                      [ [ Some "0.416666666000000000000000000000"; Some "0.416666666666666666000000000000" ] ]))
                  "division retains MySQL's operand-dependent intermediate precision"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.Decimals) [ 30uy; 30uy ]
                  "the result uses the captured decimal scale"

          testCase "decimal guard digits survive arithmetic but follow declared scale in text"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let _, result = handle session "SELECT CONCAT(1/3) AS text_value, (1/3)*3 AS numeric_value, CAST(10.00/3 AS CHAR) AS cast_value"
              Expect.equal result
                  (ResultSet([ "text_value"; "numeric_value"; "cast_value" ],
                      [ [ Some "0.3333"; Some "1.0000"; Some "3.333333" ] ]))
                  "text conversion and further arithmetic use different decimal precision"

          testCase "prepared decimal expressions distinguish fixed scale from value-preserving results"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=1.25"
              let session, _ = handle session "PREPARE decimal_types FROM 'SELECT @v AS direct,@v+1 AS added,@v*2 AS multiplied,ROUND(@v,2) AS rounded,COALESCE(@v,0) AS coalesced,CASE WHEN 1 THEN @v ELSE 0 END AS conditional'"
              let session, _ = handle session "SET @v=2"
              let session, result = handle session "EXECUTE decimal_types"
              Expect.equal result
                  (ResultSet([ "direct"; "added"; "multiplied"; "rounded"; "coalesced"; "conditional" ],
                      [ [ Some "2"; Some "3.000000000000000000000000000000"; Some "4.000000000000000000000000000000"
                          Some "2.00"; Some "2.000000000000000000000000000000"; Some "2" ] ]))
                  "direct and CASE reads preserve value scale while arithmetic uses declared scale"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.Decimals) [ 30uy; 30uy; 30uy; 2uy; 30uy; 30uy ]
                  "the descriptor scales follow each expression"
              let session, _ = handle session "SET @v=NULL"
              let _, result = handle session "EXECUTE decimal_types"
              Expect.equal result
                  (ResultSet([ "direct"; "added"; "multiplied"; "rounded"; "coalesced"; "conditional" ],
                      [ [ None; None; None; None; Some "0.000000000000000000000000000000"; None ] ]))
                  "an integer fallback retains the decimal result scale"

          testCase "prepared variable types refresh for referenced DDL but not row writes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE type_source (id INT)"
              let session, _ = handle session "INSERT INTO type_source VALUES (1)"
              let session, _ = handle session "CREATE TABLE unrelated_source (id INT)"
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE schema_types FROM 'SELECT @v FROM type_source'"
              let session, _ = handle session "SET @v=1.75"
              let execute session expected =
                  let session, result = handle session "EXECUTE schema_types"
                  match result with
                  | ResultSet(_, [ _ ]) -> ()
                  | other -> failtestf "expected one row, got %A" other
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ expected ] "retained or refreshed type"
                  session
              let session, _ = handle session "UPDATE type_source SET id=2"
              let session = execute session TypeLongLong
              let session, _ = handle session "ALTER TABLE unrelated_source ADD COLUMN extra INT"
              let session = execute session TypeLongLong
              let session, _ = handle session "ALTER TABLE type_source ADD COLUMN extra INT"
              let session = execute session TypeNewDecimal
              let session, _ = handle session "SET @v=1.5e0"
              let session, _ = handle session "ALTER TABLE type_source ENGINE=InnoDB"
              execute session TypeDouble |> ignore

          testCase "prepared schema dependencies include nested views and temporary tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE schema_base (id INT)"
              let session, _ = handle session "INSERT INTO schema_base VALUES(1)"
              let session, _ = handle session "CREATE VIEW schema_view AS SELECT id FROM schema_base"
              let session, _ = handle session "CREATE VIEW schema_nested AS SELECT id FROM schema_view"
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE view_types FROM 'SELECT @v FROM schema_nested'"
              let session, _ = handle session "SET @v=1.75"
              let session, _ = handle session "ALTER TABLE schema_base ADD COLUMN extra INT"
              let session, _ = handle session "EXECUTE view_types"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "base DDL refreshes a nested view dependency"
              let session, _ = handle session "SET @v=1.5e0"
              let session, _ = handle session "ALTER VIEW schema_view AS SELECT id+1 AS id FROM schema_base"
              let session, _ = handle session "EXECUTE view_types"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeDouble ] "view definition changes refresh types"
              let session, _ = handle session "CREATE TEMPORARY TABLE schema_temp(id INT)"
              let session, _ = handle session "INSERT INTO schema_temp VALUES(1)"
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE temp_types FROM 'SELECT @v FROM schema_temp'"
              let session, _ = handle session "SET @v=1.75"
              let session, _ = handle session "ALTER TABLE schema_temp ADD COLUMN extra INT"
              let session, _ = handle session "EXECUTE temp_types"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "temporary DDL refreshes types"

          testCase "dependency expansion stops at a temporary table shadowing a view"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE shadow_base(id INT)"
              let session, _ = handle session "CREATE VIEW shadow_view AS SELECT id FROM shadow_base"
              let table = Fsdb.Storage.tableSnapshot session.Store "fsdb" "shadow_base" |> Result.defaultWith (failtestf "%A")
              let temporary = Map.ofList [ "fsdb", Map.ofList [ "shadow_view", table ] ]
              let statement = Fsdb.Parser.parse "SELECT @v FROM shadow_view" |> Result.defaultWith (failtestf "%A")
              let dependencies = Fsdb.TableLocks.dependenciesForStatement session.Store temporary "fsdb" statement
              Expect.equal (dependencies |> List.map _.Table) [ "shadow_view" ] "the hidden view is not expanded"

          testCase "prepared SET retains variable types and delays outer assignments"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE assignments FROM 'SET @x=@v'"
              let session, _ = handle session "SET @v=1.75"
              let session, result = handle session "EXECUTE assignments"
              Expect.equal result (Affected 0UL) "SET has no result columns"
              Expect.equal session.UserVariables["x"] (VInt 2L) "the captured integer type rounds the decimal"
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE delayed FROM 'SET @v=1.75,@x=@v'"
              let session, _ = handle session "EXECUTE delayed"
              Expect.equal session.UserVariables["x"] (VInt -2L) "outer assignments are applied after evaluation"
              Expect.equal session.UserVariables["v"] (VDecimal 1.75M) "the assignment keeps its own type"

          testCase "prepared SET NAMES combinations retain direct variable types"
          <| fun _ ->
              for sql in
                  [ "SET NAMES utf8mb4 COLLATE utf8mb4_bin,@x=@v"
                    "SET @x=@v,NAMES utf8mb4 COLLATE utf8mb4_bin" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "SET @v=2"
                  let session, result = handle session ("PREPARE names_assignment FROM '" + sql + "'")
                  Expect.equal result (Affected 0UL) "the combined SET prepares"
                  let session, _ = handle session "SET @v=1.75"
                  let session, result = handle session "EXECUTE names_assignment"
                  Expect.equal result (Affected 0UL) "the combined SET executes"
                  Expect.equal session.UserVariables["x"] (VInt 2L) "the captured integer type is retained"
                  Expect.equal session.Variables["collation_connection"] (Some "utf8mb4_bin") "the NAMES clause is applied"

          testCase "prepared mixed SET retains direct variable types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=2"
              let session, result = handle session "PREPARE mixed FROM 'SET @x=@v, SESSION max_sp_recursion_depth=100'"
              Expect.equal result (Affected 0UL) "mixed SET prepares"
              let session, _ = handle session "SET @v=1.75"
              let session, result = handle session "EXECUTE mixed"
              Expect.equal result (Affected 0UL) "mixed SET executes"
              Expect.equal session.UserVariables["x"] (VInt 2L) "captured BIGINT rounds the current decimal"

          testCase "prepared system SET converts retained integer NULL without changing user assignments"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=2"
              let session, _ = handle session "PREPARE mixed FROM 'SET SESSION max_sp_recursion_depth=@v,@x=@v'"
              let session, _ = handle session "SET @v=NULL"
              let session, result = handle session "EXECUTE mixed"
              Expect.equal result (Affected 0UL) "a retained integer NULL is valid for the numeric system variable"
              Expect.equal session.UserVariables["x"] VNull "the user assignment keeps NULL"
              Expect.equal (handle session "SELECT @@session.max_sp_recursion_depth" |> snd)
                  (ResultSet([ "@@session.max_sp_recursion_depth" ], [ [ Some "0" ] ])) "the system assignment reads integer zero"

          testCase "binary prepared SET binds parameters without advertising result columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2"
              let sql = "SET @x=? -- parameter\n,@y=@v"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              let parameters, columns = preparedMetadata session statement.Ast count
              Expect.hasLength parameters 1 "one bind parameter"
              Expect.isEmpty columns "SET returns no columns"
              let session = { session with Statements = Map.ofList [ 1, statement ] }
              let session, _ = handle session "SET @v=1.75"
              let session, result = executePreparedHandle session 1 [ VNull ]
              Expect.equal result (Affected 0UL) "binary execution returns OK"
              Expect.equal session.UserVariables["y"] (VInt 2L) "the prepare-time type survives"
              let session, result = executePreparedHandle session 1 [ VInt 1L ]
              Expect.equal result (Affected 0UL) "reprepare returns OK"
              Expect.equal session.UserVariables["y"] (VDecimal 1.75M) "binary reprepare refreshes variable types"
              Expect.isEmpty session.LastResultColumnMetadata "no stale result metadata"

          testCase "prepared nullable system assignment preserves literal NULL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "PREPARE nullable_setting FROM 'SET character_set_results=NULL'"
              let session, result = handle session "EXECUTE nullable_setting"
              Expect.equal result (Affected 0UL) "NULL remains a value rather than a bare keyword"
              Expect.equal session.Variables["character_set_results"] None "the nullable setting is cleared"

          testCase "binary SET NAMES with mixed assignments retains types and resolves DEFAULT at execution"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=2"
              let sql = "SET NAMES utf8mb4 COLLATE utf8mb4_bin, @x=?, SESSION max_sp_recursion_depth=DEFAULT, @y=@v"
              let ast, count = prepareStatement sql |> Result.defaultWith (failtestf "%A")
              let statement = createPreparedStatement session sql ast count
              let session = { session with Statements = Map.ofList [ 1, statement ] }
              let session, _ = handle session "SET GLOBAL max_sp_recursion_depth=9"
              let session, _ = handle session "SET @v=1.75"
              let session, result = executePreparedHandle session 1 [ VNull ]
              Expect.equal result (Affected 0UL) "binary mixed assignment succeeds"
              Expect.equal session.Variables["collation_connection"] (Some "utf8mb4_bin") "the charset clause applies"
              Expect.equal session.UserVariables["y"] (VInt 2L) "NULL binding retains the captured type"
              Expect.equal (handle session "SELECT @@session.max_sp_recursion_depth" |> snd)
                  (ResultSet([ "@@session.max_sp_recursion_depth" ], [ [ Some "9" ] ])) "DEFAULT reads the current global value"
              let session, result = executePreparedHandle session 1 [ VInt 1L ]
              Expect.equal result (Affected 0UL) "parameter reprepare succeeds"
              Expect.equal session.UserVariables["y"] (VDecimal 1.75M) "reprepare refreshes the captured type"
              Expect.isEmpty session.LastResultColumnMetadata "SET returns no columns"

          testCase "SET preserves nested assignments when a later expression fails"
          <| fun _ ->
              for prepared in [ false; true ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "SET @side=9"
                  let sql = "SET @x=(@side:=1),@y=(SELECT 1 UNION ALL SELECT 2)"
                  let session, result =
                      if prepared then
                          let session, _ = handle session ("PREPARE failing_set FROM '" + sql + "'")
                          handle session "EXECUTE failing_set"
                      else
                          handle session sql
                  match result with
                  | Err(1242, _) -> ()
                  | other -> failtestf "expected the scalar subquery error, got %A" other
                  Expect.equal session.UserVariables["side"] (VInt 1L) "nested effects survive the later evaluation error"
                  Expect.isFalse (session.UserVariables.ContainsKey "x") "no outer assignment is published"

          testCase "prepared SET parameter reprepare refreshes direct variables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2,@p=NULL"
              let session, _ = handle session "PREPARE assignments FROM 'SET @x=?,@y=@v'"
              let session, _ = handle session "SET @v=1.75"
              let session, _ = handle session "EXECUTE assignments USING @p"
              Expect.equal session.UserVariables["y"] (VInt 2L) "NULL keeps the captured type"
              let session, _ = handle session "SET @p=1"
              let session, _ = handle session "EXECUTE assignments USING @p"
              Expect.equal session.UserVariables["y"] (VDecimal 1.75M) "parameter reprepare captures decimal"
              let session, _ = handle session "SET @v=1.5e0,@p=2.5"
              let session, _ = handle session "EXECUTE assignments USING @p"
              Expect.equal session.UserVariables["y"] (VDouble 1.5) "the next reprepare captures double"

          testCase "direct prepared user variables retain their type while reading current values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE direct_variables FROM 'SELECT @v,ABS(@v)'"
              let steps =
                  [ "-2", Some "-2", Some "2"
                    "1.75", Some "2", Some "2"
                    "2.75e0", Some "2", Some "2"
                    "'1.75'", Some "1", Some "1"
                    "'abc'", Some "0", Some "0"
                    "NULL", None, None ]
              (session, steps)
              ||> List.fold (fun session (value, first, second) ->
                  let session, _ = handle session ("SET @v=" + value)
                  let session, result = handle session "EXECUTE direct_variables"
                  Expect.equal result (ResultSet([ "@v"; "ABS(@v)" ], [ [ first; second ] ])) value
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLongLong; TypeLongLong ] "prepare-time types"
                  session)
              |> ignore

          testCase "prepared decimal variables preserve value scale and declared ABS scale"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=1.25"
              let session, _ = handle session "PREPARE decimal_variable FROM 'SELECT @v,ABS(@v)'"
              let session, _ = handle session "SET @v=-2"
              let _, result = handle session "EXECUTE decimal_variable"
              Expect.equal result
                  (ResultSet([ "@v"; "ABS(@v)" ], [ [ Some "-2"; Some "2.000000000000000000000000000000" ] ]))
                  "the direct read keeps its value scale while ABS uses its derived scale"

          testCase "prepared user-variable reads observe assignments within the same statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE assigned_variable FROM 'SELECT @v:=1.75,@v'"
              let session, result = handle session "EXECUTE assigned_variable"
              match result with
              | ResultSet(_, [ [ Some "1.75"; Some "2" ] ]) -> ()
              | other -> failtestf "expected a live read with the retained integer type, got %A" other
              Expect.equal session.UserVariables["v"] (VDecimal 1.75M) "the stored value retains its actual decimal type"

          testCase "parameter-driven repreparation refreshes direct user-variable types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=-2"
              let session, _ = handle session "PREPARE mixed_variables FROM 'SELECT ?,@v,ABS(@v)'"
              let steps =
                  [ "NULL", "1.25", [ TypeVarString; TypeLongLong; TypeLongLong ]
                    "1", "1.25", [ TypeLongLong; TypeNewDecimal; TypeNewDecimal ]
                    "2", "1.5e0", [ TypeLongLong; TypeNewDecimal; TypeNewDecimal ]
                    "2.5", "1.5e0", [ TypeNewDecimal; TypeDouble; TypeDouble ]
                    "NULL", "'abc'", [ TypeNewDecimal; TypeDouble; TypeDouble ] ]
              (session, steps)
              ||> List.fold (fun session (parameter, value, expected) ->
                  let session, _ = handle session ("SET @p=" + parameter + ",@v=" + value)
                  let session, result = handle session "EXECUTE mixed_variables USING @p"
                  match result with
                  | ResultSet(_, [ _ ]) -> ()
                  | other -> failtestf "expected a row, got %A" other
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) expected "reprepare refreshes variable types"
                  session)
              |> ignore

          testCase "initially NULL prepared user variables retain a binary string type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "PREPARE absent_variable FROM 'SELECT @missing'"
              let session, _ = handle session "SET @missing=1.75"
              let session, result = handle session "EXECUTE absent_variable"
              Expect.equal result (ResultSet([ "@missing" ], [ [ Some "1.75" ] ])) "current value rendered as bytes"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeBlob ] "retained binary type"
              Expect.equal session.UserVariables["missing"] (VDecimal 1.75M) "reading never changes the variable"

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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                            ParameterTypes = None
                            SchemaDependencies = Map.empty }
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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

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
                        ParameterTypes = None
                        SchemaDependencies = Map.empty }

                  let session, result = executePrepared session statement [ VInt 0L ]

                  Expect.equal result (Err(1690, "The length of RANDOM_BYTES must be between 1 and 1024")) "error"
                  Expect.isNone session.Tx "transaction"
              | other -> failtestf "expected a parsed statement, got %A" other ]
