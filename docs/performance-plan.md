# fsdb — phased performance plan (beauty-preserving)

A phased plan for the remaining performance wins, written against the
findings in the low-level database performance research survey. It treats
"beautiful F# is the primary goal" as a first-class constraint: every phase
carries a *beauty verdict* (the same lens `performance-design.md` already
uses), and the ordering front-loads wins where the code gets *nicer*, not
uglier.

The transformative items (codegen, columnar storage, a real cost-based
optimizer) live in a final "strategic ceiling" phase honestly marked
probably-not-worth-it for this project.

---

## Guiding rule

Every phase must pass one of three beauty tests, in order of preference:

1. **Makes the code prettier** (deletes a smell, removes a workaround) — do
   it unconditionally.
2. **Beauty-neutral** (mutation/abstraction stays encapsulated behind an
   existing module API) — do it, keep the seam clean.
3. **Costs beauty** (adds a `Dictionary`, a second algorithm, generated
   code) — only if a measured pathology demands it, and only behind a narrow
   boundary.

This is the same discipline the repo already uses: `Storage`'s private
`Map`-backed index, `updateRows`' `ToBuilder()`, and `upsertRows`'
statement-local mutable `Value[][]` are all "neutral" wins. Keep that
pattern.

---

## Status (2026-08-18)

| Phase | Status |
|---|---|
| 1 — beauty-positive quick wins | **done** (1.1 JSON-path memo, 1.2 AST-bound prepared stmts, 1.3 `conjuncts`) |
| 2 — streaming wire boundary | **done** (batched row writes, inline framing) |
| 3 — join predicate pushdown | **re-scoped** — see the note below |
| 4 — `[<Struct>] Value` | **tried, regressed — dropped** |
| 5 — WAL group commit | **dropped** — obsolete |
| 6 — strategic ceiling | not planned (by design) |

**Phase 5 is obsolete.** The ~5 ms per-commit fsync it was written against was
an artifact: .NET's `FileStream.Flush(true)` issues `F_FULLFSYNC` on macOS.
That is now a plain libc `fsync` (~16 µs), so `--data-dir` writes are already
near MySQL parity on point writes. Row storage now uses immutable fixed-size
pages and stable identities; indexed updates prepare under row stripes and
publish through a short database-slot critical section. Full-scan and
structural writes retain the database gate.

**Phase 3 is re-scoped.** Its original premise — that the join is dominated by
`Array.append` over ~50k matched pairs — is stale: the hash join already yields
lazily and `WHERE`/`LIMIT` streaming short-circuits it, so `LIMIT 50` pulls
only ~80 pairs. Instrumenting `hashPairs` showed the build is a steady
~1.3 ms and the bench's 15 ms mean was first-query JIT (14 ms error bar); the
real remaining cost is the 60k-row scan + `List.indexed` tuple churn + the
collation-aware key comparer. Done instead: lazy probe indexing (the streaming
inner-join path probes `joinRows` directly with `Seq.indexed`, so the 50k
tuples only materialize where an outer join / nested-loop fallback needs them)
and single-column fast paths in `JoinKeyComparer.GetHashCode` / `equiKeyOf`.
The join's remaining gap is broad per-row interpretation overhead, not a
fixable hotspot — Phase 4 (`[<Struct>] Value`) was tried against it and
regressed (see below), so that overhead stands as the documented floor.

**Done since writing:** the WAL and snapshot are binary (no JSON) with CRC
torn-tail detection, and the snapshot streams past the 2 GB `byte[]` ceiling.
The row heap uses immutable pages with stable identities, making point writes
independent of table size while preserving snapshot roots through structural
sharing.

**Next:** nothing scheduled. Phase 4 was the last planned item and it
regressed (see below); the remaining gap is per-row interpretation overhead
with no beauty-preserving lever identified.

---

## Phase 1 — Beauty-positive quick wins *(~2–3 days, near-zero risk)*

Three changes that are *strictly prettier* and each remove a known smell.
Do these first: they are free and they set the tone.

### 1.1 Memoize the JSON path literal (design-doc change G)

- **Where:** `Functions.fs` JSON section (`jsonExtractFn`, `parseJsonPath`,
  ~`Functions.fs:319`).
- **What:** parse the `$.a[2].b` path once per query (on the `Lit`
  argument) instead of per row. `parseJsonPath` is a pure function — cache
  its result keyed by the literal.
- **Measured:** ~2.34 µs/row today; this removes ~60% of the surrounding
  cost → `JsonExtract` from ~3.3× MySQL toward ~1.5×.
- **Beauty verdict:** *Neutral-to-better.* Pure-function memoization on a
  `Lit` is idiomatic; the existing `tryParseJsonValue`/`parseJsonPath` shape
  doesn't change.

### 1.2 Prepared statements: bind `Value`s directly, delete the SQL splice (design-doc change F)

- **Where:** `QueryHandler.valueToSqlLiteral` / `escapeSqlString` /
  `substitutePlaceholders` (`QueryHandler.fs:107-146`), `Server.fs`
  STMT_EXECUTE branch, `Session.PreparedStmt`.
- **What:** keep the parsed AST on `PreparedStmt` and bind parameters as
  `Value`s at execution, instead of rendering them to SQL literals and
  re-parsing. Lets the grammar own placeholders.
- **Measured:** ~0 perf (already proven), but it deletes an injection-shaped
  string splice.
- **Beauty verdict:** *Better.* Removes the only hand-rolled SQL-escaping in
  the repo; the prepared path stops round-tripping through text.

### 1.3 `conjuncts` prepend+reverse (`Executor.fs:492`)

- **What:** `conjuncts l @ conjuncts r` → accumulate with `::` and reverse,
  or fold.
- **Measured:** negligible (AND chains are short) — this is pure hygiene.
- **Beauty verdict:** *Better.* `@` in a recursive fold is the canonical F#
  smell; this is a 2-line diff.

**Phase 1 gate:** Expecto + one gauntlet suite green; `JsonExtract`
re-measured `< ~150 µs` at 10k rows; no benchmark row regresses.

---

## Phase 2 — Streaming wire boundary *(~3–5 days, beauty-neutral)*

The `Executor` already produces lazy `seq`s for LIMIT, but
`Server.resultPayloads` throws that away: it materializes every row into a
`byte[]` list, then `sendPayloads` issues one `WriteAsync` per row packet.

### 2.1 Stream row encoding + writes

- **Where:** `Server.resultPayloads` / `sendQueryResult` /
  `sendBinaryQueryResult` (`Server.fs:91-170`), `Packet.Writer`.
- **What:** encode and write rows incrementally through a `BufferedStream`
  (or pooled `ArrayBufferWriter<byte>`), one buffer flush per several rows
  instead of one `WriteAsync` per row. Column-count/defs/EOF stay as-is;
  only the row stream changes.
- **Measured:** the design doc already names "one `WriteAsync` per row" as a
  broad tax; this removes the syscall floor and the full-result-set byte[]
  spike.
- **Beauty verdict:** *Neutral.* The change lives entirely inside
  `Server`/`Packet`; the module API (`sendQueryResult`) doesn't change.

### 2.2 `Span<T>`/`ArrayPool` framing in `Packet`

- **Where:** `Packet.Writer` (`ResizeArray<byte>` + `ToArray()`),
  `Packet.Reader`, `readExactAsync` (`Packet.fs:151` allocates per packet).
- **What:** pooled buffers for the hot read/write path; `ReadOnlySpan<byte>`
  slicing for the decode paths.
- **Beauty verdict:** *Neutral.* The `Writer`/`Reader` types are already
  IO-free abstractions; swapping their internals is invisible to callers.

**Phase 2 gate:** `FilterScanOrderLimit` and any large-result query show
reduced `Allocated` + Gen0 in BenchmarkDotNet; memory spike on a big
`SELECT` bounded; Expecto + differential wire tests green.

---

## Phase 3 — Join predicate pushdown *(re-scoped — see Status)*

*(This section's original analysis is stale: the hash join already yields
lazily and `LIMIT` streaming bounds the appended pairs, so 3.1/3.2 below
target an append volume that no longer exists. What shipped instead — lazy
probe indexing plus single-column key fast paths — is summarized in the
Status section above.)*

The hash join is correct and streams, but the 35× gap comes from three
things, the cheapest of which is structural: `WHERE u.age > 30` is applied
*after* `Array.append` combines every matched pair, and the build side is
chosen by a fixed "build on smaller" heuristic.

### 3.1 Push single-side WHERE conjuncts into the join sides

- **Where:** `Executor.applyJoin` / `runSelectStmt` (`Executor.fs:1293`,
  `~1792`).
- **What:** partition the `WHERE` (like `extractEquiKeys` already partitions
  `ON`) into *left-only*, *right-only*, and *mixed* conjuncts. Left-only /
  right-only predicates filter each side *before* hashing/probing. This is
  the exact same "pure narrowing, never a correctness risk" shape
  `tryPointLookup` already documents.
- **Measured:** the dominant remaining cost in `JoinUsersOrders` is
  building/combining the probe side; filtering `users` down before hashing
  cuts the hash/probe/`Array.append` volume by the selectivity of
  `age > 30`.
- **Beauty verdict:** *Neutral.* It's a second instance of the
  `extractEquiKeys` pattern — a pure function partitioning conjuncts by which
  side they reference. The DU/`seq` pipeline is untouched.

### 3.2 Defer `Array.append` until a row survives the filter

- **Where:** the `hashPairs` → `Seq.map (fun (_, l, _, r) -> Array.append l r)`
  step (`Executor.fs:1433-1441`).
- **What:** thread `(l, r)` and only combine after `WHERE`/projection accept
  the row.
- **Measured:** removes one `Value[]` allocation per matched pair — the
  single biggest allocation source in the join at 50k matches.
- **Beauty verdict:** *Neutral.* The lazy `seq` already yields lazily; this
  just moves the combine one stage later.

**Phase 3 gate:** `JoinUsersOrders` re-measured; target `< ~2 ms` (from
9.16 ms; MySQL 259 µs). Correctness: differential property test — hash-join
output equals nested-loop output on randomized ON/WHERE shapes, including
fallbacks.

*(Deliberately out of scope for Phase 3: a full cost-based optimizer /
cardinality statistics. That's the survey's #1 move, but it's a research
project, not a patch — see Phase 6.)*

---

## Phase 4 — `[<Struct>] Value` *(tried 2026-08-18, regressed — dropped)*

*(This section's premise is disproven. The one-line annotation shipped on a
branch and all 966 Expecto tests passed, but the clean full bench regressed
the scan/aggregate paths hard: `FilterScanOrderLimit` 6.65 → 16.7 ms,
`GroupByAggregate` 25.4 → 104 ms, `JoinUsersOrders` 9.16 → 12.9 ms. The
mistake in the analysis below: it treats a row as "an array of pointers to
heap objects," but the hot path isn't reading rows — it's the
expression-evaluation pipeline, where `Value` flows through `Result`, `Option`,
`list`, and tuples. A struct DU makes every one of those positions *box* and
copies 24 bytes by value at every pass, which swamps the inline-`Value[]`
win. The only gain was prepared point-select (~100 → ~71 µs), not worth the
2.5–4× regressions. A future attempt would need to first eliminate the
generic positions `Value` flows through — a much bigger change than the
one-line annotation.)*

The design doc deferred this "until the index work is done, when scan count
stops being the top term." That condition is now met — point ops are
O(1)/O(log n), and the remaining 2–3× is the boxing tax: `Value` is a 9-case
reference DU, so a row is `Value[]`, an array of *pointers to heap objects* —
the exact layout the research's Valhalla/`UnsafeRow` sections identify as the
root cost.

**What:** `[<Struct>]` on `Value`. The DU stays *identical* in shape — the
attribute is one line — but `Value[]` becomes a flat, contiguous array of
inline structs (header-free, no per-cell indirection). `VInt`/`VDouble`/
`VDecimal`/`VDate`/`VDateTime` inline their payloads; `VString`/`VBytes`/
`VJson` still point at heap payloads, so the win is largest on numeric
columns and neutral on text-heavy ones.

**Why it's beauty-preserving:** the type and all its `match` sites read the
same. The annotation *is* the idiom (F# 4.1+ `[<Struct>]` DUs are standard
for exactly this).

**What it costs / risks (must be audited):**

- Default `Value` becomes `VNull` (the first case) — which happens to be
  *semantically perfect* for SQL, but any `null`-checks or `box`/`unbox`/
  interface dispatch on `Value` need review.
- Struct size ~24 bytes (16-byte `decimal` payload + tag). Fine, but confirm
  it doesn't bloat the `ResizeArray`/builder hot paths.
- `Value` used as a generic argument where reference semantics were assumed
  (e.g. `Dictionary` keys — the join comparer is on `Value[]`, so it's fine,
  but grep for `Value` in generic positions).

**Beauty verdict:** *Neutral-to-better.* The type is unchanged; the *code*
may actually get cleaner where "default is NULL" can replace explicit
`VNull` fallbacks.

**Gate:** `PointSelectByPk`, `FilterScanOrderLimit`, `JoinUsersOrders` all
measurably improved (target ≥1.5× each); full Expecto + gauntlet parity; no
behavioral change in the differential tests.

---

## Phase 5 — WAL group commit *(dropped — obsolete, see Status)*

**What:** batch + single `fsync` per group instead of "one open + `fsync` per
commit" (`Persistence.fs:784-791`, already self-documented as a ponytail).

**Beauty verdict:** *Neutral.* Entirely encapsulated in `Persistence.attach`;
the `OnCommit` event stream already flows through one `Lock`, so batching is
a natural fit.

**Why it's last/optional:** the design doc proved the in-memory write path
is allocation/scan-bound, not fsync-bound. Only pursue if a real deployment
runs fsdb `--data-dir` under write load.

**Gate:** under `--data-dir`, insert throughput within ~2× of the in-memory
number; `kill -9` durability test still passes.

---

## Phase 6 — The strategic ceiling *(explicitly NOT planned, documented for honesty)*

Three items from the research that would close most of the *remaining* gap,
but which are argued **against** for this project:

| Item | What it is | Why we skip it |
|---|---|---|
| **Whole-stage codegen** | Fuse WHERE/project/aggregate into generated code (Spark Tungsten's move) | Generates ugly imperative bytecode — the antithesis of "beautiful F#." Only pays off at fsdb's scale in the join/aggregate, which Phase 3 already attacks structurally. |
| **Columnar/SoA storage** | `Value[][]` → per-column arrays (kdb+/Arrow/XTDB) | A wholesale change to what a `Table` *is*, and the project's identity is the DU pipeline. A large beauty cost for a multiplier Phase 4 mostly recovers. |
| **Cost-based optimizer** | Cardinality stats + join-order search (Datalevin's move) | A research project. The two existing heuristics (point-lookup narrowing, build-on-smaller hash join) are the idiomatic 80%; the rest is "parity," not "beautiful." |

**Revisit trigger:** only if a real Laravel workload ships a query that
Phase 3/4 can't get within ~2× of MySQL, and it's genuinely a production
blocker — not before.

---

## Summary table

| Phase | Win | Beauty verdict | Effort | Risk | Moves fsdb closer to |
|---|---|---|---|---|---|
| 1 | JSON path memo + AST-bound prepared stmts + `conjuncts` | **Better** | 2–3 d | Low | `JsonExtract` ~1.5×; correctness |
| 2 | Streamed wire + `Span`/`ArrayPool` framing | Neutral | 3–5 d | Low | All result sets; memory + syscalls |
| 3 | Join predicate pushdown + late combine | Neutral | ~1 wk | Med | `JoinUsersOrders` 35× → ~5× |
| 4 | `[<Struct>] Value` | Neutral-to-better | 1–2 wk | Med-High | The 2–3× broad tax everywhere |
| 5 | WAL group commit | Neutral | ~1 wk | Low | `--data-dir` write throughput |
| 6 | Codegen / columnar / CBO | Costs beauty | — | High | Parity with MySQL (not pursued) |

**Sequencing rationale:** Phase 1 is unconditional (beauty-positive).
Phase 2 is neutral and unblocks the memory profile of every later change.
Phase 3 builds on the lazy `seq` that already streams, so it slots cleanly.
Phase 4 is the highest-impact but touches the central type, so it goes after
the low-risk wins have landed and been benchmarked — and it's the only phase
that genuinely changes a foundational type, so it deserves its own milestone
with a full differential-safety gate. Phases 5–6 are explicitly
deprioritized and gated on real need.
