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
      Encoding: unit -> Encoding
      StrictEncoding: unit -> Encoding
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

let private codec name defaultCollation description maxBytes supportsLoadData encoding strictEncoding allowsSupplementary =
    { Info =
        { Name = name
          DefaultCollation = defaultCollation
          Description = description
          MaxBytesPerCharacter = maxBytes
          SupportsLoadData = supportsLoadData }
      Encoding = encoding
      StrictEncoding = strictEncoding
      AllowsSupplementaryCharacters = allowsSupplementary }

let private codecs =
    [ codec "utf8mb4" "utf8mb4_0900_ai_ci" "UTF-8 Unicode" 4 true utf8 strictUtf8 true
      codec "utf8mb3" "utf8mb3_general_ci" "UTF-8 Unicode" 3 true utf8 strictUtf8 false
      codec "latin1" "latin1_swedish_ci" "cp1252 West European" 1 true (replacingCodePage 1252) (strictCodePage 1252) true
      codec "ascii" "ascii_general_ci" "US ASCII" 1 true (replacingCodePage 20127) (strictCodePage 20127) true
      codec "binary" "binary" "Binary pseudo charset" 1 false utf8 strictUtf8 true ]

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
    text.EnumerateRunes()
    |> Seq.map (fun rune -> if rune.Value <= 0xFFFF then rune.ToString() else "?")
    |> String.concat ""

let private textAcceptedBy (codec: Codec) (text: string) =
    if codec.AllowsSupplementaryCharacters then text else replaceSupplementaryCharacters text

let transcodeText (name: string) (text: string) =
    match tryCodec name with
    | None -> text
    | Some codec ->
        let strict = codec.StrictEncoding()

        text.EnumerateRunes()
        |> Seq.map (fun rune ->
            if not codec.AllowsSupplementaryCharacters && rune.Value > 0xFFFF then
                "?"
            else
                let value = rune.ToString()

                try
                    strict.GetBytes value |> ignore
                    value
                with :? EncoderFallbackException ->
                    "?")
        |> String.concat ""

let encode (name: string) (text: string) =
    match tryCodec name with
    | Some codec -> codec.Encoding().GetBytes(textAcceptedBy codec text)
    | None -> Encoding.UTF8.GetBytes text

let decodeBytes (name: string) (bytes: byte[]) =
    match tryCodec name with
    | Some codec -> codec.Encoding().GetString bytes |> textAcceptedBy codec
    | None -> Encoding.UTF8.GetString bytes

let decodeLoadData (name: string) (bytes: byte[]) =
    match tryCodec name with
    | None -> Error(sprintf "Unsupported character set '%s'" (canonicalName name))
    | Some codec when not codec.Info.SupportsLoadData ->
        Error(sprintf "Unsupported character set '%s'" codec.Info.Name)
    | Some codec ->
        try
            let encoding = codec.StrictEncoding()

            let text = encoding.GetString bytes

            if not codec.AllowsSupplementaryCharacters
               && text.EnumerateRunes() |> Seq.exists (fun rune -> rune.Value > 0xFFFF) then
                Error(sprintf "Invalid %s character string" codec.Info.Name)
            else
                Ok text
        with :? DecoderFallbackException ->
            Error(sprintf "Invalid %s character string" codec.Info.Name)
