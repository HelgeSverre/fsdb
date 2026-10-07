module Fsdb.StorageOptions

type WordLengths =
    { Minimum: int
      Maximum: int }

let defaultWordLengths = { Minimum = 3; Maximum = 84 }

type StopwordTables =
    { UserTable: string option
      ServerTable: string option }

type Settings =
    { FullTextWordLengths: WordLengths
      NgramTokenSize: int
      FullTextStopwordsEnabled: bool
      FullTextStopwordTables: StopwordTables }

let defaults =
    { FullTextWordLengths = defaultWordLengths
      NgramTokenSize = 2
      FullTextStopwordsEnabled = true
      FullTextStopwordTables = { UserTable = None; ServerTable = None } }

let internal normalizeMinimumWordLength size = int (max 0L (min 16L size))
let internal normalizeMaximumWordLength size = int (max 10L (min 84L size))

let internal validWordLengths lengths =
    lengths.Minimum >= 0 && lengths.Minimum <= 16 && lengths.Maximum >= 10 && lengths.Maximum <= 84

let internal normalizeNgramTokenSize size = int (max 1L (min 10L size))

/// Consumes storage settings, leaving other server options for their owners.
let fromEntries (entries: OptionFile.Entry list) =
    let folder (settings, remaining, errors) (entry: OptionFile.Entry) =
        let name = OptionFile.normalizeName entry.Name
        let name = if name.StartsWith "loose_" then name.Substring 6 else name

        let stopwords enabled = { settings with FullTextStopwordsEnabled = enabled }, remaining, errors

        match name with
        | "innodb_ft_enable_stopword" ->
            // MySQL startup booleans treat unrecognized values as false, unlike SET.
            let enabled =
                match entry.Value |> Option.map (fun value -> value.ToLowerInvariant()) with
                | None | Some "1" | Some "on" | Some "true" -> true
                | _ -> false
            stopwords enabled
        | "skip_innodb_ft_enable_stopword" | "disable_innodb_ft_enable_stopword" -> stopwords false
        | "enable_innodb_ft_enable_stopword" -> stopwords true
        | "innodb_ft_user_stopword_table" ->
            { settings with FullTextStopwordTables = { settings.FullTextStopwordTables with UserTable = entry.Value } }, remaining, errors
        | "innodb_ft_server_stopword_table" ->
            { settings with FullTextStopwordTables = { settings.FullTextStopwordTables with ServerTable = entry.Value } }, remaining, errors
        | "innodb_ft_min_token_size" | "innodb_ft_max_token_size" ->
            match entry.Value |> Option.bind OptionFile.tryParseUnsignedSize with
            | Some size ->
                let bounded = int64 (min 84UL size)
                let lengths =
                    if name = "innodb_ft_min_token_size" then
                        { settings.FullTextWordLengths with Minimum = normalizeMinimumWordLength bounded }
                    else
                        { settings.FullTextWordLengths with Maximum = normalizeMaximumWordLength bounded }
                { settings with FullTextWordLengths = lengths }, remaining, errors
            | None ->
                let error = sprintf "%s:%d: %s requires an unsigned size with an optional K, M, G, T, P or E suffix" entry.Source entry.Line name
                settings, remaining, error :: errors
        | "ngram_token_size" ->
            match entry.Value |> Option.bind OptionFile.tryParseSize with
            | Some size -> { settings with NgramTokenSize = normalizeNgramTokenSize size }, remaining, errors
            | None ->
                let error =
                    sprintf "%s:%d: ngram_token_size requires an integer, optionally suffixed K, M or G" entry.Source entry.Line

                settings, remaining, error :: errors
        | _ -> settings, entry :: remaining, errors

    let settings, remaining, errors = List.fold folder (defaults, [], []) entries

    match errors with
    | [] -> Ok(settings, List.rev remaining)
    | _ -> Error(errors |> List.rev |> String.concat "\n")
