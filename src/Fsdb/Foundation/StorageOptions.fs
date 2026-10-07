module Fsdb.StorageOptions

type StopwordTables =
    { UserTable: string option
      ServerTable: string option }

type Settings =
    { NgramTokenSize: int
      FullTextStopwordsEnabled: bool
      FullTextStopwordTables: StopwordTables }

let defaults =
    { NgramTokenSize = 2
      FullTextStopwordsEnabled = true
      FullTextStopwordTables = { UserTable = None; ServerTable = None } }

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
