module Fsdb.Tests.IntegrationTests

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
          // code path from a second, independent client implementation.
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
              |> Async.RunSynchronously ]
