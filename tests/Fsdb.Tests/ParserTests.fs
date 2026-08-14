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

/// Builds a `Select` statement from the same positional shape the old
/// tuple-based `Ast.Select` case had, so every test below reads as a plain
/// AST comparison instead of a record literal per case; `from` is still a
/// bare table name string here since none of these tests exercise a
/// qualified name or alias.
let private mkSelect
    (projections: Projection list, from: string option, where: Expr option, orderBy: OrderKey list, limit: int option, offset: int option)
    : Statement =
    Select
        { Projections = projections
          From = from |> Option.map (fun t -> { Database = None; Table = t; Alias = None })
          Where = where
          OrderBy = orderBy
          Limit = limit
          Offset = offset }

let tests =
    testList
        "parser"
        [ testList
              "SELECT"
              [ testCase "SELECT * FROM t"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t")
                        (mkSelect([ Star, None ], Some "t", None, [], None, None))
                        "select star"

                testCase "FROM db.table AS alias parses a qualified, aliased TableRef"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM information_schema.tables AS t")
                        (Select
                            { Projections = [ Star, None ]
                              From = Some { Database = Some "information_schema"; Table = "tables"; Alias = Some "t" }
                              Where = None
                              OrderBy = []
                              Limit = None
                              Offset = None })
                        "qualified aliased table ref"

                testCase "FROM t x: alias without AS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t x")
                        (Select
                            { Projections = [ Star, None ]
                              From = Some { Database = None; Table = "t"; Alias = Some "x" }
                              Where = None
                              OrderBy = []
                              Limit = None
                              Offset = None })
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

                testCase "SELECT t.* is Star"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT t.* FROM t")
                        (mkSelect([ Star, None ], Some "t", None, [], None, None))
                        "qualified star"

                testCase "COUNT(*) shape"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT COUNT(*) FROM t")
                        (mkSelect([ FuncCall("COUNT", [ Star ]), None ], Some "t", None, [], None, None))
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

                testCase "WHERE clause"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t WHERE a = 1")
                        (mkSelect([ Star, None ], Some "t", Some(BinOp(Eq, col "a", Lit(VInt 1L))), [], None, None))
                        "where"

                testCase "ORDER BY with explicit and default direction"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t ORDER BY a DESC, b")
                        (mkSelect([ Star, None ], Some "t", None, [ col "a", Desc; col "b", Asc ], None, None))
                        "order by"

                testCase "LIMIT n"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 10")
                        (mkSelect([ Star, None ], Some "t", None, [], Some 10, None))
                        "limit n"

                testCase "LIMIT n OFFSET m"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 10 OFFSET 5")
                        (mkSelect([ Star, None ], Some "t", None, [], Some 10, Some 5))
                        "limit offset"

                testCase "LIMIT m, n means offset m, count n"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t LIMIT 5, 10")
                        (mkSelect([ Star, None ], Some "t", None, [], Some 10, Some 5))
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
                            [ Like(col "a", Lit(VString "%x%")), None
                              Not(Like(col "a", Lit(VString "%y%"))), None ],
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
                            [ Star, None ],
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
                                Type = TInt
                                Nullable = true
                                Default = None
                                AutoIncrement = true
                                PrimaryKey = true }
                              { Name = "name"
                                Type = TVarchar 255
                                Nullable = false
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false }
                              { Name = "score"
                                Type = TDecimal(5, 2)
                                Nullable = true
                                Default = Some(DConst(VInt 0L))
                                AutoIncrement = false
                                PrimaryKey = false } ],
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
                                Type = TInt
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false } ],
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
                                Type = TInt
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = true }
                              { Name = "name"
                                Type = TVarchar 10
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false } ],
                            false
                        ))
                        "trailing primary key"

                testCase "BIGINT UNSIGNED"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id BIGINT UNSIGNED)" with
                    | CreateTable(_, [ { Type = TBigInt true } ], _) -> ()
                    | other -> failtestf "expected an unsigned bigint column, got %A" other

                testCase "DEFAULT CURRENT_TIMESTAMP"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)" with
                    | CreateTable(_, [ { Type = TTimestamp; Default = Some DCurrentTimestamp } ], _) -> ()
                    | other -> failtestf "expected a CURRENT_TIMESTAMP default, got %A" other

                testCase "ENGINE=/CHARSET=/COLLATE= table options are ignored but accepted"
                <| fun _ ->
                    Expect.equal
                        (parseOk
                            "CREATE TABLE t (id INT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci")
                        (CreateTable(
                            "t",
                            [ { Name = "id"
                                Type = TInt
                                Nullable = true
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false } ],
                            false
                        ))
                        "table options" ]

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
                <| fun _ -> Expect.equal (parseOk "TRUNCATE t") (Truncate "t") "truncate" ]

          testList
              "INSERT"
              [ testCase "INSERT INTO t (cols) VALUES (...), (...)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) VALUES (1, 'x'), (2, 'y')")
                        (Insert(
                            "t",
                            [ "a"; "b" ],
                            [ [ Lit(VInt 1L); Lit(VString "x") ]; [ Lit(VInt 2L); Lit(VString "y") ] ]
                        ))
                        "insert with columns"

                testCase "INSERT INTO t VALUES (...) without a column list"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t VALUES (1, 2)")
                        (Insert("t", [], [ [ Lit(VInt 1L); Lit(VInt 2L) ] ]))
                        "insert without columns" ]

          testList
              "UPDATE / DELETE"
              [ testCase "UPDATE t SET a=expr, b=expr WHERE ..."
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t SET a = 1, b = a + 1 WHERE id = 5")
                        (Update(
                            "t",
                            [ "a", Lit(VInt 1L); "b", BinOp(Add, col "a", Lit(VInt 1L)) ],
                            Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                        ))
                        "update"

                testCase "UPDATE without WHERE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t SET a = 1")
                        (Update("t", [ "a", Lit(VInt 1L) ], None))
                        "update without where"

                testCase "DELETE FROM t WHERE ..."
                <| fun _ ->
                    Expect.equal
                        (parseOk "DELETE FROM t WHERE id = 5")
                        (Delete("t", Some(BinOp(Eq, col "id", Lit(VInt 5L)))))
                        "delete"

                testCase "DELETE FROM t without WHERE"
                <| fun _ -> Expect.equal (parseOk "DELETE FROM t") (Delete("t", None)) "delete without where" ]

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
                    match parse "SELECT 1 EXTRA" with
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
