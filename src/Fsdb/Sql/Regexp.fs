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

let private scalarAtom = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[\s\S])"

let private scalarNegatedAtom (atom: string) =
    // Test the positive atom before consuming a complete scalar. Reject a
    // low surrogate preceded by its high surrogate: .NET may retry there
    // after a failed lookahead at the start of a matching pair.
    "(?:(?<![\\uD800-\\uDBFF])(?!(?:" + atom + "))" + scalarAtom + ")"

let private scalarNegatedClass (target: string) =
    scalarNegatedAtom (negateClass target)

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

let private unicodeCategoryAliases =
    [ "OTHER", "C"; "CONTROL", "Cc"; "FORMAT", "Cf"; "UNASSIGNED", "Cn"
      "PRIVATE_USE", "Co"; "SURROGATE", "Cs"
      "LETTER", "L"; "LOWERCASE_LETTER", "Ll"; "MODIFIER_LETTER", "Lm"
      "OTHER_LETTER", "Lo"; "TITLECASE_LETTER", "Lt"; "UPPERCASE_LETTER", "Lu"
      "MARK", "M"; "SPACING_MARK", "Mc"; "ENCLOSING_MARK", "Me"; "NONSPACING_MARK", "Mn"
      "NUMBER", "N"; "DECIMAL_NUMBER", "Nd"; "LETTER_NUMBER", "Nl"; "OTHER_NUMBER", "No"
      "PUNCTUATION", "P"; "CONNECTOR_PUNCTUATION", "Pc"; "DASH_PUNCTUATION", "Pd"
      "CLOSE_PUNCTUATION", "Pe"; "FINAL_PUNCTUATION", "Pf"; "INITIAL_PUNCTUATION", "Pi"
      "OTHER_PUNCTUATION", "Po"; "OPEN_PUNCTUATION", "Ps"
      "SYMBOL", "S"; "CURRENCY_SYMBOL", "Sc"; "MODIFIER_SYMBOL", "Sk"
      "MATH_SYMBOL", "Sm"; "OTHER_SYMBOL", "So"
      "SEPARATOR", "Z"; "LINE_SEPARATOR", "Zl"; "PARAGRAPH_SEPARATOR", "Zp"; "SPACE_SEPARATOR", "Zs" ]
    |> List.collect (fun (name, category) -> [ name, category; category.ToUpperInvariant(), category ])
    |> Map.ofList

let private basicLatinAliases =
    set [ "ASCII"; "BLK=BASIC_LATIN"; "BLOCK=BASIC_LATIN"; "INBASICLATIN" ]

let private binaryClassMembers =
    [ [ "ASCII_HEX_DIGIT"; "AHEX" ], "0-9A-Fa-f"
      [ "HEX_DIGIT"; "HEX"; "XDIGIT" ], "0-9A-Fa-f\\uFF10-\\uFF19\\uFF21-\\uFF26\\uFF41-\\uFF46"
      [ "JOIN_CONTROL"; "JOIN_C" ], "\\u200C\\u200D"
      [ "BIDI_CONTROL"; "BIDI_C" ], "\\u061C\\u200E\\u200F\\u202A-\\u202E\\u2066-\\u2069" ]
    |> List.collect (fun (aliases, members) -> aliases |> List.map (fun alias -> alias, members))
    |> Map.ofList

let private scalarBinaryAtoms =
    [ [ "REGIONAL_INDICATOR"; "RI" ], @"\uD83C[\uDDE6-\uDDFF]"
      [ "EMOJI_MODIFIER"; "EMOD" ], @"\uD83C[\uDFFB-\uDFFF]"
      [ "VARIATION_SELECTOR"; "VS" ], @"(?:[\u180B-\u180D\u180F\uFE00-\uFE0F]|\uDB40[\uDD00-\uDDEF])" ]
    |> List.collect (fun (aliases, atom) -> aliases |> List.map (fun alias -> alias, atom))
    |> Map.ofList

let private unicodePropertyNameAt (pattern: string) index =
    if index + 3 >= pattern.Length
       || (pattern[index + 1] <> 'p' && pattern[index + 1] <> 'P')
       || pattern[index + 2] <> '{' then
        None
    else
        let closing = pattern.IndexOf('}', index + 3)
        if closing < 0 then None
        else Some(closing + 1, pattern.Substring(index + 3, closing - index - 3).ToUpperInvariant())

let private rejectedEscapeAt (pattern: string) index =
    (index + 1 < pattern.Length && pattern[index + 1] = 'N')
    || (unicodePropertyNameAt pattern index
        |> Option.exists (fun (_, name) -> name = "ISBASICLATIN"))

type private UnicodePropertyReplacement =
    | ClassMember of atom: string * memberSyntax: string
    | ScalarAlternative of string

let private unicodePropertyAt ignoreCase (pattern: string) index =
    match unicodePropertyNameAt pattern index with
    | None -> None
    | Some(nextIndex, "ANY") ->
        Some(nextIndex, ScalarAlternative(if pattern[index + 1] = 'P' then "(?!)" else scalarAtom))
    | Some(nextIndex, name) when Map.containsKey name scalarBinaryAtoms ->
        let atom = Map.find name scalarBinaryAtoms
        Some(nextIndex, ScalarAlternative(if pattern[index + 1] = 'P' then scalarNegatedAtom atom else atom))
    | Some(nextIndex, name) ->
        match RegexpScriptProperties.tryFindAtom ignoreCase name with
        | Some atom ->
            Some(nextIndex, ScalarAlternative(if pattern[index + 1] = 'P' then scalarNegatedAtom atom else atom))
        | None ->
            let categoryName =
                if name.StartsWith("GC=", StringComparison.Ordinal) then name.Substring(3)
                elif name.StartsWith("GENERAL_CATEGORY=", StringComparison.Ordinal) then name.Substring(17)
                else name
            let positive =
                if name = "WHITE_SPACE" then
                    Some("\\s", "\\s")
                elif Set.contains name basicLatinAliases then
                    Some("\\p{IsBasicLatin}", "\\p{IsBasicLatin}")
                else
                    match Map.tryFind name binaryClassMembers with
                    | Some members -> Some("[" + members + "]", members)
                    | None ->
                        Map.tryFind categoryName unicodeCategoryAliases
                        |> Option.map (fun category ->
                            let property = "\\p{" + category + "}"
                            property, property)
            positive
            |> Option.map (fun (atom, memberSyntax) ->
                nextIndex,
                if pattern[index + 1] = 'P' then ScalarAlternative(scalarNegatedClass ("[^" + memberSyntax + "]"))
                else ClassMember(atom, memberSyntax))

let private mixedNegatedClassAt ignoreCase (pattern: string) start =
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
            invalidMember <- invalidMember || rejectedEscapeAt pattern index
            match unicodePropertyAt ignoreCase pattern index with
            | Some(nextIndex, ClassMember(_, memberSyntax)) ->
                ordinary.Append memberSyntax |> ignore
                index <- nextIndex
            | Some(nextIndex, ScalarAlternative property) ->
                alternatives.Add property
                index <- nextIndex
            | None ->
                match characterEscape pattern[index + 1] with
                | Some target when target[1] = '^' -> alternatives.Add(scalarNegatedClass target)
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
                if target[1] = '^' then alternatives.Add(scalarNegatedClass target)
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
            if outerNegated then scalarNegatedAtom union
            else "(?:" + union + ")"
        Some(Ok(index - start, rewritten))

let private literalLetterEscapes = "CFKLqy"

let private normalizePosixClasses ignoreCase (pattern: string) =
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
            else
                invalid <- invalid || rejectedEscapeAt pattern index
                match unicodePropertyAt ignoreCase pattern index with
                | Some(nextIndex, property) ->
                    let replacement =
                        match property with
                        | ClassMember(atom, memberSyntax) -> if inClass then memberSyntax else atom
                        | ScalarAlternative memberPattern -> memberPattern
                    builder.Append replacement |> ignore
                    index <- nextIndex
                | None when inClass && index + 1 < pattern.Length && (pattern[index + 1] = 'b' || pattern[index + 1] = 'B') ->
                    builder.Append pattern[index + 1] |> ignore
                    index <- index + 2
                | None when not inClass && index + 1 < pattern.Length && pattern[index + 1] = 'R' ->
                    builder.Append("(?:\\r\\n|" + verticalClass + ")") |> ignore
                    index <- index + 2
                | None ->
                    match if index + 1 < pattern.Length then characterEscape pattern[index + 1] else None with
                    | Some target when not inClass ->
                        builder.Append(if target[1] = '^' then scalarNegatedClass target else target) |> ignore
                        index <- index + 2
                    | Some target when target[1] <> '^' ->
                        builder.Append(target, 1, target.Length - 2) |> ignore
                        index <- index + 2
                    | None when index + 1 < pattern.Length && literalLetterEscapes.Contains(pattern[index + 1]) ->
                        builder.Append pattern[index + 1] |> ignore
                        index <- index + 2
                    | _ ->
                        builder.Append '\\' |> ignore
                        escaped <- true
                        index <- index + 1
        else
            match posixClassAt inClass pattern index with
            | Some(source, target) ->
                builder.Append(if target[1] = '^' then scalarNegatedClass target else target) |> ignore
                index <- index + source.Length
            | None ->
                match if not inClass && pattern[index] = '[' then mixedNegatedClassAt ignoreCase pattern index else None with
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
    let ignoreCase = options.HasFlag RegexOptions.IgnoreCase

    if not ignoreCase && posix.IndexOf('.') < 0 then posix
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
                | '.' when not inClass ->
                    // ICU's dot consumes one Unicode scalar; .NET's consumes
                    // one UTF-16 code unit unless a surrogate pair comes first.
                    builder.Append @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|.)" |> ignore
                | ('Σ' | 'σ' | 'ς') when ignoreCase && inClass -> builder.Append "Σσς" |> ignore
                | ('Σ' | 'σ' | 'ς') when ignoreCase -> builder.Append "[Σσς]" |> ignore
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

let private isEscapedAt (pattern: string) index =
    let mutable previous = index - 1
    while previous >= 0 && pattern[previous] = '\\' do
        previous <- previous - 1
    (index - previous - 1) % 2 = 1

let private tryQuantifierStart (pattern: string) =
    let last = pattern.Length - 1
    if last < 0 || isEscapedAt pattern last then None
    else
        match pattern[last] with
        | '*' | '+' | '?' -> Some last
        | '}' ->
            let opening = pattern.LastIndexOf('{', last - 1)
            if opening >= 0
               && not (isEscapedAt pattern opening)
               && Regex.IsMatch(pattern.Substring(opening + 1, last - opening - 1), @"^\d+(?:,\d*)?$") then
                Some opening
            else None
        | _ -> None

let private tryAtomStart (pattern: string) endIndex =
    let groups = System.Collections.Generic.Stack<int>()
    let mutable classStart = None
    let mutable lastAtom = None
    let mutable index = 0

    while index < endIndex do
        match pattern[index] with
        | '\\' when index + 1 < endIndex ->
            let start = index
            if index + 2 < endIndex
               && (pattern[index + 1] = 'p' || pattern[index + 1] = 'P')
               && pattern[index + 2] = '{' then
                let closing = pattern.IndexOf('}', index + 3)
                index <- if closing >= 0 && closing < endIndex then closing + 1 else index + 2
            else index <- index + 2
            if classStart.IsNone then lastAtom <- Some start
        | '[' when classStart.IsNone ->
            classStart <- Some index
            lastAtom <- None
            index <- index + 1
        | ']' when classStart.IsSome ->
            lastAtom <- classStart
            classStart <- None
            index <- index + 1
        | _ when classStart.IsSome -> index <- index + 1
        | '(' ->
            groups.Push index
            lastAtom <- None
            index <- index + 1
        | ')' ->
            if groups.Count > 0 then lastAtom <- Some(groups.Pop())
            index <- index + 1
        | '|' | '^' | '$' ->
            lastAtom <- None
            index <- index + 1
        | '*' | '+' | '?' -> index <- index + 1
        | character ->
            lastAtom <-
                if Char.IsLowSurrogate character
                   && index > 0
                   && Char.IsHighSurrogate pattern[index - 1] then Some(index - 1)
                else Some index
            index <- index + 1

    if classStart.IsSome then None else lastAtom

let private normalizePossessiveQuantifiers (pattern: string) : Result<string, RegexError> =
    let output = StringBuilder(pattern.Length)
    let mutable index = 0
    let mutable inClass = false
    let mutable afterPossessive = false
    let mutable error = None

    while index < pattern.Length && error.IsNone do
        let character = pattern[index]
        if afterPossessive
           && not inClass
           && (character = '+' || character = '*' || character = '?' || character = '{') then
            let position = scalarAtUtf16Offset pattern (index + 1)
            error <- Some(InvalidPattern(3688, sprintf "Syntax error in regular expression on line 1, character %d." position))
        elif character = '\\' && index + 1 < pattern.Length then
            output.Append(pattern, index, 2) |> ignore
            index <- index + 2
            afterPossessive <- false
        else
            if character = '[' then inClass <- true
            elif character = ']' then inClass <- false

            if character = '+'
               && not inClass
               && output.Length > 0
               && (output[output.Length - 1] = '*'
                   || output[output.Length - 1] = '+'
                   || output[output.Length - 1] = '?'
                   || output[output.Length - 1] = '}') then
                let preceding = output.ToString()
                match tryQuantifierStart preceding with
                | Some quantifierStart ->
                    let beforeQuantifier = preceding.Substring(0, quantifierStart)
                    if tryQuantifierStart beforeQuantifier |> Option.isSome then
                        let position = scalarAtUtf16Offset pattern (index + 1)
                        error <- Some(InvalidPattern(3688, sprintf "Syntax error in regular expression on line 1, character %d." position))
                    else
                        match tryAtomStart preceding quantifierStart with
                        | Some atomStart ->
                            let quantified = preceding.Substring(atomStart)
                            output.Length <- atomStart
                            output.Append("(?>").Append(quantified).Append(')') |> ignore
                            afterPossessive <- true
                        | None -> output.Append character |> ignore
                | None -> output.Append character |> ignore
            else output.Append character |> ignore

            if character <> '+' || inClass then afterPossessive <- false
            index <- index + 1

    match error with
    | Some error -> Error error
    | None -> Ok(output.ToString())

let private invalidPattern (pattern: string) =
    let interval = Regex.Match(pattern, @"(?<!\\)\{(?<minimum>\d+),(?<maximum>\d+)\}")
    let missingIntervalMinimum = Regex.IsMatch(pattern, @"(?<!\\)\{,\d+\}")

    if missingIntervalMinimum then
        Some(3692, "Incorrect description of a {min,max} interval.")
    elif interval.Success then
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

let private parseError (pattern: string) (error: RegexParseException) =
    match error.Error.ToString() with
    | "QuantifierAfterNothing" ->
        let character = scalarAtUtf16Offset pattern error.Offset
        InvalidPattern(3688, sprintf "Syntax error in regular expression on line 1, character %d." character)
    | "InsufficientClosingParentheses"
    | "InsufficientOpeningParentheses" -> InvalidPattern(3691, "Mismatched parenthesis in regular expression.")
    | "UnterminatedBracket" -> InvalidPattern(3696, "The regular expression contains an unclosed bracket expression.")
    | "ReversedCharacterRange" -> InvalidPattern(3697, "The regular expression contains an [x-y] character range where x comes after y.")
    | "QuantifierOrCaptureGroupOutOfRange" ->
        let largeCapture = Regex.Match(pattern, @"(?<!\\)\(\?<\d{10,}>")
        if largeCapture.Success then
            let character = scalarAtUtf16Offset pattern (largeCapture.Index + 4)
            InvalidPattern(3688, sprintf "Syntax error in regular expression on line 1, character %d." character)
        elif Regex.IsMatch(pattern, @"(?<!\\)\\[1-9]\d{9,}") then
            InvalidPattern(3694, "Invalid back-reference in regular expression.")
        elif Regex.IsMatch(pattern, @"(?<!\\)\{\d{10,}(?:,\d*)?\}") then
            InvalidPattern(4007, "Decimal number in regular expression is too large.")
        else InvalidPattern(3691, "Invalid regular expression.")
    | "MalformedNamedReference" -> InvalidPattern(3887, "A capture group has an invalid name.")
    | "InsufficientOrInvalidHexDigits" -> InvalidPattern(3689, "Unrecognized escape sequence in regular expression.")
    | "InvalidUnicodePropertyEscape"
    | "UnrecognizedUnicodeProperty" -> InvalidPattern(3685, "Illegal argument to a regular expression.")
    | _ -> InvalidPattern(3691, "Invalid regular expression.")

let compile (collation: Collation.Collation) (matchType: string option) (pattern: string) : Result<Regex, RegexError> =
    let selectedOptions = optionsFor collation matchType
    let ignoreCase =
        match selectedOptions with
        | Ok options -> options &&& RegexOptions.IgnoreCase <> RegexOptions.None
        | Error _ -> false
    let posix, invalidPosix = normalizePosixClasses ignoreCase pattern
    match invalidPattern posix with
    | Some(code, message) -> Error(InvalidPattern(code, message))
    | None ->
        if invalidPosix then Error(InvalidPattern(3685, "Illegal argument to a regular expression."))
        else
            selectedOptions
            |> Result.bind (fun options ->
                normalizePossessiveQuantifiers (normalizePattern options posix)
                |> Result.bind (fun normalized ->
                    try
                        Ok(Regex(normalized, options, Limits.regexpMatchTimeout))
                    with
                    | :? RegexParseException as error -> Error(parseError posix error)
                    | :? ArgumentException -> Error(InvalidPattern(3691, "Invalid regular expression."))))
