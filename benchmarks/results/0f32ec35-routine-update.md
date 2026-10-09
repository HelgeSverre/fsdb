# UPDATE controls after routine snapshot correction

Measured 2026-10-09 on arm64 macOS 15.6 with .NET SDK 10.0.401 and Debug
assemblies. Before is the frozen `0f32ec35` assembly; after is the routine
snapshot correction based on that revision.

The unchanged [harness](../scripts/update-ignore-profile.fsx) runs serially in
before-forward, after-forward, after-reverse, before-reverse order. Each process
uses four processors and a 4 GiB GC heap limit. Fresh stores have two parent
rows and 100, 1,000, or 5,000 child rows. Three updates warm each path; five
are measured. The harness checks affected counts, empty diagnostics, and final
aggregate values. Raw samples are adjacent to this report.

These are in-process, in-memory controls without stored-function calls,
networking, triggers, or durability. They do not measure the newly corrected
routine path or compare fsdb with MySQL. The table gives medians across ten
samples at 5,000 rows.

| Workload | Before ms/update | After ms/update | Before bytes/update | After bytes/update |
|---|---:|---:|---:|---:|
| plain payload | 115.828 | 114.781 | 320,533,728 | 320,534,976 |
| ignore payload | 115.419 | 115.194 | 320,112,976 | 320,112,952 |
| plain reference | 121.050 | 123.479 | 330,093,612 | 330,094,860 |
| ignore reference | 123.875 | 123.320 | 329,672,860 | 329,672,836 |

Timing changes range from about -0.9% to +2.0%. Allocation changes are less
than 0.001%. These samples show no material regression in the ordinary paths;
they do not establish a speed improvement or production throughput.

The correctness evidence is in the
[routine UPDATE audit](../../torture/findings/2026-10-09-routine-update-writes.md).

Assembly SHA-256:

- Before: `c425fddadb56d47637a1315904795013726a3c9df82cb9d391699e486430633d`
- After: `936aae3265bcf08c88a339ffae56dae9bd9f1dab8620805198ca58ac0f718768`
