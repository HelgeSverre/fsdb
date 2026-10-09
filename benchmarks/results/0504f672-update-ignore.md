# UPDATE IGNORE performance after row-level constraint handling

Measured 2026-10-09 on arm64 macOS 15.6, .NET SDK 10.0.401, Debug assemblies.
Before is `53e1d2e6`; after is `0504f672`. The intervening UPDATE IGNORE fix
preserves accepted rows and native trigger effects when another row violates a
constraint. All rows in this performance workload are valid on both revisions.

## Method

The harness is [update-ignore-profile.fsx](../scripts/update-ignore-profile.fsx).
Runs are serial with `DOTNET_PROCESSOR_COUNT=4` and a 4 GiB GC heap limit. Each
revision runs the workload list once forward and once in reverse. Actual run
order is after-forward, after-reverse, before-forward, before-reverse. The
baseline assembly is built from an isolated archive of the recorded revision;
only the script's assembly reference changes for that baseline.

Each workload gets a fresh in-memory store, two parent rows, and 100, 1,000,
or 5,000 child rows with a primary key, foreign key, and unindexed payload.
Setup is outside measurement. Three updates warm the path; five updates are
measured per invocation. Every update checks its affected count and absence
of diagnostics; the final row count, payload, and reference values are checked.
Payload updates increment a non-indexed column. Reference updates toggle the
foreign-key value between two existing parents. There are no triggers.

These are in-process engine measurements. They exclude networking and durable
storage and do not compare fsdb with MySQL. Debug timings are not production
throughput claims. Run order is not interleaved between revisions, so the
unchanged plain-UPDATE controls are important when interpreting host drift.

## Results at 5,000 rows

Medians across ten measured samples per revision and workload:

| Workload | Before ms/update | After ms/update | Before bytes/update | After bytes/update |
|---|---:|---:|---:|---:|
| Plain payload update | 110.702 | 111.166 | 318,853,176 | 318,853,560 |
| IGNORE payload update | 110.146 | 430.805 | 318,853,288 | 740,984,928 |
| Plain reference update | 116.486 | 116.422 | 328,413,072 | 328,413,456 |
| IGNORE reference update | 119.468 | 434.157 | 328,413,172 | 750,544,812 |

UPDATE IGNORE is 3.6–3.9 times slower and allocates about 2.3 times as much in
these valid-row workloads. Plain-UPDATE control medians change by less than
0.5%. The two after-run medians for ignored updates are 429–440 ms. Smaller
sizes and all raw samples are retained in the adjacent CSV files; short-run
warmup makes their timing comparisons less stable.

## Follow-up

The shared UPDATE helper publishes one candidate at a time for IGNORE, even
without triggers. Repeated storage publication is a concrete optimization
candidate. A batched path must still skip only rejected rows, retain accepted
rows, preserve warning order and affected counts, and avoid re-evaluating
assignments or side effects after a failure. Triggered updates must preserve
BEFORE effects for ignored rows and run AFTER only for accepted rows.

The before revision mishandles rejected foreign-key rows. Its faster path is a
performance baseline, not a correctness-preserving replacement. No runtime
implementation changed during this measurement.

Assembly SHA-256:

- Before: `172590cceecf94eaf4c6b97ea5178f74d9ad2d0fb5e43ec47abeb1e26cc263b8`
- After: `697dedf2c8bda4b764ec9fd8f0783dbfb16724cc6fea9dcb827408f0b390ef4a`
