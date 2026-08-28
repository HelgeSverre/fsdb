module Fsdb.Tests.PersistenceTests

open System
open System.IO
open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Temporal
open Fsdb.Storage
open Fsdb.Persistence
open Fsdb.Binary
open Fsdb.Executor
open Fsdb.QueryHandler

/// A fresh scratch directory keeps each test's `wal.bin` and `snapshot.fsdb` isolated.
let private tempDataDir () =
    TestSupport.directory "persistence"

let private usersColumns =
    [ { Name = "id"
        Type = TInt false
        Nullable = false
        Default = None
        AutoIncrement = true
        PrimaryKey = true
        Unique = false
        Generated = None
        Comment = ""
        Collation = None
        Charset = None
        OnUpdateCurrentTimestamp = false }
      { Name = "name"
        Type = TVarchar 255
        Nullable = false
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None
        Comment = ""
        Collation = None
        Charset = None
        OnUpdateCurrentTimestamp = false }
      { Name = "note"
        Type = TText
        Nullable = true
        Default = None
        AutoIncrement = false
        PrimaryKey = false
        Unique = false
        Generated = None
        Comment = ""
        Collation = None
        Charset = None
        OnUpdateCurrentTimestamp = false } ]

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
        Comment = ""
        Collation = None
        Charset = None
        OnUpdateCurrentTimestamp = false } ]

let private walPath dir = Path.Combine(dir, "wal.bin")
let private snapshotPath dir = Path.Combine(dir, "snapshot.fsdb")

let private mkCol (name: string) (typ: ColumnType) : ColumnDef =
    { Name = name
      Type = typ
      Nullable = true
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Comment = ""
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

let private writeLegacyColumn (w: Writer) (name: string) =
    w.WriteLenEncString name
    w.WriteByte 0x04uy
    w.WriteByte 0uy
    w.WriteByte 1uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteByte 0uy

let private writeLegacyTable (w: Writer) (name: string) =
    w.WriteLenEncString name
    w.WriteLenEncString name
    w.WriteInt32LE 1
    writeLegacyColumn w "id"
    w.WriteInt32LE 0
    w.WriteInt32LE 0
    w.WriteByte 0uy
    w.WriteByte 0uy
    w.WriteInt64LE 0L
    w.WriteInt64LE 1L
    w.WriteInt32LE 0

let private legacySnapshot (table: string) =
    let payload = Writer()
    payload.WriteInt32LE 1
    payload.WriteLenEncString defaultDatabase
    payload.WriteInt32LE 1
    writeLegacyTable payload table
    let payload = payload.ToArray()
    let snapshot = Writer()
    snapshot.WriteBytes [| 0x46uy; 0x53uy; 0x4Euy; 0x31uy |]
    snapshot.WriteBytes payload
    snapshot.WriteInt64LE(int64 payload.Length)
    snapshot.WriteUInt32LE(crc32 payload)
    snapshot.ToArray()

let private columnCommentSnapshot (table: string) (comment: string) =
    let payload = Writer()
    payload.WriteInt32LE 1
    payload.WriteLenEncString defaultDatabase
    payload.WriteInt32LE 1
    payload.WriteLenEncString table
    payload.WriteLenEncString table
    payload.WriteInt32LE 1
    writeLegacyColumn payload "id"
    payload.WriteLenEncString comment
    payload.WriteInt32LE 0
    payload.WriteInt32LE 0
    payload.WriteByte 0uy
    payload.WriteByte 0uy
    payload.WriteInt64LE 0L
    payload.WriteInt64LE 1L
    payload.WriteInt32LE 0
    let payload = payload.ToArray()
    let snapshot = Writer()
    snapshot.WriteBytes [| 0x46uy; 0x53uy; 0x4Euy; 0x32uy |]
    snapshot.WriteBytes payload
    snapshot.WriteInt64LE(int64 payload.Length)
    snapshot.WriteUInt32LE(crc32 payload)
    snapshot.ToArray()

let private legacyCreateTableWalRecord (table: string) =
    let payload = Writer()
    payload.WriteByte 0x04uy
    payload.WriteLenEncString defaultDatabase
    payload.WriteByte 0x03uy
    payload.WriteLenEncString table
    payload.WriteInt32LE 1
    writeLegacyColumn payload "id"
    payload.WriteInt32LE 0
    payload.WriteInt32LE 0
    payload.WriteByte 0uy
    payload.WriteByte 0uy
    payload.WriteByte 0uy
    payload.WriteByte 0uy
    let payload = payload.ToArray()
    let record = Writer()
    record.WriteInt32LE payload.Length
    record.WriteUInt32LE(crc32 payload)
    record.WriteBytes payload
    record.ToArray()

let private rowsOf (store: Store) (dbName: string) (table: string) : Value[] list =
    match scan store dbName table with
    | Ok(_, rows) -> List.ofSeq rows
    | Error e -> failtestf "scan failed: %A" e

let tests =
    testList
        "persistence"
        [ testList
              "group commit queue"
              [ testCase "writes arriving during a flush share the next batch"
                <| fun _ ->
                    let batches = ResizeArray<int list>()
                    use flushStarted = new Threading.ManualResetEventSlim()
                    use releaseFlush = new Threading.ManualResetEventSlim()

                    let queue =
                        Fsdb.GroupCommit.Queue<int>(
                            8,
                            (fun batch ->
                                batches.Add batch

                                if batches.Count = 1 then
                                    flushStarted.Set()
                                    releaseFlush.Wait()),
                            ignore
                        )

                    let first = queue.Enqueue 1
                    let firstTask = Threading.Tasks.Task.Run first
                    Expect.isTrue (flushStarted.Wait(TimeSpan.FromSeconds 5.)) "the first flush starts"

                    let second = queue.Enqueue 2
                    let third = queue.Enqueue 3
                    let secondTask = Threading.Tasks.Task.Run second
                    let thirdTask = Threading.Tasks.Task.Run third
                    releaseFlush.Set()
                    Threading.Tasks.Task.WaitAll [| firstTask; secondTask; thirdTask |]

                    Expect.sequenceEqual batches [ [ 1 ]; [ 2; 3 ] ] "followers share one ordered flush"

                testCase "checkpoints split adjacent write batches without reordering"
                <| fun _ ->
                    let operations = ResizeArray<string>()

                    let queue =
                        Fsdb.GroupCommit.Queue<int>(
                            8,
                            (fun batch -> operations.Add(sprintf "write:%A" batch)),
                            (fun () -> operations.Add "checkpoint")
                        )

                    let first = queue.Enqueue 1
                    let checkpoint = queue.EnqueueCheckpoint()
                    let second = queue.Enqueue 2
                    first ()
                    checkpoint ()
                    second ()

                    Expect.sequenceEqual operations [ "write:[1]"; "checkpoint"; "write:[2]" ] "barrier order"

                testCase "a flush failure reaches every waiter and closes the queue"
                <| fun _ ->
                    let queue = Fsdb.GroupCommit.Queue<int>(8, (fun _ -> invalidOp "disk failed"), ignore)
                    let first = queue.Enqueue 1
                    let second = queue.Enqueue 2

                    Expect.throwsT<InvalidOperationException> first "the leader sees the flush failure"
                    Expect.throwsT<InvalidOperationException> second "the follower sees the same flush failure"

                    Expect.throwsT<InvalidOperationException>
                        (fun () -> queue.Enqueue 3 |> ignore)
                        "a failed queue refuses later writes"

                testCase "capacity applies backpressure until the leader drains"
                <| fun _ ->
                    let batches = ResizeArray<int list>()
                    let queue = Fsdb.GroupCommit.Queue<int>(1, batches.Add, ignore)
                    let first = queue.Enqueue 1
                    let follower =
                        Threading.Tasks.Task.Run<unit -> unit>(System.Func<unit -> unit>(fun () -> queue.Enqueue 2))
                    Expect.isFalse (follower.Wait(TimeSpan.FromMilliseconds 100.)) "a full queue blocks another producer"
                    first ()
                    Expect.isTrue (follower.Wait(TimeSpan.FromSeconds 5.)) "draining releases the producer"
                    follower.Result ()
                    Expect.sequenceEqual batches [ [ 1 ]; [ 2 ] ] "both writes retain order"

                testCase "concurrent producers: every acked write lands in exactly one batch, in per-producer order"
                <| fun _ ->
                    // Flush callbacks are serialized, so `batches` is confined
                    // to the active queue leader while producers race for it.
                    let batches = ResizeArray<int list>()
                    let queue = Fsdb.GroupCommit.Queue<int>(4, batches.Add, ignore)
                    let producers = 8
                    let perProducer = 50

                    let tasks =
                        [| for p in 0 .. producers - 1 ->
                               Threading.Tasks.Task.Run(fun () ->
                                   for i in 0 .. perProducer - 1 do
                                       queue.Enqueue(p * perProducer + i) ()) |]

                    Expect.isTrue
                        (Threading.Tasks.Task.WaitAll(tasks, TimeSpan.FromSeconds 30.))
                        "all producers drain without wedging"

                    let flushed = batches |> List.concat
                    Expect.equal (List.length flushed) (producers * perProducer) "every acked write flushed exactly once"
                    Expect.equal (List.sort flushed) [ 0 .. producers * perProducer - 1 ] "no write lost or duplicated"

                    for p in 0 .. producers - 1 do
                        let mine = flushed |> List.filter (fun v -> v / perProducer = p)

                        Expect.equal
                            mine
                            [ p * perProducer .. (p + 1) * perProducer - 1 ]
                            (sprintf "producer %d's writes flush in the order it acked them" p) ]

          testCase "attach + reload round-trips one value of every Value case, including datetime fractional seconds"
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

          testCase "attach + reload retains a typed spatial column"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "places" [ mkCol "shape" (TGeometry Point) ] [] [] None None |> ignore
              let point = VGeometry(tryGeometryFromText 4326 "POINT(1.5 -2)" |> Option.get)
              insertRows store defaultDatabase "places" None [ [ point ] ] |> ignore

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "places") [ [| point |] ] "geometry row survives WAL replay"

          testCase "WAL and snapshot recovery retain typed TIME values"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "times" [ mkCol "value" (TTime 6) ] [] [] None None |> ignore
              let value = tryParseTimeValue "-838:59:58.123456" |> Option.get |> VTime
              insertRows store defaultDatabase "times" None [ [ value ] ] |> ignore

              Expect.equal (rowsOf (load dir) defaultDatabase "times") [ [| value |] ] "WAL replay"

              snapshotNow dir store
              Expect.equal (rowsOf (load dir) defaultDatabase "times") [ [| value |] ] "snapshot replay"

          testCase "attach + reload preserves BIT(64) boundary values"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "bits" [ mkCol "value" (TBit 64) ] [] [] None None |> ignore
              insertRows store defaultDatabase "bits" None [ [ VUInt 0x8000000000000000UL ]; [ VUInt UInt64.MaxValue ] ] |> ignore

              let expected = [ [| VBit(64, 0x8000000000000000UL) |]; [| VBit(64, UInt64.MaxValue) |] ]
              let walOnly = load dir
              Expect.equal (rowsOf walOnly defaultDatabase "bits") expected "WAL replay preserves BIT values"

              snapshotNow dir store
              let snapshotted = load dir
              Expect.equal (rowsOf snapshotted defaultDatabase "bits") expected "snapshot replay preserves BIT values"

          testCase "WAL replay reapplies a non-strict clamping ALTER MODIFY instead of skipping it"
          <| fun _ ->
              // The replayed store starts strict (fresh-store default);
              // replay must still reapply an ALTER whose re-coercion only
              // succeeded because the original session was non-strict —
              // `applyDdl` forces non-strict for exactly this.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let cCol =
                  { Name = "c"
                    Type = TInt false
                    Nullable = true
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    Generated = None
                    Comment = ""
                    Collation = None
                    Charset = None
                    OnUpdateCurrentTimestamp = false }

              createTable store defaultDatabase "narrow" [ cCol ] [] [] None None |> ignore
              insertRows store defaultDatabase "narrow" None [ [ VInt 300L ] ] |> ignore
              setStrictMode store false

              alterTable store defaultDatabase "narrow" [ ModifyColumn({ cCol with Type = TTinyInt false }, PositionDefault) ]
              |> Result.mapError (failtestf "non-strict clamping ALTER should succeed, got %A")
              |> ignore

              Expect.equal (rowsOf store defaultDatabase "narrow") [ [| VInt 127L |] ] "clamped to 127 before the restart"

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "narrow") [ [| VInt 127L |] ] "replay reapplies the clamp"

              match scan reloaded defaultDatabase "narrow" with
              | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Type)) [ TTinyInt false ] "narrowed type survives replay"
              | Error e -> failtestf "scan after reload failed: %A" e

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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
                    { Name = "created_at"
                      // fsp 6 preserves the stand-in's fraction so replay is
                      // compared against the complete physical value.
                      Type = TDateTime 6
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
                    { Name = "token"
                      Type = TVarchar 64
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false } ]

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

          testCase "concurrent same-row commits and a checkpoint replay to the published value"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let columns = [ mkCol "id" (TInt false); mkCol "n" (TInt false) ]
              createTable store defaultDatabase "counter" columns [] [] None None |> ignore
              insertRows store defaultDatabase "counter" None [ [ VInt 1L; VInt 0L ] ] |> ignore
              use start = new Threading.ManualResetEventSlim()

              let increment () =
                  start.Wait()

                  updateRows
                      store
                      defaultDatabase
                      "counter"
                      None
                      (fun _ -> Ok true)
                      (fun row ->
                          match row.[1] with
                          | VInt value -> Ok [| row.[0]; VInt(value + 1L) |]
                          | value -> failtestf "expected integer counter, got %A" value)
                  |> function
                      | Ok 1 -> ()
                      | result -> failtestf "increment failed: %A" result

              let writers = Array.init 64 (fun _ -> Threading.Tasks.Task.Run increment)
              let checkpoint = Threading.Tasks.Task.Run(fun () -> start.Wait(); snapshotNow dir store)
              start.Set()
              Threading.Tasks.Task.WaitAll(Array.append writers [| checkpoint |])

              let live = rowsOf store defaultDatabase "counter"
              let reloaded = load dir |> fun reloaded -> rowsOf reloaded defaultDatabase "counter"
              Expect.equal live [ [| VInt 1L; VInt 64L |] ] "every concurrent update committed"
              Expect.equal reloaded live "checkpoint and WAL preserve publication order"

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
                    Comment = ""
                    Collation = None
                    Charset = None
                    OnUpdateCurrentTimestamp = false }

              alterTable
                  store
                  defaultDatabase
                  "widgets"
                  [ AddColumn(extraCol, PositionDefault)
                    AddIndex { Name = "ix_sku"; KeyColumns = indexColumns [ "sku" ]; Unique = false; Visible = true; Kind = BTree } ]
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
              // Private snapshots have no durable side effects until publication.

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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false } ]
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
              // Replayed updates must move unique keys before the final
              // compatibility rebuild so the live snapshot mirror stays usable.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "pk" (usersColumns |> List.filter (fun c -> c.Name <> "note")) [] [] None None |> ignore
              insertRows store defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 1L; VString "a" ]; [ VInt 2L; VString "b" ]; [ VInt 3L; VString "c" ] ]
              |> ignore

              updateRows store defaultDatabase "pk" None (fun row -> Ok(row.[0] = VInt 1L)) (fun row -> Ok [| VInt 10L; row.[1] |])
              |> ignore

              let reloaded = load dir

              match insertRows reloaded defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 1L; VString "d" ] ] with
              | Ok { LastInsertId = 1L; Affected = 1 } -> ()
              | other -> failtestf "expected id 1 to be free again after replay, got %A" other

              match insertRows reloaded defaultDatabase "pk" (Some [ "id"; "name" ]) [ [ VInt 10L; VString "e" ] ] with
              | Error(DuplicateKey("PRIMARY", _)) -> ()
              | other -> failtestf "expected id 10 to be rejected as a duplicate after replay, got %A" other

          testCase "WAL replay accepts an INSERT that reuses a deleted primary key"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let primaryKey = { mkCol "id" (TInt false) with Nullable = false; PrimaryKey = true }
              createTable store defaultDatabase "reused" [ primaryKey ] [] [] None None
              |> ignore
              insertRows store defaultDatabase "reused" None [ [ VInt 1L ] ] |> ignore
              deleteRows store defaultDatabase "reused" (fun _ -> Ok true) |> ignore
              insertRows store defaultDatabase "reused" None [ [ VInt 1L ] ] |> ignore

              Expect.equal (rowsOf (load dir) defaultDatabase "reused") [ [| VInt 1L |] ] "the reused key survives recovery"

          testCase "WAL replay preserves REPLACE candidate order and optimized updates"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run session sql =
                  match handle session sql with
                  | session', Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | session', _ -> session'

              [ "CREATE TABLE t (id INT PRIMARY KEY, n INT)"
                "REPLACE INTO t VALUES (1, 10), (1, 20)"
                "REPLACE INTO t VALUES (1, 30)" ]
              |> List.fold run session
              |> ignore

              let reloaded = load dir
              Expect.equal (rowsOf reloaded defaultDatabase "t") [ [| VInt 1L; VInt 30L |] ] "the last candidate survives replay"

          testCase "snapshot and WAL recovery rebuild non-unique equality and ordered indexes"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let category = { mkCol "category" (TVarchar 20) with Nullable = false }
              let prefixIndex =
                  { Name = "ix_category_prefix"
                    KeyColumns = [ { Name = "category"; PrefixLength = Some 3; Transform = None; Direction = Asc } ]
                    Unique = false
                    Visible = true
                    Kind = BTree }
              let fullIndex = { Name = "ix_category"; KeyColumns = indexColumns [ "category" ]; Unique = false; Visible = true; Kind = BTree }
              createTable store defaultDatabase "items" [ mkCol "id" (TInt false); category ] [ prefixIndex; fullIndex ] [] None None |> ignore
              createTable store defaultDatabase "prefix_items" [ mkCol "id" (TInt false); category ] [ prefixIndex ] [] None None |> ignore
              insertRows store defaultDatabase "items" None [ [ VInt 1L; VString "books" ]; [ VInt 2L; VString "books" ]; [ VInt 3L; VString "music" ] ] |> ignore
              insertRows store defaultDatabase "prefix_items" None [ [ VInt 1L; VString "books" ]; [ VInt 2L; VString "books" ]; [ VInt 3L; VString "music" ] ] |> ignore
              snapshotNow dir store
              insertRows store defaultDatabase "items" None [ [ VInt 4L; VString "books" ] ] |> ignore
              insertRows store defaultDatabase "prefix_items" None [ [ VInt 4L; VString "books" ] ] |> ignore

              let reloaded = load dir
              match trySecondaryLookup reloaded defaultDatabase "items" "category" (VString "books") with
              | Some(_, rows) -> Expect.equal (rows |> List.map (snd >> fun row -> row.[0])) [ VInt 1L; VInt 2L; VInt 4L ] "recovered prefix buckets preserve row order"
              | None -> failtest "expected a recovered prefix-index probe"

              match trySecondaryRangeLookup reloaded defaultDatabase "items" "category" (Some(VString "books", true)) (Some(VString "music", false)) with
              | Some lookup -> Expect.equal (lookup.RangeRows |> List.map (snd >> fun row -> row.[0])) [ VInt 1L; VInt 2L; VInt 4L ] "recovered ordered entries preserve row order"
              | None -> failtest "expected a recovered ordered secondary-index probe"

              match trySecondaryRangeLookup reloaded defaultDatabase "prefix_items" "category" (Some(VString "books", true)) (Some(VString "boz", false)) with
              | Some lookup -> Expect.equal (lookup.RangeRows |> List.map (snd >> fun row -> row.[0])) [ VInt 1L; VInt 2L; VInt 4L ] "recovered prefix ranges include matching buckets"
              | None -> failtest "expected a recovered prefix range probe"

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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
                    { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false } ]
                  []
                  []
                  None
                  None
              |> ignore

              insertRows setupStore defaultDatabase "hot" None [ for i in 1 .. rowCount -> [ VInt(int64 i); VInt 0L ] ]
              |> ignore

              let catalogTableCount =
                  setupStore.Catalog |> Map.toSeq |> Seq.sumBy (snd >> Map.count)

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

              Expect.isLessThan
                  reindexesDuringLoad
                  (2 * catalogTableCount + 4)
                  (sprintf
                      "loading a snapshot + %d single-row UPDATEs against a %d-row UNIQUE-indexed table triggered %d reindexes — looks like a per-event reindex again"
                      rowCount
                      rowCount
                      reindexesDuringLoad)

          testCase "the mysql system schema round-trips through snapshot + reload, and a mutated user row survives"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              // Mutate mysql.user through the ordinary row path (what CREATE
              // USER will do) so the WAL carries it.
              insertRows
                  store
                  "mysql"
                  "user"
                  (Some [ "Host"; "User"; "plugin"; "authentication_string" ])
                  [ [ VString "%"; VString "alice"; VString "mysql_native_password"; VString "*HASH" ] ]
              |> Result.mapError (failtestf "insert into mysql.user failed: %A")
              |> ignore

              let reloaded = load dir

              let users =
                  rowsOf reloaded "mysql" "user" |> List.map (fun r -> r.[1]) |> List.sortBy string

              Expect.equal users [ VString "alice"; VString "root" ] "root bootstrap row + the persisted alice row"

          testCase "CREATE USER / SET PASSWORD / DROP USER mutations replay from the WAL"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              Fsdb.Auth.createUserWithTlsRequirement store "alice" "%" (Some "pw1") RequireSsl |> ignore
              Fsdb.Auth.createUser store "alice" "localhost" (Some "local") |> ignore
              Fsdb.Auth.createUser store "bob" "%" None |> ignore
              Fsdb.Auth.setPassword store "alice" "%" "pw2" |> ignore
              Fsdb.Auth.dropUser store "bob" "%" |> ignore

              let reloaded = load dir

              match Fsdb.Auth.tryUserRow reloaded "alice" with
              | Some(cols, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash cols row)
                      (Fsdb.Auth.nativePasswordHash "pw2")
                      "replayed alice with her updated hash"
                  Expect.equal (Fsdb.Auth.accountTlsRequirement cols row) RequireSsl "replayed TLS requirement"
              | None -> failtest "expected alice to survive the reload"

              match Fsdb.Auth.tryUserRowForAccount reloaded (Fsdb.Auth.account "alice" "localhost") with
              | Some(cols, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash cols row)
                      (Fsdb.Auth.nativePasswordHash "local")
                      "replayed localhost account separately"
              | None -> failtest "expected localhost alice to survive the reload"

              Expect.isNone (Fsdb.Auth.tryUserRow reloaded "bob") "bob's replayed drop stuck"

          testCase "GRANT/REVOKE mutations to mysql.db and tables_priv replay from the WAL"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              Fsdb.Auth.createUser store "worker" "%" None |> ignore
              Fsdb.Auth.grant store [ "SELECT"; "UPDATE" ] (Fsdb.Auth.OnDb "shop") [ "worker", "%" ] false |> ignore
              Fsdb.Auth.grant store [ "DELETE" ] (Fsdb.Auth.OnTable("shop", "orders")) [ "worker", "%" ] false |> ignore
              Fsdb.Auth.grant store [ "INSERT" ] (Fsdb.Auth.OnDb "shop") [ "worker", "%" ] false |> ignore
              Fsdb.Auth.revoke store [ "UPDATE" ] (Fsdb.Auth.OnDb "shop") [ "worker", "%" ] |> ignore

              let reloaded = load dir

              match Fsdb.Auth.renderGrants reloaded "worker" with
              | Ok(_, lines) ->
                  Expect.equal
                      lines
                      [ "GRANT USAGE ON *.* TO `worker`@`%`"
                        "GRANT SELECT, INSERT ON `shop`.* TO `worker`@`%`"
                        "GRANT DELETE ON `shop`.`orders` TO `worker`@`%`" ]
                      "replayed grants minus the revoked UPDATE"
              | Error e -> failtestf "expected worker's grants after reload, got %A" e

          testCase "a snapshot written without the mysql schema gets it re-seeded on load"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = create ()
              // Simulate a pre-feature snapshot: drop mysql from the catalog
              // before writing it out.
              store.Databases.TryRemove "mysql" |> ignore
              snapshotNow dir store

              let reloaded = load dir
              Expect.isTrue (databaseExists reloaded "mysql") "mysql re-seeded"

              let users = rowsOf reloaded "mysql" "user" |> List.map (fun r -> r.[1])
              Expect.equal users [ VString "root" ] "bootstrap root row present"

          testCase "stored routines and events survive WAL and snapshot recovery"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let session, result = handle session "SET NAMES latin1 COLLATE latin1_bin"
              Expect.equal result (Affected 0UL) "set routine charset"
              let session, result = handle session "SET SESSION sql_mode=''"
              Expect.equal result (Affected 0UL) "set routine sql_mode"

              match
                  handle
                      session
                      "CREATE PROCEDURE topics(IN num INT) SQL SECURITY INVOKER BEGIN DECLARE doubled INT DEFAULT num * 2; SELECT doubled; END"
                  |> snd
              with
              | Affected 0UL -> ()
              | other -> failtestf "expected procedure creation, got %A" other

              match
                  handle
                      session
                      "CREATE FUNCTION doubled(value INT) RETURNS INT DETERMINISTIC RETURN value * 2"
                  |> snd
              with
              | Affected 0UL -> ()
              | other -> failtestf "expected function creation, got %A" other

              match
                  handle
                      session
                      "CREATE EVENT durable_event ON SCHEDULE EVERY 1 DAY ON COMPLETION PRESERVE DISABLE COMMENT 'durable' DO SELECT 1"
                  |> snd
              with
              | Affected 0UL -> ()
              | other -> failtestf "expected event creation, got %A" other

              let reloaded = load dir
              let recovered = Fsdb.Session.create 2 reloaded

              match handle recovered "SHOW CREATE PROCEDURE topics" |> snd with
              | ResultSet(_, [ [ Some "topics"; Some ""; Some ddl; Some "latin1"; Some "latin1_bin"; Some "utf8mb4_0900_ai_ci" ] ]) ->
                  Expect.stringContains ddl "PROCEDURE `topics`(IN num INT) SQL SECURITY INVOKER" "signature recovered"
              | other -> failtestf "expected recovered procedure metadata, got %A" other

              match handle recovered "CALL topics(6)" |> snd with
              | MultipleResults [ (ResultSet([ "doubled" ], [ [ Some "12" ] ]), _); (Affected 0UL, []) ] -> ()
              | other -> failtestf "expected recovered compound procedure execution, got %A" other

              match handle recovered "SELECT doubled(7)" |> snd with
              | ResultSet(_, [ [ Some "14" ] ]) -> ()
              | other -> failtestf "expected recovered stored function execution, got %A" other

              match handle recovered "SHOW CREATE EVENT durable_event" |> snd with
              | ResultSet(_, [ [ Some "durable_event"; Some ""; Some "SYSTEM"; Some ddl; Some "latin1"; Some "latin1_bin"; _ ] ]) ->
                  Expect.stringContains ddl "ON COMPLETION PRESERVE DISABLE COMMENT 'durable'" "event metadata recovered"
              | other -> failtestf "expected recovered event metadata, got %A" other

              snapshotNow dir reloaded

              let snapshotted = load dir
              let recovered = Fsdb.Session.create 3 snapshotted

              match handle recovered "SELECT doubled(9)" |> snd with
              | ResultSet(_, [ [ Some "18" ] ]) -> ()
              | other -> failtestf "expected snapshotted stored function execution, got %A" other

              match handle recovered "SHOW CREATE EVENT durable_event" |> snd with
              | ResultSet(_, [ [ Some "durable_event"; _; _; Some ddl; _; _; _ ] ]) ->
                  Expect.stringContains ddl "COMMENT 'durable'" "snapshotted event metadata recovered"
              | other -> failtestf "expected snapshotted event metadata, got %A" other

          testCase "scheduled events execute after WAL recovery"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  match handle session sql with
                  | next, Affected _ -> next
                  | _, result -> failtestf "expected %s to succeed, got %A" sql result

              let session = apply session "CREATE TABLE recovered_event_log (value INT)"

              let _ =
                  apply
                      session
                      "CREATE EVENT recovered_event ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 SECOND ON COMPLETION PRESERVE DO INSERT INTO recovered_event_log VALUES (1)"

              let recovered = load dir
              use scheduler = Fsdb.EventScheduler.acquire recovered Fsdb.Functions.empty
              let timer = System.Diagnostics.Stopwatch.StartNew()

              while timer.Elapsed < TimeSpan.FromSeconds 4.0 && TestSupport.Sql.rows recovered "SELECT value FROM recovered_event_log" = [] do
                  System.Threading.Thread.Sleep 25

              Expect.equal
                  (TestSupport.Sql.rows recovered "SELECT value FROM recovered_event_log")
                  [ [ Some "1" ] ]
                  "recovered event body"

              Expect.equal
                  (TestSupport.Sql.rows recovered "SELECT status,last_executed IS NOT NULL FROM mysql.events WHERE event_name='recovered_event'")
                  [ [ Some "DISABLED"; Some "1" ] ]
                  "recovered event completion"

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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false } ]
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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
                    { Name = "n"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false } ]
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
                    Comment = ""
                    Collation = None
                    Charset = None
                    OnUpdateCurrentTimestamp = false }

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
                    Generated = Some(BinOp(Mul, Col "a", Lit(VInt 2L)), Stored)
                    Comment = ""
                    Collation = None
                    Charset = None
                    OnUpdateCurrentTimestamp = false }

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
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
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
                  | Some(BinOp(Mul, Col "a", Lit(VInt 2L)), Stored) -> ()
                  | other -> failtestf "expected the generated expression to survive the restart intact, got %A" other
              | Error e -> failtestf "expected table 'g' to reload, got %A" e

          testCase "a generated signed-subtraction expression survives a restart"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let source = mkCol "source" (TBigInt true)
              let difference =
                  { mkCol "difference" (TBigInt false) with
                      Generated = Some(BinOp(SignedSub, Col "source", Lit(VInt 1L)), Stored) }

              createTable store defaultDatabase "signed_generated" [ source; difference ] [] [] None None
              |> ignore

              let reloaded = load dir

              match scan reloaded defaultDatabase "signed_generated" with
              | Ok(columns, _) ->
                  match columns |> List.tryFind (fun column -> column.Name = "difference") |> Option.bind _.Generated with
                  | Some(BinOp(SignedSub, Col "source", Lit(VInt 1L)), Stored) -> ()
                  | other -> failtestf "expected signed subtraction to survive the restart, got %A" other
              | Error error -> failtestf "expected signed_generated to reload, got %A" error

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

          testCase "composite primary declaration order survives WAL and snapshot recovery"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store
              let session, result =
                  handle session "CREATE TABLE ordered_primary (first INT, second INT, body TEXT, PRIMARY KEY (second DESC, first ASC), KEY ix_body (body(12) DESC) INVISIBLE)"

              match result with
              | Affected 0UL -> ()
              | other -> failtestf "expected CREATE TABLE to succeed, got %A" other

              let assertOrder (sourceStore: Store) visible message =
                  match Fsdb.InformationSchema.findTable sourceStore.Catalog defaultDatabase "ordered_primary" with
                  | Ok table ->
                      Expect.equal (primaryKeyColumns table) [ "second"; "first" ] message

                      let primaryIndex = table.Indexes |> List.find (fun index -> index.Name = "PRIMARY")
                      Expect.equal (primaryIndex.KeyColumns |> List.map _.Direction) [ Desc; Asc ] "primary directions survive recovery"

                      let bodyIndex = table.Indexes |> List.find (fun index -> index.Name = "ix_body")
                      Expect.equal bodyIndex.KeyColumns [ { Name = "body"; PrefixLength = Some 12; Transform = None; Direction = Desc } ] "key-part attributes survive recovery"
                      Expect.equal bodyIndex.Visible visible "visibility survives recovery"
                  | Error error -> failtestf "expected ordered_primary after recovery, got %A" error

              assertOrder (load dir) false "WAL retains the key order"
              snapshotNow dir store
              assertOrder (load dir) false "snapshot retains the key order"

              let _, altered = handle session "ALTER TABLE ordered_primary ALTER INDEX ix_body VISIBLE"
              Expect.equal altered (Affected 0UL) "visibility altered"
              assertOrder (load dir) true "WAL retains altered visibility"

          testCase "a column comment survives a restart"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let columns = [ { mkCol "id" (TInt false) with Comment = "created by import" } ]
              createTable store defaultDatabase "documented" columns [] [] None None |> ignore

              match scan (load dir) defaultDatabase "documented" with
              | Ok([ column ], _) -> Expect.equal column.Comment "created by import" "the WAL comment survives"
              | other -> failtestf "expected one WAL-reloaded column, got %A" other

              snapshotNow dir store

              let reloaded = load dir

              match scan reloaded defaultDatabase "documented" with
              | Ok([ column ], _) -> Expect.equal column.Comment "created by import" "the snapshot comment survives"
              | other -> failtestf "expected one reloaded column, got %A" other

          testCase "a table comment survives WAL and snapshot recovery"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              createTable store defaultDatabase "documented" [ mkCol "id" (TInt false) ] [] [] None None |> ignore
              alterTable store defaultDatabase "documented" [ SetTableComment "created by import" ] |> ignore

              let tableOf (recovered: Store) = recovered.Catalog.[defaultDatabase].[normalizeTableName "documented"]
              Expect.equal (tableOf (load dir)).TableComment "created by import" "the WAL comment survives"

              snapshotNow dir store
              Expect.equal (tableOf (load dir)).TableComment "created by import" "the snapshot comment survives"

          testCase "pre-comment snapshots and WAL records load with empty comments"
          <| fun _ ->
              let dir = tempDataDir ()
              File.WriteAllBytes(snapshotPath dir, legacySnapshot "from_snapshot")
              File.WriteAllBytes(walPath dir, legacyCreateTableWalRecord "from_wal")

              let reloaded = load dir

              for table in [ "from_snapshot"; "from_wal" ] do
                  match scan reloaded defaultDatabase table with
                  | Ok([ column ], rows) when Seq.isEmpty rows ->
                      Expect.equal column.Comment "" (table + " has no historical column comment")
                      Expect.equal reloaded.Catalog.[defaultDatabase].[normalizeTableName table].TableComment "" (table + " has no historical table comment")
                  | other -> failtestf "expected legacy table '%s' to load, got %A" table other

          testCase "column-comment snapshots load with an empty table comment"
          <| fun _ ->
              let dir = tempDataDir ()
              File.WriteAllBytes(snapshotPath dir, columnCommentSnapshot "documented" "legacy column")

              let reloaded = load dir
              let table = reloaded.Catalog.[defaultDatabase].[normalizeTableName "documented"]
              Expect.equal table.Columns.Head.Comment "legacy column" "the FSN2 column comment survives"
              Expect.equal table.TableComment "" "FSN2 predates table comments"

          testCase "a pre-comment snapshot replays a comment-aware WAL record"
          <| fun _ ->
              let dir = tempDataDir ()
              File.WriteAllBytes(snapshotPath dir, legacySnapshot "from_snapshot")
              let column = { mkCol "id" (TInt false) with Comment = "from WAL" }
              let statement =
                  CreateTable
                      { Name = "from_wal"
                        Columns = [ column ]
                        Indexes = []
                        ForeignKeys = []
                        Checks = []
                        IfNotExists = false
                        Charset = None
                        Collation = None
                        AutoIncrementSeed = None
                        Comment = None }
              File.WriteAllBytes(walPath dir, encodeWalRecord (SchemaChanged(defaultDatabase, statement)))

              let reloaded = load dir

              match scan reloaded defaultDatabase "from_snapshot", scan reloaded defaultDatabase "from_wal" with
              | Ok([ snapshotColumn ], _), Ok([ walColumn ], _) ->
                  Expect.equal snapshotColumn.Comment "" "the legacy snapshot has no comment"
                  Expect.equal walColumn.Comment "from WAL" "the new WAL comment survives"
              | other -> failtestf "expected mixed-format recovery, got %A" other

          testCase "a column's ON UPDATE CURRENT_TIMESTAMP flag survives a restart"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let stampColumns =
                  [ { Name = "id"
                      Type = TInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false }
                    { Name = "stamp"
                      Type = TDateTime 3
                      Nullable = true
                      Default = Some DCurrentTimestamp
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = true } ]

              createTable store defaultDatabase "stamped" stampColumns [] [] None None |> ignore

              let reloaded = load dir

              match Fsdb.InformationSchema.findTable reloaded.Catalog defaultDatabase "stamped" with
              | Ok table ->
                  let stampCol = table.Columns |> List.find (fun c -> c.Name = "stamp")
                  Expect.isTrue stampCol.OnUpdateCurrentTimestamp "ON UPDATE CURRENT_TIMESTAMP survives the restart"
              | Error e -> failtestf "expected table 'stamped' to reload, got %A" e

          testCase "a functional default expression survives WAL recovery"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let expression = FuncCall("ABS", [ BinOp(Sub, Lit(VInt 0L), Lit(VInt 2L)) ])
              let column = { mkCol "n" (TInt false) with Default = Some(DExpression expression) }
              match createTable store defaultDatabase "functional_default" [ column ] [] [] None None with
              | Ok() -> ()
              | Error error -> failtestf "expected table creation, got %A" error

              let reloaded = load dir

              match Fsdb.InformationSchema.findTable reloaded.Catalog defaultDatabase "functional_default" with
              | Ok table -> Expect.equal table.Columns.[0].Default (Some(DExpression expression)) "the expression round-trips"
              | Error error -> failtestf "expected recovered functional_default, got %A" error

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

          TestSupport.processGlobalCase "concurrent writes to two databases across a forced WAL rotation replay with no duplicated/lost rows"
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
              Fsdb.Limits.withSettings [ "wal_rotate_entries", "0" ] (fun () ->
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
                  Expect.equal countB (2 * perThread) "db_b has exactly its inserted rows, no dup/loss across rotation")

          testCase "the Value binary codec (encodeValue/decodeValue) round-trips VJson non-ASCII text exactly"
          <| fun _ ->
              // No column type ever carries a `VJson` value all the way into
              // a stored row — every JSON-typed column coerces it down to
              // `VString` at write time (`Storage.coerceValue`, `TJson` case;
              // see also the round-trip test above). This pins the WAL/
              // snapshot binary codec's own VJson tag (`Value.encodeValue`
              // 0x08) directly, since no table column can exercise it.
              let original = VJson """{"emoji":"🎉","key":"ünïcödé"}"""
              let w = Writer()
              encodeValue w original
              let r = Reader(w.ToArray())
              Expect.equal (decodeValue r) original "VJson round-trips through encodeValue/decodeValue"

          testCase "the Value binary codec round-trips zero temporal values"
          <| fun _ ->
              let date = tryZeroDate 2020 0 1 |> Option.get
              let dateTime = tryZeroDateTime date 12 34 56 123_000 |> Option.get

              let time = tryParseTimeValue "-838:59:58.123456" |> Option.get

              for original in [ VZeroDate date; VZeroDateTime dateTime; VTime time ] do
                  let w = Writer()
                  encodeValue w original
                  let r = Reader(w.ToArray())
                  Expect.equal (decodeValue r) original (sprintf "%A round-trips" original)

          testCase "the streamed binary reader rejects impossible lengths before allocating"
          <| fun _ ->
              let encodedLength (length: int64) =
                  let w = Writer()
                  w.WriteByte 0xfeuy
                  w.WriteInt64LE length
                  w.ToArray()

              Expect.throwsT<EndOfStreamException>
                  (fun () -> Fsdb.Binary.StreamReader(new MemoryStream(encodedLength (int64 Int32.MaxValue))).ReadLenEncString() |> ignore)
                  "a declared length beyond the remaining stream is rejected"

              Expect.throwsT<InvalidDataException>
                  (fun () -> Fsdb.Binary.StreamReader(new MemoryStream(encodedLength 0x100000000L)).ReadLenEncString() |> ignore)
                  "a length that cannot fit in an array never wraps through int"

          testCase "WAL transaction nesting beyond the decode limit fails without overflowing the process stack"
          <| fun _ ->
              let dir = tempDataDir ()

              let nested =
                  [ 1..258 ]
                  |> List.fold (fun event _ -> TransactionCommitted [ event ]) (RowsInserted(defaultDatabase, "t", []))

              File.WriteAllBytes(walPath dir, encodeWalRecord nested)
              load dir |> ignore
              Expect.equal (FileInfo(walPath dir).Length) 0L "the rejected first WAL record is truncated"

          testCase "the Value binary codec round-trips the whole BIGINT UNSIGNED range, including the half int64 cannot hold"
          <| fun _ ->
              // `encodeValue`'s 0x09 tag writes the same eight bytes as
              // `VInt`; the tag is what says to read them back unsigned, so
              // the top of the range is exactly where a lost tag shows up.
              for original in [ VUInt 0UL; VUInt 9223372036854775808UL; VUInt UInt64.MaxValue ] do
                  let w = Writer()
                  encodeValue w original
                  let r = Reader(w.ToArray())
                  Expect.equal (decodeValue r) original (sprintf "%A round-trips" original)

          testCase "DOUBLE/DECIMAL/BLOB/DATE values round-trip exactly through real columns, both via a WAL-only reload and a snapshot+reload"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let columns =
                  [ { (mkCol "id" (TInt false)) with Nullable = false; AutoIncrement = true; PrimaryKey = true }
                    mkCol "d" (TDouble false)
                    mkCol "dec" (TDecimal(20, 4, false))
                    mkCol "blb" TBlob
                    mkCol "dt" TDate ]

              createTable store defaultDatabase "codec" columns [] [] None None |> ignore

              // A negative double, a DECIMAL(20,4) at its exact digit ceiling
              // (16 integer digits + 4 fractional = 20, the column's own
              // limit) whose unscaled magnitude (~1e20) exceeds 2^64 and so
              // sets all three of `Decimal.GetBits`' integer words, and a
              // zero-length byte string.
              let row1: Value list =
                  [ VNull; VDouble -1.5e300; VDecimal 9999999999999999.9999M; VBytes [||]; VDate(DateOnly(2026, 8, 18)) ]

              // A 300-byte string (past the 255-byte lenenc-int boundary)
              // and a negative, differently-scaled decimal.
              let bigBytes = Array.init 300 (fun i -> byte (i % 256))
              let row2: Value list =
                  [ VNull; VDouble 3.14159265358979; VDecimal -1234567890123.456M; VBytes bigBytes; VDate(DateOnly(1970, 1, 1)) ]

              insertRows store defaultDatabase "codec" None [ row1; row2 ] |> ignore

              let expected =
                  [ [| VInt 1L; VDouble -1.5e300; VDecimal 9999999999999999.9999M; VBytes [||]; VDate(DateOnly(2026, 8, 18)) |]
                    [| VInt 2L; VDouble 3.14159265358979; VDecimal -1234567890123.4560M; VBytes bigBytes; VDate(DateOnly(1970, 1, 1)) |] ]

              // WAL-only reload: no snapshot has been taken yet.
              let walOnly = load dir
              Expect.equal (rowsOf walOnly defaultDatabase "codec") expected "every value round-trips through a WAL-only reload"

              snapshotNow dir store

              let snapshotted = load dir
              Expect.equal (rowsOf snapshotted defaultDatabase "codec") expected "every value round-trips through a snapshot + reload"

          testCase "WAL replay preserves CREATE and TRUNCATE times"
          <| fun _ ->
              TestSupport.withDirectory "persistence" (fun dir ->
                  let store = load dir
                  attach dir store
                  createTable store defaultDatabase "stable_time" [ mkCol "id" (TInt false) ] [] [] None None |> ignore
                  let expected = store.Catalog.[defaultDatabase].["stable_time"].CreateTime
                  Threading.Thread.Sleep 10
                  let reloaded = load dir
                  let actual = reloaded.Catalog.[defaultDatabase].["stable_time"].CreateTime
                  Expect.equal actual expected "CREATE time"

                  Threading.Thread.Sleep 10
                  truncate store defaultDatabase "stable_time" |> ignore
                  let expected = store.Catalog.[defaultDatabase].["stable_time"].CreateTime
                  Threading.Thread.Sleep 10
                  let reloaded = load dir
                  let actual = reloaded.Catalog.[defaultDatabase].["stable_time"].CreateTime
                  Expect.equal actual expected "TRUNCATE time")

          testCase "a table with a column of every ColumnType survives a restart with byte-identical SHOW CREATE TABLE output"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store

              let allTypeColumns =
                  [ mkCol "c_tinyint" (TTinyInt false)
                    mkCol "c_smallint" (TSmallInt false)
                    mkCol "c_mediumint" (TMediumInt true)
                    mkCol "c_int" (TInt false)
                    mkCol "c_bigint" (TBigInt true)
                    mkCol "c_bit" (TBit 9)
                    mkCol "c_char" (TChar 10)
                    mkCol "c_varchar" (TVarchar 20)
                    mkCol "c_tinytext" TTinyText
                    mkCol "c_text" TText
                    mkCol "c_mediumtext" TMediumText
                    mkCol "c_longtext" TLongText
                    mkCol "c_binary" (TBinary 4)
                    mkCol "c_varbinary" (TVarBinary 8)
                    mkCol "c_tinyblob" TTinyBlob
                    mkCol "c_blob" TBlob
                    mkCol "c_mediumblob" TMediumBlob
                    mkCol "c_longblob" TLongBlob
                    mkCol "c_enum" (TEnum [ "a"; "b" ])
                    mkCol "c_set" (TSet [ "x"; "y" ])
                    mkCol "c_decimal" (TDecimal(10, 2, false))
                    mkCol "c_double" (TDouble false)
                    mkCol "c_float" (TFloat false)
                    mkCol "c_double_unsigned" (TDouble true)
                    mkCol "c_float_unsigned" (TFloat true)
                    mkCol "c_date" TDate
                    mkCol "c_datetime" (TDateTime 3)
                    mkCol "c_timestamp" (TTimestamp 6)
                    mkCol "c_time" (TTime 2)
                    mkCol "c_year" TYear
                    mkCol "c_json" TJson ]

              createTable store defaultDatabase "alltypes" allTypeColumns [] [] None None |> ignore

              let before =
                  match Fsdb.InformationSchema.showCreateTable store.Catalog defaultDatabase "alltypes" with
                  | Ok(_, [ [ _; Some ddl ] ]) -> ddl
                  | other -> failtestf "expected SHOW CREATE TABLE to succeed before restart, got %A" other

              let reloaded = load dir

              let after =
                  match Fsdb.InformationSchema.showCreateTable reloaded.Catalog defaultDatabase "alltypes" with
                  | Ok(_, [ [ _; Some ddl ] ]) -> ddl
                  | other -> failtestf "expected SHOW CREATE TABLE to succeed after restart, got %A" other

              Expect.equal after before "SHOW CREATE TABLE is byte-identical before and after the restart, across every ColumnType tag"

          testCase "RENAME TABLE / CREATE INDEX / DROP INDEX replay from the WAL"
          <| fun _ ->
              // CREATE/DROP INDEX reach the WAL as `AlterTable` actions, so
              // they pin `encodeAlterAction`'s tags; RENAME TABLE has its own
              // statement tag, so it pins `encodeStatement` 0x06.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run (session: Fsdb.Session.Session) (sql: string) =
                  match handle session sql with
                  | session', Err(code, msg) -> failtestf "%s failed: %d %s" sql code msg
                  | session', _ -> session'

              let session =
                  [ "CREATE TABLE old_name (id INT PRIMARY KEY, c VARCHAR(20))"
                    "INSERT INTO old_name VALUES (1, 'x')"
                    "RENAME TABLE old_name TO new_name" // 0x06
                    "CREATE INDEX ix_c ON new_name (c)" // 0x07
                    "CREATE UNIQUE INDEX ix_lower_c ON new_name ((LOWER(c)))"
                    "CREATE INDEX ix_expression ON new_name ((CASE WHEN c = 'x' THEN lower(c) END))"
                    "CREATE INDEX ix_gone ON new_name (id)"
                    "DROP INDEX ix_gone ON new_name" // 0x08
                    "CREATE TABLE lookup_names (id INT PRIMARY KEY, name VARCHAR(20) COLLATE utf8mb4_bin, INDEX ix_lower_name ((LOWER(name))))"
                    "INSERT INTO lookup_names VALUES (1, 'Reference'), (2, 'REFERENCE')" ]
                  |> List.fold run session

              ignore session
              let reloaded = load dir

              Expect.isTrue (databaseExists reloaded defaultDatabase) "database replayed"

              // The rename replayed: rows live under the new name, and the
              // old one is gone rather than lingering as a stale copy.
              Expect.equal (rowsOf reloaded defaultDatabase "new_name") [ [| VInt 1L; VString "x" |] ] "renamed table kept its rows"

              match scanList reloaded defaultDatabase "old_name" with
              | Error(NoSuchTable _) -> ()
              | other -> failtestf "the pre-rename name should be gone, got %A" other

              let indexes =
                  match Map.tryFind defaultDatabase reloaded.Catalog |> Option.bind (Map.tryFind "new_name") with
                  | Some t -> t.Indexes |> List.map (fun ix -> ix.Name) |> List.sort
                  | None -> failtest "new_name missing after reload"

              Expect.equal indexes [ "PRIMARY"; "ix_c"; "ix_expression"; "ix_lower_c" ] "created indexes replayed while the dropped index stayed dropped"

              match Fsdb.InformationSchema.showCreateTable reloaded.Catalog defaultDatabase "new_name" with
              | Ok(_, [ [ _; Some ddl ] ]) -> Expect.stringContains ddl "KEY `ix_expression` (((case" "expression index survives recovery"
              | other -> failtestf "expected recovered expression DDL, got %A" other

              match handle (Fsdb.Session.create 2 reloaded) "INSERT INTO new_name VALUES (2, 'X')" |> snd with
              | Err(1062, _) -> ()
              | other -> failtestf "expected the recovered functional index to reject a duplicate, got %A" other

              match handle (Fsdb.Session.create 3 reloaded) "SELECT id FROM lookup_names WHERE LOWER(name) = 'reference' ORDER BY id" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "the recovered functional bucket returns both rows"
              | other -> failtestf "expected recovered functional lookup rows, got %A" other

          testCase "every Op tag and every ALTER action survives a WAL round-trip"
          <| fun _ ->
              // `encodeOp`/`encodeAlterAction` are reachable from ordinary SQL
              // — any operator can appear in a generated column's expression,
              // and every action is an ALTER a migration can issue — but most
              // tags had no test, so a wrong byte would only surface as a
              // database that won't reopen. One table exercises the operator
              // set; one ALTER sequence exercises the action set.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run (session: Fsdb.Session.Session) (sql: string) =
                  match handle session sql with
                  | _, Err(code, msg) -> failtestf "%s failed: %d %s" sql code msg
                  | session', _ -> session'

              // One generated column per operator tag (0x01-0x0E).
              let ddl =
                  "CREATE TABLE ops (a INT, b INT, "
                  + "g_and INT GENERATED ALWAYS AS (a > 0 AND b > 0) STORED, "
                  + "g_or INT GENERATED ALWAYS AS (a > 0 OR b > 0) STORED, "
                  + "g_eq INT GENERATED ALWAYS AS (a = b) STORED, "
                  + "g_neq INT GENERATED ALWAYS AS (a <> b) STORED, "
                  + "g_lt INT GENERATED ALWAYS AS (a < b) STORED, "
                  + "g_lte INT GENERATED ALWAYS AS (a <= b) STORED, "
                  + "g_gt INT GENERATED ALWAYS AS (a > b) STORED, "
                  + "g_gte INT GENERATED ALWAYS AS (a >= b) STORED, "
                  + "g_add INT GENERATED ALWAYS AS (a + b) STORED, "
                  + "g_sub INT GENERATED ALWAYS AS (a - b) STORED, "
                  + "g_mul INT GENERATED ALWAYS AS (a * b) STORED, "
                  + "g_div DOUBLE GENERATED ALWAYS AS (a / b) STORED, "
                  + "g_intdiv INT GENERATED ALWAYS AS (a DIV b) STORED, "
                  + "g_nseq INT GENERATED ALWAYS AS (a <=> b) STORED)"

              let session = [ ddl; "INSERT INTO ops (a, b) VALUES (7, 2)" ] |> List.fold run session

              // One ALTER per action tag.
              let session =
                  [ "CREATE TABLE parent (id INT PRIMARY KEY)"
                    "CREATE TABLE acts (id INT, spare INT, note VARCHAR(20), pid INT)"
                    "ALTER TABLE acts ADD COLUMN extra INT"
                    "ALTER TABLE acts DROP COLUMN spare"
                    "ALTER TABLE acts MODIFY COLUMN note VARCHAR(40)"
                    "ALTER TABLE acts CHANGE COLUMN note remark VARCHAR(60)"
                    "ALTER TABLE acts RENAME COLUMN remark TO comment"
                    "ALTER TABLE acts ADD INDEX ix_extra (extra)"
                    "ALTER TABLE acts DROP INDEX ix_extra"
                    "ALTER TABLE acts ADD INDEX ix_old (extra)"
                    "ALTER TABLE acts RENAME INDEX ix_old TO ix_new"
                    "ALTER TABLE acts ADD CONSTRAINT fk_p FOREIGN KEY (pid) REFERENCES parent(id)"
                    "ALTER TABLE acts DROP FOREIGN KEY fk_p"
                    "ALTER TABLE acts ADD PRIMARY KEY (id)"
                    "ALTER TABLE acts DROP PRIMARY KEY"
                    "ALTER TABLE acts ADD PRIMARY KEY (id DESC, pid ASC)"
                    "ALTER TABLE acts ALTER COLUMN extra SET DEFAULT 9"
                    "ALTER TABLE acts CONVERT TO CHARACTER SET latin1"
                    "ALTER TABLE acts AUTO_INCREMENT = 500" ]
                  |> List.fold run session

              ignore session
              let reloaded = load dir

              // Insert *after* the reload so the generated columns are
              // recomputed from the decoded expressions. Asserting on the
              // pre-restart row instead would pass with any operator tag,
              // since STORED columns replay their stored values.
              let reloadedSession = Fsdb.Session.create 2 reloaded

              match handle reloadedSession "INSERT INTO ops (a, b) VALUES (7, 2)" with
              | _, Err(code, msg) -> failtestf "post-reload insert failed: %d %s" code msg
              | _ -> ()

              // 7 and 2 give a distinct answer per operator, so the recomputed
              // row pins which operator each tag decoded to.
              match scanList reloaded defaultDatabase "ops" with
              | Ok(cols, [ _; row ]) ->
                  let value name =
                      cols
                      |> List.tryFindIndex (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                      |> Option.map (fun i -> row.[i])

                  Expect.equal (value "g_and") (Some(VInt 1L)) "AND"
                  Expect.equal (value "g_or") (Some(VInt 1L)) "OR"
                  Expect.equal (value "g_eq") (Some(VInt 0L)) "="
                  Expect.equal (value "g_neq") (Some(VInt 1L)) "<>"
                  Expect.equal (value "g_lt") (Some(VInt 0L)) "<"
                  Expect.equal (value "g_lte") (Some(VInt 0L)) "<="
                  Expect.equal (value "g_gt") (Some(VInt 1L)) ">"
                  Expect.equal (value "g_gte") (Some(VInt 1L)) ">="
                  Expect.equal (value "g_add") (Some(VInt 9L)) "+"
                  Expect.equal (value "g_sub") (Some(VInt 5L)) "-"
                  Expect.equal (value "g_mul") (Some(VInt 14L)) "*"
                  Expect.equal (value "g_div") (Some(VDouble 3.5)) "/"
                  Expect.equal (value "g_intdiv") (Some(VInt 3L)) "DIV"
                  Expect.equal (value "g_nseq") (Some(VInt 0L)) "<=>"
              | other -> failtestf "ops table did not survive the reload: %A" other

              match Map.tryFind defaultDatabase reloaded.Catalog |> Option.bind (Map.tryFind "acts") with
              | Some t ->
                  let names = t.Columns |> List.map (fun c -> c.Name.ToLowerInvariant()) |> List.sort
                  Expect.equal names [ "comment"; "extra"; "id"; "pid" ] "add/drop/change/rename column all replayed"
                  Expect.isEmpty t.ForeignKeys "the dropped foreign key stayed dropped"
                  Expect.equal (t.Indexes |> List.filter (fun ix -> ix.Name = "ix_extra")) [] "the dropped index stayed dropped"
                  Expect.isTrue (t.Indexes |> List.exists (fun ix -> ix.Name = "ix_new")) "the renamed index replayed"
                  let primary = t.Columns |> List.choose (fun column -> if column.PrimaryKey then Some column.Name else None)
                  Expect.equal primary [ "id"; "pid" ] "primary key replacement replayed"
                  let primaryIndex = t.Indexes |> List.find (fun index -> index.Name = "PRIMARY")
                  Expect.equal (primaryIndex.KeyColumns |> List.map _.Direction) [ Desc; Asc ] "primary-key directions replayed"
                  Expect.equal
                      (t.Columns |> List.find (fun c -> c.Name = "extra") |> _.Default)
                      (Some(DConst(VInt 9L)))
                      "ALTER COLUMN SET DEFAULT replayed"
                  Expect.equal t.TableCharset (Some "latin1") "charset conversion replayed"
                  Expect.equal t.TableCollation (Some "latin1_swedish_ci") "default collation replayed"
                  Expect.equal t.NextAutoId 500L "AUTO_INCREMENT seed replayed"
              | None -> failtest "acts table missing after reload"

          testCase "a multi-pair RENAME TABLE is one WAL event, so replay can't apply half of it"
          <| fun _ ->
              // MySQL's RENAME TABLE is atomic across its pairs. Emitting one
              // `alterTable` per pair would log N independently-replayable
              // events, and a WAL truncated between them (the torn-tail case
              // `replayWal` handles) would restore `a` renamed and `c` not.
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run (session: Fsdb.Session.Session) (sql: string) =
                  match handle session sql with
                  | _, Err(code, msg) -> failtestf "%s failed: %d %s" sql code msg
                  | session', _ -> session'

              let session =
                  [ "CREATE TABLE a (id INT PRIMARY KEY)"
                    "CREATE TABLE c (id INT PRIMARY KEY)"
                    "INSERT INTO a VALUES (1)"
                    "INSERT INTO c VALUES (2)" ]
                  |> List.fold run session

              // One statement, both pairs.
              run session "RENAME TABLE a TO b, c TO d" |> ignore

              let reloaded = load dir

              let tables =
                  Map.tryFind defaultDatabase reloaded.Catalog
                  |> Option.map (Map.toList >> List.map fst >> List.sort)
                  |> Option.defaultValue []

              Expect.equal tables [ "b"; "d" ] "both renames replayed, neither original name lingers"
              Expect.equal (rowsOf reloaded defaultDatabase "b") [ [| VInt 1L |] ] "b kept a's row"
              Expect.equal (rowsOf reloaded defaultDatabase "d") [ [| VInt 2L |] ] "d kept c's row"

          testCase "a GENERATED column using CASE/LIKE ESCAPE/IN/BETWEEN/row comparison/CAST/CONCAT survives a restart and still computes correctly"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run (session: Fsdb.Session.Session) (sql: string) =
                  let session', result = handle session sql

                  match result with
                  | Err(code, msg) -> failtestf "%s failed: %d %s" sql code msg
                  | _ -> ()

                  session'

              let ddl =
                  "CREATE TABLE g ("
                  + "a INT, name VARCHAR(20), "
                  + "case_full VARCHAR(20) AS (CASE WHEN a > 10 THEN 'big' WHEN a > 0 THEN 'small' ELSE 'non-positive' END) STORED, "
                  + "case_bare VARCHAR(20) AS (CASE WHEN a = 1 THEN 'one' END) STORED, "
                  + "like_esc INT AS (name LIKE '50!%' ESCAPE '!') STORED, "
                  + "in_set INT AS (a IN (1, 2, 3)) STORED, "
                  + "betw INT AS (a BETWEEN 1 AND 10) STORED, "
                  + "row_eq INT AS (ROW(a, name) = ROW(5, '50%')) STORED, "
                  + "casted VARCHAR(20) AS (CAST(a AS CHAR)) STORED, "
                  + "conc VARCHAR(30) AS (CONCAT('x-', a)) STORED)"

              let session = run session ddl

              // Restart *before* any row exists, so the row below is
              // computed entirely by the reloaded store's own generated-
              // column expressions, not carried over from the live one.
              let reloaded = load dir
              let session2 = Fsdb.Session.create 1 reloaded
              let session2 = run session2 "INSERT INTO g (a, name) VALUES (5, '50%')"

              match handle session2 "SELECT case_full, case_bare, like_esc, in_set, betw, row_eq, casted, conc FROM g" |> snd with
              | ResultSet(_, [ row ]) ->
                  Expect.equal
                      row
                      [ Some "small"; None; Some "1"; Some "0"; Some "1"; Some "1"; Some "5"; Some "x-5" ]
                      "every generated expression computes correctly post-reload"
              | other -> failtestf "expected one row back, got %A" other

              ignore session

          testCase "WAL-only replay (no snapshot ever taken) reproduces ADD COLUMN AFTER, ADD PRIMARY KEY, a FOREIGN KEY add+drop, CREATE/DROP INDEX, TRUNCATE, DROP TABLE, and DROP DATABASE"
          <| fun _ ->
              let dir = tempDataDir ()
              let store = load dir
              attach dir store
              let session = Fsdb.Session.create 1 store

              let run (session: Fsdb.Session.Session) (sql: string) =
                  let session', result = handle session sql

                  match result with
                  | Err(code, msg) -> failtestf "%s failed: %d %s" sql code msg
                  | _ -> ()

                  session'

              let session = run session "CREATE DATABASE db2"
              let session = run session "CREATE TABLE p (id INT NOT NULL)"
              let session = run session "ALTER TABLE p ADD PRIMARY KEY (id)"
              let session = run session "INSERT INTO p (id) VALUES (1)"
              let session = run session "CREATE TABLE t (id INT NOT NULL, name VARCHAR(20))"
              let session = run session "ALTER TABLE t ADD COLUMN extra VARCHAR(10) AFTER id"
              let session = run session "ALTER TABLE t ADD CONSTRAINT fk_t_p FOREIGN KEY (id) REFERENCES p (id)"
              let session = run session "ALTER TABLE t DROP FOREIGN KEY fk_t_p"
              let session = run session "CREATE INDEX ix_name ON t (name)"
              let session = run session "DROP INDEX ix_name ON t"
              let session = run session "INSERT INTO t (id, extra, name) VALUES (1, 'e1', 'n1'), (2, 'e2', 'n2')"
              let session = run session "TRUNCATE TABLE t"
              let session = run session "DROP TABLE p"
              let session = run session "DROP DATABASE db2"
              ignore session

              // `snapshotNow` is never called above — the catalog reaches
              // this restart as WAL records only (barring an unrelated
              // background rotation via `attach`'s size/entry threshold,
              // which a handful of statements never crosses), so `load`
              // rebuilds it entirely through `applyDdl`/`applyEvent` replay.
              let reloaded = load dir

              match Fsdb.InformationSchema.showCreateTable reloaded.Catalog defaultDatabase "t" with
              | Ok(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "`extra`" "the AFTER-positioned ADD COLUMN survives"
                  Expect.isFalse (ddl.Contains "FOREIGN KEY") "the added-then-dropped foreign key is gone"
                  Expect.isFalse (ddl.Contains "ix_name") "the created-then-dropped index is gone"
              | other -> failtestf "expected table 't' to reload, got %A" other

              Expect.isEmpty (rowsOf reloaded defaultDatabase "t") "TRUNCATE survives replay"

              match scan reloaded defaultDatabase "p" with
              | Error(NoSuchTable _) -> ()
              | other -> failtestf "expected table 'p' to be gone (DROP TABLE), got %A" other

              match scan reloaded "db2" "anything" with
              | Error(NoSuchDatabase _) -> ()
              | other -> failtestf "expected database 'db2' to be gone (DROP DATABASE), got %A" other

          testCase "withDataDir durability and Db.onCommit CDC coexist on one store"
          <| fun _ ->
              let dir = tempDataDir ()
              let events = ResizeArray<CommitEvent>()

              let db =
                  Fsdb.Db.create () |> Fsdb.Db.withDataDir dir |> Fsdb.Db.onCommit events.Add

              let conn = Fsdb.Db.connect db
              conn.Query "CREATE TABLE t (n INT)" |> ignore
              conn.Query "INSERT INTO t VALUES (1)" |> ignore

              Expect.isTrue
                  (events
                   |> Seq.exists (function
                       | RowsInserted(_, "t", _) -> true
                       | _ -> false))
                  "the CDC subscriber saw the insert"

              let reloaded = load dir

              match scanList reloaded defaultDatabase "t" with
              | Ok(_, rows) -> Expect.equal (List.length rows) 1 "the WAL subscriber persisted the same insert"
              | Error e -> failtestf "expected table 't' to reload from the WAL, got %A" e ]
