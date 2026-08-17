/// Opt-in durability (`--data-dir`): WAL + snapshot, replay on startup.
/// `Db.withDataDir` is the door in: `load` rebuilds a
/// `Store` from whatever's on disk, `attach` subscribes it (via
/// `Storage.Store.OnCommit`) to keep writing.
///
/// Row-level events (`RowsInserted`/`RowsUpdated`/`RowsDeleted`) already
/// carry physically-evaluated `Value[]`s — see `Storage.CommitEvent` — so
/// replaying them is "write exactly these values back", never "re-run an
/// expression" (the whole point: `INSERT ... VALUES (NOW(), UUID())`
/// replays to the *same* row, not a freshly-evaluated one).
///
/// Both halves of persistence are binary. The WAL is `[len][crc32][payload]`
/// records over a `CommitEvent` payload; the snapshot is a self-delimiting
/// tree (`db count` → tables → rows) over the same codecs. Everything encodes
/// through `Binary.Writer`/`Value.encodeValue` — no JSON anywhere. Replay
/// calls `Storage`'s own DDL functions directly (`Executor.execute` isn't
/// reachable — `Executor.fs` compiles *after* this file).
module Fsdb.Persistence

open System
open System.Collections.Immutable
open System.IO
open System.Runtime.InteropServices
open Fsdb.Ast
open Fsdb.Binary
open Fsdb.Value
open Fsdb.Storage

let private walFileName = "wal.bin"
let private snapshotFileName = "snapshot.fsdb"

/// `PosixSignalRegistration.Create` returns a disposable that unregisters
/// its handler when finalized — `attach`'s SIGTERM/SIGINT registrations
/// have to stay reachable for the process's whole lifetime, or the first GC
/// silently stops the shutdown snapshot from firing. See `attach`.
let private shutdownRegistrations = ResizeArray<IDisposable>()

/// Once the WAL crosses this many bytes, or this many appended entries,
/// whichever comes first, `attach`'s subscriber snapshots the whole catalog
/// and truncates it — keeps startup replay bounded instead of an
/// ever-growing WAL. ponytail: fixed constants, not a knob; promote to a
/// `Db.withDataDir` parameter if a real deployment ever needs a different
/// rotation size.
let private walRotateBytes = 64L * 1024L * 1024L
let private walRotateEntries = 100_000

[<DllImport("libc", SetLastError = true)>]
extern int private fsync(int fd)

/// fsync-before-ack without .NET's `Flush(true)`: on macOS `FileStream.Flush(true)`
/// issues `fcntl(fd, F_FULLFSYNC)` — a full drive-write-cache flush costing
/// ~5 ms per call here — while plain `fsync` is ~16 us. MySQL's default
/// `innodb_flush_method` on macOS is plain `fsync`, so this matches its
/// durability semantics exactly: a write survives an OS crash, not a power
/// loss that also drops the drive's write cache. `Flush(false)` pushes the
/// .NET buffer out first; the raw `fsync` then orders it.
let private flushToDisk (s: FileStream) : unit =
    s.Flush false
    fsync(s.SafeFileHandle.DangerousGetHandle().ToInt32()) |> ignore

// ---------------------------------------------------------------------
// Binary codecs: each DU encodes as a tag byte + its fields, written with
// `Binary.Writer`; `decodeXxx` is each `encodeXxx`'s exact inverse. Options
// are presence-flagged, lists are length-prefixed, so every record is
// self-delimiting and a multi-GB snapshot needs no length table.
// ---------------------------------------------------------------------

let private writeBool (w: Writer) (b: bool) = w.WriteByte(if b then 1uy else 0uy)

let private readBool (r: Reader) : bool = r.ReadByte() = 1uy

let private writeStr (w: Writer) (s: string) = w.WriteLenEncString s

let private readStr (r: Reader) : string = r.ReadLenEncString() |> Option.defaultValue ""

let private writeOptStr (w: Writer) (s: string option) =
    match s with
    | None -> w.WriteByte 0uy
    | Some x ->
        w.WriteByte 1uy
        writeStr w x

let private readOptStr (r: Reader) : string option =
    match r.ReadByte() with
    | 0uy -> None
    | _ -> Some(readStr r)

let private writeStrList (w: Writer) (xs: string list) =
    w.WriteInt32LE(List.length xs)
    List.iter (writeStr w) xs

let private readStrList (r: Reader) : string list =
    List.init (r.ReadInt32LE()) (fun _ -> readStr r)

// ---------------------------------------------------------------------
// `Ast.ColumnType`
// ---------------------------------------------------------------------

let private encodeColumnType (w: Writer) (t: ColumnType) : unit =
    match t with
    | TTinyInt u -> w.WriteByte 0x01uy; writeBool w u
    | TSmallInt u -> w.WriteByte 0x02uy; writeBool w u
    | TMediumInt u -> w.WriteByte 0x03uy; writeBool w u
    | TInt u -> w.WriteByte 0x04uy; writeBool w u
    | TBigInt u -> w.WriteByte 0x05uy; writeBool w u
    | TChar l -> w.WriteByte 0x06uy; w.WriteInt32LE l
    | TVarchar l -> w.WriteByte 0x07uy; w.WriteInt32LE l
    | TTinyText -> w.WriteByte 0x08uy
    | TText -> w.WriteByte 0x09uy
    | TMediumText -> w.WriteByte 0x0Auy
    | TLongText -> w.WriteByte 0x0Buy
    | TBinary l -> w.WriteByte 0x0Cuy; w.WriteInt32LE l
    | TVarBinary l -> w.WriteByte 0x0Duy; w.WriteInt32LE l
    | TTinyBlob -> w.WriteByte 0x0Euy
    | TBlob -> w.WriteByte 0x0Fuy
    | TMediumBlob -> w.WriteByte 0x10uy
    | TLongBlob -> w.WriteByte 0x11uy
    | TEnum values -> w.WriteByte 0x12uy; writeStrList w values
    | TSet values -> w.WriteByte 0x13uy; writeStrList w values
    | TDecimal(p, s) -> w.WriteByte 0x14uy; w.WriteInt32LE p; w.WriteInt32LE s
    | TDouble -> w.WriteByte 0x15uy
    | TFloat -> w.WriteByte 0x16uy
    | TDate -> w.WriteByte 0x17uy
    | TDateTime -> w.WriteByte 0x18uy
    | TTimestamp -> w.WriteByte 0x19uy
    | TTime -> w.WriteByte 0x1Auy
    | TYear -> w.WriteByte 0x1Buy
    | TJson -> w.WriteByte 0x1Cuy

let private decodeColumnType (r: Reader) : ColumnType =
    match r.ReadByte() with
    | 0x01uy -> TTinyInt(readBool r)
    | 0x02uy -> TSmallInt(readBool r)
    | 0x03uy -> TMediumInt(readBool r)
    | 0x04uy -> TInt(readBool r)
    | 0x05uy -> TBigInt(readBool r)
    | 0x06uy -> TChar(r.ReadInt32LE())
    | 0x07uy -> TVarchar(r.ReadInt32LE())
    | 0x08uy -> TTinyText
    | 0x09uy -> TText
    | 0x0Auy -> TMediumText
    | 0x0Buy -> TLongText
    | 0x0Cuy -> TBinary(r.ReadInt32LE())
    | 0x0Duy -> TVarBinary(r.ReadInt32LE())
    | 0x0Euy -> TTinyBlob
    | 0x0Fuy -> TBlob
    | 0x10uy -> TMediumBlob
    | 0x11uy -> TLongBlob
    | 0x12uy -> TEnum(readStrList r)
    | 0x13uy -> TSet(readStrList r)
    | 0x14uy -> TDecimal(r.ReadInt32LE(), r.ReadInt32LE())
    | 0x15uy -> TDouble
    | 0x16uy -> TFloat
    | 0x17uy -> TDate
    | 0x18uy -> TDateTime
    | 0x19uy -> TTimestamp
    | 0x1Auy -> TTime
    | 0x1Buy -> TYear
    | 0x1Cuy -> TJson
    | tag -> failwithf "Persistence: unknown ColumnType tag 0x%02x in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Ast.Expr` — the only place one needs to survive the WAL/snapshot at all
// is `ColumnDef.Generated` (`GENERATED ALWAYS AS (expr)`), and MySQL itself
// rejects a subquery there, so `InSubquery`/`Exists`/`Subquery` fail loudly
// rather than needing a `SelectStmt` encoder too — a real migration can't
// produce one here to lose in the first place.
// ---------------------------------------------------------------------

let private encodeOp (w: Writer) (op: Op) : unit =
    w.WriteByte(
        match op with
        | And -> 0x01uy
        | Or -> 0x02uy
        | Eq -> 0x03uy
        | Neq -> 0x04uy
        | Lt -> 0x05uy
        | Lte -> 0x06uy
        | Gt -> 0x07uy
        | Gte -> 0x08uy
        | Add -> 0x09uy
        | Sub -> 0x0Auy
        | Mul -> 0x0Buy
        | Div -> 0x0Cuy
        | IntDiv -> 0x0Duy
        | NullSafeEq -> 0x0Euy
    )

let private decodeOp (r: Reader) : Op =
    match r.ReadByte() with
    | 0x01uy -> And
    | 0x02uy -> Or
    | 0x03uy -> Eq
    | 0x04uy -> Neq
    | 0x05uy -> Lt
    | 0x06uy -> Lte
    | 0x07uy -> Gt
    | 0x08uy -> Gte
    | 0x09uy -> Add
    | 0x0Auy -> Sub
    | 0x0Buy -> Mul
    | 0x0Cuy -> Div
    | 0x0Duy -> IntDiv
    | 0x0Euy -> NullSafeEq
    | tag -> failwithf "Persistence: unknown Op tag 0x%02x in WAL/snapshot" tag

let private encodeDirection (w: Writer) (d: Direction) : unit =
    w.WriteByte(match d with Asc -> 0x00uy | Desc -> 0x01uy)

let private decodeDirection (r: Reader) : Direction =
    match r.ReadByte() with
    | 0x00uy -> Asc
    | _ -> Desc

let rec private encodeExpr (w: Writer) (expr: Expr) : unit =
    match expr with
    | Lit v -> w.WriteByte 0x01uy; encodeValue w v
    | Col name -> w.WriteByte 0x02uy; writeStr w name
    | QualifiedCol(t, c) -> w.WriteByte 0x03uy; writeStr w t; writeStr w c
    | BinOp(op, a, b) -> w.WriteByte 0x04uy; encodeOp w op; encodeExpr w a; encodeExpr w b
    | Not e -> w.WriteByte 0x05uy; encodeExpr w e
    | IsNull e -> w.WriteByte 0x06uy; encodeExpr w e
    | IsNotNull e -> w.WriteByte 0x07uy; encodeExpr w e
    | IsTrue e -> w.WriteByte 0x08uy; encodeExpr w e
    | IsFalse e -> w.WriteByte 0x09uy; encodeExpr w e
    | Like(e, p, cs, esc) ->
        w.WriteByte 0x0Auy
        encodeExpr w e
        encodeExpr w p
        writeBool w cs

        match esc with
        | None -> w.WriteByte 0uy
        | Some c ->
            w.WriteByte 1uy
            w.WriteByte(byte c)
    | Regexp(e, p) -> w.WriteByte 0x0Buy; encodeExpr w e; encodeExpr w p
    | In(e, xs) ->
        w.WriteByte 0x0Cuy
        encodeExpr w e
        w.WriteInt32LE(List.length xs)
        List.iter (encodeExpr w) xs
    | Between(e, lo, hi) -> w.WriteByte 0x0Duy; encodeExpr w e; encodeExpr w lo; encodeExpr w hi
    | FuncCall(name, args) ->
        w.WriteByte 0x0Euy
        writeStr w name
        w.WriteInt32LE(List.length args)
        List.iter (encodeExpr w) args
    | RowNumberOver(partitionBy, orderBy) ->
        w.WriteByte 0x0Fuy
        w.WriteInt32LE(List.length partitionBy)
        List.iter (encodeExpr w) partitionBy
        w.WriteInt32LE(List.length orderBy)
        List.iter (fun (e, d) -> encodeExpr w e; encodeDirection w d) orderBy
    | LagOver(e, offset, partitionBy, orderBy) ->
        w.WriteByte 0x10uy
        encodeExpr w e
        w.WriteInt64LE offset
        w.WriteInt32LE(List.length partitionBy)
        List.iter (encodeExpr w) partitionBy
        w.WriteInt32LE(List.length orderBy)
        List.iter (fun (e, d) -> encodeExpr w e; encodeDirection w d) orderBy
    | Distinct e -> w.WriteByte 0x11uy; encodeExpr w e
    | OrderBy(e, d) -> w.WriteByte 0x12uy; encodeExpr w e; encodeDirection w d
    | Cast(e, t) -> w.WriteByte 0x13uy; encodeExpr w e; encodeColumnType w t
    | Collate(e, name) -> w.WriteByte 0x14uy; encodeExpr w e; writeStr w name
    | Star q -> w.WriteByte 0x15uy; writeOptStr w q
    | Case(subject, whens, elseBranch) ->
        w.WriteByte 0x16uy

        match subject with
        | None -> w.WriteByte 0uy
        | Some s ->
            w.WriteByte 1uy
            encodeExpr w s

        w.WriteInt32LE(List.length whens)
        List.iter (fun (c, r) -> encodeExpr w c; encodeExpr w r) whens

        match elseBranch with
        | None -> w.WriteByte 0uy
        | Some e ->
            w.WriteByte 1uy
            encodeExpr w e
    | InSubquery _
    | Exists _
    | Subquery _ -> failwithf "Persistence: a GENERATED column can't hold a subquery (MySQL itself rejects one there)"

let rec private decodeExpr (r: Reader) : Expr =
    let optExpr () =
        match r.ReadByte() with
        | 0uy -> None
        | _ -> Some(decodeExpr r)

    let exprList () = List.init (r.ReadInt32LE()) (fun _ -> decodeExpr r)

    let orderByList () = List.init (r.ReadInt32LE()) (fun _ -> decodeExpr r, decodeDirection r)

    match r.ReadByte() with
    | 0x01uy -> Lit(decodeValue r)
    | 0x02uy -> Col(readStr r)
    | 0x03uy -> QualifiedCol(readStr r, readStr r)
    | 0x04uy -> BinOp(decodeOp r, decodeExpr r, decodeExpr r)
    | 0x05uy -> Not(decodeExpr r)
    | 0x06uy -> IsNull(decodeExpr r)
    | 0x07uy -> IsNotNull(decodeExpr r)
    | 0x08uy -> IsTrue(decodeExpr r)
    | 0x09uy -> IsFalse(decodeExpr r)
    | 0x0Auy ->
        let e = decodeExpr r
        let p = decodeExpr r
        let cs = readBool r
        let esc = match r.ReadByte() with 0uy -> None | _ -> Some(char (r.ReadByte()))
        Like(e, p, cs, esc)
    | 0x0Buy -> Regexp(decodeExpr r, decodeExpr r)
    | 0x0Cuy -> In(decodeExpr r, exprList ())
    | 0x0Duy -> Between(decodeExpr r, decodeExpr r, decodeExpr r)
    | 0x0Euy -> FuncCall(readStr r, exprList ())
    | 0x0Fuy -> RowNumberOver(exprList (), orderByList ())
    | 0x10uy ->
        let e = decodeExpr r
        let offset = r.ReadInt64LE()
        LagOver(e, offset, exprList (), orderByList ())
    | 0x11uy -> Distinct(decodeExpr r)
    | 0x12uy -> OrderBy(decodeExpr r, decodeDirection r)
    | 0x13uy -> Cast(decodeExpr r, decodeColumnType r)
    | 0x14uy -> Collate(decodeExpr r, readStr r)
    | 0x15uy -> Star(readOptStr r)
    | 0x16uy ->
        let subject = optExpr ()
        let whens = List.init (r.ReadInt32LE()) (fun _ -> decodeExpr r, decodeExpr r)
        let elseBranch = optExpr ()
        Case(subject, whens, elseBranch)
    | tag -> failwithf "Persistence: unknown Expr tag 0x%02x in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Ast.ColumnDefault` / `ColumnDef` / `IndexDef` / `ForeignKeyDef`
// ---------------------------------------------------------------------

let private encodeColumnDefault (w: Writer) (d: ColumnDefault) : unit =
    match d with
    | DConst v -> w.WriteByte 0x01uy; encodeValue w v
    | DCurrentTimestamp -> w.WriteByte 0x02uy

let private decodeColumnDefault (r: Reader) : ColumnDefault =
    match r.ReadByte() with
    | 0x01uy -> DConst(decodeValue r)
    | _ -> DCurrentTimestamp

/// `ColumnDef.Generated` (`GENERATED ALWAYS AS (expr)`) round-trips through
/// the `Expr` codec above — without it, a generated column silently stopped
/// being computed after a restart (new rows got `NULL` where MySQL computes
/// a value; Laravel Pulse's `key_hash ... AS (unhex(md5(key)))` is exactly
/// this shape).
let private encodeColumnDef (w: Writer) (c: ColumnDef) : unit =
    writeStr w c.Name
    encodeColumnType w c.Type
    writeBool w c.Nullable

    match c.Default with
    | None -> w.WriteByte 0uy
    | Some d ->
        w.WriteByte 1uy
        encodeColumnDefault w d

    writeBool w c.AutoIncrement
    writeBool w c.PrimaryKey
    writeBool w c.Unique

    match c.Generated with
    | None -> w.WriteByte 0uy
    | Some g ->
        w.WriteByte 1uy
        encodeExpr w g

    writeOptStr w c.Collation
    writeOptStr w c.Charset

let private decodeColumnDef (r: Reader) : ColumnDef =
    { Name = readStr r
      Type = decodeColumnType r
      Nullable = readBool r
      Default = (match r.ReadByte() with 0uy -> None | _ -> Some(decodeColumnDefault r))
      AutoIncrement = readBool r
      PrimaryKey = readBool r
      Unique = readBool r
      Generated = (match r.ReadByte() with 0uy -> None | _ -> Some(decodeExpr r))
      Collation = readOptStr r
      Charset = readOptStr r }

let private encodeIndexDef (w: Writer) (ix: IndexDef) : unit =
    writeStr w ix.Name
    writeStrList w ix.Columns
    writeBool w ix.Unique

let private decodeIndexDef (r: Reader) : IndexDef =
    { Name = readStr r; Columns = readStrList r; Unique = readBool r }

let private encodeForeignKeyDef (w: Writer) (fk: ForeignKeyDef) : unit =
    writeStr w fk.Name
    writeStrList w fk.Columns
    writeStr w fk.RefTable
    writeStrList w fk.RefColumns
    writeOptStr w fk.OnDelete
    writeOptStr w fk.OnUpdate

let private decodeForeignKeyDef (r: Reader) : ForeignKeyDef =
    { Name = readStr r
      Columns = readStrList r
      RefTable = readStr r
      RefColumns = readStrList r
      OnDelete = readOptStr r
      OnUpdate = readOptStr r }

// ---------------------------------------------------------------------
// `Ast.AlterAction` / `Ast.Statement` — only the DDL shapes `SchemaChanged`
// ever wraps (see `Storage`'s every `emit (Some(SchemaChanged ...)))` call);
// any other `Statement` case reaching here would be a `Storage` bug, not a
// WAL-format one, so it's a hard `failwithf` rather than a silent skip.
// ---------------------------------------------------------------------

let private encodeColumnPosition (w: Writer) (p: ColumnPosition) : unit =
    match p with
    | PositionDefault -> w.WriteByte 0x01uy
    | PositionFirst -> w.WriteByte 0x02uy
    | PositionAfter column -> w.WriteByte 0x03uy; writeStr w column

let private decodeColumnPosition (r: Reader) : ColumnPosition =
    match r.ReadByte() with
    | 0x01uy -> PositionDefault
    | 0x02uy -> PositionFirst
    | _ -> PositionAfter(readStr r)

let private encodeAlterAction (w: Writer) (a: AlterAction) : unit =
    match a with
    | AddColumn(c, position) -> w.WriteByte 0x01uy; encodeColumnDef w c; encodeColumnPosition w position
    | DropColumn name -> w.WriteByte 0x02uy; writeStr w name
    | ModifyColumn(c, position) -> w.WriteByte 0x03uy; encodeColumnDef w c; encodeColumnPosition w position
    | ChangeColumn(oldName, c, position) -> w.WriteByte 0x04uy; writeStr w oldName; encodeColumnDef w c; encodeColumnPosition w position
    | RenameTo name -> w.WriteByte 0x05uy; writeStr w name
    | RenameColumnTo(oldName, newName) -> w.WriteByte 0x06uy; writeStr w oldName; writeStr w newName
    | AddIndex ix -> w.WriteByte 0x07uy; encodeIndexDef w ix
    | DropIndexAction name -> w.WriteByte 0x08uy; writeStr w name
    | AddForeignKey fk -> w.WriteByte 0x09uy; encodeForeignKeyDef w fk
    | DropForeignKey name -> w.WriteByte 0x0Auy; writeStr w name
    | AddPrimaryKey columns -> w.WriteByte 0x0Buy; writeStrList w columns

let private decodeAlterAction (r: Reader) : AlterAction =
    match r.ReadByte() with
    | 0x01uy -> AddColumn(decodeColumnDef r, decodeColumnPosition r)
    | 0x02uy -> DropColumn(readStr r)
    | 0x03uy -> ModifyColumn(decodeColumnDef r, decodeColumnPosition r)
    | 0x04uy -> ChangeColumn(readStr r, decodeColumnDef r, decodeColumnPosition r)
    | 0x05uy -> RenameTo(readStr r)
    | 0x06uy -> RenameColumnTo(readStr r, readStr r)
    | 0x07uy -> AddIndex(decodeIndexDef r)
    | 0x08uy -> DropIndexAction(readStr r)
    | 0x09uy -> AddForeignKey(decodeForeignKeyDef r)
    | 0x0Auy -> DropForeignKey(readStr r)
    | _ -> AddPrimaryKey(readStrList r)

let private encodeStatement (w: Writer) (s: Statement) : unit =
    match s with
    | CreateDatabase(name, ifNotExists) -> w.WriteByte 0x01uy; writeStr w name; writeBool w ifNotExists
    | DropDatabase(name, ifExists) -> w.WriteByte 0x02uy; writeStr w name; writeBool w ifExists
    | CreateTable(name, columns, indexes, fks, ifNotExists, tableCharset, tableCollation) ->
        w.WriteByte 0x03uy
        writeStr w name
        w.WriteInt32LE(List.length columns)
        List.iter (encodeColumnDef w) columns
        w.WriteInt32LE(List.length indexes)
        List.iter (encodeIndexDef w) indexes
        w.WriteInt32LE(List.length fks)
        List.iter (encodeForeignKeyDef w) fks
        writeBool w ifNotExists
        writeOptStr w tableCharset
        writeOptStr w tableCollation
    | DropTable(names, ifExists) -> w.WriteByte 0x04uy; writeStrList w names; writeBool w ifExists
    | AlterTable(table, actions) ->
        w.WriteByte 0x05uy
        writeStr w table
        w.WriteInt32LE(List.length actions)
        List.iter (encodeAlterAction w) actions
    | RenameTable pairs ->
        w.WriteByte 0x06uy
        w.WriteInt32LE(List.length pairs)
        List.iter (fun (a, b) -> writeStr w a; writeStr w b) pairs
    | CreateIndex(name, table, columns, unique) -> w.WriteByte 0x07uy; writeStr w name; writeStr w table; writeStrList w columns; writeBool w unique
    | DropIndexStmt(name, table, ifExists) -> w.WriteByte 0x08uy; writeStr w name; writeStr w table; writeBool w ifExists
    | Truncate table -> w.WriteByte 0x09uy; writeStr w table
    | other -> failwithf "Persistence: %A isn't a DDL statement SchemaChanged should ever carry" other

let private decodeStatement (r: Reader) : Statement =
    match r.ReadByte() with
    | 0x01uy -> CreateDatabase(readStr r, readBool r)
    | 0x02uy -> DropDatabase(readStr r, readBool r)
    | 0x03uy ->
        let name = readStr r
        let columns = List.init (r.ReadInt32LE()) (fun _ -> decodeColumnDef r)
        let indexes = List.init (r.ReadInt32LE()) (fun _ -> decodeIndexDef r)
        let fks = List.init (r.ReadInt32LE()) (fun _ -> decodeForeignKeyDef r)
        let ifNotExists = readBool r
        let tableCharset = readOptStr r
        let tableCollation = readOptStr r
        CreateTable(name, columns, indexes, fks, ifNotExists, tableCharset, tableCollation)
    | 0x04uy -> DropTable(readStrList r, readBool r)
    | 0x05uy -> AlterTable(readStr r, List.init (r.ReadInt32LE()) (fun _ -> decodeAlterAction r))
    | 0x06uy -> RenameTable(List.init (r.ReadInt32LE()) (fun _ -> readStr r, readStr r))
    | 0x07uy -> CreateIndex(readStr r, readStr r, readStrList r, readBool r)
    | 0x08uy -> DropIndexStmt(readStr r, readStr r, readBool r)
    | 0x09uy -> Truncate(readStr r)
    | tag -> failwithf "Persistence: unknown Statement tag 0x%02x in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Storage.CommitEvent` — one WAL record each, binary. The [len][crc][payload]
// framing around the payload lives in `attach` and `replayWal`.
// ---------------------------------------------------------------------

let private KindRowsInserted = 0x01uy
let private KindRowsUpdated = 0x02uy
let private KindRowsDeleted = 0x03uy
let private KindSchemaChanged = 0x04uy
let private KindTransactionCommitted = 0x05uy

let private encodeRowBin (w: Writer) (row: Value[]) : unit =
    w.WriteInt32LE row.Length

    for v in row do
        encodeValue w v

let private decodeRowBin (r: Reader) : Value[] =
    Array.init (r.ReadInt32LE()) (fun _ -> decodeValue r)

let rec private encodeEvent (w: Writer) (event: CommitEvent) : unit =
    match event with
    | RowsInserted(db, table, rows) ->
        w.WriteByte KindRowsInserted
        w.WriteLenEncString db
        w.WriteLenEncString table
        w.WriteInt32LE (List.length rows)

        for row in rows do
            encodeRowBin w row
    | RowsUpdated(db, table, changes) ->
        w.WriteByte KindRowsUpdated
        w.WriteLenEncString db
        w.WriteLenEncString table
        w.WriteInt32LE (List.length changes)

        for before, after in changes do
            encodeRowBin w before
            encodeRowBin w after
    | RowsDeleted(db, table, rows) ->
        w.WriteByte KindRowsDeleted
        w.WriteLenEncString db
        w.WriteLenEncString table
        w.WriteInt32LE (List.length rows)

        for row in rows do
            encodeRowBin w row
    | SchemaChanged(db, stmt) ->
        w.WriteByte KindSchemaChanged
        w.WriteLenEncString db
        encodeStatement w stmt
    | TransactionCommitted events ->
        w.WriteByte KindTransactionCommitted
        w.WriteInt32LE (List.length events)

        for e in events do
            encodeEvent w e

let rec private decodeEvent (r: Reader) : CommitEvent =
    let str () = r.ReadLenEncString() |> Option.defaultValue ""

    match r.ReadByte() with
    | k when k = KindRowsInserted ->
        let db = str ()
        let table = str ()
        let rows = List.init (r.ReadInt32LE()) (fun _ -> decodeRowBin r)
        RowsInserted(db, table, rows)
    | k when k = KindRowsUpdated ->
        let db = str ()
        let table = str ()
        let changes = List.init (r.ReadInt32LE()) (fun _ -> decodeRowBin r, decodeRowBin r)
        RowsUpdated(db, table, changes)
    | k when k = KindRowsDeleted ->
        let db = str ()
        let table = str ()
        let rows = List.init (r.ReadInt32LE()) (fun _ -> decodeRowBin r)
        RowsDeleted(db, table, rows)
    | k when k = KindSchemaChanged ->
        let db = str ()
        SchemaChanged(db, decodeStatement r)
    | k when k = KindTransactionCommitted ->
        TransactionCommitted(List.init (r.ReadInt32LE()) (fun _ -> decodeEvent r))
    | tag -> failwithf "Persistence: unknown WAL event kind 0x%02x" tag

/// One framed WAL record: `[int32 payload length][uint32 crc32][payload]`.
/// Public so the tests can hand-build WAL files (and torn tails) without
/// reimplementing the framing.
let encodeWalRecord (event: CommitEvent) : byte[] =
    let payloadWriter = Writer()
    encodeEvent payloadWriter event
    let payload = payloadWriter.ToArray()

    let frame = Writer()
    frame.WriteInt32LE payload.Length
    frame.WriteUInt32LE(crc32 payload)
    frame.WriteBytes payload
    frame.ToArray()

// ---------------------------------------------------------------------
// Replay: applies a decoded `CommitEvent` to a live `Store` via `Storage`'s
// own public write functions — row events write the exact physical values
// back (see the module doc), DDL events redo the same `Storage` call the
// original statement made.
// ---------------------------------------------------------------------

let private warn (context: string) (result: Result<'a, StorageError>) : unit =
    match result with
    | Ok _ -> ()
    | Error e -> Log.diagnostic "fsdb: WAL replay warning (%s): %A" context e

let private applyDdl (store: Store) (db: string) (stmt: Statement) : unit =
    match stmt with
    | CreateDatabase(name, _) -> warn "CreateDatabase" (createDatabase store name)
    | DropDatabase(name, _) -> warn "DropDatabase" (dropDatabase store name)
    | CreateTable(name, columns, indexes, fks, _, tableCharset, tableCollation) ->
        warn "CreateTable" (createTable store db name columns indexes fks tableCharset tableCollation)
    | DropTable(names, _) -> names |> List.iter (fun n -> warn "DropTable" (dropTable store db n))
    | AlterTable(table, actions) -> warn "AlterTable" (alterTable store db table actions)
    | RenameTable pairs -> pairs |> List.iter (fun (oldName, newName) -> warn "RenameTable" (renameTable store db oldName newName))
    | CreateIndex(name, table, columns, unique) ->
        warn "CreateIndex" (alterTable store db table [ AddIndex { Name = name; Columns = columns; Unique = unique } ])
    | DropIndexStmt(name, table, _) -> warn "DropIndexStmt" (alterTable store db table [ DropIndexAction name ])
    | Truncate table -> warn "Truncate" (truncate store db table)
    | other -> Log.diagnostic "fsdb: WAL replay warning (SchemaChanged): unexpected statement %A" other

/// Applies `changes` — `(before, after)` pairs in the same ascending
/// original-row order `Storage.updateRows` emitted them, one entry per
/// physically distinct row it touched — to `rows` in a single forward pass:
/// walk both lists together, consuming the next change only once it's due
/// (its `before` matches the row in hand). Never re-scans from the top of
/// `rows` per change, so a change's `after` value can't be picked back up
/// as a later change's `before` — the cascade a naive "find any row equal
/// to `before`, replace it" replay hits on `UPDATE t SET n = n + 1` (every
/// row's `after` equals the next row's `before`). Any two rows the pass
/// can't tell apart are byte-identical, so it never matters which one
/// consumes which change.
let private applyRowChanges (changes: (Value[] * Value[]) list) (rows: Value[] list) : Value[] list =
    let rec loop acc rows changes =
        match rows, changes with
        | [], _ -> List.rev acc
        | row :: restRows, (before, after) :: restChanges when row = before -> loop (after :: acc) restRows restChanges
        | row :: restRows, _ -> loop (row :: acc) restRows changes

    loop [] rows changes

/// As `applyRowChanges`, for `RowsDeleted`'s logged rows: drops the next
/// not-yet-consumed match for each logged row, in order — fixes replaying a
/// partial delete over duplicate-valued rows (`DELETE ... LIMIT 1` over two
/// identical rows) wiping every row equal to the target instead of just the
/// one that was actually removed.
let private applyRowDeletes (targets: Value[] list) (rows: Value[] list) : Value[] list =
    let rec loop acc rows targets =
        match rows, targets with
        | [], _ -> List.rev acc
        | row :: restRows, target :: restTargets when row = target -> loop acc restRows restTargets
        | row :: restRows, _ -> loop (row :: acc) restRows targets

    loop [] rows targets

/// Rewrites `dbName.tableName`'s `Rows` in `store.Catalog` directly with
/// `f`, bypassing `Storage.updateRows`/`deleteRows` entirely. Replay is a
/// physical log of rows that already passed every check once, at commit
/// time — routing it back through those checked write paths is what causes
/// the cascade/over-delete bugs `applyRowChanges`/`applyRowDeletes` fix (a
/// value-equality predicate scanning the *whole* table can't express "this
/// one physical row"), and would also re-run FK/unique validation that a
/// `SET FOREIGN_KEY_CHECKS = 0` write may have deliberately skipped.
///
/// Deliberately leaves `UniqueIndex` stale rather than calling
/// `reindexTable` here — nothing reads it mid-replay (FK checks are off,
/// see `load`), so paying its full-table-rescan cost once per *event*
/// would be pure waste for a table with many updates/deletes in the WAL.
/// `replayWal`'s caller reindexes every table exactly once, after the last
/// event has been applied.
let private mapTableRows (store: Store) (dbName: string) (tableName: string) (f: Value[] list -> Value[] list) : unit =
    replaceTablesForReplay store dbName tableName f (Log.diagnostic "fsdb: WAL replay warning: %s")

let rec private applyEvent (store: Store) (event: CommitEvent) : unit =
    match event with
    | RowsInserted(db, table, rows) ->
        if not rows.IsEmpty then
            warn "RowsInserted" (insertRows store db table None (rows |> List.map List.ofArray) |> Result.map ignore)
    | RowsUpdated(db, table, changes) -> mapTableRows store db table (applyRowChanges changes)
    | RowsDeleted(db, table, rows) -> mapTableRows store db table (applyRowDeletes rows)
    | SchemaChanged(db, stmt) -> applyDdl store db stmt
    | TransactionCommitted events -> events |> List.iter (applyEvent store)

/// Replays every complete record in `walPath` into `store`, returning the
/// byte offset just past the last successfully applied record. A torn final
/// record (a `kill -9` mid-write) is expected, not corruption to panic over —
/// everything before it already committed durably — so replay stops there
/// rather than guessing at what the rest of the record might have meant;
/// `load` truncates the WAL back to the returned offset so the *next*
/// append glues onto a clean record boundary instead of the torn bytes
/// (otherwise every write from then on decodes into one ever-growing
/// corrupt record and is lost again at the next restart). A record whose
/// length overruns the file or whose CRC doesn't match is the torn tail:
/// either way replay stops before it.
let private replayWal (store: Store) (walPath: string) : int64 =
    if not (File.Exists walPath) then
        0L
    else
        let r = Reader(File.ReadAllBytes walPath)
        let mutable offset = 0L
        let mutable stopped = false

        while r.Remaining >= 8 && not stopped do
            let recLen = r.ReadInt32LE()
            let crc = r.ReadUInt32LE()

            if recLen < 0 || recLen > r.Remaining then
                stopped <- true
            else
                let payload = r.ReadBytes recLen

                if crc32 payload <> crc then
                    stopped <- true
                else
                    try
                        applyEvent store (decodeEvent (Reader(payload)))
                        offset <- offset + int64 (8 + recLen)
                    with ex ->
                        Log.diagnostic "fsdb: WAL replay stopped at an unreadable record (%s): %s" walPath ex.Message
                        stopped <- true

        offset

// ---------------------------------------------------------------------
// Snapshot: the whole catalog, binary, written atomically (tmp + rename).
// The codec below is the same tag-byte scheme as the WAL; rows reuse
// `encodeRowBin`/`decodeRowBin` so a snapshot and a WAL share one row format.
// ---------------------------------------------------------------------

let private encodeTable (w: Writer) (t: Table) : unit =
    writeStr w t.OriginalName
    w.WriteInt32LE(List.length t.Columns)
    List.iter (encodeColumnDef w) t.Columns
    w.WriteInt32LE(List.length t.Indexes)
    List.iter (encodeIndexDef w) t.Indexes
    w.WriteInt32LE(List.length t.ForeignKeys)
    List.iter (encodeForeignKeyDef w) t.ForeignKeys
    writeOptStr w t.TableCharset
    writeOptStr w t.TableCollation
    w.WriteInt64LE t.NextAutoId
    w.WriteInt32LE t.RowsArray.Length

    for row in t.RowsArray do
        encodeRowBin w row

let private decodeTable (r: Reader) : Table =
    let originalName = readStr r
    let columns = List.init (r.ReadInt32LE()) (fun _ -> decodeColumnDef r)
    let indexes = List.init (r.ReadInt32LE()) (fun _ -> decodeIndexDef r)
    let fks = List.init (r.ReadInt32LE()) (fun _ -> decodeForeignKeyDef r)
    let tableCharset = readOptStr r
    let tableCollation = readOptStr r
    let nextAutoId = r.ReadInt64LE()
    let rows = List.init (r.ReadInt32LE()) (fun _ -> decodeRowBin r)

    reindexTable
        { OriginalName = originalName
          Columns = columns
          Indexes = indexes
          ForeignKeys = fks
          TableCharset = tableCharset
          TableCollation = tableCollation
          RowsArray = ImmutableArray.CreateRange rows
          NextAutoId = nextAutoId
          UniqueIndex = Map.empty }

let private encodeCatalog (w: Writer) (catalog: Catalog) : unit =
    w.WriteInt32LE(Map.count catalog)

    catalog
    |> Map.iter (fun dbName db ->
        writeStr w dbName
        w.WriteInt32LE(Map.count db)

        db |> Map.iter (fun tableKey table ->
            writeStr w tableKey
            encodeTable w table))

let private decodeCatalog (r: Reader) : Catalog =
    let dbCount = r.ReadInt32LE()

    [ for _ in 1..dbCount ->
          let dbName = readStr r
          let tableCount = r.ReadInt32LE()
          let tables = [ for _ in 1..tableCount -> readStr r, decodeTable r ] |> Map.ofList
          dbName, tables ]
    |> Map.ofList

/// Snapshots `store`'s whole catalog to `dataDir/snapshot.fsdb` and
/// truncates the WAL back to empty. Safe to call any time — holds
/// `store.Lock` for the duration, same as every other write.
///
/// Ordered (and fsynced) so a crash at any point still leaves `load` a
/// consistent state to recover from: write the *whole* catalog to
/// `snapshot.fsdb.new` through a `FileStream` and `flushToDisk` (an fsync,
/// matching `attach`'s WAL writes — `File.WriteAllText` never syncs, so the
/// old tmp-file dance could lose the snapshot to a power loss right after
/// the WAL truncation it's supposed to be a backup for), *then* truncate
/// the WAL, *then* rename `.new` into place. A crash between the fsync and
/// the rename leaves both `.new` (complete) and the old `snapshot.fsdb`
/// (stale but harmless) plus a truncated WAL on disk; `load` prefers `.new`
/// when it's there and parses cleanly. A crash *before* the fsync leaves an
/// incomplete/absent `.new` and the WAL untouched, so `load` falls back to
/// the old snapshot + full WAL replay — nothing lost either way.
let snapshotNow (dataDir: string) (store: Store) : unit =
    lock store.Lock (fun () ->
        Directory.CreateDirectory dataDir |> ignore
        let finalPath = Path.Combine(dataDir, snapshotFileName)
        let newPath = finalPath + ".new"

        (use s = new FileStream(newPath, FileMode.Create, FileAccess.Write)
         let w = Writer()
         encodeCatalog w store.Catalog
         let bytes = w.ToArray()
         s.Write(bytes, 0, bytes.Length)
         flushToDisk s)

        File.WriteAllText(Path.Combine(dataDir, walFileName), "")
        File.Move(newPath, finalPath, true))

/// Loads durable state from `dataDir` into a fresh `Store`: the snapshot if
/// one exists, then any WAL entries written after it. Call once at startup,
/// before `attach` subscribes the result to further writes. An empty/
/// nonexistent `dataDir` loads the same empty `Store` a plain `Storage.create
/// ()` would.
let load (dataDir: string) : Store =
    let store = Storage.create ()
    Directory.CreateDirectory dataDir |> ignore
    let snapshotPath = Path.Combine(dataDir, snapshotFileName)
    let newPath = snapshotPath + ".new"
    let walPath = Path.Combine(dataDir, walFileName)

    // See `snapshotNow`: a fully-written `.new` is a superset of whatever's
    // in the WAL (it's fsynced *before* the WAL is truncated), so prefer it
    // and skip the WAL entirely rather than double-applying what it already
    // has. A `.new` that fails to parse means the crash landed mid-write,
    // before the fsync — it's garbage, not authoritative; fall through to
    // the untouched old snapshot + full WAL instead.
    let loadedFromNew =
        File.Exists newPath
        && (try
                setCatalog store (decodeCatalog (Reader(File.ReadAllBytes newPath)))
                true
            with _ ->
                false)

    if loadedFromNew then
        File.Move(newPath, snapshotPath, true)
        File.WriteAllText(walPath, "")
    else
        if File.Exists snapshotPath then
            setCatalog store (decodeCatalog (Reader(File.ReadAllBytes snapshotPath)))

        // The WAL holds rows that already passed every check once, at
        // commit time — re-validating foreign keys on replay only risks
        // dropping one written under `SET FOREIGN_KEY_CHECKS = 0` (wired up
        // at `QueryHandler.fs`, used by Laravel migrations/seeders), since
        // event order already preserves whatever check *was* enforced.
        store.ForeignKeyChecks <- false
        let goodOffset = replayWal store walPath
        store.ForeignKeyChecks <- true

        // `mapTableRows` (RowsUpdated/RowsDeleted) left `UniqueIndex` stale
        // per-table, not per-event — one rescan per table here instead of
        // one per replayed row-change event.
        reindexAllForReplay store

        // A torn final line (`kill -9` mid-append) must not poison the WAL
        // forever — see `replayWal`'s doc comment.
        if File.Exists walPath && FileInfo(walPath).Length <> goodOffset then
            use fs = new FileStream(walPath, FileMode.Open, FileAccess.Write)
            fs.SetLength goodOffset

    store

/// Subscribes `store` to `Storage.Store.OnCommit`, appending every commit as
/// one framed binary record to `dataDir/wal.bin`, fsynced before the write
/// that triggered it returns — durability means fsync-before-ack. Opens,
/// writes and closes the file fresh per record rather than holding one
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

    let appendRecord (event: CommitEvent) =
        let walSize =
            try
                use s = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read)
                let bytes = encodeWalRecord event
                s.Write(bytes, 0, bytes.Length)
                flushToDisk s
                s.Length
            with ex ->
                // By the time `OnCommit` fires, `Storage` has already
                // applied the mutation to `store.Catalog` (see
                // `Storage.emit`) — a WAL append failing here (ENOSPC,
                // permissions, …) can't be turned into a client-visible
                // error without silently serving a row that exists only in
                // memory, invisible to the very durability mechanism whose
                // job is to keep it. Crash rather than keep serving reads
                // and writes against a catalog the WAL can no longer prove.
                Log.diagnostic "fsdb: WAL append failed, catalog and disk have diverged: %s" ex.Message
                Environment.FailFast(sprintf "fsdb: fatal WAL append failure: %s" ex.Message, ex)
                reraise ()

        entryCount := !entryCount + 1

        if walSize > walRotateBytes || !entryCount > walRotateEntries then
            snapshotNow dataDir store
            entryCount := 0

    store.OnCommit <- Some appendRecord

    let onShutdown (_: PosixSignalContext) = snapshotNow dataDir store

    // `PosixSignalRegistration` unregisters its handler when finalized —
    // `attach` returning right after this would leave nothing holding a
    // reference, so the first GC silently stops the SIGTERM/SIGINT
    // shutdown snapshot from ever firing again. Root them for the process's
    // lifetime instead of `|> ignore`-ing the disposable away.
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, onShutdown))
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, onShutdown))
