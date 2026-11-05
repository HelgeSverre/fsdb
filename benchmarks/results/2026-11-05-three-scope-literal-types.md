# Three-scope scalar literals beyond integers

The three-scope filtered scalar path previously admitted only integer literal
projections. The projection is evaluated only after its nested predicate is
true, and its output metadata is fixed for any SQL literal. The path now admits
all `Lit` values under the same nested-predicate and materialization guards.
This retains per-row predicate evaluation and statement-scoped successful
binding validation.

The maintained [embedded profile](../CorrelatedNestedProfile/Program.fs) ran
the same 100-user, 50,000-order query with a middle `SELECT 'matched' WHERE
(SELECT c.id+u.id WHERE c.id % 2=0) IS NOT NULL`. Each query verified 100 rows
and a count total of 250. Two candidate runs followed one baseline run on the
same host and used a 10-second timed phase after warmup:

| Engine | Completed queries | Seconds |
| --- | ---: | ---: |
| Baseline | 1,085 | 10.002 |
| Candidate | 2,787 | 10.003 |
| Candidate repeat | 2,729 | 10.002 |

The candidate completed about 2.5 times as many queries in this sequence.
The sample is short and not interleaved, so the ratio is exploratory rather
than an isolated speedup. The focused fsdb regression covers string and decimal
literals with intermediate and ancestor bindings; the [native MySQL 8.4.11
oracle](../../torture/scripts/correlated-nested-literal-oracle.py) confirms
integer, string, and decimal result counts.

```sh
FSDB_PROFILE_QUERY=three_text FSDB_PROFILE_SECONDS=10 \
  dotnet run -c Release --project benchmarks/CorrelatedNestedProfile
```
