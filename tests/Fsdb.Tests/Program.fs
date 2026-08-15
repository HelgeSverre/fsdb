module Fsdb.Tests.Program

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let packetTests =
    testList
        "Packet"
        [ testCase "lenenc int round-trips small values"
          <| fun _ ->
              for v in [ 0UL; 1UL; 250UL ] do
                  let w = Writer()
                  w.WriteLenEncInt v
                  let r = Reader(w.ToArray())
                  Expect.equal (r.ReadLenEncInt ()) (Some v) (sprintf "lenenc int %d" v)

          testCase "lenenc int round-trips 2-byte, 3-byte, and 8-byte values"
          <| fun _ ->
              for v in [ 251UL; 65535UL; 65536UL; 16777215UL; 16777216UL; 4294967295UL ] do
                  let w = Writer()
                  w.WriteLenEncInt v
                  let r = Reader(w.ToArray())
                  Expect.equal (r.ReadLenEncInt ()) (Some v) (sprintf "lenenc int %d" v)

          testCase "lenenc string round-trips"
          <| fun _ ->
              let w = Writer()
              w.WriteLenEncString "hello, world"
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadLenEncString ()) (Some "hello, world") "lenenc string"

          testCase "lenenc null decodes to None"
          <| fun _ ->
              let w = Writer()
              w.WriteLenEncNull ()
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadLenEncInt ()) None "lenenc null"

          testCase "null-terminated string round-trips"
          <| fun _ ->
              let w = Writer()
              w.WriteNullTerminatedString "root"
              w.WriteByte 0xAAuy
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadNullTerminatedString ()) "root" "null-terminated string"
              Expect.equal (r.ReadByte ()) 0xAAuy "byte after string"

          testCase "packet framing round-trips through a MemoryStream"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let original = { SeqId = 3uy; Payload = [| 1uy; 2uy; 3uy; 4uy; 5uy |] }
                  let! nextSeqId = writePacketAsync stream original
                  Expect.equal nextSeqId 4uy "next seq id after a single-chunk write"
                  stream.Position <- 0L
                  let! result = readPacketAsync stream

                  match result with
                  | Some p ->
                      Expect.equal p.SeqId original.SeqId "seq id"
                      Expect.equal p.Payload original.Payload "payload"
                  | None -> failtest "expected a packet"
              }
              |> Async.RunSynchronously

          testCase "writePacketAsync splits a payload >= 16 MiB instead of truncating the length header"
          <| fun _ ->
              // Regression: frame's 3-byte length prefix can't represent
              // more than 0xffffff bytes; naively writing an oversized
              // payload silently declared `length &&& 0xffffff` and then
              // wrote the full body, permanently desyncing the connection.
              async {
                  use stream = new IO.MemoryStream()
                  let payload = Array.zeroCreate<byte> maxPacketPayload // exactly the 0xffffff boundary
                  let! nextSeqId = writePacketAsync stream { SeqId = 5uy; Payload = payload }
                  // An exact-multiple-of-maxPacketPayload payload needs a
                  // trailing empty packet so the reader knows where it ends.
                  Expect.equal nextSeqId 7uy "two wire packets consumed for an exact-boundary payload"

                  stream.Position <- 0L
                  let header1 = Array.zeroCreate<byte> 4
                  stream.Read(header1, 0, 4) |> ignore
                  let r1 = Reader(header1)
                  Expect.equal (r1.ReadInt24LE()) maxPacketPayload "first chunk declares the max length"
                  Expect.equal (r1.ReadByte()) 5uy "first chunk seq id"

                  stream.Seek(int64 maxPacketPayload, IO.SeekOrigin.Current) |> ignore
                  let header2 = Array.zeroCreate<byte> 4
                  stream.Read(header2, 0, 4) |> ignore
                  let r2 = Reader(header2)
                  Expect.equal (r2.ReadInt24LE ()) 0 "trailing packet declares zero length"
                  Expect.equal (r2.ReadByte ()) 6uy "trailing packet seq id"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync returns None on clean disconnect"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream([||])
                  let! result = readPacketAsync stream
                  Expect.equal result None "empty stream yields no packet"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync reassembles a payload split across multiple wire packets"
          <| fun _ ->
              // Regression: a payload of exactly maxPacketPayload bytes means
              // "more packets follow" per the protocol. Any client sending a
              // statement >= 16 MiB (MySqlConnector, Connector/J,
              // libmysqlclient all do) sends it this way; failing to
              // reassemble ran the first chunk as its own command and then
              // treated the continuation as a new one. Uses `frame` directly
              // (not writePacketAsync, which auto-splits/terminates) so the
              // test controls the exact wire fragmentation.
              async {
                  use stream = new IO.MemoryStream()
                  let firstChunk = Array.create maxPacketPayload 7uy
                  let secondChunk = [| 1uy; 2uy; 3uy |]
                  let bytes1 = frame { SeqId = 9uy; Payload = firstChunk }
                  stream.Write(bytes1, 0, bytes1.Length)
                  let bytes2 = frame { SeqId = 10uy; Payload = secondChunk }
                  stream.Write(bytes2, 0, bytes2.Length)
                  stream.Position <- 0L

                  let! result = readPacketAsync stream

                  match result with
                  | Some p ->
                      Expect.equal p.SeqId 9uy "reassembled packet keeps the FIRST fragment's seq id"
                      Expect.equal p.Payload.Length (maxPacketPayload + 3) "payload is the concatenation of both chunks"
                      Expect.equal p.Payload.[maxPacketPayload..] secondChunk "tail bytes come from the second chunk"
                  | None -> failtest "expected a reassembled packet"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync raises PacketTooLargeException instead of allocating unboundedly"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  // Enough maxPacketPayload-sized fragments (each declaring
                  // "more data follows") to exceed maxAccumulatedPacketSize.
                  let chunkCount = maxAccumulatedPacketSize / maxPacketPayload + 2
                  let chunk = Array.zeroCreate<byte> maxPacketPayload

                  for i in 0 .. chunkCount - 1 do
                      let bytes = frame { SeqId = byte i; Payload = chunk }
                      stream.Write(bytes, 0, bytes.Length)

                  stream.Position <- 0L

                  let! outcome = Async.Catch(readPacketAsync stream)

                  match outcome with
                  | Choice2Of2(:? PacketTooLargeException) -> ()
                  | Choice1Of2 _ -> failtest "expected PacketTooLargeException, got a result"
                  | Choice2Of2 ex -> failtestf "expected PacketTooLargeException, got %A" ex
              }
              |> Async.RunSynchronously ]

let protocolTests =
    testList
        "Protocol"
        [ testCase "HandshakeV10 payload starts with protocol version 10 and the server version"
          <| fun _ ->
              let authData = Array.create 20 1uy
              let payload = buildHandshakeV10 42 authData
              let r = Reader(payload)
              Expect.equal (r.ReadByte ()) 10uy "protocol version"
              Expect.equal (r.ReadNullTerminatedString ()) ServerVersion "server version string"
              Expect.equal (r.ReadInt32LE ()) 42 "connection id"

          testCase "HandshakeV10 payload declares CLIENT_PLUGIN_AUTH and ends with the plugin name"
          <| fun _ ->
              let payload = buildHandshakeV10 1 (Array.create 20 1uy)
              let text = Text.Encoding.ASCII.GetString payload
              Expect.stringContains text "mysql_native_password" "auth plugin name present"

          testCase "OK payload starts with 0x00 header"
          <| fun _ ->
              let payload = okPayload ClientProtocol41 StatusAutocommit 0UL 0UL
              Expect.equal payload.[0] 0uy "OK header byte"

          testCase "OK payload status flags carry SERVER_STATUS_IN_TRANS while a transaction is open"
          <| fun _ ->
              // Regression: the status flags used to be hardcoded to
              // StatusAutocommit alone, so PDO's inTransaction() (which reads
              // this bit off the wire) always reported false.
              let payload = okPayload ClientProtocol41 (StatusAutocommit ||| StatusInTrans) 0UL 0UL
              let r = Reader(payload.[1..])
              r.ReadLenEncInt() |> ignore // affected rows
              r.ReadLenEncInt() |> ignore // last insert id
              let statusFlags = r.ReadInt16LE()
              Expect.isTrue (statusFlags &&& StatusInTrans <> 0) "SERVER_STATUS_IN_TRANS set"

          testCase "ERR payload carries the error code and message"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              Expect.equal payload.[0] 0xffuy "ERR header byte"
              let r = Reader(payload.[1..])
              Expect.equal (r.ReadInt16LE ()) 1064 "error code"

          testCase "ERR payload for 1064 carries SQLSTATE 42000, not the generic HY000"
          <| fun _ ->
              // Regression: PDO/Doctrine branch on SQLSTATE (42000 -> syntax
              // error, not the generic HY000) to classify exceptions.
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "42000" "sqlstate for 1064"

          testCase "ERR payload for an unmapped code falls back to HY000"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 9999 "whatever"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "HY000" "sqlstate fallback"

          testCase "the resultset-terminating OK uses header 0xfe, not 0x00"
          <| fun _ ->
              // Regression: mysql CLI distinguishes this from a plain OK by the
              // 0xfe header (it reuses the legacy EOF marker byte). Sending 0x00
              // here makes mysql_use_result callers (e.g. the CLI's startup
              // banner query) hang forever waiting for a terminator that never
              // looks like one.
              let payload = okEndOfResultSetPayload ClientProtocol41 StatusAutocommit
              Expect.equal payload.[0] 0xfeuy "end-of-resultset OK header byte"

          testCase "parseHandshakeResponse reads username and capabilities"
          <| fun _ ->
              let w = Writer()
              w.WriteInt32LE(int (ClientProtocol41 ||| ClientSecureConnection))
              w.WriteInt32LE 16777216 // max packet size
              w.WriteByte 45uy // charset
              w.WriteBytes(Array.zeroCreate<byte> 23) // reserved
              w.WriteNullTerminatedString "root"
              w.WriteByte 0uy // zero-length auth response
              let resp = parseHandshakeResponse (w.ToArray())
              Expect.equal resp.Username "root" "username"
              Expect.equal resp.Database None "no database requested" ]

let queryHandlerTests =
    testList
        "QueryHandler"
        [ testCase "SELECT 1 returns a single row with column name '1'"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 1" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols [ "1" ] "column name"
                  Expect.equal rows [ [ Some "1" ] ] "row value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version returns the server version"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version" |> snd with
              | ResultSet(cols, [ [ Some v ] ]) ->
                  Expect.equal cols [ "@@version" ] "column name"
                  Expect.equal v ServerVersion "version value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version, @@version_comment returns both columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version, @@version_comment" |> snd with
              | ResultSet(cols, [ row ]) ->
                  Expect.equal cols [ "@@version"; "@@version_comment" ] "columns"
                  Expect.equal (List.length row) 2 "row has two values"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@unknown_var returns a 1193 unknown-system-variable error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@totally_not_a_var" |> snd with
              | Err(1193, msg) -> Expect.stringContains msg "totally_not_a_var" "message names the variable"
              | other -> failtestf "expected a 1193 error, got %A" other

          testCase "the Connector/J connection probe (auto_increment_increment, transaction_isolation) resolves"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT @@session.auto_increment_increment AS auto_increment_increment, @@character_set_client AS character_set_client, @@session.transaction_isolation AS transaction_isolation"
                  |> snd
              with
              | ResultSet(_, [ [ Some "1"; Some _; Some "REPEATABLE-READ" ] ]) -> ()
              | other -> failtestf "expected all three variables resolved, got %A" other

          testCase "SELECT @@version_comment LIMIT 1 tolerates the trailing LIMIT clause"
          <| fun _ ->
              // Regression: mysql CLI probes the connection banner with exactly
              // this query at connect time.
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "select @@version_comment limit 1" |> snd with
              | ResultSet([ "@@version_comment" ], [ [ Some _ ] ]) -> ()
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET NAMES utf8mb4 returns OK"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET NAMES utf8mb4" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "SET NAMES updates character_set_client, reflected by SELECT @@character_set_client"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET NAMES latin1"

              match handle session "SELECT @@character_set_client" |> snd with
              | ResultSet(_, [ [ Some "latin1" ] ]) -> ()
              | other -> failtestf "expected latin1, got %A" other

          testCase "SET sql_mode = '...' updates the session variable, reflected by SELECT @@sql_mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'ANSI_QUOTES'"

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "ANSI_QUOTES" ] ]) -> ()
              | other -> failtestf "expected ANSI_QUOTES, got %A" other

          testCase "SELECT DATABASE() returns NULL before USE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected a single NULL row, got %A" other

          testCase "USE sets the session database, reflected by SELECT DATABASE()"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE mydb"

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "SHOW DATABASES returns a resultset"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet([ "Database" ], _ :: _) -> ()
              | other -> failtestf "expected a non-empty resultset, got %A" other

          testCase "SHOW VARIABLES LIKE filters by pattern"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW VARIABLES LIKE 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the autocommit row, got %A" other

          testCase "SHOW TABLES / SHOW FULL TABLES list the current database's tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"

              match handle session "SHOW TABLES" |> snd with
              | ResultSet([ "Tables_in_shop" ], [ [ Some "widgets" ] ]) -> ()
              | other -> failtestf "expected the one table, got %A" other

              match handle session "SHOW FULL TABLES" |> snd with
              | ResultSet([ "Tables_in_shop"; "Table_type" ], [ [ Some "widgets"; Some "BASE TABLE" ] ]) -> ()
              | other -> failtestf "expected the FULL variant's extra column, got %A" other

          testCase "SHOW COLUMNS FROM t / DESCRIBE t report field metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL)"

              match handle session "SHOW COLUMNS FROM widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "int"; Some "NO"; Some "PRI"; None; Some "auto_increment" ]
                        [ Some "name"; Some "varchar(50)"; Some "NO"; Some ""; None; Some "" ] ]
                      "both columns with their metadata"
              | other -> failtestf "expected a resultset, got %A" other

              match handle session "DESCRIBE widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) -> Expect.equal (List.length rows) 2 "DESCRIBE is SHOW COLUMNS under another name"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE reconstructs plausible DDL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) UNIQUE)"

              match handle session "SHOW CREATE TABLE widgets" |> snd with
              | ResultSet([ "Table"; "Create Table" ], [ [ Some "widgets"; Some ddl ] ]) ->
                  Expect.stringContains ddl "CREATE TABLE `widgets`" "names the table"
                  Expect.stringContains ddl "PRIMARY KEY (`id`)" "includes the primary key"
                  Expect.stringContains ddl "UNIQUE KEY `name`" "includes the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW INDEX FROM t lists the primary key and other indexes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY, sku VARCHAR(20) UNIQUE)"

              match handle session "SHOW INDEX FROM widgets" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols.[0] "Table" "first column is Table"
                  Expect.equal (List.length rows) 2 "primary key + the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "an unrecognized statement is a 1064 syntax error naming the query"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "GARBAGE NOT SQL" |> snd with
              | Err(1064, msg) -> Expect.stringContains msg "GARBAGE NOT SQL" "message names the query"
              | other -> failtestf "expected a 1064 error, got %A" other

          testCase "a query whose string data merely starts with SET is not hijacked by the SET-statement probe"
          <| fun _ ->
              // handle's `upper.StartsWith "SET "` check is anchored to the
              // whole trimmed query text, so this can't actually misfire —
              // this test documents and locks in that guarantee rather than
              // reproducing a live bug.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE notes (body VARCHAR(50))"

              match handle session "INSERT INTO notes VALUES ('SET x = 1')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected a normal INSERT, got %A" other

              match handle session "SELECT body FROM notes" |> snd with
              | ResultSet(_, [ [ Some "SET x = 1" ] ]) -> ()
              | other -> failtestf "expected the literal string preserved, got %A" other

          testCase "a query containing @@ inside a string literal is not hijacked by the @@-variable probe"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE users (email VARCHAR(50))"
              let session, _ = handle session "INSERT INTO users VALUES ('a@@b.com')"

              match handle session "SELECT * FROM users WHERE email LIKE '%@@%'" |> snd with
              | ResultSet(_, [ [ Some "a@@b.com" ] ]) -> ()
              | other -> failtestf "expected the row to be found via the real parser, got %A" other

          testCase "an exception inside the engine (decimal overflow) is an Err, not an escaping exception"
          <| fun _ ->
              // Storage.coerceValue's `decimal d` throws OverflowException for
              // a DECIMAL column given a value outside decimal's range — this
              // must never escape `handle` and drop the connection.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE overflow_t (d DECIMAL(10,2))"

              match handle session "INSERT INTO overflow_t VALUES (1e300)" |> snd with
              | Err(1105, _) -> ()
              | other -> failtestf "expected a 1105 internal-error Err, got %A" other ]

let preparedStatementTests =
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
              // Regression: a raw CR spliced into the SQL text gets silently
              // normalized to LF by FParsec's CharStream on re-parse (it
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

let transactionTests =
    testList
        "Transactions"
        [ testCase "a write inside BEGIN...COMMIT is invisible to another connection until commit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_t (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_t VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before commit, got %A" result

              let session, _ = handle session "COMMIT"
              ignore session

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the committed row, got %A" result

          testCase "ROLLBACK discards writes made inside the transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_r (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_r VALUES (1)"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_r" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the insert to be rolled back, got %A" result

          testCase "SAVEPOINT / ROLLBACK TO SAVEPOINT undoes only the writes made after it"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_s (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_s VALUES (1)"
              let session, _ = handle session "SAVEPOINT sp1"
              let session, _ = handle session "INSERT INTO tx_s VALUES (2)"
              let session, _ = handle session "ROLLBACK TO SAVEPOINT sp1"
              let session, _ = handle session "COMMIT"

              match handle session "SELECT id FROM tx_s ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the pre-savepoint row, got %A" result

          testCase "SET autocommit = 0 opens an implicit transaction; SET autocommit = 1 commits it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ac (id INT)"
              let session, _ = handle session "SET autocommit = 0"
              let session, _ = handle session "INSERT INTO tx_ac VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before autocommit = 1, got %A" result

              let session, _ = handle session "SET autocommit = 1"
              ignore session

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the row visible once autocommit = 1 commits it, got %A" result

          testCase "RELEASE SAVEPOINT on an unknown savepoint is a 1305 error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"

              match handle session "RELEASE SAVEPOINT nope" |> snd with
              | Err(1305, _) -> ()
              | result -> failtestf "expected a 1305 error, got %A" result ]

/// Reads every packet off a stream until clean EOF.
let private readAllPackets (stream: IO.Stream) : Async<Packet list> =
    let rec loop acc =
        async {
            match! readPacketAsync stream with
            | Some p -> return! loop (p :: acc)
            | None -> return List.rev acc
        }

    loop []

let serverTests =
    testList
        "Server"
        [ // Regression: sendQueryResult is the only sequence-id-bearing logic
          // in the server, and getting a resultset terminator wrong hangs
          // mysql CLI / mysqlnd forever waiting for a terminator that never
          // arrives (see the okEndOfResultSetPayload regression test above).
          // MySqlConnector always negotiates CLIENT_DEPRECATE_EOF, so the
          // integration test alone never exercises the legacy EOF path that
          // PDO/mysqlnd may still use — cover both here directly.
          for caps, label, terminator in
              [ ClientProtocol41 ||| ClientDeprecateEof, "CLIENT_DEPRECATE_EOF", 0xfeuy
                ClientProtocol41, "legacy EOF", 0xfeuy ] do
              testCase (sprintf "resultset packets are sequential and correctly terminated (%s)" label)
              <| fun _ ->
                  async {
                      use stream = new IO.MemoryStream()

                      do!
                          Fsdb.Server.sendQueryResult
                              stream
                              caps
                              1uy
                              StatusAutocommit
                              0UL
                              (ResultSet([ "a"; "b" ], [ [ Some "1"; None ] ]))

                      stream.Position <- 0L
                      let! packets = readAllPackets stream

                      Expect.sequenceEqual
                          (packets |> List.map (fun p -> p.SeqId))
                          [ 1uy .. byte packets.Length ]
                          "sequence ids are contiguous starting at 1"

                      Expect.equal
                          (Reader(packets.Head.Payload).ReadLenEncInt())
                          (Some 2UL)
                          "first packet is the column count"

                      Expect.equal (List.last packets).Payload.[0] terminator "terminator header" }
                  |> Async.RunSynchronously

          testCase "the legacy EOF path emits one more packet than CLIENT_DEPRECATE_EOF (the column-defs EOF)"
          <| fun _ ->
              async {
                  let run caps =
                      async {
                          use stream = new IO.MemoryStream()
                          do! Fsdb.Server.sendQueryResult stream caps 1uy StatusAutocommit 0UL (ResultSet([ "a" ], [ [ Some "1" ] ]))
                          stream.Position <- 0L
                          return! readAllPackets stream
                      }

                  let! deprecated = run (ClientProtocol41 ||| ClientDeprecateEof)
                  let! legacy = run ClientProtocol41
                  Expect.equal (List.length legacy) (List.length deprecated + 1) "legacy path has one extra packet" }
              |> Async.RunSynchronously ]

let integrationTests =
    testList
        "Integration"
        [ testCase "mysql client can connect, SELECT 1, and read @@version"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener (Fsdb.Storage.create ()) |> Async.StartAsTask

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use cmd1 = conn.CreateCommand()
                      cmd1.CommandText <- "SELECT 1"
                      let! result1 = cmd1.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string result1) "1" "SELECT 1 result"

                      use cmd2 = conn.CreateCommand()
                      cmd2.CommandText <- "SELECT @@version"
                      let! result2 = cmd2.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string result2) ServerVersion "SELECT @@version result"

                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          testCase "a real CRUD round-trip: create table, insert, select, update, delete"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener (Fsdb.Storage.create ()) |> Async.StartAsTask

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      let exec (sql: string) =
                          async {
                              use cmd = conn.CreateCommand()
                              cmd.CommandText <- sql
                              return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                          }

                      do! exec "CREATE TABLE crud_users (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50), age INT)" |> Async.Ignore

                      use insertCmd = conn.CreateCommand()
                      insertCmd.CommandText <- "INSERT INTO crud_users (name, age) VALUES ('alice', 30), ('bob', 25)"
                      let! inserted = insertCmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                      Expect.equal inserted 2 "two rows inserted"

                      // Regression: the OK packet's last_insert_id used to be
                      // hardcoded to 0 regardless of what the session tracked,
                      // so PDO::lastInsertId()/MySqlCommand.LastInsertedId
                      // always read back "0".
                      Expect.equal insertCmd.LastInsertedId 1L "OK packet reports the real last_insert_id"

                      use selectCmd = conn.CreateCommand()
                      selectCmd.CommandText <- "SELECT name, age FROM crud_users WHERE age > 26 ORDER BY name"
                      use! reader = selectCmd.ExecuteReaderAsync() |> Async.AwaitTask
                      let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow "one row matches age > 26"
                      Expect.equal (reader.GetString 0) "alice" "matching row is alice"
                      let! hasMore = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isFalse hasMore "only one row matches"
                      do! reader.CloseAsync() |> Async.AwaitTask

                      let! updated = exec "UPDATE crud_users SET age = 31 WHERE name = 'alice'"
                      Expect.equal updated 1 "one row updated"

                      let! deleted = exec "DELETE FROM crud_users WHERE name = 'bob'"
                      Expect.equal deleted 1 "one row deleted"

                      use countCmd = conn.CreateCommand()
                      countCmd.CommandText <- "SELECT UPPER(name), age FROM crud_users"
                      let! upperName = countCmd.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string upperName) "ALICE" "UPPER() applied through the function registry"

                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // Forces the binary COM_STMT_PREPARE/COM_STMT_EXECUTE path via
          // MySqlCommand.Prepare() (MySqlConnector otherwise inlines
          // parameters as literal text over COM_QUERY) — the only way this
          // suite exercises Goal A's binary parameter decoding from a real
          // client. `@name`-style parameters are used here (MySqlConnector's
          // usual style); MySqlConnector itself rewrites them to positional
          // `?` before it ever hits the wire, so this still exercises the
          // server's `?`-counting/substitution path the same as a client
          // that writes `?` directly. php PDO with
          // `PDO::ATTR_EMULATE_PREPARES => false` exercises the same server
          // code path from a second, independent client implementation —
          // covered in the Grind phase, since this suite has no PHP runtime.
          // Reads back via `GetString`/`IsDBNull` rather than the typed
          // getters (`GetInt32`, `GetDouble`, ...): every column this server
          // advertises is MYSQL_TYPE_VAR_STRING (see `columnDefPayload`),
          // and MySqlConnector's strict typed accessors throw
          // `InvalidCastException` for a getter that doesn't match the
          // wire-declared type, regardless of the SQL column's real type.
          testCase "server-side prepared statements: MySqlCommand.Prepare() with several bound param types, executed twice"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener (Fsdb.Storage.create ()) |> Async.StartAsTask

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use createCmd = conn.CreateCommand()

                      createCmd.CommandText <-
                          "CREATE TABLE ps_int (id INT, name VARCHAR(50), score DOUBLE, active TINYINT)"

                      do! createCmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                      use insertCmd = conn.CreateCommand()

                      insertCmd.CommandText <-
                          "INSERT INTO ps_int (id, name, score, active) VALUES (@id, @name, @score, @active)"

                      insertCmd.Parameters.AddWithValue("@id", 1) |> ignore
                      insertCmd.Parameters.AddWithValue("@name", "alice") |> ignore
                      insertCmd.Parameters.AddWithValue("@score", 3.5) |> ignore
                      insertCmd.Parameters.AddWithValue("@active", 1) |> ignore
                      do! insertCmd.PrepareAsync() |> Async.AwaitTask
                      let! affected1 = insertCmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                      Expect.equal affected1 1 "first prepared INSERT"

                      // Re-executes the SAME prepared statement with new
                      // values (one of them NULL) — exercises
                      // COM_STMT_EXECUTE's new-params-bound-flag path, since
                      // MySqlConnector resends bound types on every execute.
                      insertCmd.Parameters.["@id"].Value <- 2
                      insertCmd.Parameters.["@name"].Value <- DBNull.Value
                      insertCmd.Parameters.["@score"].Value <- -1.25
                      insertCmd.Parameters.["@active"].Value <- 0
                      let! affected2 = insertCmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                      Expect.equal affected2 1 "second prepared INSERT with a NULL param"

                      use selectCmd = conn.CreateCommand()
                      selectCmd.CommandText <- "SELECT id, name, score, active FROM ps_int ORDER BY id"
                      do! selectCmd.PrepareAsync() |> Async.AwaitTask
                      use! reader = selectCmd.ExecuteReaderAsync() |> Async.AwaitTask

                      let! hasRow1 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow1 "first row present"
                      Expect.equal (reader.GetString 0) "1" "row 1 id"
                      Expect.equal (reader.GetString 1) "alice" "row 1 name"
                      Expect.equal (reader.GetString 2) "3.5" "row 1 score"
                      Expect.equal (reader.GetString 3) "1" "row 1 active"

                      let! hasRow2 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow2 "second row present"
                      Expect.equal (reader.GetString 0) "2" "row 2 id"
                      Expect.isTrue (reader.IsDBNull 1) "row 2 name is NULL"
                      Expect.equal (reader.GetString 2) "-1.25" "row 2 score"
                      Expect.equal (reader.GetString 3) "0" "row 2 active"

                      let! hasRow3 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isFalse hasRow3 "only two rows"

                      do! reader.CloseAsync() |> Async.AwaitTask
                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          testCase "a table with an index and a foreign key is visible through information_schema and SHOW CREATE TABLE"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener (Fsdb.Storage.create ()) |> Async.StartAsTask

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;Database=shop;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      let exec (sql: string) =
                          async {
                              use cmd = conn.CreateCommand()
                              cmd.CommandText <- sql
                              return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                          }

                      let scalar (sql: string) =
                          async {
                              use cmd = conn.CreateCommand()
                              cmd.CommandText <- sql
                              return! cmd.ExecuteScalarAsync() |> Async.AwaitTask
                          }

                      // `Database=shop` on the connection string exercises the
                      // handshake's auto-create path (`shop` doesn't exist yet).
                      let! dbName = scalar "SELECT DATABASE()"
                      Expect.equal (string dbName) "shop" "connecting with Database=shop auto-created it"

                      do!
                          exec "CREATE TABLE users (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(255) NOT NULL UNIQUE)"
                          |> Async.Ignore

                      do!
                          exec
                              "CREATE TABLE posts (id INT AUTO_INCREMENT PRIMARY KEY, user_id INT, CONSTRAINT posts_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE)"
                          |> Async.Ignore

                      let! tableType =
                          scalar
                              "SELECT table_type FROM information_schema.tables WHERE table_schema = 'shop' AND table_name = 'posts'"

                      Expect.equal (string tableType) "BASE TABLE" "posts shows up in information_schema.tables"

                      let! columnType =
                          scalar
                              "SELECT column_type FROM information_schema.columns WHERE table_schema = 'shop' AND table_name = 'users' AND column_name = 'email'"

                      Expect.equal (string columnType) "varchar(255)" "information_schema.columns reports the declared type"

                      let! indexColumn =
                          scalar
                              "SELECT column_name FROM information_schema.statistics WHERE table_schema = 'shop' AND table_name = 'users' AND index_name = 'email'"

                      Expect.equal (string indexColumn) "email" "the column-level UNIQUE surfaces in information_schema.statistics"

                      let! refTable =
                          scalar
                              "SELECT referenced_table_name FROM information_schema.key_column_usage WHERE table_schema = 'shop' AND table_name = 'posts' AND referenced_table_name IS NOT NULL"

                      Expect.equal (string refTable) "users" "the foreign key surfaces in information_schema.key_column_usage"

                      use showCmd = conn.CreateCommand()
                      showCmd.CommandText <- "SHOW CREATE TABLE users"
                      use! showReader = showCmd.ExecuteReaderAsync() |> Async.AwaitTask
                      let! hasRow = showReader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow "SHOW CREATE TABLE returns one row"
                      let ddl = showReader.GetString 1
                      Expect.stringContains ddl "PRIMARY KEY (`id`)" "SHOW CREATE TABLE reconstructs the primary key"
                      Expect.stringContains ddl "UNIQUE KEY `email`" "SHOW CREATE TABLE reconstructs the unique index"
                      do! showReader.CloseAsync() |> Async.AwaitTask

                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // Regression: a COM_STMT_EXECUTE payload whose declared param type
          // doesn't match the bytes actually on the wire used to throw
          // straight out of the connection loop (readBinaryValue's
          // `Reader.ReadBytes` runs outside `QueryHandler.handle`'s
          // try/with) and drop the socket with no ERR packet. A well-behaved
          // driver never sends this, but a malformed/adversarial one
          // shouldn't be able to silently kill the connection either — hand
          // this one over the wire directly since no real client library
          // will construct it. Declares MYSQL_TYPE_LONGLONG (needs 8 bytes)
          // but supplies only 2.
          testCase "a malformed COM_STMT_EXECUTE param payload gets an ERR, not a dropped connection"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener (Fsdb.Storage.create ()) |> Async.StartAsTask

                  try
                      use client = new Net.Sockets.TcpClient()
                      do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      use stream = client.GetStream()

                      // Handshake: read the server's HandshakeV10, reply with a
                      // minimal HandshakeResponse41 (no auth, no database).
                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy // zero-length auth response
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      // COM_STMT_PREPARE "SELECT ?"
                      let prepPayload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT ?")
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = prepPayload }
                      let! prepareOk = readPacketAsync stream
                      let stmtId = Reader(prepareOk.Value.Payload.[1..]).ReadInt32LE()
                      let! _ = readPacketAsync stream // the one param's column-def packet
                      let! _ = readPacketAsync stream // its trailing EOF

                      // COM_STMT_EXECUTE: stmtId, cursor flags, iteration
                      // count, null bitmap (1 byte, param count 1), new-
                      // params-bound=1, type=LONGLONG/signed, then only 2
                      // payload bytes where 8 are required.
                      let execPayload =
                          let w = Writer()
                          w.WriteByte 0x17uy
                          w.WriteInt32LE stmtId
                          w.WriteByte 0uy
                          w.WriteInt32LE 1
                          w.WriteByte 0uy // null bitmap
                          w.WriteByte 1uy // new-params-bound
                          w.WriteByte TypeLongLong
                          w.WriteByte 0uy // signed
                          w.WriteBytes [| 1uy; 2uy |] // only 2 of the 8 bytes LONGLONG needs
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                      let! errReply = readPacketAsync stream
                      Expect.isTrue errReply.IsSome "the connection is still alive after the malformed EXECUTE"
                      Expect.equal errReply.Value.Payload.[0] 0xffuy "server replies ERR, not silence/close"

                      // The connection itself must still be usable afterwards.
                      let queryPayload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1")
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = queryPayload }
                      let! afterReply = readPacketAsync stream
                      Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously ]

[<EntryPoint>]
let main argv =
    Tests.runTestsWithCLIArgs
        []
        argv
        (testList
            "fsdb"
            [ packetTests
              protocolTests
              queryHandlerTests
              preparedStatementTests
              transactionTests
              serverTests
              ValueTests.tests
              ParserTests.tests
              StorageTests.tests
              ExecutorTests.tests
              InformationSchemaTests.tests
              integrationTests ])
