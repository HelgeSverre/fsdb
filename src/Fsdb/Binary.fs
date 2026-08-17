/// Shared binary writer/reader + CRC-32. Extracted from `Packet` (which used
/// to own `Writer`/`Reader` for the wire protocol) so `Persistence` — which
/// compiles *before* `Packet` — can use the same primitives for the binary
/// WAL. Both are IO-free byte accumulators/readers.
module Fsdb.Binary

open System
open System.IO
open System.Text

/// Accumulates bytes. IO-free; call ToArray() when done.
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

    member this.WriteUInt32LE(v: uint32) =
        for i in 0..3 do
            this.WriteByte(byte ((v >>> (i * 8)) &&& 0xffffffffu))

    member this.WriteInt64LE(v: int64) =
        for i in 0..7 do
            this.WriteByte(byte ((v >>> (i * 8)) &&& 0xffL))

    /// IEEE 754 double, raw 8 bytes little-endian — `DoubleToInt64Bits`
    /// gets the raw bits platform-independently, then `WriteInt64LE`
    /// writes them out explicitly rather than trusting `BitConverter`'s
    /// own (platform-dependent) endianness.
    member this.WriteDoubleLE(v: float) = this.WriteInt64LE(BitConverter.DoubleToInt64Bits v)

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

    /// Length-encoded raw bytes. Unlike `WriteLenEncString`, this performs
    /// no character encoding and is therefore safe for BLOB/VARBINARY data.
    member this.WriteLenEncBytes(bytes: byte[]) =
        this.WriteLenEncInt(uint64 bytes.Length)
        this.WriteBytes bytes

    /// The NULL sentinel for a length-encoded value (0xfb).
    member this.WriteLenEncNull() = this.WriteByte 0xfbuy

    member _.ToArray() = buf.ToArray()

    /// Bytes accumulated so far — lets a streaming caller flush to disk in
    /// bounded chunks instead of one ever-growing `byte[]`.
    member _.Count = buf.Count

    /// Drops the accumulated bytes so the same `Writer` can be reused after a
    /// streaming flush.
    member _.Clear() = buf.Clear()

/// The read surface the persistence codecs need, so one decode implementation
/// runs over both an in-memory `Reader` (WAL replay) and a streamed
/// `StreamReader` (multi-GB snapshot load).
type IReader =
    abstract ReadByte: unit -> byte
    abstract ReadBytes: int -> byte[]
    abstract ReadInt32LE: unit -> int
    abstract ReadInt64LE: unit -> int64
    abstract ReadLenEncInt: unit -> uint64 option
    abstract ReadLenEncString: unit -> string option

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

    member this.ReadUInt32LE() =
        uint32 (this.ReadInt32LE())

    member this.ReadInt64LE() =
        let mutable v = 0L

        for i in 0..7 do
            v <- v ||| ((int64 (this.ReadByte())) <<< (i * 8))

        v

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

    interface IReader with
        member this.ReadByte() = this.ReadByte()
        member this.ReadBytes(n) = this.ReadBytes n
        member this.ReadInt32LE() = this.ReadInt32LE()
        member this.ReadInt64LE() = this.ReadInt64LE()
        member this.ReadLenEncInt() = this.ReadLenEncInt()
        member this.ReadLenEncString() = this.ReadLenEncString()

/// A `Reader` over a `Stream`, buffering internally so a multi-GB snapshot
/// decodes without ever being held in memory as one `byte[]`. Same `IReader`
/// surface the persistence codecs run against.
type StreamReader(stream: Stream) =
    let buf = Array.zeroCreate<byte> (1 <<< 16)
    let mutable pos = 0
    let mutable len = 0
    let mutable eof = false

    let ensure () =
        if pos >= len && not eof then
            len <- stream.Read(buf, 0, buf.Length)
            pos <- 0

            if len = 0 then
                eof <- true

    member _.ReadByte() : byte =
        ensure ()

        if pos >= len then
            raise (EndOfStreamException())

        let b = buf.[pos]
        pos <- pos + 1
        b

    member _.ReadBytes(n: int) : byte[] =
        let result = Array.zeroCreate<byte> n
        let mutable written = 0

        if pos < len then
            let take = min n (len - pos)
            Array.Copy(buf, pos, result, 0, take)
            pos <- pos + take
            written <- take

        while written < n do
            let read = stream.Read(result, written, n - written)

            if read = 0 then
                raise (EndOfStreamException())

            written <- written + read

        result

    member private this.ReadInt16LE() =
        let a = int (this.ReadByte())
        let b = int (this.ReadByte())
        a ||| (b <<< 8)

    member private this.ReadInt24LE() =
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

    member this.ReadInt64LE() =
        let mutable v = 0L

        for i in 0..7 do
            v <- v ||| ((int64 (this.ReadByte())) <<< (i * 8))

        v

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

    interface IReader with
        member this.ReadByte() = this.ReadByte()
        member this.ReadBytes(n) = this.ReadBytes n
        member this.ReadInt32LE() = this.ReadInt32LE()
        member this.ReadInt64LE() = this.ReadInt64LE()
        member this.ReadLenEncInt() = this.ReadLenEncInt()
        member this.ReadLenEncString() = this.ReadLenEncString()

// CRC-32 (IEEE 802.3), table-driven. Detects a torn/corrupt WAL record —
// corruption detection, not security.
let private crcTable =
    [| for n in 0..255 ->
           let mutable c = uint32 n

           for _ in 1..8 do
               c <- if (c &&& 1u) <> 0u then (c >>> 1) ^^^ 0xEDB88320u else c >>> 1

           c |]

let crc32 (data: byte[]) : uint32 =
    let mutable c = 0xFFFFFFFFu

    for b in data do
        c <- crcTable.[int (c ^^^ uint32 b) &&& 0xFF] ^^^ (c >>> 8)

    ~~~c
