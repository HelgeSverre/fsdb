module Fsdb.Tests.QueryHandlerAccountTests

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
        "Accounts and privileges"
        [ testCase "CURRENT_USER()/USER()/SESSION_USER() report the session's user, not a hardcoded name"
          <| fun _ ->
              let session = { create 1 (Fsdb.Storage.create ()) with User = "alice" }

              match handle session "SELECT CURRENT_USER(), USER(), SESSION_USER()" |> snd with
              | ResultSet(_, [ [ Some "alice@%"; Some "alice@localhost"; Some "alice@localhost" ] ]) -> ()
              | other -> failtestf "expected the session user's identities, got %A" other

          testCase "paren-less SELECT CURRENT_USER parses as the function, not a column (TablePlus/phpMyAdmin form)"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT CURRENT_USER" |> snd with
              | ResultSet(_, [ [ Some "root@%" ] ]) -> ()
              | other -> failtestf "expected root@%%, got %A" other

          testCase "SHOW DATABASES lists mysql alphabetically interleaved with real databases"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE zoo"

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet(_, rows) ->
                  let names = rows |> List.map (List.head >> Option.get)
                  Expect.equal names [ "fsdb"; "information_schema"; "mysql"; "zoo" ] "sorted, mysql included"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "USE mysql works and SHOW TABLES FROM mysql lists the system tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE mysql" with
              | session, Affected 0UL ->
                  match handle session "SHOW TABLES" |> snd with
                  | ResultSet([ "Tables_in_mysql" ], rows) ->
                      let names = rows |> List.map (List.head >> Option.get)
                      Expect.equal
                          names
                          [ "check_constraints"; "columns_priv"; "db"; "global_grants"; "routines"; "tables_priv"; "triggers"; "user"; "views" ]
                          "the system tables"
                  | other -> failtestf "expected the mysql table list, got %A" other
              | _, other -> failtestf "expected USE mysql to succeed, got %A" other

          testCase "SHOW TABLES FROM information_schema lists the virtual tables instead of 1049"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW FULL TABLES FROM information_schema" |> snd with
              | ResultSet([ "Tables_in_information_schema"; "Table_type" ], rows) ->
                  Expect.isTrue
                      (rows |> List.exists (fun r -> r = [ Some "TABLES"; Some "SYSTEM VIEW" ]))
                      "TABLES present as a SYSTEM VIEW"
              | other -> failtestf "expected the virtual table list, got %A" other

          testCase "SELECT from mysql.user finds the bootstrap root row (phpMyAdmin's isSuperUser probe shape)"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT User, Host, plugin, Select_priv FROM mysql.user" |> snd with
              | ResultSet(_, [ [ Some "root"; Some "%"; Some "mysql_native_password"; Some "Y" ] ]) -> ()
              | other -> failtestf "expected the root row, got %A" other

              match handle session "SELECT 1 FROM mysql.user LIMIT 1" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the isSuperUser probe to succeed, got %A" other

          testCase "CREATE USER / DROP USER manage mysql.user rows with MySQL's 1396 duplicate/missing semantics"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "CREATE USER 'bob'@'%' IDENTIFIED BY 's3cret'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected CREATE USER to succeed, got %A" other

              match Fsdb.Auth.tryUserRow store "bob" with
              | Some(cols, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash cols row)
                      (Fsdb.Auth.nativePasswordHash "s3cret")
                      "hash landed in authentication_string"
              | None -> failtest "expected bob to exist"

              match handle session "CREATE USER bob" |> snd with
              | Err(1396, msg) -> Expect.stringContains msg "CREATE USER failed" "duplicate is 1396"
              | other -> failtestf "expected 1396, got %A" other

              match handle session "CREATE USER IF NOT EXISTS bob" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected IF NOT EXISTS to be a no-op, got %A" other

              match handle session "DROP USER bob" |> snd with
              | Affected 0UL -> Expect.isNone (Fsdb.Auth.tryUserRow store "bob") "bob gone"
              | other -> failtestf "expected DROP USER to succeed, got %A" other

              match handle session "DROP USER bob" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected dropping a missing user to be 1396, got %A" other

          testCase "ALTER USER and SET PASSWORD rewrite the stored hash; SET PASSWORD defaults to the session user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE USER carol"

              match handle session "ALTER USER 'carol'@'%' IDENTIFIED BY 'first'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ALTER USER to succeed, got %A" other

              match handle session "SET PASSWORD FOR 'carol'@'%' = 'second'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "carol" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "second")
                          "SET PASSWORD FOR overwrote ALTER USER's hash"
                  | None -> failtest "carol vanished"
              | other -> failtestf "expected SET PASSWORD FOR to succeed, got %A" other

              // No FOR clause: applies to the session's own user (root).
              match handle session "SET PASSWORD = 'rootpw'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "root" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "rootpw")
                          "session user's hash set"
                  | None -> failtest "root vanished"
              | other -> failtestf "expected SET PASSWORD to succeed, got %A" other

          testCase "SET PASSWORD and SHOW GRANTS keep host-qualified accounts separate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE USER 'alice'@'%' IDENTIFIED BY 'broad'"
              let session, _ = handle session "CREATE USER 'alice'@'localhost' IDENTIFIED BY 'local'"

              match handle session "SET PASSWORD FOR alice@localhost = 'changed'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected host-qualified SET PASSWORD to succeed, got %A" other

              match Fsdb.Auth.tryUserRowForAccount store (Fsdb.Auth.account "alice" "localhost") with
              | Some(columns, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash columns row)
                      (Fsdb.Auth.nativePasswordHash "changed")
                      "localhost password changes"
              | None -> failtest "localhost account exists"

              match Fsdb.Auth.tryUserRowForAccount store (Fsdb.Auth.account "alice" "%") with
              | Some(columns, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash columns row)
                      (Fsdb.Auth.nativePasswordHash "broad")
                      "percent password remains"
              | None -> failtest "percent account exists"

              match handle session "SHOW GRANTS FOR alice@localhost" |> snd with
              | ResultSet([ "Grants for alice@localhost" ], [ [ Some grant ] ]) ->
                  Expect.stringContains grant "`alice`@`localhost`" "selected account renders"
              | other -> failtestf "expected localhost grants, got %A" other

          testCase "RENAME USER moves the account and its grants"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "CREATE USER 'alice'@'localhost' IDENTIFIED BY 'secret'"
              let session, _ = handle session "GRANT SELECT ON shop.* TO alice@localhost"

              match handle session "RENAME USER 'alice'@'localhost' TO 'bob'@'%'" |> snd with
              | Affected 0UL ->
                  Expect.isNone (Fsdb.Auth.tryUserRow store "alice") "old account removed"
                  Expect.isSome (Fsdb.Auth.tryUserRow store "bob") "new account created"

                  match Fsdb.Auth.check store "bob" [ "SELECT", Fsdb.Auth.OnDb "shop" ] with
                  | Ok() -> ()
                  | Error error -> failtestf "expected the renamed grant, got %A" error
              | other -> failtestf "expected RENAME USER to succeed, got %A" other

              let session, _ = handle session "CREATE USER carol"

              match handle session "RENAME USER bob TO carol" |> snd with
              | Err(1396, _) ->
                  Expect.isSome (Fsdb.Auth.tryUserRow store "bob") "source survives a destination collision"
                  Expect.isSome (Fsdb.Auth.tryUserRow store "carol") "destination survives a collision"
              | other -> failtestf "expected a destination collision to be 1396, got %A" other

          testCase "SHOW CREATE USER renders the stored authentication definition"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE USER 'show_user'@'%' IDENTIFIED BY 'secret'"

              match handle session "SHOW CREATE USER 'show_user'@'%'" |> snd with
              | ResultSet([ column ], [ [ Some ddl ] ]) ->
                  Expect.equal column "CREATE USER for show_user@%" "column label"
                  Expect.stringContains ddl "CREATE USER `show_user`@`%` IDENTIFIED WITH 'mysql_native_password'" "account and plugin"
                  Expect.stringContains ddl (Fsdb.Auth.nativePasswordHash "secret") "stored password hash"
                  Expect.stringContains ddl "ACCOUNT UNLOCK" "account state"
              | other -> failtestf "expected SHOW CREATE USER row, got %A" other

              match handle session "SHOW CREATE USER missing" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected missing account error 1396, got %A" other

          testCase "SET PASSWORD is enforced: own password is free, someone else's needs CREATE USER"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER mallory"
              let _root, _ = handle root "CREATE USER victim"

              let mallory = { create 2 store with User = "mallory" }

              match handle mallory "SET PASSWORD FOR victim = 'owned'" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected changing another user's password to be 1227, got %A" other

              match handle mallory "SET PASSWORD = 'mine'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "mallory" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "mine")
                          "own password change works without privileges"
                  | None -> failtest "mallory vanished"
              | other -> failtestf "expected own-password SET PASSWORD to succeed, got %A" other

          testCase "DROP DATABASE mysql is rejected with 3552 like a real system schema"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "DROP DATABASE mysql" |> snd with
              | Err(3552, msg) -> Expect.stringContains msg "system schema" "names the rejection"
              | other -> failtestf "expected 3552, got %A" other

          testCase "privilege enforcement: db and table grants gate SELECT/INSERT/DDL with 1142/1044/1227"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE orders (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE TABLE secrets (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER worker"
              let root, _ = handle root "GRANT SELECT ON shop.orders TO worker"

              let worker = { create 2 store with User = "worker"; Database = Some "shop" }

              match handle worker "SHOW DATABASES" |> snd with
              | ResultSet(_, rows) ->
                  let names = rows |> List.map (List.head >> Option.get)
                  Expect.equal names [ "information_schema"; "shop" ] "only databases reachable through grants are visible"
              | other -> failtestf "expected filtered SHOW DATABASES, got %A" other

              match handle worker "SELECT * FROM orders" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected the table grant to allow SELECT, got %A" other

              match handle worker "SELECT * FROM secrets" |> snd with
              | Err(1142, msg) -> Expect.stringContains msg "SELECT command denied to user 'worker'" "1142 shape"
              | other -> failtestf "expected 1142 on the ungranted table, got %A" other

              match handle worker "INSERT INTO orders VALUES (1)" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected INSERT to be denied, got %A" other

              match handle worker "CREATE DATABASE sneaky" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected CREATE DATABASE to be 1044, got %A" other

              match handle worker "CREATE USER accomplice" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected CREATE USER to be 1227, got %A" other

              // MySQL's grant-denial codes are level-shaped (oracle-verified):
              // 1142 for a table target, 1044 db, 1045 global.
              match handle worker "GRANT SELECT ON shop.secrets TO worker" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected table-level GRANT without grant option to be 1142, got %A" other

              match handle worker "GRANT SELECT ON shop.* TO worker" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected db-level GRANT without grant option to be 1044, got %A" other

              match handle worker "GRANT SELECT ON *.* TO worker" |> snd with
              | Err(1045, _) -> ()
              | other -> failtestf "expected global GRANT without grant option to be 1045, got %A" other

              // A db-level grant covers every table in the db.
              let root, _ = handle root "GRANT INSERT ON shop.* TO worker"

              match handle worker "INSERT INTO secrets VALUES (2)" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the db-level INSERT grant to work, got %A" other

              // information_schema stays readable for everyone.
              match handle worker "SELECT COUNT(*) FROM information_schema.TABLES" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected information_schema to stay readable, got %A" other

              // REVOKE takes it back.
              let _root, _ = handle root "REVOKE SELECT ON shop.orders FROM worker"

              match handle worker "SELECT * FROM orders" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected the revoked SELECT to be denied again, got %A" other

          testCase "WITH GRANT OPTION delegates at its own level, only for held privileges (MySQL-differential-verified)"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE t1 (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER dave"
              let root, _ = handle root "CREATE USER eve"
              let root, _ = handle root "CREATE USER carol"
              let root, _ = handle root "GRANT SELECT ON shop.* TO dave WITH GRANT OPTION"
              let root, _ = handle root "GRANT SELECT ON shop.t1 TO carol WITH GRANT OPTION"

              let dave = { create 2 store with User = "dave"; Database = Some "shop" }
              let carol = { create 3 store with User = "carol"; Database = Some "shop" }
              let eve = { create 4 store with User = "eve"; Database = Some "shop" }

              // db-scoped grant option delegates within the db...
              match handle dave "GRANT SELECT ON shop.* TO eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected dave's db-scoped delegation to work, got %A" other

              match handle eve "SELECT COUNT(*) FROM t1" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected eve's delegated SELECT to work, got %A" other

              // ...but not privileges the grantor doesn't hold (1044 at db
              // level), nor scopes above its own (1045 at global).
              match handle dave "GRANT INSERT ON shop.* TO eve" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected granting an unheld privilege to be 1044, got %A" other

              match handle dave "GRANT SELECT ON *.* TO eve" |> snd with
              | Err(1045, _) -> ()
              | other -> failtestf "expected escalating to global to be 1045, got %A" other

              // Table-scoped grant option: that one table only.
              match handle carol "GRANT SELECT ON shop.t1 TO eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected carol's table-scoped delegation to work, got %A" other

              match handle carol "GRANT SELECT ON shop.* TO eve" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected table-scoped option not to cover the db, got %A" other

              // The delegate can revoke what it could grant.
              match handle dave "REVOKE SELECT ON shop.* FROM eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected dave's revoke to work, got %A" other

          testCase "REVOKE ALL deletes the emptied grant rows — no ghost USAGE lines in SHOW GRANTS"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE t1 (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER gina"
              let root, _ = handle root "GRANT ALL PRIVILEGES ON shop.* TO gina"
              let root, _ = handle root "GRANT SELECT ON shop.t1 TO gina"
              let root, _ = handle root "REVOKE ALL PRIVILEGES ON shop.* FROM gina"
              let root, _ = handle root "REVOKE SELECT ON shop.t1 FROM gina"

              match handle root "SHOW GRANTS FOR gina" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `gina`@`%`" ]
                      "only the global USAGE line remains, like MySQL"
              | other -> failtestf "expected gina's grants, got %A" other

          testCase "SHOW GRANTS renders global/db/table lines; USER_PRIVILEGES and SHOW PRIVILEGES enumerate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store

              match handle root "SHOW GRANTS" |> snd with
              | ResultSet([ header ], rows) ->
                  Expect.equal header "Grants for root@%" "header names the account"

                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT ALL PRIVILEGES ON *.* TO `root`@`%` WITH GRANT OPTION" ]
                      "root's single global line"
              | other -> failtestf "expected root's grants, got %A" other

              let root, _ = handle root "CREATE USER worker"
              let root, _ = handle root "GRANT SELECT, UPDATE ON shop.* TO worker"
              let root, _ = handle root "GRANT DELETE ON shop.orders TO worker"

              match handle root "SHOW GRANTS FOR 'worker'@'%'" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `worker`@`%`"
                        "GRANT SELECT, UPDATE ON `shop`.* TO `worker`@`%`"
                        "GRANT DELETE ON `shop`.`orders` TO `worker`@`%`" ]
                      "usage + db + table lines in order"
              | other -> failtestf "expected worker's grants, got %A" other

              match handle root "SHOW GRANTS FOR nobody" |> snd with
              | Err(1141, _) -> ()
              | other -> failtestf "expected 1141 for an unknown grantee, got %A" other

              match handle root "SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = \"'worker'@'%'\"" |> snd with
              | ResultSet(_, [ [ Some "USAGE" ] ]) -> ()
              | other -> failtestf "expected worker's USAGE row in USER_PRIVILEGES, got %A" other

              match handle root "SHOW PRIVILEGES" |> snd with
              | ResultSet([ "Privilege"; "Context"; "Comment" ], rows) ->
                  Expect.equal (List.length rows) 73 "MySQL 8.4's 73 privileges"
              | other -> failtestf "expected the privilege table, got %A" other

              match handle root "FLUSH PRIVILEGES" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected FLUSH PRIVILEGES to be an OK no-op, got %A" other

          testCase "table metadata probes require a privilege and listings hide inaccessible tables"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE secrets"
              let root, _ = handle root "CREATE TABLE secrets.vault (id INT PRIMARY KEY, treasure TEXT)"
              let root, _ = handle root "CREATE USER snoop"

              let snoop =
                  { create 2 store with
                      User = "snoop"
                      AccountHost = "%"
                      Database = Some "secrets" }

              [ "SHOW CREATE TABLE secrets.vault"
                "SHOW COLUMNS FROM secrets.vault"
                "DESCRIBE secrets.vault"
                "SHOW INDEX FROM secrets.vault" ]
              |> List.iter (fun sql ->
                  match handle snoop sql |> snd with
                  | Err(1142, _) -> ()
                  | other -> failtestf "expected metadata denial for %s, got %A" sql other)

              match handle snoop "SHOW TABLES FROM secrets" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected an empty table listing, got %A" other

              match handle snoop "SHOW CREATE USER root" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected another account definition to be hidden, got %A" other

              match handle snoop "SHOW CREATE USER snoop" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected the current account definition to remain visible, got %A" other

              let _, grantResult = handle root "GRANT INSERT ON secrets.vault TO snoop"
              Expect.equal grantResult (Affected 0UL) "grant succeeds"

              match handle snoop "SHOW CREATE TABLE secrets.vault" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected metadata visibility from any table privilege, got %A" other

              match handle snoop "SHOW TABLES FROM secrets" |> snd with
              | ResultSet(_, [ [ Some "vault" ] ]) -> ()
              | other -> failtestf "expected the granted table in the listing, got %A" other

          testCase "repeated database grants update the existing privilege row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER repeatable"
              let root, _ = handle root "GRANT SELECT ON shop.* TO repeatable"

              match handle root "GRANT SELECT ON shop.* TO repeatable" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected an idempotent repeated grant, got %A" other

          testCase "mysql.user has MySQL 8.4's exact 51-column shape and mysql.db its 22"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              match Fsdb.Storage.scanList store "mysql" "user" with
              | Ok(cols, rows) ->
                  Expect.equal (List.length cols) 51 "51 columns"
                  Expect.equal (cols |> List.item 2 |> fun c -> c.Name) "Select_priv" "priv columns start at 3"
                  Expect.equal (cols |> List.last |> fun c -> c.Name) "User_attributes" "last column"
                  Expect.equal (List.length rows) 1 "just root"
              | Error e -> failtestf "expected mysql.user to scan, got %A" e

              match Fsdb.Storage.scanList store "mysql" "db" with
              | Ok(cols, rows) ->
                  Expect.equal (List.length cols) 22 "22 columns"
                  Expect.isEmpty rows "no db-level grants out of the box"
              | Error e -> failtestf "expected mysql.db to scan, got %A" e

        ]
