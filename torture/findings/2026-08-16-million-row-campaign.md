# Million-row FSDB torture campaign — 2026-08-16

This campaign used FSDB revision `b9fb487da204495e6d55536a45d496020809a840`
with the in-progress fixes described below, SQL Splitter 1.21.0, MySQL 8.4.11,
and .NET 10.0.7. The working tree was intentionally dirty because each
deterministic A/B run was used to validate the next engine fix. Raw bundles are
ignored under `torture/artifacts/runs`; the paths below identify the retained
local evidence.

## Defects exposed and repaired

| Surface | Failure evidence | Repair | Verification |
|---|---|---|---|
| `SUM(BIGINT)` | At 10,000 volume rows, the number matched but FSDB advertised/returned an integer while MySQL returned `NEWDECIMAL` | Accumulate exact integer/decimal inputs as decimal and report the MySQL-compatible type | Focused root regression includes a sum beyond `Int64.MaxValue`; the original volume probe passes |
| Raw multi-row INSERT | 100,000 unkeyed rows took 261.449 s (382 rows/s) because each candidate copied/scanned growing accepted-row lists even when no key existed | Prepend accepted rows and reverse once; skip absent constraints; use transient hash lookups for unique and FK checks | Same seed/model took 5.010 s (19,960 rows/s), about 52x faster, with identical MySQL/FSDB state |
| Equality JOIN | Relational 30,000 materialized the Cartesian product: probes took 72.421 s and process peak reached 5.153 GiB | Use a conservative hash path for qualified integer equality joins, retaining the general evaluator as fallback | Same seed/model probes took 75 ms and peak fell to 175.4 MiB, about 966x and 30x improvements respectively |
| Unique/FK enforcement | After the join repair, the same 30,000-row load still took 16.025 s from repeated parent/unique scans | Build statement-local encoded hash sets matching FSDB equality semantics | Same load took 3.056 s, about 5.2x faster, with constraints and typed state matching |
| Self-referencing FK | Commerce 150,000 took 29.162 s; the 40,000-row payments table alone took 21.538 s | Extend the statement-local FK lookup as each accepted self-parent row becomes visible | Same load took 9.956 s; payments fell to 2.666 s, about 8x faster |

No defect was converted into a known-gap suppression. Each understood engine
bug received a focused root regression or is exercised directly by the
deterministic torture case.

After the final exact-sum hardening and review, the original 10,000-row volume
case passed again at
`artifacts/runs/20260816T142305989-2339/volume-seed61-scale10-rows10000-batch10000`
(FSDB load 564 ms, final typed snapshot and all probes equal).

## Largest clean differential passes

All rows below passed generated-statement execution, final direct-catalog
invariants, scenario probes, normalized schemas/indexes/FKs, row counts, and
deterministically ordered typed-data hashes against MySQL.

| Scenario | Rows | SQL | MySQL load | FSDB load | FSDB probes | FSDB snapshot | Process peak |
|---|---:|---:|---:|---:|---:|---:|---:|
| `volume`, seed 61 | 1,000,000 | 68.3 MiB | 8.852 s | 53.278 s | 15.198 s | 9.948 s | 2.51 GiB |
| `volume`, seed 67 | 2,000,000 | 137.6 MiB | 15.973 s | 111.008 s | 30.587 s | 20.893 s | 4.79 GiB |
| `relational`, seed 79 | 1,500,000 across five tables | 97.8 MiB | 39.737 s | 127.766 s | 16.859 s | 13.771 s | 2.01 GiB |
| `commerce`, seed 89 | 1,500,000 across five tables | 161.8 MiB | 33.233 s | 155.925 s | 7.037 s | 18.174 s | 2.89 GiB |

Evidence bundles:

- `artifacts/runs/20260816T134203947-95353/volume-seed61-scale1000-rows1000000-batch10000`
- `artifacts/runs/20260816T134451056-1917/volume-seed67-scale2000-rows2000000-batch10000`
- `artifacts/runs/20260816T140251980-38023/relational-seed79-scale100-rows500000-batch10000`
- `artifacts/runs/20260816T141102428-61628/commerce-seed89-scale100-rows500000-batch10000`

## What is still bleeding

Semantic agreement held at the tested scales, but resource behavior did not
become cheap. The two-million-row flat case used 4.79 GiB in the combined
harness/FSDB process; FSDB load was roughly 7x MySQL, its aggregate probes 26x,
and its final snapshot 5x. The relational and commerce cases remained roughly
3–5x slower to load than MySQL. These ratios are diagnostic, not controlled
benchmarks, because both targets, the byte-complete SQL scanner, evidence
objects, and in-process FSDB share one harness process and one host.

The remaining pressure points are list-backed table storage, statement-local
constraint lookup construction, sorting/aggregation over full tables, the
narrow scope of the hash-join fast path, and the harness retaining the full
generated SQL byte array and split statement set. The next campaign should
isolate FSDB memory, sample it over time, stream the input scanner, and attack
negative DML, transactions, protocol/concurrency, and restart behavior rather
than merely multiplying valid INSERT rows again.

The first prepared-transaction concurrency campaign was subsequently completed
in [`2026-08-16-concurrency-campaign.md`](2026-08-16-concurrency-campaign.md).
