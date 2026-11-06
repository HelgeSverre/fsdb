# Indexed correlated EXISTS with inner-column grouping

The correlated `EXISTS` shortcut previously rejected all `GROUP BY` queries.
On the 50-outer-row, 500-inner-row regression fixture, grouping by the
correlated inner key returned the correct membership values but examined
25,550 rows. The indexed membership path examines fewer than 200 rows on
that fixture after this change.

The shortcut accepts grouping only when every projection is a literal,
plain inner column, or inner-source star, there is no `HAVING`, and each
grouping key resolves to a unique column of
the single inner source. A bare key shadowed by a projection alias stays on
the ordinary path. Other limits, offsets, source shapes, and expressions
retain their existing eligibility rules. These restrictions preserve
binding errors and avoid skipping evaluation of grouping expressions.

The focused regression covers qualified and bare keys, `LIMIT 1`, plain
column and star projections, matched and unmatched outer rows, an unknown
grouping column, and the examined-row budget. The native MySQL 8.4.11 oracle
confirms the same membership and
unknown-column error, and prepares the valid queries. This is an examined-row
improvement; no latency measurement is claimed.

Run `dotnet run --project tests/Fsdb.Tests -c Release -- --filter-test-case
'correlated' --no-spinner` and `uv run --with pymysql python
torture/scripts/correlated-order-alias-oracle.py`.
