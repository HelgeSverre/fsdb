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

let private normalizePattern (options: RegexOptions) (pattern: string) : string =
    let posix = posixClasses |> List.fold (fun (value: string) (source, target) -> value.Replace(source, target, StringComparison.Ordinal)) pattern

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

let prepareInput (matchType: string option) (text: string) =
    let multiline = matchType |> Option.defaultValue "" |> String.exists ((=) 'm')
    let unixLines = matchType |> Option.defaultValue "" |> String.exists ((=) 'u')

    if multiline && not unixLines then text.Replace('\r', '\n') else text

let compile (collation: Collation.Collation) (matchType: string option) (pattern: string) : Result<Regex, RegexError> =
    optionsFor collation matchType
    |> Result.bind (fun options ->
        try
            Ok(Regex(normalizePattern options pattern, options, Limits.regexpMatchTimeout))
        with
        | :? RegexParseException as error when error.Error = RegexParseError.InsufficientClosingParentheses ->
            Error(InvalidPattern(3691, "Mismatched parenthesis in regular expression."))
        | :? RegexParseException as error when error.Error.ToString() = "UnterminatedBracket" ->
            Error(InvalidPattern(3696, "The regular expression contains an unclosed bracket expression."))
        | :? ArgumentException ->
            Error(InvalidPattern(3691, "Invalid regular expression.")))
