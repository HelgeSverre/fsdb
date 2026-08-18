module Fsdb.Tests.InformationSchemaTests

open Expecto
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

/// Parses `sql` and runs it against `store`, failing the test with the
/// parse error if the SQL itself doesn't parse — same helper shape as
/// `ExecutorTests.run`, kept local since this file only ever runs SELECTs
/// against `information_schema`.
let private run (store: Store) (sql: string) : QueryResult =
    match Fsdb.Parser.parse sql with
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
    | Ok stmt -> execute store builtins defaultDatabase (0L, 0L) false stmt |> snd

let private setup () : Store =
    let store = create ()

    run store "CREATE TABLE users (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(255) NOT NULL UNIQUE, name VARCHAR(100))"
    |> ignore

    run
        store
        "CREATE TABLE posts (id INT AUTO_INCREMENT PRIMARY KEY, user_id INT, title VARCHAR(200), CONSTRAINT posts_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE)"
    |> ignore

    run store "INSERT INTO users (email, name) VALUES ('a@b.com', 'alice')" |> ignore
    store

let tests =
    testList
        "information_schema"
        [ testCase "TABLES lists every real table with its row count, case-insensitive db/table names"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT table_name, table_type, engine, table_rows FROM information_schema.TABLES WHERE table_schema = 'fsdb' ORDER BY table_name" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "posts"; Some "BASE TABLE"; Some "InnoDB"; Some "0" ]
                        [ Some "users"; Some "BASE TABLE"; Some "InnoDB"; Some "1" ] ]
                      "both tables, correctly typed and counted"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "TABLES' t.AUTO_INCREMENT and t.CREATE_OPTIONS both project without a 1054/1064, Doctrine DBAL's own introspection query"
          <| fun _ ->
              // Laravel's `Blueprint::change()` (e.g. `$table->decimal(...)
              // ->change()`) goes through Doctrine DBAL, which probes
              // `getListTableMetadataSQL` — that query must handle
              // `t.AUTO_INCREMENT` (a reserved word) and `t.CREATE_OPTIONS`
              // without erroring.
              let store = setup ()

              match
                  run
                      store
                      "SELECT t.AUTO_INCREMENT, t.CREATE_OPTIONS FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = 'fsdb' AND t.TABLE_NAME = 'users'"
              with
              | ResultSet(_, [ [ _; Some "" ] ]) -> ()
              | other -> failtestf "expected a one-row resultset, got %A" other

          testCase "COLLATION_CHARACTER_SET_APPLICABILITY maps a table's collation to its character set"
          <| fun _ ->
              // The other half of that same Doctrine DBAL query: `INNER
              // JOIN information_schema.COLLATION_CHARACTER_SET_APPLICABILITY
              // ccsa ON ccsa.COLLATION_NAME = t.TABLE_COLLATION`.
              let store = setup ()

              match run store "SELECT character_set_name FROM information_schema.COLLATION_CHARACTER_SET_APPLICABILITY WHERE collation_name = 'utf8mb4_unicode_ci'" with
              | ResultSet(_, [ [ Some "utf8mb4" ] ]) -> ()
              | other -> failtestf "expected a single utf8mb4 row, got %A" other

          testCase "COLUMNS.character_set_name is utf8mb4 for a string column, NULL for a numeric one"
          <| fun _ ->
              // Doctrine DBAL's `selectTableColumns` (behind Laravel's
              // `Blueprint::change()`) projects this alongside `collation_name`.
              let store = setup ()

              match
                  run
                      store
                      "SELECT column_name, character_set_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'users' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; None ]; [ Some "email"; Some "utf8mb4" ]; [ Some "name"; Some "utf8mb4" ] ]
                      "numeric column NULL, string columns utf8mb4"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "COLUMNS.collation_name reports the server default and the declared COLLATE"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE tagged (name VARCHAR(20) COLLATE utf8mb4_bin, plain VARCHAR(20))" |> ignore

              match
                  run
                      store
                      "SELECT column_name, collation_name, character_set_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'tagged' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "name"; Some "utf8mb4_bin"; Some "utf8mb4" ]
                        [ Some "plain"; Some "utf8mb4_0900_ai_ci"; Some "utf8mb4" ] ]
                      "explicit COLLATE reported, default otherwise"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE renders per-column CHARACTER SET/COLLATE and table defaults"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE gc (name VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore

              // SHOW statements live in QueryHandler (text-probed, like
              // SET), not the Executor grammar.
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE gc" |> snd with
              | ResultSet(_, [ [ Some "gc"; Some ddl ] ]) ->
                  Expect.stringContains ddl "`name` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin" "the column's declared collation"
                  Expect.stringContains ddl "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci" "the table defaults"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

          testCase "SHOW CREATE TABLE keeps the table-level declaration, and never attaches charset/collation to a non-string column"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE gc_decl (id INT) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci" |> ignore
              run store "CREATE TABLE gc_intcoll (id INT COLLATE utf8mb4_bin)" |> ignore

              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE gc_decl" |> snd with
              | ResultSet(_, [ [ Some "gc_decl"; Some ddl ] ]) ->
                  Expect.stringContains ddl "`id` int DEFAULT NULL" "the INT column renders plain"
                  Expect.stringContains ddl "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci" "the table's declared defaults"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE gc_intcoll" |> snd with
              | ResultSet(_, [ [ Some "gc_intcoll"; Some ddl ] ]) ->
                  Expect.stringContains ddl "`id` int DEFAULT NULL" "a column-level COLLATE on an INT is a no-op"
                  Expect.stringContains ddl "DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci" "server-default table options"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

          testCase "generated columns surface in SHOW CREATE TABLE, information_schema.columns and SHOW COLUMNS"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE gen (n INT, doubled INT AS (n * 2) STORED, tripled INT AS (n * 3))" |> ignore

              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE gen" |> snd with
              | ResultSet(_, [ [ Some "gen"; Some ddl ] ]) ->
                  Expect.stringContains ddl "`doubled` int GENERATED ALWAYS AS ((`n` * 2)) STORED" "STORED renders like MySQL"
                  Expect.stringContains ddl "`tripled` int GENERATED ALWAYS AS ((`n` * 3)) VIRTUAL" "bare AS defaults to VIRTUAL"
                  Expect.isFalse (ddl.Contains "`doubled` int GENERATED ALWAYS AS ((`n` * 2)) STORED DEFAULT") "no DEFAULT clause on a generated column"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

              match
                  run
                      store
                      "SELECT column_name, extra, generation_expression FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'gen' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "n"; Some ""; Some "" ]
                        [ Some "doubled"; Some "STORED GENERATED"; Some "(`n` * 2)" ]
                        [ Some "tripled"; Some "VIRTUAL GENERATED"; Some "(`n` * 3)" ] ]
                      "EXTRA and GENERATION_EXPRESSION per kind"
              | other -> failtestf "expected a resultset, got %A" other

              match Fsdb.QueryHandler.handle session "SHOW COLUMNS FROM gen" |> snd with
              | ResultSet(cols, rows) ->
                  let extraIdx = cols |> List.findIndex ((=) "Extra")
                  Expect.equal (rows |> List.map (fun r -> List.item extraIdx r)) [ Some ""; Some "STORED GENERATED"; Some "VIRTUAL GENERATED" ] "SHOW COLUMNS Extra"
              | other -> failtestf "expected SHOW COLUMNS output, got %A" other

          testCase "ON UPDATE CURRENT_TIMESTAMP surfaces in SHOW CREATE TABLE and information_schema.columns EXTRA"
          <| fun _ ->
              let store = setup ()

              run
                  store
                  "CREATE TABLE stamped (id INT, updated_at DATETIME(3) DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3), plain DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP)"
              |> ignore

              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE stamped" |> snd with
              | ResultSet(_, [ [ Some "stamped"; Some ddl ] ]) ->
                  Expect.stringContains ddl "ON UPDATE CURRENT_TIMESTAMP(3)" "fsp > 0 renders the (N) suffix on ON UPDATE"
                  Expect.stringContains
                      ddl
                      "`plain` datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP"
                      "fsp 0 renders the bare keyword, no (0), right after DEFAULT"
                  Expect.isFalse (ddl.Contains "ON UPDATE CURRENT_TIMESTAMP(0)") "fsp 0 never renders (0)"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

              match
                  run
                      store
                      "SELECT column_name, extra FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'stamped' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "" ]
                        [ Some "updated_at"; Some "on update CURRENT_TIMESTAMP(3)" ]
                        [ Some "plain"; Some "on update CURRENT_TIMESTAMP" ] ]
                      "EXTRA renders the lowercase keyword with an fsp suffix when declared"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "ALTER TABLE ADD COLUMN attaches the table's declared defaults to string columns"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE alt (id INT) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci" |> ignore
              run store "ALTER TABLE alt ADD COLUMN name VARCHAR(10)" |> ignore
              run store "ALTER TABLE alt ADD COLUMN tag VARCHAR(10) COLLATE utf8mb4_bin" |> ignore

              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE alt" |> snd with
              | ResultSet(_, [ [ Some "alt"; Some ddl ] ]) ->
                  Expect.stringContains ddl "`name` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" "the added column inherits the table's declared collation"
                  Expect.stringContains ddl "`tag` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin" "an explicit column COLLATE wins over the table default"
                  Expect.stringContains ddl "`id` int DEFAULT NULL" "the INT column renders plain"
              | other -> failtestf "expected SHOW CREATE TABLE output, got %A" other

          testCase "CHARACTER_SETS and COLLATIONS list the supported charsets and registered collations"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT character_set_name, default_collate_name, maxlen FROM information_schema.character_sets WHERE character_set_name = 'utf8mb4'"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"; Some "4" ] ] "utf8mb4 with its MySQL 8.4 default"
              | other -> failtestf "expected a resultset, got %A" other

              match
                  run
                      store
                      "SELECT collation_name, character_set_name, id, sortlen, is_default, pad_attribute FROM information_schema.collations WHERE collation_name IN ('utf8mb4_bin', 'utf8mb4_0900_ai_ci', 'utf8mb4_unicode_ci') ORDER BY collation_name"
              with
              | ResultSet(_, rows) ->
                  // id/sortlen are MySQL 8.4.11's real values
                  // (information_schema.collations on the bench oracle).
                  Expect.equal
                      rows
                      [ [ Some "utf8mb4_0900_ai_ci"; Some "utf8mb4"; Some "255"; Some "0"; Some "Yes"; Some "NO PAD" ]
                        [ Some "utf8mb4_bin"; Some "utf8mb4"; Some "46"; Some "1"; Some ""; Some "PAD SPACE" ]
                        [ Some "utf8mb4_unicode_ci"; Some "utf8mb4"; Some "224"; Some "8"; Some ""; Some "PAD SPACE" ] ]
                      "the charset default flagged, pad attributes reported"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW COLLATION lists the registered collations and honors LIKE"
          <| fun _ ->
              let store = setup ()
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW COLLATION LIKE 'utf8mb4_bin'" |> snd with
              | ResultSet([ "Collation"; "Charset"; "Id"; "Default"; "Compiled"; "Sortlen"; "Pad_attribute" ], rows) ->
                  Expect.equal
                      rows
                      [ [ Some "utf8mb4_bin"; Some "utf8mb4"; Some "46"; Some ""; Some "Yes"; Some "1"; Some "PAD SPACE" ] ]
                      "one bin row with MySQL's real id/sortlen and its pad attribute"
              | other -> failtestf "expected SHOW COLLATION output, got %A" other

          testCase "every registered collation carries MySQL's real id, and vice versa"
          <| fun _ ->
              // `Collation.idAndSortlen` is harvested from a real 8.4.11
              // server; a collation in one map but not the other means the
              // registry drifted from what MySQL actually ships.
              Expect.equal
                  (Fsdb.Collation.registry |> Map.toList |> List.map fst |> Set.ofList)
                  (Fsdb.Collation.idAndSortlen |> Map.toList |> List.map fst |> Set.ofList)
                  "registry and id table cover exactly the same collations"

              for invented in [ "utf8mb4_norwegian_ci"; "utf8mb4_ja_0900_ai_ci"; "utf8mb4_zh_0900_ai_ci" ] do
                  Expect.isNone (Fsdb.Collation.tryFind invented) (sprintf "%s doesn't exist in MySQL 8.4 and must not resolve" invented)

          testCase "SHOW TABLE STATUS reports real row counts, sizes, and auto_increment"
          <| fun _ ->
              let store = setup ()
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'users'" |> snd with
              | ResultSet(cols, [ row ]) ->
                  let get name = List.item (List.findIndex ((=) name) cols) row
                  Expect.equal (get "Name") (Some "users") "table name"
                  Expect.equal (get "Rows") (Some "1") "actual row count"
                  Expect.equal (get "Auto_increment") (Some "2") "next id after one insert"
                  Expect.equal (get "Collation") (Some "utf8mb4_0900_ai_ci") "table collation"
                  Expect.isTrue ((get "Data_length" |> Option.defaultValue "0") <> "0") "in-memory payload size, not a constant"
                  // One row, so the average must equal the total — pins
                  // Avg_row_length to Data_length / Rows, not a constant.
                  Expect.equal (get "Avg_row_length") (get "Data_length") "avg row length is data_length / rows"
              | other -> failtestf "expected one status row, got %A" other

          testCase "SHOW TABLE STATUS on an empty table reports zeros, not an error"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE empties (id INT)" |> ignore
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'empties'" |> snd with
              | ResultSet(cols, [ row ]) ->
                  let get name = List.item (List.findIndex ((=) name) cols) row
                  Expect.equal (get "Rows") (Some "0") "no rows"
                  Expect.equal (get "Avg_row_length") (Some "0") "no division-by-zero"
                  Expect.equal (get "Data_length") (Some "0") "no payload bytes"
              | other -> failtestf "expected one status row, got %A" other

          testCase "SHOW TABLE STATUS on a table with no AUTO_INCREMENT column reports Auto_increment NULL"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE plain (id INT, name VARCHAR(20))" |> ignore
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'plain'" |> snd with
              | ResultSet(cols, [ row ]) ->
                  let get name = List.item (List.findIndex ((=) name) cols) row
                  Expect.equal (get "Auto_increment") None "no AUTO_INCREMENT column, no next value"
              | other -> failtestf "expected one status row, got %A" other

          testCase "SHOW TABLE STATUS Rows/Data_length track the live table through DELETE and ADD COLUMN"
          <| fun _ ->
              // Deliberate divergence from InnoDB: fsdb reports live values
              // where MySQL keeps stale page-count estimates until ANALYZE
              // (real 8.4.11 still shows Rows=3 right after DELETE FROM, and
              // an unchanged Data_length after ADD COLUMN).
              let store = setup ()
              run store "CREATE TABLE ts (id INT)" |> ignore
              run store "INSERT INTO ts VALUES (1), (2), (3)" |> ignore
              let session = Fsdb.Session.create 1 store

              let status () =
                  match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'ts'" |> snd with
                  | ResultSet(cols, [ row ]) -> fun name -> List.item (List.findIndex ((=) name) cols) row
                  | other -> failtestf "expected one status row, got %A" other

              let before = status ()
              Expect.equal (before "Rows") (Some "3") "live row count"
              let dataBefore = before "Data_length" |> Option.defaultValue "0" |> int
              Expect.isTrue (dataBefore > 0) "payload bytes for three rows"

              run store "ALTER TABLE ts ADD COLUMN extra VARCHAR(20)" |> ignore
              let widened = status ()
              let dataAfter = widened "Data_length" |> Option.defaultValue "0" |> int
              Expect.isTrue (dataAfter > dataBefore) "ADD COLUMN grows the live payload (NULL fill per row)"

              run store "DELETE FROM ts" |> ignore
              let emptied = status ()
              Expect.equal (emptied "Rows") (Some "0") "live zero immediately after DELETE, no stale estimate"
              Expect.equal (emptied "Avg_row_length") (Some "0") "no division-by-zero"

          testCase "SHOW TABLE STATUS Data_length stays 0 when ADD COLUMN hits an empty table"
          <| fun _ ->
              let store = setup ()
              run store "CREATE TABLE bare (id INT)" |> ignore
              run store "ALTER TABLE bare ADD COLUMN extra VARCHAR(20)" |> ignore
              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'bare'" |> snd with
              | ResultSet(cols, [ row ]) ->
                  let get name = List.item (List.findIndex ((=) name) cols) row
                  Expect.equal (get "Data_length") (Some "0") "no rows, no payload"
              | other -> failtestf "expected one status row, got %A" other

          testCase "COLUMNS projects declared columns with type/nullability/key metadata"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT column_name, column_type, is_nullable, column_key, extra FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'users' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "int"; Some "NO"; Some "PRI"; Some "auto_increment" ]
                        [ Some "email"; Some "varchar(255)"; Some "NO"; Some "UNI"; Some "" ]
                        [ Some "name"; Some "varchar(100)"; Some "YES"; Some ""; Some "" ] ]
                      "columns in declared order with the right metadata"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "STATISTICS has one row per index column, including a synthesized PRIMARY"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT index_name, non_unique, seq_in_index, column_name FROM information_schema.statistics WHERE table_schema = 'fsdb' AND table_name = 'users' ORDER BY index_name, seq_in_index"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "email"; Some "0"; Some "1"; Some "email" ]
                        [ Some "PRIMARY"; Some "0"; Some "1"; Some "id" ] ]
                      "primary key and the column-level unique index both show up"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "STATISTICS.sub_part is always NULL — no prefix-length indexes to report"
          <| fun _ ->
              // Doctrine DBAL's `selectIndexColumns` (behind Laravel's
              // `Blueprint::change()`) projects `SUB_PART`.
              let store = setup ()

              match run store "SELECT sub_part FROM information_schema.statistics WHERE table_schema = 'fsdb' AND table_name = 'users' AND index_name = 'PRIMARY'" with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected a single NULL row, got %A" other

          testCase "KEY_COLUMN_USAGE and REFERENTIAL_CONSTRAINTS surface the foreign key"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE table_schema = 'fsdb' AND table_name = 'posts' AND referenced_table_name IS NOT NULL"
              with
              | ResultSet(_, [ [ Some "user_id"; Some "users"; Some "id" ] ]) -> ()
              | other -> failtestf "expected the fk column usage row, got %A" other

              match
                  run
                      store
                      "SELECT delete_rule, table_name, referenced_table_name FROM information_schema.referential_constraints WHERE constraint_schema = 'fsdb' AND table_name = 'posts'"
              with
              | ResultSet(_, [ [ Some "CASCADE"; Some "posts"; Some "users" ] ]) -> ()
              | other -> failtestf "expected the referential constraint row, got %A" other

          testCase "TABLE_CONSTRAINTS has one row per PRIMARY KEY/UNIQUE/FOREIGN KEY, and none for a plain index"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT constraint_name, constraint_type FROM information_schema.table_constraints WHERE constraint_schema = 'fsdb' AND table_name = 'posts' ORDER BY constraint_type"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "posts_user_id_foreign"; Some "FOREIGN KEY" ]; [ Some "PRIMARY"; Some "PRIMARY KEY" ] ]
                      "posts' PK and FK, nothing else"
              | other -> failtestf "expected a resultset, got %A" other

              match
                  run
                      store
                      "SELECT EXISTS(SELECT * FROM information_schema.table_constraints WHERE constraint_schema = 'fsdb' AND table_name = 'posts' AND constraint_name = 'posts_user_id_foreign' AND constraint_type = 'FOREIGN KEY') AS `exists`"
              with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the exists() probe to find the named foreign key, got %A" other

          testCase "SCHEMATA lists every real database plus information_schema itself"
          <| fun _ ->
              let store = setup ()
              run store "CREATE DATABASE app" |> ignore

              match run store "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "app" ]; [ Some "fsdb" ]; [ Some "information_schema" ] ] "every schema present"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "an unknown information_schema table is a plain 1146"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT * FROM information_schema.nope" with
              | Err(1146, _) -> ()
              | other -> failtestf "expected 1146 for an unknown virtual table, got %A" other

          testCase "USE information_schema (as the statement db) resolves an unqualified table name"
          <| fun _ ->
              let store = setup ()

              match Fsdb.Parser.parse "SELECT table_name FROM tables WHERE table_schema = 'fsdb' AND table_name = 'users'" with
              | Error msg -> failtestf "expected the query to parse, got error: %s" msg
              | Ok stmt ->
                  match execute store builtins "information_schema" (0L, 0L) false stmt |> snd with
                  | ResultSet(_, [ [ Some "users" ] ]) -> ()
                  | other -> failtestf "expected the unqualified lookup to still resolve, got %A" other

          testCase "TABLES carries MySQL 8.4's full column set with a real CREATE_TIME"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT engine, version, row_format, create_time, checksum FROM information_schema.tables WHERE table_schema = 'fsdb' AND table_name = 'users'" with
              | ResultSet(_, [ [ Some "InnoDB"; Some "10"; Some "Dynamic"; Some createTime; None ] ]) ->
                  Expect.isTrue (createTime.Contains "-") "CREATE_TIME renders as a datetime"
              | other -> failtestf "expected the full TABLES row, got %A" other

          testCase "information_schema lists its own tables as SYSTEM VIEWs, matching the scan registry"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema = 'information_schema' ORDER BY table_name" with
              | ResultSet(_, rows) ->
                  Expect.isGreaterThan rows.Length 20 "all virtual tables listed"
                  Expect.all rows (fun r -> r.[1] = Some "SYSTEM VIEW") "typed SYSTEM VIEW"

                  // Every self-listed name must actually resolve through scan.
                  for row in rows do
                      match row.[0] with
                      | Some name ->
                          match run store (sprintf "SELECT * FROM information_schema.%s LIMIT 1" name) with
                          | ResultSet _ -> ()
                          | other -> failtestf "self-listed %s doesn't resolve: %A" name other
                      | None -> failtestf "NULL table_name in self-listing"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "COLUMNS reports CHARACTER_OCTET_LENGTH and PRIVILEGES"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT character_maximum_length, character_octet_length, privileges FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'name'" with
              | ResultSet(_, [ [ Some "100"; Some "400"; Some "select,insert,update,references" ] ]) -> ()
              | other -> failtestf "expected varchar(100) octet metadata, got %A" other

          testCase "VIEWS/ROUTINES/TRIGGERS/EVENTS/PARAMETERS are genuinely empty, not 1146"
          <| fun _ ->
              let store = setup ()

              for table in [ "views"; "routines"; "triggers"; "events"; "parameters" ] do
                  match run store (sprintf "SELECT * FROM information_schema.%s" table) with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected empty %s, got %A" table other

          testCase "the verbatim TablePlus routines query returns an empty set with aliased columns"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT ROUTINE_SCHEMA as function_schema,ROUTINE_NAME as function_name,ROUTINE_DEFINITION as create_statement,ROUTINE_TYPE as function_type FROM information_schema.routines where ROUTINE_SCHEMA='fsdb'" with
              | ResultSet([ "function_schema"; "function_name"; "create_statement"; "function_type" ], []) -> ()
              | other -> failtestf "expected the aliased empty set, got %A" other

          testCase "ENGINES reports the one real engine; PARTITIONS one unpartitioned row per table"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT engine, support FROM information_schema.engines" with
              | ResultSet(_, [ [ Some "InnoDB"; Some "DEFAULT" ] ]) -> ()
              | other -> failtestf "expected the single InnoDB row, got %A" other

              match run store "SELECT table_name, partition_name FROM information_schema.partitions WHERE table_schema = 'fsdb' ORDER BY table_name" with
              | ResultSet(_, [ [ Some "posts"; None ]; [ Some "users"; None ] ]) -> ()
              | other -> failtestf "expected NULL-partition rows, got %A" other

          testCase "the TablePlus TABLES x COLLATION_CHARACTER_SET_APPLICABILITY comma join resolves charsets"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT CCSA.character_set_name as charset, T.table_name AS name FROM information_schema.TABLES T, information_schema.COLLATION_CHARACTER_SET_APPLICABILITY CCSA WHERE CCSA.collation_name = T.table_collation AND T.TABLE_SCHEMA = 'fsdb' AND T.TABLE_NAME = 'users'" with
              | ResultSet(_, [ [ Some "utf8mb4"; Some "users" ] ]) -> ()
              | other -> failtestf "expected the joined charset row, got %A" other

          testCase "legacy-charset collations resolve, utf8_bin included as the utf8mb3 alias"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT 'demo' COLLATE utf8_bin = 'demo', BINARY 'a' = 'A', CAST('x' AS CHAR CHARACTER SET utf8)" with
              | ResultSet(_, [ [ Some "1"; Some "0"; Some "x" ] ]) -> ()
              | other -> failtestf "expected COLLATE/BINARY/CAST forms to evaluate, got %A" other ]
