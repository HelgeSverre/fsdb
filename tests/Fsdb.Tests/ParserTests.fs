module Fsdb.Tests.ParserTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Parser

/// Parses `sql` and fails the test with the parse error if it doesn't
/// succeed, so every happy-path test reads as a plain AST comparison.
let private parseOk (sql: string) : Statement =
    match parse sql with
    | Ok stmt -> stmt
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg

let private col name = Col name

/// Builds a `Select` statement from a flat positional tuple, so every test
/// below reads as a plain AST comparison instead of a record literal per
/// case; `from` is still a bare table name string here since none of these
/// tests exercise a qualified name or alias.
let private mkSelect
    (projections: Projection list, from: string option, where: Expr option, orderBy: OrderKey list, limit: int option, offset: int option)
    : Statement =
    Select
        { Projections = projections
          Distinct = false
          From = from |> Option.map (fun t -> FromTable { Database = None; Table = t; Alias = None })
          Joins = []
          Where = where
          GroupBy = []
          Having = None
          OrderBy = orderBy
          Limit = limit
          Offset = offset
          Locking = false }

let tests =
    testList
        "parser"
        [ testList
              "SELECT"
              [ testCase "SELECT * FROM t"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t")
                        (mkSelect([ Star None, None ], Some "t", None, [], None, None))
                        "select star"

                testCase "SELECT DISTINCT col FROM t sets Distinct"
                <| fun _ ->
                    match parseOk "SELECT DISTINCT name FROM t" with
                    | Select { Distinct = true; Projections = [ Col "name", None ] } -> ()
                    | other -> failtestf "expected Distinct = true, got %A" other

                testCase "FROM db.table AS alias parses a qualified, aliased TableRef"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM information_schema.tables AS t")
                        (Select
                            { Projections = [ Star None, None ]
                              Distinct = false
                              From = Some(FromTable { Database = Some "information_schema"; Table = "tables"; Alias = Some "t" })
                              Joins = []
                              Where = None
                              GroupBy = []
                              Having = None
                              OrderBy = []
                              Limit = None
                              Offset = None
                              Locking = false })
                        "qualified aliased table ref"

                testCase "FROM t x: alias without AS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t x")
                        (Select
                            { Projections = [ Star None, None ]
                              Distinct = false
                              From = Some(FromTable { Database = None; Table = "t"; Alias = Some "x" })
                              Joins = []
                              Where = None
                              GroupBy = []
                              Having = None
                              OrderBy = []
                              Limit = None
                              Offset = None
                              Locking = false })
                        "bare alias"

                testCase "SELECT without FROM"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1+1")
                        (mkSelect([ BinOp(Add, Lit(VInt 1L), Lit(VInt 1L)), None ], None, None, [], None, None))
                        "select without from"

                testCase "SELECT projections with expr AS alias, qualified column, and bare column"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a, t.b, a+1 AS c FROM t")
                        (mkSelect(
                            [ col "a", None
                              QualifiedCol("t", "b"), None
                              BinOp(Add, col "a", Lit(VInt 1L)), Some "c" ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "projections"

                testCase "SELECT projections with an implicit alias (no AS)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1 x, a+1 total FROM t")
                        (mkSelect(
                            [ Lit(VInt 1L), Some "x"
                              BinOp(Add, col "a", Lit(VInt 1L)), Some "total" ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "bare aliases, no AS"

                testCase "TIMESTAMPDIFF/TIMESTAMPADD take an unquoted unit keyword"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT TIMESTAMPDIFF(MONTH, a, b) FROM t")
                        (mkSelect(
                            [ FuncCall("TIMESTAMPDIFF", [ Lit(VString "MONTH"); col "a"; col "b" ]), None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "unquoted MONTH parses as the unit, not a column reference"

                    Expect.equal
                        (parseOk "SELECT TIMESTAMPADD(DAY, 1, a) FROM t")
                        (mkSelect(
                            [ FuncCall("TIMESTAMPADD", [ Lit(VString "DAY"); Lit(VInt 1L); col "a" ]), None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "TIMESTAMPADD too"

                testCase "SELECT t.* is Star(Some \"t\")"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT t.* FROM t")
                        (mkSelect([ Star(Some "t"), None ], Some "t", None, [], None, None))
                        "qualified star"

                testCase "COUNT(*) shape"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT COUNT(*) FROM t")
                        (mkSelect([ FuncCall("COUNT", [ Star None ]), None ], Some "t", None, [], None, None))
                        "count star"

                testCase "function call with multiple args and no args"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT CONCAT(a, b), NOW() FROM t")
                        (mkSelect(
                            [ FuncCall("CONCAT", [ col "a"; col "b" ]), None; FuncCall("NOW", []), None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "func calls"

                testCase "IF(...) parses as a function call even though IF is a reserved keyword"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT IF(1, 2, 3)")
                        (mkSelect([ FuncCall("IF", [ Lit(VInt 1L); Lit(VInt 2L); Lit(VInt 3L) ]), None ], None, None, [], None, None))
                        "if as function call"

                testCase "WHERE clause"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t WHERE a = 1")
                        (mkSelect([ Star None, None ], Some "t", Some(BinOp(Eq, col "a", Lit(VInt 1L))), [], None, None))
                        "where"

                testCase "ORDER BY with explicit and default direction"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t ORDER BY a DESC, b")
                        (mkSelect([ Star None, None ], Some "t", None, [ col "a", Desc; col "b", Asc ], None, None))
                        "order by"

                testCase "LIMIT n"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 10")
                        (mkSelect([ Star None, None ], Some "t", None, [], Some 10, None))
                        "limit n"

                testCase "LIMIT n OFFSET m"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 10 OFFSET 5")
                        (mkSelect([ Star None, None ], Some "t", None, [], Some 10, Some 5))
                        "limit offset"

                testCase "LIMIT m, n means offset m, count n"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 5, 10")
                        (mkSelect([ Star None, None ], Some "t", None, [], Some 10, Some 5))
                        "limit comma form"

                testCase "LIMIT 18446744073709551615 (2^64-1, the 'no limit' idiom) parses instead of a 1064 error"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT id FROM t LIMIT 18446744073709551615 OFFSET 5")
                        (mkSelect([ col "id", None ], Some "t", None, [], Some System.Int32.MaxValue, Some 5))
                        "clamped to Int32.MaxValue rather than a syntax error"

                testCase "full clause order: WHERE, ORDER BY, LIMIT together"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a FROM t WHERE a > 1 ORDER BY a LIMIT 2")
                        (mkSelect(
                            [ col "a", None ],
                            Some "t",
                            Some(BinOp(Gt, col "a", Lit(VInt 1L))),
                            [ col "a", Asc ],
                            Some 2,
                            None
                        ))
                        "full select" ]

          testList
              "expression precedence"
              [ testCase "a OR b AND c: AND binds tighter than OR"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a OR b AND c")
                        (mkSelect(
                            [ BinOp(Or, col "a", BinOp(And, col "b", col "c")), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "or/and precedence"

                testCase "1+2*3: multiplication binds tighter than addition"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1+2*3")
                        (mkSelect(
                            [ BinOp(Add, Lit(VInt 1L), BinOp(Mul, Lit(VInt 2L), Lit(VInt 3L))), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "add/mul precedence"

                testCase "(1+2)*3: parens override precedence"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT (1+2)*3")
                        (mkSelect(
                            [ BinOp(Mul, BinOp(Add, Lit(VInt 1L), Lit(VInt 2L)), Lit(VInt 3L)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "parens"

                testCase "NOT a = b: NOT wraps the whole comparison"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT NOT a = b")
                        (mkSelect([ Not(BinOp(Eq, col "a", col "b")), None ], None, None, [], None, None))
                        "not precedence"

                testCase "a AND NOT b: NOT binds only to b"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a AND NOT b")
                        (mkSelect([ BinOp(And, col "a", Not(col "b")), None ], None, None, [], None, None))
                        "not scoped to right operand"

                testCase "unary minus binds tighter than binary minus"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT -a - 1")
                        (mkSelect(
                            [ BinOp(Sub, BinOp(Sub, Lit(VInt 0L), col "a"), Lit(VInt 1L)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "unary minus"

                testCase "modulo desugars to MOD()"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a % 2")
                        (mkSelect([ FuncCall("MOD", [ col "a"; Lit(VInt 2L) ]), None ], None, None, [], None, None))
                        "modulo"

                testCase "DIV parses as integer division, case-insensitively, at the same precedence as * / %"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a DIV 2")
                        (mkSelect([ BinOp(IntDiv, col "a", Lit(VInt 2L)), None ], None, None, [], None, None))
                        "uppercase DIV"

                    Expect.equal
                        (parseOk "SELECT a div 2")
                        (mkSelect([ BinOp(IntDiv, col "a", Lit(VInt 2L)), None ], None, None, [], None, None))
                        "lowercase div"

                testCase "DIV only matches on a word boundary, not the prefix of an identifier like div_price"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT div_price FROM t")
                        (mkSelect([ col "div_price", None ], Some "t", None, [], None, None))
                        "div_price stays one identifier"

                testCase "comparison operators: <=, >=, <>, !=, <, >"
                <| fun _ ->
                    let cases =
                        [ "a <= 1", Lte
                          "a >= 1", Gte
                          "a <> 1", Neq
                          "a != 1", Neq
                          "a < 1", Lt
                          "a > 1", Gt ]

                    for sql, op in cases do
                        Expect.equal
                            (parseOk (sprintf "SELECT %s" sql))
                            (mkSelect([ BinOp(op, col "a", Lit(VInt 1L)), None ], None, None, [], None, None))
                            sql

                testCase "IS NULL / IS NOT NULL"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a IS NULL, a IS NOT NULL")
                        (mkSelect(
                            [ IsNull(col "a"), None; IsNotNull(col "a"), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "is null"

                testCase "LIKE and NOT LIKE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a LIKE '%x%', a NOT LIKE '%y%'")
                        (mkSelect(
                            [ Like(col "a", Lit(VString "%x%"), false, None), None
                              Not(Like(col "a", Lit(VString "%y%"), false, None)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "like / not like"

                testCase "IN and NOT IN"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a IN (1, 2, 3), a NOT IN (4, 5)")
                        (mkSelect(
                            [ In(col "a", [ Lit(VInt 1L); Lit(VInt 2L); Lit(VInt 3L) ]), None
                              Not(In(col "a", [ Lit(VInt 4L); Lit(VInt 5L) ])), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "in / not in"

                testCase "BETWEEN and NOT BETWEEN"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a BETWEEN 1 AND 10, a NOT BETWEEN 1 AND 10")
                        (mkSelect(
                            [ Between(col "a", Lit(VInt 1L), Lit(VInt 10L)), None
                              Not(Between(col "a", Lit(VInt 1L), Lit(VInt 10L))), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "between / not between"

                testCase "BETWEEN followed by AND-chained boolean term"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t WHERE a BETWEEN 1 AND 10 AND b")
                        (mkSelect(
                            [ Star None, None ],
                            Some "t",
                            Some(BinOp(And, Between(col "a", Lit(VInt 1L), Lit(VInt 10L)), col "b")),
                            [],
                            None,
                            None
                        ))
                        "between then and" ]

          testList
              "literals and quoting"
              [ testCase "an out-of-range integer literal falls back to VDouble instead of throwing"
                <| fun _ ->
                    match parseOk "SELECT 99999999999999999999" with
                    | Select { Projections = [ Lit(VDouble _), None ] } -> ()
                    | other -> failtestf "expected a VDouble fallback, got %A" other

                testCase "an out-of-range decimal literal falls back to VDouble instead of throwing"
                <| fun _ ->
                    match parseOk "SELECT 123456789012345678901234567890123456789.5" with
                    | Select { Projections = [ Lit(VDouble _), None ] } -> ()
                    | other -> failtestf "expected a VDouble fallback, got %A" other

                testCase "single-quoted string with doubled-quote escape"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 'it''s here'")
                        (mkSelect([ Lit(VString "it's here"), None ], None, None, [], None, None))
                        "doubled quote"

                testCase "backslash escapes: \\n \\t \\\\ \\'"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 'a\\nb\\t\\\\\\'c'")
                        (mkSelect([ Lit(VString "a\nb\t\\'c"), None ], None, None, [], None, None))
                        "backslash escapes"

                testCase "\\%% and \\_ stay backslash-escaped rather than collapsing to %%/_"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 'a\\%b\\_c'")
                        (mkSelect([ Lit(VString "a\\%b\\_c"), None ], None, None, [], None, None))
                        "wildcard escapes preserved"

                testCase "double-quoted string literal, same escaping as single-quoted"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT \"bob\", \"it\"\"s here\" FROM users WHERE name = \"bob\"")
                        (mkSelect(
                            [ Lit(VString "bob"), None; Lit(VString "it\"s here"), None ],
                            Some "users",
                            Some(BinOp(Eq, col "name", Lit(VString "bob"))),
                            [],
                            None,
                            None
                        ))
                        "double-quoted strings"

                testCase "quoted hexadecimal literals parse as raw bytes, case-insensitively"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT X'00ffA5', x''")
                        (mkSelect(
                            [ Lit(VBytes [| 0x00uy; 0xffuy; 0xa5uy |]), None
                              Lit(VBytes [||]), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "hex bytes"

                testCase "quoted hexadecimal literals require byte-aligned digits"
                <| fun _ ->
                    match Fsdb.Parser.parse "SELECT X'abc'" with
                    | Ok statement -> failtestf "expected an error, got %A" statement
                    | Error error -> Expect.stringContains error "even number" "actionable malformed-literal error"

                testCase "backtick-quoted identifier, including a reserved word and a doubled backtick"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT `order`, `a``b` FROM `select`")
                        (mkSelect([ col "order", None; col "a`b", None ], Some "select", None, [], None, None))
                        "backtick identifiers"

                testCase "integer, decimal, and exponent-notation numeric literals"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 42, 3.14, 1.5e3")
                        (mkSelect(
                            [ Lit(VInt 42L), None; Lit(VDecimal 3.14M), None; Lit(VDouble 1500.0), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "numeric literals"

                testCase "NULL, TRUE, FALSE literals"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT NULL, TRUE, FALSE")
                        (mkSelect(
                            [ Lit VNull, None; Lit(VInt 1L), None; Lit(VInt 0L), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "null/true/false"

                testCase "line and block comments are skipped"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a -- trailing comment\nFROM /* mid */ t")
                        (mkSelect([ col "a", None ], Some "t", None, [], None, None))
                        "comments"

                testCase "AUTO_INCREMENT is a non-reserved word, usable as a plain column reference"
                <| fun _ ->
                    // Real MySQL agrees (`AUTO_INCREMENT` is non-reserved
                    // there too) — `information_schema.tables.auto_increment`
                    // and Doctrine DBAL's schema-introspection query
                    // (`t.AUTO_INCREMENT`) both depend on this parsing.
                    Expect.equal
                        (parseOk "SELECT t.AUTO_INCREMENT FROM t")
                        (mkSelect([ QualifiedCol("t", "AUTO_INCREMENT"), None ], Some "t", None, [], None, None))
                        "AUTO_INCREMENT as a qualified column reference"

                testCase "version-gated /*!NNNNN ... */ comment splices its SQL back in, below server version"
                <| fun _ ->
                    Expect.equal
                        (stripVersionComments "/*!40103 SET TIME_ZONE='+00:00' */")
                        " SET TIME_ZONE='+00:00' "
                        "below server version executes"

                testCase "version-gated /*!NNNNN ... */ comment is dropped, above server version"
                <| fun _ ->
                    Expect.equal (stripVersionComments "SELECT /*!99999 SQL_NO_CACHE */ 1") "SELECT  1" "above server version is inert"

                testCase "version-gated /*!NNNNN ... */ comment reads only the first 5 digits of a longer run"
                <| fun _ ->
                    // A 6-digit run isn't "no version" -- MySQL gates on its
                    // first 5 (99999, always above the server's version), so
                    // this is inert, not a 1064 with the digits spliced in as
                    // garbage tokens.
                    Expect.equal (stripVersionComments "SELECT /*!999999 BOGUSTOKEN */ 2") "SELECT  2" "first 5 digits gate the comment"

                testCase "version-gated /*!NNNNN ... */ comment reads a run shorter than 5 digits as a version too"
                <| fun _ ->
                    Expect.equal (stripVersionComments "SELECT /*!9999 body */ 1") "SELECT  body  1" "a short digit run still counts as a version"

                testCase "stripVersionComments leaves a /*! -lookalike inside a string literal alone"
                <| fun _ ->
                    Expect.equal
                        (stripVersionComments "INSERT INTO t VALUES (1,'price /*!40101 x*/ note')")
                        "INSERT INTO t VALUES (1,'price /*!40101 x*/ note')"
                        "a quoted /*! is data, not a version comment"

                testCase "stripVersionComments doesn't run off the end of an unterminated /*! lookalike in a literal"
                <| fun _ ->
                    Expect.equal (stripVersionComments "SELECT 'a/*!b'") "SELECT 'a/*!b'" "the literal's closing quote still ends the string"

                testCase "isBlank is true only for a comment/whitespace-only statement"
                <| fun _ ->
                    Expect.isTrue (isBlank "  /* trailing comment */ ") "comment-only"
                    Expect.isTrue (isBlank "-- just a line comment") "line-comment-only"
                    Expect.isFalse (isBlank "SELECT 1") "real statement"

                testCase "keywords are case-insensitive"
                <| fun _ ->
                    Expect.equal
                        (parseOk "select A from T where A = 1")
                        (mkSelect([ col "A", None ], Some "T", Some(BinOp(Eq, col "A", Lit(VInt 1L))), [], None, None))
                        "case insensitivity"

                testCase "optional trailing semicolon"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1;")
                        (mkSelect([ Lit(VInt 1L), None ], None, None, [], None, None))
                        "trailing semicolon" ]

          testList
              "CREATE TABLE"
              [ testCase "columns with types, NOT NULL, DEFAULT, AUTO_INCREMENT, inline PRIMARY KEY"
                <| fun _ ->
                    Expect.equal
                        (parseOk
                            "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(255) NOT NULL, score DECIMAL(5,2) DEFAULT 0)")
                        (CreateTable(
                            "t",
                            [ { Name = "id"
                                Type = TInt false
                                Nullable = true
                                Default = None
                                AutoIncrement = true
                                PrimaryKey = true
                                Unique = false
                                Generated = None }
                              { Name = "name"
                                Type = TVarchar 255
                                Nullable = false
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None }
                              { Name = "score"
                                Type = TDecimal(5, 2)
                                Nullable = true
                                Default = Some(DConst(VInt 0L))
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None } ],
                            [],
                            [],
                            false
                        ))
                        "create table"

                testCase "IF NOT EXISTS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE TABLE IF NOT EXISTS t (id INT)")
                        (CreateTable(
                            "t",
                            [ { Name = "id"
                                Type = TInt false
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None } ],
                            [],
                            [],
                            true
                        ))
                        "if not exists"

                testCase "trailing PRIMARY KEY (col) marks the referenced column"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE TABLE t (id INT, name VARCHAR(10), PRIMARY KEY (id))")
                        (CreateTable(
                            "t",
                            [ { Name = "id"
                                Type = TInt false
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = true
                                Unique = false
                                Generated = None }
                              { Name = "name"
                                Type = TVarchar 10
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None } ],
                            [],
                            [],
                            false
                        ))
                        "trailing primary key"

                testCase "BIGINT UNSIGNED"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id BIGINT UNSIGNED)" with
                    | CreateTable(_, [ { Type = TBigInt true } ], _, _, _) -> ()
                    | other -> failtestf "expected an unsigned bigint column, got %A" other

                testCase "DEFAULT CURRENT_TIMESTAMP"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)" with
                    | CreateTable(_, [ { Type = TTimestamp; Default = Some DCurrentTimestamp } ], _, _, _) -> ()
                    | other -> failtestf "expected a CURRENT_TIMESTAMP default, got %A" other

                testCase "ENGINE=/CHARSET=/COLLATE= table options are ignored but accepted"
                <| fun _ ->
                    Expect.equal
                        (parseOk
                            "CREATE TABLE t (id INT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci")
                        (CreateTable(
                            "t",
                            [ { Name = "id"
                                Type = TInt false
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None } ],
                            [],
                            [],
                            false
                        ))
                        "table options"

                testCase "COLLATE with a quoted value, as Laravel's MySQL grammar emits it"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id INT) DEFAULT CHARACTER SET utf8mb4 COLLATE 'utf8mb4_unicode_ci'" with
                    | CreateTable("t", [ { Name = "id" } ], [], [], false) -> ()
                    | other -> failtestf "expected the quoted collation to parse, got %A" other

                testCase "column-level UNIQUE synthesizes a unique index named after the column"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (email VARCHAR(255) UNIQUE)" with
                    | CreateTable(_, [ { Unique = true } ], [ { Name = "email"; Columns = [ "email" ]; Unique = true } ], [], false) -> ()
                    | other -> failtestf "expected a synthesized unique index, got %A" other

                testCase "trailing UNIQUE KEY / KEY / INDEX with an explicit name"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (a INT, b INT, UNIQUE KEY uq_a (a), KEY idx_b (b))" with
                    | CreateTable(
                        _,
                        _,
                        [ { Name = "uq_a"; Columns = [ "a" ]; Unique = true }
                          { Name = "idx_b"; Columns = [ "b" ]; Unique = false } ],
                        [],
                        false) -> ()
                    | other -> failtestf "expected two indexes, got %A" other

                testCase "trailing CONSTRAINT ... FOREIGN KEY with ON DELETE/ON UPDATE"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE posts (user_id BIGINT UNSIGNED, CONSTRAINT posts_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE ON UPDATE RESTRICT)"
                    with
                    | CreateTable(
                        _,
                        _,
                        [],
                        [ { Name = "posts_user_id_foreign"
                            Columns = [ "user_id" ]
                            RefTable = "users"
                            RefColumns = [ "id" ]
                            OnDelete = Some "CASCADE"
                            OnUpdate = Some "RESTRICT" } ],
                        false) -> ()
                    | other -> failtestf "expected a foreign key, got %A" other

                testCase "unnamed trailing FOREIGN KEY gets a synthesized name"
                <| fun _ ->
                    match parseOk "CREATE TABLE posts (user_id INT, FOREIGN KEY (user_id) REFERENCES users (id))" with
                    | CreateTable(_, _, [], [ { Name = "users_user_id_foreign" } ], false) -> ()
                    | other -> failtestf "expected a synthesized FK name, got %A" other

                testCase "a bare CONSTRAINT with no symbol name before FOREIGN KEY still parses"
                <| fun _ ->
                    match parseOk "CREATE TABLE posts (user_id INT, CONSTRAINT FOREIGN KEY (user_id) REFERENCES users (id))" with
                    | CreateTable(_, _, [], [ { Name = "users_user_id_foreign" } ], false) -> ()
                    | other -> failtestf "expected an unnamed CONSTRAINT to still synthesize a name, got %A" other

                testCase "ENUM and SET column types carry their declared values"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (status ENUM('a', 'b'), flags SET('x', 'y'))" with
                    | CreateTable(_, [ { Type = TEnum [ "a"; "b" ] }; { Type = TSet [ "x"; "y" ] } ], [], [], false) -> ()
                    | other -> failtestf "expected enum/set types, got %A" other

                testCase "CHAR/TEXT/BLOB family and TINY/MEDIUM/SMALL int variants all parse"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE t (a CHAR(3), b TINYTEXT, c MEDIUMTEXT, d LONGTEXT, e BLOB, f VARBINARY(16), g BINARY(4), h SMALLINT, i MEDIUMINT UNSIGNED, j TIME, k YEAR, l FLOAT, m DOUBLE(8,2))"
                    with
                    | CreateTable(
                        _,
                        [ { Type = TChar 3 }
                          { Type = TTinyText }
                          { Type = TMediumText }
                          { Type = TLongText }
                          { Type = TBlob }
                          { Type = TVarBinary 16 }
                          { Type = TBinary 4 }
                          { Type = TSmallInt false }
                          { Type = TMediumInt true }
                          { Type = TTime }
                          { Type = TYear }
                          { Type = TFloat }
                          { Type = TDouble } ],
                        [],
                        [],
                        false) -> ()
                    | other -> failtestf "expected every new column type to parse, got %A" other

                testCase "BOOLEAN/BOOL is an alias for TINYINT"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (a BOOLEAN, b BOOL)" with
                    | CreateTable(_, [ { Type = TTinyInt false }; { Type = TTinyInt false } ], [], [], false) -> ()
                    | other -> failtestf "expected TTinyInt for BOOLEAN/BOOL, got %A" other

                testCase "COMMENT / CHARACTER SET / COLLATE column modifiers are accepted and ignored"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE t (name VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci COMMENT 'a name')"
                    with
                    | CreateTable(_, [ { Name = "name" } ], [], [], false) -> ()
                    | other -> failtestf "expected the comment/charset/collate to be ignored, got %A" other

                testCase "a generated column's AS (expr) [VIRTUAL|STORED] is captured on ColumnDef.Generated (Laravel Pulse's key_hash)"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE pulse_values (`key` MEDIUMTEXT NOT NULL, key_hash CHAR(16) CHARACTER SET binary AS (UNHEX(MD5(`key`))))"
                    with
                    | CreateTable(_,
                                  [ { Name = "key"; Generated = None }
                                    { Name = "key_hash"; Type = TChar 16; Generated = Some(FuncCall("UNHEX", _)) } ],
                                  [],
                                  [],
                                  false) -> ()
                    | other -> failtestf "expected the generated column's AS (...) to be captured, got %A" other ]

          testList
              "DROP TABLE / TRUNCATE"
              [ testCase "DROP TABLE a, b"
                <| fun _ -> Expect.equal (parseOk "DROP TABLE a, b") (DropTable([ "a"; "b" ], false)) "drop table"

                testCase "DROP TABLE IF EXISTS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DROP TABLE IF EXISTS a")
                        (DropTable([ "a" ], true))
                        "drop table if exists"

                testCase "TRUNCATE TABLE t"
                <| fun _ -> Expect.equal (parseOk "TRUNCATE TABLE t") (Truncate "t") "truncate table"

                testCase "TRUNCATE t without the TABLE keyword"
                <| fun _ -> Expect.equal (parseOk "TRUNCATE t") (Truncate "t") "truncate"

                testCase "DELETE FROM db.t qualified table name"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DELETE FROM app.t")
                        (Delete
                            { Targets = [ "t" ]
                              From = { Database = Some "app"; Table = "t"; Alias = None }
                              Joins = []
                              Where = None
                              OrderBy = []
                              Limit = None })
                        "qualified delete target" ]

          testList
              "DATABASE"
              [ testCase "CREATE DATABASE"
                <| fun _ -> Expect.equal (parseOk "CREATE DATABASE foo") (CreateDatabase("foo", false)) "create database"

                testCase "CREATE DATABASE IF NOT EXISTS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE DATABASE IF NOT EXISTS foo")
                        (CreateDatabase("foo", true))
                        "create database if not exists"

                testCase "CREATE SCHEMA"
                <| fun _ -> Expect.equal (parseOk "CREATE SCHEMA foo") (CreateDatabase("foo", false)) "create schema"

                testCase "DROP DATABASE"
                <| fun _ -> Expect.equal (parseOk "DROP DATABASE foo") (DropDatabase("foo", false)) "drop database"

                testCase "DROP DATABASE IF EXISTS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DROP DATABASE IF EXISTS foo")
                        (DropDatabase("foo", true))
                        "drop database if exists"

                testCase "CREATE DATABASE with a trailing CHARACTER SET/COLLATE tail is accepted and discarded"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE DATABASE IF NOT EXISTS crescat_testing CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
                        (CreateDatabase("crescat_testing", true))
                        "charset/collate tail parses and is ignored"

                testCase "CREATE DATABASE with Laravel's exact DEFAULT CHARACTER SET/DEFAULT COLLATE, backticked, form"
                <| fun _ ->
                    // Verbatim what MySqlGrammar::compileCreateDatabase emits
                    // — what Illuminate\Testing\Concerns\TestDatabases calls
                    // to build each parallel worker's own database.
                    Expect.equal
                        (parseOk "create database `x` default character set `utf8mb4` default collate `utf8mb4_unicode_ci`")
                        (CreateDatabase("x", false))
                        "backticked default charset/collate tail parses and is ignored"

                testCase "ALTER DATABASE with a CHARACTER SET/COLLATE tail parses"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER DATABASE x CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci")
                        (AlterDatabase "x")
                        "alter database" ]

          testList
              "INSERT"
              [ testCase "INSERT INTO t (cols) VALUES (...), (...)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) VALUES (1, 'x'), (2, 'y')")
                        (Insert(
                            "t",
                            [ "a"; "b" ],
                            [ [ Lit(VInt 1L); Lit(VString "x") ]; [ Lit(VInt 2L); Lit(VString "y") ] ],
                            [],
                            false
                        ))
                        "insert with columns"

                testCase "INSERT INTO t VALUES (...) without a column list"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t VALUES (1, 2)")
                        (Insert("t", [], [ [ Lit(VInt 1L); Lit(VInt 2L) ] ], [], false))
                        "insert without columns"

                testCase "INSERT IGNORE sets the ignore flag"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT IGNORE INTO t VALUES (1)")
                        (Insert("t", [], [ [ Lit(VInt 1L) ] ], [], true))
                        "insert ignore"

                testCase "INSERT ... ON DUPLICATE KEY UPDATE carries the assignment list"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) VALUES (1, 2) ON DUPLICATE KEY UPDATE b = VALUES(b) + 1")
                        (Insert(
                            "t",
                            [ "a"; "b" ],
                            [ [ Lit(VInt 1L); Lit(VInt 2L) ] ],
                            [ "b", BinOp(Add, FuncCall("VALUES", [ col "b" ]), Lit(VInt 1L)) ],
                            false
                        ))
                        "on duplicate key update"

                testCase "INSERT INTO t (cols) SELECT ... is InsertSelect, not a VALUES Insert"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) SELECT x, y FROM u WHERE x > 1")
                        (InsertSelect(
                            "t",
                            [ "a"; "b" ],
                            { Projections = [ col "x", None; col "y", None ]
                              Distinct = false
                              From = Some(FromTable { Database = None; Table = "u"; Alias = None })
                              Joins = []
                              Where = Some(BinOp(Gt, col "x", Lit(VInt 1L)))
                              GroupBy = []
                              Having = None
                              OrderBy = []
                              Limit = None
                              Offset = None
                              Locking = false },
                            false
                        ))
                        "insert select"

                testCase "INSERT IGNORE INTO t SELECT ... sets the ignore flag"
                <| fun _ ->
                    match parseOk "INSERT IGNORE INTO t SELECT * FROM u" with
                    | InsertSelect("t", [], _, true) -> ()
                    | other -> failtestf "expected an ignore-flagged InsertSelect, got %A" other ]

          testList
              "ROW_NUMBER() OVER (...)"
              [ testCase "PARTITION BY and ORDER BY both present"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER (PARTITION BY a ORDER BY b DESC) AS rn FROM t" with
                    | Select { Projections = [ RowNumberOver([ Col "a" ], [ Col "b", Desc ]), Some "rn" ] } -> ()
                    | other -> failtestf "expected a RowNumberOver projection, got %A" other

                testCase "PARTITION BY with multiple columns, ORDER BY defaulting to ASC"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER (PARTITION BY a, b ORDER BY c) FROM t" with
                    | Select { Projections = [ RowNumberOver([ Col "a"; Col "b" ], [ Col "c", Asc ]), None ] } -> ()
                    | other -> failtestf "expected a two-column partition key, got %A" other

                testCase "OVER () with neither PARTITION BY nor ORDER BY"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER () FROM t" with
                    | Select { Projections = [ RowNumberOver([], []), None ] } -> ()
                    | other -> failtestf "expected an empty partition/order spec, got %A" other ]

          testList
              "LAG(expr[, offset]) OVER (...)"
              [ testCase "no explicit offset defaults to 1"
                <| fun _ ->
                    match parseOk "SELECT LAG(value) OVER (PARTITION BY a ORDER BY b) AS prev FROM t" with
                    | Select { Projections = [ LagOver(Col "value", 1L, [ Col "a" ], [ Col "b", Asc ]), Some "prev" ] } -> ()
                    | other -> failtestf "expected a LagOver projection with offset 1, got %A" other

                testCase "an explicit offset is parsed through"
                <| fun _ ->
                    match parseOk "SELECT LAG(value, 2) OVER (ORDER BY b) FROM t" with
                    | Select { Projections = [ LagOver(Col "value", 2L, [], [ Col "b", Asc ]), None ] } -> ()
                    | other -> failtestf "expected offset 2, got %A" other

                testCase "usable nested inside arithmetic"
                <| fun _ ->
                    match parseOk "SELECT value - LAG(value) OVER (ORDER BY b) AS diff FROM t" with
                    | Select { Projections = [ BinOp(Sub, Col "value", LagOver(Col "value", 1L, [], [ Col "b", Asc ])), Some "diff" ] } -> ()
                    | other -> failtestf "expected LagOver nested in a BinOp, got %A" other ]

          testList
              "UPDATE / DELETE"
              [ testCase "UPDATE t SET a=expr, b=expr WHERE ..."
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t SET a = 1, b = a + 1 WHERE id = 5")
                        (Update
                            { From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Assignments =
                                [ { Table = None; Column = "a"; Value = Lit(VInt 1L) }
                                  { Table = None; Column = "b"; Value = BinOp(Add, col "a", Lit(VInt 1L)) } ]
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = []
                              Limit = None })
                        "update"

                testCase "UPDATE without WHERE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t SET a = 1")
                        (Update
                            { From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Assignments = [ { Table = None; Column = "a"; Value = Lit(VInt 1L) } ]
                              Where = None
                              OrderBy = []
                              Limit = None })
                        "update without where"

                testCase "DELETE FROM t WHERE ..."
                <| fun _ ->
                    Expect.equal
                        (parseOk "DELETE FROM t WHERE id = 5")
                        (Delete
                            { Targets = [ "t" ]
                              From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = []
                              Limit = None })
                        "delete"

                testCase "DELETE FROM t without WHERE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DELETE FROM t")
                        (Delete
                            { Targets = [ "t" ]
                              From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Where = None
                              OrderBy = []
                              Limit = None })
                        "delete without where"

                testCase "DELETE FROM t WHERE ... LIMIT n"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DELETE FROM t WHERE id = 5 LIMIT 100")
                        (Delete
                            { Targets = [ "t" ]
                              From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = []
                              Limit = Some 100 })
                        "delete with limit"

                testCase "UPDATE with an alias, ORDER BY, and LIMIT"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t AS x SET a = 1 WHERE id = 5 ORDER BY id LIMIT 10")
                        (Update
                            { From = { Database = None; Table = "t"; Alias = Some "x" }
                              Joins = []
                              Assignments = [ { Table = None; Column = "a"; Value = Lit(VInt 1L) } ]
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = [ col "id", Asc ]
                              Limit = Some 10 })
                        "alias parsed, order/limit real"

                testCase "UPDATE with a bare alias (no AS)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t x SET a = 1")
                        (Update
                            { From = { Database = None; Table = "t"; Alias = Some "x" }
                              Joins = []
                              Assignments = [ { Table = None; Column = "a"; Value = Lit(VInt 1L) } ]
                              Where = None
                              OrderBy = []
                              Limit = None })
                        "bare alias parsed"

                testCase "UPDATE SET with a table-qualified column (Laravel's touch())"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE chatbots SET restrict_allowed_origins = 1, `chatbots`.`updated_at` = '2024-01-01'")
                        (Update
                            { From = { Database = None; Table = "chatbots"; Alias = None }
                              Joins = []
                              Assignments =
                                [ { Table = None; Column = "restrict_allowed_origins"; Value = Lit(VInt 1L) }
                                  { Table = Some "chatbots"; Column = "updated_at"; Value = Lit(VString "2024-01-01") } ]
                              Where = None
                              OrderBy = []
                              Limit = None })
                        "table.column assignment target keeps the table qualifier"

                testCase "UPDATE t1 JOIN t2 ON ... SET t1.x = ..., t2.y = ..."
                <| fun _ ->
                    match parseOk "UPDATE t1 JOIN t2 ON t1.id = t2.t1_id SET t1.x = 1, t2.y = 2 WHERE t1.id = 5" with
                    | Update { Joins = [ { Kind = InnerJoin; Table = FromTable { Table = "t2" } } ]
                               Assignments = [ { Table = Some "t1"; Column = "x" }; { Table = Some "t2"; Column = "y" } ]
                               OrderBy = []
                               Limit = None } -> ()
                    | other -> failtestf "expected a two-table UPDATE JOIN, got %A" other

                testCase "UPDATE t1 JOIN t2 ON ... ORDER BY is a syntax error (MySQL rejects it too)"
                <| fun _ -> Expect.isError (parse "UPDATE t1 JOIN t2 ON t1.id = t2.id SET t1.x = 1 ORDER BY t1.id") "multi-table UPDATE ORDER BY rejected"

                testCase "DELETE t1 FROM t1 JOIN t2 ON ... — named targets before FROM"
                <| fun _ ->
                    match parseOk "DELETE t1 FROM t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Delete { Targets = [ "t1" ]
                               From = { Table = "t1" }
                               Joins = [ { Kind = InnerJoin; Table = FromTable { Table = "t2" } } ] } -> ()
                    | other -> failtestf "expected a named-target multi-table DELETE, got %A" other

                testCase "DELETE FROM t1 USING t1 JOIN t2 ON ..."
                <| fun _ ->
                    match parseOk "DELETE FROM t1 USING t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Delete { Targets = [ "t1" ]
                               From = { Table = "t1" }
                               Joins = [ { Kind = InnerJoin; Table = FromTable { Table = "t2" } } ] } -> ()
                    | other -> failtestf "expected a USING-form multi-table DELETE, got %A" other ]

          testList
              "ALTER TABLE / RENAME TABLE / CREATE INDEX / DROP INDEX"
              [ testCase "ADD COLUMN, with and without the COLUMN keyword, and AFTER accepted"
                <| fun _ ->
                    match parseOk "ALTER TABLE t ADD COLUMN a INT, ADD b VARCHAR(10) AFTER a" with
                    | AlterTable("t",
                                 [ AddColumn({ Name = "a"; Type = TInt false }, PositionDefault)
                                   AddColumn({ Name = "b"; Type = TVarchar 10 }, PositionAfter "a") ]) -> ()
                    | other -> failtestf "expected two AddColumn actions, got %A" other

                testCase "ADD COLUMN ... FIRST"
                <| fun _ ->
                    match parseOk "ALTER TABLE t ADD COLUMN a INT FIRST" with
                    | AlterTable("t", [ AddColumn({ Name = "a" }, PositionFirst) ]) -> ()
                    | other -> failtestf "expected a FIRST-positioned AddColumn, got %A" other

                testCase "DROP COLUMN, with and without the COLUMN keyword"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t DROP COLUMN a, DROP b")
                        (AlterTable("t", [ DropColumn "a"; DropColumn "b" ]))
                        "drop column"

                testCase "MODIFY COLUMN changes the column's definition"
                <| fun _ ->
                    match parseOk "ALTER TABLE t MODIFY COLUMN a BIGINT UNSIGNED NOT NULL" with
                    | AlterTable("t", [ ModifyColumn({ Name = "a"; Type = TBigInt true; Nullable = false }, PositionDefault) ]) -> ()
                    | other -> failtestf "expected a ModifyColumn action, got %A" other

                testCase "MODIFY COLUMN ... AFTER col"
                <| fun _ ->
                    match parseOk "ALTER TABLE t MODIFY a INT AFTER b" with
                    | AlterTable("t", [ ModifyColumn({ Name = "a" }, PositionAfter "b") ]) -> ()
                    | other -> failtestf "expected an AFTER-positioned ModifyColumn, got %A" other

                testCase "CHANGE COLUMN renames and redefines"
                <| fun _ ->
                    match parseOk "ALTER TABLE t CHANGE old_name new_name INT" with
                    | AlterTable("t", [ ChangeColumn("old_name", { Name = "new_name"; Type = TInt false }, PositionDefault) ]) -> ()
                    | other -> failtestf "expected a ChangeColumn action, got %A" other

                testCase "RENAME TO / RENAME AS / bare RENAME"
                <| fun _ ->
                    Expect.equal (parseOk "ALTER TABLE t RENAME TO u") (AlterTable("t", [ RenameTo "u" ])) "rename to"
                    Expect.equal (parseOk "ALTER TABLE t RENAME AS u") (AlterTable("t", [ RenameTo "u" ])) "rename as"
                    Expect.equal (parseOk "ALTER TABLE t RENAME u") (AlterTable("t", [ RenameTo "u" ])) "bare rename"

                testCase "RENAME COLUMN a TO b"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t RENAME COLUMN a TO b")
                        (AlterTable("t", [ RenameColumnTo("a", "b") ]))
                        "rename column"

                testCase "ADD [UNIQUE] INDEX|KEY name (cols) and DROP INDEX|KEY name"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t ADD UNIQUE INDEX uq (a), ADD KEY idx (b), DROP INDEX uq, DROP KEY idx")
                        (AlterTable(
                            "t",
                            [ AddIndex { Name = "uq"; Columns = [ "a" ]; Unique = true }
                              AddIndex { Name = "idx"; Columns = [ "b" ]; Unique = false }
                              DropIndexAction "uq"
                              DropIndexAction "idx" ]
                        ))
                        "add/drop index"

                testCase "ADD CONSTRAINT ... FOREIGN KEY and DROP FOREIGN KEY"
                <| fun _ ->
                    match
                        parseOk
                            "ALTER TABLE posts ADD CONSTRAINT fk1 FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE, DROP FOREIGN KEY fk_old"
                    with
                    | AlterTable(
                        "posts",
                        [ AddForeignKey { Name = "fk1"; Columns = [ "user_id" ]; RefTable = "users"; RefColumns = [ "id" ] }
                          DropForeignKey "fk_old" ]) -> ()
                    | other -> failtestf "expected add/drop foreign key actions, got %A" other

                testCase "ADD PRIMARY KEY (cols)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t ADD PRIMARY KEY (id)")
                        (AlterTable("t", [ AddPrimaryKey [ "id" ] ]))
                        "add primary key"

                testCase "RENAME TABLE a TO b, c TO d"
                <| fun _ ->
                    Expect.equal
                        (parseOk "RENAME TABLE a TO b, c TO d")
                        (RenameTable [ "a", "b"; "c", "d" ])
                        "rename table"

                testCase "CREATE INDEX / CREATE UNIQUE INDEX"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE INDEX idx_a ON t (a)")
                        (CreateIndex("idx_a", "t", [ "a" ], false))
                        "create index"

                    Expect.equal
                        (parseOk "CREATE UNIQUE INDEX uq_a ON t (a)")
                        (CreateIndex("uq_a", "t", [ "a" ], true))
                        "create unique index"

                testCase "DROP INDEX [IF EXISTS] name ON table"
                <| fun _ ->
                    Expect.equal (parseOk "DROP INDEX idx_a ON t") (DropIndexStmt("idx_a", "t", false)) "drop index"
                    Expect.equal (parseOk "DROP INDEX IF EXISTS idx_a ON t") (DropIndexStmt("idx_a", "t", true)) "drop index if exists"

                testCase "CAST(expr AS type), including SIGNED/UNSIGNED"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT CAST(a AS UNSIGNED), CAST('1' AS SIGNED), CAST(a AS CHAR)")
                        (mkSelect(
                            [ Cast(col "a", TInt true), None
                              Cast(Lit(VString "1"), TInt false), None
                              Cast(col "a", TChar 1), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "cast"

                testCase "CASE WHEN ... THEN ... ELSE ... END (searched form)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT CASE WHEN a > 0 THEN 'pos' WHEN a < 0 THEN 'neg' ELSE 'zero' END")
                        (mkSelect(
                            [ Case(
                                  None,
                                  [ BinOp(Gt, col "a", Lit(VInt 0L)), Lit(VString "pos")
                                    BinOp(Lt, col "a", Lit(VInt 0L)), Lit(VString "neg") ],
                                  Some(Lit(VString "zero"))
                              ),
                              None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "searched CASE"

                testCase "CASE subject WHEN ... THEN ... END, no ELSE (simple form)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT CASE status WHEN 1 THEN 'a' WHEN 2 THEN 'b' END")
                        (mkSelect(
                            [ Case(Some(col "status"), [ Lit(VInt 1L), Lit(VString "a"); Lit(VInt 2L), Lit(VString "b") ], None), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "simple CASE, no ELSE"

                testCase "<=> is the null-safe equals operator"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a <=> b")
                        (mkSelect([ BinOp(NullSafeEq, col "a", col "b"), None ], None, None, [], None, None))
                        "<=>"

                testCase "IS TRUE / IS FALSE / IS NOT TRUE / IS NOT FALSE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a IS TRUE, a IS FALSE, a IS NOT TRUE, a IS NOT FALSE")
                        (mkSelect(
                            [ IsTrue(col "a"), None
                              IsFalse(col "a"), None
                              Not(IsTrue(col "a")), None
                              Not(IsFalse(col "a")), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "IS TRUE/FALSE"

                testCase "LIKE BINARY sets Like's case-sensitive flag"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a LIKE BINARY 'x', a NOT LIKE BINARY 'y'")
                        (mkSelect(
                            [ Like(col "a", Lit(VString "x"), true, None), None; Not(Like(col "a", Lit(VString "y"), true, None)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "LIKE BINARY"

                testCase "REGEXP / RLIKE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a REGEXP '^x', a RLIKE '^y', a NOT REGEXP '^z'")
                        (mkSelect(
                            [ Regexp(col "a", Lit(VString "^x")), None
                              Regexp(col "a", Lit(VString "^y")), None
                              Not(Regexp(col "a", Lit(VString "^z"))), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "REGEXP/RLIKE"

                testCase "col->'$.path' and col->>'$.path' desugar to JSON_EXTRACT/JSON_UNQUOTE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT data->'$.a', data->>'$.b'")
                        (mkSelect(
                            [ FuncCall("JSON_EXTRACT", [ col "data"; Lit(VString "$.a") ]), None
                              FuncCall("JSON_UNQUOTE", [ FuncCall("JSON_EXTRACT", [ col "data"; Lit(VString "$.b") ]) ]), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "JSON arrow operators"

                testCase "INTERVAL n UNIT parses as a FuncCall shape"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT DATE_ADD(created_at, INTERVAL 1 DAY)")
                        (mkSelect(
                            [ FuncCall("DATE_ADD", [ col "created_at"; FuncCall("INTERVAL", [ Lit(VInt 1L); Lit(VString "DAY") ]) ]), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "INTERVAL encoding"

                testCase "scalar subquery: (SELECT ...) used as a value"
                <| fun _ ->
                    match parseOk "SELECT (SELECT COUNT(*) FROM t) AS c" with
                    | Select { Projections = [ Subquery { From = Some(FromTable { Table = "t" }) }, Some "c" ] } -> ()
                    | other -> failtestf "expected a Subquery projection, got %A" other

                testCase "IN (SELECT ...) parses as InSubquery"
                <| fun _ ->
                    match parseOk "SELECT a FROM t WHERE a IN (SELECT b FROM u)" with
                    | Select { Where = Some(InSubquery(Col "a", { From = Some(FromTable { Table = "u" }) })) } -> ()
                    | other -> failtestf "expected InSubquery, got %A" other

                testCase "NOT IN (SELECT ...) desugars to Not(InSubquery(...))"
                <| fun _ ->
                    match parseOk "SELECT a FROM t WHERE a NOT IN (SELECT b FROM u)" with
                    | Select { Where = Some(Not(InSubquery(Col "a", _))) } -> ()
                    | other -> failtestf "expected Not(InSubquery(...)), got %A" other

                testCase "NOT EXISTS desugars through the ordinary NOT/EXISTS parsers"
                <| fun _ ->
                    match parseOk "SELECT a FROM t WHERE NOT EXISTS (SELECT 1 FROM u)" with
                    | Select { Where = Some(Not(Exists _)) } -> ()
                    | other -> failtestf "expected Not(Exists ...), got %A" other ]

          testList
              "GROUP BY / HAVING"
              [ testCase "GROUP BY col, HAVING with an aggregate"
                <| fun _ ->
                    match parseOk "SELECT dept, COUNT(*) AS c FROM t GROUP BY dept HAVING COUNT(*) > 1" with
                    | Select { GroupBy = [ Col "dept" ]; Having = Some(BinOp(Gt, FuncCall("COUNT", [ Star None ]), Lit(VInt 1L))) } -> ()
                    | other -> failtestf "expected GroupBy/Having to parse, got %A" other

                testCase "GROUP BY accepts multiple comma-separated expressions"
                <| fun _ ->
                    match parseOk "SELECT a, b, COUNT(*) FROM t GROUP BY a, b" with
                    | Select { GroupBy = [ Col "a"; Col "b" ] } -> ()
                    | other -> failtestf "expected two GROUP BY keys, got %A" other

                testCase "COUNT(DISTINCT x) parses to FuncCall with a Distinct-wrapped argument"
                <| fun _ ->
                    match parseOk "SELECT COUNT(DISTINCT x) FROM t" with
                    | Select { Projections = [ FuncCall("COUNT", [ Distinct(Col "x") ]), None ] } -> ()
                    | other -> failtestf "expected COUNT(DISTINCT x), got %A" other

                testCase "GROUP_CONCAT(x SEPARATOR '-') and GROUP_CONCAT(DISTINCT x)"
                <| fun _ ->
                    let expectedProjections =
                        [ FuncCall("GROUP_CONCAT", [ Col "x"; Lit(VString "-") ]), None
                          FuncCall("GROUP_CONCAT", [ Distinct(Col "y") ]), None ]

                    match parseOk "SELECT GROUP_CONCAT(x SEPARATOR '-'), GROUP_CONCAT(DISTINCT y) FROM t" with
                    | Select { Projections = projs } -> Expect.equal projs expectedProjections "two GROUP_CONCAT shapes"
                    | other -> failtestf "expected a Select, got %A" other

                testCase "GROUP_CONCAT(x ORDER BY y DESC SEPARATOR ',')"
                <| fun _ ->
                    let expected =
                        [ FuncCall("GROUP_CONCAT", [ Col "x"; OrderBy(Col "y", Desc); Lit(VString ",") ]), None ]

                    match parseOk "SELECT GROUP_CONCAT(x ORDER BY y DESC SEPARATOR ',') FROM t" with
                    | Select { Projections = projs } -> Expect.equal projs expected "GROUP_CONCAT with an ORDER BY key"
                    | other -> failtestf "expected a Select, got %A" other ]

          testList
              "JOIN kinds"
              [ testCase "RIGHT JOIN"
                <| fun _ ->
                    match parseOk "SELECT * FROM a RIGHT JOIN b ON a.id = b.a_id" with
                    | Select { Joins = [ { Kind = RightJoin } ] } -> ()
                    | other -> failtestf "expected a RightJoin, got %A" other

                testCase "CROSS JOIN has no ON clause and parses to the always-true condition"
                <| fun _ ->
                    match parseOk "SELECT * FROM a CROSS JOIN b" with
                    | Select { Joins = [ { Kind = CrossJoin; On = Lit(VInt 1L) } ] } -> ()
                    | other -> failtestf "expected a CrossJoin, got %A" other

                testCase "CROSS JOIN (SELECT ...) AS t is a derived table join source"
                <| fun _ ->
                    match parseOk "SELECT * FROM a CROSS JOIN (SELECT id FROM t) AS derived" with
                    | Select { Joins = [ { Kind = CrossJoin; Table = FromSubquery(PlainSelect { From = Some(FromTable { Table = "t" }) }, "derived") } ] } -> ()
                    | other -> failtestf "expected a CrossJoin over a FromSubquery, got %A" other

                testCase "multiple chained joins with aliases"
                <| fun _ ->
                    match parseOk "SELECT * FROM a AS x JOIN b AS y ON x.id = y.a_id LEFT JOIN c AS z ON y.id = z.b_id" with
                    | Select { Joins = [ { Kind = InnerJoin; Table = FromTable { Alias = Some "y" } }; { Kind = LeftJoin; Table = FromTable { Alias = Some "z" } } ] } ->
                        ()
                    | other -> failtestf "expected two chained joins, got %A" other

                testCase "NATURAL [LEFT|RIGHT] JOIN parses to the natural kinds, and takes no ON"
                <| fun _ ->
                    match parseOk "SELECT * FROM a NATURAL JOIN b" with
                    | Select { Joins = [ { Kind = NaturalJoin; Using = [] } ] } -> ()
                    | other -> failtestf "expected a NaturalJoin, got %A" other

                    match parseOk "SELECT * FROM a NATURAL LEFT JOIN b" with
                    | Select { Joins = [ { Kind = NaturalLeftJoin } ] } -> ()
                    | other -> failtestf "expected a NaturalLeftJoin, got %A" other

                    match parseOk "SELECT * FROM a NATURAL RIGHT OUTER JOIN b" with
                    | Select { Joins = [ { Kind = NaturalRightJoin } ] } -> ()
                    | other -> failtestf "expected a NaturalRightJoin, got %A" other

                    // MySQL rejects an ON after NATURAL (and after USING) —
                    // the grammar doesn't consume it, so this is a 1064.
                    match parse "SELECT * FROM a NATURAL JOIN b ON a.j = b.j" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected NATURAL JOIN ... ON to be a parse error, got %A" stmt

                testCase "JOIN ... USING (cols) parses the column list; USING + ON is a 1064"
                <| fun _ ->
                    match parseOk "SELECT * FROM a JOIN b USING (id)" with
                    | Select { Joins = [ { Kind = InnerJoin; Using = [ "id" ] } ] } -> ()
                    | other -> failtestf "expected a USING inner join, got %A" other

                    match parseOk "SELECT * FROM a LEFT JOIN b USING (x, y)" with
                    | Select { Joins = [ { Kind = LeftJoin; Using = [ "x"; "y" ] } ] } -> ()
                    | other -> failtestf "expected a LEFT USING join with two columns, got %A" other

                    match parse "SELECT * FROM a JOIN b USING (id) ON a.id = b.id" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected USING ... ON to be a parse error, got %A" stmt ]

          testList
              "derived tables and UNION"
              [ testCase "FROM (SELECT ...) AS t is a derived table"
                <| fun _ ->
                    match parseOk "SELECT * FROM (SELECT id FROM t) AS derived" with
                    | Select { From = Some(FromSubquery(PlainSelect { From = Some(FromTable { Table = "t" }) }, "derived")) } -> ()
                    | other -> failtestf "expected a FromSubquery, got %A" other

                testCase "LEFT JOIN (SELECT ...) AS t ON ... is a derived table join source"
                <| fun _ ->
                    // Eloquent's leftJoinSub/joinSub — a real table's JOIN
                    // target can be a subquery too, not just the leading FROM.
                    match parseOk "SELECT * FROM a LEFT JOIN (SELECT id FROM t) AS derived ON a.id = derived.id" with
                    | Select { Joins = [ { Kind = LeftJoin; Table = FromSubquery(PlainSelect { From = Some(FromTable { Table = "t" }) }, "derived") } ] } -> ()
                    | other -> failtestf "expected a FromSubquery join target, got %A" other

                testCase "UNION ALL keeps duplicates, plain UNION dedupes"
                <| fun _ ->
                    match parseOk "SELECT a FROM t UNION ALL SELECT b FROM u UNION SELECT c FROM v" with
                    | Union(_, [ (true, _); (false, _) ], _, _, _) -> ()
                    | other -> failtestf "expected a two-branch Union with ALL then DISTINCT flags, got %A" other

                testCase "a single SELECT (no UNION) still parses to the plain Select case"
                <| fun _ ->
                    match parseOk "SELECT a FROM t" with
                    | Select _ -> ()
                    | other -> failtestf "expected a plain Select, got %A" other

                testCase "trailing ORDER BY/LIMIT after a UNION apply to the combined result"
                <| fun _ ->
                    match parseOk "SELECT a FROM t UNION SELECT a FROM u ORDER BY a LIMIT 5" with
                    | Union(_, _, [ (Col "a", Asc) ], Some 5, None) -> ()
                    | other -> failtestf "expected the trailing ORDER BY/LIMIT to land on the Union, got %A" other

                testCase "(SELECT ...) UNION (SELECT ...) — each branch individually parenthesized"
                <| fun _ ->
                    match parseOk "(SELECT a FROM t) UNION (SELECT a FROM u)" with
                    | Union(_, [ (false, _) ], _, _, _) -> ()
                    | other -> failtestf "expected a two-branch Union, got %A" other

                testCase "FROM ((SELECT ...) UNION (SELECT ...)) AS alias — a UNION as a derived table"
                <| fun _ ->
                    // Laravel's `unionAll(...)->paginate()` compiles to exactly
                    // this shape: `SELECT COUNT(*) FROM ((SELECT ...) UNION
                    // (SELECT ...)) AS alias`.
                    match parseOk "SELECT COUNT(*) AS aggregate FROM ((SELECT a FROM t) UNION (SELECT a FROM u)) AS search_items" with
                    | Select { From = Some(FromSubquery(UnionSelect(_, [ (false, _) ], _, _, _), "search_items")) } -> ()
                    | other -> failtestf "expected a FromSubquery wrapping a UnionSelect, got %A" other

                testCase "FROM ((SELECT ...)) AS alias — redundant double parens around a plain SELECT"
                <| fun _ ->
                    match parseOk "SELECT * FROM ((SELECT id FROM t)) AS derived" with
                    | Select { From = Some(FromSubquery(PlainSelect { From = Some(FromTable { Table = "t" }) }, "derived")) } -> ()
                    | other -> failtestf "expected a FromSubquery wrapping a PlainSelect, got %A" other ]

          testList
              "FOR UPDATE / LOCK IN SHARE MODE"
              [ testCase "FOR UPDATE sets Locking"
                <| fun _ ->
                    match parseOk "SELECT * FROM t FOR UPDATE" with
                    | Select { Locking = true } -> ()
                    | other -> failtestf "expected Locking = true, got %A" other

                testCase "LOCK IN SHARE MODE sets Locking"
                <| fun _ ->
                    match parseOk "SELECT * FROM t LOCK IN SHARE MODE" with
                    | Select { Locking = true } -> ()
                    | other -> failtestf "expected Locking = true, got %A" other

                testCase "no locking clause leaves Locking false"
                <| fun _ ->
                    match parseOk "SELECT * FROM t" with
                    | Select { Locking = false } -> ()
                    | other -> failtestf "expected Locking = false, got %A" other ]

          testList
              "failure cases"
              [ testCase "garbage input is an Error, not an exception"
                <| fun _ ->
                    match parse "not sql at all !!" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an error, got %A" stmt

                testCase "unterminated string is an Error"
                <| fun _ ->
                    match parse "SELECT 'unterminated" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an error, got %A" stmt

                testCase "trailing garbage after a valid statement is an Error"
                <| fun _ ->
                    // `SELECT 1 EXTRA` is valid MySQL — `EXTRA` is a bare
                    // column alias, no `AS` required, covered by the
                    // "implicit alias" test below. A trailing number can't
                    // be an alias, so it's still unambiguous garbage.
                    match parse "SELECT 1 42" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an error, got %A" stmt

                testCase "empty input is an Error"
                <| fun _ ->
                    match parse "" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an error, got %A" stmt

                testCase "SELECT @@version is out of scope for this parser"
                <| fun _ ->
                    match parse "SELECT @@version" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an error, got %A" stmt ] ]
