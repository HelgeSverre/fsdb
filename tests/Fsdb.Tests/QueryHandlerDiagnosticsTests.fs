module Fsdb.Tests.QueryHandlerDiagnosticsTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let private note code message = Fsdb.Diagnostics.Note, code, message
let private warning code message = Fsdb.Diagnostics.Warning, code, message
let private error code message = Fsdb.Diagnostics.Error, code, message

let private conditionTriples session =
    session.Diagnostics
    |> List.map (fun condition -> condition.Level, condition.Code, condition.Message)

let private expectAffectedWithConditions context expected (session, result) =
    Expect.equal result (Affected 0UL) context
    Expect.equal (conditionTriples session) expected (context + " diagnostics")
    session

let private promoteParent =
    "CREATE FUNCTION promote_parent() RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN UPDATE parent SET n=3 WHERE n=2; RETURN 3; END"

let private routineUpdateSetup functionSql =
    [ "CREATE TABLE parent(n INT PRIMARY KEY)"
      "INSERT INTO parent VALUES(1),(2)"
      "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
      "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
      functionSql ]

let tests =
    testList
        "Diagnostics"
        [ testCase "CHECKSUM TABLE reports each missing object while retaining result rows"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE present (id INT)"
              let session, _ = handle session "CREATE VIEW present_view AS SELECT id FROM present"
              for suffix in [ ""; " QUICK" ] do
                  let session, result = handle session ("CHECKSUM TABLE absent, present_view, missing_db.other, present" + suffix)
                  let presentChecksum = if suffix = "" then Some "0" else None
                  Expect.equal
                      result
                      (ResultSet(
                          [ "Table"; "Checksum" ],
                          [ [ Some "fsdb.absent"; None ]
                            [ Some "fsdb.present_view"; None ]
                            [ Some "missing_db.other"; None ]
                            [ Some "fsdb.present"; presentChecksum ] ]
                      ))
                      "checksum rows remain aligned with the requested tables"
                  Expect.equal
                      (conditionTriples session)
                      [ error 1146 "Table 'fsdb.absent' doesn't exist"
                        error 1347 "'fsdb.present_view' is not BASE TABLE"
                        error 1049 "Unknown database 'missing_db'" ]
                      "missing objects emit ordered conditions in both modes"

          testCase "CHECKSUM TABLE resolves session temporary tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TEMPORARY TABLE scratch (id INT)"
              let session, _ = handle session "INSERT INTO scratch VALUES (1)"
              let session, result = handle session "CHECKSUM TABLE scratch"
              match result with
              | ResultSet([ "Table"; "Checksum" ], [ [ Some "fsdb.scratch"; Some _ ] ]) -> ()
              | other -> failtestf "expected a temporary-table checksum, got %A" other
              Expect.isEmpty (conditionTriples session) "temporary table exists"

          testCase "Foreign-key ALTER rejects missing drops and required column drops atomically"
          <| fun _ ->
              for sql, code, state in
                  [ "ALTER TABLE child DROP FOREIGN KEY missing", 1091, "42000"
                    "ALTER TABLE child DROP FOREIGN KEY fk,DROP FOREIGN KEY fk", 1091, "42000"
                    "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(b) REFERENCES parent(n),DROP FOREIGN KEY new_fk", 1091, "42000"
                    "ALTER TABLE child DROP COLUMN a", 1828, "HY000"
                    "ALTER TABLE parent DROP COLUMN n", 1829, "HY000" ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for setup in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY,m INT)"
                        "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))" ] do
                      Expect.isNone (run setup |> errorInfo) setup
                  Expect.equal (run sql |> errorInfo |> Option.map (fun error -> error.Code, error.State)) (Some(code, state)) sql
                  let database = store.Catalog.[Fsdb.Storage.defaultDatabase]
                  Expect.equal (database.["child"].Columns |> List.map _.Name) [ "a"; "b" ] "child columns survive"
                  Expect.equal (database.["parent"].Columns |> List.map _.Name) [ "n"; "m" ] "parent columns survive"
                  Expect.equal (database.["child"].ForeignKeys |> List.map _.Name) [ "fk" ] "constraint survives"

          testCase "foreign keys reject virtual generated columns on either side"
          <| fun _ ->
              for parentSql, childSql in
                  [ ("CREATE TABLE parent(id INT PRIMARY KEY,x INT AS (id+1) VIRTUAL,UNIQUE KEY(x))",
                     "CREATE TABLE child(n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(x))")
                    ("CREATE TABLE parent(id INT PRIMARY KEY)",
                     "CREATE TABLE child(id INT,x INT AS (id+1) VIRTUAL,KEY(x),CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(id))") ] do
                  let store = Fsdb.Storage.create()
                  let session = create 1 store
                  let session, parentResult = handle session parentSql
                  Expect.isNone (errorInfo parentResult) "parent setup"
                  let _, childResult = handle session childSql
                  Expect.equal
                      (errorInfo childResult |> Option.map (fun error -> error.Code, error.State, error.Message))
                      (Some(3733, "HY000", "Foreign key 'fk' uses virtual column 'x' which is not supported."))
                      "virtual FK rejection"
                  Expect.isFalse
                      (store.Catalog.[Fsdb.Storage.defaultDatabase].ContainsKey "child")
                      "rejected table stays absent"

          testCase "stored generated child columns restrict foreign-key actions"
          <| fun _ ->
              for action, expectedError in
                  [ "ON DELETE CASCADE", None
                    "ON UPDATE RESTRICT", None
                    "ON UPDATE CASCADE", Some 3104
                    "ON DELETE SET NULL", Some 3104
                    "ON UPDATE SET NULL", Some 3104 ] do
                  let store = Fsdb.Storage.create()
                  let session = create 1 store
                  let session, parentResult = handle session "CREATE TABLE parent(id INT PRIMARY KEY)"
                  Expect.isNone (errorInfo parentResult) "parent setup"
                  let childSql =
                      sprintf
                          "CREATE TABLE child(id INT,g INT AS(id+1) STORED,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) %s)"
                          action
                  let session, childResult = handle session childSql
                  Expect.equal
                      (errorInfo childResult |> Option.map (fun error -> error.Code, error.State, error.Message))
                      (expectedError
                       |> Option.map (fun code ->
                           code, "HY000", sprintf "Cannot define foreign key with %s clause on a generated column." action))
                      action
                  Expect.equal
                      (store.Catalog.[Fsdb.Storage.defaultDatabase].ContainsKey "child")
                      expectedError.IsNone
                      "rejected table stays absent"
                  if action = "ON DELETE CASCADE" then
                      let session, parentInsert = handle session "INSERT INTO parent VALUES(2)"
                      Expect.isNone (errorInfo parentInsert) "referenced row setup"
                      let session, childInsert = handle session "INSERT INTO child(id) VALUES(1)"
                      Expect.isNone (errorInfo childInsert) "generated child row setup"
                      let session, deletion = handle session "DELETE FROM parent WHERE id=2"
                      Expect.isNone (errorInfo deletion) "cascade delete"
                      let _, remaining = handle session "SELECT COUNT(*) FROM child"
                      Expect.equal remaining (ResultSet([ "COUNT(*)" ], [ [ Some "0" ] ])) "stored generated child cascades"

              let session = create 1 (Fsdb.Storage.create())
              let session, _ = handle session "CREATE TABLE parent(id INT PRIMARY KEY)"
              let _, virtualResult =
                  handle session
                      "CREATE TABLE child(id INT,g INT AS(id+1) VIRTUAL,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON UPDATE CASCADE)"
              Expect.equal (errorInfo virtualResult |> Option.map _.Code) (Some 3104) "action error precedes virtual-column error"

              let session = create 1 (Fsdb.Storage.create())
              let session, parentResult =
                  handle session "CREATE TABLE parent(id INT PRIMARY KEY,g INT AS(id+1) STORED,UNIQUE KEY(g))"
              Expect.isNone (errorInfo parentResult) "generated parent setup"
              let _, childResult =
                  handle session "CREATE TABLE child(x INT,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(g) ON UPDATE CASCADE)"
              Expect.isNone (errorInfo childResult) "stored generated parent accepts update action"

          testCase "ALTER keeps SET NULL child columns nullable"
          <| fun _ ->
              for action in [ "ON DELETE SET NULL"; "ON UPDATE SET NULL" ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for sql in
                      [ "CREATE TABLE parent(id INT PRIMARY KEY)"
                        sprintf "CREATE TABLE child(id INT PRIMARY KEY,p_id INT,CONSTRAINT fk_cp FOREIGN KEY(p_id) REFERENCES parent(id) %s)" action ] do
                      Expect.isNone (run sql |> errorInfo) sql

                  let reject = "ALTER TABLE child MODIFY p_id INT NOT NULL"
                  Expect.equal
                      (run reject |> errorInfo |> Option.map (fun error -> error.Code, error.State, error.Message))
                      (Some(1830, "HY000", "Column 'p_id' cannot be NOT NULL: needed in a foreign key constraint 'fk_cp' SET NULL"))
                      action
                  Expect.isTrue (store.Catalog.[Fsdb.Storage.defaultDatabase].["child"].Columns.[1].Nullable) "failed ALTER is atomic"

                  let removeAction = "ALTER TABLE child DROP FOREIGN KEY fk_cp,MODIFY p_id INT NOT NULL"
                  Expect.isNone (run removeAction |> errorInfo) removeAction
                  Expect.isFalse (store.Catalog.[Fsdb.Storage.defaultDatabase].["child"].Columns.[1].Nullable) "column becomes required"

          testCase "BIT and binary foreign-key columns follow the MySQL type family"
          <| fun _ ->
              for childType, parentType, expectedError in
                  [ "BIT(9)", "BINARY(1)", None
                    "BINARY(1)", "BIT(8)", None
                    "BIT(16)", "VARBINARY(1)", None
                    "VARBINARY(2)", "BIT(1)", None
                    "BIT(8)", "CHAR(1)", Some 3780 ] do
                  let store = Fsdb.Storage.create()
                  let session = create 1 store
                  let parentSql = sprintf "CREATE TABLE parent(x %s NOT NULL UNIQUE)" parentType
                  let childSql = sprintf "CREATE TABLE child(x %s,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x))" childType
                  let session, parentResult = handle session parentSql
                  Expect.isNone (errorInfo parentResult) parentSql
                  let _, childResult = handle session childSql
                  Expect.equal
                      (errorInfo childResult |> Option.map _.Code)
                      expectedError
                      (sprintf "%s references %s" childType parentType)

          testCase "YEAR and unsigned TINYINT foreign keys compare stored bytes"
          <| fun _ ->
              for parentType, childType, parentValue, childValue, newParentValue, expectedChild in
                  [ "YEAR", "TINYINT UNSIGNED", "2024", "124", "2025", "125"
                    "TINYINT UNSIGNED", "YEAR", "124", "2024", "125", "2025" ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for sql in
                      [ sprintf "CREATE TABLE parent(x %s PRIMARY KEY)" parentType
                        sprintf "CREATE TABLE child(x %s,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x) ON UPDATE CASCADE ON DELETE CASCADE)" childType
                        sprintf "INSERT INTO parent VALUES(%s),(0)" parentValue
                        sprintf "INSERT INTO child VALUES(%s),(0)" childValue
                        sprintf "UPDATE parent SET x=%s WHERE x=%s" newParentValue parentValue ] do
                      Expect.isNone (run sql |> errorInfo) sql
                  Expect.equal
                      (run "SELECT x FROM child ORDER BY x")
                      (ResultSet([ "x" ], [ [ Some "0" ]; [ Some expectedChild ] ]))
                      "cascade maps the stored key"
                  Expect.isNone (run "DELETE FROM parent WHERE x=0" |> errorInfo) "delete cascades across the type pair"
                  Expect.equal (run "SELECT COUNT(*) FROM child") (ResultSet([ "COUNT(*)" ], [ [ Some "1" ] ])) "zero child removed"

          testCase "TIME foreign keys accept date-time types without matching clock fields"
          <| fun _ ->
              for parentType, childType, parentValue, childValue in
                  [ "DATETIME", "TIME", "2024-01-01 12:34:56", "12:34:56"
                    "TIME", "DATETIME", "12:34:56", "2024-01-01 12:34:56"
                    "TIMESTAMP(2)", "TIME(4)", "2024-01-01 12:34:56.12", "12:34:56.1200"
                    "TIME(4)", "TIMESTAMP(2)", "12:34:56.1200", "2024-01-01 12:34:56.12" ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  let parentSql = sprintf "CREATE TABLE parent(x %s PRIMARY KEY)" parentType
                  let childSql = sprintf "CREATE TABLE child(x %s,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x))" childType
                  Expect.isNone (run parentSql |> errorInfo) parentSql
                  Expect.isNone (run childSql |> errorInfo) childSql
                  Expect.isNone (run (sprintf "INSERT INTO parent VALUES('%s')" parentValue) |> errorInfo) "parent value"
                  Expect.equal
                      (run (sprintf "INSERT INTO child VALUES('%s')" childValue) |> errorInfo |> Option.map _.Code)
                      (Some 1452)
                      "clock fields do not form a foreign-key match"

          testCase "ENUM and SET foreign keys require matching storage widths"
          <| fun _ ->
              let declaration kind count =
                  [ 0 .. count - 1 ]
                  |> List.map (sprintf "'v%d'")
                  |> String.concat ","
                  |> sprintf "%s(%s)" kind
              for childType, parentType, expectedError in
                  [ declaration "SET" 8, declaration "ENUM" 2, None
                    declaration "SET" 9, declaration "ENUM" 2, Some 3780
                    declaration "SET" 9, declaration "SET" 8, Some 3780
                    declaration "ENUM" 256, declaration "ENUM" 255, Some 3780 ] do
                  let session = create 1 (Fsdb.Storage.create())
                  let parentSql = sprintf "CREATE TABLE parent(x %s NOT NULL UNIQUE)" parentType
                  let childSql = sprintf "CREATE TABLE child(x %s,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x))" childType
                  let session, parentResult = handle session parentSql
                  Expect.isNone (errorInfo parentResult) "parent setup"
                  let _, childResult = handle session childSql
                  Expect.equal
                      (errorInfo childResult |> Option.map _.Code)
                      expectedError
                      (sprintf "%s references %s" childType parentType)

          testCase "foreign-key ALTER protects fixed-width column representations"
          <| fun _ ->
              for oldType, newType, parentType, expectedError in
                  [ "BIT(8)", "BIT(9)", "BIT(8)", Some 1832
                    "BINARY(1)", "BINARY(2)", "BINARY(1)", Some 1832
                    "CHAR(1)", "CHAR(2)", "CHAR(1)", Some 1832
                    "TIME(0)", "TIME(6)", "TIME(0)", Some 1832
                    "DATETIME(0)", "DATETIME(6)", "TIMESTAMP(0)", Some 1832
                    "DECIMAL(10,2)", "DECIMAL(11,2)", "DECIMAL(10,2)", Some 1832
                    "VARBINARY(1)", "VARBINARY(2)", "VARBINARY(1)", None
                    "VARCHAR(1)", "VARCHAR(2)", "VARCHAR(1)", None
                    "ENUM('a','b')", "ENUM('a','b','c')", "ENUM('a','b')", None
                    "ENUM('a','b')", "ENUM('a','c')", "ENUM('a','b')", Some 1832
                    "SET('a','b')", "SET('a','b','c')", "SET('a','b')", None
                    "SET('a','b')", "SET('a','c')", "SET('a','b')", Some 1832 ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  Expect.isNone
                      (run (sprintf "CREATE TABLE parent(x %s NOT NULL UNIQUE)" parentType) |> errorInfo)
                      (sprintf "%s parent setup" parentType)
                  Expect.isNone
                      (run (sprintf "CREATE TABLE child(x %s,CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x))" oldType) |> errorInfo)
                      (sprintf "%s child setup against %s" oldType parentType)
                  let result = run (sprintf "ALTER TABLE child MODIFY COLUMN x %s" newType)
                  Expect.equal
                      (errorInfo result |> Option.map _.Code)
                      expectedError
                      (sprintf "%s to %s" oldType newType)

              let store = Fsdb.Storage.create()
              let mutable session = create 1 store
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              Expect.isNone (run "CREATE TABLE parent(x BIT(8) NOT NULL UNIQUE)" |> errorInfo) "parent setup"
              Expect.isNone
                  (run "CREATE TABLE child(x BIT(8),CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x))" |> errorInfo)
                  "child setup"
              Expect.equal
                  (run "ALTER TABLE parent MODIFY COLUMN x BIT(9) NOT NULL UNIQUE"
                   |> errorInfo
                   |> Option.map (fun error -> error.Code, error.State, error.Message))
                  (Some(1833, "HY000", "Cannot change column 'x': used in a foreign key constraint 'fk' of table 'fsdb.child'"))
                  "referenced column cannot change representation"

              for alter in
                  [ "MODIFY COLUMN x BIT(9),ADD CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x)"
                    "ADD CONSTRAINT fk FOREIGN KEY(x) REFERENCES parent(x),MODIFY COLUMN x BIT(9)" ] do
                  let mutable session = create 1 (Fsdb.Storage.create())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  Expect.isNone (run "CREATE TABLE parent(x BIT(8) NOT NULL UNIQUE)" |> errorInfo) "parent setup"
                  Expect.isNone (run "CREATE TABLE child(x BIT(8))" |> errorInfo) "child setup"
                  Expect.isNone (run ("ALTER TABLE child " + alter) |> errorInfo) alter

          testCase "Foreign-key additions bind to the final column definition"
          <| fun _ ->
              for sql in
                  [ "ALTER TABLE child ADD CONSTRAINT added FOREIGN KEY(c) REFERENCES parent(n),ADD COLUMN c INT"
                    "ALTER TABLE child ADD COLUMN c INT,ADD CONSTRAINT added FOREIGN KEY(c) REFERENCES parent(n)"
                    "ALTER TABLE child ADD CONSTRAINT added FOREIGN KEY(c) REFERENCES parent(n),RENAME COLUMN b TO c" ] do
                  let mutable session = create 1 (Fsdb.Storage.create())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for setup in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                        "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                        "INSERT INTO parent VALUES(1)" ] do
                      Expect.isNone (run setup |> errorInfo) setup
                  Expect.isNone (run sql |> errorInfo) sql
                  Expect.isNone (run "INSERT INTO child(a,c) VALUES(1,1)" |> errorInfo) "valid final-column reference"
                  Expect.equal (run "INSERT INTO child(a,c) VALUES(1,2)" |> errorInfo |> Option.map _.Code) (Some 1452) "new constraint enforces the final column"

          testCase "Foreign-key rename collisions reject the complete statement atomically"
          <| fun _ ->
              for sql in
                  [ "RENAME TABLE child TO renamed"
                    "ALTER TABLE child RENAME TO renamed"
                    "RENAME TABLE parent TO parent_new,child TO renamed" ] do
                  let store = Fsdb.Storage.create()
                  let mutable session = create 1 store
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for setup in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                        "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))" ] do
                      Expect.isNone (run setup |> errorInfo) setup
                  let before = store.Catalog
                  Expect.equal
                      (run sql |> errorInfo |> Option.map (fun error -> error.Code, error.State, error.Message))
                      (Some(1826, "HY000", "Duplicate foreign key constraint name 'renamed_ibfk_1'")) sql
                  let tables = store.Catalog.[Fsdb.Storage.defaultDatabase]
                  Expect.isTrue (tables.ContainsKey "parent" && tables.ContainsKey "child" && tables.ContainsKey "other") "all original names survive"
                  Expect.isFalse (tables.ContainsKey "renamed" || tables.ContainsKey "parent_new") "no partial rename is published"
                  Expect.equal tables.["child"].ForeignKeys before.[Fsdb.Storage.defaultDatabase].["child"].ForeignKeys "constraint identity is unchanged"

          testCase "Generated foreign-key indexes are replaced unless explicitly renamed"
          <| fun _ ->
              for preparation, expected in
                  [ [], [ "replacement"; "replacement" ]
                    [ "ALTER TABLE child DROP FOREIGN KEY fk" ], [ "replacement"; "replacement" ]
                    [ "ALTER TABLE child RENAME INDEX fk TO renamed" ], [ "renamed"; "replacement"; "replacement" ] ] do
                  let mutable session = create 1 (Fsdb.Storage.create())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for sql in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                        "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))" ]
                      @ preparation @ [ "ALTER TABLE child ADD INDEX replacement(a,b)" ] do
                      Expect.isNone (run sql |> errorInfo) sql
                  match run "SHOW INDEX FROM child" with
                  | ResultSet(_, rows) -> Expect.equal (rows |> List.map (List.item 2)) (expected |> List.map Some) "native index retention"
                  | result -> failtestf "expected indexes, got %A" result

          testCase "An explicit index can replace a generated index under the same name"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD INDEX fk(a,b)" ] do
                  Expect.isNone (run sql |> errorInfo) sql
              match run "SHOW INDEX FROM child" with
              | ResultSet(_, rows) ->
                  Expect.equal (rows |> List.map (List.item 4)) [ Some "a"; Some "b" ] "one replacement index survives"
              | result -> failtestf "expected replacement index, got %A" result

          testCase "A new primary key replaces generated foreign-key indexes and reports nonnullable columns"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD PRIMARY KEY(a)" ] do
                  Expect.isNone (run sql |> errorInfo) sql
              match run "SHOW INDEX FROM child" with
              | ResultSet(_, [ row ]) ->
                  Expect.equal row.[2] (Some "PRIMARY") "generated index is replaced"
                  Expect.equal row.[9] (Some "") "primary key cannot contain NULL"
              | result -> failtestf "expected primary index, got %A" result
              Expect.equal
                  (run "ALTER TABLE child ADD PRIMARY KEY(b)" |> errorInfo |> Option.map (fun error -> error.Code, error.State))
                  (Some(1068, "42000"))
                  "duplicate primary key retains its diagnostic"

          testCase "Foreign keys protect their last child and parent index even with checks disabled"
          <| fun _ ->
              for checks in [ "0"; "1" ] do
                  for sql, name in
                      [ "ALTER TABLE child DROP INDEX fk", "fk"
                        "DROP INDEX fk ON child", "fk"
                        "ALTER TABLE parent DROP PRIMARY KEY", "PRIMARY" ] do
                      let mutable session = create 1 (Fsdb.Storage.create ())
                      let run sql =
                          let next, result = handle session sql
                          session <- next
                          result
                      for setup in
                          [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                            "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                            "SET foreign_key_checks=" + checks ] do
                          Expect.isNone (run setup |> errorInfo) setup
                      Expect.equal (run sql |> errorInfo |> Option.map (fun error -> error.Code, error.State, error.Message))
                          (Some(1553, "HY000", sprintf "Cannot drop index '%s': needed in a foreign key constraint" name)) sql
                      let table = if name = "PRIMARY" then "parent" else "child"
                      match run ("SHOW INDEX FROM " + table) with
                      | ResultSet(_, rows) -> Expect.equal (rows |> List.map (List.item 2)) [ Some name ] "rejected ALTER preserves the index"
                      | result -> failtestf "expected surviving index, got %A" result

          testCase "Foreign-key index drops validate the complete ALTER definition"
          <| fun _ ->
              for sql in
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,DROP INDEX fk"
                    "ALTER TABLE child DROP INDEX fk,DROP FOREIGN KEY fk"
                    "ALTER TABLE child DROP INDEX fk,ADD INDEX replacement(a,b)"
                    "ALTER TABLE parent DROP PRIMARY KEY,ADD UNIQUE KEY replacement(n)" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, _ = handle session "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                  let _, result = handle session sql
                  Expect.isNone (errorInfo result) sql

          testCase "Foreign keys create or reuse native supporting indexes"
          <| fun _ ->
              for definition, expected in
                  [ "FOREIGN KEY(a) REFERENCES parent(n)", [ "a" ]
                    "CONSTRAINT fk FOREIGN KEY ix(a) REFERENCES parent(n)", [ "fk" ]
                    "FOREIGN KEY ix(a) REFERENCES parent(n)", [ "ix" ]
                    "KEY existing(a,b),CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n)", [ "existing" ]
                    "CONSTRAINT first_fk FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT second_fk FOREIGN KEY(a) REFERENCES parent(n)", [ "second_fk" ]
                    "KEY a(b),FOREIGN KEY(a) REFERENCES parent(n)", [ "a"; "a_2" ] ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, result = handle session ("CREATE TABLE child(a INT,b INT," + definition + ")")
                  Expect.isNone (errorInfo result) "definition accepted"
                  match handle session "SHOW INDEX FROM child" |> snd with
                  | ResultSet(_, rows) ->
                      Expect.equal (rows |> List.map (List.item 2) |> List.distinct) (expected |> List.map Some) "supporting index names"
                  | result -> failtestf "expected indexes, got %A" result

          testCase "Duplicate foreign-key index names precede constraint collisions"
          <| fun _ ->
              for statement in
                  [ "CREATE TABLE child(a INT,b INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT shared FOREIGN KEY(b) REFERENCES parent(n))"
                    "ALTER TABLE other ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),ADD CONSTRAINT shared FOREIGN KEY(b) REFERENCES parent(n)" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, _ = handle session "CREATE TABLE other(a INT,b INT)"
                  let _, result = handle session statement
                  Expect.equal (errorInfo result |> Option.map (fun error -> error.Code, error.State, error.Message))
                      (Some(1061, "42000", "Duplicate key name 'shared'")) "index diagnostics precede foreign-key name validation"

          testCase "Foreign key names remain reserved across an ALTER and across tables"
          <| fun _ ->
              for sql in
                  [ "CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT SHARED FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)"
                    "ALTER TABLE child DROP FOREIGN KEY shared,ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)"
                    "ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),DROP FOREIGN KEY shared" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, _ = handle session "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                  let session, _ = handle session "SET foreign_key_checks=0"
                  let _, result = handle session sql
                  Expect.equal (errorInfo result |> Option.map (fun error -> error.Code, error.State)) (Some(1826, "HY000")) sql

          testCase "UPDATE observes and retains writes from assignment and predicate functions"
          <| fun _ ->
              for statement, affected, value in
                  [ "UPDATE child SET n=promote_parent()", 3UL, "3"
                    "UPDATE IGNORE child SET n=promote_parent()", 3UL, "3"
                    "UPDATE child SET n=3 WHERE promote_parent()=3", 3UL, "3"
                    "UPDATE child SET n=3 WHERE promote_parent()=0", 0UL, "1"
                    "UPDATE child JOIN (SELECT 1 AS seed) AS one ON 1=1 SET child.n=promote_parent()", 3UL, "3"
                    "UPDATE child JOIN (SELECT 1 AS seed) AS one ON promote_parent()=3 SET child.n=3", 3UL, "3" ] do
                  let mutable session = create 1 (Fsdb.Storage.create ())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for sql in routineUpdateSetup promoteParent do
                      Expect.isNone (run sql |> errorInfo) sql
                  Expect.equal (run statement) (Affected affected) "function writes precede the constraint checks"
                  Expect.isEmpty session.Diagnostics "all references are valid"
                  Expect.equal (run "SELECT * FROM parent ORDER BY n")
                      (ResultSet([ "n" ], [ [ Some "1" ]; [ Some "3" ] ])) "function write survives publication"
                  Expect.equal (run "SELECT n FROM child ORDER BY id")
                      (ResultSet([ "n" ], List.replicate 3 [ Some value ])) "outer writes survive publication"

          testCase "UPDATE rolls back earlier function writes when a later invocation fails"
          <| fun _ ->
              for explicitTransaction in [ false; true ] do
                  for statement in
                      [ "UPDATE child SET n=promote_parent(id) ORDER BY id"
                        "UPDATE IGNORE child SET n=promote_parent(id) ORDER BY id"
                        "UPDATE child SET n=3 WHERE promote_parent(id)=3"
                        "UPDATE child JOIN (SELECT 1 AS seed) AS one ON 1=1 SET child.n=promote_parent(id)"
                        "UPDATE child JOIN (SELECT 1 AS seed) AS one ON promote_parent(child.id)=3 SET child.n=3" ] do
                      let mutable session = create 1 (Fsdb.Storage.create ())
                      let run sql =
                          let next, result = handle session sql
                          session <- next
                          result
                      let functionSql = "CREATE FUNCTION promote_parent(input_id INT) RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN UPDATE parent SET n=3 WHERE n=2; IF input_id=2 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='stop'; END IF; RETURN 3; END"
                      for sql in routineUpdateSetup functionSql do
                          Expect.isNone (run sql |> errorInfo) sql
                      if explicitTransaction then Expect.equal (run "START TRANSACTION") (Affected 0UL) "begin"
                      let result = run statement
                      Expect.equal (errorInfo result |> Option.map (fun error -> error.Code, error.State)) (Some(1644, "45000")) "function error aborts the outer statement"
                      let expectOriginalRows () =
                          Expect.equal (run "SELECT * FROM parent ORDER BY n")
                              (ResultSet([ "n" ], [ [ Some "1" ]; [ Some "2" ] ])) "function writes roll back"
                          Expect.equal (run "SELECT n FROM child ORDER BY id")
                              (ResultSet([ "n" ], List.replicate 3 [ Some "1" ])) "earlier outer writes roll back"
                      expectOriginalRows ()
                      if explicitTransaction then
                          Expect.equal (run "COMMIT") (Affected 0UL) "transaction remains usable"
                          expectOriginalRows ()

          testCase "UPDATE function writes remain private until commit and obey rollback at every isolation level"
          <| fun _ ->
              for isolation in [ "REPEATABLE READ"; "READ COMMITTED"; "READ UNCOMMITTED"; "SERIALIZABLE" ] do
                  for finish in [ "COMMIT"; "ROLLBACK" ] do
                      let store = Fsdb.Storage.create ()
                      let mutable session = create 1 store
                      let run sql =
                          let next, result = handle session sql
                          session <- next
                          result
                      for sql in routineUpdateSetup promoteParent do
                          Expect.isNone (run sql |> errorInfo) sql
                      Expect.equal (run ("SET SESSION TRANSACTION ISOLATION LEVEL " + isolation)) (Affected 0UL) "isolation"
                      Expect.equal (run "START TRANSACTION") (Affected 0UL) "begin"
                      Expect.equal (run "UPDATE IGNORE child SET n=promote_parent()") (Affected 3UL) "nested writes succeed"
                      let observe sql = handle (create 2 store) sql |> snd
                      Expect.equal (observe "SELECT * FROM parent ORDER BY n")
                          (ResultSet([ "n" ], [ [ Some "1" ]; [ Some "2" ] ])) "function writes are private"
                      Expect.equal (observe "SELECT n FROM child ORDER BY id")
                          (ResultSet([ "n" ], List.replicate 3 [ Some "1" ])) "outer writes are private"
                      Expect.equal (run finish) (Affected 0UL) "finish"
                      let parent, child = if finish = "COMMIT" then "3", "3" else "2", "1"
                      Expect.equal (observe "SELECT * FROM parent ORDER BY n")
                          (ResultSet([ "n" ], [ [ Some "1" ]; [ Some parent ] ])) "function write follows the transaction"
                      Expect.equal (observe "SELECT n FROM child ORDER BY id")
                          (ResultSet([ "n" ], List.replicate 3 [ Some child ])) "outer writes follow the transaction"

          testCase "UPDATE IGNORE evaluates rejected candidates only once"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create ())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "SET @calls=0" ] do
                  Expect.isNone (run sql |> errorInfo) sql
              Expect.equal (run "UPDATE IGNORE child SET n=(@calls:=@calls+1) ORDER BY id") (Affected 1UL) "valid changed row survives rejection"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1287; 1452 ] "syntax warning precedes the rejected row"
              Expect.equal (run "SELECT @calls") (ResultSet([ "@calls" ], [ [ Some "3" ] ])) "one evaluation per selected row"
              Expect.equal (run "SELECT * FROM child ORDER BY id")
                  (ResultSet([ "id"; "n" ], [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "3" ] ])) "only rejected candidate is skipped"

          testCase "ALTER foreign key numbering uses the original table definition"
          <| fun _ ->
              for initial, alter, expected in
                  [ "CONSTRAINT child_ibfk_7 FOREIGN KEY(a) REFERENCES parent(n)", "ADD FOREIGN KEY(b) REFERENCES parent(n)", [ "child_ibfk_7"; "child_ibfk_8" ]
                    "CONSTRAINT child_ibfk_07 FOREIGN KEY(a) REFERENCES parent(n)", "ADD FOREIGN KEY(b) REFERENCES parent(n)", [ "child_ibfk_07"; "child_ibfk_1" ]
                    "FOREIGN KEY(a) REFERENCES parent(n)", "DROP FOREIGN KEY child_ibfk_1,ADD FOREIGN KEY(b) REFERENCES parent(n)", [ "child_ibfk_2" ] ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, _ = handle session ("CREATE TABLE child(a INT,b INT," + initial + ")")
                  let session, result = handle session ("ALTER TABLE child " + alter)
                  Expect.isNone (errorInfo result) "ALTER accepted"
                  let _, result = handle session "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fsdb' ORDER BY CONSTRAINT_NAME"
                  Expect.equal result (ResultSet([ "CONSTRAINT_NAME" ], expected |> List.map (fun name -> [ Some name ]))) "resolved names"

          testCase "CREATE generates foreign key names from the owning table"
          <| fun _ ->
              for definition, expected in
                  [ "a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT child_ibfk_7 FOREIGN KEY(b) REFERENCES parent(n)", [ [ Some "child_ibfk_1" ]; [ Some "child_ibfk_7" ] ]
                    "a INT,FOREIGN KEY key_label(a) REFERENCES parent(n)", [ [ Some "child_ibfk_1" ] ] ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
                  let session, result = handle session ("CREATE TABLE child(" + definition + ")")
                  Expect.isNone (errorInfo result) "CREATE accepted"
                  let _, result = handle session "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fsdb' ORDER BY CONSTRAINT_NAME"
                  Expect.equal result (ResultSet([ "CONSTRAINT_NAME" ], expected)) "constraint names are independent of index labels"

          testCase "UPDATE IGNORE skips rejected rows and preserves trigger order"
          <| fun _ ->
              for statement in
                  [ "UPDATE IGNORE child SET n=id ORDER BY id"
                    "UPDATE IGNORE child JOIN parent ON parent.n=child.n SET child.n=child.id" ] do
                  let mutable session = create 1 (Fsdb.Storage.create ())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  for sql in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                        "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                        "INSERT INTO parent VALUES(1),(3)"
                        "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                        "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                        "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                        "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)" ] do
                      Expect.isNone (run sql |> errorInfo) sql
                  Expect.equal (run statement) (Affected 1UL) "only row three changes"
                  Expect.equal (session.Diagnostics |> List.map _.Code) [ 1452 ] "orphan becomes a warning"
                  Expect.equal (run "SELECT * FROM child ORDER BY id")
                      (ResultSet([ "id"; "n" ], [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "3" ] ])) "accepted rows survive"
                  Expect.equal (run "SELECT * FROM audit")
                      (ResultSet([ "phase"; "id" ],
                          [ [ Some "before"; Some "1" ]; [ Some "after"; Some "1" ]
                            [ Some "before"; Some "2" ]; [ Some "before"; Some "3" ]; [ Some "after"; Some "3" ] ]))
                      "BEFORE survives rejection and AFTER fires only for accepted rows, including no-ops"

          testCase "Self-referencing writes can use their own candidate as parent"
          <| fun _ ->
              for sql, expected in
                  [ "INSERT INTO child VALUES(1,1),(2,1),(3,3)", [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "3" ] ]
                    "REPLACE INTO child VALUES(1,1)", [ [ Some "1"; Some "1" ] ]
                    "INSERT INTO child VALUES(1,1) ON DUPLICATE KEY UPDATE p=VALUES(p)", [ [ Some "1"; Some "1" ] ] ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                  let session, result = handle session sql
                  Expect.isNone (errorInfo result) sql
                  let _, result = handle session "SELECT * FROM child ORDER BY n"
                  Expect.equal result (ResultSet([ "n"; "p" ], expected)) "accepted self references"

          testCase "Ignored self-referencing rows cannot supply parent keys"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
              let session, result = handle session "INSERT IGNORE INTO child VALUES(1,2),(2,1),(3,3)"
              Expect.equal result (Affected 1UL) "only the valid self-reference survives"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1452; 1452 ] "both orphan rows warn"
              let _, result = handle session "SELECT * FROM child"
              Expect.equal result (ResultSet([ "n"; "p" ], [ [ Some "3"; Some "3" ] ])) "failed candidates do not enter the lookup"

          testCase "Self-referencing updates can introduce their own parent key"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
              let session, _ = handle session "INSERT INTO child VALUES(1,NULL)"
              let session, result = handle session "UPDATE child SET n=2,p=2"
              Expect.equal result (Affected 1UL) "candidate satisfies its own reference"
              let _, result = handle session "SELECT * FROM child"
              Expect.equal result (ResultSet([ "n"; "p" ], [ [ Some "2"; Some "2" ] ])) "updated row"

          testCase "Child foreign key diagnostics retain the complete constraint"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE parent(n INT PRIMARY KEY)"
              let session, _ = handle session "CREATE TABLE child(n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n) ON DELETE NO ACTION ON UPDATE CASCADE)"
              let session, result = handle session "INSERT INTO child VALUES(2)"
              let message = "Cannot add or update a child row: a foreign key constraint fails (`fsdb`.`child`, CONSTRAINT `Fk_Child` FOREIGN KEY (`n`) REFERENCES `parent` (`n`) ON UPDATE CASCADE)"
              Expect.equal (errorInfo result |> Option.map (fun error -> error.Code, error.State, error.Message))
                  (Some(1452, "23000", message)) "full child-side error"
              Expect.equal (session.Diagnostics |> List.map _.Message) [ message ] "SHOW WARNINGS uses the same error"

          testCase "Missing foreign key parents reject new references after checks are enabled"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create ())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,payload INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1,0)"
                    "SET foreign_key_checks=0"
                    "DROP TABLE parent"
                    "SET foreign_key_checks=1" ] do
                  Expect.isNone (run sql |> errorInfo) sql
              Expect.equal (run "INSERT INTO child VALUES(2,2,0)" |> errorInfo |> Option.map _.Code) (Some 1452) "new reference rejected"
              Expect.equal (run "UPDATE child SET id=2" |> errorInfo |> Option.map _.Code) (Some 1452) "primary-key change rechecks the reference"
              Expect.equal (run "UPDATE child SET payload=7") (Affected 1UL) "unrelated payload can change"
              Expect.equal (run "INSERT IGNORE INTO child VALUES(2,2,0),(3,NULL,0)") (Affected 1UL) "ignored orphan does not block the NULL reference"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1452 ] "ignored orphan warning"
              Expect.equal (run "SELECT id FROM child ORDER BY id")
                  (ResultSet([ "id" ], [ [ Some "1" ]; [ Some "3" ] ])) "rejected rows are not published"

          testCase "Foreign key revalidation follows changes to its supporting index"
          <| fun _ ->
              for indexed in [ false; true ] do
                  let mutable session = create 1 (Fsdb.Storage.create ())
                  let run sql =
                      let next, result = handle session sql
                      session <- next
                      result
                  let index = if indexed then ",KEY cover(n,payload)" else ""
                  for sql in
                      [ "CREATE TABLE parent(n INT PRIMARY KEY)"
                        "CREATE TABLE child(id INT PRIMARY KEY,n INT,payload INT" + index + ",CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                        "INSERT INTO parent VALUES(1)"
                        "INSERT INTO child VALUES(1,1,0)"
                        "SET foreign_key_checks=0"
                        "DELETE FROM parent"
                        "SET foreign_key_checks=1" ] do
                      Expect.isNone (run sql |> errorInfo) sql
                  let result = run "UPDATE child SET payload=7"
                  if indexed then Expect.equal (errorInfo result |> Option.map _.Code) (Some 1452) "supporting index changed"
                  else Expect.equal result (Affected 1UL) "foreign key index unchanged"
                  Expect.equal (run "UPDATE child SET n=n") (Affected 0UL) "no-op does not revalidate an existing orphan"

          testCase "Quoted table targets retain dots and escaped backticks"
          <| fun _ ->
              for name in [ "Odd.Table"; "Odd`.Table" ] do
                  let quoted = "`" + name.Replace("`", "``") + "`"
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, result = handle session (sprintf "CREATE TABLE %s(n INT, UNIQUE KEY `Odd.Key`(n))" quoted)
                  Expect.isNone (errorInfo result) "quoted table created"
                  let session, result = handle session (sprintf "INSERT INTO %s VALUES(1)" quoted)
                  Expect.isNone (errorInfo result) "quoted table populated"
                  let session, result = handle session (sprintf "INSERT INTO %s VALUES(1)" quoted)
                  Expect.equal (errorInfo result |> Option.map _.Code) (Some 1062) "duplicate key"
                  Expect.equal (session.Diagnostics |> List.map _.Message)
                      [ sprintf "Duplicate entry '1' for key '%s.Odd.Key'" (name.ToLowerInvariant()) ] "literal dot survives diagnostics"
                  let _, result = handle session (sprintf "SELECT n FROM %s" quoted)
                  Expect.equal result (ResultSet([ "n" ], [ [ Some "1" ] ])) "same table resolved for reading"

          testCase "Quoted view writes and cross-schema renames retain table identity"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create ())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  Expect.isNone (errorInfo result) sql
                  result
              for sql in
                  [ "CREATE DATABASE other"
                    "CREATE TABLE `Odd.Table`(n INT)"
                    "CREATE VIEW `Odd.View` AS SELECT n FROM `Odd.Table`"
                    "INSERT INTO `Odd.View` VALUES(3)"
                    "ALTER TABLE `Odd.Table` RENAME TO other.`New.Table`" ] do
                  run sql |> ignore
              Expect.equal (run "SELECT n FROM other.`New.Table`")
                  (ResultSet([ "n" ], [ [ Some "3" ] ])) "view write and rename use the same physical table"

          testCase "Quoted dots remain distinct from database qualification"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create ())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE DATABASE Odd"
                    "CREATE TABLE Odd.Target(n INT)"
                    "CREATE TABLE `Odd.Target`(n INT)"
                    "INSERT INTO Odd.Target VALUES(1)"
                    "INSERT INTO `Odd.Target` VALUES(2)" ] do
                  Expect.isNone (run sql |> errorInfo) sql
              Expect.equal (run "SELECT n FROM Odd.Target") (ResultSet([ "n" ], [ [ Some "1" ] ])) "qualified target"
              Expect.equal (run "SELECT n FROM `Odd.Target`") (ResultSet([ "n" ], [ [ Some "2" ] ])) "literal dot"

          testCase "CHECK OPTION diagnostics normalize the view name"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE checked(n INT)"
              let session, _ = handle session "CREATE VIEW `Odd.View` AS SELECT n FROM checked WHERE n>0 WITH CHECK OPTION"
              let _, result = handle session "INSERT INTO `Odd.View` VALUES(-1)"
              Expect.equal (errorInfo result |> Option.map (fun error -> error.Code, error.Message))
                  (Some(1369, "CHECK OPTION failed 'fsdb.odd.view'")) "physical view identity"

          testCase "CREATE DATABASE reports one affected row even when it already exists"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "CREATE DATABASE counted"
              Expect.equal result (Affected 1UL) "created database"
              let _, count = handle session "SELECT ROW_COUNT()"
              Expect.equal count (ResultSet([ "ROW_COUNT()" ], [ [ Some "1" ] ])) "created count"
              let session, result = handle session "CREATE DATABASE IF NOT EXISTS counted"
              Expect.equal result (Affected 1UL) "existing database"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1007 ] "existing database note"
              let _, count = handle session "SELECT ROW_COUNT()"
              Expect.equal count (ResultSet([ "ROW_COUNT()" ], [ [ Some "1" ] ])) "existing count"

          testCase "SHOW INDEX declares numeric types for empty results"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE unindexed(n INT)"
              let session, result = handle session "SHOW INDEX FROM unindexed"
              match result with
              | ResultSet(_, rows) -> Expect.isEmpty rows "no index rows"
              | other -> failtestf "expected SHOW INDEX result, got %A" other
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId)
                  [ TypeVarString; TypeLong; TypeVarString; TypeLong; TypeVarString; TypeVarString
                    TypeLongLong; TypeLongLong; TypeNull; TypeVarString; TypeVarString
                    TypeVarString; TypeVarString; TypeVarString; TypeBlob ] "native declared types survive empty rows"

          testCase "Expression assignments warn per syntax occurrence"
          <| fun _ ->
              for sql, expected in
                  [ "SELECT @a:=1 AS value", 1
                    "SELECT @a:=(@b:=2) AS value", 2
                    "SELECT IF(0,@a:=1,2) AS value", 1
                    "SELECT @a:=1 AS value WHERE 0", 1
                    "SELECT @a:=n FROM (SELECT 1 AS n UNION ALL SELECT 2) t", 1
                    "SET @a:=1", 0
                    "SET @a=(@b:=1)", 1 ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, result = handle session sql
                  Expect.isNone (errorInfo result) sql
                  Expect.equal (session.Diagnostics |> List.map _.Code) (List.replicate expected 1287) sql

          testCase "Expression assignment deprecation follows literal introducers and precedes binding failure"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SELECT @a:=_utf8'x' AS value"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 3719; 1287 ] "literal warning precedes its enclosing assignment"
              let session, _ = handle session "SELECT @a:=absent"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1287; 1054 ] "assignment warning survives binding error"

          testCase "Prepared expression assignments warn during preparation only"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, prepared = handle session "PREPARE p FROM 'SELECT @a:=1 AS value'"
              Expect.isNone (errorInfo prepared) "prepared"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1287 ] "prepare warning"
              let session, first = handle session "EXECUTE p"
              Expect.equal first (ResultSet([ "value" ], [ [ Some "1" ] ])) "first execution"
              Expect.isEmpty session.Diagnostics "execution does not repeat syntax warning"
              let session, _ = handle session "EXECUTE p"
              Expect.isEmpty session.Diagnostics "repeated execution has no syntax warning"

          testCase "HEX rejects overflowing computed DOUBLE arguments"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, rendered in
                  [ "-1e30", "-(1e30)"
                    "CAST('1e30' AS DOUBLE)", "cast('1e30' as double)"
                    "1e30+0e0", "(1e30 + 0e0)"
                    "IF(1,CAST('1e30' AS DOUBLE),0e0)", "cast('1e30' as double)"
                    "(SELECT IF(1,CAST('1e30' AS DOUBLE),0e0))", "cast('1e30' as double)" ] do
                  let _, result = handle session (sprintf "SELECT HEX(%s)" expression)
                  Expect.equal result (Err(1690, sprintf "BIGINT value is out of range in '%s'" rendered)) expression
              let session, result = handle session "SELECT HEX(1e30) AS value"
              Expect.equal result (ResultSet([ "value" ], [ [ Some "7FFFFFFFFFFFFFFF" ] ])) "literal clamps"
              Expect.isEmpty session.Diagnostics "literal has no warning"

          testCase "HEX evaluates a selected conditional branch once"
          <| fun _ ->
              for expression in
                  [ "IF((@calls:=@calls+1),1e30,CAST('1e30' AS DOUBLE))"
                    "CASE (@calls:=@calls+1) WHEN 1 THEN 1e30 ELSE CAST('1e30' AS DOUBLE) END" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "SET @calls=0"
                  let session, result = handle session (sprintf "SELECT HEX(%s) AS value" expression)
                  Expect.equal result (ResultSet([ "value" ], [ [ Some "7FFFFFFFFFFFFFFF" ] ])) "unchosen overflowing branch is not evaluated"
                  let _, calls = handle session "SELECT @calls AS calls"
                  Expect.equal calls (ResultSet([ "calls" ], [ [ Some "1" ] ])) "condition evaluated once"

          testCase "HEX clamps stored DOUBLE overflow with an integer warning"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE hex_double(n DOUBLE)"
              let session, _ = handle session "INSERT INTO hex_double VALUES(1e30)"
              let session, result = handle session "SELECT HEX(n) AS value FROM hex_double"
              Expect.equal result (ResultSet([ "value" ], [ [ Some "7FFFFFFFFFFFFFFF" ] ])) "column clamps"
              Expect.equal (conditionTriples session)
                  [ warning 1292 "Truncated incorrect INTEGER value: '1e30'" ] "column warning"

          testCase "ALTER converts complete rows in final column order"
          <| fun _ ->
              for mode in [ ""; "STRICT_ALL_TABLES" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE row_conversion(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))"
                  let session, _ = handle session "INSERT INTO row_conversion VALUES(1,'aa','bb'),(2,'cc','dd')"
                  let session, _ = handle session (sprintf "SET sql_mode='%s'" mode)
                  let session, result = handle session "ALTER TABLE row_conversion MODIFY w VARCHAR(1), MODIFY v VARCHAR(1)"
                  let expected =
                      [ for row in (if mode = "" then [ 1; 2 ] else [ 1 ]) do
                            for column in [ "v"; "w" ] do
                                yield (if mode = "" then Fsdb.Diagnostics.Warning else Fsdb.Diagnostics.Error), 1265,
                                    sprintf "Data truncated for column '%s' at row %d" column row ]
                  Expect.equal (conditionTriples session) expected "final column order, stopping after the first failing row"
                  if mode = "" then Expect.equal result (Affected 2UL) "both rows copied"
                  else
                      Expect.equal result (Err(1265, "Data truncated for column 'v' at row 1")) "first conversion error returned"
                      let _, rows = handle session "SELECT v,w FROM row_conversion ORDER BY id"
                      Expect.equal rows (ResultSet([ "v"; "w" ], [ [ Some "aa"; Some "bb" ]; [ Some "cc"; Some "dd" ] ])) "failed conversion is atomic"

          testCase "ALTER retains typed conversion errors after the first strict failure"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE mixed_conversion(v VARCHAR(8),w VARCHAR(8))"
              let session, _ = handle session "INSERT INTO mixed_conversion VALUES('aa','x')"
              let session, _ = handle session "ALTER TABLE mixed_conversion MODIFY v VARCHAR(1), MODIFY w TINYINT"
              Expect.equal (session.Diagnostics |> List.map _.Message)
                  [ "Data truncated for column 'v' at row 1"; "Incorrect integer value: 'x' for column 'w' at row 1" ]
                  "secondary errors retain their target type and row"

          testCase "ALTER converts NULL primary-key values in final column order"
          <| fun _ ->
              for mode in [ ""; "STRICT_ALL_TABLES" ] do
                  let session = create 1 (Fsdb.Storage.create ())
                  let session, _ = handle session "CREATE TABLE null_primary(id INT,v VARCHAR(8))"
                  let session, _ = handle session "INSERT INTO null_primary VALUES(NULL,'aa')"
                  let session, _ = handle session (sprintf "SET sql_mode='%s'" mode)
                  let session, result = handle session "ALTER TABLE null_primary MODIFY v VARCHAR(1), ADD PRIMARY KEY(id)"
                  Expect.equal (session.Diagnostics |> List.map _.Message)
                      [ "Data truncated for column 'id' at row 1"; "Data truncated for column 'v' at row 1" ] "nullability conversion precedes text conversion"
                  if mode = "" then
                      Expect.equal result (Affected 1UL) "non-strict mode copies the row"
                      let _, rows = handle session "SELECT id,v FROM null_primary"
                      Expect.equal rows (ResultSet([ "id"; "v" ], [ [ Some "0"; Some "a" ] ])) "implicit zero replaces NULL"
                  else Expect.equal result (Err(1265, "Data truncated for column 'id' at row 1")) "strict mode returns the first error"

          testCase "ALTER validates later definitions before converting earlier columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE definition_order(v VARCHAR(8))"
              let session, _ = handle session "INSERT INTO definition_order VALUES('aa')"
              let session, result = handle session "ALTER TABLE definition_order MODIFY v VARCHAR(1), MODIFY absent VARCHAR(1)"
              Expect.equal result (Err(1054, "Unknown column 'absent' in 'definition_order'")) "definition error precedes truncation"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1054 ] "no conversion conditions"

          testCase "ALTER checks uniqueness after all columns of the conflicting row"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE row_unique(v VARCHAR(8),w VARCHAR(8),UNIQUE KEY uq(v))"
              let session, _ = handle session "INSERT INTO row_unique VALUES('aa','bb'),('ab','dd'),('zz','ff')"
              let session, _ = handle session "SET sql_mode=''"
              let session, result = handle session "ALTER TABLE row_unique MODIFY v VARCHAR(1), MODIFY w VARCHAR(1)"
              Expect.equal result (Err(1062, "Duplicate entry 'a' for key 'row_unique.uq'")) "duplicate rejected"
              Expect.equal (session.Diagnostics |> List.map _.Message)
                  [ "Data truncated for column 'v' at row 1"; "Data truncated for column 'w' at row 1"
                    "Data truncated for column 'v' at row 2"; "Data truncated for column 'w' at row 2"
                    "Duplicate entry 'a' for key 'row_unique.uq'" ] "no later rows converted"

          testCase "HEX preserves exact decimal rounding and reports signed overflow"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              for expression, expected, overflow in
                  [ "2.5", "3", false
                    "-2.5", "FFFFFFFFFFFFFFFD", false
                    "2.5e0", "2", false
                    "2.51e0", "3", false
                    "9223372036854775807.0", "7FFFFFFFFFFFFFFF", false
                    "9223372036854775807.5", "7FFFFFFFFFFFFFFF", true
                    "-9223372036854775808.5", "8000000000000000", true ] do
                  let actualSession, result = handle session (sprintf "SELECT HEX(%s) AS value" expression)
                  Expect.equal result (ResultSet([ "value" ], [ [ Some expected ] ])) expression
                  let expectedConditions =
                      if overflow then [ warning 1292 (sprintf "Truncated incorrect DECIMAL value: '%s'" expression) ]
                      else []
                  Expect.equal (conditionTriples actualSession) expectedConditions (expression + " conditions")

          testCase "TIME functions warn once per clamped argument"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result =
                  handle session "SELECT HOUR('838:59:59.9999995'), MINUTE('838:59:59.9999995'), TIME('839:00:00')"
              Expect.equal result (ResultSet([ "HOUR('838:59:59.9999995')"; "MINUTE('838:59:59.9999995')"; "TIME('839:00:00')" ], [ [ Some "838"; Some "59"; Some "838:59:59" ] ])) "clamped values"
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '838:59:59.9999995'"
                    warning 1292 "Truncated incorrect time value: '838:59:59.9999995'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'" ]
                  "one warning per clamped argument"
              let session, result = handle session "SELECT HOUR('838:59:59.0000004')"
              Expect.equal result (ResultSet([ "HOUR('838:59:59.0000004')" ], [ [ Some "838" ] ])) "rounded maximum"
              Expect.equal (conditionTriples session) [] "rounding into range does not warn"

          testCase "overflowing SEC_TO_TIME and MAKETIME retain MySQL warning values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result =
                  handle session "SELECT SEC_TO_TIME(3020400), SEC_TO_TIME(-3020400), MAKETIME(839,0,0), MAKETIME(-839,0,0)"
              match result with
              | ResultSet(_, [ [ Some "838:59:59"; Some "-838:59:59"; Some "838:59:59"; Some "-838:59:59" ] ]) -> ()
              | other -> failtestf "unexpected clamped TIME values: %A" other
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '3020400'"
                    warning 1292 "Truncated incorrect time value: '-3020400'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'"
                    warning 1292 "Truncated incorrect time value: '-839:00:00'" ]
                  "source values in overflow warnings"
              let session, _ = handle session "SELECT MAKETIME(839,1,2.5), MAKETIME(839,1,'2.500'), MAKETIME(839,1,2e0)"
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '839:01:02.5'"
                    warning 1292 "Truncated incorrect time value: '839:01:02.500000'"
                    warning 1292 "Truncated incorrect time value: '839:01:02.000000'" ]
                  "seconds precision in overflow warnings"

          testCase "TIME arithmetic reports overflowing result values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result =
                  handle session "SELECT ADDTIME('838:59:59','00:00:01'), SUBTIME('-838:59:59','00:00:01'), TIMEDIFF('838:59:59','-00:00:01')"
              match result with
              | ResultSet(_, [ [ Some "838:59:59"; Some "-838:59:59"; Some "838:59:59" ] ]) -> ()
              | other -> failtestf "unexpected TIME arithmetic values: %A" other
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '839:00:00'"
                    warning 1292 "Truncated incorrect time value: '-839:00:00'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'" ]
                  "whole-second arithmetic overflow"
              let session, _ =
                  handle session "SELECT ADDTIME('838:59:59','00:00:00.000001'), TIMEDIFF('838:59:59','-00:00:00.000001')"
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '838:59:59'"
                    warning 1292 "Truncated incorrect time value: '838:59:59.000001'" ]
                  "microsecond arithmetic overflow"

          testCase "TIME arithmetic clamps an invalid operand before calculating"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result =
                  handle session "SELECT SUBTIME('839:00:00','00:00:01'), TIMEDIFF('839:00:00','00:00:01')"
              match result with
              | ResultSet(_, [ [ Some "838:59:58"; Some "838:59:58" ] ]) -> ()
              | other -> failtestf "unexpected result after operand clamping: %A" other
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '839:00:00'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'" ]
                  "one warning for each invalid operand"
              let session, _ = handle session "SELECT ADDTIME('839:00:00','00:00:01')"
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '839:00:00'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'" ]
                  "operand and result overflows both warn"
              let session, result = handle session "SELECT TIMEDIFF('00:00:01','-839:00:00')"
              match result with
              | ResultSet(_, [ [ Some "838:59:59" ] ]) -> ()
              | other -> failtestf "unexpected clamped TIMEDIFF result: %A" other
              Expect.equal
                  (conditionTriples session)
                  [ warning 1292 "Truncated incorrect time value: '-839:00:00'"
                    warning 1292 "Truncated incorrect time value: '839:00:00'" ]
                  "invalid right operand and overflowing difference"

          testCase "SHOW WARNINGS LIMIT n is accepted, matching the mysql CLI's/mysqli's routine probe"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW WARNINGS LIMIT 10" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty warnings resultset, got %A" other

              match handle session "SHOW WARNINGS LIMIT 5, 10" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty warnings resultset with offset, got %A" other

          testCase "SHOW COUNT(*) WARNINGS / SHOW COUNT(*) ERRORS report a single zero row"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW COUNT(*) WARNINGS" |> snd with
              | ResultSet([ "@@session.warning_count" ], [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected @@session.warning_count = 0, got %A" other

              match handle session "SHOW COUNT(*) ERRORS" |> snd with
              | ResultSet([ "@@session.error_count" ], [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected @@session.error_count = 0, got %A" other

          testCase "max_error_count caps retained warnings while preserving the total count"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = create 1 store
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              run "CREATE TABLE t (n INT)" |> ignore
              run "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'" |> ignore
              run "SET SESSION max_error_count = 1" |> ignore
              Expect.equal (run "INSERT INTO t VALUES ('one'), ('two')") (Affected 2UL) "both rows insert"
              Expect.equal session.DiagnosticsCount 2 "both warnings count"
              Expect.equal session.Diagnostics.Length 1 "only one warning is retained"
              match run "SHOW COUNT(*) WARNINGS" with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected total warning count, got %A" other
              match run "SHOW WARNINGS" with
              | ResultSet(_, [ [ Some "Warning"; Some "1366"; _ ] ]) -> ()
              | other -> failtestf "expected one retained warning, got %A" other

          testCase "SHOW ERRORS is accepted like SHOW WARNINGS"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW ERRORS" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty errors resultset, got %A" other

          testCase "SHOW condition resultsets expose MySQL numeric wire metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SHOW WARNINGS"

              Expect.equal
                  (session.LastResultColumnMetadata |> List.map _.TypeId)
                  [ TypeVarString; TypeLong; TypeVarString ]
                  "the condition code is an INT"

              let session, _ = handle session "SHOW COUNT(*) WARNINGS"

              Expect.equal
                  (session.LastResultColumnMetadata |> List.map _.TypeId)
                  [ TypeLongLong ]
                  "the condition count is a BIGINT"

              Expect.isTrue
                  (session.LastResultColumnMetadata.Head.Flags &&& UnsignedFlag <> 0us)
                  "the condition count is unsigned"

          testCase "conditional schema and table DDL records MySQL notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE diagnostics_db"
              let session, result = handle session "CREATE DATABASE IF NOT EXISTS diagnostics_db"
              Expect.equal result (Affected 1UL) "existing database reports one affected row"
              Expect.equal (conditionTriples session)
                  [ note 1007 "Can't create database 'diagnostics_db'; database exists" ] "existing database note"

              let session, _ = handle session "CREATE TABLE diagnostics_table (id INT)"
              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table (other INT)"
                  |> expectAffectedWithConditions
                      "existing table is ignored"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session, _ = handle session "CREATE TABLE diagnostics_source (id INT)"
              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table AS SELECT id FROM diagnostics_source"
                  |> expectAffectedWithConditions
                      "existing table skips CREATE TABLE AS"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table LIKE diagnostics_source"
                  |> expectAffectedWithConditions
                      "existing table skips CREATE TABLE LIKE"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session =
                  handle session "DROP TABLE IF EXISTS absent_one, absent_two"
                  |> expectAffectedWithConditions
                      "missing tables are ignored"
                      [ note 1051 "Unknown table 'fsdb.absent_one'"
                        note 1051 "Unknown table 'fsdb.absent_two'" ]

              let session, result = handle session "DROP DATABASE IF EXISTS absent_database"
              Expect.equal result (Affected 0UL) "missing database is ignored"
              Expect.isEmpty session.Diagnostics "DROP DATABASE IF EXISTS remains silent"

          testCase "CREATE TABLE unknown engines follow NO_ENGINE_SUBSTITUTION"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, strictResult =
                  handle session "CREATE TABLE strict_engine (id INT) ENGINE=totally_unknown"

              match strictResult with
              | Err(1286, "Unknown storage engine 'totally_unknown'") -> ()
              | other -> failtestf "expected strict unknown-engine rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode=''"

              let session =
                  handle session "CREATE TABLE lax_engine (id INT) ENGINE totally_unknown"
                  |> expectAffectedWithConditions
                      "unknown engine is substituted"
                      [ warning 1286 "Unknown storage engine 'totally_unknown'"
                        warning 1266 "Using storage engine InnoDB for table 'lax_engine'" ]

              let session, ctasResult =
                  handle session "CREATE TABLE lax_ctas ENGINE=totally_unknown AS SELECT 1 AS id"

              Expect.equal ctasResult (Affected 1UL) "unknown CTAS engine is substituted"

              Expect.equal
                  (conditionTriples session)
                  [ warning 1286 "Unknown storage engine 'totally_unknown'"
                    warning 1266 "Using storage engine InnoDB for table 'lax_ctas'" ]
                  "unknown CTAS engine diagnostics"

              match handle session "SELECT id FROM lax_ctas" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected substituted CTAS contents, got %A" other

              let session =
                  handle session "CREATE TABLE IF NOT EXISTS lax_engine (other INT) ENGINE=totally_unknown"
                  |> expectAffectedWithConditions
                      "engine substitution precedes the existence note"
                      [ warning 1286 "Unknown storage engine 'totally_unknown'"
                        warning 1266 "Using storage engine InnoDB for table 'lax_engine'"
                        note 1050 "Table 'lax_engine' already exists" ]

              let session, knownResult = handle session "CREATE TABLE memory_engine (id INT) ENGINE=MEMORY"
              Expect.equal knownResult (Affected 0UL) "known engines remain accepted"
              Expect.isEmpty session.Diagnostics "known engines do not warn"

              let session, invalidResult =
                  handle session "CREATE TABLE invalid_performance_schema (id INT) ENGINE=PERFORMANCE_SCHEMA"

              match invalidResult with
              | Err(1683, "Invalid performance_schema usage.") -> ()
              | other -> failtestf "expected performance_schema rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              match handle session "CREATE TABLE IF NOT EXISTS lax_engine (id INT) ENGINE=totally_unknown" |> snd with
              | Err(1286, "Unknown storage engine 'totally_unknown'") -> ()
              | other -> failtestf "expected engine validation before IF NOT EXISTS, got %A" other

          testCase "ALTER TABLE engine selection follows NO_ENGINE_SUBSTITUTION"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE altered_engine (id INT)"

              let session, strictResult =
                  handle session "ALTER TABLE altered_engine ENGINE=totally_unknown"

              match strictResult with
              | Err(1286, "Unknown storage engine 'totally_unknown'") -> ()
              | other -> failtestf "expected strict ALTER engine rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode=''"

              let session =
                  handle session "ALTER TABLE altered_engine ENGINE=totally_unknown"
                  |> expectAffectedWithConditions
                      "unknown ALTER engine is ignored"
                      [ warning 1286 "Unknown storage engine 'totally_unknown'" ]

              let session, knownResult = handle session "ALTER TABLE altered_engine ENGINE=ARCHIVE"
              Expect.equal knownResult (Affected 0UL) "known ALTER engines are accepted"
              Expect.isEmpty session.Diagnostics "known ALTER engines do not warn"

              match handle session "ALTER TABLE altered_engine ENGINE=PERFORMANCE_SCHEMA" |> snd with
              | Err(1031, "Table storage engine for 'altered_engine' doesn't have this option") -> ()
              | other -> failtestf "expected performance_schema ALTER rejection, got %A" other

          testCase "conditional object and account DDL records MySQL notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session =
                  handle session "DROP VIEW IF EXISTS absent_view"
                  |> expectAffectedWithConditions "missing view is ignored" [ note 1051 "Unknown table 'fsdb.absent_view'" ]

              let session =
                  handle session "DROP TRIGGER IF EXISTS absent_trigger"
                  |> expectAffectedWithConditions "missing trigger is ignored" [ note 1360 "Trigger does not exist" ]

              let session, _ = handle session "CREATE TABLE trigger_target (id INT)"
              let session, _ =
                  handle
                      session
                      "CREATE TRIGGER present_trigger BEFORE INSERT ON trigger_target FOR EACH ROW SET NEW.id = NEW.id + 1"

              let session =
                  handle
                      session
                      "CREATE TRIGGER IF NOT EXISTS present_trigger BEFORE INSERT ON trigger_target FOR EACH ROW SET NEW.missing = 1"
                  |> expectAffectedWithConditions
                      "existing trigger is ignored"
                      [ note 4099 "Trigger 'present_trigger' already exists on the table 'fsdb'.'trigger_target'." ]

              let session, _ = handle session "CREATE TABLE other_trigger_target (id INT)"
              let session, result =
                  handle
                      session
                      "CREATE TRIGGER IF NOT EXISTS present_trigger BEFORE INSERT ON other_trigger_target FOR EACH ROW SET NEW.id = 1"

              match result with
              | Err(4100, message) ->
                  Expect.equal
                      message
                      "Trigger 'fsdb'.'present_trigger' already exists on a different table. The 'IF NOT EXISTS' clause is only supported for triggers associated with the same table."
                      "different-table error"
              | other -> failtestf "expected different-table trigger error, got %A" other

              let session, _ = handle session "INSERT INTO trigger_target VALUES (1)"

              match handle session "SELECT id FROM trigger_target" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected the original trigger body, got %A" other

              let session, _ = handle session "CREATE PROCEDURE present_procedure() SELECT 1 AS value"
              let session =
                  handle session "CREATE PROCEDURE IF NOT EXISTS present_procedure() SELECT 2 AS value"
                  |> expectAffectedWithConditions
                      "existing procedure is ignored"
                      [ note 1304 "PROCEDURE present_procedure already exists" ]

              match handle session "CALL present_procedure()" |> snd with
              | MultipleResults [ (ResultSet([ "value" ], [ [ Some "1" ] ]), _); (Affected _, []) ] -> ()
              | other -> failtestf "expected the original procedure body, got %A" other

              let session =
                  handle session "DROP PROCEDURE IF EXISTS absent_procedure"
                  |> expectAffectedWithConditions
                      "missing procedure is ignored"
                      [ note 1305 "PROCEDURE fsdb.absent_procedure does not exist" ]

              let session =
                  handle session "DROP FUNCTION IF EXISTS absent_function"
                  |> expectAffectedWithConditions
                      "missing function is ignored"
                      [ note 1305 "FUNCTION fsdb.absent_function does not exist" ]

              let session =
                  handle session "DROP EVENT IF EXISTS absent_event"
                  |> expectAffectedWithConditions "missing event is ignored" [ note 1305 "Event absent_event does not exist" ]

              let session, _ = handle session "CREATE USER 'present_user'@'localhost'"
              let session =
                  handle session "CREATE USER IF NOT EXISTS 'present_user'@'localhost'"
                  |> expectAffectedWithConditions
                      "existing user is ignored"
                      [ note 3163 "Authorization ID 'present_user'@'localhost' already exists." ]

              let session =
                  handle session "ALTER USER IF EXISTS 'absent_user'@'localhost' IDENTIFIED BY 'secret'"
                  |> expectAffectedWithConditions
                      "missing user alteration is ignored"
                      [ note 3162 "Authorization ID 'absent_user'@'localhost' does not exist." ]

              let session =
                  handle session "DROP USER IF EXISTS 'absent_user'@'localhost'"
                  |> expectAffectedWithConditions
                      "missing user drop is ignored"
                      [ note 3162 "Authorization ID 'absent_user'@'localhost' does not exist." ]

              let session, _ = handle session "CREATE ROLE 'present_role'"
              let session =
                  handle session "CREATE ROLE IF NOT EXISTS 'present_role'"
                  |> expectAffectedWithConditions
                      "existing role is ignored"
                      [ note 3163 "Authorization ID 'present_role'@'%' already exists." ]

              handle session "DROP ROLE IF EXISTS 'absent_role'"
              |> expectAffectedWithConditions
                  "missing role drop is ignored"
                  [ note 3162 "Authorization ID 'absent_role'@'%' does not exist." ]
              |> ignore

          testCase "GET CURRENT DIAGNOSTICS assigns statement and condition information"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE diagnostics_row (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO diagnostics_row VALUES (1)"

              let session, result =
                  handle session "GET CURRENT DIAGNOSTICS @condition_count = NUMBER, @affected = ROW_COUNT;"

              Expect.equal result (Affected 0UL) "statement diagnostics"

              match handle session "SELECT @condition_count, @affected" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected statement diagnostics, got %A" other

              let session, _ = handle session "SET @kept = 41"
              let session, result = handle session "GET CURRENT DIAGNOSTICS CONDITION 2 @kept = MESSAGE_TEXT"

              Expect.equal result (Affected 0UL) "invalid condition number succeeds"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1758 ] "invalid condition becomes current"
              Expect.equal (session.Diagnostics |> List.map _.State) [ "35000" ] "invalid condition SQLSTATE"

              let session, _ =
                  handle
                      session
                      "GET CURRENT DIAGNOSTICS CONDITION 1 @diagnostic_code = MYSQL_ERRNO, @diagnostic_message = MESSAGE_TEXT"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION 0x1 @hex_code = MYSQL_ERRNO"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION 1e0 @exponent_code = MYSQL_ERRNO"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION '1' @text_code = MYSQL_ERRNO"

              match
                  handle
                      session
                      "SELECT @kept, @diagnostic_code, @diagnostic_message, @hex_code, @exponent_code, @text_code"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "41"
                      Some "1758"
                      Some "Invalid condition number"
                      Some "1758"
                      Some "1758"
                      Some "1758" ] ]
                ) ->
                  ()
              | other -> failtestf "expected condition diagnostics, got %A" other

              match handle session "GET STACKED DIAGNOSTICS @condition_count = NUMBER" |> snd |> errorInfo with
              | Some error ->
                  Expect.equal error.Code 3004 "stacked error code"
                  Expect.equal error.State "0Z002" "stacked SQLSTATE"
                  Expect.equal error.Message "GET STACKED DIAGNOSTICS when handler not active" "stacked error text"
              | None -> failtest "expected GET STACKED DIAGNOSTICS to fail outside a handler"

          testCase "INSERT IGNORE records warnings until the next ordinary statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, value INT NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 1)"
              let session, result = handle session "INSERT IGNORE INTO t VALUES (1, 2), (2, NULL)"

              Expect.equal result (Affected 0UL) "ignored rows do not affect the count"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1062; 1048 ] "one condition per ignored row"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], [ [ Some "Warning"; Some "1062"; _ ]; [ Some "Warning"; Some "1048"; _ ] ]) -> ()
              | other -> failtestf "expected INSERT IGNORE warnings, got %A" other

              match handle session "SELECT @@warning_count" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected warning count 2, got %A" other

              let session, _ = handle session "SELECT 1"
              Expect.isEmpty session.Diagnostics "ordinary statements replace the diagnostics area"

          testCase "non-strict omitted columns report their implicit defaults"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL)"
              let session, result = handle session "INSERT INTO t (id) VALUES (1)"
              Expect.equal result (Affected 1UL) "insert succeeds"

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 1364; Message = "Field 'name' doesn't have a default value" } ] -> ()
              | other -> failtestf "expected the implicit-default warning, got %A" other

              match handle session "SELECT name FROM t" |> snd with
              | ResultSet(_, [ [ Some "" ] ]) -> ()
              | other -> failtestf "expected the implicit empty string, got %A" other

          testCase "statement errors appear in SHOW ERRORS and SHOW WARNINGS"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, result = handle session "SELECT missing FROM t"

              match result with
              | Err(1054, _) -> ()
              | other -> failtestf "expected an unknown-column error, got %A" other

              match handle session "SHOW ERRORS" |> snd with
              | ResultSet(_, [ [ Some "Error"; Some "1054"; _ ] ]) -> ()
              | other -> failtestf "expected one error condition, got %A" other

              match handle session "SHOW COUNT(*) WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected warning count to include errors, got %A" other

          testCase "division by zero follows the session sql mode in reads"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "SELECT 1 / 0, 1 DIV 0, MOD(1, 0)"

              match result with
              | ResultSet(_, [ [ None; None; None ] ]) -> ()
              | other -> failtestf "expected NULL division results, got %A" other

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1365, "Division by 0"
                    Fsdb.Diagnostics.Warning, 1365, "Division by 0"
                    Fsdb.Diagnostics.Warning, 1365, "Division by 0" ]
                  "default mode reports each zero divisor"

              let session, result = handle session "SELECT NULL / 0, NULL DIV 0, MOD(NULL, 0), 1 / NULL"

              match result with
              | ResultSet(_, [ [ None; None; None; None ] ]) -> ()
              | other -> failtestf "expected NULL arithmetic to remain NULL, got %A" other

              Expect.isEmpty session.Diagnostics "NULL arithmetic does not report division by zero"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES'"
              let session, result = handle session "SELECT 1 / 0"

              match result with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected mode-disabled division to remain NULL, got %A" other

              Expect.isEmpty session.Diagnostics "disabled ERROR_FOR_DIVISION_BY_ZERO is silent"

          testCase "strict writes reject division by zero unless errors are ignored"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, value INT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 10)"
              let session, result = handle session "INSERT INTO t VALUES (2, NULL / 0)"
              Expect.equal result (Affected 1UL) "NULL arithmetic remains valid in a strict write"
              Expect.isEmpty session.Diagnostics "NULL arithmetic remains silent in a strict write"

              let session, result = handle session "INSERT INTO t VALUES (9, 1 / 0)"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected strict INSERT division error, got %A" other

              let session, result = handle session "UPDATE t SET value = 99 WHERE 1 DIV 0"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected strict UPDATE predicate division error, got %A" other

              let session, result = handle session "INSERT IGNORE INTO t VALUES (3, MOD(1, 0))"
              Expect.equal result (Affected 1UL) "IGNORE retains the row"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1365 ] "IGNORE downgrades the error"

              let session, _ = handle session "SET SESSION sql_mode = 'ERROR_FOR_DIVISION_BY_ZERO'"
              let session, result = handle session "INSERT INTO t VALUES (4, 1 / 0)"
              Expect.equal result (Affected 1UL) "non-strict mode retains the row"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1365 ] "non-strict mode reports a warning"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES'"
              let session, result = handle session "INSERT INTO t VALUES (5, 1 / 0)"
              Expect.equal result (Affected 1UL) "disabled error mode retains the row"
              Expect.isEmpty session.Diagnostics "disabled error mode is silent"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO'"
              let session, _ = handle session "START TRANSACTION"
              let session, _ = handle session "INSERT INTO t VALUES (6, 60)"
              let session, result = handle session "INSERT INTO t VALUES (7, 1 / 0)"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected the transaction statement to fail, got %A" other

              let session, result = handle session "INSERT INTO t VALUES (8, 80)"
              Expect.equal result (Affected 1UL) "the transaction remains usable after the statement error"
              let session, result = handle session "COMMIT"
              Expect.equal result (Affected 0UL) "the surviving transaction writes commit"

              match handle session "SELECT id, value FROM t ORDER BY id" |> snd with
              | ResultSet(
                  _,
                  [ [ Some "1"; Some "10" ]
                    [ Some "2"; None ]
                    [ Some "3"; None ]
                    [ Some "4"; None ]
                    [ Some "5"; None ]
                    [ Some "6"; Some "60" ]
                    [ Some "8"; Some "80" ] ]
                ) ->
                  ()
              | other -> failtestf "expected only successful writes, got %A" other

          testCase "GROUP_CONCAT truncation records warning 1260"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, value VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'aa'), (2, 'bb')"
              let session, _ = handle session "SET group_concat_max_len = 4"
              let session, result = handle session "SELECT GROUP_CONCAT(value ORDER BY id SEPARATOR '-') FROM t"

              match result with
              | ResultSet(_, [ [ Some "aa-b" ] ]) -> ()
              | other -> failtestf "expected truncated GROUP_CONCAT result, got %A" other

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 1260; Message = message } ] ->
                  Expect.equal message "Row 2 was cut by GROUP_CONCAT()" "MySQL warning text"
              | other -> failtestf "expected one GROUP_CONCAT warning, got %A" other

          testCase "UPDATE IGNORE records a skipped CHECK violation"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, CHECK (id > 0))"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, result = handle session "UPDATE IGNORE t SET id = 0"

              Expect.equal result (Affected 0UL) "ignored CHECK violations leave the row unchanged"

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 3819; Message = _ } ] -> ()
              | other -> failtestf "expected one UPDATE IGNORE warning, got %A" other

          testCase "non-strict inserts retain conversion and truncation conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT, u TINYINT UNSIGNED, e ENUM('a', 'b'), s SET('a', 'b'))"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"

              let session, result = handle session "INSERT INTO t VALUES ('abc', 300, 'x', 'a,x')"
              Expect.equal result (Affected 1UL) "non-strict coercions retain the row"

              let conditions =
                  session.Diagnostics
                  |> List.map (fun condition -> condition.Level, condition.Code, condition.Message)

              Expect.equal
                  conditions
                  [ Fsdb.Diagnostics.Warning, 1366, "Incorrect integer value: 'abc' for column 'i' at row 1"
                    Fsdb.Diagnostics.Warning, 1264, "Out of range value for column 'u' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'e' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 's' at row 1" ]
                  "MySQL condition codes and messages"

              match handle session "SHOW WARNINGS LIMIT 1, 2" |> snd with
              | ResultSet(_, [ [ Some "Warning"; Some "1264"; _ ]; [ Some "Warning"; Some "1265"; _ ] ]) -> ()
              | other -> failtestf "expected limited conditions, got %A" other

              match handle session "SELECT i, u, e, s FROM t" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "255"; Some ""; Some "a" ] ]) -> ()
              | other -> failtestf "expected MySQL non-strict stored values, got %A" other

          testCase "prepared inserts replace prior diagnostics with conversion conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT)"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "INSERT INTO t VALUES ('abc')"

              match prepareStatement "INSERT INTO t VALUES (?)" with
              | Ok(Some ast, 1) ->
                  let prepared =
                      { Ast = Some ast
                        Sql = "INSERT INTO t VALUES (?)"
                        ParamCount = 1
                        LastParamTypes = None
                        ParameterTypes = None
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

                  let session, result = executePrepared session prepared [ VString "abc" ]
                  Expect.equal result (Affected 1UL) "prepared insert succeeds"

                  match session.Diagnostics with
                  | [ { Level = Fsdb.Diagnostics.Warning; Code = 1366; Message = "Incorrect integer value: 'abc' for column 'i' at row 1" } ] -> ()
                  | other -> failtestf "expected prepared conversion condition, got %A" other
              | other -> failtestf "expected one prepared parameter, got %A" other

          testCase "non-strict multi-row inserts retain source row numbers"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT)"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "INSERT INTO t VALUES ('one'), ('two')"

              Expect.equal
                  (session.Diagnostics |> List.map _.Message)
                  [ "Incorrect integer value: 'one' for column 'i' at row 1"
                    "Incorrect integer value: 'two' for column 'i' at row 2" ]
                  "condition rows match the VALUES source rows"

          testCase "DECIMAL scale loss records MySQL note conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DECIMAL(5, 2))"
              let session, result = handle session "INSERT INTO t VALUES (12.345), (67.891)"
              Expect.equal result (Affected 2UL) "rounded values insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1"
                    Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 2" ]
                  "one MySQL note per rounded source value"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "Note"; Some "1265"; Some "Data truncated for column 'd' at row 1" ]; [ Some "Note"; Some "1265"; Some "Data truncated for column 'd' at row 2" ] ]) -> ()
              | other -> failtestf "expected DECIMAL notes, got %A" other

              match handle session "SELECT d FROM t" |> snd with
              | ResultSet(_, [ [ Some "12.35" ]; [ Some "67.89" ] ]) -> ()
              | other -> failtestf "expected rounded DECIMAL values, got %A" other

          testCase "prepared DECIMAL inserts replace prior diagnostics with scale-loss notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DECIMAL(5, 2))"
              let session, _ = handle session "INSERT INTO t VALUES (12.345)"

              match prepareStatement "INSERT INTO t VALUES (?)" with
              | Ok(Some ast, 1) ->
                  let prepared =
                      { Ast = Some ast
                        Sql = "INSERT INTO t VALUES (?)"
                        ParamCount = 1
                        LastParamTypes = None
                        ParameterTypes = None
                        SchemaDependencies = Map.empty
                        DivisionPrecisionIncrement = 4 }

                  let session, result = executePrepared session prepared [ VDecimal 67.891M ]
                  Expect.equal result (Affected 1UL) "prepared rounded value inserts"

                  match session.Diagnostics with
                  | [ { Level = Fsdb.Diagnostics.Note; Code = 1265; Message = "Data truncated for column 'd' at row 1" } ] -> ()
                  | other -> failtestf "expected prepared scale-loss note, got %A" other
              | other -> failtestf "expected one prepared parameter, got %A" other

          testCase "declared text and binary widths retain non-strict truncation warnings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(3), c CHAR(3), b BINARY(3), vb VARBINARY(3))"
              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "INSERT INTO t VALUES ('abcd', 'wxyz', 'abcd', 'wxyz')"
              Expect.equal result (Affected 1UL) "truncated values insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'v' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'c' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'b' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'vb' at row 1" ]
                  "MySQL reports each shortened value"

              match handle session "SELECT v, c, HEX(b), HEX(vb) FROM t" |> snd with
              | ResultSet(_, [ [ Some "abc"; Some "wxy"; Some "616263"; Some "777879" ] ]) -> ()
              | other -> failtestf "expected truncated text and bytes, got %A" other

          testCase "strict declared text width returns MySQL's data-too-long error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(3))"

              match handle session "INSERT INTO t VALUES ('abcd')" |> snd with
              | Err(1406, "Data too long for column 'v' at row 1") -> ()
              | other -> failtestf "expected MySQL's data-too-long error, got %A" other

              match handle session "INSERT INTO t VALUES ('ok'), ('abcd')" |> snd with
              | Err(1406, "Data too long for column 'v' at row 2") -> ()
              | other -> failtestf "expected a source-row error, got %A" other

          testCase "CHAR removes trailing spaces while VARCHAR preserves them"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (c CHAR(3), v VARCHAR(3))"
              let session, result = handle session "INSERT INTO t VALUES ('x  ', 'x  ')"
              Expect.equal result (Affected 1UL) "space-padded values insert"

              match handle session "SELECT c, LENGTH(c), v, LENGTH(v) FROM t" |> snd with
              | ResultSet(_, [ [ Some "x"; Some "1"; Some "x  "; Some "3" ] ]) -> ()
              | other -> failtestf "expected CHAR to trim and VARCHAR to retain trailing spaces, got %A" other

          testCase "ALTER text widths count Unicode scalar values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(2))"
              let session, _ = handle session "INSERT INTO t VALUES ('😀')"
              let session, result = handle session "ALTER TABLE t MODIFY v VARCHAR(1)"
              Expect.equal result (Affected 1UL) "one scalar value fits VARCHAR(1) and is copied"

              match handle session "SELECT v FROM t" |> snd with
              | ResultSet(_, [ [ Some "😀" ] ]) -> ()
              | other -> failtestf "expected the supplementary-plane scalar to survive ALTER, got %A" other

          testCase "over-width text defaults are invalid in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE TABLE strict_default (v VARCHAR(3) DEFAULT 'abcd')" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's strict invalid-default error, got %A" other

              let session, _ = handle session "SET SESSION sql_mode = ''"

              match handle session "CREATE TABLE nonstrict_default (v VARCHAR(3) DEFAULT 'abcd')" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's non-strict invalid-default error, got %A" other

              let session, _ = handle session "CREATE TABLE alter_default (v VARCHAR(3))"

              match handle session "ALTER TABLE alter_default ALTER COLUMN v SET DEFAULT 'abcd'" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's ALTER invalid-default error, got %A" other

          testCase "DECIMAL defaults retain scale-loss notes in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "CREATE TABLE strict_default (d DECIMAL(5, 2) DEFAULT 1.234)"
              Expect.equal result (Affected 0UL) "strict DECIMAL default creates"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1" ]
                  "strict default reports MySQL's scale-loss note"

              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "CREATE TABLE nonstrict_default (d DECIMAL(5, 2) DEFAULT 1.234)"
              Expect.equal result (Affected 0UL) "non-strict DECIMAL default creates"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1" ]
                  "non-strict default reports MySQL's scale-loss note"

          testCase "binary and lossy charset defaults are invalid in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let expectInvalid session sql =
                  match handle session sql |> snd with
                  | Err(1067, "Invalid default value for 'v'") -> ()
                  | other -> failtestf "expected MySQL's invalid-default error, got %A" other

              expectInvalid session "CREATE TABLE strict_binary (v BINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE strict_varbinary (v VARBINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE strict_ascii (v VARCHAR(3) CHARACTER SET ascii DEFAULT 'å')"
              expectInvalid session "CREATE TABLE strict_latin1 (v VARCHAR(3) CHARACTER SET latin1 DEFAULT '😀')"
              expectInvalid session "CREATE TABLE strict_utf8mb3 (v VARCHAR(3) CHARACTER SET utf8mb3 DEFAULT '😀')"

              let session, _ = handle session "SET SESSION sql_mode = ''"
              expectInvalid session "CREATE TABLE nonstrict_binary (v BINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE nonstrict_varbinary (v VARBINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE nonstrict_ascii (v VARCHAR(3) CHARACTER SET ascii DEFAULT 'å')"
              expectInvalid session "CREATE TABLE nonstrict_latin1 (v VARCHAR(3) CHARACTER SET latin1 DEFAULT '😀')"
              expectInvalid session "CREATE TABLE nonstrict_utf8mb3 (v VARCHAR(3) CHARACTER SET utf8mb3 DEFAULT '😀')"

          testCase "lossy column charsets retain MySQL conversion warnings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (a VARCHAR(20) CHARACTER SET ascii, l VARCHAR(20) CHARACTER SET latin1)"
              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "INSERT INTO t VALUES ('xåy', 'x😀y')"
              Expect.equal result (Affected 1UL) "lossy conversions insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1366, "Incorrect string value: '\\xC3\\xA5y' for column 'a' at row 1"
                    Fsdb.Diagnostics.Warning, 1366, "Incorrect string value: '\\xF0\\x9F\\x98\\x80y' for column 'l' at row 1" ]
                  "MySQL reports the UTF-8 suffix from the first unrepresentable character"

              match handle session "SELECT a, l FROM t" |> snd with
              | ResultSet(_, [ [ Some "x?y"; Some "x?y" ] ]) -> ()
              | other -> failtestf "expected replacement characters, got %A" other

          testCase "strict column charsets return MySQL's conversion error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (a VARCHAR(20) CHARACTER SET ascii)"

              match handle session "INSERT INTO t VALUES ('å')" |> snd with
              | Err(1366, "Incorrect string value: '\\xC3\\xA5' for column 'a' at row 1") -> ()
              | other -> failtestf "expected MySQL's incorrect-string error, got %A" other

          testCase "utf8mb3 columns reject or replace supplementary scalars according to strictness"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE utf8mb3_values (v VARCHAR(20) CHARACTER SET utf8mb3)"

              match handle session "INSERT INTO utf8mb3_values VALUES ('😀')" |> snd with
              | Err(1366, "Incorrect string value: '\\xF0\\x9F\\x98\\x80' for column 'v' at row 1") -> ()
              | other -> failtestf "expected strict utf8mb3 rejection, got %A" other

              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, inserted = handle session "INSERT INTO utf8mb3_values VALUES ('😀')"
              Expect.equal inserted (Affected 1UL) "non-strict insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning,
                    1366,
                    "Incorrect string value: '\\xF0\\x9F\\x98\\x80' for column 'v' at row 1" ]
                  "non-strict replacement warning"

              match handle session "SELECT v FROM utf8mb3_values" |> snd with
              | ResultSet(_, [ [ Some "?" ] ]) -> ()
              | other -> failtestf "expected the replacement character, got %A" other

          testCase "utf8mb3 conversion errors keep a bounded byte preview"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE utf8mb3_large (v LONGTEXT CHARACTER SET utf8mb3)"
              let value = "😀" + String.replicate 100_000 "a"

              match handle session ("INSERT INTO utf8mb3_large VALUES ('" + value + "')") |> snd with
              | Err(1366, message) -> Expect.isLessThan message.Length 200 "the diagnostic previews only the offending prefix"
              | other -> failtestf "expected strict utf8mb3 rejection, got %A" other

        ]
