# Three-scope filtered literal scalar

The maintained [embedded profile](../CorrelatedNestedProfile/Program.fs) ran
the same parsed, 100-user query repeatedly over 10,000 users and 50,000
indexed orders. `FSDB_PROFILE_QUERY=three` selects a middle source-free
`SELECT 1 WHERE (SELECT ...) IS NOT NULL`, nested inside a correlated count.
Every execution checks 100 output rows and the count total of 250.

The previous engine materialized the middle scalar for each row. A direct
evaluation trial without cached binding validation was slower: 1,431 versus
1,748 queries in 15 seconds. The retained path admits only integer literal
projections with a nested subquery in the predicate, validates the binding
once per statement site, and still evaluates the predicate for every row.
Ordinary literal predicates keep their existing scalar reduction behavior.

| Run order | Engine | Completed queries | Seconds |
| --- | --- | ---: | ---: |
| 1 | baseline | 1,748 | 15.006 |
| 2 | candidate | 4,147 | 15.002 |
| 3 | candidate | 3,786 | 15.002 |
| 4 | baseline | 1,935 | 15.002 |
| 5 | candidate | 4,067 | 15.005 |

The candidate completed roughly 2.1 times as many queries as the interleaved
baseline on this workload. These are whole-query counts, so they do not
isolate the cost of binding validation from materialization and other work.
The first unbounded literal-projection trial changed prepared-statement
reduction semantics and was rejected by the suite; the narrower path passed
that focused test and the full 3,885-test suite. After adding repeated
LATERAL and unknown-column controls, their focused test also passed.

A disposable native MySQL 8.4.11 server returned `(1,1)` and `(2,1)` for the
three-scope query, and `(1,NULL)` and `(2,1)` for the simpler filtered-literal
control, matching the fsdb assertions.

The embedded profile command was:

```sh
FSDB_PROFILE_QUERY=three FSDB_PROFILE_SECONDS=15 \
  dotnet run -c Release --project benchmarks/CorrelatedNestedProfile
```
