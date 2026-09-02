/// Schemas for InnoDB diagnostic views whose storage subsystems do not exist
/// in fsdb. Their row sets are empty; preserving the MySQL descriptors keeps
/// metadata clients compatible without fabricating buffer, file, or metric data.
module Fsdb.InnoDbMetadata

open Fsdb.Ast
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
      Collation = collation }

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

let private nullableBinary name length =
    column name (TVarBinary length) true None None None

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

let emptyTableDefs : (string * ColumnDef list) list =
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

let private emptyTableNames = emptyTableDefs |> List.map fst |> Set.ofList

let contains name = emptyTableNames.Contains name
