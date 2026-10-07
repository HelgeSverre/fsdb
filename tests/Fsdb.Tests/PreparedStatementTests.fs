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

let private relationNameSession () =
    let mutable session = create 1 (Fsdb.Storage.create ())
    for sql in
        [ "CREATE DATABASE probe"
          "CREATE DATABASE other"
          "USE probe"
          "CREATE TABLE a(id INT)"
          "CREATE TABLE b(id INT)"
          "CREATE TABLE c(id INT)"
          "CREATE TABLE other.a(id INT)"
          "CREATE TABLE other.x(id INT)"
          "INSERT INTO a VALUES(1)"
          "INSERT INTO b VALUES(1)" ] do
        let next, result = handle session sql
        session <- next
        match result with
        | Err(code, message) -> failtestf "%d %s" code message
        | _ -> ()
    session

let tests =
    testList
        "PreparedStatements"
        [ testCase "expression labels retain nested literal quotes and qualifiers"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              handle session "CREATE TABLE a(id INT)" |> ignore
              handle session "INSERT INTO a VALUES(1)" |> ignore
              for sql, label, value in
                  [ "SELECT CONCAT('a', 'b')", "CONCAT('a', 'b')", "ab"
                    "SELECT COALESCE(NULL, 'a')", "COALESCE(NULL, 'a')", "a"
                    "SELECT 'a' = 'b'", "'a' = 'b'", "0"
                    "SELECT 'a'", "a", "a"
                    "SELECT CONCAT('it''s', 'x')", "CONCAT('it''s', 'x')", "it'sx"
                    @"SELECT CONCAT('a\\b', 'x')", @"CONCAT('a\\b', 'x')", @"a\bx"
                    "SELECT a.id + 1 FROM a", "a.id + 1", "2"
                    "SELECT a.id FROM a", "id", "1" ] do
                  Expect.equal (handle session sql |> snd) (ResultSet([ label ], [ [ Some value ] ])) sql
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map _.Name) [ label ] ("prepared label: " + sql)

          testCase "qualified reference diagnostics distinguish isolated and enclosing scopes"
          <| fun _ ->
              let session = relationNameSession ()
              for sql, code, message in
                  [ "SELECT a.id", 1109, "Unknown table 'a' in field list"
                    "SELECT 1 WHERE a.id=1", 1109, "Unknown table 'a' in where clause"
                    "SELECT 1 ORDER BY a.id", 1109, "Unknown table 'a' in order clause"
                    "SELECT 1 GROUP BY a.id", 1109, "Unknown table 'a' in group statement"
                    "SELECT 1 HAVING a.id=1", 1054, "Unknown column 'a.id' in 'having clause'"
                    "SELECT x.id FROM a", 1054, "Unknown column 'x.id' in 'field list'"
                    "SELECT x.id FROM (SELECT a.id AS id) x", 1109, "Unknown table 'a' in field list"
                    "SELECT x.id FROM a JOIN (SELECT a.id AS id) x ON 1", 1109, "Unknown table 'a' in field list"
                    "SELECT x.id FROM a JOIN (SELECT z.id AS id) x ON 1", 1109, "Unknown table 'z' in field list"
                    "SELECT x.id FROM a JOIN LATERAL (SELECT z.id AS id) x ON 1", 1054, "Unknown column 'z.id' in 'field list'"
                    "SELECT x.id FROM a JOIN (SELECT a.id AS id FROM b) x ON 1", 1054, "Unknown column 'a.id' in 'field list'"
                    "SELECT (SELECT z.id) FROM a", 1054, "Unknown column 'z.id' in 'field list'"
                    "SELECT (SELECT a.id)", 1054, "Unknown column 'a.id' in 'field list'"
                    "SELECT * FROM LATERAL (SELECT a.id) d", 1054, "Unknown column 'a.id' in 'field list'"
                    "SELECT * FROM a RIGHT JOIN LATERAL (SELECT a.id) d ON 1", 1054, "Unknown column 'a.id' in 'field list'"
                    "SELECT x.id FROM a JOIN (SELECT id) x ON 1", 1054, "Unknown column 'id' in 'field list'" ] do
                  let result = handle session sql |> snd
                  Expect.equal result (Err(code, message)) sql
                  match errorInfo result with
                  | Some error -> Expect.equal error.State (if code = 1109 then "42S02" else "42S22") "native SQLSTATE"
                  | _ -> failtest "expected an error"
                  match prepareStatementForSession session sql with
                  | Error error -> Expect.equal error (code, message) ("prepare: " + sql)
                  | other -> failtestf "expected binding failure: %A" other
                  Expect.equal (handle session ("PREPARE scope_check FROM '" + sql + "'") |> snd)
                      (Err(code, message)) ("SQL prepare: " + sql)

          testCase "JSON_TABLE preparation validates argument references in lateral scope"
          <| fun _ ->
              let session = relationNameSession ()
              for sql, code, message in
                  [ "SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(a.missing),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", 1054, "Unknown column 'a.missing' in 'a table function argument'"
                    "SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(b.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1 JOIN b ON 1", 1054, "Unknown column 'b.id' in 'a table function argument'"
                    "SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(x.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", 1054, "Unknown column 'x.id' in 'a table function argument'"
                    "SELECT * FROM a RIGHT JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", 1109, "Unknown table 'a' in a table function argument"
                    "SELECT * FROM JSON_TABLE(JSON_ARRAY(b.id),'$[*]' COLUMNS(v INT PATH '$')) j JOIN b ON 1", 1109, "Unknown table 'b' in a table function argument"
                    "SELECT * FROM a JOIN b ON 1 JOIN JSON_TABLE(JSON_ARRAY(id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", 1052, "Column 'id' in a table function argument is ambiguous" ] do
                  match prepareStatementForSession session sql with
                  | Error error -> Expect.equal error (code, message) sql
                  | other -> failtestf "expected binary preparation error: %A" other
                  let quoted = sql.Replace("'", "''")
                  Expect.equal (handle session ("PREPARE json_source FROM '" + quoted + "'") |> snd) (Err(code, message)) "SQL preparation"
                  Expect.equal (handle session sql |> snd) (Err(code, message)) "execution"
              let valid = "SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1"
              match prepareStatementForSession session valid with
              | Ok _ -> ()
              | Error error -> failtestf "valid preceding reference: %A" error

          testCase "right lateral preparation excludes only the left operand"
          <| fun _ ->
              let session = relationNameSession ()
              for sql, expected in
                  [ "SELECT * FROM a RIGHT JOIN LATERAL (SELECT a.id AS id) d ON 1", Some "a.id"
                    "SELECT a.id,d.v FROM a JOIN (b RIGHT JOIN LATERAL (SELECT b.id AS v) d ON 1) ON 1", Some "b.id"
                    "SELECT a.id,d.v FROM a JOIN (b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) ON 1", None
                    "SELECT a.id,(SELECT d.v FROM b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) FROM a", None ] do
                  match prepareStatementForSession session sql, expected with
                  | Error(1054, message), Some name ->
                      Expect.equal message ("Unknown column '" + name + "' in 'field list'") sql
                  | Ok _, None -> ()
                  | actual, _ -> failtestf "unexpected preparation for %s: %A" sql actual
                  let _, result = handle session ("PREPARE right_lateral FROM '" + sql + "'")
                  match result, expected with
                  | Err(1054, message), Some name ->
                      Expect.equal message ("Unknown column '" + name + "' in 'field list'") sql
                  | Affected 0UL, None -> ()
                  | actual, _ -> failtestf "unexpected SQL PREPARE for %s: %A" sql actual

          testCase "prepared direct columns retain native source flags through aliases"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE t(id INT PRIMARY KEY AUTO_INCREMENT,u INT NOT NULL UNIQUE,k INT NOT NULL,KEY(k))"; "CREATE TABLE a(id INT PRIMARY KEY)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              let mask = NotNullFlag ||| PrimaryKeyFlag ||| UniqueKeyFlag ||| MultipleKeyFlag ||| AutoIncrementFlag ||| NoDefaultValueFlag ||| PartKeyFlag
              for sql, expected in
                  [ "SELECT id,u,k FROM t", [ 16899us; 20485us; 20489us ]
                    "SELECT id AS renamed,u AS un,k AS kn FROM t", [ 16899us; 20485us; 20489us ]
                    "SELECT a.id,t.id,t.u,t.k FROM a LEFT JOIN t ON a.id=t.id", [ 20483us; 16898us; 20484us; 20488us ]
                    "SELECT id,u,k FROM (SELECT id,u,k FROM t) d", [ 16899us; 20485us; 20489us ]
                    "WITH d AS (SELECT id,u,k FROM t) SELECT id,u,k FROM d", [ 16899us; 20485us; 20489us ]
                    "SELECT d.id FROM (SELECT id FROM a LIMIT 1) x JOIN LATERAL (SELECT x.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM (SELECT id FROM a LIMIT 1) x LEFT JOIN LATERAL (SELECT x.id) d ON 1", [ 4096us ]
                    "SELECT d.id FROM (SELECT id FROM a) x JOIN LATERAL (SELECT x.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT a.id AS id) x JOIN LATERAL (SELECT x.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT a.id AS id LIMIT 1) x JOIN LATERAL (SELECT x.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT a.id AS id GROUP BY a.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT DISTINCT a.id AS id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM t JOIN LATERAL (SELECT t.id) d ON 1", [ 1us ]
                    "SELECT d.id FROM t LEFT JOIN LATERAL (SELECT t.id) d ON 1", [ 0us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT a.id) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a LEFT JOIN LATERAL (SELECT a.id) d ON 1", [ 4096us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT a.id LIMIT 1) d ON 1", [ 4097us ]
                    "SELECT d.id FROM a LEFT JOIN LATERAL (SELECT a.id LIMIT 1) d ON 1", [ 4096us ]
                    "SELECT d.id FROM a JOIN (t JOIN LATERAL (SELECT a.id) d ON 1) ON 1", [ 4097us ]
                    "SELECT d.id FROM a LEFT JOIN (t JOIN LATERAL (SELECT a.id) d ON 1) ON 1", [ 4096us ]
                    "SELECT d.id FROM a JOIN LATERAL (SELECT t.id FROM t WHERE t.id=a.id) d ON 1", [ 16899us ]
                    "SELECT d.id FROM a LEFT JOIN LATERAL (SELECT t.id FROM t WHERE t.id=a.id) d ON 1", [ 16898us ]
                    "SELECT id FROM (SELECT id FROM t) d", [ 16899us ]
                    "SELECT id FROM (SELECT DISTINCT id FROM t) d", [ 1us ]
                    "SELECT id FROM (SELECT id FROM t GROUP BY id) d", [ 1us ]
                    "SELECT id FROM (SELECT id FROM t HAVING id>0) d", [ 1us ]
                    "SELECT id FROM (SELECT id,ROW_NUMBER() OVER() AS rn FROM t) d", [ 1us ]
                    "SELECT id FROM (SELECT id,(SELECT 1) AS n FROM t) d", [ 16899us ]
                    "SELECT id FROM (SELECT id,(SELECT MAX(id) FROM a) AS n FROM t) d", [ 16899us ]
                    "SELECT id FROM (SELECT id FROM t LIMIT 1) d", [ 1us ]
                    "WITH d AS (SELECT id,(SELECT 1) AS n FROM t) SELECT id FROM d", [ 16899us ]
                    "WITH d AS (SELECT id FROM t LIMIT 1) SELECT id FROM d", [ 1us ] ] do
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> column.Metadata.Flags &&& mask)) expected
                      ("source flags match native COM_STMT_PREPARE: " + sql)
                  Expect.all (columns |> List.map (fun column -> column.Metadata.TypeId)) (fun ty -> ty = TypeLong)
                      ("declared INT type survives projection: " + sql)
                  let executed, result = handle session sql
                  match result with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
                  Expect.equal (executed.LastResultColumnMetadata |> List.map (fun column -> column.Flags &&& mask)) expected
                      ("execution retains the native source flags: " + sql)
              let rolledUp, _ = handle session "SELECT id,COUNT(*) FROM t GROUP BY id WITH ROLLUP"
              let sourceMask = mask &&& ~~~NotNullFlag
              Expect.equal (rolledUp.LastResultColumnMetadata |> List.map (fun column -> column.Flags &&& sourceMask)) [ 0us; 0us ]
                  "ROLLUP temporary results do not inherit physical source flags"

          testCase "outer join metadata clears nullability on optional grouped sources"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE a(id INT PRIMARY KEY)"; "CREATE TABLE b(id INT PRIMARY KEY)"; "CREATE TABLE c(id INT PRIMARY KEY)"; "INSERT INTO a VALUES(1)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              for sql, expected in
                  [ "SELECT a.id,b.id,c.id FROM a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id", [ true; false; false ]
                    "SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id", [ true; false ]
                    "SELECT * FROM a RIGHT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id", [ false; true ]
                    "SELECT * FROM b RIGHT JOIN c USING(id)", [ true ] ] do
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  let notNull metadata = metadata |> List.map (hasMetadataFlag NotNullFlag)
                  Expect.equal (notNull (columns |> List.map _.Metadata)) expected "prepared optional columns are nullable"
                  let executed, result = handle session sql
                  match result with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
                  Expect.equal (notNull executed.LastResultColumnMetadata) expected "execution preserves the same nullable boundaries"
                  Expect.all executed.LastResultColumnMetadata (hasMetadataFlag PrimaryKeyFlag) "nullable source columns retain primary-key origin flags"

          testCase "grouped USING prepared metadata matches execution"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in
                  [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)"
                    "INSERT INTO a VALUES(1),(2)"; "INSERT INTO b VALUES(1),(3)"; "INSERT INTO c VALUES(3)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              let sql = "SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id ORDER BY a.id"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal columns.Length 2 "prepare exposes logical columns"
              Expect.equal (columns |> List.map (fun column -> column.Metadata.TypeId)) [ TypeLong; TypeLong ]
                  "merged integer columns retain their declared family"
              let executed, result = executePrepared session statement []
              Expect.equal result (ResultSet([ "id"; "id" ], [ [ Some "1"; Some "1" ]; [ Some "2"; None ] ]))
                  "prepared execution preserves merged ownership and NULL extension"
              Expect.equal executed.LastResultColumnMetadata.Length columns.Length "prepare and execute agree on arity"

          testCase "grouped USING metadata retains the preserved column origin"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in
                  [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)"
                    "INSERT INTO a VALUES(1),(2)"; "INSERT INTO b VALUES(1),(3)"; "INSERT INTO c VALUES(3)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              for kind, owner in [ "LEFT JOIN", "b"; "RIGHT JOIN", "c"; "JOIN", "b" ] do
                  let sql = "SELECT * FROM a LEFT JOIN (b " + kind + " c USING(id)) ON a.id=b.id"
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let statement = createPreparedStatement session sql ast count
                  let _, columns = preparedMetadata session ast count
                  let owners metadata = metadata |> List.map (fun column -> column.Origin |> Option.map _.OriginalTable)
                  Expect.equal (owners (columns |> List.map _.Metadata)) [ Some "a"; Some owner ]
                      "PREPARE reports the preserved source column"
                  let executed, result = executePrepared session statement []
                  match result with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
                  Expect.equal (owners executed.LastResultColumnMetadata) [ Some "a"; Some owner ]
                      "execution reports the same origins"
              let ast, count = prepareStatementForSession session "SELECT COALESCE(b.id,c.id) FROM b LEFT JOIN c USING(id)" |> Result.defaultWith (fun error -> failtestf "%A" error)
              let _, columns = preparedMetadata session ast count
              Expect.equal (columns |> List.map (fun column -> column.Metadata.Origin)) [ None ]
                  "an explicit COALESCE remains a computed expression"

          testCase "derived and CTE merged columns retain physical origins"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)"; "INSERT INTO b VALUES(1)"; "INSERT INTO c VALUES(3)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              for sql, name in
                  [ "SELECT * FROM (SELECT * FROM b RIGHT JOIN c USING(id)) merged", "id"
                    "WITH merged AS (SELECT * FROM b RIGHT JOIN c USING(id)) SELECT * FROM merged", "id"
                    "SELECT x FROM (SELECT id AS x FROM b RIGHT JOIN c USING(id)) merged", "x"
                    "WITH merged(x) AS (SELECT * FROM b RIGHT JOIN c USING(id)) SELECT x FROM merged", "x" ] do
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let statement = createPreparedStatement session sql ast count
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> column.Metadata.Origin |> Option.map _.OriginalTable)) [ Some "c" ]
                      "PREPARE retains the physical owner through the relation boundary"
                  let executed, result = executePrepared session statement []
                  Expect.equal result (ResultSet([ name ], [ [ Some "3" ] ])) "the preserved row survives materialization"
                  Expect.equal (executed.LastResultColumnMetadata |> List.map (fun column -> column.Origin |> Option.map _.OriginalTable)) [ Some "c" ]
                      "execution retains the same physical owner"

          testCase "PREPARE rejects invalid USING through query boundaries"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)"; "CREATE TABLE target(id INT)" ] do
                  Expect.equal (handle session sql |> snd) (Affected 0UL) "create sources"
              for sql, code in
                  [ "SELECT * FROM a LEFT JOIN (b JOIN (SELECT 1 AS other) c USING(id)) ON a.id=b.id", 1054
                    "SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052
                    "SELECT * FROM (SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)) d", 1052
                    "WITH d AS (SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)) SELECT * FROM d", 1052
                    "SELECT (SELECT COUNT(*) FROM a JOIN (b JOIN c ON b.id=c.id) USING(id))", 1052
                    "SELECT 1 WHERE EXISTS(SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) USING(id))", 1052
                    "SELECT id FROM a UNION ALL SELECT id FROM b JOIN c USING(missing)", 1054
                    "UPDATE a JOIN (b JOIN c ON b.id=c.id) USING(id) SET a.id=7", 1052
                    "DELETE a FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052
                    "INSERT INTO target SELECT a.id FROM a JOIN (b JOIN c ON b.id=c.id) USING(id)", 1052 ] do
                  match prepareStatementForSession session sql with
                  | Error(actual, _) -> Expect.equal actual code "binary preparation uses the native binding error"
                  | other -> failtestf "expected PREPARE rejection for %s: %A" sql other
                  let prepared, result = handle session ("PREPARE invalid_join FROM '" + sql + "'")
                  match result with
                  | Err(actual, _) -> Expect.equal actual code "SQL PREPARE uses the same validation"
                  | other -> failtestf "expected SQL PREPARE rejection for %s: %A" sql other
                  Expect.isFalse (Map.containsKey "invalid_join" prepared.TextStatements) "a rejected statement has no handle"

          testCase "PREPARE validates ON names within operand and subquery scopes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)" ] do
                  Expect.equal (handle session sql |> snd) (Affected 0UL) "create sources"
              for sql, expected in
                  [ "SELECT a.id FROM a JOIN (b JOIN c ON EXISTS(SELECT a.id)) ON 1", (1054, "Unknown column 'a.id' in 'field list'")
                    "WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT 1 UNION ALL SELECT id FROM d", (1054, "Unknown column 'b.missing' in 'on clause'")
                    "SELECT a.id FROM a JOIN b ON b.missing=a.id", (1054, "Unknown column 'b.missing' in 'on clause'")
                    "SELECT a.id FROM a JOIN b ON missing=a.id", (1054, "Unknown column 'missing' in 'on clause'")
                    "SELECT a.id FROM a JOIN b ON id=a.id", (1052, "Column 'id' in on clause is ambiguous")
                    "SELECT a.id FROM a JOIN b ON c.id=a.id JOIN c ON 1", (1054, "Unknown column 'c.id' in 'on clause'")
                    "SELECT a.id FROM a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id", (1054, "Unknown column 'a.id' in 'on clause'")
                    "SELECT a.id FROM a JOIN (b RIGHT JOIN c USING(id)) ON id=a.id", (1052, "Column 'id' in on clause is ambiguous")
                    "SELECT a.id FROM a JOIN (b JOIN c ON EXISTS(SELECT 1 WHERE a.id=c.id)) ON 1", (1054, "Unknown column 'a.id' in 'where clause'")
                    "SELECT a.id FROM a JOIN b ON EXISTS(SELECT 1 WHERE c.id=a.id) JOIN c ON 1", (1054, "Unknown column 'c.id' in 'where clause'")
                    "SELECT a.id AS chosen FROM a JOIN b ON chosen=b.id", (1054, "Unknown column 'chosen' in 'on clause'")
                    "SELECT a.id FROM a JOIN (SELECT b.id FROM b JOIN c ON c.id=a.id) d ON 1", (1054, "Unknown column 'a.id' in 'on clause'")
                    "WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT * FROM d", (1054, "Unknown column 'b.missing' in 'on clause'")
                    "UPDATE a JOIN (b JOIN c ON c.id=a.id) ON 1 SET a.id=7", (1054, "Unknown column 'a.id' in 'on clause'")
                    "DELETE a FROM a JOIN (b JOIN c ON c.id=a.id) ON 1", (1054, "Unknown column 'a.id' in 'on clause'")
                    "SELECT a.id FROM a JOIN (b JOIN c ON b.missing=c.id) ON 0", (1054, "Unknown column 'b.missing' in 'on clause'") ] do
                  match prepareStatementForSession session sql with
                  | Error error -> Expect.equal error expected "binary preparation retains the native code and clause"
                  | other -> failtestf "expected binding rejection for %s: %A" sql other
                  let prepared, result = handle session ("PREPARE scoped_join FROM '" + sql + "'")
                  match result with
                  | Err(code, message) -> Expect.equal (code, message) expected "SQL PREPARE uses the same scopes"
                  | other -> failtestf "expected SQL PREPARE binding rejection for %s: %A" sql other
                  Expect.isFalse (Map.containsKey "scoped_join" prepared.TextStatements) "a rejected statement has no handle"

          testCase "PREPARE retains valid outer lateral and CTE bindings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)" ] do
                  Expect.equal (handle session sql |> snd) (Affected 0UL) "create sources"
              for sql in
                  [ "WITH bad AS (SELECT * FROM a JOIN b USING(missing)) SELECT (WITH unused AS (SELECT * FROM bad) SELECT 1)"
                    "WITH d AS (SELECT * FROM a JOIN b USING(missing)) SELECT (WITH d AS (SELECT 1 AS id) SELECT id FROM d)"
                    "WITH d AS (SELECT * FROM a JOIN b USING(missing)) SELECT 1"
                    "SELECT a.id AS chosen,(SELECT COUNT(*) FROM b JOIN c ON c.id=chosen) FROM a"
                    "WITH bad AS (SELECT * FROM a JOIN b USING(missing)),good AS (SELECT * FROM c) SELECT * FROM good"
                    "WITH d AS (SELECT a.id FROM a JOIN b ON a.id=b.id) SELECT 1 UNION ALL SELECT id FROM d"
                    "SELECT c.id FROM (SELECT 3 AS wanted) d JOIN (a RIGHT JOIN c USING(id)) ON id=d.wanted"
                    "SELECT b.id FROM a RIGHT JOIN b USING(id) JOIN (SELECT 3 AS wanted) d ON id=d.wanted"
                    "SELECT a.id,(SELECT COUNT(*) FROM b JOIN (c JOIN (SELECT 1 AS seed) d ON c.id=a.id+1) ON 1) FROM a"
                    "SELECT a.id,(SELECT COUNT(*) FROM (SELECT 1 AS other) b JOIN (SELECT 1 AS seed) c ON id=1) FROM a"
                    "SELECT a.id,(SELECT COUNT(*) FROM (SELECT 1 AS other) a JOIN b ON a.id=b.id) FROM a"
                    "SELECT a.id,(SELECT COUNT(*) FROM (SELECT b.id FROM b JOIN c ON c.id=a.id) d) FROM a"
                    "SELECT a.id FROM a JOIN LATERAL (SELECT b.id FROM b JOIN c ON c.id=a.id) d ON 1"
                    "SELECT a.id FROM a JOIN (b JOIN LATERAL (SELECT c.id FROM c JOIN (SELECT 1 AS seed) d ON c.id=a.id) x ON 1) ON 1"
                    "WITH d AS (SELECT a.id FROM a JOIN b ON b.missing=a.id) SELECT 1" ] do
                  match prepareStatementForSession session sql with
                  | Ok _ -> ()
                  | Error error -> failtestf "native accepts %s but preparation failed: %A" sql error
                  let prepared, result = handle session ("PREPARE scoped_join FROM '" + sql + "'")
                  Expect.equal result (Affected 0UL) "SQL PREPARE accepts the same scope"
                  Expect.isTrue (Map.containsKey "scoped_join" prepared.TextStatements) "the statement is available for execution"

          testCase "relation name errors agree at preparation and execution"
          <| fun _ ->
              let session = relationNameSession ()
              for sql, (code, state, message) in
                  [ "WITH d AS (SELECT 1 AS id) SELECT * FROM d x JOIN d x ON 1", (1066, "42000", "Not unique table/alias: 'x'")
                    "SELECT * FROM a JOIN a ON 1", (1066, "42000", "Not unique table/alias: 'a'")
                    "SELECT a.id FROM a JOIN (a JOIN c ON a.id=c.id) ON 1", (1066, "42000", "Not unique table/alias: 'a'")
                    "SELECT * FROM a x JOIN (b x JOIN c y ON 1) ON 1", (1066, "42000", "Not unique table/alias: 'x'")
                    "SELECT * FROM a x JOIN b X ON 1", (1066, "42000", "Not unique table/alias: 'X'")
                    "SELECT * FROM (SELECT 1 AS id) d JOIN (SELECT 2 AS id) d ON 1", (1066, "42000", "Not unique table/alias: 'd'")
                    "SELECT * FROM absent x JOIN absent x ON 1", (1066, "42000", "Not unique table/alias: 'x'")
                    "SELECT * FROM (SELECT 1 AS id,2 AS id) d", (1060, "42S21", "Duplicate column name 'id'")
                    "SELECT * FROM (SELECT a.id,b.id FROM a JOIN b ON 1) d", (1060, "42S21", "Duplicate column name 'id'")
                    "SELECT * FROM (SELECT 1,1) d", (1060, "42S21", "Duplicate column name '1'")
                    "SELECT * FROM (SELECT a.id,a.id+0 AS id FROM a) d", (1060, "42S21", "Duplicate column name 'id'")
                    "SELECT * FROM (SELECT 1 AS id,2 AS ID) d", (1060, "42S21", "Duplicate column name 'ID'")
                    "SELECT * FROM a LEFT JOIN (SELECT 1 AS id,2 AS id) d ON 0", (1060, "42S21", "Duplicate column name 'id'")
                    "WITH d AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d", (1060, "42S21", "Duplicate column name 'id'")
                    "WITH d(x,x) AS (SELECT 1,2) SELECT * FROM d", (1060, "42S21", "Duplicate column name 'x'")
                    "WITH RECURSIVE d AS (SELECT 1 AS n,2 AS n UNION ALL SELECT n+1,n+2 FROM d WHERE n<2) SELECT * FROM d", (1060, "42S21", "Duplicate column name 'n'")
                    "WITH d AS (SELECT 1),d AS (SELECT 2) SELECT 1", (1066, "42000", "Not unique table/alias: 'd'")
                    "UPDATE a x JOIN b x ON 1 SET x.id=2", (1066, "42000", "Not unique table/alias: 'x'")
                    "DELETE x FROM a x JOIN b x ON 1", (1066, "42000", "Not unique table/alias: 'x'") ] do
                  match prepareStatementForSession session sql with
                  | Error error -> Expect.equal error (code, message) "binary PREPARE reports the relation diagnostic"
                  | other -> failtestf "expected invalid relation rejection for %s: %A" sql other
                  for command in [ sql; "PREPARE invalid_relation FROM '" + sql + "'" ] do
                      let _, result = handle session command
                      match errorInfo result with
                      | Some error -> Expect.equal (error.Code, error.State, error.Message) (code, state, message) "native code, SQLSTATE, and name"
                      | None -> failtestf "expected relation-name error for %s: %A" command result

          testCase "relation names retain database query and CTE column namespaces"
          <| fun _ ->
              let session = relationNameSession ()
              for sql in
                  [ "SELECT * FROM a x JOIN other.a x ON 1"
                    "SELECT * FROM a a JOIN (SELECT 1 AS id) a ON 1"
                    "WITH a AS (SELECT 1 AS id) SELECT * FROM a JOIN probe.a ON 1"
                    "SELECT * FROM probe.a JOIN other.a ON 1"
                    "SELECT * FROM a x JOIN other.x ON 1"
                    "SELECT * FROM a JOIN (SELECT 1 AS id) a ON 1"
                    "SELECT a.id,(SELECT a.id FROM a WHERE a.id=1) FROM a"
                    "WITH d(x,y) AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d"
                    "WITH d(x,x) AS (SELECT 1,2) SELECT 1"
                    "WITH RECURSIVE d(x,y) AS (SELECT 1 AS n,2 AS n UNION ALL SELECT x+1,y+1 FROM d WHERE x<2) SELECT * FROM d" ] do
                  match prepareStatementForSession session sql with
                  | Ok _ -> ()
                  | Error error -> failtestf "native accepts %s: %A" sql error
                  match handle session sql |> snd with
                  | ResultSet _ -> ()
                  | other -> failtestf "expected valid relation rows for %s: %A" sql other
              Expect.equal (handle session "WITH d(x,y) AS (SELECT a.id,b.id FROM a JOIN b ON 1) SELECT * FROM d" |> snd)
                  (ResultSet([ "x"; "y" ], [ [ Some "1"; Some "1" ] ])) "explicit CTE names replace duplicate projection names"
              Expect.equal (handle session "WITH RECURSIVE d(x,y) AS (SELECT 1 AS n,2 AS n UNION ALL SELECT x+1,y+1 FROM d WHERE x<2) SELECT * FROM d" |> snd)
                  (ResultSet([ "x"; "y" ], [ [ Some "1"; Some "2" ]; [ Some "2"; Some "3" ] ])) "recursive members use the renamed anchor columns"

          testCase "duplicate derived columns are rejected before application expressions run"
          <| fun _ ->
              let session = relationNameSession ()
              let mutable calls = 0
              let functions =
                  session.CustomFunctions
                  |> Fsdb.Functions.registerScalar "RELATION_TOUCH" (fun _ ->
                      calls <- calls + 1
                      VInt 1L)
              let session = { session with CustomFunctions = functions }
              match handle session "SELECT * FROM (SELECT RELATION_TOUCH() AS id,1 AS id) d" |> snd with
              | Err(1060, _) -> ()
              | other -> failtestf "expected duplicate derived columns: %A" other
              Expect.equal calls 0 "the invalid relation is rejected from schema metadata"

          testCase "join preparation resolves schema without evaluating expressions or writing"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql in [ "CREATE TABLE a(id INT)"; "CREATE TABLE b(id INT)"; "CREATE TABLE c(id INT)"
                           "INSERT INTO a VALUES(1)"; "INSERT INTO b VALUES(1)"; "INSERT INTO c VALUES(1)" ] do
                  match handle session sql |> snd with
                  | Err(code, message) -> failtestf "%d %s" code message
                  | _ -> ()
              let mutable calls = 0
              let functions =
                  session.CustomFunctions
                  |> Fsdb.Functions.registerScalar "PREPARE_TOUCH" (fun _ ->
                      calls <- calls + 1
                      VInt 1L)
              let session = { session with CustomFunctions = functions }
              for sql in
                  [ "SELECT PREPARE_TOUCH(a.id),? FROM a JOIN (b JOIN c USING(id)) USING(id) WHERE PREPARE_TOUCH(a.id)"
                    "SELECT * FROM a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id WHERE a.id=?"
                    "UPDATE a JOIN (b JOIN c USING(id)) USING(id) SET a.id=PREPARE_TOUCH(a.id)+?"
                    "DELETE a FROM a JOIN (b JOIN c USING(id)) USING(id) WHERE PREPARE_TOUCH(a.id)" ] do
                  match prepareStatementForSession session sql with
                  | Ok _ -> ()
                  | Error error -> failtestf "valid join preparation failed: %A" error
              Expect.equal calls 0 "schema-only validation does not invoke application functions"
              Expect.equal (handle session "SELECT id FROM a" |> snd) (ResultSet([ "id" ], [ [ Some "1" ] ]))
                  "preparing mutations leaves stored rows unchanged"

          testCase "placeholderPositions counts only ? outside strings, comments, and backtick identifiers"
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
              | Result.Ok(Select { Projections = [ { Expression = Lit(VString roundtripped); Alias = _ } ] }) ->
                  Expect.equal roundtripped original "CR/LF survive the literal round-trip"
              | other -> failtestf "expected a parsed SELECT literal, got %A" other

          testCase "valueToSqlLiteral renders NULL for VNull and a plain digit string for VInt"
          <| fun _ ->
              Expect.equal (valueToSqlLiteral VNull) "NULL" "null literal"
              Expect.equal (valueToSqlLiteral (VInt 42L)) "42" "int literal"

          testCase "valueToSqlLiteral renders raw bytes as a hexadecimal literal"
          <| fun _ ->
              let literal = valueToSqlLiteral (VBytes [| 0x00uy; 0xffuy; 0x80uy |])
              Expect.equal literal "_binary X'00FF80'" "lossless binary literal"

              match Fsdb.Parser.parse ("SELECT " + literal) with
              | Result.Ok(Select { Projections = [ { Expression = Lit(VBytes bytes); Alias = _ } ] }) ->
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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

                  match executePrepared session statement [ VString "name" ] |> snd with
                  | ResultSet(_, [ [ Some "name"; Some "varchar(255)"; Some "NO"; Some ""; None; Some "" ] ]) -> ()
                  | other -> failtestf "expected only the bound metadata field, got %A" other
              | other -> failtestf "expected a text-probed prepared SHOW COLUMNS, got %A" other

          testCase "prepared projection names survive parameter binding and repeated execution"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for sql, names in
                  [ "SELECT ?, ABS(?), ? + 1", [ "?"; "ABS(?)"; "? + 1" ]
                    "SELECT CONCAT(?, 'b')", [ "CONCAT(?, 'b')" ]
                    "SELECT d.`?` FROM (SELECT ?) d", [ "?" ]
                    "WITH c AS (SELECT ?) SELECT * FROM c", [ "?" ]
                    "SELECT ? UNION ALL SELECT ?", [ "?" ]
                    "SELECT ? AS chosen", [ "chosen" ] ] do
                  let ast, count =
                      match prepareStatement sql with
                      | Ok prepared -> prepared
                      | Error error -> failtestf "prepare failed: %A" error
                  let statement = { Ast = ast; Sql = sql; ParamCount = count; LastParamTypes = None; ParameterTypes = None; SchemaDependencies = Map.empty; DivisionPrecisionIncrement = 4 }
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
                    VDateTime(DateTime(2024, 1, 3, 12, 30, 0)), TypeDateTime, Some "2024-01-03 12:30:00.000000"
                    VDate(DateOnly(2024, 1, 4)), TypeDateTime, Some "2024-01-04 00:00:00.000000"
                    VInt 20240105L, TypeDateTime, Some "2024-01-05 00:00:00.000000"
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

          testCase "division precision follows session settings but prepared statements retain it until reprepare"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE division_source(n INT)"
              let session, _ = handle session "INSERT INTO division_source VALUES (1)"
              let session, _ = handle session "PREPARE division_setting FROM 'SELECT n/3 AS quotient FROM division_source'"
              let session, assigned = handle session "SET div_precision_increment=1"
              Expect.equal assigned (Affected 0UL) "the session precision is configurable"
              let session, result = handle session "SELECT 1/3 AS quotient,(1/3)*3 AS product"
              Expect.equal result (ResultSet([ "quotient"; "product" ], [ [ Some "0.3"; Some "1.0" ] ]))
                  "ordinary expressions use the current increment"
              let session, retained = handle session "EXECUTE division_setting"
              Expect.equal retained (ResultSet([ "quotient" ], [ [ Some "0.3333" ] ])) "prepared expressions retain four"
              let session, _ = handle session "ALTER TABLE division_source ADD COLUMN extra INT"
              let session, refreshed = handle session "EXECUTE division_setting"
              Expect.equal refreshed (ResultSet([ "quotient" ], [ [ Some "0.3" ] ])) "schema reprepare captures one"
              let session, _ = handle session "SET div_precision_increment=0"
              let _, average = handle session "SELECT AVG(n) AS average FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 2) t"
              Expect.equal average (ResultSet([ "average" ], [ [ Some "1" ] ])) "zero increment truncates the integral quotient"

          testCase "binary prepared division retains its increment without hiding the session variable"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET div_precision_increment=1"
              let sql = "SELECT 1/3 AS quotient,@@div_precision_increment AS setting"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal columns.Head.Metadata.Decimals 1uy "prepare metadata uses the captured scale"
              let session, _ = handle session "SET div_precision_increment=9"
              let session, result = executePrepared session statement []
              Expect.equal result (ResultSet([ "quotient"; "setting" ], [ [ Some "0.3"; Some "9" ] ]))
                  "only expression precision is retained"
              Expect.equal session.LastResultColumnMetadata.Head.Decimals 1uy "execute metadata retains the scale"
              let _, ordinary = handle session "SELECT (1/3)*3 AS product"
              Expect.equal ordinary (ResultSet([ "product" ], [ [ Some "0.999999999" ] ])) "the override is scoped to execution"

          testCase "parameter repreparation refreshes the retained division increment"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ? AS parameter_value,1/3 AS quotient"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let session = { session with Statements = Map.add 1 statement session.Statements }
              let session, _ = executePreparedHandle session 1 [ VInt 1L ]
              let session, _ = handle session "SET div_precision_increment=1"
              let session, retained = executePreparedHandle session 1 [ VInt 2L ]
              Expect.equal retained (ResultSet([ "parameter_value"; "quotient" ], [ [ Some "2"; Some "0.3333" ] ]))
                  "a compatible parameter retains the increment"
              let _, refreshed = executePreparedHandle session 1 [ VDecimal 1.25M ]
              Expect.equal refreshed (ResultSet([ "parameter_value"; "quotient" ], [ [ Some "1.25"; Some "0.3" ] ]))
                  "a changed parameter type captures the current increment"

          testCase "window averages materialize their scale before outer arithmetic"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET div_precision_increment=1"
              let session, ordinary = handle session "SELECT AVG(n)*3 AS product FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 2) t"
              Expect.equal ordinary (ResultSet([ "product" ], [ [ Some "5.0" ] ])) "ordinary AVG retains guard digits"
              let _, windowed = handle session "SELECT AVG(n) OVER () AS average,(AVG(n) OVER ())*3 AS product FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 2) t"
              Expect.equal windowed (ResultSet([ "average"; "product" ], List.replicate 3 [ Some "1.7"; Some "5.1" ]))
                  "window AVG materializes before its consumer"

          testCase "division increment clamps with diagnostics and inherits global defaults"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              for value, expected in [ "-1", "0"; "31", "30" ] do
                  let changed, result = handle session ("SET div_precision_increment=" + value)
                  Expect.equal result (Affected 0UL) "out-of-range integer assignments clamp"
                  Expect.equal (changed.Diagnostics |> List.map _.Code) [ 1292 ] "clamping records a warning"
                  let _, actual = handle changed "SELECT @@div_precision_increment AS setting"
                  Expect.equal actual (ResultSet([ "setting" ], [ [ Some expected ] ])) "the endpoint is stored"
              for value in [ "1.5"; "'2'"; "NULL" ] do
                  match handle session ("SET div_precision_increment=" + value) |> snd with
                  | Err(1232, _) -> ()
                  | other -> failtestf "expected integer-only assignment error for %s; got %A" value other
              let session, _ = handle session "SET GLOBAL div_precision_increment=1"
              let _, retained = handle session "SELECT 1/3 AS quotient"
              Expect.equal retained (ResultSet([ "quotient" ], [ [ Some "0.3333" ] ])) "global assignment preserves existing sessions"
              let inherited = create 2 store
              let _, result = handle inherited "SELECT AVG(n) AS average,AVG(DISTINCT n) AS distinct_average FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 2) t"
              Expect.equal result (ResultSet([ "average"; "distinct_average" ], [ [ Some "1.7"; Some "1.5" ] ]))
                  "ordinary and DISTINCT averages inherit the increment"
              let session, _ = handle session "SET div_precision_increment=DEFAULT"
              let _, result = handle session "SELECT 1/3 AS quotient"
              Expect.equal result (ResultSet([ "quotient" ], [ [ Some "0.3" ] ])) "session DEFAULT uses the global value"
              let session, _ = handle session "SET GLOBAL div_precision_increment=DEFAULT"
              let _, result = handle session "SELECT @@global.div_precision_increment AS setting"
              Expect.equal result (ResultSet([ "setting" ], [ [ Some "4" ] ])) "global DEFAULT restores four"

          testCase "binary literal aggregates derive precision from byte width"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT SUM(b''),SUM(b'100000001'),AVG(b'100000001'),SUM(X'010001')"
              let session, _ = handle session sql
              let shape metadata = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
              let expected = [ TypeNewDecimal,24u,0uy; TypeNewDecimal,28u,0uy; TypeNewDecimal,11u,4uy; TypeNewDecimal,31u,0uy ]
              Expect.equal (session.LastResultColumnMetadata |> List.map shape) expected "execution byte widths"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal (columns |> List.map (fun column -> shape column.Metadata)) expected "prepare byte widths"

          testCase "unfiltered scalar subqueries retain projected numeric metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT (SELECT b'01')+0 AS n,SUM((SELECT b'01')) AS s,AVG((SELECT b'01')) AS a,(SELECT 1.25)+0 AS d,(SELECT (SELECT b'01'))+0 AS nested"
              let session, result = handle session sql
              Expect.equal result (ResultSet([ "n"; "s"; "a"; "d"; "nested" ], [ [ Some "1"; Some "1"; Some "1.0000"; Some "1.25"; Some "1" ] ])) "scalar values and decimal display"
              let families = List.map (fun metadata -> metadata.TypeId)
              let expected = [ TypeLongLong; TypeNewDecimal; TypeNewDecimal; TypeNewDecimal; TypeLongLong ]
              Expect.equal (families session.LastResultColumnMetadata) expected "execution families"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal (columns |> List.map (fun column -> column.Metadata) |> families) expected "prepared families"

          testCase "scalar reduction ignores limits but preserves materialized boundaries"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let cases =
                  [ "(SELECT b'01' LIMIT 0)+0", Some "1", TypeLongLong
                    "(SELECT b'01' LIMIT 1 OFFSET 10)+0", Some "1", TypeLongLong
                    "(SELECT b'01' GROUP BY 1 LIMIT 0)+0", Some "1", TypeLongLong
                    "SUM((SELECT b'01' GROUP BY 1 LIMIT 0))", Some "1", TypeNewDecimal
                    "(SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 1)+0", Some "0", TypeDouble
                    "(SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 0)+0", None, TypeDouble
                    "(SELECT MIN(b'01') LIMIT 0)+0", None, TypeDouble
                    "(SELECT FIRST_VALUE(b'01') OVER () LIMIT 0)+0", None, TypeDouble
                    "(SELECT b'01' HAVING 1 LIMIT 0)+0", None, TypeDouble
                    "(SELECT b'01' FROM (SELECT 1)t LIMIT 0)+0", None, TypeDouble ]
              for expression, expected, family in cases do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ family ] (expression + " execution type")
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let statement = createPreparedStatement session sql ast count
                  let _, columns = preparedMetadata session statement.Ast count
                  Expect.equal (columns |> List.map (fun column -> column.Metadata.TypeId)) [ family ] (expression + " prepared type")

          testCase "conditional scalar reduction distinguishes literals from runtime predicates"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let cases =
                  [ "1", Some "1", TypeLongLong
                    "0", None, TypeDouble
                    "NULL", None, TypeDouble
                    "1=1", Some "1", TypeLongLong
                    "ABS(-1)=1", Some "1", TypeLongLong
                    "'a'='A'", Some "1", TypeLongLong
                    "COALESCE(NULL,1)", Some "1", TypeLongLong
                    "RAND()>=0", Some "0", TypeDouble
                    "EXISTS(SELECT 1)", Some "0", TypeDouble
                    "1 LIMIT 0", Some "1", TypeLongLong
                    "RAND()>=0 LIMIT 0", None, TypeDouble
                    "'a' COLLATE utf8mb4_bin='A'", None, TypeDouble
                    "NULL OR 1", Some "1", TypeLongLong
                    "NULL AND 1", None, TypeDouble ]
              for predicate, expected, family in cases do
                  let sql = "SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) predicate
                  Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ family ] (predicate + " execution type")

          testCase "conditional scalar reduction preserves retained variable and parameter bindings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @condition=1"
              let session, _ = handle session "PREPARE scalar_variable FROM 'SELECT (SELECT b''01'' WHERE @condition)+0 AS value'"
              let session, _ = handle session "PREPARE scalar_parameter FROM 'SELECT (SELECT b''01'' WHERE ?)+0 AS value'"
              let session, _ = handle session "PREPARE scalar_disjunction FROM 'SELECT (SELECT b''01'' WHERE 1 OR @condition)+0 AS value'"
              [ 1; 0; 1 ]
              |> List.fold (fun session value ->
                  let session, _ = handle session (sprintf "SET @condition=%d" value)
                  for sql, expected, family in
                      [ "SELECT (SELECT b'01' WHERE @condition)+0 AS value", (if value = 0 then None else Some "0"), TypeDouble
                        "EXECUTE scalar_variable", (if value = 0 then None else Some "0"), TypeDouble
                        "EXECUTE scalar_parameter USING @condition", (if value = 0 then None else Some "0"), TypeDouble
                        "EXECUTE scalar_disjunction", Some "1", TypeLongLong ] do
                      let current, result = handle session sql
                      Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) (sprintf "%s binding=%d" sql value)
                      Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ family ] (sql + " execution type")
                  session) session
              |> ignore

          testCase "conditional scalar reduction respects logical nesting boundaries"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let shapes =
                  [ "NOT (0 AND @condition)", "1", TypeLongLong
                    "NOT NOT (1 OR @condition)", "1", TypeLongLong
                    "IF(1 OR @condition,1,0)", "1", TypeLongLong
                    "IF(0 AND @condition,0,1)", "1", TypeLongLong
                    "IF(NULL AND @condition,0,1)", "1", TypeLongLong
                    "IF(1,1,@condition)", "0", TypeDouble
                    "(1 OR @condition)=1", "0", TypeDouble
                    "(1 OR @condition) IS TRUE", "0", TypeDouble
                    "CAST(1 OR @condition AS SIGNED)", "0", TypeDouble
                    "COALESCE(1 OR @condition,0)", "0", TypeDouble
                    "CASE WHEN 1 OR @condition THEN 1 ELSE 0 END", "0", TypeDouble
                    "(1 OR @condition)+0", "0", TypeDouble ]
              for predicate, expected, family in shapes do
                  let sql = "SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value"
                  let parameterized = sql.Replace("@condition", "?").Replace("'", "''")
                  let session, _ = handle session ("PREPARE nested_condition FROM '" + parameterized + "'")
                  [ 0; 1; 0 ]
                  |> List.fold (fun session value ->
                      let session, _ = handle session (sprintf "SET @condition=%d" value)
                      [ sql; "EXECUTE nested_condition USING @condition" ]
                      |> List.fold (fun session query ->
                          let current, result = handle session query
                          Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) (predicate + " value")
                          Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ family ] (predicate + " type")
                          current) session) session
                  |> ignore

          testCase "conditional scalar reduction evaluates original numeric and text builtins"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for predicate in
                  [ "CEIL(0.1)=1"; "FLOOR(1.9)=1"; "SQRT(4)=2"; "POWER(2,3)=8"; "SIGN(-2)=-1"
                    "GREATEST(1,2)=2"; "LEAST(1,2)=1"; "NULLIF(1,2)=1"; "SIN(0)=0"; "COS(0)=1"
                    "PI()>3"; "EXP(0)=1"; "LN(1)=0"; "LOG10(100)=2"; "BIT_COUNT(3)=2"
                    "CRC32('a')>0"; "HEX('a')='61'"; "REVERSE('ab')='ba'"; "TRIM(' a ')='a'" ] do
                  let current, result = handle session ("SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some "1" ] ])) predicate
                  Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLongLong ] (predicate + " type")

          testCase "division descriptors follow operand numeric contexts"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode=''"
              for expression, expected, family, width, scale in
                  [ "b'01'/2", Some "0.5000", TypeNewDecimal, 9u, 4uy
                    "2/b'01'", Some "2.0000", TypeNewDecimal, 7u, 4uy
                    "b''/2", Some "0.0000", TypeNewDecimal, 7u, 4uy
                    "b'100000001'/2", Some "128.5000", TypeNewDecimal, 11u, 4uy
                    "X'010001'/2", Some "32768.5000", TypeNewDecimal, 14u, 4uy
                    "b'01'/2.00", Some "0.5000", TypeNewDecimal, 11u, 4uy
                    "b'01'/2e0", Some "0.5", TypeDouble, 23u, 31uy
                    "b'01'/'2'", Some "0.5", TypeDouble, 23u, 31uy
                    "'1'/2", Some "0.5", TypeDouble, 23u, 31uy
                    "1/'2'", Some "0.5", TypeDouble, 23u, 31uy
                    "_binary X'31'/2", Some "0.5", TypeDouble, 23u, 31uy
                    "_binary b'01'/2", Some "0", TypeDouble, 23u, 31uy
                    "NULL/2", None, TypeDouble, 4u, 4uy
                    "1/NULL", None, TypeDouble, 6u, 4uy
                    "NULL/NULL", None, TypeDouble, 4u, 4uy
                    "b'01'/NULL", None, TypeDouble, 5u, 4uy
                    "NULL/b'01'", None, TypeDouble, 4u, 4uy
                    "(SELECT b'01')/2", Some "0.5000", TypeNewDecimal, 9u, 4uy
                    "(SELECT b'01' WHERE 1)/2", Some "0.5000", TypeNewDecimal, 9u, 4uy
                    "(SELECT b'01' FROM (SELECT 1)t)/2", Some "0.0000", TypeDouble, 5u, 4uy
                    "CAST('2020-01-01' AS DATE)/2", Some "10100050.5000", TypeNewDecimal, 14u, 4uy
                    "CAST('2020-01-02 03:04:05' AS DATETIME)/2", Some "10100051015202.5000", TypeNewDecimal, 20u, 4uy
                    "CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))/2", Some "10100051015202.5617280000", TypeNewDecimal, 26u, 10uy
                    "CAST('-12:34:56.123456' AS TIME(6))/2", Some "-61728.0617280000", TypeNewDecimal, 19u, 10uy
                    "CAST('2020-00-01' AS DATE)/2", Some "10100000.5000", TypeNewDecimal, 14u, 4uy
                    "CAST('2020-00-01 03:04:05.123456' AS DATETIME(6))/2", Some "10100000515202.5617280000", TypeNewDecimal, 26u, 10uy
                    "CAST('1' AS JSON)/2", Some "0.5", TypeDouble, 23u, 31uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ family, width, scale ] (expression + " descriptor")

          testCase "approximate division retains finite scales and caps unspecified scale"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for increment, expression, width, scale in
                  [ 0, "NULL/2", 0u, 0uy
                    0, "1/NULL", 2u, 0uy
                    0, "b'01'/NULL", 1u, 0uy
                    0, "NULL/CAST('2020-01-01' AS DATETIME(6))", 6u, 6uy
                    30, "NULL/2", 30u, 30uy
                    30, "1/NULL", 32u, 30uy
                    30, "b'01'/NULL", 31u, 30uy
                    30, "NULL/CAST('2020-01-01' AS DATETIME(6))", 23u, 31uy ] do
                  let session, _ = handle session (sprintf "SET div_precision_increment=%d" increment)
                  let current, result = handle session ("SELECT " + expression + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ None ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ TypeDouble, width, scale ] (expression + " descriptor")

          testCase "division of scalar string columns retains unspecified numeric scale"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE division_strings(s VARCHAR(10),b VARBINARY(10),j JSON)"
              let session, _ = handle session "INSERT INTO division_strings VALUES('1','1','1')"
              for increment in [ 0; 4; 10; 30 ] do
                  let session, _ = handle session (sprintf "SET div_precision_increment=%d" increment)
                  for column in [ "s"; "b"; "j" ] do
                      let expression = sprintf "(SELECT %s FROM division_strings)/2" column
                      let current, result = handle session ("SELECT " + expression + " AS value")
                      Expect.equal result (ResultSet([ "value" ], [ [ Some "0.5" ] ])) expression
                      Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                          [ TypeDouble, 23u, 31uy ] (expression + " descriptor")

          testCase "temporal division retains complete fields fractional precision and descriptors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode=''"
              for expression, expected, length, scale in
                  [ "CAST('2020-01-01' AS DATE)/2", "10100050.5000", 14u, 4uy
                    "CAST('2020-01-02 03:04:05' AS DATETIME)/2", "10100051015202.5000", 20u, 4uy
                    "CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))/2", "10100051015202.5617280000", 26u, 10uy
                    "CAST('-12:34:56.123456' AS TIME(6))/2", "-61728.0617280000", 19u, 10uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
                  let shape metadata = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (current.LastResultColumnMetadata |> List.map shape) [ TypeNewDecimal, length, scale ] (expression + " shape")
              let session, _ = handle session "CREATE TABLE temporal_zero(d DATE,dt DATETIME(6))"
              let session, _ = handle session "INSERT INTO temporal_zero VALUES('2020-00-01','2020-00-01 03:04:05.123456')"
              let current, result = handle session "SELECT d/2 AS d,dt/2 AS dt FROM temporal_zero"
              Expect.equal result (ResultSet([ "d"; "dt" ], [ [ Some "10100000.5000"; Some "10100000515202.5617280000" ] ])) "stored zero-component fields"
              Expect.equal (current.LastResultColumnMetadata |> List.map (fun value -> value.TypeId, value.ColumnLength, value.Decimals))
                  [ TypeNewDecimal, 14u, 4uy; TypeNewDecimal, 26u, 10uy ] "stored zero-component shapes"

          testCase "temporal arithmetic descriptors retain declared numeric precision"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected, typeId, width, scale in
                  [ "TIMESTAMP '2020-01-01 00:00:00.000'+0", "20200101000000.000", TypeNewDecimal, 20u, 3uy
                    "TIMESTAMP '2020-01-01 00:00:00.123'+0", "20200101000000.123", TypeNewDecimal, 20u, 3uy
                    "-TIMESTAMP '2020-01-01 00:00:00.123'", "-20200101000000.120", TypeDouble, 20u, 3uy
                    "CAST('2020-01-01' AS DATE)+0", "20200101", TypeLongLong, 10u, 0uy
                    "CAST('2020-01-01' AS DATETIME)*1", "20200101000000", TypeLongLong, 16u, 0uy
                    "CAST('2020-01-01' AS DATETIME(6))+0", "20200101000000.000000", TypeNewDecimal, 23u, 6uy
                    "CAST('2020-01-01' AS DATETIME(6))-0", "20200101000000.000000", TypeNewDecimal, 23u, 6uy
                    "CAST('2020-01-01' AS DATETIME(6))*1", "20200101000000.000000", TypeNewDecimal, 23u, 6uy
                    "CAST('-12:34:56' AS TIME(3))+0", "-123456.000", TypeNewDecimal, 13u, 3uy
                    "-CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))", "-20200101030405.125000", TypeDouble, 23u, 6uy
                    "ABS(CAST('2020-01-01 03:04:05.123456' AS DATETIME(6)))", "20200101030405.125000", TypeDouble, 23u, 6uy ] do
                  let current, result = handle session ("SELECT " + expression + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ typeId, width, scale ] (expression + " descriptor")

          testCase "binary temporal arithmetic parameters retain six fractional digits"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT ?+0 AS value"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              for value, expected in
                  [ DateTime(2020,1,1), "20200101000000.000000"
                    DateTime(2020,1,1).AddMilliseconds(123.0), "20200101000000.123000" ] do
                  let current, result = executePrepared session statement [ VDateTime value ]
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) "bound value"
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ TypeNewDecimal, 23u, 6uy ] "bound descriptor"
              let sql = "SELECT ADDTIME(CAST(? AS TIME(6)), ?) AS value"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              let values = [ "10:00:00"; "01:02:03" ] |> List.map (Fsdb.Temporal.tryParseTimeValue >> Option.get >> VTime)
              let _, result = executePrepared session statement values
              Expect.equal result (ResultSet([ "value" ], [ [ Some "11:02:03.000000" ] ])) "TIME values survive an inherited DATETIME context"


          testCase "year zero invalid calendar days obey ALLOW_INVALID_DATES"
          <| fun _ ->
              for mode, allowInvalid in [ "", false; "ALLOW_INVALID_DATES", true ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session ("SET sql_mode='" + mode + "'")
                  for input in [ "0000-02-29"; "0000-02-31" ] do
                      let current, result = handle session ("SELECT CAST('" + input + "' AS DATE) AS value")
                      Expect.equal result (ResultSet([ "value" ], [ [ if allowInvalid then Some input else None ] ])) input
                      let _, warnings = handle current "SHOW WARNINGS"
                      let expected = if allowInvalid then [] else [ [ Some "Warning"; Some "1292"; Some(sprintf "Incorrect datetime value: '%s'" input) ] ]
                      Expect.equal warnings (ResultSet([ "Level"; "Code"; "Message" ], expected)) "calendar validation warnings"
                  let _, result = handle session "SELECT CAST('0000-02-29 03:04:05.999' AS DATETIME(2)) AS value"
                  Expect.equal result (ResultSet([ "value" ], [ [ None ] ])) "invalid date cannot carry"

          testCase "component datetime fractions round truncate and reject calendar carries"
          <| fun _ ->
              for mode, truncate in [ "ALLOW_INVALID_DATES", false; "ALLOW_INVALID_DATES,TIME_TRUNCATE_FRACTIONAL", true ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session ("SET sql_mode='" + mode + "'")
                  for date in [ "2020-00-01"; "0000-00-00"; "2023-02-31" ] do
                      for fraction, rounded in [ "129", Some "13"; "999", None ] do
                          let input = date + " 03:04:05." + fraction
                          let expected =
                              if truncate then Some(date + " 03:04:05." + fraction.Substring(0,2))
                              else rounded |> Option.map (fun fraction -> date + " 03:04:05." + fraction)
                          let current, result = handle session ("SELECT CAST('" + input + "' AS DATETIME(2)) AS value")
                          Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) input
                          let _, warnings = handle current "SHOW WARNINGS"
                          Expect.equal warnings (ResultSet([ "Level"; "Code"; "Message" ], [])) "rounding rejection is silent in CAST"
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode=''"
              for input, expected in
                  [ "0000-01-01 03:04:05.999", "0000-00-00 03:04:06.00"
                    "0000-03-01 23:59:59.999", "0000-00-00 00:00:00.00"
                    "0000-12-31 23:59:59.999", "0001-01-01 00:00:00.00" ] do
                  let _, result = handle session ("SELECT CAST('" + input + "' AS DATETIME(2)) AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) input

          testCase "stored component datetime rounding reports column errors and permissive fallback"
          <| fun _ ->
              for mode, strict in [ "", false; "STRICT_TRANS_TABLES", true ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session ("SET sql_mode='" + mode + "'")
                  let session, _ = handle session "CREATE TABLE component_fraction(dt DATETIME(2))"
                  let current, result = handle session "INSERT INTO component_fraction VALUES('2020-00-01 03:04:05.999')"
                  if strict then
                      Expect.equal result (Err(1292, "Incorrect datetime value: '2020-00-01 03:04:05.999' for column 'dt' at row 1")) "strict carry rejection"
                  else
                      let _, warnings = handle current "SHOW WARNINGS"
                      Expect.equal warnings
                          (ResultSet([ "Level"; "Code"; "Message" ], [ [ Some "Warning"; Some "1264"; Some "Out of range value for column 'dt' at row 1" ] ]))
                          "permissive carry warning"
                      let _, result = handle current "SELECT dt FROM component_fraction"
                      Expect.equal result (ResultSet([ "dt" ], [ [ Some "0000-00-00 00:00:00.00" ] ])) "permissive zero fallback"

          testCase "zero timestamps reject nonzero fields before fractional quantization"
          <| fun _ ->
              for mode, strict in [ "", false; "STRICT_TRANS_TABLES", true; "TIME_TRUNCATE_FRACTIONAL", false ] do
                  for input in [ "0000-00-00 00:00:00.001"; "0000-00-00 00:00:00.129"; "2020-00-01 00:00:00"; "0000-01-01 00:00:00" ] do
                      let session = create 1 (Fsdb.Storage.create ())
                      let session, _ = handle session ("SET sql_mode='" + mode + "'")
                      let session, _ = handle session "CREATE TABLE ts_fraction(ts TIMESTAMP(2))"
                      let current, result = handle session ("INSERT INTO ts_fraction VALUES('" + input + "')")
                      if strict then
                          Expect.equal result (Err(1292, sprintf "Incorrect datetime value: '%s' for column 'ts' at row 1" input)) input
                      else
                          let _, warnings = handle current "SHOW WARNINGS"
                          Expect.equal warnings
                              (ResultSet([ "Level"; "Code"; "Message" ], [ [ Some "Warning"; Some "1264"; Some "Out of range value for column 'ts' at row 1" ] ])) input
                          let _, result = handle current "SELECT ts FROM ts_fraction"
                          Expect.equal result (ResultSet([ "ts" ], [ [ Some "0000-00-00 00:00:00.00" ] ])) "zero timestamp fallback"

          testCase "temporal text arguments retain declared fractional precision"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode=''"
              for expression, expected in
                  [ "CAST(CAST('2020-00-01' AS DATETIME(6)) AS CHAR)", "2020-00-01 00:00:00.000000"
                    "CONCAT(CAST('2020-01-01' AS DATETIME(3)))", "2020-01-01 00:00:00.000"
                    "CAST(CAST('-12:34:56.12' AS TIME(4)) AS CHAR)", "-12:34:56.1200"
                    "HEX(CAST(CAST('2020-01-01' AS DATETIME(3)) AS BINARY))", "323032302D30312D30312030303A30303A30302E303030" ] do
                  let _, result = handle session ("SELECT " + expression + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
              let _, result = handle session "SELECT CAST(CAST(NULL AS DATETIME(6)) AS CHAR) AS value"
              Expect.equal result (ResultSet([ "value" ], [ [ None ] ])) "NULL remains NULL"
              let session, _ = handle session "SET time_zone='+00:00'"
              let session, _ = handle session "CREATE TABLE text_temporal(dt DATETIME(3),tm TIME(4),ts TIMESTAMP(6))"
              let session, _ = handle session "INSERT INTO text_temporal VALUES('2020-01-01','-12:34:56.12','2020-01-01')"
              let session, _ = handle session "SET time_zone='+02:00'"
              let _, result = handle session "SELECT CAST(dt AS CHAR) AS dt,CONCAT(tm) AS tm,CAST(ts AS CHAR) AS ts FROM text_temporal"
              Expect.equal result
                  (ResultSet([ "dt"; "tm"; "ts" ], [ [ Some "2020-01-01 00:00:00.000"; Some "-12:34:56.1200"; Some "2020-01-01 02:00:00.000000" ] ]))
                  "stored precision and local timestamp"


          testCase "calendar casts respect independent zero-date modes and report invalid input"
          <| fun _ ->
              for mode, rejectZero, rejectPartial, allowInvalid in
                  [ "", false, false, false
                    "NO_ZERO_DATE", true, false, false
                    "NO_ZERO_IN_DATE", false, true, false
                    "NO_ZERO_DATE,NO_ZERO_IN_DATE", true, true, false
                    "STRICT_TRANS_TABLES", false, false, false
                    "ALLOW_INVALID_DATES", false, false, true ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session ("SET sql_mode='" + mode + "'")
                  for input, rejected in
                      [ "2020-00-01", rejectPartial; "0000-00-00", rejectZero
                        "0000-01-01", false; "2023-02-31", not allowInvalid
                        "2020-13-01", true; "nonsense", true; "0", true ] do
                      for target in [ "DATE"; "DATETIME(6)" ] do
                          let sql = sprintf "SELECT CAST('%s' AS %s) AS value" input target
                          let current, result = handle session sql
                          let expected = if rejected then None else Some(input + (if target = "DATE" then "" else " 00:00:00.000000"))
                          Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) (mode + " " + sql)
                          let _, warnings = handle current "SHOW WARNINGS"
                          let rows = if rejected then [ [ Some "Warning"; Some "1292"; Some(sprintf "Incorrect datetime value: '%s'" input) ] ] else []
                          Expect.equal warnings (ResultSet([ "Level"; "Code"; "Message" ], rows)) (mode + " " + sql + " warnings")

          testCase "calendar casts distinguish numeric zero and preserve year zero"
          <| fun _ ->
              for mode, zero in [ "", Some "0000-00-00"; "NO_ZERO_DATE", None ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session ("SET sql_mode='" + mode + "'")
                  let _, result = handle session "SELECT CAST(0 AS DATE) AS zero,CAST(20200101 AS DATE) AS compact"
                  Expect.equal result (ResultSet([ "zero"; "compact" ], [ [ zero; Some "2020-01-01" ] ])) mode
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode='STRICT_TRANS_TABLES,NO_ZERO_DATE,NO_ZERO_IN_DATE'"
              let _, literals = handle session "SELECT DATE '0000-01-01' AS d,TIMESTAMP '0000-01-01 03:04:05' AS dt"
              Expect.equal literals (ResultSet([ "d"; "dt" ], [ [ Some "0000-01-01"; Some "0000-01-01 03:04:05" ] ])) "year zero literals"
              let session, _ = handle session "CREATE TABLE calendar_year(d DATE,dt DATETIME(6))"
              let session, _ = handle session "INSERT INTO calendar_year VALUES('0000-01-01','0000-01-01 03:04:05.123456')"
              let _, stored = handle session "SELECT d,dt FROM calendar_year"
              Expect.equal stored (ResultSet([ "d"; "dt" ], [ [ Some "0000-01-01"; Some "0000-01-01 03:04:05.123456" ] ])) "year zero columns"

          testCase "binary literal variables discard numeric origin before binding"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @literal_bytes=b'01'"
              Expect.equal session.UserVariables["literal_bytes"] (VBytes [| 1uy |]) "SET stores ordinary bytes"
              let session, assigned = handle session "SELECT (@assigned:=b'01')+0 AS value"
              Expect.equal assigned (ResultSet([ "value" ], [ [ Some "0" ] ])) "assignment results materialize too"
              let session, _ = handle session "PREPARE literal_context FROM 'SELECT ?+0 AS value,SUM(?) AS total'"
              let _, result = handle session "EXECUTE literal_context USING @literal_bytes,@literal_bytes"
              Expect.equal result (ResultSet([ "value"; "total" ], [ [ Some "0"; Some "0" ] ])) "bindings do not regain literal origin"

          testCase "aggregate descriptors distinguish untyped null text and exact numeric inputs"
          <| fun _ ->
              for increment in [ 0; 4; 10 ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session (sprintf "SET div_precision_increment=%d" increment)
                  let sql = "SELECT SUM(NULL),AVG(NULL),SUM(DISTINCT NULL),AVG(DISTINCT NULL),SUM(NULL+NULL),AVG(NULL+NULL),SUM('1.25'),AVG('1.25'),SUM(1.25e0),AVG(1.25e0),SUM(CAST(NULL AS DECIMAL(10,2))),AVG(CAST(NULL AS DECIMAL(10,2)))"
                  let expected =
                      [ TypeDouble,17u,0uy; TypeDouble,uint32 (17+increment),byte increment
                        TypeDouble,17u,0uy; TypeDouble,uint32 (17+increment),byte increment
                        TypeDouble,17u,0uy; TypeDouble,uint32 (17+increment),byte increment
                        TypeDouble,23u,31uy; TypeDouble,23u,31uy
                        TypeDouble,23u,31uy; TypeDouble,23u,31uy
                        TypeNewDecimal,34u,2uy; TypeNewDecimal,uint32 (12+increment),byte (2+increment) ]
                  let session, result = handle session sql
                  match result with
                  | ResultSet(_, [ row ]) ->
                      Expect.equal row ([ None; None; None; None; None; None ] @ List.replicate 4 (Some "1.25") @ [ None; None ]) "aggregate values"
                  | other -> failtestf "unexpected aggregate result: %A" other
                  let shape metadata = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (session.LastResultColumnMetadata |> List.map shape) expected "execution descriptors"
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let statement = createPreparedStatement session sql ast count
                  let _, columns = preparedMetadata session statement.Ast count
                  Expect.equal (columns |> List.map (fun column -> shape column.Metadata)) expected "prepare descriptors"

          testCase "decimal expression descriptors derive precision from their operands"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=1.25"
              let session, _ = handle session "SELECT 1.25,0.00,1.25+1,1.25*2,1.25/3.00,@v+1,@v*2,ROUND(@v,2),TRUNCATE(@v,2),123.45%6.7,COALESCE(1.25,123),(@v+@v)+1,SUM(1.25),AVG(1.25),AVG(DISTINCT 1),SUM(DISTINCT 1)"
              let descriptors = session.LastResultColumnMetadata |> List.map (fun metadata -> metadata.ColumnLength, metadata.Decimals)
              Expect.equal descriptors
                  [ 5u,2uy; 5u,2uy; 6u,2uy; 6u,2uy; 11u,6uy; 68u,30uy; 67u,30uy
                    40u,2uy; 39u,2uy; 7u,2uy; 7u,2uy; 68u,30uy; 27u,2uy; 9u,6uy; 7u,4uy; 24u,0uy ]
                  "wire lengths include precision, sign, and the decimal point"
              let sql = "SELECT 1.25 AS literal,1.25/3.00 AS quotient,@v+1 AS addition,ROUND(@v,2) AS rounded,TRUNCATE(@v,2) AS truncated"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal (columns |> List.map (fun column -> column.Metadata.ColumnLength, column.Metadata.Decimals))
                  [ 5u,2uy; 11u,6uy; 68u,30uy; 40u,2uy; 39u,2uy ]
                  "binary PREPARE retains expression descriptors beyond stored-column bounds"

          testCase "decimal precision bounds distinguish addition coalescing and modulo"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT CAST(1 AS DECIMAL(65,0))+CAST(1 AS DECIMAL(65,30)) AS addition,COALESCE(CAST(1 AS DECIMAL(40,0)),CAST(1 AS DECIMAL(40,30))) AS coalesced,MOD(CAST(1 AS DECIMAL(40,0)),CAST(1 AS DECIMAL(40,30))) AS modulo"
              let session, _ = handle session sql
              let expected = [ 98u,30uy; 67u,30uy; 42u,30uy ]
              Expect.equal (session.LastResultColumnMetadata |> List.map (fun metadata -> metadata.ColumnLength, metadata.Decimals)) expected
                  "addition combines widths without truncating while coalescing and modulo use different bounds"
              let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
              let statement = createPreparedStatement session sql ast count
              let _, columns = preparedMetadata session statement.Ast count
              Expect.equal (columns |> List.map (fun column -> column.Metadata.ColumnLength, column.Metadata.Decimals)) expected
                  "preparation preserves the same wire bounds"

          testCase "unsigned negation separates constant promotion from runtime overflow"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE unsigned_negation(u BIGINT UNSIGNED,s BIGINT)"
              let session, _ = handle session "INSERT INTO unsigned_negation VALUES(1,1),(9223372036854775808,-9223372036854775808),(18446744073709551615,2)"
              let session, small = handle session "SELECT -u AS value FROM unsigned_negation WHERE u=1"
              Expect.equal small (ResultSet([ "value" ], [ [ Some "-1" ] ])) "unsigned unary minus has a signed result"
              let session, boundary = handle session "SELECT -u AS value FROM unsigned_negation WHERE u=9223372036854775808"
              Expect.equal boundary (ResultSet([ "value" ], [ [ Some "-9223372036854775808" ] ])) "the signed minimum is representable"
              for sql in [ "SELECT -u FROM unsigned_negation WHERE u=18446744073709551615"; "SELECT -s FROM unsigned_negation WHERE s=-9223372036854775808" ] do
                  match handle session sql |> snd with
                  | Err(1690, _) -> ()
                  | other -> failtestf "expected runtime BIGINT overflow: %A" other
              let session, constants = handle session "SELECT -CAST(18446744073709551615 AS UNSIGNED) AS unsigned_value,-CAST(-9223372036854775808 AS SIGNED) AS signed_value"
              Expect.equal constants (ResultSet([ "unsigned_value"; "signed_value" ], [ [ Some "-18446744073709551615"; Some "9223372036854775808" ] ])) "constants promote to decimal"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal; TypeNewDecimal ] "promoted constants advertise decimal"

          testCase "rounding precision expressions preserve exact values and descriptors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected, family, width, scale in
                  [ "ROUND(1.25,1+0)", Some "1.3", TypeNewDecimal, 5u, 1uy
                    "TRUNCATE(1.25,1+0)", Some "1.2", TypeNewDecimal, 4u, 1uy
                    "ROUND(CAST(1 AS DECIMAL(10,0)),0)", Some "1", TypeNewDecimal, 12u, 0uy
                    "TRUNCATE(CAST(0.1 AS DECIMAL(4,4)),0)", Some "0", TypeNewDecimal, 2u, 0uy
                    "ROUND(1.25,NULL)", None, TypeNewDecimal, 3u, 0uy
                    "ROUND(1.25,1.5)", Some "1.25", TypeNewDecimal, 5u, 2uy
                    "ROUND(1.25,1.5e0)", Some "1.25", TypeNewDecimal, 5u, 2uy
                    "ROUND(1.25,'1.5')", Some "1.3", TypeNewDecimal, 5u, 1uy
                    "TRUNCATE(1.29,1.5)", Some "1.29", TypeNewDecimal, 5u, 2uy
                    "ROUND(1.25,30)", Some "1.25", TypeNewDecimal, 5u, 2uy
                    "TRUNCATE(1.25,30)", Some "1.25", TypeNewDecimal, 5u, 2uy
                    "TRUNCATE(9223372036854775807,0)", Some "9223372036854775807", TypeLongLong, 21u, 0uy
                    "TRUNCATE(-9223372036854775808,-1)", Some "-9223372036854775800", TypeLongLong, 21u, 0uy
                    "ROUND(1.25,18446744073709551615)", Some "1.25", TypeNewDecimal, 5u, 2uy
                    "TRUNCATE(1.25,-9223372036854775808)", Some "0", TypeNewDecimal, 2u, 0uy
                    "ROUND(1.25,-9223372036854775808)", Some "0", TypeNewDecimal, 3u, 0uy
                    "ROUND('2.5')", Some "2", TypeDouble, 23u, 31uy
                    "TRUNCATE(1.25e0,18446744073709551615)", Some "1.25", TypeDouble, 23u, 31uy
                    "TRUNCATE(1.25e0,-9223372036854775808)", Some "0", TypeDouble, 23u, 31uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  let shape (metadata: ColumnMetadata) = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (current.LastResultColumnMetadata |> List.map shape)
                      [ family, width, scale ] (expression + " execution descriptor")
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> shape column.Metadata))
                      [ family, width, scale ] (expression + " prepare descriptor")

          testCase "prepared rounding precision retains the input scale"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE rounding_parameters(d DECIMAL(10,2))"
              let session, _ = handle session "INSERT INTO rounding_parameters VALUES(1.25)"
              let session, _ = handle session "SET @digits=1"
              for name, expected in [ "ROUND", "1.30"; "TRUNCATE", "1.20" ] do
                  let sql = "SELECT " + name + "(d,?) AS value FROM rounding_parameters"
                  let current, prepared = handle session ("PREPARE rounding_parameter FROM '" + sql + "'")
                  Expect.equal prepared (Affected 0UL) "prepare succeeds"
                  let current, result = handle current "EXECUTE rounding_parameter USING @digits"
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) "bound precision remains runtime-dependent"
                  Expect.equal current.LastResultColumnMetadata.Head.Decimals 2uy "execution retains input scale"

          testCase "rounding preserves nullability and reports signed overflow"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let current, result = handle session "SELECT ROUND(1.25,NULL) AS value"
              Expect.equal result (ResultSet([ "value" ], [ [ None ] ])) "a NULL precision yields NULL"
              Expect.equal (current.LastResultColumnMetadata.Head.Flags &&& NotNullFlag) 0us "NULL precision is nullable"
              for value in [ "9223372036854775807"; "-9223372036854775808" ] do
                  match handle session ("SELECT ROUND(" + value + ",-1)") |> snd with
                  | Err(1690, _) -> ()
                  | other -> failtestf "expected MySQL signed BIGINT overflow for %s, got %A" value other

          testCase "floor and ceiling derive whole-number result descriptors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected, family, width, scale in
                  [ "FLOOR(1.25)", Some "1", TypeLongLong, 21u, 0uy
                    "CEIL(-1.25)", Some "-1", TypeLongLong, 21u, 0uy
                    "FLOOR(1)", Some "1", TypeLongLong, 21u, 0uy
                    "FLOOR(CAST(1.25 AS DECIMAL(19,2)))", Some "1", TypeLongLong, 21u, 0uy
                    "CEILING(CAST(1.25 AS DECIMAL(20,2)))", Some "2", TypeNewDecimal, 20u, 0uy
                    "FLOOR(CAST(1.25 AS DECIMAL(30,2)))", Some "1", TypeNewDecimal, 30u, 0uy
                    "CEIL(999999999999999999.99)", Some "1000000000000000000", TypeNewDecimal, 20u, 0uy
                    "FLOOR(NULL)", None, TypeDouble, 23u, 31uy
                    "FLOOR('1.25')", Some "1", TypeDouble, 23u, 31uy
                    "FLOOR(1e20)", Some "1e20", TypeDouble, 23u, 31uy
                    "CEIL(-1e20)", Some "-1e20", TypeDouble, 23u, 31uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  let shape (metadata: ColumnMetadata) = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (current.LastResultColumnMetadata |> List.map shape)
                      [ family, width, scale ] (expression + " execution descriptor")
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> shape column.Metadata))
                      [ family, width, scale ] (expression + " prepare descriptor")

          testCase "scientific literals retain spelling widths across projection boundaries"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE scientific_source(n INT,f FLOAT,d DOUBLE(10,2))"
              let session, _ = handle session "INSERT INTO scientific_source VALUES(1,1.25,1.25)"
              for expression, expected, family, width, scale in
                  [ "1e0", "1", TypeDouble, 3u, 31uy
                    "1E+00", "1", TypeDouble, 5u, 31uy
                    "0001e000", "1", TypeDouble, 8u, 31uy
                    "1.00e0", "1", TypeDouble, 6u, 31uy
                    ".1e1", "1", TypeDouble, 4u, 31uy
                    "1.e0", "1", TypeDouble, 4u, 31uy
                    "+1e0", "1", TypeDouble, 3u, 31uy
                    "-1e0", "-1", TypeDouble, 23u, 31uy
                    "-(-1e0)", "1", TypeDouble, 23u, 31uy
                    "(1e0)", "1", TypeDouble, 3u, 31uy
                    "(SELECT 1e0)", "1", TypeDouble, 3u, 31uy
                    "(SELECT 1e0 FROM scientific_source LIMIT 1)", "1", TypeDouble, 3u, 31uy
                    "(SELECT x FROM (SELECT 1e0 AS x)t)", "1", TypeDouble, 3u, 31uy
                    "1 DIV 2e0", "0", TypeLongLong, 22u, 0uy
                    "2e0 DIV 1", "2", TypeLongLong, 22u, 0uy
                    "(SELECT 2e0 FROM scientific_source LIMIT 1) DIV 1", "2", TypeLongLong, 22u, 0uy
                    "(SELECT f FROM scientific_source) DIV 1", "1", TypeLongLong, 13u, 0uy
                    "(SELECT d FROM scientific_source) DIV 1", "1", TypeLongLong, 21u, 0uy
                    "(SELECT d FROM scientific_source) DIV 0.1", "12", TypeLongLong, 22u, 0uy
                    "2 DIV (SELECT d FROM scientific_source)", "1", TypeLongLong, 4u, 0uy
                    "1e0/2", "0.5", TypeDouble, 23u, 31uy
                    "ABS(1e0)", "1", TypeDouble, 23u, 31uy
                    "COALESCE(1e0,NULL)", "1", TypeDouble, 23u, 31uy
                    "1e0+0", "1", TypeDouble, 23u, 31uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
                  let shape (metadata: ColumnMetadata) = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (current.LastResultColumnMetadata |> List.map shape)
                      [ family, width, scale ] (expression + " execution descriptor")
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> shape column.Metadata))
                      [ family, width, scale ] (expression + " prepare descriptor")

          testCase "binary prepared results retain unrounded approximate values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE approximate_products(d DOUBLE(10,2))"
              let session, _ = handle session "INSERT INTO approximate_products VALUES(1.25)"
              for sql, binaryValue, textValue, copies in
                  [ "SELECT d*d AS product,CAST(d*d AS CHAR) AS text FROM approximate_products", "1.5625", "1.56", 1
                    "SELECT d*d AS product,CAST(d*d AS CHAR) AS text FROM approximate_products UNION ALL SELECT d*d,CAST(d*d AS CHAR) FROM approximate_products", "1.56", "1.56", 2
                    "SELECT d*0.1 AS product,CAST(d*0.1 AS CHAR) AS text FROM approximate_products UNION ALL SELECT d*0.1,CAST(d*0.1 AS CHAR) FROM approximate_products", "0.12", "0.12", 2 ] do
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let statement = createPreparedStatement session sql ast count
                  let prepared = { session with Statements = Map.add 1 statement session.Statements }
                  let afterBinary, binary = executePreparedHandle prepared 1 []
                  Expect.equal binary
                      (ResultSet([ "product"; "text" ], List.replicate copies [ Some binaryValue; Some textValue ]))
                      "SELECT retains precision while UNION materialization and text conversion round"
                  let _, text = handle afterBinary sql
                  Expect.equal text
                      (ResultSet([ "product"; "text" ], List.replicate copies [ Some textValue; Some textValue ]))
                      "the following text query retains display scale"

          testCase "approximate expression descriptors distinguish stored fields and numeric results"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE approximate_numbers(d DOUBLE,f FLOAT,df DOUBLE(10,2))"
              let session, _ = handle session "INSERT INTO approximate_numbers VALUES(1.25,1.25,1.25)"
              for expression, expected, family, width, scale in
                  [ "-1e0", Some "-1", TypeDouble, 23u, 31uy
                    "1e0+1", Some "2", TypeDouble, 23u, 31uy
                    "'1'+1", Some "2", TypeDouble, 23u, 31uy
                    "ABS(1e0)", Some "1", TypeDouble, 23u, 31uy
                    "SQRT(4)", Some "2", TypeDouble, 23u, 31uy
                    "ROUND(1e0,2)", Some "1", TypeDouble, 23u, 31uy
                    "COALESCE(1e0,0)", Some "1", TypeDouble, 23u, 31uy
                    "COALESCE(1e0,NULL)", Some "1", TypeDouble, 23u, 31uy
                    "CAST(1 AS DOUBLE)", Some "1", TypeDouble, 23u, 31uy
                    "d", Some "1.25", TypeDouble, 22u, 31uy
                    "f", Some "1.25", TypeFloat, 12u, 31uy
                    "df", Some "1.25", TypeDouble, 10u, 2uy
                    "d+0", Some "1.25", TypeDouble, 23u, 31uy
                    "f+0", Some "1.25", TypeDouble, 23u, 31uy
                    "df+0", Some "1.25", TypeDouble, 10u, 2uy
                    "-df", Some "-1.25", TypeDouble, 19u, 2uy
                    "ABS(df)", Some "1.25", TypeDouble, 19u, 2uy
                    "ROUND(df,1)", Some "1.2", TypeDouble, 23u, 31uy
                    "TRUNCATE(df,1)", Some "1.2", TypeDouble, 23u, 31uy
                    "FLOOR(df)", Some "1", TypeDouble, 23u, 31uy
                    "CEIL(df)", Some "2", TypeDouble, 23u, 31uy
                    "ABS(f)", Some "1.25", TypeDouble, 23u, 31uy
                    "-f", Some "-1.25", TypeDouble, 23u, 31uy
                    "df+df", Some "2.50", TypeDouble, 10u, 2uy
                    "df*df", Some "1.56", TypeDouble, 10u, 2uy
                    "df+NULL", None, TypeDouble, 10u, 2uy
                    "COALESCE(NULLIF(df,df),2)", Some "2.00", TypeDouble, 10u, 2uy
                    "COALESCE(NULLIF(df,df),2)+0.5", Some "2.50", TypeDouble, 10u, 2uy
                    "COALESCE(df,0)", Some "1.25", TypeDouble, 10u, 2uy
                    "IF(1,df,0)", Some "1.25", TypeDouble, 10u, 2uy
                    "COALESCE(f,0)", Some "1.25", TypeFloat, 23u, 31uy
                    "COALESCE(f,1.25)", Some "1.25", TypeDouble, 23u, 31uy
                    "IFNULL(f,1.25)", Some "1.25", TypeDouble, 23u, 31uy
                    "IF(0,df,2)", Some "2.00", TypeDouble, 10u, 2uy
                    "CASE WHEN 0 THEN df ELSE 2 END", Some "2.00", TypeDouble, 10u, 2uy
                    "df*1.2345", Some "1.5431", TypeDouble, 12u, 4uy
                    "df+1.2345", Some "2.4845", TypeDouble, 12u, 4uy
                    "CAST(1 AS FLOAT)", Some "1", TypeFloat, 23u, 31uy
                    "-b'01'", Some "-1", TypeDouble, 17u, 0uy
                    "ABS(b'01')", Some "1", TypeDouble, 17u, 0uy ] do
                  let sql = "SELECT " + expression + " AS value FROM approximate_numbers"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  let shape (metadata: ColumnMetadata) = metadata.TypeId, metadata.ColumnLength, metadata.Decimals
                  Expect.equal (current.LastResultColumnMetadata |> List.map shape)
                      [ family, width, scale ] (expression + " descriptor")
                  let ast, count = prepareStatementForSession session sql |> Result.defaultWith (fun error -> failtestf "%A" error)
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> shape column.Metadata))
                      [ family, width, scale ] (expression + " prepared descriptor")

          testCase "integer literal and arithmetic descriptors retain operand precision"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected, family, width, scale in
                  [ "1", Some "1", TypeLongLong, 2u, 0uy
                    "-1", Some "-1", TypeLongLong, 2u, 0uy
                    "127", Some "127", TypeLongLong, 4u, 0uy
                    "128", Some "128", TypeLongLong, 4u, 0uy
                    "9223372036854775807", Some "9223372036854775807", TypeLongLong, 20u, 0uy
                    "18446744073709551615", Some "18446744073709551615", TypeLongLong, 20u, 0uy
                    "1+1", Some "2", TypeLongLong, 3u, 0uy
                    "12+34", Some "46", TypeLongLong, 4u, 0uy
                    "1-1", Some "0", TypeLongLong, 3u, 0uy
                    "1*1", Some "1", TypeLongLong, 3u, 0uy
                    "12*34", Some "408", TypeLongLong, 5u, 0uy
                    "CAST(1 AS SIGNED)", Some "1", TypeLongLong, 21u, 0uy
                    "CAST(1 AS UNSIGNED)", Some "1", TypeLongLong, 21u, 0uy
                    "CAST(18446744073709551615 AS UNSIGNED)", Some "18446744073709551615", TypeLongLong, 21u, 0uy
                    "-CAST(18446744073709551615 AS UNSIGNED)", Some "-18446744073709551615", TypeNewDecimal, 22u, 0uy
                    "CAST(1 AS UNSIGNED)/2", Some "0.5000", TypeNewDecimal, 27u, 4uy
                    "CAST(1 AS UNSIGNED)/CAST(2 AS UNSIGNED)", Some "0.5000", TypeNewDecimal, 26u, 4uy
                    "COALESCE(CAST(18446744073709551615 AS UNSIGNED),0)", Some "18446744073709551615", TypeNewDecimal, 22u, 0uy
                    "1 DIV 2", Some "0", TypeLongLong, 2u, 0uy
                    "ROUND(1,0)", Some "1", TypeLongLong, 21u, 0uy
                    "TRUNCATE(1,0)", Some "1", TypeLongLong, 21u, 0uy
                    "CAST(NULL AS UNSIGNED)", None, TypeLongLong, 21u, 0uy
                    "CAST('123' AS SIGNED)", Some "123", TypeLongLong, 21u, 0uy
                    "b'01'/b'01'", Some "1.0000", TypeNewDecimal, 9u, 4uy
                    "b'01'/CAST(2 AS UNSIGNED)", Some "0.5000", TypeNewDecimal, 9u, 4uy ] do
                  let sql = "SELECT " + expression + " AS value"
                  let current, result = handle session sql
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ family, width, scale ] (expression + " descriptor")

                  let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
                  let _, columns = preparedMetadata session ast count
                  Expect.equal (columns |> List.map (fun column -> column.Metadata.TypeId, column.Metadata.ColumnLength, column.Metadata.Decimals))
                      [ family, width, scale ] (expression + " prepared descriptor")

          testCase "integer operator precision is independent of operand unsignedness"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @value=7"
              for expression, expected, width, unsigned in
                  [ "b'01'+1", Some "2", 5u, false
                    "1+b'01'", Some "2", 5u, false
                    "b'01'-1", Some "0", 5u, false
                    "b'01'*2", Some "2", 5u, false
                    "CAST(1 AS UNSIGNED)+2", Some "3", 22u, true
                    "CAST(3 AS UNSIGNED)-2", Some "1", 22u, true
                    "-(b'01'+1)", Some "-2", 5u, false
                    "-(1+1)", Some "-2", 3u, false
                    "MOD(b'01',2)", Some "1", 4u, false
                    "MOD(2,b'01')", Some "0", 4u, false
                    "123 DIV 2", Some "61", 4u, false
                    "1.25 DIV 0.1", Some "12", 3u, false
                    "'12' DIV 2", Some "6", 3u, false
                    "12 DIV '2'", Some "6", 4u, false
                    "b'01' DIV 2", Some "0", 4u, false
                    "2 DIV b'01'", Some "2", 2u, false
                    "NULL DIV 2", None, 1u, false
                    "1 DIV NULL", None, 2u, false
                    "CAST(1 AS UNSIGNED) DIV 2", Some "0", 21u, true
                    "1 DIV 2e0", Some "0", 22u, false
                    "9223372036854775807+0", Some "9223372036854775807", 21u, false
                    "MOD(CAST(1 AS UNSIGNED),2)", Some "1", 22u, true
                    "MOD(2,CAST(1 AS UNSIGNED))", Some "0", 22u, false
                    "1=1", Some "1", 1u, false
                    "1<2", Some "1", 1u, false
                    "NOT 0", Some "1", 1u, false
                    "1 IS NULL", Some "0", 1u, false
                    "EXISTS(SELECT 1)", Some "1", 1u, false
                    "-(1=1)", Some "-1", 2u, false
                    "-(NOT 0)", Some "-1", 2u, false
                    "(1=1)+1", Some "2", 3u, false
                    "@value", Some "7", 21u, false
                    "@value+1", Some "8", 22u, false
                    "-@value", Some "-7", 21u, false
                    "@value DIV 2", Some "3", 21u, false ] do
                  let current, result = handle session ("SELECT " + expression + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ expected ] ])) expression
                  let metadata = current.LastResultColumnMetadata.Head
                  Expect.equal (metadata.TypeId, metadata.ColumnLength, metadata.Decimals, hasMetadataFlag UnsignedFlag metadata)
                      (TypeLongLong, width, 0uy, unsigned) (expression + " descriptor")

          testCase "closed conditional and numeric functions support negation promotion"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for operand, expected, width in
                  [ "COALESCE(18446744073709551615,0)", "-18446744073709551615", 21u
                    "IFNULL(18446744073709551615,0)", "-18446744073709551615", 21u
                    "IF(1,18446744073709551615,0)", "-18446744073709551615", 21u
                    "GREATEST(18446744073709551615,0)", "-18446744073709551615", 21u
                    "LEAST(18446744073709551615,18446744073709551615)", "-18446744073709551615", 21u
                    "NULLIF(18446744073709551615,0)", "-18446744073709551615", 21u
                    "ROUND(18446744073709551615,0)", "-18446744073709551615", 22u
                    "TRUNCATE(18446744073709551615,0)", "-18446744073709551615", 22u
                    "CASE WHEN 1 THEN 18446744073709551615 ELSE 0 END", "-18446744073709551615", 21u
                    "COALESCE(CAST(-9223372036854775808 AS SIGNED),0)", "9223372036854775808", 21u ] do
                  let current, result = handle session ("SELECT -" + operand + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) operand
                  Expect.equal (current.LastResultColumnMetadata |> List.map (fun item -> item.TypeId, item.ColumnLength, item.Decimals))
                      [ TypeNewDecimal, width, 0uy ] (operand + " descriptor")

          testCase "mixed integer conditional results carry decimal semantics into arithmetic"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected in
                  [ "COALESCE(18446744073709551615,0)+1", "18446744073709551616"
                    "IF(1,18446744073709551615,0)+1", "18446744073709551616"
                    "CASE WHEN 1 THEN 18446744073709551615 ELSE 0 END+1", "18446744073709551616" ] do
                  let current, result = handle session ("SELECT " + expression + " AS value")
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
                  Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "decimal arithmetic"
              for fallback, expected in
                  [ "0", Some "-18446744073709551615"; "CAST(0 AS UNSIGNED)", None ] do
                  let sql = "SELECT -COALESCE(CAST(? AS UNSIGNED)," + fallback + ") AS value"
                  let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
                  let statement = createPreparedStatement session sql ast count
                  let current, result = executePrepared session statement [ VUInt UInt64.MaxValue ]
                  match expected, result with
                  | Some expected, ResultSet([ "value" ], [ [ Some actual ] ]) ->
                      Expect.equal actual expected "mixed runtime signedness"
                      Expect.equal (current.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "mixed runtime descriptor"
                  | None, Err(1690, _) -> ()
                  | _ -> failtestf "Unexpected conditional runtime negation: %A" result

          testCase "prepared unsigned negation keeps runtime overflow checks after binding"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SELECT -CAST(? AS UNSIGNED) AS value"
              let ast, count = prepareStatement sql |> function Ok value -> value | Error error -> failtestf "%A" error
              let statement = createPreparedStatement session sql ast count
              let session = { session with Statements = Map.ofList [ 1, statement ] }
              let steps =
                  [ 1UL, Some "-1"
                    9223372036854775808UL, Some "-9223372036854775808"
                    UInt64.MaxValue, None
                    2UL, Some "-2" ]
              (session, steps)
              ||> List.fold (fun session (value, expected) ->
                  let session, result = executePreparedHandle session 1 [ VUInt value ]
                  match expected, result with
                  | None, Err(1690, _) -> ()
                  | Some expected, ResultSet([ "value" ], [ [ Some actual ] ]) ->
                      Expect.equal actual expected "runtime signed negation"
                      Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLongLong ] "runtime operands retain BIGINT"
                  | _ -> failtestf "unexpected negation result for %A: %A" value result
                  session)
              |> ignore

          testCase "decimal unary negation preserves precision independently of subtraction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @v=1.25"
              let session, result = handle session "SELECT -@v AS negated,0-@v AS subtracted"
              let negative = Some "-1.250000000000000000000000000000"
              Expect.equal result (ResultSet([ "negated"; "subtracted" ], [ [ negative; negative ] ])) "both operations return the same value"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.ColumnLength) [ 67u; 68u ] "only subtraction reserves a carry digit"
              let session, _ = handle session "PREPARE negation FROM 'SELECT -@v AS negated'"
              let session, _ = handle session "SET @v=2"
              let session, result = handle session "EXECUTE negation"
              Expect.equal result (ResultSet([ "negated" ], [ [ Some "-2.000000000000000000000000000000" ] ])) "capture and binding traverse unary negation"
              Expect.equal session.LastResultColumnMetadata.Head.ColumnLength 67u "the captured decimal precision survives binding"

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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

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
                            SchemaDependencies = Map.empty
                            DivisionPrecisionIncrement = 4 }
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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

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
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

                  let session, result = executePrepared session statement [ VInt 0L ]

                  Expect.equal result (Err(1690, "The length of RANDOM_BYTES must be between 1 and 1024")) "error"
                  Expect.isNone session.Tx "transaction"
              | other -> failtestf "expected a parsed statement, got %A" other ]
