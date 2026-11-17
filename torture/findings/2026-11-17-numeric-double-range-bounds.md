# Exact numeric DOUBLE range bounds

Status: exact integer, unsigned integer, and DECIMAL range bounds now normalize
to DOUBLE keys when conversion preserves their numeric value exactly.

An execution trace of the correlated performance workload found that its
estimated-range branch never obtained an eligible numeric range. The storage
regression reproduced this directly: `VDecimal 12.5m` through `VInt 13L` on a
DOUBLE index returned no lookup, even though both values are exactly
representable. The generic exact-probe test compared the converted DOUBLE
Value case with the original numeric Value case, rejecting the conversion.

The range normalizer now uses the existing exact integer/DOUBLE and
DECIMAL/DOUBLE comparison helpers before producing a DOUBLE boundary.
The integer 9007199254740993 and DECIMAL 0.1 are not exactly representable;
they retain the conservative fallback. Existing string-bound handling and
overflow controls are unchanged.

## Pinned oracle

MySQL 8.4.11, using the digest in `torture/compose.yaml`, was tested with the
performance benchmark's schema: 100 outer rows alternating owner IDs 1 and
2; two matching inner rows at k=12.5; 10,000 owner-9 rows outside the range;
indexes on owner and k created before loading data.

```sql
SELECT o.id,
       (SELECT COUNT(*) FROM inner_rows i
        WHERE i.owner_id=o.id AND i.k BETWEEN 12.5 AND 13) c
FROM outer_rows o;
```

The query returned 100 rows, each count one, with zero warnings. The initial
ordinary EXPLAIN chose `ref` on the owner index with an estimate of one row.
FORCE INDEX(ix_k) chose a two-row range. After `ANALYZE TABLE inner_rows`,
the ordinary EXPLAIN also chose the two-row range on ix_k. Thus the oracle's
ordinary choice depends on statistics; numeric range eligibility is present
in all cases. The maintained range-boundary SQL replay also checks numeric
forced bounds on its smaller fixture, returning the same counts and zero
warnings.

## Performance interpretation correction

The original performance report's aggregate measurements remain valid, but
its explanation of the plan change was too strong. Before this correction,
the numeric workload paid for unsuccessful range planning and then fell back
to equality candidates. It did not execute the newly estimated range.
Likewise, the rejected selection-reuse experiment did not reach its changed
recheck branch, so it cannot rule out benefits for workloads that do.

Those reports now record this coverage limitation. The
[matched remeasurement](../../benchmarks/results/2026-11-17-correlated-range-numeric-bounds.md)
shows a higher cost for successful range execution: median throughput falls
40.2% and allocation rises 57.6% against the preceding failed-plan/equality
fallback. Broader statistics, optimizer cost parity, and successful-range
execution cost remain open.
