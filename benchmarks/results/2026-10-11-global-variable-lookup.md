# Single global variable lookup allocation

The prepared point-lookup `gc-verbose` trace ranked `Session.liveDefaults`
among the largest sampled allocation sites. `Session.tryGlobalVariable`
called it after an absent GLOBAL override, constructing a complete map of
live limits and store-specific defaults to read one variable. Point lookups
ask for several such variables during statement handling.

The change reads one live limit directly through `Limits.tryVariable`, or one
store-specific value through the same definitions used to build snapshots.
`globalVariablesSnapshot` and new-session initialization still construct the
complete map. A focused test compares every single-value read with the
snapshot before and after a live limit change and a GLOBAL override.

The same 10,000-row point-lookup runner measured twelve paired trials of
2,000 calls with 1,000 warmups per action. The
[new raw CSV](2026-10-11-global-variable-lookup.csv) and
[preceding raw CSV](2026-10-11-indexed-probe-fastpath.csv) preserve all
trials. The table uses medians of trials 5–12 in each separate process:

| Action | Prior bytes/call | New bytes/call | Reduction |
|---|---:|---:|---:|
| Cached text handler | 108,438 | 97,730 | 10,708 |
| Reused prepared handler | 115,792 | 105,149 | 10,643 |

The paired action's allocations stabilize at those values after warmup.
Median latency was 52.81 → 47.08 µs for text and 49.06 → 43.48 µs for
prepared. These are separate short process runs with visible warmup and host
variation, so they are not a controlled latency improvement claim. The
remaining one-row allocation is still substantial and the broader point
lookup gap remains open.

`just check` passed 3,385 tests. The pinned MySQL 8.4.11 contract run
covered 139 cases and 18,222 steps; its nine differences have exactly the
same case and step signatures as the preceding run, all under the documented
identifier-case policy. The manifest is retained under
`torture/artifacts/runs/20261011T100530156-98606/contracts/`.
