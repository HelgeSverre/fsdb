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

          testCase "valueToSqlLiteral renders raw bytes as a hexadecimal literal"
          <| fun _ ->
              let literal = valueToSqlLiteral (VBytes [| 0x00uy; 0xffuy; 0x80uy |])
              Expect.equal literal "X'00FF80'" "lossless binary literal"

              match Fsdb.Parser.parse ("SELECT " + literal) with
              | Result.Ok(Select { Projections = [ Lit(VBytes bytes), _ ] }) ->
                  Expect.equal bytes [| 0x00uy; 0xffuy; 0x80uy |] "prepared substitution round-trip"
              | other -> failtestf "expected a parsed binary literal, got %A" other

          testCase "prepareStatement reports the placeholder count for a valid statement"
          <| fun _ ->
              match prepareStatement "INSERT INTO t (a, b) VALUES (?, ?)" with
              | Result.Ok(Some _, 2) -> ()
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
                  | Result.Ok(None, 0) -> ()
                  | other -> failtestf "expected %s to prepare with 0 placeholders, got %A" sql other

          testCase "a prepared INSERT/SELECT binds values into the parsed AST and executes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE ps_t (id INT, name VARCHAR(50))"

              match prepareStatement "INSERT INTO ps_t (id, name) VALUES (?, ?)" with
              | Result.Ok(Some ast, 2) ->
                  let stmt =
                      { Ast = Some ast
                        Sql = "INSERT INTO ps_t (id, name) VALUES (?, ?)"
                        ParamCount = 2
                        LastParamTypes = None }

                  // The name carries a quote and a backslash — bound as a
                  // `Value` into the AST, never re-spliced SQL text, so there
                  // is nothing to escape and the string arrives intact.
                  let session, insertResult = executePrepared session stmt [ VInt 1L; VString "O'Brien\\" ]

                  match insertResult with
                  | Affected 1UL -> ()
                  | other -> failtestf "expected 1 affected row, got %A" other

                  match handle session "SELECT name FROM ps_t WHERE id = 1" |> snd with
                  | ResultSet(_, [ [ Some "O'Brien\\" ] ]) -> ()
                  | other -> failtestf "expected the bound name back, got %A" other
              | other -> failtestf "expected a parsed statement with 2 params, got %A" other

          testCase "a backtracked atom does not double-count its placeholder, and renumbering binds it correctly"
          <| fun _ ->
              // CONVERT(?, x) makes `convertUsingAtom` consume the `?` then
              // fail over to `genericFuncCall`; FParsec rewinds the input but
              // not the parse-time counter, so the raw count was 2 for one `?`
              // and the surviving node was `Placeholder 1` (a gap).
              match prepareStatement "SELECT CONVERT(?, CHAR)" with
              | Result.Ok(Some _, 1) -> ()
              | Result.Ok(Some _, n) -> failtestf "expected ParamCount 1, got %d" n
              | other -> failtestf "expected a parsed statement, got %A" other

              // A second `?` after the backtracked one: the raw counter
              // reported 3 for two placeholders; renumbering restores 2.
              match prepareStatement "SELECT CONVERT(?, CHAR), ? AS second" with
              | Result.Ok(Some _, 2) -> ()
              | Result.Ok(Some _, n) -> failtestf "expected ParamCount 2, got %d" n
              | other -> failtestf "expected a parsed statement, got %A" other ]
