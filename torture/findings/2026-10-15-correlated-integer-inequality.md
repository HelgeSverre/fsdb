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
filter. The focused regression observes fewer than 1,100 residual calls for
physical, pass-through derived, and stable materialized sources, including an
extra indexed OR branch. `IGNORE INDEX` restores more than 1,800 calls.

The split requires a direct integer inner column and a stable integer outer
bound or literal. Text, mixed-type, expression-key, and other inequality
forms retain their existing evaluation paths; their comparison domains need
separate oracle evidence before a range union can be assumed safe. This probe
still has a preparatory full evaluation in the measured statement shape, so
it does not establish one-slice-only execution or a whole-query latency gain.
