module Fsdb.StorageOptions

type Settings = { NgramTokenSize: int }

let defaults = { NgramTokenSize = 2 }

let internal normalizeNgramTokenSize size = int (max 1L (min 10L size))

/// Consumes storage settings, leaving other server options for their owners.
let fromEntries (entries: OptionFile.Entry list) =
    let folder (settings, remaining, errors) (entry: OptionFile.Entry) =
        let name = OptionFile.normalizeName entry.Name
        let name = if name.StartsWith "loose_" then name.Substring 6 else name

        if name <> "ngram_token_size" then
            settings, entry :: remaining, errors
        else
            match entry.Value |> Option.bind OptionFile.tryParseSize with
            | Some size -> { NgramTokenSize = normalizeNgramTokenSize size }, remaining, errors
            | None ->
                let error =
                    sprintf "%s:%d: ngram_token_size requires an integer, optionally suffixed K, M or G" entry.Source entry.Line

                settings, remaining, error :: errors

    let settings, remaining, errors = List.fold folder (defaults, [], []) entries

    match errors with
    | [] -> Ok(settings, List.rev remaining)
    | _ -> Error(errors |> List.rev |> String.concat "\n")
