/// FParsec-based SQL parser: raw text in, `Ast.Statement` out.
///
/// Grammar is built from small named parsers composed with combinators.
/// Expression precedence is split across two layers. Numeric and bitwise
/// operators use an `OperatorPrecedenceParser`; the boolean layer (`OR`,
/// `AND`, `NOT`, comparisons, `LIKE`/`IN`/`BETWEEN`) is hand-written because
/// those forms need extra keywords and sub-parses (`BETWEEN lo AND hi`,
/// `IN (list)`) that do not fit the OPP's operator shape.
module Fsdb.Parser

open System
open System.Collections.Generic
open System.Globalization
open FParsec
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Temporal

/// The supported `LOAD DATA LOCAL INFILE` options, separated from `Statement`
/// because the data stream arrives after the server has parsed the command.
type LocalLoad =
    { FileName: string
      Table: string
      Replace: bool
      Ignore: bool
      Charset: string option
      FieldTerminator: string
      EnclosedBy: string option
      Escape: string option
      LineTerminator: string
      IgnoreLines: int
      Columns: string list }

// ---------------------------------------------------------------------------
// Whitespace, comments, tokens
// ---------------------------------------------------------------------------

/// MySQL only treats `--` as the start of a comment when followed by
/// whitespace or end of input — `SELECT 1--1` is subtraction (`0`), not
/// `1` with the rest commented out. `attempt` backtracks over the whole
/// `--` when the lookahead fails, rather than leaving `--` consumed and
/// turning a plain `1--1` into a hard parse error: `ws` tries this inside a
/// `choice`, which only tries the next alternative when a branch fails
/// *without* consuming input.
let private lineComment: Parser<unit, unit> =
    attempt (pstring "--" .>> followedBy (skipSatisfy Char.IsWhiteSpace <|> eof))
    >>. skipManyTill anyChar (skipNewline <|> eof)

let private blockComment: Parser<unit, unit> = pstring "/*" >>. skipManyTill anyChar (pstring "*/" >>% ())

/// Whitespace and comments, skipped after every token so parsers never have
/// to think about trailing space.
let private ws: Parser<unit, unit> = skipMany (choice [ spaces1; lineComment; blockComment ])

/// The numeric form of `Protocol.ServerVersion` ("8.4.0-fsdb"), for
/// `stripVersionComments` below. Duplicated rather than shared because
/// `Protocol.fs` compiles after this file — keep the two in sync by hand if
/// `ServerVersion` ever changes.
let private serverVersionNumber = 80400

/// mysqldump wraps version-specific SQL in `/*!NNNNN ... */` (or a bare
/// `/*! ... */` for "any version") so one dump can target several server
/// versions at once: MySQL's grammar runs the wrapped SQL as ordinary
/// executable SQL when the server's version is >= NNNNN, and treats it as
/// an inert comment otherwise. `blockComment` above only ever does the
/// second half (skip it); this rewrites the SQL text ahead of parsing so
/// the first half (splice it back in) doesn't need its own grammar rule.
/// Not recursive — mysqldump never nests these.
///
/// Ordinary comments are stripped in the same pass — `# ...`-to-EOL,
/// `-- `-to-EOL (MySQL requires whitespace/EOL after the `--`, `5--3` is
/// arithmetic), and plain `/* ... */` — because `QueryHandler`'s text
/// probes (SET/SHOW/...) match on this normalized text, and a dump-import
/// client (TablePlus) ships each statement with its surrounding comment
/// banner attached rather than stripping client-side like the mysql CLI.
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
        | None when sql.[i] = '#' ->
            // `# ...` comment: to end of line.
            let eol = sql.IndexOf('\n', i)
            sb.Append ' ' |> ignore
            i <- (if eol = -1 then sql.Length else eol)
        | None when
            sql.[i] = '-'
            && i + 1 < sql.Length
            && sql.[i + 1] = '-'
            && (i + 2 = sql.Length || Char.IsWhiteSpace sql.[i + 2])
            ->
            // `-- ` comment: to end of line.
            let eol = sql.IndexOf('\n', i)
            sb.Append ' ' |> ignore
            i <- (if eol = -1 then sql.Length else eol)
        | None when
            i + 2 < sql.Length
            && sql.[i] = '/'
            && sql.[i + 1] = '*'
            && sql.[i + 2] <> '!'
            ->
            // Plain `/* ... */` comment: replaced by one space so it can't
            // glue two tokens together.
            let closeAt = sql.IndexOf("*/", i + 2)

            if closeAt = -1 then
                sb.Append(sql.Substring i) |> ignore
                i <- sql.Length
            else
                sb.Append ' ' |> ignore
                i <- closeAt + 2
        | None when i + 2 < sql.Length && sql.[i] = '/' && sql.[i + 1] = '*' && sql.[i + 2] = '!' ->
            match sql.IndexOf("*/", i + 3) with
            | -1 ->
                sb.Append(sql.Substring i) |> ignore
                i <- sql.Length
            | closeAt ->
                let inner = sql.Substring(i + 3, closeAt - (i + 3))
                let leadingDigits = inner |> Seq.takeWhile Char.IsDigit |> Seq.length

                let versionLength =
                    if
                        leadingDigits >= 6
                        && (inner.Length = 6 || Char.IsWhiteSpace inner.[6])
                    then
                        6
                    elif leadingDigits >= 5 then
                        5
                    else
                        0

                if leadingDigits = 0 then
                    sb.Append(inner: string) |> ignore
                elif versionLength > 0 then
                    let version = Int32.Parse(inner.Substring(0, versionLength), Globalization.CultureInfo.InvariantCulture)

                    if version <= serverVersionNumber then
                        sb.Append(inner.Substring(versionLength): string) |> ignore
                    else
                        sb.Append ' ' |> ignore
                else
                    sb.Append ' ' |> ignore

                i <- closeAt + 2
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

let private functionKeyword (s: string) : Parser<unit, unit> =
    attempt (pstringCI s >>. nextCharSatisfiesNot isIdentChar >>. pchar '(') >>. ws <?> s

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
          "use"
          "force"
          "ignore"
          "constraint"
          "foreign"
          "references"
          "regexp"
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
          // Reserved in MySQL 8.4 too, and the grammar needs them: an
          // unparenthesized branch's `FROM t INTERSECT ...` would otherwise
          // read the operator as table `t`'s alias, exactly as `union` would.
          "intersect"
          "except"
          "xor"
          "all"
          "any"
          "some"
          "when"
          "for"
          // Reserved in MySQL 8 too: without it, `FROM t WINDOW w AS (...)`
          // reads `WINDOW` as `t`'s alias and dies on the window name.
          "window"
          "lock"
          "with"
          "recursive" ],
        StringComparer.OrdinalIgnoreCase
    )

let private charsetIntroducerNames =
    System.Collections.Generic.HashSet<string>(
        [ "_armscii8"; "_ascii"; "_big5"; "_binary"; "_cp1250"; "_cp1251"; "_cp1256"; "_cp1257"
          "_cp850"; "_cp852"; "_cp866"; "_cp932"; "_dec8"; "_eucjpms"; "_euckr"; "_gb18030"; "_gb2312"
          "_gbk"; "_geostd8"; "_greek"; "_hebrew"; "_hp8"; "_keybcs2"; "_koi8r"; "_koi8u"; "_latin1"
          "_latin2"; "_latin5"; "_latin7"; "_macce"; "_macroman"; "_sjis"; "_swe7"; "_tis620"; "_ucs2"
          "_ujis"; "_utf16"; "_utf16le"; "_utf32"; "_utf8"; "_utf8mb3"; "_utf8mb4" ],
        StringComparer.OrdinalIgnoreCase
    )

let private bareIdent: Parser<string, unit> =
    many1Satisfy2 isIdentStart isIdentChar
    >>= fun w ->
        if reservedWords.Contains w || charsetIntroducerNames.Contains w then
            fail (sprintf "'%s' is a reserved keyword" w)
        else
            preturn w

/// Backtick quoting, with `` `` `` as the escape for a literal backtick.
let private backtickChar: Parser<char, unit> = (pstring "``" >>% '`') <|> satisfy (fun c -> c <> '`')

let private backtickIdent: Parser<string, unit> = pchar '`' >>. manyChars backtickChar .>> pchar '`'

let private identifier: Parser<string, unit> =
    (backtickIdent <|> attempt bareIdent) .>> ws <?> "identifier"

let private qualifiedIdentifier: Parser<string, unit> =
    (backtickIdent <|> many1Satisfy2 isIdentStart isIdentChar) .>> ws

/// `[db.]table` — like `tableRef` below but with no alias, for statements
/// that target exactly one table rather than projecting columns (DDL,
/// INSERT/UPDATE/DELETE/TRUNCATE). Encoded as a single "db.table" string
/// rather than widening every `Ast.Statement` table field to a record —
/// `Storage.splitQualified` peels it back apart right before resolving
/// against `Storage`, which already takes database and table name as two
/// separate arguments everywhere.
let private qualifiedTableName: Parser<string, unit> =
    (identifier .>>. opt (sym "." >>. qualifiedIdentifier))
    |>> function
        | first, Some second -> first + "." + second
        | first, None -> first

// ---------------------------------------------------------------------------
// Literals
// ---------------------------------------------------------------------------

let private numberFormat =
    NumberLiteralOptions.AllowFraction
    ||| NumberLiteralOptions.AllowExponent
    ||| NumberLiteralOptions.AllowHexadecimal

/// Plain integers become `VInt`, exponent notation becomes `VDouble`, and
/// everything else with a decimal point stays exact as `VDecimal` — an
/// integers beyond BIGINT remain exact as `VDecimal` while possible; a
/// literal beyond `decimal`'s range falls back to `VDouble` instead of
/// throwing an unguarded overflow exception out of the parser.
///
/// `0x..` hex literals become `VBytes` — MySQL treats them as binary strings
/// by default (only numeric *context*, e.g. `0x41 + 1`, coerces to a number,
/// which fsdb doesn't model), so `0x41 = 'A'` compares equal the same way it
/// does against a real server. `AllowHexadecimal` above is what makes
/// `0x41` a single token here rather than the number `0` followed by a
/// bare identifier `x41`.
let private numberLit: Parser<Value, unit> =
    (numberLiteral numberFormat "number" .>> ws)
    |>> fun nl ->
        if nl.IsHexadecimal then
            let digits = nl.String.Substring(2)
            let digits = if digits.Length % 2 = 1 then "0" + digits else digits

            Array.init (digits.Length / 2) (fun i -> Convert.ToByte(digits.Substring(i * 2, 2), 16))
            |> VBytes
        elif nl.IsInteger then
            match Int64.TryParse(nl.String, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, i -> VInt i
            // MySQL types an integer literal past BIGINT's signed range but
            // inside its unsigned one as BIGINT UNSIGNED, exactly:
            // `SELECT 18446744073709551615` echoes all twenty digits back
            // rather than the nearest double.
            | false, _ ->
                match UInt64.TryParse(nl.String, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, u -> VUInt u
                | false, _ ->
                    match Decimal.TryParse(nl.String, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, d -> VDecimal d
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

/// MySQL's `_charset'text'` introducer — labels the literal's
/// client-encoded (hence UTF-8) bytes with the named charset *without*
/// converting them, verified against 8.4: `_latin1'é'` reads back as the
/// two cp1252 chars of é's UTF-8 bytes (`Ã©`), `_ascii'å'` as `??` (one
/// '?' per byte), and `_binary'abc'` compares byte-wise. Desugared at parse
/// time into the final `Lit` — the common ASCII-subset cases are identical
/// to a real conversion.
let private introducedStringLit: Parser<Expr, unit> =
    let introducer =
        attempt (
            pchar '_'
            >>. many1Chars (satisfy isIdentChar)
            .>> ws
            .>> followedBy (anyOf "'\"")
        )

    introducer
    .>>. stringLit
    >>= fun (charset, v) ->
        let text =
            match v with
            | VString s -> s
            | _ -> ""

        let bytes = Text.Encoding.UTF8.GetBytes text

        match charset.ToLowerInvariant() with
        | "utf8mb4"
        | "utf8" -> preturn (Lit(VString text))
        | "binary" -> preturn (Lit(VBytes bytes))
        | "latin1" -> preturn (Lit(VString(Collation.Charset.decodeLatin1Bytes bytes)))
        | "ascii" -> preturn (Lit(VString(Collation.Charset.decodeAsciiBytes bytes)))
        | _ -> fail (sprintf "Unknown character set: '%s'" charset)

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

let private bytesOfBits (digits: string) : byte[] =
    if digits.Length = 0 then
        [||]
    else
        let byteCount = (digits.Length + 7) / 8
        let padding = byteCount * 8 - digits.Length
        let bytes = Array.zeroCreate<byte> byteCount

        digits
        |> Seq.iteri (fun index digit ->
            if digit = '1' then
                let bit = index + padding
                bytes.[bit / 8] <- bytes.[bit / 8] ||| (1uy <<< (7 - bit % 8)))

        bytes

let private bitBytesLit: Parser<Value, unit> =
    let quoted =
        attempt (pstringCI "B" .>> pchar '\'')
        >>. manyChars (anyOf "01")
        .>> pchar '\''

    let unquoted = attempt (pstringCI "0b") >>. many1Chars (anyOf "01")

    (quoted <|> unquoted) .>> ws |>> (bytesOfBits >> VBytes)

let private nationalStringLit: Parser<Value, unit> =
    attempt (pstringCI "N" .>> followedBy (pchar '\'')) >>. stringLit

let private literalValue: Parser<Value, unit> =
    choice
        [ bitBytesLit
          numberLit
          hexBytesLit
          nationalStringLit
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

/// As `ignoredWidth`, but keeping the number: `TINYINT`'s display width is
/// the one that carries meaning, since `TINYINT(1)` is `BOOLEAN` (see
/// `Ast.TBool`).
let private tinyIntWidth: Parser<int option, unit> =
    opt (between (sym "(") (sym ")") intTok)

/// `UNSIGNED` (and MySQL's deprecated `ZEROFILL`, which implies it) after
/// any numeric type — carried on the int types, accepted-and-discarded on
/// float/double/decimal since `Ast.ColumnType` doesn't track it there.
let private unsignedFlag: Parser<bool, unit> = opt (keyword "UNSIGNED") |>> Option.isSome

let private widthLen: Parser<int, unit> = between (sym "(") (sym ")") intTok
let private optWidthLen: Parser<int option, unit> = opt widthLen
let private positiveWidthLen: Parser<int, unit> = widthLen >>= fun n -> if n > 0 then preturn n else fail "a WEIGHT_STRING width must be positive"

/// The fractional-seconds precision on a temporal type — `DATETIME(6)` →
/// `6`, a bare `DATETIME` → `0`. Any non-negative int parses here; the
/// `fsp > 6` rejection (MySQL error 1426, which names the column) happens at
/// DDL time in `Storage`, where the column name is in scope — not here,
/// where only the type is.
let private optFsp: Parser<int, unit> = opt widthLen |>> Option.defaultValue 0

let private stringListParen: Parser<string list, unit> =
    between (sym "(") (sym ")") (sepBy1 (stringLit |>> (function VString s -> s | _ -> "")) (sym ","))

let private columnType: Parser<ColumnType, unit> =
    choice
        [ keyword "TINYINT" >>. tinyIntWidth .>>. unsignedFlag
          |>> (fun (width, unsigned) ->
              // `TINYINT(1)` is what `BOOLEAN` expands to, and clients read
              // the width to tell them apart. Signed only: `TINYINT(1)
              // UNSIGNED` keeps the integer reading, as MySQL's own clients
              // do.
              if width = Some 1 && not unsigned then TBool else TTinyInt unsigned)
          keyword "SMALLINT" >>. ignoredWidth >>. unsignedFlag |>> TSmallInt
          keyword "MEDIUMINT" >>. ignoredWidth >>. unsignedFlag |>> TMediumInt
          keyword "BIGINT" >>. ignoredWidth >>. unsignedFlag |>> TBigInt
          (keyword "INT" <|> keyword "INTEGER") >>. ignoredWidth >>. unsignedFlag |>> TInt
          keyword "BIT" >>. optWidthLen |>> (fun width -> TBit(defaultArg width 1))
          keyword "VARCHAR" >>. widthLen |>> TVarchar
          keyword "CHAR" >>. optWidthLen |>> (fun n -> TChar(defaultArg n 1))
          keyword "TINYTEXT" >>% TTinyText
          keyword "MEDIUMTEXT" >>% TMediumText
          keyword "LONGTEXT" >>% TLongText
          // `TEXT(n)`/`BLOB(n)` pick the smallest family member that holds
          // n — measured in CHARACTERS for TEXT (×4 bytes under utf8mb4:
          // TEXT(64) is already a plain TEXT) and bytes for BLOB
          // (oracle-verified boundaries: 63/16383 vs 255/65535).
          keyword "TEXT" >>. opt (attempt widthLen)
          |>> (function
              | Some n when n <= 63 -> TTinyText
              | Some n when n > 16383 && n <= 4194303 -> TMediumText
              | Some n when n > 4194303 -> TLongText
              | _ -> TText)
          keyword "VARBINARY" >>. widthLen |>> TVarBinary
          keyword "BINARY" >>. optWidthLen |>> (fun n -> TBinary(defaultArg n 1))
          keyword "TINYBLOB" >>% TTinyBlob
          keyword "MEDIUMBLOB" >>% TMediumBlob
          keyword "LONGBLOB" >>% TLongBlob
          keyword "BLOB" >>. opt (attempt widthLen)
          |>> (function
              | Some n when n <= 255 -> TTinyBlob
              | Some n when n > 65535 && n <= 16777215 -> TMediumBlob
              | Some n when n > 16777215 -> TLongBlob
              | _ -> TBlob)
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
          keyword "DATETIME" >>. optFsp |>> TDateTime
          keyword "TIMESTAMP" >>. optFsp |>> TTimestamp
          keyword "DATE" >>% TDate
          keyword "TIME" >>. optFsp |>> TTime
          keyword "YEAR" >>. ignoredWidth >>% TYear
          keyword "JSON" >>% TJson
          keyword "GEOMETRY" >>% TGeometry Geometry
          keyword "POINT" >>% TGeometry Point
          keyword "LINESTRING" >>% TGeometry LineString
          keyword "POLYGON" >>% TGeometry Polygon
          keyword "MULTIPOINT" >>% TGeometry MultiPoint
          keyword "MULTILINESTRING" >>% TGeometry MultiLineString
          keyword "MULTIPOLYGON" >>% TGeometry MultiPolygon
          (keyword "GEOMETRYCOLLECTION" <|> keyword "GEOMCOLLECTION") >>% TGeometry GeometryCollection
          // Bare `VECTOR` is MySQL 9's default dimension 2048; `VECTOR()` is
          // a syntax error there too — `attempt widthLen` backtracks off the
          // empty parens, which then fail the rest of the column grammar.
          keyword "VECTOR" >>. opt (attempt widthLen) |>> (fun n -> TVector(defaultArg n 2048))
          (keyword "BOOLEAN" <|> keyword "BOOL") >>% TBool ]
    <?> "column type"

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

/// FParsec recurses on the real call stack with no depth check of its own —
/// `((((...1000s deep...))))`, `NOT NOT NOT ...`, or nested subqueries/CASE
/// would otherwise blow the stack with an uncatchable `StackOverflowException`
/// that kills the whole process instead of a clean syntax error. `AsyncLocal`
/// so concurrent connections parsing at the same time don't share a counter —
/// same pattern as `placeholderCounterLocal` below. `expr` and `notExpr` are
/// wrapped with this (see their definitions) since every parenthesized
/// expression, subquery, `CASE`, and `NOT` chain recurses back through one of
/// the two.
let private exprDepth = System.Threading.AsyncLocal<int>()
let private maxExprDepth = 32

let private exceedsParenthesisDepthLimit (sql: string) =
    let mutable index = 0
    let mutable depth = 0
    let mutable quote: char option = None
    let mutable blockComment = false
    let mutable lineComment = false
    let mutable exceeded = false

    while index < sql.Length && not exceeded do
        match quote with
        | Some q when sql.[index] = '\\' && q <> '`' && index + 1 < sql.Length -> index <- index + 2
        | Some q when sql.[index] = q && index + 1 < sql.Length && sql.[index + 1] = q -> index <- index + 2
        | Some q when sql.[index] = q ->
            quote <- None
            index <- index + 1
        | Some _ -> index <- index + 1
        | None when blockComment && sql.[index] = '*' && index + 1 < sql.Length && sql.[index + 1] = '/' ->
            blockComment <- false
            index <- index + 2
        | None when blockComment -> index <- index + 1
        | None when lineComment && (sql.[index] = '\n' || sql.[index] = '\r') ->
            lineComment <- false
            index <- index + 1
        | None when lineComment -> index <- index + 1
        | None when sql.[index] = '\'' || sql.[index] = '"' || sql.[index] = '`' ->
            quote <- Some sql.[index]
            index <- index + 1
        | None when sql.[index] = '#' ->
            lineComment <- true
            index <- index + 1
        | None when
            sql.[index] = '-'
            && index + 1 < sql.Length
            && sql.[index + 1] = '-'
            && (index + 2 = sql.Length || Char.IsWhiteSpace sql.[index + 2])
            ->
            lineComment <- true
            index <- index + 2
        | None when sql.[index] = '/' && index + 1 < sql.Length && sql.[index + 1] = '*' ->
            blockComment <- true
            index <- index + 2
        | None when sql.[index] = '(' ->
            depth <- depth + 1
            exceeded <- depth > maxExprDepth
            index <- index + 1
        | None when sql.[index] = ')' ->
            depth <- max 0 (depth - 1)
            index <- index + 1
        | None -> index <- index + 1

    exceeded

let private depthGuard (p: Parser<'a, unit>) : Parser<'a, unit> =
    fun stream ->
        if exprDepth.Value >= maxExprDepth then
            (fail "expression nested too deeply") stream
        else
            exprDepth.Value <- exprDepth.Value + 1

            let reply = p stream
            exprDepth.Value <- exprDepth.Value - 1
            reply

// Parenthesized expressions, function-call arguments and `IN (...)` lists all recurse back
// into the full expression grammar, which is itself built on top of them —
// tie the knot with a forward reference.
let private expr, exprRef = createParserForwardedToRef<Expr, unit> ()

/// `SELECT`'s own clauses recurse into `expr` (projections, `WHERE`, ...),
/// and `expr`'s `Exists` case recurses back into a `SELECT` — tie that knot
/// the same way `expr` ties its own, with the real definition assigned down
/// by `selectStmt` once `tableRef`/`projection`/etc. exist.
let private selectStmtRecord, selectStmtRecordRef = createParserForwardedToRef<SelectStmt, unit> ()

let private selectWithCtes, selectWithCtesRef = createParserForwardedToRef<SelectStmt, unit> ()

let private selectQuery, selectQueryRef = createParserForwardedToRef<SelectOrUnion, unit> ()

/// `EXPLAIN stmt` wraps any other statement, including (in principle)
/// another `EXPLAIN` — tie the same forward-reference knot so `explainStmt`
/// (defined well before the full `statement` choice exists) can recurse into
/// it; the real definition is assigned at the bottom of the file, same as
/// `expr`/`selectStmtRecord` above.
let private statement, statementRef = createParserForwardedToRef<Statement, unit> ()

/// A comma distinguishes a row constructor from ordinary grouping, so
/// `(a)` stays the scalar expression `a` while `(a, b)` keeps both operands.
let private parenthesizedExpr: Parser<Expr, unit> =
    between (sym "(") (sym ")") (
        pipe2 expr (opt (sym "," >>. sepBy1 expr (sym ","))) (fun first rest ->
            match rest with
            | None -> first
            | Some values -> Row(first :: values))
    )

let private starAtom: Parser<Expr, unit> = pstring "*" >>. ws >>% Star None

/// MySQL's grammar allows `DISTINCT` inside a call only for these
/// aggregates; every other name — including `JSON_ARRAYAGG` and
/// `JSON_OBJECTAGG`, which have no de-duplicating form at all — is a 1064
/// syntax error, so accepting it anywhere would mean answering where MySQL
/// refuses.
let private distinctAggregates =
    System.Collections.Generic.HashSet<string>([ "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "GROUP_CONCAT" ], System.StringComparer.OrdinalIgnoreCase)

/// `DISTINCT expr` inside a function call's argument list (`COUNT(DISTINCT
/// x)`, `SUM(DISTINCT x)`, ...).
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

/// Carries the modifier as a cast node so stored expressions retain an
/// ordinary AST; `Executor` applies WEIGHT_STRING's own length semantics.
let private weightStringAtom: Parser<Expr, unit> =
    let cast =
        keyword "AS"
        >>. choice
                [ keyword "CHAR" >>. positiveWidthLen |>> TChar
                  keyword "BINARY" >>. positiveWidthLen |>> TBinary ]

    attempt (
        keyword "WEIGHT_STRING"
        >>. between (sym "(") (sym ")") (expr .>>. opt cast)
    )
    |>> fun (value, modifier) ->
        let argument =
            match modifier with
            | Some target -> Cast(value, target)
            | None -> value

        FuncCall("WEIGHT_STRING", [ argument ])

let private rowConstructorAtom: Parser<Expr, unit> =
    keyword "ROW"
    >>. between (sym "(") (sym ")") (sepBy1 expr (sym ","))
    >>= function
        | _ :: _ :: _ as values -> preturn (Row values)
        | _ -> fail "ROW requires at least two expressions"

let private genericFuncCall: Parser<Expr, unit> =
    let reservedNames = set [ "any"; "select"; "some"; "regexp" ]
    let whitespaceSensitiveNames =
        set
            [ "adddate"; "bit_and"; "bit_or"; "bit_xor"; "cast"; "count"; "curdate"; "curtime"
              "date_add"; "date_sub"; "extract"; "group_concat"; "max"; "mid"; "min"; "now"
              "position"; "session_user"; "std"; "stddev"; "stddev_pop"; "stddev_samp"; "subdate"
              "substr"; "substring"; "sum"; "sysdate"; "system_user"; "trim"; "variance"; "var_pop"
              "var_samp" ]

    attempt (
        many1Satisfy2 isIdentStart isIdentChar
        >>= fun name ->
            let normalizedName = name.ToLowerInvariant()

            if reservedNames.Contains normalizedName then
                fail "reserved function name"
            else
                let openParen =
                    if whitespaceSensitiveNames.Contains normalizedName then
                        pchar '(' >>. ws
                    else
                        ws >>. pchar '(' >>. ws

                openParen
                >>. sepBy (if distinctAggregates.Contains name then distinctArg else expr) (sym ",")
                .>> sym ")"
                |>> fun args -> FuncCall(name, args)
    )

let private trimAtom: Parser<Expr, unit> =
    let mode = choice [ keyword "BOTH" >>% "TRIM_BOTH"; keyword "LEADING" >>% "TRIM_LEADING"; keyword "TRAILING" >>% "TRIM_TRAILING" ]

    let specification =
        choice
            [ attempt (
                  mode
                  >>= fun selected ->
                      ((keyword "FROM" >>% Lit(VString " ")) <|> (expr .>> keyword "FROM"))
                      |>> fun removed -> selected, removed
              )
              attempt (keyword "FROM" >>% ("TRIM_BOTH", Lit(VString " ")))
              attempt (expr .>> keyword "FROM" |>> fun removed -> "TRIM_BOTH", removed) ]

    attempt (
        functionKeyword "TRIM"
        >>. (specification .>>. expr)
        .>> sym ")"
        |>> fun ((mode, removed), source) -> FuncCall(mode, [ removed; source ])
    )

let private funcCallAtom: Parser<Expr, unit> = choice [ attempt convertUsingAtom; attempt weightStringAtom; trimAtom; rowConstructorAtom; genericFuncCall ]

/// `GROUP_CONCAT([DISTINCT] expr [ORDER BY key [ASC|DESC], ...] [SEPARATOR
/// 'str'])` — parsed separately from `funcCallAtom` rather than folding
/// `ORDER BY`/`SEPARATOR` into the general call-argument grammar, since it's
/// the one built-in whose argument list isn't just a comma-separated
/// expression list. Each `ORDER BY` key becomes an `OrderBy` marker in the
/// trailing argument list (see its doc) so `Ast.FuncCall` doesn't need its
/// own order-key vocabulary just for this one call; `Executor.evalAggregate`
/// picks the markers back out.
let private groupConcatAtom: Parser<Expr, unit> =
    attempt (functionKeyword "GROUP_CONCAT")
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

/// The `PARTITION BY expr, ... ORDER BY expr [ASC|DESC], ... [frame]` body
/// of an `OVER (...)`, also reused verbatim by the `WINDOW w AS (...)`
/// clause. Written out here rather than reusing the later `orderKey` parser
/// (which needs `Asc`/`Desc`'s default already applied) since `orderKey`
/// isn't defined until after `atom`; duplicating its two-line
/// direction-defaulting logic is cheaper than reordering the file to hoist
/// it.
let internal windowFrameBound: Parser<FrameBound, unit> =
    choice
        [ attempt (keyword "UNBOUNDED" >>. keyword "PRECEDING") >>% UnboundedPreceding
          attempt (keyword "UNBOUNDED" >>. keyword "FOLLOWING") >>% UnboundedFollowing
          attempt (keyword "CURRENT" >>. keyword "ROW") >>% CurrentRow
          // MySQL's grammar takes only an unsigned literal/parameter here,
          // so a negative offset is its 1064, not a runtime error.
          expr
          .>>. ((keyword "PRECEDING" >>% true) <|> (keyword "FOLLOWING" >>% false))
          >>= fun (offset, preceding) ->
              match offset with
              | Lit(VInt n) when n < 0L -> fail "a window frame offset cannot be negative"
              | Lit VNull -> fail "a window frame offset cannot be NULL"
              | _ -> preturn (if preceding then BoundPreceding offset else BoundFollowing offset) ]

/// `ROWS|RANGE BETWEEN a AND b`, or the shorthand `ROWS|RANGE a` — which
/// MySQL defines as `BETWEEN a AND CURRENT ROW`.
let internal windowFrameClause: Parser<WindowFrame, unit> =
    ((keyword "ROWS" >>% FrameRows) <|> (keyword "RANGE" >>% FrameRange))
    .>>. ((attempt (keyword "BETWEEN" >>. windowFrameBound .>> keyword "AND") .>>. windowFrameBound)
          <|> (windowFrameBound |>> fun b -> b, CurrentRow))
    |>> fun (unit', (startBound, endBound)) -> { Unit = unit'; Start = startBound; End = endBound }

let private inheritedWindowName: Parser<string, unit> =
    notFollowedBy (choice [ keyword "PARTITION"; keyword "ORDER"; keyword "ROWS"; keyword "RANGE" ])
    >>. identifier

let internal windowSpecBody: Parser<WindowSpec, unit> =
    opt (attempt inheritedWindowName)
    .>>. opt (keyword "PARTITION" >>. keyword "BY" >>. sepBy1 expr (sym ","))
    .>>. opt (
        keyword "ORDER" >>. keyword "BY"
        >>. sepBy1 (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc))) (sym ",")
    )
    .>>. opt windowFrameClause
    |>> fun (((inheritName, partitionBy), orderBy), frame) ->
        { Inherit = inheritName
          PartitionBy = partitionBy |> Option.defaultValue []
          OrderBy =
            orderBy
            |> Option.defaultValue []
            |> List.map (fun (e, dir) -> e, dir |> Option.defaultValue Asc)
          Frame = frame }

/// `OVER (...)` or `OVER window_name` — inherited and named forms resolve at
/// execution time (see `Ast.OverClause`).
let private overClause: Parser<OverClause, unit> =
    keyword "OVER"
    >>. ((between (sym "(") (sym ")") windowSpecBody |>> OverSpec) <|> (identifier |>> OverName))

/// The function names that may carry an `OVER` clause: the window-only
/// family plus the aggregates MySQL also allows as window functions. Any
/// other name followed by `OVER` is a 1064, same as MySQL.
let private windowFunctionNames =
    System.Collections.Generic.HashSet<string>(
        [ "ROW_NUMBER"; "RANK"; "DENSE_RANK"; "PERCENT_RANK"; "CUME_DIST"; "NTILE"
          "LAG"; "LEAD"; "FIRST_VALUE"; "LAST_VALUE"; "NTH_VALUE"
          "SUM"; "COUNT"; "AVG"; "MIN"; "MAX"; "GROUP_CONCAT"; "BIT_AND"; "BIT_OR"; "BIT_XOR"
          "STD"; "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"; "VARIANCE"; "VAR_POP"; "VAR_SAMP"
          "JSON_ARRAYAGG" ],
        System.StringComparer.OrdinalIgnoreCase
    )

/// Maps a parsed `name(args)` head onto its `WindowFn`, rejecting a wrong
/// argument count the way MySQL's own grammar/1582 does.
let private windowFnOf (name: string) (args: Expr list) : Choice<WindowFn, string> =
    let ok = Choice1Of2
    let wrongCount () =
        Choice2Of2(sprintf "Incorrect parameter count in the call to native function '%s'" (name.ToUpperInvariant()))

    match name.ToUpperInvariant(), args with
    | "ROW_NUMBER", [] -> ok WinRowNumber
    | "RANK", [] -> ok (WinRank false)
    | "DENSE_RANK", [] -> ok (WinRank true)
    | "PERCENT_RANK", [] -> ok WinPercentRank
    | "CUME_DIST", [] -> ok WinCumeDist
    | "NTILE", [ n ] -> ok (WinNTile n)
    | ("LAG" | "LEAD"), (_ :: offset :: _) when (match offset with Lit(VInt n) -> n < 0L | _ -> false) ->
        // Same unsigned-literal-only grammar rule as a frame offset above.
        Choice2Of2 "a LAG/LEAD offset cannot be negative"
    | ("LAG" | "LEAD"), (value :: rest) when List.length rest <= 2 ->
        let lead = System.String.Equals(name, "LEAD", System.StringComparison.OrdinalIgnoreCase)
        ok (WinLagLead(lead, value, List.tryItem 0 rest, List.tryItem 1 rest))
    | "FIRST_VALUE", [ e ] -> ok (WinFirstValue e)
    | "LAST_VALUE", [ e ] -> ok (WinLastValue e)
    | "NTH_VALUE", [ e; n ] -> ok (WinNthValue(e, n))
    | _, args -> ok (WinAggregate(name, args))
    | _ -> wrongCount ()

/// `fn(args) OVER (...)` — one rule for every window function (see
/// `Ast.WindowFn`). The `followedBy` keeps a plain aggregate call
/// (`SUM(x)` with no `OVER`) on the ordinary `funcCallAtom` path.
let private windowCallAtom: Parser<Expr, unit> =
    attempt (
        (many1Satisfy2 isIdentStart isIdentChar .>> ws)
        >>= fun name ->
            if not (windowFunctionNames.Contains name) then
                fail "not a window function"
            else
                between (sym "(") (sym ")") (sepBy (if distinctAggregates.Contains name then distinctArg else expr) (sym ","))
                .>> followedByL (keyword "OVER") "OVER"
                |>> fun args -> name, args
    )
    .>>. overClause
    >>= fun ((name, args), over) ->
        match windowFnOf name args with
        | Choice1Of2 fn -> preturn (WindowOver(fn, over))
        | Choice2Of2 message -> fail message

/// `CAST(expr AS type)` — `SIGNED`/`UNSIGNED [INTEGER]` are only valid as a
/// cast target, not a column type, so they're handled here rather than in
/// `columnType`.
let private castTargetType: Parser<ColumnType, unit> =
    choice
        [ attempt (keyword "SIGNED" >>. optional (keyword "INTEGER")) >>% TBigInt false
          // `UNSIGNED` names the 64-bit unsigned domain, not `INT UNSIGNED`:
          // `CAST(-1 AS UNSIGNED)` is 18446744073709551615, and
          // `CAST(x AS SIGNED)` likewise round-trips the full BIGINT range.
          attempt (keyword "UNSIGNED" >>. optional (keyword "INTEGER")) >>% TBigInt true
          columnType ]

/// `CAST(x AS CHAR CHARACTER SET cs)` — strings are Unicode internally, so
/// the charset is parsed and dropped, except `binary`, which MySQL defines
/// as equivalent to `CAST(x AS BINARY)` and lands as a `binary` collation
/// tag so comparisons turn byte-wise.
let private castCharsetClause: Parser<string, unit> =
    (keyword "CHARACTER" >>. keyword "SET" >>. identifier) <|> (keyword "CHARSET" >>. identifier)

let private castExpr: Parser<Expr, unit> =
    attempt (
        functionKeyword "CAST" >>. expr .>> keyword "AS" .>>. castTargetType
        .>>. opt castCharsetClause
        .>> sym ")"
    )
    |>> fun ((e, target), charset) ->
        let cast = Cast(e, target)

        match charset with
        | Some cs when cs.ToLowerInvariant() = "binary" -> Collate(cast, "binary")
        | _ -> cast

let private existsExpr: Parser<Expr, unit> =
    attempt (keyword "EXISTS" >>. sym "(" >>. selectWithCtes .>> sym ")") |>> Exists

/// `(SELECT ...)` used as a value — tried with `attempt` ahead of parenthesized expressions
/// since both start with `(`; a plain parenthesized expression never starts
/// with `SELECT` or `WITH`, so the two never compete once `selectWithCtes`
/// commits.
let private subqueryExpr: Parser<Expr, unit> =
    attempt (sym "(" >>. selectWithCtes .>> sym ")") |>> Subquery

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

let private getFormatAtom: Parser<Expr, unit> =
    attempt (
        keyword "GET_FORMAT" >>. sym "("
        >>. ((keyword "DATE" >>% "DATE") <|> (keyword "DATETIME" >>% "DATETIME") <|> (keyword "TIME" >>% "TIME"))
        .>> sym ","
        .>>. expr
        .>> sym ")"
    )
    |>> fun (kind, locale) -> FuncCall("GET_FORMAT", [ Lit(VString kind); locale ])

/// `EXTRACT(unit FROM expr)` — the unit is an unquoted keyword and the
/// separator is `FROM`, not a comma, so the generic call grammar can't reach
/// it; same splice-the-unit-in-as-argument-one trick as `timestampFuncAtom`.
let private extractAtom: Parser<Expr, unit> =
    attempt (functionKeyword "EXTRACT" >>. (many1Satisfy2 isIdentStart isIdentChar .>> ws) .>> keyword "FROM")
    .>>. expr
    .>> sym ")"
    |>> fun (unit, e) -> FuncCall("EXTRACT", [ Lit(VString(unit.ToUpperInvariant())); e ])

/// MySQL's temporal-literal grammar, which is *not* .NET's date parser:
/// year always comes first, any single punctuation character delimits
/// (`2020.1.2`, `2020@01@02`), and a delimiter-free run of 6/8 (date) or
/// 12/14 (datetime) digits is equally legal. Letters, spaces inside the
/// delimiter, and a fourth component are all rejected. Oracle-pinned against
/// 8.4.11, which answers 1525 for `'01/02/2020'` and `'Jan 5, 2020'` that
/// `DateOnly.TryParse` would happily (and wrongly) accept.
module private MySqlTemporal =
    /// A two-digit year is 2000s below 70, 1900s at or above it (MySQL's
    /// fixed pivot, not a sliding window).
    let private expandYear (y: int) (width: int) =
        if width > 2 then y
        elif y < 70 then 2000 + y
        else 1900 + y

    /// Splits on single punctuation delimiters, refusing letters, spaces and
    /// runs of two delimiters. `None` means the shape isn't delimited at all.
    let private delimitedText (expected: int) (s: string) : string[] option =
        let parts = ResizeArray<string>()
        let cur = Text.StringBuilder()
        let mutable ok = s.Length > 0 && Char.IsDigit s.[0]

        for c in s do
            if ok then
                if Char.IsDigit c then
                    if cur.Length = 4 then ok <- false else cur.Append c |> ignore
                elif Char.IsLetter c || Char.IsWhiteSpace c || cur.Length = 0 then
                    ok <- false
                elif parts.Count = expected - 1 then
                    ok <- false
                else
                    parts.Add(cur.ToString())
                    cur.Clear() |> ignore

        if ok then
            if cur.Length = 0 then ok <- false else parts.Add(cur.ToString())

        // No temporal component is wider than four digits, so a longer run is
        // malformed rather than an overflow waiting to happen in `int`.
        if ok && parts.Count = expected then
            Some(parts.ToArray())
        else
            None

    let private delimited (expected: int) (s: string) : int[] option =
        delimitedText expected s |> Option.map (Array.map int)

    let private allDigits (s: string) = s.Length > 0 && s |> Seq.forall Char.IsDigit

    /// The `y-m-d` triple, from either the delimited or the bare-digit shape.
    let private dateParts (s: string) : (int * int * int) option =
        match delimitedText 3 s with
        | Some [| y; m; d |] -> Some(expandYear (int y) y.Length, int m, int d)
        | _ when allDigits s && (s.Length = 6 || s.Length = 8) ->
            let w = s.Length - 4
            Some(expandYear (int (s.Substring(0, w))) w, int (s.Substring(w, 2)), int (s.Substring(w + 2, 2)))
        | _ -> None

    let tryDate (s: string) : DateOnly option =
        match dateParts s with
        | Some(y, m, d) when y >= 1 && y <= 9999 && m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ->
            Some(DateOnly(y, m, d))
        | _ -> None

    /// Splits a trailing `.fraction` off, returning the microsecond count
    /// rounded to six digits plus the declared width; a bare trailing dot
    /// carries no fraction at all (`TIME '10:20:30.'` is `10:20:30`).
    let private splitFraction (s: string) : (string * int64 * int) option =
        match s.IndexOf '.' with
        | -1 -> Some(s, 0L, 0)
        | i ->
            let frac = s.Substring(i + 1)

            if frac = "" then Some(s.Substring(0, i), 0L, 0)
            elif not (allDigits frac) then None
            else
                let width = min frac.Length 6
                // Nine digits is more than enough to decide the sixth's rounding.
                let scaled =
                    Decimal.Parse("0." + frac.Substring(0, min frac.Length 9), CultureInfo.InvariantCulture) * 1000000M

                Some(s.Substring(0, i), int64 (Math.Round(scaled, MidpointRounding.AwayFromZero)), width)

    let tryDateTime (s: string) : DateTime option =
        match splitFraction s with
        | None -> None
        | Some(body, micros, _) ->
            let whole =
                if allDigits body && (body.Length = 12 || body.Length = 14) then
                    let w = body.Length - 10

                    match tryDate (body.Substring(0, w + 4)) with
                    | Some d ->
                        Some(d, int (body.Substring(w + 4, 2)), int (body.Substring(w + 6, 2)), int (body.Substring(w + 8, 2)))
                    | None -> None
                else
                    // Date and time are separated by 'T' or by one or more
                    // spaces; anything else between them is 1525.
                    let split = body.Split([| ' '; 'T'; 't' |], StringSplitOptions.RemoveEmptyEntries)

                    match split with
                    | [| datePart; timePart |] when body.IndexOfAny [| ' '; 'T'; 't' |] = datePart.Length ->
                        match tryDate datePart, delimited 3 timePart with
                        | Some d, Some [| h; mi; sec |] -> Some(d, h, mi, sec)
                        | _ -> None
                    | _ -> None

            match whole with
            | Some(d, h, mi, sec) when h < 24 && mi < 60 && sec < 60 ->
                Some(d.ToDateTime(TimeOnly(0, 0)).AddHours(float h).AddMinutes(float mi).AddSeconds(float sec).AddTicks(micros * 10L))
            | _ -> None

    /// `TIME` has no `Value` case of its own (see `Functions.timeFn`), so it
    /// lands as the `[-]HH:MM:SS[.frac]` text MySQL renders — hours past 24
    /// (up to the 838:59:59 ceiling) and a leading sign included, neither of
    /// which `TimeOnly` can hold. The declared fraction width is preserved,
    /// as MySQL does.
    let tryTime (s: string) : string option =
        let neg = s.StartsWith "-"
        let body = if neg || s.StartsWith "+" then s.Substring 1 else s

        let hms =
            match splitFraction body with
            | None -> None
            | Some(t, micros, width) ->
                let dayPart, clock =
                    match t.IndexOf ' ' with
                    | -1 -> Some 0, t
                    | i ->
                        let d = t.Substring(0, i)
                        let clock = t.Substring(i + 1).TrimStart()
                        // The `D HH:MM[:SS]` form needs a real clock after the
                        // day: `TIME '1 2'` is 1525, not 24:00:02.
                        (if allDigits d && d.Length <= 4 && clock.Contains ":" then Some(int d) else None), clock

                let comps =
                    if clock.Contains ":" then
                        match delimited 3 clock, delimited 2 clock with
                        | Some [| h; mi; sec |], _ -> Some(h, mi, sec)
                        | _, Some [| h; mi |] -> Some(h, mi, 0)
                        | _ -> None
                    elif allDigits clock && clock.Length <= 6 then
                        // A delimiter-free run reads right-to-left: seconds,
                        // then minutes, then hours (`'1005'` is 00:10:05).
                        let pad = clock.PadLeft(6, '0')
                        Some(int (pad.Substring(0, 2)), int (pad.Substring(2, 2)), int (pad.Substring(4, 2)))
                    else
                        None

                match dayPart, comps with
                | Some days, Some(h, mi, sec) when mi < 60 && sec < 60 -> Some(days * 24 + h, mi, sec, micros, width)
                | _ -> None

        match hms with
        | Some(h, mi, sec, micros, width) ->
            // Rounding the fraction up can carry into the seconds, and MySQL
            // then shows the full six digits (10:20:30.999999999 →
            // 10:20:31.000000).
            let carry = micros / 1000000L
            let micros, width = micros % 1000000L, (if carry > 0L then 6 else width)
            let total = int64 h * 3600L + int64 mi * 60L + int64 sec + carry

            if total > 838L * 3600L + 59L * 60L + 59L || (total = 838L * 3600L + 59L * 60L + 59L && micros > 0L) then
                None
            else
                let frac =
                    if width = 0 then
                        ""
                    else
                        sprintf ".%s" ((sprintf "%06d" micros).Substring(0, width))

                Some(
                    sprintf
                        "%s%02d:%02d:%02d%s"
                        (if neg && total > 0L then "-" else "")
                        (total / 3600L)
                        (total % 3600L / 60L)
                        (total % 60L)
                        frac
                )
        | None -> None

/// `DATE 'text'` / `TIME 'text'` / `TIMESTAMP 'text'` — SQL's typed temporal
/// literals. Folded to a `Lit` here rather than desugared to the same-named
/// function call, because MySQL *rejects* a malformed one (1525 "Incorrect
/// DATE value") where `DATE('...')` answers NULL with a warning; the literal
/// has to be validated where it's written. Only a string literal follows the
/// type word, so `DATE(x)`/`TIME(x)` calls are untouched.
///
/// ponytail: a DATETIME literal's declared fraction width is lost — `VDateTime`
/// carries no fsp, so `Value.toText` always renders six digits where MySQL
/// renders the three of `TIMESTAMP '2020-01-01 10:00:00.123'`. The value
/// itself is exact; fixing the width needs an fsp channel on the value (or on
/// expression column metadata), which reaches persistence and the protocol.
let private temporalLit: Parser<Expr, unit> =
    let asText v = match v with VString s -> s | _ -> ""

    attempt (
        ((keyword "TIMESTAMP" >>% "TIMESTAMP") <|> (keyword "DATE" >>% "DATE") <|> (keyword "TIME" >>% "TIME"))
        .>>. stringLit
    )
    >>= fun (kind, lit) ->
        let text = (asText lit).Trim()

        let refuse name =
            fail (sprintf "Incorrect %s value: '%s'" name text)

        match kind with
        | "DATE" ->
            match MySqlTemporal.tryDate text with
            | Some d -> preturn (Lit(VDate d))
            | None ->
                tryParseZeroDate text
                |> Option.map (VZeroDate >> Lit >> preturn)
                |> Option.defaultWith (fun () -> refuse "DATE")
        | "TIMESTAMP" ->
            match MySqlTemporal.tryDateTime text with
            | Some dt -> preturn (Lit(VDateTime dt))
            | None ->
                tryParseZeroDateTime text
                |> Option.map (VZeroDateTime >> Lit >> preturn)
                |> Option.defaultWith (fun () -> refuse "DATETIME")
        | _ ->
            match MySqlTemporal.tryTime text with
            | Some t -> preturn (Lit(VString t))
            | None -> refuse "TIME"

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

/// Paren-less `CURRENT_USER` — MySQL's one niladic user function callable
/// without `()` in expressions (TablePlus/phpMyAdmin both emit `SELECT
/// CURRENT_USER`), which would otherwise parse as a column reference below.
/// `CURRENT_USER()` still goes through `funcCallAtom`.
let private currentUserAtom: Parser<Expr, unit> =
    attempt (keyword "CURRENT_USER" .>> notFollowedBy (pstring "(")) >>% FuncCall("CURRENT_USER", [])

let private niladicTimeAtom: Parser<Expr, unit> =
    [ "CURRENT_DATE", "CURRENT_DATE"
      "CURRENT_TIME", "CURRENT_TIME"
      "CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP"
      "LOCALTIME", "LOCALTIME"
      "LOCALTIMESTAMP", "LOCALTIMESTAMP"
      "UTC_DATE", "UTC_DATE"
      "UTC_TIME", "UTC_TIME"
      "UTC_TIMESTAMP", "UTC_TIMESTAMP" ]
    |> List.map (fun (keywordName, functionName) -> attempt (keyword keywordName .>> notFollowedBy (pstring "(") >>% FuncCall(functionName, [])))
    |> choice

/// A bare word: a column, a qualified `t.col` (or `t.*`, `Star(Some "t")`),
/// or a function call if followed by `(args)` (handled by `funcCallAtom`
/// above, tried first so a reserved-word function name still parses).
let private identAtom: Parser<Expr, unit> =
    currentUserAtom
    <|> niladicTimeAtom
    <|> funcCallAtom
    <|> (identifier
         >>= fun name ->
             choice
                 [ sym "."
                   >>. ((pstring "*" >>. ws >>% Star(Some name))
                        <|> (qualifiedIdentifier |>> fun col -> QualifiedCol(name, col)))
                   preturn (Col name) ])

/// `?` parameter placeholder, numbered by SQL-text position via an
/// `AsyncLocal` counter (reset per `parse` call, so concurrent connections'
/// parses never share one). The counter stays out of FParsec user state, so
/// every parser keeps its `unit` state and the placeholder index rides in the
/// AST node instead.
let private placeholderCounterLocal = System.Threading.AsyncLocal<int>()

let private placeholderAtom: Parser<Expr, unit> =
    pchar '?' .>> ws
    |>> (fun _ ->
        let n = placeholderCounterLocal.Value
        placeholderCounterLocal.Value <- n + 1
        Placeholder n)

/// User-variable names are case-insensitive but otherwise opaque to the
/// expression evaluator. MySQL permits punctuation and whitespace when the
/// name is backtick-, single-, or double-quoted; double quotes remain a
/// user-variable delimiter under ANSI_QUOTES.
let private userVariableTarget: Parser<UserVariableRef, unit> =
    let quotedName quote =
        pchar quote >>. manyStrings (quotedStringChar quote) .>> pchar quote

    ((pchar '@'
      >>. choice
              [ attempt backtickIdent
                attempt (quotedName '\'')
                attempt (quotedName '"')
                many1Satisfy (fun c -> isIdentChar c || c = '.' || c = '$')
                notFollowedBy (pchar '@') >>% "" ])
     |> withSkippedString (fun sql name ->
         { Name = name.ToLowerInvariant()
           Sql = sql }))
    .>> ws

let private variableAtom: Parser<Expr, unit> =
    let systemVariable =
        pstring "@@"
        >>. ((attempt (pstringCI "GLOBAL." >>% Some "GLOBAL")) <|> (attempt (pstringCI "SESSION." >>% Some "SESSION")) <|> preturn None)
        .>>. (many1Satisfy isIdentChar .>> ws)
        |>> SystemVariable

    let userVariable = userVariableTarget |>> UserVariable
    attempt systemVariable <|> userVariable

/// `MATCH (col [, col ...]) AGAINST ('query' [modifier])` — the modifier
/// keywords aren't expressions, so like `timestampFuncAtom` below this is
/// its own atom rather than a `funcCallAtom` name. The default (no
/// modifier) is natural language mode; `WITH QUERY EXPANSION` with or
/// without the leading `IN NATURAL LANGUAGE MODE` is the same mode.
let private matchAgainstAtom: Parser<Expr, unit> =
    let matchColumn =
        identifier .>>. opt (sym "." >>. qualifiedIdentifier)
        |>> function
            | qualifier, Some name -> { Qualifier = Some qualifier; Name = name }
            | name, None -> { Qualifier = None; Name = name }

    let modifier =
        choice
            [ attempt (
                  keyword "IN" >>. keyword "NATURAL" >>. keyword "LANGUAGE" >>. keyword "MODE"
                  >>. opt (keyword "WITH" >>. keyword "QUERY" >>. keyword "EXPANSION")
              )
              |>> fun qe -> if qe.IsSome then QueryExpansion else NaturalLanguage
              attempt (keyword "IN" >>. keyword "BOOLEAN" >>. keyword "MODE") >>% BooleanMode
              attempt (keyword "WITH" >>. keyword "QUERY" >>. keyword "EXPANSION") >>% QueryExpansion ]

    // The argument must be a constant (a bound `?` counts; a bare column
    // reference parses so the executor can answer 1210 like MySQL) — full
    // `expr` would swallow the `IN NATURAL LANGUAGE MODE` modifier as an
    // `IN (...)` comparison.
    let againstArg =
        choice [ stringLit |>> Lit; numberLit |>> Lit; placeholderAtom; identifier |>> Col ]

    (keyword "MATCH" >>. sym "(" >>. sepBy1 matchColumn (sym ",") .>> sym ")"
     .>> keyword "AGAINST"
     .>> sym "("
     .>>. againstArg
     .>>. opt modifier
     .>> sym ")")
    |>> fun ((cols, query), mode) -> MatchAgainst(cols, query, mode |> Option.defaultValue NaturalLanguage)

let private atom: Parser<Expr, unit> =
    choice
        [ subqueryExpr
          parenthesizedExpr
          starAtom
          castExpr
          existsExpr
          caseExpr
          intervalAtom
          matchAgainstAtom
          timestampFuncAtom
          getFormatAtom
          extractAtom
          temporalLit
          windowCallAtom
          groupConcatAtom
          bitBytesLit |>> Lit
          numberLit |>> Lit
          hexBytesLit |>> Lit
          nationalStringLit |>> Lit
          introducedStringLit
          stringLit |>> Lit
          keyword "NULL" >>% Lit VNull
          keyword "TRUE" >>% Lit(VInt 1L)
          keyword "FALSE" >>% Lit(VInt 0L)
          placeholderAtom
          variableAtom
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

/// Numeric operators share one precedence parser. Operators without an
/// `Ast.Op` case desugar to internal scalar calls, keeping evaluation and
/// metadata in the same paths as ordinary functions.
let private opp = OperatorPrecedenceParser<Expr, unit, unit>()
let private arithExpr = opp.ExpressionParser

/// `expr COLLATE name` — a postfix on the arithmetic term (`a + b COLLATE c`
/// is `a + (b COLLATE c)` in MySQL), validated against the collation
/// registry here; the tag rides the `Collate` AST node into `Executor`,
/// where comparisons resolve it.
let private collateTerm: Parser<Expr, unit> =
    // `BINARY x` prefix — MySQL's shorthand for a byte-wise comparison cast,
    // expressed as the `binary` collation tag on the operand.
    ((attempt (keyword "BINARY" >>. jsonArrowAtom) |>> fun e -> Collate(e, "binary"))
     <|> jsonArrowAtom)
    .>>. opt (keyword "COLLATE" >>. (identifier <|> (stringLit |>> (function VString name -> name | _ -> ""))))
    >>= fun (e, nameOpt) ->
        match nameOpt with
        | None -> preturn e
        | Some name ->
            match Collation.tryFind name with
            | Some _ -> preturn (Collate(e, name))
            | None -> fail (sprintf "Unknown collation '%s'" name)

opp.TermParser <- collateTerm
opp.AddOperator(InfixOperator("|", ws, 1, Associativity.Left, (fun a b -> FuncCall("BITWISE_OR", [ a; b ]))))
opp.AddOperator(InfixOperator("^", ws, 2, Associativity.Left, (fun a b -> FuncCall("BITWISE_XOR", [ a; b ]))))
opp.AddOperator(InfixOperator("&", ws, 3, Associativity.Left, (fun a b -> FuncCall("BITWISE_AND", [ a; b ]))))
opp.AddOperator(InfixOperator("<<", ws, 4, Associativity.Left, (fun a b -> FuncCall("BITWISE_SHIFT_LEFT", [ a; b ]))))
opp.AddOperator(InfixOperator(">>", ws, 4, Associativity.Left, (fun a b -> FuncCall("BITWISE_SHIFT_RIGHT", [ a; b ]))))
opp.AddOperator(InfixOperator("+", ws, 5, Associativity.Left, (fun a b -> BinOp(Add, a, b))))
opp.AddOperator(InfixOperator("-", ws, 5, Associativity.Left, (fun a b -> BinOp(Sub, a, b))))
opp.AddOperator(InfixOperator("*", ws, 6, Associativity.Left, (fun a b -> BinOp(Mul, a, b))))
opp.AddOperator(InfixOperator("/", ws, 6, Associativity.Left, (fun a b -> BinOp(Div, a, b))))
opp.AddOperator(InfixOperator("%", ws, 6, Associativity.Left, (fun a b -> FuncCall("MOD", [ a; b ]))))

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
opp.AddOperator(InfixOperator("DIV", divKeywordBoundary, 6, Associativity.Left, (fun a b -> BinOp(IntDiv, a, b))))
opp.AddOperator(InfixOperator("div", divKeywordBoundary, 6, Associativity.Left, (fun a b -> BinOp(IntDiv, a, b))))
// `a MOD b` is the word spelling of `a % b`, with the same precedence and
// the same word-boundary/casing caveats as `DIV` above. `MOD(a, b)` still
// parses as a function call: the term parser consumes the `(` form before
// the operator parser ever looks for an infix keyword.
opp.AddOperator(InfixOperator("MOD", divKeywordBoundary, 6, Associativity.Left, (fun a b -> FuncCall("MOD", [ a; b ]))))
opp.AddOperator(InfixOperator("mod", divKeywordBoundary, 6, Associativity.Left, (fun a b -> FuncCall("MOD", [ a; b ]))))
/// Unary minus. On a *literal* the sign is part of the literal, the way
/// MySQL's own lexer reads it — `-9223372036854775808` is BIGINT's signed
/// minimum and `-18446744073709551615` an exact DECIMAL, where the general
/// `0 - x` desugaring would subtract from the `BIGINT UNSIGNED` those digits
/// parse as and leave the unsigned domain (error 1690).
///
/// ponytail: `-(<unsigned expression>)` still desugars, so it raises 1690
/// where MySQL negates into DECIMAL. Give `Ast.Expr` a real `Neg` case if
/// that shape shows up outside literals.
let private negateExpr (e: Expr) : Expr =
    match e with
    | Lit(VInt i) -> Lit(VInt(-i))
    | Lit(VUInt u) when u <= uint64 Int64.MaxValue -> Lit(VInt(-(int64 u)))
    | Lit(VUInt u) when u = 9223372036854775808UL -> Lit(VInt Int64.MinValue)
    | Lit(VUInt u) -> Lit(VDecimal(-(decimal u)))
    | Lit(VDecimal d) -> Lit(VDecimal(-d))
    | Lit(VDouble d) -> Lit(VDouble(-d))
    | _ -> BinOp(Sub, Lit(VInt 0L), e)

opp.AddOperator(PrefixOperator("-", ws, 7, true, negateExpr))
opp.AddOperator(PrefixOperator("~", ws, 7, true, (fun value -> FuncCall("BITWISE_NOT", [ value ]))))

/// `IN (SELECT ...)` vs. `IN (expr, expr, ...)` — both start with `(`, so
/// the subquery form is tried first (`attempt`ed since `selectWithCtes`
/// commits on its leading `SELECT` keyword the same way `subqueryExpr`
/// does) before falling back to the literal candidate list.
let private inCandidates: Parser<Choice<SelectStmt, Expr list>, unit> =
    sym "(" >>. (attempt (selectWithCtes |>> Choice1Of2) <|> (sepBy1 expr (sym ",") |>> Choice2Of2)) .>> sym ")"

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

/// `ANY` and `SOME` are aliases; keeping that normalization in the parser
/// makes execution a two-case universal/existential fold.
let private quantifier: Parser<Quantifier, unit> =
    (keyword "ANY" >>% Any)
    <|> (keyword "SOME" >>% Any)
    <|> (keyword "ALL" >>% All)

let private quantifiedComparison: Parser<Op * Quantifier * SelectStmt, unit> =
    let comparisonOp =
        choice
            [ pstring "<=" >>% Lte
              pstring ">=" >>% Gte
              pstring "<>" >>% Neq
              pstring "!=" >>% Neq
              pstring "=" >>% Eq
              pstring "<" >>% Lt
              pstring ">" >>% Gt ]
        .>> ws

    comparisonOp .>>. quantifier .>>. between (sym "(") (sym ")") selectWithCtes
    |>> fun ((op, quantifier), select) -> op, quantifier, select

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
              attempt (keyword "SOUNDS" >>. keyword "LIKE") >>. arithExpr
              |>> fun right -> BinOp(Eq, FuncCall("SOUNDEX", [ left ]), FuncCall("SOUNDEX", [ right ]))
              attempt (keyword "MEMBER" >>. keyword "OF") >>. between (sym "(") (sym ")") arithExpr
              |>> fun array -> FuncCall("JSON_MEMBER_OF", [ left; array ])
              attempt quantifiedComparison |>> fun (op, quantifier, select) -> QuantifiedComparison(left, op, quantifier, select)
              compareOp .>>. arithExpr |>> fun (op, right) -> BinOp(op, left, right)
              preturn left ]

/// `NOT` sits between `AND` and comparisons: `NOT a = b` is `NOT (a = b)`,
/// but `a AND NOT b` negates only `b`. Forward-referenced like `expr` above,
/// since `let rec` on a parser *value* (rather than a function) would
/// evaluate the right-hand side eagerly and see itself undefined.
let private notExpr, notExprRef = createParserForwardedToRef<Expr, unit> ()
notExprRef.Value <- depthGuard ((keyword "NOT" >>. notExpr |>> Not) <|> comparisonExpr)

let private andExpr: Parser<Expr, unit> =
    chainl1 notExpr (keyword "AND" >>% fun a b -> BinOp(And, a, b))

/// `XOR` sits between `OR` and `AND` in MySQL's precedence table, and is
/// left-associative — oracle-verified: `1 XOR 1 OR 1` is 1 (so XOR binds
/// tighter than OR) and `1 XOR 1 AND 0` is 1 (so AND binds tighter still).
let private xorExpr: Parser<Expr, unit> =
    chainl1 andExpr (keyword "XOR" >>% fun a b -> BinOp(Xor, a, b))

let private orExpr: Parser<Expr, unit> =
    chainl1 xorExpr (keyword "OR" >>% fun a b -> BinOp(Or, a, b))

let private assignmentExpr: Parser<Expr, unit> =
    attempt (userVariableTarget .>> sym ":=" .>>. expr)
    |>> AssignUserVariable
    <|> orExpr

do exprRef.Value <- depthGuard assignmentExpr

type private ColMod =
    | MNotNull
    | MNull
    | MDefault of ColumnDefault
    | MAutoIncrement
    | MPrimaryKey
    | MUnique
    | MGenerated of Expr * GeneratedKind
    /// A validated `COLLATE name` — the column's collation, resolved against
    /// the registry at parse time so an unknown name is a clean error.
    | MCollate of string
    /// A validated `CHARACTER SET name` (utf8mb4/latin1/ascii).
    | MCharset of string
    | MOnUpdateCurrentTimestamp
    | MCheck of name: string option * expression: Expr * enforced: bool
    | MComment of string

/// `CURRENT_TIMESTAMP[(N)]` — the `(N)` is accepted and dropped: MySQL
/// requires it to match the column's own declared fsp, and the default is
/// evaluated at that declared fsp regardless (`Storage.evalDefault`).
let private defaultValueLit: Parser<ColumnDefault, unit> =
    let negativeNumber =
        sym "-"
        >>. numberLit
        >>= function
            | VInt value when value = Int64.MinValue -> preturn (VDecimal(-(decimal value)))
            | VInt value -> preturn (VInt(-value))
            | VUInt value when value = 9223372036854775808UL -> preturn (VInt Int64.MinValue)
            | VUInt value -> preturn (VDecimal(-(decimal value)))
            | VDecimal value -> preturn (VDecimal(-value))
            | VDouble value -> preturn (VDouble(-value))
            | _ -> fail "a numeric default must follow '-'"

    // MariaDB dumps emit the function-call spelling `current_timestamp()`;
    // the empty parens are the same as none.
    (keyword "CURRENT_TIMESTAMP" >>. optional (attempt widthLen) >>. optional (sym "(" >>. sym ")") >>% DCurrentTimestamp)
    <|> attempt (between (sym "(") (sym ")") expr |>> DExpression)
    <|> attempt (negativeNumber |>> DConst)
    <|> (literalValue |>> DConst)

/// A charset/collation name — Laravel emits `COLLATE 'utf8mb4_unicode_ci'`
/// (quoted) at the table level but a bare identifier at the column level, so
/// this accepts either.
let private identOrString: Parser<string, unit> =
    identifier <|> (stringLit |>> (function VString s -> s | _ -> ""))

/// `[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]` — a computed column
/// (`char(16) ... AS (UNHEX(MD5(\`key\`)))`, Laravel Pulse's dedup key hash).
/// Reuses the full `expr` grammar (arbitrary nested function calls), and the
/// parsed `Expr` is kept on `ColumnDef.Generated` for `Executor`/`Storage`
/// to evaluate on insert/update, alongside the VIRTUAL/STORED kind
/// (VIRTUAL when omitted, matching MySQL).
let private generatedColumn: Parser<Expr * GeneratedKind, unit> =
    optional (keyword "GENERATED" >>. keyword "ALWAYS")
    >>. keyword "AS"
    >>. sym "("
    >>. expr
    .>> sym ")"
    .>>. (opt ((keyword "VIRTUAL" >>% Virtual) <|> (keyword "STORED" >>% Stored))
          |>> Option.defaultValue Virtual)

/// The charsets fsdb accepts in DDL (see `ColumnDef.Charset`'s doc for what
/// each actually does at runtime), lowercased; anything else is a parse
/// error. One validator shared by column mods and table options.
let private knownCharset: Parser<string, unit> =
    identOrString
    >>= fun name ->
        match name.ToLowerInvariant() with
        | "utf8mb4"
        | "utf8mb3"
        | "utf8"
        | "latin1"
        | "ascii"
        | "binary" as cs -> preturn cs
        | _ -> fail (sprintf "Unknown character set: '%s'" name)

let private checkEnforcement: Parser<bool, unit> =
    opt ((attempt (keyword "NOT" >>. keyword "ENFORCED") >>% false) <|> (keyword "ENFORCED" >>% true))
    |>> Option.defaultValue true

let private checkDefinition: Parser<string option * Expr * bool, unit> =
    (opt (attempt (keyword "CONSTRAINT" >>. opt identifier)) |>> Option.flatten)
    .>> keyword "CHECK"
    .>>. between (sym "(") (sym ")") expr
    .>>. checkEnforcement
    |>> fun ((name, expression), enforced) -> name, expression, enforced

let private colMod: Parser<ColMod, unit> =
    choice
        [ attempt (checkDefinition |>> MCheck)
          attempt (keyword "NOT" >>. keyword "NULL") >>% MNotNull
          keyword "NULL" >>% MNull
          keyword "DEFAULT" >>. defaultValueLit |>> MDefault
          keyword "AUTO_INCREMENT" >>% MAutoIncrement
          attempt (keyword "PRIMARY" >>. keyword "KEY") >>% MPrimaryKey
          keyword "UNIQUE" >>. optional (keyword "KEY") >>% MUnique
          attempt (
              keyword "ON" >>. keyword "UPDATE" >>. keyword "CURRENT_TIMESTAMP"
              >>. optional (attempt widthLen)
              >>. optional (sym "(" >>. sym ")")
          )
          >>% MOnUpdateCurrentTimestamp
          keyword "COMMENT" >>. stringLit |>> (function VString text -> MComment text | _ -> MComment "")
          attempt (keyword "CHARACTER" >>. keyword "SET" <|> keyword "CHARSET") >>. knownCharset |>> MCharset
          keyword "COLLATE"
          >>. identOrString
          >>= fun name ->
              match Collation.tryFind name with
              | Some _ -> preturn (MCollate name)
              | None -> fail (sprintf "Unknown collation '%s'" name)
          attempt generatedColumn |>> MGenerated ]

let private parsedColumnDef: Parser<ColumnDef * CheckConstraintDef list, unit> =
    (identifier .>>. columnType .>>. many colMod)
    |>> fun ((name, ty), mods) ->
        let column =
            { Name = name
              Type = ty
              Nullable = not (List.contains MNotNull mods)
              Default = mods |> List.tryPick (function MDefault v -> Some v | _ -> None)
              AutoIncrement = List.contains MAutoIncrement mods
              PrimaryKey = List.contains MPrimaryKey mods
              Unique = List.contains MUnique mods
              OnUpdateCurrentTimestamp = List.contains MOnUpdateCurrentTimestamp mods
              Generated = mods |> List.tryPick (function MGenerated(e, k) -> Some(e, k) | _ -> None)
              Comment = mods |> List.rev |> List.tryPick (function MComment text -> Some text | _ -> None) |> Option.defaultValue ""
              Collation = mods |> List.tryPick (function MCollate c -> Some c | _ -> None)
              Charset =
                  mods
                  |> List.tryPick (function
                      | MCharset c -> Some c
                      | _ -> None)
                  |> Option.orElseWith (fun () ->
                      mods
                      |> List.tryPick (function
                          | MCollate _ -> Some "utf8mb4"
                          | _ -> None)) }

        let checks =
            mods
            |> List.choose (function
                | MCheck(checkName, expression, enforced) ->
                    Some
                        { Name = checkName
                          Expression = expression
                          Enforced = enforced
                          Column = Some name }
                | _ -> None)

        column, checks

let private columnDef: Parser<ColumnDef, unit> = parsedColumnDef |>> fst

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
let private indexPrefix: Parser<bool * IndexKind, unit> =
    (keyword "UNIQUE" >>. optional (keyword "KEY" <|> keyword "INDEX") >>% (true, BTree))
    <|> (keyword "FULLTEXT" >>. optional (keyword "KEY" <|> keyword "INDEX") >>% (false, FullTextIndex))
    // SPATIAL collapses to an ordinary index; the storage layer has no
    // spatial access path.
    <|> (keyword "SPATIAL" >>. optional (keyword "KEY" <|> keyword "INDEX") >>% (false, BTree))
    <|> ((keyword "KEY" <|> keyword "INDEX") >>% (false, BTree))

let private indexItem: Parser<IndexDef, unit> =
    (indexPrefix .>>. opt identifier
     .>>. between (sym "(") (sym ")") (sepBy1 indexColumn (sym ","))
     // `USING BTREE|HASH` — parsed and discarded, every index here is the
     // same structure either way.
     .>> optional (keyword "USING" >>. (keyword "BTREE" <|> keyword "HASH")))
    |>> fun (((unique, kind), name), cols) ->
        { Name = name |> Option.defaultValue (List.head cols)
          Columns = cols
          Unique = unique
          Kind = kind }

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
    | CColumn of ColumnDef * CheckConstraintDef list
    | CPrimaryKey of string list
    | CIndex of IndexDef
    | CForeignKey of ForeignKeyDef
    | CCheck of CheckConstraintDef

let private createTableItem: Parser<CreateItem, unit> =
    choice
        [ attempt (foreignKeyItem |>> CForeignKey)
          attempt (
              checkDefinition
              |>> fun (name, expression, enforced) ->
                  CCheck
                      { Name = name
                        Expression = expression
                        Enforced = enforced
                        Column = None }
          )
          attempt (trailingPrimaryKey |>> CPrimaryKey)
          attempt (indexItem |>> CIndex)
          parsedColumnDef |>> CColumn ]

/// `ENGINE=`, `CHARSET=`/`DEFAULT CHARSET=` table options: accepted and
/// discarded, same treatment as column display widths. `COLLATE=` is the
/// one tracked option — it becomes the table's column default (validated
/// against the collation registry), which `createTable` bakes into every
/// column that didn't name one explicitly.
type private TableOption =
    | TableCharset of string
    | TableCollate of string
    | TableAutoIncrement of int64
    | TableComment of string
    | TableOptionIgnored

let private hashPartitionOption: Parser<TableOption, unit> =
    keyword "PARTITION"
    >>. keyword "BY"
    >>. optional (keyword "LINEAR")
    >>. keyword "HASH"
    >>. between (sym "(") (sym ")") expr
    >>. opt (keyword "PARTITIONS" >>. (puint64 .>> ws))
    >>= function
        | Some 0UL -> fail "the number of partitions must be positive"
        | _ -> preturn TableOptionIgnored

/// One table-option tail entry. Options fsdb has no behavior for
/// (ROW_FORMAT, KEY_BLOCK_SIZE, the STATS_* family) are accepted and
/// discarded so their dump-file tails restore.
let private tableOption: Parser<TableOption, unit> =
    choice
        [ keyword "ENGINE" >>. opt (sym "=") >>. identOrString >>% TableOptionIgnored
          attempt (keyword "AUTO_INCREMENT" >>. opt (sym "=")) >>. pint64 .>> ws |>> TableAutoIncrement
          keyword "COMMENT" >>. opt (sym "=") >>. stringLit
          |>> (function VString value -> TableComment value | value -> TableComment(toText value |> Option.defaultValue ""))
          (keyword "ROW_FORMAT" <|> keyword "CHECKSUM" <|> keyword "DELAY_KEY_WRITE" <|> keyword "PACK_KEYS")
          >>. opt (sym "=")
          >>. identOrString
          >>% TableOptionIgnored
          (keyword "KEY_BLOCK_SIZE"
           <|> keyword "STATS_PERSISTENT"
           <|> keyword "STATS_AUTO_RECALC"
           <|> keyword "STATS_SAMPLE_PAGES"
           <|> keyword "AVG_ROW_LENGTH"
           <|> keyword "MAX_ROWS"
           <|> keyword "MIN_ROWS")
          >>. opt (sym "=")
          >>. identOrString
          >>% TableOptionIgnored
          attempt (
              optional (keyword "DEFAULT")
              >>. (keyword "CHARSET" <|> (keyword "CHARACTER" >>. keyword "SET"))
          )
          >>. opt (sym "=")
          >>. knownCharset
          |>> TableCharset
          keyword "COLLATE"
          >>. opt (sym "=")
          >>. identOrString
          >>= fun name ->
              match Collation.tryFind name with
              | Some _ -> preturn (TableCollate name)
              | None -> fail (sprintf "Unknown collation '%s'" name)
          attempt hashPartitionOption ]

let private tableOptions: Parser<string option * string option * int64 option * string option, unit> =
    many (optional (sym ",") >>. tableOption)
    |>> fun opts ->
        opts
        |> List.fold
            // A repeated option's last occurrence wins, same as MySQL.
            (fun (cs, col, seed, comment) opt ->
                match opt with
                | TableCharset c -> Some c, col, seed, comment
                | TableCollate l -> cs, Some l, seed, comment
                | TableAutoIncrement n -> cs, col, Some n, comment
                | TableComment value -> cs, col, seed, Some value
                | TableOptionIgnored -> cs, col, seed, comment)
            (None, None, None, None)

let private createTable: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. qualifiedTableName
     .>>. between (sym "(") (sym ")") (sepBy1 createTableItem (sym ","))
     .>>. tableOptions)
    |>> fun (((ifNotExists, name), items), (tableCharset, tableCollation, autoIncrementSeed, tableComment)) ->
        let pkNames = items |> List.collect (function CPrimaryKey names -> names | _ -> [])
        let explicitIndexes = items |> List.choose (function CIndex ix -> Some ix | _ -> None)
        let foreignKeys = items |> List.choose (function CForeignKey fk -> Some fk | _ -> None)

        let columns =
            items
            |> List.choose (function
                | CColumn(c, _) ->
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
                                // A real column always ends up with an
                                // explicit collation — the column-level
                                // COLLATE, else the table's declaration,
                                // else the server default — exactly like
                                // MySQL, where a plain `name VARCHAR(20)`
                                // still reports utf8mb4_0900_ai_ci. The
                                // charset stays `None` unless declared, so
                                // SHOW CREATE TABLE renders the default
                                // case as plain `varchar(20)`.
                                Collation =
                                    c.Collation
                                    |> Option.orElse tableCollation
                                    |> Option.orElse (Some Collation.defaultCollation.Name)
                                Charset = c.Charset |> Option.orElse tableCharset }
                        else
                            c

                    Some(if List.contains c.Name pkNames then { withDefaults with PrimaryKey = true } else withDefaults)
                | _ -> None)

        let checks =
            items
            |> List.collect (function
                | CColumn(_, checks) -> checks
                | CCheck check -> [ check ]
                | _ -> [])

        // A column-level `UNIQUE` modifier is just sugar for a single-column
        // unique index named after the column, so it lands in the same
        // `Indexes` bucket a trailing `UNIQUE KEY` would.
        let uniqueColumnIndexes =
            columns
            |> List.filter (fun c -> c.Unique)
            |> List.map (fun c -> { Name = c.Name; Columns = [ c.Name ]; Unique = true; Kind = BTree })

        CreateTable
            { Name = name
              Columns = columns
              Indexes = explicitIndexes @ uniqueColumnIndexes
              ForeignKeys = foreignKeys
              Checks = checks
              IfNotExists = ifNotExists
              Charset = tableCharset
              Collation = tableCollation
              AutoIncrementSeed = autoIncrementSeed
              Comment = tableComment }

let private createTableLike: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. qualifiedTableName
     .>> keyword "LIKE"
     .>>. qualifiedTableName)
    |>> fun ((ifNotExists, name), source) -> CreateTableLike(name, source, ifNotExists)

let private createIndexStmt: Parser<Statement, unit> =
    (keyword "CREATE"
     >>. ((keyword "UNIQUE" >>% (true, BTree))
          <|> (keyword "FULLTEXT" >>% (false, FullTextIndex))
          <|> (keyword "SPATIAL" >>% (false, BTree))
          <|> preturn (false, BTree))
     .>> keyword "INDEX"
     .>>. identifier
     .>> keyword "ON"
     .>>. qualifiedTableName
     .>>. between (sym "(") (sym ")") (sepBy1 indexColumn (sym ",")))
    |>> fun ((((unique, kind), name), table), cols) -> CreateIndex(name, table, cols, unique, kind)

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

let private doStmt: Parser<Statement, unit> =
    keyword "DO" >>. sepBy1 expr (sym ",") |>> Do

let private checksumTableStmt: Parser<Statement, unit> =
    keyword "CHECKSUM"
    >>. keyword "TABLE"
    >>. sepBy1 qualifiedTableName (sym ",")
    .>>. opt ((keyword "QUICK" >>% true) <|> (keyword "EXTENDED" >>% false))
    |>> fun (tables, quick) -> ChecksumTables(tables, quick |> Option.defaultValue false)

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

let private addColumnAction: Parser<AlterAction list, unit> =
    attempt (keyword "ADD" >>. optColumnKw >>. parsedColumnDef .>>. colPosition)
    |>> fun ((column, checks), position) -> AddColumn(column, position) :: (checks |> List.map AddCheck)

let private addPrimaryKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. trailingPrimaryKey) |>> AddPrimaryKey

let private addIndexAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. indexItem) |>> AddIndex

let private addForeignKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. foreignKeyItem) |>> AddForeignKey

let private addCheckAction: Parser<AlterAction, unit> =
    attempt (keyword "ADD" >>. checkDefinition)
    |>> fun (name, expression, enforced) ->
        AddCheck
            { Name = name
              Expression = expression
              Enforced = enforced
              Column = None }

let private dropCheckAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. (keyword "CHECK" <|> keyword "CONSTRAINT") >>. identifier) |>> DropCheck

let private setCheckEnforcementAction: Parser<AlterAction, unit> =
    attempt (keyword "ALTER" >>. (keyword "CHECK" <|> keyword "CONSTRAINT") >>. identifier .>>. checkEnforcement)
    |>> SetCheckEnforced

let private setColumnDefaultAction: Parser<AlterAction, unit> =
    attempt (
        keyword "ALTER"
        >>. optColumnKw
        >>. identifier
        .>>. ((keyword "SET" >>. keyword "DEFAULT" >>. defaultValueLit |>> Some)
              <|> (keyword "DROP" >>. keyword "DEFAULT" >>% None))
    )
    |>> SetDefault

let private dropForeignKeyAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. keyword "FOREIGN" >>. keyword "KEY" >>. identifier) |>> DropForeignKey

let private dropIndexAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. (keyword "INDEX" <|> keyword "KEY") >>. identifier) |>> DropIndexAction

let private dropColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "DROP" >>. optColumnKw >>. identifier) |>> DropColumn

let private modifyColumnAction: Parser<AlterAction list, unit> =
    attempt (keyword "MODIFY" >>. optColumnKw >>. parsedColumnDef .>>. colPosition)
    |>> fun ((column, checks), position) -> ModifyColumn(column, position) :: (checks |> List.map AddCheck)

let private changeColumnAction: Parser<AlterAction list, unit> =
    attempt (keyword "CHANGE" >>. optColumnKw >>. identifier .>>. parsedColumnDef .>>. colPosition)
    |>> fun ((oldName, (newDef, checks)), position) -> ChangeColumn(oldName, newDef, position) :: (checks |> List.map AddCheck)

let private renameColumnAction: Parser<AlterAction, unit> =
    attempt (keyword "RENAME" >>. keyword "COLUMN" >>. identifier .>> keyword "TO" .>>. identifier)
    |>> RenameColumnTo

let private renameIndexAction: Parser<AlterAction, unit> =
    attempt (
        keyword "RENAME" >>. (keyword "INDEX" <|> keyword "KEY") >>. identifier .>> keyword "TO" .>>. identifier
    )
    |>> RenameIndex

let private renameToAction: Parser<AlterAction, unit> =
    attempt (keyword "RENAME" >>. opt (keyword "TO" <|> keyword "AS") >>. identifier) |>> RenameTo

let private setAutoIncrementAction: Parser<AlterAction, unit> =
    attempt (keyword "AUTO_INCREMENT" >>. opt (sym "=")) >>. pint64 .>> ws |>> SetAutoIncrement

let private setEngineAction: Parser<AlterAction, unit> =
    attempt (keyword "ENGINE" >>. opt (sym "=") >>. identifier) |>> SetEngine

let private setTableCommentAction: Parser<AlterAction, unit> =
    attempt (keyword "COMMENT" >>. opt (sym "=") >>. stringLit)
    |>> function
        | VString comment -> SetTableComment comment
        | _ -> SetTableComment ""

let private convertCharsetAction: Parser<AlterAction, unit> =
    attempt (
        keyword "CONVERT"
        >>. keyword "TO"
        >>. (keyword "CHARACTER" >>. keyword "SET" <|> keyword "CHARSET")
        >>. knownCharset
        .>>. opt (keyword "COLLATE" >>. identOrString)
    )
    >>= fun (charset, collation) ->
        match collation with
        | Some name when Collation.tryFind name |> Option.isNone -> fail (sprintf "Unknown collation '%s'" name)
        | _ -> preturn (ConvertCharset(charset, collation))

let private alterAction: Parser<AlterAction list, unit> =
    choice
        [ addForeignKeyAction |>> List.singleton
          addCheckAction |>> List.singleton
          addPrimaryKeyAction |>> List.singleton
          addIndexAction |>> List.singleton
          addColumnAction
          dropForeignKeyAction |>> List.singleton
          dropCheckAction |>> List.singleton
          dropIndexAction |>> List.singleton
          dropColumnAction |>> List.singleton
          modifyColumnAction
          changeColumnAction
          setColumnDefaultAction |>> List.singleton
          setCheckEnforcementAction |>> List.singleton
          renameIndexAction |>> List.singleton
          renameColumnAction |>> List.singleton
          setAutoIncrementAction |>> List.singleton
          setEngineAction |>> List.singleton
          setTableCommentAction |>> List.singleton
          convertCharsetAction |>> List.singleton
          renameToAction |>> List.singleton ]
    <?> "ALTER TABLE action"

let private alterTableStmt: Parser<Statement, unit> =
    (keyword "ALTER" >>. keyword "TABLE" >>. qualifiedTableName .>>. sepBy1 alterAction (sym ","))
    |>> fun (table, actions) -> AlterTable(table, List.concat actions)

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
/// One `INSERT ... VALUES` cell, literal fast path first: bulk dump/ORM
/// inserts are overwhelmingly plain literals (quoted string, [signed]
/// number, NULL), and running the full `expr` operator-precedence machinery
/// per cell made a 500-row × 20-column INSERT parse in ~60 ms — the whole
/// import bottleneck. Each fast alternative requires the cell to end right
/// after the literal (next char `,` or `)`), so any real expression
/// (`1 + 2`, `NOW()`, `?`, `_binary'..'`) still falls through to `expr`
/// with identical semantics.
let private insertValue: Parser<Expr, unit> =
    // A fast alternative only wins the cell if the literal is the whole
    // cell — the next char must be the tuple's `,` or `)`.
    let cellEnd = followedBy (pchar ',' <|> pchar ')')
    let literal p = attempt (p .>> cellEnd |>> Lit)

    // Numbers/NULL before strings: dump cells are mostly numeric, and a
    // failed string attempt is cheaper than a failed number parse. The
    // negative form goes through the same `negateExpr` the grammar's unary
    // minus uses, so both paths share one negation semantics.
    choice
        [ literal numberLit
          literal (keyword "NULL" >>% VNull)
          literal stringLit
          attempt (pchar '-' >>. ws >>. numberLit .>> cellEnd |>> (Lit >> negateExpr))
          expr ]

let private insertStmt: Parser<Statement, unit> =
    let assignments = sepBy1 ((identifier .>> sym "=") .>>. expr) (sym ",")
    let row = optional (keyword "ROW") >>. between (sym "(") (sym ")") (sepBy1 insertValue (sym ","))

    (keyword "INSERT" >>. (opt (keyword "IGNORE") |>> Option.isSome)
     .>> keyword "INTO"
     .>>. qualifiedTableName
     .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
     .>>. choice
              [ ((keyword "VALUES" <|> keyword "VALUE") >>. sepBy1 row (sym ",")
                 .>>. opt onDuplicateKeyUpdate)
                |>> Choice1Of3
                (selectWithCtes .>>. opt onDuplicateKeyUpdate) |>> Choice2Of3
                (keyword "SET" >>. assignments .>>. opt onDuplicateKeyUpdate) |>> Choice3Of3 ])
    |>> fun (((ignoreDuplicates, table), cols), branch) ->
        let cols = cols |> Option.defaultValue []

        match branch with
        | Choice1Of3(rows, onDup) -> Insert(table, cols, rows, onDup |> Option.defaultValue [], ignoreDuplicates)
        | Choice2Of3(select, onDup) ->
            InsertSelect(table, cols, select, onDup |> Option.defaultValue [], ignoreDuplicates)
        | Choice3Of3(assignments, onDup) ->
            let columns, values = List.unzip assignments
            Insert(table, columns, [ values ], onDup |> Option.defaultValue [], ignoreDuplicates)

let private replaceStmt: Parser<Statement, unit> =
    let row = optional (keyword "ROW") >>. between (sym "(") (sym ")") (sepBy1 insertValue (sym ","))
    let assignments = sepBy1 ((identifier .>> sym "=") .>>. expr) (sym ",")
    let columns = opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))

    let rows =
        columns
        .>>. choice
                 [ ((keyword "VALUES" <|> keyword "VALUE") >>. sepBy1 row (sym ",")) |>> Choice1Of2
                   selectWithCtes |>> Choice2Of2 ]

    (keyword "REPLACE"
     >>. optional (keyword "INTO")
     >>. qualifiedTableName
     .>>. choice [ (keyword "SET" >>. assignments) |>> Choice1Of2; rows |>> Choice2Of2 ])
    |>> fun (table, source) ->
        match source with
        | Choice1Of2 assignments -> ReplaceSet(table, assignments)
        | Choice2Of2(cols, Choice1Of2 rows) -> Replace(table, cols |> Option.defaultValue [], rows)
        | Choice2Of2(cols, Choice2Of2 select) -> ReplaceSelect(table, cols |> Option.defaultValue [], select)

let private localLoadString = stringLit |>> function VString value -> value | _ -> ""

let private localLoadFields =
    opt
        ((keyword "FIELDS" <|> keyword "COLUMNS")
         >>. opt (keyword "TERMINATED" >>. keyword "BY" >>. localLoadString)
         .>>. opt (keyword "ENCLOSED" >>. keyword "BY" >>. localLoadString)
         .>>. opt (keyword "ESCAPED" >>. keyword "BY" >>. localLoadString))
    |>> function
        | None -> "\t", None, Some "\\"
        | Some((terminator, enclosed), escape) ->
            (terminator |> Option.defaultValue "\t"), enclosed, (escape |> Option.defaultValue "\\" |> Some)

let private localLoadLines =
    opt
        (keyword "LINES"
         >>. opt (keyword "TERMINATED" >>. keyword "BY" >>. localLoadString))
    |>> function
        | None -> "\n"
        | Some terminator -> terminator |> Option.defaultValue "\n"

let private localLoadData: Parser<LocalLoad, unit> =
    (keyword "LOAD"
     >>. keyword "DATA"
     >>. keyword "LOCAL"
     >>. keyword "INFILE"
     >>. localLoadString
     .>>. opt ((keyword "REPLACE" >>% true) <|> (keyword "IGNORE" >>% false))
     .>> keyword "INTO"
     .>> keyword "TABLE"
     .>>. qualifiedTableName
     .>>. opt (keyword "CHARACTER" >>. keyword "SET" >>. identifier)
     .>>. localLoadFields
     .>>. localLoadLines
     .>>. opt (keyword "IGNORE" >>. intTok .>> (keyword "LINES" <|> keyword "ROWS"))
     .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ","))))
    |>> fun (((((((fileName, replace), table), charset), fields), lineTerminator), ignoreLines), columns) ->
        let fieldTerminator, enclosed, escape = fields

        { FileName = fileName
          Table = table
          Replace = replace |> Option.defaultValue false
          Ignore = replace |> Option.defaultValue false |> not
          Charset = charset
          FieldTerminator = fieldTerminator
          EnclosedBy = enclosed
          Escape = escape
          LineTerminator = lineTerminator
          IgnoreLines = ignoreLines |> Option.defaultValue 0
          Columns = columns |> Option.defaultValue [] }

/// A projection's alias — `AS name`, or real MySQL's implicit form with no
/// `AS` at all (`SELECT 1 x FROM t`, `SELECT price * qty total FROM
/// orders`): a bare word right after the expression that isn't the next
/// clause's keyword. MySQL also accepts a quoted string in this alias-only
/// position. `identifier` rejects every word in `reservedWords`
/// (`FROM`/`WHERE`/`GROUP`/`ORDER`/`HAVING`/`LIMIT`/...), so this only fires
/// on an actual alias, not the start of the next clause; `attempt`ed so a
/// comma or clause keyword cleanly falls through to `None`.
let private projectionAlias: Parser<string option, unit> =
    let name = identifier <|> (stringLit |>> function VString value -> value | _ -> "")
    (attempt (keyword "AS" >>. name) |>> Some) <|> (attempt name |>> Some) <|> preturn None

let private projection: Parser<Projection, unit> = expr .>>. projectionAlias

let private orderKey: Parser<OrderKey, unit> =
    (expr .>>. opt ((keyword "ASC" >>% Asc) <|> (keyword "DESC" >>% Desc)))
    |>> fun (e, dir) -> (e, dir |> Option.defaultValue Asc)

/// LIMIT/OFFSET literals and prepared-statement markers remain expressions
/// until binding. Literal counts are clamped to the engine's in-memory row
/// ceiling while preserving MySQL's unsigned 64-bit syntax.
let private limitTok: Parser<Expr, unit> =
    (puint64 .>> ws |>> fun n -> Lit(VInt(int64 (min n (uint64 Int32.MaxValue))))) <|> placeholderAtom

/// `LIMIT n`, `LIMIT n OFFSET m`, and the MySQL-specific `LIMIT m, n` (which
/// means offset `m`, count `n` — the arguments are in the opposite order
/// from `LIMIT n OFFSET m`).
let private limitClause: Parser<Expr option * Expr option, unit> =
    keyword "LIMIT" >>. limitTok
    >>= fun a ->
        (sym "," >>. limitTok |>> fun b -> (Some b, Some a))
        <|> (keyword "OFFSET" >>. limitTok |>> fun b -> (Some a, Some b))
        <|> preturn (Some a, None)

/// `[db.]table [[AS] alias]` — the alias form omits `AS` too (`FROM t x`),
/// same as MySQL; `identifier` already backtracks cleanly off a reserved
/// word (e.g. `WHERE`), so no `attempt` is needed around the bare-alias
/// alternative.
let private indexHint: Parser<unit, unit> =
    ((keyword "USE" <|> keyword "FORCE" <|> keyword "IGNORE")
     >>. (keyword "INDEX" <|> keyword "KEY")
     >>. optional (
         keyword "FOR"
         >>. (keyword "JOIN" <|> attempt (keyword "ORDER" >>. keyword "BY") <|> attempt (keyword "GROUP" >>. keyword "BY"))
     )
     >>. between
         (sym "(")
         (sym ")")
         (sepBy ((keyword "PRIMARY" >>% "PRIMARY") <|> identifier) (sym ",")))
    >>% ()

let private tableRef: Parser<TableRef, unit> =
    (identifier .>>. opt (sym "." >>. qualifiedIdentifier))
    .>>. opt ((keyword "AS" >>. identifier) <|> identifier)
    .>> many indexHint
    |>> fun ((first, second), alias) ->
        match second with
        | Some table -> { Database = Some first; Table = table; Alias = alias }
        | None -> { Database = None; Table = first; Alias = alias }

let private withClause: Parser<CommonTableExpr list, unit> =
    keyword "WITH" >>. opt (keyword "RECURSIVE")
    >>= fun recursive ->
        sepBy1
            (identifier
             .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
             .>> keyword "AS"
             .>>. between (sym "(") (sym ")") selectQuery
             |>> fun ((name, cols), body) ->
                 { CteName = name
                   CteColumns = cols |> Option.defaultValue []
                   Recursive = recursive.IsSome
                   Body = body })
            (sym ",")

/// A single `SELECT`, redundantly wrapped in one or more parens —
/// `(SELECT ...)` and `((SELECT ...))` both reduce to the same `SelectStmt`.
/// This is what lets a `UNION` branch (`selectOrUnionBranches` below, shared
/// with the top-level `selectOrUnionStmt`) accept MySQL's `(SELECT ...)
/// UNION (SELECT ...)` form where every branch is individually
/// parenthesized, rather than just the bare `SELECT ...` this engine already
/// handled.
let private parenSelect, parenSelectRef = createParserForwardedToRef<SelectStmt, unit> ()
parenSelectRef.Value <-
    attempt (sym "(" >>. parenSelect .>> sym ")")
    <|> (withClause .>>. selectStmtRecord |>> fun (ctes, select) -> { select with Ctes = ctes })
    <|> selectStmtRecord

/// `parenSelect`, paired with whether this particular branch was actually
/// wrapped in parens — `selectOrUnionBranches` needs that to decide whose
/// `ORDER BY`/`LIMIT` a union's final branch contributes (see its doc).
let private parenSelectFlag: Parser<SelectStmt * bool, unit> =
    (attempt (sym "(" >>. parenSelect .>> sym ")") |>> fun s -> s, true)
    <|> (selectStmtRecord |>> fun s -> s, false)

/// `UNION`/`INTERSECT`/`EXCEPT`, each `[ALL|DISTINCT]`, between two
/// `SELECT`s — `ALL` keeps duplicates, the bare operator (or an explicit
/// `DISTINCT`) dedupes, matching MySQL's default for all three. Precedence
/// isn't expressed here: the flat operator list carries it to
/// `Executor.runTopLevelUnion` (see `Ast.SetOp`).
let private unionOp: Parser<SetOp, unit> =
    let all = (keyword "ALL" >>% true) <|> (optional (keyword "DISTINCT") >>% false)

    ((keyword "UNION" >>% OpUnion) <|> (keyword "INTERSECT" >>% OpIntersect) <|> (keyword "EXCEPT" >>% OpExcept))
    .>>. all
    |>> fun (ctor, isAll) -> ctor isAll

/// One `SELECT`, or a `UNION`-chained sequence of them — shared between a
/// top-level statement (`selectOrUnionStmt`) and a derived table's body
/// (`derivedTable`), since MySQL allows `UNION` in both places and each
/// branch may be individually parenthesized (`parenSelectFlag`).
let private selectOrUnionBranches, selectOrUnionBranchesRef =
    createParserForwardedToRef<(SelectStmt * bool) * (SetOp * (SelectStmt * bool)) list, unit> ()

/// A whole set operation wrapped in one more layer of parens and standing on
/// its own — `((SELECT ...) EXCEPT ALL (SELECT ...)) ORDER BY x`, the shape a
/// set operation with a trailing `ORDER BY` has to be written in. The group's
/// branches splice straight into the enclosing list, which is only sound
/// while nothing follows the group: `(A UNION B) INTERSECT C` would flatten
/// into `A UNION B INTERSECT C`, and INTERSECT's tighter binding (see
/// `Ast.SetOp`) then regroups it wrongly. `notFollowedBy unionOp` is what
/// keeps that case out — it falls through and fails the parse rather than
/// answering the wrong grouping.
///
/// ponytail: a real nested set-expression tree in `Ast` is the upgrade path
/// if a workload ever writes an operator after a parenthesized group.
let private parenSetGroup: Parser<(SelectStmt * bool) * (SetOp * (SelectStmt * bool)) list, unit> =
    attempt (
        sym "("
        >>. selectOrUnionBranches
        >>= (fun (first, rest) ->
            if rest.IsEmpty then
                fail "not a parenthesized set operation"
            else
                preturn (first, rest))
        .>> sym ")"
        .>> notFollowedBy unionOp
    )

selectOrUnionBranchesRef.Value <-
    parenSetGroup
    <|> (parenSelectFlag .>>. many (unionOp .>>. parenSelectFlag))

/// A trailing union-level `ORDER BY`/`LIMIT`, tried only once at least one
/// `UNION` branch has parsed — what `MySqlGrammar::compileUnionOrders`/
/// `compileUnionLimit` emit for `->union(...)->orderBy()->limit()`, legal
/// once every branch is individually parenthesized (a bare final branch's
/// own trailing clause already grammatically belongs to the union as a
/// whole, so there's nothing left here to parse in that case — see
/// `combineUnion`'s doc). Independently optional, like a plain `SELECT`'s.
let private unionTailClause: Parser<OrderKey list option * (Expr option * Expr option) option, unit> =
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
    (rest: (SetOp * (SelectStmt * bool)) list)
    ((unionOrderBy, unionLimitOffset): OrderKey list option * (Expr option * Expr option) option)
    : SelectStmt * (SetOp * SelectStmt) list * OrderKey list * Expr option * Expr option =
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
/// body may itself carry a `WITH` clause or `UNION` (Laravel's `unionAll(...)->paginate()`
/// compiles to `SELECT COUNT(*) FROM ((SELECT ...) UNION (SELECT ...)) AS
/// alias`), hence `selectQuery` rather than a bare `selectStmtRecord`.
let private derivedTable: Parser<FromItem, unit> =
    attempt (
        sym "("
        >>. selectQuery
        .>> sym ")"
        .>>. ((keyword "AS" >>. identifier) <|> identifier)
    )
    |>> fun (selectOrUnion, alias) -> FromSubquery(selectOrUnion, alias)

/// `LATERAL (SELECT ...) [AS] alias` — same grammar as `derivedTable`, one
/// `LATERAL` keyword ahead of it, landing on the correlated `FromLateral`
/// case instead (see its doc).
let private lateralTable: Parser<FromItem, unit> =
    attempt (keyword "LATERAL" >>. derivedTable)
    |>> function
        | FromSubquery(body, alias) -> FromLateral(body, alias)
        | other -> other

/// `(VALUES ROW(...), ROW(...)) [AS] alias [(c1, c2, ...)]` — MySQL 8's table
/// value constructor. Desugared into the `UNION ALL` of one-row `SELECT`s it
/// is exactly equivalent to, so it needs no `FromItem` case and no executor
/// path of its own: the union machinery already reconciles the column types
/// across rows the way `VALUES` does, keeps duplicates, and preserves order.
/// Without an explicit column list MySQL names the columns `column_0`,
/// `column_1`, ... (oracle-verified).
let private valuesTable: Parser<FromItem, unit> =
    let rowCtor = keyword "ROW" >>. between (sym "(") (sym ")") (sepBy1 expr (sym ","))

    attempt (sym "(" >>. keyword "VALUES" >>. sepBy1 rowCtor (sym ",") .>> sym ")")
    .>>. ((keyword "AS" >>. identifier) <|> identifier)
    .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
    >>= fun ((rows, alias), colNames) ->
        let width = List.length (List.head rows)

        if rows |> List.exists (fun r -> List.length r <> width) then
            fail "every ROW() of a VALUES table must have the same number of columns"
        else
            let names =
                match colNames with
                | Some ns when List.length ns <> width -> []
                | Some ns -> ns
                | None -> List.init width (sprintf "column_%d")

            if names.IsEmpty then
                fail "the column list of a VALUES table must match its ROW() width"
            else
                let branch (cells: Expr list) : SelectStmt =
                    { Projections = List.map2 (fun name cell -> cell, Some name) names cells
                      IntoVariables = []
                      Distinct = false
                      CalculateFoundRows = false
                      StraightJoin = false
                      From = None
                      Joins = []
                      Where = None
                      GroupBy = []
                      Rollup = false
                      Windows = []
                      Ctes = []
                      Having = None
                      OrderBy = []
                      Limit = None
                      Offset = None
                      Locking = false }

                match rows with
                | [ single ] -> preturn (FromSubquery(PlainSelect(branch single), alias))
                | head :: tail ->
                    preturn (
                        FromSubquery(
                            UnionSelect(branch head, tail |> List.map (fun r -> OpUnion true, branch r), [], None, None),
                            alias
                        )
                    )
                | [] -> fail "VALUES needs at least one ROW()"

/// One `COLUMNS (...)` entry: `name FOR ORDINALITY`, `name TYPE PATH 'path'`
/// with its optional `DEFAULT ... ON EMPTY|ERROR` clauses, or `name TYPE
/// EXISTS PATH 'path'`. `columnType` is the CREATE TABLE/CAST type grammar,
/// so every declarable type works here too, and `NESTED [PATH] 'p' COLUMNS
/// (...)` recurses into this same rule.
let private jsonTableColumn, jsonTableColumnRef = createParserForwardedToRef<JsonTableColumn, unit> ()

let private nestedJsonTableColumn: Parser<JsonTableColumn, unit> =
    attempt (keyword "NESTED" >>. optional (keyword "PATH") >>. stringLit)
    .>> keyword "COLUMNS"
    .>>. between (sym "(") (sym ")") (sepBy1 jsonTableColumn (sym ","))
    |>> fun (path, columns) -> NestedColumns((match path with VString s -> s | _ -> ""), columns)

let private flatJsonTableColumn: Parser<JsonTableColumn, unit> =
    let asText v = match v with VString s -> s | _ -> ""

    // The DEFAULT value is a string of *JSON text*, not a SQL literal:
    // `DEFAULT '"zz"'` substitutes `zz` (the JSON string, unquoted), while
    // the un-JSON `'zz'` is 3141 and the bare number `7` is 1235. Only a
    // JSON string is unquoted; anything else keeps its JSON text for the
    // column's own coercion to consume.
    let jsonDefault (v: Value) : Value option =
        match v with
        | VString s ->
            try
                match Text.Json.Nodes.JsonNode.Parse s with
                | null -> Some VNull
                | node when node.GetValueKind() = Text.Json.JsonValueKind.String -> Some(VString(node.GetValue<string>()))
                | node -> Some(VString(node.ToJsonString()))
            with _ ->
                None
        | _ -> None

    // `DEFAULT <json-text> ON EMPTY|ERROR`, in MySQL's fixed ON EMPTY-then-ON
    // ERROR order; `NULL ON EMPTY|ERROR` restates the default, so it parses
    // to the same `None` an absent clause gives.
    let onClause (which: string) : Parser<JsonTableAction, unit> =
        opt (
            attempt (
                ((keyword "DEFAULT" >>. literalValue
                  >>= fun v ->
                      match jsonDefault v with
                      | Some d -> preturn (JsonDefault d)
                      | None -> fail "DEFAULT for a JSON_TABLE column must be a string of valid JSON text")
                 <|> (keyword "NULL" >>% JsonNull)
                 <|> (keyword "ERROR" >>% JsonError))
                .>> keyword "ON"
                .>> keyword which
            )
        )
        |>> Option.defaultValue JsonNull

    identifier
    >>= fun name ->
        (keyword "FOR" >>. keyword "ORDINALITY" >>% ForOrdinality name)
        <|> (columnType
             >>= fun ty ->
                 (attempt (keyword "EXISTS" >>. keyword "PATH") >>. stringLit
                  |>> fun p -> ExistsColumn(name, ty, asText p))
                 <|> (keyword "PATH" >>. stringLit .>>. onClause "EMPTY" .>>. onClause "ERROR"
                      |>> fun ((p, onEmpty), onError) -> PathColumn(name, ty, asText p, onEmpty, onError)))

jsonTableColumnRef.Value <- nestedJsonTableColumn <|> flatJsonTableColumn

/// `JSON_TABLE(expr, 'path' COLUMNS (col, ...)) [AS] alias` — the alias is
/// required (MySQL's 3667 "Every table function must have an alias"), same
/// grammar shape as `derivedTable`'s mandatory alias. The `attempt` only
/// spans `JSON_TABLE (`, so a real table that happens to be named
/// `json_table` still parses through `tableRef` when no paren follows.
let private jsonTable: Parser<FromItem, unit> =
    attempt (keyword "JSON_TABLE" >>. sym "(")
    >>. expr
    .>> sym ","
    .>>. stringLit
    .>> keyword "COLUMNS"
    .>> sym "("
    .>>. sepBy1 jsonTableColumn (sym ",")
    .>> sym ")"
    .>> sym ")"
    .>>. ((keyword "AS" >>. identifier) <|> identifier)
    |>> fun (((source, path), columns), alias) ->
        FromJsonTable(
            source,
            (match path with
             | VString s -> s
             | _ -> ""),
            columns,
            alias
        )

let private fromItem: Parser<FromItem, unit> =
    lateralTable <|> derivedTable <|> valuesTable <|> jsonTable <|> (tableRef |>> FromTable)

/// `LEFT` and `RIGHT JOIN` require `ON` or `USING`. MySQL treats an
/// unqualified `[INNER] JOIN` without either clause as a Cartesian product.
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
/// for `SELECT`/`UPDATE`/`DELETE` alike with no executor change; a real
/// table or a `JSON_TABLE(...)` (MySQL's correlated comma-join form, `FROM
/// t, JSON_TABLE(t.doc, ...) jt`) or a `LATERAL (SELECT ...)` (its
/// correlated derived-table form, which is what the comma is *for* here)
/// can follow the comma, but not a plain derived `(SELECT ...)`.
let private commaJoinClause: Parser<Join, unit> =
    attempt (sym "," >>. (lateralTable <|> jsonTable <|> (tableRef |>> FromTable)))
    |>> fun table -> { Kind = CrossJoin; Table = table; On = Lit(VInt 1L); Using = [] }

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
             | InnerJoin ->
                 (keyword "ON" >>. expr |>> fun onExpr -> { Kind = kind; Table = table; On = onExpr; Using = [] })
                 <|> (usingClause |>> fun cols -> { Kind = kind; Table = table; On = Lit(VInt 1L); Using = cols })
                 <|> preturn { Kind = kind; Table = table; On = Lit(VInt 1L); Using = [] }
             | LeftJoin
             | RightJoin ->
                 (keyword "ON" >>. expr |>> fun onExpr -> { Kind = kind; Table = table; On = onExpr; Using = [] })
                 <|> (usingClause |>> fun cols -> { Kind = kind; Table = table; On = Lit(VInt 1L); Using = cols })
             | CrossJoin -> fail "CROSS JOIN is parsed separately")

/// `GROUP BY expr, ... [WITH ROLLUP]` — the flag rides along with the keys
/// since it only ever qualifies them.
let private groupByClause: Parser<Expr list * bool, unit> =
    keyword "GROUP" >>. keyword "BY" >>. sepBy1 expr (sym ",")
    .>>. opt (attempt (keyword "WITH" >>. keyword "ROLLUP"))
    |>> fun (keys, rollup) -> keys, rollup.IsSome

/// `WINDOW w AS (...), w2 AS (...)` — named window definitions an `OVER w`
/// resolves against at execution time.
let private windowClause: Parser<(string * WindowSpec) list, unit> =
    keyword "WINDOW"
    >>. sepBy1 (identifier .>> keyword "AS" .>>. between (sym "(") (sym ")") windowSpecBody) (sym ",")

let private havingClause: Parser<Expr, unit> = keyword "HAVING" >>. expr

/// `FOR UPDATE` / `FOR SHARE` / `LOCK IN SHARE MODE` — parsed and discarded;
/// see the `Ast.SelectStmt.Locking` doc for why there's nothing else to do
/// with it.
let private lockClause: Parser<unit, unit> =
    (keyword "FOR"
     >>. (keyword "UPDATE" <|> (keyword "SHARE" >>% ()))
     >>. optional (keyword "OF" >>. sepBy1 identifier (sym ","))
     >>. optional ((keyword "NOWAIT" >>% ()) <|> (keyword "SKIP" >>. keyword "LOCKED" >>% ())))
    <|> (keyword "LOCK" >>. keyword "IN" >>. keyword "SHARE" >>. keyword "MODE" >>% ())

let private selectModifiers: Parser<bool * bool * bool, unit> =
    let duplicateMode =
        opt (choice [ keyword "DISTINCT" >>% true; keyword "DISTINCTROW" >>% true; keyword "ALL" >>% false ])

    let optimizerHint =
        choice
            [ keyword "SQL_CALC_FOUND_ROWS" >>% (true, false)
              keyword "STRAIGHT_JOIN" >>% (false, true)
              keyword "HIGH_PRIORITY" >>% (false, false)
              keyword "SQL_SMALL_RESULT" >>% (false, false)
              keyword "SQL_BIG_RESULT" >>% (false, false)
              keyword "SQL_BUFFER_RESULT" >>% (false, false)
              keyword "SQL_NO_CACHE" >>% (false, false) ]

    duplicateMode .>>. many optimizerHint
    |>> fun (distinct, modifiers) ->
        Option.defaultValue false distinct,
        (modifiers |> List.exists fst),
        (modifiers |> List.exists snd)

let private selectHead =
    selectModifiers
    .>>. sepBy1 projection (sym ",")
    .>>. opt (keyword "INTO" >>. sepBy1 userVariableTarget (sym ","))
    |>> fun (((distinct, calculateFoundRows, straightJoin), projections), intoVariables) ->
        distinct, calculateFoundRows, straightJoin, projections, (intoVariables |> Option.defaultValue [])

selectStmtRecordRef.Value <-
    (keyword "SELECT" >>. selectHead
     .>>. opt (keyword "FROM" >>. fromItem .>>. many joinClause)
     .>>. opt (keyword "WHERE" >>. expr)
     .>>. opt groupByClause
     .>>. opt havingClause
     .>>. opt windowClause
     .>>. opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ","))
     .>>. opt limitClause
     .>>. opt lockClause)
    |>> fun (((((((((distinct, calculateFoundRows, straightJoin, projs, intoVariables), fromAndJoins), where), groupBy), having), windows), orderBy), limitOffset), locking) ->
        let limit, offset = limitOffset |> Option.defaultValue (None, None)
        let from = fromAndJoins |> Option.map fst
        let joins = fromAndJoins |> Option.map snd |> Option.defaultValue []

        { Projections = projs
          IntoVariables = intoVariables
          Distinct = distinct
          CalculateFoundRows = calculateFoundRows
          StraightJoin = straightJoin
          From = from
          Joins = joins
          Where = where
          GroupBy = groupBy |> Option.map fst |> Option.defaultValue []
          Rollup = groupBy |> Option.map snd |> Option.defaultValue false
          Windows = windows |> Option.defaultValue []
          Ctes = []
          Having = having
          OrderBy = orderBy |> Option.defaultValue []
          Limit = limit
          Offset = offset
          Locking = locking.IsSome }

selectQueryRef.Value <-
    opt withClause .>>. selectOrUnionBranches
    >>= fun (ctes, (first, rest)) ->
        let first =
            match ctes with
            | Some ctes -> { fst first with Ctes = ctes }, snd first
            | None -> first

        match rest with
        | [] -> preturn (PlainSelect(fst first))
        | _ -> unionTailClause |>> fun tail -> combineUnion first rest tail |> UnionSelect

let private expressionSelect =
    function
    | PlainSelect select -> select
    | (UnionSelect _ as body) ->
        { Projections = [ Star None, None ]
          IntoVariables = []
          Distinct = false
          CalculateFoundRows = false
          StraightJoin = false
          From = Some(FromLateral(body, "__fsdb_set_expression"))
          Joins = []
          Where = None
          GroupBy = []
          Rollup = false
          Windows = []
          Ctes = []
          Having = None
          OrderBy = []
          Limit = None
          Offset = None
          Locking = false }

selectWithCtesRef.Value <-
    selectQuery |>> expressionSelect

/// A single `SELECT`, or a `UNION`-chained sequence of them
/// (`selectOrUnionBranches`, shared with `derivedTable` — see its doc). Each
/// branch is a full `selectStmtRecord` (so it can itself carry a trailing
/// `ORDER BY`/`LIMIT`/lock clause), and a genuine union-level clause
/// (`unionTailClause`) is tried once at least one `UNION` branch parsed —
/// `combineUnion` picks between the two per branch/clause.
let private selectOrUnionStmt: Parser<Statement, unit> =
    selectQuery
    |>> function
        | PlainSelect select -> Select select
        | UnionSelect(first, rest, orderBy, limit, offset) -> Union(first, rest, orderBy, limit, offset)

let private createTableAs: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "TABLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. qualifiedTableName
     .>> optional (keyword "AS")
     .>>. selectOrUnionStmt)
    |>> fun ((ifNotExists, name), query) -> CreateTableAs(name, query, ifNotExists)

let private querySelect projections from orderBy limit offset =
    { Projections = projections
      IntoVariables = []
      Distinct = false
      CalculateFoundRows = false
      StraightJoin = false
      From = from
      Joins = []
      Where = None
      GroupBy = []
      Rollup = false
      Windows = []
      Ctes = []
      Having = None
      OrderBy = orderBy
      Limit = limit
      Offset = offset
      Locking = false }

let private queryTail =
    opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ","))
    .>>. opt limitClause
    |>> fun (orderBy, limitOffset) ->
        let limit, offset = limitOffset |> Option.defaultValue (None, None)
        orderBy |> Option.defaultValue [], limit, offset

let private tableQueryStmt: Parser<Statement, unit> =
    keyword "TABLE" >>. tableRef .>>. queryTail
    |>> fun (table, (orderBy, limit, offset)) ->
        Select(querySelect [ Star None, None ] (Some(FromTable table)) orderBy limit offset)

let private valuesQueryStmt: Parser<Statement, unit> =
    let row = keyword "ROW" >>. between (sym "(") (sym ")") (sepBy1 expr (sym ","))

    keyword "VALUES" >>. sepBy1 row (sym ",") .>>. queryTail
    >>= fun (rows, (orderBy, limit, offset)) ->
        let width = rows.Head.Length

        if rows |> List.exists (fun values -> values.Length <> width) then
            fail "all VALUES rows must have the same number of columns"
        else
            let selectOf values =
                values
                |> List.mapi (fun index value -> value, Some(sprintf "column_%d" index))
                |> fun projections -> querySelect projections None [] None None

            match rows with
            | [ values ] -> preturn (Select { selectOf values with OrderBy = orderBy; Limit = limit; Offset = offset })
            | first :: rest ->
                preturn (Union(selectOf first, rest |> List.map (fun values -> OpUnion true, selectOf values), orderBy, limit, offset))
            | [] -> fail "VALUES requires at least one row"

/// An assignment target, `col` or `table.col` (Laravel's `touch()` qualifies
/// `updated_at` with the table name even in a single-table `UPDATE`) — the
/// table part only matters once there's more than one table in scope (a
/// multi-table `UPDATE ... JOIN`); a single-table `UPDATE` still parses one
/// the same way it always could, `Executor` just never needs it there.
let private assignment: Parser<Assignment, unit> =
    ((identifier .>>. opt (sym "." >>. qualifiedIdentifier)) .>> sym "=") .>>. expr
    |>> function
        | (first, None), value -> { Table = None; Column = first; Value = value }
        | (first, Some col), value -> { Table = Some first; Column = col; Value = value }

/// `[ORDER BY ...] [LIMIT n]`, legal only on a single-table `UPDATE`/`DELETE`
/// (`joins` empty) — matching MySQL's own grammar restriction, a
/// multi-table form simply never attempts to parse this trailing clause, so
/// leftover `ORDER BY`/`LIMIT` tokens after a JOIN'd `UPDATE`/`DELETE` fall
/// through to `Parser.parse`'s top-level `eof` and surface as an ordinary
/// syntax error — the same 1064 real MySQL gives that combination.
let private singleTableOrderLimit (joins: Join list) : Parser<OrderKey list * Expr option, unit> =
    if joins.IsEmpty then
        (opt (keyword "ORDER" >>. keyword "BY" >>. sepBy1 orderKey (sym ",")) |>> Option.defaultValue [])
        .>>. opt (keyword "LIMIT" >>. limitTok)
    else
        preturn ([], None)

/// `UPDATE t1 [[AS] a] [JOIN ...] SET assignments [WHERE ...] [ORDER BY ...]
/// [LIMIT ...]`.
let private updateStmt: Parser<Statement, unit> =
    (keyword "UPDATE" >>. (opt (keyword "IGNORE") |>> Option.isSome) .>>. tableRef .>>. many joinClause .>> keyword "SET" .>>. sepBy1 assignment (sym ","))
    >>= fun (((ignoreErrors, from), joins), assignments) ->
        opt (keyword "WHERE" >>. expr) .>>. singleTableOrderLimit joins
        |>> fun (where, (orderBy, limit)) ->
            Update
                { Ctes = []
                  Ignore = ignoreErrors
                  From = from
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
                { Ctes = []
                  Targets = targets
                  From = from
                  Joins = joins
                  Where = where
                  OrderBy = orderBy
                  Limit = limit }

let private withDmlStmt: Parser<Statement, unit> =
    withClause .>>. (updateStmt <|> deleteStmt)
    |>> fun (ctes, statement) ->
        match statement with
        | Update update -> Update { update with Ctes = ctes }
        | Delete delete -> Delete { delete with Ctes = ctes }
        | _ -> statement

/// `EXPLAIN [FORMAT=TRADITIONAL|JSON] stmt` — MySQL also accepts `DESCRIBE`/
/// `DESC` as synonyms when just describing a table's columns (not a
/// statement), out of scope here; this only covers the `EXPLAIN stmt` form.
let private explainStmt: Parser<Statement, unit> =
    let format =
        keyword "FORMAT"
        >>. sym "="
        >>. ((keyword "TRADITIONAL" >>% ExplainTraditional)
             <|> (keyword "JSON" >>% ExplainJson)
             <|> (keyword "TREE" >>% ExplainTree))

    (((keyword "EXPLAIN"
       >>. ((keyword "ANALYZE" >>% Some ExplainAnalyze) <|> opt (attempt format)))
      |>> Option.defaultValue ExplainTraditional)
     <|> (keyword "DESCRIBE" >>% ExplainTraditional)
     <|> (keyword "DESC" >>% ExplainTraditional))
    .>>. statement
    |>> Explain

// ---------------------------------------------------------------------------
// CREATE USER / DROP USER / ALTER USER — account DDL over `mysql.user`.
// ---------------------------------------------------------------------------

/// `'name'[@'host']` — name and host each an identifier or quoted string
/// (`'bob'@'%'`, `bob@localhost`, bare `bob`); host defaults to `'%'`.
let private userRef: Parser<string * string, unit> =
    identOrString .>>. (opt (sym "@" >>. identOrString) |>> Option.defaultValue "%")

let private identifiedBy: Parser<string, unit> =
    keyword "IDENTIFIED" >>. keyword "BY"
    >>. (stringLit |>> (function VString s -> s | _ -> ""))

let private createUserStmt: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "USER"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 (userRef .>>. opt identifiedBy) (sym ",")
     .>>. opt ((keyword "ACCOUNT" >>. keyword "LOCK" >>% true) <|> (keyword "ACCOUNT" >>. keyword "UNLOCK" >>% false)))
    |>> fun ((ifNotExists, users), locked) ->
        CreateUser(users |> List.map (fun ((n, h), pw) -> n, h, pw), ifNotExists, Option.defaultValue false locked)

let private dropUserStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "USER"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 userRef (sym ","))
    |>> fun (ifExists, users) -> DropUser(users, ifExists)

let private renameUserStmt: Parser<Statement, unit> =
    keyword "RENAME" >>. keyword "USER"
    >>. sepBy1 (userRef .>> keyword "TO" .>>. userRef) (sym ",")
    |>> RenameUser

let private alterUserStmt: Parser<Statement, unit> =
    (keyword "ALTER" >>. keyword "USER"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. userRef
     .>>. identifiedBy)
    |>> fun ((ifExists, (name, host)), pw) -> AlterUser(name, host, pw, ifExists)

let private createRoleStmt: Parser<Statement, unit> =
    (keyword "CREATE" >>. keyword "ROLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "NOT" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 userRef (sym ","))
    |>> fun (ifNotExists, users) -> CreateRole(users, ifNotExists)

let private dropRoleStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "ROLE"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 userRef (sym ","))
    |>> fun (ifExists, users) -> DropRole(users, ifExists)

// ---------------------------------------------------------------------------
// CREATE TRIGGER / DROP TRIGGER.
// ---------------------------------------------------------------------------

/// The trigger body: everything after `FOR EACH ROW` to end of input,
/// captured as raw text (validated by parsing in the executor, not here —
/// the AST carries the text once, see `Ast.CreateTrigger`). A trailing `;`
/// belongs to the outer statement, not the body, so it's trimmed off.
let private createTriggerStmt: Parser<Statement, unit> =
    let timing = (keyword "BEFORE" >>% Before) <|> (keyword "AFTER" >>% After)
    let event =
        (keyword "INSERT" >>% TriggerInsert)
        <|> (keyword "UPDATE" >>% TriggerUpdate)
        <|> (keyword "DELETE" >>% TriggerDelete)

    let order =
        opt (
            attempt (keyword "FOLLOWS" >>. identifier |>> Follows)
            <|> (keyword "PRECEDES" >>. identifier |>> Precedes)
        )

    (keyword "CREATE" >>. keyword "TRIGGER" >>. identifier .>>. timing .>>. event
     .>> keyword "ON"
     .>>. qualifiedTableName
     .>> keyword "FOR"
     .>> keyword "EACH"
     .>> keyword "ROW"
     .>>. order
     .>>. manyChars anyChar)
    |>> fun (((((name, timing), event), table), order), body) ->
        CreateTrigger(name, timing, event, table, order, body.Trim().TrimEnd(';').Trim())

let private dropTriggerStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "TRIGGER"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. identifier)
    |>> fun (ifExists, name) -> DropTrigger(name, ifExists)

let private setTriggerNewStmt: Parser<Statement, unit> =
    (keyword "SET" >>. keyword "NEW" >>. sym "." >>. identifier .>> sym "=" .>>. expr)
    |>> SetTriggerNew

// ---------------------------------------------------------------------------
// CREATE VIEW / DROP VIEW.
// ---------------------------------------------------------------------------

let private createViewStmt: Parser<Statement, unit> =
    (keyword "CREATE"
     >>. (opt (attempt (keyword "OR" >>. keyword "REPLACE")) |>> Option.isSome)
     .>> keyword "VIEW"
     .>>. qualifiedTableName
     .>>. opt (between (sym "(") (sym ")") (sepBy1 identifier (sym ",")))
     .>> keyword "AS"
     .>>. manyChars anyChar)
    |>> fun (((orReplace, name), columns), definition) ->
        CreateView(name, columns |> Option.defaultValue [], definition.Trim().TrimEnd(';').Trim(), orReplace)

let private dropViewStmt: Parser<Statement, unit> =
    (keyword "DROP" >>. keyword "VIEW"
     >>. (opt (attempt (keyword "IF" >>. keyword "EXISTS")) |>> Option.isSome)
     .>>. sepBy1 qualifiedTableName (sym ","))
    |>> fun (ifExists, names) -> DropView(names, ifExists)

// ---------------------------------------------------------------------------
// GRANT / REVOKE
// ---------------------------------------------------------------------------

/// One privilege name, normalized to the canonical uppercase spelling
/// `Auth.staticPrivileges` uses. Each name compiles to "match its words in
/// order"; list order matters — multi-word forms come before their prefix
/// word (`CREATE TEMPORARY TABLES` before `CREATE`) so nothing half-matches.
let private privilegeName: Parser<string, unit> =
    let ofName (name: string) : Parser<string, unit> =
        attempt (name.Split ' ' |> Array.map keyword |> Array.reduce (>>.) >>% name)

    let names =
        [ "GRANT OPTION"
          "CREATE TEMPORARY TABLES"
          "CREATE VIEW"
          "CREATE ROUTINE"
          "CREATE USER"
          "CREATE ROLE"
          "CREATE TABLESPACE"
          "CREATE"
          "ALTER ROUTINE"
          "ALTER"
          "SHOW DATABASES"
          "SHOW VIEW"
          "DROP ROLE"
          "DROP"
          "LOCK TABLES"
          "REPLICATION SLAVE"
          "REPLICATION CLIENT"
          "SELECT"
          "INSERT"
          "UPDATE"
          "DELETE"
          "RELOAD"
          "SHUTDOWN"
          "PROCESS"
          "FILE"
          "REFERENCES"
          "INDEX"
          "SUPER"
          "EXECUTE"
          "EVENT"
          "TRIGGER"
          "USAGE" ]

    choice ((attempt (keyword "ALL" >>. optional (keyword "PRIVILEGES") >>% "ALL")) :: (names |> List.map ofName))

/// `ON *.* | db.* | db.tbl | tbl` — see `Ast.Grant`'s doc for the encoding.
let private grantLevel: Parser<string option * string option, unit> =
    choice
        [ attempt (sym "*" >>. sym "." >>. sym "*") >>% (None, None)
          attempt (identOrString .>> sym "." .>> sym "*") |>> fun db -> Some db, None
          attempt (identOrString .>> sym "." .>>. identOrString) |>> fun (db, t) -> Some db, Some t
          identOrString |>> fun t -> None, Some t ]

let private grantStmt: Parser<Statement, unit> =
    (keyword "GRANT" >>. sepBy1 privilegeName (sym ",")
     .>> keyword "ON"
     .>>. grantLevel
     .>> keyword "TO"
     .>>. sepBy1 userRef (sym ",")
     .>>. (opt (keyword "WITH" >>. keyword "GRANT" >>. keyword "OPTION") |>> Option.isSome))
    |>> fun (((privs, level), users), wgo) -> Grant(privs, level, users, wgo)

let private revokeStmt: Parser<Statement, unit> =
    (keyword "REVOKE" >>. sepBy1 privilegeName (sym ",")
     .>> keyword "ON"
     .>>. grantLevel
     .>> keyword "FROM"
     .>>. sepBy1 userRef (sym ","))
    |>> fun ((privs, level), users) -> Revoke(privs, level, users)

/// `CREATE TABLE` vs. `CREATE INDEX` and `DROP TABLE` vs. `DROP INDEX` share
/// a leading keyword before diverging, so those four need `attempt` to
/// backtrack cleanly between alternatives; every other statement starts on
/// a keyword none of the others do, so `choice` picks the right one off
/// just that first token without needing to backtrack at all.
statementRef.Value <-
    choice
        [ attempt createUserStmt
          attempt createRoleStmt
          attempt renameUserStmt
          attempt createTriggerStmt
          attempt createViewStmt
          attempt createDatabaseStmt
          attempt createTableAs
          attempt createTableLike
          attempt createTable
          attempt createIndexStmt
          attempt dropUserStmt
          attempt dropRoleStmt
          attempt dropTriggerStmt
          attempt dropViewStmt
          attempt dropDatabaseStmt
          attempt dropTable
          dropIndexStmt
          truncateTable
          checksumTableStmt
          doStmt
          insertStmt
          replaceStmt
          tableQueryStmt
          valuesQueryStmt
          attempt withDmlStmt
          selectOrUnionStmt
          setTriggerNewStmt
          updateStmt
          deleteStmt
          attempt alterUserStmt
          attempt alterTableStmt
          alterDatabaseStmt
          renameTableStmt
          grantStmt
          revokeStmt
          explainStmt ]
    <?> "statement"

/// Parses one SQL statement, with an optional trailing `;`. Session-variable
/// forms like `SELECT @@version` are deliberately out of scope — those are
/// handled by `QueryHandler` before reaching this parser.
let parse (sql: string) : Result<Statement, string> =
    placeholderCounterLocal.Value <- 0
    exprDepth.Value <- 0
    let sql = stripVersionComments sql
    let full = ws >>. statement .>> opt (sym ";") .>> eof

    // `open FParsec` brings its own `Ok`/`Error` (from `Reply`'s status) into
    // scope, shadowing `Result`'s — qualify to get the ones this signature means.
    //
    // Belt-and-braces around `numberLit`'s overflow guard above: no parser
    // exception should ever be able to escape as a raw .NET exception and
    // drop the caller's connection — a syntax error is always a clean
    // `Result.Error`, however it originates.
    try
        if exceedsParenthesisDepthLimit sql then
            Result.Error "expression nested too deeply"
        else
            match run full sql with
            | Success(stmt, _, _) -> Result.Ok stmt
            | Failure(msg, _, _) -> Result.Error msg
    with ex ->
        Result.Error ex.Message

/// Parses a `LOAD DATA LOCAL INFILE` command without consuming its later
/// client-to-server data stream.
let parseLocalLoad (sql: string) : Result<LocalLoad, string> =
    try
        match run (ws >>. localLoadData .>> opt (sym ";") .>> eof) sql with
        | Success(load, _, _) ->
            let validSeparator value = value = "" || value.Length = 1

            if
                validSeparator load.FieldTerminator
                && validSeparator load.LineTerminator
                && (load.EnclosedBy |> Option.forall validSeparator)
                && (load.Escape |> Option.forall validSeparator)
            then
                Result.Ok load
            else
                Result.Error "LOAD DATA delimiters must be empty or one character"
        | Failure(message, _, _) -> Result.Error message
    with ex ->
        Result.Error ex.Message

/// Splits a COM_QUERY batch at statement delimiters outside literals,
/// comments, and compound trigger bodies. The parser still validates each
/// returned statement separately.
let splitStatements (sql: string) : Result<string list, string> =
    let sql = stripVersionComments sql
    let statements = ResizeArray<string>()
    let mutable start = 0
    let mutable i = 0
    let mutable quote: char option = None
    let mutable blockComment = false
    let mutable lineComment = false
    let mutable triggerCompoundDepth = 0

    let isWordStart c = Char.IsLetter c || c = '_'
    let isWordPart c = Char.IsLetterOrDigit c || c = '_'

    let startsTriggerCompound at =
        let prefix = sql.[start .. at - 1]

        System.Text.RegularExpressions.Regex.IsMatch(
            prefix,
            @"^\s*CREATE\s+TRIGGER\b[\s\S]*\bFOR\s+EACH\s+ROW(?:\s+(?:FOLLOWS|PRECEDES)\s+(?:`(?:``|[^`])+`|[A-Za-z_][A-Za-z0-9_$]*))?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        )

    let addStatement stop =
        if stop > start then
            let statement = sql.[start .. stop - 1].Trim()

            if not (isBlank statement) then
                statements.Add statement

    while i < sql.Length do
        match quote with
        | Some q when sql.[i] = '\\' && q <> '`' && i + 1 < sql.Length -> i <- i + 2
        | Some q when sql.[i] = q && i + 1 < sql.Length && sql.[i + 1] = q -> i <- i + 2
        | Some q when sql.[i] = q ->
            quote <- None
            i <- i + 1
        | Some _ -> i <- i + 1
        | None when blockComment ->
            if sql.[i] = '*' && i + 1 < sql.Length && sql.[i + 1] = '/' then
                blockComment <- false
                i <- i + 2
            else
                i <- i + 1
        | None when lineComment ->
            if sql.[i] = '\n' || sql.[i] = '\r' then
                lineComment <- false

            i <- i + 1
        | None when sql.[i] = '\'' || sql.[i] = '"' || sql.[i] = '`' ->
            quote <- Some sql.[i]
            i <- i + 1
        | None when sql.[i] = '#' ->
            lineComment <- true
            i <- i + 1
        | None when
            sql.[i] = '-'
            && i + 1 < sql.Length
            && sql.[i + 1] = '-'
            && (i + 2 = sql.Length || Char.IsWhiteSpace sql.[i + 2])
            ->
            lineComment <- true
            i <- i + 2
        | None when sql.[i] = '/' && i + 1 < sql.Length && sql.[i + 1] = '*' ->
            blockComment <- true
            i <- i + 2
        | None when isWordStart sql.[i] ->
            let mutable stop = i + 1

            while stop < sql.Length && isWordPart sql.[stop] do
                stop <- stop + 1

            let word = sql.[i .. stop - 1]

            if word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) then
                if triggerCompoundDepth > 0 || startsTriggerCompound i then
                    triggerCompoundDepth <- triggerCompoundDepth + 1
            elif triggerCompoundDepth > 0 && word.Equals("CASE", StringComparison.OrdinalIgnoreCase) then
                triggerCompoundDepth <- triggerCompoundDepth + 1
            elif triggerCompoundDepth > 0 && word.Equals("END", StringComparison.OrdinalIgnoreCase) then
                triggerCompoundDepth <- triggerCompoundDepth - 1

            i <- stop
        | None when sql.[i] = ';' && triggerCompoundDepth = 0 ->
            addStatement i
            start <- i + 1
            i <- i + 1
        | None -> i <- i + 1

    match quote, blockComment with
    | Some _, _ -> Result.Error "unterminated quoted string"
    | None, true -> Result.Error "unterminated comment"
    | None, false ->
        addStatement sql.Length
        Result.Ok(List.ofSeq statements)

/// Parses one standalone expression for persisted schema objects such as
/// CHECK constraints. It shares the statement parser's placeholder/depth
/// guards so damaged catalog text fails as a normal schema error rather
/// than escaping through the query worker.
let parseExpression (sql: string) : Result<Expr, string> =
    placeholderCounterLocal.Value <- 0
    exprDepth.Value <- 0
    let sql = stripVersionComments sql

    try
        if exceedsParenthesisDepthLimit sql then
            Result.Error "expression nested too deeply"
        else
            match run (ws >>. expr .>> eof) sql with
            | Success(expression, _, _) -> Result.Ok expression
            | Failure(message, _, _) -> Result.Error message
    with ex ->
        Result.Error ex.Message

/// Parses the user-defined-variable target at the front of a `SET`
/// assignment. The right-hand side remains source text because `SET` has
/// its own literal rules before ordinary expression evaluation.
let parseUserVariableSetAssignment (sql: string) : Result<UserVariableRef * string, string> =
    let assignment =
        userVariableTarget
        .>> (attempt (sym ":=") <|> sym "=")
        .>>. manyChars anyChar

    match run (ws >>. assignment .>> eof) sql with
    | Success(result, _, _) -> Result.Ok result
    | Failure(message, _, _) -> Result.Error message
