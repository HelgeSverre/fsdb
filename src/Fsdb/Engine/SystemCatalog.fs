module Fsdb.Engine.SystemCatalog

open System
open Fsdb.Value

let private textOr fallback index (row: Value[]) =
    row |> Array.tryItem index |> Option.bind toText |> Option.defaultValue fallback

let private textAt index row = textOr "" index row

let private dateTimeAt index (row: Value[]) =
    row
    |> Array.tryItem index
    |> Option.bind (function
        | VDateTime value -> Some value
        | _ -> None)

let private int64At fallback index (row: Value[]) =
    match Array.tryItem index row with
    | Some(VInt value) -> value
    | Some(VUInt value) when value <= uint64 Int64.MaxValue -> int64 value
    | _ -> fallback

let private withValue index value (row: Value[]) =
    let updated =
        if row.Length > index then
            Array.copy row
        else
            Array.append row (Array.create (index - row.Length + 1) VNull)

    updated.[index] <- value
    updated

let private sameIdentity schema name actualSchema actualName =
    String.Equals(actualSchema, schema, StringComparison.OrdinalIgnoreCase)
    && String.Equals(actualName, name, StringComparison.OrdinalIgnoreCase)

let private readCompleteRow requiredValues read (row: Value[]) =
    if row.Length < requiredValues then None else Some(read row)

module StoredExecutionContext =
    let legacySqlMode = "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION"
    let legacyCharacterSetClient = "utf8mb4"
    let legacyCollationConnection = "utf8mb4_0900_ai_ci"
    let legacyDatabaseCollation = "utf8mb4_0900_ai_ci"

module Trigger =
    let legacySqlMode = StoredExecutionContext.legacySqlMode
    let legacyCharacterSetClient = StoredExecutionContext.legacyCharacterSetClient
    let legacyCollationConnection = StoredExecutionContext.legacyCollationConnection
    let legacyDatabaseCollation = StoredExecutionContext.legacyDatabaseCollation

    type Entry =
        { Name: string
          Schema: string
          Table: string
          Timing: string
          Event: string
          Body: string
          Created: DateTime option
          Definer: string
          Order: int64
          SqlMode: string
          CharacterSetClient: string
          CollationConnection: string
          DatabaseCollation: string }

    let actionOrder row = int64At 1L 8 row

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 6
            (fun row ->
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Table = textAt 2 row
                  Timing = textAt 3 row
                  Event = textAt 4 row
                  Body = textAt 5 row
                  Created = dateTimeAt 6 row
                  Definer = textAt 7 row
                  Order = actionOrder row
                  SqlMode = textOr legacySqlMode 9 row
                  CharacterSetClient = textOr legacyCharacterSetClient 10 row
                  CollationConnection = textOr legacyCollationConnection 11 row
                  DatabaseCollation = textOr legacyDatabaseCollation 12 row })
            row

    let withTable table row = withValue 2 (VString table) row
    let withActionOrder order row = withValue 8 (VInt order) row

module View =
    type Entry =
        { Name: string
          Schema: string
          Definition: string
          ColumnNames: string
          Created: DateTime option
          Definer: string
          CheckOption: string
          SecurityType: string
          Algorithm: string }

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 5
            (fun row ->
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Definition = textAt 2 row
                  ColumnNames = textAt 3 row
                  Created = dateTimeAt 4 row
                  Definer = textAt 5 row
                  CheckOption = textOr "NONE" 6 row
                  SecurityType = textOr "DEFINER" 7 row
                  Algorithm = textOr "UNDEFINED" 8 row })
            row

module Routine =
    type Entry =
        { Schema: string
          Name: string
          Definition: string
          Created: DateTime option
          Definer: string
          Parameters: string
          SecurityType: string
          SqlMode: string
          CharacterSetClient: string
          CollationConnection: string
          DatabaseCollation: string }

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 5
            (fun row ->
                { Schema = textAt 0 row
                  Name = textAt 1 row
                  Definition = textAt 2 row
                  Created = dateTimeAt 3 row
                  Definer = textAt 4 row
                  Parameters = textAt 5 row
                  SecurityType = textOr "DEFINER" 6 row
                  SqlMode = textOr StoredExecutionContext.legacySqlMode 7 row
                  CharacterSetClient = textOr StoredExecutionContext.legacyCharacterSetClient 8 row
                  CollationConnection = textOr StoredExecutionContext.legacyCollationConnection 9 row
                  DatabaseCollation = textOr StoredExecutionContext.legacyDatabaseCollation 10 row })
            row

    let matches schema name (entry: Entry) =
        sameIdentity schema name entry.Schema entry.Name

    let rowMatches schema name row = tryRead row |> Option.exists (matches schema name)

module StoredFunction =
    type Entry =
        { Schema: string
          Name: string
          ReturnType: string
          Definition: string
          Created: DateTime option
          Definer: string
          Parameters: string
          SecurityType: string
          Deterministic: bool
          SqlDataAccess: string
          SqlMode: string
          CharacterSetClient: string
          CollationConnection: string
          DatabaseCollation: string }

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 6
            (fun row ->
                { Schema = textAt 0 row
                  Name = textAt 1 row
                  ReturnType = textAt 2 row
                  Definition = textAt 3 row
                  Created = dateTimeAt 4 row
                  Definer = textAt 5 row
                  Parameters = textAt 6 row
                  SecurityType = textOr "DEFINER" 7 row
                  Deterministic = String.Equals(textAt 8 row, "YES", StringComparison.OrdinalIgnoreCase)
                  SqlDataAccess = textOr "CONTAINS SQL" 9 row
                  SqlMode = textOr StoredExecutionContext.legacySqlMode 10 row
                  CharacterSetClient = textOr StoredExecutionContext.legacyCharacterSetClient 11 row
                  CollationConnection = textOr StoredExecutionContext.legacyCollationConnection 12 row
                  DatabaseCollation = textOr StoredExecutionContext.legacyDatabaseCollation 13 row })
            row

    let matches schema name (entry: Entry) =
        sameIdentity schema name entry.Schema entry.Name

    let rowMatches schema name row = tryRead row |> Option.exists (matches schema name)

module Event =
    type Entry =
        { Schema: string
          Name: string
          Schedule: string
          Definition: string
          Created: DateTime option
          Definer: string
          Status: Fsdb.Sql.Event.Status
          OnCompletion: string
          Comment: string
          LastAltered: DateTime option
          LastExecuted: DateTime option
          SqlMode: string
          TimeZone: string
          CharacterSetClient: string
          CollationConnection: string
          DatabaseCollation: string
          Originator: int64
          ExecuteAt: DateTime option
          IntervalValue: string option
          IntervalField: string option
          Starts: DateTime option
          Ends: DateTime option }

    let toRow (entry: Entry) : Value[] =
        let dateTimeValue = Option.map VDateTime >> Option.defaultValue VNull
        let textValue = Option.map VString >> Option.defaultValue VNull

        [| VString entry.Schema
           VString entry.Name
           VString entry.Schedule
           VString entry.Definition
           dateTimeValue entry.Created
           VString entry.Definer
           VString(Fsdb.Sql.Event.statusText entry.Status)
           VString entry.OnCompletion
           VString entry.Comment
           dateTimeValue entry.LastAltered
           dateTimeValue entry.LastExecuted
           VString entry.SqlMode
           VString entry.TimeZone
           VString entry.CharacterSetClient
           VString entry.CollationConnection
           VString entry.DatabaseCollation
           VUInt(uint64 entry.Originator)
           dateTimeValue entry.ExecuteAt
           textValue entry.IntervalValue
           textValue entry.IntervalField
           dateTimeValue entry.Starts
           dateTimeValue entry.Ends |]

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 7
            (fun row ->
                { Schema = textAt 0 row
                  Name = textAt 1 row
                  Schedule = textAt 2 row
                  Definition = textAt 3 row
                  Created = dateTimeAt 4 row
                  Definer = textAt 5 row
                  Status = textAt 6 row |> Fsdb.Sql.Event.statusOfText
                  OnCompletion = textOr "NOT PRESERVE" 7 row
                  Comment = textAt 8 row
                  LastAltered = dateTimeAt 9 row |> Option.orElseWith (fun () -> dateTimeAt 4 row)
                  LastExecuted = dateTimeAt 10 row
                  SqlMode = textOr StoredExecutionContext.legacySqlMode 11 row
                  TimeZone = textOr "SYSTEM" 12 row
                  CharacterSetClient = textOr StoredExecutionContext.legacyCharacterSetClient 13 row
                  CollationConnection = textOr StoredExecutionContext.legacyCollationConnection 14 row
                  DatabaseCollation = textOr StoredExecutionContext.legacyDatabaseCollation 15 row
                  Originator = int64At 1L 16 row
                  ExecuteAt = dateTimeAt 17 row
                  IntervalValue = Array.tryItem 18 row |> Option.bind toText
                  IntervalField = Array.tryItem 19 row |> Option.bind toText
                  Starts = dateTimeAt 20 row
                  Ends = dateTimeAt 21 row })
            row

    let mapRow transform row =
        row
        |> tryRead
        |> Option.map (transform >> toRow)
        |> Option.defaultWith (fun () -> Array.copy row)

    let timing (entry: Entry) =
        match entry.ExecuteAt, entry.IntervalValue, entry.IntervalField, entry.Starts with
        | Some executeAt, _, _, _ -> Some(Fsdb.Sql.Event.Timing.OneTime executeAt)
        | _, Some value, Some field, Some starts -> Fsdb.Sql.Event.tryRecurringTiming value field starts entry.Ends
        | _ -> None

    let matches schema name (entry: Entry) =
        sameIdentity schema name entry.Schema entry.Name

    let rowMatches schema name row = tryRead row |> Option.exists (matches schema name)

module Check =
    type Entry =
        { Name: string
          Schema: string
          Table: string
          Clause: string
          Enforced: bool
          Column: string option
          GeneratedName: bool
          Ordinal: int }

    let tryRead (row: Value[]) : Entry option =
        readCompleteRow 5
            (fun row ->
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Table = textAt 2 row
                  Clause = textAt 3 row
                  Enforced = String.Equals(textAt 4 row, "YES", StringComparison.OrdinalIgnoreCase)
                  Column = row |> Array.tryItem 5 |> Option.bind toText
                  GeneratedName = String.Equals(textAt 6 row, "YES", StringComparison.OrdinalIgnoreCase)
                  Ordinal = int (int64At 1L 7 row) })
            row

    let withName name row = withValue 0 (VString name) row
    let withTable table row = withValue 2 (VString table) row
    let withEnforced enforced row = withValue 4 (VString(if enforced then "YES" else "NO")) row
