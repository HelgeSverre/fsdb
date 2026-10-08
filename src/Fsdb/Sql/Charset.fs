module Fsdb.Charset

open System
open System.Text

type Info =
    { Name: string
      DefaultCollation: string
      Description: string
      MaxBytesPerCharacter: int
      SupportsLoadData: bool }

type private Codec =
    { Info: Info
      Encode: string -> byte[]
      TryEncode: string -> byte[] option
      Decode: byte[] -> string
      TryDecode: byte[] -> string option
      AllowsSupplementaryCharacters: bool }

let private codePagesReady =
    lazy Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

let private codePage (number: int) (encoderFallback: EncoderFallback) (decoderFallback: DecoderFallback) =
    codePagesReady.Force()
    Encoding.GetEncoding(number, encoderFallback, decoderFallback)

let private replacingCodePage number () =
    codePage number (EncoderReplacementFallback "?") (DecoderReplacementFallback "?")

let private strictCodePage number () =
    codePage number EncoderFallback.ExceptionFallback DecoderFallback.ExceptionFallback

let private utf8 () : Encoding =
    UTF8Encoding(false, false)

let private strictUtf8 () : Encoding =
    UTF8Encoding(false, true)

let private utf16 bigEndian strict () : Encoding =
    UnicodeEncoding(bigEndian, false, strict)

let private utf32 bigEndian strict () : Encoding =
    UTF32Encoding(bigEndian, false, strict)

let private tryEncode (encoding: Lazy<Encoding>) (text: string) =
    try
        Some(encoding.Value.GetBytes text)
    with :? EncoderFallbackException ->
        None

let private tryDecode (encoding: Lazy<Encoding>) (bytes: byte[]) =
    try
        Some(encoding.Value.GetString bytes)
    with :? DecoderFallbackException ->
        None

let private codec name defaultCollation description maxBytes supportsLoadData encoding strictEncoding allowsSupplementary =
    let encoding: Lazy<Encoding> = lazy encoding ()
    let strictEncoding: Lazy<Encoding> = lazy strictEncoding ()

    { Info =
        { Name = name
          DefaultCollation = defaultCollation
          Description = description
          MaxBytesPerCharacter = maxBytes
          SupportsLoadData = supportsLoadData }
      Encode = fun text -> encoding.Value.GetBytes text
      TryEncode = tryEncode strictEncoding
      Decode = fun bytes -> encoding.Value.GetString bytes
      TryDecode = tryDecode strictEncoding
      AllowsSupplementaryCharacters = allowsSupplementary }

let private singleByteCodec name defaultCollation description (highCodePoints: int[]) =
    if highCodePoints.Length <> 128 then
        invalidArg (nameof highCodePoints) "A single-byte charset must define bytes 0x80 through 0xFF"

    let byteByCodePoint =
        let ascii = [ 0..127 ] |> List.map (fun value -> value, byte value) |> Map.ofList

        highCodePoints
        |> Array.indexed
        |> Array.fold (fun bytes (index, codePoint) ->
            if codePoint = 0 || Map.containsKey codePoint bytes then
                bytes
            else
                Map.add codePoint (byte (index + 128)) bytes) ascii

    let tryEncodeText (text: string) =
        let bytes = ResizeArray<byte>()
        let mutable valid = true

        for rune in text.EnumerateRunes() do
            match Map.tryFind rune.Value byteByCodePoint with
            | Some value -> bytes.Add value
            | None -> valid <- false

        if valid then Some(bytes.ToArray()) else None

    let encodeText (text: string) =
        text.EnumerateRunes()
        |> Seq.map (fun rune -> Map.tryFind rune.Value byteByCodePoint |> Option.defaultValue (byte '?'))
        |> Array.ofSeq

    let codePoint value =
        if value < 0x80uy then int value else highCodePoints.[int value - 128]

    let decodeText bytes =
        bytes
        |> Array.map (fun value ->
            match codePoint value with
            | 0 -> "?"
            | value -> Rune(value).ToString())
        |> String.concat ""

    { Info =
        { Name = name
          DefaultCollation = defaultCollation
          Description = description
          MaxBytesPerCharacter = 1
          SupportsLoadData = true }
      Encode = encodeText
      TryEncode = tryEncodeText
      Decode = decodeText
      TryDecode =
        fun bytes ->
            if bytes |> Array.forall (fun value -> codePoint value <> 0) then Some(decodeText bytes) else None
      AllowsSupplementaryCharacters = true }

let private restrictedCodePageCodec name defaultCollation description maxBytes codePage accepts =
    let encoding = lazy replacingCodePage codePage ()
    let strictEncoding = lazy strictCodePage codePage ()

    let tryEncodeText text =
        tryEncode strictEncoding text
        |> Option.filter (accepts text)

    let replaceUnsupported (text: string) =
        text.EnumerateRunes()
        |> Seq.map (fun rune ->
            let value = rune.ToString()
            if tryEncodeText value |> Option.isSome then value else "?")
        |> String.concat ""

    let tryDecodeBytes bytes =
        tryDecode strictEncoding bytes
        |> Option.filter (fun text -> accepts text bytes)

    { Info =
        { Name = name
          DefaultCollation = defaultCollation
          Description = description
          MaxBytesPerCharacter = maxBytes
          SupportsLoadData = true }
      Encode = fun text -> encoding.Value.GetBytes(replaceUnsupported text)
      TryEncode = tryEncodeText
      Decode =
        fun bytes ->
            match tryDecode strictEncoding bytes with
            | Some text -> replaceUnsupported text
            | None -> encoding.Value.GetString bytes |> replaceUnsupported
      TryDecode = tryDecodeBytes
      AllowsSupplementaryCharacters = true }

let private tis620Codec =
    let encoding = lazy replacingCodePage 874 ()
    let strictEncoding = lazy strictCodePage 874 ()
    let isControl value = value >= 0x80 && value <= 0x9F

    let isDefinedByte value =
        value <= 0x9Fuy
        || (value >= 0xA1uy && value <= 0xDAuy)
        || (value >= 0xDFuy && value <= 0xFBuy)

    let isEncodedTextByte value =
        value <= 0x7Fuy
        || (value >= 0xA1uy && value <= 0xDAuy)
        || (value >= 0xDFuy && value <= 0xFBuy)

    let tryEncodeRune (rune: Rune) =
        if isControl rune.Value then
            Some [| byte rune.Value |]
        else
            tryEncode strictEncoding (rune.ToString())
            |> Option.filter (Array.forall isEncodedTextByte)

    let tryEncodeText (text: string) =
        let bytes = ResizeArray<byte>()
        let mutable valid = true

        for rune in text.EnumerateRunes() do
            match tryEncodeRune rune with
            | Some encoded -> bytes.AddRange encoded
            | None -> valid <- false

        if valid then Some(bytes.ToArray()) else None

    let encodeText (text: string) =
        text.EnumerateRunes()
        |> Seq.collect (fun rune -> tryEncodeRune rune |> Option.defaultValue [| byte '?' |])
        |> Array.ofSeq

    let decodeByte value =
        if isControl (int value) then
            string (char value)
        elif isDefinedByte value then
            encoding.Value.GetString [| value |]
        else
            "?"

    let decodeBytes bytes = bytes |> Array.map decodeByte |> String.concat ""

    { Info =
        { Name = "tis620"
          DefaultCollation = "tis620_thai_ci"
          Description = "TIS620 Thai"
          MaxBytesPerCharacter = 1
          SupportsLoadData = true }
      Encode = encodeText
      TryEncode = tryEncodeText
      Decode = decodeBytes
      TryDecode =
        fun bytes ->
            if bytes |> Array.forall isDefinedByte then Some(decodeBytes bytes) else None
      AllowsSupplementaryCharacters = true }

let private codecs =
    let legacy name collation description maxBytes codePage =
        codec name collation description maxBytes true (replacingCodePage codePage) (strictCodePage codePage) true

    let mapped name collation description =
        SingleByteCharset.tryFind name
        |> Option.map (singleByteCodec name collation description)
        |> Option.defaultWith (fun () -> invalidOp (sprintf "Missing character map for %s" name))

    let jis = lazy strictCodePage 20932 ()
    let standardJis text _ =
        let rec valid (bytes: byte[]) index =
            if index = bytes.Length then
                true
            elif bytes.[index] <= 0x7Fuy then
                valid bytes (index + 1)
            elif
                bytes.[index] = 0x8Euy
                && index + 1 < bytes.Length
                && bytes.[index + 1] >= 0xA1uy
                && bytes.[index + 1] <= 0xDFuy
            then
                valid bytes (index + 2)
            elif
                index + 1 < bytes.Length
                && ((bytes.[index] >= 0xA1uy && bytes.[index] <= 0xA8uy)
                    || (bytes.[index] >= 0xB0uy && bytes.[index] <= 0xF3uy)
                    || (bytes.[index] = 0xF4uy && bytes.[index + 1] <= 0xA6uy))
                && bytes.[index + 1] >= 0xA1uy
                && bytes.[index + 1] <= 0xFEuy
            then
                valid bytes (index + 2)
            else
                false

        tryEncode jis text |> Option.exists (fun bytes -> valid bytes 0)
    let gb2312Bytes _ (bytes: byte[]) =
        let rec valid index =
            if index = bytes.Length then
                true
            elif bytes.[index] <= 0x7Fuy then
                valid (index + 1)
            elif
                index + 1 < bytes.Length
                && bytes.[index] >= 0xA1uy
                && bytes.[index] <= 0xF7uy
                && bytes.[index + 1] >= 0xA1uy
                && bytes.[index + 1] <= 0xFEuy
            then
                valid (index + 2)
            else
                false

        valid 0

    [ mapped "armscii8" "armscii8_general_ci" "ARMSCII-8 Armenian"
      legacy "ascii" "ascii_general_ci" "US ASCII" 1 20127
      legacy "big5" "big5_chinese_ci" "Big5 Traditional Chinese" 2 950
      codec "binary" "binary" "Binary pseudo charset" 1 false utf8 strictUtf8 true
      legacy "cp1250" "cp1250_general_ci" "Windows Central European" 1 1250
      legacy "cp1251" "cp1251_general_ci" "Windows Cyrillic" 1 1251
      legacy "cp1256" "cp1256_general_ci" "Windows Arabic" 1 1256
      legacy "cp1257" "cp1257_general_ci" "Windows Baltic" 1 1257
      legacy "cp850" "cp850_general_ci" "DOS West European" 1 850
      legacy "cp852" "cp852_general_ci" "DOS Central European" 1 852
      legacy "cp866" "cp866_general_ci" "DOS Russian" 1 866
      legacy "cp932" "cp932_japanese_ci" "SJIS for Windows Japanese" 2 932
      mapped "dec8" "dec8_swedish_ci" "DEC West European"
      legacy "euckr" "euckr_korean_ci" "EUC-KR Korean" 2 51949
      legacy "gb18030" "gb18030_chinese_ci" "China National Standard GB18030" 4 54936
      restrictedCodePageCodec "gb2312" "gb2312_chinese_ci" "GB2312 Simplified Chinese" 2 20936 gb2312Bytes
      legacy "gbk" "gbk_chinese_ci" "GBK Simplified Chinese" 2 936
      mapped "geostd8" "geostd8_general_ci" "GEOSTD8 Georgian"
      legacy "greek" "greek_general_ci" "ISO 8859-7 Greek" 1 28597
      legacy "hebrew" "hebrew_general_ci" "ISO 8859-8 Hebrew" 1 28598
      mapped "hp8" "hp8_english_ci" "HP West European"
      mapped "keybcs2" "keybcs2_general_ci" "DOS Kamenicky Czech-Slovak"
      legacy "koi8r" "koi8r_general_ci" "KOI8-R Relcom Russian" 1 20866
      legacy "koi8u" "koi8u_general_ci" "KOI8-U Ukrainian" 1 21866
      legacy "latin1" "latin1_swedish_ci" "cp1252 West European" 1 1252
      legacy "latin2" "latin2_general_ci" "ISO 8859-2 Central European" 1 28592
      legacy "latin5" "latin5_turkish_ci" "ISO 8859-9 Turkish" 1 28599
      legacy "latin7" "latin7_general_ci" "ISO 8859-13 Baltic" 1 28603
      legacy "macce" "macce_general_ci" "Mac Central European" 1 10029
      legacy "macroman" "macroman_general_ci" "Mac West European" 1 10000
      restrictedCodePageCodec "sjis" "sjis_japanese_ci" "Shift-JIS Japanese" 2 932 standardJis
      legacy "swe7" "swe7_swedish_ci" "7bit Swedish" 1 20107
      tis620Codec
      codec "ucs2" "ucs2_general_ci" "UCS-2 Unicode" 2 false (utf16 true false) (utf16 true true) false
      legacy "ujis" "ujis_japanese_ci" "EUC-JP Japanese" 3 51932
      codec "utf16" "utf16_general_ci" "UTF-16 Unicode" 4 false (utf16 true false) (utf16 true true) true
      codec "utf16le" "utf16le_general_ci" "UTF-16LE Unicode" 4 false (utf16 false false) (utf16 false true) true
      codec "utf32" "utf32_general_ci" "UTF-32 Unicode" 4 false (utf32 true false) (utf32 true true) true
      codec "utf8mb3" "utf8mb3_general_ci" "UTF-8 Unicode" 3 true utf8 strictUtf8 false
      codec "utf8mb4" "utf8mb4_0900_ai_ci" "UTF-8 Unicode" 4 true utf8 strictUtf8 true ]

let private byName =
    codecs |> List.map (fun codec -> codec.Info.Name, codec) |> Map.ofList

let canonicalName (name: string) =
    match name.ToLowerInvariant() with
    | "utf8" -> "utf8mb3"
    | name -> name

let private tryCodec name =
    byName |> Map.tryFind (canonicalName name)

let all = codecs |> List.map _.Info

let tryFind name =
    tryCodec name |> Option.map _.Info

let defaultCollationName name =
    tryFind name |> Option.map _.DefaultCollation

let maxBytes name =
    tryFind name |> Option.map _.MaxBytesPerCharacter

let supportsLoadData name =
    tryFind name |> Option.exists _.SupportsLoadData

let private replaceSupplementaryCharacters (text: string) =
    let builder = StringBuilder(text.Length)

    for rune in text.EnumerateRunes() do
        if rune.Value <= 0xFFFF then
            builder.Append(char rune.Value) |> ignore
        else
            builder.Append '?' |> ignore

    builder.ToString()

let private textAcceptedBy (codec: Codec) (text: string) =
    if codec.AllowsSupplementaryCharacters then text else replaceSupplementaryCharacters text

let transcodeText (name: string) (text: string) =
    match tryCodec name with
    | None -> text
    | Some codec ->
        let hasForbiddenSupplementary =
            not codec.AllowsSupplementaryCharacters
            && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF)

        let fullyRepresentable =
            not hasForbiddenSupplementary && codec.TryEncode text |> Option.isSome

        if fullyRepresentable then
            text
        elif not codec.AllowsSupplementaryCharacters && hasForbiddenSupplementary then
            replaceSupplementaryCharacters text
        else
            text.EnumerateRunes()
            |> Seq.map (fun rune ->
                if not codec.AllowsSupplementaryCharacters && rune.Value > 0xFFFF then
                    "?"
                else
                    let value = rune.ToString()
                    if codec.TryEncode value |> Option.isSome then value else "?")
            |> String.concat ""

let encode (name: string) (text: string) =
    match tryCodec name with
    | Some codec -> codec.Encode(textAcceptedBy codec text)
    | None -> Encoding.UTF8.GetBytes text

let tryEncodeStrict (name: string) (text: string) =
    match tryCodec name with
    | Some codec when
        not codec.AllowsSupplementaryCharacters
        && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF)
        ->
        None
    | Some codec -> codec.TryEncode text
    | None -> tryEncode (lazy strictUtf8 ()) text

let decodeBytes (name: string) (bytes: byte[]) =
    match tryCodec name with
    | Some codec -> codec.Decode bytes
    | None -> Encoding.UTF8.GetString bytes

/// Fixed-width introducers left-pad binary literals to complete code units.
let padBinaryLiteral (name: string) (bytes: byte[]) =
    let width =
        match canonicalName name with
        | "ucs2" | "utf16" | "utf16le" -> 2
        | "utf32" -> 4
        | _ -> 1
    let padding = (width - bytes.Length % width) % width
    if padding = 0 then bytes
    else Array.append (Array.zeroCreate padding) bytes

let private unicodeScalarWidth charset (bytes: byte[]) offset =
    let remaining = bytes.Length - offset
    let input = ReadOnlySpan<byte>(bytes, offset, remaining)
    match charset with
    | "utf8mb3" | "utf8mb4" ->
        let mutable rune = Unchecked.defaultof<Rune>
        let mutable consumed = 0
        let status = Rune.DecodeFromUtf8(input, &rune, &consumed)
        if status = System.Buffers.OperationStatus.Done
           && (charset = "utf8mb4" || rune.Value <= 0xFFFF) then Some consumed
        else None
    | "utf16" | "utf16le" when remaining >= 2 ->
        let codeUnit index =
            let first, second = int bytes.[index], int bytes.[index + 1]
            if charset = "utf16le" then first ||| (second <<< 8)
            else (first <<< 8) ||| second
        let first = char (codeUnit offset)
        if Char.IsHighSurrogate first then
            if remaining >= 4 && Char.IsLowSurrogate(char (codeUnit (offset + 2))) then Some 4
            else None
        elif Char.IsLowSurrogate first then None
        else Some 2
    | "utf32" when remaining >= 4 ->
        let codePoint = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian input
        if codePoint <= 0x10FFFFu && Rune.IsValid(int codePoint) then Some 4 else None
    | _ -> None

let private hasLegacyByteRules = function
    | "sjis" | "cp932" | "big5" | "gbk" | "gb2312" | "euckr" | "ujis" | "gb18030" -> true
    | _ -> false

let private legacyCharacterWidth charset (bytes: byte[]) offset =
    let lead = bytes.[offset]
    let shiftJis = charset = "sjis" || charset = "cp932"
    if lead <= 0x7Fuy || (shiftJis && lead >= 0xA1uy && lead <= 0xDFuy) then Some 1
    elif charset = "gb18030" && lead >= 0x81uy && lead <= 0xFEuy && offset + 3 < bytes.Length
         && bytes.[offset + 1] >= 0x30uy && bytes.[offset + 1] <= 0x39uy
         && bytes.[offset + 2] >= 0x81uy && bytes.[offset + 2] <= 0xFEuy
         && bytes.[offset + 3] >= 0x30uy && bytes.[offset + 3] <= 0x39uy then Some 4
    elif charset = "ujis" && lead = 0x8Fuy && offset + 2 < bytes.Length
         && bytes.[offset + 1] >= 0xA1uy && bytes.[offset + 1] <= 0xFEuy
         && bytes.[offset + 2] >= 0xA1uy && bytes.[offset + 2] <= 0xFEuy then Some 3
    elif offset + 1 < bytes.Length then
        let trail = bytes.[offset + 1]
        let validPair =
            match charset with
            | "sjis" | "cp932" ->
                ((lead >= 0x81uy && lead <= 0x9Fuy) || (lead >= 0xE0uy && lead <= 0xFCuy))
                && ((trail >= 0x40uy && trail <= 0x7Euy) || (trail >= 0x80uy && trail <= 0xFCuy))
            | "big5" ->
                lead >= 0xA1uy && lead <= 0xF9uy
                && ((trail >= 0x40uy && trail <= 0x7Euy) || (trail >= 0xA1uy && trail <= 0xFEuy))
            | "gb2312" ->
                lead >= 0xA1uy && lead <= 0xF7uy && trail >= 0xA1uy && trail <= 0xFEuy
            | "euckr" ->
                lead >= 0x81uy && lead <= 0xFEuy && trail >= 0x81uy && trail <= 0xFEuy
            | "ujis" ->
                (lead >= 0xA1uy && lead <= 0xFEuy && trail >= 0xA1uy && trail <= 0xFEuy)
                || (lead = 0x8Euy && trail >= 0xA1uy && trail <= 0xDFuy)
            | "gbk" | "gb18030" ->
                lead >= 0x81uy && lead <= 0xFEuy
                && trail >= 0x40uy && trail <= 0xFEuy && trail <> 0x7Fuy
            | _ -> false
        if validPair then Some 2 else None
    else None

let private firstInvalidByte length characterWidth =
    let mutable offset = 0
    let mutable invalid = None
    while offset < length && invalid.IsNone do
        match characterWidth offset with
        | Some width -> offset <- offset + width
        | None -> invalid <- Some offset
    invalid

/// Invalid encoded bytes occupy individual characters; UCS2 uses code units.
let characterByteOffsets name (bytes: byte[]) =
    let charset = canonicalName name
    let offsets = ResizeArray<int>()
    let mutable offset = 0
    while offset < bytes.Length do
        offsets.Add offset
        let width =
            if charset = "ucs2" then min 2 (bytes.Length - offset)
            elif hasLegacyByteRules charset then
                legacyCharacterWidth charset bytes offset |> Option.defaultValue 1
            else unicodeScalarWidth charset bytes offset |> Option.defaultValue 1
        offset <- offset + width
    offsets.Add bytes.Length
    offsets.ToArray()

let textPreservesCharacterCount name bytes (text: string) =
    not (hasLegacyByteRules (canonicalName name))
    || (characterByteOffsets name bytes).Length - 1 = (text.EnumerateRunes() |> Seq.length)

let reverseCharacterBytes name (bytes: byte[]) =
    let offsets = characterByteOffsets name bytes
    let output = Array.zeroCreate<byte> bytes.Length
    let mutable destination = 0
    for index in offsets.Length - 2 .. -1 .. 0 do
        let length = offsets.[index + 1] - offsets.[index]
        Array.Copy(bytes, offsets.[index], output, destination, length)
        destination <- destination + length
    output

/// First malformed byte in UTF-8/16/32 hex or bit literals. Quoted strings
/// and UCS-2 have different validation rules in MySQL.
let tryInvalidUnicodeByteOffset (name: string) (bytes: byte[]) =
    let charset = canonicalName name

    match charset with
    | "utf8mb3" | "utf8mb4" | "utf16" | "utf16le" | "utf32" ->
        firstInvalidByte bytes.Length (unicodeScalarWidth charset bytes)
    | _ -> None

let tryInvalidBinaryLiteralByteOffset name (bytes: byte[]) =
    match canonicalName name with
    | charset when hasLegacyByteRules charset ->
        firstInvalidByte bytes.Length (fun offset ->
            // UJIS accepts this literal pair but string functions count each byte.
            if charset = "ujis" && offset + 1 < bytes.Length
               && bytes.[offset] = 0x8Euy && bytes.[offset + 1] = 0xA0uy then Some 2
            else legacyCharacterWidth charset bytes offset)
    | _ -> tryInvalidUnicodeByteOffset name bytes

/// UCS-2 code units remain accepted even when they are isolated surrogates.
let tryInvalidTextByteOffset name bytes =
    match canonicalName name with
    | "ascii" -> bytes |> Array.tryFindIndex (fun value -> value > 0x7Fuy)
    | _ -> tryInvalidUnicodeByteOffset name bytes

/// Conversion substitutes each invalid source byte while retaining valid sequences.
let decodeWithByteReplacement name (bytes: byte[]) =
    let charset = canonicalName name
    let output = StringBuilder()
    let mutable offset = 0
    let mutable validStart = 0
    let appendValid finish =
        if finish > validStart then
            output.Append(decodeBytes charset bytes.[validStart .. finish - 1]) |> ignore
    while offset < bytes.Length do
        let width =
            if charset = "ascii" then
                if bytes.[offset] < 0x80uy then Some 1 else None
            else unicodeScalarWidth charset bytes offset
        match width with
        | Some width -> offset <- offset + width
        | None ->
            appendValid offset
            output.Append '?' |> ignore
            offset <- offset + 1
            validStart <- offset
    appendValid offset
    output.ToString()

/// UCS2 converts each 16-bit code unit independently, including surrogates.
let ucs2ToUtf8 (bytes: byte[]) =
    let output = ResizeArray<byte>()
    for offset in 0 .. 2 .. bytes.Length - 2 do
        let value = (int bytes.[offset] <<< 8) ||| int bytes.[offset + 1]
        if value < 0x80 then output.Add(byte value)
        elif value < 0x800 then
            output.Add(byte (0xC0 ||| (value >>> 6)))
            output.Add(byte (0x80 ||| (value &&& 0x3F)))
        else
            output.Add(byte (0xE0 ||| (value >>> 12)))
            output.Add(byte (0x80 ||| ((value >>> 6) &&& 0x3F)))
            output.Add(byte (0x80 ||| (value &&& 0x3F)))
    output.ToArray()

let private bytePreview escapeAscii (bytes: byte[]) offset =
    let remaining = bytes.Length - offset
    let preview =
        bytes.[offset .. offset + min 6 remaining - 1]
        |> Array.map (fun value -> if not escapeAscii && value < 0x80uy then string (char value) else sprintf "\\x%02X" value)
        |> String.concat ""
    if remaining > 6 then preview + "..." else preview

let invalidBytePreview bytes offset = bytePreview false bytes offset

let wideBytePreview bytes offset = bytePreview true bytes offset

let decodeLoadData (name: string) (bytes: byte[]) =
    match tryCodec name with
    | None -> Error(sprintf "Unsupported character set '%s'" (canonicalName name))
    | Some codec when not codec.Info.SupportsLoadData ->
        Error(sprintf "Unsupported character set '%s'" codec.Info.Name)
    | Some codec ->
        match codec.TryDecode bytes with
        | Some text ->
            if not codec.AllowsSupplementaryCharacters
               && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF) then
                Error(sprintf "Invalid %s character string" codec.Info.Name)
            else
                Ok text
        | None ->
            Error(sprintf "Invalid %s character string" codec.Info.Name)
