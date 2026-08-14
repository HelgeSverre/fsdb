/// MySQL wire protocol packet framing: length-encoded integers/strings, a
/// binary writer/reader, and the 4-byte packet header (3-byte little-endian
/// length + 1-byte sequence id).
/// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_basic_packets.html
module Fsdb.Packet

open System
open System.IO
open System.Text

/// Accumulates bytes for one packet payload. IO-free; call ToArray() when done.
type Writer() =
    let buf = ResizeArray<byte>()

    member _.WriteByte(b: byte) = buf.Add b

    member _.WriteBytes(bs: byte[]) = buf.AddRange bs

    member this.WriteInt16LE(v: int) =
        this.WriteByte(byte (v &&& 0xff))
        this.WriteByte(byte ((v >>> 8) &&& 0xff))

    member this.WriteInt24LE(v: int) =
        for i in 0..2 do
            this.WriteByte(byte ((v >>> (i * 8)) &&& 0xff))

    member this.WriteInt32LE(v: int) =
        for i in 0..3 do
            this.WriteByte(byte ((v >>> (i * 8)) &&& 0xff))

    member this.WriteNullTerminatedString(s: string) =
        this.WriteBytes(Encoding.UTF8.GetBytes s)
        this.WriteByte 0uy

    /// Length-encoded integer per the MySQL protocol.
    member this.WriteLenEncInt(v: uint64) =
        if v < 251UL then
            this.WriteByte(byte v)
        elif v < 65536UL then
            this.WriteByte 0xfcuy
            this.WriteInt16LE(int v)
        elif v < 16777216UL then
            this.WriteByte 0xfduy
            this.WriteInt24LE(int v)
        else
            this.WriteByte 0xfeuy

            for i in 0..7 do
                this.WriteByte(byte ((v >>> (i * 8)) &&& 0xffUL))

    /// Length-encoded string: lenenc length prefix followed by raw UTF-8 bytes.
    member this.WriteLenEncString(s: string) =
        let bytes = Encoding.UTF8.GetBytes s
        this.WriteLenEncInt(uint64 bytes.Length)
        this.WriteBytes bytes

    /// The NULL sentinel for a length-encoded value (0xfb).
    member this.WriteLenEncNull() = this.WriteByte 0xfbuy

    member _.ToArray() = buf.ToArray()

/// Reads sequentially from a byte array. IO-free.
type Reader(data: byte[]) =
    let mutable pos = 0

    member _.Remaining = data.Length - pos

    member _.ReadByte() =
        let b = data.[pos]
        pos <- pos + 1
        b

    member _.ReadBytes(n: int) =
        let bs = data.[pos .. pos + n - 1]
        pos <- pos + n
        bs

    member this.ReadInt16LE() =
        let a = int (this.ReadByte())
        let b = int (this.ReadByte())
        a ||| (b <<< 8)

    member this.ReadInt24LE() =
        let a = int (this.ReadByte())
        let b = int (this.ReadByte())
        let c = int (this.ReadByte())
        a ||| (b <<< 8) ||| (c <<< 16)

    member this.ReadInt32LE() =
        let a = int (this.ReadByte())
        let b = int (this.ReadByte())
        let c = int (this.ReadByte())
        let d = int (this.ReadByte())
        a ||| (b <<< 8) ||| (c <<< 16) ||| (d <<< 24)

    member this.ReadNullTerminatedString() =
        let start = pos

        while data.[pos] <> 0uy do
            pos <- pos + 1

        let s = Encoding.UTF8.GetString(data, start, pos - start)
        pos <- pos + 1
        s

    /// None means the lenenc value was the NULL sentinel (0xfb).
    member this.ReadLenEncInt() : uint64 option =
        match this.ReadByte() with
        | 0xfbuy -> None
        | 0xfcuy -> Some(uint64 (this.ReadInt16LE()))
        | 0xfduy -> Some(uint64 (this.ReadInt24LE()))
        | 0xfeuy -> Some(BitConverter.ToUInt64(this.ReadBytes 8, 0))
        | b -> Some(uint64 b)

    member this.ReadLenEncString() : string option =
        this.ReadLenEncInt()
        |> Option.map (fun len -> Encoding.UTF8.GetString(this.ReadBytes(int len)))

    member this.ReadRestAsString() =
        let s = Encoding.UTF8.GetString(data, pos, data.Length - pos)
        pos <- data.Length
        s

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

/// Reads one packet from a stream, or None on clean disconnect.
let readPacketAsync (stream: Stream) : Async<Packet option> =
    async {
        match! readExactAsync stream 4 with
        | None -> return None
        | Some header ->
            let r = Reader(header)
            let len = r.ReadInt24LE()
            let seqId = r.ReadByte()

            match! readExactAsync stream len with
            | None -> return None
            | Some payload -> return Some { SeqId = seqId; Payload = payload }
    }

/// The largest payload a single packet header can declare (2^24 - 1). A
/// payload of exactly this many bytes means "more packets follow" on the
/// wire — see `writePacketAsync` and `readPacketAsync`.
let maxPacketPayload = 0xffffff

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
