# Indexed correlated EXISTS with a qualified inner ORDER BY

The indexed correlated `EXISTS` path previously rejected every `ORDER BY`.
On the 50-outer-row, 500-inner-row regression fixture, a query ordered by
`i.id DESC` returned the correct membership values but examined 550 rows.
Ordering cannot change membership for this shape, and MySQL 8.4.11 returns
the same values with and without that order.

The shortcut now accepts order keys that are qualified columns owned
uniquely by its single inner source. It still validates every projected
column. Unknown order columns retain MySQL's 1054 error through the
ordinary path. DISTINCT with ORDER BY, aliases, ordinal positions,
computed order expressions, grouped queries, and other source shapes
also retain ordinary evaluation because their binding or diagnostic rules
need separate coverage.

The focused test confirms correct membership and fewer than 200 examined
rows for ordered `EXISTS`, both with and without `LIMIT 1`. The previous
ordered case examined 550. The native MySQL oracle confirms positive and
negative membership, unknown-column diagnostics, and preparation of the
valid ordered queries. All 90 correlated tests pass.

Run `dotnet run --project tests/Fsdb.Tests -c Release -- --filter-test-case
'correlated' --no-spinner` and `uv run --with pymysql
torture/scripts/correlated-order-alias-oracle.py`.
