# Correlated composite ranges on integer key domains

Correlated aggregates can use an equality prefix and an ordered range on
the following index column for signed and unsigned integer types from
TINYINT through BIGINT. Direct bounds preserve signed or unsigned values
through index lookup, including values above the signed 64-bit limit.
The storage lookup checks that each bound is represented exactly by its
indexed column. Out-of-domain bounds and expressions requiring evaluation
continue through the general executor.

The [wire fixture](../../torture/findings/2026-11-26-correlated-integer-key-domains.sql)
matched eight ordered rows against the [pinned MySQL image](../../torture/config/toolchain.json).
It covers both signed BIGINT endpoints, BIGINT UNSIGNED values through
18446744073709551615, nullable keys and payloads, descending index
suffixes, contradictory ranges, an index hint, and a projected CTE.
A seeded matrix added 120 statements and 1,800 ordered rows across all
ten integer type variants, with matching warnings. Five binary prepared
executions matched all 20 rows and column types, including a NULL
parameter and an unsigned bound at its largest value. The Expecto
regression verifies that the unsigned lookup stays within three times
the allocation of an equivalent signed control. Before the change, it
allocated 66.7 MB against the control's 4.4 MB.

## Native timing

Release Fsdb at `d079aabb` and the candidate ran with WAL and Server GC
on .NET 10.0.12. MySQL 8.4.11 used commit-time flushing and disabled
binary logging. All servers ran natively over IPv4 loopback on an Apple
M2 Max with macOS 15.6. Results use MySqlConnector 2.6.2 and 500 parent
rows with 40 child rows per parent. Each pass checked the result before
four warmups and during all 20 timed executions. Pass two reversed the
engine and case order. Cells average the two median times.
[Per-pass medians and p95s](2026-11-26-correlated-integer-key-domains.csv).

| Workload | Fsdb before (ms) | Fsdb after (ms) | MySQL (ms) | Fsdb speedup |
| --- | ---: | ---: | ---: | ---: |
| Signed INT count | 7.19 | 8.49 | 2.80 | 0.85× |
| Unsigned INT count | 45.55 | 9.52 | 10.89 | 4.79× |
| Unsigned BIGINT count | 45.58 | 8.86 | 10.98 | 5.14× |
| Unsigned BIGINT sum through a CTE | 57.77 | 10.86 | 11.32 | 5.32× |

The signed control ranged from 5.39 to 8.99 ms on the baseline and
8.32 to 8.65 ms on the candidate. Its two passes leave the effect on
signed INT latency uncertain. The three unsigned cases improved in
both passes.

Run the fixture against a disposable native server with
`dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS integer-key-controls`.
