module Fsdb.Tests.ProtocolTests

open System
open Expecto
open Fsdb.Ast
open Fsdb.Binary
open Fsdb.ColumnWire
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Temporal

type private BlockingWriteStream() =
    inherit IO.Stream()

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = 0L
    override _.Position with get () = 0L and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.WriteAsync(_, _, _, cancellationToken) =
        Threading.Tasks.Task.Delay(Threading.Timeout.Infinite, cancellationToken)

let tests =
    testList
        "Protocol"
        [ testCase "HandshakeV10 payload starts with protocol version 10 and the server version"
          <| fun _ ->
              let authData = Array.create 20 1uy
              let payload = buildHandshakeV10 42 authData
              let r = Reader(payload)
              Expect.equal (r.ReadByte ()) 10uy "protocol version"
              Expect.equal (r.ReadNullTerminatedString ()) ServerVersion "server version string"
              Expect.stringStarts ServerVersion "8.4." "MySQL compatibility version"
              Expect.equal (r.ReadInt32LE ()) 42 "connection id"

          testCase "HandshakeV10 payload declares CLIENT_PLUGIN_AUTH and ends with the plugin name"
          <| fun _ ->
              let payload = buildHandshakeV10 1 (Array.create 20 1uy)
              let text = Text.Encoding.ASCII.GetString payload
              Expect.stringContains text "mysql_native_password" "auth plugin name present"
              Expect.isTrue
                  (ServerCapabilities &&& ClientCanHandleExpiredPasswords <> 0u)
                  "clients may request password-expiry sandbox handling"

          testCase "OK payload starts with 0x00 header"
          <| fun _ ->
              let payload = okPayload ClientProtocol41 StatusAutocommit 0UL 0UL
              Expect.equal payload.[0] 0uy "OK header byte"

          testCase "COM_STMT_PREPARE_OK advertises result and parameter counts"
          <| fun _ ->
              let reader = Reader(stmtPrepareOkPayload 17 2 3)
              Expect.equal (reader.ReadByte()) 0uy "status"
              Expect.equal (reader.ReadInt32LE()) 17 "statement id"
              Expect.equal (reader.ReadInt16LE()) 2 "result columns"
              Expect.equal (reader.ReadInt16LE()) 3 "parameters"
              Expect.equal (reader.ReadByte()) 0uy "reserved"
              Expect.equal (reader.ReadInt16LE()) 0 "warnings"

          testCase "OK payload status flags carry SERVER_STATUS_IN_TRANS while a transaction is open"
          <| fun _ ->
              // PDO's inTransaction() reads SERVER_STATUS_IN_TRANS directly
              // off this bit.
              let payload = okPayload ClientProtocol41 (StatusAutocommit ||| StatusInTrans) 0UL 0UL
              let r = Reader(payload.[1..])
              r.ReadLenEncInt() |> ignore // affected rows
              r.ReadLenEncInt() |> ignore // last insert id
              let statusFlags = r.ReadInt16LE()
              Expect.isTrue (statusFlags &&& StatusInTrans <> 0) "SERVER_STATUS_IN_TRANS set"

          testCase "OK and result terminators carry warning counts"
          <| fun _ ->
              let warningCount (payload: byte[]) =
                  let r = Reader(payload.[1..])
                  r.ReadLenEncInt() |> ignore
                  r.ReadLenEncInt() |> ignore
                  r.ReadInt16LE() |> ignore
                  r.ReadInt16LE()

              Expect.equal (warningCount (okPayloadWithWarnings ClientProtocol41 StatusAutocommit 0UL 0UL 3)) 3 "OK warnings"
              Expect.equal (warningCount (okEndOfResultSetPayloadWithWarnings ClientProtocol41 StatusAutocommit 4)) 4 "deprecate-EOF warnings"

              let eof = Reader((eofPayloadWithWarnings ClientProtocol41 StatusAutocommit 5).[1..])
              Expect.equal (eof.ReadInt16LE()) 5 "legacy EOF warnings"

          testCase "session tracking is advertised and encoded in OK packets"
          <| fun _ ->
              Expect.isTrue (ServerCapabilities &&& ClientSessionTrack <> 0u) "capability advertised"

              let capabilities = ClientProtocol41 ||| ClientSessionTrack
              let payload =
                  okPayloadWithWarningsAndSessionState
                      capabilities
                      StatusAutocommit
                      0UL
                      0UL
                      0
                      [ SystemVariableChanged("autocommit", "OFF"); SchemaChanged "application" ]

              let reader = Reader(payload.[1..])
              reader.ReadLenEncInt() |> ignore
              reader.ReadLenEncInt() |> ignore
              let status = reader.ReadInt16LE()
              reader.ReadInt16LE() |> ignore
              Expect.isTrue (status &&& StatusSessionStateChanged <> 0) "state-changed status"
              Expect.equal (reader.ReadLenEncString()) (Some "") "empty human-readable info"

              let state = reader.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0 |> reader.ReadBytes |> Reader
              Expect.equal (state.ReadByte()) SessionTrackSystemVariables "system-variable tracker"
              let systemVariable = state.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0 |> state.ReadBytes |> Reader
              Expect.equal (systemVariable.ReadLenEncString()) (Some "autocommit") "variable name"
              Expect.equal (systemVariable.ReadLenEncString()) (Some "OFF") "variable value"
              Expect.equal (state.ReadByte()) SessionTrackSchema "schema tracker"
              let schema = state.ReadLenEncInt() |> Option.map int |> Option.defaultValue 0 |> state.ReadBytes |> Reader
              Expect.equal (schema.ReadLenEncString()) (Some "application") "schema name"
              Expect.equal state.Remaining 0 "all tracker bytes consumed"

          testCase "ERR payload carries the error code and message"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              Expect.equal payload.[0] 0xffuy "ERR header byte"
              let r = Reader(payload.[1..])
              Expect.equal (r.ReadInt16LE ()) 1064 "error code"

          testCase "ERR payload for 1064 carries SQLSTATE 42000, not the generic HY000"
          <| fun _ ->
              // PDO/Doctrine branch on SQLSTATE (42000 -> syntax error, not
              // the generic HY000) to classify exceptions.
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "42000" "sqlstate for 1064"

          testCase "schema limit errors carry SQLSTATE 42000"
          <| fun _ ->
              Expect.equal (sqlStateForCode 1071) "42000" "key length"
              Expect.equal (sqlStateForCode 1074) "42000" "column length"

          testCase "ERR payload for an unmapped code falls back to HY000"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 9999 "whatever"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "HY000" "sqlstate fallback"

          testCase "ERR payload preserves an explicit SQLSTATE"
          <| fun _ ->
              let payload = errPayloadWithState ClientProtocol41 60001 "45001" "raised condition"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "45001" "explicit SQLSTATE"

          TestSupport.processGlobalCase "packet writes stop after net_write_timeout"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "net_write_timeout", "1" ] (fun () ->
                  use stream = new BlockingWriteStream()

                  Expect.throwsT<Threading.Tasks.TaskCanceledException>
                      (fun () -> writePacketAsync stream { SeqId = 0uy; Payload = [| 1uy |] } |> Async.RunSynchronously |> ignore)
                      "a peer that stops reading cannot retain a connection indefinitely")

          testCase "the resultset-terminating OK uses header 0xfe, not 0x00"
          <| fun _ ->
              // mysql CLI distinguishes this from a plain OK by the 0xfe
              // header (it reuses the legacy EOF marker byte). Sending 0x00
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
              Expect.equal resp.Database None "no database requested"

          testCase "parseHandshakeResponse treats an empty requested database as absent"
          <| fun _ ->
              let w = Writer()
              w.WriteInt32LE(int (ClientProtocol41 ||| ClientSecureConnection ||| ClientConnectWithDb))
              w.WriteInt32LE 16777216
              w.WriteByte 45uy
              w.WriteBytes(Array.zeroCreate<byte> 23)
              w.WriteNullTerminatedString "root"
              w.WriteByte 0uy
              w.WriteNullTerminatedString ""
              let response = parseHandshakeResponse (w.ToArray())
              Expect.equal response.Database None "empty database names do not request USE"

          testCase "parseChangeUserRequest follows negotiated field layout"
          <| fun _ ->
              let capabilities = ClientProtocol41 ||| ClientSecureConnection ||| ClientPluginAuth
              let writer = Writer()
              writer.WriteNullTerminatedString "changed"
              writer.WriteByte 3uy
              writer.WriteBytes [| 1uy; 2uy; 3uy |]
              writer.WriteNullTerminatedString "application"
              writer.WriteInt16LE 8
              writer.WriteNullTerminatedString "mysql_native_password"

              let request = parseChangeUserRequest capabilities (writer.ToArray())
              Expect.equal request.Username "changed" "username"
              Expect.sequenceEqual request.AuthResponse [| 1uy; 2uy; 3uy |] "auth response"
              Expect.equal request.Database (Some "application") "database"
              Expect.equal request.CharacterSet (Some 8) "character set"
              Expect.equal request.ClientPlugin (Some "mysql_native_password") "plugin"

          testCase "parseHandshakeResponse rejects an auth length above Int32.MaxValue"
          <| fun _ ->
              let w = Writer()
              w.WriteInt32LE(int (ClientProtocol41 ||| ClientPluginAuthLenencClientData))
              w.WriteInt32LE 16777216
              w.WriteByte 45uy
              w.WriteBytes(Array.zeroCreate<byte> 23)
              w.WriteNullTerminatedString "root"
              w.WriteByte 0xfeuy
              w.WriteInt64LE Int64.MaxValue

              Expect.throws
                  (fun () -> parseHandshakeResponse (w.ToArray()) |> ignore)
                  "an oversized wire length is rejected before conversion to int"

          testCase "parseHandshakeResponse recognizes an SSLRequest without reading a username"
          <| fun _ ->
              let w = Writer()
              w.WriteInt32LE(int (ClientProtocol41 ||| ClientSsl))
              w.WriteInt32LE 16777216
              w.WriteByte 45uy
              w.WriteBytes(Array.zeroCreate<byte> 23)

              Expect.throwsT<SslRequestException>
                  (fun () -> parseHandshakeResponse (w.ToArray()) |> ignore)
                  "the short SSLRequest shape is rejected deliberately"

              match tryParseSslRequest (w.ToArray()) with
              | Some request -> Expect.isTrue (request.Capabilities &&& ClientSsl <> 0u) "the SSL capability is retained"
              | None -> failtest "the fixed-size SSLRequest is recognized before login parsing"

          testCase "TLS capability is advertised only when a certificate is configured"
          <| fun _ ->
              Expect.equal (serverCapabilities false &&& ClientSsl) 0u "plaintext servers omit CLIENT_SSL"
              Expect.equal (serverCapabilities true &&& ClientSsl) ClientSsl "TLS servers advertise CLIENT_SSL"

          testCase "zlib compression is advertised"
          <| fun _ ->
              Expect.equal (serverCapabilities false &&& ClientCompress) ClientCompress "clients can negotiate CLIENT_COMPRESS"

          testCase "typed text rows encode BLOB values as raw bytes"
          <| fun _ ->
              let bytes = [| 0x00uy; 0xffuy; 0x80uy |]
              let carrier = Text.Encoding.Latin1.GetString bytes
              let metadata = { columnMetadata TypeBlob with Flags = BlobFlag ||| BinaryFlag }
              let reader = Reader(textRowPayloadTyped [ metadata ] [ Some carrier ])
              Expect.equal (reader.ReadLenEncInt ()) (Some(uint64 bytes.Length)) "raw byte length"
              Expect.equal (reader.ReadBytes bytes.Length) bytes "no UTF-8 expansion"

          testCase "typed text rows encode textual BLOB values as UTF-8"
          <| fun _ ->
              let value = "blåbær"
              let reader = Reader(textRowPayloadTyped [ columnMetadata TypeBlob ] [ Some value ])
              let expected = Text.Encoding.UTF8.GetBytes value
              Expect.equal (reader.ReadLenEncInt ()) (Some(uint64 expected.Length)) "UTF-8 byte length"
              Expect.equal (reader.ReadBytes expected.Length) expected "UTF-8 payload"

          testCase "binary rows encode textual BLOB values as UTF-8"
          <| fun _ ->
              let value = "blåbær"
              let reader = Reader(binaryRowPayload [ columnMetadata TypeBlob ] [ Some value ])
              reader.ReadBytes 2 |> ignore
              let expected = Text.Encoding.UTF8.GetBytes value
              Expect.equal (reader.ReadLenEncInt ()) (Some(uint64 expected.Length)) "UTF-8 byte length"
              Expect.equal (reader.ReadBytes expected.Length) expected "UTF-8 payload"

          testCase "BLOB column definitions advertise binary collation and flags"
          <| fun _ ->
              let metadata =
                  { columnMetadata TypeBlob with
                      ColumnLength = 65535u
                      Flags = BlobFlag ||| BinaryFlag }

              let reader = Reader(columnDefPayload { Name = "payload"; Metadata = metadata })
              for _ in 1..6 do
                  reader.ReadLenEncString() |> ignore
              reader.ReadLenEncInt() |> ignore
              Expect.equal (reader.ReadInt16LE ()) BinaryCollation "binary collation"
              reader.ReadInt32LE() |> ignore
              Expect.equal (reader.ReadByte ()) TypeBlob "BLOB type"
              let flags = reader.ReadInt16LE()
              Expect.isTrue (flags &&& 0x0010 <> 0) "BLOB flag"
              Expect.isTrue (flags &&& 0x0080 <> 0) "BINARY flag"

          testCase "column definitions advertise an explicit text collation"
          <| fun _ ->
              let metadata =
                  { columnMetadata TypeVarString with
                      ColumnLength = 40u
                      CollationId = Some 8us }

              let reader = Reader(columnDefPayload { Name = "latin"; Metadata = metadata })
              for _ in 1..6 do
                  reader.ReadLenEncString() |> ignore

              reader.ReadLenEncInt() |> ignore
              Expect.equal (reader.ReadInt16LE()) 8 "latin1_swedish_ci charset number"

          testCase "column definitions encode physical source fields"
          <| fun _ ->
              let metadata =
                  { columnMetadata TypeLong with
                      Origin =
                          Some
                              { Schema = "app"
                                Table = "u"
                                OriginalTable = "users"
                                OriginalName = "id" } }

              let reader = Reader(columnDefPayload { Name = "renamed"; Metadata = metadata })
              Expect.equal (reader.ReadLenEncString()) (Some "def") "catalog"
              Expect.equal (reader.ReadLenEncString()) (Some "app") "schema"
              Expect.equal (reader.ReadLenEncString()) (Some "u") "table alias"
              Expect.equal (reader.ReadLenEncString()) (Some "users") "physical table"
              Expect.equal (reader.ReadLenEncString()) (Some "renamed") "result name"
              Expect.equal (reader.ReadLenEncString()) (Some "id") "physical column"

          testCase "BIT column definitions advertise binary collation and unsigned metadata"
          <| fun _ ->
              let metadata = metadataOfType (TBit 9)
              Expect.isTrue (metadata.Flags &&& UnsignedFlag <> 0us) "unsigned flag"
              Expect.isFalse (metadata.Flags &&& BinaryFlag <> 0us) "binary flag"

              let reader = Reader(columnDefPayload { Name = "bits"; Metadata = metadata })
              for _ in 1..6 do
                  reader.ReadLenEncString() |> ignore
              reader.ReadLenEncInt() |> ignore
              Expect.equal (reader.ReadInt16LE ()) BinaryCollation "binary collation"
              Expect.equal (reader.ReadInt32LE ()) 9 "bit width"
              Expect.equal (reader.ReadByte ()) TypeBit "BIT type"
              Expect.isTrue (reader.ReadInt16LE () &&& int UnsignedFlag <> 0) "unsigned metadata"

          testCase "TIME metadata advertises binary collation and flags"
          <| fun _ ->
              let metadata = metadataOfType (TTime 6)
              let value = VTime(tryParseTimeValue "01:02:03.123456" |> Option.get)
              Expect.isTrue (metadata.Flags &&& BinaryFlag <> 0us) "declared binary flag"
              Expect.isTrue ((mysqlMetadataOf value).Flags &&& BinaryFlag <> 0us) "value binary flag"

              let reader = Reader(columnDefPayload { Name = "elapsed"; Metadata = metadata })
              for _ in 1..6 do
                  reader.ReadLenEncString() |> ignore
              reader.ReadLenEncInt() |> ignore
              Expect.equal (reader.ReadInt16LE ()) BinaryCollation "binary collation"
              Expect.equal (reader.ReadInt32LE ()) 17 "display length"
              Expect.equal (reader.ReadByte ()) TypeTime "TIME type"
              Expect.isTrue (reader.ReadInt16LE () &&& int BinaryFlag <> 0) "wire binary flag"
              Expect.equal (reader.ReadByte ()) 6uy "fractional precision"

          testCase "binary protocol BLOB parameters decode as raw bytes"
          <| fun _ ->
              let bytes = [| 0x00uy; 0xffuy; 0x80uy |]
              let writer = Writer()
              writer.WriteLenEncBytes bytes
              Expect.equal (readBinaryValue (Reader(writer.ToArray())) TypeBlob false) (VBytes bytes) "raw BLOB parameter"

          testCase "binary protocol string parameters preserve non-UTF-8 bytes"
          <| fun _ ->
              let raw = [| 0x2fuy; 0xbbuy; 0x5fuy; 0xe2uy; 0xe2uy; 0x9auy; 0x4duy; 0x70uy; 0xaauy; 0x58uy; 0x54uy; 0xceuy; 0x7cuy; 0xe3uy; 0xe2uy; 0x0buy |]
              let binary = Writer()
              binary.WriteLenEncBytes raw

              Expect.equal
                  (readBinaryValue (Reader(binary.ToArray())) TypeVarString false)
                  (VBytes raw)
                  "binary string parameter"

              let text = Writer()
              text.WriteLenEncString "blåbær"

              Expect.equal
                  (readBinaryValue (Reader(text.ToArray())) TypeVarString false)
                  (VString "blåbær")
                  "UTF-8 string parameter"

          testCase "SQL packets preserve non-UTF-8 string literal bytes"
          <| fun _ ->
              let prefix = Text.Encoding.UTF8.GetBytes "INSERT INTO café VALUES (X'00', 'ok', '"
              let value = [| 0x01uy; 0xffuy; 0x27uy; 0x5cuy; 0x00uy |]
              let suffix = Text.Encoding.ASCII.GetBytes "')"
              let escapedValue = [| 0x01uy; 0xffuy; 0x5cuy; 0x27uy; 0x5cuy; 0x5cuy; 0x5cuy; 0x30uy |]

              Expect.equal
                  (decodeSqlBytes (Array.concat [ prefix; escapedValue; suffix ]))
                  "INSERT INTO café VALUES (X'00', 'ok', X'01FF275C00')"
                  "binary literal"

              Expect.equal (stringValueOfBytes value) (VBytes value) "raw value remains binary"

              Expect.throwsT<Text.DecoderFallbackException>
                  (fun () -> decodeSqlBytes (Array.append (Text.Encoding.ASCII.GetBytes "SELECT ") [| 0xffuy |]) |> ignore)
                  "invalid syntax bytes"

          testCase "binary protocol geometry parameters retain their SRID and WKB"
          <| fun _ ->
              let bytes = Convert.FromHexString "E61000000101000000000000000000F83F00000000000000C0"
              let writer = Writer()
              writer.WriteLenEncBytes bytes

              match readBinaryValue (Reader(writer.ToArray())) TypeGeometry false with
              | VGeometry geometry ->
                  Expect.equal geometry.Srid 4326 "SRID"
                  Expect.equal (geometryToText geometry) "POINT(1.5 -2)" "point"
              | other -> failtestf "expected geometry, got %A" other

          testCase "binary protocol geometry parameters reject malformed payloads"
          <| fun _ ->
              let writer = Writer()
              writer.WriteLenEncBytes(Convert.FromHexString "01020000000100000000000000000000000000000000000000")

              Expect.throwsT<GeometryError>
                  (fun () -> readBinaryValue (Reader(writer.ToArray())) TypeGeometry false |> ignore)
                  "one-point lines are not coerced to NULL"

          testCase "binary protocol BIT values use their length-encoded binary form"
          <| fun _ ->
              let payload = binaryRowPayload [ metadataOfType (TBit 9) ] [ Some "\000\001" ]
              let reader = Reader payload
              reader.ReadByte() |> ignore
              reader.ReadByte() |> ignore
              Expect.equal (readBinaryValue reader TypeBit false) (VBytes [| 0uy; 1uy |]) "bit bytes"

          testCase "wireTypeOfColumnType maps every declared-type family to its wire id"
          <| fun _ ->
              Expect.equal (wireTypeOfColumnType (TTinyInt false)) TypeTiny "tinyint"
              Expect.equal (wireTypeOfColumnType (TSmallInt false)) TypeShort "smallint"
              Expect.equal (wireTypeOfColumnType (TMediumInt false)) TypeLong "mediumint"
              Expect.equal (wireTypeOfColumnType (TInt false)) TypeLong "int"
              Expect.equal (wireTypeOfColumnType (TBigInt false)) TypeLongLong "bigint"
              Expect.equal (wireTypeOfColumnType (TBit 9)) TypeBit "bit"
              Expect.equal (wireTypeOfColumnType (TDecimal(10, 2, false))) TypeNewDecimal "decimal"
              Expect.equal (wireTypeOfColumnType (TDouble false)) TypeDouble "double"
              Expect.equal (wireTypeOfColumnType (TFloat false)) TypeFloat "float"
              Expect.equal (wireTypeOfColumnType TDate) TypeDate "date"
              Expect.equal (wireTypeOfColumnType (TDateTime 0)) TypeDateTime "datetime"
              Expect.equal (wireTypeOfColumnType (TTimestamp 0)) TypeTimestamp "timestamp"
              Expect.equal (wireTypeOfColumnType (TBinary 16)) TypeString "binary"
              Expect.equal (wireTypeOfColumnType (TVarBinary 16)) TypeVarString "varbinary"
              Expect.equal (wireTypeOfColumnType TTinyBlob) TypeBlob "tinyblob"
              Expect.equal (wireTypeOfColumnType TBlob) TypeBlob "blob"
              Expect.equal (wireTypeOfColumnType TMediumBlob) TypeBlob "mediumblob"
              Expect.equal (wireTypeOfColumnType TLongBlob) TypeBlob "longblob"
              // String families retain the distinctions carried by MySQL's protocol.
              Expect.equal (wireTypeOfColumnType (TVarchar 10)) TypeVarString "varchar"
              Expect.equal (wireTypeOfColumnType (TChar 10)) TypeString "char"
              Expect.equal (wireTypeOfColumnType TText) TypeBlob "text"
              Expect.equal (wireTypeOfColumnType (TEnum [ "a"; "b" ])) TypeString "enum"
              Expect.equal (wireTypeOfColumnType TBool) TypeTiny "boolean"
              Expect.equal (wireTypeOfColumnType TYear) TypeYear "year"
              Expect.equal (wireTypeOfColumnType (TSet [ "a" ])) TypeString "set"
              Expect.equal (wireTypeOfColumnType (TGeometry Point)) TypeGeometry "geometry"

          testCase "declared types report MySQL numeric and temporal metadata"
          <| fun _ ->
              let intMetadata = metadataOfType (TInt true)
              Expect.equal intMetadata.Flags (UnsignedFlag ||| NumFlag) "unsigned integer flags"

              let doubleMetadata = metadataOfType (TDouble false)
              Expect.equal doubleMetadata.Decimals 31uy "double decimals"
              Expect.isTrue (doubleMetadata.Flags &&& NumFlag <> 0us) "double numeric flag"

              let unsignedFloat = metadataOfType (TFloat true)
              Expect.isTrue (unsignedFloat.Flags &&& UnsignedFlag <> 0us) "unsigned float flag"

              let timestampMetadata = metadataOfType (TTimestamp 3)
              Expect.equal timestampMetadata.TypeId TypeTimestamp "timestamp wire type"
              Expect.equal timestampMetadata.Decimals 3uy "timestamp decimals"

              Expect.equal
                  timestampMetadata.Flags
                  (BinaryFlag ||| TimestampFlag)
                  "timestamp flags"

              let yearMetadata = metadataOfType TYear

              Expect.equal
                  yearMetadata.Flags
                  (UnsignedFlag ||| ZeroFillFlag ||| NumFlag)
                  "year flags"

          testCase "stored columns report key default and update flags"
          <| fun _ ->
              let required =
                  { Name = "n"
                    Type = TInt false
                    NumericDisplay = None
                    Nullable = false
                    Default = None
                    AutoIncrement = false
                    PrimaryKey = false
                    Unique = false
                    OnUpdateCurrentTimestamp = false
                    Generated = None
                    Comment = ""
                    Collation = None
                    Charset = None }

              let requiredMetadata = metadataOfColumn required
              Expect.isTrue (requiredMetadata.Flags &&& NotNullFlag <> 0us) "not null"
              Expect.isTrue (requiredMetadata.Flags &&& NoDefaultValueFlag <> 0us) "no default"

              let primaryMetadata = metadataOfColumn { required with PrimaryKey = true }
              Expect.isTrue (primaryMetadata.Flags &&& PrimaryKeyFlag <> 0us) "primary key"
              Expect.isTrue (primaryMetadata.Flags &&& PartKeyFlag <> 0us) "key part"

              let updatingTimestamp =
                  { required with
                      Type = TTimestamp 0
                      NumericDisplay = None
                      Nullable = true
                      Default = Some DCurrentTimestamp
                      OnUpdateCurrentTimestamp = true }

              let timestampMetadata = metadataOfColumn updatingTimestamp
              Expect.isTrue (timestampMetadata.Flags &&& OnUpdateNowFlag <> 0us) "on update"
              Expect.isFalse (timestampMetadata.Flags &&& NoDefaultValueFlag <> 0us) "has default"

              let index name unique columns =
                  { Name = name
                    KeyColumns = indexColumns columns
                    Unique = unique
                    Visible = true
                    Kind = BTree }

              let indexes =
                  [ index "uq_single" true [ "single" ]
                    index "uq_pair" true [ "first"; "second" ]
                    index "ix_plain" false [ "plain" ] ]

              let flags name =
                  metadataOfColumn { required with Name = name }
                  |> withIndexFlags indexes name
                  |> _.Flags

              Expect.isTrue (flags "single" &&& UniqueKeyFlag <> 0us) "single unique key"
              Expect.isTrue (flags "first" &&& MultipleKeyFlag <> 0us) "leading composite key"
              Expect.isTrue (flags "second" &&& MultipleKeyFlag = 0us) "non-leading composite key"
              Expect.isTrue (flags "second" &&& PartKeyFlag <> 0us) "composite key part"
              Expect.isTrue (flags "plain" &&& MultipleKeyFlag <> 0us) "non-unique key"

          testCase "textRowPayload encodes NULL and strings in one row"
          <| fun _ ->
              let reader = Reader(textRowPayload [ None; Some "hi"; Some "" ])
              Expect.equal (reader.ReadLenEncInt ()) None "NULL marker"
              Expect.equal (reader.ReadLenEncString ()) (Some "hi") "string value"
              Expect.equal (reader.ReadLenEncString ()) (Some "") "empty string"

          testCase "binary resultset DATETIME round-trips microseconds through the 11-byte form"
          <| fun _ ->
              // A DATETIME value carrying microseconds must reach the wire as
              // the 11-byte form, not silently collapse to the 7-byte
              // (second-precision) form. Encode a one-column binary row, skip
              // its header byte + 1-byte null bitmap, and decode the value.
              let payload = binaryRowPayload [ columnMetadata TypeDateTime ] [ Some "2024-03-05 13:45:09.123456" ]
              let r = Reader payload
              r.ReadByte() |> ignore // 0x00 row header
              r.ReadByte() |> ignore // null bitmap (1 column => 1 byte)

              Expect.equal
                  (readBinaryValue r TypeDateTime false)
                  (VDateTime(DateTime(2024, 3, 5, 13, 45, 9).AddTicks 1234560L))
                  "microseconds survive the binary resultset round-trip"

          testCase "binary resultset TIME encodes as the TIME wire form, not a string"
          <| fun _ ->
              // A declared TIME column reaches the wire as MySQL's binary TIME
              // form (readBinaryValue's TypeTime case decodes it), not a
              // length-encoded string. Covers microseconds, the >24h hour that
              // splits into a days field, and a negative duration.
              let roundTrip (s: string) =
                  let payload = binaryRowPayload [ columnMetadata TypeTime ] [ Some s ]
                  let r = Reader payload
                  r.ReadByte() |> ignore // row header
                  r.ReadByte() |> ignore // null bitmap
                  readBinaryValue r TypeTime false

              Expect.equal (roundTrip "10:20:30.126") (VTime(tryParseTimeValue "10:20:30.126000" |> Option.get)) "microseconds"
              Expect.equal (roundTrip "838:59:59") (VTime(tryParseTimeValue "838:59:59" |> Option.get)) "hours past 24 split into a days field"
              Expect.equal (roundTrip "-01:02:03") (VTime(tryParseTimeValue "-01:02:03" |> Option.get)) "negative duration"
              Expect.equal (roundTrip "00:00:00") (VTime(timeValueOrClamp 0L)) "zero time is the length-0 form"

          testCase "binary DATE/DATETIME/TIME decode length variants, zero values, and integer signs"
          <| fun _ ->
              // DATE (len=4): year, month, day
              let date = Writer()
              date.WriteByte 4uy
              date.WriteInt16LE 2024
              date.WriteByte 3uy
              date.WriteByte 5uy
              Expect.equal (readBinaryValue (Reader(date.ToArray())) TypeDate false) (VDate(DateOnly(2024, 3, 5))) "date"

              // length 0 is the all-zero datetime.
              let zero = Writer()
              zero.WriteByte 0uy
              let allZero = tryZeroDate 0 0 0 |> Option.get |> fun date -> tryZeroDateTime date 0 0 0 0 |> Option.get
              Expect.equal (readBinaryValue (Reader(zero.ToArray())) TypeDateTime false) (VZeroDateTime allZero) "zero datetime"

              // full DATETIME (len=11): year..second plus microseconds
              let dt = Writer()
              dt.WriteByte 11uy
              dt.WriteInt16LE 2024
              dt.WriteByte 3uy
              dt.WriteByte 5uy
              dt.WriteByte 13uy
              dt.WriteByte 45uy
              dt.WriteByte 9uy
              dt.WriteInt32LE 123456
              Expect.equal
                  (readBinaryValue (Reader(dt.ToArray())) TypeDateTime false)
                  (VDateTime(DateTime(2024, 3, 5, 13, 45, 9).AddTicks 1234560L))
                  "datetime with microseconds"

              // length 0 TIME is zero
              let tzero = Writer()
              tzero.WriteByte 0uy
              Expect.equal (readBinaryValue (Reader(tzero.ToArray())) TypeTime false) (VTime(timeValueOrClamp 0L)) "zero time"

              // negative TIME (len=12) with microseconds: 1 day + 10 hours
              let time = Writer()
              time.WriteByte 12uy
              time.WriteByte 1uy // negative
              time.WriteInt32LE 1 // days
              time.WriteByte 10uy // hour
              time.WriteByte 20uy
              time.WriteByte 30uy
              time.WriteInt32LE 123456
              Expect.equal (readBinaryValue (Reader(time.ToArray())) TypeTime false) (VTime(tryParseTimeValue "-34:20:30.123456" |> Option.get)) "negative time with microseconds"

              let invalidTime len sign days hour minute second micros =
                  let writer = Writer()
                  writer.WriteByte len

                  if len <> 0uy then
                      writer.WriteByte sign
                      writer.WriteInt32LE days
                      writer.WriteByte hour
                      writer.WriteByte minute
                      writer.WriteByte second

                      if len = 12uy then
                          writer.WriteInt32LE micros

                  Expect.throws (fun () -> readBinaryValue (Reader(writer.ToArray())) TypeTime false |> ignore) "invalid binary TIME is rejected"

              invalidTime 7uy 0uy 0 0uy 0uy 0uy 0
              invalidTime 8uy 2uy 0 0uy 0uy 0uy 0
              invalidTime 8uy 0uy 0 24uy 0uy 0uy 0
              invalidTime 12uy 0uy 0 0uy 0uy 0uy 1_000_000
              invalidTime 8uy 0uy -1 0uy 0uy 0uy 0
              invalidTime 12uy 0uy 34 22uy 59uy 59uy 1

              // TINYINT: 0xFF is -1 signed, 255 unsigned
              let tiny = Writer()
              tiny.WriteByte 0xFFuy
              Expect.equal (readBinaryValue (Reader(tiny.ToArray())) TypeTiny false) (VInt(-1L)) "tiny signed"
              Expect.equal (readBinaryValue (Reader(tiny.ToArray())) TypeTiny true) (VInt 255L) "tiny unsigned"

              // SMALLINT: 0xFFFF is -1 signed, 65535 unsigned
              let small = Writer()
              small.WriteInt16LE(-1)
              Expect.equal (readBinaryValue (Reader(small.ToArray())) TypeShort false) (VInt(-1L)) "short signed"
              Expect.equal (readBinaryValue (Reader(small.ToArray())) TypeShort true) (VInt 65535L) "short unsigned"

          testCase "binaryRowPayload writes DATETIME with length 7 when there's no sub-second part"
          <| fun _ ->
              let payload = binaryRowPayload [ columnMetadata TypeDateTime ] [ Some "2024-03-05 13:45:09" ]
              let r = Reader(payload)
              r.ReadBytes 2 |> ignore // row header byte + null bitmap
              Expect.equal (r.ReadByte ()) 7uy "no microseconds: the compact 7-byte form"

          testCase "binaryRowPayload writes the 11-byte DATETIME form when microseconds are non-zero"
          <| fun _ ->
              // Non-zero microseconds require the full 11-byte form — the
              // compact 7-byte form silently drops the sub-second precision
              // of a DATETIME(6)/TIMESTAMP(6) value.
              let payload = binaryRowPayload [ columnMetadata TypeDateTime ] [ Some "2024-03-05 13:45:09.123456" ]
              let r = Reader(payload)
              r.ReadBytes 2 |> ignore // row header byte + null bitmap
              Expect.equal (r.ReadByte ()) 11uy "microseconds present: the full 11-byte form"
              Expect.equal (r.ReadInt16LE ()) 2024 "year"
              Expect.equal (r.ReadByte ()) 3uy "month"
              Expect.equal (r.ReadByte ()) 5uy "day"
              Expect.equal (r.ReadByte ()) 13uy "hour"
              Expect.equal (r.ReadByte ()) 45uy "minute"
              Expect.equal (r.ReadByte ()) 9uy "second"
              Expect.equal (r.ReadInt32LE ()) 123456 "microseconds" ]
