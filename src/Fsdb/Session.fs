/// Per-connection session state: current database and session variables.
module Fsdb.Session

open Fsdb.Protocol
open Fsdb.Storage

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

/// A server-side prepared statement (COM_STMT_PREPARE / COM_STMT_EXECUTE):
/// the SQL text as given, `?` placeholders and all. Execution substitutes
/// bound parameters back into this text as SQL literals and re-parses (see
/// `QueryHandler.substitutePlaceholders`) — a typed plan tree would avoid
/// the double-parse, but the parser runs in well under a millisecond for
/// anything Laravel throws at it, and reusing COM_QUERY's own execution
/// path beats keeping two in sync. ponytail: textual substitution instead
/// of a typed plan; revisit if EXECUTE-heavy workloads ever make the
/// reparse show up in a profile.
///
/// `LastParamTypes` caches the (type id, unsigned) pairs from the most
/// recent EXECUTE that actually sent them — COM_STMT_EXECUTE's
/// new-params-bound-flag lets a client omit them on a later EXECUTE and
/// reuse what it sent before.
type PreparedStmt =
    { Sql: string
      ParamCount: int
      LastParamTypes: (byte * bool) list option }

/// One open transaction. `Snapshot` is a private `Store` — its own
/// `Catalog`, its own lock — seeded from the shared store's catalog at
/// BEGIN time; every statement inside the transaction reads/writes this
/// snapshot instead of the shared store, so concurrent connections see
/// nothing from it until COMMIT copies `Snapshot.Catalog` back over the
/// shared store's. That's real repeatable-read isolation for the reading
/// side. ponytail: COMMIT is last-writer-wins on the *whole* catalog, not a
/// per-table/per-row merge — a concurrent write against a different table
/// by another connection during the transaction's lifetime is silently
/// lost when this transaction commits. Cheap because `Store`'s fields are
/// already public and mutable (no hook needed in Storage.fs); a real MVCC
/// engine with write-write conflict detection is the upgrade path if
/// concurrent-transaction data loss ever bites.
type Transaction =
    { Snapshot: Store
      Savepoints: Map<string, Catalog> }

type Session =
    { ConnectionId: int
      Database: string option
      Variables: Map<string, string>
      /// The single shared catalog every connection reads/writes through —
      /// `Session` itself stays an immutable per-connection value; `Store`
      /// is the one mutable boundary (see `Storage.Store`). Always the
      /// shared store, even inside a transaction — use `currentStore` to
      /// get the store statements should actually run against.
      Store: Store
      /// The AUTO_INCREMENT id assigned by this session's most recent
      /// INSERT, for `LAST_INSERT_ID()`. 0 until the first such INSERT.
      LastInsertId: int64
      /// `Some` between BEGIN/START TRANSACTION and COMMIT/ROLLBACK.
      Tx: Transaction option
      /// Prepared statements registered by this connection's COM_STMT_PREPARE
      /// calls, by statement id. Threaded through the connection loop like
      /// the rest of `Session` rather than a mutable dict at the `Server`
      /// boundary — every other piece of per-connection state (database,
      /// variables, transaction) already lives here as plain immutable data,
      /// and a prepared statement is exactly that: state scoped to one
      /// connection, not shared across them.
      Statements: Map<int, PreparedStmt>
      /// The next id COM_STMT_PREPARE will assign.
      NextStmtId: int
      /// Bytes buffered by COM_STMT_SEND_LONG_DATA, keyed by (statement id,
      /// param index), appended to on each call and consumed (then cleared)
      /// by the next COM_STMT_EXECUTE or COM_STMT_RESET for that statement.
      LongData: Map<int * int, byte[]> }

let create (connectionId: int) (store: Store) : Session =
    { ConnectionId = connectionId
      Database = None
      Variables = defaultVariables
      Store = store
      LastInsertId = 0L
      Tx = None
      Statements = Map.empty
      NextStmtId = 1
      LongData = Map.empty }

/// The catalog store all statements on this session currently execute
/// against: the shared store outside a transaction, or the transaction's
/// private snapshot inside one (see `Transaction`).
let currentStore (session: Session) : Store =
    session.Tx |> Option.map (fun tx -> tx.Snapshot) |> Option.defaultValue session.Store
