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

let private horizontalClass = "[\\p{Zs}\\t]"
let private verticalClass = "[\\n\\r\\v\\f\\x85\\u2028\\u2029]"
let private wordClass = "[\\p{L}\\p{M}\\p{Nl}\\p{Nd}\\p{Pc}\\u200c\\u200d]"

let private negateClass (target: string) =
    if target[1] = '^' then "[" + target.Substring(2)
    else "[^" + target.Substring(1)

let private characterEscape = function
    | 'h' -> Some horizontalClass
    | 'H' -> Some(negateClass horizontalClass)
    | 'v' -> Some verticalClass
    | 'V' -> Some(negateClass verticalClass)
    | 'w' -> Some wordClass
    | 'W' -> Some(negateClass wordClass)
    | _ -> None

let private posixPositiveClasses =
    [ "[[:ascii:]]", "[\\x00-\\x7F]"
      "[[:alpha:]]", "[\\p{L}\\p{Nl}]"
      "[[:lower:]]", "[\\p{Ll}]"
      "[[:upper:]]", "[\\p{Lu}]"
      "[[:digit:]]", "[\\p{Nd}]"
      "[[:xdigit:]]", "[\\p{Nd}A-Fa-f]"
      "[[:alnum:]]", "[\\p{L}\\p{Nl}\\p{Nd}]"
      "[[:space:]]", "[\\s]"
      "[[:blank:]]", horizontalClass
      "[[:cntrl:]]", "[\\p{Cc}]"
      "[[:punct:]]", "[\\p{P}]"
      // ICU graph/print admit format and private-use characters, but not
      // controls, surrogates, or unassigned code points.
      "[[:graph:]]", "[\\p{L}\\p{M}\\p{N}\\p{P}\\p{S}\\p{Cf}\\p{Co}]"
      "[[:print:]]", "[\\p{L}\\p{M}\\p{N}\\p{P}\\p{S}\\p{Cf}\\p{Co}\\p{Zs}]"
      // Unicode word characters also include combining marks and join controls.
      "[[:word:]]", wordClass ]

let private posixClasses =
    let negated =
        posixPositiveClasses |> List.map (fun (source: string, target: string) ->
            source.Replace("[[:", "[[:^"), negateClass target)
    posixPositiveClasses @ negated

let private posixMembers =
    posixClasses
    |> List.map (fun (source: string, target) -> source.Substring(1, source.Length - 2), target)

let private posixClassAt inClass (pattern: string) (index: int) =
    if pattern[index] <> '[' then None
    elif not inClass then
        posixClasses
        |> List.tryFind (fun (source, _) -> pattern.AsSpan(index).StartsWith(source, StringComparison.Ordinal))
    else
        posixPositiveClasses
        |> List.tryPick (fun (source: string, target: string) ->
            let memberSyntax = source.Substring(1, source.Length - 2)
            if pattern.AsSpan(index).StartsWith(memberSyntax, StringComparison.Ordinal) then
                Some(memberSyntax, target.Substring(1, target.Length - 2))
            else None)

let private quotedLiteralAt inClass (pattern: string) index =
    let contentStart = index + 2
    let closing = pattern.IndexOf("\\E", contentStart, StringComparison.Ordinal)
    let contentEnd = if closing < 0 then pattern.Length else closing
    let escaped = Regex.Escape(pattern.Substring(contentStart, contentEnd - contentStart))
    let escaped = if inClass then escaped.Replace("]", "\\]").Replace("-", "\\-") else escaped
    (if closing < 0 then contentEnd else contentEnd + 2), escaped

let private mixedNegatedClassAt (pattern: string) start =
    let outerNegated = start + 1 < pattern.Length && pattern[start + 1] = '^'
    let ordinary = StringBuilder()
    let alternatives = ResizeArray<string>()
    let mutable index = start + (if outerNegated then 2 else 1)
    let mutable closed = false
    let mutable invalidMember = false

    while index < pattern.Length && not closed do
        if pattern[index] = '\\' && index + 1 < pattern.Length && pattern[index + 1] = 'Q' then
            let nextIndex, literal = quotedLiteralAt true pattern index
            ordinary.Append literal |> ignore
            index <- nextIndex
        elif pattern[index] = '\\' && index + 1 < pattern.Length then
            match characterEscape pattern[index + 1] with
            | Some target when target[1] = '^' -> alternatives.Add target
            | Some target -> ordinary.Append(target, 1, target.Length - 2) |> ignore
            | None when pattern[index + 1] = 'b' || pattern[index + 1] = 'B' ->
                ordinary.Append pattern[index + 1] |> ignore
            | None -> ordinary.Append(pattern, index, 2) |> ignore
            index <- index + 2
        elif pattern[index] = ']' then
            closed <- true
            index <- index + 1
        else
            let posixMember =
                if pattern[index] = '[' then
                    posixMembers
                    |> List.tryFind (fun (syntax, _) -> pattern.AsSpan(index).StartsWith(syntax, StringComparison.Ordinal))
                else None
            match posixMember with
            | Some(syntax, target) ->
                if target[1] = '^' then alternatives.Add target
                else ordinary.Append(target, 1, target.Length - 2) |> ignore
                index <- index + syntax.Length
            | None ->
                if pattern.AsSpan(index).StartsWith("[:", StringComparison.Ordinal)
                   && pattern.IndexOf(":]", index + 2, StringComparison.Ordinal) >= 0 then
                    invalidMember <- true
                ordinary.Append pattern[index] |> ignore
                index <- index + 1

    if not closed || alternatives.Count = 0 then None
    elif invalidMember then Some(Error(index - start))
    else
        if ordinary.Length > 0 then alternatives.Insert(0, "[" + ordinary.ToString() + "]")
        let union = String.concat "|" alternatives
        // A bracket expression is one atom, so keep quantifiers attached to
        // the whole union. The outer ^ complements that one-character union.
        let rewritten =
            if outerNegated then "(?:(?!(?:" + union + "))[\\s\\S])"
            else "(?:" + union + ")"
        Some(Ok(index - start, rewritten))

let private normalizePosixClasses (pattern: string) =
    let builder = StringBuilder(pattern.Length)
    let mutable index = 0
    let mutable escaped = false
    let mutable inClass = false
    let mutable invalid = false

    while index < pattern.Length do
        if escaped then
            builder.Append pattern[index] |> ignore
            escaped <- false
            index <- index + 1
        elif pattern[index] = '\\' then
            if index + 1 < pattern.Length && pattern[index + 1] = 'Q' then
                let nextIndex, literal = quotedLiteralAt inClass pattern index
                builder.Append literal |> ignore
                index <- nextIndex
            elif index + 1 < pattern.Length && pattern[index + 1] = 'E' then
                builder.Append 'E' |> ignore
                index <- index + 2
            elif inClass && index + 1 < pattern.Length && (pattern[index + 1] = 'b' || pattern[index + 1] = 'B') then
                builder.Append pattern[index + 1] |> ignore
                index <- index + 2
            elif not inClass && index + 1 < pattern.Length && pattern[index + 1] = 'R' then
                builder.Append("(?:\\r\\n|" + verticalClass + ")") |> ignore
                index <- index + 2
            else
                match if index + 1 < pattern.Length then characterEscape pattern[index + 1] else None with
                | Some target when not inClass ->
                    builder.Append target |> ignore
                    index <- index + 2
                | Some target when target[1] <> '^' ->
                    builder.Append(target, 1, target.Length - 2) |> ignore
                    index <- index + 2
                | _ ->
                    builder.Append '\\' |> ignore
                    escaped <- true
                    index <- index + 1
        else
            match posixClassAt inClass pattern index with
            | Some(source, target) ->
                builder.Append target |> ignore
                index <- index + source.Length
            | None ->
                match if not inClass && pattern[index] = '[' then mixedNegatedClassAt pattern index else None with
                | Some(Ok(length, rewritten)) ->
                    builder.Append rewritten |> ignore
                    index <- index + length
                | Some(Error length) ->
                    invalid <- true
                    index <- index + length
                | None ->
                    if inClass && pattern.AsSpan(index).StartsWith("[:", StringComparison.Ordinal) then
                        let closing = pattern.IndexOf(":]", index + 2, StringComparison.Ordinal)
                        if closing >= 0 then
                            let memberSyntax = pattern.Substring(index, closing + 2 - index)
                            invalid <- invalid || not (posixMembers |> List.exists (fun (syntax, _) -> syntax = memberSyntax))
                    let character = pattern[index]
                    builder.Append character |> ignore
                    if character = '[' then inClass <- true
                    elif character = ']' then inClass <- false
                    index <- index + 1

    builder.ToString(), invalid

let private normalizePattern (options: RegexOptions) (posix: string) : string =
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

let private invalidPattern (pattern: string) =
    let interval = Regex.Match(pattern, @"(?<!\\)\{(?<minimum>\d+),(?<maximum>\d+)\}")

    if interval.Success then
        match
            Int32.TryParse interval.Groups.["minimum"].Value,
            Int32.TryParse interval.Groups.["maximum"].Value
        with
        | (true, minimum), (true, maximum) when maximum < minimum ->
            Some(3693, "The maximum is less than the minumum in a {min,max} interval.")
        | _ -> None
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
    let posix, invalidPosix = normalizePosixClasses pattern
    match invalidPattern posix with
    | Some(code, message) -> Error(InvalidPattern(code, message))
    | None ->
        if invalidPosix then Error(InvalidPattern(3685, "Illegal argument to a regular expression."))
        else
            optionsFor collation matchType
            |> Result.bind (fun options ->
                try
                    Ok(Regex(normalizePattern options posix, options, Limits.regexpMatchTimeout))
                with
                | :? RegexParseException as error -> Error(parseError error)
                | :? ArgumentException -> Error(InvalidPattern(3691, "Invalid regular expression.")))
