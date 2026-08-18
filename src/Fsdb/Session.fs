/// Per-connection session state: current database and session variables.
module Fsdb.Session

open System
open Fsdb.Ast
open Fsdb.Protocol
open Fsdb.Storage

/// Session variable defaults good enough to satisfy mysql CLI / PDO on
/// connect. Grows as real clients ask for more `@@vars` / SHOW VARIABLES.
let defaultVariables: Map<string, string option> =
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
          // mysqldump's preamble reads these three before ever `SET`-ing
          // anything (`SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS`,
          // same for unique_checks/sql_notes) — real defaults, so a fresh
          // session already knows them instead of only picking them up
          // once something sets them.
          "foreign_key_checks", "1"
          "unique_checks", "1"
          "sql_notes", "1"
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
        |> Map.map (fun _ v -> Some v)

/// A server-side prepared statement (COM_STMT_PREPARE / COM_STMT_EXECUTE).
/// `Ast` is the parsed statement for everything the grammar produces —
/// EXECUTE binds parameters as `Value`s into it (see
/// `QueryHandler.bindPlaceholders`) instead of splicing SQL literals back
/// into text and re-parsing. It's `None` for the text-probed forms the
/// grammar doesn't produce (SET/SHOW/transaction control), which still
/// substitute into `Sql` and re-probe.
///
/// `LastParamTypes` caches the (type id, unsigned) pairs from the most
/// recent EXECUTE that actually sent them — COM_STMT_EXECUTE's
/// new-params-bound-flag lets a client omit them on a later EXECUTE and
/// reuse what it sent before.
type PreparedStmt =
    { Ast: Statement option
      Sql: string
      ParamCount: int
      LastParamTypes: (byte * bool) list option }

/// One open transaction. `Snapshot` is a private `Store` — its own
/// `Catalog`, its own lock — seeded from the shared store's catalog when the
/// transaction executes its first real database statement; every statement
/// inside the transaction reads/writes this
/// snapshot instead of the shared store, so concurrent connections see
/// nothing from it until COMMIT merges `Snapshot.Catalog` back into the
/// shared store's (see `QueryHandler.commitSession`). That's real
/// repeatable-read isolation for the reading side. `BaseCatalog` is that
/// same seed, kept alongside `Snapshot` untouched by any write this
/// transaction makes — COMMIT diffs `Snapshot.Catalog` against it,
/// table-by-table, to tell "this transaction wrote table X" apart from
/// "table X just happened to be in the snapshot", so a concurrent write to
/// an *untouched* table survives the commit instead of being silently
/// overwritten by a stale copy. `GateLease` currently serializes active
/// transaction lifetimes from that first statement through COMMIT/ROLLBACK,
/// so two same-table writers cannot reach the merger concurrently. This is
/// correct but deliberately coarse; row-version conflict detection/MVCC is
/// the upgrade path for parallel write throughput.
type Transaction =
    { Snapshot: Store
      BaseCatalog: Catalog
      /// Every database's write gate this transaction currently holds,
      /// keyed by database name — not just `session.Database`: a qualified
      /// `INSERT/UPDATE INTO otherdb.t` needs `otherdb`'s own gate too, or a
      /// concurrent writer to `otherdb` can race this transaction's merge
      /// back at COMMIT (see `Storage.mergeDatabaseSlot`'s doc). Acquired
      /// lazily, one database at a time as each statement in the
      /// transaction first names it, then all held until COMMIT/ROLLBACK.
      /// Empty immediately after BEGIN lets multiple connections begin
      /// concurrently and matches InnoDB's default behavior of establishing
      /// a consistent snapshot on the first statement rather than the BEGIN
      /// packet.
      GateLease: Map<string, IDisposable>
      /// Each savepoint's catalog plus how many events `Snapshot.PendingEvents`
      /// had buffered at that point — `ROLLBACK TO SAVEPOINT` truncates the
      /// buffer back to that length too, so a physical WAL never sees events
      /// for writes the savepoint rollback just undid.
      Savepoints: Map<string, Catalog * int> }

type Session =
    { ConnectionId: int
      Database: string option
      /// Real, known system variables. `string option` per value (not just
      /// `string`) distinguishes a variable MySQL accepts NULL for (e.g.
      /// `SET character_set_results = NULL`) from one holding an ordinary
      /// string — the map lookup's own `None` for a key that isn't in the
      /// map at all still means "unknown variable" (1193), same as before;
      /// only a *present* key can hold `None`. See `UserVariables` below
      /// for the analogous convention on user-defined variables.
      Variables: Map<string, string option>
      /// `SET @name = ...` user-defined variables — unlike `Variables`
      /// (real, known system variables), any `@name` is legal in real
      /// MySQL, so there's no default set and no "unknown variable" error
      /// path for these. `string option` per value (not just `string`)
      /// distinguishes "SET to NULL" from the map lookup itself already
      /// telling `None` apart from `Some` — both mean "reads back as
      /// NULL", so callers reading a value back collapse the two with
      /// `Option.flatten` rather than needing to branch on which.
      UserVariables: Map<string, string option>
      /// The single shared catalog every connection reads/writes through —
      /// `Session` itself stays an immutable per-connection value; `Store`
      /// is the one mutable boundary (see `Storage.Store`). Always the
      /// shared store, even inside a transaction — use `currentStore` to
      /// get the store statements should actually run against.
      Store: Store
      /// The OK packet's `last_insert_id` (what `PDO::lastInsertId()`/
      /// `mysql_insert_id()` read) for this session's most recent statement:
      /// the first AUTO_INCREMENT id it generated, or else the last one it
      /// explicitly supplied. 0 until the first INSERT that assigns either.
      /// See `LastGeneratedId` for the narrower value the SQL function
      /// `LAST_INSERT_ID()` reads — the two diverge for an INSERT that
      /// supplies its own id instead of letting AUTO_INCREMENT generate one.
      LastInsertId: int64
      /// The AUTO_INCREMENT id actually *generated* (never explicitly
      /// supplied) by this session's most recent statement that generated
      /// one, for the SQL function `LAST_INSERT_ID()`. 0 until the first
      /// such INSERT; unlike `LastInsertId`, a statement that generates
      /// none — including one that supplies its own id — leaves this
      /// unchanged rather than resetting it, matching real MySQL.
      LastGeneratedId: int64
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
      UserVariables = Map.empty
      Store = store
      LastInsertId = 0L
      LastGeneratedId = 0L
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
