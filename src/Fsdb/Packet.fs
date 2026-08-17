/// MySQL wire protocol packet framing: length-encoded integers/strings, a
/// binary writer/reader, and the 4-byte packet header (3-byte little-endian
/// length + 1-byte sequence id).
/// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_basic_packets.html
module Fsdb.Packet

open System
open System.IO
open System.Text
open Fsdb.Binary

/// One protocol packet: a payload tagged with its sequence id.
type Packet = { SeqId: byte; Payload: byte[] }

/// Frames a packet as bytes ready to write to the socket.
let frame (p: Packet) : byte[] =
    let w = Writer()
    w.WriteInt24LE p.Payload.Length
    w.WriteByte p.SeqId
    w.WriteBytes p.Payload
    w.ToArray()

let private readExactAsync (stream: Stream) (n: int) : Async<byte[] option> =
    async {
        if n = 0 then
            return Some [||]
        else
            let buf = Array.zeroCreate<byte> n
            let mutable total = 0
            let mutable eof = false

            while total < n && not eof do
                let! read = stream.ReadAsync(buf, total, n - total) |> Async.AwaitTask

                if read = 0 then eof <- true else total <- total + read

            return if eof then None else Some buf
    }

/// The largest payload a single packet header can declare (2^24 - 1). A
/// payload of exactly this many bytes means "more packets follow" on the
/// wire — see `writePacketAsync` and `readPacketAsync`.
let maxPacketPayload = 0xffffff

/// Raised by `readPacketAsync` when a multi-packet payload would exceed
/// `maxAccumulatedPacketSize`, so a malicious or buggy client can't make the
/// server allocate unbounded memory by streaming 0xffffff-byte chunks
/// forever.
exception PacketTooLargeException of size: int

/// Safety ceiling for a reassembled multi-packet payload — matches MySQL's
/// common `max_allowed_packet` default. ponytail: hardcoded rather than
/// wired to the `max_allowed_packet` session variable (Packet.fs can't
/// depend on Session.fs — Session already depends on Protocol which depends
/// on Packet); revisit if per-connection tuning is ever needed.
let maxAccumulatedPacketSize = 64 * 1024 * 1024 // 64 MiB

/// Reads one logical packet from a stream, or None on clean disconnect.
/// Reassembles packets split across the wire per the MySQL protocol: a
/// chunk of exactly maxPacketPayload bytes means "more packets follow"; the
/// terminating chunk is the first one shorter than that (possibly empty).
/// Returns the sequence id of the FIRST fragment, so callers computing the
/// next response seq id (`packet.SeqId + 1uy`) stay correct.
let readPacketAsync (stream: Stream) : Async<Packet option> =
    let rec loop (firstSeqId: byte option) (acc: byte[]) : Async<Packet option> =
        async {
            match! readExactAsync stream 4 with
            | None -> return None
            | Some header ->
                let r = Reader(header)
                let len = r.ReadInt24LE()
                let seqId = r.ReadByte()

                match! readExactAsync stream len with
                | None -> return None
                | Some payload ->
                    let acc = Array.append acc payload

                    if acc.Length > maxAccumulatedPacketSize then
                        raise (PacketTooLargeException acc.Length)

                    let firstSeqId = firstSeqId |> Option.defaultValue seqId

                    if len = maxPacketPayload then
                        return! loop (Some firstSeqId) acc
                    else
                        return Some { SeqId = firstSeqId; Payload = acc }
        }

    loop None [||]

/// Writes one logical packet to a stream, splitting the payload into
/// maxPacketPayload-byte chunks with incrementing sequence ids if it's too
/// big for a single packet (`frame`'s 3-byte length prefix can't represent
/// more than that; naively calling `frame` on an oversized payload silently
/// truncated the declared length and desynced the connection forever). A
/// payload whose length is an exact multiple of maxPacketPayload — including
/// zero — still gets a final (possibly empty) packet, since a chunk of
/// exactly maxPacketPayload bytes signals "more data follows".
/// Returns the next free sequence id, so callers writing several packets in
/// a row don't have to assume one payload == one seq id.
let writePacketAsync (stream: Stream) (p: Packet) : Async<byte> =
    async {
        let payload = p.Payload
        let total = payload.Length
        let mutable offset = 0
        let mutable seqId = p.SeqId

        while total - offset >= maxPacketPayload do
            let chunk = payload.[offset .. offset + maxPacketPayload - 1]
            let bytes = frame { SeqId = seqId; Payload = chunk }
            do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
            seqId <- seqId + 1uy
            offset <- offset + maxPacketPayload

        let lastChunk = payload.[offset..]
        let bytes = frame { SeqId = seqId; Payload = lastChunk }
        do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
        return seqId + 1uy
    }
