/// FParsec-based SQL parser: raw text in, `Ast.Statement` out.
///
/// Grammar is built from small named parsers composed with combinators.
/// Expression precedence is split across two layers rather than crammed
/// into one `OperatorPrecedenceParser`: arithmetic (`+ - * / %` and unary
/// `-`) uses an OPP since that's exactly the shape it's designed for, while
/// the boolean layer (`OR`, `AND`, `NOT`, comparisons, `LIKE`/`IN`/`BETWEEN`)
/// is hand-written, because those forms need extra keywords and sub-parses
/// (`BETWEEN lo AND hi`, `IN (list)`) that don't fit the OPP's
/// string-token-in, expression-out operator shape.
module Fsdb.Parser

open System
open System.Collections.Generic
open System.Globalization
open FParsec
open Fsdb.Ast
open Fsdb.Value

// ---------------------------------------------------------------------------
// Whitespace, comments, tokens
// ---------------------------------------------------------------------------

let private lineComment: Parser<unit, unit> = pstring "--" >>. skipManyTill anyChar (skipNewline <|> eof)

let private blockComment: Parser<unit, unit> = pstring "/*" >>. skipManyTill anyChar (pstring "*/" >>% ())

/// Whitespace and comments, skipped after every token so parsers never have
/// to think about trailing space.
let private ws: Parser<unit, unit> = skipMany (choice [ spaces1; lineComment; blockComment ])

/// A punctuation token (`(`, `,`, `=`, ...) followed by whitespace.
let private sym (s: string) : Parser<unit, unit> = pstring s >>. ws

/// A case-insensitive keyword that only matches on a word boundary, so
/// `keyword "IN"` doesn't fire on the `IN` prefix of `INSERT`. Wrapped in
/// `attempt` so it never leaves partial input consumed on failure, making it
/// safe to use directly inside `choice`/`<|>`.
let private isIdentStart c = Char.IsLetter c || c = '_'
let private isIdentChar c = Char.IsLetterOrDigit c || c = '_'

let private keyword (s: string) : Parser<unit, unit> =
    attempt (pstringCI s >>. nextCharSatisfiesNot isIdentChar) .>> ws <?> s

let private intTok: Parser<int, unit> = pint32 .>> ws

// ---------------------------------------------------------------------------
// Identifiers
// ---------------------------------------------------------------------------

/// Words that can't be used as a bare identifier because the grammar needs
/// them unambiguously as keywords. Backtick-quoted identifiers bypass this
/// entirely, same as real MySQL.
let private reservedWords =
    HashSet<string>(
        [ "select"
          "from"
          "where"
          "as"
          "order"
          "by"
          "asc"
          "desc"
          "limit"
          "offset"
          "insert"
          "into"
          "values"
          "update"
          "set"
          "delete"
          "create"
          "table"
          "drop"
          "truncate"
          "if"
          "exists"
          "not"
          "primary"
          "key"
          "default"
          "auto_increment"
          "unsigned"
          "null"
          "true"
          "false"
          "and"
          "or"
          "is"
          "like"
          "in"
          "between"
          "engine"
          "charset"
          "collate"
          "character"
          "current_timestamp" ],
        StringComparer.OrdinalIgnoreCase
    )

let private bareIdent: Parser<string, unit> =
    many1Satisfy2 isIdentStart isIdentChar
    >>= fun w ->
        if reservedWords.Contains w then
            fail (sprintf "'%s' is a reserved keyword" w)
        else
            preturn w

/// Backtick quoting, with `` `` `` as the escape for a literal backtick.
let private backtickChar: Parser<char, unit> = (pstring "``" >>% '`') <|> satisfy (fun c -> c <> '`')

let private backtickIdent: Parser<string, unit> = pchar '`' >>. manyChars backtickChar .>> pchar '`'

let private identifier: Parser<string, unit> =
    (backtickIdent <|> attempt bareIdent) .>> ws <?> "identifier"

// ---------------------------------------------------------------------------
// Literals
// ---------------------------------------------------------------------------

let private numberFormat = NumberLiteralOptions.AllowFraction ||| NumberLiteralOptions.AllowExponent

/// Plain integers become `VInt`, exponent notation becomes `VDouble`, and
/// everything else with a decimal point stays exact as `VDecimal` — an
/// integer or decimal literal outside its type's range falls back to
/// `VDouble` (as MySQL's own DECIMAL/BIGINT overflow handling does) instead
/// of throwing `int64`/`decimal`'s unguarded overflow exception, which would
/// otherwise escape the parser and drop the client's connection.
let private numberLit: Parser<Value, unit> =
    (numberLiteral numberFormat "number" .>> ws)
    |>> fun nl ->
        if nl.IsInteger then
            match Int64.TryParse(nl.String, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, i -> VInt i
            | false, _ -> VDouble(float nl.String)
        elif nl.HasExponent then
            VDouble(float nl.String)
        else
            match Decimal.TryParse(nl.String, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, d -> VDecimal d
            | false, _ -> VDouble(float nl.String)

/// A single quoted string char: `''` escapes to `'`, a backslash escapes
/// the next character (`\n`, `\t`, `\\`, `\'`, ... or itself for anything
/// without a special meaning), anything else is literal.
let private stringChar: Parser<char, unit> =
    (pstring "''" >>% '\'')
    <|> (pchar '\\'
         >>. anyChar
         |>> function
             | 'n' -> '\n'
             | 't' -> '\t'
             | 'r' -> '\r'
             | 'b' -> '\b'
             | '0' -> '\000'
             | 'Z' -> '\x1A'
             | other -> other)
    <|> satisfy (fun c -> c <> '\'')

let private stringLit: Parser<Value, unit> =
    (pchar '\'' >>. manyChars stringChar .>> pchar '\'' .>> ws) |>> VString

let private literalValue: Parser<Value, unit> =
    choice
        [ numberLit
          stringLit
          keyword "NULL" >>% VNull
          keyword "TRUE" >>% VInt 1L
          keyword "FALSE" >>% VInt 0L ]

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

// `parenExpr`, function-call arguments and `IN (...)` lists all recurse back
// into the full expression grammar, which is itself built on top of them —
// tie the knot with a forward reference.
let private expr, exprRef = createParserForwardedToRef<Expr, unit> ()

let private parenExpr: Parser<Expr, unit> = between (sym "(") (sym ")") expr

let private starAtom: Parser<Expr, unit> = pstring "*" >>. ws >>% Star

/// A bare word: a column, a qualified `t.col` (or `t.*`, which is `Star` —
/// `Ast.Expr` doesn't distinguish it from an unqualified `*`), or a function
/// call if followed by `(args)`.
let private identAtom: Parser<Expr, unit> =
    identifier
    >>= fun name ->
        choice
            [ sym "(" >>. sepBy expr (sym ",") .>> sym ")" |>> fun args -> FuncCall(name, args)
              sym "." >>. (starAtom <|> (identifier |>> fun col -> QualifiedCol(name, col)))
              preturn (Col name) ]

let private atom: Parser<Expr, unit> =
    choice
        [ parenExpr
          starAtom
          numberLit |>> Lit
          stringLit |>> Lit
          keyword "NULL" >>% Lit VNull
          keyword "TRUE" >>% Lit(VInt 1L)
          keyword "FALSE" >>% Lit(VInt 0L)
          identAtom ]
    <?> "expression"

/// Arithmetic: `+ -` bind loosest, `* / %` tighter, unary `-` tightest.
/// `Ast.Op` has no modulo or unary-negation case, so both desugar: `%`
/// becomes a call to `MOD` (which is what MySQL's `%` already means) and
/// unary `-x` becomes `0 - x`.
let private opp = OperatorPrecedenceParser<Expr, unit, unit>()
let private arithExpr = opp.ExpressionParser
opp.TermParser <- atom
opp.AddOperator(InfixOperator("+", ws, 1, Associativity.Left, (fun a b -> BinOp(Add, a, b))))
opp.AddOperator(InfixOperator("-", ws, 1, Associativity.Left, (fun a b -> BinOp(Sub, a, b))))
opp.AddOperator(InfixOperator("*", ws, 2, Associativity.Left, (fun a b -> BinOp(Mul, a, b))))
opp.AddOperator(InfixOperator("/", ws, 2, Associativity.Left, (fun a b -> BinOp(Div, a, b))))
opp.AddOperator(InfixOperator("%", ws, 2, Associativity.Left, (fun a b -> FuncCall("MOD", [ a; b ]))))
opp.AddOperator(PrefixOperator("-", ws, 3, true, (fun e -> BinOp(Sub, Lit(VInt 0L), e))))

let private inList: Parser<Expr list, unit> = between (sym "(") (sym ")") (sepBy1 expr (sym ","))

let private betweenTail: Parser<Expr * Expr, unit> = (arithExpr .>> keyword "AND") .>>. arithExpr

/// Comparisons and the `IS NULL` / `LIKE` / `IN` / `BETWEEN` predicates,
/// all sitting at the same precedence just above arithmetic. The `NOT
/// LIKE`/`NOT IN`/`NOT BETWEEN` forms desugar to `Not (Like ...)` etc.
/// since `Ast.Expr` doesn't carry negated variants of its own.
let private compareOp: Parser<Op, unit> =
    choice
        [ pstring "<=" >>% Lte
          pstring ">=" >>% Gte
          pstring "<>" >>% Neq
          pstring "!=" >>% Neq
          pstring "=" >>% Eq
          pstring "<" >>% Lt
          pstring ">" >>% Gt ]
    .>> ws

let private comparisonExpr: Parser<Expr, unit> =
    arithExpr
    >>= fun left ->
        choice
            [ attempt (keyword "IS" >>. keyword "NOT" >>. keyword "NULL") >>% IsNotNull left
              attempt (keyword "IS" >>. keyword "NULL") >>% IsNull left
              attempt (keyword "NOT" >>. keyword "LIKE") >>. arithExpr |>> fun p -> Not(Like(left, p))
              keyword "LIKE" >>. arithExpr |>> fun p -> Like(left, p)
              attempt (keyword "NOT" >>. keyword "IN") >>. inList |>> fun xs -> Not(In(left, xs))
              keyword "IN" >>. inList |>> fun xs -> In(left, xs)
              attempt (keyword "NOT" >>. keyword "BETWEEN") >>. betweenTail
              |>> fun (lo, hi) -> Not(Between(left, lo, hi))
              keyword "BETWEEN" >>. betweenTail |>> fun (lo, hi) -> Between(left, lo, hi)
              compareOp .>>. arithExpr |>> fun (op, right) -> BinOp(op, left, right)
              preturn left ]

/// `NOT` sits between `AND` and comparisons: `NOT a = b` is `NOT (a = b)`,
/// but `a AND NOT b` negates only `b`. Forward-referenced like `expr` above,
/// since `let rec` on a parser *value* (rather than a function) would
/// evaluate the right-hand side eagerly and see itself undefined.
let private notExpr, notExprRef = createParserForwardedToRef<Expr, unit> ()
notExprRef.Value <- (keyword "NOT" >>. notExpr |>> Not) <|> comparisonExpr

let private andExpr: Parser<Expr, unit> =
    chainl1 notExpr (keyword "AND" >>% fun a b -> BinOp(And, a, b))

let private orExpr: Parser<Expr, unit> =
    chainl1 andExpr (keyword "OR" >>% fun a b -> BinOp(Or, a, b))

do exprRef.Value <- orExpr

// ---------------------------------------------------------------------------
// CREATE TABLE column definitions
// ---------------------------------------------------------------------------

/// A parenthesized width/precision like `(11)` or `(10,2)`, parsed and
/// discarded — `Ast.ColumnType` doesn't track display width.
let private ignoredWidth: Parser<unit, unit> =
    optional (between (sym "(") (sym ")") (sepBy1 intTok (sym ",")))

/// `UNSIGNED` is only representable on `TBigInt` in `Ast.ColumnType`; it's
/// still accepted (and discarded) after any integer type so `INT UNSIGNED`
/// parses, matching the "ignore-but-accept" treatment used for table options.
let private unsignedFlag: Parser<bool, unit> = opt (keyword "UNSIGNED") |>> Option.isSome

let private columnType: Parser<ColumnType, unit> =
    choice
        [ keyword "TINYINT" >>. ignoredWidth >>. unsignedFlag >>% TTinyInt
          keyword "BIGINT" >>. ignoredWidth >>. unsignedFlag |>> TBigInt
          (keyword "INT" <|> keyword "INTEGER") >>. ignoredWidth >>. unsignedFlag >>% TInt
          keyword "VARCHAR" >>. between (sym "(") (sym ")") intTok |>> TVarchar
          keyword "TEXT" >>% TText
          (keyword "DECIMAL" <|> keyword "NUMERIC")
          >>. opt (between (sym "(") (sym ")") ((intTok .>> sym ",") .>>. intTok))
          |>> function
              | Some(p, s) -> TDecimal(p, s)
              | None -> TDecimal(10, 0)
          (keyword "DOUBLE" <|> keyword "FLOAT") >>. ignoredWidth >>% TDouble
          keyword "DATETIME" >>. ignoredWidth >>% TDateTime
          keyword "TIMESTAMP" >>. ignoredWidth >>% TTimestamp
          keyword "DATE" >>% TDate
          keyword "JSON" >>% TJson
          (keyword "BOOLEAN" <|> keyword "BOOL") >>% TBool ]
    <?> "column type"

type private ColMod =
    | MNotNull
    | MNull
    | MDefault of ColumnDefault
    | MAutoIncrement
    | MPrimaryKey

let private defaultValueLit: Parser<ColumnDefault, unit> =
    (keyword "CURRENT_TIMESTAMP" >>% DCurrentTimestamp) <|> (literalValue |>> DConst)

let private colMod: Parser<ColMod, unit> =
    choice
        [ attempt (keyword "NOT" >>. keyword "NULL") >>% MNotNull
          keyword "NULL" >>% MNull
          keyword "DEFAULT" >>. defaultValueLit |>> MDefault
          keyword "AUTO_INCREMENT" >>% MAutoIncrement
          attempt (keyword "PRIMARY" >>. keyword "KEY") >>% MPrimaryKey ]

let private columnDef: Parser<ColumnDef, unit> =
    (identifier .>>. columnType .>>. many colMod)
    |>> fun ((name, ty), mods) ->
        { Name = name
          Type = ty
          Nullable = not (List.contains MNotNull mods)
          Default = mods |> List.tryPick (function MDefault v -> Some v | _ -> None)
          AutoIncrement = List.contains MAutoIncrement mods
          PrimaryKey = List.contains MPrimaryKey mods }

// ---------------------------------------------------------------------------
// Statements
// ---------------------------------------------------------------------------

/// A trailing `PRIMARY KEY (col, ...)` table constraint. `Ast.CreateTable`
/// has no separate slot for it, so it's applied as a post-pass that flags
/// the matching columns' `PrimaryKey` field instead.
let private trailingPrimaryKey: Parser<string list, unit> =
    attempt (keyword "PRIMARY" >>. keyword "KEY") >>. between (sym "(") (sym ")") (sepBy1 identifier (sym ","))

let private createTableItem: Parser<Choice<ColumnDef, string list>, unit> =
    (trailingPrimaryKey |>> Choice2Of2) <|> (columnDef |>> Choice1Of2)

/// `ENGINE=`, `CHARSET=`/`DEFAULT CHARSET=`, `COLLATE=` table options:
/// accepted and discarded, same treatment as column display widths.
let private tableOption: Parser<unit, unit> =
    choice
        [ keyword "ENGINE" >>. opt (sym "=") >>. identifier >>% ()
          keyword "DEFAULT" >>. (keyword "CHARSET" <|> (keyword "CHARACTER" >>. keyword "SET"))
          >>. opt (sym "=")
          >>. identifier
          >>% ()
          keyword "CHARSET" >>. opt (sym "=") >>. identifier >>% ()
          keyword "COLLATE" >>. opt (sym "=") >>. identifier >>% () ]

let private tableOptions: Parser<unit, unit> = skipMany tableOption

let private createTable: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. identifier
     .>>. between (sym "(") (sym ")") (sepBy1 createTableItem (sym ","))
     .>> tableOptions)
    |>> fun ((ifNotExists, name), items) ->
        let pkNames = items |> List.collect (function Choice2Of2 names -> names | _ -> [])

        let columns =
            items
            |> List.choose (function
                | Choice1Of2 c -> Some(if List.contains c.Name pkNames then { c with PrimaryKey = true } else c)
                | Choice2Of2 _ -> None)

        CreateTable(name, columns, ifNotExists)

let private dropTable: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 identifier (sym ","))
    |>> fun (ifExists, names) -> DropTable(names, ifExists)

let private truncateTable: Parser<Statement, unit> =
    keyword "TRUNCATE" >>. opt (keyword "TABLE") >>. identifier |>> Truncate

let private insertStmt: Parser<Statement, unit> =
    (keyword "INSERT" >>. keyword "INTO" >>. identifier
     .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
     .>> keyword "VALUES"
     .>>. sepBy1 (between (sym "(") (sym ")") (sepBy1 expr (sym ","))) (sym ","))
    |>> fun ((table, cols), rows) -> Insert(table, cols |> Option.defaultValue [], rows)

let private projection: Parser<Projection, unit> = expr .>>. opt (keyword "AS" >>. identifier)

let private orderKey: Parser<OrderKey, unit> =
    (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc)))
    |>> fun (e, dir) -> (e, dir |> Option.defaultValue Asc)

/// `LIMIT n`, `LIMIT n OFFSET m`, and the MySQL-specific `LIMIT m, n` (which
/// means offset `m`, count `n` — the arguments are in the opposite order
/// from `LIMIT n OFFSET m`).
let private limitClause: Parser<int option * int option, unit> =
    keyword "LIMIT" >>. intTok
    >>= fun a ->
        (sym "," >>. intTok |>> fun b -> (Some b, Some a))
        <|> (keyword "OFFSET" >>. intTok |>> fun b -> (Some a, Some b))
        <|> preturn (Some a, None)

/// `[db.]table [[AS] alias]` — the alias form omits `AS` too (`FROM t x`),
/// same as MySQL; `identifier` already backtracks cleanly off a reserved
/// word (e.g. `WHERE`), so no `attempt` is needed around the bare-alias
/// alternative.
let private tableRef: Parser<TableRef, unit> =
    (identifier .>>. opt (sym "." >>. identifier))
    .>>. opt ((keyword "AS" >>. identifier) <|> identifier)
    |>> fun ((first, second), alias) ->
        match second with
        | Some table -> { Database = Some first; Table = table; Alias = alias }
        | None -> { Database = None; Table = first; Alias = alias }

let private selectStmt: Parser<Statement, unit> =
    (keyword "SELECT" >>. sepBy1 projection (sym ",")
     .>>. opt (keyword "FROM" >>. tableRef)
     .>>. opt (keyword "WHERE" >>. expr)
     .>>. opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ","))
     .>>. opt limitClause)
    |>> fun ((((projs, from), where), orderBy), limitOffset) ->
        let limit, offset = limitOffset |> Option.defaultValue (None, None)

        Select
            { Projections = projs
              From = from
              Where = where
              OrderBy = orderBy |> Option.defaultValue []
              Limit = limit
              Offset = offset }

let private updateStmt: Parser<Statement, unit> =
    (keyword "UPDATE" >>. identifier .>> keyword "SET"
     .>>. sepBy1 ((identifier .>> sym "=") .>>. expr) (sym ",")
     .>>. opt (keyword "WHERE" >>. expr))
    |>> fun ((table, assignments), where) -> Update(table, assignments, where)

let private deleteStmt: Parser<Statement, unit> =
    (keyword "DELETE" >>. keyword "FROM" >>. identifier .>>. opt (keyword "WHERE" >>. expr))
    |>> fun (table, where) -> Delete(table, where)

let private statement: Parser<Statement, unit> =
    choice [ createTable; dropTable; truncateTable; insertStmt; selectStmt; updateStmt; deleteStmt ]
    <?> "statement"

/// Parses one SQL statement, with an optional trailing `;`. Session-variable
/// forms like `SELECT @@version` are deliberately out of scope — those are
/// handled by `QueryHandler` before reaching this parser.
let parse (sql: string) : Result<Statement, string> =
    let full = ws >>. statement .>> opt (sym ";") .>> eof

    // `open FParsec` brings its own `Ok`/`Error` (from `Reply`'s status) into
    // scope, shadowing `Result`'s — qualify to get the ones this signature means.
    //
    // Belt-and-braces around `numberLit`'s overflow guard above: no parser
    // exception should ever be able to escape as a raw .NET exception and
    // drop the caller's connection — a syntax error is always a clean
    // `Result.Error`, however it originates.
    try
        match run full sql with
        | Success(stmt, _, _) -> Result.Ok stmt
        | Failure(msg, _, _) -> Result.Error msg
    with ex ->
        Result.Error ex.Message
