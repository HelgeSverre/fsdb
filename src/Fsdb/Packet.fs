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

/// Chunk size for `readExactAsync`'s read loop — deliberately far smaller
/// than a packet's declared length can be (up to 16 MiB). A client that
/// sends a 4-byte header declaring a huge length and then no payload (or a
/// slow trickle) must not make the server allocate that whole length
/// up front; reading into a small buffer and growing a `MemoryStream` only
/// as bytes actually arrive keeps the allocation proportional to what was
/// really received.
let private readChunkSize = 64 * 1024

let private readExactAsync (stream: Stream) (n: int) : Async<byte[] option> =
    async {
        if n = 0 then
            return Some [||]
        else
            use ms = new MemoryStream(min n readChunkSize)
            let buf = Array.zeroCreate<byte> (min n readChunkSize)
            let mutable remaining = n
            let mutable eof = false

            while remaining > 0 && not eof do
                let! read = stream.ReadAsync(buf, 0, min remaining buf.Length) |> Async.AwaitTask

                if read = 0 then
                    eof <- true
                else
                    ms.Write(buf, 0, read)
                    remaining <- remaining - read

            return if eof then None else Some(ms.ToArray())
    }

/// The largest payload a single packet header can declare (2^24 - 1). A
/// payload of exactly this many bytes means "more packets follow" on the
/// wire — see `writePacketAsync` and `readPacketAsync`.
let maxPacketPayload = 0xffffff

/// Raised by `readPacketAsync` when a multi-packet payload would exceed
/// `Limits.maxAllowedPacket`, so a malicious or buggy client can't make the
/// server allocate unbounded memory by streaming 0xffffff-byte chunks
/// forever.
exception PacketTooLargeException of size: int

/// Reads one logical packet from a stream, or None on clean disconnect.
/// Reassembles packets split across the wire per the MySQL protocol: a
/// chunk of exactly maxPacketPayload bytes means "more packets follow"; the
/// terminating chunk is the first one shorter than that (possibly empty).
/// Returns the sequence id of the LAST fragment (each fragment consumes its
/// own sequence number on the wire, so the first fragment's id is already
/// "used up" by the time reassembly finishes) — callers computing the next
/// response seq id as `packet.SeqId + 1uy` need that last id, not the
/// first, or their reply's seq id collides with a fragment the client
/// already sent.
let readPacketAsync (stream: Stream) : Async<Packet option> =
    let rec loop (acc: byte[]) : Async<Packet option> =
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

                    if acc.Length > Limits.maxAllowedPacket then
                        raise (PacketTooLargeException acc.Length)

                    if len = maxPacketPayload then
                        return! loop acc
                    else
                        return Some { SeqId = seqId; Payload = acc }
        }

    loop [||]

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
        use timeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(float Limits.netWriteTimeoutSeconds))
        let payload = p.Payload
        let total = payload.Length
        let mutable offset = 0
        let mutable seqId = p.SeqId

        while total - offset >= maxPacketPayload do
            let chunk = payload.[offset .. offset + maxPacketPayload - 1]
            let bytes = frame { SeqId = seqId; Payload = chunk }
            do! stream.WriteAsync(bytes, 0, bytes.Length, timeout.Token) |> Async.AwaitTask
            seqId <- seqId + 1uy
            offset <- offset + maxPacketPayload

        let lastChunk = payload.[offset..]
        let bytes = frame { SeqId = seqId; Payload = lastChunk }
        do! stream.WriteAsync(bytes, 0, bytes.Length, timeout.Token) |> Async.AwaitTask
        return seqId + 1uy
    }
