# Exact integer equality-probe allocation

The [reused prepared lookup profile](2026-10-11-prepared-point-lookup.md)
showed roughly 110–116 KiB allocated per one-row call. A .NET `gc-verbose`
EventPipe sample of the prepared action showed substantial `AsyncLocal`
value-map allocation. One sampled stack entered `Diagnostics.suppress` from
`Storage.exactProbeValue`: even an in-range signed integer probe was sent
through warning-suppressed column coercion. The trace identified a candidate,
not an exact per-site byte total.

The candidate returns an in-range `VInt` directly for signed `INT` and
`BIGINT` columns. Those cases have the same stored representation and cannot
emit a conversion warning. Every other type and value continues through the
existing coercion and exactness check.

The runner used the same 10,000-row table and two actions as the preceding
profile, with twelve paired trials of 2,000 calls and 1,000 warmups per action.
The [candidate CSV](2026-10-11-indexed-probe-fastpath.csv) and
[baseline CSV](2026-10-11-prepared-point-lookup.csv) retain every trial. The
table uses medians of trials 5–12 in each separate process:

| Action | Baseline bytes/call | Candidate bytes/call | Difference |
|---|---:|---:|---:|
| Cached text handler | 111,919 | 108,438 | −3,481 |
| Reused prepared handler | 119,273 | 115,792 | −3,481 |

The post-warmup allocation reduction is identical on both paths. Median
latencies were 49.88 → 52.81 µs for text and 47.15 → 49.06 µs for prepared;
these separate short process runs do not establish a latency improvement.
The broader fixed-cost gap remains: this change removes one avoidable probe
scope without claiming that the one-row lookup is now inexpensive.

`just check` passed 3,384 tests. The pinned MySQL 8.4.11 compatibility
contracts covered 139 cases and 18,222 steps; their nine differences match
the prior identifier-case-policy signatures, with no new signature from this
change. The run manifest is retained under
`torture/artifacts/runs/20261011T095239533-96189/contracts/`.
