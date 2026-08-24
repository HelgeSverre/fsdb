/// Where fsdb's diagnostic stderr output goes — connection drops, WAL replay
/// warnings, query errors surfaced server-side, not the wire-protocol error
/// sent to the client. One mutable sink so tests can silence or capture it
/// without an interface/DI layer for a single function.
module Fsdb.Log

let mutable private sink: string -> unit = eprintfn "%s"

/// Points every diagnostic at `f` instead of stderr — tests use this to
/// capture or silence output; embedders use `Db.withLogger`.
let useSink (f: string -> unit) : unit = sink <- f

/// Drops every diagnostic on the floor.
let silence () : unit = sink <- ignore

/// Formats and routes one diagnostic line through the current sink.
let diagnostic fmt = Printf.kprintf sink fmt

let private maxSqlLogLength = 1024

/// Removes secrets before SQL text crosses an observability boundary — the
/// diagnostic log and the PROCESSLIST `INFO` column. A statement that sets a
/// credential collapses whole (the password is a bare token, not a quotable
/// literal); everything else keeps its shape with string/number literals
/// replaced by `?`. Quote-aware so a `#`/`--` or a keyword inside a literal
/// isn't mistaken for structure.
let redactSql (sql: string) : string =
    let mutable start = 0

    while start < sql.Length && System.Char.IsWhiteSpace sql.[start] do
        start <- start + 1

    let isCredential =
        sql.IndexOf("CREATE USER", start, System.StringComparison.OrdinalIgnoreCase) = start
        || sql.IndexOf("ALTER USER", start, System.StringComparison.OrdinalIgnoreCase) = start
        || sql.IndexOf("SET PASSWORD", start, System.StringComparison.OrdinalIgnoreCase) = start
        || sql.IndexOf("IDENTIFIED BY", System.StringComparison.OrdinalIgnoreCase) >= 0
        || sql.IndexOf("IDENTIFIED WITH", System.StringComparison.OrdinalIgnoreCase) >= 0

    if isCredential then
        "[REDACTED CREDENTIAL STATEMENT]"
    else
        let sb = System.Text.StringBuilder(min sql.Length maxSqlLogLength)
        let n = sql.Length
        let mutable i = 0
        let mutable truncated = false

        let append (c: char) =
            if sb.Length < maxSqlLogLength then
                sb.Append(if System.Char.IsControl c then '?' else c) |> ignore
            else
                truncated <- true

        while i < n && not truncated do
            let c = sql.[i]

            if c = '\'' || c = '"' then
                // Skip the whole literal, emit one placeholder for it.
                let quote = c
                i <- i + 1
                let mutable closed = false

                while not closed && i < n do
                    if sql.[i] = '\\' && i + 1 < n then i <- i + 2
                    elif sql.[i] = quote && i + 1 < n && sql.[i + 1] = quote then i <- i + 2
                    elif sql.[i] = quote then
                        i <- i + 1
                        closed <- true
                    else
                        i <- i + 1

                append '?'
            elif c = '`' then
                // Backticked identifier — structure, keep it verbatim.
                append c
                i <- i + 1

                while i < n && sql.[i] <> '`' && not truncated do
                    append sql.[i]
                    i <- i + 1

                if i < n && not truncated then
                    append '`'
                    i <- i + 1
            elif System.Char.IsDigit c && (i = 0 || not (System.Char.IsLetterOrDigit sql.[i - 1] || sql.[i - 1] = '_')) then
                // A numeric literal (not an identifier's trailing digits).
                while i < n && (System.Char.IsDigit sql.[i] || sql.[i] = '.') do
                    i <- i + 1

                append '?'
            else
                append c
                i <- i + 1

        if truncated then
            sb.Length <- maxSqlLogLength - 3
            sb.Append("...") |> ignore

        sb.ToString()
