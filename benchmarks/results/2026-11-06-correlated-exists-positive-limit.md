# Positive literal limits in indexed correlated EXISTS

`EXISTS` needs to know only whether at least one matching row survives. Any
positive literal limit leaves that answer unchanged when there is no offset.
The guarded indexed lookup now accepts positive signed and unsigned limits;
zero, offset, and expression limits still use the normal path.

The regression control covers zero, one, and two inner matches, `LIMIT 1`,
`LIMIT 2`, the largest unsigned literal, `LIMIT 0`, and `LIMIT 1 OFFSET 1`.
It also verifies that an unknown projected column still reports 1054.

The [previous ShortRun](2026-11-06-correlated-exists-limit-one-quick.md)
measured `LIMIT 2` on the general path at 4.45 ms for 100 outer rows, while
`LIMIT 1` took 0.92 ms. The [follow-up ShortRun](2026-11-06-correlated-exists-positive-limit-quick.md)
uses the same 10,000-user, 50,000-order dataset and query shapes:

| Query | Earlier fsdb | Follow-up fsdb | Follow-up MySQL 8.4.11 |
|---|---:|---:|---:|
| `EXISTS(... LIMIT 1)` | 0.92 ms | 0.66 ms | 0.17 ms |
| `EXISTS(... LIMIT 2)` | 4.45 ms | 0.65 ms | 0.17 ms |

The `LIMIT 2` means fell about 6.9 times across the two runs and now match
the eligible `LIMIT 1` path. The earlier `LIMIT 2` ShortRun had high
uncertainty, so the ratio is directional rather than a stable speedup claim.
