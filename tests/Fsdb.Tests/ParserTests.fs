module Fsdb.Tests.ParserTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Parser
open Fsdb.Sql

/// Parses `sql` and fails the test with the parse error if it doesn't
/// succeed, so every happy-path test reads as a plain AST comparison.
let private parseOk (sql: string) : Statement =
    match parse sql with
    | Ok stmt -> stmt
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg

let private col name = Col name

let private createTableSpec name columns =
    { Name = name
      Columns = columns
      Indexes = []
      ForeignKeys = []
      Checks = []
      IfNotExists = false
      Charset = None
      Collation = None
      AutoIncrementSeed = None
      Comment = None }

/// Builds a `Select` statement from a flat positional tuple, so every test
/// below reads as a plain AST comparison instead of a record literal per
/// case; `from` is still a bare table name string here since none of these
/// tests exercise a qualified name or alias.
let private mkSelect
    (projections: Projection list, from: string option, where: Expr option, orderBy: OrderKey list, limit: int option, offset: int option)
    : Statement =
    Select
        { Projections = projections
          IntoVariables = []
          Distinct = false
          CalculateFoundRows = false
          StraightJoin = false
          From = from |> Option.map (fun t -> FromTable { Database = None; Table = t; Alias = None })
          Joins = []
          Where = where
          GroupBy = []
          Rollup = false
          Windows = []
          Ctes = []
          Having = None
          OrderBy = orderBy
          Limit = limit |> Option.map (int64 >> VInt >> Lit)
          Offset = offset |> Option.map (int64 >> VInt >> Lit)
          Locking = false }

let tests =
    testList
        "parser"
        [ testList
              "expression traversal"
              [ testCase "walks window arguments, ordering, and frame bounds in encounter order"
                <| fun _ ->
                    let expression =
                        WindowOver(
                            WinLagLead(false, Col "value", Some(Col "offset"), Some(Col "fallback")),
                            OverSpec
                                { Inherit = None
                                  PartitionBy = [ Col "partition" ]
                                  OrderBy = [ Col "order", Asc ]
                                  Frame =
                                    Some
                                        { Unit = FrameRows
                                          Start = BoundPreceding(Col "start")
                                          End = BoundFollowing(Col "finish") } }
                        )

                    let columns =
                        expression
                        |> Expression.collect (function Col name -> Some name | _ -> None)

                    Expect.equal columns [ "value"; "offset"; "fallback"; "partition"; "order"; "start"; "finish" ] "columns"

                testCase "keeps nested selects behind an explicit boundary"
                <| fun _ ->
                    let nested =
                        match parseOk "SELECT hidden FROM t" with
                        | Select select -> select
                        | statement -> failtestf "expected SELECT, got %A" statement

                    let expression = BinOp(Eq, Col "visible", Subquery nested)

                    Expect.equal
                        (expression |> Expression.collect (function Col name -> Some name | _ -> None))
                        [ "visible" ]
                        "expression children"

                    Expect.equal (Expression.subqueries expression) [] "root subqueries"
                    Expect.equal (Expression.subqueries (Subquery nested)) [ nested ] "nested select"

                testCase "replacement prunes the replaced subtree"
                <| fun _ ->
                    let expression = FuncCall("OUTER", [ FuncCall("TARGET", [ Col "hidden" ]); Col "visible" ])

                    let rewritten =
                        expression
                        |> Expression.rewrite (function
                            | FuncCall("TARGET", _) -> Some(Lit(VInt 7L))
                            | _ -> None)

                    Expect.equal rewritten (FuncCall("OUTER", [ Lit(VInt 7L); Col "visible" ])) "rewritten expression" ]

          testList
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

                testCase "SELECT SQL_CALC_FOUND_ROWS carries the modifier"
                <| fun _ ->
                    match parseOk "SELECT SQL_CALC_FOUND_ROWS name FROM t LIMIT 2" with
                    | Select { CalculateFoundRows = true; Limit = Some(Lit(VInt 2L)) } -> ()
                    | other -> failtestf "expected CalculateFoundRows = true, got %A" other

                testCase "SELECT STRAIGHT_JOIN carries the join-order constraint"
                <| fun _ ->
                    match parseOk "SELECT STRAIGHT_JOIN t.id FROM t JOIN u ON u.id = t.id" with
                    | Select { StraightJoin = true; Joins = [ _ ] } -> ()
                    | other -> failtestf "expected StraightJoin = true, got %A" other

                testCase "FROM db.table AS alias parses a qualified, aliased TableRef"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM information_schema.tables AS t")
                        (Select
                            { Projections = [ Star None, None ]
                              IntoVariables = []
                              Distinct = false
                              CalculateFoundRows = false
                              StraightJoin = false
                              From = Some(FromTable { Database = Some "information_schema"; Table = "tables"; Alias = Some "t" })
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
                              Locking = false })
                        "qualified aliased table ref"

                testCase "FROM t x: alias without AS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT * FROM t x")
                        (Select
                            { Projections = [ Star None, None ]
                              IntoVariables = []
                              Distinct = false
                              CalculateFoundRows = false
                              StraightJoin = false
                              From = Some(FromTable { Database = None; Table = "t"; Alias = Some "x" })
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

                testCase "function names require an adjacent opening parenthesis"
                <| fun _ ->
                    for sql in
                        [ "SELECT COUNT (*) FROM t"
                          "SELECT COUNT/**/(*) FROM t"
                          "SELECT CAST (1 AS SIGNED)"
                          "SELECT EXTRACT (YEAR FROM created_at) FROM t" ] do
                        match parse sql with
                        | Ok statement -> failtestf "expected %s to fail, got %A" sql statement
                        | Error _ -> ()

                testCase "ordinary function names permit whitespace before arguments"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT HEX /**/ ('a')")
                        (mkSelect([ FuncCall("HEX", [ Lit(VString "a") ]), None ], None, None, [], None, None))
                        "hex call"

                testCase "IGNORE_SPACE permits spaced built-ins and reserves their unquoted names"
                <| fun _ ->
                    let options: ParserOptions =
                        { defaultOptions with IgnoreSpace = true }

                    Expect.equal
                        (parseWithOptions options "SELECT COUNT (*) FROM t")
                        (Ok(mkSelect([ FuncCall("COUNT", [ Star None ]), None ], Some "t", None, [], None, None)))
                        "spaced COUNT"

                    for sql in
                        [ "SELECT CAST (1 AS SIGNED)"
                          "SELECT EXTRACT (YEAR FROM created_at) FROM t"
                          "SELECT GROUP_CONCAT (a) FROM t"
                          "SELECT NOW ()"
                          "SELECT TRIM (' a ')" ] do
                        match parseWithOptions options sql with
                        | Ok _ -> ()
                        | Error error -> failtestf "expected IGNORE_SPACE to parse %s, got %s" sql error

                    match parseWithOptions options "SELECT COUNT /**/ (*) FROM t" with
                    | Error _ -> ()
                    | Ok statement -> failtestf "expected an intervening comment to remain invalid, got %A" statement

                    match parseWithOptions options "CREATE TABLE count (i INT)" with
                    | Error _ -> ()
                    | Ok statement -> failtestf "expected COUNT to be reserved, got %A" statement

                    match parseWithOptions options "CREATE TABLE `count` (i INT)" with
                    | Ok(CreateTable { Name = "count" }) -> ()
                    | other -> failtestf "expected a quoted COUNT table, got %A" other

                testCase "PIPES_AS_CONCAT rewrites only unquoted SQL operators"
                <| fun _ ->
                    let options: ParserOptions =
                        { defaultOptions with PipesAsConcat = true }

                    Expect.equal
                        (parse "SELECT 1 || 0")
                        (Ok(mkSelect([ BinOp(Or, Lit(VInt 1L), Lit(VInt 0L)), None ], None, None, [], None, None)))
                        "default pipes are logical OR"

                    Expect.equal
                        (parseWithOptions options "SELECT 'a' || 'b'")
                        (Ok(mkSelect([ FuncCall("CONCAT", [ Lit(VString "a"); Lit(VString "b") ]), None ], None, None, [], None, None)))
                        "mode pipes are CONCAT"

                    match parseWithOptions options "SELECT 'a||b', 'a' /* || */ || 'b'" with
                    | Ok(Select { Projections = [ Lit(VString "a||b"), _; FuncCall("CONCAT", _), _ ] }) -> ()
                    | other -> failtestf "expected quoted/comment pipes to remain untouched, got %A" other

                    match parseWithOptions options "SELECT /*!80000 'a' || */ 'b'" with
                    | Ok(Select { Projections = [ FuncCall("CONCAT", _), _ ] }) -> ()
                    | other -> failtestf "expected executable-comment pipes to use the active mode, got %A" other

                testCase "NO_BACKSLASH_ESCAPES keeps backslashes literal"
                <| fun _ ->
                    let options =
                        { defaultOptions with
                            NoBackslashEscapes = true }

                    match parseWithOptions options "SELECT 'a\\nb', 'it''s'" with
                    | Ok(Select { Projections = [ Lit(VString "a\\nb"), None; Lit(VString "it's"), None ] }) -> ()
                    | other -> failtestf "expected literal backslashes and doubled quotes, got %A" other

                    match parseWithOptions options "SELECT 'it\\'s'" with
                    | Error _ -> ()
                    | Ok statement -> failtestf "expected a backslash before a quote to remain literal, got %A" statement

                    match parseWithOptions options "SELECT 'x\\' /*!80000 + 1 */" with
                    | Ok(Select { Projections = [ BinOp(Add, Lit(VString "x\\"), Lit(VInt 1L)), None ] }) -> ()
                    | other -> failtestf "expected executable comments to respect quote boundaries, got %A" other

                testCase "SELECT cannot be parsed as a function name"
                <| fun _ ->
                    match parse "SELECT SELECT(1)" with
                    | Ok statement -> failtestf "expected syntax error, got %A" statement
                    | Error _ -> ()

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

                testCase "WEIGHT_STRING casts its argument before computing weights"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT WEIGHT_STRING(name AS CHAR(3)), WEIGHT_STRING(name AS BINARY(4)) FROM t")
                        (mkSelect(
                            [ FuncCall("WEIGHT_STRING", [ Cast(col "name", TChar 3) ]), None
                              FuncCall("WEIGHT_STRING", [ Cast(col "name", TBinary 4) ]), None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "weight string modifiers"

                testCase "WEIGHT_STRING rejects omitted and zero widths"
                <| fun _ ->
                    for sql in
                        [ "SELECT WEIGHT_STRING('a' AS CHAR)"
                          "SELECT WEIGHT_STRING('a' AS BINARY)"
                          "SELECT WEIGHT_STRING('a' AS CHAR(0))"
                          "SELECT WEIGHT_STRING('a' LEVEL 1)" ] do
                        match parse sql with
                        | Ok statement -> failtestf "expected %s to fail, got %A" sql statement
                        | Error _ -> ()

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

                    match
                        parseOk
                            "SELECT id FROM t ORDER BY CASE WHEN (course = 5 OR name = 'xyz') THEN 0 ELSE 1 END, name, course"
                    with
                    | Select select ->
                        match select.OrderBy with
                        | (Case(None, _, Some(Lit(VInt 1L))), Asc) :: tail ->
                            Expect.equal tail [ col "name", Asc; col "course", Asc ] "remaining order keys"
                        | other -> failtestf "expected searched CASE ordering, got %A" other
                    | other -> failtestf "expected searched CASE ordering, got %A" other

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

                testCase "HIGH_NOT_PRECEDENCE binds prefix NOT before comparisons"
                <| fun _ ->
                    let options =
                        { defaultOptions with
                            HighNotPrecedence = true }

                    Expect.equal
                        (parseWithOptions options "SELECT nOt a = b, NOT 1 BETWEEN -1 AND 1")
                        (Ok(mkSelect(
                            [ BinOp(Eq, Not(col "a"), col "b"), None
                              Between(Not(Lit(VInt 1L)), Lit(VInt -1L), Lit(VInt 1L)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        )))
                        "prefix NOT should bind like unary minus"

                    Expect.equal
                        (parseWithOptions options "SELECT a NOT BETWEEN 1 AND 2, a NOT IN (1, 2), a NOT LIKE 'x'")
                        (Ok(mkSelect(
                            [ Not(Between(col "a", Lit(VInt 1L), Lit(VInt 2L))), None
                              Not(In(col "a", [ Lit(VInt 1L); Lit(VInt 2L) ])), None
                              Not(Like(col "a", Lit(VString "x"), false, None)), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        )))
                        "infix NOT predicates should retain their grammar"

                testCase "a AND NOT b: NOT binds only to b"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT a AND NOT b")
                        (mkSelect([ BinOp(And, col "a", Not(col "b")), None ], None, None, [], None, None))
                        "not scoped to right operand"

                testCase "SELECT 1--1 is subtraction (1 - -1), not `1` with a trailing comment"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1--1")
                        (mkSelect([ BinOp(Sub, Lit(VInt 1L), Lit(VInt -1L)), None ], None, None, [], None, None))
                        "-- with no trailing space is not a comment; the sign folds into the literal"

                testCase "-- followed by a space is still a line comment"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 1 -- trailing comment\n")
                        (mkSelect([ Lit(VInt 1L), None ], None, None, [], None, None))
                        "-- with trailing space is a comment"

                testCase "0x41 parses as a hex literal, not `0` aliased as `x41`"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT 0x41")
                        (mkSelect([ Lit(VBytes [| 0x41uy |]), None ], None, None, [], None, None))
                        "hex literal"

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

                testCase "row constructors remain expressions inside comparisons and IN"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT (a, b) = (1, 2), (a, b) IN ((1, 2), (3, 4)) FROM t")
                        (mkSelect(
                            [ BinOp(Eq, Row [ col "a"; col "b" ], Row [ Lit(VInt 1L); Lit(VInt 2L) ]), None
                              In(
                                  Row [ col "a"; col "b" ],
                                  [ Row [ Lit(VInt 1L); Lit(VInt 2L) ]
                                    Row [ Lit(VInt 3L); Lit(VInt 4L) ] ]
                              ),
                              None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "row constructor AST"

                testCase "ROW constructors parse as rows, including nested and parameterized forms"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT ROW(a, b) = (1, 2), ROW((1, 2), 3) = ROW(ROW(1, 2), 3), ROW(?, ?) IN (ROW(1, 2), (3, 4)) FROM t")
                        (mkSelect(
                            [ BinOp(Eq, Row [ Col "a"; Col "b" ], Row [ Lit(VInt 1L); Lit(VInt 2L) ]), None
                              BinOp(
                                  Eq,
                                  Row [ Row [ Lit(VInt 1L); Lit(VInt 2L) ]; Lit(VInt 3L) ],
                                  Row [ Row [ Lit(VInt 1L); Lit(VInt 2L) ]; Lit(VInt 3L) ]
                              ),
                              None
                              In(Row [ Placeholder 0; Placeholder 1 ], [ Row [ Lit(VInt 1L); Lit(VInt 2L) ]; Row [ Lit(VInt 3L); Lit(VInt 4L) ] ]), None ],
                            Some "t",
                            None,
                            [],
                            None,
                            None
                        ))
                        "ROW constructor AST"

                    for sql in [ "SELECT ROW()"; "SELECT ROW(1)" ] do
                        match parse sql with
                        | Error _ -> ()
                        | Ok statement -> failtestf "expected %s to be a syntax error, got %A" sql statement

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
              [ testCase "an integer beyond BIGINT remains exact while it fits DECIMAL"
                <| fun _ ->
                    match parseOk "SELECT 99999999999999999999" with
                    | Select { Projections = [ Lit(VDecimal 99999999999999999999M), None ] } -> ()
                    | other -> failtestf "expected an exact VDecimal, got %A" other

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

                testCase "charset introducers require a literal unless qualified or quoted"
                <| fun _ ->
                    [ "_utf8mb4"; "_utf8mb3"; "_sjis"; "_binary" ]
                    |> List.iter (fun introducer -> Expect.isError (parse ("SELECT " + introducer)) introducer)

                    Expect.isError (parse "SELECT ROW(1, 'A') = (SELECT 1, _utf8mb4)") "subquery introducer"

                    match parseOk "SELECT t._sjis, `_utf8mb4` FROM t" with
                    | Select { Projections = [ (QualifiedCol("t", "_sjis"), None); (Col "_utf8mb4", None) ] } -> ()
                    | other -> failtestf "expected qualified and quoted identifiers, got %A" other

                testCase "bit and national string literals parse as MySQL literals"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT b'0101', B'111111111', 0b0101, b'', N'héllo'")
                        (mkSelect(
                            [ Lit(VBytes [| 0x05uy |]), None
                              Lit(VBytes [| 0x01uy; 0xffuy |]), None
                              Lit(VBytes [| 0x05uy |]), None
                              Lit(VBytes [||]), None
                              Lit(VString "héllo"), None ],
                            None,
                            None,
                            [],
                            None,
                            None
                        ))
                        "literal values"

                testCase "backtick-quoted identifier, including a reserved word and a doubled backtick"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT `order`, `a``b` FROM `select`")
                        (mkSelect([ col "order", None; col "a`b", None ], Some "select", None, [], None, None))
                        "backtick identifiers"

                testCase "ROW and PARTITION cannot become implicit aliases"
                <| fun _ ->
                    Expect.isError (parse "SELECT ROW(1, 2) ROW") "ROW alias"
                    Expect.isError (parse "SELECT id FROM t PARTITION") "PARTITION alias"

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
                    Expect.equal (stripVersionComments "SELECT /*!99999 SQL_NO_CACHE */ 1") "SELECT   1" "above server version is inert"

                testCase "version-gated comments use the advertised MySQL 8.4 compatibility version"
                <| fun _ ->
                    Expect.equal (stripVersionComments "/*!80400 SET @at_version = 1 */") " SET @at_version = 1 " "8.4.0 executes"
                    Expect.equal (stripVersionComments "/*!80401 SET @above_version = 1 */") " " "versions above 8.4.0 are inert"

                testCase "version-gated comments accept a six-digit MMmmrr version"
                <| fun _ ->
                    Expect.equal (stripVersionComments "SELECT /*!999999 BOGUSTOKEN */ 2") "SELECT   2" "six digits gate the comment"

                testCase "fewer than five version digits leave an ordinary comment"
                <| fun _ ->
                    Expect.equal (stripVersionComments "SELECT /*!9999 body */ 1") "SELECT   1" "a short digit run is inert"

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
                            { createTableSpec
                                "t"
                                [ { Name = "id"
                                    Type = TInt false
                                    Nullable = true
                                    Default = None
                                    AutoIncrement = true
                                    PrimaryKey = true
                                    Unique = false
                                    Generated = None
                                    Comment = ""
                                    Collation = None
                                    Charset = None
                                    OnUpdateCurrentTimestamp = false };
                              { Name = "name"
                                Type = TVarchar 255
                                Nullable = false
                                Default = None
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None
                                Comment = ""
                                Collation = Some "utf8mb4_0900_ai_ci"
                                Charset = None
                                OnUpdateCurrentTimestamp = false };
                              { Name = "score"
                                Type = TDecimal(5, 2, false)
                                Nullable = true
                                Default = Some(DConst(VInt 0L))
                                AutoIncrement = false
                                PrimaryKey = false
                                Unique = false
                                Generated = None
                                Comment = ""
                                Collation = None
                                Charset = None
                                OnUpdateCurrentTimestamp = false } ] with
                                Indexes =
                                    [ { Name = "PRIMARY"
                                        KeyColumns = indexColumns [ "id" ]
                                        Unique = true
                                        Kind = BTree } ] }
                        ))
                        "create table"

                testCase "column comments parse on CREATE and ALTER definitions"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id INT COMMENT 'identifier', body TEXT COMMENT 'line\\ntext')" with
                    | CreateTable { Columns = [ { Name = "id"; Comment = "identifier" }; { Name = "body"; Comment = "line\ntext" } ] } -> ()
                    | other -> failtestf "expected CREATE TABLE column comments, got %A" other

                    match parseOk "ALTER TABLE t MODIFY id BIGINT COMMENT 'replacement', CHANGE body content TEXT COMMENT ''" with
                    | AlterTable(_, [ ModifyColumn({ Name = "id"; Comment = "replacement" }, _); ChangeColumn("body", { Name = "content"; Comment = "" }, _) ]) -> ()
                    | other -> failtestf "expected ALTER TABLE column comments, got %A" other

                testCase "CREATE TABLE LIKE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE TABLE IF NOT EXISTS archive LIKE app.source")
                        (CreateTableLike("archive", "app.source", true))
                        "create table like"

                    Expect.equal
                        (parseOk "CREATE TABLE archive (LIKE app.source)")
                        (CreateTableLike("archive", "app.source", false))
                        "parenthesized create table like"

                testCase "CREATE TABLE AS SELECT"
                <| fun _ ->
                    match parseOk "CREATE TABLE IF NOT EXISTS archive AS SELECT id, name FROM source" with
                    | CreateTableAs("archive", Select { Projections = [ Col "id", None; Col "name", None ] }, true) -> ()
                    | other -> failtestf "expected CREATE TABLE AS SELECT, got %A" other

                    match parseOk "CREATE TABLE archive ENGINE=MEMORY SELECT name FROM source" with
                    | CreateTableAs("archive", Select { Projections = [ Col "name", None ] }, false) -> ()
                    | other -> failtestf "expected CREATE TABLE AS SELECT with options, got %A" other

                testCase "IF NOT EXISTS"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE TABLE IF NOT EXISTS t (id INT)")
                        (CreateTable
                            { createTableSpec
                                "t"
                                [ { Name = "id"
                                    Type = TInt false
                                    Nullable = true
                                    Default = None
                                    AutoIncrement = false
                                    PrimaryKey = false
                                    Unique = false
                                    Generated = None
                                    Comment = ""
                                    Collation = None
                                    Charset = None
                                    OnUpdateCurrentTimestamp = false } ] with
                                IfNotExists = true })
                        "if not exists"

                testCase "trailing PRIMARY KEY (col) marks the referenced column"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE TABLE t (id INT, name VARCHAR(10), PRIMARY KEY (id))")
                        (CreateTable(
                            { createTableSpec
                                "t"
                                [ { Name = "id"
                                    Type = TInt false
                                    Nullable = true
                                    Default = None
                                    AutoIncrement = false
                                    PrimaryKey = true
                                    Unique = false
                                    Generated = None
                                    Comment = ""
                                    Collation = None
                                    Charset = None
                                    OnUpdateCurrentTimestamp = false };
                                  { Name = "name"
                                    Type = TVarchar 10
                                    Nullable = true
                                    Default = None
                                    AutoIncrement = false
                                    PrimaryKey = false
                                    Unique = false
                                    Generated = None
                                    Comment = ""
                                    Collation = Some "utf8mb4_0900_ai_ci"
                                    Charset = None
                                    OnUpdateCurrentTimestamp = false } ] with
                                Indexes =
                                    [ { Name = "PRIMARY"
                                        KeyColumns = indexColumns [ "id" ]
                                        Unique = true
                                        Kind = BTree } ] }
                        ))
                        "trailing primary key"

                testCase "CONSTRAINT accepts an optional primary-key symbol"
                <| fun _ ->
                    for sql in
                        [ "CREATE TABLE t (id INT, CONSTRAINT PRIMARY KEY (id))"
                          "CREATE TABLE t (id INT, CONSTRAINT pk_t PRIMARY KEY (id))" ] do
                        match parseOk sql with
                        | CreateTable { Columns = [ { Name = "id"; PrimaryKey = true } ] } -> ()
                        | other -> failtestf "expected constrained primary key, got %A" other

                    let magento =
                        "CREATE TABLE `admin_analytics_usage_version_log` (`id` int UNSIGNED NOT NULL AUTO_INCREMENT COMMENT \"Log ID\", `last_viewed_in_version` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL COMMENT \"Viewer last viewed on product version\", CONSTRAINT PRIMARY KEY (`id`), CONSTRAINT `ADMIN_ANALYTICS_USAGE_VERSION_LOG_LAST_VIEWED_IN_VERSION` UNIQUE KEY (`last_viewed_in_version`)) ENGINE=innodb DEFAULT CHARSET=utf8mb4 DEFAULT COLLATE=utf8mb4_general_ci COMMENT=\"Admin Notification Viewer Log Table\""

                    match parseOk magento with
                    | CreateTable
                        { Columns = [ { PrimaryKey = true }; _ ]
                          Indexes = [ { Name = "PRIMARY" }; { Name = "ADMIN_ANALYTICS_USAGE_VERSION_LOG_LAST_VIEWED_IN_VERSION"; Unique = true } ] } -> ()
                    | other -> failtestf "expected Magento constraints, got %A" other

                testCase "composite primary keys retain declaration order"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (first INT, second INT, PRIMARY KEY (second, first))" with
                    | CreateTable { Indexes = [ { Name = "PRIMARY"; KeyColumns = [ { Name = "second" }; { Name = "first" } ] } ] } -> ()
                    | other -> failtestf "expected an ordered primary index, got %A" other

                testCase "BIGINT UNSIGNED"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id BIGINT UNSIGNED)" with
                    | CreateTable { Columns = [ { Type = TBigInt true } ] } -> ()
                    | other -> failtestf "expected an unsigned bigint column, got %A" other

                testCase "NUMERIC precision defaults to scale zero"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (grade NUMERIC(20), percent NUMERIC(5,2), ratio DECIMAL(8,3) UNSIGNED) DEFAULT COLLATE utf8mb4_unicode_ci ROW_FORMAT=DYNAMIC" with
                    | CreateTable { Columns = columns } ->
                        Expect.equal
                            (columns |> List.map _.Type)
                            [ TDecimal(20, 0, false); TDecimal(5, 2, false); TDecimal(8, 3, true) ]
                            "numeric column types"
                    | other -> failtestf "expected numeric precision and scale, got %A" other

                testCase "BIT keeps its declared width and defaults to one bit"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (one BIT, three BIT(3), all_bits BIT(64))" with
                    | CreateTable { Columns = [ { Type = TBit 1 }; { Type = TBit 3 }; { Type = TBit 64 } ] } -> ()
                    | other -> failtestf "expected BIT widths, got %A" other

                testCase "DEFAULT CURRENT_TIMESTAMP"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)" with
                    | CreateTable { Columns = [ { Type = TTimestamp 0; Default = Some DCurrentTimestamp } ] } -> ()
                    | other -> failtestf "expected a CURRENT_TIMESTAMP default, got %A" other

                testCase "functional default expression"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (n INT DEFAULT (ABS(-2)))" with
                    | CreateTable { Columns = [ { Default = Some(DExpression(FuncCall(name, _))) } ] }
                        when name.Equals("ABS", System.StringComparison.OrdinalIgnoreCase) -> ()
                    | other -> failtestf "expected a functional default, got %A" other

                testCase "ENGINE=/CHARSET= are accepted; the table's defaults stay table-level (numeric columns don't inherit them)"
                <| fun _ ->
                    Expect.equal
                        (parseOk
                            "CREATE TABLE t (id INT) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci")
                        (CreateTable
                            { createTableSpec
                                "t"
                                [ { Name = "id"
                                    Type = TInt false
                                    Nullable = true
                                    Default = None
                                    AutoIncrement = false
                                    PrimaryKey = false
                                    Unique = false
                                    Generated = None
                                    Comment = ""
                                    Collation = None
                                    Charset = None
                                    OnUpdateCurrentTimestamp = false } ] with
                                Charset = Some "utf8mb4"
                                Collation = Some "utf8mb4_unicode_ci" })
                        "table defaults kept on the table; an INT column inherits nothing"

                testCase "application-generated indexes, negative defaults, and comma-separated table options parse"
                <| fun _ ->
                    let statements =
                        [ "CREATE TABLE `actor` (actor_id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL, actor_user INT UNSIGNED DEFAULT NULL, actor_name VARBINARY(255) NOT NULL, UNIQUE INDEX actor_user (actor_user), UNIQUE INDEX actor_name (actor_name), PRIMARY KEY(actor_id)) ENGINE=InnoDB, DEFAULT CHARSET=binary"
                          "CREATE TABLE promotion_sales_channel (id BINARY(16) NOT NULL, promotion_id BINARY(16) NOT NULL, sales_channel_id BINARY(16) NOT NULL, INDEX ix_sales_channel (sales_channel_id ASC), INDEX ix_promotion (promotion_id ASC), PRIMARY KEY (id))"
                          "CREATE TABLE oc_file_locks (id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL, `lock` INT DEFAULT 0 NOT NULL, `key` VARCHAR(64) NOT NULL, ttl INT DEFAULT -1 NOT NULL, UNIQUE INDEX lock_key_index (`key`), INDEX lock_ttl_index (ttl), PRIMARY KEY(id)) DEFAULT CHARACTER SET UTF8 COLLATE `utf8_bin` ENGINE = InnoDB" ]

                    for sql in statements do
                        match parseOk sql with
                        | CreateTable { Columns = columns; Indexes = indexes } ->
                            Expect.isGreaterThan indexes.Length 1 "inline indexes"

                            columns
                            |> List.tryFind (fun column -> column.Name = "ttl")
                            |> Option.iter (fun column -> Expect.equal column.Default (Some(DConst(VInt -1L))) "negative default")
                        | other -> failtestf "expected generated CREATE TABLE to parse, got %A" other

                    match parseOk "CREATE TABLE task (progress DOUBLE PRECISION DEFAULT 0)" with
                    | CreateTable { Columns = [ { Type = TDouble false } ] } -> ()
                    | other -> failtestf "expected DOUBLE PRECISION to parse, got %A" other

                    match parseOk "CREATE TABLE measurements (single_value FLOAT UNSIGNED, double_value DOUBLE UNSIGNED)" with
                    | CreateTable { Columns = [ { Type = TFloat true }; { Type = TDouble true } ] } -> ()
                    | other -> failtestf "expected unsigned floating-point types, got %A" other

                    match parseOk "CREATE TABLE app_user (name VARCHAR(255) CHARACTER SET ascii BINARY, CONSTRAINT `uniq.user.name` UNIQUE (name))" with
                    | CreateTable
                        { Columns = [ { Collation = Some "ascii_bin" } ]
                          Indexes = [ { Name = "uniq.user.name"; Unique = true } ] } ->
                        ()
                    | other -> failtestf "expected binary column attributes and named unique constraints, got %A" other

                testCase "REAL_AS_FLOAT changes the REAL type synonym"
                <| fun _ ->
                    let realAsFloat =
                        { defaultOptions with
                            RealAsFloat = true }

                    match parseWithOptions realAsFloat "CREATE TABLE reading (value REAL UNSIGNED)" with
                    | Ok(CreateTable { Columns = [ { Type = TFloat true } ] }) -> ()
                    | other -> failtestf "expected REAL_AS_FLOAT to select FLOAT, got %A" other

                testCase "ANSI_QUOTES treats double-quoted table names as identifiers"
                <| fun _ ->
                    match parseWithAnsiQuotes true "CREATE TABLE \"quoted table\" (\"id\" INT PRIMARY KEY)" with
                    | Ok(CreateTable { Name = "quoted table"; Columns = [ { Name = "id" } ] }) -> ()
                    | other -> failtestf "expected ANSI-quoted identifiers, got %A" other

                    match parse "SELECT \"still a string\"" with
                    | Ok(Select { Projections = [ Lit(VString "still a string"), None ] }) -> ()
                    | other -> failtestf "expected default double-quoted string semantics, got %A" other

                testCase "HASH partition declarations are accepted"
                <| fun _ ->
                    [ "CREATE TABLE p (id INT) PARTITION BY HASH(id) PARTITIONS 4"
                      "CREATE TABLE p (id INT) PARTITION BY HASH(id)"
                      "CREATE TABLE p (id INT) PARTITION BY LINEAR HASH(id) PARTITIONS 4" ]
                    |> List.iter (fun sql ->
                        match parseOk sql with
                        | CreateTable { Name = "p"; Columns = [ { Name = "id" } ] } -> ()
                        | other -> failtestf "expected a HASH-partitioned table declaration, got %A" other)

                    match parse "CREATE TABLE p (id INT) PARTITION BY HASH(id) PARTITIONS 0" with
                    | Error _ -> ()
                    | Ok statement -> failtestf "expected zero partitions to be rejected, got %A" statement

                testCase "a column-level COLLATE wins over the table-level default, and an unknown collation is a parse error"
                <| fun _ ->
                    match
                        parseOk "CREATE TABLE t (a VARCHAR(10) COLLATE utf8mb4_bin, b VARCHAR(10)) COLLATE=utf8mb4_unicode_ci"
                    with
                    | CreateTable
                        { Columns = [ { Name = "a"; Collation = Some "utf8mb4_bin" }; { Name = "b"; Collation = Some "utf8mb4_unicode_ci" } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } ->
                        ()
                    | other -> failtestf "expected column-over-table COLLATE resolution, got %A" other

                    match parse "CREATE TABLE t (a VARCHAR(10) COLLATE no_such_collation)" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an unknown collation to be a parse error, got %A" stmt

                testCase "COLLATE with a quoted value, as Laravel's MySQL grammar emits it"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (id INT) DEFAULT CHARACTER SET utf8mb4 COLLATE 'utf8mb4_unicode_ci'" with
                    | CreateTable
                        { Name = "t"
                          Columns = [ { Name = "id" } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected the quoted collation to parse, got %A" other

                testCase "column-level UNIQUE synthesizes a unique index named after the column"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (email VARCHAR(255) UNIQUE)" with
                    | CreateTable
                        { Columns = [ { Unique = true } ]
                          Indexes = [ { Name = "email"; KeyColumns = [ { Name = "email" } ]; Unique = true; Kind = BTree } ]
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected a synthesized unique index, got %A" other

                testCase "trailing UNIQUE KEY / KEY / INDEX with an explicit name"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (a INT, b INT, UNIQUE KEY uq_a (a), KEY idx_b (b))" with
                    | CreateTable
                        { Indexes = [ { Name = "uq_a"; KeyColumns = [ { Name = "a" } ]; Unique = true; Kind = BTree };
                              { Name = "idx_b"; KeyColumns = [ { Name = "b" } ]; Unique = false; Kind = BTree } ]
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected two indexes, got %A" other

                testCase "index columns retain prefix lengths"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (body TEXT, KEY ix_body (body(191)))" with
                    | CreateTable { Indexes = [ { KeyColumns = [ { Name = "body"; PrefixLength = Some 191 } ] } ] } -> ()
                    | other -> failtestf "expected an indexed prefix, got %A" other

                testCase "trailing CONSTRAINT ... FOREIGN KEY with ON DELETE/ON UPDATE"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE posts (user_id BIGINT UNSIGNED, CONSTRAINT posts_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE ON UPDATE RESTRICT)"
                    with
                    | CreateTable
                        { Indexes = []
                          ForeignKeys = [ { Name = "posts_user_id_foreign";
                                Columns = [ "user_id" ];
                                RefTable = "users";
                                RefColumns = [ "id" ];
                                OnDelete = Some "CASCADE";
                                OnUpdate = Some "RESTRICT" } ]
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected a foreign key, got %A" other

                testCase "unnamed trailing FOREIGN KEY gets a synthesized name"
                <| fun _ ->
                    match parseOk "CREATE TABLE posts (user_id INT, FOREIGN KEY (user_id) REFERENCES users (id))" with
                    | CreateTable
                        { Indexes = []
                          ForeignKeys = [ { Name = "users_user_id_foreign" } ]
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected a synthesized FK name, got %A" other

                testCase "a bare CONSTRAINT with no symbol name before FOREIGN KEY still parses"
                <| fun _ ->
                    match parseOk "CREATE TABLE posts (user_id INT, CONSTRAINT FOREIGN KEY (user_id) REFERENCES users (id))" with
                    | CreateTable
                        { Indexes = []
                          ForeignKeys = [ { Name = "users_user_id_foreign" } ]
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected an unnamed CONSTRAINT to still synthesize a name, got %A" other

                testCase "ENUM and SET column types carry their declared values"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (status ENUM('a', 'b'), flags SET('x', 'y'))" with
                    | CreateTable
                        { Columns = [ { Type = TEnum [ "a"; "b" ] }; { Type = TSet [ "x"; "y" ] } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected enum/set types, got %A" other

                testCase "spatial column types retain their concrete geometry kind"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (g GEOMETRY, p POINT, l LINESTRING, po POLYGON, gc GEOMETRYCOLLECTION)" with
                    | CreateTable
                        { Columns = [ { Type = TGeometry Geometry };
                              { Type = TGeometry Point };
                              { Type = TGeometry LineString };
                              { Type = TGeometry Polygon };
                              { Type = TGeometry GeometryCollection } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected spatial column types, got %A" other

                testCase "CHAR/TEXT/BLOB family and TINY/MEDIUM/SMALL int variants all parse"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE t (a CHAR(3), b TINYTEXT, c MEDIUMTEXT, d LONGTEXT, e BLOB, f VARBINARY(16), g BINARY(4), h SMALLINT, i MEDIUMINT UNSIGNED, j TIME, k YEAR, l FLOAT, m DOUBLE(8,2))"
                    with
                    | CreateTable
                        { Columns = [ { Type = TChar 3 };
                              { Type = TTinyText };
                              { Type = TMediumText };
                              { Type = TLongText };
                              { Type = TBlob };
                              { Type = TVarBinary 16 };
                              { Type = TBinary 4 };
                              { Type = TSmallInt false };
                              { Type = TMediumInt true };
                              { Type = TTime 0 };
                              { Type = TYear };
                              { Type = TFloat false };
                              { Type = TDouble false } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected every new column type to parse, got %A" other

                // MySQL's BOOLEAN is TINYINT(1), and clients read the
                // width of 1 as "this is a bool" — so the two spellings and
                // the explicit width all land on the same type, while a
                // plain TINYINT stays an integer.
                testCase "BOOLEAN, BOOL and TINYINT(1) are one type, distinct from plain TINYINT"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (a BOOLEAN, b BOOL, c TINYINT(1), d TINYINT, e TINYINT(4))" with
                    | CreateTable
                        { Columns = [ { Type = TBool };
                                      { Type = TBool };
                                      { Type = TBool };
                                      { Type = TTinyInt false };
                                      { Type = TTinyInt false } ]
                          Indexes = []
                          ForeignKeys = []
                          Checks = []
                          IfNotExists = false } -> ()
                    | other -> failtestf "expected TBool for the three boolean spellings only, got %A" other

                // TINYINT(1) UNSIGNED keeps the integer reading: a client's
                // bool mapping is for the signed form.
                testCase "TINYINT(1) UNSIGNED stays an integer"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (a TINYINT(1) UNSIGNED)" with
                    | CreateTable { Columns = [ { Type = TTinyInt true } ] } -> ()
                    | other -> failtestf "expected TTinyInt true, got %A" other

                testCase "COMMENT / CHARACTER SET / COLLATE column modifiers are accepted and ignored"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE t (name VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci COMMENT 'a name')"
                    with
                    | CreateTable { Columns = [ { Name = "name" } ] } -> ()
                    | other -> failtestf "expected the comment/charset/collate to be ignored, got %A" other

                testCase "a generated column's AS (expr) [VIRTUAL|STORED] is captured on ColumnDef.Generated (Laravel Pulse's key_hash)"
                <| fun _ ->
                    match
                        parseOk
                            "CREATE TABLE pulse_values (`key` MEDIUMTEXT NOT NULL, key_hash CHAR(16) CHARACTER SET binary AS (UNHEX(MD5(`key`))))"
                    with
                    | CreateTable
                        { Columns = [ { Name = "key"; Generated = None };
                              { Name = "key_hash"; Type = TChar 16; Generated = Some(FuncCall("UNHEX", _), _) } ] } -> ()
                    | other -> failtestf "expected the generated column's AS (...) to be captured, got %A" other

                testCase "VIRTUAL/STORED is captured on the generated column, defaulting to VIRTUAL like MySQL"
                <| fun _ ->
                    match parseOk "CREATE TABLE t (n INT, s INT AS (n) STORED, v INT GENERATED ALWAYS AS (n) VIRTUAL, d INT AS (n))" with
                    | CreateTable
                        { Columns = [ { Generated = None };
                                      { Name = "s"; Generated = Some(_, Stored) };
                                      { Name = "v"; Generated = Some(_, Virtual) };
                                      { Name = "d"; Generated = Some(_, Virtual) } ] } -> ()
                    | other -> failtestf "expected STORED/VIRTUAL/default-VIRTUAL kinds captured, got %A" other ]

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
                            { Ctes = []
                              Targets = [ "t" ]
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
                        (AlterDatabase(Some "x"))
                        "named database"

                    Expect.equal
                        (parseOk "ALTER DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs")
                        (AlterDatabase None)
                        "current database" ]

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

                testCase "INSERT VALUES accepts explicit DEFAULT cells"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) VALUES (DEFAULT, 2)")
                        (Insert("t", [ "a"; "b" ], [ [ FuncCall("DEFAULT", []); Lit(VInt 2L) ] ], [], false))
                        "explicit defaults"

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

                testCase "INSERT SET desugars to one named VALUES row"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT IGNORE INTO t SET a = 1, b = NOW() ON DUPLICATE KEY UPDATE b = VALUES(b)")
                        (Insert(
                            "t",
                            [ "a"; "b" ],
                            [ [ Lit(VInt 1L); FuncCall("NOW", []) ] ],
                            [ "b", FuncCall("VALUES", [ Col "b" ]) ],
                            true
                        ))
                        "insert set"

                testCase "INSERT VALUE accepts optional ROW constructors"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) VALUE ROW(1, 2), ROW(3, 4)")
                        (Insert("t", [ "a"; "b" ], [ [ Lit(VInt 1L); Lit(VInt 2L) ]; [ Lit(VInt 3L); Lit(VInt 4L) ] ], [], false))
                        "insert value rows"

                testCase "INSERT INTO t (cols) SELECT ... is InsertSelect, not a VALUES Insert"
                <| fun _ ->
                    Expect.equal
                        (parseOk "INSERT INTO t (a, b) SELECT x, y FROM u WHERE x > 1")
                        (InsertSelect(
                            "t",
                            [ "a"; "b" ],
                            { Projections = [ col "x", None; col "y", None ]
                              IntoVariables = []
                              Distinct = false
                              CalculateFoundRows = false
                              StraightJoin = false
                              From = Some(FromTable { Database = None; Table = "u"; Alias = None })
                              Joins = []
                              Where = Some(BinOp(Gt, col "x", Lit(VInt 1L)))
                              GroupBy = []
                              Rollup = false
                              Windows = []
                              Ctes = []
                              Having = None
                              OrderBy = []
                              Limit = None
                              Offset = None
                              Locking = false },
                            [],
                            false
                        ))
                        "insert select"

                testCase "INSERT IGNORE INTO t SELECT ... sets the ignore flag"
                <| fun _ ->
                    match parseOk "INSERT IGNORE INTO t SELECT * FROM u" with
                    | InsertSelect("t", [], _, [], true) -> ()
                    | other -> failtestf "expected an ignore-flagged InsertSelect, got %A" other

                testCase "INSERT accepts a parenthesized SELECT source"
                <| fun _ ->
                    match parseOk "INSERT INTO t (SELECT x, y FROM u WHERE x > 1)" with
                    | InsertSelect("t", [], { Projections = [ Col "x", None; Col "y", None ] }, [], false) -> ()
                    | other -> failtestf "expected a parenthesized InsertSelect, got %A" other

                testCase "INSERT ... SELECT carries a trailing ON DUPLICATE KEY UPDATE"
                <| fun _ ->
                    match parseOk "INSERT INTO t (a, b) SELECT x, y FROM u AS s ON DUPLICATE KEY UPDATE b = s.y, a = a + 1" with
                    | InsertSelect(
                        "t",
                        [ "a"; "b" ],
                        _,
                        [ "b", QualifiedCol("s", "y"); "a", BinOp(Add, Col "a", Lit(VInt 1L)) ],
                        false) -> ()
                    | other -> failtestf "expected an InsertSelect with the ODKU assignments, got %A" other

                testCase "explicit keyword aliases remain usable as qualifiers"
                <| fun _ ->
                    let sql =
                        "INSERT INTO dst (value_id, parent_id) "
                        + "SELECT values.value_id, options.parent_id AS parent_id "
                        + "FROM source_values AS values "
                        + "LEFT JOIN source_options AS options ON values.option_id = options.option_id "
                        + "ON DUPLICATE KEY UPDATE value_id = VALUES(value_id), parent_id = VALUES(parent_id)"

                    match parseOk sql with
                    | InsertSelect(
                        "dst",
                        [ "value_id"; "parent_id" ],
                        { Projections = [ QualifiedCol("values", "value_id"), None; QualifiedCol("options", "parent_id"), Some "parent_id" ]
                          From = Some(FromTable { Table = "source_values"; Alias = Some "values" })
                          Joins = [ { Table = FromTable { Table = "source_options"; Alias = Some "options" } } ] },
                        [ "value_id", FuncCall("VALUES", [ Col "value_id" ]); "parent_id", FuncCall("VALUES", [ Col "parent_id" ]) ],
                        false) -> ()
                    | other -> failtestf "expected keyword aliases in an InsertSelect, got %A" other

                testCase "INSERT accepts a WITH clause on its SELECT source"
                <| fun _ ->
                    match parseOk "INSERT INTO t (a) WITH c AS (SELECT 1 AS n) SELECT n FROM c" with
                    | InsertSelect("t", [ "a" ], { Ctes = [ { CteName = "c" } ] }, [], false) -> ()
                    | other -> failtestf "expected an InsertSelect with a CTE source, got %A" other

                testCase "a WITH clause cannot precede INSERT"
                <| fun _ ->
                    Expect.isError
                        (parse "WITH c AS (SELECT 1 AS n) INSERT INTO t (a) SELECT n FROM c")
                        "MySQL requires WITH after the INSERT target"

                testCase "REPLACE accepts optional INTO, VALUE, and ROW constructors"
                <| fun _ ->
                    Expect.equal
                        (parseOk "REPLACE t (a, b) VALUE ROW(1, 2), ROW(3, 4)")
                        (Replace(
                            "t",
                            [ "a"; "b" ],
                            [ [ Lit(VInt 1L); Lit(VInt 2L) ]; [ Lit(VInt 3L); Lit(VInt 4L) ] ]
                        ))
                        "replace values"

                testCase "REPLACE ... SELECT has a distinct statement shape"
                <| fun _ ->
                    match parseOk "REPLACE INTO dst (a, b) SELECT x, y FROM src" with
                    | ReplaceSelect("dst", [ "a"; "b" ], _) -> ()
                    | other -> failtestf "expected ReplaceSelect, got %A" other

                testCase "REPLACE accepts a WITH clause on its SELECT source"
                <| fun _ ->
                    match parseOk "REPLACE INTO dst (a) WITH c AS (SELECT 1 AS n) SELECT n FROM c" with
                    | ReplaceSelect("dst", [ "a" ], { Ctes = [ { CteName = "c" } ] }) -> ()
                    | other -> failtestf "expected ReplaceSelect with a CTE source, got %A" other

                testCase "REPLACE ... SET retains assignment expressions"
                <| fun _ ->
                    Expect.equal
                        (parseOk "REPLACE INTO t SET id = 1, n = n + 1, u = DEFAULT(u)")
                        (ReplaceSet(
                            "t",
                            [ "id", Lit(VInt 1L)
                              "n", BinOp(Add, Col "n", Lit(VInt 1L))
                              "u", FuncCall("DEFAULT", [ Col "u" ]) ]
                        ))
                        "replace set" ]

          testList
              "ROW_NUMBER() OVER (...)"
              [ testCase "PARTITION BY and ORDER BY both present"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER (PARTITION BY a ORDER BY b DESC) AS rn FROM t" with
                    | Select { Projections = [ WindowOver(WinRowNumber, OverSpec { PartitionBy = [ Col "a" ]; OrderBy = [ Col "b", Desc ]; Frame = None }), Some "rn" ] } -> ()
                    | other -> failtestf "expected a RowNumberOver projection, got %A" other

                testCase "PARTITION BY with multiple columns, ORDER BY defaulting to ASC"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER (PARTITION BY a, b ORDER BY c) FROM t" with
                    | Select { Projections = [ WindowOver(WinRowNumber, OverSpec { PartitionBy = [ Col "a"; Col "b" ]; OrderBy = [ Col "c", Asc ]; Frame = None }), None ] } -> ()
                    | other -> failtestf "expected a two-column partition key, got %A" other

                testCase "OVER () with neither PARTITION BY nor ORDER BY"
                <| fun _ ->
                    match parseOk "SELECT ROW_NUMBER() OVER () FROM t" with
                    | Select { Projections = [ WindowOver(WinRowNumber, OverSpec { PartitionBy = []; OrderBy = []; Frame = None }), None ] } -> ()
                    | other -> failtestf "expected an empty partition/order spec, got %A" other ]

          testList
              "LAG(expr[, offset]) OVER (...)"
              [ testCase "no explicit offset defaults to 1"
                <| fun _ ->
                    match parseOk "SELECT LAG(value) OVER (PARTITION BY a ORDER BY b) AS prev FROM t" with
                    | Select { Projections = [ WindowOver(WinLagLead(false, Col "value", None, None), OverSpec { PartitionBy = [ Col "a" ]; OrderBy = [ Col "b", Asc ]; Frame = None }), Some "prev" ] } -> ()
                    | other -> failtestf "expected a LagOver projection with offset 1, got %A" other

                testCase "an explicit offset is parsed through"
                <| fun _ ->
                    match parseOk "SELECT LAG(value, 2) OVER (ORDER BY b) FROM t" with
                    | Select { Projections = [ WindowOver(WinLagLead(false, Col "value", Some(Lit(VInt 2L)), None), OverSpec { PartitionBy = []; OrderBy = [ Col "b", Asc ]; Frame = None }), None ] } -> ()
                    | other -> failtestf "expected offset 2, got %A" other

                testCase "usable nested inside arithmetic"
                <| fun _ ->
                    match parseOk "SELECT value - LAG(value) OVER (ORDER BY b) AS diff FROM t" with
                    | Select { Projections = [ BinOp(Sub, Col "value", WindowOver(WinLagLead(false, Col "value", None, None), OverSpec { PartitionBy = []; OrderBy = [ Col "b", Asc ]; Frame = None })), Some "diff" ] } -> ()
                    | other -> failtestf "expected LagOver nested in a BinOp, got %A" other ]

          testList
              "LEAD(expr[, offset]) OVER (...)"
              [ testCase "no explicit offset leaves the offset argument absent"
                <| fun _ ->
                    match parseOk "SELECT LEAD(value) OVER (ORDER BY id) FROM t" with
                    | Select { Projections = [ WindowOver(WinLagLead(true, Col "value", None, None), _), None ] } -> ()
                    | other -> failtestf "expected a LEAD window projection, got %A" other

                testCase "an explicit offset is parsed through"
                <| fun _ ->
                    match parseOk "SELECT LEAD(value, 2) OVER (ORDER BY id) FROM t" with
                    | Select { Projections = [ WindowOver(WinLagLead(true, Col "value", Some(Lit(VInt 2L)), None), _), None ] } -> ()
                    | other -> failtestf "expected offset 2, got %A" other ]

          testList
              "UPDATE / DELETE"
              [ testCase "UPDATE t SET a=expr, b=expr WHERE ..."
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t SET a = 1, b = a + 1 WHERE id = 5")
                        (Update
                            { Ctes = []
                              Ignore = false
                              From = { Database = None; Table = "t"; Alias = None }
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
                            { Ctes = []
                              Ignore = false
                              From = { Database = None; Table = "t"; Alias = None }
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
                            { Ctes = []
                              Targets = [ "t" ]
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
                            { Ctes = []
                              Targets = [ "t" ]
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
                            { Ctes = []
                              Targets = [ "t" ]
                              From = { Database = None; Table = "t"; Alias = None }
                              Joins = []
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = []
                              Limit = Some(Lit(VInt 100L)) })
                        "delete with limit"

                testCase "UPDATE with an alias, ORDER BY, and LIMIT"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t AS x SET a = 1 WHERE id = 5 ORDER BY id LIMIT 10")
                        (Update
                            { Ctes = []
                              Ignore = false
                              From = { Database = None; Table = "t"; Alias = Some "x" }
                              Joins = []
                              Assignments = [ { Table = None; Column = "a"; Value = Lit(VInt 1L) } ]
                              Where = Some(BinOp(Eq, col "id", Lit(VInt 5L)))
                              OrderBy = [ col "id", Asc ]
                              Limit = Some(Lit(VInt 10L)) })
                        "alias parsed, order/limit real"

                testCase "UPDATE with a bare alias (no AS)"
                <| fun _ ->
                    Expect.equal
                        (parseOk "UPDATE t x SET a = 1")
                        (Update
                            { Ctes = []
                              Ignore = false
                              From = { Database = None; Table = "t"; Alias = Some "x" }
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
                            { Ctes = []
                              Ignore = false
                              From = { Database = None; Table = "chatbots"; Alias = None }
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

          testCase "DO parses a comma-separated expression list"
          <| fun _ ->
              Expect.equal
                  (parseOk "DO 1, ABS(2)")
                  (Do [ Lit(VInt 1L); FuncCall("ABS", [ Lit(VInt 2L) ]) ])
                  "do expressions"

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

                testCase "RENAME INDEX a TO b"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t RENAME INDEX old_ix TO new_ix")
                        (AlterTable("t", [ RenameIndex("old_ix", "new_ix") ]))
                        "rename index"

                testCase "ADD [UNIQUE] INDEX|KEY name (cols) and DROP INDEX|KEY name"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t ADD UNIQUE INDEX uq (a), ADD KEY idx (b), DROP INDEX uq, DROP KEY idx")
                        (AlterTable(
                            "t",
                            [ AddIndex { Name = "uq"; KeyColumns = indexColumns [ "a" ]; Unique = true; Kind = BTree }
                              AddIndex { Name = "idx"; KeyColumns = indexColumns [ "b" ]; Unique = false; Kind = BTree }
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

                testCase "ADD FOREIGN KEY accepts a key name without CONSTRAINT"
                <| fun _ ->
                    match
                        parseOk
                            "ALTER TABLE theme ADD FOREIGN KEY `fk.theme.preview_media_id` (preview_media_id) REFERENCES media(id) ON UPDATE CASCADE ON DELETE SET NULL"
                    with
                    | AlterTable(
                        "theme",
                        [ AddForeignKey
                              { Name = "fk.theme.preview_media_id"
                                Columns = [ "preview_media_id" ]
                                RefTable = "media"
                                RefColumns = [ "id" ]
                                OnDelete = Some "SET NULL"
                                OnUpdate = Some "CASCADE" } ]
                      ) -> ()
                    | other -> failtestf "expected the directly named foreign key, got %A" other

                testCase "ADD and DROP PRIMARY KEY"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t DROP PRIMARY KEY, ADD PRIMARY KEY (id, tenant_id)")
                        (AlterTable("t", [ DropPrimaryKey; AddPrimaryKey [ "id"; "tenant_id" ] ]))
                        "replace primary key"

                    Expect.equal
                        (parseOk "ALTER TABLE migrations_lock ADD PRIMARY KEY migrations_lock_pkey(lock_key)")
                        (AlterTable("migrations_lock", [ AddPrimaryKey [ "lock_key" ] ]))
                        "optional primary-key name"

                testCase "ALTER TABLE named unique constraints and execution options"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE document_type ADD CONSTRAINT `uniq.document_type.name` UNIQUE (technical_name), ALGORITHM=INSTANT, LOCK=NONE")
                        (AlterTable(
                            "document_type",
                            [ AddIndex
                                  { Name = "uniq.document_type.name"
                                    KeyColumns = indexColumns [ "technical_name" ]
                                    Unique = true
                                    Kind = BTree } ]
                        ))
                        "alter options"

                    Expect.equal
                        (parseOk "ALTER TABLE files ROW_FORMAT = DYNAMIC")
                        (AlterTable("files", []))
                        "row format is a storage hint"

                testCase "DROP TABLE accepts referential action suffixes"
                <| fun _ ->
                    Expect.equal (parseOk "DROP TABLE unittest_actor CASCADE") (DropTable([ "unittest_actor" ], false)) "drop cascade"

                testCase "ALTER COLUMN SET and DROP DEFAULT"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t ALTER COLUMN n SET DEFAULT 7, ALTER n DROP DEFAULT")
                        (AlterTable("t", [ SetDefault("n", Some(DConst(VInt 7L))); SetDefault("n", None) ]))
                        "alter defaults"

                testCase "ALTER TABLE ENGINE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t ENGINE=InnoDB")
                        (AlterTable("t", [ SetEngine "InnoDB" ]))
                        "set engine"

                testCase "ALTER TABLE COMMENT"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t COMMENT = 'application data'")
                        (AlterTable("t", [ SetTableComment "application data" ]))
                        "table comment"

                testCase "CHECKSUM TABLE"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CHECKSUM TABLE t, archive.t EXTENDED")
                        (ChecksumTables([ "t"; "archive.t" ], false))
                        "extended checksum"

                    Expect.equal
                        (parseOk "CHECKSUM TABLE t QUICK")
                        (ChecksumTables([ "t" ], true))
                        "quick checksum"

                testCase "ALTER TABLE CONVERT TO CHARACTER SET"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER TABLE t CONVERT TO CHARACTER SET latin1 COLLATE latin1_swedish_ci")
                        (AlterTable("t", [ ConvertCharset("latin1", Some "latin1_swedish_ci") ]))
                        "convert charset"

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
                        (CreateIndex("idx_a", "t", indexColumns [ "a" ], false, BTree))
                        "create index"

                    Expect.equal
                        (parseOk "CREATE UNIQUE INDEX uq_a ON t (a)")
                        (CreateIndex("uq_a", "t", indexColumns [ "a" ], true, BTree))
                        "create unique index"

                    match parseOk "CREATE INDEX ix_prefix ON t (a(12))" with
                    | CreateIndex("ix_prefix", "t", [ { Name = "a"; PrefixLength = Some 12; Transform = None } ], false, BTree) -> ()
                    | other -> failtestf "expected CREATE INDEX prefix metadata, got %A" other

                    match parseOk "CREATE UNIQUE INDEX ix_lower ON t ((LOWER(external_id)))" with
                    | CreateIndex("ix_lower", "t", [ { Name = "external_id"; PrefixLength = None; Transform = Some Lowercase } ], true, BTree) -> ()
                    | other -> failtestf "expected a lowercase functional key part, got %A" other

                    match
                        parseOk
                            "CREATE TABLE companies (name VARCHAR(255), rating BIGINT, firm_name VARCHAR(255), firm_id BIGINT, client_of BIGINT, INDEX company_name_index USING btree (name), INDEX company_expression_index ((CASE WHEN rating > 0 THEN lower(name) END) DESC), INDEX full_name_index ((CONCAT_WS(firm_name, name, _utf8mb4' '))), INDEX company_disabled_index (firm_id, client_of) INVISIBLE)"
                    with
                    | CreateTable { Indexes = [ _; { KeyColumns = [ { Transform = Some(Expression(Case _)) } ] }; { KeyColumns = [ { Transform = Some(Expression(FuncCall("CONCAT_WS", _))) } ] }; _ ] } -> ()
                    | other -> failtestf "expected Rails expression/index-option metadata, got %A" other

                testCase "DROP INDEX [IF EXISTS] name ON table"
                <| fun _ ->
                    Expect.equal (parseOk "DROP INDEX idx_a ON t") (DropIndexStmt("idx_a", "t", false)) "drop index"
                    Expect.equal (parseOk "DROP INDEX IF EXISTS idx_a ON t") (DropIndexStmt("idx_a", "t", true)) "drop index if exists"

                testCase "CAST(expr AS type), including SIGNED/UNSIGNED"
                <| fun _ ->
                    Expect.equal
                        (parseOk "SELECT CAST(a AS UNSIGNED), CAST('1' AS SIGNED), CAST(a AS CHAR)")
                        (mkSelect(
                            [ Cast(col "a", TBigInt true), None
                              Cast(Lit(VString "1"), TBigInt false), None
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

                testCase "WITH clauses parse inside scalar and EXISTS subqueries"
                <| fun _ ->
                    match parseOk "SELECT (WITH c AS (SELECT 1 AS n) SELECT n FROM c), EXISTS (WITH d AS (SELECT 2 AS n) SELECT n FROM d)" with
                    | Select { Projections = [ (Subquery scalar, None); (Exists exists, None) ] } ->
                        Expect.equal (scalar.Ctes |> List.map _.CteName) [ "c" ] "scalar CTE"
                        Expect.equal (exists.Ctes |> List.map _.CteName) [ "d" ] "EXISTS CTE"
                    | other -> failtestf "expected CTE-bearing expression subqueries, got %A" other

                testCase "set operations parse in every expression subquery shape"
                <| fun _ ->
                    match
                        parseOk
                            "SELECT (SELECT 1 UNION SELECT 1), EXISTS (SELECT 1 UNION ALL SELECT 2), 2 IN (SELECT 1 UNION ALL SELECT 2), 2 = ANY (SELECT 1 UNION ALL SELECT 2)"
                    with
                    | Select select ->
                        let isSetExpression (select: SelectStmt) =
                            match select.From with
                            | Some(FromLateral(UnionSelect _, _)) -> true
                            | _ -> false

                        match select.Projections with
                        | [ Subquery scalar, None
                            Exists exists, None
                            InSubquery(_, inSelect), None
                            QuantifiedComparison(_, _, _, quantified), None ] ->
                            Expect.isTrue
                                ([ scalar; exists; inSelect; quantified ] |> List.forall isSetExpression)
                                "all four subqueries wrap a set expression"
                        | projections -> failtestf "expected four expression subqueries, got %A" projections
                    | other -> failtestf "expected derived set-expression subqueries, got %A" other

                testCase "WITH clauses attach to UPDATE and DELETE"
                <| fun _ ->
                    match
                        parseOk "WITH changed AS (SELECT id FROM src) UPDATE dst SET n = n + 1 WHERE id IN (SELECT id FROM changed)",
                        parseOk "WITH removed AS (SELECT id FROM src) DELETE FROM dst WHERE id IN (SELECT id FROM removed)"
                    with
                    | Update { Ctes = [ { CteName = "changed" } ] }, Delete { Ctes = [ { CteName = "removed" } ] } -> ()
                    | other -> failtestf "expected CTE-bearing mutations, got %A" other

                testCase "a parenthesized set branch may begin with WITH"
                <| fun _ ->
                    match parseOk "(WITH c AS (SELECT 1 AS n) SELECT n FROM c) UNION ALL (SELECT 2)" with
                    | Union({ Ctes = [ { CteName = "c" } ] }, [ OpUnion true, _ ], _, _, _) -> ()
                    | other -> failtestf "expected a branch-local CTE, got %A" other

                testCase "WITH and RECURSIVE are reserved outside CTE grammar"
                <| fun _ ->
                    Expect.isError (parse "SELECT d.n FROM WITH c") "WITH cannot become a table name"
                    Expect.isError (parse "SELECT recursive FROM t") "RECURSIVE cannot become a column name"

                    match parseOk "SELECT t.with, t.recursive FROM t" with
                    | Select { Projections = [ (QualifiedCol("t", "with"), None); (QualifiedCol("t", "recursive"), None) ] } -> ()
                    | other -> failtestf "expected reserved words after a qualifier, got %A" other

                    match parseOk "SELECT * FROM db.with" with
                    | Select { From = Some(FromTable { Database = Some "db"; Table = "with" }) } -> ()
                    | other -> failtestf "expected a reserved qualified table name, got %A" other

                testCase "window function names require quoting when used as aliases"
                <| fun _ ->
                    for name in
                        [ "ROW_NUMBER"; "RANK"; "DENSE_RANK"; "PERCENT_RANK"; "CUME_DIST"; "NTILE"
                          "LAG"; "LEAD"; "FIRST_VALUE"; "LAST_VALUE"; "NTH_VALUE" ] do
                        Expect.isError (parse (sprintf "SELECT 1 AS %s" name)) name

                    match parseOk "SELECT ROW_NUMBER() OVER () AS `rank`" with
                    | Select { Projections = [ WindowOver(WinRowNumber, _), Some "rank" ] } -> ()
                    | other -> failtestf "expected a quoted window alias, got %A" other

                    for name in [ "OFFSET"; "TRUNCATE"; "CAST"; "ANY"; "SOME"; "END" ] do
                        match parseOk (sprintf "SELECT 1 AS %s" name) with
                        | Select { Projections = [ Lit(VInt 1L), Some alias ] } -> Expect.equal alias name name
                        | other -> failtestf "expected explicit alias %s, got %A" name other

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

                testCase "ANY, SOME, and ALL comparisons parse with an explicit quantifier"
                <| fun _ ->
                    let parses sql expectedOp expectedQuantifier =
                        match parseOk sql with
                        | Select { Where = Some(QuantifiedComparison(Col "a", op, quantifier, { From = Some(FromTable { Table = "u" }) })) } ->
                            Expect.equal op expectedOp sql
                            Expect.equal quantifier expectedQuantifier sql
                        | other -> failtestf "expected quantified comparison, got %A" other

                    parses "SELECT a FROM t WHERE a = ANY (SELECT b FROM u)" Eq Any
                    parses "SELECT a FROM t WHERE a = SOME (SELECT b FROM u)" Eq Any
                    parses "SELECT a FROM t WHERE a <= ALL (SELECT b FROM u)" Lte All

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

                testCase "DISTINCT inside a call MySQL's grammar doesn't allow it in is a 1064"
                <| fun _ ->
                    // Answering `[1, 1]` where the oracle refuses is worse
                    // than refusing: JSON_ARRAYAGG/JSON_OBJECTAGG have no
                    // DISTINCT form at all, and neither does a scalar.
                    for sql in
                        [ "SELECT JSON_ARRAYAGG(DISTINCT x) FROM t"
                          "SELECT JSON_OBJECTAGG(DISTINCT k, v) FROM t"
                          "SELECT BIT_OR(DISTINCT x) FROM t"
                          "SELECT CONCAT(DISTINCT x) FROM t" ] do
                        match parse sql with
                        | Error _ -> ()
                        | Ok stmt -> failtestf "expected %s to be a parse error, got %A" sql stmt

                    for sql in
                        [ "SELECT SUM(DISTINCT x) FROM t"
                          "SELECT AVG(DISTINCT x) FROM t"
                          "SELECT MIN(DISTINCT x) FROM t"
                          "SELECT MAX(DISTINCT x) FROM t" ] do
                        parseOk sql |> ignore

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

                testCase "INNER JOIN may omit its condition"
                <| fun _ ->
                    match parseOk "SELECT * FROM a INNER JOIN b" with
                    | Select { Joins = [ { Kind = InnerJoin; On = Lit(VInt 1L) } ] } -> ()
                    | other -> failtestf "expected an unconditional InnerJoin, got %A" other

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
                    | Union(_, [ (OpUnion true, _); (OpUnion false, _) ], _, _, _) -> ()
                    | other -> failtestf "expected a two-branch Union with ALL then DISTINCT flags, got %A" other

                testCase "a single SELECT (no UNION) still parses to the plain Select case"
                <| fun _ ->
                    match parseOk "SELECT a FROM t" with
                    | Select _ -> ()
                    | other -> failtestf "expected a plain Select, got %A" other

                testCase "trailing ORDER BY/LIMIT after a UNION apply to the combined result"
                <| fun _ ->
                    match parseOk "SELECT a FROM t UNION SELECT a FROM u ORDER BY a LIMIT 5" with
                    | Union(_, _, [ (Col "a", Asc) ], Some(Lit(VInt 5L)), None) -> ()
                    | other -> failtestf "expected the trailing ORDER BY/LIMIT to land on the Union, got %A" other

                testCase "(SELECT ...) UNION (SELECT ...) — each branch individually parenthesized"
                <| fun _ ->
                    match parseOk "(SELECT a FROM t) UNION (SELECT a FROM u)" with
                    | Union(_, [ (OpUnion false, _) ], _, _, _) -> ()
                    | other -> failtestf "expected a two-branch Union, got %A" other

                testCase "FROM ((SELECT ...) UNION (SELECT ...)) AS alias — a UNION as a derived table"
                <| fun _ ->
                    // Laravel's `unionAll(...)->paginate()` compiles to exactly
                    // this shape: `SELECT COUNT(*) FROM ((SELECT ...) UNION
                    // (SELECT ...)) AS alias`.
                    match parseOk "SELECT COUNT(*) AS aggregate FROM ((SELECT a FROM t) UNION (SELECT a FROM u)) AS search_items" with
                    | Select { From = Some(FromSubquery(UnionSelect(_, [ (OpUnion false, _) ], _, _, _), "search_items")) } -> ()
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

                testCase "FOR UPDATE and FOR SHARE accept locking details"
                <| fun _ ->
                    [ "SELECT * FROM t FOR UPDATE NOWAIT"
                      "SELECT * FROM t FOR UPDATE SKIP LOCKED"
                      "SELECT * FROM t FOR SHARE OF t NOWAIT" ]
                    |> List.iter (fun sql ->
                        match parseOk sql with
                        | Select { Locking = true } -> ()
                        | other -> failtestf "expected a locking SELECT, got %A" other)

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

                testCase "user and system variables parse as ordinary expressions"
                <| fun _ ->
                    let variable name = { Name = name; Sql = "@" + name }

                    let expected =
                        [ BinOp(Add, UserVariable(variable "x"), Lit(VInt 1L)), None
                          SystemVariable(Some "GLOBAL", "max_connections"), None
                          AssignUserVariable(variable "x", Lit(VInt 3L)), None ]

                    match parse "SELECT @x + 1, @@GLOBAL.max_connections, @x := 3" with
                    | Ok(Select { Projections = projections }) -> Expect.equal projections expected "variable expressions"
                    | other -> failtestf "expected variable expressions, got %A" other

                testCase "SELECT INTO keeps its user-variable targets"
                <| fun _ ->
                    match parse "SELECT id, name INTO @chosen_id, @`chosen name` FROM users" with
                    | Ok(Select { IntoVariables = targets }) ->
                        Expect.equal
                            targets
                            [ { Name = "chosen_id"; Sql = "@chosen_id" }
                              { Name = "chosen name"; Sql = "@`chosen name`" } ]
                            "assignment targets"
                    | other -> failtestf "expected SELECT INTO targets, got %A" other

                testCase "a bare at sign is MySQL's anonymous NULL variable reference"
                <| fun _ ->
                    match parse "SELECT @, @ + 1" with
                    | Ok(Select { Projections = [ (UserVariable first, None); (BinOp(Add, UserVariable second, Lit(VInt 1L)), None) ] }) ->
                        Expect.equal first { Name = ""; Sql = "@" } "bare reference"
                        Expect.equal second first "nested bare reference"
                    | other -> failtestf "expected bare user-variable references, got %A" other

                testCase "quoted user-variable names parse with their MySQL escapes"
                <| fun _ ->
                    let expected =
                        [ UserVariable { Name = "has space"; Sql = "@`has space`" }, None
                          UserVariable { Name = "single'quote"; Sql = "@'single''quote'" }, None
                          UserVariable { Name = "double\"quote"; Sql = "@\"double\"\"quote\"" }, None
                          UserVariable { Name = "back`tick"; Sql = "@`back``tick`" }, None ]

                    match parse "SELECT @`has space`, @'single''quote', @\"double\"\"quote\", @`back``tick`" with
                    | Ok(Select { Projections = projections }) -> Expect.equal projections expected "quoted variables"
                    | other -> failtestf "expected quoted variables, got %A" other

                testCase "1000 levels of nested parens is a syntax error, not a stack overflow"
                <| fun _ ->
                    let deep = String.replicate 1000 "(" + "1" + String.replicate 1000 ")"

                    match parse (sprintf "SELECT %s" deep) with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected a depth-limit error, got %A" stmt

                testCase "1000 levels of NOT NOT NOT ... is a syntax error, not a stack overflow"
                <| fun _ ->
                    let deep = String.replicate 1000 "NOT " + "TRUE"

                    match parse (sprintf "SELECT %s" deep) with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected a depth-limit error, got %A" stmt

                testCase "compact scalar-subquery nesting is rejected before parser amplification"
                <| fun _ ->
                    let deep = String.replicate 50 "(SELECT " + "1" + String.replicate 50 ")"

                    match parse ("SELECT " + deep) with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected a depth-limit error, got %A" stmt ]

          testList
              "view statements"
              [ testCase "CREATE VIEW parses its declaration envelope"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE OR REPLACE ALGORITHM=TEMPTABLE DEFINER='owner'@'localhost' SQL SECURITY INVOKER VIEW db.v (item) AS SELECT id FROM t")
                        (CreateView
                            { Action = CreateViewDdl true
                              Algorithm = Some ViewAlgorithmTemptable
                              Definer = Some(ExplicitViewDefiner("owner", "localhost"))
                              Security = Some ViewInvoker
                              Name = "db.v"
                              Columns = [ "item" ]
                              Definition = "SELECT id FROM t" })
                        "create envelope"

                testCase "ALTER VIEW distinguishes omitted and explicit envelope options"
                <| fun _ ->
                    Expect.equal
                        (parseOk "ALTER ALGORITHM=MERGE DEFINER=CURRENT_USER() VIEW v AS SELECT id FROM t")
                        (CreateView
                            { Action = AlterViewDdl
                              Algorithm = Some ViewAlgorithmMerge
                              Definer = Some CurrentViewDefiner
                              Security = None
                              Name = "v"
                              Columns = []
                              Definition = "SELECT id FROM t" })
                        "alter envelope"

                testCase "view definition parsing separates CHECK OPTION"
                <| fun _ ->
                    match parseViewDefinition "SELECT id FROM t WITH LOCAL CHECK OPTION" with
                    | Ok definition ->
                        Expect.equal definition.Sql "SELECT id FROM t" "stored SQL"
                        Expect.equal definition.CheckOption "LOCAL" "check option"

                        match definition.Statement with
                        | Select _ -> ()
                        | other -> failtestf "expected SELECT definition, got %A" other
                    | Error error -> failtestf "expected parsed view definition, got %s" error ]

          testList
              "user statements"
              [ testCase "CREATE USER parses quoted user@host, IDENTIFIED BY, IF NOT EXISTS, and multiple accounts"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE USER 'bob'@'%' IDENTIFIED BY 's3cret'")
                        (CreateUser([ "bob", "%", Some "s3cret" ], false, false, RequireNone))
                        "quoted with password"

                    Expect.equal
                        (parseOk "CREATE USER IF NOT EXISTS bob")
                        (CreateUser([ "bob", "%", None ], true, false, RequireNone))
                        "bare name defaults host to %"

                    Expect.equal
                        (parseOk "CREATE USER 'a'@'localhost', 'b'@'%' IDENTIFIED BY 'pw'")
                        (CreateUser([ "a", "localhost", None; "b", "%", Some "pw" ], false, false, RequireNone))
                        "per-account password in a list"

                    Expect.equal
                        (parseOk "CREATE USER locked ACCOUNT LOCK")
                        (CreateUser([ "locked", "%", None ], false, true, RequireNone))
                        "account state"

                    Expect.equal
                        (parseOk "CREATE USER secure REQUIRE SSL")
                        (CreateUser([ "secure", "%", None ], false, false, RequireSsl))
                        "SSL requirement"

                    Expect.equal
                        (parseOk "CREATE USER certified REQUIRE X509 ACCOUNT LOCK")
                        (CreateUser([ "certified", "%", None ], false, true, RequireX509))
                        "X509 requirement"

                testCase "CREATE ROLE and DROP ROLE parse account lists"
                <| fun _ ->
                    Expect.equal
                        (parseOk "CREATE ROLE IF NOT EXISTS 'reader'@'localhost', writer")
                        (CreateRole([ "reader", "localhost"; "writer", "%" ], true))
                        "role accounts"

                    Expect.equal
                        (parseOk "DROP ROLE IF EXISTS reader, writer@localhost")
                        (DropRole([ "reader", "%"; "writer", "localhost" ], true))
                        "drop role accounts"

                testCase "GRANT parses privilege lists, all four ON levels, and WITH GRANT OPTION"
                <| fun _ ->
                    Expect.equal
                        (parseOk "GRANT SELECT, INSERT ON shop.* TO 'bob'@'%'")
                        (Grant([ "SELECT"; "INSERT" ], (Some "shop", None), [ "bob", "%" ], false))
                        "db level"

                    Expect.equal
                        (parseOk "GRANT ALL PRIVILEGES ON *.* TO bob WITH GRANT OPTION")
                        (Grant([ "ALL" ], (None, None), [ "bob", "%" ], true))
                        "global with grant option"

                    Expect.equal
                        (parseOk "GRANT CREATE TEMPORARY TABLES, SHOW DATABASES ON *.* TO bob")
                        (Grant([ "CREATE TEMPORARY TABLES"; "SHOW DATABASES" ], (None, None), [ "bob", "%" ], false))
                        "multi-word privilege names"

                    Expect.equal
                        (parseOk "GRANT SELECT ON shop.orders TO bob")
                        (Grant([ "SELECT" ], (Some "shop", Some "orders"), [ "bob", "%" ], false))
                        "table level"

                testCase "REVOKE parses, including GRANT OPTION in the list"
                <| fun _ ->
                    Expect.equal
                        (parseOk "REVOKE SELECT, GRANT OPTION ON shop.* FROM 'bob'@'%'")
                        (Revoke([ "SELECT"; "GRANT OPTION" ], (Some "shop", None), [ "bob", "%" ]))
                        "revoke with grant option"

                testCase "DROP USER and ALTER USER ... IDENTIFIED BY parse"
                <| fun _ ->
                    Expect.equal
                        (parseOk "DROP USER IF EXISTS 'bob'@'%', carol")
                        (DropUser([ "bob", "%"; "carol", "%" ], true))
                        "drop list"

                    Expect.equal
                        (parseOk "ALTER USER 'bob'@'%' IDENTIFIED BY 'newpw'")
                        (AlterUser("bob", "%", "newpw", false))
                        "alter password"

                    Expect.equal
                        (parseOk "RENAME USER 'bob'@'localhost' TO 'robert'@'%', carol TO dave")
                        (RenameUser([ (("bob", "localhost"), ("robert", "%")); (("carol", "%"), ("dave", "%")) ]))
                        "rename list" ]
          testCase "dump-style CREATE TABLE options parse: AUTO_INCREMENT seed, ROW_FORMAT, COMMENT, KEY USING BTREE, utf8mb3"
          <| fun _ ->
              match
                  Fsdb.Parser.parse
                      "CREATE TABLE t (id INT NOT NULL AUTO_INCREMENT, a VARCHAR(10), KEY k1 (a) USING BTREE, PRIMARY KEY (id)) ENGINE=InnoDB AUTO_INCREMENT=13 DEFAULT CHARSET=utf8mb3 COLLATE=utf8mb3_general_ci ROW_FORMAT=DYNAMIC COMMENT='imported'"
              with
              | Ok(CreateTable table) ->
                  Expect.equal table.Charset (Some "utf8mb3") "utf8mb3 accepted as a table charset"
                  Expect.equal table.AutoIncrementSeed (Some 13L) "AUTO_INCREMENT table option carried"
                  Expect.equal table.Comment (Some "imported") "COMMENT table option carried"
              | Ok other -> failtestf "expected CreateTable, got %A" other
              | Error e -> failtestf "expected the dump-style CREATE to parse, got %s" e
          testCase "MariaDB dump forms: current_timestamp(), column CHARACTER SET utf8mb3, ALTER AUTO_INCREMENT"
          <| fun _ ->
              match Fsdb.Parser.parse "CREATE TABLE t (c timestamp NOT NULL DEFAULT current_timestamp(), n varchar(10) CHARACTER SET utf8mb3 COLLATE utf8mb3_general_ci)" with
              | Ok(CreateTable { Columns = [ { Default = Some DCurrentTimestamp }; { Charset = Some "utf8mb3" } ] }) -> ()
              | other -> failtestf "expected the MariaDB column forms to parse, got %A" other

              match Fsdb.Parser.parse "ALTER TABLE t MODIFY id INT NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=5" with
              | Ok(AlterTable(_, [ ModifyColumn _; SetAutoIncrement 5L ])) -> ()
              | other -> failtestf "expected the ALTER AUTO_INCREMENT action, got %A" other
          testCase "MATCH ... AGAINST parses in every modifier spelling; FULLTEXT DDL carries its kind"
          <| fun _ ->
              let modeOf sql =
                  match Fsdb.Parser.parse sql with
                  | Ok(Select { Where = Some(MatchAgainst(cols, Lit(VString "q"), mode)) }) -> cols, mode
                  | other -> failtestf "unexpected parse of %s: %A" sql other

              let columns names = names |> List.map (fun name -> { Qualifier = None; Name = name })

              Expect.equal (modeOf "SELECT 1 FROM t WHERE MATCH (a,b) AGAINST ('q')") (columns [ "a"; "b" ], NaturalLanguage) "default mode"
              Expect.equal (modeOf "SELECT 1 FROM t WHERE MATCH (a) AGAINST ('q' IN NATURAL LANGUAGE MODE)") (columns [ "a" ], NaturalLanguage) "explicit NL"
              Expect.equal (modeOf "SELECT 1 FROM t WHERE MATCH (a) AGAINST ('q' IN BOOLEAN MODE)") (columns [ "a" ], BooleanMode) "boolean"
              Expect.equal (modeOf "SELECT 1 FROM t WHERE MATCH (a) AGAINST ('q' WITH QUERY EXPANSION)") (columns [ "a" ], QueryExpansion) "expansion"

              Expect.equal
                  (modeOf "SELECT 1 FROM t WHERE MATCH (a) AGAINST ('q' IN NATURAL LANGUAGE MODE WITH QUERY EXPANSION)")
                  (columns [ "a" ], QueryExpansion)
                  "NL with expansion is expansion"

              Expect.equal
                  (modeOf "SELECT 1 FROM t x WHERE MATCH (x.a, x.b) AGAINST ('q')")
                  ([ { Qualifier = Some "x"; Name = "a" }; { Qualifier = Some "x"; Name = "b" } ], NaturalLanguage)
                  "qualified MATCH columns retain their source"

              match Fsdb.Parser.parse "CREATE TABLE t (a TEXT, FULLTEXT KEY ft (a), KEY plain (a))" with
              | Ok(CreateTable { Indexes = [ ft; plain ] }) ->
                  Expect.equal ft.Kind FullTextIndex "FULLTEXT KEY kind"
                  Expect.equal plain.Kind BTree "plain KEY kind"
              | other -> failtestf "unexpected parse: %A" other

              match Fsdb.Parser.parse "CREATE FULLTEXT INDEX ft ON t (a)" with
              | Ok(CreateIndex("ft", "t", [ { Name = "a" } ], false, FullTextIndex)) -> ()
              | other -> failtestf "unexpected parse: %A" other

              Expect.isError (Fsdb.Parser.parse "SELECT MATCH(body) partial") "MATCH requires AGAINST"
          testCase "TEXT(n)/BLOB(n) map to the smallest family member that fits, like MySQL"
          <| fun _ ->
              // TEXT(n) counts characters (×4 bytes under utf8mb4), BLOB(n)
              // bytes — boundaries read off a live 8.4: TEXT(63) is the last
              // tinytext, TEXT(16384) the first mediumtext.
              match Fsdb.Parser.parse "CREATE TABLE t (a TEXT(63), b TEXT(500), c TEXT(16384), d BLOB(100), e BLOB(90000))" with
              | Ok(CreateTable { Columns = cols }) ->
                  Expect.equal
                      (cols |> List.map (fun c -> c.Type))
                      [ TTinyText; TText; TMediumText; TTinyBlob; TMediumBlob ]
                      "length-directed family selection"
              | other -> failtestf "unexpected parse: %A" other
          testCase "statement batches preserve semicolons inside literals and comments"
          <| fun _ ->
              let sql = "SELECT ';'; /* ; */ SELECT `a;b` FROM t; -- ;\n SELECT 3"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "SELECT ';'"; "SELECT `a;b` FROM t"; "SELECT 3" ]
                      "only statement delimiters split the batch"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "statement batches preserve compound trigger bodies"
          <| fun _ ->
              let sql =
                  "CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.n = CASE WHEN NEW.n > 0 THEN NEW.n ELSE 0 END; INSERT INTO log VALUES (NEW.n); END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.n = CASE WHEN NEW.n > 0 THEN NEW.n ELSE 0 END; INSERT INTO log VALUES (NEW.n); END"
                        "SELECT 1" ]
                      "the outer END closes the trigger statement"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "statement batches preserve ordered compound trigger bodies"
          <| fun _ ->
              let sql =
                  "CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW FOLLOWS first BEGIN INSERT INTO log VALUES (NEW.n); SET NEW.n = NEW.n + 1; END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE TRIGGER trg BEFORE INSERT ON t FOR EACH ROW FOLLOWS first BEGIN INSERT INTO log VALUES (NEW.n); SET NEW.n = NEW.n + 1; END"
                        "SELECT 1" ]
                      "trigger order does not expose body delimiters"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "statement batches preserve conditional trigger bodies"
          <| fun _ ->
              let sql =
                  "CREATE TRIGGER trg AFTER UPDATE ON t FOR EACH ROW BEGIN IF (NOT(NEW.n <=> OLD.n)) THEN INSERT INTO log VALUES (NEW.n); END IF; END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE TRIGGER trg AFTER UPDATE ON t FOR EACH ROW BEGIN IF (NOT(NEW.n <=> OLD.n)) THEN INSERT INTO log VALUES (NEW.n); END IF; END"
                        "SELECT 1" ]
                      "END IF does not close the outer trigger block"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "scalar IF calls do not add compound nesting"
          <| fun _ ->
              let sql = "CREATE PROCEDURE choose_value() BEGIN SELECT IF(1, 2, 3); END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE PROCEDURE choose_value() BEGIN SELECT IF(1, 2, 3); END"; "SELECT 1" ]
                      "the function call leaves BEGIN as the only compound level"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "statement batches preserve compound procedure bodies"
          <| fun _ ->
              let sql = "CREATE PROCEDURE first_post() BEGIN SELECT id FROM posts LIMIT 1; END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE PROCEDURE first_post() BEGIN SELECT id FROM posts LIMIT 1; END"; "SELECT 1" ]
                      "the procedure body remains one statement"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "statement batches preserve parameterized procedure bodies"
          <| fun _ ->
              let sql =
                  "CREATE PROCEDURE topics(IN num INT) SQL SECURITY INVOKER BEGIN SELECT * FROM topics LIMIT num; END; SELECT 1"

              match splitStatements sql with
              | Ok statements ->
                  Expect.sequenceEqual
                      statements
                      [ "CREATE PROCEDURE topics(IN num INT) SQL SECURITY INVOKER BEGIN SELECT * FROM topics LIMIT num; END"
                        "SELECT 1" ]
                      "routine characteristics do not expose body delimiters"
              | Error error -> failtestf "unexpected split error: %s" error

          testCase "top-level SQL scanning preserves nested and commented tokens"
          <| fun _ ->
              let text = "a INT, label VARCHAR(10), calculated DECIMAL(8, 2) /* retained, comment */, note TEXT"

              Expect.sequenceEqual
                  (splitTopLevelCommaSeparatedWithOptions defaultOptions text)
                  [ "a INT"
                    "label VARCHAR(10)"
                    "calculated DECIMAL(8, 2) /* retained, comment */"
                    "note TEXT" ]
                  "only top-level commas split the list"

              Expect.equal
                  (trySplitTopLevelKeywordWithOptions
                      defaultOptions
                      "DO"
                      "EVERY 1 DAY COMMENT 'DO is text' /* DO is a comment */ DO INSERT INTO log VALUES (CONCAT('DO', 1))")
                  (Some(
                      "EVERY 1 DAY COMMENT 'DO is text' /* DO is a comment */",
                      "INSERT INTO log VALUES (CONCAT('DO', 1))"
                  ))
                  "only a top-level keyword splits the text"

          testCase "event schedules retain expressions and calculate due occurrences"
          <| fun _ ->
              match
                  Fsdb.Sql.Event.tryParseSchedule
                      defaultOptions
                      "EVERY (1 + 1) SECOND STARTS CURRENT_TIMESTAMP + INTERVAL 1 SECOND ENDS CURRENT_TIMESTAMP + INTERVAL 9 SECOND"
              with
              | Some(Fsdb.Sql.Event.ScheduleSpec.Every(value, field, Some starts, Some ends)) ->
                  Expect.equal value "(1 + 1)" "interval expression"
                  Expect.equal field "SECOND" "interval field"
                  Expect.stringContains starts "INTERVAL 1 SECOND" "start expression"
                  Expect.stringContains ends "INTERVAL 9 SECOND" "end expression"
              | other -> failtestf "unexpected recurring schedule: %A" other

              let starts = System.DateTime(2026, 8, 28, 12, 0, 0)
              let ends = starts.AddSeconds 9.0

              match Fsdb.Sql.Event.tryRecurringTiming "2" "SECOND" starts (Some ends) with
              | None -> failtest "expected a recurring schedule"
              | Some timing ->
                  Expect.equal
                      (Fsdb.Sql.Event.dueOccurrence (starts.AddSeconds 8.9) None timing)
                      (Some(starts.AddSeconds 8.0))
                      "missed intervals collapse to the latest due occurrence"

                  Expect.equal
                      (Fsdb.Sql.Event.dueOccurrence (starts.AddSeconds 8.9) (Some(starts.AddSeconds 8.0)) timing)
                      None
                      "an occurrence is claimed once"

                  Expect.isTrue
                      (Fsdb.Sql.Event.isFinalOccurrence (starts.AddSeconds 8.0) timing)
                      "the occurrence before ENDS is final"

                  Expect.equal
                      (Fsdb.Sql.Event.dueOccurrence (ends.AddSeconds 2.0) None timing)
                      None
                      "an event whose schedule elapsed while disabled does not catch up"

              let monthEnd = System.DateTime(2024, 1, 31, 12, 0, 0)

              match Fsdb.Sql.Event.tryRecurringTiming "1" "MONTH" monthEnd (Some(System.DateTime(2024, 3, 30, 12, 0, 0))) with
              | Some timing ->
                  Expect.isTrue
                      (Fsdb.Sql.Event.isFinalOccurrence (System.DateTime(2024, 2, 29, 12, 0, 0)) timing)
                      "calendar finality keeps the original monthly anchor"
              | None -> failtest "expected a monthly schedule"

          testCase "stored programs parse typed parameters and local declarations"
          <| fun _ ->
              match Fsdb.StoredProgram.parseParameters defaultOptions "IN n INT, OUT label VARCHAR(10), INOUT amount DECIMAL(8, 2)" with
              | Ok
                  [ { Name = "n"; ColumnType = TInt false; Mode = Fsdb.StoredProgram.In }
                    { Name = "label"; ColumnType = TVarchar 10; Mode = Fsdb.StoredProgram.Out }
                    { Name = "amount"; ColumnType = TDecimal(8, 2, false); Mode = Fsdb.StoredProgram.InOut } ] ->
                  ()
              | other -> failtestf "unexpected parameters: %A" other

              match Fsdb.StoredProgram.parse defaultOptions "BEGIN DECLARE amount DECIMAL(8, 2) DEFAULT 1.25; SET amount = amount + 1; END" with
              | Ok
                  [ Fsdb.StoredProgram.Declare
                        { Name = "amount"
                          ColumnType = TDecimal(8, 2, false)
                          InitialValue = Some(Lit(VDecimal initial)) }
                    Fsdb.StoredProgram.SetLocal("amount", BinOp(Add, Col "amount", Lit(VInt 1L))) ] ->
                  Expect.equal initial 1.25M "declaration default"
              | other -> failtestf "unexpected stored program: %A" other

              match Fsdb.StoredProgram.parse defaultOptions "RETURN amount * 2" with
              | Ok [ Fsdb.StoredProgram.Return(BinOp(Mul, Col "amount", Lit(VInt 2L))) ] -> ()
              | other -> failtestf "unexpected stored function return: %A" other

          testCase "stored condition syntax accepts comments as token separators"
          <| fun _ ->
              match
                  Fsdb.StoredProgram.parse
                      defaultOptions
                      "BEGIN DECLARE/* declaration */ named CONDITION/* for */FOR SQLSTATE/* state */'45000'; SIGNAL named; END"
              with
              | Ok
                  [ Fsdb.StoredProgram.DeclareCondition("named", Fsdb.StoredProgram.SqlState "45000")
                    Fsdb.StoredProgram.Signal(Fsdb.StoredProgram.NamedCondition "named", []) ] ->
                  ()
              | other -> failtestf "unexpected commented declaration: %A" other

              match
                  Fsdb.StoredProgram.parse
                      defaultOptions
                      "BEGIN DECLARE named CONDITION FOR SQLSTATE '45000'; SIGNAL named; END"
              with
              | Ok
                  [ Fsdb.StoredProgram.DeclareCondition("named", Fsdb.StoredProgram.SqlState "45000")
                    Fsdb.StoredProgram.Signal(Fsdb.StoredProgram.NamedCondition "named", []) ] ->
                  ()
              | other -> failtestf "unexpected declaration and signal: %A" other

              let body =
                  """BEGIN
                       DECLARE/* declaration */ named CONDITION/* for */FOR SQLSTATE/* state */'45000';
                       DECLARE/* action */CONTINUE HANDLER/* target */FOR named,/* class */SQLWARNING
                       BEGIN
                         SIGNAL/* target */named SET MESSAGE_TEXT = 'handled';
                       END;
                     END"""

              match Fsdb.StoredProgram.parse defaultOptions body with
              | Ok
                  [ Fsdb.StoredProgram.DeclareCondition("named", Fsdb.StoredProgram.SqlState "45000")
                    Fsdb.StoredProgram.DeclareHandler(
                        Fsdb.StoredProgram.Continue,
                        [ Fsdb.StoredProgram.NamedCondition "named"; Fsdb.StoredProgram.SqlWarning ],
                        Fsdb.StoredProgram.Block(
                            None,
                            [ Fsdb.StoredProgram.Signal(
                                  Fsdb.StoredProgram.NamedCondition "named",
                                  [ "message_text", Lit(VString "handled") ]
                              ) ]
                        )
                    ) ] ->
                  ()
              | other -> failtestf "unexpected condition program: %A" other

          testCase "stored diagnostics syntax accepts comments as token separators"
          <| fun _ ->
              let body =
                  "BEGIN DECLARE condition_number INT DEFAULT 1; DECLARE state_name VARCHAR(5); GET/* area */CURRENT/* diagnostics */DIAGNOSTICS/* condition */CONDITION/* number */condition_number state_name/* assign */=/* item */RETURNED_SQLSTATE, @'error code' = MYSQL_ERRNO; END"

              match Fsdb.StoredProgram.parse defaultOptions body with
              | Ok
                  [ Fsdb.StoredProgram.Declare _
                    Fsdb.StoredProgram.Declare _
                    Fsdb.StoredProgram.GetDiagnostics diagnostics ] ->
                  Expect.equal diagnostics.Area Fsdb.StoredProgram.Current "diagnostics area"

                  match diagnostics.Request with
                  | Fsdb.StoredProgram.ConditionInformation(
                      Col "condition_number",
                      [ (Fsdb.StoredProgram.LocalVariable "state_name", Fsdb.StoredProgram.ReturnedSqlState)
                        (Fsdb.StoredProgram.UserVariable variable, Fsdb.StoredProgram.MySqlErrorNumber) ]
                    ) ->
                      Expect.equal variable.Name "error code" "quoted target name"
                  | request -> failtestf "unexpected diagnostics request: %A" request
              | other -> failtestf "unexpected diagnostics program: %A" other

              for invalid in
                  [ "GET DIAGNOSTICS value = MESSAGE_TEXT"
                    "GET DIAGNOSTICS CONDITION 1 value = NUMBER"
                    "GET DIAGNOSTICS CONDITION -1 value = MYSQL_ERRNO"
                    "GET DIAGNOSTICS CONDITION (1) value = MYSQL_ERRNO" ] do
                  match Fsdb.StoredProgram.parseDiagnostics defaultOptions invalid with
                  | Error _ -> ()
                  | Ok diagnostics -> failtestf "expected diagnostics grammar rejection, got %A" diagnostics

              match Fsdb.StoredProgram.parseDiagnostics defaultOptions "GET DIAGNOSTICS CONDITION 1.0 @code = MYSQL_ERRNO" with
              | Ok(Some _) -> ()
              | other -> failtestf "expected decimal condition number, got %A" other

          testCase "stored cursor syntax accepts comments and FETCH variants"
          <| fun _ ->
              let body =
                  """BEGIN
                       DECLARE value INT;
                       DECLARE done INT DEFAULT 0;
                       DECLARE/* name */numbers/* cursor */CURSOR/* for */FOR
                         SELECT n FROM source_numbers ORDER BY n;
                       DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = 1;
                       OPEN/* cursor */numbers;
                       FETCH NEXT FROM numbers INTO value;
                       FETCH numbers INTO value, done;
                       CLOSE/* cursor */numbers;
                     END"""

              match Fsdb.StoredProgram.parse defaultOptions body with
              | Ok
                  [ Fsdb.StoredProgram.Declare _
                    Fsdb.StoredProgram.Declare _
                    Fsdb.StoredProgram.DeclareCursor("numbers", Fsdb.Ast.Select _)
                    Fsdb.StoredProgram.DeclareHandler _
                    Fsdb.StoredProgram.OpenCursor "numbers"
                    Fsdb.StoredProgram.FetchCursor("numbers", [ "value" ])
                    Fsdb.StoredProgram.FetchCursor("numbers", [ "value"; "done" ])
                    Fsdb.StoredProgram.CloseCursor "numbers" ] ->
                  ()
              | other -> failtestf "unexpected cursor program: %A" other

          testCase "statement batches reject unterminated literals"
          <| fun _ ->
              match splitStatements "SELECT 'unterminated" with
              | Error _ -> ()
              | Ok statements -> failtestf "expected an error, got %A" statements

          testCase "statements reject unterminated block comments"
          <| fun _ ->
              for sql in [ "SELECT 1 /*"; "SELECT 1 /*!80000" ] do
                  match Fsdb.Parser.parse sql with
                  | Error _ -> ()
                  | Ok statement -> failtestf "expected %s to fail, got %A" sql statement

          testCase "select projections accept quoted aliases"
          <| fun _ ->
              for sql in [ "SELECT 1 'one'"; "SELECT 1 AS 'one'" ] do
                  match Fsdb.Parser.parse sql with
                  | Ok(Select { Projections = [ Lit(VInt 1L), Some "one" ] }) -> ()
                  | other -> failtestf "unexpected parse for %s: %A" sql other

          testCase "POSITION accepts the standard IN argument separator"
          <| fun _ ->
              match Fsdb.Parser.parse "SELECT POSITION(('ood') IN ('Moodle'))" with
              | Ok(Select { Projections = [ FuncCall("POSITION", [ Lit(VString "ood"); Lit(VString "Moodle") ]), None ] }) -> ()
              | other -> failtestf "unexpected POSITION parse: %A" other

          testCase "binary-introduced hexadecimal literals allow adjacent introducers"
          <| fun _ ->
              for sql in [ "SELECT _binaryX'00ff'"; "SELECT _binary X'00ff'" ] do
                  match Fsdb.Parser.parse sql with
                  | Ok(Select { Projections = [ Lit(VBytes [| 0uy; 255uy |]), None ] }) -> ()
                  | other -> failtestf "unexpected binary hexadecimal parse for %s: %A" sql other

          testCase "REGEXP, ANY, and SOME are reserved in expression position"
          <| fun _ ->
              for sql in [ "SELECT REGEXP"; "SELECT 1 = ANY (SELE)"; "SELECT SOME(1)" ] do
                  match Fsdb.Parser.parse sql with
                  | Error _ -> ()
                  | Ok statement -> failtestf "expected %s to fail, got %A" sql statement

          testCase "LOAD DATA LOCAL INFILE parses field and line settings"
          <| fun _ ->
              match
                  parseLocalLoad
                      "LOAD DATA LOCAL INFILE 'records.tsv' IGNORE INTO TABLE people FIELDS TERMINATED BY '\\t' ENCLOSED BY '\"' ESCAPED BY '\\\\' LINES TERMINATED BY '\\n' IGNORE 1 LINES (id, name)"
              with
              | Ok load ->
                  Expect.equal load.FileName "records.tsv" "file name"
                  Expect.equal load.Table "people" "target table"
                  Expect.isTrue load.Ignore "LOCAL input ignores row conversion errors"
                  Expect.equal load.FieldTerminator "\t" "field terminator"
                  Expect.equal load.EnclosedBy (Some "\"") "enclosure"
                  Expect.equal load.Escape (Some "\\") "escape"
                  Expect.equal load.LineTerminator "\n" "line terminator"
                  Expect.equal load.IgnoreLines 1 "header lines"
                  Expect.sequenceEqual load.Columns [ "id"; "name" ] "target columns"
              | Error error -> failtestf "unexpected parse error: %s" error

          testCase "LOAD DATA LOCAL INFILE rejects multi-character enclosure and escape settings"
          <| fun _ ->
              for sql in
                  [ "LOAD DATA LOCAL INFILE 'x' INTO TABLE t FIELDS ENCLOSED BY 'xx'"
                    "LOAD DATA LOCAL INFILE 'x' INTO TABLE t FIELDS ESCAPED BY 'xx'" ] do
                  match parseLocalLoad sql with
                  | Error _ -> ()
                  | Ok load -> failtestf "expected a separator error, got %A" load

          testCase "LOAD DATA LOCAL INFILE retains custom and empty escape settings"
          <| fun _ ->
              let parseEscape sql =
                  match parseLocalLoad sql with
                  | Ok load -> load.Escape
                  | Error error -> failtestf "unexpected parse error: %s" error

              Expect.equal
                  (parseEscape "LOAD DATA LOCAL INFILE 'x' INTO TABLE t FIELDS ESCAPED BY '!'")
                  (Some "!")
                  "custom escape"

              Expect.equal
                  (parseEscape "LOAD DATA LOCAL INFILE 'x' INTO TABLE t FIELDS ESCAPED BY ''")
                  (Some "")
                  "empty escape" ]
