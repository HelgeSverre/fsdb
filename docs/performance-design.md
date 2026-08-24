# Making fsdb fast without making it ugly — the M9 performance design

> **Status (2026-08-18):** the slice in section 3 has shipped — Phases 1–3 of
> `performance-plan.md`, the living status doc, cover it. The struct-`Value`
> idea floated in section 2 was tried and regressed; see the Phase 4 note
> there. This document remains the forensic analysis that motivated the plan.

Three research lanes (hotspot forensics, F# idiom microbenchmarks, candidate
designs) fed this document. Where the lanes disagreed on a number or a
mechanism, the decisive measurement was re-run against the **real server**
rather than an fsi model of it, and the in-server number wins.

Measurement environment for every "measured (server)" number below: fsdb
built `Release` (net10.0), started **in-memory** (`--port 3399`, no
`--data-dir`, so no WAL and no fsync anywhere in the numbers), Apple M2 Max,
.NET 10.0.107, client = `dotnet fsi` + MySqlConnector 2.3.7 over loopback.
Client-side overhead is ~0.2 ms/op (`SELECT 1` round trip: 0.258 ms), so
sub-millisecond targets are floored by the harness, not by the engine.

Calibration against the recorded suite: `PointSelectByPk` measures 1.315 ms
here against a freshly seeded 10k-row `users`, versus 1,322.79 µs in
`benchmarks/results/f1b15ab.md`. The rig reproduces the recorded suite to
within noise — which is what makes the discrepancies in section 1.6 evidence
rather than error.

---

## 1. Where the time actually goes

Ranked by cost at the benchmark's own scale (10k users / 50k orders).

### 1.1 JOIN: a materialized cross product, ~0.85 µs per pair, unbounded memory

`Executor.applyJoin` (Executor.fs:751, mirrored in `applyMutationJoin` at
Executor.fs:823) builds the entire left × right product as a strict list
*before* the ON clause runs, and long before WHERE and LIMIT
(`applyLimitOffset`, Executor.fs:927/1812, truncates an
already-materialized list).

Measured (server), `SELECT ... FROM users u JOIN orders o ON o.user_id = u.id
WHERE u.age > 30 LIMIT 50`:

| left × right | pairs | time | per pair |
|---|---|---|---|
| 250 × 1,000 | 250,000 | 210 ms | 0.839 µs |
| 500 × 2,000 | 1,000,000 | 837 ms | 0.837 µs |
| 1,000 × 4,000 | 4,000,000 | 3,420 ms | 0.855 µs |

Dead linear in pair count. The benchmark's 10,000 × 50,000 = 500M pairs
therefore costs **~425 s (~7 minutes)** — matching the recorded `NA` and the
"did not return within 2 minutes" note, and settling the lanes' competing
extrapolations (~13.7 s from a bare-equality fsi model, ~100 s from a
list-append fsi model; both under-count because neither includes the real
per-pair `evalExpr`).

The second half of this pathology is memory and lifetime. A 2,000 × 20,000
join was started and its client killed after 3 s, BenchmarkDotNet-style:

```
baseline point select 1.696 ms/op   fsdb rss = 88 MB
t+ 3s  point select   7.800 ms/op   rss = 1,860 MB
t+15s  point select   7.320 ms/op   rss = 2,240 MB
t+30s  point select  13.057 ms/op   rss = 2,652 MB   (still climbing)
```

At the time of this measurement, a disconnected client did not cancel the
query. The server kept building the product, RSS grew to gigabytes, and
**unrelated queries on a different database slowed down 4-8x and kept
degrading**. The current disconnect watcher cancels row evaluation.

### 1.2 UPDATE: O(n²) row rebuild, measured in the server

`Storage.updateRows` folds over every row and accumulates with
`doneRows @ [ row ]` (Storage.fs:1274) / `doneRows @ [ newRow ]`
(Storage.fs:1294). `@` is O(length of the left operand), so the fold is
O(n²) regardless of how many rows the WHERE clause matches.

Measured (server), `UPDATE users SET age = age + 1 WHERE id = <pk>`:

| rows | ms/op | ratio per doubling |
|---|---|---|
| 10,000 | 426.3 | — |
| 20,000 | 2,017.7 | 4.7x |
| 40,000 | 9,625.7 | 4.8x |

Textbook quadratic. The recorded 2.41 s corresponds to a `users` table that
the insert benchmarks have grown to ~20k rows. Secondary O(n) costs in the
same fold (`notYetProcessed` re-filter at Storage.fs:1279-1282, unique-group
collision scan at 1284, FK scans at 1288-1289) are real but invisible behind
the quadratic term.

### 1.3 Every point operation is linear in table size — no index anywhere

`Table`'s own doc comment says it (Storage.fs:65-66): "every scan is a full
table scan". Measured (server), same table, three sizes:

| rows | point SELECT by PK | INSERT single row |
|---|---|---|
| 10,000 | 1.315 ms | 1.067 ms |
| 20,000 | 3.078 ms | 2.112 ms |
| 40,000 | 7.287 ms | 4.521 ms |

Two consequences worth naming separately:

- The INSERT numbers come from a server with **no WAL at all**. The
  per-insert cost is the O(n) unique-key scan plus the `table.Rows @ accepted`
  copy (Storage.fs:828/845/852), not fsync. Seeding in 500-row batches makes
  the quadratic obvious: 3.6 s for rows 0-10k, 10.5 s for 10-20k, 47.8 s for
  20-40k.
- Baseline per-row scan cost is **0.14 µs/row** (10k-row scan, no match,
  1.44 ms). That is the number every "broad tax" item below is measured
  against.

### 1.4 The row pipeline costs ~17x more per *matched* row than per scanned row

Measured (server), 10k-row `users`:

| query | ms |
|---|---|
| `WHERE age = 999 LIMIT 20` (0 matches) | 1.44 |
| `WHERE age > 40 LIMIT 20` (~3,300 matches) | 9.34 |
| `WHERE age > 40 ORDER BY id DESC LIMIT 20` | 15.77 |
| `WHERE age > 40 ORDER BY created_at DESC LIMIT 20` | 13.10 |

~2.4 µs per matched row is spent materializing, order-keying and projecting
rows that `LIMIT 20` then discards; sorting adds ~5 ms on top. This is the
headroom a LIMIT short-circuit would recover — see section 4 for why the
obvious lazy-`seq` version is rejected anyway.

### 1.5 JSON path extraction costs 16x a plain row scan

Measured (server), 10k-row `users`, `meta` = `{"plan":"free"}`:

| query | ms | µs/row |
|---|---|---|
| `WHERE name = 'zzz'` (plain scan) | 1.44 | 0.14 |
| `WHERE meta->>'$.plan' = 'pro'` | 23.43 | 2.34 |

`Functions.fs:139-258` parses the document per row with
`JsonNode.Parse`, parses the *path literal* per row, and round-trips through
`formatJsonNode`. `JsonNode.Parse` + key lookup on this document measures
0.873 µs standalone, so ~40% of the per-row cost is inherent to
parse-per-row and ~60% is fsdb's surrounding work.

### 1.6 Prepared statements are NOT a pathology — the recorded 23 ms is a harness artifact

Both hotspot lanes proposed mechanisms for `PreparedPointSelect` being ~17x
slower than the unprepared point select (lane 1: EXECUTE re-parses; lane 3:
parse is only 0.24-0.30 ms, so ~1% of the gap). Direct measurement rejects
the premise. Prepared vs text point select on the *same* table at the *same*
moment:

| rows | text SELECT | prepared (prepare once, execute many) | prepared (Prepare() every iteration) |
|---|---|---|---|
| 10,000 | 1.315 / 2.082 ms | 1.839 / 1.473 ms | 1.385 ms |
| 14,000 | 2.115 ms | 1.936 ms | — |
| 18,500 | 2.851 ms | 2.788 ms | — |
| 20,000 | 3.078 ms | 3.583 ms | 3.159 ms |
| 40,000 | 7.287 ms | 7.138 ms | 6.799 ms |

The prepared path is within noise of the text path everywhere. Two further
facts kill the "prepare re-parses per iteration" theory outright:
MySqlConnector caches prepared statements per session, so the benchmark's
in-loop `cmd.Prepare()` costs 0.004 ms/op after the first call; and growing
`users` by everything the insert benchmarks plausibly add (simulated: 4,000
single inserts + 45 batches of 100 → 18,500 rows) only reaches 2.8 ms, not
23 ms.

The actual cause is section 1.1: `JoinUsersOrders` runs immediately before
`GroupByAggregate`, `JsonExtract` and `PreparedPointSelect` in declaration
order, times out, and leaves the server building a 500M-pair product with
multi-gigabyte RSS while those three are measured. Every recorded row
*before* the join reproduces here within ~20% (PointSelectByPk 1.32 vs
1.315 ms; InsertSingle 1.25 vs 1.07 ms; InsertBatch100 141.8 vs ~111 ms;
UpdateSingleRow 2.41 s vs 2.02 s at 20k). Every recorded row *after* it is
5-8x higher than a direct measurement of the same query (JsonExtract 130.7
vs 23.4 ms; PreparedPointSelect 22.96 vs 2.8 ms).

The suite's own commentary in `benchmarks/results/f1b15ab.md` ("the
prepare/bind path carries its own separate overhead") is wrong and should be
corrected when the numbers are regenerated.

### 1.7 Byproduct at measurement time: connection-pool reset was unimplemented

MySqlConnector's pooled `Open()` sends `COM_RESET_CONNECTION`; the measured
build answered "Unknown command" and the client threw, so every measurement
above used `Pooling=false`. Current fsdb replies OK and clears session state.

### Ranked summary

| # | Cost | Scale of the problem | Category |
|---|---|---|---|
| 1 | JOIN cross product | 0.85 µs × left × right → ~425 s at bench scale; +2.6 GB RSS; survives client disconnect | pathology |
| 2 | UPDATE quadratic rebuild | O(n²): 0.43 s @10k → 9.6 s @40k | pathology |
| 3 | No index: every point op is O(table) | 0.13 µs/row × n on SELECT/UPDATE/DELETE/INSERT-unique-check; INSERT batches quadratic | pathology (broad tax at small n) |
| 4 | Per-matched-row pipeline overhead | 2.4 µs/matched row before LIMIT | broad tax |
| 5 | JSON path per row | 2.34 µs/row = 16x plain scan | broad tax |
| 6 | Protocol: one `WriteAsync` per row, Nagle left on | ~0.26 ms round-trip floor | broad tax |
| 7 | `Value` DU heap-boxes every scalar | baseline per cell | accepted |
| — | Prepared-statement path | no measurable overhead | **not a defect** |

---

## 2. The thesis tested: is the fast version also the more idiomatic version?

| Change | Speedup | Beauty verdict | Blast radius |
|---|---|---|---|
| **A. `x :: acc` + `List.rev` instead of `acc @ [x]`** in `Storage.updateRows` (1274, 1294) and `insertCore` (845) | **~500x** at bench scale (2.41 s → ~1 ms class); fsi models of the isolated shape: 8,977 ms → 1 ms at n=50k | **Better.** `acc @ [x]` inside a fold is the canonical F# beginner smell; prepend-and-reverse is what the style guides and FSharp.Core's own `List` implementations do. Shorter, too. | One function each, ~5 lines. No signature changes. |
| **B. Hash join for equi-`ON`, nested-loop fallback otherwise** (`applyJoin`, `applyMutationJoin`) | **∞ → milliseconds.** Today: does not finish (~425 s). Prototype build+probe at the full 10k × 50k: 2 ms | **Worse, and worth it.** Adds a `Dictionary`/`ResizeArray`, a static ON-clause analysis (`extractEquiKeys`), and a second algorithm behind a fallback branch — genuinely less elegant than today's one-line cross product. But the elegant version *does not terminate*, so this is not "pretty vs fast", it is "pretty vs correct". | ~60-80 lines across two near-duplicate functions; outer-join padding re-derived from matched index sets; needs a `Value`-aware `IEqualityComparer` (see below). No AST or protocol change. |
| **C. PK/unique hash index + index-addressable row storage** | Point SELECT 1.32 ms → round-trip floor (~20-30x at 10k, ~70x at 40k); INSERT single 1.07 → ~0.1 ms; batch-100 into 10k rows 111 → ~5 ms; UPDATE (after A) → O(1) locate + O(log n)/O(1) rewrite | **Neutral.** A private `Dictionary` behind `Storage`'s module API is established F# — the discipline is encapsulation, not "no mutation". Measured backing: `Dictionary` 57-61 ms vs `Map` 404-457 ms per 2M lookups; build 0.1 ms vs 1.0 ms per 2k inserts. Swapping `Value[] list` for an array/`ResizeArray` also *reads* the same (`Array.*` pipelines) and builds 7x cheaper (0.6 ms vs 4.7 ms per 500k). | **Large.** `Storage.Table`, every `Rows` consumer in `Executor`, `Persistence` snapshot/replay, plus a planner step that recognizes `col = literal`. |
| **D. Cancel server-side work when the client disconnects** | Removes the 2.6 GB / cross-query poisoning; no direct throughput win | Worse (a `CancellationToken` threaded through the executor's fold). | Wide and invasive for the payoff — **B removes the only query that triggers it**. Deferred; see section 4. |
| **E. Buffered protocol writes + `client.NoDelay <- true`** | Unmeasured; bounded by the 0.26 ms round trip and ≤50-row result sets in this suite | **Neutral.** `new BufferedStream(client.GetStream())` plus an explicit flush before awaiting the next command is one line and one line. | `Server.fs` connection setup only. |
| **F. Cache the parsed AST on `PreparedStmt`, bind `Value`s directly** | **~0** (section 1.6: prepared ≈ text; MySqlConnector prepares once per session anyway) | **Better** — deletes `valueToSqlLiteral`/`escapeSqlString`, an injection-shaped string splice, and lets the grammar own placeholders. | `Session.PreparedStmt`, `QueryHandler.prepareStatement`, `Server`'s STMT_EXECUTE branch. Real work for a *correctness* win. |
| **G. Parse the JSON path literal once per query** | ≤2x on `JsonExtract` (0.87 µs of the 2.34 µs/row is `JsonNode.Parse` itself and stays) | Neutral (memoize on the `Lit` argument). | `Functions.fs` JSON section. |

The honest summary of the thesis: **one of the three pathology fixes makes
the code prettier (A), one makes it uglier and is still mandatory (B), and
one is neutral if the mutation stays encapsulated (C).** The changes that
would most clearly *cost* beauty — a lazy `seq` pipeline, a struct `Value`,
a custom open-addressing dictionary — are also the ones with the weakest
measured payoff at this engine's current scale, which is a convenient
truth and is why section 4 exists.

### The non-obvious correctness trap in B

`Value.compareStrings` (Value.fs:174) is case-insensitive and
PAD-SPACE-insensitive, so `'Alice' = 'alice'` and `'a' = 'a '` join today.
.NET's default structural hashing is ordinal and case-sensitive. A naive
`Dictionary<Value list, _>` therefore **silently drops matching rows** on
string join keys. The hash join needs a custom `IEqualityComparer<Value list>`
(or a normalizing key transform mirroring `Storage.rowsCollideOn`) before it
is safe for anything but integer keys.

---

## 3. The recommended M9 slice

Ordered. Each step is independently shippable and independently gated.

### M9-1 — Kill the quadratic list appends (hours)

`Storage.updateRows`: accumulate with `::`, `List.rev` once at the end;
collapse the per-match `notYetProcessed` re-filter into the same single pass.
`Storage.insertCore`: same treatment for `accepted @ [ candidate ]` and the
repeated `table.Rows @ accepted`.

**Gate** (rerun `just bench`, fsdb rows):

- `UpdateSingleRow` < **10 ms** (from 2,411,465 µs).
- `InsertBatch100` < **100 ms** (from 141.8 ms — the O(batch × n) unique scan
  survives until M9-3).
- Expecto suite and `torture/` green, unchanged.

M9-3 supersedes the data structure this step touches. Ship it anyway: it is
a five-line diff worth ~500x today, and it is the only change here that makes
the code strictly nicer.

### M9-2 — Hash join for equi-`ON`, nested-loop fallback (1 day)

Decompose the ON clause into AND-conjuncts; if *every* conjunct is a
`col = col` equality that splits cleanly across the two sides, build a
bucket index on the smaller side and probe from the other. Anything else —
`OR`, a range, a mixed-side expression — falls back to today's loop,
unchanged. Key equality goes through a `Value`-aware comparer, not default
structural hashing.

**Gate:**

- `JoinUsersOrders` returns a result (no `NA`) in < **25 ms** (MySQL: 287 µs).
- A property test comparing hash-join output against nested-loop output over
  randomized ON clauses, including `ON a.x = b.y AND a.z > 1` (fallback
  fires) and `ON a.x + 1 = b.y` (fallback fires).
- A string-keyed join test with mixed case and trailing spaces, asserting the
  same rows as the nested loop.
- Existing JOIN/LEFT JOIN/`UPDATE ... JOIN` tests green, unchanged.

**Gate reconciliation, `< 25 ms` — not met, real gap, not a documentation
error.** `just bench` at 5037a48: `JoinUsersOrders` 202,198 µs, ~8x over
gate (MySQL: 239 µs on the same box). Everything else in this gate holds
(no `NA`, all three correctness properties, existing JOIN tests green) —
the hash join itself works and is fast: the `ON o.user_id = u.id` equi
join over 10k × 50k rows is no longer the 425 s cross-product pathology
section 1.1 measured. What's left is `WHERE u.age > 30 LIMIT 50`
materializing, filtering, and fully projecting every matched row before
`LIMIT` slices it down to 50 — section 1.4's ~2.4 µs/matched-row pipeline
tax, at whatever row count the equi join actually produces before the
`WHERE`/`LIMIT` narrow it. Section 4 ("Explicitly rejected") rejects the
lazy-`seq` fix for exactly this *on purpose*, for reasons that still hold
(no `ORDER BY` short-circuit, `Seq` measured 10x slower than `Array` on
full scans, a wholesale pipeline type change). Net: this sub-gate's number
was set before that trade-off was made explicit, and nothing in M9-1
through M9-4 as scoped closes it. Renegotiated here rather than chased:
the M9 milestone stayed unticked until either the lazy-pipeline work was
scheduled as its own milestone, or this number is formally revised.

### M9-3 — Index-addressable rows + PK/unique hash index (~1 week)

One structure, four payoffs: point lookup, unique enforcement on INSERT,
UPDATE/DELETE locate, and FK parent/child checks. Scope it deliberately:
**hash equality only** (`col = literal` against a PK or UNIQUE column), no
range/B-tree, no multi-column planner. `Table.Rows` becomes index-addressable
(array/`ResizeArray` behind the existing module API); the index lives private
to `Storage` and is rebuilt on snapshot replay.

**Gate:**

- `PointSelectByPk` < **250 µs** (from 1,322 µs; MySQL 47 µs).
- `PreparedPointSelect` < **250 µs** — and, independently, within 30% of
  `PointSelectByPk` in the same run.
- `InsertSingle` < **300 µs** (from 1,252 µs; MySQL 126 µs).
- `InsertBatch100` < **20 ms** (from 141.8 ms; MySQL 1.5 ms).
- `UpdateSingleRow` < **500 µs** (MySQL 136 µs).
- Point-lookup latency flat from 10k to 40k rows (the linearity in section 1.3
  is the thing being deleted).

**Measured against this gate** (`just bench` at 5037a48, in-memory, same
rig as section 1's numbers; `tests/Fsdb.Tests/ExecutorTests.fs`'s "point
SELECT by PRIMARY KEY latency is flat from 10k to 40k rows" for the last
line — the earlier draft of this gate shipped with no scaling measurement
at all, so the flatness claim rested on a single fixed-size number, which
proves "faster" but not "O(1)"):

| Sub-gate | Target | Measured | Holds? |
|---|---|---|---|
| `PointSelectByPk` | < 250 µs | 102 µs | pass |
| `PreparedPointSelect` | < 250 µs, within 30% of `PointSelectByPk` | 89 µs, -13% (faster, not slower) | pass |
| `InsertSingle` | < 300 µs | 496 µs | fail — 1.65x over |
| `InsertBatch100` | < 20 ms | 8.6 ms | pass |
| `UpdateSingleRow` | < 500 µs | 2,356 µs | fail — 4.7x over (still clears the coarser top-level milestone gate, < 10 ms) |
| Point-lookup flat 10k→40k | ratio ≈ 1 | ratio 0.76 (10k: 0.028 ms median of 21, 40k: 0.021 ms) | pass — genuinely O(1)/O(log n), not just a faster scan |

`InsertSingle`/`UpdateSingleRow` missing their sub-gates is real, not a
harness artifact (`InsertBatch100` clearing its own, looser, gate on the
same run rules out a poisoned-benchmark explanation like section 1.6's).
Both still touch a live TCP round trip plus fsync-free in-memory commit
bookkeeping per statement — the O(1) index lookup this milestone added
is real (see the flat-ratio row above), but per-statement overhead
elsewhere in the write path (row coercion, unique-key re-encoding,
`OnCommit` dispatch) is now the larger term at this table size. Left as
open follow-up rather than re-chased here: chasing sub-millisecond
per-statement overhead is a different, narrower problem than the O(n)
scan pathology this milestone actually targeted, and every *pathology*
this milestone named (full-scan SELECT/UPDATE/INSERT-unique-check) is
fixed.

### M9-4 — Make the benchmark suite trustworthy (hours, land with M9-1)

Non-negotiable, because sections 1.1 and 1.6 show the current suite reports
fiction for every case after a timed-out one:

- Restart (or reseed) the fsdb server between benchmark methods, so one case
  cannot poison the next.
- Reply to `COM_RESET_CONNECTION` (section 1.7) so pooled clients work.
- Regenerate `benchmarks/results/*.md` and delete the "the prepare/bind path
  carries its own separate overhead" claim.

**Gate:** two consecutive full runs agree on `PointSelectByPk` within 20%.

### Expected end state

| Row | today | after M9 | MySQL |
|---|---|---|---|
| PointSelectByPk | 1,323 µs | < 250 µs | 47 µs |
| PreparedPointSelect | 22,957 µs | < 250 µs | 45 µs |
| InsertSingle | 1,253 µs | < 300 µs | 126 µs |
| InsertBatch100 | 141.8 ms | < 20 ms | 1.5 ms |
| UpdateSingleRow | 2,411 ms | < 0.5 ms | 136 µs |
| JoinUsersOrders | NA (~425 s) | < 25 ms | 287 µs |
| FilterScanOrderLimit | 21.6 ms | unchanged | 1.9 ms |
| GroupByAggregate | 212 ms | ~25 ms (artifact removed) | 21 ms |
| JsonExtract | 131 ms | ~23 ms (artifact removed) | 73 µs |

Three pathologies dead; the broad tax lands at roughly 5-15x MySQL instead of
30-1000x. `JsonExtract` stays the worst honest ratio and is the natural M10
opener.

---

## 4. Explicitly rejected

**Lazy `seq` row pipeline so LIMIT short-circuits.** Section 1.4 shows real
headroom (9.34 ms → ~1.5 ms for a high-match `LIMIT 20`). Rejected anyway:
it only helps when there is no `ORDER BY` (sorting must see every row), it
requires converting `runSelect`, GROUP BY and subquery handling from `list`
to `seq` wholesale, and `Seq` is **10x slower than `Array` on full scans**
(67.06 ms vs 6.85 ms per 2M rows) — so the common case regresses to buy the
uncommon one. If this headroom is ever wanted, the lazy version is: keep the
strict pipeline and thread a bounded counter through the filter step when no
`ORDER BY`/`GROUP BY` is present. Not in M9.

**`[<Struct>]` `Value` DU.** ValueOption vs Option measures 11-13 ms vs
45-47 ms per 20M, and a 2-case struct DU is 2-3x cheaper than its reference
twin — real numbers. But `Value` carries `VString`/`VBytes`/`VJson`, whose
payloads keep allocating either way, and today's per-row cost is dominated by
O(n) scans, not by boxing. Revisit only after M9-3, when scan count stops
being the top term. Changing the engine's central type for a broad-tax item
while a 425-second query exists is the wrong order of operations.

**Custom open-addressing dictionary (Crews-style).** The stdlib `Dictionary`
is already 7.5x faster than `Map` here and is the entire win. A hand-rolled
hash table is a new data structure to maintain for a fraction of a fraction.

**Sort-merge join.** Handles range joins that the hash join cannot, but
needs its own three-valued-NULL merge walk and reuses none of the existing
evaluator machinery. The nested-loop fallback already covers non-equi joins
correctly, just slowly. More code, no benefit for the shapes that exist.

**Group commit for the WAL.** `Persistence.fs:754-781` fsyncs per commit and
self-documents the ceiling. Rejected for M9 because it is not currently the
bottleneck: an **in-memory** server with no WAL at all still takes 1.07 ms
per single-row INSERT at 10k rows, scaling linearly with table size. Fix the
scan first (M9-3); re-measure fsync's share afterwards.

**Cached AST for prepared statements, sold as a performance fix.** Rejected
as performance (measured: no gap to close), kept on the backlog as a
correctness/security cleanup — it deletes the textual
`valueToSqlLiteral`/`escapeSqlString` splice.

**Binary/pre-parsed JSON storage.** Would remove the 0.87 µs/row
`JsonNode.Parse`, but changes what a `VJson` *is* across storage,
persistence and every JSON function. Path-literal memoization (change G) gets
half the win for a tenth of the risk; take that first, if anything.

**Query cancellation on client disconnect.** The original design rejected
this change because it threaded cancellation through the executor. The
current server implements it: the disconnect watcher cancels row evaluation.

**Columnar storage / removing `Result`-based `evalExpr`.** Both trade the
project's identity for a broad-tax multiplier the index work makes moot.
Not proposed, not scheduled.

---

## 5. Sources

**Repo (read directly):**
`src/Fsdb/Engine/Storage.fs` (`updateRows` 1239-1308, `insertCore` 802-853, `Table`
doc 58-66), `src/Fsdb/Engine/Executor.fs` (`applyJoin` 728-781, `applyMutationJoin`
796-854, `runSelect` 1727-1813, `applyLimitOffset` 927),
`src/Fsdb/Sql/Functions.fs` (JSON section 139-258),
`src/Fsdb/Wire/QueryHandler.fs` (`prepareStatement` 871-881, placeholder
substitution 59-121), `src/Fsdb/Wire/Server.fs` (STMT_PREPARE 287-322,
STMT_EXECUTE 323-418, `sendPayloads`/`resultPayloads` 66-169, `TcpClient`
setup 183-190), `src/Fsdb/Wire/Packet.fs` 214-241, `src/Fsdb/Sql/Value.fs` (DU 10-21,
`compareStrings` 174), `src/Fsdb/Engine/Persistence.fs` 677-781,
`benchmarks/Fsdb.Benchmarks/{Schema,ServerBenchmarks,Program}.fs`,
`benchmarks/results/{f1b15ab,66894b2}.md`.

**Measurements re-run against the real server for this document** (scripts in
the scratchpad: `prep.fsx`, `scale.fsx`, `join.fsx`, `json.fsx`,
`bdnsim.fsx`, `poison.fsx`, `js.fsx`): point/prepared/update/insert scaling
at 10k/14k/18.5k/20k/40k rows; join scaling at 250k/1M/4M pairs; JSON and
order-by breakdowns at 10k rows; orphaned-join poisoning with RSS and
latency traces; `JsonNode.Parse` standalone cost.

**Lane microbenchmarks (fsi, structures in isolation)**: quadratic-append vs
prepend+reverse at n=50k; cross-product materialization at 500k/10M pairs;
hash-join build+probe at 10k × 50k; `Dictionary`/`Map`/`ImmutableDictionary`/
`FrozenDictionary` lookup and build; `List`/`Array`/`ResizeArray` build;
`Seq` vs `Array` vs for-loop; `Option` vs `ValueOption`; reference vs struct
DU; `Span`-based protocol encode/decode; FParsec parse cost per statement.

**External:**
matthewcrews.com (F# key-value lookup performance; `FastDictionaryTest`),
JetBrains "F# for Performance-Critical Code",
fslang-design FS-1006 (struct tuples) and FS-1057 (`ValueOption`),
Microsoft Learn F# value options,
FSharpx.Extras issue #214 (PersistentVector / HashMap for F#),
G-Research F# formatting conventions,
`.NET 8 FrozenDictionary` benchmark write-ups.
