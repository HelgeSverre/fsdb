/// Deployment-tunable ceilings, in one place because they're either read by
/// more than one module or plausibly changed at deploy time. A constant that
/// is neither — a read buffer's size, a recursion guard, a poll interval —
/// stays next to the rationale that explains it; hoisting those here would
/// only put every value further from the paragraph that justifies it.
///
/// Compiles before every consumer so early SQL and wire modules share the
/// same ceilings.
///
/// The mutable integer knobs may change while connections are live. Reads and
/// writes are atomic; a command already waiting keeps the value it captured,
/// while the next command observes the new setting.
module Fsdb.Limits

open System
open System.Threading

/// Safety ceiling for a reassembled multi-packet payload, and the value
/// advertised as `max_allowed_packet`. A malicious or buggy client can't
/// stream 0xffffff-byte chunks forever and make the server allocate without
/// bound. One number for both roles deliberately: advertising less than the
/// wire accepts made clients (MySqlConnector included) refuse >16 MiB
/// statements — a large blob as a hex literal — before ever sending them.
let mutable maxAllowedPacket = 64 * 1024 * 1024

/// LOCAL INFILE is disabled unless an operator explicitly enables it.
let mutable localInfile = false

/// Bounds a complete LOCAL INFILE upload, whose packet stream is not limited
/// by `max_allowed_packet` as one logical command.
let mutable maxLoadDataBytes = 64 * 1024 * 1024

/// Ceiling on concurrently handled connections. `Server.serve` reads it
/// after every accept, so a runtime raise or reduction applies to the next
/// connection without resizing a semaphore.
let mutable maxConnections = 500

/// Server-wide setting used as a per-session prepared-statement ceiling.
/// The narrower scope still bounds every individual connection's retained
/// ASTs without coupling otherwise independent sessions.
let mutable maxPreparedStmtCount = 16382

/// Ambient cancellation for synchronous query work. Long-running SQL and
/// engine loops share this token so disconnect and KILL do not depend on a
/// particular row-pipeline helper being on the call path.
let queryCancellation = new ThreadLocal<CancellationToken>(fun () -> CancellationToken.None)
let queryWorkDeadline = new ThreadLocal<int64 option>(fun () -> None)

let cancellationCheckInterval = 256

let checkQueryCancellation iteration =
    if iteration % cancellationCheckInterval = 0 then
        queryCancellation.Value.ThrowIfCancellationRequested()

let queryWorkDeadlineAfter (duration: TimeSpan) =
    System.Diagnostics.Stopwatch.GetTimestamp()
    + int64 (duration.TotalSeconds * float System.Diagnostics.Stopwatch.Frequency)

let queryWorkDeadlineExpired () =
    queryWorkDeadline.Value
    |> Option.exists (fun deadline -> System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)

let queryWorkDeadlineRemaining () =
    queryWorkDeadline.Value
    |> Option.map (fun deadline ->
        let ticks = max 0L (deadline - System.Diagnostics.Stopwatch.GetTimestamp())
        TimeSpan.FromSeconds(float ticks / float System.Diagnostics.Stopwatch.Frequency))

/// Explicit ceilings for functions whose successful result is constant-size
/// regardless of the requested work.
let maxSleepSeconds = 60.0
let maxBenchmarkIterations = 10_000_000L
let maxBenchmarkDuration = TimeSpan.FromSeconds 1.0

/// Feedback modes perform one AES block operation per bit or byte.
let maxAesCfb1Bytes = 1024
let maxAesCfb8Bytes = 16 * 1024

let maxGeometryDistanceComparisons = 10_000_000

/// Compression work is chosen by the server even when the client advertises
/// a more expensive level in its handshake response.
let maxZstdCompressionLevel = 3

/// A session can stream large values without retaining one backing page for
/// every parameter in an attacker-sized prepared-statement collection.
let maxLongDataParameters = 4096

/// Password lifetime inherited by accounts whose mysql.user row stores NULL.
let mutable defaultPasswordLifetimeDays = 0

/// Mode inherited by WEEK(date) when its optional second argument is absent.
let mutable defaultWeekFormat = 0

/// Idle timeout waiting for the next command packet.
let mutable waitTimeoutSeconds = 28800

/// Bounds greeting, TLS, and authentication exchanges before a session has
/// an account whose ordinary idle policy can apply.
let mutable connectTimeoutSeconds = 10

/// Idle timeout inherited by clients that negotiate CLIENT_INTERACTIVE.
let mutable interactiveTimeoutSeconds = 28800

/// Once a client starts a packet, bounds every pause before more bytes arrive.
/// Idle connections remain governed separately by `wait_timeout`.
let mutable netReadTimeoutSeconds = 30

/// Maximum time a socket write may remain blocked by a client that stopped
/// reading while keeping its connection open.
let mutable netWriteTimeoutSeconds = 60

/// How long a transaction waits to enter the commit/rebase section before
/// giving up with a retryable 1205 rather than blocking forever —
/// `innodb_lock_wait_timeout`'s MySQL default.
let mutable lockWaitTimeoutSeconds = 50

/// `WITH RECURSIVE`'s default pass ceiling. A session override is threaded
/// through the statement execution context.
let mutable cteMaxRecursionDepth = 1000L

/// Once the WAL crosses this many bytes, or this many appended entries,
/// whichever comes first, `Persistence.attach`'s subscriber snapshots the
/// whole catalog and truncates it — keeps startup replay bounded instead of
/// an ever-growing WAL.
let mutable walRotateBytes = 64L * 1024L * 1024L
let mutable walRotateEntries = 100_000
let mutable walGroupCommitQueueCapacity = 1024

/// The ReDoS ceiling on every `Regex.Match` fsdb runs against a
/// user-supplied pattern (the `REGEXP`/`RLIKE` operator and the `REGEXP_*`
/// functions): a catastrophically-backtracking pattern (`'(a+)+$'` against a
/// long non-matching subject) errors out instead of pinning a core forever.
let regexpMatchTimeout = TimeSpan.FromMilliseconds 100.0

/// Timeouts are stored as `int` seconds rather than `TimeSpan`: writing a
/// multi-field struct carries no atomicity guarantee, writing an `int` does,
/// and these are written while readers may already exist.
let lockWaitTimeout () = TimeSpan.FromSeconds(float lockWaitTimeoutSeconds)

// ---------------------------------------------------------------------------
// Configuration. One table drives setting, validation, and SHOW VARIABLES
// reporting so those paths cannot drift.
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
      { Name = "local_infile"
        Min = 0L
        Max = 1L
        Set = fun v -> localInfile <- v <> 0L
        Get = fun () -> if localInfile then 1L else 0L
        Reportable = true }
      { Name = "max_load_data_bytes"
        Min = 1024L
        Max = 1073741824L
        Set = fun v -> maxLoadDataBytes <- int v
        Get = fun () -> int64 maxLoadDataBytes
        Reportable = false }
      { Name = "max_connections"
        Min = 1L
        Max = 100000L
        Set = fun v -> maxConnections <- int v
        Get = fun () -> int64 maxConnections
        Reportable = true }
      { Name = "max_prepared_stmt_count"
        Min = 0L
        Max = 1048576L
        Set = fun v -> maxPreparedStmtCount <- int v
        Get = fun () -> int64 maxPreparedStmtCount
        Reportable = true }
      { Name = "default_password_lifetime"
        Min = 0L
        Max = 65535L
        Set = fun v -> defaultPasswordLifetimeDays <- int v
        Get = fun () -> int64 defaultPasswordLifetimeDays
        Reportable = true }
      { Name = "default_week_format"
        Min = 0L
        Max = 7L
        Set = fun v -> defaultWeekFormat <- int v
        Get = fun () -> int64 defaultWeekFormat
        Reportable = true }
      { Name = "wait_timeout"
        Min = 1L
        Max = 31536000L
        Set = fun v -> waitTimeoutSeconds <- int v
        Get = fun () -> int64 waitTimeoutSeconds
        Reportable = true }
      { Name = "connect_timeout"
        Min = 2L
        Max = 31536000L
        Set = fun v -> connectTimeoutSeconds <- int v
        Get = fun () -> int64 connectTimeoutSeconds
        Reportable = true }
      { Name = "interactive_timeout"
        Min = 1L
        Max = 31536000L
        Set = fun v -> interactiveTimeoutSeconds <- int v
        Get = fun () -> int64 interactiveTimeoutSeconds
        Reportable = true }
      { Name = "net_read_timeout"
        Min = 1L
        Max = 31536000L
        Set = fun v -> netReadTimeoutSeconds <- int v
        Get = fun () -> int64 netReadTimeoutSeconds
        Reportable = true }
      { Name = "net_write_timeout"
        Min = 1L
        Max = 31536000L
        Set = fun v -> netWriteTimeoutSeconds <- int v
        Get = fun () -> int64 netWriteTimeoutSeconds
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
        Set = fun v -> cteMaxRecursionDepth <- v
        Get = fun () -> cteMaxRecursionDepth
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
        Reportable = false }
      { Name = "wal_group_commit_queue_capacity"
        Min = 1L
        Max = 1000000L
        Set = fun v -> walGroupCommitQueueCapacity <- int v
        Get = fun () -> int64 walGroupCommitQueueCapacity
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
        | true, n ->
            // Checked: an unchecked multiply wraps, and the wrapped result
            // can land back inside a knob's range, so `18014398509483008K`
            // (2^64 + 1 MiB) would be accepted as 1 MiB rather than refused.
            try
                Some(Checked.(*) n (defaultArg multiplier 1L))
            with :? OverflowException ->
                None
        | _ -> None

/// my.cnf treats `-` and `_` in an option name as the same character, and
/// option names are case-insensitive.
let private normalizeName = OptionFile.normalizeName

/// Whether `name` is a knob at all — the question `loose-` asks, since that
/// prefix suppresses an unknown option but not a bad value for a known one.
let private isKnownSetting (name: string) : bool =
    knobs |> List.exists (fun k -> k.Name = normalizeName name)

let isReportableSetting (name: string) : bool =
    let name = normalizeName name
    knobs |> List.exists (fun knob -> knob.Name = name && knob.Reportable)

let private validatedSetting (name: string) (value: string) : Result<Knob * int64, string> =
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
        let parsed =
            if name = "local_infile" then
                match value.Trim().ToLowerInvariant() with
                | "1"
                | "on"
                | "true" -> Some 1L
                | "0"
                | "off"
                | "false" -> Some 0L
                | _ -> None
            else
                parseSize value

        match parsed with
        | None -> Error(sprintf "%s: '%s' is not a number (digits, optionally suffixed K, M or G)" name value)
        | Some n when n < knob.Min || n > knob.Max ->
            Error(sprintf "%s: %d is out of range %d..%d" name n knob.Min knob.Max)
        | Some n -> Ok(knob, n)

let validateSetting (name: string) (value: string) : Result<unit, string> =
    validatedSetting name value |> Result.map ignore

/// Sets one knob by its MySQL system-variable name, accepting `-` for `_`
/// the way my.cnf does. An unknown name, an unparseable value, or one
/// outside the accepted range is an `Error` the caller is expected to
/// surface and exit on — never a silent no-op, because a typo'd knob that
/// quietly does nothing is a production surprise found months later.
let applySetting (name: string) (value: string) : Result<unit, string> =
    validatedSetting name value
    |> Result.map (fun (knob, value) -> knob.Set value)

/// Every reportable knob's live value, for SHOW VARIABLES and `SELECT @@x`.
let variables () : (string * string) list =
    [ for knob in knobs do
          if knob.Reportable then
              let value =
                  if knob.Name = "local_infile" then
                      if localInfile then "ON" else "OFF"
                  else
                      string (knob.Get())

              knob.Name, value ]

/// Applies `settings` for the duration of `f`, restoring every knob
/// afterwards. The process-wide knobs need one gate so independent tests
/// cannot observe each other's temporary settings.
let private settingsGate = obj ()

let withSettings (settings: (string * string) list) (f: unit -> 'a) : 'a =
    lock settingsGate (fun () ->
        let saved = [ for knob in knobs -> knob.Name, string (knob.Get()) ]

        try
            for name, value in settings do
                match applySetting name value with
                | Ok() -> ()
                | Error message -> failwith message

            f ()
        finally
            for name, value in saved do
                applySetting name value |> ignore)

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
        if normalizeName bare = "local_infile" then
            applySetting bare "1"
        elif isKnownSetting bare then
            Error(sprintf "%s needs a value" (normalizeName bare))
        elif isLoose then
            Ok()
        else
            // Unknown either way; let `applySetting` phrase it, so the
            // "known: ..." list lives in exactly one place.
            applySetting bare ""

/// Applies options parsed from a my.cnf-style file. Every failure is
/// attributed to its source line, while valid entries still apply.
let private applyParsed (parsed: OptionFile.Parsed) : Result<unit, string> =
    let errors = ResizeArray<string>(parsed.Errors)

    for entry in parsed.Entries do
        match applyOption entry.Name entry.Value with
        | Ok() -> ()
        | Error message -> errors.Add(sprintf "%s:%d: %s" entry.Source entry.Line message)

    if errors.Count = 0 then Ok() else Error(String.concat "\n" errors)

/// Applies parsed option entries as Limits settings.
let applyEntries (entries: OptionFile.Entry list) : Result<unit, string> =
    ({ Entries = entries
       Errors = [] }: OptionFile.Parsed)
    |> applyParsed

/// Applies the server entries in `lines` as Limits settings.
let applyLines (source: string) (lines: string seq) : Result<unit, string> =
    OptionFile.parseLines source lines |> applyParsed

/// Reads and applies one my.cnf-style option file as Limits settings.
let loadDefaultsFile (path: string) : Result<unit, string> =
    OptionFile.parseFile path |> applyParsed

/// Server option files in MySQL's Unix precedence order.
let defaultFilePaths () : string list = OptionFile.defaultFilePaths ()

/// Reads and applies existing option files from least to most specific.
let loadDefaultsFiles (paths: string list) : Result<unit, string> =
    OptionFile.parseFiles paths |> applyParsed
