module Fsdb.Tests.PersistenceTests

open System
open System.IO
open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Persistence

/// A fresh, empty scratch directory under the OS temp dir — one per test, so
/// tests never trip over each other's `wal.jsonl`/`snapshot.fsdb`.
let private tempDataDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fsdb-persistence-tests", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

let private usersColumns =
    [ { Name = "id"
        Type = TInt false
        Nullable = false
        Default = None
        AutoIncrement = true
        PrimaryKey = true
        Unique = false
        Generated = None }
      { Name = "name"
        Type = TVarchar 255
        Nullable = false
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None }
      { Name = "note"
        Type = TText
        Nullable = true
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None } ]

let private walPath dir = Path.Combine(dir, "wal.jsonl")
let private snapshotPath dir = Path.Combine(dir, "snapshot.fsdb")

let private rowsOf (store: Store) (dbName: string) (table: string) : Value[] list =
    match scan store dbName table with
    | Ok(_, rows) -> List.ofSeq rows
    | Error e -> failtestf "scan failed: %A" e

let tests =
    testList
        "persistence"
        [ testCase "attach + reload round-trips one value of every Value case, including datetime fractional seconds"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "vals" usersColumns [] [] |> ignore

              insertRows store defaultDatabase "vals" None [ [ VNull; VString "row1"; VNull ] ] |> ignore

              insertRows
                  store
                  defaultDatabase
                  "vals"
                  None
                  [ [ VInt 42L; VString "unicode héllo 🎉"; VJson """{"a":1}""" ] ]
              |> ignore

              let reloaded = load dir
              let rows = rowsOf reloaded defaultDatabase "vals"
              Expect.equal (List.length rows) 2 "both inserted rows survive"
              Expect.contains rows [| VInt 1L; VString "row1"; VNull |] "NULL round-trips"
              // `note` is a `TText` column — `Storage.coerceValue` coerces
              // any value written to it down to `VString` at insert time
              // (true for `VJson` too), so that's the physical value the WAL
              // actually carries and replay should reproduce.
              Expect.contains rows [| VInt 42L; VString "unicode héllo 🎉"; VString """{"a":1}""" |] "unicode/JSON-text round-trip"

          testCase "WAL replay reproduces NOW()/UUID()-generated values identically, not re-evaluated"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let eventsColumns =
                  [ { Name = "id"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = true
                      PrimaryKey = true
                      Unique = false
                      Generated = None }
                    { Name = "created_at"
                      Type = TDateTime
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None }
                    { Name = "token"
                      Type = TVarchar 64
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None } ]

              createTable store defaultDatabase "events" eventsColumns [] [] |> ignore

              // Stand-ins for what `Executor` would have already evaluated
              // NOW()/UUID() into before calling `insertRows` — the WAL logs
              // physical values, never the expression, so replaying must
              // reproduce this exact instant/guid rather than a fresh one.
              let now = DateTime(2026, 8, 14, 3, 4, 5, 678, DateTimeKind.Unspecified)
              let uuid = Guid.NewGuid().ToString()

              match insertRows store defaultDatabase "events" None [ [ VNull; VDateTime now; VString uuid ] ] with
              | Ok _ -> ()
              | Error e -> failtestf "setup insert failed: %A" e

              let reloaded = load dir
              let rows = rowsOf reloaded defaultDatabase "events"
              Expect.equal rows [ [| VInt 1L; VDateTime now; VString uuid |] ] "replayed row is byte-for-byte the original, not re-evaluated"

          testCase "snapshot + WAL tail: rows before the snapshot and rows after it both survive a reload"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "before-snapshot"; VNull ] ] |> ignore

              snapshotNow dir store
              Expect.isTrue (File.Exists(snapshotPath dir)) "snapshot file written"
              Expect.equal (FileInfo(walPath dir).Length) 0L "WAL truncated after snapshot"

              insertRows store defaultDatabase "t" None [ [ VNull; VString "after-snapshot"; VNull ] ] |> ignore

              let reloaded = load dir
              let names = rowsOf reloaded defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.containsAll names [ VString "before-snapshot"; VString "after-snapshot" ] "both the snapshotted row and the WAL-tail row are back"

          testCase "DDL replay reproduces schema: create, alter (add column + index), rename"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "widgets" usersColumns [] [] |> ignore

              let extraCol =
                  { Name = "sku"
                    Type = TVarchar 64
                    Nullable = true
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    Generated = None }

              alterTable
                  store
                  defaultDatabase
                  "widgets"
                  [ AddColumn(extraCol, PositionDefault); AddIndex { Name = "ix_sku"; Columns = [ "sku" ]; Unique = false } ]
              |> ignore

              renameTable store defaultDatabase "widgets" "gadgets" |> ignore
              insertRows store defaultDatabase "gadgets" None [ [ VNull; VString "n"; VNull; VString "sku-1" ] ] |> ignore

              let reloaded = load dir

              match scan reloaded defaultDatabase "gadgets" with
              | Ok(columns, rows) ->
                  Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name"; "note"; "sku" ] "columns, including the ALTERed one, survive"
                  Expect.equal (List.ofSeq rows) [ [| VInt 1L; VString "n"; VNull; VString "sku-1" |] ] "row inserted under the renamed table survives"
              | Error e -> failtestf "expected the renamed table to scan, got %A" e

              match scan reloaded defaultDatabase "widgets" with
              | Error(NoSuchTable _) -> ()
              | other -> failtestf "expected the pre-rename name to be gone, got %A" other

          testCase "a torn/corrupt final WAL line stops replay there instead of crashing"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "good"; VNull ] ] |> ignore

              // Simulate a `kill -9` mid-`Write`: a half-written trailing
              // line with no closing brace.
              File.AppendAllText(walPath dir, "{\"case\":\"RowsInserted\",\"db\":\"fsdb\",\"table\":\"t\",\"rows\":[[\"I2\"")

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "t") [ [| VInt 1L; VString "good"; VNull |] ] "only the row before the torn line survives, no crash"

          testCase "a rolled-back transaction leaves no WAL entries"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] |> ignore

              let beforeRollback = File.ReadAllLines(walPath dir) |> Array.length

              let snapshot = beginTransactionSnapshot store
              insertRows snapshot defaultDatabase "t" None [ [ VNull; VString "never-committed"; VNull ] ] |> ignore
              // ROLLBACK: just discard `snapshot` — never call `commitTransactionEvents`.

              let afterRollback = File.ReadAllLines(walPath dir) |> Array.length
              Expect.equal afterRollback beforeRollback "rollback wrote nothing to the WAL"

              let reloaded = load dir
              Expect.isEmpty (rowsOf reloaded defaultDatabase "t") "the rolled-back row never made it in"

          testCase "a torn WAL line doesn't poison future appends: writes after a kill -9 restart still survive a second restart"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "good"; VNull ] ] |> ignore
              // Simulate a `kill -9` mid-`Write`: a half-written trailing line.
              File.AppendAllText(walPath dir, "{\"case\":\"RowsInserted\",\"db\":\"fsdb\",\"table\":\"t\",\"rows\":[[\"I2\"")

              // Restart #1: replay stops at the torn line, but must also
              // truncate it away so the next append starts clean.
              let restarted = load dir
              attach dir restarted
              Expect.equal (rowsOf restarted defaultDatabase "t") [ [| VInt 1L; VString "good"; VNull |] ] "torn line dropped, good row survives"

              insertRows restarted defaultDatabase "t" None [ [ VNull; VString "second"; VNull ] ] |> ignore
              insertRows restarted defaultDatabase "t" None [ [ VNull; VString "third"; VNull ] ] |> ignore

              // Restart #2: both post-restart writes must be intact — before
              // the fix, they'd glue onto the torn bytes and be lost here.
              let restarted2 = load dir
              let names = rowsOf restarted2 defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.containsAll names [ VString "good"; VString "second"; VString "third" ] "every row acked after the torn-line restart survives a second restart"

          testCase "WAL replay of a sequential UPDATE (n = n + 1) doesn't cascade through its own earlier changes"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              createTable
                  store
                  defaultDatabase
                  "seq"
                  [ { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None } ]
                  []
                  []
              |> ignore

              insertRows store defaultDatabase "seq" None [ [ VInt 1L ]; [ VInt 2L ]; [ VInt 3L ] ] |> ignore
              updateRows store defaultDatabase "seq" (fun _ -> Ok true) (fun row -> Ok [| VInt((row.[0] |> function VInt i -> i | _ -> 0L) + 1L) |])
              |> ignore

              let reloaded = load dir
              let values = rowsOf reloaded defaultDatabase "seq" |> List.map (fun r -> r.[0]) |> List.sortBy (function VInt i -> i | _ -> 0L)
              Expect.equal values [ VInt 2L; VInt 3L; VInt 4L ] "each row incremented exactly once on replay, not cascaded"

          testCase "WAL replay of a duplicate-row DELETE LIMIT 1 removes exactly one physical row, not every value-equal twin"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              createTable
                  store
                  defaultDatabase
                  "dups"
                  [ { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None } ]
                  []
                  []
              |> ignore

              insertRows store defaultDatabase "dups" None [ [ VInt 7L ]; [ VInt 7L ]; [ VInt 7L ] ] |> ignore
              // Delete exactly one row (as `DELETE ... WHERE n = 7 LIMIT 1` would).
              let mutable deleted = false

              deleteRows store defaultDatabase "dups" (fun row ->
                  if not deleted && row = [| VInt 7L |] then
                      deleted <- true
                      Ok true
                  else
                      Ok false)
              |> ignore

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "dups" |> List.length) 2 "only the one deleted row is gone after replay"

          testCase "WAL replay doesn't re-validate foreign keys — a row written under SET FOREIGN_KEY_CHECKS=0 survives"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let idCol name =
                  { Name = name
                    Type = TInt false
                    Nullable = false
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = true
                    Unique = false
                    Generated = None }

              createTable store defaultDatabase "p" [ idCol "id" ] [] [] |> ignore

              createTable
                  store
                  defaultDatabase
                  "c"
                  [ idCol "id"; { (idCol "pid") with PrimaryKey = false } ]
                  []
                  [ { Name = "fkc"; Columns = [ "pid" ]; RefTable = "p"; RefColumns = [ "id" ]; OnDelete = None; OnUpdate = None } ]
              |> ignore

              insertRows store defaultDatabase "p" None [ [ VInt 1L ] ] |> ignore
              insertRows store defaultDatabase "c" None [ [ VInt 1L; VInt 1L ] ] |> ignore

              setForeignKeyChecks store false
              insertRows store defaultDatabase "c" None [ [ VInt 2L; VInt 999L ] ] |> ignore
              setForeignKeyChecks store true

              let reloaded = load dir
              let ids = rowsOf reloaded defaultDatabase "c" |> List.map (fun r -> r.[0])
              Expect.containsAll ids [ VInt 1L; VInt 2L ] "the FK-checks-disabled orphan row survives replay"

          testCase "a GENERATED column fails loudly at CREATE TABLE time under --data-dir instead of silently degrading"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let genCol =
                  { Name = "b"
                    Type = TInt false
                    Nullable = true
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    Generated = Some(BinOp(Mul, Col "a", Lit(VInt 2L))) }

              let createFails () =
                  createTable
                      store
                      defaultDatabase
                      "g"
                      [ { Name = "a"
                          Type = TInt false
                          Nullable = true
                          Default = None
                          AutoIncrement = false
                          PrimaryKey = false
                          Unique = false
                          Generated = None }
                        genCol ]
                      []
                      []
                  |> ignore

              Expect.throws createFails "encoding a GENERATED column's DDL into the WAL raises instead of quietly dropping the expression"

          testCase "a crash between the fsynced .new snapshot and the WAL truncation still recovers the full catalog, no duplicates"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "a"; VNull ]; [ VNull; VString "b"; VNull ] ] |> ignore

              // Manually reproduce the on-disk state right after `snapshotNow`
              // fsyncs `.new` but before it renames it into place — the WAL is
              // *not* yet truncated in this window (truncation happens before
              // the rename), so both the fsynced `.new` and a full WAL are on
              // disk at once.
              snapshotNow dir store
              let snap = File.ReadAllText(snapshotPath dir)
              File.WriteAllText(snapshotPath dir + ".new", snap)
              File.Delete(snapshotPath dir)

              let reloaded = load dir
              let names = rowsOf reloaded defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.equal (List.length names) 2 "the .new snapshot is trusted as-is, not merged with an (already-truncated, in the real path) WAL"
              Expect.containsAll names [ VString "a"; VString "b" ] "no data lost"
              Expect.isFalse (File.Exists(snapshotPath dir + ".new")) ".new is renamed into place after a successful load" ]
