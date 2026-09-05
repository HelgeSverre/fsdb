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
      Encoding: Lazy<Encoding>
      StrictEncoding: Lazy<Encoding>
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

let private codec name defaultCollation description maxBytes supportsLoadData encoding strictEncoding allowsSupplementary =
    { Info =
        { Name = name
          DefaultCollation = defaultCollation
          Description = description
          MaxBytesPerCharacter = maxBytes
          SupportsLoadData = supportsLoadData }
      Encoding = lazy encoding ()
      StrictEncoding = lazy strictEncoding ()
      AllowsSupplementaryCharacters = allowsSupplementary }

let private codecs =
    let legacy name collation description maxBytes codePage =
        codec name collation description maxBytes true (replacingCodePage codePage) (strictCodePage codePage) true

    [ legacy "ascii" "ascii_general_ci" "US ASCII" 1 20127
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
      legacy "euckr" "euckr_korean_ci" "EUC-KR Korean" 2 51949
      legacy "gb18030" "gb18030_chinese_ci" "China National Standard GB18030" 4 54936
      legacy "gbk" "gbk_chinese_ci" "GBK Simplified Chinese" 2 936
      legacy "greek" "greek_general_ci" "ISO 8859-7 Greek" 1 28597
      legacy "hebrew" "hebrew_general_ci" "ISO 8859-8 Hebrew" 1 28598
      legacy "koi8r" "koi8r_general_ci" "KOI8-R Relcom Russian" 1 20866
      legacy "koi8u" "koi8u_general_ci" "KOI8-U Ukrainian" 1 21866
      legacy "latin1" "latin1_swedish_ci" "cp1252 West European" 1 1252
      legacy "latin2" "latin2_general_ci" "ISO 8859-2 Central European" 1 28592
      legacy "latin5" "latin5_turkish_ci" "ISO 8859-9 Turkish" 1 28599
      legacy "latin7" "latin7_general_ci" "ISO 8859-13 Baltic" 1 28603
      legacy "macce" "macce_general_ci" "Mac Central European" 1 10029
      legacy "macroman" "macroman_general_ci" "Mac West European" 1 10000
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

let private canEncode (encoding: Encoding) (text: string) =
    try
        encoding.GetByteCount text |> ignore
        true
    with :? EncoderFallbackException ->
        false

let transcodeText (name: string) (text: string) =
    match tryCodec name with
    | None -> text
    | Some codec ->
        let strict = codec.StrictEncoding.Value
        let hasForbiddenSupplementary =
            not codec.AllowsSupplementaryCharacters
            && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF)

        let fullyRepresentable =
            not hasForbiddenSupplementary && canEncode strict text

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
                    if canEncode strict value then value else "?")
            |> String.concat ""

let encode (name: string) (text: string) =
    match tryCodec name with
    | Some codec -> codec.Encoding.Value.GetBytes(textAcceptedBy codec text)
    | None -> Encoding.UTF8.GetBytes text

let decodeBytes (name: string) (bytes: byte[]) =
    match tryCodec name with
    | Some codec -> codec.Encoding.Value.GetString bytes
    | None -> Encoding.UTF8.GetString bytes

let decodeLoadData (name: string) (bytes: byte[]) =
    match tryCodec name with
    | None -> Error(sprintf "Unsupported character set '%s'" (canonicalName name))
    | Some codec when not codec.Info.SupportsLoadData ->
        Error(sprintf "Unsupported character set '%s'" codec.Info.Name)
    | Some codec ->
        try
            let encoding = codec.StrictEncoding.Value

            let text = encoding.GetString bytes

            if not codec.AllowsSupplementaryCharacters
               && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF) then
                Error(sprintf "Invalid %s character string" codec.Info.Name)
            else
                Ok text
        with :? DecoderFallbackException ->
            Error(sprintf "Invalid %s character string" codec.Info.Name)
