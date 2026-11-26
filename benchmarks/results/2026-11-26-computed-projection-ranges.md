# Computed payloads in correlated composite ranges

The correlated range planner can retain a physical equality prefix and
ordered range when a derived table or statement-stable CTE also projects
signed integer arithmetic. Direct column aliases still identify the
physical keys. The planner calculates the other outputs only for
selected rows. Aliases can carry those calculated values through
another projection, and eligible filters can use them before the
aggregate runs.

The planner checks the result bounds of each addition, subtraction,
and multiplication in the calculation. It falls back to ordinary
evaluation if an intermediate result might overflow signed BIGINT
or an operand needs coercion. SQL NULL continues to propagate through
the arithmetic and through residual predicates.
[MySQL arithmetic operators](https://dev.mysql.com/doc/refman/8.4/en/arithmetic-functions.html).

## Native timing

Release Fsdb at `c4e04ec4` and the candidate ran with WAL and Server GC.
Native MySQL 8.4.11 used commit-time flushing and disabled binary
logging. The servers ran separately over loopback on an Apple M2 Max
on macOS 15.6. Fsdb used .NET 10.0.12 and the driver was MySqlConnector
2.6.2. The fixture has 500 parents and 10,000 children. Each pass checks
the result during preflight, four warmups, and 30 timed calls. The
second pass reverses the engine and workload order. Times are the
mean of the two pass medians in milliseconds.
[Raw samples](2026-11-26-computed-projection-ranges.csv).

| Workload | Fsdb before (ms) | Fsdb after (ms) | MySQL (ms) |
| --- | ---: | ---: | ---: |
| Direct aliases with a nullable source filter | 8.396 | 8.806 | 2.453 |
| Count of a computed payload | 55.368 | 7.688 | 2.443 |
| Sum of nested computed aliases | 56.838 | 9.325 | 2.508 |
| Sum of a computed column in a named CTE | 49.395 | 10.055 | 2.600 |
| Filter on a computed payload | 207.365 | 10.429 | 2.467 |

The computed cases ran 4.9–19.9× faster than the prior Fsdb build
and took 3.1–4.2× MySQL's median time. The direct-alias control ranged
from 6.42 to 10.38 ms before and 6.94 to 10.67 ms after.

An in-process control with 100 parents and 4,000 children measured
median calling-thread allocations over five checked calls after three
warmups. The computed count fell from 36.1 MB to 2.0 MB, the named
CTE sum from 25.4 MB to 2.5 MB, and the computed filter from 96.4 MB
to 2.2 MB.

A MySQL differential matrix matched 207 statements and 916 ordered
rows, including column names, type codes, scales, warnings, and
numeric error codes. The cases cover nullable values, signed limits,
nested and filtered derived tables, named CTEs, and overflow and
unknown-column errors. Four executions of a binary prepared statement
also matched, including a NULL parameter.

Run the timing fixture against a disposable native server with:

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS computed-projection-controls
```
