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
                          [ "check_constraints"
                            "columns_priv"
                            "db"
                            "default_roles"
                            "events"
                            "functions"
                            "global_grants"
                            "role_edges"
                            "routines"
                            "tables_priv"
                            "triggers"
                            "user"
                            "views" ]
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

          testCase "CREATE USER retains TLS requirements"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, sslCreated = handle session "CREATE USER 'ssl_user'@'%' REQUIRE SSL"
              let session, x509Created = handle session "CREATE USER 'x509_user'@'%' REQUIRE X509"

              Expect.equal sslCreated (Affected 0UL) "SSL account created"
              Expect.equal x509Created (Affected 0UL) "X509 account created"

              match handle session "SELECT User, ssl_type FROM mysql.user WHERE User IN ('ssl_user', 'x509_user') ORDER BY User" |> snd with
              | ResultSet(_, [ [ Some "ssl_user"; Some "ANY" ]; [ Some "x509_user"; Some "X509" ] ]) -> ()
              | other -> failtestf "expected stored TLS requirements, got %A" other

              match handle session "SHOW CREATE USER 'ssl_user'@'%'" |> snd with
              | ResultSet(_, [ [ Some ddl ] ]) -> Expect.stringContains ddl " REQUIRE SSL " "SSL requirement"
              | other -> failtestf "expected SHOW CREATE USER for SSL account, got %A" other

              match handle session "SHOW CREATE USER 'x509_user'@'%'" |> snd with
              | ResultSet(_, [ [ Some ddl ] ]) -> Expect.stringContains ddl " REQUIRE X509 " "X509 requirement"
              | other -> failtestf "expected SHOW CREATE USER for X509 account, got %A" other

              let transportAllowed name encrypted clientCertificate =
                  match Fsdb.Auth.tryUserRowForAccount store (Fsdb.Auth.account name "%") with
                  | Some(columns, row) ->
                      Fsdb.Auth.transportSatisfiesAccount
                          { Encrypted = encrypted
                            ClientCertificateValidated = clientCertificate }
                          columns
                          row
                  | None -> failtestf "expected account %s" name

              Expect.isFalse (transportAllowed "ssl_user" false false) "SSL rejects plaintext"
              Expect.isTrue (transportAllowed "ssl_user" true false) "SSL accepts encryption"
              Expect.isFalse (transportAllowed "x509_user" true false) "X509 requires a client certificate"
              Expect.isTrue (transportAllowed "x509_user" true true) "X509 accepts an encrypted certificate transport"

          testCase "CREATE and ALTER USER persist resource, expiry, TLS, and lock options"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store

              let root, created =
                  handle
                      root
                      "CREATE USER policy REQUIRE SSL WITH MAX_QUERIES_PER_HOUR 60 MAX_UPDATES_PER_HOUR 20 MAX_CONNECTIONS_PER_HOUR 10 MAX_USER_CONNECTIONS 3 PASSWORD EXPIRE INTERVAL 180 DAY ACCOUNT LOCK"

              Expect.equal created (Affected 0UL) "account created"

              match
                  handle
                      root
                      "SELECT ssl_type,max_questions,max_updates,max_connections,max_user_connections,password_expired,password_lifetime,account_locked FROM mysql.user WHERE User='policy'"
                  |> snd
              with
              | ResultSet(_, [ values ]) ->
                  Expect.equal
                      values
                      [ Some "ANY"; Some "60"; Some "20"; Some "10"; Some "3"; Some "N"; Some "180"; Some "Y" ]
                      "mysql.user values"
              | other -> failtestf "expected account policy row, got %A" other

              match Fsdb.Auth.tryUserRow store "policy" with
              | Some(columns, row) ->
                  let changed =
                      List.zip columns (Array.toList row)
                      |> List.pick (fun (column, value) ->
                          match column.Name, value with
                          | "password_last_changed", VDateTime timestamp -> Some timestamp
                          | _ -> None)

                  Expect.isFalse (Fsdb.Auth.isPasswordExpiredAt changed columns row) "fresh lifetime"
                  Expect.isTrue (Fsdb.Auth.isPasswordExpiredAt (changed.AddDays 180.0) columns row) "lifetime boundary"
              | None -> failtest "expected policy account"

              let root, altered =
                  handle root "ALTER USER policy WITH MAX_QUERIES_PER_HOUR 7 PASSWORD EXPIRE NEVER ACCOUNT UNLOCK"

              Expect.equal altered (Affected 0UL) "account altered"

              match handle root "SHOW CREATE USER policy" |> snd with
              | ResultSet(_, [ [ Some ddl ] ]) ->
                  Expect.stringContains ddl "REQUIRE SSL" "unmentioned TLS requirement survives"
                  Expect.stringContains ddl "MAX_QUERIES_PER_HOUR 7" "changed query limit"
                  Expect.stringContains ddl "MAX_UPDATES_PER_HOUR 20" "unmentioned update limit survives"
                  Expect.stringContains ddl "PASSWORD EXPIRE NEVER" "password lifetime"
                  Expect.stringContains ddl "ACCOUNT UNLOCK" "account state"
              | other -> failtestf "expected SHOW CREATE USER, got %A" other

          testCase "account statement and connection limits reject and reset with MySQL 1226"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE TABLE limited_rows(id INT)"
              let root, _ = handle root "CREATE USER query_limited WITH MAX_QUERIES_PER_HOUR 2"
              let root, _ = handle root "CREATE USER update_limited WITH MAX_UPDATES_PER_HOUR 1"
              let root, _ = handle root "GRANT INSERT ON fsdb.limited_rows TO update_limited"
              let root, _ = handle root "CREATE USER password_limited WITH MAX_UPDATES_PER_HOUR 1"
              let root, _ = handle root "GRANT INSERT ON fsdb.limited_rows TO password_limited"
              let root, _ = handle root "CREATE USER denied_limited WITH MAX_UPDATES_PER_HOUR 1"
              let root, _ = handle root "CREATE USER hourly_limited WITH MAX_CONNECTIONS_PER_HOUR 1"
              let _, _ = handle root "CREATE USER active_limited WITH MAX_USER_CONNECTIONS 1"

              let querySession = { create 2 store with User = "query_limited" }
              let querySession, first = handle querySession "SELECT 1"
              let querySession, second = handle querySession "SELECT 2"
              let _, third = handle querySession "SELECT 3"
              Expect.isTrue (match first with ResultSet _ -> true | _ -> false) "first query"
              Expect.isTrue (match second with ResultSet _ -> true | _ -> false) "second query"

              match third with
              | Err(1226, message) -> Expect.stringContains message "max_questions" "query limit name"
              | other -> failtestf "expected max_questions 1226, got %A" other

              let root, flushed = handle root "FLUSH USER_RESOURCES"
              Expect.equal flushed (Affected 0UL) "resource counters flushed"

              match handle querySession "SELECT 3" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | other -> failtestf "expected query after FLUSH USER_RESOURCES, got %A" other

              let updateSession = { create 3 store with User = "update_limited" }
              let updateSession, firstInsert = handle updateSession "INSERT INTO limited_rows VALUES(1)"
              let _, secondInsert = handle updateSession "INSERT INTO limited_rows VALUES(2)"
              Expect.equal firstInsert (Affected 1UL) "first update"

              match secondInsert with
              | Err(1226, message) -> Expect.stringContains message "max_updates" "update limit name"
              | other -> failtestf "expected max_updates 1226, got %A" other

              let root, reset = handle root "ALTER USER update_limited WITH MAX_UPDATES_PER_HOUR 1"
              Expect.equal reset (Affected 0UL) "ALTER resets counters"
              Expect.equal (handle updateSession "INSERT INTO limited_rows VALUES(2)" |> snd) (Affected 1UL) "update after reset"

              let passwordSession = { create 4 store with User = "password_limited" }
              let passwordSession, passwordChanged = handle passwordSession "SET PASSWORD = ''"
              Expect.equal passwordChanged (Affected 0UL) "password change consumes an update"

              match handle passwordSession "INSERT INTO limited_rows VALUES(3)" |> snd with
              | Err(1226, message) -> Expect.stringContains message "max_updates" "password update limit name"
              | other -> failtestf "expected password change to consume max_updates, got %A" other

              let deniedSession = { create 5 store with User = "denied_limited" }

              match handle deniedSession "INSERT INTO limited_rows VALUES(4)" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected unprivileged update to be denied, got %A" other

              let root, granted = handle root "GRANT INSERT ON fsdb.limited_rows TO denied_limited"
              Expect.equal granted (Affected 0UL) "insert granted"
              Expect.equal (handle deniedSession "INSERT INTO limited_rows VALUES(4)" |> snd) (Affected 1UL) "denial did not consume update"

              let hourly = Fsdb.Auth.account "hourly_limited" "%"
              use firstHourly = Fsdb.Auth.tryAcquireAccountConnection store hourly |> Result.defaultWith (failtestf "%A")

              match Fsdb.Auth.tryAcquireAccountConnection store hourly with
              | Error(1226, message) -> Expect.stringContains message "max_connections_per_hour" "hourly limit name"
              | other -> failtestf "expected hourly connection 1226, got %A" other

              let active = Fsdb.Auth.account "active_limited" "%"
              let firstActive = Fsdb.Auth.tryAcquireAccountConnection store active |> Result.defaultWith (failtestf "%A")

              match Fsdb.Auth.tryAcquireAccountConnection store active with
              | Error(1226, message) -> Expect.stringContains message "max_user_connections" "active limit name"
              | other -> failtestf "expected active connection 1226, got %A" other

              firstActive.Dispose()
              use _secondActive = Fsdb.Auth.tryAcquireAccountConnection store active |> Result.defaultWith (failtestf "%A")
              ()

          testCase "expired-password sessions are restricted until their own password is reset"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let _, created = handle root "CREATE USER expired PASSWORD EXPIRE"
              Expect.equal created (Affected 0UL) "expired account created"

              let expired =
                  { create 2 store with
                      User = "expired"
                      PasswordExpired = true }

              match handle expired "SELECT 1" |> snd with
              | Err(1820, message) -> Expect.stringContains message "reset your password" "sandbox denial"
              | other -> failtestf "expected password sandbox 1820, got %A" other

              let active, reset = handle expired "SET PASSWORD = 'new-secret'"
              Expect.equal reset (Affected 0UL) "password reset"
              Expect.isFalse active.PasswordExpired "sandbox cleared"

              match handle active "SELECT 1" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected normal access after reset, got %A" other

              match Fsdb.Auth.tryUserRow store "expired" with
              | Some(columns, row) -> Expect.isFalse (Fsdb.Auth.isPasswordExpired columns row) "catalog expiry cleared"
              | None -> failtest "expected expired account"

          testCase "hourly account counters reset one hour after their window starts"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let account = Fsdb.Auth.account "rolling_limit" "%"
              let options =
                  { AccountOptions.empty with
                      ResourceLimits =
                        { AccountOptions.empty.ResourceLimits with
                            MaxQueriesPerHour = Some 1u
                            MaxConnectionsPerHour = Some 1u } }

              Fsdb.Auth.createUserWithOptions store account.Name account.Host None options
              |> Result.defaultWith (failtestf "%A")

              let started = DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc)
              Expect.isOk (Fsdb.Auth.tryConsumeAccountStatementAt store account false started) "first question"
              Expect.isError (Fsdb.Auth.tryConsumeAccountStatementAt store account false started) "question limit"
              Expect.isOk
                  (Fsdb.Auth.tryConsumeAccountStatementAt store account false (started.AddHours 1.0))
                  "next question window"

              use first =
                  Fsdb.Auth.tryAcquireAccountConnectionAt store account (started.AddHours 2.0)
                  |> Result.defaultWith (failtestf "%A")

              Expect.isError
                  (Fsdb.Auth.tryAcquireAccountConnectionAt store account (started.AddHours 2.0))
                  "connection limit"
              use _next =
                  Fsdb.Auth.tryAcquireAccountConnectionAt store account (started.AddHours 3.0)
                  |> Result.defaultWith (failtestf "%A")

              ()

          testCase "update limits cover text DDL and SQL-prepared DML without charging procedure bodies"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE TABLE account_updates(id INT)"
              let root, _ = handle root "CREATE USER prepared_limited WITH MAX_UPDATES_PER_HOUR 1"
              let root, _ = handle root "GRANT INSERT ON fsdb.account_updates TO prepared_limited"
              let root, _ = handle root "CREATE USER routine_ddl_limited WITH MAX_UPDATES_PER_HOUR 1"
              let root, _ = handle root "GRANT CREATE ROUTINE,CREATE ON fsdb.* TO routine_ddl_limited"
              let root, _ = handle root "CREATE PROCEDURE counted_call(IN n INT) BEGIN INSERT INTO account_updates VALUES(n); END"
              let root, _ = handle root "CREATE USER call_limited WITH MAX_UPDATES_PER_HOUR 1"
              let _, _ = handle root "GRANT EXECUTE ON fsdb.* TO call_limited"

              let preparedSession = { create 2 store with User = "prepared_limited" }
              let preparedSession, prepared = handle preparedSession "PREPARE add_row FROM 'INSERT INTO account_updates VALUES(?)'"
              Expect.equal prepared (Affected 0UL) "statement prepared"
              let preparedSession, _ = handle preparedSession "SET @id = 1"
              let preparedSession, inserted = handle preparedSession "EXECUTE add_row USING @id"
              Expect.equal inserted (Affected 1UL) "prepared insert"

              match handle preparedSession "EXECUTE add_row USING @id" |> snd with
              | Err(1226, message) -> Expect.stringContains message "max_updates" "prepared update limit"
              | other -> failtestf "expected prepared update limit, got %A" other

              let routineSession = { create 3 store with User = "routine_ddl_limited" }
              let routineSession, created = handle routineSession "CREATE PROCEDURE resource_counted() SELECT 1"
              Expect.equal created (Affected 0UL) "routine DDL"

              match handle routineSession "CREATE TABLE resource_counted_table(id INT)" |> snd with
              | Err(1226, message) -> Expect.stringContains message "max_updates" "routine DDL update limit"
              | other -> failtestf "expected routine DDL update limit, got %A" other

              let callSession = { create 4 store with User = "call_limited" }
              let callSession, firstCall = handle callSession "CALL counted_call(2)"
              let _, secondCall = handle callSession "CALL counted_call(3)"
              Expect.isFalse (match firstCall with Err(1226, _) -> true | _ -> false) "first call is not charged"
              Expect.isFalse (match secondCall with Err(1226, _) -> true | _ -> false) "second call is not charged"

              match handle root "SELECT GROUP_CONCAT(id ORDER BY id) FROM account_updates" |> snd with
              | ResultSet(_, [ [ Some "1,2,3" ] ]) -> ()
              | other -> failtestf "expected prepared and procedure rows, got %A" other

          testCase "roles are locked accounts and retain role-specific duplicate semantics"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "CREATE ROLE 'reader'@'%'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected CREATE ROLE to succeed, got %A" other

              match Fsdb.Auth.tryUserRowForAccount store (Fsdb.Auth.account "reader" "%") with
              | Some(columns, row) -> Expect.isTrue (Fsdb.Auth.isAccountLocked columns row) "roles cannot authenticate"
              | None -> failtest "expected the role account"

              match handle session "SHOW CREATE USER 'reader'@'%'" |> snd with
              | ResultSet(_, [ [ Some ddl ] ]) -> Expect.stringContains ddl "ACCOUNT LOCK" "locked state renders"
              | other -> failtestf "expected SHOW CREATE USER, got %A" other

              match handle session "CREATE ROLE reader" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected duplicate role error, got %A" other

              match handle session "CREATE ROLE IF NOT EXISTS reader" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected duplicate role no-op, got %A" other

              match handle session "SET ROLE NONE" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected SET ROLE NONE to clear the empty active-role set, got %A" other

              match handle session "DROP ROLE reader" |> snd with
              | Affected 0UL -> Expect.isNone (Fsdb.Auth.tryUserRow store "reader") "role removed"
              | other -> failtestf "expected DROP ROLE, got %A" other

          testCase "CREATE USER ACCOUNT LOCK persists the authentication state"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "CREATE USER 'locked'@'%' IDENTIFIED BY 'secret' ACCOUNT LOCK" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "locked" with
                  | Some(columns, row) -> Expect.isTrue (Fsdb.Auth.isAccountLocked columns row) "locked in mysql.user"
                  | None -> failtest "expected locked user"
              | other -> failtestf "expected CREATE USER ACCOUNT LOCK, got %A" other

          testCase "role DDL uses its dedicated global privileges"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER role_admin"
              let root, _ = handle root "GRANT CREATE ROLE ON *.* TO role_admin"
              let roleAdmin = { create 2 store with User = "role_admin" }

              match handle roleAdmin "CREATE ROLE reader" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected CREATE ROLE privilege to authorize creation, got %A" other

              match handle roleAdmin "DROP ROLE reader" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected DROP ROLE to require its own privilege, got %A" other

          testCase "role grants activate transitive privileges defaults and admin options"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE role_db"
              let root, _ = handle root "CREATE TABLE role_db.documents (id INT PRIMARY KEY)"
              let root, _ = handle root "INSERT INTO role_db.documents VALUES (1)"
              let root, _ = handle root "CREATE ROLE reader, parent, cycle_a, cycle_b"
              let root, _ = handle root "CREATE USER alice, bob"
              let root, _ = handle root "GRANT SELECT ON role_db.* TO reader"
              let root, _ = handle root "GRANT reader TO parent"
              let root, _ = handle root "GRANT parent TO alice WITH ADMIN OPTION"

              let alice = { create 2 store with User = "alice"; AccountHost = "%" }

              match handle alice "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "NONE" ] ]) -> ()
              | other -> failtestf "expected no initially active role, got %A" other

              match handle alice "SELECT * FROM role_db.documents" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected inactive role privilege refusal, got %A" other

              let alice, activated = handle alice "SET ROLE parent"
              Expect.equal activated (Affected 0UL) "direct role activates"

              match handle alice "SELECT CURRENT_ROLE(), id FROM role_db.documents" |> snd with
              | ResultSet(_, [ [ Some "`parent`@`%`"; Some "1" ] ]) -> ()
              | other -> failtestf "expected inherited reader privilege, got %A" other

              match handle alice "SET ROLE reader" |> snd with
              | Err(3530, message) -> Expect.stringContains message "not granted" "only directly granted roles activate"
              | other -> failtestf "expected indirect role activation refusal, got %A" other

              match handle alice "GRANT parent TO bob" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected admin-option delegation, got %A" other

              let bob = { create 3 store with User = "bob"; AccountHost = "%" }
              let bob, _ = handle bob "SET ROLE parent"

              match handle bob "SELECT id FROM role_db.documents" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected delegated role privilege, got %A" other

              let root, _ = handle root "GRANT cycle_a TO cycle_b"

              match handle root "GRANT cycle_b TO cycle_a" |> snd with
              | Err(4027, message) -> Expect.stringContains message "create a loop" "cycle refusal"
              | other -> failtestf "expected role-cycle refusal, got %A" other

              let alice, defaulted = handle alice "SET DEFAULT ROLE parent TO alice"
              Expect.equal defaulted (Affected 0UL) "users may select their own direct default role"
              let alice, _ = handle alice "SET ROLE NONE"

              match handle alice "SELECT id FROM role_db.documents" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected SET ROLE NONE to remove inherited access, got %A" other

              let alice, _ = handle alice "SET ROLE DEFAULT"

              match handle alice "SELECT CURRENT_ROLE(), id FROM role_db.documents" |> snd with
              | ResultSet(_, [ [ Some "`parent`@`%`"; Some "1" ] ]) -> ()
              | other -> failtestf "expected default role reactivation, got %A" other

              match
                  handle
                      alice
                      "SELECT ROLE_NAME, ROLE_HOST, IS_DEFAULT, IS_MANDATORY FROM information_schema.ENABLED_ROLES ORDER BY ROLE_NAME"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "parent"; Some "%"; Some "YES"; Some "NO" ]
                    [ Some "reader"; Some "%"; Some "NO"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected active and inherited role metadata, got %A" other

              match
                  handle
                      alice
                      "SELECT GRANTEE, ROLE_NAME, IS_GRANTABLE, IS_DEFAULT FROM information_schema.APPLICABLE_ROLES ORDER BY GRANTEE, ROLE_NAME"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "alice"; Some "parent"; Some "YES"; Some "YES" ]
                    [ Some "parent"; Some "reader"; Some "NO"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected direct and inherited applicable roles, got %A" other

              match handle alice "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'role_db'" |> snd with
              | ResultSet(_, [ [ Some "documents" ] ]) -> ()
              | other -> failtestf "expected role privileges to reveal table metadata, got %A" other

              let alice, prepared = handle alice "PREPARE clear_roles FROM 'SET ROLE NONE'"
              Expect.equal prepared (Affected 0UL) "role statement prepares"
              let alice, executed = handle alice "EXECUTE clear_roles"
              Expect.equal executed (Affected 0UL) "prepared role statement executes in the session"

              match handle alice "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "NONE" ] ]) -> ()
              | other -> failtestf "expected the prepared statement to clear active roles, got %A" other

              match handle root "SHOW GRANTS FOR alice" |> snd with
              | ResultSet(_, rows) ->
                  Expect.isTrue
                      (rows |> List.exists (fun row -> row = [ Some "GRANT `parent`@`%` TO `alice`@`%` WITH ADMIN OPTION" ]))
                      "role grant appears in SHOW GRANTS"
              | other -> failtestf "expected role grant metadata, got %A" other

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
                  let grants = rows |> List.map (List.head >> Option.get)
                  Expect.equal (List.head grants) "GRANT ALL PRIVILEGES ON *.* TO `root`@`%` WITH GRANT OPTION" "static grants"
                  Expect.equal grants.Length 2 "static and dynamic global lines"
                  Expect.stringContains grants.[1] "XA_RECOVER_ADMIN" "bootstrap dynamic privileges"
                  Expect.stringEnds grants.[1] "WITH GRANT OPTION" "bootstrap dynamic privileges are grantable"
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

          testCase "dynamic global privileges retain individual grant options and metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER grantor, target"
              let root, _ = handle root "GRANT XA_RECOVER_ADMIN ON *.* TO grantor"
              let root, _ = handle root "GRANT BACKUP_ADMIN ON *.* TO grantor WITH GRANT OPTION"

              match handle root "SHOW GRANTS FOR grantor" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `grantor`@`%`"
                        "GRANT XA_RECOVER_ADMIN ON *.* TO `grantor`@`%`"
                        "GRANT BACKUP_ADMIN ON *.* TO `grantor`@`%` WITH GRANT OPTION" ]
                      "dynamic privileges are grouped by grantability"
              | other -> failtestf "expected dynamic grants, got %A" other

              match handle root "SELECT PRIV, WITH_GRANT_OPTION FROM mysql.global_grants WHERE USER = 'grantor' ORDER BY PRIV" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "BACKUP_ADMIN"; Some "Y" ]; [ Some "XA_RECOVER_ADMIN"; Some "N" ] ] "stored grants"
              | other -> failtestf "expected mysql.global_grants rows, got %A" other

              match handle root "SELECT PRIVILEGE_TYPE, IS_GRANTABLE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = \"'grantor'@'%'\" ORDER BY PRIVILEGE_TYPE" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "BACKUP_ADMIN"; Some "YES" ]
                        [ Some "USAGE"; Some "NO" ]
                        [ Some "XA_RECOVER_ADMIN"; Some "NO" ] ]
                      "information_schema combines static usage and dynamic grants"
              | other -> failtestf "expected dynamic USER_PRIVILEGES rows, got %A" other

              let grantor = { create 2 store with User = "grantor"; AccountHost = "%" }

              match handle grantor "GRANT XA_RECOVER_ADMIN ON *.* TO target" |> snd with
              | Err(1227, message) -> Expect.stringContains message "GRANT OPTION" "non-grantable privilege"
              | other -> failtestf "expected dynamic grant-option refusal, got %A" other

              match handle grantor "GRANT BACKUP_ADMIN ON *.* TO target" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected grantable dynamic privilege to delegate, got %A" other

              let grantor, _ = handle grantor "REVOKE BACKUP_ADMIN ON *.* FROM target"

              match handle grantor "REVOKE BACKUP_ADMIN ON *.* FROM target" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected absent dynamic revoke to succeed, got %A" other

              match handle root "GRANT XA_RECOVER_ADMIN ON shop.* TO target" |> snd with
              | Err(3619, message) -> Expect.stringContains message "XA_RECOVER_ADMIN" "global-only privilege level"
              | other -> failtestf "expected dynamic privilege level refusal, got %A" other

              match handle root "GRANT MADE_UP_ADMIN ON *.* TO target" |> snd with
              | Err(1149, message) -> Expect.stringContains message "MADE_UP_ADMIN" "unknown privilege"
              | other -> failtestf "expected unknown privilege refusal, got %A" other

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
