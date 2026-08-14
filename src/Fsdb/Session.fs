/// Per-connection session state: current database and session variables.
module Fsdb.Session

open Fsdb.Protocol

/// Session variable defaults good enough to satisfy mysql CLI / PDO on
/// connect. Grows as real clients ask for more `@@vars` / SHOW VARIABLES.
let defaultVariables: Map<string, string> =
    Map.ofList
        [ "version", ServerVersion
          "version_comment", "fsdb"
          "version_compile_os", "osx"
          "sql_mode", "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION"
          "character_set_client", "utf8mb4"
          "character_set_connection", "utf8mb4"
          "character_set_results", "utf8mb4"
          "character_set_server", "utf8mb4"
          "collation_connection", "utf8mb4_general_ci"
          "collation_server", "utf8mb4_general_ci"
          "autocommit", "1"
          "max_allowed_packet", "16777216"
          "system_time_zone", "UTC"
          "time_zone", "SYSTEM"
          "auto_increment_increment", "1"
          "transaction_isolation", "REPEATABLE-READ"
          "lower_case_table_names", "0"
          "have_ssl", "DISABLED"
          "init_connect", ""
          "interactive_timeout", "28800"
          "wait_timeout", "28800"
          "license", "GPL"
          "net_write_timeout", "60"
          "performance_schema", "0"
          "query_cache_size", "0"
          "query_cache_type", "OFF" ]

type Session =
    { ConnectionId: int
      Database: string option
      Variables: Map<string, string> }

let create (connectionId: int) : Session =
    { ConnectionId = connectionId
      Database = None
      Variables = defaultVariables }
