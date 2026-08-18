module Fsdb.Tests.IntegrationTests

open System
open Expecto
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let tests =
    testList
        "Integration"
        [ testCase "mysql client can connect, SELECT 1, and read @@version"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

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
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

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

                      // PDO::lastInsertId()/MySqlCommand.LastInsertedId both
                      // read this straight off the OK packet.
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

          testCase "BLOB and VARBINARY bytes survive a real MySqlConnector round-trip"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use create = conn.CreateCommand()
                      create.CommandText <- "CREATE TABLE binary_values (fixed VARBINARY(8), payload BLOB)"
                      do! create.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                      use insert = conn.CreateCommand()
                      insert.CommandText <- "INSERT INTO binary_values VALUES (X'00ff80', X'deadbeef')"
                      do! insert.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                      use select = conn.CreateCommand()
                      select.CommandText <- "SELECT fixed, payload FROM binary_values"
                      use! reader = select.ExecuteReaderAsync() |> Async.AwaitTask
                      let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow "binary row present"
                      Expect.equal (reader.GetFieldValue<byte[]>(0)) [| 0x00uy; 0xffuy; 0x80uy |] "VARBINARY bytes"
                      Expect.equal (reader.GetFieldValue<byte[]>(1)) [| 0xdeuy; 0xaduy; 0xbeuy; 0xefuy |] "BLOB bytes"
                      do! reader.CloseAsync() |> Async.AwaitTask
                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // Forces the binary COM_STMT_PREPARE/COM_STMT_EXECUTE path via
          // MySqlCommand.Prepare() (MySqlConnector otherwise inlines
          // parameters as literal text over COM_QUERY) — the only way this
          // suite exercises binary parameter decoding from a real client.
          // `@name`-style parameters are used here (MySqlConnector's usual
          // style); MySqlConnector itself rewrites them to positional `?`
          // before it ever hits the wire, so this still exercises the
          // server's `?`-counting/substitution path the same as a client
          // that writes `?` directly. php PDO with
          // `PDO::ATTR_EMULATE_PREPARES => false` exercises the same server
          // code path from a second, independent client implementation, but
          // not from this suite — there is no PHP runtime here.
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
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

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

                      // `id`/`score`/`active` now come back column-typed
                      // (LONGLONG/DOUBLE) rather than a blanket VAR_STRING
                      // — see `Value.mysqlTypeOf` — so a real ADO.NET
                      // reader hands back native `Int64`/`Double` for them,
                      // the same as it would against real MySQL; only
                      // `name` (VARCHAR) is still a string.
                      let! hasRow1 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow1 "first row present"
                      Expect.equal (reader.GetInt64 0) 1L "row 1 id"
                      Expect.equal (reader.GetString 1) "alice" "row 1 name"
                      Expect.equal (reader.GetDouble 2) 3.5 "row 1 score"
                      Expect.equal (reader.GetInt64 3) 1L "row 1 active"

                      let! hasRow2 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow2 "second row present"
                      Expect.equal (reader.GetInt64 0) 2L "row 2 id"
                      Expect.isTrue (reader.IsDBNull 1) "row 2 name is NULL"
                      Expect.equal (reader.GetDouble 2) -1.25 "row 2 score"
                      Expect.equal (reader.GetInt64 3) 0L "row 2 active"

                      let! hasRow3 = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isFalse hasRow3 "only two rows"

                      do! reader.CloseAsync() |> Async.AwaitTask
                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          testCase "disconnect rolls back a prepared transaction and releases the transaction gate"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=False"
                              port

                      use setup = new MySqlConnector.MySqlConnection(connStr)
                      do! setup.OpenAsync() |> Async.AwaitTask
                      use create = setup.CreateCommand()
                      create.CommandText <- "CREATE TABLE disconnect_tx (id INT PRIMARY KEY, n INT)"
                      do! create.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                      use seed = setup.CreateCommand()
                      seed.CommandText <- "INSERT INTO disconnect_tx VALUES (1, 0)"
                      do! seed.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                      do! setup.CloseAsync() |> Async.AwaitTask

                      // Exercise COM_STMT_EXECUTE while holding the transaction
                      // gate, then drop the client without COMMIT or ROLLBACK.
                      let abandoned = new MySqlConnector.MySqlConnection(connStr)
                      do! abandoned.OpenAsync() |> Async.AwaitTask
                      let! abandonedTx = abandoned.BeginTransactionAsync().AsTask() |> Async.AwaitTask
                      use abandonedUpdate = abandoned.CreateCommand()
                      abandonedUpdate.Transaction <- abandonedTx
                      abandonedUpdate.CommandText <- "UPDATE disconnect_tx SET n = n + @delta WHERE id = @id"
                      abandonedUpdate.Parameters.AddWithValue("@delta", 100) |> ignore
                      abandonedUpdate.Parameters.AddWithValue("@id", 1) |> ignore
                      do! abandonedUpdate.PrepareAsync() |> Async.AwaitTask
                      let! abandonedAffected = abandonedUpdate.ExecuteNonQueryAsync() |> Async.AwaitTask
                      Expect.equal abandonedAffected 1 "the abandoned prepared update ran inside its transaction"
                      abandoned.Dispose()
                      abandonedTx.Dispose()

                      // If disconnect cleanup fails, this prepared update blocks
                      // behind the leaked gate. The deadline turns that into a
                      // bounded and diagnostic test failure instead of a hung run.
                      use timeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                      use survivor = new MySqlConnector.MySqlConnection(connStr)
                      do! survivor.OpenAsync(timeout.Token) |> Async.AwaitTask
                      let! survivorTx = survivor.BeginTransactionAsync(timeout.Token).AsTask() |> Async.AwaitTask
                      use survivorTx = survivorTx
                      use survivorUpdate = survivor.CreateCommand()
                      survivorUpdate.Transaction <- survivorTx
                      survivorUpdate.CommandText <- "UPDATE disconnect_tx SET n = n + @delta WHERE id = @id"
                      survivorUpdate.Parameters.AddWithValue("@delta", 1) |> ignore
                      survivorUpdate.Parameters.AddWithValue("@id", 1) |> ignore
                      do! survivorUpdate.PrepareAsync(timeout.Token) |> Async.AwaitTask
                      let! survivorAffected = survivorUpdate.ExecuteNonQueryAsync(timeout.Token) |> Async.AwaitTask
                      Expect.equal survivorAffected 1 "a later transaction is not wedged behind the disconnected client"
                      do! survivorTx.CommitAsync(timeout.Token) |> Async.AwaitTask

                      use read = survivor.CreateCommand()
                      read.CommandText <- "SELECT n FROM disconnect_tx WHERE id = 1"
                      let! finalValue = read.ExecuteScalarAsync(timeout.Token) |> Async.AwaitTask
                      Expect.equal (Convert.ToInt64 finalValue) 1L "the abandoned +100 rolled back and only +1 committed"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          testCase "a table with an index and a foreign key is visible through information_schema and SHOW CREATE TABLE"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

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

          // readBinaryValue's `Reader.ReadBytes` runs outside
          // `QueryHandler.handle`'s try/with, so a COM_STMT_EXECUTE payload
          // whose declared param type doesn't match the bytes actually on
          // the wire must not throw straight out of the connection loop and
          // drop the socket with no ERR packet. A well-behaved driver never
          // sends this, but a malformed/adversarial one shouldn't be able to
          // silently kill the connection either — hand this one over the
          // wire directly since no real client library will construct it.
          // Declares MYSQL_TYPE_LONGLONG (needs 8 bytes) but supplies only 2.
          testCase "a malformed COM_STMT_EXECUTE param payload gets an ERR, not a dropped connection"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

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
              |> Async.RunSynchronously

          // The wire-level commands the main MySqlConnector tests never
          // exercise: COM_INIT_DB (USE), the deprecated COM_FIELD_LIST
          // (PDO/mysqlnd metadata probing), COM_STMT_SEND_LONG_DATA, and
          // COM_STMT_EXECUTE re-using the previous execution's parameter
          // types. Raw packets, one connection, in order.
          testCase "raw wire commands: USE, COM_FIELD_LIST, long data, and parameter-type reuse"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      use client = new Net.Sockets.TcpClient()
                      do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      use stream = client.GetStream()

                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      // USE an existing database: OK; USE a missing one: 1049.
                      let! _ = query "CREATE DATABASE app"
                      let! _ = readPacketAsync stream

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x02uy |] (Text.Encoding.UTF8.GetBytes "app") }
                      let! useOk = readPacketAsync stream
                      Expect.equal useOk.Value.Payload.[0] 0x00uy "USE an existing database replies OK"

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x02uy |] (Text.Encoding.UTF8.GetBytes "missing") }
                      let! useErr = readPacketAsync stream
                      Expect.equal useErr.Value.Payload.[0] 0xffuy "USE a missing database replies ERR"
                      Expect.equal (Reader(useErr.Value.Payload.[1..]).ReadInt16LE()) 1049 "1049 unknown database"

                      let! _ = query "CREATE TABLE t (n INT)"
                      let! _ = readPacketAsync stream

                      // COM_FIELD_LIST: column defs + EOF for an existing
                      // table, ERR 1146 for a missing one.
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x04uy |] (Array.append (Text.Encoding.UTF8.GetBytes "t") [| 0uy |]) }
                      let! fieldList = readPacketAsync stream
                      Expect.isTrue fieldList.IsSome "COM_FIELD_LIST answers"
                      Expect.equal fieldList.Value.Payload.[0] 0x03uy "existing table: first packet is a column definition"
                      let! _ = readPacketAsync stream // trailing EOF

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x04uy |] (Array.append (Text.Encoding.UTF8.GetBytes "ghost") [| 0uy |]) }
                      let! fieldListErr = readPacketAsync stream
                      Expect.equal fieldListErr.Value.Payload.[0] 0xffuy "missing table: ERR"
                      Expect.equal (Reader(fieldListErr.Value.Payload.[1..]).ReadInt16LE()) 1146 "1146 table not found"

                      // Prepare "SELECT ?".
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT ?") }
                      let! prepareOk = readPacketAsync stream
                      let stmtId = Reader(prepareOk.Value.Payload.[1..]).ReadInt32LE()
                      let! _ = readPacketAsync stream // param column def
                      let! _ = readPacketAsync stream // trailing EOF

                      // Long data for an unknown statement is silently
                      // ignored (no reply, per protocol); for the real one it
                      // is buffered and substituted at EXECUTE time.
                      let longData (id: int) =
                          let w = Writer()
                          w.WriteByte 0x18uy
                          w.WriteInt32LE id
                          w.WriteInt16LE 0
                          w.WriteBytes(Text.Encoding.UTF8.GetBytes "abc")
                          writePacketAsync stream { SeqId = 0uy; Payload = w.ToArray() }

                      let! _ = longData 9999
                      let! _ = longData stmtId

                      let execute newParamsBound =
                          let w = Writer()
                          w.WriteByte 0x17uy
                          w.WriteInt32LE stmtId
                          w.WriteByte 0uy
                          w.WriteInt32LE 1
                          w.WriteByte 0uy // null bitmap: param not NULL
                          w.WriteByte newParamsBound

                          if newParamsBound = 1uy then
                              w.WriteByte TypeVarString
                              w.WriteByte 0uy // not unsigned

                          // Param values are always on the wire, whether or
                          // not the type descriptors were re-bound. The first
                          // execute ignores this (long data wins); the second
                          // reads it.
                          w.WriteLenEncString "def"
                          writePacketAsync stream { SeqId = 0uy; Payload = w.ToArray() }

                      // Executing without binding types on a never-executed
                      // statement is an error (LastParamTypes = None).
                      let! _ = execute 0uy
                      let! noTypes = readPacketAsync stream
                      Expect.equal noTypes.Value.Payload.[0] 0xffuy "execute with no bound types replies ERR"

                      // Bind a type this time: the buffered long data is
                      // substituted for the parameter value.
                      let! _ = execute 1uy
                      let! colCount = readPacketAsync stream
                      let! _ = readPacketAsync stream // column def
                      let! _ = readPacketAsync stream // EOF
                      let! row = readPacketAsync stream
                      let! _ = readPacketAsync stream // EOF
                      Expect.equal colCount.Value.Payload.[0] 0x01uy "one-column resultset"
                      Expect.stringContains (Text.Encoding.ASCII.GetString row.Value.Payload) "abc" "long data was substituted for the parameter"

                      // Re-execute without re-binding: the stored types are
                      // reused (LastParamTypes = Some).
                      let! _ = execute 0uy
                      let! reuseColCount = readPacketAsync stream
                      Expect.isTrue reuseColCount.IsSome "type reuse re-execution gets a reply"
                      Expect.equal reuseColCount.Value.Payload.[0] 0x01uy "reused types still produce the resultset"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // A truncated COM_STMT_CLOSE (needs a 4-byte statement id, gets
          // none) used to throw straight out of `Reader.ReadInt32LE` inside
          // `parseCommand`, which escaped the command loop entirely and
          // dropped the socket with no reply. It must now answer ERR and
          // leave the connection usable.
          testCase "a malformed short command packet gets an ERR, not a dropped connection"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      use client = new Net.Sockets.TcpClient()
                      do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      use stream = client.GetStream()

                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      // COM_STMT_CLOSE (0x19) with no statement id bytes at all.
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x19uy |] }
                      let! errReply = readPacketAsync stream
                      Expect.isTrue errReply.IsSome "the connection survives a truncated command"
                      Expect.equal errReply.Value.Payload.[0] 0xffuy "server replies ERR, not silence/close"

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                      let! afterReply = readPacketAsync stream
                      Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // COM_RESET_CONNECTION (0x1f): connection pools (PDO's persistent
          // connections, Doctrine's DBAL, ...) send this instead of a full
          // reconnect to clear session state. Used to fall through to
          // `Unsupported` and answer ERR 1047, breaking pooled reconnect.
          testCase "COM_RESET_CONNECTION replies OK and clears session state"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      use client = new Net.Sockets.TcpClient()
                      do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      use stream = client.GetStream()

                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      let! _ = query "SET @x = 42"
                      let! _ = readPacketAsync stream

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x1fuy |] }
                      let! resetReply = readPacketAsync stream
                      Expect.equal resetReply.Value.Payload.[0] 0x00uy "COM_RESET_CONNECTION replies OK"

                      // The user variable set before the reset no longer reads back.
                      let! _ = query "SELECT @x"
                      let! colCount = readPacketAsync stream
                      let! _ = readPacketAsync stream // column def
                      let! _ = readPacketAsync stream // EOF
                      let! row = readPacketAsync stream
                      Expect.equal colCount.Value.Payload.[0] 0x01uy "SELECT @x still answers after reset"
                      Expect.equal (Reader(row.Value.Payload).ReadLenEncInt()) None "@x reads back NULL: the reset cleared user variables"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // COM_STMT_SEND_LONG_DATA force-decoded every buffered chunk as
          // UTF-8 before substituting it as the bound parameter, corrupting
          // any byte sequence that isn't valid UTF-8. A BLOB param's bytes
          // must round-trip byte-identical.
          testCase "COM_STMT_SEND_LONG_DATA round-trips non-UTF-8 bytes for a BLOB parameter"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.empty |> Async.StartAsTask |> ignore

                  try
                      use client = new Net.Sockets.TcpClient()
                      do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      use stream = client.GetStream()

                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      let! _ = query "CREATE TABLE blobs (b BLOB)"
                      let! _ = readPacketAsync stream

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "INSERT INTO blobs VALUES (?)") }
                      let! prepareOk = readPacketAsync stream
                      let stmtId = Reader(prepareOk.Value.Payload.[1..]).ReadInt32LE()
                      let! _ = readPacketAsync stream // param column def
                      let! _ = readPacketAsync stream // trailing EOF

                      // Invalid UTF-8: a lone continuation byte and a byte
                      // that's never valid in UTF-8 at all — decoding this
                      // as UTF-8 and re-encoding would change the bytes.
                      let bytes = [| 0uy; 0xffuy; 0x80uy; 65uy; 66uy; 0xC0uy |]

                      let longDataPayload =
                          let w = Writer()
                          w.WriteByte 0x18uy
                          w.WriteInt32LE stmtId
                          w.WriteInt16LE 0
                          w.WriteBytes bytes
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = longDataPayload }

                      let execPayload =
                          let w = Writer()
                          w.WriteByte 0x17uy
                          w.WriteInt32LE stmtId
                          w.WriteByte 0uy
                          w.WriteInt32LE 1
                          w.WriteByte 0uy // null bitmap: param not NULL
                          w.WriteByte 1uy // new-params-bound
                          w.WriteByte TypeBlob
                          w.WriteByte 0uy // not unsigned
                          w.WriteLenEncString "" // placeholder value; long data wins
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                      let! _ = readPacketAsync stream // OK (INSERT)

                      let! _ = query "SELECT b FROM blobs"
                      let! _ = readPacketAsync stream // column count
                      let! _ = readPacketAsync stream // column def
                      let! _ = readPacketAsync stream // EOF
                      let! row = readPacketAsync stream

                      let r = Reader(row.Value.Payload)

                      match r.ReadLenEncInt() with
                      | Some len -> Expect.equal (r.ReadBytes(int len)) bytes "BLOB long-data bytes survive byte-identical"
                      | None -> failtest "expected a non-NULL blob value"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // The README's embedding example, exercised over the real wire
          // with a real MySqlConnector client — a custom scalar (SLUGIFY)
          // and a custom aggregate (MEDIAN) registered on a `Db` via
          // `Db.registerScalar`/`registerAggregate`, then queried.
          testCase "Db.registerScalar/registerAggregate are queryable over the wire"
          <| fun _ ->
              async {
                  let slugify =
                      function
                      | [ VString s ] ->
                          let lowered = s.ToLowerInvariant()

                          let slug =
                              Text.RegularExpressions.Regex.Replace(lowered, "[^a-z0-9]+", "-")
                              |> fun s -> s.Trim '-'

                          VString slug
                      | _ -> VNull

                  let median: Fsdb.Value.Value list -> Fsdb.Value.Value =
                      fun values ->
                          let sorted =
                              values
                              |> List.choose (function
                                  | VInt i -> Some(float i)
                                  | VDouble f -> Some f
                                  | _ -> None)
                              |> List.sort

                          match sorted with
                          | [] -> VNull
                          | _ ->
                              let n = List.length sorted
                              let mid = n / 2

                              if n % 2 = 0 then
                                  VDouble((sorted.[mid - 1] + sorted.[mid]) / 2.0)
                              else
                                  VDouble sorted.[mid]

                  let db =
                      Fsdb.Db.create ()
                      |> Fsdb.Db.registerScalar "SLUGIFY" slugify
                      |> Fsdb.Db.registerAggregate "MEDIAN" median

                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener db.Store db.Functions |> Async.StartAsTask |> ignore

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use slugCmd = conn.CreateCommand()
                      slugCmd.CommandText <- "SELECT SLUGIFY('Hello, World!')"
                      let! slugResult = slugCmd.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string slugResult) "hello-world" "SLUGIFY produces a slug over the wire"

                      let exec (sql: string) =
                          async {
                              use cmd = conn.CreateCommand()
                              cmd.CommandText <- sql
                              return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                          }

                      do! exec "CREATE TABLE scores (id INT AUTO_INCREMENT PRIMARY KEY, score INT)" |> Async.Ignore
                      do! exec "INSERT INTO scores (score) VALUES (1), (3), (2), (9), (4)" |> Async.Ignore

                      use medianCmd = conn.CreateCommand()
                      medianCmd.CommandText <- "SELECT MEDIAN(score) FROM scores"
                      let! medianResult = medianCmd.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string medianResult) "3" "MEDIAN aggregates over the wire"

                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          // A client that vanishes mid-query must not leave the server
          // computing into the void (88MB -> 2.65GB RSS observed from one
          // abandoned join). A registered scalar (`PROBE`) spliced
          // into the ON clause of a non-equi cross join — `t1.n <> t2.n`
          // has no equi key, so `applyJoin` takes the lazy nested-loop
          // fallback (`traverseSeq`), not the hash-join path — gives an
          // honest, non-flaky signal that server-side row evaluation
          // actually stopped: not "the socket closed" and not "a
          // *different* connection still feels responsive" (which a
          // multi-core box would show even if cancellation were a no-op),
          // but the real call count the row loop drives, watched until it
          // stops moving.
          testCase "a client disconnect mid-query stops server-side row evaluation"
          <| fun _ ->
              async {
                  let mutable probeCount = 0L

                  let probe =
                      function
                      | [ _ ] ->
                          System.Threading.Interlocked.Increment(&probeCount) |> ignore
                          VInt 1L
                      | _ -> VInt 1L

                  let registry = Fsdb.Functions.empty |> Fsdb.Functions.registerScalar "PROBE" probe

                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) registry |> Async.StartAsTask |> ignore

                  try
                      // `Pooling=false`: this test opens two separate
                      // connections against the same connection string
                      // (setup, then a follow-up health check), and a
                      // pooled `Open()` sends `COM_RESET_CONNECTION` —
                      // unimplemented, unrelated to what this test is
                      // actually checking.
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                              port

                      // Seed two plain tables big enough that their non-equi
                      // cross join (2,250,000 pairs) runs for several
                      // seconds — long enough to kill the client mid-flight
                      // and still have room to observe the row loop unwind.
                      use setupConn = new MySqlConnector.MySqlConnection(connStr)
                      do! setupConn.OpenAsync() |> Async.AwaitTask

                      let exec (sql: string) =
                          async {
                              use cmd = setupConn.CreateCommand()
                              cmd.CommandText <- sql
                              return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                          }

                      // Sized so the full cross product (16,000,000 pairs)
                      // couldn't finish inside this test's own polling
                      // window even on a fast machine — cancellation kicks
                      // in on wall-clock time regardless of table size, so
                      // this costs nothing on the passing path, but it's
                      // what keeps a *broken* cancellation path from
                      // accidentally passing this test just by finishing
                      // the join naturally before the poll deadline.
                      let rowsPerTable = 4000
                      let totalPairs = int64 rowsPerTable * int64 rowsPerTable
                      do! exec "CREATE TABLE t1 (n INT)" |> Async.Ignore
                      do! exec "CREATE TABLE t2 (n INT)" |> Async.Ignore
                      let values n = String.Join(",", [ for i in 1 .. n -> sprintf "(%d)" i ])
                      do! exec (sprintf "INSERT INTO t1 (n) VALUES %s" (values rowsPerTable)) |> Async.Ignore
                      do! exec (sprintf "INSERT INTO t2 (n) VALUES %s" (values rowsPerTable)) |> Async.Ignore
                      do! setupConn.CloseAsync() |> Async.AwaitTask

                      // Fire the doomed query over its own raw socket —
                      // handshake by hand so the test can kill the
                      // connection out from under the query without any
                      // higher-level client machinery muddying what "the
                      // client vanished" means.
                      use doomed = new Net.Sockets.TcpClient()
                      do! doomed.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                      let stream = doomed.GetStream()
                      let! handshake = readPacketAsync stream
                      let handshakeSeq = handshake.Value.SeqId

                      let helloResponse =
                          let w = Writer()
                          w.WriteInt32LE(int ClientProtocol41)
                          w.WriteInt32LE 16777216
                          w.WriteByte 45uy
                          w.WriteBytes(Array.zeroCreate<byte> 23)
                          w.WriteNullTerminatedString "root"
                          w.WriteByte 0uy
                          w.ToArray()

                      let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
                      let! _ = readPacketAsync stream // connection OK

                      let joinSql = "SELECT COUNT(*) FROM t1 JOIN t2 ON t1.n <> t2.n AND PROBE(t1.n) = 1"
                      let queryPayload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes joinSql)
                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = queryPayload }

                      // Let the join get properly underway, then kill the
                      // client without ever reading a reply — exactly what
                      // a killed CLI client or a closed browser tab looks
                      // like from the server's side.
                      do! Async.Sleep 1000
                      let countAfterKick = System.Threading.Interlocked.Read(&probeCount)
                      Expect.isTrue (countAfterKick > 0L) "the join was actually underway before the kill"
                      doomed.Close()

                      // Poll PROBE's call count until two checks in a row
                      // agree — the honest "stopped" signal. A genuinely
                      // cancelled query settles within a couple of poll
                      // ticks (the watcher polls the socket every 50ms,
                      // `traverse`/`traverseSeq` check cancellation every
                      // 256 rows); a query that ignores cancellation keeps
                      // incrementing right through this whole window.
                      let mutable lastCount = System.Threading.Interlocked.Read(&probeCount)
                      let mutable stable = false
                      let deadline = DateTime.UtcNow.AddSeconds 5.0

                      while not stable && DateTime.UtcNow < deadline do
                          do! Async.Sleep 300
                          let current = System.Threading.Interlocked.Read(&probeCount)
                          stable <- current = lastCount
                          lastCount <- current

                      Expect.isTrue
                          stable
                          "PROBE's call count stopped growing after the client disconnected — the row fold actually unwound"

                      // Rules out the one way the above could pass without
                      // cancellation actually doing anything: the join
                      // finishing the full cross product on its own before
                      // the polling loop ever caught it stopping.
                      Expect.isTrue
                          (lastCount < totalPairs / 2L)
                          (sprintf
                              "only a fraction of the %d-pair cross product should have been evaluated (got %d) — it was interrupted, not finished"
                              totalPairs
                              lastCount)

                      // The server itself is unharmed — a fresh connection
                      // still gets served.
                      use followUp = new MySqlConnector.MySqlConnection(connStr)
                      do! followUp.OpenAsync() |> Async.AwaitTask
                      use pingCmd = followUp.CreateCommand()
                      pingCmd.CommandText <- "SELECT 1"
                      let! pingResult = pingCmd.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string pingResult) "1" "a follow-up connection is still served"
                      do! followUp.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously ]
