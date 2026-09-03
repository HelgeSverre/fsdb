module Fsdb.Compression

open System
open System.IO
open System.IO.Compression
open System.Threading
open System.Threading.Tasks
open Fsdb.Binary

let private compressedHeaderLength = 7
// Leaves room for zlib framing when incompressible input expands slightly.
let private maxInputChunk = 0xff0000
let private minimumCompressionLength = 50

let private readExactlyInto
    (stream: Stream)
    (buffer: byte[])
    offset
    count
    (cancellationToken: CancellationToken)
    =
    task {
        let mutable position = offset
        let endPosition = offset + count

        while position < endPosition do
            let! read = stream.ReadAsync(buffer, position, endPosition - position, cancellationToken)

            if read = 0 then
                raise (EndOfStreamException())

            position <- position + read
    }

let private readExact (stream: Stream) count (cancellationToken: CancellationToken) =
    task {
        let bytes = Array.zeroCreate<byte> count
        do! readExactlyInto stream bytes 0 count cancellationToken

        return bytes
    }

let private readHeader (stream: Stream) (cancellationToken: CancellationToken) =
    task {
        let bytes = Array.zeroCreate<byte> compressedHeaderLength
        let! firstByte = stream.ReadAsync(bytes, 0, 1, cancellationToken)

        if firstByte = 0 then
            return None
        else
            do! readExactlyInto stream bytes 1 (bytes.Length - 1) cancellationToken
            return Some bytes
    }

type private Codec =
    { Compress: byte[] -> CancellationToken -> Task<byte[]>
      Decompress: int -> byte[] -> CancellationToken -> Task<byte[]> }

let private decompressZlib (expectedLength: int) (payload: byte[]) (cancellationToken: CancellationToken) =
    task {
        use source = new MemoryStream(payload, false)
        use inflater = new ZLibStream(source, CompressionMode.Decompress)
        let bytes = Array.zeroCreate<byte> expectedLength
        let mutable offset = 0

        while offset < bytes.Length do
            let! read = inflater.ReadAsync(bytes, offset, bytes.Length - offset, cancellationToken)

            if read = 0 then
                raise (InvalidDataException("Compressed packet is shorter than its header"))

            offset <- offset + read

        let extra = Array.zeroCreate<byte> 1
        let! extraLength = inflater.ReadAsync(extra, 0, 1, cancellationToken)

        if extraLength <> 0 then
            raise (InvalidDataException("Compressed packet is longer than its header"))

        return bytes
    }

let private compressZlib (payload: byte[]) (cancellationToken: CancellationToken) =
    task {
        use output = new MemoryStream()

        do!
            task {
                use compressor = new ZLibStream(output, CompressionLevel.Fastest, true)
                do! compressor.WriteAsync(payload, 0, payload.Length, cancellationToken)
            }

        return output.ToArray()
    }

let private zlib =
    { Compress = compressZlib
      Decompress = decompressZlib }

/// Translates MySQL compressed packets to the ordinary packet byte stream.
type CompressedStream(inner: Stream, leaveOpen: bool) =
    inherit Stream()

    let codec = zlib
    let mutable readBuffer = Array.empty<byte>
    let mutable readOffset = 0
    let mutable sequenceId = 0uy

    member _.BeginCommand() =
        if readOffset <> readBuffer.Length then
            raise (InvalidDataException("Compressed command ended with unread bytes"))

        sequenceId <- 0uy

    member private _.ReadFrameAsync(cancellationToken: CancellationToken) =
        task {
            match! readHeader inner cancellationToken with
            | None -> return false
            | Some header ->
                let reader = Reader header
                let payloadLength = reader.ReadInt24LE()
                let packetSequence = reader.ReadByte()
                let uncompressedLength = reader.ReadInt24LE()

                if packetSequence <> sequenceId then
                    raise (InvalidDataException("Compressed packets arrived out of order"))

                let! payload = readExact inner payloadLength cancellationToken

                let! bytes =
                    if uncompressedLength = 0 then
                        Task.FromResult payload
                    else
                        codec.Decompress uncompressedLength payload cancellationToken

                readBuffer <- bytes
                readOffset <- 0
                sequenceId <- sequenceId + 1uy
                return true
        }

    member private this.ReadCoreAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) =
        task {
            if count = 0 then
                return 0
            else
                let! available =
                    if readOffset = readBuffer.Length then
                        this.ReadFrameAsync cancellationToken
                    else
                        Task.FromResult true

                if not available then
                    return 0
                else
                    let copied = min count (readBuffer.Length - readOffset)
                    Array.Copy(readBuffer, readOffset, buffer, offset, copied)
                    readOffset <- readOffset + copied
                    return copied
        }

    member private _.WriteFrameAsync(payload: byte[], cancellationToken: CancellationToken) =
        task {
            let! compressed =
                if payload.Length < minimumCompressionLength then
                    Task.FromResult Array.empty<byte>
                else
                    codec.Compress payload cancellationToken

            let useCompressed = compressed.Length > 0 && compressed.Length < payload.Length
            let body = if useCompressed then compressed else payload
            let header = Writer()
            header.WriteInt24LE body.Length
            header.WriteByte sequenceId
            header.WriteInt24LE(if useCompressed then payload.Length else 0)
            let header = header.ToArray()
            do! inner.WriteAsync(header, 0, header.Length, cancellationToken)
            do! inner.WriteAsync(body, 0, body.Length, cancellationToken)
            sequenceId <- sequenceId + 1uy
        }

    member private this.WriteCoreAsync(buffer: byte[], offset: int, count: int, cancellationToken: CancellationToken) =
        task {
            let mutable sourceOffset = offset
            let mutable remaining = count

            while remaining > 0 do
                let length = min remaining maxInputChunk
                let payload = Array.sub buffer sourceOffset length
                do! this.WriteFrameAsync(payload, cancellationToken)
                sourceOffset <- sourceOffset + length
                remaining <- remaining - length
        }

    override _.CanRead = inner.CanRead
    override _.CanSeek = false
    override _.CanWrite = inner.CanWrite
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = inner.Flush()
    override _.FlushAsync cancellationToken = inner.FlushAsync cancellationToken
    override this.Read(buffer, offset, count) = this.ReadCoreAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult()
    override this.ReadAsync(buffer, offset, count, cancellationToken) = this.ReadCoreAsync(buffer, offset, count, cancellationToken)
    override this.Write(buffer, offset, count) = this.WriteCoreAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult()
    override this.WriteAsync(buffer, offset, count, cancellationToken) = this.WriteCoreAsync(buffer, offset, count, cancellationToken) :> Task
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())

    override _.Dispose disposing =
        if disposing && not leaveOpen then
            inner.Dispose()

        base.Dispose disposing
