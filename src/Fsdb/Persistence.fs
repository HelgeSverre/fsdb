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
open System.IO
open System.Runtime.InteropServices
open Fsdb.Ast
open Fsdb.Binary
open Fsdb.Value
open Fsdb.Storage

let private walFileName = "wal.bin"
let private snapshotFileName = "snapshot.fsdb"

/// 4-byte magic that opens every `snapshot.fsdb`/`.new` — the first line of
/// defense against a torn/zero-filled `.new` being accepted as an empty-but-
/// valid catalog (a truncated file's leading bytes read as zero, which
/// `decodeCatalog` alone happily parses as `dbCount = 0`). Paired with the
/// trailing length+CRC below for the rest of the file.
let private snapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x31uy |] // "FSN1"

/// Trailer written right after the catalog payload: `[int64 payload
/// length][uint32 crc32]`. Same incremental IEEE-802.3 CRC as
/// `Binary.crc32` (reimplemented here, not imported, so it can be folded in
/// one flushed chunk at a time instead of requiring the whole multi-GB
/// payload as one `byte[]` — see `writeCatalog`).
let private snapshotTrailerSize = 12

let private crcTable =
    [| for n in 0..255 ->
           let mutable c = uint32 n

           for _ in 1..8 do
               c <- if (c &&& 1u) <> 0u then (c >>> 1) ^^^ 0xEDB88320u else c >>> 1

           c |]

let private crc32Update (crc: uint32) (data: byte[]) (count: int) : uint32 =
    let mutable c = crc

    for i in 0 .. count - 1 do
        c <- crcTable.[int (c ^^^ uint32 data.[i]) &&& 0xFF] ^^^ (c >>> 8)

    c

/// `PosixSignalRegistration.Create` returns a disposable that unregisters
/// its handler when finalized — `attach`'s SIGTERM/SIGINT registrations
/// have to stay reachable for the process's whole lifetime, or the first GC
/// silently stops the shutdown snapshot from firing. See `attach`.
let private shutdownRegistrations = ResizeArray<IDisposable>()

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
    let rc = fsync(s.SafeFileHandle.DangerousGetHandle().ToInt32())

    if rc <> 0 then
        // A `0` return is the only proof the bytes actually reached disk;
        // silently ignoring EIO/ENOSPC here means every caller above
        // (WAL append, snapshot write) goes on believing a write is durable
        // when it isn't. Same "crash rather than serve an unprovable
        // durability guarantee" call `attach`'s WAL-append failure makes.
        let err = Marshal.GetLastWin32Error()
        Environment.FailFast(sprintf "fsdb: fatal fsync failure (errno %d) — write cannot be confirmed durable" err)

/// Fsyncs `dir` itself so a rename into it (`File.Move .new -> snapshot.fsdb`)
/// survives a crash — a file's own `flushToDisk` only guarantees the file's
/// *bytes*, not that the directory entry pointing at its new name landed.
/// POSIX-only, same as `fsync` above (no Windows guard); best-effort since
/// a directory that fails to `open` here (e.g. already gone) isn't itself
/// an unwritten row to lose sleep over.
[<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
extern int private posixOpen(string path, int flags)

[<DllImport("libc", SetLastError = true, EntryPoint = "close")>]
extern int private posixClose(int fd)

let private fsyncDir (dir: string) : unit =
    let O_RDONLY = 0
    let fd = posixOpen (dir, O_RDONLY)

    if fd >= 0 then
        fsync fd |> ignore
        posixClose fd |> ignore

// ---------------------------------------------------------------------
// Binary codecs: each DU encodes as a tag byte + its fields, written with
// `Binary.Writer`; `decodeXxx` is each `encodeXxx`'s exact inverse. Options
// are presence-flagged, lists are length-prefixed, so every record is
// self-delimiting and a multi-GB snapshot needs no length table.
// ---------------------------------------------------------------------

let private writeBool (w: Writer) (b: bool) = w.WriteByte(if b then 1uy else 0uy)

let private readBool (r: #IReader) : bool = r.ReadByte() = 1uy

let private writeStr (w: Writer) (s: string) = w.WriteLenEncString s

let private readStr (r: #IReader) : string = r.ReadLenEncString() |> Option.defaultValue ""

let private writeOptStr (w: Writer) (s: string option) =
    match s with
    | None -> w.WriteByte 0uy
    | Some x ->
        w.WriteByte 1uy
        writeStr w x

let private readOptStr (r: #IReader) : string option =
    match r.ReadByte() with
    | 0uy -> None
    | _ -> Some(readStr r)

let private writeStrList (w: Writer) (xs: string list) =
    w.WriteInt32LE(List.length xs)
    List.iter (writeStr w) xs

let private readStrList (r: #IReader) : string list =
    List.init (r.ReadInt32LE()) (fun _ -> readStr r)

// ---------------------------------------------------------------------
// `Ast.ColumnType`
// ---------------------------------------------------------------------

let private encodeColumnType (w: Writer) (t: ColumnType) : unit =
    match t with
    | TTinyInt u -> w.WriteByte 0x01uy; writeBool w u
    | TBool -> w.WriteByte 0x1Euy
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
    // An fsp byte rides after the tag for the three fractional-second types
    // so a `DATETIME(6)` column survives a snapshot/WAL round-trip with its
    // precision. The persistence format carries no version field, so a
    // snapshot encoding these tags without the trailing fsp byte would be
    // misread — acceptable pre-1.0, and self-consistent for any data this
    // build both wrote and reads.
    | TDateTime fsp -> w.WriteByte 0x18uy; w.WriteByte(byte fsp)
    | TTimestamp fsp -> w.WriteByte 0x19uy; w.WriteByte(byte fsp)
    | TTime fsp -> w.WriteByte 0x1Auy; w.WriteByte(byte fsp)
    | TYear -> w.WriteByte 0x1Buy
    | TJson -> w.WriteByte 0x1Cuy
    | TVector dim -> w.WriteByte 0x1Duy; w.WriteInt32LE dim

let private decodeColumnType (r: #IReader) : ColumnType =
    match r.ReadByte() with
    | 0x01uy -> TTinyInt(readBool r)
    | 0x1Euy -> TBool
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
    | 0x18uy -> TDateTime(int (r.ReadByte()))
    | 0x19uy -> TTimestamp(int (r.ReadByte()))
    | 0x1Auy -> TTime(int (r.ReadByte()))
    | 0x1Buy -> TYear
    | 0x1Cuy -> TJson
    | 0x1Duy -> TVector(r.ReadInt32LE())
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
        | Xor -> 0x0Fuy
    )

let private decodeOp (r: #IReader) : Op =
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
    | 0x0Fuy -> Xor
    | tag -> failwithf "Persistence: unknown Op tag 0x%02x in WAL/snapshot" tag

let private encodeDirection (w: Writer) (d: Direction) : unit =
    w.WriteByte(match d with Asc -> 0x00uy | Desc -> 0x01uy)

let private decodeDirection (r: #IReader) : Direction =
    match r.ReadByte() with
    | 0x00uy -> Asc
    | _ -> Desc

let rec private encodeExpr (w: Writer) (expr: Expr) : unit =
    match expr with
    | Lit v -> w.WriteByte 0x01uy; encodeValue w v
    | Placeholder _ -> failwith "Persistence: a prepared-statement placeholder can't reach the WAL/snapshot"
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
    | Subquery _
    | WindowOver _
    | MatchAgainst _ ->
        failwithf "Persistence: a GENERATED column can't hold a subquery, MATCH or window function (MySQL itself rejects them there)"

let rec private decodeExpr (r: #IReader) : Expr =
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

let private decodeColumnDefault (r: #IReader) : ColumnDefault =
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

    // Tag doubles as the VIRTUAL/STORED kind: old snapshots wrote 1uy for
    // every generated column, which decodes as Virtual (MySQL's default).
    match c.Generated with
    | None -> w.WriteByte 0uy
    | Some(g, Virtual) ->
        w.WriteByte 1uy
        encodeExpr w g
    | Some(g, Stored) ->
        w.WriteByte 2uy
        encodeExpr w g

    writeOptStr w c.Collation
    writeOptStr w c.Charset
    writeBool w c.OnUpdateCurrentTimestamp

let private decodeColumnDef (r: #IReader) : ColumnDef =
    { Name = readStr r
      Type = decodeColumnType r
      Nullable = readBool r
      Default = (match r.ReadByte() with 0uy -> None | _ -> Some(decodeColumnDefault r))
      AutoIncrement = readBool r
      PrimaryKey = readBool r
      Unique = readBool r
      Generated =
        (match r.ReadByte() with
         | 0uy -> None
         | 2uy -> Some(decodeExpr r, Stored)
         | _ -> Some(decodeExpr r, Virtual))
      Collation = readOptStr r
      Charset = readOptStr r
      OnUpdateCurrentTimestamp = readBool r }

let private encodeIndexDef (w: Writer) (ix: IndexDef) : unit =
    writeStr w ix.Name
    writeStrList w ix.Columns
    writeBool w ix.Unique
    writeBool w (ix.Kind = FullTextIndex)

let private decodeIndexDef (r: #IReader) : IndexDef =
    { Name = readStr r
      Columns = readStrList r
      Unique = readBool r
      Kind = (if readBool r then FullTextIndex else BTree) }

let private encodeForeignKeyDef (w: Writer) (fk: ForeignKeyDef) : unit =
    writeStr w fk.Name
    writeStrList w fk.Columns
    writeStr w fk.RefTable
    writeStrList w fk.RefColumns
    writeOptStr w fk.OnDelete
    writeOptStr w fk.OnUpdate

let private decodeForeignKeyDef (r: #IReader) : ForeignKeyDef =
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

let private decodeColumnPosition (r: #IReader) : ColumnPosition =
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
    | SetAutoIncrement value -> w.WriteByte 0x0Cuy; w.WriteInt64LE value

let private decodeAlterAction (r: #IReader) : AlterAction =
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
    | 0x0Cuy -> SetAutoIncrement(r.ReadInt64LE())
    | _ -> AddPrimaryKey(readStrList r)

let private encodeStatement (w: Writer) (s: Statement) : unit =
    match s with
    | CreateDatabase(name, ifNotExists) -> w.WriteByte 0x01uy; writeStr w name; writeBool w ifNotExists
    | DropDatabase(name, ifExists) -> w.WriteByte 0x02uy; writeStr w name; writeBool w ifExists
    | CreateTable(name, columns, indexes, fks, ifNotExists, tableCharset, tableCollation, autoIncrementSeed) ->
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
        writeOptStr w (autoIncrementSeed |> Option.map string)
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
    | Truncate table -> w.WriteByte 0x09uy; writeStr w table
    // Tags 0x07/0x08 (`CreateIndex`/`DropIndexStmt`) are retired: the executor
    // routes both through `alterTable`, so they reach the WAL as
    // `AlterTable [AddIndex ...]`/`[DropIndexAction ...]` — byte-identical
    // payloads via `encodeAlterAction`. A second spelling of one event only
    // rots, and no WAL ever carried these.
    | other -> failwithf "Persistence: %A isn't a DDL statement SchemaChanged should ever carry" other

let private decodeStatement (r: #IReader) : Statement =
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
        let autoIncrementSeed = readOptStr r |> Option.map int64
        CreateTable(name, columns, indexes, fks, ifNotExists, tableCharset, tableCollation, autoIncrementSeed)
    | 0x04uy -> DropTable(readStrList r, readBool r)
    | 0x05uy -> AlterTable(readStr r, List.init (r.ReadInt32LE()) (fun _ -> decodeAlterAction r))
    | 0x06uy -> RenameTable(List.init (r.ReadInt32LE()) (fun _ -> readStr r, readStr r))
    | 0x09uy -> Truncate(readStr r)
    // 0x07/0x08 are retired — see `encodeStatement`.
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

let private decodeRowBin (r: #IReader) : Value[] =
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

let rec private decodeEvent (r: #IReader) : CommitEvent =
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
    | CreateTable(name, columns, indexes, fks, _, tableCharset, tableCollation, autoIncrementSeed) ->
        warn "CreateTable" (createTableSeeded store db name columns indexes fks tableCharset tableCollation autoIncrementSeed)
    | DropTable(names, _) -> names |> List.iter (fun n -> warn "DropTable" (dropTable store db n))
    | AlterTable(table, actions) ->
        // Replay non-strict, whatever the store's current mode: MODIFY/
        // CHANGE re-coerce existing rows, and a logged ALTER already
        // succeeded once — if it ran strict, every value was in range, so
        // non-strict re-coercion is the identity; if it ran non-strict, the
        // clamping replays identically. Replaying strict instead would
        // reject (and silently skip) an ALTER that clamped values.
        let saved = store.StrictMode
        store.StrictMode <- false
        warn "AlterTable" (alterTable store db table actions)
        store.StrictMode <- saved
    // One catalog swap for the whole event, matching how it was logged (see
    // `Storage.renameTables`) — replaying pair-by-pair would reintroduce the
    // partial rename the single event exists to prevent.
    | RenameTable pairs -> warn "RenameTable" (renameTables store db pairs)
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
            appendRowsForReplay store db table rows (Log.diagnostic "fsdb: WAL replay warning: %s")
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

let private encodeTableMeta (w: Writer) (t: Table) : unit =
    writeStr w t.OriginalName
    w.WriteInt32LE(List.length t.Columns)
    List.iter (encodeColumnDef w) t.Columns
    w.WriteInt32LE(List.length t.Indexes)
    List.iter (encodeIndexDef w) t.Indexes
    w.WriteInt32LE(List.length t.ForeignKeys)
    List.iter (encodeForeignKeyDef w) t.ForeignKeys
    writeOptStr w t.TableCharset
    writeOptStr w t.TableCollation
    w.WriteInt64LE t.CreateTime.Ticks
    w.WriteInt64LE t.NextAutoId
    w.WriteInt32LE t.RowsArray.Length

/// Writes the catalog straight to `s`, flushing the `Writer` every chunk so a
/// multi-GB snapshot never materializes as one `byte[]`. Rows are the only
/// unbounded part, so the flush checkpoint lives in the per-row loop.
///
/// Framed as `[magic][payload][int64 payload length][uint32 crc32]` — see
/// `snapshotMagic`'s doc — so `load` can tell a torn/zero-filled `.new`
/// apart from a genuinely empty catalog instead of trusting whatever
/// `decodeCatalog` happens to parse out of partial bytes. The CRC is folded
/// in per flushed chunk (`crc32Update`), not computed over one assembled
/// `byte[]`, for the same reason the flush loop exists at all.
let private writeCatalog (s: FileStream) (catalog: Catalog) : unit =
    s.Write(snapshotMagic, 0, snapshotMagic.Length)
    let mutable w = Writer()
    let mutable crc = 0xFFFFFFFFu
    let mutable payloadLen = 0L

    let flush () =
        if w.Count > 0 then
            let bytes = w.ToArray()
            s.Write(bytes, 0, bytes.Length)
            crc <- crc32Update crc bytes bytes.Length
            payloadLen <- payloadLen + int64 bytes.Length
            w.Clear()

    w.WriteInt32LE(Map.count catalog)

    catalog
    |> Map.iter (fun dbName db ->
        writeStr w dbName
        w.WriteInt32LE(Map.count db)

        db
        |> Map.iter (fun tableKey table ->
            writeStr w tableKey
            encodeTableMeta w table

            for row in table.RowsArray do
                encodeRowBin w row

                if w.Count >= (1 <<< 20) then
                    flush ()))

    flush ()

    let trailer = Writer()
    trailer.WriteInt64LE payloadLen
    trailer.WriteUInt32LE(~~~crc)
    let trailerBytes = trailer.ToArray()
    s.Write(trailerBytes, 0, trailerBytes.Length)

let private decodeTable (r: #IReader) : Table =
    let originalName = readStr r
    let columns = List.init (r.ReadInt32LE()) (fun _ -> decodeColumnDef r)
    let indexes = List.init (r.ReadInt32LE()) (fun _ -> decodeIndexDef r)
    let fks = List.init (r.ReadInt32LE()) (fun _ -> decodeForeignKeyDef r)
    let tableCharset = readOptStr r
    let tableCollation = readOptStr r
    let createTime = DateTime(r.ReadInt64LE())
    let nextAutoId = r.ReadInt64LE()
    let rows = List.init (r.ReadInt32LE()) (fun _ -> decodeRowBin r)

    reindexTable
        { OriginalName = originalName
          Columns = columns
          Indexes = indexes
          ForeignKeys = fks
          TableCharset = tableCharset
          TableCollation = tableCollation
          CreateTime = createTime
          RowsArray = RowStore.ofSeq rows
          NextAutoId = nextAutoId
          UniqueIndex = Map.empty }

/// Rejects a `snapshot.fsdb`/`.new` whose magic, claimed payload length, or
/// CRC doesn't match what's actually on disk — the guard `load` needs before
/// trusting a `.new` as authoritative (see `writeCatalog`'s doc): a
/// torn/zero-filled write parses to a "valid" empty catalog otherwise,
/// silently wiping every prior row once it's promoted over the real
/// snapshot and the WAL is truncated. Streams the payload in bounded chunks,
/// same reasoning as `writeCatalog`, so checking a multi-GB snapshot doesn't
/// need a multi-GB buffer.
let private verifySnapshotIntegrity (path: string) : bool =
    try
        use s = new FileStream(path, FileMode.Open, FileAccess.Read)
        let total = s.Length

        if total < int64 snapshotMagic.Length + int64 snapshotTrailerSize then
            false
        else
            let header = Array.zeroCreate<byte> snapshotMagic.Length

            if s.Read(header, 0, header.Length) <> header.Length || header <> snapshotMagic then
                false
            else
                let payloadLen = total - int64 snapshotMagic.Length - int64 snapshotTrailerSize
                s.Seek(int64 snapshotMagic.Length + payloadLen, SeekOrigin.Begin) |> ignore
                let trailerBytes = Array.zeroCreate<byte> snapshotTrailerSize

                if s.Read(trailerBytes, 0, trailerBytes.Length) <> trailerBytes.Length then
                    false
                else
                    let trailer = Reader(trailerBytes)
                    let claimedLen = trailer.ReadInt64LE()
                    let claimedCrc = trailer.ReadUInt32LE()

                    if claimedLen <> payloadLen then
                        false
                    else
                        s.Seek(int64 snapshotMagic.Length, SeekOrigin.Begin) |> ignore
                        let buf = Array.zeroCreate<byte> (1 <<< 16)
                        let mutable crc = 0xFFFFFFFFu
                        let mutable remaining = payloadLen
                        let mutable ok = true

                        while remaining > 0L && ok do
                            let toRead = int (min (int64 buf.Length) remaining)
                            let n = s.Read(buf, 0, toRead)

                            if n <= 0 then
                                ok <- false
                            else
                                crc <- crc32Update crc buf n
                                remaining <- remaining - int64 n

                        ok && (~~~crc) = claimedCrc
    with _ ->
        false

let private decodeCatalog (r: #IReader) : Catalog =
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
/// The filesystem half of `snapshotNow`, factored out so `attach`'s
/// rotation/shutdown paths (see `attach`'s `replica`) can write a snapshot
/// from a catalog other than the live `store.Catalog` while still sharing
/// every on-disk guarantee (integrity trailer, fsync, fsynced rename).
/// Callers are responsible for holding `store.Lock` for the duration.
let private writeSnapshotAndTruncate (dataDir: string) (catalog: Catalog) : unit =
    Directory.CreateDirectory dataDir |> ignore
    let finalPath = Path.Combine(dataDir, snapshotFileName)
    let newPath = finalPath + ".new"

    (use s = new FileStream(newPath, FileMode.Create, FileAccess.Write)
     writeCatalog s catalog
     flushToDisk s)

    File.WriteAllText(Path.Combine(dataDir, walFileName), "")
    File.Move(newPath, finalPath, true)
    fsyncDir dataDir

let snapshotNow (dataDir: string) (store: Store) : unit =
    lock store.Lock (fun () -> writeSnapshotAndTruncate dataDir store.Catalog)

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
    // A streamed reader, not `File.ReadAllBytes`: a multi-GB snapshot would
    // exceed the `byte[]` size limit if slurped whole. `StreamReader` decodes
    // it a buffer at a time.
    // Skip the `FSN1` magic only when it's actually there. A committed
    // `snapshot.fsdb` written before magic/CRC framing existed is pure
    // payload from offset 0 — seeking +4 unconditionally would drop its
    // first 4 bytes and crash-loop the server on every start after an
    // upgrade. `.new` always carries the magic (`verifySnapshotIntegrity`
    // requires it), so only the legacy committed snapshot needs this.
    let readSnapshot (path: string) : Catalog =
        use s = new FileStream(path, FileMode.Open, FileAccess.Read)
        let header = Array.zeroCreate<byte> snapshotMagic.Length
        let read = s.Read(header, 0, header.Length)
        let start = if read = snapshotMagic.Length && header = snapshotMagic then int64 snapshotMagic.Length else 0L
        s.Seek(start, SeekOrigin.Begin) |> ignore
        decodeCatalog (StreamReader(s))

    // `verifySnapshotIntegrity` first: a `.new` that fails its magic/length/
    // CRC check is a torn write from a crash mid-`writeCatalog`, not
    // authoritative data — falls straight through to the untouched old
    // snapshot + full WAL below, same as a `.new` that doesn't exist at all.
    let loadedFromNew =
        File.Exists newPath
        && verifySnapshotIntegrity newPath
        && (try
                setCatalog store (readSnapshot newPath)
                true
            with _ ->
                false)

    if loadedFromNew then
        File.Move(newPath, snapshotPath, true)
        File.WriteAllText(walPath, "")
        fsyncDir dataDir
    else
        if File.Exists snapshotPath then
            setCatalog store (readSnapshot snapshotPath)

        // A pre-feature snapshot has no `mysql` system schema; re-seed it
        // *before* replay so WAL events touching mysql.* find their tables.
        Storage.ensureMysqlSchema store

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

    // Covers the `.new`-snapshot path above the same way (no-op if the
    // snapshot already carried the schema, or the else-branch re-seeded it).
    Storage.ensureMysqlSchema store
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
/// crosses `Limits.walRotateBytes`/`Limits.walRotateEntries`, and does one last rotation
/// when the process gets a SIGTERM/SIGINT so a graceful shutdown always
/// leaves a fresh snapshot behind (a `kill -9` skips this — replaying the
/// WAL from the last snapshot is what that's for).
let attach (dataDir: string) (store: Store) : unit =
    Directory.CreateDirectory dataDir |> ignore
    let walPath = Path.Combine(dataDir, walFileName)
    let entryCount = ref 0

    // `Storage`'s writers publish a mutation to `store.Catalog` (the
    // `Database ref` swap in `withDatabase`) and only *afterwards* call
    // `emit`, which is what actually reaches `appendRecord` below — two
    // separate, unlocked steps. A rotation/shutdown snapshot that read
    // `store.Catalog` directly could land in that gap: it'd capture a row
    // that's visible in the catalog but whose WAL record hasn't been
    // appended yet, and — since the same snapshot also truncates the WAL —
    // that record then lands in the *truncated* WAL right after, applying
    // the row twice on the next replay (`load`'s snapshot-then-WAL replay
    // has no way to tell the duplicate apart from a real second insert).
    //
    // `replica` closes that window by never trusting the live catalog for a
    // snapshot at all: it starts as a copy of `store`'s catalog at `attach`
    // time (before any commit can reach it) and from then on only ever
    // advances by replaying an `event` right here, immediately after that
    // same event's WAL record is confirmed on disk — both steps inside the
    // one `store.Lock` critical section `emit` already wraps every
    // `appendRecord` call in. So `replica.Catalog` is always exactly "what
    // the WAL can currently prove", never ahead of it — a snapshot taken
    // from it can truncate the WAL without ever discarding a record that
    // hasn't been folded in yet.
    let replica = Storage.create ()
    setCatalog replica store.Catalog
    replica.ForeignKeyChecks <- false

    // Rotates from `replica`, not `store` — see `replica`'s doc above.
    // `reindexAllForReplay` mirrors `load`'s own "reindex once, after every
    // event up to this point has landed" — `applyEvent`'s row-update/delete
    // path (`mapTableRows`) deliberately leaves `UniqueIndex` stale per
    // event for the same reason `load` accepts it: rebuilding it on every
    // single replayed event would cost a full-table rescan per WAL record;
    // once per rotation matches `load`'s own amortization.
    let rotateFromReplica () =
        reindexAllForReplay replica
        writeSnapshotAndTruncate dataDir replica.Catalog

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

        // `event`'s WAL record is durably on disk as of the line above —
        // safe to fold into `replica` now (see `replica`'s doc). `applyEvent`
        // is the same function `load` trusts for a cold-start WAL replay, so
        // a failure here would mean that replay itself can't reproduce this
        // event either — logged, not fatal, since the WAL (the actual
        // durability guarantee) already has the record regardless of what
        // `replica` does with it; only `replica`-sourced rotation snapshots
        // would miss the row until the next full replay (a fresh `load`)
        // rebuilds `replica`'s equivalent from scratch.
        (try
            applyEvent replica event
         with ex ->
             Log.diagnostic "fsdb: WAL mirror apply failed: %s" ex.Message)

        entryCount := !entryCount + 1

        if walSize > Limits.walRotateBytes || !entryCount > Limits.walRotateEntries then
            rotateFromReplica ()
            entryCount := 0

    // Appended, never assigned — `OnCommit` is multi-subscriber, so
    // durability and a host's `Db.onCommit` CDC handlers coexist.
    store.OnCommit.Add appendRecord

    let onShutdown (_: PosixSignalContext) = lock store.Lock rotateFromReplica

    // `PosixSignalRegistration` unregisters its handler when finalized —
    // `attach` returning right after this would leave nothing holding a
    // reference, so the first GC silently stops the SIGTERM/SIGINT
    // shutdown snapshot from ever firing again. Root them for the process's
    // lifetime instead of `|> ignore`-ing the disposable away.
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, onShutdown))
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, onShutdown))
