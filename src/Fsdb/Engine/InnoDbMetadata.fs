/// InnoDB information-schema compatibility. Dictionary views project fsdb's
/// live catalog; physical diagnostics keep MySQL's descriptors but no rows
/// because fsdb has no buffer-pool, tablespace, compression, or metrics layer.
module Fsdb.InnoDbMetadata

open Fsdb.Ast
open Fsdb.Storage
open Fsdb.Value

let private column name columnType nullable defaultValue charset collation =
    { Name = name
      Type = columnType
      NumericDisplay = None
      Nullable = nullable
      Default = defaultValue
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      OnUpdateCurrentTimestamp = false
      Generated = None
      Comment = ""
      Charset = charset
      Collation = collation
      Srid = None }

let private blank name columnType nullable =
    column name columnType nullable (Some(DConst(VString ""))) None None

let private signedInt name = blank name (TInt false) false
let private unsignedInt name = blank name (TInt true) false
let private signedBigInt name = blank name (TBigInt false) false
let private unsignedBigInt name = blank name (TBigInt true) false

let private float12 name nullable =
    { blank name (TFloat false) nullable with
        NumericDisplay =
            Some
                { Width = Some 12
                  Decimals = Some 0
                  ZeroFill = false } }

let private text name length nullable collation =
    column
        name
        (TVarchar length)
        nullable
        (Some(DConst(VString "")))
        (Some "utf8mb3")
        (Some collation)

let private generalText name length nullable = text name length nullable "utf8mb3_general_ci"
let private binaryText name length nullable = text name length nullable "utf8mb3_bin"

let private internalText name length nullable defaultValue =
    column name (TVarchar length) nullable defaultValue (Some "utf8mb3") (Some "utf8mb3_tolower_ci")

let private nullableBinary name length =
    column name (TVarBinary length) true None None None

let private generalLongText name nullable =
    column name TText nullable (Some(DConst(VString ""))) (Some "utf8mb3") (Some "utf8mb3_general_ci")

let private bufferPageColumns =
    [ unsignedBigInt "POOL_ID"
      unsignedBigInt "BLOCK_ID"
      unsignedBigInt "SPACE"
      unsignedBigInt "PAGE_NUMBER"
      generalText "PAGE_TYPE" 64 true
      unsignedBigInt "FLUSH_TYPE"
      unsignedBigInt "FIX_COUNT"
      generalText "IS_HASHED" 3 true
      unsignedBigInt "NEWEST_MODIFICATION"
      unsignedBigInt "OLDEST_MODIFICATION"
      unsignedBigInt "ACCESS_TIME"
      generalText "TABLE_NAME" 1024 true
      generalText "INDEX_NAME" 1024 true
      unsignedBigInt "NUMBER_RECORDS"
      unsignedBigInt "DATA_SIZE"
      unsignedBigInt "COMPRESSED_SIZE"
      generalText "PAGE_STATE" 64 true
      generalText "IO_FIX" 64 true
      generalText "IS_OLD" 3 true
      unsignedBigInt "FREE_PAGE_CLOCK"
      generalText "IS_STALE" 3 true ]

let private bufferPageLruColumns =
    [ unsignedBigInt "POOL_ID"
      unsignedBigInt "LRU_POSITION"
      unsignedBigInt "SPACE"
      unsignedBigInt "PAGE_NUMBER"
      generalText "PAGE_TYPE" 64 true
      unsignedBigInt "FLUSH_TYPE"
      unsignedBigInt "FIX_COUNT"
      generalText "IS_HASHED" 3 true
      unsignedBigInt "NEWEST_MODIFICATION"
      unsignedBigInt "OLDEST_MODIFICATION"
      unsignedBigInt "ACCESS_TIME"
      generalText "TABLE_NAME" 1024 true
      generalText "INDEX_NAME" 1024 true
      unsignedBigInt "NUMBER_RECORDS"
      unsignedBigInt "DATA_SIZE"
      unsignedBigInt "COMPRESSED_SIZE"
      generalText "COMPRESSED" 3 true
      generalText "IO_FIX" 64 true
      generalText "IS_OLD" 3 true
      unsignedBigInt "FREE_PAGE_CLOCK" ]

let private bufferPoolStatsColumns =
    [ unsignedBigInt "POOL_ID"
      unsignedBigInt "POOL_SIZE"
      unsignedBigInt "FREE_BUFFERS"
      unsignedBigInt "DATABASE_PAGES"
      unsignedBigInt "OLD_DATABASE_PAGES"
      unsignedBigInt "MODIFIED_DATABASE_PAGES"
      unsignedBigInt "PENDING_DECOMPRESS"
      unsignedBigInt "PENDING_READS"
      unsignedBigInt "PENDING_FLUSH_LRU"
      unsignedBigInt "PENDING_FLUSH_LIST"
      unsignedBigInt "PAGES_MADE_YOUNG"
      unsignedBigInt "PAGES_NOT_MADE_YOUNG"
      float12 "PAGES_MADE_YOUNG_RATE" false
      float12 "PAGES_MADE_NOT_YOUNG_RATE" false
      unsignedBigInt "NUMBER_PAGES_READ"
      unsignedBigInt "NUMBER_PAGES_CREATED"
      unsignedBigInt "NUMBER_PAGES_WRITTEN"
      float12 "PAGES_READ_RATE" false
      float12 "PAGES_CREATE_RATE" false
      float12 "PAGES_WRITTEN_RATE" false
      unsignedBigInt "NUMBER_PAGES_GET"
      unsignedBigInt "HIT_RATE"
      unsignedBigInt "YOUNG_MAKE_PER_THOUSAND_GETS"
      unsignedBigInt "NOT_YOUNG_MAKE_PER_THOUSAND_GETS"
      unsignedBigInt "NUMBER_PAGES_READ_AHEAD"
      unsignedBigInt "NUMBER_READ_AHEAD_EVICTED"
      float12 "READ_AHEAD_RATE" false
      float12 "READ_AHEAD_EVICTED_RATE" false
      unsignedBigInt "LRU_IO_TOTAL"
      unsignedBigInt "LRU_IO_CURRENT"
      unsignedBigInt "UNCOMPRESS_TOTAL"
      unsignedBigInt "UNCOMPRESS_CURRENT" ]

let private compressedPageColumns =
    [ signedInt "page_size"
      signedInt "compress_ops"
      signedInt "compress_ops_ok"
      signedInt "compress_time"
      signedInt "uncompress_ops"
      signedInt "uncompress_time" ]

let private compressedIndexColumns =
    [ generalText "database_name" 192 false
      generalText "table_name" 192 false
      generalText "index_name" 192 false
      signedInt "compress_ops"
      signedInt "compress_ops_ok"
      signedInt "compress_time"
      signedInt "uncompress_ops"
      signedInt "uncompress_time" ]

let private compressedMemoryColumns =
    [ signedInt "page_size"
      signedInt "buffer_pool_instance"
      signedInt "pages_used"
      signedInt "pages_free"
      signedBigInt "relocation_ops"
      signedInt "relocation_time" ]

let private fullTextIndexColumns =
    [ generalText "WORD" 337 false
      unsignedBigInt "FIRST_DOC_ID"
      unsignedBigInt "LAST_DOC_ID"
      unsignedBigInt "DOC_COUNT"
      unsignedBigInt "DOC_ID"
      unsignedBigInt "POSITION" ]

let private metricsColumns =
    [ generalText "NAME" 193 false
      generalText "SUBSYSTEM" 193 false
      signedBigInt "COUNT"
      blank "MAX_COUNT" (TBigInt false) true
      blank "MIN_COUNT" (TBigInt false) true
      float12 "AVG_COUNT" true
      signedBigInt "COUNT_RESET"
      blank "MAX_COUNT_RESET" (TBigInt false) true
      blank "MIN_COUNT_RESET" (TBigInt false) true
      float12 "AVG_COUNT_RESET" true
      blank "TIME_ENABLED" (TDateTime 0) true
      blank "TIME_DISABLED" (TDateTime 0) true
      blank "TIME_ELAPSED" (TBigInt false) true
      blank "TIME_RESET" (TDateTime 0) true
      generalText "STATUS" 193 false
      generalText "TYPE" 193 false
      generalText "COMMENT" 193 false ]

let private tablespacesColumns =
    [ unsignedInt "SPACE"
      generalText "NAME" 655 false
      unsignedInt "FLAG"
      generalText "ROW_FORMAT" 22 true
      unsignedInt "PAGE_SIZE"
      unsignedInt "ZIP_PAGE_SIZE"
      generalText "SPACE_TYPE" 10 true
      unsignedInt "FS_BLOCK_SIZE"
      unsignedBigInt "FILE_SIZE"
      unsignedBigInt "ALLOCATED_SIZE"
      unsignedBigInt "AUTOEXTEND_SIZE"
      generalText "SERVER_VERSION" 10 true
      unsignedInt "SPACE_VERSION"
      generalText "ENCRYPTION" 1 true
      generalText "STATE" 10 true ]

let private dictionaryTableDefs : (string * ColumnDef list) list =
    [ "INNODB_COLUMNS",
      [ unsignedBigInt "TABLE_ID"
        generalText "NAME" 193 false
        unsignedBigInt "POS"
        signedInt "MTYPE"
        signedInt "PRTYPE"
        signedInt "LEN"
        signedInt "HAS_DEFAULT"
        generalLongText "DEFAULT_VALUE" true ]
      "INNODB_FIELDS",
      [ nullableBinary "INDEX_ID" 256
        internalText "NAME" 64 false None
        { unsignedBigInt "POS" with Default = Some(DConst(VUInt 0UL)) } ]
      "INNODB_INDEXES",
      [ unsignedBigInt "INDEX_ID"
        generalText "NAME" 193 false
        unsignedBigInt "TABLE_ID"
        signedInt "TYPE"
        signedInt "N_FIELDS"
        signedInt "PAGE_NO"
        signedInt "SPACE"
        signedInt "MERGE_THRESHOLD" ]
      "INNODB_TABLES",
      [ unsignedBigInt "TABLE_ID"
        generalText "NAME" 655 false
        signedInt "FLAG"
        signedInt "N_COLS"
        signedBigInt "SPACE"
        generalText "ROW_FORMAT" 12 true
        unsignedInt "ZIP_PAGE_SIZE"
        generalText "SPACE_TYPE" 10 true
        signedInt "INSTANT_COLS"
        signedInt "TOTAL_ROW_VERSIONS" ]
      "INNODB_TABLESTATS",
      [ unsignedBigInt "TABLE_ID"
        generalText "NAME" 193 false
        generalText "STATS_INITIALIZED" 193 false
        unsignedBigInt "NUM_ROWS"
        unsignedBigInt "CLUST_INDEX_SIZE"
        unsignedBigInt "OTHER_INDEX_SIZE"
        unsignedBigInt "MODIFIED_COUNTER"
        unsignedBigInt "AUTOINC"
        signedInt "REF_COUNT" ]
      "INNODB_VIRTUAL",
      [ unsignedBigInt "TABLE_ID"; unsignedInt "POS"; unsignedInt "BASE_POS" ] ]

let private emptyTableDefs : (string * ColumnDef list) list =
    [ "INNODB_BUFFER_PAGE", bufferPageColumns
      "INNODB_BUFFER_PAGE_LRU", bufferPageLruColumns
      "INNODB_BUFFER_POOL_STATS", bufferPoolStatsColumns
      "INNODB_CACHED_INDEXES",
      [ unsignedInt "SPACE_ID"; unsignedBigInt "INDEX_ID"; unsignedBigInt "N_CACHED_PAGES" ]
      "INNODB_CMP", compressedPageColumns
      "INNODB_CMPMEM", compressedMemoryColumns
      "INNODB_CMPMEM_RESET", compressedMemoryColumns
      "INNODB_CMP_PER_INDEX", compressedIndexColumns
      "INNODB_CMP_PER_INDEX_RESET", compressedIndexColumns
      "INNODB_CMP_RESET", compressedPageColumns
      "INNODB_DATAFILES",
      [ nullableBinary "SPACE" 256
        column "PATH" (TVarchar 512) false None (Some "utf8mb3") (Some "utf8mb3_bin") ]
      "INNODB_FT_BEING_DELETED", [ unsignedBigInt "DOC_ID" ]
      "INNODB_FT_CONFIG", [ generalText "KEY" 193 false; generalText "VALUE" 193 false ]
      "INNODB_FT_DELETED", [ unsignedBigInt "DOC_ID" ]
      "INNODB_FT_INDEX_CACHE", fullTextIndexColumns
      "INNODB_FT_INDEX_TABLE", fullTextIndexColumns
      "INNODB_METRICS", metricsColumns
      "INNODB_SESSION_TEMP_TABLESPACES",
      [ unsignedInt "ID"
        unsignedInt "SPACE"
        generalText "PATH" 4001 false
        unsignedBigInt "SIZE"
        generalText "STATE" 192 false
        generalText "PURPOSE" 192 false ]
      "INNODB_TABLESPACES", tablespacesColumns
      "INNODB_TABLESPACES_BRIEF",
      [ nullableBinary "SPACE" 256
        column "NAME" (TVarchar 268) false None (Some "utf8mb3") (Some "utf8mb3_bin")
        column "PATH" (TVarchar 512) false None (Some "utf8mb3") (Some "utf8mb3_bin")
        nullableBinary "FLAG" 256
        generalText "SPACE_TYPE" 7 false ]
      "INNODB_TEMP_TABLE_INFO",
      [ unsignedBigInt "TABLE_ID"
        generalText "NAME" 64 true
        unsignedInt "N_COLS"
        unsignedInt "SPACE" ] ]

type private TableEntry =
    { Id: uint64
      Database: string
      Table: Table }

type private IndexEntry =
    { Id: uint64
      Table: TableEntry
      Name: string
      KeyColumns: IndexColumn list
      Unique: bool
      Kind: IndexKind
      Primary: bool
      GeneratedCluster: bool }

module private InternalCode =
    [<Literal>]
    let NotNull = 0x100

    [<Literal>]
    let Unsigned = 0x200

    [<Literal>]
    let Binary = 0x400

    [<Literal>]
    let Virtual = 0x2000

    [<Literal>]
    let BinaryCollation = 63

    [<Literal>]
    let DecimalTemporalCollation = 8

    [<Literal>]
    let JsonCollation = 46

    [<Literal>]
    let VirtualPosition = 0x10000

    [<Literal>]
    let IndexIdStride = 1024UL

    let collation id = id <<< 16

let private stableTableId (database: string) (table: Table) =
    let offset = 14695981039346656037UL
    let prime = 1099511628211UL

    let hash =
        (database + "/" + table.OriginalName).ToLowerInvariant()
        |> System.Text.Encoding.UTF8.GetBytes
        |> Array.fold (fun value byte -> (value ^^^ uint64 byte) * prime) offset
        |> fun value -> value &&& 0x003FFFFFFFFFFFFFUL

    if hash = 0UL then 1UL else hash

let private nextUnusedId used candidate =
    let rec loop value =
        if Set.contains value used then loop (value + 1UL) else value

    loop candidate

let private tableEntries (catalog: Catalog) =
    catalog
    |> Map.toList
    |> List.collect (fun (database, tables) ->
        tables |> Map.toList |> List.map (fun (_, table) -> database, table))
    |> List.mapFold
        (fun used (database, table) ->
            let id = stableTableId database table |> nextUnusedId used

            { Id = id
              Database = database
              Table = table },
            Set.add id used)
        Set.empty
    |> fst

let private isVirtualColumn (column: ColumnDef) =
    match column.Generated with
    | Some(_, Virtual) -> true
    | _ -> false

let private indexEntries (tables: TableEntry list) =
    tables
    |> List.collect (fun table ->
        let hasPrimary =
            table.Table.Indexes
            |> List.exists (fun index -> index.Name.Equals("PRIMARY", System.StringComparison.OrdinalIgnoreCase))

        let explicitIndexes =
            table.Table.Indexes
            |> List.map (fun index ->
                index.Name,
                index.KeyColumns,
                index.Unique,
                index.Kind,
                index.Name.Equals("PRIMARY", System.StringComparison.OrdinalIgnoreCase),
                false)

        let indexes =
            if hasPrimary then
                explicitIndexes
            else
                ("GEN_CLUST_INDEX", [], true, BTree, true, true) :: explicitIndexes

        indexes
        |> List.mapi (fun ordinal (name, keyColumns, unique, kind, primary, generatedCluster) ->
            { Id = table.Id * InternalCode.IndexIdStride + uint64 ordinal + 1UL
              Table = table
              Name = name
              KeyColumns = keyColumns
              Unique = unique
              Kind = kind
              Primary = primary
              GeneratedCluster = generatedCluster }))

let private decimalBytes precision scale =
    let compressedDigits = [| 0; 1; 1; 2; 2; 3; 3; 3; 4; 4 |]
    let bytes digits = (digits / 9 * 4) + compressedDigits.[digits % 9]
    bytes (precision - scale) + bytes scale

let private fractionalBytes fsp = (fsp + 1) / 2

let private columnStorageLength (column: ColumnDef) =
    let bytesPerCharacter = Collation.maxBytesPerCharacter column.Charset

    match column.Type with
    | TTinyInt _
    | TBool
    | TYear -> 1
    | TSmallInt _ -> 2
    | TMediumInt _
    | TDate -> 3
    | TInt _
    | TFloat _ -> 4
    | TBigInt _
    | TDouble _ -> 8
    | TBit width -> (width + 7) / 8
    | TChar length
    | TVarchar length -> length * bytesPerCharacter
    | TBinary length
    | TVarBinary length -> length
    | TTinyText
    | TTinyBlob -> 9
    | TText
    | TBlob -> 10
    | TMediumText
    | TMediumBlob -> 11
    | TLongText
    | TLongBlob
    | TJson
    | TGeometry _ -> 12
    | TEnum values -> if values.Length <= 255 then 1 else 2
    | TSet values -> (values.Length + 7) / 8
    | TDecimal(precision, scale, _) -> decimalBytes precision scale
    | TDateTime fsp -> 5 + fractionalBytes fsp
    | TTimestamp fsp -> 4 + fractionalBytes fsp
    | TTime fsp -> 3 + fractionalBytes fsp
    | TVector dimensions -> dimensions * 4

let private columnCollationId (column: ColumnDef) =
    let name =
        column.Collation
        |> Option.defaultWith (fun () -> Collation.defaultNameForCharset (column.Charset |> Option.defaultValue "utf8mb4"))

    Collation.tryId name |> Option.defaultValue 255

let private columnMainType (column: ColumnDef) =
    match column.Type with
    | TChar _ -> if Collation.maxBytesPerCharacter column.Charset = 1 then 2 else 13
    | TVarchar _ -> if Collation.maxBytesPerCharacter column.Charset = 1 then 1 else 12
    | TBinary _
    | TBit _
    | TDecimal _
    | TDateTime _
    | TTimestamp _
    | TTime _ -> 3
    | TVarBinary _
    | TVector _ -> 4
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob
    | TJson -> 5
    | TTinyInt _
    | TBool
    | TSmallInt _
    | TMediumInt _
    | TInt _
    | TBigInt _
    | TEnum _
    | TSet _
    | TDate
    | TYear -> 6
    | TFloat _ -> 9
    | TDouble _ -> 10
    | TGeometry _ -> 14

let private columnPreciseType (column: ColumnDef) =
    let notNull = if column.Nullable then 0 else InternalCode.NotNull
    let virtualColumn = if isVirtualColumn column then InternalCode.Virtual else 0

    let specific =
        match column.Type with
        | TTinyInt isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 1
        | TBool -> InternalCode.Binary + 1
        | TSmallInt isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 2
        | TMediumInt isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 9
        | TInt isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 3
        | TBigInt isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 8
        | TBit _ ->
            InternalCode.collation InternalCode.BinaryCollation
            + InternalCode.Binary
            + InternalCode.Unsigned
            + 16
        | TChar _ -> InternalCode.collation (columnCollationId column) + 254
        | TVarchar _ -> InternalCode.collation (columnCollationId column) + 15
        | TBinary _ -> InternalCode.collation InternalCode.BinaryCollation + InternalCode.Binary + 254
        | TVarBinary _
        | TVector _ -> InternalCode.collation InternalCode.BinaryCollation + InternalCode.Binary + 15
        | TTinyText
        | TText
        | TMediumText
        | TLongText -> InternalCode.collation (columnCollationId column) + 252
        | TTinyBlob
        | TBlob
        | TMediumBlob
        | TLongBlob -> InternalCode.collation InternalCode.BinaryCollation + InternalCode.Binary + 252
        | TEnum _
        | TSet _ -> InternalCode.Unsigned + 254
        | TDecimal(_, _, isUnsigned) ->
            InternalCode.collation InternalCode.DecimalTemporalCollation
            + InternalCode.Binary
            + (if isUnsigned then InternalCode.Unsigned else 0)
            + 246
        | TFloat isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 4
        | TDouble isUnsigned -> InternalCode.Binary + (if isUnsigned then InternalCode.Unsigned else 0) + 5
        | TDate -> InternalCode.Binary + 10
        | TDateTime _ ->
            InternalCode.collation InternalCode.DecimalTemporalCollation + InternalCode.Binary + 12
        | TTimestamp _ ->
            InternalCode.collation InternalCode.DecimalTemporalCollation + InternalCode.Binary + 7
        | TTime _ -> InternalCode.collation InternalCode.DecimalTemporalCollation + InternalCode.Binary + 11
        | TYear -> InternalCode.Binary + InternalCode.Unsigned + 13
        | TJson -> InternalCode.collation InternalCode.JsonCollation + InternalCode.Binary + 245
        | TGeometry _ -> InternalCode.Binary + 255

    specific + notNull + virtualColumn

let private tableName (entry: TableEntry) =
    entry.Database + "/" + entry.Table.OriginalName

let private tableRows (tables: TableEntry list) =
    tables
    |> List.map (fun entry ->
        let storedColumns = entry.Table.Columns |> List.filter (isVirtualColumn >> not)

        [| VUInt entry.Id
           VString(tableName entry)
           VInt 33L
           VInt(int64 storedColumns.Length + 3L)
           VInt(int64 entry.Id)
           VString "Dynamic"
           VUInt 0UL
           VString "Single"
           VInt 0L
           VInt 0L |])

let private columnRows (tables: TableEntry list) =
    tables
    |> List.collect (fun entry ->
        let positioned = entry.Table.Columns |> List.mapi (fun ordinal column -> ordinal, column)
        let stored, virtualColumns = positioned |> List.partition (snd >> isVirtualColumn >> not)

        let rows (columns: (int * ColumnDef) list) position =
            columns
            |> List.mapi (fun physicalPosition (ordinal, column) ->
                [| VUInt entry.Id
                   VString column.Name
                   VUInt(uint64 (position physicalPosition ordinal))
                   VInt(int64 (columnMainType column))
                   VInt(int64 (columnPreciseType column))
                   VInt(int64 (columnStorageLength column))
                   VInt 0L
                   VNull |])

        rows stored (fun physical _ -> physical)
        @ rows virtualColumns (fun _ ordinal -> InternalCode.VirtualPosition + ordinal))

let private indexFieldName (index: IndexEntry) position (column: IndexColumn) =
    match column.Transform with
    | None when column.Name <> "" -> column.Name
    | _ -> sprintf "!hidden!%s!%d!0" index.Name position

let private indexFieldCount (index: IndexEntry) =
    let storedColumns = index.Table.Table.Columns |> List.filter (isVirtualColumn >> not) |> List.length

    if index.GeneratedCluster then
        storedColumns + 3
    elif index.Primary then
        storedColumns + 2
    else
        let primary = Storage.primaryKeyColumns index.Table.Table
        let keyNames = index.KeyColumns |> List.map _.Name |> Set.ofList
        let primarySuffix = primary |> List.filter (keyNames.Contains >> not) |> List.length
        index.KeyColumns.Length + (if primary.IsEmpty then 1 else primarySuffix)

let private indexType (index: IndexEntry) =
    if index.GeneratedCluster then 1
    elif index.Primary then 3
    elif index.Kind = FullTextIndex then 32
    elif index.Unique then 2
    else 0

let private indexRows (indexes: IndexEntry list) =
    indexes
    |> List.map (fun index ->
        [| VUInt index.Id
           VString index.Name
           VUInt index.Table.Id
           VInt(int64 (indexType index))
           VInt(int64 (indexFieldCount index))
           VInt 0L
           VInt(int64 index.Table.Id)
           VInt 50L |])

let private fieldRows (indexes: IndexEntry list) =
    indexes
    |> List.collect (fun index ->
        index.KeyColumns
        |> List.mapi (fun position column ->
            [| VUInt index.Id; VString(indexFieldName index position column); VUInt(uint64 position) |]))

let private tableStatsRows (tables: TableEntry list) =
    tables
    |> List.map (fun entry ->
        let hasAutoIncrement = entry.Table.Columns |> List.exists _.AutoIncrement

        [| VUInt entry.Id
           VString(tableName entry)
           VString "Initialized"
           VUInt(uint64 entry.Table.RowsArray.Count)
           VUInt 0UL
           VUInt 0UL
           VUInt 0UL
           VUInt(if hasAutoIncrement then uint64 entry.Table.NextAutoId else 0UL)
           VInt 1L |])

let private virtualRows (tables: TableEntry list) =
    let equals left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

    tables
    |> List.collect (fun entry ->
        entry.Table.Columns
        |> List.mapi (fun ordinal column -> ordinal, column)
        |> List.collect (fun (ordinal, column) ->
            match column.Generated with
            | Some(expression, Virtual) ->
                Fsdb.Sql.Expression.collect
                    (function
                    | Col name
                    | QualifiedCol(_, name) -> Some name
                    | _ -> None)
                    expression
                |> List.distinctBy _.ToLowerInvariant()
                |> List.choose (fun name ->
                    entry.Table.Columns
                    |> List.tryFindIndex (fun candidate -> equals candidate.Name name)
                    |> Option.map (fun basePosition ->
                        [| VUInt entry.Id
                           VUInt(uint64 (InternalCode.VirtualPosition + ordinal))
                           VUInt(uint64 basePosition) |]))
            | _ -> []))

let tableDefs = dictionaryTableDefs @ emptyTableDefs

let private tableNames = tableDefs |> List.map fst |> Set.ofList
let private emptyTableNames = emptyTableDefs |> List.map fst |> Set.ofList

let contains name = tableNames.Contains name

let tryRows (catalog: Catalog) name =
    if emptyTableNames.Contains name then
        Some []
    else
        let tables = tableEntries catalog

        match name with
        | "INNODB_COLUMNS" -> Some(columnRows tables)
        | "INNODB_FIELDS" -> Some(fieldRows (indexEntries tables))
        | "INNODB_INDEXES" -> Some(indexRows (indexEntries tables))
        | "INNODB_TABLES" -> Some(tableRows tables)
        | "INNODB_TABLESTATS" -> Some(tableStatsRows tables)
        | "INNODB_VIRTUAL" -> Some(virtualRows tables)
        | _ -> None
