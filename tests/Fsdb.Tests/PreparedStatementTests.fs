module Fsdb.Tests.PreparedStatementTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let tests =
    testList
        "PreparedStatements"
        [ testCase "placeholderPositions counts only ? outside strings, comments, and backtick identifiers"
          <| fun _ ->
              let sql =
                  "SELECT * FROM t WHERE a = ? AND b = '?' AND c = \"?\" AND d = `?` -- ?\nAND e = ? /* ? */ AND f = ?"

              Expect.equal (placeholderPositions sql |> List.length) 3 "three real placeholders (a, e, f)"

          testCase "placeholderPositions treats a doubled quote as an escaped quote, not the string's end"
          <| fun _ ->
              let sql = "SELECT * FROM t WHERE a = 'it''s a ? mystery' AND b = ?"
              Expect.equal (placeholderPositions sql |> List.length) 1 "one real placeholder"

          testCase "placeholderPositions treats a backslash-escaped quote as not ending the string"
          <| fun _ ->
              let sql = @"SELECT * FROM t WHERE a = 'a \' ? b' AND b = ?"
              Expect.equal (placeholderPositions sql |> List.length) 1 "one real placeholder"

          testCase "substitutePlaceholders replaces placeholders in order and leaves the rest of the SQL untouched"
          <| fun _ ->
              let sql = "INSERT INTO t (a, b) VALUES (?, ?)"
              let result = substitutePlaceholders sql [ "1"; "'x'" ]
              Expect.equal result "INSERT INTO t (a, b) VALUES (1, 'x')" "substitution"

          testCase "valueToSqlLiteral escapes single quotes and backslashes in strings"
          <| fun _ ->
              Expect.equal (valueToSqlLiteral (VString "O'Brien\\")) "'O\\'Brien\\\\'" "escaped literal"

          testCase "valueToSqlLiteral escapes CR/LF so a bound param round-trips through re-parsing"
          <| fun _ ->
              // A raw CR spliced into the SQL text gets silently normalized
              // to LF by FParsec's CharStream on re-parse (it
              // treats bare \r/\r\n as line endings) unless the literal
              // escapes it, corrupting any CRLF value a prepared statement
              // substitutes in — e.g. an HTML textarea's body.
              let original = "a\r\nb\rc"
              let literal = valueToSqlLiteral (VString original)
              Expect.stringContains literal "\\r" "CR is escaped in the literal"

              match Fsdb.Parser.parse (sprintf "SELECT %s AS x" literal) with
              | Result.Ok(Select { Projections = [ Lit(VString roundtripped), _ ] }) ->
                  Expect.equal roundtripped original "CR/LF survive the literal round-trip"
              | other -> failtestf "expected a parsed SELECT literal, got %A" other

          testCase "valueToSqlLiteral renders NULL for VNull and a plain digit string for VInt"
          <| fun _ ->
              Expect.equal (valueToSqlLiteral VNull) "NULL" "null literal"
              Expect.equal (valueToSqlLiteral (VInt 42L)) "42" "int literal"

          testCase "prepareStatement reports the placeholder count for a valid statement"
          <| fun _ ->
              match prepareStatement "INSERT INTO t (a, b) VALUES (?, ?)" with
              | Result.Ok 2 -> ()
              | other -> failtestf "expected Ok 2, got %A" other

          testCase "prepareStatement reports a 1064 syntax error for invalid SQL"
          <| fun _ ->
              match prepareStatement "GARBAGE NOT SQL" with
              | Result.Error(1064, _) -> ()
              | other -> failtestf "expected a 1064 error, got %A" other

          testCase "prepareStatement accepts SET/SHOW/transaction-control forms the grammar itself doesn't parse"
          <| fun _ ->
              // Laravel's Schema::disableForeignKeyConstraints() runs
              // Connection::statement(), which always calls PDO::prepare()
              // regardless of emulation — real MySQL PDO's default is
              // ATTR_EMULATE_PREPARES = false, so even a bare `SET
              // FOREIGN_KEY_CHECKS=0` goes through COM_STMT_PREPARE.
              for sql in
                  [ "SET FOREIGN_KEY_CHECKS=0"
                    "SET NAMES utf8mb4"
                    "START TRANSACTION"
                    "COMMIT"
                    "SHOW TABLES" ] do
                  match prepareStatement sql with
                  | Result.Ok 0 -> ()
                  | other -> failtestf "expected %s to prepare with 0 placeholders, got %A" sql other

          testCase "a prepared INSERT/SELECT round-trips through textual substitution + the normal execution path"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE ps_t (id INT, name VARCHAR(50))"
              let stmtSql = "INSERT INTO ps_t (id, name) VALUES (?, ?)"

              let finalSql =
                  substitutePlaceholders
                      stmtSql
                      [ valueToSqlLiteral (VInt 1L); valueToSqlLiteral (VString "alice") ]

              let session, insertResult = handle session finalSql

              match insertResult with
              | Affected 1UL -> ()
              | other -> failtestf "expected 1 affected row, got %A" other

              match handle session "SELECT name FROM ps_t WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "alice" ] ]) -> ()
              | other -> failtestf "expected alice, got %A" other ]
