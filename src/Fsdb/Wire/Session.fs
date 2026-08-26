/// Per-connection session state: current database and session variables.
module Fsdb.Session

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Fsdb.Ast
open Fsdb.Diagnostics
open Fsdb.Protocol
open Fsdb.Storage
open Fsdb.Value

/// Session defaults not backed by a live Limits setting.
let defaultVariables: Map<string, string option> =
    Map.ofList
        [ "version", ServerVersion
          "version_comment", "fsdb"
          "version_compile_os", "osx"
          "sql_mode", "STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION"
          "character_set_client", "utf8mb4"
          "character_set_connection", "utf8mb4"
          "character_set_results", "utf8mb4"
          "character_set_server", "utf8mb4"
          "collation_connection", "utf8mb4_general_ci"
          "collation_server", "utf8mb4_general_ci"
          "collation_database", "utf8mb4_general_ci"
          "autocommit", "1"
          "system_time_zone", "UTC"
          "time_zone", "SYSTEM"
          "auto_increment_increment", "1"
          // mysqldump reads these before setting them.
          "foreign_key_checks", "1"
          "unique_checks", "1"
          "sql_notes", "1"
          "transaction_isolation", "REPEATABLE-READ"
          "transaction_read_only", "0"
          "tx_read_only", "0"
          "lower_case_table_names", "0"
          "have_ssl", "DISABLED"
          "init_connect", ""
          "license", "GPL"
          "group_concat_max_len", "1024"
          "max_heap_table_size", "16777216"
          "performance_schema", "0"
          "query_cache_size", "0"
          "query_cache_type", "OFF"
          "block_encryption_mode", "aes-128-ecb"
          "default_storage_engine", "InnoDB"
          "innodb_file_per_table", "ON"
          "read_only", "OFF"
          "restrict_fk_on_non_standard_key", "ON"
          "sql_generate_invisible_primary_key", "OFF" ]
        |> Map.map (fun _ v -> Some v)

/// Recomputes defaults so configured limits and reported values cannot drift.
let private liveDefaults () : Map<string, string option> =
    Limits.variables () |> List.fold (fun m (name, value) -> Map.add name (Some value) m) defaultVariables

/// GLOBAL overrides share Store.Lock identity and expire with the store.
let private globalVariablesByStore =
    ConditionalWeakTable<obj, ConcurrentDictionary<string, string option>>()

let private globalVariablesOf (store: Store) : ConcurrentDictionary<string, string option> =
    globalVariablesByStore.GetValue(store.Lock, (fun _ -> ConcurrentDictionary()))

/// Applies a global override without changing the issuing session.
let setGlobalVariable (store: Store) (name: string) (value: string option) : unit =
    (globalVariablesOf store).[name.ToLowerInvariant()] <- value

/// `None` is unknown; `Some None` is a known variable holding NULL.
let tryGlobalVariable (store: Store) (name: string) : string option option =
    let name = name.ToLowerInvariant()

    match (globalVariablesOf store).TryGetValue name with
    | true, v -> Some v
    | false, _ -> liveDefaults () |> Map.tryFind name

/// Returns defaults overlaid with the store's GLOBAL assignments.
let globalVariablesSnapshot (store: Store) : Map<string, string option> =
    globalVariablesOf store
    |> Seq.fold (fun m (kv: System.Collections.Generic.KeyValuePair<string, string option>) -> Map.add kv.Key kv.Value m) (liveDefaults ())

/// A connection-local prepared statement. Text-probed commands have no AST;
/// parameter types persist because later EXECUTEs may omit them.
type PreparedStmt =
    { Ast: Statement option
      Sql: string
      ParamCount: int
      LastParamTypes: (byte * bool) list option }

/// A materialized read-only result retained between COM_STMT_FETCH calls.
type PreparedCursor =
    { Metadata: ColumnMetadata list
      Rows: string option list array
      Offset: int }

type TransactionIsolation =
    | ReadUncommitted
    | ReadCommitted
    | RepeatableRead
    | Serializable

type Savepoint =
    { Sequence: int
      BaseCatalog: Catalog
      Catalog: Catalog
      PendingEventCount: int }

/// One open transaction. `Snapshot` is private until COMMIT. `BaseCatalog`
/// distinguishes its concrete changes from committed rows: repeatable read
/// retains one base, while read committed replaces both at each statement.
type Transaction =
    { Snapshot: Store
      BaseCatalog: Catalog
      Isolation: TransactionIsolation
      ReadOnly: bool
      /// Set after the first database statement seeds a repeatable-read view.
      Seeded: bool
      /// Sequence numbers preserve establishment order independently of writes.
      Savepoints: Map<string, Savepoint>
      /// Monotonic and never reused after a savepoint is dropped.
      NextSavepointSeq: int }

type Session =
    { ConnectionId: int
      /// The selected account's name. `"root"` for a session built directly.
      User: string
      /// The Host column of the account selected during authentication.
      AccountHost: string
      /// The username the client supplied in its handshake.
      LoginUser: string
      /// The peer address used for `USER()` and `SESSION_USER()`.
      ClientHost: string
      Database: string option
      /// A present key may hold NULL; a missing key is unknown.
      Variables: Map<string, string option>
      /// User variables retain their Value type; missing names read as NULL.
      UserVariables: Map<string, Value>
      /// Shared store; currentStore selects a private transaction snapshot.
      Store: Store
      /// Connection-local tables keyed by their logical database and name.
      TemporaryCatalog: Catalog
      /// OK-packet insert id; explicit values may change it.
      LastInsertId: int64
      /// LAST_INSERT_ID() value; only generated values change it.
      LastGeneratedId: int64
      /// Values exposed by `ROW_COUNT()` and `FOUND_ROWS()` for the previous
      /// statement on this connection.
      LastRowCount: int64
      FoundRows: uint64
      /// Carries SQL_CALC_FOUND_ROWS across execution into `recordResult`,
      /// which would otherwise replace it with the limited result length.
      PendingFoundRows: uint64 option
      /// Conditions from the most recently executed statement.
      Diagnostics: Condition list
      /// Per-column wire descriptors for the latest typed result set.
      LastResultColumnMetadata: ColumnMetadata list
      /// `Some` between BEGIN/START TRANSACTION and COMMIT/ROLLBACK.
      Tx: Transaction option
      PendingTransactionReadOnly: bool option
      PendingTransactionIsolation: TransactionIsolation option
      /// Binary-protocol statements by connection-local id.
      Statements: Map<int, PreparedStmt>
      /// At most one forward-only cursor per prepared statement.
      Cursors: Map<int, PreparedCursor>
      /// SQL PREPARE names are connection-local strings rather than the
      /// integer ids assigned by COM_STMT_PREPARE.
      TextStatements: Map<string, PreparedStmt>
      /// The next id COM_STMT_PREPARE will assign.
      NextStmtId: int
      /// Bytes buffered by COM_STMT_SEND_LONG_DATA, keyed by (statement id,
      /// param index), newest chunk first so each arrival is constant-time.
      /// EXECUTE reverses and concatenates once, then clears the chunks.
      LongData: Map<int * int, byte[] list>
      /// Total bytes held in `LongData` for constant-time limit checks.
      LongDataBytes: int64
      /// Overflows surface on EXECUTE because SEND_LONG_DATA has no reply.
      LongDataOverflow: Set<int * int>
      /// Embedding functions layered over built-ins by QueryHandler.
      CustomFunctions: Fsdb.Functions.Registry
      /// Effective handshake capabilities.
      Capabilities: uint32
      MultiStatementsEnabled: bool
      TlsVersion: string option
      TlsCipher: string option }

let create (connectionId: int) (store: Store) : Session =
    // New sessions inherit the current GLOBAL values.
    let variables =
        (globalVariablesOf store) |> Seq.fold (fun acc (KeyValue(k, v)) -> Map.add k v acc) (liveDefaults ())

    { ConnectionId = connectionId
      User = "root"
      AccountHost = "%"
      LoginUser = ""
      ClientHost = "localhost"
      Database = None
      Variables = variables
      UserVariables = Map.empty
      // Reference fields stay shared; mutable SQL-mode settings stay per session.
      Store = { store with StrictMode = store.StrictMode }
      TemporaryCatalog = Map.empty
      LastInsertId = 0L
      LastGeneratedId = 0L
      LastRowCount = 0L
      FoundRows = 0UL
      PendingFoundRows = None
      Diagnostics = []
      LastResultColumnMetadata = []
      Tx = None
      PendingTransactionReadOnly = None
      PendingTransactionIsolation = None
      Statements = Map.empty
      Cursors = Map.empty
      TextStatements = Map.empty
      NextStmtId = 1
      LongData = Map.empty
      LongDataBytes = 0L
      LongDataOverflow = Set.empty
      CustomFunctions = Fsdb.Functions.empty
      Capabilities = 0u
      MultiStatementsEnabled = false
      TlsVersion = None
      TlsCipher = None }

/// The catalog store all statements on this session currently execute
/// against: the shared store outside a transaction, or the transaction's
/// private snapshot inside one (see `Transaction`).
let currentStore (session: Session) : Store =
    session.Tx |> Option.map (fun tx -> tx.Snapshot) |> Option.defaultValue session.Store
