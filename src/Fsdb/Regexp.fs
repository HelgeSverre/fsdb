/// The bounded .NET implementation of MySQL's ICU regular-expression entry
/// points. Collation chooses case sensitivity; accents remain literal regex
/// characters even under an accent-insensitive comparison collation.
module Fsdb.Regexp

open System
open System.Text.RegularExpressions

type RegexError =
    | InvalidPattern of message: string
    | InvalidMatchType

let errorMessage = function
    | InvalidPattern message -> message
    | InvalidMatchType -> ""

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

let compile (collation: Collation.Collation) (matchType: string option) (pattern: string) : Result<Regex, RegexError> =
    optionsFor collation matchType
    |> Result.bind (fun options ->
        try
            Ok(Regex(pattern, options, Limits.regexpMatchTimeout))
        with
        | :? RegexParseException as error when error.Error = RegexParseError.InsufficientClosingParentheses ->
            Error(InvalidPattern "Mismatched parenthesis in regular expression.")
        | :? ArgumentException ->
            Error(InvalidPattern "Invalid regular expression."))
