module Fsdb.Tests.QueryHandlerVariableTests

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
        "User variables"
        [ testCase "a bad multi-assignment SET applies none of its assignments, not just the ones before the bad one"
          <| fun _ ->
              // Two-phase: every fragment parses before any of them apply,
              // so a `SET` that fails partway through — the same as real
              // MySQL — can't leave `sql_mode` (or any other variable it
              // named first) half-updated. `bad-name` (a hyphen isn't a
              // valid identifier char) matches neither `setVar` nor
              // `setUserVar`; `@user_var=1` can't serve as the bad fragment
              // because `SET @foo = ...` is a real feature.
              let session = create 1 (Fsdb.Storage.create ())

              let session, result = handle session "SET SESSION sql_mode='ANSI_QUOTES', bad-name=1"

              match result with
              | Err(1193, _) -> ()
              | other -> failtestf "expected a 1193 error, got %A" other

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some mode ] ]) -> Expect.stringContains mode "STRICT_TRANS_TABLES" "sql_mode is unchanged from its default"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET @user_var = 1 defines a user variable, readable back via SELECT @user_var"
          <| fun _ ->
              // Real MySQL never validates a user-defined variable's name —
              // `SET @foo = ...` is always legal, unlike `SET SESSION x =
              // y`. mysqldump leans on this to save/restore settings around
              // a dump (`SET @OLD_SQL_MODE=@@SQL_MODE` ... later `SET
              // SQL_MODE=@OLD_SQL_MODE`).
              let session = create 1 (Fsdb.Storage.create ())
              let session, setResult = handle session "SET @user_var = 1"

              match setResult with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @user_var" |> snd with
              | ResultSet([ "@user_var" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected user_var to read back as 1, got %A" other

          testCase "quoted user-variable names retain MySQL escaping and case-insensitivity in SET and expressions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, setResult =
                  handle
                      session
                      "SET @`has, comma` = 1, @'sp ace' = 2, @\"double-name\" = 3, @`back``tick, name` = 4, @'single''quote' = 5"

              match setResult with
              | Affected 0UL -> ()
              | other -> failtestf "expected quoted assignments to succeed, got %A" other

              match handle session "SELECT @'HAS, COMMA', @`sp ace`, @\"DOUBLE-NAME\", @`BACK``TICK, NAME`, @'SINGLE''QUOTE'" |> snd with
              | ResultSet(
                    [ "@'HAS, COMMA'"; "@`sp ace`"; "@\"DOUBLE-NAME\""; "@`BACK``TICK, NAME`"; "@'SINGLE''QUOTE'" ],
                    [ [ Some "1"; Some "2"; Some "3"; Some "4"; Some "5" ] ]
                ) -> ()
              | other -> failtestf "expected quoted variables to read back, got %A" other

              match handle session "SELECT @'sp ace' := @`HAS, COMMA` + @\"DOUBLE-NAME\"" with
              | updated, ResultSet([ "@'sp ace':=@`HAS, COMMA` + @\"DOUBLE-NAME\"" ], [ [ Some "4" ] ]) ->
                  Expect.equal updated.UserVariables.["sp ace"] (VInt 4L) "assignment uses quoted references"
              | _, other -> failtestf "expected quoted variables in an assignment expression, got %A" other

          testCase "a bare at sign reads as NULL but remains an illegal assignment target"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @, @ + 1" |> snd with
              | ResultSet([ "@"; "@ + 1" ], [ [ None; None ] ]) -> ()
              | other -> failtestf "expected anonymous NULL variable reads, got %A" other

              match handle session "SET @x := @" with
              | updated, Affected 0UL -> Expect.equal updated.UserVariables.["x"] VNull "bare reference assignment"
              | _, other -> failtestf "expected assignment from a bare reference, got %A" other

              match handle session "SET @ := 1" |> snd with
              | Err(3061, _) -> ()
              | other -> failtestf "expected illegal empty assignment target, got %A" other

          testCase "double-quoted user-variable names remain variables under ANSI_QUOTES"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET SESSION sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES')"
              let session, assigned = handle session "SET @\"ansi name\" = 6"

              match assigned with
              | Affected 0UL -> ()
              | other -> failtestf "expected ANSI_QUOTES assignment to succeed, got %A" other

              match handle session "SELECT @\"ANSI NAME\" + 1, @\"ansi name\" := @\"ansi name\" + 1" with
              | updated, ResultSet(_, [ [ Some "7"; Some "7" ] ]) ->
                  Expect.equal updated.UserVariables.["ansi name"] (VInt 7L) "assignment retains the value"
              | _, other -> failtestf "expected double-quoted variables under ANSI_QUOTES, got %A" other

          testCase "user-variable names allow dots and dollars and reject empty or overlong decoded names"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, assigned = handle session "SET @a.b = 1, @a$b = 2"

              match assigned with
              | Affected 0UL -> ()
              | other -> failtestf "expected dotted and dollar names to assign, got %A" other

              match handle session "SELECT @A.B, @A$B" |> snd with
              | ResultSet([ "@A.B"; "@A$B" ], [ [ Some "1"; Some "2" ] ]) -> ()
              | other -> failtestf "expected dotted and dollar names to read back, got %A" other

              let valid = String.replicate 64 "😀"
              let invalid = String.replicate 65 "😀"
              let session, assigned = handle session ("SET @'" + valid + "' = 3")

              match assigned with
              | Affected 0UL -> ()
              | other -> failtestf "expected a 64-rune name to assign, got %A" other

              let session, _ = handle session "CREATE TABLE variable_names (id INT)"

              for sql in
                  [ "SELECT @'" + invalid + "'"
                    "SELECT @'" + invalid + "' LIMIT 0"
                    "SELECT @'" + invalid + "' FROM variable_names"
                    "SELECT @'" + invalid + "' := 1"
                    "SET @" + String.replicate 65 "a" + " = 1"
                    "SELECT @``"
                    "SET @'' = 1"
                    "SELECT @\"\"" ] do
                  match handle session sql with
                  | _, Err(3061, _) -> ()
                  | _, other -> failtestf "expected 3061 for %s, got %A" sql other

          testCase "SELECT @never_set is NULL, not an error — unlike an unknown @@system_var"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @never_set" |> snd with
              | ResultSet([ "@never_set" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected a NULL row, got %A" other

          testCase "user and system variables participate in ordinary expressions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @base = 2"

              match handle session "SELECT @base + 3, @@session.autocommit + 1" |> snd with
              | ResultSet(_, [ [ Some "5"; Some "2" ] ]) -> ()
              | other -> failtestf "expected expression values, got %A" other

          testCase "user-variable assignment evaluates and persists inside a SELECT"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @counter = 0"
              let session, result = handle session "SELECT @counter := @counter + 1"

              match result with
              | ResultSet([ "@counter:=@counter + 1" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected assignment result, got %A" other

              match handle session "SELECT @counter" |> snd with
              | ResultSet([ "@counter" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected persisted user variable, got %A" other

          testCase "user variables retain their assigned SQL values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @literal = 7"
              let session, _ = handle session "SELECT @decimal := 1.5, @payload := JSON_OBJECT('id', 7)"

              Expect.equal session.UserVariables.["literal"] (VInt 7L) "SET preserves integer values"
              Expect.equal session.UserVariables.["decimal"] (VDecimal 1.5m) "expression assignment preserves decimals"
              Expect.equal session.UserVariables.["payload"] (VJson "{\"id\": 7}") "expression assignment preserves JSON"

              match handle session "SELECT @literal + 1, @decimal + 1" with
              | resultSession, ResultSet(_, [ [ Some "8"; Some "2.5" ] ]) ->
                  Expect.equal (resultSession.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLongLong; TypeNewDecimal ] "expression metadata follows the retained values"
              | _, other -> failtestf "expected typed user-variable arithmetic, got %A" other

          testCase "a bare typed user variable retains metadata with LIMIT 0"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @value = 7"

              match handle session "SELECT @value LIMIT 0" with
              | resultSession, ResultSet([ "@value" ], []) ->
                  Expect.equal (resultSession.LastResultColumnMetadata |> List.map _.TypeId) [ TypeTiny ] "metadata follows the assigned value without rows"
              | _, other -> failtestf "expected an empty typed resultset, got %A" other

          testCase "SET evaluates a user-variable arithmetic expression"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "SET @x = 1 + 2"

              match result, session.UserVariables |> Map.tryFind "x" with
              | Affected 0UL, Some(VInt 3L) -> ()
              | other -> failtestf "expected SET to retain the expression result, got %A" other

          testCase "SET keeps nested user-variable assignments and defers its top-level targets"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @a = (@b := 1), @c = @b + 1"

              Expect.equal session.UserVariables.["a"] (VInt 1L) "outer assignment"
              Expect.equal session.UserVariables.["b"] (VInt 1L) "nested assignment"
              Expect.equal session.UserVariables.["c"] (VInt 2L) "later expression sees nested assignment"

              match handle session "SET @d = (@e := 1), missing_variable = 1" with
              | unchanged, Err(1193, _) -> Expect.isFalse (Map.containsKey "e" unchanged.UserVariables) "failed SET applies none of its nested assignments"
              | _, other -> failtestf "expected the multi-assignment to fail atomically, got %A" other

          testCase "user variables work in VALUES and WHERE expressions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "SET @next = 0, @wanted = 2"
              let session, inserted = handle session "INSERT INTO t VALUES (@next := @next + 1), (@next := @next + 1)"

              match inserted with
              | Affected 2UL -> ()
              | other -> failtestf "expected two inserted rows, got %A" other

              match handle session "SELECT id FROM t WHERE id = @wanted" |> snd with
              | ResultSet([ "id" ], [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected the user-variable predicate to select id 2, got %A" other

              let session, _ = handle session "SET @ordinal = 0"

              match handle session "SELECT @ordinal := @ordinal + 1 FROM t ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
              | other -> failtestf "expected one assignment per projected row, got %A" other

          testCase "SET @x = NULL defines a user variable holding NULL, not the string 'NULL'"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @x = NULL"

              match handle session "SELECT @x" |> snd with
              | ResultSet([ "@x" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected a real NULL, not the string 'NULL', got %A" other

          testCase "SET foreign_key_checks = @never_set is a 1231, not a silent empty string"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET foreign_key_checks = @never_set" |> snd with
              | Err(1231, _) -> ()
              | other -> failtestf "expected a 1231 error, got %A" other

          testCase "SET @x = 'NULL' stores the four-character string, not a real NULL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @x = 'NULL'"

              match handle session "SELECT @x" |> snd with
              | ResultSet([ "@x" ], [ [ Some "NULL" ] ]) -> ()
              | other -> failtestf "expected the string 'NULL', not a real NULL, got %A" other

          testCase "SET character_set_results = NULL is accepted, unlike other system variables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, setResult = handle session "SET character_set_results = NULL"

              match setResult with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @@character_set_results" |> snd with
              | ResultSet([ "@@character_set_results" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected @@character_set_results to read back as NULL, got %A" other

          testCase "SET x = @old_var restores a saved system variable, mysqldump's preamble/postamble idiom"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @OLD_SQL_MODE=@@SQL_MODE"
              let session, _ = handle session "SET SESSION sql_mode='ANSI_QUOTES'"
              let session, _ = handle session "SET SQL_MODE=@OLD_SQL_MODE"

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some mode ] ]) -> Expect.stringContains mode "STRICT_TRANS_TABLES" "sql_mode restored to its original value"
              | other -> failtestf "expected a resultset, got %A" other

        ]
