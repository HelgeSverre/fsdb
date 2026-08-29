/// Host-qualified accounts, mysql_native_password, and privilege policy.
module Fsdb.Auth

open System
open System.Collections.Generic
open System.Net
open System.Security.Cryptography
open System.Text.Json
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Sql
open Fsdb.Engine

let private sha1 (bytes: byte[]) : byte[] = SHA1.HashData bytes

/// The stored mysql_native_password hash for a plaintext password:
/// `'*' + uppercase hex SHA1(SHA1(password))` — what `IDENTIFIED BY` writes
/// into `mysql.user.authentication_string`.
let nativePasswordHash (password: string) : string =
    "*" + Convert.ToHexString(sha1 (sha1 (Text.Encoding.UTF8.GetBytes password)))

/// Verifies a client's mysql_native_password challenge answer.
/// The client sends `SHA1(pw) XOR SHA1(scramble + SHA1(SHA1(pw)))` (20
/// bytes); XORing with `SHA1(scramble + stage2)` recovers `SHA1(pw)`, whose
/// SHA1 must equal the stored `stage2 = SHA1(SHA1(pw))`.
let verifyNative (storedHash: string) (scramble: byte[]) (response: byte[]) : bool =
    if response.Length <> 20 then
        false
    else
        try
            let stage2 = Convert.FromHexString(storedHash.TrimStart '*')
            let mask = sha1 (Array.append scramble stage2)
            let stage1 = Array.map2 (^^^) response mask
            CryptographicOperations.FixedTimeEquals(sha1 stage1, stage2)
        with _ ->
            false

type Account =
    { Name: string
      Host: string }

let private canonicalHost (host: string) =
    match IPAddress.TryParse host with
    | true, address -> address.ToString()
    | _ -> host

let account name host = { Name = name; Host = canonicalHost host }

let formatAccount account = account.Name + "@" + account.Host

let internal tryParseAccount (identity: string) =
    if identity = "" then
        None
    else
        let separator = identity.LastIndexOf '@'

        if separator < 0 then
            Some(account identity "%")
        else
            Some(account identity[.. separator - 1] identity[(separator + 1) ..])

/// Whether two account names identify the same host-qualified account.
let sameAccount left right =
    left.Name = right.Name && String.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)

let private mandatoryRolesByStore =
    System.Runtime.CompilerServices.ConditionalWeakTable<obj, Account list ref>()

let mandatoryRoles (store: Store) =
    let roles = mandatoryRolesByStore.GetValue(store.Lock, fun _ -> ref [])
    lock roles (fun () -> roles.Value)

let setMandatoryRoles (store: Store) roles =
    let roles =
        roles
        |> List.map (fun role -> account role.Name role.Host)
        |> List.distinctBy (fun role -> role.Name, role.Host.ToLowerInvariant())

    let stored = mandatoryRolesByStore.GetValue(store.Lock, fun _ -> ref [])
    lock stored (fun () -> stored.Value <- roles)

let isMandatoryRole store wanted =
    mandatoryRoles store |> List.exists (sameAccount wanted)

let private mandatoryRoleError role =
    Error(
        3628,
        sprintf
            "The role `%s`@`%s` is a mandatory role and can't be revoked or dropped. The restriction can be lifted by excluding the role identifier from the global variable mandatory_roles."
            role.Name
            role.Host
    )

let private rowAccount (cols: ColumnDef list) (row: Value[]) =
    match resolveColumn cols "User", resolveColumn cols "Host" with
    | Ok userIndex, Ok hostIndex ->
        match row.[userIndex], row.[hostIndex] with
        | VString name, VString host -> Some(account name host)
        | _ -> None
    | _ -> None

/// Reads one exact account from the live catalog.
let tryUserRowForAccount (store: Store) (wanted: Account) : (ColumnDef list * Value[]) option =
    match scanList store "mysql" "user" with
    | Error _ -> None
    | Ok(cols, rows) ->
        rows
        |> List.tryFind (fun row -> rowAccount cols row |> Option.exists (sameAccount wanted))
        |> Option.map (fun row -> cols, row)

/// A compatibility lookup for in-process callers without an account host.
/// It prefers the conventional `'name'@'%'` row before any same-name row.
let tryUserRow (store: Store) (username: string) =
    tryUserRowForAccount store (account username "%")
    |> Option.orElseWith (fun () ->
        match scanList store "mysql" "user" with
        | Ok(cols, rows) ->
            rows
            |> List.tryFind (fun row -> rowAccount cols row |> Option.exists (fun selected -> selected.Name = username))
            |> Option.map (fun row -> cols, row)
        | Error _ -> None)

let private isLoopbackHost (host: string) =
    match IPAddress.TryParse host with
    | true, address -> IPAddress.IsLoopback address
    | _ -> false

let private wildcardMatch (pattern: string) (value: string) =
    let seen = Dictionary<int * int, bool>()

    let rec matches patternIndex valueIndex =
        match seen.TryGetValue((patternIndex, valueIndex)) with
        | true, answer -> answer
        | _ ->
            let answer =
                if patternIndex = pattern.Length then
                    valueIndex = value.Length
                else
                    match pattern.[patternIndex] with
                    | '\\' when patternIndex + 1 < pattern.Length ->
                        valueIndex < value.Length
                        && Char.ToUpperInvariant pattern.[patternIndex + 1] = Char.ToUpperInvariant value.[valueIndex]
                        && matches (patternIndex + 2) (valueIndex + 1)
                    | '_' -> valueIndex < value.Length && matches (patternIndex + 1) (valueIndex + 1)
                    | '%' ->
                        let next =
                            let mutable index = patternIndex + 1
                            while index < pattern.Length && pattern.[index] = '%' do
                                index <- index + 1
                            index

                        matches next valueIndex || (valueIndex < value.Length && matches patternIndex (valueIndex + 1))
                    | c ->
                        valueIndex < value.Length
                        && Char.ToUpperInvariant c = Char.ToUpperInvariant value.[valueIndex]
                        && matches (patternIndex + 1) (valueIndex + 1)

            seen.[(patternIndex, valueIndex)] <- answer
            answer

    matches 0 0

let private networkPrefix (pattern: string) (host: string) =
    let slash = pattern.IndexOf '/'

    if slash <= 0 || slash = pattern.Length - 1 then
        None
    else
        match IPAddress.TryParse(pattern[.. slash - 1]), IPAddress.TryParse host with
        | (true, network), (true, address) when network.AddressFamily = address.AddressFamily ->
            let bits = network.GetAddressBytes().Length * 8
            let suffix = pattern[(slash + 1) ..]

            let prefixLength =
                match Int32.TryParse suffix with
                | true, length when length >= 0 && length <= bits -> Some length
                | _ ->
                    match IPAddress.TryParse suffix with
                    | true, mask when mask.AddressFamily = network.AddressFamily ->
                        let maskBits = mask.GetAddressBytes()
                        let mutable prefix = 0
                        let mutable valid = true
                        let mutable seenZero = false

                        for b in maskBits do
                            for shift in 7 .. -1 .. 0 do
                                let set = (int b &&& (1 <<< shift)) <> 0
                                if set && seenZero then valid <- false
                                if set then prefix <- prefix + 1 else seenZero <- true

                        if valid then Some prefix else None
                    | _ -> None

            prefixLength
            |> Option.filter (fun prefix ->
                let networkBytes = network.GetAddressBytes()
                let addressBytes = address.GetAddressBytes()
                let wholeBytes = prefix / 8
                let remainder = prefix % 8
                let sameWholeBytes =
                    wholeBytes = 0 || networkBytes[.. wholeBytes - 1] = addressBytes[.. wholeBytes - 1]

                sameWholeBytes
                && (remainder = 0
                    || (networkBytes.[wholeBytes] &&& byte (0xff <<< (8 - remainder)))
                       = (addressBytes.[wholeBytes] &&& byte (0xff <<< (8 - remainder)))))
        | _ -> None

let private hostMatchRank (pattern: string) (clientHost: string) =
    let rec specificity index literals wildcards =
        if index = pattern.Length then
            literals, wildcards
        else
            match pattern.[index] with
            | '\\' when index + 1 < pattern.Length -> specificity (index + 2) (literals + 1) wildcards
            | '%'
            | '_' -> specificity (index + 1) literals (wildcards + 1)
            | _ -> specificity (index + 1) (literals + 1) wildcards

    let literalCount, wildcardCount = specificity 0 0 0

    if String.Equals(canonicalHost pattern, clientHost, StringComparison.OrdinalIgnoreCase) then
        Some(4, literalCount, 0, pattern.Length)
    else
        match networkPrefix pattern clientHost with
        | Some prefix -> Some(3, prefix, 0, pattern.Length)
        | None when String.Equals(pattern, "localhost", StringComparison.OrdinalIgnoreCase) && isLoopbackHost clientHost ->
            Some(2, literalCount, 0, pattern.Length)
        | None when wildcardMatch pattern clientHost -> Some(1, literalCount, -wildcardCount, pattern.Length)
        | None -> None

/// Selects the account MySQL would authenticate for a peer address. Host
/// specificity takes precedence; a named account breaks ties with anonymous.
let resolveAccount (store: Store) (username: string) (clientHost: string) : (Account * ColumnDef list * Value[]) option =
    let clientHost = canonicalHost clientHost

    match scanList store "mysql" "user" with
    | Error _ -> None
    | Ok(cols, rows) ->
        rows
        |> List.choose (fun row ->
            match rowAccount cols row with
            | Some selected when selected.Name = username || selected.Name = "" ->
                hostMatchRank selected.Host clientHost
                |> Option.map (fun rank -> rank, selected.Name <> "", selected, row)
            | _ -> None)
        |> List.sortByDescending (fun (rank, named, selected, _) -> rank, named, selected.Host)
        |> List.tryHead
        |> Option.map (fun (_, _, selected, row) -> selected, cols, row)

/// A user row's column as text, `""` for NULL/absent.
let userColumnText (cols: ColumnDef list) (row: Value[]) (name: string) : string =
    match resolveColumn cols name with
    | Ok i ->
        match row.[i] with
        | VString s -> s
        | v -> Value.toText v |> Option.defaultValue ""
    | Error _ -> ""

let private userColumnValue (cols: ColumnDef list) (row: Value[]) (name: string) =
    resolveColumn cols name |> Result.toOption |> Option.map (fun index -> row.[index])

let private userColumnUInt32 (cols: ColumnDef list) (row: Value[]) (name: string) =
    match userColumnValue cols row name with
    | Some(VInt value) when value > 0L -> uint32 (min value (int64 UInt32.MaxValue))
    | Some(VUInt value) when value > 0UL -> uint32 (min value (uint64 UInt32.MaxValue))
    | _ -> 0u

/// The stored password hash for a user row — `""` means no password is set.
let storedPasswordHash (cols: ColumnDef list) (row: Value[]) : string = userColumnText cols row "authentication_string"

let accountTlsRequirement (cols: ColumnDef list) (row: Value[]) =
    match (userColumnText cols row "ssl_type").ToUpperInvariant() with
    | "ANY" -> RequireSsl
    | "X509"
    | "SPECIFIED" -> RequireX509
    | _ -> RequireNone

type TransportSecurity =
    { Encrypted: bool
      ClientCertificateValidated: bool }

let transportSatisfiesAccount (transport: TransportSecurity) (cols: ColumnDef list) (row: Value[]) =
    match accountTlsRequirement cols row with
    | RequireNone -> true
    | RequireSsl -> transport.Encrypted
    | RequireX509 -> transport.Encrypted && transport.ClientCertificateValidated

type AccountLimits =
    { MaxQuestions: uint32
      MaxUpdates: uint32
      MaxConnectionsPerHour: uint32
      MaxUserConnections: uint32 }

let accountLimits (cols: ColumnDef list) (row: Value[]) =
    { MaxQuestions = userColumnUInt32 cols row "max_questions"
      MaxUpdates = userColumnUInt32 cols row "max_updates"
      MaxConnectionsPerHour = userColumnUInt32 cols row "max_connections"
      MaxUserConnections = userColumnUInt32 cols row "max_user_connections" }

let tryAccountLimits (store: Store) (account: Account) =
    tryUserRowForAccount store account
    |> Option.map (fun (columns, row) -> accountLimits columns row)

let isPasswordExpiredAt (now: DateTime) (cols: ColumnDef list) (row: Value[]) =
    if userColumnText cols row "password_expired" = "Y" then
        true
    else
        match userColumnValue cols row "password_lifetime", userColumnValue cols row "password_last_changed" with
        | Some(VInt days), Some(VDateTime changed) when days > 0L -> changed.AddDays(float days) <= now
        | Some(VUInt days), Some(VDateTime changed) when days > 0UL -> changed.AddDays(float days) <= now
        | _ -> false

let isPasswordExpired cols row = isPasswordExpiredAt DateTime.Now cols row

let private accountResourceKey (account: Account) =
    account.Name + "\u0000" + account.Host.ToUpperInvariant()

let private resourceUsageAt (store: Store) (account: Account) (now: DateTime) =
    store.AccountResources.GetOrAdd(
        accountResourceKey account,
        fun _ ->
            { Gate = obj ()
              WindowStartedUtc = now
              Questions = 0UL
              Updates = 0UL
              Connections = 0UL
              ActiveConnections = 0u }
    )

let private resetExpiredWindow (now: DateTime) (usage: AccountResourceUsage) =
    if now - usage.WindowStartedUtc >= TimeSpan.FromHours 1.0 then
        usage.WindowStartedUtc <- now
        usage.Questions <- 0UL
        usage.Updates <- 0UL
        usage.Connections <- 0UL

let private clearHourlyUsage now (usage: AccountResourceUsage) =
    usage.WindowStartedUtc <- now
    usage.Questions <- 0UL
    usage.Updates <- 0UL
    usage.Connections <- 0UL

let resetAccountResources (store: Store) (account: Account) =
    match store.AccountResources.TryGetValue(accountResourceKey account) with
    | true, usage -> lock usage.Gate (fun () -> clearHourlyUsage DateTime.UtcNow usage)
    | false, _ -> ()

let resetAllAccountResources (store: Store) =
    let now = DateTime.UtcNow

    for usage in store.AccountResources.Values do
        lock usage.Gate (fun () -> clearHourlyUsage now usage)

let private resourceExceeded account resource limit =
    Error(
        1226,
        sprintf "User '%s' has exceeded the '%s' resource (current value: %u)" account.Name resource limit
    )

type private AccountConnectionLease(usage: AccountResourceUsage) =
    let mutable disposed = false

    interface IDisposable with
        member _.Dispose() =
            lock usage.Gate (fun () ->
                if not disposed then
                    disposed <- true

                    if usage.ActiveConnections > 0u then
                        usage.ActiveConnections <- usage.ActiveConnections - 1u)

let tryAcquireAccountConnectionAt
    (store: Store)
    (account: Account)
    (now: DateTime)
    : Result<IDisposable, int * string> =
    match tryUserRowForAccount store account with
    | None -> Error(1396, sprintf "Operation CONNECT failed for '%s'@'%s'" account.Name account.Host)
    | Some(cols, row) ->
        let limits = accountLimits cols row
        let usage = resourceUsageAt store account now

        lock usage.Gate (fun () ->
            resetExpiredWindow now usage

            if limits.MaxConnectionsPerHour > 0u && usage.Connections >= uint64 limits.MaxConnectionsPerHour then
                resourceExceeded account "max_connections_per_hour" limits.MaxConnectionsPerHour
            elif limits.MaxUserConnections > 0u && usage.ActiveConnections >= limits.MaxUserConnections then
                resourceExceeded account "max_user_connections" limits.MaxUserConnections
            else
                usage.Connections <- usage.Connections + 1UL
                usage.ActiveConnections <- usage.ActiveConnections + 1u
                Ok(new AccountConnectionLease(usage) :> IDisposable))

let tryAcquireAccountConnection store account =
    tryAcquireAccountConnectionAt store account DateTime.UtcNow

let tryConsumeAccountStatementWithLimitsAt
    (store: Store)
    (account: Account)
    (limits: AccountLimits option)
    (isUpdate: bool)
    (now: DateTime)
    : Result<unit, int * string> =
    match limits with
    | None -> Ok()
    | Some limits ->
        if limits.MaxQuestions = 0u && (not isUpdate || limits.MaxUpdates = 0u) then
            Ok()
        else
            let usage = resourceUsageAt store account now

            lock usage.Gate (fun () ->
                resetExpiredWindow now usage

                if limits.MaxQuestions > 0u && usage.Questions >= uint64 limits.MaxQuestions then
                    resourceExceeded account "max_questions" limits.MaxQuestions
                elif isUpdate && limits.MaxUpdates > 0u && usage.Updates >= uint64 limits.MaxUpdates then
                    resourceExceeded account "max_updates" limits.MaxUpdates
                else
                    usage.Questions <- usage.Questions + 1UL

                    if isUpdate then
                        usage.Updates <- usage.Updates + 1UL

                    Ok())

let tryConsumeAccountStatementWithLimits store account limits isUpdate =
    tryConsumeAccountStatementWithLimitsAt store account limits isUpdate DateTime.UtcNow

let tryConsumeAccountStatementAt store account isUpdate now =
    tryConsumeAccountStatementWithLimitsAt store account (tryAccountLimits store account) isUpdate now

let tryConsumeAccountStatement store account isUpdate =
    tryConsumeAccountStatementAt store account isUpdate DateTime.UtcNow

// ---------------------------------------------------------------------------
// The static privilege vocabulary: SQL name ↔ mysql.user column, plus where
// (if anywhere) the privilege exists at db level (mysql.db column) and table
// level (a `tables_priv.Table_priv` SET member). Order matches mysql.user's
// column order — SHOW GRANTS and USER_PRIVILEGES render in this order, same
// as MySQL. GRANT OPTION is deliberately absent (it's `Grant_priv`/the
// `WITH GRANT OPTION` suffix, not a grantable list member). Dynamic
// privileges live in mysql.global_grants instead of mysql.user columns.
// ---------------------------------------------------------------------------

type PrivDef =
    { Sql: string
      UserCol: string
      DbCol: string option
      TablePriv: string option }

let staticPrivileges: PrivDef list =
    let p sql userCol dbCol tablePriv = { Sql = sql; UserCol = userCol; DbCol = dbCol; TablePriv = tablePriv }

    [ p "SELECT" "Select_priv" (Some "Select_priv") (Some "Select")
      p "INSERT" "Insert_priv" (Some "Insert_priv") (Some "Insert")
      p "UPDATE" "Update_priv" (Some "Update_priv") (Some "Update")
      p "DELETE" "Delete_priv" (Some "Delete_priv") (Some "Delete")
      p "CREATE" "Create_priv" (Some "Create_priv") (Some "Create")
      p "DROP" "Drop_priv" (Some "Drop_priv") (Some "Drop")
      p "RELOAD" "Reload_priv" None None
      p "SHUTDOWN" "Shutdown_priv" None None
      p "PROCESS" "Process_priv" None None
      p "FILE" "File_priv" None None
      p "REFERENCES" "References_priv" (Some "References_priv") (Some "References")
      p "INDEX" "Index_priv" (Some "Index_priv") (Some "Index")
      p "ALTER" "Alter_priv" (Some "Alter_priv") (Some "Alter")
      p "SHOW DATABASES" "Show_db_priv" None None
      p "SUPER" "Super_priv" None None
      p "CREATE TEMPORARY TABLES" "Create_tmp_table_priv" (Some "Create_tmp_table_priv") None
      p "LOCK TABLES" "Lock_tables_priv" (Some "Lock_tables_priv") None
      p "EXECUTE" "Execute_priv" (Some "Execute_priv") None
      p "REPLICATION SLAVE" "Repl_slave_priv" None None
      p "REPLICATION CLIENT" "Repl_client_priv" None None
      p "CREATE VIEW" "Create_view_priv" (Some "Create_view_priv") (Some "Create View")
      p "SHOW VIEW" "Show_view_priv" (Some "Show_view_priv") (Some "Show view")
      p "CREATE ROUTINE" "Create_routine_priv" (Some "Create_routine_priv") None
      p "ALTER ROUTINE" "Alter_routine_priv" (Some "Alter_routine_priv") None
      p "CREATE USER" "Create_user_priv" None None
      p "EVENT" "Event_priv" (Some "Event_priv") None
      p "TRIGGER" "Trigger_priv" (Some "Trigger_priv") (Some "Trigger")
      p "CREATE TABLESPACE" "Create_tablespace_priv" None None
      p "CREATE ROLE" "Create_role_priv" None None
      p "DROP ROLE" "Drop_role_priv" None None ]

// Keyed once — `check` looks privileges up per required privilege on every
// enforced statement.
let private privBySqlMap = staticPrivileges |> List.map (fun d -> d.Sql, d) |> dict

let private privBySql (sql: string) : PrivDef option =
    match privBySqlMap.TryGetValue sql with
    | true, d -> Some d
    | _ -> None

// ---------------------------------------------------------------------------
// Account mutations — all through `Storage`'s ordinary row functions so the
// WAL/snapshot carry them like any other data change. Every error shape
// matches MySQL's 1396.
// ---------------------------------------------------------------------------

let private operationFailed (op: string) (name: string) (host: string) =
    Error(1396, sprintf "Operation %s failed for '%s'@'%s'" op name host)

let private sslType = function
    | RequireNone -> ""
    | RequireSsl -> "ANY"
    | RequireX509 -> "X509"

let private initialPasswordExpiration = function
    | Some ExpirePassword -> VString "Y", VNull
    | Some NeverExpirePassword -> VString "N", VInt 0L
    | Some(ExpirePasswordAfterDays days) -> VString "N", VInt(int64 days)
    | Some ExpirePasswordByDefault
    | None -> VString "N", VNull

let private resourceLimitValue value =
    VInt(int64 (Option.defaultValue 0u value))

let createUserWithOptions
    (store: Store)
    (name: string)
    (host: string)
    (password: string option)
    (options: AccountOptions)
    : Result<unit, int * string> =
    let wanted = account name host

    if (tryUserRowForAccount store wanted).IsSome then
        operationFailed "CREATE USER" name host
    else
        let hash = password |> Option.map nativePasswordHash |> Option.defaultValue ""
        let expired, lifetime = initialPasswordExpiration options.PasswordExpiration

        let columns =
            [ "Host"
              "User"
              "plugin"
              "authentication_string"
              "ssl_type"
              "max_questions"
              "max_updates"
              "max_connections"
              "max_user_connections"
              "password_expired"
              "password_last_changed"
              "password_lifetime"
              "account_locked" ]

        let values =
            [ VString wanted.Host
              VString name
              VString "mysql_native_password"
              VString hash
              VString(options.TlsRequirement |> Option.defaultValue RequireNone |> sslType)
              resourceLimitValue options.ResourceLimits.MaxQueriesPerHour
              resourceLimitValue options.ResourceLimits.MaxUpdatesPerHour
              resourceLimitValue options.ResourceLimits.MaxConnectionsPerHour
              resourceLimitValue options.ResourceLimits.MaxUserConnections
              expired
              VDateTime(Functions.truncateToSecond DateTime.Now)
              lifetime
              VString(if Option.defaultValue false options.Locked then "Y" else "N") ]

        match insertRows store "mysql" "user" (Some columns) [ values ] with
        | Ok _ -> Ok()
        | Error error -> Error(toMySqlError error)

let createUserWithTlsRequirement
    (store: Store)
    (name: string)
    (host: string)
    (password: string option)
    (tlsRequirement: AccountTlsRequirement)
    : Result<unit, int * string> =
    createUserWithOptions
        store
        name
        host
        password
        { AccountOptions.empty with TlsRequirement = Some tlsRequirement }

/// `CREATE USER 'name'@'host' [IDENTIFIED BY 'pw']` — one account.
let createUser (store: Store) (name: string) (host: string) (password: string option) : Result<unit, int * string> =
    createUserWithTlsRequirement store name host password RequireNone

/// `DROP USER 'name'@'host'` — removes the account and any of its rows in
/// the other grant tables.
let dropUser (store: Store) (name: string) (host: string) : Result<unit, int * string> =
    let wanted = account name host

    let deleteWhere (table: string) =
        match scanList store "mysql" table with
        | Error _ -> ()
        | Ok(cols, _) ->
            deleteRows store "mysql" table (fun row -> Ok(rowAccount cols row |> Option.exists (sameAccount wanted))) |> ignore

    if (tryUserRowForAccount store wanted).IsNone then
        operationFailed "DROP USER" name host
    elif isMandatoryRole store wanted then
        mandatoryRoleError wanted
    else
        deleteWhere "user"
        deleteWhere "db"
        deleteWhere "tables_priv"
        deleteWhere "columns_priv"
        deleteWhere "global_grants"

        let deleteRoleReferences table accountColumns =
            match scanList store "mysql" table with
            | Error _ -> ()
            | Ok(columns, _) ->
                deleteRows
                    store
                    "mysql"
                    table
                    (fun row ->
                        accountColumns
                        |> List.exists (fun (userColumn, hostColumn) ->
                            sameAccount
                                (account (userColumnText columns row userColumn) (userColumnText columns row hostColumn))
                                wanted)
                        |> Ok)
                |> ignore

        deleteRoleReferences "role_edges" [ "FROM_USER", "FROM_HOST"; "TO_USER", "TO_HOST" ]
        deleteRoleReferences "default_roles" [ "USER", "HOST"; "DEFAULT_ROLE_USER", "DEFAULT_ROLE_HOST" ]
        store.AccountResources.TryRemove(accountResourceKey wanted) |> ignore
        Ok()

let renameUser
    (store: Store)
    (oldName: string)
    (oldHost: string)
    (newName: string)
    (newHost: string)
    : Result<unit, int * string> =
    let oldAccount = account oldName oldHost
    let newAccount = account newName newHost

    let isRoleIdentifier =
        match scanList store "mysql" "role_edges" with
        | Error _ -> false
        | Ok(columns, rows) ->
            match resolveColumn columns "FROM_USER", resolveColumn columns "FROM_HOST" with
            | Ok userIndex, Ok hostIndex ->
                rows
                |> List.exists (fun row ->
                    sameAccount
                        oldAccount
                        (account
                            (Value.toText row.[userIndex] |> Option.defaultValue "")
                            (Value.toText row.[hostIndex] |> Option.defaultValue "")))
            | _ -> false

    if (tryUserRowForAccount store oldAccount).IsNone || (tryUserRowForAccount store newAccount).IsSome then
        operationFailed "RENAME USER" oldName oldHost
    elif isRoleIdentifier then
        Error(3532, "Renaming of a role identifier is forbidden")
    else
        let renameRows table =
            match scanList store "mysql" table with
            | Error error -> Error(toMySqlError error)
            | Ok(columns, _) ->
                match resolveColumn columns "User", resolveColumn columns "Host" with
                | Ok userIndex, Ok hostIndex ->
                    updateRows
                        store
                        "mysql"
                        table
                        None
                        (fun row -> Ok(rowAccount columns row |> Option.exists (sameAccount oldAccount)))
                        (fun row ->
                            let renamed = Array.copy row
                            renamed.[userIndex] <- VString newAccount.Name
                            renamed.[hostIndex] <- VString newAccount.Host
                            Ok renamed)
                    |> Result.map ignore
                    |> Result.mapError toMySqlError
                | _ -> Ok()

        let renameRoleRows table accountColumns =
            match scanList store "mysql" table with
            | Error error -> Error(toMySqlError error)
            | Ok(columns, _) ->
                let resolved =
                    accountColumns
                    |> traverse (fun (userColumn, hostColumn) ->
                        match resolveColumn columns userColumn, resolveColumn columns hostColumn with
                        | Ok userIndex, Ok hostIndex -> Ok(userIndex, hostIndex)
                        | _ -> Error(1105, "Invalid role catalog shape"))

                resolved
                |> Result.bind (fun indices ->
                    updateRows
                        store
                        "mysql"
                        table
                        None
                        (fun row ->
                            Ok(
                                indices
                                |> List.exists (fun (userIndex, hostIndex) ->
                                    sameAccount (account (Value.toText row.[userIndex] |> Option.defaultValue "") (Value.toText row.[hostIndex] |> Option.defaultValue "")) oldAccount)
                            ))
                        (fun row ->
                            let renamed = Array.copy row

                            indices
                            |> List.iter (fun (userIndex, hostIndex) ->
                                if sameAccount (account (Value.toText row.[userIndex] |> Option.defaultValue "") (Value.toText row.[hostIndex] |> Option.defaultValue "")) oldAccount then
                                    renamed.[userIndex] <- VString newAccount.Name
                                    renamed.[hostIndex] <- VString newAccount.Host)

                            Ok renamed)
                    |> Result.map ignore
                    |> Result.mapError toMySqlError)

        [ for table in [ "user"; "db"; "tables_priv"; "columns_priv"; "global_grants" ] do
              yield renameRows table
          yield renameRoleRows "role_edges" [ "FROM_USER", "FROM_HOST"; "TO_USER", "TO_HOST" ]
          yield renameRoleRows "default_roles" [ "USER", "HOST"; "DEFAULT_ROLE_USER", "DEFAULT_ROLE_HOST" ] ]
        |> traverse id
        |> Result.map (fun _ ->
            match store.AccountResources.TryRemove(accountResourceKey oldAccount) with
            | true, usage -> store.AccountResources.[accountResourceKey newAccount] <- usage
            | false, _ -> ())

/// Rewrites the columns named in `changes` on every mysql.`table` row
/// matching `matches` — the one shared row-mutation shape grant/revoke/
/// password changes all reduce to.
let private updateSystemRows
    (store: Store)
    (table: string)
    (matches: ColumnDef list -> Value[] -> bool)
    (changes: (string * Value) list)
    : Result<int, int * string> =
    match scanList store "mysql" table with
    | Error e -> Error(toMySqlError e)
    | Ok(cols, _) ->
        let indexed =
            changes |> List.choose (fun (name, v) -> resolveColumn cols name |> Result.toOption |> Option.map (fun i -> i, v))

        match
            updateRows
                store
                "mysql"
                table
                None
                (fun r -> Ok(matches cols r))
                (fun r ->
                    let r' = Array.copy r
                    indexed |> List.iter (fun (i, v) -> r'.[i] <- v)
                    Ok r')
        with
        | Ok n -> Ok n
        | Error e -> Error(toMySqlError e)

/// A mysql.user row-matcher for one account — the shape both
/// `setPassword` and `applyAtLevel`'s global branch filter by.
let private matchUserRow (wanted: Account) (cols: ColumnDef list) (row: Value[]) =
    rowAccount cols row |> Option.exists (sameAccount wanted)

let private accountOptionChanges (options: AccountOptions) =
    let limits = options.ResourceLimits

    [ options.TlsRequirement |> Option.map (fun requirement -> "ssl_type", VString(sslType requirement))
      limits.MaxQueriesPerHour |> Option.map (fun value -> "max_questions", VInt(int64 value))
      limits.MaxUpdatesPerHour |> Option.map (fun value -> "max_updates", VInt(int64 value))
      limits.MaxConnectionsPerHour |> Option.map (fun value -> "max_connections", VInt(int64 value))
      limits.MaxUserConnections |> Option.map (fun value -> "max_user_connections", VInt(int64 value))
      options.Locked |> Option.map (fun locked -> "account_locked", VString(if locked then "Y" else "N")) ]
    |> List.choose id
    |> fun changes ->
        match options.PasswordExpiration with
        | Some ExpirePassword -> ("password_expired", VString "Y") :: changes
        | Some ExpirePasswordByDefault -> ("password_lifetime", VNull) :: changes
        | Some NeverExpirePassword -> ("password_lifetime", VInt 0L) :: changes
        | Some(ExpirePasswordAfterDays days) -> ("password_lifetime", VInt(int64 days)) :: changes
        | None -> changes

let private hasResourceLimitChanges (limits: AccountResourceLimits) =
    limits.MaxQueriesPerHour.IsSome
    || limits.MaxUpdatesPerHour.IsSome
    || limits.MaxConnectionsPerHour.IsSome
    || limits.MaxUserConnections.IsSome

let alterUser
    (store: Store)
    (name: string)
    (host: string)
    (password: string option)
    (options: AccountOptions)
    : Result<unit, int * string> =
    let wanted = account name host

    if (tryUserRowForAccount store wanted).IsNone then
        operationFailed "ALTER USER" name host
    else
        let changes =
            match password with
            | None -> accountOptionChanges options
            | Some password ->
                [ "authentication_string", VString(if password = "" then "" else nativePasswordHash password)
                  "password_expired", VString "N"
                  "password_last_changed", VDateTime(Functions.truncateToSecond DateTime.Now) ]
                @ accountOptionChanges options

        let updated =
            if changes.IsEmpty then
                Ok 0
            else
                updateSystemRows store "user" (matchUserRow wanted) changes

        updated
        |> Result.map (fun _ ->
            if hasResourceLimitChanges options.ResourceLimits then
                resetAccountResources store wanted)

let alterUserOptions (store: Store) (name: string) (host: string) (options: AccountOptions) : Result<unit, int * string> =
    alterUser store name host None options

/// `ALTER USER ... IDENTIFIED BY 'pw'` / `SET PASSWORD [FOR user] = 'pw'` —
/// rewrites the stored hash (empty password clears it back to
/// accept-anything).
let setPassword (store: Store) (name: string) (host: string) (password: string) : Result<unit, int * string> =
    alterUser store name host (Some password) AccountOptions.empty

let setAccountLocked (store: Store) (name: string) (host: string) (locked: bool) : Result<unit, int * string> =
    alterUserOptions store name host { AccountOptions.empty with Locked = Some locked }

let isAccountLocked (cols: ColumnDef list) (row: Value[]) = userColumnText cols row "account_locked" = "Y"

// ---------------------------------------------------------------------------
// GRANT / REVOKE and privilege checks. Scope hierarchy is MySQL's:
// global (mysql.user) ⊃ db (mysql.db) ⊃ table (mysql.tables_priv) ⊃
// column (mysql.columns_priv).
// ---------------------------------------------------------------------------

/// Where a privilege applies.
type PrivTarget =
    | Global
    | OnDb of db: string
    | OnTable of db: string * table: string
    | OnColumn of db: string * table: string * column: string
    | OnAllColumns of db: string * table: string

/// Resolves `Ast.Grant`/`Revoke`'s `(db, table)` level encoding against the
/// session database (a bare `ON t` means the current db's table).
let targetOfLevel (defaultDb: string) (level: string option * string option) : PrivTarget =
    match level with
    | None, None -> Global
    | Some db, None -> OnDb db
    | Some db, Some t -> OnTable(db, t)
    | None, Some t -> OnTable(defaultDb, t)

let private eqI (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

type DynamicGrant =
    { Privilege: string
      Grantable: bool }

let dynamicGrantsForAccount (store: Store) (wanted: Account) : DynamicGrant list =
    match scanList store "mysql" "global_grants" with
    | Error _ -> []
    | Ok(columns, rows) ->
        rows
        |> List.choose (fun row ->
            if rowAccount columns row |> Option.exists (sameAccount wanted) then
                let privilege = userColumnText columns row "PRIV"

                if Privileges.contains privilege then
                    Some
                        { Privilege = privilege.ToUpperInvariant()
                          Grantable = userColumnText columns row "WITH_GRANT_OPTION" = "Y" }
                else
                    None
            else
                None)
        |> List.sortBy _.Privilege

let hasDynamicPrivilege (store: Store) (wanted: Account) (privilege: string) =
    dynamicGrantsForAccount store wanted
    |> List.exists (fun grant -> eqI grant.Privilege privilege)

let private hasGrantableDynamicPrivilege (store: Store) (wanted: Account) (privilege: string) =
    dynamicGrantsForAccount store wanted
    |> List.exists (fun grant -> eqI grant.Privilege privilege && grant.Grantable)

type RoleGrant =
    { Role: Account
      Grantee: Account
      AdminOption: bool }

let private compareAccounts left right =
    Operators.compare (left.Name, left.Host.ToLowerInvariant()) (right.Name, right.Host.ToLowerInvariant())

let roleGrants (store: Store) : RoleGrant list =
    match scanList store "mysql" "role_edges" with
    | Error _ -> []
    | Ok(columns, rows) ->
        rows
        |> List.map (fun row ->
            { Role = account (userColumnText columns row "FROM_USER") (userColumnText columns row "FROM_HOST")
              Grantee = account (userColumnText columns row "TO_USER") (userColumnText columns row "TO_HOST")
              AdminOption = userColumnText columns row "WITH_ADMIN_OPTION" = "Y" })

let directRoleGrantsForAccount (store: Store) (wanted: Account) : RoleGrant list =
    roleGrants store
    |> List.filter (fun grant -> sameAccount grant.Grantee wanted)
    |> List.sortWith (fun left right -> compareAccounts left.Role right.Role)

let applicableRolesForAccount store wanted =
    let configuredMandatoryRoles =
        mandatoryRoles store
        |> List.filter (fun role -> tryUserRowForAccount store role |> Option.isSome)

    (directRoleGrantsForAccount store wanted |> List.map _.Role) @ configuredMandatoryRoles
    |> List.distinctBy (fun role -> role.Name, role.Host.ToLowerInvariant())
    |> List.sortWith compareAccounts

let roleClosure (store: Store) (roots: Account list) : Account list =
    let grants = roleGrants store

    let rec visit visited pending =
        match pending with
        | [] -> visited
        | current :: rest when visited |> List.exists (sameAccount current) -> visit visited rest
        | current :: rest ->
            let inherited =
                grants
                |> List.choose (fun grant -> if sameAccount grant.Grantee current then Some grant.Role else None)

            visit (current :: visited) (inherited @ rest)

    visit [] roots |> List.sortWith compareAccounts

let defaultRolesForAccount (store: Store) (wanted: Account) : Account list =
    match scanList store "mysql" "default_roles" with
    | Error _ -> []
    | Ok(columns, rows) ->
        rows
        |> List.choose (fun row ->
            let grantee = account (userColumnText columns row "USER") (userColumnText columns row "HOST")

            if sameAccount grantee wanted then
                Some(account (userColumnText columns row "DEFAULT_ROLE_USER") (userColumnText columns row "DEFAULT_ROLE_HOST"))
            else
                None)
        |> List.sortWith compareAccounts

let effectiveAccounts (store: Store) (wanted: Account) (activeRoles: Account list) =
    wanted :: roleClosure store activeRoles

/// Splits a `Table_priv`/`Column_priv` SET string into its members — public
/// because `InformationSchema.TABLE_PRIVILEGES` reads the same encoding.
let setMembers (s: string) : string list =
    s.Split(',') |> Array.toList |> List.map (fun m -> m.Trim()) |> List.filter (fun m -> m <> "")

/// Expands a GRANT/REVOKE privilege list for a target level: `ALL` becomes
/// every static privilege that exists at that level, `USAGE` becomes
/// nothing, and a privilege that doesn't exist at the level is a MySQL
/// 1221/1144.
type private ResolvedPrivileges =
    { Static: PrivDef list
      Dynamic: string list }

let private expandPrivs (privs: string list) (target: PrivTarget) : Result<ResolvedPrivileges, int * string> =
    let atLevel (d: PrivDef) =
        match target with
        | Global -> true
        | OnDb _ -> d.DbCol.IsSome
        | OnTable _
        | OnColumn _
        | OnAllColumns _ -> d.TablePriv.IsSome

    if privs |> List.exists (fun p -> p = "ALL") then
        Ok
            { Static = staticPrivileges |> List.filter atLevel
              Dynamic = [] }
    else
        privs
        |> List.filter (fun p -> p <> "USAGE")
        |> traverse (fun p ->
            match privBySql p with
            | Some d when atLevel d -> Result.Ok(Choice1Of2 d)
            | Some _ ->
                match target with
                | OnTable _
                | OnColumn _
                | OnAllColumns _ -> Result.Error(1144, "Illegal GRANT/REVOKE command; please consult the manual to see which privileges can be used")
                | _ -> Result.Error(1221, "Incorrect usage of DB GRANT and GLOBAL PRIVILEGES")
            | None when Privileges.contains p ->
                match target with
                | Global -> Result.Ok(Choice2Of2(p.ToUpperInvariant()))
                | _ -> Result.Error(3619, sprintf "Illegal privilege level specified for %s" (p.ToUpperInvariant()))
            | None -> Result.Error(1149, sprintf "Unknown privilege '%s'" p))
        |> Result.map (fun resolved ->
            { Static = resolved |> List.choose (function Choice1Of2 privilege -> Some privilege | _ -> None)
              Dynamic = resolved |> List.choose (function Choice2Of2 privilege -> Some privilege | _ -> None) })

let private privilegeNames (privileges: PrivilegeSpec list) =
    privileges |> List.map (fun privilege -> privilege.Name)

/// One user's grant/revoke at one level. `yes` is `'Y'` for GRANT, `'N'`
/// for REVOKE; table-level edits union/subtract the SET string instead.
let private applyAtLevel
    (store: Store)
    (name: string)
    (host: string)
    (defs: PrivDef list)
    (target: PrivTarget)
    (withGrantOption: bool)
    (granting: bool)
    : Result<unit, int * string> =
    let host = (account name host).Host
    let yn = if granting then "Y" else "N"

    let grantOptCol =
        if withGrantOption then [ "Grant_priv", VString yn ] else []

    match target with
    | Global ->
        let changes = (defs |> List.map (fun d -> d.UserCol, VString yn)) @ grantOptCol
        updateSystemRows store "user" (matchUserRow (account name host)) changes |> Result.map ignore
    | OnDb db ->
        let matches (cols: ColumnDef list) (r: Value[]) =
            match rowAccount cols r, resolveColumn cols "Db" with
            | Some rowAccount, Result.Ok d -> sameAccount rowAccount (account name host) && (match r.[d] with VString s -> eqI s db | _ -> false)
            | _ -> false

        let changes =
            (defs |> List.choose (fun d -> d.DbCol |> Option.map (fun c -> c, VString yn))) @ grantOptCol

        match updateSystemRows store "db" matches changes with
        | Result.Error e -> Result.Error e
        | Result.Ok 0 when granting ->
            match scanList store "mysql" "db" with
            | Result.Ok(cols, rows) when rows |> List.exists (matches cols) -> Result.Ok()
            | Result.Error e -> Result.Error(toMySqlError e)
            | Result.Ok _ ->
                let grantedCols = changes |> List.map fst
                match
                    insertRows
                        store
                        "mysql"
                        "db"
                        (Some([ "Host"; "Db"; "User" ] @ grantedCols))
                        [ [ VString host; VString db; VString name ] @ (grantedCols |> List.map (fun _ -> VString "Y")) ]
                with
                | Result.Ok _ -> Result.Ok()
                | Result.Error e -> Result.Error(toMySqlError e)
        | Result.Ok 0 -> Result.Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" name host)
        | Result.Ok _ ->
            if not granting then
                // MySQL deletes a mysql.db row once nothing is left in it —
                // an all-N row would otherwise render a ghost `GRANT USAGE
                // ON db.*` line in SHOW GRANTS.
                match scanList store "mysql" "db" with
                | Result.Error _ -> ()
                | Result.Ok(cols, _) ->
                    let dbLevelCols =
                        "Grant_priv" :: (staticPrivileges |> List.choose (fun d -> d.DbCol))

                    let allN (r: Value[]) =
                        dbLevelCols
                        |> List.forall (fun c ->
                            match resolveColumn cols c with
                            | Result.Ok i -> r.[i] <> VString "Y"
                            | _ -> true)

                    deleteRows store "mysql" "db" (fun r -> Result.Ok(matches cols r && allN r)) |> ignore

            Result.Ok()
    | OnTable(db, table) ->
        let wanted =
            (defs |> List.choose (fun d -> d.TablePriv))
            @ (if withGrantOption then [ "Grant" ] else [])

        match scanList store "mysql" "tables_priv" with
        | Result.Error e -> Result.Error(toMySqlError e)
        | Result.Ok(cols, rows) ->
            let idx n = resolveColumn cols n |> Result.toOption
            match idx "Db", idx "Table_name", idx "Table_priv", idx "Column_priv" with
            | Some d, Some t, Some tp, Some cp ->
                let matchesRow (r: Value[]) =
                    rowAccount cols r |> Option.exists (sameAccount (account name host))
                    && (match r.[d] with VString s -> eqI s db | _ -> false)
                    && (match r.[t] with VString s -> eqI s table | _ -> false)

                let existing = rows |> List.tryFind matchesRow

                let currentSet =
                    existing
                    |> Option.map (fun r -> match r.[tp] with VString s -> setMembers s | _ -> [])
                    |> Option.defaultValue []

                let currentColumnSet =
                    existing
                    |> Option.map (fun row -> match row.[cp] with VString value -> setMembers value | _ -> [])
                    |> Option.defaultValue []

                let newSet =
                    if granting then
                        currentSet @ (wanted |> List.filter (fun w -> not (currentSet |> List.exists (eqI w))))
                    else
                        currentSet |> List.filter (fun c -> not (wanted |> List.exists (eqI c)))

                match existing with
                | Some _ when newSet.IsEmpty && currentColumnSet.IsEmpty && not granting ->
                    // Same as mysql.db above: MySQL removes a tables_priv row
                    // once its SET is empty.
                    deleteRows store "mysql" "tables_priv" (fun r -> Result.Ok(matchesRow r)) |> ignore
                    Result.Ok()
                | Some _ ->
                    updateSystemRows
                        store
                        "tables_priv"
                        (fun _ r -> matchesRow r)
                        [ "Table_priv", VString(String.concat "," newSet) ]
                    |> Result.map ignore
                | None when granting ->
                    match
                        insertRows
                            store
                            "mysql"
                            "tables_priv"
                            (Some [ "Host"; "Db"; "User"; "Table_name"; "Grantor"; "Table_priv" ])
                            [ [ VString host
                                VString db
                                VString name
                                VString table
                                VString "root@%"
                                VString(String.concat "," newSet) ] ]
                    with
                    | Result.Ok _ -> Result.Ok()
                    | Result.Error e -> Result.Error(toMySqlError e)
                | None -> Result.Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" name host)
            | _ -> Result.Error(1146, "Table 'tables_priv' doesn't exist")
    | OnColumn _
    | OnAllColumns _ -> Result.Error(1144, "Illegal GRANT/REVOKE command; please consult the manual to see which privileges can be used")

let private applyDynamicPrivileges
    (store: Store)
    (name: string)
    (host: string)
    (privileges: string list)
    (withGrantOption: bool)
    (granting: bool)
    : Result<unit, int * string> =
    let wanted = account name host

    privileges
    |> traverse (fun privilege ->
        match scanList store "mysql" "global_grants" with
        | Error error -> Error(toMySqlError error)
        | Ok(columns, rows) ->
            let matches row =
                rowAccount columns row |> Option.exists (sameAccount wanted)
                && eqI (userColumnText columns row "PRIV") privilege

            match rows |> List.tryFind matches, granting with
            | Some _, true when withGrantOption ->
                updateSystemRows
                    store
                    "global_grants"
                    (fun _ row -> matches row)
                    [ "WITH_GRANT_OPTION", VString "Y" ]
                |> Result.map ignore
            | Some _, true -> Ok()
            | None, true ->
                insertRows
                    store
                    "mysql"
                    "global_grants"
                    (Some [ "USER"; "HOST"; "PRIV"; "WITH_GRANT_OPTION" ])
                    [ [ VString wanted.Name
                        VString wanted.Host
                        VString privilege
                        VString(if withGrantOption then "Y" else "N") ] ]
                |> Result.map ignore
                |> Result.mapError toMySqlError
            | Some _, false ->
                deleteRows store "mysql" "global_grants" (matches >> Ok)
                |> Result.map ignore
                |> Result.mapError toMySqlError
            | None, false -> Ok())
    |> Result.map ignore

let private applyResolvedPrivileges store name host resolved target withGrantOption granting =
    let changesOnlyDynamicPrivileges =
        resolved.Static.IsEmpty
        && not resolved.Dynamic.IsEmpty
        && (granting || not withGrantOption)

    let applyStatic =
        if changesOnlyDynamicPrivileges then
            Ok()
        else
            applyAtLevel store name host resolved.Static target withGrantOption granting

    applyStatic
    |> Result.bind (fun () ->
        applyDynamicPrivileges store name host resolved.Dynamic withGrantOption granting)

let private projectionName =
    function
    | _, Some alias -> Some alias
    | Col name, None -> Some name
    | QualifiedCol(_, name), None -> Some name
    | _ -> None

let private projectionNames (select: SelectStmt) =
    select.Projections |> List.choose projectionName

let private storedViewColumns store database table =
    let decode names =
        if names = "" then
            []
        else
            try
                match JsonSerializer.Deserialize<string[]>(names) with
                | null -> []
                | values -> List.ofArray values
            with :? JsonException ->
                []

    match scanList store "mysql" "views" with
    | Error _ -> []
    | Ok(_, rows) ->
        rows
        |> List.choose SystemCatalog.View.tryRead
        |> List.tryFind (fun view -> eqI view.Schema database && eqI view.Name table)
        |> Option.map (fun view ->
            match decode view.ColumnNames with
            | _ :: _ as explicit -> explicit
            | [] ->
                match Parser.parseViewDefinition view.Definition with
                | Ok definition ->
                    match definition.Statement with
                    | Select select -> projectionNames select
                    | Union(first, _, _, _, _) -> projectionNames first
                    | _ -> []
                | Error _ -> [])
        |> Option.defaultValue []

let private columnPrivilegeDefs =
    staticPrivileges
    |> List.filter (fun privilege ->
        privilege.TablePriv
        |> Option.exists (fun name -> [ "Select"; "Insert"; "Update"; "References" ] |> List.exists (eqI name)))

let private canonicalColumnPrivilegeMembers members =
    columnPrivilegeDefs
    |> List.choose (fun privilege ->
        privilege.TablePriv
        |> Option.filter (fun name -> members |> List.exists (eqI name)))

let private columnPrivilegeSetName name =
    columnPrivilegeDefs
    |> List.tryFind (fun privilege -> eqI privilege.Sql name)
    |> Option.bind _.TablePriv

let private validateColumnSpecifications store target (specifications: PrivilegeSpec list) =
    match specifications, target with
    | [], _ -> Ok []
    | _, OnTable(database, table) ->
        let columnNames =
            match scan store database table with
            | Ok(columns, _) -> Ok(columns |> List.map _.Name)
            | Error error ->
                match storedViewColumns store database table with
                | _ :: _ as columns -> Ok columns
                | [] -> Error(toMySqlError error)

        columnNames
        |> Result.bind (fun columns ->
            specifications
            |> traverse (fun specification ->
                match columnPrivilegeSetName specification.Name with
                | None ->
                    Error(
                        1144,
                        "Illegal GRANT/REVOKE command; please consult the manual to see which privileges can be used"
                    )
                | Some _ ->
                    specification.Columns
                    |> traverse (fun requested ->
                        match columns |> List.tryFind (fun column -> eqI column requested) with
                        | Some column -> Ok column
                        | None -> Error(1054, sprintf "Unknown column '%s' in '%s'" requested table))
                    |> Result.map (fun resolved -> { specification with Columns = resolved |> List.distinctBy _.ToLowerInvariant() })))
    | _ ->
        Error(
            1144,
            "Illegal GRANT/REVOKE command; please consult the manual to see which privileges can be used"
        )

let private syncTableColumnPrivileges store wanted database table withGrantOption =
    let columnMembers =
        match scanList store "mysql" "columns_priv" with
        | Error _ -> []
        | Ok(columns, rows) ->
            rows
            |> List.filter (fun row ->
                rowAccount columns row |> Option.exists (sameAccount wanted)
                && eqI (userColumnText columns row "Db") database
                && eqI (userColumnText columns row "Table_name") table)
            |> List.collect (fun row -> setMembers (userColumnText columns row "Column_priv"))
            |> canonicalColumnPrivilegeMembers

    match scanList store "mysql" "tables_priv" with
    | Error error -> Error(toMySqlError error)
    | Ok(columns, rows) ->
        let matches row =
            rowAccount columns row |> Option.exists (sameAccount wanted)
            && eqI (userColumnText columns row "Db") database
            && eqI (userColumnText columns row "Table_name") table

        match rows |> List.tryFind matches with
        | Some row ->
            let tableMembers = setMembers (userColumnText columns row "Table_priv")

            let tableMembers =
                if withGrantOption && not (tableMembers |> List.exists (eqI "Grant")) then
                    tableMembers @ [ "Grant" ]
                else
                    tableMembers

            if tableMembers.IsEmpty && columnMembers.IsEmpty then
                deleteRows store "mysql" "tables_priv" (matches >> Ok)
                |> Result.map ignore
                |> Result.mapError toMySqlError
            else
                updateSystemRows
                    store
                    "tables_priv"
                    (fun _ row -> matches row)
                    [ "Table_priv", VString(String.concat "," tableMembers)
                      "Column_priv", VString(String.concat "," columnMembers) ]
                |> Result.map ignore
        | None when columnMembers.IsEmpty -> Ok()
        | None ->
            insertRows
                store
                "mysql"
                "tables_priv"
                (Some [ "Host"; "Db"; "User"; "Table_name"; "Grantor"; "Table_priv"; "Column_priv" ])
                [ [ VString wanted.Host
                    VString database
                    VString wanted.Name
                    VString table
                    VString "root@%"
                    VString(if withGrantOption then "Grant" else "")
                    VString(String.concat "," columnMembers) ] ]
            |> Result.map ignore
            |> Result.mapError toMySqlError

let private applyColumnSpecifications
    store
    wanted
    database
    table
    (specifications: PrivilegeSpec list)
    withGrantOption
    granting
    =
    let requested =
        specifications
        |> List.collect (fun specification ->
            match columnPrivilegeSetName specification.Name with
            | Some setName -> specification.Columns |> List.map (fun column -> column, setName)
            | None -> [])
        |> List.groupBy fst
        |> List.map (fun (column, values) -> column, values |> List.map snd |> List.distinctBy _.ToLowerInvariant())

    match scanList store "mysql" "columns_priv" with
    | Error error -> Error(toMySqlError error)
    | Ok(columns, rows) ->
        let matches column row =
            rowAccount columns row |> Option.exists (sameAccount wanted)
            && eqI (userColumnText columns row "Db") database
            && eqI (userColumnText columns row "Table_name") table
            && eqI (userColumnText columns row "Column_name") column

        let changes =
            requested
            |> List.map (fun (column, members) ->
                let existing = rows |> List.tryFind (matches column)
                let current = existing |> Option.map (userColumnText columns >> fun read -> read "Column_priv" |> setMembers) |> Option.defaultValue []

                let updated =
                    (if granting then
                         current @ (members |> List.filter (fun setName -> current |> List.exists (eqI setName) |> not))
                     else
                         current |> List.filter (fun setName -> members |> List.exists (eqI setName) |> not))
                    |> canonicalColumnPrivilegeMembers

                column, members, existing.IsSome, current, updated)

        if
            not granting
            && changes
               |> List.exists (fun (_, requested, _, current, _) ->
                   requested |> List.exists (fun setName -> current |> List.exists (eqI setName) |> not))
        then
            Error(1147, sprintf "There is no such grant defined for user '%s' on host '%s' on table '%s'" wanted.Name wanted.Host table)
        else
            changes
            |> traverse (fun (column, _, exists, _, updated) ->
                match exists, updated with
                | true, [] ->
                    deleteRows store "mysql" "columns_priv" (matches column >> Ok)
                    |> Result.map ignore
                    |> Result.mapError toMySqlError
                | true, _ ->
                    updateSystemRows
                        store
                        "columns_priv"
                        (fun _ row -> matches column row)
                        [ "Column_priv", VString(String.concat "," updated) ]
                    |> Result.map ignore
                | false, _ ->
                    insertRows
                        store
                        "mysql"
                        "columns_priv"
                        (Some [ "Host"; "Db"; "User"; "Table_name"; "Column_name"; "Column_priv" ])
                        [ [ VString wanted.Host
                            VString database
                            VString wanted.Name
                            VString table
                            VString column
                            VString(String.concat "," updated) ] ]
                    |> Result.map ignore
                    |> Result.mapError toMySqlError)
            |> Result.bind (fun _ -> syncTableColumnPrivileges store wanted database table withGrantOption)

let private revokeTablePrivilegesFromColumns store wanted database table (privileges: PrivilegeSpec list) =
    let members =
        if privileges |> List.exists (fun privilege -> privilege.Name = "ALL") then
            columnPrivilegeDefs |> List.choose _.TablePriv
        else
            privileges
            |> List.choose (fun privilege -> if privilege.Columns.IsEmpty then columnPrivilegeSetName privilege.Name else None)

    if members.IsEmpty then
        Ok()
    else
        match scanList store "mysql" "columns_priv" with
        | Error error -> Error(toMySqlError error)
        | Ok(columns, rows) ->
            let matches row =
                rowAccount columns row |> Option.exists (sameAccount wanted)
                && eqI (userColumnText columns row "Db") database
                && eqI (userColumnText columns row "Table_name") table

            rows
            |> List.filter matches
            |> traverse (fun row ->
                let column = userColumnText columns row "Column_name"
                let current = setMembers (userColumnText columns row "Column_priv")
                let updated =
                    current
                    |> List.filter (fun setName -> members |> List.exists (eqI setName) |> not)
                    |> canonicalColumnPrivilegeMembers

                if updated.IsEmpty then
                    deleteRows store "mysql" "columns_priv" (fun candidate -> Ok(matches candidate && eqI (userColumnText columns candidate "Column_name") column))
                    |> Result.map ignore
                    |> Result.mapError toMySqlError
                else
                    updateSystemRows
                        store
                        "columns_priv"
                        (fun _ candidate -> matches candidate && eqI (userColumnText columns candidate "Column_name") column)
                        [ "Column_priv", VString(String.concat "," updated) ]
                    |> Result.map ignore)
            |> Result.bind (fun _ -> syncTableColumnPrivileges store wanted database table false)

let private quotedAccount account = sprintf "`%s`@`%s`" account.Name account.Host

let private distinctAccounts references =
    references
    |> List.map (fun (name, host) -> account name host)
    |> List.distinctBy (fun account -> account.Name, account.Host.ToLowerInvariant())

let private roleGrantMatches (columns: ColumnDef list) (role: Account) (grantee: Account) (row: Value[]) =
    sameAccount role (account (userColumnText columns row "FROM_USER") (userColumnText columns row "FROM_HOST"))
    && sameAccount grantee (account (userColumnText columns row "TO_USER") (userColumnText columns row "TO_HOST"))

let private unknownAuthorization account =
    Error(3523, sprintf "Unknown authorization ID %s" (quotedAccount account))

let private validateAccountsExist store accounts =
    accounts
    |> List.distinctBy (fun account -> account.Name, account.Host.ToLowerInvariant())
    |> traverse (fun wanted ->
        if tryUserRowForAccount store wanted |> Option.isSome then Ok() else unknownAuthorization wanted)
    |> Result.map ignore

let private roleGraphWouldCycle (grants: RoleGrant list) =
    let rec reaches target visited current =
        if sameAccount target current then
            true
        elif visited |> List.exists (sameAccount current) then
            false
        else
            grants
            |> List.exists (fun grant ->
                sameAccount grant.Grantee current
                && reaches target (current :: visited) grant.Role)

    grants |> List.exists (fun grant -> reaches grant.Grantee [] grant.Role)

let grantRoles
    (store: Store)
    (roles: (string * string) list)
    (users: (string * string) list)
    (withAdminOption: bool)
    : Result<unit, int * string> =
    let roles = distinctAccounts roles
    let users = distinctAccounts users
    let proposed =
        [ for user in users do
              for role in roles do
                  yield
                      { Role = role
                        Grantee = user
                        AdminOption = withAdminOption } ]

    validateAccountsExist store (roles @ users)
    |> Result.bind (fun () ->
        if roleGraphWouldCycle (roleGrants store @ proposed) then
            let cycle = proposed |> List.head
            Error(
                4027,
                sprintf
                    "User account %s is directly or indirectly granted to the role %s. The GRANT would create a loop"
                    (quotedAccount cycle.Grantee)
                    (quotedAccount cycle.Role)
            )
        else
            proposed
            |> traverse (fun grant ->
                match scanList store "mysql" "role_edges" with
                | Error error -> Error(toMySqlError error)
                | Ok(columns, rows) ->
                    let matches = roleGrantMatches columns grant.Role grant.Grantee

                    match rows |> List.tryFind matches with
                    | Some _ when withAdminOption ->
                        updateSystemRows
                            store
                            "role_edges"
                            (fun _ row -> matches row)
                            [ "WITH_ADMIN_OPTION", VString "Y" ]
                        |> Result.map ignore
                    | Some _ -> Ok()
                    | None ->
                        insertRows
                            store
                            "mysql"
                            "role_edges"
                            (Some [ "FROM_HOST"; "FROM_USER"; "TO_HOST"; "TO_USER"; "WITH_ADMIN_OPTION" ])
                            [ [ VString grant.Role.Host
                                VString grant.Role.Name
                                VString grant.Grantee.Host
                                VString grant.Grantee.Name
                                VString(if withAdminOption then "Y" else "N") ] ]
                        |> Result.map ignore
                        |> Result.mapError toMySqlError)
            |> Result.map ignore)

let revokeRoles (store: Store) (roles: (string * string) list) (users: (string * string) list) : Result<unit, int * string> =
    let roles = distinctAccounts roles
    let users = distinctAccounts users

    let mandatory = roles |> List.tryFind (isMandatoryRole store)

    (match mandatory with
     | Some role -> mandatoryRoleError role
     | None -> validateAccountsExist store (roles @ users))
    |> Result.bind (fun () ->
        [ for user in users do
              for role in roles do
                  yield role, user ]
        |> traverse (fun (role, user) ->
            match scanList store "mysql" "role_edges", scanList store "mysql" "default_roles" with
            | Ok(edgeColumns, _), Ok(defaultColumns, _) ->
                deleteRows store "mysql" "role_edges" (fun row -> Ok(roleGrantMatches edgeColumns role user row))
                |> Result.mapError toMySqlError
                |> Result.bind (fun _ ->
                    deleteRows
                        store
                        "mysql"
                        "default_roles"
                        (fun row ->
                            let grantee =
                                account
                                    (userColumnText defaultColumns row "USER")
                                    (userColumnText defaultColumns row "HOST")

                            let defaultRole =
                                account
                                    (userColumnText defaultColumns row "DEFAULT_ROLE_USER")
                                    (userColumnText defaultColumns row "DEFAULT_ROLE_HOST")

                            Ok(sameAccount grantee user && sameAccount defaultRole role))
                    |> Result.map ignore
                    |> Result.mapError toMySqlError)
            | Error error, _
            | _, Error error -> Error(toMySqlError error))
        |> Result.map ignore)

let dropRole (store: Store) (name: string) (host: string) =
    dropUser store name host

let resolveRoleSelection (store: Store) (grantee: Account) selection : Result<Account list, int * string> =
    let direct = applicableRolesForAccount store grantee
    let isDirect role = direct |> List.exists (sameAccount role)

    let validate roles =
        roles
        |> List.distinctBy (fun role -> role.Name, role.Host.ToLowerInvariant())
        |> traverse (fun role ->
            if isDirect role then
                Ok role
            else
                Error(3530, sprintf "%s is not granted to %s" (quotedAccount role) (quotedAccount grantee)))
        |> Result.map (List.sortWith compareAccounts)

    match selection with
    | NoRoles -> Ok []
    | DefaultRoles -> defaultRolesForAccount store grantee |> validate
    | AllRoles -> Ok direct
    | AllRolesExcept excluded ->
        let excluded = excluded |> List.map (fun (name, host) -> account name host)
        validate excluded
        |> Result.map (fun valid -> direct |> List.filter (fun role -> valid |> List.exists (sameAccount role) |> not))
    | NamedRoles roles -> roles |> List.map (fun (name, host) -> account name host) |> validate

let setDefaultRoles
    (store: Store)
    (selection: RoleSelection)
    (users: (string * string) list)
    : Result<unit, int * string> =
    users
    |> distinctAccounts
    |> traverse (fun user ->
        if tryUserRowForAccount store user |> Option.isNone then
            unknownAuthorization user
        else
            resolveRoleSelection store user selection
            |> Result.map (fun roles -> user, roles))
    |> Result.bind (fun assignments ->
        assignments
        |> traverse (fun (user, roles) ->
                match scanList store "mysql" "default_roles" with
                | Error error -> Error(toMySqlError error)
                | Ok(columns, _) ->
                    deleteRows
                        store
                        "mysql"
                        "default_roles"
                        (fun row ->
                            Ok(
                                sameAccount
                                    user
                                    (account (userColumnText columns row "USER") (userColumnText columns row "HOST"))
                            ))
                    |> Result.mapError toMySqlError
                    |> Result.bind (fun _ ->
                        roles
                        |> traverse (fun role ->
                            insertRows
                                store
                                "mysql"
                                "default_roles"
                                (Some [ "HOST"; "USER"; "DEFAULT_ROLE_HOST"; "DEFAULT_ROLE_USER" ])
                                [ [ VString user.Host; VString user.Name; VString role.Host; VString role.Name ] ]
                            |> Result.map ignore
                            |> Result.mapError toMySqlError)
                        |> Result.map ignore)))
    |> Result.map ignore

let checkRoleGrantAuthorityForAccount
    (store: Store)
    (wanted: Account)
    (activeRoles: Account list)
    (roles: (string * string) list)
    : Result<unit, int * string> =
    let actors = effectiveAccounts store wanted activeRoles
    let requestedRoles = roles |> List.map (fun (name, host) -> account name host)
    let roleAdmin = actors |> List.exists (fun actor -> hasDynamicPrivilege store actor "ROLE_ADMIN")

    let canAdmin role =
        roleGrants store
        |> List.exists (fun grant ->
            sameAccount grant.Role role
            && grant.AdminOption
            && actors |> List.exists (sameAccount grant.Grantee))

    if roleAdmin || requestedRoles |> List.forall canAdmin then
        Ok()
    else
        Error(
            1227,
            "Access denied; you need (at least one of) the WITH ADMIN OPTION privilege(s) for this operation"
        )

let formatCurrentRoles roles =
    match roles |> List.sortWith compareAccounts with
    | [] -> "NONE"
    | active -> active |> List.map quotedAccount |> String.concat ","

/// `GRANT privs ON target TO users [WITH GRANT OPTION]`. MySQL 8 no longer
/// auto-creates unknown grantees — that's 1410.
let grantSpecifications
    (store: Store)
    (privs: PrivilegeSpec list)
    (target: PrivTarget)
    (users: (string * string) list)
    (withGrantOption: bool)
    : Result<unit, int * string> =
    let columnSpecifications, wholeSpecifications = privs |> List.partition (fun privilege -> not privilege.Columns.IsEmpty)

    validateColumnSpecifications store target columnSpecifications
    |> Result.bind (fun columns ->
        expandPrivs (privilegeNames wholeSpecifications) target
        |> Result.bind (fun resolved ->
            users
            |> traverse (fun (name, host) ->
                let wanted = account name host

                if (tryUserRowForAccount store wanted).IsNone then
                    Result.Error(1410, "You are not allowed to create a user with GRANT")
                else
                    (if wholeSpecifications.IsEmpty then
                         Ok()
                     else
                         applyResolvedPrivileges store name host resolved target withGrantOption true)
                    |> Result.bind (fun () ->
                        match columns, target with
                        | [], _ -> Ok()
                        | columns, OnTable(database, table) ->
                            applyColumnSpecifications store wanted database table columns withGrantOption true
                        | _ -> Error(1144, "Illegal GRANT/REVOKE command")))
            |> Result.map ignore))

let grant store privileges target users withGrantOption =
    privileges
    |> List.map PrivilegeSpec.named
    |> fun specs -> grantSpecifications store specs target users withGrantOption

/// `REVOKE privs ON target FROM users`.
let revokeSpecifications
    (store: Store)
    (privs: PrivilegeSpec list)
    (target: PrivTarget)
    (users: (string * string) list)
    : Result<unit, int * string> =
    let names = privilegeNames privs
    let revokesGrantOption = names |> List.exists ((=) "GRANT OPTION")
    let columnSpecifications, wholeSpecifications = privs |> List.partition (fun privilege -> not privilege.Columns.IsEmpty)

    validateColumnSpecifications store target columnSpecifications
    |> Result.bind (fun columns ->
        expandPrivs
            (wholeSpecifications |> privilegeNames |> List.filter (fun privilege -> privilege <> "GRANT OPTION"))
            target
        |> Result.bind (fun resolved ->
            users
            |> traverse (fun (name, host) ->
                let wanted = account name host

                if (tryUserRowForAccount store wanted).IsNone then
                    Result.Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" name host)
                else
                    (if wholeSpecifications.IsEmpty then
                         Ok()
                     else
                         applyResolvedPrivileges store name host resolved target revokesGrantOption false)
                    |> Result.bind (fun () ->
                        match target with
                        | OnTable(database, table) ->
                            revokeTablePrivilegesFromColumns store wanted database table wholeSpecifications
                            |> Result.bind (fun () ->
                                if columns.IsEmpty then
                                    Ok()
                                else
                                    applyColumnSpecifications store wanted database table columns false false)
                        | _ when columns.IsEmpty -> Ok()
                        | _ -> Error(1144, "Illegal GRANT/REVOKE command")))
            |> Result.map ignore))

let revoke store privileges target users =
    privileges
    |> List.map PrivilegeSpec.named
    |> fun specs -> revokeSpecifications store specs target users

/// Every real table a statement's expressions and sources read, walked
/// recursively — a derived table (`FROM (SELECT ... secret)`), a scalar or
/// `IN`/`EXISTS` subquery in any clause (WHERE, projections, SET, VALUES),
/// and unions nested in either all reach their tables here, so a privilege
/// check can't be dodged by burying the reference below the top level.
let rec private exprReadTablesIn (boundCtes: Set<string>) (defaultDb: string) (expr: Expr) : (string * string) list =
    Expression.collectSubqueries expr
    |> List.collect (selectReadTablesIn boundCtes defaultDb)

and private fromItemReadTablesIn (boundCtes: Set<string>) (defaultDb: string) (item: FromItem) : (string * string) list =
    match item with
    | FromTable(r: TableRef) when r.Database.IsNone && Set.contains (r.Table.ToLowerInvariant()) boundCtes -> []
    | FromTable(r: TableRef) -> [ (defaultArg r.Database defaultDb), r.Table ]
    | FromSubquery(body, _)
    | FromLateral(body, _) -> selectOrUnionReadTablesIn boundCtes defaultDb body
    // A JSON_TABLE reads nothing itself; its source expression may (a
    // subquery, a correlated column of a table already listed), so walk it.
    | FromJsonTable(source, _, _, _) -> exprReadTablesIn boundCtes defaultDb source

and private selectOrUnionReadTablesIn (boundCtes: Set<string>) (defaultDb: string) (body: SelectOrUnion) : (string * string) list =
    match body with
    | PlainSelect s -> selectReadTablesIn boundCtes defaultDb s
    | UnionSelect(first, rest, _, _, _) ->
        selectReadTablesIn boundCtes defaultDb first
        @ (rest |> List.collect (snd >> selectReadTablesIn boundCtes defaultDb))

and private selectReadTablesIn (boundCtes: Set<string>) (defaultDb: string) (s: SelectStmt) : (string * string) list =
    let cteReads, localCtes = cteReadTablesIn boundCtes defaultDb s.Ctes

    cteReads
    @ (s.From |> Option.map (fromItemReadTablesIn localCtes defaultDb) |> Option.defaultValue [])
    @ (s.Joins |> List.collect (fun j -> fromItemReadTablesIn localCtes defaultDb j.Table @ exprReadTablesIn localCtes defaultDb j.On))
    @ (s.Where |> Option.map (exprReadTablesIn localCtes defaultDb) |> Option.defaultValue [])
    @ (s.Having |> Option.map (exprReadTablesIn localCtes defaultDb) |> Option.defaultValue [])
    @ (s.Projections |> List.collect (fst >> exprReadTablesIn localCtes defaultDb))
    @ (s.GroupBy |> List.collect (exprReadTablesIn localCtes defaultDb))
    @ (s.Windows
       |> List.collect (fun (_, spec) ->
           Expression.overExpressions (OverSpec spec))
       |> List.collect (exprReadTablesIn localCtes defaultDb))
    @ (s.OrderBy |> List.collect (fst >> exprReadTablesIn localCtes defaultDb))
    |> List.distinct

and private cteReadTablesIn
    (boundCtes: Set<string>)
    (defaultDb: string)
    (ctes: CommonTableExpr list)
    : (string * string) list * Set<string> =
    ctes
    |> List.fold
        (fun (reads, names) cte ->
            let name = cte.CteName.ToLowerInvariant()
            let visible = if cte.Recursive then Set.add name names else names
            let reads = selectOrUnionReadTablesIn visible defaultDb cte.Body @ reads
            reads, Set.add name names)
        ([], boundCtes)

let private exprReadTables defaultDb expression = exprReadTablesIn Set.empty defaultDb expression
let private fromItemReadTables defaultDb item = fromItemReadTablesIn Set.empty defaultDb item
let private selectOrUnionReadTables defaultDb body = selectOrUnionReadTablesIn Set.empty defaultDb body
let private selectReadTables defaultDb select = selectReadTablesIn Set.empty defaultDb select

/// Kept for callers that still want the top-level `From`/`Joins` set with a
/// per-item transform; `selectReadTables` is the recursive whole-statement
/// collector.
let private tableRefsOfFrom (defaultDb: string) (from: FromItem option) (joins: Join list) : (string * string) list =
    (from |> Option.map (fromItemReadTables defaultDb) |> Option.defaultValue [])
    @ (joins |> List.collect (fun j -> fromItemReadTables defaultDb j.Table))

let private selectTables (defaultDb: string) (s: SelectStmt) : (string * string) list =
    selectReadTables defaultDb s

type private PrivilegeSource =
    { Qualifier: string
      Columns: string list
      Target: (string * string) option }

type private ColumnReference =
    | BareColumn of string
    | QualifiedColumn of qualifier: string * column: string
    | AllColumns
    | QualifiedAllColumns of qualifier: string

let private selectOrUnionProjectionNames =
    function
    | PlainSelect select -> projectionNames select
    | UnionSelect(first, _, _, _, _) -> projectionNames first

let private jsonTableColumnNames columns =
    let rec collect =
        function
        | ForOrdinality name
        | PathColumn(name, _, _, _, _)
        | ExistsColumn(name, _, _) -> [ name ]
        | NestedColumns(_, nested) -> nested |> List.collect collect

    columns |> List.collect collect

let private physicalSource store defaultDb (reference: TableRef) =
    let database = reference.Database |> Option.defaultValue defaultDb

    let columns =
        match scan store database reference.Table with
        | Ok(columns, _) -> columns |> List.map _.Name
        | Error _ -> storedViewColumns store database reference.Table

    { Qualifier = reference.Alias |> Option.defaultValue reference.Table
      Columns = columns
      Target = Some(database, reference.Table) }

let private virtualSource qualifier columns =
    { Qualifier = qualifier
      Columns = columns
      Target = None }

let private sourceForItem store defaultDb ctes =
    function
    | FromTable reference when reference.Database.IsNone ->
        ctes
        |> Map.tryFind (reference.Table.ToLowerInvariant())
        |> Option.map (virtualSource (reference.Alias |> Option.defaultValue reference.Table))
        |> Option.defaultWith (fun () -> physicalSource store defaultDb reference)
    | FromTable reference -> physicalSource store defaultDb reference
    | FromSubquery(body, alias)
    | FromLateral(body, alias) -> virtualSource alias (selectOrUnionProjectionNames body)
    | FromJsonTable(_, _, columns, alias) -> virtualSource alias (jsonTableColumnNames columns)

let private columnReferences aliases expression =
    Expression.collect
        (function
        | Col name when not (Set.contains (name.ToLowerInvariant()) aliases) -> Some [ BareColumn name ]
        | QualifiedCol(qualifier, column) -> Some [ QualifiedColumn(qualifier, column) ]
        | MatchAgainst(columns, _, _) ->
            columns
            |> List.map (fun column ->
                match column.Qualifier with
                | Some qualifier -> QualifiedColumn(qualifier, column.Name)
                | None -> BareColumn column.Name)
            |> Some
        | _ -> None)
        expression
    |> List.concat

let private targetRequirement privilege target column =
    privilege, OnColumn(fst target, snd target, column)

let private allColumnsRequirement privilege target =
    privilege, OnAllColumns(fst target, snd target)

let private requirementsForReferences privilege localSources outerSources references =
    let matchingColumn name sources =
        sources
        |> List.filter (fun source -> source.Columns |> List.exists (eqI name))

    let targets sources = sources |> List.choose _.Target

    references
    |> List.collect (function
        | BareColumn name ->
            let local = matchingColumn name localSources
            let resolved = if local.IsEmpty then matchingColumn name outerSources else local

            let resolved =
                if resolved.IsEmpty then
                    targets localSources
                    |> List.map (fun target ->
                        { Qualifier = ""
                          Columns = []
                          Target = Some target })
                else
                    resolved

            resolved
            |> List.choose _.Target
            |> List.map (fun target -> targetRequirement privilege target name)
        | QualifiedColumn(qualifier, column) ->
            let find sources = sources |> List.filter (fun source -> eqI source.Qualifier qualifier)
            let local = find localSources
            let resolved = if local.IsEmpty then find outerSources else local

            resolved
            |> List.choose _.Target
            |> List.map (fun target -> targetRequirement privilege target column)
        | AllColumns -> targets localSources |> List.map (allColumnsRequirement privilege)
        | QualifiedAllColumns qualifier ->
            localSources
            |> List.filter (fun source -> eqI source.Qualifier qualifier)
            |> List.choose _.Target
            |> List.map (allColumnsRequirement privilege))

let private joinKeyRequirements outerSources leftSources right (join: Join) =
    let columns =
        if not join.Using.IsEmpty then
            join.Using
        elif
            join.Kind = NaturalJoin
            || join.Kind = NaturalLeftJoin
            || join.Kind = NaturalRightJoin
        then
            let rightNames = right.Columns |> List.map _.ToLowerInvariant() |> Set.ofList

            leftSources
            |> List.collect _.Columns
            |> List.filter (fun column -> Set.contains (column.ToLowerInvariant()) rightNames)
            |> List.distinctBy _.ToLowerInvariant()
        else
            []

    columns
    |> List.collect (fun column ->
        requirementsForReferences
            "SELECT"
            (leftSources @ [ right ])
            outerSources
            [ QualifiedColumn(right.Qualifier, column); BareColumn column ])

let rec private selectColumnRequirements store defaultDb outerSources inheritedCtes (select: SelectStmt) =
    let cteRequirements, ctes =
        select.Ctes
        |> List.fold
            (fun (requirements, visible) cte ->
                let name = cte.CteName.ToLowerInvariant()
                let columns = if cte.CteColumns.IsEmpty then selectOrUnionProjectionNames cte.Body else cte.CteColumns
                let bodyScope = if cte.Recursive then Map.add name columns visible else visible
                let bodyRequirements = selectOrUnionColumnRequirements store defaultDb [] bodyScope cte.Body
                requirements @ bodyRequirements, Map.add name columns visible)
            ([], inheritedCtes)

    let initialSources, fromRequirements =
        match select.From with
        | None -> [], []
        | Some item ->
            let source = sourceForItem store defaultDb ctes item
            [ source ], fromItemColumnRequirements store defaultDb outerSources ctes [] item

    let sources, joinRequirements =
        select.Joins
        |> List.fold
            (fun (leftSources, requirements) join ->
                let right = sourceForItem store defaultDb ctes join.Table
                let visibleSources = leftSources @ [ right ]

                let nested =
                    fromItemColumnRequirements store defaultDb outerSources ctes leftSources join.Table

                let onRequirements =
                    expressionColumnRequirements store defaultDb visibleSources outerSources ctes Set.empty join.On

                let usingRequirements = joinKeyRequirements outerSources leftSources right join

                visibleSources, requirements @ nested @ onRequirements @ usingRequirements)
            (initialSources, [])

    let aliases =
        select.Projections
        |> List.choose snd
        |> List.map _.ToLowerInvariant()
        |> Set.ofList

    let groupAliases =
        aliases
        |> Set.filter (fun alias ->
            sources
            |> List.exists (fun source -> source.Columns |> List.exists (eqI alias))
            |> not)

    let plain expression =
        expressionColumnRequirements store defaultDb sources outerSources ctes Set.empty expression

    let projectionRequirements (expression, _) =
        let references =
            match expression with
            | Star None -> [ AllColumns ]
            | Star(Some qualifier) -> [ QualifiedAllColumns qualifier ]
            | _ -> []

        requirementsForReferences "SELECT" sources outerSources references @ plain expression

    let withAliases expression =
        expressionColumnRequirements store defaultDb sources outerSources ctes aliases expression

    let withGroupAliases expression =
        expressionColumnRequirements store defaultDb sources outerSources ctes groupAliases expression

    cteRequirements
    @ fromRequirements
    @ joinRequirements
    @ (select.Projections |> List.collect projectionRequirements)
    @ (select.Where |> Option.map plain |> Option.defaultValue [])
    @ (select.GroupBy |> List.collect withGroupAliases)
    @ (select.Having |> Option.map withAliases |> Option.defaultValue [])
    @ (select.OrderBy |> List.collect (fst >> withAliases))
    @ (select.Windows
       |> List.collect (snd >> OverSpec >> Expression.overExpressions)
       |> List.collect plain)
    @ (select.Limit |> Option.map plain |> Option.defaultValue [])
    @ (select.Offset |> Option.map plain |> Option.defaultValue [])

and private selectOrUnionColumnRequirements store defaultDb outerSources ctes =
    function
    | PlainSelect select -> selectColumnRequirements store defaultDb outerSources ctes select
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        let branchRequirements =
            selectColumnRequirements store defaultDb outerSources ctes first
            @ (rest
               |> List.collect (fun (_, select) ->
                   selectColumnRequirements store defaultDb outerSources ctes select))

        let trailingExpressions =
            (orderBy |> List.map fst) @ (limit |> Option.toList) @ (offset |> Option.toList)

        branchRequirements
        @ (trailingExpressions
           |> List.collect (fun expression ->
               Expression.collectSubqueries expression
               |> List.collect (selectColumnRequirements store defaultDb outerSources ctes)))

and private fromItemColumnRequirements store defaultDb outerSources ctes leftSources =
    function
    | FromTable _ -> []
    | FromSubquery(body, _) -> selectOrUnionColumnRequirements store defaultDb [] ctes body
    | FromLateral(body, _) -> selectOrUnionColumnRequirements store defaultDb (leftSources @ outerSources) ctes body
    | FromJsonTable(source, _, _, _) ->
        expressionColumnRequirements store defaultDb leftSources outerSources ctes Set.empty source

and private expressionColumnRequirements store defaultDb localSources outerSources ctes aliases expression =
    requirementsForReferences "SELECT" localSources outerSources (columnReferences aliases expression)
    @ (Expression.collectSubqueries expression
       |> List.collect (selectColumnRequirements store defaultDb (localSources @ outerSources) ctes))

let private mutationJoinScope store defaultDb ctes (initial: PrivilegeSource) (joins: Join list) =
    joins
    |> List.fold
        (fun (leftSources, requirements) (join: Join) ->
            let right = sourceForItem store defaultDb ctes join.Table
            let sources = leftSources @ [ right ]
            let nested = fromItemColumnRequirements store defaultDb [] ctes leftSources join.Table
            let predicate = expressionColumnRequirements store defaultDb sources [] ctes Set.empty join.On
            let keys = joinKeyRequirements [] leftSources right join
            sources, requirements @ nested @ predicate @ keys)
        ([ initial ], [])

let private targetColumns defaultDb (table: string) (columns: string list) privilege =
    let database, table = splitQualified defaultDb table
    let target = database, table

    if columns.IsEmpty then
        [ allColumnsRequirement privilege target ]
    else
        columns |> List.map (targetRequirement privilege target)

let private targetSource store defaultDb table =
    let database, table = splitQualified defaultDb table

    physicalSource
        store
        defaultDb
        { Database = Some database
          Table = table
          Alias = None
          Partitions = [] }

let private updateColumnRequirements store defaultDb (update: UpdateStmt) =
    let cteNames =
        update.Ctes
        |> List.map (fun cte -> cte.CteName.ToLowerInvariant(), if cte.CteColumns.IsEmpty then selectOrUnionProjectionNames cte.Body else cte.CteColumns)
        |> Map.ofList

    let sources, joinRequirements =
        mutationJoinScope store defaultDb cteNames (physicalSource store defaultDb update.From) update.Joins

    let targetOf (assignment: Assignment) =
        match assignment.Table with
        | None ->
            sources
            |> List.filter (fun source -> source.Columns |> List.exists (eqI assignment.Column))
            |> function
                | [ source ] -> Some source
                | _ -> None
        | Some qualifier -> sources |> List.tryFind (fun source -> eqI source.Qualifier qualifier)

    let writes =
        update.Assignments
        |> List.choose (fun assignment ->
            targetOf assignment
            |> Option.bind _.Target
            |> Option.map (fun target -> targetRequirement "UPDATE" target assignment.Column))

    let expressions =
        (update.Assignments |> List.map _.Value)
        @ (update.Where |> Option.toList)
        @ (update.OrderBy |> List.map fst)
        @ (update.Limit |> Option.toList)

    writes
    @ joinRequirements
    @ (expressions
       |> List.collect (expressionColumnRequirements store defaultDb sources [] cteNames Set.empty))
    @ (update.Ctes
       |> List.collect (fun cte -> selectOrUnionColumnRequirements store defaultDb [] Map.empty cte.Body))

let private deleteColumnRequirements store defaultDb (delete: DeleteStmt) =
    let cteNames =
        delete.Ctes
        |> List.map (fun cte -> cte.CteName.ToLowerInvariant(), if cte.CteColumns.IsEmpty then selectOrUnionProjectionNames cte.Body else cte.CteColumns)
        |> Map.ofList

    let sources, joinRequirements =
        mutationJoinScope store defaultDb cteNames (physicalSource store defaultDb delete.From) delete.Joins

    let expressions =
        (delete.Where |> Option.toList)
        @ (delete.OrderBy |> List.map fst)
        @ (delete.Limit |> Option.toList)

    joinRequirements
    @ (expressions
     |> List.collect (expressionColumnRequirements store defaultDb sources [] cteNames Set.empty))
    @ (delete.Ctes
       |> List.collect (fun cte -> selectOrUnionColumnRequirements store defaultDb [] Map.empty cte.Body))

let rec private statementColumnRequirements store defaultDb =
    function
    | Select select -> selectColumnRequirements store defaultDb [] Map.empty select
    | Union(first, rest, _, _, _) ->
        selectColumnRequirements store defaultDb [] Map.empty first
        @ (rest
           |> List.collect (fun (_, select) ->
               selectColumnRequirements store defaultDb [] Map.empty select))
    | Insert(table, columns, rows, onDuplicate, _) ->
        let source = targetSource store defaultDb table

        targetColumns defaultDb table columns "INSERT"
        @ (if onDuplicate.IsEmpty then [] else targetColumns defaultDb table (onDuplicate |> List.map fst) "UPDATE")
        @ (rows
           |> List.collect id
           |> List.collect (expressionColumnRequirements store defaultDb [ source ] [] Map.empty Set.empty))
        @ (onDuplicate
           |> List.collect (snd >> expressionColumnRequirements store defaultDb [ source ] [] Map.empty Set.empty))
    | InsertSelect(table, columns, select, onDuplicate, _) ->
        let source = targetSource store defaultDb table

        targetColumns defaultDb table columns "INSERT"
        @ (if onDuplicate.IsEmpty then [] else targetColumns defaultDb table (onDuplicate |> List.map fst) "UPDATE")
        @ selectColumnRequirements store defaultDb [] Map.empty select
        @ (onDuplicate
           |> List.collect (snd >> expressionColumnRequirements store defaultDb [ source ] [] Map.empty Set.empty))
    | Replace(table, columns, rows) ->
        targetColumns defaultDb table columns "INSERT"
        @ (rows
           |> List.collect id
           |> List.collect (expressionColumnRequirements store defaultDb [] [] Map.empty Set.empty))
    | ReplaceSelect(table, columns, select) ->
        targetColumns defaultDb table columns "INSERT"
        @ selectColumnRequirements store defaultDb [] Map.empty select
    | ReplaceSet(table, assignments) ->
        targetColumns defaultDb table (assignments |> List.map fst) "INSERT"
        @ (assignments
           |> List.collect (snd >> expressionColumnRequirements store defaultDb [] [] Map.empty Set.empty))
    | Update update -> updateColumnRequirements store defaultDb update
    | Delete delete -> deleteColumnRequirements store defaultDb delete
    | CreateTableAs(_, query, _) -> statementColumnRequirements store defaultDb query
    | CreateView view ->
        let database, _ = splitQualified defaultDb view.Name

        match Parser.parseViewDefinition view.Definition with
        | Ok definition -> statementColumnRequirements store database definition.Statement
        | Error _ -> []
    | Grant(privileges, level, _, _)
    | Revoke(privileges, level, _) ->
        match targetOfLevel defaultDb level with
        | OnTable(database, table) ->
            privileges
            |> List.collect (fun privilege ->
                privilege.Columns
                |> List.map (fun column -> privilege.Name, OnColumn(database, table, column)))
        | _ -> []
    | Explain(_, statement) -> statementColumnRequirements store defaultDb statement
    | _ -> []

let requiredPrivilegesForExpression (defaultDb: string) (expression: Expr) : (string * PrivTarget) list =
    exprReadTables defaultDb expression
    |> List.map (fun (database, table) -> "SELECT", OnTable(database, table))

/// The `(privilege, target)` pairs `stmt` needs. Table references are
/// collected recursively (`selectReadTables`/`exprReadTables`), so a table
/// read through a derived table or a subquery in any clause still requires
/// SELECT on it. SHOW/SET text probes are dispatched before this gate and
/// carry their own checks; `information_schema` is allow-listed in `check`.
let rec requiredPrivileges (defaultDb: string) (stmt: Statement) : (string * PrivTarget) list =
    let onTables priv tables = tables |> List.map (fun (db, t) -> priv, OnTable(db, t))
    let split (name: string) = splitQualified defaultDb name

    match stmt with
    | Select s -> onTables "SELECT" (selectTables defaultDb s)
    | Union(first, rest, _, _, _) ->
        onTables "SELECT" (selectTables defaultDb first @ (rest |> List.collect (snd >> selectTables defaultDb)))
    | Insert(table, _, rows, onDup, _) ->
        let readInExprs =
            (rows |> List.collect (List.collect (exprReadTables defaultDb)))
            @ (onDup |> List.collect (snd >> exprReadTables defaultDb))
            |> List.distinct

        onTables "INSERT" [ split table ]
        @ (if List.isEmpty onDup then [] else onTables "UPDATE" [ split table ])
        @ onTables "SELECT" readInExprs
    | InsertSelect(table, _, select, onDup, _) ->
        let readInExprs = onDup |> List.collect (snd >> exprReadTables defaultDb) |> List.distinct

        onTables "INSERT" [ split table ]
        @ (if List.isEmpty onDup then [] else onTables "UPDATE" [ split table ])
        @ onTables "SELECT" ((selectTables defaultDb select @ readInExprs) |> List.distinct)
    | Replace(table, _, rows) ->
        onTables "INSERT" [ split table ]
        @ onTables "DELETE" [ split table ]
        @ onTables "SELECT" (rows |> List.collect (List.collect (exprReadTables defaultDb)) |> List.distinct)
    | ReplaceSelect(table, _, select) ->
        onTables "INSERT" [ split table ]
        @ onTables "DELETE" [ split table ]
        @ onTables "SELECT" (selectTables defaultDb select |> List.distinct)
    | ReplaceSet(table, assignments) ->
        onTables "INSERT" [ split table ]
        @ onTables "DELETE" [ split table ]
        @ onTables "SELECT" (assignments |> List.collect (snd >> exprReadTables defaultDb) |> List.distinct)
    | Do expressions -> onTables "SELECT" (expressions |> List.collect (exprReadTables defaultDb) |> List.distinct)
    | Update u ->
        let cteTables, boundCtes = cteReadTablesIn Set.empty defaultDb u.Ctes

        let physicalSources =
            u.From
            :: (u.Joins
                |> List.choose (fun join ->
                    match join.Table with
                    | FromTable table when table.Database.IsNone && Set.contains (table.Table.ToLowerInvariant()) boundCtes -> None
                    | FromTable table -> Some table
                    | _ -> None))

        let sourceForQualifier qualifier =
            physicalSources
            |> List.tryFind (fun table ->
                let sourceName = table.Alias |> Option.defaultValue table.Table
                sourceName.Equals(qualifier, StringComparison.OrdinalIgnoreCase))

        let readInExprs =
            (u.Assignments |> List.collect (fun a -> exprReadTablesIn boundCtes defaultDb a.Value))
            @ (u.Where |> Option.map (exprReadTablesIn boundCtes defaultDb) |> Option.defaultValue [])
            @ (u.Joins
               |> List.collect (fun j ->
                   exprReadTablesIn boundCtes defaultDb j.On
                   @ (match j.Table with
                      | FromTable _ -> []
                      | source -> fromItemReadTablesIn boundCtes defaultDb source)))
            |> List.distinct

        let resolvedUpdateSources =
            u.Assignments
            |> List.map (fun assignment ->
                match assignment.Table with
                | None -> Some u.From
                | Some qualifier -> sourceForQualifier qualifier)

        let updatedTables =
            if resolvedUpdateSources |> List.exists Option.isNone then
                physicalSources
            else
                resolvedUpdateSources |> List.choose id
            |> List.map (fun table -> table.Database |> Option.defaultValue defaultDb, table.Table)
            |> List.distinct

        let joinedTables =
            if u.Joins.IsEmpty then
                []
            else
                physicalSources
                |> List.map (fun table -> table.Database |> Option.defaultValue defaultDb, table.Table)
                |> List.distinct

        onTables "UPDATE" updatedTables
        @ onTables "SELECT" ((cteTables @ joinedTables @ readInExprs) |> List.distinct)
    | Delete d ->
        let cteTables, boundCtes = cteReadTablesIn Set.empty defaultDb d.Ctes

        let readInExprs =
            (d.Where |> Option.map (exprReadTablesIn boundCtes defaultDb) |> Option.defaultValue [])
            @ (d.Joins
               |> List.collect (fun j ->
                   exprReadTablesIn boundCtes defaultDb j.On
                   @ (match j.Table with
                      | FromTable _ -> []
                      | source -> fromItemReadTablesIn boundCtes defaultDb source)))
            |> List.distinct

        let deletedTables =
            (d.From.Database |> Option.defaultValue defaultDb, d.From.Table)
            :: (d.Joins
                |> List.choose (fun join ->
                    match join.Table with
                    | FromTable table when table.Database.IsNone && Set.contains (table.Table.ToLowerInvariant()) boundCtes -> None
                    | FromTable table -> Some(table.Database |> Option.defaultValue defaultDb, table.Table)
                    | _ -> None))

        onTables "DELETE" deletedTables
        @ onTables "SELECT" ((cteTables @ readInExprs) |> List.distinct)
    | CreateTable table -> onTables "CREATE" [ split table.Name ]
    | CreateTableLike(name, source, _) -> onTables "CREATE" [ split name ] @ onTables "SELECT" [ split source ]
    | CreateTableAs(name, query, _) -> onTables "CREATE" [ split name ] @ requiredPrivileges defaultDb query
    | DropTable(names, _) -> onTables "DROP" (names |> List.map split)
    | Truncate table -> onTables "DROP" [ split table ]
    | AlterTable(table, _) -> onTables "ALTER" [ split table ]
    | RenameTable pairs -> onTables "ALTER" (pairs |> List.map (fst >> split))
    | CreateIndex(_, table, _, _, _, _) -> onTables "INDEX" [ split table ]
    | DropIndexStmt(_, table, _) -> onTables "INDEX" [ split table ]
    | CreateDatabase(name, _) -> [ "CREATE", OnDb name ]
    | DropDatabase(name, _) -> [ "DROP", OnDb name ]
    | AlterDatabase name -> [ "ALTER", OnDb(name |> Option.defaultValue defaultDb) ]
    | CreateUser _
    | DropUser _
    | RenameUser _
    | AlterUser _ -> [ "CREATE USER", Global ]
    | CreateRole _ -> [ "CREATE ROLE", Global ]
    | DropRole _ -> [ "DROP ROLE", Global ]
    | Grant(privs, level, _, _)
    | Revoke(privs, level, _) ->
        // MySQL requires the grantor to hold grant option *at the target's
        // level* (a db-scoped WITH GRANT OPTION delegates that db, no global
        // Grant_priv needed) plus every privilege being granted, also at
        // that level — `check`'s global ⊃ db ⊃ table hierarchy supplies the
        // "or higher" part.
        let target = targetOfLevel defaultDb level

        // A dynamic privilege carries its own grant option in
        // mysql.global_grants. Static privileges still share the grant
        // option stored at the target level.
        let privilegeRequirements, grantOptionRequirements =
            match expandPrivs (privilegeNames privs |> List.filter (fun privilege -> privilege <> "GRANT OPTION")) target with
            | Result.Ok resolved ->
                let privileges =
                    (resolved.Static |> List.map (fun privilege -> privilege.Sql)) @ resolved.Dynamic
                    |> List.map (fun privilege -> privilege, target)

                let grantOption =
                    if resolved.Static.IsEmpty && not resolved.Dynamic.IsEmpty then
                        []
                    else
                        [ "GRANT OPTION", target ]

                privileges, grantOption
            | Result.Error _ -> [], [] // invalid list — the executor reports it

        grantOptionRequirements @ privilegeRequirements
    | GrantRoles _
    | RevokeRoles _
    | SetRole _
    | SetDefaultRole _ -> []
    // CREATE TRIGGER carries its subject table in the statement. DROP's
    // subject is resolved by `requiredPrivilegesInStore` below.
    | CreateTrigger(_, _, _, table, _, _) -> onTables "TRIGGER" [ split table ]
    | SetTriggerNew _ -> []
    | DropTrigger _ -> []
    | CreateView view ->
        let viewDb, _ = split view.Name

        let replaces =
            match view.Action with
            | CreateViewDdl orReplace -> orReplace
            | AlterViewDdl -> true

        let own =
            onTables "CREATE VIEW" [ split view.Name ]
            @ if replaces then onTables "DROP" [ split view.Name ] else []

        match Parser.parseViewDefinition view.Definition with
        | Ok definition -> own @ requiredPrivileges viewDb definition.Statement
        | Error _ -> own
    | DropView(names, _) -> onTables "DROP" (names |> List.map split)
    | ChecksumTables(tables, _) -> onTables "SELECT" (tables |> List.map split)
    | Explain(_, inner) -> requiredPrivileges defaultDb inner

/// Adds privilege requirements whose target can only be resolved from the
/// live catalog rather than from the statement shape alone.
let requiredPrivilegesInStore (store: Store) (defaultDb: string) (stmt: Statement) : (string * PrivTarget) list =
    let statementRequirements =
        match stmt with
        | DropTrigger(name, _) ->
            match scanList store "mysql" "triggers" with
            | Ok(_, rows) ->
                rows
                |> List.tryPick (fun row ->
                    row
                    |> SystemCatalog.Trigger.tryRead
                    |> Option.bind (fun trigger ->
                        if eqI trigger.Name name && eqI trigger.Schema defaultDb then
                            Some [ "TRIGGER", OnTable(trigger.Schema, trigger.Table) ]
                        else
                            None))
                |> Option.defaultValue []
            | Error _ -> []
        | _ -> requiredPrivileges defaultDb stmt

    statementRequirements @ statementColumnRequirements store defaultDb stmt
    |> List.distinct

/// Checks one selected account against every required privilege, denying with
/// MySQL's
/// 1142 (table), 1044 (database), or 1227 (admin privilege) shape.
let checkForAccount (store: Store) (wanted: Account) (required: (string * PrivTarget) list) : Result<unit, int * string> =
    match required with
    | [] -> Ok()
    | _ ->
        let user = wanted.Name
        let userRow = tryUserRowForAccount store wanted

        // Fast path: every requirement satisfied by the user's own global
        // row — root's all-Y row is the overwhelming common case, and this
        // runs per statement, so exit before any of the db/table lookup
        // machinery below is even allocated.
        let globallyHeld (privSql: string) =
            if Privileges.contains privSql then
                hasDynamicPrivilege store wanted privSql
            else
                match userRow with
                | None -> false
                | Some(cols, row) ->
                    let col =
                        if privSql = "GRANT OPTION" then
                            Some "Grant_priv"
                        else
                            privBySql privSql |> Option.map (fun d -> d.UserCol)

                    match col with
                    | Some c -> userColumnText cols row c = "Y"
                    | None ->
                        // An unknown privilege name means the emitter and the
                        // static vocabulary drifted — fail closed and log, never
                        // silently grant.
                        Log.diagnostic "fsdb: auth: unknown privilege '%s' required — denying" privSql
                        false

        if required |> List.forall (fst >> globallyHeld) then
            Ok()
        else

        let hasGlobal (def: PrivDef) =
            match userRow with
            | Some(cols, row) -> userColumnText cols row def.UserCol = "Y"
            | None -> false

        let dbRowGrants =
            lazy
                (match scanList store "mysql" "db" with
                 | Result.Ok(cols, rows) -> Some(cols, rows)
                 | Result.Error _ -> None)

        let tablePrivGrants =
            lazy
                (match scanList store "mysql" "tables_priv" with
                 | Result.Ok(cols, rows) -> Some(cols, rows)
                 | Result.Error _ -> None)

        let columnPrivGrants =
            lazy
                (match scanList store "mysql" "columns_priv" with
                 | Result.Ok(cols, rows) -> Some(cols, rows)
                 | Result.Error _ -> None)

        // Every grant-table lookup below is the same question — "does some
        // row satisfy a predicate per named column?" — asked with different
        // columns. One matcher, three cell predicates.
        let textIs expected =
            function
            | VString s -> eqI s expected
            | _ -> false

        let isYes v = v = VString "Y"

        let hasSetMember memberName =
            function
            | VString s -> setMembers s |> List.exists (eqI memberName)
            | _ -> false

        let rowExists (scanned: (ColumnDef list * Value[] list) option) (conds: (string * (Value -> bool)) list) =
            match scanned with
            | None -> false
            | Some(cols, rows) ->
                match conds |> traverse (fun (name, p) -> resolveColumn cols name |> Result.map (fun i -> i, p)) with
                | Result.Error _ -> false
                | Result.Ok resolved -> rows |> List.exists (fun r -> resolved |> List.forall (fun (i, p) -> p r.[i]))

        let mine = [ "User", (=) (VString user); "Host", textIs wanted.Host ]

        let hasDb (def: PrivDef) (db: string) =
            match def.DbCol with
            | Some dbCol -> rowExists dbRowGrants.Value (mine @ [ "Db", textIs db; dbCol, isYes ])
            | None -> false

        let hasTable (def: PrivDef) (db: string) (table: string) =
            match def.TablePriv with
            | Some setName ->
                rowExists
                    tablePrivGrants.Value
                    (mine @ [ "Db", textIs db; "Table_name", textIs table; "Table_priv", hasSetMember setName ])
            | None -> false

        let hasColumn (def: PrivDef) (db: string) (table: string) (column: string) =
            match def.TablePriv with
            | Some setName ->
                rowExists
                    columnPrivGrants.Value
                    (mine
                     @ [ "Db", textIs db
                         "Table_name", textIs table
                         "Column_name", textIs column
                         "Column_priv", hasSetMember setName ])
            | None -> false

        let hasAnyColumn (def: PrivDef) (db: string) (table: string) =
            match def.TablePriv, columnPrivGrants.Value with
            | Some setName, Some(columns, rows) ->
                rows
                |> List.exists (fun row ->
                    rowAccount columns row |> Option.exists (sameAccount wanted)
                    && eqI (userColumnText columns row "Db") db
                    && eqI (userColumnText columns row "Table_name") table
                    && setMembers (userColumnText columns row "Column_priv") |> List.exists (eqI setName))
            | _ -> false

        let hasGlobalGrantOption () =
            match userRow with
            | Some(cols, row) -> userColumnText cols row "Grant_priv" = "Y"
            | None -> false

        // Grant option below the global level: mysql.db's `Grant_priv` for
        // (user, db), or tables_priv's `Grant` SET member for the table.
        let hasDbGrantOption (db: string) =
            rowExists dbRowGrants.Value (mine @ [ "Db", textIs db; "Grant_priv", isYes ])

        let hasTableGrantOption (db: string) (table: string) =
            rowExists
                tablePrivGrants.Value
                (mine @ [ "Db", textIs db; "Table_name", textIs table; "Table_priv", hasSetMember "Grant" ])

        let checkOne (privSql: string, target: PrivTarget) : Result<unit, int * string> =
            if Privileges.contains privSql then
                if target = Global && hasDynamicPrivilege store wanted privSql then
                    Ok()
                else
                    Error(
                        1227,
                        sprintf "Access denied; you need (at least one of) the %s privilege(s) for this operation" privSql
                    )
            elif privSql = "GRANT OPTION" then
                // GRANT/REVOKE themselves: grant option held at the target's
                // level or higher (global Grant_priv ⊃ mysql.db row ⊃
                // tables_priv `Grant` member). Denials use MySQL's
                // level-shaped codes (oracle-verified): 1045 global, 1044
                // db, 1142 table.
                let allowed =
                    hasGlobalGrantOption ()
                    || (match target with
                        | Global -> false
                        | OnDb db -> hasDbGrantOption db
                        | OnTable(db, table)
                        | OnColumn(db, table, _)
                        | OnAllColumns(db, table) -> hasDbGrantOption db || hasTableGrantOption db table)

                if allowed then
                    Ok()
                else
                    match target with
                    | Global -> Error(1045, sprintf "Access denied for user '%s'@'%s' (using password: YES)" user wanted.Host)
                    | OnDb db -> Error(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" user wanted.Host db)
                    | OnTable(_, table) ->
                        Error(1142, sprintf "GRANT command denied to user '%s'@'localhost' for table '%s'" user table)
                    | OnColumn(_, table, column) ->
                        Error(1143, sprintf "GRANT command denied to user '%s'@'localhost' for column '%s' in table '%s'" user column table)
                    | OnAllColumns(_, table) ->
                        Error(1142, sprintf "GRANT command denied to user '%s'@'localhost' for table '%s'" user table)
            else
                match privBySql privSql with
                | None ->
                    Log.diagnostic "fsdb: auth: unknown privilege '%s' required — denying" privSql
                    Error(1227, sprintf "Access denied; you need (at least one of) the %s privilege(s) for this operation" privSql)
                | Some def ->
                    let allowed =
                        hasGlobal def
                        || (match target with
                            | Global -> false
                            | OnDb db
                            | OnTable(db, _)
                            | OnColumn(db, _, _)
                            | OnAllColumns(db, _) when eqI db "information_schema" && eqI privSql "SELECT" -> true
                            | OnDb db -> hasDb def db
                            | OnTable(db, table) -> hasDb def db || hasTable def db table || hasAnyColumn def db table
                            | OnColumn(db, table, column) ->
                                hasDb def db || hasTable def db table || hasColumn def db table column
                            | OnAllColumns(db, table) -> hasDb def db || hasTable def db table)

                    if allowed then
                        Ok()
                    else
                        match target with
                        | Global ->
                            Error(
                                1227,
                                sprintf "Access denied; you need (at least one of) the %s privilege(s) for this operation" privSql
                            )
                        | OnDb db -> Error(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" user wanted.Host db)
                        | OnTable(_, table) ->
                            Error(1142, sprintf "%s command denied to user '%s'@'localhost' for table '%s'" privSql user table)
                        | OnColumn(_, table, column) ->
                            Error(
                                1143,
                                sprintf "%s command denied to user '%s'@'localhost' for column '%s' in table '%s'" privSql user column table
                            )
                        | OnAllColumns(_, table) ->
                            Error(1142, sprintf "%s command denied to user '%s'@'localhost' for table '%s'" privSql user table)

        required |> traverse checkOne |> Result.map ignore

let checkForAccountWithRoles
    (store: Store)
    (wanted: Account)
    (activeRoles: Account list)
    (required: (string * PrivTarget) list)
    : Result<unit, int * string> =
    let roles = roleClosure store activeRoles

    required
    |> traverse (fun requirement ->
        let direct = checkForAccount store wanted [ requirement ]

        if Result.isOk direct || roles |> List.exists (fun role -> checkForAccount store role [ requirement ] |> Result.isOk) then
            Ok()
        else
            direct)
    |> Result.map ignore

let checkDynamicGrantOptionsForAccount
    (store: Store)
    (wanted: Account)
    (activeRoles: Account list)
    (privileges: string list)
    (target: PrivTarget)
    : Result<unit, int * string> =
    expandPrivs (privileges |> List.filter (fun privilege -> privilege <> "GRANT OPTION")) target
    |> Result.bind (fun resolved ->
        let actors = effectiveAccounts store wanted activeRoles
        let grantable privilege = actors |> List.exists (fun actor -> hasGrantableDynamicPrivilege store actor privilege)

        if resolved.Dynamic |> List.forall grantable then
            Ok()
        else
            Error(
                1227,
                "Access denied; you need (at least one of) the GRANT OPTION privilege(s) for this operation"
            ))

/// Checks the conventional `'name'@'%'` identity used by embedded callers.
let check (store: Store) (user: string) (required: (string * PrivTarget) list) =
    checkForAccount store (account user "%") required

// ---------------------------------------------------------------------------
// SHOW GRANTS rendering.
// ---------------------------------------------------------------------------

let renderCreateUserForAccount (store: Store) (wanted: Account) : Result<string * string, int * string> =
    match tryUserRowForAccount store wanted with
    | None -> Error(1396, sprintf "Operation SHOW CREATE USER failed for '%s'@'%s'" wanted.Name wanted.Host)
    | Some(cols, row) ->
        let name = wanted.Name
        let host = userColumnText cols row "Host"
        let plugin = userColumnText cols row "plugin"
        let hash = userColumnText cols row "authentication_string"
        let accountState = if isAccountLocked cols row then "LOCK" else "UNLOCK"
        let tlsRequirement =
            match accountTlsRequirement cols row with
            | RequireNone -> "NONE"
            | RequireSsl -> "SSL"
            | RequireX509 -> "X509"
        let limits = accountLimits cols row
        let resources =
            [ "MAX_QUERIES_PER_HOUR", limits.MaxQuestions
              "MAX_UPDATES_PER_HOUR", limits.MaxUpdates
              "MAX_CONNECTIONS_PER_HOUR", limits.MaxConnectionsPerHour
              "MAX_USER_CONNECTIONS", limits.MaxUserConnections ]
            |> List.choose (fun (label, value) -> if value = 0u then None else Some(sprintf "%s %u" label value))
            |> function
                | [] -> ""
                | values -> " WITH " + String.concat " " values
        let passwordExpiration =
            if userColumnText cols row "password_expired" = "Y" then
                "PASSWORD EXPIRE"
            else
                match userColumnValue cols row "password_lifetime" with
                | Some(VInt 0L)
                | Some(VUInt 0UL) -> "PASSWORD EXPIRE NEVER"
                | Some(VInt days) when days > 0L -> sprintf "PASSWORD EXPIRE INTERVAL %d DAY" days
                | Some(VUInt days) when days > 0UL -> sprintf "PASSWORD EXPIRE INTERVAL %d DAY" days
                | _ -> "PASSWORD EXPIRE DEFAULT"
        let account = sprintf "`%s`@`%s`" (name.Replace("`", "``")) (host.Replace("`", "``"))

        Ok(
            sprintf "CREATE USER for %s@%s" name host,
            sprintf
                "CREATE USER %s IDENTIFIED WITH '%s' AS '%s' REQUIRE %s%s %s ACCOUNT %s PASSWORD HISTORY DEFAULT PASSWORD REUSE INTERVAL DEFAULT PASSWORD REQUIRE CURRENT DEFAULT"
                account
                plugin
                hash
                tlsRequirement
                resources
                passwordExpiration
                accountState
        )

let renderCreateUser (store: Store) (name: string) = renderCreateUserForAccount store (account name "%")

/// Whether `user` holds a global privilege — the gate for PROCESS-scoped
/// visibility (PROCESSLIST, KILL) and mysql-schema reads. Reuses `check`'s
/// hierarchy, so root's all-Y row and any GLOBAL grant satisfy it.
let hasGlobalPrivForAccount (store: Store) (wanted: Account) (privSql: string) : bool =
    if Privileges.contains privSql then
        hasDynamicPrivilege store wanted privSql
    else
        match checkForAccount store wanted [ privSql, Global ] with
        | Result.Ok() -> true
        | Result.Error _ -> false

let hasGlobalPrivForAccountWithRoles store wanted activeRoles privSql =
    checkForAccountWithRoles store wanted activeRoles [ privSql, Global ] |> Result.isOk

let hasGlobalPriv (store: Store) (user: string) (privSql: string) = hasGlobalPrivForAccount store (account user "%") privSql

/// Whether `SHOW DATABASES` may reveal `db` to `user`: the global SHOW
/// DATABASES privilege sees everything; otherwise any database- or
/// table-scoped grant reveals its containing database. `information_schema`
/// is visible to every authenticated account, matching MySQL.
let canSeeDatabaseForAccount (store: Store) (wanted: Account) (db: string) : bool =
    if eqI db "information_schema" || hasGlobalPrivForAccount store wanted "SHOW DATABASES" then
        true
    elif staticPrivileges |> List.exists (fun def -> checkForAccount store wanted [ def.Sql, OnDb db ] |> Result.isOk) then
        true
    else
        match scanList store "mysql" "tables_priv" with
        | Result.Error _ -> false
        | Result.Ok(cols, rows) ->
            match resolveColumn cols "Db", resolveColumn cols "Table_priv", resolveColumn cols "Column_priv" with
            | Ok dbIdx, Ok tablePrivIdx, Ok columnPrivIdx ->
                rows
                |> List.exists (fun row ->
                    rowAccount cols row |> Option.exists (sameAccount wanted)
                    && (match row.[dbIdx] with | VString value -> eqI value db | _ -> false)
                    && (row.[tablePrivIdx] <> VString "" || row.[columnPrivIdx] <> VString ""))
            | _ -> false

let canSeeDatabase (store: Store) (user: string) (db: string) = canSeeDatabaseForAccount store (account user "%") db

let canSeeDatabaseForAccountWithRoles store wanted activeRoles db =
    effectiveAccounts store wanted activeRoles
    |> List.exists (fun actor -> canSeeDatabaseForAccount store actor db)

/// Whether any privilege at table scope or above makes a table visible in
/// metadata views.
let canSeeTableForAccount (store: Store) (wanted: Account) (db: string) (table: string) : bool =
    staticPrivileges |> List.exists (fun def -> checkForAccount store wanted [ def.Sql, OnTable(db, table) ] |> Result.isOk)

let canSeeTableForAccountWithRoles store wanted activeRoles db table =
    effectiveAccounts store wanted activeRoles
    |> List.exists (fun actor -> canSeeTableForAccount store actor db table)

let canSeeTable (store: Store) (user: string) (db: string) (table: string) = canSeeTableForAccount store (account user "%") db table

let canSeeColumnForAccount store wanted db table column =
    staticPrivileges
    |> List.exists (fun privilege ->
        checkForAccount store wanted [ privilege.Sql, OnColumn(db, table, column) ]
        |> Result.isOk)

let canSeeColumnForAccountWithRoles store wanted activeRoles db table column =
    effectiveAccounts store wanted activeRoles
    |> List.exists (fun actor -> canSeeColumnForAccount store actor db table column)

let columnPrivilegesForAccountWithRoles store wanted activeRoles db table column =
    columnPrivilegeDefs
    |> List.filter (fun privilege ->
        checkForAccountWithRoles store wanted activeRoles [ privilege.Sql, OnColumn(db, table, column) ]
        |> Result.isOk)
    |> List.map (_.Sql >> _.ToLowerInvariant())

/// A privilege list rendered MySQL-style: every static privilege → `ALL
/// PRIVILEGES`, none → `USAGE`, otherwise the names in column order.
let private renderPrivList (granted: PrivDef list) (all: PrivDef list) : string =
    if List.length granted = List.length all then "ALL PRIVILEGES"
    elif granted.IsEmpty then "USAGE"
    else granted |> List.map (fun d -> d.Sql) |> String.concat ", "

/// Combines grants from selected roles under the account named in the output;
/// MySQL's `USING` form materializes inherited privileges rather than showing
/// separate grants to each role account.
let renderGrantsForAccountUsing
    (store: Store)
    (wanted: Account)
    (usingRoles: Account list option)
    : Result<string * string list, int * string> =
    match tryUserRowForAccount store wanted with
    | None -> Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" wanted.Name wanted.Host)
    | Some _ ->
        let name = wanted.Name
        let host = wanted.Host
        let quoted = quotedAccount wanted

        let resolveActors =
            match usingRoles with
            | None -> Ok [ wanted ]
            | Some roles ->
                let selection = NamedRoles(roles |> List.map (fun role -> role.Name, role.Host))

                resolveRoleSelection store wanted selection
                |> Result.map (fun active -> wanted :: roleClosure store active)

        resolveActors
        |> Result.map (fun actors ->
            let belongsToActor columns row =
                rowAccount columns row
                |> Option.exists (fun rowOwner -> actors |> List.exists (sameAccount rowOwner))

            let userRows =
                match scanList store "mysql" "user" with
                | Ok(columns, rows) -> columns, rows |> List.filter (belongsToActor columns)
                | Error _ -> [], []

            let globalGranted =
                let columns, rows = userRows

                staticPrivileges
                |> List.filter (fun privilege ->
                    rows |> List.exists (fun row -> userColumnText columns row privilege.UserCol = "Y"))

            let globalGrantOption =
                let columns, rows = userRows
                rows |> List.exists (fun row -> userColumnText columns row "Grant_priv" = "Y")

            let globalLine =
                sprintf
                    "GRANT %s ON *.* TO %s%s"
                    (renderPrivList globalGranted staticPrivileges)
                    quoted
                    (if globalGrantOption then " WITH GRANT OPTION" else "")

            let dynamicLines =
                actors
                |> List.collect (dynamicGrantsForAccount store)
                |> List.groupBy (fun grant -> grant.Privilege)
                |> List.map (fun (privilege, grants) ->
                    { Privilege = privilege
                      Grantable = grants |> List.exists _.Grantable })
                |> List.groupBy _.Grantable
                |> List.sortBy fst
                |> List.map (fun (grantable, grants) ->
                    sprintf
                        "GRANT %s ON *.* TO %s%s"
                        (grants |> List.map _.Privilege |> List.sort |> String.concat ",")
                        quoted
                        (if grantable then " WITH GRANT OPTION" else ""))

            let dbLines =
                match scanList store "mysql" "db" with
                | Result.Error _ -> []
                | Result.Ok(columns, rows) ->
                    let dbLevel = staticPrivileges |> List.filter (fun privilege -> privilege.DbCol.IsSome)

                    rows
                    |> List.filter (belongsToActor columns)
                    |> List.groupBy (fun row -> userColumnText columns row "Db")
                    |> List.sortBy fst
                    |> List.map (fun (database, grants) ->
                        let granted =
                            dbLevel
                            |> List.filter (fun privilege ->
                                grants
                                |> List.exists (fun row -> userColumnText columns row privilege.DbCol.Value = "Y"))

                        let hasOption = grants |> List.exists (fun row -> userColumnText columns row "Grant_priv" = "Y")

                        sprintf
                            "GRANT %s ON `%s`.* TO %s%s"
                            (renderPrivList granted dbLevel)
                            database
                            quoted
                            (if hasOption then " WITH GRANT OPTION" else ""))

            let tableLines =
                let tableGrants =
                    match scanList store "mysql" "tables_priv" with
                    | Result.Error _ -> []
                    | Result.Ok(columns, rows) ->
                        rows
                        |> List.filter (belongsToActor columns)
                        |> List.map (fun row ->
                            (userColumnText columns row "Db", userColumnText columns row "Table_name"),
                            setMembers (userColumnText columns row "Table_priv"))

                let columnGrants =
                    match scanList store "mysql" "columns_priv" with
                    | Result.Error _ -> []
                    | Result.Ok(columns, rows) ->
                        rows
                        |> List.filter (belongsToActor columns)
                        |> List.map (fun row ->
                            (userColumnText columns row "Db", userColumnText columns row "Table_name"),
                            userColumnText columns row "Column_name",
                            setMembers (userColumnText columns row "Column_priv"))

                let keys =
                    (tableGrants |> List.map fst) @ (columnGrants |> List.map (fun (key, _, _) -> key))
                    |> List.distinct
                    |> List.sort

                keys
                |> List.choose (fun ((database, table) as key) ->
                    let members =
                        tableGrants
                        |> List.choose (fun (candidate, privileges) -> if candidate = key then Some privileges else None)
                        |> List.concat

                    let hasMember name = members |> List.exists (eqI name)

                    let tablePrivileges =
                        staticPrivileges
                        |> List.filter (fun privilege -> privilege.TablePriv |> Option.exists hasMember)
                        |> List.map _.Sql

                    let columnPrivileges =
                        columnPrivilegeDefs
                        |> List.choose (fun privilege ->
                            let setName = privilege.TablePriv |> Option.defaultValue ""

                            let columns =
                                columnGrants
                                |> List.choose (fun (candidate, column, privileges) ->
                                    if candidate = key && privileges |> List.exists (eqI setName) then
                                        Some column
                                    else
                                        None)
                                |> List.distinctBy _.ToLowerInvariant()
                                |> List.sortBy _.ToLowerInvariant()

                            if columns.IsEmpty then
                                None
                            else
                                let rendered = columns |> List.map (fun column -> "`" + column.Replace("`", "``") + "`")
                                Some(sprintf "%s (%s)" privilege.Sql (String.concat ", " rendered)))

                    let privileges = tablePrivileges @ columnPrivileges

                    if privileges.IsEmpty && not (hasMember "Grant") then
                        None
                    else
                        Some(
                            sprintf
                                "GRANT %s ON `%s`.`%s` TO %s%s"
                                (if privileges.IsEmpty then "USAGE" else String.concat ", " privileges)
                                database
                                table
                                quoted
                                (if hasMember "Grant" then " WITH GRANT OPTION" else "")
                        ))

            let roleLines =
                directRoleGrantsForAccount store wanted
                |> List.groupBy _.AdminOption
                |> List.sortBy fst
                |> List.map (fun (adminOption, grants) ->
                    sprintf
                        "GRANT %s TO %s%s"
                        (grants |> List.map (_.Role >> quotedAccount) |> String.concat ",")
                        quoted
                        (if adminOption then " WITH ADMIN OPTION" else ""))

            sprintf "Grants for %s@%s" name host, globalLine :: dynamicLines @ dbLines @ tableLines @ roleLines)

let renderGrantsForAccount store wanted = renderGrantsForAccountUsing store wanted None

let renderGrants (store: Store) (name: string) = renderGrantsForAccount store (account name "%")
