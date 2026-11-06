# Statement-scoped non-NULL count proof cache

The correlated `COUNT(physical_column)` path added a schema and outer-join
nullability proof. That proof initially ran once per outer row, even though
the select shape and physical schema are stable for a statement. On an
embedded Release workload with 1,000 outer rows, 500 matched inner rows,
and one joined marker row, four warm runs placed `COUNT(*)` at 7.5–8.6 ms,
`COUNT(i.id)` at 9.7–10.6 ms, and `COUNT(i.required)` at 10.0–11.7 ms.
Every query returned 1,000 rows and a count total of 500.

The candidate caches qualified-column presence by select, database, and
argument within the statement. It does not reuse a proof under a CTE scope,
where a CTE can shadow a physical table. The same focused test checks that
shadowing, left/right outer-join padding, and nullable columns.

An interleaved baseline/candidate/baseline/candidate replay used separate
Release builds of `a29b7c25` and the candidate. Each process warmed the
three query forms, then ran 20 adjacent rounds. The table reports the median
process CPU milliseconds for each form, with its ratio to `COUNT(*)` in the
same process:

| Phase | `COUNT(*)` | `COUNT(i.id)` | `COUNT(i.required)` |
| --- | ---: | ---: | ---: |
| Baseline 1 | 10.28 | 13.50 (1.31×) | 12.16 (1.18×) |
| Candidate 1 | 9.56 | 9.82 (1.03×) | 10.02 (1.05×) |
| Baseline 2 | 8.78 | 11.19 (1.27×) | 10.36 (1.18×) |
| Candidate 2 | 12.54 | 10.30 (0.82×) | 13.88 (1.11×) |

Host load rose above 20, and absolute wall times varied enough that this
does not establish a precise speedup. The repeated within-process CPU ratio
does show that the primary-key form no longer consistently pays the larger
per-row proof cost. The ordinary non-NULL column result is less decisive.
The cache is retained for its bounded statement scope and the repeatable
primary-key ratio improvement; this is not a general query-planner benchmark.
