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
/// nothing from it until COMMIT merges `Snapshot.Catalog` back into the
/// shared store's (see `QueryHandler.commitSession`). That's real
/// repeatable-read isolation for the reading side. `BaseCatalog` is that
/// same seed, kept alongside `Snapshot` untouched by any write this
/// transaction makes — COMMIT diffs `Snapshot.Catalog` against it,
/// table-by-table, to tell "this transaction wrote table X" apart from
/// "table X just happened to be in the snapshot", so a concurrent write to
/// an *untouched* table by another connection survives the commit instead
/// of being silently overwritten by a stale copy of it. ponytail: last-
/// writer-wins per table, not per row — a concurrent write to the *same*
/// table this transaction also wrote is still overwritten by whichever
/// commits second; real MVCC write-write conflict detection is the upgrade
/// path if that ever bites.
type Transaction =
    { Snapshot: Store
      BaseCatalog: Catalog
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
      /// Per-column MySQL wire types for the most recent statement's
      /// `ResultSet`, if any — `[]` for anything else (an `Affected`/`Err`
      /// result, or a `ResultSet` this session's dispatch path didn't
      /// bother typing, e.g. `SHOW ...`/session-variable probes). `Server`
      /// reads this right after `QueryHandler.handle` to build the
      /// resultset's column-definition packets; threaded through `Session`
      /// like `LastInsertId` instead of widening `handle`'s own return
      /// type, since dozens of tests destructure `QueryHandler.handle`'s
      /// plain `Session * QueryResult` pair directly.
      LastResultColumnTypes: byte list
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
      LongData: Map<int * int, byte[]>
      /// Custom functions registered on the embedding `Db` this session's
      /// connection was accepted on (see `Fsdb.Db.registerScalar`/
      /// `registerAggregate`) — empty for a session built directly (every
      /// test). `QueryHandler.registryFor` layers these over the built-ins,
      /// under session-bound overrides like `DATABASE()`.
      CustomFunctions: Fsdb.Functions.Registry }

let create (connectionId: int) (store: Store) : Session =
    { ConnectionId = connectionId
      Database = None
      Variables = defaultVariables
      Store = store
      LastInsertId = 0L
      LastResultColumnTypes = []
      Tx = None
      Statements = Map.empty
      NextStmtId = 1
      LongData = Map.empty
      CustomFunctions = Fsdb.Functions.empty }

/// The catalog store all statements on this session currently execute
/// against: the shared store outside a transaction, or the transaction's
/// private snapshot inside one (see `Transaction`).
let currentStore (session: Session) : Store =
    session.Tx |> Option.map (fun tx -> tx.Snapshot) |> Option.defaultValue session.Store
