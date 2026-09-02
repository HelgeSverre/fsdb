module Fsdb.Tests.IntegrationTests

open System
open System.Net.Security
open System.Security.Authentication
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Expecto
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.ColumnWire
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let private passwordlessHandshakeResponse (capabilities: uint32) (username: string) =
    let writer = Writer()
    writer.WriteInt32LE(int capabilities)
    writer.WriteInt32LE 16777216
    writer.WriteByte 45uy
    writer.WriteBytes(Array.zeroCreate<byte> 23)
    writer.WriteNullTerminatedString username
    writer.WriteByte 0uy
    writer.ToArray()

let private nativePasswordResponse (password: string) (scramble: byte[]) =
    let stage1 = SHA1.HashData(Text.Encoding.UTF8.GetBytes password)
    let mask = SHA1.HashData(Array.append scramble (SHA1.HashData stage1))
    Array.map2 (^^^) stage1 mask

let private changeUserPayload (username: string) (authResponse: byte[]) (database: string) characterSet plugin =
    let writer = Writer()
    writer.WriteByte 0x11uy
    writer.WriteNullTerminatedString username
    writer.WriteByte(byte authResponse.Length)
    writer.WriteBytes authResponse
    writer.WriteNullTerminatedString database
    writer.WriteInt16LE characterSet
    plugin |> Option.iter writer.WriteNullTerminatedString
    writer.ToArray()

let private selfSignedCertificate () =
    use key = RSA.Create 2048
    let request = CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, false))
    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddDays(1.0))

let private certificateAuthority () =
    use key = RSA.Create 2048
    let request = CertificateRequest("CN=fsdb test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
    request.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true))
    request.CertificateExtensions.Add(X509SubjectKeyIdentifierExtension(request.PublicKey, false))
    request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddDays(1.0))

let private clientCertificate (issuer: X509Certificate2) =
    use key = RSA.Create 2048
    let request = CertificateRequest("CN=fsdb client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))
    request.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true))
    let usages = OidCollection()
    usages.Add(Oid "1.3.6.1.5.5.7.3.2") |> ignore
    request.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(usages, true))
    let serialNumber = RandomNumberGenerator.GetBytes 16

    use certificate =
        request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1.0), DateTimeOffset.UtcNow.AddHours(12.0), serialNumber)

    certificate.CopyWithPrivateKey key

let private connectTls (port: int) (certificate: X509Certificate2 option) =
    async {
        let client = new Net.Sockets.TcpClient()
        do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
        let rawStream = client.GetStream()
        let! greeting = readPacketAsync rawStream
        let greeting = greeting |> Option.defaultWith (fun () -> failtest "the server sends its greeting")
        let capabilities = ClientProtocol41 ||| ClientSsl ||| ClientSecureConnection
        let request = Writer()
        request.WriteInt32LE(int capabilities)
        request.WriteInt32LE 16777216
        request.WriteByte 45uy
        request.WriteBytes(Array.zeroCreate<byte> 23)
        do! writePacketAsync rawStream { SeqId = greeting.SeqId + 1uy; Payload = request.ToArray() } |> Async.Ignore

        let secured = new SslStream(rawStream, false, fun _ _ _ _ -> true)
        let authentication = SslClientAuthenticationOptions()
        authentication.TargetHost <- "localhost"
        authentication.EnabledSslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13

        certificate
        |> Option.iter (fun clientCertificate ->
            let certificates = X509CertificateCollection()
            certificates.Add clientCertificate |> ignore
            authentication.ClientCertificates <- certificates)

        try
            do! secured.AuthenticateAsClientAsync(authentication) |> Async.AwaitTask
            return client, secured, greeting.SeqId
        with error ->
            secured.Dispose()
            client.Dispose()
            return raise error
    }

let private connectRawAsWithCapabilitiesAndScramble
    (port: int)
    (username: string)
    (capabilities: uint32)
    : Async<Net.Sockets.TcpClient * IO.Stream * byte[]> =
    async {
        let client = new Net.Sockets.TcpClient()
        do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
        let stream = client.GetStream()

        let! handshake = readPacketAsync stream
        let handshakeSeq = handshake.Value.SeqId
        let reader = Reader(handshake.Value.Payload)
        reader.ReadByte() |> ignore
        reader.ReadNullTerminatedString() |> ignore
        reader.ReadInt32LE() |> ignore
        let authPart1 = reader.ReadBytes 8
        reader.ReadByte() |> ignore
        reader.ReadInt16LE() |> ignore
        reader.ReadByte() |> ignore
        reader.ReadInt16LE() |> ignore
        reader.ReadInt16LE() |> ignore
        reader.ReadByte() |> ignore
        reader.ReadBytes 10 |> ignore
        let scramble = Array.append authPart1 (reader.ReadBytes 12)

        let helloResponse = passwordlessHandshakeResponse capabilities username

        let! _ = writePacketAsync stream { SeqId = handshakeSeq + 1uy; Payload = helloResponse }
        let! _ = readPacketAsync stream // connection OK
        return client, (stream :> IO.Stream), scramble
    }

let private connectRawAsWithCapabilities (port: int) (username: string) (capabilities: uint32) : Async<Net.Sockets.TcpClient * IO.Stream> =
    async {
        let! client, stream, _ = connectRawAsWithCapabilitiesAndScramble port username capabilities
        return client, stream
    }

let private connectRawAs (port: int) (username: string) = connectRawAsWithCapabilities port username ClientProtocol41

/// Connects a raw client as the passwordless bootstrap account.
let private connectRaw (port: int) : Async<Net.Sockets.TcpClient * IO.Stream> = connectRawAs port "root"

let private readPreparedReply (stream: IO.Stream) =
    async {
        let! prepareOk = readPacketAsync stream
        let reader = Reader(prepareOk.Value.Payload.[1..])
        let statementId = reader.ReadInt32LE()
        let columnCount = reader.ReadInt16LE()
        let parameterCount = reader.ReadInt16LE()

        let readDefinitions count =
            async {
                let definitions = ResizeArray<Packet>()

                for _ in 1 .. count do
                    let! definition = readPacketAsync stream
                    definitions.Add definition.Value

                if count > 0 then
                    let! _ = readPacketAsync stream
                    ()

                return List.ofSeq definitions
            }

        let! parameterDefinitions = readDefinitions parameterCount
        let! columnDefinitions = readDefinitions columnCount
        return statementId, parameterDefinitions, columnDefinitions
    }

type private WireDefinition =
    { CharacterSet: int
      Metadata: ColumnMetadata }

let private readWireDefinition (packet: Packet) =
    let reader = Reader(packet.Payload)

    for _ in 1..6 do
        reader.ReadLenEncString() |> ignore

    reader.ReadLenEncInt() |> ignore
    let characterSet = reader.ReadInt16LE()
    let columnLength = uint32 (reader.ReadInt32LE())
    let typeId = reader.ReadByte()
    let flags = uint16 (reader.ReadInt16LE())
    let decimals = reader.ReadByte()

    { CharacterSet = characterSet
      Metadata =
        { TypeId = typeId
          ColumnLength = columnLength
          Flags = flags
          Decimals = decimals
          CollationId = None
          Origin = None } }

let tests =
    testList
        "Integration"
        [ testCase "mysql client can connect, SELECT 1, and read @@version"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
              }
              |> Async.RunSynchronously

          testCase "MySqlConnector reads procedure resultsets and OUT parameters"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let connectionString =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;AllowUserVariables=True"
                          server.Port

                  use connection = new MySqlConnector.MySqlConnection(connectionString)
                  do! connection.OpenAsync() |> Async.AwaitTask
                  use create = connection.CreateCommand()

                  create.CommandText <-
                      "CREATE PROCEDURE wire_results(IN a INT, OUT b INT) BEGIN SELECT a AS first_result; SET b = a + 1; SELECT b AS second_result; END"

                  let! _ = create.ExecuteNonQueryAsync() |> Async.AwaitTask
                  use call = connection.CreateCommand()
                  call.CommandText <- "CALL wire_results(4, @output)"
                  use! reader = call.ExecuteReaderAsync() |> Async.AwaitTask
                  let! first = reader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue first "first result row"
                  Expect.equal (reader.GetInt32 0) 4 "first result value"
                  let! hasSecond = reader.NextResultAsync() |> Async.AwaitTask
                  Expect.isTrue hasSecond "second resultset"
                  let! second = reader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue second "second result row"
                  Expect.equal (reader.GetInt32 0) 5 "second result value"
                  let! hasThird = reader.NextResultAsync() |> Async.AwaitTask
                  Expect.isFalse hasThird "final OK is not exposed as a resultset"
                  do! reader.CloseAsync() |> Async.AwaitTask

                  use output = connection.CreateCommand()
                  output.CommandText <- "SELECT @output"
                  let! value = output.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (Convert.ToInt32 value) 5 "OUT parameter value"
              }
              |> Async.RunSynchronously

          testCase "DDL clears the wire transaction status"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let queryStatus (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! response = readPacketAsync stream
                          Expect.equal response.Value.Payload.[0] 0uy "the statement returns an OK packet"
                          let reader = Reader(response.Value.Payload.[1..])
                          reader.ReadLenEncInt() |> ignore
                          reader.ReadLenEncInt() |> ignore
                          return reader.ReadInt16LE()
                      }

                  let! begun = queryStatus "BEGIN"
                  Expect.isTrue (begun &&& StatusInTrans <> 0) "BEGIN advertises an active transaction"
                  let! created = queryStatus "CREATE TABLE ddl_state_schema (id INT PRIMARY KEY)"
                  Expect.equal (created &&& StatusInTrans) 0 "DDL advertises that its implicit commit ended the transaction"
              }
              |> Async.RunSynchronously

          testCase "MySqlConnector can negotiate zlib compression"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;UseCompression=True"
                          server.Port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask
                  use command = conn.CreateCommand()
                  command.CommandText <- "SELECT REPEAT('compressible-', 512)"
                  let! value = command.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string value).Length (13 * 512) "a compressed result round-trips"

                  command.CommandText <- "SHOW STATUS LIKE 'Compression'"
                  use! reader = command.ExecuteReaderAsync() |> Async.AwaitTask
                  let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue hasRow "compression status has one row"
                  Expect.equal (reader.GetString 1) "ON" "the negotiated compressed connection is reported"
              }
              |> Async.RunSynchronously

          testCase "CLIENT_MULTI_STATEMENTS returns sequenced results"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream =
                      connectRawAsWithCapabilities port "root" (ClientProtocol41 ||| ClientMultiStatements ||| ClientMultiResults)

                  use client = client

                  let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1; SELECT 2")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                  let rec receive count packets =
                      async {
                          if count = 0 then
                              return List.rev packets
                          else
                              match! readPacketAsync stream with
                              | Some packet -> return! receive (count - 1) (packet :: packets)
                              | None -> return failtest "the server returned every result packet"
                      }

                  let! packets = receive 10 []
                  Expect.sequenceEqual (packets |> List.map (fun packet -> packet.SeqId)) [ 1uy .. 10uy ] "response packets are continuous"
                  let firstTerminator = packets.[4]
                  let status = Reader(firstTerminator.Payload.[1..])
                  status.ReadInt16LE() |> ignore
                  Expect.isTrue (status.ReadInt16LE() &&& StatusMoreResultsExists <> 0) "first result has MORE_RESULTS"
                  let finalTerminator = packets.[9]
                  let finalStatus = Reader(finalTerminator.Payload.[1..])
                  finalStatus.ReadInt16LE() |> ignore
                  Expect.equal (finalStatus.ReadInt16LE() &&& StatusMoreResultsExists) 0 "last result clears MORE_RESULTS"
              }
              |> Async.RunSynchronously

          testCase "CLIENT_SESSION_TRACK reports schema and system-variable assignments"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let capabilities = ClientProtocol41 ||| ClientSessionTrack
                  let! client, stream = connectRawAsWithCapabilities server.Port "root" capabilities
                  use client = client

                  let query (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          return! readPacketAsync stream
                      }

                  let sessionState (packet: Packet) =
                      let reader = Reader(packet.Payload.[1..])
                      reader.ReadLenEncInt() |> ignore
                      reader.ReadLenEncInt() |> ignore
                      let status = reader.ReadInt16LE()
                      reader.ReadInt16LE() |> ignore
                      reader.ReadLenEncString() |> ignore
                      let length = reader.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0
                      status, Reader(reader.ReadBytes length)

                  let! _ = query "CREATE DATABASE tracked_wire"
                  let! used = query "USE tracked_wire"
                  let useStatus, useState = sessionState used.Value
                  Expect.isTrue (useStatus &&& StatusSessionStateChanged <> 0) "USE marks the session changed"
                  Expect.equal (useState.ReadByte()) SessionTrackSchema "schema tracker"
                  let schema = useState.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0 |> useState.ReadBytes |> Reader
                  Expect.equal (schema.ReadLenEncString()) (Some "tracked_wire") "tracked schema"

                  let! assigned = query "SET autocommit = 1"
                  let setStatus, setState = sessionState assigned.Value
                  Expect.isTrue (setStatus &&& StatusSessionStateChanged <> 0) "SET marks the session changed"
                  Expect.equal (setState.ReadByte()) SessionTrackSystemVariables "system-variable tracker"
                  let variable = setState.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0 |> setState.ReadBytes |> Reader
                  Expect.equal (variable.ReadLenEncString()) (Some "autocommit") "tracked variable"
                  Expect.equal (variable.ReadLenEncString()) (Some "ON") "tracked value"

                  let! transactionTracking = query "SET session_track_transaction_info = CHARACTERISTICS"
                  let transactionStatus, transactionState = sessionState transactionTracking.Value
                  Expect.isTrue
                      (transactionStatus &&& StatusSessionStateChanged <> 0)
                      "transaction tracker marks the session changed"
                  Expect.equal
                      (transactionState.ReadByte())
                      SessionTrackTransactionCharacteristics
                      "transaction-characteristics tracker"
                  let initialCharacteristics =
                      transactionState.ReadLenEncInt()
                      |> Option.map int
                      |> Option.defaultValue 0
                      |> transactionState.ReadBytes
                      |> Reader
                  Expect.equal (initialCharacteristics.ReadLenEncString()) (Some "") "default characteristics"

                  let! begun = query "START TRANSACTION READ ONLY"
                  let beginStatus, beginState = sessionState begun.Value
                  Expect.isTrue (beginStatus &&& StatusInTrans <> 0) "transaction status flag"
                  Expect.equal
                      (beginState.ReadByte())
                      SessionTrackTransactionCharacteristics
                      "begin characteristics"
                  let characteristics =
                      beginState.ReadLenEncInt()
                      |> Option.map int
                      |> Option.defaultValue 0
                      |> beginState.ReadBytes
                      |> Reader
                  Expect.equal
                      (characteristics.ReadLenEncString())
                      (Some "START TRANSACTION READ ONLY;")
                      "wire replay statement"
                  Expect.equal (beginState.ReadByte()) SessionTrackTransactionState "begin transaction state"
                  let state =
                      beginState.ReadLenEncInt()
                      |> Option.map int
                      |> Option.defaultValue 0
                      |> beginState.ReadBytes
                      |> Reader
                  Expect.equal (state.ReadLenEncString()) (Some "T_______") "wire transaction state"

                  let! rolledBack = query "ROLLBACK"
                  let rollbackStatus, rollbackState = sessionState rolledBack.Value
                  Expect.equal (rollbackStatus &&& StatusInTrans) 0 "rollback status flag"
                  Expect.equal
                      (rollbackState.ReadByte())
                      SessionTrackTransactionCharacteristics
                      "rollback characteristics"
                  let cleared =
                      rollbackState.ReadLenEncInt()
                      |> Option.map int
                      |> Option.defaultValue 0
                      |> rollbackState.ReadBytes
                      |> Reader
                  Expect.equal (cleared.ReadLenEncString()) (Some "") "cleared characteristics"
                  Expect.equal (rollbackState.ReadByte()) SessionTrackTransactionState "rollback transaction state"
                  let clearedState =
                      rollbackState.ReadLenEncInt()
                      |> Option.map int
                      |> Option.defaultValue 0
                      |> rollbackState.ReadBytes
                      |> Reader
                  Expect.equal (clearedState.ReadLenEncString()) (Some "________") "cleared transaction state"
                  Expect.equal rollbackState.Remaining 0 "all rollback tracker bytes consumed"
              }
              |> Async.RunSynchronously

          testCase "COM_SET_OPTION toggles multi-statements on the live connection"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream =
                      connectRawAsWithCapabilities
                          server.Port
                          "root"
                          (ClientProtocol41 ||| ClientMultiStatements ||| ClientMultiResults)

                  use client = client
                  let command bytes = writePacketAsync stream { SeqId = 0uy; Payload = bytes } |> Async.Ignore
                  let query = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1; SELECT 2")

                  do! command [| 0x1buy; 1uy; 0uy |]
                  let! disabled = readPacketAsync stream
                  Expect.equal disabled.Value.Payload.[0] 0xfeuy "disable returns EOF"
                  do! command query
                  let! rejected = readPacketAsync stream
                  Expect.equal rejected.Value.Payload.[0] 0xffuy "disabled batches are rejected"

                  do! command [| 0x1buy; 0uy; 0uy |]
                  let! enabled = readPacketAsync stream
                  Expect.equal enabled.Value.Payload.[0] 0xfeuy "enable returns EOF"
                  do! command query

                  let mutable packets = 0
                  let mutable more = true

                  while more do
                      let! packet = readPacketAsync stream
                      packets <- packets + 1
                      more <- packets < 10

                  Expect.equal packets 10 "both resultsets are returned"
              }
              |> Async.RunSynchronously

          testCase "CLIENT_MULTI_STATEMENTS stops after an error"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRawAsWithCapabilities port "root" (ClientProtocol41 ||| ClientMultiStatements ||| ClientMultiResults)
                  use client = client
                  let sql = "CREATE TABLE batch_before (id INT); INSERT INTO missing_batch VALUES (1); CREATE TABLE batch_after (id INT)"
                  do! writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) } |> Async.Ignore
                  let! first = readPacketAsync stream
                  let! second = readPacketAsync stream
                  Expect.equal first.Value.Payload.[0] 0uy "first statement succeeds"
                  let status = Reader(first.Value.Payload.[1..])
                  status.ReadLenEncInt() |> ignore
                  status.ReadLenEncInt() |> ignore
                  Expect.isTrue (status.ReadInt16LE() &&& StatusMoreResultsExists <> 0) "first result has MORE_RESULTS"
                  Expect.equal second.Value.Payload.[0] 0xffuy "second statement terminates the batch"
                  Expect.equal second.Value.SeqId 2uy "error continues response packet numbering"
                  Expect.isTrue (Fsdb.Storage.scanList store "fsdb" "batch_before" |> Result.isOk) "prior statement remains applied"
                  Expect.isTrue (Fsdb.Storage.scanList store "fsdb" "batch_after" |> Result.isError) "later statement is skipped"
              }
              |> Async.RunSynchronously

          testCase "a semicolon batch without CLIENT_MULTI_STATEMENTS has no side effects"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client
                  let sql = "CREATE TABLE disallowed_batch (id INT); INSERT INTO disallowed_batch VALUES (1)"
                  do! writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) } |> Async.Ignore
                  let! rejected = readPacketAsync stream
                  Expect.equal rejected.Value.Payload.[0] 0xffuy "batch is rejected"
                  Expect.equal (Reader(rejected.Value.Payload.[1..]).ReadInt16LE()) 1064 "batch requires CLIENT_MULTI_STATEMENTS"
                  Expect.isTrue (Fsdb.Storage.scanList store "fsdb" "disallowed_batch" |> Result.isError) "first statement is not applied"
              }
              |> Async.RunSynchronously

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE receives client bytes without reading a server path"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let port = server.Port

                      let! client, stream = connectRawAsWithCapabilities port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client
                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      do! query "CREATE TABLE load_rows (id INT PRIMARY KEY, name VARCHAR(20))" |> Async.Ignore
                      let! _ = readPacketAsync stream
                      do! query "LOAD DATA LOCAL INFILE 'client-only.tsv' INTO TABLE load_rows" |> Async.Ignore
                      let! request = readPacketAsync stream
                      Expect.equal request.Value.SeqId 1uy "LOCAL request sequence"
                      Expect.equal request.Value.Payload.[0] 0xfbuy "LOCAL request header"
                      Expect.equal (Text.Encoding.UTF8.GetString(request.Value.Payload.[1..])) "client-only.tsv" "server echoes the client file name"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "1\tAda\n1\tDuplicate\n2\tGrace\n" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! result = readPacketAsync stream
                      Expect.equal result.Value.SeqId 4uy "LOAD result sequence"
                      Expect.equal result.Value.Payload.[0] 0uy "LOAD result is OK"
                      let ok = Reader(result.Value.Payload.[1..])
                      ok.ReadLenEncInt() |> ignore
                      ok.ReadLenEncInt() |> ignore
                      ok.ReadInt16LE() |> ignore
                      Expect.equal (ok.ReadInt16LE()) 1 "LOCAL duplicate is a warning"

                      match Fsdb.Storage.scanList store "fsdb" "load_rows" with
                      | Ok(_, rows) ->
                          Expect.equal (rows |> List.map (fun row -> row.[0], row.[1])) [ VInt 1L, VString "Ada"; VInt 2L, VString "Grace" ] "uploaded rows"
                      | Error error -> failtestf "table scan failed: %A" error
                  }
                  |> Async.RunSynchronously)

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE accepts string field and line terminators"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let! client, stream = connectRawAsWithCapabilities server.Port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client

                      let query (sql: string) =
                          writePacketAsync
                              stream
                              { SeqId = 0uy
                                Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      do! query "CREATE TABLE string_separated_load (id INT PRIMARY KEY, name VARCHAR(20))" |> Async.Ignore
                      let! _ = readPacketAsync stream

                      do!
                          query
                              "LOAD DATA LOCAL INFILE 'string-separated.txt' INTO TABLE string_separated_load CHARACTER SET utf8mb4 FIELDS TERMINATED BY '::' LINES TERMINATED BY '💥'"
                          |> Async.Ignore

                      let! request = readPacketAsync stream
                      Expect.equal request.Value.Payload.[0] 0xfbuy "LOCAL request"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "1::alpha💥2::beta💥" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! loaded = readPacketAsync stream
                      Expect.equal loaded.Value.Payload.[0] 0uy "LOAD succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "string_separated_load" with
                      | Ok(_, rows) ->
                          Expect.sequenceEqual
                              (rows |> List.map Array.toList)
                              [ [ VInt 1L; VString "alpha" ]; [ VInt 2L; VString "beta" ] ]
                              "string delimiters split uploaded rows"
                      | Error error -> failtestf "table scan failed: %A" error
                  }
                  |> Async.RunSynchronously)

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE transforms user-variable fields"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let! client, stream = connectRawAsWithCapabilities server.Port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client

                      let query (sql: string) =
                          writePacketAsync
                              stream
                              { SeqId = 0uy
                                Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      do!
                          query
                              "CREATE TABLE transformed_load (id INT NOT NULL AUTO_INCREMENT, name VARCHAR(20), doubled INT, label VARCHAR(40), untouched INT DEFAULT 9)"
                          |> Async.Ignore

                      let! _ = readPacketAsync stream

                      do!
                          query
                              "LOAD DATA LOCAL INFILE 'transformed.tsv' INTO TABLE transformed_load (@raw_id, name) SET id = CAST(@raw_id AS UNSIGNED), doubled = id * 2, label = CONCAT(name, ':', doubled)"
                          |> Async.Ignore

                      let! request = readPacketAsync stream
                      Expect.equal request.Value.Payload.[0] 0xfbuy "LOCAL request"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "100\tAda\n101\tGrace\n" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! loaded = readPacketAsync stream
                      Expect.equal loaded.Value.Payload.[0] 0uy "LOAD succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "transformed_load" with
                      | Ok(_, rows) ->
                          Expect.sequenceEqual
                              (rows |> List.map Array.toList)
                              [ [ VInt 100L; VString "Ada"; VInt 200L; VString "Ada:200"; VInt 9L ]
                                [ VInt 101L; VString "Grace"; VInt 202L; VString "Grace:202"; VInt 9L ] ]
                              "coerced and sequential SET values"
                      | Error error -> failtestf "table scan failed: %A" error

                      do! query "SELECT @raw_id" |> Async.Ignore
                      let! _ = readPacketAsync stream
                      let! _ = readPacketAsync stream
                      let! _ = readPacketAsync stream
                      let! variable = readPacketAsync stream
                      let! _ = readPacketAsync stream
                      Expect.equal (Reader(variable.Value.Payload).ReadLenEncString()) (Some "101") "last input variable persists"

                      do! query "INSERT INTO transformed_load (name) VALUES ('Later')" |> Async.Ignore
                      let! inserted = readPacketAsync stream
                      Expect.equal inserted.Value.Payload.[0] 0uy "ordinary insert succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "transformed_load" with
                      | Ok(_, rows) -> Expect.equal rows.[2].[0] (VInt 102L) "SET auto-increment values advance the sequence"
                      | Error error -> failtestf "table scan failed: %A" error

                      do!
                          query
                              "LOAD DATA LOCAL INFILE 'null-id.tsv' INTO TABLE transformed_load (@discard, name) SET id = NULL, doubled = 1, label = name"
                          |> Async.Ignore

                      let! request = readPacketAsync stream
                      Expect.equal request.Value.Payload.[0] 0xfbuy "NULL auto-increment LOCAL request"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "ignored\tNullId\n" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! loaded = readPacketAsync stream
                      Expect.equal loaded.Value.Payload.[0] 0uy "NULL auto-increment LOAD succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "transformed_load" with
                      | Ok(_, rows) -> Expect.equal rows.[3].[0] (VInt 103L) "NULL assignment generates the next key"
                      | Error error -> failtestf "table scan failed: %A" error

                      do!
                          query "CREATE TABLE transformed_view_base (id INT PRIMARY KEY, name VARCHAR(20), label VARCHAR(40))"
                          |> Async.Ignore

                      let! _ = readPacketAsync stream

                      do!
                          query
                              "CREATE VIEW transformed_view (view_id, view_name, view_label) AS SELECT id, name, label FROM transformed_view_base"
                          |> Async.Ignore

                      let! _ = readPacketAsync stream

                      do!
                          query
                              "LOAD DATA LOCAL INFILE 'view.tsv' INTO TABLE transformed_view (@raw_id, view_name) SET view_id = @raw_id, view_label = CONCAT(view_name, ':', view_id)"
                          |> Async.Ignore

                      let! request = readPacketAsync stream
                      Expect.equal request.Value.Payload.[0] 0xfbuy "view LOCAL request"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "7\tMapped\n" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! loaded = readPacketAsync stream
                      Expect.equal loaded.Value.Payload.[0] 0uy "view LOAD succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "transformed_view_base" with
                      | Ok(_, rows) ->
                          Expect.sequenceEqual
                              (rows |> List.map Array.toList)
                              [ [ VInt 7L; VString "Mapped"; VString "Mapped:7" ] ]
                              "LOAD targets writable view columns"
                      | Error error -> failtestf "table scan failed: %A" error
                  }
                  |> Async.RunSynchronously)

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE applies transformations before REPLACE"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let! client, stream = connectRawAsWithCapabilities server.Port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client

                      let query (sql: string) =
                          writePacketAsync
                              stream
                              { SeqId = 0uy
                                Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      do! query "CREATE TABLE replaced_load (id INT NOT NULL PRIMARY KEY, name VARCHAR(20), marker VARCHAR(40))" |> Async.Ignore
                      let! _ = readPacketAsync stream
                      do! query "INSERT INTO replaced_load VALUES (1, 'Old', 'old')" |> Async.Ignore
                      let! _ = readPacketAsync stream

                      do!
                          query
                              "LOAD DATA LOCAL INFILE 'replace.tsv' REPLACE INTO TABLE replaced_load (@raw_id, name) SET id = @raw_id, marker = CONCAT(name, ':', id)"
                          |> Async.Ignore

                      let! request = readPacketAsync stream
                      Expect.equal request.Value.Payload.[0] 0xfbuy "LOCAL request"
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes "1\tNew\n2\tAdded\n" } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! loaded = readPacketAsync stream
                      Expect.equal loaded.Value.Payload.[0] 0uy "REPLACE load succeeds"

                      match Fsdb.Storage.scanList store "fsdb" "replaced_load" with
                      | Ok(_, rows) ->
                          Expect.sequenceEqual
                              (rows |> List.map Array.toList)
                              [ [ VInt 1L; VString "New"; VString "New:1" ]
                                [ VInt 2L; VString "Added"; VString "Added:2" ] ]
                              "transformed candidates replace by their final keys"
                      | Error error -> failtestf "table scan failed: %A" error
                  }
                  |> Async.RunSynchronously)

          testCase "LOAD DATA LOCAL INFILE rejects disabled and prepared commands"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client
                  let query = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "LOAD DATA LOCAL INFILE 'never-read' INTO TABLE missing")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = query } |> Async.Ignore
                  let! rejected = readPacketAsync stream
                  Expect.equal rejected.Value.Payload.[0] 0xffuy "disabled LOCAL never sends a file request"
                  Expect.equal (Reader(rejected.Value.Payload.[1..]).ReadInt16LE()) 3948 "disabled LOCAL error code"

                  let prepare = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "LOAD DATA LOCAL INFILE 'never-read' INTO TABLE missing")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = prepare } |> Async.Ignore
                  let! prepared = readPacketAsync stream
                  Expect.equal prepared.Value.Payload.[0] 0xffuy "prepared LOCAL is rejected"
                  Expect.equal (Reader(prepared.Value.Payload.[1..]).ReadInt16LE()) 1295 "prepared LOCAL error code"
              }
              |> Async.RunSynchronously

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE derives NULL from the configured escape marker"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let port = server.Port

                      let! client, stream = connectRawAsWithCapabilities port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client
                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      let load (table: string) (clause: string) (data: string) =
                          async {
                              do! query (sprintf "CREATE TABLE %s (value VARCHAR(20))" table) |> Async.Ignore
                              let! _ = readPacketAsync stream
                              do! query (sprintf "LOAD DATA LOCAL INFILE 'null.tsv' INTO TABLE %s %s" table clause) |> Async.Ignore
                              let! _ = readPacketAsync stream
                              do! writePacketAsync stream { SeqId = 2uy; Payload = Text.Encoding.UTF8.GetBytes data } |> Async.Ignore
                              do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                              let! result = readPacketAsync stream
                              Expect.equal result.Value.Payload.[0] 0uy "LOAD result"
                          }

                      do! load "default_null" "" "\\N\n"
                      do! load "custom_null" "FIELDS ESCAPED BY '!'" "!N\n"
                      do! load "empty_escape" "FIELDS ESCAPED BY ''" "\\N\n"

                      let value table =
                          match Fsdb.Storage.scanList store "fsdb" table with
                          | Ok(_, [ row ]) -> row.[0]
                          | Ok(_, rows) -> failtestf "expected one row, got %A" rows
                          | Error error -> failtestf "table scan failed: %A" error

                      Expect.equal (value "default_null") VNull "default escape null"
                      Expect.equal (value "custom_null") VNull "custom escape null"
                      Expect.equal (value "empty_escape") (VString "\\N") "empty escape keeps literal text"
                  }
                  |> Async.RunSynchronously)

          TestSupport.processGlobalCase "LOAD DATA LOCAL INFILE drains an oversized upload before returning 1153"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "local_infile", "ON"; "max_load_data_bytes", "1024" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                      let port = server.Port

                      let! client, stream = connectRawAsWithCapabilities port "root" (ClientProtocol41 ||| ClientLocalFiles)
                      use client = client
                      let query (sql: string) =
                          writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql) }

                      do! query "CREATE TABLE limited_load (value TEXT)" |> Async.Ignore
                      let! _ = readPacketAsync stream
                      do! query "LOAD DATA LOCAL INFILE 'large.tsv' INTO TABLE limited_load" |> Async.Ignore
                      let! _ = readPacketAsync stream
                      do! writePacketAsync stream { SeqId = 2uy; Payload = Array.create 1025 120uy } |> Async.Ignore
                      do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                      let! rejected = readPacketAsync stream
                      Expect.equal rejected.Value.SeqId 4uy "overflow result sequence"
                      Expect.equal rejected.Value.Payload.[0] 0xffuy "overflow returns ERR"
                      Expect.equal (Reader(rejected.Value.Payload.[1..]).ReadInt16LE()) 1153 "overflow error code"
                      match Fsdb.Storage.scanList store "fsdb" "limited_load" with
                      | Ok(_, rows) -> Expect.isEmpty rows "no rows are published"
                      | Error error -> failtestf "table scan failed: %A" error
                  }
                  |> Async.RunSynchronously)

          testCase "TLS upgrades after SSLRequest and reports negotiated session values"
          <| fun _ ->
              async {
                  use certificate = selfSignedCertificate ()
                  let options = Fsdb.ServerOptions.defaults |> Fsdb.ServerOptions.withCertificate certificate
                  use server = TestSupport.ServerFixture.startWithOptions options (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  use rawClient = new Net.Sockets.TcpClient()
                  do! rawClient.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                  let rawStream = rawClient.GetStream()
                  let! greeting = readPacketAsync rawStream

                  match greeting with
                  | None -> failtest "the server sends its greeting"
                  | Some greeting ->
                      let reader = Reader(greeting.Payload)
                      reader.ReadByte() |> ignore
                      reader.ReadNullTerminatedString() |> ignore
                      reader.ReadInt32LE() |> ignore
                      reader.ReadBytes 8 |> ignore
                      reader.ReadByte() |> ignore
                      let low = uint32 (reader.ReadInt16LE())
                      reader.ReadByte() |> ignore
                      reader.ReadInt16LE() |> ignore
                      let high = uint32 (reader.ReadInt16LE())
                      let capabilities = low ||| (high <<< 16)
                      Expect.isTrue (capabilities &&& ClientSsl <> 0u) "the greeting advertises CLIENT_SSL"

                      let request =
                          let writer = Writer()
                          writer.WriteInt32LE(int (ClientProtocol41 ||| ClientSsl ||| ClientSecureConnection))
                          writer.WriteInt32LE 16777216
                          writer.WriteByte 45uy
                          writer.WriteBytes(Array.zeroCreate<byte> 23)
                          writer.ToArray()

                      do! writePacketAsync rawStream { SeqId = greeting.SeqId + 1uy; Payload = request } |> Async.Ignore

                      use secured = new SslStream(rawStream, false, fun _ _ _ _ -> true)
                      let authentication = SslClientAuthenticationOptions()
                      authentication.TargetHost <- "localhost"
                      authentication.EnabledSslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
                      do! secured.AuthenticateAsClientAsync(authentication) |> Async.AwaitTask

                      let response =
                          passwordlessHandshakeResponse (ClientProtocol41 ||| ClientSsl ||| ClientSecureConnection) "root"

                      do! writePacketAsync secured { SeqId = greeting.SeqId + 2uy; Payload = response } |> Async.Ignore
                      let! authenticated = readPacketAsync secured

                      match authenticated with
                      | Some packet ->
                          Expect.equal packet.SeqId (greeting.SeqId + 3uy) "the encrypted handshake response continues packet sequencing"
                          Expect.equal packet.Payload.[0] 0uy "the encrypted handshake is accepted"
                      | None -> failtest "the server acknowledges the encrypted handshake"

                  let connectionString =
                      sprintf "Server=127.0.0.1;Port=%d;User ID=root;Password=;SslMode=Required;Pooling=false;UseCompression=True" port

                  use connection = new MySqlConnector.MySqlConnection(connectionString)
                  do! connection.OpenAsync() |> Async.AwaitTask
                  use status = connection.CreateCommand()
                  status.CommandText <- "SHOW STATUS LIKE 'Ssl_%'"
                  use! rows = status.ExecuteReaderAsync() |> Async.AwaitTask
                  let mutable values = []

                  while rows.Read() do
                      values <- (rows.GetString(0), rows.GetString(1)) :: values

                  Expect.equal values.Length 2 "both TLS session status values are present"
                  Expect.all values (fun (_, value) -> not (String.IsNullOrWhiteSpace value)) "TLS status values are populated"
              }
              |> Async.RunSynchronously

          testCase "require_secure_transport rejects a plaintext handshake with 3159"
          <| fun _ ->
              async {
                  use certificate = selfSignedCertificate ()
                  let options =
                      Fsdb.ServerOptions.defaults
                      |> Fsdb.ServerOptions.withCertificate certificate
                      |> Fsdb.ServerOptions.requireSecureTransport

                  use server = TestSupport.ServerFixture.startWithOptions options (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  use client = new Net.Sockets.TcpClient()
                  do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                  let stream = client.GetStream()
                  let! greeting = readPacketAsync stream

                  match greeting with
                  | None -> failtest "the server sends its greeting"
                  | Some greeting ->
                      do!
                          writePacketAsync
                              stream
                              { SeqId = greeting.SeqId + 1uy
                                Payload = passwordlessHandshakeResponse ClientProtocol41 "root" }
                          |> Async.Ignore

                      let! rejected = readPacketAsync stream

                      match rejected with
                      | Some packet ->
                          Expect.equal packet.SeqId (greeting.SeqId + 2uy) "the plaintext rejection follows the handshake response"
                          Expect.equal packet.Payload.[0] 0xffuy "the plaintext handshake receives ERR"
                          Expect.equal (Reader(packet.Payload.[1..]).ReadInt16LE()) 3159 "ER_SECURE_TRANSPORT_REQUIRED"
                      | None -> failtest "the server returns ER_SECURE_TRANSPORT_REQUIRED"
              }
              |> Async.RunSynchronously

          testCase "an embedding certificate needs a private key"
          <| fun _ ->
              use certificate = selfSignedCertificate ()
              use publicCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert))
              let options = Fsdb.ServerOptions.defaults |> Fsdb.ServerOptions.withCertificate publicCertificate

              Expect.throwsT<ArgumentException>
                  (fun () -> TestSupport.ServerFixture.startWithOptions options (Fsdb.Storage.create ()) Fsdb.Functions.empty |> ignore)
                  "a certificate without a private key cannot start TLS"

          testCase "REQUIRE SSL accounts reject plaintext authentication"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = Fsdb.Session.create 1 store
                  let _, created = Fsdb.QueryHandler.handle root "CREATE USER 'secure_user'@'%' REQUIRE SSL"
                  Expect.equal created (Affected 0UL) "secure account created"

                  use certificate = selfSignedCertificate ()
                  let options = Fsdb.ServerOptions.defaults |> Fsdb.ServerOptions.withCertificate certificate
                  use server = TestSupport.ServerFixture.startWithOptions options store Fsdb.Functions.empty
                  let port = server.Port

                  use client = new Net.Sockets.TcpClient()
                  do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                  let stream = client.GetStream()
                  let! greeting = readPacketAsync stream
                  let greeting = greeting |> Option.defaultWith (fun () -> failtest "the server sends its greeting")

                  do!
                      writePacketAsync
                          stream
                          { SeqId = greeting.SeqId + 1uy
                            Payload = passwordlessHandshakeResponse ClientProtocol41 "secure_user" }
                      |> Async.Ignore

                  let! rejected = readPacketAsync stream
                  let rejected = rejected |> Option.defaultWith (fun () -> failtest "the server rejects plaintext authentication")
                  Expect.equal rejected.Payload.[0] 0xffuy "plaintext handshake receives ERR"
                  Expect.equal (Reader(rejected.Payload.[1..]).ReadInt16LE()) 1045 "plaintext account denial"

                  let connectionString =
                      sprintf "Server=127.0.0.1;Port=%d;User ID=secure_user;Password=;SslMode=Required;Pooling=false" port

                  use connection = new MySqlConnector.MySqlConnection(connectionString)
                  do! connection.OpenAsync() |> Async.AwaitTask
                  use query = connection.CreateCommand()
                  query.CommandText <- "SELECT CURRENT_USER()"
                  let! identity = query.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string identity) "secure_user@%" "TLS account connects"
              }
              |> Async.RunSynchronously

          testCase "REQUIRE X509 accepts only client certificates issued by a trusted CA"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = Fsdb.Session.create 1 store
                  let _, created = Fsdb.QueryHandler.handle root "CREATE USER 'x509_user'@'%' REQUIRE X509"
                  Expect.equal created (Affected 0UL) "X509 account created"

                  use serverCertificate = selfSignedCertificate ()
                  use authority = certificateAuthority ()
                  use acceptedCertificate = clientCertificate authority
                  use untrustedAuthority = certificateAuthority ()
                  use untrustedCertificate = clientCertificate untrustedAuthority

                  let options =
                      Fsdb.ServerOptions.defaults
                      |> Fsdb.ServerOptions.withCertificate serverCertificate
                      |> Fsdb.ServerOptions.withClientCertificateAuthority authority

                  use server = TestSupport.ServerFixture.startWithOptions options store Fsdb.Functions.empty

                  let! acceptedClient, acceptedStream, acceptedSequence = connectTls server.Port (Some acceptedCertificate)
                  use acceptedClient = acceptedClient
                  use acceptedStream = acceptedStream
                  let capabilities = ClientProtocol41 ||| ClientSsl ||| ClientSecureConnection

                  do!
                      writePacketAsync
                          acceptedStream
                          { SeqId = acceptedSequence + 2uy
                            Payload = passwordlessHandshakeResponse capabilities "x509_user" }
                      |> Async.Ignore

                  let! accepted = readPacketAsync acceptedStream
                  Expect.equal accepted.Value.Payload.[0] 0uy "trusted client certificate authenticates"

                  let! ordinaryClient, ordinaryStream, ordinarySequence = connectTls server.Port None
                  use ordinaryClient = ordinaryClient
                  use ordinaryStream = ordinaryStream

                  do!
                      writePacketAsync
                          ordinaryStream
                          { SeqId = ordinarySequence + 2uy
                            Payload = passwordlessHandshakeResponse capabilities "root" }
                      |> Async.Ignore

                  let! ordinary = readPacketAsync ordinaryStream
                  Expect.equal ordinary.Value.Payload.[0] 0uy "ordinary TLS accounts do not need a client certificate"

                  let! missingClient, missingStream, missingSequence = connectTls server.Port None
                  use missingClient = missingClient
                  use missingStream = missingStream

                  do!
                      writePacketAsync
                          missingStream
                          { SeqId = missingSequence + 2uy
                            Payload = passwordlessHandshakeResponse capabilities "x509_user" }
                      |> Async.Ignore

                  let! rejected = readPacketAsync missingStream
                  Expect.equal rejected.Value.Payload.[0] 0xffuy "missing client certificate is rejected"
                  Expect.equal (Reader(rejected.Value.Payload.[1..]).ReadInt16LE()) 1045 "account denial uses access denied"

                  let! untrusted = connectTls server.Port (Some untrustedCertificate) |> Async.Catch

                  match untrusted with
                  | Choice2Of2 (:? AuthenticationException)
                  | Choice2Of2 (:? IO.IOException) -> ()
                  | Choice2Of2 error -> failtestf "unexpected TLS rejection: %A" error
                  | Choice1Of2(client, stream, sequence) ->
                      use client = client
                      use stream = stream

                      let! response =
                          async {
                              do!
                                  writePacketAsync
                                      stream
                                      { SeqId = sequence + 2uy
                                        Payload = passwordlessHandshakeResponse capabilities "x509_user" }
                                  |> Async.Ignore

                              return! readPacketAsync stream
                          }
                          |> Async.Catch

                      match response with
                      | Choice1Of2 None
                      | Choice2Of2 _ -> ()
                      | Choice1Of2(Some packet) -> failtestf "untrusted TLS returned packet %A" packet.Payload
              }
              |> Async.RunSynchronously

          testCase "expired-password capability enters the password-reset sandbox"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = Fsdb.Session.create 1 store
                  let _, created = Fsdb.QueryHandler.handle root "CREATE USER expired_wire PASSWORD EXPIRE"
                  Expect.equal created (Affected 0UL) "expired account created"
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty

                  let connect capabilities =
                      async {
                          let client = new Net.Sockets.TcpClient()
                          do! client.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                          let stream = client.GetStream()
                          let! greeting = readPacketAsync stream

                          do!
                              writePacketAsync
                                  stream
                                  { SeqId = greeting.Value.SeqId + 1uy
                                    Payload = passwordlessHandshakeResponse capabilities "expired_wire" }
                              |> Async.Ignore

                          let! response = readPacketAsync stream
                          return client, (stream :> IO.Stream), response.Value
                      }

                  let! deniedClient, _, denied = connect ClientProtocol41
                  use deniedClient = deniedClient
                  Expect.equal denied.Payload.[0] 0xffuy "incapable client is rejected"
                  Expect.equal (Reader(denied.Payload.[1..]).ReadInt16LE()) 1862 "expired-password handshake error"

                  let capabilities = ClientProtocol41 ||| ClientCanHandleExpiredPasswords
                  let! capableClient, stream, accepted = connect capabilities
                  use capableClient = capableClient
                  Expect.equal accepted.Payload.[0] 0uy "capable client authenticates"

                  let query (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! response = readPacketAsync stream
                          return response.Value
                      }

                  let! restricted = query "SELECT 1"
                  Expect.equal restricted.Payload.[0] 0xffuy "ordinary statement is sandboxed"
                  Expect.equal (Reader(restricted.Payload.[1..]).ReadInt16LE()) 1820 "sandbox error"

                  let! reset = query "ALTER USER USER() IDENTIFIED BY 'new-secret'"
                  Expect.equal reset.Payload.[0] 0uy "own password reset succeeds"
                  let! selected = query "SELECT 1"
                  Expect.equal selected.Payload.[0] 1uy "ordinary result starts after reset"
              }
              |> Async.RunSynchronously

          testCase "connection resource limits are enforced by the handshake"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = Fsdb.Session.create 1 store
                  let root, _ = Fsdb.QueryHandler.handle root "CREATE USER hourly_wire WITH MAX_CONNECTIONS_PER_HOUR 1"
                  let _, _ = Fsdb.QueryHandler.handle root "CREATE USER active_wire WITH MAX_USER_CONNECTIONS 1"
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty

                  let connect username =
                      async {
                          let client = new Net.Sockets.TcpClient()
                          do! client.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                          let stream = client.GetStream()
                          let! greeting = readPacketAsync stream

                          do!
                              writePacketAsync
                                  stream
                                  { SeqId = greeting.Value.SeqId + 1uy
                                    Payload = passwordlessHandshakeResponse ClientProtocol41 username }
                              |> Async.Ignore

                          let! response = readPacketAsync stream
                          return client, (stream :> IO.Stream), response.Value
                      }

                  let! hourlyFirst, _, accepted = connect "hourly_wire"
                  Expect.equal accepted.Payload.[0] 0uy "first hourly connection"
                  hourlyFirst.Dispose()

                  let! hourlySecond, _, rejected = connect "hourly_wire"
                  use hourlySecond = hourlySecond
                  Expect.equal rejected.Payload.[0] 0xffuy "second hourly connection rejected"
                  Expect.equal (Reader(rejected.Payload.[1..]).ReadInt16LE()) 1226 "hourly resource error"

                  let! activeFirst, activeStream, activeAccepted = connect "active_wire"
                  Expect.equal activeAccepted.Payload.[0] 0uy "first active connection"
                  let! activeSecond, _, activeRejected = connect "active_wire"
                  use activeSecond = activeSecond
                  Expect.equal activeRejected.Payload.[0] 0xffuy "second active connection rejected"
                  Expect.equal (Reader(activeRejected.Payload.[1..]).ReadInt16LE()) 1226 "active resource error"
                  do! writePacketAsync activeStream { SeqId = 0uy; Payload = [| 0x01uy |] } |> Async.Ignore
                  activeFirst.Dispose()

                  let rec reconnect attempts =
                      async {
                          let! client, stream, response = connect "active_wire"

                          if response.Payload.[0] = 0uy || attempts = 0 then
                              return client, response
                          else
                              client.Dispose()
                              do! Async.Sleep 10
                              return! reconnect (attempts - 1)
                      }

                  let! activeThird, activeReopened = reconnect 20
                  use activeThird = activeThird
                  Expect.equal activeReopened.Payload.[0] 0uy "released active slot can reconnect"
              }
              |> Async.RunSynchronously

          testCase "PROCESSLIST shows the live connection and KILL CONNECTION tears a victim down"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          port

                  use victim = new MySqlConnector.MySqlConnection(connStr)
                  do! victim.OpenAsync() |> Async.AwaitTask

                  use killer = new MySqlConnector.MySqlConnection(connStr)
                  do! killer.OpenAsync() |> Async.AwaitTask

                  use victimId = victim.CreateCommand()
                  victimId.CommandText <- "SELECT CONNECTION_ID()"
                  let! victimIdValue = victimId.ExecuteScalarAsync() |> Async.AwaitTask

                  use killerId = killer.CreateCommand()
                  killerId.CommandText <- "SELECT CONNECTION_ID()"
                  let! killerIdValue = killerId.ExecuteScalarAsync() |> Async.AwaitTask

                  // Both live connections appear with the handshake's
                  // real user — filtered by id, since concurrently
                  // running tests register their own connections in the
                  // same process-wide registry.
                  use plist = killer.CreateCommand()
                  plist.CommandText <- "SELECT USER, ID FROM information_schema.processlist ORDER BY ID"
                  use! reader = plist.ExecuteReaderAsync() |> Async.AwaitTask
                  let mutable rows = []

                  while reader.Read() do
                      rows <- (reader.GetString 0, reader.GetInt64 1) :: rows

                  do! reader.CloseAsync() |> Async.AwaitTask
                  let ours = rows |> List.filter (fun (_, id) -> string id = string victimIdValue || string id = string killerIdValue)
                  Expect.equal ours.Length 2 "both connections listed"
                  Expect.all ours (fun (user, _) -> user = "root") "the handshake user, not a stub"

                  // The wire identity is one story: CURRENT_USER(),
                  // SHOW GRANTS, and PROCESSLIST all report the
                  // handshake user.
                  use whoami = killer.CreateCommand()
                  whoami.CommandText <- "SELECT CURRENT_USER()"
                  let! whoamiValue = whoami.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string whoamiValue) "root@%" "CURRENT_USER() reports the handshake user"

                  use kill = killer.CreateCommand()
                  kill.CommandText <- sprintf "KILL CONNECTION %s" (string victimIdValue)
                  let! _ = kill.ExecuteNonQueryAsync() |> Async.AwaitTask

                  // The victim's next statement must fail — its socket is gone.
                  use dead = victim.CreateCommand()
                  dead.CommandText <- "SELECT 1"

                  let! threw =
                      async {
                          try
                              let! _ = dead.ExecuteScalarAsync() |> Async.AwaitTask
                              return false
                          with _ ->
                              return true
                      }

                  Expect.isTrue threw "the killed connection is unusable"

                  // The killer itself is untouched, and KILL of a gone id is 1094.
                  use alive = killer.CreateCommand()
                  alive.CommandText <- "SELECT 1"
                  let! one = alive.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string one) "1" "the killer connection survives"
              }
              |> Async.RunSynchronously

          testCase "the handshake username reaches USER()/CURRENT_USER() over the real wire protocol"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()

                  Fsdb.Auth.createUser store "alice" "%" None |> Result.mapError snd |> Result.defaultWith failtest
                  Fsdb.Auth.createUser store "alice" "127.0.0.1" None |> Result.mapError snd |> Result.defaultWith failtest

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=alice;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- "SELECT USER()"
                  let! user = cmd.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string user) "alice@localhost" "USER() reports the handshake username"

                  use cmd2 = conn.CreateCommand()
                  cmd2.CommandText <- "SELECT CURRENT_USER"
                  let! current = cmd2.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string current) "alice@127.0.0.1" "the exact peer-address account is selected"

                  do! conn.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          testCase "default roles activate during authentication"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = create 1 store
                  let root, _ = handle root "CREATE DATABASE role_db"
                  let root, _ = handle root "CREATE TABLE role_db.documents (id INT PRIMARY KEY)"
                  let root, _ = handle root "INSERT INTO role_db.documents VALUES (1)"
                  let root, _ = handle root "CREATE ROLE reader"
                  let root, _ = handle root "CREATE USER role_user"
                  let root, _ = handle root "GRANT SELECT ON role_db.* TO reader"
                  let root, _ = handle root "GRANT reader TO role_user"
                  let _, _ = handle root "SET DEFAULT ROLE reader TO role_user"

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty

                  let connectionString =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=role_user;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          server.Port

                  use connection = new MySqlConnector.MySqlConnection(connectionString)
                  do! connection.OpenAsync() |> Async.AwaitTask

                  use command = connection.CreateCommand()
                  command.CommandText <- "SELECT CURRENT_ROLE(), id FROM role_db.documents"
                  use! reader = command.ExecuteReaderAsync() |> Async.AwaitTask
                  Expect.isTrue (reader.Read()) "one document"
                  Expect.equal (reader.GetString 0) "`reader`@`%`" "default role"
                  Expect.equal (reader.GetInt32 1) 1 "inherited SELECT privilege"
              }
              |> Async.RunSynchronously

          testCase "activate_all_roles_on_login includes direct and mandatory roles"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  let root = create 1 store
                  let root, _ = handle root "CREATE DATABASE login_roles"
                  let root, _ = handle root "CREATE TABLE login_roles.documents (id INT PRIMARY KEY)"
                  let root, _ = handle root "INSERT INTO login_roles.documents VALUES (1)"
                  let root, _ = handle root "CREATE ROLE inherited_reader, mandatory_parent, optional_reader"
                  let root, _ = handle root "CREATE USER role_user"
                  let root, _ = handle root "GRANT SELECT ON login_roles.* TO inherited_reader"
                  let root, _ = handle root "GRANT inherited_reader TO mandatory_parent"
                  let root, _ = handle root "GRANT optional_reader TO role_user"
                  let root, _ = handle root "SET GLOBAL mandatory_roles = 'mandatory_parent@%'"
                  let _, _ = handle root "SET GLOBAL activate_all_roles_on_login = ON"

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty

                  let connectionString =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=role_user;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          server.Port

                  use connection = new MySqlConnector.MySqlConnection(connectionString)
                  do! connection.OpenAsync() |> Async.AwaitTask

                  use command = connection.CreateCommand()
                  command.CommandText <- "SELECT CURRENT_ROLE()"
                  let! currentRoles = command.ExecuteScalarAsync() |> Async.AwaitTask

                  Expect.equal
                      (string currentRoles)
                      "`mandatory_parent`@`%`,`optional_reader`@`%`"
                      "all applicable role roots activate"

                  command.CommandText <- "SELECT id FROM login_roles.documents"
                  use! reader = command.ExecuteReaderAsync() |> Async.AwaitTask
                  Expect.isTrue (reader.Read()) "one document"
                  Expect.equal (reader.GetInt32 0) 1 "mandatory role inheritance authorizes the query"
                  do! reader.CloseAsync() |> Async.AwaitTask

                  command.CommandText <- "SET ROLE NONE"
                  let! _ = command.ExecuteNonQueryAsync() |> Async.AwaitTask
                  command.CommandText <- "SELECT id FROM login_roles.documents"

                  let! denied = command.ExecuteScalarAsync() |> Async.AwaitTask |> Async.Catch

                  match denied with
                  | Choice1Of2 _ -> failtest "expected SET ROLE NONE to remove mandatory access"
                  | Choice2Of2 error ->
                      match error.GetBaseException() with
                      | :? MySqlConnector.MySqlException as error ->
                          Expect.equal error.Number 1142 "mandatory roles can be deactivated"
                      | error -> failtestf "expected a MySQL access error, got %A" error
              }
              |> Async.RunSynchronously

          testCase "handshake auth verifies stored passwords: right password connects, wrong password and unknown user get 1045"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()

                  Fsdb.Storage.insertRows
                      store
                      "mysql"
                      "user"
                      (Some [ "Host"; "User"; "plugin"; "authentication_string" ])
                      [ [ Fsdb.Value.VString "%"
                          Fsdb.Value.VString "bob"
                          Fsdb.Value.VString "mysql_native_password"
                          Fsdb.Value.VString(Fsdb.Auth.nativePasswordHash "s3cret") ] ]
                  |> ignore

                  let restrictedDatabase = "restricted_" + Guid.NewGuid().ToString "N"
                  Fsdb.Storage.createDatabase store restrictedDatabase |> Result.mapError string |> Result.defaultWith failtest

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let connStr (user: string) (password: string) =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=%s;Password=%s;AllowPublicKeyRetrieval=True;SslMode=None"
                          port
                          user
                          password

                  // `Async.AwaitTask` can surface the failure wrapped in an
                  // AggregateException, so dig the MySqlException out.
                  let rec mysqlError (e: exn) : MySqlConnector.MySqlException option =
                      match e with
                      | :? MySqlConnector.MySqlException as m -> Some m
                      | :? AggregateException as a -> a.InnerExceptions |> Seq.tryPick mysqlError
                      | _ -> None

                  let expectDenied (cs: string) (label: string) =
                      async {
                          use conn = new MySqlConnector.MySqlConnection(cs)
                          let! result = conn.OpenAsync() |> Async.AwaitTask |> Async.Catch

                          match result with
                          | Choice1Of2() -> failtestf "%s: expected the connection to be denied" label
                          | Choice2Of2 e ->
                              match mysqlError e with
                              | Some m ->
                                  Expect.equal m.ErrorCode MySqlConnector.MySqlErrorCode.AccessDenied (label + ": 1045")
                              | None -> raise e
                      }

                  // Right password connects and reports its identity.
                  use conn = new MySqlConnector.MySqlConnection(connStr "bob" "s3cret")
                  do! conn.OpenAsync() |> Async.AwaitTask
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- "SELECT CURRENT_USER()"
                  let! current = cmd.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string current) "bob@%" "authenticated as bob"
                  do! conn.CloseAsync() |> Async.AwaitTask

                  do! expectDenied (connStr "bob" "wrong") "wrong password"
                  do! expectDenied (connStr "nobody" "") "unknown user"

                  let deniedDatabase = "denied_" + Guid.NewGuid().ToString "N"
                  do! expectDenied (connStr "nobody" "" + ";Database=" + deniedDatabase) "unknown user with database"
                  Expect.isFalse (Fsdb.Storage.databaseExists store deniedDatabase) "authentication precedes catalog mutation"

                  let unprivilegedDatabase = "unprivileged_" + Guid.NewGuid().ToString "N"
                  use unprivileged = new MySqlConnector.MySqlConnection(connStr "bob" "s3cret" + ";Database=" + unprivilegedDatabase)
                  let! unprivilegedResult = unprivileged.OpenAsync() |> Async.AwaitTask |> Async.Catch

                  match unprivilegedResult with
                  | Choice1Of2() -> failtest "expected a missing handshake database to be refused"
                  | Choice2Of2 e ->
                      match mysqlError e with
                      | Some m -> Expect.equal m.Number 1049 "the handshake reports an unknown database"
                      | None -> raise e

                  Expect.isFalse (Fsdb.Storage.databaseExists store unprivilegedDatabase) "denied handshake did not grow the catalog"

                  use restrictedHandshake =
                      new MySqlConnector.MySqlConnection(connStr "bob" "s3cret" + ";Database=" + restrictedDatabase)

                  let! restrictedHandshakeResult = restrictedHandshake.OpenAsync() |> Async.AwaitTask |> Async.Catch

                  match restrictedHandshakeResult with
                  | Choice1Of2() -> failtest "expected an existing but unprivileged handshake database to be refused"
                  | Choice2Of2 error ->
                      match mysqlError error with
                      | Some mysql -> Expect.equal mysql.Number 1044 "the handshake enforces database visibility"
                      | None -> raise error

                  use restrictedCommand = new MySqlConnector.MySqlConnection(connStr "bob" "s3cret")
                  do! restrictedCommand.OpenAsync() |> Async.AwaitTask
                  let! restrictedCommandResult =
                      restrictedCommand.ChangeDatabaseAsync(restrictedDatabase) |> Async.AwaitTask |> Async.Catch

                  match restrictedCommandResult with
                  | Choice1Of2() -> failtest "expected COM_INIT_DB to enforce database visibility"
                  | Choice2Of2 error ->
                      match mysqlError error with
                      | Some mysql -> Expect.equal mysql.Number 1044 "COM_INIT_DB access denial"
                      | None -> raise error

                  Fsdb.Auth.grant store [ "SELECT" ] (Fsdb.Auth.OnDb restrictedDatabase) [ "bob", "%" ] false
                  |> Result.mapError snd
                  |> Result.defaultWith failtest
                  do! restrictedCommand.ChangeDatabaseAsync(restrictedDatabase) |> Async.AwaitTask

                  // A passwordless account matches real MySQL: an empty
                  // offered password connects, a non-empty one is 1045.
                  do! expectDenied (connStr "root" "anything") "passwordless account, offered password"

                  use conn2 = new MySqlConnector.MySqlConnection(connStr "root" "")
                  do! conn2.OpenAsync() |> Async.AwaitTask

                  // Account created over the wire is immediately usable.
                  use create = conn2.CreateCommand()
                  create.CommandText <- "CREATE USER 'carol'@'%' IDENTIFIED BY 'cpw'"
                  let! _ = create.ExecuteNonQueryAsync() |> Async.AwaitTask

                  use createLocked = conn2.CreateCommand()
                  createLocked.CommandText <- "CREATE USER 'locked'@'%' IDENTIFIED BY 'secret' ACCOUNT LOCK"
                  let! _ = createLocked.ExecuteNonQueryAsync() |> Async.AwaitTask
                  do! conn2.CloseAsync() |> Async.AwaitTask

                  use locked = new MySqlConnector.MySqlConnection(connStr "locked" "secret")
                  let! lockedResult = locked.OpenAsync() |> Async.AwaitTask |> Async.Catch

                  match lockedResult with
                  | Choice1Of2() -> failtest "expected the locked account to be denied"
                  | Choice2Of2 error ->
                      match mysqlError error with
                      | Some mysql -> Expect.equal mysql.Number 3118 "locked-account error"
                      | None -> raise error

                  use conn3 = new MySqlConnector.MySqlConnection(connStr "carol" "cpw")
                  do! conn3.OpenAsync() |> Async.AwaitTask
                  use whoami = conn3.CreateCommand()
                  whoami.CommandText <- "SELECT CURRENT_USER()"
                  let! carol = whoami.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string carol) "carol@%" "authenticated as the created account"
                  do! conn3.CloseAsync() |> Async.AwaitTask

                  do! expectDenied (connStr "carol" "nope") "created account, wrong password"

                  // Privilege enforcement over the wire: carol has no
                  // grants, so a SELECT on a real table is 1142.
                  use conn4 = new MySqlConnector.MySqlConnection(connStr "root" "")
                  do! conn4.OpenAsync() |> Async.AwaitTask
                  use ddl = conn4.CreateCommand()
                  ddl.CommandText <- "CREATE TABLE fsdb.things (id INT PRIMARY KEY)"
                  let! _ = ddl.ExecuteNonQueryAsync() |> Async.AwaitTask
                  do! conn4.CloseAsync() |> Async.AwaitTask

                  use conn5 = new MySqlConnector.MySqlConnection(connStr "carol" "cpw")
                  do! conn5.OpenAsync() |> Async.AwaitTask
                  use denied = conn5.CreateCommand()
                  denied.CommandText <- "SELECT * FROM fsdb.things"
                  let! deniedResult = denied.ExecuteScalarAsync() |> Async.AwaitTask |> Async.Catch

                  match deniedResult with
                  | Choice1Of2 _ -> failtest "expected carol's SELECT to be denied"
                  | Choice2Of2 e ->
                      match mysqlError e with
                      | Some m ->
                          Expect.equal
                              m.ErrorCode
                              MySqlConnector.MySqlErrorCode.TableAccessDenied
                              "1142 over the wire"
                      | None -> raise e

                  // The subquery-bypass hole: carol reads `things` through
                  // a scalar subquery — must still be 1142, not allowed.
                  use sub = conn5.CreateCommand()
                  sub.CommandText <- "SELECT (SELECT id FROM fsdb.things)"
                  let! subResult = sub.ExecuteScalarAsync() |> Async.AwaitTask |> Async.Catch

                  match subResult with
                  | Choice1Of2 _ -> failtest "expected the subquery read to be denied"
                  | Choice2Of2 e ->
                      match mysqlError e with
                      | Some m -> Expect.equal m.ErrorCode MySqlConnector.MySqlErrorCode.TableAccessDenied "subquery read is 1142"
                      | None -> raise e

                  // Process visibility: with a root connection live, carol
                  // sees only her own row and can't kill or inspect root.
                  use root = new MySqlConnector.MySqlConnection(connStr "root" "")
                  do! root.OpenAsync() |> Async.AwaitTask
                  use rootId = root.CreateCommand()
                  rootId.CommandText <- "SELECT CONNECTION_ID()"
                  let! rootIdValue = rootId.ExecuteScalarAsync() |> Async.AwaitTask

                  use plist = conn5.CreateCommand()
                  plist.CommandText <- "SELECT DISTINCT USER FROM information_schema.processlist"
                  use! preader = plist.ExecuteReaderAsync() |> Async.AwaitTask
                  let mutable users = []

                  while preader.Read() do
                      users <- preader.GetString 0 :: users

                  do! preader.CloseAsync() |> Async.AwaitTask
                  Expect.equal users [ "carol" ] "carol sees only her own connection"

                  use kill = conn5.CreateCommand()
                  kill.CommandText <- sprintf "KILL %s" (string rootIdValue)
                  let! killResult = kill.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Catch

                  match killResult with
                  | Choice1Of2 _ -> failtest "expected carol's KILL of root to be denied"
                  | Choice2Of2 e ->
                      match mysqlError e with
                      | Some _ -> () // 1094 unknown thread id — root is invisible to carol
                      | None -> raise e

                  use grants = conn5.CreateCommand()
                  grants.CommandText <- "SHOW GRANTS FOR 'root'"
                  let! grantsResult = grants.ExecuteScalarAsync() |> Async.AwaitTask |> Async.Catch

                  match grantsResult with
                  | Choice1Of2 _ -> failtest "expected carol's SHOW GRANTS FOR root to be denied"
                  | Choice2Of2 e ->
                      match mysqlError e with
                      | Some _ -> ()
                      | None -> raise e

                  do! root.CloseAsync() |> Async.AwaitTask
                  do! conn5.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          // mysql CLI sends `SHOW WARNINGS LIMIT n` and mysqli's
          // `mysqli_report`/error-checking idiom sends `SHOW COUNT(*)
          // WARNINGS` — both routinely enough that either one dying with a
          // 1064 breaks real client connect/error-handling flows, not just
          // an edge case.
          testCase "SHOW WARNINGS LIMIT n and SHOW COUNT(*) WARNINGS round-trip over the real wire protocol"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use cmd1 = conn.CreateCommand()
                  cmd1.CommandText <- "SHOW WARNINGS LIMIT 10"
                  use! reader1 = cmd1.ExecuteReaderAsync() |> Async.AwaitTask
                  let! hasRow = reader1.ReadAsync() |> Async.AwaitTask
                  Expect.isFalse hasRow "SHOW WARNINGS LIMIT 10 returns no rows on a clean connection"
                  do! reader1.CloseAsync() |> Async.AwaitTask

                  use cmd2 = conn.CreateCommand()
                  cmd2.CommandText <- "SHOW COUNT(*) WARNINGS"
                  let! count = cmd2.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string count) "0" "SHOW COUNT(*) WARNINGS reports 0"

                  do! conn.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          testCase "a real CRUD round-trip: create table, insert, select, update, delete"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
              }
              |> Async.RunSynchronously

          testCase "BLOB and VARBINARY bytes survive a real MySqlConnector round-trip"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
              }
              |> Async.RunSynchronously

          testCase "MySqlConnector reads BIT columns as unsigned integers"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use create = conn.CreateCommand()
                  create.CommandText <- "CREATE TABLE bits (one BIT, three BIT(3) DEFAULT b'101', eight BIT(8) DEFAULT 0b10101010)"
                  do! create.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                  use insert = conn.CreateCommand()
                  insert.CommandText <- "INSERT INTO bits (one) VALUES (b'1')"
                  do! insert.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                  use select = conn.CreateCommand()
                  select.CommandText <- "SELECT one, three, eight FROM bits"
                  use! reader = select.ExecuteReaderAsync() |> Async.AwaitTask
                  let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue hasRow "bit row present"
                  Expect.equal (reader.GetDataTypeName 0) "BIT" "one type"
                  Expect.equal (reader.GetFieldValue<uint64> 0) 1UL "one value"
                  Expect.equal (reader.GetFieldValue<uint64> 1) 5UL "three value"
                  Expect.equal (reader.GetFieldValue<uint64> 2) 170UL "eight value"
                  do! reader.CloseAsync() |> Async.AwaitTask
                  do! conn.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          testCase "MySqlConnector reads JSON columns as JSON text"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          server.Port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use setup = conn.CreateCommand()
                  setup.CommandText <- "CREATE TABLE documents (payload JSON); INSERT INTO documents VALUES ('{\"ok\":true}')"
                  do! setup.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                  use command = conn.CreateCommand()
                  command.CommandText <- "SELECT payload FROM documents"
                  use! reader = command.ExecuteReaderAsync() |> Async.AwaitTask
                  let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue hasRow "JSON row present"
                  Expect.equal (reader.GetDataTypeName 0) "JSON" "JSON type"
                  Expect.equal (reader.GetString 0) "{\"ok\":true}" "JSON text"
              }
              |> Async.RunSynchronously

          testCase "empty grouped windows retain result metadata"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          server.Port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use setup = conn.CreateCommand()
                  setup.CommandText <- "CREATE TABLE volume_rows (bucket INT, signed_value BIGINT)"
                  do! setup.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                  use command = conn.CreateCommand()
                  command.CommandText <-
                      "SELECT bucket, COUNT(*) AS row_count, ROUND(AVG(signed_value), 4) AS avg_value, "
                      + "SUM(COUNT(*)) OVER (ORDER BY bucket ROWS UNBOUNDED PRECEDING) AS running_rows "
                      + "FROM volume_rows WHERE bucket < 64 GROUP BY bucket ORDER BY bucket"

                  use! reader = command.ExecuteReaderAsync() |> Async.AwaitTask
                  Expect.isFalse reader.HasRows "result is empty"
                  Expect.equal
                      [ for index in 0 .. reader.FieldCount - 1 -> reader.GetDataTypeName index ]
                      [ "INT"; "BIGINT"; "DECIMAL"; "DECIMAL" ]
                      "static result types"
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
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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

                  // Native numeric descriptors make ADO.NET return numeric
                  // values while VARCHAR remains a string.
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

                  use limited = conn.CreateCommand()
                  limited.CommandText <- "SELECT id FROM ps_int ORDER BY id LIMIT @count OFFSET @offset"
                  limited.Parameters.AddWithValue("@count", 1) |> ignore
                  limited.Parameters.AddWithValue("@offset", 1) |> ignore
                  do! limited.PrepareAsync() |> Async.AwaitTask
                  use! limitedReader = limited.ExecuteReaderAsync() |> Async.AwaitTask
                  let! hasLimitedRow = limitedReader.ReadAsync() |> Async.AwaitTask
                  Expect.isTrue hasLimitedRow "prepared LIMIT returns a row"
                  Expect.equal (limitedReader.GetInt64 0) 2L "prepared OFFSET skips the first row"
                  let! hasExtraRow = limitedReader.ReadAsync() |> Async.AwaitTask
                  Expect.isFalse hasExtraRow "prepared LIMIT caps the result"
                  do! limitedReader.CloseAsync() |> Async.AwaitTask
                  do! conn.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          testCase "disconnect rolls back a prepared transaction without blocking later writers"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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

                  // Exercise COM_STMT_EXECUTE, then drop the client without
                  // COMMIT or ROLLBACK.
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

                  // The next transaction must remain usable after disconnect
                  // cleanup discards the abandoned snapshot.
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
              }
              |> Async.RunSynchronously

          testCase "a table with an index and a foreign key is visible through information_schema and SHOW CREATE TABLE"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;Database=shop;AllowPublicKeyRetrieval=True;SslMode=None"
                          port

                  Fsdb.Storage.ensureDatabase store "shop"

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

                  let! dbName = scalar "SELECT DATABASE()"
                  Expect.equal (string dbName) "shop" "the handshake selected the existing database"

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
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
                  let! stmtId, _, _ = readPreparedReply stream

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
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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

                  let! _ = query "CREATE TABLE t (n INT, KEY ix_n(n))"
                  let! _ = readPacketAsync stream

                  // COM_FIELD_LIST: column defs + EOF for an existing
                  // table, ERR 1146 for a missing one.
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x04uy |] (Array.append (Text.Encoding.UTF8.GetBytes "t") [| 0uy |]) }
                  let! fieldList = readPacketAsync stream
                  Expect.isTrue fieldList.IsSome "COM_FIELD_LIST answers"
                  Expect.equal fieldList.Value.Payload.[0] 0x03uy "existing table: first packet is a column definition"
                  let field = Reader(fieldList.Value.Payload)
                  Expect.equal (field.ReadLenEncString()) (Some "def") "field catalog"
                  Expect.equal (field.ReadLenEncString()) (Some "app") "field schema"
                  Expect.equal (field.ReadLenEncString()) (Some "t") "field table"
                  Expect.equal (field.ReadLenEncString()) (Some "t") "field physical table"
                  Expect.equal (field.ReadLenEncString()) (Some "n") "field name"
                  Expect.equal (field.ReadLenEncString()) (Some "n") "field physical name"
                  Expect.equal (field.ReadByte()) 0x0cuy "fixed metadata length"
                  field.ReadInt16LE() |> ignore
                  field.ReadInt32LE() |> ignore
                  field.ReadByte() |> ignore
                  let fieldFlags = uint16 (field.ReadInt16LE())
                  Expect.isTrue (fieldFlags &&& MultipleKeyFlag <> 0us) "COM_FIELD_LIST carries secondary-key membership"
                  Expect.isTrue (fieldFlags &&& PartKeyFlag <> 0us) "COM_FIELD_LIST carries key-part membership"
                  let! _ = readPacketAsync stream // trailing EOF

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x04uy |] (Array.append (Text.Encoding.UTF8.GetBytes "ghost") [| 0uy |]) }
                  let! fieldListErr = readPacketAsync stream
                  Expect.equal fieldListErr.Value.Payload.[0] 0xffuy "missing table: ERR"
                  Expect.equal (Reader(fieldListErr.Value.Payload.[1..]).ReadInt16LE()) 1146 "1146 table not found"

                  // Prepare "SELECT ?".
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT ?") }
                  let! stmtId, _, _ = readPreparedReply stream

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
              }
              |> Async.RunSynchronously

          TestSupport.processGlobalCase "SHOW STATUS counts prepared-protocol commands"
          <| fun _ ->
              async {
                  Fsdb.InformationSchema.resetCommandCounts ()
                  let store = Fsdb.Storage.create ()
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let prepare = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = prepare } |> Async.Ignore
                  let! stmtId, _, _ = readPreparedReply stream

                  let execute = Writer()
                  execute.WriteByte 0x17uy
                  execute.WriteInt32LE stmtId
                  execute.WriteByte 0uy
                  execute.WriteInt32LE 1
                  do! writePacketAsync stream { SeqId = 0uy; Payload = execute.ToArray() } |> Async.Ignore

                  for _ in 1..5 do
                      let! packet = readPacketAsync stream
                      Expect.isTrue packet.IsSome "the prepared SELECT response is complete"

                  let close = Writer()
                  close.WriteByte 0x19uy
                  close.WriteInt32LE stmtId
                  do! writePacketAsync stream { SeqId = 0uy; Payload = close.ToArray() } |> Async.Ignore
                  do! writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0euy |] } |> Async.Ignore
                  let! ping = readPacketAsync stream
                  Expect.isTrue ping.IsSome "the ping orders the preceding close"

                  let session = create 1 store

                  for name in [ "Com_stmt_prepare"; "Com_stmt_execute"; "Com_stmt_close" ] do
                      match handle session (sprintf "SHOW GLOBAL STATUS LIKE '%s'" name) |> snd with
                      | ResultSet(_, [ [ Some actual; Some "1" ] ]) when actual = name -> ()
                      | other -> failtestf "expected %s to equal one, got %A" name other
              }
              |> Async.RunSynchronously

          // A truncated COM_STMT_CLOSE (needs a 4-byte statement id, gets
          // none) must answer ERR and leave the connection usable — a
          // `Reader` throw inside `parseCommand` must not escape the
          // command loop and drop the socket with no reply.
          testCase "a malformed short command packet gets an ERR, not a dropped connection"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
              }
              |> Async.RunSynchronously

          // COM_RESET_CONNECTION (0x1f): connection pools (PDO's persistent
          // connections, Doctrine's DBAL, ...) send this instead of a full
          // reconnect to clear session state — it must answer OK, not
          // ERR 1047 `Unsupported`.
          testCase "COM_RESET_CONNECTION replies OK and clears session state"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_SEND_LONG_DATA preserves non-UTF-8 BLOB and string parameters"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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

                  let! _ = query "CREATE TABLE blobs (b BLOB, id BINARY(16))"
                  let! _ = readPacketAsync stream

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "INSERT INTO blobs VALUES (?, ?)") }
                  let! stmtId, _, _ = readPreparedReply stream

                  let bytes = [| 0uy; 0xffuy; 0x80uy; 65uy; 66uy; 0xC0uy |]
                  let id = [| 0x01uy; 0xffuy; 0x3buy; 0x80uy; 0x66uy; 0x31uy; 0x72uy; 0x31uy; 0xaauy; 0xbbuy; 0x42uy; 0x45uy; 0xccuy; 0xdduy; 0xeeuy; 0x6cuy |]

                  let longDataPayload parameter value =
                      let w = Writer()
                      w.WriteByte 0x18uy
                      w.WriteInt32LE stmtId
                      w.WriteInt16LE parameter
                      w.WriteBytes value
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = longDataPayload 0 bytes }
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = longDataPayload 1 id }

                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE stmtId
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.WriteByte 0uy // null bitmap: params not NULL
                      w.WriteByte 1uy // new-params-bound
                      w.WriteByte TypeBlob
                      w.WriteByte 0uy // not unsigned
                      w.WriteByte TypeVarString
                      w.WriteByte 0uy
                      w.WriteLenEncString "" // placeholder values; long data wins
                      w.WriteLenEncString "" // placeholder value; long data wins
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! _ = readPacketAsync stream // OK (INSERT)

                  let! _ = query "SELECT b, id FROM blobs"
                  let! _ = readPacketAsync stream // column count
                  let! _ = readPacketAsync stream // column def
                  let! _ = readPacketAsync stream // column def
                  let! _ = readPacketAsync stream // EOF
                  let! row = readPacketAsync stream

                  let r = Reader(row.Value.Payload)

                  match r.ReadLenEncInt() with
                  | Some len -> Expect.equal (r.ReadBytes(int len)) bytes "BLOB bytes"
                  | None -> failtest "expected a non-NULL blob value"

                  match r.ReadLenEncInt() with
                  | Some len -> Expect.equal (r.ReadBytes(int len)) id "binary string bytes"
                  | None -> failtest "expected a non-NULL binary string value"

                  let! _ = readPacketAsync stream // EOF

                  let queryId = [| 0x02uy; 0xfeuy; 0x03uy; 0xfduy; 0x04uy; 0xfcuy; 0x05uy; 0xfbuy; 0x06uy; 0xfauy; 0x07uy; 0xf9uy; 0x08uy; 0xf8uy; 0x09uy; 0xf7uy |]

                  let rawQuery =
                      Array.concat
                          [ [| 0x03uy |]
                            Text.Encoding.ASCII.GetBytes "INSERT INTO blobs VALUES (0, '"
                            queryId
                            Text.Encoding.ASCII.GetBytes "')" ]

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = rawQuery }
                  let! _ = readPacketAsync stream // OK
                  let! _ = query "SELECT id FROM blobs WHERE b = X'30'"
                  let! _ = readPacketAsync stream // column count
                  let! _ = readPacketAsync stream // column def
                  let! _ = readPacketAsync stream // EOF
                  let! rawRow = readPacketAsync stream
                  let rawReader = Reader(rawRow.Value.Payload)

                  match rawReader.ReadLenEncInt() with
                  | Some len -> Expect.equal (rawReader.ReadBytes(int len)) queryId "COM_QUERY binary string bytes"
                  | None -> failtest "expected a non-NULL COM_QUERY binary string value"
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

                  use server = TestSupport.ServerFixture.start db.Store db.Functions
                  let port = server.Port

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

                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) registry
                  let port = server.Port

                  // Pooling would let the health check reuse the setup socket
                  // instead of proving that a fresh connection is accepted.
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
              }
              |> Async.RunSynchronously

          testCase "INNODB_TRX exposes the first active table statement over the wire"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let connectionString =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          server.Port

                  use writer = new MySqlConnector.MySqlConnection(connectionString)
                  use observer = new MySqlConnector.MySqlConnection(connectionString)
                  do! writer.OpenAsync() |> Async.AwaitTask
                  do! observer.OpenAsync() |> Async.AwaitTask

                  let execute sql =
                      async {
                          use command = writer.CreateCommand()
                          command.CommandText <- sql
                          let! _ = command.ExecuteNonQueryAsync() |> Async.AwaitTask
                          return ()
                      }

                  do! execute "CREATE TABLE wire_transaction_probe (id INT PRIMARY KEY)"
                  do! execute "INSERT INTO wire_transaction_probe VALUES (1)"

                  use connectionIdCommand = writer.CreateCommand()
                  connectionIdCommand.CommandText <- "SELECT CONNECTION_ID()"
                  let! writerConnectionId = connectionIdCommand.ExecuteScalarAsync() |> Async.AwaitTask
                  do! execute "START TRANSACTION"

                  use slow = writer.CreateCommand()
                  slow.CommandText <- "SELECT SLEEP(2), id FROM wire_transaction_probe"
                  let slowResult = slow.ExecuteScalarAsync()

                  let rec findQuery attempts =
                      async {
                          use probe = observer.CreateCommand()

                          probe.CommandText <-
                              sprintf
                                  "SELECT trx_query FROM information_schema.innodb_trx WHERE trx_mysql_thread_id=%s"
                                  (string writerConnectionId)

                          let! value = probe.ExecuteScalarAsync() |> Async.AwaitTask

                          if isNull value && attempts > 0 then
                              do! Async.Sleep 25
                              return! findQuery (attempts - 1)
                          else
                              return Option.ofObj value |> Option.map string
                      }

                  let! activeQuery = findQuery 40

                  Expect.equal
                      activeQuery
                      (Some "SELECT SLEEP(?), id FROM wire_transaction_probe")
                      "the running transaction exposes its current statement"

                  let! _ = slowResult |> Async.AwaitTask
                  do! execute "COMMIT"

                  use afterCommit = observer.CreateCommand()

                  afterCommit.CommandText <-
                      sprintf
                          "SELECT trx_id FROM information_schema.innodb_trx WHERE trx_mysql_thread_id=%s"
                          (string writerConnectionId)

                  let! closed = afterCommit.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.isNull closed "commit removes the transaction row"
              }
              |> Async.RunSynchronously

          // A batch UPDATE whose WHERE calls a slow registered function
          // runs its per-row predicate inside `Storage.updateRows`'s fold
          // — which, unlike the SELECT row pipeline's `traverse`, had no
          // cancellation check until `foldWithCancellation`. The slow call
          // sits in the WHERE (matching zero rows) rather than the SET
          // deliberately: a SET-side slow function is also caught by
          // `coerceRow`'s `traverse` on each rewritten row, so only a
          // never-matching WHERE pins `foldWithCancellation` itself as the
          // thing that unwinds the scan. MySqlConnector's `Cancel()` is
          // the client-side shape of the trigger: it opens a side
          // connection and issues `KILL QUERY <id>`, which flips the
          // victim's `queryCancellation` token. The fold must unwind
          // all-or-nothing: the statement-local builder is discarded, so
          // the table shows no partial rewrite.
          testCase "MySqlCommand.Cancel mid-UPDATE over a slow function unwinds with the table unmodified"
          <| fun _ ->
              async {
                  let mutable slowCalls = 0L

                  let slow =
                      function
                      | [ VInt i ] ->
                          System.Threading.Interlocked.Increment(&slowCalls) |> ignore
                          System.Threading.Thread.Sleep 5
                          VInt(i + 1L)
                      | _ -> VNull // the all-NULL probe row's type check

                  let registry = Fsdb.Functions.empty |> Fsdb.Functions.registerScalar "SLOWFN" slow

                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) registry
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          port

                  use setup = new MySqlConnector.MySqlConnection(connStr)
                  do! setup.OpenAsync() |> Async.AwaitTask

                  let exec (sql: string) =
                      async {
                          use cmd = setup.CreateCommand()
                          cmd.CommandText <- sql
                          return! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
                      }

                  // 2000 rows at ~5ms of SLOWFN each is ~10s of honest
                  // work — far past this test's cancel point, so a
                  // broken cancellation path can't pass by the UPDATE
                  // simply finishing; and well past `Storage.
                  // cancellationCheckInterval` (256), so the periodic
                  // check actually gets a turn after the cancel lands.
                  let rowCount = 2000
                  do! exec "CREATE TABLE upd_cancel (id INT PRIMARY KEY, v INT)"

                  do!
                      exec (
                          sprintf
                              "INSERT INTO upd_cancel VALUES %s"
                              (String.Join(",", [ for i in 1..rowCount -> sprintf "(%d, 0)" i ]))
                      )

                  use victim = new MySqlConnector.MySqlConnection(connStr)
                  do! victim.OpenAsync() |> Async.AwaitTask
                  use updCmd = victim.CreateCommand()
                  // SLOWFN(v) is v+1, never -1: zero rows match, so the
                  // updater/coerceRow path never runs and only the
                  // fold's own cancellation check can stop the scan.
                  updCmd.CommandText <- "UPDATE upd_cancel SET v = 1 WHERE SLOWFN(v) = -1"
                  let updTask = updCmd.ExecuteNonQueryAsync()

                  // Let the fold get properly underway before cancelling.
                  let mutable spins = 0

                  while System.Threading.Interlocked.Read(&slowCalls) < 10L && spins < 100 do
                      do! Async.Sleep 50
                      spins <- spins + 1

                  Expect.isTrue (System.Threading.Interlocked.Read(&slowCalls) >= 10L) "the UPDATE was actually underway before Cancel"
                  updCmd.Cancel()

                  // The cancelled statement surfaces as a client-side
                  // exception (fsdb ends the victim's command loop on
                  // cancellation rather than replying 1317) — either
                  // way it must not report success.
                  let! threw =
                      async {
                          try
                              let! _ = updTask |> Async.AwaitTask
                              return false
                          with _ ->
                              return true
                      }

                  Expect.isTrue threw "the cancelled UPDATE did not report success"

                  // Same honest "stopped" signal the disconnect test
                  // uses: SLOWFN's call count settles instead of
                  // grinding through all 2000 rows.
                  let mutable lastCount = System.Threading.Interlocked.Read(&slowCalls)
                  let mutable stable = false
                  let deadline = DateTime.UtcNow.AddSeconds 10.0

                  while not stable && DateTime.UtcNow < deadline do
                      do! Async.Sleep 300
                      let current = System.Threading.Interlocked.Read(&slowCalls)
                      stable <- current = lastCount
                      lastCount <- current

                  Expect.isTrue stable "SLOWFN's call count stopped growing after Cancel — the update fold actually unwound"

                  Expect.isTrue
                      (lastCount < int64 rowCount)
                      (sprintf "only a fraction of the %d rows should have been visited (got %d)" rowCount lastCount)

                  // The discarded builder means not one row shows the
                  // `SET v = 1` rewrite (none should match anyway —
                  // this also catches a broken predicate mistaking the
                  // cancel for a match).
                  use checkCmd = setup.CreateCommand()
                  checkCmd.CommandText <- "SELECT COALESCE(SUM(v), -1) FROM upd_cancel"
                  let! sumResult = checkCmd.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string sumResult) "0" "the table is unmodified — no partial rewrite survived the cancel"

                  do! setup.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          // `Session.defaultVariables` advertises the wire's real per-packet
          // ceiling (`Limits.maxAllowedPacket`, 64 MiB) as
          // max_allowed_packet — advertising MySQL's 16 MiB default made
          // MySqlConnector refuse a >16 MiB statement client-side before
          // ever sending it, even though the server would have taken it.
          testCase "max_allowed_packet reads 64MiB and a >16MiB blob inserted as query text round-trips"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                          port

                  use conn = new MySqlConnector.MySqlConnection(connStr)
                  do! conn.OpenAsync() |> Async.AwaitTask

                  use varCmd = conn.CreateCommand()
                  varCmd.CommandText <- "SELECT @@max_allowed_packet"
                  let! varResult = varCmd.ExecuteScalarAsync() |> Async.AwaitTask
                  Expect.equal (string varResult) "67108864" "@@max_allowed_packet advertises the real 64 MiB wire ceiling"

                  use ddl = conn.CreateCommand()
                  ddl.CommandText <- "CREATE TABLE big_blobs (id INT PRIMARY KEY, data LONGBLOB)"
                  do! ddl.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                  // 17 MiB of deterministic bytes → a ~34 MiB hex
                  // literal, so the COM_QUERY payload itself crosses
                  // MySQL's 16 MiB single-frame limit and exercises
                  // both multi-frame reassembly (client → server) and
                  // multi-frame writes (the SELECT reply back).
                  let blob = Array.zeroCreate<byte> (17 * 1024 * 1024)
                  Random(42).NextBytes blob

                  use ins = conn.CreateCommand()
                  ins.CommandText <- sprintf "INSERT INTO big_blobs VALUES (1, x'%s')" (Convert.ToHexString blob)
                  ins.CommandTimeout <- 120
                  let! affected = ins.ExecuteNonQueryAsync() |> Async.AwaitTask
                  Expect.equal affected 1 "the >16MiB INSERT was accepted"

                  use sel = conn.CreateCommand()
                  sel.CommandText <- "SELECT data FROM big_blobs WHERE id = 1"
                  sel.CommandTimeout <- 120
                  let! back = sel.ExecuteScalarAsync() |> Async.AwaitTask
                  let backBytes = back :?> byte[]
                  Expect.equal backBytes.Length blob.Length "the blob's length survived the round-trip"
                  Expect.isTrue (backBytes = blob) "the blob's bytes survived the round-trip"

                  do! conn.CloseAsync() |> Async.AwaitTask
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_PREPARE infers canonical parameter types from SQL context"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let query (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! _ = readPacketAsync stream
                          return ()
                      }

                  do!
                      query
                          "CREATE TABLE typed_params (i INT, u BIGINT UNSIGNED, d DECIMAL(8,2), s VARCHAR(20), dt DATETIME(6), b BLOB, flag BIT(9), j JSON)"

                  do! query "CREATE VIEW typed_view AS SELECT i, d FROM typed_params"

                  let prepare (sql: string) =
                      async {
                          let payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! _, parameters, _ = readPreparedReply stream
                          return parameters |> List.map readWireDefinition
                      }

                  let! insert =
                      prepare
                          "INSERT INTO typed_params(i,u,d,s,dt,b,flag,j) VALUES(?,?,?,?,?,?,?,?)"

                  let expected =
                      [ 63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TBigInt true)
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType(TDateTime 6)
                        63, parameterMetadataOfType TLongBlob
                        63, parameterMetadataOfType(TBit 64)
                        45, parameterMetadataOfType TJson ]

                  Expect.sequenceEqual
                      (insert |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      expected
                      "INSERT parameters use MySQL's canonical contextual metadata"

                  let! predicates = prepare "SELECT i FROM typed_params WHERE i = ? LIMIT ?"

                  Expect.sequenceEqual
                      (predicates |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TBigInt true) ]
                      "comparison and LIMIT parameters have distinct signedness"

                  let! expressions = prepare "SELECT CAST(? AS DECIMAL(8,2)), ?"

                  Expect.sequenceEqual
                      (expressions |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TDecimal(65, 30, false))
                        45, parameterMetadataOfType(TVarchar 16383) ]
                      "casts infer a type while context-free parameters remain generic"

                  let! functions =
                      prepare
                          "SELECT ABS(?), ROUND(?), DATE(?), TIME(?), JSON_LENGTH(?), ST_ASTEXT(?), MOD(?,2), SEC_TO_TIME(?), ADDTIME(?,?)"

                  Expect.sequenceEqual
                      (functions |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TDouble false)
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        45, parameterMetadataOfType TDate
                        45, parameterMetadataOfType(TDateTime 6)
                        45, parameterMetadataOfType TJson
                        63, parameterMetadataOfType(TGeometry Geometry)
                        63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        45, parameterMetadataOfType(TTime 6)
                        45, parameterMetadataOfType(TTime 6) ]
                      "function signatures determine numeric, temporal, JSON, and geometry parameters"

                  let! overloads =
                      prepare
                          "SELECT JSON_VALID(?), JSON_UNQUOTE(?), JSON_OVERLAPS(?,?), JSON_SET(?, '$.a', ?), HOUR(?), FROM_DAYS(?), WEEK(?,?), FORMAT(?,?), SUBSTRING(?,?,?), ST_BUFFER(?,?), ST_SRID(?,?)"

                  Expect.sequenceEqual
                      (overloads |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 45, parameterMetadataOfType TJson
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType TJson
                        45, parameterMetadataOfType TJson
                        45, parameterMetadataOfType(TDateTime 6)
                        63, parameterMetadataOfType(TBigInt false)
                        45, parameterMetadataOfType(TDateTime 6)
                        63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        63, parameterMetadataOfType(TBigInt false)
                        45, parameterMetadataOfType(TVarchar 16383)
                        63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TGeometry Geometry)
                        63, parameterMetadataOfType(TDouble false)
                        63, parameterMetadataOfType(TGeometry Geometry)
                        63, parameterMetadataOfType(TBigInt false) ]
                      "overloaded functions retain distinct document, text, and position types"

                  let! specialized =
                      prepare
                          "SELECT MAKETIME(?,?,?), FROM_UNIXTIME(?), SHA2(?,?), JSON_ARRAY_APPEND(?,?,?), ST_DISTANCE(?,?), ST_GEOMFROMWKB(?,?), ST_GEOMFROMTEXT(?,?)"

                  Expect.sequenceEqual
                      (specialized |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TBigInt false)
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        63, parameterMetadataOfType(TDecimal(65, 30, false))
                        45, parameterMetadataOfType(TVarchar 16383)
                        63, parameterMetadataOfType(TBigInt false)
                        45, parameterMetadataOfType TJson
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType TJson
                        63, parameterMetadataOfType(TGeometry Geometry)
                        63, parameterMetadataOfType(TGeometry Geometry)
                        63, parameterMetadataOfType TLongBlob
                        63, parameterMetadataOfType(TBigInt false)
                        45, parameterMetadataOfType(TVarchar 16383)
                        63, parameterMetadataOfType(TBigInt false) ]
                      "specialized signatures preserve argument-position metadata"

                  let! parameterOnly = prepare "SELECT ? + ?, ? = ?"

                  Expect.sequenceEqual
                      (parameterOnly |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TDouble false)
                        63, parameterMetadataOfType(TDouble false)
                        45, parameterMetadataOfType(TVarchar 16383)
                        45, parameterMetadataOfType(TVarchar 16383) ]
                      "parameter-only arithmetic and comparison follow distinct default rules"

                  let! nested =
                      prepare
                          "WITH c AS (SELECT i FROM typed_view) SELECT i FROM c WHERE i = ?"

                  Expect.sequenceEqual
                      (nested |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 63, parameterMetadataOfType(TBigInt false) ]
                      "view and CTE projections preserve source parameter types"
              }
              |> Async.RunSynchronously

          testCase "registered function signatures drive prepared parameter and result metadata"
          <| fun _ ->
              async {
                  let extension =
                      Fsdb.Functions.ScalarFunction.create "TYPED_EXTENSION" (fun _ _ -> VNull)
                      |> Fsdb.Functions.ScalarFunction.withSignature
                          [ TJson; TGeometry Point; TBigInt true ]
                          (TTime 3)

                  let registry = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension extension
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) registry
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let payload =
                      Array.append
                          [| 0x16uy |]
                          (Text.Encoding.UTF8.GetBytes "SELECT TYPED_EXTENSION(?, ?, ?)")

                  do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                  let! _, parameters, columns = readPreparedReply stream
                  let parameters = parameters |> List.map readWireDefinition
                  let columns = columns |> List.map readWireDefinition

                  Expect.sequenceEqual
                      (parameters |> List.map (fun definition -> definition.CharacterSet, definition.Metadata))
                      [ 45, parameterMetadataOfType TJson
                        63, parameterMetadataOfType(TGeometry Point)
                        63, parameterMetadataOfType(TBigInt true) ]
                      "the declared parameter types reach COM_STMT_PREPARE"

                  Expect.sequenceEqual
                      (columns |> List.map _.Metadata)
                      [ metadataOfType(TTime 3) ]
                      "the declared result type reaches COM_STMT_PREPARE"
              }
              |> Async.RunSynchronously

          testCase "prepared statements resolve schema changes when executed"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let query (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! response = readPacketAsync stream
                          Expect.notEqual response.Value.Payload.[0] 0xffuy "schema change succeeds"
                      }

                  do! query "CREATE TABLE evolving (i INT)"
                  do! query "INSERT INTO evolving VALUES (1)"

                  let preparePayload =
                      Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT * FROM evolving")

                  do! writePacketAsync stream { SeqId = 0uy; Payload = preparePayload } |> Async.Ignore
                  let! statementId, _, preparedColumns = readPreparedReply stream
                  Expect.hasLength preparedColumns 1 "the original schema has one column"

                  do! query "ALTER TABLE evolving ADD COLUMN s VARCHAR(10) DEFAULT 'x'"

                  let executePayload =
                      let writer = Writer()
                      writer.WriteByte 0x17uy
                      writer.WriteInt32LE statementId
                      writer.WriteByte 0uy
                      writer.WriteInt32LE 1
                      writer.ToArray()

                  do! writePacketAsync stream { SeqId = 0uy; Payload = executePayload } |> Async.Ignore
                  let! columnCount = readPacketAsync stream
                  Expect.equal columnCount.Value.Payload.[0] 2uy "execution sees both live columns"

                  let! firstDefinition = readPacketAsync stream
                  let! secondDefinition = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream

                  Expect.equal (readWireDefinition firstDefinition.Value).Metadata.TypeId TypeLong "the existing column keeps its type"
                  Expect.equal (readWireDefinition secondDefinition.Value).Metadata.TypeId TypeVarString "the new column is described at execution"
              }
              |> Async.RunSynchronously

          // A DATETIME(6) value round-tripped through COM_STMT_PREPARE +
          // COM_STMT_EXECUTE must use the binary protocol's 11-byte datetime
          // form (microseconds present) and advertise `decimals = 6` on the
          // column-definition packet — the two things a real binary-protocol
          // client (mysqlnd, MySqlConnector's Prepare()) reads to recover
          // sub-second precision. Raw sockets, since MySqlConnector itself
          // never exposes either the wire byte length or the column-def
          // Decimals byte.
          testCase "COM_STMT_EXECUTE on a DATETIME(6) column uses the 11-byte binary form with decimals=6"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

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

                  let! _ = query "CREATE TABLE t (d DATETIME(6))"
                  let! _ = readPacketAsync stream
                  let! _ = query "INSERT INTO t VALUES ('2024-03-05 13:45:09.123456')"
                  let! _ = readPacketAsync stream

                  // COM_STMT_PREPARE "SELECT d FROM t".
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT d FROM t") }
                  let! stmtId, _, preparedColumns = readPreparedReply stream
                  Expect.hasLength preparedColumns 1 "PREPARE advertises the SELECT result column"

                  let preparedColumn = Reader(preparedColumns.Head.Payload)
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "def") "prepared catalog"
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "fsdb") "prepared schema"
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "t") "prepared table"
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "t") "prepared physical table"
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "d") "prepared name"
                  Expect.equal (preparedColumn.ReadLenEncString()) (Some "d") "prepared physical name"

                  preparedColumn.ReadLenEncInt() |> ignore
                  preparedColumn.ReadInt16LE() |> ignore
                  preparedColumn.ReadInt32LE() |> ignore
                  Expect.equal (preparedColumn.ReadByte()) TypeDateTime "PREPARE advertises DATETIME"
                  preparedColumn.ReadInt16LE() |> ignore
                  Expect.equal (preparedColumn.ReadByte()) 6uy "PREPARE advertises fsp 6"

                  // COM_STMT_EXECUTE with zero params: stmtId, cursor
                  // flags, iteration count — no null bitmap/type array
                  // when the statement has no parameters.
                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE stmtId
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! _ = readPacketAsync stream // column count
                  let! colDef = readPacketAsync stream
                  let! _ = readPacketAsync stream // EOF
                  let! row = readPacketAsync stream
                  let! _ = readPacketAsync stream // trailing EOF

                  // Column-definition packet: skip the six length-encoded
                  // strings (catalog/schema/table/org_table/name/org_name)
                  // and the lenenc-int fixed-fields-length marker, then
                  // charset(2)/column length(4)/type(1)/flags(2) before
                  // the decimals byte.
                  let cr = Reader(colDef.Value.Payload)
                  for _ in 1..6 do
                      cr.ReadLenEncString() |> ignore

                  cr.ReadLenEncInt() |> ignore
                  cr.ReadInt16LE() |> ignore // charset
                  cr.ReadInt32LE() |> ignore // column length
                  Expect.equal (cr.ReadByte()) TypeDateTime "column advertises the DATETIME wire type"
                  cr.ReadInt16LE() |> ignore // flags
                  Expect.equal (cr.ReadByte()) 6uy "decimals byte reports fsp 6"

                  // Binary row: header byte, null bitmap ((1 col + 7 + 2)
                  // / 8 = 1 byte), then the datetime value itself.
                  let rr = Reader(row.Value.Payload)
                  rr.ReadByte() |> ignore // row packet header (0x00)
                  rr.ReadBytes 1 |> ignore // null bitmap, one column, not null
                  let length = rr.ReadByte()
                  Expect.equal length 11uy "sub-second value uses the 11-byte datetime form"
                  Expect.equal (rr.ReadInt16LE()) 2024 "year"
                  Expect.equal (rr.ReadByte()) 3uy "month"
                  Expect.equal (rr.ReadByte()) 5uy "day"
                  Expect.equal (rr.ReadByte()) 13uy "hour"
                  Expect.equal (rr.ReadByte()) 45uy "minute"
                  Expect.equal (rr.ReadByte()) 9uy "second"
                  Expect.equal (rr.ReadInt32LE()) 123456 "microseconds"
              }
              |> Async.RunSynchronously

          testCase "COM_PING replies OK and the connection stays usable"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0euy |] }
                  let! pingReply = readPacketAsync stream
                  Expect.equal pingReply.Value.Payload.[0] 0x00uy "COM_PING replies OK"

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
              }
              |> Async.RunSynchronously

          testCase "net_read_timeout applies after a command starts, not while the connection is idle"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  for compressed in [ false; true ] do
                      let capabilities = ClientProtocol41 ||| if compressed then ClientCompress else 0u
                      let! client, rawStream = connectRawAsWithCapabilities server.Port "root" capabilities
                      use client = client

                      let compressedStream =
                          if compressed then
                              Some(new Fsdb.Compression.CompressedStream(rawStream, true))
                          else
                              None

                      let stream = compressedStream |> Option.map (fun stream -> stream :> IO.Stream) |> Option.defaultValue rawStream
                      let beginCommand () = compressedStream |> Option.iter _.BeginCommand()

                      try
                          let setTimeout =
                              Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SET SESSION net_read_timeout = 1")

                          beginCommand ()
                          let! _ = writePacketAsync stream { SeqId = 0uy; Payload = setTimeout }
                          let! setReply = readPacketAsync stream
                          Expect.equal setReply.Value.Payload.[0] 0x00uy "the session timeout is accepted"

                          beginCommand ()
                          do! Async.Sleep 1200
                          let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0euy |] }
                          let! pingReply = readPacketAsync stream
                          Expect.equal pingReply.Value.Payload.[0] 0x00uy "idle time still follows wait_timeout"

                          beginCommand ()
                          do! rawStream.WriteAsync([| 1uy |], 0, 1) |> Async.AwaitTask

                          let buffer = Array.zeroCreate<byte> 1
                          use deadline = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)
                          let! read = rawStream.ReadAsync(buffer, 0, buffer.Length, deadline.Token) |> Async.AwaitTask
                          Expect.equal read 0 "a stalled partial packet closes the connection"
                      finally
                          compressedStream |> Option.iter _.Dispose()
              }
              |> Async.RunSynchronously

          testCase "CLIENT_INTERACTIVE selects interactive_timeout for idle commands"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty

                  let setTimeouts stream =
                      async {
                          for sql in [ "SET SESSION wait_timeout = 1"; "SET SESSION interactive_timeout = 4" ] do
                              let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                              let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload }
                              let! reply = readPacketAsync stream
                              Expect.equal reply.Value.Payload.[0] 0x00uy (sql + " accepted")
                      }

                  let! ordinaryClient, ordinaryStream = connectRawAsWithCapabilities server.Port "root" ClientProtocol41
                  use ordinaryClient = ordinaryClient
                  let! interactiveClient, interactiveStream =
                      connectRawAsWithCapabilities server.Port "root" (ClientProtocol41 ||| ClientInteractive)
                  use interactiveClient = interactiveClient

                  do! setTimeouts ordinaryStream
                  do! setTimeouts interactiveStream
                  do! Async.Sleep 1500

                  let! ordinaryReply = readPacketAsync ordinaryStream
                  Expect.isNone ordinaryReply "ordinary client expires at wait_timeout"

                  let! _ = writePacketAsync interactiveStream { SeqId = 0uy; Payload = [| 0x0euy |] }
                  let! interactiveReply = readPacketAsync interactiveStream
                  Expect.equal interactiveReply.Value.Payload.[0] 0x00uy "interactive client remains alive"
              }
              |> Async.RunSynchronously

          testCase "COM_STATISTICS reports live server counters"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x09uy |] }
                  let! reply = readPacketAsync stream
                  let status = Text.Encoding.UTF8.GetString reply.Value.Payload
                  Expect.stringStarts status "Uptime: " "status format"
                  Expect.stringContains status "  Threads: " "thread count"
                  Expect.stringContains status "  Questions: " "question count"
                  Expect.stringContains status "  Queries per second avg: " "query rate"
              }
              |> Async.RunSynchronously

          testCase "COM_PROCESS_INFO returns the processlist resultset"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0auy |] }
                  let! reply = readPacketAsync stream

                  Expect.equal reply.Value.Payload [| 8uy |] "processlist has eight columns"
              }
              |> Async.RunSynchronously

          testCase "COM_PROCESS_KILL closes the target connection"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let connStr =
                      sprintf
                          "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None;Pooling=false"
                          port

                  use victim = new MySqlConnector.MySqlConnection(connStr)
                  do! victim.OpenAsync() |> Async.AwaitTask

                  let! killer, stream = connectRaw port
                  use killer = killer
                  let payload = Writer()
                  payload.WriteByte 0x0cuy
                  payload.WriteInt32LE(int victim.ServerThread)

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload.ToArray() }
                  let! reply = readPacketAsync stream
                  Expect.equal reply.Value.Payload.[0] 0x00uy "kill replies OK"

                  use command = victim.CreateCommand()
                  command.CommandText <- "SELECT 1"

                  let! failed =
                      async {
                          try
                              let! _ = command.ExecuteScalarAsync() |> Async.AwaitTask
                              return false
                          with _ ->
                              return true
                      }

                  Expect.isTrue failed "target connection is closed"
              }
              |> Async.RunSynchronously

          testCase "COM_DEBUG returns EOF and keeps the connection usable"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0duy |] }
                  let! reply = readPacketAsync stream
                  Expect.equal reply.Value.Payload.[0] 0xfeuy "debug replies EOF"

                  let query = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1")
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = query }
                  let! next = readPacketAsync stream
                  Expect.isSome next "connection remains usable"
              }
              |> Async.RunSynchronously

          testCase "SHUTDOWN acknowledges before stopping the listener"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()
                  Fsdb.Auth.createUser store "ordinary" "%" None |> ignore
                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let port = server.Port

                  let! ordinary, ordinaryStream = connectRawAs port "ordinary"
                  use ordinary = ordinary
                  let shutdown = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SHUTDOWN")
                  let! _ = writePacketAsync ordinaryStream { SeqId = 0uy; Payload = shutdown }
                  let! denied = readPacketAsync ordinaryStream
                  Expect.equal (Reader(denied.Value.Payload.[1..]).ReadInt16LE()) 1227 "ordinary user is denied"

                  let! _ = writePacketAsync ordinaryStream { SeqId = 0uy; Payload = [| 0x0euy |] }
                  let! ping = readPacketAsync ordinaryStream
                  Expect.equal ping.Value.Payload.[0] 0x00uy "denied shutdown leaves listener alive"

                  let! client, stream = connectRaw port
                  use client = client
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = shutdown }
                  let! reply = readPacketAsync stream
                  Expect.equal reply.Value.Payload.[0] 0x00uy "shutdown replies OK"
                  do! server.Completion |> Async.AwaitTask

                  use probe = new Net.Sockets.TcpClient()
                  let refused =
                      try
                          probe.Connect(Net.IPAddress.Loopback, port)
                          false
                      with :? Net.Sockets.SocketException ->
                          true

                  Expect.isTrue refused "listener is stopped"
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_PREPARE on invalid SQL replies ERR and the connection stays usable"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELEC GARBAGE") }
                  let! prepareErr = readPacketAsync stream
                  Expect.equal prepareErr.Value.Payload.[0] 0xffuy "COM_STMT_PREPARE on a parse error replies ERR"

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_EXECUTE on an unknown statement id replies 1243"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE 9999
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! execErr = readPacketAsync stream
                  Expect.equal execErr.Value.Payload.[0] 0xffuy "unknown statement id replies ERR"
                  Expect.equal (Reader(execErr.Value.Payload.[1..]).ReadInt16LE()) 1243 "1243 unknown prepared statement handler"

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
              }
              |> Async.RunSynchronously

          testCase "prepared read-only cursors fetch binary rows in bounded batches"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream = connectRaw server.Port
                  use client = client

                  let query (sql: string) =
                      async {
                          let payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql)
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          let! response = readPacketAsync stream
                          Expect.notEqual response.Value.Payload.[0] 0xffuy "setup query succeeds"
                      }

                  let statusOfEof (packet: Packet) =
                      let reader = Reader(packet.Payload.[1..])
                      reader.ReadInt16LE() |> ignore
                      reader.ReadInt16LE()

                  let readIntRow (packet: Packet) =
                      let reader = Reader(packet.Payload)
                      Expect.equal (reader.ReadByte()) 0uy "binary row header"
                      reader.ReadByte() |> ignore
                      reader.ReadInt32LE()

                  let executeCursor statementId =
                      let writer = Writer()
                      writer.WriteByte 0x17uy
                      writer.WriteInt32LE statementId
                      writer.WriteByte 1uy
                      writer.WriteInt32LE 1
                      writePacketAsync stream { SeqId = 0uy; Payload = writer.ToArray() } |> Async.Ignore

                  let fetch statementId count =
                      let writer = Writer()
                      writer.WriteByte 0x1cuy
                      writer.WriteInt32LE statementId
                      writer.WriteInt32LE count
                      writePacketAsync stream { SeqId = 0uy; Payload = writer.ToArray() } |> Async.Ignore

                  do! query "CREATE TABLE cursor_rows (n INT PRIMARY KEY)"
                  do! query "INSERT INTO cursor_rows VALUES (1),(2),(3)"

                  let prepare = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT n FROM cursor_rows ORDER BY n")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = prepare } |> Async.Ignore
                  let! statementId, _, _ = readPreparedReply stream

                  do! executeCursor statementId
                  let! columnCount = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! cursorOpened = readPacketAsync stream
                  Expect.equal columnCount.Value.Payload.[0] 1uy "execute returns metadata"
                  Expect.isTrue (statusOfEof cursorOpened.Value &&& StatusCursorExists <> 0) "execute opens a cursor"

                  do! fetch statementId 2
                  let! first = readPacketAsync stream
                  let! second = readPacketAsync stream
                  let! more = readPacketAsync stream
                  Expect.sequenceEqual [ readIntRow first.Value; readIntRow second.Value ] [ 1; 2 ] "the first fetch is bounded"
                  Expect.isTrue (statusOfEof more.Value &&& StatusCursorExists <> 0) "more rows remain"

                  do! fetch statementId 2
                  let! third = readPacketAsync stream
                  let! exhausted = readPacketAsync stream
                  Expect.equal (readIntRow third.Value) 3 "the final fetch returns the remaining row"
                  Expect.equal (statusOfEof exhausted.Value &&& StatusCursorExists) 0 "the exhausted cursor closes"
                  Expect.isTrue (statusOfEof exhausted.Value &&& StatusLastRowSent <> 0) "the final fetch is marked"

                  do! fetch statementId 1
                  let! closed = readPacketAsync stream
                  Expect.equal (Reader(closed.Value.Payload.[1..]).ReadInt16LE()) 1421 "a closed cursor cannot be fetched"

                  do! executeCursor statementId
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let reset = Writer()
                  reset.WriteByte 0x1auy
                  reset.WriteInt32LE statementId
                  do! writePacketAsync stream { SeqId = 0uy; Payload = reset.ToArray() } |> Async.Ignore
                  let! resetOk = readPacketAsync stream
                  Expect.equal resetOk.Value.Payload.[0] 0uy "reset succeeds"
                  do! fetch statementId 1
                  let! resetCursor = readPacketAsync stream
                  Expect.equal (Reader(resetCursor.Value.Payload.[1..]).ReadInt16LE()) 1421 "reset closes the cursor"

                  do! executeCursor statementId
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  do! query "COMMIT"
                  do! fetch statementId 1
                  let! committedCursor = readPacketAsync stream
                  Expect.equal (Reader(committedCursor.Value.Payload.[1..]).ReadInt16LE()) 1421 "commit closes the cursor"
              }
              |> Async.RunSynchronously

          testCase "prepared cursors terminate metadata and empty fetches with deprecated EOF"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let! client, stream = connectRawAsWithCapabilities server.Port "root" (ClientProtocol41 ||| ClientDeprecateEof)
                  use client = client

                  let prepare = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1 WHERE 0")
                  do! writePacketAsync stream { SeqId = 0uy; Payload = prepare } |> Async.Ignore
                  let! prepared = readPacketAsync stream
                  let preparedReader = Reader(prepared.Value.Payload.[1..])
                  let statementId = preparedReader.ReadInt32LE()
                  let columnCount = preparedReader.ReadInt16LE()
                  preparedReader.ReadInt16LE() |> ignore
                  Expect.equal columnCount 1 "the statement has one result column"
                  let! _ = readPacketAsync stream

                  let execute = Writer()
                  execute.WriteByte 0x17uy
                  execute.WriteInt32LE statementId
                  execute.WriteByte 1uy
                  execute.WriteInt32LE 1
                  do! writePacketAsync stream { SeqId = 0uy; Payload = execute.ToArray() } |> Async.Ignore
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! opened = readPacketAsync stream
                  let openedStatus = Reader(opened.Value.Payload.[1..])
                  openedStatus.ReadLenEncInt() |> ignore
                  openedStatus.ReadLenEncInt() |> ignore
                  Expect.isTrue (openedStatus.ReadInt16LE() &&& StatusCursorExists <> 0) "metadata OK advertises the cursor"

                  let fetch = Writer()
                  fetch.WriteByte 0x1cuy
                  fetch.WriteInt32LE statementId
                  fetch.WriteInt32LE 1
                  do! writePacketAsync stream { SeqId = 0uy; Payload = fetch.ToArray() } |> Async.Ignore
                  let! exhausted = readPacketAsync stream
                  let exhaustedStatus = Reader(exhausted.Value.Payload.[1..])
                  exhaustedStatus.ReadLenEncInt() |> ignore
                  exhaustedStatus.ReadLenEncInt() |> ignore
                  Expect.isTrue (exhaustedStatus.ReadInt16LE() &&& StatusLastRowSent <> 0) "empty fetch marks the last row"
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_CLOSE gets no reply and the connection stays usable"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! stmtId, _, _ = readPreparedReply stream

                  let closePayload =
                      let w = Writer()
                      w.WriteByte 0x19uy
                      w.WriteInt32LE stmtId
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = closePayload }

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "a query right after COM_STMT_CLOSE (no reply) still gets a reply"
                  let! _ = readPacketAsync stream // column def
                  let! _ = readPacketAsync stream // EOF
                  let! _ = readPacketAsync stream // row
                  let! _ = readPacketAsync stream // trailing EOF

                  // The closed statement id is now unknown.
                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE stmtId
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! execErr = readPacketAsync stream
                  Expect.equal (Reader(execErr.Value.Payload.[1..]).ReadInt16LE()) 1243 "the closed statement id is gone"
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_RESET replies OK, drops buffered long data, and 1243s for an unknown id"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT ?") }
                  let! stmtId, _, _ = readPreparedReply stream

                  // Buffer long data for the one param, then reset — the
                  // reset must drop it, not leave it to be picked up by a
                  // later EXECUTE.
                  let longDataPayload =
                      let w = Writer()
                      w.WriteByte 0x18uy
                      w.WriteInt32LE stmtId
                      w.WriteInt16LE 0
                      w.WriteBytes(Text.Encoding.UTF8.GetBytes "buffered")
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = longDataPayload }

                  let resetPayload =
                      let w = Writer()
                      w.WriteByte 0x1auy
                      w.WriteInt32LE stmtId
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = resetPayload }
                  let! resetOk = readPacketAsync stream
                  Expect.equal resetOk.Value.Payload.[0] 0x00uy "COM_STMT_RESET on a known statement replies OK"

                  // Execute with a fresh bound value — if the buffered
                  // long data had survived, this would return "buffered"
                  // instead of "fresh".
                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE stmtId
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.WriteByte 0uy // null bitmap
                      w.WriteByte 1uy // new-params-bound
                      w.WriteByte TypeVarString
                      w.WriteByte 0uy
                      w.WriteLenEncString "fresh"
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! _ = readPacketAsync stream // column count
                  let! _ = readPacketAsync stream // column def
                  let! _ = readPacketAsync stream // EOF
                  let! row = readPacketAsync stream
                  Expect.stringContains (Text.Encoding.ASCII.GetString row.Value.Payload) "fresh" "the reset dropped the buffered long data"
                  let! _ = readPacketAsync stream // trailing EOF

                  let resetUnknownPayload =
                      let w = Writer()
                      w.WriteByte 0x1auy
                      w.WriteInt32LE 9999
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = resetUnknownPayload }
                  let! resetErr = readPacketAsync stream
                  Expect.equal (Reader(resetErr.Value.Payload.[1..]).ReadInt16LE()) 1243 "COM_STMT_RESET on an unknown id replies 1243"
              }
              |> Async.RunSynchronously

          testCase "COM_CHANGE_USER reauthenticates and replaces connection state"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()

                  match Fsdb.Storage.createDatabase store "change_wire" with
                  | Ok() -> ()
                  | Error error -> failtestf "create database failed: %A" error

                  match Fsdb.Storage.createDatabase store "restricted_change_wire" with
                  | Ok() -> ()
                  | Error error -> failtestf "create database failed: %A" error

                  Fsdb.Auth.createUser store "changed" "localhost" (Some "secret")
                  |> Result.mapError snd
                  |> Result.defaultWith failtest

                  Fsdb.Auth.grant store [ "SELECT" ] (Fsdb.Auth.OnDb "change_wire") [ "changed", "localhost" ] false
                  |> Result.mapError snd
                  |> Result.defaultWith failtest

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let capabilities = ClientProtocol41 ||| ClientSecureConnection ||| ClientPluginAuth

                  let! deniedClient, deniedStream = connectRawAsWithCapabilities server.Port "root" capabilities
                  use deniedClient = deniedClient
                  let! _ =
                      writePacketAsync
                          deniedStream
                          { SeqId = 0uy
                            Payload = changeUserPayload "changed" [||] "restricted_change_wire" 8 (Some "mysql_native_password") }

                  let! deniedSwitch = readPacketAsync deniedStream
                  let deniedReader = Reader(deniedSwitch.Value.Payload)
                  Expect.equal (deniedReader.ReadByte()) 0xfeuy "change-user authentication exchange"
                  let _ = deniedReader.ReadNullTerminatedString()
                  let deniedScramble = deniedReader.ReadBytes 20
                  let! _ =
                      writePacketAsync
                          deniedStream
                          { SeqId = deniedSwitch.Value.SeqId + 1uy
                            Payload = nativePasswordResponse "secret" deniedScramble }

                  let! denied = readPacketAsync deniedStream
                  Expect.equal (Reader(denied.Value.Payload.[1..]).ReadInt16LE()) 1044 "database privilege enforced"

                  let! client, stream = connectRawAsWithCapabilities server.Port "root" capabilities
                  use client = client

                  let command (payload: byte[]) =
                      async {
                          do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
                          return! readPacketAsync stream
                      }

                  let queryOk (sql: string) =
                      async {
                          let! reply = command (Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql))
                          Expect.equal reply.Value.Payload.[0] 0uy (sprintf "%s succeeds" sql)
                      }

                  let queryRow (sql: string) =
                      async {
                          let! head = command (Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes sql))
                          let columnCount = Reader(head.Value.Payload).ReadLenEncInt() |> Option.map int |> Option.defaultValue 0

                          for _ in 1 .. columnCount do
                              let! _ = readPacketAsync stream
                              ()

                          let! _ = readPacketAsync stream
                          let! row = readPacketAsync stream
                          let! _ = readPacketAsync stream
                          let reader = Reader(row.Value.Payload)
                          return [ for _ in 1 .. columnCount -> reader.ReadLenEncString() ]
                      }

                  let! initDb = command (Array.append [| 0x02uy |] (Text.Encoding.UTF8.GetBytes "change_wire"))
                  Expect.equal initDb.Value.Payload.[0] 0uy "initial database selected"
                  do! queryOk "CREATE TABLE change_rows (n INT)"
                  do! queryOk "SET @state = 'old'"
                  do! queryOk "CREATE TEMPORARY TABLE tmp_change (n INT)"
                  do! queryOk "START TRANSACTION"
                  do! queryOk "INSERT INTO change_rows VALUES (1)"

                  let! _ =
                      writePacketAsync
                          stream
                          { SeqId = 0uy
                            Payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }

                  let! statementId, _, _ = readPreparedReply stream

                  let! _ =
                      writePacketAsync
                          stream
                          { SeqId = 0uy
                            Payload = changeUserPayload "changed" [||] "change_wire" 8 (Some "mysql_native_password") }

                  let! authSwitch = readPacketAsync stream
                  let authReader = Reader(authSwitch.Value.Payload)
                  Expect.equal (authReader.ReadByte()) 0xfeuy "change-user starts a fresh authentication exchange"
                  Expect.equal (authReader.ReadNullTerminatedString()) "mysql_native_password" "server authentication plugin"
                  let scramble = authReader.ReadBytes 20
                  let response = nativePasswordResponse "secret" scramble
                  let! _ = writePacketAsync stream { SeqId = authSwitch.Value.SeqId + 1uy; Payload = response }
                  let! changed = readPacketAsync stream
                  Expect.equal changed.Value.Payload.[0] 0uy "new credentials accepted"

                  let! state = queryRow "SELECT CURRENT_USER(), DATABASE(), @state, @@collation_connection"

                  Expect.sequenceEqual
                      state
                      [ Some "changed@localhost"; Some "change_wire"; None; Some "latin1_swedish_ci" ]
                      "identity, schema, variables, and character set are replaced"

                  let! rows = queryRow "SELECT COUNT(*) FROM change_rows"
                  Expect.sequenceEqual rows [ Some "0" ] "the previous transaction was rolled back"

                  let! temporary = command (Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT * FROM tmp_change"))
                  Expect.equal (Reader(temporary.Value.Payload.[1..]).ReadInt16LE()) 1146 "temporary tables are dropped"

                  let execute = Writer()
                  execute.WriteByte 0x17uy
                  execute.WriteInt32LE statementId
                  execute.WriteByte 0uy
                  execute.WriteInt32LE 1
                  let! oldStatement = command (execute.ToArray())
                  Expect.equal (Reader(oldStatement.Value.Payload.[1..]).ReadInt16LE()) 1243 "prepared statements are dropped"
              }
              |> Async.RunSynchronously

          testCase "COM_CHANGE_USER accepts the original challenge response from non-plugin clients"
          <| fun _ ->
              async {
                  let store = Fsdb.Storage.create ()

                  Fsdb.Auth.createUser store "legacy_change" "localhost" (Some "secret")
                  |> Result.mapError snd
                  |> Result.defaultWith failtest

                  use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty
                  let capabilities = ClientProtocol41 ||| ClientSecureConnection
                  let! client, stream, scramble =
                      connectRawAsWithCapabilitiesAndScramble server.Port "root" capabilities

                  use client = client
                  let response = nativePasswordResponse "secret" scramble

                  let! _ =
                      writePacketAsync
                          stream
                          { SeqId = 0uy
                            Payload = changeUserPayload "legacy_change" response "" 999 None }

                  let! changed = readPacketAsync stream
                  Expect.equal changed.Value.Payload.[0] 0uy "the response embedded in the command is verified"

                  let query = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT @@collation_connection")
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = query }
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! _ = readPacketAsync stream
                  let! row = readPacketAsync stream
                  let! _ = readPacketAsync stream

                  Expect.equal
                      (Reader(row.Value.Payload).ReadLenEncString())
                      (Some "utf8mb4_0900_ai_ci")
                      "unknown ids use the server default"
              }
              |> Async.RunSynchronously

          testCase "COM_CHANGE_USER failure returns 1045 and closes the connection"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let capabilities = ClientProtocol41 ||| ClientSecureConnection ||| ClientPluginAuth
                  let! client, stream = connectRawAsWithCapabilities server.Port "root" capabilities
                  use client = client

                  let! _ =
                      writePacketAsync
                          stream
                          { SeqId = 0uy
                            Payload = changeUserPayload "missing" [||] "" 45 (Some "mysql_native_password") }

                  let! authSwitch = readPacketAsync stream
                  Expect.equal authSwitch.Value.Payload.[0] 0xfeuy "unknown accounts receive the same authentication challenge"
                  let! _ = writePacketAsync stream { SeqId = authSwitch.Value.SeqId + 1uy; Payload = [||] }
                  let! denied = readPacketAsync stream
                  Expect.equal (Reader(denied.Value.Payload.[1..]).ReadInt16LE()) 1045 "access denied"
                  let! closed = readPacketAsync stream
                  Expect.isNone closed "authentication failure ends the command phase"
              }
              |> Async.RunSynchronously

          testCase "an unsupported command byte replies ERR 1047 and the connection stays usable"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x20uy |] }
                  let! unsupportedErr = readPacketAsync stream
                  Expect.equal unsupportedErr.Value.Payload.[0] 0xffuy "unsupported command byte replies ERR"
                  Expect.equal (Reader(unsupportedErr.Value.Payload.[1..]).ReadInt16LE()) 1047 "1047 unknown command"

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "a later query on the same connection still gets a reply"
              }
              |> Async.RunSynchronously

          testCase "COM_STMT_SEND_LONG_DATA is capped across all prepared statements on a connection"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  let prepare () =
                      async {
                          let payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT ?")
                          let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload }
                          let! statementId, _, _ = readPreparedReply stream
                          return statementId
                      }

                  let sendChunk statementId (bytes: byte[]) =
                      let w = Writer()
                      w.WriteByte 0x18uy
                      w.WriteInt32LE statementId
                      w.WriteInt16LE 0
                      w.WriteBytes bytes
                      writePacketAsync stream { SeqId = 0uy; Payload = w.ToArray() }

                  let! firstStatement = prepare ()
                  let! secondStatement = prepare ()
                  let half = Array.zeroCreate<byte> (Fsdb.Limits.maxAllowedPacket / 2)
                  let! _ = sendChunk firstStatement half
                  let! _ = sendChunk secondStatement half
                  let! _ = sendChunk secondStatement [| 1uy |]

                  let execPayload =
                      let w = Writer()
                      w.WriteByte 0x17uy
                      w.WriteInt32LE secondStatement
                      w.WriteByte 0uy
                      w.WriteInt32LE 1
                      w.WriteByte 0uy // null bitmap
                      w.WriteByte 1uy // new-params-bound
                      w.WriteByte TypeVarString
                      w.WriteByte 0uy
                      w.WriteLenEncString "" // placeholder; overflow wins
                      w.ToArray()

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = execPayload }
                  let! execErr = readPacketAsync stream
                  Expect.equal execErr.Value.Payload.[0] 0xffuy "overflowed long data fails EXECUTE with ERR"
                  Expect.equal (Reader(execErr.Value.Payload.[1..]).ReadInt16LE()) 1153 "1153 ER_NET_PACKET_TOO_LARGE"

                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = Array.append [| 0x03uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1") }
                  let! afterReply = readPacketAsync stream
                  Expect.isTrue afterReply.IsSome "the connection stays usable after the 1153 ERR"
              }
              |> Async.RunSynchronously

          TestSupport.processGlobalCase "COM_STMT_PREPARE returns 1461 at max_prepared_stmt_count"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "max_prepared_stmt_count", "2" ] (fun () ->
                  async {
                      use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                      let port = server.Port

                      let! client, stream = connectRaw port
                      use client = client
                      let payload = Array.append [| 0x16uy |] (Text.Encoding.UTF8.GetBytes "SELECT 1")

                      for _ in 1..2 do
                          let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload }
                          let! _, _, columns = readPreparedReply stream
                          Expect.hasLength columns 1 "prepare below the cap succeeds with one result column"

                      let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload }
                      let! reply = readPacketAsync stream
                      Expect.equal reply.Value.Payload.[0] 0xffuy "prepare at the cap returns ERR"
                      Expect.equal (Reader(reply.Value.Payload.[1..]).ReadInt16LE()) 1461 "ER_MAX_PREPARED_STMT_COUNT_REACHED"
                  }
                  |> Async.RunSynchronously)

          // A single command whose own payload (reassembled from consecutive
          // 0xffffff-length fragments) blows past the accumulation cap can't
          // even be decoded into a command — `Server`'s connection-level
          // catch sends a best-effort ERR 1153, then the connection ends.
          testCase "streaming a single command past the accumulation cap gets a best-effort ERR 1153 before close"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  let! client, stream = connectRaw port
                  use client = client

                  // A COM_QUERY payload one byte over the cap: `writePacketAsync`
                  // splits it into `maxPacketPayload`-sized fragments; the
                  // server's reassembly raises before `parseCommand` ever runs.
                  let hugeQuery = Array.append [| 0x03uy |] (Array.zeroCreate<byte> Fsdb.Limits.maxAllowedPacket)
                  let! _ = writePacketAsync stream { SeqId = 0uy; Payload = hugeQuery }

                  let! tooLargeErr = readPacketAsync stream
                  Expect.isTrue tooLargeErr.IsSome "a best-effort ERR arrives before the connection closes"
                  Expect.equal tooLargeErr.Value.Payload.[0] 0xffuy "server replies ERR"
                  Expect.equal (Reader(tooLargeErr.Value.Payload.[1..]).ReadInt16LE()) 1153 "1153 ER_NET_PACKET_TOO_LARGE"

                  let! closed = readPacketAsync stream
                  Expect.isNone closed "the connection is closed after the oversized-packet ERR"
              }
              |> Async.RunSynchronously

          // CLIENT_FOUND_ROWS (negotiated in HandshakeResponse41's capability
          // flags) changes a no-op UPDATE's affected_rows from 0 (changed
          // rows, the default) to the matched-row count.
          testCase "CLIENT_FOUND_ROWS makes a no-op UPDATE report matched rows, not changed rows"
          <| fun _ ->
              async {
                  use server = TestSupport.ServerFixture.start (Fsdb.Storage.create ()) Fsdb.Functions.empty
                  let port = server.Port

                  use client = new Net.Sockets.TcpClient()
                  do! client.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask
                  use stream = client.GetStream()

                  let! handshake = readPacketAsync stream
                  let handshakeSeq = handshake.Value.SeqId

                  let helloResponse =
                      let w = Writer()
                      w.WriteInt32LE(int (ClientProtocol41 ||| ClientFoundRows))
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

                  let! _ = query "CREATE TABLE t (n INT)"
                  let! _ = readPacketAsync stream
                  let! _ = query "INSERT INTO t VALUES (1), (2), (3)"
                  let! _ = readPacketAsync stream

                  // Every row already holds its current value: 0 changed
                  // rows, but 3 matched rows.
                  let! _ = query "UPDATE t SET n = n"
                  let! updateOk = readPacketAsync stream
                  Expect.equal updateOk.Value.Payload.[0] 0x00uy "UPDATE replies OK"
                  Expect.equal (Reader(updateOk.Value.Payload.[1..]).ReadLenEncInt()) (Some 3UL) "CLIENT_FOUND_ROWS reports the matched-row count for a no-op UPDATE"
              }
              |> Async.RunSynchronously

        ]
