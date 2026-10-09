# Batching eligible UPDATE IGNORE writes

Measured 2026-10-09 on arm64 macOS 15.6, .NET SDK 10.0.401, Debug assemblies.
Before is a frozen assembly from `4ebf5f82`; after is the working-tree batching
change based on that revision. The original regression is recorded in
[the earlier profile](0504f672-update-ignore.md).

## Method

The unchanged [harness](../scripts/update-ignore-profile.fsx) runs serially in
before-forward, after-forward, after-reverse, before-reverse order. Each process
uses four processors and a 4 GiB GC heap limit. Each workload has a fresh store,
two parent rows, and 100, 1,000, or 5,000 child rows. Three updates warm the path;
five are measured. Affected counts, empty diagnostics, row counts, and final
column values are checked. Raw samples are adjacent to this report.

These are in-process, in-memory measurements without networking, triggers, or
durability. They do not compare fsdb with MySQL or establish production
throughput. The table gives medians across ten samples at 5,000 rows.

| Workload | Before ms/update | After ms/update | Before bytes/update | After bytes/update |
|---|---:|---:|---:|---:|
| plain payload | 110.885 | 110.842 | 318,853,560 | 319,333,704 |
| ignore payload | 427.387 | 110.053 | 740,984,928 | 318,912,952 |
| plain reference | 117.526 | 116.390 | 328,413,456 | 328,893,588 |
| ignore reference | 436.313 | 118.430 | 750,544,812 | 328,472,836 |

Ignored updates take 73–74% less time and allocate 56–57% fewer bytes. Plain
UPDATE timing controls move by less than 1%; their allocation grows by about
0.15% from the shared candidate-result representation. The measured IGNORE
regression is removed for these workloads.

## Implementation and limits

Eligible ignored updates share the normal private row builder and publish once.
Constraint rejection occurs before the builder and indexes advance. Rejected
rows retain the prior fold state and contribute warnings without retrying their
assignments. Both execution paths share the ignorable-constraint error rule.

Triggers, incoming foreign keys (including self-references), and custom or
stored-function calls retain per-row execution. Their performance is not
claimed improved. Eligibility uses the existing statement-expression traversal,
including nested query bodies and clauses, to recognize custom calls.

Validation: 3,173 root tests pass without build warnings or errors; 97 native
wire contracts / 14,454 steps pass without differences at
`torture/artifacts/runs/20261009T001705713-89878/contracts`. Native probes retain
warning order and confirm one evaluation per candidate. A separate routine-write
visibility gap reproduces both before and after and remains recorded in
[the UPDATE IGNORE findings](../../torture/findings/2026-10-09-update-ignore.md#routine-writes-during-update).

Assembly SHA-256:

- Before: `697dedf2c8bda4b764ec9fd8f0783dbfb16724cc6fea9dcb827408f0b390ef4a`
- After: `d9903117ac0b5d6085d998bf6e3acbe179cac17ccee885795dc0672c13c31fa7`
