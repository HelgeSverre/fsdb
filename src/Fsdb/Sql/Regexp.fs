/// The bounded .NET implementation of MySQL's ICU regular-expression entry
/// points. Collation chooses case sensitivity; accents remain literal regex
/// characters even under an accent-insensitive comparison collation.
module Fsdb.Regexp

open System
open System.Text
open System.Text.RegularExpressions

type RegexError =
    | InvalidPattern of code: int * message: string
    | InvalidMatchType

let errorMessage = function
    | InvalidPattern(_, message) -> message
    | InvalidMatchType -> ""

let errorCode = function
    | InvalidPattern(code, _) -> code
    | InvalidMatchType -> 1210

let private optionsFor (collation: Collation.Collation) (matchType: string option) : Result<RegexOptions, RegexError> =
    let mutable options = RegexOptions.CultureInvariant

    if collation.CharEquals 'a' 'A' then
        options <- options ||| RegexOptions.IgnoreCase

    let mutable error = None

    for flag in matchType |> Option.defaultValue "" do
        match flag with
        | 'c' -> options <- options &&& ~~~RegexOptions.IgnoreCase
        | 'i' -> options <- options ||| RegexOptions.IgnoreCase
        | 'm' -> options <- options ||| RegexOptions.Multiline
        | 'n' -> options <- options ||| RegexOptions.Singleline
        | 'u' -> ()
        | _ -> error <- Some InvalidMatchType

    error |> Option.map Error |> Option.defaultValue (Ok options)

let private posixClasses =
    [ "[[:alpha:]]", "[\\p{L}]"
      "[[:digit:]]", "[\\p{Nd}]"
      "[[:alnum:]]", "[\\p{L}\\p{Nd}]"
      "[[:space:]]", "[\\s]"
      "[[:word:]]", "[\\p{L}\\p{Nd}_]" ]

let private posixClassAt (pattern: string) (index: int) =
    posixClasses
    |> List.tryFind (fun (source, _) -> pattern.AsSpan(index).StartsWith(source, StringComparison.Ordinal))

let private normalizePosixClasses (pattern: string) =
    let builder = StringBuilder(pattern.Length)
    let mutable index = 0
    let mutable escaped = false

    while index < pattern.Length do
        if escaped then
            builder.Append pattern[index] |> ignore
            escaped <- false
            index <- index + 1
        elif pattern[index] = '\\' then
            builder.Append '\\' |> ignore
            escaped <- true
            index <- index + 1
        else
            match posixClassAt pattern index with
            | Some(source, target) ->
                builder.Append target |> ignore
                index <- index + source.Length
            | None ->
                builder.Append pattern[index] |> ignore
                index <- index + 1

    builder.ToString()

let private normalizePattern (options: RegexOptions) (pattern: string) : string =
    let posix = normalizePosixClasses pattern

    if not (options.HasFlag RegexOptions.IgnoreCase) then posix
    else
        let builder = StringBuilder(posix.Length)
        let mutable escaped = false
        let mutable inClass = false

        for character in posix do
            if escaped then
                builder.Append character |> ignore
                escaped <- false
            else
                match character with
                | '\\' ->
                    builder.Append character |> ignore
                    escaped <- true
                | '[' ->
                    builder.Append character |> ignore
                    inClass <- true
                | ']' ->
                    builder.Append character |> ignore
                    inClass <- false
                | ('Σ' | 'σ' | 'ς') when inClass -> builder.Append "Σσς" |> ignore
                | 'Σ' | 'σ' | 'ς' -> builder.Append "[Σσς]" |> ignore
                | _ -> builder.Append character |> ignore

        builder.ToString()

let private hasWildcardDot (pattern: string) =
    let mutable escaped = false
    let mutable inClass = false
    let mutable found = false

    for character in pattern do
        if escaped then escaped <- false
        else
            match character with
            | '\\' -> escaped <- true
            | '[' -> inClass <- true
            | ']' -> inClass <- false
            | '.' when not inClass -> found <- true
            | _ -> ()

    found

type PreparedInput =
    { Text: string
      RemovedCrOffsets: int array option }

let private normalizeLineTerminators (text: string) =
    if text.IndexOf '\r' < 0 then
        { Text = text; RemovedCrOffsets = None }
    else
        let builder = StringBuilder(text.Length)
        let removedCrOffsets = ResizeArray<int>()
        let mutable index = 0

        while index < text.Length do
            match text[index] with
            | '\r' when index + 1 < text.Length && text[index + 1] = '\n' ->
                builder.Append '\n' |> ignore
                removedCrOffsets.Add builder.Length
                index <- index + 2
            | '\r' ->
                builder.Append '\n' |> ignore
                index <- index + 1
            | character ->
                builder.Append character |> ignore
                index <- index + 1

        { Text = builder.ToString()
          RemovedCrOffsets = Some(removedCrOffsets.ToArray()) }

let private normalizeLineTerminatorText (text: string) =
    if text.IndexOf '\r' < 0 then
        text
    else
        let builder = StringBuilder(text.Length)
        let mutable index = 0

        while index < text.Length do
            match text[index] with
            | '\r' when index + 1 < text.Length && text[index + 1] = '\n' ->
                builder.Append '\n' |> ignore
                index <- index + 2
            | '\r' ->
                builder.Append '\n' |> ignore
                index <- index + 1
            | character ->
                builder.Append character |> ignore
                index <- index + 1

        builder.ToString()

let private needsLineTerminatorNormalization matchType pattern =
    let multiline = matchType |> Option.defaultValue "" |> String.exists ((=) 'm')
    let unixLines = matchType |> Option.defaultValue "" |> String.exists ((=) 'u')
    (hasWildcardDot pattern || multiline) && not unixLines

let prepareInput (matchType: string option) (pattern: string) (text: string) =
    if needsLineTerminatorNormalization matchType pattern then
        normalizeLineTerminators text
    else
        { Text = text
          RemovedCrOffsets = None }

let prepareText (matchType: string option) (pattern: string) (text: string) =
    if needsLineTerminatorNormalization matchType pattern then
        normalizeLineTerminatorText text
    else
        text

let sourceOffset (input: PreparedInput) index =
    match input.RemovedCrOffsets with
    | None -> index
    | Some removed ->
        let found = Array.BinarySearch(removed, index)
        let preceding = if found >= 0 then found + 1 else ~~~found
        index + preceding

let normalizedOffset (input: PreparedInput) sourcePosition =
    match input.RemovedCrOffsets with
    | None -> min sourcePosition input.Text.Length
    | Some _ ->
        let mutable lower = 0
        let mutable upper = input.Text.Length + 1

        while lower < upper do
            let middle = lower + (upper - lower) / 2

            if sourceOffset input middle <= sourcePosition then
                lower <- middle + 1
            else
                upper <- middle

        max 0 (lower - 1)

let scalarCount (text: string) =
    let mutable offset = 0
    let mutable count = 0

    while offset < text.Length do
        offset <- offset + if Char.IsHighSurrogate text[offset] && offset + 1 < text.Length && Char.IsLowSurrogate text[offset + 1] then 2 else 1
        count <- count + 1

    count

let utf16OffsetAtScalar (text: string) scalar =
    let mutable offset = 0
    let mutable count = 0

    while offset < text.Length && count < scalar do
        offset <- offset + if Char.IsHighSurrogate text[offset] && offset + 1 < text.Length && Char.IsLowSurrogate text[offset + 1] then 2 else 1
        count <- count + 1

    offset

let scalarAtUtf16Offset (text: string) utf16Offset =
    let mutable offset = 0
    let mutable count = 0

    while offset < utf16Offset do
        offset <- offset + if Char.IsHighSurrogate text[offset] && offset + 1 < text.Length && Char.IsLowSurrogate text[offset + 1] then 2 else 1
        count <- count + 1

    count

let private hasInvalidPosixClass (pattern: string) =
    let mutable index = 0
    let mutable escaped = false
    let mutable invalid = false

    while index < pattern.Length && not invalid do
        if escaped then
            escaped <- false
            index <- index + 1
        elif pattern[index] = '\\' then
            escaped <- true
            index <- index + 1
        elif pattern.AsSpan(index).StartsWith("[[:", StringComparison.Ordinal) then
            let closing = pattern.IndexOf(":]]", index + 3, StringComparison.Ordinal)

            if closing < 0 then
                index <- pattern.Length
            else
                let source = pattern.Substring(index, closing + 3 - index)
                invalid <- posixClasses |> List.exists (fun (known, _) -> known = source) |> not
                index <- closing + 3
        else
            index <- index + 1

    invalid

let private invalidPattern (pattern: string) =
    let interval = Regex.Match(pattern, @"(?<!\\)\{(\d+),(\d+)\}")

    if interval.Success then
        match Int32.TryParse interval.Groups.[1].Value, Int32.TryParse interval.Groups.[2].Value with
        | (true, minimum), (true, maximum) when maximum < minimum ->
            Some(3693, "The maximum is less than the minumum in a {min,max} interval.")
        | _ -> None
    elif hasInvalidPosixClass pattern then
        Some(3685, "Illegal argument to a regular expression.")
    elif pattern = "{" then
        Some(3688, "Syntax error in regular expression on line 1, character 1.")
    else
        None

let private parseError (error: RegexParseException) =
    match error.Error.ToString() with
    | "InsufficientClosingParentheses"
    | "InsufficientOpeningParentheses" -> InvalidPattern(3691, "Mismatched parenthesis in regular expression.")
    | "UnterminatedBracket" -> InvalidPattern(3696, "The regular expression contains an unclosed bracket expression.")
    | "ReversedCharacterRange" -> InvalidPattern(3697, "The regular expression contains an [x-y] character range where x comes after y.")
    | "QuantifierOrCaptureGroupOutOfRange" -> InvalidPattern(3693, "The maximum is less than the minumum in a {min,max} interval.")
    | _ -> InvalidPattern(3691, "Invalid regular expression.")

let compile (collation: Collation.Collation) (matchType: string option) (pattern: string) : Result<Regex, RegexError> =
    match invalidPattern pattern with
    | Some(code, message) -> Error(InvalidPattern(code, message))
    | None ->
        optionsFor collation matchType
        |> Result.bind (fun options ->
            try
                Ok(Regex(normalizePattern options pattern, options, Limits.regexpMatchTimeout))
            with
            | :? RegexParseException as error -> Error(parseError error)
            | :? ArgumentException -> Error(InvalidPattern(3691, "Invalid regular expression.")))
