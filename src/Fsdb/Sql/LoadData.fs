/// LOAD DATA decoding is independent of where the byte stream originated.
module Fsdb.LoadData

open System
open System.IO
open System.Text
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
