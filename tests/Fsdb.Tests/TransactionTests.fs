module Fsdb.Tests.TransactionTests

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
        "Transactions"
        [ testCase "a write inside BEGIN...COMMIT is invisible to another connection until commit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_t (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_t VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before commit, got %A" result

              let session, _ = handle session "COMMIT"
              ignore session

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the committed row, got %A" result

          testCase "ROLLBACK discards writes made inside the transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_r (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_r VALUES (1)"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_r" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the insert to be rolled back, got %A" result

          testCase "SAVEPOINT / ROLLBACK TO SAVEPOINT undoes only the writes made after it"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_s (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_s VALUES (1)"
              let session, _ = handle session "SAVEPOINT sp1"
              let session, _ = handle session "INSERT INTO tx_s VALUES (2)"
              let session, _ = handle session "ROLLBACK TO SAVEPOINT sp1"
              let session, _ = handle session "COMMIT"

              match handle session "SELECT id FROM tx_s ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the pre-savepoint row, got %A" result

          testCase "SET autocommit = 0 opens an implicit transaction; SET autocommit = 1 commits it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ac (id INT)"
              let session, _ = handle session "SET autocommit = 0"
              let session, _ = handle session "INSERT INTO tx_ac VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before autocommit = 1, got %A" result

              let session, _ = handle session "SET autocommit = 1"
              ignore session

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the row visible once autocommit = 1 commits it, got %A" result

          testCase "RELEASE SAVEPOINT on an unknown savepoint is a 1305 error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"

              match handle session "RELEASE SAVEPOINT nope" |> snd with
              | Err(1305, _) -> ()
              | result -> failtestf "expected a 1305 error, got %A" result

          testCase "COMMIT doesn't discard a concurrent write another connection made to a different table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_m (id INT)"
              let session, _ = handle session "CREATE TABLE tx_other (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_m VALUES (1)"

              let other = create 2 store
              let other, otherResult = handle other "INSERT INTO tx_other VALUES (99)"

              match otherResult with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert into a different table to succeed, got %A" result

              let session, _ = handle session "COMMIT"
              ignore session

              match handle other "SELECT id FROM tx_other" |> snd with
              | ResultSet(_, [ [ Some "99" ] ]) -> ()
              | result -> failtestf "expected the concurrent write to survive the commit, got %A" result

              match handle other "SELECT id FROM tx_m" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the transaction's own write to also be there, got %A" result ]
