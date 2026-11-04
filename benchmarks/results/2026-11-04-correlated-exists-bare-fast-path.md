# Indexed correlated EXISTS with a bare inner-column projection

The indexed `EXISTS` shortcut already accepted a qualified inner column.
`EXISTS(SELECT id FROM orders c WHERE c.user_id=u.id)` still took the
broader subquery path. For a single inner source, a bare name that uniquely
matches an inner column now uses the same indexed equality count. Names not
found in that source retain ordinary binding, so an outer-only reference or
unknown name is not silently treated as an inner column.

Disposable MySQL 8.4.11 confirmed the true/false values for bare,
inner-qualified, and outer-qualified projections and error 1054 for an
unknown name. The focused fsdb regression also checks that the bare form
does not scan or group the inner table for every outer row.

The [before](2026-11-04-correlated-exists-bare-before.md) and
[after](2026-11-04-correlated-exists-bare-after.md) artifacts are three-
iteration ShortRuns on the same M2 Max host and 10,000-user, 50,000-order
dataset. The before artifact uses committed `f124fbaf` plus the new
benchmark method; the after artifact includes the uncommitted engine change.
The SHA headers identify the committed base, not the candidate diff.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Qualified inner column | 776 µs | 709 µs | 182 µs | 177 µs |
| Bare inner column | 4,233 µs | 781 µs | 175 µs | 178 µs |

The bare-column query fell about 5.4× and is now close to the qualified
form. Other expressions and source shapes still require broader correlated
planning work.

```sh
FSDB_BENCH_METHODS=CorrelatedPhysicalExistsColumn,CorrelatedPhysicalExistsBareColumn just bench-quick
```
