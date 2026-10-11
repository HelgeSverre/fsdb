# Null-rejected left join with an indexed OR filter

The compatibility change at `e2597033` carries an indexed union of two right-side
primary-key candidates into a two-table left join after its `WHERE` clause
rejects null-padded rows. Before that change, fsdb scanned and joined the full
sources. MySQL 8.4.11 examines four candidates for this query on the small
oracle fixture, or two with `LIMIT 1`; fsdb now matches those counts.

The focused loopback workload ran the same SQL on native fsdb and MySQL 8.4.11:

```sql
SELECT u.id, o.id
FROM users u LEFT JOIN orders o ON o.user_id = u.id
WHERE o.id = 1 OR o.id = 2;
```

Both revisions used the same added benchmark method, a freshly seeded schema
with 1,000 users, 5,000 orders, and 1,000 articles, .NET 10.0.12 on an Apple
M2 Max, and BenchmarkDotNet `ShortRun` (three warmup and three measured
iterations). The old source was `be60f12c`; the changed source was `e2597033`.
The benchmark method was copied unchanged into the detached baseline checkout.
Fsdb ran in memory and MySQL used its normal durable configuration; this is a
read-only comparison. `PointSelectByPk` was included as a same-run control.

| Workload | Target | Before | After | Change |
|---|---|---:|---:|---:|
| Null-rejected OR join | fsdb | 4,626.26 µs | 178.82 µs | 25.9× faster |
| Null-rejected OR join | MySQL | 51.82 µs | 51.24 µs | 1.1% lower |
| Point select by PK | fsdb | 166.88 µs | 170.04 µs | 1.9% higher |
| Point select by PK | MySQL | 38.67 µs | 41.56 µs | 7.5% higher |

The raw runs retain the tool and host provenance: [before](be60f12c-quick.md)
and [after](e2597033-quick.md). ShortRun has only three measured iterations;
the magnitude of the join improvement is clear, but the small control changes
should not be treated as regressions. The remaining fsdb join latency is close
to its point-select control and about 3.5× the MySQL join latency in this run.
