/// Opt-in durability (`--data-dir`): WAL + snapshot, replay on startup — M7
/// on the roadmap. `Db.withDataDir` is the door in: `load` rebuilds a
/// `Store` from whatever's on disk, `attach` subscribes it (via
/// `Storage.Store.OnCommit`) to keep writing.
///
/// Row-level events (`RowsInserted`/`RowsUpdated`/`RowsDeleted`) already
/// carry physically-evaluated `Value[]`s — see `Storage.CommitEvent` — so
/// replaying them is "write exactly these values back", never "re-run an
/// expression" (the whole point: `INSERT ... VALUES (NOW(), UUID())`
/// replays to the *same* row, not a freshly-evaluated one). DDL
/// (`SchemaChanged`) carries the parsed `Ast.Statement`; this module hand-
/// rolls its own compact JSON encoding for the handful of DDL shapes it can
/// contain (there's no `System.Text.Json` F#-union support without a NuGet
/// package this project doesn't take a dependency on — see `Fsdb.fsproj`)
/// and replays it by calling `Storage`'s own DDL functions directly
/// (`Executor.execute` isn't reachable — `Executor.fs` compiles *after*
/// this file).
module Fsdb.Persistence

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json.Nodes
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage

let private walFileName = "wal.jsonl"
let private snapshotFileName = "snapshot.fsdb"

/// Once the WAL crosses this many bytes, or this many appended entries,
/// whichever comes first, `attach`'s subscriber snapshots the whole catalog
/// and truncates it — keeps startup replay bounded instead of an
/// ever-growing WAL. ponytail: fixed constants, not a knob; promote to a
/// `Db.withDataDir` parameter if a real deployment ever needs a different
/// rotation size.
let private walRotateBytes = 64L * 1024L * 1024L
let private walRotateEntries = 100_000

// ---------------------------------------------------------------------
// JSON leaves: strings/bools/ints/lists, plus a `"case"`-tagged object for
// every DU used here — `decodeXxx` is each `encodeXxx`'s exact inverse.
// ---------------------------------------------------------------------

let private str (s: string) : JsonNode = JsonValue.Create s
let private boolNode (b: bool) : JsonNode = JsonValue.Create b
let private intNode (i: int) : JsonNode = JsonValue.Create i
let private i64Node (i: int64) : JsonNode = JsonValue.Create i
let private arr (nodes: JsonNode list) : JsonNode = JsonArray(nodes |> Array.ofList)
let private strArr (xs: string list) : JsonNode = arr (xs |> List.map str)
let private strListOf (node: JsonNode) : string list = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq
let private optStr (o: JsonNode) : string option = match o with null -> None | v -> Some(v.GetValue<string>())
let private strOptNode (s: string option) : JsonNode = s |> Option.map str |> Option.defaultValue null

let private caseObj (name: string) (fields: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()
    o.["case"] <- str name
    fields |> List.iter (fun (k, v) -> o.[k] <- v)
    o

let private caseName (node: JsonNode) : string * JsonObject =
    let o = node.AsObject()
    o.["case"].GetValue<string>(), o

let private encodeRow (row: Value[]) : JsonNode = arr (row |> Array.toList |> List.map (toWire >> str))
let private decodeRow (node: JsonNode) : Value[] = node.AsArray() |> Seq.map (fun n -> ofWire (n.GetValue<string>())) |> Array.ofSeq
let private encodeRows (rows: Value[] list) : JsonNode = arr (rows |> List.map encodeRow)
let private decodeRows (node: JsonNode) : Value[] list = node.AsArray() |> Seq.map decodeRow |> List.ofSeq

// ---------------------------------------------------------------------
// `Ast.ColumnType`
// ---------------------------------------------------------------------

let private encodeColumnType (t: ColumnType) : JsonNode =
    match t with
    | TTinyInt u -> caseObj "TTinyInt" [ "u", boolNode u ]
    | TSmallInt u -> caseObj "TSmallInt" [ "u", boolNode u ]
    | TMediumInt u -> caseObj "TMediumInt" [ "u", boolNode u ]
    | TInt u -> caseObj "TInt" [ "u", boolNode u ]
    | TBigInt u -> caseObj "TBigInt" [ "u", boolNode u ]
    | TChar l -> caseObj "TChar" [ "l", intNode l ]
    | TVarchar l -> caseObj "TVarchar" [ "l", intNode l ]
    | TTinyText -> caseObj "TTinyText" []
    | TText -> caseObj "TText" []
    | TMediumText -> caseObj "TMediumText" []
    | TLongText -> caseObj "TLongText" []
    | TBinary l -> caseObj "TBinary" [ "l", intNode l ]
    | TVarBinary l -> caseObj "TVarBinary" [ "l", intNode l ]
    | TTinyBlob -> caseObj "TTinyBlob" []
    | TBlob -> caseObj "TBlob" []
    | TMediumBlob -> caseObj "TMediumBlob" []
    | TLongBlob -> caseObj "TLongBlob" []
    | TEnum values -> caseObj "TEnum" [ "values", strArr values ]
    | TSet values -> caseObj "TSet" [ "values", strArr values ]
    | TDecimal(p, s) -> caseObj "TDecimal" [ "p", intNode p; "s", intNode s ]
    | TDouble -> caseObj "TDouble" []
    | TFloat -> caseObj "TFloat" []
    | TDate -> caseObj "TDate" []
    | TDateTime -> caseObj "TDateTime" []
    | TTimestamp -> caseObj "TTimestamp" []
    | TTime -> caseObj "TTime" []
    | TYear -> caseObj "TYear" []
    | TJson -> caseObj "TJson" []

let private decodeColumnType (node: JsonNode) : ColumnType =
    let case, o = caseName node
    let f (k: string) = o.[k]

    match case with
    | "TTinyInt" -> TTinyInt(f("u").GetValue<bool>())
    | "TSmallInt" -> TSmallInt(f("u").GetValue<bool>())
    | "TMediumInt" -> TMediumInt(f("u").GetValue<bool>())
    | "TInt" -> TInt(f("u").GetValue<bool>())
    | "TBigInt" -> TBigInt(f("u").GetValue<bool>())
    | "TChar" -> TChar(f("l").GetValue<int>())
    | "TVarchar" -> TVarchar(f("l").GetValue<int>())
    | "TTinyText" -> TTinyText
    | "TText" -> TText
    | "TMediumText" -> TMediumText
    | "TLongText" -> TLongText
    | "TBinary" -> TBinary(f("l").GetValue<int>())
    | "TVarBinary" -> TVarBinary(f("l").GetValue<int>())
    | "TTinyBlob" -> TTinyBlob
    | "TBlob" -> TBlob
    | "TMediumBlob" -> TMediumBlob
    | "TLongBlob" -> TLongBlob
    | "TEnum" -> TEnum(strListOf (f "values"))
    | "TSet" -> TSet(strListOf (f "values"))
    | "TDecimal" -> TDecimal(f("p").GetValue<int>(), f("s").GetValue<int>())
    | "TDouble" -> TDouble
    | "TFloat" -> TFloat
    | "TDate" -> TDate
    | "TDateTime" -> TDateTime
    | "TTimestamp" -> TTimestamp
    | "TTime" -> TTime
    | "TYear" -> TYear
    | "TJson" -> TJson
    | tag -> failwithf "Persistence: unknown ColumnType case '%s' in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Ast.ColumnDefault` / `ColumnDef` / `IndexDef` / `ForeignKeyDef`
// ---------------------------------------------------------------------

let private encodeColumnDefault (d: ColumnDefault) : JsonNode =
    match d with
    | DConst v -> caseObj "DConst" [ "value", str (toWire v) ]
    | DCurrentTimestamp -> caseObj "DCurrentTimestamp" []

let private decodeColumnDefault (node: JsonNode) : ColumnDefault =
    let case, o = caseName node

    match case with
    | "DConst" -> DConst(ofWire (o.["value"].GetValue<string>()))
    | "DCurrentTimestamp" -> DCurrentTimestamp
    | tag -> failwithf "Persistence: unknown ColumnDefault case '%s' in WAL/snapshot" tag

/// `ColumnDef.Generated` (`GENERATED ALWAYS AS (expr)`) isn't replayed —
/// ponytail: no WAL encoding for `Expr` here (it's only reachable through
/// this one field among the DDL shapes `SchemaChanged` carries), so a
/// generated column comes back as a plain one after a restart, the same gap
/// `InformationSchema.showCreateTableDDL` already has for `SHOW CREATE
/// TABLE`. Add an `Expr` encoder if a migration ever depends on `GENERATED`
/// surviving one.
let private encodeColumnDef (c: ColumnDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str c.Name
    o.["type"] <- encodeColumnType c.Type
    o.["nullable"] <- boolNode c.Nullable
    o.["default"] <- (c.Default |> Option.map encodeColumnDefault |> Option.defaultValue null)
    o.["autoIncrement"] <- boolNode c.AutoIncrement
    o.["primaryKey"] <- boolNode c.PrimaryKey
    o.["unique"] <- boolNode c.Unique
    o

let private decodeColumnDef (node: JsonNode) : ColumnDef =
    let o = node.AsObject()

    { Name = o.["name"].GetValue<string>()
      Type = decodeColumnType o.["type"]
      Nullable = o.["nullable"].GetValue<bool>()
      Default = (match o.["default"] with null -> None | d -> Some(decodeColumnDefault d))
      AutoIncrement = o.["autoIncrement"].GetValue<bool>()
      PrimaryKey = o.["primaryKey"].GetValue<bool>()
      Unique = o.["unique"].GetValue<bool>()
      Generated = None }

let private encodeIndexDef (ix: IndexDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str ix.Name
    o.["columns"] <- strArr ix.Columns
    o.["unique"] <- boolNode ix.Unique
    o

let private decodeIndexDef (node: JsonNode) : IndexDef =
    let o = node.AsObject()
    { Name = o.["name"].GetValue<string>(); Columns = strListOf o.["columns"]; Unique = o.["unique"].GetValue<bool>() }

let private encodeForeignKeyDef (fk: ForeignKeyDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str fk.Name
    o.["columns"] <- strArr fk.Columns
    o.["refTable"] <- str fk.RefTable
    o.["refColumns"] <- strArr fk.RefColumns
    o.["onDelete"] <- strOptNode fk.OnDelete
    o.["onUpdate"] <- strOptNode fk.OnUpdate
    o

let private decodeForeignKeyDef (node: JsonNode) : ForeignKeyDef =
    let o = node.AsObject()

    { Name = o.["name"].GetValue<string>()
      Columns = strListOf o.["columns"]
      RefTable = o.["refTable"].GetValue<string>()
      RefColumns = strListOf o.["refColumns"]
      OnDelete = optStr o.["onDelete"]
      OnUpdate = optStr o.["onUpdate"] }

// ---------------------------------------------------------------------
// `Ast.AlterAction` / `Ast.Statement` — only the DDL shapes `SchemaChanged`
// ever wraps (see `Storage`'s every `emit (Some(SchemaChanged ...)))` call);
// any other `Statement` case reaching here would be a `Storage` bug, not a
// WAL-format one, so it's a hard `failwithf` rather than a silent skip.
// ---------------------------------------------------------------------

let private encodeColumnPosition (p: ColumnPosition) : JsonNode =
    match p with
    | PositionDefault -> caseObj "PositionDefault" []
    | PositionFirst -> caseObj "PositionFirst" []
    | PositionAfter column -> caseObj "PositionAfter" [ "column", str column ]

let private decodeColumnPosition (node: JsonNode) : ColumnPosition =
    let case, o = caseName node

    match case with
    | "PositionDefault" -> PositionDefault
    | "PositionFirst" -> PositionFirst
    | "PositionAfter" -> PositionAfter(o.["column"].GetValue<string>())
    | tag -> failwithf "Persistence: unknown ColumnPosition case '%s' in WAL/snapshot" tag

let private encodeAlterAction (a: AlterAction) : JsonNode =
    match a with
    | AddColumn(c, position) -> caseObj "AddColumn" [ "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | DropColumn name -> caseObj "DropColumn" [ "name", str name ]
    | ModifyColumn(c, position) -> caseObj "ModifyColumn" [ "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | ChangeColumn(oldName, c, position) ->
        caseObj "ChangeColumn" [ "oldName", str oldName; "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | RenameTo name -> caseObj "RenameTo" [ "name", str name ]
    | RenameColumnTo(oldName, newName) -> caseObj "RenameColumnTo" [ "oldName", str oldName; "newName", str newName ]
    | AddIndex ix -> caseObj "AddIndex" [ "index", encodeIndexDef ix ]
    | DropIndexAction name -> caseObj "DropIndexAction" [ "name", str name ]
    | AddForeignKey fk -> caseObj "AddForeignKey" [ "fk", encodeForeignKeyDef fk ]
    | DropForeignKey name -> caseObj "DropForeignKey" [ "name", str name ]
    | AddPrimaryKey columns -> caseObj "AddPrimaryKey" [ "columns", strArr columns ]

let private decodeAlterAction (node: JsonNode) : AlterAction =
    let case, o = caseName node

    match case with
    | "AddColumn" -> AddColumn(decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "DropColumn" -> DropColumn(o.["name"].GetValue<string>())
    | "ModifyColumn" -> ModifyColumn(decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "ChangeColumn" -> ChangeColumn(o.["oldName"].GetValue<string>(), decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "RenameTo" -> RenameTo(o.["name"].GetValue<string>())
    | "RenameColumnTo" -> RenameColumnTo(o.["oldName"].GetValue<string>(), o.["newName"].GetValue<string>())
    | "AddIndex" -> AddIndex(decodeIndexDef o.["index"])
    | "DropIndexAction" -> DropIndexAction(o.["name"].GetValue<string>())
    | "AddForeignKey" -> AddForeignKey(decodeForeignKeyDef o.["fk"])
    | "DropForeignKey" -> DropForeignKey(o.["name"].GetValue<string>())
    | "AddPrimaryKey" -> AddPrimaryKey(strListOf o.["columns"])
    | tag -> failwithf "Persistence: unknown AlterAction case '%s' in WAL/snapshot" tag

let private encodeStatement (s: Statement) : JsonNode =
    match s with
    | CreateDatabase(name, ifNotExists) -> caseObj "CreateDatabase" [ "name", str name; "ifNotExists", boolNode ifNotExists ]
    | DropDatabase(name, ifExists) -> caseObj "DropDatabase" [ "name", str name; "ifExists", boolNode ifExists ]
    | CreateTable(name, columns, indexes, fks, ifNotExists) ->
        caseObj
            "CreateTable"
            [ "name", str name
              "columns", arr (columns |> List.map encodeColumnDef)
              "indexes", arr (indexes |> List.map encodeIndexDef)
              "foreignKeys", arr (fks |> List.map encodeForeignKeyDef)
              "ifNotExists", boolNode ifNotExists ]
    | DropTable(names, ifExists) -> caseObj "DropTable" [ "names", strArr names; "ifExists", boolNode ifExists ]
    | AlterTable(table, actions) -> caseObj "AlterTable" [ "table", str table; "actions", arr (actions |> List.map encodeAlterAction) ]
    | RenameTable pairs -> caseObj "RenameTable" [ "pairs", arr (pairs |> List.map (fun (a, b) -> arr [ str a; str b ])) ]
    | CreateIndex(name, table, columns, unique) ->
        caseObj "CreateIndex" [ "name", str name; "table", str table; "columns", strArr columns; "unique", boolNode unique ]
    | DropIndexStmt(name, table) -> caseObj "DropIndexStmt" [ "name", str name; "table", str table ]
    | Truncate table -> caseObj "Truncate" [ "table", str table ]
    | other -> failwithf "Persistence: %A isn't a DDL statement SchemaChanged should ever carry" other

let private decodeStatement (node: JsonNode) : Statement =
    let case, o = caseName node

    match case with
    | "CreateDatabase" -> CreateDatabase(o.["name"].GetValue<string>(), o.["ifNotExists"].GetValue<bool>())
    | "DropDatabase" -> DropDatabase(o.["name"].GetValue<string>(), o.["ifExists"].GetValue<bool>())
    | "CreateTable" ->
        CreateTable(
            o.["name"].GetValue<string>(),
            o.["columns"].AsArray() |> Seq.map decodeColumnDef |> List.ofSeq,
            o.["indexes"].AsArray() |> Seq.map decodeIndexDef |> List.ofSeq,
            o.["foreignKeys"].AsArray() |> Seq.map decodeForeignKeyDef |> List.ofSeq,
            o.["ifNotExists"].GetValue<bool>()
        )
    | "DropTable" -> DropTable(strListOf o.["names"], o.["ifExists"].GetValue<bool>())
    | "AlterTable" -> AlterTable(o.["table"].GetValue<string>(), o.["actions"].AsArray() |> Seq.map decodeAlterAction |> List.ofSeq)
    | "RenameTable" ->
        RenameTable(
            o.["pairs"].AsArray()
            |> Seq.map (fun p ->
                let pair = p.AsArray()
                pair.[0].GetValue<string>(), pair.[1].GetValue<string>())
            |> List.ofSeq
        )
    | "CreateIndex" ->
        CreateIndex(o.["name"].GetValue<string>(), o.["table"].GetValue<string>(), strListOf o.["columns"], o.["unique"].GetValue<bool>())
    | "DropIndexStmt" -> DropIndexStmt(o.["name"].GetValue<string>(), o.["table"].GetValue<string>())
    | "Truncate" -> Truncate(o.["table"].GetValue<string>())
    | tag -> failwithf "Persistence: unknown Statement case '%s' in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Storage.CommitEvent` — one WAL line each.
// ---------------------------------------------------------------------

let rec private encodeEvent (event: CommitEvent) : JsonNode =
    match event with
    | RowsInserted(db, table, rows) -> caseObj "RowsInserted" [ "db", str db; "table", str table; "rows", encodeRows rows ]
    | RowsUpdated(db, table, changes) ->
        caseObj
            "RowsUpdated"
            [ "db", str db
              "table", str table
              "changes", arr (changes |> List.map (fun (before, after) -> arr [ encodeRow before; encodeRow after ])) ]
    | RowsDeleted(db, table, rows) -> caseObj "RowsDeleted" [ "db", str db; "table", str table; "rows", encodeRows rows ]
    | SchemaChanged(db, stmt) -> caseObj "SchemaChanged" [ "db", str db; "stmt", encodeStatement stmt ]
    | TransactionCommitted events -> caseObj "TransactionCommitted" [ "events", arr (events |> List.map encodeEvent) ]

let rec private decodeEvent (node: JsonNode) : CommitEvent =
    let case, o = caseName node

    match case with
    | "RowsInserted" -> RowsInserted(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), decodeRows o.["rows"])
    | "RowsUpdated" ->
        let changes =
            o.["changes"].AsArray()
            |> Seq.map (fun pair ->
                let p = pair.AsArray()
                decodeRow p.[0], decodeRow p.[1])
            |> List.ofSeq

        RowsUpdated(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), changes)
    | "RowsDeleted" -> RowsDeleted(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), decodeRows o.["rows"])
    | "SchemaChanged" -> SchemaChanged(o.["db"].GetValue<string>(), decodeStatement o.["stmt"])
    | "TransactionCommitted" -> TransactionCommitted(o.["events"].AsArray() |> Seq.map decodeEvent |> List.ofSeq)
    | tag -> failwithf "Persistence: unknown CommitEvent case '%s' in WAL" tag

// ---------------------------------------------------------------------
// Replay: applies a decoded `CommitEvent` to a live `Store` via `Storage`'s
// own public write functions — row events write the exact physical values
// back (see the module doc), DDL events redo the same `Storage` call the
// original statement made.
// ---------------------------------------------------------------------

let private warn (context: string) (result: Result<'a, StorageError>) : unit =
    match result with
    | Ok _ -> ()
    | Error e -> eprintfn "fsdb: WAL replay warning (%s): %A" context e

let private applyDdl (store: Store) (db: string) (stmt: Statement) : unit =
    match stmt with
    | CreateDatabase(name, _) -> warn "CreateDatabase" (createDatabase store name)
    | DropDatabase(name, _) -> warn "DropDatabase" (dropDatabase store name)
    | CreateTable(name, columns, indexes, fks, _) -> warn "CreateTable" (createTable store db name columns indexes fks)
    | DropTable(names, _) -> names |> List.iter (fun n -> warn "DropTable" (dropTable store db n))
    | AlterTable(table, actions) -> warn "AlterTable" (alterTable store db table actions)
    | RenameTable pairs -> pairs |> List.iter (fun (oldName, newName) -> warn "RenameTable" (renameTable store db oldName newName))
    | CreateIndex(name, table, columns, unique) ->
        warn "CreateIndex" (alterTable store db table [ AddIndex { Name = name; Columns = columns; Unique = unique } ])
    | DropIndexStmt(name, table) -> warn "DropIndexStmt" (alterTable store db table [ DropIndexAction name ])
    | Truncate table -> warn "Truncate" (truncate store db table)
    | other -> eprintfn "fsdb: WAL replay warning (SchemaChanged): unexpected statement %A" other

/// Rows matching `target` by structural equality — a duplicate-valued table
/// (no unique key on every column) makes "which physical row" ambiguous by
/// value alone; ponytail: replay dedupes by value, not row identity (there
/// is none in this `Value[] list` storage model), which is a no-op the
/// moment a matching row's already been rewritten by an earlier entry in
/// the same event list, so the end state still comes out right — see the
/// module's replay tests.
let rec private applyEvent (store: Store) (event: CommitEvent) : unit =
    match event with
    | RowsInserted(db, table, rows) ->
        if not rows.IsEmpty then
            warn "RowsInserted" (insertRows store db table None (rows |> List.map List.ofArray) |> Result.map ignore)
    | RowsUpdated(db, table, changes) ->
        changes
        |> List.iter (fun (before, after) ->
            warn "RowsUpdated" (updateRows store db table (fun row -> Ok(row = before)) (fun _ -> Ok after) |> Result.map ignore))
    | RowsDeleted(db, table, rows) ->
        rows |> List.iter (fun target -> warn "RowsDeleted" (deleteRows store db table (fun row -> Ok(row = target)) |> Result.map ignore))
    | SchemaChanged(db, stmt) -> applyDdl store db stmt
    | TransactionCommitted events -> events |> List.iter (applyEvent store)

let private replayWal (store: Store) (walPath: string) : unit =
    if File.Exists walPath then
        let mutable stopped = false

        for line in File.ReadAllLines walPath do
            if not stopped && not (String.IsNullOrWhiteSpace line) then
                try
                    applyEvent store (decodeEvent (JsonNode.Parse line))
                with ex ->
                    // A torn final line (a `kill -9` mid-write) is expected,
                    // not corruption to panic over — everything before it
                    // already committed durably; stop, don't guess at what
                    // the rest of the line might have meant.
                    eprintfn "fsdb: WAL replay stopped at a truncated/corrupt line (%s): %s" walPath ex.Message
                    stopped <- true

// ---------------------------------------------------------------------
// Snapshot: the whole catalog, JSON, written atomically (tmp + rename).
// ---------------------------------------------------------------------

let private encodeTable (t: Table) : JsonNode =
    let o = JsonObject()
    o.["originalName"] <- str t.OriginalName
    o.["columns"] <- arr (t.Columns |> List.map encodeColumnDef)
    o.["indexes"] <- arr (t.Indexes |> List.map encodeIndexDef)
    o.["foreignKeys"] <- arr (t.ForeignKeys |> List.map encodeForeignKeyDef)
    o.["nextAutoId"] <- i64Node t.NextAutoId
    o.["rows"] <- encodeRows t.Rows
    o

let private decodeTable (node: JsonNode) : Table =
    let o = node.AsObject()

    { OriginalName = o.["originalName"].GetValue<string>()
      Columns = o.["columns"].AsArray() |> Seq.map decodeColumnDef |> List.ofSeq
      Indexes = o.["indexes"].AsArray() |> Seq.map decodeIndexDef |> List.ofSeq
      ForeignKeys = o.["foreignKeys"].AsArray() |> Seq.map decodeForeignKeyDef |> List.ofSeq
      Rows = decodeRows o.["rows"]
      NextAutoId = o.["nextAutoId"].GetValue<int64>() }

let private encodeCatalog (catalog: Catalog) : JsonNode =
    let databases = JsonObject()

    catalog
    |> Map.iter (fun dbName db ->
        let tables = JsonObject()
        db |> Map.iter (fun tableKey table -> tables.[tableKey] <- encodeTable table)
        databases.[dbName] <- tables)

    let root = JsonObject()
    root.["databases"] <- databases
    root

let private decodeCatalog (node: JsonNode) : Catalog =
    node.AsObject().["databases"].AsObject()
    |> Seq.map (fun dbEntry ->
        let db = dbEntry.Value.AsObject() |> Seq.map (fun t -> t.Key, decodeTable t.Value) |> Map.ofSeq
        dbEntry.Key, db)
    |> Map.ofSeq

/// Snapshots `store`'s whole catalog to `dataDir/snapshot.fsdb` (tmp file +
/// atomic rename) and truncates the WAL back to empty. Safe to call any
/// time — holds `store.Lock` for the duration, same as every other write.
let snapshotNow (dataDir: string) (store: Store) : unit =
    lock store.Lock (fun () ->
        Directory.CreateDirectory dataDir |> ignore
        let finalPath = Path.Combine(dataDir, snapshotFileName)
        let tmpPath = finalPath + ".tmp"
        File.WriteAllText(tmpPath, (encodeCatalog store.Catalog).ToJsonString())
        File.Move(tmpPath, finalPath, true)
        File.WriteAllText(Path.Combine(dataDir, walFileName), ""))

/// Loads durable state from `dataDir` into a fresh `Store`: the snapshot if
/// one exists, then any WAL entries written after it. Call once at startup,
/// before `attach` subscribes the result to further writes. An empty/
/// nonexistent `dataDir` loads the same empty `Store` a plain `Storage.create
/// ()` would.
let load (dataDir: string) : Store =
    let store = Storage.create ()
    Directory.CreateDirectory dataDir |> ignore
    let snapshotPath = Path.Combine(dataDir, snapshotFileName)

    if File.Exists snapshotPath then
        store.Catalog <- decodeCatalog (JsonNode.Parse(File.ReadAllText snapshotPath))

    replayWal store (Path.Combine(dataDir, walFileName))
    store

/// Subscribes `store` to `Storage.Store.OnCommit`, appending every commit as
/// one JSON line to `dataDir/wal.jsonl`, `Flush`ed (with an fsync) before
/// the write that triggered it returns — durability means fsync-before-ack.
/// Opens, writes and closes the file fresh per line rather than holding one
/// long-lived handle: `snapshotNow` (called both below, on rotation, and by
/// any other caller) truncates this same file by path, and a cached
/// `FileStream`'s internal position doesn't know that happened — its next
/// write would land at its old (now-stale) offset, zero-filling the gap in
/// between. Opening fresh every time always sees the file's true current
/// end. ponytail: one open + `fsync` per commit, no group-commit batching or
/// handle reuse; add a batching knob if write throughput ever needs it.
///
/// Auto-rotates (snapshot + WAL truncate, via `snapshotNow`) once the WAL
/// crosses `walRotateBytes`/`walRotateEntries`, and does one last rotation
/// when the process gets a SIGTERM/SIGINT so a graceful shutdown always
/// leaves a fresh snapshot behind (a `kill -9` skips this — replaying the
/// WAL from the last snapshot is what that's for).
let attach (dataDir: string) (store: Store) : unit =
    Directory.CreateDirectory dataDir |> ignore
    let walPath = Path.Combine(dataDir, walFileName)
    let entryCount = ref 0

    let appendLine (line: string) =
        let walSize =
            use s = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read)
            let bytes = Text.Encoding.UTF8.GetBytes(line + "\n")
            s.Write(bytes, 0, bytes.Length)
            s.Flush true
            s.Length

        entryCount := !entryCount + 1

        if walSize > walRotateBytes || !entryCount > walRotateEntries then
            snapshotNow dataDir store
            entryCount := 0

    store.OnCommit <- Some(fun event -> appendLine ((encodeEvent event).ToJsonString()))

    let onShutdown (_: PosixSignalContext) = snapshotNow dataDir store

    PosixSignalRegistration.Create(PosixSignal.SIGTERM, onShutdown) |> ignore
    PosixSignalRegistration.Create(PosixSignal.SIGINT, onShutdown) |> ignore
