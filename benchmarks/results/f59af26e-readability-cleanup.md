# UPDATE profile after shared validation and rendering cleanup

Measured 2026-10-09 on arm64 macOS 15.6, .NET SDK 10.0.401, Debug assemblies.
Before is a frozen assembly from `f59af26e`; after is the readability cleanup
based on that revision. Both use the unchanged
[UPDATE profile](../scripts/update-ignore-profile.fsx).

## Method

Processes run serially in before-forward, after-forward, after-reverse,
before-reverse order, each with four processors and a 4 GiB GC heap limit.
Each workload uses a fresh in-memory store, two parent rows, and 100, 1,000,
or 5,000 child rows. Three updates warm the path; five are measured. The
harness checks affected counts, empty diagnostics, and final aggregate values.
Raw samples are stored alongside this report.

The table gives medians across ten samples at 5,000 rows. These measurements
exclude networking, triggers, and durability and do not compare fsdb with MySQL.

| Workload | Before ms/update | After ms/update | Before bytes/update | After bytes/update |
|---|---:|---:|---:|---:|
| plain payload | 122.585 | 118.628 | 319,333,704 | 320,533,728 |
| ignore payload | 128.458 | 116.031 | 318,912,952 | 320,112,976 |
| plain reference | 131.628 | 129.987 | 328,893,576 | 330,093,600 |
| ignore reference | 130.507 | 129.376 | 328,472,836 | 329,672,860 |

The measured medians show no timing regression. Allocation increases by
1,200,024 bytes per update, about 0.4%, after extracting the shared replacement
key validator. Timing variation between process runs does not establish a
speed improvement. The earlier UPDATE IGNORE batching benefit remains intact
for these workloads.

## Scope and validation

SQL identifier rendering shares one backtick-escaping implementation across
DDL, diagnostics, metadata, and account rendering. UPDATE and ON DUPLICATE KEY
UPDATE share replacement-key validation. UPDATE publication paths share row
refresh while retaining lock scope and candidate order. Constraint rejection
remains separate from expression evaluation and coercion.

The root gate passes all 3,173 tests with no build warnings or errors. Native
MySQL 8.4.11 wire validation passes 97 contracts and 14,454 steps with zero
differences at `torture/artifacts/runs/20261009T064928672-94173/contracts`.
The existing [routine-write gap](../../torture/findings/2026-10-09-update-ignore.md#routine-writes-during-update)
remains open; the unfinished fix is excluded from both assemblies.

Assembly SHA-256:

- Before: `342ce3b176772dfd8b6a48b4d72212d5d222388c9409e97bee7c61da67fb264b`
- After: `c425fddadb56d47637a1315904795013726a3c9df82cb9d391699e486430633d`
