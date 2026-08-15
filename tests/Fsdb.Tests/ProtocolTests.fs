module Fsdb.Tests.ProtocolTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol

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
              Expect.equal resp.Database None "no database requested" ]
