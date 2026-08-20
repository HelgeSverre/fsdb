module Fsdb.Tests.ProtocolTests

open System
open Expecto
open Fsdb.Ast
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value

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
              // PDO's inTransaction() reads SERVER_STATUS_IN_TRANS directly
              // off this bit.
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
              // PDO/Doctrine branch on SQLSTATE (42000 -> syntax error, not
              // the generic HY000) to classify exceptions.
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "42000" "sqlstate for 1064"

          testCase "ERR payload for an unmapped code falls back to HY000"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 9999 "whatever"
              let sqlState = Text.Encoding.ASCII.GetString(payload, 4, 5)
              Expect.equal sqlState "HY000" "sqlstate fallback"

          testCase "packet writes stop after net_write_timeout"
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

          testCase "binary protocol BLOB parameters decode as raw bytes"
          <| fun _ ->
              let bytes = [| 0x00uy; 0xffuy; 0x80uy |]
              let writer = Writer()
              writer.WriteLenEncBytes bytes
              Expect.equal (readBinaryValue (Reader(writer.ToArray())) TypeBlob false) (VBytes bytes) "raw BLOB parameter"

          testCase "wireTypeOfColumnType maps every declared-type family to its wire id"
          <| fun _ ->
              Expect.equal (wireTypeOfColumnType (TTinyInt false)) TypeTiny "tinyint"
              Expect.equal (wireTypeOfColumnType (TSmallInt false)) TypeShort "smallint"
              Expect.equal (wireTypeOfColumnType (TMediumInt false)) TypeLong "mediumint"
              Expect.equal (wireTypeOfColumnType (TInt false)) TypeLong "int"
              Expect.equal (wireTypeOfColumnType (TBigInt false)) TypeLongLong "bigint"
              Expect.equal (wireTypeOfColumnType (TDecimal(10, 2))) TypeNewDecimal "decimal"
              Expect.equal (wireTypeOfColumnType TDouble) TypeDouble "double"
              Expect.equal (wireTypeOfColumnType TFloat) TypeFloat "float"
              Expect.equal (wireTypeOfColumnType TDate) TypeDate "date"
              Expect.equal (wireTypeOfColumnType (TDateTime 0)) TypeDateTime "datetime"
              Expect.equal (wireTypeOfColumnType (TTimestamp 0)) TypeDateTime "timestamp"
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

              Expect.equal (roundTrip "10:20:30.126") (VString "10:20:30.126000") "microseconds (padded to 6 on decode)"
              Expect.equal (roundTrip "838:59:59") (VString "838:59:59") "hours past 24 split into a days field"
              Expect.equal (roundTrip "-01:02:03") (VString "-01:02:03") "negative duration"
              Expect.equal (roundTrip "00:00:00") (VString "00:00:00") "zero time is the length-0 form"

          testCase "binary DATE/DATETIME/TIME decode length variants, zero values, and integer signs"
          <| fun _ ->
              // DATE (len=4): year, month, day
              let date = Writer()
              date.WriteByte 4uy
              date.WriteInt16LE 2024
              date.WriteByte 3uy
              date.WriteByte 5uy
              Expect.equal (readBinaryValue (Reader(date.ToArray())) TypeDate false) (VDate(DateOnly(2024, 3, 5))) "date"

              // length 0 is the zero date — clamped, not thrown
              let zero = Writer()
              zero.WriteByte 0uy
              Expect.equal (readBinaryValue (Reader(zero.ToArray())) TypeDateTime false) (VDateTime DateTime.MinValue) "zero datetime"

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

              // length 0 TIME renders the zero text form
              let tzero = Writer()
              tzero.WriteByte 0uy
              Expect.equal (readBinaryValue (Reader(tzero.ToArray())) TypeTime false) (VString "00:00:00") "zero time"

              // negative TIME (len=12) with microseconds: 1 day + 10 hours
              let time = Writer()
              time.WriteByte 12uy
              time.WriteByte 1uy // negative
              time.WriteInt32LE 1 // days
              time.WriteByte 10uy // hour
              time.WriteByte 20uy
              time.WriteByte 30uy
              time.WriteInt32LE 123456
              Expect.equal (readBinaryValue (Reader(time.ToArray())) TypeTime false) (VString "-34:20:30.123456") "negative time with microseconds"

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
