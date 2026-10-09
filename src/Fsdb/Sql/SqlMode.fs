module Fsdb.Sql.SqlMode

open System
open Fsdb.Parser

type Settings =
    { Strict: bool
      NoZeroDate: bool
      NoZeroInDate: bool
      AllowInvalidDates: bool
      OnlyFullGroupBy: bool
      NoEngineSubstitution: bool
      NoDirInCreate: bool
      NoAutoValueOnZero: bool
      ErrorForDivisionByZero: bool
      TimeTruncateFractional: bool
      PadCharToFullLength: bool }

let defaultText =
    "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION"

let private ansiModes =
    set [ "REAL_AS_FLOAT"; "PIPES_AS_CONCAT"; "ANSI_QUOTES"; "IGNORE_SPACE"; "ONLY_FULL_GROUP_BY" ]

let private traditionalModes =
    set
        [ "STRICT_TRANS_TABLES"
          "STRICT_ALL_TABLES"
          "NO_ZERO_IN_DATE"
          "NO_ZERO_DATE"
          "ERROR_FOR_DIVISION_BY_ZERO"
          "NO_ENGINE_SUBSTITUTION" ]

let private canonicalOrder =
    [ "REAL_AS_FLOAT"
      "PIPES_AS_CONCAT"
      "ANSI_QUOTES"
      "IGNORE_SPACE"
      "ONLY_FULL_GROUP_BY"
      "NO_UNSIGNED_SUBTRACTION"
      "NO_DIR_IN_CREATE"
      "ANSI"
      "NO_AUTO_VALUE_ON_ZERO"
      "NO_BACKSLASH_ESCAPES"
      "STRICT_TRANS_TABLES"
      "STRICT_ALL_TABLES"
      "NO_ZERO_IN_DATE"
      "NO_ZERO_DATE"
      "ALLOW_INVALID_DATES"
      "ERROR_FOR_DIVISION_BY_ZERO"
      "TRADITIONAL"
      "HIGH_NOT_PRECEDENCE"
      "NO_ENGINE_SUBSTITUTION"
      "PAD_CHAR_TO_FULL_LENGTH"
      "TIME_TRUNCATE_FRACTIONAL" ]

let private scanModes (value: string) =
    let mutable modes = Set.empty
    let mutable invalid = None
    let mutable start = 0
    while start <= value.Length && invalid.IsNone do
        let separator = value.IndexOf(',', start)
        let finish = if separator < 0 then value.Length else separator
        let mutable length = finish - start
        while length > 0 && Char.IsWhiteSpace value.[start + length - 1] do
            length <- length - 1
        if length > 0 then
            let name = value.AsSpan(start, length)
            let mutable matched = None
            for mode in canonicalOrder do
                if name.Equals(mode.AsSpan(), StringComparison.OrdinalIgnoreCase) then matched <- Some mode
            match matched with
            | Some mode -> modes <- Set.add mode modes
            | None -> invalid <- Some(value.Substring(start, length))
        start <- if separator < 0 then value.Length + 1 else separator + 1
    modes, invalid

let private parse (value: string) : Set<string> = scanModes value |> fst

let private expand (modes: Set<string>) =
    modes
    |> fun modes -> if modes.Contains "ANSI" then Set.union modes ansiModes else modes
    |> fun modes -> if modes.Contains "TRADITIONAL" then Set.union modes traditionalModes else modes

let private render (modes: Set<string>) =
    canonicalOrder
    |> List.filter modes.Contains
    |> fun names -> System.String.Join(',', names)

let tryNormalize (value: string) : Result<string, string> =
    let modes, invalid = scanModes value
    match invalid with
    | Some invalid -> Error invalid
    | None -> modes |> expand |> render |> Ok

let private enabled (modes: Set<string>) (name: string) =
    modes.Contains(name.ToUpperInvariant())

let settingsFor (value: string) : Settings =
    let modes = value |> parse |> expand

    { Strict = enabled modes "STRICT_TRANS_TABLES" || enabled modes "STRICT_ALL_TABLES"
      NoZeroDate = enabled modes "NO_ZERO_DATE"
      NoZeroInDate = enabled modes "NO_ZERO_IN_DATE"
      AllowInvalidDates = enabled modes "ALLOW_INVALID_DATES"
      OnlyFullGroupBy = enabled modes "ONLY_FULL_GROUP_BY"
      NoEngineSubstitution = enabled modes "NO_ENGINE_SUBSTITUTION"
      NoDirInCreate = enabled modes "NO_DIR_IN_CREATE"
      NoAutoValueOnZero = enabled modes "NO_AUTO_VALUE_ON_ZERO"
      ErrorForDivisionByZero = enabled modes "ERROR_FOR_DIVISION_BY_ZERO"
      TimeTruncateFractional = enabled modes "TIME_TRUNCATE_FRACTIONAL"
      PadCharToFullLength = enabled modes "PAD_CHAR_TO_FULL_LENGTH" }

let defaultSettings = settingsFor defaultText

let parserOptionsFor (value: string) : ParserOptions =
    let modes = value |> parse |> expand
    let ansi = enabled modes "ANSI"
    let grammarMode name = ansi || enabled modes name

    { defaultOptions with
        AnsiQuotes = grammarMode "ANSI_QUOTES"
        IgnoreSpace = grammarMode "IGNORE_SPACE"
        PipesAsConcat = grammarMode "PIPES_AS_CONCAT"
        HighNotPrecedence = enabled modes "HIGH_NOT_PRECEDENCE"
        NoUnsignedSubtraction = enabled modes "NO_UNSIGNED_SUBTRACTION"
        RealAsFloat = grammarMode "REAL_AS_FLOAT"
        NoBackslashEscapes = enabled modes "NO_BACKSLASH_ESCAPES" }

let withMode (name: string) (active: bool) (value: string) =
    let name = name.ToUpperInvariant()
    let modes = parse value
    let updated = if active then Set.add name modes else Set.remove name modes
    updated |> expand |> render
