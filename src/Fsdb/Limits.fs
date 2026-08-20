/// Deployment-tunable ceilings, in one place because they're either read by
/// more than one module or plausibly changed at deploy time. A constant that
/// is neither — a read buffer's size, a recursion guard, a poll interval —
/// stays next to the rationale that explains it; hoisting those here would
/// only put every value further from the paragraph that justifies it.
///
/// Compiled second, right after `Log`, so nothing in the dependency-ordered
/// build (see AGENTS.md) is too early to read it. That's the whole point:
/// `Functions` and `Packet` each grew their own copy of the 64 MiB packet
/// ceiling purely because `Functions` compiles first and couldn't see
/// `Packet`'s.
///
/// Every `mutable` here is written at most once, by `Program`'s config
/// parsing, before the listener exists — so a reader either runs before any
/// connection was possible or sees the final value. Nothing rewrites one
/// mid-flight. ponytail: startup-scoped, so `SET GLOBAL max_connections`
/// lands in the session layer's override map and shows up in SHOW GLOBAL
/// VARIABLES while the running server keeps its startup value; making a knob
/// live means resizing a semaphore under load for a knob nobody has needed.
module Fsdb.Limits

open System

/// Safety ceiling for a reassembled multi-packet payload, and the value
/// advertised as `max_allowed_packet`. A malicious or buggy client can't
/// stream 0xffffff-byte chunks forever and make the server allocate without
/// bound. One number for both roles deliberately: advertising less than the
/// wire accepts made clients (MySqlConnector included) refuse >16 MiB
/// statements — a large blob as a hex literal — before ever sending them.
let mutable maxAllowedPacket = 64 * 1024 * 1024

/// Ceiling on concurrently handled connections — past this, `Server.serve`
/// stops calling `AcceptTcpClientAsync` until a slot frees, so excess
/// attempts queue at the OS socket backlog (or get refused) instead of each
/// one costing this process a thread-pool task and a read buffer's worth of
/// memory pressure.
let mutable maxConnections = 500

/// Idle timeout waiting for the *next* command packet — `wait_timeout`'s
/// semantics. fsdb's default is 300s where MySQL's is 28800: a half-open
/// peer that connects and then says nothing otherwise pins a socket and a
/// thread-pool task for eight hours, which at `maxConnections` is a real
/// denial-of-service surface. Advertised as this number rather than
/// advertising MySQL's and enforcing this one — a pool that reads
/// `wait_timeout` to size its idle-recycle needs the truth, or it hands the
/// application a connection the server closed hours earlier.
let mutable waitTimeoutSeconds = 300

/// How long a connection waits for a database's write gate before giving up
/// with a retryable 1205 rather than blocking forever —
/// `innodb_lock_wait_timeout`'s MySQL default.
let mutable lockWaitTimeoutSeconds = 50

/// `WITH RECURSIVE`'s pass ceiling, MySQL's `cte_max_recursion_depth`
/// default. ponytail: startup-scoped, not per-session — `Executor` compiles
/// well before `Session` and can't read `@@cte_max_recursion_depth`, so a
/// client that SETs it is ignored.
let mutable cteMaxRecursionDepth = 1000

/// Once the WAL crosses this many bytes, or this many appended entries,
/// whichever comes first, `Persistence.attach`'s subscriber snapshots the
/// whole catalog and truncates it — keeps startup replay bounded instead of
/// an ever-growing WAL.
let mutable walRotateBytes = 64L * 1024L * 1024L
let mutable walRotateEntries = 100_000

/// The ReDoS ceiling on every `Regex.Match` fsdb runs against a
/// user-supplied pattern (the `REGEXP`/`RLIKE` operator and the `REGEXP_*`
/// functions): a catastrophically-backtracking pattern (`'(a+)+$'` against a
/// long non-matching subject) errors out instead of pinning a core forever.
/// Not configurable — it lives here only because `Functions` and `Executor`
/// both need it and each had grown its own copy of the same five seconds.
let regexpMatchTimeout = TimeSpan.FromSeconds 5.0

/// Timeouts are stored as `int` seconds rather than `TimeSpan`: writing a
/// multi-field struct carries no atomicity guarantee, writing an `int` does,
/// and these are written while readers may already exist.
let lockWaitTimeout () = TimeSpan.FromSeconds(float lockWaitTimeoutSeconds)

// ---------------------------------------------------------------------------
// Configuration. One table drives all three of applying a setting,
// validating it, and reporting it back through SHOW VARIABLES, so a knob
// added to `knobs` is configurable and reportable with no further edits.
// ---------------------------------------------------------------------------

/// One configurable knob. `Reportable` is false for the WAL rotation
/// thresholds: MySQL has no such system variables, and inventing them in
/// SHOW VARIABLES would advertise a compatibility that isn't there.
type private Knob =
    { Name: string
      Min: int64
      Max: int64
      Set: int64 -> unit
      Get: unit -> int64
      Reportable: bool }

let private knobs =
    [ { Name = "max_allowed_packet"
        Min = 1024L
        Max = 1073741824L
        Set = fun v -> maxAllowedPacket <- int v
        Get = fun () -> int64 maxAllowedPacket
        Reportable = true }
      { Name = "max_connections"
        Min = 1L
        Max = 100000L
        Set = fun v -> maxConnections <- int v
        Get = fun () -> int64 maxConnections
        Reportable = true }
      { Name = "wait_timeout"
        Min = 1L
        Max = 31536000L
        Set = fun v -> waitTimeoutSeconds <- int v
        Get = fun () -> int64 waitTimeoutSeconds
        Reportable = true }
      { Name = "innodb_lock_wait_timeout"
        Min = 1L
        Max = 1073741824L
        Set = fun v -> lockWaitTimeoutSeconds <- int v
        Get = fun () -> int64 lockWaitTimeoutSeconds
        Reportable = true }
      { Name = "cte_max_recursion_depth"
        Min = 0L
        Max = 4294967295L
        Set = fun v -> cteMaxRecursionDepth <- int v
        Get = fun () -> int64 cteMaxRecursionDepth
        Reportable = true }
      { Name = "wal_rotate_bytes"
        Min = 0L
        Max = 1099511627776L
        Set = fun v -> walRotateBytes <- v
        Get = fun () -> walRotateBytes
        Reportable = false }
      { Name = "wal_rotate_entries"
        Min = 0L
        Max = 1000000000L
        Set = fun v -> walRotateEntries <- int v
        Get = fun () -> int64 walRotateEntries
        Reportable = false } ]

/// MySQL's size suffixes: `64M`, `16K`, `1G`. Plain digits pass through.
/// Deliberately strict — `64MB` and `64 megs` are errors, not a guess.
let private parseSize (text: string) : int64 option =
    let text = text.Trim()

    if text = "" then
        None
    else
        let multiplier =
            match Char.ToUpperInvariant text.[text.Length - 1] with
            | 'K' -> Some 1024L
            | 'M' -> Some(1024L * 1024L)
            | 'G' -> Some(1024L * 1024L * 1024L)
            | _ -> None

        let digits =
            if multiplier.IsSome then
                text.Substring(0, text.Length - 1).Trim()
            else
                text

        match Int64.TryParse digits with
        | true, n -> Some(n * defaultArg multiplier 1L)
        | _ -> None

/// my.cnf treats `-` and `_` in an option name as the same character, and
/// option names are case-insensitive.
let private normalizeName (name: string) = name.Trim().Replace('-', '_').ToLowerInvariant()

/// Whether `name` is a knob at all — the question `loose-` asks, since that
/// prefix suppresses an unknown option but not a bad value for a known one.
let isKnownSetting (name: string) : bool =
    knobs |> List.exists (fun k -> k.Name = normalizeName name)

/// Sets one knob by its MySQL system-variable name, accepting `-` for `_`
/// the way my.cnf does. An unknown name, an unparseable value, or one
/// outside the accepted range is an `Error` the caller is expected to
/// surface and exit on — never a silent no-op, because a typo'd knob that
/// quietly does nothing is a production surprise found months later.
let applySetting (name: string) (value: string) : Result<unit, string> =
    let name = normalizeName name

    match knobs |> List.tryFind (fun k -> k.Name = name) with
    | None ->
        Error(
            sprintf
                "unknown setting '%s' (known: %s)"
                name
                (knobs |> List.map (fun k -> k.Name) |> String.concat ", ")
        )
    | Some knob ->
        match parseSize value with
        | None -> Error(sprintf "%s: '%s' is not a number (digits, optionally suffixed K, M or G)" name value)
        | Some n when n < knob.Min || n > knob.Max ->
            Error(sprintf "%s: %d is out of range %d..%d" name n knob.Min knob.Max)
        | Some n ->
            knob.Set n
            Ok()

/// Every reportable knob's live value, for SHOW VARIABLES and `SELECT @@x`.
/// `interactive_timeout` mirrors `wait_timeout` because fsdb ignores
/// `CLIENT_INTERACTIVE` at handshake — reporting two different numbers would
/// advertise a distinction the server doesn't actually make.
let variables () : (string * string) list =
    [ for knob in knobs do
          if knob.Reportable then
              knob.Name, string (knob.Get()) ]
    @ [ "interactive_timeout", string waitTimeoutSeconds ]

/// Applies `settings` for the duration of `f`, restoring every knob
/// afterwards. The whole test suite runs in one process, so an override that
/// leaks silently changes unrelated tests. Goes through `applySetting` so a
/// test that tunes a knob also exercises the parser that production uses.
/// ponytail: neither thread-safe nor nestable — keep its callers in a
/// `testSequenced` list.
let withSettings (settings: (string * string) list) (f: unit -> 'a) : 'a =
    let saved = [ for knob in knobs -> knob.Name, string (knob.Get()) ]

    for name, value in settings do
        match applySetting name value with
        | Ok() -> ()
        | Error message -> failwith message

    try
        f ()
    finally
        for name, value in saved do
            applySetting name value |> ignore

// ---------------------------------------------------------------------------
// my.cnf parsing. The format looks like ini but isn't one: a bare option name
// is a boolean, values may be quoted with escape sequences inside, a comment
// can start mid-line, `loose-` downgrades an unknown option to a skip, and
// `!include`/`!includedir` pull in other files. Reading the real format is
// what lets an unrecognised line be diagnosed as what it actually is — an
// unknown option — instead of as a syntax error.
// https://dev.mysql.com/doc/refman/8.4/en/option-files.html
// ---------------------------------------------------------------------------

/// Cuts a `#` or `;` comment, which may start mid-line, without cutting one
/// inside a quoted value — MySQL's own rule is to quote a value containing a
/// comment character.
let private stripComment (line: string) : string =
    let mutable quote = '\000'
    let mutable cut = -1
    let mutable i = 0

    while cut < 0 && i < line.Length do
        let c = line.[i]

        if quote <> '\000' then
            if c = '\\' then i <- i + 1
            elif c = quote then quote <- '\000'
        elif c = '"' || c = '\'' then
            quote <- c
        elif c = '#' || c = ';' then
            cut <- i

        i <- i + 1

    if cut < 0 then line else line.Substring(0, cut)

/// Unwraps a quoted value and expands the escapes MySQL recognises inside
/// quotes. A backslash before anything else stays literal, backslash
/// included, which is also MySQL's behaviour.
let private unquote (raw: string) : string =
    let raw = raw.Trim()

    if raw.Length >= 2 && (raw.[0] = '"' || raw.[0] = '\'') && raw.[raw.Length - 1] = raw.[0] then
        let inner = raw.Substring(1, raw.Length - 2)
        let out = Text.StringBuilder()
        let mutable i = 0

        while i < inner.Length do
            if inner.[i] = '\\' && i + 1 < inner.Length then
                out.Append(
                    match inner.[i + 1] with
                    | 'b' -> "\b"
                    | 't' -> "\t"
                    | 'n' -> "\n"
                    | 'r' -> "\r"
                    | 's' -> " "
                    | '\\' -> "\\"
                    | other -> "\\" + string other
                )
                |> ignore

                i <- i + 2
            else
                out.Append inner.[i] |> ignore
                i <- i + 1

        out.ToString()
    else
        raw

/// How deep `!include` may nest before it's assumed to be a mistake. The
/// visited-path set already stops a true cycle; this catches a long chain
/// that isn't one.
let private maxIncludeDepth = 10

/// Applies one option line's `name` / optional `value`. `loose-` (MySQL's
/// "tolerate this if you don't know it") suppresses an *unknown option*, and
/// only that — a bad value for a known option still fails, or a typo in a
/// value would be tolerated too.
let private applyOption (name: string) (value: string option) : Result<unit, string> =
    let isLoose = (normalizeName name).StartsWith "loose_"
    let bare = if isLoose then name.Substring 6 else name

    match value with
    | Some v ->
        match applySetting bare v with
        | Error _ when isLoose && not (isKnownSetting bare) -> Ok()
        | result -> result
    | None ->
        // A bare name is MySQL's boolean form. fsdb has no boolean knob, so
        // the only useful thing to say is which of the two mistakes it is.
        if isKnownSetting bare then
            Error(sprintf "%s needs a value" (normalizeName bare))
        elif isLoose then
            Ok()
        else
            // Unknown either way; let `applySetting` phrase it, so the
            // "known: ..." list lives in exactly one place.
            applySetting bare ""

let rec private applyInto
    (errors: ResizeArray<string>)
    (visited: Collections.Generic.HashSet<string>)
    (depth: int)
    (source: string)
    (lines: string seq)
    : unit =
    let baseDir =
        match IO.Path.GetDirectoryName source with
        | null
        | "" -> "."
        | dir -> dir

    // Group state is per file: an included file starts outside any group and
    // cannot leak its own group back to the file that included it.
    let mutable applies = false

    lines
    |> Seq.iteri (fun i raw ->
        let fail message = errors.Add(sprintf "%s:%d: %s" source (i + 1) message)
        let line = (stripComment raw).Trim()

        if line = "" then
            ()
        elif line.StartsWith "[" then
            if line.EndsWith "]" then
                // `[server]` is read by mysqld alongside `[mysqld]`. Every
                // other group — `[client]`, `[mysqldump]`, and the
                // version-suffixed `[mysqld-8.4]` form fsdb has no version to
                // match against — belongs to some other program and is
                // skipped, which is what makes pointing this at a shared
                // my.cnf safe.
                let group = normalizeName (line.Substring(1, line.Length - 2))
                applies <- group = "mysqld" || group = "server"
            else
                fail (sprintf "unterminated group header '%s'" line)
        elif line.StartsWith "!" then
            // Include directives are file-scoped, not group-scoped, so they
            // run wherever they appear.
            let directive, rest =
                match line.IndexOf ' ' with
                | -1 -> line, ""
                | at -> line.Substring(0, at), line.Substring(at + 1).Trim()

            let resolve (p: string) =
                if IO.Path.IsPathRooted p then p else IO.Path.Combine(baseDir, p)

            match directive with
            | "!include"
            | "!includedir" when rest = "" -> fail (sprintf "%s needs a path" directive)
            | "!include" -> includeFile errors visited depth fail (resolve rest)
            | "!includedir" ->
                let dir = resolve rest

                if not (IO.Directory.Exists dir) then
                    fail (sprintf "!includedir: no such directory '%s'" dir)
                else
                    // Sorted so a directory of fragments applies in a
                    // predictable order rather than whatever the filesystem
                    // hands back.
                    IO.Directory.GetFiles(dir, "*.cnf")
                    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    |> Array.iter (includeFile errors visited depth fail)
            | other -> fail (sprintf "unknown directive '%s'" other)
        elif not applies then
            ()
        else
            let name, value =
                match line.IndexOf '=' with
                | -1 -> line, None
                | at -> line.Substring(0, at), Some(unquote (line.Substring(at + 1)))

            match applyOption (name.Trim()) value with
            | Ok() -> ()
            | Error message -> fail message)

and private includeFile
    (errors: ResizeArray<string>)
    (visited: Collections.Generic.HashSet<string>)
    (depth: int)
    (fail: string -> unit)
    (path: string)
    : unit =
    if depth >= maxIncludeDepth then
        fail (sprintf "!include nested more than %d deep at '%s'" maxIncludeDepth path)
    else
        let full =
            try
                IO.Path.GetFullPath path
            with _ ->
                path

        // A file that includes itself, directly or around a loop, would
        // otherwise recurse until the depth limit and report the same errors
        // once per level.
        if not (visited.Add full) then
            ()
        else
            match (try Ok(IO.File.ReadAllLines full) with ex -> Error ex.Message) with
            | Error message -> fail (sprintf "!include '%s': %s" path message)
            | Ok lines -> applyInto errors visited (depth + 1) full lines

/// Applies the `[mysqld]`/`[server]` options in `lines`, attributing every
/// failure to `source` and its line number. Split out from
/// `loadDefaultsFile` so the parsing is testable without a file, and so an
/// `!include` inside it resolves relative to `source`'s directory.
/// Every failure is collected rather than stopping at the first — someone
/// fixing a config wants the whole list, not one error per restart.
let applyLines (source: string) (lines: string seq) : Result<unit, string> =
    let errors = ResizeArray<string>()
    applyInto errors (Collections.Generic.HashSet<string>()) 0 source lines

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "\n" errors)

/// Reads a my.cnf-style option file: `[mysqld]` and `[server]` groups,
/// `name = value` and MySQL's bare-name boolean form, `#`/`;` comments that
/// may start mid-line, quoted values with `\n`/`\t`/`\s`-style escapes, `-`
/// and `_` interchangeable in names, `loose-` to tolerate an option fsdb
/// doesn't have, and `!include`/`!includedir`.
///
/// No config path is auto-discovered — not `/etc/my.cnf`, not `~/.my.cnf`.
/// A file that applies without being named on the command line is the most
/// reliable way to make production differ from a laptop.
let loadDefaultsFile (path: string) : Result<unit, string> =
    match (try Ok(IO.File.ReadAllLines path) with ex -> Error ex.Message) with
    | Error message -> Error(sprintf "%s: %s" path message)
    | Ok lines ->
        let errors = ResizeArray<string>()
        let visited = Collections.Generic.HashSet<string>()
        visited.Add(try IO.Path.GetFullPath path with _ -> path) |> ignore
        applyInto errors visited 0 path lines

        if errors.Count = 0 then
            Ok()
        else
            Error(String.concat "\n" errors)
