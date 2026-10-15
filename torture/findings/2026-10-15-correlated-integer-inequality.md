# Correlated integer inequality candidates

On native MySQL 8.4.11, an indexed inner table with 950 rows at `k=1` and
50 rows at `k=2..51` returns `(50, 0, 999)` for outer bounds `(1, NULL, 51)`
under `d.k <> o.k` or `NOT (d.k = o.k)`. Adding `OR d.id=500` returns
`(51, 1, 999)`. The [native oracle](../scripts/correlated-inequality-oracle.py)
checks direct, pass-through derived, and materialized derived sources.

FSDB returned these counts but did not form candidates from the correlated
inequality. On the selective `k=1` probe, a leading `TOUCH(d.id)=d.id`
residual ran 2,002 times before the change. The planner now treats a safe
integer `<>` or `NOT (=)` as the union of the strict `<` and `>` ranges,
deduplicates candidates, and retains the original expression as the result
filter. The focused regression observes fewer than 80 residual calls for
physical, pass-through derived, and stable materialized sources, including an
extra indexed OR branch. `IGNORE INDEX` restores more than 900 calls.

The split requires a direct integer inner column and a stable integer or NULL
outer bound, or a literal. A NULL bound produces empty candidates, including
when the outer SELECT probes result metadata with a placeholder row. Before
handling that case, the metadata probe scanned all 1,000 rows and the focused
query made 1,052 residual calls despite using the indexed slice for its real
outer row; after the fix it makes 52. Text, mixed-type, expression-key, and
other inequality forms retain their existing evaluation paths; their comparison
domains need separate oracle evidence before a range union can be assumed safe.
