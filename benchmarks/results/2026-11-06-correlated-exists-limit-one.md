# Indexed correlated EXISTS with LIMIT 1

The indexed correlated `EXISTS` shortcut previously rejected every `LIMIT`.
A literal `LIMIT 1` keeps the same membership result, so it can reuse the
existing guarded equality lookup after projection validation. `LIMIT 0`,
offsets, and expression limits still take ordinary subquery evaluation.

The regression test checks three outer rows with two, zero, and one inner
matches. Local MySQL 8.0.33 returned `1,0,1` for both unrestricted and
`LIMIT 1`, and `0,0,0` for `LIMIT 0`. Fsdb matched those results.

The [ShortRun artifact](2026-11-06-correlated-exists-limit-one-quick.md)
uses 10,000 users, 50,000 orders, and 100 selected outer rows. Each query
returns the same membership values. `LIMIT 2` is the general-path control;
it is not a before-version measurement of the exact `LIMIT 1` query.

| Query | fsdb mean | MySQL 8.4.11 mean |
|---|---:|---:|
| `EXISTS(... LIMIT 1)` | 0.92 ms | 0.19 ms |
| `EXISTS(... LIMIT 2)` | 4.45 ms | 0.17 ms |

The fsdb control is about 4.8 times slower than the new eligible shape in
this run, with substantial ShortRun uncertainty on `LIMIT 2`. MySQL results
are essentially equal. This supports the planner change for the common
literal-one shape; it does not establish a broad limited-subquery speedup.
