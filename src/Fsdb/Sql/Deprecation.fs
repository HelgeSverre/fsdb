module Fsdb.Deprecation

open Fsdb.Ast
open Fsdb.Sql
open Fsdb.Value

let private countFunctionCalls name statement =
    Expression.statementCount
        (function
        | FuncCall(called, _) -> called.Equals(name, System.StringComparison.OrdinalIgnoreCase)
        | _ -> false)
        statement

let private repeatWarning count code message =
    for _ in 1..count do
        Diagnostics.warning code message

let private reportCharsetConversions statement =
    statement
    |> Expression.iterStatement (function
        | FuncCall(name, [ _; Lit(VString charset) ])
            when name.Equals("CONVERT", System.StringComparison.OrdinalIgnoreCase) ->
            match charset.ToLowerInvariant() with
            | "utf8" -> Diagnostics.deprecatedUtf8Alias ()
            | "utf8mb3" -> Diagnostics.deprecatedUtf8mb3 ()
            | _ -> ()
        | _ -> ())

let reportQuery statement =
    let reportCalculateFoundRows (select: SelectStmt) =
        if select.CalculateFoundRows then
            Diagnostics.warning
                1287
                "SQL_CALC_FOUND_ROWS is deprecated and will be removed in a future release. Consider using two separate queries instead."

    match statement with
    | Select select -> reportCalculateFoundRows select
    | Union(first, rest, _, _, _) ->
        reportCalculateFoundRows first
        rest |> List.iter (snd >> reportCalculateFoundRows)
    | _ -> ()

    repeatWarning
        (countFunctionCalls "FOUND_ROWS" statement)
        1287
        "FOUND_ROWS() is deprecated and will be removed in a future release. Consider using COUNT(*) instead."

    reportCharsetConversions statement

let reportNumericDisplays columns =
    for column in columns do
        match column.NumericDisplay with
        | Some display ->
            if display.ZeroFill then
                Diagnostics.warning
                    1681
                    "The ZEROFILL attribute is deprecated and will be removed in a future release. Use the LPAD function to zero-pad numbers, or store the formatted numbers in a CHAR column."

            match column.Type, display.Width, display.Decimals with
            | (TTinyInt _ | TBool | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _), Some _, _ ->
                Diagnostics.warning 1681 "Integer display width is deprecated and will be removed in a future release."
            | (TFloat _ | TDouble _), Some _, Some _ ->
                Diagnostics.warning
                    1681
                    "Specifying number of digits for floating point data types is deprecated and will be removed in a future release."
            | _ -> ()
        | None -> ()

let reportStatement statement =
    let reportSyntax deprecations =
        deprecations
        |> List.iter (function
            | Utf8CharsetAlias -> Diagnostics.deprecatedUtf8Alias ()
            | Utf8mb3Charset -> Diagnostics.deprecatedUtf8mb3 ())

    let charsetDeprecation (charset: string) =
        match charset.ToLowerInvariant() with
        | "utf8" -> Some Utf8CharsetAlias
        | "utf8mb3" -> Some Utf8mb3Charset
        | _ -> None

    let columnDeprecations (column: ColumnDef) =
        column.Charset |> Option.bind charsetDeprecation |> Option.toList

    let alterTableDeprecations actions =
        actions
        |> List.collect (function
            | AddColumn(column, _)
            | ModifyColumn(column, _)
            | ChangeColumn(_, column, _) -> columnDeprecations column
            | ConvertCharset(charset, _) -> charsetDeprecation charset |> Option.toList
            | _ -> [])

    let reportValuesFunction assignments =
        Do(assignments |> List.map snd)
        |> countFunctionCalls "VALUES"
        |> fun count ->
            repeatWarning
                count
                1287
                "'VALUES function' is deprecated and will be removed in a future release. Please use an alias (INSERT INTO ... VALUES (...) AS alias) and replace VALUES(col) in the ON DUPLICATE KEY UPDATE clause with alias.col instead"

    match statement with
    | (Select _ | Union _) as statement -> reportQuery statement
    | CreateDatabase(_, _, deprecations)
    | AlterDatabase(_, deprecations) -> reportSyntax deprecations
    | CreateTable table ->
        reportSyntax table.Deprecations
        reportCharsetConversions (CreateTable table)
    | AlterTable(_, actions) as statement ->
        reportSyntax (alterTableDeprecations actions)
        reportCharsetConversions statement
    | (Insert(_, _, _, assignments, _)
      | InsertSelect(_, _, _, assignments, _)) as statement ->
        reportValuesFunction assignments
        reportCharsetConversions statement
    | statement -> reportCharsetConversions statement
