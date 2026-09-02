/// Opt-in binary WAL and snapshot durability. Physical row events preserve
/// evaluated values during replay; DDL reuses Storage because Executor
/// compiles after this module.
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

/// Snapshot magic prevents a torn zero-filled file from decoding as an empty catalog.
let private legacySnapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x31uy |] // "FSN1"
let private columnCommentSnapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x32uy |] // "FSN2"
let private tableCommentSnapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x33uy |] // "FSN3"
let private numericDisplaySnapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x34uy |] // "FSN4"
let private partitionSnapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x35uy |] // "FSN5"
let private snapshotMagic = [| 0x46uy; 0x53uy; 0x4Euy; 0x36uy |] // "FSN6"

type private SnapshotFormat =
    { ColumnComments: bool
      TableComments: bool
      NumericDisplays: bool
      Partitions: bool
      DynamicPrivileges: bool }

let private legacySnapshotFormat =
    { ColumnComments = false
      TableComments = false
      NumericDisplays = false
      Partitions = false
      DynamicPrivileges = false }

let private columnCommentSnapshotFormat =
    { legacySnapshotFormat with ColumnComments = true }

let private tableCommentSnapshotFormat =
    { columnCommentSnapshotFormat with TableComments = true }

let private numericDisplaySnapshotFormat =
    { tableCommentSnapshotFormat with NumericDisplays = true }

let private partitionSnapshotFormat =
    { numericDisplaySnapshotFormat with Partitions = true }

let private currentSnapshotFormat =
    { partitionSnapshotFormat with DynamicPrivileges = true }

/// Snapshot trailer: `[int64 payload length][uint32 crc32]`. The incremental
/// CRC avoids materializing a multi-gigabyte payload.
let private snapshotTrailerSize = 12

let private snapshotFormat (header: byte[]) : SnapshotFormat option =
    if header = snapshotMagic then
        Some currentSnapshotFormat
    elif header = partitionSnapshotMagic then
        Some partitionSnapshotFormat
    elif header = numericDisplaySnapshotMagic then
        Some numericDisplaySnapshotFormat
    elif header = tableCommentSnapshotMagic then
        Some tableCommentSnapshotFormat
    elif header = columnCommentSnapshotMagic then
        Some columnCommentSnapshotFormat
    elif header = legacySnapshotMagic then
        Some legacySnapshotFormat
    else
        None

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

/// Signal registrations must remain rooted or finalization unregisters them.
let private shutdownRegistrations = ResizeArray<IDisposable>()

[<DllImport("libc", SetLastError = true)>]
extern int private fsync(int fd)

/// Plain fsync matches MySQL's default macOS durability; Flush(true) uses the
/// materially stronger and slower F_FULLFSYNC.
let private flushToDisk (s: FileStream) : unit =
    s.Flush false
    let rc = fsync(s.SafeFileHandle.DangerousGetHandle().ToInt32())

    if rc <> 0 then
        // Continuing after fsync failure would falsely acknowledge durability.
        let err = Marshal.GetLastWin32Error()
        Environment.FailFast(sprintf "fsdb: fatal fsync failure (errno %d) — write cannot be confirmed durable" err)

/// A file fsync does not persist the directory entry created by snapshot rename.
/// Directory fsync is best-effort because the durable file bytes remain valid.
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

// Tagged, length-delimited codecs keep WAL and snapshots streamable.

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

let private encodeColumnType (w: Writer) (t: ColumnType) : unit =
    match t with
    | TTinyInt u -> w.WriteByte 0x01uy; writeBool w u
    | TBool -> w.WriteByte 0x1Euy
    | TSmallInt u -> w.WriteByte 0x02uy; writeBool w u
    | TMediumInt u -> w.WriteByte 0x03uy; writeBool w u
    | TInt u -> w.WriteByte 0x04uy; writeBool w u
    | TBigInt u -> w.WriteByte 0x05uy; writeBool w u
    | TBit width -> w.WriteByte 0x20uy; w.WriteInt32LE width
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
    | TDecimal(p, s, false) -> w.WriteByte 0x14uy; w.WriteInt32LE p; w.WriteInt32LE s
    | TDecimal(p, s, true) -> w.WriteByte 0x23uy; w.WriteInt32LE p; w.WriteInt32LE s
    | TDouble false -> w.WriteByte 0x15uy
    | TFloat false -> w.WriteByte 0x16uy
    | TDouble true -> w.WriteByte 0x21uy
    | TFloat true -> w.WriteByte 0x22uy
    | TDate -> w.WriteByte 0x17uy
    // Fractional precision is part of the persisted type identity.
    | TDateTime fsp -> w.WriteByte 0x18uy; w.WriteByte(byte fsp)
    | TTimestamp fsp -> w.WriteByte 0x19uy; w.WriteByte(byte fsp)
    | TTime fsp -> w.WriteByte 0x1Auy; w.WriteByte(byte fsp)
    | TYear -> w.WriteByte 0x1Buy
    | TJson -> w.WriteByte 0x1Cuy
    | TVector dim -> w.WriteByte 0x1Duy; w.WriteInt32LE dim
    | TGeometry kind ->
        w.WriteByte 0x1Fuy
        w.WriteByte(
            match kind with
            | Geometry -> 0uy
            | Point -> 1uy
            | LineString -> 2uy
            | Polygon -> 3uy
            | MultiPoint -> 4uy
            | MultiLineString -> 5uy
            | MultiPolygon -> 6uy
            | GeometryCollection -> 7uy)

let private decodeColumnType (r: #IReader) : ColumnType =
    match r.ReadByte() with
    | 0x01uy -> TTinyInt(readBool r)
    | 0x1Euy -> TBool
    | 0x02uy -> TSmallInt(readBool r)
    | 0x03uy -> TMediumInt(readBool r)
    | 0x04uy -> TInt(readBool r)
    | 0x05uy -> TBigInt(readBool r)
    | 0x20uy -> TBit(r.ReadInt32LE())
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
    | 0x14uy -> TDecimal(r.ReadInt32LE(), r.ReadInt32LE(), false)
    | 0x23uy -> TDecimal(r.ReadInt32LE(), r.ReadInt32LE(), true)
    | 0x15uy -> TDouble false
    | 0x16uy -> TFloat false
    | 0x21uy -> TDouble true
    | 0x22uy -> TFloat true
    | 0x17uy -> TDate
    | 0x18uy -> TDateTime(int (r.ReadByte()))
    | 0x19uy -> TTimestamp(int (r.ReadByte()))
    | 0x1Auy -> TTime(int (r.ReadByte()))
    | 0x1Buy -> TYear
    | 0x1Cuy -> TJson
    | 0x1Duy -> TVector(r.ReadInt32LE())
    | 0x1Fuy ->
        let kind =
            match r.ReadByte() with
            | 1uy -> Point
            | 2uy -> LineString
            | 3uy -> Polygon
            | 4uy -> MultiPoint
            | 5uy -> MultiLineString
            | 6uy -> MultiPolygon
            | 7uy -> GeometryCollection
            | 0uy -> Geometry
            | tag -> failwithf "Persistence: unknown geometry kind 0x%02x in WAL/snapshot" tag

        TGeometry kind
    | tag -> failwithf "Persistence: unknown ColumnType tag 0x%02x in WAL/snapshot" tag

// Persisted expressions belong to generated columns, where MySQL rejects
// subqueries; unsupported query-bearing nodes therefore fail explicitly.

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
        | SignedSub -> 0x10uy
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
    | 0x10uy -> SignedSub
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
    | UserVariable _
    | SystemVariable _
    | AssignUserVariable _ -> failwith "Persistence: a stored expression can't reference a session variable"
    | Col name -> w.WriteByte 0x02uy; writeStr w name
    | QualifiedCol(t, c) -> w.WriteByte 0x03uy; writeStr w t; writeStr w c
    | Row values -> w.WriteByte 0x17uy; w.WriteInt32LE(List.length values); List.iter (encodeExpr w) values
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
    | QuantifiedComparison _
    | Exists _
    | Subquery _
    | WindowOver _
    | MatchAgainst _ ->
        failwithf "Persistence: a GENERATED column can't hold a subquery, MATCH or window function (MySQL itself rejects them there)"

let private maxDecodeDepth = 256

let rec private decodeExprAt (depth: int) (r: #IReader) : Expr =
    if depth > maxDecodeDepth then
        failwith "Persistence: expression nesting exceeds the decode limit"

    let nested () = decodeExprAt (depth + 1) r

    let optExpr () =
        match r.ReadByte() with
        | 0uy -> None
        | _ -> Some(nested ())

    let exprList () = List.init (r.ReadInt32LE()) (fun _ -> nested ())

    let orderByList () = List.init (r.ReadInt32LE()) (fun _ -> nested (), decodeDirection r)

    match r.ReadByte() with
    | 0x01uy -> Lit(decodeValue r)
    | 0x02uy -> Col(readStr r)
    | 0x03uy -> QualifiedCol(readStr r, readStr r)
    | 0x17uy -> Row(exprList ())
    | 0x04uy -> BinOp(decodeOp r, nested (), nested ())
    | 0x05uy -> Not(nested ())
    | 0x06uy -> IsNull(nested ())
    | 0x07uy -> IsNotNull(nested ())
    | 0x08uy -> IsTrue(nested ())
    | 0x09uy -> IsFalse(nested ())
    | 0x0Auy ->
        let e = nested ()
        let p = nested ()
        let cs = readBool r
        let esc = match r.ReadByte() with 0uy -> None | _ -> Some(char (r.ReadByte()))
        Like(e, p, cs, esc)
    | 0x0Buy -> Regexp(nested (), nested ())
    | 0x0Cuy -> In(nested (), exprList ())
    | 0x0Duy -> Between(nested (), nested (), nested ())
    | 0x0Euy -> FuncCall(readStr r, exprList ())
    | 0x11uy -> Distinct(nested ())
    | 0x12uy -> OrderBy(nested (), decodeDirection r)
    | 0x13uy -> Cast(nested (), decodeColumnType r)
    | 0x14uy -> Collate(nested (), readStr r)
    | 0x15uy -> Star(readOptStr r)
    | 0x16uy ->
        let subject = optExpr ()
        let whens = List.init (r.ReadInt32LE()) (fun _ -> nested (), nested ())
        let elseBranch = optExpr ()
        Case(subject, whens, elseBranch)
    | tag -> failwithf "Persistence: unknown Expr tag 0x%02x in WAL/snapshot" tag

let private decodeExpr (r: #IReader) : Expr = decodeExprAt 0 r

let private encodeColumnDefault (w: Writer) (d: ColumnDefault) : unit =
    match d with
    | DConst v -> w.WriteByte 0x01uy; encodeValue w v
    | DCurrentTimestamp -> w.WriteByte 0x02uy
    | DExpression expression -> w.WriteByte 0x03uy; encodeExpr w expression

let private decodeColumnDefault (r: #IReader) : ColumnDefault =
    match r.ReadByte() with
    | 0x01uy -> DConst(decodeValue r)
    | 0x02uy -> DCurrentTimestamp
    | 0x03uy -> DExpression(decodeExpr r)
    | tag -> failwithf "Persistence: unknown ColumnDefault tag 0x%02x" tag

/// Generated expressions must survive restart or later writes compute NULL.
let private encodeColumnDef (format: SnapshotFormat) (w: Writer) (c: ColumnDef) : unit =
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

    // Tag 1 remains Virtual for snapshots predating an explicit storage kind.
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
    if format.ColumnComments then
        writeStr w c.Comment

    if format.NumericDisplays then
        match c.NumericDisplay with
        | None -> w.WriteByte 0uy
        | Some display ->
            w.WriteByte 1uy

            match display.Width with
            | None -> w.WriteByte 0uy
            | Some width -> w.WriteByte 1uy; w.WriteInt32LE width

            match display.Decimals with
            | None -> w.WriteByte 0uy
            | Some decimals -> w.WriteByte 1uy; w.WriteInt32LE decimals

            writeBool w display.ZeroFill

let private decodeColumnDef (format: SnapshotFormat) (r: #IReader) : ColumnDef =
    let name = readStr r
    let columnType = decodeColumnType r
    let nullable = readBool r
    let defaultValue = match r.ReadByte() with 0uy -> None | _ -> Some(decodeColumnDefault r)
    let autoIncrement = readBool r
    let primaryKey = readBool r
    let unique = readBool r
    let generated =
        match r.ReadByte() with
        | 0uy -> None
        | 2uy -> Some(decodeExpr r, Stored)
        | _ -> Some(decodeExpr r, Virtual)
    let collation = readOptStr r
    let charset = readOptStr r
    let onUpdateCurrentTimestamp = readBool r
    let comment = if format.ColumnComments then readStr r else ""
    let numericDisplay =
        if format.NumericDisplays && r.ReadByte() <> 0uy then
            let width = if r.ReadByte() = 0uy then None else Some(r.ReadInt32LE())
            let decimals = if r.ReadByte() = 0uy then None else Some(r.ReadInt32LE())
            Some
                { Width = width
                  Decimals = decimals
                  ZeroFill = readBool r }
        else
            None

    { Name = name
      Type = columnType
      NumericDisplay = numericDisplay
      Nullable = nullable
      Default = defaultValue
      AutoIncrement = autoIncrement
      PrimaryKey = primaryKey
      Unique = unique
      Generated = generated
      Collation = collation
      Charset = charset
      OnUpdateCurrentTimestamp = onUpdateCurrentTimestamp
      Comment = comment }

let private lowercaseIndexColumnPrefix = "\u0000L:"
let private uppercaseIndexColumnPrefix = "\u0000U:"
let private expressionIndexColumnPrefix = "\u0000E:"
let private descendingIndexColumnPrefix = "\u0000D:"
let private invisibleIndexNamePrefix = "\u0000I:"
let private lengthIndexColumnPrefix = "\u0001"

let private (|Prefixed|_|) (prefix: string) (value: string) =
    if value.StartsWith(prefix, StringComparison.Ordinal) then
        Some(value.Substring prefix.Length)
    else
        None

// Key-part attributes stay inside the existing string-list field so the
// following payload remains aligned. NUL cannot occur in an identifier.
let private encodeIndexColumn column =
    let encoded =
        match column.Transform, column.PrefixLength with
        | Some Lowercase, _ -> lowercaseIndexColumnPrefix + column.Name
        | Some Uppercase, _ -> uppercaseIndexColumnPrefix + column.Name
        | Some(Expression expression), _ ->
            let expressionBytes = Writer()
            encodeExpr expressionBytes expression
            expressionIndexColumnPrefix + Convert.ToBase64String(expressionBytes.ToArray())
        | None, None -> column.Name
        | None, Some length -> sprintf "%s%d:%s" lengthIndexColumnPrefix length column.Name

    match column.Direction with
    | Asc -> encoded
    | Desc -> descendingIndexColumnPrefix + encoded

let private decodeIndexColumn (encoded: string) =
    let direction, encoded =
        match encoded with
        | Prefixed descendingIndexColumnPrefix encoded -> Desc, encoded
        | _ -> Asc, encoded

    let column name prefixLength transform =
        { Name = name
          PrefixLength = prefixLength
          Transform = transform
          Direction = direction }

    match encoded with
    | Prefixed lowercaseIndexColumnPrefix name -> column name None (Some Lowercase)
    | Prefixed uppercaseIndexColumnPrefix name -> column name None (Some Uppercase)
    | Prefixed expressionIndexColumnPrefix expression ->
        let expression = expression |> Convert.FromBase64String |> Reader |> decodeExpr
        column "" None (Some(Expression expression))
    | Prefixed lengthIndexColumnPrefix encoded ->
        match encoded.IndexOf(':') with
        | separator when separator > 0 ->
            match Int32.TryParse(encoded.Substring(0, separator)) with
            | true, length -> column (encoded.Substring(separator + 1)) (Some length) None
            | _ -> column (lengthIndexColumnPrefix + encoded) None None
        | _ -> column (lengthIndexColumnPrefix + encoded) None None
    | _ -> column encoded None None

let private encodeIndexDef (w: Writer) (ix: IndexDef) : unit =
    writeStr w (if ix.Visible then ix.Name else invisibleIndexNamePrefix + ix.Name)
    ix.KeyColumns |> List.map encodeIndexColumn |> writeStrList w
    writeBool w ix.Unique
    writeBool w (ix.Kind = FullTextIndex)

let private decodeIndexDef (r: #IReader) : IndexDef =
    let encodedName = readStr r
    let visible, name =
        match encodedName with
        | Prefixed invisibleIndexNamePrefix name -> false, name
        | _ -> true, encodedName

    { Name = name
      KeyColumns = readStrList r |> List.map decodeIndexColumn
      Unique = readBool r
      Visible = visible
      Kind = (if readBool r then FullTextIndex else BTree) }

// Qualifiers reuse the legacy table-name field so older snapshots remain readable.
let private qualifiedForeignKeyPrefix = "\u0000Q:"

let private encodeForeignKeyTarget (fk: ForeignKeyDef) =
    match fk.RefDatabase with
    | Some database -> qualifiedForeignKeyPrefix + database + "\u0000" + fk.RefTable
    | None -> fk.RefTable

let private decodeForeignKeyTarget (encoded: string) =
    if encoded.StartsWith(qualifiedForeignKeyPrefix, StringComparison.Ordinal) then
        let qualified = encoded.Substring qualifiedForeignKeyPrefix.Length

        match qualified.IndexOf '\u0000' with
        | separator when separator >= 0 -> Some(qualified.Substring(0, separator)), qualified.Substring(separator + 1)
        | _ -> None, encoded
    else
        None, encoded

let private encodeForeignKeyDef (w: Writer) (fk: ForeignKeyDef) : unit =
    writeStr w fk.Name
    writeStrList w fk.Columns
    writeStr w (encodeForeignKeyTarget fk)
    writeStrList w fk.RefColumns
    writeOptStr w fk.OnDelete
    writeOptStr w fk.OnUpdate

let private decodeForeignKeyDef (r: #IReader) : ForeignKeyDef =
    let name = readStr r
    let columns = readStrList r
    let refDatabase, refTable = readStr r |> decodeForeignKeyTarget

    { Name = name
      Columns = columns
      RefDatabase = refDatabase
      RefTable = refTable
      RefColumns = readStrList r
      OnDelete = readOptStr r
      OnUpdate = readOptStr r }

// Commit events persist only DDL statement shapes; other statements fail loudly.

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

let private encodePartitioning (w: Writer) (partitioning: HashPartitioning option) : unit =
    match partitioning with
    | None -> w.WriteByte 0uy
    | Some value ->
        w.WriteByte 1uy
        encodeExpr w value.Expression
        w.WriteInt32LE(int32 value.Count)
        writeBool w value.Linear

let private decodePartitioning (r: #IReader) : HashPartitioning option =
    if r.ReadByte() = 0uy then
        None
    else
        Some
            { Expression = decodeExpr r
              Count = uint32 (r.ReadInt32LE())
              Linear = readBool r }

let private encodeAlterAction (format: SnapshotFormat) (w: Writer) (a: AlterAction) : unit =
    match a with
    | AddColumn(c, position) -> w.WriteByte 0x01uy; encodeColumnDef format w c; encodeColumnPosition w position
    | DropColumn name -> w.WriteByte 0x02uy; writeStr w name
    | ModifyColumn(c, position) -> w.WriteByte 0x03uy; encodeColumnDef format w c; encodeColumnPosition w position
    | ChangeColumn(oldName, c, position) -> w.WriteByte 0x04uy; writeStr w oldName; encodeColumnDef format w c; encodeColumnPosition w position
    | RenameTo name -> w.WriteByte 0x05uy; writeStr w name
    | RenameColumnTo(oldName, newName) -> w.WriteByte 0x06uy; writeStr w oldName; writeStr w newName
    | AddIndex ix -> w.WriteByte 0x07uy; encodeIndexDef w ix
    | DropIndexAction name -> w.WriteByte 0x08uy; writeStr w name
    | AddForeignKey fk -> w.WriteByte 0x09uy; encodeForeignKeyDef w fk
    | DropForeignKey name -> w.WriteByte 0x0Auy; writeStr w name
    | AddPrimaryKey columns -> w.WriteByte 0x0Buy; columns |> List.map encodeIndexColumn |> writeStrList w
    | SetAutoIncrement value -> w.WriteByte 0x0Cuy; w.WriteInt64LE value
    | SetDefault(column, value) ->
        w.WriteByte 0x0Duy
        writeStr w column

        match value with
        | None -> w.WriteByte 0uy
        | Some defaultValue ->
            w.WriteByte 1uy
            encodeColumnDefault w defaultValue
    | RenameIndex(oldName, newName) -> w.WriteByte 0x0Euy; writeStr w oldName; writeStr w newName
    | ConvertCharset(charset, collation) -> w.WriteByte 0x0Fuy; writeStr w charset; writeOptStr w collation
    | SetTableComment comment when format.TableComments -> w.WriteByte 0x10uy; writeStr w comment
    | DropPrimaryKey -> w.WriteByte 0x11uy
    | SetIndexVisibility(name, visible) -> w.WriteByte 0x12uy; writeStr w name; writeBool w visible
    | AddHashPartitions count when format.Partitions -> w.WriteByte 0x13uy; w.WriteInt32LE(int32 count)
    | CoalesceHashPartitions count when format.Partitions -> w.WriteByte 0x14uy; w.WriteInt32LE(int32 count)
    | AddCheck _
    | DropCheck _
    | SetCheckEnforced _
    | SetEngine _
    | SetAlterAlgorithm _
    | SetAlterLock _
    | SetRowFormat _
    | SetTableComment _ -> failwith "Persistence: unsupported ALTER action reached a SchemaChanged event"
    | AddHashPartitions _
    | CoalesceHashPartitions _ -> failwith "Persistence: partition ALTER action requires the current WAL format"

let private decodeAlterAction (format: SnapshotFormat) (r: #IReader) : AlterAction =
    match r.ReadByte() with
    | 0x01uy -> AddColumn(decodeColumnDef format r, decodeColumnPosition r)
    | 0x02uy -> DropColumn(readStr r)
    | 0x03uy -> ModifyColumn(decodeColumnDef format r, decodeColumnPosition r)
    | 0x04uy -> ChangeColumn(readStr r, decodeColumnDef format r, decodeColumnPosition r)
    | 0x05uy -> RenameTo(readStr r)
    | 0x06uy -> RenameColumnTo(readStr r, readStr r)
    | 0x07uy -> AddIndex(decodeIndexDef r)
    | 0x08uy -> DropIndexAction(readStr r)
    | 0x09uy -> AddForeignKey(decodeForeignKeyDef r)
    | 0x0Auy -> DropForeignKey(readStr r)
    | 0x0Cuy -> SetAutoIncrement(r.ReadInt64LE())
    | 0x0Duy ->
        let column = readStr r
        let value = if r.ReadByte() = 0uy then None else Some(decodeColumnDefault r)
        SetDefault(column, value)
    | 0x0Euy -> RenameIndex(readStr r, readStr r)
    | 0x0Fuy -> ConvertCharset(readStr r, readOptStr r)
    | 0x10uy when format.TableComments -> SetTableComment(readStr r)
    | 0x11uy -> DropPrimaryKey
    | 0x12uy -> SetIndexVisibility(readStr r, readBool r)
    | 0x13uy when format.Partitions -> AddHashPartitions(uint32 (r.ReadInt32LE()))
    | 0x14uy when format.Partitions -> CoalesceHashPartitions(uint32 (r.ReadInt32LE()))
    | _ -> AddPrimaryKey(readStrList r |> List.map decodeIndexColumn)

let private encodeStatement (format: SnapshotFormat) (w: Writer) (s: Statement) : unit =
    match s with
    | CreateDatabase(name, ifNotExists, _) -> w.WriteByte 0x01uy; writeStr w name; writeBool w ifNotExists
    | DropDatabase(name, ifExists) -> w.WriteByte 0x02uy; writeStr w name; writeBool w ifExists
    | CreateTable table ->
        w.WriteByte 0x03uy
        writeStr w table.Name
        w.WriteInt32LE(List.length table.Columns)
        List.iter (encodeColumnDef format w) table.Columns
        w.WriteInt32LE(List.length table.Indexes)
        List.iter (encodeIndexDef w) table.Indexes
        w.WriteInt32LE(List.length table.ForeignKeys)
        List.iter (encodeForeignKeyDef w) table.ForeignKeys
        writeBool w table.IfNotExists
        writeOptStr w table.Charset
        writeOptStr w table.Collation
        writeOptStr w (table.AutoIncrementSeed |> Option.map string)
        writeOptStr w table.Comment
        if format.Partitions then encodePartitioning w table.Partitioning
    | DropTable(names, ifExists) -> w.WriteByte 0x04uy; writeStrList w names; writeBool w ifExists
    | AlterTable(table, actions) ->
        w.WriteByte 0x05uy
        writeStr w table
        w.WriteInt32LE(List.length actions)
        List.iter (encodeAlterAction format w) actions
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

let private decodeStatement (format: SnapshotFormat) (r: #IReader) : Statement =
    match r.ReadByte() with
    | 0x01uy -> CreateDatabase(readStr r, readBool r, [])
    | 0x02uy -> DropDatabase(readStr r, readBool r)
    | 0x03uy ->
        let name = readStr r
        let columns = List.init (r.ReadInt32LE()) (fun _ -> decodeColumnDef format r)
        let indexes = List.init (r.ReadInt32LE()) (fun _ -> decodeIndexDef r)
        let fks = List.init (r.ReadInt32LE()) (fun _ -> decodeForeignKeyDef r)
        let ifNotExists = readBool r
        let tableCharset = readOptStr r
        let tableCollation = readOptStr r
        let autoIncrementSeed = readOptStr r |> Option.map int64
        let tableComment = if format.TableComments then readOptStr r else None
        let partitioning = if format.Partitions then decodePartitioning r else None
        CreateTable
            { Name = name
              Columns = columns
              Indexes = indexes
              ForeignKeys = fks
              Checks = []
              IfNotExists = ifNotExists
              Charset = tableCharset
              Collation = tableCollation
              AutoIncrementSeed = autoIncrementSeed
              Comment = tableComment
              Partitioning = partitioning
              Deprecations = [] }
    | 0x04uy -> DropTable(readStrList r, readBool r)
    | 0x05uy -> AlterTable(readStr r, List.init (r.ReadInt32LE()) (fun _ -> decodeAlterAction format r))
    | 0x06uy -> RenameTable(List.init (r.ReadInt32LE()) (fun _ -> readStr r, readStr r))
    | 0x09uy -> Truncate(readStr r)
    // 0x07/0x08 are retired — see `encodeStatement`.
    | tag -> failwithf "Persistence: unknown Statement tag 0x%02x in WAL/snapshot" tag

let private KindRowsInserted = 0x01uy
let private KindRowsUpdated = 0x02uy
let private KindRowsDeleted = 0x03uy
let private KindSchemaChanged = 0x04uy
let private KindTransactionCommitted = 0x05uy
let private KindSchemaChangedAt = 0x06uy
let private KindSchemaChangedV2 = 0x07uy
let private KindSchemaChangedAtV2 = 0x08uy
let private KindSchemaChangedV3 = 0x09uy
let private KindSchemaChangedAtV3 = 0x0Auy
let private KindSchemaChangedV4 = 0x0Buy
let private KindSchemaChangedAtV4 = 0x0Cuy
let private KindSchemaChangedV5 = 0x0Duy
let private KindSchemaChangedAtV5 = 0x0Euy
let private KindXaPrepared = 0x0Fuy
let private KindXaCommitted = 0x10uy
let private KindXaRolledBack = 0x11uy
let private KindAutoIncrementAdvanced = 0x12uy

let private encodeXid (w: Writer) (xid: Xa.Xid) =
    w.WriteUInt32LE xid.FormatId
    w.WriteInt32LE xid.GlobalId.Length
    w.WriteBytes(List.toArray xid.GlobalId)
    w.WriteInt32LE xid.BranchQualifier.Length
    w.WriteBytes(List.toArray xid.BranchQualifier)

let private decodeXid (r: #IReader) : Xa.Xid =
    let formatId = uint32 (r.ReadInt32LE())
    let globalLength = r.ReadInt32LE()

    if globalLength < 0 || globalLength > 64 then
        failwith "Persistence: invalid XA global identifier length"

    let globalId = r.ReadBytes globalLength |> List.ofArray
    let branchLength = r.ReadInt32LE()

    if branchLength < 0 || branchLength > 64 then
        failwith "Persistence: invalid XA branch identifier length"

    { GlobalId = globalId
      BranchQualifier = r.ReadBytes branchLength |> List.ofArray
      FormatId = formatId }

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
    | AutoIncrementAdvanced(db, table, nextId) ->
        w.WriteByte KindAutoIncrementAdvanced
        w.WriteLenEncString db
        w.WriteLenEncString table
        w.WriteInt64LE nextId
    | SchemaChanged(db, stmt) ->
        w.WriteByte KindSchemaChangedV5
        w.WriteLenEncString db
        encodeStatement currentSnapshotFormat w stmt
    | SchemaChangedAt(db, stmt, createTime) ->
        w.WriteByte KindSchemaChangedAtV5
        w.WriteLenEncString db
        encodeStatement currentSnapshotFormat w stmt
        w.WriteInt64LE createTime.Ticks
    | TransactionCommitted events ->
        w.WriteByte KindTransactionCommitted
        w.WriteInt32LE (List.length events)

        for e in events do
            encodeEvent w e
    | XaPrepared(xid, validateWholeSnapshot, events) ->
        w.WriteByte KindXaPrepared
        encodeXid w xid
        writeBool w validateWholeSnapshot
        w.WriteInt32LE events.Length

        for event in events do
            encodeEvent w event
    | XaCommitted(xid, events) ->
        w.WriteByte KindXaCommitted
        encodeXid w xid
        w.WriteInt32LE events.Length

        for event in events do
            encodeEvent w event
    | XaRolledBack xid ->
        w.WriteByte KindXaRolledBack
        encodeXid w xid

let rec private decodeEventAt (depth: int) (r: #IReader) : CommitEvent =
    if depth > maxDecodeDepth then
        failwith "Persistence: transaction nesting exceeds the decode limit"

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
    | k when k = KindAutoIncrementAdvanced ->
        AutoIncrementAdvanced(str (), str (), r.ReadInt64LE())
    | k when k = KindSchemaChanged ->
        let db = str ()
        SchemaChanged(db, decodeStatement legacySnapshotFormat r)
    | k when k = KindTransactionCommitted ->
        TransactionCommitted(List.init (r.ReadInt32LE()) (fun _ -> decodeEventAt (depth + 1) r))
    | k when k = KindSchemaChangedAt ->
        let db = str ()
        SchemaChangedAt(db, decodeStatement legacySnapshotFormat r, DateTime(r.ReadInt64LE()))
    | k when k = KindSchemaChangedV2 ->
        let db = str ()
        SchemaChanged(db, decodeStatement columnCommentSnapshotFormat r)
    | k when k = KindSchemaChangedAtV2 ->
        let db = str ()
        SchemaChangedAt(db, decodeStatement columnCommentSnapshotFormat r, DateTime(r.ReadInt64LE()))
    | k when k = KindSchemaChangedV3 ->
        let db = str ()
        SchemaChanged(db, decodeStatement tableCommentSnapshotFormat r)
    | k when k = KindSchemaChangedAtV3 ->
        let db = str ()
        SchemaChangedAt(db, decodeStatement tableCommentSnapshotFormat r, DateTime(r.ReadInt64LE()))
    | k when k = KindSchemaChangedV4 ->
        let db = str ()
        SchemaChanged(db, decodeStatement numericDisplaySnapshotFormat r)
    | k when k = KindSchemaChangedAtV4 ->
        let db = str ()
        SchemaChangedAt(db, decodeStatement numericDisplaySnapshotFormat r, DateTime(r.ReadInt64LE()))
    | k when k = KindSchemaChangedV5 ->
        let db = str ()
        SchemaChanged(db, decodeStatement currentSnapshotFormat r)
    | k when k = KindSchemaChangedAtV5 ->
        let db = str ()
        SchemaChangedAt(db, decodeStatement currentSnapshotFormat r, DateTime(r.ReadInt64LE()))
    | k when k = KindXaPrepared ->
        let xid = decodeXid r
        let validateWholeSnapshot = readBool r
        let events = List.init (r.ReadInt32LE()) (fun _ -> decodeEventAt (depth + 1) r)
        XaPrepared(xid, validateWholeSnapshot, events)
    | k when k = KindXaCommitted ->
        let xid = decodeXid r
        XaCommitted(xid, List.init (r.ReadInt32LE()) (fun _ -> decodeEventAt (depth + 1) r))
    | k when k = KindXaRolledBack ->
        XaRolledBack(decodeXid r)
    | tag -> failwithf "Persistence: unknown WAL event kind 0x%02x" tag

let private decodeEvent (r: #IReader) : CommitEvent = decodeEventAt 0 r

/// Encodes `[int32 payload length][uint32 crc32][payload]`.
let encodeWalRecord (event: CommitEvent) : byte[] =
    let payloadWriter = Writer()
    encodeEvent payloadWriter event
    let payload = payloadWriter.ToArray()

    let frame = Writer()
    frame.WriteInt32LE payload.Length
    frame.WriteUInt32LE(crc32 payload)
    frame.WriteBytes payload
    frame.ToArray()

// Replay preserves physical row values and routes DDL through Storage.

let private warn (context: string) (result: Result<'a, StorageError>) : unit =
    match result with
    | Ok _ -> ()
    | Error e -> Log.diagnostic "fsdb: WAL replay warning (%s): %A" context e

let private applyDdl (store: Store) (db: string) (stmt: Statement) : unit =
    match stmt with
    | CreateDatabase(name, _, _) -> warn "CreateDatabase" (createDatabase store name)
    | DropDatabase(name, _) -> warn "DropDatabase" (dropDatabase store name)
    | CreateTable table ->
        warn
            "CreateTable"
            (createTableSeeded
                store
                db
                table.Name
                table.Columns
                table.Indexes
                table.ForeignKeys
                table.Charset
                table.Collation
                table.AutoIncrementSeed
                table.Comment
                table.Partitioning)
    | DropTable(names, _) -> names |> List.iter (fun n -> warn "DropTable" (dropTable store db n))
    | AlterTable(table, actions) ->
        // Replay non-strict, whatever the store's current mode: MODIFY/
        // CHANGE re-coerce existing rows, and a logged ALTER already
        // succeeded once — if it ran strict, every value was in range, so
        // non-strict re-coercion is the identity; if it ran non-strict, the
        // clamping replays identically. Replaying strict instead would
        // reject (and silently skip) an ALTER that clamped values.
        let saved = store.ExecutionSettings.SqlMode.Strict
        setStrictMode store false
        warn "AlterTable" (alterTable store db table actions)
        setStrictMode store saved
    // One catalog swap for the whole event, matching how it was logged (see
    // `Storage.renameTables`) — replaying pair-by-pair would reintroduce the
    // partial rename the single event exists to prevent.
    | RenameTable pairs -> warn "RenameTable" (renameTables store db pairs)
    | Truncate table -> warn "Truncate" (truncate store db table)
    | other -> Log.diagnostic "fsdb: WAL replay warning (SchemaChanged): unexpected statement %A" other

let rec private applyEventAt (depth: int) (store: Store) (event: CommitEvent) : unit =
    if depth > maxDecodeDepth then
        failwith "Persistence: transaction nesting exceeds the apply limit"

    match event with
    | RowsInserted(db, table, rows) ->
        if not rows.IsEmpty then
            appendRowsForReplay store db table rows (Log.diagnostic "fsdb: WAL replay warning: %s")
    | RowsUpdated(db, table, changes) -> updateRowsForReplay store db table changes (Log.diagnostic "fsdb: WAL replay warning: %s")
    | RowsDeleted(db, table, rows) -> deleteRowsForReplay store db table rows (Log.diagnostic "fsdb: WAL replay warning: %s")
    | AutoIncrementAdvanced(db, table, nextId) ->
        warn "AutoIncrementAdvanced" (alterTable store db table [ SetAutoIncrement nextId ])
    | SchemaChanged(db, stmt) -> applyDdl store db stmt
    | SchemaChangedAt(db, stmt, createTime) ->
        applyDdl store db stmt

        match stmt with
        | CreateTable table ->
            setTableCreateTimeForReplay store db table.Name createTime (Log.diagnostic "fsdb: WAL replay warning: %s")
        | Truncate name -> setTableCreateTimeForReplay store db name createTime (Log.diagnostic "fsdb: WAL replay warning: %s")
        | _ -> Log.diagnostic "fsdb: WAL replay warning (SchemaChangedAt): unexpected statement %A" stmt
    | TransactionCommitted events -> events |> List.iter (applyEventAt (depth + 1) store)
    | XaPrepared(xid, validateWholeSnapshot, events) ->
        let baseCatalog = store.Catalog
        let snapshot = beginTransactionSnapshotFromCatalog store baseCatalog
        events |> List.iter (applyEventAt (depth + 1) snapshot)

        store.PreparedXas.[xid] <-
            { BaseCatalog = baseCatalog
              Catalog = snapshot.Catalog
              Events = events
              ValidateWholeSnapshot = validateWholeSnapshot
              TransactionLocks = None }
    | XaCommitted(xid, events) ->
        events |> List.iter (applyEventAt (depth + 1) store)
        store.PreparedXas.TryRemove xid |> ignore
    | XaRolledBack xid -> store.PreparedXas.TryRemove xid |> ignore

let private applyEvent (store: Store) (event: CommitEvent) : unit = applyEventAt 0 store event

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

// Snapshots share the WAL row codec and publish through an atomic rename.

let private encodeTableMeta (format: SnapshotFormat) (w: Writer) (t: Table) : unit =
    writeStr w t.OriginalName
    w.WriteInt32LE(List.length t.Columns)
    List.iter (encodeColumnDef format w) t.Columns
    w.WriteInt32LE(List.length t.Indexes)
    List.iter (encodeIndexDef w) t.Indexes
    w.WriteInt32LE(List.length t.ForeignKeys)
    List.iter (encodeForeignKeyDef w) t.ForeignKeys
    writeOptStr w t.TableCharset
    writeOptStr w t.TableCollation
    writeStr w t.TableComment
    if format.Partitions then encodePartitioning w t.Partitioning
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
            encodeTableMeta currentSnapshotFormat w table

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

let private decodeTable (format: SnapshotFormat) (r: #IReader) : Table =
    let originalName = readStr r
    let columns = List.init (r.ReadInt32LE()) (fun _ -> decodeColumnDef format r)
    let indexes = List.init (r.ReadInt32LE()) (fun _ -> decodeIndexDef r)
    let fks = List.init (r.ReadInt32LE()) (fun _ -> decodeForeignKeyDef r)
    let tableCharset = readOptStr r
    let tableCollation = readOptStr r
    let tableComment = if format.TableComments then readStr r else ""
    let partitioning = if format.Partitions then decodePartitioning r else None
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
          TableComment = tableComment
          Partitioning = partitioning
          CreateTime = createTime
          RowsArray = RowStore.ofSeq rows
          NextAutoId = nextAutoId
          UniqueIndex = Map.empty
          SecondaryIndex = Map.empty
          SecondaryOrder = Map.empty
          FullTextIndexes = Map.empty }

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

            if s.Read(header, 0, header.Length) <> header.Length || snapshotFormat header |> Option.isNone then
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

let private decodeCatalog (format: SnapshotFormat) (r: #IReader) : Catalog =
    let dbCount = r.ReadInt32LE()

    [ for _ in 1..dbCount ->
          let dbName = readStr r
          let tableCount = r.ReadInt32LE()
          let tables = [ for _ in 1..tableCount -> readStr r, decodeTable format r ] |> Map.ofList
          dbName, tables ]
    |> Map.ofList

/// Writes and fsyncs `.new`, truncates the WAL, then atomically renames the
/// snapshot. Recovery prefers a verified `.new`, preserving either the old
/// snapshot plus WAL or the complete replacement across every crash point.
/// Attached stores serialize this through the commit queue; startup tooling
/// uses the catalog lock before writers exist.
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
    let dataDir = Path.GetFullPath dataDir

    if store.PreparedXas.IsEmpty then
        let checkpoint =
            lock store.CommitLock (fun () ->
                match store.Durability.Sink with
                | Some sink when String.Equals(sink.DataDirectory, dataDir, StringComparison.Ordinal) ->
                    Some(sink.EnqueueCheckpoint())
                | _ -> None)

        match checkpoint with
        | Some wait -> wait ()
        | None -> lock store.Lock (fun () -> writeSnapshotAndTruncate dataDir store.Catalog)

/// Loads a verified snapshot followed by its valid WAL prefix.
let load (dataDir: string) : Store =
    let store = Storage.create ()
    Directory.CreateDirectory dataDir |> ignore
    let snapshotPath = Path.Combine(dataDir, snapshotFileName)
    let newPath = snapshotPath + ".new"
    let walPath = Path.Combine(dataDir, walFileName)

    // Streaming permits snapshots larger than the runtime byte-array limit.
    // Legacy unframed snapshots begin at offset zero; framed versions skip magic.
    let readSnapshot (path: string) : Catalog * SnapshotFormat =
        use s = new FileStream(path, FileMode.Open, FileAccess.Read)
        let header = Array.zeroCreate<byte> snapshotMagic.Length
        let read = s.Read(header, 0, header.Length)
        let format =
            if read = snapshotMagic.Length then
                snapshotFormat header |> Option.defaultValue legacySnapshotFormat
            else
                legacySnapshotFormat

        let start = if read = snapshotMagic.Length && snapshotFormat header |> Option.isSome then int64 snapshotMagic.Length else 0L
        s.Seek(start, SeekOrigin.Begin) |> ignore
        decodeCatalog format (StreamReader(s)), format

    let mutable loadedFormat = None

    let loadSnapshot path =
        let catalog, format = readSnapshot path
        setCatalog store catalog
        loadedFormat <- Some format

    // A torn `.new` cannot supersede the old snapshot and WAL.
    let loadedFromNew =
        File.Exists newPath
        && verifySnapshotIntegrity newPath
        && (try
                loadSnapshot newPath
                true
            with _ ->
                false)

    if loadedFromNew then
        if loadedFormat |> Option.exists (fun format -> not format.DynamicPrivileges) then
            Storage.ensureRootDynamicPrivileges store

        File.WriteAllText(walPath, "")
        File.Move(newPath, snapshotPath, true)
        fsyncDir dataDir
    else
        if File.Exists snapshotPath then
            loadSnapshot snapshotPath

        // Legacy snapshots need mysql.* before replay can apply account events.
        Storage.ensureMysqlSchema store

        if loadedFormat |> Option.exists (fun format -> not format.DynamicPrivileges) then
            Storage.ensureRootDynamicPrivileges store

        // Physical events already passed the commit-time FK policy.
        store.ForeignKeyChecks <- false
        let goodOffset = replayWal store walPath
        store.ForeignKeyChecks <- true

        // Derived indexes are rebuilt once after all physical events land.
        reindexAllForReplay store

        // Discard a torn or invalid WAL suffix after the last valid frame.
        if File.Exists walPath && FileInfo(walPath).Length <> goodOffset then
            use fs = new FileStream(walPath, FileMode.Open, FileAccess.Write)
            fs.SetLength goodOffset

    Storage.ensureMysqlSchema store
    store

/// Appends ordered WAL batches and acknowledges each commit after their shared fsync.
let attach (dataDir: string) (store: Store) : unit =
    let dataDir = Path.GetFullPath dataDir
    Directory.CreateDirectory dataDir |> ignore
    let walPath = Path.Combine(dataDir, walFileName)
    let entryCount = ref 0

    // The replica advances only after fsync, so rotation never snapshots a
    // catalog mutation whose WAL record is not yet durable.
    let replica = Storage.create ()
    setCatalog replica store.Catalog
    replica.ForeignKeyChecks <- false

    let tryRotateFromReplica () =
        if replica.PreparedXas.IsEmpty then
            reindexAllForReplay replica
            writeSnapshotAndTruncate dataDir replica.Catalog
            true
        else
            false

    let rotateFromReplica () = tryRotateFromReplica () |> ignore

    let appendBatch (events: CommitEvent list) =
        let walSize =
            use s = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read)

            for event in events do
                let bytes = encodeWalRecord event
                s.Write(bytes, 0, bytes.Length)

            flushToDisk s
            s.Length

        for event in events do
            // Mirror failure delays snapshot inclusion but cannot invalidate the WAL.
            try
                applyEvent replica event
            with ex ->
                Log.diagnostic "fsdb: WAL mirror apply failed: %s" ex.Message

        entryCount := !entryCount + events.Length

        if
            (walSize > Limits.walRotateBytes || !entryCount > Limits.walRotateEntries)
            && tryRotateFromReplica ()
        then
            entryCount := 0

    let commits =
        GroupCommit.Queue(
            Limits.walGroupCommitQueueCapacity,
            List.collect id >> appendBatch,
            rotateFromReplica
        )

    let fail context (error: exn) =
        // Publication precedes persistence, so continuing would serve a
        // catalog whose durability can no longer be proven.
        Log.diagnostic "fsdb: %s failed, catalog and disk have diverged: %s" context error.Message
        Environment.FailFast(sprintf "fsdb: fatal %s failure: %s" context error.Message, error)
        raise error

    let fatal context action =
        fun () ->
            try
                action ()
            with error ->
                fail context error

    let enqueue context prepare =
        try
            prepare () |> fatal context
        with error ->
            fail context error

    let sink =
        { DataDirectory = dataDir
          Enqueue = fun events -> enqueue "WAL append" (fun () -> commits.Enqueue events)
          EnqueueCheckpoint = fun () -> enqueue "snapshot checkpoint" commits.EnqueueCheckpoint }

    lock store.CommitLock (fun () ->
        match store.Durability.Sink with
        | Some _ -> invalidOp "Durability is already attached to this store."
        | None -> store.Durability.Sink <- Some sink)

    let onShutdown (_: PosixSignalContext) = sink.EnqueueCheckpoint() ()

    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, onShutdown))
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, onShutdown))
