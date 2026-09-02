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
                            "component"
                            "db"
                            "default_roles"
                            "events"
                            "func"
                            "functions"
                            "global_grants"
                            "password_history"
                            "plugin"
                            "proxies_priv"
                            "role_edges"
                            "routines"
                            "servers"
                            "tables_priv"
                            "time_zone"
                            "time_zone_leap_second"
                            "time_zone_name"
                            "time_zone_transition"
                            "time_zone_transition_type"
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

          testCase "CREATE and ALTER USER expose mergeable account attributes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, commentCreated = handle root "CREATE USER attr_comment COMMENT 'hello'"
              let root, jsonCreated = handle root "CREATE USER attr_json ATTRIBUTE '{\"team\":\"db\",\"n\":1}'"

              Expect.equal commentCreated (Affected 0UL) "comment account created"
              Expect.equal jsonCreated (Affected 0UL) "attribute account created"

              match
                  handle
                      root
                      "SELECT User, User_attributes FROM mysql.user WHERE User IN ('attr_comment','attr_json') ORDER BY User"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "attr_comment"; Some "{\"metadata\": {\"comment\": \"hello\"}}" ]
                    [ Some "attr_json"; Some "{\"metadata\": {\"n\": 1, \"team\": \"db\"}}" ] ]
                ) ->
                  ()
              | other -> failtestf "expected wrapped mysql.user attributes, got %A" other

              match
                  handle
                      root
                      "SELECT USER, ATTRIBUTE FROM information_schema.USER_ATTRIBUTES WHERE USER LIKE 'attr_%' ORDER BY USER"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "attr_comment"; Some "{\"comment\": \"hello\"}" ]
                    [ Some "attr_json"; Some "{\"n\": 1, \"team\": \"db\"}" ] ]
                ) ->
                  ()
              | other -> failtestf "expected unwrapped information-schema attributes, got %A" other

              let root, merged = handle root "ALTER USER attr_json ATTRIBUTE '{\"n\":2,\"team\":null}'"
              let root, commented = handle root "ALTER USER attr_json COMMENT 'second'"
              Expect.equal merged (Affected 0UL) "attribute patch merged"
              Expect.equal commented (Affected 0UL) "comment patch merged"

              match handle root "SHOW CREATE USER attr_json" |> snd with
              | ResultSet(_, [ [ Some ddl ] ]) ->
                  Expect.stringContains ddl "ATTRIBUTE '{\"n\": 2, \"comment\": \"second\"}'" "merged attribute"
              | other -> failtestf "expected attributed SHOW CREATE USER, got %A" other

              match handle root "CREATE USER invalid_attribute ATTRIBUTE '[]'" |> snd with
              | Err(3982, "The user attribute must be a valid JSON object") ->
                  Expect.isNone (Fsdb.Auth.tryUserRow store "invalid_attribute") "invalid account was not created"
              | other -> failtestf "expected invalid attribute error 3982, got %A" other

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

          testCase "PASSWORD EXPIRE DEFAULT inherits the global lifetime"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, inherited = handle root "CREATE USER inherited_password PASSWORD EXPIRE DEFAULT"
              Expect.equal inherited (Affected 0UL) "default-policy account created"
              let _, exempt = handle root "CREATE USER exempt_password PASSWORD EXPIRE NEVER"
              Expect.equal exempt (Affected 0UL) "never-expire account created"

              let accountRow name =
                  Fsdb.Auth.tryUserRow store name
                  |> Option.defaultWith (fun () -> failtestf "expected account %s" name)

              let columns, row = accountRow "inherited_password"

              let changed =
                  List.zip columns (Array.toList row)
                  |> List.pick (fun (column, value) ->
                      match column.Name, value with
                      | "password_last_changed", VDateTime timestamp -> Some timestamp
                      | _ -> None)

              Expect.isFalse
                  (Fsdb.Auth.isPasswordExpiredAtWithDefault 30 (changed.AddDays 29.0) columns row)
                  "default lifetime remains active before its boundary"

              Expect.isTrue
                  (Fsdb.Auth.isPasswordExpiredAtWithDefault 30 (changed.AddDays 30.0) columns row)
                  "default lifetime expires at its boundary"

              let exemptColumns, exemptRow = accountRow "exempt_password"

              Expect.isFalse
                  (Fsdb.Auth.isPasswordExpiredAtWithDefault 30 (changed.AddDays 365.0) exemptColumns exemptRow)
                  "PASSWORD EXPIRE NEVER overrides the global lifetime"

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

              match handle session "SET-- fuzz\nROLE-- fuzz\nNONE" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected comments between SET ROLE tokens, got %A" other

              match handle session "SET# fuzz\nDEFAULT# fuzz\nROLE NONE TO root" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected comments between SET DEFAULT ROLE tokens, got %A" other

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
              let root, _ = handle root "GRANT SELECT ON role_db.documents TO reader"
              let root, _ = handle root "GRANT UPDATE(id) ON role_db.documents TO parent"
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
              | ResultSet(_, [ [ Some "parent"; Some "%"; Some "YES"; Some "NO" ] ]) -> ()
              | other -> failtestf "expected explicitly active role metadata, got %A" other

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

              match
                  handle
                      alice
                      "SELECT USER, GRANTEE, ROLE_NAME, IS_GRANTABLE, IS_DEFAULT, IS_MANDATORY FROM information_schema.ADMINISTRABLE_ROLE_AUTHORIZATIONS"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "alice"; Some "alice"; Some "parent"; Some "YES"; Some "YES"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected administrable role metadata, got %A" other

              match
                  handle
                      alice
                      "SELECT GRANTOR, GRANTOR_HOST, GRANTEE, GRANTEE_HOST, TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, PRIVILEGE_TYPE, IS_GRANTABLE FROM information_schema.ROLE_TABLE_GRANTS"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "root"; Some "%"; Some "reader"; Some "%"; Some "def"; Some "role_db"
                      Some "documents"; Some "Select"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected inherited role table grants, got %A" other

              match
                  handle
                      alice
                      "SELECT GRANTOR, GRANTOR_HOST, GRANTEE, GRANTEE_HOST, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, PRIVILEGE_TYPE, IS_GRANTABLE FROM information_schema.ROLE_COLUMN_GRANTS"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "root"; Some "%"; Some "parent"; Some "%"; Some "role_db"; Some "documents"
                      Some "id"; Some "Update"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected active role column grants, got %A" other

              match handle alice "SELECT * FROM information_schema.ROLE_ROUTINE_GRANTS" |> snd with
              | ResultSet(
                  [ "GRANTOR"; "GRANTOR_HOST"; "GRANTEE"; "GRANTEE_HOST"; "SPECIFIC_CATALOG"; "SPECIFIC_SCHEMA"
                    "SPECIFIC_NAME"; "ROUTINE_CATALOG"; "ROUTINE_SCHEMA"; "ROUTINE_NAME"; "PRIVILEGE_TYPE"
                    "IS_GRANTABLE" ],
                  []
                ) ->
                  ()
              | other -> failtestf "expected the empty role routine grant surface, got %A" other

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

              match handle root "SHOW GRANTS FOR alice USING parent" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `alice`@`%`"
                        "GRANT SELECT, UPDATE (`id`) ON `role_db`.`documents` TO `alice`@`%`"
                        "GRANT `parent`@`%` TO `alice`@`%` WITH ADMIN OPTION" ]
                      "USING materializes inherited role privileges"
              | other -> failtestf "expected materialized role grants, got %A" other

              match handle root "SHOW GRANTS FOR alice USING reader" |> snd with
              | Err(3530, _) -> ()
              | other -> failtestf "expected indirect USING role refusal, got %A" other

          testCase "delegated grants retain their actual grantor"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE grantor_db"
              let root, _ = handle root "CREATE TABLE grantor_db.documents (id INT)"
              let root, _ = handle root "CREATE USER delegator"
              let root, _ = handle root "CREATE ROLE delegated_reader"

              let _, _ =
                  handle
                      root
                      "GRANT SELECT ON grantor_db.documents TO delegator WITH GRANT OPTION"

              let delegator = { create 2 store with User = "delegator" }

              match handle delegator "GRANT SELECT ON grantor_db.documents TO delegated_reader" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected delegated table grant, got %A" other

              match
                  handle
                      root
                      "SELECT Grantor FROM mysql.tables_priv WHERE User = 'delegated_reader' AND Db = 'grantor_db'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "delegator@%" ] ]) -> ()
              | other -> failtestf "expected the delegated grantor identity, got %A" other

          testCase "role selection and lifecycle keep grant catalogs consistent"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE ROLE alpha, beta"
              let root, _ = handle root "CREATE USER member, delegate"
              let root, _ = handle root "GRANT alpha, beta TO member"
              let root, _ = handle root "GRANT alpha TO member WITH ADMIN OPTION"
              let root, _ = handle root "GRANT alpha TO member"
              let root, _ = handle root "SET DEFAULT ROLE ALL TO member"

              let memberSession = { create 2 store with User = "member"; AccountHost = "%" }
              let memberSession, _ = handle memberSession "SET ROLE ALL EXCEPT beta"

              match handle memberSession "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "`alpha`@`%`" ] ]) -> ()
              | other -> failtestf "expected ALL EXCEPT to retain alpha, got %A" other

              let memberSession, _ = handle memberSession "SET ROLE beta, alpha"

              match handle memberSession "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "`alpha`@`%`,`beta`@`%`" ] ]) -> ()
              | other -> failtestf "expected canonical active-role order, got %A" other

              match handle root "SELECT WITH_ADMIN_OPTION FROM mysql.role_edges WHERE FROM_USER = 'alpha' AND TO_USER = 'member'" |> snd with
              | ResultSet(_, [ [ Some "Y" ] ]) -> ()
              | other -> failtestf "expected repeated grants not to remove admin option, got %A" other

              match handle root "SET DEFAULT ROLE alpha TO member, missing" |> snd with
              | Err(3523, _) -> ()
              | other -> failtestf "expected the unknown default-role target to reject the statement, got %A" other

              match handle root "SELECT DEFAULT_ROLE_USER FROM mysql.default_roles WHERE USER = 'member' ORDER BY DEFAULT_ROLE_USER" |> snd with
              | ResultSet(_, [ [ Some "alpha" ]; [ Some "beta" ] ]) -> ()
              | other -> failtestf "expected failed multi-account default change to remain atomic, got %A" other

              let delegateSession = { create 3 store with User = "delegate"; AccountHost = "%" }

              match handle delegateSession "GRANT alpha TO delegate" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected a non-administrator role grant refusal, got %A" other

              let root, _ = handle root "GRANT ROLE_ADMIN ON *.* TO delegate"

              match handle delegateSession "GRANT alpha TO delegate" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ROLE_ADMIN to authorize role grants, got %A" other

              match handle root "RENAME USER alpha TO renamed" |> snd with
              | Err(3532, _) -> ()
              | other -> failtestf "expected a granted role identifier not to be renamed, got %A" other

              let root, _ = handle root "REVOKE alpha FROM member"

              match handle root "SELECT DEFAULT_ROLE_USER FROM mysql.default_roles WHERE USER = 'member'" |> snd with
              | ResultSet(_, [ [ Some "beta" ] ]) -> ()
              | other -> failtestf "expected revoke to remove the matching default role, got %A" other

              let root, _ = handle root "DROP ROLE beta"

              match handle root "SELECT COUNT(*) FROM mysql.role_edges WHERE FROM_USER = 'beta' OR TO_USER = 'beta'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected DROP ROLE to remove graph edges, got %A" other

              match handle root "SELECT COUNT(*) FROM mysql.default_roles WHERE DEFAULT_ROLE_USER = 'beta'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected DROP ROLE to remove default-role rows, got %A" other

          testCase "mandatory roles are applicable but remain explicitly activatable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE mandatory_db"
              let root, _ = handle root "CREATE TABLE mandatory_db.documents (id INT PRIMARY KEY)"
              let root, _ = handle root "INSERT INTO mandatory_db.documents VALUES (1)"
              let root, _ = handle root "CREATE ROLE mandatory_parent, mandatory_reader"
              let root, _ = handle root "CREATE USER alice"
              let root, _ = handle root "GRANT SELECT ON mandatory_db.* TO mandatory_reader"
              let root, _ = handle root "GRANT mandatory_reader TO mandatory_parent"
              let root, configured = handle root "SET GLOBAL mandatory_roles = 'mandatory_parent@%'"
              Expect.equal configured (Affected 0UL) "mandatory roles configure globally"

              match
                  handle
                      root
                      "SELECT @@mandatory_roles, @@global.mandatory_roles, @@activate_all_roles_on_login"
                  |> snd
              with
              | ResultSet(_, [ [ Some "mandatory_parent@%"; Some "mandatory_parent@%"; Some "OFF" ] ]) -> ()
              | other -> failtestf "expected global role variables, got %A" other

              match handle root "SELECT @@session.mandatory_roles" |> snd with
              | Err(1238, _) -> ()
              | other -> failtestf "expected mandatory_roles to reject SESSION scope, got %A" other

              let aliceAccount = Fsdb.Auth.account "alice" "%"

              let alice =
                  { create 2 store with
                      User = "alice"
                      ActiveRoles = initialRoles store aliceAccount }

              match handle alice "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "NONE" ] ]) -> ()
              | other -> failtestf "expected mandatory roles to start inactive, got %A" other

              match
                  handle
                      alice
                      "SELECT GRANTEE, ROLE_NAME, IS_MANDATORY FROM information_schema.APPLICABLE_ROLES ORDER BY GRANTEE, ROLE_NAME"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "alice"; Some "mandatory_parent"; Some "YES" ]
                    [ Some "mandatory_parent"; Some "mandatory_reader"; Some "NO" ] ]
                ) ->
                  ()
              | other -> failtestf "expected mandatory and inherited applicable roles, got %A" other

              let alice, activated = handle alice "SET ROLE mandatory_parent"
              Expect.equal activated (Affected 0UL) "mandatory role activates without a direct grant"

              match handle alice "SELECT CURRENT_ROLE(), id FROM mandatory_db.documents" |> snd with
              | ResultSet(_, [ [ Some "`mandatory_parent`@`%`"; Some "1" ] ]) -> ()
              | other -> failtestf "expected mandatory role inheritance, got %A" other

              match
                  handle
                      alice
                      "SELECT ROLE_NAME, IS_DEFAULT, IS_MANDATORY FROM information_schema.ENABLED_ROLES"
                  |> snd
              with
              | ResultSet(_, [ [ Some "mandatory_parent"; Some "NO"; Some "YES" ] ]) -> ()
              | other -> failtestf "expected only the active mandatory root, got %A" other

              let alice, _ = handle alice "SET ROLE NONE"

              match handle alice "SELECT id FROM mandatory_db.documents" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected SET ROLE NONE to deactivate mandatory privileges, got %A" other

              match handle root "REVOKE mandatory_parent FROM alice" |> snd with
              | Err(3628, _) -> ()
              | other -> failtestf "expected mandatory-role revoke refusal, got %A" other

              match handle root "DROP ROLE mandatory_parent" |> snd with
              | Err(3628, _) -> ()
              | other -> failtestf "expected mandatory-role drop refusal, got %A" other

              match handle root "DROP USER mandatory_parent" |> snd with
              | Err(3628, _) -> ()
              | other -> failtestf "expected mandatory-account drop refusal, got %A" other

              let root, defaulted = handle root "SET DEFAULT ROLE mandatory_parent TO alice"
              Expect.equal defaulted (Affected 0UL) "mandatory roles can be defaults"

              let defaultSession =
                  { create 3 store with
                      User = "alice"
                      ActiveRoles = initialRoles store aliceAccount }

              match handle defaultSession "SELECT CURRENT_ROLE()" |> snd with
              | ResultSet(_, [ [ Some "`mandatory_parent`@`%`" ] ]) -> ()
              | other -> failtestf "expected mandatory default activation, got %A" other

              let root, _ = handle root "SET GLOBAL mandatory_roles = ''"
              let inactiveSession =
                  { create 4 store with
                      User = "alice"
                      ActiveRoles = initialRoles store aliceAccount }

              match handle inactiveSession "SET ROLE DEFAULT" |> snd with
              | Err(3530, _) -> ()
              | other -> failtestf "expected stale mandatory default refusal, got %A" other

              match handle root "SET mandatory_roles = 'mandatory_parent@%'" |> snd with
              | Err(1229, _) -> ()
              | other -> failtestf "expected GLOBAL-only mandatory_roles, got %A" other

              match handle root "SET activate_all_roles_on_login = ON" |> snd with
              | Err(1229, _) -> ()
              | other -> failtestf "expected GLOBAL-only login role activation, got %A" other

              match handle root "SET GLOBAL mandatory_roles = 'broken@'" |> snd with
              | Err(1231, _) -> ()
              | other -> failtestf "expected malformed mandatory role refusal, got %A" other

              let root, _ = handle root "SET GLOBAL mandatory_roles = 'ghost@%'"

              match handle root "DROP ROLE ghost" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected an absent configured role to remain absent, got %A" other

              let root, resetMandatory = handle root "SET GLOBAL mandatory_roles = DEFAULT"
              Expect.equal resetMandatory (Affected 0UL) "mandatory roles reset to the global default"
              let root, _ = handle root "SET GLOBAL activate_all_roles_on_login = ON"
              let root, resetActivation = handle root "SET GLOBAL activate_all_roles_on_login = DEFAULT"
              Expect.equal resetActivation (Affected 0UL) "login activation resets to the global default"

              match handle root "SELECT @@mandatory_roles, @@activate_all_roles_on_login" |> snd with
              | ResultSet(_, [ [ Some ""; Some "OFF" ] ]) -> ()
              | other -> failtestf "expected role variable defaults, got %A" other

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

          testCase "column grants persist and render through grant metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE column_db"
              let root, _ = handle root "CREATE TABLE column_db.orders (id INT, total INT, status INT, customer_id INT, hidden POINT)"
              let root, _ = handle root "CREATE USER column_reader"

              let root, granted =
                  handle
                      root
                      "GRANT SELECT(id, total), UPDATE(status), REFERENCES(customer_id) ON column_db.orders TO column_reader WITH GRANT OPTION"

              Expect.equal granted (Affected 0UL) "grant succeeds"

              match handle root "SHOW GRANTS FOR column_reader" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `column_reader`@`%`"
                        "GRANT SELECT (`id`, `total`), UPDATE (`status`), REFERENCES (`customer_id`) ON `column_db`.`orders` TO `column_reader`@`%` WITH GRANT OPTION" ]
                      "column grants render by privilege"
              | other -> failtestf "expected column grants, got %A" other

              match
                  handle
                      root
                      "SELECT Table_priv, Column_priv FROM mysql.tables_priv WHERE User = 'column_reader'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "Grant"; Some "Select,Update,References" ] ]) -> ()
              | other -> failtestf "expected the table privilege summary, got %A" other

              match
                  handle
                      root
                      "SELECT Column_name, Column_priv FROM mysql.columns_priv WHERE User = 'column_reader' ORDER BY Column_name"
                  |> snd
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "customer_id"; Some "References" ]
                        [ Some "id"; Some "Select" ]
                        [ Some "status"; Some "Update" ]
                        [ Some "total"; Some "Select" ] ]
                      "one row stores each column's privilege set"
              | other -> failtestf "expected mysql.columns_priv rows, got %A" other

              match
                  handle
                      root
                      "SELECT COLUMN_NAME, PRIVILEGE_TYPE, IS_GRANTABLE FROM information_schema.COLUMN_PRIVILEGES WHERE GRANTEE = \"'column_reader'@'%'\" ORDER BY COLUMN_NAME, PRIVILEGE_TYPE"
                  |> snd
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "customer_id"; Some "REFERENCES"; Some "YES" ]
                        [ Some "id"; Some "SELECT"; Some "YES" ]
                        [ Some "status"; Some "UPDATE"; Some "YES" ]
                        [ Some "total"; Some "SELECT"; Some "YES" ] ]
                      "information_schema exposes column grants"
              | other -> failtestf "expected COLUMN_PRIVILEGES rows, got %A" other

              let reader = { create 2 store with User = "column_reader" }

              match handle reader "SHOW FULL COLUMNS FROM column_db.orders" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (fun row -> row.[0], row.[7]))
                      [ Some "id", Some "select"
                        Some "total", Some "select"
                        Some "status", Some "update"
                        Some "customer_id", Some "references" ]
                      "only columns carrying a privilege are visible"
              | other -> failtestf "expected scoped SHOW FULL COLUMNS, got %A" other

              match
                  handle
                      reader
                      "SELECT column_name FROM information_schema.columns_extensions WHERE table_schema = 'column_db' AND table_name = 'orders' ORDER BY column_name"
                  |> snd
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "customer_id" ]; [ Some "id" ]; [ Some "status" ]; [ Some "total" ] ]
                      "extension metadata follows column grants"
              | other -> failtestf "expected scoped extension metadata, got %A" other

              match
                  handle
                      reader
                      "SELECT column_name FROM information_schema.st_geometry_columns WHERE table_schema = 'column_db' AND table_name = 'orders'"
                  |> snd
              with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected hidden geometry metadata, got %A" other

              let _, _ = handle root "GRANT SELECT(hidden) ON column_db.orders TO column_reader"

              match
                  handle
                      reader
                      "SELECT column_name, geometry_type_name FROM information_schema.st_geometry_columns WHERE table_schema = 'column_db' AND table_name = 'orders'"
                  |> snd
              with
              | ResultSet(_, [ [ Some "hidden"; Some "point" ] ]) -> ()
              | other -> failtestf "expected visible geometry metadata, got %A" other

          testCase "view dependency metadata requires dependency privileges"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE dependency_db"
              let root, _ = handle root "CREATE TABLE dependency_db.source_rows (id INT)"

              let root, _ =
                  handle
                      root
                      "CREATE FUNCTION dependency_db.incremented(value INT) RETURNS INT DETERMINISTIC RETURN value + 1"

              let root, _ =
                  handle root "CREATE VIEW dependency_db.table_view AS SELECT id FROM dependency_db.source_rows"

              let root, _ =
                  handle
                      root
                      "CREATE VIEW dependency_db.routine_view AS SELECT dependency_db.incremented(id) AS id FROM dependency_db.source_rows"

              let root, _ = handle root "CREATE USER dependency_reader"
              let root, _ = handle root "GRANT SELECT ON dependency_db.source_rows TO dependency_reader"
              let root, _ = handle root "GRANT SELECT ON dependency_db.table_view TO dependency_reader"
              let root, _ = handle root "GRANT SELECT ON dependency_db.routine_view TO dependency_reader"
              let reader = { create 2 store with User = "dependency_reader" }

              let tableUsage () =
                  handle
                      reader
                      "SELECT view_name, table_name FROM information_schema.view_table_usage WHERE view_schema = 'dependency_db' ORDER BY view_name"
                  |> snd

              let routineUsage () =
                  handle
                      reader
                      "SELECT table_name, specific_name FROM information_schema.view_routine_usage WHERE table_schema = 'dependency_db'"
                  |> snd

              Expect.equal (tableUsage ()) (ResultSet([ "view_name"; "table_name" ], [])) "SHOW VIEW guards table usage"
              Expect.equal (routineUsage ()) (ResultSet([ "table_name"; "specific_name" ], [])) "SHOW VIEW guards routine usage"

              let root, _ = handle root "GRANT SHOW VIEW ON dependency_db.table_view TO dependency_reader"
              let root, _ = handle root "GRANT SHOW VIEW ON dependency_db.routine_view TO dependency_reader"

              match tableUsage () with
              | ResultSet(
                  _,
                  [ [ Some "routine_view"; Some "source_rows" ]
                    [ Some "table_view"; Some "source_rows" ] ]
                ) ->
                  ()
              | other -> failtestf "expected visible table dependencies, got %A" other

              Expect.equal (routineUsage ()) (ResultSet([ "table_name"; "specific_name" ], [])) "EXECUTE guards routine usage"
              let _, _ = handle root "GRANT EXECUTE ON dependency_db.* TO dependency_reader"

              match routineUsage () with
              | ResultSet(_, [ [ Some "routine_view"; Some "incremented" ] ]) -> ()
              | other -> failtestf "expected visible routine dependency, got %A" other

          testCase "column grants authorize only referenced and written columns"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE column_auth"
              let root, _ = handle root "CREATE TABLE column_auth.items (id INT, status INT, hidden INT)"
              let root, _ = handle root "CREATE TABLE column_auth.details (id INT, public_value INT, secret_value INT)"
              let root, _ = handle root "INSERT INTO column_auth.items VALUES (1, 0, 9)"
              let root, _ = handle root "INSERT INTO column_auth.details VALUES (1, 7, 99)"
              let root, _ = handle root "CREATE VIEW column_auth.visible_items AS SELECT id, hidden FROM column_auth.items"
              let root, _ = handle root "CREATE VIEW column_auth.computed_items AS SELECT id + 1 AS next_id, hidden + 1 AS next_hidden FROM column_auth.items"
              let root, _ = handle root "CREATE USER partial"

              let root, _ =
                  handle
                      root
                      "GRANT SELECT(id), INSERT(id), UPDATE(status) ON column_auth.items TO partial"

              let root, _ =
                  handle
                      root
                      "GRANT SELECT(id, public_value), UPDATE(secret_value) ON column_auth.details TO partial"
              let root, _ = handle root "GRANT SELECT(id) ON column_auth.visible_items TO partial"
              let _, _ = handle root "GRANT SELECT(next_id) ON column_auth.computed_items TO partial"

              let partial =
                  { create 2 store with
                      User = "partial"
                      Database = Some "column_auth" }

              let expectRows sql expected =
                  match handle partial sql |> snd with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | other -> failtestf "expected rows for %s, got %A" sql other

              let expectColumnDenied command column sql =
                  match handle partial sql |> snd with
                  | Err(1143, message) ->
                      Expect.stringContains message command "command"
                      Expect.stringContains message (sprintf "column '%s'" column) "column"
                  | other -> failtestf "expected 1143 for %s, got %A" sql other

              expectRows "SELECT id FROM items" [ [ Some "1" ] ]
              expectRows "SELECT COUNT(*) FROM items" [ [ Some "1" ] ]
              expectRows "WITH visible AS (SELECT id FROM items) SELECT id FROM visible" [ [ Some "1" ] ]
              expectRows "SELECT id FROM (SELECT id FROM items) AS visible" [ [ Some "1" ] ]
              expectRows
                  "SELECT d.public_value FROM items AS i JOIN details AS d ON d.id = i.id"
                  [ [ Some "7" ] ]
              expectRows
                  "SELECT d.public_value FROM items AS i JOIN details AS d USING (id)"
                  [ [ Some "7" ] ]
              expectRows
                  "SELECT d.public_value FROM items AS i NATURAL JOIN details AS d"
                  [ [ Some "7" ] ]
              expectRows
                  "SELECT id, (SELECT public_value FROM details WHERE details.id = items.id) FROM items"
                  [ [ Some "1"; Some "7" ] ]
              expectRows "SELECT id FROM visible_items" [ [ Some "1" ] ]
              expectRows "SELECT next_id FROM computed_items" [ [ Some "2" ] ]
              expectRows "SELECT id AS projected FROM items GROUP BY projected" [ [ Some "1" ] ]
              expectColumnDenied "SELECT" "hidden" "SELECT hidden FROM items"
              expectColumnDenied "SELECT" "hidden" "SELECT i.id FROM items AS i WHERE i.hidden = 9"
              expectColumnDenied
                  "SELECT"
                  "secret_value"
                  "SELECT d.secret_value FROM items AS i JOIN details AS d ON d.id = i.id"
              expectColumnDenied
                  "SELECT"
                  "secret_value"
                  "SELECT id, (SELECT secret_value FROM details WHERE details.id = items.id) FROM items"
              expectColumnDenied "SELECT" "hidden" "SELECT hidden FROM visible_items"
              expectColumnDenied "SELECT" "next_hidden" "SELECT next_hidden FROM computed_items"
              expectColumnDenied "SELECT" "hidden" "SELECT id AS hidden FROM items GROUP BY hidden"
              expectColumnDenied
                  "SELECT"
                  "hidden"
                  "SELECT id AS hidden, ROW_NUMBER() OVER (ORDER BY hidden) FROM items"
              expectColumnDenied "SELECT" "hidden" "SELECT id FROM items WHERE hidden = 9"
              expectColumnDenied "SELECT" "hidden" "WITH hidden_cte AS (SELECT hidden FROM items) SELECT hidden FROM hidden_cte"

              match handle partial "SELECT * FROM items" |> snd with
              | Err(1142, message) -> Expect.stringContains message "SELECT command denied" "table-wide read"
              | other -> failtestf "expected table-level denial for SELECT *, got %A" other

              match handle partial "INSERT INTO items (id) VALUES (2)" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected permitted column insert, got %A" other

              match handle partial "INSERT INTO items VALUES (3, 0, 0)" |> snd with
              | Err(1142, message) -> Expect.stringContains message "INSERT command denied" "implicit all-column insert"
              | other -> failtestf "expected table-level insert denial, got %A" other

              match handle partial "UPDATE items SET status = id WHERE id = 1" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected permitted update, got %A" other

              match
                  handle
                      partial
                      "UPDATE items AS i JOIN details AS d ON d.id = i.id SET secret_value = 8 WHERE i.id = 1"
                  |> snd
              with
              | Affected 1UL -> ()
              | other -> failtestf "expected the unqualified target to resolve to details, got %A" other

              expectColumnDenied "UPDATE" "hidden" "UPDATE items SET hidden = 1 WHERE id = 1"
              expectColumnDenied "SELECT" "hidden" "UPDATE items SET status = hidden WHERE id = 1"

              match handle partial "DELETE FROM items WHERE id = 1" |> snd with
              | Err(1142, message) -> Expect.stringContains message "DELETE command denied" "delete stays table-scoped"
              | other -> failtestf "expected DELETE denial, got %A" other

          testCase "column grant validation, delegation, and revocation are exact"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE column_admin"
              let root, _ = handle root "CREATE TABLE column_admin.records (id INT, hidden INT)"
              let root, _ = handle root "CREATE USER grantor, recipient"

              match handle root "GRANT SELECT(missing) ON column_admin.records TO grantor" |> snd with
              | Err(1054, message) -> Expect.stringContains message "Unknown column 'missing'" "unknown column"
              | other -> failtestf "expected unknown grant column, got %A" other

              match handle root "GRANT SELECT(id) ON column_admin.* TO grantor" |> snd with
              | Err(1144, _) -> ()
              | other -> failtestf "expected database-level column grant refusal, got %A" other

              let root, _ = handle root "GRANT SELECT(id) ON column_admin.records TO grantor WITH GRANT OPTION"
              let grantor = { create 2 store with User = "grantor" }

              match handle grantor "GRANT SELECT(id) ON column_admin.records TO recipient" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected exact column delegation, got %A" other

              match handle grantor "GRANT SELECT(hidden) ON column_admin.records TO recipient" |> snd with
              | Err(1143, message) -> Expect.stringContains message "column 'hidden'" "delegated column"
              | other -> failtestf "expected unheld column delegation denial, got %A" other

              match handle root "REVOKE SELECT(hidden) ON column_admin.records FROM grantor" |> snd with
              | Err(1147, _) -> ()
              | other -> failtestf "expected absent column revoke refusal, got %A" other

              let root, _ = handle root "GRANT SELECT(hidden), UPDATE(id) ON column_admin.records TO grantor"
              let root, _ = handle root "REVOKE SELECT(id) ON column_admin.records FROM grantor"

              match
                  handle
                      root
                      "SELECT Column_name, Column_priv FROM mysql.columns_priv WHERE User = 'grantor' ORDER BY Column_name"
                  |> snd
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "hidden"; Some "Select" ]; [ Some "id"; Some "Update" ] ]
                      "specific revoke preserves other column grants"
              | other -> failtestf "expected remaining column grants, got %A" other

              let root, _ = handle root "REVOKE SELECT ON column_admin.records FROM grantor"

              match handle root "SELECT Column_name, Column_priv FROM mysql.columns_priv WHERE User = 'grantor'" |> snd with
              | ResultSet(_, [ [ Some "id"; Some "Update" ] ]) -> ()
              | other -> failtestf "expected table SELECT revoke to remove every column SELECT, got %A" other

              let root, _ = handle root "REVOKE ALL PRIVILEGES ON column_admin.records FROM grantor"

              match handle root "SELECT * FROM mysql.columns_priv WHERE User = 'grantor'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected REVOKE ALL to remove column grants, got %A" other

              match handle root "SELECT * FROM mysql.tables_priv WHERE User = 'grantor'" |> snd with
              | ResultSet(_, [ row ]) -> Expect.equal row.[6] (Some "Grant") "grant option remains"
              | other -> failtestf "expected the grant-option row to remain, got %A" other

              match handle root "SHOW GRANTS FOR grantor" |> snd with
              | ResultSet(_, rows) ->
                  Expect.contains
                      (rows |> List.map (List.head >> Option.get))
                      "GRANT USAGE ON `column_admin`.`records` TO `grantor`@`%` WITH GRANT OPTION"
                      "grant-option-only line"
              | other -> failtestf "expected grant-option-only SHOW GRANTS line, got %A" other

              let root, _ = handle root "REVOKE GRANT OPTION ON column_admin.records FROM grantor"

              match handle root "SELECT * FROM mysql.tables_priv WHERE User = 'grantor'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected the empty table summary to be removed, got %A" other

          testCase "active roles contribute column privileges"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE role_columns"
              let root, _ = handle root "CREATE TABLE role_columns.items (visible INT, hidden INT)"
              let root, _ = handle root "INSERT INTO role_columns.items VALUES (1, 2)"
              let root, _ = handle root "CREATE USER role_user"
              let root, _ = handle root "CREATE ROLE field_reader"
              let root, _ = handle root "GRANT SELECT(hidden) ON role_columns.items TO field_reader"
              let _, _ = handle root "GRANT field_reader TO role_user"

              let user =
                  { create 2 store with
                      User = "role_user"
                      Database = Some "role_columns" }

              match handle user "SELECT hidden FROM items" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected inactive role denial, got %A" other

              let user, activated = handle user "SET ROLE field_reader"
              Expect.equal activated (Affected 0UL) "role activates"

              match handle user "SELECT hidden FROM items" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected role column grant, got %A" other

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
              let root = create 1 store

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

              for table, expectedColumns in
                  [ "component", [ "component_id"; "component_group_id"; "component_urn" ]
                    "func", [ "name"; "ret"; "dl"; "type" ]
                    "password_history", [ "Host"; "User"; "Password_timestamp"; "Password" ]
                    "plugin", [ "name"; "dl" ]
                    "proxies_priv",
                    [ "Host"; "User"; "Proxied_host"; "Proxied_user"; "With_grant"; "Grantor"; "Timestamp" ]
                    "servers",
                    [ "Server_name"; "Host"; "Db"; "Username"; "Password"; "Port"; "Socket"; "Wrapper"; "Owner" ]
                    "time_zone", [ "Time_zone_id"; "Use_leap_seconds" ]
                    "time_zone_leap_second", [ "Transition_time"; "Correction" ]
                    "time_zone_name", [ "Name"; "Time_zone_id" ]
                    "time_zone_transition", [ "Time_zone_id"; "Transition_time"; "Transition_type_id" ]
                    "time_zone_transition_type",
                    [ "Time_zone_id"; "Transition_type_id"; "Offset"; "Is_DST"; "Abbreviation" ] ] do
                  match handle root (sprintf "SELECT * FROM mysql.%s" table) |> snd with
                  | ResultSet(columns, []) -> Expect.sequenceEqual columns expectedColumns (table + " columns")
                  | other -> failtestf "expected empty mysql.%s, got %A" table other

              let root, _ = handle root "CREATE USER attribute_reader"

              match handle root "SELECT USER, HOST, ATTRIBUTE FROM information_schema.USER_ATTRIBUTES ORDER BY USER" |> snd with
              | ResultSet(
                  _,
                  [ [ Some "attribute_reader"; Some "%"; None ]
                    [ Some "root"; Some "%"; None ] ]
                ) ->
                  ()
              | other -> failtestf "expected root to see account attributes, got %A" other

              let reader = { create 2 store with User = "attribute_reader" }

              match handle reader "SELECT USER, HOST, ATTRIBUTE FROM information_schema.USER_ATTRIBUTES" |> snd with
              | ResultSet(_, [ [ Some "attribute_reader"; Some "%"; None ] ]) -> ()
              | other -> failtestf "expected account attributes to be viewer-scoped, got %A" other

        ]
