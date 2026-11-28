module internal Fsdb.Uca0900Weights

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text

[<Struct>]
type private Weight =
    { Bytes: byte[]
      PrimaryEnd: int
      SecondaryEnd: int
      TertiaryEnd: int }

[<Struct>]
type private ImplicitRange =
    { First: int
      Last: int
      Primary: uint32 }

let private readWeight quaternary (bytes: byte[]) =
    let separator start =
        let mutable index = start
        while bytes[index] <> 0uy || bytes[index + 1] <> 0uy do
            index <- index + 2
        index

    let primary = separator 0
    let secondary = separator (primary + 2)
    { Bytes = bytes
      PrimaryEnd = primary
      SecondaryEnd = secondary
      TertiaryEnd = if quaternary then separator (secondary + 2) else bytes.Length }

let private loadTable quaternary resourceName signature =
    lazy
        use resource = typeof<Weight>.Assembly.GetManifestResourceStream(resourceName)
        use compressed = new GZipStream(resource, CompressionMode.Decompress)
        use reader = new BinaryReader(compressed, Encoding.UTF8)

        if reader.ReadUInt32() <> signature then
            invalidOp ("Invalid collation weight table " + resourceName)

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
            scalars.Add(codepoint, readWeight quaternary bytes)

        let contractions =
            Array.init contractionCount (fun _ ->
                let length = int (reader.ReadUInt16())
                let weightLength = int (reader.ReadUInt16())
                let text = Encoding.UTF8.GetString(reader.ReadBytes(length))
                text, readWeight quaternary (reader.ReadBytes(weightLength)))
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
          SecondaryEnd = 8
          TertiaryEnd = 12 }

let private chineseTable = loadTable false "Fsdb.Zh0900Weights.bin.gz" 0x31485A46u
let private accentSensitiveTable = loadTable false "Fsdb.AccentSensitive0900Weights.bin.gz" 0x31534146u
let private caseSensitiveTable = loadTable false "Fsdb.CaseSensitive0900Weights.bin.gz" 0x31534346u
let private japaneseTable = loadTable true "Fsdb.Japanese0900Weights.bin.gz" 0x31414A46u

let private japaneseLeadingMarkWeight first head pair =
    let suffix start finish headLength =
        let tailStart = min finish (start + headLength)
        pair.Bytes[tailStart .. finish - 1]
    let secondary = suffix (pair.PrimaryEnd + 2) pair.SecondaryEnd (head.SecondaryEnd - head.PrimaryEnd - 2)
    let tertiary = suffix (pair.SecondaryEnd + 2) pair.TertiaryEnd (head.TertiaryEnd - head.SecondaryEnd - 2)
    let fourth = suffix (pair.TertiaryEnd + 2) pair.Bytes.Length (max 0 (head.Bytes.Length - head.TertiaryEnd - 2))
    let separator = [| 0uy; 0uy |]
    let secondaryEnd = first.PrimaryEnd + 2 + secondary.Length
    { Bytes = Array.concat
          [ first.Bytes[0 .. first.PrimaryEnd - 1]; separator; secondary; separator; tertiary; separator; fourth ]
      PrimaryEnd = first.PrimaryEnd
      SecondaryEnd = secondaryEnd
      TertiaryEnd = secondaryEnd + 2 + tertiary.Length }

let private weightOf quaternary (ranges, scalars, contractions: IDictionary<char, (string * Weight)[]>) (value: string) =
    let separators = if quaternary then 6 else 4
    if value.Length = 0 then
        { Bytes = Array.zeroCreate<byte> separators; PrimaryEnd = 0; SecondaryEnd = 2; TertiaryEnd = 4 }
    else
        // MySQL restarts the scan for each weight level while retaining the
        // previous character. A leading mark can therefore use the last
        // character as context after the primary level.
        let leadingContext =
            if not quaternary || value.Length < 2 then None
            else
                let last = value[value.Length - 1]
                match contractions.TryGetValue last with
                | true, pairs ->
                    pairs |> Array.tryPick (fun (pair, weight) ->
                        if pair.Length = 2 && pair[1] = value[0] then
                            Some(scalarWeight ranges scalars (int last), weight)
                        else None)
                | _ -> None
        let segments = ResizeArray<Weight>()
        let mutable offset = 0
        let mutable primaryLength = 0
        let mutable secondaryLength = 0
        let mutable tertiaryLength = 0
        let mutable quaternaryLength = 0
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
                    let weight = scalarWeight ranges scalars rune.Value
                    let weight =
                        match leadingContext with
                        | Some(head, pair) when offset = 0 -> japaneseLeadingMarkWeight weight head pair
                        | _ -> weight
                    rune.Utf16SequenceLength, weight

            segments.Add(weight)
            offset <- offset + length
            primaryLength <- primaryLength + weight.PrimaryEnd
            secondaryLength <- secondaryLength + weight.SecondaryEnd - weight.PrimaryEnd - 2
            tertiaryLength <- tertiaryLength + weight.TertiaryEnd - weight.SecondaryEnd - 2
            quaternaryLength <- quaternaryLength + max 0 (weight.Bytes.Length - weight.TertiaryEnd - 2)

        let result = Array.zeroCreate<byte> (primaryLength + secondaryLength + tertiaryLength + quaternaryLength + separators)
        let mutable primary = 0
        let mutable secondary = primaryLength + 2
        let mutable tertiary = secondary + secondaryLength + 2
        let mutable fourth = tertiary + tertiaryLength + 2

        for weight in segments do
            let secondaryStart = weight.PrimaryEnd + 2
            let tertiaryStart = weight.SecondaryEnd + 2
            let secondarySize = weight.SecondaryEnd - secondaryStart
            let tertiarySize = weight.TertiaryEnd - tertiaryStart
            Array.Copy(weight.Bytes, 0, result, primary, weight.PrimaryEnd)
            Array.Copy(weight.Bytes, secondaryStart, result, secondary, secondarySize)
            Array.Copy(weight.Bytes, tertiaryStart, result, tertiary, tertiarySize)
            primary <- primary + weight.PrimaryEnd
            secondary <- secondary + secondarySize
            tertiary <- tertiary + tertiarySize
            let fourthStart = weight.TertiaryEnd + 2
            if fourthStart < weight.Bytes.Length then
                let size = weight.Bytes.Length - fourthStart
                Array.Copy(weight.Bytes, fourthStart, result, fourth, size)
                fourth <- fourth + size

        { Bytes = result; PrimaryEnd = primaryLength; SecondaryEnd = secondary; TertiaryEnd = tertiary }

let chineseWeight value = (weightOf false chineseTable.Value value).Bytes

let compareChinese a b =
    (chineseWeight a).AsSpan().SequenceCompareTo((chineseWeight b).AsSpan())

let accentSensitiveKey value =
    let weight = weightOf false accentSensitiveTable.Value value
    Convert.ToHexString(weight.Bytes, 0, weight.SecondaryEnd)

let caseSensitiveKey value =
    Convert.ToHexString((weightOf false caseSensitiveTable.Value value).Bytes)

let private japaneseSqlWeight kanaSensitive value =
    let weight = weightOf true japaneseTable.Value value
    let hasKanaWeights = weight.Bytes.Length > weight.TertiaryEnd + 2
    let length = if kanaSensitive && hasKanaWeights then weight.Bytes.Length else weight.TertiaryEnd
    struct (weight.Bytes, length)

let japaneseWeight kanaSensitive value =
    let struct (bytes, length) = japaneseSqlWeight kanaSensitive value
    if length = bytes.Length then bytes else Array.sub bytes 0 length

let compareJapanese kanaSensitive left right =
    let struct (leftBytes, leftLength) = japaneseSqlWeight kanaSensitive left
    let struct (rightBytes, rightLength) = japaneseSqlWeight kanaSensitive right
    leftBytes.AsSpan(0, leftLength).SequenceCompareTo(rightBytes.AsSpan(0, rightLength))

let japaneseFullTextKey kanaSensitive value =
    let weight = weightOf true japaneseTable.Value value
    let length = if kanaSensitive then weight.Bytes.Length else weight.TertiaryEnd
    Convert.ToHexString(weight.Bytes, 0, length)

let private prefixCompare levels (prefix: string) =
    let levelEnd (key: string) start =
        let mutable index = start
        while index < key.Length && not (key.AsSpan(index, 4).SequenceEqual("0000".AsSpan())) do
            index <- index + 4
        index

    let mutable start = 0
    let prefixLevels =
        Array.init levels (fun _ ->
            let finish = levelEnd prefix start
            let bounds = struct (start, finish - start)
            start <- finish + 4
            bounds)

    fun (key: string) ->
        let rec compareLevel level keyStart =
            let struct (prefixStart, length) = prefixLevels.[level]
            let finish = levelEnd key keyStart
            let keyLength = finish - keyStart
            let result = String.CompareOrdinal(prefix, prefixStart, key, keyStart, min length keyLength)
            if result <> 0 then result
            elif length > keyLength then 1
            elif level + 1 = levels then 0
            else compareLevel (level + 1) (finish + 4)
        compareLevel 0 0

let accentSensitivePrefixCompare prefix = prefixCompare 2 prefix
let caseSensitivePrefixCompare prefix = prefixCompare 3 prefix
let japanesePrefixCompare prefix = prefixCompare 4 prefix
