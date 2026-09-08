module internal Fsdb.Sql.SqlText

open Fsdb.Ast
open Fsdb.Value

let columnType =
    let quotedList values = values |> List.map (sprintf "'%s'") |> String.concat ","
    let unsigned enabled = if enabled then " unsigned" else ""

    function
    | TTinyInt value -> "tinyint" + unsigned value
    | TBool -> "tinyint(1)"
    | TSmallInt value -> "smallint" + unsigned value
    | TMediumInt value -> "mediumint" + unsigned value
    | TInt value -> "int" + unsigned value
    | TBigInt value -> "bigint" + unsigned value
    | TBit width -> sprintf "bit(%d)" width
    | TChar length -> sprintf "char(%d)" length
    | TVarchar length -> sprintf "varchar(%d)" length
    | TTinyText -> "tinytext"
    | TText -> "text"
    | TMediumText -> "mediumtext"
    | TLongText -> "longtext"
    | TBinary length -> sprintf "binary(%d)" length
    | TVarBinary length -> sprintf "varbinary(%d)" length
    | TTinyBlob -> "tinyblob"
    | TBlob -> "blob"
    | TMediumBlob -> "mediumblob"
    | TLongBlob -> "longblob"
    | TEnum values -> sprintf "enum(%s)" (quotedList values)
    | TSet values -> sprintf "set(%s)" (quotedList values)
    | TDecimal(precision, scale, isUnsigned) ->
        sprintf "decimal(%d,%d)%s" precision scale (unsigned isUnsigned)
    | TDouble value -> "double" + unsigned value
    | TFloat value -> "float" + unsigned value
    | TDate -> "date"
    | TDateTime precision -> if precision > 0 then sprintf "datetime(%d)" precision else "datetime"
    | TTimestamp precision -> if precision > 0 then sprintf "timestamp(%d)" precision else "timestamp"
    | TTime precision -> if precision > 0 then sprintf "time(%d)" precision else "time"
    | TYear -> "year(4)"
    | TJson -> "json"
    | TGeometry GeometryCollection -> "geomcollection"
    | TGeometry kind -> geometryTypeName kind |> _.ToLowerInvariant()
    | TVector dimensions -> sprintf "vector(%d)" dimensions

let private operatorText =
    function
    | And -> "and"
    | Or -> "or"
    | Xor -> "xor"
    | Eq -> "="
    | Neq -> "<>"
    | Lt -> "<"
    | Lte -> "<="
    | Gt -> ">"
    | Gte -> ">="
    | Add -> "+"
    | Sub
    | SignedSub -> "-"
    | Mul -> "*"
    | Div -> "/"
    | IntDiv -> "DIV"
    | NullSafeEq -> "<=>"

let private literal =
    function
    | VNull -> "NULL"
    | VString value -> "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'"
    | value -> value |> toText |> Option.defaultValue "NULL"

let rec expression =
    function
    | Lit value -> literal value
    | MatchAgainst(columns, query, _) ->
        let columnText column =
            column.Qualifier
            |> Option.map (fun qualifier -> sprintf "`%s`.`%s`" qualifier column.Name)
            |> Option.defaultWith (fun () -> sprintf "`%s`" column.Name)

        sprintf
            "match (%s) against (%s)"
            (columns |> List.map columnText |> String.concat ",")
            (expression query)
    | Placeholder _ -> "?"
    | UserVariable variable -> variable.Sql
    | SystemVariable(scope, name) ->
        "@@"
        + (scope |> Option.map (fun value -> value.ToLowerInvariant() + ".") |> Option.defaultValue "")
        + name
    | AssignUserVariable(variable, value) -> sprintf "%s := %s" variable.Sql (expression value)
    | Col name -> sprintf "`%s`" name
    | QualifiedCol(table, column) -> sprintf "`%s`.`%s`" table column
    | Row values -> sprintf "(%s)" (values |> List.map expression |> String.concat ",")
    | BinOp(operator, left, right) ->
        sprintf "(%s %s %s)" (expression left) (operatorText operator) (expression right)
    | Not value -> sprintf "(not(%s))" (expression value)
    | IsNull value -> sprintf "(%s is null)" (expression value)
    | IsNotNull value -> sprintf "(%s is not null)" (expression value)
    | IsTrue value -> sprintf "(%s is true)" (expression value)
    | IsFalse value -> sprintf "(%s is false)" (expression value)
    | Like(value, pattern, _, _) -> sprintf "(%s like %s)" (expression value) (expression pattern)
    | Regexp(value, pattern) -> sprintf "(%s regexp %s)" (expression value) (expression pattern)
    | In(value, candidates) ->
        sprintf "(%s in (%s))" (expression value) (candidates |> List.map expression |> String.concat ",")
    | Between(value, lower, upper) ->
        sprintf "(%s between %s and %s)" (expression value) (expression lower) (expression upper)
    | FuncCall(name, [ Cast(value, TChar length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as char(%d))" (expression value) length
    | FuncCall(name, [ Cast(value, TBinary length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as binary(%d))" (expression value) length
    | FuncCall(name, arguments) ->
        sprintf "%s(%s)" (name.ToLowerInvariant()) (arguments |> List.map expression |> String.concat ",")
    | Distinct value -> sprintf "distinct %s" (expression value)
    | OrderBy(value, _) -> expression value
    | Cast(value, TBigInt true) -> sprintf "cast(%s as unsigned)" (expression value)
    | Cast(value, TBigInt false) -> sprintf "cast(%s as signed)" (expression value)
    | Cast(value, target) -> sprintf "cast(%s as %s)" (expression value) (columnType target)
    | Collate(value, collation) -> sprintf "(%s collate %s)" (expression value) collation
    | Case(subject, branches, fallback) ->
        let subjectText = subject |> Option.map (expression >> sprintf " %s") |> Option.defaultValue ""

        let branchText =
            branches
            |> List.map (fun (condition, result) -> sprintf " when %s then %s" (expression condition) (expression result))
            |> String.concat ""

        let fallbackText = fallback |> Option.map (expression >> sprintf " else %s") |> Option.defaultValue ""
        sprintf "(case%s%s%s end)" subjectText branchText fallbackText
    | Star qualifier -> (qualifier |> Option.map (sprintf "`%s`.") |> Option.defaultValue "") + "*"
    | Exists _ -> "exists(...)"
    | Subquery _
    | InSubquery _
    | QuantifiedComparison _ -> "(...)"
    | WindowOver _ -> "window function() over ()"
