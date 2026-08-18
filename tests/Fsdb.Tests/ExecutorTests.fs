module Fsdb.Tests.ExecutorTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

/// Parses `sql` and runs it against a fresh in-memory store, failing the
/// test with the parse error if the SQL itself doesn't parse — end-to-end
/// statement tests read as plain "run this SQL, check this QueryResult".
let private run (store: Store) (registry: Registry) (sql: string) : QueryResult =
    match Fsdb.Parser.parse sql with
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
    | Ok stmt -> execute store registry defaultDatabase (0L, 0L) stmt |> snd

let private runDefault (store: Store) (sql: string) : QueryResult = run store builtins sql

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
                      "SELECT 'i' COLLATE utf8mb4_tr_0900_ai_ci = 'I'", "0"
                      "SELECT 'ı' COLLATE utf8mb4_tr_0900_ai_ci = 'I'", "1"
                      "SELECT 'ñ' COLLATE utf8mb4_es_0900_ai_ci = 'n'", "0" ]
                    <| fun (sql, expected) ->
                        match runDefault (newStore ()) sql with
                        | ResultSet(_, [ [ Some v ] ]) -> Expect.equal v expected sql
                        | other -> failtestf "expected a resultset for %s, got %A" sql other

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

                testCase "latin1 columns store latin1-encodable text; ascii columns reject non-ascii with 1366"
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

                    // latin1 is cp1252: € is encodable and survives the
                    // round-trip; a char cp1252 lacks maps to '?' — both
                    // even in strict mode (MySQL-verified).
                    runDefault store "INSERT INTO lat VALUES ('€'), ('☃')" |> ignore

                    match runDefault store "SELECT COUNT(*) FROM lat WHERE v = '€'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected € to store as € under latin1/cp1252, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM lat WHERE v = '?'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected ☃ to store as ?, got %A" other

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

                testCase "the binary-operator form of INTERVAL arithmetic (expr - INTERVAL n unit) does real date math, like DATE_SUB"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT DATE('2024-03-15') - INTERVAL 10 DAY, DATE('2024-03-15') + INTERVAL 1 MONTH" with
                    | ResultSet(_, [ [ minus; plus ] ]) ->
                        Expect.equal minus (Some "2024-03-05") "subtracting 10 days"
                        Expect.equal plus (Some "2024-04-15") "adding 1 month"
                    | other -> failtestf "expected a resultset, got %A" other ]

          testList
              "UPDATE / DELETE"
              [ testCase "UPDATE changes only matching rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice', 30), ('bob', 25)" |> ignore

                    match runDefault store "UPDATE users SET age = 31 WHERE name = 'alice'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT name, age FROM users ORDER BY name" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "31" ]; [ Some "bob"; Some "25" ] ] "only alice's age changed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE reports 0 affected for a no-op write to an already-matching row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 5)" |> ignore

                    match runDefault store "UPDATE t SET v = v WHERE id = 1" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected 0 rows affected (matched but unchanged), got %A" other

                testCase "UPDATE SET assignments evaluate left-to-right: a later one sees an earlier one's new value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE s1 (id INT, a INT, b INT)" |> ignore
                    runDefault store "INSERT INTO s1 VALUES (1, 1, 2)" |> ignore

                    match runDefault store "UPDATE s1 SET a = 10, b = a" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT a, b FROM s1" with
                    | ResultSet(_, [ [ Some "10"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected b to see a's new value (10), matching MySQL's left-to-right evaluation, got %A" other

                testCase "DELETE removes only matching rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice'), ('bob')" |> ignore

                    match runDefault store "DELETE FROM users WHERE name = 'bob'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT name FROM users" with
                    | ResultSet(_, [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice left, got %A" other

                testCase "DELETE ... LIMIT n caps how many matching rows are removed"
                <| fun _ ->
                    // `DELETE FROM t WHERE ... LIMIT n` is a batch-cleanup-job
                    // staple, capping how many stale rows one run removes.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "DELETE FROM t WHERE n >= 2 LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected exactly 2 rows deleted, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected 2 rows left (4 - 2 deleted), got %A" other

                testCase "UPDATE ... ORDER BY ... LIMIT mutates only the first n rows in that order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (4), (2)" |> ignore

                    match runDefault store "UPDATE t SET n = n + 100 ORDER BY n LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) ->
                        // 1 and 2 were the two lowest, so they're the ones
                        // that got +100'd; 3 and 4 are untouched.
                        Expect.equal rows [ [ Some "3" ]; [ Some "4" ]; [ Some "101" ]; [ Some "102" ] ] "only the two smallest rows changed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "DELETE ... ORDER BY ... LIMIT removes the first n rows in that order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (4), (2)" |> ignore

                    match runDefault store "DELETE FROM t ORDER BY n DESC LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "the two largest were removed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE t1 JOIN t2 ON ... SET updates matched rows in both tables"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE accounts (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE balances (account_id INT, cents INT)" |> ignore
                    runDefault store "INSERT INTO accounts VALUES (1, 'old'), (2, 'other')" |> ignore
                    runDefault store "INSERT INTO balances VALUES (1, 100)" |> ignore

                    match
                        runDefault
                            store
                            "UPDATE accounts a JOIN balances b ON a.id = b.account_id SET a.name = 'renamed', b.cents = b.cents + 1 WHERE a.id = 1"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected (one per table), got %A" other

                    match runDefault store "SELECT name FROM accounts WHERE id = 1" with
                    | ResultSet(_, [ [ Some "renamed" ] ]) -> ()
                    | other -> failtestf "expected the account renamed, got %A" other

                    match runDefault store "SELECT cents FROM balances WHERE account_id = 1" with
                    | ResultSet(_, [ [ Some "101" ] ]) -> ()
                    | other -> failtestf "expected the balance incremented, got %A" other

                    match runDefault store "SELECT name FROM accounts WHERE id = 2" with
                    | ResultSet(_, [ [ Some "other" ] ]) -> ()
                    | other -> failtestf "expected the unmatched account untouched, got %A" other

                testCase "UPDATE JOIN with multiple SET assignments on the same target table applies all of them"
                <| fun _ ->
                    // `claims` must be checked once per (source, physical
                    // row), not per assignment, or only the first SET lands.
                    let store = newStore ()
                    runDefault store "CREATE TABLE mt1 (id INT, a INT, b INT)" |> ignore
                    runDefault store "CREATE TABLE mt2 (id INT, x INT)" |> ignore
                    runDefault store "INSERT INTO mt1 VALUES (1, 0, 0)" |> ignore
                    runDefault store "INSERT INTO mt2 VALUES (1, 5)" |> ignore

                    match runDefault store "UPDATE mt1 JOIN mt2 ON mt1.id = mt2.id SET mt1.a = 10, mt1.b = 20" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT a, b FROM mt1" with
                    | ResultSet(_, [ [ Some "10"; Some "20" ] ]) -> ()
                    | other -> failtestf "expected both a and b written, got %A" other

                testCase "UPDATE JOIN updates a physical row at most once even when the join matches it multiple times"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, hits INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1, 0)" |> ignore
                    // Two t2 rows both join to the same t1 row.
                    runDefault store "INSERT INTO t2 VALUES (1), (1)" |> ignore

                    match runDefault store "UPDATE t1 JOIN t2 ON t1.id = t2.t1_id SET t1.hits = t1.hits + 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected exactly 1 row affected despite two join matches, got %A" other

                    match runDefault store "SELECT hits FROM t1" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected hits incremented exactly once, got %A" other

                testCase "UPDATE self-join through two aliases writes both sides of every matched row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sj2 (id INT, v INT, w INT, nxt INT)" |> ignore
                    runDefault store "INSERT INTO sj2 VALUES (1, 0, 0, 2), (2, 0, 0, 3), (3, 0, 0, NULL)" |> ignore

                    match runDefault store "UPDATE sj2 a JOIN sj2 b ON a.nxt = b.id SET a.v = 111, b.w = 222" with
                    | Affected _ -> ()
                    | other -> failtestf "expected the statement to succeed, got %A" other

                    match runDefault store "SELECT id, v, w FROM sj2 ORDER BY id" with
                    | ResultSet(_, rows) ->
                        // Two join matches: (a=1,b=2) and (a=2,b=3). Row 2
                        // is reached both as a 'b' (match 1, w=222) and as
                        // an 'a' (match 2, v=111) — both must land, since
                        // `a` and `b` are independent roles even though
                        // they're the same table.
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "111"; Some "0" ]; [ Some "2"; Some "111"; Some "222" ]; [ Some "3"; Some "0"; Some "222" ] ]
                            "row 1: a's v=111 only. row 2: both a's v=111 and b's w=222. row 3: b's w=222 only"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE JOIN across tables is statement-atomic: a later table's constraint violation leaves the earlier table untouched"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE p (id INT PRIMARY KEY, x INT)" |> ignore
                    runDefault store "CREATE TABLE q (id INT PRIMARY KEY, pid INT, u INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO p VALUES (1, 10), (2, 20)" |> ignore
                    runDefault store "INSERT INTO q VALUES (100, 1, 1), (101, 2, 2)" |> ignore

                    match runDefault store "UPDATE p JOIN q ON p.id = q.pid SET p.x = 999, q.u = 7" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected a 1062 duplicate-key error (q.u = 7 collides for both matched rows), got %A" other

                    match runDefault store "SELECT x FROM p ORDER BY id" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "10" ]; [ Some "20" ] ] "p's rows must be untouched — the whole statement rolled back"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE JOIN SET assignments evaluate left-to-right across tables too"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u1 (id INT, a INT)" |> ignore
                    runDefault store "CREATE TABLE u2 (id INT, b INT)" |> ignore
                    runDefault store "INSERT INTO u1 VALUES (1, 1)" |> ignore
                    runDefault store "INSERT INTO u2 VALUES (1, 0)" |> ignore

                    match runDefault store "UPDATE u1 JOIN u2 ON u1.id = u2.id SET u1.a = 42, u2.b = u1.a" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT b FROM u2" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected u2.b to see u1.a's new value (42), got %A" other

                testCase "DELETE t1 FROM t1 JOIN t2 ON ... removes only t1's matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT, flag INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 1), (2, 0)" |> ignore

                    match runDefault store "DELETE t1 FROM t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t2" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected t2 untouched (not a delete target), got %A" other

                testCase "DELETE FROM t1 USING t1 JOIN t2 ON ... deletes the same way as the named-target form"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT, flag INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 1), (2, 0)" |> ignore

                    match runDefault store "DELETE FROM t1 USING t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                testCase "DELETE t1, t2 FROM t1 JOIN t2 ON ... deletes matched rows from both target tables"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1)" |> ignore

                    match runDefault store "DELETE t1, t2 FROM t1 JOIN t2 ON t1.id = t2.t1_id" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows deleted total (one per table), got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected t1's matched row gone, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t2" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected t2 emptied, got %A" other ]

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
                    // Proves the extensibility API end to end: a function
                    // registered exactly the way user code would, resolving
                    // through the same registry the built-ins use.
                    let store = newStore ()

                    let registry =
                        builtins
                        |> registerScalar "SHOUT" (function
                            | [ VString s ] -> VString(s.ToUpperInvariant() + "!")
                            | _ -> VNull)

                    match run store registry "SELECT SHOUT('hello')" with
                    | ResultSet(_, [ [ Some "HELLO!" ] ]) -> ()
                    | other -> failtestf "expected HELLO!, got %A" other

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

                testCase "omitting a NOT NULL column with no default is a 1048 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL)" |> ignore

                    match runDefault store "INSERT INTO t (id) VALUES (1)" with
                    | Err(1048, _) -> ()
                    | other -> failtestf "expected a 1048 error, got %A" other

                testCase "an uncoercible value for a column's type is a 1366 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('not a number')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected a 1366 error, got %A" other ]

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

                testCase "DROP COLUMN then SELECT * doesn't see it"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, junk INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 2)" |> ignore
                    runDefault store "ALTER TABLE t DROP COLUMN junk" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected only id left, got %A" other

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
                    | other -> failtestf "expected both actions applied, got %A" other ]

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

                testCase "INSERT IGNORE ... SELECT skips rows that violate a unique constraint instead of failing"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (id INT)" |> ignore
                    runDefault store "CREATE TABLE dst (id INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1)" |> ignore

                    match runDefault store "INSERT IGNORE INTO dst (id) SELECT id FROM src ORDER BY id" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected only id=2 inserted (id=1 collides), got %A" other ]

          testList
              "CAST and new column types"
              [ testCase "CAST(x AS UNSIGNED)/CAST(x AS SIGNED) coerce to an integer"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST('42' AS UNSIGNED), CAST(3.9 AS SIGNED)" with
                    | ResultSet(_, [ [ Some "42"; Some "3" ] ]) -> ()
                    | other -> failtestf "expected 42/3, got %A" other

                testCase "CAST(x AS CHAR) stringifies"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST(42 AS CHAR)" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected '42', got %A" other

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
                    | other -> failtestf "expected 1146 for DROP INDEX IF EXISTS on a missing table, got %A" other ]

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

          testList
              "JOIN"
              [ testCase "INNER JOIN matches rows across two aliased instances of the same table"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE crud (id INT, name VARCHAR(10), qty INT)" |> ignore
                    runDefault store "INSERT INTO crud VALUES (1, 'widget', 5), (2, 'gadget', 9)" |> ignore

                    match runDefault store "SELECT c.name, d.qty FROM crud AS c INNER JOIN crud AS d ON c.id = d.id ORDER BY c.id" with
                    | ResultSet([ "name"; "qty" ], rows) ->
                        Expect.equal rows [ [ Some "widget"; Some "5" ]; [ Some "gadget"; Some "9" ] ] "each row joined to itself"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "INNER JOIN between two different tables, and it drops unmatched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    // bob (user_id 2) has no post; alice has two.
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first'), (2, 1, 'second')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users JOIN posts ON users.id = posts.user_id ORDER BY posts.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ Some "alice"; Some "second" ] ] "only alice's matching posts"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "LEFT JOIN keeps an unmatched left row, padding the right side with NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users LEFT JOIN posts ON users.id = posts.user_id ORDER BY users.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ Some "bob"; None ] ] "bob survives with a NULL title"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "integer equality hash join preserves duplicate matches and never matches NULL keys"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE lhs (id INT NULL)" |> ignore
                    runDefault store "CREATE TABLE rhs (owner_id INT NULL, label VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO lhs VALUES (1), (NULL), (2)" |> ignore
                    runDefault store "INSERT INTO rhs VALUES (1, 'a'), (1, 'b'), (NULL, 'null-key')" |> ignore

                    match runDefault store "SELECT lhs.id, rhs.label FROM lhs LEFT JOIN rhs ON lhs.id = rhs.owner_id ORDER BY lhs.id, rhs.label" with
                    | ResultSet([ "id"; "label" ], rows) ->
                        Expect.equal
                            rows
                            [ [ None; None ]; [ Some "1"; Some "a" ]; [ Some "1"; Some "b" ]; [ Some "2"; None ] ]
                            "NULL does not equal NULL, duplicate right keys preserve both matches, and unmatched left rows are padded"
                    | other -> failtestf "expected a duplicate-preserving left join resultset, got %A" other

                testCase "RIGHT JOIN keeps an unmatched right row, padding the left side with NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice')" |> ignore
                    // post 2's user_id (99) matches no user.
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first'), (2, 99, 'orphan')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users RIGHT JOIN posts ON users.id = posts.user_id ORDER BY posts.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ None; Some "orphan" ] ] "the orphaned post survives with a NULL name"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "CROSS JOIN produces the full Cartesian product"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (x INT)" |> ignore
                    runDefault store "CREATE TABLE b (y INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT x, y FROM a CROSS JOIN b ORDER BY x, y" with
                    | ResultSet([ "x"; "y" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10" ]; [ Some "1"; Some "20" ]; [ Some "2"; Some "10" ]; [ Some "2"; Some "20" ] ]
                            "every combination"
                    | other -> failtestf "expected a 2x2 Cartesian product, got %A" other

                testCase "FROM t1, t2 (comma/implicit join) works the same as an explicit CROSS JOIN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (x INT)" |> ignore
                    runDefault store "CREATE TABLE b (y INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT x, y FROM a, b ORDER BY x, y" with
                    | ResultSet([ "x"; "y" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10" ]; [ Some "1"; Some "20" ]; [ Some "2"; Some "10" ]; [ Some "2"; Some "20" ] ]
                            "every combination, same as CROSS JOIN"
                    | other -> failtestf "expected a 2x2 Cartesian product, got %A" other

                testCase "UPDATE t1, t2 SET ... WHERE ... (comma join) updates matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, x INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1id INT, y INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1, 0)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 5)" |> ignore

                    match runDefault store "UPDATE t1, t2 SET t1.x = t2.y WHERE t1.id = t2.t1id" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT x FROM t1" with
                    | ResultSet(_, [ [ Some "5" ] ]) -> ()
                    | other -> failtestf "expected t1.x set from t2.y, got %A" other

                testCase "DELETE t1 FROM t1, t2 WHERE ... (comma join) deletes matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1id INT, y INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 7)" |> ignore

                    match runDefault store "DELETE t1 FROM t1, t2 WHERE t1.id = t2.t1id AND t2.y > 6" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                testCase "qualified t.* in a JOIN expands only that table's own columns, not every joined column"
                <| fun _ ->
                    // This is the shape of a Laravel `belongsToMany` pivot
                    // query: `SELECT teams.*, team_user.role AS pivot_role
                    // FROM teams JOIN team_user ...`.
                    let store = newStore ()
                    runDefault store "CREATE TABLE teams (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE team_user (id INT, team_id INT, role VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO teams VALUES (1, 'acme')" |> ignore
                    // team_user.id (99) differs from teams.id (1), so a
                    // qualifier-dropping expansion is visible in the result.
                    runDefault store "INSERT INTO team_user VALUES (99, 1, 'admin')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT teams.*, team_user.role AS pivot_role FROM teams JOIN team_user ON teams.id = team_user.team_id"
                    with
                    | ResultSet([ "id"; "name"; "pivot_role" ], [ [ Some "1"; Some "acme"; Some "admin" ] ]) -> ()
                    | other -> failtestf "expected teams' own id (1), not team_user's (99), got %A" other

                // Every expectation below was verified against a real MySQL
                // 8.4 with the identical schema/data (torture-style
                // differential probe) — the exact rows, not just shapes.
                testCase "self-join through two aliases pairs rows with a shared key"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

                    match
                        runDefault store "SELECT x.id, y.id, y.tag FROM b x INNER JOIN b y ON y.aid = x.aid AND y.id <> x.id ORDER BY x.id, y.id"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "10"; Some "11"; Some "y" ]; [ Some "11"; Some "10"; Some "x" ] ] "aid=1 pairs, each excluding itself"
                    | other -> failtestf "expected the self-join pairs, got %A" other

                testCase "a three-table INNER chain keeps only rows matching through every link"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE c (id INT, bid INT, note VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO a VALUES (1,'alice'),(2,'bob'),(3,'carol')" |> ignore
                    runDefault store "INSERT INTO b VALUES (10,1,'x'),(11,1,'y'),(12,2,'z'),(13,4,'w')" |> ignore
                    runDefault store "INSERT INTO c VALUES (100,10,'n1'),(101,12,'n2'),(102,99,'n3')" |> ignore

                    match
                        runDefault store "SELECT a.id, b.id, c.id, c.note FROM a INNER JOIN b ON b.aid = a.id INNER JOIN c ON c.bid = b.id ORDER BY a.id, b.id, c.id"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "10"; Some "100"; Some "n1" ]; [ Some "2"; Some "12"; Some "101"; Some "n2" ] ] "rows surviving both links"
                    | other -> failtestf "expected the chained matches, got %A" other

                testCase "a LEFT chain pads NULLs at whichever link first fails"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
                    runDefault store "CREATE TABLE c (id INT, bid INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore
                    runDefault store "INSERT INTO c VALUES (100, 10), (101, 12), (102, 99)" |> ignore

                    match runDefault store "SELECT a.id, b.id, c.id FROM a LEFT JOIN b ON b.aid = a.id LEFT JOIN c ON c.bid = b.id ORDER BY a.id, b.id, c.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10"; Some "100" ]
                              [ Some "1"; Some "11"; None ]
                              [ Some "2"; Some "12"; Some "101" ]
                              [ Some "3"; None; None ] ]
                            "each link pads only the side past its failure point"
                    | other -> failtestf "expected the chained LEFT matches, got %A" other

                testCase "WHERE on the joined table filters LEFT JOIN rows exactly like MySQL (matched vs padded)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

                    match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id WHERE b.tag IS NOT NULL ORDER BY a.id, b.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "1"; Some "11"; Some "y" ]; [ Some "2"; Some "12"; Some "z" ] ] "WHERE on the right side drops padded rows"
                    | other -> failtestf "expected only matched rows, got %A" other

                    match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id WHERE b.tag IS NULL ORDER BY a.id" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "3"; None; None ] ] "only the unmatched left row survives"
                    | other -> failtestf "expected only the padded row, got %A" other

                testCase "aggregates over a LEFT JOIN count/SUM per group, NULLs included"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT a.id, COUNT(b.id), COALESCE(SUM(b.id), 0) FROM a LEFT JOIN b ON b.aid = a.id GROUP BY a.id ORDER BY a.id"
                    with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "2"; Some "21" ]; [ Some "2"; Some "1"; Some "12" ]; [ Some "3"; Some "0"; Some "0" ] ] "per-group counts and sums"
                    | other -> failtestf "expected grouped aggregates, got %A" other

                testCase "a multi-key ON keeps equi-key matches and filters by the residual conjunct"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

                    match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id AND b.tag = 'x' ORDER BY a.id, b.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "2"; None; None ]; [ Some "3"; None; None ] ] "only the x-tagged b row matches a=1"
                    | other -> failtestf "expected the filtered multi-key matches, got %A" other

                testCase "a non-equi ON falls back to nested loop, same matches as MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore

                    match runDefault store "SELECT a.id, b.id FROM a INNER JOIN b ON a.id < b.aid ORDER BY a.id, b.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "12" ]
                              [ Some "1"; Some "13" ]
                              [ Some "2"; Some "13" ]
                              [ Some "3"; Some "13" ] ]
                            "every a.id < b.aid pair"
                    | other -> failtestf "expected the range-join pairs, got %A" other

                testCase "an equi-key on a string-cast column still hash-matches (key class coercion)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO a VALUES (1,'alice'),(2,'bob'),(3,'carol')" |> ignore
                    runDefault store "INSERT INTO b VALUES (10,1,'x'),(11,1,'y'),(12,2,'z'),(13,4,'w')" |> ignore

                    match runDefault store "SELECT a.id, b.id, b.tag FROM a INNER JOIN b ON b.aid = CAST(a.id AS CHAR) ORDER BY a.id, b.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "1"; Some "11"; Some "y" ]; [ Some "2"; Some "12"; Some "z" ] ] "same matches as the uncast equi-join"
                    | other -> failtestf "expected the coerced-key matches, got %A" other

                testCase "chaining RIGHT JOIN then LEFT JOIN pads each outer side independently"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
                    runDefault store "CREATE TABLE c (id INT, bid INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore
                    runDefault store "INSERT INTO c VALUES (100, 10), (101, 12), (102, 99)" |> ignore

                    match runDefault store "SELECT a.id, b.id, c.id FROM a RIGHT JOIN b ON b.aid = a.id LEFT JOIN c ON c.bid = b.id ORDER BY b.id, a.id, c.id" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10"; Some "100" ]
                              [ Some "1"; Some "11"; None ]
                              [ Some "2"; Some "12"; Some "101" ]
                              [ None; Some "13"; None ] ]
                            "b=13 is RIGHT-padded on a, then LEFT-padded on c"
                    | other -> failtestf "expected the mixed RIGHT+LEFT chain, got %A" other

                testCase "joining against an aggregate derived table matches MySQL's grouped results"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore
                    runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT a.id, q2.tag FROM a INNER JOIN (SELECT aid, MAX(tag) tag FROM b GROUP BY aid) q2 ON q2.aid = a.id ORDER BY a.id"
                    with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1"; Some "y" ]; [ Some "2"; Some "z" ] ] "max tag per joined group"
                    | other -> failtestf "expected the derived-table join, got %A" other

                // NATURAL/USING expectations below were verified against a
                // real MySQL 8.4 (same schema/data, differential probe) —
                // column order, coalesced values, and padding all match.
                testCase "NATURAL JOIN matches on every common column and coalesces SELECT *"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

                    // only the row where BOTH j and m match survives; SELECT
                    // * order is the coalesced commons, then left-rest, then
                    // right-rest.
                    match runDefault store "SELECT * FROM t1 NATURAL JOIN t2" with
                    | ResultSet([ "j"; "m"; "i"; "k" ], [ [ Some "10"; Some "100"; Some "1"; Some "11" ] ]) -> ()
                    | other -> failtestf "expected the coalesced commons-first row, got %A" other

                testCase "NATURAL LEFT/RIGHT JOIN pad with the preserved side's coalesced values"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

                    match runDefault store "SELECT j, m, i, k FROM t1 NATURAL LEFT JOIN t2 ORDER BY i" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "10"; Some "100"; Some "1"; Some "11" ]
                              [ Some "20"; Some "200"; Some "2"; None ]
                              [ Some "30"; Some "300"; Some "3"; None ] ]
                            "left rows keep their own j/m when unmatched"
                    | other -> failtestf "expected LEFT-padded coalesced rows, got %A" other

                    // RIGHT puts the right table's remaining columns before
                    // the left's, and unmatched right rows keep their values.
                    match runDefault store "SELECT j, m, k, i FROM t1 NATURAL RIGHT JOIN t2 ORDER BY k" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "10"; Some "100"; Some "11"; Some "1" ]
                              [ Some "20"; Some "999"; Some "22"; None ]
                              [ Some "99"; Some "333"; Some "33"; None ] ]
                            "right rows keep their own j/m when unmatched"
                    | other -> failtestf "expected RIGHT-padded coalesced rows, got %A" other

                testCase "JOIN ... USING coalesces the listed column and keeps both sides' other columns"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

                    // j coalesced once, both m's survive (t1.m then t2.m).
                    match runDefault store "SELECT j, i, t1.m, k, t2.m FROM t1 JOIN t2 USING (j) ORDER BY j" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "10"; Some "1"; Some "100"; Some "11"; Some "100" ]
                              [ Some "20"; Some "2"; Some "200"; Some "22"; Some "999" ] ]
                            "coalesced j, both m's in place"
                    | other -> failtestf "expected the USING-joined rows, got %A" other

                testCase "a USING column missing from either side is MySQL's 1054 from-clause error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT)" |> ignore

                    match runDefault store "SELECT * FROM t1 JOIN t2 USING (nope)" with
                    | Err(1054, msg) -> Expect.stringContains msg "from clause" "message names the from clause"
                    | other -> failtestf "expected 1054 for a missing USING column, got %A" other

                testCase "NATURAL JOIN with no common columns is the Cartesian product"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE n1 (a INT, b INT)" |> ignore
                    runDefault store "CREATE TABLE n2 (c INT, d INT)" |> ignore
                    runDefault store "INSERT INTO n1 VALUES (1,2)" |> ignore
                    runDefault store "INSERT INTO n2 VALUES (3,4)" |> ignore

                    match runDefault store "SELECT a, b, c, d FROM n1 NATURAL JOIN n2" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1"; Some "2"; Some "3"; Some "4" ] ] "every pair, no coalescing"
                    | other -> failtestf "expected the Cartesian product, got %A" other

                testCase "chained NATURAL/USING joins fold left to right, commons first each step"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t3 (j INT, n INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore
                    runDefault store "INSERT INTO t3 VALUES (10, 7), (20, 8)" |> ignore

                    // t1 NATURAL t2 coalesces (j, m); then NATURAL t3 moves
                    // j to the front again: j, m, i, k, n.
                    match runDefault store "SELECT j, m, i, k, n FROM t1 NATURAL JOIN t2 NATURAL JOIN t3 ORDER BY j" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "10"; Some "100"; Some "1"; Some "11"; Some "7" ] ] "second join's common j first, rest preserved"
                    | other -> failtestf "expected the chained natural join, got %A" other

                    // t1 JOIN t2 USING (j) coalesces j; NATURAL t3 finds only
                    // j again: j, i, t1.m, k, t2.m, n.
                    match runDefault store "SELECT j, i, t1.m, k, t2.m, n FROM t1 JOIN t2 USING (j) NATURAL JOIN t3 ORDER BY j" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "10"; Some "1"; Some "100"; Some "11"; Some "100"; Some "7" ]
                              [ Some "20"; Some "2"; Some "200"; Some "22"; Some "999"; Some "8" ] ]
                            "mixed USING-then-NATURAL chain"
                    | other -> failtestf "expected the mixed chain, got %A" other

                testCase "unqualified references to a coalesced column see the COALESCE, and qualified stars stay untouched"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

                    // unqualified j in WHERE resolves to the coalesced column
                    match runDefault store "SELECT i FROM t1 NATURAL LEFT JOIN t2 WHERE j IS NOT NULL ORDER BY i" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "every left row has a coalesced j"
                    | other -> failtestf "expected unqualified j to resolve, got %A" other

                    // t1.* keeps t1's own columns, common ones included
                    match runDefault store "SELECT t1.* FROM t1 NATURAL JOIN t2" with
                    | ResultSet([ "i"; "j"; "m" ], [ [ Some "1"; Some "10"; Some "100" ] ]) -> ()
                    | other -> failtestf "expected t1's own full column list, got %A" other

                testCase "an unqualified column present in two joined tables is error 1052, not a silent pick"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE p (id INT, uid INT, title VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES (1, 'alice')" |> ignore
                    runDefault store "INSERT INTO p VALUES (10, 1, 'first')" |> ignore

                    match runDefault store "SELECT id FROM u JOIN p ON p.uid = u.id" with
                    | Err(1052, _) -> ()
                    | other -> failtestf "expected error 1052 (ambiguous column), got %A" other

                testCase "ORDER BY resolves a bare name against SELECT's output columns before FROM-tables (Laravel's belongsToMany-through shape)"
                <| fun _ ->
                    // Both `chat_sessions` and `chatbots` have `created_at`,
                    // but the projection only outputs one of them (via
                    // `chat_sessions.*`) — real MySQL resolves the bare
                    // `ORDER BY created_at` against that single output
                    // column, not against the two ambiguous FROM-table ones
                    // (verified against a live MySQL 8 instance).
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (2, 1, '2019-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "select chat_sessions.*, chatbots.team_id as laravel_through_key from chat_sessions inner join chatbots on chatbots.id = chat_sessions.chatbot_id where chatbots.team_id = 1 order by created_at desc limit 5"
                    with
                    | ResultSet([ "id"; "chatbot_id"; "created_at"; "laravel_through_key" ], rows) ->
                        Expect.equal
                            (rows |> List.map (fun r -> r.[0]))
                            [ Some "1"; Some "2" ]
                            "expected chat_sessions sorted by its own created_at, descending"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * FROM two joined tables, ORDER BY a column both have, is error 1052 in the order clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT * FROM chat_sessions INNER JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY created_at"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous ORDER BY), got %A" other

                testCase "ORDER BY a name that's a duplicate SELECT alias is error 1052 in the order clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.created_at AS x, chatbots.created_at AS x FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY x"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous duplicate alias), got %A" other

                testCase "WHERE a bare ambiguous column is error 1052 in the where clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.* FROM chat_sessions INNER JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id WHERE created_at > '2020-01-01'"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "where clause" "expected the where-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous WHERE), got %A" other

                testCase "ORDER BY a name absent from the output but unique across FROM-tables still resolves"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT)" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1)" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (2, 1, 'b')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, 'a')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.id FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY chatbot_id"
                    with
                    | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "1" ] ] "expected both rows, chatbot_id ties broken by (stable) scan/insertion order"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "ORDER BY a name absent from the output and ambiguous across FROM-tables is error 1052"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.id FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY created_at"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous ORDER BY fallback), got %A" other

                testCase "hash join set-equals a reference nested-loop join, over randomized tables (pure equi, equi+residual, and a non-extractable arithmetic ON that must fall back)"
                <| fun _ ->
                    let rnd = System.Random(20260817)

                    let readRows (store: Store) (sql: string) : (int64 * int64 * int64 * int64) list =
                        match runDefault store sql with
                        | ResultSet(_, rows) ->
                            rows
                            |> List.map (function
                                | [ Some a; Some b; Some c; Some d ] -> int64 a, int64 b, int64 c, int64 d
                                | other -> failtestf "expected 4 non-null columns, got %A" other)
                        | other -> failtestf "expected a resultset for %s, got %A" sql other

                    for _ in 1 .. 30 do
                        let store = newStore ()
                        runDefault store "CREATE TABLE l (k INT, x INT)" |> ignore
                        runDefault store "CREATE TABLE r (k INT, y INT)" |> ignore

                        // Small key range (0..3) on purpose, so most runs
                        // produce duplicate keys on both sides — exercising
                        // the hash join's one-key-to-many-rows bucket, not
                        // just a 1:1 lookup.
                        let leftRows = [ for _ in 1 .. rnd.Next(1, 8) -> int64 (rnd.Next(0, 4)), int64 (rnd.Next(0, 10)) ]
                        let rightRows = [ for _ in 1 .. rnd.Next(1, 8) -> int64 (rnd.Next(0, 4)), int64 (rnd.Next(0, 10)) ]
                        let valuesOf (rows: (int64 * int64) list) = rows |> List.map (fun (a, b) -> sprintf "(%d, %d)" a b) |> String.concat ", "

                        runDefault store (sprintf "INSERT INTO l VALUES %s" (valuesOf leftRows)) |> ignore
                        runDefault store (sprintf "INSERT INTO r VALUES %s" (valuesOf rightRows)) |> ignore

                        // Pure equi ON — extractEquiKeys finds one key pair
                        // and no residual, so this is the hash path end to
                        // end.
                        let referenceEqui = [ for lk, lx in leftRows do for rk, ry in rightRows do if lk = rk then yield lk, lx, rk, ry ]

                        Expect.equal
                            (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k = r.k" |> List.sort)
                            (referenceEqui |> List.sort)
                            "pure equi-join hash path"

                        // Equi + residual ON — the equi key still drives the
                        // hash probe; `l.x > 1` is the leftover conjunct
                        // applied per matched candidate.
                        let referenceResidual = referenceEqui |> List.filter (fun (_, lx, _, _) -> lx > 1L)

                        Expect.equal
                            (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k = r.k AND l.x > 1" |> List.sort)
                            (referenceResidual |> List.sort)
                            "equi-plus-residual ON"

                        // `l.k + 1 = r.k` has no `QualifiedCol = QualifiedCol`
                        // conjunct at all, so `extractEquiKeys` reports no
                        // keys and this falls back to the lazy nested loop
                        // entirely — still has to come out correct.
                        let referenceArithmetic = [ for lk, lx in leftRows do for rk, ry in rightRows do if lk + 1L = rk then yield lk, lx, rk, ry ]

                        Expect.equal
                            (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k + 1 = r.k" |> List.sort)
                            (referenceArithmetic |> List.sort)
                            "non-extractable arithmetic ON falls back to the nested loop and stays correct"

                testCase "the non-equi JOIN fallback's accumulator holds only matched rows, not one entry per candidate pair"
                <| fun _ ->
                    // A low-selectivity non-equi ON must stream matches, not
                    // hold a `(left, right, combined, matched: bool)` tuple
                    // per *candidate pair* — that keeps the full cross
                    // product in memory at once, not just the eventual
                    // result. Per-pair `evalExpr` cost dominates wall time
                    // either way (much larger than the accumulator-shape
                    // difference at unit-test scale), so this is a
                    // correctness/doesn't-hang check at a meaningfully
                    // low-selectivity size (n-1 matches out of n^2 candidate
                    // pairs), not a tight perf assertion — see the
                    // perf-canary tests elsewhere in this suite for that
                    // style of check where the gap is wide enough to assert
                    // on reliably.
                    let store = newStore ()
                    runDefault store "CREATE TABLE l (n INT)" |> ignore
                    runDefault store "CREATE TABLE r (n INT)" |> ignore

                    let n = 800
                    let values = [ for i in 1 .. n -> sprintf "(%d)" i ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO l VALUES %s" values) |> ignore
                    runDefault store (sprintf "INSERT INTO r VALUES %s" values) |> ignore

                    // `l.n + 1 = r.n` has no extractable equi key, so this
                    // is the nested-loop fallback over the full n * n cross
                    // product (640,000 candidate pairs here), even though at
                    // most n - 1 of them ever match.
                    let sql = "SELECT l.n, r.n FROM l JOIN r ON l.n + 1 = r.n"

                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let result = runDefault store sql
                    sw.Stop()

                    match result with
                    | ResultSet(_, rows) -> Expect.equal (List.length rows) (n - 1) "one match per row except the last"
                    | other -> failtestf "expected a resultset, got %A" other

                    Expect.isLessThan
                        sw.Elapsed.TotalSeconds
                        10.0
                        (sprintf "a %d x %d non-equi join with only %d matches took %A — looks hung, not just slow" n n (n - 1) sw.Elapsed)

                testCase "hash join on string keys matches case-insensitively, and trailing spaces stay distinct (NO PAD)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE l (name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE r (name VARCHAR(20), tag VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO l VALUES ('Alice'), ('bob  '), ('Carol')" |> ignore
                    runDefault store "INSERT INTO r VALUES ('alice', 'x'), ('BOB', 'y'), ('dave', 'z')" |> ignore

                    match runDefault store "SELECT l.name, r.tag FROM l JOIN r ON l.name = r.name ORDER BY l.name" with
                    | ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "Alice"; Some "x" ] ]
                            "utf8mb4_0900_ai_ci folds case but not trailing spaces: 'Alice'/'alice' join, 'bob  '/'BOB' don't"
                    | other -> failtestf "expected case-insensitive-only matches, got %A" other

                testCase "UPDATE ... JOIN hash-matches string keys case-insensitively, trailing spaces distinct"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE accounts2 (name VARCHAR(20), balance INT)" |> ignore
                    runDefault store "CREATE TABLE rates (name VARCHAR(20), bonus INT)" |> ignore
                    runDefault store "INSERT INTO accounts2 VALUES ('Alice', 0), ('bob  ', 0)" |> ignore
                    runDefault store "INSERT INTO rates VALUES ('alice', 10), ('BOB', 20)" |> ignore

                    match runDefault store "UPDATE accounts2 a JOIN rates r ON a.name = r.name SET a.balance = r.bonus" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected only 'Alice'/'alice' to match, got %A" other

                    match runDefault store "SELECT name, balance FROM accounts2 ORDER BY name" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "Alice"; Some "10" ]; [ Some "bob  "; Some "0" ] ] "'bob  ' vs 'BOB' doesn't match under NO PAD"
                    | other -> failtestf "expected a resultset, got %A" other ]

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

                testCase "a bare non-aggregated column picks the first row of its group (ANY_VALUE-style, ONLY_FULL_GROUP_BY off)"
                <| fun _ ->
                    let store = newStore ()
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

          testList
              "correlated subqueries"
              [ testCase "correlated EXISTS: WHERE EXISTS (... referencing the outer row) — the Eloquent whereHas() shape"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    // only alice has a post.
                    runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

                    let sql = "SELECT name FROM users WHERE EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

                    match runDefault store sql with
                    | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice (who has a post), got %A" other

                testCase "correlated NOT EXISTS"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

                    let sql = "SELECT name FROM users WHERE NOT EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

                    match runDefault store sql with
                    | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
                    | other -> failtestf "expected only bob (who has no post), got %A" other

                testCase "IN (SELECT ...): a non-correlated subquery's first column is the candidate set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1)" |> ignore

                    match runDefault store "SELECT name FROM users WHERE id IN (SELECT user_id FROM posts)" with
                    | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice, got %A" other

                testCase "NOT IN (SELECT ...)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1)" |> ignore

                    match runDefault store "SELECT name FROM users WHERE id NOT IN (SELECT user_id FROM posts)" with
                    | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
                    | other -> failtestf "expected only bob, got %A" other

                testCase "NULL NOT IN (SELECT ...) survives when the subquery has no rows"
                <| fun _ ->
                    // `NULL IN (<empty set>)` is FALSE, not UNKNOWN, so
                    // `NOT IN` against an empty subquery must be TRUE
                    // regardless of `v`'s own NULL-ness — the subquery has
                    // to run before any NULL short-circuit.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
                    runDefault store "CREATE TABLE empty_t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, NULL), (2, 5)" |> ignore

                    match runDefault store "SELECT id FROM t WHERE v NOT IN (SELECT id FROM empty_t) ORDER BY id" with
                    | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "both rows survive against an empty candidate set"
                    | other -> failtestf "expected both rows to survive NOT IN against an empty subquery, got %A" other

                testCase "scalar subquery: (SELECT ...) used as a value, zero rows is NULL, one row is that value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1), (2, 1)" |> ignore

                    let sql = "SELECT name, (SELECT COUNT(*) FROM posts WHERE posts.user_id = users.id) AS n FROM users ORDER BY name"

                    match runDefault store sql with
                    | ResultSet([ "name"; "n" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "2" ]; [ Some "bob"; Some "0" ] ] "correlated scalar subquery per row"
                    | other -> failtestf "expected a per-row correlated count, got %A" other

                testCase "scalar subquery returning more than one row is MySQL error 1242"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    match runDefault store "SELECT (SELECT n FROM t) AS x" with
                    | Err(1242, _) -> ()
                    | other -> failtestf "expected error 1242, got %A" other

                testCase "derived table: FROM (SELECT ...) AS t"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT doubled FROM (SELECT n * 2 AS doubled FROM t) AS d WHERE doubled > 2 ORDER BY doubled" with
                    | ResultSet([ "doubled" ], rows) -> Expect.equal rows [ [ Some "4" ]; [ Some "6" ] ] "derived table filtered and projected"
                    | other -> failtestf "expected a derived-table resultset, got %A" other

                testCase "a derived table's columns compare numerically, not as re-wrapped text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT MAX(y.n) AS m FROM (SELECT n FROM nums) y" with
                    | ResultSet([ "m" ], [ [ Some "10" ] ]) -> ()
                    | other -> failtestf "expected MAX to compare numerically (10), got %A" other

                    match runDefault store "SELECT y.n FROM (SELECT n FROM nums) y ORDER BY y.n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "2" ]; [ Some "9" ]; [ Some "10" ] ] "ORDER BY sorts numerically, not lexicographically"
                    | other -> failtestf "expected a numerically-sorted resultset, got %A" other

                testCase "LEFT JOIN (SELECT ...) AS t ON ... — a derived table as a JOIN target, not just the leading FROM"
                <| fun _ ->
                    // Eloquent's leftJoinSub/joinSub send exactly this shape.
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE orders (user_id INT, total INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO orders VALUES (1, 10), (1, 5)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT users.name, o.total_spent FROM users LEFT JOIN (SELECT user_id, SUM(total) AS total_spent FROM orders GROUP BY user_id) AS o ON users.id = o.user_id ORDER BY users.id"
                    with
                    | ResultSet([ "name"; "total_spent" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "15" ]; [ Some "bob"; None ] ] "bob has no orders, padded with NULL"
                    | other -> failtestf "expected a joined-against-subquery resultset, got %A" other

                testCase "UPDATE t1 JOIN (SELECT ...) dt ON ... is a 1064 error, not a crash"
                <| fun _ ->
                    // `Executor.applyMutationJoin`'s documented real-tables-only
                    // simplification — a clean error, not a wrong result.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, n INT)" |> ignore

                    match runDefault store "UPDATE t1 JOIN (SELECT 1 AS id) dt ON t1.id = dt.id SET t1.n = 1" with
                    | Err(1064, _) -> ()
                    | other -> failtestf "expected a 1064 error, got %A" other

                testCase "a scalar subquery's comparison is numeric, not lexicographic text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT (SELECT MAX(n) FROM nums) > (SELECT MIN(n) FROM nums) AS r" with
                    | ResultSet([ "r" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected 10 > 2 to be true, got %A" other ]

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
                    // `regexpOp` must honor the column's collation, not
                    // hardcode `IgnoreCase`.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello'), ('world')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^h'" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected a case-sensitive REGEXP not to match 'Hello' against '^h', got %A" other

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^H'" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected the case-sensitive REGEXP to match 'Hello' against '^H', got %A" other

                testCase "REGEXP on a catastrophically-backtracking pattern errors instead of hanging"
                <| fun _ ->
                    // Every `Regex` in `regexpOp` must carry a `MatchTimeout`
                    // — `(a+)+$` against a long non-matching subject would
                    // otherwise pin a core forever.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(200))" |> ignore
                    let pathological = String.replicate 40 "a" + "!"
                    runDefault store (sprintf "INSERT INTO t VALUES ('%s')" pathological) |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '(a+)+$'" with
                    | Err(_, _) -> ()
                    | other -> failtestf "expected a catastrophic-backtracking REGEXP to error out (timeout), got %A" other

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

                testCase "a generated column recomputes after UPDATE of a column it depends on"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2))" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3)" |> ignore
                    runDefault store "UPDATE t SET n = 10" |> ignore

                    match runDefault store "SELECT doubled FROM t" with
                    | ResultSet([ "doubled" ], [ [ Some "20" ] ]) -> ()
                    | other -> failtestf "expected doubled to recompute to 20, got %A" other ]

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
                    | other -> failtestf "expected offset-2 lag, got %A" other ]

          testList
              "PK/UNIQUE point-lookup fast path (Storage.tryUniqueLookup)"
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

                testCase "FK parent-existence checks (indexed parent PK) match an unindexed parent's full-scan fallback"
                <| fun _ ->
                    // `child`'s FK references `parentIndexed`'s real PRIMARY
                    // KEY, so `checkFkParent` takes `parentUniqueIndex`'s
                    // fast path; `childUnindexed`'s FK references
                    // `parentPlain`'s `id`, which carries no PK/UNIQUE at
                    // all, so the identical check there can only ever fall
                    // back to the full scan.
                    let store = newStore ()
                    runDefault store "CREATE TABLE parentIndexed (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE parentPlain (id INT)" |> ignore
                    runDefault store "CREATE TABLE child (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parentIndexed(id))" |> ignore
                    runDefault store "CREATE TABLE childUnindexed (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parentPlain(id))" |> ignore
                    runDefault store "INSERT INTO parentIndexed VALUES (1), (2), (3)" |> ignore
                    runDefault store "INSERT INTO parentPlain VALUES (1), (2), (3)" |> ignore

                    for parentId in [ 1; 2; 3; 999 ] do
                        let indexedResult = runDefault store (sprintf "INSERT INTO child VALUES (%d, %d)" parentId parentId)
                        let scanResult = runDefault store (sprintf "INSERT INTO childUnindexed VALUES (%d, %d)" parentId parentId)

                        match indexedResult, scanResult with
                        | Affected 1UL, Affected 1UL -> ()
                        | Err(1452, _), Err(1452, _) -> ()
                        | a, b -> failtestf "indexed vs unindexed parent disagreed for parent_id = %d: %A vs %A" parentId a b

                testCase "single-row INSERT into a child of a large PK-indexed parent stays flat, not linear in the parent's size"
                <| fun _ ->
                    // insertCore's own FK-parent lookup (`foreignKeyLookups`,
                    // separate from `checkFkParent`'s fast path exercised
                    // above) must answer via the parent's PK/UNIQUE index in
                    // O(log n), not by scanning the *entire* parent table on
                    // every INSERT statement.
                    let store = newStore ()
                    runDefault store "CREATE TABLE parent (id INT PRIMARY KEY)" |> ignore
                    runDefault store "CREATE TABLE child (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES parent(id))" |> ignore

                    let batch = [ for i in 1 .. 20_000 -> sprintf "(%d)" i ] |> String.concat ", "
                    runDefault store (sprintf "INSERT INTO parent VALUES %s" batch) |> ignore

                    // Warm up (JIT, allocator) before timing.
                    runDefault store "INSERT INTO child VALUES (1, 1)" |> ignore

                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    for i in 2 .. 200 do
                        runDefault store (sprintf "INSERT INTO child VALUES (%d, %d)" i i) |> ignore

                    sw.Stop()

                    let perInsertMs = sw.Elapsed.TotalMilliseconds / 199.0

                    Expect.isLessThan
                        perInsertMs
                        1.0
                        (sprintf
                            "199 single-row INSERTs into a child of a 20,000-row PK-indexed parent averaged %f ms/insert — looks like a full parent scan again"
                            perInsertMs)

                testCase "point SELECT by PRIMARY KEY on a 50,000-row table stays flat, not linear in table size"
                <| fun _ ->
                    // Only catches a regression back to a full table scan —
                    // the 20ms bound is generous, not a tight perf target
                    // (the network round-trip floor is ~250µs).
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
                    | other -> failtestf "expected a resultset, got %A" other ]

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
                        "ORDER BY forcing a filesort must still evaluate every matched row's projection" ] ]
