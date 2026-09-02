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
open Fsdb.Sql

/// Session defaults not backed by a live Limits setting.
let defaultVariables: Map<string, string option> =
    Map.ofList
        [ "version", ServerVersion
          "version_comment", "fsdb"
          "version_compile_os", "osx"
          "sql_mode", SqlMode.defaultText
          "character_set_client", "utf8mb4"
          "character_set_connection", "utf8mb4"
          "character_set_results", "utf8mb4"
          "character_set_server", "utf8mb4"
          "collation_connection", "utf8mb4_general_ci"
          "collation_server", "utf8mb4_general_ci"
          "collation_database", "utf8mb4_general_ci"
          "lc_time_names", "en_US"
          "autocommit", "1"
          "system_time_zone", "UTC"
          "time_zone", "SYSTEM"
          "session_track_schema", "ON"
          "session_track_state_change", "OFF"
          "session_track_transaction_info", "OFF"
          "session_track_system_variables",
          "time_zone,autocommit,character_set_client,character_set_results,character_set_connection"
          "auto_increment_increment", "1"
          // mysqldump reads these before setting them.
          "foreign_key_checks", "1"
          "unique_checks", "1"
          "sql_notes", "1"
          "transaction_isolation", "REPEATABLE-READ"
          "transaction_read_only", "0"
          "tx_read_only", "0"
          "lower_case_table_names", "2"
          "have_ssl", "DISABLED"
          "init_connect", ""
          "license", "GPL"
          "group_concat_max_len", "1024"
          "max_sp_recursion_depth", "0"
          "max_heap_table_size", "16777216"
          "tmp_table_size", "16777216"
          "performance_schema", "0"
          "query_cache_size", "0"
          "query_cache_type", "OFF"
          "block_encryption_mode", "aes-128-ecb"
          "default_storage_engine", "InnoDB"
          "event_scheduler", "ON"
          "activate_all_roles_on_login", "OFF"
          "mandatory_roles", ""
          "innodb_buffer_pool_size", "134217728"
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

let private unquoteAccountPart (value: string) =
    let value = value.Trim()

    if value.Length >= 2 && (value.[0] = value.[value.Length - 1]) && (value.[0] = '`' || value.[0] = '\'') then
        let quote = string value.[0]
        value.[1 .. value.Length - 2].Replace(quote + quote, quote)
    else
        value

let tryParseMandatoryRoles (value: string) =
    let value = value.Trim()

    if value = "" then
        Some []
    else
        match Parser.parse ("SET ROLE " + value) with
        | Ok(SetRole(NamedRoles roles)) -> roles |> List.map (fun (name, host) -> Fsdb.Auth.account name host) |> Some
        | _ ->
            match Parser.splitNonEmptyTopLevelCommaSeparatedWithOptions Parser.defaultOptions value with
            | Result.Error _ -> None
            | Result.Ok identities ->
                let roles =
                    identities
                    |> List.map (fun identity ->
                        let separator = identity.LastIndexOf '@'

                        let name, host =
                            if separator < 0 then
                                unquoteAccountPart identity, "%"
                            else
                                unquoteAccountPart identity[.. separator - 1], unquoteAccountPart identity[separator + 1 ..]

                        if name = "" || host = "" then None else Some(Fsdb.Auth.account name host))

                if roles |> List.forall Option.isSome then roles |> List.choose id |> Some else None

/// Applies a global override without changing the issuing session.
let setGlobalVariable (store: Store) (name: string) (value: string option) : unit =
    let name = name.ToLowerInvariant()

    lock store.Lock (fun () ->
        (globalVariablesOf store).[name] <- value

        if name = "mandatory_roles" then
            value
            |> Option.bind tryParseMandatoryRoles
            |> Option.defaultValue []
            |> Fsdb.Auth.setMandatoryRoles store)

/// `None` is unknown; `Some None` is a known variable holding NULL.
let tryGlobalVariable (store: Store) (name: string) : string option option =
    let name = name.ToLowerInvariant()

    match (globalVariablesOf store).TryGetValue name with
    | true, v -> Some v
    | false, _ -> liveDefaults () |> Map.tryFind name

let initialRoles store account =
    let applicable = Fsdb.Auth.applicableRolesForAccount store account

    let activateAll =
        tryGlobalVariable store "activate_all_roles_on_login"
        |> Option.flatten
        |> Option.exists (fun value -> value = "1" || value.Equals("ON", StringComparison.OrdinalIgnoreCase))

    if activateAll then
        applicable
    else
        Fsdb.Auth.defaultRolesForAccount store account
        |> List.filter (fun role -> applicable |> List.exists (Fsdb.Auth.sameAccount role))

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

type HandlerCursorPosition =
    | Unpositioned
    | BeforeFirst
    | AtRow of RowId
    | AfterLast

type TableHandler =
    { Database: string
      Table: string
      Temporary: bool
      CreateTime: DateTime
      Columns: Fsdb.Ast.ColumnDef list
      Indexes: Fsdb.Ast.IndexDef list
      Positions: Map<string, HandlerCursorPosition> }

type TransactionIsolation =
    | ReadUncommitted
    | ReadCommitted
    | RepeatableRead
    | Serializable

type XaAssociationState =
    | Active
    | Idle

type Savepoint =
    { Sequence: int
      BaseCatalog: Catalog
      Catalog: Catalog
      PendingEventCount: int
      RollbackWork: int64 }

type TransportMetrics =
    { mutable BytesReceived: int64
      mutable BytesSent: int64 }

type TransactionTrackingKind =
    | NoTrackedTransaction
    | ExplicitTrackedTransaction
    | ImplicitTrackedTransaction

type TransactionTrackingState =
    { Kind: TransactionTrackingKind
      ReadTransactional: bool
      WroteTransactional: bool
      UnsafeStatement: bool
      SentResultSet: bool
      LockedTables: bool }

type TransactionTracking =
    { State: TransactionTrackingState
      Characteristics: string
      CharacteristicsVersion: int64 }

let private emptyTransactionTrackingState =
    { Kind = NoTrackedTransaction
      ReadTransactional = false
      WroteTransactional = false
      UnsafeStatement = false
      SentResultSet = false
      LockedTables = false }

let private emptyTransactionTracking =
    { State = emptyTransactionTrackingState
      Characteristics = ""
      CharacteristicsVersion = 0L }

/// One open transaction. `Snapshot` is private until COMMIT. `BaseCatalog`
/// distinguishes its concrete changes from visible rows: fixed views retain
/// one base, while statement-scoped isolation replaces both when it refreshes.
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
      NextSavepointSeq: int
      Xa: (Xa.Xid * XaAssociationState) option }

type Session =
    { ConnectionId: int
      /// The selected account's name. `"root"` for a session built directly.
      User: string
      /// The Host column of the account selected during authentication.
      AccountHost: string
      /// Directly granted roles enabled for this connection.
      ActiveRoles: Fsdb.Auth.Account list
      /// An authenticated expired credential restricts this connection to
      /// password-reset statements until one succeeds.
      PasswordExpired: bool
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
      /// Session changes encoded in the successful command's final OK packet.
      SessionStateChanges: SessionStateChange list
      TransactionTracking: TransactionTracking
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
      /// Session-local low-level table cursors keyed by OPEN name or alias.
      TableHandlers: Map<string, TableHandler>
      /// SQL PREPARE names are connection-local strings rather than the
      /// integer ids assigned by COM_STMT_PREPARE.
      TextStatements: Map<string, PreparedStmt>
      /// Active stored-procedure identities, innermost first.
      RoutineStack: (string * string * string) list
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
      StatusCounters: Fsdb.InformationSchema.StatusCounters
      TlsVersion: string option
      TlsCipher: string option
      TransportMetrics: TransportMetrics }

let create (connectionId: int) (store: Store) : Session =
    // New sessions inherit the current GLOBAL values.
    let variables =
        (globalVariablesOf store) |> Seq.fold (fun acc (KeyValue(k, v)) -> Map.add k v acc) (liveDefaults ())

    { ConnectionId = connectionId
      User = "root"
      AccountHost = "%"
      ActiveRoles = initialRoles store (Fsdb.Auth.account "root" "%")
      PasswordExpired = false
      LoginUser = ""
      ClientHost = "localhost"
      Database = None
      Variables = variables
      UserVariables = Map.empty
      // Reference fields stay shared; mutable SQL-mode settings stay per session.
      Store = { store with ExecutionSettings = store.ExecutionSettings }
      TemporaryCatalog = Map.empty
      LastInsertId = 0L
      LastGeneratedId = 0L
      LastRowCount = 0L
      FoundRows = 0UL
      PendingFoundRows = None
      Diagnostics = []
      SessionStateChanges = []
      TransactionTracking = emptyTransactionTracking
      LastResultColumnMetadata = []
      Tx = None
      PendingTransactionReadOnly = None
      PendingTransactionIsolation = None
      Statements = Map.empty
      Cursors = Map.empty
      TableHandlers = Map.empty
      TextStatements = Map.empty
      RoutineStack = []
      NextStmtId = 1
      LongData = Map.empty
      LongDataBytes = 0L
      LongDataOverflow = Set.empty
      CustomFunctions = Fsdb.Functions.empty
      Capabilities = 0u
      MultiStatementsEnabled = false
      StatusCounters = Fsdb.InformationSchema.createStatusCounters ()
      TlsVersion = None
      TlsCipher = None
      TransportMetrics =
        { BytesReceived = 0L
          BytesSent = 0L } }

let clearSessionStateChanges (session: Session) =
    { session with SessionStateChanges = [] }

let private booleanSystemVariables =
    Set.ofList
        [ "autocommit"
          "foreign_key_checks"
          "innodb_file_per_table"
          "local_infile"
          "performance_schema"
          "read_only"
          "restrict_fk_on_non_standard_key"
          "session_track_schema"
          "session_track_state_change"
          "sql_generate_invisible_primary_key"
          "sql_notes"
          "transaction_read_only"
          "tx_read_only"
          "unique_checks" ]

let private sessionTrackValue (name: string) (value: string option) =
    match value with
    | None -> ""
    | Some value when Set.contains name booleanSystemVariables ->
        match value.Trim().ToUpperInvariant() with
        | "0"
        | "OFF"
        | "FALSE" -> "OFF"
        | _ -> "ON"
    | Some value -> value

let private trackingIsEnabled name (session: Session) =
    session.Variables
    |> Map.tryFind name
    |> Option.flatten
    |> Option.exists (fun value ->
        value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ON", StringComparison.OrdinalIgnoreCase))

let private appendSessionStateChanges stateChanged (changes: Protocol.SessionStateChange list) (session: Session) =
    if session.Capabilities &&& ClientSessionTrack = 0u then
        session
    else
        let changes =
            if stateChanged && trackingIsEnabled "session_track_state_change" session then
                changes @ [ Protocol.StateChanged ]
            else
                changes

        { session with SessionStateChanges = session.SessionStateChanges @ changes }

let private trackedSystemVariableNames (session: Session) =
    session.Variables
    |> Map.tryFind "session_track_system_variables"
    |> Option.flatten
    |> Option.defaultValue ""
    |> fun value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> Seq.map _.ToLowerInvariant()
    |> Set.ofSeq

let trackSystemVariableAssignments stateChanged (names: string list) (session: Session) =
    let tracked = trackedSystemVariableNames session
    let tracksAll = Set.contains "*" tracked

    names
    |> List.distinct
    |> List.choose (fun name ->
        let name = name.ToLowerInvariant()

        if tracksAll || Set.contains name tracked then
            session.Variables
            |> Map.tryFind name
            |> Option.map (fun value -> Protocol.SystemVariableChanged(name, sessionTrackValue name value))
        else
            None)
    |> fun changes -> appendSessionStateChanges stateChanged changes session

let trackSchemaAssignment (schema: string) (session: Session) =
    let changes =
        if trackingIsEnabled "session_track_schema" session then
            [ Protocol.SchemaChanged schema ]
        else
            []

    appendSessionStateChanges true changes session

type TransactionTrackingLevel =
    | TransactionTrackingOff
    | TransactionStateOnly
    | TransactionCharacteristics

let private transactionTrackingLevel (session: Session) =
    match session.Variables |> Map.tryFind "session_track_transaction_info" |> Option.flatten with
    | Some value when value.Equals("STATE", StringComparison.OrdinalIgnoreCase) -> TransactionStateOnly
    | Some value when value.Equals("CHARACTERISTICS", StringComparison.OrdinalIgnoreCase) -> TransactionCharacteristics
    | _ -> TransactionTrackingOff

let private renderTransactionState state =
    String(
        [| match state.Kind with
           | ExplicitTrackedTransaction -> 'T'
           | ImplicitTrackedTransaction -> 'I'
           | NoTrackedTransaction -> '_'
           '_'
           if state.ReadTransactional then 'R' else '_'
           '_'
           if state.WroteTransactional then 'W' else '_'
           if state.UnsafeStatement then 's' else '_'
           if state.SentResultSet then 'S' else '_'
           if state.LockedTables then 'L' else '_' |]
    )

let beginTransactionTracking kind characteristics (session: Session) =
    { session with
        TransactionTracking =
            { State = { emptyTransactionTrackingState with Kind = kind }
              Characteristics = characteristics
              CharacteristicsVersion = session.TransactionTracking.CharacteristicsVersion + 1L } }

let endTransactionTracking (session: Session) =
    { session with
        TransactionTracking =
            { emptyTransactionTracking with
                CharacteristicsVersion = session.TransactionTracking.CharacteristicsVersion + 1L } }

let setTransactionCharacteristics characteristics (session: Session) =
    { session with
        TransactionTracking =
            { session.TransactionTracking with
                Characteristics = characteristics
                CharacteristicsVersion = session.TransactionTracking.CharacteristicsVersion + 1L } }

let trackTransactionActivity readTransactional wroteTransactional unsafeStatement (session: Session) =
    let state = session.TransactionTracking.State

    if state.Kind = NoTrackedTransaction && not state.LockedTables then
        session
    else
        { session with
            TransactionTracking =
                { session.TransactionTracking with
                    State =
                        { state with
                            ReadTransactional = state.ReadTransactional || readTransactional
                            WroteTransactional = state.WroteTransactional || wroteTransactional
                            UnsafeStatement = state.UnsafeStatement || unsafeStatement } } }

let trackTransactionResultSet (session: Session) =
    let state = session.TransactionTracking.State

    if state.Kind = NoTrackedTransaction && not state.LockedTables then
        session
    else
        { session with
            TransactionTracking =
                { session.TransactionTracking with
                    State = { state with SentResultSet = true } } }

let trackExplicitTableLocks readTransactional wroteTransactional (session: Session) =
    let implicitTransaction =
        session.Variables
        |> Map.tryFind "autocommit"
        |> Option.flatten
        |> Option.contains "0"

    { session with
        TransactionTracking =
            { session.TransactionTracking with
                State =
                    { emptyTransactionTrackingState with
                        Kind = if implicitTransaction then ImplicitTrackedTransaction else NoTrackedTransaction
                        ReadTransactional = implicitTransaction && readTransactional
                        WroteTransactional = implicitTransaction && wroteTransactional
                        LockedTables = true } } }

let finalizeTransactionTracking (previous: Session) (session: Session) =
    if session.Capabilities &&& ClientSessionTrack = 0u then
        session
    else
        let previousLevel = transactionTrackingLevel previous
        let level = transactionTrackingLevel session
        let previousTracking = previous.TransactionTracking
        let tracking = session.TransactionTracking

        let characteristics =
            if
                level = TransactionCharacteristics
                && (previousLevel <> TransactionCharacteristics
                    || previousTracking.CharacteristicsVersion <> tracking.CharacteristicsVersion)
            then
                [ Protocol.TransactionCharacteristicsChanged tracking.Characteristics ]
            else
                []

        let state =
            if
                level <> TransactionTrackingOff
                && previousTracking.State <> tracking.State
            then
                [ Protocol.TransactionStateChanged(renderTransactionState tracking.State) ]
            else
                []

        { session with SessionStateChanges = session.SessionStateChanges @ characteristics @ state }

/// The catalog store all statements on this session currently execute
/// against: the shared store outside a transaction, or the transaction's
/// private snapshot inside one (see `Transaction`).
let currentStore (session: Session) : Store =
    session.Tx |> Option.map (fun tx -> tx.Snapshot) |> Option.defaultValue session.Store
