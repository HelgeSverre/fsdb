/// LOAD DATA decoding is independent of where the byte stream originated.
module Fsdb.LoadData

open System
open System.IO
open System.Text
open Fsdb.Ast
open Fsdb.Value

let private singleCharacter (value: string) =
    Seq.tryExactlyOne value

let private secureFileError =
    1290,
    "The MySQL server is running with the --secure-file-priv option so it cannot execute this statement"

let private pathComparison =
    if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

let private allowedPath policy (path: string) =
    try
        let candidate = ServerOptions.canonicalPath path

        match policy with
        | ServerOptions.SecureFilePolicy.Disabled -> Error secureFileError
        | ServerOptions.SecureFilePolicy.Unrestricted -> Ok candidate
        | ServerOptions.SecureFilePolicy.Directory root ->
            let root = ServerOptions.normalizeSecureFileDirectory root

            if candidate.StartsWith(root, pathComparison) then
                Ok candidate
            else
                Error secureFileError
    with
    | :? ArgumentException
    | :? NotSupportedException
    | :? PathTooLongException -> Error secureFileError
    | :? UnauthorizedAccessException -> Error(13, sprintf "Can't get stat of '%s' (OS errno 13 - Permission denied)" path)
    | :? IOException as error -> Error(29, sprintf "Error reading file '%s': %s" path error.Message)

/// Opens a server-owned input only after applying `secure_file_priv`, then
/// bounds both the initial size and growth after the open.
let readServerFile policy maxBytes path : Result<byte[], int * string> =
    allowedPath policy path
    |> Result.bind (fun fullPath ->
        try
            use input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)

            if input.Length > int64 maxBytes then
                Error(1153, "LOAD DATA INFILE exceeds max_load_data_bytes")
            else
                use output = new MemoryStream(int input.Length)
                let buffer = Array.zeroCreate<byte> 81920
                let mutable read = input.Read(buffer, 0, buffer.Length)
                let mutable overflow = false

                while read > 0 && not overflow do
                    if output.Length + int64 read > int64 maxBytes then
                        overflow <- true
                    else
                        output.Write(buffer, 0, read)
                        read <- input.Read(buffer, 0, buffer.Length)

                if overflow then
                    Error(1153, "LOAD DATA INFILE exceeds max_load_data_bytes")
                else
                    Ok(output.ToArray())
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> Error(13, sprintf "Can't get stat of '%s' (OS errno 2 - No such file or directory)" fullPath)
        | :? UnauthorizedAccessException -> Error(13, sprintf "Can't get stat of '%s' (OS errno 13 - Permission denied)" fullPath)
        | :? IOException as error -> Error(29, sprintf "Error reading file '%s': %s" fullPath error.Message))

let private textBytes characterSet value =
    match tryRawBytes value, characterSet with
    | Some bytes, None
    | Some bytes, Some "binary" -> bytes
    | _ -> value |> toText |> Option.defaultValue "" |> Charset.encode (characterSet |> Option.defaultValue "utf8mb4")

let private stringLike =
    function
    | VString _
    | VBytes _
    | VBit _
    | VJson _
    | VGeometry _ -> true
    | _ -> false

let private firstRune (value: string) =
    value.EnumerateRunes() |> Seq.tryHead |> Option.map string

let private escapedText (options: SelectOutfileOptions) (value: string) =
    match options.Escape with
    | None -> value
    | Some escape ->
        let specials =
            [ Some escape
              options.EnclosedBy
              firstRune options.FieldTerminator
              firstRune options.LineTerminator ]
            |> List.choose id
            |> Set.ofList

        value.EnumerateRunes()
        |> Seq.map (fun rune ->
            let text = string rune

            if rune.Value = 0 then escape + "0"
            elif specials.Contains text then escape + text
            else text)
        |> String.concat ""

let private writeBytes (output: Stream) (bytes: byte[]) =
    output.Write(bytes, 0, bytes.Length)

let private escapedBytes charset escape markers (value: byte[]) =
    let escapeBytes = Charset.encode charset escape

    let markers =
        markers
        |> List.map (Charset.encode charset)
        |> List.filter (not << Array.isEmpty)
        |> List.sortByDescending _.Length

    let startsWith offset (marker: byte[]) =
        offset + marker.Length <= value.Length
        && marker
           |> Array.indexed
           |> Array.forall (fun (index, current) -> value.[offset + index] = current)

    use escaped = new MemoryStream()
    let mutable offset = 0

    while offset < value.Length do
        if value.[offset] = 0uy then
            writeBytes escaped escapeBytes
            escaped.WriteByte(byte '0')
            offset <- offset + 1
        else
            match markers |> List.tryFind (startsWith offset) with
            | Some marker ->
                writeBytes escaped escapeBytes
                writeBytes escaped marker
                offset <- offset + marker.Length
            | None ->
                escaped.WriteByte value.[offset]
                offset <- offset + 1

    escaped.ToArray()

let private outfileValue (options: SelectOutfileOptions) value =
    let characterSet = options.CharacterSet |> Option.map _.ToLowerInvariant()
    let charset = characterSet |> Option.defaultValue "utf8mb4"

    match value with
    | VNull ->
        options.Escape
        |> Option.map (fun escape -> Charset.encode charset (escape + "N"))
        |> Option.defaultWith (fun () -> Charset.encode charset "NULL")
    | value ->
        let enclosed = options.EnclosedBy.IsSome && (not options.OptionallyEnclosed || stringLike value)
        let marker = options.EnclosedBy |> Option.defaultValue ""

        let bytes =
            match tryRawBytes value, characterSet with
            | Some raw, None
            | Some raw, Some "binary" ->
                match options.Escape with
                | None -> raw
                | Some escape ->
                    let markers =
                        [ Some escape
                          options.EnclosedBy
                          firstRune options.FieldTerminator
                          firstRune options.LineTerminator ]
                        |> List.choose id

                    escapedBytes charset escape markers raw
            | _ ->
                value
                |> toText
                |> Option.defaultValue ""
                |> escapedText options
                |> Charset.encode charset

        if enclosed then
            Array.concat
                [ Charset.encode charset marker; bytes; Charset.encode charset marker ]
        else
            bytes

let private createOutput fullPath =
    let mutable opened: FileStream option = None

    try
        let output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        opened <- Some output

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead)

        Ok output
    with
    | error ->
        opened |> Option.iter _.Dispose()

        match error with
        | :? IOException when File.Exists fullPath && opened.IsNone ->
            Error(1086, sprintf "File '%s' already exists" fullPath)
        | :? UnauthorizedAccessException ->
            Error(1, sprintf "Can't create/write to file '%s' (OS errno 13 - Permission denied)" fullPath)
        | :? IOException as error -> Error(1, sprintf "Can't create/write to file '%s' (%s)" fullPath error.Message)
        | _ -> raise error

let private writeFailure fullPath (error: exn) =
    match error with
    | :? UnauthorizedAccessException ->
        Error(1, sprintf "Can't create/write to file '%s' (OS errno 13 - Permission denied)" fullPath)
    | :? IOException as error -> Error(1, sprintf "Can't create/write to file '%s' (%s)" fullPath error.Message)
    | _ -> raise error

let writeServerFile policy destination (rows: Value[] list) : Result<uint64, int * string> =
    let fileName =
        match destination with
        | Outfile(fileName, _) -> fileName
        | Dumpfile fileName -> fileName

    let validate =
        match destination with
        | Outfile(_, options) ->
            match options.CharacterSet with
            | Some characterSet when Charset.tryFind characterSet |> Option.isNone ->
                Error(1115, sprintf "Unknown character set: '%s'" characterSet)
            | _ -> Ok()
        | Dumpfile _ when rows.Length > 1 -> Error(1172, "Result consisted of more than one row")
        | Dumpfile _ -> Ok()

    validate
    |> Result.bind (fun () -> allowedPath policy fileName)
    |> Result.bind (fun fullPath ->
        createOutput fullPath
        |> Result.bind (fun output ->
            use output = output

            try
                match destination with
                | Dumpfile _ ->
                    rows
                    |> List.tryHead
                    |> Option.iter (Array.iter (textBytes None >> writeBytes output))
                | Outfile(_, options) ->
                    let charset = options.CharacterSet |> Option.defaultValue "utf8mb4"
                    let fieldTerminator = Charset.encode charset options.FieldTerminator
                    let linePrefix = Charset.encode charset options.LinePrefix
                    let lineTerminator = Charset.encode charset options.LineTerminator

                    for row in rows do
                        writeBytes output linePrefix

                        row
                        |> Array.iteri (fun index value ->
                            if index > 0 then writeBytes output fieldTerminator
                            writeBytes output (outfileValue options value))

                        writeBytes output lineTerminator

                Ok(uint64 rows.Length)
            with error ->
                writeFailure fullPath error))

let decode (load: Parser.LoadRequest) (bytes: byte[]) : Result<Value list list, int * string> =
    try
        let charset = load.Charset |> Option.defaultValue "utf8mb4"
        let text =
            match Charset.decodeLoadData charset bytes with
            | Ok text -> text
            | Error message -> raise (DecoderFallbackException message)
        let enclosedBy = load.EnclosedBy |> Option.bind singleCharacter
        let escape = load.Escape |> Option.bind singleCharacter
        let nullMarker = escape |> Option.map (fun value -> string value + "N")
        let rows = ResizeArray<Value list>()
        let fields = ResizeArray<Value>()
        let value = StringBuilder()
        let raw = StringBuilder()
        let mutable index = 0
        let mutable enclosed = false
        let mutable fieldEnclosed = false
        let mutable escaped = false

        let endField () =
            let text = value.ToString()
            let rawText = raw.ToString()
            fields.Add(if not fieldEnclosed && Some rawText = nullMarker then VNull else VString text)
            value.Clear() |> ignore
            raw.Clear() |> ignore
            fieldEnclosed <- false

        let endRow () =
            endField ()
            rows.Add(List.ofSeq fields)
            fields.Clear()

        let startsWith (candidate: string) =
            candidate <> ""
            && index + candidate.Length <= text.Length
            && String.CompareOrdinal(text, index, candidate, 0, candidate.Length) = 0

        while index < text.Length do
            let current = text.[index]
            raw.Append current |> ignore

            if escaped then
                value.Append(
                    match current with
                    | '0' -> '\u0000'
                    | 'b' -> '\b'
                    | 'n' -> '\n'
                    | 'r' -> '\r'
                    | 't' -> '\t'
                    | character -> character
                )
                |> ignore

                escaped <- false
                index <- index + 1
            elif escape = Some current then
                escaped <- true
                index <- index + 1
            elif enclosed then
                if enclosedBy = Some current then
                    enclosed <- false
                else
                    value.Append current |> ignore

                index <- index + 1
            elif enclosedBy = Some current && value.Length = 0 then
                enclosed <- true
                fieldEnclosed <- true
                index <- index + 1
            elif startsWith load.LineTerminator then
                raw.Length <- raw.Length - 1
                endRow ()
                index <- index + load.LineTerminator.Length
            elif startsWith load.FieldTerminator then
                raw.Length <- raw.Length - 1
                endField ()
                index <- index + load.FieldTerminator.Length
            else
                value.Append current |> ignore
                index <- index + 1

        if escaped || enclosed then
            Error(1300, "Invalid LOAD DATA input")
        else
            if value.Length > 0 || raw.Length > 0 || fields.Count > 0 then
                endRow ()

            Ok(rows |> Seq.skip (min load.IgnoreLines rows.Count) |> List.ofSeq)
    with :? DecoderFallbackException as error ->
        Error(1300, error.Message)
