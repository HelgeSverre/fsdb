module internal Fsdb.Zh0900Weights

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text

[<Struct>]
type private Weight =
    { Bytes: byte[]
      PrimaryEnd: int
      SecondaryEnd: int }

[<Struct>]
type private ImplicitRange =
    { First: int
      Last: int
      Primary: uint32 }

let private readWeight (bytes: byte[]) =
    let separator start =
        let mutable index = start
        while bytes[index] <> 0uy || bytes[index + 1] <> 0uy do
            index <- index + 2
        index

    let primary = separator 0
    { Bytes = bytes
      PrimaryEnd = primary
      SecondaryEnd = separator (primary + 2) }

let private table =
    lazy
        use resource = typeof<Weight>.Assembly.GetManifestResourceStream("Fsdb.Zh0900Weights.bin.gz")
        use compressed = new GZipStream(resource, CompressionMode.Decompress)
        use reader = new BinaryReader(compressed, Encoding.UTF8)

        if reader.ReadUInt32() <> 0x31485A46u then
            invalidOp "Invalid Chinese collation weight table"

        let rangeCount = reader.ReadInt32()
        let scalarCount = reader.ReadInt32()
        let contractionCount = reader.ReadInt32()
        let ranges =
            Array.init rangeCount (fun _ ->
                { First = reader.ReadInt32()
                  Last = reader.ReadInt32()
                  Primary = reader.ReadUInt32() })

        let scalars = Dictionary<int, Weight>(scalarCount)
        for _ in 1..scalarCount do
            let codepoint = reader.ReadInt32()
            let bytes = reader.ReadBytes(int (reader.ReadUInt16()))
            scalars.Add(codepoint, readWeight bytes)

        let contractions =
            Array.init contractionCount (fun _ ->
                let length = int (reader.ReadUInt16())
                let weightLength = int (reader.ReadUInt16())
                let text = Encoding.UTF8.GetString(reader.ReadBytes(length))
                text, readWeight (reader.ReadBytes(weightLength)))
            |> Array.groupBy (fun (text, _) -> text[0])
            |> Array.map (fun (first, entries) -> first, Array.sortByDescending (fun ((text: string), _) -> text.Length) entries)
            |> dict

        ranges, scalars, contractions

let private scalarWeight (ranges: ImplicitRange[]) (scalars: Dictionary<int, Weight>) codepoint =
    match scalars.TryGetValue(codepoint) with
    | true, weight -> weight
    | _ ->
        let mutable low = 0
        let mutable high = ranges.Length
        while low < high do
            let middle = low + (high - low) / 2
            if ranges[middle].Last < codepoint then low <- middle + 1
            else high <- middle

        let range = ranges[low]
        let primary = range.Primary + uint32 (codepoint - range.First)
        { Bytes =
            [| byte (primary >>> 24); byte (primary >>> 16); byte (primary >>> 8); byte primary
               0uy; 0uy; 0uy; 0x20uy; 0uy; 0uy; 0uy; 2uy |]
          PrimaryEnd = 4
          SecondaryEnd = 8 }

let weightOf (value: string) =
    if value.Length = 0 then Array.zeroCreate<byte> 4
    else
        let ranges, scalars, contractions = table.Value
        let segments = ResizeArray<Weight>()
        let mutable offset = 0
        let mutable primaryLength = 0
        let mutable secondaryLength = 0
        let mutable tertiaryLength = 0
        while offset < value.Length do
            let contraction =
                match contractions.TryGetValue(value[offset]) with
                | true, candidates ->
                    candidates
                    |> Array.tryFind (fun (text, _) -> value.AsSpan(offset).StartsWith(text.AsSpan(), StringComparison.Ordinal))
                | _ -> None

            let length, weight =
                match contraction with
                | Some(text, weight) -> text.Length, weight
                | None ->
                    let rune =
                        match Rune.TryGetRuneAt(value, offset) with
                        | true, rune -> rune
                        | _ -> Rune.ReplacementChar
                    rune.Utf16SequenceLength, scalarWeight ranges scalars rune.Value

            segments.Add(weight)
            offset <- offset + length
            primaryLength <- primaryLength + weight.PrimaryEnd
            secondaryLength <- secondaryLength + weight.SecondaryEnd - weight.PrimaryEnd - 2
            tertiaryLength <- tertiaryLength + weight.Bytes.Length - weight.SecondaryEnd - 2

        let result = Array.zeroCreate<byte> (primaryLength + secondaryLength + tertiaryLength + 4)
        let mutable primary = 0
        let mutable secondary = primaryLength + 2
        let mutable tertiary = secondary + secondaryLength + 2

        for weight in segments do
            let secondaryStart = weight.PrimaryEnd + 2
            let tertiaryStart = weight.SecondaryEnd + 2
            let secondarySize = weight.SecondaryEnd - secondaryStart
            let tertiarySize = weight.Bytes.Length - tertiaryStart
            Array.Copy(weight.Bytes, 0, result, primary, weight.PrimaryEnd)
            Array.Copy(weight.Bytes, secondaryStart, result, secondary, secondarySize)
            Array.Copy(weight.Bytes, tertiaryStart, result, tertiary, tertiarySize)
            primary <- primary + weight.PrimaryEnd
            secondary <- secondary + secondarySize
            tertiary <- tertiary + tertiarySize

        result

let compare a b =
    (weightOf a).AsSpan().SequenceCompareTo((weightOf b).AsSpan())
