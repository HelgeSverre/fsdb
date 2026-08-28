module Fsdb.Tests.ExecutorTests

open System.Text.Json
open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

let private run = TestSupport.Sql.execute
let private runDefault = TestSupport.Sql.executeDefault

let private newStore () = create ()

let tests =
    testList
        "executor"
        [ testList
              "CREATE / INSERT / SELECT with WHERE, ORDER BY, LIMIT"
              [ testCase "create, insert, and select round-trip with where + order + limit"
                <| fun _ ->
                    let store = newStore ()

                    runDefault store "CREATE TABLE users (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50), age INT)"
                    |> ignore

                    match runDefault store "INSERT INTO users (name, age) VALUES ('alice', 30), ('bob', 25), ('carol', 40)" with
                    | Affected 3UL -> ()
                    | other -> failtestf "expected 3 rows affected, got %A" other

                    match runDefault store "SELECT name, age FROM users WHERE age > 26 ORDER BY name LIMIT 10" with
                    | ResultSet([ "name"; "age" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "30" ]; [ Some "carol"; Some "40" ] ] "filtered, sorted rows"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * expands every column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "name" ], [ [ Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected id/name columns, got %A" other

                testCase "SELECT without FROM returns a single row"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 1 + 1 AS two" with
                    | ResultSet([ "two" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected a single computed row, got %A" other

                testCase "POSITION uses its standard SQL syntax"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT POSITION(('ood') IN ('Moodle')), POSITION(('Oracle') IN ('Moodle'))" with
                    | ResultSet(_, [ [ Some "2"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected POSITION offsets 2 and 0, got %A" other

                testCase "binary-introduced hexadecimal literals retain their bytes"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT HEX(_binaryX'01A03D')" with
                    | ResultSet(_, [ [ Some "01A03D" ] ]) -> ()
                    | other -> failtestf "expected the introduced bytes, got %A" other

                testCase "FROM DUAL provides MySQL's single-row source"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT 1 + 1 AS two FROM DUAL" with
                    | ResultSet([ "two" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected one row from DUAL, got %A" other

                testCase "SELECT duplicate modes and optimizer hints preserve result semantics"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (1), (2)" |> ignore

                    match runDefault store "SELECT DISTINCTROW HIGH_PRIORITY SQL_NO_CACHE n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "DISTINCTROW deduplicates"
                    | other -> failtestf "expected the hinted DISTINCTROW query to run, got %A" other

                    match runDefault store "SELECT ALL STRAIGHT_JOIN n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "1" ]; [ Some "2" ] ] "ALL retains duplicates"
                    | other -> failtestf "expected the hinted ALL query to run, got %A" other

                testCase "INSERT SET uses named-column insert and ODKU semantics"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT DEFAULT 7)" |> ignore
                    Expect.equal (runDefault store "INSERT INTO t SET id = 1") (Affected 1UL) "the row inserts"
                    Expect.equal (runDefault store "INSERT INTO t SET id = 1, n = 9 ON DUPLICATE KEY UPDATE n = VALUES(n)") (Affected 2UL) "ODKU updates"

                    match runDefault store "SELECT id, n FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "9" ] ]) -> ()
                    | other -> failtestf "expected the INSERT SET row, got %A" other

                testCase "ORDER BY DESC reverses the sort"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (3), (2)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n DESC" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "3" ]; [ Some "2" ]; [ Some "1" ] ] "descending order"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "ORDER BY an ENUM uses declaration order, including aliases and grouped results"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE memberships (role ENUM('owner','admin','member','viewer'))" |> ignore

                    runDefault store "INSERT INTO memberships VALUES ('viewer'), ('member'), ('admin'), ('owner'), ('member')"
                    |> ignore

                    match runDefault store "SELECT role AS r FROM memberships ORDER BY r" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "owner" ]; [ Some "admin" ]; [ Some "member" ]; [ Some "member" ]; [ Some "viewer" ] ]
                            "a projection alias retains the ENUM's ordinal sort"
                    | other -> failtestf "expected an enum resultset, got %A" other

                    match runDefault store "SELECT role, COUNT(*) FROM memberships GROUP BY role ORDER BY role" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "owner"; Some "1" ]
                              [ Some "admin"; Some "1" ]
                              [ Some "member"; Some "2" ]
                              [ Some "viewer"; Some "1" ] ]
                            "grouped ENUM results use declaration order"
                    | other -> failtestf "expected a grouped enum resultset, got %A" other

                    match runDefault store "SELECT role FROM memberships ORDER BY CAST(role AS CHAR)" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "admin" ]; [ Some "member" ]; [ Some "member" ]; [ Some "owner" ]; [ Some "viewer" ] ]
                            "an explicit character cast remains lexical"
                    | other -> failtestf "expected a cast-sorted resultset, got %A" other

                testCase "LIMIT with OFFSET pages through rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n LIMIT 2 OFFSET 1" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "3" ] ] "page of two starting at offset 1"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "a whole-number INSERT into a DECIMAL column is padded to its declared scale on read"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (price DECIMAL(10,2))" |> ignore
                    runDefault store "INSERT INTO t VALUES (100), (49.999)" |> ignore

                    match runDefault store "SELECT price FROM t ORDER BY price" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "50.00" ]; [ Some "100.00" ] ] "both rows carry the column's 2-digit scale"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * with no FROM is a 1096 error, not a 0-column resultset"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT *" with
                    | Err(1096, _) -> ()
                    | other -> failtestf "expected a 1096 error, got %A" other

                testCase "ORDER BY resolves a SELECT alias, not just a table column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (2)" |> ignore

                    match runDefault store "SELECT n AS x FROM t ORDER BY x" with
                    | ResultSet([ "x" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "sorted by alias"
                    | other -> failtestf "expected a resultset sorted by alias, got %A" other

                testCase "LIKE treats a backslash-escaped %/_ as a literal character, not a wildcard"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'axb' LIKE 'a\\%b'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected no match (escaped %% is literal), got %A" other

                    match runDefault store "SELECT 'a%b' LIKE 'a\\%b'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a literal %% match, got %A" other

                    match runDefault store "SELECT 'a\\\\aa' LIKE 'a\\\\\\\\aa'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected an escaped backslash match, got %A" other

                testCase "LIKE ESCAPE names a custom escape character instead of backslash"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'a%b' LIKE 'a!%b' ESCAPE '!'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a literal %% match via the '!' escape, got %A" other

                    match runDefault store "SELECT 'axb' LIKE 'a!%b' ESCAPE '!'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected no match (escaped %% is literal), got %A" other

                    // With a custom escape char in play, a bare backslash goes back to
                    // being a plain, unescaped character rather than an escape.
                    match runDefault store "SELECT 'a\\\\b' LIKE 'a\\\\b' ESCAPE '!'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected backslash to be literal when ESCAPE picks another character, got %A" other

                testCase "LIKE matches across embedded newlines"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'line1\nline2' LIKE '%line2%'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a match across the newline, got %A" other

                testCase "LIKE does not match past a trailing newline for an unqualified pattern"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'ab\n' LIKE 'ab'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected no match, got %A" other

                // LIKE under utf8mb4_0900_ai_ci, MySQL-verified: per-character
                // case+accent folding, but *no* expansions — 'æ' LIKE 'ae' is
                // false while 'æ' = 'ae' is true.
                testTheory
                    "LIKE folds case and accents per character"
                    [ "ÅGE", "age", "1"
                      "åge", "ag_", "1"
                      "ä", "a", "1"
                      "åge", "å%e", "1"
                      "age", "åge", "1" ]
                    <| fun (subject, pattern, expected) ->
                        match runDefault (newStore ()) (sprintf "SELECT '%s' LIKE '%s'" subject pattern) with
                        | ResultSet(_, [ [ Some v ] ]) -> Expect.equal v expected (sprintf "'%s' LIKE '%s'" subject pattern)
                        | other -> failtestf "expected a resultset, got %A" other

                testTheory
                    "LIKE never expands digraphs, even though equality does"
                    [ "æ", "ae"; "ß", "ss"; "ﬀ", "ff" ]
                    <| fun (subject, pattern) ->
                        match runDefault (newStore ()) (sprintf "SELECT '%s' LIKE '%s'" subject pattern) with
                        | ResultSet(_, [ [ Some "0" ] ]) -> ()
                        | other -> failtestf "expected no match (per-char), got %A" other

                testTheory
                    "unique keys collide under accent folding, exactly like MySQL's ai_ci"
                    [ "åge", "age"; "ß", "ss"; "æra", "aera"; "Céline", "celine" ]
                    <| fun (first, second) ->
                        let store = newStore ()
                        runDefault store "CREATE TABLE t (name VARCHAR(20) UNIQUE)" |> ignore
                        runDefault store (sprintf "INSERT INTO t VALUES ('%s')" first) |> ignore

                        match runDefault store (sprintf "INSERT INTO t VALUES ('%s')" second) with
                        | Err(1062, _) -> ()
                        | other -> failtestf "expected '%s' to collide with '%s' under ai_ci, got %A" second first other

                testTheory
                    "joins match accented keys to their base spelling"
                    [ "åge", "age"; "ØL", "ol" ]
                    <| fun (leftName, rightName) ->
                        let store = newStore ()
                        runDefault store "CREATE TABLE l (name VARCHAR(20))" |> ignore
                        runDefault store "CREATE TABLE r (name VARCHAR(20), tag VARCHAR(20))" |> ignore
                        runDefault store (sprintf "INSERT INTO l VALUES ('%s')" leftName) |> ignore
                        runDefault store (sprintf "INSERT INTO r VALUES ('%s', 'hit')" rightName) |> ignore

                        match runDefault store "SELECT r.tag FROM l JOIN r ON l.name = r.name" with
                        | ResultSet(_, [ [ Some "hit" ] ]) -> ()
                        | other -> failtestf "expected '%s' to join '%s' under ai_ci, got %A" leftName rightName other

                // Expression-level `expr COLLATE name` — MySQL-verified
                // behaviors, pinned per collation.
                testTheory
                    "expr COLLATE overrides the comparison collation"
                    [ "SELECT 'å' COLLATE utf8mb4_0900_as_cs = 'a'", "0"
                      "SELECT 'a' COLLATE utf8mb4_bin = 'A'", "0"
                      "SELECT 'a' COLLATE utf8mb4_bin = 'a '", "1"
                      "SELECT 'a' COLLATE utf8mb4_unicode_ci = 'a '", "1"
                      "SELECT 'ß' COLLATE utf8mb4_general_ci = 's'", "1"
                      "SELECT 'ß' COLLATE utf8mb4_general_ci = 'ss'", "0"
                      "SELECT 'i' COLLATE utf8mb4_tr_0900_ai_ci = 'I'", "0"
                      "SELECT 'ı' COLLATE utf8mb4_tr_0900_ai_ci = 'I'", "1"
                      "SELECT 'ñ' COLLATE utf8mb4_es_0900_ai_ci = 'n'", "0" ]
                    <| fun (sql, expected) ->
                        match runDefault (newStore ()) sql with
                        | ResultSet(_, [ [ Some v ] ]) -> Expect.equal v expected sql
                        | other -> failtestf "expected a resultset for %s, got %A" sql other

                testCase "WEIGHT_STRING applies CHAR truncation, BINARY padding, and source collation"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            "SELECT HEX(WEIGHT_STRING('abcdef' AS CHAR(3))), HEX(WEIGHT_STRING('abcdef' AS CHAR(4))), HEX(WEIGHT_STRING('abcdef' AS BINARY(8))), HEX(WEIGHT_STRING('a' COLLATE utf8mb4_bin)), HEX(WEIGHT_STRING('a' COLLATE utf8mb4_bin AS CHAR(3)) )"
                    with
                    | ResultSet(_, [ [ Some shortWeight; Some longWeight; Some binaryWeight; Some binaryCollationWeight; Some paddedCollationWeight ] ]) ->
                        Expect.notEqual shortWeight longWeight "CHAR width bounds characters"
                        Expect.equal binaryWeight "6162636465660000" "BINARY pads with zero bytes"
                        Expect.equal binaryCollationWeight "000061" "binary collation has code-point weights"
                        Expect.equal paddedCollationWeight "000061000020000020" "CHAR pads text before binary collation weights"
                    | other -> failtestf "expected one WEIGHT_STRING row, got %A" other

                testCase "WEIGHT_STRING keeps BIT column bytes"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE weight_bits (value BIT(8))" |> ignore
                    runDefault store "INSERT INTO weight_bits VALUES (b'10000000')" |> ignore

                    match runDefault store "SELECT HEX(WEIGHT_STRING(value)), HEX(WEIGHT_STRING(value AS BINARY(2))) FROM weight_bits" with
                    | ResultSet(_, [ [ Some weight; Some paddedWeight ] ]) ->
                        Expect.equal weight "80" "BIT weight is its stored byte"
                        Expect.equal paddedWeight "8000" "BINARY width pads the stored byte"
                    | other -> failtestf "expected one BIT weight row, got %A" other

                    match runDefault store "SELECT HEX(WEIGHT_STRING(value AS CHAR(3))), HEX(WEIGHT_STRING(X'6100' AS CHAR(1))), HEX(WEIGHT_STRING(X'6100' AS CHAR(2))), HEX(WEIGHT_STRING(X'6100' AS CHAR(3))) FROM weight_bits" with
                    | ResultSet(_, [ [ Some bitWeight; Some shortBytes; Some fullBytes; Some unpaddedBytes ] ]) ->
                        Expect.equal bitWeight "80" "CHAR leaves BIT bytes unpadded"
                        Expect.equal shortBytes "61" "CHAR truncates raw bytes"
                        Expect.equal fullBytes "6100" "CHAR retains raw zero bytes"
                        Expect.equal unpaddedBytes "6100" "CHAR does not pad raw bytes"
                    | other -> failtestf "expected one raw CHAR weight row, got %A" other

                testCase "WEIGHT_STRING BINARY uses the column character set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE weight_charset (value VARCHAR(10) CHARACTER SET latin1 COLLATE latin1_bin)" |> ignore
                    runDefault store "INSERT INTO weight_charset VALUES ('é')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT HEX(WEIGHT_STRING(value)), HEX(WEIGHT_STRING(value AS BINARY(2))), HEX(WEIGHT_STRING('é' COLLATE utf8mb4_bin)), HEX(WEIGHT_STRING('é' COLLATE utf8mb4_0900_bin)) FROM weight_charset"
                    with
                    | ResultSet(_, [ [ Some latin1Weight; Some paddedWeight; Some legacyWeight; Some utf8Weight ] ]) ->
                        Expect.equal latin1Weight "E9" "latin1_bin uses CP1252 bytes"
                        Expect.equal paddedWeight "E900" "latin1 bytes are padded"
                        Expect.equal legacyWeight "0000E9" "legacy utf8mb4_bin uses code-point weights"
                        Expect.equal utf8Weight "C3A9" "utf8mb4_0900_bin uses UTF-8 bytes"
                    | other -> failtestf "expected one latin1 weight row, got %A" other

                testTheory
                    "a bin-collated column compares byte-for-byte in WHERE and its unique key"
                    [ "Bob", "bob", "2"
                      "åge", "age", "2" ]
                    <| fun (first, second, _) ->
                        let store = newStore ()
                        runDefault store "CREATE TABLE t (name VARCHAR(20) COLLATE utf8mb4_bin UNIQUE)" |> ignore
                        runDefault store (sprintf "INSERT INTO t VALUES ('%s')" first) |> ignore
                        // case/accent differences are distinct under bin
                        runDefault store (sprintf "INSERT INTO t VALUES ('%s')" second) |> ignore

                        match runDefault store "SELECT COUNT(*) FROM t" with
                        | ResultSet(_, [ [ Some "2" ] ]) -> ()
                        | other -> failtestf "expected both bin-distinct rows to insert, got %A" other

                        match runDefault store (sprintf "SELECT COUNT(*) FROM t WHERE name = '%s'" first) with
                        | ResultSet(_, [ [ Some "1" ] ]) -> ()
                        | other -> failtestf "expected an exact byte match under bin, got %A" other

                testCase "a unicode_ci-collated unique key folds case and trailing spaces (PAD SPACE)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (name VARCHAR(20) COLLATE utf8mb4_unicode_ci UNIQUE)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Bob')" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('bob ')" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected 'bob ' to collide with 'Bob' under unicode_ci, got %A" other

                testCase "ORDER BY honors an expr-level COLLATE (da sorts æ ø å after z)"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            "SELECT w FROM (SELECT 'a' w UNION ALL SELECT 'z' UNION ALL SELECT 'æ' UNION ALL SELECT 'ø' UNION ALL SELECT 'å') x ORDER BY w COLLATE utf8mb4_da_0900_ai_ci"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "a" ]; [ Some "z" ]; [ Some "æ" ]; [ Some "ø" ]; [ Some "å" ] ] "Danish order, MySQL-verified"
                    | other -> failtestf "expected the Danish collation order, got %A" other

                // Collation-aware grouping/dedup: MySQL folds åge/age/ÅGE
                // into ONE group under ai_ci, in GROUP BY, DISTINCT, UNION,
                // and window PARTITION BY alike.
                testCase "GROUP BY folds by collation: åge/age/ÅGE are one group under ai_ci"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore

                    match runDefault store "SELECT name, COUNT(*) AS c FROM g GROUP BY name" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "åge"; Some "3" ] ] "one collation-equal group, first value wins"
                    | other -> failtestf "expected one group, got %A" other

                testCase "GROUP BY keeps case/accent distinctions under a bin column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age')" |> ignore

                    match runDefault store "SELECT name, COUNT(*) AS c FROM g GROUP BY name ORDER BY name" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "age"; Some "1" ]; [ Some "åge"; Some "1" ] ] "bin keeps them distinct"
                    | other -> failtestf "expected two bin-distinct groups, got %A" other

                testCase "SELECT DISTINCT dedupes collation-equal strings, first occurrence wins"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore

                    match runDefault store "SELECT DISTINCT name FROM g" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "åge" ] ] "one distinct value"
                    | other -> failtestf "expected one distinct row, got %A" other

                testCase "UNION (without ALL) dedupes collation-equal strings"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'åge' AS v UNION SELECT 'age'" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "åge" ] ] "one union row"
                    | other -> failtestf "expected the union to fold, got %A" other

                testCase "UNION keeps case/accent distinctions under a bin column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age')" |> ignore

                    match runDefault store "SELECT name FROM g UNION SELECT name FROM g" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            (rows |> List.sort)
                            [ [ Some "age" ]; [ Some "åge" ] ]
                            "bin keeps both values in the union"
                    | other -> failtestf "expected a bin union to keep both rows, got %A" other

                testCase "UNION of mixed-collation branches aggregates to the strictest collation, in either order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE m (a VARCHAR(10) COLLATE utf8mb4_0900_ai_ci, b VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO m VALUES ('åge', 'age')" |> ignore

                    match runDefault store "SELECT a FROM m UNION SELECT b FROM m" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            (rows |> List.sort)
                            [ [ Some "age" ]; [ Some "åge" ] ]
                            "a bin branch anywhere keeps åge/age apart"
                    | other -> failtestf "expected the mixed union to stay distinct, got %A" other

                    match runDefault store "SELECT b FROM m UNION SELECT a FROM m" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            (rows |> List.sort)
                            [ [ Some "age" ]; [ Some "åge" ] ]
                            "order-independent"
                    | other -> failtestf "expected the reversed mixed union to stay distinct, got %A" other

                testCase "UNIQUE keys honor the collation's pad attribute: NO PAD keeps 'x'/'x ' apart, PAD SPACE rejects"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (a VARCHAR(10) COLLATE utf8mb4_0900_ai_ci UNIQUE, b VARCHAR(10) COLLATE utf8mb4_bin UNIQUE)" |> ignore
                    runDefault store "INSERT INTO u (a) VALUES ('x'), ('x ')" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM u WHERE a IS NOT NULL" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected NO PAD to keep both rows, got %A" other

                    match runDefault store "INSERT INTO u (b) VALUES ('y'), ('y ')" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected PAD SPACE to reject the trailing-space duplicate, got %A" other

                testCase "COUNT(DISTINCT x) folds by the column's collation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE b (name VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore
                    runDefault store "INSERT INTO b VALUES ('åge'), ('age'), ('ÅGE')" |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT name) FROM g" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected ai_ci COUNT(DISTINCT) to fold to 1, got %A" other

                    match runDefault store "SELECT COUNT(DISTINCT name) FROM b" with
                    | ResultSet(_, [ [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected bin COUNT(DISTINCT) to keep 3, got %A" other

                testCase "COUNT(DISTINCT a, b) folds each tuple element by its own collation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE p (a VARCHAR(10), b VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO p VALUES ('åge','x'), ('age','x'), ('ÅGE','x'), ('åge','y')" |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT a, b) FROM p" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected the collation-equal pairs to fold to 2, got %A" other

                testCase "GROUP_CONCAT(DISTINCT x) folds by the column's collation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore

                    match runDefault store "SELECT GROUP_CONCAT(DISTINCT name SEPARATOR ',') FROM g" with
                    | ResultSet(_, [ [ Some "åge" ] ]) -> ()
                    | other -> failtestf "expected GROUP_CONCAT(DISTINCT) to fold to one value, got %A" other

                testCase "JOIN ON equality folds by each key column's own collation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE b1 (name VARCHAR(20) COLLATE utf8mb4_bin, v INT)" |> ignore
                    runDefault store "CREATE TABLE b2 (name VARCHAR(20) COLLATE utf8mb4_bin, w INT)" |> ignore
                    runDefault store "INSERT INTO b1 VALUES ('age', 1), ('ÅGE', 2)" |> ignore
                    runDefault store "INSERT INTO b2 VALUES ('age', 10), ('ÅGE', 20), ('AGE', 30)" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM b1 JOIN b2 ON b1.name = b2.name" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected a bin join to match byte-exactly (2), got %A" other

                    runDefault store "CREATE TABLE c1 (name VARCHAR(20), v INT)" |> ignore
                    runDefault store "CREATE TABLE c2 (name VARCHAR(20), w INT)" |> ignore
                    runDefault store "INSERT INTO c1 VALUES ('åge', 1)" |> ignore
                    runDefault store "INSERT INTO c2 VALUES ('age', 10), ('ÅGE', 20)" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM c1 JOIN c2 ON c1.name = c2.name" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected an ai_ci join to fold åge/age/ÅGE (2), got %A" other

                testCase "MIN/MAX over strings use the expression's collation, first-seen wins primary-equal ties"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE b (name VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore
                    runDefault store "INSERT INTO b VALUES ('age'), ('ÅGE')" |> ignore

                    match runDefault store "SELECT MAX(name) FROM g" with
                    | ResultSet(_, [ [ Some "åge" ] ]) -> ()
                    | other -> failtestf "expected ai_ci MAX to keep the first-seen value, got %A" other

                    match runDefault store "SELECT MIN(name) FROM g" with
                    | ResultSet(_, [ [ Some "åge" ] ]) -> ()
                    | other -> failtestf "expected ai_ci MIN to keep the first-seen value, got %A" other

                    match runDefault store "SELECT MAX(name) FROM b" with
                    | ResultSet(_, [ [ Some "ÅGE" ] ]) -> ()
                    | other -> failtestf "expected bin MAX by byte order, got %A" other

                    match runDefault store "SELECT MIN(name) FROM b" with
                    | ResultSet(_, [ [ Some "age" ] ]) -> ()
                    | other -> failtestf "expected bin MIN by byte order, got %A" other

                    // literals compare under the connection collation:
                    // primary-equal there too, so first-seen wins.
                    match runDefault store "SELECT MAX(x) FROM (SELECT 'ÅGE' AS x UNION ALL SELECT 'age') t" with
                    | ResultSet(_, [ [ Some "ÅGE" ] ]) -> ()
                    | other -> failtestf "expected literal MAX to keep the first-seen value, got %A" other

                testCase "window PARTITION BY folds by collation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE g (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO g VALUES ('åge'), ('age'), ('ÅGE')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT name, ROW_NUMBER() OVER (PARTITION BY name ORDER BY name) AS r FROM g"
                    with
                    | ResultSet(_, rows) ->
                        // one partition (collation-folded): row numbers are
                        // exactly {1,2,3} — their order follows the
                        // documented ICU tie-break, so assert the set.
                        Expect.equal
                            (rows |> List.map (fun r -> r.[1]) |> List.sort)
                            [ Some "1"; Some "2"; Some "3" ]
                            "one partition numbers 1..3"
                    | other -> failtestf "expected one window partition, got %A" other

                testCase "CHAR follows its collation's pad attribute: 0900 = NO PAD, unicode_ci = PAD SPACE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE c1 (c CHAR(10) COLLATE utf8mb4_unicode_ci)" |> ignore
                    runDefault store "CREATE TABLE c2 (c CHAR(10) COLLATE utf8mb4_0900_ai_ci)" |> ignore
                    runDefault store "INSERT INTO c1 VALUES ('a')" |> ignore
                    runDefault store "INSERT INTO c2 VALUES ('a')" |> ignore

                    match runDefault store "SELECT c = 'a ' FROM c1" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected PAD SPACE equality for CHAR under unicode_ci, got %A" other

                    match runDefault store "SELECT c = 'a ' FROM c2" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected NO PAD for CHAR under 0900_ai_ci, got %A" other

                testCase "COLLATE on a non-string column is accepted and ignored, like MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (i INT COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "SELECT 1 COLLATE utf8mb4_bin" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected COLLATE on a number to be a no-op, got %A" other

                testCase "the `_charset'...'` introducer labels bytes without converting, like MySQL"
                <| fun _ ->
                    let store = newStore ()

                    // binary: byte-wise comparison
                    match runDefault store "SELECT _binary'abc' = 'ABC'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected _binary to compare byte-wise, got %A" other

                    match runDefault store "SELECT _binary'abc' = 'abc'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected identical bytes to match, got %A" other

                    // utf8mb4: the bytes are already utf8mb4, so nothing changes
                    match runDefault store "SELECT _utf8mb4'ÅGE' = 'age'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected _utf8mb4 to fold under ai_ci, got %A" other

                    // latin1 labels the UTF-8 bytes: é's two bytes decode as
                    // the cp1252 pair Ã©, which doesn't equal é
                    match runDefault store "SELECT _latin1'é'" with
                    | ResultSet(_, [ [ Some "Ã©" ] ]) -> ()
                    | other -> failtestf "expected _latin1'é' to read back as Ã©, got %A" other

                    match runDefault store "SELECT _latin1'é' = 'é'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected the mojibake pair to differ from é, got %A" other

                    // ascii labels byte-wise: one '?' per non-7-bit byte
                    match runDefault store "SELECT _ascii'å'" with
                    | ResultSet(_, [ [ Some "??" ] ]) -> ()
                    | other -> failtestf "expected _ascii'å' to read back as ??, got %A" other

                    // the ASCII subset behaves identically in every charset
                    match runDefault store "SELECT _ascii'a' = 'A'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected the ascii subset to fold under the connection collation, got %A" other

                    // an unknown charset is a syntax error, like MySQL
                    match Fsdb.Parser.parse "SELECT _nosuchcharset'x'" with
                    | Error _ -> ()
                    | Ok _ -> failtestf "expected an unknown introducer charset to be a parse error"

                testCase "CONVERT(expr USING charset) transcodes with MySQL's cp1252/lossy rules"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CONVERT('é' USING latin1) = 'é'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected latin1 to keep é, got %A" other

                    // latin1 is cp1252 in MySQL: € is encodable (byte 0x80),
                    // so it survives rather than mapping to '?' — MySQL-verified.
                    match runDefault store "SELECT CONVERT('€' USING latin1)" with
                    | ResultSet(_, [ [ Some "€" ] ]) -> ()
                    | other -> failtestf "expected € to survive latin1 (cp1252), got %A" other

                    // a character cp1252 genuinely lacks (☃) maps to '?'
                    match runDefault store "SELECT CONVERT('☃' USING latin1)" with
                    | ResultSet(_, [ [ Some "?" ] ]) -> ()
                    | other -> failtestf "expected ☃ to lossy-map to ?, got %A" other

                    match runDefault store "SELECT CONVERT('é' USING ascii)" with
                    | ResultSet(_, [ [ Some "?" ] ]) -> ()
                    | other -> failtestf "expected é to lossy-map to ? under ascii, got %A" other

                testCase "CONVERT(expr USING binary) compares byte-for-byte, not via the connection collation"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CONVERT('abc' USING binary) = 'ABC'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected a binary string to compare byte-wise (0), got %A" other

                    match runDefault store "SELECT CONVERT('abc' USING binary) = 'abc'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected identical bytes to compare equal (1), got %A" other

                testCase "legacy charset columns retain encodable text and reject unencodable text in strict mode"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE lat (v VARCHAR(10) CHARACTER SET latin1)" |> ignore
                    runDefault store "CREATE TABLE asc_t (v VARCHAR(10) CHARACTER SET ascii)" |> ignore
                    runDefault store "INSERT INTO lat VALUES ('é'), ('ø')" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM lat WHERE v = 'é'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected latin1 to keep é, got %A" other

                    match runDefault store "INSERT INTO asc_t VALUES ('é')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected 1366 for é into ascii, got %A" other

                    runDefault store "INSERT INTO lat VALUES ('€')" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM lat WHERE v = '€'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected € to store as € under latin1/cp1252, got %A" other

                    match runDefault store "INSERT INTO lat VALUES ('☃')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected 1366 for an unencodable latin1 value, got %A" other

                testCase "an unknown column in WHERE is a 1054 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE ghost = 1" with
                    | Err(1054, msg) -> Expect.stringContains msg "ghost" "message names the column"
                    | other -> failtestf "expected a 1054 error, got %A" other

                testCase "an unknown function is a 1305 error"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NOPE(1)" with
                    | Err(1305, msg) -> Expect.stringContains msg "NOPE" "message names the function"
                    | other -> failtestf "expected a 1305 error, got %A" other

                testCase "an unavailable enterprise encryption function is a 1305 error"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT ASYMMETRIC_ENCRYPT('RSA', 'plain', 'key')" with
                    | Err(1305, msg) -> Expect.stringContains msg "ASYMMETRIC_ENCRYPT" "message names the function"
                    | other -> failtestf "expected a 1305 error, got %A" other

                testCase "the binary-operator form of INTERVAL arithmetic (expr - INTERVAL n unit) does real date math, like DATE_SUB"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT DATE('2024-03-15') - INTERVAL 10 DAY, DATE('2024-03-15') + INTERVAL 1 MONTH" with
                    | ResultSet(_, [ [ minus; plus ] ]) ->
                        Expect.equal minus (Some "2024-03-05") "subtracting 10 days"
                        Expect.equal plus (Some "2024-04-15") "adding 1 month"
                    | other -> failtestf "expected a resultset, got %A" other ]

          ExecutorDmlTests.tests

          testList
              "ALTER TABLE ... AFTER / FIRST"
              [ testCase "ADD COLUMN ... FIRST repositions in SELECT * order and ordinal_position"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT FIRST" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "flag"; "id"; "name" ], [ [ None; Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected flag first, got %A" other

                testCase "ADD COLUMN ... AFTER col repositions in SELECT * order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT AFTER id" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "flag"; "name" ], [ [ Some "1"; None; Some "x" ] ]) -> ()
                    | other -> failtestf "expected flag right after id, got %A" other

                testCase "CHANGE COLUMN ... FIRST renames, redefines, and repositions in one statement"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x', 30)" |> ignore
                    runDefault store "ALTER TABLE t CHANGE age years INT FIRST" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "years"; "id"; "name" ], [ [ Some "30"; Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected years first with its value moved along, got %A" other

                testCase "ALTER TABLE replaces comments on redefinitions and preserves them on RENAME COLUMN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT COMMENT 'original', body TEXT COMMENT 'body')" |> ignore
                    runDefault store "ALTER TABLE t MODIFY id BIGINT COMMENT 'replacement', CHANGE body content TEXT COMMENT ''" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN summary VARCHAR(20) COMMENT 'summary', RENAME COLUMN summary TO excerpt" |> ignore

                    match
                        runDefault
                            store
                            "SELECT column_name, column_comment FROM information_schema.columns WHERE table_name = 't' ORDER BY ordinal_position"
                    with
                    | ResultSet(_, [ [ Some "id"; Some "replacement" ]; [ Some "content"; Some "" ]; [ Some "excerpt"; Some "summary" ] ]) -> ()
                    | other -> failtestf "expected altered column comments, got %A" other

                testCase "ADD COLUMN ... FIRST updates information_schema.columns ordinal_position"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT FIRST" |> ignore

                    match
                        runDefault
                            store
                            "SELECT column_name FROM information_schema.columns WHERE table_name = 't' ORDER BY ordinal_position"
                    with
                    | ResultSet(_, [ [ Some "flag" ]; [ Some "id" ]; [ Some "name" ] ]) -> ()
                    | other -> failtestf "expected flag/id/name ordinal order, got %A" other ]

          testList
              "CHECKSUM TABLE"
              [ testCase "checksums are deterministic and reflect row contents"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, value VARCHAR(10))" |> ignore

                    Expect.equal
                        (runDefault store "CHECKSUM TABLE t")
                        (ResultSet([ "Table"; "Checksum" ], [ [ Some "fsdb.t"; Some "0" ] ]))
                        "empty checksum"

                    runDefault store "INSERT INTO t VALUES (1, 'a')" |> ignore

                    match runDefault store "CHECKSUM TABLE t, missing" with
                    | ResultSet([ "Table"; "Checksum" ], [ [ Some "fsdb.t"; Some checksum ]; [ Some "fsdb.missing"; None ] ]) ->
                        Expect.notEqual checksum "0" "row data changes the checksum"
                    | other -> failtestf "expected table and missing-table checksum rows, got %A" other

                    Expect.equal
                        (runDefault store "CHECKSUM TABLE t QUICK")
                        (ResultSet([ "Table"; "Checksum" ], [ [ Some "fsdb.t"; None ] ]))
                        "InnoDB-style QUICK result" ]

          testList
              "EXPLAIN"
              [ testCase "EXPLAIN SELECT with a WHERE and JOIN describes both tables in FROM order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 JOIN t2 ON t1.id = t2.t1_id WHERE t1.id = 1" with
                    | ResultSet(cols,
                                [ [ Some "1"; Some "SIMPLE"; Some "t1"; _; Some "ALL"; _; _; _; _; Some "2"; Some "100.00"; None ]
                                  [ Some "1"; Some "SIMPLE"; Some "t2"; _; Some "system"; _; _; _; _; Some "1"; Some "100.00"; Some extra ] ]) ->
                        Expect.equal
                            cols
                            [ "id"; "select_type"; "table"; "partitions"; "type"; "possible_keys"; "key"; "key_len"; "ref"; "rows"; "filtered"; "Extra" ]
                            "the classic 12 columns"

                        Expect.stringContains extra "Using where" "WHERE noted on the last table"
                    | other -> failtestf "expected a two-row join plan, got %A" other

                testCase "EXPLAIN SELECT by unique key is MySQL's const row, key columns filled in"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(50))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'a'), (2, 'b')" |> ignore

                    // Oracle (MySQL 8.4.11): type=const, possible_keys/key=PRIMARY,
                    // key_len=4, ref=const, rows=1, Extra NULL.
                    match runDefault store "EXPLAIN SELECT * FROM users WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "users"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a const plan row, got %A" other

                testCase "EXPLAIN const also fires on an alias-qualified equality (WHERE u.id = 1)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY, v INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 10)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM users u WHERE u.id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "u"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a const plan for the alias-qualified lookup, got %A" other

                testCase "EXPLAIN UPDATE/DELETE by primary key report the same const row a SELECT does"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY, v INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 10), (2, 20)" |> ignore

                    match runDefault store "EXPLAIN UPDATE users SET v = 5 WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "UPDATE"; Some "users"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a const UPDATE plan, got %A" other

                    match runDefault store "EXPLAIN DELETE FROM users WHERE id = 2" with
                    | ResultSet(_, [ [ Some "1"; Some "DELETE"; Some "users"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a const DELETE plan, got %A" other

                testCase "EXPLAIN by a nullable VARCHAR(50) UNIQUE key reports key_len 203 (50*4+2+1 for the null flag)"
                <| fun _ ->
                    // Probe values are key-relative even when the key column
                    // is not the table's first column.
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(50) UNIQUE)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'a')" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM users WHERE name = 'a'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "users"; None; Some "const"; Some "name"; Some "name"; Some "203"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected key_len 203 for the nullable unique varchar, got %A" other

                testCase "EXPLAIN by a NOT NULL VARCHAR(50) UNIQUE key reports key_len 202 (no null flag)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(50) NOT NULL UNIQUE)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'a')" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM users WHERE name = 'a'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "users"; None; Some "const"; Some "name"; Some "name"; Some "202"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected key_len 202 for the NOT NULL unique varchar, got %A" other

                testCase "EXPLAIN by a BIGINT PRIMARY KEY reports key_len 8"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id BIGINT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM users WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "users"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "8"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected key_len 8 for the bigint primary key, got %A" other

                testCase "EXPLAIN by unique key with no matching row is the const-miss shape"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(50))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'a')" |> ignore

                    // Oracle: every cell NULL, Extra 'no matching row in const table'.
                    match runDefault store "EXPLAIN SELECT * FROM users WHERE id = 99" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; None; None; None; None; None; None; None; None; None; Some "no matching row in const table" ] ]) ->
                        ()
                    | other -> failtestf "expected the const-miss row, got %A" other

                testCase "EXPLAIN SELECT with a correlated EXISTS subquery is DEPENDENT SUBQUERY"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore

                    match
                        runDefault
                            store
                            "EXPLAIN SELECT * FROM t1 WHERE EXISTS (SELECT 1 FROM t2 WHERE t2.t1_id = t1.id)"
                    with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "DEPENDENT SUBQUERY") "the correlated subquery is flagged dependent"
                        let ids = rows |> List.map (fun r -> r.[0])
                        Expect.equal (ids |> List.distinct |> List.length) 2 "outer and subquery are different id blocks"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with an uncorrelated subquery is plain SUBQUERY"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 WHERE id IN (SELECT id FROM t2)" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "SUBQUERY") "the uncorrelated subquery is plain SUBQUERY"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with a quantified comparison plans its subquery"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 WHERE id = ANY (SELECT id FROM t2)" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "SUBQUERY") "the quantified subquery has its own plan block"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with a derived table is DERIVED"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM (SELECT n FROM t) AS d" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "DERIVED") "the derived table gets its own DERIVED block"
                        let tables = rows |> List.map (fun r -> r.[2])
                        Expect.contains tables (Some "<derived2>") "the outer row references it by its derived id"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with GROUP BY notes Using temporary"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (g INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT g, COUNT(*) FROM t GROUP BY g" with
                    | ResultSet(_, [ row ]) -> Expect.stringContains (row.[11] |> Option.defaultValue "") "Using temporary" "GROUP BY notes Using temporary"
                    | other -> failtestf "expected one row, got %A" other

                testCase "EXPLAIN SELECT with ORDER BY notes Using filesort"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, [ row ]) -> Expect.stringContains (row.[11] |> Option.defaultValue "") "Using filesort" "ORDER BY notes Using filesort"
                    | other -> failtestf "expected one row, got %A" other

                testCase "EXPLAIN on a UNION includes PRIMARY, UNION, and a UNION RESULT row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT n FROM t UNION SELECT n FROM t" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.choose (fun r -> r.[1])
                        Expect.contains selectTypes "PRIMARY" "first branch is PRIMARY"
                        Expect.contains selectTypes "UNION" "second branch is UNION"
                        Expect.contains selectTypes "UNION RESULT" "a UNION RESULT row combines them"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN UPDATE describes the target table with select_type UPDATE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 1), (2, 2)" |> ignore

                    match runDefault store "EXPLAIN UPDATE t SET n = 1 WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "UPDATE"; Some "t"; _; Some "ALL"; _; _; _; _; Some "2"; Some "100.00"; Some extra ] ]) ->
                        Expect.stringContains extra "Using where" "WHERE noted"
                    | other -> failtestf "expected one UPDATE-typed row, got %A" other

                testCase "EXPLAIN DELETE describes the target table with select_type DELETE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN DELETE FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "DELETE"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected one DELETE-typed row, got %A" other

                testCase "EXPLAIN INSERT works without error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN INSERT INTO t VALUES (1)" with
                    | ResultSet(_, [ [ Some "1"; Some "INSERT"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected one INSERT-typed row, got %A" other

                testCase "EXPLAIN FORMAT=TRADITIONAL parses the same as bare EXPLAIN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN FORMAT=TRADITIONAL SELECT * FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected the same shape as bare EXPLAIN, got %A" other

                testCase "EXPLAIN FORMAT=JSON returns a structured plan"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN FORMAT=JSON SELECT id FROM t WHERE id = 1" with
                    | ResultSet([ "EXPLAIN" ], [ [ Some json ] ]) ->
                        use document = JsonDocument.Parse(json)
                        let queryBlock = document.RootElement.GetProperty("query_block")
                        let table = queryBlock.GetProperty("table")
                        Expect.equal (queryBlock.GetProperty("select_id").GetInt32()) 1 "select id"
                        Expect.equal (table.GetProperty("table_name").GetString()) "t" "table name"
                        Expect.equal (table.GetProperty("access_type").GetString()) "const" "access type"
                        Expect.equal (table.GetProperty("key").GetString()) "PRIMARY" "selected key"
                    | other -> failtestf "expected one JSON plan document, got %A" other

                testCase "EXPLAIN FORMAT=TREE returns the shared access plan"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, value INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10), (2, 20)" |> ignore

                    match runDefault store "EXPLAIN FORMAT=TREE SELECT id FROM t WHERE id = 1" with
                    | ResultSet([ "EXPLAIN" ], [ [ Some plan ] ]) ->
                        Expect.stringContains plan "const on t" "access path"
                        Expect.stringContains plan "rows=1" "estimate"
                    | other -> failtestf "expected one tree plan, got %A" other

                testCase "EXPLAIN ANALYZE executes a SELECT and reports observations"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    match runDefault store "EXPLAIN ANALYZE SELECT id FROM t WHERE id = 1" with
                    | ResultSet([ "EXPLAIN" ], [ [ Some root ]; [ Some access ] ]) ->
                        Expect.stringContains root "actual time=" "runtime is reported"
                        Expect.stringContains root "rows=1" "actual output cardinality"
                        Expect.stringContains access "const on t" "observed plan retains its access path"
                    | other -> failtestf "expected an analyzed plan, got %A" other

                testCase "DESCRIBE statement is an EXPLAIN synonym"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore

                    Expect.equal
                        (runDefault store "DESCRIBE SELECT * FROM t WHERE id = 1")
                        (runDefault store "EXPLAIN SELECT * FROM t WHERE id = 1")
                        "DESCRIBE uses the same plan"

                testCase "EXPLAIN on a 1-row table reports type system"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t" with
                    | ResultSet(_, [ [ _; _; _; _; Some "system"; _; _; _; _; Some "1"; _; _ ] ]) -> ()
                    | other -> failtestf "expected type=system for a 1-row table, got %A" other

                testCase "EXPLAIN validates the statement it describes: a missing table is 1146, not a fake plan"
                <| fun _ ->
                    let store = newStore ()

                    for sql in
                        [ "EXPLAIN SELECT * FROM nosuchtable"
                          "EXPLAIN UPDATE nosuchtable SET x = 1"
                          "EXPLAIN INSERT INTO nosuchtable VALUES (1)" ] do
                        match runDefault store sql with
                        | Err(1146, _) -> ()
                        | other -> failtestf "expected 1146 for %s, got %A" sql other

                testCase "EXPLAIN on a JOIN against a missing table is 1146"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 JOIN nosuch2 ON t1.id = nosuch2.id" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected 1146, got %A" other

                testCase "EXPLAIN validates unknown columns: 1054, not a fake plan"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT nosuchcol FROM t1" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected 1054, got %A" other

                    match runDefault store "EXPLAIN DELETE FROM t1 WHERE nosuchcol = 1" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected 1054, got %A" other ]

          testList
              "column defaults"
              [ testCase "functional defaults evaluate per row and may reference supplied columns"
                <| fun _ ->
                    let store = newStore ()
                    runDefault
                        store
                        "CREATE TABLE defaults_expr (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), magnitude INT DEFAULT (ABS(-2)), token VARCHAR(40) DEFAULT (UUID()))"
                    |> ignore

                    match runDefault store "SELECT COLUMN_DEFAULT, EXTRA FROM information_schema.columns WHERE TABLE_NAME = 'defaults_expr' AND COLUMN_NAME = 'magnitude'" with
                    | ResultSet(_, [ [ Some defaultValue; Some "DEFAULT_GENERATED" ] ]) ->
                        Expect.stringContains defaultValue "abs" "the expression is exposed in metadata"
                    | other -> failtestf "expected functional-default metadata, got %A" other

                    match runDefault store "INSERT INTO defaults_expr (id, base) VALUES (1, 4), (2, 9)" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected two inserted rows, got %A" other

                    match runDefault store "SELECT id, derived, magnitude, LENGTH(token) FROM defaults_expr ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "5"; Some "2"; Some "36" ]
                              [ Some "2"; Some "10"; Some "2"; Some "36" ] ]
                            "each omitted functional default is evaluated in its candidate row"
                    | other -> failtestf "expected functional defaults, got %A" other

                    match runDefault store "INSERT INTO defaults_expr (id, base, derived) VALUES (3, 4, NULL)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected an explicit NULL insert, got %A" other

                    match runDefault store "SELECT derived FROM defaults_expr WHERE id = 3" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected explicit NULL to bypass the default, got %A" other

                    runDefault
                        store
                        "CREATE TABLE explicit_defaults (id INT AUTO_INCREMENT PRIMARY KEY, base INT DEFAULT 7, derived INT DEFAULT (base + 1))"
                    |> ignore

                    match runDefault store "INSERT INTO explicit_defaults VALUES (DEFAULT, DEFAULT, DEFAULT)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected explicit defaults to insert, got %A" other

                    match runDefault store "SELECT id, base, derived FROM explicit_defaults" with
                    | ResultSet(_, [ [ Some "1"; Some "7"; Some "8" ] ]) -> ()
                    | other -> failtestf "expected declared defaults, got %A" other

                    match runDefault store "INSERT INTO defaults_expr (id, base) VALUES (1, 20) ON DUPLICATE KEY UPDATE base = VALUES(base), derived = VALUES(derived)" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected the upsert candidate default to be evaluated, got %A" other

                    match runDefault store "REPLACE INTO defaults_expr (id, base) VALUES (2, 30)" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected the replacement default to be evaluated, got %A" other

                    match runDefault store "SELECT base, derived FROM defaults_expr WHERE id IN (1, 2) ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "20"; Some "21" ]; [ Some "30"; Some "31" ] ] "upsert and replace use their candidate defaults"
                    | other -> failtestf "expected upserted and replaced defaults, got %A" other

                    match runDefault store "ALTER TABLE defaults_expr ADD COLUMN shifted INT DEFAULT (base + 2)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected the functional column to be added, got %A" other

                    match runDefault store "SELECT shifted FROM defaults_expr ORDER BY id" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "22" ]; [ Some "32" ]; [ Some "6" ] ] "existing rows receive the added functional default"
                    | other -> failtestf "expected backfilled defaults, got %A" other

                testCase "functional defaults reject unsafe or invalid dependencies"
                <| fun _ ->
                    let store = newStore ()

                    for sql, code in
                        [ "CREATE TABLE default_subquery (n INT DEFAULT ((SELECT 1)))", 3769
                          "CREATE TABLE default_variable (n INT DEFAULT (@x + 1))", 3772
                          "CREATE TABLE default_sleep (n INT DEFAULT (SLEEP(0)))", 3770
                          "CREATE TABLE default_aggregate (n INT DEFAULT (SUM(1)))", 1111
                          "CREATE TABLE default_auto (id INT AUTO_INCREMENT PRIMARY KEY, n INT DEFAULT (id + 1))", 3768
                          "CREATE TABLE default_self (n INT DEFAULT (n + 1))", 3767
                          "CREATE TABLE default_forward (a INT DEFAULT (b + 1), b INT DEFAULT (ABS(-2)))", 3767 ] do
                        match runDefault store sql with
                        | Err(actual, _) -> Expect.equal actual code sql
                        | other -> failtestf "expected error %d for %s, got %A" code sql other

                testCase "DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) parses, and an omitted column stores 6 fractional digits"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, c DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6))" |> ignore
                    runDefault store "INSERT INTO t (id) VALUES (1)" |> ignore
                    System.Threading.Thread.Sleep 2
                    runDefault store "INSERT INTO t (id) VALUES (2)" |> ignore

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet([ "c" ], [ [ Some a ]; [ Some b ] ]) ->
                        for text in [ a; b ] do
                            Expect.isTrue
                                (System.Text.RegularExpressions.Regex.IsMatch(text, @"\.\d{6}$"))
                                (sprintf "expected six fractional digits, got %s" text)

                        // The default must carry the clock's actual
                        // sub-second component, not a truncated-to-seconds
                        // value that the fsp-6 rendering merely pads back out
                        // to `.000000` — two inserts 2ms apart can't both
                        // land on an exact whole second.
                        Expect.isTrue
                            (not (a.EndsWith ".000000") || not (b.EndsWith ".000000"))
                            (sprintf "expected real microseconds in at least one of %s / %s" a b)
                    | other -> failtestf "expected two rows with rendered timestamps, got %A" other

                testCase "bare DEFAULT CURRENT_TIMESTAMP on a fsp-0 DATETIME stores no fraction"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, c DATETIME DEFAULT CURRENT_TIMESTAMP)" |> ignore
                    runDefault store "INSERT INTO t (id) VALUES (1)" |> ignore

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet([ "c" ], [ [ Some text ] ]) ->
                        Expect.isFalse (text.Contains ".") (sprintf "expected no fractional part, got %s" text)
                    | other -> failtestf "expected one row with a rendered timestamp, got %A" other

                testCase "ON UPDATE CURRENT_TIMESTAMP(6) parses"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            "CREATE TABLE t (id INT, c DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6))"
                    with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected CREATE TABLE to parse and succeed, got %A" other

                testCase "UPDATE that actually changes a row bumps its ON UPDATE CURRENT_TIMESTAMP column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT, stamp DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6))" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    let stampOf () =
                        match runDefault store "SELECT stamp FROM t WHERE id = 1" with
                        | ResultSet(_, [ [ Some s ] ]) -> s
                        | other -> failtestf "expected one row, got %A" other

                    let before = stampOf ()
                    System.Threading.Thread.Sleep 20
                    runDefault store "UPDATE t SET n = 20 WHERE id = 1" |> ignore
                    let after = stampOf ()

                    Expect.notEqual after before "changing another column bumps the auto column too"

                testCase "a no-op UPDATE (same values) doesn't bump ON UPDATE CURRENT_TIMESTAMP"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT, stamp DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6))" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    let stampOf () =
                        match runDefault store "SELECT stamp FROM t WHERE id = 1" with
                        | ResultSet(_, [ [ Some s ] ]) -> s
                        | other -> failtestf "expected one row, got %A" other

                    let before = stampOf ()
                    System.Threading.Thread.Sleep 20
                    runDefault store "UPDATE t SET n = 10 WHERE id = 1" |> ignore
                    let after = stampOf ()

                    Expect.equal after before "setting a column to its current value leaves the row unchanged, no bump"

                testCase "an explicit assignment to the ON UPDATE CURRENT_TIMESTAMP column wins over the auto-bump"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT, stamp DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6))" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore
                    runDefault store "UPDATE t SET n = 20, stamp = '2000-01-01 00:00:00.000000' WHERE id = 1" |> ignore

                    match runDefault store "SELECT stamp FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some s ] ]) -> Expect.equal s "2000-01-01 00:00:00.000000" "the explicit value is kept, not overwritten by NOW()"
                    | other -> failtestf "expected one row, got %A" other

                testCase "INSERT ... ON DUPLICATE KEY UPDATE that changes a row bumps its ON UPDATE CURRENT_TIMESTAMP column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT, stamp DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6))" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    let stampOf () =
                        match runDefault store "SELECT stamp FROM t WHERE id = 1" with
                        | ResultSet(_, [ [ Some s ] ]) -> s
                        | other -> failtestf "expected one row, got %A" other

                    let before = stampOf ()
                    System.Threading.Thread.Sleep 20
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 99) ON DUPLICATE KEY UPDATE n = 99" |> ignore
                    let after = stampOf ()

                    Expect.notEqual after before "ODKU changing another column bumps the auto column too" ]

          testList
              "functions"
              [ testCase "UPPER, LOWER, CONCAT, and arithmetic compose in one projection"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice', 30)" |> ignore

                    match runDefault store "SELECT UPPER(name), age + 1 FROM users" with
                    | ResultSet(_, [ [ Some "ALICE"; Some "31" ] ]) -> ()
                    | other -> failtestf "expected ALICE/31, got %A" other

                testCase "COALESCE and IFNULL skip past NULLs"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT COALESCE(NULL, NULL, 5), IFNULL(NULL, 7)" with
                    | ResultSet(_, [ [ Some "5"; Some "7" ] ]) -> ()
                    | other -> failtestf "expected 5/7, got %A" other

                    // A non-NULL first argument is returned unchanged — the
                    // second branch of IFNULL's match. Cover it directly so an
                    // inverted/incorrect NULL check can't hide in a test that
                    // only ever feeds the NULL-first shape.
                    match runDefault store "SELECT IFNULL('x', 'y')" with
                    | ResultSet(_, [ [ Some "x" ] ]) -> ()
                    | other -> failtestf "expected IFNULL('x','y') = 'x', got %A" other

                testCase "a custom registerScalar function is callable through the same registry"
                <| fun _ ->
                    let store = newStore ()

                    let registry =
                        builtins
                        |> registerScalar "SHOUT" (function
                            | [ VString s ] -> VString(s.ToUpperInvariant() + "!")
                            | _ -> VNull)

                    match run store registry "SELECT SHOUT('hello')" with
                    | ResultSet(_, [ [ Some "HELLO!" ] ]) -> ()
                    | other -> failtestf "expected HELLO!, got %A" other

                    let registry = registry |> registerScalar "MOD" (fun _ -> VInt 42L)

                    match run store registry "SELECT MOD(1, 0)" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected the custom MOD override, got %A" other

                testCase "IF() works even though IF is also a reserved keyword"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT IF(1, 'yes', 'no')" with
                    | ResultSet(_, [ [ Some "yes" ] ]) -> ()
                    | other -> failtestf "expected yes, got %A" other

                testCase "ABS and ROUND"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT ABS(-5), ROUND(3.7)" with
                    | ResultSet(_, [ [ Some "5"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected 5/4, got %A" other ]

          testList
              "three-valued logic"
              [ testCase "WHERE x = NULL matches no rows (NULL never equals anything)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (NULL)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n = NULL" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero rows, got %A" other

                testCase "NULL AND FALSE is FALSE, not NULL (short-circuits to false either way)"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL AND FALSE" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected 0, got %A" other

                testCase "NULL AND TRUE is NULL (unknown, neither true nor false)"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL AND TRUE" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL, got %A" other

                testCase "ORDER BY sorts NULLs first"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (2), (NULL), (1)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ None ]; [ Some "1" ]; [ Some "2" ] ] "NULL sorts first"
                    | other -> failtestf "expected a resultset, got %A" other ]

          testList
              "LIKE / IN / BETWEEN"
              [ testCase "IN matches any candidate, including a mix of types"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n IN (1, 3) ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "matches 1 and 3 only"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "NOT IN excludes the candidates"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n NOT IN (2) ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "excludes 2"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "BETWEEN is inclusive on both ends"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n BETWEEN 2 AND 3 ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "3" ] ] "includes both endpoints"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "BETWEEN on an ENUM compares by declaration ordinal, same as =/IN"
                <| fun _ ->
                    // BETWEEN must apply the same ENUM-ordinal resolution as
                    // `=`/`IN`, not raw `Value.compare`.
                    let store = newStore ()
                    runDefault store "CREATE TABLE te (e ENUM('a','b','c'))" |> ignore
                    runDefault store "INSERT INTO te VALUES ('a'), ('b'), ('c')" |> ignore

                    match runDefault store "SELECT e FROM te WHERE e BETWEEN 1 AND 2 ORDER BY e" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "a" ]; [ Some "b" ] ] "ordinal 1..2 is a..b, not lexical"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "BETWEEN on a _bin column is case-sensitive, same as =/IN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE tb (s VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO tb VALUES ('A'), ('B'), ('a'), ('b'), ('c')" |> ignore

                    match runDefault store "SELECT s FROM tb WHERE s BETWEEN 'A' AND 'B' ORDER BY s" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "A" ]; [ Some "B" ] ] "byte-ordinal range, lowercase excluded"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "LIKE with % and _ wildcards"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('alice'), ('bob'), ('alan')" |> ignore

                    match runDefault store "SELECT name FROM t WHERE name LIKE 'al_ce' ORDER BY name" with
                    | ResultSet(_, [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice, got %A" other

                    match runDefault store "SELECT name FROM t WHERE name LIKE 'a%' ORDER BY name" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "alan" ]; [ Some "alice" ] ] "both a-names"
                    | other -> failtestf "expected a resultset, got %A" other ]

          testList
              "storage errors reaching QueryResult"
              [ testCase "INSERT into an unknown table is a 1146 error"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "INSERT INTO ghost VALUES (1)" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected a 1146 error, got %A" other

                testCase "strict omission of a NOT NULL column with no default is a 1364 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL)" |> ignore

                    match runDefault store "INSERT INTO t (id) VALUES (1)" with
                    | Err(1364, _) -> ()
                    | other -> failtestf "expected a 1364 error, got %A" other

                testCase "non-strict omission uses the column type's implicit default"
                <| fun _ ->
                    let store = newStore ()
                    Fsdb.Storage.setStrictMode store false
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL, amount DECIMAL(5,2) NOT NULL)" |> ignore

                    match runDefault store "INSERT INTO t (id) VALUES (1)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the non-strict insert to succeed, got %A" other

                    match runDefault store "SELECT id, name, amount FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some ""; Some "0.00" ] ]) -> ()
                    | other -> failtestf "expected implicit defaults, got %A" other

                testCase "an uncoercible value for a column's type is a 1366 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('not a number')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected a 1366 error, got %A" other ]

          testCase "DO evaluates every expression and returns no result set"
          <| fun _ ->
              let store = newStore ()
              Expect.equal (runDefault store "DO 1, ABS(2), (SELECT 3)") (Affected 0UL) "expressions evaluated"

              match runDefault store "DO NO_SUCH_FUNCTION()" with
              | Err(1305, _) -> ()
              | other -> failtestf "expected expression error, got %A" other

          testList
              "ALTER TABLE / RENAME TABLE / CREATE INDEX / DROP INDEX end to end"
              [ testCase "ADD COLUMN then SELECT sees the new column with default values on old rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN active INT DEFAULT 1" |> ignore

                    match runDefault store "SELECT id, active FROM t" with
                    | ResultSet([ "id"; "active" ], [ [ Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected the new column filled with its default, got %A" other

                testCase "ALTER COLUMN SET and DROP DEFAULT affect subsequent inserts"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT DEFAULT 1)" |> ignore
                    runDefault store "ALTER TABLE t ALTER COLUMN n SET DEFAULT 7" |> ignore
                    runDefault store "INSERT INTO t (id) VALUES (1)" |> ignore
                    runDefault store "ALTER TABLE t ALTER n DROP DEFAULT" |> ignore
                    runDefault store "INSERT INTO t (id) VALUES (2)" |> ignore

                    match runDefault store "SELECT id, n FROM t ORDER BY id" with
                    | ResultSet(_, [ [ Some "1"; Some "7" ]; [ Some "2"; None ] ]) -> ()
                    | other -> failtestf "expected changed defaults, got %A" other

                    match runDefault store "ALTER TABLE t ALTER missing SET DEFAULT 1" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected unknown column error, got %A" other

                testCase "DROP COLUMN then SELECT * doesn't see it"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, junk INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 2)" |> ignore
                    runDefault store "ALTER TABLE t DROP COLUMN junk" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected only id left, got %A" other

                testCase "RENAME INDEX preserves the index and reports name errors"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, u INT, UNIQUE KEY old_ix (u), KEY occupied (id))" |> ignore
                    Expect.equal (runDefault store "ALTER TABLE t RENAME INDEX old_ix TO new_ix") (Affected 0UL) "renamed"

                    let table = store.Catalog.[defaultDatabase].[normalizeTableName "t"]
                    Expect.isTrue (table.Indexes |> List.exists (fun index -> index.Name = "new_ix" && index.Unique)) "unique index renamed"
                    Expect.isFalse (table.Indexes |> List.exists (fun index -> index.Name = "old_ix")) "old name removed"

                    match runDefault store "ALTER TABLE t RENAME INDEX missing TO another" with
                    | Err(1176, _) -> ()
                    | other -> failtestf "expected missing key error, got %A" other

                    match runDefault store "ALTER TABLE t RENAME INDEX new_ix TO occupied" with
                    | Err(1061, _) -> ()
                    | other -> failtestf "expected duplicate key name error, got %A" other

                testCase "ALTER TABLE adds an enforced UNIQUE prefix index"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (value VARCHAR(50))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('1234567890 foo')" |> ignore
                    runDefault store "ALTER TABLE t ADD UNIQUE KEY uq_value (value(10))" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('1234567890 bar')" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected the shared prefix to collide, got %A" other

                testCase "LOWER functional indexes enforce transformed uniqueness"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE books (id INT PRIMARY KEY, external_id VARCHAR(255), UNIQUE INDEX ix_lower_external_id ((LOWER(external_id))))" |> ignore
                    Expect.equal (runDefault store "INSERT INTO books VALUES (1, 'Reference')") (Affected 1UL) "first spelling accepted"

                    match runDefault store "INSERT INTO books VALUES (2, 'reference')" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected lowercase-equivalent values to collide, got %A" other

                    Expect.equal (runDefault store "INSERT INTO books VALUES (2, 'different')") (Affected 1UL) "different value accepted"

                    match runDefault store "UPDATE books SET external_id = 'REFERENCE' WHERE id = 2" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected updates to maintain the functional key, got %A" other

                    Expect.equal
                        (runDefault
                            store
                            "CREATE TABLE companies (name VARCHAR(255), rating BIGINT, firm_name VARCHAR(255), firm_id BIGINT, client_of BIGINT, INDEX company_name_index USING btree (name), INDEX company_expression_index ((CASE WHEN rating > 0 THEN lower(name) END) DESC), INDEX full_name_index ((CONCAT_WS(firm_name, name, _utf8mb4' '))), INDEX company_disabled_index (firm_id, client_of) INVISIBLE)")
                        (Affected 0UL)
                        "non-unique expression and visibility metadata is accepted"

                    match runDefault store "CREATE INDEX invalid_expression ON companies ((UUID()))" with
                    | Err(3758, _) -> ()
                    | other -> failtestf "expected nondeterministic expression rejection, got %A" other

                    match runDefault store "ALTER TABLE companies ADD INDEX invalid_alter ((RAND()))" with
                    | Err(3758, _) -> ()
                    | other -> failtestf "expected subquery expression rejection, got %A" other

                    let companies = store.Catalog.[defaultDatabase].[normalizeTableName "companies"]
                    Expect.isFalse (companies.Indexes |> List.exists (fun index -> index.Name = "invalid_expression")) "rejected index is not published"
                    Expect.isFalse (companies.Indexes |> List.exists (fun index -> index.Name = "invalid_alter")) "rejected ALTER index is not published"

                testCase "ALTER TABLE ENGINE accepts InnoDB and rejects unsupported engines"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    Expect.equal (runDefault store "ALTER TABLE t ENGINE=InnoDB") (Affected 0UL) "InnoDB remains selected"

                    match runDefault store "ALTER TABLE t ENGINE=MEMORY" with
                    | Err(1286, _) -> ()
                    | other -> failtestf "expected unknown storage engine error, got %A" other

                    match runDefault store "ALTER TABLE missing ENGINE=InnoDB" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected missing table error, got %A" other

                testCase "ALTER TABLE COMMENT retains bounded table comments"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    Expect.equal (runDefault store "ALTER TABLE t COMMENT = 'application data'") (Affected 0UL) "comment accepted"
                    Expect.equal store.Catalog.[defaultDatabase].[normalizeTableName "t"].TableComment "application data" "comment retained"

                    let atLimit = String.replicate 2048 "x"
                    Expect.equal (runDefault store (sprintf "ALTER TABLE t COMMENT = '%s'" atLimit)) (Affected 0UL) "2048 characters accepted"

                    match runDefault store (sprintf "ALTER TABLE t COMMENT = '%s'" (atLimit + "x")) with
                    | Err(1628, _) -> ()
                    | other -> failtestf "expected an oversized table comment error, got %A" other

                    match runDefault store "ALTER TABLE missing COMMENT = 'application data'" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected missing table error, got %A" other

                testCase "ALTER TABLE CONVERT TO CHARACTER SET updates text columns atomically"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'blå')" |> ignore
                    Expect.equal (runDefault store "ALTER TABLE t CONVERT TO CHARACTER SET latin1") (Affected 0UL) "converted"

                    let table = store.Catalog.[defaultDatabase].[normalizeTableName "t"]
                    Expect.equal table.TableCharset (Some "latin1") "table charset"
                    Expect.equal table.TableCollation (Some "latin1_swedish_ci") "default collation"
                    Expect.equal table.Columns.[1].Charset (Some "latin1") "column charset"
                    Expect.equal table.Columns.[1].Collation (Some "latin1_swedish_ci") "column collation"

                    match runDefault store "ALTER TABLE t CONVERT TO CHARACTER SET ascii" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected unrepresentable ASCII data to reject the ALTER, got %A" other

                    let afterFailed = store.Catalog.[defaultDatabase].[normalizeTableName "t"]
                    Expect.equal afterFailed.TableCharset (Some "latin1") "failed ALTER leaves the published table unchanged"

                    match runDefault store "SELECT name FROM t" with
                    | ResultSet(_, [ [ Some "blå" ] ]) -> ()
                    | other -> failtestf "expected failed conversion to preserve rows, got %A" other

                    match runDefault store "ALTER TABLE t CONVERT TO CHARACTER SET latin1 COLLATE utf8mb4_bin" with
                    | Err(1253, _) -> ()
                    | other -> failtestf "expected incompatible collation error, got %A" other

                testCase "CHANGE COLUMN renames and SELECT sees the new name"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (old_name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('x')" |> ignore
                    runDefault store "ALTER TABLE t CHANGE old_name new_name VARCHAR(20)" |> ignore

                    match runDefault store "SELECT new_name FROM t" with
                    | ResultSet([ "new_name" ], [ [ Some "x" ] ]) -> ()
                    | other -> failtestf "expected the renamed column, got %A" other

                testCase "ALTER TABLE ... RENAME TO makes the table reachable under the new name only"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "ALTER TABLE t RENAME TO u" |> ignore

                    match runDefault store "SELECT * FROM u" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected the renamed table to be queryable, got %A" other

                    match runDefault store "SELECT * FROM t" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected the old name to be gone, got %A" other

                testCase "RENAME TABLE a TO b"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore

                    match runDefault store "RENAME TABLE a TO b" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                    match runDefault store "SELECT * FROM b" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected b to exist, got %A" other

                testCase "CREATE INDEX / DROP INDEX round-trip without error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (a INT)" |> ignore

                    match runDefault store "CREATE INDEX idx_a ON t (a)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                    match runDefault store "DROP INDEX idx_a ON t" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                testCase "several ALTER TABLE actions comma-separated in one statement all apply"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, junk INT)" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN extra INT, DROP COLUMN junk" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "extra" ], _) -> ()
                    | other -> failtestf "expected both actions applied, got %A" other

                testCase "ALTER TABLE replaces a primary key in one statement"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, tenant_id INT NOT NULL DEFAULT 0)" |> ignore
                    runDefault store "INSERT INTO t (tenant_id) VALUES (1), (2)" |> ignore

                    Expect.equal
                        (runDefault store "ALTER TABLE t DROP PRIMARY KEY, ADD PRIMARY KEY (tenant_id, id)")
                        (Affected 0UL)
                        "primary key replaced"

                    let table = store.Catalog.[defaultDatabase].[normalizeTableName "t"]

                    Expect.equal (primaryKeyColumns table) [ "tenant_id"; "id" ] "composite primary key"

                    runDefault store "ALTER TABLE t RENAME COLUMN tenant_id TO account_id" |> ignore

                    let renamed = store.Catalog.[defaultDatabase].[normalizeTableName "t"]
                    Expect.equal (primaryKeyColumns renamed) [ "account_id"; "id" ] "renamed key column"

                    runDefault store "ALTER TABLE t DROP COLUMN account_id" |> ignore

                    let shortened = store.Catalog.[defaultDatabase].[normalizeTableName "t"]
                    Expect.equal (primaryKeyColumns shortened) [ "id" ] "dropping a key column retains the remaining key"

                    runDefault store "CREATE TABLE drop_only_key (id INT PRIMARY KEY, value INT)" |> ignore
                    runDefault store "ALTER TABLE drop_only_key DROP COLUMN id" |> ignore

                    let withoutPrimary = store.Catalog.[defaultDatabase].[normalizeTableName "drop_only_key"]
                    Expect.isEmpty (primaryKeyColumns withoutPrimary) "dropping the only key column removes the primary key"

                    runDefault store "CREATE TABLE lone_auto (id INT AUTO_INCREMENT PRIMARY KEY)" |> ignore

                    match runDefault store "ALTER TABLE lone_auto DROP PRIMARY KEY" with
                    | Err(1075, _) -> ()
                    | other -> failtestf "expected an unindexed auto-increment error, got %A" other

                testCase "ALTER TABLE adds a named unique constraint with execution options"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE document_type (technical_name VARCHAR(255))" |> ignore

                    Expect.equal
                        (runDefault store "ALTER TABLE document_type ADD CONSTRAINT `uniq.document_type.name` UNIQUE (technical_name), ALGORITHM=INSTANT")
                        (Affected 0UL)
                        "constraint added"

                    match runDefault store "INSERT INTO document_type VALUES ('invoice'), ('invoice')" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected duplicate-key error, got %A" other

                testCase "MODIFY narrowing DATETIME(6) to DATETIME(2) rounds stored values half-up"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (c DATETIME(6))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('2024-01-01 10:00:00.987654')" |> ignore
                    runDefault store "ALTER TABLE t MODIFY c DATETIME(2)" |> ignore

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet(_, [ [ Some "2024-01-01 10:00:00.99" ] ]) -> ()
                    | other -> failtestf "expected the stored value rounded to fsp 2, got %A" other

                testCase "MODIFY narrowing DATETIME(6) to DATETIME(0) rounds up into the next second"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (c DATETIME(6))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('2024-01-01 10:00:00.987654')" |> ignore
                    runDefault store "ALTER TABLE t MODIFY c DATETIME(0)" |> ignore

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet(_, [ [ Some "2024-01-01 10:00:01" ] ]) -> ()
                    | other -> failtestf "expected the stored value rounded to the next second, got %A" other

                testCase "MODIFY narrowing VARCHAR over too-long data errors 1265 and leaves the table untouched"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (c VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('hello world')" |> ignore

                    match runDefault store "ALTER TABLE t MODIFY c VARCHAR(5)" with
                    | Err(1265, "Data truncated for column 'c' at row 1") -> ()
                    | other -> failtestf "expected error 1265, got %A" other

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet(_, [ [ Some "hello world" ] ]) -> ()
                    | other -> failtestf "expected the value intact after the aborted ALTER, got %A" other

                testCase "MODIFY INT to TINYINT with an out-of-range value errors 1264 and leaves the table untouched"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (c INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (300)" |> ignore

                    match runDefault store "ALTER TABLE t MODIFY c TINYINT" with
                    | Err(1264, "Out of range value for column 'c' at row 1") -> ()
                    | other -> failtestf "expected error 1264, got %A" other

                    match runDefault store "SELECT c FROM t" with
                    | ResultSet(_, [ [ Some "300" ] ]) -> ()
                    | other -> failtestf "expected the value intact after the aborted ALTER, got %A" other

                testCase "non-strict MODIFY narrowing clamps values, but errors 1062 when the clamp collides on a UNIQUE key"
                <| fun _ ->
                    // Oracle (MySQL 8.4.11, sql_mode=''): INT 300 -> TINYINT
                    // clamps to 127; a VARCHAR narrowing that folds two
                    // unique values together fails the whole ALTER with 1062
                    // even non-strict.
                    let store = newStore ()
                    setStrictMode store false
                    runDefault store "CREATE TABLE nums (c INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (300)" |> ignore
                    runDefault store "ALTER TABLE nums MODIFY c TINYINT" |> ignore

                    match runDefault store "SELECT c FROM nums" with
                    | ResultSet(_, [ [ Some "127" ] ]) -> ()
                    | other -> failtestf "expected 300 clamped to 127 non-strict, got %A" other

                    runDefault store "CREATE TABLE u (s VARCHAR(10) UNIQUE)" |> ignore
                    runDefault store "INSERT INTO u VALUES ('abcdef1'), ('abcdef2')" |> ignore

                    match runDefault store "ALTER TABLE u MODIFY s VARCHAR(6)" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected 1062 from the colliding narrow, got %A" other

                    match runDefault store "SELECT s FROM u ORDER BY s" with
                    | ResultSet(_, [ [ Some "abcdef1" ] ; [ Some "abcdef2" ] ]) -> ()
                    | other -> failtestf "expected the table untouched after the aborted ALTER, got %A" other ]

          testList
              "INSERT ... ON DUPLICATE KEY UPDATE / INSERT IGNORE"
              [ testCase "no collision inserts a fresh row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore

                    match runDefault store "INSERT INTO t (id, n) VALUES (1, 10) ON DUPLICATE KEY UPDATE n = n + 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row inserted, got %A" other

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "10" ] ]) -> ()
                    | other -> failtestf "expected n = 10, got %A" other

                testCase "a primary key collision runs the UPDATE clause instead of erroring"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    runDefault store "INSERT INTO t (id, n) VALUES (1, 999) ON DUPLICATE KEY UPDATE n = n + 1"
                    |> ignore

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "11" ] ]) -> ()
                    | other -> failtestf "expected n incremented from the existing row, not overwritten by 999, got %A" other

                testCase "the duplicate update preserves NOT NULL and UNIQUE constraints"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT NOT NULL UNIQUE)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 1), (2, 2)" |> ignore

                    match runDefault store "INSERT INTO t VALUES (1, 3) ON DUPLICATE KEY UPDATE n = NULL" with
                    | Err(1048, _) -> ()
                    | other -> failtestf "expected 1048 for NULL, got %A" other

                    match runDefault store "INSERT INTO t VALUES (1, 3) ON DUPLICATE KEY UPDATE n = 2" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected 1062 for the UNIQUE collision, got %A" other

                    match runDefault store "SELECT id, n FROM t ORDER BY id" with
                    | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected both failed updates to leave the rows unchanged, got %A" other

                testCase "VALUES(col) inside ON DUPLICATE KEY UPDATE resolves to the incoming row's value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    runDefault store "INSERT INTO t (id, n) VALUES (1, 999) ON DUPLICATE KEY UPDATE n = VALUES(n)"
                    |> ignore

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "999" ] ]) -> ()
                    | other -> failtestf "expected n replaced with the incoming value 999, got %A" other

                testCase "a unique-index collision (not the primary key) also triggers the UPDATE clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, email VARCHAR(255) UNIQUE, hits INT)" |> ignore
                    runDefault store "INSERT INTO t (id, email, hits) VALUES (1, 'a@x.com', 1)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t (id, email, hits) VALUES (2, 'a@x.com', 1) ON DUPLICATE KEY UPDATE hits = hits + 1"
                    |> ignore

                    match runDefault store "SELECT id, hits FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected the existing row bumped, not a second row, got %A" other

                testCase "a collision on a unique index over a VIRTUAL generated column runs the UPDATE clause, not error 1062"
                <| fun _ ->
                    let store = newStore ()

                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, k VARCHAR(50), k_hash VARCHAR(64) AS (MD5(k)), UNIQUE KEY uq_hash (k_hash))"
                    |> ignore

                    runDefault store "INSERT INTO t (id, k) VALUES (1, 'x') ON DUPLICATE KEY UPDATE id = id"
                    |> ignore

                    match runDefault store "INSERT INTO t (id, k) VALUES (2, 'x') ON DUPLICATE KEY UPDATE id = id" with
                    | Affected _ -> ()
                    | other -> failtestf "expected the ODKU clause to run instead of a 1062 error, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM t" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected one row (updated, not duplicated), got %A" other

                testCase "INSERT IGNORE parses and executes like a plain INSERT"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "INSERT IGNORE INTO t VALUES (1)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other ]

          testList
              "REPLACE"
              [ testCase "inserts a new key and replaces an existing key"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore

                    Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 10)") (Affected 1UL) "new key"
                    Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 20)") (Affected 2UL) "changed replacement"
                    Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 20)") (Affected 1UL) "unchanged optimized replacement"

                    match runDefault store "SELECT id, n FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "20" ] ]) -> ()
                    | other -> failtestf "expected one replacement row, got %A" other

                testCase "retains one row identity when primary and unique keys identify it"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, email VARCHAR(100) UNIQUE, n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'alice@example.test', 10)" |> ignore

                    let rowId () =
                        match tryUniqueLookup store defaultDatabase "t" "id" (VInt 1L) with
                        | Some(_, [ rowId, _ ]) -> rowId
                        | other -> failtestf "expected one indexed row, got %A" other

                    let before = rowId ()

                    Expect.equal
                        (runDefault store "REPLACE INTO t VALUES (1, 'alice@example.test', 20)")
                        (Affected 2UL)
                        "changed replacement"

                    Expect.equal (rowId ()) before "the replacement stays in the existing row slot"

                testCase "one candidate deletes every row conflicting through separate unique keys"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, u INT UNIQUE, label VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10, 'one'), (2, 20, 'two')" |> ignore

                    Expect.equal
                        (runDefault store "REPLACE INTO t VALUES (1, 20, 'replacement')")
                        (Affected 3UL)
                        "two deletes plus one insert"

                    match runDefault store "SELECT id, u, label FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "20"; Some "replacement" ] ]) -> ()
                    | other -> failtestf "expected both conflicting rows replaced by one candidate, got %A" other

                testCase "later candidates observe rows inserted earlier in the same statement"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore

                    Expect.equal
                        (runDefault store "REPLACE INTO t VALUES (1, 10), (1, 20)")
                        (Affected 3UL)
                        "insert followed by delete and insert"

                    match runDefault store "SELECT n FROM t" with
                    | ResultSet(_, [ [ Some "20" ] ]) -> ()
                    | other -> failtestf "expected the last candidate, got %A" other

                testCase "omitted columns use defaults and a unique collision generates a fresh auto id"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, u INT UNIQUE, n INT DEFAULT 7)" |> ignore
                    runDefault store "INSERT INTO t (u, n) VALUES (1, 99)" |> ignore

                    Expect.equal (runDefault store "REPLACE INTO t (u) VALUES (1)") (Affected 2UL) "delete and insert"

                    match runDefault store "SELECT id, u, n FROM t" with
                    | ResultSet(_, [ [ Some "2"; Some "1"; Some "7" ] ]) -> ()
                    | other -> failtestf "expected a fresh id and defaulted n, got %A" other

                testCase "REPLACE ... SELECT applies candidates in source order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (id INT, n INT)" |> ignore
                    runDefault store "CREATE TABLE dst (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10), (1, 20), (2, 30)" |> ignore

                    Expect.equal
                        (runDefault store "REPLACE INTO dst SELECT id, n FROM src ORDER BY n")
                        (Affected 4UL)
                        "two inserts and one replacement"

                    match runDefault store "SELECT id, n FROM dst ORDER BY id" with
                    | ResultSet(_, [ [ Some "1"; Some "20" ]; [ Some "2"; Some "30" ] ]) -> ()
                    | other -> failtestf "expected last source duplicate to win, got %A" other

                testCase "REPLACE ... SET resolves column references and DEFAULT() from column defaults"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, u INT UNIQUE, n INT DEFAULT 7)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10, 1)" |> ignore

                    Expect.equal
                        (runDefault store "REPLACE INTO t SET id = 1, u = 10, n = n + 1")
                        (Affected 2UL)
                        "bare n reads its default value"

                    Expect.equal
                        (runDefault store "REPLACE INTO t SET id = 2, u = 20, n = DEFAULT(n)")
                        (Affected 1UL)
                        "DEFAULT(n) supplies the declared default"

                    match runDefault store "SELECT id, u, n FROM t ORDER BY id" with
                    | ResultSet(_, [ [ Some "1"; Some "10"; Some "8" ]; [ Some "2"; Some "20"; Some "7" ] ]) -> ()
                    | other -> failtestf "expected default-derived SET values, got %A" other

                testCase "DEFAULT returns a column default in ordinary expressions"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE defaults_t (a INT DEFAULT 7, b INT, c INT NOT NULL)" |> ignore
                    runDefault store "INSERT INTO defaults_t (c) VALUES (1)" |> ignore

                    match runDefault store "SELECT DEFAULT(a), DEFAULT(b) FROM defaults_t" with
                    | ResultSet(_, [ [ Some "7"; None ] ]) -> ()
                    | other -> failtestf "expected declared and implicit NULL defaults, got %A" other

                    match runDefault store "SELECT DEFAULT(c) FROM defaults_t" with
                    | Err(1364, "Field 'c' doesn't have a default value") -> ()
                    | other -> failtestf "expected 1364 for a missing default, got %A" other

                testCase "COERCIBILITY classifies explicit, column, literal, numeric, and NULL expressions"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE coercibility_t (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO coercibility_t VALUES ('x')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT COERCIBILITY(name COLLATE utf8mb4_bin), COERCIBILITY(name), COERCIBILITY(USER()), COERCIBILITY('x'), COERCIBILITY(1), COERCIBILITY(NULL) FROM coercibility_t"
                    with
                    | ResultSet(_, [ [ Some "0"; Some "2"; Some "3"; Some "4"; Some "5"; Some "6" ] ]) -> ()
                    | other -> failtestf "unexpected coercibility classes: %A" other

                testCase "equal-rank collations resolve symmetrically"
                <| fun _ ->
                    let store = newStore ()

                    runDefault
                        store
                        "CREATE TABLE coercion_t (ci VARCHAR(10) COLLATE utf8mb4_0900_ai_ci, bin VARCHAR(10) COLLATE utf8mb4_bin, cs VARCHAR(10) COLLATE utf8mb4_0900_as_cs, latin VARCHAR(10) CHARACTER SET latin1 COLLATE latin1_swedish_ci)"
                    |> ignore

                    runDefault store "INSERT INTO coercion_t VALUES ('A', 'a', 'a', 'a')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT ci = bin, bin = ci, ci < bin, bin > ci, ci IN (bin), CASE ci WHEN bin THEN 1 ELSE 0 END, ci LIKE bin, ci < 'a', ci > 'a', ci <= 'a' FROM coercion_t"
                    with
                    | ResultSet(_, [ [ Some "0"; Some "0"; Some "1"; Some "1"; Some "0"; Some "0"; Some "0"; Some "0"; Some "0"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected _bin to win equal-rank comparisons, got %A" other

                    match runDefault store "SELECT latin = ci, ci = latin FROM coercion_t" with
                    | ResultSet(_, [ [ Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected the Unicode operand to win in either order, got %A" other

                    match runDefault store "SELECT ci = ('a' COLLATE utf8mb4_bin), ('a' COLLATE utf8mb4_bin) = ci FROM coercion_t" with
                    | ResultSet(_, [ [ Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected explicit COLLATE to win in either order, got %A" other

                    match
                        runDefault
                            store
                            "SELECT (ci, 1) = (bin, 1), ci IN (SELECT bin FROM coercion_t), ci = ANY (SELECT bin FROM coercion_t), bin = ANY (SELECT ci FROM coercion_t) FROM coercion_t"
                    with
                    | ResultSet(_, [ [ Some "0"; Some "0"; Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected row and subquery comparisons to share coercibility rules, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM coercion_t a JOIN coercion_t b ON a.ci = b.bin" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected hash joins to resolve both key collations, got %A" other

                testCase "outer hash joins preserve the retained side's scan order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (id INT PRIMARY KEY, ref_id INT)" |> ignore
                    runDefault store "CREATE TABLE referenced (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO parent VALUES (1, NULL), (2, 10), (3, 20)" |> ignore
                    runDefault store "INSERT INTO referenced VALUES (10), (20), (30)" |> ignore

                    match runDefault store "SELECT p.id, r.id FROM parent p LEFT JOIN referenced r ON p.ref_id = r.id" with
                    | ResultSet(_, [ [ Some "1"; None ]; [ Some "2"; Some "10" ]; [ Some "3"; Some "20" ] ]) -> ()
                    | other -> failtestf "expected left rows to retain scan order, got %A" other

                    match runDefault store "SELECT p.id, r.id FROM parent p RIGHT JOIN referenced r ON p.ref_id = r.id" with
                    | ResultSet(_, [ [ Some "2"; Some "10" ]; [ Some "3"; Some "20" ]; [ None; Some "30" ] ]) -> ()
                    | other -> failtestf "expected right rows to retain scan order, got %A" other

                testCase "incompatible equal-rank collations return 1267"
                <| fun _ ->
                    let store = newStore ()

                    runDefault
                        store
                        "CREATE TABLE incompatible_t (ci VARCHAR(10) COLLATE utf8mb4_0900_ai_ci, cs VARCHAR(10) COLLATE utf8mb4_0900_as_cs)"
                    |> ignore

                    runDefault store "INSERT INTO incompatible_t VALUES ('a', 'a')" |> ignore

                    match runDefault store "SELECT ci = cs FROM incompatible_t" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,IMPLICIT) and (utf8mb4_0900_as_cs,IMPLICIT) for operation '='") -> ()
                    | other -> failtestf "expected an incompatible comparison error, got %A" other

                    match runDefault store "SELECT ci LIKE cs FROM incompatible_t" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,IMPLICIT) and (utf8mb4_0900_as_cs,IMPLICIT) for operation 'like'") -> ()
                    | other -> failtestf "expected an incompatible LIKE error, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM incompatible_t a JOIN incompatible_t b ON a.ci = b.cs" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,IMPLICIT) and (utf8mb4_0900_as_cs,IMPLICIT) for operation '='") -> ()
                    | other -> failtestf "expected an incompatible join error, got %A" other

                    match runDefault store "SELECT ci <=> cs FROM incompatible_t" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,IMPLICIT) and (utf8mb4_0900_as_cs,IMPLICIT) for operation '<=>'") -> ()
                    | other -> failtestf "expected an incompatible null-safe comparison error, got %A" other

                    match runDefault store "SELECT ('a' COLLATE utf8mb4_0900_ai_ci) = ('a' COLLATE utf8mb4_bin)" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,EXPLICIT) and (utf8mb4_bin,EXPLICIT) for operation '='") -> ()
                    | other -> failtestf "expected conflicting explicit collations to fail, got %A" other

                testCase "SLEEP and BENCHMARK follow MySQL argument and evaluation semantics"
                <| fun _ ->
                    let store = newStore ()
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values |> List.tryHead |> Option.defaultValue VNull)

                    match run store registry "DO BENCHMARK(4, TOUCH(1))" with
                    | Affected 0UL -> Expect.equal calls 4 "BENCHMARK evaluates its body exactly count times"
                    | other -> failtestf "unexpected BENCHMARK result: %A" other

                    match run store registry "SELECT SLEEP(0), BENCHMARK(0, 1), BENCHMARK(NULL, 2), BENCHMARK(-1, 3)" with
                    | ResultSet(_, [ [ Some "0"; Some "0"; None; None ] ]) -> ()
                    | other -> failtestf "unexpected SLEEP/BENCHMARK values: %A" other

                    match run store registry "SELECT SLEEP(NULL)" with
                    | Err(1210, "Incorrect arguments to sleep.") -> ()
                    | other -> failtestf "expected invalid SLEEP arguments to return 1210, got %A" other

                testCase "delete-side foreign-key actions run before the replacement insert"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent_restrict (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE child_restrict (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parent_restrict(id))" |> ignore
                    runDefault store "INSERT INTO parent_restrict VALUES (1)" |> ignore
                    runDefault store "INSERT INTO child_restrict VALUES (1, 1)" |> ignore

                    match runDefault store "REPLACE INTO parent_restrict VALUES (1)" with
                    | Err(1451, _) -> ()
                    | other -> failtestf "expected the restricting child to reject the delete, got %A" other

                    runDefault store "CREATE TABLE parent_cascade (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE child_cascade (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parent_cascade(id) ON DELETE CASCADE)" |> ignore
                    runDefault store "INSERT INTO parent_cascade VALUES (1)" |> ignore
                    runDefault store "INSERT INTO child_cascade VALUES (1, 1)" |> ignore

                    Expect.equal (runDefault store "REPLACE INTO parent_cascade VALUES (1)") (Affected 2UL) "target rows only"

                    match runDefault store "SELECT COUNT(*) FROM child_cascade" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected the old row's child to be cascaded away, got %A" other

                testCase "a later candidate error rolls back earlier replacements"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT NOT NULL)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10)" |> ignore

                    match runDefault store "REPLACE INTO t VALUES (1, 20), (2, NULL)" with
                    | Err(1048, _) -> ()
                    | other -> failtestf "expected the invalid second candidate to fail, got %A" other

                    match runDefault store "SELECT id, n FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected statement atomicity, got %A" other ]

          testList
              "INSERT ... SELECT"
              [ testCase "inserts every row a SELECT (with WHERE/GROUP BY/aggregates) produces"
                <| fun _ ->
                    // `INSERT INTO t (cols) SELECT ...` is a Laravel
                    // reporting-job staple: roll a detail table up into a
                    // daily summary table.
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore
                    runDefault store "CREATE TABLE region_totals (region VARCHAR(10), total INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5)"
                    |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO region_totals (region, total) SELECT region, SUM(amount) FROM sales GROUP BY region ORDER BY region"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows inserted, got %A" other

                    match runDefault store "SELECT region, total FROM region_totals ORDER BY region" with
                    | ResultSet([ "region"; "total" ], rows) ->
                        Expect.equal rows [ [ Some "east"; Some "30" ]; [ Some "west"; Some "5" ] ] "one summary row per region"
                    | other -> failtestf "expected the grouped totals, got %A" other

                testCase "accepts a parenthesized SELECT source"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (id INT, value INT)" |> ignore
                    runDefault store "CREATE TABLE dst (id INT, value INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10), (2, NULL)" |> ignore

                    match runDefault store "INSERT INTO dst (SELECT id, value FROM src WHERE value IS NOT NULL)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected one inserted row, got %A" other

                    match runDefault store "SELECT id, value FROM dst" with
                    | ResultSet(_, [ [ Some "1"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected the selected row, got %A" other

                testCase "INSERT IGNORE ... SELECT skips rows that violate a unique constraint instead of failing"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (id INT)" |> ignore
                    runDefault store "CREATE TABLE dst (id INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1)" |> ignore

                    match runDefault store "INSERT IGNORE INTO dst (id) SELECT id FROM src ORDER BY id" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected only id=2 inserted (id=1 collides), got %A" other

                // MySQL 8.4.11 write probe (disposable server, 2026-08-19):
                // src = (1,10),(2,20),(1,30) into empty dst(k PK, v, n DEFAULT 0)
                // with `v = VALUES(v), n = n + 1` → 4 affected (insert=1,
                // update=2 per row; the third source row collides with the
                // first's fresh insert), final v=30 (last dup wins), n=1.
                // Second run → 6 affected (all three rows hit existing keys).
                // A run whose updates change nothing counts 0 per row.
                testCase "ON DUPLICATE KEY UPDATE on INSERT ... SELECT collapses dupes with MySQL's affected-row counts"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10), (2, 20), (1, 30)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT, n INT DEFAULT 0)" |> ignore

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, v FROM src ON DUPLICATE KEY UPDATE v = VALUES(v), n = n + 1" with
                    | Affected 4UL -> ()
                    | other -> failtestf "expected 4 affected (2 inserts + 1 in-batch dup update), got %A" other

                    match runDefault store "SELECT k, v, n FROM dst ORDER BY k" with
                    | ResultSet(_, [ [ Some "1"; Some "30"; Some "1" ]; [ Some "2"; Some "20"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected last-dup-wins v and bumped n, got %A" other

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, v FROM src ON DUPLICATE KEY UPDATE v = VALUES(v), n = n + 1" with
                    | Affected 6UL -> ()
                    | other -> failtestf "expected 6 affected (3 changing updates x 2), got %A" other

                testCase "an update that changes nothing counts 0, matching MySQL's no-op semantics"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 10)" |> ignore

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, v FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected a 0-affected no-op update, got %A" other

                testCase "VALUES(col) resolves against the select-derived candidate row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 999)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 10)" |> ignore

                    runDefault store "INSERT INTO dst (k, v) SELECT k, v * 2 FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)"
                    |> ignore

                    match runDefault store "SELECT v FROM dst WHERE k = 1" with
                    | ResultSet(_, [ [ Some "1998" ] ]) -> ()
                    | other -> failtestf "expected v replaced with the select's computed 1998, got %A" other

                testCase "ODKU reads qualified projected and unprojected source columns per input row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10, 99), (1, 10, 100), (2, 20, 200)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 1, 1)" |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO dst (k, v) SELECT k, v FROM src AS s ON DUPLICATE KEY UPDATE v = VALUES(v), extra = s.extra + s.v"
                    with
                    | Affected 5UL -> ()
                    | other -> failtestf "expected two updates and one insert, got %A" other

                    match runDefault store "SELECT k, v, extra FROM dst ORDER BY k" with
                    | ResultSet(_, [ [ Some "1"; Some "10"; Some "110" ]; [ Some "2"; Some "20"; None ] ]) -> ()
                    | other -> failtestf "expected each duplicate to see its own source row, got %A" other

                testCase "hidden ODKU source bindings do not change DISTINCT or derived-table semantics"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10, 99), (1, 10, 100)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 1, 1)" |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO dst (k, v) SELECT DISTINCT k, v FROM src AS s ON DUPLICATE KEY UPDATE extra = s.extra"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected DISTINCT to retain one source row, got %A" other

                    match runDefault store "SELECT extra FROM dst" with
                    | ResultSet(_, [ [ Some "99" ] ]) -> ()
                    | other -> failtestf "expected the first distinct source binding, got %A" other

                    runDefault store "UPDATE dst SET extra = 1" |> ignore

                    runDefault
                        store
                        "INSERT INTO dst (k, v) SELECT d.k, d.v FROM (SELECT k, v, extra + 1 AS computed_extra FROM src LIMIT 1) AS d ON DUPLICATE KEY UPDATE extra = d.computed_extra"
                    |> ignore

                    match runDefault store "SELECT extra FROM dst" with
                    | ResultSet(_, [ [ Some "100" ] ]) -> ()
                    | other -> failtestf "expected the derived expression to remain visible, got %A" other

                    runDefault store "CREATE TABLE source_extra (k INT, replacement INT)" |> ignore
                    runDefault store "INSERT INTO source_extra VALUES (1, 123)" |> ignore

                    runDefault
                        store
                        "INSERT INTO dst (k, v) SELECT s.k, s.v FROM src AS s JOIN source_extra AS x ON x.k = s.k LIMIT 1 ON DUPLICATE KEY UPDATE extra = x.replacement"
                    |> ignore

                    match runDefault store "SELECT extra FROM dst" with
                    | ResultSet(_, [ [ Some "123" ] ]) -> ()
                    | other -> failtestf "expected the joined source column to remain visible, got %A" other

                    runDefault store "UPDATE dst SET extra = 1" |> ignore

                    runDefault
                        store
                        "INSERT INTO dst (k, v) SELECT k, v FROM src AS s LIMIT 1 ON DUPLICATE KEY UPDATE extra = (SELECT s.extra)"
                    |> ignore

                    match runDefault store "SELECT extra FROM dst" with
                    | ResultSet(_, [ [ Some "99" ] ]) -> ()
                    | other -> failtestf "expected the correlated subquery to see the source row, got %A" other

                testCase "ODKU source references preserve MySQL ambiguity and grouping errors"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (k INT, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1, 10, 99)" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, v INT, extra INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 1, 1)" |> ignore

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, v FROM src AS s ON DUPLICATE KEY UPDATE extra = v" with
                    | Err(1052, _) -> ()
                    | other -> failtestf "expected ambiguous source/target column error 1052, got %A" other

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, v AS incoming FROM src AS s ON DUPLICATE KEY UPDATE extra = incoming" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected projection aliases to remain unavailable, got %A" other

                    match runDefault store "INSERT INTO dst (k, v) SELECT k, MAX(v) FROM src AS s GROUP BY k ON DUPLICATE KEY UPDATE extra = s.extra" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected grouped source references to fail with 1054, got %A" other

                testCase "ODKU source bindings retain collation and ENUM metadata"
                <| fun _ ->
                    let store = newStore ()

                    runDefault
                        store
                        "CREATE TABLE src (k INT, b VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, e ENUM('red', 'blue'))"
                    |> ignore

                    runDefault store "INSERT INTO src VALUES (1, 'a', 'red')" |> ignore
                    runDefault store "CREATE TABLE dst (k INT PRIMARY KEY, comparison INT)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1, 9)" |> ignore

                    runDefault
                        store
                        "INSERT INTO dst (k, comparison) SELECT k, 0 FROM src AS s ON DUPLICATE KEY UPDATE comparison = (s.b = 'A') + (s.e = 1) * 2"
                    |> ignore

                    match runDefault store "SELECT comparison FROM dst" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected binary comparison 0 plus ENUM ordinal comparison 2, got %A" other ]

          testList
              "CAST and new column types"
              [ testCase "CAST(x AS UNSIGNED)/CAST(x AS SIGNED) coerce to an integer"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST('42' AS UNSIGNED), CAST(3.9 AS SIGNED)" with
                    | ResultSet(_, [ [ Some "42"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected 42/4, got %A" other

                testCase "CAST(x AS CHAR) stringifies"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST(42 AS CHAR)" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected '42', got %A" other

                testCase "DECIMAL precision and scale are validated before coercion"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST(1 AS DECIMAL(10,10000))" with
                    | Err(1425, _) -> ()
                    | other -> failtestf "expected 1425 for the CAST scale, got %A" other

                    match runDefault store "CREATE TABLE t (n DECIMAL(10,10000))" with
                    | Err(1425, _) -> ()
                    | other -> failtestf "expected 1425 for the column scale, got %A" other

                testCase "CAST never raises 1366, even under the session's default strict sql_mode"
                <| fun _ ->
                    // Oracle (real MySQL 8, default sql_mode): CAST('abc' AS
                    // SIGNED) = 0, CAST('12abc' AS SIGNED) = 12 (the leading
                    // numeric run, not the non-strict-fallback 0),
                    // CAST('abc' AS DATE) = NULL — all three with only a
                    // truncation *warning*, never error 1366.
                    let store = newStore ()

                    match runDefault store "SELECT CAST('abc' AS SIGNED), CAST('12abc' AS SIGNED), CAST('abc' AS DATE)" with
                    | ResultSet(_, [ [ Some "0"; Some "12"; None ] ]) -> ()
                    | other -> failtestf "expected 0, 12, NULL, got %A" other

                testCase "CAST(x AS SIGNED) stops at the exponent, unlike a DECIMAL/float target"
                <| fun _ ->
                    // Oracle: CAST('1e3' AS SIGNED) = 1 (string-to-integer
                    // conversion stops at the first non-digit); CAST('1e3' AS
                    // DECIMAL(10,2)) = 1000.00 (the float grammar applies,
                    // then the result is padded to the target's declared scale).
                    let store = newStore ()

                    match runDefault store "SELECT CAST('1e3' AS SIGNED), CAST('1e3' AS DECIMAL(10,2))" with
                    | ResultSet(_, [ [ Some "1"; Some "1000.00" ] ]) -> ()
                    | other -> failtestf "expected 1, 1000.00, got %A" other

                testCase "ENUM column accepts a listed value and rejects one outside the set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open', 'closed'))" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('open')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the valid enum value to insert, got %A" other

                    match runDefault store "INSERT INTO t VALUES ('bogus')" with
                    | Err(1265, _) -> ()
                    | other -> failtestf "expected MySQL's 1265 Data-truncated error for an unlisted enum value, got %A" other

                testCase "ENUM stores the declared spelling, and accepts integer and quoted-number indices like MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open', 'closed'))" |> ignore

                    // Case-insensitive label match, canonicalized to the
                    // declared spelling on read (MySQL stores the index).
                    runDefault store "INSERT INTO t VALUES ('OPEN')" |> ignore

                    match runDefault store "SELECT status FROM t" with
                    | ResultSet(_, [ [ Some "open" ] ]) -> ()
                    | other -> failtestf "expected 'OPEN' to read back as the declared 'open', got %A" other

                    // Integer index (1-based) into the declared list.
                    runDefault store "INSERT INTO t VALUES (2)" |> ignore

                    // A quoted number with no matching label is still an index.
                    runDefault store "INSERT INTO t VALUES ('2')" |> ignore

                    match runDefault store "SELECT status FROM t" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "open" ]; [ Some "closed" ]; [ Some "closed" ] ] "index insertions land on the declared values"
                    | other -> failtestf "expected index-based insertions, got %A" other

                    // Out-of-range indices are rejected, like an invalid label.
                    match runDefault store "INSERT INTO t VALUES (0)" with
                    | Err(1265, _) -> ()
                    | other -> failtestf "expected 1265 for out-of-range index 0, got %A" other

                testCase "ENUM comparisons in WHERE/IN use declaration ordinals against numbers, strings against labels"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open', 'closed'))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('open'), ('closed'), ('open')" |> ignore

                    let where (cond: string) =
                        match runDefault store $"SELECT COUNT(*) FROM t WHERE {cond}" with
                        | ResultSet(_, [ [ Some n ] ]) -> n
                        | other -> failtestf "expected a count for WHERE %s, got %A" cond other

                    // label comparison is the plain case-insensitive string match
                    Expect.equal (where "status = 'open'") "2" "label match"
                    Expect.equal (where "status = 'OPEN'") "2" "label match is case-insensitive"
                    // numbers — bare or quoted — compare by declaration ordinal
                    Expect.equal (where "status = 1") "2" "ordinal 1 matches the first declared value"
                    Expect.equal (where "status = '1'") "2" "a quoted number is still an ordinal"
                    Expect.equal (where "status = 2") "1" "ordinal 2 matches the second declared value"
                    Expect.equal (where "status > 1") "1" "ordinal ordering drives numeric comparisons"
                    Expect.equal (where "status = 9") "0" "an out-of-range ordinal matches nothing"
                    Expect.equal (where "status IN (1, 2)") "3" "IN (numbers) matches by ordinal"
                    Expect.equal (where "status IN ('open')") "2" "IN (labels) matches by string"

                testCase "ENUM in numeric context reads its declaration ordinal (arithmetic, CAST, numeric aggregates)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open','active','done','blocked'))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('open'),('active'),('done'),('blocked'),('open')" |> ignore

                    let scalar (sql: string) =
                        match runDefault store $"SELECT {sql} FROM t LIMIT 1" with
                        | ResultSet(_, [ [ Some v ] ]) -> v
                        | other -> failtestf "expected one value for %s, got %A" sql other

                    // MySQL 8.4-verified: `status + 0` is the ordinal, not 0.
                    Expect.equal (scalar "status + 0") "1" "arithmetic sees the ordinal"
                    Expect.equal (scalar "status * 1") "1" "multiplication sees the ordinal"
                    Expect.equal (scalar "CAST(status AS SIGNED)") "1" "CAST AS SIGNED sees the ordinal"
                    Expect.equal (scalar "CAST(status AS UNSIGNED)") "1" "CAST AS UNSIGNED sees the ordinal"
                    Expect.equal (scalar "CAST(status AS CHAR)") "open" "CAST AS CHAR keeps the label"

                    match runDefault store "SELECT SUM(status), MAX(status), MIN(status), GROUP_CONCAT(status) FROM t" with
                    | ResultSet(_, [ [ Some total; Some biggest; Some smallest; Some joined ] ]) ->
                        Expect.equal total "11" "SUM folds ordinals"
                        Expect.equal biggest "open" "MAX still returns a label"
                        Expect.equal smallest "active" "MIN still returns a label"
                        Expect.equal joined "open,active,done,blocked,open" "GROUP_CONCAT still joins labels"
                    | other -> failtestf "expected one aggregate row, got %A" other

                testCase "WITH ROLLUP sorts an ENUM group key lexically, not by declaration ordinal"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open','active','done','blocked'))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('open'),('active'),('done'),('blocked'),('open')" |> ignore

                    let statuses (sql: string) =
                        match runDefault store sql with
                        | ResultSet(_, rows) -> rows |> List.map List.head
                        | other -> failtestf "expected rows for %s, got %A" sql other

                    // Without ROLLUP the key keeps its ENUM type and sorts by
                    // ordinal; ROLLUP materializes it into a nullable
                    // temporary that loses the type, so MySQL sorts it as text.
                    Expect.equal
                        (statuses "SELECT status FROM t GROUP BY status ORDER BY status")
                        [ Some "open"; Some "active"; Some "done"; Some "blocked" ]
                        "plain GROUP BY sorts by declaration ordinal"

                    Expect.equal
                        (statuses "SELECT status FROM t GROUP BY status WITH ROLLUP ORDER BY GROUPING(status), status")
                        [ Some "active"; Some "blocked"; Some "done"; Some "open"; None ]
                        "ROLLUP sorts the group key lexically, super-aggregate row last"

                    // No ORDER BY at all: ROLLUP keeps the grouping order.
                    Expect.equal
                        (statuses "SELECT status FROM t GROUP BY status WITH ROLLUP")
                        [ Some "open"; Some "active"; Some "done"; Some "blocked"; None ]
                        "unordered ROLLUP keeps the ordinal grouping order"

                testCase "hexadecimal literals preserve arbitrary bytes in VARBINARY and BLOB columns"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (binary_value VARBINARY(8), blob_value BLOB)" |> ignore

                    match runDefault store "INSERT INTO t VALUES (X'00ff80', x'deadbeef')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the binary row to insert, got %A" other

                    match runDefault store "SELECT HEX(binary_value), HEX(blob_value) FROM t" with
                    | ResultSet(_, [ [ Some "00FF80"; Some "DEADBEEF" ] ]) -> ()
                    | other -> failtestf "expected exact binary values, got %A" other

                    match scan store defaultDatabase "t" with
                    | Ok(_, rows) ->
                        match List.ofSeq rows with
                        | [ [| VBytes binary; VBytes blob |] ] ->
                            Expect.equal binary [| 0x00uy; 0xffuy; 0x80uy |] "VARBINARY stored raw bytes"
                            Expect.equal blob [| 0xdeuy; 0xaduy; 0xbeuy; 0xefuy |] "BLOB stored raw bytes"
                        | other -> failtestf "expected stored VBytes values, got %A" other
                    | Error error -> failtestf "expected the binary table to scan, got %A" error

                testCase "UNHEX(MD5(...)) stored into a BINARY column round-trips through LENGTH/HEX"
                <| fun _ ->
                    // UNHEX must surface the exact decoded bytes — if it
                    // returned a UTF-8 VString, the BINARY column coercion
                    // would re-encode it and double most bytes above 0x7F.
                    let store = newStore ()
                    runDefault store "CREATE TABLE bt (h BINARY(16))" |> ignore

                    match runDefault store "INSERT INTO bt VALUES (UNHEX(MD5('k')))" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the row to insert, got %A" other

                    match runDefault store "SELECT LENGTH(h), HEX(h) FROM bt" with
                    | ResultSet(_, [ [ Some "16"; Some "8CE4B16B22B58894AA86C421E8759DF3" ] ]) -> ()
                    | other -> failtestf "expected a 16-byte round-tripped hash, got %A" other

                testCase "SET column accepts any string without validating against the declared set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (flags SET('a', 'b'))" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('a,b')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected SET to accept any string, got %A" other

                testCase "the full new column-type surface creates and round-trips through INSERT/SELECT"
                <| fun _ ->
                    let store = newStore ()

                    runDefault
                        store
                        "CREATE TABLE t (a CHAR(3), b TINYTEXT, c BLOB, d SMALLINT, e MEDIUMINT UNSIGNED, f TIME, g YEAR, h FLOAT, i BOOLEAN)"
                    |> ignore

                    match runDefault store "INSERT INTO t VALUES ('x', 'y', 'z', 1, 2, '10:00:00', 2024, 1.5, 1)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the row to insert, got %A" other

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet(_, [ row ]) -> Expect.equal (List.length row) 9 "every column round-trips"
                    | other -> failtestf "expected one row back, got %A" other ]

          testList
              "CREATE/DROP DATABASE and db.table-qualified names"
              [ testCase "CREATE DATABASE then a qualified CREATE TABLE/INSERT/SELECT round-trip"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "CREATE DATABASE app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected CREATE DATABASE to succeed, got %A" other

                    match runDefault store "CREATE DATABASE app" with
                    | Err(1007, _) -> ()
                    | other -> failtestf "expected 1007 for a duplicate CREATE DATABASE, got %A" other

                    runDefault store "CREATE TABLE app.widgets (id INT, name VARCHAR(20))" |> ignore

                    match runDefault store "INSERT INTO app.widgets VALUES (1, 'cog')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified INSERT to affect one row, got %A" other

                    match runDefault store "SELECT name FROM app.widgets WHERE id = 1" with
                    | ResultSet(_, [ [ Some "cog" ] ]) -> ()
                    | other -> failtestf "expected the qualified SELECT to find the row, got %A" other

                    match runDefault store "UPDATE app.widgets SET name = 'gear' WHERE id = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified UPDATE to affect one row, got %A" other

                    match runDefault store "DELETE FROM app.widgets WHERE id = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified DELETE to affect one row, got %A" other

                testCase "DROP DATABASE removes it; IF EXISTS/IF NOT EXISTS are no-ops"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE DATABASE IF NOT EXISTS app" |> ignore

                    match runDefault store "DROP DATABASE app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP DATABASE to succeed, got %A" other

                    match runDefault store "DROP DATABASE app" with
                    | Err(1049, _) -> ()
                    | other -> failtestf "expected 1049 for a missing DROP DATABASE, got %A" other

                    match runDefault store "DROP DATABASE IF EXISTS app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP DATABASE IF EXISTS to be a no-op, got %A" other

                testCase "CREATE TABLE IF NOT EXISTS / DROP TABLE IF EXISTS / DROP INDEX IF EXISTS are no-ops when absent"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "CREATE TABLE IF NOT EXISTS t (a INT)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected CREATE TABLE IF NOT EXISTS to succeed, got %A" other

                    // a second create with IF NOT EXISTS is a silent no-op.
                    runDefault store "CREATE TABLE t (a INT)" |> ignore

                    match runDefault store "CREATE TABLE IF NOT EXISTS t (a INT)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected CREATE TABLE IF NOT EXISTS to no-op on an existing table, got %A" other

                    match runDefault store "DROP TABLE IF EXISTS missing" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP TABLE IF EXISTS to no-op for a missing table, got %A" other

                    // DROP INDEX on an existing table with a missing index is
                    // a no-op whether or not IF EXISTS is given (MySQL 1091
                    // suppressed by IF EXISTS; fsdb's ALTER drops to the same
                    // silent no-op).
                    match runDefault store "DROP INDEX no_such_idx ON t" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP INDEX on a missing index to no-op, got %A" other

                    match runDefault store "DROP INDEX IF EXISTS no_such_idx ON t" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP INDEX IF EXISTS to no-op for a missing index, got %A" other

                    // a missing table still errors even under IF EXISTS,
                    // matching MySQL.
                    match runDefault store "DROP INDEX IF EXISTS no_such_idx ON missing_table" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected 1146 for DROP INDEX IF EXISTS on a missing table, got %A" other

                testCase "CREATE TABLE AS SELECT infers columns and inserts atomically"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE source (id INT NOT NULL, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO source VALUES (1, 'one'), (2, 'two')" |> ignore

                    match runDefault store "CREATE TABLE copy AS SELECT id, name, id + 1 AS next_id FROM source ORDER BY id" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected two copied rows, got %A" other

                    match runDefault store "SELECT id, name, next_id FROM copy ORDER BY id" with
                    | ResultSet(_, [ [ Some "1"; Some "one"; Some "2" ]; [ Some "2"; Some "two"; Some "3" ] ]) -> ()
                    | other -> failtestf "expected copied query rows, got %A" other

                    match scan store defaultDatabase "copy" with
                    | Ok(columns, _) ->
                        Expect.equal (columns |> List.map _.Type) [ TInt false; TVarchar 20; TBigInt false ] "inferred types"
                        Expect.equal (columns |> List.map _.Nullable) [ false; true; false ] "inferred nullability"
                    | Error error -> failtestf "expected created table, got %A" error

                    match runDefault store "CREATE TABLE broken AS SELECT * FROM missing" with
                    | Err(1146, _) -> Expect.isError (scan store defaultDatabase "broken") "failed query leaves no table"
                    | other -> failtestf "expected source error, got %A" other

                    match runDefault store "CREATE TABLE IF NOT EXISTS copy AS SELECT * FROM missing" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected existing destination to skip the query, got %A" other

                testCase "CREATE TABLE LIKE copies the definition without rows, foreign keys, or the auto-increment counter"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (id INT PRIMARY KEY)" |> ignore

                    runDefault
                        store
                        "CREATE TABLE source (id INT AUTO_INCREMENT PRIMARY KEY, u INT UNIQUE, n INT DEFAULT 7, g INT AS (n + 1), CHECK (n > 0), KEY ix_n (n), FOREIGN KEY (n) REFERENCES parent(id)) AUTO_INCREMENT=40"
                    |> ignore

                    runDefault store "INSERT INTO parent VALUES (7)" |> ignore
                    runDefault store "INSERT INTO source (u, n) VALUES (1, 7)" |> ignore

                    Expect.equal (runDefault store "CREATE TABLE clone (LIKE source)") (Affected 0UL) "table created"

                    match scan store defaultDatabase "clone" with
                    | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "rows are not copied"
                    | Error error -> failtestf "expected cloned table, got %A" error

                    let clone = store.Catalog.[defaultDatabase].[normalizeTableName "clone"]
                    Expect.isEmpty clone.ForeignKeys "foreign keys are not copied"
                    Expect.equal (clone.Indexes |> List.map _.Name |> Set.ofList) (Set.ofList [ "PRIMARY"; "u"; "ix_n" ]) "indexes copied"

                    match runDefault store "INSERT INTO clone (u, n) VALUES (2, 5)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected cloned table insert, got %A" other

                    match runDefault store "SELECT id, u, n, g FROM clone" with
                    | ResultSet(_, [ [ Some "1"; Some "2"; Some "5"; Some "6" ] ]) -> ()
                    | other -> failtestf "expected reset auto increment and generated value, got %A" other

                    match runDefault store "INSERT INTO clone (u, n) VALUES (3, 0)" with
                    | Err(3819, _) -> ()
                    | other -> failtestf "expected copied CHECK constraint, got %A" other ]

          testList
              "EXISTS (subquery)"
              [ testCase "EXISTS is true when the subquery has rows, false when it doesn't"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "SELECT EXISTS (SELECT 1 FROM t WHERE id = 1) AS `exists`" with
                    | ResultSet([ "exists" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected exists=1, got %A" other

                    match runDefault store "SELECT EXISTS (SELECT 1 FROM t WHERE id = 2) AS `exists`" with
                    | ResultSet([ "exists" ], [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected exists=0, got %A" other

                testCase "EXISTS reaches information_schema, the shape Laravel's hasTable() uses"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE widgets (id INT)" |> ignore

                    let sql =
                        "select exists (select 1 from information_schema.tables where table_schema = 'fsdb' "
                        + "and table_name = 'widgets' and table_type in ('BASE TABLE', 'SYSTEM VERSIONED')) as `exists`"

                    match runDefault store sql with
                    | ResultSet([ "exists" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected exists=1, got %A" other ]

          testList
              "ungrouped aggregates (COUNT/SUM/AVG/MIN/MAX, no GROUP BY)"
              [ testCase "MAX(batch) is what Laravel's migration repository runs to pick the next batch number"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE migrations (id INT, batch INT)" |> ignore
                    runDefault store "INSERT INTO migrations VALUES (1, 1), (2, 1), (3, 2)" |> ignore

                    match runDefault store "SELECT MAX(batch) AS aggregate FROM migrations" with
                    | ResultSet([ "aggregate" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected aggregate=2, got %A" other

                testCase "COUNT(*)/SUM/AVG/MIN over an empty table and a NULL-containing one"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c, SUM(n) AS s, MIN(n) AS mn FROM t" with
                    | ResultSet([ "c"; "s"; "mn" ], [ [ Some "0"; None; None ] ]) -> ()
                    | other -> failtestf "expected count=0 and sum/min NULL on an empty table, got %A" other

                    runDefault store "INSERT INTO t VALUES (10), (NULL), (20)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c, COUNT(n) AS cn, SUM(n) AS s, AVG(n) AS a FROM t" with
                    | ResultSet([ "c"; "cn"; "s"; "a" ], [ [ Some "3"; Some "2"; Some "30"; Some "15.0000" ] ]) -> ()
                    | other -> failtestf "expected NULLs to drop out of COUNT(n)/SUM/AVG, got %A" other

                testCase "empty bit aggregates return their MySQL identities"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n BIGINT)" |> ignore

                    match runDefault store "SELECT BIT_AND(n), BIT_OR(n), BIT_XOR(n) FROM t" with
                    | ResultSet(_, [ [ Some "18446744073709551615"; Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected the all-ones/all-zero bit identities, got %A" other

                testCase "an aggregate nested inside an expression is detected and evaluated, not just a bare top-level call"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT COUNT(*) + 1 AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected COUNT(*) + 1 = 3, got %A" other

                    match runDefault store "SELECT ROUND(AVG(n), 1) AS a FROM t" with
                    | ResultSet([ "a" ], [ [ Some "15.0" ] ]) -> ()
                    | other -> failtestf "expected a scalar function wrapping an aggregate to work, got %A" other ]

          testList
              "table-qualified columns are checked against the FROM's alias-or-table, not silently accepted from anywhere"
              [ testCase "a qualifier that doesn't match the table in scope is a 1054 unknown-column error, not a resultset"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT p.id FROM u" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected a 1054 unknown-column error, got %A" other

                testCase "a qualifier matching the table's alias resolves correctly"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT x.id FROM u AS x" with
                    | ResultSet([ "id" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected the alias-qualified column to resolve, got %A" other

                testCase "a qualifier matching the bare table name still resolves once it's aliased away"
                <| fun _ ->
                    // Once a table has an alias, real MySQL only accepts the
                    // alias as the qualifier, not the original table name —
                    // `u.id` must be unknown the same way `p.id` is.
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT u.id FROM u AS x" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected the pre-alias table name to not resolve, got %A" other ]

          testList
              "SELECT DISTINCT"
              [ testCase "SELECT DISTINCT dedupes on the projected columns, preserving first-occurrence order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES ('bob'), ('alice'), ('bob'), ('alice'), ('carol')" |> ignore

                    match runDefault store "SELECT DISTINCT name FROM u ORDER BY name" with
                    | ResultSet([ "name" ], rows) ->
                        Expect.equal rows [ [ Some "alice" ]; [ Some "bob" ]; [ Some "carol" ] ] "three distinct names, sorted"
                    | other -> failtestf "expected a deduped resultset, got %A" other

                testCase "SELECT DISTINCT applies LIMIT to the deduped set, not the raw row count"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES ('a'), ('a'), ('a'), ('b')" |> ignore

                    match runDefault store "SELECT DISTINCT name FROM u ORDER BY name LIMIT 1" with
                    | ResultSet([ "name" ], [ [ Some "a" ] ]) -> ()
                    | other -> failtestf "expected LIMIT 1 to keep only the first distinct row, got %A" other ]

          ExecutorJoinTests.tests

          testList
              "real GROUP BY / HAVING / grouped aggregates"
              [ testCase "GROUP BY groups rows and aggregates per group"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('west', 15), ('west', 25)"
                    |> ignore

                    match runDefault store "SELECT region, SUM(amount) AS total, COUNT(*) AS n FROM sales GROUP BY region ORDER BY region" with
                    | ResultSet([ "region"; "total"; "n" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "east"; Some "30"; Some "2" ]; [ Some "west"; Some "45"; Some "3" ] ]
                            "one row per region, aggregated over just that region's rows"
                    | other -> failtestf "expected two grouped rows, got %A" other

                testCase "HAVING filters grouped rows, including referencing an aggregate not in the SELECT list"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('south', 100)"
                    |> ignore

                    match runDefault store "SELECT region FROM sales GROUP BY region HAVING COUNT(*) > 1 ORDER BY region" with
                    | ResultSet([ "region" ], rows) -> Expect.equal rows [ [ Some "east" ] ] "only east has more than one row"
                    | other -> failtestf "expected only 'east' to survive HAVING, got %A" other

                testCase "HAVING with no GROUP BY and no aggregate filters per row, not the whole table down to one"
                <| fun _ ->
                    // HAVING alone must not route to the grouped path, which
                    // collapses the ungrouped table to one row before the
                    // filter runs.
                    let store = newStore ()
                    runDefault store "CREATE TABLE hv (id INT, v INT)" |> ignore
                    runDefault store "INSERT INTO hv VALUES (1, 1), (2, 6), (3, 10)" |> ignore

                    match runDefault store "SELECT id FROM hv HAVING v > 5 ORDER BY id" with
                    | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "3" ] ] "filters per-row like WHERE"
                    | other -> failtestf "expected HAVING without GROUP BY to filter per row, got %A" other

                testCase "HAVING can reference a SELECT list alias, not just a bare aggregate call"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('south', 100)"
                    |> ignore

                    match runDefault store "SELECT region, COUNT(*) AS c FROM sales GROUP BY region HAVING c > 1 ORDER BY region" with
                    | ResultSet([ "region"; "c" ], rows) -> Expect.equal rows [ [ Some "east"; Some "2" ] ] "only east has c > 1"
                    | other -> failtestf "expected the alias 'c' to resolve inside HAVING, got %A" other

                testCase "GROUP BY with a NULL-valued key groups every NULL together, same as MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (NULL, 1), (NULL, 2), ('a', 3)" |> ignore

                    match runDefault store "SELECT grp, COUNT(*) AS c FROM t GROUP BY grp ORDER BY grp" with
                    | ResultSet([ "grp"; "c" ], rows) ->
                        Expect.equal rows [ [ None; Some "2" ]; [ Some "a"; Some "1" ] ] "both NULL rows land in one group"
                    | other -> failtestf "expected NULLs to group together, got %A" other

                testCase "COUNT ignores NULL, SUM of an all-NULL group is NULL, AVG propagates NULL the same way"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', NULL), ('a', NULL), ('b', 10), ('b', NULL)" |> ignore

                    match runDefault store "SELECT grp, COUNT(n) AS cn, SUM(n) AS s, AVG(n) AS a FROM t GROUP BY grp ORDER BY grp" with
                    | ResultSet([ "grp"; "cn"; "s"; "a" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "0"; None; None ]; [ Some "b"; Some "1"; Some "10"; Some "10.0000" ] ]
                            "group 'a' is all-NULL (COUNT 0, SUM/AVG NULL), group 'b' has one real value"
                    | other -> failtestf "expected NULL-aware aggregates per group, got %A" other

                testCase "custom aggregates may replace an optimized built-in"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    let registry = builtins |> registerAggregate "SUM" (fun _ -> VInt 42L)

                    match run store registry "SELECT SUM(n) FROM t" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected the custom SUM implementation, got %A" other

                testCase "a bare non-aggregated column picks the first row of its group (ANY_VALUE-style, ONLY_FULL_GROUP_BY off)"
                <| fun _ ->
                    let store = newStore ()
                    Fsdb.Storage.setOnlyFullGroupBy store false
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 'first'), ('a', 'second')" |> ignore

                    match runDefault store "SELECT grp, tag FROM t GROUP BY grp" with
                    | ResultSet([ "grp"; "tag" ], [ [ Some "a"; Some "first" ] ]) -> ()
                    | other -> failtestf "expected the first row's tag, got %A" other

                testCase "GROUP BY a positional projection reference and an alias"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('a', 2), ('b', 3)" |> ignore

                    match runDefault store "SELECT grp AS g, COUNT(*) AS c FROM t GROUP BY 1 ORDER BY g" with
                    | ResultSet([ "g"; "c" ], rows) -> Expect.equal rows [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ] "GROUP BY 1"
                    | other -> failtestf "expected GROUP BY 1 to group by the first projection, got %A" other

                    match runDefault store "SELECT grp AS g, COUNT(*) AS c FROM t GROUP BY g ORDER BY g" with
                    | ResultSet([ "g"; "c" ], rows) -> Expect.equal rows [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ] "GROUP BY alias"
                    | other -> failtestf "expected GROUP BY alias to group by the aliased projection, got %A" other

                testCase "GROUP BY with no ORDER BY keeps first-occurrence order, matching MySQL's unindexed-key default"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (code VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('GB'), ('NO'), ('DE'), ('NL'), ('GB')" |> ignore

                    match runDefault store "SELECT code, COUNT(*) AS c FROM t GROUP BY code" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "GB"; Some "2" ]; [ Some "NO"; Some "1" ]; [ Some "DE"; Some "1" ]; [ Some "NL"; Some "1" ] ]
                            "grouped rows come back in first-occurrence order, not sorted by code"
                    | other -> failtestf "expected first-occurrence-order groups, got %A" other

                    match runDefault store "SELECT code, COUNT(*) AS c FROM t GROUP BY code LIMIT 2" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal rows [ [ Some "GB"; Some "2" ]; [ Some "NO"; Some "1" ] ] "LIMIT after grouping takes the first-occurrence groups"
                    | other -> failtestf "expected first-occurrence-order groups under LIMIT, got %A" other

                testCase "GROUP BY with no ORDER BY sorts by the group key when a leftmost index covers it, matching MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (code VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('GB'), ('NO'), ('DE'), ('NL'), ('GB')" |> ignore
                    runDefault store "CREATE INDEX idx_code ON t (code)" |> ignore

                    match runDefault store "SELECT code, COUNT(*) AS c FROM t GROUP BY code" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "DE"; Some "1" ]; [ Some "GB"; Some "2" ]; [ Some "NL"; Some "1" ]; [ Some "NO"; Some "1" ] ]
                            "an index leading with the group column sorts, unlike the unindexed case above"
                    | other -> failtestf "expected code-sorted groups, got %A" other

                testCase "GROUP BY sorts through a composite index once WHERE pins every column ahead of the group key"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (is_published INT, code VARCHAR(10), city VARCHAR(10))" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES (1, 'GB', 'x'), (1, 'NO', 'x'), (1, 'DE', 'x'), (1, 'NL', 'x'), (0, 'ZZ', 'x')"
                    |> ignore

                    runDefault store "CREATE INDEX idx_pub_code_city ON t (is_published, code, city)" |> ignore

                    // Without the WHERE pinning `is_published`, the group
                    // column isn't a leading run of the index by itself, so
                    // this stays unsorted (first-occurrence).
                    match runDefault store "SELECT code, COUNT(*) AS c FROM t GROUP BY code" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "GB"; Some "1" ]; [ Some "NO"; Some "1" ]; [ Some "DE"; Some "1" ]; [ Some "NL"; Some "1" ]; [ Some "ZZ"; Some "1" ] ]
                            "no WHERE pin means the composite index doesn't apply, so groups stay unsorted"
                    | other -> failtestf "expected unsorted groups, got %A" other

                    // With the equality WHERE, `is_published` is pinned and
                    // `code` becomes the index's next column — sorted.
                    match runDefault store "SELECT code, COUNT(*) AS c FROM t WHERE is_published = 1 GROUP BY code" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "DE"; Some "1" ]; [ Some "GB"; Some "1" ]; [ Some "NL"; Some "1" ]; [ Some "NO"; Some "1" ] ]
                            "the WHERE-pinned composite index sorts, matching MySQL's GROUP BY-using-an-index optimization"
                    | other -> failtestf "expected code-sorted groups, got %A" other

                testCase "ORDER BY an aggregate not in the SELECT list, over a grouped query"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('b', 1), ('b', 2), ('b', 3)" |> ignore

                    match runDefault store "SELECT grp FROM t GROUP BY grp ORDER BY COUNT(*) DESC" with
                    | ResultSet([ "grp" ], rows) -> Expect.equal rows [ [ Some "b" ]; [ Some "a" ] ] "'b' has more rows, sorts first descending"
                    | other -> failtestf "expected ORDER BY COUNT(*) DESC to work, got %A" other

                testCase "COUNT(DISTINCT x) counts only distinct non-NULL values"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('a', 1), ('a', 2), ('a', NULL)" |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT n) AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected COUNT(DISTINCT n) = 2, got %A" other

                testCase "COUNT(DISTINCT a, b) counts distinct tuples, dropping any tuple with a NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (g VARCHAR(10), n INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES ('a', 1), ('a', NULL), ('b', 3), ('b', 3), (NULL, 5)"
                    |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT g, n) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected 2 distinct (g, n) tuples ('a',1) and ('b',3), got %A" other

                testCase "GROUP_CONCAT joins group members with the default comma separator, and a custom SEPARATOR"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 'x'), ('a', 'y'), ('a', 'x')" |> ignore

                    match runDefault store "SELECT GROUP_CONCAT(tag) AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "x,y,x" ] ]) -> ()
                    | other -> failtestf "expected the default comma separator, got %A" other

                    match runDefault store "SELECT GROUP_CONCAT(DISTINCT tag SEPARATOR '|') AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "x|y" ] ]) -> ()
                    | other -> failtestf "expected DISTINCT deduping and a custom separator, got %A" other

                testCase "GROUP_CONCAT(expr ORDER BY key [ASC|DESC]) sorts members before concatenating"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), tag VARCHAR(10), seq INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 'x', 3), ('a', 'y', 1), ('a', 'z', 2)" |> ignore

                    match runDefault store "SELECT GROUP_CONCAT(tag ORDER BY seq) AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "y,z,x" ] ]) -> ()
                    | other -> failtestf "expected ascending-by-seq order, got %A" other

                    match runDefault store "SELECT GROUP_CONCAT(tag ORDER BY seq DESC SEPARATOR '|') AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "x|z|y" ] ]) -> ()
                    | other -> failtestf "expected descending-by-seq order with a custom separator, got %A" other

                testCase "SELECT COUNT(*) FROM an empty table still returns one row with 0, not zero rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected one row with COUNT(*) = 0 on an empty table, got %A" other

                testCase "a real GROUP BY over an empty table produces zero rows, unlike the whole-table aggregate case above"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore

                    match runDefault store "SELECT grp, COUNT(*) AS c FROM t GROUP BY grp" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero grouped rows on an empty table, got %A" other

                testCase "every Expr operand shape recurses when it wraps an aggregate in the projection"
                <| fun _ ->
                    // `rewriteAggregates` walks the whole projection expression
                    // to pre-evaluate each aggregate call, descending through
                    // every `Expr` node kind. Each of these wraps `COUNT(*)`
                    // in a different constructor so that walk's per-kind
                    // recursion arms are all exercised, not just the common
                    // BinOp/FuncCall ones.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    let expectInt (sql: string) (expected: string) =
                        match runDefault store sql with
                        | ResultSet(_, [ [ Some v ] ]) -> Expect.equal v expected sql
                        | other -> failtestf "expected %s -> %s, got %A" sql expected other

                    // `FROM t` so COUNT(*) = 3, not the no-FROM single row.
                    expectInt "SELECT COUNT(*) IS NULL FROM t" "0"
                    expectInt "SELECT COUNT(*) IS NOT NULL FROM t" "1"
                    expectInt "SELECT NOT (COUNT(*) = 0) FROM t" "1"
                    expectInt "SELECT (COUNT(*) = 0) IS TRUE FROM t" "0"
                    expectInt "SELECT (COUNT(*) = 0) IS FALSE FROM t" "1"
                    expectInt "SELECT COUNT(*) BETWEEN 1 AND 5 FROM t" "1"
                    expectInt "SELECT COUNT(*) IN (1, 2, 3) FROM t" "1"
                    expectInt "SELECT COUNT(*) LIKE '3' FROM t" "1"
                    expectInt "SELECT COUNT(*) REGEXP '3' FROM t" "1"

                    match runDefault store "SELECT CAST(COUNT(*) AS CHAR) FROM t" with
                    | ResultSet(_, [ [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected CAST(COUNT(*)) = '3', got %A" other

                    match runDefault store "SELECT CASE WHEN COUNT(*) > 5 THEN 'big' ELSE 'small' END FROM t" with
                    | ResultSet(_, [ [ Some "small" ] ]) -> ()
                    | other -> failtestf "expected searched-CASE else branch (3 is not > 5), got %A" other

                    match runDefault store "SELECT CASE COUNT(*) WHEN 3 THEN 'three' ELSE 'other' END FROM t" with
                    | ResultSet(_, [ [ Some "three" ] ]) -> ()
                    | other -> failtestf "expected simple-CASE matching arm, got %A" other

                testCase "every Expr operand shape recurses when it wraps an aggregate alias in HAVING"
                <| fun _ ->
                    // `resolveHavingRef` resolves SELECT aliases nested anywhere
                    // inside a HAVING boolean expression, again recursing
                    // through every Expr node kind. Here each construct wraps
                    // the aliased count `c` (and the real column `grp`) so
                    // those per-kind arms fire.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('a', 2), ('b', 3)" |> ignore

                    let grouped (having: string) expected : unit =
                        match runDefault store $"SELECT grp, COUNT(*) AS c FROM t GROUP BY grp HAVING {having} ORDER BY grp" with
                        | ResultSet(_, rows) -> Expect.equal rows expected having
                        | other -> failtestf "expected %A for HAVING %s, got %A" expected having other

                    // both groups pass, in order
                    grouped "c IS NOT NULL" [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ]
                    grouped "NOT (c = 5)" [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ]
                    grouped "c BETWEEN 1 AND 2" [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ]
                    grouped "c IN (1, 2)" [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ]
                    // no group has a NULL count, so nothing matches (still
                    // exercises the HAVING-side IS NULL recursion arm)
                    grouped "c IS NULL" []
                    grouped "c LIKE '2'" [ [ Some "a"; Some "2" ] ]
                    grouped "c REGEXP '2'" [ [ Some "a"; Some "2" ] ]
                    grouped "CAST(c AS CHAR) = '1'" [ [ Some "b"; Some "1" ] ]
                    grouped "CASE WHEN c > 1 THEN 1 ELSE 0 END = 1" [ [ Some "a"; Some "2" ] ]
                    // simple CASE over the real GROUP BY column
                    grouped "CASE grp WHEN 'a' THEN 1 ELSE 0 END = 1" [ [ Some "a"; Some "2" ] ]
                    // HAVING-side IS TRUE / IS FALSE arms (covered in the
                    // projection walk above, but each walk is its own match).
                    grouped "(c = 2) IS TRUE" [ [ Some "a"; Some "2" ] ]
                    // (c = 2) is false for group 'b' (c = 1)
                    grouped "(c = 2) IS FALSE" [ [ Some "b"; Some "1" ] ]

                    ]

          ExecutorSubqueryTests.tests

          testList
              "CASE / UNION / <=> / IS TRUE-FALSE / REGEXP / LIKE BINARY"
              [ testCase "searched CASE WHEN ... THEN ... ELSE ... END"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (-1), (0)" |> ignore

                    let sql = "SELECT CASE WHEN n > 0 THEN 'pos' WHEN n < 0 THEN 'neg' ELSE 'zero' END AS sign FROM t ORDER BY n"

                    match runDefault store sql with
                    | ResultSet([ "sign" ], rows) ->
                        Expect.equal rows [ [ Some "neg" ]; [ Some "zero" ]; [ Some "pos" ] ] "one branch per row"
                    | other -> failtestf "expected a CASE per row, got %A" other

                testCase "simple CASE subject WHEN value THEN ... END, falling through to NULL with no ELSE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT CASE n WHEN 1 THEN 'one' WHEN 2 THEN 'two' END AS label FROM t ORDER BY n" with
                    | ResultSet([ "label" ], rows) -> Expect.equal rows [ [ Some "one" ]; [ Some "two" ]; [ None ] ] "3 falls through to NULL"
                    | other -> failtestf "expected a simple CASE with an implicit NULL else, got %A" other

                testCase "UNION dedupes, UNION ALL keeps duplicates"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (n INT)" |> ignore
                    runDefault store "CREATE TABLE b (n INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM a UNION SELECT n FROM b ORDER BY n" with
                    | ResultSet([ "n" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "deduped"
                    | other -> failtestf "expected UNION to dedupe, got %A" other

                    match runDefault store "SELECT n FROM a UNION ALL SELECT n FROM b ORDER BY n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "2" ]; [ Some "3" ] ] "duplicates kept"
                    | other -> failtestf "expected UNION ALL to keep duplicates, got %A" other

                testCase "UNION's ORDER BY sorts numerically, not as re-wrapped text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT n FROM nums UNION SELECT n FROM nums ORDER BY n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "2" ]; [ Some "9" ]; [ Some "10" ] ] "sorted numerically, not lexicographically"
                    | other -> failtestf "expected a numerically-sorted UNION resultset, got %A" other

                testCase "UNION reconciles mixed branch types like MySQL — a string anywhere makes the column sort as text"
                <| fun _ ->
                    // Verified against MySQL 8.4: the reconciled column is
                    // VARCHAR, so ORDER BY is lexical — '1.5,10,2,9', not a
                    // numeric 1.5,2,9,10.
                    let store = newStore ()

                    match runDefault store "SELECT '10' AS v UNION SELECT '9' UNION SELECT 2 UNION SELECT 1.5 ORDER BY v" with
                    | ResultSet([ "v" ], rows) ->
                        Expect.equal rows [ [ Some "1.5" ]; [ Some "10" ]; [ Some "2" ]; [ Some "9" ] ] "string-reconciled lexical sort"
                    | other -> failtestf "expected the string-reconciled sort, got %A" other

                    // all-numeric branches still sort numerically, and the
                    // DECIMAL reconciliation re-renders at the union's scale
                    // (MySQL shows 2.0, not 2)
                    match runDefault store "SELECT 2 AS n UNION SELECT 1.5 ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1.5" ]; [ Some "2.0" ] ] "numeric branches sort numerically"
                    | other -> failtestf "expected the numeric sort, got %A" other

                testCase "UNION DISTINCT dedupes after type reconciliation, not on each branch's raw text"
                <| fun _ ->
                    // Dedup must key on the reconciled value, not each
                    // branch's raw text ("1.0" vs "1"), to collapse to the
                    // one row MySQL gives.
                    match runDefault (newStore ()) "SELECT 1.0 UNION SELECT 1" with
                    | ResultSet(_, [ [ Some "1.0" ] ]) -> ()
                    | other -> failtestf "expected UNION to dedupe post-reconciliation to one row, got %A" other

                testCase "UNION ... ORDER BY an expression sorts by it, not silently by insertion order"
                <| fun _ ->
                    // `orderKeyOf` must evaluate full expressions, not just
                    // a bare `Col` — otherwise every key is VNull and the
                    // union stays unsorted.
                    match runDefault (newStore ()) "SELECT 1 AS v UNION SELECT 3 UNION SELECT 2 ORDER BY -v" with
                    | ResultSet([ "v" ], rows) -> Expect.equal rows [ [ Some "3" ]; [ Some "2" ]; [ Some "1" ] ] "sorted by -v descending"
                    | other -> failtestf "expected ORDER BY -v to actually sort, got %A" other

                testCase "COUNT(*) FROM ((SELECT ...) UNION (SELECT ...)) AS alias — Laravel's paginate-over-union shape"
                <| fun _ ->
                    // `unionAll(...)->paginate()` and `->count()` compile to
                    // exactly this: a `UNION` whose branches are each
                    // individually parenthesized, itself wrapped in one more
                    // pair of parens as a derived table's body.
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (n INT)" |> ignore
                    runDefault store "CREATE TABLE b (n INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (2), (3)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS aggregate FROM ((SELECT n FROM a) UNION (SELECT n FROM b)) AS search_items" with
                    | ResultSet([ "aggregate" ], [ [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected the deduped union's row count (3), got %A" other

                    match runDefault store "SELECT n FROM ((SELECT n FROM a) UNION ALL (SELECT n FROM b)) AS search_items ORDER BY n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "2" ]; [ Some "3" ] ] "UNION ALL keeps duplicates inside the derived table too"
                    | other -> failtestf "expected the union-derived-table's rows, got %A" other

                testCase "a parenthesized UNION branch's own ORDER BY/LIMIT stays that branch's, not the whole union's"
                <| fun _ ->
                    // A parenthesized branch's own ORDER BY/LIMIT must stay
                    // attached to that branch — MySQL runs the union itself
                    // unordered/unlimited here, not with one branch's clause
                    // hijacked into a union-wide one.
                    let store = newStore ()
                    runDefault store "CREATE TABLE u1 (n INT)" |> ignore
                    runDefault store "CREATE TABLE u2 (n INT)" |> ignore
                    runDefault store "INSERT INTO u1 VALUES (3)" |> ignore
                    runDefault store "INSERT INTO u2 VALUES (1), (4)" |> ignore

                    match runDefault store "(SELECT n FROM u1) UNION ALL (SELECT n FROM u2 ORDER BY n DESC LIMIT 1)" with
                    | ResultSet([ "n" ], [ [ Some "3" ]; [ Some "4" ] ]) -> ()
                    | other -> failtestf "expected u1's whole row plus u2's own top-1 DESC row, got %A" other

                    match
                        runDefault
                            store
                            "SELECT * FROM ((SELECT n FROM u1) UNION ALL (SELECT n FROM u2 ORDER BY n DESC LIMIT 1)) z"
                    with
                    | ResultSet([ "n" ], [ [ Some "3" ]; [ Some "4" ] ]) -> ()
                    | other -> failtestf "expected the same result inside a derived table, got %A" other

                testCase "a genuine union-level ORDER BY/LIMIT parses after every branch is parenthesized"
                <| fun _ ->
                    // What MySqlGrammar emits for ->union(...)->orderBy()->limit().
                    let store = newStore ()
                    runDefault store "CREATE TABLE u1 (n INT)" |> ignore
                    runDefault store "CREATE TABLE u2 (n INT)" |> ignore
                    runDefault store "INSERT INTO u1 VALUES (3)" |> ignore
                    runDefault store "INSERT INTO u2 VALUES (1), (4)" |> ignore

                    match runDefault store "(SELECT n FROM u1) UNION (SELECT n FROM u2) ORDER BY n DESC LIMIT 2" with
                    | ResultSet([ "n" ], [ [ Some "4" ]; [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected the top 2 rows of the deduped union, sorted DESC, got %A" other

                testCase "<=> is a null-safe equals: NULL <=> NULL is true, unlike ="
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL <=> NULL AS a, NULL = NULL AS b, 1 <=> 1 AS c, 1 <=> 2 AS d" with
                    | ResultSet([ "a"; "b"; "c"; "d" ], [ [ Some "1"; None; Some "1"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected <=> to never be NULL, got %A" other

                testCase "row comparisons are lexicographic and retain three-valued NULL semantics"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            "SELECT ROW(1, 2) = (1, 2) AS eq, (1, NULL) = ROW(1, NULL) AS unknown_eq, ROW(1, NULL) < (2, 0) AS first_decides, (1, NULL) < ROW(1, 0) AS unknown_order, ROW(NULL, 1) <=> (NULL, 1) AS null_safe, ROW((1, 2), 3) = ROW(ROW(1, 2), 3) AS nested_eq, ((1, NULL), 3) < ROW((2, 0), 0) AS nested_first_decides"
                    with
                    | ResultSet(_, [ [ Some "1"; None; Some "1"; None; Some "1"; Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected row comparison results, got %A" other

                    runDefault store "CREATE TABLE pairs (a INT, b INT)" |> ignore
                    runDefault store "INSERT INTO pairs VALUES (1, 2), (1, NULL), (2, 0), (3, 4)" |> ignore

                    match runDefault store "SELECT a, b FROM pairs WHERE (a, b) = (1, 2) OR (a, b) > (1, 9) ORDER BY a" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ]; [ Some "3"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected lexicographic row filtering, got %A" other

                    match runDefault store "SELECT a, b FROM pairs WHERE ROW(a, b) IN (ROW(1, 2), (3, 4)) ORDER BY a" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ]; [ Some "3"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected ROW IN matches, got %A" other

                testCase "row IN supports literal candidates and multi-column subqueries"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE pairs (a INT, b INT)" |> ignore
                    runDefault store "CREATE TABLE allowed (a INT, b INT)" |> ignore
                    runDefault store "INSERT INTO pairs VALUES (1, 2), (1, NULL), (3, 4)" |> ignore
                    runDefault store "INSERT INTO allowed VALUES (1, 2), (3, 4)" |> ignore

                    match runDefault store "SELECT a, b FROM pairs WHERE (a, b) IN ((1, 2), (3, 4)) ORDER BY a" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ]; [ Some "3"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected literal row IN matches, got %A" other

                    match runDefault store "SELECT a, b FROM pairs WHERE (a, b) IN (SELECT a, b FROM allowed) ORDER BY a" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ]; [ Some "3"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected subquery row IN matches, got %A" other

                    match runDefault store "SELECT (1, 2) = (SELECT a, b FROM allowed WHERE a = 1) AS matches" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected multi-column scalar-subquery comparison, got %A" other

                    runDefault store "CREATE TABLE binary_pairs (value VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO binary_pairs VALUES ('A')" |> ignore

                    match runDefault store "SELECT ('a', 1) IN (SELECT value COLLATE utf8mb4_bin, 1 FROM binary_pairs) AS binary_match" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected a binary subquery field not to fold case, got %A" other

                    match
                        runDefault
                            store
                            "SELECT ('a', 1) IN (SELECT value, 1 FROM (SELECT value COLLATE utf8mb4_bin AS value FROM binary_pairs) AS binary_source) AS derived_binary_match"
                    with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected a derived binary subquery field not to fold case, got %A" other

                    runDefault store "CREATE TABLE enum_pairs (value ENUM('one', 'two'))" |> ignore
                    runDefault store "INSERT INTO enum_pairs VALUES ('two')" |> ignore

                    match runDefault store "SELECT (2, 1) IN (SELECT value, 1 FROM enum_pairs) AS enum_match" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected an ENUM subquery field to compare by ordinal, got %A" other

                testCase "row predicates reject mismatched arity and bare row projections"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT (1, 2) = (1, 2, 3)" with
                    | Err(1241, "Operand should contain 2 column(s)") -> ()
                    | other -> failtestf "expected row-comparison arity error, got %A" other

                    match runDefault store "SELECT (1, 2) IN ((1, 2, 3))" with
                    | Err(1241, "Operand should contain 2 column(s)") -> ()
                    | other -> failtestf "expected row-IN arity error, got %A" other

                    match runDefault store "SELECT (1, 2)" with
                    | Err(1241, "Operand should contain 1 column(s)") -> ()
                    | other -> failtestf "expected scalar-context row arity error, got %A" other

                testCase "IS TRUE / IS FALSE are never NULL, even for a NULL operand"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT (1 IS TRUE) AS a, (0 IS FALSE) AS b, (NULL IS TRUE) AS c, (NULL IS FALSE) AS d" with
                    | ResultSet([ "a"; "b"; "c"; "d" ], [ [ Some "1"; Some "1"; Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected IS TRUE/FALSE to be plain booleans, got %A" other

                testCase "REGEXP matches a real regex pattern, case-insensitively"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello'), ('world')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^h' ORDER BY s" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected REGEXP to case-insensitively match 'Hello', got %A" other

                testCase "REGEXP on a _bin column is case-sensitive, like =/LIKE BINARY"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello'), ('world')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^h'" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected a case-sensitive REGEXP not to match 'Hello' against '^h', got %A" other

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^H'" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected the case-sensitive REGEXP to match 'Hello' against '^H', got %A" other

                testCase "REGEXP entry points use operand collation and match_type case policy"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            "SELECT ('HÉllo' COLLATE utf8mb4_0900_ai_ci) REGEXP '^héllo$', ('HÉllo' COLLATE utf8mb4_0900_as_ci) REGEXP '^héllo$', ('HÉllo' COLLATE utf8mb4_bin) REGEXP '^héllo$', REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_ai_ci, '^héllo$'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_as_ci, '^héllo$'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_bin, '^héllo$'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_ai_ci, '^hello$'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_bin, '^héllo$', 'i'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_ai_ci, '^héllo$', 'c'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_ai_ci, '^héllo$', 'ci'), REGEXP_LIKE('HÉllo' COLLATE utf8mb4_0900_ai_ci, '^héllo$', 'ic'), REGEXP_INSTR('HÉllo' COLLATE utf8mb4_0900_ai_ci, 'é'), REGEXP_SUBSTR('HÉllo' COLLATE utf8mb4_0900_ai_ci, 'é'), REGEXP_REPLACE('HÉllo' COLLATE utf8mb4_0900_ai_ci, 'é', 'X')"
                    with
                    | ResultSet(_, [ [ Some "1"; Some "1"; Some "0"; Some "1"; Some "1"; Some "0"; Some "0"; Some "1"; Some "0"; Some "1"; Some "0"; Some "2"; Some "É"; Some "HXllo" ] ]) -> ()
                    | other -> failtestf "expected collation-aware regular expressions, got %A" other

                    match runDefault store "SELECT REGEXP_LIKE('Ångström' COLLATE utf8mb4_0900_ai_ci, '^ångström$'), REGEXP_LIKE('Ångström' COLLATE utf8mb4_0900_ai_ci, '^angstrom$')" with
                    | ResultSet(_, [ [ Some "1"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected Unicode case matching without accent folding, got %A" other

                    match runDefault store "SELECT _binary'Hello' REGEXP _binary'^hello$', REGEXP_LIKE(_binary'Hello', _binary'^hello$')" with
                    | ResultSet(_, [ [ Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected binary regular expressions to remain case-sensitive, got %A" other

                    match runDefault store "SELECT REGEXP_LIKE('Hello', '^hello$' COLLATE utf8mb4_0900_ai_ci), REGEXP_LIKE('Σ', '^ς$')" with
                    | ResultSet(_, [ [ Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected explicit pattern collation and sigma folding, got %A" other

                    match runDefault store "SELECT REGEXP_LIKE('x' COLLATE utf8mb4_0900_ai_ci, 'x' COLLATE utf8mb4_bin)" with
                    | Err(1267, "Illegal mix of collations (utf8mb4_0900_ai_ci,EXPLICIT) and (utf8mb4_bin,EXPLICIT) for operation 'regexp_like'") -> ()
                    | other -> failtestf "expected conflicting explicit collations to fail with 1267, got %A" other

                    match runDefault store "SELECT REGEXP_LIKE(_binary'x', 'x')" with
                    | Err(3995, "Character set 'binary' cannot be used in conjunction with 'utf8mb4_0900_ai_ci' in call to regexp_like.") -> ()
                    | other -> failtestf "expected mixed binary and text operands to fail with 3995, got %A" other

                testCase "REGEXP on a catastrophically-backtracking pattern errors instead of hanging"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(200))" |> ignore
                    let pathological = String.replicate 40 "a" + "!"
                    runDefault store (sprintf "INSERT INTO t VALUES ('%s')" pathological) |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '(a+)+$'" with
                    | Err(3699, "Timeout exceeded in regular expression match.") -> ()
                    | other -> failtestf "expected a REGEXP timeout, got %A" other

                testCase "LIKE BINARY is case-sensitive, unlike plain LIKE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s LIKE BINARY 'hello'" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected LIKE BINARY 'hello' not to match 'Hello', got %A" other

                    match runDefault store "SELECT s FROM t WHERE s LIKE 'hello'" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected plain LIKE to still match case-insensitively, got %A" other ]

          testList
              "generated columns"
              [ testCase "a STORED generated column computes on INSERT"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2) STORED)" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3), (5)" |> ignore

                    match runDefault store "SELECT n, doubled FROM t ORDER BY n" with
                    | ResultSet([ "n"; "doubled" ], rows) ->
                        Expect.equal rows [ [ Some "3"; Some "6" ]; [ Some "5"; Some "10" ] ] "computed from n"
                    | other -> failtestf "expected doubled to be computed, got %A" other

                testCase "generated columns reject user and system variables"
                <| fun _ ->
                    let store = newStore ()

                    [ "CREATE TABLE user_variable (n INT, g INT AS (@value + n))"
                      "CREATE TABLE system_variable (n INT, g INT AS (@@max_connections + n))" ]
                    |> List.iter (fun sql ->
                        match runDefault store sql with
                        | Err(3772, "Default value expression of column 'g' cannot refer user or system variables.") -> ()
                        | other -> failtestf "expected generated-column variable rejection, got %A" other)

                testCase "a generated column recomputes after UPDATE of a column it depends on"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2))" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3)" |> ignore
                    runDefault store "UPDATE t SET n = 10" |> ignore

                    match runDefault store "SELECT doubled FROM t" with
                    | ResultSet([ "doubled" ], [ [ Some "20" ] ]) -> ()
                    | other -> failtestf "expected doubled to recompute to 20, got %A" other

                testCase "an INSERT naming a generated column errors 3105 like MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2) STORED)" |> ignore

                    match runDefault store "INSERT INTO t (n, doubled) VALUES (3, 7)" with
                    | Err(3105, "The value specified for generated column 'doubled' in table 't' is not allowed.") -> ()
                    | other -> failtestf "expected error 3105, got %A" other

                testCase "an UPDATE SET of a generated column errors 3105"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2))" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3)" |> ignore

                    match runDefault store "UPDATE t SET doubled = 7" with
                    | Err(3105, "The value specified for generated column 'doubled' in table 't' is not allowed.") -> ()
                    | other -> failtestf "expected error 3105, got %A" other

                testCase "an ON DUPLICATE KEY UPDATE targeting a generated column errors 3105"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT PRIMARY KEY, doubled INT AS (n * 2))" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3)" |> ignore

                    match runDefault store "INSERT INTO t (n) VALUES (3) ON DUPLICATE KEY UPDATE doubled = 9" with
                    | Err(3105, _) -> ()
                    | other -> failtestf "expected error 3105, got %A" other

                testCase "ALTER TABLE ADD COLUMN AS (expr) backfills existing rows, not NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3), (5)" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN doubled INT AS (n * 2) STORED" |> ignore

                    match runDefault store "SELECT n, doubled FROM t ORDER BY n" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "3"; Some "6" ]; [ Some "5"; Some "10" ] ] "backfilled from existing n values"
                    | other -> failtestf "expected the added generated column backfilled, got %A" other ]

          testList
              "DATE column coercion"
              [ testCase "a full datetime string into a DATE column keeps just the date part, like real MySQL"
                <| fun _ ->
                    // Real MySQL silently truncates a full datetime string
                    // into just its date part on insert into a DATE column
                    // — this is exactly what Eloquent's `date` cast sends
                    // (Carbon's full `'Y-m-d H:i:s'` string form).
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (d DATE)" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('2024-03-05 13:45:09')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the datetime string to coerce into DATE, got %A" other

                    match runDefault store "SELECT d FROM t" with
                    | ResultSet([ "d" ], [ [ Some "2024-03-05" ] ]) -> ()
                    | other -> failtestf "expected the date part only, got %A" other ]

          testList
              "ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)"
              [ testCase "numbers rows 1.. per partition, in the window's own ORDER BY order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, created_at INT, body VARCHAR(20))" |> ignore

                    runDefault
                        store
                        "INSERT INTO msgs VALUES (1, 1, 'a'), (1, 2, 'b'), (1, 3, 'c'), (2, 1, 'x')"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT body, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at DESC) AS rn FROM msgs ORDER BY session_id, rn"
                    with
                    | ResultSet([ "body"; "rn" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "c"; Some "1" ]; [ Some "b"; Some "2" ]; [ Some "a"; Some "3" ]; [ Some "x"; Some "1" ] ]
                            "each session's messages numbered newest-first, restarting per session"
                    | other -> failtestf "expected numbered rows, got %A" other

                testCase "SELECT * alongside ROW_NUMBER() OVER (...) doesn't leak the synthetic column into *"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, body VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO msgs VALUES (1, 'a')" |> ignore

                    match
                        runDefault store "SELECT *, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY body) AS rn FROM msgs"
                    with
                    | ResultSet([ "session_id"; "body"; "rn" ], [ [ Some "1"; Some "a"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected exactly session_id, body, rn (no duplicate/leaked column), got %A" other

                testCase "the Laravel 'limit per group' shape: a derived table filtered on the window alias"
                <| fun _ ->
                    // Verbatim repro of the pattern Eloquent's constrained
                    // eager loading (`->with(['msgs' => fn ($q) =>
                    // $q->orderBy('created_at', 'desc')->limit(1)])`)
                    // compiles a relation query's `->limit()` into.
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, created_at INT, body VARCHAR(20))" |> ignore

                    runDefault
                        store
                        "INSERT INTO msgs VALUES (1, 1, 'old'), (1, 2, 'newest'), (2, 1, 'only')"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at DESC) AS laravel_row FROM msgs) AS t WHERE laravel_row <= 1 ORDER BY session_id"
                    with
                    | ResultSet([ "session_id"; "created_at"; "body"; "laravel_row" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "2"; Some "newest"; Some "1" ]; [ Some "2"; Some "1"; Some "only"; Some "1" ] ]
                            "only the latest message per session survives"
                    | other -> failtestf "expected one row per session, got %A" other ]

          testList
              "LAG(expr) OVER (PARTITION BY ... ORDER BY ...)"
              [ testCase "yields the previous row's value per partition, NULL for the first"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), created_at INT, value INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO readings VALUES ('a', 1, 10), ('a', 2, 14), ('a', 3, 9), ('b', 1, 100)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT sensor, value, LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS prev FROM readings ORDER BY sensor, created_at"
                    with
                    | ResultSet([ "sensor"; "value"; "prev" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "10"; None ]
                              [ Some "a"; Some "14"; Some "10" ]
                              [ Some "a"; Some "9"; Some "14" ]
                              [ Some "b"; Some "100"; None ] ]
                            "each partition's first row has no predecessor, later rows see the prior one"
                    | other -> failtestf "expected lagged values, got %A" other

                testCase "LEAD yields the next row's value per partition, NULL for the last"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), created_at INT, value INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO readings VALUES ('a', 1, 10), ('a', 2, 14), ('a', 3, 9), ('b', 1, 100)"
                    |> ignore

                    // Oracle (MySQL 8.4.11): a -> 14, 9, NULL; b -> NULL.
                    match
                        runDefault
                            store
                            "SELECT sensor, value, LEAD(value) OVER (PARTITION BY sensor ORDER BY created_at) AS nxt FROM readings ORDER BY sensor, created_at"
                    with
                    | ResultSet([ "sensor"; "value"; "nxt" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "10"; Some "14" ]
                              [ Some "a"; Some "14"; Some "9" ]
                              [ Some "a"; Some "9"; None ]
                              [ Some "b"; Some "100"; None ] ]
                            "each partition's last row has no successor, earlier rows see the next one"
                    | other -> failtestf "expected led values, got %A" other

                testCase "LEAD with an explicit offset skips forward within the partition"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (created_at INT, value INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES (1, 10), (2, 14), (3, 9)" |> ignore

                    // Oracle: 10 -> 9 (two ahead), 14 -> NULL, 9 -> NULL.
                    match runDefault store "SELECT value, LEAD(value, 2) OVER (ORDER BY created_at) AS n2 FROM readings ORDER BY created_at" with
                    | ResultSet([ "value"; "n2" ], [ [ Some "10"; Some "9" ]; [ Some "14"; None ]; [ Some "9"; None ] ]) -> ()
                    | other -> failtestf "expected LEAD(value, 2), got %A" other

                testCase "usable nested inside arithmetic, not just as a bare projection"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), created_at INT, value INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES ('a', 1, 10), ('a', 2, 14)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT value, value - LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS diff FROM readings ORDER BY created_at"
                    with
                    | ResultSet([ "value"; "diff" ], [ [ Some "10"; None ]; [ Some "14"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected value - LAG(value) computed per row, got %A" other

                testCase "an explicit offset skips back that many rows within the partition"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (created_at INT, value INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES (1, 10), (2, 20), (3, 30)" |> ignore

                    match
                        runDefault store "SELECT value, LAG(value, 2) OVER (ORDER BY created_at) AS prev2 FROM readings ORDER BY created_at"
                    with
                    | ResultSet([ "value"; "prev2" ], [ [ Some "10"; None ]; [ Some "20"; None ]; [ Some "30"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected offset-2 lag, got %A" other

                testCase "an un-aliased LEAD/LAG projection labels itself like MySQL, not the internal synthetic column name"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10), (2, 20)" |> ignore

                    match runDefault store "SELECT LEAD(v) OVER (ORDER BY id) FROM t" with
                    | ResultSet([ "lead(v) over (order by id)" ], _) -> ()
                    | other -> failtestf "expected the column header 'lead(v) over (order by id)', got %A" other

                    match runDefault store "SELECT LAG(v) OVER (ORDER BY id) FROM t" with
                    | ResultSet([ "lag(v) over (order by id)" ], _) -> ()
                    | other -> failtestf "expected the column header 'lag(v) over (order by id)', got %A" other

                testCase "a window function in ORDER BY alone (not in the SELECT list) sorts by it, NULLs first"
                <| fun _ ->
                    // Oracle (MySQL 8.4.11): LAG values NULL,NULL,10,30 sort
                    // NULLs first -> (a,10),(b,30),(a,20),(b,5).
                    let store = newStore ()

                    runDefault store "CREATE TABLE readings (id INT PRIMARY KEY, sensor VARCHAR(10), value INT, created_at INT)"
                    |> ignore

                    runDefault store "INSERT INTO readings VALUES (1, 'a', 10, 1), (2, 'a', 20, 2), (3, 'b', 30, 1), (4, 'b', 5, 2)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT sensor, value FROM readings ORDER BY LAG(value) OVER (PARTITION BY sensor ORDER BY created_at)"
                    with
                    | ResultSet([ "sensor"; "value" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "10" ]
                              [ Some "b"; Some "30" ]
                              [ Some "a"; Some "20" ]
                              [ Some "b"; Some "5" ] ]
                            "rows sorted by the LAG value, NULLs first"
                    | other -> failtestf "expected rows ordered by the windowed LAG, got %A" other

                testCase "LEAD + LAG + ROW_NUMBER together in one SELECT each compute independently"
                <| fun _ ->
                    let store = newStore ()

                    runDefault store "CREATE TABLE readings (id INT PRIMARY KEY, sensor VARCHAR(10), value INT, created_at INT)"
                    |> ignore

                    runDefault store "INSERT INTO readings VALUES (1, 'a', 10, 1), (2, 'a', 20, 2), (3, 'b', 30, 1), (4, 'b', 5, 2)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT id, sensor, value, LEAD(value) OVER (PARTITION BY sensor ORDER BY created_at) AS nxt, LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS prv, ROW_NUMBER() OVER (PARTITION BY sensor ORDER BY created_at) AS rn FROM readings ORDER BY id"
                    with
                    | ResultSet([ "id"; "sensor"; "value"; "nxt"; "prv"; "rn" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "a"; Some "10"; Some "20"; None; Some "1" ]
                              [ Some "2"; Some "a"; Some "20"; None; Some "10"; Some "2" ]
                              [ Some "3"; Some "b"; Some "30"; Some "5"; None; Some "1" ]
                              [ Some "4"; Some "b"; Some "5"; None; Some "30"; Some "2" ] ]
                            "all three window functions computed per partition"
                    | other -> failtestf "expected LEAD/LAG/ROW_NUMBER side by side, got %A" other

                testCase "a window function in HAVING errors 3593 like MySQL, a window alias in HAVING errors 3594"
                <| fun _ ->
                    // MySQL's ER_WINDOW_INVALID_WINDOW_FUNC_USE /
                    // ..._ALIAS_USE messages both really end with a stray
                    // `.'` — verified against 8.4.11.
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), value INT, created_at INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES ('a', 10, 1), ('a', 20, 2)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT sensor, value FROM readings HAVING LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) IS NULL"
                    with
                    | Err(3593, "You cannot use the window function 'lag' in this context.'") -> ()
                    | other -> failtestf "expected error 3593, got %A" other

                    match
                        runDefault
                            store
                            "SELECT sensor, value, LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS d FROM readings HAVING d IS NULL"
                    with
                    | Err(3594, "You cannot use the alias 'd' of an expression containing a window function in this context.'") -> ()
                    | other -> failtestf "expected error 3594, got %A" other

                testCase "PARTITION BY over an explicitly collated column folds 'a'/'A'/'á' into one partition"
                <| fun _ ->
                    // Oracle (MySQL 8.4.11): all three of a/A/á share one
                    // utf8mb4_0900_ai_ci partition, so LAG chains through
                    // them; 'b' starts fresh.
                    let store = newStore ()

                    runDefault store "CREATE TABLE t3 (id INT PRIMARY KEY, k VARCHAR(10) COLLATE utf8mb4_0900_ai_ci, v INT)"
                    |> ignore

                    runDefault store "INSERT INTO t3 VALUES (1, 'a', 100), (2, 'A', 200), (3, 'á', 300), (4, 'b', 400)"
                    |> ignore

                    match
                        runDefault store "SELECT id, k, v, LAG(v) OVER (PARTITION BY k ORDER BY id) AS prev_v FROM t3 ORDER BY id"
                    with
                    | ResultSet([ "id"; "k"; "v"; "prev_v" ], rows) ->
                        Expect.equal
                            (rows |> List.map (List.item 3))
                            [ None; Some "100"; Some "200"; None ]
                            "a/A/á chain within one folded partition, b restarts"
                    | other -> failtestf "expected collation-folded LAG chain, got %A" other

                testCase "RANK() ties share a rank and the next distinct value skips ahead; DENSE_RANK() never skips"
                <| fun _ ->
                    // Oracle (MySQL 8.4.11): g=1's two 10s tie at rank 1,
                    // then 20 is rank 3 (RANK) / rank 2 (DENSE_RANK).
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, g INT, v INT)" |> ignore

                    runDefault store "INSERT INTO t VALUES (1, 1, 10), (2, 1, 10), (3, 1, 20), (4, 2, 5)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT id, g, v, RANK() OVER (PARTITION BY g ORDER BY v) AS r, DENSE_RANK() OVER (PARTITION BY g ORDER BY v) AS dr FROM t ORDER BY id"
                    with
                    | ResultSet([ "id"; "g"; "v"; "r"; "dr" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1"; Some "10"; Some "1"; Some "1" ]
                              [ Some "2"; Some "1"; Some "10"; Some "1"; Some "1" ]
                              [ Some "3"; Some "1"; Some "20"; Some "3"; Some "2" ]
                              [ Some "4"; Some "2"; Some "5"; Some "1"; Some "1" ] ]
                            "RANK skips the tied count, DENSE_RANK doesn't, and g=2 resets per partition"
                    | other -> failtestf "expected tie-aware RANK/DENSE_RANK, got %A" other

                testCase "RANK()/DENSE_RANK() with no ORDER BY tie every row in the partition together"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 10), (2, 30), (3, 20)" |> ignore

                    match
                        runDefault store "SELECT id, RANK() OVER () AS r, DENSE_RANK() OVER () AS dr FROM t ORDER BY id"
                    with
                    | ResultSet([ "id"; "r"; "dr" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1"; Some "1" ]
                              [ Some "2"; Some "1"; Some "1" ]
                              [ Some "3"; Some "1"; Some "1" ] ]
                            "no ORDER BY means every row ties at rank 1"
                    | other -> failtestf "expected every row tied at rank 1, got %A" other

                testCase "PERCENT_RANK() is (rank - 1) / (rows_in_partition - 1), 0 for a one-row partition"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, g INT, v INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES (1, 1, 10), (2, 1, 10), (3, 1, 20), (4, 1, 30), (5, 2, 100)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT id, PERCENT_RANK() OVER (PARTITION BY g ORDER BY v) AS pr FROM t ORDER BY id"
                    with
                    | ResultSet([ "id"; "pr" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "0" ]
                              [ Some "2"; Some "0" ]
                              [ Some "3"; Some "0.6666666666666666" ]
                              [ Some "4"; Some "1" ]
                              [ Some "5"; Some "0" ] ]
                            "(rank-1)/(n-1) per partition, 0 when the partition has one row"
                    | other -> failtestf "expected PERCENT_RANK values, got %A" other

                testCase "NTILE(n) distributes the remainder to earlier buckets, per partition"
                <| fun _ ->
                    // 4 rows into 3 buckets is 2/1/1, not 1/1/2 — Oracle
                    // (MySQL 8.4.11) confirmed.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, g INT, v INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES (1, 1, 1), (2, 1, 2), (3, 1, 3), (4, 1, 4), (5, 2, 1)"
                    |> ignore

                    match
                        runDefault store "SELECT id, NTILE(3) OVER (PARTITION BY g ORDER BY v) AS nt FROM t ORDER BY id"
                    with
                    | ResultSet([ "id"; "nt" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1" ]
                              [ Some "2"; Some "1" ]
                              [ Some "3"; Some "2" ]
                              [ Some "4"; Some "3" ]
                              [ Some "5"; Some "1" ] ]
                            "4 rows into 3 buckets is 2/1/1, earlier buckets absorbing the remainder"
                    | other -> failtestf "expected NTILE bucket assignment, got %A" other

                testCase "NTILE(0) errors 1210 like MySQL's ER_WRONG_ARGUMENTS"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "SELECT NTILE(0) OVER (ORDER BY id) FROM t" with
                    | Err(1210, "Incorrect arguments to ntile") -> ()
                    | other -> failtestf "expected error 1210, got %A" other

                testCase "NTILE with a huge bucket count stays O(rows): each row gets its own bucket, no giant allocation"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    // buckets >> rows: MySQL puts one row per bucket, so the
                    // three rows are buckets 1,2,3. Must not allocate a
                    // 2-billion-element array.
                    match runDefault store "SELECT id, NTILE(2147483647) OVER (ORDER BY id) AS nt FROM t ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ]; [ Some "3"; Some "3" ] ]
                            "each row its own bucket"
                    | other -> failtestf "expected NTILE assignment, got %A" other ]

          testList
              "Equality-index lookup paths"
              [ testCase "WHERE pk = <literal>, alone or ANDed with a residual condition, matches a forced-scan twin table over randomized data"
                <| fun _ ->
                    // `indexed`'s `id` is PRIMARY KEY, so a point SELECT on
                    // it takes runSelectStmt's index fast path;
                    // `unindexed`'s otherwise-identical `id` has no
                    // constraint at all, so the exact same query against it
                    // can never take that path and falls back to the
                    // ordinary full scan — same data, same SQL, two
                    // different code paths to reach it.
                    let rnd = System.Random(20260817)
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, name VARCHAR(20), age INT)" |> ignore
                    runDefault store "CREATE TABLE unindexed (id INT, name VARCHAR(20), age INT)" |> ignore

                    // Collation edge cases ('Foo' vs 'foo  ') alongside
                    // plain rows, and a duplicate-`age` value so the
                    // residual (non-indexed) predicate actually discriminates.
                    let rows =
                        [ 1, "Alice", 30
                          2, "Foo", 30
                          3, "foo  ", 40
                          4, "bob", 40
                          5, "BOB", 50 ]
                        @ [ for i in 6 .. 40 -> i, sprintf "name%d" i, rnd.Next(20, 60) ]

                    for id, name, age in rows do
                        runDefault store (sprintf "INSERT INTO indexed VALUES (%d, '%s', %d)" id name age) |> ignore
                        runDefault store (sprintf "INSERT INTO unindexed VALUES (%d, '%s', %d)" id name age) |> ignore

                    let readRows (sql: string) : (string * string) list =
                        match runDefault store sql with
                        | ResultSet(_, rowsOut) ->
                            rowsOut
                            |> List.map (function
                                | [ Some a; Some b ] -> a, b
                                | other -> failtestf "expected 2 non-null columns, got %A" other)
                        | other -> failtestf "expected a resultset for %s, got %A" sql other

                    for id, _, _ in rows do
                        Expect.equal
                            (readRows (sprintf "SELECT name, age FROM indexed WHERE id = %d" id) |> List.sort)
                            (readRows (sprintf "SELECT name, age FROM unindexed WHERE id = %d" id) |> List.sort)
                            (sprintf "plain point lookup, id = %d" id)

                        Expect.equal
                            (readRows (sprintf "SELECT name, age FROM indexed WHERE id = %d AND age > 25" id) |> List.sort)
                            (readRows (sprintf "SELECT name, age FROM unindexed WHERE id = %d AND age > 25" id) |> List.sort)
                            (sprintf "index-narrowed candidate plus a residual filter, id = %d" id)

                    // A miss (no row has this id) must narrow to zero
                    // candidates, not accidentally fall through to "every
                    // row".
                    Expect.equal (readRows "SELECT name, age FROM indexed WHERE id = 9999") [] "a missed PK lookup returns no rows"

                    // A literal that needs real coercion (a numeric-looking
                    // string against the INT PK) can't safely use the
                    // index's encoding (see `tryUniqueLookup`'s doc) — must
                    // still fall back to a correct full scan, not silently
                    // drop the row.
                    Expect.equal
                        (readRows "SELECT name, age FROM indexed WHERE id = '3'" |> List.sort)
                        (readRows "SELECT name, age FROM unindexed WHERE id = '3'" |> List.sort)
                        "a string literal against an INT PK still matches correctly"

                testCase "point lookup by a UNIQUE column that isn't the table's first returns the row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (a INT, b INT, email VARCHAR(100) UNIQUE, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 2, 'x@y.z', 9), (3, 4, 'q@w.e', 8)" |> ignore

                    match runDefault store "SELECT v FROM t WHERE email = 'q@w.e'" with
                    | ResultSet([ "v" ], [ [ Some "8" ] ]) -> ()
                    | other -> failtestf "expected the point lookup on column index 2 to find v=8, got %A" other

                testCase "a non-unique one-column B-tree index narrows SELECT and UPDATE while residual predicates preserve scan semantics"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, category VARCHAR(20), score INT, KEY ix_category (category))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, category VARCHAR(20), score INT)" |> ignore

                    for sql in
                        [ "INSERT INTO indexed VALUES (1, 'Books', 10), (2, 'books', 30), (3, 'music', 40), (4, 'books', 20), (5, NULL, 50)"
                          "INSERT INTO scanned VALUES (1, 'Books', 10), (2, 'books', 30), (3, 'music', 40), (4, 'books', 20), (5, NULL, 50)" ] do
                        runDefault store sql |> ignore

                    let rows sql =
                        match runDefault store sql with
                        | ResultSet(_, values) -> values
                        | other -> failtestf "expected a resultset for %s, got %A" sql other

                    Expect.equal
                        (rows "SELECT id FROM indexed WHERE category = 'books' AND score >= 20")
                        (rows "SELECT id FROM scanned WHERE category = 'books' AND score >= 20")
                        "residual filtering agrees with a full scan"

                    Expect.equal
                        (runDefault store "UPDATE indexed SET category = 'archived' WHERE category = 'books' AND score = 20")
                        (Affected 1UL)
                        "one indexed row updates"

                    Expect.equal (rows "SELECT id FROM indexed WHERE category = 'books'") [ [ Some "1" ]; [ Some "2" ] ] "updated rows leave their old bucket"
                    Expect.equal (rows "SELECT id FROM indexed WHERE category = 'archived'") [ [ Some "4" ] ] "updated rows join their new bucket"
                    Expect.equal (runDefault store "UPDATE indexed SET score = 77 WHERE category = 'books' ORDER BY id DESC LIMIT 1") (Affected 1UL) "one indexed row updates with a limit"
                    Expect.equal (rows "SELECT id, score FROM indexed WHERE category = 'books' ORDER BY id") [ [ Some "1"; Some "10" ]; [ Some "2"; Some "77" ] ] "the limited update keeps the other bucket row"
                    Expect.equal (runDefault store "DELETE FROM indexed WHERE category = 'books' ORDER BY id LIMIT 1") (Affected 1UL) "one indexed row deletes with a limit"
                    Expect.equal (rows "SELECT id FROM indexed WHERE category = 'books'") [ [ Some "2" ] ] "deleted rows leave their bucket"
                    Expect.equal (rows "SELECT id FROM indexed WHERE category = NULL") [] "NULL equality has no matches"

                testCase "a composite B-tree narrows fully-bound equality predicates"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values.Head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE tasks (id INT PRIMARY KEY, tenant_id INT, status VARCHAR(20), KEY ix_tenant_status (tenant_id, status))" |> ignore
                    runDefault store "INSERT INTO tasks VALUES (1, 1, 'open'), (2, 1, 'done'), (3, 2, 'open'), (4, 2, 'done'), (5, 2, 'open')" |> ignore

                    match run store registry "SELECT id FROM tasks WHERE tenant_id = 2 AND status = 'open' AND TOUCH(id) = id ORDER BY id" with
                    | ResultSet(_, [ [ Some "3" ]; [ Some "5" ] ]) -> ()
                    | other -> failtestf "expected the composite-key rows, got %A" other

                    Expect.equal calls 3 "the residual runs for the metadata probe and composite-key candidates"

                    match runDefault store "EXPLAIN SELECT id FROM tasks WHERE tenant_id = 2 AND status = 'open'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "tasks"; None; Some "ref"; Some "ix_tenant_status"; Some "ix_tenant_status"; Some "88"; Some "const,const"; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a composite ref plan, got %A" other

                    Expect.equal
                        (runDefault store "UPDATE tasks SET status = 'queued' WHERE tenant_id = 2 AND status = 'open' AND id = 3")
                        (Affected 1UL)
                        "UPDATE consumes composite candidates"

                    match runDefault store "SELECT id FROM tasks WHERE tenant_id = 2 AND status = 'open'" with
                    | ResultSet(_, [ [ Some "5" ] ]) -> ()
                    | other -> failtestf "expected the remaining open row, got %A" other

                    Expect.equal
                        (runDefault store "DELETE FROM tasks WHERE tenant_id = 2 AND status = 'queued'")
                        (Affected 1UL)
                        "DELETE consumes composite candidates"

                testCase "a composite unique key produces a const lookup"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE assignments (id INT PRIMARY KEY, team_id INT, slot_id INT, UNIQUE KEY uq_team_slot (team_id, slot_id))" |> ignore
                    runDefault store "INSERT INTO assignments VALUES (1, 4, 7), (2, 4, 8)" |> ignore

                    match runDefault store "SELECT id FROM assignments WHERE team_id = 4 AND slot_id = 8" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected the composite unique row, got %A" other

                    match runDefault store "EXPLAIN SELECT id FROM assignments WHERE team_id = 4 AND slot_id = 8" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "assignments"; None; Some "const"; Some "uq_team_slot"; Some "uq_team_slot"; Some "10"; Some "const,const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a composite const plan, got %A" other

                testCase "a one-column B-tree range matches a scan twin and retains residual predicates"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, score INT, category VARCHAR(20), KEY ix_score (score))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, score INT, category VARCHAR(20))" |> ignore

                    for sql in
                        [ "INSERT INTO indexed VALUES (1, 40, 'a'), (2, 20, 'a'), (3, 30, 'b'), (4, 10, 'a'), (5, NULL, 'a')"
                          "INSERT INTO scanned VALUES (1, 40, 'a'), (2, 20, 'a'), (3, 30, 'b'), (4, 10, 'a'), (5, NULL, 'a')" ] do
                        runDefault store sql |> ignore

                    let rows tableName =
                        match runDefault store (sprintf "SELECT id FROM %s WHERE score >= 20 AND score < 40 AND category = 'a'" tableName) with
                        | ResultSet(_, rows) -> rows
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal (rows "indexed") (rows "scanned") "range candidates retain the residual filter"

                    let scanOrder tableName =
                        match runDefault store (sprintf "SELECT id FROM %s WHERE score >= 10 AND score <= 40" tableName) with
                        | ResultSet(_, rows) -> rows
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal (scanOrder "indexed") (scanOrder "scanned") "range narrowing retains the row-store order without ORDER BY"

                    match runDefault store "EXPLAIN SELECT id FROM indexed WHERE score >= 20 AND score < 40 AND category = 'a'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "indexed"; None; Some "range"; Some "ix_score"; Some "ix_score"; Some "5"; None; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a secondary-index range plan, got %A" other

                testCase "a secondary string range follows collation boundaries with a reversed lower comparison"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, label VARCHAR(20), KEY ix_label (label))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, label VARCHAR(20))" |> ignore

                    for sql in
                        [ "INSERT INTO indexed VALUES (1, 'Álpha'), (2, 'Bravo'), (3, 'bébé'), (4, 'charlie')"
                          "INSERT INTO scanned VALUES (1, 'Álpha'), (2, 'Bravo'), (3, 'bébé'), (4, 'charlie')" ] do
                        runDefault store sql |> ignore

                    let rows tableName =
                        match runDefault store (sprintf "SELECT id FROM %s WHERE 'b' <= label AND label < 'c'" tableName) with
                        | ResultSet(_, rows) -> rows
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal (rows "indexed") (rows "scanned") "collated string bounds match the scan path"

                testCase "a secondary B-tree streams direct ORDER BY ranges with NULLs, ties, and pagination"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, score INT, category VARCHAR(20), KEY ix_score (score))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, score INT, category VARCHAR(20))" |> ignore

                    for sql in
                        [ "INSERT INTO indexed VALUES (1, 2, 'keep'), (2, NULL, 'keep'), (3, 1, 'drop'), (4, 2, 'keep'), (5, NULL, 'keep'), (6, 2, 'keep')"
                          "INSERT INTO scanned VALUES (1, 2, 'keep'), (2, NULL, 'keep'), (3, 1, 'drop'), (4, 2, 'keep'), (5, NULL, 'keep'), (6, 2, 'keep')" ] do
                        runDefault store sql |> ignore

                    let rows tableName sql =
                        match runDefault store (sprintf sql tableName) with
                        | ResultSet(_, rows) -> rows
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal
                        (rows "indexed" "SELECT id, score FROM %s ORDER BY score ASC LIMIT 3 OFFSET 1")
                        (rows "scanned" "SELECT id, score FROM %s ORDER BY score ASC LIMIT 3 OFFSET 1")
                        "ascending index order retains NULL placement and offset"

                    Expect.equal
                        (rows "indexed" "SELECT id, score FROM %s WHERE score >= 1 AND score <= 2 AND category = 'keep' ORDER BY score DESC LIMIT 2 OFFSET 1")
                        (rows "scanned" "SELECT id, score FROM %s WHERE score >= 1 AND score <= 2 AND category = 'keep' ORDER BY score DESC LIMIT 2 OFFSET 1")
                        "descending index order retains ties, residuals, and range pagination"

                testCase "a secondary B-tree streams collated string ORDER BY ranges"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, label VARCHAR(20), KEY ix_label (label))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, label VARCHAR(20))" |> ignore

                    for sql in
                        [ "INSERT INTO indexed VALUES (1, 'Álpha'), (2, 'Bravo'), (3, 'bébé'), (4, 'charlie')"
                          "INSERT INTO scanned VALUES (1, 'Álpha'), (2, 'Bravo'), (3, 'bébé'), (4, 'charlie')" ] do
                        runDefault store sql |> ignore

                    let rows tableName =
                        match runDefault store (sprintf "SELECT id, label FROM %s WHERE label >= 'a' AND label < 'd' ORDER BY label ASC LIMIT 2 OFFSET 1" tableName) with
                        | ResultSet(_, rows) -> rows
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal (rows "indexed") (rows "scanned") "collated index order matches the scan path"

                testCase "a composite B-tree streams matching ORDER BY keys"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, priority INT, created_at INT, keep_row INT, KEY ix_priority_created (priority, created_at))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, priority INT, created_at INT, keep_row INT)" |> ignore

                    for table in [ "indexed"; "scanned" ] do
                        runDefault store (sprintf "INSERT INTO %s VALUES (1, 2, 30, 1), (2, 1, 40, 1), (3, 2, 10, 0), (4, 1, 20, 1), (5, 2, 10, 1), (6, NULL, 50, 1)" table) |> ignore

                    let rows table order =
                        match runDefault store (sprintf "SELECT id, priority, created_at FROM %s WHERE keep_row = 1 ORDER BY %s LIMIT 3 OFFSET 1" table order) with
                        | ResultSet(_, values) -> values
                        | other -> failtestf "expected ordered rows, got %A" other

                    Expect.equal
                        (rows "indexed" "priority, created_at")
                        (rows "scanned" "priority, created_at")
                        "ascending composite order matches a scan"

                    Expect.equal
                        (rows "indexed" "priority DESC, created_at DESC")
                        (rows "scanned" "priority DESC, created_at DESC")
                        "descending composite order matches a scan"

                    Expect.equal
                        (rows "indexed" "priority ASC, created_at DESC")
                        (rows "scanned" "priority ASC, created_at DESC")
                        "mixed directions fall back without changing results"

                    match runDefault store "EXPLAIN SELECT id FROM indexed ORDER BY priority, created_at LIMIT 3" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "indexed"; None; Some "index"; Some "ix_priority_created"; Some "ix_priority_created"; Some "10"; None; Some "6"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a composite ordered-index plan, got %A" other

                testCase "a point equality stays ahead of secondary index ordering"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values |> List.head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, score INT, KEY ix_score (score))" |> ignore

                    let values =
                        [ for i in 1 .. 200 -> sprintf "(%d, %d)" i i ]
                        |> String.concat ", "

                    runDefault store (sprintf "INSERT INTO t VALUES %s" values) |> ignore

                    match run store registry "SELECT id FROM t WHERE id = 200 AND TOUCH(id) = id ORDER BY score LIMIT 1" with
                    | ResultSet(_, [ [ Some "200" ] ]) -> ()
                    | other -> failtestf "expected the point row, got %A" other

                    Expect.isLessThan calls 3 "the residual runs only for the point candidate"

                testCase "EXPLAIN reports streamed secondary index ordering while point equality keeps its lookup plan"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, score INT, KEY ix_score (score))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 2), (2, 1), (3, 3)" |> ignore

                    match runDefault store "EXPLAIN SELECT id, score FROM t ORDER BY score LIMIT 2" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; None; Some "index"; Some "ix_score"; Some "ix_score"; Some "5"; None; Some "3"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected an ordered index plan, got %A" other

                    match runDefault store "EXPLAIN SELECT id, score FROM t WHERE score >= 2 ORDER BY score LIMIT 2" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; None; Some "range"; Some "ix_score"; Some "ix_score"; Some "5"; None; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected an ordered range plan, got %A" other

                    match runDefault store "SELECT id, score FROM t WHERE score >= 3 AND score < 1 ORDER BY score LIMIT 2" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected an empty contradictory range, got %A" other

                    match runDefault store "EXPLAIN SELECT id, score FROM t WHERE score >= 3 AND score < 1 ORDER BY score LIMIT 2" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; None; Some "range"; Some "ix_score"; Some "ix_score"; Some "5"; None; Some "0"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected an empty ordered range plan, got %A" other

                    match runDefault store "EXPLAIN SELECT id, score FROM t WHERE id = 2 ORDER BY score LIMIT 1" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; None; Some "const"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "const"; Some "1"; Some "100.00"; _ ] ]) ->
                        ()
                    | other -> failtestf "expected a point lookup plan, got %A" other

                testCase "primary and unique ranges narrow SELECT UPDATE and DELETE"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values |> List.head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, serial INT UNIQUE, score INT, KEY ix_score (score))" |> ignore

                    let values =
                        [ for i in 1 .. 1000 -> sprintf "(%d, %d, %d)" i (2000 + i) i ]
                        |> String.concat ", "

                    runDefault store (sprintf "INSERT INTO t VALUES %s" values) |> ignore

                    match run store registry "SELECT id FROM t WHERE id >= 995 AND TOUCH(id) = id ORDER BY id" with
                    | ResultSet(_, rows) -> Expect.equal rows.Length 6 "the primary range returns its suffix"
                    | other -> failtestf "expected primary range rows, got %A" other

                    Expect.isLessThan calls 10 "SELECT evaluates the residual only for primary candidates"
                    calls <- 0

                    match run store registry "UPDATE t SET score = score + 10 WHERE serial >= 2995 AND TOUCH(id) = id ORDER BY serial LIMIT 3" with
                    | Affected 3UL -> ()
                    | other -> failtestf "expected three unique-range updates, got %A" other

                    Expect.isLessThan calls 20 "UPDATE evaluates the residual only for unique candidates"
                    calls <- 0

                    match run store registry "DELETE FROM t WHERE score >= 1005 AND TOUCH(id) = id ORDER BY score LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected two secondary-range deletes, got %A" other

                    Expect.isLessThan calls 20 "DELETE evaluates the residual only for secondary candidates"

                    match runDefault store "SELECT id, score FROM t WHERE id >= 995 ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "997"; Some "1007" ]
                              [ Some "998"; Some "998" ]
                              [ Some "999"; Some "999" ]
                              [ Some "1000"; Some "1000" ] ]
                            "range mutations retain ORDER BY and LIMIT semantics"
                    | other -> failtestf "expected surviving rows, got %A" other

                testCase "EXPLAIN reports ref only for the secondary equality access path execution uses"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE books (id INT PRIMARY KEY, category VARCHAR(20), KEY ix_category (category))" |> ignore
                    runDefault store "INSERT INTO books VALUES (1, 'fiction'), (2, 'fiction'), (3, 'history')" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM books WHERE category = 'fiction'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "books"; None; Some "ref"; Some "ix_category"; Some "ix_category"; Some "83"; Some "const"; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a secondary-index ref plan, got %A" other

                    match runDefault store "EXPLAIN SELECT * FROM books WHERE category = 'fiction' AND id = 2" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "books"; None; Some "ref"; Some "ix_category"; Some "ix_category"; Some "83"; Some "const"; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected the plan to follow the first equality probe, got %A" other

                testCase "prefix equality indexes narrow candidates and retain residual semantics"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values |> List.head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, label VARCHAR(30), KEY ix_label (label(3)))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, label VARCHAR(30))" |> ignore

                    let values =
                        [ for id in 1 .. 500 ->
                              let label = if id = 499 then "Alphabet" elif id = 500 then "Alpine" else sprintf "value-%03d" id
                              sprintf "(%d, '%s')" id label ]
                        |> String.concat ", "

                    runDefault store (sprintf "INSERT INTO indexed VALUES %s" values) |> ignore
                    runDefault store (sprintf "INSERT INTO scanned VALUES %s" values) |> ignore

                    let query table =
                        run store registry (sprintf "SELECT id FROM %s WHERE label = 'ALPHABET' AND TOUCH(id) = id" table)

                    match query "indexed", query "scanned" with
                    | ResultSet(_, indexedRows), ResultSet(_, scannedRows) ->
                        Expect.equal indexedRows scannedRows "the prefix candidate path retains full equality"
                        Expect.equal indexedRows [ [ Some "499" ] ] "collation folding still applies"
                    | indexed, scanned -> failtestf "expected resultsets, got %A and %A" indexed scanned

                    Expect.isLessThan calls 510 "the indexed side only evaluates rows sharing the indexed prefix"

                    match runDefault store "EXPLAIN SELECT id FROM indexed WHERE label = 'Alps'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "indexed"; None; Some "ref"; Some "ix_label"; Some "ix_label"; Some "15"; Some "const"; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a prefix ref plan with residual filtering, got %A" other

                    runDefault store "CREATE TABLE latin_labels (id INT PRIMARY KEY, label VARCHAR(30) CHARACTER SET latin1, KEY ix_label (label(3)))" |> ignore
                    runDefault store "INSERT INTO latin_labels VALUES (1, 'abc')" |> ignore

                    match runDefault store "EXPLAIN SELECT id FROM latin_labels WHERE label = 'abc'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "latin_labels"; None; Some "ref"; Some "ix_label"; Some "ix_label"; Some "6"; Some "const"; Some "1"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected the latin1 prefix byte length, got %A" other

                testCase "unique prefix lookups keep the complete equality residual"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, label VARCHAR(30), UNIQUE KEY uq_label (label(3)))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'Alphabet'), (2, 'Beta')" |> ignore

                    Expect.equal
                        (runDefault store "SELECT id FROM t WHERE label = 'Alphabet'")
                        (ResultSet([ "id" ], [ [ Some "1" ] ]))
                        "an exact value survives the residual"

                    Expect.equal
                        (runDefault store "SELECT id FROM t WHERE label = 'Alpine'")
                        (ResultSet([ "id" ], []))
                        "a value sharing only the indexed prefix is rejected"

                    match runDefault store "EXPLAIN SELECT id FROM t WHERE label = 'Alpine'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; None; Some "const"; Some "uq_label"; Some "uq_label"; Some "15"; Some "const"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected a unique prefix const plan, got %A" other

                testCase "composite prefix probes survive mutations"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE indexed (id INT PRIMARY KEY, tenant_id INT, label VARCHAR(30), KEY ix_tenant_label (tenant_id, label(3)))" |> ignore
                    runDefault store "CREATE TABLE scanned (id INT PRIMARY KEY, tenant_id INT, label VARCHAR(30))" |> ignore

                    for table in [ "indexed"; "scanned" ] do
                        runDefault store (sprintf "INSERT INTO %s VALUES (1, 7, 'Alphabet'), (2, 7, 'Alpine'), (3, 8, 'Alphabet'), (4, 7, 'Beta')" table) |> ignore

                    let rows table label =
                        match runDefault store (sprintf "SELECT id FROM %s WHERE tenant_id = 7 AND label = '%s'" table label) with
                        | ResultSet(_, result) -> result
                        | other -> failtestf "expected a resultset, got %A" other

                    Expect.equal (rows "indexed" "Alphabet") (rows "scanned" "Alphabet") "composite prefix lookup matches a scan"
                    Expect.equal (rows "indexed" "Alphabet") [ [ Some "1" ] ] "the full label remains a residual"

                    match runDefault store "EXPLAIN SELECT id FROM indexed WHERE tenant_id = 7 AND label = 'Alphabet'" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "indexed"; None; Some "ref"; Some "ix_tenant_label"; Some "ix_tenant_label"; Some "20"; Some "const,const"; Some "2"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a composite prefix plan, got %A" other

                    Expect.equal
                        (runDefault store "UPDATE indexed SET label = 'Gamma' WHERE tenant_id = 7 AND label = 'Alphabet'")
                        (Affected 1UL)
                        "the prefix candidate drives UPDATE"

                    Expect.equal (rows "indexed" "Alphabet") [] "the old prefix bucket loses the row"
                    Expect.equal (rows "indexed" "Gamma") [ [ Some "1" ] ] "the new prefix bucket gains the row"

                testCase "prefix indexes probe inner joins without weakening equality"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (id INT PRIMARY KEY, label VARCHAR(30))" |> ignore
                    runDefault store "CREATE TABLE child (id INT PRIMARY KEY, label VARCHAR(30), KEY ix_label (label(3)))" |> ignore
                    runDefault store "INSERT INTO parent VALUES (1, 'Alphabet'), (2, 'Alps'), (3, 'Beta')" |> ignore
                    runDefault store "INSERT INTO child VALUES (10, 'Alphabet'), (11, 'Alpine'), (12, 'Beta')" |> ignore

                    match runDefault store "SELECT p.id, c.id FROM parent p JOIN child c ON p.label = c.label ORDER BY p.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "10" ]; [ Some "3"; Some "12" ] ] "the complete join predicate filters prefix collisions"
                    | other -> failtestf "expected joined rows, got %A" other

                    match runDefault store "EXPLAIN SELECT * FROM parent p JOIN child c ON p.label = c.label" with
                    | ResultSet(_, [ _; [ Some "1"; Some "SIMPLE"; Some "c"; None; Some "ref"; Some "ix_label"; Some "ix_label"; Some "15"; Some "p.label"; Some "1"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected an indexed prefix join plan, got %A" other

                testCase "an indexed INNER JOIN preserves NULL and residual ON semantics against a scan twin"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (id INT PRIMARY KEY, minimum_score INT)" |> ignore
                    runDefault store "CREATE TABLE indexed_child (id INT PRIMARY KEY, parent_id INT, score INT, KEY ix_parent (parent_id))" |> ignore
                    runDefault store "CREATE TABLE scanned_child (id INT PRIMARY KEY, parent_id INT, score INT)" |> ignore
                    runDefault store "INSERT INTO parent VALUES (1, 10), (2, 30), (3, 20)" |> ignore

                    for table in [ "indexed_child"; "scanned_child" ] do
                        runDefault store (sprintf "INSERT INTO %s VALUES (1, 1, 10), (2, 1, 9), (3, 2, 35), (4, 2, 20), (5, NULL, 99)" table) |> ignore

                    let rows table =
                        match
                            runDefault
                                store
                                (sprintf
                                    "SELECT p.id, c.id FROM parent p INNER JOIN %s c ON p.id = c.parent_id AND c.score >= p.minimum_score ORDER BY p.id, c.id"
                                    table)
                        with
                        | ResultSet(_, values) -> values
                        | other -> failtestf "expected an indexed or scanned join result, got %A" other

                    Expect.equal (rows "indexed_child") (rows "scanned_child") "the probe path agrees with the scan"
                    Expect.equal (rows "indexed_child") [ [ Some "1"; Some "1" ]; [ Some "2"; Some "3" ] ] "NULL keys and failed residuals do not join"

                    match runDefault store "EXPLAIN SELECT * FROM parent p INNER JOIN indexed_child c ON p.id = c.parent_id AND c.score >= p.minimum_score" with
                    | ResultSet(_, [ _; [ Some "1"; Some "SIMPLE"; Some "c"; None; Some "ref"; Some "ix_parent"; Some "ix_parent"; Some "5"; Some "p.id"; Some "1"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected the right side's real indexed join plan, got %A" other

                    match runDefault store "EXPLAIN SELECT * FROM parent p INNER JOIN indexed_child c ON p.id = c.id" with
                    | ResultSet(_, [ _; [ Some "1"; Some "SIMPLE"; Some "c"; None; Some "eq_ref"; Some "PRIMARY"; Some "PRIMARY"; Some "4"; Some "p.id"; Some "1"; Some "100.00"; None ] ]) ->
                        ()
                    | other -> failtestf "expected the unique right side to report eq_ref, got %A" other

                testCase "an indexed INNER JOIN probes a composite equality key"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (tenant_id INT, code INT, PRIMARY KEY (tenant_id, code))" |> ignore
                    runDefault store "CREATE TABLE child (id INT PRIMARY KEY, tenant_id INT, code INT, active INT, KEY ix_tenant_code (tenant_id, code))" |> ignore
                    runDefault store "INSERT INTO parent VALUES (1, 10), (1, 20), (2, 10)" |> ignore
                    runDefault store "INSERT INTO child VALUES (1, 1, 10, 1), (2, 1, 10, 0), (3, 1, 20, 1), (4, 2, 10, 1), (5, 2, 20, 1), (6, NULL, 10, 1)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT p.tenant_id, p.code, c.id FROM parent p INNER JOIN child c ON p.code = c.code AND p.tenant_id = c.tenant_id AND c.active = 1 ORDER BY c.id"
                    with
                    | ResultSet(_, [ [ Some "1"; Some "10"; Some "1" ]; [ Some "1"; Some "20"; Some "3" ]; [ Some "2"; Some "10"; Some "4" ] ]) ->
                        ()
                    | other -> failtestf "expected composite join rows, got %A" other

                    match runDefault store "EXPLAIN SELECT * FROM parent p INNER JOIN child c ON p.code = c.code AND p.tenant_id = c.tenant_id AND c.active = 1" with
                    | ResultSet(_, [ _; [ Some "1"; Some "SIMPLE"; Some "c"; None; Some "ref"; Some "ix_tenant_code"; Some "ix_tenant_code"; Some "10"; Some "p.tenant_id,p.code"; Some "1"; Some "100.00"; Some "Using where" ] ]) ->
                        ()
                    | other -> failtestf "expected a composite indexed join plan, got %A" other

                testCase "a composite PRIMARY KEY (out of the single-column fast path's scope) still returns correct results"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE composite (a INT, b INT, v VARCHAR(10), PRIMARY KEY (a, b))" |> ignore
                    runDefault store "INSERT INTO composite VALUES (1, 1, 'x'), (1, 2, 'y'), (2, 1, 'z')" |> ignore

                    match runDefault store "SELECT v FROM composite WHERE a = 1" with
                    | ResultSet([ "v" ], rows) -> Expect.equal (rows |> List.sort) [ [ Some "x" ]; [ Some "y" ] ] "a = 1 alone (not the whole composite key) still scans correctly"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "INSERT/UPDATE unique-collision decisions (indexed PRIMARY KEY) match an independent full-scan oracle, incl. collation edge cases and NULL-never-collides"
                <| fun _ ->
                    let rnd = System.Random(20260817)

                    // Independent of Storage.encodeConstraintKey on purpose
                    // — this is MySQL 8's own collation rule
                    // (utf8mb4_0900_ai_ci: case-insensitive, NO PAD —
                    // trailing spaces stay distinct), reimplemented directly
                    // against .NET string comparison rather than reusing
                    // any engine internals, so it can catch a real
                    // disagreement between the index's encoding and the
                    // engine's WHERE-equality semantics.
                    let collide (a: string option) (b: string option) =
                        match a, b with
                        | Some x, Some y -> System.String.Equals(x, y, System.StringComparison.OrdinalIgnoreCase)
                        | _ -> false // NULL never collides with anything, including another NULL

                    for _ in 1 .. 20 do
                        let store = newStore ()
                        runDefault store "CREATE TABLE uniq (k VARCHAR(10) UNIQUE, v INT)" |> ignore

                        let candidates = [ "Foo"; "foo  "; "FOO"; "bar"; "bar "; "Baz"; "baz" ]
                        let mutable accepted : string option list = []

                        for _ in 1 .. 15 do
                            let key = if rnd.Next(4) = 0 then None else Some candidates.[rnd.Next(candidates.Length)]
                            let literal = key |> Option.map (sprintf "'%s'") |> Option.defaultValue "NULL"

                            let expectCollision = key |> Option.exists (fun k -> accepted |> List.exists (collide (Some k)))

                            match runDefault store (sprintf "INSERT INTO uniq VALUES (%s, %d)" literal (rnd.Next(100))) with
                            | Affected 1UL ->
                                Expect.isFalse expectCollision (sprintf "engine accepted %A but the oracle says it collides with %A" key accepted)
                                accepted <- key :: accepted
                            | Err(1062, _) -> Expect.isTrue expectCollision (sprintf "engine rejected %A as a duplicate but the oracle found no collision in %A" key accepted)
                            | other -> failtestf "expected Affected 1 or a 1062 duplicate-key error, got %A" other

                testCase "FK parent lookup falls back safely for unchecked legacy metadata"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE parentIndexed (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE parentPlain (id INT)" |> ignore
                    setForeignKeyChecks store false
                    runDefault store "CREATE TABLE childIndexed (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parentIndexed(id))" |> ignore
                    runDefault store "CREATE TABLE childPlain (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parentPlain(id))" |> ignore
                    setForeignKeyChecks store true
                    runDefault store "INSERT INTO parentIndexed VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO parentPlain VALUES (1), (2), (3)" |> ignore

                    for parentId in [ 1; 2; 3; 999 ] do
                        let indexed = runDefault store (sprintf "INSERT INTO childIndexed VALUES (%d, %d)" parentId parentId)
                        let scanned = runDefault store (sprintf "INSERT INTO childPlain VALUES (%d, %d)" parentId parentId)

                        match indexed, scanned with
                        | Affected 1UL, Affected 1UL -> ()
                        | Err(1452, _), Err(1452, _) -> ()
                        | _ -> failtestf "indexed and scanned lookups disagreed for %d: %A and %A" parentId indexed scanned

                testCase "FK parent probes allocate independently of the parent's size"
                <| fun _ ->
                    let allocatedByInserts parentRows =
                        let store = newStore ()
                        runDefault store "CREATE TABLE parent (id INT PRIMARY KEY)" |> ignore
                        runDefault store "CREATE TABLE child (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parent(id))" |> ignore

                        let batch = [ for i in 1 .. parentRows -> sprintf "(%d)" i ] |> String.concat ", "
                        runDefault store (sprintf "INSERT INTO parent VALUES %s" batch) |> ignore
                        runDefault store (sprintf "INSERT INTO child VALUES (1, %d)" parentRows) |> ignore

                        let before = System.GC.GetAllocatedBytesForCurrentThread()

                        for i in 2 .. 200 do
                            runDefault store (sprintf "INSERT INTO child VALUES (%d, %d)" i (parentRows - i + 1)) |> ignore

                        System.GC.GetAllocatedBytesForCurrentThread() - before

                    let at2k = allocatedByInserts 2_000
                    let at20k = allocatedByInserts 20_000
                    let ratio = float at20k / max (float at2k) 1.0

                    Expect.isLessThan
                        ratio
                        2.0
                        (sprintf
                            "FK inserts allocated %d bytes at 2k parent rows and %d bytes at 20k (ratio %f) — looks linear in parent size again"
                            at2k
                            at20k
                            ratio)

                testCase "point SELECT by PRIMARY KEY on a 50,000-row table stays flat, not linear in table size"
                <| fun _ ->
                    // The generous bound separates indexed access from a full
                    // scan; it is not a latency target.
                    let store = newStore ()
                    runDefault store "CREATE TABLE points (id INT PRIMARY KEY, name VARCHAR(20))" |> ignore

                    let batch = [ for i in 1 .. 50_000 -> sprintf "(%d, 'name%d')" i i ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO points VALUES %s" batch) |> ignore

                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let result = runDefault store "SELECT name FROM points WHERE id = 25000"
                    sw.Stop()

                    match result with
                    | ResultSet([ "name" ], [ [ Some "name25000" ] ]) -> ()
                    | other -> failtestf "expected exactly the row for id = 25000, got %A" other

                    if not (TestSupport.skipTimingAssertions ()) then
                        Expect.isLessThan
                            sw.Elapsed.TotalMilliseconds
                            20.0
                            (sprintf "point SELECT by PK against 50,000 rows took %A — looks like a full scan again" sw.Elapsed)

                testCase "point SELECT by PRIMARY KEY latency is flat from 10k to 40k rows, the actual O(1) proof, not just a single-size bound"
                <| fun _ ->
                    // A single-size bound (generous, at one n) is consistent
                    // with a merely-faster linear scan; the thing that
                    // actually proves the index is O(1) is this ratio staying
                    // near 1 across a 4x size increase instead of tracking
                    // it.
                    let timeLookup (n: int) : float =
                        let store = newStore ()
                        runDefault store "CREATE TABLE flat (id INT PRIMARY KEY, name VARCHAR(20))" |> ignore
                        let batch = [ for i in 1 .. n -> sprintf "(%d, 'name%d')" i i ] |> String.concat ", "
                        runDefault store (sprintf "INSERT INTO flat VALUES %s" batch) |> ignore

                        // Warm up (JIT) before timing; median of several
                        // probes to smooth out GC/scheduler noise at this
                        // small a per-call cost.
                        runDefault store (sprintf "SELECT name FROM flat WHERE id = %d" (n / 2)) |> ignore

                        [ for _ in 1 .. 21 ->
                              let sw = System.Diagnostics.Stopwatch.StartNew()
                              runDefault store (sprintf "SELECT name FROM flat WHERE id = %d" (n / 2)) |> ignore
                              sw.Stop()
                              sw.Elapsed.TotalMilliseconds ]
                        |> List.sort
                        |> List.item 10 // median of 21

                    let at10k = timeLookup 10_000
                    let at40k = timeLookup 40_000

                    // An O(n) scan would show ~4x here (1.315ms @10k ->
                    // 7.287ms @40k, a 5.5x ratio); a real O(1)/O(log n)
                    // index stays well under that. Floored by the harness's
                    // own per-call noise at this scale, so the bound is
                    // generous, not tight.
                    let ratio = at40k / (max at10k 0.001)

                    if not (TestSupport.skipTimingAssertions ()) then
                        Expect.isLessThan
                            ratio
                            2.5
                            (sprintf
                                "point SELECT by PK took %fms at 10k rows and %fms at 40k rows (ratio %f) — looks linear in table size again"
                                at10k
                                at40k
                                ratio)

                testCase "a correlated subquery's outer equality never gets mistaken for the inner table's own index probe"
                <| fun _ ->
                    // Same indexed-vs-unindexed-twin method as the top-level
                    // test above, but the equality lives inside a correlated
                    // subquery, ANDed alongside a *genuine* correlation on
                    // the inner table — and the outer column (`users.id`)
                    // shares its name with the inner table's own indexed
                    // column (`posts.id`). A bare/qualified `id = <literal>`
                    // belonging to the OUTER query must not be treated as a
                    // probe into the INNER table's PK index.
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE postsIndexed (id INT PRIMARY KEY, user_id INT)" |> ignore
                    runDefault store "CREATE TABLE postsUnindexed (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (5, 'eve')" |> ignore
                    // Deliberately no row whose id equals the outer literal
                    // (5), so a correct correlated EXISTS depends only on
                    // `user_id = users.id`, not on `posts.id`.
                    runDefault store "INSERT INTO postsIndexed VALUES (1, 5), (2, 1)" |> ignore
                    runDefault store "INSERT INTO postsUnindexed VALUES (1, 5), (2, 1)" |> ignore

                    let existsIndexed =
                        runDefault
                            store
                            "SELECT id FROM users WHERE EXISTS (SELECT 1 FROM postsIndexed WHERE postsIndexed.user_id = users.id AND users.id = 5)"

                    let existsUnindexed =
                        runDefault
                            store
                            "SELECT id FROM users WHERE EXISTS (SELECT 1 FROM postsUnindexed WHERE postsUnindexed.user_id = users.id AND users.id = 5)"

                    Expect.equal existsIndexed existsUnindexed "EXISTS: indexed and unindexed inner tables must agree"

                    match existsIndexed with
                    | ResultSet([ "id" ], rows) -> Expect.equal (rows |> List.sort) [ [ Some "5" ] ] "only the outer row whose id = 5 has a matching post"
                    | other -> failtestf "expected a resultset, got %A" other

                    let scalarIndexed =
                        runDefault
                            store
                            "SELECT id, (SELECT COUNT(*) FROM postsIndexed WHERE postsIndexed.user_id = users.id AND users.id = 5) AS c FROM users ORDER BY id"

                    let scalarUnindexed =
                        runDefault
                            store
                            "SELECT id, (SELECT COUNT(*) FROM postsUnindexed WHERE postsUnindexed.user_id = users.id AND users.id = 5) AS c FROM users ORDER BY id"

                    Expect.equal scalarIndexed scalarUnindexed "scalar subquery: indexed and unindexed inner tables must agree"

                    match scalarIndexed with
                    | ResultSet([ "id"; "c" ], rows) -> Expect.equal rows [ [ Some "1"; Some "0" ]; [ Some "5"; Some "1" ] ] "user 5 has exactly one matching post, user 1 has none"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "a correlated equality probes the inner secondary index"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values.Head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE orders (id INT PRIMARY KEY, user_id INT, KEY ix_orders_user (user_id))" |> ignore
                    runDefault store ("INSERT INTO users VALUES " + ([ 1..50 ] |> List.map (sprintf "(%d)") |> String.concat ",")) |> ignore

                    [ 1..1000 ]
                    |> List.map (fun id -> sprintf "(%d,%d)" id (((id - 1) % 50) + 1))
                    |> String.concat ","
                    |> fun rows -> runDefault store ("INSERT INTO orders VALUES " + rows)
                    |> ignore

                    match
                        run
                            store
                            registry
                            "SELECT users.id, (SELECT COUNT(*) FROM orders WHERE orders.user_id = users.id AND TOUCH(orders.id) = orders.id) AS c FROM users ORDER BY users.id"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows.Length 50 "every outer row is retained"
                        Expect.isTrue (rows |> List.forall (fun row -> row.[1] = Some "20")) "each user has twenty orders"
                    | other -> failtestf "expected correlated counts, got %A" other

                    Expect.isLessThan calls 2000 "the residual evaluates only indexed candidates, not the inner table for every outer row"

                testCase "an unindexed correlated equality materializes keyed candidates once"
                <| fun _ ->
                    let mutable calls = 0

                    let registry =
                        builtins
                        |> registerScalar "TOUCH" (fun values ->
                            calls <- calls + 1
                            values.Head)

                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE orders (id INT PRIMARY KEY, user_id INT)" |> ignore
                    runDefault store ("INSERT INTO users VALUES " + ([ 1..50 ] |> List.map (sprintf "(%d)") |> String.concat ",")) |> ignore

                    [ 1..1000 ]
                    |> List.map (fun id -> sprintf "(%d,%d)" id (((id - 1) % 50) + 1))
                    |> String.concat ","
                    |> fun rows -> runDefault store ("INSERT INTO orders VALUES " + rows)
                    |> ignore

                    match
                        run
                            store
                            registry
                            "SELECT users.id FROM users WHERE EXISTS (SELECT 1 FROM orders WHERE orders.user_id = users.id AND TOUCH(orders.id) = -1) ORDER BY users.id"
                    with
                    | ResultSet(_, rows) -> Expect.isEmpty rows "the residual rejects every candidate"
                    | other -> failtestf "expected an empty resultset, got %A" other

                    Expect.isLessThan calls 1100 "residual work stays proportional to the inner rows plus projection inference" ]

          testList
              "streaming SELECT pipeline"
              [ let resultRows (result: QueryResult) : string option list list =
                    match result with
                    | ResultSet(_, rows) -> rows
                    | other -> failtestf "expected a resultset, got %A" other

                testCase
                    "streaming LIMIT/OFFSET (no ORDER BY), plain or DISTINCT, plain scan or JOIN, matches a full-materialize-then-slice reference over randomized data"
                <| fun _ ->
                    let rnd = System.Random(20260817)

                    for _ in 1 .. 40 do
                        let store = newStore ()
                        runDefault store "CREATE TABLE l (id INT PRIMARY KEY, k INT, x INT)" |> ignore
                        runDefault store "CREATE TABLE r (id INT PRIMARY KEY, k INT, y INT)" |> ignore

                        let leftRows = [ for i in 1 .. rnd.Next(20, 300) -> i, rnd.Next(0, 8), rnd.Next(0, 5) ]
                        let rightRows = [ for i in 1 .. rnd.Next(20, 300) -> i, rnd.Next(0, 8), rnd.Next(0, 5) ]
                        let valuesOf rows = rows |> List.map (fun (i, k, v) -> sprintf "(%d,%d,%d)" i k v) |> String.concat ", "
                        runDefault store (sprintf "INSERT INTO l VALUES %s" (valuesOf leftRows)) |> ignore
                        runDefault store (sprintf "INSERT INTO r VALUES %s" (valuesOf rightRows)) |> ignore

                        let distinct = if rnd.Next(0, 2) = 0 then "DISTINCT " else ""
                        let threshold = rnd.Next(0, 5)

                        let baseSql =
                            if rnd.Next(0, 2) = 0 then
                                sprintf "SELECT %sl.x, r.y FROM l JOIN r ON l.k = r.k WHERE l.x >= %d" distinct threshold
                            else
                                sprintf "SELECT %sid, x FROM l WHERE x >= %d" distinct threshold

                        let full = runDefault store baseSql |> resultRows
                        let limit = rnd.Next(1, max 2 (List.length full + 5))
                        let offset = rnd.Next(0, List.length full + 10)
                        let limited = runDefault store (sprintf "%s LIMIT %d OFFSET %d" baseSql limit offset) |> resultRows
                        let expected = full |> List.skip (min offset (List.length full)) |> List.truncate limit

                        // SQL doesn't define which subset a no-ORDER-BY LIMIT
                        // picks, but fsdb's own scan order is deterministic
                        // for two runs against the same unmodified table —
                        // the streaming path and a full materialize-then-
                        // slice of the *same* engine must still agree
                        // exactly, not merely as sets.
                        Expect.equal limited expected (sprintf "sql=%s limit=%d offset=%d" baseSql limit offset)

                testCase "a chain of two equi-JOINs (3 tables) matches a nested-loop reference over randomized data"
                <| fun _ ->
                    // The second JOIN's left side is the first JOIN's own
                    // (possibly lazy) output rows, not a plain table scan —
                    // this is the shape that exercises re-deriving that
                    // input rather than just reading it once.
                    let rnd = System.Random(20260817)

                    for _ in 1 .. 40 do
                        let store = newStore ()
                        runDefault store "CREATE TABLE a (id INT PRIMARY KEY, k INT)" |> ignore
                        runDefault store "CREATE TABLE b (id INT PRIMARY KEY, k INT)" |> ignore
                        runDefault store "CREATE TABLE c (id INT PRIMARY KEY, k INT)" |> ignore

                        let mkRows n = [ for i in 1 .. rnd.Next(5, 40) -> i, rnd.Next(0, n) ]
                        let aRows, bRows, cRows = mkRows 6, mkRows 6, mkRows 6
                        let valuesOf rows = rows |> List.map (fun (i, k) -> sprintf "(%d,%d)" i k) |> String.concat ", "
                        runDefault store (sprintf "INSERT INTO a VALUES %s" (valuesOf aRows)) |> ignore
                        runDefault store (sprintf "INSERT INTO b VALUES %s" (valuesOf bRows)) |> ignore
                        runDefault store (sprintf "INSERT INTO c VALUES %s" (valuesOf cRows)) |> ignore

                        let actual =
                            runDefault store "SELECT a.id, b.id, c.id FROM a JOIN b ON a.k = b.k JOIN c ON b.k = c.k"
                            |> resultRows
                            |> List.sort

                        let expected =
                            [ for ai, ak in aRows do
                                  for bi, bk in bRows do
                                      if ak = bk then
                                          for ci, ck in cRows do
                                              if bk = ck then
                                                  yield [ Some(string ai); Some(string bi); Some(string ci) ] ]
                            |> List.sort

                        Expect.equal actual expected "3-table equi-JOIN chain must match a nested-loop reference, as a set"

                testCase
                    "bounded top-N for ORDER BY + LIMIT (+ OFFSET, incl. ties and OFFSET past the end) matches a full sort-then-slice reference over randomized data"
                <| fun _ ->
                    let rnd = System.Random(20260817)

                    for _ in 1 .. 40 do
                        let store = newStore ()
                        runDefault store "CREATE TABLE t (id INT PRIMARY KEY, k INT, x INT)" |> ignore

                        // Small key range on purpose: forces genuine ties in
                        // the sort key, exercising boundedTopN's tie
                        // stability at the capacity boundary — a single sort
                        // key (not `k, id`) means the comparator itself
                        // returns 0 for tied rows, not just "coincidentally
                        // adjacent after a secondary key breaks it".
                        let rows = [ for i in 1 .. rnd.Next(20, 400) -> i, rnd.Next(0, 6), rnd.Next(0, 100) ]
                        let valuesOf rows = rows |> List.map (fun (i, k, x) -> sprintf "(%d,%d,%d)" i k x) |> String.concat ", "
                        runDefault store (sprintf "INSERT INTO t VALUES %s" (valuesOf rows)) |> ignore

                        let dir = if rnd.Next(0, 2) = 0 then "ASC" else "DESC"
                        let baseSql = sprintf "SELECT id, k, x FROM t WHERE x >= %d ORDER BY k %s" (rnd.Next(0, 50)) dir

                        let full = runDefault store baseSql |> resultRows
                        let limit = rnd.Next(1, max 2 (List.length full + 5))
                        let offset = rnd.Next(0, List.length full + 10)
                        let limited = runDefault store (sprintf "%s LIMIT %d OFFSET %d" baseSql limit offset) |> resultRows
                        let expected = full |> List.skip (min offset (List.length full)) |> List.truncate limit

                        Expect.equal limited expected (sprintf "sql=%s limit=%d offset=%d" baseSql limit offset)

                testCase "ORDER BY + LIMIT with MySQL's max LIMIT (the \"OFFSET with no LIMIT\" pagination idiom) still returns rows, not an overflowed-capacity empty set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)" |> ignore

                    match runDefault store "SELECT id FROM t ORDER BY id LIMIT 18446744073709551615 OFFSET 3" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "4" ]; [ Some "5" ]; [ Some "6" ]; [ Some "7" ]; [ Some "8" ]; [ Some "9" ]; [ Some "10" ]; [ Some "11" ]; [ Some "12" ] ] "rows 4..12, same as a plain OFFSET 3 with no LIMIT"
                    | other -> failtestf "expected a resultset, got %A" other

                    match runDefault store "SELECT id FROM t ORDER BY id LIMIT 18446744073709551615" with
                    | ResultSet(_, rows) -> Expect.equal (List.length rows) 12 "the full table, not an array-dimensions fault from preallocating the clamped LIMIT"
                    | other -> failtestf "expected a resultset, got %A" other

                    match runDefault store "SELECT id FROM t ORDER BY id LIMIT 3 OFFSET 2147483640" with
                    | ResultSet(_, rows) -> Expect.equal rows [] "an OFFSET past the table's end still returns cleanly, not an overflow fault"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "LIMIT N against a WHERE matching nearly the whole table touches far fewer rows than the table size"
                <| fun _ ->
                    let mutable touched = 0

                    let touch (args: Value list) : Value =
                        touched <- touched + 1
                        List.head args

                    let registry = registerScalar "TOUCH" touch builtins
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY)" |> ignore
                    let n = 20_000
                    let values = [ for i in 1 .. n -> sprintf "(%d)" i ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO t VALUES %s" values) |> ignore

                    touched <- 0

                    match run store registry "SELECT id FROM t WHERE TOUCH(id) > 0 LIMIT 10" with
                    | ResultSet(_, rows) -> Expect.equal (List.length rows) 10 "LIMIT 10 returns exactly 10 rows"
                    | other -> failtestf "expected a resultset, got %A" other

                    Expect.isLessThan
                        touched
                        1000
                        (sprintf "LIMIT 10 against a %d-row table touched %d rows via WHERE — the streaming short-circuit isn't firing" n touched)

                testCase "LIMIT N against an equi-JOIN's WHERE matching nearly every combined row touches far fewer combined rows than the join itself"
                <| fun _ ->
                    // The hash-eligible INNER/CROSS join path streams its
                    // matched pairs straight into WHERE/LIMIT instead of
                    // combining every left×right match up front — same
                    // short-circuit proof as the plain-scan test above, but
                    // through `applyJoin`'s probe side instead of a bare
                    // table scan.
                    let mutable touched = 0

                    let touch (args: Value list) : Value =
                        touched <- touched + 1
                        List.head args

                    let registry = registerScalar "TOUCH" touch builtins
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE o (id INT PRIMARY KEY, user_id INT)" |> ignore
                    let n = 5_000
                    let uValues = [ for i in 1 .. n -> sprintf "(%d)" i ] |> String.concat ", "
                    let oValues = [ for i in 1 .. n -> sprintf "(%d,%d)" i i ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO u VALUES %s" uValues) |> ignore
                    runDefault store (sprintf "INSERT INTO o VALUES %s" oValues) |> ignore

                    touched <- 0

                    match run store registry "SELECT u.id FROM u JOIN o ON o.user_id = u.id WHERE TOUCH(u.id) > 0 LIMIT 10" with
                    | ResultSet(_, rows) -> Expect.equal (List.length rows) 10 "LIMIT 10 returns exactly 10 rows"
                    | other -> failtestf "expected a resultset, got %A" other

                    Expect.isLessThan
                        touched
                        1000
                        (sprintf "LIMIT 10 against a %d-row equi-JOIN touched %d combined rows via WHERE — the join's own streaming short-circuit isn't firing" n touched)

                testCase
                    "a row-level error past what LIMIT needs never surfaces (no ORDER BY); ORDER BY forcing a full scan still raises it — verified against a live MySQL 8.4 oracle"
                <| fun _ ->
                    // Oracle check (MySQL 8.4.11, `CAST(bad_json AS JSON)`
                    // with the poison row at scan position 100 of a
                    // `WHERE id <= 150` match set): a plain `LIMIT 10`/
                    // `LIMIT 150` pair reproduces exactly this split — the
                    // 10-row query returns cleanly, the 150-row one raises
                    // 3141 — and `ORDER BY` on an unindexed column (forcing
                    // a filesort) raises even at `LIMIT 10`, because the
                    // filesort evaluates every matched row's projection
                    // before `LIMIT` trims the sorted result.
                    let poison (args: Value list) : Value =
                        match args with
                        | [ VInt 100L ] -> failwith "poison"
                        | [ v ] -> v
                        | _ -> VNull

                    let registry = registerScalar "POISON" poison builtins
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, x INT)" |> ignore
                    let values = [ for i in 1 .. 200 -> sprintf "(%d, %d)" i (if i = 100 then 0 else 1) ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO t VALUES %s" values) |> ignore

                    match run store registry "SELECT id FROM t WHERE POISON(id) IS NOT NULL LIMIT 10" with
                    | ResultSet(_, rows) -> Expect.equal (List.length rows) 10 "first 10 rows come back untouched by the poison row"
                    | other -> failtestf "expected a resultset, got %A" other

                    Expect.throws
                        (fun () -> run store registry "SELECT id FROM t WHERE POISON(id) IS NOT NULL LIMIT 150" |> ignore)
                        "a LIMIT that reaches the poison row's scan position must still evaluate it"

                    Expect.throws
                        (fun () -> run store registry "SELECT POISON(id) FROM t WHERE id <= 150 ORDER BY x LIMIT 10" |> ignore)
                        "ORDER BY forcing a filesort must still evaluate every matched row's projection" ]

          testList
              "out-of-range temporal functions yield NULL, not an exception"
              [ testCase "DATE_ADD past DateTime's range is NULL like MySQL"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT DATE_ADD('2020-01-01', INTERVAL 100000 YEAR)" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL, got %A" other

                testCase "DATE_ADD with an enormous month interval is NULL"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT DATE_ADD(NOW(), INTERVAL 1000000000000000000 MONTH)" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL, got %A" other

                testCase "FROM_UNIXTIME past the representable range is NULL"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT FROM_UNIXTIME(1000000000000000000)" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL, got %A" other ]

          testList
              "resource-exhaustion guards"
              [ testCase "LIKE with thousands of wildcards returns without a stack overflow"
                <| fun _ ->
                    let store = newStore ()
                    let pat = String.replicate 20000 "%"

                    match runDefault store (sprintf "SELECT 'abc' LIKE '%s' AS m" pat) with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a match without crashing, got %A" other

                // A trailing escape must terminate even though it consumes no
                // subject input; otherwise one client pattern can spin.
                testCase "a pattern ending in a trailing escape terminates"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store @"SELECT 'x' LIKE '%\%' AS no_match, 'x%' LIKE '%\%' AS literal_percent" with
                    | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected 0 then 1, got %A" other

                testCase "LIKE over a long subject with a leading wildcard does not overflow"
                <| fun _ ->
                    let store = newStore ()
                    let subj = String.replicate 50000 "a" + "z"

                    match runDefault store (sprintf "SELECT '%s' LIKE '%%z' AS m" subj) with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a tail match, got %A" other

                testCase "an all-empty UNION returns an empty set, not an internal error"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT 1 WHERE FALSE UNION SELECT 2 WHERE FALSE" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected an empty resultset, got %A" other

                testCase "a point lookup on a UNIQUE column that isn't column 0 doesn't throw"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, email VARCHAR(255) UNIQUE)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'a@b.com')" |> ignore

                    match runDefault store "SELECT id FROM t WHERE email = 'a@b.com'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected the row via the unique index, got %A" other

                testCase "an FK whose parent key isn't column 0 validates without throwing"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE p (name VARCHAR(50), code INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO p VALUES ('x', 5)" |> ignore
                    runDefault store "CREATE TABLE c (id INT PRIMARY KEY, pcode INT, CONSTRAINT fk FOREIGN KEY (pcode) REFERENCES p (code))" |> ignore

                    match runDefault store "INSERT INTO c VALUES (1, 5)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the child insert to satisfy the FK, got %A" other

                    match runDefault store "INSERT INTO c VALUES (2, 999)" with
                    | Err(1452, _) -> ()
                    | other -> failtestf "expected 1452 for a missing parent key, got %A" other ]

          // Every case below mirrors a probe pinned against the MySQL 8.4.11
          // oracle (see `Executor.jsonTableRows`' doc for the semantics).
          testList
              "JSON_TABLE"
              [ testCase "uncorrelated expansion: ordinality, coercion, NULL on error"
                <| fun _ ->
                    // Oracle: 10 → (10,'10'); "abc" → (NULL,'abc'); null →
                    // (NULL,NULL); {"a":1} into scalars → (NULL,NULL);
                    // "7" → (7,'7').
                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.o, jt.x, jt.s FROM JSON_TABLE('[10,\"abc\",null,{\"a\":1},\"7\"]', '$[*]' COLUMNS (o FOR ORDINALITY, x INT PATH '$', s VARCHAR(20) PATH '$')) jt"
                    with
                    | ResultSet([ "o"; "x"; "s" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10"; Some "10" ]
                              [ Some "2"; None; Some "abc" ]
                              [ Some "3"; None; None ]
                              [ Some "4"; None; None ]
                              [ Some "5"; Some "7"; Some "7" ] ]
                            "one row per array element, NULL where coercion fails"
                    | other -> failtestf "expected 5 expanded rows, got %A" other

                testCase "malformed document is error 3141 with MySQL's message prefix"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT * FROM JSON_TABLE('not json', '$[*]' COLUMNS (x INT PATH '$')) jt" with
                    | Err(3141, message) ->
                        Expect.stringStarts
                            message
                            "Invalid JSON text in argument 1 to function json_table"
                            "MySQL's 3141 wording"
                    | other -> failtestf "expected error 3141, got %A" other

                testCase "a scalar under $[*] expands to zero rows"
                <| fun _ ->
                    // Oracle: `{"items":5}` under `$.items[*]` → 0 rows (no
                    // auto-wrap of a scalar).
                    match
                        runDefault (newStore ()) "SELECT jt.x FROM JSON_TABLE('{\"items\":5}', '$.items[*]' COLUMNS (x INT PATH '$')) jt"
                    with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero rows, got %A" other

                testCase "an invalid DECIMAL scale takes the column's ON ERROR path"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT * FROM JSON_TABLE('[1]', '$[*]' COLUMNS (x DECIMAL(1,10000) PATH '$')) jt" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL for the invalid DECIMAL column, got %A" other

                testCase "a NULL document expands to zero rows"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT jt.x FROM JSON_TABLE(NULL, '$[*]' COLUMNS (x INT PATH '$')) jt" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero rows, got %A" other

                testCase "a missing column path yields NULL (the ON EMPTY default)"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.a, jt.m FROM JSON_TABLE('[{\"a\":5}]', '$[*]' COLUMNS (a INT PATH '$.a', m INT PATH '$.missing')) jt"
                    with
                    | ResultSet(_, [ [ Some "5"; None ] ]) -> ()
                    | other -> failtestf "expected (5, NULL), got %A" other

                testCase "a JSON column keeps MySQL's spaced JSON text"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT jt.j FROM JSON_TABLE('[{\"a\":5}]', '$[*]' COLUMNS (j JSON PATH '$')) jt" with
                    | ResultSet(_, [ [ Some "{\"a\": 5}" ] ]) -> ()
                    | other -> failtestf "expected the MySQL-formatted object, got %A" other

                testCase "correlated comma-join: per-row expansion, ordinality restart, NULL/no-match rows dropped"
                <| fun _ ->
                    // Oracle: (1,'[1,2]') expands twice with o restarting at
                    // 1 for (2,'[3]'); the NULL doc and the object-under-[*]
                    // rows vanish entirely (inner semantics).
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore

                    runDefault store "INSERT INTO t VALUES (1, '[1,2]'), (2, '[3]'), (3, NULL), (4, '{\"k\":1}')"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT t.id, jt.o, jt.x FROM t, JSON_TABLE(t.j, '$[*]' COLUMNS (o FOR ORDINALITY, x INT PATH '$')) jt ORDER BY t.id, jt.o"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1"; Some "1" ]
                              [ Some "1"; Some "2"; Some "2" ]
                              [ Some "2"; Some "1"; Some "3" ] ]
                            "lateral expansion with per-left-row ordinality"
                    | other -> failtestf "expected 3 rows, got %A" other

                testCase "JOIN ... ON lateral form filters the expansion"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, '[1,2]')" |> ignore

                    match
                        runDefault store "SELECT t.id, jt.x FROM t JOIN JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt ON jt.x > 1"
                    with
                    | ResultSet(_, [ [ Some "1"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected only the x=2 combination, got %A" other

                testCase "the alias is required, like MySQL's 3667"
                <| fun _ ->
                    match Fsdb.Parser.parse "SELECT * FROM JSON_TABLE('[1]', '$[*]' COLUMNS (x INT PATH '$'))" with
                    | Error _ -> ()
                    | Ok stmt -> failtestf "expected an alias-less JSON_TABLE to fail parsing, got %A" stmt

                testCase "a correlated source built by CONCAT expands like MySQL (torture probe shape)"
                <| fun _ ->
                    // Mirrors the `json_table_lateral` torture probe
                    // (Harness.fs `ScenarioProbes`), oracle-verified: CONCAT
                    // over two INT columns forms the document per left row.
                    let store = newStore ()
                    runDefault store "CREATE TABLE s (id INT, a INT, b INT)" |> ignore
                    runDefault store "INSERT INTO s VALUES (1, -5, 200), (2, 7, 9)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT s.id, jt.o, jt.n FROM s, JSON_TABLE(CONCAT('[', s.a, ',', s.b, ']'), '$[*]' COLUMNS (o FOR ORDINALITY, n INT PATH '$')) jt ORDER BY s.id, jt.o"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1"; Some "-5" ]
                              [ Some "1"; Some "2"; Some "200" ]
                              [ Some "2"; Some "1"; Some "7" ]
                              [ Some "2"; Some "2"; Some "9" ] ]
                            "CONCAT-built document expands per left row"
                    | other -> failtestf "expected 4 rows, got %A" other

                testCase "a forward table reference is 1109 with MySQL's wording"
                <| fun _ ->
                    // Oracle: `FROM JSON_TABLE(t.j, ...) jt, t` → error 1109
                    // "Unknown table 't' in a table function argument" — the
                    // qualifier can't reach a table listed *after* it, and
                    // the code is 1109 (not a 1054 unknown-column), so
                    // clients matching on it see MySQL's behavior.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore

                    match runDefault store "SELECT jt.x FROM JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt, t" with
                    | Err(1109, message) -> Expect.equal message "Unknown table 't' in a table function argument" "MySQL's 1109 wording"
                    | other -> failtestf "expected error 1109, got %A" other

                testCase "a bare column in an uncorrelated source is 1054 in 'a table function argument'"
                <| fun _ ->
                    // Oracle: same context string as 1109, not 'field list'.
                    match runDefault (newStore ()) "SELECT jt.x FROM JSON_TABLE(j, '$[*]' COLUMNS (x INT PATH '$')) jt" with
                    | Err(1054, message) -> Expect.equal message "Unknown column 'j' in 'a table function argument'" "MySQL's 1054 wording"
                    | other -> failtestf "expected error 1054, got %A" other

                testCase "an unknown qualifier in a correlated source is 1109"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, '[1]')" |> ignore

                    match runDefault store "SELECT jt.x FROM t, JSON_TABLE(nope.j, '$[*]' COLUMNS (x INT PATH '$')) jt" with
                    | Err(1109, message) -> Expect.equal message "Unknown table 'nope' in a table function argument" "MySQL's 1109 wording"
                    | other -> failtestf "expected error 1109, got %A" other

                    match runDefault store "SELECT jt.x FROM t, JSON_TABLE(COALESCE(nope.j, t.j), '$[*]' COLUMNS (x INT PATH '$')) jt" with
                    | Err(1109, _) -> ()
                    | other -> failtestf "expected nested qualifier error 1109, got %A" other

                testCase "JOIN ... USING against JSON_TABLE filters and coalesces the key"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (x INT, j JSON)" |> ignore
                    runDefault store "INSERT INTO t VALUES (2, '[1,2,3]')" |> ignore

                    match runDefault store "SELECT jt.x FROM t JOIN JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt USING (x)" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected the matching JSON row, got %A" other

                    match runDefault store "SELECT * FROM t JOIN JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt USING (x)" with
                    | ResultSet([ "x"; "j" ], [ [ Some "2"; Some "[1,2,3]" ] ]) -> ()
                    | other -> failtestf "expected one coalesced x column, got %A" other

                testCase "LEFT JOIN against JSON_TABLE null-pads empty and rejected expansions"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, '[1,2]'), (2, '[]'), (3, NULL)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT t.id, jt.x FROM t LEFT JOIN JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt ON jt.x = 2 ORDER BY t.id"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "2" ]; [ Some "2"; None ]; [ Some "3"; None ] ] "outer rows"
                    | other -> failtestf "expected a result set, got %A" other

                testCase "JSON_TABLE as a multi-table UPDATE join source is rejected"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, j JSON)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, '[1]')" |> ignore

                    match
                        runDefault store "UPDATE t, JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt SET t.id = jt.x"
                    with
                    | Err(1064, _) -> ()
                    | other -> failtestf "expected 1064, got %A" other ]

          // Every expected value below is the answer a live MySQL 8.4.11
          // oracle gives for the same statement.
          testList
              "JSON comparison, indexing, and rendering against the 8.4 oracle"
              [ testCase "a JSON scalar compares against a SQL value in the JSON domain, not as rendered text"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            """SELECT JSON_EXTRACT(JSON_OBJECT('s','abc'), '$.s') = 'abc' AS str_match,
                                      JSON_EXTRACT('{"n":1}', '$.n') = 1 AS num_match,
                                      JSON_EXTRACT('{"n":1}', '$.n') = '1' AS num_vs_str,
                                      JSON_EXTRACT('{"b":true}', '$.b') = 1 AS bool_vs_num,
                                      JSON_EXTRACT('{"d":1.5}', '$.d') = 1.5 AS decimal_match"""
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "1"; Some "1"; Some "0"; Some "0"; Some "1" ]
                            "the SQL side converts to JSON first: 'abc' matches the JSON string, '1' does not match the JSON number, and true is not 1"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "JSON values order by MySQL's type precedence: null < number < string < object < array < boolean"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            """SELECT CAST('1' AS JSON) < CAST('"a"' AS JSON) AS num_lt_str,
                                      CAST('null' AS JSON) < CAST('1' AS JSON) AS null_lt_num,
                                      CAST('true' AS JSON) > CAST('1' AS JSON) AS bool_gt_num,
                                      CAST('"a"' AS JSON) < CAST('[1]' AS JSON) AS str_lt_arr,
                                      CAST('[1]' AS JSON) < CAST('{}' AS JSON) AS arr_lt_obj,
                                      CAST('{}' AS JSON) < CAST('true' AS JSON) AS obj_lt_bool,
                                      CAST('"a"' AS JSON) = CAST('"A"' AS JSON) AS str_case_sensitive,
                                      CAST('[1,2]' AS JSON) < CAST('[1,3]' AS JSON) AS arr_elementwise,
                                      CAST('[1]' AS JSON) < CAST('[1,0]' AS JSON) AS arr_by_length"""
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "1"; Some "1"; Some "1"; Some "1"; Some "0"; Some "1"; Some "0"; Some "1"; Some "1" ]
                            "type rank decides before content, and JSON strings compare by code unit rather than the ai_ci collation"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "an array index path treats a non-array as a one-element array"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            """SELECT JSON_EXTRACT(CAST('{}' AS JSON), '$[0]') AS obj_at_0,
                                      JSON_EXTRACT(CAST('1' AS JSON), '$[0]') AS num_at_0,
                                      JSON_EXTRACT(CAST('1' AS JSON), '$[1]') AS num_at_1,
                                      JSON_EXTRACT(CAST('{"k":2}' AS JSON), '$[0].k') AS nested,
                                      JSON_EXTRACT(CAST('{}' AS JSON), '$[0][0]') AS twice"""
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "{}"; Some "1"; None; Some "2"; Some "{}" ]
                            "$[0] on a non-array yields the value itself; any other index misses"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "JSON output emits supplementary-plane characters literally, not as escaped surrogate pairs"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST(JSON_SET('{}', '$.e', 'a🚀b') AS CHAR) AS doc" with
                    | ResultSet(_, [ [ Some doc ] ]) ->
                        Expect.equal doc "{\"e\": \"a🚀b\"}" "the emoji survives as itself"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "CAST(x AS JSON) normalizes the document and rejects non-JSON text with 3141"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store """SELECT CAST('{"bb":1,"a":2}' AS JSON) AS doc, CAST('  1 ' AS JSON) AS num""" with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal row [ Some """{"a": 2, "bb": 1}"""; Some "1" ] "MySQL's stored key order and spacing"
                    | other -> failtestf "expected a single row, got %A" other

                    match runDefault store "SELECT CAST('abc' AS JSON)" with
                    | Err(3141, _) -> ()
                    | other -> failtestf "expected 3141, got %A" other ]

          testList
              "BIGINT UNSIGNED against the 8.4 oracle"
              [ testCase "CAST wraps into the unsigned domain and back out again"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            """SELECT CAST(-1 AS UNSIGNED) AS neg_one,
                                      CAST(-9223372036854775808 AS UNSIGNED) AS min_big,
                                      CAST(CAST(18446744073709551615 AS UNSIGNED) AS SIGNED) AS round_trip,
                                      CAST(255 AS UNSIGNED) + 1 AS widened,
                                      CAST(-1.9 AS UNSIGNED) AS rounded,
                                      CAST('abc' AS UNSIGNED) AS not_a_number,
                                      CAST(18446744073709551616 AS UNSIGNED) AS clamped,
                                      CAST(1e30 AS UNSIGNED) AS double_clamped,
                                      18446744073709551615 AS literal"""
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "18446744073709551615"
                              Some "9223372036854775808"
                              Some "-1"
                              Some "256"
                              Some "18446744073709551614"
                              Some "0"
                              Some "18446744073709551615"
                              Some "9223372036854775807"
                              Some "18446744073709551615" ]
                            "the whole [0, 2^64) domain survives, including a literal past BIGINT's signed max"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "unsigned arithmetic, division, and comparison stay in the 64-bit unsigned domain"
                <| fun _ ->
                    let store = newStore ()

                    match
                        runDefault
                            store
                            """SELECT CAST(-1 AS UNSIGNED) - 1 AS minus_one,
                                      CAST(-1 AS UNSIGNED) / 2 AS halved,
                                      CAST(-1 AS UNSIGNED) DIV 2 AS int_halved,
                                      CAST(-1 AS UNSIGNED) % 10 AS remainder,
                                      CAST(-1 AS UNSIGNED) + 1.0 AS promoted,
                                      CAST(18446744073709551615 AS UNSIGNED) > 1 AS gt_one,
                                      -1 < CAST(1 AS UNSIGNED) AS signed_lt_unsigned"""
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "18446744073709551614"
                              Some "9223372036854775807.5000"
                              Some "9223372036854775807"
                              Some "5"
                              Some "18446744073709551616.0"
                              Some "1"
                              Some "1" ]
                            "an unsigned operand keeps the result unsigned; a DECIMAL operand promotes past the range"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "a BIGINT UNSIGNED column stores, reads back, sorts, and aggregates values above BIGINT's signed max"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, u BIGINT UNSIGNED, s BIGINT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES (1, 18446744073709551615, -1), (2, 0, 0), (3, 9223372036854775808, 5)"
                    |> ignore

                    match runDefault store "SELECT id, u, u > s, CAST(u AS SIGNED) FROM t ORDER BY u DESC" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "18446744073709551615"; Some "1"; Some "-1" ]
                              [ Some "3"; Some "9223372036854775808"; Some "1"; Some "-9223372036854775808" ]
                              [ Some "2"; Some "0"; Some "0"; Some "0" ] ]
                            "unsigned values sort above every signed one and round-trip through CAST AS SIGNED"
                    | other -> failtestf "expected three rows, got %A" other

                    match runDefault store "SELECT SUM(u), MAX(u), MIN(u), COUNT(DISTINCT u) FROM t" with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "27670116110564327423"; Some "18446744073709551615"; Some "0"; Some "3" ]
                            "aggregates stay exact past 2^63"
                    | other -> failtestf "expected a single row, got %A" other

                    match runDefault store "SELECT id FROM t WHERE u = 18446744073709551615" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected only row 1, got %A" other

                testCase "numeric strings retain BIGINT precision"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE ids (id BIGINT UNSIGNED PRIMARY KEY, reserved_at INT NOT NULL DEFAULT 0)" |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO ids (id) VALUES ('122092114329652993'), ('122092114329653001')"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected distinct string-bound ids, got %A" other

                    match runDefault store "UPDATE ids SET reserved_at = 1 WHERE id = '122092114329652993'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected one adjacent id update, got %A" other

                    match runDefault store "DELETE FROM ids WHERE id = '122092114329653001'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected one adjacent id delete, got %A" other

                    match runDefault store "SELECT id, reserved_at FROM ids" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "122092114329652993"; Some "1" ] ]
                            "quoted predicates distinguish adjacent BIGINT values"
                    | other -> failtestf "expected the updated first id, got %A" other

                testCase "the scalar-function family answers in the unsigned domain instead of saturating"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            "SELECT HEX(CAST(-1 AS UNSIGNED)) AS h,
                                    ABS(CAST(-1 AS UNSIGNED)) AS a,
                                    ROUND(CAST(-1 AS UNSIGNED)) AS r,
                                    FLOOR(CAST(-1 AS UNSIGNED)) AS f,
                                    CEIL(CAST(-1 AS UNSIGNED)) AS c,
                                    TRUNCATE(CAST(-1 AS UNSIGNED), 0) AS t,
                                    BIN(CAST(-1 AS UNSIGNED)) AS b,
                                    CONV(CAST(-1 AS UNSIGNED), 10, 16) AS cv,
                                    JSON_ARRAY(CAST(-1 AS UNSIGNED)) AS j,
                                    BIN(-1) AS signed_bin"
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "FFFFFFFFFFFFFFFF"
                              Some "18446744073709551615"
                              Some "18446744073709551615"
                              Some "18446744073709551615"
                              Some "18446744073709551615"
                              Some "18446744073709551615"
                              Some(String.replicate 64 "1")
                              Some "FFFFFFFFFFFFFFFF"
                              Some "[18446744073709551615]"
                              Some(String.replicate 64 "1") ]
                            "the top half of the domain survives every scalar, and BIN reads its argument unsigned"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "a UNION over an unsigned branch stays numeric instead of poisoning the column to text"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT CAST(-1 AS UNSIGNED) AS u UNION SELECT 9 ORDER BY u" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "9" ]; [ Some "18446744073709551615" ] ]
                            "UNION reconciliation knows BIGINT UNSIGNED, so ORDER BY sorts numerically"
                    | other -> failtestf "expected two rows, got %A" other

                    match runDefault (newStore ()) "SELECT x FROM (SELECT CAST(10 AS UNSIGNED) x UNION SELECT 9) t ORDER BY x" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "9" ]; [ Some "10" ] ] "10 sorts after 9, not before it as text"
                    | other -> failtestf "expected two rows, got %A" other

                // MySQL 8.4 errors on both rather than answering in a wider
                // type, and an answer here is indistinguishable from a
                // correct one.
                testCase "arithmetic outside the unsigned domain refuses with 1690 rather than answering"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT CAST(-1 AS UNSIGNED) + 1" with
                    | Err(1690, _) -> ()
                    | other -> failtestf "expected 1690, got %A" other

                    match runDefault (newStore ()) "SELECT CAST(1 AS UNSIGNED) - 2" with
                    | Err(1690, message) ->
                        Expect.equal
                            message
                            "BIGINT UNSIGNED value is out of range in '(cast(1 as unsigned) - 2)'"
                            "offending expression"
                    | other -> failtestf "expected 1690, got %A" other

                    // Unary minus is part of the literal, not `0 - x`, so
                    // these stay answers (MySQL agrees).
                    match runDefault (newStore ()) "SELECT -9223372036854775808 AS a, -18446744073709551615 AS b" with
                    | ResultSet(_, [ [ Some "-9223372036854775808"; Some "-18446744073709551615" ] ]) -> ()
                    | other -> failtestf "expected both negative literals, got %A" other

                testCase "a BIGINT UNSIGNED column rejects an out-of-range write in strict mode"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE s (v BIGINT UNSIGNED)" |> ignore

                    match runDefault store "INSERT INTO s VALUES (-1)" with
                    | Err(1264, _) -> ()
                    | other -> failtestf "expected 1264, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM s" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected the rejected row to be absent, got %A" other ]

          testList
              "JSON-typed values compare in the JSON domain"
              [ testCase "a JSON column compares and sorts as a document, not as its rendered text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE jd (id INT, j JSON)" |> ignore

                    runDefault store """INSERT INTO jd VALUES (1, '"a"'), (2, '1'), (3, '{"k": 1}'), (4, '[1]'), (5, 'true')"""
                    |> ignore

                    // MySQL's JSON type precedence: number < string < object
                    // < array < boolean — not the rendered text's order.
                    match runDefault store """SELECT id, j = CAST('"a"' AS JSON) AS eq FROM jd ORDER BY j""" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "2"; Some "0" ]
                              [ Some "1"; Some "1" ]
                              [ Some "3"; Some "0" ]
                              [ Some "4"; Some "0" ]
                              [ Some "5"; Some "0" ] ]
                            "a JSON column value is JSON-typed, so equality parses and ORDER BY ranks by type"
                    | other -> failtestf "expected five rows, got %A" other

                    // A SQL string operand is the JSON *string* it spells,
                    // not a document to parse: MySQL answers 0 here.
                    match runDefault store """SELECT id FROM jd WHERE j = '"a"'""" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected no rows, got %A" other

                testCase "JSON_TABLE's JSON columns carry their JSON-ness out of the table function"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT j, j = CAST('"a"' AS JSON) AS eq
                               FROM JSON_TABLE('["a", 1, {"k":1}]', '$[*]' COLUMNS(j JSON PATH '$')) t
                               ORDER BY j"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "0" ]
                              [ Some "\"a\""; Some "1" ]
                              [ Some "{\"k\": 1}"; Some "0" ] ]
                            "the number sorts first, the string matches the JSON literal"
                    | other -> failtestf "expected three rows, got %A" other

                // NESTED PATH row multiplication, all four shapes pinned to
                // the 8.4 oracle.
                testCase "NESTED PATH multiplies the parent row, keeping it when nothing matches"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT jt.a, jt.k
                               FROM JSON_TABLE('[{"a":1,"kids":[{"k":10},{"k":11}]},{"a":2,"kids":[{"k":20}]},{"a":3,"kids":[]}]',
                                               '$[*]' COLUMNS (a INT PATH '$.a',
                                                               NESTED PATH '$.kids[*]' COLUMNS (k INT PATH '$.k'))) AS jt
                               ORDER BY jt.a, jt.k"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10" ]
                              [ Some "1"; Some "11" ]
                              [ Some "2"; Some "20" ]
                              [ Some "3"; None ] ]
                            "two children expand to two rows; an empty array still yields the parent with NULL (OUTER semantics)"
                    | other -> failtestf "expected four rows, got %A" other

                testCase "sibling NESTED PATHs do not cross-join"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT jt.a, jt.v, jt.w
                               FROM JSON_TABLE('[{"a":1,"x":[{"v":7},{"v":8}],"y":[{"w":90}]}]',
                                               '$[*]' COLUMNS (a INT PATH '$.a',
                                                               NESTED PATH '$.x[*]' COLUMNS (v INT PATH '$.v'),
                                                               NESTED PATH '$.y[*]' COLUMNS (w INT PATH '$.w'))) AS jt
                               ORDER BY jt.a, jt.v, jt.w"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; None; Some "90" ]
                              [ Some "1"; Some "7"; None ]
                              [ Some "1"; Some "8"; None ] ]
                            "three rows, not the six a cross-join would give: each sibling NULLs the other's column"
                    | other -> failtestf "expected three rows, got %A" other

                testCase "NESTED PATH nests, and FOR ORDINALITY counts within the nested sequence"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT jt.a, jt.o, jt.z
                               FROM JSON_TABLE('[{"a":1,"k":[{"n":[{"z":5},{"z":6}]}]}]',
                                               '$[*]' COLUMNS (a INT PATH '$.a',
                                                               NESTED PATH '$.k[*]' COLUMNS (
                                                                   NESTED PATH '$.n[*]' COLUMNS (o FOR ORDINALITY, z INT PATH '$.z')))) AS jt
                               ORDER BY jt.a, jt.z"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "1"; Some "5" ]; [ Some "1"; Some "2"; Some "6" ] ]
                            "two levels deep, ordinality restarting inside the innermost sequence"
                    | other -> failtestf "expected two rows, got %A" other

                // LATERAL: the derived table re-runs per left row, so it may
                // reference the left row's columns — the one thing a plain
                // derived table cannot do. Both shapes pinned to the oracle.
                testCase "a LATERAL derived table sees the left row's columns"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT s.n, d.dbl
                               FROM (SELECT 1 AS n UNION SELECT 2 UNION SELECT 3) s,
                                    LATERAL (SELECT s.n * 2 AS dbl) d
                               ORDER BY s.n"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ]; [ Some "3"; Some "6" ] ]
                            "the body is re-evaluated against each left row"
                    | other -> failtestf "expected three rows, got %A" other

                testCase "LEFT JOIN LATERAL keeps a left row whose body produced nothing"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            """SELECT s.n, d.v
                               FROM (SELECT 1 AS n UNION SELECT 2) s
                               LEFT JOIN LATERAL (SELECT s.n AS v WHERE s.n > 1) d ON 1 = 1
                               ORDER BY s.n"""
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; None ]; [ Some "2"; Some "2" ] ]
                            "outer semantics: the empty body NULL-extends rather than dropping the row"
                    | other -> failtestf "expected two rows, got %A" other ]

          testList
              "rounding and truncation rules pinned to the 8.4 oracle"
              [ testCase "ROUND splits half-away-from-zero for exact values and half-to-even for approximate ones"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            "SELECT ROUND(2.5) AS exact, ROUND(2.5e0) AS approx, ROUND(-2.5) AS exact_neg, ROUND(-2.5e0) AS approx_neg, ROUND(0.5e0) AS approx_half"
                    with
                    | ResultSet(_, [ row ]) ->
                        Expect.equal
                            row
                            [ Some "3"; Some "2"; Some "-3"; Some "-2"; Some "0" ]
                            "DECIMAL rounds away from zero, DOUBLE rounds to even — MySQL's split, not one rule for both"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "TRUNCATE keeps the requested scale and FLOOR/CEILING keep a DECIMAL exact"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT TRUNCATE(1.999, 2) AS t, FLOOR(1.5) AS f, CEILING(1.5) AS c" with
                    | ResultSet(_, [ [ Some "1.99"; Some "1"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected 1.99, 1, 2, got %A" other ]

          // Expected values are captured from the MySQL 8.4.11 oracle.
          testList
              "builtins pinned to the 8.4 oracle"
              [ let expectRow (sql: string) (expected: string option list) =
                    match runDefault (newStore ()) sql with
                    | ResultSet(_, [ row ]) -> Expect.equal row expected sql
                    | other -> failtestf "expected a single row from %s, got %A" sql other

                testCase "XOR is three-valued and binds between OR and AND"
                <| fun _ ->
                    expectRow
                        "SELECT (NULL XOR 1) a, (1 XOR 1) b, (1 XOR 0) c, (0 XOR 0) d, (2 XOR 3) e, (NULL XOR NULL) f, 1 XOR 1 OR 1 AS g, 1 XOR 1 AND 0 AS h, NOT 1 XOR 1 AS i"
                        [ None; Some "0"; Some "1"; Some "0"; Some "0"; None; Some "1"; Some "1"; Some "1" ]

                testCase "EXTRACT's composite units concatenate their components as digits"
                <| fun _ ->
                    expectRow
                        "SELECT EXTRACT(YEAR_MONTH FROM '2020-03-04 05:06:07.123456') a, EXTRACT(DAY_SECOND FROM '2020-03-04 05:06:07.123456') b, EXTRACT(DAY_MICROSECOND FROM '2020-03-04 05:06:07.123456') c, EXTRACT(HOUR_MINUTE FROM '2020-03-04 05:06:07.123456') d, EXTRACT(MICROSECOND FROM '2020-03-04 05:06:07.123456') e, EXTRACT(WEEK FROM '2020-03-04') f, EXTRACT(HOUR FROM '2020-03-04') g, EXTRACT(YEAR FROM NULL) h, EXTRACT(NOSUCHUNIT FROM '2020-03-04') i"
                        [ Some "202003"
                          Some "4050607"
                          Some "4050607123456"
                          Some "506"
                          Some "123456"
                          Some "9"
                          Some "0"
                          None
                          None ]

                testCase "typed temporal literals carry their type, and DATE '..' feeds date arithmetic"
                <| fun _ ->
                    expectRow
                        "SELECT DATE '2030-01-01' a, TIME '10:20:30' b, TIMESTAMP '2030-01-01 05:06:07' c, TIME '100:20:30' d, TIME '10:20' e, TIME '-10:20:30' f, TIMESTAMPDIFF(DAY, DATE '2020-01-01', DATE '2030-01-01') g"
                        [ Some "2030-01-01"
                          Some "10:20:30"
                          Some "2030-01-01 05:06:07"
                          Some "100:20:30"
                          Some "10:20:00"
                          Some "-10:20:30"
                          Some "3653" ]

                testCase "temporal literals use MySQL's grammar, not .NET's date parser"
                <| fun _ ->
                    // Year-first with any punctuation delimiter, or a bare
                    // 6/8-digit run; TIME reads a delimiter-free run
                    // right-to-left and keeps the declared fraction width.
                    expectRow
                        "SELECT DATE '2020-1-1' a, DATE '20200101' b, DATE '2020.1.2' c, DATE '69-01-01' d, TIME '10' e, TIME '1005' f, TIME '10:20:30.5' g, TIME '3 10:20:30' h, TIMESTAMP '20200101100000' i"
                        [ Some "2020-01-01"
                          Some "2020-01-01"
                          Some "2020-01-02"
                          Some "2069-01-01"
                          Some "00:00:10"
                          Some "00:10:05"
                          Some "10:20:30.5"
                          Some "82:20:30"
                          Some "2020-01-01 10:00:00" ]

                testCase "a malformed temporal literal is refused, never silently reinterpreted"
                <| fun _ ->
                    // Each of these is 1525 on the 8.4 oracle. `DateOnly`/
                    // `DateTime.TryParse` would accept the first three (as
                    // 2020-01-02, 2020-01-05 and *today* 05:06:07) — the whole
                    // reason the literal is folded and validated in the parser.
                    for sql in
                        [ "SELECT DATE '01/02/2020'"
                          "SELECT DATE 'Jan 5, 2020'"
                          "SELECT TIMESTAMP '05:06:07'"
                          "SELECT DATE '2020-13-01'"
                          "SELECT TIME '99999999999:00'"
                          "SELECT TIME '839:00:00'"
                          "SELECT TIME '838:59:59.000001'"
                          "SELECT TIME '-838:59:59.000001'"
                          "SELECT TIMESTAMP '2020-01-01'"
                          "SELECT DATE '" + String.replicate 100_000 "1-" + "1'" ] do
                        match Fsdb.Parser.parse sql with
                        | Result.Error _ -> ()
                        | Result.Ok other -> failtestf "expected a parse refusal for %s, got %A" sql other

                testCase "TIMESTAMPADD is DATE_ADD with the arguments in the other order"
                <| fun _ ->
                    expectRow
                        "SELECT TIMESTAMPADD(QUARTER, 1, '2020-03-04 05:06:07') a, TIMESTAMPADD(MINUTE, 90, '2020-03-04 05:06:07') b, TIMESTAMPADD(DAY, 5, '2020-03-04') c"
                        [ Some "2020-06-04 05:06:07"; Some "2020-03-04 06:36:07"; Some "2020-03-09" ]

                testCase "DATE_FORMAT accepts DATE_ADD from a compact epoch literal"
                <| fun _ ->
                    expectRow
                        "SELECT DATE_FORMAT(DATE_ADD('19700101', INTERVAL 946771200 SECOND), '%Y%m%d') a, DATE_FORMAT(DATE_ADD('700101', INTERVAL 0 SECOND), '%Y%m%d') b"
                        [ Some "20000102"; Some "19700101" ]

                testCase "composite INTERVAL units read their components right-aligned"
                <| fun _ ->
                    expectRow
                        "SELECT '1996-02-08 00:48:46' - INTERVAL '1:30' HOUR_MINUTE a, DATE_ADD('2011-10-16', INTERVAL '2-3' YEAR_MONTH) b, DATE_ADD('2020-01-01 00:00:00', INTERVAL '1 2:3:4' DAY_SECOND) c, DATE_ADD('2020-01-01 00:00:00', INTERVAL '3:4' DAY_SECOND) d, DATE_ADD('2020-01-01 00:00:00.000000', INTERVAL '1.5' SECOND_MICROSECOND) e, DATE_SUB('2020-01-01', INTERVAL '1-2' YEAR_MONTH) f"
                        [ Some "1996-02-07 23:18:46"
                          Some "2014-01-16"
                          Some "2020-01-02 02:03:04"
                          Some "2020-01-01 00:03:04"
                          Some "2020-01-01 00:00:01.500000"
                          Some "2018-11-01" ]

                testCase "a composite INTERVAL with more components than its unit names is NULL"
                <| fun _ ->
                    expectRow
                        "SELECT DATE_ADD('2020-01-01', INTERVAL '1 2:3:4' HOUR_MINUTE) a, '2020-01-01' + INTERVAL '1:2:3' HOUR_MINUTE b, DATE_ADD('2020-01-01', INTERVAL '1-2-3' YEAR_MONTH) c"
                        [ None; None; None ]

                testCase "an unrecognized INTERVAL unit is NULL, not a silently ignored no-op"
                <| fun _ -> expectRow "SELECT DATE_ADD('2020-01-01', INTERVAL 1 NOSUCHUNIT) a" [ None ]

                testCase "MOD is an infix keyword operator binding as tightly as %"
                <| fun _ ->
                    expectRow
                        "SELECT 7 MOD 3 a, -7 MOD 3 b, 7 MOD 0 c, 1 + 2 MOD 2 d, 7 mod 3 e, MOD(7, 3) f"
                        [ Some "1"; Some "-1"; None; Some "1"; Some "1"; Some "1" ]

                testCase "a column whose name starts with MOD is not the MOD operator"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE m (mode_id INT)" |> ignore
                    runDefault store "INSERT INTO m (mode_id) VALUES (5)" |> ignore

                    match runDefault store "SELECT mode_id FROM m" with
                    | ResultSet(_, [ [ Some "5" ] ]) -> ()
                    | other -> failtestf "expected 5, got %A" other

                testCase "JSON_DEPTH counts nesting, with an empty container still depth 1"
                <| fun _ ->
                    expectRow
                        "SELECT JSON_DEPTH('1') a, JSON_DEPTH('[]') b, JSON_DEPTH('{}') c, JSON_DEPTH('[1,2]') d, JSON_DEPTH('[1,[2,[3]]]') e, JSON_DEPTH('{\"a\":{\"b\":1}}') f, JSON_DEPTH(NULL) g"
                        [ Some "1"; Some "1"; Some "1"; Some "2"; Some "4"; Some "3"; None ]

                testCase "DAYOFYEAR is 1-based and leap-year aware"
                <| fun _ ->
                    expectRow
                        "SELECT DAYOFYEAR('2024-03-01') a, DAYOFYEAR('2023-12-31') b, DAYOFYEAR(NULL) c"
                        [ Some "61"; Some "365"; None ]

                testCase "BIT_COUNT reads its argument as BIGINT UNSIGNED"
                <| fun _ ->
                    expectRow
                        "SELECT BIT_COUNT(255) a, BIT_COUNT(-1) b, BIT_COUNT(0) c, BIT_COUNT('12') d, BIT_COUNT(NULL) e"
                        [ Some "8"; Some "64"; Some "0"; Some "2"; None ]

                testCase "bit and national string literals preserve their bytes and text"
                <| fun _ ->
                    expectRow
                        "SELECT HEX(b'0101') a, HEX(B'111111111') b, HEX(0b0101) c, LENGTH(b'0101') d, HEX(b'') e, N'héllo' f"
                        [ Some "05"; Some "01FF"; Some "05"; Some "1"; Some ""; Some "héllo" ]

                testCase "BIT values retain their raw bytes in byte-oriented functions"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE bits (value BIT(64))" |> ignore
                    runDefault store "INSERT INTO bits VALUES (0x8000000000000000)" |> ignore

                    match runDefault store "SELECT HEX(value), LENGTH(value), BIT_LENGTH(value), TO_BASE64(value), HEX(CONCAT(value)), HEX(REPEAT(value, 1)), HEX(SUBSTRING(value, 1, 1)), HEX(UPPER(value)), HEX(REVERSE(value)), MD5(value), SHA2(value, 256) FROM bits" with
                    | ResultSet(_, [ [ Some "8000000000000000"; Some "8"; Some "64"; Some "gAAAAAAAAAA="; Some "8000000000000000"; Some "8000000000000000"; Some "80"; Some "8000000000000000"; Some "0000000000000080"; Some "54409ea540dc450d53a86133d867c772"; Some "b1b0bee5378188f5250138bcce25855f2617f9c55b20b9628e13d367c47404a9" ] ]) -> ()
                    | other -> failtestf "expected raw BIT bytes, got %A" other

                    runDefault store "CREATE TABLE raw_bits (lowercase BIT(8), uppercase BIT(8), padded BIT(24))" |> ignore
                    runDefault store "INSERT INTO raw_bits VALUES (0x61, 0x41, 0xA020A0)" |> ignore

                    match runDefault store "SELECT HEX(UPPER(lowercase)), HEX(LOWER(lowercase)), HEX(TRIM(padded)), LOCATE(lowercase, uppercase), INSTR(uppercase, lowercase) FROM raw_bits" with
                    | ResultSet(_, [ [ Some "61"; Some "61"; Some "A020A0"; Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected binary string semantics, got %A" other

                    match runDefault store "SELECT uppercase = X'41', uppercase = X'61', uppercase < X'61', uppercase = '65', uppercase = 'A', uppercase = X'000000000000000041' FROM raw_bits" with
                    | ResultSet(_, [ [ Some "1"; Some "0"; Some "1"; Some "1"; Some "0"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected BIT comparison semantics, got %A" other

                    runDefault store "CREATE TABLE one_bit (value BIT)" |> ignore
                    runDefault store "INSERT INTO one_bit VALUES (1)" |> ignore

                    match runDefault store "SELECT value = X'000000000000000001' FROM one_bit" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected wide binary literal coercion, got %A" other

                testCase "BIT predicates preserve values above double precision"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE bits (value BIT(64))" |> ignore
                    runDefault store "INSERT INTO bits VALUES (0x8000000000000000)" |> ignore

                    match runDefault store "SELECT value = 9223372036854775808, value = 9223372036854775809, value < 9223372036854775809, value = 9223372036854775808.0, value = '9223372036854775808', value = '9223372036854775809', value = X'8000000000000000', value = X'8000000000000001' FROM bits" with
                    | ResultSet(_, [ [ Some "1"; Some "0"; Some "1"; Some "1"; Some "1"; Some "0"; Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected exact BIT predicates, got %A" other

                testCase "SOUNDS LIKE and MEMBER OF follow MySQL comparison semantics"
                <| fun _ ->
                    expectRow
                        "SELECT 'Robert' SOUNDS LIKE 'Rupert' a, 'Robert' SOUNDS LIKE 'Rubin' b, NULL SOUNDS LIKE 'x' c, 17 MEMBER OF('[17, \"17\", null]') d, '17' MEMBER OF('[17, \"17\", null]') e, NULL MEMBER OF('[17]') f, 2 MEMBER OF('[[2]]') g"
                        [ Some "1"; Some "0"; None; Some "1"; Some "1"; None; Some "0" ]

                testCase "GET_FORMAT returns MySQL's locale format templates"
                <| fun _ ->
                    expectRow
                        "SELECT GET_FORMAT(DATE,'USA') a, GET_FORMAT(DATE,'JIS') b, GET_FORMAT(DATE,'EUR') c, GET_FORMAT(DATE,'INTERNAL') d, GET_FORMAT(DATETIME,'USA') e, GET_FORMAT(DATETIME,'ISO') f, GET_FORMAT(DATETIME,'INTERNAL') g, GET_FORMAT(TIME,'USA') h, GET_FORMAT(TIME,'ISO') i, GET_FORMAT(TIME,'INTERNAL') j, GET_FORMAT(DATE,'x') k, GET_FORMAT(DATE,NULL) l"
                        [ Some "%m.%d.%Y"
                          Some "%Y-%m-%d"
                          Some "%d.%m.%Y"
                          Some "%Y%m%d"
                          Some "%Y-%m-%d %H.%i.%s"
                          Some "%Y-%m-%d %H:%i:%s"
                          Some "%Y%m%d%H%i%s"
                          Some "%h:%i:%s %p"
                          Some "%H:%i:%s"
                          Some "%H%i%s"
                          None
                          None ]

                testCase "JSON path, overlap, quote, and pretty helpers match MySQL"
                <| fun _ ->
                    expectRow
                        "SELECT JSON_CONTAINS_PATH('{\"a\":1,\"b\":{\"c\":2}}','one','$.x','$.b.c') a, JSON_CONTAINS_PATH('{\"a\":1,\"b\":{\"c\":2}}','all','$.a','$.x') b, JSON_OVERLAPS('[1,2]', '[2,3]') c, JSON_OVERLAPS('{\"a\":1}', '{\"a\":2,\"b\":1}') d, JSON_OVERLAPS('2','[1,2]') e, JSON_QUOTE('a\"b\\\\c') f, JSON_PRETTY('{\"long\":1,\"a\":[1,2]}') g"
                        [ Some "1"
                          Some "0"
                          Some "1"
                          Some "0"
                          Some "1"
                          Some "\"a\\\"b\\\\c\""
                          Some "{\n  \"a\": [\n    1,\n    2\n  ],\n  \"long\": 1\n}" ]

                testCase "JSON merge patch and preserve keep their distinct MySQL semantics"
                <| fun _ ->
                    expectRow
                        "SELECT JSON_MERGE_PATCH('{\"a\":1,\"b\":2}','{\"a\":null,\"c\":3}') a, JSON_MERGE_PATCH('{\"a\":{\"x\":1}}','{\"a\":{\"y\":2}}') b, JSON_MERGE_PATCH('[1,2]','{\"a\":1}') c, JSON_MERGE_PRESERVE('{\"a\":1}','{\"a\":2,\"b\":3}') d, JSON_MERGE_PRESERVE('[1,2]','[3]') e, JSON_MERGE_PRESERVE('1','[2,3]') f, JSON_MERGE_PRESERVE('{\"a\":1}','{\"a\":2}','{\"a\":3}') g"
                        [ Some "{\"b\": 2, \"c\": 3}"
                          Some "{\"a\": {\"x\": 1, \"y\": 2}}"
                          Some "{\"a\": 1}"
                          Some "{\"a\": [1, 2], \"b\": 3}"
                          Some "[1, 2, 3]"
                          Some "[1, 2, 3]"
                          Some "{\"a\": [1, 2, 3]}" ]

                testCase "JSON array append and insert apply path-value pairs sequentially"
                <| fun _ ->
                    expectRow
                        "SELECT JSON_ARRAY_APPEND('[1,2]','$',3) a, JSON_ARRAY_APPEND('{\"a\":1}','$.a',2) b, JSON_ARRAY_APPEND('{\"a\":[1]}','$.a',2,'$.a',3) c, JSON_ARRAY_INSERT('[1,2]','$[1]',9) d, JSON_ARRAY_INSERT('[1,2]','$[9]',9) e, JSON_ARRAY_INSERT('{\"a\":[1,2]}','$.a[0]',9) f"
                        [ Some "[1, 2, 3]"
                          Some "{\"a\": [1, 2]}"
                          Some "{\"a\": [1, 2, 3]}"
                          Some "[1, 9, 2]"
                          Some "[1, 2, 9]"
                          Some "{\"a\": [9, 1, 2]}" ]

                testCase "JSON storage size follows MySQL's binary JSON layout"
                <| fun _ ->
                    expectRow
                        "SELECT JSON_STORAGE_SIZE('null') a, JSON_STORAGE_SIZE('true') b, JSON_STORAGE_SIZE('0') c, JSON_STORAGE_SIZE('32767') d, JSON_STORAGE_SIZE('32768') e, JSON_STORAGE_SIZE('1.5') f, JSON_STORAGE_SIZE('\"hello\"') g, JSON_STORAGE_SIZE('[]') h, JSON_STORAGE_SIZE('[1,2]') i, JSON_STORAGE_SIZE('{}') j, JSON_STORAGE_SIZE('{\"a\":1}') k, JSON_STORAGE_SIZE('{\"a\":1,\"bb\":\"x\"}') l, JSON_STORAGE_FREE('{\"a\":1}') m"
                        [ Some "2"
                          Some "2"
                          Some "3"
                          Some "3"
                          Some "5"
                          Some "9"
                          Some "7"
                          Some "5"
                          Some "11"
                          Some "5"
                          Some "13"
                          Some "24"
                          Some "0" ]

                testCase "compression, random bytes, UUID_SHORT, and NAME_CONST expose MySQL-compatible values"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT LENGTH(COMPRESS('abc')) a, HEX(COMPRESS('abc')) b, HEX(UNCOMPRESS(COMPRESS('héllo'))) c, UNCOMPRESSED_LENGTH(COMPRESS('héllo')) d, LENGTH(COMPRESS('')) e, LENGTH(RANDOM_BYTES(4)) f, UUID_SHORT() g, UUID_SHORT() h, NAME_CONST('answer',42)" with
                    | ResultSet(columns, [ [ Some "15"; Some compressed; Some "68C3A96C6C6F"; Some "6"; Some "0"; Some "4"; Some first; Some second; Some "42" ] ]) ->
                        Expect.equal compressed "03000000789C4B4C4A0600024D0127" "zlib framing"
                        Expect.equal columns.[8] "answer" "NAME_CONST supplies the column label"
                        Expect.equal (uint64 second) (uint64 first + 1UL) "UUID_SHORT is monotonic"
                    | other -> failtestf "unexpected result: %A" other

                testCase "standalone TABLE and VALUES ROW queries use the ordinary query pipeline"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE tq (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO tq VALUES (2,'b'),(1,'a'),(3,'c')" |> ignore

                    match runDefault store "TABLE tq ORDER BY id LIMIT 1 OFFSET 1" with
                    | ResultSet([ "id"; "name" ], [ [ Some "2"; Some "b" ] ]) -> ()
                    | other -> failtestf "unexpected TABLE result: %A" other

                    match runDefault store "VALUES ROW(2,'b'), ROW(1,'a'), ROW(2,'b') ORDER BY column_0 LIMIT 2" with
                    | ResultSet([ "column_0"; "column_1" ], [ [ Some "1"; Some "a" ]; [ Some "2"; Some "b" ] ]) -> ()
                    | other -> failtestf "unexpected VALUES result: %A" other

                testCase "index hints parse on base and joined tables"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE hinted (id INT PRIMARY KEY, n INT, INDEX ix_n (n))" |> ignore
                    runDefault store "INSERT INTO hinted VALUES (1,10),(2,20)" |> ignore

                    match
                        runDefault store
                            "SELECT a.id FROM hinted AS a USE INDEX (PRIMARY) JOIN hinted b FORCE KEY FOR JOIN (ix_n) IGNORE INDEX FOR ORDER BY (ix_n) ON b.id = a.id ORDER BY a.id"
                    with
                    | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
                    | other -> failtestf "unexpected hinted query result: %A" other

                testCase "integer writes reject overflow in strict mode and clamp it otherwise"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE integer_ranges (a TINYINT, b SMALLINT UNSIGNED, c INT, d BIGINT)" |> ignore

                    match runDefault store "INSERT INTO integer_ranges VALUES (128, 1, 1, 1)" with
                    | Err(1264, _) -> ()
                    | other -> failtestf "expected strict TINYINT overflow, got %A" other

                    match runDefault store "INSERT INTO integer_ranges VALUES (1, -1, 1, 1)" with
                    | Err(1264, _) -> ()
                    | other -> failtestf "expected strict unsigned SMALLINT overflow, got %A" other

                    match runDefault store "INSERT INTO integer_ranges VALUES (1, 1, 2147483648, 1)" with
                    | Err(1264, _) -> ()
                    | other -> failtestf "expected strict INT overflow, got %A" other

                    match runDefault store "INSERT INTO integer_ranges VALUES (1, 1, 1, 18446744073709551615)" with
                    | Err(1264, _) -> ()
                    | other -> failtestf "expected strict BIGINT overflow, got %A" other

                    setStrictMode store false
                    runDefault store "INSERT INTO integer_ranges VALUES (128, -1, 2147483648, 18446744073709551615)" |> ignore

                    match runDefault store "SELECT a,b,c,d FROM integer_ranges" with
                    | ResultSet(_, [ [ Some "127"; Some "0"; Some "2147483647"; Some "9223372036854775807" ] ]) -> ()
                    | other -> failtestf "expected non-strict clamping, got %A" other

                testCase "bitwise operators use unsigned 64-bit values and MySQL precedence"
                <| fun _ ->
                    expectRow
                        "SELECT 5 & 3 a, 5 | 2 b, 5 ^ 1 c, 1 << 4 d, 16 >> 2 e, ~0 f, ~1 g, NULL & 1 n, 1 << 64 s64, 1 << -1 sn, 1 + 2 << 1 p1, 1 | 2 & 4 p2, 1 ^ 3 & 1 p3, 8 >> 1 + 1 p4"
                        [ Some "1"
                          Some "7"
                          Some "4"
                          Some "16"
                          Some "4"
                          Some "18446744073709551615"
                          Some "18446744073709551614"
                          None
                          Some "0"
                          Some "0"
                          Some "6"
                          Some "1"
                          Some "0"
                          Some "2" ]

                testCase "a fractional argument rounds for BIT_COUNT/EXPORT_SET/MAKEDATE but truncates for BIN and strings"
                <| fun _ ->
                    expectRow
                        "SELECT BIT_COUNT(3.5) a, BIT_COUNT(2.5) b, BIT_COUNT(2.5e0) c, BIT_COUNT('3.5') d, BIT_COUNT(-0.5) e, BIN(2.5) f, OCT(9.7) g, EXPORT_SET(5.7,'Y','N',',',4) h, EXPORT_SET(5,'Y','N',',',3.7) i, EXPORT_SET('5.7','Y','N',',',4) j"
                        [ Some "1"
                          Some "2"
                          Some "1"
                          Some "2"
                          Some "64"
                          Some "10"
                          Some "11"
                          Some "N,Y,Y,N"
                          Some "Y,N,Y,N"
                          Some "Y,N,Y,N" ]

                testCase "CONV saturates an overflowing magnitude instead of wrapping, and NULLs Int32.MinValue bases"
                <| fun _ ->
                    expectRow
                        "SELECT CONV('18446744073709551616',10,10) a, CONV('9999999999999999999999',10,16) b, CONV('-18446744073709551615',10,10) c, CONV('-18446744073709551616',10,10) d, CONV('-1FFFFFFFFFFFFFFFF',16,10) e, CONV('-FFFFFFFFFFFFFFFF0',16,10) f, CONV(1,10,-2147483648) g, CONV(1,-2147483648,10) h, CONV(10,2.5,10) i"
                        [ Some "18446744073709551615"
                          Some "FFFFFFFFFFFFFFFF"
                          Some "1"
                          Some "0"
                          Some "0"
                          Some "18446744073709551615"
                          None
                          None
                          Some "3" ]

                testCase "CRC32 is the zlib CRC over the argument's text form"
                <| fun _ ->
                    expectRow
                        "SELECT CRC32('abc') a, CRC32('') b, CRC32(123) c, CRC32(NULL) d"
                        [ Some "891568578"; Some "0"; Some "2286445522"; None ]

                testCase "logarithmic and exponential functions match MySQL's domains"
                <| fun _ ->
                    expectRow
                        "SELECT ROUND(LOG(10),12), ROUND(LN(10),12), ROUND(LOG(10,1000),12), LOG(0), LOG(-1), ROUND(LOG2(8),12), ROUND(LOG10(1000),12), ROUND(EXP(1),12), ROUND(PI(),12)"
                        [ Some "2.302585092994"
                          Some "2.302585092994"
                          Some "3"
                          None
                          None
                          Some "3"
                          Some "3"
                          Some "2.718281828459"
                          Some "3.14159265359" ]

                testCase "trigonometric functions use radians and MySQL's null domains"
                <| fun _ ->
                    expectRow
                        "SELECT ROUND(SIN(PI()/2),12), ROUND(COS(PI()),12), ROUND(TAN(0),12), ROUND(COT(PI()/4),12), ROUND(ASIN(1),12), ASIN(2), ROUND(ACOS(0),12), ACOS(2), ROUND(ATAN(1),12), ROUND(ATAN(1,1),12), ROUND(ATAN2(1,1),12), ROUND(DEGREES(PI()),12), ROUND(RADIANS(180),12)"
                        [ Some "1"
                          Some "-1"
                          Some "0"
                          Some "1"
                          Some "1.570796326795"
                          None
                          Some "1.570796326795"
                          None
                          Some "0.785398163397"
                          Some "0.785398163397"
                          Some "0.785398163397"
                          Some "180"
                          Some "3.14159265359" ]

                testCase "IPv6 conversion and address-family predicates match packed-byte semantics"
                <| fun _ ->
                    expectRow
                        "SELECT HEX(INET6_ATON('2001:db8::1')), INET6_NTOA(INET6_ATON('2001:db8::1')), HEX(INET6_ATON('192.0.2.01')), INET6_NTOA(INET6_ATON('192.0.2.1')), INET6_ATON('127.1'), IS_IPV4('192.0.2.01'), IS_IPV4('127.1'), IS_IPV6('::1'), IS_IPV6('192.0.2.1'), IS_IPV4_COMPAT(INET6_ATON('::192.0.2.1')), IS_IPV4_COMPAT(INET6_ATON('::1')), IS_IPV4_MAPPED(INET6_ATON('::ffff:192.0.2.1')), IS_IPV4_MAPPED(INET6_ATON('2001:db8::1')), INET6_ATON('fe80::1%1'), INET6_ATON('[::1]'), IS_IPV6('fe80::1%1'), IS_IPV6('[::1]')"
                        [ Some "20010DB8000000000000000000000001"
                          Some "2001:db8::1"
                          Some "C0000201"
                          Some "192.0.2.1"
                          None
                          Some "1"
                          Some "0"
                          Some "1"
                          Some "0"
                          Some "1"
                          Some "0"
                          Some "1"
                          Some "0"
                          None
                          None
                          Some "0"
                          Some "0" ]

                testCase "byte length, ordinal, aliases, and bit-selected strings match MySQL"
                <| fun _ ->
                    expectRow
                        "SELECT BIT_LENGTH('é'), BIT_LENGTH(X'C3A9'), BIT_LENGTH(''), ORD('é'), ORD(X'C3A9'), ORD(''), ORD('😀'), MID('abcdef',2,3), MID('abcdef',-3,2), SHA('abc'), MAKE_SET(5,'a','b','c','d'), MAKE_SET(3,'a',NULL,'c'), MAKE_SET(-1,'a','b','c'), MAKE_SET(5.7,'a','b','c','d'), MAKE_SET('5.7','a','b','c','d'), ANY_VALUE(7)"
                        [ Some "16"
                          Some "16"
                          Some "0"
                          Some "50089"
                          Some "195"
                          Some "0"
                          Some "4036991104"
                          Some "bcd"
                          Some "de"
                          Some "a9993e364706816aba3e25717850c26c9cd0d89d"
                          Some "a,c"
                          Some "a"
                          Some "a,b,c"
                          Some "b,c"
                          Some "a,c"
                          Some "7" ]

                testCase "SOUNDEX and base64 preserve MySQL's extended and binary behavior"
                <| fun _ ->
                    expectRow
                        "SELECT SOUNDEX('Hello'), SOUNDEX('Ashcraft'), SOUNDEX('Pfister'), SOUNDEX('Tymczak'), SOUNDEX(''), SOUNDEX('123Robert'), SOUNDEX('Émile'), TO_BASE64('abc'), TO_BASE64(''), FROM_BASE64('YWJj'), FROM_BASE64(' YW Jj\n'), FROM_BASE64('bad!'), HEX(FROM_BASE64('/w==')), TO_BASE64(REPEAT('a',60))"
                        [ Some "H400"
                          Some "A2613"
                          Some "P236"
                          Some "T520"
                          Some ""
                          Some "R163"
                          Some "É540"
                          Some "YWJj"
                          Some ""
                          Some "abc"
                          Some "abc"
                          None
                          Some "FF"
                          Some "YWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFh\nYWFh" ]

                testCase "TRIM removes repeated substrings from the requested ends"
                <| fun _ ->
                    expectRow
                        "SELECT TRIM(BOTH 'xy' FROM 'xyxyhelloxyxy'), TRIM(LEADING 'xy' FROM 'xyxyhelloxy'), TRIM(TRAILING 'xy' FROM 'xyhelloxyxy'), TRIM('xy' FROM 'xyhelloxy'), TRIM(BOTH '' FROM '  x  '), TRIM(LEADING FROM '  x  '), TRIM(FROM '  x  ')"
                        [ Some "hello"; Some "helloxy"; Some "xyhello"; Some "hello"; Some "  x  "; Some "x  "; Some "x" ]

                testCase "time arithmetic handles datetime, signed time, fractions, and saturation"
                <| fun _ ->
                    expectRow
                        "SELECT ADDTIME('2007-12-31 23:59:59.000002','1 1:1:1.000002'), SUBTIME('2008-01-01 01:01:01.000002','1:1:1.000002'), ADDTIME('10:00:00','02:30:00'), SUBTIME('10:00:00','12:00:00'), TIMEDIFF('2000-01-02 00:00:00','2000-01-01 12:00:00'), TIMEDIFF('01:00:00','02:30:00'), SEC_TO_TIME(2378), SEC_TO_TIME(-1.5), SEC_TO_TIME(9999999), MAKETIME(12,15,30), MAKETIME(-12,15,30.5), MAKETIME(1,60,0)"
                        [ Some "2008-01-02 01:01:00.000004"
                          Some "2008-01-01 00:00:00"
                          Some "12:30:00"
                          Some "-02:00:00"
                          Some "12:00:00"
                          Some "-01:30:00"
                          Some "00:39:38"
                          Some "-00:00:01.5"
                          Some "838:59:59"
                          Some "12:15:30"
                          Some "-12:15:30.5"
                          None ]

                testCase "TIME columns retain type, precision, ordering, and numeric coercion"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE elapsed (id INT, value TIME(2) NOT NULL DEFAULT '00:00:00')" |> ignore
                    runDefault store "INSERT INTO elapsed(id, value) VALUES (1, '25:02:03.455'), (2, '-01:02:03.455')" |> ignore
                    runDefault store "INSERT INTO elapsed(id) VALUES (3)" |> ignore

                    match runDefault store "SELECT value, value < '00:00:00', value + 0 FROM elapsed ORDER BY value" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "-01:02:03.46"; Some "1"; Some "-10203.46" ]
                              [ Some "00:00:00.00"; Some "0"; Some "0" ]
                              [ Some "25:02:03.46"; Some "0"; Some "250203.46" ] ]
                            "typed time values"
                    | other -> failtestf "expected time result set, got %A" other

                testCase "TIME casts round and carry at their declared precision"
                <| fun _ ->
                    expectRow
                        "SELECT CAST('01:02:03.455' AS TIME(2)), CAST('-00:00:00.5' AS TIME(0)), TIME('01:02:03.12345678'), TIME('-00:00:00.0000005')"
                        [ Some "01:02:03.46"; Some "-00:00:01"; Some "01:02:03.123457"; Some "-00:00:00.000001" ]

                testCase "TIME values work in component and EXTRACT functions"
                <| fun _ ->
                    expectRow
                        "SELECT HOUR(TIME('-25:02:03.456789')), MINUTE(TIME('-25:02:03.456789')), SECOND(TIME('-25:02:03.456789')), MICROSECOND(TIME('-25:02:03.456789')), EXTRACT(HOUR FROM TIME('-25:02:03.456789')), EXTRACT(MICROSECOND FROM TIME('-25:02:03.456789')), EXTRACT(HOUR_MICROSECOND FROM TIME('-25:02:03.456789')), EXTRACT(DAY_HOUR FROM TIME('-25:02:03.456789')), EXTRACT(DAY FROM TIME('-25:02:03.456789'))"
                        [ Some "25"
                          Some "2"
                          Some "3"
                          Some "456789"
                          Some "-25"
                          Some "-456789"
                          Some "-250203456789"
                          Some "-25"
                          None ]

                testCase "time formatting, periods, day numbers, and seeded RAND match MySQL"
                <| fun _ ->
                    expectRow
                        "SELECT TIME_FORMAT('100:02:03.123456','%H|%k|%h|%I|%l|%i|%s|%f|%p'), PERIOD_ADD(202312,2), PERIOD_ADD(9912,2), PERIOD_DIFF(202402,202312), PERIOD_DIFF(7001,6912), TO_DAYS('0001-01-01'), TO_DAYS('1970-01-01'), TO_DAYS('2024-01-01'), FROM_DAYS(366), FROM_DAYS(719528), FROM_DAYS(739251), RAND(3), RAND(3)"
                        [ Some "100|100|04|04|4|02|03|123456|AM"
                          Some "202402"
                          Some "200002"
                          Some "2"
                          Some "-1199"
                          Some "366"
                          Some "719528"
                          Some "739251"
                          Some "0001-01-01"
                          Some "1970-01-01"
                          Some "2024-01-01"
                          Some "0.9057697559760601"
                          Some "0.9057697559760601" ]

                testCase "CONV converts both directions across bases 2..36, truncating at the first invalid digit"
                <| fun _ ->
                    expectRow
                        "SELECT CONV(255,10,16) a, CONV('ff',16,10) b, CONV('a',36,10) c, CONV(-1,10,16) d, CONV('zz',36,2) e, CONV('xyz',16,10) f, CONV('12abc',10,10) g, CONV(255,10,37) h, CONV(NULL,10,2) i"
                        [ Some "FF"
                          Some "255"
                          Some "10"
                          Some "FFFFFFFFFFFFFFFF"
                          Some "10100001111"
                          Some "0"
                          Some "12"
                          None
                          None ]

                testCase "a negative CONV to_base asks for signed output"
                <| fun _ ->
                    expectRow
                        "SELECT CONV(-5,10,-16) a, CONV(-1,10,-16) b, CONV('ffffffffffffffff',16,-10) c, CONV(255,10,-16) d, CONV(1,-10,16) e"
                        [ Some "-5"; Some "-1"; Some "-1"; Some "FF"; Some "1" ]

                testCase "MAKEDATE rolls past year end and pivots two-digit years"
                <| fun _ ->
                    expectRow
                        "SELECT MAKEDATE(2024,200) a, MAKEDATE(2024,0) b, MAKEDATE(2024,366) c, MAKEDATE(2024,367) d, MAKEDATE(70,1) e, MAKEDATE(0,1) f, MAKEDATE(100,1) g, MAKEDATE(NULL,1) h"
                        [ Some "2024-07-18"
                          None
                          Some "2024-12-31"
                          Some "2025-01-01"
                          Some "1970-01-01"
                          Some "2000-01-01"
                          Some "0100-01-01"
                          None ]

                testCase "CONVERT_TZ shifts by numeric offsets and NULLs anything else"
                <| fun _ ->
                    expectRow
                        "SELECT CONVERT_TZ('2024-01-01 12:00:00','+00:00','+05:30') a, CONVERT_TZ('2024-01-01 12:00:00','-08:00','+00:00') b, CONVERT_TZ('2024-01-01 12:00:00','+00:00','+14:00') c, CONVERT_TZ('2024-01-01 12:00:00','+00:00','+15:00') d, CONVERT_TZ('2024-01-01 12:00:00','UTC','America/New_York') e, CONVERT_TZ('2024-01-01 12:00:00','+00:00','+0530') f"
                        [ Some "2024-01-01 17:30:00"
                          Some "2024-01-01 20:00:00"
                          Some "2024-01-02 02:00:00"
                          None
                          None
                          None ]

                testCase "CONVERT_TZ resolves SYSTEM through the server local time zone"
                <| fun _ ->
                    let utc = System.DateTime(2024, 1, 15, 12, 0, 0, System.DateTimeKind.Utc)
                    let local = System.TimeZoneInfo.ConvertTimeFromUtc(utc, System.TimeZoneInfo.Local)

                    expectRow
                        "SELECT CONVERT_TZ('2024-01-15 12:00:00','+00:00','SYSTEM') a, CONVERT_TZ('2024-01-15 12:00:00','SYSTEM','+00:00') b"
                        [ Some(local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                          Some(
                              System.TimeZoneInfo
                                  .ConvertTimeToUtc(System.DateTime(2024, 1, 15, 12, 0, 0), System.TimeZoneInfo.Local)
                                  .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                          ) ]

                testCase "TIMESTAMP adds its second TIME argument"
                <| fun _ ->
                    expectRow
                        "SELECT TIMESTAMP('2024-01-02', '03:04:05') a, TIMESTAMP('2024-01-02 23:00:00', '02:30:00') b"
                        [ Some "2024-01-02 03:04:05"; Some "2024-01-03 01:30:00" ]

                testCase "MAKEDATE rounds its arguments, which decides the two-digit-year pivot"
                <| fun _ ->
                    expectRow
                        "SELECT MAKEDATE(2024,1.7) a, MAKEDATE(2023.7,1) b, MAKEDATE(69.6,1) c, MAKEDATE(2024,2.5) d, MAKEDATE(2024,2.5e0) e, MAKEDATE('2024','1.7') f"
                        [ Some "2024-01-02"
                          Some "2024-01-01"
                          Some "1970-01-01"
                          Some "2024-01-03"
                          Some "2024-01-02"
                          Some "2024-01-01" ]

                testCase "CONVERT_TZ returns its argument unchanged outside the TIMESTAMP window, and rejects offsets under -13:59"
                <| fun _ ->
                    expectRow
                        "SELECT CONVERT_TZ('1970-01-01 00:00:00','+05:00','+00:00') a, CONVERT_TZ('1000-01-01 00:00:00','+05:00','+00:00') b, CONVERT_TZ('9999-12-31 23:59:59','+00:00','+05:00') c, CONVERT_TZ('3001-01-18 23:59:59','+00:00','+05:00') d, CONVERT_TZ('3001-01-19 00:00:00','+00:00','+05:00') e, CONVERT_TZ('1970-01-01 00:00:01','+00:00','-05:00') f, CONVERT_TZ('2024-01-01 12:00:00','+00:00','-14:00') g, CONVERT_TZ('2024-01-01 12:00:00','+00:00','-13:59') h"
                        [ Some "1970-01-01 00:00:00"
                          Some "1000-01-01 00:00:00"
                          Some "9999-12-31 23:59:59"
                          Some "3001-01-19 04:59:59"
                          Some "3001-01-19 00:00:00"
                          Some "1969-12-31 19:00:01"
                          None
                          Some "2023-12-31 22:01:00" ]

                testCase "FIELD, ELT, and EXPORT_SET match the oracle's index and bit ordering"
                <| fun _ ->
                    expectRow
                        "SELECT FIELD('b','a','b','c') a, FIELD('z','a') b, FIELD(NULL,'a') c, ELT(1,'a','b') d, ELT(0,'a') e, ELT(3,'a','b') f, EXPORT_SET(5,'Y','n',',',8) g, EXPORT_SET(6,'1','0',',',10) h, EXPORT_SET(5,'Y','n','--',3) i"
                        [ Some "2"
                          Some "0"
                          Some "0"
                          Some "a"
                          None
                          None
                          Some "Y,n,Y,n,n,n,n,n"
                          Some "0,1,1,0,0,0,0,0,0,0"
                          Some "Y--n--Y" ]

                testCase "EXPORT_SET defaults to 64 bits, and caps an oversized or negative count there"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT EXPORT_SET(5,'Y','n') a, EXPORT_SET(5,'Y','n',',',100) b, EXPORT_SET(5,'Y','n',',',-1) c" with
                    | ResultSet(_, [ [ Some a; Some b; Some c ] ]) ->
                        let expected = "Y,n,Y" + String.replicate 61 ",n"
                        Expect.equal a expected "default count is 64 bits"
                        Expect.equal b expected "an oversized count caps at 64"
                        Expect.equal c expected "a negative count reads as unsigned, so also 64"
                    | other -> failtestf "expected a single row, got %A" other

                testCase "JSON_ARRAYAGG keeps NULL rows as JSON null and is NULL over an empty group"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE agg (id INT, v INT)" |> ignore
                    runDefault store "INSERT INTO agg (id, v) VALUES (1, 1), (2, NULL)" |> ignore

                    match runDefault store "SELECT CAST(JSON_ARRAYAGG(v) AS CHAR) a, JSON_LENGTH(JSON_ARRAYAGG(v)) b FROM agg" with
                    | ResultSet(_, [ [ Some "[1, null]"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected [1, null] of length 2, got %A" other

                    match runDefault store "SELECT JSON_ARRAYAGG(v) a FROM agg WHERE id = 99" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL over an empty group, got %A" other

                testCase "JSON_OBJECTAGG folds two arguments per row, last duplicate key winning"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE kv (id INT, k VARCHAR(10), v INT)" |> ignore
                    runDefault store "INSERT INTO kv (id, k, v) VALUES (1, 'a', 1), (2, 'a', 2), (3, 'bb', NULL)" |> ignore

                    match runDefault store "SELECT CAST(JSON_OBJECTAGG(k, v) AS CHAR) a FROM kv" with
                    | ResultSet(_, [ [ Some "{\"a\": 2, \"bb\": null}" ] ]) -> ()
                    | other -> failtestf "expected the last value per key, got %A" other

                    match runDefault store "SELECT JSON_OBJECTAGG(k, v) a FROM kv WHERE id = 99" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL over an empty group, got %A" other ]

          // Every expected result below was read off the MySQL 8.4.11 oracle
          // over the same [1,2,3,1,2,2] / [1,2,1,2,2] multisets.
          testList
              "set operations and the VALUES table constructor, pinned to the 8.4 oracle"
              [ let setOpStore () =
                    let store = newStore ()
                    runDefault store "CREATE TABLE s (id INT)" |> ignore
                    runDefault store "INSERT INTO s (id) VALUES (1), (2), (3), (1), (2), (2)" |> ignore
                    store

                let expectIds (sql: string) (expected: string list) =
                    match runDefault (setOpStore ()) sql with
                    | ResultSet(_, rows) -> Expect.equal rows (expected |> List.map (fun v -> [ Some v ])) sql
                    | other -> failtestf "expected a resultset from %s, got %A" sql other

                testCase "INTERSECT and EXCEPT dedupe; their ALL forms do multiset arithmetic"
                <| fun _ ->
                    expectIds "((SELECT id FROM s) INTERSECT (SELECT id FROM s WHERE id <= 2)) ORDER BY id" [ "1"; "2" ]
                    expectIds "((SELECT id FROM s) INTERSECT ALL (SELECT id FROM s WHERE id <= 2)) ORDER BY id" [ "1"; "1"; "2"; "2"; "2" ]
                    expectIds "((SELECT id FROM s) EXCEPT (SELECT id FROM s WHERE id <= 2)) ORDER BY id" [ "3" ]
                    // The right side deliberately has *lower* multiplicity than
                    // the left, so ALL keeps the duplicates DISTINCT collapses:
                    // one copy of `1` is subtracted, not every copy.
                    expectIds "((SELECT id FROM s) EXCEPT (SELECT id FROM s WHERE id = 1 LIMIT 1)) ORDER BY id" [ "2"; "3" ]

                    expectIds
                        "((SELECT id FROM s) EXCEPT ALL (SELECT id FROM s WHERE id = 1 LIMIT 1)) ORDER BY id"
                        [ "1"; "2"; "2"; "2"; "3" ]

                testCase "INTERSECT binds tighter than UNION"
                <| fun _ ->
                    // `1 UNION (2 INTERSECT 3)` is one row; a left-to-right
                    // fold would intersect {1,2} with {3} and answer nothing.
                    expectIds "SELECT 1 UNION SELECT 2 INTERSECT SELECT 3" [ "1" ]

                testCase "a set operator after a parenthesized group is refused, not misgrouped"
                <| fun _ ->
                    match Fsdb.Parser.parse "(SELECT 1 UNION SELECT 2) INTERSECT SELECT 2" with
                    | Result.Error _ -> ()
                    | Result.Ok other -> failtestf "expected a parse refusal, got %A" other

                testCase "VALUES ROW(...) is a table, joinable, with column_N default names"
                <| fun _ ->
                    match runDefault (newStore ()) "SELECT v.n, v.label FROM (VALUES ROW(1,'one'), ROW(2,'two'), ROW(3,'three')) AS v (n, label) ORDER BY v.n" with
                    | ResultSet([ "n"; "label" ], [ [ Some "1"; Some "one" ]; [ Some "2"; Some "two" ]; [ Some "3"; Some "three" ] ]) -> ()
                    | other -> failtestf "expected the three named VALUES rows, got %A" other

                    match runDefault (newStore ()) "SELECT * FROM (VALUES ROW(1,'one'), ROW(2,'two')) AS v" with
                    | ResultSet([ "column_0"; "column_1" ], [ _; _ ]) -> ()
                    | other -> failtestf "expected column_0/column_1, got %A" other

                    match runDefault (setOpStore ()) "SELECT s.id, v.label FROM s JOIN (VALUES ROW(3,'third')) AS v (n, label) ON s.id = v.n" with
                    | ResultSet(_, [ [ Some "3"; Some "third" ] ]) -> ()
                    | other -> failtestf "expected the joined VALUES row, got %A" other

                testCase "JSON_TABLE EXISTS PATH and DEFAULT ON EMPTY/ERROR"
                <| fun _ ->
                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.ord, jt.has_a FROM JSON_TABLE(CAST('[{\"a\":1},{\"b\":2}]' AS JSON), '$[*]' COLUMNS (ord FOR ORDINALITY, has_a INT EXISTS PATH '$.a')) AS jt ORDER BY jt.ord"
                    with
                    | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected EXISTS PATH to answer 1 then 0, got %A" other

                    // Row 2 has no `$.v` (ON EMPTY), row 3's 'nope' will not
                    // coerce into INT (ON ERROR), and a matched JSON null is
                    // neither — it is simply NULL.
                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.v FROM JSON_TABLE(CAST('[{\"v\":1},{\"x\":2},{\"v\":\"nope\"},{\"v\":null}]' AS JSON), '$[*]' COLUMNS (v INT PATH '$.v' DEFAULT '7' ON EMPTY DEFAULT '9' ON ERROR)) AS jt"
                    with
                    | ResultSet(_, [ [ Some "1" ]; [ Some "7" ]; [ Some "9" ]; [ None ] ]) -> ()
                    | other -> failtestf "expected 1, 7, 9, NULL, got %A" other

                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.v FROM JSON_TABLE('[{}]', '$[*]' COLUMNS (v INT PATH '$.v' ERROR ON EMPTY)) AS jt"
                    with
                    | Err(3665, "Missing value for JSON_TABLE column 'v'") -> ()
                    | other -> failtestf "expected 3665 for ERROR ON EMPTY, got %A" other

                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.v FROM JSON_TABLE('[{\"v\":\"x\"}]', '$[*]' COLUMNS (v INT PATH '$.v' ERROR ON ERROR)) AS jt"
                    with
                    | Err(3156, "Invalid JSON value for CAST to INTEGER from column v at row 1") -> ()
                    | other -> failtestf "expected 3156 for ERROR ON ERROR, got %A" other

                    // The DEFAULT clause takes JSON *text*, so the substituted
                    // string arrives unquoted; the un-JSON 'zz' (oracle 3141)
                    // and the bare number 7 (oracle 1235) are refused.
                    match
                        runDefault
                            (newStore ())
                            "SELECT jt.v FROM JSON_TABLE(CAST('[{}]' AS JSON), '$[*]' COLUMNS (v VARCHAR(10) PATH '$.v' DEFAULT '\"zz\"' ON EMPTY)) AS jt"
                    with
                    | ResultSet(_, [ [ Some "zz" ] ]) -> ()
                    | other -> failtestf "expected the JSON string 'zz' unquoted, got %A" other

                    for sql in
                        [ "SELECT * FROM JSON_TABLE('[{}]', '$[*]' COLUMNS (v VARCHAR(10) PATH '$.v' DEFAULT 'zz' ON EMPTY)) AS jt"
                          "SELECT * FROM JSON_TABLE('[{}]', '$[*]' COLUMNS (v INT PATH '$.v' DEFAULT 7 ON EMPTY)) AS jt" ] do
                        match Fsdb.Parser.parse sql with
                        | Result.Error _ -> ()
                        | Result.Ok other -> failtestf "expected a parse refusal for %s, got %A" sql other ]

          // Every expectation below was read off the MySQL 8.4.11 oracle over
          // the same six-row table (id, v, g, d):
          //   (1,10,1,1.50) (2,10,1,2.25) (3,30,2,3.00)
          //   (4,NULL,2,4.75) (5,50,2,5.10) (6,50,3,6.00)
          testList
              "window frames, the wider window-function set, and named windows"
              [ let windowStore () =
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT, g INT, d DECIMAL(10,2))" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES (1,10,1,1.50),(2,10,1,2.25),(3,30,2,3.00),(4,NULL,2,4.75),(5,50,2,5.10),(6,50,3,6.00)"
                    |> ignore

                    store

                let expectRows (sql: string) (expected: string option list list) =
                    match runDefault (windowStore ()) sql with
                    | ResultSet(_, rows) -> Expect.equal rows expected sql
                    | other -> failtestf "expected a resultset from %s, got %A" sql other

                let expectError (sql: string) (code: int) (message: string) =
                    match runDefault (windowStore ()) sql with
                    | Err(actualCode, actualMessage) ->
                        Expect.equal (actualCode, actualMessage) (code, message) sql
                    | other -> failtestf "expected error %d from %s, got %A" code sql other

                testCase "ROWS BETWEEN n PRECEDING AND CURRENT ROW is a trailing window"
                <| fun _ ->
                    expectRows
                        "SELECT id, SUM(v) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t ORDER BY id"
                        [ [ Some "1"; Some "10" ]
                          [ Some "2"; Some "20" ]
                          [ Some "3"; Some "50" ]
                          [ Some "4"; Some "40" ]
                          [ Some "5"; Some "80" ]
                          [ Some "6"; Some "100" ] ]

                testCase "a frame starting past the last row is empty, not clamped"
                <| fun _ ->
                    expectRows
                        "SELECT id, SUM(v) OVER (ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING) AS s FROM t ORDER BY id"
                        [ [ Some "1"; Some "40" ]
                          [ Some "2"; Some "30" ]
                          [ Some "3"; Some "50" ]
                          [ Some "4"; Some "100" ]
                          [ Some "5"; Some "50" ]
                          [ Some "6"; None ] ]

                testCase "RANGE frames count value distance, so peers enter together"
                <| fun _ ->
                    expectRows
                        "SELECT id, COUNT(*) OVER (ORDER BY id RANGE BETWEEN 1 PRECEDING AND 1 FOLLOWING) AS c FROM t ORDER BY id"
                        [ [ Some "1"; Some "2" ]
                          [ Some "2"; Some "3" ]
                          [ Some "3"; Some "3" ]
                          [ Some "4"; Some "3" ]
                          [ Some "5"; Some "3" ]
                          [ Some "6"; Some "2" ] ]

                testCase "a RANGE offset may be fractional and follows a DESC ORDER BY"
                <| fun _ ->
                    expectRows
                        "SELECT id, SUM(v) OVER (ORDER BY d RANGE BETWEEN 1.5 PRECEDING AND 1.5 FOLLOWING) AS s FROM t ORDER BY id"
                        [ [ Some "1"; Some "50" ]
                          [ Some "2"; Some "50" ]
                          [ Some "3"; Some "50" ]
                          [ Some "4"; Some "100" ]
                          [ Some "5"; Some "100" ]
                          [ Some "6"; Some "100" ] ]

                testCase "temporal RANGE offsets use calendar interval arithmetic"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE temporal_range (dt DATETIME, n INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO temporal_range VALUES ('2026-01-01',1),('2026-01-02',2),('2026-01-04',4),(NULL,8)"
                    |> ignore

                    match
                        runDefault
                            store
                            ("SELECT dt, SUM(n) OVER (ORDER BY dt RANGE BETWEEN INTERVAL 1 DAY PRECEDING AND CURRENT ROW), "
                             + "SUM(n) OVER (ORDER BY dt DESC RANGE BETWEEN INTERVAL 1 DAY PRECEDING AND CURRENT ROW) "
                             + "FROM temporal_range ORDER BY dt")
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ None; Some "8"; Some "8" ]
                              [ Some "2026-01-01 00:00:00"; Some "1"; Some "3" ]
                              [ Some "2026-01-02 00:00:00"; Some "3"; Some "2" ]
                              [ Some "2026-01-04 00:00:00"; Some "4"; Some "4" ] ]
                            "DAY offsets follow the window direction and keep NULL peers"
                    | other -> failtestf "expected a temporal RANGE resultset, got %A" other

                    runDefault store "TRUNCATE temporal_range" |> ignore
                    runDefault store "INSERT INTO temporal_range VALUES ('2026-01-31',1),('2026-02-28',2),('2026-03-01',4)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT dt, SUM(n) OVER (ORDER BY dt RANGE BETWEEN INTERVAL 1 MONTH PRECEDING AND CURRENT ROW) FROM temporal_range ORDER BY dt"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "2026-01-31 00:00:00"; Some "1" ]
                              [ Some "2026-02-28 00:00:00"; Some "3" ]
                              [ Some "2026-03-01 00:00:00"; Some "6" ] ]
                            "MONTH offsets retain calendar month behavior"
                    | other -> failtestf "expected a calendar RANGE resultset, got %A" other

                    runDefault store "DROP TABLE temporal_range" |> ignore
                    runDefault store "CREATE TABLE temporal_range (tm TIME, n INT)" |> ignore
                    runDefault store "INSERT INTO temporal_range VALUES ('01:00:00',1),('01:30:00',2),('03:00:00',4)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT tm, SUM(n) OVER (ORDER BY tm RANGE BETWEEN INTERVAL 1 HOUR PRECEDING AND CURRENT ROW) FROM temporal_range ORDER BY tm"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "01:00:00"; Some "1" ]; [ Some "01:30:00"; Some "3" ]; [ Some "03:00:00"; Some "4" ] ]
                            "TIME values accept fixed-duration interval frames"
                    | other -> failtestf "expected a TIME RANGE resultset, got %A" other

                testCase "the default frame with an ORDER BY is a running total, without one the whole partition"
                <| fun _ ->
                    expectRows
                        "SELECT id, SUM(d) OVER (PARTITION BY g ORDER BY id) AS running, SUM(d) OVER (PARTITION BY g) AS total FROM t ORDER BY id"
                        [ [ Some "1"; Some "1.50"; Some "3.75" ]
                          [ Some "2"; Some "3.75"; Some "3.75" ]
                          [ Some "3"; Some "3.00"; Some "12.85" ]
                          [ Some "4"; Some "7.75"; Some "12.85" ]
                          [ Some "5"; Some "12.85"; Some "12.85" ]
                          [ Some "6"; Some "6.00"; Some "6.00" ] ]

                testCase "LAG/LEAD take an offset and a default for rows outside the partition"
                <| fun _ ->
                    expectRows
                        "SELECT id, LAG(v, 2, -1) OVER (ORDER BY id) AS l2, LEAD(v, 3, -1) OVER (ORDER BY id) AS d3 FROM t ORDER BY id"
                        [ [ Some "1"; Some "-1"; None ]
                          [ Some "2"; Some "-1"; Some "50" ]
                          [ Some "3"; Some "10"; Some "50" ]
                          [ Some "4"; Some "10"; Some "-1" ]
                          [ Some "5"; Some "30"; Some "-1" ]
                          [ Some "6"; None; Some "-1" ] ]

                testCase "FIRST_VALUE/LAST_VALUE/NTH_VALUE over a named whole-partition window"
                <| fun _ ->
                    expectRows
                        ("SELECT id, FIRST_VALUE(v) OVER w AS f, LAST_VALUE(v) OVER w AS l, NTH_VALUE(v, 2) OVER w AS n2 FROM t "
                         + "WINDOW w AS (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) ORDER BY id LIMIT 2")
                        [ [ Some "1"; Some "10"; Some "50"; Some "10" ]
                          [ Some "2"; Some "10"; Some "50"; Some "10" ] ]

                testCase "inline and named windows inherit partitioning"
                <| fun _ ->
                    expectRows
                        ("SELECT id, SUM(v) OVER (partitioned ORDER BY id) AS inline_sum, SUM(v) OVER ordered AS named_sum FROM t "
                         + "WINDOW partitioned AS (PARTITION BY g), ordered AS (partitioned ORDER BY id) ORDER BY id")
                        [ [ Some "1"; Some "10"; Some "10" ]
                          [ Some "2"; Some "20"; Some "20" ]
                          [ Some "3"; Some "30"; Some "30" ]
                          [ Some "4"; Some "30"; Some "30" ]
                          [ Some "5"; Some "80"; Some "80" ]
                          [ Some "6"; Some "50"; Some "50" ] ]

                testCase "NTH_VALUE past the end of the frame is NULL"
                <| fun _ ->
                    expectRows
                        "SELECT id, NTH_VALUE(v, 3) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS n3 FROM t ORDER BY id LIMIT 3"
                        [ [ Some "1"; None ]; [ Some "2"; None ]; [ Some "3"; Some "30" ] ]

                testCase "PERCENT_RANK, CUME_DIST and NTILE over a tied ORDER BY"
                <| fun _ ->
                    expectRows
                        ("SELECT id, ROUND(PERCENT_RANK() OVER (ORDER BY v), 4) AS pr, ROUND(CUME_DIST() OVER (ORDER BY v), 4) AS cd, "
                         + "NTILE(3) OVER (ORDER BY id) AS bucket FROM t ORDER BY id")
                        [ [ Some "1"; Some "0.2"; Some "0.5"; Some "1" ]
                          [ Some "2"; Some "0.2"; Some "0.5"; Some "1" ]
                          [ Some "3"; Some "0.6"; Some "0.6667"; Some "2" ]
                          [ Some "4"; Some "0"; Some "0.1667"; Some "2" ]
                          [ Some "5"; Some "0.8"; Some "1"; Some "3" ]
                          [ Some "6"; Some "0.8"; Some "1"; Some "3" ] ]

                testCase "an aggregate window over grouped rows runs after the GROUP BY"
                <| fun _ ->
                    expectRows
                        "SELECT g, COUNT(*) AS c, SUM(COUNT(*)) OVER (ORDER BY g ROWS UNBOUNDED PRECEDING) AS running FROM t GROUP BY g ORDER BY g"
                        [ [ Some "1"; Some "2"; Some "2" ]
                          [ Some "2"; Some "3"; Some "5" ]
                          [ Some "3"; Some "1"; Some "6" ] ]

                testCase "a window function over an aggregate's output ranks the groups"
                <| fun _ ->
                    expectRows
                        "SELECT g, SUM(v) AS sv, RANK() OVER (ORDER BY SUM(v) DESC) AS r FROM t GROUP BY g ORDER BY g"
                        [ [ Some "1"; Some "20"; Some "3" ]
                          [ Some "2"; Some "80"; Some "1" ]
                          [ Some "3"; Some "50"; Some "2" ] ]

                testCase "an undefined window name is MySQL's 3579"
                <| fun _ ->
                    expectError
                        "SELECT SUM(v) OVER nosuch FROM t"
                        3579
                        "Window name 'nosuch' is not defined."

                testCase "invalid named-window dependencies use MySQL's 3580-3583 errors"
                <| fun _ ->
                    expectError
                        "SELECT SUM(v) OVER w FROM t WINDOW w AS (w)"
                        3580
                        "There is a circularity in the window dependency graph."

                    expectError
                        "SELECT SUM(v) OVER w2 FROM t WINDOW w AS (PARTITION BY g), w2 AS (w PARTITION BY id)"
                        3581
                        "A window which depends on another cannot define partitioning."

                    expectError
                        "SELECT SUM(v) OVER w2 FROM t WINDOW w AS (ORDER BY id ROWS UNBOUNDED PRECEDING), w2 AS (w)"
                        3582
                        "Window 'w' has a frame definition, so cannot be referenced by another window."

                    expectError
                        "SELECT SUM(v) OVER w2 FROM t WINDOW w AS (ORDER BY id), w2 AS (w ORDER BY v)"
                        3583
                        "Window 'w2' cannot inherit 'w' since both contain an ORDER BY clause."

                testCase "frame bound rules match the oracle's 3584/3585/3586/3587"
                <| fun _ ->
                    expectError
                        "SELECT SUM(v) OVER (ORDER BY id ROWS BETWEEN UNBOUNDED FOLLOWING AND CURRENT ROW) FROM t"
                        3584
                        "Window '<unnamed window>': frame start cannot be UNBOUNDED FOLLOWING."

                    expectError
                        "SELECT SUM(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED PRECEDING) FROM t"
                        3585
                        "Window '<unnamed window>': frame end cannot be UNBOUNDED PRECEDING."

                    expectError
                        "SELECT SUM(v) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND 1 PRECEDING) FROM t"
                        3586
                        "Window '<unnamed window>': frame start or end is negative, NULL or of non-integral type"

                    expectError
                        "SELECT SUM(v) OVER w FROM t WINDOW w AS (ORDER BY id, v RANGE BETWEEN 1 PRECEDING AND CURRENT ROW)"
                        3587
                        "Window 'w' with RANGE N PRECEDING/FOLLOWING frame requires exactly one ORDER BY expression, of numeric or temporal type"

                testCase "a window function in WHERE is MySQL's 3593"
                <| fun _ ->
                    expectError
                        "SELECT id FROM t WHERE ROW_NUMBER() OVER (ORDER BY id) = 1"
                        3593
                        "You cannot use the window function 'row_number' in this context.'"

                testCase "GROUP_CONCAT and DISTINCT as window aggregates are MySQL's 1235 refusals"
                <| fun _ ->
                    expectError
                        "SELECT GROUP_CONCAT(v) OVER (ORDER BY id) FROM t"
                        1235
                        "This version of MySQL doesn't yet support 'group_concat as window function'"

                    expectError
                        "SELECT SUM(DISTINCT v) OVER (ORDER BY id) FROM t"
                        1235
                        "This version of MySQL doesn't yet support '<window function>(DISTINCT ..)'"

                testCase "a negative LAG offset or frame bound is a parse error, like MySQL's grammar"
                <| fun _ ->
                    for sql in
                        [ "SELECT LAG(v, -1) OVER (ORDER BY id) FROM t"
                          "SELECT SUM(v) OVER (ORDER BY id ROWS BETWEEN -1 PRECEDING AND CURRENT ROW) FROM t" ] do
                        match Fsdb.Parser.parse sql with
                        | Result.Error _ -> ()
                        | Result.Ok other -> failtestf "expected a parse refusal for %s, got %A" sql other ]

          testList
              "spatial values"
              [ testCase "a POINT column preserves WKT, SRID, and accessors"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE places (shape POINT)" |> ignore
                    runDefault store "INSERT INTO places VALUES (ST_GeomFromText('POINT(1.5 -2)', 4326))" |> ignore

                    match runDefault store "SELECT ST_AsText(shape), ST_SRID(shape), ST_X(shape), ST_Y(shape) FROM places" with
                    | ResultSet(_, [ [ Some "POINT(1.5 -2)"; Some "4326"; Some "1.5"; Some "-2" ] ]) -> ()
                    | other -> failtestf "expected spatial accessors, got %A" other

                testCase "a concrete spatial column rejects a geometry of another type"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE places (shape POINT)" |> ignore

                    match runDefault store "INSERT INTO places VALUES (ST_GeomFromText('LINESTRING(0 0,1 1)'))" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected type coercion error, got %A" other

                testCase "spatial columns refuse primary and secondary indexes"
                <| fun _ ->
                    match runDefault (newStore ()) "CREATE TABLE places (shape POINT PRIMARY KEY)" with
                    | Err(3728, _) -> ()
                    | other -> failtestf "expected spatial primary key refusal, got %A" other ]

          // Expectations read off the MySQL 8.4.11 oracle over
          //   t = (1,10,1) (2,20,1) (3,30,2) (4,40,2) (5,50,2)  [id, v, g]
          testList
              "common table expressions"
              [ let cteStore () =
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT, g INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1,10,1),(2,20,1),(3,30,2),(4,40,2),(5,50,2)" |> ignore
                    store

                let expectRows (sql: string) (expected: string option list list) =
                    match runDefault (cteStore ()) sql with
                    | ResultSet(_, rows) -> Expect.equal rows expected sql
                    | other -> failtestf "expected a resultset from %s, got %A" sql other

                testCase "a single CTE feeds the main query's GROUP BY"
                <| fun _ ->
                    expectRows
                        "WITH b AS (SELECT id, id MOD 2 AS bucket, v FROM t) SELECT bucket, COUNT(*) AS c, SUM(v) AS s FROM b GROUP BY bucket ORDER BY bucket"
                        [ [ Some "0"; Some "2"; Some "60" ]; [ Some "1"; Some "3"; Some "90" ] ]

                testCase "a later CTE reads an earlier one"
                <| fun _ ->
                    expectRows
                        "WITH lo AS (SELECT id, v FROM t WHERE id <= 3), hi AS (SELECT id, v FROM lo WHERE v > 10) SELECT COUNT(*) AS c, MIN(id) AS mn, MAX(id) AS mx FROM hi"
                        [ [ Some "2"; Some "2"; Some "3" ] ]

                testCase "unused CTEs are not evaluated"
                <| fun _ ->
                    expectRows "WITH unused AS (SELECT missing) SELECT 1" [ [ Some "1" ] ]

                    let store = cteStore ()
                    match runDefault store "WITH unused AS (SELECT missing) UPDATE t SET v = v + 1" with
                    | Affected 5UL -> ()
                    | other -> failtestf "expected the update to ignore its unused CTE, got %A" other

                    match runDefault store "EXPLAIN WITH unused AS (SELECT missing) DELETE FROM t WHERE id < 0" with
                    | ResultSet _ -> ()
                    | other -> failtestf "expected EXPLAIN to ignore its unused CTE, got %A" other

                testCase "a CTE referenced twice in one FROM materializes once and joins"
                <| fun _ ->
                    expectRows "WITH x AS (SELECT 1 AS a) SELECT * FROM x JOIN x AS y ON x.a = y.a" [ [ Some "1"; Some "1" ] ]

                testCase "a CTE shadows a real table of the same name"
                <| fun _ -> expectRows "WITH t AS (SELECT 99 AS id) SELECT * FROM t" [ [ Some "99" ] ]

                testCase "a CTE carrying a window function filters on its rank"
                <| fun _ ->
                    expectRows
                        ("WITH ranked AS (SELECT g, id, v, ROW_NUMBER() OVER (PARTITION BY g ORDER BY v DESC, id) AS r FROM t) "
                         + "SELECT g, id, v FROM ranked WHERE r = 1 ORDER BY g")
                        [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "5"; Some "50" ] ]

                testCase "CTEs compose inside expression and derived-table subqueries"
                <| fun _ ->
                    expectRows
                        ("WITH outer_c AS (SELECT 5 AS n) "
                         + "SELECT "
                         + "(WITH inner_c AS (SELECT n + 1 AS n FROM outer_c) SELECT n FROM inner_c), "
                         + "EXISTS (WITH present AS (SELECT 1 AS n) SELECT n FROM present), "
                         + "2 IN (WITH values_c AS (SELECT 1 AS n UNION ALL SELECT 2) SELECT n FROM values_c), "
                         + "2 = ANY (WITH quantified_c AS (SELECT 1 AS n UNION ALL SELECT 2) SELECT n FROM quantified_c), "
                         + "d.n "
                         + "FROM (WITH derived_c AS (SELECT 42 AS n) SELECT n FROM derived_c UNION ALL SELECT 43 LIMIT 1) AS d")
                        [ [ Some "6"; Some "1"; Some "1"; Some "1"; Some "42" ] ]

                testCase "set operations compose inside expression subqueries"
                <| fun _ ->
                    expectRows
                        ("SELECT "
                         + "(SELECT 1 UNION SELECT 1), "
                         + "EXISTS (SELECT 1 WHERE FALSE UNION ALL SELECT 2), "
                         + "2 IN (SELECT 1 UNION ALL SELECT 2), "
                         + "2 = ANY (SELECT 1 UNION ALL SELECT 2), "
                         + "2 < ALL (SELECT 3 UNION ALL SELECT 4)")
                        [ [ Some "1"; Some "1"; Some "1"; Some "1"; Some "1" ] ]

                    match runDefault (newStore ()) "SELECT (SELECT 1 UNION ALL SELECT 2)" with
                    | Err(1242, _) -> ()
                    | other -> failtestf "expected scalar set-expression cardinality error 1242, got %A" other

                testCase "set-operation subqueries retain correlation and collation"
                <| fun _ ->
                    let store = cteStore ()
                    runDefault store "CREATE TABLE set_bin (value VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO set_bin VALUES ('a'), ('z')" |> ignore

                    match runDefault store "SELECT id, id IN (SELECT 0 UNION ALL SELECT t.id) FROM t WHERE id <= 2 ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ] ] "correlated union"
                    | other -> failtestf "expected a correlated set-operation result, got %A" other

                    match
                        runDefault
                            store
                            "SELECT _utf8mb4'A' = ANY (SELECT value FROM set_bin UNION ALL SELECT value FROM set_bin)"
                    with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "0" ] ] "binary union collation"
                    | other -> failtestf "expected a collation-aware set-operation result, got %A" other

                testCase "an inner CTE shadows its outer scope without leaking"
                <| fun _ ->
                    expectRows
                        "WITH c AS (SELECT 1 AS n) SELECT (WITH c AS (SELECT 2 AS n) SELECT n FROM c), (SELECT n FROM c)"
                        [ [ Some "2"; Some "1" ] ]

                testCase "a nested CTE may reference its enclosing query row"
                <| fun _ ->
                    expectRows
                        "SELECT id, (WITH c AS (SELECT t.id + 1 AS n) SELECT n FROM c) FROM t WHERE id <= 2 ORDER BY id"
                        [ [ Some "1"; Some "2" ]; [ Some "2"; Some "3" ] ]

                testCase "a correlated recursive CTE keeps its row context across iterations"
                <| fun _ ->
                    expectRows
                        ("SELECT id, (WITH RECURSIVE c(n) AS ("
                         + "SELECT t.id UNION ALL SELECT n + 1 FROM c WHERE n < t.id + 1"
                         + ") SELECT MAX(n) FROM c) FROM t WHERE id <= 2 ORDER BY id")
                        [ [ Some "1"; Some "2" ]; [ Some "2"; Some "3" ] ]

                testCase "a CTE body may contain its own WITH clause"
                <| fun _ ->
                    expectRows
                        "WITH outer_c AS (WITH inner_c AS (SELECT 42 AS n) SELECT n FROM inner_c) SELECT n FROM outer_c"
                        [ [ Some "42" ] ]

                testCase "a parenthesized set branch keeps its own CTE scope"
                <| fun _ ->
                    expectRows
                        "(WITH c AS (SELECT 2 AS n) SELECT n FROM c) UNION ALL (SELECT 1) ORDER BY n"
                        [ [ Some "1" ]; [ Some "2" ] ]

                testCase "duplicate CTE names are rejected before execution"
                <| fun _ ->
                    match runDefault (cteStore ()) "WITH c AS (SELECT 1 AS n), c AS (SELECT 2 AS n) SELECT n FROM c" with
                    | Err(1066, "Not unique table/alias: 'c'") -> ()
                    | other -> failtestf "expected duplicate CTE error 1066, got %A" other

                testCase "INSERT SELECT accepts a CTE source"
                <| fun _ ->
                    let store = cteStore ()
                    runDefault store "CREATE TABLE inserted (id INT PRIMARY KEY)" |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO inserted (id) WITH c AS (SELECT 1 AS n UNION ALL SELECT 2) SELECT n FROM c"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected two inserted rows, got %A" other

                    match runDefault store "SELECT id FROM inserted ORDER BY id" with
                    | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected CTE rows in the target table, got %A" other

                testCase "UPDATE and DELETE consume leading CTEs"
                <| fun _ ->
                    let store = cteStore ()

                    match
                        runDefault
                            store
                            "WITH changed AS (SELECT id FROM t WHERE g = 2) UPDATE t SET v = v + 1 WHERE id IN (SELECT id FROM changed)"
                    with
                    | Affected 3UL -> ()
                    | other -> failtestf "expected three CTE updates, got %A" other

                    match
                        runDefault
                            store
                            "WITH RECURSIVE removed (id) AS (SELECT 1 UNION ALL SELECT id + 1 FROM removed WHERE id < 2) DELETE FROM t WHERE id IN (SELECT id FROM removed)"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected two CTE deletes, got %A" other

                    match runDefault store "SELECT id, v FROM t ORDER BY id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "3"; Some "31" ]; [ Some "4"; Some "41" ]; [ Some "5"; Some "51" ] ]
                            "both mutations retain their CTE scope"
                    | other -> failtestf "expected the remaining rows, got %A" other

                testCase "REPLACE SELECT accepts a CTE source"
                <| fun _ ->
                    let store = cteStore ()
                    runDefault store "CREATE TABLE replaced (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO replaced VALUES (1, 10)" |> ignore

                    match runDefault store "REPLACE INTO replaced WITH c AS (SELECT 1 AS id, 20 AS n) SELECT id, n FROM c" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected one replaced row, got %A" other

                    match runDefault store "SELECT id, n FROM replaced" with
                    | ResultSet(_, [ [ Some "1"; Some "20" ] ]) -> ()
                    | other -> failtestf "expected the CTE replacement, got %A" other

                testCase "WITH RECURSIVE generates a series and renames its column"
                <| fun _ ->
                    expectRows
                        "WITH RECURSIVE seq (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5) SELECT n, n * n AS sq FROM seq ORDER BY n"
                        [ [ Some "1"; Some "1" ]
                          [ Some "2"; Some "4" ]
                          [ Some "3"; Some "9" ]
                          [ Some "4"; Some "16" ]
                          [ Some "5"; Some "25" ] ]

                testCase "a recursive UNION (distinct) stops once a pass adds nothing new"
                <| fun _ ->
                    expectRows
                        "WITH RECURSIVE s AS (SELECT 1 AS n UNION SELECT n + 1 FROM s WHERE n < 3) SELECT * FROM s"
                        [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ]

                testCase "a recursive series joins a real table"
                <| fun _ ->
                    expectRows
                        ("WITH RECURSIVE bk (b) AS (SELECT 0 UNION ALL SELECT b + 1 FROM bk WHERE b < 3) "
                         + "SELECT bk.b, COUNT(v.id) AS c FROM bk LEFT JOIN t AS v ON v.g = bk.b GROUP BY bk.b ORDER BY bk.b")
                        [ [ Some "0"; Some "0" ]
                          [ Some "1"; Some "2" ]
                          [ Some "2"; Some "3" ]
                          [ Some "3"; Some "0" ] ]

                testCase "the recursion ceiling is MySQL's 1000 iterations, to the row"
                <| fun _ ->
                    expectRows
                        "WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < 1000) SELECT COUNT(*) AS c, MAX(n) AS m FROM s"
                        [ [ Some "1000"; Some "1000" ] ]

                    match runDefault (cteStore ()) "WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < 1001) SELECT COUNT(*) FROM s" with
                    | Err(3636, message) ->
                        Expect.equal
                            message
                            "Recursive query aborted after 1001 iterations. Try increasing @@cte_max_recursion_depth to a larger value."
                            "the oracle's 3636 wording"
                    | other -> failtestf "expected 3636, got %A" other

                testCase "a self-referencing CTE with no UNION is MySQL's 3573"
                <| fun _ ->
                    match runDefault (cteStore ()) "WITH RECURSIVE s AS (SELECT n + 1 AS n FROM s) SELECT * FROM s" with
                    | Err(3573, "Recursive Common Table Expression 's' should contain a UNION") -> ()
                    | other -> failtestf "expected 3573, got %A" other

                testCase "a column-list/SELECT-list width mismatch is MySQL's 1353"
                <| fun _ ->
                    match runDefault (cteStore ()) "WITH x (a, b) AS (SELECT 1) SELECT * FROM x" with
                    | Err(1353, message) ->
                        Expect.equal
                            message
                            "In definition of view, derived table or common table expression, SELECT list and column names list have different column counts"
                            "the oracle's 1353 wording"
                    | other -> failtestf "expected 1353, got %A" other ]

          // Expectations read off the MySQL 8.4.11 oracle over
          //   t = ('x',1,10) ('x',2,20) ('y',1,30) ('y',NULL,40) (NULL,3,50)
          //   [a, b, v]
          testList
              "GROUP BY ... WITH ROLLUP and GROUPING()"
              [ let rollupStore () =
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (a VARCHAR(5), b INT, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('x',1,10),('x',2,20),('y',1,30),('y',NULL,40),(NULL,3,50)" |> ignore
                    store

                let expectRows (sql: string) (expected: string option list list) =
                    match runDefault (rollupStore ()) sql with
                    | ResultSet(_, rows) -> Expect.equal rows expected sql
                    | other -> failtestf "expected a resultset from %s, got %A" sql other

                testCase "one super-aggregate row per dropped suffix, in MySQL's own order"
                <| fun _ ->
                    expectRows
                        "SELECT a, b, COUNT(*) AS c, SUM(v) AS s FROM t GROUP BY a, b WITH ROLLUP"
                        [ [ None; Some "3"; Some "1"; Some "50" ]
                          [ None; None; Some "1"; Some "50" ]
                          [ Some "x"; Some "1"; Some "1"; Some "10" ]
                          [ Some "x"; Some "2"; Some "1"; Some "20" ]
                          [ Some "x"; None; Some "2"; Some "30" ]
                          [ Some "y"; None; Some "1"; Some "40" ]
                          [ Some "y"; Some "1"; Some "1"; Some "30" ]
                          [ Some "y"; None; Some "2"; Some "70" ]
                          [ None; None; Some "5"; Some "150" ] ]

                testCase "GROUPING tells a rolled-up NULL from a real one, and packs several keys into a bitmask"
                <| fun _ ->
                    expectRows
                        "SELECT a, b, GROUPING(a) AS ga, GROUPING(b) AS gb, GROUPING(a, b) AS gab FROM t GROUP BY a, b WITH ROLLUP"
                        [ [ None; Some "3"; Some "0"; Some "0"; Some "0" ]
                          [ None; None; Some "0"; Some "1"; Some "1" ]
                          [ Some "x"; Some "1"; Some "0"; Some "0"; Some "0" ]
                          [ Some "x"; Some "2"; Some "0"; Some "0"; Some "0" ]
                          [ Some "x"; None; Some "0"; Some "1"; Some "1" ]
                          [ Some "y"; None; Some "0"; Some "0"; Some "0" ]
                          [ Some "y"; Some "1"; Some "0"; Some "0"; Some "0" ]
                          [ Some "y"; None; Some "0"; Some "1"; Some "1" ]
                          [ None; None; Some "1"; Some "1"; Some "3" ] ]

                testCase "an expression group key rolls up like a column one"
                <| fun _ ->
                    expectRows
                        "SELECT b MOD 2 AS m, COUNT(*) AS c, GROUPING(b MOD 2) AS g FROM t GROUP BY b MOD 2 WITH ROLLUP"
                        [ [ None; Some "1"; Some "0" ]
                          [ Some "0"; Some "1"; Some "0" ]
                          [ Some "1"; Some "3"; Some "0" ]
                          [ None; Some "5"; Some "1" ] ]

                testCase "HAVING and ORDER BY both see GROUPING"
                <| fun _ ->
                    expectRows "SELECT a, COUNT(*) AS c FROM t GROUP BY a WITH ROLLUP HAVING GROUPING(a) = 1" [ [ None; Some "5" ] ]

                    expectRows
                        "SELECT a, SUM(v) AS s FROM t GROUP BY a WITH ROLLUP ORDER BY a DESC"
                        [ [ Some "y"; Some "70" ]
                          [ Some "x"; Some "30" ]
                          [ None; Some "50" ]
                          [ None; Some "150" ] ]

                testCase "GROUPING without ROLLUP is 1111, and over a non-key is 3602"
                <| fun _ ->
                    match runDefault (rollupStore ()) "SELECT a, GROUPING(a) FROM t GROUP BY a" with
                    | Err(1111, "Invalid use of group function") -> ()
                    | other -> failtestf "expected 1111, got %A" other

                    match runDefault (rollupStore ()) "SELECT a, GROUPING(b) FROM t GROUP BY a WITH ROLLUP" with
                    | Err(3602, "Argument #1 of GROUPING function is not in GROUP BY") -> ()
                    | other -> failtestf "expected 3602, got %A" other ] ]
