# Shifted keys in correlated composite ranges

A projection can carry an integer key plus or minus a constant through
a derived table or CTE. For an equality prefix or ordered range on that
output, the planner subtracts the accumulated offset from the probe
bound. It preserves the comparison direction and whether each range
edge is inclusive. The stored index still sees the original column.
Every arithmetic step must fit signed BIGINT. Untranslatable bounds
retain ordinary evaluation, including NULL and inverse arithmetic
outside the signed domain.

Correlated aggregate binding now sees the CTEs in scope when a derived
table references one. Successful binding can be reused while the
enclosing column and CTE scopes keep the same identities. The ordinary
path also resolves the CTE when an unsupported comparison rules out
the index optimization.
[MySQL range optimization](https://dev.mysql.com/doc/refman/8.4/en/range-optimization.html).

## Native timing

Release Fsdb at `3659d1fd` and the candidate ran with WAL and Server GC.
MySQL 8.4.11 used commit-time flushing and disabled binary logging. The
servers ran separately over IPv4 loopback on an Apple M2 Max with
macOS 15.6. Fsdb used .NET 10.0.12 and the connector was MySqlConnector
2.6.2. Each pass checks the result during preflight, four warmups, and
30 timed executions over 500 parents and 10,000 children. The second
pass reverses engine and workload order. Cells average the two per-pass
medians in milliseconds.
[Per-pass p50 and p95](2026-11-26-offset-key-ranges.csv).

| Workload | Fsdb before (ms) | Fsdb after (ms) | MySQL (ms) |
| --- | ---: | ---: | ---: |
| Direct keys | 6.529 | 8.571 | 1.698 |
| Shifted range key | 37.287 | 8.335 | 1.808 |
| Shifted prefix and range through nested aliases | 75.037 | 8.993 | 654.233 |
| Shifted CTE keys and residual filter | 47.437 | 19.234 | 641.164 |

The shifted queries ran 2.5–8.3× faster than the prior Fsdb build.
MySQL's TREE plan used the original equality prefix for the shifted
range key. It scanned the child table for each outer row in both
shifted-prefix cases. These plan differences explain why the comparison
varies by query shape.

The direct-key control ranged from 3.88 to 9.17 ms before and 7.93
to 9.21 ms after. In an in-process control with 100 parents and 4,000
children, median calling-thread allocations across five checked calls
after three warmups rose from 1.83 MB to 1.91 MB for direct keys and
fell from 45.9 MB to 2.5 MB for the nested shifted keys.

MySQL matched 67 differential statements and 264 ordered rows, including
column names, type codes, scales, warnings, and numeric errors. The
matrix exercised NULL and extreme bounds, reversed operands, a CTE
with the same name as a stored table, a schema-qualified table, and an
unsupported cast that forces ordinary evaluation. Four executions of a
binary prepared parameterized query also matched.

Run the timing fixture against a disposable native server with:

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS offset-key-controls
```
