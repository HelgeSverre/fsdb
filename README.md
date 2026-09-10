# fsdb

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](global.json)
[![MySQL 8.4 wire protocol](https://img.shields.io/badge/MySQL-8.4%20wire%20protocol-4479A1.svg)](docs/compatibility.md)

A MySQL-compatible database server in idiomatic F#. It speaks the MySQL wire
protocol, so clients such as `mysql`, PDO, and MySqlConnector connect without
an fsdb-specific adapter. Internally, a query follows one readable pipeline:
bytes → command → AST → logical plan → lazy `seq`.

MySQL 8.4 is the compatibility oracle; SQLite is not. Readable F# is the
primary design constraint, ahead of raw performance. The default server is
in-memory, with an opt-in binary WAL and snapshots for durable use. The
[compatibility guide](docs/compatibility.md) explains how behavior is
validated; [GAPS.md](GAPS.md) is the live ledger of known differences.

## Contents

- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Security and deployment](#security-and-deployment)
- [How it works](#how-it-works)
  - [Parser](#parser)
  - [Engine](#engine)
  - [Collations and charsets](#collations--charsets)
  - [Prepared statements](#prepared-statements)
- [SQL surface](#sql-surface)
- [Persistence format](#persistence-format)
  - [Write-ahead log](#write-ahead-log)
  - [Snapshots](#snapshots)
- [Embedding & extensibility](#embedding--extensibility)
  - [Create an embedded host](#create-an-embedded-host)
  - [Register an aggregate](#register-an-aggregate)
  - [Use query context and cancellation](#use-query-context-and-cancellation)
  - [Expose host data as a virtual table](#expose-host-data-as-a-virtual-table)
  - [Consume committed changes](#consume-committed-changes)
  - [Run SQL in-process or over the wire](#run-sql-in-process-or-over-the-wire)
  - [Included examples](#included-examples)
- [Benchmarking](#benchmarking)
- [Development](#development)
- [Documentation](#documentation)

## Quick start

The server requires the .NET 10 SDK pinned by `global.json`. The walkthrough
also uses a MySQL client. [`just`](https://github.com/casey/just) is optional,
but provides the repository's standard commands.

```sh
dotnet run --project src/Fsdb        # listens on 127.0.0.1:3307
mysql --protocol=tcp -h127.0.0.1 -P3307 -uroot -e 'SELECT 1'
```

With `just`, the same two-terminal workflow is `just run` and `just client`.

Port 3307 avoids a real MySQL on 3306 (`--port` overrides). The bootstrap
account is `root` with all privileges and no password, intended only for the
default loopback listener. See [Security and deployment](#security-and-deployment)
before accepting remote connections.

Manage accounts and grants with `CREATE USER`, `GRANT`, and `SET PASSWORD`.
Account locks, expiry, history, reuse, current-password rules, resource
limits, and JSON attributes or comments are enforced.

TLS policy can require encryption, a CA-validated client certificate, or
exact certificate subject, issuer, and cipher values.

First queries:

```sql
CREATE DATABASE app;
USE app;
CREATE TABLE notes (id BIGINT AUTO_INCREMENT PRIMARY KEY, body TEXT);
INSERT INTO notes (body) VALUES ('hello fsdb');
SELECT * FROM notes;
```

Common MySQL clients connect without an fsdb-specific driver:

| Client | Connect |
|---|---|
| mysql CLI | `mysql --protocol=tcp -h127.0.0.1 -P3307 -uroot` |
| PDO (PHP) | `new PDO('mysql:host=127.0.0.1;port=3307;dbname=app', 'root', '')` |
| MySqlConnector (.NET) | `new MySqlConnection("Server=127.0.0.1;Port=3307;User ID=root;Database=app")` |

Install as a single self-contained binary (no .NET needed on the machine):

```sh
just install      # publishes to ~/.local/bin/fsdb, then: fsdb --help
```

`fsdb --help` is the authoritative command-line reference. The startup options
fall into a few groups:

| Concern | Options |
|---|---|
| Listener | `--listen`, `--port` / `-p` |
| Storage | `--data-dir` |
| Option files | `--defaults-file` |
| TLS | `--ssl-cert`, `--ssl-key`, `--ssl-ca`, `--require-secure-transport` |
| Server files | `--secure-file-priv` |
| Caching SHA-2 RSA | `--caching-sha2-password-private-key-path`, `--caching-sha2-password-public-key-path` |
| SHA-256 RSA | `--sha256-password-private-key-path`, `--sha256-password-public-key-path` |
| Information | `--version`, `--help` |

## Configuration

### Option files

fsdb reads `/etc/my.cnf`, `/etc/mysql/my.cnf`, `$MYSQL_HOME/my.cnf`, and
`~/.my.cnf` when present. `--defaults-file` reads only the named file instead.

The parser follows MySQL's option-file format rather than a generic INI
dialect. It understands `[mysqld]`, `[mysqld-8.4]`, and `[server]` groups,
mid-line `#` and `;` comments, quoted values with escapes, interchangeable `-`
and `_`, size suffixes, `!include`, `!includedir`, and `loose-` options.

Other groups are skipped, so the server can share an option file with MySQL.
An unrecognised option inside a server group is a startup error that names the
file and line; `loose-` suppresses that error only for unsupported options.

```ini
[mysqld]
max_connections          = 2000
max_prepared_stmt_count  = 16382
max_allowed_packet       = 64M
default_password_lifetime = 0
password_history          = 0
password_reuse_interval   = 0
password_require_current  = OFF
default_week_format       = 0
max_points_in_geometry   = 65536
local_infile             = OFF
max_load_data_bytes      = 64M
secure_file_priv         = NULL
wait_timeout             = 600
connect_timeout          = 10
net_read_timeout         = 30
net_write_timeout        = 60
innodb_lock_wait_timeout = 50
cte_max_recursion_depth  = 1000
wal_rotate_bytes         = 64M
wal_rotate_entries       = 100000
wal_group_commit_queue_capacity = 1024
loose-skip-name-resolve            # an option fsdb has no knob for
ssl-cert                 = /etc/fsdb/server-cert.pem
ssl-key                  = /etc/fsdb/server-key.pem
ssl-ca                   = /etc/fsdb/client-ca.pem
require-secure-transport = ON
caching-sha2-password-private-key-path = /etc/fsdb/private-key.pem
caching-sha2-password-public-key-path  = /etc/fsdb/public-key.pem
sha256-password-private-key-path       = /etc/fsdb/private-key.pem
sha256-password-public-key-path        = /etc/fsdb/public-key.pem
```

Option-file settings become process-wide defaults at startup. The standard
files are auto-discovered unless `--defaults-file` selects one explicitly.

### Live settings

The following MySQL-shaped option-file settings also accept `SET GLOBAL`:

- connection and protocol limits: `max_allowed_packet`, `max_connections`,
  `max_prepared_stmt_count`, and `local_infile`;
- timeouts: `connect_timeout`, `wait_timeout`, `interactive_timeout`,
  `net_read_timeout`, `net_write_timeout`, and
  `innodb_lock_wait_timeout`;
- execution and account defaults: `cte_max_recursion_depth`,
  `default_password_lifetime`, `password_history`,
  `password_reuse_interval`, `password_require_current`,
  `default_week_format`, and `max_points_in_geometry`.

Other live system variables, including `time_zone`, `max_sp_recursion_depth`,
and `protocol_compression_algorithms`, are set through SQL but are not numeric
option-file knobs.

`max_points_in_geometry` is also eligible for MySQL's statement-scoped
`/*+ SET_VAR(max_points_in_geometry=...) */` optimizer hint. The override is
visible inside the hinted statement and disappears when that statement ends.

`max_load_data_bytes` and the `wal_*` settings are configuration-only fsdb
limits rather than MySQL system variables. See the
[compatibility guide](docs/compatibility.md) for detailed behavior and
deliberate divergences.

## Security and deployment

### Deployment checklist

The defaults favor local development: the listener binds to loopback, the
bootstrap `root` account has an empty password, and data stays in memory. Before
binding to a non-loopback address:

1. create a password-protected administrative account and remove or lock the
   passwordless bootstrap account;
2. configure a server certificate and enable `--require-secure-transport`;
3. use account-level `REQUIRE` rules when clients must present certificates;
4. pass `--data-dir` when committed data must survive process exit, and restrict
   that directory to the server's operating-system account.

### Password authentication

New accounts use MySQL 8.4's `caching_sha2_password` by default. The server
supports its full and cached exchanges: passwords travel inside TLS when the
connection is encrypted, while plaintext TCP clients can request the server's
RSA public key. By default that key is generated for the process.

The `caching_sha2_password_*_key_path` options select a persistent PEM pair;
embedding hosts can call
`Db.withAuthenticationRsaKey Authentication.CachingSha2Password privateKey`
with an already-loaded key. Clients that disable public-key retrieval should
use TLS.

`CREATE USER` and `ALTER USER` also accept explicit
`sha256_password` and `mysql_native_password` credentials for older clients.
Both plugins are retained for compatibility rather than used as the default;
`sha256_password` uses its matching `sha256_password_*_key_path` pair for
plaintext RSA exchange.

The WAL and snapshots detect torn or accidentally corrupt data, not malicious
changes by a local writer. Treat the data directory and every TLS or
authentication private key as trusted server state.

## How it works

One pipeline, all the way down:

```mermaid
flowchart LR
    CLI["mysql CLI"] --> WIRE
    PDO["PDO"] --> WIRE
    CON["MySqlConnector"] --> WIRE

    subgraph fsdb["fsdb"]
        direction TB

        WIRE["Packet / Protocol<br/>MySQL wire protocol"]:::wire
        SESS["Session<br/>transactions · variables"]:::session
        QH["QueryHandler<br/>COM_QUERY / COM_STMT_*"]:::session
        PARSE["Parser · FParsec<br/>SQL text → AST"]:::plan
        EXEC["Executor<br/>logical plan → lazy seq"]:::plan
        STORE["Storage<br/>catalog · indexes · snapshots"]:::data
        WAL["Persistence<br/>binary WAL · snapshot"]:::data

        WIRE --> SESS --> QH --> PARSE --> EXEC
        EXEC <-->|snapshots| STORE
        STORE <-->|commit events| WAL
        EXEC -.->|result rows| WIRE

        AUTH["Auth<br/>mysql.user · privileges"]:::side
        COL["Collation registry<br/>MySQL names · ICU semantics"]:::side
        FN["Function registry<br/>built-in · custom · session"]:::side

        WIRE -.-> AUTH
        QH -.-> AUTH
        PARSE -.-> COL
        EXEC -.-> COL
        STORE -.-> COL
        EXEC -.-> FN
    end

    classDef wire fill:#e8f5e9,stroke:#43a047,color:#1b5e20
    classDef session fill:#e3f2fd,stroke:#1e88e5,color:#0d47a1
    classDef plan fill:#ede7f6,stroke:#8e24aa,color:#4a148c
    classDef data fill:#fff8e1,stroke:#fb8c00,color:#e65100
    classDef side fill:#fce4ec,stroke:#d81b60,color:#880e4f
```

### Parser

An FParsec combinator grammar parses SQL into a discriminated-union AST.
`SELECT`s compile to a logical plan that executes lazily: `LIMIT` stops the
scan once enough rows survive, and `ORDER BY ... LIMIT n` streams a bounded
top-(n+offset) set instead of materializing the full sort.

### Engine

#### Transactions

Databases and tables live in a value-swapped catalog. Transaction visibility
depends on the selected isolation level:

- repeatable read establishes a snapshot on the first database statement;
- read committed refreshes from committed roots before each statement;
- read uncommitted also composes active private deltas into that statement
  view.

Writes remain private until commit at every isolation level. Within each
affected database, commit performs a row-level three-way merge so disjoint
concurrent changes combine. Indexed point and range `UPDATE` or `DELETE`
statements wait for an existing row owner and rebase before applying their
changes; other overlapping write shapes fail with MySQL's retryable 1205
error.

Database roots publish independently for ordinary writes. A transaction that
changes several databases swaps all of its prepared roots behind one short
publication boundary, so schema and diagnostic consumers cannot observe a
partially committed catalog.

The row store uses immutable, copy-on-write pages with stable row identities.
A merge can therefore inspect changed pages and update derived indexes without
copying the entire table.

#### Locks

`FOR UPDATE`, `FOR SHARE`, and `LOCK IN SHARE MODE` read current committed row
versions and retain shared or exclusive row-stripe ownership until the
transaction ends. `OF`, `NOWAIT`, and `SKIP LOCKED` follow MySQL's transaction
behavior. Direct indexed predicates narrow a single-table lock set; joins and
scan-shaped locking reads conservatively lock every targeted physical source.

`LOCK TABLES` provides session-scoped shared or exclusive ownership. It honors
MySQL's alias restrictions, temporary-table exception, atomic replacement
lists, and implicit view and trigger dependencies. Ordinary statements acquire
compatible table ownership only while they execute, so explicit locks also
coordinate with sessions that never issue `LOCK TABLES`.

Named `FLUSH TABLES ... WITH READ LOCK` and `FOR EXPORT` use the same read-lock
lifecycle. The global `FLUSH TABLES WITH READ LOCK` form lets reads and
temporary-table writes continue while permanent writes wait for `UNLOCK
TABLES` or disconnect.

#### Indexes and joins

The engine maintains several immutable index structures, each serving a
different access pattern:

- **Equality and ranges.** Primary, unique, and secondary equality maps use
  collation-folded keys. Direct equalities may bind a complete key or a safe
  left prefix of a composite B-tree in single-table reads, updates, and
  deletes. Literal probes and conservative row-independent numeric expressions
  use the same lookup path. Scalar and composite-row `IN` lists and direct
  comparison ranges and `BETWEEN` also accept those safe numeric expressions
  where the indexed column is numeric. Compatible shapes use maintained
  indexes, and `EXPLAIN` reports the corresponding `const`, `ref`, or `range`
  access.

  Candidate cardinalities are checked before row resolution, so broad probes
  fall back to a row-store scan instead of building an all-row union. Folded
  or PAD SPACE text prefixes also retain the scan path when SQL-equal spellings
  are not one contiguous ordered slice. Compatible scalar lists are normalized
  once per statement, so a scan fallback does not repeat the entire list
  comparison for every row. When equality, membership, spatial, and range
  predicates offer competing physical paths, the smallest observed candidate
  set wins. Fully indexable `OR` branches union and deduplicate those candidates;
  if any branch lacks a safe physical superset, the whole disjunction retains
  the scan path. Compatible `AND` branches can instead intersect candidates.
  Reads use that extra index only when both inputs are broad and the observed
  reduction pays for the merge; mutations and locking reads use the exact
  intersection to avoid touching unrelated rows. `EXPLAIN` reports the same
  choice made by execution.

- **Ordering.** Compatible `ORDER BY` operations stream a left index prefix, or
  a suffix whose earlier keys are fixed by exact equalities. Literal bounds on
  the next key—and safe numeric constant expressions on numeric keys—narrow
  that slice further. `LIMIT` or `OFFSET` can then stop it early. Numeric,
  binary, `utf8mb4_0900_bin`, and
  `utf8mb4_0900_as_cs` prefixes support this path. Case- or accent-folded and
  PAD SPACE text prefixes retain the scan/sort path because equal values can
  occupy separate suffix runs.

- **Grouping.** Compatible `GROUP BY` prefixes use the same ordered stream.
  Covered groups can derive counts and grouping-key `MIN` or `MAX` values from
  adjacent keys without resolving rows.

- **Functional keys.** Composite indexes may contain `LOWER`/`LCASE`,
  `UPPER`/`UCASE`, `TRIM`, `REVERSE`, `CHAR_LENGTH`/`CHARACTER_LENGTH`,
  `LENGTH`/`OCTET_LENGTH`, `BIT_LENGTH`, or numeric/text/binary `ABS` parts.
  Compatible unary parts can be composed, such as `UPPER(TRIM(name))` or
  `BIT_LENGTH(REVERSE(name))`. These keys participate in equality,
  uniqueness, ordering, grouping, mutation, recovery, and correlated probes.
  Numeric-result keys also serve direct and correlated comparison ranges and
  `BETWEEN`. Correlated probes pass through direct derived-table and CTE
  projections. Function aliases share a canonical physical identity, while an
  embedding override of any function in the chain keeps execution on the
  ordinary scan path.

  Text and binary values use MySQL's leading-number conversion and
  diagnostics. Arithmetic and other general expression keys retain their DDL
  and metadata but still scan or sort.

- **Spatial access.** Planar `SPATIAL` and `RTREE` declarations maintain
  immutable minimum-bounding-rectangle entries. They narrow direct
  `MBRINTERSECTS`, `MBRWITHIN`, and `MBRCONTAINS` predicates in single-table
  reads, updates, and deletes. The full predicate still validates every
  candidate.

- **Joins.** Equi-joins choose between one hash build and repeated index
  probes. Physical inner, left, and right joins can probe an index when rows
  already in scope bind its complete key or an ordered composite-key prefix,
  including `USING` and `NATURAL JOIN`. Full-result inner and left joins use
  the observed candidate counts to avoid repeated broad probes; queries that
  may stop at `LIMIT` retain the lazy index path.

  Correlated predicates use the same complete-key and safe left-prefix probes
  through direct tables and pass-through derived tables or CTEs. Literal and
  outer-row equalities may supply different key parts; projection aliases are
  mapped back to their stored columns before the lookup. A fully covered
  `COUNT(*)` reads the index cardinality directly when its comparison semantics
  match the stored key. More complex or coercive shapes retain the materialized
  or row-by-row path.

Equality buckets and ordered entries remain separate derived structures. That
trade spends memory and incremental write work to keep point probes direct and
range seeks bounded.

### Collations & charsets

The registry covers MySQL 8.4's utf8mb4 names and common Unicode, Windows,
DOS, CJK, ISO Latin, KOI8, and Mac character sets. `utf8_*` remains available
as MySQL's deprecated alias.

Each collation carries a locale, fold level, and padding rule. ICU supplies
the comparison and sort keys, so the exact weight bytes can differ from
MySQL's UCA tables. Column declarations and `SET collation_connection` feed
the same coercibility rules used by comparisons, grouping, deduplication,
joins, and unique indexes.

DDL, write coercion, `CONVERT(x USING ...)`, charset introducers, `LOAD DATA`,
and byte-oriented functions share the same codec registry.

### Prepared statements

`COM_STMT_PREPARE` and `COM_STMT_EXECUTE` bind parameter `Value`s into the
parsed AST (`?` → `Placeholder` → `Lit`). Bound values therefore keep their
SQL types for every statement handled by the grammar. The text-probed `SET`
and `SHOW` forms still splice SQL literals.

Forward-only prepared cursors, long parameter data, and zlib or Zstandard
protocol compression use the same typed execution path. The dynamic GLOBAL
`protocol_compression_algorithms` setting limits negotiation for new clients.

## SQL surface

The implemented surface targets statements used by MySQL-backed applications.
This is an orientation map rather than an exhaustive compatibility claim; the
current boundaries live in [GAPS.md](GAPS.md).

- Queries: joins including `NATURAL`/`USING`, derived and lateral tables,
  `GROUP BY`/`HAVING`, window functions, `UNION [ALL]`, expression subqueries,
  ordinary and recursive CTEs, JSON paths, `JSON_TABLE`, planar spatial
  predicates, overlays, independently configurable buffer strategies, and
  EPSG 4326 point distance and line length with axis-order and linear-unit
  handling, plus spherical point and multipoint distance.
- Writes and schema: `INSERT`, `INSERT ... SELECT`, `REPLACE`, multi-table
  `UPDATE`/`DELETE`, generated columns, foreign keys, HASH partition metadata,
  atomic multi-pair and cross-database table renames, foreign-server catalog
  DDL, `EXPLAIN`, and enforced or `NOT ENFORCED` `CHECK` constraints.
- Stored objects: views and `WITH CHECK OPTION`, procedures, functions,
  scheduled events, and `BEFORE`/`AFTER` triggers with compound bodies and
  nested procedure calls.
- Accounts: `CREATE USER`, roles, proxy grants, `GRANT`/`REVOKE`, password,
  resource, and attribute policy, plus database-, table-, and column-level
  privilege enforcement.
- Bulk and batched work: `CLIENT_MULTI_STATEMENTS`/`CLIENT_MULTI_RESULTS`,
  `LOAD DATA [LOCAL] INFILE`, and `SELECT … INTO OUTFILE/DUMPFILE`. Imports
  support target columns, user variables, and ordered `SET` transformations.
  Server-owned paths are disabled or confined by default; input sizes are
  bounded by `max_load_data_bytes`, and exports never overwrite a file.

The introspection surface used by GUI clients exposes compatible schemas and
live data wherever fsdb owns the underlying subsystem. Its
`information_schema` column sets are checked against MySQL 8.4. The `SHOW`
family covers metadata such as `STATUS`, `VARIABLES`, `ENGINES`, `GRANTS`, and
`CREATE TABLE`; `PROCESSLIST` is live, with working `KILL QUERY` and
`KILL CONNECTION` commands.

Every comparison, sort, group, deduplication, join, and unique key uses the
effective collation of its operands. `SET collation_connection` governs
literals, so `SELECT 'åge' = 'age' COLLATE utf8mb4_bin` is 0 while the same
comparison under `utf8mb4_0900_ai_ci` is 1.

Charsets transcode on write. `SHOW CREATE TABLE` reports declared collations
and column comments, while `information_schema.COLUMNS` exposes
`CHARACTER_SET_NAME`, `COLLATION_NAME`, and `COLUMN_COMMENT`.

Fixed-offset and `SYSTEM` session time zones drive current-time and Unix-epoch
functions. A numeric offset appended to a DATETIME or TIMESTAMP input is
converted into the session zone before storage. TIMESTAMP columns then retain
the UTC instant and render in the active session zone; DATETIME columns retain
the converted wall-clock fields.

`ALLOW_INVALID_DATES` preserves bounded invalid day-of-month combinations in
DATE and DATETIME values, defaults, casts, and typed literals. Month and day
fields remain bounded, zero-date modes stay independent, and TIMESTAMP always
requires a valid calendar date. Named zones require MySQL's optional time-zone
tables, which fsdb does not load.

The open compatibility ledger, including complex updatable views and
replication, lives in [GAPS.md](GAPS.md). The
[compatibility guide](docs/compatibility.md) describes the validation method.

## Persistence format

`--data-dir` stores two files, both binary (no JSON). Durable mode uses POSIX
`fsync` through libc on Unix and the managed durable file flush on Windows.

The data directory is a trusted input with the same authority as the server
process. CRC-32 detects torn or accidentally corrupted records; it does not
authenticate them. Anyone who can modify `wal.bin` or `snapshot.fsdb` can
modify the catalog, including the `mysql.user` rows loaded at startup. Keep
the directory writable only by the account running fsdb.

### Write-ahead log

`wal.bin` contains one framed record per committed event:

```
[int32 LE payload length][uint32 LE CRC-32 of payload][payload bytes]
```

The payload is a `CommitEvent` encoded with tagged binary values. Schema
events contain pre-encoded statement trees; row events contain physical
`Value[]` rows. Update and delete events also carry a stable row identity and
the old image used to verify it. Replay therefore writes the exact committed
values: a stored `NOW()` value does not advance to a new instant after restart.

A crash during append can leave a torn final record. Replay stops at a length
overrun or CRC mismatch, truncates the WAL to its last valid boundary, and
continues future appends from there.

Current update and delete events normally resolve their rows directly. If a
transaction rebase has reused a private row identity, replay detects the image
mismatch and resolves that exceptional event from its old image. WAL files
written before row identities were persisted retain their image-based replay
path. Derived indexes are maintained incrementally in every case.

Concurrent commits use a bounded group-commit queue. Commits that arrive
while a flush is in progress share the next append and `fsync`, while each
client is acknowledged only after that batch is durable. Snapshot rotation
passes through the same queue as a checkpoint barrier, so truncation cannot
overtake a published WAL event.

The `wal_group_commit_queue_capacity` option controls producer backpressure.

### Snapshots

`snapshot.fsdb` stores the catalog as a self-delimiting binary tree: databases,
tables, stable row identities, then row values. Prepared XA branches also retain
the logical row and key claims needed to rebuild their locks after restart. The
reader accepts earlier snapshot versions that lack either form of identity.

A checkpoint is written to `snapshot.fsdb.new`, durably flushed, and renamed
into place. On startup, a valid `.new` file wins; a torn one falls back to the
previous snapshot followed by full WAL replay.

On Unix, fsdb calls `fsync` directly. This avoids `FileStream.Flush(true)`,
which issues the substantially stronger `F_FULLFSYNC` on macOS and does not
match MySQL's default macOS flush behavior. Windows uses `Flush(true)` as its
portable durable-flush path.

The server snapshots the catalog and truncates the WAL when either configured
rotation threshold is crossed, and during graceful shutdown. Set those
thresholds with `wal_rotate_bytes` and `wal_rotate_entries`.

## Embedding & extensibility

The [`Fsdb.Db` facade](src/Fsdb/Db.fs) owns an engine instance, its extension
registry, and its transport settings. Register extensions before opening
connections or serving traffic.

Configuration uses pipeline-friendly builders. Most return a new `Db` value;
`withLogger` changes the process-wide sink, while `registerTable` and
`onCommit` attach state to the current store:

| API | Purpose |
|---|---|
| `Db.withDataDir` | Load and attach durable WAL/snapshot storage. |
| `Db.withLogger` | Route process-wide diagnostics to a host callback. |
| `Db.withTlsCertificate` | Supply the listener's server certificate. |
| `Db.withClientCertificateAuthority` | Trust a CA for client certificates. |
| `Db.withAuthenticationRsaKey` | Supply a private key for one plaintext SHA-2 authentication plugin. |
| `Db.requireSecureTransport` | Reject plaintext sessions. |
| `Db.withSecureFileDirectory` | Restrict server-side file statements to one directory. |
| `Db.allowUnrestrictedServerFiles` | Allow server-side file statements to access every host-visible path. |

Runtime and extension APIs are similarly small:

| API | Purpose |
|---|---|
| `Db.registerScalar` | Add a context-free scalar function. |
| `Db.registerAggregate` | Fold one SQL expression over a group. |
| `Db.registerFunction` | Add a context-aware scalar with execution metadata. |
| `Db.registerTable` | Expose host data as a read-only table in the `fsdb` schema. |
| `Db.onCommit` | Subscribe to committed row and schema changes. |
| `Db.connect` | Open a stateful in-process SQL session without a socket. |
| `Db.serve` | Start a background listener and return its bound endpoint. |
| `Db.listen` | Run a listener until its returned `Async` stops. |

### Create an embedded host

Create an F# console project inside this checkout and reference fsdb:

```sh
dotnet new console --language F# --framework net10.0 --output examples/MyHost
dotnet add examples/MyHost/MyHost.fsproj reference src/Fsdb/Fsdb.fsproj
```

This `Program.fs` registers `SLUGIFY`, then carries the configured database
straight into a listener on an available local port:

```fsharp
module MyHost.Program

open System
open System.Net
open System.Text.RegularExpressions
open Fsdb
open Fsdb.Functions
open Fsdb.Value

let slugify =
    function
    | [ VNull ] -> VNull
    | [ VString text ] ->
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-")
        |> fun slug -> slug.Trim '-'
        |> VString
    | _ -> raise (SqlError(1582, "slugify expects one string"))

[<EntryPoint>]
let main _ =
    use server =
        Db.create ()
        |> Db.registerScalar "SLUGIFY" slugify
        |> Db.serve IPAddress.Loopback 0

    printfn "fsdb listening on %O:%d" server.Address server.Port
    Console.ReadLine() |> ignore
    0
```

Function names are case-insensitive. A host registration can override a
built-in, while session-bound functions such as `DATABASE()` and
`CURRENT_USER()` retain precedence. Arguments and results use the
[`Value` discriminated union](src/Fsdb/Sql/Value.fs), so extensions keep SQL
types instead of receiving preformatted strings.

Arity is expressed by pattern matching, not by registration metadata. Handle
`VNull` explicitly: metadata validation may evaluate an expression against a
synthetic all-NULL row, and SQL functions normally propagate NULL.

Raise `SqlError(code, message)` for a deliberate client-visible failure. Any
other exception becomes error 1105, and an extension failure aborts the current
transaction.

The remaining snippets use the same `Fsdb`, `Fsdb.Functions`, and `Fsdb.Value`
opens as the complete host above.

### Register an aggregate

A custom aggregate takes one SQL expression. It receives the non-NULL value
from every row in the group after `DISTINCT`, when present, has been applied.
The engine returns NULL for an empty group.

```fsharp
let median (values: Value list) =
    let sorted =
        values
        |> List.choose (function
            | VInt value -> Some(float value)
            | VDouble value -> Some value
            | _ -> None)
        |> List.sort

    match sorted with
    | [] -> VNull
    | values ->
        let middle = values.Length / 2

        if values.Length % 2 = 0 then
            VDouble((values.[middle - 1] + values.[middle]) / 2.0)
        else
            VDouble values.[middle]

let db =
    Db.create ()
    |> Db.registerAggregate "MEDIAN" median

let connection = Db.connect db
connection.Query "CREATE TABLE scores (score INT)" |> ignore
connection.Query "INSERT INTO scores VALUES (1), (9), (3), (2)" |> ignore

match connection.Query "SELECT MEDIAN(score) AS median FROM scores" with
| Executor.ResultSet([ "median" ], [ [ Some value ] ]) -> printfn "median = %s" value
| Executor.Err(code, message) -> failwithf "query failed (%d): %s" code message
| result -> failwithf "unexpected result: %A" result
```

The wire-level integration test in
[`IntegrationTests.fs`](tests/Fsdb.Tests/IntegrationTests.fs) exercises both
`SLUGIFY` and `MEDIAN` through a real MySqlConnector client.

### Use query context and cancellation

`Db.registerFunction` supplies a `QueryContext` for extensions that depend on
the current session or perform external work:

- `Database` is the current schema and agrees with `DATABASE()`.
- `User` is the authenticated account name without its host qualifier;
  `CURRENT_USER()` returns the selected `name@host` account.
- `Cancellation` is signalled when the client disconnects, cancels, or is
  killed. Pass it into blocking I/O.

`ScalarFunction.create` produces a deterministic function that is allowed in
stored expressions. `ScalarFunction.effectful` marks it non-deterministic and
direct-only. Direct-only functions are rejected where fsdb would invoke them
indirectly later, including generated columns, functional defaults and indexes,
CHECK constraints, and trigger bodies.

`ScalarFunction.withSignature` declares SQL parameter and result types for
prepared-statement metadata. Unsigned integers, JSON, temporal, spatial, and
binary types therefore reach clients without being reported as generic strings:

```fsharp
open System.Net.Http
open Fsdb.Ast

let http = new HttpClient()

let httpGet (context: QueryContext) =
    function
    | [ VNull ] -> VNull
    | [ VString url ] ->
        try
            http.GetStringAsync(url, context.Cancellation)
                .GetAwaiter()
                .GetResult()
            |> VString
        with
        | :? OperationCanceledException -> reraise ()
        | error -> raise (SqlError(1296, sprintf "HTTP request failed: %s" error.Message))
    | _ -> raise (SqlError(1582, "http_get expects one URL"))

let db =
    Db.create ()
    |> Db.registerFunction (
        ScalarFunction.create "HTTP_GET" httpGet
        |> ScalarFunction.withSignature [ TVarchar 2048 ] TJson
        |> ScalarFunction.effectful)
```

Scalar execution is synchronous. A slow HTTP call blocks its connection's
query, so cancellation and host-side timeouts still matter.

### Expose host data as a virtual table

Virtual tables are read-only overlays in the reserved `fsdb` schema. Their row
provider runs once per referencing statement before SQL filtering, so it
should return a bounded snapshot rather than an unbounded stream.

```fsharp
let models =
    [ "embed", "http://localhost:11434/v1", "nomic-embed-text"
      "chat", "http://localhost:11434/v1", "llama3.2" ]

let modelTable =
    VirtualTable.create
        "models"
        [ VirtualTable.text "alias"
          VirtualTable.text "endpoint"
          VirtualTable.text "model" ]
        (fun () ->
            [ for alias, endpoint, model in models ->
                  [| VString alias; VString endpoint; VString model |] ])

let db =
    Db.create ()
    |> Db.registerTable modelTable

let connection = Db.connect db
connection.Query "SELECT alias, model FROM fsdb.models ORDER BY alias" |> ignore
```

The provider must return one `Value` per declared column. `VirtualTable.text`,
`int`, `bigint`, and `double` create nullable columns with server defaults;
other types can use an `Ast.ColumnDef`.

Names are case-insensitive. Re-registering a name replaces its provider, and a
virtual table shadows a physical table with the same name. Writes are rejected.
Call `registerTable` after `withDataDir`, because `withDataDir` replaces the
store.

### Consume committed changes

`Db.onCommit` is a multi-subscriber change feed derived from the ordered
persistence events. Inserts contain stored rows after defaults, coercion, and
auto-increment assignment; updates contain `(before, after)` pairs; deletes
contain removed rows. The durable stream's internal row identities are not
part of this public callback shape. Explicit transactions arrive as one
`TransactionCommitted` event. Failed statements and rollbacks emit nothing.

```fsharp
open System.Collections.Concurrent

let committed = ConcurrentQueue<Storage.CommitEvent>()

let db =
    Db.create ()
    |> Db.withDataDir "./fsdb-data"
    |> Db.onCommit (fun event -> committed.Enqueue event)

let rec insertedDocs =
    function
    | Storage.RowsInserted("fsdb", "docs", rows) -> rows
    | Storage.TransactionCommitted events -> events |> List.collect insertedDocs
    | _ -> []

let drain () =
    let mutable event = Unchecked.defaultof<Storage.CommitEvent>

    while committed.TryDequeue &event do
        for row in insertedDocs event do
            printfn "committed doc row: %A" row
```

Handlers run synchronously under the commit-ordering lock. Keep them fast,
avoid blocking, and never write back to the database from a handler; re-entry
deadlocks. Queue events and process them after the originating statement
returns.

A handler exception can make the statement report an error, but it cannot roll
back data that has already been published. Handlers should therefore capture
their own failures.

### Run SQL in-process or over the wire

`Db.connect` creates a stateful session without a socket. The selected
database, variables, temporary tables, and open transaction persist between
`Query` calls:

```fsharp
let connection = Db.connect db
connection.Query "USE app" |> ignore
connection.Query "SET @request_id = 'abc-123'" |> ignore

match connection.Query "SELECT @request_id" with
| Executor.ResultSet(columns, rows) -> printfn "%A %A" columns rows
| Executor.Affected count -> printfn "%d rows affected" count
| Executor.Err(code, message) -> eprintfn "ERR %d: %s" code message
| Executor.MultipleResults results -> printfn "%d results" results.Length
```

`Db.serve` starts a background server and returns its actual bound address and
port plus a stop function. This is especially useful with port `0`, where the
operating system chooses an available port. `Db.listen` returns a foreground
`Async<unit>` instead:

```fsharp
use server = db |> Db.serve System.Net.IPAddress.Loopback 0
printfn "listening on %O:%d" server.Address server.Port

Db.create ()
|> Db.listen System.Net.IPAddress.Loopback 3307
|> Async.RunSynchronously
```

Durability, logging, and TLS are builder-style options too. Configure the
store before registering virtual tables or commit subscribers:

```fsharp
let db =
    Db.create ()
    |> Db.withDataDir "./fsdb-data"
    |> Db.withLogger (fun message -> printfn "[fsdb] %s" message)
    |> Db.withTlsCertificate certificate
    |> Db.withClientCertificateAuthority clientCa
    |> Db.requireSecureTransport
```

The logger is process-global. The TLS server certificate must be an
`X509Certificate2` that contains its private key. Client certificate
authorities validate accounts marked `REQUIRE X509` and accounts whose policy
names an exact certificate `SUBJECT` or `ISSUER`.

### Included examples

[`examples/LlmSearch`](examples/LlmSearch/Program.fs) registers cancellable
`llm_complete` and `llm_embed` functions for an OpenAI-compatible endpoint,
exposes a `fsdb.models` virtual table, queues inserted documents with
`onCommit`, and runs semantic search with `DISTANCE(..., 'COSINE')`:

```sh
just example -- --dry-run
```

[`examples/ReceiptPipeline`](examples/ReceiptPipeline/Program.fs) registers
`ocr` and `llm_schema`, then uses SQL for batching, extraction, upserts,
deduplication, `JSON_TABLE`, constraints, views, and chained audit triggers:

```sh
just receipts -- --dry-run

RECEIPT_ENDPOINT=https://api.openai.com/v1 \
RECEIPT_MODEL=gpt-5-mini RECEIPT_API_KEY=$OPENAI_API_KEY \
  just receipts -- --dump ~/receipts/*.pdf
```

The receipt schema constrains model-produced dates and numeric ranges before
they reach relational tables. A unique file hash prevents repeated OCR and
model calls for identical bytes, while relational unique keys catch rescans
whose bytes differ but extracted identity is the same.

## Benchmarking

`benchmarks/Fsdb.Benchmarks` runs fsdb head-to-head against native MySQL 8.4
through BenchmarkDotNet. Each pair uses the same schema, seeded data, and SQL.
Choose the smallest recipe that answers the question:

```sh
just bench               # full latency suite
just bench-features      # selected SQL-feature latency subset
just bench-quick         # ShortRun job for fast local iteration
just bench-durable       # fsdb WAL vs MySQL fsync/no-fsync
just bench-scale         # larger seeded data set
just bench-load          # concurrent writer throughput
just bench-load-scale    # throughput across worker counts
just bench-comprehensive # all latency, durability, scale, and load suites
```

Each recipe starts both servers for the run and shuts them down afterward; it
does not use Homebrew services. Result artifacts are written under
[`benchmarks/results`](benchmarks/results), including the quick run.

fsdb optimizes for readable, idiomatic F# over raw speed, so MySQL is expected
to win many workloads. The measurements identify scaling slopes and engine
hotspots rather than serving as a parity target. See the
[benchmark guide](benchmarks/README.md) for isolation and interpretation rules.

## Development

Run the normal local gate before sending a change:

```sh
just check
```

That builds the root solution and runs the full Expecto suite. The `test`
recipe passes additional arguments through to Expecto, so one case can be run
by name:

```sh
just test --filter-test-case <Substring>
```

`just test-report` writes JUnit timings to `test-results/fsdb.xml`, and
`just stress [minutes]` runs Expecto's randomized stress mode with a 6 GiB
memory guard for repeated large-packet and snapshot cases.

Coverage uses the repository-pinned Coverlet tool and fails if total branch
coverage falls below 65%:

```sh
just coverage
```

F# source order is explicit. When adding a `.fs` file, place its
`<Compile Include="..." />` entry in dependency order in the relevant project
file before any file that consumes it.

MySQL 8.4 is the semantic oracle. Compatibility changes should be checked
against MySQL rather than SQLite, then captured in the Expecto suite. The
differential harness under `torture/` is a separate solution and deliberately
is not part of `just check`; see the
[torture harness guide](torture/README.md) before running or changing it.

## Documentation

The maintained guides describe the current implementation. `GAPS.md` contains
the active boundaries; immutable benchmark results and dated torture findings
remain historical evidence and are not rewritten when the implementation
moves on.

| Guide | Use it for |
|---|---|
| [Compatibility](docs/compatibility.md) | Validation method and detailed supported behavior |
| [Open gaps](GAPS.md) | Current, evidence-backed differences from MySQL 8.4 |
| [Comment style](docs/comment-style.md) | The grading every source comment and maintained document survives |
| [Torture harness](torture/README.md) | Differential fuzzing against a MySQL 8.4 oracle |
| [Application smoke tests](smoke/README.md) | Pinned upstream projects exercised over the wire |
| [Benchmarks](benchmarks/README.md) | Workloads, isolation rules, and result interpretation |
