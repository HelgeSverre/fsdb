module Fsdb.Sql.SqlMode

open Fsdb.Parser

type Settings =
    { Strict: bool
      NoZeroDate: bool
      NoZeroInDate: bool
      OnlyFullGroupBy: bool
      NoAutoValueOnZero: bool
      ErrorForDivisionByZero: bool
      TimeTruncateFractional: bool
      PadCharToFullLength: bool }

let defaultText =
    "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION"

let private traditionalModes =
    set
        [ "STRICT_TRANS_TABLES"
          "STRICT_ALL_TABLES"
          "NO_ZERO_IN_DATE"
          "NO_ZERO_DATE"
          "ERROR_FOR_DIVISION_BY_ZERO"
          "NO_ENGINE_SUBSTITUTION" ]

let private parse (value: string) : Set<string> =
    value.Split(',')
    |> Seq.map (fun mode -> mode.Trim().ToUpperInvariant())
    |> Seq.filter (System.String.IsNullOrEmpty >> not)
    |> Set.ofSeq

let private enabled (modes: Set<string>) (name: string) =
    let requested = name.ToUpperInvariant()
    modes.Contains requested
    || modes.Contains "TRADITIONAL" && traditionalModes.Contains requested

let settingsFor (value: string) : Settings =
    let modes = parse value

    { Strict = enabled modes "STRICT_TRANS_TABLES" || enabled modes "STRICT_ALL_TABLES"
      NoZeroDate = enabled modes "NO_ZERO_DATE"
      NoZeroInDate = enabled modes "NO_ZERO_IN_DATE"
      OnlyFullGroupBy = enabled modes "ONLY_FULL_GROUP_BY"
      NoAutoValueOnZero = enabled modes "NO_AUTO_VALUE_ON_ZERO"
      ErrorForDivisionByZero = enabled modes "ERROR_FOR_DIVISION_BY_ZERO"
      TimeTruncateFractional = enabled modes "TIME_TRUNCATE_FRACTIONAL"
      PadCharToFullLength = enabled modes "PAD_CHAR_TO_FULL_LENGTH" }

let defaultSettings = settingsFor defaultText

let parserOptionsFor (value: string) : ParserOptions =
    let modes = parse value
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
    System.String.Join(',', updated)
