/// Maps declared SQL column types to the metadata carried by MySQL column
/// definition packets.
module Fsdb.ColumnWire

open System
open Fsdb.Ast
open Fsdb.Value

let private withUnsigned unsigned metadata =
    if unsigned then
        { metadata with Flags = metadata.Flags ||| UnsignedFlag }
    else
        metadata

let private numeric metadata =
    { metadata with Flags = metadata.Flags ||| NumFlag }

let private utf8Length length = uint32 length * 4u

let metadataOfType (ty: ColumnType) : ColumnMetadata =
    match ty with
    | TTinyInt unsigned -> { columnMetadata TypeTiny with ColumnLength = 4u } |> withUnsigned unsigned |> numeric
    | TBool -> { columnMetadata TypeTiny with ColumnLength = 1u } |> numeric
    | TSmallInt unsigned -> { columnMetadata TypeShort with ColumnLength = 6u } |> withUnsigned unsigned |> numeric
    | TMediumInt unsigned -> { columnMetadata TypeLong with ColumnLength = 9u } |> withUnsigned unsigned |> numeric
    | TInt unsigned -> { columnMetadata TypeLong with ColumnLength = 11u } |> withUnsigned unsigned |> numeric
    | TBigInt unsigned -> { columnMetadata TypeLongLong with ColumnLength = 20u } |> withUnsigned unsigned |> numeric
    | TBit width -> { columnMetadata TypeBit with ColumnLength = uint32 width; Flags = UnsignedFlag }
    | TChar length -> { columnMetadata TypeString with ColumnLength = utf8Length length }
    | TVarchar length -> { columnMetadata TypeVarString with ColumnLength = utf8Length length }
    | TTinyText -> { columnMetadata TypeBlob with ColumnLength = 255u; Flags = BlobFlag }
    | TText -> { columnMetadata TypeBlob with ColumnLength = 65535u; Flags = BlobFlag }
    | TMediumText -> { columnMetadata TypeBlob with ColumnLength = 16777215u; Flags = BlobFlag }
    | TLongText -> { columnMetadata TypeBlob with ColumnLength = UInt32.MaxValue; Flags = BlobFlag }
    | TBinary length ->
        { columnMetadata TypeString with
            ColumnLength = uint32 length
            Flags = BinaryFlag }
    | TVarBinary length ->
        { columnMetadata TypeVarString with
            ColumnLength = uint32 length
            Flags = BinaryFlag }
    | TTinyBlob -> { columnMetadata TypeBlob with ColumnLength = 255u; Flags = BlobFlag ||| BinaryFlag }
    | TBlob -> { columnMetadata TypeBlob with ColumnLength = 65535u; Flags = BlobFlag ||| BinaryFlag }
    | TMediumBlob -> { columnMetadata TypeBlob with ColumnLength = 16777215u; Flags = BlobFlag ||| BinaryFlag }
    | TLongBlob -> { columnMetadata TypeBlob with ColumnLength = UInt32.MaxValue; Flags = BlobFlag ||| BinaryFlag }
    | TEnum values ->
        { columnMetadata TypeString with
            ColumnLength = values |> List.map String.length |> List.fold max 0 |> utf8Length
            Flags = EnumFlag }
    | TSet values ->
        { columnMetadata TypeString with
            ColumnLength = values |> List.sumBy String.length |> fun n -> utf8Length (n + max 0 (values.Length - 1))
            Flags = SetFlag }
    | TDecimal(precision, scale) ->
        { columnMetadata TypeNewDecimal with
            ColumnLength = uint32 (precision + 2)
            Decimals = byte scale }
        |> numeric
    | TDouble -> { columnMetadata TypeDouble with ColumnLength = 22u; Decimals = 31uy } |> numeric
    | TFloat -> { columnMetadata TypeFloat with ColumnLength = 12u; Decimals = 31uy } |> numeric
    | TDate -> { columnMetadata TypeDate with ColumnLength = 10u; Flags = BinaryFlag }
    | TDateTime fsp ->
        { columnMetadata TypeDateTime with
            ColumnLength = uint32 (if fsp = 0 then 19 else 20 + fsp)
            Flags = BinaryFlag
            Decimals = byte fsp }
    | TTimestamp fsp ->
        { columnMetadata TypeTimestamp with
            ColumnLength = uint32 (if fsp = 0 then 19 else 20 + fsp)
            Flags = BinaryFlag ||| TimestampFlag
            Decimals = byte fsp }
    | TTime fsp ->
        { columnMetadata TypeTime with
            ColumnLength = uint32 (if fsp = 0 then 10 else 11 + fsp)
            Flags = BinaryFlag
            Decimals = byte fsp }
    | TYear ->
        { columnMetadata TypeYear with
            ColumnLength = 4u
            Flags = UnsignedFlag ||| ZeroFillFlag ||| NumFlag }
    | TJson -> { columnMetadata TypeVarString with ColumnLength = UInt32.MaxValue }
    | TGeometry _ ->
        { columnMetadata TypeGeometry with
            ColumnLength = UInt32.MaxValue
            Flags = BlobFlag ||| BinaryFlag }
    | TVector dim ->
        { columnMetadata TypeBlob with
            ColumnLength = uint32 dim * 4u
            Flags = BlobFlag ||| BinaryFlag }

let metadataOfColumn (column: ColumnDef) : ColumnMetadata =
    let metadata = metadataOfType column.Type

    let flags =
        metadata.Flags
        ||| (if column.Nullable then 0us else NotNullFlag)
        ||| (if column.PrimaryKey then PrimaryKeyFlag else 0us)
        ||| (if column.Unique then UniqueKeyFlag else 0us)
        ||| (if column.PrimaryKey || column.Unique then PartKeyFlag else 0us)
        ||| (if column.AutoIncrement then AutoIncrementFlag else 0us)
        ||| (if not column.Nullable && column.Default.IsNone && not column.AutoIncrement && column.Generated.IsNone then NoDefaultValueFlag else 0us)
        ||| (if column.OnUpdateCurrentTimestamp then OnUpdateNowFlag else 0us)

    { metadata with Flags = flags }

let wireTypeOf ty = (metadataOfType ty).TypeId

let resultMetadataOf (column: ColumnDef) : ColumnMetadata option =
    Some(metadataOfColumn column)
