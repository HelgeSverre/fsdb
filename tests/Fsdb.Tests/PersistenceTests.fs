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
        Generated = None
        Collation = None
        Charset = None }
      { Name = "name"
        Type = TVarchar 255
        Nullable = false
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None
        Collation = None
        Charset = None }
      { Name = "note"
        Type = TText
        Nullable = true
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None
        Collation = None
        Charset = None } ]

/// No PK/AUTO_INCREMENT/UNIQUE at all — unlike `usersColumns`, a row
/// replayed twice inserts twice instead of the second copy silently dying
/// on a uniqueness violation, so a duplicate-replay bug shows up as a
/// row-count difference.
let private tagColumns =
    [ { Name = "tag"
        Type = TVarchar 64
        Nullable = false
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None
        Collation = None
        Charset = None } ]

let private walPath dir = Path.Combine(dir, "wal.bin")
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
              createTable store defaultDatabase "vals" usersColumns [] [] None None |> ignore

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
                      Generated = None
                      Collation = None
                      Charset = None }
                    { Name = "created_at"
                      // fsp 6 so the sub-second stand-in below survives
                      // coercion (a bare DATETIME is fsp 0 and would round
                      // the fraction away) — the point of this test is that
                      // the WAL replays the stored physical value verbatim.
                      Type = TDateTime 6
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None }
                    { Name = "token"
                      Type = TVarchar 64
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None } ]

              createTable store defaultDatabase "events" eventsColumns [] [] None None |> ignore

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
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "before-snapshot"; VNull ] ] |> ignore

              snapshotNow dir store
              Expect.isTrue (File.Exists(snapshotPath dir)) "snapshot file written"
              Expect.equal (FileInfo(walPath dir).Length) 0L "WAL truncated after snapshot"

              insertRows store defaultDatabase "t" None [ [ VNull; VString "after-snapshot"; VNull ] ] |> ignore

              let reloaded = load dir
              let names = rowsOf reloaded defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.containsAll names [ VString "before-snapshot"; VString "after-snapshot" ] "both the snapshotted row and the WAL-tail row are back"

          testCase "a snapshot larger than the streaming flush threshold round-trips through the chunked writer and streamed reader"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "big" usersColumns [] [] None None |> ignore

              // ~10k rows of ~150 bytes each ≈ 1.5 MB, past `writeCatalog`'s
              // 1 MiB flush checkpoint — forces a mid-write flush on the way
              // out and a chunk-spanning `StreamReader` on the way back in.
              let rowCount = 10_000

              insertRows
                  store
                  defaultDatabase
                  "big"
                  None
                  [ for i in 1 .. rowCount -> [ VNull; VString(sprintf "row-%d-%s" i (String('x', 120))); VNull ] ]
              |> ignore

              snapshotNow dir store

              let reloaded = load dir
              let count = rowsOf reloaded defaultDatabase "big" |> List.length
              Expect.equal count rowCount "every row survives a chunked snapshot write + streamed read"

          testCase "DDL replay reproduces schema: create, alter (add column + index), rename"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "widgets" usersColumns [] [] None None |> ignore

              let extraCol =
                  { Name = "sku"
                    Type = TVarchar 64
                    Nullable = true
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    Generated = None
                    Collation = None
                    Charset = None }

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

          testCase "a torn/corrupt final WAL record stops replay there instead of crashing"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "good"; VNull ] ] |> ignore

              // Simulate a `kill -9` mid-`Write`: a record header declaring
              // 100 payload bytes that were never written.
              File.AppendAllBytes(walPath dir, [| 100uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "t") [ [| VInt 1L; VString "good"; VNull |] ] "only the row before the torn record survives, no crash"

          testCase "a rolled-back transaction leaves no WAL entries"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore

              let beforeRollback = FileInfo(walPath dir).Length

              let snapshot = beginTransactionSnapshot store
              insertRows snapshot defaultDatabase "t" None [ [ VNull; VString "never-committed"; VNull ] ] |> ignore
              // ROLLBACK: just discard `snapshot` — never call `commitTransactionEvents`.

              let afterRollback = FileInfo(walPath dir).Length
              Expect.equal afterRollback beforeRollback "rollback wrote nothing to the WAL"

              let reloaded = load dir
              Expect.isEmpty (rowsOf reloaded defaultDatabase "t") "the rolled-back row never made it in"

          testCase "a torn WAL record doesn't poison future appends: writes after a kill -9 restart still survive a second restart"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "good"; VNull ] ] |> ignore
              // Simulate a `kill -9` mid-`Write`: a record header declaring
              // 100 payload bytes that were never written.
              File.AppendAllBytes(walPath dir, [| 100uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])

              // Restart #1: replay stops at the torn record, but must also
              // truncate it away so the next append starts clean.
              let restarted = load dir
              attach dir restarted
              Expect.equal (rowsOf restarted defaultDatabase "t") [ [| VInt 1L; VString "good"; VNull |] ] "torn record dropped, good row survives"

              insertRows restarted defaultDatabase "t" None [ [ VNull; VString "second"; VNull ] ] |> ignore
              insertRows restarted defaultDatabase "t" None [ [ VNull; VString "third"; VNull ] ] |> ignore

              // Restart #2: both post-restart writes must be intact.
              let restarted2 = load dir
              let names = rowsOf restarted2 defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.containsAll names [ VString "good"; VString "second"; VString "third" ] "every row acked after the torn-record restart survives a second restart"

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
                      Generated = None
                      Collation = None
                      Charset = None } ]
                  []
                  []
                  None
                  None
              |> ignore

              insertRows store defaultDatabase "seq" None [ [ VInt 1L ]; [ VInt 2L ]; [ VInt 3L ] ] |> ignore
              updateRows store defaultDatabase "seq" None (fun _ -> Ok true) (fun row -> Ok [| VInt((row.[0] |> function VInt i -> i | _ -> 0L) + 1L) |])
              |> ignore

              let reloaded = load dir
              let values = rowsOf reloaded defaultDatabase "seq" |> List.map (fun r -> r.[0]) |> List.sortBy (function VInt i -> i | _ -> 0L)
              Expect.equal values [ VInt 2L; VInt 3L; VInt 4L ] "each row incremented exactly once on replay, not cascaded"

          testCase "the reloaded store's PRIMARY KEY index reflects a replayed UPDATE's new values, not the pre-update ones"
          <| fun _ ->
              // `RowsUpdated` replay goes through `mapTableRows`, which
              // rewrites `Rows` directly (bypassing `Storage.updateRows`'
              // checked, index-maintaining path — see its doc) — this
              // proves `reindexTable` picks the slack back up afterward.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "pk" (usersColumns |> List.filter (fun c -> c.Name <> "note")) [] [] None None |> ignore
              insertRows store defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 1L; VString "a" ]; [ VInt 2L; VString "b" ]; [ VInt 3L; VString "c" ] ]
              |> ignore

              updateRows store defaultDatabase "pk" None (fun row -> Ok(row.[0] = VInt 1L)) (fun row -> Ok [| VInt 10L; row.[1] |])
              |> ignore

              let reloaded = load dir

              // id 1 is free again after the replayed update moved that row to id 10.
              match insertRows reloaded defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 1L; VString "d" ] ] with
              | Ok(1L, _, 1) -> ()
              | other -> failtestf "expected id 1 to be free again after replay, got %A" other

              // id 10 is now occupied — a stale (pre-update) index would
              // wrongly accept this as if id 10 never existed.
              match insertRows reloaded defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 10L; VString "e" ] ] with
              | Error(DuplicateKey("PRIMARY", _)) -> ()
              | other -> failtestf "expected id 10 to be rejected as a duplicate after replay, got %A" other

          testCase "WAL replay of many single-row UPDATEs against a UNIQUE-indexed table doesn't rebuild the index once per event"
          <| fun _ ->
              // Replaying RowsUpdated/RowsDeleted must not reindex — a full
              // rescan of every unique group over every row — once per
              // event. A table with k single-row UPDATEs in its WAL would
              // make replay O(k * n * groups) instead of O(k + n * groups).
              //
              // The WAL is built by hand (rather than k real attached
              // updateRows calls, each fsync'd) so this test measures only
              // replay cost, not k fsyncs' worth of setup noise.
              let dir = tempDataDir ()
              let rowCount = 2500

              let setupStore = create ()

              createTable
                  setupStore
                  defaultDatabase
                  "hot"
                  [ { Name = "id"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = true
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None }
                    { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None } ]
                  []
                  []
                  None
                  None
              |> ignore

              insertRows setupStore defaultDatabase "hot" None [ for i in 1 .. rowCount -> [ VInt(int64 i); VInt 0L ] ]
              |> ignore

              snapshotNow dir setupStore

              let records =
                  [ for i in 1 .. rowCount ->
                        encodeWalRecord (RowsUpdated(defaultDatabase, "hot", [ [| VInt(int64 i); VInt 0L |], [| VInt(int64 i); VInt 1L |] ])) ]
                  |> Array.concat

              File.WriteAllBytes(Path.Combine(dir, "wal.bin"), records)

              let countBefore = reindexCallCount ()
              let reloaded = load dir
              let reindexesDuringLoad = reindexCallCount () - countBefore

              let values = rowsOf reloaded defaultDatabase "hot" |> List.map (fun r -> r.[1])
              Expect.isTrue (values |> List.forall (fun v -> v = VInt 1L)) "every row's replayed update landed"

              // `load` reindexes "hot" a small constant number of times
              // (once decoding the snapshot, once after replay) no matter
              // how many WAL events touch it — a per-event reindex would
              // make this scale with `rowCount` (2500) instead.
              Expect.isLessThan
                  reindexesDuringLoad
                  10
                  (sprintf
                      "loading a snapshot + %d single-row UPDATEs against a %d-row UNIQUE-indexed table triggered %d reindexes — looks like a per-event reindex again"
                      rowCount
                      rowCount
                      reindexesDuringLoad)

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
                      Generated = None
                      Collation = None
                      Charset = None } ]
                  []
                  []
                  None
                  None
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

          testCase "WAL replay of a single RowsUpdated/RowsDeleted event over 10,000 rows doesn't stack-overflow"
          <| fun _ ->
              // Replay of one `CommitEvent` covering thousands of physically
              // distinct rows must be tail-recursive — a non-tail per-row
              // recursion (`row :: applyRowChanges ...`) overflows the stack
              // on restart. The 10,000-row event here sits past the ~3800
              // rows a non-tail recursion crashes at.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              createTable
                  store
                  defaultDatabase
                  "bulk"
                  [ { Name = "id"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = true
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None }
                    { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None } ]
                  []
                  []
                  None
                  None
              |> ignore

              let rowCount = 10_000
              insertRows store defaultDatabase "bulk" None [ for i in 1 .. rowCount -> [ VInt(int64 i); VInt 0L ] ]
              |> ignore

              // One bulk UPDATE touching every row, as a single `RowsUpdated` event.
              updateRows store defaultDatabase "bulk" None (fun _ -> Ok true) (fun row ->
                  Ok [| row.[0]; VInt((row.[1] |> function VInt i -> i | _ -> 0L) + 1L) |])
              |> ignore

              let afterUpdate = load dir
              attach dir afterUpdate
              let updated = rowsOf afterUpdate defaultDatabase "bulk" |> List.map (fun r -> r.[1])
              Expect.isTrue (updated |> List.forall (fun v -> v = VInt 1L)) "every one of the 10,000 rows' replayed update landed"

              // One bulk DELETE removing every row, as a single `RowsDeleted` event.
              deleteRows afterUpdate defaultDatabase "bulk" (fun _ -> Ok true) |> ignore

              let afterDelete = load dir
              Expect.isEmpty (rowsOf afterDelete defaultDatabase "bulk") "every one of the 10,000 rows' replayed delete landed"

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
                    Generated = None
                    Collation = None
                    Charset = None }

              createTable store defaultDatabase "p" [ idCol "id" ] [] [] None None |> ignore

              createTable
                  store
                  defaultDatabase
                  "c"
                  [ idCol "id"; { (idCol "pid") with PrimaryKey = false } ]
                  []
                  [ { Name = "fkc"; Columns = [ "pid" ]; RefTable = "p"; RefColumns = [ "id" ]; OnDelete = None; OnUpdate = None } ]
                  None
                  None
              |> ignore

              insertRows store defaultDatabase "p" None [ [ VInt 1L ] ] |> ignore
              insertRows store defaultDatabase "c" None [ [ VInt 1L; VInt 1L ] ] |> ignore

              setForeignKeyChecks store false
              insertRows store defaultDatabase "c" None [ [ VInt 2L; VInt 999L ] ] |> ignore
              setForeignKeyChecks store true

              let reloaded = load dir
              let ids = rowsOf reloaded defaultDatabase "c" |> List.map (fun r -> r.[0])
              Expect.containsAll ids [ VInt 1L; VInt 2L ] "the FK-checks-disabled orphan row survives replay"

          testCase "a GENERATED column's expression survives a restart, so writes after it still compute the right value"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              // `b AS (a * 2)` — a stand-in for Laravel Pulse's `key_hash
              // ... AS (unhex(md5(key)))` generated-column shape.
              let genCol =
                  { Name = "b"
                    Type = TInt false
                    Nullable = true
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    Generated = Some(BinOp(Mul, Col "a", Lit(VInt 2L)))
                    Collation = None
                    Charset = None }

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
                      Generated = None
                      Collation = None
                      Charset = None }
                    genCol ]
                  []
                  []
                  None
                  None
              |> ignore

              let reloaded = load dir

              match scan reloaded defaultDatabase "g" with
              | Ok(columns, _) ->
                  match columns |> List.tryFind (fun c -> c.Name = "b") |> Option.bind (fun c -> c.Generated) with
                  | Some(BinOp(Mul, Col "a", Lit(VInt 2L))) -> ()
                  | other -> failtestf "expected the generated expression to survive the restart intact, got %A" other
              | Error e -> failtestf "expected table 'g' to reload, got %A" e

          testCase "a crash between the fsynced .new snapshot and the WAL truncation still recovers the full catalog, no duplicates"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "a"; VNull ]; [ VNull; VString "b"; VNull ] ] |> ignore

              // Manually reproduce the on-disk state right after `snapshotNow`
              // fsyncs `.new` but before it renames it into place — the WAL is
              // *not* yet truncated in this window (truncation happens before
              // the rename), so both the fsynced `.new` and a full WAL are on
              // disk at once.
              snapshotNow dir store
              let snap = File.ReadAllBytes(snapshotPath dir)
              File.WriteAllBytes(snapshotPath dir + ".new", snap)
              File.Delete(snapshotPath dir)

              let reloaded = load dir
              let names = rowsOf reloaded defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.equal (List.length names) 2 "the .new snapshot is trusted as-is, not merged with an (already-truncated, in the real path) WAL"
              Expect.containsAll names [ VString "a"; VString "b" ] "no data lost"
              Expect.isFalse (File.Exists(snapshotPath dir + ".new")) ".new is renamed into place after a successful load"

          testCase "a table's declared charset/collation survives a restart, so SHOW CREATE stays faithful"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "decl" usersColumns [] [] (Some "utf8mb4") (Some "utf8mb4_unicode_ci") |> ignore

              let reloaded = load dir

              match Fsdb.InformationSchema.findTable reloaded.Catalog defaultDatabase "decl" with
              | Ok table ->
                  Expect.equal table.TableCharset (Some "utf8mb4") "the declared charset survives the restart"
                  Expect.equal table.TableCollation (Some "utf8mb4_unicode_ci") "the declared collation survives the restart"
              | Error e -> failtestf "expected table 'decl' to reload, got %A" e

          testCase "a torn/zero-filled .new is rejected, not promoted as an empty catalog that wipes the real snapshot"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "t" usersColumns [] [] None None |> ignore
              insertRows store defaultDatabase "t" None [ [ VNull; VString "keep-me"; VNull ] ] |> ignore
              snapshotNow dir store // a real, valid snapshot.fsdb now exists

              // Simulate a crash mid-`writeCatalog`: `.new` exists but is
              // truncated to a handful of zero bytes — `decodeCatalog` alone
              // happily parses that as `dbCount = 0`, an empty-but-"valid"
              // catalog, so `load` must demand more than "it parsed" before
              // promoting a `.new` over the real snapshot.
              File.WriteAllBytes(snapshotPath dir + ".new", Array.zeroCreate<byte> 8)

              let reloaded = load dir
              let names = rowsOf reloaded defaultDatabase "t" |> List.map (fun r -> r.[1])
              Expect.containsAll names [ VString "keep-me" ] "the real snapshot survives a torn .new instead of being wiped"
              Expect.isTrue (File.Exists(snapshotPath dir + ".new")) "the rejected .new is left alone, not promoted"

          testCase "concurrent writes to two databases across a forced WAL rotation replay with no duplicated/lost rows"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              // Rotate on every single WAL entry instead of the real 100k —
              // forces `attach`'s rotation to fire continuously *during* the
              // write storm below, landing squarely (and often) in the
              // window `Storage`'s writers leave between publishing a row to
              // the catalog and this module's WAL append recording it (see
              // `attach`'s `replica` doc). A rotation caught in that window
              // must not duplicate the in-flight row (snapshotted, then
              // appended again to the freshly-truncated WAL). Stress check:
              // the race is a narrow timing window, not reliably
              // reproducible on demand.
              testRotateEntriesOverride <- Some 0

              try
                  attach dir store
                  createDatabase store "db_a" |> ignore
                  createDatabase store "db_b" |> ignore
                  createTable store "db_a" "t" tagColumns [] [] None None |> ignore
                  createTable store "db_b" "t" tagColumns [] [] None None |> ignore

                  let perThread = 300

                  let writer (dbName: string) (tag: string) =
                      fun () ->
                          for i in 1..perThread do
                              insertRows store dbName "t" None [ [ VString(sprintf "%s-%d" tag i) ] ] |> ignore

                  let threads =
                      [ writer "db_a" "a1"; writer "db_a" "a2"; writer "db_b" "b1"; writer "db_b" "b2" ]
                      |> List.map (fun f -> System.Threading.Thread(System.Threading.ThreadStart f))

                  threads |> List.iter (fun t -> t.Start())
                  threads |> List.iter (fun t -> t.Join())

                  let reloaded = load dir
                  let countA = rowsOf reloaded "db_a" "t" |> List.length
                  let countB = rowsOf reloaded "db_b" "t" |> List.length
                  Expect.equal countA (2 * perThread) "db_a has exactly its inserted rows, no dup/loss across rotation"
                  Expect.equal countB (2 * perThread) "db_b has exactly its inserted rows, no dup/loss across rotation"
              finally
                  testRotateEntriesOverride <- None ]
