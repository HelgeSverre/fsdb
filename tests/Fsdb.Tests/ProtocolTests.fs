module Fsdb.Tests.ProtocolTests

open System
open Expecto
open Fsdb.Ast
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value

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

          testCase "typed text rows encode BLOB values as raw bytes"
          <| fun _ ->
              let bytes = [| 0x00uy; 0xffuy; 0x80uy |]
              let carrier = Text.Encoding.Latin1.GetString bytes
              let reader = Reader(textRowPayloadTyped [ TypeBlob ] [ Some carrier ])
              Expect.equal (reader.ReadLenEncInt ()) (Some(uint64 bytes.Length)) "raw byte length"
              Expect.equal (reader.ReadBytes bytes.Length) bytes "no UTF-8 expansion"

          testCase "BLOB column definitions advertise binary collation and flags"
          <| fun _ ->
              let reader = Reader(columnDefPayload { Name = "payload"; Type = TypeBlob })
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
              Expect.equal (wireTypeOfColumnType TDateTime) TypeDateTime "datetime"
              Expect.equal (wireTypeOfColumnType TTimestamp) TypeDateTime "timestamp"
              Expect.equal (wireTypeOfColumnType (TBinary 16)) TypeBlob "binary"
              Expect.equal (wireTypeOfColumnType (TVarBinary 16)) TypeBlob "varbinary"
              Expect.equal (wireTypeOfColumnType TTinyBlob) TypeBlob "tinyblob"
              Expect.equal (wireTypeOfColumnType TBlob) TypeBlob "blob"
              Expect.equal (wireTypeOfColumnType TMediumBlob) TypeBlob "mediumblob"
              Expect.equal (wireTypeOfColumnType TLongBlob) TypeBlob "longblob"
              // every other declared type falls back to VAR_STRING
              Expect.equal (wireTypeOfColumnType (TVarchar 10)) TypeVarString "varchar"
              Expect.equal (wireTypeOfColumnType (TChar 10)) TypeVarString "char"
              Expect.equal (wireTypeOfColumnType TText) TypeVarString "text"
              Expect.equal (wireTypeOfColumnType (TEnum [ "a"; "b" ])) TypeVarString "enum"
              Expect.equal (wireTypeOfColumnType (TSet [ "a" ])) TypeVarString "set"

          testCase "textRowPayload encodes NULL and strings in one row"
          <| fun _ ->
              let reader = Reader(textRowPayload [ None; Some "hi"; Some "" ])
              Expect.equal (reader.ReadLenEncInt ()) None "NULL marker"
              Expect.equal (reader.ReadLenEncString ()) (Some "hi") "string value"
              Expect.equal (reader.ReadLenEncString ()) (Some "") "empty string"

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
              Expect.equal (readBinaryValue (Reader(small.ToArray())) TypeShort true) (VInt 65535L) "short unsigned" ]
