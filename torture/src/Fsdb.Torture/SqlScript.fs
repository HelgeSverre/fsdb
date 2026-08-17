namespace Fsdb.Torture

open System
open System.Text

[<CLIMutable>]
type SqlStatement =
    { Index: int
      StartByte: int
      EndByte: int
      Sha256: string
      Text: string }

[<CLIMutable>]
type ScriptError =
    { ByteOffset: int
      Message: string }

[<RequireQualifiedAccess>]
module SqlScript =
    type private State =
        | Normal
        | SingleQuote
        | DoubleQuote
        | Backtick
        | LineComment
        | BlockComment

    let private isSpace (value: byte) =
        value = byte ' ' || value = byte '\t' || value = byte '\r' || value = byte '\n' || value = 0x0Buy || value = 0x0Cuy

    let private trimmedBounds (bytes: byte array) startExclusive endExclusive =
        let mutable first = startExclusive
        let mutable last = endExclusive

        while first < last && isSpace bytes.[first] do
            first <- first + 1

        while last > first && isSpace bytes.[last - 1] do
            last <- last - 1

        first, last

    let private isDelimiterDirective (bytes: byte array) index =
        let keyword = "DELIMITER"

        let startsAtLineBoundary =
            let mutable cursor = index - 1
            let mutable onlyIndent = true

            while cursor >= 0 && bytes.[cursor] <> byte '\n' do
                if bytes.[cursor] <> byte ' ' && bytes.[cursor] <> byte '\t' && bytes.[cursor] <> byte '\r' then
                    onlyIndent <- false

                cursor <- cursor - 1

            onlyIndent

        let keywordMatches =
            index + keyword.Length <= bytes.Length
            && Seq.forall2
                (fun actual expected -> Char.ToUpperInvariant(char actual) = expected)
                bytes.[index .. index + keyword.Length - 1]
                keyword

        let hasBoundary =
            index + keyword.Length <= bytes.Length
            && (index + keyword.Length = bytes.Length || isSpace bytes.[index + keyword.Length])

        startsAtLineBoundary && keywordMatches && hasBoundary

    let splitBytes (bytes: byte array) : Result<SqlStatement array, ScriptError> =
        let statements = ResizeArray<SqlStatement>()
        let mutable state = Normal
        let mutable stateStart = 0
        let mutable segmentStart = 0
        let mutable index = 0

        let addStatement segmentEnd =
            let first, last = trimmedBounds bytes segmentStart segmentEnd

            if first < last then
                let slice = bytes.[first .. last - 1]

                try
                    let text = UTF8Encoding(false, true).GetString slice

                    statements.Add
                        { Index = statements.Count
                          StartByte = first
                          EndByte = last
                          Sha256 = Hashing.bytes slice
                          Text = text }

                    Ok()
                with :? DecoderFallbackException as error ->
                    Error
                        { ByteOffset = first + error.Index
                          Message = "generated SQL is not valid UTF-8" }
            else
                Ok()

        let mutable failure: ScriptError option = None

        while index < bytes.Length && failure.IsNone do
            let current = bytes.[index]
            let next = if index + 1 < bytes.Length then Some bytes.[index + 1] else None

            match state with
            | Normal ->
                if (current = byte 'D' || current = byte 'd') && isDelimiterDirective bytes index then
                    failure <-
                        Some
                            { ByteOffset = index
                              Message = "DELIMITER scripts are outside the generated-dump scanner contract" }
                else
                    match char current, next |> Option.map char with
                    | '\'', _ ->
                        state <- SingleQuote
                        stateStart <- index
                        index <- index + 1
                    | '"', _ ->
                        state <- DoubleQuote
                        stateStart <- index
                        index <- index + 1
                    | '`', _ ->
                        state <- Backtick
                        stateStart <- index
                        index <- index + 1
                    | '#', _ ->
                        state <- LineComment
                        index <- index + 1
                    | '-', Some '-' when index + 2 >= bytes.Length || isSpace bytes.[index + 2] ->
                        state <- LineComment
                        index <- index + 2
                    | '/', Some '*' ->
                        state <- BlockComment
                        stateStart <- index
                        index <- index + 2
                    | ';', _ ->
                        match addStatement (index + 1) with
                        | Ok() ->
                            segmentStart <- index + 1
                            index <- index + 1
                        | Error error -> failure <- Some error
                    | _ -> index <- index + 1
            | LineComment ->
                if current = byte '\n' then
                    state <- Normal

                index <- index + 1
            | BlockComment ->
                if current = byte '*' && next = Some(byte '/') then
                    state <- Normal
                    index <- index + 2
                else
                    index <- index + 1
            | SingleQuote
            | DoubleQuote
            | Backtick as quotedState ->
                let quote =
                    match quotedState with
                    | SingleQuote -> byte '\''
                    | DoubleQuote -> byte '"'
                    | Backtick -> byte '`'
                    | _ -> failwith "unreachable"

                if quotedState <> Backtick && current = byte '\\' && index + 1 < bytes.Length then
                    index <- index + 2
                elif current = quote && next = Some quote then
                    index <- index + 2
                elif current = quote then
                    state <- Normal
                    index <- index + 1
                else
                    index <- index + 1

        match failure with
        | Some error -> Error error
        | None ->
            match state with
            | SingleQuote -> Error { ByteOffset = stateStart; Message = "unterminated single-quoted string" }
            | DoubleQuote -> Error { ByteOffset = stateStart; Message = "unterminated double-quoted string" }
            | Backtick -> Error { ByteOffset = stateStart; Message = "unterminated backtick identifier" }
            | BlockComment -> Error { ByteOffset = stateStart; Message = "unterminated block comment" }
            | Normal
            | LineComment ->
                match addStatement bytes.Length with
                | Error error -> Error error
                | Ok() -> Ok(statements.ToArray())

    let splitText (text: string) = text |> Encoding.UTF8.GetBytes |> splitBytes
