module Fsdb.Engine.SystemCatalog

open System
open Fsdb.Value

let private textAt index (row: Value[]) =
    row |> Array.tryItem index |> Option.bind toText |> Option.defaultValue ""

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

module Trigger =
    type Entry =
        { Name: string
          Schema: string
          Table: string
          Timing: string
          Event: string
          Body: string
          Created: DateTime option
          Definer: string
          Order: int64 }

    let actionOrder row = int64At 1L 8 row

    let tryRead (row: Value[]) : Entry option =
        if row.Length < 6 then
            None
        else
            Some
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Table = textAt 2 row
                  Timing = textAt 3 row
                  Event = textAt 4 row
                  Body = textAt 5 row
                  Created = dateTimeAt 6 row
                  Definer = textAt 7 row
                  Order = actionOrder row }

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
          SecurityType: string }

    let tryRead (row: Value[]) : Entry option =
        if row.Length < 5 then
            None
        else
            Some
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Definition = textAt 2 row
                  ColumnNames = textAt 3 row
                  Created = dateTimeAt 4 row
                  Definer = textAt 5 row
                  CheckOption = row |> Array.tryItem 6 |> Option.bind toText |> Option.defaultValue "NONE"
                  SecurityType = row |> Array.tryItem 7 |> Option.bind toText |> Option.defaultValue "DEFINER" }

module Routine =
    type Entry =
        { Schema: string
          Name: string
          Definition: string
          Created: DateTime option
          Definer: string }

    let tryRead (row: Value[]) : Entry option =
        if row.Length < 5 then
            None
        else
            Some
                { Schema = textAt 0 row
                  Name = textAt 1 row
                  Definition = textAt 2 row
                  Created = dateTimeAt 3 row
                  Definer = textAt 4 row }

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
          Status: string }

    let tryRead (row: Value[]) : Entry option =
        if row.Length < 7 then
            None
        else
            Some
                { Schema = textAt 0 row
                  Name = textAt 1 row
                  Schedule = textAt 2 row
                  Definition = textAt 3 row
                  Created = dateTimeAt 4 row
                  Definer = textAt 5 row
                  Status = textAt 6 row }

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
        if row.Length < 5 then
            None
        else
            Some
                { Name = textAt 0 row
                  Schema = textAt 1 row
                  Table = textAt 2 row
                  Clause = textAt 3 row
                  Enforced = String.Equals(textAt 4 row, "YES", StringComparison.OrdinalIgnoreCase)
                  Column = row |> Array.tryItem 5 |> Option.bind toText
                  GeneratedName = String.Equals(textAt 6 row, "YES", StringComparison.OrdinalIgnoreCase)
                  Ordinal = int (int64At 1L 7 row) }

    let withName name row = withValue 0 (VString name) row
    let withTable table row = withValue 2 (VString table) row
    let withEnforced enforced row = withValue 4 (VString(if enforced then "YES" else "NO")) row
