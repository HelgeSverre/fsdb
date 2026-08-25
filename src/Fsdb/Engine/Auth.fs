/// Account lookup and mysql_native_password verification against the
/// `mysql.user` system table (see `Storage`'s bootstrap). The rule the
/// handshake enforces matches real MySQL: an account must exist, a
/// non-empty stored hash is scramble-verified, and an empty
/// `authentication_string` (no password set) accepts only an empty offered
/// password.
module Fsdb.Auth

open System
open System.Collections.Generic
open System.Net
open System.Security.Cryptography
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
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

/// Whether two account names identify the same host-qualified account.
let sameAccount left right =
    left.Name = right.Name && String.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)

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

/// The stored password hash for a user row — `""` means no password is set.
let storedPasswordHash (cols: ColumnDef list) (row: Value[]) : string = userColumnText cols row "authentication_string"

// ---------------------------------------------------------------------------
// The static privilege vocabulary: SQL name ↔ mysql.user column, plus where
// (if anywhere) the privilege exists at db level (mysql.db column) and table
// level (a `tables_priv.Table_priv` SET member). Order matches mysql.user's
// column order — SHOW GRANTS and USER_PRIVILEGES render in this order, same
// as MySQL. GRANT OPTION is deliberately absent (it's `Grant_priv`/the
// `WITH GRANT OPTION` suffix, not a grantable list member); roles/dynamic
// privileges don't exist here at all.
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

/// `CREATE USER 'name'@'host' [IDENTIFIED BY 'pw']` — one account.
let createUser (store: Store) (name: string) (host: string) (password: string option) : Result<unit, int * string> =
    let wanted = account name host

    if (tryUserRowForAccount store wanted).IsSome then
        operationFailed "CREATE USER" name host
    else
        let hash = password |> Option.map nativePasswordHash |> Option.defaultValue ""

        match
            insertRows
                store
                "mysql"
                "user"
                (Some [ "Host"; "User"; "plugin"; "authentication_string" ])
                [ [ VString wanted.Host; VString name; VString "mysql_native_password"; VString hash ] ]
        with
        | Ok _ -> Ok()
        | Error e -> Error(toMySqlError e)

/// `DROP USER 'name'@'host'` — removes the account and any of its rows in
/// the other grant tables.
let dropUser (store: Store) (name: string) (host: string) : Result<unit, int * string> =
    let deleteWhere (table: string) =
        match scanList store "mysql" table with
        | Error _ -> ()
        | Ok(cols, _) ->
            deleteRows store "mysql" table (fun row -> Ok(rowAccount cols row |> Option.exists (sameAccount (account name host)))) |> ignore

    if (tryUserRowForAccount store (account name host)).IsNone then
        operationFailed "DROP USER" name host
    else
        deleteWhere "user"
        deleteWhere "db"
        deleteWhere "tables_priv"
        deleteWhere "columns_priv"
        deleteWhere "global_grants"
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

    if (tryUserRowForAccount store oldAccount).IsNone || (tryUserRowForAccount store newAccount).IsSome then
        operationFailed "RENAME USER" oldName oldHost
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

        [ "user"; "db"; "tables_priv"; "columns_priv"; "global_grants" ]
        |> traverse renameRows
        |> Result.map ignore

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

/// `ALTER USER ... IDENTIFIED BY 'pw'` / `SET PASSWORD [FOR user] = 'pw'` —
/// rewrites the stored hash (empty password clears it back to
/// accept-anything).
let setPassword (store: Store) (name: string) (host: string) (password: string) : Result<unit, int * string> =
    let wanted = account name host

    if (tryUserRowForAccount store wanted).IsNone then
        operationFailed "ALTER USER" name host
    else
        let hash = if password = "" then "" else nativePasswordHash password

        updateSystemRows store "user" (matchUserRow wanted) [ "authentication_string", VString hash ]
        |> Result.map ignore

let setAccountLocked (store: Store) (name: string) (host: string) (locked: bool) : Result<unit, int * string> =
    let wanted = account name host

    if (tryUserRowForAccount store wanted).IsNone then
        operationFailed "ALTER USER" name host
    else
        updateSystemRows store "user" (matchUserRow wanted) [ "account_locked", VString(if locked then "Y" else "N") ]
        |> Result.map ignore

let isAccountLocked (cols: ColumnDef list) (row: Value[]) = userColumnText cols row "account_locked" = "Y"

// ---------------------------------------------------------------------------
// GRANT / REVOKE and privilege checks. Scope hierarchy is MySQL's:
// global (mysql.user) ⊃ db (mysql.db) ⊃ table (mysql.tables_priv).
// ponytail: no column-level privileges, no roles, no partial-revoke — the
// three levels above are what real clients and apps actually exercise.
// ---------------------------------------------------------------------------

/// Where a privilege applies.
type PrivTarget =
    | Global
    | OnDb of db: string
    | OnTable of db: string * table: string

/// Resolves `Ast.Grant`/`Revoke`'s `(db, table)` level encoding against the
/// session database (a bare `ON t` means the current db's table).
let targetOfLevel (defaultDb: string) (level: string option * string option) : PrivTarget =
    match level with
    | None, None -> Global
    | Some db, None -> OnDb db
    | Some db, Some t -> OnTable(db, t)
    | None, Some t -> OnTable(defaultDb, t)

let private eqI (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// Splits a `Table_priv`/`Column_priv` SET string into its members — public
/// because `InformationSchema.TABLE_PRIVILEGES` reads the same encoding.
let setMembers (s: string) : string list =
    s.Split(',') |> Array.toList |> List.map (fun m -> m.Trim()) |> List.filter (fun m -> m <> "")

/// Expands a GRANT/REVOKE privilege list for a target level: `ALL` becomes
/// every static privilege that exists at that level, `USAGE` becomes
/// nothing, and a privilege that doesn't exist at the level is a MySQL
/// 1221/1144.
let private expandPrivs (privs: string list) (target: PrivTarget) : Result<PrivDef list, int * string> =
    let atLevel (d: PrivDef) =
        match target with
        | Global -> true
        | OnDb _ -> d.DbCol.IsSome
        | OnTable _ -> d.TablePriv.IsSome

    if privs |> List.exists (fun p -> p = "ALL") then
        Ok(staticPrivileges |> List.filter atLevel)
    else
        privs
        |> List.filter (fun p -> p <> "USAGE")
        |> traverse (fun p ->
            match privBySql p with
            | Some d when atLevel d -> Result.Ok d
            | Some _ ->
                match target with
                | OnTable _ -> Result.Error(1144, "Illegal GRANT/REVOKE command; please consult the manual to see which privileges can be used")
                | _ -> Result.Error(1221, "Incorrect usage of DB GRANT and GLOBAL PRIVILEGES")
            | None -> Result.Error(1149, sprintf "Unknown privilege '%s'" p))

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
            match idx "Db", idx "Table_name", idx "Table_priv" with
            | Some d, Some t, Some tp ->
                let matchesRow (r: Value[]) =
                    rowAccount cols r |> Option.exists (sameAccount (account name host))
                    && (match r.[d] with VString s -> eqI s db | _ -> false)
                    && (match r.[t] with VString s -> eqI s table | _ -> false)

                let existing = rows |> List.tryFind matchesRow

                let currentSet =
                    existing
                    |> Option.map (fun r -> match r.[tp] with VString s -> setMembers s | _ -> [])
                    |> Option.defaultValue []

                let newSet =
                    if granting then
                        currentSet @ (wanted |> List.filter (fun w -> not (currentSet |> List.exists (eqI w))))
                    else
                        currentSet |> List.filter (fun c -> not (wanted |> List.exists (eqI c)))

                match existing with
                | Some _ when newSet.IsEmpty && not granting ->
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

/// `GRANT privs ON target TO users [WITH GRANT OPTION]`. MySQL 8 no longer
/// auto-creates unknown grantees — that's 1410.
let grant
    (store: Store)
    (privs: string list)
    (target: PrivTarget)
    (users: (string * string) list)
    (withGrantOption: bool)
    : Result<unit, int * string> =
    expandPrivs privs target
    |> Result.bind (fun defs ->
        users
        |> traverse (fun (name, host) ->
            if (tryUserRowForAccount store (account name host)).IsNone then
                Result.Error(1410, "You are not allowed to create a user with GRANT")
            else
                applyAtLevel store name host defs target withGrantOption true)
        |> Result.map ignore)

/// `REVOKE privs ON target FROM users`.
let revoke (store: Store) (privs: string list) (target: PrivTarget) (users: (string * string) list) : Result<unit, int * string> =
    let revokesGrantOption = privs |> List.exists (fun p -> p = "GRANT OPTION" || p = "ALL")

    expandPrivs (privs |> List.filter (fun p -> p <> "GRANT OPTION")) target
    |> Result.bind (fun defs ->
        users
        |> traverse (fun (name, host) ->
            if (tryUserRowForAccount store (account name host)).IsNone then
                Result.Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" name host)
            else
                applyAtLevel store name host defs target revokesGrantOption false)
        |> Result.map ignore)

// ---------------------------------------------------------------------------
// Enforcement: the privileges a parsed statement needs, and whether a user
// has them. ponytail: per-check linear scans of the tiny grant tables via
// the lock-free catalog snapshot — cache the lookups if profiling ever says
// a real workload notices.
// ---------------------------------------------------------------------------

/// Every real table a statement's expressions and sources read, walked
/// recursively — a derived table (`FROM (SELECT ... secret)`), a scalar or
/// `IN`/`EXISTS` subquery in any clause (WHERE, projections, SET, VALUES),
/// and unions nested in either all reach their tables here, so a privilege
/// check can't be dodged by burying the reference below the top level.
/// Mirrors `Executor.collectSubqueries`' traversal; kept local since
/// `Auth` compiles before `Executor`.
let rec private exprReadTablesIn (boundCtes: Set<string>) (defaultDb: string) (expr: Expr) : (string * string) list =
    let recur = exprReadTablesIn boundCtes defaultDb

    match expr with
    | Subquery s
    | Exists s -> selectReadTablesIn boundCtes defaultDb s
    | InSubquery(e, s) -> recur e @ selectReadTablesIn boundCtes defaultDb s
    | QuantifiedComparison(e, _, _, s) -> recur e @ selectReadTablesIn boundCtes defaultDb s
    | BinOp(_, a, b) -> recur a @ recur b
    | Row values -> values |> List.collect recur
    | AssignUserVariable(_, value) -> recur value
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _)
    | Cast(e, _)
    | Collate(e, _) -> recur e
    | Like(e, p, _, _) -> recur e @ recur p
    | Regexp(e, p) -> recur e @ recur p
    | In(e, xs) -> recur e @ (xs |> List.collect recur)
    | Between(e, lo, hi) -> recur e @ recur lo @ recur hi
    | FuncCall(_, args) -> args |> List.collect recur
    | MatchAgainst(_, q, _) -> recur q
    | WindowOver(fn, over) ->
        let fnExprs =
            match fn with
            | WinNTile buckets -> [ buckets ]
            | WinLagLead(_, expr, offset, deflt) -> expr :: (offset |> Option.toList) @ (deflt |> Option.toList)
            | WinFirstValue expr
            | WinLastValue expr -> [ expr ]
            | WinNthValue(expr, n) -> [ expr; n ]
            | WinAggregate(_, args) -> args
            | WinRowNumber
            | WinRank _
            | WinPercentRank
            | WinCumeDist -> []

        let frameExprs frame =
            let boundExpr = function
                | BoundPreceding expr
                | BoundFollowing expr -> [ expr ]
                | _ -> []

            boundExpr frame.Start @ boundExpr frame.End

        let overExprs =
            match over with
            | OverName _ -> []
            | OverSpec spec ->
                spec.PartitionBy
                @ (spec.OrderBy |> List.map fst)
                @ (spec.Frame |> Option.map frameExprs |> Option.defaultValue [])

        (fnExprs @ overExprs) |> List.collect recur
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map recur |> Option.defaultValue [])
        @ (whens |> List.collect (fun (c, r) -> recur c @ recur r))
        @ (elseBranch |> Option.map recur |> Option.defaultValue [])
    | Placeholder _
    | UserVariable _
    | SystemVariable _
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _ -> []

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
           spec.PartitionBy
           @ (spec.OrderBy |> List.map fst)
           @ (spec.Frame
              |> Option.map (fun frame ->
                  [ frame.Start; frame.End ]
                  |> List.collect (function
                      | BoundPreceding expr
                      | BoundFollowing expr -> [ expr ]
                      | _ -> []))
              |> Option.defaultValue []))
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

        let updatedTables =
            (u.From.Database |> Option.defaultValue defaultDb, u.From.Table)
            :: (u.Joins
                |> List.choose (fun join ->
                    match join.Table with
                    | FromTable table when table.Database.IsNone && Set.contains (table.Table.ToLowerInvariant()) boundCtes -> None
                    | FromTable table -> Some(table.Database |> Option.defaultValue defaultDb, table.Table)
                    | _ -> None))

        onTables "UPDATE" updatedTables
        @ onTables "SELECT" ((cteTables @ readInExprs) |> List.distinct)
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
    | CreateTable(name, _, _, _, _, _, _, _, _, _) -> onTables "CREATE" [ split name ]
    | CreateTableLike(name, source, _) -> onTables "CREATE" [ split name ] @ onTables "SELECT" [ split source ]
    | CreateTableAs(name, query, _) -> onTables "CREATE" [ split name ] @ requiredPrivileges defaultDb query
    | DropTable(names, _) -> onTables "DROP" (names |> List.map split)
    | Truncate table -> onTables "DROP" [ split table ]
    | AlterTable(table, _) -> onTables "ALTER" [ split table ]
    | RenameTable pairs -> onTables "ALTER" (pairs |> List.map (fst >> split))
    | CreateIndex(_, table, _, _, _) -> onTables "INDEX" [ split table ]
    | DropIndexStmt(_, table, _) -> onTables "INDEX" [ split table ]
    | CreateDatabase(name, _) -> [ "CREATE", OnDb name ]
    | DropDatabase(name, _) -> [ "DROP", OnDb name ]
    | AlterDatabase name -> [ "ALTER", OnDb name ]
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

        // `GRANT OPTION` is required unconditionally below and isn't a
        // static privilege `expandPrivs` knows — filter it out first (same
        // as `revoke`'s own expansion) so a statement like
        // `REVOKE GRANT OPTION, SELECT ...` still collects the SELECT
        // requirement instead of `expandPrivs` erroring and dropping every
        // privilege-specific check, which would let a scoped grant-option
        // holder revoke privileges it doesn't hold.
        let privReqs =
            match expandPrivs (privs |> List.filter (fun p -> p <> "GRANT OPTION")) target with
            | Result.Ok defs -> defs |> List.map (fun d -> d.Sql, target)
            | Result.Error _ -> [] // invalid list — the executor reports it

        ("GRANT OPTION", target) :: privReqs
    // CREATE TRIGGER carries its subject table in the statement. DROP's
    // subject is resolved by `requiredPrivilegesInStore` below.
    | CreateTrigger(_, _, _, table, _, _) -> onTables "TRIGGER" [ split table ]
    | SetTriggerNew _ -> []
    | DropTrigger _ -> []
    | CreateView(name, _, definition, orReplace) ->
        let viewDb, _ = split name
        let own = onTables "CREATE VIEW" [ split name ] @ (if orReplace then onTables "DROP" [ split name ] else [])

        match Parser.parse definition with
        | Ok select -> own @ requiredPrivileges viewDb select
        | Error _ -> own
    | DropView(names, _) -> onTables "DROP" (names |> List.map split)
    | ChecksumTables(tables, _) -> onTables "SELECT" (tables |> List.map split)
    | Explain(_, inner) -> requiredPrivileges defaultDb inner

/// Adds privilege requirements whose target can only be resolved from the
/// live catalog rather than from the statement shape alone.
let requiredPrivilegesInStore (store: Store) (defaultDb: string) (stmt: Statement) : (string * PrivTarget) list =
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
            if privSql = "GRANT OPTION" then
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
                        | OnTable(db, table) -> hasDbGrantOption db || hasTableGrantOption db table)

                if allowed then
                    Ok()
                else
                    match target with
                    | Global -> Error(1045, sprintf "Access denied for user '%s'@'%s' (using password: YES)" user wanted.Host)
                    | OnDb db -> Error(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" user wanted.Host db)
                    | OnTable(_, table) ->
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
                        | OnTable(db, _) when eqI db "information_schema" && eqI privSql "SELECT" -> true
                        | OnDb db -> hasDb def db
                        | OnTable(db, table) -> hasDb def db || hasTable def db table)

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

        required |> traverse checkOne |> Result.map ignore

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
        let account = sprintf "`%s`@`%s`" (name.Replace("`", "``")) (host.Replace("`", "``"))

        Ok(
            sprintf "CREATE USER for %s@%s" name host,
            sprintf
                "CREATE USER %s IDENTIFIED WITH '%s' AS '%s' REQUIRE NONE PASSWORD EXPIRE DEFAULT ACCOUNT %s PASSWORD HISTORY DEFAULT PASSWORD REUSE INTERVAL DEFAULT PASSWORD REQUIRE CURRENT DEFAULT"
                account
                plugin
                hash
                accountState
        )

let renderCreateUser (store: Store) (name: string) = renderCreateUserForAccount store (account name "%")

/// Whether `user` holds a global privilege — the gate for PROCESS-scoped
/// visibility (PROCESSLIST, KILL) and mysql-schema reads. Reuses `check`'s
/// hierarchy, so root's all-Y row and any GLOBAL grant satisfy it.
let hasGlobalPrivForAccount (store: Store) (wanted: Account) (privSql: string) : bool =
    match checkForAccount store wanted [ privSql, Global ] with
    | Result.Ok() -> true
    | Result.Error _ -> false

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
            match resolveColumn cols "Db", resolveColumn cols "Table_priv" with
            | Ok dbIdx, Ok privIdx ->
                rows
                |> List.exists (fun row ->
                    rowAccount cols row |> Option.exists (sameAccount wanted)
                    && (match row.[dbIdx] with | VString value -> eqI value db | _ -> false)
                    && row.[privIdx] <> VString "")
            | _ -> false

let canSeeDatabase (store: Store) (user: string) (db: string) = canSeeDatabaseForAccount store (account user "%") db

/// Whether any privilege at table scope or above makes a table visible in
/// metadata views.
let canSeeTableForAccount (store: Store) (wanted: Account) (db: string) (table: string) : bool =
    staticPrivileges |> List.exists (fun def -> checkForAccount store wanted [ def.Sql, OnTable(db, table) ] |> Result.isOk)

let canSeeTable (store: Store) (user: string) (db: string) (table: string) = canSeeTableForAccount store (account user "%") db table

/// A privilege list rendered MySQL-style: every static privilege → `ALL
/// PRIVILEGES`, none → `USAGE`, otherwise the names in column order.
let private renderPrivList (granted: PrivDef list) (all: PrivDef list) : string =
    if List.length granted = List.length all then "ALL PRIVILEGES"
    elif granted.IsEmpty then "USAGE"
    else granted |> List.map (fun d -> d.Sql) |> String.concat ", "

/// The `SHOW GRANTS FOR 'name'@'host'` rows: the global line from the
/// mysql.user row, one line per mysql.db row, one per tables_priv row —
/// 1141 when the account doesn't exist. ponytail: no dynamic-privilege or
/// PROXY lines (real root shows both; nothing here models either).
let renderGrantsForAccount (store: Store) (wanted: Account) : Result<string * string list, int * string> =
    match tryUserRowForAccount store wanted with
    | None -> Error(1141, sprintf "There is no such grant defined for user '%s' on host '%s'" wanted.Name wanted.Host)
    | Some(cols, row) ->
        let name = wanted.Name
        let host = userColumnText cols row "Host"
        let quoted = sprintf "`%s`@`%s`" name host

        let withOption (grantCol: string) (getCols: ColumnDef list) (r: Value[]) =
            if userColumnText getCols r grantCol = "Y" then " WITH GRANT OPTION" else ""

        let globalGranted =
            staticPrivileges |> List.filter (fun d -> userColumnText cols row d.UserCol = "Y")

        let globalLine =
            sprintf
                "GRANT %s ON *.* TO %s%s"
                (renderPrivList globalGranted staticPrivileges)
                quoted
                (withOption "Grant_priv" cols row)

        let dbLines =
            match scanList store "mysql" "db" with
            | Result.Error _ -> []
            | Result.Ok(dbCols, rows) ->
                let dbLevel = staticPrivileges |> List.filter (fun d -> d.DbCol.IsSome)

                rows
                |> List.filter (fun r -> rowAccount dbCols r |> Option.exists (sameAccount wanted))
                |> List.map (fun r ->
                    let granted = dbLevel |> List.filter (fun d -> userColumnText dbCols r d.DbCol.Value = "Y")

                    sprintf
                        "GRANT %s ON `%s`.* TO %s%s"
                        (renderPrivList granted dbLevel)
                        (userColumnText dbCols r "Db")
                        quoted
                        (withOption "Grant_priv" dbCols r))

        let tableLines =
            match scanList store "mysql" "tables_priv" with
            | Result.Error _ -> []
            | Result.Ok(tCols, rows) ->
                rows
                |> List.filter (fun r -> rowAccount tCols r |> Option.exists (sameAccount wanted))
                |> List.map (fun r ->
                    let members = setMembers (userColumnText tCols r "Table_priv")
                    let hasOption = members |> List.exists (eqI "Grant")

                    let granted =
                        staticPrivileges
                        |> List.filter (fun d ->
                            match d.TablePriv with
                            | Some tp -> members |> List.exists (eqI tp)
                            | None -> false)

                    let privText =
                        if granted.IsEmpty then
                            "USAGE"
                        else
                            granted |> List.map (fun d -> d.Sql) |> String.concat ", "

                    sprintf
                        "GRANT %s ON `%s`.`%s` TO %s%s"
                        privText
                        (userColumnText tCols r "Db")
                        (userColumnText tCols r "Table_name")
                        quoted
                        (if hasOption then " WITH GRANT OPTION" else ""))

        Ok(sprintf "Grants for %s@%s" name host, globalLine :: dbLines @ tableLines)

let renderGrants (store: Store) (name: string) = renderGrantsForAccount store (account name "%")
