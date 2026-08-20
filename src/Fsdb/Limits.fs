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

/// Sets one knob by its MySQL system-variable name, accepting `-` for `_`
/// the way my.cnf does. An unknown name, an unparseable value, or one
/// outside the accepted range is an `Error` the caller is expected to
/// surface and exit on — never a silent no-op, because a typo'd knob that
/// quietly does nothing is a production surprise found months later.
let applySetting (name: string) (value: string) : Result<unit, string> =
    let name = name.Trim().Replace('-', '_').ToLowerInvariant()

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

/// The `[mysqld]` section of `lines`, applied in order. Split out from
/// `loadDefaultsFile` so the parsing can be tested without a real file.
/// Every failure is collected rather than stopping at the first — someone
/// fixing a config wants the whole list, not one error per restart.
let applyLines (source: string) (lines: string seq) : Result<unit, string> =
    let errors = ResizeArray<string>()
    let mutable section = ""

    lines
    |> Seq.iteri (fun i raw ->
        let line = raw.Trim()
        let fail message = errors.Add(sprintf "%s:%d: %s" source (i + 1) message)

        if line = "" || line.StartsWith "#" || line.StartsWith ";" then
            ()
        elif line.StartsWith "[" && line.EndsWith "]" then
            section <- line.Trim([| '['; ']'; ' ' |]).ToLowerInvariant()
        // A real my.cnf carries `[client]`, `[mysqldump]` and friends that
        // fsdb has no business rejecting. Inside `[mysqld]`, though, every
        // line must mean something — that strictness is what keeps this from
        // growing into a general ini parser.
        elif section <> "mysqld" then
            ()
        else
            match line.IndexOf '=' with
            | -1 -> fail (sprintf "expected 'key = value', got '%s'" line)
            | i ->
                let value = line.Substring(i + 1).Trim().Trim([| '"'; '\'' |])

                match applySetting (line.Substring(0, i)) value with
                | Ok() -> ()
                | Error message -> fail message)

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "\n" errors)

/// Reads a deliberately small my.cnf subset: `[mysqld]` only, `key = value`,
/// `#`/`;` comments, `-` and `_` interchangeable in keys. No `!include`, no
/// bare boolean flags, and no auto-discovery of `/etc/my.cnf` or `~/.my.cnf`
/// — a config that applies without being named on the command line is the
/// most reliable way to make production differ from a laptop.
let loadDefaultsFile (path: string) : Result<unit, string> =
    try
        applyLines path (IO.File.ReadLines path)
    with ex ->
        Error(sprintf "%s: %s" path ex.Message)
