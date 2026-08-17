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

/// The numeric form of `Protocol.ServerVersion` ("8.0.36-fsdb"), for
/// `stripVersionComments` below. Duplicated rather than shared because
/// `Protocol.fs` compiles after this file — keep the two in sync by hand if
/// `ServerVersion` ever changes.
let private serverVersionNumber = 80036

/// mysqldump wraps version-specific SQL in `/*!NNNNN ... */` (or a bare
/// `/*! ... */` for "any version") so one dump can target several server
/// versions at once: MySQL's grammar runs the wrapped SQL as ordinary
/// executable SQL when the server's version is >= NNNNN, and treats it as
/// an inert comment otherwise. `blockComment` above only ever does the
/// second half (skip it); this rewrites the SQL text ahead of parsing so
/// the first half (splice it back in) doesn't need its own grammar rule.
/// Not recursive — mysqldump never nests these.
let stripVersionComments (sql: string) : string =
    let sb = Text.StringBuilder(sql.Length)
    let mutable i = 0
    // `'`/`"`/`` ` `` while inside a string/identifier literal — a `/*!`
    // that appears there is data, not a version comment (see the copy loop
    // below for how it's tracked in and out of literals).
    let mutable quoteChar: char option = None

    while i < sql.Length do
        match quoteChar with
        | Some q when sql.[i] = '\\' && q <> '`' && i + 1 < sql.Length ->
            // backslash-escapes only apply inside '...'/"...", not `...`
            sb.Append(sql.[i]).Append(sql.[i + 1]) |> ignore
            i <- i + 2
        | Some q when sql.[i] = q && i + 1 < sql.Length && sql.[i + 1] = q ->
            // a doubled quote char is an escaped literal quote, not the close
            sb.Append(sql.[i]).Append(sql.[i + 1]) |> ignore
            i <- i + 2
        | Some q when sql.[i] = q ->
            quoteChar <- None
            sb.Append(sql.[i]) |> ignore
            i <- i + 1
        | Some _ ->
            sb.Append(sql.[i]) |> ignore
            i <- i + 1
        | None when sql.[i] = '\'' || sql.[i] = '"' || sql.[i] = '`' ->
            quoteChar <- Some sql.[i]
            sb.Append(sql.[i]) |> ignore
            i <- i + 1
        | None when i + 2 < sql.Length && sql.[i] = '/' && sql.[i + 1] = '*' && sql.[i + 2] = '!' ->
            let closeAt =
                match sql.IndexOf("*/", i + 3) with
                | -1 -> sql.Length
                | idx -> idx

            let inner = sql.Substring(i + 3, closeAt - (i + 3))
            // MySQL reads the *first 5 digits* of the leading digit run as
            // the version, not "exactly 5 digits or none" -- a run longer
            // than 5 (mysqldump's own `/*!999999\- enable the sandbox mode
            // */` preamble) still gates on its first 5, with the rest
            // spliced back into the body; a run shorter than 5 still counts
            // as a version (`/*!9999 body */` runs `body`). Only an empty
            // run means "any version".
            let digits = String(inner |> Seq.truncate 5 |> Seq.takeWhile Char.IsDigit |> Seq.toArray)

            let version, body =
                if digits.Length > 0 then int digits, inner.Substring(digits.Length)
                else 0, inner

            if version <= serverVersionNumber then
                sb.Append(body: string) |> ignore

            i <- (if closeAt = sql.Length then closeAt else closeAt + 2)
        | None ->
            sb.Append(sql.[i]) |> ignore
            i <- i + 1

    sb.ToString()

/// True when `sql` is nothing but whitespace/comments — real MySQL treats
/// that as a harmless no-op (`Query OK, 0 rows affected`), not a syntax
/// error, which matters once a version-gated comment above strips down to
/// nothing on its own line (a routine shape in mysqldump preambles).
let isBlank (sql: string) : bool =
    match run (ws .>> eof) sql with
    | Success _ -> true
    | Failure _ -> false

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
/// entirely, same as real MySQL. Deliberately *not* real MySQL's full
/// reserved-word list: only words this grammar's `expr`/statement dispatch
/// would otherwise misparse land here — `ENGINE`/`CHARSET`/`COLLATE`/
/// `CHARACTER`/`AUTO_INCREMENT` are deliberately excluded: they're matched
/// via `keyword` (literal text, independent of this set) only inside
/// `CREATE TABLE`'s own table-options/column-modifier grammar, never as a
/// general expression atom, since reserving them would break
/// `information_schema.tables.engine`/`.auto_increment` (and any other
/// query naming an ordinary column `engine`/`charset`/`collate`/
/// `auto_increment` — Doctrine DBAL's own schema introspection, behind
/// Laravel's `Blueprint::change()`, is one such query) from parsing as a
/// plain column reference. Real MySQL agrees: `AUTO_INCREMENT` is a
/// non-reserved keyword there too.
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
          "current_timestamp"
          "alter"
          "rename"
          "index"
          "constraint"
          "foreign"
          "references"
          "cast"
          "join"
          "inner"
          "left"
          "right"
          "cross"
          "outer"
          "on"
          "using"
          "natural"
          "group"
          "having"
          "union"
          "all"
          "when"
          "for"
          "lock" ],
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

/// `[db.]table` — like `tableRef` below but with no alias, for statements
/// that target exactly one table rather than projecting columns (DDL,
/// INSERT/UPDATE/DELETE/TRUNCATE). Encoded as a single "db.table" string
/// rather than widening every `Ast.Statement` table field to a record —
/// `Storage.splitQualified` peels it back apart right before resolving
/// against `Storage`, which already takes database and table name as two
/// separate arguments everywhere.
let private qualifiedTableName: Parser<string, unit> =
    (identifier .>>. opt (sym "." >>. identifier))
    |>> function
        | first, Some second -> first + "." + second
        | first, None -> first

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

/// One character of a `quote`-delimited string literal, as a (possibly
/// two-character) string rather than one `char`: a doubled quote (`''` or
/// `""`) escapes to one literal quote, a backslash escapes the next
/// character (`\n`, `\t`, `\\`, `\'`, ... or itself for anything without a
/// special meaning) — except `\%` and `\_`, which MySQL deliberately leaves
/// as the two literal characters `\%`/`\_` rather than collapsing them, so
/// `LIKE` (via `Executor.likeToRegex`) can still tell "match the wildcard
/// literally" apart from "the wildcard". Anything else is literal.
let private quotedStringChar (quote: char) : Parser<string, unit> =
    (pstring (string quote + string quote) >>% string quote)
    <|> (pchar '\\'
         >>. anyChar
         |>> function
             | 'n' -> "\n"
             | 't' -> "\t"
             | 'r' -> "\r"
             | 'b' -> "\b"
             | '0' -> "\000"
             | 'Z' -> "\x1A"
             | '%' -> "\\%"
             | '_' -> "\\_"
             | other -> string other)
    <|> (satisfy (fun c -> c <> quote) |>> string)

/// Single- and double-quoted string literals, with identical escaping
/// rules. Default `sql_mode` (no `ANSI_QUOTES`) treats `"..."` as a string
/// literal exactly like `'...'` — only backtick-quoting is identifier
/// quoting — so raw queries/seeders written with double quotes parse
/// without needing `ANSI_QUOTES` support.
let private quoted (quote: char) : Parser<Value, unit> =
    (pchar quote >>. manyStrings (quotedStringChar quote) .>> pchar quote .>> ws) |>> VString

let private stringLit: Parser<Value, unit> = quoted '\'' <|> quoted '"'

/// MySQL's quoted hexadecimal binary literal (`X'00ff'`, case-insensitive
/// on the introducer). The introducer and opening quote are attempted as a
/// unit so an ordinary identifier beginning with `x` can still fall through
/// to `identAtom`; once the quote is present, malformed/odd-length hex is a
/// committed syntax error instead of being reinterpreted as an identifier.
let private hexBytesLit: Parser<Value, unit> =
    attempt (pstringCI "X" .>> pchar '\'')
    >>. manyChars (satisfy Uri.IsHexDigit)
    .>> pchar '\''
    .>> ws
    >>= fun digits ->
        if digits.Length % 2 <> 0 then
            fail "a hexadecimal binary literal must contain an even number of digits"
        else
            preturn (VBytes(Convert.FromHexString digits))

let private literalValue: Parser<Value, unit> =
    choice
        [ numberLit
          hexBytesLit
          stringLit
          keyword "NULL" >>% VNull
          keyword "TRUE" >>% VInt 1L
          keyword "FALSE" >>% VInt 0L ]

// ---------------------------------------------------------------------------
// CREATE TABLE column types and definitions — parsed ahead of expressions
// since `CAST(expr AS type)` reuses `columnType`.
// ---------------------------------------------------------------------------

/// A parenthesized width/precision like `(11)` or `(10,2)`, parsed and
/// discarded — `Ast.ColumnType` doesn't track display width.
let private ignoredWidth: Parser<unit, unit> =
    optional (between (sym "(") (sym ")") (sepBy1 intTok (sym ",")))

/// `UNSIGNED` (and MySQL's deprecated `ZEROFILL`, which implies it) after
/// any numeric type — carried on the int types, accepted-and-discarded on
/// float/double/decimal since `Ast.ColumnType` doesn't track it there.
let private unsignedFlag: Parser<bool, unit> = opt (keyword "UNSIGNED") |>> Option.isSome

let private widthLen: Parser<int, unit> = between (sym "(") (sym ")") intTok
let private optWidthLen: Parser<int option, unit> = opt widthLen

let private stringListParen: Parser<string list, unit> =
    between (sym "(") (sym ")") (sepBy1 (stringLit |>> (function VString s -> s | _ -> "")) (sym ","))

let private columnType: Parser<ColumnType, unit> =
    choice
        [ keyword "TINYINT" >>. ignoredWidth >>. unsignedFlag |>> TTinyInt
          keyword "SMALLINT" >>. ignoredWidth >>. unsignedFlag |>> TSmallInt
          keyword "MEDIUMINT" >>. ignoredWidth >>. unsignedFlag |>> TMediumInt
          keyword "BIGINT" >>. ignoredWidth >>. unsignedFlag |>> TBigInt
          (keyword "INT" <|> keyword "INTEGER") >>. ignoredWidth >>. unsignedFlag |>> TInt
          keyword "VARCHAR" >>. widthLen |>> TVarchar
          keyword "CHAR" >>. optWidthLen |>> (fun n -> TChar(defaultArg n 1))
          keyword "TINYTEXT" >>% TTinyText
          keyword "MEDIUMTEXT" >>% TMediumText
          keyword "LONGTEXT" >>% TLongText
          keyword "TEXT" >>% TText
          keyword "VARBINARY" >>. widthLen |>> TVarBinary
          keyword "BINARY" >>. optWidthLen |>> (fun n -> TBinary(defaultArg n 1))
          keyword "TINYBLOB" >>% TTinyBlob
          keyword "MEDIUMBLOB" >>% TMediumBlob
          keyword "LONGBLOB" >>% TLongBlob
          keyword "BLOB" >>% TBlob
          keyword "ENUM" >>. stringListParen |>> TEnum
          keyword "SET" >>. stringListParen |>> TSet
          (keyword "DECIMAL" <|> keyword "NUMERIC")
          >>. opt (between (sym "(") (sym ")") ((intTok .>> sym ",") .>>. intTok))
          .>> unsignedFlag
          |>> function
              | Some(p, s) -> TDecimal(p, s)
              | None -> TDecimal(10, 0)
          keyword "DOUBLE" >>. ignoredWidth >>. unsignedFlag >>% TDouble
          keyword "FLOAT" >>. ignoredWidth >>. unsignedFlag >>% TFloat
          keyword "DATETIME" >>. ignoredWidth >>% TDateTime
          keyword "TIMESTAMP" >>. ignoredWidth >>% TTimestamp
          keyword "DATE" >>% TDate
          keyword "TIME" >>. ignoredWidth >>% TTime
          keyword "YEAR" >>. ignoredWidth >>% TYear
          keyword "JSON" >>% TJson
          (keyword "BOOLEAN" <|> keyword "BOOL") >>% TTinyInt false ]
    <?> "column type"

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

// `parenExpr`, function-call arguments and `IN (...)` lists all recurse back
// into the full expression grammar, which is itself built on top of them —
// tie the knot with a forward reference.
let private expr, exprRef = createParserForwardedToRef<Expr, unit> ()

/// `SELECT`'s own clauses recurse into `expr` (projections, `WHERE`, ...),
/// and `expr`'s `Exists` case recurses back into a `SELECT` — tie that knot
/// the same way `expr` ties its own, with the real definition assigned down
/// by `selectStmt` once `tableRef`/`projection`/etc. exist.
let private selectStmtRecord, selectStmtRecordRef = createParserForwardedToRef<SelectStmt, unit> ()

/// `EXPLAIN stmt` wraps any other statement, including (in principle)
/// another `EXPLAIN` — tie the same forward-reference knot so `explainStmt`
/// (defined well before the full `statement` choice exists) can recurse into
/// it; the real definition is assigned at the bottom of the file, same as
/// `expr`/`selectStmtRecord` above.
let private statement, statementRef = createParserForwardedToRef<Statement, unit> ()

let private parenExpr: Parser<Expr, unit> = between (sym "(") (sym ")") expr

let private starAtom: Parser<Expr, unit> = pstring "*" >>. ws >>% Star None

/// `DISTINCT expr` inside a function call's argument list (`COUNT(DISTINCT
/// x)`, `SUM(DISTINCT x)`, ...) — only meaningful for an aggregate, but
/// accepted for any call syntactically, same as the codebase's other
/// accept-and-let-the-executor-care choices (e.g. `MIgnored` column
/// modifiers).
let private distinctArg: Parser<Expr, unit> =
    (attempt (keyword "DISTINCT" >>. expr) |>> Distinct) <|> expr

/// A function call built from a *raw* word rather than `identifier` — tried
/// before the reserved-word check, the same way MySQL disambiguates a
/// function name from a keyword (`IF(...)`, `LEFT(...)`): a word
/// immediately followed by `(` is a function call regardless of whether
/// it's also in `reservedWords`, so `SELECT IF(1,2,3)` reaches the `IF`
/// scalar instead of dying on "reserved keyword", and `VALUES(col)` inside
/// an `ON DUPLICATE KEY UPDATE` clause parses as an ordinary call too (see
/// `Executor`'s substitution of it). `attempt`ed so a bare reserved word
/// with no `(` falls through to `identAtom`'s normal (and still
/// reserved-word-rejecting) column/qualified-column path.
/// `CONVERT(expr USING charset)` — MySQL's special transcode form, parsed
/// ahead of the generic call grammar (its argument list isn't comma-
/// separated expressions). Desugars to a `FuncCall("CONVERT", [expr;
/// charset-name])` that `Functions.convertFn` evaluates.
let private convertUsingAtom: Parser<Expr, unit> =
    attempt (
        keyword "CONVERT"
        >>. between
            (sym "(")
            (sym ")")
            (expr .>> keyword "USING" .>>. (identifier <|> (stringLit |>> (function VString s -> s | _ -> ""))))
    )
    |>> fun (e, charset) -> FuncCall("CONVERT", [ e; Lit(VString charset) ])

let private genericFuncCall: Parser<Expr, unit> =
    attempt ((many1Satisfy2 isIdentStart isIdentChar .>> ws) .>>. (sym "(" >>. sepBy distinctArg (sym ",") .>> sym ")"))
    |>> FuncCall

let private funcCallAtom: Parser<Expr, unit> = attempt (convertUsingAtom) <|> genericFuncCall

/// `GROUP_CONCAT([DISTINCT] expr [ORDER BY key [ASC|DESC], ...] [SEPARATOR
/// 'str'])` — parsed separately from `funcCallAtom` rather than folding
/// `ORDER BY`/`SEPARATOR` into the general call-argument grammar, since it's
/// the one built-in whose argument list isn't just a comma-separated
/// expression list. Each `ORDER BY` key becomes an `OrderBy` marker in the
/// trailing argument list (see its doc) so `Ast.FuncCall` doesn't need its
/// own order-key vocabulary just for this one call; `Executor.evalAggregate`
/// picks the markers back out.
let private groupConcatAtom: Parser<Expr, unit> =
    attempt (keyword "GROUP_CONCAT" >>. sym "(")
    >>. (opt (keyword "DISTINCT") .>>. expr)
    .>>. opt (
        keyword "ORDER" >>. keyword "BY"
        >>. sepBy1 (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc))) (sym ",")
    )
    .>>. opt (keyword "SEPARATOR" >>. (stringLit |>> Lit))
    .>> sym ")"
    |>> fun (((distinctOpt, arg), orderByOpt), sepOpt) ->
        let argExpr = if distinctOpt.IsSome then Distinct arg else arg
        let orderByArgs =
            orderByOpt
            |> Option.defaultValue []
            |> List.map (fun (e, dirOpt) -> OrderBy(e, dirOpt |> Option.defaultValue Asc))
        FuncCall("GROUP_CONCAT", argExpr :: orderByArgs @ (sepOpt |> Option.toList))

/// The `PARTITION BY expr, ... ORDER BY expr [ASC|DESC], ...` body shared by
/// every `OVER (...)` clause below. Written out here rather than reusing the
/// later `orderKey` parser (which needs `Asc`/`Desc`'s default already
/// applied) since `orderKey` isn't defined until after `atom`; duplicating
/// its two-line direction-defaulting logic is cheaper than reordering the
/// file to hoist it.
let private windowClause: Parser<Expr list * OrderKey list, unit> =
    opt (keyword "PARTITION" >>. keyword "BY" >>. sepBy1 expr (sym ","))
    .>>. opt (
        keyword "ORDER" >>. keyword "BY"
        >>. sepBy1 (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc))) (sym ",")
    )
    |>> fun (partitionBy, orderBy) ->
        let orderBy =
            orderBy
            |> Option.defaultValue []
            |> List.map (fun (e, dir) -> e, dir |> Option.defaultValue Asc)

        partitionBy |> Option.defaultValue [], orderBy

/// `ROW_NUMBER() OVER (...)` — see `Ast.Expr.RowNumberOver`'s doc.
let private rowNumberOverAtom: Parser<Expr, unit> =
    attempt (keyword "ROW_NUMBER" >>. sym "(" >>. sym ")" >>. keyword "OVER" >>. sym "(")
    >>. windowClause
    .>> sym ")"
    |>> RowNumberOver

/// `LAG(expr[, offset]) OVER (...)` — see `Ast.Expr.LagOver`'s doc.
let private lagOverAtom: Parser<Expr, unit> =
    attempt (keyword "LAG" >>. sym "(") >>. expr
    .>>. opt (sym "," >>. intTok)
    .>> sym ")"
    .>> keyword "OVER"
    .>> sym "("
    .>>. windowClause
    .>> sym ")"
    |>> fun ((lagExpr, offsetOpt), (partitionBy, orderBy)) ->
        LagOver(lagExpr, offsetOpt |> Option.map int64 |> Option.defaultValue 1L, partitionBy, orderBy)

/// `CAST(expr AS type)` — `SIGNED`/`UNSIGNED [INTEGER]` are only valid as a
/// cast target, not a column type, so they're handled here rather than in
/// `columnType`.
let private castTargetType: Parser<ColumnType, unit> =
    choice
        [ attempt (keyword "SIGNED" >>. optional (keyword "INTEGER")) >>% TInt false
          attempt (keyword "UNSIGNED" >>. optional (keyword "INTEGER")) >>% TInt true
          columnType ]

let private castExpr: Parser<Expr, unit> =
    attempt (keyword "CAST" >>. sym "(" >>. expr .>> keyword "AS" .>>. castTargetType .>> sym ")")
    |>> Cast

let private existsExpr: Parser<Expr, unit> =
    attempt (keyword "EXISTS" >>. sym "(" >>. selectStmtRecord .>> sym ")") |>> Exists

/// `(SELECT ...)` used as a value — tried with `attempt` ahead of `parenExpr`
/// since both start with `(`; a plain parenthesized expression never starts
/// with the `SELECT` keyword, so the two never actually compete once
/// `selectStmtRecord` commits.
let private subqueryExpr: Parser<Expr, unit> =
    attempt (sym "(" >>. selectStmtRecord .>> sym ")") |>> Subquery

/// `INTERVAL n UNIT` — only ever valid as a date-arithmetic function's
/// argument (`DATE_ADD(x, INTERVAL 1 DAY)`), but parsed here as a general
/// expression atom (rather than special-cased only inside a call's argument
/// list) since that's the one place it can occur and this is simpler than
/// threading a separate "date-function argument" grammar through just for
/// it. Encodes as `FuncCall("INTERVAL", [n; Lit(VString "DAY")])` — the unit
/// word is accepted as-is (uppercased) and not validated against MySQL's
/// real unit list, left for whatever date function reads it.
let private intervalAtom: Parser<Expr, unit> =
    attempt (keyword "INTERVAL" >>. expr .>>. (many1Satisfy2 isIdentStart isIdentChar .>> ws))
    |>> fun (n, unit) -> FuncCall("INTERVAL", [ n; Lit(VString(unit.ToUpperInvariant())) ])

/// `TIMESTAMPDIFF(unit, expr1, expr2)` / `TIMESTAMPADD(unit, n, expr)` —
/// the first argument is an *unquoted* unit keyword in real MySQL (`MONTH`,
/// not `'MONTH'`), which `funcCallAtom`'s general call-argument grammar
/// can't parse (every argument goes through `expr`, so a bare `MONTH` there
/// resolves as an ordinary column reference and fails with 1054). Same
/// trick `intervalAtom` already uses for `INTERVAL n UNIT` — parse the unit
/// word directly into a `Lit(VString ...)` and splice it in as the
/// function's first argument, ahead of `funcCallAtom` in `atom`'s `choice`
/// so it wins for these two names specifically.
let private timestampFuncAtom: Parser<Expr, unit> =
    attempt (
        ((keyword "TIMESTAMPDIFF" >>% "TIMESTAMPDIFF") <|> (keyword "TIMESTAMPADD" >>% "TIMESTAMPADD"))
        .>> sym "("
        .>>. (many1Satisfy2 isIdentStart isIdentChar .>> ws)
        .>> sym ","
        .>>. sepBy1 expr (sym ",")
        .>> sym ")"
    )
    |>> fun ((name, unit), args) -> FuncCall(name, Lit(VString(unit.ToUpperInvariant())) :: args)

let private caseWhenThen: Parser<Expr * Expr, unit> = (keyword "WHEN" >>. expr .>> keyword "THEN") .>>. expr

/// `CASE WHEN cond THEN result ... [ELSE result] END` (searched form) and
/// `CASE subject WHEN value THEN result ... [ELSE result] END` (simple
/// form) share one production: `opt expr` right after `CASE` either matches
/// the simple form's subject or (since `WHEN` is a reserved word and can't
/// start an expression) consumes nothing and leaves the searched form's
/// `WHEN` for `caseWhenThen`.
let private caseExpr: Parser<Expr, unit> =
    attempt (
        keyword "CASE" >>. opt expr .>>. many1 caseWhenThen .>>. opt (keyword "ELSE" >>. expr)
        .>> keyword "END"
    )
    |>> fun ((subject, whens), elseBranch) -> Case(subject, whens, elseBranch)

/// A bare word: a column, a qualified `t.col` (or `t.*`, `Star(Some "t")`),
/// or a function call if followed by `(args)` (handled by `funcCallAtom`
/// above, tried first so a reserved-word function name still parses).
let private identAtom: Parser<Expr, unit> =
    funcCallAtom
    <|> (identifier
         >>= fun name ->
             choice
                 [ sym "."
                   >>. ((pstring "*" >>. ws >>% Star(Some name))
                        <|> (identifier |>> fun col -> QualifiedCol(name, col)))
                   preturn (Col name) ])

let private atom: Parser<Expr, unit> =
    choice
        [ subqueryExpr
          parenExpr
          starAtom
          castExpr
          existsExpr
          caseExpr
          intervalAtom
          timestampFuncAtom
          groupConcatAtom
          rowNumberOverAtom
          lagOverAtom
          numberLit |>> Lit
          hexBytesLit |>> Lit
          stringLit |>> Lit
          keyword "NULL" >>% Lit VNull
          keyword "TRUE" >>% Lit(VInt 1L)
          keyword "FALSE" >>% Lit(VInt 0L)
          identAtom ]
    <?> "expression"

/// `col->'$.path'` / `col->>'$.path'` — MySQL's JSON path-extraction
/// operators, desugared at parse time into ordinary function calls
/// (`JSON_EXTRACT`, and `->>` additionally unquotes the result) rather than
/// adding an `Expr` case: they're pure sugar over a function pair the
/// registry already needs to provide, so the executor only ever sees a
/// `FuncCall` either way. Postfix and left-associative (chains `many`), tried
/// at the atom level (rather than as an `opp` infix operator) since the
/// right-hand side is always a string literal path, never a general
/// expression.
let private jsonArrowAtom: Parser<Expr, unit> =
    atom
    >>= fun a ->
        many (
            choice
                [ sym "->>" >>. stringLit |>> fun p e -> FuncCall("JSON_UNQUOTE", [ FuncCall("JSON_EXTRACT", [ e; Lit p ]) ])
                  sym "->" >>. stringLit |>> fun p e -> FuncCall("JSON_EXTRACT", [ e; Lit p ]) ]
        )
        |>> List.fold (fun acc f -> f acc) a

/// Arithmetic: `+ -` bind loosest, `* / % DIV` tighter, unary `-` tightest.
/// `Ast.Op` has no modulo or unary-negation case, so both desugar: `%`
/// becomes a call to `MOD` (which is what MySQL's `%` already means) and
/// unary `-x` becomes `0 - x`.
let private opp = OperatorPrecedenceParser<Expr, unit, unit>()
let private arithExpr = opp.ExpressionParser

/// `expr COLLATE name` — a postfix on the arithmetic term (`a + b COLLATE c`
/// is `a + (b COLLATE c)` in MySQL), validated against the collation
/// registry here; the tag rides the `Collate` AST node into `Executor`,
/// where comparisons resolve it.
let private collateTerm: Parser<Expr, unit> =
    jsonArrowAtom
    .>>. opt (keyword "COLLATE" >>. (identifier <|> (stringLit |>> (function VString name -> name | _ -> ""))))
    >>= fun (e, nameOpt) ->
        match nameOpt with
        | None -> preturn e
        | Some name ->
            match Collation.tryFind name with
            | Some _ -> preturn (Collate(e, name))
            | None -> fail (sprintf "Unknown collation '%s'" name)

opp.TermParser <- collateTerm
opp.AddOperator(InfixOperator("+", ws, 1, Associativity.Left, (fun a b -> BinOp(Add, a, b))))
opp.AddOperator(InfixOperator("-", ws, 1, Associativity.Left, (fun a b -> BinOp(Sub, a, b))))
opp.AddOperator(InfixOperator("*", ws, 2, Associativity.Left, (fun a b -> BinOp(Mul, a, b))))
opp.AddOperator(InfixOperator("/", ws, 2, Associativity.Left, (fun a b -> BinOp(Div, a, b))))
opp.AddOperator(InfixOperator("%", ws, 2, Associativity.Left, (fun a b -> FuncCall("MOD", [ a; b ]))))

// `DIV` is a keyword operator, not punctuation, so it needs the same
// word-boundary guard `keyword` uses (`nextCharSatisfiesNot isIdentChar`) —
// without it, a column named `div_price` would parse as the operator `DIV`
// followed by a stray `_price` term. `OperatorPrecedenceParser.InfixOperator`
// matches its operator string case-sensitively with no case-insensitive
// option, so both the all-caps and all-lowercase spellings (by far the two
// real-world casings) are registered explicitly — ponytail: a query mixing
// case mid-keyword (`Div`) won't match; fold in real case-insensitive
// matching if that ever shows up outside a lint test.
let private divKeywordBoundary: Parser<unit, unit> = nextCharSatisfiesNot isIdentChar >>. ws
opp.AddOperator(InfixOperator("DIV", divKeywordBoundary, 2, Associativity.Left, (fun a b -> BinOp(IntDiv, a, b))))
opp.AddOperator(InfixOperator("div", divKeywordBoundary, 2, Associativity.Left, (fun a b -> BinOp(IntDiv, a, b))))
opp.AddOperator(PrefixOperator("-", ws, 3, true, (fun e -> BinOp(Sub, Lit(VInt 0L), e))))

/// `IN (SELECT ...)` vs. `IN (expr, expr, ...)` — both start with `(`, so
/// the subquery form is tried first (`attempt`ed since `selectStmtRecord`
/// commits on its leading `SELECT` keyword the same way `subqueryExpr`
/// does) before falling back to the literal candidate list.
let private inCandidates: Parser<Choice<SelectStmt, Expr list>, unit> =
    sym "(" >>. (attempt (selectStmtRecord |>> Choice1Of2) <|> (sepBy1 expr (sym ",") |>> Choice2Of2)) .>> sym ")"

let private betweenTail: Parser<Expr * Expr, unit> = (arithExpr .>> keyword "AND") .>>. arithExpr

/// The optional `ESCAPE '<c>'` tail on a `LIKE` predicate, naming the
/// character that un-wildcards a literal `%`/`_` in the pattern (MySQL
/// default: backslash). `None` means "use the default" — covers both a
/// missing clause and the rare `ESCAPE ''` (MySQL then disables escaping
/// entirely, which `likeToRegex`'s no-escape-char behavior can't express
/// separately from "default", but nobody writes that in practice).
let private escapeClause: Parser<char option, unit> =
    opt (keyword "ESCAPE" >>. stringLit)
    |>> Option.bind (function
        | VString s when s.Length > 0 -> Some s.[0]
        | _ -> None)

/// Comparisons and the `IS NULL` / `LIKE` / `IN` / `BETWEEN` predicates,
/// all sitting at the same precedence just above arithmetic. The `NOT
/// LIKE`/`NOT IN`/`NOT BETWEEN` forms desugar to `Not (Like ...)` etc.
/// since `Ast.Expr` doesn't carry negated variants of its own.
let private compareOp: Parser<Op, unit> =
    choice
        [ pstring "<=>" >>% NullSafeEq
          pstring "<=" >>% Lte
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
        let inExpr xs =
            match xs with
            | Choice1Of2 sel -> InSubquery(left, sel)
            | Choice2Of2 candidates -> In(left, candidates)

        choice
            [ attempt (keyword "IS" >>. keyword "NOT" >>. keyword "NULL") >>% IsNotNull left
              attempt (keyword "IS" >>. keyword "NULL") >>% IsNull left
              attempt (keyword "IS" >>. keyword "NOT" >>. keyword "TRUE") >>% Not(IsTrue left)
              attempt (keyword "IS" >>. keyword "NOT" >>. keyword "FALSE") >>% Not(IsFalse left)
              attempt (keyword "IS" >>. keyword "TRUE") >>% IsTrue left
              attempt (keyword "IS" >>. keyword "FALSE") >>% IsFalse left
              attempt (keyword "NOT" >>. keyword "LIKE" >>. keyword "BINARY") >>. arithExpr .>>. escapeClause
              |>> fun (p, esc) -> Not(Like(left, p, true, esc))
              attempt (keyword "LIKE" >>. keyword "BINARY") >>. arithExpr .>>. escapeClause
              |>> fun (p, esc) -> Like(left, p, true, esc)
              attempt (keyword "NOT" >>. keyword "LIKE") >>. arithExpr .>>. escapeClause
              |>> fun (p, esc) -> Not(Like(left, p, false, esc))
              keyword "LIKE" >>. arithExpr .>>. escapeClause |>> fun (p, esc) -> Like(left, p, false, esc)
              attempt (keyword "NOT" >>. (keyword "REGEXP" <|> keyword "RLIKE")) >>. arithExpr
              |>> fun p -> Not(Regexp(left, p))
              (keyword "REGEXP" <|> keyword "RLIKE") >>. arithExpr |>> fun p -> Regexp(left, p)
              attempt (keyword "NOT" >>. keyword "IN") >>. inCandidates |>> (inExpr >> Not)
              keyword "IN" >>. inCandidates |>> inExpr
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

type private ColMod =
    | MNotNull
    | MNull
    | MDefault of ColumnDefault
    | MAutoIncrement
    | MPrimaryKey
    | MUnique
    | MGenerated of Expr
    /// A validated `COLLATE name` — the column's collation, resolved against
    /// the registry at parse time so an unknown name is a clean error.
    | MCollate of string
    /// A validated `CHARACTER SET name` (utf8mb4/latin1/ascii).
    | MCharset of string
    /// `COMMENT 'txt'`, `CHARACTER SET x`, `ON UPDATE CURRENT_TIMESTAMP` —
    /// accepted so the column definition parses, but nothing in
    /// `Ast.ColumnDef` tracks them (ponytail: add fields if a migration's
    /// assertion ever depends on one).
    | MIgnored

let private defaultValueLit: Parser<ColumnDefault, unit> =
    (keyword "CURRENT_TIMESTAMP" >>% DCurrentTimestamp) <|> (literalValue |>> DConst)

/// A charset/collation name — Laravel emits `COLLATE 'utf8mb4_unicode_ci'`
/// (quoted) at the table level but a bare identifier at the column level, so
/// this accepts either.
let private identOrString: Parser<string, unit> =
    identifier <|> (stringLit |>> (function VString s -> s | _ -> ""))

/// `[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]` — a computed column
/// (`char(16) ... AS (UNHEX(MD5(\`key\`)))`, Laravel Pulse's dedup key hash).
/// Reuses the full `expr` grammar (arbitrary nested function calls), and the
/// parsed `Expr` is kept on `ColumnDef.Generated` for `Executor`/`Storage`
/// to evaluate on insert/update — VIRTUAL vs STORED isn't distinguished
/// (see the doc on `Ast.ColumnDef.Generated`).
let private generatedColumn: Parser<Expr, unit> =
    optional (keyword "GENERATED" >>. keyword "ALWAYS")
    >>. keyword "AS"
    >>. sym "("
    >>. expr
    .>> sym ")"
    .>> optional (keyword "VIRTUAL" <|> keyword "STORED")

let private colMod: Parser<ColMod, unit> =
    choice
        [ attempt (keyword "NOT" >>. keyword "NULL") >>% MNotNull
          keyword "NULL" >>% MNull
          keyword "DEFAULT" >>. defaultValueLit .>> optional (keyword "ON" >>. keyword "UPDATE" >>. keyword "CURRENT_TIMESTAMP") |>> MDefault
          keyword "AUTO_INCREMENT" >>% MAutoIncrement
          attempt (keyword "PRIMARY" >>. keyword "KEY") >>% MPrimaryKey
          keyword "UNIQUE" >>. optional (keyword "KEY") >>% MUnique
          attempt (keyword "ON" >>. keyword "UPDATE" >>. keyword "CURRENT_TIMESTAMP") >>% MIgnored
          keyword "COMMENT" >>. stringLit >>% MIgnored
          attempt (keyword "CHARACTER" >>. keyword "SET" <|> keyword "CHARSET")
          >>. identOrString
          >>= fun name ->
              match name.ToLowerInvariant() with
              | "utf8mb4"
              | "utf8"
              | "latin1"
              | "ascii"
              | "binary" -> preturn (MCharset(name.ToLowerInvariant()))
              | _ -> fail (sprintf "Unknown character set: '%s'" name)
          keyword "COLLATE"
          >>. identOrString
          >>= fun name ->
              match Collation.tryFind name with
              | Some _ -> preturn (MCollate name)
              | None -> fail (sprintf "Unknown collation '%s'" name)
          attempt generatedColumn |>> MGenerated ]

let private columnDef: Parser<ColumnDef, unit> =
    (identifier .>>. columnType .>>. many colMod)
    |>> fun ((name, ty), mods) ->
        { Name = name
          Type = ty
          Nullable = not (List.contains MNotNull mods)
          Default = mods |> List.tryPick (function MDefault v -> Some v | _ -> None)
          AutoIncrement = List.contains MAutoIncrement mods
          PrimaryKey = List.contains MPrimaryKey mods
          Unique = List.contains MUnique mods
          Generated = mods |> List.tryPick (function MGenerated e -> Some e | _ -> None)
          Collation = mods |> List.tryPick (function MCollate c -> Some c | _ -> None)
          Charset = mods |> List.tryPick (function MCharset c -> Some c | _ -> None) }

/// `AFTER col` / `FIRST` after an `ADD`/`MODIFY`/`CHANGE COLUMN` —
/// `PositionDefault` when neither is written.
let private colPosition: Parser<ColumnPosition, unit> =
    opt ((keyword "AFTER" >>. identifier |>> PositionAfter) <|> (keyword "FIRST" >>% PositionFirst))
    |>> Option.defaultValue PositionDefault

// ---------------------------------------------------------------------------
// CREATE TABLE trailing items: PRIMARY KEY / INDEX / FOREIGN KEY
// ---------------------------------------------------------------------------

/// A trailing `PRIMARY KEY (col, ...)` table constraint. `Ast.CreateTable`
/// has no separate slot for it, so it's applied as a post-pass that flags
/// the matching columns' `PrimaryKey` field instead.
let private trailingPrimaryKey: Parser<string list, unit> =
    attempt (keyword "PRIMARY" >>. keyword "KEY") >>. between (sym "(") (sym ")") (sepBy1 identifier (sym ","))

/// One column inside an index's column list, with its optional MySQL
/// "key length" (`col(191)`) parsed and discarded — `Ast.IndexDef` doesn't
/// track prefix lengths.
let private indexColumn: Parser<string, unit> = identifier .>> optional (between (sym "(") (sym ")") intTok)

/// `[UNIQUE] KEY|INDEX name (cols)` — `UNIQUE` alone (no `KEY`/`INDEX`) is
/// also legal MySQL, so the `KEY`/`INDEX` keyword itself is optional once
/// `UNIQUE` has matched; without `UNIQUE`, `KEY`/`INDEX` is required so this
/// doesn't swallow an ordinary column definition.
let private indexPrefix: Parser<bool, unit> =
    (keyword "UNIQUE" >>. optional (keyword "KEY" <|> keyword "INDEX") >>% true)
    <|> ((keyword "KEY" <|> keyword "INDEX") >>% false)

let private indexItem: Parser<IndexDef, unit> =
    (indexPrefix .>>. opt identifier .>>. between (sym "(") (sym ")") (sepBy1 indexColumn (sym ",")))
    |>> fun ((unique, name), cols) ->
        { Name = name |> Option.defaultValue (List.head cols)
          Columns = cols
          Unique = unique }

let private refAction: Parser<string, unit> =
    choice
        [ keyword "CASCADE" >>% "CASCADE"
          attempt (keyword "SET" >>. keyword "NULL") >>% "SET NULL"
          attempt (keyword "SET" >>. keyword "DEFAULT") >>% "SET DEFAULT"
          keyword "RESTRICT" >>% "RESTRICT"
          keyword "NO" >>. keyword "ACTION" >>% "NO ACTION" ]

/// `ON DELETE ...` / `ON UPDATE ...`, order-independent and both optional —
/// gathered with `many` rather than two fixed `opt`s since MySQL allows
/// either order (Laravel always emits `ON DELETE` first, but nothing in the
/// grammar requires it).
let private foreignKeyRefOptions: Parser<string option * string option, unit> =
    many (
        (attempt (keyword "ON" >>. keyword "DELETE") >>. refAction |>> fun a -> Choice1Of2 a)
        <|> (attempt (keyword "ON" >>. keyword "UPDATE") >>. refAction |>> fun a -> Choice2Of2 a)
    )
    |>> fun opts ->
        (opts |> List.tryPick (function Choice1Of2 a -> Some a | _ -> None),
         opts |> List.tryPick (function Choice2Of2 a -> Some a | _ -> None))

/// `CONSTRAINT [symbol]` — the symbol name is optional even when
/// `CONSTRAINT` itself is present, so a bare `CONSTRAINT FOREIGN KEY (...)`
/// (no name at all) parses too.
let private constraintName: Parser<string option, unit> =
    opt (keyword "CONSTRAINT" >>. opt identifier) |>> Option.flatten

let private foreignKeyItem: Parser<ForeignKeyDef, unit> =
    (constraintName .>> keyword "FOREIGN" .>> keyword "KEY"
     .>>. between (sym "(") (sym ")") (sepBy1 identifier (sym ","))
     .>> keyword "REFERENCES"
     .>>. identifier
     .>>. between (sym "(") (sym ")") (sepBy1 identifier (sym ","))
     .>>. foreignKeyRefOptions)
    |>> fun ((((cname, cols), refTable), refCols), (onDelete, onUpdate)) ->
        { Name = cname |> Option.defaultValue (sprintf "%s_%s_foreign" refTable (List.head cols))
          Columns = cols
          RefTable = refTable
          RefColumns = refCols
          OnDelete = onDelete
          OnUpdate = onUpdate }

/// One item inside a `CREATE TABLE (...)` list: an ordinary column, or one
/// of the trailing table-level constraints. Each alternative is tried with
/// `attempt` since they can share a leading keyword (`CONSTRAINT ... FOREIGN
/// KEY` vs. a column literally named `constraint`) before diverging.
type private CreateItem =
    | CColumn of ColumnDef
    | CPrimaryKey of string list
    | CIndex of IndexDef
    | CForeignKey of ForeignKeyDef

let private createTableItem: Parser<CreateItem, unit> =
    choice
        [ attempt (foreignKeyItem |>> CForeignKey)
          attempt (trailingPrimaryKey |>> CPrimaryKey)
          attempt (indexItem |>> CIndex)
          columnDef |>> CColumn ]

/// `ENGINE=`, `CHARSET=`/`DEFAULT CHARSET=` table options: accepted and
/// discarded, same treatment as column display widths. `COLLATE=` is the
/// one tracked option — it becomes the table's column default (validated
/// against the collation registry), which `createTable` bakes into every
/// column that didn't name one explicitly.
let private tableOption: Parser<string option * string option, unit> =
    choice
        [ keyword "ENGINE" >>. opt (sym "=") >>. identOrString >>% (None, None)
          attempt (keyword "DEFAULT" >>. (keyword "CHARSET" <|> (keyword "CHARACTER" >>. keyword "SET")))
          >>. opt (sym "=")
          >>. identOrString
          >>= fun name ->
              match name.ToLowerInvariant() with
              | "utf8mb4"
              | "utf8"
              | "latin1"
              | "ascii"
              | "binary" -> preturn (Some(name.ToLowerInvariant()), None)
              | _ -> fail (sprintf "Unknown character set: '%s'" name)
          keyword "CHARSET" >>. opt (sym "=") >>. identOrString
          >>= fun name ->
              match name.ToLowerInvariant() with
              | "utf8mb4"
              | "utf8"
              | "latin1"
              | "ascii"
              | "binary" -> preturn (Some(name.ToLowerInvariant()), None)
              | _ -> fail (sprintf "Unknown character set: '%s'" name)
          keyword "COLLATE"
          >>. opt (sym "=")
          >>. identOrString
          >>= fun name ->
              match Collation.tryFind name with
              | Some _ -> preturn (None, Some name)
              | None -> fail (sprintf "Unknown collation '%s'" name) ]

let private tableOptions: Parser<string option * string option, unit> =
    many tableOption
    |>> fun opts ->
        opts |> List.fold (fun (cs, col) (c, l) -> c |> Option.orElse cs, l |> Option.orElse col) (None, None)

let private createTable: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. qualifiedTableName
     .>>. between (sym "(") (sym ")") (sepBy1 createTableItem (sym ","))
     .>>. tableOptions)
    |>> fun (((ifNotExists, name), items), (tableCharset, tableCollation)) ->
        let pkNames = items |> List.collect (function CPrimaryKey names -> names | _ -> [])
        let explicitIndexes = items |> List.choose (function CIndex ix -> Some ix | _ -> None)
        let foreignKeys = items |> List.choose (function CForeignKey fk -> Some fk | _ -> None)

        let columns =
            items
            |> List.choose (function
                | CColumn c ->
                    // Table-level COLLATE is the default for columns that
                    // didn't name one; the explicit column COLLATE wins.
                    // String-typed columns only — MySQL doesn't attach the
                    // table charset/collation to numeric/temporal columns
                    // (verified: `CREATE TABLE t (id INT) COLLATE=utf8mb4_bin`
                    // shows plain `id int` in SHOW CREATE and NULLs in
                    // information_schema.COLUMNS).
                    let isStringy =
                        match c.Type with
                        | TChar _
                        | TVarchar _
                        | TTinyText
                        | TText
                        | TMediumText
                        | TLongText
                        | TEnum _
                        | TSet _ -> true
                        | _ -> false

                    let withDefaults =
                        if isStringy then
                            { c with
                                Collation = c.Collation |> Option.orElse tableCollation
                                Charset = c.Charset |> Option.orElse tableCharset }
                        else
                            c

                    Some(if List.contains c.Name pkNames then { withDefaults with PrimaryKey = true } else withDefaults)
                | _ -> None)

        // A column-level `UNIQUE` modifier is just sugar for a single-column
        // unique index named after the column, so it lands in the same
        // `Indexes` bucket a trailing `UNIQUE KEY` would.
        let uniqueColumnIndexes =
            columns
            |> List.filter (fun c -> c.Unique)
            |> List.map (fun c -> { Name = c.Name; Columns = [ c.Name ]; Unique = true })

        CreateTable(name, columns, explicitIndexes @ uniqueColumnIndexes, foreignKeys, ifNotExists, tableCharset, tableCollation)

let private createIndexStmt: Parser<Statement, unit> =
    (keyword "CREATE" >>. (opt (keyword "UNIQUE") |>> Option.isSome)
     .>> keyword "INDEX"
     .>>. identifier
     .>> keyword "ON"
     .>>. qualifiedTableName
     .>>. between (sym "(") (sym ")") (sepBy1 indexColumn (sym ",")))
    |>> fun (((unique, name), table), cols) -> CreateIndex(name, table, cols, unique)

let private dropIndexStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "INDEX"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. identifier
     .>> keyword "ON"
     .>>. qualifiedTableName)
    |>> fun ((ifExists, name), table) -> DropIndexStmt(name, table, ifExists)

let private dropTable: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 qualifiedTableName (sym ","))
    |>> fun (ifExists, names) -> DropTable(names, ifExists)

let private truncateTable: Parser<Statement, unit> =
    keyword "TRUNCATE" >>. opt (keyword "TABLE") >>. qualifiedTableName |>> Truncate

/// `[DEFAULT] CHARACTER SET [=] x` / `[DEFAULT] COLLATE [=] y`, in either
/// order, either/both/neither present — `CREATE`/`ALTER DATABASE`'s own
/// tail, accepted and discarded like `tableOption`'s charset/collate
/// alternatives, but with `COLLATE` also taking the `DEFAULT` prefix Laravel
/// emits (`MySqlGrammar::compileCreateDatabase`, what
/// `Illuminate\Testing\Concerns\TestDatabases` calls to build each parallel
/// worker's own database) which `tableOption` doesn't need to.
let private databaseOption: Parser<unit, unit> =
    choice
        [ attempt (
              optional (keyword "DEFAULT")
              >>. (keyword "CHARSET" <|> (keyword "CHARACTER" >>. keyword "SET"))
          )
          >>. opt (sym "=")
          >>. identOrString
          >>% ()
          optional (keyword "DEFAULT") >>. keyword "COLLATE" >>. opt (sym "=") >>. identOrString >>% () ]

let private databaseOptions: Parser<unit, unit> = skipMany databaseOption

let private createDatabaseStmt: Parser<Statement, unit> =
    (keyword "CREATE" >>. (keyword "DATABASE" <|> keyword "SCHEMA")
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. identifier
     .>> databaseOptions)
    |>> fun (ifNotExists, name) -> CreateDatabase(name, ifNotExists)

let private dropDatabaseStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. (keyword "DATABASE" <|> keyword "SCHEMA")
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. identifier)
    |>> fun (ifExists, name) -> DropDatabase(name, ifExists)

let private alterDatabaseStmt: Parser<Statement, unit> =
    (keyword "ALTER" >>. (keyword "DATABASE" <|> keyword "SCHEMA") >>. identifier .>> databaseOptions)
    |>> AlterDatabase

// ---------------------------------------------------------------------------
// ALTER TABLE / RENAME TABLE
// ---------------------------------------------------------------------------

let private optColumnKw: Parser<unit, unit> = optional (keyword "COLUMN")

let private addColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. optColumnKw >>. columnDef .>>. colPosition) |>> AddColumn

let private addPrimaryKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. trailingPrimaryKey) |>> AddPrimaryKey

let private addIndexAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. indexItem) |>> AddIndex

let private addForeignKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. foreignKeyItem) |>> AddForeignKey

let private dropForeignKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. keyword "FOREIGN" >>. keyword "KEY" >>. identifier) |>> DropForeignKey

let private dropIndexAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. (keyword "INDEX" <|> keyword "KEY") >>. identifier) |>> DropIndexAction

let private dropColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. optColumnKw >>. identifier) |>> DropColumn

let private modifyColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "MODIFY" >>. optColumnKw >>. columnDef .>>. colPosition) |>> ModifyColumn

let private changeColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "CHANGE" >>. optColumnKw >>. identifier .>>. columnDef .>>. colPosition)
    |>> fun ((oldName, newDef), position) -> ChangeColumn(oldName, newDef, position)

let private renameColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "RENAME" >>. keyword "COLUMN" >>. identifier .>> keyword "TO" .>>. identifier)
    |>> RenameColumnTo

let private renameToAction: Parser<AlterAction, unit> =
    attempt (keyword "RENAME" >>. opt (keyword "TO" <|> keyword "AS") >>. identifier) |>> RenameTo

let private alterAction: Parser<AlterAction, unit> =
    choice
        [ addForeignKeyAction
          addPrimaryKeyAction
          addIndexAction
          addColumnAction
          dropForeignKeyAction
          dropIndexAction
          dropColumnAction
          modifyColumnAction
          changeColumnAction
          renameColumnAction
          renameToAction ]
    <?> "ALTER TABLE action"

let private alterTableStmt: Parser<Statement, unit> =
    (keyword "ALTER" >>. keyword "TABLE" >>. qualifiedTableName .>>. sepBy1 alterAction (sym ","))
    |>> AlterTable

let private renameTablePair: Parser<string * string, unit> =
    qualifiedTableName .>> (keyword "TO" <|> keyword "AS") .>>. qualifiedTableName

let private renameTableStmt: Parser<Statement, unit> =
    (keyword "RENAME" >>. keyword "TABLE" >>. sepBy1 renameTablePair (sym ",")) |>> RenameTable

// ---------------------------------------------------------------------------
// INSERT / SELECT / UPDATE / DELETE
// ---------------------------------------------------------------------------

let private onDuplicateKeyUpdate: Parser<(string * Expr) list, unit> =
    keyword "ON" >>. keyword "DUPLICATE" >>. keyword "KEY" >>. keyword "UPDATE"
    >>. sepBy1 ((identifier .>> sym "=") .>>. expr) (sym ",")

/// `INSERT INTO t (cols) VALUES (...), (...) [ON DUPLICATE KEY UPDATE ...]`
/// or `INSERT INTO t (cols) SELECT ...` — both share the same `INSERT
/// [IGNORE] INTO table (cols)?` prefix, diverging only on the `VALUES`/
/// `SELECT` keyword right after it, so parsing that prefix once and
/// `choice`-ing between the two row sources needs no `attempt` backtracking
/// (see the `statement` parser's doc on why that matters).
let private insertStmt: Parser<Statement, unit> =
    (keyword "INSERT" >>. (opt (keyword "IGNORE") |>> Option.isSome)
     .>> keyword "INTO"
     .>>. qualifiedTableName
     .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
     .>>. choice
              [ (keyword "VALUES" >>. sepBy1 (between (sym "(") (sym ")") (sepBy1 expr (sym ","))) (sym ",")
                 .>>. opt onDuplicateKeyUpdate)
                |>> Choice1Of2
                selectStmtRecord |>> Choice2Of2 ])
    |>> fun (((ignoreDuplicates, table), cols), branch) ->
        let cols = cols |> Option.defaultValue []

        match branch with
        | Choice1Of2(rows, onDup) -> Insert(table, cols, rows, onDup |> Option.defaultValue [], ignoreDuplicates)
        | Choice2Of2 select -> InsertSelect(table, cols, select, ignoreDuplicates)

/// A projection's alias — `AS name`, or real MySQL's implicit form with no
/// `AS` at all (`SELECT 1 x FROM t`, `SELECT price * qty total FROM
/// orders`): a bare word right after the expression that isn't the next
/// clause's keyword. `identifier` already rejects every word in
/// `reservedWords` (`FROM`/`WHERE`/`GROUP`/`ORDER`/`HAVING`/`LIMIT`/...), so
/// this only fires on an actual alias, not the start of the next clause;
/// `attempt`ed so a comma or clause keyword right after the expression
/// cleanly falls through to `None` instead of failing the whole projection.
let private projectionAlias: Parser<string option, unit> =
    (attempt (keyword "AS" >>. identifier) |>> Some) <|> (attempt identifier |>> Some) <|> preturn None

let private projection: Parser<Projection, unit> = expr .>>. projectionAlias

let private orderKey: Parser<OrderKey, unit> =
    (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc)))
    |>> fun (e, dir) -> (e, dir |> Option.defaultValue Asc)

/// LIMIT/OFFSET accept up to 2^64-1 in MySQL (the "no limit, just an
/// offset" idiom is `LIMIT 18446744073709551615 OFFSET n`), while
/// `Ast.SelectStmt.Limit`/`Offset` stay plain `int` — nothing this small an
/// in-memory engine holds needs a row count past `Int32.MaxValue`, so clamp
/// rather than widen the AST.
let private limitTok: Parser<int, unit> =
    puint64 .>> ws |>> fun n -> if n > uint64 Int32.MaxValue then Int32.MaxValue else int n

/// `LIMIT n`, `LIMIT n OFFSET m`, and the MySQL-specific `LIMIT m, n` (which
/// means offset `m`, count `n` — the arguments are in the opposite order
/// from `LIMIT n OFFSET m`).
let private limitClause: Parser<int option * int option, unit> =
    keyword "LIMIT" >>. limitTok
    >>= fun a ->
        (sym "," >>. limitTok |>> fun b -> (Some b, Some a))
        <|> (keyword "OFFSET" >>. limitTok |>> fun b -> (Some a, Some b))
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

/// A single `SELECT`, redundantly wrapped in one or more parens —
/// `(SELECT ...)` and `((SELECT ...))` both reduce to the same `SelectStmt`.
/// This is what lets a `UNION` branch (`selectOrUnionBranches` below, shared
/// with the top-level `selectOrUnionStmt`) accept MySQL's `(SELECT ...)
/// UNION (SELECT ...)` form where every branch is individually
/// parenthesized, rather than just the bare `SELECT ...` this engine already
/// handled.
let private parenSelect, parenSelectRef = createParserForwardedToRef<SelectStmt, unit> ()
parenSelectRef.Value <- attempt (sym "(" >>. parenSelect .>> sym ")") <|> selectStmtRecord

/// `parenSelect`, paired with whether this particular branch was actually
/// wrapped in parens — `selectOrUnionBranches` needs that to decide whose
/// `ORDER BY`/`LIMIT` a union's final branch contributes (see its doc).
let private parenSelectFlag: Parser<SelectStmt * bool, unit> =
    (attempt (sym "(" >>. parenSelect .>> sym ")") |>> fun s -> s, true)
    <|> (selectStmtRecord |>> fun s -> s, false)

/// `UNION [ALL|DISTINCT]` between two `SELECT`s — `ALL` keeps duplicates,
/// plain `UNION` (or explicit `DISTINCT`) dedupes, matching MySQL's default.
let private unionOp: Parser<bool, unit> =
    keyword "UNION" >>. ((keyword "ALL" >>% true) <|> (optional (keyword "DISTINCT") >>% false))

/// One `SELECT`, or a `UNION`-chained sequence of them — shared between a
/// top-level statement (`selectOrUnionStmt`) and a derived table's body
/// (`derivedTable`), since MySQL allows `UNION` in both places and each
/// branch may be individually parenthesized (`parenSelectFlag`).
let private selectOrUnionBranches: Parser<(SelectStmt * bool) * (bool * (SelectStmt * bool)) list, unit> =
    parenSelectFlag .>>. many (unionOp .>>. parenSelectFlag)

/// A trailing union-level `ORDER BY`/`LIMIT`, tried only once at least one
/// `UNION` branch has parsed — what `MySqlGrammar::compileUnionOrders`/
/// `compileUnionLimit` emit for `->union(...)->orderBy()->limit()`, legal
/// once every branch is individually parenthesized (a bare final branch's
/// own trailing clause already grammatically belongs to the union as a
/// whole, so there's nothing left here to parse in that case — see
/// `combineUnion`'s doc). Independently optional, like a plain `SELECT`'s.
let private unionTailClause: Parser<OrderKey list option * (int option * int option) option, unit> =
    opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ",")) .>>. opt limitClause

/// Resolves one `selectOrUnionBranches` parse plus its optional
/// `unionTailClause` into the union's own `(first, rest, orderBy, limit,
/// offset)`, shared between `selectOrUnionStmt` and `derivedTable`. Real
/// MySQL only lets a branch's own trailing `ORDER BY`/`LIMIT` win over the
/// union's when that branch is individually parenthesized
/// (`(SELECT ... ORDER BY x)`) — a bare final branch's clause always belongs
/// to the union as a whole instead, so it's promoted here when no explicit
/// union-level clause was parsed. Requires `rest` non-empty; both call sites
/// only invoke this once they've confirmed at least one `UNION` branch.
let private combineUnion
    ((first, _): SelectStmt * bool)
    (rest: (bool * (SelectStmt * bool)) list)
    ((unionOrderBy, unionLimitOffset): OrderKey list option * (int option * int option) option)
    : SelectStmt * (bool * SelectStmt) list * OrderKey list * int option * int option =
    // The bare (unparenthesized) final branch's trailing ORDER BY/LIMIT
    // belongs to the union as a whole — strip it from that branch so it
    // doesn't re-run the clause against its own columns (`... UNION SELECT
    // 2 ORDER BY v` would otherwise resolve `v` — a union-level alias —
    // inside the branch and 1054). Parenthesized branches keep their own.
    let restStmts =
        rest
        |> List.mapi (fun i (op, (s, parenthesized)) ->
            if i = rest.Length - 1 && not parenthesized then
                op, { s with OrderBy = []; Limit = None; Offset = None }
            else
                op, s)

    let lastStmt, lastParenthesized = rest |> List.last |> snd

    let promotedOrderBy, promotedLimit, promotedOffset =
        if lastParenthesized then [], None, None else lastStmt.OrderBy, lastStmt.Limit, lastStmt.Offset

    let orderBy = unionOrderBy |> Option.defaultValue promotedOrderBy

    let limit, offset =
        match unionLimitOffset with
        | Some(l, o) -> l, o
        | None -> promotedLimit, promotedOffset

    first, restStmts, orderBy, limit, offset

/// `FROM (SELECT ...) AS alias` — a derived table; the alias is required
/// (MySQL rejects an unaliased one), so unlike `tableRef`'s optional alias
/// this one is a plain `identifier`, not an `opt`. Tried with `attempt`
/// ahead of `tableRef |>> FromTable` since both start by looking for `(` vs.
/// a bare identifier — no ambiguity in practice (a real table name is never
/// `(`), but `attempt` keeps the two alternatives cleanly independent. The
/// body may itself be a `UNION` (Laravel's `unionAll(...)->paginate()`
/// compiles to `SELECT COUNT(*) FROM ((SELECT ...) UNION (SELECT ...)) AS
/// alias`), hence `selectOrUnionBranches` rather than a bare `selectStmtRecord`
/// — and `unionTailClause` lets a union-level `ORDER BY`/`LIMIT` sit inside
/// the derived table's own parens, ahead of its closing `)`.
let private derivedTable: Parser<FromItem, unit> =
    attempt (
        sym "("
        >>. (selectOrUnionBranches
             >>= fun (first, rest) ->
                 match rest with
                 | [] -> preturn (PlainSelect(fst first))
                 | _ -> unionTailClause |>> fun tail -> combineUnion first rest tail |> UnionSelect)
        .>> sym ")"
        .>>. ((keyword "AS" >>. identifier) <|> identifier)
    )
    |>> fun (selectOrUnion, alias) -> FromSubquery(selectOrUnion, alias)

let private fromItem: Parser<FromItem, unit> = derivedTable <|> (tableRef |>> FromTable)

/// `[INNER] JOIN`, `LEFT [OUTER] JOIN`, and `RIGHT [OUTER] JOIN` all require
/// an `ON` or a `USING (...)`; `NATURAL [INNER|LEFT [OUTER]|RIGHT [OUTER]]`
/// JOIN` requires neither (an `ON` after `NATURAL` is a MySQL syntax error,
/// which the grammar rejects naturally by not consuming it); `CROSS JOIN`
/// (parsed separately by `crossJoinClause` below) never takes one. A bare
/// `JOIN` (no `INNER`) means the same as `INNER JOIN`, matching MySQL.
let private joinKind: Parser<JoinKind, unit> =
    attempt (keyword "NATURAL" >>. keyword "LEFT" >>. optional (keyword "OUTER") >>. keyword "JOIN" >>% NaturalLeftJoin)
    <|> attempt (keyword "NATURAL" >>. keyword "RIGHT" >>. optional (keyword "OUTER") >>. keyword "JOIN" >>% NaturalRightJoin)
    <|> (keyword "NATURAL" >>. optional (keyword "INNER") >>. keyword "JOIN" >>% NaturalJoin)
    <|> (keyword "INNER" >>. keyword "JOIN" >>% InnerJoin)
    <|> (keyword "LEFT" >>. optional (keyword "OUTER") >>. keyword "JOIN" >>% LeftJoin)
    <|> (keyword "RIGHT" >>. optional (keyword "OUTER") >>. keyword "JOIN" >>% RightJoin)
    <|> (keyword "JOIN" >>% InnerJoin)

/// `CROSS JOIN (table | (SELECT ...) AS alias)` — no `ON` at all; encoded
/// with the always-true `Lit (VInt 1L)` condition so `Executor.applyJoin`
/// can run it through the exact same matching logic as `INNER JOIN` (every
/// pair "matches") instead of a separate Cartesian-product code path.
/// `fromItem` (not `tableRef`) so a derived-table right side parses too —
/// MySQL accepts `CROSS JOIN (SELECT ...) AS alias` the same as it does for
/// `JOIN`/`LEFT JOIN`.
let private crossJoinClause: Parser<Join, unit> =
    attempt (keyword "CROSS" >>. keyword "JOIN" >>. fromItem)
    |>> fun table -> { Kind = CrossJoin; Table = table; On = Lit(VInt 1L); Using = [] }

/// `FROM t1, t2` — MySQL's legacy comma (implicit-join) syntax, still the
/// form plenty of handwritten SQL uses for what an explicit `CROSS JOIN`
/// says today. Desugars into the exact same `CrossJoin` shape
/// `crossJoinClause` already produces, so every consumer that already walks
/// an N-source join list (`Executor.applyJoin`/`runMutationJoin`) lights up
/// for `SELECT`/`UPDATE`/`DELETE` alike with no executor change; like every
/// other `Join`, only a real table can follow the comma, not a derived
/// `(SELECT ...)`.
let private commaJoinClause: Parser<Join, unit> =
    attempt (sym "," >>. tableRef)
    |>> fun table -> { Kind = CrossJoin; Table = FromTable table; On = Lit(VInt 1L); Using = [] }

/// `JOIN ... USING (col, ...)`'s column list — the equi-keys are resolved by
/// name at execution time (`Executor.applyJoin`), so the parser only carries
/// the names. MySQL's `USING (...)` can't be combined with an `ON`, which
/// the grammar below rejects naturally by not consuming an `ON` after it.
let private usingClause: Parser<string list, unit> =
    keyword "USING" >>. between (sym "(") (sym ")") (sepBy1 identifier (sym ","))

/// `[INNER | LEFT [OUTER] | RIGHT [OUTER]] JOIN (table | (SELECT ...) AS
/// alias) {ON expr | USING (cols)}`, plus the `NATURAL` kinds in `joinKind`
/// which take no tail at all — `fromItem` (not `tableRef`) so
/// `JOIN (SELECT ...) AS alias ON ...` (Eloquent's
/// `joinSub`/`leftJoinSub`/`rightJoinSub`) parses; a multi-table
/// `UPDATE`/`DELETE ... JOIN` shares this same grammar but rejects a
/// derived-table target at execution time (see `Executor.applyMutationJoin`),
/// not here — the grammar itself doesn't know which statement kind it's
/// parsing.
let private joinClause: Parser<Join, unit> =
    crossJoinClause
    <|> commaJoinClause
    <|> ((joinKind .>>. fromItem)
         >>= fun (kind, table) ->
             match kind with
             | NaturalJoin
             | NaturalLeftJoin
             | NaturalRightJoin -> preturn { Kind = kind; Table = table; On = Lit(VInt 1L); Using = [] }
             | _ ->
                 (keyword "ON" >>. expr |>> fun onExpr -> { Kind = kind; Table = table; On = onExpr; Using = [] })
                 <|> (usingClause |>> fun cols -> { Kind = kind; Table = table; On = Lit(VInt 1L); Using = cols }))

let private groupByClause: Parser<Expr list, unit> = keyword "GROUP" >>. keyword "BY" >>. sepBy1 expr (sym ",")

let private havingClause: Parser<Expr, unit> = keyword "HAVING" >>. expr

/// `FOR UPDATE` / `FOR SHARE` / `LOCK IN SHARE MODE` — parsed and discarded;
/// see the `Ast.SelectStmt.Locking` doc for why there's nothing else to do
/// with it.
let private lockClause: Parser<unit, unit> =
    (keyword "FOR" >>. (keyword "UPDATE" <|> (keyword "SHARE" >>% ())) >>% ())
    <|> (keyword "LOCK" >>. keyword "IN" >>. keyword "SHARE" >>. keyword "MODE" >>% ())

selectStmtRecordRef.Value <-
    (keyword "SELECT" >>. opt (keyword "DISTINCT") .>>. sepBy1 projection (sym ",")
     .>>. opt (keyword "FROM" >>. fromItem .>>. many joinClause)
     .>>. opt (keyword "WHERE" >>. expr)
     .>>. opt groupByClause
     .>>. opt havingClause
     .>>. opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ","))
     .>>. opt limitClause
     .>>. opt lockClause)
    |>> fun ((((((((distinct, projs), fromAndJoins), where), groupBy), having), orderBy), limitOffset), locking) ->
        let limit, offset = limitOffset |> Option.defaultValue (None, None)
        let from = fromAndJoins |> Option.map fst
        let joins = fromAndJoins |> Option.map snd |> Option.defaultValue []

        { Projections = projs
          Distinct = distinct.IsSome
          From = from
          Joins = joins
          Where = where
          GroupBy = groupBy |> Option.defaultValue []
          Having = having
          OrderBy = orderBy |> Option.defaultValue []
          Limit = limit
          Offset = offset
          Locking = locking.IsSome }

/// A single `SELECT`, or a `UNION`-chained sequence of them
/// (`selectOrUnionBranches`, shared with `derivedTable` — see its doc). Each
/// branch is a full `selectStmtRecord` (so it can itself carry a trailing
/// `ORDER BY`/`LIMIT`/lock clause), and a genuine union-level clause
/// (`unionTailClause`) is tried once at least one `UNION` branch parsed —
/// `combineUnion` picks between the two per branch/clause.
let private selectOrUnionStmt: Parser<Statement, unit> =
    selectOrUnionBranches
    >>= fun (first, rest) ->
        match rest with
        | [] -> preturn (Select(fst first))
        | _ -> unionTailClause |>> fun tail -> combineUnion first rest tail |> Union

/// An assignment target, `col` or `table.col` (Laravel's `touch()` qualifies
/// `updated_at` with the table name even in a single-table `UPDATE`) — the
/// table part only matters once there's more than one table in scope (a
/// multi-table `UPDATE ... JOIN`); a single-table `UPDATE` still parses one
/// the same way it always could, `Executor` just never needs it there.
let private assignment: Parser<Assignment, unit> =
    ((identifier .>>. opt (sym "." >>. identifier)) .>> sym "=") .>>. expr
    |>> function
        | (first, None), value -> { Table = None; Column = first; Value = value }
        | (first, Some col), value -> { Table = Some first; Column = col; Value = value }

/// `[ORDER BY ...] [LIMIT n]`, legal only on a single-table `UPDATE`/`DELETE`
/// (`joins` empty) — matching MySQL's own grammar restriction, a
/// multi-table form simply never attempts to parse this trailing clause, so
/// leftover `ORDER BY`/`LIMIT` tokens after a JOIN'd `UPDATE`/`DELETE` fall
/// through to `Parser.parse`'s top-level `eof` and surface as an ordinary
/// syntax error — the same 1064 real MySQL gives that combination.
let private singleTableOrderLimit (joins: Join list) : Parser<OrderKey list * int option, unit> =
    if joins.IsEmpty then
        (opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ",")) |>> Option.defaultValue [])
        .>>. opt (keyword "LIMIT" >>. limitTok)
    else
        preturn ([], None)

/// `UPDATE t1 [[AS] a] [JOIN ...] SET assignments [WHERE ...] [ORDER BY ...]
/// [LIMIT ...]`.
let private updateStmt: Parser<Statement, unit> =
    (keyword "UPDATE" >>. tableRef .>>. many joinClause .>> keyword "SET" .>>. sepBy1 assignment (sym ","))
    >>= fun ((from, joins), assignments) ->
        opt (keyword "WHERE" >>. expr) .>>. singleTableOrderLimit joins
        |>> fun (where, (orderBy, limit)) ->
            Update
                { From = from
                  Joins = joins
                  Assignments = assignments
                  Where = where
                  OrderBy = orderBy
                  Limit = limit }

/// `DELETE t1[, t2, ...] FROM t1 JOIN t2 ON ... [WHERE ...]` — the
/// multi-table form naming its delete targets before `FROM`.
let private namedTargetsDelete: Parser<string list * TableRef * Join list, unit> =
    attempt (sepBy1 identifier (sym ",") .>> keyword "FROM" .>>. tableRef .>>. many joinClause)
    |>> fun ((targets, from), joins) -> targets, from, joins

/// `DELETE FROM t1[, t2, ...] USING t1 JOIN t2 ON ... [WHERE ...]` — the
/// `USING` multi-table form; the target list (before `USING`) names which
/// of the `USING` join's tables actually lose rows.
let private usingDelete: Parser<string list * TableRef * Join list, unit> =
    attempt (keyword "FROM" >>. sepBy1 identifier (sym ",") .>> keyword "USING" .>>. tableRef .>>. many joinClause)
    |>> fun ((targets, from), joins) -> targets, from, joins

/// `DELETE FROM t1 [WHERE ...] [ORDER BY ...] [LIMIT n]` — single-table;
/// `Targets` is just `t1` itself (by its alias, if it has one).
let private singleTableDeleteHead: Parser<string list * TableRef * Join list, unit> =
    keyword "FROM" >>. tableRef
    |>> fun from -> [ from.Alias |> Option.defaultValue from.Table ], from, []

let private deleteStmt: Parser<Statement, unit> =
    keyword "DELETE" >>. (namedTargetsDelete <|> usingDelete <|> singleTableDeleteHead)
    >>= fun (targets, from, joins) ->
        opt (keyword "WHERE" >>. expr) .>>. singleTableOrderLimit joins
        |>> fun (where, (orderBy, limit)) ->
            Delete
                { Targets = targets
                  From = from
                  Joins = joins
                  Where = where
                  OrderBy = orderBy
                  Limit = limit }

/// `EXPLAIN [FORMAT=TRADITIONAL] stmt` — MySQL also accepts `DESCRIBE`/
/// `DESC` as synonyms when just describing a table's columns (not a
/// statement), out of scope here; this only covers the `EXPLAIN stmt` form.
let private explainStmt: Parser<Statement, unit> =
    keyword "EXPLAIN"
    >>. optional (attempt (keyword "FORMAT" >>. sym "=" >>. keyword "TRADITIONAL"))
    >>. statement
    |>> Explain

/// `CREATE TABLE` vs. `CREATE INDEX` and `DROP TABLE` vs. `DROP INDEX` share
/// a leading keyword before diverging, so those four need `attempt` to
/// backtrack cleanly between alternatives; every other statement starts on
/// a keyword none of the others do, so `choice` picks the right one off
/// just that first token without needing to backtrack at all.
statementRef.Value <-
    choice
        [ attempt createDatabaseStmt
          attempt createTable
          attempt createIndexStmt
          attempt dropDatabaseStmt
          attempt dropTable
          dropIndexStmt
          truncateTable
          insertStmt
          selectOrUnionStmt
          updateStmt
          deleteStmt
          attempt alterTableStmt
          alterDatabaseStmt
          renameTableStmt
          explainStmt ]
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
